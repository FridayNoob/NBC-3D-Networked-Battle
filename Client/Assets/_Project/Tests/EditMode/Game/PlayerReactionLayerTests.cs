// 这个文件里有：
//   PlayerReactionLayerTests（测试类）：反应层（受击左右/硬直/死亡）的 EditMode 用例

using NBC.Game.Player;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    // 主要功能：验证受击方向、硬直打断与免疫、死亡终态，全部在纯逻辑层跑，不需要打开场景。
    // 主要数据成员：m_motion（假位移）、m_context（共享状态）、m_machine（被测状态机）、m_frame（逻辑帧号）。
    // 主要外部方法：各个 [Test] 用例。
    // 谁在用它：Unity Test Runner（EditMode）；AI 不能代跑，由负责人执行。
    public sealed class PlayerReactionLayerTests
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
        // 入参：x 横向输入；z 前后输入。
        // 返回：无。
        // 步骤：帧号 +1 后调 Tick。
        // 上游/下游：各用例调用。
        private void Tick(float x = 0f, float z = 0f)
        {
            m_frame++;
            m_machine.Tick(m_frame, new PlayerInput(x, z, false, false, false));
        }

        // 功能：一直 Tick 到动作结束（或超过上限）。
        // 入参：maxFrames 上限帧数。
        // 返回：无。
        // 步骤：ActionBusy 为 false 就停；最多跑 maxFrames 帧防止死循环。
        // 上游/下游：硬直/受击结束用例调用。
        private void AdvanceUntilActionEnds(int maxFrames)
        {
            for (int i = 0; i < maxFrames && m_context.ActionBusy; i++)
            {
                Tick();
            }
        }

        // 功能：验证左受击进入 HitLeft 且 StackSide 为 -1。
        // 入参：无。
        // 返回：无。
        // 步骤：请求左受击 → Tick → 断言状态与参数。
        // 上游/下游：NUnit 调用。
        [Test]
        public void HitLeft_EntersHitLeftWithNegativeStackSide()
        {
            Assert.IsTrue(m_machine.NotifyHit(true));
            Tick();

            Assert.AreEqual(EPlayerState.HitLeft, m_machine.State);
            Assert.AreEqual(-1f, m_machine.Parameters.StackSide, 0.001f);
            Assert.AreEqual(PlayerActionId.Hit, m_machine.Parameters.ActionId, 0.001f);
        }

        // 功能：验证右受击进入 HitRight 且 StackSide 为 +1。
        // 入参：无。
        // 返回：无。
        // 步骤：请求右受击 → Tick → 断言状态与参数。
        // 上游/下游：NUnit 调用。
        [Test]
        public void HitRight_EntersHitRightWithPositiveStackSide()
        {
            Assert.IsTrue(m_machine.NotifyHit(false));
            Tick();

            Assert.AreEqual(EPlayerState.HitRight, m_machine.State);
            Assert.AreEqual(1f, m_machine.Parameters.StackSide, 0.001f);
        }

        // 功能：验证受击期间角色不能移动。
        // 入参：无。
        // 返回：无。
        // 步骤：受击 → Tick 带方向输入 → 断言闸门关闭且速度为 0。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Hit_BlocksMovement()
        {
            m_machine.NotifyHit(true);
            Tick();
            Tick(0f, 1f);

            Assert.IsFalse(m_motion.MovementEnabled);
            Assert.AreEqual(0f, m_motion.CurrentSpeed, 0.001f);
        }

        // 功能：验证硬直能打断正在播的攻击。
        // 入参：无。
        // 返回：无。
        // 步骤：起手攻击 → 推进几帧 → 请求硬直 → Tick → 断言进入 Stun。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Stun_InterruptsAttack()
        {
            Assert.IsTrue(m_machine.NotifyAttack());
            Tick();
            Assert.AreEqual(EPlayerState.Attack1, m_machine.State);

            Assert.IsTrue(m_machine.NotifyStun());
            Tick();

            Assert.AreEqual(EPlayerState.Stun, m_machine.State);
            Assert.AreEqual(PlayerActionId.Stun, m_machine.Parameters.ActionId, 0.001f);
            Assert.IsTrue(m_machine.IsStunned);
        }

        // 功能：验证硬直期间免疫受击。
        // 入参：无。
        // 返回：无。
        // 步骤：进入硬直 → 请求受击 → 断言被拒绝且状态不变。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Hit_IsIgnoredWhileStunned()
        {
            m_machine.NotifyStun();
            Tick();

            Assert.IsFalse(m_machine.NotifyHit(true), "硬直期间受击应被拒绝");
            Assert.IsFalse(m_machine.NotifyHit(false), "硬直期间受击应被拒绝");

            Tick();
            Assert.AreEqual(EPlayerState.Stun, m_machine.State);
        }

        // 功能：验证硬直到点后回到移动层（有输入就是 Run）。
        // 入参：无。
        // 返回：无。
        // 步骤：进入硬直 → 一直 Tick 到动作结束 → 断言回移动层。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Stun_EndsAfterStunFrames()
        {
            m_machine.NotifyStun();
            Tick();

            AdvanceUntilActionEnds(PlayerTimings.Default.StunFrames + 5);
            Tick(0f, 1f);

            Assert.IsFalse(m_machine.IsStunned);
            Assert.AreEqual(EPlayerState.Run, m_machine.State);
            Assert.IsTrue(m_motion.MovementEnabled);
        }

        // 功能：验证重复硬直被拒绝。
        // 入参：无。
        // 返回：无。
        // 步骤：进入硬直 → 再请求一次 → 断言返回 false。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Stun_IsRejectedWhileAlreadyStunned()
        {
            Assert.IsTrue(m_machine.NotifyStun());
            Tick();
            Assert.IsFalse(m_machine.NotifyStun());
        }

        // 功能：验证死亡之后所有请求都无效且永远不会离开 Die。
        // 入参：无。
        // 返回：无。
        // 步骤：死亡 → 依次请求攻击/技能/受击/硬直 → Tick 很多帧 → 断言仍在 Die 且参数 Dead 为 true。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Death_RejectsEverythingAndStaysDead()
        {
            Assert.IsTrue(m_machine.NotifyDead());
            Tick();

            Assert.IsTrue(m_machine.IsDead);
            Assert.AreEqual(EPlayerState.Die, m_machine.State);
            Assert.IsTrue(m_machine.Parameters.Dead);

            Assert.IsFalse(m_machine.NotifyAttack());
            Assert.IsFalse(m_machine.NotifySkill(1));
            Assert.IsFalse(m_machine.NotifyDash());
            Assert.IsFalse(m_machine.NotifyPose());
            Assert.IsFalse(m_machine.NotifyHit(true));
            Assert.IsFalse(m_machine.NotifyStun());
            Assert.IsFalse(m_machine.NotifyDead(), "重复死亡应返回 false");

            for (int i = 0; i < 200; i++)
            {
                Tick(0f, 1f);
            }

            Assert.AreEqual(EPlayerState.Die, m_machine.State);
            Assert.IsFalse(m_motion.MovementEnabled);
        }

        // 功能：验证受击中也能被硬直接管（硬直优先级更高）。
        // 入参：无。
        // 返回：无。
        // 步骤：左受击 → Tick → 请求硬直 → Tick → 断言进入 Stun。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Stun_InterruptsHit()
        {
            m_machine.NotifyHit(true);
            Tick();
            Assert.AreEqual(EPlayerState.HitLeft, m_machine.State);

            Assert.IsTrue(m_machine.NotifyStun());
            Tick();

            Assert.AreEqual(EPlayerState.Stun, m_machine.State);
        }
    }
}
