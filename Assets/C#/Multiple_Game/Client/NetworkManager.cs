using System;
using System.Collections.Generic;
using UnityEngine;
using SocketGameProtocol;

namespace Multiple_Game
{
    /// <summary>
    /// 网络管理器：负责消息分发、玩家生成、状态同步。
    /// 挂在场景中的空 GameObject 上，依赖 Client_TCP 组件。
    /// </summary>
    public class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Instance { get; private set; }

      //"本地玩家信息"
        public int LocalPlayerId { get; private set; }
        public string LocalUsername { get; private set; }
        public bool IsHost { get; private set; }
        public bool IsInRoom { get; private set; }

        //"同步配置"
        [SerializeField] private float syncInterval = 0.05f; // 位置同步间隔（秒）
        private float syncTimer = 0f;

        // 本地玩家组件引用
        private playerMove localPlayerMove;
        private playerCamera localPlayerCamera;

        // 远端玩家字典：playerId -> RemotePlayerData
        private Dictionary<int, RemotePlayerData> remotePlayers = new Dictionary<int, RemotePlayerData>();

        // ==================== 生命周期 ====================

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

        private void Start()
        {
            // 订阅 Client_TCP 的消息事件
            if (Client_TCP.Instance != null)
            {
                Client_TCP.Instance.OnMessageReceived += HandleMessage;
                Client_TCP.Instance.OnDisconnected += OnDisconnected;
            }
        }

        private void Update()
        {
            if (!IsInRoom || LocalPlayerId == 0) return;

            // 定时发送位置和视角同步
            syncTimer += Time.deltaTime;
            if (syncTimer >= syncInterval)
            {
                syncTimer = 0f;
                SendSyncData();
            }
        }

        private void OnDestroy()
        {
            if (Client_TCP.Instance != null)
            {
                Client_TCP.Instance.OnMessageReceived -= HandleMessage;
                Client_TCP.Instance.OnDisconnected -= OnDisconnected;
            }

            if (Instance == this) Instance = null;
        }

        // ==================== 公共方法 ====================

        /// <summary>
        /// 发送加入房间请求。
        /// </summary>
        public void SendJoinRoom(string username, bool isHost)
        {
            LocalUsername = username;
            IsHost = isHost;

            var pack = new MainPack
            {
                requestcode = RequestCode.Room,
                actioncode = ActionCode.RoomJoin,
                returncode = ReturnCode.ReturnNone,
                roomJoinReqData = new RoomJoinRequestData
                {
                    username = username,
                    roomId = 1 // 简化：默认房间 ID 为 1
                }
            };

            Client_TCP.Instance.Send(pack);
            Debug.Log($"[NetworkManager] 发送加入房间请求：{username}, 主机={isHost}");
        }

        /// <summary>
        /// 发送离开房间请求。
        /// </summary>
        public void SendLeaveRoom()
        {
            if (LocalPlayerId == 0) return;

            var pack = new MainPack
            {
                requestcode = RequestCode.Room,
                actioncode = ActionCode.RoomLeave,
                returncode = ReturnCode.ReturnNone,
                roomLeaveData = new RoomLeaveData { playerId = LocalPlayerId }
            };

            Client_TCP.Instance.Send(pack);
            Debug.Log("[NetworkManager] 发送离开房间请求");

            CleanupRemotePlayers();
            IsInRoom = false;
            LocalPlayerId = 0;
        }

        /// <summary>
        /// 设置本地玩家组件引用（由 PlayerSpawner 在生成后调用）。
        /// </summary>
        public void SetLocalPlayerComponents(playerMove move, playerCamera camera)
        {
            localPlayerMove = move;
            localPlayerCamera = camera;

            // 标记为本地玩家
            move.SetLocalPlayer(true);
            camera.SetLocalPlayer(true);

            Debug.Log("[NetworkManager] 本地玩家组件已绑定");
        }

        // ==================== 消息处理 ====================

        private void HandleMessage(MainPack pack)
        {
            switch (pack.requestcode)
            {
                case RequestCode.Login:
                    HandleLoginResponse(pack);
                    break;

                case RequestCode.Room:
                    HandleRoomMessage(pack);
                    break;

                case RequestCode.PlayerSync:
                    HandleSyncMessage(pack);
                    break;
            }
        }

        private void HandleLoginResponse(MainPack pack)
        {
            if (pack.actioncode == ActionCode.LoginResponse && pack.loginResData != null)
            {
                Debug.Log($"[登录] {pack.loginResData.message}");
            }
        }

        private void HandleRoomMessage(MainPack pack)
        {
            switch (pack.actioncode)
            {
                case ActionCode.RoomJoinResponse:
                    OnRoomJoinResponse(pack.roomJoinResData);
                    break;

                case ActionCode.RoomPlayerList:
                    OnRoomPlayerList(pack.playerInfoList);
                    break;
            }
        }

        private void HandleSyncMessage(MainPack pack)
        {
            switch (pack.actioncode)
            {
                case ActionCode.SyncBroadcast:
                    OnSyncBroadcast(pack.syncBroadcastData);
                    break;

                case ActionCode.SyncPlayerSpawn:
                    OnPlayerSpawn(pack.syncSpawnData);
                    break;

                case ActionCode.SyncPlayerLeave:
                    OnPlayerLeave(pack.syncLeaveData);
                    break;
            }
        }

        // ==================== 房间事件回调 ====================

        private void OnRoomJoinResponse(RoomJoinResponseData resData)
        {
            if (resData == null) return;

            if (!resData.success)
            {
                Debug.LogError($"[房间] 加入失败：{resData.message}");
                return;
            }

            LocalPlayerId = resData.playerId;
            IsInRoom = true;

            Debug.Log($"[房间] 加入成功！我的 ID: {LocalPlayerId}");

            // 生成已有玩家（首次进入房间时服务器下发的列表）
            if (resData.playerList != null && resData.playerList.Count > 0)
            {
                foreach (var info in resData.playerList)
                {
                    if (info.playerId != LocalPlayerId) // 跳过自己
                    {
                        SpawnRemotePlayer(info);
                    }
                }
            }

            // 通知 UI 更新
            //ConnectionUI.Instance?.OnJoinRoomSuccess();
        }

        private void OnRoomPlayerList(List<PlayerInfoData> playerList)
        {
            if (playerList == null) return;

            foreach (var info in playerList)
            {
                if (info.playerId != LocalPlayerId && !remotePlayers.ContainsKey(info.playerId))
                {
                    SpawnRemotePlayer(info);
                }
            }
        }

        // ==================== 同步事件回调 ====================

        private void OnSyncBroadcast(SyncBroadcastData broadcastData)
        {
            if (broadcastData == null) return;

            // 处理位置同步
            if (broadcastData.positions != null)
            {
                foreach (var posData in broadcastData.positions)
                {
                    if (posData.playerId == LocalPlayerId) continue; // 忽略自己的数据

                    if (remotePlayers.TryGetValue(posData.playerId, out var remote))
                    {
                        remote.ApplyPositionData(posData);
                    }
                }
            }

            // 处理视角同步
            if (broadcastData.looks != null)
            {
                foreach (var lookData in broadcastData.looks)
                {
                    if (lookData.playerId == LocalPlayerId) continue;

                    if (remotePlayers.TryGetValue(lookData.playerId, out var remote))
                    {
                        remote.ApplyLookData(lookData);
                    }
                }
            }
        }

        private void OnPlayerSpawn(SyncPlayerSpawnData spawnData)
        {
            if (spawnData == null) return;
            if (spawnData.playerId == LocalPlayerId) return;

            var info = new PlayerInfoData
            {
                playerId = spawnData.playerId,
                username = spawnData.username,
                posX = spawnData.posX,
                posY = spawnData.posY,
                posZ = spawnData.posZ
            };

            SpawnRemotePlayer(info);
            Debug.Log($"[同步] 玩家 [{spawnData.username}](ID={spawnData.playerId}) 加入房间");
        }

        private void OnPlayerLeave(SyncPlayerLeaveData leaveData)
        {
            if (leaveData == null) return;

            DespawnRemotePlayer(leaveData.playerId);
            Debug.Log($"[同步] 玩家 ID={leaveData.playerId} 离开房间");
        }

        // ==================== 远端玩家管理 ====================

        private void SpawnRemotePlayer(PlayerInfoData info)
        {
            if (remotePlayers.ContainsKey(info.playerId))
            {
                Debug.LogWarning($"[生成] 远端玩家 ID={info.playerId} 已存在，跳过");
                return;
            }

            var remoteData = PlayerSpawner.Instance.SpawnRemotePlayer(info);
            if (remoteData != null)
            {
                remotePlayers[info.playerId] = remoteData;
                Debug.Log($"[生成] 远端玩家 [{info.username}](ID={info.playerId}) 已生成");
            }
        }

        private void DespawnRemotePlayer(int playerId)
        {
            if (remotePlayers.TryGetValue(playerId, out var remote))
            {
                PlayerSpawner.Instance.DespawnRemotePlayer(remote);
                remotePlayers.Remove(playerId);
                Debug.Log($"[销毁] 远端玩家 ID={playerId} 已销毁");
            }
        }

        private void CleanupRemotePlayers()
        {
            foreach (var remote in remotePlayers.Values)
            {
                PlayerSpawner.Instance.DespawnRemotePlayer(remote);
            }
            remotePlayers.Clear();
        }

        // ==================== 发送同步数据 ====================

        private void SendSyncData()
        {
            if (!IsInRoom || localPlayerMove == null || localPlayerCamera == null) return;

            // 发送位置同步
            var posPack = new MainPack
            {
                requestcode = RequestCode.PlayerSync,
                actioncode = ActionCode.SyncPosition,
                returncode = ReturnCode.ReturnNone,
                syncPosData = new SyncPositionData
                {
                    playerId = LocalPlayerId,
                    posX = localPlayerMove.transform.position.x,
                    posY = localPlayerMove.transform.position.y,
                    posZ = localPlayerMove.transform.position.z,
                    velX = localPlayerMove.GetComponent<Rigidbody>().velocity.x,
                    velY = localPlayerMove.GetComponent<Rigidbody>().velocity.y,
                    velZ = localPlayerMove.GetComponent<Rigidbody>().velocity.z,
                    yaw = localPlayerMove.transform.eulerAngles.y,
                    inputX = localPlayerMove.GetMoveInput().x,
                    inputY = localPlayerMove.GetMoveInput().y,
                    isRunning = localPlayerMove.GetIsRunning(),
                    isGrounded = localPlayerMove.GetIsGrounded(),
                    animTrigger = 0, // TODO: 根据动画状态设置
                    aim = localPlayerMove.Aim,
                    handGun = false // TODO: 如果需要
                }
            };

            Client_TCP.Instance.Send(posPack);

            // 发送视角同步
            var lookPack = new MainPack
            {
                requestcode = RequestCode.PlayerSync,
                actioncode = ActionCode.SyncLook,
                returncode = ReturnCode.ReturnNone,
                syncLookData = new SyncLookData
                {
                    playerId = LocalPlayerId,
                    yaw = localPlayerCamera.GetYaw(),
                    pitch = localPlayerCamera.GetPitch()
                }
            };

            Client_TCP.Instance.Send(lookPack);
        }

        // ==================== 断连处理 ====================

        private void OnDisconnected()
        {
            Debug.LogWarning("[NetworkManager] 与服务器断开连接");
            CleanupRemotePlayers();
            IsInRoom = false;
            LocalPlayerId = 0;

            //ConnectionUI.Instance?.OnDisconnected();
        }
    }

    /// <summary>
    /// 远端玩家数据封装类。
    /// </summary>
    public class RemotePlayerData
    {
        public int PlayerId { get; private set; }
        public string Username { get; private set; }
        public GameObject GameObject { get; private set; }
        public playerMove MoveComponent { get; private set; }
        public playerCamera CameraComponent { get; private set; }

        // 平滑插值参数
        private float positionLerpSpeed = 10f;
        private float rotationLerpSpeed = 15f;

        private Vector3 targetPosition;
        private float targetYaw;
        private float targetPitch;

        public RemotePlayerData(int playerId, string username, GameObject go, playerMove move, playerCamera camera)
        {
            PlayerId = playerId;
            Username = username;
            GameObject = go;
            MoveComponent = move;
            CameraComponent = camera;

            // 标记为非本地玩家
            move.SetLocalPlayer(false);
            camera.SetLocalPlayer(false);

            targetPosition = go.transform.position;
        }

        public void ApplyPositionData(SyncPositionData data)
        {
            targetPosition = new Vector3(data.posX, data.posY, data.posZ);
            targetYaw = data.yaw;

            // 更新动画参数
            MoveComponent.SetMoveInput(new Vector2(data.inputX, data.inputY));
            MoveComponent.SetIsRunning(data.isRunning);

            // 平滑插值位置
            GameObject.transform.position = Vector3.Lerp(
                GameObject.transform.position,
                targetPosition,
                Time.deltaTime * positionLerpSpeed
            );

            // 平滑插值旋转
            var currentEuler = GameObject.transform.eulerAngles;
            float newYaw = Mathf.LerpAngle(currentEuler.y, targetYaw, Time.deltaTime * rotationLerpSpeed);
            GameObject.transform.eulerAngles = new Vector3(currentEuler.x, newYaw, currentEuler.z);
        }

        public void ApplyLookData(SyncLookData data)
        {
            targetYaw = data.yaw;
            targetPitch = data.pitch;

            CameraComponent.SetRotation(targetYaw, targetPitch);
        }
    }
}
