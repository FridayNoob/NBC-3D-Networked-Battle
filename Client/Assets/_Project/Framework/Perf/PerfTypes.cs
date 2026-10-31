// 这个文件里有：
//   PerfCounterId（枚举）：性能计数器的标识，值 0..5 对应六项统计
//   PerfSnapshot（结构体）：某一帧的性能快照，不可变，读不到的项用 NaN 表示
// 注意：性能数据只给人看、不参与逻辑判断，所以本文件允许 float；共享层（NBC.Shared）的定点约束不适用于这里。
// 注意：读不到的值必须用 NaN，不能填 0 —— 0 会显示成看起来正常的假数字，显示层要把 NaN 渲染成 N/A。

using System;

namespace NBC.Framework.Perf
{
    // 主要功能：给六项性能计数器编号，采样层按编号分发取值。
    // 主要数据成员：六个枚举值，0 = DrawCalls、1 = SetPassCalls、2 = Triangles、3 = GcAllocatedInFrame、4 = TotalMemory、5 = MonoUsedMemory。
    // 主要外部方法：无（纯枚举）。
    // 谁在用它：PerfSampler 按顺序遍历六项组装快照，ProfilerRecorderCounterSource 按编号分发到对应 recorder，EditMode 用例按编号造数据。
    // 注意：枚举值同时是 PerfSampler 内部数组的下标，顺序不能改；新增计数器要同步 AllCounters 与 Build 的取值顺序。
    /// <summary>性能计数器的标识，取值 0..5；同时是 PerfSampler 内部数组的下标，顺序不能改。</summary>
    public enum PerfCounterId
    {
        /// <summary>每帧 DrawCall 数，个/帧，≥ 0；枚举值 0。</summary>
        DrawCalls = 0,

        /// <summary>每帧 SetPass 数，个/帧，≥ 0；越高说明渲染状态切换越多；枚举值 1。</summary>
        SetPassCalls = 1,

        /// <summary>每帧三角形数，个/帧，≥ 0；枚举值 2。</summary>
        Triangles = 2,

        /// <summary>本帧托管堆分配字节数，字节/帧，≥ 0；GC 压力的直接指标；枚举值 3。</summary>
        GcAllocatedInFrame = 3,

        /// <summary>已用总内存，字节，≥ 0；枚举值 4。</summary>
        TotalMemory = 4,

        /// <summary>托管堆已用内存，字节，≥ 0；枚举值 5。</summary>
        MonoUsedMemory = 5
    }

    // 主要功能：把某一帧的帧率、帧耗时与六项计数器打成一个只读值，供看板显示。
    // 主要数据成员：Fps（帧/秒）、FrameMilliseconds（毫秒）、DrawCalls / SetPassCalls / Triangles（个/帧）、GcAllocatedInFrame / TotalMemory / MonoUsedMemory（字节）、FrameCount（帧）、Empty（尚未采样的空快照）；读不到的项是 NaN。
    // 主要外部方法：构造函数、HasRenderStats / HasMemoryStats / IsGcAllocationFree、FormatBytes / FormatValue、ToString。
    // 谁在用它：PerfSampler.Build 每帧构造，NBC.Framework.UI 的 PerfHudPanel 读字段并调格式化方法，EditMode 用例断言 NaN 的传播与格式化结果。
    // 注意：判断某项可不可用要看它是不是 NaN，别拿 0 当「没有」—— 0 是合法统计值，例如本帧零分配。
    /// <summary>某一帧的性能快照：不可变值类型，每帧产出一个，不应逐条分配对象。</summary>
    public readonly struct PerfSnapshot
    {
        /// <summary>读不到数据时的哨兵值（NaN）；显示层应渲染成 N/A，不要用 0 代替。</summary>
        public const float Unavailable = float.NaN;

        /// <summary>平滑后的帧率，帧/秒，≥ 0；还没采到样本时是 0。</summary>
        public readonly float Fps;

        /// <summary>滑动窗口内的平均帧耗时，毫秒，≥ 0；还没采到样本时是 0。</summary>
        public readonly float FrameMilliseconds;

        /// <summary>每帧 DrawCall 数，个/帧，≥ 0；读不到时是 Unavailable（NaN）。</summary>
        public readonly float DrawCalls;

        /// <summary>每帧 SetPass 数，个/帧，≥ 0；越高说明渲染状态切换越多；读不到时是 NaN。</summary>
        public readonly float SetPassCalls;

        /// <summary>每帧三角形数，个/帧，≥ 0；读不到时是 NaN。</summary>
        public readonly float Triangles;

        /// <summary>本帧托管堆分配字节数，字节/帧，≥ 0；读不到时是 NaN。</summary>
        public readonly float GcAllocatedInFrame;

        /// <summary>已用总内存，字节，≥ 0；读不到时是 NaN。</summary>
        public readonly float TotalMemory;

        /// <summary>托管堆已用内存，字节，≥ 0；读不到时是 NaN。</summary>
        public readonly float MonoUsedMemory;

        /// <summary>累计推进的帧数，帧，≥ 0；从 0 起，每 Tick 一次加 1。</summary>
        public readonly long FrameCount;

        /// <summary>按给定的一帧统计值构造快照；原样存入，不做校验也不换算单位。</summary>
        // 功能：把一帧的九项统计原样装进只读快照，构造完就不能再改。
        // 入参：fps 帧率（帧/秒）；frameMilliseconds 平均帧耗时（毫秒）；drawCalls / setPassCalls / triangles 每帧个数；gcAllocatedInFrame 本帧分配字节数；totalMemory / monoUsedMemory 已用字节数；frameCount 累计帧数（帧）；读不到的项传 Unavailable
        // 返回：无（构造函数）
        // 步骤：按参数顺序逐个赋给 readonly 字段，不换算也不校验 —— 数值由 PerfSampler 在同一帧组装好，单位写错只能靠调用方保证。
        // 上游：PerfSampler.Build、PerfSnapshot.Empty 的静态初始化
        // 下游：无
        // 注意：传 0 表示「确实是 0」，读不到要传 Unavailable；两者在显示层是「0」和「N/A」的区别。
        public PerfSnapshot(float fps, float frameMilliseconds, float drawCalls, float setPassCalls,
                            float triangles, float gcAllocatedInFrame, float totalMemory,
                            float monoUsedMemory, long frameCount)
        {
            Fps = fps;
            FrameMilliseconds = frameMilliseconds;
            DrawCalls = drawCalls;
            SetPassCalls = setPassCalls;
            Triangles = triangles;
            GcAllocatedInFrame = gcAllocatedInFrame;
            TotalMemory = totalMemory;
            MonoUsedMemory = monoUsedMemory;
            FrameCount = frameCount;
        }

        /// <summary>还没采过任何一帧的空快照：Fps 为 0（帧/秒），计数与内存为 NaN，FrameCount 为 0（帧）；它不等于「数值都是 0」。</summary>
        public static readonly PerfSnapshot Empty =
            new PerfSnapshot(0f, 0f, Unavailable, Unavailable, Unavailable,
                             Unavailable, Unavailable, Unavailable, 0L);

        /// <summary>渲染统计可用吗：DrawCalls 不是 NaN 时为 true。</summary>
        // 功能：判断这一帧的渲染统计读没读到，需要区分 N/A 与真实 0 的调用方靠它。
        // 入参：无
        // 返回：DrawCalls 不是 NaN 时 true，否则 false
        // 步骤：只查 DrawCalls 这一项 —— 它是渲染统计的代表项，不代表 SetPass / Triangles 也可用；后两项要逐项看是不是 NaN
        // 上游：EditMode 用例、需要判断渲染统计可用性的调用方
        // 下游：无
        public bool HasRenderStats
        {
            get { return !float.IsNaN(DrawCalls); }
        }

        /// <summary>内存统计可用吗：TotalMemory 不是 NaN 时为 true。</summary>
        // 功能：判断这一帧的内存统计读没读到，需要区分 N/A 与真实 0 的调用方靠它。
        // 入参：无
        // 返回：TotalMemory 不是 NaN 时 true，否则 false
        // 步骤：只查 TotalMemory 这一项 —— 它是内存统计的代表项，不代表 MonoUsedMemory 也可用；后者要单独看是不是 NaN
        // 上游：EditMode 用例、需要判断内存统计可用性的调用方
        // 下游：无
        public bool HasMemoryStats
        {
            get { return !float.IsNaN(TotalMemory); }
        }

        /// <summary>本帧有没有托管堆分配：GcAllocatedInFrame 有值且 ≤ 0 时为 true，零分配是好事。</summary>
        // 功能：判断本帧是不是零分配 —— 只对读到的值下结论。
        // 入参：无
        // 返回：GcAllocatedInFrame 不是 NaN 且 ≤ 0 时 true，否则 false
        // 步骤：
        //   1) 先排除 NaN，读不到直接判 false
        //   2) 再判 ≤ 0：0 是零分配，负值也同样按无分配处理
        // 上游：EditMode 用例、需要判断零分配的调用方
        // 下游：无
        // 注意：读不到必须是 false，不能当成零分配 —— 把未知当好消息会让 GC 结论偏乐观。
        public bool IsGcAllocationFree
        {
            get { return !float.IsNaN(GcAllocatedInFrame) && GcAllocatedInFrame <= 0f; }
        }

        /// <summary>把字节数渲染成 B / KB / MB 的可读文本；NaN 返回 N/A。</summary>
        /// <returns>小于 1024 为整数字节，小于 1 MB 为一位小数 KB，否则为一位小数 MB；NaN 时是 N/A。</returns>
        // 功能：把字节数按 1024 进制换算成 B / KB / MB，避免看板显示一长串数字。
        // 入参：bytes 字节数，≥ 0；读不到传 NaN
        // 返回：B 档保留 0 位小数，KB / MB 档保留 1 位小数；NaN 返回 N/A
        // 步骤：
        //   1) 先判 NaN 返回 N/A —— NaN 和任何数比较都是 false，不先拦会一路落到 MB 档
        //   2) 再按 1024 与 1024² 两个分界选单位，分别按 F0 / F1 / F1 格式化
        // 上游：PerfHudPanel 显示 GC 分配与总内存、ToString
        // 下游：无
        public static string FormatBytes(float bytes)
        {
            if (float.IsNaN(bytes))
            {
                return "N/A";
            }

            if (bytes < 1024f)
            {
                return bytes.ToString("F0") + " B";
            }

            if (bytes < 1024f * 1024f)
            {
                return (bytes / 1024f).ToString("F1") + " KB";
            }

            return (bytes / (1024f * 1024f)).ToString("F1") + " MB";
        }

        /// <summary>把计数器数值渲染成不带小数的文本；NaN 返回 N/A。</summary>
        /// <returns>按 F0 格式化的整数文本；NaN 时是 N/A。</returns>
        // 功能：把计数统一成整数文本，读不到时统一显示 N/A。
        // 入参：value 要显示的数值；读不到传 NaN
        // 返回：非 NaN 按 F0 格式化，NaN 返回 N/A
        // 步骤：先判 NaN 再格式化 —— 否则会直接显示「NaN」，与看板其它项的 N/A 不一致
        // 上游：PerfHudPanel 显示 DrawCall、ToString 拼详情
        // 下游：无
        public static string FormatValue(float value)
        {
            return float.IsNaN(value) ? "N/A" : value.ToString("F0");
        }

        /// <summary>拼一行调试文本：FPS、DrawCall、SetPass、三角形、GC 分配与总内存。</summary>
        /// <returns>单行文本，FPS 保留 1 位小数，计数走 FormatValue、字节走 FormatBytes；读不到的项显示 N/A。</returns>
        // 功能：把快照拼成一行可直接贴日志或显示在详情栏的文本。
        // 入参：无（重写 object.ToString）
        // 返回：单行文本，字段顺序固定为 FPS、DC、SetPass、Tris、GC、Mem
        // 步骤：按固定顺序拼接，计数与字节分别交给 FormatValue / FormatBytes —— 顺序固定，看板与日志才能横向对比
        // 上游：PerfHudPanel 的详情文本
        // 下游：FormatValue、FormatBytes
        // 注意：每次调用都会新建字符串，别在每帧热路径里反复拼。
        public override string ToString()
        {
            return "FPS " + Fps.ToString("F1") +
                   " | DC " + FormatValue(DrawCalls) +
                   " | SetPass " + FormatValue(SetPassCalls) +
                   " | Tris " + FormatValue(Triangles) +
                   " | GC " + FormatBytes(GcAllocatedInFrame) +
                   " | Mem " + FormatBytes(TotalMemory);
        }
    }
}
