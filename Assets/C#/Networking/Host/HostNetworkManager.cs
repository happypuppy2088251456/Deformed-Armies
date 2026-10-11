using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Google.Protobuf;
using Game.Protocol;
using UnityEngine;
using Ping = Game.Protocol.Ping;
namespace Game.Net.Host
{
    /// <summary>
    /// 房主（Host）网络管理器 —— 挂在主界面场景的一个常驻 GameObject 上。
    ///
    /// “创建房间”时它会在本机同时打开两个监听端口：
    ///   * TCP —— Client 首次接入（握手 / 大厅）
    ///   * UDP —— 进入游戏后的实时同步
    /// 两者使用同一个端口号，所以只需要告诉队友一个地址，例如 192.168.1.3:10003。
    ///
    /// 线程模型：所有网络回调都已在主线程上触发，可以放心调用 Unity API。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HostNetworkManager : MonoBehaviour
    {
        public static HostNetworkManager Instance { get; private set; }

        [Header("监听端口")]
        [Tooltip("TCP / UDP 共用起始端口。若被占用会自动 +1 重试（见 maxPortProbeAttempts）。")]
        [SerializeField] private int basePort = NetConst.DefaultTcpPort;

        [Tooltip("端口被占用时最多向后探测多少个端口。设为 1 表示不做回退。")]
        [SerializeField] private int maxPortProbeAttempts = 10;

        [Header("房间")]
        [SerializeField] private string roomName = "局域网房间";
        [SerializeField] private int maxPlayers = NetConst.DefaultMaxPlayers;
        [SerializeField] private int tickRate = NetConst.DefaultTickRate;
        [SerializeField] private uint mapSeed = 0;

        [Header("行为")]
        [Tooltip("只绑定本机探测到的那个局域网 IPv4。关闭时绑定 0.0.0.0（多网卡更稳）。")]
        [SerializeField] private bool bindToDetectedAddress = false;

        [Tooltip("切场景后保留房主服务。")]
        [SerializeField] private bool dontDestroyOnLoad = true;

        [Header("调试")]
        [SerializeField] private bool verboseLog = false;

        // ------------------------------------------------------------------
        // 运行时状态
        // ------------------------------------------------------------------

        private TcpHostServer _tcpServer;
        private UdpHostServer _udpServer;

        private readonly RoomState _room = new RoomState();
        private uint _nextPlayerId = 1;

        private long _lastHeartbeatMs;
        private long _lastTimeoutCheckMs;
        private long _lastTickMs;
        private long _tickAccumulatorMs;
        private bool _closing;

        /// <summary>当前是否已经开好房间。</summary>
        public bool IsHosting { get; private set; }

        /// <summary>房主对外公布的局域网 IPv4，例如 192.168.1.3。</summary>
        public string LocalIPv4 { get; private set; } = "127.0.0.1";

        /// <summary>
        /// 多网卡机器（有线 + 无线 + 虚拟机网卡）上自动挑错网卡时，
        /// 在 <see cref="CreateRoom(string,int,int,int,out string)"/> 之前手动指定这一个。
        /// 留空表示自动探测（推荐）。
        /// </summary>
        public string PreferredIPv4;

        /// <summary>TCP 实际监听端口。</summary>
        public int TcpPort { get { return _tcpServer != null && _tcpServer.LocalEndPoint != null ? _tcpServer.LocalEndPoint.Port : basePort; } }

        /// <summary>UDP 实际监听端口。</summary>
        public int UdpPort { get { return _udpServer != null ? _udpServer.Port : basePort; } }

        /// <summary>要发给队友的地址，形如 192.168.1.3:10003。</summary>
        public string HostAddress { get { return NetUtil.FormatAddress(LocalIPv4, TcpPort); } }

        public RoomState Room { get { return _room; } }
        public string RoomName { get { return _room.RoomName; } }
        public int TickRate { get { return tickRate; } }
        public uint MapSeed { get { return mapSeed; } }
        public uint CurrentTick { get; private set; }
        public string LastError { get; private set; }

        /// <summary>房主自己的会话（也是房间里的玩家之一）。</summary>
        public PlayerSession LocalPlayer { get; private set; }

        // ------------------------------------------------------------------
        // 事件（全部在主线程触发）
        // ------------------------------------------------------------------

        /// <summary>房间创建成功：(IPv4, TCP 端口, UDP 端口)。</summary>
        public event Action<string, int, int> RoomStarted;
        public event Action RoomStopped;

        public event Action<PlayerSession> PlayerJoined;
        public event Action<PlayerSession, string> PlayerLeft;

        /// <summary>某个客户端完成了 UDP 握手，可以开始收它的高频输入了。</summary>
        public event Action<PlayerSession> PlayerUdpReady;

        /// <summary>TCP 上收到未在内部处理的消息（由上层游戏逻辑处理）。</summary>
        public event Action<PlayerSession, Envelope> EnvelopeReceived;

        /// <summary>UDP 上收到该玩家的数据报（Input 等），负载已去掉 4 字节头。</summary>
        public event Action<PlayerSession, UdpPacketType, byte[]> DatagramReceived;

        /// <summary>20Hz 逻辑帧（或你设置的 tickRate），用于生成并广播快照。</summary>
        public event Action<uint> ServerTick;

        public event Action<Exception> NetworkError;

        // ------------------------------------------------------------------
        // 生命周期
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
            ShutdownImmediate();
        }

        private void OnApplicationQuit()
        {
            ShutdownImmediate();
        }

        // ------------------------------------------------------------------
        // 创建 / 关闭房间
        // ------------------------------------------------------------------

        /// <summary>
        /// 主界面“创建房间”按钮调这个。
        /// 成功返回 true，并把本机地址写入 <see cref="HostAddress"/>。
        /// </summary>
        public bool CreateRoom(out string error)
        {
            return CreateRoom(roomName, basePort, maxPlayers, tickRate, out error);
        }

        public bool CreateRoom(string name, int port, int playerLimit, int serverTickRate, out string error)
        {
            error = null;
            LastError = null;

            if (IsHosting)
            {
                error = "房间已经创建了";
                LastError = error;
                return false;
            }

            // 1) 找到本机局域网 IPv4（192.168.x.x / 10.x.x.x / 172.16-31.x.x）
            LocalIPv4 = string.IsNullOrEmpty(PreferredIPv4)
                ? NetUtil.GetLocalIPv4()
                : PreferredIPv4.Trim();

            IPAddress bindAddress = bindToDetectedAddress
                ? NetUtil.ResolveBindAddress(LocalIPv4)
                : IPAddress.Any;

            _room.RoomName = string.IsNullOrEmpty(name) ? "局域网房间" : name.Trim();
            _room.MaxPlayers = Mathf.Clamp(playerLimit, 2, 64);
            _room.GameStarted = false;
            tickRate = Mathf.Clamp(serverTickRate, 5, 60);
            roomName = _room.RoomName;

            // 2) 探测可用端口：TCP 和 UDP 必须同一个端口号，地址才对得上
            int attempts = Mathf.Max(1, maxPortProbeAttempts);
            int chosenPort = -1;
            string lastError = null;

            for (int i = 0; i < attempts; i++)
            {
                int candidate = port + i;
                if (candidate > 65535) break;

                var tcp = new TcpHostServer();
                string tcpError;
                if (!tcp.Start(bindAddress, candidate, out tcpError))
                {
                    lastError = tcpError;
                    tcp.Stop();
                    continue;
                }

                var udp = new UdpHostServer();
                string udpError;
                if (!udp.Start(bindAddress, candidate, out udpError))
                {
                    lastError = udpError;
                    tcp.Stop();
                    udp.Stop();
                    continue;
                }

                _tcpServer = tcp;
                _udpServer = udp;
                chosenPort = candidate;
                break;
            }

            if (chosenPort < 0)
            {
                error = lastError ?? "无法在本机开启监听端口";
                LastError = error;
                ShutdownImmediate();
                return false;
            }

            // 3) 挂事件
            _tcpServer.PeerConnected += OnPeerConnected;
            _tcpServer.PeerDisconnected += OnPeerDisconnected;
            _tcpServer.EnvelopeReceived += OnTcpEnvelope;
            _tcpServer.Error += OnServerError;

            _udpServer.DatagramReceived += OnUdpDatagram;
            _udpServer.Error += OnServerError;

            // 4) 房主自己也是玩家 1
            _nextPlayerId = 1;
            LocalPlayer = new PlayerSession
            {
                PlayerId = _nextPlayerId++,
                PlayerName = "房主",
                SessionToken = Guid.NewGuid().ToString("N"),
                IsHost = true,
                Ready = true,
                PingMs = 0,
            };
            _room.Add(LocalPlayer);

            long now = NetUtil.NowMs;
            _lastHeartbeatMs = now;
            _lastTimeoutCheckMs = now;
            _lastTickMs = now;
            _tickAccumulatorMs = 0;
            CurrentTick = 0;
            _closing = false;
            IsHosting = true;

            NetLog.Info("房间已创建: " + HostAddress + " (TCP + UDP 同端口 " + chosenPort + ")");
            if (chosenPort != port)
            {
                NetLog.Warn("端口 " + port + " 被占用，已改用 " + chosenPort);
            }

            var started = RoomStarted;
            if (started != null) started(LocalIPv4, TcpPort, UdpPort);

            return true;
        }

        /// <summary>关闭房间并通知所有客户端。延迟一小段时间保证 Kick 包能发出去。</summary>
        public void CloseRoom()
        {
            if (!IsHosting || _closing) return;
            _closing = true;
            IsHosting = false;

            var bye = new Envelope { Kick = new Kick { Reason = "房主关闭了房间" } };
            if (_tcpServer != null) _tcpServer.Broadcast(bye);
            if (_udpServer != null)
                _udpServer.SendMany(_room.UdpEndpoints(), UdpPacketType.Bye, new UdpBye { Reason = "房主关闭了房间" });

            if (isActiveAndEnabled) StartCoroutine(ShutdownRoutine());
            else ShutdownImmediate();
        }

        private IEnumerator ShutdownRoutine()
        {
            yield return new WaitForSecondsRealtime(0.25f);
            ShutdownImmediate();
            var stopped = RoomStopped;
            if (stopped != null) stopped();
        }

        private void ShutdownImmediate()
        {
            if (_tcpServer != null)
            {
                _tcpServer.PeerConnected -= OnPeerConnected;
                _tcpServer.PeerDisconnected -= OnPeerDisconnected;
                _tcpServer.EnvelopeReceived -= OnTcpEnvelope;
                _tcpServer.Error -= OnServerError;
                _tcpServer.Stop();
                _tcpServer = null;
            }

            if (_udpServer != null)
            {
                _udpServer.DatagramReceived -= OnUdpDatagram;
                _udpServer.Error -= OnServerError;
                _udpServer.Stop();
                _udpServer = null;
            }

            _room.Clear();
            _room.GameStarted = false;
            LocalPlayer = null;
            IsHosting = false;
            _closing = false;
        }

        // ------------------------------------------------------------------
        // 每帧：心跳 / 超时 / 逻辑帧
        // ------------------------------------------------------------------

        private void Update()
        {
            if (!IsHosting) return;

            long now = NetUtil.NowMs;

            // --- TCP 心跳（同时用于测 RTT） ---
            if (now - _lastHeartbeatMs >= (long)(NetConst.TcpHeartbeatSeconds * 1000f))
            {
                _lastHeartbeatMs = now;
                List<TcpPeer> peers = _room.TcpPeers();
                for (int i = 0; i < peers.Count; i++)
                {
                    TcpPeer peer = peers[i];
                    peer.LastPingSentMs = now;
                    peer.LastPingPayload = (uint)now;
                    _tcpServer.Send(peer, new Envelope { Ping = new Ping { SentTimeMs = (ulong)now } });
                }
            }

            // --- 掉线检测 ---
            if (now - _lastTimeoutCheckMs >= 1000)
            {
                _lastTimeoutCheckMs = now;
                long timeoutMs = (long)(NetConst.PeerTimeoutSeconds * 1000f);

                List<PlayerSession> snapshot = _room.Snapshot();
                for (int i = 0; i < snapshot.Count; i++)
                {
                    PlayerSession s = snapshot[i];
                    if (s.IsHost) continue;
                    if (s.Tcp == null) continue;

                    if (now - s.Tcp.LastRecvMs > timeoutMs)
                    {
                        NetLog.Warn("玩家 " + s.PlayerId + " (" + s.PlayerName + ") 心跳超时，移出房间");
                        RemovePlayer(s, "心跳超时");
                    }
                }
            }

            // --- 逻辑帧 ---
            long tickIntervalMs = (long)(1000f / Mathf.Max(1, tickRate));
            _tickAccumulatorMs += now - _lastTickMs;
            _lastTickMs = now;
            if (_tickAccumulatorMs < 0) _tickAccumulatorMs = 0; // 时钟异常保护

            int guard = 0;
            while (_tickAccumulatorMs >= tickIntervalMs && guard < 5)
            {
                _tickAccumulatorMs -= tickIntervalMs;
                CurrentTick++;
                guard++;

                var handler = ServerTick;
                if (handler != null) handler(CurrentTick);
            }

            // 落后太多就丢掉积压，避免追帧雪崩
            if (_tickAccumulatorMs > tickIntervalMs * 5) _tickAccumulatorMs = tickIntervalMs;
        }

        // ------------------------------------------------------------------
        // TCP：接入与消息
        // ------------------------------------------------------------------

        private void OnPeerConnected(TcpPeer peer)
        {
            // 连接建立后什么都不做，等它发 JoinRequest。
            NetLog.Info("等待握手: " + peer.RemoteAddress);
        }

        private void OnPeerDisconnected(TcpPeer peer)
        {
            PlayerSession session = _room.FindByTcpPeer(peer);
            if (session != null) RemovePlayer(session, "连接断开");
        }

        private void OnTcpEnvelope(TcpPeer peer, Envelope envelope)
        {
            if (envelope == null) return;

            switch (envelope.BodyCase)
            {
                case Envelope.BodyOneofCase.JoinRequest:
                    HandleJoinRequest(peer, envelope.JoinRequest);
                    break;

                case Envelope.BodyOneofCase.Ping:
                {
                    // 客户端也在测 RTT，原样回填它的时间戳
                    _tcpServer.Send(peer, new Envelope
                    {
                        Pong = new Pong
                        {
                            SentTimeMs = envelope.Ping.SentTimeMs,
                            ServerTimeMs = (ulong)NetUtil.NowMs,
                        }
                    });
                    break;
                }

                case Envelope.BodyOneofCase.Pong:
                {
                    PlayerSession s = _room.FindByTcpPeer(peer);
                    if (s != null)
                    {
                        long rtt = NetUtil.NowMs - (long)envelope.Pong.SentTimeMs;
                        if (rtt >= 0 && rtt < 10000) s.PingMs = rtt;
                    }
                    break;
                }

                case Envelope.BodyOneofCase.PlayerLeave:
                {
                    PlayerSession s = _room.FindByTcpPeer(peer);
                    if (s != null) RemovePlayer(s, "客户端离开", notify: false);
                    break;
                }

                case Envelope.BodyOneofCase.PlayerReady:
                {
                    PlayerSession s = _room.FindByTcpPeer(peer);
                    if (s != null)
                    {
                        s.Ready = envelope.PlayerReady.Ready;
                        BroadcastRoomStateExcept(s);
                        var readyHandler = PlayerReadyChanged;
                        if (readyHandler != null) readyHandler(s);
                    }
                    break;
                }

                case Envelope.BodyOneofCase.Chat:
                {
                    PlayerSession s = _room.FindByTcpPeer(peer);
                    if (s != null)
                    {
                        var broadcast = new Envelope
                        {
                            ChatBroadcast = new ChatMessage
                            {
                                Sender = s.PlayerName,
                                Text = envelope.Chat.Text ?? string.Empty,
                            }
                        };
                        _tcpServer.Broadcast(broadcast);
                    }
                    break;
                }

                default:
                {
                    // 其余业务消息交给上层
                    PlayerSession s = _room.FindByTcpPeer(peer);
                    if (s != null)
                    {
                        s.LastTcpRecvMs = NetUtil.NowMs;
                        var handler = EnvelopeReceived;
                        if (handler != null) handler(s, envelope);
                    }
                    break;
                }
            }
        }

        /// <summary>某个玩家的准备状态变化（额外提供一个更细的事件，方便 UI 直接订阅）。</summary>
        public event Action<PlayerSession> PlayerReadyChanged;

        private void HandleJoinRequest(TcpPeer peer, JoinRequest request)
        {
            if (peer.PlayerId != 0)
            {
                SendReject(peer, RejectReason.Unspecified, "该连接已经加入过房间");
                return;
            }

            if (_room.GameStarted)
            {
                SendReject(peer, RejectReason.GameAlreadyBegun, "游戏已经开始了");
                return;
            }

            if (_room.IsFull)
            {
                SendReject(peer, RejectReason.RoomFull, "房间已满（" + _room.MaxPlayers + " 人）");
                return;
            }

            uint playerId = _nextPlayerId++;

            var session = new PlayerSession
            {
                PlayerId = playerId,
                PlayerName = SanitizeName(request != null ? request.PlayerName : null, playerId),
                SessionToken = Guid.NewGuid().ToString("N"),
                IsHost = false,
                Ready = false,
                Tcp = peer,
            };

            peer.PlayerId = playerId;
            peer.SessionToken = session.SessionToken;

            if (!_room.Add(session))
            {
                SendReject(peer, RejectReason.Unspecified, "内部错误：玩家 ID 冲突");
                return;
            }

            // 回执：带上 UDP 端口和会话令牌，客户端据此开始 UDP 握手
            var accepted = new JoinAccepted
            {
                PlayerId = playerId,
                SessionToken = session.SessionToken,
                RoomName = _room.RoomName ?? string.Empty,
                UdpPort = (uint)UdpPort,
                TickRate = (uint)tickRate,
                MaxPlayers = (uint)_room.MaxPlayers,
                ServerTimeMs = (ulong)NetUtil.NowMs,
                MapSeed = mapSeed,
            };

            List<PlayerSession> players = _room.Snapshot();
            for (int i = 0; i < players.Count; i++) accepted.Players.Add(players[i].ToPlayerInfo());

            _tcpServer.Send(peer, new Envelope { JoinAccepted = accepted });

            // 通知其他人
            _tcpServer.Broadcast(new Envelope
            {
                PlayerJoined = new PlayerJoined { Player = session.ToPlayerInfo() }
            }, peer);

            NetLog.Info("玩家加入: #" + playerId + " " + session.PlayerName + " @ " + peer.RemoteAddress);

            var handler = PlayerJoined;
            if (handler != null) handler(session);
        }

        private void SendReject(TcpPeer peer, RejectReason reason, string message)
        {
            _tcpServer.Send(peer, new Envelope
            {
                JoinRejected = new JoinRejected { Reason = reason, Message = message ?? string.Empty }
            });

            // 给发送队列一点时间把拒绝包写出去，然后断开
            if (isActiveAndEnabled) StartCoroutine(DisconnectAfter(peer, 0.3f));
            else _tcpServer.Disconnect(peer, "拒绝加入");
        }

        private IEnumerator DisconnectAfter(TcpPeer peer, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            if (_tcpServer != null) _tcpServer.Disconnect(peer, "延迟断开");
        }

        // ------------------------------------------------------------------
        // UDP：握手与数据报
        // ------------------------------------------------------------------

        private void OnUdpDatagram(IPEndPoint from, UdpPacketType type, byte[] body)
        {
            switch (type)
            {
                case UdpPacketType.Hello:
                    HandleUdpHello(from, body);
                    break;

                case UdpPacketType.Ping:
                {
                    UdpPing ping;
                    if (ProtoCodec.TryParseUdpBody(body, 0, body.Length, out ping))
                    {
                        _udpServer.Send(from, UdpPacketType.Pong, new UdpPong
                        {
                            SentTimeMs = ping.SentTimeMs,
                            ServerTimeMs = (ulong)NetUtil.NowMs,
                        });
                    }
                    break;
                }

                case UdpPacketType.Bye:
                {
                    PlayerSession s = _room.FindByUdpEndPoint(from);
                    if (s != null) RemovePlayer(s, "客户端关闭了 UDP 通道");
                    break;
                }

                case UdpPacketType.Pong:
                {
                    PlayerSession s = _room.FindByUdpEndPoint(from);
                    if (s != null)
                    {
                        s.LastUdpRecvMs = NetUtil.NowMs;
                        UdpPong pong;
                        if (ProtoCodec.TryParseUdpBody(body, 0, body.Length, out pong))
                        {
                            long rtt = NetUtil.NowMs - (long)pong.SentTimeMs;
                            if (rtt >= 0 && rtt < 10000) s.PingMs = rtt;
                        }
                    }
                    break;
                }

                default:
                {
                    PlayerSession s = _room.FindByUdpEndPoint(from);
                    if (s == null) return; // 未知来源，丢弃

                    s.LastUdpRecvMs = NetUtil.NowMs;
                    var handler = DatagramReceived;
                    if (handler != null) handler(s, type, body);
                    break;
                }
            }
        }

        private void HandleUdpHello(IPEndPoint from, byte[] body)
        {
            UdpHello hello;
            if (!ProtoCodec.TryParseUdpBody(body, 0, body.Length, out hello)) return;

            PlayerSession s = _room.Get(hello.PlayerId);

            // 令牌校验：防止别人猜到玩家 ID 就插进房间
            if (s == null || s.IsHost || string.IsNullOrEmpty(s.SessionToken) ||
                !string.Equals(s.SessionToken, hello.SessionToken, StringComparison.Ordinal))
            {
                NetLog.Warn("UDP 握手失败，来源 " + from);
                _udpServer.Send(from, UdpPacketType.Bye, new UdpBye { Reason = "会话无效" });
                return;
            }

            bool firstBind = s.UdpEndPoint == null;

            // 客户端换网卡 / NAT 端口重映射时允许重新绑定同一个会话
            s.UdpEndPoint = from;
            s.LastUdpRecvMs = NetUtil.NowMs;

            _udpServer.Send(from, UdpPacketType.HelloAck, new UdpHelloAck
            {
                PlayerId = s.PlayerId,
                TickRate = (uint)tickRate,
                ServerTimeMs = (ulong)NetUtil.NowMs,
            });

            if (firstBind)
            {
                NetLog.Info("玩家 #" + s.PlayerId + " UDP 就绪 @ " + from);
                var handler = PlayerUdpReady;
                if (handler != null) handler(s);
            }
        }

        // ------------------------------------------------------------------
        // 主动移出玩家
        // ------------------------------------------------------------------

        public void KickPlayer(uint playerId, string reason)
        {
            PlayerSession s = _room.Get(playerId);
            if (s == null || s.IsHost) return;
            RemovePlayer(s, string.IsNullOrEmpty(reason) ? "被房主移出房间" : reason);
        }

        private void RemovePlayer(PlayerSession session, string reason, bool notify = true)
        {
            if (session == null) return;
            if (!_room.Remove(session.PlayerId)) return; // 已经被移除过

            if (session.Tcp != null)
            {
                if (notify && !session.Tcp.IsClosed)
                {
                    _tcpServer.Send(session.Tcp, new Envelope { Kick = new Kick { Reason = reason } });
                    if (isActiveAndEnabled) StartCoroutine(DisconnectAfter(session.Tcp, 0.2f));
                    else _tcpServer.Disconnect(session.Tcp, reason);
                }
                else
                {
                    _tcpServer.Disconnect(session.Tcp, reason);
                }
            }

            if (_tcpServer != null)
            {
                _tcpServer.Broadcast(new Envelope
                {
                    PlayerLeft = new PlayerLeft
                    {
                        PlayerId = session.PlayerId,
                        Reason = reason ?? string.Empty,
                    }
                });
            }

            NetLog.Info("玩家离开: #" + session.PlayerId + " " + session.PlayerName + " (" + reason + ")");

            var handler = PlayerLeft;
            if (handler != null) handler(session, reason);
        }

        // ------------------------------------------------------------------
        // 对外发送接口
        // ------------------------------------------------------------------

        /// <summary>TCP 发给指定玩家。房主自己（IsHost）返回 false。</summary>
        public bool SendTcp(uint playerId, IMessage message)
        {
            if (!IsHosting || _tcpServer == null || message == null) return false;

            PlayerSession s = _room.Get(playerId);
            if (s == null || s.Tcp == null) return false;

            return _tcpServer.Send(s.Tcp, message);
        }

        /// <summary>TCP 广播给所有远程客户端。</summary>
        public int BroadcastTcp(IMessage message)
        {
            if (!IsHosting || _tcpServer == null || message == null) return 0;
            return _tcpServer.Broadcast(message);
        }

        /// <summary>UDP 发给指定玩家（需要它已完成 UDP 握手）。</summary>
        public bool SendUdp(uint playerId, UdpPacketType type, IMessage message)
        {
            if (!IsHosting || _udpServer == null) return false;

            PlayerSession s = _room.Get(playerId);
            if (s == null || s.UdpEndPoint == null) return false;

            return _udpServer.Send(s.UdpEndPoint, type, message);
        }

        /// <summary>UDP 广播给所有已握手的客户端。UDP 会丢包，调用方自行决定重发/插值。</summary>
        public int BroadcastUdp(UdpPacketType type, IMessage message)
        {
            if (!IsHosting || _udpServer == null) return 0;
            return _udpServer.SendMany(_room.UdpEndpoints(), type, message);
        }

        /// <summary>把当前房间状态推给所有人（含房主自己，方便 UI 刷新）。</summary>
        public void BroadcastRoomState()
        {
            BroadcastRoomStateExcept(null);
        }

        private void BroadcastRoomStateExcept(PlayerSession except)
        {
            if (!IsHosting || _tcpServer == null) return;

            var envelope = new Envelope { RoomState = _room.ToRoomStateMsg() };
            List<PlayerSession> players = _room.Snapshot();

            for (int i = 0; i < players.Count; i++)
            {
                PlayerSession s = players[i];
                if (s.Tcp == null || ReferenceEquals(s, except)) continue;
                _tcpServer.Send(s.Tcp, envelope);
            }
        }

        /// <summary>开始游戏：锁定房间并广播 StartGame。</summary>
        public bool StartGame()
        {
            if (!IsHosting || _room.GameStarted) return false;

            _room.GameStarted = true;
            mapSeed = (uint)UnityEngine.Random.Range(1, int.MaxValue);

            var start = new Envelope
            {
                StartGame = new StartGame
                {
                    StartServerTimeMs = (ulong)NetUtil.NowMs,
                    MapSeed = mapSeed,
                }
            };

            // 重复发两次，降低 TCP 单包丢失风险（TCP 不丢，但客户端可能没订阅好）
            BroadcastTcp(start);

            NetLog.Info("游戏开始，mapSeed=" + mapSeed);
            return true;
        }

        // ------------------------------------------------------------------

        private void OnServerError(Exception e)
        {
            var handler = NetworkError;
            if (handler != null) handler(e);
        }

        private static string SanitizeName(string raw, uint playerId)
        {
            if (string.IsNullOrEmpty(raw)) return "Player" + playerId;

            string trimmed = raw.Trim();
            var sb = new StringBuilder(trimmed.Length);
            for (int i = 0; i < trimmed.Length && sb.Length < 16; i++)
            {
                char c = trimmed[i];
                if (char.IsControl(c)) continue;
                sb.Append(c);
            }

            string result = sb.ToString().Trim();
            return result.Length == 0 ? "Player" + playerId : result;
        }

        /// <summary>供 UI 显示的房间摘要。</summary>
        public string DescribeRoom()
        {
            int count = _room.Count;
            return string.Format("{0}  {1}人/{2}人  TCP+UDP:{3}",
                _room.RoomName, count, _room.MaxPlayers, TcpPort);
        }
    }
}
