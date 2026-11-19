// 这个文件里有：
//   BattleBootSequenceTests（测试类）：编排链的顺序、超时、取消与回调次数。

using System.Collections.Generic;
using NBC.Game.Bootstrap;
using NUnit.Framework;

namespace NBC.Tests.EditMode.Game
{
    // 主要功能：用假条件驱动 BattleBootSequence，验证它「按顺序前进、只触发一次、超时会失败」。
    // 主要数据成员：三个可切换的就绪标志、两个动作计数器、日志收集。
    // 主要外部方法：SetUp（复位）、七条 TestXxx 用例。
    // 谁在用它：Test Runner（EditMode）。
    public sealed class BattleBootSequenceTests
    {
        // 假资源层就绪标志
        private bool m_assetsReady;

        // 假配置来源标志
        private bool m_sourceReady;

        // 假配置表就绪标志
        private bool m_tablesReady;

        // PrepareConfigs 被调用了几次
        private int m_prepareCalls;

        // BindHud 被调用了几次
        private int m_bindCalls;

        // Completed 被触发了几次
        private int m_completedCalls;

        // Failed 原因（没失败时为 null）
        private string m_failure;

        // 日志（按顺序）
        private readonly List<string> m_logs = new List<string>();

        // 功能：每条用例前把全部状态复位。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 三个标志置 false 2) 计数清零 3) 清空日志。
        // 上游：NUnit；下游：无。
        [SetUp]
        public void SetUp()
        {
            m_assetsReady = false;
            m_sourceReady = false;
            m_tablesReady = false;
            m_prepareCalls = 0;
            m_bindCalls = 0;
            m_completedCalls = 0;
            m_failure = null;
            m_logs.Clear();
        }

        // 功能：Begin 之后应停在第一步。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 建链 2) Begin 3) 断言 Step 与 IsRunning。
        // 上游：测试驱动；下游：BattleBootSequence.Begin。
        [Test]
        public void Begin_StartsAtWaitAssets()
        {
            BattleBootSequence sequence = NewSequence();
            sequence.Begin();

            Assert.AreEqual(EBootStep.WaitAssets, sequence.Step, "Begin 之后应该停在第一步");
            Assert.IsTrue(sequence.IsRunning, "Begin 之后应在运行中");
            Assert.IsFalse(sequence.IsFinished, "刚 Begin 不该算完成");
        }

        // 功能：依次满足条件后四步应依次走完，并在最后触发一次 Completed。
        // 入参：无。
        // 返回：无。
        // 步骤：1) Begin 2) 逐帧打开资源/来源/表 3) 断言 Step=Done、BindHud 一次、Completed 一次。
        // 上游：测试驱动；下游：Tick。
        [Test]
        public void StepsAdvanceInOrder_WhenConditionsBecomeTrue()
        {
            BattleBootSequence sequence = NewSequence();
            sequence.Begin();
            sequence.Tick(0.1f);

            Assert.AreEqual(EBootStep.WaitAssets, sequence.Step, "资源没好吗哪都别去");

            m_assetsReady = true;
            sequence.Tick(0.1f);

            Assert.AreEqual(EBootStep.PrepareConfigs, sequence.Step, "资源好了应该去设来源");
            Assert.AreEqual(1, m_prepareCalls, "设来源动作应该执行一次");

            sequence.Tick(0.1f);

            Assert.AreEqual(EBootStep.PrepareConfigs, sequence.Step, "来源还没设好，应继续等");

            m_sourceReady = true;
            sequence.Tick(0.1f);

            Assert.AreEqual(EBootStep.WaitTables, sequence.Step, "来源好了应该去等表");

            m_tablesReady = true;
            sequence.Tick(0.1f);

            Assert.AreEqual(EBootStep.Done, sequence.Step, "表齐了应该走完");
            Assert.AreEqual(1, m_bindCalls, "挂 HUD 应该执行一次");
            Assert.AreEqual(1, m_completedCalls, "Completed 应该触发一次");
            Assert.IsTrue(sequence.IsFinished, "走完应标记完成");
            Assert.IsFalse(sequence.IsRunning, "走完不该还在运行");
        }

        // 功能：Completed 之后继续 Tick 不应再触发任何动作。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 走完 2) 再 Tick 三次 3) 断言计数不变。
        // 上游：测试驱动；下游：Tick。
        [Test]
        public void Tick_AfterFinished_DoesNothing()
        {
            BattleBootSequence sequence = NewSequence();
            RunToDone(sequence);
            sequence.Tick(1f);
            sequence.Tick(1f);

            Assert.AreEqual(1, m_bindCalls, "BindHud 不该重复执行");
            Assert.AreEqual(1, m_completedCalls, "Completed 不该重复触发");
        }

        // 功能：某一步超时应触发 Failed，并带上步号与人话名字。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 用 1 秒超时建链 2) Begin 后 Tick 2 秒 3) 断言失败原因含「第 1 步」与「等资源层」。
        // 上游：测试驱动；下游：Tick、Failed。
        [Test]
        public void Timeout_FailsWithStepName()
        {
            BattleBootSequence sequence = NewSequence(1f);
            sequence.Begin();
            sequence.Tick(2f);

            Assert.IsNotNull(m_failure, "超时应该触发 Failed");
            StringAssert.Contains("第 1 步", m_failure, "失败原因要带步号");
            StringAssert.Contains("等资源层", m_failure, "失败原因要带步骤名");
            Assert.IsFalse(sequence.IsRunning, "失败之后不该还在运行");
            Assert.AreEqual(0, m_bindCalls, "失败不该继续挂 HUD");
        }

        // 功能：Cancel 之后不再触发任何回调。
        // 入参：无。
        // 返回：无。
        // 步骤：1) Begin 2) Cancel 3) 把条件全部置真再 Tick 4) 断言 BindHud 与 Completed 都是 0。
        // 上游：测试驱动；下游：Cancel、Tick。
        [Test]
        public void Cancel_StopsCallbacks()
        {
            BattleBootSequence sequence = NewSequence();
            sequence.Begin();
            sequence.Cancel();
            m_assetsReady = true;
            m_sourceReady = true;
            m_tablesReady = true;
            sequence.Tick(0.1f);

            Assert.AreEqual(0, m_bindCalls, "取消后不该挂 HUD");
            Assert.AreEqual(0, m_completedCalls, "取消后不该触发 Completed");
            Assert.IsFalse(sequence.IsRunning, "取消后不该还在运行");
        }

        // 功能：取消后再 Begin 应能从第一步重新跑（回世界再进副本的场景）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 跑到第二步 2) Cancel 3) Begin 4) 断言 Step 回到 WaitAssets 且跑完仍只有一次 BindHud。
        // 上游：测试驱动；下游：Begin、Tick。
        [Test]
        public void Begin_AfterCancel_RestartsFromFirstStep()
        {
            BattleBootSequence sequence = NewSequence();
            sequence.Begin();
            m_assetsReady = true;
            sequence.Tick(0.1f);
            sequence.Cancel();
            sequence.Begin();

            Assert.AreEqual(EBootStep.WaitAssets, sequence.Step, "重新 Begin 应回到第一步");

            m_sourceReady = true;
            m_tablesReady = true;
            sequence.Tick(0.1f);
            sequence.Tick(0.1f);
            sequence.Tick(0.1f);

            Assert.AreEqual(1, m_bindCalls, "重跑一遍只该挂一次 HUD");
            Assert.AreEqual(1, m_completedCalls, "重跑一遍只该完成一次");
        }

        // 功能：日志应按步输出，便于现场定位卡在哪一步。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 走完 2) 断言日志里出现「第 1 步」与「表现层就绪」。
        // 上游：测试驱动；下游：注入的日志出口。
        [Test]
        public void Log_ContainsStepMarkers()
        {
            BattleBootSequence sequence = NewSequence();
            RunToDone(sequence);

            Assert.IsTrue(m_logs.Exists(line => line.Contains("第 1 步")), "日志应出现第 1 步");
            Assert.IsTrue(m_logs.Exists(line => line.Contains("表现层就绪")), "日志应出现就绪");
        }

        // 功能：建一条测试用的链（条件读本类字段，动作只计数）。
        // 入参：timeoutSeconds 单步超时秒数。
        // 返回：建好的编排链。
        // 步骤：1) 用本类字段构造四个依赖 2) 接上 Completed/Failed 计数 3) 返回。
        // 上游：各用例；下游：BattleBootSequence 构造。
        private BattleBootSequence NewSequence(float timeoutSeconds = 8f)
        {
            BattleBootSequence sequence = new BattleBootSequence(
                () => m_assetsReady,
                () => m_sourceReady,
                () => m_tablesReady,
                () => m_prepareCalls++,
                () => m_bindCalls++,
                line => m_logs.Add(line),
                timeoutSeconds);

            sequence.Completed += () => m_completedCalls++;
            sequence.Failed += reason => m_failure = reason;
            return sequence;
        }

        // 功能：把条件全部置真并推进到完成（供「已完成之后」类用例复用）。
        // 入参：sequence 目标链。
        // 返回：无。
        // 步骤：1) 三个标志置真 2) Begin 3) Tick 四次走完。
        // 上游：测试驱动；下游：Begin、Tick。
        private void RunToDone(BattleBootSequence sequence)
        {
            m_assetsReady = true;
            m_sourceReady = true;
            m_tablesReady = true;
            sequence.Begin();
            sequence.Tick(0.1f);
            sequence.Tick(0.1f);
            sequence.Tick(0.1f);
            sequence.Tick(0.1f);
        }
    }
}
