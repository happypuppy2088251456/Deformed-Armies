using UnityEngine;

/// <summary>
/// UI 遮罩开关：用来在进入游戏后把整套 UI 藏起来，避免 UI 一直盖在游戏画面上。
///
/// 背景：项目里的面板（Select_Server_Or_Client / Server_Start_Listen /
/// Client_Connect_IP_Input）是靠 SetActive 互相切换的，但没有任何一处代码
/// 会把整个 Canvas 关掉，所以选完服务器/客户端之后 UI 会一直挡着画面。
///
/// 用法：把这个脚本挂在 Canvas 上（或任意空物体上，然后把 uiRoot 拖成 Canvas）。
/// </summary>
public class UIOverlayToggle : MonoBehaviour
{
    [Header("要开关的 UI 根节点")]
    [Tooltip("留空则自动往上找最近的 Canvas。游戏里的 UI 通常整个都挂在 Canvas 下，所以关掉它就等于关掉全部 UI。")]
    [SerializeField] private GameObject uiRoot;

    [Header("行为")]
    [Tooltip("勾选后：进入 Play 时自动隐藏 UI，直接就能看到游戏画面。")]
    [SerializeField] private bool hideOnStart = true;

    [Tooltip("允许按 Esc 切换 UI 的显示/隐藏。因为 Input.GetKeyDown 只在 Update 里有效，" +
             "所以编辑器里不按 Esc 时它没有任何作用。")]
    [SerializeField] private bool escapeToToggle = true;

    private void Awake()
    {
        // 没手动指定时，自动找最近的 Canvas
        if (uiRoot == null)
        {
            Canvas canvas = GetComponentInParent<Canvas>();

            // 再兜底一次：从自己往下找
            if (canvas == null)
                canvas = GetComponentInChildren<Canvas>();

            if (canvas != null)
                uiRoot = canvas.gameObject;
        }
    }

    private void Start()
    {
        if (uiRoot == null)
        {
            Debug.LogWarning("[UIOverlayToggle] 没有找到要控制的 UI 根节点，请在 Inspector 里手动指定 uiRoot。", this);
            return;
        }

        if (hideOnStart)
            HideUI();
    }

    private void Update()
    {
        if (escapeToToggle && Input.GetKeyDown(KeyCode.Escape))
            ToggleUI();
    }

    /// <summary>显示 UI。</summary>
    public void ShowUI()
    {
        if (uiRoot != null) uiRoot.SetActive(true);
    }

    /// <summary>隐藏 UI。</summary>
    public void HideUI()
    {
        if (uiRoot != null) uiRoot.SetActive(false);
    }

    /// <summary>切换 UI 的显示状态。</summary>
    public void ToggleUI()
    {
        if (uiRoot == null) return;

        bool willShow = !uiRoot.activeSelf;
        uiRoot.SetActive(willShow);

        Debug.Log($"[UIOverlayToggle] UI 已{(willShow ? "显示" : "隐藏")}（按 Esc 可切换）");
    }
}
