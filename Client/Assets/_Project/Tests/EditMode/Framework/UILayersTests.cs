// ============================================================================
//  M1-A9 · UILayers 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2
//  对应记录：Docs/06-框架改造记录.md §三 FW-10
//
//  ---------------------------------------------------------------------------
//  这个文件要回答的问题
//  ---------------------------------------------------------------------------
//  FW-10 的原版是 `canvas.Find("Bot")` —— **找不到时返回 null 且不报错**，
//  结果是"界面一片空白，Console 干干净净"。
//
//  所以这一组测试的核心不是"能不能取到层"，而是：
//    · **四种配置错误能不能被认出来**（漏填 / 两个层指同一个节点 / 拖了别人的节点 / 未知层）
//    · **报错信息里有没有点名指出是哪一个**（只说"配置错了"等于没说）
//
//  ⚠️ 报错文案也被断言了。这是刻意的：**能救命的不是"抛了异常"，
//     而是"异常里写着该去改哪里"**。
// ============================================================================

using System;
using NBC.Framework.UI;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// A9 第一步：UI 层引用持有者的测试。
    /// </summary>
    public sealed class UILayersTests
    {
        private GameObject m_canvas;
        private UILayers m_layers;
        private Transform m_bot;
        private Transform m_mid;
        private Transform m_top;
        private Transform m_system;

        /// <summary>搭一个"Canvas + 四个层"的结构，四个字段全部正确填好。</summary>
        [SetUp]
        public void SetUp()
        {
            m_canvas = new GameObject("MainCanvas", typeof(RectTransform));
            m_layers = m_canvas.AddComponent<UILayers>();

            m_bot = CreateChild(m_canvas.transform, "Bot");
            m_mid = CreateChild(m_canvas.transform, "Mid");
            m_top = CreateChild(m_canvas.transform, "Top");
            m_system = CreateChild(m_canvas.transform, "System");

            m_layers.Bind(UILayer.Bot, m_bot);
            m_layers.Bind(UILayer.Mid, m_mid);
            m_layers.Bind(UILayer.Top, m_top);
            m_layers.Bind(UILayer.System, m_system);
        }

        /// <summary>清理。</summary>
        [TearDown]
        public void TearDown()
        {
            if (m_canvas != null)
            {
                UnityEngine.Object.DestroyImmediate(m_canvas);
                m_canvas = null;
            }
        }

        // ====================================================================
        //  正常路径
        // ====================================================================

        /// <summary>四个层都填好时，取得到、且检查通过。</summary>
        [Test]
        public void AllLayersBound_IsValidAndRetrievable()
        {
            Assert.IsTrue(m_layers.IsValid, "四个层都填好了，应当有效");
            Assert.IsNull(m_layers.DescribeProblem(), "没有问题时应当返回 null");

            Assert.AreSame(m_bot, m_layers.Get(UILayer.Bot));
            Assert.AreSame(m_mid, m_layers.Get(UILayer.Mid));
            Assert.AreSame(m_top, m_layers.Get(UILayer.Top));
            Assert.AreSame(m_system, m_layers.Get(UILayer.System));
        }

        /// <summary>层数量常量与枚举的取值个数一致（防止加了层忘了改常量）。</summary>
        [Test]
        public void LayerCount_MatchesEnumValues()
        {
            int enumCount = Enum.GetValues(typeof(UILayer)).Length;

            Assert.AreEqual(enumCount, UILayers.LayerCount,
                "UILayers.LayerCount 与 UILayer 的取值个数不一致");
        }

        /// <summary>名字稳定（报错文案靠它）。</summary>
        [Test]
        public void GetLayerName_ReturnsStableNames()
        {
            Assert.AreEqual("Bot", UILayers.GetLayerName(UILayer.Bot));
            Assert.AreEqual("Mid", UILayers.GetLayerName(UILayer.Mid));
            Assert.AreEqual("Top", UILayers.GetLayerName(UILayer.Top));
            Assert.AreEqual("System", UILayers.GetLayerName(UILayer.System));
        }

        // ====================================================================
        //  错误一：漏填
        // ====================================================================

        /// <summary>漏填一层时，报错**点名说是哪一层**。</summary>
        [Test]
        public void MissingLayer_IsReportedByItsName()
        {
            m_layers.Bind(UILayer.Mid, null);

            string problem = m_layers.DescribeProblem();

            Assert.IsNotNull(problem, "Mid 是空的，必须被检查出来");
            Assert.IsTrue(problem.Contains("Mid"),
                "报错必须点名 «Mid»，否则用户不知道该去填哪一格。实际文案：" + problem);
            Assert.IsFalse(m_layers.IsValid);
        }

        /// <summary>取一个没填的层：抛异常，且消息里带同样的指引。</summary>
        [Test]
        public void Get_OnMissingLayer_ThrowsWithActionableMessage()
        {
            m_layers.Bind(UILayer.System, null);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => m_layers.Get(UILayer.System));

            Assert.IsTrue(ex.Message.Contains("System"),
                "异常消息里必须点名 «System»。实际：" + ex.Message);
        }

        /// <summary>`TryGet` 是"不抛异常"的那条路，供调用方自己决定怎么处理。</summary>
        [Test]
        public void TryGet_OnMissingLayer_ReturnsFalseWithoutThrowing()
        {
            m_layers.Bind(UILayer.Top, null);

            // ⚠️ 必须显式初始化：`result` 是在 lambda 里通过 out 赋值的，
            // 编译器**不认** lambda 内的赋值算作"确定赋值"（CS0165）。
            Transform result = null;
            bool ok = false;

            Assert.DoesNotThrow(() => { ok = m_layers.TryGet(UILayer.Top, out result); });

            Assert.IsFalse(ok);
            Assert.IsNull(result);
        }

        // ====================================================================
        //  错误二：两个层指同一个节点
        // ====================================================================

        /// <summary>两个层指向同一个节点会被查出来（这会让层级管理失效）。</summary>
        [Test]
        public void TwoLayersOnSameNode_IsReported()
        {
            m_layers.Bind(UILayer.Top, m_mid);

            string problem = m_layers.DescribeProblem();

            Assert.IsNotNull(problem, "Top 和 Mid 指到了同一个节点，必须被检查出来");
            Assert.IsTrue(problem.Contains("Top") && problem.Contains("Mid"),
                "报错要把**两个**层名都说出来。实际：" + problem);
        }

        // ====================================================================
        //  错误三：拖了别的 Canvas 的节点
        // ====================================================================

        /// <summary>
        /// 拖了不属于这个 Canvas 的节点 —— 后果是面板被挂到别的 Canvas 下、位置和层级都错。
        /// </summary>
        [Test]
        public void LayerOutsideThisCanvas_IsReported()
        {
            GameObject otherCanvas = new GameObject("OtherCanvas", typeof(RectTransform));

            try
            {
                Transform foreign = CreateChild(otherCanvas.transform, "Bot");
                m_layers.Bind(UILayer.Bot, foreign);

                string problem = m_layers.DescribeProblem();

                Assert.IsNotNull(problem, "Bot 指向了别的 Canvas 的子节点，必须被检查出来");
                Assert.IsTrue(problem.Contains("Bot"), "报错要点名 «Bot»。实际：" + problem);

                Transform result;
                Assert.IsFalse(m_layers.TryGet(UILayer.Bot, out result),
                    "不属于本 Canvas 的节点，TryGet 也应当判为不可用");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(otherCanvas);
            }
        }

        // ====================================================================
        //  错误四：未知层
        // ====================================================================

        /// <summary>未知层当场抛异常，而不是静默返回 null。</summary>
        [Test]
        public void UnknownLayer_Throws()
        {
            UILayer bogus = (UILayer)99;

            Assert.Throws<ArgumentOutOfRangeException>(() => m_layers.Bind(bogus, m_bot),
                "Bind 遇到野值应当抛异常");

            Assert.Throws<ArgumentOutOfRangeException>(() => m_layers.Get(bogus),
                "Get 遇到野值应当抛异常");

            // ⚠️ 这一条曾经是红的：第一版 `GetLayerName` 里写了个兜底
            // `return "Unknown(99)"`，把这个野值悄悄变成一个看着正常的字符串。
            // 兜底字符串正是本项目最讨厌的静默行为 —— 假名字会一路流进日志。
            Assert.Throws<ArgumentOutOfRangeException>(() => UILayers.GetLayerName(bogus),
                "GetLayerName 遇到野值也必须抛异常，不能返回兜底字符串");
        }

        /// <summary>
        /// **区分两种"不行"**，这是这一组里最该记住的一条：
        /// <list type="bullet">
        /// <item><description>层是合法的、只是**没配好** → `TryGet` 返回 false（配置问题，不炸）</description></item>
        /// <item><description>层**根本不存在** → 抛异常（编程错误，必须炸）</description></item>
        /// </list>
        /// </summary>
        [Test]
        public void TryGet_DistinguishesUnconfiguredLayerFromNonexistentLayer()
        {
            m_layers.Bind(UILayer.Mid, null);

            // ⚠️ out 参数写在 lambda 里时，编译器不认它是"确定赋值"（CS0165），
            // 所以下面两个变量必须先初始化。
            Transform configured = null;
            Transform bogus = null;

            Assert.DoesNotThrow(
                () => { m_layers.TryGet(UILayer.Mid, out configured); },
                "层是合法的、只是没填 —— 这是配置问题，TryGet 不该抛异常");

            Assert.Throws<ArgumentOutOfRangeException>(
                () => { m_layers.TryGet((UILayer)99, out bogus); },
                "层根本不存在 —— 这是编程错误，必须响亮地报出来");
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>建一个带 RectTransform 的子节点（UI 节点必须带它）。</summary>
        /// <param name="parent">父节点。</param>
        /// <param name="childName">名字。</param>
        /// <returns>子节点。</returns>
        private static Transform CreateChild(Transform parent, string childName)
        {
            GameObject go = new GameObject(childName, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.transform;
        }
    }
}
