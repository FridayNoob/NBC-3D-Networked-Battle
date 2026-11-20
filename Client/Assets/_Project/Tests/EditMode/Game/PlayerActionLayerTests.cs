// 这个文件里有：
//   PlayerActionLayerTests（测试类）：动作层（连击/技能/冲刺/格挡/姿势）的 EditMode 用例

using NBC.Game.Player;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    // 主要功能：验证动作层的排队规则、时长、格挡常驻与姿势轮换，全在纯逻辑层跑，不需要打开场景。
    // 主要数据成员：m_motion（假位移）、m_context（共享状态）、m_machine（被测状态机）、m_frame（逻辑帧号）。
    // 主要外部方法：各个 [Test] 用例。
    // 谁在用它：Unity Test Runner（EditMode）；按 AGENTS.md 4.3，由 AI 代跑并写回 05。
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
        // 上游/下游：技能与姿势用例调用（不涉及连击）。
        private void AdvanceTo(int targetFrame)
        {
            while (m_context.ActionBusy && m_context.ActionFrame < targetFrame)
            {
                Tick();
            }
        }

        // 功能：精确推进指定帧数。
        // 入参：frames 要推进的逻辑帧数。
        // 返回：无。
        // 步骤：循环调用 Tick；连击用例必须用「精确帧数」而不是「推进到某帧」，
        //      否则第 1 段结束接上第 2 段时，循环会继续把第 2 段也推完，断言就落在 Idle 上。
        // 上游/下游：连击用例调用。
        private void TickFrames(int frames)
        {
            for (int i = 0; i < frames; i++)
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

            TickFrames(20);
            Assert.IsTrue(m_machine.NotifyAttack(), "窗口内请求应该被排队");

            TickFrames(40);
            Assert.AreEqual(EPlayerState.Attack2, m_machine.State, "第 1 段结束应当直接接上第 2 段");
            Assert.AreEqual(PlayerActionId.Attack2, m_machine.Parameters.ActionId, 0.001f);

            TickFrames(20);
            Assert.IsTrue(m_machine.NotifyAttack(), "第 2 段窗口内请求应该被排队");

            TickFrames(40);
            Assert.AreEqual(EPlayerState.Attack3, m_machine.State, "第 2 段结束应当直接接上第 3 段");
        }

        // 功能：验证窗口没开时连按不会接下一段。
        // 入参：无。
        // 返回：无。
        // 步骤：起手后立刻再请求 → 断言拒绝 → 推满 60 帧 → 断言回到移动层（无输入 = 站立）。
        // 上游/下游：NUnit 调用。
        [Test]
        public void ComboOutsideWindow_IsRejected()
        {
            Assert.IsTrue(m_machine.NotifyAttack());
            Tick();
            Assert.IsFalse(m_machine.NotifyAttack(), "第 18 帧之前不接受下一段");

            TickFrames(60);
            Assert.AreEqual(EPlayerState.Idle, m_machine.State, "没有排队就连击结束，回移动层");
        }

        // 功能：验证第 3 段之后不会再接第 1 段。
        // 入参：无。
        // 返回：无。
        // 步骤：精确推进打完三段 → 第 3 段窗口内请求断言被拒 → 推满 93 帧断言回移动层。
        // 上游/下游：NUnit 调用。
        [Test]
        public void AfterThirdAttack_DoesNotChain()
        {
            m_machine.NotifyAttack();
            Tick();
            TickFrames(20);
            m_machine.NotifyAttack();
            TickFrames(40);
            TickFrames(20);
            m_machine.NotifyAttack();
            TickFrames(40);
            Assert.AreEqual(EPlayerState.Attack3, m_machine.State);

            TickFrames(20);
            Assert.IsFalse(m_machine.NotifyAttack(), "第 3 段没有下一段");

            TickFrames(73);
            Assert.AreEqual(EPlayerState.Idle, m_machine.State);
        }

        // 功能：验证技能期间禁移动、到点回移动层。
        // 入参：无。
        // 返回：无。
        // 步骤：请求技能 1 → 推一帧断言 Skill1 与禁移动 → 推满 160 帧断言回到跑。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Skill_BlocksMovementForItsDuration()
        {
            Assert.IsTrue(m_machine.NotifySkill(1));
            Tick(0f, 1f);

            Assert.AreEqual(EPlayerState.Skill1, m_machine.State);
            Assert.AreEqual(PlayerActionId.Skill1, m_machine.Parameters.ActionId, 0.001f);
            Assert.IsFalse(m_motion.MovementEnabled);

            AdvanceTo(160);
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

        // 功能：验证按 F 能把姿势从第 1 个轮换到第 7 个、第 8 次回到第 1 个，且参数快照同步变化。
        // 入参：无。
        // 返回：无。
        // 步骤：循环七次「请求姿势 → 推满 90 帧」→ 断言 Parameters.PoseIndex 依次为 0..6；再按一次断言回到 0。
        // 上游/下游：NUnit 调用。
        // 注意：这里必须断言 Parameters.PoseIndex（发给 Animator 的那份），只断言内部字段会漏掉「没写进上下文」的 bug。
        [Test]
        public void Pose_CyclesThroughSevenValues()
        {
            for (int expected = 0; expected <= 6; expected++)
            {
                Assert.IsTrue(m_machine.NotifyPose());
                Tick();
                Assert.AreEqual((float)expected, m_machine.Parameters.PoseIndex, 0.001f, "参数快照里的 PoseIndex 必须同步");
                Assert.AreEqual((expected + 1) % 7, m_machine.PoseIndex, "内部索引已经指向下一个表情");
                AdvanceTo(90);
            }

            Assert.IsTrue(m_machine.NotifyPose());
            Tick();
            Assert.AreEqual(0f, m_machine.Parameters.PoseIndex, 0.001f, "第八次回到第一个表情");
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
