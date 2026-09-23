// ============================================================================
//  M2-D · 任务面板（MonoBehaviour 那一半）的 EditMode 测试
//  对应验收：Docs\22-M2开工清单.md D 组（V6）
//  被测：Client\Assets\_Project\Game\UI\QuestPanel.cs
//
//  ---------------------------------------------------------------------------
//  为什么这组能在 EditMode 里跑（这是 A9 刻意设计的）
//  ---------------------------------------------------------------------------
//  因为 `BasePanel` 的初始化入口是**公开方法** `Initialize()`，不是 Unity 回调
//  （**EditMode 下 Unity 根本不调用 `Awake`**，依赖 Awake 的面板一条都测不了）。
//  于是测试能自己 `new GameObject` + `AddComponent` + 摆几个控件 + `Initialize()`，
//  完全不碰预制体、不进播放模式。
//
//  ⚠️ 这组盯的是**面板与模型的接缝**，不是"字好不好看"：
//    · 控件名对不对（`RequireControl` 找不到会抛带指引的异常）
//    · **动态生成的按钮有没有接上线**（克隆体不会带上模板的监听 —— 漏了就是"点了没反应"）
//    · 任务事件来了会不会自动重画
// ============================================================================

using System.Collections.Generic;
using NBC.Framework;
using NBC.Game.Config;
using NBC.Game.Quest;
using NBC.Game.UI;
using NBC.Shared.Condition;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace NBC.Tests.EditMode
{
    /// <summary>M2-D：任务面板的测试。</summary>
    public sealed class QuestPanelTests
    {
        /// <summary>面板根物体。</summary>
        private GameObject m_root;

        /// <summary>被测面板。</summary>
        private QuestPanel m_panel;

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

        /// <summary>视图模型。</summary>
        private QuestPanelModel m_model;

        /// <summary>每个用例前造一张"控件齐全"的面板。</summary>
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

            BuildPanel(true);
        }

        /// <summary>每个用例后清理。</summary>
        [TearDown]
        public void TearDown()
        {
            if (m_root != null)
            {
                Object.DestroyImmediate(m_root);
                m_root = null;
            }

            if (m_runtime != null)
            {
                m_runtime.Dispose();
                m_runtime = null;
            }

            EventCenter.Instance.WarnOnMissingListener = true;

            BattleTestTables.Destroy(m_quests);
            BattleTestTables.Destroy(m_conditions);
            BattleTestTables.Destroy(m_rewards);
        }

        // ====================================================================
        //  夹具：搭一个面板
        // ====================================================================

        /// <summary>搭面板（控件名与 `QuestPanel` 里的常量一致）。</summary>
        /// <param name="withTitle">要不要摆标题控件（false = 故意缺一个，验报错）。</param>
        private void BuildPanel(bool withTitle)
        {
            m_root = new GameObject("QuestPanel", typeof(RectTransform));
            m_panel = m_root.AddComponent<QuestPanel>();

            if (withTitle)
            {
                AddControl<Text>(QuestPanel.TitleControl);
            }

            AddControl<Text>(QuestPanel.MessageControl);
            AddControl<VerticalLayoutGroup>(QuestPanel.OfferListControl);
            AddControl<VerticalLayoutGroup>(QuestPanel.TrackingListControl);

            // 行模板：一个 Button + 一个子 Text（**子物体的名字随意**，代码按组件找）
            GameObject template = AddControl<Button>(QuestPanel.RowTemplateControl);
            GameObject label = new GameObject("Whatever", typeof(RectTransform));
            label.transform.SetParent(template.transform, false);
            label.AddComponent<Text>();
        }

        /// <summary>往面板下挂一个带指定组件的子物体。</summary>
        /// <typeparam name="T">组件类型。</typeparam>
        /// <param name="name">子物体名字（= 控件名）。</param>
        /// <returns>子物体。</returns>
        private GameObject AddControl<T>(string name) where T : Component
        {
            GameObject child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(m_root.transform, false);
            child.AddComponent<T>();
            return child;
        }

        /// <summary>取容器下的行。</summary>
        /// <param name="containerName">容器名。</param>
        /// <returns>行按钮。</returns>
        private List<Button> RowsIn(string containerName)
        {
            Transform container = m_root.transform.Find(containerName);
            Assert.IsNotNull(container, "找不到容器 " + containerName);

            List<Button> rows = new List<Button>();

            for (int i = 0; i < container.childCount; i++)
            {
                Button button = container.GetChild(i).GetComponent<Button>();

                if (button != null)
                {
                    rows.Add(button);
                }
            }

            return rows;
        }

        // ====================================================================
        //  一、初始化与控件查找
        // ====================================================================

        /// <summary>初始化之后应当能拿到全部控件，并且**模板是关着的**。</summary>
        [Test]
        public void Initialize_ResolvesControlsAndHidesTemplate()
        {
            m_panel.Initialize();

            Assert.IsTrue(m_panel.IsInitialized);
            Assert.IsTrue(m_panel.ControlCount >= 5, "应当收集到 5 个控件（2 Text + 2 LayoutGroup + 1 Button）");

            Transform template = m_root.transform.Find(QuestPanel.RowTemplateControl);
            Assert.IsFalse(template.gameObject.activeSelf, "模板只是用来克隆的，本体不能显示");
        }

        /// <summary>
        /// 缺控件时 `Initialize` **当场抛异常**，而且消息里列出"现有这些控件"。
        /// <para>这是 A9 那句"让错误自己说出来"的直接落地：报错发生在写错的那一行，
        /// 而且不用去翻预制体就知道该改成什么。</para>
        /// </summary>
        [Test]
        public void Initialize_MissingControl_ThrowsWithControlList()
        {
            Object.DestroyImmediate(m_root);
            BuildPanel(false);   // 故意不摆 Title

            System.InvalidOperationException exception =
                Assert.Throws<System.InvalidOperationException>(() => m_panel.Initialize());

            StringAssert.Contains(QuestPanel.TitleControl, exception.Message);
            StringAssert.Contains("现有这些控件", exception.Message);
        }

        // ====================================================================
        //  二、绑定与刷新
        // ====================================================================

        /// <summary>绑定之后：可接列表里应当出现两条任务行，名字就是**按钮名**。</summary>
        [Test]
        public void Bind_SpawnsOfferRowsNamedByAction()
        {
            m_panel.Initialize();
            m_panel.Bind(m_model);

            List<Button> offers = RowsIn(QuestPanel.OfferListControl);

            Assert.AreEqual(2, offers.Count);
            Assert.AreEqual("Accept_3004", offers[0].name, "行名字必须是按钮名（契约）");
            Assert.AreEqual("Accept_3002", offers[1].name);
            Assert.AreEqual(2, m_panel.SpawnedRowCount);
        }

        /// <summary>行上的文字要带上标题、逐条条件进度、按钮名对应的动作。</summary>
        [Test]
        public void Bind_RowTextShowsTitleAndProgress()
        {
            m_panel.Initialize();
            m_panel.Bind(m_model);

            m_model.Accept(GameTestTables.DemoQuest);
            m_tracker.Notify(EConditionEvent.ReachArea, 1, 1);
            m_panel.Refresh();

            List<Button> trackings = RowsIn(QuestPanel.TrackingListControl);
            Assert.AreEqual(1, trackings.Count);

            Text label = trackings[0].GetComponentInChildren<Text>(true);
            Assert.IsNotNull(label);
            StringAssert.Contains("清剿野狼", label.text);
            StringAssert.Contains("1/1", label.text);
            StringAssert.Contains("交付", label.text);
        }

        // ====================================================================
        //  三、点击（**动态按钮必须自己接线**）
        // ====================================================================

        /// <summary>点"接取"行按钮：任务真的被接了，并且界面重画（可接少一条、已接多一条）。</summary>
        [Test]
        public void Click_AcceptRow_AcceptsQuestAndRefreshes()
        {
            m_panel.Initialize();
            m_panel.Bind(m_model);

            List<Button> offers = RowsIn(QuestPanel.OfferListControl);
            Assert.AreEqual(2, offers.Count);

            // 直接触发按钮的 onClick（等价于玩家点了一下）
            offers[0].onClick.Invoke();

            Assert.AreEqual(EQuestState.Accepted, m_runtime.StateOf(GameTestTables.DemoQuest));
            Assert.AreEqual(1, RowsIn(QuestPanel.OfferListControl).Count, "可接列表应当少一条");
            Assert.AreEqual(1, RowsIn(QuestPanel.TrackingListControl).Count, "已接列表应当多一条");
        }

        /// <summary>条件没满时点"交付"：任务状态不变，**原因显示在消息控件上**。</summary>
        [Test]
        public void Click_SubmitTooEarly_ShowsReasonInMessage()
        {
            m_panel.Initialize();
            m_panel.Bind(m_model);

            RowsIn(QuestPanel.OfferListControl)[0].onClick.Invoke();   // 接取 3004

            List<Button> trackings = RowsIn(QuestPanel.TrackingListControl);
            Assert.IsFalse(trackings[0].interactable, "条件没满时按钮应当是灰的");

            // 就算是灰的，直接 Invoke 也要有明确行为（防御性：真实点击不会触发灰按钮）
            trackings[0].onClick.Invoke();

            Assert.AreEqual(EQuestState.Accepted, m_runtime.StateOf(GameTestTables.DemoQuest));
            Assert.IsNotEmpty(m_model.LastMessage);
        }

        /// <summary>条件全满 -> 按钮变为可按 -> 点它就能交付。</summary>
        [Test]
        public void Click_SubmitAfterComplete_Submits()
        {
            m_panel.Initialize();
            m_panel.Bind(m_model);

            RowsIn(QuestPanel.OfferListControl)[0].onClick.Invoke();

            m_tracker.Notify(EConditionEvent.ReachArea, 1, 1);
            m_tracker.Notify(EConditionEvent.KillMonster, BattleTestTables.Wolf, 3);
            m_panel.Refresh();

            List<Button> trackings = RowsIn(QuestPanel.TrackingListControl);
            Assert.IsTrue(trackings[0].interactable, "条件全满后按钮该能按");

            trackings[0].onClick.Invoke();

            Assert.AreEqual(EQuestState.Submitted, m_runtime.StateOf(GameTestTables.DemoQuest));
        }

        // ====================================================================
        //  四、自动刷新（订阅任务事件）
        // ====================================================================

        /// <summary>进度变化事件会**自动**让面板重画（不需要调用方手动 Refresh）。</summary>
        [Test]
        public void QuestEvent_AutoRefreshesPanel()
        {
            m_panel.Initialize();
            m_panel.Bind(m_model);

            m_model.Accept(GameTestTables.DemoQuest);
            m_panel.Refresh();

            // 一开始进度是 0/3
            Text before = RowsIn(QuestPanel.TrackingListControl)[0].GetComponentInChildren<Text>(true);
            StringAssert.Contains("0/3", before.text);

            // ⚠️ 这里**不持有旧行的引用**去比对：重画会**销毁并重建**行，
            //    旧引用会变成已销毁对象（访问它直接抛 MissingReferenceException）。
            //    正确姿势是"触发事件之后重新取行，看内容对不对"。
            m_tracker.Notify(EConditionEvent.KillMonster, BattleTestTables.Wolf, 1);

            Text after = RowsIn(QuestPanel.TrackingListControl)[0].GetComponentInChildren<Text>(true);
            StringAssert.Contains("1/3", after.text, "事件到了应当自动重画成最新进度");
        }

        /// <summary>面板销毁后**不再响应事件**（订阅不退订是 A4/A8 反复踩的坑）。</summary>
        [Test]
        public void AfterDestroy_NoLongerListensToEvents()
        {
            m_panel.Initialize();
            m_panel.Bind(m_model);

            Object.DestroyImmediate(m_root);
            m_root = null;

            // 销毁后触发事件：不该抛异常（面板已经退订了）
            Assert.DoesNotThrow(() =>
                EventCenter.Instance.Trigger(QuestEvents.ProgressChanged, GameTestTables.DemoQuest));
        }

        /// <summary>
        /// 名字对了、但那物体上挂的**不是文字组件**（例如 `Image`）：报错要指出这一点。
        /// <para>
        /// ⚠️ 这是另一类错（"找不到控件" vs "找到了但不是文字"），两类必须分开说 ——
        /// 否则拿到"找不到"的人会一直去改名，而问题其实在组件上。
        /// </para>
        /// </summary>
        [Test]
        public void Initialize_ControlWithoutTextComponent_ThrowsActionableMessage()
        {
            Object.DestroyImmediate(m_root);

            m_root = new GameObject("QuestPanel", typeof(RectTransform));
            m_panel = m_root.AddComponent<QuestPanel>();

            // Title 位置上放一个 Image（它也是 Graphic，但不是文字）
            AddControl<Image>(QuestPanel.TitleControl);
            AddControl<Text>(QuestPanel.MessageControl);
            AddControl<VerticalLayoutGroup>(QuestPanel.OfferListControl);
            AddControl<VerticalLayoutGroup>(QuestPanel.TrackingListControl);
            AddControl<Button>(QuestPanel.RowTemplateControl);

            System.InvalidOperationException exception =
                Assert.Throws<System.InvalidOperationException>(() => m_panel.Initialize());

            StringAssert.Contains(QuestPanel.TitleControl, exception.Message);
            StringAssert.Contains("不是文字组件", exception.Message);
        }

        /// <summary>绑定 null 要当场报错（免得后面每处都判）。</summary>
        [Test]
        public void Bind_NullModel_Throws()
        {
            m_panel.Initialize();

            Assert.Throws<System.ArgumentNullException>(() => m_panel.Bind(null));
        }
    }
}
