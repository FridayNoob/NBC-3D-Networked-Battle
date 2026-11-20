// 这个文件里有：
//   BattlePresenceTests：验证「进副本 / 回世界」两个入口把四个开关打到同一个方向。

using NBC.Game.Bootstrap;
using NUnit.Framework;

namespace NBC.Tests.EditMode.Game
{
    // 功能：锁住 BattlePresence 的进出语义（2026-11-20「回世界后还挂在房间里」的回归测试）。
    // 主要数据成员：无（每个用例自建实例）。
    // 主要外部方法：四个 [Test]。
    // 谁在用它：EditMode 测试套件。
    public sealed class BattlePresenceTests
    {
        // 功能：新建出来时应是「允许自动进房、活着、在副本里」的默认态。
        // 入参：无。
        // 返回：无。
        [Test]
        public void Default_AllowsAutoJoinAndStartsAlive()
        {
            BattlePresence presence = new BattlePresence();

            Assert.IsTrue(presence.AutoJoin, "默认应允许自动进房");
            Assert.IsTrue(presence.HeroAlive, "默认按活着算，避免开局误判死亡");
            Assert.IsTrue(presence.InDungeon, "默认按在副本里");
            Assert.IsFalse(presence.JoinRequested, "还没发过进房请求");
        }

        // 功能：进副本要把「自动进房 + 表现层 + 活着」一起打开，并清掉上次的请求标记。
        // 入参：无。
        // 返回：无。
        [Test]
        public void EnterDungeon_TurnsEverythingOn()
        {
            BattlePresence presence = new BattlePresence();

            presence.ExitToWorld();
            presence.MarkJoinRequested();
            presence.SetHeroAlive(false);
            presence.EnterDungeon();

            Assert.IsTrue(presence.AutoJoin, "进副本要重新允许自动进房");
            Assert.IsTrue(presence.InDungeon, "进副本要打开表现层");
            Assert.IsTrue(presence.HeroAlive, "进副本先按活着算");
            Assert.IsFalse(presence.JoinRequested, "进副本要重新允许发进房请求");
        }

        // 功能：回世界必须同时关掉自动进房与表现层，并把存活复位（死只属于副本）。
        // 入参：无。
        // 返回：无。
        [Test]
        public void ExitToWorld_TurnsEverythingOff()
        {
            BattlePresence presence = new BattlePresence();

            presence.SetHeroAlive(false);
            presence.ExitToWorld();

            Assert.IsFalse(presence.AutoJoin, "回世界后不许再自动进房");
            Assert.IsFalse(presence.InDungeon, "回世界后表现层要关掉");
            Assert.IsTrue(presence.HeroAlive, "回世界要把存活复位，否则 World 里会再死一次");
            Assert.IsFalse(presence.JoinRequested, "回世界后请求标记要清掉");
        }

        // 功能：连续调用 ExitToWorld 结果必须一样（幂等），避免场景反复切换时状态漂移。
        // 入参：无。
        // 返回：无。
        [Test]
        public void ExitToWorld_IsIdempotent()
        {
            BattlePresence presence = new BattlePresence();

            presence.ExitToWorld();
            string first = presence.ToString();
            presence.ExitToWorld();

            Assert.AreEqual(first, presence.ToString(), "第二次回世界不应改变任何开关");
        }

        // 功能：ToString 要能让诊断日志一眼看出四个开关（回世界后 autoJoin 必须是 False）。
        // 入参：无。
        // 返回：无。
        [Test]
        public void ToString_ShowsAllFourSwitches()
        {
            BattlePresence presence = new BattlePresence();

            presence.ExitToWorld();

            StringAssert.Contains("autoJoin=False", presence.ToString());
            StringAssert.Contains("inDungeon=False", presence.ToString());
            StringAssert.Contains("heroAlive=True", presence.ToString());
        }
    }
}
