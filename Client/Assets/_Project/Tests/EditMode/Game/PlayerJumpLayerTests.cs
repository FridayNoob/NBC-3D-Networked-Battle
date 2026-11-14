// 这个文件里有：
//   PlayerJumpLayerTests（测试类）：R035d 的跳跃 / 下落 / 落地缓冲用例，以及 PlayerVerticalMotion 的纯逻辑用例

using NBC.Game.Player;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    // 主要功能：验证「离地选 Jump/Fall、落地缓冲禁移动、空中不能改向、动作中不能起跳」以及竖直积分的高度与滞空时间。
    // 主要数据成员：m_motion（假位移）、m_context（共享状态）、m_machine（状态机）、m_frame（帧号）。
    // 主要外部方法：SetUp 重建机器；Tick 推一帧。
    // 谁在用它：NUnit 测试运行器（AI 按 AGENTS.md 4.3 代跑）。
    public sealed class PlayerJumpLayerTests
    {
        // 假位移：只记闸门与速度，不碰 Transform
        private FakeMotion m_motion;

        // 共享状态
        private PlayerStateContext m_context;

        // 被测状态机
        private PlayerStateMachine m_machine;

        // 逻辑帧号（必须单调递增）
        private int m_frame;

        // 功能：每个用例前重建机器。
        // 入参：无。
        // 返回：无。
        // 步骤：建假位移 → 上下文 → 默认帧数（末位 12 是落地缓冲）→ 默认五档速度。
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

        // 功能：推一帧逻辑。
        // 入参：x/z 方向输入。
        // 返回：无。
        // 步骤：帧号 +1 后调 Tick。
        // 上游/下游：各用例调用。
        private void Tick(float x, float z)
        {
            m_frame++;
            m_machine.Tick(m_frame, new PlayerInput(x, z, false, false, false));
        }

        // 功能：起跳后这一帧应该选 Jump，并且位移被关掉。
        // 入参：无。
        // 返回：无。
        // 步骤：先落地一帧 → NotifyJump → 推一帧 → 断言状态与闸门。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Jump_SelectsJumpStateAndDisablesMovement()
        {
            Tick(0f, 1f);
            Assert.IsTrue(m_machine.NotifyJump(), "站在地上应该允许起跳");

            Tick(0f, 1f);

            Assert.AreEqual(EPlayerState.Jump, m_machine.State);
            Assert.IsFalse(m_machine.Parameters.Grounded, "空中参数必须是 Grounded=false");
            Assert.IsFalse(m_motion.MovementEnabled, "空中不能有水平位移");
            Assert.AreEqual(0f, m_machine.Parameters.Speed, 0.001f, "空中速度必须是 0");
        }

        // 功能：空中按住 W 也不能改向（速度仍为 0、状态仍是 Jump/Fall）。
        // 入参：无。
        // 返回：无。
        // 步骤：起跳 → 连续推 5 帧带 W → 断言状态属于空中两态且速度为 0。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Airborne_IgnoresDirectionInput()
        {
            m_machine.NotifyJump();

            for (int i = 0; i < 5; i++)
            {
                Tick(1f, 1f);
            }

            Assert.IsTrue(m_machine.State == EPlayerState.Jump || m_machine.State == EPlayerState.Fall, "空中只能处于 Jump 或 Fall");
            Assert.AreEqual(0f, m_machine.Parameters.Speed, 0.001f);
        }

        // 功能：竖直速度转负后应该选 Fall。
        // 入参：无。
        // 返回：无。
        // 步骤：起跳 → SetVertical(false, -2) → 推一帧 → 断言 Fall。
        // 上游/下游：NUnit 调用。
        [Test]
        public void FallingVerticalSpeed_SelectsFall()
        {
            m_machine.NotifyJump();
            Tick(0f, 0f);

            m_machine.SetVertical(false, -2f);
            Tick(0f, 0f);

            Assert.AreEqual(EPlayerState.Fall, m_machine.State);
        }

        // 功能：落地后先停在 Land，缓冲 12 帧过完才回移动层。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 起跳再落地 2) 断言 Land 且闸门关 3) 推 12 帧 4) 断言回 Run。
        // 上游/下游：NUnit 调用。
        [Test]
        public void Landing_BlocksMovementForTwelveFrames()
        {
            m_machine.NotifyJump();
            m_machine.SetVertical(true, 0f);
            Assert.IsTrue(m_machine.NotifyLand());

            Tick(0f, 1f);
            Assert.AreEqual(EPlayerState.Land, m_machine.State);
            Assert.IsFalse(m_motion.MovementEnabled, "落地缓冲期间不能移动");

            for (int i = 0; i < 12; i++)
            {
                Tick(0f, 1f);
            }

            Assert.AreEqual(EPlayerState.Run, m_machine.State, "缓冲结束后应回到移动层");
            Assert.IsTrue(m_motion.MovementEnabled);
        }

        // 功能：动作期间不能起跳。
        // 入参：无。
        // 返回：无。
        // 步骤：起手攻击 → NotifyJump → 断言 false。
        // 上游/下游：NUnit 调用。
        [Test]
        public void NotifyJump_RejectedWhileActionBusy()
        {
            Assert.IsTrue(m_machine.NotifyAttack());
            Assert.IsFalse(m_machine.NotifyJump(), "播动作时不能起跳");
        }

        // 功能：死亡后不能起跳。
        // 入参：无。
        // 返回：无。
        // 步骤：NotifyDead → NotifyJump → 断言 false。
        // 上游/下游：NUnit 调用。
        [Test]
        public void NotifyJump_RejectedAfterDeath()
        {
            m_machine.NotifyDead();
            Assert.IsFalse(m_machine.NotifyJump(), "死亡后不能起跳");
        }

        // 功能：在地面上重复请求落地要被拒绝（避免每帧重开缓冲）。
        // 入参：无。
        // 返回：无。
        // 步骤：先 NotifyLand 一次 → 再 NotifyLand → 断言第二次 false。
        // 上游/下游：NUnit 调用。
        [Test]
        public void NotifyLand_RejectsRepeatedCalls()
        {
            Assert.IsTrue(m_machine.NotifyLand());
            Assert.IsFalse(m_machine.NotifyLand());
        }

        // 功能：竖直积分要跳够约 0.9 米、滞空约 0.86 秒。
        // 入参：无。
        // 返回：无。
        // 步骤：按 1/60 秒推进，记录最大高度与空中帧数。
        // 上游/下游：NUnit 调用。
        [Test]
        public void VerticalMotion_ReachesTargetHeightAndLands()
        {
            PlayerVerticalMotion motion = new PlayerVerticalMotion();
            Assert.IsTrue(motion.Jump(0.9f));

            float maxHeight = 0f;
            int airFrames = 0;
            for (int i = 0; i < 600 && !motion.Grounded; i++)
            {
                motion.Step(1f / 60f);
                airFrames++;
                if (motion.Height > maxHeight)
                {
                    maxHeight = motion.Height;
                }
            }

            Assert.IsTrue(motion.Grounded, "0.9 米的跳跃必须在 10 秒内落地");
            Assert.AreEqual(0.9f, maxHeight, 0.05f, "最高点应接近 0.9 米");
            Assert.AreEqual(52, airFrames, 3, "0.9 米跳高约 0.86 秒 = 52 帧");
        }

        // 功能：空中不能再跳一次。
        // 入参：无。
        // 返回：无。
        // 步骤：第一次 Jump 成功 → 第二次 Jump 断言 false。
        // 上游/下游：NUnit 调用。
        [Test]
        public void VerticalMotion_RejectsSecondJumpInAir()
        {
            PlayerVerticalMotion motion = new PlayerVerticalMotion();
            Assert.IsTrue(motion.Jump(0.9f));
            Assert.IsFalse(motion.Jump(0.9f));
        }

        // 假位移：只记录闸门与最近速度，不碰 Transform。
        private sealed class FakeMotion : IPlayerMotion
        {
            // 位移是否开着
            public bool MovementEnabled { get; set; } = true;

            // 最近一次写入的移动速度，米/秒
            public float CurrentSpeed { get; private set; }

            // 功能：记录速度。
            // 入参：metersPerSecond 米/秒。
            // 返回：无。
            // 步骤：直接赋值。
            // 上游/下游：PlayerStateMachine.Tick 调用。
            public void SetMoveSpeed(float metersPerSecond)
            {
                CurrentSpeed = metersPerSecond;
            }
        }
    }
}
