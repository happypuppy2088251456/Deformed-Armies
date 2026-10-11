using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Game.Protocol;

namespace Game.Net.Host
{
    /// <summary>
    /// 房主的 TCP 监听器：负责 Client 的首次接入。
    ///
    /// 每个连接两条后台任务：
    ///   * ReceiveLoop  —— 4 字节大端长度头 + 负载，反序列化成 Envelope
    ///   * SendLoop     —— 串行消费发送队列
    /// 所有对外的回调都通过 MainThreadDispatcher 切回 Unity 主线程。
    /// </summary>
    public sealed class TcpHostServer : IDisposable
    {
        private readonly ConcurrentDictionary<int, TcpPeer> _peers =
            new ConcurrentDictionary<int, TcpPeer>();

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptTask;
        private int _nextPeerId;
        private volatile bool _running;

        /// <summary>监听的实际端点（端口填 0 时这里能看到系统分配的真实端口）。</summary>
        public IPEndPoint LocalEndPoint { get; private set; }

        public bool IsRunning { get { return _running; } }
        public int PeerCount { get { return _peers.Count; } }

        public event Action<TcpPeer> PeerConnected;
        public event Action<TcpPeer> PeerDisconnected;
        public event Action<TcpPeer, Envelope> EnvelopeReceived;
        public event Action<Exception> Error;

        // ------------------------------------------------------------------
        // 启停
        // ------------------------------------------------------------------

        public bool Start(IPAddress bindAddress, int port, out string error)
        {
            error = null;

            if (_running)
            {
                error = "TCP 服务器已经在运行";
                return false;
            }

            try
            {
                _listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
                _listener.Start(64); // backlog
                _listener.Server.NoDelay = true;

                LocalEndPoint = (IPEndPoint)_listener.LocalEndpoint;

                _cts = new CancellationTokenSource();
                _running = true;
                _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));

                NetLog.Info("TCP 监听已启动: " + LocalEndPoint);
                return true;
            }
            catch (Exception e)
            {
                error = ProtoCodec.HumanizeError(e);
                NetLog.Exception(e, "启动 TCP 监听失败");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            _running = false;

            try { if (_cts != null) _cts.Cancel(); } catch (Exception) { }

            try { if (_listener != null) _listener.Stop(); } catch (Exception) { }
            _listener = null;

            foreach (var kv in _peers)
            {
                ClosePeer(kv.Value, "服务器关闭");
            }
            _peers.Clear();

            try { if (_cts != null) _cts.Dispose(); } catch (Exception) { }
            _cts = null;
            _acceptTask = null;
        }

        public void Dispose()
        {
            Stop();
        }

        // ------------------------------------------------------------------
        // 接入
        // ------------------------------------------------------------------

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _running)
            {
                // 先把监听器抓到局部变量：Stop() 可能随时把字段置空
                TcpListener listener = _listener;
                if (listener == null) break;

                TcpClient client = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }   // Stop() 关掉监听器
                catch (InvalidOperationException) { break; } // 监听器未启动 / 已停止
                catch (SocketException e)
                {
                    if (ct.IsCancellationRequested) break;
                    NetLog.Warn("Accept 失败: " + e.SocketErrorCode);
                    continue;
                }
                catch (Exception e)
                {
                    if (ct.IsCancellationRequested) break;
                    RaiseError(e);
                    continue;
                }

                if (client == null) continue;

                try
                {
                    client.NoDelay = true;
                }
                catch (Exception) { }

                int peerId = Interlocked.Increment(ref _nextPeerId);
                var peer = new TcpPeer(peerId, client);
                _peers[peerId] = peer;

                NetLog.Info("TCP 新连接 #" + peerId + " 来自 " + peer.RemoteAddress);

                var capturedPeer = peer;
                var capturedToken = ct;

                _ = Task.Run(() => ReceiveLoopAsync(capturedPeer, capturedToken));
                _ = Task.Run(() => SendLoopAsync(capturedPeer, capturedToken));
                MainThreadDispatcher.Post(() =>
                {
                    var handler = PeerConnected;
                    if (handler != null) handler(capturedPeer);
                });
            }
        }

        /// <summary>主动断开某个连接。</summary>
        public void Disconnect(TcpPeer peer, string reason)
        {
            if (peer == null) return;
            NetLog.Info("断开 TCP #" + peer.PeerId + " (" + reason + ")");
            ClosePeer(peer, reason);
        }

        // ------------------------------------------------------------------
        // 收
        // ------------------------------------------------------------------

        private async Task ReceiveLoopAsync(TcpPeer peer, CancellationToken ct)
        {
            byte[] header = new byte[4];
            NetworkStream stream = peer.Stream;

            try
            {
                while (!ct.IsCancellationRequested && !peer.IsClosed)
                {
                    if (!await ReadExactAsync(stream, header, 0, 4, ct).ConfigureAwait(false)) break;

                    int length = ProtoCodec.ReadFrameLength(header, 0);

                    if (length <= 0 || length > NetConst.MaxTcpPayloadBytes)
                    {
                        NetLog.Warn("非法帧长度 " + length + "，来自 " + peer.RemoteAddress + "，断开该连接");
                        break;
                    }

                    byte[] body = new byte[length];
                    if (!await ReadExactAsync(stream, body, 0, length, ct).ConfigureAwait(false)) break;

                    peer.Touch();

                    Envelope envelope;
                    if (!ProtoCodec.TryParse(body, 0, length, out envelope)) continue;

                    var capturedPeer = peer;
                    var capturedEnvelope = envelope;
                    MainThreadDispatcher.Post(() =>
                    {
                        var handler = EnvelopeReceived;
                        if (handler != null) handler(capturedPeer, capturedEnvelope);
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException e)
            {
                if (!ct.IsCancellationRequested) NetLog.Trace("TCP 读取中断 " + peer.RemoteAddress + ": " + e.Message);
            }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested) RaiseError(e);
            }
            finally
            {
                TcpPeer removed;
                _peers.TryRemove(peer.PeerId, out removed);
                if (ClosePeer(peer, "连接结束"))
                {
                    var capturedPeer = peer;
                    MainThreadDispatcher.Post(() =>
                    {
                        var handler = PeerDisconnected;
                        if (handler != null) handler(capturedPeer);
                    });
                }
            }
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer, offset + read, count - read, ct).ConfigureAwait(false);
                if (n <= 0) return false; // 对端正常关闭
                read += n;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // 发
        // ------------------------------------------------------------------

        private async Task SendLoopAsync(TcpPeer peer, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && !peer.IsClosed)
                {
                    await peer.SendSignal.WaitAsync(ct).ConfigureAwait(false);

                    byte[] frame;
                    while (!peer.IsClosed && peer.SendQueue.TryDequeue(out frame))
                    {
                        await peer.Stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException e)
            {
                if (!ct.IsCancellationRequested) NetLog.Trace("TCP 写入失败 " + peer.RemoteAddress + ": " + e.Message);
            }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested) RaiseError(e);
            }
            finally
            {
                ClosePeer(peer, "发送循环结束");
            }
        }

        /// <summary>给单个连接发送一条消息（非阻塞，实际写入由发送循环完成）。</summary>
        public bool Send(TcpPeer peer, IMessage message)
        {
            if (peer == null || message == null || peer.IsClosed) return false;

            byte[] frame;
            try { frame = ProtoCodec.FrameTcp(message); }
            catch (Exception e) { RaiseError(e); return false; }

            peer.Enqueue(frame);
            return true;
        }

        /// <summary>给指定玩家 ID 的所有连接发消息（正常情况一玩家一连接）。</summary>
        public int Broadcast(IMessage message, TcpPeer except = null)
        {
            if (message == null) return 0;

            byte[] frame;
            try { frame = ProtoCodec.FrameTcp(message); }
            catch (Exception e) { RaiseError(e); return 0; }

            int count = 0;
            foreach (var kv in _peers)
            {
                TcpPeer peer = kv.Value;
                if (peer == null || peer.IsClosed) continue;
                if (except != null && ReferenceEquals(peer, except)) continue;

                peer.Enqueue(frame);
                count++;
            }
            return count;
        }

        public void BroadcastToPlayers(System.Collections.Generic.IEnumerable<TcpPeer> peers, IMessage message)
        {
            if (peers == null || message == null) return;

            byte[] frame;
            try { frame = ProtoCodec.FrameTcp(message); }
            catch (Exception e) { RaiseError(e); return; }

            foreach (TcpPeer peer in peers)
            {
                if (peer == null || peer.IsClosed) continue;
                peer.Enqueue(frame);
            }
        }

        // ------------------------------------------------------------------

        private bool ClosePeer(TcpPeer peer, string reason)
        {
            if (peer == null) return false;
            if (!peer.MarkClosed()) return false;

            try { peer.Close(); } catch (Exception) { }

            NetLog.Trace("关闭 TCP #" + peer.PeerId + " (" + reason + ")");
            return true;
        }

        private void RaiseError(Exception e)
        {
            NetLog.Exception(e, "TCP 服务器");
            MainThreadDispatcher.Post(() =>
            {
                var handler = Error;
                if (handler != null) handler(e);
            });
        }
    }
}
