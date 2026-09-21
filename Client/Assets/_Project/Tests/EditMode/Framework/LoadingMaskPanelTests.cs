// ============================================================================
//  M1-A9 · LoadingMaskPanel 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2
//
//  ---------------------------------------------------------------------------
//  这组测试的重点：**两个控件都是可选的**
//  ---------------------------------------------------------------------------
//  遮罩长什么样是美术决定的：可能只有进度条、可能只有文字、也可能两个都有。
//  所以这里用的是 `GetControl`（找不到返回 null）而不是 `RequireControl`（找不到抛异常）——
//  **这是框架里少数故意不 fail-fast 的地方**，所以必须有测试把"缺控件也能跑"钉住，
//  否则以后有人"顺手"改成 `RequireControl`，美术换个预制体就崩。
// ============================================================================

using NBC.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// A9 第三部分：加载遮罩面板的测试。
    /// </summary>
    public sealed class LoadingMaskPanelTests
    {
        private GameObject m_root;
        private LoadingMaskPanel m_mask;
        private Image m_bar;
        private Text m_text;

        /// <summary>搭一个"进度条 + 文字"都有的遮罩。</summary>
        [SetUp]
        public void SetUp()
        {
            m_root = new GameObject("LoadingMask", typeof(RectTransform));
            m_mask = m_root.AddComponent<LoadingMaskPanel>();

            GameObject bar = new GameObject(LoadingMaskPanel.ProgressBarControlName, typeof(RectTransform));
            bar.transform.SetParent(m_root.transform, false);
            m_bar = bar.AddComponent<Image>();

            GameObject text = new GameObject(LoadingMaskPanel.ProgressTextControlName, typeof(RectTransform));
            text.transform.SetParent(m_root.transform, false);
            m_text = text.AddComponent<Text>();

            m_mask.Initialize();
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

        /// <summary>进度会同时写到进度条的 `fillAmount` 和文字上。</summary>
        [Test]
        public void SetProgress_UpdatesBarAndText()
        {
            m_mask.SetProgress(0.42f, "Level1");

            Assert.AreEqual(0.42f, m_bar.fillAmount, 1e-4f);
            Assert.AreEqual(0.42f, m_mask.CurrentProgress, 1e-4f);
            Assert.AreEqual("Level1", m_mask.CurrentLocation);
            StringAssert.Contains("Level1", m_text.text);
            StringAssert.Contains("42", m_text.text);
            Assert.IsFalse(m_mask.HasError);
        }

        /// <summary>进度会被钳到 0..1（后端给出越界值也不该把界面搞坏）。</summary>
        [Test]
        public void SetProgress_ClampsToUnitRange()
        {
            m_mask.SetProgress(5f, null);
            Assert.AreEqual(1f, m_mask.CurrentProgress, 1e-4f);

            m_mask.SetProgress(-3f, null);
            Assert.AreEqual(0f, m_mask.CurrentProgress, 1e-4f);
        }

        /// <summary>没有地址时文字照样能看。</summary>
        [Test]
        public void SetProgress_WithoutLocation_StillProducesReadableText()
        {
            m_mask.SetProgress(0.5f, null);

            StringAssert.Contains("50", m_text.text);
            Assert.IsTrue(m_mask.CurrentLocation == null);
        }

        /// <summary>
        /// **一个控件都没有也不许崩** —— 遮罩样式是美术决定的，
        /// 只有背景图的遮罩也是合法预制体。
        /// </summary>
        [Test]
        public void SetProgress_WithoutAnyOptionalControl_DoesNotThrow()
        {
            GameObject bare = new GameObject("BareMask", typeof(RectTransform));
            LoadingMaskPanel bareMask = bare.AddComponent<LoadingMaskPanel>();

            try
            {
                bareMask.Initialize();

                Assert.DoesNotThrow(() => bareMask.SetProgress(0.3f, "Level1"));
                Assert.DoesNotThrow(() => bareMask.SetError("炸了"));

                Assert.AreEqual(0.3f, bareMask.CurrentProgress, 1e-4f);
                Assert.IsTrue(bareMask.HasError);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bare);
            }
        }

        /// <summary>错误状态要能被置上，而且**下一次设置进度会清掉它**。</summary>
        [Test]
        public void SetError_MarksError_AndNextProgressClearsIt()
        {
            m_mask.SetError("磁盘读不出来");

            Assert.IsTrue(m_mask.HasError);
            StringAssert.Contains("磁盘读不出来", m_text.text);
            StringAssert.Contains("加载失败", m_text.text);

            m_mask.SetProgress(0.1f, "Level2");

            Assert.IsFalse(m_mask.HasError, "重新开始加载时错误状态要清掉");
            Assert.IsFalse(m_text.text.Contains("加载失败"));
        }
    }
}
