// ============================================================================
//  M1-A9 · LoadingMaskController 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2
//  对应需求：FW-M09 + FW-M06（消费 A5 的 SceneEvents）
//
//  ---------------------------------------------------------------------------
//  这组测试怎么跑起来的
//  ---------------------------------------------------------------------------
//  控制器只做三件事：听 A5 的三个场景事件 → 决定遮罩显示/收起 → 把进度写进去。
//  它**不认识任何预制体**，所以测试用现造的假遮罩就能把逻辑测透。
//
//  ⚠️ 订阅 / 退订是这里最该测的地方：`EventCenter.RemoveEventListener` 按**委托相等性**匹配，
//     如果回调没有存成字段、移除时现写一个方法组，**就会匹配不上、监听一直留着** ——
//     那正是审计里的 **P-05（订阅不退订）**。所以有专门一条 `Detach_StopsReacting`。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NBC.Framework;
using NBC.Framework.Asset;
using NBC.Framework.Scenes;
using NBC.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// A9 第三部分：加载遮罩控制器的测试。
    /// </summary>
    public sealed class LoadingMaskControllerTests
    {
        private const string CanvasLocation = "UI/Canvas";
        private const string MaskLocation = "UI/LoadingMask";

        private AssetManager m_assets;
        private FakeAssetProvider m_provider;
        private UIManager m_ui;
        private EventCenter m_events;
        private LoadingMaskController m_controller;

        private GameObject m_canvasPrefab;
        private readonly List<UnityEngine.Object> m_created = new List<UnityEngine.Object>();

        /// <summary>准备资源假实现、UIManager、事件中心、控制器。</summary>
        [SetUp]
        public void SetUp()
        {
            SingletonRegistry.ResetAll();

            m_assets = AssetManager.Instance;
            m_provider = new FakeAssetProvider();
            m_assets.SetProvider(m_provider);
            m_assets.InitializeAsync(AssetRuntimeMode.EditorSimulate).GetAwaiter().GetResult();

            m_ui = UIManager.Instance;
            m_events = EventCenter.Instance;

            m_canvasPrefab = CreateCanvasPrefab();
            m_controller = new LoadingMaskController(m_ui);
        }

        /// <summary>清理。</summary>
        [TearDown]
        public void TearDown()
        {
            if (m_controller != null)
            {
                m_controller.Dispose();
                m_controller = null;
            }

            if (UIManager.HasInstance)
            {
                UIManager.DisposeInstance();
            }

            for (int i = 0; i < m_created.Count; i++)
            {
                if (m_created[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(m_created[i]);
                }
            }

            m_created.Clear();
            SingletonRegistry.ResetAll();
        }

        // ====================================================================
        //  订阅 / 退订
        // ====================================================================

        /// <summary>没 `Attach` 之前，事件不该被响应。</summary>
        [Test]
        public void BeforeAttach_ProgressEventIsIgnored()
        {
            TriggerProgress(0.5f, "Level1");

            Assert.IsFalse(m_controller.IsMaskRequested, "还没订阅就不该有反应");
            Assert.AreEqual(0, m_provider.Operations.Count, "更不该去加载什么东西");
        }

        /// <summary>`Attach` 之后才响应。</summary>
        [Test]
        public void Attach_StartsReactingToProgress()
        {
            m_controller.Attach();

            TriggerProgress(0.5f, "Level1");

            Assert.IsTrue(m_controller.IsMaskRequested);
            Assert.AreEqual(0.5f, m_controller.LastProgress, 1e-4f);
        }

        /// <summary>
        /// **`Detach` 之后必须真的不再响应** —— 这条守的是 P-05（订阅不退订）。
        /// </summary>
        [Test]
        public void Detach_StopsReacting()
        {
            m_controller.Attach();
            m_controller.Detach();

            TriggerProgress(0.5f, "Level1");

            Assert.IsFalse(m_controller.IsMaskRequested, "退订之后不该再有反应");
            Assert.AreEqual(0, m_provider.Operations.Count);
        }

        /// <summary>重复 `Attach` 不会订阅两遍（否则一次事件会走两遍处理）。</summary>
        [Test]
        public void Attach_Twice_DoesNotDoubleSubscribe()
        {
            m_controller.Attach();
            m_controller.Attach();

            TriggerProgress(0.3f, "Level1");

            Assert.AreEqual(1, m_controller.ShowCount, "只该显示一次");
        }

        // ====================================================================
        //  进度 → 显示遮罩
        // ====================================================================

        /// <summary>收到进度时，在**最上层**请求遮罩，并带上当前进度。</summary>
        [Test]
        public void ProgressChanged_ShowsMaskOnSystemLayerWithProgress()
        {
            m_controller.Attach();

            TriggerProgress(0.42f, "Level1");
            CompleteMask();

            LoadingMaskPanel mask = m_ui.GetPanel<LoadingMaskPanel>(LoadingMaskController.DefaultPanelName);

            Assert.IsNotNull(mask, "遮罩应当已经显示");
            Assert.AreSame(m_ui.Layers.Get(UILayer.System), mask.transform.parent,
                "遮罩必须在最上层，否则会被别的面板盖住");
            Assert.AreEqual(0.42f, mask.CurrentProgress, 1e-4f, "显示时要把最新进度补上");
            StringAssert.Contains("Level1", mask.CurrentText);
        }

        /// <summary>遮罩已经显示时，后续进度直接更新，**不再重复请求显示**。</summary>
        [Test]
        public void ProgressChanged_WhenMaskVisible_UpdatesItInPlace()
        {
            m_controller.Attach();

            TriggerProgress(0.2f, "Level1");
            CompleteMask();

            int requestsAfterFirst = CountRequests(MaskLocation);

            TriggerProgress(0.8f, "Level1");

            LoadingMaskPanel mask = m_ui.GetPanel<LoadingMaskPanel>(LoadingMaskController.DefaultPanelName);

            Assert.AreEqual(0.8f, mask.CurrentProgress, 1e-4f, "进度要更新");
            Assert.AreEqual(requestsAfterFirst, CountRequests(MaskLocation),
                "已经显示了就不该再请求一次");
            Assert.AreEqual(1, m_controller.ShowCount);
        }

        /// <summary>遮罩预制体还在加载中时又来了新进度 —— 显示出来时要补上**最新**的那个。</summary>
        [Test]
        public void ProgressChanged_WhileMaskStillLoading_AppliesLatestOnShow()
        {
            m_controller.Attach();

            TriggerProgress(0.2f, "Level1");
            TriggerProgress(0.9f, "Level1");   // 遮罩还没加载完

            CompleteMask();

            LoadingMaskPanel mask = m_ui.GetPanel<LoadingMaskPanel>(LoadingMaskController.DefaultPanelName);

            Assert.IsNotNull(mask);
            Assert.AreEqual(0.9f, mask.CurrentProgress, 1e-4f,
                "遮罩出现时应当显示**最新**进度，而不是第一次那个");
            Assert.AreEqual(1, m_controller.ShowCount, "只该请求显示一次");
        }

        // ====================================================================
        //  成功 / 失败
        // ====================================================================

        /// <summary>加载成功 → 遮罩收起。</summary>
        [Test]
        public void LoadSucceeded_HidesMask()
        {
            m_controller.Attach();

            TriggerProgress(0.6f, "Level1");
            CompleteMask();

            m_events.Trigger(SceneEvents.LoadSucceeded, "Level1");

            Assert.IsFalse(m_ui.IsPanelVisible(LoadingMaskController.DefaultPanelName));
            Assert.AreEqual(1, m_controller.HideCount);
            Assert.IsFalse(m_controller.IsMaskRequested);
        }

        /// <summary>加载失败 → 遮罩**留着**并显示错误（让玩家看见出了什么事）。</summary>
        [Test]
        public void LoadFailed_KeepsMaskAndShowsError()
        {
            m_controller.Attach();

            TriggerProgress(0.6f, "Level1");
            CompleteMask();

            m_events.Trigger(SceneEvents.LoadFailed, new SceneFailureInfo("Level1", "下载超时"));

            LoadingMaskPanel mask = m_ui.GetPanel<LoadingMaskPanel>(LoadingMaskController.DefaultPanelName);

            Assert.IsNotNull(mask, "失败时遮罩不该消失");
            Assert.IsTrue(mask.HasError);
            StringAssert.Contains("下载超时", mask.CurrentText);
            Assert.AreEqual(0, m_controller.HideCount);
        }

        /// <summary>第一次加载就失败（遮罩还没出现过）→ 也要弹出来并写上错误。</summary>
        [Test]
        public void LoadFailed_BeforeAnyProgress_StillShowsMaskWithError()
        {
            m_controller.Attach();

            m_events.Trigger(SceneEvents.LoadFailed, new SceneFailureInfo("Level1", "包不存在"));
            CompleteMask();

            LoadingMaskPanel mask = m_ui.GetPanel<LoadingMaskPanel>(LoadingMaskController.DefaultPanelName);

            Assert.IsNotNull(mask);
            Assert.IsTrue(mask.HasError);
            StringAssert.Contains("包不存在", mask.CurrentText);
        }

        /// <summary>可以手动提前收起（游戏层想自己控制时机时用）。</summary>
        [Test]
        public void HideNow_HidesMaskManually()
        {
            m_controller.Attach();
            TriggerProgress(0.5f, "Level1");
            CompleteMask();

            Assert.IsTrue(m_controller.HideNow());
            Assert.IsFalse(m_ui.IsPanelVisible(LoadingMaskController.DefaultPanelName));
            Assert.IsFalse(m_controller.HideNow(), "已经收起了，再收一次应当返回 false");
        }

        // ====================================================================
        //  预制体挂错脚本：要说清楚
        // ====================================================================

        /// <summary>
        /// 遮罩预制体根节点挂的不是 `LoadingMaskPanel` —— 进度就传不进去。
        /// 这时必须**报一句能看懂的错**，而不是静默什么都不发生。
        /// </summary>
        [Test]
        public void MaskPrefabWithWrongPanelType_LogsActionableError()
        {
            LogAssert.Expect(LogType.Error, new Regex("LoadingMaskPanel"));

            m_controller.Attach();
            TriggerProgress(0.5f, "Level1");

            // 故意喂一个 ProbePanel（不是 LoadingMaskPanel）
            GameObject wrong = Track(new GameObject("LoadingMaskPrefab", typeof(RectTransform)));
            wrong.AddComponent<ProbePanel>();
            Complete(MaskLocation, wrong);

            LogAssert.NoUnexpectedReceived();
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>广播一次场景进度。</summary>
        /// <param name="progress">进度。</param>
        /// <param name="location">场景地址。</param>
        private void TriggerProgress(float progress, string location)
        {
            m_events.Trigger(SceneEvents.ProgressChanged, new SceneProgressInfo(location, progress));
        }

        /// <summary>喂给 Canvas 请求（按需）。</summary>
        private void CompleteCanvas()
        {
            if (!m_ui.IsCanvasReady && HasRequest(CanvasLocation))
            {
                Complete(CanvasLocation, m_canvasPrefab);
            }
        }

        /// <summary>喂一个"进度条 + 文字"齐全的遮罩预制体。</summary>
        private void CompleteMask()
        {
            CompleteCanvas();

            if (!HasRequest(MaskLocation) || OperationFor(MaskLocation).IsDone)
            {
                return;
            }

            GameObject prefab = Track(new GameObject("LoadingMaskPrefab", typeof(RectTransform)));
            prefab.AddComponent<LoadingMaskPanel>();

            GameObject bar = new GameObject(LoadingMaskPanel.ProgressBarControlName, typeof(RectTransform));
            bar.transform.SetParent(prefab.transform, false);
            bar.AddComponent<Image>();

            GameObject text = new GameObject(LoadingMaskPanel.ProgressTextControlName, typeof(RectTransform));
            text.transform.SetParent(prefab.transform, false);
            text.AddComponent<Text>();

            Complete(MaskLocation, prefab);
        }

        /// <summary>造一个四层齐全的 Canvas 预制体。</summary>
        /// <returns>预制体。</returns>
        private GameObject CreateCanvasPrefab()
        {
            GameObject canvas = Track(new GameObject("CanvasPrefab", typeof(RectTransform)));
            UILayers layers = canvas.AddComponent<UILayers>();

            layers.Bind(UILayer.Bot, CreateChild(canvas.transform, "Bot"));
            layers.Bind(UILayer.Mid, CreateChild(canvas.transform, "Mid"));
            layers.Bind(UILayer.Top, CreateChild(canvas.transform, "Top"));
            layers.Bind(UILayer.System, CreateChild(canvas.transform, "System"));

            return canvas;
        }

        /// <summary>建一个 UI 子节点。</summary>
        /// <param name="parent">父节点。</param>
        /// <param name="childName">名字。</param>
        /// <returns>子节点。</returns>
        private Transform CreateChild(Transform parent, string childName)
        {
            GameObject go = Track(new GameObject(childName, typeof(RectTransform)));
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        /// <summary>登记临时对象。</summary>
        /// <param name="obj">对象。</param>
        /// <returns>原对象。</returns>
        private GameObject Track(GameObject obj)
        {
            m_created.Add(obj);
            return obj;
        }

        /// <summary>有没有针对这个地址的请求。</summary>
        /// <param name="location">地址。</param>
        /// <returns>有没有。</returns>
        private bool HasRequest(string location)
        {
            return CountRequests(location) > 0;
        }

        /// <summary>数这个地址被请求过几次。</summary>
        /// <param name="location">地址。</param>
        /// <returns>次数。</returns>
        private int CountRequests(string location)
        {
            int count = 0;

            for (int i = 0; i < m_provider.Operations.Count; i++)
            {
                if (m_provider.Operations[i].Location == location)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>取某个地址的加载请求。</summary>
        /// <param name="location">地址。</param>
        /// <returns>请求。</returns>
        private FakeLoadOperation OperationFor(string location)
        {
            for (int i = 0; i < m_provider.Operations.Count; i++)
            {
                if (m_provider.Operations[i].Location == location)
                {
                    return m_provider.Operations[i];
                }
            }

            Assert.Fail("没有找到地址为 «" + location + "» 的加载请求。");
            return null;
        }

        /// <summary>完成某个地址的加载（已经完成就跳过）。</summary>
        /// <param name="location">地址。</param>
        /// <param name="asset">素材。</param>
        private void Complete(string location, UnityEngine.Object asset)
        {
            FakeLoadOperation operation = OperationFor(location);

            if (operation.IsDone)
            {
                return;
            }

            operation.Complete(asset);
        }
    }
}
