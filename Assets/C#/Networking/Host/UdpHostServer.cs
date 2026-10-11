using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Game.Net;

namespace Game.Net.Host
{
    /// <summary>
    /// 房主的 UDP 监听器：进入游戏后的实时同步通道。
    ///
    /// 只做“收包 / 拆头 / 校验魔数 / 抛给上层”，不认识任何会话语义。
    /// 会话（玩家 ID ↔ UDP 端点）的绑定由 HostNetworkManager 在收到 UdpHello 后完成。
    /// </summary>
    public sealed class UdpHostServer : IDisposable
    {
        private UdpClient _socket;
        private CancellationTokenSource _cts;
        private Task _receiveTask;
        private volatile bool _running;

        private int _sendErrorLogged;

        /// <summary>实际监听端口（填 0 时这里能看到系统分配的端口）。</summary>
        public int Port { get; private set; }

        public bool IsRunning { get { return _running; } }

        /// <summary>收到的合法数据报：来源端点 + 包类型 + protobuf 负载（已去掉 4 字节头）。</summary>
        public event Action<IPEndPoint, UdpPacketType, byte[]> DatagramReceived;

        public event Action<Exception> Error;

        // ------------------------------------------------------------------

        public bool Start(IPAddress bindAddress, int port, out string error)
        {
            error = null;

            if (_running)
            {
                error = "UDP 服务器已经在运行";
                return false;
            }

            try
            {
                _socket = new UdpClient((bindAddress ?? IPAddress.Any).AddressFamily);

                // 允许同一端口被复用（防止上次异常退出后 TIME_WAIT 影响调试）
                _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                _socket.Client.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, port));
                _socket.EnableBroadcast = true;

                // 加大接收缓冲，减少突发快照丢包
                try { _socket.Client.ReceiveBufferSize = 1 << 20; } catch (Exception) { }
                try { _socket.Client.SendBufferSize = 1 << 20; } catch (Exception) { }

                Port = ((IPEndPoint)_socket.Client.LocalEndPoint).Port;

                _cts = new CancellationTokenSource();
                _running = true;
                _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));

                NetLog.Info("UDP 监听已启动: 0.0.0.0:" + Port);
                return true;
            }
            catch (Exception e)
            {
                error = ProtoCodec.HumanizeError(e);
                NetLog.Exception(e, "启动 UDP 监听失败");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            _running = false;

            try { if (_cts != null) _cts.Cancel(); } catch (Exception) { }

            try { if (_socket != null) _socket.Close(); } catch (Exception) { }
            _socket = null;

            try { if (_cts != null) _cts.Dispose(); } catch (Exception) { }
            _cts = null;
            _receiveTask = null;
        }

        public void Dispose()
        {
            Stop();
        }

        // ------------------------------------------------------------------

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _running)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (NullReferenceException) { break; }
                catch (SocketException e)
                {
                    if (ct.IsCancellationRequested) break;

                    // 某些系统上 ICMP 端口不可达会以 ConnectionReset 冒泡，忽略即可
                    if (e.SocketErrorCode == SocketError.ConnectionReset ||
                        e.SocketErrorCode == SocketError.MessageSize)
                    {
                        continue;
                    }

                    NetLog.Warn("UDP 接收错误: " + e.SocketErrorCode);
                    continue;
                }
                catch (Exception e)
                {
                    if (ct.IsCancellationRequested) break;
                    RaiseError(e);
                    continue;
                }

                byte[] buffer = result.Buffer;
                if (buffer == null || buffer.Length < NetConst.UdpHeaderBytes) continue;

                UdpPacketType type;
                int bodyOffset, bodyLength;
                if (!ProtoCodec.TryUnwrapUdp(buffer, buffer.Length, out type, out bodyOffset, out bodyLength))
                    continue; // 不是本协议的包，静默丢弃

                // 复制出负载，交给主线程后不依赖原缓冲
                byte[] body = new byte[bodyLength];
                if (bodyLength > 0) Buffer.BlockCopy(buffer, bodyOffset, body, 0, bodyLength);

                IPEndPoint remote = result.RemoteEndPoint;
                MainThreadDispatcher.Post(() =>
                {
                    var handler = DatagramReceived;
                    if (handler != null) handler(remote, type, body);
                });
            }
        }

        // ------------------------------------------------------------------
        // 发送
        // ------------------------------------------------------------------

        /// <summary>给单个端点发送数据报。</summary>
        public bool Send(IPEndPoint endpoint, UdpPacketType type, IMessage message)
        {
            if (endpoint == null) return false;

            UdpClient socket = _socket;
            if (socket == null || !_running) return false;

            byte[] packet;
            try { packet = ProtoCodec.BuildUdp(type, message); }
            catch (Exception e) { RaiseError(e); return false; }

            SendRaw(socket, packet, endpoint);
            return true;
        }

        /// <summary>批量发送（每个端点一份相同的负载，避免重复序列化）。</summary>
        public int SendMany(System.Collections.Generic.IEnumerable<IPEndPoint> endpoints, UdpPacketType type, IMessage message)
        {
            if (endpoints == null) return 0;

            UdpClient socket = _socket;
            if (socket == null || !_running) return 0;

            byte[] packet;
            try { packet = ProtoCodec.BuildUdp(type, message); }
            catch (Exception e) { RaiseError(e); return 0; }

            int count = 0;
            foreach (IPEndPoint ep in endpoints)
            {
                if (ep == null) continue;
                SendRaw(socket, packet, ep);
                count++;
            }
            return count;
        }

        private void SendRaw(UdpClient socket, byte[] packet, IPEndPoint endpoint)
        {
            // UdpClient 没有纯同步且安全的发送接口（Send 在某些平台上会阻塞），
            // 这里 fire-and-forget 并吞掉错误，避免一个坏端点拖垮整帧。
            var task = socket.SendAsync(packet, packet.Length, endpoint);
            task.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    if (Interlocked.Increment(ref _sendErrorLogged) <= 5)
                    {
                        NetLog.Trace("UDP 发送到 " + endpoint + " 失败: " +
                                     ProtoCodec.HumanizeError(t.Exception.GetBaseException()));
                    }
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private void RaiseError(Exception e)
        {
            NetLog.Exception(e, "UDP 服务器");
            MainThreadDispatcher.Post(() =>
            {
                var handler = Error;
                if (handler != null) handler(e);
            });
        }
    }
}
