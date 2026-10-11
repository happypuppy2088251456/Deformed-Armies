using System.Collections.Generic;
using System.Text;
using Game.Net.Client;
using Game.Net.Host;
using Game.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Net.UI
{
    /// <summary>
    /// 主界面控制器。
    ///
    /// 房主流程：[创建房间] -> HostNetworkManager 打开 TCP+UDP 监听
    ///                     -> 界面上显示本机地址（如 192.168.1.3:10003），抄给队友
    ///                     -> 队友陆续出现在玩家列表里 -> [开始游戏]
    ///
    /// 客机流程：[加入房间] -> 输入 192.168.1.3:10003 -> TCP 接入 + UDP 握手
    ///
    /// 用的是内置 UGUI（InputField / Text / Button），不依赖 TextMeshPro。
    /// 如果你的项目用 TMP，把类型换成 TMP_InputField / TMP_Text 即可，其余逻辑不变。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuController : MonoBehaviour
    {
        private enum Mode { None, Host, Client }

        [Header("面板")]
        [SerializeField] private GameObject mainPanel;
        [SerializeField] private GameObject roomPanel;

        [Header("主界面控件")]
        [SerializeField] private Button createRoomButton;
        [SerializeField] private Button joinRoomButton;
        [SerializeField] private InputField playerNameInput;
        [SerializeField] private InputField addressInput;

        [Header("房间界面控件")]
        [SerializeField] private Text roomTitleText;
        [SerializeField] private Text addressText;
        [SerializeField] private Button copyAddressButton;
        [SerializeField] private Text playerListText;
        [SerializeField] private Button readyButton;
        [SerializeField] private Button startGameButton;
        [SerializeField] private Button leaveRoomButton;

        [Header("多网卡（可选）")]
        [Tooltip("若项目里放了下拉框，房主可以从本机所有 IPv4 里手动挑一个对外公布")]
        [SerializeField] private Dropdown nicDropdown;

        [Header("状态提示")]
        [SerializeField] private Text statusText;

        [Header("行为")]
        [Tooltip("开始游戏后自动加载的场景名，留空表示由外部逻辑接管")]
        [SerializeField] private string gameSceneName = "";

        private Mode _mode = Mode.None;
        private HostNetworkManager _host;
        private ClientNetworkManager _client;

        private bool _localReady;
        private float _refreshTimer;
        private readonly StringBuilder _builder = new StringBuilder(256);

        /// <summary>当前要抄给队友的地址，例如 192.168.1.3:10003。</summary>
        public string CurrentAddress { get; private set; }

        // ------------------------------------------------------------------

        private void Awake()
        {
            if (createRoomButton != null) createRoomButton.onClick.AddListener(OnClickCreateRoom);
            if (joinRoomButton != null) joinRoomButton.onClick.AddListener(OnClickJoinRoom);
            if (copyAddressButton != null) copyAddressButton.onClick.AddListener(OnClickCopyAddress);
            if (readyButton != null) readyButton.onClick.AddListener(OnClickReady);
            if (startGameButton != null) startGameButton.onClick.AddListener(OnClickStartGame);
            if (leaveRoomButton != null) leaveRoomButton.onClick.AddListener(OnClickLeaveRoom);

            if (addressInput != null && string.IsNullOrEmpty(addressInput.text))
                addressInput.text = "192.168.1.3:10003";

            ShowMainPanel();
            SetStatus("点击「创建房间」开房，或输入房主的 IP:端口后加入。");
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        private void Update()
        {
            if (_mode == Mode.None) return;

            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f) return;
            _refreshTimer = 0.5f;

            RefreshPlayerList();
        }

        // ------------------------------------------------------------------
        // 创建房间（房主）
        // ------------------------------------------------------------------

        public void OnClickCreateRoom()
        {
            _host = EnsureHostManager();

            // 多网卡时让玩家自己选对外公布的网卡（有线 / 无线 / 虚拟网卡）
            if (nicDropdown != null)
            {
                if (nicDropdown.options == null || nicDropdown.options.Count == 0)
                {
                    List<string> candidates = NetUtil.GetLocalIPv4List();
                    nicDropdown.ClearOptions();
                    nicDropdown.AddOptions(candidates);
                    nicDropdown.RefreshShownValue();
                }

                List<string> list = NetUtil.GetLocalIPv4List();
                int index = nicDropdown.value;
                if (index >= 0 && index < list.Count) _host.PreferredIPv4 = list[index];
            }

            string error;
            if (!_host.CreateRoom(out error))
            {
                SetStatus("创建房间失败：" + error);
                NetLog.Warn("创建房间失败: " + error);
                return;
            }

            _mode = Mode.Host;
            _localReady = true;
            CurrentAddress = _host.HostAddress;

            SubscribeHost();

            if (addressText != null) addressText.text = CurrentAddress;
            if (roomTitleText != null) roomTitleText.text = _host.RoomName + "（你是房主）";
            if (readyButton != null) readyButton.gameObject.SetActive(false);
            if (startGameButton != null) startGameButton.gameObject.SetActive(true);

            ShowRoomPanel();
            RefreshPlayerList();

            SetStatus("房间已创建。把地址 " + CurrentAddress + " 告诉队友（同一交换机/局域网内）。");
            NetLog.Info("创建房间成功，地址 = " + CurrentAddress);
        }

        // ------------------------------------------------------------------
        // 加入房间（客机）
        // ------------------------------------------------------------------

        public void OnClickJoinRoom()
        {
            string address = addressInput != null ? addressInput.text : string.Empty;

            string host;
            int port;
            if (!NetUtil.TryParseAddress(address, NetConst.DefaultTcpPort, out host, out port))
            {
                SetStatus("地址格式不正确，正确写法如 192.168.1.3:10003");
                return;
            }

            _client = EnsureClientManager();
            _client.PlayerName = playerNameInput != null && !string.IsNullOrEmpty(playerNameInput.text)
                ? playerNameInput.text
                : "Player";

            SubscribeClient();

            SetStatus("正在连接 " + NetUtil.FormatAddress(host, port) + " ...");

            CurrentAddress = NetUtil.FormatAddress(host, port);
            _client.JoinRoom(CurrentAddress, _client.PlayerName);
        }

        // ------------------------------------------------------------------
        // 房间内操作
        // ------------------------------------------------------------------

        public void OnClickCopyAddress()
        {
            if (string.IsNullOrEmpty(CurrentAddress)) return;

            GUIUtility.systemCopyBuffer = CurrentAddress; // 方便直接粘贴到群里
            SetStatus("地址已复制到剪贴板：" + CurrentAddress);
        }

        public void OnClickReady()
        {
            if (_mode != Mode.Client || _client == null) return;

            _localReady = !_localReady;
            _client.SetReady(_localReady);
            SetStatus(_localReady ? "已准备" : "取消准备");
        }

        public void OnClickStartGame()
        {
            if (_mode != Mode.Host || _host == null) return;

            if (_host.Room.Count <= 1)
            {
                SetStatus("还没有其他人加入，先等队友进来。");
                return;
            }

            if (!_host.StartGame())
            {
                SetStatus("开始游戏失败（可能已经开始了）。");
                return;
            }

            SetStatus("游戏开始！");
            LoadGameSceneIfConfigured();
        }

        public void OnClickLeaveRoom()
        {
            if (_mode == Mode.Host && _host != null) _host.CloseRoom();
            else if (_mode == Mode.Client && _client != null) _client.LeaveRoom();

            Unsubscribe();
            _mode = Mode.None;
            _localReady = false;
            CurrentAddress = null;

            ShowMainPanel();
            SetStatus("已离开房间。");
        }

        // ------------------------------------------------------------------
        // 事件订阅
        // ------------------------------------------------------------------

        private void SubscribeHost()
        {
            if (_host == null) return;

            // 先退订再订阅，避免重复点击造成多次回调
            UnsubscribeHost();

            _host.PlayerJoined += OnHostPlayerJoined;
            _host.PlayerLeft += OnHostPlayerLeft;
            _host.PlayerUdpReady += OnHostPlayerUdpReady;
            _host.RoomStopped += OnHostRoomStopped;
            _host.NetworkError += OnNetworkError;
        }

        private void SubscribeClient()
        {
            if (_client == null) return;

            UnsubscribeClient();

            _client.Joined += OnClientJoined;
            _client.JoinFailed += OnClientJoinFailed;
            _client.Disconnected += OnClientDisconnected;
            _client.UdpLost += OnClientUdpLost;
            _client.RemotePlayerJoined += OnClientRemotePlayerJoined;
            _client.RemotePlayerLeft += OnClientRemotePlayerLeft;
            _client.GameStarting += OnClientGameStarting;
        }

        private void UnsubscribeHost()
        {
            if (_host == null) return;

            _host.PlayerJoined -= OnHostPlayerJoined;
            _host.PlayerLeft -= OnHostPlayerLeft;
            _host.PlayerUdpReady -= OnHostPlayerUdpReady;
            _host.RoomStopped -= OnHostRoomStopped;
            _host.NetworkError -= OnNetworkError;
        }

        private void UnsubscribeClient()
        {
            if (_client == null) return;

            _client.Joined -= OnClientJoined;
            _client.JoinFailed -= OnClientJoinFailed;
            _client.Disconnected -= OnClientDisconnected;
            _client.UdpLost -= OnClientUdpLost;
            _client.RemotePlayerJoined -= OnClientRemotePlayerJoined;
            _client.RemotePlayerLeft -= OnClientRemotePlayerLeft;
            _client.GameStarting -= OnClientGameStarting;
        }

        private void Unsubscribe()
        {
            UnsubscribeHost();
            UnsubscribeClient();
        }

        // ------------------------------------------------------------------
        // 事件处理
        // ------------------------------------------------------------------

        private void OnHostPlayerJoined(PlayerSession session)
        {
            SetStatus(session.PlayerName + " 加入了房间（等待 UDP 握手...）");
            RefreshPlayerList();
        }

        private void OnHostPlayerLeft(PlayerSession session, string reason)
        {
            SetStatus(session.PlayerName + " 离开了房间（" + reason + "）");
            RefreshPlayerList();
        }

        private void OnHostPlayerUdpReady(PlayerSession session)
        {
            SetStatus(session.PlayerName + " 已就绪（UDP " + session.UdpEndPoint + "）");
            RefreshPlayerList();
        }

        private void OnHostRoomStopped()
        {
            if (_mode != Mode.Host) return;
            _mode = Mode.None;
            ShowMainPanel();
            SetStatus("房间已关闭。");
        }

        private void OnClientJoined(JoinAccepted accepted)
        {
            _mode = Mode.Client;
            CurrentAddress = _client != null ? _client.ServerAddress : CurrentAddress;

            if (addressText != null) addressText.text = CurrentAddress;
            if (roomTitleText != null) roomTitleText.text = accepted.RoomName + "（你是玩家 " + accepted.PlayerId + "）";
            if (startGameButton != null) startGameButton.gameObject.SetActive(false);
            if (readyButton != null) readyButton.gameObject.SetActive(true);

            ShowRoomPanel();
            RefreshPlayerList();

            SetStatus("已加入房间：" + accepted.RoomName + "，你的玩家 ID = " + accepted.PlayerId);
        }

        private void OnClientJoinFailed(string reason)
        {
            _mode = Mode.None;
            ShowMainPanel();
            SetStatus("加入失败：" + reason);
        }

        private void OnClientDisconnected(string reason)
        {
            if (_mode != Mode.Client) return;
            _mode = Mode.None;
            ShowMainPanel();
            SetStatus("已断开：" + reason);
        }

        private void OnClientUdpLost(string reason)
        {
            SetStatus("UDP 通道中断：" + reason + "（游戏数据无法同步，请重进房间）");
        }

        private void OnClientRemotePlayerJoined(PlayerInfo info)
        {
            SetStatus(info.PlayerName + " 加入了房间");
            RefreshPlayerList();
        }

        private void OnClientRemotePlayerLeft(uint playerId, string reason)
        {
            SetStatus("玩家 " + playerId + " 离开了房间");
            RefreshPlayerList();
        }

        private void OnClientGameStarting(StartGame start)
        {
            SetStatus("房主开始了游戏（mapSeed=" + start.MapSeed + "）");
            LoadGameSceneIfConfigured();
        }

        private void OnNetworkError(System.Exception e)
        {
            SetStatus("网络错误：" + ProtoCodec.HumanizeError(e));
        }

        // ------------------------------------------------------------------
        // 界面刷新
        // ------------------------------------------------------------------

        private void RefreshPlayerList()
        {
            if (playerListText == null) return;

            _builder.Length = 0;

            if (_mode == Mode.Host && _host != null)
            {
                List<PlayerSession> players = _host.Room.Snapshot();
                for (int i = 0; i < players.Count; i++)
                {
                    PlayerSession p = players[i];
                    _builder.Append(p.IsHost ? "[房主] " : "       ");
                    _builder.Append(p.PlayerName);
                    _builder.Append(p.IsHost ? "" : (p.IsUdpReady ? "  (已连接)" : "  (握手中...)"));
                    if (p.PingMs >= 0 && !p.IsHost) _builder.Append("  ").Append(p.PingMs).Append("ms");
                    _builder.Append('\n');
                }
                _builder.Append("\n共 ").Append(players.Count).Append(" / ").Append(_host.Room.MaxPlayers).Append(" 人");
            }
            else if (_mode == Mode.Client && _client != null)
            {
                IList<PlayerInfo> players = _client.Players;
                for (int i = 0; i < players.Count; i++)
                {
                    PlayerInfo p = players[i];
                    bool me = _client.PlayerId == p.PlayerId;
                    _builder.Append(p.IsHost ? "[房主] " : "       ");
                    _builder.Append(p.PlayerName);
                    if (me) _builder.Append("  (你)");
                    if (p.Ready && !p.IsHost) _builder.Append("  [已准备]");
                    _builder.Append('\n');
                }
                _builder.Append("\n共 ").Append(players.Count).Append(" 人");
            }

            playerListText.text = _builder.ToString();
        }

        private void ShowMainPanel()
        {
            if (mainPanel != null) mainPanel.SetActive(true);
            if (roomPanel != null) roomPanel.SetActive(false);
        }

        private void ShowRoomPanel()
        {
            if (mainPanel != null) mainPanel.SetActive(false);
            if (roomPanel != null) roomPanel.SetActive(true);
        }

        private void SetStatus(string message)
        {
            if (statusText != null) statusText.text = message;
            NetLog.Info(message);
        }

        private void LoadGameSceneIfConfigured()
        {
            if (string.IsNullOrEmpty(gameSceneName)) return;
            SceneManager.LoadScene(gameSceneName);
        }

        // ------------------------------------------------------------------
        // 管理器兜底创建（这样不挂脚本也能跑起来）
        // ------------------------------------------------------------------

        private static HostNetworkManager EnsureHostManager()
        {
            if (HostNetworkManager.Instance != null) return HostNetworkManager.Instance;

            var go = new GameObject("[HostNetworkManager]");
            return go.AddComponent<HostNetworkManager>();
        }

        private static ClientNetworkManager EnsureClientManager()
        {
            if (ClientNetworkManager.Instance != null) return ClientNetworkManager.Instance;

            var go = new GameObject("[ClientNetworkManager]");
            return go.AddComponent<ClientNetworkManager>();
        }
    }
}
