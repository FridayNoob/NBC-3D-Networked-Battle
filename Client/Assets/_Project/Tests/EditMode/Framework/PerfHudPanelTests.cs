// ============================================================================
//  M1-E1 · PerfHudPanel 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md E1（屏幕上可见；开关可切）
//
//  ---------------------------------------------------------------------------
//  这组测试盯的两件事
//  ---------------------------------------------------------------------------
//  **① 所有显示控件都是可选的。**
//     预制体上想显示哪几项就放哪几个 `Text`，少放、一个都不放，都不能报错。
//     HUD 长什么样是调试需求决定的，不该逼一套固定布局。
//     （和 A9 的 `LoadingMaskPanel` 同款思路。）
//
//  **② 文字刷新必须限频。**
//     `Text.text = ...` 每次都分配字符串。每帧刷 4 个文本 = 每秒 240 次分配 ——
//     **一个用来观测 GC 的看板自己去制造 GC**，那就本末倒置。
//     所以"采样每帧做、文字限频刷"这条要有测试钉住。
// ============================================================================

using System.Collections.Generic;
using NBC.Framework.Perf;
using NBC.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// E1：性能看板面板的测试。
    /// </summary>
    public sealed class PerfHudPanelTests
    {
        private GameObject m_root;
        private PerfHudPanel m_panel;
        private FakePerfCounterSource m_counterSource;
        private PerfSampler m_sampler;

        /// <summary>建一个"五项文本齐全"的看板，并注入假计数器。</summary>
        [SetUp]
        public void SetUp()
        {
            m_root = new GameObject("PerfHud", typeof(RectTransform));
            m_panel = m_root.AddComponent<PerfHudPanel>();

            m_counterSource = new FakePerfCounterSource();
            m_counterSource.Values[PerfCounterId.DrawCalls] = 42f;
            m_counterSource.Values[PerfCounterId.GcAllocatedInFrame] = 2048f;
            m_counterSource.Values[PerfCounterId.TotalMemory] = 100f * 1024f * 1024f;

            m_sampler = new PerfSampler(m_counterSource);

            // ⚠️ 必须在 Initialize 之前注入：EditMode 下面板不会自己建采样器
            m_panel.AttachSampler(m_sampler);
        }

        /// <summary>清理。</summary>
        [TearDown]
        public void TearDown()
        {
            if (m_panel != null)
            {
                m_panel.ReleaseSampler();
            }

            if (m_root != null)
            {
                UnityEngine.Object.DestroyImmediate(m_root);
                m_root = null;
            }
        }

        // ====================================================================
        //  一、控件都是可选的
        // ====================================================================

        /// <summary>一个控件都没挂，也不能报错。</summary>
        [Test]
        public void WithoutAnyTextControl_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => m_panel.Initialize());
            Assert.DoesNotThrow(() => m_panel.Tick(1f / 60f));
            Assert.DoesNotThrow(() => m_panel.Refresh());
        }

        /// <summary>只挂一部分控件 —— 挂上的刷新，没挂的跳过。</summary>
        [Test]
        public void PartialControls_OnlyRefreshWhatExists()
        {
            Text fpsText = CreateTextChild(PerfHudPanel.FpsControlName);

            m_panel.Initialize();
            m_panel.Tick(1f / 60f);
            m_panel.Refresh();

            StringAssert.Contains("FPS", fpsText.text);
        }

        /// <summary>五项都挂上时，每一项都写到对应的文本里。</summary>
        [Test]
        public void AllControls_ReceiveTheirValues()
        {
            Text fpsText = CreateTextChild(PerfHudPanel.FpsControlName);
            Text drawCallText = CreateTextChild(PerfHudPanel.DrawCallControlName);
            Text gcText = CreateTextChild(PerfHudPanel.GcAllocControlName);
            Text memoryText = CreateTextChild(PerfHudPanel.MemoryControlName);
            Text detailText = CreateTextChild(PerfHudPanel.DetailControlName);

            m_panel.Initialize();

            for (int i = 0; i < 60; i++)
            {
                m_panel.Tick(1f / 60f);
            }

            m_panel.Refresh();

            StringAssert.Contains("FPS", fpsText.text);
            StringAssert.Contains("42", drawCallText.text);
            StringAssert.Contains("KB", gcText.text, "GC 分配应当被格式化成人话");
            StringAssert.Contains("MB", memoryText.text);
            StringAssert.Contains("DC", detailText.text);
        }

        // ====================================================================
        //  二、文字刷新限频（看板自己不该造 GC）
        // ====================================================================

        /// <summary>
        /// **采样每帧做，文字不每帧刷。**
        /// <para>默认 0.25 秒刷一次；几帧之内不该触发刷新。</para>
        /// </summary>
        [Test]
        public void TextRefresh_IsThrottled()
        {
            m_panel.Initialize();

            int afterInitialize = m_panel.RefreshCount;

            // 连刷 5 帧（约 0.083 秒）—— 离 0.25 秒还差得远
            for (int i = 0; i < 5; i++)
            {
                m_panel.Tick(1f / 60f);
            }

            Assert.AreEqual(afterInitialize, m_panel.RefreshCount,
                "限频之内不该刷新文字（否则看板自己就成了 GC 来源）");
            Assert.AreEqual(5L, m_sampler.TickCount, "但**采样**必须每帧都做");
        }

        /// <summary>累计够了一个间隔才刷。</summary>
        [Test]
        public void TextRefresh_HappensAfterInterval()
        {
            m_panel.Initialize();

            int afterInitialize = m_panel.RefreshCount;

            // 0.3 秒 > 默认间隔 0.25 秒
            m_panel.Tick(0.3f);

            Assert.AreEqual(afterInitialize + 1, m_panel.RefreshCount);
        }

        /// <summary>把间隔设成 0 → 每帧都刷（**不推荐**，但要有这条路）。</summary>
        [Test]
        public void ZeroInterval_RefreshesEveryTick()
        {
            m_panel.RefreshInterval = 0f;
            m_panel.Initialize();

            int afterInitialize = m_panel.RefreshCount;

            for (int i = 0; i < 10; i++)
            {
                m_panel.Tick(1f / 60f);
            }

            Assert.AreEqual(afterInitialize + 10, m_panel.RefreshCount);
        }

        /// <summary>`Refresh` 是"立即刷"，不看限频。</summary>
        [Test]
        public void Refresh_IgnoresThrottle()
        {
            m_panel.Initialize();

            int before = m_panel.RefreshCount;
            m_panel.Refresh();

            Assert.AreEqual(before + 1, m_panel.RefreshCount);
        }

        /// <summary>
        /// 没配过时，刷新间隔应当等于代码里的默认值。
        /// <para>
        /// ⚠️ 这条守的是"默认值"这件事本身：`m_refreshInterval` 现在是**序列化字段**
        /// （为了能在 Inspector 里调），而它的初值来自 `DefaultRefreshInterval` 常量。
        /// 两个数一旦不同步，就会出现"新挂的组件刷新速度和老的不一样"这种鬼故事。
        /// </para>
        /// </summary>
        [Test]
        public void DefaultRefreshInterval_IsUsedWhenNothingConfigured()
        {
            GameObject bare = new GameObject("BareHud", typeof(RectTransform));
            PerfHudPanel barePanel = bare.AddComponent<PerfHudPanel>();

            try
            {
                Assert.AreEqual(PerfHudPanel.DefaultRefreshInterval, barePanel.RefreshInterval,
                    "新建的看板应当用代码里的默认刷新间隔");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bare);
            }
        }

        /// <summary>刷新间隔不能被设成负数（那会让判断反过来）。</summary>
        [Test]
        public void NegativeInterval_IsClampedToZero()
        {
            m_panel.RefreshInterval = -5f;

            Assert.AreEqual(0f, m_panel.RefreshInterval);
        }

        // ====================================================================
        //  三、采样器的接法
        // ====================================================================

        /// <summary>`LastSnapshot` 直接反映采样器的最新结果（**即使还没刷到文本上**）。</summary>
        [Test]
        public void LastSnapshot_ReflectsSamplerImmediately()
        {
            m_panel.Initialize();

            m_panel.Tick(1f / 60f);

            Assert.AreEqual(60f, m_panel.LastSnapshot.Fps, 0.5f);
            Assert.AreEqual(42f, m_panel.LastSnapshot.DrawCalls);
        }

        /// <summary>注入进来的采样器由注入方负责释放，面板不该把它 Dispose 掉。</summary>
        [Test]
        public void InjectedSampler_IsNotDisposedByPanel()
        {
            m_panel.Initialize();
            m_panel.ReleaseSampler();

            // 采样器还能用 —— 说明面板没把它销毁
            Assert.DoesNotThrow(() => m_sampler.Tick(1f / 60f));
        }

        /// <summary>
        /// **EditMode 下面板不自己建采样器**。
        /// <para>
        /// 因为 Profiler 在 EditMode 没跑，`ProfilerRecorder` 既没值又会打噪音日志，
        /// 而测试里任何一条没被声明的日志都会让用例失败。
        /// </para>
        /// </summary>
        [Test]
        public void InEditMode_NoSamplerIsAutoCreated()
        {
            GameObject bare = new GameObject("BareHud", typeof(RectTransform));
            PerfHudPanel barePanel = bare.AddComponent<PerfHudPanel>();

            try
            {
                barePanel.Initialize();

                Assert.IsNull(barePanel.Sampler,
                    "EditMode 下不该自动建采样器（Profiler 没在跑，只会打噪音日志）");
                Assert.DoesNotThrow(() => barePanel.Tick(1f / 60f), "没有采样器也不能崩");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bare);
            }
        }

        /// <summary>释放之后再 `Tick` 不能崩（面板可能被隐藏着还收到驱动）。</summary>
        [Test]
        public void TickAfterRelease_DoesNotThrow()
        {
            m_panel.Initialize();
            m_panel.ReleaseSampler();

            Assert.DoesNotThrow(() => m_panel.Tick(1f / 60f));
            Assert.IsNull(m_panel.Sampler);
        }

        // ====================================================================
        //  四、读不到时显示 N/A（不能显示 0）
        // ====================================================================

        /// <summary>计数器不可用时，文本里应当是 `N/A` 而不是 `0`。</summary>
        [Test]
        public void UnavailableCounters_ShowNaInText()
        {
            m_counterSource.Values.Clear();

            Text drawCallText = CreateTextChild(PerfHudPanel.DrawCallControlName);

            m_panel.Initialize();
            m_panel.Tick(1f / 60f);
            m_panel.Refresh();

            StringAssert.Contains("N/A", drawCallText.text);
            StringAssert.DoesNotContain("DC 0", drawCallText.text,
                "不可用显示成 0 就是在骗人");
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>在面板下建一个带 `Text` 的子节点。</summary>
        /// <param name="childName">节点名（要和面板认识的控件名一致）。</param>
        /// <returns>文本控件。</returns>
        private Text CreateTextChild(string childName)
        {
            GameObject child = new GameObject(childName, typeof(RectTransform));
            child.transform.SetParent(m_root.transform, false);

            return child.AddComponent<Text>();
        }
    }
}
