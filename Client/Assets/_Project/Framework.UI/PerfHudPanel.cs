// 这个文件里有：
//   PerfHudPanel（类）：性能看板面板，把每帧采样结果限频写成几行 Text，控件全部可选

using System;
using NBC.Framework.Perf;
using UnityEngine;
using UnityEngine.UI;

namespace NBC.Framework.UI
{
    // 主要功能：把每帧采到的 FPS、DrawCall、GC 分配与内存限频刷到几个可选 Text 上，供运行时观察。
    // 主要数据成员：m_refreshInterval（文字刷新间隔，秒）、m_accumulated（刷新节流累加，秒）、m_sampler（采样器）、五个可选 Text 控件。
    // 主要外部方法：AttachSampler、Tick、Refresh、ReleaseSampler，以及 RefreshInterval、LastSnapshot、Sampler、RefreshCount 属性。
    // 谁在用它：PerfHudDebug 场景里直接摆着（由 Start 自举）；EditMode 与 PlayMode 测试也直接挂载，EditMode 下注入假采样器。
    /// <summary>性能看板面板：把采样结果刷到若干可选 Text 上，缺哪个控件就不刷哪一项。</summary>
    public class PerfHudPanel : BasePanel
    {
        /// <summary>帧率文本的节点名；按 GameObject 名字匹配，没有就跳过这一项。</summary>
        public const string FpsControlName = "FpsText";

        /// <summary>DrawCall 文本的节点名；按 GameObject 名字匹配。</summary>
        public const string DrawCallControlName = "DrawCallText";

        /// <summary>GC 分配文本的节点名；按 GameObject 名字匹配。</summary>
        public const string GcAllocControlName = "GcAllocText";

        /// <summary>内存文本的节点名；按 GameObject 名字匹配。</summary>
        public const string MemoryControlName = "MemoryText";

        /// <summary>一行全量文本的节点名；按 GameObject 名字匹配。</summary>
        public const string DetailControlName = "DetailText";

        /// <summary>默认文字刷新间隔，秒；0.25 秒 = 4 Hz。</summary>
        public const float DefaultRefreshInterval = 0.25f;

        // 文字刷新间隔，秒；0 = 每帧都刷（不建议），0.25 = 4 Hz。
        // 注意：必须是字段才能进 Inspector，属性永远不显示 —— Unity 只序列化字段。
        // 注意：预制体里存过值后，改这里不再影响已有实例；要回默认值得点 Reset 或删掉重挂组件，DefaultRefreshInterval 只是新建组件时的初值。
        [SerializeField]
        [Tooltip("文字刷新间隔（秒）。0.25 = 4 Hz；0 = 每帧都刷（不建议，看板会自己造 GC）。")]
        [Min(0f)]
        private float m_refreshInterval = DefaultRefreshInterval;

        // 帧率文本；没挂叫 FpsText 的 Text 时为 null，刷新时跳过。
        private Text m_fpsText;
        // DrawCall 文本；没挂叫 DrawCallText 的 Text 时为 null，刷新时跳过。
        private Text m_drawCallText;
        // GC 分配文本；没挂叫 GcAllocText 的 Text 时为 null，刷新时跳过。
        private Text m_gcAllocText;
        // 内存文本；没挂叫 MemoryText 的 Text 时为 null，刷新时跳过。
        private Text m_memoryText;
        // 一行全量文本；没挂叫 DetailText 的 Text 时为 null，刷新时跳过。
        private Text m_detailText;

        // 采样器；播放模式下由 OnInit 自建，或由 AttachSampler 注入；未就绪时为 null，Tick 直接返回。
        private PerfSampler m_sampler;
        // 采样器是否由本面板创建；true 才由本面板 Dispose，注入的归注入方释放。
        private bool m_ownsSampler;

        // 距上次文字刷新的累加时间，秒；达到 m_refreshInterval 就归零并刷一次。
        private float m_accumulated;
        // 是否已挂到 MonoManager 的每帧回调上；用来防重复挂与重复摘。
        private bool m_driving;

        /// <summary>最新一次采样结果；还没采过时是空快照，渲染统计为不可用。</summary>
        public PerfSnapshot LastSnapshot
        {
            get { return m_sampler != null ? m_sampler.Current : PerfSnapshot.Empty; }
        }

        /// <summary>当前采样器；没注入也没自建时为 null。</summary>
        public PerfSampler Sampler
        {
            get { return m_sampler; }
        }

        /// <summary>文字刷新间隔，秒；赋值时负数钳成 0，0 表示每帧都刷（不建议，看板会自己造 GC）。</summary>
        public float RefreshInterval
        {
            get { return m_refreshInterval; }
            set { m_refreshInterval = value < 0f ? 0f : value; }
        }

        /// <summary>累计刷了多少次文字，从 0 起；测试与诊断用。</summary>
        public int RefreshCount { get; private set; }

        // 功能：换上外部采样器；本面板自建的那份先释放，避免句柄泄漏。
        // 入参：sampler 采样器；null 表示清空，不会当场新建，之后 Tick 空转
        // 返回：无
        // 步骤：
        //   1) 只有旧采样器是本面板自建的时候才 Dispose —— 注入的归注入方，越权释放会让对方手里的引用失效
        //   2) 换成传入的实例，所有权一律标记为外部
        // 上游：测试在 Initialize 之前注入；需要自定义计数器来源的调用方
        // 下游：PerfSampler.Dispose
        /// <summary>换上外部采样器；本面板自建的那份会先释放。</summary>
        /// <param name="sampler">采样器；null 表示清空，不会当场新建。</param>
        public void AttachSampler(PerfSampler sampler)
        {
            if (m_ownsSampler && m_sampler != null)
            {
                m_sampler.Dispose();
            }

            m_sampler = sampler;
            m_ownsSampler = false;
        }

        // 功能：把这一帧喂给采样器，并按刷新间隔决定要不要写文字。
        // 入参：deltaSeconds 这一帧耗时，秒（调用方传 unscaledDeltaTime，暂停时也要能走）
        // 返回：无
        // 步骤：
        //   1) 没采样器就直接返回 —— EditMode 未注入时走这里，属于正常路径
        //   2) 采样每帧都做：FPS 需要每一帧的耗时，漏帧就算错
        //   3) 间隔不大于 0 走每帧刷（测试要这条路，实战不建议，看板会自己造 GC）
        //   4) 否则累加到点再刷，并把累加器归零 —— 限掉的是字符串分配，不是采样
        // 上游：OnUpdate（每帧回调）、测试直接调
        // 下游：PerfSampler.Tick、Refresh
        /// <summary>推进一帧：先采样，再按刷新间隔决定要不要写文字。</summary>
        /// <param name="deltaSeconds">这一帧耗时，秒。</param>
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

        // 功能：把最新快照格式化后写进每个已挂的 Text，并累加刷新次数。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先取一次快照，后面几项格式化都用同一份，避免中途换值
        //   2) 五个控件逐个判空、逐个拼，没挂的跳过
        //   3) FPS 保留一位小数；DrawCall、GC 分配与内存走 PerfSnapshot 的格式化方法，读不到时统一显示 N/A
        // 上游：Tick 限频到点、OnInit 首次、测试手动调
        // 下游：PerfSnapshot.FormatValue、FormatBytes、ToString
        // 注意：必须先判空再拼字符串 —— C# 会先把实参算出来，写成 SetText(m_fpsText, ... + 「FPS」) 时控件是 null 也照样分配；一个用来观测 GC 的看板自己制造分配就本末倒置。
        /// <summary>立刻把最新快照刷到已挂的文本上，不看刷新间隔。</summary>
        public void Refresh()
        {
            RefreshCount++;

            PerfSnapshot snapshot = LastSnapshot;

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

        // 功能：收集五个可选 Text 控件，播放模式下自建采样器，最后先刷一次。
        // 入参：无（由 BasePanel.Initialize 调用）
        // 返回：无
        // 步骤：
        //   1) 用 GetControl 而不是 RequireControl —— 控件全是可选的，预制体少放甚至一个不放都不报错
        //   2) 只有播放模式才自建采样器（原因见下面注意）
        //   3) 最后 Refresh 一次，避免第一帧空白
        // 上游：BasePanel.Initialize（UIManager 实例化后同步调用），或被 Start 自举调用
        // 下游：BasePanel.GetControl、PerfSampler、Refresh
        // 注意：EditMode 下不能自建采样器 —— Profiler 没在跑，ProfilerRecorder 既读不到值还可能往 Console 打噪音，而测试里未声明的日志会让用例失败，所以 EditMode 必须由测试注入。
        protected override void OnInit()
        {
            m_fpsText = GetControl<Text>(FpsControlName);
            m_drawCallText = GetControl<Text>(DrawCallControlName);
            m_gcAllocText = GetControl<Text>(GcAllocControlName);
            m_memoryText = GetControl<Text>(MemoryControlName);
            m_detailText = GetControl<Text>(DetailControlName);

            if (m_sampler == null && Application.isPlaying)
            {
                m_sampler = new PerfSampler(new ProfilerRecorderCounterSource());
                m_ownsSampler = true;
            }

            Refresh();
        }

        // 功能：面板被直接摆进场景时自举 —— 没初始化就先 Initialize，再显示自己。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已初始化说明 UIManager 或别人已经接管，直接返回
        //   2) 否则自己初始化并显示 —— 看板是调试工具，要能拖进任意场景直接看，不能要求先跑通资源打包与 UIManager 流程
        // 上游：Unity 在面板创建后的下一帧调 Start
        // 下游：BasePanel.Initialize、ShowMe
        // 注意：业务面板不该学这一手 —— 它们的显示时机属于玩法逻辑，必须由 UIManager 决定，否则面板会自己决定什么时候出现。
        // 注意：这样不会抢 UIManager 的活 —— Initialize 幂等，而 UIManager 在 Instantiate 之后同步就调了 Initialize，Start 要等下一帧，被管理的面板走到这里是空操作。
        private void Start()
        {
            if (IsInitialized)
            {
                return;
            }

            Initialize();
            ShowMe();
        }

        // 功能：显示时挂上每帧驱动，开始采样与刷新。
        /// <summary>面板显示时开始每帧驱动。</summary>
        public override void ShowMe()
        {
            StartDriving();
        }

        // 功能：隐藏时摘掉每帧驱动；采样停掉但采样器留着，再显示能接着用。
        /// <summary>面板隐藏时停止每帧驱动；采样器保留，再显示能接着用。</summary>
        public override void HideMe()
        {
            StopDriving();
        }

        // 功能：停驱动后释放自建采样器并清空引用；注入的采样器不碰。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先停驱动，避免释放后还有每帧回调进来
        //   2) 只 Dispose 自建的那份 —— 注入的归注入方
        //   3) 引用与所有权都清空，之后 Tick 直接返回
        // 上游：OnDestroy（Unity 销毁时）、测试显式调用
        // 下游：StopDriving、PerfSampler.Dispose
        // 注意：做成公开方法而不是只靠 OnDestroy —— EditMode 测试里 OnDestroy 不一定会被调用，测试要能显式释放，否则 ProfilerRecorder 的句柄会一直留着。
        /// <summary>停掉驱动并释放本面板自建的采样器；注入进来的不动。</summary>
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

        // 功能：Unity 销毁面板时兜底释放自建采样器。
        // 注意：这里走 Unity 的 OnDestroy 而不是给 BasePanel 加钩子 —— BasePanel 没有 OnDispose 这种钩子，框架再依赖 Unity 回调正是之前修掉的毛病；面板是叶子类，覆写 OnDestroy 不会被谁顶掉。
        private void OnDestroy()
        {
            ReleaseSampler();
        }

        // 功能：把 OnUpdate 挂到 MonoManager 的每帧回调上，只在播放模式且尚未挂过时生效。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已挂过或不在播放模式就直接返回 —— 既防重复挂，也因为 EditMode 没有帧循环，测试自己调 Tick
        //   2) 先置标志再挂回调，标志与监听保持一一对应
        // 上游：ShowMe
        // 下游：MonoManager.AddUpdateListener
        private void StartDriving()
        {
            if (m_driving || !Application.isPlaying)
            {
                return;
            }

            m_driving = true;
            MonoManager.Instance.AddUpdateListener(OnUpdate);
        }

        // 功能：从 MonoManager 摘掉 OnUpdate；没挂过就什么都不做。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没挂着直接返回，避免重复摘
        //   2) 先清标志，再在 MonoManager 还在时摘回调 —— 宿主可能已经先于面板被销毁
        // 上游：HideMe、ReleaseSampler
        // 下游：MonoManager.RemoveUpdateListener
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

        // 功能：每帧回调，把这一帧的耗时交给 Tick。
        // 注意：用 unscaledDeltaTime 而不是 deltaTime —— 暂停时 timeScale = 0，看板也要能正常显示，而那正是最需要看的时候。
        private void OnUpdate()
        {
            Tick(Time.unscaledDeltaTime);
        }
    }
}
