using NetGame.Protocol;
using UnityEngine;

namespace NetGame.Unity
{
    /// <summary>
    /// 玩家生成器：在场景中生成/销毁本地与远端玩家预制体。
    /// 挂在场景中的空 GameObject 上。
    /// </summary>
    public sealed class PlayerSpawner : MonoBehaviour
    {
        public static PlayerSpawner Instance { get; private set; }

        [Header("玩家预制体（需含 playerMove、playerCamera、Rigidbody、Animator）")]
        [SerializeField] private GameObject playerPrefab;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        /// <summary>生成本地玩家。</summary>
        public (playerMove move, playerCamera cam) SpawnLocalPlayer(Vector3 pos)
        {
            if (playerPrefab == null)
            {
                Debug.LogError("[PlayerSpawner] 未设置玩家预制体！");
                return (null, null);
            }

            GameObject go = Instantiate(playerPrefab, pos, Quaternion.identity);
            go.name = "LocalPlayer";
            return Configure(go, true);
        }

        /// <summary>生成远端玩家。</summary>
        public RemotePlayer SpawnRemotePlayer(PlayerInfo info)
        {
            if (playerPrefab == null)
            {
                Debug.LogError("[PlayerSpawner] 未设置玩家预制体！");
                return null;
            }

            Vector3 pos = new Vector3(info.Position.X, info.Position.Y, info.Position.Z);
            GameObject go = Instantiate(playerPrefab, pos, Quaternion.identity);
            go.name = $"RemotePlayer_{info.Username}_{info.PlayerId}";

            var (move, cam) = Configure(go, false);
            if (move == null)
            {
                Destroy(go);
                return null;
            }
            return new RemotePlayer(info.PlayerId, info.Username, go, move, cam);
        }

        public void Despawn(RemotePlayer remote)
        {
            if (remote?.GameObject != null) Destroy(remote.GameObject);
        }

        public void DespawnLocal(GameObject local)
        {
            if (local != null) Destroy(local);
        }

        private (playerMove, playerCamera) Configure(GameObject go, bool isLocal)
        {
            playerMove move = go.GetComponent<playerMove>();
            playerCamera cam = go.GetComponentInChildren<playerCamera>();

            if (move == null)
            {
                Debug.LogError($"[PlayerSpawner] 预制体缺少 playerMove 组件：{go.name}");
                return (null, null);
            }

            move.SetLocalPlayer(isLocal);
            if (cam != null) cam.SetLocalPlayer(isLocal);

            if (!isLocal)
            {
                // 远端玩家由插值直接驱动 transform，物理体设为 Kinematic 避免双驱动打架
                Rigidbody rb = go.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;

                // 关闭远端玩家自带的相机与监听，避免重复渲染
                if (cam != null && cam.camera != null) cam.camera.enabled = false;
                AudioListener listener = go.GetComponentInChildren<AudioListener>();
                if (listener != null) listener.enabled = false;
            }

            return (move, cam);
        }
    }
}
