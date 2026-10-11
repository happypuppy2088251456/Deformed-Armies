using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// 主线程派发器。
    ///
    /// TCP / UDP 的收发都跑在后台线程上，而 Unity 的 API（Transform、GameObject、
    /// UI 等）只能在主线程调用。所以后台线程收到消息后一律用
    /// <see cref="Post"/> 投递到主线程，再由主线程回调游戏逻辑。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static int _mainThreadId = -1;

        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private int _queued;

        /// <summary>当前是否运行在主线程（未初始化时一律按主线程处理）。</summary>
        public static bool IsMainThread
        {
            get { return _mainThreadId == -1 || Thread.CurrentThread.ManagedThreadId == _mainThreadId; }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            EnsureExists();
        }

        /// <summary>确保场景里存在一个常驻的派发器。</summary>
        public static MainThreadDispatcher EnsureExists()
        {
            if (_instance != null) return _instance;

            var go = new GameObject("[MainThreadDispatcher]");
            _instance = go.AddComponent<MainThreadDispatcher>();
            return _instance;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        /// <summary>
        /// 把回调排到主线程执行。已经在主线程时直接同步执行（保持调用顺序直觉）。
        /// </summary>
        public static void Post(Action action)
        {
            if (action == null) return;

            if (_instance == null || IsMainThread)
            {
                try { action(); }
                catch (Exception e) { NetLog.Exception(e, "主线程回调"); }
                return;
            }

            _instance.Enqueue(action);
        }

        private void Enqueue(Action action)
        {
            _queue.Enqueue(action);
            Interlocked.Increment(ref _queued);
        }

        private void Update()
        {
            // 限制单帧处理量，避免一帧内涌入大量包把帧率拖垮
            const int maxPerFrame = 256;

            int processed = 0;
            Action action;
            while (processed < maxPerFrame && _queue.TryDequeue(out action))
            {
                Interlocked.Decrement(ref _queued);
                processed++;

                try { action(); }
                catch (Exception e) { NetLog.Exception(e, "主线程回调"); }
            }
        }

        /// <summary>当前积压的回调数量（可用来观察是否出现处理瓶颈）。</summary>
        public static int PendingCount
        {
            get { return _instance == null ? 0 : _instance._queued; }
        }
    }
}
