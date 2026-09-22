// ============================================================================
//  M2-A1 · 条件系统的 EditMode 测试
//  对应验收：Docs\22-M2开工清单.md A1（V1）
//  被测：Client\Assets\_Project\Shared\Condition\（**双端共享**的那一份源码）
//
//  ---------------------------------------------------------------------------
//  为什么这一整套能在 EditMode 里跑
//  ---------------------------------------------------------------------------
//  因为条件系统是**纯 C#**：没有 MonoBehaviour、没有帧循环、没有 UnityEngine。
//  它唯一的对外依赖是两道接缝：
//      · IConditionProgressStore（进度存哪）—— 测试用内存实现
//      · ConditionMet / ProgressChanged（达成了谁关心）—— 测试挂一个记录器
//  于是"打怪 → 进度 → 完成"这条链路**不需要 Unity、不需要配置表、不需要网络**。
//
//  📌 这就是"共享层不许碰引擎"（D1/DET-02）顺带换来的红利：
//     同一份代码在服务端也是这么测的。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Shared.Condition;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>M2-A1：条件系统核心的测试。</summary>
    public sealed class ConditionTrackerTests
    {
        /// <summary>进度存放处。</summary>
        private InMemoryConditionProgressStore m_store;

        /// <summary>被测对象。</summary>
        private ConditionTracker m_tracker;

        /// <summary>记录：哪些条件达成了（按顺序）。</summary>
        private List<int> m_metKeys;

        /// <summary>记录：达成时的进度。</summary>
        private List<ConditionProgress> m_metProgress;

        /// <summary>记录：哪些条件的进度变了（按顺序）。</summary>
        private List<int> m_changedKeys;

        /// <summary>每个用例前：造一套干净的。</summary>
        [SetUp]
        public void SetUp()
        {
            m_store = new InMemoryConditionProgressStore();
            m_tracker = new ConditionTracker(m_store);

            m_metKeys = new List<int>();
            m_metProgress = new List<ConditionProgress>();
            m_changedKeys = new List<int>();

            m_tracker.ConditionMet += OnMet;
            m_tracker.ProgressChanged += OnChanged;
        }

        /// <summary>每个用例后：解开订阅（**订阅不退订是 M1 反复踩的坑**）。</summary>
        [TearDown]
        public void TearDown()
        {
            if (m_tracker != null)
            {
                m_tracker.ConditionMet -= OnMet;
                m_tracker.ProgressChanged -= OnChanged;
                m_tracker.Clear();
            }

            if (m_store != null)
            {
                m_store.Clear();
            }
        }

        /// <summary>记录达成。</summary>
        /// <param name="key">条件编号。</param>
        /// <param name="progress">进度。</param>
        private void OnMet(int key, ConditionProgress progress)
        {
            m_metKeys.Add(key);
            m_metProgress.Add(progress);
        }

        /// <summary>记录进度变化。</summary>
        /// <param name="key">条件编号。</param>
        /// <param name="progress">进度。</param>
        private void OnChanged(int key, ConditionProgress progress)
        {
            m_changedKeys.Add(key);
        }

        /// <summary>登记一条"击杀 3 只目标 6001"的条件。</summary>
        /// <param name="key">条件编号。</param>
        /// <param name="reset">是否清零进度。</param>
        private void RegisterKillThree(int key, bool reset = true)
        {
            m_tracker.Register(key, new ConditionDef(EConditionEvent.KillMonster, 6001, 3), reset);
        }

        // ====================================================================
        //  一、累加与达成
        // ====================================================================

        /// <summary>喂事件会累加进度。</summary>
        [Test]
        public void Notify_AccumulatesProgress()
        {
            RegisterKillThree(4001);

            Assert.AreEqual(0, m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1));

            ConditionProgress progress;
            Assert.IsTrue(m_tracker.TryGetProgress(4001, out progress));
            Assert.AreEqual(1, progress.Current);
            Assert.AreEqual(3, progress.Required);
            Assert.IsFalse(progress.IsMet);
            Assert.AreEqual(2, progress.Remaining);
            CollectionAssert.IsEmpty(m_metKeys, "才打 1 只，不该达成");
        }

        /// <summary>够数量时达成，并且**只通知一次**。</summary>
        [Test]
        public void Notify_ReachingRequired_FiresConditionMetExactlyOnce()
        {
            RegisterKillThree(4001);

            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Assert.AreEqual(0, m_metKeys.Count, "2/3 还没到");

            int met = m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Assert.AreEqual(1, met, "这一次应当让 1 条条件达成");
            CollectionAssert.AreEqual(new[] { 4001 }, m_metKeys);
            Assert.AreEqual(3, m_metProgress[0].Current);
        }

        /// <summary>达成之后再喂事件：不累计、也不再通知（钳位带来的语义）。</summary>
        [Test]
        public void Notify_AfterMet_DoesNotAccumulateNorFireAgain()
        {
            RegisterKillThree(4001);

            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 3);
            Assert.AreEqual(1, m_metKeys.Count);

            int metAgain = m_tracker.Notify(EConditionEvent.KillMonster, 6001, 5);

            Assert.AreEqual(0, metAgain, "已经达成，不该再通知");
            Assert.AreEqual(1, m_metKeys.Count, "达成只通知一次");

            ConditionProgress progress;
            m_tracker.TryGetProgress(4001, out progress);
            Assert.AreEqual(3, progress.Current, "进度要钳在需求值上，不能被超额推高");
        }

        /// <summary>一次给多了数量，进度要钳位。</summary>
        [Test]
        public void Notify_Overshoot_ClampsToRequired()
        {
            RegisterKillThree(4001);

            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 100);

            ConditionProgress progress;
            m_tracker.TryGetProgress(4001, out progress);
            Assert.AreEqual(3, progress.Current);
            Assert.AreEqual(0, progress.Remaining);
            Assert.IsTrue(progress.IsMet);
        }

        /// <summary>一次事件让两条条件达成时，返回值是 2。</summary>
        [Test]
        public void Notify_MeetsTwoConditions_ReturnsTwo()
        {
            m_tracker.Register(1, new ConditionDef(EConditionEvent.KillMonster, 6001, 1), true);
            m_tracker.Register(2, new ConditionDef(EConditionEvent.KillMonster, 0, 1), true);

            Assert.AreEqual(2, m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1));
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, m_metKeys);
        }

        // ====================================================================
        //  二、匹配规则（事件类型 / 目标编号）
        // ====================================================================

        /// <summary>`targetId == 0` 表示"任意目标"。</summary>
        [Test]
        public void Notify_AnyTargetCondition_MatchesEveryTarget()
        {
            m_tracker.Register(1, new ConditionDef(EConditionEvent.KillMonster, 0, 2), true);

            m_tracker.Notify(EConditionEvent.KillMonster, 9999, 1);
            m_tracker.Notify(EConditionEvent.KillMonster, 1, 1);

            Assert.AreEqual(1, m_metKeys.Count, "两个不同的目标各算一次，凑够 2 就该达成");
        }

        /// <summary>指定目标的条目不关心别的目标。</summary>
        [Test]
        public void Notify_SpecificTargetCondition_IgnoresOtherTargets()
        {
            RegisterKillThree(4001);

            m_tracker.Notify(EConditionEvent.KillMonster, 6002, 5);

            ConditionProgress progress;
            m_tracker.TryGetProgress(4001, out progress);
            Assert.AreEqual(0, progress.Current, "打的是别的怪，不该涨进度");
        }

        /// <summary>事件类型不同就不匹配（击杀条件不该被"捡东西"推进）。</summary>
        [Test]
        public void Notify_DifferentEventType_DoesNotMatch()
        {
            RegisterKillThree(4001);

            m_tracker.Notify(EConditionEvent.CollectItem, 6001, 5);

            Assert.IsFalse(m_tracker.IsMet(4001));
            Assert.AreEqual(0, m_changedKeys.Count);
        }

        // ====================================================================
        //  三、注册语义（任务的"接取才计数" vs 成就的"一直生效"）
        // ====================================================================

        /// <summary>`resetProgress = true`：接取时清零（任务用）。</summary>
        [Test]
        public void Register_WithReset_ClearsPreviousProgress()
        {
            m_store.SetProgress(4001, 2);

            RegisterKillThree(4001, true);

            ConditionProgress progress;
            m_tracker.TryGetProgress(4001, out progress);
            Assert.AreEqual(0, progress.Current, "任务接取时应当清零");
        }

        /// <summary>`resetProgress = false`：保留既有进度（成就用）。</summary>
        [Test]
        public void Register_WithoutReset_KeepsPreviousProgress()
        {
            m_store.SetProgress(4001, 2);

            RegisterKillThree(4001, false);

            ConditionProgress progress;
            m_tracker.TryGetProgress(4001, out progress);
            Assert.AreEqual(2, progress.Current);
            CollectionAssert.IsEmpty(m_metKeys, "2/3 没满，注册时不该通知");
        }

        /// <summary>
        /// **不清零且注册时就已经达成 → 当场通知一次**（成就"登录即解锁"）。
        /// <para>不这么做的话，玩家得**再打一只**才解锁 —— 一个看起来像玄学的 bug。</para>
        /// </summary>
        [Test]
        public void Register_WithoutReset_WhenAlreadyMet_FiresImmediately()
        {
            m_store.SetProgress(4001, 3);

            RegisterKillThree(4001, false);

            CollectionAssert.AreEqual(new[] { 4001 }, m_metKeys, "注册时就已达成，应当立刻通知");
        }

        /// <summary>重复登记同一个编号要**当场报错**（静默覆盖会让进度被悄悄清零）。</summary>
        [Test]
        public void Register_DuplicateKey_Throws()
        {
            RegisterKillThree(4001);

            InvalidOperationException exception =
                Assert.Throws<InvalidOperationException>(() => RegisterKillThree(4001));

            StringAssert.Contains("已经登记过", exception.Message);
            StringAssert.Contains("Unregister", exception.Message);
        }

        /// <summary>注销后不再跟踪，但**进度留在存放处**（重新接可以选择接着算）。</summary>
        [Test]
        public void Unregister_StopsTrackingButKeepsProgressInStore()
        {
            RegisterKillThree(4001);
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Assert.IsTrue(m_tracker.Unregister(4001));
            Assert.IsFalse(m_tracker.IsRegistered(4001));

            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);
            Assert.AreEqual(1, m_store.GetProgress(4001), "注销不动进度");

            Assert.IsFalse(m_tracker.Unregister(4001), "再注销一次应当返回 false");
        }

        // ====================================================================
        //  四、非法输入必须"响亮地失败"
        // ====================================================================

        /// <summary>`count = 0` 不是事件，要报错（静默忽略会让"进度怎么不涨"变成悬案）。</summary>
        [Test]
        public void Notify_ZeroCount_Throws()
        {
            RegisterKillThree(4001);
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => m_tracker.Notify(EConditionEvent.KillMonster, 6001, 0));
        }

        /// <summary>枚举强转出来的非法值要报错（它在 C# 里不报错，会安静地匹配不到任何条件）。</summary>
        [Test]
        public void Notify_UnknownEventValue_Throws()
        {
            RegisterKillThree(4001);

            System.ArgumentOutOfRangeException exception =
                Assert.Throws<System.ArgumentOutOfRangeException>(
                    () => m_tracker.Notify((EConditionEvent)99, 6001, 1));

            StringAssert.Contains("99", exception.Message);
        }

        /// <summary>负的目标编号要报错（要用 0 表示"任意"）。</summary>
        [Test]
        public void Notify_NegativeTarget_Throws()
        {
            RegisterKillThree(4001);
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => m_tracker.Notify(EConditionEvent.KillMonster, -1, 1));
        }

        /// <summary>`requiredCount = 0` 的条件会"立刻达成"，所以构造时就要报错。</summary>
        [Test]
        public void ConditionDef_ZeroRequired_Throws()
        {
            System.ArgumentException exception =
                Assert.Throws<System.ArgumentException>(
                    () => new ConditionDef(EConditionEvent.KillMonster, 6001, 0));

            StringAssert.Contains("需要数量", exception.Message);
        }

        /// <summary>构造器为 null 要报错。</summary>
        [Test]
        public void Constructor_NullStore_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => new ConditionTracker(null));
        }

        /// <summary>`TryCreate` 不抛异常，只交出原因。</summary>
        [Test]
        public void TryCreate_Invalid_ReturnsReasonWithoutThrowing()
        {
            ConditionDef def;
            string error;

            bool ok = ConditionDef.TryCreate((EConditionEvent)42, 1, 1, out def, out error);

            Assert.IsFalse(ok);
            Assert.IsNotNull(error);
            StringAssert.Contains("事件类型", error);
        }

        // ====================================================================
        //  五、通知的顺序与重入（**契约**，不是实现细节）
        // ====================================================================

        /// <summary>每次推进都发 `ProgressChanged`（追踪条要显示 1/3、2/3、3/3）。</summary>
        [Test]
        public void ProgressChanged_FiresForEveryAdvance()
        {
            RegisterKillThree(4001);

            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            CollectionAssert.AreEqual(new[] { 4001, 4001, 4001 }, m_changedKeys);
        }

        /// <summary>**先发"进度变了"，再发"达成了"**（反过来 UI 会先闪"完成"再回退到 2/3）。</summary>
        [Test]
        public void ProgressChanged_FiresBeforeConditionMet()
        {
            List<string> order = new List<string>();

            ConditionTracker tracker = new ConditionTracker(new InMemoryConditionProgressStore());
            tracker.ProgressChanged += (key, progress) => order.Add("changed:" + progress);
            tracker.ConditionMet += (key, progress) => order.Add("met:" + progress);
            tracker.Register(1, new ConditionDef(EConditionEvent.KillMonster, 6001, 1), true);

            tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            CollectionAssert.AreEqual(new[] { "changed:1/1", "met:1/1" }, order);
        }

        /// <summary>
        /// 达成回调里**登记一条新条件**是允许的（交任务时接下一个任务就是这个形状）；
        /// 但新条件**不受这一次事件影响**（否则"刚接的任务被上一只怪计数"）。
        /// </summary>
        [Test]
        public void Callback_MayRegisterDuringDispatch_NewConditionNotAffectedBySameEvent()
        {
            m_tracker.ConditionMet += (key, progress) =>
            {
                if (key == 1)
                {
                    m_tracker.Register(2, new ConditionDef(EConditionEvent.KillMonster, 6001, 3), true);
                }
            };

            m_tracker.Register(1, new ConditionDef(EConditionEvent.KillMonster, 6001, 1), true);
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Assert.IsTrue(m_tracker.IsRegistered(2), "回调里登记应当成功");

            ConditionProgress fresh;
            m_tracker.TryGetProgress(2, out fresh);
            Assert.AreEqual(0, fresh.Current, "这一次事件不该影响刚登记的条件");
        }

        /// <summary>达成回调里**再喂一个事件**是允许的（发奖励 → 奖励里有物品 → 又是一次 CollectItem）。</summary>
        [Test]
        public void Callback_MayNotifyReentrantly()
        {
            bool reentered = false;

            // ⚠️ 这两个条件的安排很关键（第一版我写错了，被 .NET 探针当场抓出来）：
            //    回调必须喂**另一个条件**关心的事件，否则永远不会重入，
            //    用例会"看着像在测重入、其实是空过"。
            //    条件 1（捡物品）由**重入的那次** Notify 完成；
            //    条件 2（击杀）由**第一次** Notify 完成，它的回调里再喂一次事件。
            m_tracker.Register(1, new ConditionDef(EConditionEvent.CollectItem, 0, 1), true);
            m_tracker.Register(2, new ConditionDef(EConditionEvent.KillMonster, 6001, 1), true);

            m_tracker.ConditionMet += (key, progress) =>
            {
                if (key == 2 && !reentered)
                {
                    reentered = true;
                    m_tracker.Notify(EConditionEvent.CollectItem, 7002, 1);
                }
            };

            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Assert.IsTrue(reentered, "回调里应当能再喂事件");
            Assert.IsTrue(m_tracker.IsMet(1), "重入的那次事件应当正常结算");
            Assert.IsTrue(m_tracker.IsMet(2), "触发重入的那条条件也应当达成");
        }

        /// <summary>没登记任何条件时喂事件：直接返回 0，不报错。</summary>
        [Test]
        public void Notify_NothingRegistered_ReturnsZero()
        {
            Assert.AreEqual(0, m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1));
        }

        // ====================================================================
        //  六、查询接口
        // ====================================================================

        /// <summary>没登记过的条件：查进度返回 false，`IsMet` 返回 false（不抛异常）。</summary>
        [Test]
        public void Queries_Unregistered_ReturnFalseInsteadOfThrowing()
        {
            ConditionProgress progress;

            Assert.IsFalse(m_tracker.TryGetProgress(123, out progress));
            Assert.IsFalse(m_tracker.IsMet(123));
            Assert.AreEqual(0, m_tracker.RegisteredCount);
        }

        /// <summary>`MetCount` 报告已达成条数。</summary>
        [Test]
        public void MetCount_ReportsMetConditions()
        {
            m_tracker.Register(1, new ConditionDef(EConditionEvent.KillMonster, 6001, 1), true);
            m_tracker.Register(2, new ConditionDef(EConditionEvent.KillMonster, 6002, 2), true);

            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            Assert.AreEqual(2, m_tracker.RegisteredCount);
            Assert.AreEqual(1, m_tracker.MetCount);
        }

        /// <summary>`Clear` 只清登记，不动进度。</summary>
        [Test]
        public void Clear_RemovesRegistrationsButKeepsProgress()
        {
            RegisterKillThree(4001);
            m_tracker.Notify(EConditionEvent.KillMonster, 6001, 1);

            m_tracker.Clear();

            Assert.AreEqual(0, m_tracker.RegisteredCount);
            Assert.AreEqual(1, m_store.GetProgress(4001));
        }
    }
}
