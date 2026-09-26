// ============================================================================
//  AchievementTypes —— 成就模块用到的小类型（追踪视图）
//  项目：3D联网战斗Demo   对应：M4-S2（成就）
//
//  ---------------------------------------------------------------------------
//  成就与任务**只差三点**（把这个记牢，这一片就没有别的难点了）
//  ---------------------------------------------------------------------------
//      ① **没有接取**：成就是"一直生效"的，构造时就全部登记（任务要玩家点「接取」）
//      ② **进度不清零**：`Register(..., resetProgress: false)`（任务传 true）
//      ③ **没有交付**：条件一齐**当场解锁并发奖**（任务要玩家点「交付」）
//
//  ⇒ 所以**没有 `EAchievementState` 那种五态枚举** —— 成就只有"锁着 / 解锁了"两种。
//    多写一个枚举只会多一处能不同步的状态（`ConditionTracker` 文件头第 ① 条同一个道理）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么复用 `QuestConditionLine` 而不是自己再写一个
//  ---------------------------------------------------------------------------
//  追踪条要显示的东西**一模一样**："这一条条件的文字描述 + 现在几 / 要几 + 达成没有"。
//  再写一个同形状的类型，只会让"以后想加一列（比如条件图标）"变成**要改两处**。
//  名字里的 `Quest` 同样是历史遗留 —— 它其实是"**一条条件的进度行**"，
//  成就是第二个使用者（与 `IQuestRewardSink` 同一族，见那个文件的说明）。
// ============================================================================

using System.Collections.Generic;
using NBC.Game.Quest;

namespace NBC.Game.Achievement
{
    /// <summary>一个成就的追踪视图（给 UI / 调试面板用）。</summary>
    public sealed class AchievementTracking
    {
        /// <summary>成就编号。</summary>
        public int AchievementId;

        /// <summary>成就名（来自配置表）。</summary>
        public string Name;

        /// <summary>成就描述（来自配置表）。</summary>
        public string Description;

        /// <summary>解锁了没有。</summary>
        public bool IsUnlocked;

        /// <summary>各条件的进度（顺序与配置表一致）。</summary>
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

        /// <summary>转成一句人话（控制台 / 日志用）。</summary>
        /// <returns>形如「[9001] 初出茅庐 · 已解锁」。</returns>
        public override string ToString()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append('[').Append(AchievementId).Append("] ").Append(Name)
                   .Append(" · ").Append(IsUnlocked ? "已解锁" : "未解锁");

            for (int i = 0; i < Conditions.Count; i++)
            {
                builder.Append('\n').Append("    · ").Append(Conditions[i].Description)
                       .Append("  ").Append(Conditions[i].Current).Append('/')
                       .Append(Conditions[i].Required)
                       .Append(Conditions[i].IsMet ? "  ✅" : string.Empty);
            }

            return builder.ToString();
        }
    }
}
