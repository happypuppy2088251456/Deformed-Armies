using System;

namespace Game.Net
{
    /// <summary>
    /// 全局网络常量。TCP 和 UDP 默认使用同一个端口号（不同协议不冲突），
    /// 这样房主只需要把 "192.168.1.3:10003" 一个地址告诉队友即可。
    /// </summary>
    public static class NetConst
    {
        /// <summary>协议版本，握手时用于拒绝不兼容的客户端。</summary>
        public const byte ProtocolVersion = 1;

        public const int DefaultTcpPort = 10003;
        public const int DefaultUdpPort = 10003;

        /// <summary>UDP 魔数 'NK'，用来丢弃交换机上其他程序的杂包。</summary>
        public const ushort UdpMagic = 0x4E4B;

        /// <summary>TCP 单帧最大负载（4 字节长度前缀所允许的上限）。</summary>
        public const int MaxTcpPayloadBytes = 64 * 1024;

        /// <summary>UDP 头长度：魔数(2) + 类型(1) + 版本(1)。</summary>
        public const int UdpHeaderBytes = 4;

        /// <summary>单包 UDP 建议上限，避免 IP 分片（MTU 1500 - IP/UDP 头）。</summary>
        public const int MaxUdpDatagramBytes = 1200;

        public const int DefaultMaxPlayers = 16;
        public const int DefaultTickRate = 20;

        /// <summary>TCP 心跳间隔（秒）。</summary>
        public const float TcpHeartbeatSeconds = 2f;

        /// <summary>多久没收到该玩家的任何 TCP 数据就判定掉线（秒）。</summary>
        public const float PeerTimeoutSeconds = 10f;

        /// <summary>TCP 连接超时（秒）。</summary>
        public const int ConnectTimeoutMs = 5000;
    }

    /// <summary>
    /// UDP 包类型。放在 C# 而不是 proto 里，避免 protoc 的
    /// C# 枚举前缀裁剪规则造成成员名歧义。
    /// </summary>
    public enum UdpPacketType : byte
    {
        None = 0,
        Hello = 1,     // C -> H  UDP 握手
        HelloAck = 2,  // H -> C  握手确认
        Input = 3,     // C -> H  输入帧
        Snapshot = 4,  // H -> C  世界快照
        Ping = 5,      // 双向
        Pong = 6,      // 双向
        Bye = 7,       // 双向  优雅断开
    }
}
