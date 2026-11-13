// 这个文件里有：
//   PlayerSpeedSmootherTests（EditMode 单测）：验证速度平滑器的首次对齐、线性过程、到位、中途换目标

using NUnit.Framework;
using NBC.Game.Player;

namespace NBC.Tests.EditMode
{
    // 主要功能：给 PlayerSpeedSmoother 的每个分支上锁——混合树的顺滑全靠它，错了动画会「顿一下」。
    // 主要数据成员：无。
    // 主要外部方法：五个 Test 方法，每个只验一条规则。
    // 谁在用它：Unity Test Runner 的 EditMode 页签。
    public sealed class PlayerSpeedSmootherTests
    {
        // 一帧 60fps 的时长（秒）
        private const float Frame = 1f / 60f;

        // 一次滑动时长（秒），与 PlayerAnimatorPublisher 的默认值一致
        private const float Duration = 0.12f;

        // 功能：第一次调用不做动画，直接等于目标值。
        // 入参：无。
        // 返回：无。
        // 步骤：新建平滑器喂 5，断言当前值与返回值都是 5。
        // 上游/下游：NUnit 调用。
        [Test]
        public void FirstStepAlignsToTarget()
        {
            PlayerSpeedSmoother smoother = new PlayerSpeedSmoother(Duration);

            float value = smoother.Step(5f, Frame);

            Assert.AreEqual(5f, value, 0.0001f);
            Assert.IsTrue(smoother.IsSettled);
        }

        // 功能：从 0 滑到 5，走一半时间应到 2.5。
        // 入参：无。
        // 返回：无。
        // 步骤：先对齐 0，再喂 5 并推进 0.06 秒。
        // 上游/下游：NUnit 调用。
        [Test]
        public void LinearRampHalfway()
        {
            PlayerSpeedSmoother smoother = new PlayerSpeedSmoother(Duration);
            smoother.Step(0f, Frame);

            float value = smoother.Step(5f, Duration * 0.5f);

            Assert.AreEqual(2.5f, value, 0.0001f);
            Assert.IsFalse(smoother.IsSettled);
        }

        // 功能：走满时长后精确等于目标。
        // 入参：无。
        // 返回：无。
        // 步骤：对齐 0，喂 5 推进满 0.12 秒。
        // 上游/下游：NUnit 调用。
        [Test]
        public void ReachesTargetAfterDuration()
        {
            PlayerSpeedSmoother smoother = new PlayerSpeedSmoother(Duration);
            smoother.Step(0f, Frame);

            float value = smoother.Step(5f, Duration);

            Assert.AreEqual(5f, value, 0.0001f);
            Assert.IsTrue(smoother.IsSettled);
        }

        // 功能：滑到一半改目标，从当前值继续滑，不回起点。
        // 入参：无。
        // 返回：无。
        // 步骤：0→5 滑一半得 2.5，再改成 0 滑一半，应得 1.25。
        // 上游/下游：NUnit 调用。
        [Test]
        public void RetargetStartsFromCurrentValue()
        {
            PlayerSpeedSmoother smoother = new PlayerSpeedSmoother(Duration);
            smoother.Step(0f, Frame);
            smoother.Step(5f, Duration * 0.5f);

            float value = smoother.Step(0f, Duration * 0.5f);

            Assert.AreEqual(1.25f, value, 0.0001f);
        }

        // 功能：deltaSeconds 为 0 时速度不动。
        // 入参：无。
        // 返回：无。
        // 步骤：对齐 0 后喂 5 但推进 0 秒。
        // 上游/下游：NUnit 调用。
        [Test]
        public void ZeroDeltaKeepsValue()
        {
            PlayerSpeedSmoother smoother = new PlayerSpeedSmoother(Duration);
            smoother.Step(0f, Frame);

            float value = smoother.Step(5f, 0f);

            Assert.AreEqual(0f, value, 0.0001f);
        }
    }
}
