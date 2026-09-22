using System;
using System.Collections.Concurrent;
using NetGame.ClientSide;
using NetGame.Protocol;
using UnityEngine;

namespace NetGame.Unity
{
    /// <summary>
    /// Client 的薄 Unity 表现层。仅把纯 C# Client 的后台线程事件封送到主线程，
    /// 供 UI / ClientGameController 订阅，并负责在 Update 中派发。
    ///
    /// 关键约束：网络回调绝不直接操作 GameObject —— 全部通过主线程队列在 Update 中派发。
    /// </summary>
    public sealed class ClientMonoBridge : MonoBehaviour
    {
        public static ClientMonoBridge Instance { get; private set; }

        /// <summary>主线程安全事件（供 UI / ClientGameController 订阅）。</summary>
        public event Action OnConnected;
        public event Action<string> OnDisconnected;
        public event Action<NetMessage> OnMessage;

        private Client _client;
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        // 保存处理器，便于 Detach 反订阅
        private Action _onConnectedHandler;
        private Action<string> _onDisconnectedHandler;
        private Action<NetMessage> _onMessageHandler;
        private Action<string> _onLogHandler;

        public Client Client => _client;
        public bool IsConnected => _client != null && _client.IsConnected;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>绑定一个纯 C# Client（主机与普通客户端都走这里）。</summary>
        public void Attach(Client client)
        {
            Detach();
            _client = client;
            if (_client == null) return;

            _onConnectedHandler = () => Enqueue(() => OnConnected?.Invoke());
            _onDisconnectedHandler = r => Enqueue(() => OnDisconnected?.Invoke(r));
            _onMessageHandler = m => Enqueue(() => OnMessage?.Invoke(m));
            _onLogHandler = s => Enqueue(() => Debug.Log("[Client] " + s));

            _client.OnConnected += _onConnectedHandler;
            _client.OnDisconnected += _onDisconnectedHandler;
            _client.OnMessage += _onMessageHandler;
            _client.OnLog += _onLogHandler;
        }

        public void Detach()
        {
            if (_client != null)
            {
                _client.OnConnected -= _onConnectedHandler;
                _client.OnDisconnected -= _onDisconnectedHandler;
                _client.OnMessage -= _onMessageHandler;
                _client.OnLog -= _onLogHandler;
            }
            _client = null;
        }

        /// <summary>发送一条消息（可在任意线程调用）。</summary>
        public void Send(NetMessage msg)
        {
            if (_client == null || !_client.IsConnected)
            {
                Debug.LogWarning("[ClientMonoBridge] 未连接，无法发送");
                return;
            }
            _ = _client.SendAsync(msg);
        }

        private void Update()
        {
            while (_queue.TryDequeue(out Action a)) a?.Invoke();
        }

        private void Enqueue(Action a) => _queue.Enqueue(a);
    }
}
