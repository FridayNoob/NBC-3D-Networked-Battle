// 这个文件里有：
//   QuestRow（类）：任务面板一行的显示数据 —— 标题、详情、按钮名与按钮可点状态，纯数据载体
//   QuestPanelModel（类）：任务面板的视图模型 —— 纯 C# 把任务状态翻成显示行与提示，不碰控件

using System;
using System.Collections.Generic;
using NBC.Game.Quest;

namespace NBC.Game.UI
{
    /// <summary>任务面板的一行显示数据：标题、详情、按钮名与可点状态；面板照它摆控件，Editor 调试窗口直接读字段。</summary>
    // 主要功能：一行管一个任务在界面上的全部显示素材，把「算什么」与「怎么摆」隔开
    // 主要数据成员：QuestId（任务编号）、ActionName（按钮名，面板回传动作就靠它）、Title / Detail（标题行与详情行）、ActionLabel（按钮上的字）、ActionEnabled（能不能按）
    // 主要外部方法：无 —— 纯数据载体，由 QuestPanelModel 造，消费方只读
    // 谁在用它：QuestPanel.RebuildRows 摆成按钮与文字；Editor 的 NetDebugWindow.DrawQuestRow 直接读字段画调试行；EditMode 用例断言字段内容
    public sealed class QuestRow
    {
        // 任务编号，正整数（Quest 表主键，演示任务是 3004）
        public int QuestId;

        // 按钮名，契约值「Accept_任务编号」或「Submit_任务编号」；面板把它原样设成按钮的 name，点击回传后 HandleAction 靠它认动作
        public string ActionName;

        // 标题行，形如「[3004] 清剿野狼」；带编号是为了玩家报问题时能对上配置表
        public string Title;

        // 详情行，多条条件各占一行，形如「击杀怪物 ×3（目标 6001）  2/3」；空串表示没有明细
        public string Detail;

        // 按钮上显示的字，只有「接取」与「交付」两种
        public string ActionLabel;

        // 现在能不能按：可接区恒为 true，交付要等条件全满，「待确认」期间为 false
        public bool ActionEnabled;
    }

    /// <summary>任务面板的视图模型：算显示行与提示文字，不碰控件；联机时也不改本地任务状态。</summary>
    // 主要功能：把本地任务运行时（以及服务端权威）的状态翻成四个区的显示行和一句给玩家看的消息
    // 主要数据成员：m_quests（本地任务运行时）、m_pending（已发请求、未确认的任务编号）、Authority（条件进度权威，可空）、StateAuthority（任务状态权威，可空）、SendAction（动作请求出口，null = 走本地）
    // 主要外部方法：CopySections / CopyOfferRows / CopyTrackingRows（产行）、HandleAction / Accept / Submit（动作）、IsPending / ClearPending / FailPending / FailAllPending（待确认落定）、LastMessage（提示）
    // 谁在用它：QuestPanel 绑一份负责摆控件；Boot 的 M2DemoBehaviour.BindQuestPanel 在绑面板时 new 一个；Editor 的 NetDebugWindow 复用同一份视图模型画调试区；EditMode 用例直接测
    // 注意：按钮名 = 「动作_任务编号」是面板与模型之间的契约（见 HandleAction），不要改成下标映射
    // 注意：动作失败的原因必须留在 LastMessage 给 UI 显示 —— 玩家点了按钮却什么都没发生，是最难排查的一类问题
    public sealed class QuestPanelModel
    {
        // 接取按钮名前缀，契约值「Accept_」，拼上任务编号即成按钮名
        public const string AcceptPrefix = "Accept_";

        // 交付按钮名前缀，契约值「Submit_」，拼上任务编号即成按钮名
        public const string SubmitPrefix = "Submit_";

        // 本地任务运行时：可接 / 已接的列表与本地动作都问它，单机模式下它是唯一数据源
        private readonly QuestRuntime m_quests;

        // 条件进度的权威来源，null = 还没接上线（此时条件数字用本地那份并标「本地预测」）
        // 注意：用可写属性而不是构造参数 —— 面板 / 演示壳 / 用例都是「先建模型、连上会话后再接权威」，塞进构造会逼所有既有调用点改签名
        public IQuestProgressAuthority Authority { get; set; }

        // 协议里 QuestActionRequest.action 的「接取」编号，值与协议一致（1）
        public const int ActionAccept = 1;

        // 协议里 QuestActionRequest.action 的「交付」编号，值与协议一致（2）
        public const int ActionSubmit = 2;

        // 把动作请求发给服务端的出口，返回是否发出去了；null（默认）= 单机，直接改本地 QuestRuntime
        // 注意：设了它之后 Accept / Submit 不再本地改任务状态 —— 服务端才是权威，本地自己改就是客户端自封权威，正是之前修掉的那个洞
        /// <summary>动作请求的发送出口；null 表示单机模式，动作直接落到本地 QuestRuntime。</summary>
        public Func<int, int, bool> SendAction { get; set; }

        // 已发出请求、还没被服务端表态的任务编号集合（「待确认」）
        private readonly HashSet<int> m_pending = new HashSet<int>();

        /// <summary>该任务是否「请求已发、等服务端确认」；它不代表已接取，不许拿它推进本地状态。</summary>
        // 功能：查这个任务是不是「请求已发、等确认」
        // 入参：questId 任务编号
        // 返回：在待确认集合里返回 true，否则 false
        // 步骤：
        //   1) 直接查 m_pending，不改任何状态 —— 调用方可能每次画按钮都要问一遍
        // 上游：QuestStateReconciler.Plan（经 Editor 的 NetDebugWindow 传进去）、BuildOfferRow / BuildTrackingRow 的待确认标注
        // 下游：m_pending
        // 注意：「待确认」不是「已接取」——它只是给玩家看的一行提示，不许拿它点亮「可交付」，也不许让本地状态机往前走一格（否则等于乐观地谎报成功）
        public bool IsPending(int questId)
        {
            return m_pending.Contains(questId);
        }

        /// <summary>服务端已表态（确认成功或明说没接过都算）后，清掉该任务的「待确认」。</summary>
        // 功能：服务端表态后清掉该任务的「待确认」
        // 入参：questId 任务编号
        // 返回：无
        // 步骤：
        //   1) 直接从 m_pending 移除 —— 不判断是不是它发的，收包方只按编号落定
        // 上游：NetSession.QuestStateReceived 的订阅方（Editor 的 NetDebugWindow）、EditMode 用例
        // 下游：m_pending
        // 注意：无论是「确认成功」还是「服务端说没接过」都要清 —— 留着界面就会一直显示「待确认」
        public void ClearPending(int questId)
        {
            m_pending.Remove(questId);
        }

        // 功能：收到针对某个任务的错误 ⇒ 清掉「待确认」并把服务端给的原因原文放进 LastMessage
        // 入参：questId 任务编号；reason 服务端原因（会原样显示给玩家，空或 null 时换成一句兜底文案）
        // 返回：无
        // 步骤：
        //   1) 先移除待确认，再写消息 —— 消息是给玩家看的，集合是给界面判断用的，两件事都要做
        //   2) 原因原样透传，不改写：玩家看到的就是服务端那句话
        // 上游：EditMode 用例（收包路径目前走 ClearPending 与 FailAllPending）
        // 下游：m_pending、LastMessage
        // 注意：兜底文案只挡空原因，不能替代服务端原因 —— 把原因吃掉会让玩家点了没反应
        public void FailPending(int questId, string reason)
        {
            m_pending.Remove(questId);
            LastMessage = string.IsNullOrEmpty(reason) ? "任务动作被服务端拒绝（没给原因）" : reason;
        }

        /// <summary>把当前全部「待确认」一起落定，返回落定个数（0 表示本来就没有待确认的）。</summary>
        // 功能：收到不带任务编号的错误 ⇒ 把当前所有「待确认」一次落定，并把原因原文放进 LastMessage
        // 入参：reason 服务端原因（会原样显示，空或 null 时换成兜底文案）
        // 返回：落定了几个；0 表示当时没有待确认的，调用方据此可以不打扰玩家
        // 步骤：
        //   1) 先数个数 —— 返回值和「要不要写消息」都靠它
        //   2) 只有 count > 0 才清集合并写消息：没有待确认就不该冒出一条失败提示
        // 上游：NetSession.ErrorReceived 的订阅方（Editor 的 NetDebugWindow）
        // 下游：m_pending、LastMessage
        // 注意：ErrorResponse 里没有任务编号，而界面上可能同时有两个任务在等确认 ⇒ 只能一起落定，没法只落一个
        public int FailAllPending(string reason)
        {
            int count = m_pending.Count;

            if (count > 0)
            {
                m_pending.Clear();
                LastMessage = string.IsNullOrEmpty(reason) ? "任务动作被服务端拒绝（没给原因）" : reason;
            }

            return count;
        }
        // 复用的临时列表（避免每次刷新都分配），装本轮从 QuestRuntime 拷出的可接任务
        private readonly List<QuestOffer> m_offers = new List<QuestOffer>();

        // 复用的临时列表（避免每次刷新都分配），装本轮拷出的已接任务
        private readonly List<QuestTracking> m_trackings = new List<QuestTracking>();

        // 分桶用的临时输入（本地已接那一组），每轮先 Clear 再填，交给 QuestSectionPlanner.Plan
        private readonly List<QuestSectionPlanner.Entry> m_plannedTracked =
            new List<QuestSectionPlanner.Entry>();

        // 分桶用的临时输入（本地可接那一组），装任务编号
        private readonly List<int> m_plannedOffers = new List<int>();

        /// <summary>造一个视图模型，数据来源与本地动作都走 quests。</summary>
        // 功能：保存任务运行时，并把最近消息清成空串
        // 入参：quests 任务运行时（不能为 null，否则抛 ArgumentNullException）
        // 返回：无（构造）
        // 步骤：
        //   1) 先判空再赋值 —— 早抛比让后面每个方法都空引用好排查（注意：抛出的那句话是异常消息，不是日志）
        //   2) LastMessage 置空串，面板首次刷新不会带着上一局的消息
        // 上游：Boot 的 M2DemoBehaviour.BindQuestPanel、Editor 的 NetDebugWindow、EditMode 用例
        // 下游：无
        public QuestPanelModel(QuestRuntime quests)
        {
            if (quests == null)
            {
                throw new ArgumentNullException(nameof(quests),
                    "[QuestPanelModel] 必须给一个 QuestRuntime。");
            }

            m_quests = quests;
            LastMessage = string.Empty;
        }

        /// <summary>最近一条给玩家看的消息（成功提示或失败原因原文）；没有消息时是空串。</summary>
        // 空串表示没有消息；面板在 Refresh 里读它刷 Message，Editor 直接显示在调试区
        public string LastMessage { get; private set; }

        // 功能：把最近一条消息清成空串
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 直接赋空串，不判空 —— 空串本身就是「没有消息」的表示
        // 上游：EditMode 用例（面板打开路径目前还没调它）
        // 下游：无
        public void ClearMessage()
        {
            LastMessage = string.Empty;
        }

        // 给面板用的两份数据

        // 功能：把「可接任务」逐条翻成显示行，填进 buffer，顺序就是配置表顺序
        // 入参：buffer 目标列表（会先 Clear；为 null 抛 ArgumentNullException）
        // 返回：无（结果全在 buffer 里）
        // 步骤：
        //   1) 先 Clear —— 面板每次刷新复用同一个列表，不清会越积越多
        //   2) 从 m_quests 拷一份到复用缓冲 m_offers，再按下标遍历：拷一份是为了不直接摸运行时的内部列表
        //   3) 逐条造 QuestRow：ActionName 用 AcceptPrefix 拼编号（面板靠它回传动作），可接任务的按钮恒可点
        //   4) 标题与详情直接用配置表原文，模型不做富文本改写 —— 换皮肤只该动面板那一半
        // 上游：QuestPanel.Refresh、EditMode 用例
        // 下游：QuestRuntime.CopyOffers、QuestRow
        public void CopyOfferRows(List<QuestRow> buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            buffer.Clear();
            m_quests.CopyOffers(m_offers);

            for (int i = 0; i < m_offers.Count; i++)
            {
                QuestOffer offer = m_offers[i];

                QuestRow row = new QuestRow();
                row.QuestId = offer.QuestId;
                row.ActionName = AcceptPrefix + offer.QuestId;
                row.ActionLabel = "接取";
                row.ActionEnabled = true;
                row.Title = "[" + offer.QuestId + "] " + offer.Name;
                row.Detail = offer.Description;
                buffer.Add(row);
            }
        }

        // 功能：把「已接任务」逐条翻成显示行（带逐条条件进度），填进 buffer
        // 入参：buffer 目标列表（会先 Clear；为 null 抛 ArgumentNullException）
        // 返回：无（结果全在 buffer 里）
        // 步骤：
        //   1) 先 Clear，再从 m_quests 拷一份已接到复用缓冲 m_trackings
        //   2) 造行并拼标题：编号 + 名字 + 状态中文
        //   3) 拼详情之前先 QuestProgressOverlay.Apply 把权威值盖上去 —— 必须在拼文字之前，否则这一行拿的是本地旧值，也丢了数字来源
        //   4) 只有条件全满（State == Completed）才让按：按钮点不动，比让玩家点了被拒更友好
        // 上游：QuestPanel.Refresh、EditMode 用例
        // 下游：QuestRuntime.CopyActiveTrackings、QuestProgressOverlay.Apply、BuildDetail、StateText
        // 注意：Apply 是原地改 tracking.Conditions（改 Current / Required / IsMet / Source），所以每轮刷新都要重新盖一次，不能只在建模型时盖
        public void CopyTrackingRows(List<QuestRow> buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            buffer.Clear();
            m_quests.CopyActiveTrackings(m_trackings);

            for (int i = 0; i < m_trackings.Count; i++)
            {
                QuestTracking tracking = m_trackings[i];

                QuestRow row = new QuestRow();
                row.QuestId = tracking.QuestId;
                row.ActionName = SubmitPrefix + tracking.QuestId;
                row.ActionLabel = "交付";
                row.Title = "[" + tracking.QuestId + "] " + tracking.Name + "（" + StateText(tracking.State) + "）";

                QuestProgressOverlay.Apply(tracking.Conditions, Authority);

                row.Detail = BuildDetail(tracking);

                row.ActionEnabled = tracking.State == EQuestState.Completed;
                buffer.Add(row);
            }
        }

        // 动作（面板把点击转到这里）

        /// <summary>按按钮名（Accept_任务编号 / Submit_任务编号）执行动作；认不出来时返回 false 并记一条内部错误。</summary>
        // 功能：按按钮名执行动作，把名字解析成「任务编号 + 接取还是交付」
        // 入参：actionName 按钮名（Accept_前缀或 Submit_前缀 + 任务编号）
        // 返回：认出前缀并执行了返回 true；名字为空或认不出来返回 false（说明面板上多了一个不该有的按钮）
        // 步骤：
        //   1) 先挡空名 —— 这是程序错误，也写进 LastMessage，让界面上看得见而不是静默
        //   2) 用 Ordinal 比较认两种前缀，把编号那截文本交给 Apply
        //   3) 都认不出就记一条内部错误并返回 false，不猜也不抛
        // 上游：QuestPanel.OnClick、Editor 的 NetDebugWindow.RunQuestAction、EditMode 用例
        // 下游：Apply
        // 注意：按钮名必须带任务编号。不要改成「第几个按钮 → 哪个任务」的下标映射：列表顺序一变或某条被过滤掉，下标就会悄悄错位，表现是点接取 A 却接了 B；名字带编号之后错位在结构上不可能
        public bool HandleAction(string actionName)
        {
            if (string.IsNullOrEmpty(actionName))
            {
                LastMessage = "（内部错误：按钮名是空的）";
                return false;
            }

            if (actionName.StartsWith(AcceptPrefix, StringComparison.Ordinal))
            {
                Apply(actionName.Substring(AcceptPrefix.Length), true);
                return true;
            }

            if (actionName.StartsWith(SubmitPrefix, StringComparison.Ordinal))
            {
                Apply(actionName.Substring(SubmitPrefix.Length), false);
                return true;
            }

            LastMessage = "（内部错误：不认识的按钮名「" + actionName + "」）";
            return false;
        }

        // 功能：直接接取一个任务，不经过按钮名解析（测试与脚本用）
        // 入参：questId 任务编号
        // 返回：动作结果；联机模式下 Success 只代表请求发出去了，不代表任务已接取
        // 步骤：
        //   1) 接了 SendAction 就走 RequestRemote —— 服务端权威，本地不改状态
        //   2) 否则走本地 QuestRuntime.Accept，再交给 Record 记消息
        // 上游：EditMode 用例、脚本
        // 下游：RequestRemote / QuestRuntime.Accept / Record
        public QuestActionResult Accept(int questId)
        {
            if (SendAction != null)
            {
                return RequestRemote(questId, ActionAccept, "接取");
            }

            return Record(m_quests.Accept(questId));
        }

        // 功能：直接交付一个任务，不经过按钮名解析（测试与脚本用）
        // 入参：questId 任务编号
        // 返回：动作结果；联机模式下 Success 只代表请求发出去了，不代表已交付
        // 步骤：
        //   1) 接了 SendAction 就走 RequestRemote —— 服务端权威，本地不改状态
        //   2) 否则走本地 QuestRuntime.Submit，再交给 Record 记消息
        // 上游：EditMode 用例、脚本
        // 下游：RequestRemote / QuestRuntime.Submit / Record
        public QuestActionResult Submit(int questId)
        {
            if (SendAction != null)
            {
                return RequestRemote(questId, ActionSubmit, "交付");
            }

            return Record(m_quests.Submit(questId));
        }

        // 功能：把动作发给服务端并置「待确认」，本地一点任务状态都不改
        // 入参：questId 任务编号；action 动作编号（ActionAccept / ActionSubmit）；verb 动作的中文说法（只用于提示语）
        // 返回：请求发出去了返回 Success（不代表任务已接取 / 交付）；没发出去返回 Fail，原因已写进 LastMessage
        // 步骤：
        //   1) 调 SendAction，先看它有没有发出去
        //   2) 没发出去 ⇒ 写一句「请求没能发出去、任务状态没有变化」并返回 Fail —— 不能让玩家以为已经接上了
        //   3) 发出去了 ⇒ 加进 m_pending，写「等确认」提示，返回 Success
        // 上游：Accept / Submit（联机模式）、Apply
        // 下游：SendAction、m_pending、LastMessage
        // 注意：返回的 Success 只表示「请求发出去了」，权威在服务端；服务端认了或拒了之后由收包路径调 ClearPending / FailPending 才算落定
        private QuestActionResult RequestRemote(int questId, int action, string verb)
        {
            bool sent = SendAction(questId, action);

            if (!sent)
            {
                LastMessage = "「" + verb + "」请求**没能发出去**（没连上服务端？）—— 任务状态没有变化。";
                return QuestActionResult.Fail(LastMessage);
            }

            m_pending.Add(questId);
            LastMessage = "已把「" + verb + "」请求发给服务端（任务 " + questId + "），等它确认……";

            return QuestActionResult.Success();
        }

        /// <summary>任务状态（进行中 / 可交付 / 已交付）的权威来源；null 表示四个区完全按本地 QuestRuntime 分。</summary>
        // 任务状态的权威来源，null = 一切按本地 QuestRuntime（所以既有 EditMode 行为不变）
        // 注意：它与「条件进度」那个 Authority 并排、不合并 —— 一个答「这个任务到哪一步」，一个答「这条条件累计了多少」
        public IQuestStateAuthority StateAuthority { get; set; }

        /// <summary>一次遍历把全部任务分进四个区并各造一行；每个任务恰好出现一次，一个都不丢。</summary>
        // 功能：把本地已接与本地可接两组任务一次遍历分进可接 / 进行中 / 可交付 / 已交付，四区各造显示行
        // 入参：offerBuffer 可接区、activeBuffer 进行中区、readyBuffer 可交付区、doneBuffer 已交付区（四个都会先 Clear，任一为 null 抛 ArgumentNullException）
        // 返回：无（结果全在四个入参列表里）
        // 步骤：
        //   1) 先清四个区，再拷本地已接与本地可接到复用缓冲 —— 不清会重复堆行
        //   2) 把两边压成「任务编号 + 本地状态」交给 QuestSectionPlanner.Plan，归属由它一次算定
        //   3) 按 placement.Section 挑目标缓冲；LocalState 非 0 说明本地有 tracking，能带条件行，否则按 offer 造行
        //   4) 每个任务只 Add 一次：四个区的并集等于输入并集，这条不变式有专门用例盯着
        // 上游：QuestPanel.Refresh、Editor 的 NetDebugWindow.DrawRealQuests、EditMode 用例
        // 下游：QuestSectionPlanner.Plan、BuildTrackingRow / BuildOfferRow、BufferOf、Place、FindTracking / FindOffer
        // 注意：必须一次遍历决定归属，不能「先从可接里删掉、再加到进行中」—— 漏掉加的那一步，任务会从界面上消失而且不报错
        // 注意：分桶决策放在 QuestSectionPlanner 是有原因的：那边只依赖 BCL，探针能编、能跑、能变异；这段逻辑以前长在本方法里，探针编不进去，「不丢行」只能靠 EditMode 验
        // 注意：服务端可能说某条「已经接过」而本地只有它的可接行 —— 那种情况靠 Section 把它挪进正确的区，并且照样产出一行
        public void CopySections(
            List<QuestRow> offerBuffer, List<QuestRow> activeBuffer,
            List<QuestRow> readyBuffer, List<QuestRow> doneBuffer)
        {
            if (offerBuffer == null || activeBuffer == null || readyBuffer == null || doneBuffer == null)
            {
                throw new ArgumentNullException(nameof(offerBuffer),
                    "[QuestPanelModel] 四个区的列表都不能为 null。");
            }

            offerBuffer.Clear();
            activeBuffer.Clear();
            readyBuffer.Clear();
            doneBuffer.Clear();

            m_quests.CopyActiveTrackings(m_trackings);
            m_quests.CopyOffers(m_offers);

            List<QuestSectionPlanner.Entry> tracked = m_plannedTracked;
            tracked.Clear();

            for (int i = 0; i < m_trackings.Count; i++)
            {
                tracked.Add(new QuestSectionPlanner.Entry(m_trackings[i].QuestId, (int)m_trackings[i].State));
            }

            List<int> offers = m_plannedOffers;
            offers.Clear();

            for (int i = 0; i < m_offers.Count; i++)
            {
                offers.Add(m_offers[i].QuestId);
            }

            List<QuestSectionPlanner.Placement> placements =
                QuestSectionPlanner.Plan(tracked, offers, QueryStateAuthority);

            for (int i = 0; i < placements.Count; i++)
            {
                QuestSectionPlanner.Placement placement = placements[i];

                List<QuestRow> target = BufferOf(offerBuffer, activeBuffer, readyBuffer, doneBuffer,
                                                 (int)placement.Section);

                if (placement.LocalState != 0)
                {
                    Place(target, BuildTrackingRow(FindTracking(placement.QuestId), (int)placement.Section));
                }
                else
                {
                    Place(target, BuildOfferRow(FindOffer(placement.QuestId), (int)placement.Section));
                }
            }
        }

        // 功能：把面板的权威接成 planner 要的那个查询委托
        // 入参：questId 任务编号；state 服务端给的状态（0..3，仅在返回 true 时有意义）
        // 返回：服务端对这个任务表过态返回 true，否则 false（此时 state 置 0）
        // 步骤：
        //   1) 没有权威就把 state 置 0 并返回 false —— planner 据此退回本地
        //   2) 有权威就转发 TryGetState，三值语义原样传下去
        // 上游：CopySections 传给 QuestSectionPlanner.Plan
        // 下游：IQuestStateAuthority.TryGetState
        private bool QueryStateAuthority(int questId, out int state)
        {
            if (StateAuthority == null)
            {
                state = 0;
                return false;
            }

            return StateAuthority.TryGetState(questId, out state);
        }

        // 功能：按编号在本地已接列表里找 tracking
        // 入参：questId 任务编号（调用方保证存在）
        // 返回：找到的 tracking；调用方保证存在，所以正常不会为 null
        // 步骤：
        //   1) 线性扫 m_trackings —— 一屏几十条，没必要建字典
        // 上游：CopySections
        // 下游：m_trackings
        private QuestTracking FindTracking(int questId)
        {
            for (int i = 0; i < m_trackings.Count; i++)
            {
                if (m_trackings[i].QuestId == questId)
                {
                    return m_trackings[i];
                }
            }

            return null;
        }

        // 功能：按编号在本地可接列表里找 offer
        // 入参：questId 任务编号（调用方保证存在）
        // 返回：找到的 offer；找不到时返回 default（QuestOffer 是结构体，不是 null）
        // 步骤：
        //   1) 线性扫 m_offers 比对编号
        // 上游：CopySections
        // 下游：m_offers
        private QuestOffer FindOffer(int questId)
        {
            for (int i = 0; i < m_offers.Count; i++)
            {
                if (m_offers[i].QuestId == questId)
                {
                    return m_offers[i];
                }
            }

            return default(QuestOffer);
        }

        // 功能：按区号取对应的目标列表
        // 入参：offers 可接、active 进行中、ready 可交付、done 已交付；section 区号（0 = 可接，1 = 进行中，2 = 可交付，3 = 已交付）
        // 返回：该区对应的列表；区号越界时按可接处理
        // 步骤：
        //   1) switch 直接映射，越界落 default 到 offers —— 宁可挤回可接，也不要抛
        // 上游：CopySections
        // 下游：无
        private static List<QuestRow> BufferOf(
            List<QuestRow> offers, List<QuestRow> active, List<QuestRow> ready, List<QuestRow> done, int section)
        {
            switch (section)
            {
                case 1: return active;
                case 2: return ready;
                case 3: return done;
                default: return offers;
            }
        }

        // 功能：按「权威优先、服务端没说才用本地」算一个任务该归哪个区号
        // 入参：questId 任务编号；local 本地认为的状态（EQuestState）
        // 返回：区号 0..3；服务端明确表态时就是它的数字，否则等于 local
        // 步骤：
        //   1) 有权威就查 TryGetState，返回 true 才算服务端说了话
        //   2) 返回 false 是「服务端没说」而不是「未接取」—— 这时退回本地状态，不能当 0 处理
        // 上游：当前没有调用点 —— 分区已改由 QuestSectionPlanner 统一决策
        // 下游：IQuestStateAuthority.TryGetState
        // 注意：本方法是分区改造前的遗留（SourceMarkOf 还在用，别顺手一起删）；留着会让人以为还有第二条分桶路径，清理请单独提一次改动
        private int SectionOf(int questId, EQuestState local)
        {
            int authoritative;

            if (StateAuthority != null && StateAuthority.TryGetState(questId, out authoritative))
            {
                return authoritative;
            }

            return (int)local;
        }

        // 功能：判断这一行的数字是「服务端说的」还是「本地猜的」，并给出对应标注
        // 入参：questId 任务编号
        // 返回：服务端表过态返回空串（不标）；否则返回 QuestProgressOverlay.LocalMark
        // 步骤：
        //   1) 有权威且 TryGetState 为 true ⇒ 空串，权威不标
        //   2) 其余一律标本地预测 —— 保守方向：宁可把事实标成预测，也不要把预测标成事实
        // 上游：BuildOfferRow / BuildTrackingRow 拼标题
        // 下游：IQuestStateAuthority.TryGetState、QuestProgressOverlay.LocalMark
        private string SourceMarkOf(int questId)
        {
            int ignored;

            if (StateAuthority != null && StateAuthority.TryGetState(questId, out ignored))
            {
                return string.Empty;
            }

            return QuestProgressOverlay.LocalMark;
        }

        // 功能：拼「待确认」那一小段标注
        // 入参：questId 任务编号
        // 返回：在待确认时返回「  ⏳待确认」（前两个空格是分隔用），否则空串
        // 步骤：
        //   1) 直接问 IsPending，不自己查 m_pending —— 判定口径只留一处
        // 上游：BuildOfferRow / BuildTrackingRow 拼标题
        // 下游：IsPending
        private string PendingMarkOf(int questId)
        {
            return IsPending(questId) ? "  ⏳待确认" : string.Empty;
        }

        // 功能：把一行放进目标区 —— 全文件只有这里做 Add，保证「每个任务恰好放一次」只有一个入口
        private static void Place(List<QuestRow> buffer, QuestRow row)
        {
            buffer.Add(row);
        }

        // 功能：造「从可接那边来」的一行
        // 入参：offer 可接任务；section 归到哪个区（0..3，服务端可能说它已接过而把它挪走）
        // 返回：造好的显示行
        // 步骤：
        //   1) 编号、按钮名、按钮字照可接任务的写法填
        //   2) 只有落回可接区（section == 0）且不在待确认时才允许按 —— 被挪到别区的说明服务端已经接了，不许再显示可点的接取
        //   3) 标题尾部拼待确认与来源标注；详情在非可接区补一句「明细还没同步」
        // 上游：CopySections
        // 下游：PendingMarkOf、SourceMarkOf
        // 注意：本地没有 tracking 就拿不到条件行，绝不编造「0/N」—— 那正是这条不变式禁止的事：把「没有数据」画成「进度是 0」
        private QuestRow BuildOfferRow(QuestOffer offer, int section)
        {
            QuestRow row = new QuestRow();
            row.QuestId = offer.QuestId;
            row.ActionName = AcceptPrefix + offer.QuestId;
            row.ActionLabel = "接取";

            row.ActionEnabled = section == 0 && !IsPending(offer.QuestId);

            row.Title = "[" + offer.QuestId + "] " + offer.Name +
                        PendingMarkOf(offer.QuestId) + SourceMarkOf(offer.QuestId);

            row.Detail = section == 0
                ? offer.Description
                : offer.Description + "\n（服务端说这个任务已经接取，但本地还没有它的进度明细 —— 等同步）";

            return row;
        }

        // 功能：造「从已接那边来」的一行，带逐条条件进度
        // 入参：tracking 追踪视图；section 归到哪个区（0..3）
        // 返回：造好的显示行
        // 步骤：
        //   1) 编号、按钮名、按钮字照已接任务的写法填
        //   2) 只有可交付区（section == 2）且不在待确认时才允许按 —— 待确认期间再点会重复发请求
        //   3) 标题尾部拼状态中文、待确认与来源标注
        //   4) 拼详情之前先 Apply 权威进度，再 BuildDetail 逐条拼
        // 上游：CopySections
        // 下游：QuestProgressOverlay.Apply、BuildDetail、StateText、PendingMarkOf、SourceMarkOf
        // 注意：条件那几行是权威进度优先盖上去的，服务端没说的条件保留本地值并由来源标注说明 —— 不要在这里改判定，否则玩家分不清哪些是事实
        private QuestRow BuildTrackingRow(QuestTracking tracking, int section)
        {
            QuestRow row = new QuestRow();
            row.QuestId = tracking.QuestId;
            row.ActionName = SubmitPrefix + tracking.QuestId;
            row.ActionLabel = "交付";

            row.ActionEnabled = section == 2 && !IsPending(tracking.QuestId);

            row.Title = "[" + tracking.QuestId + "] " + tracking.Name +
                        "（" + StateText(tracking.State) + "）" +
                        PendingMarkOf(tracking.QuestId) + SourceMarkOf(tracking.QuestId);

            QuestProgressOverlay.Apply(tracking.Conditions, Authority);

            row.Detail = BuildDetail(tracking);
            return row;
        }


        // 功能：把按钮名里解析出的编号与动作真正执行掉
        // 入参：idText 按钮名里那截数字文本；accept true = 接取，false = 交付
        // 返回：无（结果写进 LastMessage）
        // 步骤：
        //   1) 先 TryParse 编号：按钮名是我们自己造的，解析失败只能是程序错误，记一条内部错误就返回
        //   2) 接了 SendAction 就走 RequestRemote 并直接返回 —— 联机时不许在本地改任务状态
        //   3) 否则走本地 QuestRuntime 的 Accept / Submit，再交给 Record 记消息
        // 上游：HandleAction
        // 下游：RequestRemote / QuestRuntime.Accept、Submit / Record
        private void Apply(string idText, bool accept)
        {
            int questId;

            if (!int.TryParse(idText, out questId))
            {
                LastMessage = "（内部错误：按钮名里的任务编号不是数字「" + idText + "」）";
                return;
            }

            if (SendAction != null)
            {
                RequestRemote(questId, accept ? ActionAccept : ActionSubmit, accept ? "接取" : "交付");
                return;
            }

            Record(accept ? m_quests.Accept(questId) : m_quests.Submit(questId));
        }

        // 功能：把一次本地动作的结果记成要给玩家看的消息
        // 入参：result 动作结果（Ok 与 Reason 已由 QuestRuntime / QuestActionResult 判定）
        // 返回：原样返回 result，方便调用方直接 return 或链式使用
        // 步骤：
        //   1) Ok 写「已受理。」，失败写 result.Reason —— 原因原文必须透传，不要在这里改写
        // 上游：Accept / Submit / Apply（本地模式）
        // 下游：LastMessage
        private QuestActionResult Record(QuestActionResult result)
        {
            LastMessage = result.Ok ? "已受理。" : result.Reason;
            return result;
        }

        // 功能：把任务状态翻成面板直接显示的中文
        // 入参：state 任务状态（EQuestState 的 0..3）
        // 返回：未接取 / 进行中 / 可交付 / 已交付；未知值按未接取处理
        // 步骤：
        //   1) switch 直接映射，default 落「未接取」—— 宁可显示得保守，也不要抛
        // 上游：CopyTrackingRows 与 BuildTrackingRow 拼标题
        // 下游：无
        private static string StateText(EQuestState state)
        {
            switch (state)
            {
                case EQuestState.Accepted: return "进行中";
                case EQuestState.Completed: return "可交付";
                case EQuestState.Submitted: return "已交付";
                default: return "未接取";
            }
        }

        // 功能：把一条 tracking 的条件逐条拼成多行详情，带达成标记与来源标记
        // 入参：tracking 追踪视图（条件已由 QuestProgressOverlay.Apply 盖过权威值）
        // 返回：多行文本；没有条件时返回「（没有条件）」
        // 步骤：
        //   1) 零条件先返回一句兜底，免得面板上出现一行空白
        //   2) 逐条拼「描述 + 当前 / 需求」，达成才追一个勾
        //   3) 每条都追 QuestProgressOverlay.Mark(Source)，标出这个数字是权威还是本地预测
        // 上游：CopyTrackingRows 与 BuildTrackingRow
        // 下游：QuestProgressOverlay.Mark
        // 注意：来源标注不能省 —— 没有它玩家就分不清这一格是事实还是客户端猜的；权威不标、预测才标
        private static string BuildDetail(QuestTracking tracking)
        {
            if (tracking.Conditions.Count == 0)
            {
                return "（没有条件）";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();

            for (int i = 0; i < tracking.Conditions.Count; i++)
            {
                QuestConditionLine line = tracking.Conditions[i];

                if (i > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(line.Description).Append("  ")
                       .Append(line.Current).Append('/').Append(line.Required)
                       .Append(line.IsMet ? "  ✅" : string.Empty)
                       .Append(QuestProgressOverlay.Mark(line.Source));
            }

            return builder.ToString();
        }
    }
}
