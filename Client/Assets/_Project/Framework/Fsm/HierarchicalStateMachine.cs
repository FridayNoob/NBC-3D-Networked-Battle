// ============================================================================
//  NBC.Framework.Fsm · 分层状态机（**固定两层**）
//  对应需求：FW-M14（有限状态机基类（分层））
//  需求条目：FSM-05（**上层互斥、下层跟随上层切换**）、FSM-08（转移历史）、FSM-09（纯 C#）
//  需求出处：Docs/01 §8.3「本项目实现**两级 HFSM**」
//
//  ---------------------------------------------------------------------------
//  为什么是"固定两层"而不是"任意 N 层"（2026-09-21 经负责人确认）
//  ---------------------------------------------------------------------------
//  §8.3 要的就是两级（上层 Locomotion/Action/Special，下层 Idle/Run/Attack…）。
//  通用 N 层要多处理一大类问题：子状态机怎么向上报事件、深层状态的"当前路径"怎么拼、
//  调试面板怎么递归画。**而需求一个都用不上。**
//  ⇒ 选固定两层：API 小、行为可预料、测试能写透。
//  ⚠️ 将来真要三层，再加一层是**新增**，不是推翻 —— 这一点在设计时就留好了。
//
//  ---------------------------------------------------------------------------
//  分层体现在哪（这是 FSM-05 的核心）
//  ---------------------------------------------------------------------------
//  **下层是"某个上层的下层"，不是全局的下层。** 所以：
//      · 每个上层状态**自带**一组下层状态（不共享）
//      · 切换上层时，下层**一定**跟着换到新上层的入口状态
//        （例：Ground/Run → Action/Attack_1，上层换、下层也换）
//      · `TryChangeLower("Attack_2")` 在 Ground 下**会被拒绝** ——
//        因为 Ground 根本没有 Attack_2 这个下层
//
//  这条约束就是"分层"的意义：**下层不能跨上层乱跳**。
//
//  ---------------------------------------------------------------------------
//  四条写死的语义
//  ---------------------------------------------------------------------------
//  **① 上层和下层都会收到事件，但顺序是"先下层、后上层"，且下层说"我处理了"就停。**
//     这就是 `IState.OnEvent` 返回 `bool` 的用处（见 `IState.cs` 文件头的说明）。
//
//  **② `Tick` 时也是"先下层、后上层"。** 与事件派发保持一致 —— 少一条要记的规则。
//
//  **③ 转移的检查分两种情况：**
//     · 换**上层** → 问新上层 `CanEnterFrom(旧上层)`
//     · 只换**下层** → 问新下层 `CanEnterFrom(旧下层)`
//     两层各管各的，不会互相代管。
//
//  **④ 切到"当前已在的配置"是空操作**（返回 false、不记录、不触发事件）。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;

namespace NBC.Framework.Fsm
{
    /// <summary>
    /// 两层分层状态机。**纯 C#，可被服务端复用**（FSM-09）。
    /// </summary>
    /// <typeparam name="S">状态标识类型（通常是 enum）。</typeparam>
    public sealed class HierarchicalStateMachine<S>
    {
        /// <summary>默认保留多少条转移记录（FSM-08 要求"最近 10 次"）。</summary>
        public const int DefaultHistoryCapacity = 10;

        /// <summary>一个上层状态，以及它**自带**的那一组下层状态。</summary>
        private sealed class UpperLayer
        {
            /// <summary>上层状态对象。</summary>
            public IState<S> State;

            /// <summary>切到这个上层时，下层默认进哪个（就是"入口状态"）。</summary>
            public S EntrySub;

            /// <summary>这个上层自带的下层状态集合。**不与别的上层共享。**</summary>
            public readonly Dictionary<S, IState<S>> Subs = new Dictionary<S, IState<S>>();
        }

        private readonly Dictionary<S, UpperLayer> m_uppers = new Dictionary<S, UpperLayer>();
        private readonly List<StateChangeRecord> m_history = new List<StateChangeRecord>();
        private readonly int m_historyCapacity;

        private int m_frame = -1;
        private bool m_started;
        private bool m_changing;

        private bool m_hasPending;
        private S m_pendingUpper;
        private S m_pendingSub;
        private bool m_pendingForced;

        /// <summary>构造。</summary>
        /// <param name="historyCapacity">保留多少条转移记录；小于等于 0 表示用默认值。</param>
        public HierarchicalStateMachine(int historyCapacity = DefaultHistoryCapacity)
        {
            m_historyCapacity = historyCapacity > 0 ? historyCapacity : DefaultHistoryCapacity;
        }

        /// <summary>当前上层状态标识。</summary>
        public S UpperCurrent { get; private set; }

        /// <summary>当前下层状态标识。</summary>
        public S LowerCurrent { get; private set; }

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

        /// <summary>上层状态对象；没 `Start` 时是 null。</summary>
        public IState<S> UpperState
        {
            get
            {
                UpperLayer layer;
                return m_started && m_uppers.TryGetValue(UpperCurrent, out layer) ? layer.State : null;
            }
        }

        /// <summary>下层状态对象；没 `Start` 时是 null。</summary>
        public IState<S> LowerState
        {
            get
            {
                UpperLayer layer;
                if (!m_started || !m_uppers.TryGetValue(UpperCurrent, out layer))
                {
                    return null;
                }

                IState<S> sub;
                return layer.Subs.TryGetValue(LowerCurrent, out sub) ? sub : null;
            }
        }

        /// <summary>当前完整路径，形如 `Ground/Run`。**调试面板直接显示这个**（FSM-08）。</summary>
        public string CurrentPath
        {
            get
            {
                if (!m_started)
                {
                    return "<未开始>";
                }

                IState<S> upper = UpperState;
                IState<S> lower = LowerState;

                return (upper != null ? upper.Name : UpperCurrent.ToString()) + "/" +
                       (lower != null ? lower.Name : LowerCurrent.ToString());
            }
        }

        /// <summary>已注册的上层状态数量。</summary>
        public int UpperCount
        {
            get { return m_uppers.Count; }
        }

        /// <summary>已记录的转移条数。</summary>
        public int HistoryCount
        {
            get { return m_history.Count; }
        }

        /// <summary>状态发生了切换时触发（在 `OnExit` / `OnEnter` **都完成之后**）。</summary>
        public event Action<StateChangeRecord> StateChanged;

        /// <summary>一个转移被 `CanEnterFrom` 拒绝时触发。</summary>
        public event Action<StateChangeRecord> TransitionRejected;

        // ====================================================================
        //  注册
        // ====================================================================

        /// <summary>
        /// 注册一个上层状态，并指定它的**入口下层状态**。
        /// </summary>
        /// <param name="upper">上层标识。</param>
        /// <param name="state">上层状态对象。</param>
        /// <param name="entrySub">切到这个上层时，下层先进哪个。</param>
        public void RegisterUpper(S upper, IState<S> state, S entrySub)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state), "[HierarchicalStateMachine] 上层状态不能为 null。");
            }

            if (m_uppers.ContainsKey(upper))
            {
                throw new InvalidOperationException(
                    "[HierarchicalStateMachine] 上层状态 «" + upper + "» 已经注册过了。");
            }

            UpperLayer layer = new UpperLayer();
            layer.State = state;
            layer.EntrySub = entrySub;

            m_uppers.Add(upper, layer);
        }

        /// <summary>
        /// 给某个上层注册一个下层状态。
        /// <para>⚠️ 先 `RegisterUpper` 再注册下层；下层**只属于**这一个上层。</para>
        /// </summary>
        /// <param name="upper">它属于哪个上层。</param>
        /// <param name="sub">下层标识。</param>
        /// <param name="state">下层状态对象。</param>
        public void RegisterSub(S upper, S sub, IState<S> state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state), "[HierarchicalStateMachine] 下层状态不能为 null。");
            }

            UpperLayer layer = RequireUpper(upper);

            if (layer.Subs.ContainsKey(sub))
            {
                throw new InvalidOperationException(
                    "[HierarchicalStateMachine] 上层 «" + upper + "» 下的 «" + sub + "» 已经注册过了。");
            }

            layer.Subs.Add(sub, state);

            // 第一个注册的下层自动成为入口（除非调用方已经在 RegisterUpper 里指定了别的）。
            if (layer.Subs.Count == 1)
            {
                layer.EntrySub = sub;
            }
        }

        /// <summary>某个下层是不是注册在指定上层下面。</summary>
        /// <param name="upper">上层标识。</param>
        /// <param name="sub">下层标识。</param>
        /// <returns>在的话返回 true。</returns>
        public bool HasSub(S upper, S sub)
        {
            UpperLayer layer;

            return m_uppers.TryGetValue(upper, out layer) && layer.Subs.ContainsKey(sub);
        }

        // ====================================================================
        //  启动 / 推进
        // ====================================================================

        /// <summary>
        /// 启动：进入指定的上层，并**自动**进入它的入口下层。
        /// </summary>
        /// <param name="upper">初始上层。</param>
        public void Start(S upper)
        {
            Start(upper, default(S), false);
        }

        /// <summary>
        /// 启动：进入指定的上层与下层。
        /// </summary>
        /// <param name="upper">初始上层。</param>
        /// <param name="sub">初始下层。</param>
        public void Start(S upper, S sub)
        {
            Start(upper, sub, true);
        }

        /// <summary>推进一帧。**先下层、后上层**（语义②）。</summary>
        /// <param name="frame">逻辑帧号，只能比上次大。</param>
        public void Tick(int frame)
        {
            if (m_frame >= 0 && frame < m_frame)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frame), frame,
                    "[HierarchicalStateMachine] 逻辑帧号倒退了（上次是 " + m_frame + "）。" +
                    "帧号只增不减；倒退会让所有\"持续 N 帧\"的判断失效。");
            }

            int delta = m_frame < 0 ? 0 : frame - m_frame;
            m_frame = frame;

            if (!m_started)
            {
                return;
            }

            StateTick tick = new StateTick(frame, delta);

            IState<S> lower = LowerState;
            if (lower != null)
            {
                lower.OnUpdate(tick);
            }

            IState<S> upper = UpperState;
            if (upper != null)
            {
                upper.OnUpdate(tick);
            }

            DrainPending();
        }

        // ====================================================================
        //  转移
        // ====================================================================

        /// <summary>切到某个上层，下层自动进它的入口状态（FSM-05）。</summary>
        /// <param name="upper">目标上层。</param>
        /// <returns>true 表示执行了（或被排队）。</returns>
        public bool TryChangeUpper(S upper)
        {
            UpperLayer layer = RequireUpper(upper);

            if (!layer.Subs.ContainsKey(layer.EntrySub))
            {
                throw new InvalidOperationException(
                    "[HierarchicalStateMachine] 上层 «" + upper + "» 的入口下层 «" + layer.EntrySub +
                    "» 没有注册过。请为它注册至少一个下层状态。");
            }

            return RequestChange(upper, layer.EntrySub, false);
        }

        /// <summary>切到某个上层，并**同时**指定它下面的哪个状态。</summary>
        /// <param name="upper">目标上层。</param>
        /// <param name="sub">目标下层（必须注册在这个上层下）。</param>
        /// <returns>true 表示执行了（或被排队）。</returns>
        public bool TryChangeUpper(S upper, S sub)
        {
            return RequestChange(upper, sub, false);
        }

        /// <summary>
        /// 只切下层（上层不变）。
        /// <para>⚠️ 目标下层必须注册在**当前上层**下；否则抛异常并说明"是分层拦住了你"。</para>
        /// </summary>
        /// <param name="sub">目标下层。</param>
        /// <returns>true 表示执行了（或被排队）。</returns>
        public bool TryChangeLower(S sub)
        {
            if (!m_started)
            {
                throw new InvalidOperationException(
                    "[HierarchicalStateMachine] 还没 Start 就想切下层。请先 Start(初始上层)。");
            }

            return RequestChange(UpperCurrent, sub, false);
        }

        /// <summary>**强制**切换（跳过 `CanEnterFrom`）。用于复活、剧情强制、调试。</summary>
        /// <param name="upper">目标上层。</param>
        /// <param name="sub">目标下层。</param>
        /// <returns>true 表示执行了。</returns>
        public bool ForceChange(S upper, S sub)
        {
            return RequestChange(upper, sub, true);
        }

        /// <summary>
        /// 把一个事件交给状态处理：**先下层、后上层**，下层说"我处理了"就停（语义①）。
        /// </summary>
        /// <param name="id">事件标识。</param>
        /// <returns>true 表示某一层处理了。</returns>
        public bool SendEvent(EventId id)
        {
            if (!id.IsValid)
            {
                throw new ArgumentException("[HierarchicalStateMachine] 事件标识无效。", nameof(id));
            }

            IState<S> lower = LowerState;

            if (lower != null && lower.OnEvent(id))
            {
                return true;
            }

            IState<S> upper = UpperState;

            return upper != null && upper.OnEvent(id);
        }

        // ====================================================================
        //  历史（FSM-08）
        // ====================================================================

        /// <summary>把转移历史拷进一个列表（**新的在前**）。</summary>
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

        /// <summary>启动的实现。</summary>
        /// <param name="upper">初始上层。</param>
        /// <param name="sub">初始下层。</param>
        /// <param name="hasSub">是否显式指定了下层。</param>
        private void Start(S upper, S sub, bool hasSub)
        {
            if (m_started)
            {
                throw new InvalidOperationException(
                    "[HierarchicalStateMachine] 已经启动过了（当前 " + CurrentPath + "）。" +
                    "要重置请重新 new 一个 —— 复用同一个实例会让旧状态收不到 OnExit。");
            }

            UpperLayer layer = RequireUpper(upper);

            if (!hasSub)
            {
                sub = layer.EntrySub;
            }

            IState<S> subState = RequireSub(upper, sub);

            m_started = true;
            m_changing = true;

            try
            {
                UpperCurrent = upper;
                LowerCurrent = sub;

                layer.State.OnEnter(upper);
                subState.OnEnter(sub);
            }
            finally
            {
                m_changing = false;
            }

            RecordChange(upper + "/" + sub, upper + "/" + sub, false, true);
            DrainPending();
        }

        /// <summary>请求一次转移（统一入口）。</summary>
        /// <param name="upper">目标上层。</param>
        /// <param name="sub">目标下层。</param>
        /// <param name="forced">是否强制。</param>
        /// <returns>是否被接受。</returns>
        private bool RequestChange(S upper, S sub, bool forced)
        {
            if (!m_started)
            {
                throw new InvalidOperationException(
                    "[HierarchicalStateMachine] 还没 Start 就想切状态。请先 Start(初始上层)。");
            }

            RequireUpper(upper);
            RequireSub(upper, sub);

            bool sameConfiguration = Equals(upper, UpperCurrent) && Equals(sub, LowerCurrent);

            if (sameConfiguration && !forced && !m_hasPending)
            {
                return false;
            }

            if (m_changing)
            {
                m_hasPending = true;
                m_pendingUpper = upper;
                m_pendingSub = sub;
                m_pendingForced = forced;
                return true;
            }

            return Apply(upper, sub, forced);
        }

        /// <summary>
        /// 真正执行一次转移。
        /// <para>顺序：退下层 → 退上层 → 进上层 → 进下层。</para>
        /// </summary>
        /// <param name="upper">目标上层。</param>
        /// <param name="sub">目标下层。</param>
        /// <param name="forced">是否强制。</param>
        /// <returns>是否成功。</returns>
        private bool Apply(S upper, S sub, bool forced)
        {
            bool upperChanges = !Equals(upper, UpperCurrent);
            bool subChanges = upperChanges || !Equals(sub, LowerCurrent);

            if (!upperChanges && !subChanges)
            {
                return false;
            }

            UpperLayer targetLayer = RequireUpper(upper);
            IState<S> targetSub = RequireSub(upper, sub);

            if (!forced)
            {
                // 语义③：换上层问新上层；只换下层问新下层。两层各管各的。
                if (upperChanges && !targetLayer.State.CanEnterFrom(UpperCurrent))
                {
                    RaiseTransitionRejected(CurrentPath, upper + "/" + sub);
                    return false;
                }

                if (!upperChanges && !targetSub.CanEnterFrom(LowerCurrent))
                {
                    RaiseTransitionRejected(CurrentPath, upper + "/" + sub);
                    return false;
                }
            }

            S previousUpper = UpperCurrent;
            S previousSub = LowerCurrent;
            string previousPath = CurrentPath;

            IState<S> oldUpper = UpperState;
            IState<S> oldSub = LowerState;

            m_changing = true;

            try
            {
                if (subChanges && oldSub != null)
                {
                    oldSub.OnExit(sub);
                }

                if (upperChanges && oldUpper != null)
                {
                    oldUpper.OnExit(upper);
                }

                UpperCurrent = upper;
                LowerCurrent = sub;

                if (upperChanges)
                {
                    targetLayer.State.OnEnter(previousUpper);
                }

                if (subChanges)
                {
                    targetSub.OnEnter(previousSub);
                }
            }
            finally
            {
                m_changing = false;
            }

            RecordChange(previousPath, upper + "/" + sub, forced, false);
            return true;
        }

        /// <summary>把排队的转移执行掉（如果有）。</summary>
        private void DrainPending()
        {
            if (!m_hasPending)
            {
                return;
            }

            S upper = m_pendingUpper;
            S sub = m_pendingSub;
            bool forced = m_pendingForced;
            m_hasPending = false;

            Apply(upper, sub, forced);
        }

        /// <summary>取上层，没注册就报清楚。</summary>
        /// <param name="upper">上层标识。</param>
        /// <returns>上层记录。</returns>
        private UpperLayer RequireUpper(S upper)
        {
            UpperLayer layer;

            if (!m_uppers.TryGetValue(upper, out layer))
            {
                throw new InvalidOperationException(
                    "[HierarchicalStateMachine] 上层状态 «" + upper + "» 没有注册过。" +
                    "请先 RegisterUpper(" + upper + ", new ...State(), 入口下层)。");
            }

            return layer;
        }

        /// <summary>
        /// 取下层，并**顺带把"分层"这条规则守住**。
        /// </summary>
        /// <param name="upper">它应该在哪个上层下。</param>
        /// <param name="sub">下层标识。</param>
        /// <returns>下层状态对象。</returns>
        private IState<S> RequireSub(S upper, S sub)
        {
            UpperLayer layer = RequireUpper(upper);

            IState<S> state;

            if (!layer.Subs.TryGetValue(sub, out state))
            {
                throw new InvalidOperationException(
                    "[HierarchicalStateMachine] 上层 «" + upper + "» 下面没有下层状态 «" + sub + "»。\n" +
                    "⚠️ 这就是**分层**在起作用：下层只属于某一个上层，不能跨上层乱跳。\n" +
                    "（如果 «" + sub + "» 确实属于 «" + upper + "»，请先 RegisterSub 注册它。）");
            }

            return state;
        }

        /// <summary>记一条历史并广播。</summary>
        /// <param name="fromPath">原路径。</param>
        /// <param name="toPath">目标路径。</param>
        /// <param name="forced">是否强制。</param>
        /// <param name="silent">true 表示只记录、不广播。</param>
        private void RecordChange(string fromPath, string toPath, bool forced, bool silent)
        {
            StateChangeRecord record = new StateChangeRecord(m_frame, fromPath, toPath, forced);

            m_history.Add(record);

            while (m_history.Count > m_historyCapacity)
            {
                m_history.RemoveAt(0);
            }

            if (!silent)
            {
                Action<StateChangeRecord> handlers = StateChanged;

                if (handlers != null)
                {
                    handlers(record);
                }
            }
        }

        /// <summary>广播"这个转移被拒绝了"。</summary>
        /// <param name="fromPath">原路径。</param>
        /// <param name="toPath">目标路径。</param>
        private void RaiseTransitionRejected(string fromPath, string toPath)
        {
            Action<StateChangeRecord> handlers = TransitionRejected;

            if (handlers != null)
            {
                handlers(new StateChangeRecord(m_frame, fromPath, toPath, false));
            }
        }
    }
}
