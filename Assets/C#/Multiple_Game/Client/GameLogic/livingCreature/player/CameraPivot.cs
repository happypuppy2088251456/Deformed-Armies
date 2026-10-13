using UnityEngine;

public class CameraHeadFollow : MonoBehaviour
{
    [Header("跟随目标")]
    public Transform headBone;        // 头部骨骼
    public Transform playerRoot;      // 角色根节点（用于获取朝向旋转）

    [Header("偏移")]
    public Vector3 positionOffset = new Vector3(0, 0.1f, 0); // 相对头部的偏移

    [Header("平滑参数")]
    public float positionSmooth = 15f;  // 位置跟随平滑度，越大越跟手
    public float rotationSmooth = 10f;  // 旋转跟随平滑度

    private Vector3 _velocity; // SmoothDamp 用

    void LateUpdate()
    {
        if (headBone == null) return;

        // 目标位置 = 头部世界位置 + 偏移
        Vector3 targetPos = headBone.position + positionOffset;

        // 平滑跟随位置（过滤掉高频抖动）
        transform.position = Vector3.SmoothDamp(
            transform.position, targetPos, ref _velocity,
            1f / positionSmooth
        );

        // 旋转只跟随角色根节点的朝向（玩家输入控制
        if (playerRoot != null)
        {
            Quaternion targetRot = playerRoot.rotation;
            transform.rotation = Quaternion.Slerp(
                transform.rotation, targetRot, Time.deltaTime * rotationSmooth
            );
        }
    }
}