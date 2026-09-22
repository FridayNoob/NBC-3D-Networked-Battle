// ============================================================================
//  NBC.Framework.Perf · 性能计数器来源（接缝）
//  ── 与 A8 的 `IAudioPlaybackProbe`、E2 的 `ILogSink` 同一个手法
//
//  ---------------------------------------------------------------------------
//  为什么必须有这道缝（这次理由很硬）
//  ---------------------------------------------------------------------------
//  渲染/内存这些计数器走的是 `ProfilerRecorder`，而它：
//    · **在 EditMode 里拿不到值**（Profiler 只在播放模式下真正在跑）
//    · **在发布版里大部分不可用**（非开发版没有渲染统计）
//
//  也就是说：**不注入就没法测**，而且"拿不到"本身就是**必须处理**的一种正常状态。
//  把"读一个计数器"抽成接口之后：
//    · EditMode 测试喂固定值 → `PerfSampler` 的组装逻辑可测
//    · 运行时喂真实 `ProfilerRecorder` → 真实数据
//    · **"读不到"就是 `TryRead` 返回 false** → 上层渲染成 N/A，不编假数字
// ============================================================================

namespace NBC.Framework.Perf
{
    /// <summary>
    /// 性能计数器的读取来源。
    /// </summary>
    public interface IPerfCounterSource
    {
        /// <summary>
        /// 开始采集（建立底层计数器）。
        /// <para>在 EditMode 或发布版里可能**什么都建立不了**，这不算错误。</para>
        /// </summary>
        void Start();

        /// <summary>停止采集并释放底层资源。</summary>
        void Stop();

        /// <summary>
        /// 读一个计数器。
        /// </summary>
        /// <param name="id">计数器标识。</param>
        /// <param name="value">读到的值。</param>
        /// <returns>**读不到返回 false**（上层渲染成 N/A，不要填 0 冒充）。</returns>
        bool TryRead(PerfCounterId id, out float value);
    }
}
