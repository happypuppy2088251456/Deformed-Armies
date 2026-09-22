using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetGame.Protocol;

namespace NetGame.ServerSide
{
    /// <summary>
    /// 服务端（纯 C# 逻辑层，不继承 MonoBehaviour，不引用 UnityEngine）。
    /// 职责：创建房间（监听端口）、Accept 新连接、维护权威状态、广播/转发、
    /// 心跳与断线判定、玩家上下线。
    ///
    /// 对外只暴露：CreateRoom(port)、Send(clientId, msg)、Broadcast(msg)、Shutdown()
    /// 以及 OnPlayerJoin / OnPlayerLeave / OnRoomDissolved / OnLog 事件。
    /// </summary>
    public sealed class Server
    {
        public event Action<int, PlayerInfo> OnPlayerJoin; // (playerId, info)
        public event Action<int> OnPlayerLeave;            // playerId
        public event Action OnRoomDissolved;
        public event Action<string> OnLog;

        private const int HeartbeatTimeoutSeconds = 15;
        private const int HeartbeatCheckIntervalMs = 1000;

        private readonly RoomManager _rooms = new RoomManager();
        private readonly object _lock = new object();
        private readonly Dictionary<int, ServerConnection> _connections = new Dictionary<int, ServerConnection>(); // by playerId
        private readonly HashSet<ServerConnection> _pending = new HashSet<ServerConnection>(); // 已 Accept 尚未入房

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private volatile bool _running;
        private int _nextPlayerId = 1;

        public int Port { get; private set; }
        public bool IsRunning => _running;
        public int PlayerCount => _rooms.Count;

        /// <summary>创建房间：绑定端口并开始监听、Accept 与心跳检测。</summary>
        public void CreateRoom(int port)
        {
            if (_running) throw new InvalidOperationException("服务端已在运行");

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Port = port;
            _running = true;
            _cts = new CancellationTokenSource();

            Log($"服务端已创建房间，监听端口 {port}");
            _ = AcceptLoopAsync(_cts.Token);
            _ = HeartbeatLoopAsync(_cts.Token);
        }

        /// <summary>关停服务端：广播“房间解散”后断开所有客户端。</summary>
        public void Shutdown()
        {
            if (!_running) return;
            _running = false;

            // 先尽力广播“房间解散”，再关闭连接，让客户端尽量收到明确提示
            Broadcast(new NetMessage(MessageType.RoomDissolved));

            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }

            List<ServerConnection> all;
            lock (_lock)
            {
                all = new List<ServerConnection>(_connections.Values);
                _connections.Clear();
                _pending.Clear();
            }
            foreach (var c in all) c.Close();

            _rooms.Clear();
            OnRoomDissolved?.Invoke();
            Log("服务端已关停，房间解散");
        }

        /// <summary>向指定玩家发送消息。</summary>
        public void Send(int playerId, NetMessage msg)
        {
            ServerConnection conn;
            lock (_lock)
            {
                if (!_connections.TryGetValue(playerId, out conn)) return;
            }
            _ = conn.SendAsync(msg);
        }

        /// <summary>向房间内所有已入房玩家广播，可排除某玩家。</summary>
        public void Broadcast(NetMessage msg, int exceptPlayerId = -1)
        {
            List<ServerConnection> targets;
            lock (_lock)
            {
                targets = new List<ServerConnection>(_connections.Count);
                foreach (var kv in _connections)
                    if (kv.Key != exceptPlayerId)
                        targets.Add(kv.Value);
            }
            foreach (var c in targets) _ = c.SendAsync(msg);
        }

        /// <summary>获取本机局域网 IPv4（用于房主向他人展示连接地址）。</summary>
        public static string GetLocalIPv4()
        {
            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface nic in nics)
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    IPInterfaceProperties props = nic.GetIPProperties();
                    foreach (UnicastIPAddressInformation ip in props.UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = ip.Address.ToString();
                        if (s.StartsWith("192.168.") || s.StartsWith("10.") || s.StartsWith("172."))
                            return s;
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        // ==================== 内部：Accept / 心跳 ====================

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (_running && !ct.IsCancellationRequested)
            {
                try
                {
                    TcpClient tcp = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    var conn = new ServerConnection(tcp);
                    lock (_lock) _pending.Add(conn);
                    conn.OnMessage += OnConnectionMessage;
                    conn.OnDisconnected += OnConnectionDisconnected;
                    Log($"新连接：{tcp.Client.RemoteEndPoint}");
                    _ = conn.ReceiveLoopAsync(ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception e) { Log($"Accept 异常：{e.Message}"); }
            }
        }

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (_running && !ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HeartbeatCheckIntervalMs, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException) { break; }

                DateTime cutoff = DateTime.UtcNow.AddSeconds(-HeartbeatTimeoutSeconds);
                List<ServerConnection> idle = new List<ServerConnection>();

                lock (_lock)
                {
                    foreach (var kv in _connections)
                        if (kv.Value.LastReceiveUtc < cutoff) idle.Add(kv.Value);
                    foreach (var c in _pending)
                        if (c.LastReceiveUtc < cutoff) idle.Add(c);
                }

                foreach (var c in idle)
                {
                    Log($"连接心跳超时，主动断开：{(c.Username ?? "未入房")}");
                    c.Close(); // 触发 OnConnectionDisconnected 清理
                }
            }
        }

        // ==================== 内部：消息路由 ====================

        private void OnConnectionMessage(ServerConnection conn, NetMessage msg)
        {
            try
            {
                switch (msg.Type)
                {
                    case MessageType.Ping:
                        _ = conn.SendAsync(new NetMessage(MessageType.Pong));
                        break;

                    case MessageType.JoinRequest:
                        HandleJoin(conn, msg);
                        break;

                    case MessageType.PlayerState:
                        HandlePlayerState(conn, msg);
                        break;

                    case MessageType.GameStart:
                        HandleGameStart(conn);
                        break;
                }
            }
            catch (Exception e)
            {
                Log($"处理消息异常：{e.Message}");
            }
        }

        private void HandleJoin(ServerConnection conn, NetMessage msg)
        {
            lock (_lock)
            {
                if (conn.PlayerId != 0)
                {
                    _ = conn.SendAsync(new NetMessage(MessageType.JoinResponse) { Success = false, Error = "该连接已在房间中" });
                    return;
                }
            }

            string username = msg.Username ?? string.Empty;
            int playerId = Interlocked.Increment(ref _nextPlayerId);
            Vec3 spawnPos = new Vec3((playerId - 1) * 2f, 0f, 0f);

            if (!_rooms.TryAddPlayer(playerId, username, spawnPos, out string failReason))
            {
                _ = conn.SendAsync(new NetMessage(MessageType.JoinResponse) { Success = false, Error = failReason });
                return;
            }

            conn.PlayerId = playerId;
            conn.Username = username;

            lock (_lock)
            {
                _pending.Remove(conn);
                _connections[playerId] = conn;
            }

            PlayerInfo info = _rooms.GetPlayer(playerId);
            List<PlayerInfo> existing = _rooms.GetPlayersExcept(playerId);

            // 1) 回复加入者
            _ = conn.SendAsync(new NetMessage(MessageType.JoinResponse)
            {
                Success = true,
                PlayerId = playerId,
                SpawnPosition = spawnPos,
                Players = existing
            });

            // 2) 通知房间内其他玩家有新玩家加入
            Broadcast(new NetMessage(MessageType.PlayerJoined) { JoinedPlayer = info }, playerId);

            OnPlayerJoin?.Invoke(playerId, info);
            Log($"玩家 [{username}] 加入，ID={playerId}，当前 {_rooms.Count} 人");
        }

        private void HandlePlayerState(ServerConnection conn, NetMessage msg)
        {
            if (conn.PlayerId == 0) return;

            // 权威：用连接上登记的 playerId 覆写，防止伪造
            PlayerState state = msg.State;
            state.PlayerId = conn.PlayerId;
            _rooms.UpdateState(state);

            Broadcast(new NetMessage(MessageType.StateBroadcast)
            {
                States = new List<PlayerState> { state }
            }, conn.PlayerId);
        }

        private void HandleGameStart(ServerConnection conn)
        {
            if (conn.PlayerId == 0) return;
            Broadcast(new NetMessage(MessageType.GameStart));
            Log($"玩家 {conn.PlayerId} 请求开始游戏，已广播");
        }

        private void OnConnectionDisconnected(ServerConnection conn)
        {
            if (!_running) return; // 关停中，统一由 Shutdown 处理

            lock (_lock) _pending.Remove(conn);

            if (conn.PlayerId == 0)
            {
                Log("连接断开（未入房）");
                return;
            }

            if (!_rooms.RemovePlayer(conn.PlayerId, out string username))
                return; // 已处理过

            lock (_lock) _connections.Remove(conn.PlayerId);

            Broadcast(new NetMessage(MessageType.PlayerLeft) { LeftPlayerId = conn.PlayerId });
            OnPlayerLeave?.Invoke(conn.PlayerId);
            Log($"玩家 [{username}] 离开，ID={conn.PlayerId}");
        }

        private void Log(string s) => OnLog?.Invoke(s);
    }
}
