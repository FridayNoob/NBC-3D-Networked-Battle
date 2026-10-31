// 这个文件里有：
//   StateMachine（类）：单层有限状态机 —— 注册状态对象、按逻辑帧推进、按目标状态的准入规则切换，并保留最近若干条转移记录；纯 C#，服务端与客户端编同一份源码。

#nullable disable
// ↑ 双端共用（服务端也编它，见 Server\NBC.Server.Game\NBC.Server.Game.csproj）：服务端开了可空、Unity 没开，
//   这里统一关掉，与 Shared\ 是同一条理由。
using System;
using System.Collections.Generic;
using NBC.Framework;

namespace NBC.Framework.Fsm
{
    // 主要功能：单层有限状态机 —— 注册状态对象、按逻辑帧推进、按目标状态的准入规则切换，并保留最近若干条转移记录；纯 C#，服务端与客户端编同一份源码。
    // 主要数据成员：m_states（状态标识到状态对象的表）、m_history / m_historyCapacity（转移记录与容量，默认 10 条）、m_frame（逻辑帧号，-1 表示还没推进过）、m_changing / m_hasPending 等转移排队标记。
    // 主要外部方法：Register / IsRegistered / Start / Tick / TryChangeState / ForceChangeState / SendEvent / CopyHistory / ClearHistory，以及 Current / CurrentState / CurrentName 等只读属性和 StateChanged / TransitionRejected 事件。
    // 谁在用它：目前只有 EditMode 的 StateMachineTests 实例化使用（Game / Boot / Editor 还没接入）；分层版是另一个类 HierarchicalStateMachine，不复用本类。
    /// <summary>单层有限状态机：注册状态对象、按逻辑帧推进、按目标状态的准入规则切换，并保留最近若干条转移记录；纯 C#，服务端与客户端编同一份源码。</summary>
    /// <typeparam name="S">状态标识类型（通常是 enum），同时用作字典键。</typeparam>
    public sealed class StateMachine<S>
    {
        /// <summary>默认保留多少条转移记录；构造时可覆盖，容量在构造后不再变化。</summary>
        public const int DefaultHistoryCapacity = 10;

        // 已注册的状态表：键是状态标识，值是对应的状态对象；同一标识只能注册一次。
        private readonly Dictionary<S, IState<S>> m_states = new Dictionary<S, IState<S>>();
        // 转移历史，按发生顺序追加（最旧在前）；CopyHistory 输出时才反转成新的在前。
        private readonly List<StateChangeRecord> m_history = new List<StateChangeRecord>();
        // 历史最多保留多少条（不小于 1），构造时定下、之后不变。
        private readonly int m_historyCapacity;

        // 当前逻辑帧号，由 Tick 注入；-1 表示还没推进过（逻辑帧，不是 Unity 帧）。
        private int m_frame = -1;
        // 是否已经 Start 过；只用于挡住重复 Start。
        private bool m_started;
        // 是否正在转移中（OnExit / OnEnter 执行期间为 true），用来把中途请求排队而不是递归执行。
        private bool m_changing;

        // 是否有排队的转移请求；只有这一个位置。
        private bool m_hasPending;
        // 排队的目标状态标识；只在 m_hasPending 为 true 时有意义。
        private S m_pendingNext;
        // 排队请求是否强制转移；只在 m_hasPending 为 true 时有意义。
        private bool m_pendingForced;

        /// <summary>构造状态机；历史上限在此时定下，之后不再变化。</summary>
        /// <param name="historyCapacity">最多保留多少条转移记录；小于等于 0 时按默认值 10 处理。</param>
        public StateMachine(int historyCapacity = DefaultHistoryCapacity)
        {
            // 功能：构造状态机并定下转移历史的容量上限。
            // 入参：historyCapacity 最多保留几条转移记录，小于等于 0 当作默认值；默认 10 条
            // 返回：无
            // 步骤：非法容量在构造时一次性兜底成默认值，之后 RecordChange 只管按这个上限裁剪，不再判非法。
            // 上游：外部 new 状态机的代码（目前是测试）
            // 下游：无
            m_historyCapacity = historyCapacity > 0 ? historyCapacity : DefaultHistoryCapacity;
        }

        /// <summary>当前状态标识；Start 之前是类型默认值，不代表任何状态，Start 之后才可信。</summary>
        public S Current { get; private set; }

        /// <summary>是否已经 Start 过；Start 只允许调用一次。</summary>
        public bool IsStarted
        {
            get { return m_started; }
        }

        /// <summary>当前逻辑帧号（不是 Unity 帧）；还没推进过时是 -1，Tick 之后只增不减。</summary>
        public int Frame
        {
            get { return m_frame; }
        }

        /// <summary>当前状态对象；没 Start 或当前标识没注册时是 null。</summary>
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

        /// <summary>当前状态显示名，调试面板直接显示；没 Start 时是「未开始」占位文本。</summary>
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

        /// <summary>已记录的转移条数；上限是构造时给的容量。</summary>
        public int HistoryCount
        {
            get { return m_history.Count; }
        }

        /// <summary>状态切换完成时触发；参数是本次转移记录，在 OnExit / OnEnter 都结束之后才发出。</summary>
        public event Action<StateChangeRecord> StateChanged;

        /// <summary>一次转移被目标状态的 CanEnterFrom 拒绝时触发；实际状态没有变。</summary>
        public event Action<StateChangeRecord> TransitionRejected;

        /// <summary>注册一个状态对象；同一状态标识只能注册一次。</summary>
        /// <param name="id">状态标识，同时用作字典键。</param>
        /// <param name="state">状态对象；为 null 时抛异常。</param>
        public void Register(S id, IState<S> state)
        {
            // 功能：把一个状态对象登记到状态标识上，供 Start 与切换时按标识取用。
            // 入参：id 状态标识；state 状态对象，不能为 null
            // 返回：无
            // 步骤：
            //   1) 先挡 null —— 把 null 注册进去的话，第一次切换才会炸，届时看不出是谁注册的
            //   2) 再挡重复注册 —— 同一标识映射到两个状态对象时，切换拿到哪一个全看不出来
            //   3) 最后才写入字典，保证前面失败时字典没被改动
            // 上游：外部启动前的注册代码（目前是测试）
            // 下游：m_states
            // 注意：重复注册抛异常而不是覆盖 —— 同一标识对应两个状态对象时，切换拿到哪一个全看不出来；要换实现请重新 new 一个状态机。
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

        /// <summary>某个状态标识注册过没有。</summary>
        /// <param name="id">状态标识。</param>
        /// <returns>注册过返回 true。</returns>
        public bool IsRegistered(S id)
        {
            // 功能：查询某个状态标识是否已经注册。
            // 入参：id 状态标识
            // 返回：注册过返回 true，否则 false
            // 步骤：只查表，不触发事件、不改状态；未注册只返回 false，不抛异常。
            // 上游：外部查询代码（目前是测试）
            // 下游：m_states
            return m_states.ContainsKey(id);
        }

        /// <summary>启动状态机：进入初始状态并写一条起始记录。</summary>
        /// <param name="initial">初始状态标识；必须已经注册，否则抛异常。</param>
        public void Start(S initial)
        {
            // 功能：启动状态机，进入初始状态并把这次进入记成一条历史（不广播 StateChanged）。
            // 入参：initial 初始状态标识，必须已经 Register 过
            // 返回：无
            // 步骤：
            //   1) 先挡重复 Start —— 复用同一个实例会让旧状态收不到 OnExit，要重置请重新 new 一个
            //   2) 取状态对象（没注册就抛异常），再把状态机标成已启动、正在转移
            //   3) 设当前状态并调 OnEnter(initial)；首次进入的来源状态就是自己
            //   4) 记一条 silent 历史：起始不算切换，不广播 StateChanged
            //   5) 把 OnEnter 里排队的转移执行掉
            // 上游：外部启动代码（目前是测试）
            // 下游：Require / OnEnter / RecordChange / DrainPending
            // 注意：m_changing 用 try/finally 复位 —— OnEnter 抛异常时也要退出「正在转移」状态，否则之后所有切换都只会排队。
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

        /// <summary>推进一个逻辑帧：交给当前状态 OnUpdate，并执行它请求的转移；帧号只能比上次大。</summary>
        /// <param name="frame">逻辑帧号（不是 Unity 帧），必须不小于上次传入的值。</param>
        public void Tick(int frame)
        {
            // 功能：推进一个逻辑帧 —— 先把帧号差成相隔帧数，交给当前状态 OnUpdate，再统一执行其中请求的转移。
            // 入参：frame 逻辑帧号（不是 Unity 帧），必须不小于上次传入的值
            // 返回：无
            // 步骤：
            //   1) 先挡帧号倒退 —— 倒退会让所有「持续 N 帧」的判断失效，而且很难查，所以当场抛异常
            //   2) 算相隔帧数：第一次推进按 0 处理，不能拿 -1 去减
            //   3) 还没 Start 就只更新帧号，直接返回
            //   4) 调当前状态 OnUpdate（状态对象缺失时跳过）
            //   5) 在 OnUpdate 之后执行排队转移 —— 避免一边遍历状态一边改状态
            // 上游：外部逻辑帧驱动（目前是测试）；将来由逻辑层每个逻辑帧调一次
            // 下游：CurrentState / OnUpdate / DrainPending
            // 注意：不要放进 Unity 的 Update 里靠真实时间驱动 —— 逻辑帧号必须由逻辑层给，客户端与服务端才会算出同一结果；同一帧重复推进是允许的（相隔帧数为 0）。
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

            DrainPending();
        }

        /// <summary>请求切换到某个状态；先问目标状态准不准入，被拒绝时返回 false 并触发 TransitionRejected。</summary>
        /// <param name="next">目标状态标识；必须已经注册。</param>
        /// <returns>true 表示这次转移真的执行了，或被排进了队列；false 表示被忽略（切到自己）或被目标状态拒绝。</returns>
        public bool TryChangeState(S next)
        {
            // 功能：按准入规则请求切换到目标状态（跳过准入的强制版见 ForceChangeState）。
            return RequestChange(next, false);
        }

        /// <summary>强制切换：跳过目标状态的 CanEnterFrom 检查，用于复活、剧情强制与调试。</summary>
        /// <param name="next">目标状态标识；必须已经注册。</param>
        /// <returns>true 表示请求被接受（执行了或排进了队列）；false 表示目标就是当前状态、没有执行。</returns>
        public bool ForceChangeState(S next)
        {
            // 功能：跳过准入检查直接切到目标状态；复活、剧情强制与调试用。
            return RequestChange(next, true);
        }

        /// <summary>把事件交给当前状态处理；无效事件标识直接抛异常。</summary>
        /// <param name="id">事件标识，不能是默认无效值。</param>
        /// <returns>true 表示当前状态处理了；false 表示没处理，或还没 Start。</returns>
        public bool SendEvent(EventId id)
        {
            // 功能：把事件转给当前状态，并把「处理没处理」原样告诉调用方。
            // 入参：id 事件标识，必须是有效值（default 视为无效）
            // 返回：true 表示状态说它处理了；false 表示没处理，或还没 Start（没有当前状态）
            // 步骤：
            //   1) 先校验事件标识，无效值直接抛异常 —— 避免把 default 当事件静默丢掉
            //   2) 没有当前状态就返回 false，不抛异常
            //   3) 否则交给当前状态 OnEvent，返回值原样透出
            // 上游：外部事件输入（目前是测试）
            // 下游：CurrentState / OnEvent
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

        /// <summary>把转移历史拷进调用方的列表，新的在前，方便直接按顺序显示。</summary>
        /// <param name="buffer">接收结果的列表，会先被清空；传 null 时直接返回，不抛异常。</param>
        public void CopyHistory(List<StateChangeRecord> buffer)
        {
            // 功能：把转移历史按新的在前的顺序拷进调用方给的列表。
            // 入参：buffer 接收结果的列表（会先清空）；为 null 时直接返回，不抛异常
            // 返回：无
            // 步骤：
            //   1) 先清空 buffer —— 否则调用方复用同一个列表会看到旧数据
            //   2) 从历史末尾往前拷，得到新的在前，调用方不用再反转
            // 上游：外部读取历史的代码（目前是测试，将来给调试面板）
            // 下游：m_history
            // 注意：历史只留最近 m_historyCapacity 条（默认 10），更早的已经被挤掉，别假设能拿到全量。
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

        /// <summary>清空转移历史；不影响当前状态与已注册的状态。</summary>
        public void ClearHistory()
        {
            // 功能：清空转移历史，方便调试时重新观察。
            // 入参：无
            // 返回：无
            // 步骤：直接清空列表，不广播事件 —— 历史是调试数据，清空不需要通知订阅者。
            // 上游：外部清理历史的代码（目前是测试，将来给调试面板）
            // 下游：m_history
            m_history.Clear();
        }

        private bool RequestChange(S next, bool forced)
        {
            // 功能：所有切换请求的统一入口 —— 决定这次是忽略、排队还是立即执行。
            // 入参：next 目标状态标识（必须已注册）；forced 是否跳过目标状态的准入检查
            // 返回：true 表示这次转移被执行了，或被排进了队列；false 表示没有执行（切到自己算空操作，或被目标状态拒绝）
            // 步骤：
            //   1) 没 Start 就抛异常 —— 这时切状态没有「从哪来」，语义不成立
            //   2) 先 Require(next)，未注册的目标在这里就报清楚
            //   3) 非强制、目标就是当前状态、且没有排队请求时直接返回 false：切到自己算空操作，也不发事件，否则每帧调一次会把历史刷爆
            //   4) 正在转移中时只记进唯一一个待处理位置，等本次转移完整结束再执行
            //   5) 否则立即 Apply
            // 上游：TryChangeState / ForceChangeState
            // 下游：Require / Apply（排队项由 DrainPending 负责执行）
            // 注意：排队只有这一个位置，连续请求只保留最后一次 —— 语义是「最终停在哪个状态」，不是按顺序播一遍。
            // 注意：强制切到当前状态不会被第 3 步挡住，但 Apply 里会再挡一次并返回 false，不会重复进 OnEnter。
            if (!m_started)
            {
                throw new InvalidOperationException(
                    "[StateMachine] 还没 Start 就想切状态。请先 Start(初始状态)。");
            }

            Require(next);

            if (!forced && Equals(next, Current) && !m_hasPending)
            {
                return false;
            }

            if (m_changing)
            {
                m_hasPending = true;
                m_pendingNext = next;
                m_pendingForced = forced;
                return true;
            }

            return Apply(next, forced);
        }

        private bool Apply(S next, bool forced)
        {
            // 功能：真正执行一次转移，负责 OnExit / OnEnter 的顺序与历史记录。
            // 入参：next 目标状态标识（必须已注册）；forced 是否跳过目标状态的准入检查
            // 返回：true 表示真的切过去了；false 表示目标就是当前状态，或目标状态拒绝准入
            // 步骤：
            //   1) 先取目标对象并再挡一次「切到自己」—— 防止强制路径绕过空操作判断
            //   2) 非强制时问目标状态 CanEnterFrom(来源)：准入规则集中在目标状态里，而不是问当前状态放不放行；拒绝就广播一条拒绝记录并返回 false
            //   3) 标记正在转移，先调旧状态 OnExit(next)，再改 Current，最后调新状态 OnEnter(previous)
            //   4) 用 try/finally 复位 m_changing：OnExit / OnEnter 抛异常也不能把状态机卡在「永远正在转移」
            //   5) 记一条非 silent 历史并广播 StateChanged
            // 上游：RequestChange / DrainPending
            // 下游：Require / CanEnterFrom / OnExit / OnEnter / RecordChange
            // 注意：OnExit 时当前状态还是旧状态，OnEnter 时已经指向新状态 —— 两个回调里读 Current 会得到不同结果。
            S previous = Current;
            IState<S> target = Require(next);

            if (Equals(next, previous))
            {
                return false;
            }

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

        private void DrainPending()
        {
            // 功能：执行掉当前排队的转移请求（如果有）。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 没有排队就直接返回
            //   2) 先取出目标与强制标记并清掉排队标志，再 Apply —— 这样 Apply 里新排的请求会留给下一轮
            //   3) 只处理一次：排队的转移自己再排一个时下一轮再说，免得互相来回切的状态把这一帧卡死
            // 上游：Start / Tick
            // 下游：Apply
            if (!m_hasPending)
            {
                return;
            }

            S next = m_pendingNext;
            bool forced = m_pendingForced;
            m_hasPending = false;

            Apply(next, forced);
        }

        private IState<S> Require(S id)
        {
            // 功能：按标识取状态对象，查不到时抛一条能照着修的异常。
            // 入参：id 状态标识
            // 返回：已注册的状态对象，不会为 null
            // 步骤：直接查表；查不到就抛异常，消息里带上标识与注册示例 —— 这是最常见的初始化漏配。
            // 上游：Start / RequestChange / Apply
            // 下游：m_states
            IState<S> state;

            if (!m_states.TryGetValue(id, out state))
            {
                throw new InvalidOperationException(
                    "[StateMachine] 状态 «" + id + "» 没有注册过。" +
                    "请先 Register(" + id + ", new ...State())。");
            }

            return state;
        }

        private string NameOf(S id)
        {
            // 功能：取状态显示名，用于转移记录与调试文本。
            // 入参：id 状态标识
            // 返回：注册过返回状态对象的 Name；没注册退回 id.ToString()
            // 步骤：查表命中就用状态名，否则退回标识本身 —— 记录历史时不该因为一个未注册的标识再抛异常。
            // 上游：RecordChange / Apply（拒绝记录）
            // 下游：m_states
            IState<S> state;
            return m_states.TryGetValue(id, out state) ? state.Name : id.ToString();
        }

        private void RecordChange(S from, S to, bool forced, bool silent)
        {
            // 功能：把一次转移记成历史，超过容量就丢掉最旧的，并按需广播。
            // 入参：from 原状态；to 目标状态；forced 是否强制；silent true 表示只记录不广播（Start 用）
            // 返回：无
            // 步骤：
            //   1) 用当前帧号与两端显示名构造记录 —— 记录里只存名字，状态对象以后换掉也不影响回看
            //   2) 追加到历史末尾
            //   3) 超容量就从最旧的开始删，保证总量不超过 m_historyCapacity
            //   4) 非 silent 才广播 StateChanged；Start 的起始记录不算切换，不广播
            // 上游：Start / Apply
            // 下游：NameOf / m_history / RaiseStateChanged
            StateChangeRecord record =
                new StateChangeRecord(m_frame, NameOf(from), NameOf(to), forced);

            m_history.Add(record);

            while (m_history.Count > m_historyCapacity)
            {
                m_history.RemoveAt(0);
            }

            if (!silent)
            {
                RaiseStateChanged(record);
            }
        }

        private void RaiseStateChanged(StateChangeRecord record)
        {
            // 功能：把一条转移记录广播给 StateChanged 的订阅者。
            // 入参：record 本次转移记录
            // 返回：无
            // 步骤：先把委托取到局部变量再判空调用 —— 避免回调里退订后这次调用拿到 null。
            // 上游：RecordChange（非 silent 时）
            // 下游：StateChanged 订阅者
            // 注意：这里直接用 C# 事件而不是事件中心 —— 状态切换每帧都可能发生，事件中心是给低频游戏事件用的（字典查找 + 委托链 + 类型检查）；要进全局事件流由游戏层自己转发。
            Action<StateChangeRecord> handlers = StateChanged;

            if (handlers != null)
            {
                handlers(record);
            }
        }

        private void RaiseTransitionRejected(StateChangeRecord record)
        {
            // 功能：把一个被拒绝的转移记录广播给 TransitionRejected 的订阅者。
            // 入参：record 被拒绝的转移记录（这种记录的 Forced 恒为 false）
            // 返回：无
            // 步骤：先把委托取到局部变量再判空调用，避免回调里退订导致的空引用。
            // 上游：Apply（CanEnterFrom 拒绝时）
            // 下游：TransitionRejected 订阅者
            Action<StateChangeRecord> handlers = TransitionRejected;

            if (handlers != null)
            {
                handlers(record);
            }
        }
    }
}
