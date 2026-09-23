// ============================================================================
//  GameTestTables —— Game 层测试共用的「干净」配置夹具（任务 / 条件 / 奖励）
//  项目：3D联网战斗Demo   对应：M2-C2 / M2-D
//
//  ---------------------------------------------------------------------------
//  为什么要把这份夹具抽出来
//  ---------------------------------------------------------------------------
//  `M2EndToEndTests`（端到端）与任务 UI 的两组测试需要**同一套数值**才能互相对答案：
//  "任务 3004 = 击杀 3 只野狼 + 抵达 1 号区域"、"奖励 5001 = 100 经验 + 50 金币 + 狼皮 ×2"。
//  两边各写一份的话，数值一漂移就会出现**各自绿着、合起来没人验过**的情况。
//
//  📌 这里只集中**造数据**，不集中断言：
//     断言里的数字**照旧写死字面量**（`Assert.AreEqual(3, progress.Required)`）——
//     期望值引用常量的话，常量改错会让测试跟着一起错，等于没测。
//
//  ⚠️ `QuestRuntimeTests` **不用**这份夹具：它的夹具是**故意做坏的**
//     （缺条件的、条件为空的、条件非法的、奖励缺行的），那是另一类用途。
// ============================================================================

using System.Collections.Generic;
using NBC.Game.Config;
using NBC.Shared.Condition;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>Game 层测试共用的任务类配置表工厂。</summary>
    internal static class GameTestTables
    {
        /// <summary>演示任务：清剿野狼（击杀 3 只野狼 + 抵达一号区域）。</summary>
        public const int DemoQuest = 3004;

        /// <summary>演示任务的击杀条件。</summary>
        public const int KillWolfCondition = 4007;

        /// <summary>演示任务的区域条件。</summary>
        public const int ReachAreaCondition = 4008;

        /// <summary>演示任务的奖励。</summary>
        public const int DemoReward = 5001;

        /// <summary>第二个任务：草药采集（**单条件**，用来验"可接列表有多条"）。</summary>
        public const int GatherQuest = 3002;

        /// <summary>第二个任务的条件。</summary>
        public const int GatherCondition = 4003;

        /// <summary>第二个任务的奖励。</summary>
        public const int GatherReward = 5002;

        /// <summary>奖励 5001 里的经验。</summary>
        public const int DemoRewardExp = 100;

        /// <summary>奖励 5001 里的金币。</summary>
        public const int DemoRewardGold = 50;

        /// <summary>奖励 5001 里的物品编号（狼皮）。</summary>
        public const int DemoRewardItemId = 7001;

        /// <summary>奖励 5001 里的物品数量。</summary>
        public const int DemoRewardItemCount = 2;

        /// <summary>造任务表。</summary>
        /// <returns>资产（用完要 `Destroy`）。</returns>
        public static QuestConfig CreateQuests()
        {
            QuestConfig table = ScriptableObject.CreateInstance<QuestConfig>();
            table.rows = new List<Config_Quest>
            {
                Quest(DemoQuest, "清剿野狼", "清剿 3 只野狼并抵达一号区域",
                      new[] { KillWolfCondition, ReachAreaCondition }, DemoReward),
                Quest(GatherQuest, "草药采集", "帮药师采集 2 份草药",
                      new[] { GatherCondition }, GatherReward)
            };
            table.RebuildIndex();
            return table;
        }

        /// <summary>造条件表。</summary>
        /// <returns>资产（用完要销毁）。</returns>
        public static QuestConditionConfig CreateQuestConditions()
        {
            QuestConditionConfig table = ScriptableObject.CreateInstance<QuestConditionConfig>();
            table.rows = new List<Config_QuestCondition>
            {
                Condition(KillWolfCondition, EConditionEvent.KillMonster, BattleTestTables.Wolf, 3),
                Condition(ReachAreaCondition, EConditionEvent.ReachArea, 1, 1),
                Condition(GatherCondition, EConditionEvent.CollectItem, 7002, 2)
            };
            table.RebuildIndex();
            return table;
        }

        /// <summary>造奖励表。</summary>
        /// <returns>资产（用完要销毁）。</returns>
        public static RewardConfig CreateRewards()
        {
            RewardConfig table = ScriptableObject.CreateInstance<RewardConfig>();
            table.rows = new List<Config_Reward>
            {
                Reward(DemoReward, DemoRewardExp, DemoRewardGold, DemoRewardItemId, DemoRewardItemCount),
                Reward(GatherReward, 80, 40, 7002, 1)
            };
            table.RebuildIndex();
            return table;
        }

        /// <summary>造一行条件。</summary>
        /// <param name="id">编号。</param>
        /// <param name="eventType">事件类型。</param>
        /// <param name="targetId">目标编号。</param>
        /// <param name="required">需要数量。</param>
        /// <returns>行。</returns>
        public static Config_QuestCondition Condition(int id, EConditionEvent eventType,
                                                     int targetId, int required)
        {
            Config_QuestCondition row = new Config_QuestCondition();
            row.id = id;
            row.eventType = eventType;
            row.targetId = targetId;
            row.requiredCount = required;
            row.note = "测试夹具";
            return row;
        }

        /// <summary>造一行奖励。</summary>
        /// <param name="id">编号。</param>
        /// <param name="exp">经验。</param>
        /// <param name="gold">金币。</param>
        /// <param name="itemId">物品编号。</param>
        /// <param name="itemCount">物品数量。</param>
        /// <returns>行。</returns>
        public static Config_Reward Reward(int id, int exp, int gold, int itemId, int itemCount)
        {
            Config_Reward row = new Config_Reward();
            row.id = id;
            row.exp = exp;
            row.gold = gold;
            row.itemId = itemId;
            row.itemCount = itemCount;
            return row;
        }

        /// <summary>造一行任务。</summary>
        /// <param name="id">编号。</param>
        /// <param name="name">名称。</param>
        /// <param name="desc">描述。</param>
        /// <param name="conditionIds">条件编号。</param>
        /// <param name="rewardId">奖励编号。</param>
        /// <returns>行。</returns>
        public static Config_Quest Quest(int id, string name, string desc, int[] conditionIds, int rewardId)
        {
            Config_Quest row = new Config_Quest();
            row.id = id;
            row.name = name;
            row.desc = desc;
            row.conditionIds = conditionIds;
            row.rewardId = rewardId;
            return row;
        }
    }
}
