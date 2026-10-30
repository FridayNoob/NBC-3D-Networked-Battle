// 这个文件里有：
//   AchievementTracking（类）：一个成就的追踪视图，供调试面板 / UI 读取编号、文案、解锁状态与逐条条件进度

using System.Collections.Generic;
using NBC.Game.Quest;

namespace NBC.Game.Achievement
{
    // 主要功能：把一条成就配置与它的实时进度摊平成扁平对象，让调试面板不必再碰运行时内部状态。
    // 主要数据成员：AchievementId（成就编号）、Name / Description（配置文本）、IsUnlocked（是否解锁）、Conditions（逐条条件进度）。
    // 主要外部方法：AllMet（全部条件是否达成）、ToString（拼成多行文本）。
    // 谁在用它：AchievementRuntime 的 BuildTracking / CopyTrackings 产出；NetDebugWindow 的成就页读字段显示；EditMode 测试做断言。
    // 注意：成就与任务只差三点 —— 没有接取（构造时全部登记）、进度不清零、没有交付（条件一齐当场解锁并发奖），
    //   所以状态只有锁着 / 解锁两态，没有五态枚举；Conditions 复用任务侧的 QuestConditionLine，
    //   因为追踪条要显示的内容一模一样，另写一个类型只会让以后加一列要改两处（Quest 前缀是历史名）。
    /// <summary>一个成就的追踪视图，供调试面板 / UI 读取编号、文案、解锁状态与逐条条件进度。</summary>
    public sealed class AchievementTracking
    {
        /// <summary>成就编号，对应配置表 Achievement 的 id；配置表主键，正整数。</summary>
        public int AchievementId;

        /// <summary>成就名，来自配置表；配置没填时为空。</summary>
        public string Name;

        /// <summary>成就描述，来自配置表；配置没填时为空。</summary>
        public string Description;

        /// <summary>是否已解锁；false = 锁着。成就只有锁着 / 解锁两态。</summary>
        public bool IsUnlocked;

        /// <summary>各条件的进度，顺序与配置表的 conditionIds 一致；0 条表示没有配置条件。</summary>
        public readonly List<QuestConditionLine> Conditions = new List<QuestConditionLine>();

        // 功能：判断所有条件是否都已达成。
        // 入参：无（读 Conditions 里每一行的 IsMet）
        // 返回：true = 至少有一条条件且全部达成；false = 一条条件都没有，或还有条件没达成。
        // 步骤：
        //   1) 逐条看 IsMet，遇到没达成的立刻返回 false —— 后面的不必再看
        //   2) 全部达成后仍要 Conditions.Count > 0，空列表不算「全部达成」
        // 上游：目前只有 EditMode 测试（AchievementRuntimeTests）用
        // 下游：QuestConditionLine.IsMet
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

        // 功能：把一个成就连同逐条条件拼成多行人话，供控制台与日志直接输出。
        // 入参：无（读本对象字段与 Conditions）
        // 返回：首行形如「[9001] 初出茅庐 · 已解锁」，其后每条条件一行「    · 描述  1/2  ✅」。
        // 步骤：
        //   1) 先拼编号、名字与解锁状态，保证一眼看出是哪个成就
        //   2) 再按配置顺序逐条追加描述与「当前 / 需要」，达成时才补勾，避免把未达成看成达成
        // 上游：AchievementRuntime.Describe（控制台 / 日志）
        // 下游：Conditions 里每行的 Description / Current / Required / IsMet
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
