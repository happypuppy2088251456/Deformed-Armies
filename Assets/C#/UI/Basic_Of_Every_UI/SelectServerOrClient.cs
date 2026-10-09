using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 大厅入口：选择“成为主机”或“作为客户端加入”。
/// 两个按钮分别显示对应面板（serverPanel / clientPanel），并隐藏本面板。
/// </summary>
public class SelectServerOrClient : MonoBehaviour
{
    [Header("S/C")]
    public Button StartAsServer;   // 成为主机
    public Button StartAsClient;   // 作为客户端加入

    public GameObject serverPanel;
    public GameObject clientPanel;

    private void Start()
    {
        if (StartAsServer != null)
        {
            StartAsServer.onClick.RemoveAllListeners();
            StartAsServer.onClick.AddListener(() => ShowPanel(serverPanel));
        }

        if (StartAsClient != null)
        {
            StartAsClient.onClick.RemoveAllListeners();
            StartAsClient.onClick.AddListener(() => ShowPanel(clientPanel));
        }
    }

    private void ShowPanel(GameObject target)
    {
        if (target == null)
        {
            Debug.LogWarning($"[{GetType().Name}] 目标面板为空", this);
            return;
        }
        target.SetActive(true);
        gameObject.SetActive(false);
    }
    public void TestClick()
    {
        Debug.Log("按钮被点了");
    }
}
