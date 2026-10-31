// 这个文件里有：
//   IPerfCounterSource（接口）：性能计数器来源的接缝，采样器通过它读渲染与内存计数器，读不到就如实报读不到。

namespace NBC.Framework.Perf
{
    // 主要功能：把「读一个性能计数器」抽成接口，让 PerfSampler 只管把计数器组装成快照，数据来自真实 Profiler 还是测试假值由实现方决定。
    // 主要数据成员：无（纯接口；状态由实现方持有，例如 ProfilerRecorderCounterSource 的 6 个 ProfilerRecorder 与是否已 Start 的标记）。
    // 主要外部方法：Start / Stop / TryRead。
    // 谁在用它：PerfSampler 构造时调 Start、Dispose 时调 Stop、每帧 Build 对 6 个 PerfCounterId 各调一次 TryRead；实现方是 ProfilerRecorderCounterSource 与 EditMode 测试里的 FakePerfCounterSource。
    // 注意：渲染与内存计数器走 ProfilerRecorder，EditMode 里拿不到值（Profiler 只在播放模式下真正在跑），非开发版里大部分渲染统计也不可用；所以「拿不到」是必须处理的正常状态，不是错误。
    /// <summary>性能计数器来源：采样器按 id 读值，读不到必须返回 false，由上层渲染成 N/A。</summary>
    public interface IPerfCounterSource
    {
        // 功能：让实现方建立底层计数器，建好之后 TryRead 才可能读到值。
        // 入参：无
        // 返回：无
        // 步骤：接口方法没有实现步骤；实现方在这里建底层计数器（ProfilerRecorderCounterSource 建 6 个 ProfilerRecorder，测试假实现只是清空自己的字典）
        // 上游：PerfSampler 构造函数末尾调一次
        // 下游：实现方（ProfilerRecorderCounterSource.Start 调 ProfilerRecorder.StartNew）
        // 注意：在 EditMode 或发布版里可能什么都建立不了，这不算错误 —— 之后 TryRead 一律返回 false、看板显示 N/A 即可，不要在这里抛异常或填假值。
        /// <summary>开始采集：实现方建立底层计数器，之后 TryRead 才可能读到值。</summary>
        void Start();

        // 功能：停止采集并释放底层计数器，让实现方不再占着 Profiler 与句柄。
        // 入参：无
        // 返回：无
        // 步骤：接口方法没有实现步骤；实现方在没 Start 过或已停过时直接返回（ProfilerRecorderCounterSource 用 m_started 判断），否则 Dispose 掉全部计数器
        // 上游：PerfSampler.Dispose 调一次（PerfSampler 自己保证只 Dispose 一次）
        // 下游：实现方（ProfilerRecorderCounterSource.Stop 对 6 个 recorder 各 Dispose 一次）
        // 注意：Stop 之后 TryRead 必须返回 false 而不是最后一帧的旧值 —— 留缓存的话看板会一直显示停止前的数字，看不出采集已经停了。
        /// <summary>停止采集并释放底层计数器，之后 TryRead 一律返回 false。</summary>
        void Stop();

        // 功能：读一个计数器此刻的值；读不到就明确报读不到，不编数字。
        // 入参：id 计数器标识（PerfCounterId 的 6 个值）；value 读到的值，读不到时写 NaN（PerfSnapshot.Unavailable）。单位随 id 变：DrawCalls / SetPassCalls / Triangles 是每帧个数（个），GcAllocatedInFrame / TotalMemory / MonoUsedMemory 是字节（B）
        // 返回：true 表示 value 有效；false 表示这个计数器此刻读不到（未 Start / 平台不支持 / 非开发版没有渲染统计），此时 value 是 NaN
        // 步骤：1) 实现方先确认已 Start，未启动直接返回 false 并写 NaN
        //   2) 按 id 分派到对应计数器（渲染、内存、GC 分配各一路）
        //   3) 底层读数不可用时写 NaN 并返回 false —— 绝不填 0 冒充，填 0 会让看板显示一个看起来正常的假数字
        // 上游：PerfSampler.Build 每帧对 6 个 id 各调一次；返回 false 一次就给 ReadFailureCount 加一
        // 下游：实现方（ProfilerRecorderCounterSource 的 TryReadRecorder / TryReadGcAllocation；测试假实现读自己的字典）
        // 注意：false 是正常状态而不是错误（EditMode 与发布版都读不到渲染统计）；调用方必须区分「读到 0」与「读不到」，把 false 当 0 就是在编假数据。
        // 注意：GcAllocatedInFrame 在拿不到 Profiler 计数器时会退化成托管堆净增长 —— 语义是净增长而不是本帧分配量，别把两者当成一回事。
        /// <summary>读一个计数器；读不到返回 false 且 value 为 NaN，不要拿 0 冒充。</summary>
        /// <returns>true 表示 value 有效；false 表示此刻读不到，value 为 NaN。</returns>
        bool TryRead(PerfCounterId id, out float value);
    }
}
