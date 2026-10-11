using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class UI_Restart : MonoBehaviour
{
    [Header("重启面板（启动时显示）")]
    public GameObject RestartPannel;

    [Header("其他面板（启动时隐藏，用下方菜单增删）")]
    public List<GameObject> otherPannels = new List<GameObject>();

    // ---------- 运行时逻辑 ----------
    void Awake()
    {
        if (RestartPannel != null)
            RestartPannel.SetActive(true);

        foreach (var p in otherPannels)
            if (p != null) p.SetActive(false); 
    }

    // ---------- 编辑器菜单（Inspector 齿轮里出现） ----------
    [ContextMenu("➕ 添加一个面板槽位")]
    private void AddPanelSlot()
    {
        otherPannels.Add(null);   // 新增一行空槽位，然后手动拖进去
        Debug.Log($"[UI_Restart] 已添加槽位，当前共 {otherPannels.Count} 个");
    }

    [ContextMenu("➖ 删除最后一个槽位")]
    private void RemoveLastSlot()
    {
        if (otherPannels.Count > 0)
        {
            otherPannels.RemoveAt(otherPannels.Count - 1);
            Debug.Log($"[UI_Restart] 已删除，剩余 {otherPannels.Count} 个");
        }
        else
        {
            Debug.LogWarning("[UI_Restart] 已经没有可删除的槽位了");
        }
    }

    [ContextMenu("🧹 清空全部槽位")]
    private void ClearAllSlots()
    {
        otherPannels.Clear();
        Debug.Log("[UI_Restart] 已清空");
    }

    [ContextMenu("🔍 自动收集所有子物体作为面板")]
    private void AutoCollectChildren()
    {
        otherPannels.Clear();
        for (int i = 0; i < transform.childCount; i++)
            otherPannels.Add(transform.GetChild(i).gameObject);
        Debug.Log($"[UI_Restart] 已收集 {otherPannels.Count} 个子物体");

    }
}