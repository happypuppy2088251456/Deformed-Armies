using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Protocol;
using UnityEngine;

namespace Game.Net.Client
{
    /// <summary>
    /// 客机（Client）网络管理器 —— 负责“加入房间”的完整流程：
    ///
    ///   TCP 连接房主 -> 发 JoinRequest -> 收 JoinAccepted（拿到 udp_port + session_token）
    ///   -> 用该端口做 UDP 握手 -> 进入房间
    ///
    /// 加入成功后：大厅消息走 TCP，游戏数据走 UDP。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientNetworkManager : MonoBehaviour
    {
        public static ClientNetworkManager Instance { get; private set; }

        [Header("默认值")]
        [Tooltip("主界面输入框为空时使用的地址，形如 192.168.1.3:10003")]
        [SerializeField] private string defaultServerAddress = "192.168.1.3:10003";
        [SerializeField] private string playerName = "Player";

        [Header("行为")]
        [Tooltip("UDP 握手失败时是否判定加入失败。射击游戏必须为 true。")]
        [SerializeField] private bool requireUdpHandshake = true;
        [SerializeField] private bool dontDestroyOnLoad = true;

        [Header("调试")]
        [SerializeField] private bool verboseLog = false;

        private TcpClientChannel _tcp;
        private UdpClientChannel _udp;

        private TaskCompletionSource<Envelope> _joinReply;
        private int _joinEpoch;
        private uint _nextInputTick;

        // ------------------------------------------------------------------
        // 状态
        // ------------------------------------------------------------------

        public bool IsJoining { get; private set; }
        public bool IsJoined { get; private set; }
        public uint PlayerId { get; private set; }
        public JoinAccepted RoomInfo { get; private set; }
        public string ServerAddress { get; private set; }

        public bool IsUdpReady { get { return _udp != null && _udp.IsHandshakeDone; } }

        public long UdpRttMs
        {
            get { return _udp != null ? _udp.RttMs : -1; }
        }

        public string PlayerName
        {
            get { return playerName; }
            set { playerName = value; }
        }

        public string DefaultServerAddress
        {
            get { return defaultServerAddress; }
            set { defaultServerAddress = value; }
        }

        // ------------------------------------------------------------------
        // 事件（全部在主线程触发）
        // ------------------------------------------------------------------

        /// <summary>成功进入房间（TCP + UDP 都就绪）。</summary>
        public event Action<JoinAccepted> Joined;
        /// <summary>加入失败，参数为可直接显示给玩家的原因。</summary>
        public event Action<string> JoinFailed;
        /// <summary>与房主断开（被踢、超时、房主关房、主动离开都走这里）。</summary>
        public event Action<string> Disconnected;
        /// <summary>UDP 通道断了（TCP 可能还在）。射击游戏此时应当暂停并提示玩家。</summary>
        public event Action<string> UdpLost;

        public event Action<PlayerInfo> RemotePlayerJoined;
        public event Action<uint, string> RemotePlayerLeft;
        public event Action<StartGame> GameStarting;
        public event Action<ChatMessage> ChatReceived;

        /// <summary>TCP 上未在内部处理的消息（交给上层业务）。</summary>
        public event Action<Envelope> EnvelopeReceived;
        /// <summary>UDP 数据报（Snapshot 等）。</summary>
        public event Action<UdpPacketType, byte[]> DatagramReceived;

        // ------------------------------------------------------------------

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            NetLog.Verbose = verboseLog;
            MainThreadDispatcher.EnsureExists();

            if (dontDestroyOnLoad) DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            CleanupChannels();
        }

        private void OnApplicationQuit()
        {
            CleanupChannels();
        }

        private void Update()
        {
            // UDP 的握手重发 / 心跳 / 超时判定需要主线程驱动
            if (_udp != null) _udp.Update();
        }

        // ------------------------------------------------------------------
        // 加入房间
        // ------------------------------------------------------------------

        /// <summary>
        /// 主界面“加入房间”按钮调这个。
        /// address 支持 "192.168.1.3"、"192.168.1.3:10003"。
        /// </summary>
        public void JoinRoom(string address, string name = null)
        {
            if (IsJoining)
            {
                NetLog.Warn("正在加入中，忽略重复请求");
                return;
            }

            if (IsJoined)
            {
                NetLog.Warn("已经在房间里了");
                return;
            }

            if (!string.IsNullOrEmpty(name)) playerName = name;

            if (string.IsNullOrEmpty(address)) address = defaultServerAddress;

            _joinEpoch++;
            JoinAsync(address, _joinEpoch); // 不 await，让按钮回调立刻返回
        }

        private async void JoinAsync(string address, int epoch)
        {
            IsJoining = true;

            try
            {
                string host;
                int port;
                if (!NetUtil.TryParseAddress(address, NetConst.DefaultTcpPort, out host, out port))
                {
                    Fail("地址格式不正确，正确写法如 192.168.1.3:10003");
                    return;
                }

                ServerAddress = NetUtil.FormatAddress(host, port);

                // ---------- 1) TCP 接入 ----------
                var tcp = new TcpClientChannel();
                tcp.Closed += OnTcpClosed;
                tcp.EnvelopeReceived += OnTcpEnvelope;
                _tcp = tcp;

                string connectError = null;
                bool connected = await tcp.ConnectAsync(host, port, NetConst.ConnectTimeoutMs, e => connectError = e);

                if (epoch != _joinEpoch) { CleanupChannels(); return; }

                if (!connected)
                {
                    CleanupChannels();
                    Fail(connectError ?? "无法连接房主");
                    return;
                }

                // ---------- 2) 发加入请求，等回执 ----------
                var replySource = new TaskCompletionSource<Envelope>();
                _joinReply = replySource;

                tcp.Send(new Envelope
                {
                    JoinRequest = new JoinRequest
                    {
                        PlayerName = playerName ?? string.Empty,
                        ClientVersion = Application.version ?? string.Empty,
                    }
                });

                Task completed = await Task.WhenAny(replySource.Task, Task.Delay(NetConst.ConnectTimeoutMs));

                if (epoch != _joinEpoch) { CleanupChannels(); return; }

                if (completed != replySource.Task)
                {
                    CleanupChannels();
                    Fail("房主没有回应加入请求（可能在忙，或消息被拦截）");
                    return;
                }

                _joinReply = null;
                Envelope reply = replySource.Task.Result;

                if (reply.BodyCase == Envelope.BodyOneofCase.JoinRejected)
                {
                    string reason = reply.JoinRejected != null && !string.IsNullOrEmpty(reply.JoinRejected.Message)
                        ? reply.JoinRejected.Message
                        : reply.JoinRejected.Reason.ToString();
                    CleanupChannels();
                    Fail("被房主拒绝：" + reason);
                    return;
                }

                if (reply.BodyCase != Envelope.BodyOneofCase.JoinAccepted)
                {
                    CleanupChannels();
                    Fail("房主返回了意外的响应: " + reply.BodyCase);
                    return;
                }

                JoinAccepted accepted = reply.JoinAccepted;
                RoomInfo = accepted;
                PlayerId = accepted.PlayerId;

                // ---------- 3) UDP 握手 ----------
                var udp = new UdpClientChannel();
                udp.DatagramReceived += OnUdpDatagram;
                udp.Closed += OnUdpClosed;
                _udp = udp;

                var handshakeSource = new TaskCompletionSource<bool>();
                udp.HandshakeCompleted += () => handshakeSource.TrySetResult(true);

                string udpError;
                if (!udp.Start(host, (int)accepted.UdpPort, accepted.PlayerId, accepted.SessionToken, out udpError))
                {
                    CleanupChannels();
                    Fail("建立 UDP 通道失败：" + udpError);
                    return;
                }

                Task hsDone = await Task.WhenAny(handshakeSource.Task, Task.Delay(8000));

                if (epoch != _joinEpoch) { CleanupChannels(); return; }

                if (hsDone != handshakeSource.Task)
                {
                    CleanupChannels();

                    if (requireUdpHandshake)
                    {
                        Fail("UDP 握手超时：房主 " + host + ":" + accepted.UdpPort +
                             " 的 UDP 端口没回应。请检查 Windows 防火墙是否放行了本程序的 UDP 入站/出站。");
                        return;
                    }

                    NetLog.Warn("UDP 握手未完成，按仅 TCP 模式继续");
                }

                // ---------- 4) 完成 ----------
                IsJoined = true;
                NetLog.Info("已加入房间 \"" + accepted.RoomName + "\"，玩家 ID = " + accepted.PlayerId);

                var handler = Joined;
                if (handler != null) handler(accepted);
            }
            catch (Exception e)
            {
                NetLog.Exception(e, "加入房间");
                CleanupChannels();
                Fail(ProtoCodec.HumanizeError(e));
            }
            finally
            {
                IsJoining = false;
            }
        }

        /// <summary>主动离开房间。</summary>
        public void LeaveRoom()
        {
            if (!IsJoined && !IsJoining && _tcp == null) return;

            if (_tcp != null && _tcp.IsConnected)
            {
                _tcp.Send(new Envelope { PlayerLeave = new PlayerLeave { Reason = "主动离开" } });
            }

            _joinEpoch++;
            CleanupChannels();
            IsJoined = false;
            NotifyDisconnected("已离开房间");
        }

        private void Fail(string reason)
        {
            NetLog.Warn("加入房间失败: " + reason);
            IsJoined = false;
            var handler = JoinFailed;
            if (handler != null) handler(reason);
        }

        // ------------------------------------------------------------------
        // TCP 事件
        // ------------------------------------------------------------------

        private void OnTcpEnvelope(Envelope envelope)
        {
            if (envelope == null) return;

            switch (envelope.BodyCase)
            {
                case Envelope.BodyOneofCase.JoinAccepted:
                case Envelope.BodyOneofCase.JoinRejected:
                {
                    TaskCompletionSource<Envelope> source = _joinReply;
                    _joinReply = null;
                    if (source != null) source.TrySetResult(envelope);
                    return;
                }

                case Envelope.BodyOneofCase.Ping:
                {
                    if (_tcp != null)
                    {
                        _tcp.Send(new Envelope
                        {
                            Pong = new Pong
                            {
                                SentTimeMs = envelope.Ping.SentTimeMs,
                                ServerTimeMs = (ulong)NetUtil.NowMs,
                            }
                        });
                    }
                    return;
                }

                case Envelope.BodyOneofCase.PlayerJoined:
                {
                    var handler = RemotePlayerJoined;
                    if (handler != null && envelope.PlayerJoined != null && envelope.PlayerJoined.Player != null)
                        handler(envelope.PlayerJoined.Player);
                    return;
                }

                case Envelope.BodyOneofCase.PlayerLeft:
                {
                    var handler = RemotePlayerLeft;
                    if (handler != null && envelope.PlayerLeft != null)
                        handler(envelope.PlayerLeft.PlayerId, envelope.PlayerLeft.Reason);
                    return;
                }

                case Envelope.BodyOneofCase.RoomState:
                {
                    if (RoomInfo != null && envelope.RoomState != null)
                    {
                        RoomInfo.Players.Clear();
                        RoomInfo.Players.AddRange(envelope.RoomState.Players);
                    }
                    return;
                }

                case Envelope.BodyOneofCase.StartGame:
                {
                    var handler = GameStarting;
                    if (handler != null) handler(envelope.StartGame);
                    return;
                }

                case Envelope.BodyOneofCase.ChatBroadcast:
                {
                    var handler = ChatReceived;
                    if (handler != null) handler(envelope.ChatBroadcast);
                    return;
                }

                case Envelope.BodyOneofCase.Kick:
                {
                    string reason = envelope.Kick != null && !string.IsNullOrEmpty(envelope.Kick.Reason)
                        ? envelope.Kick.Reason
                        : "被房主移出房间";
                    _joinEpoch++;
                    CleanupChannels();
                    IsJoined = false;
                    NotifyDisconnected(reason);
                    return;
                }
            }

            var gameHandler = EnvelopeReceived;
            if (gameHandler != null) gameHandler(envelope);
        }

        private void OnTcpClosed(string reason)
        {
            if (!IsJoined) return; // 加入阶段由 JoinAsync 自己处理

            _joinEpoch++;
            CleanupChannels();
            IsJoined = false;
            NotifyDisconnected(reason);
        }

        // ------------------------------------------------------------------
        // UDP 事件
        // ------------------------------------------------------------------

        private void OnUdpDatagram(UdpPacketType type, byte[] body)
        {
            var handler = DatagramReceived;
            if (handler != null) handler(type, body);
        }

        private void OnUdpClosed(string reason)
        {
            if (!IsJoined) return;

            // UDP 断了其实游戏已经没法继续，但 TCP 还在，交给上层决定是否退出
            NetLog.Warn("UDP 通道关闭: " + reason);
            var handler = UdpLost;
            if (handler != null) handler(reason ?? "UDP 通道已关闭");
        }

        // ------------------------------------------------------------------
        // 发送
        // ------------------------------------------------------------------

        public bool SendTcp(Google.Protobuf.IMessage message)
        {
            return _tcp != null && _tcp.Send(message);
        }

        /// <summary>发送一帧输入。tick 会自动递增。</summary>
        public bool SendInput(float moveX, float moveY, float yaw, float pitch, uint buttons)
        {
            if (_udp == null || !_udp.IsHandshakeDone) return false;

            var input = new UdpInput
            {
                Tick = ++_nextInputTick,
                MoveX = moveX,
                MoveY = moveY,
                LookYaw = yaw,
                LookPitch = pitch,
                Buttons = buttons,
                ClientTimeMs = (ulong)NetUtil.NowMs,
            };

            return _udp.SendInput(input);
        }

        /// <summary>需要自己指定 tick 时用这个（例如做输入预测回滚）。</summary>
        public bool SendInput(UdpInput input)
        {
            if (_udp == null || !_udp.IsHandshakeDone || input == null) return false;
            return _udp.SendInput(input);
        }

        public bool SendChat(string text)
        {
            if (_tcp == null || !_tcp.IsConnected || string.IsNullOrEmpty(text)) return false;
            return _tcp.Send(new Envelope { Chat = new ChatMessage { Text = text } });
        }

        public bool SetReady(bool ready)
        {
            if (_tcp == null || !_tcp.IsConnected) return false;
            return _tcp.Send(new Envelope { PlayerReady = new PlayerReady { Ready = ready } });
        }

        // ------------------------------------------------------------------

        private void NotifyDisconnected(string reason)
        {
            var handler = Disconnected;
            if (handler != null) handler(reason ?? "已断开");
        }

        private void CleanupChannels()
        {
            if (_udp != null)
            {
                _udp.DatagramReceived -= OnUdpDatagram;
                _udp.Closed -= OnUdpClosed;
                _udp.Dispose();
                _udp = null;
            }

            if (_tcp != null)
            {
                _tcp.EnvelopeReceived -= OnTcpEnvelope;
                _tcp.Closed -= OnTcpClosed;
                _tcp.Dispose();
                _tcp = null;
            }

            _joinReply = null;
            RoomInfo = null;
            PlayerId = 0;
            _nextInputTick = 0;
        }

        /// <summary>当前房间玩家列表（只读用途）。</summary>
        public IList<PlayerInfo> Players
        {
            get
            {
                if (RoomInfo == null) return new List<PlayerInfo>();
                return RoomInfo.Players;
            }
        }
    }
}
