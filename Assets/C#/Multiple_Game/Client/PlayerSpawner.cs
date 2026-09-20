using UnityEngine;
using SocketGameProtocol;

namespace Multiple_Game
{
    /// <summary>
    /// 玩家生成器：负责在场景中生成和销毁玩家预制体。
    /// 挂在场景中的空 GameObject 上。
    /// </summary>
    public class PlayerSpawner : MonoBehaviour
    {
        public static PlayerSpawner Instance { get; private set; }

        [Header("预制体引用")]
        [SerializeField] private GameObject playerPrefab; // 玩家预制体（包含 playerMove、playerCamera、Animator 等组件）

        [Header("生成配置")]
        [SerializeField] private Vector3 spawnOffset = new Vector3(2f, 0f, 0f); // 每个新玩家的生成偏移

        private int spawnCount = 0;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        /// <summary>
        /// 生成远端玩家预制体。
        /// </summary>
        public RemotePlayerData SpawnRemotePlayer(PlayerInfoData info)
        {
            if (playerPrefab == null)
            {
                Debug.LogError("[PlayerSpawner] 未设置玩家预制体！");
                return null;
            }

            // 计算生成位置（带偏移，避免重叠）
            Vector3 spawnPos = new Vector3(info.posX, info.posY, info.posZ) + spawnOffset * spawnCount;
            spawnCount++;

            // 实例化预制体
            GameObject playerGO = Instantiate(playerPrefab, spawnPos, Quaternion.identity);
            playerGO.name = $"RemotePlayer_{info.username}_{info.playerId}";

            // 获取组件
            var move = playerGO.GetComponent<playerMove>();
            var camera = playerGO.GetComponentInChildren<playerCamera>();

            if (move == null)
            {
                Debug.LogError($"[PlayerSpawner] 预制体缺少 playerMove 组件：{playerGO.name}");
                Destroy(playerGO);
                return null;
            }

            if (camera == null)
            {
                Debug.LogError($"[PlayerSpawner] 预制体缺少 playerCamera 组件：{playerGO.name}");
                Destroy(playerGO);
                return null;
            }

            // 创建远端玩家数据
            var remoteData = new RemotePlayerData(info.playerId, info.username, playerGO, move, camera);

            return remoteData;
        }

        /// <summary>
        /// 销毁远端玩家预制体。
        /// </summary>
        public void DespawnRemotePlayer(RemotePlayerData remote)
        {
            if (remote?.GameObject != null)
            {
                Destroy(remote.GameObject);
                spawnCount--;
            }
        }

        /// <summary>
        /// 生成本地玩家预制体（可选：如果本地玩家也需要动态生成）。
        /// 如果本地玩家已经存在于场景中，则不需要调用此方法。
        /// </summary>
        public (playerMove move, playerCamera camera) SpawnLocalPlayer(Vector3 spawnPos)
        {
            if (playerPrefab == null)
            {
                Debug.LogError("[PlayerSpawner] 未设置玩家预制体！");
                return (null, null);
            }

            GameObject playerGO = Instantiate(playerPrefab, spawnPos, Quaternion.identity);
            playerGO.name = "LocalPlayer";

            var move = playerGO.GetComponent<playerMove>();
            var camera = playerGO.GetComponentInChildren<playerCamera>();

            return (move, camera);
        }
    }
}
