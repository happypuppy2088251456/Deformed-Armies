namespace NetGame.Protocol
{
    /// <summary>
    /// 消息类型。帧头以 int32 存储本枚举值。
    /// </summary>
    public enum MessageType : int
    {
        None = 0,

        /// <summary>客户端 → 服务端：心跳。</summary>
        Ping = 1,

        /// <summary>服务端 → 客户端：心跳应答。</summary>
        Pong = 2,

        /// <summary>客户端 → 服务端：请求加入房间（用户名）。</summary>
        JoinRequest = 3,

        /// <summary>服务端 → 客户端：加入结果（成功/失败 + 自己 ID + 已在房玩家）。</summary>
        JoinResponse = 4,

        /// <summary>服务端 → 其他客户端：有新玩家加入。</summary>
        PlayerJoined = 5,

        /// <summary>服务端 → 其他客户端：有玩家离开。</summary>
        PlayerLeft = 6,

        /// <summary>客户端 → 服务端：上报自己的权威状态。</summary>
        PlayerState = 7,

        /// <summary>服务端 → 客户端：转发他人的状态快照。</summary>
        StateBroadcast = 8,

        /// <summary>客户端(房主) → 服务端：开始游戏；服务端广播给所有人。</summary>
        GameStart = 9,

        /// <summary>服务端 → 所有客户端：房间已解散。</summary>
        RoomDissolved = 10,
    }
}
