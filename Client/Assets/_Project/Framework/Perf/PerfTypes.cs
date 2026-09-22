// ============================================================================
//  NBC.Framework.Perf · 性能数据的基础类型
//  对应需求：FW-M12（FPS / DrawCall / SetPass / Tris / GC Alloc / 内存 / 网络 / 回滚次数）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 这里**允许用 float**（和 D2 的共享层相反）
//  ---------------------------------------------------------------------------
//  性能数据是**给人看的数字**，不参与任何逻辑判断，也不需要跨端复现。
//  所以用 float 完全没问题 —— **不要**因为"D2 说了不许用浮点"就把这里也改成定点，
//  那是把"帧同步的确定性要求"错误地推广到了表现层。
//
//  这条边界值得记清楚：**共享层（`NBC.Shared`）不许有浮点；诊断/表现层可以有。**
//
//  ---------------------------------------------------------------------------
//  "读不到"要用 NaN 表示，不能用 0
//  ---------------------------------------------------------------------------
//  有些计数器在**发布版**里根本不可用（`ProfilerRecorder` 在非开发版拿不到渲染统计）。
//  这时候如果填 0，看板会显示"DrawCall = 0" —— 那是**一个看起来正常的假数字**，
//  比"看不见"危险得多。
//
//  所以约定：**读不到就是 `float.NaN`，显示层渲染成 `N/A`。**
//  （这和 A9 那个"兜底字符串 `Unknown(99)`"是同一类问题：宁可说不知道，也别编一个。）
// ============================================================================

using System;

namespace NBC.Framework.Perf
{
    /// <summary>性能计数器的标识。</summary>
    public enum PerfCounterId
    {
        /// <summary>每帧 DrawCall 数。</summary>
        DrawCalls = 0,

        /// <summary>每帧 SetPass 数（越高说明状态切换越多）。</summary>
        SetPassCalls = 1,

        /// <summary>每帧三角形数。</summary>
        Triangles = 2,

        /// <summary>本帧托管堆分配的字节数（**GC 压力的直接指标**）。</summary>
        GcAllocatedInFrame = 3,

        /// <summary>已用总内存。</summary>
        TotalMemory = 4,

        /// <summary>托管堆已用内存。</summary>
        MonoUsedMemory = 5
    }

    /// <summary>
    /// 某一帧的性能快照。**不可变值类型** —— 每帧产出一个，不该每条都分配对象。
    /// </summary>
    public readonly struct PerfSnapshot
    {
        /// <summary>读不到数据时的值。**显示层请渲染成 `N/A`。**</summary>
        public const float Unavailable = float.NaN;

        /// <summary>平滑后的帧率。</summary>
        public readonly float Fps;

        /// <summary>滑动窗口内的平均帧耗时（毫秒）。</summary>
        public readonly float FrameMilliseconds;

        /// <summary>每帧 DrawCall 数；不可用时是 <see cref="Unavailable"/>。</summary>
        public readonly float DrawCalls;

        /// <summary>每帧 SetPass 数；不可用时是 <see cref="Unavailable"/>。</summary>
        public readonly float SetPassCalls;

        /// <summary>每帧三角形数；不可用时是 <see cref="Unavailable"/>。</summary>
        public readonly float Triangles;

        /// <summary>本帧托管堆分配字节数；不可用时是 <see cref="Unavailable"/>。</summary>
        public readonly float GcAllocatedInFrame;

        /// <summary>已用总内存（字节）；不可用时是 <see cref="Unavailable"/>。</summary>
        public readonly float TotalMemory;

        /// <summary>托管堆已用内存（字节）；不可用时是 <see cref="Unavailable"/>。</summary>
        public readonly float MonoUsedMemory;

        /// <summary>累计推进了多少帧。</summary>
        public readonly long FrameCount;

        /// <summary>构造。</summary>
        /// <param name="fps">帧率。</param>
        /// <param name="frameMilliseconds">平均帧耗时。</param>
        /// <param name="drawCalls">DrawCall 数。</param>
        /// <param name="setPassCalls">SetPass 数。</param>
        /// <param name="triangles">三角形数。</param>
        /// <param name="gcAllocatedInFrame">本帧 GC 分配字节。</param>
        /// <param name="totalMemory">已用总内存。</param>
        /// <param name="monoUsedMemory">托管堆已用内存。</param>
        /// <param name="frameCount">累计帧数。</param>
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

        /// <summary>还没采过任何一帧。</summary>
        public static readonly PerfSnapshot Empty =
            new PerfSnapshot(0f, 0f, Unavailable, Unavailable, Unavailable,
                             Unavailable, Unavailable, Unavailable, 0L);

        /// <summary>渲染统计可用吗。</summary>
        public bool HasRenderStats
        {
            get { return !float.IsNaN(DrawCalls); }
        }

        /// <summary>内存统计可用吗。</summary>
        public bool HasMemoryStats
        {
            get { return !float.IsNaN(TotalMemory); }
        }

        /// <summary>本帧有没有托管堆分配（**零分配是好事**）。</summary>
        public bool IsGcAllocationFree
        {
            get { return !float.IsNaN(GcAllocatedInFrame) && GcAllocatedInFrame <= 0f; }
        }

        /// <summary>把字节数渲染成人话（K/M）。不可用时返回 `N/A`。</summary>
        /// <param name="bytes">字节数。</param>
        /// <returns>可读文本。</returns>
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

        /// <summary>把数值渲染成人话。不可用时返回 `N/A`。</summary>
        /// <param name="value">数值。</param>
        /// <returns>可读文本。</returns>
        public static string FormatValue(float value)
        {
            return float.IsNaN(value) ? "N/A" : value.ToString("F0");
        }

        /// <summary>调试文本。</summary>
        /// <returns>可读描述。</returns>
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
