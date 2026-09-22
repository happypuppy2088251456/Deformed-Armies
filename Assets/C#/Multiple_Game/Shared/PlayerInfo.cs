namespace NetGame.Protocol
{
    /// <summary>
    /// 玩家基本信息（用于加入/生成时携带 ID、名字与出生位置）。
    /// </summary>
    public sealed class PlayerInfo
    {
        public int PlayerId;
        public string Username;
        public Vec3 Position;
    }
}
