// ============================================================================
//  QuestFollowTests —— §二十八「本地状态机跟随权威」在 Unity 那一层的守门人
//  项目：3D联网战斗Demo   对应：`Docs\27` §28
//
//  ---------------------------------------------------------------------------
//  为什么要有这一组（纯逻辑层已经有【二十】8 条了）
//  ---------------------------------------------------------------------------
//  `QuestStateReconciler` 是引擎无关的纯决策，探针能跑能变异 —— 那一层已经有人守。
//  但**真正改本地那一下**（`QuestRuntime.FollowState` + `RuntimeQuestFollowSink`）只有 Unity 能跑，
//  而它恰好是三个**静默**错误最容易钻进来的地方：
//      ① 把「服务端状态」当成本地玩家动作（发奖、提示、重置进度）；
//      ② 不幂等（同一份同步施加两次 ⇒ "接取两次""进度被清两次"，**不报错**）；
//      ③ 回退时把界面搞得「刚点完就弹回去」（请求在飞时不许回退）。
//  ⇒ 每一条都在这组用例里钉住，并且**用阳性对照**（先证明真的改了状态，再证明没多改别的东西）。
// ============================================================================

using System.Collections.Generic;
using NBC.Framework;               // EventCenter / EventId
using NBC.Game.Quest;              // QuestRuntime / EQuestState / QuestEvents / Reconciler
using NBC.Shared.Condition;        // ConditionTracker / ConditionDef / ConditionProgress
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>§二十八：本地跟随权威 —— 只改状态、幂等、能回退、离线不变。</summary>
    public class QuestFollowTests
    {
        /// <summary>本地任务运行时（被测对象所在的那一层）。</summary>
        private QuestRuntime m_runtime;

        /// <summary>本地条件判定器（用于「不许清进度」那条断言）。</summary>
        private ConditionTracker m_tracker;

        /// <summary>本地奖励出口（用于「不许发奖」那条断言）。</summary>
        private InMemoryQuestRewardSink m_rewards;

        /// <summary>收到过几次「接取成功」事件。</summary>
        private int m_acceptedEvents;

        /// <summary>收到过几次「被拒绝」事件。</summary>
        private int m_rejectedEvents;

        /// <summary>每个用例前造一套干净的。</summary>
        [SetUp]
        public void SetUp()
        {
            EventCenter.Instance.WarnOnMissingListener = false;

            m_acceptedEvents = 0;
            m_rejectedEvents = 0;

            EventCenter.Instance.AddEventListener<int>(QuestEvents.Accepted, OnAccepted);
            EventCenter.Instance.AddEventListener<QuestRejectedPayload>(QuestEvents.Rejected, OnRejected);

            m_tracker = new ConditionTracker(new InMemoryConditionProgressStore());
            m_rewards = new InMemoryQuestRewardSink();

            m_runtime = new QuestRuntime(GameTestTables.CreateQuests(), GameTestTables.CreateQuestConditions(),
                                         GameTestTables.CreateRewards(), m_tracker, m_rewards);
        }

        /// <summary>每个用例后清理（事件监听一定要摘，否则会串到别的用例）。</summary>
        [TearDown]
        public void TearDown()
        {
            EventCenter.Instance.ClearListeners(QuestEvents.Accepted);
            EventCenter.Instance.ClearListeners(QuestEvents.Rejected);

            EventCenter.Instance.WarnOnMissingListener = true;

            if (m_runtime != null)
            {
                m_runtime.Dispose();
                m_runtime = null;
            }
        }

        /// <summary>
        /// **完整那条路**：服务端说「已接取」⇒ 走 `Plan` + `Apply` + `RuntimeQuestFollowSink`
        /// ⇒ 本地状态真的变了，而且**一个本地副作用都没有**（不发奖、不触发接取/拒绝事件）。
        /// </summary>
        [Test]
        public void ReconcilePath_AdoptsLocalState_WithoutAnyLocalSideEffect()
        {
            int questId = GameTestTables.DemoQuest;

            // 阳性对照：起点确实是「没接过」（否则下面「变了」就没有意义）
            Assert.AreEqual(EQuestState.None, m_runtime.StateOf(questId), "起点应当是没接过");

            var entries = new List<QuestSectionPlanner.Entry> { new QuestSectionPlanner.Entry(questId, 1) };

            List<QuestSyncDecision> plan = QuestStateReconciler.Plan(
                entries,
                id => (int)m_runtime.StateOf(id),
                id => false);

            Assert.AreEqual(1, plan.Count, "同步里有一条 ⇒ 应当产生一条决定");

            int applied = QuestStateReconciler.Apply(plan, new RuntimeQuestFollowSink(m_runtime));

            Assert.AreEqual(1, applied, "应当真的施加了 1 条");
            Assert.AreEqual(EQuestState.Accepted, m_runtime.StateOf(questId), "本地跟随到「已接取」");

            // ⚠️ 以下三条才是这一片的重点：跟随**不是**玩家动作
            Assert.AreEqual(0, m_acceptedEvents,
                "跟随**不许**触发「接取成功」事件 —— 那是玩家动作/服务端的事，触发了界面会显示成玩家自己接的");
            Assert.AreEqual(0, m_rejectedEvents, "也不许触发「被拒绝」事件");
            Assert.AreEqual(0, m_rewards.GrantCount, "跟随**不许**发奖");
        }

        /// <summary>
        /// **幂等**：本地已经等于权威 ⇒ 第二次必须什么都不做（返回 0），
        /// 而且**不许把进度清零**（「接取两次」的表现就是进度被清，且不报错）。
        /// </summary>
        [Test]
        public void Follow_IsIdempotent_AndDoesNotResetProgress()
        {
            int questId = GameTestTables.DemoQuest;

            Assert.AreEqual(1, m_runtime.FollowState(questId, 1), "第一次：真的改了状态");

            // 本地能看到这个任务了（条件也登记上了 —— 否则拿不到条件编号去涨进度）
            QuestTracking tracking;
            Assert.IsTrue(m_runtime.TryGetTracking(questId, out tracking), "跟随之后本地应当能看到它");
            Assert.Greater(tracking.Conditions.Count, 0, "这个任务应当有至少一条条件");

            int conditionId = tracking.Conditions[0].ConditionId;

            ConditionDef def;
            Assert.IsTrue(m_tracker.TryGetDef(conditionId, out def), "条件应当已登记（编号 " + conditionId + "）");

            m_tracker.Notify(def.EventType, def.TargetId, 1);

            ConditionProgress before;
            Assert.IsTrue(m_tracker.TryGetProgress(conditionId, out before), "涨了一次进度之后应当读得到");

            // 同一份状态再来一次 ⇒ 必须什么都不做
            int second = m_runtime.FollowState(questId, 1);

            Assert.AreEqual(0, second, "已经等于权威 ⇒ 0（幂等）");
            Assert.AreEqual(EQuestState.Accepted, m_runtime.StateOf(questId), "状态不变");

            ConditionProgress after;
            Assert.IsTrue(m_tracker.TryGetProgress(conditionId, out after));

            Assert.AreEqual(before.Current, after.Current,
                "⚠️ 重复跟随**不许**把进度清零（那正是「接取两次」的静默表现）");
        }

        /// <summary>
        /// **以服务端为准，含往回退**：服务端明确说「未接取」 ⇒ 本地回到未接取，
        /// 而且回退也**不许**产生任何玩家可见事件/奖励。
        /// </summary>
        [Test]
        public void Follow_RevertsWhenServerSaysNotAccepted()
        {
            int questId = GameTestTables.DemoQuest;

            m_runtime.FollowState(questId, 1);
            Assert.AreEqual(EQuestState.Accepted, m_runtime.StateOf(questId));

            int reverted = m_runtime.FollowState(questId, 0);

            Assert.AreEqual(1, reverted, "回退也应当真的改了状态");
            Assert.AreEqual(EQuestState.None, m_runtime.StateOf(questId), "服务端说未接取 ⇒ 本地回退");

            Assert.AreEqual(0, m_acceptedEvents, "回退**不许**触发任何玩家可见事件");
            Assert.AreEqual(0, m_rewards.GrantCount, "回退**不许**发奖");
        }

        /// <summary>
        /// **离线/单机行为不变**：没有权威（空同步）⇒ 一条决定都不产生。
        /// <para>⚠️ 这条守的是 `M2DemoBehaviour` 那条单机路径：接了权威也不许把本地玩法改掉。</para>
        /// </summary>
        [Test]
        public void Plan_EmptySync_ProducesNothing_SoOfflineBehaviourIsUnchanged()
        {
            List<QuestSyncDecision> plan = QuestStateReconciler.Plan(
                new List<QuestSectionPlanner.Entry>(),
                id => (int)m_runtime.StateOf(id),
                id => false);

            Assert.AreEqual(0, plan.Count, "服务端什么都没说 ⇒ 一条决定都不产生");
            Assert.AreEqual(EQuestState.None, m_runtime.StateOf(GameTestTables.DemoQuest), "本地状态原封不动");
        }

        /// <summary>
        /// **请求在飞时不许回退**：否则玩家刚点完「接取」、回包还没到，
        /// 一份旧的全量状态就能把它「弹回去」（看起来像点了没用）。
        /// </summary>
        [Test]
        public void Decide_PendingRequest_DoesNotRevert()
        {
            QuestSyncDecision decision = QuestStateReconciler.Decide(GameTestTables.DemoQuest, 0, 1, true);

            Assert.AreEqual(EQuestSyncAction.None, decision.Action,
                "请求在飞 ⇒ 不回退（等它回来再说）");
        }

        /// <summary>记一次「接取成功」事件。</summary>
        /// <param name="questId">任务编号（这里只数次数）。</param>
        private void OnAccepted(int questId)
        {
            m_acceptedEvents++;
        }

        /// <summary>记一次「被拒绝」事件。</summary>
        /// <param name="payload">拒绝载荷（这里只数次数）。</param>
        private void OnRejected(QuestRejectedPayload payload)
        {
            m_rejectedEvents++;
        }
    }
}
