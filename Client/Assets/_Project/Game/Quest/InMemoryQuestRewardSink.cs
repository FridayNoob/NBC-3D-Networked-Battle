// 这个文件里有：
//   InMemoryQuestRewardSink（类）：把发下去的奖励记在内存里的发奖实现，供验收直接断言经验、金币、物品各到账多少

using System.Collections.Generic;
using System.Text;

namespace NBC.Game.Quest
{
    // 主要功能：把每一笔发奖累加进内存账（经验、金币、物品件数、发放历史），让「奖励真的发下去」这件事能被断言。
    // 主要数据成员：m_totalExp（累计经验，点）、m_totalGold（累计金币，枚）、m_items（物品编号 → 累计件数）、m_history（按发放顺序的每笔奖励）。
    // 主要外部方法：TotalExp / TotalGold / GrantCount / ItemCountOf / HistoryAt（只读查询，给断言用）、Grant（记一笔账）、Clear（清空）、ToString（一句话汇总）。
    // 谁在用它：Boot 的 M2DemoBehaviour 造一个当 BattleSession 的发奖接缝，交付成功后打印汇总；EditMode 的 QuestRuntimeTests / M2EndToEndTests / AchievementRuntimeTests / QuestFollowTests 用它断言到账内容。
    // 注意：它不是占位符，而是能验收到账的实现 —— 验收线是「奖励真的发下去」，所以要能回答「经验一共加了多少、发了哪几件物品」；只断言 Grant 被调用过不够，调用过不等于内容对（曾经有过「打印全空却报通过」的教训，判据要盯结果、不盯中间物）。
    // 注意：它故意不把奖励加到真实角色身上 —— 角色属性属于角色模块，M2 还没有；这里只做到账记录，换实现时对答案用的就是这几个数字。
    // 注意：M2 先用这个内存实现，M4 换服务端权威实现；进程一退记录就没了，别拿它当持久化。
    /// <summary>把奖励记在内存里的发奖实现，供验收直接断言到账内容。</summary>
    public sealed class InMemoryQuestRewardSink : IQuestRewardSink
    {
        // 累计经验，单位：点；只加不减，Clear 才归零
        private int m_totalExp;

        // 累计金币，单位：枚；只加不减，Clear 才归零
        private int m_totalGold;

        // 物品编号（配置表主键）→ 累计件数，单位：件；编号 0（没有物品）不会成为键
        private readonly Dictionary<int, int> m_items = new Dictionary<int, int>();

        // 每一笔发放的奖励原文，按发放顺序追加，下标 0 基；只追加不覆盖，Clear 才清空
        private readonly List<QuestReward> m_history = new List<QuestReward>();

        // 功能：读累计经验，单位：点（只读，不改任何状态）。
        public int TotalExp
        {
            get { return m_totalExp; }
        }

        // 功能：读累计金币，单位：枚（只读，不改任何状态）。
        public int TotalGold
        {
            get { return m_totalGold; }
        }

        // 功能：读发过几笔奖励，等于历史条数（奖励全 0 的笔也算一笔）。
        public int GrantCount
        {
            get { return m_history.Count; }
        }

        // 功能：查某件物品累计到账多少件，没发过就算 0，调用方不必先判存在。
        // 入参：itemId 物品编号（配置表主键；0 表示没有物品，查它必然得 0）
        // 步骤：只有一步查表 —— 把 TryGetValue 的 false 直接翻成 0，让「没发过」和「发过 0 件」共用一个答案，省掉调用方分支
        // 返回：该物品的累计件数（>= 0）；0 = 一次都没发过这件
        // 上游：EditMode 的 QuestRuntimeTests / M2EndToEndTests / AchievementRuntimeTests（断言某件物品发了几个）
        // 下游：m_items
        public int ItemCountOf(int itemId)
        {
            int count;
            return m_items.TryGetValue(itemId, out count) ? count : 0;
        }

        // 功能：按发放顺序取第 index 笔奖励的原文，用来核对「发的到底是配置表哪一行」。
        // 入参：index 第几笔，0 基（0 = 第一笔，合法范围 0..GrantCount-1）
        // 步骤：只有一步下标访问 —— 故意不加范围判断，越界让 List 自己抛，守住「拿错下标是调用方的 bug」这条界线
        // 返回：那一笔的 QuestReward（奖励编号、经验、金币、物品编号与件数）
        // 上游：EditMode 的 AchievementRuntimeTests（核对发出的 RewardId 是不是配置表那一行）
        // 下游：m_history
        // 注意：越界会抛 ArgumentOutOfRangeException；要先看 GrantCount 再取，别把「一笔都没发」当成空奖励。
        public QuestReward HistoryAt(int index)
        {
            return m_history[index];
        }

        // 功能：把一份奖励记进内存账：累加经验与金币、按物品编号累加件数、并把原文追加进历史。
        // 入参：questId 任务编号（只为满足 IQuestRewardSink 的签名，本实现不记它）；reward 奖励内容（奖励编号、经验、金币、物品编号与件数）
        // 步骤：
        //   1) 先累加经验与金币 —— 这两项每笔都算，不看有没有物品
        //   2) 物品编号非 0 且件数大于 0 才累加，否则会多记出一种「没有的物品」，把汇总的种数算虚
        //   3) 最后追加历史 —— 顺序就是发放顺序，HistoryAt 的下标全靠它
        // 返回：无；到账结果只能从 TotalExp / TotalGold / ItemCountOf / GrantCount 反查
        // 上游：QuestRuntime 的交付流程判定通过后、AchievementRuntime 解锁成就时（都经 IQuestRewardSink）
        // 下游：m_items、m_history
        // 注意：只要被调用就记一笔，哪怕经验、金币、物品全为 0 —— 笔数口径是「调用了几次」，不是「有东西到账」。
        // 注意：questId 没进历史，所以从这里查不出「哪条任务发的」；要查只能另加字段，别指望 reward 里有。
        // 注意：它只累加、不校验、不抛异常，调用方才能放心按「先改状态、再发奖」的顺序写（成就那边就是这个理由）。
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

        // 功能：把四样记录全部归零，让同一进程里的下一局从干净状态开始。
        // 入参：无
        // 步骤：四样逐一清 —— 经验、金币、物品表、历史；少清一样，下一局的总计就会带着上一局的数一起长
        // 返回：无
        // 上游：EditMode 测试的收尾与重跑（Boot 的演示重跑不调它，见注意）
        // 下游：m_items、m_history 的 Clear
        // 注意：Boot 的演示按 R 重跑时不清它，所以那边的汇总是累计的；要归零必须显式调 Clear。
        public void Clear()
        {
            m_totalExp = 0;
            m_totalGold = 0;
            m_items.Clear();
            m_history.Clear();
        }

        // 功能：把当前到账情况拼成一句人话，供控制台与断言失败信息直接输出。
        // 入参：无
        // 步骤：按「经验 → 金币 → 物品种数 → 笔数」的顺序拼，每项都是名字紧跟数字，读日志时不用回查代码
        // 返回：形如「经验 180、金币 90、物品 1 种、共 2 笔」；物品那项是种类数（字典键数），不是总件数，别和 ItemCountOf 混
        // 上游：Boot 的 M2DemoBehaviour 在交付成功后打印「奖励到账：」后面这句
        // 下游：StringBuilder
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
