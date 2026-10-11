using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Game.Net.Host
{
    /// <summary>
    /// 一条 TCP 连接的服务端视图。
    /// 发送走“队列 + 信号量 + 单发送协程”，保证顺序且不阻塞任何线程。
    /// </summary>
    public sealed class TcpPeer
    {
        private int _closed;

        internal readonly ConcurrentQueue<byte[]> SendQueue = new ConcurrentQueue<byte[]>();
        internal readonly SemaphoreSlim SendSignal = new SemaphoreSlim(0);

        public int PeerId { get; internal set; }
        public TcpClient Client { get; private set; }
        public NetworkStream Stream { get; private set; }
        public IPEndPoint RemoteEndPoint { get; private set; }

        /// <summary>由 HostNetworkManager 在握手成功后写入；0 表示尚未加入房间。</summary>
        public uint PlayerId;
        public string SessionToken;

        /// <summary>最后一次收到该连接任何数据的单调毫秒时间。</summary>
        public long LastRecvMs;

        /// <summary>最近一次心跳发出的时间与序号，用于算 RTT。</summary>
        public long LastPingSentMs;
        public uint LastPingPayload;

        /// <summary>RTT（毫秒），-1 表示还没测出来。</summary>
        public long PingMs = -1;

        public bool IsClosed { get { return Volatile.Read(ref _closed) != 0; } }

        internal TcpPeer(int peerId, TcpClient client)
        {
            PeerId = peerId;
            Client = client;
            Stream = client.GetStream();
            RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            LastRecvMs = NetUtil.NowMs;
        }

        /// <summary>返回 true 表示本次调用是第一次标记关闭（用于保证只通知一次断开）。</summary>
        internal bool MarkClosed()
        {
            return Interlocked.Exchange(ref _closed, 1) == 0;
        }

        public string RemoteAddress
        {
            get
            {
                if (RemoteEndPoint == null) return "unknown";
                return RemoteEndPoint.Address + ":" + RemoteEndPoint.Port;
            }
        }

        public void Touch()
        {
            LastRecvMs = NetUtil.NowMs;
        }

        internal void Enqueue(byte[] frame)
        {
            if (frame == null || frame.Length == 0) return;
            if (IsClosed) return;

            SendQueue.Enqueue(frame);
            try { SendSignal.Release(); }
            catch (SemaphoreFullException) { /* 理论不会发生 */ }
            catch (ObjectDisposedException) { /* 正在关闭 */ }
        }

        internal void Close()
        {
            if (!MarkClosed()) return;

            try { if (Stream != null) Stream.Close(); } catch (Exception) { }
            try { if (Client != null) Client.Close(); } catch (Exception) { }
        }
    }
}
