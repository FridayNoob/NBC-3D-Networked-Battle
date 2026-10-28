// 这个文件里有：
//   FollowCamera（类）：相机只跟「位置」，绝不跟「朝向」—— 角色转身时镜头一动不动

using UnityEngine;

namespace NBC.Game.Player
{
    // 主要功能：每帧把相机放在「角色的世界固定偏移」处，平滑追过去，再看住角色胸口高度。
    // 主要数据成员：m_target（跟谁）、m_offset（世界固定偏移）、m_smooth（跟随顺滑程度）。
    // 主要外部方法：无。只有 Unity 的 LateUpdate 回调。
    // 谁在用它：挂在主相机上，Target 槽指向角色；由引擎每帧调用。
    public sealed class FollowCamera : MonoBehaviour
    {
        // 跟着谁：把角色拖进来。留空则不做事，不报错
        [SerializeField] private Transform m_target = null;

        // 世界固定偏移：x 左右，y 高度，z 前后（负数表示在 -Z 方向）
        [SerializeField] private Vector3 m_offset = new Vector3(0f, 4f, -6f);

        // 跟随顺滑程度：0 是死贴（会硬），越大越跟手，8 比较稳
        [SerializeField] private float m_smooth = 8f;

        // 入参：无（引擎每帧调用）
        // 返回：无
        // 步骤：
        //   1) 没设 Target 就返回，避免空引用刷屏
        //   2) 想要的机位 = 角色位置 + **世界固定偏移**（不乘角色旋转）
        //   3) 用 Lerp 从当前机位追向目标机位，系数乘 deltaTime，避免帧率变化导致跟随速度不同
        //   4) 看向角色胸口高度（抬高 1.5 米），不要盯着脚
        // 上游：Unity 引擎每帧调用（在 Update 之后）
        // 下游：Transform.position、Transform.LookAt
        // 注意：**这里不能乘 m_target.rotation**。乘了相机就会跟着角色转身（2026-10-28 负责人明确要求：镜头始终不会跟着转向）
        // 注意：必须用 LateUpdate。放 Update 会拿到角色移动前的位置，画面会抖一下
        private void LateUpdate()
        {
            if (m_target == null)
            {
                return;
            }

            Vector3 desired = m_target.position + m_offset;

            transform.position = Vector3.Lerp(
                transform.position, desired, Mathf.Clamp01(m_smooth * Time.deltaTime));

            transform.LookAt(m_target.position + Vector3.up * 1.5f);
        }
    }
}
