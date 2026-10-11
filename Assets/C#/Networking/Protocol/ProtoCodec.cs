using System;
using System.IO;
using Google.Protobuf;
using Game.Protocol;

namespace Game.Net
{
    /// <summary>
    /// protobuf 编解码 + 自定义分帧。
    ///
    /// TCP 是字节流，必须自己定边界，这里用最简单可靠的
    /// “4 字节大端长度前缀 + protobuf 负载”（length-prefix framing）。
    ///
    /// UDP 自带边界，但为了丢弃交换机上的杂包，额外加 4 字节头：
    /// [0..1] 魔数 0x4E4B  [2] 包类型  [3] 协议版本
    /// </summary>
    public static class ProtoCodec
    {
        private static class ParserCache<T> where T : IMessage<T>, new()
        {
            public static readonly MessageParser<T> Parser = new MessageParser<T>(() => new T());
        }

        // ------------------------------------------------------------------
        // protobuf 基础
        // ------------------------------------------------------------------

        public static byte[] Serialize(IMessage message)
        {
            if (message == null) throw new ArgumentNullException("message");
            return message.ToByteArray();
        }

        public static T Parse<T>(byte[] buffer, int offset, int count) where T : IMessage<T>, new()
        {
            return ParserCache<T>.Parser.ParseFrom(buffer, offset, count);
        }

        public static T Parse<T>(byte[] buffer) where T : IMessage<T>, new()
        {
            return ParserCache<T>.Parser.ParseFrom(buffer, 0, buffer.Length);
        }

        public static bool TryParse<T>(byte[] buffer, int offset, int count, out T result)
            where T : IMessage<T>, new()
        {
            result = default(T);
            try
            {
                result = Parse<T>(buffer, offset, count);
                return true;
            }
            catch (InvalidProtocolBufferException e)
            {
                NetLog.Trace("protobuf 解析失败: " + e.Message);
                return false;
            }
            catch (Exception e)
            {
                NetLog.Trace("protobuf 解析异常: " + e.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // TCP 分帧
        // ------------------------------------------------------------------

        /// <summary>把消息打包成 [4 字节大端长度][负载] 的一帧。</summary>
        public static byte[] FrameTcp(IMessage message)
        {
            byte[] body = Serialize(message);
            if (body.Length > NetConst.MaxTcpPayloadBytes)
            {
                throw new InvalidOperationException(
                    "TCP 单帧负载过大: " + body.Length + " 字节（上限 " + NetConst.MaxTcpPayloadBytes + "）");
            }

            byte[] frame = new byte[4 + body.Length];
            WriteUInt32BigEndian(frame, 0, (uint)body.Length);
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            return frame;
        }

        /// <summary>读取 4 字节大端长度头。</summary>
        public static int ReadFrameLength(byte[] header, int offset)
        {
            return (header[offset] << 24)
                 | (header[offset + 1] << 16)
                 | (header[offset + 2] << 8)
                 | header[offset + 3];
        }

        public static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        // ------------------------------------------------------------------
        // UDP 分帧
        // ------------------------------------------------------------------

        /// <summary>打包一个 UDP 数据报（含 4 字节头）。</summary>
        public static byte[] BuildUdp(UdpPacketType type, IMessage message)
        {
            byte[] body = message == null ? new byte[0] : Serialize(message);

            int total = NetConst.UdpHeaderBytes + body.Length;
            if (total > NetConst.MaxUdpDatagramBytes)
            {
                // 快照超长时截断是危险的，宁可报错让上层降频/压缩
                throw new InvalidOperationException(
                    "UDP 数据报过大: " + total + " 字节（上限 " + NetConst.MaxUdpDatagramBytes + "）");
            }

            byte[] packet = new byte[total];
            packet[0] = (byte)(NetConst.UdpMagic >> 8);
            packet[1] = (byte)(NetConst.UdpMagic & 0xFF);
            packet[2] = (byte)type;
            packet[3] = NetConst.ProtocolVersion;

            if (body.Length > 0) Buffer.BlockCopy(body, 0, packet, NetConst.UdpHeaderBytes, body.Length);
            return packet;
        }

        /// <summary>校验 UDP 头并取出负载。返回 false 表示这不是本协议的包，直接丢。</summary>
        public static bool TryUnwrapUdp(byte[] buffer, int length, out UdpPacketType type, out int bodyOffset, out int bodyLength)
        {
            type = UdpPacketType.None;
            bodyOffset = 0;
            bodyLength = 0;

            if (buffer == null || length < NetConst.UdpHeaderBytes) return false;

            ushort magic = (ushort)((buffer[0] << 8) | buffer[1]);
            if (magic != NetConst.UdpMagic) return false;
            if (buffer[3] != NetConst.ProtocolVersion) return false;

            byte rawType = buffer[2];
            if (rawType == 0 || rawType > (byte)UdpPacketType.Bye) return false;

            type = (UdpPacketType)rawType;
            bodyOffset = NetConst.UdpHeaderBytes;
            bodyLength = length - NetConst.UdpHeaderBytes;
            return true;
        }

        /// <summary>解出 UDP 负载为指定消息类型。</summary>
        public static bool TryParseUdpBody<T>(byte[] buffer, int offset, int length, out T result)
            where T : IMessage<T>, new()
        {
            if (length <= 0)
            {
                result = new T();
                return true;
            }
            return TryParse(buffer, offset, length, out result);
        }

        // ------------------------------------------------------------------
        // 便捷方法
        // ------------------------------------------------------------------

        public static string Describe(Envelope envelope)
        {
            if (envelope == null) return "(null)";
            return "Envelope(seq=" + envelope.Seq + ", body=" + envelope.BodyCase + ")";
        }

        /// <summary>把异常里的 socket 噪音过滤成人话，方便在 UI 上提示玩家。</summary>
        public static string HumanizeError(Exception e)
        {
            if (e == null) return "未知错误";

            var io = e as IOException;
            if (io != null && io.InnerException != null)
            {
                var inner = io.InnerException;
                if (inner is System.Net.Sockets.SocketException) return HumanizeSocket((System.Net.Sockets.SocketException)inner);
            }

            var se = e as System.Net.Sockets.SocketException;
            if (se != null) return HumanizeSocket(se);

            return e.Message;
        }

        private static string HumanizeSocket(System.Net.Sockets.SocketException se)
        {
            switch (se.SocketErrorCode)
            {
                case System.Net.Sockets.SocketError.AddressAlreadyInUse:
                    return "端口已被占用（可能已经开了一个房间，或被其他程序占用）";
                case System.Net.Sockets.SocketError.AccessDenied:
                    return "端口被系统拒绝（小于 1024 的端口需要管理员权限，或被杀毒软件拦截）";
                case System.Net.Sockets.SocketError.ConnectionRefused:
                    return "对方拒绝连接（房主没开房间 / 防火墙拦截）";
                case System.Net.Sockets.SocketError.TimedOut:
                    return "连接超时（不在同一局域网，或防火墙拦截）";
                case System.Net.Sockets.SocketError.HostUnreachable:
                case System.Net.Sockets.SocketError.NetworkUnreachable:
                    return "网络不可达（检查交换机 / 网线 / IP 网段是否一致）";
                case System.Net.Sockets.SocketError.ConnectionReset:
                    return "连接被对方重置";
                default:
                    return se.SocketErrorCode + ": " + se.Message;
            }
        }
    }
}
