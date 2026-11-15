// 这个文件里有：
//   PlayerReviveTests（测试类）：死亡与复活的状态机用例（R041）

using NBC.Game.Player;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    // 主要功能：验证「死亡后复活」能把状态机从 Die 拉回 Idle，并恢复移动与输入响应。
    // 主要数据成员：m_motion（假位移）、m_context（共享状态）、m_machine（被测状态机）、m_frame（逻辑帧号）。
    // 主要外部方法：各个 [Test] 用例。
    // 谁在用它：Unity Test Runner（EditMode），按 AGENTS.md 4.3 由 AI 代跑。
    public sealed class PlayerReviveTests
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

        // 功能：没死的时候调用复活应被拒绝，且不改变任何状态。
        // 入参：无。
        // 返回：无。
        // 步骤：先 Tick 一帧 → 调 NotifyRevive → 断言返回 false、状态仍是 Idle、没被标记死亡。
        // 上游/下游：PlayerStateMachine.NotifyRevive。
        [Test]
        public void NotifyRevive_WhenAlive_ReturnsFalse()
        {
            Tick();

            Assert.IsFalse(m_machine.NotifyRevive(), "活着的时候复活必须返回 false");
            Assert.AreEqual(EPlayerState.Idle, m_machine.State, "活着调复活不能改状态");
            Assert.IsFalse(m_machine.IsDead, "活着调复活不能把角色标成死亡");
        }

        // 功能：死亡后复活要清掉死亡标记、动作、落地缓冲，并把移动闸门重新打开。
        // 入参：无。
        // 返回：无。
        // 步骤：死亡 → 断言进了 Die → 复活 → 断言返回 true、状态 Idle、参数快照里 Dead/ActionBusy 都是 false、闸门打开。
        // 上游/下游：PlayerStateMachine.NotifyDead / NotifyRevive。
        [Test]
        public void NotifyRevive_AfterDeath_ClearsFlagsAndRestoresMovement()
        {
            Assert.IsTrue(m_machine.NotifyDead(), "第一次死亡应该被接受");
            Assert.AreEqual(EPlayerState.Die, m_machine.State, "死亡后应停在 Die");
            Assert.IsTrue(m_machine.IsDead, "死亡后 IsDead 应为 true");
            Assert.IsFalse(m_motion.MovementEnabled, "死亡期间移动闸门必须关着");

            Assert.IsTrue(m_machine.NotifyRevive(), "死亡后复活应该被接受");

            Assert.IsFalse(m_machine.IsDead, "复活后 IsDead 应为 false");
            Assert.AreEqual(EPlayerState.Idle, m_machine.State, "复活后应回到 Idle");
            Assert.IsFalse(m_machine.Parameters.Dead, "参数快照里的 Dead 必须是 false");
            Assert.IsFalse(m_machine.Parameters.ActionBusy, "复活后不能还挂着动作");
            Assert.AreEqual(0f, m_machine.Parameters.ActionId, 0.001f, "复活后 ActionId 应清零");
            Assert.IsTrue(m_motion.MovementEnabled, "复活后移动闸门要打开");
        }

        // 功能：复活后的第一帧要能把速度归零（不能沿用死亡前的高速）。
        // 入参：无。
        // 返回：无。
        // 步骤：先以冲刺跑起来 → 死亡 → 复活 → 无输入 Tick → 断言速度 0、状态 Idle。
        // 上游/下游：PlayerStateMachine.Tick。
        [Test]
        public void NotifyRevive_ResetsSpeedToZero()
        {
            m_frame++;
            m_machine.Tick(m_frame, new PlayerInput(1f, 0f, false, true, false));
            Assert.AreEqual(8f, m_machine.Parameters.Speed, 0.001f, "前提：冲刺时速度应为 8");

            m_machine.NotifyDead();
            m_machine.NotifyRevive();
            Tick();

            Assert.AreEqual(0f, m_machine.Parameters.Speed, 0.001f, "复活后静止站立速度必须归零");
            Assert.AreEqual(0f, m_motion.CurrentSpeed, 0.001f, "下发给位移层的速度也必须归零");
        }

        // 功能：复活后要能重新响应方向输入（不是变成死循环里的 Idle）。
        // 入参：无。
        // 返回：无。
        // 步骤：死亡 → 复活 → 按前进 Tick → 断言进入 Run、速度为 5。
        // 上游/下游：PlayerStateMachine.NotifyRevive / Tick。
        [Test]
        public void NotifyRevive_AllowsInputAgain()
        {
            m_machine.NotifyDead();
            m_machine.NotifyRevive();

            Tick(0f, 1f);

            Assert.AreEqual(EPlayerState.Run, m_machine.State, "复活后按前进应进入 Run");
            Assert.AreEqual(5f, m_machine.Parameters.Speed, 0.001f, "默认跑速应为 5 米/秒");
        }
    }
}
