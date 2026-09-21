// ============================================================================
//  M1-A9 · BasePanel 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2
//  对应记录：Docs/06-框架改造记录.md §四 P-08
//
//  ---------------------------------------------------------------------------
//  这个文件要回答的问题
//  ---------------------------------------------------------------------------
//  P-08 的原版把初始化和"找控件"写在 `Awake` 里，于是：
//      子类写了个 `Awake` 忘调 `base.Awake()` → **所有按钮失灵，而且不报错**。
//
//  所以核心是两件事：
//    ① **框架根本不依赖 `Awake`** —— 有一条反射断言机械守着这件事
//       （`BasePanel_DoesNotDeclareAwake`），比"记得别写 Awake"可靠
//    ② **名字打错时当场报错、并列出可用的名字** —— 而不是等用它的时候才崩
//
//  ⚠️ 为什么这些能在 EditMode 跑：因为初始化入口是**公开方法** `Initialize()`，
//     不是 Unity 回调。**EditMode 下 `Awake` 根本不会被调用**（见 Docs/06 §9.7），
//     如果框架依赖 `Awake`，这组测试**一条都跑不起来**。
//     反过来说：这组测试能跑，本身就是"不依赖 Awake"的证据。
// ============================================================================

using System;
using System.Reflection;
using NBC.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// 测试用的面板。**故意声明一个 `Awake`**，模拟"子类乱写 Awake"的场景。
    /// </summary>
    internal sealed class ProbePanel : BasePanel
    {
        /// <summary>`OnInit` 被调用了几次。</summary>
        public int InitCount;

        /// <summary>最后一次被点的按钮名。</summary>
        public string LastClicked;

        /// <summary>按钮被点了几次。</summary>
        public int ClickCount;

        /// <summary>最后一次变化的勾选框名。</summary>
        public string LastToggleName;

        /// <summary>最后一次勾选框的值。</summary>
        public bool LastToggleValue;

        /// <summary>
        /// ⚠️ **故意的**：子类自己声明 `Awake`，而且**没有** `base.Awake()` 可调。
        /// 原版框架在这里就崩了（控件全空、按钮全哑），改造后应当毫无影响。
        /// </summary>
        private void Awake()
        {
        }

        /// <summary>记录初始化次数。</summary>
        protected override void OnInit()
        {
            InitCount++;
        }

        /// <summary>记录点击。</summary>
        /// <param name="buttonName">按钮名。</param>
        protected override void OnClick(string buttonName)
        {
            LastClicked = buttonName;
            ClickCount++;
        }

        /// <summary>记录勾选变化。</summary>
        /// <param name="toggleName">勾选框名。</param>
        /// <param name="value">新值。</param>
        protected override void OnValueChanged(string toggleName, bool value)
        {
            LastToggleName = toggleName;
            LastToggleValue = value;
        }

        /// <summary>把受保护的取控件接口暴露给测试。</summary>
        /// <typeparam name="T">控件类型。</typeparam>
        /// <param name="controlName">控件名。</param>
        /// <returns>控件；没有则 null。</returns>
        public T Peek<T>(string controlName) where T : UIBehaviour
        {
            return GetControl<T>(controlName);
        }

        /// <summary>把受保护的"必须有"接口暴露给测试。</summary>
        /// <typeparam name="T">控件类型。</typeparam>
        /// <param name="controlName">控件名。</param>
        /// <returns>控件。</returns>
        public T Demand<T>(string controlName) where T : UIBehaviour
        {
            return RequireControl<T>(controlName);
        }
    }

    /// <summary>
    /// A9 第一步：面板基类的测试。
    /// </summary>
    public sealed class BasePanelTests
    {
        private GameObject m_root;
        private ProbePanel m_panel;

        /// <summary>搭一个面板根节点。</summary>
        [SetUp]
        public void SetUp()
        {
            m_root = new GameObject("ShopPanel", typeof(RectTransform));
            m_panel = m_root.AddComponent<ProbePanel>();
        }

        /// <summary>清理。</summary>
        [TearDown]
        public void TearDown()
        {
            if (m_root != null)
            {
                UnityEngine.Object.DestroyImmediate(m_root);
                m_root = null;
            }
        }

        // ====================================================================
        //  框架不依赖 Awake（P-08 的机械保证）
        // ====================================================================

        /// <summary>
        /// **机械断言**：`BasePanel` 里不许有 `Awake`。
        /// <para>
        /// 这条比"记得别写 Awake"可靠：一旦有人（包括我）把初始化挪回 `Awake`，
        /// 它当场变红。理由见 P-08 —— Unity 按名字反射调用**最派生**的那个 `Awake`，
        /// C# 的 `sealed` / `private` **挡不住**子类再声明一个同名的。
        /// </para>
        /// </summary>
        [Test]
        public void BasePanel_DoesNotDeclareAwake()
        {
            MethodInfo awake = typeof(BasePanel).GetMethod(
                "Awake",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            Assert.IsNull(awake,
                "BasePanel 不该声明 Awake。框架的初始化必须由 UIManager 显式调用 Initialize() —— " +
                "否则子类再写一个 Awake 就会把初始化整个顶掉（P-08），而且不报错。");
        }

        /// <summary>
        /// **子类乱写 `Awake` 也不影响初始化** —— 这就是 P-08 的修复证据。
        /// <para>（`ProbePanel` 里确实声明了一个什么都不做的 `Awake`。）</para>
        /// </summary>
        [Test]
        public void Initialize_WorksEvenWhenSubclassDeclaresAwake()
        {
            CreateControl("BtnStart", typeof(Image), typeof(Button));

            m_panel.Initialize();

            Assert.IsTrue(m_panel.IsInitialized);
            Assert.IsNotNull(m_panel.Peek<Button>("BtnStart"),
                "子类声明了 Awake，控件索引仍然必须正常工作");
            Assert.AreEqual(1, m_panel.InitCount, "OnInit 应当被调用");
        }

        // ====================================================================
        //  收集控件
        // ====================================================================

        /// <summary>按 GameObject 名字建索引，深层嵌套也能找到。</summary>
        [Test]
        public void CollectControls_IndexesByNameIncludingDeepNesting()
        {
            CreateControl("BtnStart", typeof(Image), typeof(Button));

            Transform deep = CreateNode(m_root.transform, "Level1");
            deep = CreateNode(deep, "Level2");
            CreateControl(deep, "ImgIcon", typeof(Image));

            m_panel.Initialize();

            Assert.IsNotNull(m_panel.Peek<Button>("BtnStart"), "浅层控件应当被找到");
            Assert.IsNotNull(m_panel.Peek<Image>("ImgIcon"), "深层嵌套的控件也应当被找到");
        }

        /// <summary>
        /// **一次扫描的顺带红利**：原版只扫 7 种类型（Button/Image/Text/Toggle/Slider/
        /// ScrollRect/InputField），**其他 UI 组件根本进不了索引**。
        /// 现在扫的是基类 `UIBehaviour`，所以 `Scrollbar` 这类也能取到。
        /// </summary>
        [Test]
        public void CollectControls_IndexesUiTypesTheOriginalSevenScansMissed()
        {
            CreateControl("ScrollBarHp", typeof(Scrollbar));

            m_panel.Initialize();

            Assert.IsNotNull(m_panel.Peek<Scrollbar>("ScrollBarHp"),
                "原版只扫固定 7 种类型，Scrollbar 取不到；现在应当能取到");
        }

        /// <summary>同一个名字下挂多个 UI 组件时，按类型各取各的（原版的 List 分桶语义保留）。</summary>
        [Test]
        public void CollectControls_MergesMultipleComponentsUnderOneName()
        {
            CreateControl("BtnStart", typeof(Image), typeof(Button));

            m_panel.Initialize();

            Image image = m_panel.Peek<Image>("BtnStart");
            Button button = m_panel.Peek<Button>("BtnStart");

            Assert.IsNotNull(image, "同名下的 Image 应当取得到");
            Assert.IsNotNull(button, "同名下的 Button 应当取得到");
            Assert.AreNotSame(image, button, "两个不同类型的组件应当是两个对象");

            Assert.AreEqual(2, m_panel.ControlCount, "同名下的两个组件都应当被记入");
        }

        /// <summary>面板根节点自己身上的 UI 组件也应当被收集。</summary>
        [Test]
        public void CollectControls_IncludesRootItself()
        {
            Image rootImage = m_root.AddComponent<Image>();

            m_panel.Initialize();

            Assert.AreSame(rootImage, m_panel.Peek<Image>("ShopPanel"),
                "根节点自己的组件也应当在索引里");
        }

        // ====================================================================
        //  初始化语义
        // ====================================================================

        /// <summary>重复 `Initialize` 是安全的（面板要进对象池反复用）。</summary>
        [Test]
        public void Initialize_IsIdempotent()
        {
            CreateControl("BtnStart", typeof(Image), typeof(Button));

            m_panel.Initialize();
            m_panel.Initialize();
            m_panel.Initialize();

            Assert.AreEqual(1, m_panel.InitCount, "OnInit 只该被调用一次");
        }

        /// <summary>
        /// 重复 `Initialize` **不该把按钮监听接第二遍** ——
        /// 否则按钮点一下会走两次 `OnClick`（这类毛病在真实项目里很常见）。
        /// </summary>
        [Test]
        public void Initialize_DoesNotDoubleWireButton()
        {
            Button button = CreateControl("BtnStart", typeof(Image), typeof(Button)).GetComponent<Button>();

            m_panel.Initialize();
            m_panel.Initialize();

            button.onClick.Invoke();

            Assert.AreEqual(1, m_panel.ClickCount, "按钮点一次只该回调一次");
        }

        // ====================================================================
        //  自动接线
        // ====================================================================

        /// <summary>按钮点击自动路由到 `OnClick`，并带上 GameObject 名字。</summary>
        [Test]
        public void ButtonClick_RoutesToOnClickWithGameObjectName()
        {
            Button button = CreateControl("BtnBuy", typeof(Image), typeof(Button)).GetComponent<Button>();

            m_panel.Initialize();
            button.onClick.Invoke();

            Assert.AreEqual(1, m_panel.ClickCount);
            Assert.AreEqual("BtnBuy", m_panel.LastClicked,
                "回调要带上名字 —— 一个 OnClick 要能分辨是哪个按钮");
        }

        /// <summary>勾选框变化自动路由到 `OnValueChanged`。</summary>
        [Test]
        public void ToggleChange_RoutesToOnValueChanged()
        {
            Toggle toggle = CreateControl("TogAuto", typeof(Toggle)).GetComponent<Toggle>();

            m_panel.Initialize();
            toggle.onValueChanged.Invoke(true);

            Assert.AreEqual("TogAuto", m_panel.LastToggleName);
            Assert.IsTrue(m_panel.LastToggleValue);
        }

        // ====================================================================
        //  取控件：静默 vs 响亮
        // ====================================================================

        /// <summary>`Peek` 找不到时返回 null（与原版行为一致）。</summary>
        [Test]
        public void Peek_ReturnsNullForUnknownName()
        {
            CreateControl("BtnStart", typeof(Image), typeof(Button));
            m_panel.Initialize();

            Assert.IsNull(m_panel.Peek<Button>("BtnTypo"), "找不到时返回 null，与原版一致");
            Assert.IsNull(m_panel.Peek<Button>(null), "null 名字也应当安全返回 null");
        }

        /// <summary>
        /// `Demand` 找不到时**当场抛异常，并列出这个面板里所有可用的控件名**。
        /// <para>这是"名字打错 → 后面才崩"的修法：错误出现在写错的那一行，
        /// 而且不用去翻预制体就知道该改成什么。</para>
        /// </summary>
        [Test]
        public void Demand_ThrowsAndListsAvailableNames()
        {
            CreateControl("BtnStart", typeof(Image), typeof(Button));
            CreateControl("ImgIcon", typeof(Image));
            m_panel.Initialize();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => m_panel.Demand<Button>("BtnTypo"));

            Assert.IsTrue(ex.Message.Contains("BtnTypo"),
                "消息里要带上写错的那个名字。实际：" + ex.Message);
            Assert.IsTrue(ex.Message.Contains("BtnStart"),
                "消息里要列出**可用的**控件名，用户才知道该改成什么。实际：" + ex.Message);
            Assert.IsTrue(ex.Message.Contains("Button"),
                "消息里要点出**在找什么类型**。实际：" + ex.Message);
        }

        /// <summary>控件名大小写敏感（写清楚，免得踩）。</summary>
        [Test]
        public void Demand_IsCaseSensitive()
        {
            CreateControl("BtnStart", typeof(Image), typeof(Button));
            m_panel.Initialize();

            Assert.IsNotNull(m_panel.Demand<Button>("BtnStart"));
            Assert.Throws<InvalidOperationException>(() => m_panel.Demand<Button>("btnstart"));
        }

        /// <summary>`Demand` 找到时正常返回。</summary>
        [Test]
        public void Demand_ReturnsControlWhenFound()
        {
            Button button = CreateControl("BtnStart", typeof(Image), typeof(Button)).GetComponent<Button>();
            m_panel.Initialize();

            Assert.AreSame(button, m_panel.Demand<Button>("BtnStart"));
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>建一个 UI 节点并可挂若干组件。</summary>
        /// <param name="controlName">节点名。</param>
        /// <param name="componentTypes">要挂的组件类型。</param>
        /// <returns>节点。</returns>
        private GameObject CreateControl(string controlName, params Type[] componentTypes)
        {
            return CreateControl(m_root.transform, controlName, componentTypes);
        }

        /// <summary>在指定父节点下建一个 UI 节点。</summary>
        /// <param name="parent">父节点。</param>
        /// <param name="controlName">节点名。</param>
        /// <param name="componentTypes">要挂的组件类型。</param>
        /// <returns>节点。</returns>
        private static GameObject CreateControl(Transform parent, string controlName, params Type[] componentTypes)
        {
            GameObject go = new GameObject(controlName, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            for (int i = 0; i < componentTypes.Length; i++)
            {
                go.AddComponent(componentTypes[i]);
            }

            return go;
        }

        /// <summary>建一个纯节点（不挂 UI 组件）。</summary>
        /// <param name="parent">父节点。</param>
        /// <param name="nodeName">名字。</param>
        /// <returns>节点。</returns>
        private static Transform CreateNode(Transform parent, string nodeName)
        {
            GameObject go = new GameObject(nodeName, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.transform;
        }
    }
}
