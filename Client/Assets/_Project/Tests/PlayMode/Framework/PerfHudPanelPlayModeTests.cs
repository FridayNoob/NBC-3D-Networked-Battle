// ============================================================================
//  M1-E1 · PerfHudPanel 的 PlayMode 测试
//  对应验收：Docs/16-M1开工清单.md E1（屏幕上可见；开关可切）
//
//  ---------------------------------------------------------------------------
//  为什么这几条**必须**放 PlayMode（不能塞进 EditMode 那一组）
//  ---------------------------------------------------------------------------
//  ① **自动建采样器这条路只有在播放模式才走得到。**
//     EditMode 下 `Application.isPlaying` 是 false，面板故意不建采样器
//     （Profiler 没在跑，`ProfilerRecorder` 只会打噪音日志）。
//     所以"面板不注入也能自己活起来"这件事，**EditMode 里根本测不到**。
//
//  ② **"每帧被驱动"是一个播放模式才存在的现象。**
//     面板靠 `MonoManager` 的每帧回调推进 —— EditMode 连 `Awake` 都不调，
//     更不会有帧循环。在 EditMode 里断言"它每帧刷新"是在自欺。
//
//  ③ 顺带验"销毁面板会把每帧回调摘干净" —— `OnDestroy` 也是播放模式才有的事。
//
//  ⚠️ 所以 E1 显示侧的完整证据 = **EditMode 13 条（纯逻辑） + PlayMode 这 7 条（生命周期）**。
//     只看其中一边都会漏一半。
// ============================================================================

using System.Collections;
using NBC.Framework;
using NBC.Framework.Perf;
using NBC.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace NBC.Tests.PlayMode
{
    /// <summary>
    /// E1：性能看板面板在播放模式下的测试。
    /// </summary>
    public class PerfHudPanelPlayModeTests
    {
        private GameObject m_root;
        private PerfHudPanel m_panel;

        /// <summary>测试开始前清空每帧回调，让"挂上去几个"有一个干净的基线。</summary>
        [SetUp]
        public void SetUp()
        {
            MonoManager.Instance.RemoveAllListeners();
        }

        /// <summary>收尾：销毁面板（走一遍 `OnDestroy`），再清空监听。</summary>
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (m_root != null)
            {
                UnityEngine.Object.Destroy(m_root);
                m_root = null;
            }

            yield return null;

            if (MonoManager.HasInstance)
            {
                MonoManager.Instance.RemoveAllListeners();
            }
        }

        // ====================================================================
        //  一、不注入也能自己活起来
        // ====================================================================

        /// <summary>播放模式下，面板应当**自己**建出一个真采样器。</summary>
        [UnityTest]
        public IEnumerator AutoCreatesSamplerInPlayMode()
        {
            PerfHudPanel panel = CreatePanel(PerfHudPanel.FpsControlName);

            panel.Initialize();

            Assert.IsNotNull(panel.Sampler, "播放模式下应当自动建采样器");
            Assert.IsInstanceOf<ProfilerRecorderCounterSource>(panel.Sampler.Source,
                "真机上应当用 Profiler 计数器，而不是测试用的假货");

            yield return null;
        }

        /// <summary>
        /// **直接摆在场景里**的看板要自己活起来（不靠 `UIManager`）。
        /// <para>
        /// 这条守的是"排查问题时不想先修资源系统"：把面板拖进场景、进播放模式，
        /// 就该看见数字 —— 而不是先跑通 YooAsset 打包。
        /// </para>
        /// </summary>
        [UnityTest]
        public IEnumerator ScenePlacedPanel_BootstrapsItself()
        {
            // ⚠️ 故意**不**手动 Initialize / ShowMe —— 模拟"直接拖进场景"
            PerfHudPanel panel = CreatePanel(PerfHudPanel.FpsControlName);

            yield return null;
            yield return null;

            Assert.IsTrue(panel.IsInitialized, "摆在场景里的看板应当自己初始化");
            Assert.Greater(panel.LastSnapshot.FrameCount, 0L, "并且自己接上每帧驱动");
        }

        /// <summary>没有任何显示控件时，整块面板照样能跑。</summary>
        [UnityTest]
        public IEnumerator WithoutAnyControl_StillRuns()
        {
            PerfHudPanel panel = CreatePanel();

            panel.RefreshInterval = 0f;
            panel.Initialize();
            panel.ShowMe();

            yield return null;
            yield return null;

            Assert.Greater(panel.LastSnapshot.FrameCount, 0L, "没有文本控件也要照常采样");
        }

        // ====================================================================
        //  二、显示时被每帧驱动
        // ====================================================================

        /// <summary>`ShowMe()` 之后，每帧都会被驱动。</summary>
        [UnityTest]
        public IEnumerator ShowMe_DrivesEveryFrame()
        {
            PerfHudPanel panel = CreatePanel(PerfHudPanel.FpsControlName);

            panel.RefreshInterval = 0f;
            panel.Initialize();

            int refreshBeforeShow = panel.RefreshCount;

            panel.ShowMe();

            yield return null;
            yield return null;
            yield return null;

            Assert.Greater(panel.LastSnapshot.FrameCount, 0L,
                "`ShowMe()` 之后应当每帧都在采样");
            Assert.Greater(panel.RefreshCount, refreshBeforeShow,
                "刷新间隔为 0 时，每帧都应当把文字刷一遍");
        }

        /// <summary>重复 `ShowMe()` 不该把回调挂两遍。</summary>
        [UnityTest]
        public IEnumerator ShowMe_TwiceAddsOnlyOneListener()
        {
            PerfHudPanel panel = CreatePanel(PerfHudPanel.FpsControlName);

            panel.Initialize();
            panel.ShowMe();
            panel.ShowMe();
            panel.ShowMe();

            yield return null;

            Assert.AreEqual(1, MonoManager.Instance.UpdateListenerCount,
                "重复 ShowMe 只能有一条每帧回调（否则采样会被推进好几倍）");
        }

        // ====================================================================
        //  三、隐藏 / 销毁
        // ====================================================================

        /// <summary>`HideMe()` 之后不再被驱动，但**采样器留着**。</summary>
        [UnityTest]
        public IEnumerator HideMe_StopsDrivingButKeepsSampler()
        {
            PerfHudPanel panel = CreatePanel(PerfHudPanel.FpsControlName);

            panel.Initialize();
            panel.ShowMe();

            yield return null;
            yield return null;

            panel.HideMe();

            // 等一帧，让"排在这一帧里的回调"先跑完，再取基线
            yield return null;

            long baseline = panel.LastSnapshot.FrameCount;

            yield return null;
            yield return null;

            Assert.AreEqual(baseline, panel.LastSnapshot.FrameCount,
                "`HideMe()` 之后不该再推进采样");
            Assert.IsNotNull(panel.Sampler, "采样器要留着，再显示时能接着用");
            Assert.AreEqual(0, MonoManager.Instance.UpdateListenerCount);
        }

        /// <summary>隐藏之后再显示，应当继续跑（而不是坏了）。</summary>
        [UnityTest]
        public IEnumerator HideThenShow_ResumesDriving()
        {
            PerfHudPanel panel = CreatePanel(PerfHudPanel.FpsControlName);

            panel.Initialize();
            panel.ShowMe();

            yield return null;

            panel.HideMe();
            yield return null;

            long baseline = panel.LastSnapshot.FrameCount;

            panel.ShowMe();

            yield return null;
            yield return null;

            Assert.Greater(panel.LastSnapshot.FrameCount, baseline,
                "再显示之后应当继续采样");
        }

        /// <summary>销毁面板时，**每帧回调和采样器都必须被摘干净**。</summary>
        [UnityTest]
        public IEnumerator Destroy_RemovesListenerAndReleasesSampler()
        {
            PerfHudPanel panel = CreatePanel(PerfHudPanel.FpsControlName);

            panel.Initialize();
            panel.ShowMe();

            yield return null;

            Assert.AreEqual(1, MonoManager.Instance.UpdateListenerCount);

            UnityEngine.Object.Destroy(m_root);
            m_root = null;

            yield return null;

            Assert.AreEqual(0, MonoManager.Instance.UpdateListenerCount,
                "面板销毁了却还挂着每帧回调 —— 那是每帧一次的泄漏");
        }

        // ====================================================================
        //  四、屏幕上真的有字（这是"显示侧"的正面证据）
        // ====================================================================

        /// <summary>跑几帧之后，文本控件里应当有**真的数字**。</summary>
        [UnityTest]
        public IEnumerator TextShowsRealNumbers()
        {
            PerfHudPanel panel = CreatePanel(
                PerfHudPanel.FpsControlName,
                PerfHudPanel.DetailControlName);

            Text fpsText = FindText(PerfHudPanel.FpsControlName);
            Text detailText = FindText(PerfHudPanel.DetailControlName);

            panel.RefreshInterval = 0f;
            panel.Initialize();
            panel.ShowMe();

            for (int i = 0; i < 5; i++)
            {
                yield return null;
            }

            Assert.Greater(panel.LastSnapshot.FrameCount, 0L, "看板应当在每帧被驱动");
            Assert.Greater(panel.LastSnapshot.Fps, 0f,
                "播放模式下每一帧都应当拿到正的耗时（否则说明帧循环没跑起来）");

            StringAssert.Contains("FPS", fpsText.text);
            StringAssert.Contains("FPS", detailText.text);
            StringAssert.Contains("DC", detailText.text);
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>建一块看板，并按名字挂上若干文本控件。</summary>
        /// <param name="textControlNames">要挂的文本控件名（可以为空）。</param>
        /// <returns>面板。</returns>
        private PerfHudPanel CreatePanel(params string[] textControlNames)
        {
            m_root = new GameObject("PerfHud", typeof(RectTransform));
            m_panel = m_root.AddComponent<PerfHudPanel>();

            for (int i = 0; i < textControlNames.Length; i++)
            {
                GameObject child = new GameObject(textControlNames[i], typeof(RectTransform));
                child.transform.SetParent(m_root.transform, false);
                child.AddComponent<Text>();
            }

            return m_panel;
        }

        /// <summary>取看板下某个名字的文本控件。</summary>
        /// <param name="controlName">控件名。</param>
        /// <returns>文本控件。</returns>
        private Text FindText(string controlName)
        {
            Transform found = m_root.transform.Find(controlName);

            Assert.IsNotNull(found, "没找到叫 «" + controlName + "» 的子节点");
            return found.GetComponent<Text>();
        }
    }
}
