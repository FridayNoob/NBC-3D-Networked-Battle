// 这个文件里有：
//   ProfilerRecorderCounterSource（类）：用 Unity 的 ProfilerRecorder 读六项性能计数器，读不到就返回 false，不填 0 冒充

using System;
using Unity.Profiling;
using UnityEngine;

namespace NBC.Framework.Perf
{
    // 主要功能：用 ProfilerRecorder 读六项真实计数器（DrawCall / SetPass / 三角形、总内存 / 托管堆、本帧 GC 分配），读不到就如实返回 false。
    // 主要数据成员：6 个 ProfilerRecorder（渲染三项、内存两项、GC 分配一项）、m_lastGcTotal（上次托管堆字节数，字节）、m_started（是否已 Start）。
    // 主要外部方法：Start / Stop / TryRead、IsStarted / IsGcAllocationFromProfiler 两个只读属性，以及 6 个可覆盖的计数器名公开静态字段。
    // 谁在用它：NBC.Framework.UI 的 PerfHudPanel 用 new ProfilerRecorderCounterSource() 建一个交给 PerfSampler；PerfSampler 构造时调 Start、Dispose 时调 Stop、每帧对六个 PerfCounterId 各调一次 TryRead。
    // 注意：Profiler 只在播放模式下真正在跑，EditMode 里拿不到值；发布版（非开发版）大部分渲染统计也不可用 —— 看板显示 N/A 是正确答案，显示 0 才是骗人。
    // 注意：Stop 先清标记再逐个 Dispose，这期间 TryRead 立刻返回 false；无效的 recorder 调 Dispose 也是安全的，不需要逐个判 Valid。
    /// <summary>用 Unity 的 ProfilerRecorder 读渲染与内存计数器；读不到时返回 false，由上层显示 N/A，绝不填 0 冒充。</summary>
    public sealed class ProfilerRecorderCounterSource : IPerfCounterSource
    {
        /// <summary>DrawCall 计数器名；值是每帧 DrawCall 个数（个/帧），Unity 版本换了名字就覆盖它。</summary>
        public static string DrawCallsCounterName = "Draw Calls Count";

        /// <summary>SetPass 计数器名；值是每帧渲染状态切换次数（个/帧），越高说明状态切换越碎。</summary>
        public static string SetPassCallsCounterName = "SetPass Calls Count";

        /// <summary>三角形计数器名；值是每帧提交的三角形数（个/帧）。</summary>
        public static string TrianglesCounterName = "Triangles Count";

        /// <summary>本帧 GC 分配计数器名；值是这一帧的分配量（字节/帧），有的版本与构建拿不到，会退化成托管堆净增长。</summary>
        public static string GcAllocatedInFrameCounterName = "GC Allocated In Frame";

        /// <summary>总内存计数器名；值是已用总内存（字节）。</summary>
        public static string TotalMemoryCounterName = "Total Used Memory";

        /// <summary>托管堆内存计数器名；值是托管堆已用内存（字节）。</summary>
        public static string MonoUsedMemoryCounterName = "GC Used Memory";

        // DrawCall 计数器，个/帧；未 Start 时是无效 recorder
        private ProfilerRecorder m_drawCalls;
        // SetPass 计数器，个/帧
        private ProfilerRecorder m_setPassCalls;
        // 三角形计数器，个/帧
        private ProfilerRecorder m_triangles;
        // 本帧 GC 分配计数器，字节/帧；无效时退化成托管堆净增长
        private ProfilerRecorder m_gcAllocated;
        // 总内存计数器，字节
        private ProfilerRecorder m_totalMemory;
        // 托管堆内存计数器，字节
        private ProfilerRecorder m_monoUsedMemory;

        // 上次 GC.GetTotalMemory(false) 读到的托管堆字节数，字节；退化方案靠它与本帧的差值算净增长
        private long m_lastGcTotal;
        // 是否已经 Start 过，false 时 TryRead 一律返回 false
        private bool m_started;

        // 功能：告诉调用方采集有没有在跑。
        // 入参：无。
        // 返回：已 Start 且还没 Stop 时 true，否则 false。
        // 步骤：只读 m_started 一个标记 —— 它只说明 Start 过，不保证底层 recorder 有效，有效性由各项 Valid 判断（GC 那一项另看 IsGcAllocationFromProfiler）。
        // 上游：需要判断采集状态的调用方（本类型内部直接用 m_started，不读这个属性）。
        // 下游：无。
        /// <summary>采集是否在跑；false 时 TryRead 一律返回 false。</summary>
        public bool IsStarted
        {
            get { return m_started; }
        }

        // 功能：说明 GC 分配那一列的数据来源，让调用方分清「本帧分配量」与「托管堆净增长」。
        // 入参：无。
        // 返回：true 表示值来自 Profiler 的 GC Allocated In Frame（本帧分配量）；false 表示退化成 GC.GetTotalMemory 的逐帧差值（净增长）。
        // 步骤：Start 时按 m_gcAllocated.Valid 赋值，Stop 时置 false —— 只在采集期间有意义。
        // 注意：净增长把「分配 1KB 又回收 1KB」看成 0，不是本帧分配量；看板别把两者当成同一个指标。
        // 上游：需要判断 GC 数据语义的调用方。
        // 下游：无。
        /// <summary>GC 分配是否来自 Profiler 计数器；false 时是「托管堆净增长」，语义不同，别混用。</summary>
        public bool IsGcAllocationFromProfiler { get; private set; }

        // 功能：为六项计数器各建一个 ProfilerRecorder，并记下托管堆基线，之后 TryRead 才可能读到值。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 已经 Start 过就直接返回 —— 重复建 recorder 会多占一套 Profiler 句柄
        //   2) 先置 m_started，再建六个 recorder：渲染三项走 ProfilerCategory.Render，总内存与托管堆走 Memory，GC 分配也走 Memory
        //   3) 按 m_gcAllocated.Valid 记下 GC 分配这一列有没有 Profiler 来源
        //   4) 记下此刻 GC.GetTotalMemory(false) 作基线，供退化方案逐帧算净增长
        // 注意：EditMode 与发布版里 StartNew 拿到的 recorder 多半无效，这里不报错，之后 TryRead 一律返回 false。
        // 上游：PerfSampler 构造函数的最后一件事。
        // 下游：ProfilerRecorder.StartNew（六次）、GC.GetTotalMemory。
        /// <summary>开始采集：建六个计数器并记下托管堆基线；已经 Start 过时什么也不做。</summary>
        public void Start()
        {
            if (m_started)
            {
                return;
            }

            m_started = true;

            m_drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, DrawCallsCounterName);
            m_setPassCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, SetPassCallsCounterName);
            m_triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, TrianglesCounterName);
            m_totalMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, TotalMemoryCounterName);
            m_monoUsedMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, MonoUsedMemoryCounterName);
            m_gcAllocated = ProfilerRecorder.StartNew(ProfilerCategory.Memory, GcAllocatedInFrameCounterName);

            IsGcAllocationFromProfiler = m_gcAllocated.Valid;
            m_lastGcTotal = GC.GetTotalMemory(false);
        }

        // 功能：停掉采集，把六个 recorder 占的 Profiler 句柄还回去。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 没 Start 过就直接返回，避免对没建过的 recorder 动手
        //   2) 先清 m_started 与 IsGcAllocationFromProfiler，再逐个 Dispose —— 顺序不能反，否则正在 Dispose 的 recorder 还可能被 TryRead 读到
        //   3) 六个 recorder 无条件 Dispose，不逐个判 Valid：无效的 recorder 调 Dispose 也是安全的
        // 注意：Stop 之后 TryRead 必须返回 false 而不是最后一帧的旧值，否则看板会一直显示停止前的数字，看不出采集已经停了。
        // 上游：PerfSampler.Dispose 调一次（PerfSampler 自己保证只 Dispose 一次）。
        // 下游：ProfilerRecorder.Dispose（六次）。
        /// <summary>停止采集：清标记并释放六个 ProfilerRecorder；没 Start 过时什么也不做。</summary>
        public void Stop()
        {
            if (!m_started)
            {
                return;
            }

            m_started = false;
            IsGcAllocationFromProfiler = false;

            m_drawCalls.Dispose();
            m_setPassCalls.Dispose();
            m_triangles.Dispose();
            m_gcAllocated.Dispose();
            m_totalMemory.Dispose();
            m_monoUsedMemory.Dispose();
        }

        // 功能：按编号把读数分派到对应计数器，并把「读不到」如实报上去。
        // 入参：id 计数器标识（PerfCounterId 的六个值）；value 读到的值，单位随 id 变 —— DrawCalls / SetPassCalls / Triangles 是个/帧，GcAllocatedInFrame 是字节/帧，TotalMemory / MonoUsedMemory 是字节。
        // 返回：true 表示 value 有效；false 表示未 Start 或该计数器此刻无效，此时 value 是 NaN（PerfSnapshot.Unavailable）。
        // 步骤：
        //   1) 先把 value 写成 Unavailable，任何提前返回的分支都不会留下 0 这个假数字
        //   2) 没 Start 过直接返回 false：没建 recorder 时读到的只会是无意义的值
        //   3) 按 id 分派：五项走 TryReadRecorder，GcAllocatedInFrame 单独走 TryReadGcAllocation（它有退化方案）；未知 id 返回 false
        // 注意：false 是正常状态而不是错误（EditMode 与发布版都读不到渲染统计）；调用方必须区分「读到 0」与「读不到」，把 false 当 0 就是在编假数据。
        // 上游：PerfSampler.Build 每帧对六个 id 各调一次；false 一次就给 ReadFailureCount 加一。
        // 下游：TryReadRecorder、TryReadGcAllocation。
        /// <summary>读一个计数器此刻的值；读不到返回 false 并写 NaN，不要拿 0 冒充。</summary>
        /// <returns>true 表示 value 有效；false 表示此刻读不到，value 为 NaN。</returns>
        public bool TryRead(PerfCounterId id, out float value)
        {
            value = PerfSnapshot.Unavailable;

            if (!m_started)
            {
                return false;
            }

            switch (id)
            {
                case PerfCounterId.DrawCalls:
                    return TryReadRecorder(m_drawCalls, out value);

                case PerfCounterId.SetPassCalls:
                    return TryReadRecorder(m_setPassCalls, out value);

                case PerfCounterId.Triangles:
                    return TryReadRecorder(m_triangles, out value);

                case PerfCounterId.TotalMemory:
                    return TryReadRecorder(m_totalMemory, out value);

                case PerfCounterId.MonoUsedMemory:
                    return TryReadRecorder(m_monoUsedMemory, out value);

                case PerfCounterId.GcAllocatedInFrame:
                    return TryReadGcAllocation(out value);

                default:
                    return false;
            }
        }

        // 功能：从单个 recorder 取最近一次的值，recorder 无效就报读不到。
        // 入参：recorder 要读的计数器；value 读到的值，单位随计数器而定（个/帧 或 字节）。
        // 返回：recorder 有效时 true 且 value 是 LastValue；无效时 false 且 value 是 NaN。
        // 步骤：先判 Valid 再取 LastValue —— 无效 recorder 的 LastValue 没有意义，所以无效分支先写 NaN 再返回 false。
        // 上游：TryRead 的五个分支。
        // 下游：无。
        private static bool TryReadRecorder(ProfilerRecorder recorder, out float value)
        {
            if (!recorder.Valid)
            {
                value = PerfSnapshot.Unavailable;
                return false;
            }

            value = recorder.LastValue;
            return true;
        }

        // 功能：读本帧 GC 分配量，优先用 Profiler 计数器，拿不到就退化成托管堆逐帧净增长。
        // 入参：value 读到的值，字节/帧。
        // 返回：总是 true —— 退化方案永远拿得到数，所以这一项不会显示 N/A。
        // 步骤：
        //   1) 优先读 m_gcAllocated.LastValue，它是这一帧真正的分配量（字节）
        //   2) 拿不到就用 GC.GetTotalMemory(false) 与上次基线的差值，并更新基线供下一帧再算
        //   3) 差值为负（回收多于分配）夹到 0，看板不需要负数
        // 注意：退化值算的是托管堆净增长而不是本帧分配量 ——「分配 1KB 又回收 1KB」在它眼里是 0，别把两者当成同一个指标。
        // 上游：TryRead 的 GcAllocatedInFrame 分支。
        // 下游：GC.GetTotalMemory。
        private bool TryReadGcAllocation(out float value)
        {
            if (m_gcAllocated.Valid)
            {
                value = m_gcAllocated.LastValue;
                return true;
            }

            long now = GC.GetTotalMemory(false);
            value = now - m_lastGcTotal;
            m_lastGcTotal = now;

            if (value < 0f)
            {
                value = 0f;
            }

            return true;
        }
    }
}
