// ============================================================================
//  NBC.Framework.Perf · 用 `ProfilerRecorder` 读真实计数器
//  对应需求：FW-M12
//
//  ---------------------------------------------------------------------------
//  ⚠️ 三条"它可能读不到"的诚实说明
//  ---------------------------------------------------------------------------
//  **① EditMode 下拿不到值。** Profiler 只在播放模式下真正在跑。
//  **② 发布版（非开发版）大部分渲染统计不可用。** 那是 Unity 的设计：
//     统计要开 Profiler 才有，正式包不会为了看板一直开着它。
//  **③ 计数器的"名字"随 Unity 版本可能变。** 所以名字做成**可覆盖的公开常量**，
//     而且读不到时**返回 false**（上层渲染成 `N/A`），不填 0 冒充。
//
//  > 这三条都是"正常的运行状态"，不是错误。**看板显示 N/A 是正确答案，
//  > 显示 0 才是骗人。**
//
//  ---------------------------------------------------------------------------
//  GC 分配这一项为什么要"两手准备"
//  ---------------------------------------------------------------------------
//  `ProfilerRecorder` 的 "GC Allocated In Frame" 计数器在有些版本/构建下拿不到，
//  所以退化方案是：**用 `GC.GetTotalMemory(false)` 的逐帧差值**（托管堆净增长）。
//
//  ⚠️ 两者的语义要分清楚（这也是为什么返回值里有个 `IsGcAllocationFromProfiler`）：
//    · Profiler 计数器：**这一帧分配了多少**（准确，但可能拿不到）
//    · GC 差值：**这一帧托管堆净增长了多少**（永远拿得到，但"分配了 1KB 又回收了 1KB"
//      在它眼里是 0）
//  看板要判断"有没有 GC 压力"用哪个都行，但**别把后者当成前者**。
// ============================================================================

using System;
using Unity.Profiling;
using UnityEngine;

namespace NBC.Framework.Perf
{
    /// <summary>
    /// 用 Unity 的 `ProfilerRecorder` 读计数器。
    /// </summary>
    public sealed class ProfilerRecorderCounterSource : IPerfCounterSource
    {
        // 计数器名字。**不同 Unity 版本可能不同**，所以是公开字段，可以改。
        // 找不到时返回 false（不会崩），看板显示 N/A。
        /// <summary>DrawCall 计数器名。</summary>
        public static string DrawCallsCounterName = "Draw Calls Count";

        /// <summary>SetPass 计数器名。</summary>
        public static string SetPassCallsCounterName = "SetPass Calls Count";

        /// <summary>三角形计数器名。</summary>
        public static string TrianglesCounterName = "Triangles Count";

        /// <summary>每帧 GC 分配计数器名。</summary>
        public static string GcAllocatedInFrameCounterName = "GC Allocated In Frame";

        /// <summary>总内存计数器名。</summary>
        public static string TotalMemoryCounterName = "Total Used Memory";

        /// <summary>托管堆内存计数器名。</summary>
        public static string MonoUsedMemoryCounterName = "GC Used Memory";

        private ProfilerRecorder m_drawCalls;
        private ProfilerRecorder m_setPassCalls;
        private ProfilerRecorder m_triangles;
        private ProfilerRecorder m_gcAllocated;
        private ProfilerRecorder m_totalMemory;
        private ProfilerRecorder m_monoUsedMemory;

        private long m_lastGcTotal;
        private bool m_started;

        /// <summary>是否已经 `Start` 过。</summary>
        public bool IsStarted
        {
            get { return m_started; }
        }

        /// <summary>
        /// GC 分配那一项**是不是来自 Profiler**。
        /// <para>`false` 表示退化成"托管堆净增长"（见文件头说明）。</para>
        /// </summary>
        public bool IsGcAllocationFromProfiler { get; private set; }

        /// <summary>开始采集。</summary>
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

        /// <summary>停止采集。</summary>
        public void Stop()
        {
            if (!m_started)
            {
                return;
            }

            m_started = false;
            IsGcAllocationFromProfiler = false;

            // 无效的 recorder 调 Dispose 也是安全的
            m_drawCalls.Dispose();
            m_setPassCalls.Dispose();
            m_triangles.Dispose();
            m_gcAllocated.Dispose();
            m_totalMemory.Dispose();
            m_monoUsedMemory.Dispose();
        }

        /// <summary>读一个计数器；读不到返回 false。</summary>
        /// <param name="id">计数器标识。</param>
        /// <param name="value">读到的值。</param>
        /// <returns>读到返回 true。</returns>
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

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>从一个 recorder 取值。</summary>
        /// <param name="recorder">计数器。</param>
        /// <param name="value">值。</param>
        /// <returns>可用返回 true。</returns>
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

        /// <summary>读"本帧 GC 分配"：优先 Profiler 计数器，退化成托管堆净增长。</summary>
        /// <param name="value">值。</param>
        /// <returns>总是 true（退化方案永远拿得到）。</returns>
        private bool TryReadGcAllocation(out float value)
        {
            if (m_gcAllocated.Valid)
            {
                value = m_gcAllocated.LastValue;
                return true;
            }

            // 退化：托管堆净增长。
            // ⚠️ 注意语义差别：这是"净增长"，不是"本帧分配量"（见文件头）。
            long now = GC.GetTotalMemory(false);
            value = now - m_lastGcTotal;
            m_lastGcTotal = now;

            // 回收导致的负增长对看板没意义，夹到 0
            if (value < 0f)
            {
                value = 0f;
            }

            return true;
        }
    }
}
