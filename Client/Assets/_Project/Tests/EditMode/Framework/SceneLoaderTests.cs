// ============================================================================
//  M1-A5 · SceneLoader 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2
//  对应记录：Docs/06-框架改造记录.md §三 FW-11、§十四
//
//  ---------------------------------------------------------------------------
//  为什么这组测试能放在 EditMode（而不是像场景加载"应该"的那样放 PlayMode）
//  ---------------------------------------------------------------------------
//  因为 `SceneLoader` 把"时间"从 Unity 手里拿回来了：它不写协程，而是一个
//  **`Tick(float deltaTime)` 驱动的状态机**。
//
//      · 游戏运行时：`MonoManager`（A4）每帧调 `Tick(Time.deltaTime)`
//      · 测试里：**测试自己调 `Tick`**，可以精确控制"过了多少秒"
//
//  于是"30 秒超时"这种逻辑，用一次 `Tick(31f)` 就测完了 —— 不需要真等 30 秒。
//  **如果超时逻辑必须真等 30 秒才能测，那它多半不会被测。**
//
//  再加上假加载器（`FakeSceneOperation`），整个过程完全确定，不依赖任何真实资源或场景。
//
//  ---------------------------------------------------------------------------
//  本文件最核心的一条：`Load_DoesNotPublishProgressOnEveryTick`
//  ---------------------------------------------------------------------------
//  它守的就是 **FW-11**：原版 `yield return ao.progress` 不是等待指令，
//  导致那个 while **每帧都广播一次进度**。这条测试用"连续 10 帧进度不变"来证明
//  改造后**只发 1 条**而不是 10 条。
// ============================================================================

using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NBC.Framework;
using NBC.Framework.Asset;
using NBC.Framework.Scenes;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NBC.Tests.EditMode
{
    [TestFixture]
    public class SceneLoaderTests
    {
        private FakeAssetProvider m_provider;
        private SceneLoader m_loader;
        private List<SceneProgressInfo> m_progressEvents;
        private List<string> m_succeededEvents;
        private List<SceneFailureInfo> m_failedEvents;

        [SetUp]
        public void SetUp()
        {
            SingletonRegistry.ResetAll();

            AssetManager assets = AssetManager.Instance;
            m_provider = new FakeAssetProvider();
            assets.SetProvider(m_provider);
            assets.InitializeAsync(AssetRuntimeMode.EditorSimulate).GetAwaiter().GetResult();

            // 事件接收（A3 的事件中心）
            m_progressEvents = new List<SceneProgressInfo>();
            m_succeededEvents = new List<string>();
            m_failedEvents = new List<SceneFailureInfo>();

            EventCenter center = EventCenter.Instance;
            center.WarnOnMissingListener = false;
            center.AddEventListener(SceneEvents.ProgressChanged, (SceneProgressInfo p) => m_progressEvents.Add(p));
            center.AddEventListener(SceneEvents.LoadSucceeded, (string location) => m_succeededEvents.Add(location));
            center.AddEventListener(SceneEvents.LoadFailed, (SceneFailureInfo f) => m_failedEvents.Add(f));

            m_loader = SceneLoader.Instance;
        }

        [TearDown]
        public void TearDown()
        {
            SingletonRegistry.ResetAll();
        }

        // ====================================================================
        //  FW-11 的核心：进度只在"真的变了"时广播
        // ====================================================================

        [Test]
        public void Load_DoesNotPublishProgressOnEveryTick()
        {
            // 这条就是 FW-11 的修复证据。
            // 原版每帧都发一次（10 帧 = 10 条）；改造后进度不变就不发。
            m_loader.Load("Scene_A");

            FakeSceneOperation op = m_provider.SceneOperations[0];
            op.SetProgress(0.3f);

            for (int i = 0; i < 10; i++)
            {
                m_loader.Tick(0.016f);
            }

            Assert.LessOrEqual(
                m_progressEvents.Count, 2,
                "进度没变时不应该每帧都广播（原版 FW-11 就是每帧一条）。实际发了 " +
                m_progressEvents.Count + " 条");
        }

        [Test]
        public void Load_PublishesOncePerProgressChange()
        {
            m_loader.Load("Scene_A");
            FakeSceneOperation op = m_provider.SceneOperations[0];

            // ⚠️ 先推一帧：此时进度还是 0，会广播出「第 1 条」。
            //    早先漏了这一帧，于是第一次广播的进度是 0.25 而不是 0，
            //    断言"5 条"自然就成了 4 条 —— **是我的测试写错了，不是产品代码**。
            m_loader.Tick(0.016f);

            // 4 个不同的进度值 -> 各自广播一次（加上刚才的 0，共 5 次）
            float[] steps = { 0.25f, 0.5f, 0.75f, 0.9f };
            for (int i = 0; i < steps.Length; i++)
            {
                op.SetProgress(steps[i]);
                m_loader.Tick(0.016f);
                m_loader.Tick(0.016f);   // 同一进度再推一帧，不应重复广播
            }

            Assert.AreEqual(steps.Length + 1, m_progressEvents.Count,
                "每个不同进度值应各广播一次（首帧的 0 也算一次）");

            Assert.AreEqual("Scene_A", m_progressEvents[0].Location);
            Assert.AreEqual(steps[steps.Length - 1], m_progressEvents[m_progressEvents.Count - 1].Progress, 0.0001f);
        }

        [Test]
        public void Load_WithNoTicks_PublishesNothing()
        {
            m_loader.Load("Scene_A");

            Assert.AreEqual(0, m_progressEvents.Count, "没推进时间就不该有任何广播");
        }

        // ====================================================================
        //  完成 / 失败
        // ====================================================================

        [Test]
        public void Load_WhenSucceeded_PublishesSucceededEvent()
        {
            SceneLoadRequest request = m_loader.Load("Scene_A");
            m_loader.Tick(0.016f);

            m_provider.SceneOperations[0].Complete();
            m_loader.Tick(0.016f);

            Assert.IsTrue(request.IsDone);
            Assert.IsTrue(request.Succeeded);
            Assert.AreEqual(1, m_succeededEvents.Count);
            Assert.AreEqual("Scene_A", m_succeededEvents[0]);
            Assert.AreEqual(0, m_failedEvents.Count);
            Assert.AreEqual(0, m_loader.PendingCount, "完成后不应再留在进行中列表里");
        }

        [Test]
        public void Load_WhenFailed_PublishesFailureWithReason()
        {
            SceneLoadRequest request = m_loader.Load("Scene_Bad");
            m_loader.Tick(0.016f);

            m_provider.SceneOperations[0].Fail("模拟：场景不存在");
            m_loader.Tick(0.016f);

            Assert.IsTrue(request.IsDone);
            Assert.IsFalse(request.Succeeded);
            StringAssert.Contains("场景不存在", request.Error);
            Assert.AreEqual(1, m_failedEvents.Count);
            StringAssert.Contains("场景不存在", m_failedEvents[0].Error);
            Assert.AreEqual(0, m_succeededEvents.Count);
        }

        [Test]
        public void Tick_AfterDone_DoesNotPublishAgain()
        {
            m_loader.Load("Scene_A");
            m_loader.Tick(0.016f);
            m_provider.SceneOperations[0].Complete();
            m_loader.Tick(0.016f);

            int succeeded = m_succeededEvents.Count;
            int progress = m_progressEvents.Count;

            for (int i = 0; i < 5; i++)
            {
                m_loader.Tick(0.016f);
            }

            Assert.AreEqual(succeeded, m_succeededEvents.Count, "结束后不应重复广播成功");
            Assert.AreEqual(progress, m_progressEvents.Count, "结束后不应重复广播进度");
        }

        // ====================================================================
        //  FW-M06：超时 —— 而且超时必须**卸载**
        // ====================================================================

        [Test]
        public void Load_TimesOut_WhenOperationNeverCompletes()
        {
            SceneLoadRequest request = m_loader.Load("Scene_Hang", LoadSceneMode.Single, 5f);

            m_loader.Tick(1f);
            Assert.IsFalse(request.IsDone, "还没到超时时间");

            // 超时是**真问题**，所以 SceneLoader 会打一条 LogError。
            // Unity Test Runner 默认把"未预期的 LogError"判为失败 —— 所以这里先声明期望。
            LogAssert.Expect(LogType.Error, new Regex("场景加载超时"));

            m_loader.Tick(4.5f);      // 累计 5.5 秒 > 5 秒

            Assert.IsTrue(request.IsDone);
            Assert.IsFalse(request.Succeeded);
            StringAssert.Contains("超时", request.Error);
            Assert.AreEqual(1, m_failedEvents.Count);
            Assert.AreEqual(0, m_loader.PendingCount);
        }

        [Test]
        public void Timeout_UnloadsTheStuckScene()
        {
            // 这条很重要：一个"永远加载不完"的场景如果不卸载，会一直占着内存。
            m_loader.Load("Scene_Hang", LoadSceneMode.Single, 5f);

            LogAssert.Expect(LogType.Error, new Regex("场景加载超时"));

            m_loader.Tick(6f);

            FakeSceneOperation op = m_provider.SceneOperations[0];
            Assert.IsTrue(op.UnloadCalled, "超时后必须卸载那个卡住的场景");
            Assert.IsTrue(op.Disposed, "超时后底层操作也应被释放");
        }

        [Test]
        public void Timeout_Disabled_WhenZero()
        {
            SceneLoadRequest request = m_loader.Load("Scene_Hang", LoadSceneMode.Single, 0f);

            m_loader.Tick(1000f);

            Assert.IsFalse(request.IsDone, "timeoutSeconds <= 0 表示不超时");
            Assert.AreEqual(1, m_loader.PendingCount);
        }

        // ====================================================================
        //  取消
        // ====================================================================

        [Test]
        public void Abort_RemovesRequest_AndUnloads()
        {
            SceneLoadRequest request = m_loader.Load("Scene_A");
            m_loader.Tick(0.016f);
            Assert.AreEqual(1, m_loader.PendingCount);

            m_loader.Abort(request);

            Assert.IsTrue(request.IsDone);
            Assert.IsFalse(request.Succeeded);
            Assert.AreEqual(0, m_loader.PendingCount);
            Assert.IsTrue(m_provider.SceneOperations[0].UnloadCalled, "取消也要卸载，否则场景一直占着");

            m_loader.Tick(0.016f);
            Assert.AreEqual(0, m_succeededEvents.Count);
        }

        [Test]
        public void Abort_AfterDone_IsNoOp()
        {
            SceneLoadRequest request = m_loader.Load("Scene_A");
            m_loader.Tick(0.016f);
            m_provider.SceneOperations[0].Complete();
            m_loader.Tick(0.016f);

            Assert.DoesNotThrow(() => m_loader.Abort(request));
            Assert.AreEqual(1, m_succeededEvents.Count, "已完成的请求不应被取消覆盖掉结果");
        }

        // ====================================================================
        //  多个并发请求
        // ====================================================================

        [Test]
        public void MultipleRequests_AreTrackedIndependently()
        {
            SceneLoadRequest a = m_loader.Load("Scene_A");
            SceneLoadRequest b = m_loader.Load("Scene_B", LoadSceneMode.Additive);

            Assert.AreEqual(2, m_loader.PendingCount);
            Assert.AreEqual(2, m_provider.SceneOperations.Count);

            m_loader.Tick(0.016f);
            m_provider.SceneOperations[1].Complete();      // 只完成 B
            m_loader.Tick(0.016f);

            Assert.IsTrue(b.IsDone);
            Assert.IsFalse(a.IsDone);
            Assert.AreEqual(1, m_loader.PendingCount);
            Assert.AreEqual(1, m_succeededEvents.Count);
            Assert.AreEqual("Scene_B", m_succeededEvents[0]);
        }

        // ====================================================================
        //  交叉验证：SceneLoader 也是 A1 的单例
        // ====================================================================

        [Test]
        public void SceneLoader_CannotBeConstructedDirectly()
        {
            Assert.Throws<System.InvalidOperationException>(() => new SceneLoader());
        }
    }
}
