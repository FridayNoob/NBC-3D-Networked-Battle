// ============================================================================
//  NBC.Framework.Fsm · 单层状态机
//  对应需求：FW-M14（有限状态机基类、泛型状态、状态切换事件）
//  需求条目：FSM-02（状态接口）、FSM-04（逻辑帧驱动）、FSM-08（转移历史）、FSM-09（纯 C#）
//
//  ---------------------------------------------------------------------------
//  怎么用
//  ---------------------------------------------------------------------------
//      enum PlayerState { Idle, Run, Attack }
//
//      var machine = new StateMachine<PlayerState>();
//      machine.Register(PlayerState.Idle,   new IdleState());
//      machine.Register(PlayerState.Run,    new RunState());
//      machine.Register(PlayerState.Attack, new AttackState());
//
//      machine.Start(PlayerState.Idle);
//
//      // 由逻辑层每逻辑帧调一次（**注意不是 Unity 的 Update**）
//      machine.Tick(logicFrame);
//
//      machine.TryChangeState(PlayerState.Run);
//      machine.SendEvent(InputEvents.JumpPressed);
//
//  ---------------------------------------------------------------------------
//  四条写死的语义（避免猜）
//  ---------------------------------------------------------------------------
//  **① 问的是"目标状态允不允许"，不是"当前状态放不放行"。**
//     检查落在 `target.CanEnterFrom(Current)` 上。
//     这样"谁能打断谁"的规则**集中在目标状态里**，而不是散落在每个状态身上。
//
//  **② 转移过程中（`OnExit` / `OnEnter` 里）再请求转移，会被排队，不是立刻执行。**
//     `OnEnter` 里判断"该直接切到 Run"是很常见的写法；如果立刻递归执行，
//     就会变成"在别人的 OnEnter 里跑自己的 OnEnter"，顺序很难预料。
//     所以先记下来，等当前这次转移**完整结束**后再执行。
//     ⚠️ 排队只有**一个位置**：连续请求多次只保留最后一次（后面的覆盖前面的）。
//        这是刻意的 —— 状态机的语义是"最终停在哪个状态"，不是"按顺序播一遍"。
//
//  **③ 切到"当前已经在的状态"是空操作，返回 false，也不触发事件。**
//     否则每次 Tick 都写 `TryChangeState(Current)` 会刷爆历史记录。
//
//  **④ 帧号倒退会当场抛异常。** 逻辑帧号只增不减；
//     倒退说明调用方算错了，而这种错会让"持续 N 帧"的判断全部失效、且**很难查**。
// ============================================================================

#nullable disable
// ↑ 双端共用（服务端也编它，见 Server\NBC.Server.Game.csproj）：服务端开了可空、Unity 没开
//   —— 与 Shared\ 同一处理由（M3-S6b，2026-09-23）。
using System;
using System.Collections.Generic;
using NBC.Framework;

namespace NBC.Framework.Fsm
{
    /// <summary>
    /// 单层有限状态机。**纯 C#，可被服务端复用**（FSM-09）。
    /// </summary>
    /// <typeparam name="S">状态标识类型（通常是 enum）。</typeparam>
    public sealed class StateMachine<S>
    {
        /// <summary>默认保留多少条转移记录（FSM-08 要求"最近 10 次"）。</summary>
        public const int DefaultHistoryCapacity = 10;

        private readonly Dictionary<S, IState<S>> m_states = new Dictionary<S, IState<S>>();
        private readonly List<StateChangeRecord> m_history = new List<StateChangeRecord>();
        private readonly int m_historyCapacity;

        private int m_frame = -1;
        private bool m_started;
        private bool m_changing;

        private bool m_hasPending;
        private S m_pendingNext;
        private bool m_pendingForced;

        /// <summary>构造。</summary>
        /// <param name="historyCapacity">保留多少条转移记录；小于等于 0 表示用默认值。</param>
        public StateMachine(int historyCapacity = DefaultHistoryCapacity)
        {
            m_historyCapacity = historyCapacity > 0 ? historyCapacity : DefaultHistoryCapacity;
        }

        /// <summary>当前状态标识。**没 `Start` 之前是无意义的默认值。**</summary>
        public S Current { get; private set; }

        /// <summary>是否已经 `Start` 过。</summary>
        public bool IsStarted
        {
            get { return m_started; }
        }

        /// <summary>当前逻辑帧号；还没推进过时是 -1。</summary>
        public int Frame
        {
            get { return m_frame; }
        }

        /// <summary>当前状态对象；没 `Start` 时是 null。</summary>
        public IState<S> CurrentState
        {
            get
            {
                if (!m_started)
                {
                    return null;
                }

                IState<S> state;
                return m_states.TryGetValue(Current, out state) ? state : null;
            }
        }

        /// <summary>当前状态名（调试面板直接用这个）。</summary>
        public string CurrentName
        {
            get
            {
                IState<S> state = CurrentState;
                return state != null ? state.Name : "<未开始>";
            }
        }

        /// <summary>已注册的状态数量。</summary>
        public int RegisteredCount
        {
            get { return m_states.Count; }
        }

        /// <summary>已记录的转移条数。</summary>
        public int HistoryCount
        {
            get { return m_history.Count; }
        }

        /// <summary>状态发生切换时触发（在 `OnExit` / `OnEnter` **都完成之后**）。</summary>
        public event Action<StateChangeRecord> StateChanged;

        /// <summary>一个转移被 `CanEnterFrom` 拒绝时触发。</summary>
        public event Action<StateChangeRecord> TransitionRejected;

        // ====================================================================
        //  注册
        // ====================================================================

        /// <summary>
        /// 注册一个状态。
        /// </summary>
        /// <param name="id">状态标识。</param>
        /// <param name="state">状态对象。</param>
        public void Register(S id, IState<S> state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state), "[StateMachine] 状态对象不能为 null。");
            }

            if (m_states.ContainsKey(id))
            {
                throw new InvalidOperationException(
                    "[StateMachine] 状态 «" + id + "» 已经注册过了。" +
                    "同一个标识只能对应一个状态对象，否则转移时会拿到哪一个全看不出来。");
            }

            m_states.Add(id, state);
        }

        /// <summary>某个标识注册过没有。</summary>
        /// <param name="id">状态标识。</param>
        /// <returns>注册过返回 true。</returns>
        public bool IsRegistered(S id)
        {
            return m_states.ContainsKey(id);
        }

        // ====================================================================
        //  启动 / 推进
        // ====================================================================

        /// <summary>
        /// 启动状态机：进入初始状态。
        /// </summary>
        /// <param name="initial">初始状态标识。</param>
        public void Start(S initial)
        {
            if (m_started)
            {
                throw new InvalidOperationException(
                    "[StateMachine] 已经启动过了（当前 " + CurrentName + "）。" +
                    "要重置请重新 new 一个 —— 复用同一个实例会让旧状态收不到 OnExit。");
            }

            IState<S> state = Require(initial);

            m_started = true;
            m_changing = true;

            try
            {
                Current = initial;
                state.OnEnter(initial);
            }
            finally
            {
                m_changing = false;
            }

            RecordChange(initial, initial, false, true);
            DrainPending();
        }

        /// <summary>
        /// 推进一帧。**由逻辑层调用，不要放在 Unity 的 `Update` 里靠真实时间驱动。**
        /// </summary>
        /// <param name="frame">逻辑帧号，只能比上次大。</param>
        public void Tick(int frame)
        {
            if (m_frame >= 0 && frame < m_frame)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frame), frame,
                    "[StateMachine] 逻辑帧号倒退了（上次是 " + m_frame + "）。" +
                    "帧号只增不减；倒退会让所有\"持续 N 帧\"的判断失效，而且很难查。");
            }

            int delta = m_frame < 0 ? 0 : frame - m_frame;
            m_frame = frame;

            if (!m_started)
            {
                return;
            }

            IState<S> state = CurrentState;

            if (state != null)
            {
                state.OnUpdate(new StateTick(frame, delta));
            }

            // `OnUpdate` 里请求的转移，在这里统一执行（避免一边遍历一边改状态）。
            DrainPending();
        }

        // ====================================================================
        //  转移
        // ====================================================================

        /// <summary>
        /// 请求切换到某个状态（会先问目标状态允不允许）。
        /// </summary>
        /// <param name="next">目标状态标识。</param>
        /// <returns>true 表示这次转移真的执行了（或者被排进了队列）。</returns>
        public bool TryChangeState(S next)
        {
            return RequestChange(next, false);
        }

        /// <summary>
        /// **强制**切换（跳过 `CanEnterFrom`）。用于复活、剧情强制、调试。
        /// </summary>
        /// <param name="next">目标状态标识。</param>
        /// <returns>true 表示真的执行了。</returns>
        public bool ForceChangeState(S next)
        {
            return RequestChange(next, true);
        }

        /// <summary>
        /// 把一个事件交给当前状态处理。
        /// </summary>
        /// <param name="id">事件标识。</param>
        /// <returns>true 表示状态说"我处理了"。</returns>
        public bool SendEvent(EventId id)
        {
            if (!id.IsValid)
            {
                throw new ArgumentException("[StateMachine] 事件标识无效。", nameof(id));
            }

            IState<S> state = CurrentState;

            if (state == null)
            {
                return false;
            }

            return state.OnEvent(id);
        }

        // ====================================================================
        //  历史（FSM-08）
        // ====================================================================

        /// <summary>把转移历史拷进一个列表（**新的在前**，方便直接显示）。</summary>
        /// <param name="buffer">接收结果的列表（会先清空）。</param>
        public void CopyHistory(List<StateChangeRecord> buffer)
        {
            if (buffer == null)
            {
                return;
            }

            buffer.Clear();

            for (int i = m_history.Count - 1; i >= 0; i--)
            {
                buffer.Add(m_history[i]);
            }
        }

        /// <summary>清空转移历史。</summary>
        public void ClearHistory()
        {
            m_history.Clear();
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>请求转移的统一入口。</summary>
        /// <param name="next">目标状态。</param>
        /// <param name="forced">是否强制。</param>
        /// <returns>是否被接受。</returns>
        private bool RequestChange(S next, bool forced)
        {
            if (!m_started)
            {
                throw new InvalidOperationException(
                    "[StateMachine] 还没 Start 就想切状态。请先 Start(初始状态)。");
            }

            Require(next);

            // 语义③：切到自己 = 空操作。
            if (!forced && Equals(next, Current) && !m_hasPending)
            {
                return false;
            }

            // 语义②：正在转移中就排队，等这次转移完整结束再执行。
            if (m_changing)
            {
                m_hasPending = true;
                m_pendingNext = next;
                m_pendingForced = forced;
                return true;
            }

            return Apply(next, forced);
        }

        /// <summary>真正执行一次转移。</summary>
        /// <param name="next">目标状态。</param>
        /// <param name="forced">是否强制。</param>
        /// <returns>是否成功。</returns>
        private bool Apply(S next, bool forced)
        {
            S previous = Current;
            IState<S> target = Require(next);

            if (Equals(next, previous))
            {
                return false;
            }

            // 语义①：问**目标**允不允许从当前状态进来。
            if (!forced && !target.CanEnterFrom(previous))
            {
                RaiseTransitionRejected(new StateChangeRecord(m_frame, NameOf(previous), NameOf(next), false));
                return false;
            }

            m_changing = true;

            try
            {
                IState<S> from = CurrentState;

                if (from != null)
                {
                    from.OnExit(next);
                }

                Current = next;
                target.OnEnter(previous);
            }
            finally
            {
                m_changing = false;
            }

            RecordChange(previous, next, forced, false);
            return true;
        }

        /// <summary>把排队的转移执行掉（如果有）。</summary>
        private void DrainPending()
        {
            // 只处理**一次**：排队的转移自己也可能再排一个，让它下一轮再说，
            // 免得写错的状态互相来回切时把这一帧卡死。
            if (!m_hasPending)
            {
                return;
            }

            S next = m_pendingNext;
            bool forced = m_pendingForced;
            m_hasPending = false;

            Apply(next, forced);
        }

        /// <summary>取状态对象，没注册就报清楚。</summary>
        /// <param name="id">状态标识。</param>
        /// <returns>状态对象。</returns>
        private IState<S> Require(S id)
        {
            IState<S> state;

            if (!m_states.TryGetValue(id, out state))
            {
                throw new InvalidOperationException(
                    "[StateMachine] 状态 «" + id + "» 没有注册过。" +
                    "请先 Register(" + id + ", new ...State())。");
            }

            return state;
        }

        /// <summary>取状态显示名（没注册时退回标识本身）。</summary>
        /// <param name="id">状态标识。</param>
        /// <returns>名字。</returns>
        private string NameOf(S id)
        {
            IState<S> state;
            return m_states.TryGetValue(id, out state) ? state.Name : id.ToString();
        }

        /// <summary>记一条历史并广播。</summary>
        /// <param name="from">原状态。</param>
        /// <param name="to">目标状态。</param>
        /// <param name="forced">是否强制。</param>
        /// <param name="silent">true 表示只记录、不广播（`Start` 用）。</param>
        private void RecordChange(S from, S to, bool forced, bool silent)
        {
            StateChangeRecord record =
                new StateChangeRecord(m_frame, NameOf(from), NameOf(to), forced);

            m_history.Add(record);

            // 只保留最近 N 条：超了就从**最旧的**开始删。
            while (m_history.Count > m_historyCapacity)
            {
                m_history.RemoveAt(0);
            }

            if (!silent)
            {
                RaiseStateChanged(record);
            }
        }

        /// <summary>广播"状态变了"。</summary>
        /// <param name="record">转移记录。</param>
        private void RaiseStateChanged(StateChangeRecord record)
        {
            // ⚠️ 这里**不走 A3 的 EventCenter**，而是直接用 C# 事件。
            //    理由和 A7 的"命令不走事件中心"一致：状态切换是**每帧都可能发生的数据**，
            //    而事件中心是给**低频"游戏事件"**用的工具（字典查找 + 委托链 + 类型检查）。
            //    需要它出现在全局事件流里的场合，由游戏层自己转发。
            Action<StateChangeRecord> handlers = StateChanged;

            if (handlers != null)
            {
                handlers(record);
            }
        }

        /// <summary>广播"这个转移被拒绝了"。</summary>
        /// <param name="record">转移记录。</param>
        private void RaiseTransitionRejected(StateChangeRecord record)
        {
            Action<StateChangeRecord> handlers = TransitionRejected;

            if (handlers != null)
            {
                handlers(record);
            }
        }
    }
}
