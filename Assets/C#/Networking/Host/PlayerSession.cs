using System;
using System.Net;
using Game.Net;
using Game.Protocol;

namespace Game.Net.Host
{
    /// <summary>
    /// 房主视角下的一个玩家：把 TCP 连接、UDP 端点、令牌、心跳状态绑在一起。
    /// 房主自己也是一个 PlayerSession（IsHost = true，Tcp 为 null）。
    /// </summary>
    public sealed class PlayerSession
    {
        public uint PlayerId;
        public string PlayerName = "Player";
        public string SessionToken;

        /// <summary>是否房主本人（房主没有 TCP 连接，消息直接走本地）。</summary>
        public bool IsHost;

        public bool Ready;

        /// <summary>该玩家的 TCP 连接；房主自己为 null。</summary>
        public TcpPeer Tcp;

        /// <summary>该玩家的 UDP 端点；收到 UdpHello 之前为 null。</summary>
        public IPEndPoint UdpEndPoint;

        public long LastTcpRecvMs;
        public long LastUdpRecvMs;

        /// <summary>最近一次 RTT，-1 表示未测出。</summary>
        public long PingMs = -1;

        /// <summary>房主已处理到该玩家的最后一个输入帧号。</summary>
        public uint LastProcessedInputTick;

        public bool IsUdpReady { get { return UdpEndPoint != null; } }

        public PlayerSession()
        {
            long now = NetUtil.NowMs;
            LastTcpRecvMs = now;
            LastUdpRecvMs = now;
        }

        public string RemoteAddress
        {
            get
            {
                if (Tcp != null) return Tcp.RemoteAddress;
                if (IsHost) return "本机(房主)";
                return "unknown";
            }
        }

        /// <summary>转成发给客户端的 PlayerInfo。</summary>
        public PlayerInfo ToPlayerInfo()
        {
            return new PlayerInfo
            {
                PlayerId = PlayerId,
                PlayerName = PlayerName ?? string.Empty,
                Ready = Ready,
                IsHost = IsHost,
                PingMs = PingMs < 0 ? 0u : (uint)Math.Min(PingMs, uint.MaxValue),
                Address = RemoteAddress,
            };
        }

        /// <summary>是否已超过 timeoutMs 没有任何 TCP 流量（判定掉线）。</summary>
        public bool IsTcpTimedOut(long nowMs, long timeoutMs)
        {
            return Tcp != null && nowMs - LastTcpRecvMs > timeoutMs;
        }
    }
}
