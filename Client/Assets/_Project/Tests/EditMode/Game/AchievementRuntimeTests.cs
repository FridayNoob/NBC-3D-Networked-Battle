// ============================================================================
//  M4-S2 · 成就运行时的 EditMode 测试
//  对应验收：Docs\27-M4开工清单.md §十（M4-S2 成就）
//  被测：Client\Assets\_Project\Game\Achievement\
//
//  ---------------------------------------------------------------------------
//  这一组测的是**成就与任务那三处不同**，不是"某个方法"
//  ---------------------------------------------------------------------------
//      · 没有接取     → **构造时就该全部登记**
//      · 进度不清零   → **跨局累计**（这是成就的定义性行为）
//      · 没有交付     → 条件一齐**当场解锁 + 当场发奖**
//
//  ⚠️ 最要紧的一条是 `AlreadyMetOnConstruction_UnlocksImmediately`：
//     它钉住的是 `ConditionTracker` 语义②（`resetProgress: false` + 注册时已达成 → 当场回调）
//     与"**先记归属、再登记**"那个**顺序**要求。
//     写反了的后果是"登录时该解锁的成就永远不解锁，而且一行日志都没有" ——
//     这正是"看起来像玄学"的那类 bug。所以它必须有测试。
//
//  ⚠️ 发奖断言看的是**到账的数字**（经验/金币/物品数量），不是"Grant 被调用过"：
//     M1-B5 那次的教训 —— **调用过 ≠ 内容对**。
//
//  ⚠️ 与任务**共用同一个 `ConditionTracker`** 是设计（`Docs\20` §四），
//     所以"任务的条件变化不该让成就动"必须是显式的**阴性对照**。
// ============================================================================

using System;
using System.Collections.Generic;
// ⚠️ `using System;` 之后裸写 `Object` 会变成 CS0104（`UnityEngine.Object` 与 `object` 二义）。
//    用**别名**把它钉回 UnityEngine 那一侧 —— 比在每个调用点写 `UnityEngine.Object.` 干净。
using Object = UnityEngine.Object;
using NBC.Framework;
using NBC.Game.Achievement;
using NBC.Game.Config;
using NBC.Game.Quest;
using NBC.Shared.Condition;
using NBC.Shared.Reward;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>M4-S2：成就运行时的测试。</summary>
    public sealed class AchievementRuntimeTests
    {
        /// <summary>成就表。</summary>
        private AchievementConfig m_achievements;

        /// <summary>条件表。</summary>
        private QuestConditionConfig m_conditions;

        /// <summary>奖励表。</summary>
        private RewardConfig m_rewards;

        /// <summary>进度存放处（**跨"局"共享**，用来验证"不清零"）。</summary>
        private InMemoryConditionProgressStore m_store;

        /// <summary>条件系统（**与任务共用的那一个**）。</summary>
        private ConditionTracker m_tracker;

        /// <summary>发奖实现（内存版）。</summary>
        private InMemoryQuestRewardSink m_sink;

        /// <summary>
        /// 已发奖励台账（内存版）。
        /// <para>⚠️ 用**同一个**实例跨"两次构造"传下去，就是模拟"进程重启"：
        /// 内存版在这一组测试里是够的 —— 我们要验的是**台账的判定逻辑**，
        /// 不是"台账本身能不能活过重启"（那是持久化实现的事）。</para>
        /// </summary>
        private InMemoryRewardLedger m_ledger;

        /// <summary>被测对象。</summary>
        private AchievementRuntime m_runtime;

        /// <summary>记录：收到了哪些成就事件。</summary>
        private List<string> m_received;

        // ====================================================================
        //  夹具
        // ====================================================================

        /// <summary>每个用例前：造一套干净的配置。</summary>
        [SetUp]
        public void SetUp()
        {
            EventCenter.Instance.WarnOnMissingListener = false;

            m_received = new List<string>();

            m_store = new InMemoryConditionProgressStore();
            m_tracker = new ConditionTracker(m_store);
            m_sink = new InMemoryQuestRewardSink();
            m_ledger = new InMemoryRewardLedger();

            BuildTables();

            EventCenter.Instance.AddEventListener<AchievementUnlockedPayload>(
                AchievementEvents.Unlocked, OnUnlocked);
            EventCenter.Instance.AddEventListener<int>(
                AchievementEvents.ProgressChanged, OnProgressChanged);
            EventCenter.Instance.AddEventListener<AchievementUnlockFailedPayload>(
                AchievementEvents.UnlockFailed, OnUnlockFailed);
        }

        /// <summary>每个用例后：退订、Dispose、销毁测试里造的资产。</summary>
        [TearDown]
        public void TearDown()
        {
            if (m_runtime != null)
            {
                m_runtime.Dispose();
                m_runtime = null;
            }

            EventCenter.Instance.ClearListeners(AchievementEvents.Unlocked);
            EventCenter.Instance.ClearListeners(AchievementEvents.ProgressChanged);
            EventCenter.Instance.ClearListeners(AchievementEvents.UnlockFailed);
            EventCenter.Instance.WarnOnMissingListener = true;

            Destroy(m_achievements);
            Destroy(m_conditions);
            Destroy(m_rewards);

            m_achievements = null;
            m_conditions = null;
            m_rewards = null;
        }

        /// <summary>销毁一个测试里造出来的资产。</summary>
        /// <param name="asset">资产。</param>
        private static void Destroy(Object asset)
        {
            if (asset != null)
            {
                Object.DestroyImmediate(asset);
            }
        }

        /// <summary>
        /// 造三张表：条件是 **4001（杀 6002 ×2，给任务用）** 与
        /// **4009 / 4010（给成就用，编号刻意分开 —— 这是 CFG0023 要求的）**。
        /// </summary>
        private void BuildTables()
        {
            m_conditions = ScriptableObject.CreateInstance<QuestConditionConfig>();
            m_conditions.rows = new List<Config_QuestCondition>
            {
                Condition(4001, EConditionEvent.KillMonster, 6002, 2),   // 任务用：杀 2 只毒蛛
                Condition(4009, EConditionEvent.KillMonster, 6001, 2),   // 成就用：杀 2 只野狼
                Condition(4010, EConditionEvent.KillMonster, 6001, 10)   // 成就用：杀 10 只野狼（跨局）
            };
            m_conditions.RebuildIndex();

            m_rewards = ScriptableObject.CreateInstance<RewardConfig>();
            m_rewards.rows = new List<Config_Reward>
            {
                Reward(5001, 100, 50, 7001, 2),
                Reward(5003, 300, 150, 7003, 1)
            };
            m_rewards.RebuildIndex();

            m_achievements = ScriptableObject.CreateInstance<AchievementConfig>();
            m_achievements.rows = new List<Config_Achievement>
            {
                AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001),
                AchievementRow(9002, "狼王终结者", new[] { 4010 }, 5003)
            };
            m_achievements.RebuildIndex();
        }

        /// <summary>造一行条件。</summary>
        /// <param name="id">编号。</param>
        /// <param name="eventType">事件类型。</param>
        /// <param name="targetId">目标编号。</param>
        /// <param name="required">需要数量。</param>
        /// <returns>行。</returns>
        private static Config_QuestCondition Condition(int id, EConditionEvent eventType, int targetId, int required)
        {
            Config_QuestCondition row = new Config_QuestCondition();
            row.id = id;
            row.eventType = eventType;
            row.targetId = targetId;
            row.requiredCount = required;
            row.note = "测试";
            return row;
        }

        /// <summary>造一行奖励。</summary>
        /// <param name="id">编号。</param>
        /// <param name="exp">经验。</param>
        /// <param name="gold">金币。</param>
        /// <param name="itemId">物品编号。</param>
        /// <param name="itemCount">物品数量。</param>
        /// <returns>行。</returns>
        private static Config_Reward Reward(int id, int exp, int gold, int itemId, int itemCount)
        {
            Config_Reward row = new Config_Reward();
            row.id = id;
            row.exp = exp;
            row.gold = gold;
            row.itemId = itemId;
            row.itemCount = itemCount;
            return row;
        }

        /// <summary>造一行成就。</summary>
        /// <param name="id">编号。</param>
        /// <param name="name">名称。</param>
        /// <param name="conditionIds">条件编号。</param>
        /// <param name="rewardId">奖励编号。</param>
        /// <returns>行。</returns>
        private static Config_Achievement AchievementRow(int id, string name, int[] conditionIds, int rewardId)
        {
            Config_Achievement row = new Config_Achievement();
            row.id = id;
            row.name = name;
            row.desc = name + " 的描述";
            row.conditionIds = conditionIds;
            row.rewardId = rewardId;
            return row;
        }

        /// <summary>
        /// 造一行**任务**（只在"与任务共用条件系统"那两个用例里用）。
        /// <para>⚠️ 方法名**不能**叫 `Quest` —— 会跟 `using NBC.Game.Quest;` 引进来的
        /// **命名空间名**撞，报错形状是 CS0103「当前上下文中不存在名称 Quest」，
        /// 而它**完全不提示**真实的冲突对象。闸门当场抓到的（同族：`AchievementRow` 同理）。</para>
        /// </summary>
        /// <param name="id">编号。</param>
        /// <param name="name">名称。</param>
        /// <param name="conditionIds">条件编号。</param>
        /// <param name="rewardId">奖励编号。</param>
        /// <returns>行。</returns>
        private static Config_Quest QuestRow(int id, string name, int[] conditionIds, int rewardId)
        {
            Config_Quest row = new Config_Quest();
            row.id = id;
            row.name = name;
            row.desc = name + " 的描述";
            row.conditionIds = conditionIds;
            row.rewardId = rewardId;
            return row;
        }

        /// <summary>造一个只有**指定几条**成就的被测对象（避开夹具里那两条）。</summary>
        /// <param name="rows">成就行。</param>
        /// <returns>运行时。</returns>
        private AchievementRuntime CreateRuntime(params Config_Achievement[] rows)
        {
            m_achievements.rows = new List<Config_Achievement>(rows);
            m_achievements.RebuildIndex();

            m_runtime = new AchievementRuntime(m_achievements, m_conditions, m_rewards, m_tracker, m_sink, m_ledger);
            return m_runtime;
        }

        /// <summary>记录解锁事件。</summary>
        /// <param name="payload">载荷。</param>
        private void OnUnlocked(AchievementUnlockedPayload payload)
        {
            m_received.Add("unlocked:" + payload.AchievementId + ":reward" + payload.RewardId);
        }

        /// <summary>记录进度事件（**只带成就编号** —— 断言"任务的条件不该出现在这里"用它）。</summary>
        /// <param name="achievementId">成就编号。</param>
        private void OnProgressChanged(int achievementId)
        {
            m_received.Add("progress:" + achievementId);
        }

        /// <summary>记录解锁失败。</summary>
        /// <param name="payload">载荷。</param>
        private void OnUnlockFailed(AchievementUnlockFailedPayload payload)
        {
            m_received.Add("failed:" + payload.AchievementId);
        }

        // ====================================================================
        //  ① 没有接取：构造时就登记（且"登记前已达成"要当场解锁）
        // ====================================================================

        /// <summary>
        /// **最要紧的一条**：注册时条件**已经达成** → 构造完就该解锁并发奖。
        /// <para>场景就是真事："上次已经打了 10 只狼，这次登录就该解锁"。
        /// 不这么做的话，玩家得**再打一只**才会解锁（看起来像玄学）。</para>
        /// <para>⚠️ 它同时钉住**登记顺序**：如果把"记归属"写在"登记"之后，
        /// `Register` 的当场回调会查不到归属 → 静默 `return` → **永远不解锁**。
        /// 所以这一条红了就说明顺序写反了。</para>
        /// </summary>
        [Test]
        public void AlreadyMetOnConstruction_UnlocksImmediately()
        {
            m_store.SetProgress(4009, 2);      // 假装"上次已经打了 2 只"

            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            Assert.IsTrue(m_runtime.IsUnlocked(9001), "注册时条件已达成，就该在构造完当场解锁");
            Assert.AreEqual(1, m_sink.GrantCount, "该发一次奖");
            Assert.AreEqual(100, m_sink.TotalExp, "到账的经验要**对**（不是\"发过\"就算）");
            Assert.Contains("unlocked:9001:reward5001", m_received);
        }

        /// <summary>**阴性对照**：条件没达成 → 构造完**不该**解锁（证明上一条不是"构造就解锁"）。</summary>
        [Test]
        public void NotYetMetOnConstruction_StaysLocked()
        {
            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            Assert.IsFalse(m_runtime.IsUnlocked(9001), "只打了 1 只、要求 2 只，不该解锁");
            Assert.AreEqual(0, m_sink.GrantCount, "没解锁就不该发奖");
            Assert.AreEqual(0, m_runtime.UnlockedCount);
        }

        // ====================================================================
        //  ② 进度不清零：跨局累计
        // ====================================================================

        /// <summary>
        /// **成就的定义性行为**：第一局打了 1 只、第二局再打 1 只 → 解锁。
        /// <para>⚠️ 对照：任务那边 `resetProgress = true`，第二局会**清零重算**。
        /// 同一套条件系统、同一份进度存放处，差别**只有那个 bool**。</para>
        /// </summary>
        [Test]
        public void ProgressSurvivesAcrossRuns_AndStacksUp()
        {
            // ---- 第一局：打 1 只，然后销毁运行时（相当于退出这一局）----
            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Assert.IsFalse(m_runtime.IsUnlocked(9001), "一局只打 1 只、要 2 只 —— 还不该解锁");

            m_runtime.Dispose();
            m_runtime = null;

            Assert.AreEqual(1, m_store.GetProgress(4009), "Dispose **只注销登记、不动进度**（跨局累计的前提）");
            Assert.AreEqual(0, m_tracker.RegisteredCount, "Dispose 之后不该还占着条件");

            // ---- 第二局：再打 1 只 → 累计 2 只 → 解锁 ----
            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Assert.IsTrue(m_runtime.IsUnlocked(9001), "第二局累计到 2 只，就该解锁（进度没被清零）");
            Assert.AreEqual(1, m_sink.GrantCount);
        }

        // ====================================================================
        //  ③ 没有交付：条件一齐当场解锁 + 当场发奖
        // ====================================================================

        /// <summary>多条条件：**必须全部满足**才解锁，一条满足不解锁。</summary>
        [Test]
        public void UnlocksOnlyWhenAllConditionsAreMet()
        {
            m_conditions.rows.Add(Condition(4011, EConditionEvent.KillMonster, 6003, 1));
            m_conditions.RebuildIndex();

            CreateRuntime(AchievementRow(9001, "狼群清道夫", new[] { 4009, 4011 }, 5001));

            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 2);
            Assert.IsFalse(m_runtime.IsUnlocked(9001), "只满足了 4009，还有 4011 没满足");

            m_tracker.Notify(EConditionEvent.KillMonster, 6003, 1);
            Assert.IsTrue(m_runtime.IsUnlocked(9001), "两条都满足了，就该解锁");
        }

        /// <summary>解锁时**奖励真的到账**（经验/金币/物品，全都断言数字）。</summary>
        [Test]
        public void Unlock_GrantsRewardWithRealNumbers()
        {
            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 2);

            Assert.AreEqual(1, m_sink.GrantCount, "该恰好发一次");
            Assert.AreEqual(100, m_sink.TotalExp, "经验");
            Assert.AreEqual(50, m_sink.TotalGold, "金币");
            Assert.AreEqual(2, m_sink.ItemCountOf(7001), "物品 7001 的数量");
            Assert.AreEqual(5001, m_sink.HistoryAt(0).RewardId, "发的必须是**配置表里那一行**奖励");
        }

        /// <summary>解锁**只发生一次**：之后再打多少只都不再发奖。</summary>
        [Test]
        public void Unlock_HappensOnlyOnce()
        {
            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 2);
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 5);
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 5);

            Assert.AreEqual(1, m_sink.GrantCount, "解锁只能发一次奖（进度是钳位的，成就也不能重复解锁）");
            Assert.AreEqual(1, m_runtime.UnlockedCount);
        }

        // ====================================================================
        //  ④ 与任务共用条件系统：边界必须显式钉住
        // ====================================================================

        /// <summary>
        /// **阴性对照**：任务那条条件（4001）的进度变化，**不该**广播成成就进度。
        /// <para>两边共用同一个 `ConditionTracker`，所以两边的回调**都会收到对方的条件编号**。
        /// 本类必须"认不出来就安静忽略" —— 绝不能把它当成自己的成就去广播/评定。</para>
        /// <para>⚠️ 为什么这条能证伪：真把守卫去掉，`achievementId` 就会是错的值
        /// （或者按条件编号去查成就），表现为**多出一条 `progress:` 记录**。</para>
        /// </summary>
        [Test]
        public void QuestConditionChanges_AreNotReportedAsAchievementProgress()
        {
            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            QuestConfig quests = ScriptableObject.CreateInstance<QuestConfig>();
            quests.rows = new List<Config_Quest> { QuestRow(3001, "清剿蛛群", new[] { 4001 }, 5001) };
            quests.RebuildIndex();

            QuestRuntime questRuntime = new QuestRuntime(quests, m_conditions, m_rewards, m_tracker, m_sink);

            try
            {
                questRuntime.Accept(3001);

                m_tracker.Notify(EConditionEvent.KillMonster, 6002, 2);   // 只推进任务的条件 4001

                Assert.AreEqual(2, m_store.GetProgress(4001), "任务的条件该涨到 2");
                Assert.AreEqual(0, m_store.GetProgress(4009), "成就的条件**一点都不该动**");
                Assert.IsFalse(m_runtime.IsUnlocked(9001), "任务完成不该让成就解锁");

                // **本用例的判据**：成就一条 progress 都不该发（成就自己那条一次都没涨）
                for (int i = 0; i < m_received.Count; i++)
                {
                    Assert.IsFalse(m_received[i].StartsWith("progress:"),
                        "任务的条件变化被当成成就进度广播了：" + m_received[i]);
                }
            }
            finally
            {
                questRuntime.Dispose();
                Object.DestroyImmediate(quests);
            }
        }

        /// <summary>**同一个事件同时命中两边**：两边各自算各自的，互不干扰。</summary>
        [Test]
        public void OneKill_AdvancesBothQuestAndAchievement_Independently()
        {
            m_conditions.rows.Add(Condition(4002, EConditionEvent.KillMonster, 6001, 2));
            m_conditions.RebuildIndex();

            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            QuestConfig quests = ScriptableObject.CreateInstance<QuestConfig>();
            quests.rows = new List<Config_Quest> { QuestRow(3001, "清剿狼群", new[] { 4002 }, 5001) };
            quests.RebuildIndex();

            QuestRuntime questRuntime = new QuestRuntime(quests, m_conditions, m_rewards, m_tracker, m_sink);

            try
            {
                questRuntime.Accept(3001);

                m_tracker.Notify(EConditionEvent.KillMonster, 6001, 2);

                Assert.AreEqual(2, m_store.GetProgress(4009), "成就的条件 4009 该涨到 2");
                Assert.AreEqual(2, m_store.GetProgress(4002), "任务的条件 4002 也该涨到 2");
                Assert.IsTrue(m_runtime.IsUnlocked(9001), "成就在条件一齐时当场解锁");
            }
            finally
            {
                questRuntime.Dispose();
                Object.DestroyImmediate(quests);
            }
        }

        // ====================================================================
        //  ⑤ 配置错误：抛异常，且**点名是谁跟谁**
        // ====================================================================

        /// <summary>两个成就抢同一条条件 → 构造时抛，消息里点名**两个成就**。</summary>
        [Test]
        public void TwoAchievementsSharingCondition_ThrowsWithBothNames()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                CreateRuntime(
                    AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001),
                    AchievementRow(9002, "猎手的直觉", new[] { 4009 }, 5003)));

            StringAssert.Contains("4009", error.Message);
            StringAssert.Contains("9001", error.Message);
            StringAssert.Contains("9002", error.Message);
            StringAssert.Contains("CFG0023", error.Message);
        }

        /// <summary>条件在条件表里找不到 → 构造时抛（不是等玩家打到时才炸）。</summary>
        [Test]
        public void MissingConditionRow_Throws()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                CreateRuntime(AchievementRow(9001, "坏配置", new[] { 9999 }, 5001)));

            StringAssert.Contains("9999", error.Message);
            StringAssert.Contains("找不到", error.Message);
        }

        /// <summary>奖励在奖励表里找不到 → 构造时抛（**避免出现"成就亮了奖励没到"**）。</summary>
        [Test]
        public void MissingRewardRow_Throws()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                CreateRuntime(AchievementRow(9001, "坏配置", new[] { 4009 }, 5999)));

            StringAssert.Contains("5999", error.Message);
            StringAssert.Contains("Reward", error.Message);
        }

        /// <summary>一条条件都没有的成就 → 构造时抛（这种成就永远解不开）。</summary>
        [Test]
        public void AchievementWithoutConditions_Throws()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                CreateRuntime(AchievementRow(9001, "空条件", new int[0], 5001)));

            StringAssert.Contains("一条条件都没有", error.Message);
        }

        // ====================================================================
        //  ⑤之二 台账：**重启之后不再重复发奖**（M4-S3 的核心）
        // ====================================================================

        /// <summary>
        /// **先证明 bug 是真的**：不带台账（每次都给一个新台账 = 每次都像"全新进程"），
        /// 同一个"已达成"的进度会让成就**重复发奖** —— 这正是"进度一旦落库、
        /// 服务端每重启一次就再发一遍奖"那条（`Docs\\27` §11.5）。
        /// <para>⚠️ 这条不是可有可无的：**如果它不红，说明 bug 不存在，</para>
        /// 那下面那条"带台账就不重复发"的用例就是**假绿**。
        /// 先有阳性对照，阴性对照才算数（本项目的规矩）。</para>
        /// </summary>
        [Test]
        public void WithoutLedger_AlreadyMetProgress_GrantsAgainOnEveryStartup()
        {
            // 模拟"上次已经打够了"
            m_store.SetProgress(4009, 2);

            // 第 1 次启动
            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));
            Assert.AreEqual(1, m_sink.GrantCount, "第一次启动该发一次奖");
            m_runtime.Dispose();

            // 第 2 次启动：**换一个全新的内存台账**（模拟"没接持久化台账"）
            m_ledger = new InMemoryRewardLedger();
            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            Assert.AreEqual(2, m_sink.GrantCount,
                "⚠️ 没有持久化台账时，重启会**再发一次** —— 这就是本片要修的那个 bug（阳性对照）");
            Assert.IsTrue(m_runtime.IsUnlocked(9001), "并且它确实是解锁状态");
        }

        /// <summary>
        /// **修好了**：台账说"发过了" → 重启后**点亮但不重发**。
        /// <para>⚠️ 判据是"生成状态对 + 奖励不重复"，两条都要：
        /// 只看"没重复发"会漏掉"成就显示成没解锁"（那是另一种错）。</para>
        /// </summary>
        [Test]
        public void WithLedger_AlreadyMetProgress_MarksUnlockedButDoesNotPayAgain()
        {
            m_store.SetProgress(4009, 2);

            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));
            Assert.AreEqual(1, m_sink.GrantCount, "第一次启动该发一次奖");
            m_runtime.Dispose();

            // 第 2 次启动：**台账还是同一个**（模拟"从库里读回了台账"）
            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            Assert.AreEqual(1, m_sink.GrantCount, "有台账时**不该**再发一次");
            Assert.IsTrue(m_runtime.IsUnlocked(9001), "但状态必须仍然是**已解锁**（玩家看到的是对的）");
        }

        /// <summary>
        /// **恢复状态 ≠ 发生事件**：重启时不能重放"刚解锁"的庆祝信号。
        /// <para>否则玩家每次上线都看到一次"🏆 成就解锁"。</para>
        /// </summary>
        [Test]
        public void WithLedger_RestoredUnlock_DoesNotReplayUnlockEvent()
        {
            m_store.SetProgress(4009, 2);

            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));
            m_received.Clear();     // 只看"第二次启动"期间发生了什么
            m_runtime.Dispose();

            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            for (int i = 0; i < m_received.Count; i++)
            {
                Assert.IsFalse(m_received[i].StartsWith("unlocked:"),
                    "重启时重放了「刚解锁」事件：" + m_received[i]);
            }

            Assert.IsTrue(m_runtime.IsUnlocked(9001), "状态还是要恢复成已解锁");
        }

        /// <summary>
        /// **崩溃在中间能自愈**：进度说"已达成"、台账说"没发过" → **应当补发**。
        /// <para>这条是台账的第二个好处（也是它比"按次数记账"更好的地方）：
        /// "进度落库"与"发奖"是两步，中间崩了不会**少给**玩家。</para>
        /// </summary>
        [Test]
        public void WithLedger_MetButNeverGranted_PaysOnRecovery()
        {
            // 进度已达成，但台账里**没有**记录（模拟"上次落完进度就崩了，奖还没发"）
            m_store.SetProgress(4009, 2);
            Assert.IsFalse(m_ledger.HasGranted(ERewardOwnerKind.Achievement, 9001), "前置：台账里本来没有");

            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            Assert.AreEqual(1, m_sink.GrantCount, "欠玩家的那一次要补上");
            Assert.IsTrue(m_ledger.HasGranted(ERewardOwnerKind.Achievement, 9001), "补发之后要记账");
        }

        /// <summary>台账是**必填**的：传 null 要当场报错（而不是静默退回"会重复发奖"的行为）。</summary>
        [Test]
        public void NullLedger_Throws()
        {
            m_achievements.rows = new List<Config_Achievement>
            {
                AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001)
            };
            m_achievements.RebuildIndex();

            ArgumentNullException error = Assert.Throws<ArgumentNullException>(() =>
                new AchievementRuntime(m_achievements, m_conditions, m_rewards, m_tracker, m_sink, null));

            StringAssert.Contains("台账", error.Message);
        }

        /// <summary>台账**幂等**：同一个成就重复记账不会变成两条。</summary>
        [Test]
        public void Ledger_MarkGranted_IsIdempotent()
        {
            m_ledger.MarkGranted(ERewardOwnerKind.Achievement, 9001, 5001);
            m_ledger.MarkGranted(ERewardOwnerKind.Achievement, 9001, 5001);
            m_ledger.MarkGranted(ERewardOwnerKind.Achievement, 9001, 5001);

            Assert.AreEqual(1, m_ledger.Count, "重复记账只算一笔");
        }

        /// <summary>**任务与成就的台账互不干扰**（同一个编号在两个种类下是两回事）。</summary>
        [Test]
        public void Ledger_SeparatesQuestAndAchievement()
        {
            m_ledger.MarkGranted(ERewardOwnerKind.Achievement, 9001, 5001);

            Assert.IsTrue(m_ledger.HasGranted(ERewardOwnerKind.Achievement, 9001), "成就有记录");
            Assert.IsFalse(m_ledger.HasGranted(ERewardOwnerKind.Quest, 9001),
                "同编号的**任务**不该被算成发过（编号空间是分开的）");
        }

        // ====================================================================
        //  ⑥ 追踪视图（给调试面板/UI 用）
        // ====================================================================

        /// <summary>追踪视图要给出**每条条件的 `几/几`**，而不只是"完成/未完成"。</summary>
        [Test]
        public void Tracking_ShowsPerConditionProgress()
        {
            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            AchievementTracking tracking;
            Assert.IsTrue(m_runtime.TryGetTracking(9001, out tracking));
            Assert.AreEqual(9001, tracking.AchievementId);
            Assert.AreEqual("初出茅庐", tracking.Name);
            Assert.IsFalse(tracking.IsUnlocked);
            Assert.AreEqual(1, tracking.Conditions.Count, "该有 1 条条件行");
            Assert.AreEqual(1, tracking.Conditions[0].Current, "中间那格数字要**活着**（1/2，不是只有完成/未完成）");
            Assert.AreEqual(2, tracking.Conditions[0].Required);
            Assert.IsFalse(tracking.Conditions[0].IsMet);
            Assert.IsFalse(tracking.AllMet);
        }

        /// <summary>配置表里没有这个成就时 `TryGetTracking` 返回 false（不抛）。</summary>
        [Test]
        public void Tracking_UnknownId_ReturnsFalse()
        {
            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            AchievementTracking tracking;
            Assert.IsFalse(m_runtime.TryGetTracking(8888, out tracking));
            Assert.IsNull(tracking);
        }

        /// <summary>`Describe()` 要把解锁数与每条进度都写出来（调试面板直接显示它）。</summary>
        [Test]
        public void Describe_ListsEverything()
        {
            CreateRuntime(AchievementRow(9001, "初出茅庐", new[] { 4009 }, 5001));

            string text = m_runtime.Describe();

            StringAssert.Contains("9001", text);
            StringAssert.Contains("初出茅庐", text);
            StringAssert.Contains("0/2", text);
            StringAssert.Contains("未解锁", text);
        }
    }
}
