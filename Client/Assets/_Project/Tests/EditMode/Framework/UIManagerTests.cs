// ============================================================================
//  M1-A9 · UIManager 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2
//  对应记录：Docs/06-框架改造记录.md §三 FW-10、§四 P-09 / P-11 / P-12
//
//  ---------------------------------------------------------------------------
//  这组测试怎么做到"确定性地"测异步加载
//  ---------------------------------------------------------------------------
//  复用 B2 就有的 `FakeAssetProvider`（见 `AssetManagerTests.cs`）：
//  它不会真的去加载，而是把每次 `LoadAsync` 记成一个 `FakeLoadOperation`，
//  由测试**手动** `Complete(素材)` 决定"什么时候加载完、加载到什么"。
//
//  于是"加载中"这个状态可以被**精确地停在某一刻**去断言 ——
//  这是真实资源系统做不到的，也是 B2 那条依赖倒置设计的回报。
//
//  ⚠️ 本组测试**全部用回调入口（`ShowPanel`）**，不用 `ShowPanelAsync`。
//     原因见 `UIManager.cs` 文件头：`await` 在 Unity 里会把续体投递回同步上下文，
//     而 **EditMode 没有帧循环去跑那个队列**。回调是在完成那一刻**直接调用**的，
//     所以确定性能保证。`ShowPanelAsync` 有单独一条用例验证它也能用。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NBC.Framework;
using NBC.Framework.Asset;
using NBC.Framework.UI;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// A9 第二部分：UI 管理器的测试。
    /// </summary>
    public sealed class UIManagerTests
    {
        private AssetManager m_assets;
        private FakeAssetProvider m_provider;
        private UIManager m_ui;
        private GameObject m_canvasPrefab;

        private readonly List<UnityEngine.Object> m_created = new List<UnityEngine.Object>();

        private BasePanel m_shown;
        private Exception m_failure;

        /// <summary>重置单例、装上假加载器、准备一个正确的 Canvas 预制体。</summary>
        [SetUp]
        public void SetUp()
        {
            SingletonRegistry.ResetAll();

            m_assets = AssetManager.Instance;
            m_provider = new FakeAssetProvider();
            m_assets.SetProvider(m_provider);
            m_assets.InitializeAsync(AssetRuntimeMode.EditorSimulate).GetAwaiter().GetResult();

            m_ui = UIManager.Instance;
            m_canvasPrefab = CreateCanvasPrefab(true);
        }

        /// <summary>清理单例与临时对象。</summary>
        [TearDown]
        public void TearDown()
        {
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
        //  正常路径
        // ====================================================================

        /// <summary>第一次显示：先加载 Canvas，再加载面板，然后实例化并显示。</summary>
        [Test]
        public void ShowPanel_LoadsCanvasThenPanel_AndShowsIt()
        {
            RequestShow("BagPanel");

            // 此刻只发出了 Canvas 的加载请求 —— 面板的还没发（因为 Canvas 还没好）
            Assert.IsFalse(m_ui.IsCanvasReady);
            Assert.AreEqual(1, m_ui.LoadingPanelCount, "面板应当处于「加载中」");
            Assert.IsTrue(HasRequest("UI/Canvas"));

            CompleteCanvas();

            // Canvas 好了之后，面板的加载请求才发出来
            Assert.IsTrue(m_ui.IsCanvasReady);
            Assert.IsTrue(HasRequest("UI/BagPanel"), "Canvas 就绪后应当紧接着请求面板预制体");

            CompletePanel("BagPanel");

            Assert.IsNull(m_failure);
            Assert.IsNotNull(m_shown, "应当成功拿到面板");
            Assert.IsTrue(m_ui.IsPanelVisible("BagPanel"));
            Assert.AreEqual(1, m_ui.VisiblePanelCount);
            Assert.AreEqual(0, m_ui.LoadingPanelCount, "加载完就该从「加载中」里摘掉");
            Assert.IsNotNull(m_ui.GetPanel<ProbePanel>("BagPanel"));
        }

        /// <summary>面板要挂到请求的那一层下面。</summary>
        [Test]
        public void ShowPanel_ParentsPanelToRequestedLayer()
        {
            RequestShow("BagPanel", UILayer.Top);
            CompleteCanvas();
            CompletePanel("BagPanel");

            Transform expected = m_ui.Layers.Get(UILayer.Top);

            Assert.AreSame(expected, m_shown.transform.parent,
                "面板必须挂到请求的那一层下面 —— 挂错层就等于层级管理失效");
        }

        /// <summary>面板的 RectTransform 要被铺满（否则位置和大小都是乱的）。</summary>
        [Test]
        public void ShowPanel_StretchesPanelRect()
        {
            RequestShow("BagPanel");
            CompleteCanvas();
            CompletePanel("BagPanel");

            RectTransform rect = (RectTransform)m_shown.transform;

            Assert.AreEqual(Vector3.zero, rect.localPosition);
            Assert.AreEqual(Vector3.one, rect.localScale);
            Assert.AreEqual(Vector2.zero, rect.offsetMax);
            Assert.AreEqual(Vector2.zero, rect.offsetMin);
        }

        /// <summary>已显示的面板再请求一次：只调 `ShowMe`，不重新加载。</summary>
        [Test]
        public void ShowPanel_WhenAlreadyVisible_DoesNotReload()
        {
            BasePanel first = RequestAndShow("BagPanel");
            int operationsAfterFirstShow = m_provider.Operations.Count;

            BasePanel second = RequestAndShow("BagPanel");

            Assert.AreSame(first, second, "应当是同一条实例");
            Assert.AreEqual(operationsAfterFirstShow, m_provider.Operations.Count,
                "已经显示了就不该再发加载请求");
        }

        // ====================================================================
        //  语义①：同一个面板名同时只有一条加载任务（P-09 的修法）
        // ====================================================================

        /// <summary>
        /// **加载中再请求一次，要挂到同一条上**，而不是再加载一遍。
        /// <para>原版没有这个保护：同帧连点两下 = 加载两遍 + `panelDic.Add` 抛重复键。</para>
        /// </summary>
        [Test]
        public void ShowPanel_WhileLoading_HooksOntoSameLoad()
        {
            BasePanel first = null;
            BasePanel second = null;

            m_ui.ShowPanel("BagPanel", UILayer.Mid, p => first = p, e => m_failure = e);
            CompleteCanvas();

            // 面板正在加载中，这时再请求一次
            Assert.AreEqual(1, m_ui.LoadingPanelCount);
            m_ui.ShowPanel("BagPanel", UILayer.Mid, p => second = p, e => m_failure = e);

            Assert.AreEqual(1, m_ui.LoadingPanelCount, "第二次请求不该另起一条加载");
            Assert.AreEqual(1, CountRequests("UI/BagPanel"), "面板预制体只该被请求一次");

            CompletePanel("BagPanel");

            Assert.IsNull(m_failure);
            Assert.IsNotNull(first, "第一次的回调要收到");
            Assert.IsNotNull(second, "第二次的回调也要收到");
            Assert.AreSame(first, second, "两次请求应当拿到同一个面板实例");
        }

        // ====================================================================
        //  加载失败 / 配置错误：必须报出来，而且要能看懂
        // ====================================================================

        /// <summary>Canvas 预制体上没挂 `UILayers` —— 报错要告诉人怎么修。</summary>
        [Test]
        public void ShowPanel_CanvasWithoutUILayers_ReportsActionableError()
        {
            GameObject badCanvas = CreateCanvasPrefab(false);

            RequestShow("BagPanel");
            Complete("UI/Canvas", badCanvas);

            Assert.IsNotNull(m_failure);
            StringAssert.Contains("UILayers", m_failure.Message);
            StringAssert.Contains("根节点", m_failure.Message, "报错要说清挂在哪");
            Assert.IsFalse(m_ui.IsCanvasReady);
        }

        /// <summary>Canvas 上的层没配好 —— 报错要点名是哪一层（复用 `UILayers.DescribeProblem`）。</summary>
        [Test]
        public void ShowPanel_CanvasWithBadLayerConfig_ReportsTheLayerName()
        {
            GameObject badCanvas = CreateCanvasPrefab(true);
            badCanvas.GetComponent<UILayers>().Bind(UILayer.Mid, null);

            RequestShow("BagPanel");
            Complete("UI/Canvas", badCanvas);

            Assert.IsNotNull(m_failure);
            StringAssert.Contains("Mid", m_failure.Message, "必须点名是 «Mid» 没配");
            Assert.IsFalse(m_ui.IsCanvasReady);
        }

        /// <summary>面板预制体根节点上没挂面板脚本。</summary>
        [Test]
        public void ShowPanel_PrefabWithoutPanelScript_ReportsError()
        {
            GameObject bare = Track(new GameObject("BarePrefab", typeof(RectTransform)));

            RequestShow("BagPanel");
            CompleteCanvas();
            Complete("UI/BagPanel", bare);

            Assert.IsNotNull(m_failure);
            StringAssert.Contains("BasePanel", m_failure.Message);
            StringAssert.Contains("根节点", m_failure.Message);
            Assert.IsFalse(m_ui.IsPanelVisible("BagPanel"));
        }

        /// <summary>
        /// **P-12**：根节点不是 `RectTransform` 时，原版是静默拿到 null 然后 NRE；
        /// 这里必须**当场报错并说清常见原因**。
        /// </summary>
        [Test]
        public void ShowPanel_PrefabRootNotRectTransform_ReportsError()
        {
            // ⚠️ 故意用普通 GameObject（Transform，不是 RectTransform）
            GameObject notUi = Track(new GameObject("NotUiPrefab"));
            notUi.AddComponent<ProbePanel>();

            RequestShow("BagPanel");
            CompleteCanvas();
            Complete("UI/BagPanel", notUi);

            Assert.IsNotNull(m_failure, "根节点类型不对必须当场报错，而不是等到用的时候 NRE");
            StringAssert.Contains("RectTransform", m_failure.Message);
        }

        /// <summary>加载失败时把提供方的错误原因带出来。</summary>
        [Test]
        public void ShowPanel_LoadFailure_CarriesProviderError()
        {
            RequestShow("BagPanel");
            CompleteCanvas();

            OperationFor("UI/BagPanel").Fail("磁盘上找不到这个包");

            Assert.IsNotNull(m_failure);
            StringAssert.Contains("磁盘上找不到这个包", m_failure.Message);
            StringAssert.Contains("BagPanel", m_failure.Message);
            Assert.IsFalse(m_ui.IsPanelVisible("BagPanel"));
            Assert.AreEqual(0, m_ui.VisiblePanelCount, "失败之后不该变成「显示中」");
            Assert.AreEqual(0, m_ui.LoadingPanelCount, "失败之后也不该还挂在「加载中」");
        }

        // ====================================================================
        //  隐藏 = 回池，不销毁（P-09 的结构性修法）
        // ====================================================================

        /// <summary>
        /// **P-09 的核心证据**：`HidePanel` 之后面板**对象还在**，只是被收起来了。
        /// </summary>
        [Test]
        public void HidePanel_PutsPanelIntoPoolWithoutDestroyingIt()
        {
            BasePanel panel = RequestAndShow("BagPanel");
            GameObject instance = panel.gameObject;

            bool hidden = m_ui.HidePanel("BagPanel");

            Assert.IsTrue(hidden);
            Assert.AreEqual(0, m_ui.VisiblePanelCount);
            Assert.AreEqual(1, m_ui.PooledPanelCount, "应当进了池");
            Assert.IsTrue(instance != null, "面板对象必须还在 —— 「收起来」不是「销毁」");
            Assert.IsFalse(instance.activeSelf, "收起来时要停用");
        }

        /// <summary>收起来再显示：**复用同一个实例，不重新加载**。</summary>
        [Test]
        public void HidePanel_ThenShow_ReusesSameInstanceWithoutReloading()
        {
            BasePanel first = RequestAndShow("BagPanel");
            m_ui.HidePanel("BagPanel");

            int requestsBefore = CountRequests("UI/BagPanel");

            BasePanel second = RequestAndShow("BagPanel");

            Assert.AreSame(first, second, "应当复用同一个实例");
            Assert.AreEqual(requestsBefore, CountRequests("UI/BagPanel"),
                "从池里拿的不该再发加载请求");
            Assert.IsTrue(second.gameObject.activeSelf);
        }

        /// <summary>复用时会**重新挂到这次请求的层**上（层可能和上次不一样）。</summary>
        [Test]
        public void HidePanel_ThenShowOnAnotherLayer_Reparents()
        {
            BasePanel panel = RequestAndShow("BagPanel", UILayer.Mid);
            m_ui.HidePanel("BagPanel");

            BasePanel again = RequestAndShow("BagPanel", UILayer.Top);

            Assert.AreSame(panel, again);
            Assert.AreSame(m_ui.Layers.Get(UILayer.Top), again.transform.parent,
                "复用时必须重新挂到新请求的层");
        }

        /// <summary>隐藏一个没显示的面板：返回 false，不抛异常。</summary>
        [Test]
        public void HidePanel_UnknownPanel_ReturnsFalse()
        {
            Assert.IsFalse(m_ui.HidePanel("NoSuchPanel"));
            Assert.IsFalse(m_ui.HidePanel(null));
            Assert.IsFalse(m_ui.HidePanel(string.Empty));
        }

        // ====================================================================
        //  关闭 = 真销毁
        // ====================================================================

        /// <summary>`ClosePanel` 才是真的关掉：对象销毁，下次显示要重新加载。</summary>
        [Test]
        public void ClosePanel_DestroysAndForcesReloadNextTime()
        {
            BasePanel panel = RequestAndShow("BagPanel");
            GameObject instance = panel.gameObject;

            Assert.IsTrue(m_ui.ClosePanel("BagPanel"));

            Assert.IsTrue(instance == null, "关闭必须真的销毁对象");
            Assert.AreEqual(0, m_ui.VisiblePanelCount);
            Assert.AreEqual(0, m_ui.PooledPanelCount);

            int requestsBefore = CountRequests("UI/BagPanel");

            BasePanel reloaded = RequestAndShow("BagPanel");

            Assert.AreNotSame(panel, reloaded);
            Assert.AreEqual(requestsBefore + 1, CountRequests("UI/BagPanel"),
                "关掉之后应当重新加载");
        }

        /// <summary>`CloseAll` 把显示中的和池里的一起清掉。</summary>
        [Test]
        public void CloseAll_ClearsVisibleAndPooled()
        {
            RequestAndShow("BagPanel");
            RequestAndShow("ShopPanel");
            m_ui.HidePanel("BagPanel");

            Assert.AreEqual(1, m_ui.VisiblePanelCount);
            Assert.AreEqual(1, m_ui.PooledPanelCount);

            m_ui.CloseAll();

            Assert.AreEqual(0, m_ui.VisiblePanelCount);
            Assert.AreEqual(0, m_ui.PooledPanelCount);
            Assert.AreEqual(0, m_ui.StackDepth);
        }

        // ====================================================================
        //  UI 栈
        // ====================================================================

        /// <summary>只有**正在显示**的面板才能压栈（否则返回键会失灵）。</summary>
        [Test]
        public void PushPanel_RequiresThePanelToBeVisible()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => m_ui.PushPanel("BagPanel"));

            StringAssert.Contains("BagPanel", ex.Message);
            StringAssert.Contains("没有在显示", ex.Message);
        }

        /// <summary>压栈 / 出栈：出栈会把栈顶面板隐藏掉。</summary>
        [Test]
        public void PopPanel_HidesTheTopPanel()
        {
            RequestAndShow("BagPanel");
            RequestAndShow("ShopPanel");
            m_ui.PushPanel("BagPanel");
            m_ui.PushPanel("ShopPanel");

            Assert.AreEqual(2, m_ui.StackDepth);
            Assert.AreEqual("ShopPanel", m_ui.PeekPanel());

            string popped = m_ui.PopPanel();

            Assert.AreEqual("ShopPanel", popped);
            Assert.IsFalse(m_ui.IsPanelVisible("ShopPanel"), "出栈的面板应当被收起来");
            Assert.AreEqual(1, m_ui.PooledPanelCount);
            Assert.IsTrue(m_ui.IsPanelVisible("BagPanel"), "下面那个不该被动");
            Assert.AreEqual(1, m_ui.StackDepth);
        }

        /// <summary>同一个面板重复压栈不会在栈里出现两次。</summary>
        [Test]
        public void PushPanel_SamePanelTwice_AppearsOnce()
        {
            RequestAndShow("BagPanel");
            m_ui.PushPanel("BagPanel");
            m_ui.PushPanel("BagPanel");

            Assert.AreEqual(1, m_ui.StackDepth);
        }

        /// <summary>栈里残留着"已经被别处关掉"的面板时，出栈要跳过它。</summary>
        [Test]
        public void PopPanel_SkipsPanelsAlreadyClosed()
        {
            RequestAndShow("BagPanel");
            RequestAndShow("ShopPanel");
            m_ui.PushPanel("BagPanel");
            m_ui.PushPanel("ShopPanel");

            m_ui.ClosePanel("ShopPanel");   // 绕过栈直接关掉

            string popped = m_ui.PopPanel();

            Assert.AreEqual("BagPanel", popped, "应当跳过已经关掉的那个，而不是返回 null");
        }

        /// <summary>栈空时返回键"没人接手"。</summary>
        [Test]
        public void HandleBack_ReturnsFalseWhenStackIsEmpty()
        {
            Assert.IsFalse(m_ui.HandleBack());
            Assert.IsNull(m_ui.PopPanel());
        }

        // ====================================================================
        //  配置 / 便利接口
        // ====================================================================

        /// <summary>Canvas 已经开始加载后就不该再改地址（改了没意义）。</summary>
        [Test]
        public void SetCanvasLocation_AfterLoadStarted_Throws()
        {
            RequestShow("BagPanel");
            CompleteCanvas();

            Assert.Throws<InvalidOperationException>(() => m_ui.SetCanvasLocation("UI/Other"));
        }

        /// <summary>`ShowPanelAsync` 也能用（内部把回调包成了任务）。</summary>
        [Test]
        public void ShowPanelAsync_CompletesWithThePanel()
        {
            Task<BasePanel> task = m_ui.ShowPanelAsync("BagPanel");

            CompleteCanvas();
            CompletePanel("BagPanel");

            Assert.IsTrue(task.IsCompleted, "回调触发时任务当场就该完成");

            BasePanel panel = task.GetAwaiter().GetResult();
            Assert.IsNotNull(panel);
            Assert.IsTrue(m_ui.IsPanelVisible("BagPanel"));
        }

        /// <summary>
        /// **销毁管理器时，还在等加载的调用方必须收到失败，而不是永远等下去。**
        /// </summary>
        [Test]
        public void Dispose_CancelsPendingLoadsInsteadOfHanging()
        {
            RequestShow("BagPanel");
            CompleteCanvas();

            Assert.AreEqual(1, m_ui.LoadingPanelCount);

            UIManager.DisposeInstance();

            Assert.IsNotNull(m_failure, "等着的调用方必须收到失败通知，否则会永远挂住");
            StringAssert.Contains("已销毁", m_failure.Message);
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>发起一次显示请求，结果记在 <see cref="m_shown"/> / <see cref="m_failure"/>。</summary>
        /// <param name="panelName">面板名。</param>
        /// <param name="layer">层。</param>
        private void RequestShow(string panelName, UILayer layer = UILayer.Mid)
        {
            m_shown = null;
            m_failure = null;

            m_ui.ShowPanel(panelName, layer, panel => m_shown = panel, error => m_failure = error);
        }

        /// <summary>请求 → 喂 Canvas → 喂面板，一条龙。返回拿到的面板。</summary>
        /// <param name="panelName">面板名。</param>
        /// <param name="layer">层。</param>
        /// <returns>面板。</returns>
        private BasePanel RequestAndShow(string panelName, UILayer layer = UILayer.Mid)
        {
            RequestShow(panelName, layer);
            CompleteCanvas();
            CompletePanel(panelName);

            Assert.IsNull(m_failure, "不该失败：" + (m_failure == null ? "" : m_failure.Message));
            Assert.IsNotNull(m_shown, "应当拿到面板");

            return m_shown;
        }

        /// <summary>喂给 Canvas 加载请求（已经好了就跳过）。</summary>
        private void CompleteCanvas()
        {
            if (!m_ui.IsCanvasReady && HasRequest("UI/Canvas"))
            {
                Complete("UI/Canvas", m_canvasPrefab);
            }
        }

        /// <summary>喂给面板加载请求：现场造一个"预制体"（带面板脚本 + RectTransform）。</summary>
        /// <param name="panelName">面板名。</param>
        private void CompletePanel(string panelName)
        {
            GameObject prefab = Track(new GameObject(panelName + "Prefab", typeof(RectTransform)));
            prefab.AddComponent<ProbePanel>();

            Complete("UI/" + panelName, prefab);
        }

        /// <summary>造一个 Canvas 预制体；`withLayers` 为 false 时故意不挂组件。</summary>
        /// <param name="withLayers">是否挂 UILayers 并填好四层。</param>
        /// <returns>预制体。</returns>
        private GameObject CreateCanvasPrefab(bool withLayers)
        {
            GameObject canvas = Track(new GameObject("CanvasPrefab", typeof(RectTransform)));

            if (!withLayers)
            {
                return canvas;
            }

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

        /// <summary>登记一个临时对象，TearDown 时统一销毁。</summary>
        /// <param name="obj">对象。</param>
        /// <returns>原对象。</returns>
        private GameObject Track(GameObject obj)
        {
            m_created.Add(obj);
            return obj;
        }

        /// <summary>有没有针对这个地址的、还没完成的加载请求。</summary>
        /// <param name="location">地址。</param>
        /// <returns>有没有。</returns>
        private bool HasRequest(string location)
        {
            for (int i = 0; i < m_provider.Operations.Count; i++)
            {
                if (m_provider.Operations[i].Location == location)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>数一数针对这个地址发出过几次请求。</summary>
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

        /// <summary>取针对这个地址的加载请求（取不到就让测试失败，并列出实际有哪些）。</summary>
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

            Assert.Fail("没有找到地址为 «" + location + "» 的加载请求。实际有：" + DescribeRequests());
            return null;
        }

        /// <summary>完成针对某个地址的加载。</summary>
        /// <param name="location">地址。</param>
        /// <param name="asset">素材。</param>
        private void Complete(string location, UnityEngine.Object asset)
        {
            OperationFor(location).Complete(asset);
        }

        /// <summary>把已发出的请求列成一句人话（报错用）。</summary>
        /// <returns>描述。</returns>
        private string DescribeRequests()
        {
            if (m_provider.Operations.Count == 0)
            {
                return "（一个都没有）";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();

            for (int i = 0; i < m_provider.Operations.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(m_provider.Operations[i].Location);
            }

            return builder.ToString();
        }
    }
}
