using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetGame.Protocol;

namespace NetGame.ClientSide
{
    /// <summary>
    /// 客户端（纯 C# 逻辑层，不继承 MonoBehaviour，不引用 UnityEngine）。
    /// 职责：Connect(ip, port)、发送本地操作、接收服务端快照/指令、心跳与断线判定。
    /// 绝不监听端口、绝不替他人转发、绝不做权威判定。
    /// </summary>
    public sealed class Client
    {
        public event Action OnConnected;
        public event Action<string> OnDisconnected;  // 断开原因
        public event Action<NetMessage> OnMessage;
        public event Action<string> OnLog;

        private const int HeartbeatIntervalMs = 5000;
        private const int HeartbeatTimeoutSeconds = 15;

        private TcpClient _tcp;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private volatile bool _connected;
        private int _sequence;
        private int _disconnectRaised;
        private long _lastReceiveUtcTicks;

        public bool IsConnected => _connected;

        /// <summary>连接到服务器。成功后在后台启动接收循环与心跳循环。</summary>
        public async Task ConnectAsync(string ip, int port)
        {
            if (_connected) throw new InvalidOperationException("已连接");
            Interlocked.Exchange(ref _disconnectRaised, 0);
            Interlocked.Exchange(ref _lastReceiveUtcTicks, DateTime.UtcNow.Ticks);

            _cts = new CancellationTokenSource();
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(ip, port).ConfigureAwait(false);

            _stream = _tcp.GetStream();
            _connected = true;
            Log($"已连接到 {ip}:{port}");

            _ = ReceiveLoopAsync(_cts.Token);
            _ = HeartbeatLoopAsync(_cts.Token);

            OnConnected?.Invoke(); // 后台线程触发，由桥接层封送到主线程
        }

        /// <summary>主动断开。</summary>
        public void Disconnect(string reason = "主动断开")
        {
            FireDisconnect(reason);
        }

        /// <summary>发送一条消息（线程安全，内部串行化发送）。</summary>
        public async Task SendAsync(NetMessage msg)
        {
            if (!_connected || _stream == null) return;
            msg.Sequence = Interlocked.Increment(ref _sequence);

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_connected || _stream == null) return;
                await MessageCodec.WriteFrameAsync(_stream, msg).ConfigureAwait(false);
            }
            catch (Exception)
            {
                FireDisconnect("发送失败，连接已断开");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (_connected && !ct.IsCancellationRequested)
                {
                    NetMessage msg = await MessageCodec.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
                    if (msg == null)
                    {
                        FireDisconnect("服务器已关闭连接（房间可能已解散）");
                        return;
                    }
                    Interlocked.Exchange(ref _lastReceiveUtcTicks, DateTime.UtcNow.Ticks);
                    OnMessage?.Invoke(msg);
                }
            }
            catch (OperationCanceledException)
            {
                // 主动取消，忽略
            }
            catch (Exception)
            {
                FireDisconnect("连接中断");
            }
        }

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (_connected && !ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HeartbeatIntervalMs, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException) { break; }

                if (!_connected) break;

                try
                {
                    await SendAsync(new NetMessage(MessageType.Ping)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    FireDisconnect("发送心跳失败");
                    return;
                }

                long elapsedTicks = DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastReceiveUtcTicks);
                if (elapsedTicks > TimeSpan.TicksPerSecond * HeartbeatTimeoutSeconds)
                {
                    FireDisconnect("心跳超时");
                    return;
                }
            }
        }

        private void FireDisconnect(string reason)
        {
            if (Interlocked.Exchange(ref _disconnectRaised, 1) == 1) return; // 只触发一次
            _connected = false;
            try { _cts?.Cancel(); } catch { }
            try { _tcp?.Close(); } catch { }
            OnDisconnected?.Invoke(reason);
        }

        private void Log(string s) => OnLog?.Invoke(s);
    }
}
