// ============================================================================
//  QuestTypes —— 任务模块用到的小类型（状态 / 操作结果 / 奖励 / 追踪视图）
//  项目：3D联网战斗Demo   对应：M2-A2（任务运行时）
//
//  ---------------------------------------------------------------------------
//  为什么"玩家操作的结果"用返回值 + 原因字符串，而不是抛异常
//  ---------------------------------------------------------------------------
//  本项目的规矩是"**静默失败最危险**"，但"报错"有两种形态，要分清：
//
//      · **程序错误**（枚举值不合法、重复登记同一个条件）→ **抛异常**
//        它们只可能是 bug，越早炸越好（而且不该被 catch 掉当业务分支用）
//      · **玩家能触发的拒绝**（重复接取、条件没达成就要交付）→ **返回值 + 原因**
//        玩家点一次按钮不能把游戏炸掉；这类"失败"是**正常业务流程**的一部分，
//        必须有一条**给人看的原因**，而不是一个异常堆栈
//
//  `QuestActionResult` 就是后者的形状：**要么成功，要么带一句人话**。
//
//  ---------------------------------------------------------------------------
//  追踪视图为什么要单独一个类型
//  ---------------------------------------------------------------------------
//  UI（任务追踪条）需要的是"这个任务现在长什么样"：
//      任务名 + 每条条件的 `2/3`
//  而不是去问 `ConditionTracker` 一堆编号。把"给 UI 看的形状"和"内部状态"
//  分开，UI 就永远不需要知道条件系统的存在 —— 换一套 UI 也不用动逻辑。
// ============================================================================

using System.Collections.Generic;

namespace NBC.Game.Quest
{
    /// <summary>一个任务在玩家身上的状态。</summary>
    public enum EQuestState
    {
        /// <summary>没接过（或已放弃）。</summary>
        None = 0,

        /// <summary>已接取、还没做完。</summary>
        Accepted = 1,

        /// <summary>条件全部达成、**等着交付**。</summary>
        Completed = 2,

        /// <summary>已交付（奖励已发）。</summary>
        Submitted = 3
    }

    /// <summary>一次任务操作的结果。</summary>
    public readonly struct QuestActionResult
    {
        /// <summary>成功了吗。</summary>
        private readonly bool m_ok;

        /// <summary>失败原因（成功时为 null）。</summary>
        private readonly string m_reason;

        /// <summary>造一个结果。</summary>
        /// <param name="ok">成功了吗。</param>
        /// <param name="reason">失败原因。</param>
        private QuestActionResult(bool ok, string reason)
        {
            m_ok = ok;
            m_reason = reason;
        }

        /// <summary>成功了吗。</summary>
        public bool Ok
        {
            get { return m_ok; }
        }

        /// <summary>失败原因（成功时为 null）。</summary>
        public string Reason
        {
            get { return m_reason; }
        }

        /// <summary>成功。</summary>
        public static QuestActionResult Success()
        {
            return new QuestActionResult(true, null);
        }

        /// <summary>失败（**必须给一句人话**：这句话会显示给玩家）。</summary>
        /// <param name="reason">原因。</param>
        /// <returns>结果。</returns>
        public static QuestActionResult Fail(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                // 空原因等于"玩家点了没反应" —— 那正是本项目一直在防的形态
                reason = "（内部错误：拒绝原因没写）";
            }

            return new QuestActionResult(false, reason);
        }

        /// <summary>转成一句人话。</summary>
        /// <returns>描述。</returns>
        public override string ToString()
        {
            return m_ok ? "成功" : "失败：" + m_reason;
        }
    }

    /// <summary>一次奖励的内容（**与配置表的行解耦**：发奖方不需要认识生成类型）。</summary>
    public readonly struct QuestReward
    {
        /// <summary>奖励编号（奖励表主键）。</summary>
        public readonly int RewardId;

        /// <summary>经验。</summary>
        public readonly int Exp;

        /// <summary>金币。</summary>
        public readonly int Gold;

        /// <summary>物品编号（0 = 没有物品）。</summary>
        public readonly int ItemId;

        /// <summary>物品数量（<see cref="ItemId"/> 为 0 时无意义）。</summary>
        public readonly int ItemCount;

        /// <summary>造一份奖励。</summary>
        /// <param name="rewardId">奖励编号。</param>
        /// <param name="exp">经验。</param>
        /// <param name="gold">金币。</param>
        /// <param name="itemId">物品编号（0 = 无）。</param>
        /// <param name="itemCount">物品数量。</param>
        public QuestReward(int rewardId, int exp, int gold, int itemId, int itemCount)
        {
            RewardId = rewardId;
            Exp = exp;
            Gold = gold;
            ItemId = itemId;
            ItemCount = itemCount;
        }

        /// <summary>转成一句人话（日志/UI 用）。</summary>
        /// <returns>例如「经验 +100、金币 +50、物品 7001 ×3」。</returns>
        public override string ToString()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("经验 +").Append(Exp).Append("、金币 +").Append(Gold);

            if (ItemId != 0)
            {
                builder.Append("、物品 ").Append(ItemId).Append(" ×").Append(ItemCount);
            }

            return builder.ToString();
        }
    }

    /// <summary>追踪视图里的一行（一条条件）。</summary>
    public sealed class QuestConditionLine
    {
        /// <summary>条件编号。</summary>
        public int ConditionId;

        /// <summary>条件的文字描述（例如「击杀怪物 ×3（目标 6001）」）。</summary>
        public string Description;

        /// <summary>已累计数量。</summary>
        public int Current;

        /// <summary>需要数量。</summary>
        public int Required;

        /// <summary>达成了没有。</summary>
        public bool IsMet;
    }

    /// <summary>一个**可接任务**（配置里有、玩家还没接）的展示信息。</summary>
    public readonly struct QuestOffer
    {
        /// <summary>任务编号。</summary>
        public readonly int QuestId;

        /// <summary>任务名。</summary>
        public readonly string Name;

        /// <summary>任务描述。</summary>
        public readonly string Description;

        /// <summary>造一条可接信息（只有 `QuestRuntime` 会造）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="name">任务名。</param>
        /// <param name="description">描述。</param>
        internal QuestOffer(int questId, string name, string description)
        {
            QuestId = questId;
            Name = name;
            Description = description;
        }

        /// <summary>转成一句人话。</summary>
        /// <returns>描述。</returns>
        public override string ToString()
        {
            return "[" + QuestId + "] " + Name;
        }
    }

    /// <summary>一个任务的追踪视图（给 UI 用）。</summary>
    public sealed class QuestTracking
    {
        /// <summary>任务编号。</summary>
        public int QuestId;

        /// <summary>任务名（来自配置表）。</summary>
        public string Name;

        /// <summary>任务描述（来自配置表）。</summary>
        public string Description;

        /// <summary>当前状态。</summary>
        public EQuestState State;

        /// <summary>各条条件的进度（顺序与配置表一致）。</summary>
        public readonly List<QuestConditionLine> Conditions = new List<QuestConditionLine>();

        /// <summary>全部条件都达成了吗。</summary>
        public bool AllMet
        {
            get
            {
                for (int i = 0; i < Conditions.Count; i++)
                {
                    if (!Conditions[i].IsMet)
                    {
                        return false;
                    }
                }

                return Conditions.Count > 0;
            }
        }

        /// <summary>转成一句人话（控制台/日志用）。</summary>
        /// <returns>多行文本。</returns>
        public override string ToString()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append('[').Append(QuestId).Append("] ").Append(Name)
                   .Append("（").Append(State).Append("）\n");

            for (int i = 0; i < Conditions.Count; i++)
            {
                builder.Append("    · ").Append(Conditions[i].Description)
                       .Append("  ").Append(Conditions[i].Current).Append('/')
                       .Append(Conditions[i].Required)
                       .Append(Conditions[i].IsMet ? "  ✅" : string.Empty).Append('\n');
            }

            return builder.ToString();
        }
    }
}
