// 这个文件里有：
// 1) PlayerSkinnedMeshGuard：把玩家身上所有蒙皮网格改成「每帧重算包围盒」，避免大动作时网格被剔除一帧。

using UnityEngine;

namespace NBC.Game.Player
{
    // 主要功能：解决「某些表情/动作播到一半，角色的剑或衣服突然消失一下」的问题。
    // 主要数据成员：m_logApplied 决定是否在 Console 说明改了几个网格。
    // 主要外部方法：没有对外方法，挂上就在 Awake 里自动处理子物体上的 SkinnedMeshRenderer。
    // 谁在用它：PlayerBrain.Awake 会给玩家根节点自动补上这个组件。
    //
    // 为什么需要它：
    // 蒙皮网格默认用导入时烘焙的一份包围盒做剔除（updateWhenOffscreen = false）。
    // 这份包围盒是按绑定姿势算的，玩家抬高手臂、挥剑、摆表情时，剑和衣摆会跑到盒子外面，
    // 渲染器就整块被剔除一帧，肉眼看起来就是「闪了一下」。
    // 改成 true 后 Unity 每帧按当前骨骼重算包围盒，代价是一点 CPU，换来画面不闪。
    [DisallowMultipleComponent]
    public sealed class PlayerSkinnedMeshGuard : MonoBehaviour
    {
        // 是否在 Console 打印「改了几个网格」，排查时打开，平时关掉
        [SerializeField]
        private bool m_logApplied = false;

        // 功能：挂载时把子物体上的蒙皮网格全部改成逐帧算包围盒。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 连同未激活的子物体一起找 SkinnedMeshRenderer 2) 把 updateWhenOffscreen 打开 3) 按需打一行日志。
        // 上游/下游：Unity 生命周期；下游 SkinnedMeshRenderer.updateWhenOffscreen。
        private void Awake()
        {
            Apply();
        }

        // 功能：执行一次包围盒策略切换，可重复调用（已经是 true 的会跳过）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 收集所有蒙皮网格 2) 逐个打开 updateWhenOffscreen 并计数 3) 计数大于 0 且开了日志才打印。
        // 上游/下游：Awake 调用；下游 SkinnedMeshRenderer.updateWhenOffscreen。
        public void Apply()
        {
            SkinnedMeshRenderer[] renderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int changed = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                if (renderer == null || renderer.updateWhenOffscreen)
                {
                    continue;
                }

                renderer.updateWhenOffscreen = true;
                changed++;
            }

            if (m_logApplied && changed > 0)
            {
                Debug.Log("[PlayerSkinnedMeshGuard] 已把 " + changed + " 个蒙皮网格改成逐帧算包围盒，避免大动作时整块网格被剔除。", this);
            }
        }
    }
}
