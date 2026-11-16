// 这个文件里有：
//   PlayerPersist（类）：把玩家根物体标成跨场景保留，关掉只在 World 生效的组件，并给新场景的相机重新接线。
// 为什么要单独一个类：切场景时「保留谁、关掉谁、谁跟谁」是一组固定动作，散落在入口脚本里下次换入口又会重写一遍。

using NBC.Game.Player;
using UnityEngine;

namespace NBC.Game.GameFlow
{
    // 类：玩家跨场景携带工具
    // 主要功能：把玩家根物体交给 DontDestroyOnLoad，关掉只在 World 里成立的组件（贴地、World 相机接线），进新场景后把相机重新绑到玩家。
    // 主要数据成员：无（纯静态方法，不持有状态）。
    // 主要外部方法：MarkPersistent（标记一个玩家根物体可以跨场景存活）、BindCamera（让当前场景的主相机跟住玩家）。
    // 谁在用它：DungeonEntrance 在切到 Battle 之前调用 MarkPersistent、切完之后调用 BindCamera；将来做副本传送门也复用。
    public static class PlayerPersist
    {
        // 功能：让玩家根物体在切场景时不被销毁，并停掉 World 专属逻辑。
        // 入参：playerRoot 玩家根物体（World 里的 [Player_Hero]），不能为空
        // 返回：true 表示已标记；false 表示入参为空
        // 步骤：
        //   1) 空引用直接返回 false，调用方据此提示错误
        //   2) 只有根物体能 DontDestroyOnLoad，挂了父物体就跳过，避免 Unity 警告
        //   3) 关掉 WorldPlayerSetup：它会每帧把玩家按到地面、并接线 World 的相机，副本里由服务器纠偏与副本相机接管
        // 上游/下游：DungeonEntrance.BeginEnter 调用；下游 Unity 场景加载、WorldPlayerSetup.enabled
        public static bool MarkPersistent(GameObject playerRoot)
        {
            if (playerRoot == null)
            {
                return false;
            }

            if (playerRoot.transform.parent == null)
            {
                Object.DontDestroyOnLoad(playerRoot);
            }

            WorldPlayerSetup setup = playerRoot.GetComponent<WorldPlayerSetup>();
            if (setup != null)
            {
                setup.enabled = false;
            }

            return true;
        }

        // 功能：把当前场景的主相机接到玩家身上（副本里的相机跟人）。
        // 入参：playerRoot 玩家根物体，不能为空
        // 返回：无
        // 步骤：
        //   1) 空引用或没有主相机就直接返回，副本里没相机时不该报错
        //   2) 相机上还没有 WorldFollowCamera 就补一个，有就复用
        //   3) 把目标设成玩家、打开它，并关掉旧的 FollowCamera（两套相机跟随同时开会互相抢位置）
        // 上游：DungeonEntrance 在 Battle 场景加载成功之后调用
        // 下游：WorldFollowCamera.SetTarget、FollowCamera.enabled
        public static void BindCamera(GameObject playerRoot)
        {
            if (playerRoot == null)
            {
                return;
            }

            Camera mainCamera = Camera.main;
            if (mainCamera == null)
            {
                return;
            }

            WorldFollowCamera follow = mainCamera.GetComponent<WorldFollowCamera>();
            if (follow == null)
            {
                follow = mainCamera.gameObject.AddComponent<WorldFollowCamera>();
            }

            follow.enabled = true;
            follow.SetTarget(playerRoot.transform);

            FollowCamera legacy = mainCamera.GetComponent<FollowCamera>();
            if (legacy != null)
            {
                legacy.enabled = false;
            }
        }
    }
}
