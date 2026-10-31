// 这个文件里有：
//   HierarchicalStateMachine（类）：固定两层的分层状态机 —— 上层互斥、每个上层自带一组下层，按逻辑帧推进、按目标状态准入切换，并保留最近若干条转移记录。
//   UpperLayer（嵌套私有类）：一个上层状态加它自带的那组下层状态（入口下层与下层表），只在本文件内部用。

#nullable disable
// ↑ 双端共用（服务端也编它，见 Server\NBC.Server.Game\NBC.Server.Game.csproj）：服务端开了可空、Unity 没开，
//   这里统一关掉，与 Shared\ 是同一条理由。
using System;
using System.Collections.Generic;
using NBC.Framework;

namespace NBC.Framework.Fsm
{
    // 主要功能：固定两层的分层状态机 —— 上层互斥，每个上层自带一组下层；按逻辑帧推进，按目标状态准入切换，并保留最近若干条转移记录；纯 C#，服务端与客户端编同一份源码。
    // 主要数据成员：m_uppers（上层标识 → UpperLayer，每个 UpperLayer 自带入口下层与下层表）、m_history / m_historyCapacity（转移记录与容量，默认 10 条）、m_frame（逻辑帧号，-1 表示还没推进过）、m_changing / m_hasPending 等转移排队标记。
    // 主要外部方法：RegisterUpper / RegisterSub / HasSub / Start / Tick / TryChangeUpper / TryChangeLower / ForceChange / SendEvent / CopyHistory / ClearHistory，以及 UpperCurrent / LowerCurrent / CurrentPath 等只读属性与 StateChanged / TransitionRejected 事件。
    // 谁在用它：目前只有 EditMode 的 HierarchicalStateMachineTests 实例化使用（Game / Boot / Editor 还没接入）；单层版是另一个类 StateMachine，本类不复用它。
    // 注意：本类固定两层（上层 Locomotion / Action / Special，下层 Idle / Run / Attack…）：下层只属于某一个上层、不共享，也不能跨上层乱跳；将来真要三层是新增一层，不是推翻本类。
    // 注意：事件与 Tick 都是先下层、后上层；事件下层返回 true 就停，否则同一个事件会被两层各处理一次。
    // 注意：换上层问新上层 CanEnterFrom（旧上层），只换下层问新下层 CanEnterFrom（旧下层），两层各管各的；切到当前已在的配置是空操作，不记录也不广播。
    // 注意：状态机不可重入 —— OnExit / OnEnter 里再请求转移只会排队，且只有一个位置，连续请求只保留最后一次。
    /// <summary>固定两层的分层状态机：上层互斥、每个上层自带一组下层，按逻辑帧推进并保留最近若干条转移记录；纯 C#，服务端与客户端编同一份源码。</summary>
    /// <typeparam name="S">状态标识类型（通常是 enum），同时用作字典键。</typeparam>
    public sealed class HierarchicalStateMachine<S>
    {
        /// <summary>默认保留多少条转移记录；构造时可覆盖，容量在构造后不再变化。</summary>
        public const int DefaultHistoryCapacity = 10;

        // 主要功能：一个上层状态加它自带的那组下层状态 —— 分层数据只存在这里，状态机本身不另存跨层共享表。
        // 主要数据成员：State（上层状态对象）、EntrySub（入口下层标识）、Subs（下层标识 → 状态对象）。
        // 主要外部方法：无方法，只被 HierarchicalStateMachine 读写。
        // 谁在用它：只在本文件的 HierarchicalStateMachine 里构造与查表。
        private sealed class UpperLayer
        {
            // 上层状态对象；由 RegisterUpper 写入，之后不再替换。
            public IState<S> State;

            // 切到这个上层时默认进入的下层标识（入口状态）；取值必须是本上层已注册的下层，切进来时还没注册会抛异常；RegisterSub 注册第一个下层时会改写它。
            public S EntrySub;

            // 这个上层自带的下层状态表（下层标识 → 状态对象），注册后只增不减；不与别的上层共享，所以下层不能跨上层乱跳。
            public readonly Dictionary<S, IState<S>> Subs = new Dictionary<S, IState<S>>();
        }

        // 上层状态表：上层标识 → UpperLayer（状态对象 + 入口下层 + 下层表）；上层互斥，同一标识只能注册一次。
        private readonly Dictionary<S, UpperLayer> m_uppers = new Dictionary<S, UpperLayer>();
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

        // 是否有排队的转移请求；只有这一个位置，新的覆盖旧的。
        private bool m_hasPending;
        // 排队的目标上层标识；只在 m_hasPending 为 true 时有意义。
        private S m_pendingUpper;
        // 排队的目标下层标识；只在 m_hasPending 为 true 时有意义。
        private S m_pendingSub;
        // 排队请求是否强制跳过准入检查；只在 m_hasPending 为 true 时有意义。
        private bool m_pendingForced;

        /// <summary>构造状态机；历史上限在此时定下，之后不再变化。</summary>
        /// <param name="historyCapacity">最多保留多少条转移记录；小于等于 0 时按默认值 10 处理。</param>
        public HierarchicalStateMachine(int historyCapacity = DefaultHistoryCapacity)
        {
            // 功能：构造状态机并定下转移历史的容量上限。
            // 入参：historyCapacity 最多保留几条转移记录，小于等于 0 当作默认值；默认 10 条
            // 返回：无
            // 步骤：非法容量在构造时一次性兜底成默认值，之后 RecordChange 只管按这个上限裁剪，不再判非法。
            // 上游：外部 new 状态机的代码（目前是测试）
            // 下游：无
            m_historyCapacity = historyCapacity > 0 ? historyCapacity : DefaultHistoryCapacity;
        }

        /// <summary>当前上层状态标识；Start 之前是类型默认值，不代表任何状态。</summary>
        public S UpperCurrent { get; private set; }

        /// <summary>当前下层状态标识（属于当前上层自带的那组下层）；Start 之前是类型默认值。</summary>
        public S LowerCurrent { get; private set; }

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

        /// <summary>当前上层状态对象；没 Start 时是 null。</summary>
        public IState<S> UpperState
        {
            get
            {
                UpperLayer layer;
                return m_started && m_uppers.TryGetValue(UpperCurrent, out layer) ? layer.State : null;
            }
        }

        /// <summary>当前下层状态对象（只在当前上层自带的下层表里查）；没 Start 时是 null。</summary>
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

        /// <summary>当前完整路径，形如 Ground/Run，调试面板直接显示；没 Start 时返回占位文本（未开始）。</summary>
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

        /// <summary>已记录的转移条数；上限是构造时给的容量。</summary>
        public int HistoryCount
        {
            get { return m_history.Count; }
        }

        /// <summary>状态切换完成时触发；参数是本次转移记录，在 OnExit / OnEnter 都结束之后才发出，Start 的起始记录不广播。</summary>
        public event Action<StateChangeRecord> StateChanged;

        /// <summary>一次转移被 CanEnterFrom 拒绝时触发；实际状态没有变，记录里的 Forced 恒为 false。</summary>
        public event Action<StateChangeRecord> TransitionRejected;

        /// <summary>注册一个上层状态，并指定它自带的入口下层；同一上层标识只能注册一次。</summary>
        /// <param name="upper">上层标识，同时用作字典键。</param>
        /// <param name="state">上层状态对象；为 null 时抛异常。</param>
        /// <param name="entrySub">切到这个上层时下层先进哪个；可以先给一个稍后才注册的下层。</param>
        public void RegisterUpper(S upper, IState<S> state, S entrySub)
        {
            // 功能：把一个上层状态登记到上层标识上，并记下它的入口下层。
            // 入参：upper 上层标识；state 上层状态对象，不能为 null；entrySub 切到这个上层时下层先进哪个
            // 返回：无
            // 步骤：
            //   1) 先挡 null —— 把 null 注册进去的话，第一次切到它才会炸，届时看不出是谁注册的
            //   2) 再挡重复注册 —— 同一上层映射到两个状态对象时，切换拿到哪一个全看不出来
            //   3) 建 UpperLayer 写入状态与入口，下层表先留空，由 RegisterSub 填
            // 上游：外部启动前的注册代码（目前是测试）
            // 下游：m_uppers
            // 注意：入口下层此刻可以还没注册，但切进这个上层之前必须已注册，否则会抛入口下层没有注册过。
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

        /// <summary>给某个上层注册一个下层状态；下层只属于这一个上层，不会共享。</summary>
        /// <param name="upper">它属于哪个上层；该上层必须已经 RegisterUpper。</param>
        /// <param name="sub">下层标识。</param>
        /// <param name="state">下层状态对象；为 null 时抛异常。</param>
        public void RegisterSub(S upper, S sub, IState<S> state)
        {
            // 功能：把一个下层状态挂到指定上层自带的表里，并让第一个注册的下层成为入口。
            // 入参：upper 它属于哪个上层；sub 下层标识；state 下层状态对象，不能为 null
            // 返回：无
            // 步骤：
            //   1) 先挡 null、再取上层 —— 上层没注册会直接抛异常，这时不该往下写
            //   2) 挡同一上层下重复注册：同一标识对应两个状态对象时，切换拿到哪一个全看不出来
            //   3) 写入下层表；若是第一个，顺带把 EntrySub 改成它 —— 调用方不用再记入口是哪个
            // 上游：外部启动前的注册代码（目前是测试）
            // 下游：RequireUpper / layer.Subs / layer.EntrySub
            // 注意：第一个注册的下层会覆盖 RegisterUpper 给的入口 —— 想让某个下层当入口，就把它第一个注册。
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

            if (layer.Subs.Count == 1)
            {
                layer.EntrySub = sub;
            }
        }

        /// <summary>某个下层是不是注册在指定上层下面；上层没注册时返回 false，不抛异常。</summary>
        /// <param name="upper">上层标识。</param>
        /// <param name="sub">下层标识。</param>
        /// <returns>从属关系对得上时返回 true。</returns>
        public bool HasSub(S upper, S sub)
        {
            // 功能：查询某个下层是否挂在指定上层的表里，用来提前判断分层允不允许这次切换。
            // 入参：upper 上层标识；sub 下层标识
            // 返回：都注册且从属关系对得上时返回 true；上层没注册只返回 false，不抛异常
            // 步骤：一次查上层表再查它的下层表；查不到就返回 false，不做异常。
            // 上游：外部查询代码（目前是测试，将来给调试面板）
            // 下游：m_uppers / layer.Subs
            UpperLayer layer;

            return m_uppers.TryGetValue(upper, out layer) && layer.Subs.ContainsKey(sub);
        }

        /// <summary>启动：进入指定上层，并自动进入它的入口下层；只能调用一次。</summary>
        /// <param name="upper">初始上层；必须已经注册。</param>
        public void Start(S upper)
        {
            // 功能：按上层启动，下层走该上层的入口。
            Start(upper, default(S), false);
        }

        /// <summary>启动：进入指定上层与下层；下层必须注册在该上层下，且只能调用一次。</summary>
        /// <param name="upper">初始上层；必须已经注册。</param>
        /// <param name="sub">初始下层；必须属于 upper。</param>
        public void Start(S upper, S sub)
        {
            // 功能：按上层加下层启动，显式指定下层而不是走入口。
            Start(upper, sub, true);
        }

        /// <summary>推进一个逻辑帧：先下层、后上层各 OnUpdate 一次，再执行排队转移；帧号只能比上次大。</summary>
        /// <param name="frame">逻辑帧号（不是 Unity 帧），必须不小于上次传入的值。</param>
        public void Tick(int frame)
        {
            // 功能：推进一个逻辑帧 —— 先把帧号差成相隔帧数，按先下层、后上层各调一次 OnUpdate，再统一执行其中请求的转移。
            // 入参：frame 逻辑帧号（不是 Unity 帧），必须不小于上次传入的值
            // 返回：无
            // 步骤：
            //   1) 先挡帧号倒退 —— 倒退会让所有持续 N 帧的判断失效，而且很难查，所以当场抛异常
            //   2) 算相隔帧数：第一次推进按 0 处理，不能拿 -1 去减
            //   3) 还没 Start 就只更新帧号，直接返回
            //   4) 两层收到同一份 StateTick：先下层 OnUpdate，再上层 OnUpdate —— 与事件派发同序，少一条要记的规则
            //   5) 两层都跑完才 DrainPending：避免一边推进一边改状态
            // 上游：外部逻辑帧驱动（目前是测试）；将来由逻辑层每个逻辑帧调一次
            // 下游：LowerState / UpperState / OnUpdate / DrainPending
            // 注意：不要放进 Unity 的 Update 里靠真实时间驱动 —— 逻辑帧号必须由逻辑层给，客户端与服务端才会算出同一结果；同一帧重复推进是允许的（相隔帧数为 0）。
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

        /// <summary>切到某个上层，下层自动进它的入口状态；目标上层必须已注册且入口下层可用。</summary>
        /// <param name="upper">目标上层。</param>
        /// <returns>true 表示执行了或被排进了队列；false 表示切到当前配置（空操作）或被目标上层拒绝。</returns>
        public bool TryChangeUpper(S upper)
        {
            // 功能：切到指定上层，下层跟着进这个上层的入口状态。
            // 入参：upper 目标上层标识，必须已经 RegisterUpper
            // 返回：true 表示执行了或被排进了队列；false 表示切到当前配置（空操作）或被目标上层的 CanEnterFrom 拒绝
            // 步骤：
            //   1) 先取上层并检查入口下层已注册 —— 入口没配好就切过去，会停在「上层变了、下层没有」的半截状态
            //   2) 再走统一入口 RequestChange，由它决定排队还是立即执行
            // 上游：外部切换请求与状态内部请求（目前是测试）
            // 下游：RequireUpper / RequestChange
            UpperLayer layer = RequireUpper(upper);

            if (!layer.Subs.ContainsKey(layer.EntrySub))
            {
                throw new InvalidOperationException(
                    "[HierarchicalStateMachine] 上层 «" + upper + "» 的入口下层 «" + layer.EntrySub +
                    "» 没有注册过。请为它注册至少一个下层状态。");
            }

            return RequestChange(upper, layer.EntrySub, false);
        }

        /// <summary>切到某个上层，并同时指定它下面的哪个状态；下层必须注册在该上层下。</summary>
        /// <param name="upper">目标上层。</param>
        /// <param name="sub">目标下层，必须属于 upper。</param>
        /// <returns>true 表示执行了或被排进了队列；false 表示切到当前配置（空操作）或被目标状态拒绝。</returns>
        public bool TryChangeUpper(S upper, S sub)
        {
            // 功能：切到指定上层并显式指定下层，不走入口。
            return RequestChange(upper, sub, false);
        }

        /// <summary>只切下层、上层不变；目标下层必须注册在当前上层下，否则抛异常。</summary>
        /// <param name="sub">目标下层，必须属于当前上层。</param>
        /// <returns>true 表示执行了或被排进了队列；false 表示目标就是当前下层（空操作）或被目标下层拒绝。</returns>
        public bool TryChangeLower(S sub)
        {
            // 功能：在当前上层下只换下层，上层不动。
            // 入参：sub 目标下层标识，必须注册在当前上层下
            // 返回：true 表示执行了或被排进了队列；false 表示目标就是当前下层（空操作）或被目标下层的 CanEnterFrom 拒绝
            // 步骤：
            //   1) 还没 Start 就抛异常 —— 没有当前上层，谈不上只换下层
            //   2) 用当前上层加目标下层走 RequestChange，由 RequireSub 守住下层只属于当前上层这条分层规则
            // 上游：外部切换请求与状态内部请求（目前是测试）
            // 下游：RequestChange / RequireSub
            // 注意：跨上层切下层会被 RequireSub 拒绝（例如 Ground 下切 Attack_2）—— 这是分层在拦，不是漏注册。
            if (!m_started)
            {
                throw new InvalidOperationException(
                    "[HierarchicalStateMachine] 还没 Start 就想切下层。请先 Start(初始上层)。");
            }

            return RequestChange(UpperCurrent, sub, false);
        }

        /// <summary>强制切换：跳过目标状态的 CanEnterFrom 检查，用于复活、剧情强制与调试。</summary>
        /// <param name="upper">目标上层；仍须已注册。</param>
        /// <param name="sub">目标下层；仍须属于 upper。</param>
        /// <returns>true 表示真的切过去了；false 表示目标就是当前配置、没有执行。</returns>
        public bool ForceChange(S upper, S sub)
        {
            // 功能：跳过准入检查直接切到指定上层加下层；复活、剧情强制与调试用。
            // 入参：upper 目标上层；sub 目标下层（必须注册在该上层下）
            // 返回：true 表示真的切过去了；false 表示目标就是当前配置、没有执行
            // 步骤：直接走 RequestChange 的强制路径 —— 仍然校验注册与分层从属，只是不问 CanEnterFrom。
            // 上游：外部强制切换代码（目前是测试）
            // 下游：RequestChange / Apply
            // 注意：强制只跳过准入检查，不跳过 RequireUpper / RequireSub；目标没注册照样抛异常。
            return RequestChange(upper, sub, true);
        }

        /// <summary>把一个事件交给状态处理：先下层、后上层，下层说处理了就停。</summary>
        /// <param name="id">事件标识，不能是默认无效值。</param>
        /// <returns>true 表示某一层处理了；false 表示两层都没处理，或还没 Start。</returns>
        public bool SendEvent(EventId id)
        {
            // 功能：把事件按先下层、后上层的顺序派发，下层返回 true 就不再往上传。
            // 入参：id 事件标识，必须是有效值（default 视为无效）
            // 返回：true 表示下层或上层处理了；false 表示两层都没处理，或还没 Start（没有状态对象）
            // 步骤：
            //   1) 先校验事件标识，无效值直接抛异常 —— 避免把 default 当事件静默丢掉
            //   2) 先问下层，返回 true 就停 —— 否则同一个事件会被两层各处理一次（按下闪避被上下层各吃一遍）
            //   3) 下层没处理才问上层，返回值原样透出
            // 上游：外部事件输入（目前是测试）
            // 下游：LowerState / UpperState / OnEvent
            // 注意：派发期间状态对象可能被换掉（回调里请求转移会先排队，但订阅者可能直接改状态机外的数据）；先各取一次状态对象再调用，别假设下层处理完上层还是原来那个。
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

        /// <summary>把转移历史按新的在前拷进调用方的列表；传 null 时直接返回，不抛异常。</summary>
        /// <param name="buffer">接收结果的列表，会先被清空。</param>
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

        private void Start(S upper, S sub, bool hasSub)
        {
            // 功能：启动的真实实现 —— 先校验没启动过，再进入上层与下层，并记一条起始记录。
            // 入参：upper 初始上层；sub 初始下层；hasSub true 表示调用方显式给了下层，false 表示走上层入口
            // 返回：无
            // 步骤：
            //   1) 先挡重复 Start —— 复用同一个实例会让旧状态收不到 OnExit，要重置请重新 new 一个
            //   2) 取上层；没显式给下层就用它的入口下层，再取下层对象（没注册或不属于该上层都会在这里抛）
            //   3) 标成已启动、正在转移，先设当前标识，再调上层 OnEnter、下层 OnEnter —— 与 Apply 的进入顺序一致：先进上层、再进下层
            //   4) try/finally 复位 m_changing：OnEnter 抛异常也不能把状态机卡在永远正在转移
            //   5) 记一条 silent 历史：起始不算切换，不广播 StateChanged；再把 OnEnter 里排队的转移执行掉
            // 上游：Start(S) / Start(S, S)
            // 下游：RequireUpper / RequireSub / OnEnter / RecordChange / DrainPending
            // 注意：起始记录里 From 与 To 相同（都是初始路径）；OnEnter 里请求的转移要等这条记录写完才执行。
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

        private bool RequestChange(S upper, S sub, bool forced)
        {
            // 功能：所有转移请求的统一入口 —— 决定这次是忽略、排队还是立即执行。
            // 入参：upper 目标上层；sub 目标下层；forced 是否跳过目标状态的准入检查
            // 返回：true 表示被执行了或排进了队列；false 表示切到当前配置（空操作）或被目标状态拒绝
            // 步骤：
            //   1) 没 Start 就抛异常 —— 这时切状态没有从哪来，语义不成立
            //   2) 先 RequireUpper / RequireSub，未注册的目标与跨上层下层在这里就报清楚
            //   3) 非强制、目标就是当前配置、且没有排队请求时直接返回 false：空操作不记录也不广播，否则每帧调一次会把历史刷爆
            //   4) 正在转移中时只记进唯一一个待处理位置，等本次转移完整结束再执行
            //   5) 否则立即 Apply
            // 上游：TryChangeUpper / TryChangeLower / ForceChange
            // 下游：RequireUpper / RequireSub / Apply（排队项由 DrainPending 负责执行）
            // 注意：排队只有这一个位置，连续请求只保留最后一次 —— 语义是最终停在哪个上层/下层，不是按顺序播一遍。
            // 注意：强制切到当前配置不会被第 3 步挡住，但 Apply 里会再挡一次并返回 false，不会重复进 OnEnter。
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

        private bool Apply(S upper, S sub, bool forced)
        {
            // 功能：真正执行一次转移，负责两层 OnExit / OnEnter 的顺序与历史记录。
            // 入参：upper 目标上层；sub 目标下层（两者都必须已注册且从属正确）；forced 是否跳过准入检查
            // 返回：true 表示真的切过去了；false 表示目标就是当前配置，或目标状态拒绝准入
            // 步骤：
            //   1) 先算哪几层需要动：换上层时下层一定跟着换，只换下层才只动下层
            //   2) 两层都没变化就返回 false —— 挡住强制路径绕过空操作判断
            //   3) 非强制时按分层规则问准入：换上层问新上层 CanEnterFrom（旧上层）；只换下层问新下层 CanEnterFrom（旧下层）；拒绝就广播拒绝记录并返回 false
            //   4) 标记正在转移，退的顺序是先下层、后上层，进的顺序反过来先上层、后下层 —— 与 IState 的约定一致
            //   5) 用 try/finally 复位 m_changing：OnExit / OnEnter 抛异常也不能把状态机卡在永远正在转移
            //   6) 记一条非 silent 历史并广播 StateChanged
            // 上游：RequestChange / DrainPending
            // 下游：RequireUpper / RequireSub / CanEnterFrom / OnExit / OnEnter / RecordChange
            // 注意：退下层时 UpperCurrent 与 LowerCurrent 还是旧值，进上层时两者都已改成新值 —— 回调里读当前状态会得到不同结果。
            // 注意：只换下层时上层不重进、也收不到任何回调；别把下层切换当成整层重建。
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

            S upper = m_pendingUpper;
            S sub = m_pendingSub;
            bool forced = m_pendingForced;
            m_hasPending = false;

            Apply(upper, sub, forced);
        }

        private UpperLayer RequireUpper(S upper)
        {
            // 功能：按上层标识取 UpperLayer，查不到时抛一条能照着修的异常。
            // 入参：upper 上层标识
            // 返回：已注册的上层记录，不会为 null
            // 步骤：直接查表；查不到就抛异常，消息里带上标识与注册示例 —— 这是最常见的初始化漏配。
            // 上游：RegisterSub / TryChangeUpper / RequestChange / Apply / RequireSub
            // 下游：m_uppers
            UpperLayer layer;

            if (!m_uppers.TryGetValue(upper, out layer))
            {
                throw new InvalidOperationException(
                    "[HierarchicalStateMachine] 上层状态 «" + upper + "» 没有注册过。" +
                    "请先 RegisterUpper(" + upper + ", new ...State(), 入口下层)。");
            }

            return layer;
        }

        private IState<S> RequireSub(S upper, S sub)
        {
            // 功能：按上层加下层取状态对象，并顺带守住下层只属于某一个上层这条分层规则。
            // 入参：upper 它应该在哪个上层下；sub 下层标识
            // 返回：该上层自带表里的下层状态对象，不会为 null
            // 步骤：先 RequireUpper 取上层，再查它的下层表；查不到就抛异常，消息里点明是分层的从属关系不成立。
            // 上游：Start（私有重载）/ RequestChange / Apply
            // 下游：RequireUpper / layer.Subs
            // 注意：跨上层切下层会在这里被拦下（例如 Ground 下切 Attack_2）—— 这是分层在起作用，不是注册漏了；先 RegisterSub 把它挂到目标上层下。
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

        private void RecordChange(string fromPath, string toPath, bool forced, bool silent)
        {
            // 功能：把一次转移记成历史，超过容量就丢掉最旧的，并按需广播。
            // 入参：fromPath 原路径；toPath 目标路径；forced 是否强制；silent true 表示只记录不广播（Start 用）
            // 返回：无
            // 步骤：
            //   1) 用当前帧号与两端路径构造记录 —— 记录里只存路径文本，状态对象以后换掉也不影响回看
            //   2) 追加到历史末尾
            //   3) 超容量就从最旧的开始删，保证总量不超过 m_historyCapacity
            //   4) 非 silent 才广播 StateChanged；Start 的起始记录不算切换，不广播
            // 上游：Start（私有重载）/ Apply
            // 下游：m_history / StateChanged 订阅者
            // 注意：广播前先把委托取到局部变量 —— 订阅者在回调里退订或再加，不会让这次调用拿到 null。
            // 注意：广播发生在 m_changing 复位之后 —— 订阅者里再切状态会立即执行而不是排队，别在回调里无限递归。
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

        private void RaiseTransitionRejected(string fromPath, string toPath)
        {
            // 功能：把这次转移被拒绝广播给 TransitionRejected 的订阅者。
            // 入参：fromPath 原路径；toPath 这次请求的目标路径
            // 返回：无
            // 步骤：先用当前帧号构造一条 Forced 恒为 false 的记录，再取委托局部变量判空调用。
            // 上游：Apply（CanEnterFrom 拒绝时）
            // 下游：TransitionRejected 订阅者
            // 注意：记录里的 From 与 To 只表示试图从哪到哪，实际状态没有变；被拒绝时 m_changing 还没置 true，订阅者里再切状态会立即执行。
            Action<StateChangeRecord> handlers = TransitionRejected;

            if (handlers != null)
            {
                handlers(new StateChangeRecord(m_frame, fromPath, toPath, false));
            }
        }
    }
}
