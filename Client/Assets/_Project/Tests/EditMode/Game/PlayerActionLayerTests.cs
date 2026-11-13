// 这个文件里有：
//   PlayerActionLayerTests（测试类）：动作层（连击/技能/冲刺/格挡/姿势）的 EditMode 用例

using NBC.Game.Player;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    // 主要功能：验证动作层的排队规则、时长、格挡常驻与姿势轮换，全在纯逻辑层跑，不需要打开场景。
    // 主要数据成员：m_motion（假位移）、m_context（共享状态）、m_machine（被测状态机）、m_frame（逻辑帧号）。
    // 主要外部方法：各个 [Test] 用例。
    // 谁在用它：Unity Test Runner（EditMode）；AI 不能代跑，由负责人执行。
    public sealed class PlayerActionLayerTests
    {
        // 假位移：只记录闸门与速度，不做真实位移
        private sealed class FakeMotion : IPlayerMotion
        {
            // 位移闸门
            private bool m_enabled = true;

            // 功能：读写位移闸门。
            // 入参：true 允许移动。
            // 返回：当前闸门。
            // 步骤：直接存取字段。
            // 上游/下游：状态机每帧写。
            public bool MovementEnabled
            {
                get { return m_enabled; }
                set { m_enabled = value; }
            }

            // 当前速度（米/秒）
            public float CurrentSpeed { get; private set; }

            // 功能：记录状态机要求的速度。
            // 入参：metersPerSecond 米/秒。
            // 返回：无。
            // 步骤：直接写 CurrentSpeed。
            // 上游/下游：状态机每帧调用。
            public void SetMoveSpeed(float metersPerSecond)
            {
                CurrentSpeed = metersPerSecond;
            }
        }

        // 假位移
        private FakeMotion m_motion;

        // 共享状态
        private PlayerStateContext m_context;

        // 被测状态机
        private PlayerStateMachine m_machine;

        // 逻辑帧号
        private int m_frame;

        // 功能：每个用例前重建机器。
        // 入参：无。
        // 返回：无。
        // 步骤：建假位移 → 建上下文 → 用 PlayerTimings.Default 与默认档位建机器。
        // 上游/下游：NUnit 调用。
        [SetUp]
        public void SetUp()
        {
            m_motion = new FakeMotion();
            m_context = new PlayerStateContext(m_motion);
            m_machine = new PlayerStateMachine(
                m_context,
                PlayerTimings.Default,
                new PlayerSpeeds(2f, 5f, 8f, 1.5f, 3f));
            m_frame = 0;
        }

        // 功能：推进一帧逻辑。
        // 入参：x/z 方向；sprint 按住。
        // 返回：无。
        // 步骤：帧号 +1 后调 Tick。
        // 上游/下游：各用例调用。
        private void Tick(float x = 0f, float z = 0f, bool sprint = false)
        {
            m_frame++;
            m_machine.Tick(m_frame, new PlayerInput(x, z, false, sprint, false));
        }

        // 功能：推进到动作的指定帧（含当前帧）。
        // 入参：targetFrame 目标帧（从进入动作算起）。
        // 返回：无。
        // 步骤：循环 Tick 直到 context.ActionFrame 达到目标或动作结束。
        // 上游/下游：连击用例调用。
        private void AdvanceTo(int targetFrame)
        {
            while (m_context.ActionBusy && m_context.ActionFrame < targetFrame)
            {
                Tick();
            }
        }

        // 功能：验证连按三次能打完三段攻击。
        // 入参：无。
        // 返回：无。
        // 步骤：起手 → 推到窗口内 → 再请求 → 等第 1 段结束断言第 2 段 → 同样进第 3 段。
        // 上游/下游：NUnit 调用。
        [Test]
        public void ComboChain_ReachesThirdAttack()
        {
            Assert.IsTrue(m_machine.NotifyAttack());
            Tick();
            Assert.AreEqual(EPlayerState.Attack1, m_machine.State);

            AdvanceTo(20);
            Assert.IsTrue(m_machine.NotifyAttack(), "窗口内请求应该被排队");

            AdvanceTo(60);
            Tick();
            Assert.AreEqual(EPlayerState.Attack2, m_machine.State);

            AdvanceTo(20);
            Assert.IsTrue(m_machine.NotifyAttack(), "第 2 段窗口内请求应该被排队");

            AdvanceTo(60);
            Tick();
            Assert.AreEqual(EPlayerState.Attack3, m_machine.State);
        }

        // 功能：验证窗口没开时连按不会接下一段。
        // 入参：无。
        // 返回：无。
        // 步骤：起手后立刻再请求 → 断言拒绝 → 等第 1 段结束 → 断言回到移动层。
        // 上游/下游：NUnit 调用。
        [Test]
        public void ComboOutsideWindow_IsRejected()
        {
            Assert.IsTrue(m_machine.NotifyAttack());
            Tick();
            Assert.IsFalse(m_machine.NotifyAttack(), "第 18 帧之前不接受下一段");

            AdvanceTo(60);
            Tick();
            Assert.AreEqual(EPlayerState.Run, m_machine.State);
        }

        // 功能：验证第 3 段之后不会再接第 1 段。
        // 入参：无。
        // 返回：无。
        // 步骤：直接进第 3 段 → 窗口内请求 → 断言排队被拒 → 结束后回移动层。
        // 上游/下游：NUnit 调用。
        [Test]
        public void AfterThirdAttack_DoesNotChain()
        {
            m_machine.NotifyAttack();
            Tick();
            AdvanceTo(20);
            m_machine.NotifyAttack();
            AdvanceTo(60);
            Tick();
            AdvanceTo(20);
            m_machine.NotifyAttack();
            AdvanceTo(60);
            Tick();
            Assert.AreEqual(EPlayerState.Attack3, m_machine.State);

            AdvanceTo(20);
            Assert.IsFalse(m_machine.NotifyAttack(), "第 3 段没有下一段");

            AdvanceTo(93);
            Tick();
            Assert.AreEqual(EPlayerState.Run, m_machine.State);
        }

        // 功能：验证技能期间禁移动、到点回移动层。
        // 入参：无。
        // 返回：无。
        // 步骤：请求技能 1 → 推一帧断言 Skill1 与禁移动 → 推满 240 帧断言回到跑。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Skill_BlocksMovementForItsDuration()
        {
            Assert.IsTrue(m_machine.NotifySkill(1));
            Tick(0f, 1f);

            Assert.AreEqual(EPlayerState.Skill1, m_machine.State);
            Assert.AreEqual(PlayerActionId.Skill1, m_machine.Parameters.ActionId, 0.001f);
            Assert.IsFalse(m_motion.MovementEnabled);

            AdvanceTo(240);
            Tick(0f, 1f);

            Assert.IsFalse(m_machine.Parameters.ActionBusy);
            Assert.AreEqual(EPlayerState.Run, m_machine.State);
        }

        // 功能：验证非法技能序号被拒绝。
        // 入参：无。
        // 返回：无。
        // 步骤：请求 0 与 4 → 断言都返回 false。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Skill_RejectsInvalidIndex()
        {
            Assert.IsFalse(m_machine.NotifySkill(0));
            Assert.IsFalse(m_machine.NotifySkill(4));
        }

        // 功能：验证冲刺会进 Dash 并禁移动。
        // 入参：无。
        // 返回：无。
        // 步骤：请求冲刺 → 推一帧 → 断言 Dash 与禁移动；再请求一次断言拒绝。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Dash_EntersDashAndBlocksMovement()
        {
            Assert.IsTrue(m_machine.NotifyDash());
            Tick();

            Assert.AreEqual(EPlayerState.Dash, m_machine.State);
            Assert.IsFalse(m_motion.MovementEnabled);
            Assert.IsFalse(m_machine.NotifyDash(), "动作期间不能再冲");
        }

        // 功能：验证格挡是常驻动作：不占 ActionBusy、禁移动、期间不能攻击或放技能。
        // 入参：无。
        // 返回：无。
        // 步骤：SetBlocking(true) → 推一帧 → 断言各字段 → 松手断言复原。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Block_HoldsWithoutBusy_AndRejectsActions()
        {
            m_machine.SetBlocking(true);
            Tick(0f, 1f);

            Assert.IsTrue(m_machine.Blocking);
            Assert.AreEqual(PlayerActionId.Block, m_machine.Parameters.ActionId, 0.001f);
            Assert.IsFalse(m_machine.Parameters.ActionBusy);
            Assert.IsFalse(m_motion.MovementEnabled);
            Assert.IsFalse(m_machine.NotifyAttack());
            Assert.IsFalse(m_machine.NotifySkill(1));

            m_machine.SetBlocking(false);
            Tick(0f, 1f);

            Assert.IsFalse(m_machine.Blocking);
            Assert.AreEqual(PlayerActionId.None, m_machine.Parameters.ActionId, 0.001f);
            Assert.IsTrue(m_motion.MovementEnabled);
        }

        // 功能：验证按 F 七次能把姿势轮换一整圈（1~6 再回 0）。
        // 入参：无。
        // 返回：无。
        // 步骤：循环七次「请求姿势 → 推满 90 帧」→ 断言 PoseIndex 依次为 1..6、0。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Pose_CyclesThroughSevenValues()
        {
            for (int expected = 1; expected <= 6; expected++)
            {
                Assert.IsTrue(m_machine.NotifyPose());
                Tick();
                Assert.AreEqual(expected, m_machine.PoseIndex);
                AdvanceTo(90);
            }

            Assert.IsTrue(m_machine.NotifyPose());
            Tick();
            Assert.AreEqual(0, m_machine.PoseIndex, "第七次之后回到 0");
        }

        // 功能：验证蹲着做姿势，结束后回蹲而不是回站立。
        // 入参：无。
        // 返回：无。
        // 步骤：先按 Ctrl 进蹲 → 请求姿势 → 推满 90 帧 → 断言回 CrouchIdle。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Pose_InCrouch_ReturnsToCrouch()
        {
            m_frame++;
            m_machine.Tick(m_frame, new PlayerInput(0f, 0f, false, false, true));
            Assert.AreEqual(EPlayerState.CrouchIdle, m_machine.State);

            Assert.IsTrue(m_machine.NotifyPose());
            Tick();
            AdvanceTo(90);
            Tick();

            Assert.AreEqual(EPlayerState.CrouchIdle, m_machine.State);
        }
    }
}
