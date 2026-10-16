using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 玩家移动与网络同步控制器。
/// 负责处理本地玩家的移动跳跃输入、移动跳跃逻辑，以及与服务器的位置同步和状态校验。
/// </summary>
public class playerMove : MonoBehaviour
{

    [Header("Move Settings")]
    private CapsuleCollider moveCollider_stand;
    private Rigidbody rb;
    private Animator animator;

    [SerializeField] private float runSpeed = 8.4f;
    [SerializeField] private float walkSpeed = 5f;
     private float speedChangeRate =20f;

    [Header("Jump Settings")]
    [SerializeField] private float jumpForce = 7f;
    private float gravity = 20f;
    private float groundCheckDistance = 0.2f;
    private float PreGroundDistance = 0.5f;
    [SerializeField] private LayerMask groundLayer;
    private const float TerminalFallSpeed = 30f;
    private const float GroundStickSpeed = 2f;
    private readonly RaycastHit[] groundHits = new RaycastHit[16];

    private float currentSpeed;
    private float targetSpeed;
    private float smoothInputX;
    private float smoothInputY;
    private bool isRunning = false;

    private float verticalVelocity;
    private bool isGrounded;
    private bool JumpEndPre;
    private bool jumpRequested;
    private bool HandGun=false;
    public bool Aim;
    private float currentMultiplier = 0f;
    private float targetMultiplier;
    private Vector2 moveInput;
    private bool isLocalPlayer = true;
    private void GatherInput()
    {
        moveInput = new Vector2(
            Input.GetAxisRaw("Horizontal"),
            Input.GetAxisRaw("Vertical")
        );
    }
    private void WalkOrRunManager()
    {
        if (Input.GetKeyDown(KeyCode.LeftShift))
            isRunning = !isRunning;
    }
    private void JumpInput()
    {
        if (Input.GetKeyDown(KeyCode.Space))
            jumpRequested = true;
    }

    private void HandGunInput()
    {
        if (Input.GetKeyDown(KeyCode.Alpha1))
            HandGun = !HandGun;
        animator.SetBool("HandGun", HandGun);
    }
    private void AimInput()
    {
        if (Input.GetMouseButtonDown(1))
            Aim = !Aim;
        animator.SetBool("Aim", Aim);
    }
    private void CheckGround()
    {
        // The model origin sits below the capsule's feet. Probe from the actual
        // collider instead, so landing and slopes cannot bury the ray origin.
        Bounds bounds = moveCollider_stand.bounds;
        float radius = Mathf.Min(bounds.extents.x, bounds.extents.z) * 0.9f;
        const float lift = 0.08f;
        Vector3 origin = new Vector3(bounds.center.x, bounds.min.y + radius + lift, bounds.center.z);
        int count = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, groundHits,
            PreGroundDistance + lift, groundLayer, QueryTriggerInteraction.Ignore);
        isGrounded = false;
        JumpEndPre = false;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = groundHits[i];
            // Walls, triggers and our own collider must not count as the floor.
            if (hit.collider.attachedRigidbody == rb || hit.normal.y < 0.5f) continue;
            JumpEndPre = true;
            if (hit.distance <= groundCheckDistance + lift) isGrounded = true;
        }
    }

    private void Move_player()
    {
        targetMultiplier = isRunning ? 1f : 0.5f;
       
        currentMultiplier = Mathf.MoveTowards(currentMultiplier, targetMultiplier, 2f * Time.fixedDeltaTime);

        smoothInputX = Mathf.MoveTowards(smoothInputX, moveInput.x, 5f * Time.fixedDeltaTime);
        smoothInputY = Mathf.MoveTowards(smoothInputY, moveInput.y, 5f * Time.fixedDeltaTime);

        animator.SetFloat("InputX", smoothInputX * currentMultiplier);
        animator.SetFloat("InputY", smoothInputY * currentMultiplier);



        if (moveInput.sqrMagnitude > 0.01f)
        {
            animator.SetBool("run", isRunning);
            animator.SetBool("walk", !isRunning);
            targetSpeed = isRunning ? runSpeed : walkSpeed;
        }
        else
        {
            animator.SetBool("run", false);
            animator.SetBool("walk", false);
            targetSpeed = 0f;
            isRunning = false;
        }

        // 根据目标速度与当前速度的关系，动态调整加速/减速平滑率
        float rate = (targetSpeed > currentSpeed)
            ? speedChangeRate
            : speedChangeRate * 0.9f;

        currentSpeed = Mathf.Lerp(currentSpeed, targetSpeed, rate * Time.fixedDeltaTime);
        if (moveInput.sqrMagnitude > 0.01f)
        {
            // 有输入时：归一化方向并应用当前速度
            float magnitude = moveInput.magnitude;
            float normalizedX = moveInput.x / magnitude;
            float normalizedZ = moveInput.y / magnitude;

            Vector3 moveDirection = transform.TransformDirection(normalizedX, 0f, normalizedZ);
            rb.velocity = new Vector3(moveDirection.x * currentSpeed, rb.velocity.y, moveDirection.z * currentSpeed);
        }
        else if (currentSpeed > 0.01f)
        {
            // 无输入但有残余速度时：保持方向进行惯性滑行
            Vector3 currentHorizontal = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
            if (currentHorizontal.sqrMagnitude > 0.001f)
            {
                Vector3 slideDir = currentHorizontal.normalized;
                rb.velocity = new Vector3(slideDir.x * currentSpeed, rb.velocity.y, slideDir.z * currentSpeed);
            }
            else
            {
                rb.velocity = new Vector3(0f, rb.velocity.y, 0f);
            }
        }
        else
        {
            // 完全停止
            rb.velocity = new Vector3(0f, rb.velocity.y, 0f);
        }
    }
    private void JumpAndGravity()
    {
        // Respect the velocity resolved by the last physics step (ground or
        // ceiling contact), rather than accumulating hidden downward speed.
        verticalVelocity = rb.velocity.y;
        animator.SetBool("Aim", false);
        animator.SetBool("HandGun", false);
        //起跳
        if (jumpRequested && isGrounded)
        {
            verticalVelocity = jumpForce;
            //isJumping = true;
            animator.SetTrigger("JumpPre");
            animator.ResetTrigger("JumpEnd");
        }
        jumpRequested = false;

        //落地检测
        if ( verticalVelocity < 0f && JumpEndPre&&!isGrounded)//isG
        {
           
            animator.SetTrigger("JumpEnd");
           // isJumping = false;             
        }

        // 重力
        if (!isGrounded || verticalVelocity > 0f)
        {
            verticalVelocity = Mathf.Max(verticalVelocity - gravity * Time.fixedDeltaTime, -TerminalFallSpeed);
        }
        else
        {
            // A small downward speed keeps contact on slopes without hovering
            // inside the ground-check distance or forcing the body into the floor.
            verticalVelocity = -GroundStickSpeed;
        }

        rb.velocity = new Vector3(rb.velocity.x, verticalVelocity, rb.velocity.z);
    }
    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        animator = GetComponent<Animator>();
        Transform stand = transform.Find("moveCollider_stand");
        moveCollider_stand = stand != null ? stand.GetComponent<CapsuleCollider>() : null;
        if (rb == null || animator == null || moveCollider_stand == null)
        {
            Debug.LogError("playerMove requires a Rigidbody, Animator and moveCollider_stand CapsuleCollider.", this);
            enabled = false;
            return;
        }
        rb.useGravity = false; // 使用自定义重力逻辑
        if (!rb.isKinematic) rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        currentSpeed = walkSpeed;
    }

    private void InputManager()
    {
        GatherInput();
        JumpInput();
        HandGunInput();
        AimInput();
    }
    private void Update()
    {
        if (isLocalPlayer)
        {
            WalkOrRunManager();
            InputManager();
        }
    }
    private void FixedUpdate()
    {
        CheckGround();
        Move_player();
        JumpAndGravity();
    }

    #region 网络同步 - 配置参数 (Network Sync Settings)

    [Header("Network Correction Settings")]
    /// <summary>
    /// 客户端与服务器位置偏差超过此阈值时，触发位置平滑修正。
    /// </summary>
    [SerializeField] private float correctionThreshold = 0.5f;

    /// <summary>
    /// 位置修正的插值强度（0~1），值越大修正越快，但可能产生拉扯感。
    /// </summary>
    [SerializeField] private float correctionStrength = 0.3f;

    #endregion
    #region 网络同步 - 状态获取与服务器交互 (Network Sync & Server API)

    // --- 供外部（如网络管理器、UI）读取本地状态的接口 ---

    public Vector2 GetMoveInput() => moveInput;
    public bool GetIsRunning() => isRunning;
    public bool GetJumpRequested() => jumpRequested;
    public bool GetIsGrounded() => isGrounded;

    // --- 供外部（如网络管理器）覆写本地状态的接口 ---

    public void SetMoveInput(Vector2 input) => moveInput = input;
    public void SetIsRunning(bool running) => isRunning = running;
    public void SetJumpRequested(bool jump) => jumpRequested = jump;

    /// <summary>
    /// 设置当前对象是否为本地玩家（非本地玩家不响应本地输入）。
    /// </summary>
    public void SetLocalPlayer(bool isLocal) => isLocalPlayer = isLocal;

    /// <summary>
    /// 【网络同步核心】接收服务器下发的权威位置，并进行平滑修正。
    /// 当客户端预测位置与服务器权威位置偏差过大时触发。
    /// </summary>
    /// <param name="serverPos">服务器下发的权威坐标</param>
    public void OnServerPositionReceived(Vector3 serverPos)
    {
        float distance = Vector3.Distance(transform.position, serverPos);

        if (distance > correctionThreshold)
        {
            rb.MovePosition(Vector3.Lerp(transform.position, serverPos, correctionStrength));
        }
    }

    #endregion
}
