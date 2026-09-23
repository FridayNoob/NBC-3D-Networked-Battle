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

        /// <summary>复用的临时列表（避免每次刷新都分配）。</summary>
        private readonly List<QuestOffer> m_offers = new List<QuestOffer>();

        /// <summary>复用的临时列表（避免每次刷新都分配）。</summary>
        private readonly List<QuestTracking> m_trackings = new List<QuestTracking>();

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
            return Record(m_quests.Accept(questId));
        }

        /// <summary>交付一个任务（直接调）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>结果。</returns>
        public QuestActionResult Submit(int questId)
        {
            return Record(m_quests.Submit(questId));
        }

        // ====================================================================
        //  内部
        // ====================================================================

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
                       .Append(line.IsMet ? "  ✅" : string.Empty);
            }

            return builder.ToString();
        }
    }
}
