using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using NetGame.ClientSide;
using NetGame.Host;
using NetGame.ServerSide;
using UnityEngine;

namespace NetGame.Unity
{
    /// <summary>
    /// 大厅/启动编排：根据 UI 选择“成为主机”或“作为客户端”，分别组装 HostClient 或 Client。
    /// 本类自身不监听端口、也不连接服务器——它只负责 new 出并编排纯 C# 的
    /// Server / Client / HostClient，生命周期与启动顺序都在这里管理。
    /// </summary>
    public sealed class LobbyController : MonoBehaviour
    {
        public static LobbyController Instance { get; private set; }

        public event Action OnHostReady;       // 主机已建房（服务端已监听）
        public event Action<string> OnStatus;  // 主线程状态文本

        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        private HostClient _host;
        private Client _client;
        private Server _server;

        public bool IsHost => _host != null;
        public Server Server => _server;

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

        private void Update()
        {
            while (_queue.TryDequeue(out Action a)) a?.Invoke();
        }

        // ==================== 启动入口 ====================

        /// <summary>成为主机：进程内启动 Server(port)，再让本地 Client 连 127.0.0.1:port。</summary>
        public void StartHost(string username, int port)
        {
            Cleanup();
            SetStatus($"正在创建房间（端口 {port}）...");

            try
            {
                _host = new HostClient(port);
                _server = _host.Server;
            }
            catch (Exception e)
            {
                SetStatus("创建房间失败：" + e.Message);
                Cleanup();
                return;
            }

            // 服务端日志转发到主线程（仅日志；主机玩家的游戏表现仍走 Client）
            _host.Server.OnLog += s => Enqueue(() => Debug.Log("[Server] " + s));
            _host.Client.OnLog += s => Enqueue(() => Debug.Log("[HostClient] " + s));

            BindClient(_host.Client);
            SetUsername(username);
            _ = RunHostAsync();
        }

        /// <summary>作为客户端：直接连接 IP:Port 的服务端。</summary>
        public void StartClient(string ip, int port, string username)
        {
            Cleanup();
            SetStatus($"正在连接 {ip}:{port} ...");

            _client = new Client();
            _client.OnLog += s => Enqueue(() => Debug.Log("[Client] " + s));

            BindClient(_client);
            SetUsername(username);
            _ = RunClientAsync(ip, port);
        }

        /// <summary>退出：断开 Client，并销毁 Server（若为主机则房间解散）。</summary>
        public void Shutdown()
        {
            _host?.Shutdown();
            _client?.Disconnect("退出");
            Cleanup();
        }

        // ==================== 内部 ====================

        private async Task RunHostAsync()
        {
            try
            {
                await _host.StartAsync();
                Enqueue(() => OnHostReady?.Invoke());
                SetStatus("房间已创建，等待其他玩家加入...");
            }
            catch (Exception e)
            {
                SetStatus("主机启动失败：" + e.Message);
                Cleanup();
            }
        }

        private async Task RunClientAsync(string ip, int port)
        {
            try
            {
                await _client.ConnectAsync(ip, port);
                SetStatus("连接成功，正在加入房间...");
            }
            catch (Exception e)
            {
                SetStatus("连接失败：" + e.Message);
                Cleanup();
            }
        }

        private void BindClient(Client client)
        {
            if (ClientMonoBridge.Instance == null)
            {
                SetStatus("场景缺少 ClientMonoBridge");
                return;
            }
            ClientMonoBridge.Instance.Attach(client);
        }

        private void SetUsername(string username)
        {
            if (ClientGameController.Instance != null)
                ClientGameController.Instance.SetUsername(username);
        }

        private void Cleanup()
        {
            _host = null;
            _client = null;
            _server = null;
            ClientMonoBridge.Instance?.Detach();
        }

        private void SetStatus(string s) => Enqueue(() => OnStatus?.Invoke(s));
        private void Enqueue(Action a) => _queue.Enqueue(a);
    }
}
