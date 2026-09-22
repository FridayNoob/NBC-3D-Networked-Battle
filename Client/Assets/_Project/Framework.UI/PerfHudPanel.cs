// ============================================================================
//  NBC.Framework.UI · 性能看板面板
//  对应需求：FW-M12（PerfHUD）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 一个必须处理的细节：看板自己不该成为 GC 的来源
//  ---------------------------------------------------------------------------
//  `Text.text = ...` 每次赋值都会**分配字符串**。
//  如果每帧刷 4 个文本，那就是**每秒 240 次字符串分配** ——
//  一个用来观测 GC 分配的看板，自己去制造 GC 分配，**本末倒置**。
//
//  所以：**采样每帧都做，但文字刷新限频**（默认 0.25 秒一次，即 4 Hz）。
//  人眼看数字也跟不上 60 Hz，限频既不损失可读性，又把分配量降下来。
//
//  ⚠️ 降多少要说准确（按 60 FPS 算，4 个文本）：
//      每帧刷：4 × 60 = **240 次/秒**
//      4 Hz ：4 ×  4 =  **16 次/秒**
//      → **少 15 倍**。说"两个数量级"是吹牛，实测口径就是一个数量级多一点。
//
//  ⚠️ 还有一条同样重要：**控件不存在时不要去拼那个字符串**。
//     `SetText(m_fpsText, Fps.ToString() + " FPS")` 这种写法，C# 会**先把参数算出来**，
//     哪怕 `m_fpsText` 是 null —— 于是"没挂的控件"照样在分配。
//     所以每个都先判空，再拼。
//
//  ⚠️ 还有一条：用 `Time.unscaledDeltaTime` 而不是 `deltaTime`。
//     看板要在 `timeScale = 0`（暂停）时也能正常显示 —— 恰恰是暂停时最需要看它。
//
//  ---------------------------------------------------------------------------
//  和 A9 的 `LoadingMaskPanel` 同款做法
//  ---------------------------------------------------------------------------
//  **所有控件都是可选的**（用 `GetControl` 而不是 `RequireControl`）：
//  预制体上想显示哪几项就放哪几个 `Text`，少放不报错。
//  HUD 长什么样是调试需求决定的，不该逼着做一套固定布局。
//
//  认识的控件名（都在面板根节点下，按 GameObject 名字匹配）：
//      FpsText       帧率
//      DrawCallText  DrawCall
//      GcAllocText   本帧 GC 分配
//      MemoryText    内存
//      DetailText    一行全量（方便快速看一眼）
// ============================================================================

using System;
using NBC.Framework.Perf;
using UnityEngine;
using UnityEngine.UI;

namespace NBC.Framework.UI
{
    /// <summary>
    /// 性能看板面板。**所有显示控件都是可选的。**
    /// </summary>
    public class PerfHudPanel : BasePanel
    {
        /// <summary>帧率文本的节点名。</summary>
        public const string FpsControlName = "FpsText";

        /// <summary>DrawCall 文本的节点名。</summary>
        public const string DrawCallControlName = "DrawCallText";

        /// <summary>GC 分配文本的节点名。</summary>
        public const string GcAllocControlName = "GcAllocText";

        /// <summary>内存文本的节点名。</summary>
        public const string MemoryControlName = "MemoryText";

        /// <summary>一行全量文本的节点名。</summary>
        public const string DetailControlName = "DetailText";

        /// <summary>默认的文字刷新间隔（秒）—— 4 Hz，见文件头说明。</summary>
        public const float DefaultRefreshInterval = 0.25f;

        private Text m_fpsText;
        private Text m_drawCallText;
        private Text m_gcAllocText;
        private Text m_memoryText;
        private Text m_detailText;

        private PerfSampler m_sampler;
        private bool m_ownsSampler;

        private float m_refreshInterval = DefaultRefreshInterval;
        private float m_accumulated;
        private bool m_driving;

        /// <summary>最新一次采样的结果（**即使还没刷到文本上**）。</summary>
        public PerfSnapshot LastSnapshot
        {
            get { return m_sampler != null ? m_sampler.Current : PerfSnapshot.Empty; }
        }

        /// <summary>采样器。没注入也没建起来时是 null。</summary>
        public PerfSampler Sampler
        {
            get { return m_sampler; }
        }

        /// <summary>文字刷新间隔（秒）。设成 0 表示每帧都刷（**不建议**，见文件头）。</summary>
        public float RefreshInterval
        {
            get { return m_refreshInterval; }
            set { m_refreshInterval = value < 0f ? 0f : value; }
        }

        /// <summary>累计刷了多少次文字（测试 / 诊断用）。</summary>
        public int RefreshCount { get; private set; }

        /// <summary>取得/建好采样器。**外部注入的采样器由外部负责 Dispose。**</summary>
        /// <param name="sampler">采样器；传 null 表示让面板自己建一个。</param>
        public void AttachSampler(PerfSampler sampler)
        {
            if (m_ownsSampler && m_sampler != null)
            {
                m_sampler.Dispose();
            }

            m_sampler = sampler;
            m_ownsSampler = false;
        }

        /// <summary>
        /// 推进一帧。
        /// <para>
        /// ⚠️ **采样每帧都做**（FPS 需要每一帧的耗时），
        /// 但**文字刷新限频**（见文件头）。
        /// </para>
        /// </summary>
        /// <param name="deltaSeconds">这一帧耗时（秒）。</param>
        public void Tick(float deltaSeconds)
        {
            if (m_sampler == null)
            {
                return;
            }

            m_sampler.Tick(deltaSeconds);

            if (m_refreshInterval <= 0f)
            {
                Refresh();
                return;
            }

            m_accumulated += deltaSeconds;

            if (m_accumulated >= m_refreshInterval)
            {
                m_accumulated = 0f;
                Refresh();
            }
        }

        /// <summary>立刻把最新快照刷到文本上（不看限频）。</summary>
        public void Refresh()
        {
            RefreshCount++;

            PerfSnapshot snapshot = LastSnapshot;

            // ⚠️ 每个都**先判空、再拼字符串**。
            //    写成 `SetText(m_fpsText, ... + " FPS")` 的话，C# 会先把实参算出来 ——
            //    哪怕控件是 null，"没挂的控件"也照样在分配字符串。
            //    这个看板存在的意义就是暴露分配，自己更不能这么写。
            if (m_fpsText != null)
            {
                m_fpsText.text = snapshot.Fps.ToString("F1") + " FPS";
            }

            if (m_drawCallText != null)
            {
                m_drawCallText.text = "DC " + PerfSnapshot.FormatValue(snapshot.DrawCalls);
            }

            if (m_gcAllocText != null)
            {
                m_gcAllocText.text = "GC " + PerfSnapshot.FormatBytes(snapshot.GcAllocatedInFrame);
            }

            if (m_memoryText != null)
            {
                m_memoryText.text = "Mem " + PerfSnapshot.FormatBytes(snapshot.TotalMemory);
            }

            if (m_detailText != null)
            {
                m_detailText.text = snapshot.ToString();
            }
        }

        /// <summary>取可选的显示控件，并在没有采样器时自己建一个。</summary>
        protected override void OnInit()
        {
            m_fpsText = GetControl<Text>(FpsControlName);
            m_drawCallText = GetControl<Text>(DrawCallControlName);
            m_gcAllocText = GetControl<Text>(GcAllocControlName);
            m_memoryText = GetControl<Text>(MemoryControlName);
            m_detailText = GetControl<Text>(DetailControlName);

            // ⚠️ **只在播放模式自动建采样器。**
            //    EditMode 下 Profiler 没在跑，`ProfilerRecorder` 既拿不到值、
            //    还可能往 Console 打噪音 —— 而测试里任何一条没被声明的日志都会让用例失败。
            //    所以 EditMode 里**必须由测试注入**（和 A5/A8/A9 用 `Application.isPlaying`
            //    挡住编辑模式是同一个套路）。
            if (m_sampler == null && Application.isPlaying)
            {
                m_sampler = new PerfSampler(new ProfilerRecorderCounterSource());
                m_ownsSampler = true;
            }

            Refresh();
        }

        /// <summary>
        /// 直接摆在场景里时的**自举**。
        /// <para>
        /// ⚠️ 为什么这块看板可以有、而 `LoadingMaskPanel` 这类不该有：
        /// </para>
        /// <list type="bullet">
        /// <item>
        /// <description>
        /// **看板是调试工具**，要能在**任何一个场景**里直接拖进去就看 ——
        /// 不能要求"先跑通 YooAsset 打包 + `UIManager.ShowPanel`"才看得见数字。
        /// 排查问题时恰恰是最不想先修资源系统的时候。
        /// </description>
        /// </item>
        /// <item>
        /// <description>
        /// **业务面板不行**：它们的显示时机属于玩法逻辑，必须由 `UIManager` 决定
        /// （A9 的整条设计就是"面板不许自己决定什么时候出现"，见 `BasePanel` 上的 P-08 说明）。
        /// </description>
        /// </item>
        /// </list>
        /// <para>
        /// ⚠️ **它不会抢 `UIManager` 的活**：`Initialize()` 是幂等的，
        /// 而 `UIManager` 在 `Instantiate` 之后**同步**就调了 `Initialize()`
        /// （`Start` 要等到下一帧才跑），所以被管理的面板走到这里是空操作。
        /// </para>
        /// </summary>
        private void Start()
        {
            if (IsInitialized)
            {
                return;
            }

            Initialize();
            ShowMe();
        }

        /// <summary>显示时开始驱动。</summary>
        public override void ShowMe()
        {
            StartDriving();
        }

        /// <summary>隐藏时停止驱动（**采样停掉，但采样器留着**，再显示时能接着用）。</summary>
        public override void HideMe()
        {
            StopDriving();
        }

        /// <summary>
        /// 释放面板自己建的采样器（**注入进来的由注入方负责**）。
        /// <para>
        /// ⚠️ 做成公开方法而不是只靠 `OnDestroy`：`OnDestroy` 在 EditMode 的测试里
        /// **不一定会被调用**（和 A1 那个"EditMode 不调 `Awake`"是同一类坑）。
        /// 测试要能**显式**释放，不然 `ProfilerRecorder` 的句柄会留在那儿。
        /// </para>
        /// </summary>
        public void ReleaseSampler()
        {
            StopDriving();

            if (m_ownsSampler && m_sampler != null)
            {
                m_sampler.Dispose();
            }

            m_sampler = null;
            m_ownsSampler = false;
        }

        /// <summary>
        /// Unity 销毁面板时兜底释放。
        /// <para>
        /// ⚠️ 这里用 Unity 自己的 `OnDestroy` 而不是给 `BasePanel` 加钩子：
        /// `BasePanel` 没有 `OnDispose`（A9 只留了 `OnInit`），
        /// 而"框架依赖 Unity 回调"正是 A9 修掉的那个毛病 —— 不该再引回来。
        /// 面板是叶子类，覆写 `OnDestroy` 不会被谁顶掉。
        /// </para>
        /// </summary>
        private void OnDestroy()
        {
            ReleaseSampler();
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>挂上每帧驱动（只在播放模式真正需要，且不重复挂）。</summary>
        private void StartDriving()
        {
            if (m_driving || !Application.isPlaying)
            {
                return;
            }

            m_driving = true;
            MonoManager.Instance.AddUpdateListener(OnUpdate);
        }

        /// <summary>摘掉每帧驱动。</summary>
        private void StopDriving()
        {
            if (!m_driving)
            {
                return;
            }

            m_driving = false;

            if (MonoManager.HasInstance)
            {
                MonoManager.Instance.RemoveUpdateListener(OnUpdate);
            }
        }

        /// <summary>
        /// 每帧回调。
        /// <para>用 `unscaledDeltaTime`：暂停（`timeScale = 0`）时也要能看 —— 那正是最需要看的时候。</para>
        /// </summary>
        private void OnUpdate()
        {
            Tick(Time.unscaledDeltaTime);
        }
    }
}
