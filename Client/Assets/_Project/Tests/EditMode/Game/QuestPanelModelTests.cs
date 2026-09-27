// ============================================================================
//  M2-D · 任务面板**视图模型**的 EditMode 测试
//  对应验收：Docs\22-M2开工清单.md D 组（V6）
//  被测：Client\Assets\_Project\Game\UI\QuestPanelModel.cs
//
//  ---------------------------------------------------------------------------
//  为什么这一组不需要预制体
//  ---------------------------------------------------------------------------
//  这正是"面板与策略分离"（`LoadingMaskPanel`/`LoadingMaskController` 那套）的回报：
//  视图模型是**纯 C#** —— 它只依赖 `QuestRuntime`，不碰 `Text`/`Button`/`Canvas`。
//  于是"显示什么、按钮叫什么、点了会怎样"全都能在这里测。
//
//  ⚠️ 本组最重要的两条断言：
//    · **按钮名契约**（`Accept_3004` / `Submit_3004`）—— 面板就靠它派发，
//      错位在这里挡住比在界面上"点 A 结果接了 B"便宜得多
//    · **失败原因要留在 `LastMessage`** —— 玩家点了没反应是最难查的一类问题
// ============================================================================

using System.Collections.Generic;
using NBC.Framework;
using NBC.Game.Config;
using NBC.Game.Quest;
using NBC.Game.UI;
using NBC.Shared.Condition;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>M2-D：任务面板视图模型的测试。</summary>
    public sealed class QuestPanelModelTests
    {
        /// <summary>任务表。</summary>
        private QuestConfig m_quests;

        /// <summary>条件表。</summary>
        private QuestConditionConfig m_conditions;

        /// <summary>奖励表。</summary>
        private RewardConfig m_rewards;

        /// <summary>条件系统。</summary>
        private ConditionTracker m_tracker;

        /// <summary>任务运行时。</summary>
        private QuestRuntime m_runtime;

        /// <summary>被测的视图模型。</summary>
        private QuestPanelModel m_model;

        /// <summary>每个用例前造一套干净的。</summary>
        [SetUp]
        public void SetUp()
        {
            EventCenter.Instance.WarnOnMissingListener = false;

            m_quests = GameTestTables.CreateQuests();
            m_conditions = GameTestTables.CreateQuestConditions();
            m_rewards = GameTestTables.CreateRewards();

            m_tracker = new ConditionTracker(new InMemoryConditionProgressStore());
            m_runtime = new QuestRuntime(m_quests, m_conditions, m_rewards,
                                        m_tracker, new InMemoryQuestRewardSink());
            m_model = new QuestPanelModel(m_runtime);
        }

        /// <summary>每个用例后清理。</summary>
        [TearDown]
        public void TearDown()
        {
            if (m_runtime != null)
            {
                m_runtime.Dispose();
                m_runtime = null;
            }

            EventCenter.Instance.WarnOnMissingListener = true;

            BattleTestTables.Destroy(m_quests);
            BattleTestTables.Destroy(m_conditions);
            BattleTestTables.Destroy(m_rewards);

            m_quests = null;
            m_conditions = null;
            m_rewards = null;
        }

        /// <summary>取一份可接行的快照。</summary>
        /// <returns>行。</returns>
        private List<QuestRow> OfferRows()
        {
            List<QuestRow> rows = new List<QuestRow>();
            m_model.CopyOfferRows(rows);
            return rows;
        }

        /// <summary>取一份已接行的快照。</summary>
        /// <returns>行。</returns>
        private List<QuestRow> TrackingRows()
        {
            List<QuestRow> rows = new List<QuestRow>();
            m_model.CopyTrackingRows(rows);
            return rows;
        }

        /// <summary>按按钮名找一行。</summary>
        /// <param name="rows">行列表。</param>
        /// <param name="actionName">按钮名。</param>
        /// <returns>行；没找到返回 null。</returns>
        private static QuestRow Find(List<QuestRow> rows, string actionName)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].ActionName == actionName)
                {
                    return rows[i];
                }
            }

            return null;
        }

        // ====================================================================
        //  一、可接列表
        // ====================================================================

        /// <summary>没接之前，两张任务都在可接列表里（顺序 = 配置表顺序）。</summary>
        [Test]
        public void Offers_ContainAllUnacceptedQuests()
        {
            List<QuestRow> rows = OfferRows();

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("Accept_3004", rows[0].ActionName, "顺序应当跟配置表一致");
            Assert.AreEqual("Accept_3002", rows[1].ActionName);
            StringAssert.Contains("清剿野狼", rows[0].Title);
            Assert.IsTrue(rows[0].ActionEnabled, "可接任务的按钮应当能按");
            Assert.AreEqual("接取", rows[0].ActionLabel);
        }

        /// <summary>接过的任务从"可接"移到"已接"。</summary>
        [Test]
        public void Offers_ExcludeAcceptedQuests()
        {
            m_model.Accept(GameTestTables.DemoQuest);

            List<QuestRow> offers = OfferRows();
            List<QuestRow> trackings = TrackingRows();

            Assert.AreEqual(1, offers.Count, "接过的那条不该还在可接列表里");
            Assert.AreEqual("Accept_3002", offers[0].ActionName);
            Assert.AreEqual(1, trackings.Count);
            Assert.AreEqual("Submit_3004", trackings[0].ActionName);
        }

        // ====================================================================
        //  二、已接列表（逐条条件进度）
        // ====================================================================

        /// <summary>详情里要能看到**逐条条件的进度**（这就是"追踪条"）。</summary>
        [Test]
        public void Tracking_DetailShowsPerConditionProgress()
        {
            m_model.Accept(GameTestTables.DemoQuest);

            // 区域条件靠"进区域"事件满足；击杀条件还差 2 只
            m_tracker.Notify(EConditionEvent.ReachArea, 1, 1);
            m_tracker.Notify(EConditionEvent.KillMonster, BattleTestTables.Wolf, 1);

            List<QuestRow> rows = TrackingRows();
            Assert.AreEqual(1, rows.Count);

            string detail = rows[0].Detail;

            StringAssert.Contains("1/3", detail, "击杀条件应当是 1/3");
            StringAssert.Contains("1/1", detail, "区域条件应当是 1/1");
            StringAssert.Contains("✅", detail, "已达成的那条要有标记");
            StringAssert.Contains("进行中", rows[0].Title);
        }

        /// <summary>条件没全满时**交付按钮不可按**（比"点了被拒绝"更友好）。</summary>
        [Test]
        public void Tracking_SubmitDisabledUntilAllConditionsMet()
        {
            m_model.Accept(GameTestTables.DemoQuest);

            Assert.IsFalse(TrackingRows()[0].ActionEnabled, "条件没满，交付按钮该是灰的");

            m_tracker.Notify(EConditionEvent.ReachArea, 1, 1);
            m_tracker.Notify(EConditionEvent.KillMonster, BattleTestTables.Wolf, 3);

            List<QuestRow> rows = TrackingRows();
            Assert.IsTrue(rows[0].ActionEnabled, "条件全满了，交付按钮该能按");
            StringAssert.Contains("可交付", rows[0].Title);
        }

        // ====================================================================
        //  三、按钮名契约（**面板就靠它派发**）
        // ====================================================================

        /// <summary>按按钮名执行：接取。</summary>
        [Test]
        public void HandleAction_AcceptByName()
        {
            Assert.IsTrue(m_model.HandleAction("Accept_" + GameTestTables.DemoQuest));

            Assert.AreEqual(EQuestState.Accepted, m_runtime.StateOf(GameTestTables.DemoQuest));
            Assert.AreEqual("已受理。", m_model.LastMessage);
        }

        /// <summary>按按钮名执行：条件没满时交付 -> 拒绝，且原因**留在消息里**。</summary>
        [Test]
        public void HandleAction_SubmitTooEarly_KeepsReason()
        {
            m_model.HandleAction("Accept_" + GameTestTables.DemoQuest);

            Assert.IsTrue(m_model.HandleAction("Submit_" + GameTestTables.DemoQuest));

            Assert.AreEqual(EQuestState.Accepted, m_runtime.StateOf(GameTestTables.DemoQuest));
            StringAssert.Contains("还没做完", m_model.LastMessage,
                "失败原因必须留下来给 UI 显示（玩家点了没反应是最难查的问题）");
        }

        /// <summary>条件满后交付：状态变化 + 消息变成成功提示。</summary>
        [Test]
        public void HandleAction_SubmitAfterComplete_Succeeds()
        {
            m_model.HandleAction("Accept_" + GameTestTables.DemoQuest);
            m_tracker.Notify(EConditionEvent.ReachArea, 1, 1);
            m_tracker.Notify(EConditionEvent.KillMonster, BattleTestTables.Wolf, 3);

            Assert.IsTrue(m_model.HandleAction("Submit_" + GameTestTables.DemoQuest));
            Assert.AreEqual(EQuestState.Submitted, m_runtime.StateOf(GameTestTables.DemoQuest));
            Assert.AreEqual("已受理。", m_model.LastMessage);
        }

        /// <summary>认不出的按钮名：返回 false 并说明（不静默）。</summary>
        [Test]
        public void HandleAction_UnknownName_ReportsFalse()
        {
            Assert.IsFalse(m_model.HandleAction("Drop_3004"));
            StringAssert.Contains("不认识的按钮名", m_model.LastMessage);

            Assert.IsFalse(m_model.HandleAction(null));
            Assert.IsFalse(m_model.HandleAction(string.Empty));
        }

        /// <summary>编号不是数字时也要说清（程序错误，不是玩家输入错误）。</summary>
        [Test]
        public void HandleAction_NonNumericId_ReportsInternalError()
        {
            Assert.IsTrue(m_model.HandleAction("Accept_abc"));
            StringAssert.Contains("不是数字", m_model.LastMessage);
        }

        // ====================================================================
        //  四、消息管理
        // ====================================================================

        /// <summary>重复接取：原因留在 `LastMessage`（UI 直接显示）。</summary>
        [Test]
        public void Accept_Twice_KeepsRejectReason()
        {
            m_model.Accept(GameTestTables.DemoQuest);
            m_model.ClearMessage();

            m_model.Accept(GameTestTables.DemoQuest);

            StringAssert.Contains("已经接过", m_model.LastMessage);
        }

        /// <summary>`ClearMessage` 把消息清空（面板刚打开时用）。</summary>
        [Test]
        public void ClearMessage_EmptiesMessage()
        {
            m_model.Accept(9999);
            Assert.IsNotEmpty(m_model.LastMessage);

            m_model.ClearMessage();
            Assert.IsEmpty(m_model.LastMessage);
        }

        /// <summary>构造器要挡住 null（免得后面每处都判）。</summary>
        [Test]
        public void Constructor_NullRuntime_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => new QuestPanelModel(null));
        }

        // ====================================================================
        //  M4-S3 收口（`Docs\27` §二十四）：进度**是谁说的**
        // ====================================================================
        //  ⚠️ 这一组盯的是本片**最容易回归**的地方：`FindProgress` 返回 **null**
        //     意思是"**服务端没说**"，而**不是**"进度是 0"。把前者画成「0/N」
        //     会让玩家以为进度被清零了（而其实只是还没同步）。

        /// <summary>测试用的假权威：只回答"我认识的那一条"，别的**一律沉默**（= 服务端没说）。</summary>
        private sealed class FakeAuthority : IQuestProgressAuthority
        {
            /// <summary>我"说过"的那些条件。</summary>
            private readonly Dictionary<int, AuthoritativeProgress> m_known =
                new Dictionary<int, AuthoritativeProgress>();

            /// <summary>被问过几次（证明"真的走了权威这条路"，而不是压根没接）。</summary>
            public int Asked { get; private set; }

            /// <summary>让某条条件有权威值。</summary>
            /// <param name="conditionId">条件编号。</param>
            /// <param name="current">已累计。</param>
            /// <param name="required">需求值。</param>
            /// <param name="met">达成了没有。</param>
            public void Set(int conditionId, int current, int required, bool met)
            {
                m_known[conditionId] = new AuthoritativeProgress(current, required, met);
            }

            /// <inheritdoc/>
            public bool TryGetCondition(int conditionId, out AuthoritativeProgress progress)
            {
                Asked++;
                return m_known.TryGetValue(conditionId, out progress);
            }
        }

        /// <summary>
        /// 接一个任务，并把它的第一条条件在**本地**推到 `localCount`（= 界面上"客户端自己算"的那个数）。
        /// <para>⚠️ 用 `TryGetDef` 反查事件与目标来推进度：这样用例**不依赖具体配置的编号**，
        /// 表改了一个数字它也不会红。</para>
        /// </summary>
        /// <param name="localCount">要推到的数量。</param>
        /// <returns>被推进的那条条件行（来自**这一次**构建的追踪视图）。</returns>
        private QuestConditionLine PrimeLocalProgress(int localCount)
        {
            m_model.Accept(GameTestTables.DemoQuest);

            List<QuestTracking> trackings = new List<QuestTracking>();
            m_runtime.CopyActiveTrackings(trackings);

            Assert.Greater(trackings.Count, 0, "接了任务之后应当有一行追踪");
            Assert.Greater(trackings[0].Conditions.Count, 0, "这个任务应当至少有 1 条条件");

            QuestConditionLine line = trackings[0].Conditions[0];

            ConditionDef def;
            Assert.IsTrue(m_tracker.TryGetDef(line.ConditionId, out def),
                "条件应当已在接取时登记（编号 " + line.ConditionId + "）");

            m_tracker.Notify(def.EventType, def.TargetId, localCount);

            return line;
        }

        /// <summary>新造的条件行默认算**预测**（保守默认：忘了盖也不会显示得像事实）。</summary>
        [Test]
        public void ConditionLine_DefaultSource_IsLocalPrediction()
        {
            Assert.AreEqual(EProgressSource.LocalPrediction, new QuestConditionLine().Source);
        }

        /// <summary>
        /// ⚠️ **本片最容易回归的一条**：服务端**没说**这条条件 ⇒
        /// 必须保留**本地**那个数（绝不能变成 0/N），并且**标出来它是本地预测**。
        /// </summary>
        [Test]
        public void Authority_SilentCondition_KeepsLocalValue_AndMarksItAsPrediction()
        {
            const int localCount = 2;

            QuestConditionLine line = PrimeLocalProgress(localCount);
            int required = line.Required;

            var authority = new FakeAuthority();     // 什么都不认识 = 服务端没提过这条
            m_model.Authority = authority;

            List<QuestRow> rows = new List<QuestRow>();
            m_model.CopyTrackingRows(rows);

            string detail = rows[0].Detail;

            Assert.Greater(authority.Asked, 0, "应当真的去问过权威（而不是压根没接上）");

            // 本地那个数还在
            StringAssert.Contains(localCount + "/" + required, detail);

            // ⚠️ 关键：**绝不能**画成 0/N（把"服务端没说"当成"进度是 0"）
            StringAssert.DoesNotContain("0/" + required, detail);

            // 而且要一眼看得出它是预测
            StringAssert.Contains(QuestProgressOverlay.LocalMark, detail);
        }

        /// <summary>服务端**说了** ⇒ 以它为准，而且**不标**成预测（标了就分不清真假）。</summary>
        [Test]
        public void Authority_Speaks_OverridesLocalValue_AndIsNotMarkedAsPrediction()
        {
            QuestConditionLine line = PrimeLocalProgress(2);

            const int authoritativeCurrent = 1;
            int required = line.Required;

            var authority = new FakeAuthority();
            authority.Set(line.ConditionId, authoritativeCurrent, required, false);
            m_model.Authority = authority;

            List<QuestRow> rows = new List<QuestRow>();
            m_model.CopyTrackingRows(rows);

            string detail = rows[0].Detail;

            // 显示的是**服务端**那个数（1），不是本地那个（2）
            StringAssert.Contains(authoritativeCurrent + "/" + required, detail);
            StringAssert.DoesNotContain("2/" + required, detail);

            // 权威值**不标**预测
            StringAssert.DoesNotContain(QuestProgressOverlay.LocalMark, detail);
        }

        /// <summary>没接权威（`Authority == null`）⇒ 与"权威没说话"**同一条路**：一律标成预测。</summary>
        [Test]
        public void Authority_Null_EverythingIsMarkedAsPrediction()
        {
            PrimeLocalProgress(1);

            m_model.Authority = null;

            List<QuestRow> rows = new List<QuestRow>();
            m_model.CopyTrackingRows(rows);

            StringAssert.Contains(QuestProgressOverlay.LocalMark, rows[0].Detail);
        }

        /// <summary>权威值会**原地改写**条件行（供其它显示点复用同一份数据）。</summary>
        [Test]
        public void Overlay_OverwritesLinesInPlace_AndReportsHowMany()
        {
            var lines = new List<QuestConditionLine>();
            QuestConditionLine a = new QuestConditionLine();
            a.ConditionId = 4001;
            a.Current = 0;
            a.Required = 3;
            QuestConditionLine b = new QuestConditionLine();
            b.ConditionId = 4002;
            b.Current = 5;
            b.Required = 9;
            lines.Add(a);
            lines.Add(b);

            var authority = new FakeAuthority();
            authority.Set(4001, 3, 3, true);

            int applied = QuestProgressOverlay.Apply(lines, authority);

            Assert.AreEqual(1, applied, "只有 4001 拿到了权威值");
            Assert.AreEqual(EProgressSource.ServerAuthoritative, a.Source);
            Assert.AreEqual(3, a.Current);
            Assert.IsTrue(a.IsMet);

            Assert.AreEqual(EProgressSource.LocalPrediction, b.Source, "4002 服务端没说 ⇒ 保持预测");
            Assert.AreEqual(5, b.Current, "而且**不许**被改成 0");
        }
    }
}
