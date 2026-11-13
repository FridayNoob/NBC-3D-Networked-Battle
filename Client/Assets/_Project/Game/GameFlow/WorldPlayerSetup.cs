// 这个文件里有：
//   WorldPlayerSetup（MonoBehaviour）：把 World 里的玩家接到跟随相机上，并让他贴着地面走。
//
// 为什么需要它：World 场景原来只有观察相机（WorldOrbitCamera）；有了玩家之后，
// 相机要跟人走，人还要贴地形高度 —— 这两件事不能写死在相机/玩家各自的脚本里。

using NBC.Game.Player;
using UnityEngine;

namespace NBC.Game.GameFlow
{
    // 主要功能：找到玩家 → 把跟随相机的目标设成他 → 关掉观察相机 → 每帧把人贴到地面。
    // 主要数据成员：m_player（玩家根节点）、m_followCamera、m_orbitCamera、m_groundMask（哪些层算地面）。
    // 主要外部方法：Unity 的 Start / LateUpdate。
    // 谁在用它：World 场景里的玩家对象（[Player_Hero]）。
    // 注意：贴地用的是向下射线；地形/房子都要有碰撞体，否则人会穿下去。
    public sealed class WorldPlayerSetup : MonoBehaviour
    {
        // 玩家根节点（留空 = 用挂这个组件的对象）
        [SerializeField]
        private Transform m_player;

        // 跟随相机（World 场景的 Main Camera）
        [SerializeField]
        private FollowCamera m_followCamera;

        // 观察相机（没有玩家时用的那个；有玩家就关掉它）
        [SerializeField]
        private WorldOrbitCamera m_orbitCamera;

        // 贴地射线的层遮罩（默认全部）
        [SerializeField]
        private LayerMask m_groundMask = ~0;

        // 贴地射线的起点高度（米，够高才能打到地面）
        [SerializeField]
        private float m_probeHeight = 5f;

        // 贴地射线的最长距离（米）
        [SerializeField]
        private float m_probeDistance = 50f;

        // 功能：启动时接线：相机跟人、关观察相机。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 玩家没配就用自己
        //   2) 关掉观察相机（避免两套相机抢着写位置）
        //   3) 把跟随相机的目标设成玩家
        // 上游：Unity
        // 下游：FollowCamera / WorldOrbitCamera
        private void Start()
        {
            if (m_player == null)
            {
                m_player = transform;
            }

            if (m_orbitCamera != null)
            {
                m_orbitCamera.enabled = false;
            }

            if (m_followCamera != null)
            {
                m_followCamera.SetTarget(m_player);
            }
            else
            {
                Debug.LogWarning("[WorldPlayerSetup] 没配跟随相机，画面会停在原地");
            }

            Debug.Log("[WorldPlayerSetup] 玩家已就位：" + m_player.name + "，跟随相机=" + (m_followCamera != null));
        }

        // 功能：每帧把玩家贴到地面（地形有起伏，不贴会浮空/陷地）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 从玩家上方往下打一条射线
        //   2) 打到地面就把 y 设成命中点（保留 x/z）
        // 上游：Unity
        // 下游：Transform.position
        // 注意：只改 y；水平移动仍由 PlayerMove 负责
        private void LateUpdate()
        {
            if (m_player == null)
            {
                return;
            }

            Vector3 origin = m_player.position + Vector3.up * m_probeHeight;

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, m_probeDistance, m_groundMask, QueryTriggerInteraction.Ignore))
            {
                Vector3 position = m_player.position;
                position.y = hit.point.y;
                m_player.position = position;
            }
        }
    }
}
