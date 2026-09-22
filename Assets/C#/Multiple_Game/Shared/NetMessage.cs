using System.Collections.Generic;

namespace NetGame.Protocol
{
    /// <summary>
    /// 网络消息信封。根据 <see cref="Type"/> 使用不同字段，未用字段为默认值。
    /// 序列化由 <see cref="MessageCodec"/> 负责（自定义二进制，无第三方依赖）。
    /// </summary>
    public sealed class NetMessage
    {
        public MessageType Type;
        public int Sequence;

        // ---- JoinRequest ----
        public string Username;

        // ---- JoinResponse ----
        public bool Success;
        public string Error;
        public int PlayerId;
        public Vec3 SpawnPosition;         // 自己的出生点（服务端分配）
        public List<PlayerInfo> Players;   // 已在房间内的其他玩家（含最新位置）

        // ---- PlayerState ----
        public PlayerState State;

        // ---- StateBroadcast ----
        public List<PlayerState> States;

        // ---- PlayerJoined ----
        public PlayerInfo JoinedPlayer;

        // ---- PlayerLeft ----
        public int LeftPlayerId;

        public NetMessage()
        {
        }

        public NetMessage(MessageType type)
        {
            Type = type;
        }
    }
}
