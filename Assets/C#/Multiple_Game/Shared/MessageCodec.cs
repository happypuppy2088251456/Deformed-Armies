using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetGame.Protocol
{
    /// <summary>
    /// 自定义二进制编解码器（无第三方依赖，纯 C# 可用）。
    ///
    /// 帧格式（小端 Little-Endian）：
    ///   [0..3]   int32 payloadLength —— 之后 payload 的字节数
    ///   [4..7]   int32 sequence      —— 序列号
    ///   [8..11]  int32 messageType   —— MessageType
    ///   [12..]   payload             —— 按 MessageType 变化的二进制体
    ///
    /// 字符串统一使用长度前缀 + UTF-8 编码。
    /// </summary>
    public static class MessageCodec
    {
        public const int HeaderSize = 12;
        public const int MaxFrameSize = 1024 * 1024; // 单帧上限 1 MB，防止脏数据

        // ==================== 帧级编解码 ====================

        public static byte[] Encode(NetMessage m)
        {
            byte[] payload;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WritePayload(w, m);
                w.Flush();
                payload = ms.ToArray();
            }

            using (var outMs = new MemoryStream(HeaderSize + payload.Length))
            using (var hw = new BinaryWriter(outMs, Encoding.UTF8, leaveOpen: true))
            {
                hw.Write(payload.Length);   // 4
                hw.Write(m.Sequence);       // 4
                hw.Write((int)m.Type);      // 4
                hw.Write(payload);
                hw.Flush();
                return outMs.ToArray();
            }
        }

        public static NetMessage Decode(byte[] frame, int offset, int length)
        {
            using (var ms = new MemoryStream(frame, offset, length))
            using (var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true))
            {
                int payloadLen = r.ReadInt32();
                if (payloadLen < 0 || payloadLen > MaxFrameSize)
                    throw new InvalidDataException($"帧长度非法: {payloadLen}");

                int seq = r.ReadInt32();
                int typeInt = r.ReadInt32();
                var m = new NetMessage { Sequence = seq, Type = (MessageType)typeInt };
                ReadPayload(r, m);
                return m;
            }
        }

        /// <summary>
        /// 从流中读取一帧（阻塞等待到完整帧）。返回 null 表示对端正常关闭（EOF）。
        /// 需要调用方保证在后台线程 / ConfigureAwait(false) 上下文中执行。
        /// </summary>
        public static async Task<NetMessage> ReadFrameAsync(Stream stream, CancellationToken ct)
        {
            byte[] header = new byte[HeaderSize];
            int got = await ReadExactAsync(stream, header, 0, HeaderSize, ct).ConfigureAwait(false);
            if (got == 0) return null;                 // 正常 EOF
            if (got < HeaderSize) throw new EndOfStreamException("不完整的帧头");

            int payloadLen = BitConverter.ToInt32(header, 0);
            if (payloadLen < 0 || payloadLen > MaxFrameSize)
                throw new InvalidDataException($"帧长度非法: {payloadLen}");

            byte[] frame = new byte[HeaderSize + payloadLen];
            Array.Copy(header, 0, frame, 0, HeaderSize);
            await ReadExactAsync(stream, frame, HeaderSize, payloadLen, ct).ConfigureAwait(false);
            return Decode(frame, 0, frame.Length);
        }

        public static async Task WriteFrameAsync(Stream stream, NetMessage m)
        {
            byte[] frame = Encode(m);
            await stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
        }

        // ==================== payload 读写 ====================

        private static void WritePayload(BinaryWriter w, NetMessage m)
        {
            switch (m.Type)
            {
                case MessageType.JoinRequest:
                    w.Write(m.Username ?? string.Empty);
                    break;

                case MessageType.JoinResponse:
                    w.Write(m.Success);
                    w.Write(m.Error ?? string.Empty);
                    w.Write(m.PlayerId);
                    WriteVec3(w, m.SpawnPosition);
                    WritePlayerList(w, m.Players);
                    break;

                case MessageType.PlayerJoined:
                    WritePlayerInfo(w, m.JoinedPlayer);
                    break;

                case MessageType.PlayerLeft:
                    w.Write(m.LeftPlayerId);
                    break;

                case MessageType.PlayerState:
                    WritePlayerState(w, m.State);
                    break;

                case MessageType.StateBroadcast:
                    WriteStateList(w, m.States);
                    break;

                // Ping / Pong / GameStart / RoomDissolved：无 payload
            }
        }

        private static void ReadPayload(BinaryReader r, NetMessage m)
        {
            switch (m.Type)
            {
                case MessageType.JoinRequest:
                    m.Username = r.ReadString();
                    break;

                case MessageType.JoinResponse:
                    m.Success = r.ReadBoolean();
                    m.Error = r.ReadString();
                    m.PlayerId = r.ReadInt32();
                    m.SpawnPosition = ReadVec3(r);
                    m.Players = ReadPlayerList(r);
                    break;

                case MessageType.PlayerJoined:
                    m.JoinedPlayer = ReadPlayerInfo(r);
                    break;

                case MessageType.PlayerLeft:
                    m.LeftPlayerId = r.ReadInt32();
                    break;

                case MessageType.PlayerState:
                    m.State = ReadPlayerState(r);
                    break;

                case MessageType.StateBroadcast:
                    m.States = ReadStateList(r);
                    break;
            }
        }

        private static void WritePlayerList(BinaryWriter w, List<PlayerInfo> list)
        {
            int n = list?.Count ?? 0;
            w.Write(n);
            if (list != null)
                foreach (var p in list) WritePlayerInfo(w, p);
        }

        private static List<PlayerInfo> ReadPlayerList(BinaryReader r)
        {
            int n = r.ReadInt32();
            var list = new List<PlayerInfo>(n);
            for (int i = 0; i < n; i++) list.Add(ReadPlayerInfo(r));
            return list;
        }

        private static void WritePlayerInfo(BinaryWriter w, PlayerInfo p)
        {
            w.Write(p.PlayerId);
            w.Write(p.Username ?? string.Empty);
            WriteVec3(w, p.Position);
        }

        private static PlayerInfo ReadPlayerInfo(BinaryReader r)
        {
            var p = new PlayerInfo();
            p.PlayerId = r.ReadInt32();
            p.Username = r.ReadString();
            p.Position = ReadVec3(r);
            return p;
        }

        private static void WriteStateList(BinaryWriter w, List<PlayerState> list)
        {
            int n = list?.Count ?? 0;
            w.Write(n);
            if (list != null)
                foreach (var s in list) WritePlayerState(w, s);
        }

        private static List<PlayerState> ReadStateList(BinaryReader r)
        {
            int n = r.ReadInt32();
            var list = new List<PlayerState>(n);
            for (int i = 0; i < n; i++) list.Add(ReadPlayerState(r));
            return list;
        }

        private static void WritePlayerState(BinaryWriter w, PlayerState s)
        {
            w.Write(s.PlayerId);
            WriteVec3(w, s.Position);
            w.Write(s.Yaw);
            w.Write(s.Pitch);
            w.Write(s.VelX);
            w.Write(s.VelY);
            w.Write(s.VelZ);
            w.Write(s.InputX);
            w.Write(s.InputY);
            w.Write(s.IsRunning);
            w.Write(s.IsGrounded);
        }

        private static PlayerState ReadPlayerState(BinaryReader r)
        {
            var s = new PlayerState();
            s.PlayerId = r.ReadInt32();
            s.Position = ReadVec3(r);
            s.Yaw = r.ReadSingle();
            s.Pitch = r.ReadSingle();
            s.VelX = r.ReadSingle();
            s.VelY = r.ReadSingle();
            s.VelZ = r.ReadSingle();
            s.InputX = r.ReadSingle();
            s.InputY = r.ReadSingle();
            s.IsRunning = r.ReadBoolean();
            s.IsGrounded = r.ReadBoolean();
            return s;
        }

        private static void WriteVec3(BinaryWriter w, Vec3 v)
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }

        private static Vec3 ReadVec3(BinaryReader r)
        {
            return new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        // ==================== 工具 ====================

        private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await stream.ReadAsync(buffer, offset + total, count - total, ct).ConfigureAwait(false);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }
}
