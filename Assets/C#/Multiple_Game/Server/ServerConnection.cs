using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetGame.Protocol;

namespace NetGame.ServerSide
{
    /// <summary>
    /// 服务端的一条连接（对应一个已 Accept 的客户端）。负责该连接上的收发。
    /// 纯 C#，不依赖 UnityEngine；回调在后台线程触发，由上层封送到主线程。
    /// </summary>
    public sealed class ServerConnection
    {
        public event Action<ServerConnection, NetMessage> OnMessage;
        public event Action<ServerConnection> OnDisconnected;

        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private volatile bool _running;
        private int _sequence;
        private int _disconnectRaised;

        /// <summary>入房后由 Server 分配；0 表示尚未入房。</summary>
        public int PlayerId { get; set; }

        public string Username { get; set; }
        public DateTime LastReceiveUtc { get; private set; }
        public bool IsConnected => _running;

        public ServerConnection(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            LastReceiveUtc = DateTime.UtcNow;
            _running = true;
        }

        /// <summary>后台接收循环，直到对端断开或出现异常。</summary>
        public async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (_running && !ct.IsCancellationRequested)
                {
                    NetMessage msg = await MessageCodec.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
                    if (msg == null) break; // 对端正常关闭
                    LastReceiveUtc = DateTime.UtcNow;
                    OnMessage?.Invoke(this, msg);
                }
            }
            catch (Exception)
            {
                // 连接中断，统一走断开清理
            }
            finally
            {
                Close();
                if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0)
                    OnDisconnected?.Invoke(this);
            }
        }

        public async Task SendAsync(NetMessage msg)
        {
            if (!_running) return;
            msg.Sequence = Interlocked.Increment(ref _sequence);

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_running)
                    await MessageCodec.WriteFrameAsync(_stream, msg).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 发送失败：关闭连接，触发断开清理
                Close();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Close()
        {
            _running = false;
            try { _tcp.Close(); } catch { }
        }
    }
}
