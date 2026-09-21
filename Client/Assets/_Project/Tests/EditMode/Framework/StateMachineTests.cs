// ============================================================================
//  M1-A10 · 状态机的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2
//  对应需求：FSM-01/02/04/05/08/09
//
//  ---------------------------------------------------------------------------
//  为什么这些能在 EditMode 跑（而且**本来就该**在这里跑）
//  ---------------------------------------------------------------------------
//  FSM-09 要求状态机是**纯 C#、不依赖 MonoBehaviour**（帧同步前提）。
//  所以它天然就是"纯逻辑"，放 EditMode 是**正确的位置**，不是将就 ——
//  这和 A1 那些 MonoBehaviour 用例必须搬去 PlayMode 正好相反。
//
//  ⚠️ 反过来说：**如果哪天这些用例需要 PlayMode 才能跑，说明有人往状态机里
//     塞了 MonoBehaviour 或协程** —— 那是 FSM-09 被破坏了，测试会当场挡住。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Fsm;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>平面状态机用的状态标识。</summary>
    internal enum ProbeId
    {
        Idle,
        Run,
        Attack,
        Dead
    }

    /// <summary>分层状态机用的标识：上层 Ground/Action，下层 Idle/Run/Attack。</summary>
    internal enum LayeredId
    {
        Ground,
        Action,
        Idle,
        Run,
        Attack
    }

    /// <summary>测试事件（用 A3 的强类型 EventId，拼错编译不过）。</summary>
    internal static class ProbeFsmEvents
    {
        /// <summary>跳跃。</summary>
        public static readonly EventId Jump = EventId.Declare("ProbeFsm.Jump");

        /// <summary>攻击。</summary>
        public static readonly EventId Attack = EventId.Declare("ProbeFsm.Attack");

        /// <summary>受伤。</summary>
        public static readonly EventId Damage = EventId.Declare("ProbeFsm.Damage");
    }

    /// <summary>
    /// 探针状态：记录被调用了什么、按需拒绝转移、按需处理事件。
    /// </summary>
    /// <typeparam name="S">状态标识类型。</typeparam>
    internal sealed class ProbeState<S> : StateBase<S>
    {
        private readonly string m_name;

        /// <summary>构造。</summary>
        /// <param name="name">显示名。</param>
        public ProbeState(string name)
        {
            m_name = name;
        }

        /// <summary>显示名。</summary>
        public override string Name
        {
            get { return m_name; }
        }

        /// <summary>`OnEnter` 被调用次数。</summary>
        public int EnterCount;

        /// <summary>`OnExit` 被调用次数。</summary>
        public int ExitCount;

        /// <summary>`OnUpdate` 被调用次数。</summary>
        public int UpdateCount;

        /// <summary>`OnEvent` 被调用次数。</summary>
        public int EventCount;

        /// <summary>最近一次 `OnEnter` 的来源状态。</summary>
        public S LastPrevious;

        /// <summary>最近一次 `OnExit` 的目标状态。</summary>
        public S LastNext;

        /// <summary>最近一次 `OnUpdate` 收到的推进信息。</summary>
        public StateTick LastTick;

        /// <summary>`OnEnter` 里要额外做的事情（用来测"转移中再请求转移"）。</summary>
        public Action OnEnterAction;

        /// <summary>`OnExit` 里要额外做的事情（用来验证**调用顺序**）。</summary>
        public Action ExitRecorder;

        /// <summary>`CanEnterFrom` 的规则；null 表示都允许。</summary>
        public Func<S, bool> EnterRule;

        /// <summary>这个状态愿意处理的事件（返回 true 的那些）。</summary>
        public readonly HashSet<EventId> Handled = new HashSet<EventId>();

        /// <summary>进入。</summary>
        /// <param name="previous">来源。</param>
        public override void OnEnter(S previous)
        {
            EnterCount++;
            LastPrevious = previous;

            if (OnEnterAction != null)
            {
                OnEnterAction();
            }
        }

        /// <summary>推进。</summary>
        /// <param name="tick">推进信息。</param>
        public override void OnUpdate(in StateTick tick)
        {
            UpdateCount++;
            LastTick = tick;
        }

        /// <summary>离开。</summary>
        /// <param name="next">目标。</param>
        public override void OnExit(S next)
        {
            ExitCount++;
            LastNext = next;

            if (ExitRecorder != null)
            {
                ExitRecorder();
            }
        }

        /// <summary>处理事件。</summary>
        /// <param name="id">事件标识。</param>
        /// <returns>在 <see cref="Handled"/> 里就返回 true。</returns>
        public override bool OnEvent(EventId id)
        {
            EventCount++;
            return Handled.Contains(id);
        }

        /// <summary>能不能从某个状态切进来。</summary>
        /// <param name="from">来源。</param>
        /// <returns>由 <see cref="EnterRule"/> 决定，没设就允许。</returns>
        public override bool CanEnterFrom(S from)
        {
            return EnterRule == null || EnterRule(from);
        }
    }

    /// <summary>
    /// A10：单层状态机的测试。
    /// </summary>
    public sealed class StateMachineTests
    {
        private StateMachine<ProbeId> m_machine;
        private ProbeState<ProbeId> m_idle;
        private ProbeState<ProbeId> m_run;
        private ProbeState<ProbeId> m_attack;
        private ProbeState<ProbeId> m_dead;

        /// <summary>建一个注册好四个状态的机器（**不自动 Start**）。</summary>
        [SetUp]
        public void SetUp()
        {
            m_machine = new StateMachine<ProbeId>();
            m_idle = new ProbeState<ProbeId>("Idle");
            m_run = new ProbeState<ProbeId>("Run");
            m_attack = new ProbeState<ProbeId>("Attack");
            m_dead = new ProbeState<ProbeId>("Dead");

            m_machine.Register(ProbeId.Idle, m_idle);
            m_machine.Register(ProbeId.Run, m_run);
            m_machine.Register(ProbeId.Attack, m_attack);
            m_machine.Register(ProbeId.Dead, m_dead);
        }

        // ====================================================================
        //  注册 / 启动
        // ====================================================================

        /// <summary>`Start` 进入初始状态。</summary>
        [Test]
        public void Start_EntersInitialState()
        {
            m_machine.Start(ProbeId.Idle);

            Assert.IsTrue(m_machine.IsStarted);
            Assert.AreEqual(ProbeId.Idle, m_machine.Current);
            Assert.AreEqual("Idle", m_machine.CurrentName);
            Assert.AreEqual(1, m_idle.EnterCount);
        }

        /// <summary>`Start` 会记一条历史，但**不广播**（它不是"切换"）。</summary>
        [Test]
        public void Start_IsRecordedButDoesNotRaiseStateChanged()
        {
            int changed = 0;
            m_machine.StateChanged += record => changed++;

            m_machine.Start(ProbeId.Idle);

            Assert.AreEqual(0, changed, "Start 不该被当成一次状态切换");
            Assert.AreEqual(1, m_machine.HistoryCount);
        }

        /// <summary>重复 `Start` 当场报错（复用实例会让旧状态收不到 `OnExit`）。</summary>
        [Test]
        public void Start_Twice_Throws()
        {
            m_machine.Start(ProbeId.Idle);

            InvalidOperationException ex =
                Assert.Throws<InvalidOperationException>(() => m_machine.Start(ProbeId.Run));

            StringAssert.Contains("已经启动过", ex.Message);
        }

        /// <summary>没 `Start` 就切状态 = 编程错误。</summary>
        [Test]
        public void ChangeState_BeforeStart_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => m_machine.TryChangeState(ProbeId.Run));
        }

        /// <summary>同一个标识注册两次要挡住（否则转移时拿哪个全看不出来）。</summary>
        [Test]
        public void Register_DuplicateId_Throws()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => m_machine.Register(ProbeId.Idle, new ProbeState<ProbeId>("Duplicate")));

            StringAssert.Contains("已经注册过", ex.Message);
        }

        /// <summary>状态对象为 null 要挡住。</summary>
        [Test]
        public void Register_NullState_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => m_machine.Register(ProbeId.Dead, null));
        }

        /// <summary>切到没注册的状态，报错要说清该怎么做。</summary>
        [Test]
        public void ChangeState_Unregistered_ThrowsWithHint()
        {
            StateMachine<ProbeId> bare = new StateMachine<ProbeId>();
            bare.Register(ProbeId.Idle, new ProbeState<ProbeId>("Idle"));

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => bare.Start(ProbeId.Run));

            StringAssert.Contains("没有注册过", ex.Message);
            StringAssert.Contains("Register", ex.Message, "报错要告诉人该怎么做");
        }

        // ====================================================================
        //  转移
        // ====================================================================

        /// <summary>切换时先 `OnExit` 旧的、再 `OnEnter` 新的。</summary>
        [Test]
        public void TryChangeState_InvokesExitThenEnter()
        {
            m_machine.Start(ProbeId.Idle);

            bool changed = m_machine.TryChangeState(ProbeId.Run);

            Assert.IsTrue(changed);
            Assert.AreEqual(1, m_idle.ExitCount);
            Assert.AreEqual(ProbeId.Run, m_idle.LastNext, "旧的要被告知切去哪");
            Assert.AreEqual(1, m_run.EnterCount);
            Assert.AreEqual(ProbeId.Idle, m_run.LastPrevious, "新的要被告知从哪来");
            Assert.AreEqual(ProbeId.Run, m_machine.Current);
        }

        /// <summary>切到当前已在的状态 = 空操作（否则历史会被刷爆）。</summary>
        [Test]
        public void TryChangeState_ToSameState_IsNoOp()
        {
            m_machine.Start(ProbeId.Idle);

            bool changed = m_machine.TryChangeState(ProbeId.Idle);

            Assert.IsFalse(changed);
            Assert.AreEqual(0, m_idle.ExitCount, "不该退出");
            Assert.AreEqual(1, m_idle.EnterCount, "不该再进入一次");
            Assert.AreEqual(1, m_machine.HistoryCount, "不该记历史");
        }

        /// <summary>目标状态说"不许从那边过来"时，转移被拒绝并广播。</summary>
        [Test]
        public void TryChangeState_RejectedByTarget_ReturnsFalseAndReports()
        {
            m_run.EnterRule = from => from != ProbeId.Attack;   // 攻击中不能切跑步
            m_machine.Start(ProbeId.Idle);

            StateChangeRecord rejected = default(StateChangeRecord);
            bool raised = false;
            m_machine.TransitionRejected += record => { raised = true; rejected = record; };

            m_machine.TryChangeState(ProbeId.Attack);
            bool changed = m_machine.TryChangeState(ProbeId.Run);

            Assert.IsFalse(changed, "被拒绝就该返回 false");
            Assert.IsTrue(raised);
            Assert.AreEqual("Run", rejected.To);
            Assert.AreEqual("Attack", rejected.From);
            Assert.AreEqual(ProbeId.Attack, m_machine.Current, "拒绝之后应当停在原状态");
        }

        /// <summary>强制转移跳过 `CanEnterFrom`（复活 / 剧情 / 调试用）。</summary>
        [Test]
        public void ForceChangeState_IgnoresCanEnterFrom()
        {
            m_dead.EnterRule = from => false;   // 谁都不许进"死亡"
            m_machine.Start(ProbeId.Idle);

            Assert.IsFalse(m_machine.TryChangeState(ProbeId.Dead));
            Assert.IsTrue(m_machine.ForceChangeState(ProbeId.Dead));
            Assert.AreEqual(ProbeId.Dead, m_machine.Current);
        }

        /// <summary>`StateChanged` 在**两边回调都跑完**之后才广播。</summary>
        [Test]
        public void StateChanged_IsRaisedAfterBothCallbacks()
        {
            m_machine.Start(ProbeId.Idle);

            int exitCountWhenRaised = -1;
            int enterCountWhenRaised = -1;

            m_machine.StateChanged += record =>
            {
                exitCountWhenRaised = m_idle.ExitCount;
                enterCountWhenRaised = m_run.EnterCount;
            };

            m_machine.TryChangeState(ProbeId.Run);

            Assert.AreEqual(1, exitCountWhenRaised, "广播时旧状态应当已经 OnExit 完");
            Assert.AreEqual(1, enterCountWhenRaised, "广播时新状态应当已经 OnEnter 完");
        }

        /// <summary>
        /// **转移过程中再请求转移会被排队**，等这次转移完整结束后才执行。
        /// <para>`OnEnter` 里判断"该直接切走"是很常见的写法，不能让它递归执行。</para>
        /// </summary>
        [Test]
        public void TransitionRequestedFromOnEnter_IsQueuedAndAppliedAfter()
        {
            // Idle 一进去就要求切到 Run
            m_idle.OnEnterAction = () => m_machine.TryChangeState(ProbeId.Run);

            m_machine.Start(ProbeId.Idle);

            Assert.AreEqual(ProbeId.Run, m_machine.Current, "排队的转移应当在 Start 结束后执行");
            Assert.AreEqual(1, m_idle.ExitCount);
            Assert.AreEqual(1, m_run.EnterCount);
            Assert.AreEqual(2, m_machine.HistoryCount, "两条历史：Idle->Idle（Start）、Idle->Run（排队的那次）");
        }

        // ====================================================================
        //  推进（FSM-04：逻辑帧驱动）
        // ====================================================================

        /// <summary>`Tick` 把帧号和相隔帧数原样交给当前状态。</summary>
        [Test]
        public void Tick_ForwardsFrameAndDeltaFrames()
        {
            m_machine.Start(ProbeId.Idle);

            m_machine.Tick(10);
            m_machine.Tick(13);

            Assert.AreEqual(2, m_idle.UpdateCount);
            Assert.AreEqual(13, m_idle.LastTick.Frame);
            Assert.AreEqual(3, m_idle.LastTick.DeltaFrames);
            Assert.AreEqual(13, m_machine.Frame);
        }

        /// <summary>第一次推进时"相隔帧数"是 0（还没有上一帧）。</summary>
        [Test]
        public void Tick_FirstCall_HasZeroDelta()
        {
            m_machine.Start(ProbeId.Idle);
            m_machine.Tick(5);

            Assert.AreEqual(0, m_idle.LastTick.DeltaFrames);
        }

        /// <summary>**帧号倒退当场抛异常** —— 那会让"持续 N 帧"的判断全部失效。</summary>
        [Test]
        public void Tick_FrameGoesBackwards_Throws()
        {
            m_machine.Start(ProbeId.Idle);
            m_machine.Tick(10);

            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => m_machine.Tick(9));

            StringAssert.Contains("倒退", ex.Message);
        }

        /// <summary>没 `Start` 也能推进（不抛），只是不会调任何状态。</summary>
        [Test]
        public void Tick_BeforeStart_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => m_machine.Tick(1));
            Assert.AreEqual(1, m_machine.Frame);
        }

        // ====================================================================
        //  事件
        // ====================================================================

        /// <summary>事件交给当前状态，返回值就是它有没有处理。</summary>
        [Test]
        public void SendEvent_GoesToCurrentState()
        {
            m_idle.Handled.Add(ProbeFsmEvents.Jump);
            m_machine.Start(ProbeId.Idle);

            Assert.IsTrue(m_machine.SendEvent(ProbeFsmEvents.Jump));
            Assert.IsFalse(m_machine.SendEvent(ProbeFsmEvents.Attack));
            Assert.AreEqual(2, m_idle.EventCount);
        }

        /// <summary>无效的事件标识当场抛（不要让它静默丢事件）。</summary>
        [Test]
        public void SendEvent_InvalidId_Throws()
        {
            m_machine.Start(ProbeId.Idle);

            Assert.Throws<ArgumentException>(() => m_machine.SendEvent(default(EventId)));
        }

        // ====================================================================
        //  历史（FSM-08）
        // ====================================================================

        /// <summary>历史只保留最近 N 条。</summary>
        [Test]
        public void History_KeepsOnlyRecentEntries()
        {
            StateMachine<ProbeId> machine = new StateMachine<ProbeId>(historyCapacity: 3);
            ProbeState<ProbeId> a = new ProbeState<ProbeId>("A");
            ProbeState<ProbeId> b = new ProbeState<ProbeId>("B");
            machine.Register(ProbeId.Idle, a);
            machine.Register(ProbeId.Run, b);
            machine.Start(ProbeId.Idle);

            for (int i = 0; i < 5; i++)
            {
                machine.TryChangeState(ProbeId.Run);
                machine.TryChangeState(ProbeId.Idle);
            }

            Assert.AreEqual(3, machine.HistoryCount);
        }

        /// <summary>`CopyHistory` 给的是**新的在前**（调试面板直接从上往下显示）。</summary>
        [Test]
        public void CopyHistory_IsNewestFirst()
        {
            m_machine.Start(ProbeId.Idle);
            m_machine.Tick(1);
            m_machine.TryChangeState(ProbeId.Run);
            m_machine.Tick(2);
            m_machine.TryChangeState(ProbeId.Attack);

            List<StateChangeRecord> history = new List<StateChangeRecord>();
            m_machine.CopyHistory(history);

            Assert.AreEqual(3, history.Count);
            Assert.AreEqual("Run", history[0].From, "第一条应当是最新的那次");
            Assert.AreEqual("Attack", history[0].To);
            Assert.AreEqual(2, history[0].Frame);
        }
    }
}
