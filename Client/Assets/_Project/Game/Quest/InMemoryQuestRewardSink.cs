// ============================================================================
//  InMemoryQuestRewardSink —— 把奖励记在内存里的实现（M2 用这个；M4 换服务端权威）
//  项目：3D联网战斗Demo   对应：M2-A2、Docs\20 §三 M4
//
//  ---------------------------------------------------------------------------
//  它不是"占位符"，而是**能验收到账的实现**
//  ---------------------------------------------------------------------------
//  M2 的验收线是「**奖励真的发下去**」。所以这个实现必须能回答：
//      "经验一共加了多少？发了哪几件物品？"
//  否则测试只能断言"Grant 被调用过"，而**调用过 ≠ 内容对** ——
//  这正是 M1-B5 那次"打印全空却报 ✅"的教训（判据要盯着结果，不是盯着中间物）。
//
//  ⚠️ 它**故意不模拟角色属性**（不加到真实角色身上）：
//     那属于角色模块，M2 还没有。这里只做"到账记录"，
//     换实现时对答案用的就是这几个数字。
// ============================================================================

using System.Collections.Generic;
using System.Text;

namespace NBC.Game.Quest
{
    /// <summary>把奖励记在内存里的发奖实现。</summary>
    public sealed class InMemoryQuestRewardSink : IQuestRewardSink
    {
        /// <summary>累计经验。</summary>
        private int m_totalExp;

        /// <summary>累计金币。</summary>
        private int m_totalGold;

        /// <summary>物品编号 → 累计数量。</summary>
        private readonly Dictionary<int, int> m_items = new Dictionary<int, int>();

        /// <summary>按顺序记下每一笔发放（测试断言"发了什么"用）。</summary>
        private readonly List<QuestReward> m_history = new List<QuestReward>();

        /// <summary>累计经验。</summary>
        public int TotalExp
        {
            get { return m_totalExp; }
        }

        /// <summary>累计金币。</summary>
        public int TotalGold
        {
            get { return m_totalGold; }
        }

        /// <summary>发过几次奖励。</summary>
        public int GrantCount
        {
            get { return m_history.Count; }
        }

        /// <summary>某件物品累计到账多少个。</summary>
        /// <param name="itemId">物品编号。</param>
        /// <returns>数量（没发过时 0）。</returns>
        public int ItemCountOf(int itemId)
        {
            int count;
            return m_items.TryGetValue(itemId, out count) ? count : 0;
        }

        /// <summary>第 <paramref name="index"/> 笔发放的内容。</summary>
        /// <param name="index">下标（0 基）。</param>
        /// <returns>奖励内容。</returns>
        public QuestReward HistoryAt(int index)
        {
            return m_history[index];
        }

        /// <summary>把一份奖励发下去（记到账）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="reward">奖励内容。</param>
        public void Grant(int questId, QuestReward reward)
        {
            m_totalExp += reward.Exp;
            m_totalGold += reward.Gold;

            if (reward.ItemId != 0 && reward.ItemCount > 0)
            {
                int old;
                m_items.TryGetValue(reward.ItemId, out old);
                m_items[reward.ItemId] = old + reward.ItemCount;
            }

            m_history.Add(reward);
        }

        /// <summary>清空记录（测试收尾、重跑演示用）。</summary>
        public void Clear()
        {
            m_totalExp = 0;
            m_totalGold = 0;
            m_items.Clear();
            m_history.Clear();
        }

        /// <summary>转成一句人话。</summary>
        /// <returns>例如「经验 180、金币 90、物品 1 种、共 2 笔」。</returns>
        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("经验 ").Append(m_totalExp)
                   .Append("、金币 ").Append(m_totalGold)
                   .Append("、物品 ").Append(m_items.Count).Append(" 种")
                   .Append("、共 ").Append(m_history.Count).Append(" 笔");
            return builder.ToString();
        }
    }
}
