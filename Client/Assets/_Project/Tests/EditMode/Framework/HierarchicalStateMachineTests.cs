// ============================================================================
//  M1-A10 · 分层状态机的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2
//  对应需求：FSM-05（**上层互斥、下层跟随上层切换**）、FSM-08
//
//  ---------------------------------------------------------------------------
//  这组测试的重点：**"分层"到底约束了什么**
//  ---------------------------------------------------------------------------
//  如果只是"两个状态机拼在一起"，那就不叫分层。真正体现分层的是这条：
//
//      **下层属于某一个上层，不能跨上层乱跳。**
//
//  所以最该看的用例是 `ChangeLower_AcrossUpper_Throws`：
//  在 `Ground` 下层想切到 `Action` 才有的 `Attack` —— **必须被拒绝**，
//  而且报错要说清"这就是分层在起作用"，而不是给一句 `InvalidOperationException`。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Fsm;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// A10：两层状态机的测试。
    /// </summary>
    public sealed class HierarchicalStateMachineTests
    {
        private HierarchicalStateMachine<LayeredId> m_machine;
        private ProbeState<LayeredId> m_ground;
        private ProbeState<LayeredId> m_action;
        private ProbeState<LayeredId> m_idle;
        private ProbeState<LayeredId> m_run;
        private ProbeState<LayeredId> m_attack;

        /// <summary>
        /// 搭一个两级结构：
        /// <code>
        /// Ground ─┬─ Idle（入口）
        ///         └─ Run
        /// Action ─┬─ Attack（入口）
        /// </code>
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            m_machine = new HierarchicalStateMachine<LayeredId>();

            m_ground = new ProbeState<LayeredId>("Ground");
            m_action = new ProbeState<LayeredId>("Action");
            m_idle = new ProbeState<LayeredId>("Idle");
            m_run = new ProbeState<LayeredId>("Run");
            m_attack = new ProbeState<LayeredId>("Attack");

            m_machine.RegisterUpper(LayeredId.Ground, m_ground, LayeredId.Idle);
            m_machine.RegisterSub(LayeredId.Ground, LayeredId.Idle, m_idle);
            m_machine.RegisterSub(LayeredId.Ground, LayeredId.Run, m_run);

            m_machine.RegisterUpper(LayeredId.Action, m_action, LayeredId.Attack);
            m_machine.RegisterSub(LayeredId.Action, LayeredId.Attack, m_attack);
        }

        // ====================================================================
        //  启动
        // ====================================================================

        /// <summary>`Start` 进上层，并**自动**进它的入口下层。</summary>
        [Test]
        public void Start_EntersUpperAndItsEntrySub()
        {
            m_machine.Start(LayeredId.Ground);

            Assert.IsTrue(m_machine.IsStarted);
            Assert.AreEqual(LayeredId.Ground, m_machine.UpperCurrent);
            Assert.AreEqual(LayeredId.Idle, m_machine.LowerCurrent);
            Assert.AreEqual(1, m_ground.EnterCount);
            Assert.AreEqual(1, m_idle.EnterCount);
        }

        /// <summary>也可以指定初始下层。</summary>
        [Test]
        public void Start_WithExplicitSub_UsesIt()
        {
            m_machine.Start(LayeredId.Ground, LayeredId.Run);

            Assert.AreEqual(LayeredId.Run, m_machine.LowerCurrent);
            Assert.AreEqual(0, m_idle.EnterCount);
            Assert.AreEqual(1, m_run.EnterCount);
        }

        /// <summary>不指定入口下层时，**第一个注册的下层**自动成为入口。</summary>
        [Test]
        public void RegisterSub_FirstOneBecomesEntry()
        {
            HierarchicalStateMachine<LayeredId> machine = new HierarchicalStateMachine<LayeredId>();
            ProbeState<LayeredId> upper = new ProbeState<LayeredId>("Ground");
            ProbeState<LayeredId> first = new ProbeState<LayeredId>("First");

            machine.RegisterUpper(LayeredId.Ground, upper, LayeredId.Idle);
            machine.RegisterSub(LayeredId.Ground, LayeredId.Run, first);
            machine.Start(LayeredId.Ground);

            Assert.AreEqual(LayeredId.Run, machine.LowerCurrent,
                "没有显式指定时，第一个注册的下层应当成为入口");
        }

        /// <summary>重复 `Start` 当场报错。</summary>
        [Test]
        public void Start_Twice_Throws()
        {
            m_machine.Start(LayeredId.Ground);

            Assert.Throws<InvalidOperationException>(() => m_machine.Start(LayeredId.Action));
        }

        /// <summary>路径字符串是"上层/下层"，调试面板直接显示它。</summary>
        [Test]
        public void CurrentPath_IsUpperSlashLower()
        {
            m_machine.Start(LayeredId.Ground);

            Assert.AreEqual("Ground/Idle", m_machine.CurrentPath);

            m_machine.TryChangeLower(LayeredId.Run);

            Assert.AreEqual("Ground/Run", m_machine.CurrentPath);
        }

        // ====================================================================
        //  切上层：下层跟着换（FSM-05 的核心）
        // ====================================================================

        /// <summary>切上层时，下层**一定**跟着换到新上层的入口状态。</summary>
        [Test]
        public void ChangeUpper_SwitchesSubToTheNewUpperEntry()
        {
            m_machine.Start(LayeredId.Ground);
            m_machine.TryChangeLower(LayeredId.Run);

            bool changed = m_machine.TryChangeUpper(LayeredId.Action);

            Assert.IsTrue(changed);
            Assert.AreEqual(LayeredId.Action, m_machine.UpperCurrent);
            Assert.AreEqual(LayeredId.Attack, m_machine.LowerCurrent, "下层必须跟着上层走");
            Assert.AreEqual(1, m_run.ExitCount, "旧的下层要先退出");
            Assert.AreEqual(1, m_attack.EnterCount);
        }

        /// <summary>进退顺序：**退下层 → 退上层 → 进上层 → 进下层**。</summary>
        [Test]
        public void ChangeUpper_ExitsLowerBeforeUpper_AndEntersUpperBeforeLower()
        {
            m_machine.Start(LayeredId.Ground);

            List<string> order = new List<string>();

            // 让四个状态在各自的回调里往同一个列表记一笔，然后比对顺序。
            RecordOrder(m_idle, "exit-idle", order, true);
            RecordOrder(m_ground, "exit-ground", order, true);
            RecordOrder(m_action, "enter-action", order, false);
            RecordOrder(m_attack, "enter-attack", order, false);

            m_machine.TryChangeUpper(LayeredId.Action);

            CollectionAssert.AreEqual(
                new[] { "exit-idle", "exit-ground", "enter-action", "enter-attack" },
                order,
                "顺序必须是：退下层 → 退上层 → 进上层 → 进下层");
        }

        /// <summary>要切的上层被目标拒绝时，返回 false 并广播。</summary>
        [Test]
        public void ChangeUpper_RejectedByTarget_ReturnsFalse()
        {
            m_action.EnterRule = from => false;
            m_machine.Start(LayeredId.Ground);

            bool raised = false;
            m_machine.TransitionRejected += record => raised = true;

            Assert.IsFalse(m_machine.TryChangeUpper(LayeredId.Action));
            Assert.IsTrue(raised);
            Assert.AreEqual(LayeredId.Ground, m_machine.UpperCurrent, "拒绝之后停在原地");
            Assert.AreEqual(LayeredId.Idle, m_machine.LowerCurrent);
        }

        // ====================================================================
        //  切下层
        // ====================================================================

        /// <summary>同一个上层下面切下层是正常的。</summary>
        [Test]
        public void ChangeLower_WithinSameUpper_Works()
        {
            m_machine.Start(LayeredId.Ground);

            bool changed = m_machine.TryChangeLower(LayeredId.Run);

            Assert.IsTrue(changed);
            Assert.AreEqual(LayeredId.Ground, m_machine.UpperCurrent, "上层不该动");
            Assert.AreEqual(LayeredId.Run, m_machine.LowerCurrent);
            Assert.AreEqual(0, m_ground.ExitCount, "上层不该被退出");
        }

        /// <summary>切到当前已在的下层 = 空操作。</summary>
        [Test]
        public void ChangeLower_ToSameSub_IsNoOp()
        {
            m_machine.Start(LayeredId.Ground);

            Assert.IsFalse(m_machine.TryChangeLower(LayeredId.Idle));
            Assert.AreEqual(0, m_idle.ExitCount);
        }

        /// <summary>
        /// **⚠️ 这就是"分层"的意义**：下层属于某一个上层，不能跨上层乱跳。
        /// </summary>
        [Test]
        public void ChangeLower_AcrossUpper_ThrowsWithLayeringExplanation()
        {
            m_machine.Start(LayeredId.Ground);   // 当前 Ground/Idle

            // Attack 只在 Action 下面，Ground 下面没有它
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => m_machine.TryChangeLower(LayeredId.Attack));

            StringAssert.Contains("Attack", ex.Message);
            StringAssert.Contains("分层", ex.Message,
                "报错要说清这是分层的约束，而不是一句干巴巴的 InvalidOperationException");
            StringAssert.Contains("RegisterSub", ex.Message, "还要告诉人怎么修");
        }

        /// <summary>切上层时指定的下层必须属于那个上层。</summary>
        [Test]
        public void ChangeUpper_WithSubFromAnotherUpper_Throws()
        {
            m_machine.Start(LayeredId.Ground);

            Assert.Throws<InvalidOperationException>(
                () => m_machine.TryChangeUpper(LayeredId.Action, LayeredId.Run));
        }

        /// <summary>强切可以跳过所有的检查（复活 / 剧情 / 调试）。</summary>
        [Test]
        public void ForceChange_SkipsChecks()
        {
            m_action.EnterRule = from => false;
            m_machine.Start(LayeredId.Ground);

            Assert.IsFalse(m_machine.TryChangeUpper(LayeredId.Action));
            Assert.IsTrue(m_machine.ForceChange(LayeredId.Action, LayeredId.Attack));
            Assert.AreEqual("Action/Attack", m_machine.CurrentPath);
        }

        // ====================================================================
        //  事件派发：先下层、后上层
        // ====================================================================

        /// <summary>下层说"我处理了"，上层就收不到（避免重复触发）。</summary>
        [Test]
        public void SendEvent_StopsAtLowerWhenHandled()
        {
            m_idle.Handled.Add(ProbeFsmEvents.Jump);
            m_machine.Start(LayeredId.Ground);

            bool handled = m_machine.SendEvent(ProbeFsmEvents.Jump);

            Assert.IsTrue(handled);
            Assert.AreEqual(1, m_idle.EventCount);
            Assert.AreEqual(0, m_ground.EventCount, "下层处理了，上层不该再收到");
        }

        /// <summary>下层不管时，事件**冒泡到上层**。</summary>
        [Test]
        public void SendEvent_FallsThroughToUpperWhenLowerIgnores()
        {
            m_ground.Handled.Add(ProbeFsmEvents.Damage);
            m_machine.Start(LayeredId.Ground);

            bool handled = m_machine.SendEvent(ProbeFsmEvents.Damage);

            Assert.IsTrue(handled);
            Assert.AreEqual(1, m_idle.EventCount, "下层先被问过");
            Assert.AreEqual(1, m_ground.EventCount, "下层没处理，冒泡到上层");
        }

        /// <summary>两级都不管时返回 false。</summary>
        [Test]
        public void SendEvent_ReturnsFalseWhenNobodyHandles()
        {
            m_machine.Start(LayeredId.Ground);

            Assert.IsFalse(m_machine.SendEvent(ProbeFsmEvents.Attack));
        }

        /// <summary>无效事件标识当场抛。</summary>
        [Test]
        public void SendEvent_InvalidId_Throws()
        {
            m_machine.Start(LayeredId.Ground);

            Assert.Throws<ArgumentException>(() => m_machine.SendEvent(default(EventId)));
        }

        // ====================================================================
        //  推进
        // ====================================================================

        /// <summary>`Tick` 会同时推进下层和上层（**先下层、后上层**）。</summary>
        [Test]
        public void Tick_UpdatesLowerThenUpper()
        {
            m_machine.Start(LayeredId.Ground);
            m_machine.Tick(7);

            Assert.AreEqual(1, m_idle.UpdateCount);
            Assert.AreEqual(1, m_ground.UpdateCount);
            Assert.AreEqual(7, m_idle.LastTick.Frame);
            Assert.AreEqual(7, m_ground.LastTick.Frame);
        }

        /// <summary>帧号倒退当场抛。</summary>
        [Test]
        public void Tick_FrameGoesBackwards_Throws()
        {
            m_machine.Start(LayeredId.Ground);
            m_machine.Tick(10);

            Assert.Throws<ArgumentOutOfRangeException>(() => m_machine.Tick(9));
        }

        // ====================================================================
        //  历史
        // ====================================================================

        /// <summary>历史里记的是**完整路径**（"Ground/Run"），不是单个标识。</summary>
        [Test]
        public void History_RecordsFullPath()
        {
            m_machine.Start(LayeredId.Ground);
            m_machine.Tick(1);
            m_machine.TryChangeLower(LayeredId.Run);
            m_machine.Tick(2);
            m_machine.TryChangeUpper(LayeredId.Action);

            List<StateChangeRecord> history = new List<StateChangeRecord>();
            m_machine.CopyHistory(history);

            Assert.AreEqual(3, history.Count);
            Assert.AreEqual("Ground/Run", history[0].From, "新的在前");
            Assert.AreEqual("Action/Attack", history[0].To);
            Assert.AreEqual("Action/Attack", m_machine.CurrentPath);
        }

        /// <summary>`StateChanged` 在两边回调都跑完之后才广播。</summary>
        [Test]
        public void StateChanged_IsRaisedAfterCallbacks()
        {
            m_machine.Start(LayeredId.Ground);

            int exitWhenRaised = -1;
            int enterWhenRaised = -1;

            m_machine.StateChanged += record =>
            {
                exitWhenRaised = m_idle.ExitCount;
                enterWhenRaised = m_attack.EnterCount;
            };

            m_machine.TryChangeUpper(LayeredId.Action);

            Assert.AreEqual(1, exitWhenRaised);
            Assert.AreEqual(1, enterWhenRaised);
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>
        /// 把一个状态的两个回调都变成"往列表里记一笔"，用来验证**调用顺序**。
        /// </summary>
        /// <param name="state">目标状态（用子类覆写不方便，这里用一个包装状态替换）。</param>
        /// <param name="tag">记进列表的标记。</param>
        /// <param name="order">收集顺序的列表。</param>
        /// <param name="isExit">true 表示这个状态要被"退出"，false 表示要被"进入"。</param>
        private static void RecordOrder(ProbeState<LayeredId> state, string tag,
                                        List<string> order, bool isExit)
        {
            // ProbeState 用的是 override，不能再包一层；所以这里直接改写它的行为：
            // 退出 → 用 OnExit 记；进入 → 用 OnEnterAction 记。
            if (isExit)
            {
                state.ExitRecorder = () => order.Add(tag);
            }
            else
            {
                state.OnEnterAction = () => order.Add(tag);
            }
        }
    }
}
