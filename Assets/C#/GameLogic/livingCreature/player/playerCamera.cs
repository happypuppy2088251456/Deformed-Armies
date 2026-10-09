using UnityEngine;

/// <summary>
/// 玩家相机控制器。
/// 负责处理本地玩家的鼠标视角输入、相机旋转限制，以及向服务器发送视角同步数据。
/// </summary>
public class playerCamera : MonoBehaviour
{
    public playerMove pm;
    private float targetCameraViewField;
    private float currentCameraViewField = 60f;
    private void ChangeCameraViewField()
    {
        if (pm.Aim) targetCameraViewField = 50f;
        else targetCameraViewField = 60f;
        currentCameraViewField = Mathf.Lerp(currentCameraViewField, targetCameraViewField, 5f * Time.deltaTime);
        PlayerCamera.fieldOfView = currentCameraViewField;
    }

    [Header("Camera Settings")]
    
    [SerializeField] private float mouseSensitivity = 300f;
    private Animator animator;
    public Camera PlayerCamera; 
    private void GatherInput()
    {
        lookInput = new Vector2(
            Input.GetAxisRaw("Mouse X"),
            Input.GetAxisRaw("Mouse Y")
        );

        // 达到发送间隔时，触发视角数据就绪事件
        sendTimer += Time.deltaTime;
        if (sendTimer >= sendInterval)
        {
            sendTimer = 0f;
            OnLookDataReady(GetYaw(), GetPitch());
        }
    }
    private void ApplyRotation()
    {
        float mouseX = lookInput.x * mouseSensitivity * Time.deltaTime;
        float mouseY = lookInput.y * mouseSensitivity * Time.deltaTime;

        // 计算并限制俯仰角（上下看）
        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -60f, 60f);

        float AimDegree = 0.5f+ PlayerCamera.transform.rotation.x;
        if (AimDegree < 0) AimDegree = 0;
        if(AimDegree > 1f) AimDegree = 1f;
        animator.SetFloat("AimDegree", AimDegree);

        // 应用旋转：相机自身只负责上下旋转，父物体（玩家模型）负责左右旋转
        PlayerCameraTransform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        transform.Rotate(Vector3.up * mouseX);
    }

    public Transform PlayerCameraTransform;
    private float xRotation = 0f;
    private Vector2 lookInput;
    private bool isLocalPlayer = true;

    private void Awake()
    {
        // 查找子物体中的实际相机节点
        //PlayerCameraTransform = transform.Find("Main_Camera");
        animator = GetComponent<Animator>();
    }
    private void Update()
    {
        if (isLocalPlayer)
        {
            GatherInput();
            ChangeCameraViewField();
        }

        // 无论是否为本地玩家，都需要应用旋转（用于同步远端玩家的视角）
        ApplyRotation();
    }

    #region 网络同步 - 配置参数 (Network Sync Settings)

    [Header("Network Settings")]
    private float sendTimer = 0f;

    /// <summary>
    /// 向服务器发送视角数据的频率（秒）。值越小同步越频繁，但网络开销越大。
    /// </summary>
    [SerializeField] private float sendInterval = 0.05f;

    #endregion
    #region 网络同步 - 状态获取与服务器交互 (Network Sync & Server API)

    // --- 供外部（如网络管理器）读取本地状态的接口 ---

    /// <summary>
    /// 获取玩家当前的偏航角（左右旋转角度）。
    /// </summary>
    public float GetYaw()
    {
        return transform.eulerAngles.y;
    }

    /// <summary>
    /// 获取玩家当前的俯仰角（上下旋转角度）。
    /// </summary>
    public float GetPitch()
    {
        return xRotation;
    }

    /// <summary>
    /// 获取相机正前方的方向向量（常用于射击或瞄准判定）。
    /// </summary>
    public Vector3 GetAimDirection()
    {
        return PlayerCameraTransform.forward;
    }

    // --- 供外部（如网络管理器）覆写本地状态的接口 ---

    /// <summary>
    /// 外部设置视角输入（用于网络同步时，远端玩家接收到的视角数据）。
    /// </summary>
    public void SetLookInput(Vector2 input)
    {
        lookInput = input;
    }

    /// <summary>
    /// 【网络同步核心】直接设置相机的绝对旋转角度。
    /// 通常由服务器权威数据或客户端预测修正时调用。
    /// </summary>
    /// <param name="yaw">偏航角</param>
    /// <param name="pitch">俯仰角</param>
    public void SetRotation(float yaw, float pitch)
    {
        transform.eulerAngles = new Vector3(0f, yaw, 0f);
        xRotation = pitch;
        PlayerCameraTransform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
    }

    /// <summary>
    /// 仅设置相机俯仰角（远端玩家视角插值用，避免重复覆盖身体偏航角）。
    /// </summary>
    public void SetPitch(float pitch)
    {
        xRotation = pitch;
        if (PlayerCameraTransform != null)
            PlayerCameraTransform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
    }

    /// <summary>
    /// 设置当前对象是否为本地玩家。
    /// </summary>
    public void SetLocalPlayer(bool isLocal)
    {
        isLocalPlayer = isLocal;
    }

    /// <summary>
    /// 【网络事件回调】当本地玩家的视角数据准备好发送时触发。
    /// 子类或外部网络管理器应重写/订阅此方法来实际发送网络包。
    /// </summary>
    /// <param name="yaw">当前偏航角</param>
    /// <param name="pitch">当前俯仰角</param>
    protected virtual void OnLookDataReady(float yaw, float pitch)
    {
        // 默认空实现，等待外部继承或事件绑定
    }

    #endregion
}