using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 简单地面 AI。只由 CharacterController.Move 移动，脚本负责重力和水平转向。
/// 动画读取碰撞后的实际速度；没有寻路、攻击或联机消息。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
public class DragonAI : MonoBehaviour
{
    public enum State { Idle, Patrol, Chase }
    private enum WaitReason { None, Arrived, Retry }

    [Header("目标")]
    [Tooltip("优先追踪这个玩家；留空则按 Tag 定期寻找最近玩家。")]
    public Transform playerTarget;
    public string playerTag = "Player";
    [Min(0.1f)] public float targetSearchInterval = 0.5f;

    [Header("发现 / 放弃")]
    public float detectRange = 18f;
    public float loseRange = 28f;
    public float stopDistance = 5f;
    [Tooltip("停下后超过 stopDistance + stopMargin 才重新移动。")]
    public float stopMargin = 1f;

    [Header("速度（米/秒）")]
    public float patrolSpeed = 2.5f;
    public float chaseSpeed = 6f;
    [Tooltip("水平速度变化率（米/秒²）。到达或原地转身时立即停止。")]
    public float speedChangeRate = 6f;
    public float minMoveSpeed = 0.05f;

    [Header("转身")]
    public float turnSpeed = 150f;
    public float turnBeforeMoveAngle = 60f;
    [Range(0f, 1f)] public float speedWhileTurning = 0f;
    [Tooltip("移动时偏差超过此角度开始转向，进入四分之一死区后停止。")]
    public float rotationDeadZone = 10f;
    [Tooltip("追到玩家附近停下后，偏差超过此角度才重新转向。")]
    public float facingTolerance = 8f;

    [Header("巡逻")]
    [Tooltip("场景中独立的空物体，不要放在移动的龙下面。")]
    public Transform[] patrolPoints;
    public float waitAtPoint = 2f;
    public float arriveDistance = 2f;
    public float stuckTimeout = 1f;
    [Tooltip("同一点连续受阻达到此次数后跳点；之前的等待会重试原点。")]
    public int maxStuckRetries = 2;
    public float allBlockedCooldown = 8f;
    [Tooltip("兼容已有配置，已停用。是否不可达由实际移动失败判断，不按距离猜测。")]
    public float unreachableDistance = 3f;

    [Header("物理")]
    public float gravity = 20f;
    public float groundedDownSpeed = -2f;
    [Min(1f)] public float maxFallSpeed = 30f;
    // 保留旧序列化字段，但不让长射线命中代替真正的胶囊接地。
    [Tooltip("旧配置兼容字段，当前接地使用 CharacterController 的碰撞结果。")]
    public LayerMask groundMask = 128;
    [Tooltip("旧配置兼容字段，当前不使用射线决定接地。")]
    public float groundProbeDistance = 0.7f;
    [Tooltip("旧配置兼容字段，当前不使用射线决定接地。")]
    public float groundProbeOffset = 0.25f;
    [Tooltip("旧配置兼容字段，请在 CharacterController.slopeLimit 设置可走坡度。")]
    public float maxGroundSlope = 50f;

    [Header("视图 / 调试")]
    [Tooltip("身体 Animator，不是眨眼 Animator。留空会优先找带 Speed 参数的 Animator。")]
    public Animator animator;
    public bool drawGizmos = true;
    public bool logStateChanges = false;

    /// <summary>本次 Move 之后的实际水平速度（米/秒），用于动画。</summary>
    public float MoveSpeed { get; private set; }
    /// <summary>经过转向限制、加速和到达限制后，实际提交给 Move 的水平速度。</summary>
    public float DesiredSpeed { get; private set; }
    public State CurrentState { get { return _state; } }
    public Transform CurrentTarget { get { return IsUsableTarget(_player) ? _player : null; } }
    public bool HasValidTarget { get { return CurrentTarget != null; } }
    public int CurrentPatrolIndex { get { return _patrolIndex; } }
    public bool IsWaiting { get { return _waitReason != WaitReason.None; } }
    public bool IsGrounded { get { return _cc != null && _cc.isGrounded; } }

    private CharacterController _cc;
    private DragonAnimatorDriver _driver;
    private Transform _player;
    private Transform _lastConfiguredTarget;
    private float _targetSearchTimer;
    private bool _targetWarned;
    private State _state;
    private int _patrolIndex;
    private Transform _activePatrolPoint;
    private WaitReason _waitReason;
    private float _waitTimer;
    private float _stuckTimer;
    private int _stuckCount;
    private float _cooldownTimer;
    private readonly HashSet<Transform> _failedPatrolPoints = new HashSet<Transform>();
    private bool _isStopping;
    private bool _isRotating;
    private bool _turningBeforeMove;
    private float _commandSpeed;
    private float _verticalVelocity;
    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private Animator _checkedAnimator;
    private RuntimeAnimatorController _checkedController;
    private bool _hasSpeedParameter;

    private void Awake()
    {
        EnsureReferences();
    }

    private void Start()
    {
        // Start 时才读取 Inspector/场景构建器最终配置，不能只在 Awake 查目标一次。
        EnsureReferences();
        RefreshTarget(0f);
    }

    private void Update()
    {
        Tick(Time.deltaTime);
    }

    /// <summary>
    /// 执行一次 AI 更新。正常运行由 Update 调用；隔离验证时可禁用本脚本并手动调用。
    /// 不要在自动 Update 同时从别处再次调用，也不要另外移动这个控制器。
    /// </summary>
    public void Tick(float deltaTime)
    {
        if (deltaTime <= 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime)) return;
        EnsureReferences();
        if (_cc == null || !_cc.enabled || !gameObject.activeInHierarchy)
        {
            MoveSpeed = DesiredSpeed = _commandSpeed = 0f;
            return;
        }

        RefreshTarget(deltaTime);
        ResolvePatrolPoint();
        UpdateTimers(deltaTime);
        UpdateState();

        Vector3 direction;
        float requestedSpeed;
        float remainingDistance;
        DecideMotion(out direction, out requestedSpeed, out remainingDistance);
        ApplyRotation(direction, requestedSpeed <= 0f, deltaTime);
        ApplySpeed(direction, requestedSpeed, remainingDistance, deltaTime);
        ApplyMove(deltaTime);
        UpdateStuck(deltaTime);
        WriteAnimatorFallback();
    }

    private void EnsureReferences()
    {
        if (_cc == null) _cc = GetComponent<CharacterController>();
        if (_driver == null) _driver = GetComponent<DragonAnimatorDriver>();
        if (animator == null)
        {
            animator = _driver != null && _driver.animator != null
                ? _driver.animator : FindBodyAnimator(transform, SpeedHash);
        }
        if (animator != null && animator.applyRootMotion) animator.applyRootMotion = false;
    }

    private void RefreshTarget(float dt)
    {
        if (playerTarget != _lastConfiguredTarget)
        {
            _lastConfiguredTarget = playerTarget;
            AssignTarget(IsUsableTarget(playerTarget) ? playerTarget : null);
            _targetSearchTimer = 0f;
        }

        if (IsUsableTarget(playerTarget))
        {
            AssignTarget(playerTarget);
            return;
        }
        if (!ReferenceEquals(_player, null) && !IsUsableTarget(_player))
        {
            AssignTarget(null);
            _targetSearchTimer = 0f;
        }
        // 追赶中保留当前玩家，避免每次查找都改追另外一个人。
        if (_state == State.Chase && _player != null &&
            HorizontalDistanceTo(_player.position) <= Mathf.Max(detectRange + 0.1f, loseRange)) return;

        _targetSearchTimer -= dt;
        if (_targetSearchTimer > 0f) return;
        _targetSearchTimer = Mathf.Max(0.1f, targetSearchInterval);
        Transform nearest = null;
        float nearestSqr = float.PositiveInfinity;
        if (!string.IsNullOrEmpty(playerTag))
        {
            try
            {
                GameObject[] players = GameObject.FindGameObjectsWithTag(playerTag);
                foreach (GameObject candidate in players)
                {
                    if (!IsUsableTarget(candidate.transform)) continue;
                    Vector3 delta = candidate.transform.position - transform.position;
                    delta.y = 0f;
                    if (delta.sqrMagnitude >= nearestSqr) continue;
                    nearestSqr = delta.sqrMagnitude;
                    nearest = candidate.transform;
                }
            }
            catch (UnityException)
            {
                // 未注册的 Tag 按无目标处理；只在需要调试时输出一次说明。
            }
        }
        AssignTarget(nearest);
        if (nearest == null && !_targetWarned && logStateChanges)
        {
            _targetWarned = true;
            Debug.Log("[DragonAI] 尚未找到玩家，先巡逻并定期重新查找。", this);
        }
    }

    private bool IsUsableTarget(Transform target)
    {
        return target != null && target.gameObject.activeInHierarchy &&
               target != transform && !target.IsChildOf(transform);
    }

    private void AssignTarget(Transform target)
    {
        if (ReferenceEquals(_player, target)) return;
        _player = target;
        _isStopping = false;
        _isRotating = false;
        if (target != null) _targetWarned = false;
    }

    /// <summary>设置运行时手动目标；传 null 恢复按 Tag 自动查找。</summary>
    public void SetTarget(Transform target)
    {
        playerTarget = target;
        _lastConfiguredTarget = target;
        AssignTarget(IsUsableTarget(target) ? target : null);
        _targetSearchTimer = 0f;
    }

    private void UpdateTimers(float dt)
    {
        if (_cooldownTimer > 0f)
        {
            _cooldownTimer = Mathf.Max(0f, _cooldownTimer - dt);
            if (_cooldownTimer <= 0f) _failedPatrolPoints.Clear();
        }
        if (_waitReason == WaitReason.None) return;
        _waitTimer -= dt;
        if (_waitTimer > 0f) return;

        WaitReason finished = _waitReason;
        _waitReason = WaitReason.None;
        _waitTimer = 0f;
        _stuckTimer = 0f;
        // 到达后的等待才前往下一个点；受阻等待保留索引和重试次数。
        if (finished == WaitReason.Arrived) AdvancePatrolIndex();
    }

    private void UpdateState()
    {
        float range = _state == State.Chase ? Mathf.Max(detectRange + 0.1f, loseRange) : detectRange;
        State next = HasValidTarget && HorizontalDistanceTo(_player.position) <= range
            ? State.Chase
            : (_cooldownTimer > 0f || ResolvePatrolPoint() == null ? State.Idle : State.Patrol);
        ChangeState(next);
    }

    private void ChangeState(State next)
    {
        if (_state == next) return;
        if (logStateChanges) Debug.Log("[DragonAI] " + name + ": " + _state + " -> " + next, this);
        bool chaseChanged = _state == State.Chase || next == State.Chase;
        _state = next;
        _isStopping = false;
        _isRotating = false;
        ClearPatrolAttempt();
        if (chaseChanged)
        {
            // 追赶改变了所在位置，之前一轮巡逻的受阻结果不再适用。
            _failedPatrolPoints.Clear();
            _cooldownTimer = 0f;
        }
    }

    private void DecideMotion(out Vector3 direction, out float speed, out float remainingDistance)
    {
        direction = Vector3.zero;
        speed = 0f;
        remainingDistance = float.PositiveInfinity;
        if (_state == State.Chase && HasValidTarget)
        {
            Vector3 toPlayer = _player.position - transform.position;
            toPlayer.y = 0f;
            float distance = toPlayer.magnitude;
            if (distance > 0.5f) direction = toPlayer / distance;
            if (_isStopping)
            {
                if (distance > stopDistance + Mathf.Max(0f, stopMargin)) _isStopping = false;
            }
            else if (distance <= stopDistance + 0.01f) _isStopping = true;
            if (!_isStopping)
            {
                speed = Mathf.Max(0f, chaseSpeed);
                remainingDistance = Mathf.Max(0f, distance - stopDistance);
            }
            return;
        }
        if (_state != State.Patrol || IsWaiting) return;
        Transform point = ResolvePatrolPoint();
        if (point == null) return;
        Vector3 toPoint = point.position - transform.position;
        toPoint.y = 0f;
        float distanceToPoint = toPoint.magnitude;
        if (distanceToPoint <= Mathf.Max(0.01f, arriveDistance) + 0.01f)
        {
            _failedPatrolPoints.Clear();
            _stuckCount = 0;
            BeginWait(WaitReason.Arrived);
            return;
        }
        direction = toPoint / distanceToPoint;
        speed = Mathf.Max(0f, patrolSpeed);
        remainingDistance = Mathf.Max(0f, distanceToPoint - arriveDistance);
    }

    private void ApplyRotation(Vector3 direction, bool stopped, float dt)
    {
        if (direction.sqrMagnitude < 0.0001f)
        {
            _isRotating = false;
            return;
        }
        float startAngle = Mathf.Max(0f, stopped && _state == State.Chase ? facingTolerance : rotationDeadZone);
        float stopAngle = Mathf.Min(2f, startAngle * 0.25f);
        Quaternion current = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        Quaternion desired = Quaternion.LookRotation(direction, Vector3.up);
        float angle = Quaternion.Angle(current, desired);
        if (_isRotating && angle <= stopAngle) _isRotating = false;
        else if (!_isRotating && angle > startAngle) _isRotating = true;
        transform.rotation = _isRotating
            ? Quaternion.RotateTowards(current, desired, Mathf.Max(0f, turnSpeed) * dt) : current;
    }

    private void ApplySpeed(Vector3 direction, float speed, float remainingDistance, float dt)
    {
        _turningBeforeMove = speed > 0f && direction.sqrMagnitude > 0.0001f &&
                             Vector3.Angle(transform.forward, direction) > turnBeforeMoveAngle;
        if (_turningBeforeMove) speed *= Mathf.Clamp01(speedWhileTurning);
        if (direction.sqrMagnitude < 0.0001f || speed <= 0f) _commandSpeed = 0f;
        else _commandSpeed = Mathf.MoveTowards(_commandSpeed, speed, Mathf.Max(0f, speedChangeRate) * dt);
        _commandSpeed = Mathf.Min(_commandSpeed, remainingDistance / dt);
        DesiredSpeed = _commandSpeed;
    }

    private void ApplyMove(float dt)
    {
        float downSpeed = -Mathf.Max(0.1f, Mathf.Abs(groundedDownSpeed));
        if (_cc.isGrounded && _verticalVelocity <= 0f) _verticalVelocity = downSpeed;
        else _verticalVelocity = Mathf.Max(_verticalVelocity - Mathf.Max(0f, gravity) * dt, -Mathf.Max(1f, maxFallSpeed));

        Vector3 before = transform.position;
        Vector3 motion = transform.forward * DesiredSpeed;
        motion.y = _verticalVelocity;
        CollisionFlags flags = _cc.Move(motion * dt);
        Vector3 actualDelta = transform.position - before;
        actualDelta.y = 0f;
        MoveSpeed = actualDelta.magnitude / dt;
        if ((flags & CollisionFlags.Below) != 0 && _verticalVelocity <= 0f) _verticalVelocity = downSpeed;
        if ((flags & CollisionFlags.Above) != 0 && _verticalVelocity > 0f) _verticalVelocity = 0f;
    }

    private void UpdateStuck(float dt)
    {
        // 等待、原地转身、空中和主动停止均不属于被障碍物挡住。
        if (_state != State.Patrol || IsWaiting || _turningBeforeMove || !IsGrounded ||
            DesiredSpeed <= Mathf.Max(0.01f, minMoveSpeed))
        {
            _stuckTimer = 0f;
            return;
        }
        if (MoveSpeed >= DesiredSpeed * 0.15f)
        {
            _stuckTimer = 0f;
            return;
        }
        _stuckTimer += dt;
        if (_stuckTimer < Mathf.Max(0.1f, stuckTimeout)) return;
        _stuckTimer = 0f;
        _stuckCount++;
        if (logStateChanges)
            Debug.Log("[DragonAI] 巡逻点 " + _patrolIndex + " 受阻，第 " + _stuckCount + " 次尝试失败。", this);
        if (_stuckCount < Mathf.Max(1, maxStuckRetries))
        {
            BeginWait(WaitReason.Retry);
            return;
        }

        Transform failedPoint = ResolvePatrolPoint();
        if (failedPoint != null) _failedPatrolPoints.Add(failedPoint);
        if (AllPatrolPointsFailed())
        {
            _cooldownTimer = Mathf.Max(0.1f, allBlockedCooldown);
            ChangeState(State.Idle);
            _commandSpeed = DesiredSpeed = 0f;
        }
        else AdvancePatrolIndex();
    }

    private bool AllPatrolPointsFailed()
    {
        if (patrolPoints == null) return true;
        foreach (Transform point in patrolPoints)
            if (point != null && !_failedPatrolPoints.Contains(point)) return false;
        return true;
    }

    private void BeginWait(WaitReason reason)
    {
        _waitReason = reason;
        _waitTimer = Mathf.Max(0f, waitAtPoint);
        _stuckTimer = 0f;
        _commandSpeed = DesiredSpeed = 0f;
    }

    private Transform ResolvePatrolPoint()
    {
        if (patrolPoints == null || patrolPoints.Length == 0)
        {
            _activePatrolPoint = null;
            _patrolIndex = 0;
            return null;
        }
        _patrolIndex = Mathf.Clamp(_patrolIndex, 0, patrolPoints.Length - 1);
        for (int i = 0; i < patrolPoints.Length; i++)
        {
            int index = (_patrolIndex + i) % patrolPoints.Length;
            Transform point = patrolPoints[index];
            if (point == null) continue;
            _patrolIndex = index;
            if (_activePatrolPoint != point)
            {
                _activePatrolPoint = point;
                ClearPatrolAttempt();
            }
            return point;
        }
        _activePatrolPoint = null;
        return null;
    }

    private void AdvancePatrolIndex()
    {
        ClearPatrolAttempt();
        _activePatrolPoint = null;
        if (patrolPoints == null || patrolPoints.Length == 0) return;
        for (int i = 1; i <= patrolPoints.Length; i++)
        {
            int next = (_patrolIndex + i) % patrolPoints.Length;
            if (patrolPoints[next] == null || _failedPatrolPoints.Contains(patrolPoints[next])) continue;
            _patrolIndex = next;
            break;
        }
        ResolvePatrolPoint();
    }

    private void ClearPatrolAttempt()
    {
        _waitReason = WaitReason.None;
        _waitTimer = _stuckTimer = 0f;
        _stuckCount = 0;
    }

    /// <summary>运行时重新配置巡逻路线后调用。</summary>
    public void ResetPatrol()
    {
        _patrolIndex = 0;
        _activePatrolPoint = null;
        _failedPatrolPoints.Clear();
        _cooldownTimer = 0f;
        ClearPatrolAttempt();
    }

    private float HorizontalDistanceTo(Vector3 point)
    {
        Vector3 delta = point - transform.position;
        delta.y = 0f;
        return delta.magnitude;
    }

    private void WriteAnimatorFallback()
    {
        // 有启用的专用 Driver 时，AI 不再竞争写动画参数。
        if (_driver != null && _driver.isActiveAndEnabled) return;
        if (animator == null || animator.runtimeAnimatorController == null) return;
        if (_checkedAnimator != animator || _checkedController != animator.runtimeAnimatorController)
        {
            _checkedAnimator = animator;
            _checkedController = animator.runtimeAnimatorController;
            _hasSpeedParameter = HasFloatParameter(animator, SpeedHash);
        }
        if (_hasSpeedParameter) animator.SetFloat(SpeedHash, MoveSpeed < minMoveSpeed ? 0f : MoveSpeed);
    }

    internal static Animator FindBodyAnimator(Transform root, int speedHash)
    {
        Animator[] animators = root.GetComponentsInChildren<Animator>(true);
        foreach (Animator candidate in animators)
            if (HasFloatParameter(candidate, speedHash)) return candidate;
        foreach (Animator candidate in animators)
            if (candidate.avatar != null) return candidate;
        return animators.Length > 0 ? animators[0] : null;
    }

    internal static bool HasFloatParameter(Animator target, int parameterHash)
    {
        if (target == null || target.runtimeAnimatorController == null) return false;
        foreach (AnimatorControllerParameter parameter in target.parameters)
            if (parameter.nameHash == parameterHash && parameter.type == AnimatorControllerParameterType.Float) return true;
        return false;
    }

    private void OnValidate()
    {
        detectRange = Mathf.Max(0f, detectRange);
        loseRange = Mathf.Max(detectRange + 0.1f, loseRange);
        stopDistance = Mathf.Max(0f, stopDistance);
        stopMargin = Mathf.Max(0f, stopMargin);
        arriveDistance = Mathf.Max(0.01f, arriveDistance);
        turnBeforeMoveAngle = Mathf.Clamp(turnBeforeMoveAngle, 0f, 180f);
        rotationDeadZone = Mathf.Clamp(rotationDeadZone, 0f, 180f);
        facingTolerance = Mathf.Clamp(facingTolerance, 0f, 180f);
        maxStuckRetries = Mathf.Max(1, maxStuckRetries);
        maxFallSpeed = Mathf.Max(1f, maxFallSpeed);
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;
        Vector3 center = transform.position;
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(center, detectRange);
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(center, loseRange);
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(center, stopDistance);
        Gizmos.color = Color.white;
        Gizmos.DrawRay(center, transform.forward * 3f);
        if (patrolPoints == null) return;
        Gizmos.color = Color.cyan;
        foreach (Transform point in patrolPoints)
            if (point != null) Gizmos.DrawWireSphere(point.position, 0.8f);
    }
}
