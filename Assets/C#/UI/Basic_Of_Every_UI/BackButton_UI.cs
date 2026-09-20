using UnityEngine;
using UnityEngine.UI;

public class BaseUI : MonoBehaviour
{
    [Header("返回设置 ")]
    [Tooltip("点击返回后要打开的面板")]
    public GameObject targetPanel;
    public bool deactivateSelf = true;
    [Tooltip("返回按钮（不拖则不自动绑定）")]
    public Button backButton;

    // 初始化绑定，子类 Start 会在此之后执行
    protected void Awake()
    {
        if (backButton != null)
        {
            backButton.onClick.RemoveAllListeners();
            backButton.onClick.AddListener(OnBackClick);
        }
    }
    //返回上一级
    protected void OnBackClick()
    {
        if (targetPanel == null)
        {
            Debug.LogWarning($"[{GetType().Name}] 返回目标为空，请在 Inspector 中拖入 targetPanel！", this);
            return;
        }

        targetPanel.SetActive(true);
        if (deactivateSelf)
            gameObject.SetActive(false);
    }
}