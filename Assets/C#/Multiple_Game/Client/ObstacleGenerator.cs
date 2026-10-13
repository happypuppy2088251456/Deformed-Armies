using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Multiple_Game
{
    /// <summary>
    /// 确定性随机数生成器（xorshift32）。
    ///
    /// 为什么不用 UnityEngine.Random / System.Random：
    /// 1) UnityEngine.Random 是全局状态，会被游戏里其他随机调用干扰，且无法隔离复现；
    /// 2) System.Random 的算法在 .NET Framework 与 .NET Core 下不同，
    ///    将来服务端换成 .NET 6+ 时，同一颗种子会算出不同结果。
    ///
    /// xorshift32 只有整数位运算，任何平台任何版本结果都完全一致 —— 这是联机场景
    /// "同一颗种子 => 同一张地图" 的基础。
    /// </summary>
    public class DeterministicRandom
    {
        private uint state;

        public DeterministicRandom(int seed)
        {
            // 种子为 0 时 xorshift 会永远输出 0，这里强制替换掉
            state = seed == 0 ? 0x9E3779B9u : unchecked((uint)seed);
        }

        /// <summary>返回 [0, 1) 的浮点数。</summary>
        public float Value()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>返回 [min, max) 的浮点数。自动处理 min > max 的写法。</summary>
        public float Range(float min, float max)
        {
            if (min > max)
            {
                float t = min;
                min = max;
                max = t;
            }
            return min + (max - min) * Value();
        }

        /// <summary>返回 [min, max] 闭区间的整数（max 也取得到）。</summary>
        public int RangeInt(int min, int max)
        {
            if (min > max)
            {
                int t = min;
                min = max;
                max = t;
            }
            return min + (int)(Value() * (max - min + 1));
        }
    }

    /// <summary>
    /// 障碍物生成器：在一块平坦地面上随机撒 N 个障碍物。
    ///
    /// 设计要点：
    /// - 只用一颗种子驱动全部随机，保证联机时各客户端生成完全一致的布局；
    /// - 支持"禁放区"（玩家出生点周围）与额外的回避点；
    /// - 用拒绝采样 + 最大尝试次数，参数过紧时只会少生成，绝不会死循环卡死主线程；
    /// - 不依赖任何已经存在的障碍物资源：未指定预制体时自动退化为 Cube 原型。
    ///
    /// 尺寸要点（踩过的坑，改代码前先读）：
    /// 模型的最终大小 = 运行时量出来的真实包围盒 x 代码反算出的缩放倍率，
    /// 目标是 modelMaxSize（默认 2 米，指最长边）。
    /// 之所以不能"直接用模型自己的尺寸"：Blender 导出的 FBX 里带 Lcl Scaling 100，
    /// Unity 导入后会把这个 100 倍应用到实例上，导致 2 米的箱子渲染成 200 米。
    /// 现在无论 FBX 被放大多少倍，最终都会收敛到 modelMaxSize。
    ///
    /// 使用方式：挂到场景中任意空 GameObject 上，Play 时自动生成。
    /// </summary>
    public class ObstacleGenerator : MonoBehaviour
    {
        [Header("随机种子（联机时所有客户端必须填同一个值）")]
        [Tooltip("同一颗种子 => 完全相同的地图布局。改成别的值就会换一张图。")]
        [SerializeField] private int seed = 20260906;

        [Header("生成数量")]
        [SerializeField] private int spawnCount = 15;

        [Header("生成范围（水平面，单位：米）")]
        [Tooltip("地图中心。当前那块地面 Cube 的位置是 (3.82, 0.97, -0.45)，中心取其 XZ。")]
        [SerializeField] private Vector2 areaCenter = new Vector2(3.8f, -0.45f);

        [Tooltip("半边长。地面 scale 是 300，所以半边长最大 150；这里留出边距，避免障碍物被推到地图外沿。")]
        [SerializeField] private float areaHalfSize = 140f;

        [Header("禁放区（玩家出生点）")]
        [Tooltip("玩家出生点的 XZ 坐标。当前场景 Client260906 里玩家根物体的位置是 " +
                 "(3.368, 1.964, -0.332)，注意它不在原点上。")]
        [SerializeField] private Vector2 spawnSafeCenter = new Vector2(3.368f, -0.332f);

        [Tooltip("出生点周围的禁放半径。障碍物边缘与出生点的距离小于该值就不生成，避免玩家一出生就被卡住。")]
        [SerializeField] private float spawnSafeRadius = 10f;

        [Tooltip("额外禁放点（例如敌人、任务点）。留空即可。")]
        [SerializeField] private List<Vector3> avoidPoints = new List<Vector3>();

        [Tooltip("额外禁放点的回避半径。")]
        [SerializeField] private float avoidPointRadius = 5f;

        [Header("障碍物尺寸")]
        [Tooltip("回退 Cube 的底面边长范围（X/Z）。只在没有加载到任何模型时才生效。")]
        [SerializeField] private Vector2 sizeRange = new Vector2(1.5f, 4f);

        [Tooltip("回退 Cube 的高度范围（Y）。只在没有加载到任何模型时才生效。\n" +
                 "建议不要超过 3：障碍物太高时，玩家贴身站立会被向下的落地射线打到墙面，" +
                 "从而误判为站在地面上（表现为可以贴墙悬空跳）。")]
        [SerializeField] private Vector2 heightRange = new Vector2(1f, 3f);

        [Tooltip("模型的尺寸基准（米），指模型三个维度里最长的那条边在 Unity 里最终的长度。\n" +
                 "取值方式：实际边长 = 该值 x randomSizeScaleRange，所以默认就是 \u201c最长边约 2 米\u201d。\n" +
                 "重要：这个值由代码在运行时量出模型真实包围盒后反算缩放，所以它一定生效；\n" +
                 "不管 FBX 自身被放大了多少倍（本项目历史上出现过 100 倍），最终都会收敛到这个尺寸。")]
        [SerializeField] private float modelMaxSize = 2f;

        [Tooltip("模型尺寸的随机倍率范围。x=1 且 y=1 表示所有障碍物严格一样大；\n" +
                 "想有一点自然变化就填 (0.9, 1.1) 之类，最终边长 = modelMaxSize x 该倍率。")]
        [SerializeField] private Vector2 randomSizeScaleRange = new Vector2(1f, 1f);

        [Tooltip("模型是否按长宽高独立缩放（拉伸）。\n" +
                 "不勾选（默认）= 等比缩放，只有一个统一倍率，岩石/隔离墩不会被拉成奇形怪状，\n" +
                 "最长边等于目标尺寸，其余两边按模型原比例。\n" +
                 "勾选 = 把 X/Y/Z 分别拉到目标长宽高，外观会被拉变形。")]
        [SerializeField] private bool stretchModelToTargetSize = false;

        [Tooltip("勾选后，若模型在某个维度上比目标尺寸还大，就把它按比例压回目标尺寸；\n" +
                 "不勾选则只做等比缩放，允许略超。仅在未勾选 stretchModelToTargetSize 时有意义。")]
        [SerializeField] private bool clampModelToTargetSize = false;

        [Header("排布规则")]
        [Tooltip("障碍物之间的最小中心间距。")]
        [SerializeField] private float minSpacing = 6f;

        [Tooltip("是否随机 Y 轴旋转。只影响观感，碰撞体是盒体，旋转后依然是盒体。")]
        [SerializeField] private bool randomYaw = true;

        [Header("预制体")]
        [Tooltip("留空则自动用内置 Cube 临时替代，便于快速验证生成逻辑。\n" +
                 "正式使用请自己做一个 Cube 预制体拖进来：挂 BoxCollider，不要挂 Rigidbody。")]
        [SerializeField] private GameObject obstaclePrefab;

        [Header("障碍物模型（Blender 导出）")]
        [Tooltip("可选的模型预制体列表。填了就用它们，运行时随机挑选一个。\n" +
                 "留空时会自动从 Resources/Obstacles/ 按下面的名字加载。")]
        [SerializeField] private List<GameObject> obstacleModels = new List<GameObject>();

        [Tooltip("Resources 目录（Assets 下的相对路径）里存放障碍物模型的位置。")]
        [SerializeField] private string modelsResourceFolder = "Obstacles";

        [Tooltip("Resources 里要自动加载的模型名字（不带扩展名）。\n" +
                 "这些 FBX 由 Tools/Blender/build_obstacles.py 生成。")]
        [SerializeField] private List<string> modelNames = new List<string>
        {
            "Obstacle_Crate",
            "Obstacle_Barrier",
            "Obstacle_Rock",
        };

        [Tooltip("每个模型自带材质（Blender 里设的颜色）。勾上则不再覆盖成 fallbackCubeColor。")]
        [SerializeField] private bool keepModelOwnMaterial = true;

        [Tooltip("回退 Cube 使用的颜色。默认橙色，便于在灰白地面上快速找到它们。")]
        [SerializeField] private Color fallbackCubeColor = new Color(1f, 0.45f, 0.05f, 1f);

        [Tooltip("障碍物使用的材质。留空则用 fallbackCubeColor 在运行时现场造一个。\n" +
                 "注意：keepModelOwnMaterial 勾选时，只有回退 Cube 会用到它。")]
        [SerializeField] private Material obstacleMaterial;

        [Header("物理与层级")]
        [Tooltip("障碍物所在 Layer，默认 7(ground)。当前项目的碰撞矩阵是全开的，放哪层都能挡住玩家。")]
        [SerializeField] private int obstacleLayer = 7;

        [Tooltip("重叠检测时用来判断\"这个位置已经被占用\"的 Layer。\n" +
                 "默认 Nothing，表示不做物理重叠检测，只靠最小间距。\n" +
                 "如果启用，切记不要包含地面所在的 Layer 7，否则地面会挡住每一个候选点。")]
        [SerializeField] private LayerMask occupancyMask = 0;

        [Tooltip("向下探测地面高度时使用的 Layer。默认 7(ground)，也就是那块地板。")]
        [SerializeField] private LayerMask groundMask = 128;

        [Header("可见性保险（调试用）")]
        [Tooltip("额外生成几个\"保证看得见\"的障碍物：位置固定（正前 / 左前 / 右前），\n" +
                 "不受随机分布与禁放区影响。尺寸与随机生成的完全一致（由 FBX 决定）。\n" +
                 "设为 0 可关闭。")]
        [SerializeField] private int guaranteedNearbyCount = 3;

        [Header("运行控制")]
        [Tooltip("勾选后在 Start() 自动生成。")]
        [SerializeField] private bool generateOnStart = true;

        [Tooltip("生成出来的障碍物统一挂到这个父节点下，方便在 Hierarchy 里查看和清理。留空则散落在场景根节点。")]
        [SerializeField] private Transform parentRoot;

        // 外部只读查询接口
        public int LastSpawnedCount { get; private set; }
        public int LastAttemptCount { get; private set; }
        public IReadOnlyList<GameObject> SpawnedObstacles => spawned;

        private readonly List<GameObject> spawned = new List<GameObject>();
        private readonly List<Vector2> placedPoints = new List<Vector2>();

        // 回退 Cube 用的材质。必须自己持有引用，否则模板销毁后材质会一起消失，
        // 已实例化的立方体会变成洋红色（Missing Material）。
        private Material fallbackMaterial;

        // 本次生成使用的模板（真预制体或回退 Cube），仅在 GenerateObstacles 期间有效
        private GameObject _sourcePrefab;

        // 本次生成可用的模型列表（每个障碍物从这里随机挑一个）；为空则走回退 Cube
        private readonly List<GameObject> _activeModels = new List<GameObject>();

        // 每个模型需要的缩放倍率（与 _activeModels 一一对应），由 MeasureActiveModels() 算好。
        // 单位是"最终尺寸 ÷ 模型原始尺寸"，只在这两处用，不需要跨帧保留。
        private readonly List<Vector3> _modelScaleFactors = new List<Vector3>();

        // 每个模型在 localScale = 1 时的真实尺寸（米）。与 _activeModels 一一对应。
        private readonly List<Vector3> _modelBaseSizes = new List<Vector3>();

        // 每个模型"包围盒底部到物体原点"的距离（米，localScale = 1 时）。
        // 用来把模型底面精确放到地面上，不依赖模型原点恰好在下底面。
        private readonly List<float> _modelBaseY = new List<float>();

        // 每个模型在 localScale = 1 时碰撞盒的中心与尺寸（模型自身坐标系）。
        // 模型没有碰撞体时用它现场生成一个 BoxCollider——否则玩家会直接穿过去、也站不上去。
        private readonly List<Vector3> _modelColliderCenters = new List<Vector3>();
        private readonly List<Vector3> _modelColliderSizes = new List<Vector3>();

        // 本批模型缩放后的最大水平尺寸（米）。保险障碍物的安全距离必须参考它，
        // 否则 2 米的箱子出现在玩家眼前 0.2 米处，人一出生就被卡住。
        private float _maxFinalModelSize = 0f;


        // 探测地面高度时的起始高度。从足够高的位置往下打，避免起点落在地面体内部。
        private const float GroundProbeStartY = 100f;
        private const float GroundProbeDistance = 200f;

        private void Start()
        {
            if (!generateOnStart) return;

            GenerateObstacles();
        }

        /// <summary>
        /// 执行一次生成。重复调用会先清掉上一次生成的结果，保证结果可复现。
        /// </summary>
        /// <returns>本次实际生成的障碍物数量。</returns>
        public int GenerateObstacles()
        {
            ClearExisting();

            LastSpawnedCount = 0;
            LastAttemptCount = 0;

            if (spawnCount <= 0)
            {
                Debug.LogWarning("[ObstacleGenerator] spawnCount <= 0，没有东西可生成。");
                return 0;
            }

            var rng = new DeterministicRandom(seed);

            // 用上限尝试次数做兜底：参数过紧时宁可少生成，也不能让主线程卡死
            int maxAttempts = Mathf.Max(spawnCount * 30, 100);

            // 依次决定使用哪些模型：
            // ① 手动指定的预制体列表优先；
            // ② 否则从 Resources 按名字加载（Blender 导出的 FBX 放在 Resources/Obstacles/ 下）；
            // ③ 都没有才退回内置 Cube。
            _activeModels.Clear();
            if (obstacleModels != null)
            {
                for (int i = 0; i < obstacleModels.Count; i++)
                {
                    if (obstacleModels[i] != null) _activeModels.Add(obstacleModels[i]);
                }
            }

            if (_activeModels.Count == 0)
                LoadModelsFromResources();

            bool usingFallbackCube = _activeModels.Count == 0;

            if (usingFallbackCube)
            {
                _sourcePrefab = CreateFallbackCubePrefab();
                Debug.LogWarning("[ObstacleGenerator] 没有可用的障碍物模型（预制体列表为空，" +
                                 $"Resources/{modelsResourceFolder} 下也没加载到），已改用内置 Cube 临时替代。");
            }
            else
            {
                _sourcePrefab = _activeModels[0]; // 仅用于兼容旧字段，实际按 _activeModels 随机挑选

                // 量出每个模型的真实尺寸，并反算出"把它缩到目标尺寸"需要的倍率。
                // 这是修复"模型巨大"的关键：FBX 里带的 100 倍缩放会在这一步被抵消掉。
                MeasureActiveModels();
            }

            for (int i = 0; i < maxAttempts && spawned.Count < spawnCount; i++)
            {
                LastAttemptCount++;

                float halfExtentX = areaHalfSize;
                float halfExtentZ = areaHalfSize;

                // ① 在生成范围内取一个候选坐标（XZ 平面）
                float x = areaCenter.x + rng.Range(-halfExtentX, halfExtentX);
                float z = areaCenter.y + rng.Range(-halfExtentZ, halfExtentZ);

                // ② 这里的尺寸【只用于摆位计算】：出生点禁放区、最小间距、保险距离。
                //    模型的实际显示大小由 MeasureActiveModels() 算出的倍率决定（默认最长边 2 米）。
                float width = rng.Range(sizeRange.x, sizeRange.y);
                float depth = rng.Range(sizeRange.x, sizeRange.y);
                float height = rng.Range(heightRange.x, heightRange.y);

                // ③ 禁放区：玩家出生点
                if (spawnSafeRadius > 0f)
                {
                    float dxSpawn = x - spawnSafeCenter.x;
                    float dzSpawn = z - spawnSafeCenter.y;
                    float distToSpawn = Mathf.Sqrt(dxSpawn * dxSpawn + dzSpawn * dzSpawn);

                    // 加上障碍物自身半径，保证的是"边缘距离"而不是"中心距离"
                    if (distToSpawn < spawnSafeRadius + Mathf.Max(width, depth) * 0.5f)
                        continue;
                }

                // ④ 禁放区：额外回避点
                if (IsBlockedByAvoidPoints(x, z, width, depth))
                    continue;

                // ⑤ 与已放置障碍物的最小间距
                if (IsTooCloseToExisting(x, z))
                    continue;

                // ⑥ 探测地面高度，把障碍物底面正好放在地面上
                if (!TryGetGroundHeight(x, z, out float groundY))
                    continue; // 该位置没有地面，跳过（比如地图外沿）

                // ⑦ 可选：物理重叠检测
                if (IsOccupied(x, z, groundY, width, height, depth))
                    continue;

                // ⑧ 生成
                float yaw = randomYaw ? rng.Range(0f, 360f) : 0f;

                InstantiateObstacle(x, z, groundY, width, height, depth, yaw, "Obstacle");
            }

            // ⑨ 保证有几个"一定看得见"的大方块：紧贴出生点、方向固定
            SpawnGuaranteedNearby();

            if (usingFallbackCube)
            {
                // 用完就销毁掉那个只当模板用的原型（它在编辑模式下是失活的，不会显示出来）
                DestroySafe(_sourcePrefab);
            }
            _sourcePrefab = null;

            LastSpawnedCount = spawned.Count;

            if (LastSpawnedCount < spawnCount)
            {
                Debug.LogWarning(
                    $"[ObstacleGenerator] 目标 {spawnCount} 个，实际只生成了 {LastSpawnedCount} 个" +
                    $"（尝试 {LastAttemptCount} 次）。原因通常是 minSpacing 太大、生成范围太小或禁放区太大。");
            }
            else
            {
                Debug.Log($"[ObstacleGenerator] 生成完成：{LastSpawnedCount} 个障碍物，种子 {seed}，" +
                          $"尝试 {LastAttemptCount} 次。");
            }

            // 输出真实测量结果：到底生成在哪、多大、有没有可见实体、能不能站上去
            VerifyColliders();
            LogSpawnReport();

            return LastSpawnedCount;
        }

        /// <summary>
        /// 从 Resources/&lt;modelsResourceFolder&gt;/ 按名字加载障碍物模型。
        /// Blender 导出的 FBX 由 Tools/Blender/build_obstacles.py 生成并放在那里。
        /// </summary>
        private void LoadModelsFromResources()
        {
            if (modelNames == null || modelNames.Count == 0) return;

            string folder = string.IsNullOrEmpty(modelsResourceFolder) ? "" : modelsResourceFolder.Trim('/') + "/";

            for (int i = 0; i < modelNames.Count; i++)
            {
                string n = modelNames[i];
                if (string.IsNullOrEmpty(n)) continue;

                var prefab = Resources.Load<GameObject>(folder + n);
                if (prefab == null)
                {
                    Debug.LogWarning($"[ObstacleGenerator] Resources 里找不到模型 \"{folder}{n}\"，已跳过。");
                    continue;
                }

                _activeModels.Add(prefab);
            }
        }

        /// <summary>
        /// 在一个确定的 XZ 位置实例化一个障碍物，底面贴在 groundY 上。
        /// 所有生成路径都走这里，保证随机选型、缩放、材质、Layer、父节点处理完全一致。
        ///
        /// 关键设计：模型走 MeasureActiveModels() 算出来的缩放倍率，默认等比，
        /// 保证"最长边 = modelMaxSize"，同时不破坏岩石/隔离墩自己的比例。
        /// 这也是修复"物体非常巨大"的地方——以前这里对模型完全不缩放。
        /// </summary>
        private GameObject InstantiateObstacle(float x, float z, float groundY,
                                              float width, float height, float depth,
                                              float yawDegrees, string namePrefix, int modelIndex = -1)
        {
            // 优先用模型；没有模型时才用回退 Cube
            bool isFallbackCube = _activeModels.Count == 0;
            int idx = 0;
            GameObject template = null;

            if (!isFallbackCube)
            {
                idx = modelIndex >= 0
                    ? modelIndex % _activeModels.Count
                    : Mathf.Abs(seed + spawned.Count * 7919) % _activeModels.Count;
                template = _activeModels[idx];
            }
            else
            {
                template = _sourcePrefab;
            }

            if (template == null) return null;

            // 模型：底面不在原点时，用测到的"包围盒底到原点"距离把底面压到地面上；
            // 回退 Cube 的原点就在几何中心，所以抬高半个高度。
            float posY = groundY;
            if (isFallbackCube)
            {
                posY = groundY + height * 0.5f;
            }
            else if (idx < _modelBaseY.Count && idx < _modelScaleFactors.Count)
            {
                posY = groundY - _modelBaseY[idx] * _modelScaleFactors[idx].y;
            }

            Vector3 spawnPos = new Vector3(x, posY, z);
            Quaternion spawnRot = Quaternion.Euler(0f, yawDegrees, 0f);

            GameObject go = Instantiate(template, spawnPos, spawnRot);
            if (go == null) return null;

            // 名字里带上模型名、序号和种子，出问题时方便对照
            go.name = $"{namePrefix}_{spawned.Count:D2}_{template.name}_seed{seed}";

            // 缩放：
            // - 回退 Cube 没有自己的尺寸，直接用随机到的长宽高；
            // - 模型用 MeasureActiveModels() 反算出来的倍率，把 FBX 自带的倍数（历史上是 100x）
            //   抵消掉，最终最长边收敛到 modelMaxSize（默认 2 米）。
            if (isFallbackCube)
            {
                go.transform.localScale = new Vector3(width, height, depth);
            }
            else if (idx < _modelScaleFactors.Count)
            {
                go.transform.localScale = _modelScaleFactors[idx];
            }

            // 碰撞体：
            // FBX 导入设置里 addColliders = 0，Blender 出来的模型本身【没有】碰撞体，
            // 而只有"回退 Cube"路径才自带一个（GameObject.CreatePrimitive）。
            // 于是走模型路径时障碍物是纯装饰品：玩家能穿过去，也永远站不到上面。
            // 这里按量到的包围盒现场补一个 BoxCollider，尺寸写在 localScale = 1 的坐标系里，
            // 物体自身的缩放会自动带上它，跟上一步的缩放天然对齐。
            EnsureObstacleCollider(go, isFallbackCube ? -1 : idx);

            // 材质：模型默认保留 Blender 里设的颜色，避免把三种模型染成同一个色。
            var renderer = go.GetComponentInChildren<Renderer>();
            if (renderer == null)
            {
                Debug.LogWarning($"[ObstacleGenerator] {go.name} 上没有 Renderer，会看不到。");
            }
            else if (isFallbackCube || !keepModelOwnMaterial)
            {
                Material materialToUse = obstacleMaterial;
                if (materialToUse == null)
                    materialToUse = EnsureFallbackMaterial();

                if (materialToUse != null)
                {
                    // 模型可能有多个子渲染器，统一刷成同一个材质
                    var renderers = go.GetComponentsInChildren<Renderer>();
                    for (int i = 0; i < renderers.Length; i++)
                        renderers[i].sharedMaterial = materialToUse;
                }
            }

            if (obstacleLayer >= 0 && obstacleLayer <= 31)
                go.layer = obstacleLayer;

            // 统一挂到父节点下：留空时挂到自己身上，这样在 Hierarchy 里
            // 点开 ObstacleGenerator 就能看到全部障碍物，而不是散落在场景根部。
            Transform parent = parentRoot != null ? parentRoot : transform;
            go.transform.SetParent(parent, true);

            spawned.Add(go);
            placedPoints.Add(new Vector2(x, z));

            return go;
        }

        /// <summary>
        /// 保证障碍物一定有一个实体碰撞体，否则玩家会直接穿过去、也站不到上面。
        ///
        /// 背景：Obstacle_Crate/Barrier/Rock 这三个 FBX 的导入设置里 addColliders = 0，
        /// 模型本身不带碰撞体；而唯一自带碰撞体的是"回退 Cube"（CreatePrimitive 产物）。
        /// 之前代码假定"正式使用时会自己拖一个挂了 BoxCollider 的预制体进来"，
        /// 但场景里 obstaclePrefab 是空的，所以模型路径一个碰撞体都没有。
        ///
        /// 这里按量出来的包围盒补一个 BoxCollider，并放在模型所在 layer（默认 7 = ground），
        /// 让玩家的地面检测射线（groundLayer = 128）能打到它。
        /// 模型自己已经带了碰撞体时不动它，避免产生重复碰撞体。
        /// </summary>
        private void EnsureObstacleCollider(GameObject go, int modelIdx)
        {
            // 回退 Cube 自带 BoxCollider，模型可能自带 MeshCollider/BoxCollider，都不重复添加
            if (go.GetComponentInChildren<Collider>() != null) return;

            var collider = go.AddComponent<BoxCollider>();

            if (modelIdx >= 0 && modelIdx < _modelColliderSizes.Count &&
                modelIdx < _modelColliderCenters.Count)
            {
                // 尺寸与中心都取自 localScale = 1 时的包围盒，物体自身缩放会自动应用，
                // 所以这里不需要再乘 _modelScaleFactors——乘了反而会变成双重缩放。
                collider.size = _modelColliderSizes[modelIdx];
                collider.center = _modelColliderCenters[modelIdx];
            }
            else
            {
                // 量不到尺寸时的兜底：给一个 2 米的立方体，至少能站人
                collider.size = new Vector3(2f, 2f, 2f);
                collider.center = new Vector3(0f, 1f, 0f);
            }

            // 有些模型的包围盒中心不在原点上（上面的 collider.center 已经跟着挪了），
            // 这里只保证它是实体碰撞体而不是触发区，否则玩家还是会穿过去
            collider.isTrigger = false;
        }

        /// <summary>
        /// 校验每个生成的障碍物都有实体（非 Trigger）碰撞体，并打印数量。
        /// 这是"到底能不能站上去"的直接证据：没有碰撞体就一定会穿过去。
        /// </summary>
        private void VerifyColliders()
        {
            if (spawned.Count == 0) return;

            int withSolid = 0;
            int withTriggerOnly = 0;
            int missing = 0;

            for (int i = 0; i < spawned.Count; i++)
            {
                GameObject go = spawned[i];
                if (go == null) continue;

                var colliders = go.GetComponentsInChildren<Collider>();
                bool solid = false;
                bool any = false;
                for (int c = 0; c < colliders.Length; c++)
                {
                    any = true;
                    if (!colliders[c].isTrigger) solid = true;
                }

                if (solid) withSolid++;
                else if (any) withTriggerOnly++;
                else missing++;
            }

            if (missing > 0 || withTriggerOnly > 0)
            {
                Debug.LogWarning($"[ObstacleGenerator] 碰撞体校验：实体碰撞体 {withSolid} 个，" +
                                 $"只有 Trigger {withTriggerOnly} 个，完全没有 {missing} 个。" +
                                 "没有实体碰撞体的障碍物会被玩家穿过，也站不上去。");
            }
            else
            {
                Debug.Log($"[ObstacleGenerator] 碰撞体校验：{withSolid} 个障碍物全部带实体碰撞体，" +
                          "玩家可以站上去（它们的 layer 与玩家 groundLayer 一致）。");
            }
        }

        /// <summary>
        /// 量出每个模型的真实尺寸，并算出"把它缩到目标尺寸"需要的缩放倍率。
        ///
        /// 为什么必须有这一步（本 bug 的根因）：
        /// Blender 导出的 FBX 里带 Lcl Scaling = 100，Unity 的 FBX 导入器会把这个
        /// 100 倍应用到实例上；而 build_obstacles.py 里又把 MODEL_SCALE=165 烘进了顶点。
        /// 结果：代码注释里 2 米的木箱，实际渲染出来是 200 米。之前的代码写的是
        /// "完全使用模型自己的尺寸，代码不做任何缩放"，所以模型多大就多大。
        ///
        /// 现在的做法：量一遍真实包围盒，反算倍率，把最终尺寸收敛到 modelMaxSize。
        /// 这样即使以后重新导出 FBX、倍率再变，生成出来的障碍物依旧是 2 米左右。
        /// </summary>
        private void MeasureActiveModels()
        {
            _modelScaleFactors.Clear();
            _modelBaseSizes.Clear();
            _modelBaseY.Clear();
            _modelColliderCenters.Clear();
            _modelColliderSizes.Clear();
            _maxFinalModelSize = 0f;

            // 目标尺寸的随机倍率；填反了也能正常工作
            float sMin = Mathf.Min(randomSizeScaleRange.x, randomSizeScaleRange.y);
            float sMax = Mathf.Max(randomSizeScaleRange.x, randomSizeScaleRange.y);

            // 本批统一用一个随机倍率，保证同一张地图里所有障碍物尺寸一致，
            // 同时它又受 seed 控制，联机各客户端算出来完全相同。
            var sizeRng = new DeterministicRandom(seed ^ 0x51ED270B);
            float sizeScale = sizeRng.Range(sMin, sMax);
            float targetMax = Mathf.Max(0.01f, modelMaxSize * Mathf.Max(0.01f, sizeScale));

            for (int i = 0; i < _activeModels.Count; i++)
            {
                GameObject model = _activeModels[i];
                Vector3 baseSize = Vector3.one;
                float baseY = 0f;
                Vector3 colliderCenter = Vector3.zero;

                GameObject probe = Instantiate(model, Vector3.zero, Quaternion.identity);
                if (probe != null)
                {
                    // 先清掉物体自身可能带的缩放，量到的才是"localScale = 1 时的尺寸"，
                    // 后面乘倍率时才不会重复计算。
                    // 注意：这一步必须在量包围盒【之前】做，否则量到的是被放大 100 倍的结果，
                    // 而且外面再乘倍率就会叠乘。
                    probe.transform.localScale = Vector3.one;

                    var renderers = probe.GetComponentsInChildren<Renderer>();
                    if (renderers != null && renderers.Length > 0)
                    {
                        Bounds b = renderers[0].bounds;
                        for (int r = 1; r < renderers.Length; r++) b.Encapsulate(renderers[r].bounds);

                        if (b.size.x > 0f && b.size.y > 0f && b.size.z > 0f)
                        {
                            baseSize = b.size;
                            baseY = b.min.y;
                            colliderCenter = b.center;
                        }
                    }

                    DestroySafe(probe);
                }
                else
                {
                    Debug.LogWarning($"[ObstacleGenerator] 模型 {model.name} 无法实例化，尺寸测量已跳过（按 1 米处理）。");
                }

                // 反算倍率：在 1x 尺寸上直接除以目标值，因为物体自身的缩放已经被重置成 1
                Vector3 factor;
                if (stretchModelToTargetSize)
                {
                    // 拉伸模式：把 X/Y/Z 分别拉到目标长宽高，外观会变形
                    float targetW = Mathf.Max(0.01f, sizeRange.x + (sizeRange.y - sizeRange.x) * 0.5f);
                    float targetH = Mathf.Max(0.01f, heightRange.x + (heightRange.y - heightRange.x) * 0.5f);
                    factor = new Vector3(targetW / baseSize.x, targetH / baseSize.y, targetW / baseSize.z);
                }
                else
                {
                    // 等比模式（默认）：只有一个统一倍率，让最长边 == targetMax。
                    // 岩石、隔离墩不会被拉成奇形怪状。
                    float longest = Mathf.Max(baseSize.x, Mathf.Max(baseSize.y, baseSize.z));
                    float k = targetMax / Mathf.Max(0.0001f, longest);

                    if (clampModelToTargetSize)
                    {
                        // 保险：某个维度超了就按比例压回来，保证没有任何一边超过目标
                        k = Mathf.Min(k, targetMax / Mathf.Max(0.0001f, baseSize.x));
                        k = Mathf.Min(k, targetMax / Mathf.Max(0.0001f, baseSize.y));
                        k = Mathf.Min(k, targetMax / Mathf.Max(0.0001f, baseSize.z));
                    }

                    factor = new Vector3(k, k, k);
                }

                _modelScaleFactors.Add(factor);
                _modelBaseSizes.Add(baseSize);
                _modelBaseY.Add(baseY);
                _modelColliderCenters.Add(colliderCenter);
                _modelColliderSizes.Add(baseSize);

                if (baseSize.x > _maxFinalModelSize) _maxFinalModelSize = baseSize.x * factor.x;
                if (baseSize.z > _maxFinalModelSize) _maxFinalModelSize = baseSize.z * factor.z;

                Debug.Log($"[ObstacleGenerator] 模型 {model.name}：" +
                          $"FBX 原始尺寸 {baseSize.x:F2} x {baseSize.y:F2} x {baseSize.z:F2} 米 " +
                          $"-> 缩放倍率 {factor.x:F5} -> 最终 {baseSize.x * factor.x:F2} x " +
                          $"{baseSize.y * factor.y:F2} x {baseSize.z * factor.z:F2} 米");
            }

            Debug.Log($"[ObstacleGenerator] 已加载 {_activeModels.Count} 个障碍物模型：" +
                      string.Join("、", _activeModels.ConvertAll(m => m.name)) +
                      $"。目标最长边 {modelMaxSize:F2} 米（本次倍率 {sizeScale:F2}），" +
                      $"缩放后最大水平尺寸 {_maxFinalModelSize:F2} 米。" +
                      (stretchModelToTargetSize ? " 模式：拉伸。" : " 模式：等比缩放（保持模型原比例）。"));
        }

        /// <summary>
        /// 生成几个"保证看得见"的障碍物：位置固定（正前 / 左前 / 右前），
        /// 不受随机分布与禁放区影响。尺寸与随机生成的完全一致（同样收敛到 modelMaxSize）。
        /// </summary>
        private void SpawnGuaranteedNearby()
        {
            if (guaranteedNearbyCount <= 0) return;
            if (_activeModels.Count == 0 && _sourcePrefab == null) return;

            // 用固定种子，保证联机时各客户端位置一致
            var rng = new DeterministicRandom(seed ^ 0x5BF03635);
            const float baseYaw = 0f; // 保险方块朝向由夹角决定，不额外随机

            // 相对出生点的"正前方"方向。出生点朝向在场景里是 180.6°，
            // 正前方 ≈ -Z 方向，也就是 (0, -1)。
            Vector2 forward = new Vector2(0f, -1f);

            for (int i = 0; i < guaranteedNearbyCount; i++)
            {
                // 第 0 个正前方，其余左右交替铺开（约 ±50°）
                float angleDeg = i == 0 ? 0f : (i % 2 == 1 ? -50f : 50f);

                // 距离取决于模型缩放后的真实尺寸：太大的模型放在出生点旁边会把玩家整个罩住。
                // _maxFinalModelSize 是本批模型缩放后的最大水平尺寸（默认 2 米左右）。
                float clearance = Mathf.Max(_maxFinalModelSize, 10f);
                float distance = clearance * (1.1f + (i / 2) * 0.8f);

                float rad = angleDeg * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad);
                float sin = Mathf.Sin(rad);

                // 把 forward 旋转 angleDeg
                float dirX = forward.x * cos - forward.y * sin;
                float dirZ = forward.x * sin + forward.y * cos;

                float x = spawnSafeCenter.x + dirX * distance;
                float z = spawnSafeCenter.y + dirZ * distance;

                if (!TryGetGroundHeight(x, z, out float groundY))
                {
                    // 万一该点没有地面，就退回用出生点所在地面高度
                    if (!TryGetGroundHeight(spawnSafeCenter.x, spawnSafeCenter.y, out groundY))
                        groundY = 0f;

                    Debug.LogWarning($"[ObstacleGenerator] 保险障碍物 {i} 在 ({x:F1}, {z:F1}) 处探不到地面，已改用出生点高度。");
                }

                float yaw = baseYaw + rng.Range(0f, 90f);

                // 保险障碍物固定各用一种模型，方便一次性看到全部造型。
                // 尺寸传 0：模型尺寸由 FBX 决定，这里不需要目标尺寸。
                GameObject placed = InstantiateObstacle(x, z, groundY, 0f, 0f, 0f,
                                                        yaw, "Guaranteed", modelIndex: i);

                string modelName = placed != null ? placed.name : "(失败)";
                Debug.Log($"[ObstacleGenerator] 保险障碍物 {i}：{modelName}  位置 ({x:F1}, {groundY:F1}, {z:F1})  " +
                          $"距出生点 {distance:F1} 米  偏离正前方 {angleDeg:F0}°");
            }
        }

        /// <summary>
        /// 把生成结果量化打印到 Console：数量、包围盒、实际尺寸、离本物体的距离、是否有可见实体。
        /// 这是用来回答"到底生成出来了没有 / 多大 / 在哪"的直接证据。
        /// </summary>
        private void LogSpawnReport()
        {
            if (spawned.Count == 0)
            {
                Debug.LogWarning("[ObstacleGenerator] 报告：一个障碍物都没有生成。");
                return;
            }

            // 用渲染器包围盒统计真实的世界尺寸（这比 localScale 更可信）
            bool hasBounds = false;
            Bounds total = new Bounds();
            int withRenderer = 0;
            int withCollider = 0;
            int withMaterial = 0;
            float nearest = float.MaxValue;
            string nearestName = "(无)";
            Vector3 nearestPos = Vector3.zero;

            for (int i = 0; i < spawned.Count; i++)
            {
                GameObject go = spawned[i];
                if (go == null) continue;

                // 碰撞体可能在子物体上，所以整体查一遍
                var cols = go.GetComponentsInChildren<Collider>();
                if (cols != null && cols.Length > 0) withCollider++;

                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    withRenderer++;
                    if (renderer.sharedMaterial != null) withMaterial++;

                    if (!hasBounds)
                    {
                        total = renderer.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        total.Encapsulate(renderer.bounds);
                    }
                }

                float d = Vector3.Distance(transform.position, go.transform.position);
                if (d < nearest)
                {
                    nearest = d;
                    nearestName = go.name;
                    nearestPos = go.transform.position;
                }
            }

            Debug.Log(
                $"[ObstacleGenerator] 报告：数量={spawned.Count}，" +
                $"有渲染器={withRenderer}，有碰撞体={withCollider}，有材质={withMaterial}\n" +
                $"  整体包围盒 中心=({total.center.x:F1}, {total.center.y:F1}, {total.center.z:F1}) " +
                $"尺寸=({total.size.x:F1} x {total.size.y:F1} x {total.size.z:F1}) 米\n" +
                $"  离本物体最近的一个：{nearestName}，距离 {nearest:F1} 米，位置 " +
                $"({nearestPos.x:F1}, {nearestPos.y:F1}, {nearestPos.z:F1})\n" +
                $"  （离本物体远不代表看不见；请用 Scene 视图的黄色框确认生成区域）");

            // 逐个列出位置与朝向，这是"到底在哪、有没有在我前面"的直接依据。
            // 出生点朝向在场景里是 180.6°，所以 -Z 方向 = 玩家正前方。
            Vector3 playerPos = new Vector3(spawnSafeCenter.x, 0f, spawnSafeCenter.y);
            for (int i = 0; i < spawned.Count; i++)
            {
                GameObject go = spawned[i];
                if (go == null) continue;

                Vector3 pos = go.transform.position;

                // 打印【实际大小】，直接量渲染器包围盒。
                float wx = 0f, wy = 0f, wz = 0f;
                var rs = go.GetComponentsInChildren<Renderer>();
                if (rs != null && rs.Length > 0)
                {
                    Bounds b = rs[0].bounds;
                    for (int r = 1; r < rs.Length; r++) b.Encapsulate(rs[r].bounds);
                    wx = b.size.x; wy = b.size.y; wz = b.size.z;
                }

                float dx = pos.x - playerPos.x;
                float dz = pos.z - playerPos.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);

                // dz < 0 表示在玩家正前方（因为出生朝向是 -Z）
                string dir = dz < -0.5f ? "正前方" : (dz > 0.5f ? "身后" : "侧面");
                if (Mathf.Abs(dz) <= 0.5f && dx > 0f) dir = "正右手边";
                if (Mathf.Abs(dz) <= 0.5f && dx < 0f) dir = "正左手边";

                Debug.Log($"  [{i:D2}] {go.name}  位置=({pos.x:F1}, {pos.y:F1}, {pos.z:F1})  " +
                          $"实际大小=({wx:F2} x {wy:F2} x {wz:F2}) 米  " +
                          $"距出生点 {dist:F1} 米  方位={dir}");
            }

            if (withMaterial < withRenderer)
            {
                Debug.LogWarning(
                    $"[ObstacleGenerator] 有 {withRenderer - withMaterial} 个障碍物没有材质，" +
                    $"它们会显示为 Unity 默认的白色/洋红色。这是回退 Cube 没有指定材质的正常现象。");
            }
        }

        /// <summary>
        /// 在 Scene 视图里画出生成区域（黄色框）和出生点禁放区（青色球），
        /// 这样不用运行游戏就能看到"障碍物会撒在哪个范围"。
        /// 前提：在 Hierarchy 里选中挂了这个脚本的物体。
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            // 生成区域：以 areaCenter 为中心的一个范围框，高度用 heightRange 表示
            Vector3 center = new Vector3(areaCenter.x, 0f, areaCenter.y);
            Vector3 size = new Vector3(areaHalfSize * 2f, Mathf.Max(heightRange.y, 1f), areaHalfSize * 2f);

            Gizmos.color = new Color(1f, 0.85f, 0.1f, 1f); // 黄色
            Gizmos.DrawWireCube(center, size);

            // 出生点禁放区：青色球
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 1f);
            Gizmos.DrawWireSphere(new Vector3(spawnSafeCenter.x, 0f, spawnSafeCenter.y), spawnSafeRadius);

            // 如果已经生成过，把每个障碍物的位置和尺寸也画出来
            Gizmos.color = new Color(1f, 0.3f, 0.2f, 1f); // 红色
            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] == null) continue;

                var renderer = spawned[i].GetComponent<Renderer>();
                if (renderer != null)
                    Gizmos.DrawWireCube(renderer.bounds.center, renderer.bounds.size);
                else
                    Gizmos.DrawWireCube(spawned[i].transform.position, spawned[i].transform.localScale);
            }
        }

        /// <summary>
        /// 在设置里登记一个禁放点（XZ 平面）。运行时调用有效，编辑器里调用无效。
        /// 适合"出生点由代码决定、无法在 Inspector 里填死"的情况。
        /// 注意：必须在 GenerateObstacles() 之前调用。
        /// </summary>
        public void AddAvoidPoint(Vector3 worldPoint)
        {
            if (avoidPoints == null)
                avoidPoints = new List<Vector3>();

            if (!avoidPoints.Contains(worldPoint))
                avoidPoints.Add(worldPoint);
        }

        /// <summary>清掉本脚本上一次生成出来的所有障碍物，并重置记录。</summary>
        public void ClearExisting()
        {
            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] != null)
                    DestroySafe(spawned[i]);
            }

            spawned.Clear();
            placedPoints.Clear();
            _modelScaleFactors.Clear();
            _modelBaseSizes.Clear();
            _modelBaseY.Clear();
            _maxFinalModelSize = 0f;
            LastSpawnedCount = 0;
            LastAttemptCount = 0;

            // 清理回退材质，避免重复生成时泄漏
            if (fallbackMaterial != null)
            {
                if (Application.isPlaying) Destroy(fallbackMaterial);
                else DestroyImmediate(fallbackMaterial);
                fallbackMaterial = null;
            }
        }

        // ==================== 内部工具 ====================

        /// <summary>
        /// 没指定预制体时的临时模板。它是一个失活的 Cube 模板，
        /// 实例化出来的每个障碍物都是独立对象，销毁模板不影响它们。
        /// 会额外给它一个醒目的颜色，否则默认灰白材质在灰白地面上很难被看到。
        /// </summary>
        private GameObject CreateFallbackCubePrefab()
        {
            GameObject template = GameObject.CreatePrimitive(PrimitiveType.Cube);
            template.name = "__ObstacleTemplate_Cube";
            template.SetActive(false);
            return template;
        }

        /// <summary>
        /// 运行时现场造一个回退材质（只在没有指定 obstacleMaterial 时使用）。
        ///
        /// 这里显式把渲染模式设为 Opaque：某些情况下 Shader.Find("Standard") 拿到的材质
        /// 会带上透明的混合模式（_Mode/_SrcBlend/_DstBlend），结果就是方块变成"透明的"。
        /// 明确写死这些属性可以彻底避免该问题。
        /// </summary>
        private Material EnsureFallbackMaterial()
        {
            if (fallbackMaterial != null) return fallbackMaterial;

            var shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Debug.LogError("[ObstacleGenerator] 找不到可用的着色器，障碍物将没有材质。");
                return null;
            }

            fallbackMaterial = new Material(shader);
            fallbackMaterial.color = fallbackCubeColor;

            // 强制 Opaque，避免渲染成透明
            if (fallbackMaterial.HasProperty("_Mode")) fallbackMaterial.SetFloat("_Mode", 0f);
            if (fallbackMaterial.HasProperty("_SrcBlend")) fallbackMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            if (fallbackMaterial.HasProperty("_DstBlend")) fallbackMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            if (fallbackMaterial.HasProperty("_ZWrite")) fallbackMaterial.SetFloat("_ZWrite", 1f);
            if (fallbackMaterial.HasProperty("_Glossiness")) fallbackMaterial.SetFloat("_Glossiness", 0.1f);

            fallbackMaterial.DisableKeyword("_ALPHATEST_ON");
            fallbackMaterial.DisableKeyword("_ALPHABLEND_ON");
            fallbackMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            fallbackMaterial.renderQueue = -1;

            return fallbackMaterial;
        }

        /// <summary>
        /// 兼容运行时与编辑模式的销毁。
        /// 运行时必须用 Destroy（DestroyImmediate 会破坏正在进行的帧）；
        /// 编辑模式下只能用 DestroyImmediate（Destroy 在编辑器里会直接报错且不生效）。
        /// </summary>
        private static void DestroySafe(GameObject go)
        {
            if (go == null) return;

            if (Application.isPlaying)
                Destroy(go);
            else
                DestroyImmediate(go);
        }

        private bool IsBlockedByAvoidPoints(float x, float z, float width, float depth)
        {
            if (avoidPoints == null || avoidPoints.Count == 0) return false;

            float maxExtent = Mathf.Max(width, depth) * 0.5f;

            for (int i = 0; i < avoidPoints.Count; i++)
            {
                Vector3 p = avoidPoints[i];
                float dx = x - p.x;
                float dz = z - p.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);

                if (dist < avoidPointRadius + maxExtent)
                    return true;
            }

            return false;
        }

        private bool IsTooCloseToExisting(float x, float z)
        {
            float minSpacingSqr = minSpacing * minSpacing;

            for (int i = 0; i < placedPoints.Count; i++)
            {
                float dx = x - placedPoints[i].x;
                float dz = z - placedPoints[i].y;
                if (dx * dx + dz * dz < minSpacingSqr)
                    return true;
            }

            return false;
        }

        /// <summary>从高空向下打射线，取该坐标处的地面高度。</summary>
        private bool TryGetGroundHeight(float x, float z, out float groundY)
        {
            groundY = 0f;

            var ray = new Ray(new Vector3(x, GroundProbeStartY, z), Vector3.down);

            if (!Physics.Raycast(ray, out RaycastHit hit, GroundProbeDistance, groundMask,
                                 QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            groundY = hit.point.y;
            return true;
        }

        /// <summary>
        /// 可选的物理重叠检测。occupancyMask 默认是 Nothing，此时直接返回 false，
        /// 完全不做物理查询 —— 因为地面自己会挡住每一个候选点。
        /// </summary>
        private bool IsOccupied(float x, float z, float groundY, float width, float height, float depth)
        {
            if (occupancyMask.value == 0) return false;

            Vector3 center = new Vector3(x, groundY + height * 0.5f, z);
            Vector3 halfExtents = new Vector3(width * 0.5f, height * 0.5f, depth * 0.5f) * 0.95f;

            return Physics.CheckBox(center, halfExtents, Quaternion.identity, occupancyMask,
                                    QueryTriggerInteraction.Ignore);
        }

        // ==================== 编辑器辅助 ====================

        private void OnValidate()
        {
            // 兜底修正，避免在 Inspector 里填出矛盾值
            if (obstacleLayer < 0) obstacleLayer = 0;
            if (obstacleLayer > 31) obstacleLayer = 31;

            if (spawnCount < 0) spawnCount = 0;
            if (areaHalfSize < 1f) areaHalfSize = 1f;
            if (spawnSafeRadius < 0f) spawnSafeRadius = 0f;
            if (minSpacing < 0f) minSpacing = 0f;
            if (avoidPointRadius < 0f) avoidPointRadius = 0f;

            // occupancyMask 若把 ground 层(7)算进去，地面会挡住每一个候选点，导致一个都放不出来。
            // 这里自动剔除，避免误配。
            if ((occupancyMask.value & groundMask.value) != 0)
            {
                occupancyMask.value &= ~groundMask.value;
                Debug.LogWarning("[ObstacleGenerator] occupancyMask 与 groundMask 有重叠，已自动移除重叠部分，"
                                 + "否则地面会挡住所有候选点。");
            }
        }

#if UNITY_EDITOR
        [ContextMenu("生成障碍物（编辑器预览）")]
        private void GenerateInEditor()
        {
            // 编辑器里生成的对象没有 Undo 记录，用菜单显式触发，避免误操作
            int result = GenerateObstacles();

            // 让编辑器知道组件状态变了（生成的对象本身不属于本组件的序列化数据，
            // 所以不会被 Undo 记录，也不会被存进场景）
            EditorUtility.SetDirty(this);
            Debug.Log($"[ObstacleGenerator] 编辑器预览生成 {result} 个。注意：这些对象不会被 Undo 记录，" +
                      $"不满意请手动删除，或重新生成（重新生成会先清理上一次的结果）。");
        }
#endif
    }
}
