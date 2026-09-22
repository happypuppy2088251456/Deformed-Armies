using System;
using System.Collections.Generic;
using NetGame.Protocol;
using UnityEngine;

namespace NetGame.Unity
{
    /// <summary>
    /// 客户端游戏表现层：把 Client 收到的主线程消息转成玩家 GameObject 的生成/销毁/插值，
    /// 并采集本地玩家状态上报给服务端。纯客户端职责：不监听、不转发、不做权威判定。
    /// </summary>
    public sealed class ClientGameController : MonoBehaviour
    {
        public static ClientGameController Instance { get; private set; }

        [Header("同步配置")]
        [SerializeField] private float syncInterval = 0.05f;
        [SerializeField] private float moveThreshold = 0.01f;
        [SerializeField] private float rotThreshold = 1f;

        [Header("本地玩家（可选）：留空则优先用场景中已有的 playerMove，再退回 PlayerSpawner 生成")]
        [SerializeField] private GameObject localPlayer;

        // 事件（主线程）
        public event Action<int, string> OnJoinedRoom; // (localPlayerId, username)
        public event Action<string> OnJoinFailed;
        public event Action OnGameStart;
        public event Action OnLeftRoom;                // 房间解散 / 断线，回到大厅

        public string LocalUsername { get; private set; }
        public int LocalPlayerId { get; private set; }
        public bool IsInRoom { get; private set; }

        private readonly Dictionary<int, RemotePlayer> _remotePlayers = new Dictionary<int, RemotePlayer>();

        private playerMove _localMove;
        private playerCamera _localCamera;
        private Rigidbody _localRb;

        private float _syncTimer;
        private Vector3 _lastSentPos = Vector3.one * float.MaxValue;
        private float _lastSentYaw = float.MaxValue;
        private float _lastSentPitch = float.MaxValue;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            if (ClientMonoBridge.Instance == null)
            {
                Debug.LogError("[ClientGameController] 场景缺少 ClientMonoBridge");
                return;
            }
            ClientMonoBridge.Instance.OnConnected += HandleConnected;
            ClientMonoBridge.Instance.OnDisconnected += HandleDisconnected;
            ClientMonoBridge.Instance.OnMessage += HandleMessage;
        }

        private void OnDestroy()
        {
            if (ClientMonoBridge.Instance != null)
            {
                ClientMonoBridge.Instance.OnConnected -= HandleConnected;
                ClientMonoBridge.Instance.OnDisconnected -= HandleDisconnected;
                ClientMonoBridge.Instance.OnMessage -= HandleMessage;
            }
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!IsInRoom) return;

            _syncTimer += Time.deltaTime;
            if (_syncTimer >= syncInterval)
            {
                _syncTimer = 0f;
                SendLocalState();
            }

            foreach (RemotePlayer r in _remotePlayers.Values) r.Tick(Time.deltaTime);
        }

        /// <summary>由 LobbyController 在发起连接前设置用户名。</summary>
        public void SetUsername(string username) => LocalUsername = username;

        /// <summary>房主请求开始游戏（服务端广播 GameStart）。</summary>
        public void SendGameStart()
        {
            if (ClientMonoBridge.Instance != null && ClientMonoBridge.Instance.IsConnected)
                ClientMonoBridge.Instance.Send(new NetMessage(MessageType.GameStart));
        }

        // ==================== 事件处理（主线程） ====================

        private void HandleConnected()
        {
            SendJoinRequest();
        }

        private void HandleDisconnected(string reason)
        {
            Debug.LogWarning("[ClientGameController] " + reason);
            bool wasInRoom = IsInRoom;
            LeaveRoom();
            if (wasInRoom) OnLeftRoom?.Invoke();
        }

        private void HandleMessage(NetMessage msg)
        {
            switch (msg.Type)
            {
                case MessageType.JoinResponse: HandleJoinResponse(msg); break;
                case MessageType.PlayerJoined: HandlePlayerJoined(msg); break;
                case MessageType.PlayerLeft: HandlePlayerLeft(msg); break;
                case MessageType.StateBroadcast: HandleStateBroadcast(msg); break;
                case MessageType.GameStart: HandleGameStart(); break;
                case MessageType.RoomDissolved: HandleRoomDissolved(); break;
            }
        }

        private void SendJoinRequest()
        {
            string username = LocalUsername;
            if (string.IsNullOrWhiteSpace(username))
                username = "Player_" + UnityEngine.Random.Range(1000, 9999);
            LocalUsername = username;

            ClientMonoBridge.Instance.Send(new NetMessage(MessageType.JoinRequest) { Username = username });
        }

        private void SendLocalState()
        {
            if (_localMove == null) return;

            Vector3 pos = _localMove.transform.position;
            float yaw = _localMove.transform.eulerAngles.y;
            float pitch = _localCamera != null ? _localCamera.GetPitch() : 0f;
            Vector2 input = _localMove.GetMoveInput();
            bool running = _localMove.GetIsRunning();
            bool grounded = _localMove.GetIsGrounded();
            Vector3 vel = _localRb != null ? _localRb.velocity : Vector3.zero;

            bool changed =
                (pos - _lastSentPos).sqrMagnitude > moveThreshold * moveThreshold ||
                Mathf.Abs(Mathf.DeltaAngle(yaw, _lastSentYaw)) > rotThreshold ||
                Mathf.Abs(pitch - _lastSentPitch) > rotThreshold;

            if (!changed) return;

            _lastSentPos = pos;
            _lastSentYaw = yaw;
            _lastSentPitch = pitch;

            var state = new PlayerState
            {
                PlayerId = LocalPlayerId,
                Position = new Vec3(pos.x, pos.y, pos.z),
                Yaw = yaw,
                Pitch = pitch,
                VelX = vel.x,
                VelY = vel.y,
                VelZ = vel.z,
                InputX = input.x,
                InputY = input.y,
                IsRunning = running,
                IsGrounded = grounded
            };

            ClientMonoBridge.Instance.Send(new NetMessage(MessageType.PlayerState) { State = state });
        }

        // ==================== 消息处理 ====================

        private void HandleJoinResponse(NetMessage msg)
        {
            if (!msg.Success)
            {
                Debug.LogError("[ClientGameController] 加入失败：" + msg.Error);
                OnJoinFailed?.Invoke(msg.Error);
                return;
            }

            LocalPlayerId = msg.PlayerId;
            IsInRoom = true;

            SpawnLocalPlayer(new Vector3(msg.SpawnPosition.X, msg.SpawnPosition.Y, msg.SpawnPosition.Z));

            if (msg.Players != null)
                foreach (PlayerInfo p in msg.Players)
                    SpawnRemote(p);

            Debug.Log($"[ClientGameController] 加入房间成功，我的 ID={LocalPlayerId}");
            OnJoinedRoom?.Invoke(LocalPlayerId, LocalUsername);
        }

        private void HandlePlayerJoined(NetMessage msg)
        {
            if (msg.JoinedPlayer != null) SpawnRemote(msg.JoinedPlayer);
        }

        private void HandlePlayerLeft(NetMessage msg)
        {
            DespawnRemote(msg.LeftPlayerId);
        }

        private void HandleStateBroadcast(NetMessage msg)
        {
            if (msg.States == null) return;
            foreach (PlayerState s in msg.States)
            {
                if (s.PlayerId == LocalPlayerId) continue;
                if (_remotePlayers.TryGetValue(s.PlayerId, out RemotePlayer r))
                    r.PushState(s);
            }
        }

        private void HandleGameStart()
        {
            Debug.Log("[ClientGameController] 收到开始游戏通知");
            OnGameStart?.Invoke();
        }

        private void HandleRoomDissolved()
        {
            Debug.Log("[ClientGameController] 房间已解散");
            LeaveRoom();
            OnLeftRoom?.Invoke();
        }

        // ==================== 玩家生成/销毁 ====================

        private void SpawnLocalPlayer(Vector3 spawnPos)
        {
            GameObject go = localPlayer;

            if (go == null && PlayerSpawner.Instance != null)
            {
                var (m, c) = PlayerSpawner.Instance.SpawnLocalPlayer(spawnPos);
                _localMove = m;
                _localCamera = c;
            }
            else
            {
                _localMove = go != null ? go.GetComponent<playerMove>() : null;
                _localCamera = go != null ? go.GetComponentInChildren<playerCamera>() : null;
            }

            // 兜底：场景中已放置本地玩家
            if (_localMove == null)
                _localMove = FindObjectOfType<playerMove>();
            if (_localCamera == null && _localMove != null)
                _localCamera = _localMove.GetComponentInChildren<playerCamera>();

            if (_localMove == null)
            {
                Debug.LogError("[ClientGameController] 未找到本地玩家 playerMove 组件");
                return;
            }

            _localMove.SetLocalPlayer(true);
            if (_localCamera != null) _localCamera.SetLocalPlayer(true);
            _localRb = _localMove.GetComponent<Rigidbody>();

            _lastSentPos = Vector3.one * float.MaxValue;
            _lastSentYaw = float.MaxValue;
            _lastSentPitch = float.MaxValue;
        }

        private void SpawnRemote(PlayerInfo info)
        {
            if (info.PlayerId == LocalPlayerId) return;
            if (_remotePlayers.ContainsKey(info.PlayerId)) return;
            if (PlayerSpawner.Instance == null)
            {
                Debug.LogError("[ClientGameController] 场景缺少 PlayerSpawner");
                return;
            }

            RemotePlayer remote = PlayerSpawner.Instance.SpawnRemotePlayer(info);
            if (remote != null)
                _remotePlayers[info.PlayerId] = remote;
        }

        private void DespawnRemote(int playerId)
        {
            if (_remotePlayers.TryGetValue(playerId, out RemotePlayer r))
            {
                PlayerSpawner.Instance?.Despawn(r);
                _remotePlayers.Remove(playerId);
            }
        }

        private void LeaveRoom()
        {
            IsInRoom = false;
            LocalPlayerId = 0;

            foreach (RemotePlayer r in _remotePlayers.Values)
                PlayerSpawner.Instance?.Despawn(r);
            _remotePlayers.Clear();

            _localMove = null;
            _localCamera = null;
            _localRb = null;
        }
    }
}
