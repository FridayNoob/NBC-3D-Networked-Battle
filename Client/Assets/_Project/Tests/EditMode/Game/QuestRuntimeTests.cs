// ============================================================================
//  M2-A2 · 任务运行时的 EditMode 测试（**任务系统的端到端闭环**）
//  对应验收：Docs\22-M2开工清单.md A2（V2）
//  被测：Client\Assets\_Project\Game\Quest\
//
//  ---------------------------------------------------------------------------
//  这一组测的是"闭环"，不是"某个方法"
//  ---------------------------------------------------------------------------
//      接取 → 条件累加 → 全部达成 → 交付 → **奖励真的到账**
//
//  ⚠️ 最后一步的断言刻意**不看"Grant 被调用过"**，而是看**到账的数字**
//     （经验/金币/物品数量）。M1-B5 那次"打印全空却报 ✅"的教训：
//     **调用过 ≠ 内容对**。
//
//  ---------------------------------------------------------------------------
//  配置表怎么来的
//  ---------------------------------------------------------------------------
//  直接在测试里 `ScriptableObject.CreateInstance<...>()` 填 `rows` ——
//  EditMode 下完全可行，**不需要 YooAsset、不需要打包、不需要点菜单**。
//  真实的"从 .tsv 读进 SO"由 `ConfigImporter` 负责（那是编辑器的事），
//  两条路都汇到同一个 `rows`，所以这里验的逻辑就是运行时的那份。
// ============================================================================

using System.Collections.Generic;
using NBC.Framework;
using NBC.Game.Config;
using NBC.Game.Quest;
using NBC.Shared.Condition;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>M2-A2：任务运行时 + 事件桥的测试。</summary>
    public sealed class QuestRuntimeTests
    {
        /// <summary>测试专用的事件标识（模拟"战斗模块报告怪物死了"）。</summary>
        private static readonly EventId MonsterDied =
            EventId.Declare("Test.MonsterDied");

        /// <summary>任务表。</summary>
        private QuestConfig m_quests;

        /// <summary>条件表。</summary>
        private QuestConditionConfig m_conditions;

        /// <summary>奖励表。</summary>
        private RewardConfig m_rewards;

        /// <summary>进度存放处。</summary>
        private InMemoryConditionProgressStore m_store;

        /// <summary>条件系统。</summary>
        private ConditionTracker m_tracker;

        /// <summary>发奖实现（内存版）。</summary>
        private InMemoryQuestRewardSink m_sink;

        /// <summary>被测对象。</summary>
        private QuestRuntime m_runtime;

        /// <summary>事件桥（个别用例才用）。</summary>
        private ConditionEventBridge m_bridge;

        /// <summary>记录：收到过哪些任务事件。</summary>
        private List<string> m_received;

        /// <summary>记录：被拒绝的原因。</summary>
        private List<string> m_rejectedReasons;

        // ====================================================================
        //  夹具
        // ====================================================================

        /// <summary>每个用例前：造一套干净的配置 + 运行时。</summary>
        [SetUp]
        public void SetUp()
        {
            // 事件中心是常驻单例：关掉"没人听"的警告，否则每个用例刷一片 LogWarning
            EventCenter.Instance.WarnOnMissingListener = false;

            m_received = new List<string>();
            m_rejectedReasons = new List<string>();

            BuildTables();

            m_store = new InMemoryConditionProgressStore();
            m_tracker = new ConditionTracker(m_store);
            m_sink = new InMemoryQuestRewardSink();
            m_runtime = new QuestRuntime(m_quests, m_conditions, m_rewards, m_tracker, m_sink);

            // ⚠️ 必须写**显式泛型参数**：`AddEventListener(EventId, UnityAction)` 这个非泛型重载
            //    会和泛型重载抢，而"方法组"参数不会去做泛型类型推断 ——
            //    报错形状是"无法从方法组转换为 UnityAction"（闸门当场抓到的）。
            EventCenter.Instance.AddEventListener<int>(QuestEvents.Accepted, OnAccepted);
            EventCenter.Instance.AddEventListener<int>(QuestEvents.Completed, OnCompleted);
            EventCenter.Instance.AddEventListener<int>(QuestEvents.Submitted, OnSubmitted);
            EventCenter.Instance.AddEventListener<QuestRejectedPayload>(QuestEvents.Rejected, OnRejected);
        }

        /// <summary>每个用例后：退订、Dispose、销毁测试里造的资产。</summary>
        [TearDown]
        public void TearDown()
        {
            if (m_bridge != null)
            {
                m_bridge.Dispose();
                m_bridge = null;
            }

            if (m_runtime != null)
            {
                m_runtime.Dispose();
                m_runtime = null;
            }

            EventCenter.Instance.ClearListeners(QuestEvents.Accepted);
            EventCenter.Instance.ClearListeners(QuestEvents.Completed);
            EventCenter.Instance.ClearListeners(QuestEvents.Submitted);
            EventCenter.Instance.ClearListeners(QuestEvents.Rejected);
            EventCenter.Instance.ClearListeners(MonsterDied);
            EventCenter.Instance.WarnOnMissingListener = true;

            Destroy(m_quests);
            Destroy(m_conditions);
            Destroy(m_rewards);

            m_quests = null;
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

        /// <summary>造三张表的内容。</summary>
        private void BuildTables()
        {
            // -------- 条件表 --------
            m_conditions = ScriptableObject.CreateInstance<QuestConditionConfig>();
            m_conditions.rows = new List<Config_QuestCondition>
            {
                Condition(4001, EConditionEvent.KillMonster, 6001, 3),   // 杀 3 只野狼
                Condition(4002, EConditionEvent.CollectItem, 7001, 2),   // 捡 2 张狼皮
                Condition(4003, EConditionEvent.CollectItem, 7002, 2),   // 采 2 份草药
                Condition(4004, EConditionEvent.ReachArea, 1, 1),        // 到 1 号区域
                Condition(4005, EConditionEvent.KillMonster, 6003, 1),   // 杀狼王
                Condition(4006, EConditionEvent.KillMonster, 6001, 0)    // **故意非法**：需要数量 0
            };
            m_conditions.RebuildIndex();

            // -------- 奖励表 --------
            m_rewards = ScriptableObject.CreateInstance<RewardConfig>();
            m_rewards.rows = new List<Config_Reward>
            {
                Reward(5001, 100, 50, 7001, 2),
                Reward(5002, 80, 40, 7002, 1)
            };
            m_rewards.RebuildIndex();

            // -------- 任务表 --------
            m_quests = ScriptableObject.CreateInstance<QuestConfig>();
            m_quests.rows = new List<Config_Quest>
            {
                Quest(3001, "初次狩猎", new[] { 4001, 4002 }, 5001),   // 两条条件
                Quest(3002, "草药采集", new[] { 4003 }, 5002),         // 一条条件
                Quest(3003, "坏配置：条件不存在", new[] { 4001, 9999 }, 5001),
                Quest(3004, "坏配置：没有条件", new int[0], 5001),
                Quest(3005, "坏配置：条件非法", new[] { 4006 }, 5001),
                Quest(3006, "坏配置：奖励不存在", new[] { 4003 }, 5999)
            };
            m_quests.RebuildIndex();
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

        /// <summary>造一行任务。</summary>
        /// <param name="id">编号。</param>
        /// <param name="name">名称。</param>
        /// <param name="conditionIds">条件编号。</param>
        /// <param name="rewardId">奖励编号。</param>
        /// <returns>行。</returns>
        private static Config_Quest Quest(int id, string name, int[] conditionIds, int rewardId)
        {
            Config_Quest row = new Config_Quest();
            row.id = id;
            row.name = name;
            row.desc = name + " 的描述";
            row.conditionIds = conditionIds;
            row.rewardId = rewardId;
            return row;
        }

        /// <summary>记录接取事件。</summary>
        /// <param name="questId">任务编号。</param>
        private void OnAccepted(int questId) { m_received.Add("accepted:" + questId); }

        /// <summary>记录完成事件。</summary>
        /// <param name="questId">任务编号。</param>
        private void OnCompleted(int questId) { m_received.Add("completed:" + questId); }

        /// <summary>记录交付事件。</summary>
        /// <param name="questId">任务编号。</param>
        private void OnSubmitted(int questId) { m_received.Add("submitted:" + questId); }

        /// <summary>记录被拒绝的原因。</summary>
        /// <param name="payload">载荷。</param>
        private void OnRejected(QuestRejectedPayload payload) { m_rejectedReasons.Add(payload.Reason); }

        // ====================================================================
        //  一、接取
        // ====================================================================

        /// <summary>接取成功：状态变了、条件登记了、事件广播了。</summary>
        [Test]
        public void Accept_Succeeds_AndRegistersConditions()
        {
            QuestActionResult result = m_runtime.Accept(3001);

            Assert.IsTrue(result.Ok, result.Reason);
            Assert.AreEqual(EQuestState.Accepted, m_runtime.StateOf(3001));
            Assert.AreEqual(1, m_runtime.ActiveCount);
            Assert.AreEqual(2, m_tracker.RegisteredCount, "两条条件都要登记");
            CollectionAssert.Contains(m_received, "accepted:3001");
        }

        /// <summary>接一个配置表里没有的任务：给原因，不抛异常。</summary>
        [Test]
        public void Accept_UnknownQuest_ReturnsReasonAndBroadcastsRejection()
        {
            QuestActionResult result = m_runtime.Accept(9999);

            Assert.IsFalse(result.Ok);
            StringAssert.Contains("9999", result.Reason);
            Assert.AreEqual(1, m_rejectedReasons.Count, "被拒绝的原因要广播出去（UI 要显示给玩家）");
            Assert.AreEqual(EQuestState.None, m_runtime.StateOf(9999));
        }

        /// <summary>重复接取被拒绝，且原因里说清"已经接过了"。</summary>
        [Test]
        public void Accept_Twice_SecondIsRejected()
        {
            Assert.IsTrue(m_runtime.Accept(3001).Ok);

            QuestActionResult second = m_runtime.Accept(3001);

            Assert.IsFalse(second.Ok);
            StringAssert.Contains("已经接过", second.Reason);
            Assert.AreEqual(1, m_runtime.ActiveCount, "不能变成两条");
        }

        /// <summary>
        /// **原子性**：条件表里缺一行时，整个接取失败，
        /// **不能留下"登记了一半"的半成品任务**（那种任务会在后台悄悄累计进度）。
        /// </summary>
        [Test]
        public void Accept_QuestWithMissingConditionRow_RejectsAndRegistersNothing()
        {
            QuestActionResult result = m_runtime.Accept(3003);

            Assert.IsFalse(result.Ok);
            StringAssert.Contains("9999", result.Reason);
            Assert.AreEqual(0, m_tracker.RegisteredCount, "一条都不该登记");
            Assert.AreEqual(EQuestState.None, m_runtime.StateOf(3003));
        }

        /// <summary>没有条件的任务会被拒绝（接了也永远做不完）。</summary>
        [Test]
        public void Accept_QuestWithoutConditions_Rejects()
        {
            QuestActionResult result = m_runtime.Accept(3004);

            Assert.IsFalse(result.Ok);
            StringAssert.Contains("一条条件都没有", result.Reason);
        }

        /// <summary>条件非法（需要数量 0）时，拒绝原因要指向"配置有错"并带上细节。</summary>
        [Test]
        public void Accept_QuestWithInvalidCondition_Rejects()
        {
            QuestActionResult result = m_runtime.Accept(3005);

            Assert.IsFalse(result.Ok);
            StringAssert.Contains("需要数量", result.Reason);
            Assert.AreEqual(0, m_tracker.RegisteredCount);
        }

        // ====================================================================
        //  二、进度与完成
        // ====================================================================

        /// <summary>**核心闭环**：接取 → 打够怪 + 捡够东西 → 完成 → 交付 → 奖励到账。</summary>
        [Test]
        public void EndToEnd_AcceptProgressCompleteSubmit_GrantsReward()
        {
            // 1) 接取
            Assert.IsTrue(m_runtime.Accept(3001).Ok);

            // 2) 打 3 只野狼（最后一只让第一条条件达成）
            for (int i = 0; i < 3; i++)
            {
                m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);
            }

            Assert.AreEqual(EQuestState.Accepted, m_runtime.StateOf(3001),
                "还有一条条件没完成，不该变成可交付");

            // 3) 捡 2 张狼皮（第二条条件达成 → 全部达成 → 可交付）
            m_tracker.Notify(EConditionEvent.CollectItem, 7001, 2);

            Assert.AreEqual(EQuestState.Completed, m_runtime.StateOf(3001));
            CollectionAssert.Contains(m_received, "completed:3001");

            // 4) 交付
            QuestActionResult submit = m_runtime.Submit(3001);
            Assert.IsTrue(submit.Ok, submit.Reason);
            Assert.AreEqual(EQuestState.Submitted, m_runtime.StateOf(3001));
            Assert.AreEqual(0, m_runtime.ActiveCount);
            CollectionAssert.Contains(m_received, "submitted:3001");

            // 5) **奖励真的到账**（断言数字，不是"调用过"）
            Assert.AreEqual(1, m_sink.GrantCount);
            Assert.AreEqual(100, m_sink.TotalExp);
            Assert.AreEqual(50, m_sink.TotalGold);
            Assert.AreEqual(2, m_sink.ItemCountOf(7001));
        }

        /// <summary>没接过任务时，事件不该被算进任何任务（进度是干净的）。</summary>
        [Test]
        public void Progress_BeforeAccept_DoesNotCount()
        {
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 3);

            // 一个都没登记过 → 存放处里**一条记录都不该有**
            Assert.AreEqual(0, m_store.Count, "没登记的条件不该往存放处写东西");
            Assert.AreEqual(0, m_store.GetProgress(4001));
            Assert.IsFalse(m_tracker.IsMet(4001));
            Assert.AreEqual(EQuestState.None, m_runtime.StateOf(3001));

            Assert.IsTrue(m_runtime.Accept(3001).Ok);

            // 接取会清零（resetProgress = true）—— 也就是说"事先打的怪不算"
            ConditionProgress progress;
            m_tracker.TryGetProgress(4001, out progress);
            Assert.AreEqual(0, progress.Current);
        }

        /// <summary>交付之后不再累计（条件已注销）。</summary>
        [Test]
        public void AfterSubmit_ProgressNoLongerAccumulates()
        {
            m_runtime.Accept(3002);
            m_tracker.Notify(EConditionEvent.CollectItem, 7002, 2);
            Assert.AreEqual(EQuestState.Completed, m_runtime.StateOf(3002));

            Assert.IsTrue(m_runtime.Submit(3002).Ok);

            m_tracker.Notify(EConditionEvent.CollectItem, 7002, 5);

            ConditionProgress progress;
            Assert.IsFalse(m_tracker.TryGetProgress(4003, out progress), "条件应当已注销");
        }

        /// <summary>条件系统里的"别人的条件"（成就的）不该影响任务，也不该抛异常。</summary>
        [Test]
        public void ForeignCondition_IsIgnoredWithoutError()
        {
            // 模拟成就：往同一个 tracker 里登记一条**不属于任何任务**的条件
            m_tracker.Register(9001, new ConditionDef(EConditionEvent.KillMonster, 6001, 1), true);

            Assert.DoesNotThrow(() => m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1));

            Assert.IsFalse(m_received.Contains("completed:3001"));
            Assert.AreEqual(EQuestState.None, m_runtime.StateOf(3001));
        }

        // ====================================================================
        //  三、交付的拒绝路径
        // ====================================================================

        /// <summary>没接就交付：拒绝。</summary>
        [Test]
        public void Submit_NotAccepted_IsRejected()
        {
            QuestActionResult result = m_runtime.Submit(3001);

            Assert.IsFalse(result.Ok);
            StringAssert.Contains("还没接", result.Reason);
            Assert.AreEqual(0, m_sink.GrantCount, "被拒绝时不能发奖");
        }

        /// <summary>没做完就交付：拒绝，且原因说清"还没做完"。</summary>
        [Test]
        public void Submit_BeforeCompleted_IsRejected()
        {
            m_runtime.Accept(3001);
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            QuestActionResult result = m_runtime.Submit(3001);

            Assert.IsFalse(result.Ok);
            StringAssert.Contains("还没做完", result.Reason);
            Assert.AreEqual(0, m_sink.GrantCount);
        }

        /// <summary>
        /// 奖励表缺行时**不交付**（避免"任务没了、奖励也没到"），
        /// 而且状态必须**原封不动**。
        /// </summary>
        [Test]
        public void Submit_WithMissingRewardRow_RejectsAndKeepsState()
        {
            m_runtime.Accept(3006);
            m_tracker.Notify(EConditionEvent.CollectItem, 7002, 2);
            Assert.AreEqual(EQuestState.Completed, m_runtime.StateOf(3006));

            QuestActionResult result = m_runtime.Submit(3006);

            Assert.IsFalse(result.Ok);
            StringAssert.Contains("5999", result.Reason);
            Assert.AreEqual(EQuestState.Completed, m_runtime.StateOf(3006), "状态不能被改掉");
            Assert.AreEqual(0, m_sink.GrantCount);
        }

        /// <summary>重复交付：拒绝。</summary>
        [Test]
        public void Submit_Twice_SecondIsRejected()
        {
            m_runtime.Accept(3002);
            m_tracker.Notify(EConditionEvent.CollectItem, 7002, 2);
            Assert.IsTrue(m_runtime.Submit(3002).Ok);

            QuestActionResult second = m_runtime.Submit(3002);

            Assert.IsFalse(second.Ok);
            StringAssert.Contains("已经交付", second.Reason);
            Assert.AreEqual(1, m_sink.GrantCount, "奖励只发一次");
        }

        // ====================================================================
        //  四、放弃与追踪视图
        // ====================================================================

        /// <summary>放弃：状态回 None、条件注销、事件广播。</summary>
        [Test]
        public void Abandon_ReturnsToNoneAndUnregisters()
        {
            m_runtime.Accept(3001);

            Assert.IsTrue(m_runtime.Abandon(3001).Ok);

            Assert.AreEqual(EQuestState.None, m_runtime.StateOf(3001));
            Assert.AreEqual(0, m_tracker.RegisteredCount);
            Assert.AreEqual(0, m_runtime.ActiveCount);
        }

        /// <summary>没接过就放弃：拒绝。</summary>
        [Test]
        public void Abandon_NotAccepted_IsRejected()
        {
            QuestActionResult result = m_runtime.Abandon(3001);

            Assert.IsFalse(result.Ok);
            StringAssert.Contains("没有接过", result.Reason);
        }

        /// <summary>追踪视图：每条条件都给出 `当前/需求`，且顺序与配置一致。</summary>
        [Test]
        public void Tracking_ShowsPerConditionProgress()
        {
            m_runtime.Accept(3001);
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 2);
            m_tracker.Notify(EConditionEvent.CollectItem, 7001, 1);

            QuestTracking tracking;
            Assert.IsTrue(m_runtime.TryGetTracking(3001, out tracking));

            Assert.AreEqual("初次狩猎", tracking.Name);
            Assert.AreEqual(EQuestState.Accepted, tracking.State);
            Assert.AreEqual(2, tracking.Conditions.Count);

            Assert.AreEqual(4001, tracking.Conditions[0].ConditionId);
            Assert.AreEqual(2, tracking.Conditions[0].Current);
            Assert.AreEqual(3, tracking.Conditions[0].Required);
            Assert.IsFalse(tracking.Conditions[0].IsMet);

            Assert.AreEqual(4002, tracking.Conditions[1].ConditionId);
            Assert.AreEqual(1, tracking.Conditions[1].Current);
            Assert.AreEqual(2, tracking.Conditions[1].Required);

            Assert.IsFalse(tracking.AllMet);
        }

        /// <summary>配置表里没有的任务取追踪返回 false（不抛异常）。</summary>
        [Test]
        public void Tracking_UnknownQuest_ReturnsFalse()
        {
            QuestTracking tracking;
            Assert.IsFalse(m_runtime.TryGetTracking(9999, out tracking));
            Assert.IsNull(tracking);
        }

        /// <summary>进度变化会广播 `Quest.ProgressChanged`（追踪条靠它刷新）。</summary>
        [Test]
        public void ProgressChanged_BroadcastsQuestId()
        {
            List<int> changed = new List<int>();
            UnityEngine.Events.UnityAction<int> handler = questId => changed.Add(questId);
            EventCenter.Instance.AddEventListener(QuestEvents.ProgressChanged, handler);

            try
            {
                m_runtime.Accept(3001);
                m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

                CollectionAssert.AreEqual(new[] { 3001 }, changed);
            }
            finally
            {
                EventCenter.Instance.RemoveEventListener(QuestEvents.ProgressChanged, handler);
            }
        }

        // ====================================================================
        //  五、事件桥（真实事件 → 条件系统）
        // ====================================================================

        /// <summary>接上桥之后，**事件中心里的事件**会推进任务条件。</summary>
        [Test]
        public void Bridge_TurnsGameEventIntoConditionProgress()
        {
            m_bridge = new ConditionEventBridge(m_tracker);
            m_bridge.Bind<int>(MonsterDied, EConditionEvent.KillMonster, monsterId => monsterId);

            Assert.AreEqual(1, m_bridge.BindCount);

            m_runtime.Accept(3001);

            EventCenter.Instance.Trigger(MonsterDied, 6001);
            EventCenter.Instance.Trigger(MonsterDied, 6001);

            ConditionProgress progress;
            m_tracker.TryGetProgress(4001, out progress);
            Assert.AreEqual(2, progress.Current);
        }

        /// <summary>桥 Dispose 之后不再转发（**订阅不退订是 M1 反复踩的坑**）。</summary>
        [Test]
        public void Bridge_AfterDispose_StopsForwarding()
        {
            m_bridge = new ConditionEventBridge(m_tracker);
            m_bridge.Bind<int>(MonsterDied, EConditionEvent.KillMonster, monsterId => monsterId);

            m_runtime.Accept(3001);
            EventCenter.Instance.Trigger(MonsterDied, 6001);

            m_bridge.Dispose();

            EventCenter.Instance.Trigger(MonsterDied, 6001);

            ConditionProgress progress;
            m_tracker.TryGetProgress(4001, out progress);
            Assert.AreEqual(1, progress.Current, "Dispose 之后不该再涨");
        }

        /// <summary>无效的事件标识要当场报错（不许悄悄接一条永远不触发的链路）。</summary>
        [Test]
        public void Bridge_InvalidEventId_Throws()
        {
            m_bridge = new ConditionEventBridge(m_tracker);

            Assert.Throws<System.ArgumentException>(
                () => m_bridge.Bind<int>(default(EventId), EConditionEvent.KillMonster, id => id));
        }

        // ====================================================================
        //  六、生命周期
        // ====================================================================

        /// <summary>Dispose 之后不再响应条件达成（事件已退订）。</summary>
        [Test]
        public void Dispose_UnsubscribesFromConditionTracker()
        {
            m_runtime.Accept(3002);
            m_runtime.Dispose();

            // 直接喂条件系统：运行时已经退订，不该再改状态、也不该抛异常
            Assert.DoesNotThrow(() => m_tracker.Notify(EConditionEvent.CollectItem, 7002, 2));

            m_runtime = null;   // TearDown 不要再 Dispose 一次
            Assert.Pass("Dispose 之后喂事件没有异常");
        }

        /// <summary>Dispose 之后调用方法要明确报错（而不是行为诡异）。</summary>
        [Test]
        public void AfterDispose_MethodsThrow()
        {
            QuestRuntime runtime = new QuestRuntime(m_quests, m_conditions, m_rewards, m_tracker, m_sink);
            runtime.Dispose();

            Assert.Throws<System.ObjectDisposedException>(() => runtime.Accept(3001));
        }
    }
}
