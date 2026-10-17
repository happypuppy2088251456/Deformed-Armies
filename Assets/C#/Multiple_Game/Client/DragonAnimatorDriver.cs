using UnityEngine;

/// <summary>
/// 将 DragonAI 的实际水平速度（米/秒）写入身体 Animator。
/// 启用本组件时，它是 Speed 参数的唯一写入者；AI 仅在没有启用的 Driver 时兜底。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(DragonAI))]
public class DragonAnimatorDriver : MonoBehaviour
{
    [Header("引用")]
    [Tooltip("身体 Animator，不是眨眼 Animator。留空优先寻找带速度参数的 Animator。")]
    public Animator animator;
    public string speedParameter = "Speed";

    [Header("动画参数")]
    [Tooltip("低于这个实际水平速度时播放待机，单位为米/秒。")]
    public float speedDeadZone = 0.05f;
    public float maxSpeedClamp = 12f;

    [Header("调试")]
    public bool logSpeed;
    public float logInterval = 1f;

    private DragonAI _ai;
    private int _hash;
    private string _checkedParameter;
    private Animator _checkedAnimator;
    private RuntimeAnimatorController _checkedController;
    private bool _hasParameter;
    private float _logTimer;

    private void Awake()
    {
        ResolveReferences();
    }

    private void Start()
    {
        ResolveReferences();
    }

    private void LateUpdate()
    {
        WriteAnimation(Time.deltaTime);
    }

    /// <summary>写入一次动画参数；供手动 Tick 的隔离验证在移动之后调用。</summary>
    public void WriteAnimation(float deltaTime)
    {
        ResolveReferences();
        if (_ai == null || animator == null || !_hasParameter) return;
        float speed = _ai.MoveSpeed;
        if (speed < Mathf.Max(0f, speedDeadZone)) speed = 0f;
        speed = Mathf.Clamp(speed, 0f, Mathf.Max(0f, maxSpeedClamp));
        animator.SetFloat(_hash, speed);

        if (!logSpeed) return;
        _logTimer += Mathf.Max(0f, deltaTime);
        if (_logTimer < Mathf.Max(0.1f, logInterval)) return;
        _logTimer = 0f;
        Debug.Log("[DragonAnimatorDriver] " + name + " 状态=" + _ai.CurrentState +
                  "，实际水平速度=" + speed.ToString("F2") + " 米/秒", this);
    }

    private void ResolveReferences()
    {
        if (_ai == null) _ai = GetComponent<DragonAI>();
        bool parameterChanged = _checkedParameter != speedParameter;
        if (parameterChanged)
        {
            _checkedParameter = speedParameter;
            _hash = Animator.StringToHash(speedParameter ?? string.Empty);
        }
        if (animator == null)
            animator = _ai != null && _ai.animator != null
                ? _ai.animator : DragonAI.FindBodyAnimator(transform, _hash);
        if (animator == null)
        {
            _hasParameter = false;
            return;
        }
        if (animator.applyRootMotion) animator.applyRootMotion = false;
        if (parameterChanged || _checkedAnimator != animator ||
            _checkedController != animator.runtimeAnimatorController)
        {
            _checkedAnimator = animator;
            _checkedController = animator.runtimeAnimatorController;
            _hasParameter = DragonAI.HasFloatParameter(animator, _hash);
        }
    }
}
