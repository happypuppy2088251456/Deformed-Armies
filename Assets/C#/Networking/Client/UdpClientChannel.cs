using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Game.Net;
using Game.Protocol;

namespace Game.Net.Client
{
    /// <summary>
    /// 客户端 UDP 通道：进入游戏后的实时同步通道。
    ///
    /// 生命周期：
    ///   1. Start()  —— 创建 socket 并 Connect 到房主 UDP 端口
    ///   2. 反复发 UdpHello(playerId, sessionToken) 直到收到 UdpHelloAck
    ///   3. 握手完成后开始发 UdpInput / 收 UdpSnapshot
    ///
    /// <see cref="Update"/> 必须在主线程被周期性调用（ClientNetworkManager 会代劳），
    /// 用于重发握手包、发心跳、做超时判定。
    /// </summary>
    public sealed class UdpClientChannel : IDisposable
    {
        private const float HelloIntervalSeconds = 0.5f;
        private const float HandshakeTimeoutSeconds = 6f;
        private const float KeepAliveIntervalSeconds = 1f;
        private const float ServerTimeoutSeconds = 8f;

        private UdpClient _socket;
        private CancellationTokenSource _cts;
        private volatile bool _running;

        private IPEndPoint _serverEndPoint;
        private uint _playerId;
        private string _sessionToken;

        private long _startedMs;
        private long _lastHelloMs;
        private long _lastKeepAliveMs;
        private long _lastRecvMs;

        private volatile bool _handshakeDone;
        private int _recvNotified;

        /// <summary>握手是否完成（可以开始发输入了）。</summary>
        public bool IsHandshakeDone { get { return _handshakeDone; } }

        public bool IsRunning { get { return _running; } }

        /// <summary>与房主的 UDP RTT，-1 表示未知。</summary>
        public long RttMs { get; private set; } = -1;

        public event Action<UdpPacketType, byte[]> DatagramReceived;
        public event Action HandshakeCompleted;
        /// <summary>通道关闭，参数为原因。</summary>
        public event Action<string> Closed;

        public UdpClientChannel()
        {
        }

        // ------------------------------------------------------------------

        public bool Start(string host, int port, uint playerId, string sessionToken, out string error)
        {
            error = null;

            if (_running)
            {
                error = "UDP 通道已经在运行";
                return false;
            }

            try
            {
                _serverEndPoint = new IPEndPoint(ResolveHost(host), port);
                _playerId = playerId;
                _sessionToken = sessionToken;

                _socket = new UdpClient(AddressFamily.InterNetwork);
                _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

                try { _socket.Client.ReceiveBufferSize = 1 << 20; } catch (Exception) { }
                try { _socket.Client.SendBufferSize = 1 << 20; } catch (Exception) { }

                // 连接后只会收到该端点的包，同时避免被 ICMP 干扰
                _socket.Connect(_serverEndPoint);

                _cts = new CancellationTokenSource();
                _running = true;
                _handshakeDone = false;
                _recvNotified = 0;

                long now = NetUtil.NowMs;
                _startedMs = now;
                _lastHelloMs = 0;   // 立刻发第一次握手
                _lastKeepAliveMs = now;
                _lastRecvMs = now;

                Task.Run(() => ReceiveLoopAsync(_cts.Token));

                NetLog.Info("UDP 通道已打开 -> " + _serverEndPoint);
                return true;
            }
            catch (Exception e)
            {
                error = ProtoCodec.HumanizeError(e);
                NetLog.Exception(e, "打开 UDP 通道失败");
                Stop(null);
                return false;
            }
        }

        private static IPAddress ResolveHost(string host)
        {
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return ip;

            IPAddress[] addresses = Dns.GetHostAddresses(host);
            foreach (IPAddress a in addresses)
            {
                if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            }
            throw new InvalidOperationException("无法解析主机名: " + host);
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

                    // Windows 上收到 ICMP 端口不可达会以 ConnectionReset 抛出，属正常
                    if (e.SocketErrorCode == SocketError.ConnectionReset ||
                        e.SocketErrorCode == SocketError.MessageSize ||
                        e.SocketErrorCode == SocketError.Interrupted)
                    {
                        continue;
                    }

                    NetLog.Warn("UDP 接收错误: " + e.SocketErrorCode);
                    continue;
                }
                catch (Exception e)
                {
                    if (ct.IsCancellationRequested) break;
                    NetLog.Trace("UDP 接收异常: " + e.Message);
                    continue;
                }

                byte[] buffer = result.Buffer;
                if (buffer == null || buffer.Length < NetConst.UdpHeaderBytes) continue;

                _lastRecvMs = NetUtil.NowMs;

                UdpPacketType type;
                int bodyOffset, bodyLength;
                if (!ProtoCodec.TryUnwrapUdp(buffer, buffer.Length, out type, out bodyOffset, out bodyLength))
                    continue;

                byte[] body = new byte[bodyLength];
                if (bodyLength > 0) Buffer.BlockCopy(buffer, bodyOffset, body, 0, bodyLength);

                HandlePacket(type, body);
            }
        }

        /// <summary>协议层包（握手 / 心跳）在内部消化，其余交给上层。</summary>
        private void HandlePacket(UdpPacketType type, byte[] body)
        {
            switch (type)
            {
                case UdpPacketType.HelloAck:
                {
                    UdpHelloAck ack;
                    if (!ProtoCodec.TryParseUdpBody(body, 0, body.Length, out ack)) return;

                    if (!_handshakeDone)
                    {
                        _handshakeDone = true;

                        // 立刻发一次心跳，这样 UI 上能马上显示出 RTT，而不用等一个心跳周期
                        _lastKeepAliveMs = 0;
                        _lastRecvMs = NetUtil.NowMs;

                        NetLog.Info("UDP 握手完成，房主分配的玩家 ID = " + ack.PlayerId);
                        MainThreadDispatcher.Post(() =>
                        {
                            var handler = HandshakeCompleted;
                            if (handler != null) handler();
                        });
                    }
                    return;
                }

                case UdpPacketType.Bye:
                {
                    UdpBye bye;
                    string reason = ProtoCodec.TryParseUdpBody(body, 0, body.Length, out bye)
                        ? bye.Reason
                        : "房主断开了 UDP 通道";
                    MainThreadDispatcher.Post(() => Stop(reason));
                    return;
                }

                case UdpPacketType.Ping:
                {
                    UdpPing ping;
                    if (ProtoCodec.TryParseUdpBody(body, 0, body.Length, out ping))
                    {
                        Send(UdpPacketType.Pong, new UdpPong
                        {
                            SentTimeMs = ping.SentTimeMs,
                            ServerTimeMs = (ulong)NetUtil.NowMs,
                        });
                    }
                    return;
                }

                case UdpPacketType.Pong:
                {
                    UdpPong pong;
                    if (ProtoCodec.TryParseUdpBody(body, 0, body.Length, out pong))
                    {
                        long rtt = NetUtil.NowMs - (long)pong.SentTimeMs;
                        if (rtt >= 0 && rtt < 10000) RttMs = rtt;
                    }
                    return;
                }
            }

            var capturedType = type;
            var capturedBody = body;
            MainThreadDispatcher.Post(() =>
            {
                var handler = DatagramReceived;
                if (handler != null) handler(capturedType, capturedBody);
            });
        }

        // ------------------------------------------------------------------

        /// <summary>主线程周期性调用：重发握手包 / 心跳 / 超时判定。</summary>
        public void Update()
        {
            if (!_running) return;

            long now = NetUtil.NowMs;

            if (!_handshakeDone)
            {
                if (now - _lastHelloMs >= (long)(HelloIntervalSeconds * 1000f))
                {
                    _lastHelloMs = now;
                    Send(UdpPacketType.Hello, new UdpHello
                    {
                        PlayerId = _playerId,
                        SessionToken = _sessionToken ?? string.Empty,
                    });
                }

                if (now - _startedMs > (long)(HandshakeTimeoutSeconds * 1000f))
                {
                    Stop("UDP 握手超时（房主可能被防火墙拦住 UDP 端口）");
                }
                return;
            }

            // 心跳：既测 RTT，也让房主知道我们还活着
            if (now - _lastKeepAliveMs >= (long)(KeepAliveIntervalSeconds * 1000f))
            {
                _lastKeepAliveMs = now;
                Send(UdpPacketType.Ping, new UdpPing { SentTimeMs = (ulong)now });
            }

            // 长时间收不到房主任何 UDP 包 => 认为断线
            if (now - _lastRecvMs > (long)(ServerTimeoutSeconds * 1000f))
            {
                Stop("与房主的 UDP 连接超时");
            }
        }

        // ------------------------------------------------------------------

        public bool Send(UdpPacketType type, IMessage message)
        {
            UdpClient socket = _socket;
            if (socket == null || !_running) return false;

            byte[] packet;
            try { packet = ProtoCodec.BuildUdp(type, message); }
            catch (Exception e) { NetLog.Exception(e, "打包 UDP 包"); return false; }

            try
            {
                var task = socket.SendAsync(packet, packet.Length);
                task.ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                        NetLog.Trace("UDP 发送失败: " + ProtoCodec.HumanizeError(t.Exception.GetBaseException()));
                }, TaskContinuationOptions.ExecuteSynchronously);
                return true;
            }
            catch (Exception e)
            {
                NetLog.Trace("UDP 发送异常: " + e.Message);
                return false;
            }
        }

        /// <summary>发送一帧输入（握手完成后才有意义）。</summary>
        public bool SendInput(UdpInput input)
        {
            if (!_handshakeDone) return false;
            return Send(UdpPacketType.Input, input);
        }

        /// <summary>优雅关闭：先告知房主，再关 socket。</summary>
        public void Stop(string reason)
        {
            if (!_running && _socket == null) return;

            if (_running)
            {
                try { Send(UdpPacketType.Bye, new UdpBye { Reason = reason ?? string.Empty }); }
                catch (Exception) { }
            }

            _running = false;
            _handshakeDone = false;

            try { if (_cts != null) _cts.Cancel(); } catch (Exception) { }
            try { if (_socket != null) _socket.Close(); } catch (Exception) { }
            _socket = null;

            try { if (_cts != null) _cts.Dispose(); } catch (Exception) { }
            _cts = null;

            if (Interlocked.Exchange(ref _recvNotified, 1) != 0) return;

            var captured = reason ?? "UDP 通道已关闭";
            MainThreadDispatcher.Post(() =>
            {
                var handler = Closed;
                if (handler != null) handler(captured);
            });
        }

        public void Dispose()
        {
            Stop("已释放");
        }
    }
}
