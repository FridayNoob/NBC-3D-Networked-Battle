// ============================================================================
//  QuestPanelModel —— 任务面板的**视图模型**：把任务状态翻译成"要给玩家看的东西"
//  项目：3D联网战斗Demo   对应：M2-D（任务 UI）
//
//  ---------------------------------------------------------------------------
//  为什么面板要拆成"模型 + 面板"两层（沿用 `LoadingMaskPanel`/`LoadingMaskController` 的做法）
//  ---------------------------------------------------------------------------
//      QuestPanelModel（本类）  纯 C#：算什么、显示什么文字、按钮叫什么
//      QuestPanel（面板）      MonoBehaviour：把上面的结果摆到 Text/Button 上
//
//  这么拆的好处很具体：
//    · **逻辑能在 EditMode 里测**（不需要预制体、不需要 Canvas、不需要帧循环）
//    · **换皮肤不改逻辑**：做一套新 UI 只要重写"摆放"那一半
//
//  ---------------------------------------------------------------------------
//  ⚠️ 按钮名里带任务编号：这是**面板与模型之间的契约**
//  ---------------------------------------------------------------------------
//  一屏上会有"接取 A / 接取 B / 交付 C"好几个按钮，而 `BasePanel.OnClick` 只给你
//  **一个按钮名**。所以约定按钮名 = `<动作>_<任务编号>`：
//
//      Accept_3004   -> 接取 3004
//      Submit_3004   -> 交付 3004
//
//  ⚠️ 一开始我想过另一种做法：面板里维护"第几个按钮 → 哪个任务"的下标映射。
//     那更脆 —— 一旦列表顺序变了（或者某条被过滤掉），下标就会**悄悄错位**，
//     表现是"点接取 A 结果接了 B"。按钮名带编号之后，**错位在结构上就不可能**。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 玩家能触发的失败要**留下来给 UI 显示**（而不是只进日志）
//  ---------------------------------------------------------------------------
//  `Accept`/`Submit` 失败时（已经接过、条件没做完……）把原因存进 `LastMessage`，
//  UI 负责把它显示出来。这和 M2-A 那条"**让错误自己说出来**"是同一条原则：
//  玩家点了按钮却什么都没发生，是最难排查的一类问题。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Game.Quest;

namespace NBC.Game.UI
{
    /// <summary>一行要显示的按钮（接取或交付）。</summary>
    public sealed class QuestRow
    {
        /// <summary>任务编号。</summary>
        public int QuestId;

        /// <summary>
        /// 按钮名（**契约**：`Accept_<任务编号>` / `Submit_<任务编号>`）。
        /// <para>面板把这个名字原样给按钮，`OnClick` 收到它就知道要做什么。</para>
        /// </summary>
        public string ActionName;

        /// <summary>标题行（例如「[3004] 清剿野狼」）。</summary>
        public string Title;

        /// <summary>详情行（多条条件各一行，例如「击杀怪物 ×3（目标 6001）  2/3」）。</summary>
        public string Detail;

        /// <summary>按钮上显示的字（`接取` / `交付`）。</summary>
        public string ActionLabel;

        /// <summary>现在能不能按（可接任务恒为 true；交付要等条件全满）。</summary>
        public bool ActionEnabled;
    }

    /// <summary>任务面板的视图模型。</summary>
    public sealed class QuestPanelModel
    {
        /// <summary>接取按钮的名字前缀（契约）。</summary>
        public const string AcceptPrefix = "Accept_";

        /// <summary>交付按钮的名字前缀（契约）。</summary>
        public const string SubmitPrefix = "Submit_";

        /// <summary>任务运行时（数据来源 + 动作执行者）。</summary>
        private readonly QuestRuntime m_quests;

        /// <summary>
        /// **权威进度**的来源（`Docs\27` §二十四）。**可以事后设置**（`null` = 还没接）。
        /// <para>⚠️ 用可写属性而不是构造参数：调用点（面板 / 演示壳 / 用例）大都是
        /// "先建模型、连上会话后再接权威"，塞进构造函数会逼着所有既有调用点改签名。</para>
        /// </summary>
        public IQuestProgressAuthority Authority { get; set; }

        /// <summary>`QuestActionRequest.action` = **接取**（与协议注释一致）。</summary>
        public const int ActionAccept = 1;

        /// <summary>`QuestActionRequest.action` = **交付**。</summary>
        public const int ActionSubmit = 2;

        /// <summary>
        /// **把动作请求发给服务端**的出口（M4-S3 §二十六）。
        /// <para>⚠️ 设了它之后 `Accept`/`Submit` **不再本地改任务状态** —— 服务端才是权威；
        /// 本地自己改就是"客户端自己当权威"，正是 §二十一 修掉的那个洞。</para>
        /// <para>传 null（**默认**）= 单机/既有用例：照旧走本地 `QuestRuntime`
        /// （所以既有的 696 条 EditMode 用例行为不变）。</para>
        /// </summary>
        public Func<int, int, bool> SendAction { get; set; }

        /// <summary>已经发出去、**还没被服务端确认**的任务编号（「待确认」）。</summary>
        private readonly HashSet<int> m_pending = new HashSet<int>();

        /// <summary>
        /// 这个任务是不是"请求已发、等确认"。
        /// <para>⚠️ **「待确认」不是「已接取」**：它只是给玩家看的一行提示，
        /// **不许**拿它去点亮"可交付"、也不许让本地状态机往前走一格（那样就成了乐观地谎报成功）。</para>
        /// </summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>待确认返回 true。</returns>
        public bool IsPending(int questId)
        {
            return m_pending.Contains(questId);
        }

        /// <summary>
        /// 服务端**表态**了 ⇒ 清掉这个任务的「待确认」（由收包方在
        /// `NetSession.QuestStateReceived` 里调）。
        /// <para>⚠️ 无论是"确认成功"还是"服务端说没接过"，都要清 ——
        /// 留着它界面就会一直显示「待确认」。</para>
        /// </summary>
        /// <param name="questId">任务编号。</param>
        public void ClearPending(int questId)
        {
            m_pending.Remove(questId);
        }

        /// <summary>
        /// 收到错误 ⇒ 清掉「待确认」并把**服务端给的原因原文**放进 `LastMessage`（显示给玩家）。
        /// </summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="reason">服务端给的原因（会原样显示）。</param>
        public void FailPending(int questId, string reason)
        {
            m_pending.Remove(questId);
            LastMessage = string.IsNullOrEmpty(reason) ? "任务动作被服务端拒绝（没给原因）" : reason;
        }

        /// <summary>
        /// 收到**不针对某个任务**的错误 ⇒ 把当前所有「待确认」一起落定，并把原因原文放进 `LastMessage`。
        /// <para>⚠️ 为什么需要它：`ErrorResponse` 里**没有任务编号**（它是"这次请求被拒"的通用回复），
        /// 而界面上可能同时有两个任务在等确认 ⇒ 没法只针对一个落定。</para>
        /// </summary>
        /// <param name="reason">服务端给的原因（会原样显示）。</param>
        /// <returns>落定了几个（**0 = 当时没有待确认的**，调用方据此可以不打扰玩家）。</returns>
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
        /// <summary>复用的临时列表（避免每次刷新都分配）。</summary>
        private readonly List<QuestOffer> m_offers = new List<QuestOffer>();

        /// <summary>复用的临时列表（避免每次刷新都分配）。</summary>
        private readonly List<QuestTracking> m_trackings = new List<QuestTracking>();

        /// <summary>分桶用的临时输入（本地已接那一组）。</summary>
        private readonly List<QuestSectionPlanner.Entry> m_plannedTracked =
            new List<QuestSectionPlanner.Entry>();

        /// <summary>分桶用的临时输入（本地可接那一组）。</summary>
        private readonly List<int> m_plannedOffers = new List<int>();

        /// <summary>造一个视图模型。</summary>
        /// <param name="quests">任务运行时（不能为 null）。</param>
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

        /// <summary>最近一条要显示给玩家的消息（成功提示或失败原因；没消息时是空串）。</summary>
        public string LastMessage { get; private set; }

        /// <summary>清掉最近的消息（面板刚打开时可以调一次）。</summary>
        public void ClearMessage()
        {
            LastMessage = string.Empty;
        }

        // ====================================================================
        //  给面板用的两份数据
        // ====================================================================

        /// <summary>把"可接任务"行复制进一个列表（顺序 = 配置表顺序）。</summary>
        /// <param name="buffer">目标列表（会先 Clear）。</param>
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

        /// <summary>把"已接任务"行复制进一个列表（带逐条条件进度）。</summary>
        /// <param name="buffer">目标列表（会先 Clear）。</param>
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

                // ⚠️ **在拼文字之前**把权威值盖上去（`Docs\27` §二十四）：
                //    这一句之后，每一行都带上了"这个数字是谁说的"（`Source`）。
                //    没接权威 / 权威没提过这条 ⇒ 保留本地那份，只是被标成预测。
                QuestProgressOverlay.Apply(tracking.Conditions, Authority);

                row.Detail = BuildDetail(tracking);

                // 只有"条件全满"才让按：让按钮点不动，比让玩家点了被拒绝更友好
                row.ActionEnabled = tracking.State == EQuestState.Completed;
                buffer.Add(row);
            }
        }

        // ====================================================================
        //  动作（面板把点击转到这里）
        // ====================================================================

        /// <summary>
        /// 按按钮名执行动作（`Accept_3004` / `Submit_3004`）。
        /// <para>返回 false 表示"这个名字我认不出来"（面板上多了一个不该有的按钮）。</para>
        /// </summary>
        /// <param name="actionName">按钮名。</param>
        /// <returns>认出来并执行了返回 true。</returns>
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

        /// <summary>接取一个任务（直接调，不走按钮名解析；测试与脚本用）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>结果。</returns>
        public QuestActionResult Accept(int questId)
        {
            if (SendAction != null)
            {
                return RequestRemote(questId, ActionAccept, "接取");
            }

            return Record(m_quests.Accept(questId));
        }

        /// <summary>交付一个任务（直接调）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>结果。</returns>
        public QuestActionResult Submit(int questId)
        {
            if (SendAction != null)
            {
                return RequestRemote(questId, ActionSubmit, "交付");
            }

            return Record(m_quests.Submit(questId));
        }

        /// <summary>
        /// 把动作**发给服务端**并置「待确认」—— ⚠️ **本地一点任务状态都不改**。
        /// <para>服务端认了（或拒了）之后，由收包路径调 `ClearPending` / `FailPending`。</para>
        /// </summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="action">动作编号。</param>
        /// <param name="verb">动作的中文说法（提示语用）。</param>
        /// <returns>请求发出去了就算成功（**不代表任务已经接取/交付**）。</returns>
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

            // ⚠️ 返回"请求发出去了"，**不是**"任务接取成功了" —— 权威在服务端
            return QuestActionResult.Success();
        }

        /// <summary>
        /// **任务状态**的权威来源（M4-S3 §二十七）。
        /// <para>⚠️ 与既有的 `Authority`（**条件进度**权威）**并排、不合并** ——
        /// 一个答"这个任务到哪一步"，一个答"这条条件累计了多少"。</para>
        /// <para>传 null（**默认**）= 一切照旧：分区完全按本地 `QuestRuntime`
        /// （所以既有的 700+ 条 EditMode 行为不变）。</para>
        /// </summary>
        public IQuestStateAuthority StateAuthority { get; set; }

        /// <summary>
        /// **一次遍历填满四个区**（可接 / 进行中 / 可交付 / 已交付）。
        ///
        /// <para>⚠️⚠️ 这是本片最要紧的一处设计：**"挪列"必须一次遍历决定归属**，
        /// 而不是"先从可接里删掉、再加到进行中" —— 后者只要漏掉加的那一步，
        /// 那个任务就**从界面上消失了**，而且**不报错**（这类"多视图挪列"最经典的 bug）。</para>
        ///
        /// <para>做法：把"可接"和"已接"两个来源**放进同一次遍历**，每个任务
        /// 用一个 `placed` 集合保证**只落一个区**；遍历结束四个区的并集 == 输入并集，
        /// **每个任务恰好出现一次**。这条不变式有专门的 EditMode 用例盯着。</para>
        ///
        /// <para>分区规则（三值语义，见 `IQuestStateAuthority`）：
        /// <list type="bullet">
        ///   <item><description>`TryGetState` 返回 **false**（服务端没说）⇒ 按**本地**判断，标「（本地预测）」</description></item>
        ///   <item><description>**true + 0** ⇒ 服务端明确说未接取 ⇒ 放**可接**</description></item>
        ///   <item><description>**1** ⇒ 进行中；**2** ⇒ 可交付；**3** ⇒ 已交付</description></item>
        /// </list></para>
        /// </summary>
        /// <param name="offerBuffer">可接区。</param>
        /// <param name="activeBuffer">进行中区。</param>
        /// <param name="readyBuffer">可交付区。</param>
        /// <param name="doneBuffer">已交付区。</param>
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

            // ⚠️ **分桶决策在 `QuestSectionPlanner`**（引擎无关的纯逻辑 ⇒ 探针能编、能跑、**能变异**）；
            //    本方法只负责把"谁去哪个桶"翻译成**给玩家看的行**。
            //    这段 `placed` 逻辑以前长在这里 ⇒ 探针编不进去 ⇒ "不丢行"只能靠 EditMode 验、**做不出变异**。
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

                // 本地状态 != 0 ⇒ 本地**有**它的 tracking（那种构造带条件行）
                if (placement.LocalState != 0)
                {
                    Place(target, BuildTrackingRow(FindTracking(placement.QuestId), (int)placement.Section));
                }
                else
                {
                    // ⚠️ 服务端可能说它**已经接过**（本地却只有"可接"）⇒ 靠 `placement.Section` 挪到正确的区，
                    //    并且**照样产出一行**（哪怕本地没有 tracking）—— 这就是"挪进来"的那一半。
                    Place(target, BuildOfferRow(FindOffer(placement.QuestId), (int)placement.Section));
                }
            }
        }

        /// <summary>把面板的权威缝接成 planner 要的那个查询委托（`StateAuthority == null` ⇒ 全按本地）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="state">状态。</param>
        /// <returns>服务端表态过时返回 true。</returns>
        private bool QueryStateAuthority(int questId, out int state)
        {
            if (StateAuthority == null)
            {
                state = 0;
                return false;
            }

            return StateAuthority.TryGetState(questId, out state);
        }

        /// <summary>按编号找本地 tracking（调用方保证存在）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>tracking。</returns>
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

        /// <summary>按编号找本地 offer（调用方保证存在）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>offer。</returns>
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

        /// <summary>按区号取对应的列表。</summary>
        /// <param name="offers">可接。</param>
        /// <param name="active">进行中。</param>
        /// <param name="ready">可交付。</param>
        /// <param name="done">已交付。</param>
        /// <param name="section">区号（0..3，见 `EQuestState`）。</param>
        /// <returns>该区的列表。</returns>
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

        /// <summary>把一个任务归到哪个区（权威优先，服务端没说才用本地）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="local">本地认为的状态。</param>
        /// <returns>区号（0..3）。</returns>
        private int SectionOf(int questId, EQuestState local)
        {
            int authoritative;

            // ⚠️ 三值语义：false = 服务端**没说**（不是"未接取"）⇒ 退回本地
            if (StateAuthority != null && StateAuthority.TryGetState(questId, out authoritative))
            {
                return authoritative;
            }

            return (int)local;
        }

        /// <summary>这一行的状态是"服务端说的"还是"本地猜的"（标注文案只定义一次）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>标注文案（权威为空串）。</returns>
        private string SourceMarkOf(int questId)
        {
            int ignored;

            if (StateAuthority != null && StateAuthority.TryGetState(questId, out ignored))
            {
                return string.Empty;    // 服务端说的 ⇒ 不标
            }

            return QuestProgressOverlay.LocalMark;
        }

        /// <summary>"待确认"的标注（`IsPending` 时加）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>标注文案。</returns>
        private string PendingMarkOf(int questId)
        {
            return IsPending(questId) ? "  ⏳待确认" : string.Empty;
        }

        /// <summary>把一个任务行放进去（**只放一次**，调用方保证）。</summary>
        /// <param name="buffer">目标区。</param>
        /// <param name="row">行。</param>
        private static void Place(List<QuestRow> buffer, QuestRow row)
        {
            buffer.Add(row);
        }

        /// <summary>造"从可接那边来"的一行（服务端可能已经说它接过了 ⇒ 靠 `section` 决定放哪）。</summary>
        /// <param name="offer">可接任务。</param>
        /// <param name="section">归到哪个区（0..3）。</param>
        /// <returns>行。</returns>
        private QuestRow BuildOfferRow(QuestOffer offer, int section)
        {
            QuestRow row = new QuestRow();
            row.QuestId = offer.QuestId;
            row.ActionName = AcceptPrefix + offer.QuestId;
            row.ActionLabel = "接取";

            // ⚠️ **只有"可接"区能点接取**：被权威挪到别的区的（服务端说已接）不许再显示接取按钮 ——
            //    否则玩家会对着一个"其实已经接过"的任务反复点接取。
            row.ActionEnabled = section == 0 && !IsPending(offer.QuestId);

            row.Title = "[" + offer.QuestId + "] " + offer.Name +
                        PendingMarkOf(offer.QuestId) + SourceMarkOf(offer.QuestId);

            // ⚠️ 本地**没有** tracking ⇒ 拿不到条件行（条件编号在配置里，面板没有那份数据）。
            //    这里**绝不**编造「0/N」——那正是 §二十四 那条不变式禁止的事（把"没有数据"画成"进度是 0"）。
            //    如实说一句，让玩家知道"明细还没同步过来"。
            row.Detail = section == 0
                ? offer.Description
                : offer.Description + "\n（服务端说这个任务已经接取，但本地还没有它的进度明细 —— 等同步）";

            return row;
        }

        /// <summary>造"从已接那边来"的一行（带逐条条件进度）。</summary>
        /// <param name="tracking">追踪视图。</param>
        /// <param name="section">归到哪个区（0..3）。</param>
        /// <returns>行。</returns>
        private QuestRow BuildTrackingRow(QuestTracking tracking, int section)
        {
            QuestRow row = new QuestRow();
            row.QuestId = tracking.QuestId;
            row.ActionName = SubmitPrefix + tracking.QuestId;
            row.ActionLabel = "交付";

            // ⚠️ **只有"可交付"区能点交付**，而且「待确认」期间不许再点（免得重复发请求）
            row.ActionEnabled = section == 2 && !IsPending(tracking.QuestId);

            row.Title = "[" + tracking.QuestId + "] " + tracking.Name +
                        "（" + StateText(tracking.State) + "）" +
                        PendingMarkOf(tracking.QuestId) + SourceMarkOf(tracking.QuestId);

            // 条件那几行照旧：**权威进度优先**盖上去，服务端没说的条件保持本地值并标注（§二十四）
            QuestProgressOverlay.Apply(tracking.Conditions, Authority);

            row.Detail = BuildDetail(tracking);
            return row;
        }


        /// <summary>按按钮名解析出的动作真正执行。</summary>
        /// <param name="idText">按钮名里那截数字文本。</param>
        /// <param name="accept">true = 接取，false = 交付。</param>
        private void Apply(string idText, bool accept)
        {
            int questId;

            if (!int.TryParse(idText, out questId))
            {
                // 按钮名是**我们自己做出来的**，所以这里不可能是玩家输入错误 = 程序错误
                LastMessage = "（内部错误：按钮名里的任务编号不是数字「" + idText + "」）";
                return;
            }

            // ⚠️ 接了服务端就**不许**在本地改任务状态（见 `SendAction`）
            if (SendAction != null)
            {
                RequestRemote(questId, accept ? ActionAccept : ActionSubmit, accept ? "接取" : "交付");
                return;
            }

            Record(accept ? m_quests.Accept(questId) : m_quests.Submit(questId));
        }

        /// <summary>把一次动作的结果记成"要给玩家看的消息"。</summary>
        /// <param name="result">结果。</param>
        /// <returns>原样返回（方便链式调用与测试）。</returns>
        private QuestActionResult Record(QuestActionResult result)
        {
            LastMessage = result.Ok ? "已受理。" : result.Reason;
            return result;
        }

        /// <summary>任务状态的中文说法（面板直接显示）。</summary>
        /// <param name="state">状态。</param>
        /// <returns>中文。</returns>
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

        /// <summary>把逐条条件拼成多行详情。</summary>
        /// <param name="tracking">追踪视图。</param>
        /// <returns>多行文本。</returns>
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
                       // ⚠️ 来源标注（**权威不标、预测才标**）：见 `QuestProgressOverlay.Mark`。
                       //    没有它，玩家就分不清"这一格是事实"还是"客户端猜的"。
                       .Append(QuestProgressOverlay.Mark(line.Source));
            }

            return builder.ToString();
        }
    }
}
