namespace NetGame.Protocol
{
    /// <summary>
    /// 玩家实时状态快照（位置、朝向、速度、输入等）。
    /// 客户端本地是权威移动方，状态上报给服务端，由服务端转发给其他玩家。
    /// </summary>
    public struct PlayerState
    {
        public int PlayerId;
        public Vec3 Position;

        /// <summary>身体偏航角（度）。</summary>
        public float Yaw;

        /// <summary>相机俯仰角（度）。</summary>
        public float Pitch;

        public float VelX;
        public float VelY;
        public float VelZ;

        public float InputX;
        public float InputY;

        public bool IsRunning;
        public bool IsGrounded;
    }
}
