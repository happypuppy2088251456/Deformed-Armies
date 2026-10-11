using System;
using System.Collections.Concurrent;
using System.IO;
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
    /// 客户端 TCP 通道：首次接入房主用。
    /// 帧格式与房主一致：[4 字节大端长度][protobuf Envelope]。
    /// </summary>
    public sealed class TcpClientChannel : IDisposable
    {
        private readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);

        private TcpClient _client;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;
        private int _closed;
        private int _closeNotified;

        public bool IsConnected { get; private set; }
        public IPEndPoint RemoteEndPoint { get; private set; }

        public event Action<Envelope> EnvelopeReceived;
        /// <summary>通道关闭（正常或异常），参数为原因。</summary>
        public event Action<string> Closed;

        // ------------------------------------------------------------------

        /// <summary>连接房主。失败返回 false 并给出可读错误。</summary>
        public async Task<bool> ConnectAsync(string host, int port, int timeoutMs, Action<string> onError = null)
        {
            if (IsConnected)
            {
                if (onError != null) onError("已经连接过了");
                return false;
            }

            var client = new TcpClient();
            try
            {
                client.NoDelay = true;

                Task connectTask = client.ConnectAsync(host, port);
                Task finished = await Task.WhenAny(connectTask, Task.Delay(timeoutMs)).ConfigureAwait(false);

                if (finished != connectTask)
                {
                    try { client.Close(); } catch (Exception) { }
                    if (onError != null) onError("连接超时（" + timeoutMs + "ms）：确认房主已创建房间、IP 正确、Windows 防火墙已放行");
                    return false;
                }

                await connectTask.ConfigureAwait(false); // 失败会在这里抛异常
            }
            catch (Exception e)
            {
                try { client.Close(); } catch (Exception) { }
                string message = ProtoCodec.HumanizeError(e);
                NetLog.Warn("TCP 连接失败 " + host + ":" + port + " -> " + message);
                if (onError != null) onError(message);
                return false;
            }

            _client = client;
            _stream = client.GetStream();
            RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            _cts = new CancellationTokenSource();
            IsConnected = true;

            CancellationToken ct = _cts.Token;
            _ = Task.Run(() => ReceiveLoopAsync(ct));
            _ = Task.Run(() => SendLoopAsync(ct));

            NetLog.Info("TCP 已连接 " + host + ":" + port);
            return true;
        }

        // ------------------------------------------------------------------

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            byte[] header = new byte[4];

            try
            {
                while (!ct.IsCancellationRequested && IsConnected)
                {
                    if (!await ReadExactAsync(_stream, header, 0, 4, ct).ConfigureAwait(false)) break;

                    int length = ProtoCodec.ReadFrameLength(header, 0);
                    if (length <= 0 || length > NetConst.MaxTcpPayloadBytes)
                    {
                        NetLog.Warn("收到非法帧长度 " + length + "，断开连接");
                        break;
                    }

                    byte[] body = new byte[length];
                    if (!await ReadExactAsync(_stream, body, 0, length, ct).ConfigureAwait(false)) break;

                    Envelope envelope;
                    if (!ProtoCodec.TryParse(body, 0, length, out envelope)) continue;

                    var captured = envelope;
                    MainThreadDispatcher.Post(() =>
                    {
                        var handler = EnvelopeReceived;
                        if (handler != null) handler(captured);
                    });
                }

                Close("与房主的连接已断开");
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException e)
            {
                if (!ct.IsCancellationRequested) Close("网络错误: " + ProtoCodec.HumanizeError(e));
            }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested) Close("网络异常: " + ProtoCodec.HumanizeError(e));
            }
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer, offset + read, count - read, ct).ConfigureAwait(false);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        private async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && IsConnected)
                {
                    await _sendSignal.WaitAsync(ct).ConfigureAwait(false);

                    byte[] frame;
                    while (IsConnected && _sendQueue.TryDequeue(out frame))
                    {
                        await _stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException e)
            {
                if (!ct.IsCancellationRequested) Close("发送失败: " + ProtoCodec.HumanizeError(e));
            }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested) Close("发送异常: " + ProtoCodec.HumanizeError(e));
            }
        }

        // ------------------------------------------------------------------

        public bool Send(IMessage message)
        {
            if (!IsConnected || message == null) return false;

            byte[] frame;
            try { frame = ProtoCodec.FrameTcp(message); }
            catch (Exception e) { NetLog.Exception(e, "打包 TCP 帧"); return false; }

            _sendQueue.Enqueue(frame);
            try { _sendSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
            return true;
        }

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;

            IsConnected = false;

            try { if (_cts != null) _cts.Cancel(); } catch (Exception) { }
            try { if (_stream != null) _stream.Close(); } catch (Exception) { }
            try { if (_client != null) _client.Close(); } catch (Exception) { }

            try { if (_cts != null) _cts.Dispose(); } catch (Exception) { }
            _cts = null;
            _stream = null;
            _client = null;

            if (Interlocked.Exchange(ref _closeNotified, 1) != 0) return;

            var capturedReason = reason ?? "连接关闭";
            MainThreadDispatcher.Post(() =>
            {
                var handler = Closed;
                if (handler != null) handler(capturedReason);
            });
        }

        public void Dispose()
        {
            Close("已释放");
        }
    }
}
