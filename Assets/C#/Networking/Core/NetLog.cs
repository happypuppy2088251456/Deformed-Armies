using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// 统一日志开关。网络代码跑在后台线程，Debug.Log 本身是线程安全的，
    /// 但量很大时会拖慢主线程，所以啰嗦日志只在 Editor / Development Build 输出。
    ///
    /// 注意：这里故意不 using System.Diagnostics，否则 UnityEngine.Debug
    /// 会和 System.Diagnostics.Debug 撞名。
    /// </summary>
    public static class NetLog
    {
        /// <summary>是否输出逐包级别的详细日志。</summary>
        public static bool Verbose = false;

        public static void Info(string message)
        {
            if (!Verbose) return;
            Debug.Log("[Net] " + message);
        }

        public static void Warn(string message)
        {
            Debug.LogWarning("[Net] " + message);
        }

        public static void Error(string message)
        {
            Debug.LogError("[Net] " + message);
        }

        public static void Exception(System.Exception e, string context = null)
        {
            Debug.LogError("[Net] " + (context ?? "异常") + ": " + e);
        }

        /// <summary>开发期排障用；发布包里调用点会被编译器整个移除。</summary>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void Trace(string message)
        {
            if (Verbose) Debug.Log("[Net] " + message);
        }
    }
}
