using System;

namespace NetGame.Protocol
{
    /// <summary>
    /// 纯 C# 三维向量值类型。
    /// Server / Client 逻辑层禁止引用 UnityEngine.Vector3，网络消息中的坐标一律使用本结构，
    /// 由 Unity 表现层负责与 UnityEngine.Vector3 互转。
    /// </summary>
    public struct Vec3 : IEquatable<Vec3>
    {
        public float X;
        public float Y;
        public float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0f, 0f, 0f);

        public bool Equals(Vec3 other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is Vec3 v && Equals(v);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + X.GetHashCode();
                h = h * 31 + Y.GetHashCode();
                h = h * 31 + Z.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(Vec3 a, Vec3 b) => a.Equals(b);
        public static bool operator !=(Vec3 a, Vec3 b) => !a.Equals(b);

        public override string ToString() => $"({X}, {Y}, {Z})";
    }
}
