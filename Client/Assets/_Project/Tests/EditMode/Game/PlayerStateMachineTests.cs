// 这个文件里有：
//   PlayerStateMachineTests（EditMode 测试）：用假位移驱动分层状态机，断言状态、速度与动画参数

using NBC.Game.Player;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    // 主要功能：验证 R035a 的「输入 → 状态 → 速度 / 移动闸门 / 动画参数」链路。
    // 主要数据成员：m_motion（假位移）、m_context（共享状态）、m_machine（被测状态机）、m_frame（逻辑帧号）。
    // 主要外部方法：SetUp 建机器；Tick 推进一帧；各 Test 方法断言行为。
    // 谁在用它：Unity Test Runner（EditMode）。
    // 注意：全部用浮点容差 0.001 比较速度 —— 速度来自 Inspector 档位，不做字符串或整数断言。
    public sealed class PlayerStateMachineTests
    {
        // 假位移：只记录「闸门开没开」和「最后设置的速度」
        private sealed class FakeMotion : IPlayerMotion
        {
            // 最后一次 SetMoveSpeed 的值，米/秒
            public float LastSpeed;

            // SetMoveSpeed 被调用次数
            public int SetSpeedCalls;

            // 位移闸门
            public bool MovementEnabled { get; set; }

            // 当前速度（测试里就等于最后设置值）
            public float CurrentSpeed
            {
                get { return LastSpeed; }
            }

            // 记录状态机要求的速度
            public void SetMoveSpeed(float metersPerSecond)
            {
                LastSpeed = metersPerSecond;
                SetSpeedCalls++;
            }
        }

        // 假发布器：只记录最后一次参数，用来验证参数发布链路（真发布器要 Animator，EditMode 里不建）
        private sealed class RecordingSink : IPlayerAnimatorSink
        {
            // 最后一次收到的参数
            public PlayerAnimatorParams Last;

            // 收到几次
            public int ApplyCount;

            // 记录参数
            public void Apply(in PlayerAnimatorParams parameters)
            {
                Last = parameters;
                ApplyCount++;
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

        // 功能：每个用例前重建机器，保证互不污染。
        // 入参：无。
        // 返回：无。
        // 步骤：建假位移 → 建上下文 → 用默认档位（2/5/8/1.5/3）与默认帧数（36/18/48/18/45/90）建机器。
        // 上游/下游：NUnit 调用。
        [SetUp]
        public void SetUp()
        {
            m_motion = new FakeMotion();
            m_context = new PlayerStateContext(m_motion);
            m_machine = new PlayerStateMachine(
                m_context,
                new PlayerTimings(36, 18, 48, 18, 45, 90),
                new PlayerSpeeds(2f, 5f, 8f, 1.5f, 3f));
            m_frame = 0;
        }

        // 功能：推进一帧逻辑。
        // 入参：x/z 方向；walk/sprint 按住；crouch 这一帧刚按下。
        // 返回：无。
        // 步骤：帧号 +1 后调 Tick。
        // 上游/下游：各用例调用；下游 PlayerStateMachine.Tick。
        private void Tick(float x, float z, bool walk = false, bool sprint = false, bool crouch = false)
        {
            m_frame++;
            m_machine.Tick(m_frame, new PlayerInput(x, z, walk, sprint, crouch));
        }

        // 功能：验证默认有方向输入就是跑，速度 5 米/秒。
        // 入参：无。
        // 返回：无。
        // 步骤：推一帧 W → 断言状态、上下文速度、参数速度、位移闸门。
        // 上游/下游：NUnit 调用。
        [Test]
        public void DefaultMove_IsRunAtFiveMetersPerSecond()
        {
            Tick(0f, 1f);

            Assert.AreEqual(EPlayerState.Run, m_machine.State);
            Assert.AreEqual(EPlayerLayer.Locomotion, m_machine.Layer);
            Assert.AreEqual(5f, m_context.Speed, 0.001f);
            Assert.AreEqual(5f, m_machine.Parameters.Speed, 0.001f);
            Assert.IsTrue(m_motion.MovementEnabled);
        }

        // 功能：验证按住 Alt 走 2 米/秒。
        // 入参：无。
        // 返回：无。
        // 步骤：W + walk → 断言 Walk 与速度。
        // 上游/下游：NUnit 调用。
        [Test]
        public void WalkHeld_IsWalkAtTwoMetersPerSecond()
        {
            Tick(0f, 1f, walk: true);

            Assert.AreEqual(EPlayerState.Walk, m_machine.State);
            Assert.AreEqual(2f, m_context.Speed, 0.001f);
        }

        // 功能：验证按住 Shift 冲刺 8 米/秒。
        // 入参：无。
        // 返回：无。
        // 步骤：W + sprint → 断言 Sprint 与速度。
        // 上游/下游：NUnit 调用。
        [Test]
        public void SprintHeld_IsSprintAtEightMetersPerSecond()
        {
            Tick(0f, 1f, sprint: true);

            Assert.AreEqual(EPlayerState.Sprint, m_machine.State);
            Assert.AreEqual(8f, m_context.Speed, 0.001f);
        }

        // 功能：验证蹲三态：不动 = CrouchIdle、有方向 = CrouchWalk 1.5、蹲着按 Shift = CrouchJog 3。
        // 入参：无。
        // 返回：无。
        // 步骤：先按一次 Ctrl（toggle 进入蹲）→ 断言站住 → 推方向 → 断言蹲走 → 加 Shift → 断言蹲跑。
        // 上游/下游：NUnit 调用。
        [Test]
        public void CrouchToggle_GivesThreeCrouchStates()
        {
            Tick(0f, 0f, crouch: true);
            Assert.AreEqual(EPlayerState.CrouchIdle, m_machine.State);
            Assert.IsTrue(m_machine.Parameters.Crouch);

            Tick(0f, 1f);
            Assert.AreEqual(EPlayerState.CrouchWalk, m_machine.State);
            Assert.AreEqual(1.5f, m_context.Speed, 0.001f);

            Tick(0f, 1f, sprint: true);
            Assert.AreEqual(EPlayerState.CrouchJog, m_machine.State);
            Assert.AreEqual(3f, m_context.Speed, 0.001f);

            Tick(0f, 0f, crouch: true);
            Assert.AreEqual(EPlayerState.Idle, m_machine.State, "再按一次 Ctrl 应该站起来");
        }

        // 功能：验证攻击期间位移闸门关闭、动作结束后按输入回到跑。
        // 入参：无。
        // 返回：无。
        // 步骤：请求攻击 → 推一帧 → 断言 Attack1/忙碌/禁移动/参数 → 推满 36 帧 → 断言回到 Run。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Attack_BlocksMovementForThirtySixFrames_ThenReturnsToLocomotion()
        {
            m_machine.NotifyAttack();
            Tick(0f, 1f);

            Assert.AreEqual(EPlayerState.Attack1, m_machine.State);
            Assert.AreEqual(EPlayerLayer.Action, m_machine.Layer);
            Assert.IsTrue(m_machine.Parameters.ActionBusy);
            Assert.AreEqual(PlayerActionId.Attack1, m_machine.Parameters.ActionId, 0.001f);
            Assert.IsFalse(m_motion.MovementEnabled);
            Assert.AreEqual(0f, m_context.Speed, 0.001f);

            for (int i = 0; i < 36; i++)
            {
                Tick(0f, 1f);
            }

            Assert.IsFalse(m_machine.Parameters.ActionBusy);
            Assert.AreEqual(EPlayerState.Run, m_machine.State);
            Assert.IsTrue(m_motion.MovementEnabled);
            Assert.AreEqual(5f, m_context.Speed, 0.001f);
        }

        // 功能：验证动作期间按 Shift / Ctrl 不会切层、不会改状态。
        // 入参：无。
        // 返回：无。
        // 步骤：进入攻击 → 带 Shift 推帧 → 断言仍是 Attack1 → 带 Ctrl（toggle）推帧 → 断言仍是 Attack1 且蹲标记没变。
        // 上游/下游：NUnit 调用。
        [Test]
        public void DuringAction_LayerChangesAreIgnored()
        {
            m_machine.NotifyAttack();
            Tick(0f, 0f);

            Tick(0f, 1f, sprint: true);
            Assert.AreEqual(EPlayerState.Attack1, m_machine.State);

            bool crouchBefore = m_machine.Parameters.Crouch;
            Tick(0f, 1f, crouch: true);
            Assert.AreEqual(EPlayerState.Attack1, m_machine.State);
            Assert.AreEqual(crouchBefore, m_machine.Parameters.Crouch, "蹲切换只在允许移动时才生效");
        }

        // 功能：验证受击能打断攻击，并且方向写进 StackSide。
        // 入参：无。
        // 返回：无。
        // 步骤：进入攻击 → 请求左侧受击 → 推一帧 → 断言 HitLeft、ActionId=10、StackSide=-1。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Hit_InterruptsAttack_AndWritesSide()
        {
            m_machine.NotifyAttack();
            Tick(0f, 0f);

            m_machine.NotifyHit(true);
            Tick(0f, 0f);

            Assert.AreEqual(EPlayerState.HitLeft, m_machine.State);
            Assert.AreEqual(EPlayerLayer.Reaction, m_machine.Layer);
            Assert.AreEqual(PlayerActionId.Hit, m_machine.Parameters.ActionId, 0.001f);
            Assert.AreEqual(-1f, m_machine.Parameters.StackSide, 0.001f);
        }

        // 功能：验证死亡是终态：状态停在 Die、参数 Dead=true、之后输入不再生效。
        // 入参：无。
        // 返回：无。
        // 步骤：请求死亡 → 断言立即 Die → 推 30 帧带方向 → 断言仍是 Die 且闸门关闭。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Dead_IsTerminal_AndIgnoresInput()
        {
            m_machine.NotifyDead();

            Assert.IsTrue(m_machine.IsDead);
            Assert.AreEqual(EPlayerState.Die, m_machine.State);
            Assert.IsTrue(m_machine.Parameters.Dead);

            for (int i = 0; i < 30; i++)
            {
                Tick(0f, 1f, sprint: true);
            }

            Assert.AreEqual(EPlayerState.Die, m_machine.State);
            Assert.IsFalse(m_motion.MovementEnabled);
            Assert.AreEqual(0f, m_context.Speed, 0.001f);
        }

        // 功能：验证参数发布链路：假发布器收到与状态机一致的参数。
        // 入参：无。
        // 返回：无。
        // 步骤：推一帧 W → 手动把 Parameters 交给 RecordingSink → 断言速度与 ApplyCount。
        // 上游/下游：NUnit 调用；PlayerBrain 在运行时做同样的事。
        [Test]
        public void Parameters_FlowToAnimatorSink()
        {
            RecordingSink sink = new RecordingSink();
            Tick(0f, 1f);
            sink.Apply(m_machine.Parameters);

            Assert.AreEqual(1, sink.ApplyCount);
            Assert.AreEqual(5f, sink.Last.Speed, 0.001f);
            Assert.IsFalse(sink.Last.ActionBusy);
            Assert.IsFalse(sink.Last.Dead);
        }

        // 功能：验证横向输入写进 Direction，且被夹在 -1~1。
        // 入参：无。
        // 返回：无。
        // 步骤：推一帧 x=2（超界）→ 断言 Direction=1 且状态 Run。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Direction_IsClampedToOne()
        {
            Tick(2f, 0f);

            Assert.AreEqual(EPlayerState.Run, m_machine.State);
            Assert.AreEqual(1f, m_machine.Parameters.Direction, 0.001f);
        }
    }
}
