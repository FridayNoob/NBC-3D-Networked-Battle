// ============================================================================
//  NBC.Server.Core —— 主循环调度器
//  项目：3D联网战斗Demo   对应需求文档 §13.3（SRV-04）、§6.4（LOCK-01）
//
//  M0 阶段：仅建立骨架与时间计算，不含实际逻辑推进。
//  M4 阶段接入：驱动战斗模拟、生成快照/帧数据、确定性哈希校验。
// ============================================================================

using System.Diagnostics;
using NBC.Shared;

namespace NBC.Server.Core;

/// <summary>
/// 服务端定长帧调度器。
/// </summary>
/// <remarks>
/// 为什么必须"定长"（需求文档 DET-03 / LOCK-01）：
/// 帧同步要求所有端按【相同的步长】推进逻辑。若用真实流逝时间（deltaTime）驱动，
/// 不同机器、不同帧率的计算结果就会不同，帧同步立刻失败。
/// 因此这里用累加器把真实时间"切成"固定长度的逻辑帧。
/// </remarks>
public sealed class TickScheduler
{
    private readonly double _tickIntervalSeconds;
    private readonly Stopwatch _stopwatch = new();
    private double _accumulatorSeconds;

    /// <summary>当前逻辑帧号（从 0 开始）。</summary>
    public long CurrentTick { get; private set; }

    /// <summary>每秒逻辑帧数。</summary>
    public int TickRate { get; }

    public int DroppedTicks { get; private set; } = 0;

    /// <summary>构造调度器。</summary>
    /// <param name="tickRate">逻辑帧率，必须等于 <see cref="SharedInfo.LogicTickRate"/>。</param>
    public TickScheduler(int tickRate = SharedInfo.LogicTickRate)
    {
        if (tickRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickRate), "TickRate 必须为正数。");

        TickRate = tickRate;
        _tickIntervalSeconds = 1.0 / tickRate;
    }

    /// <summary>开始计时（M0 阶段可被调用，但尚不驱动逻辑）。</summary>
    public void Start()
    {
        _accumulatorSeconds = 0;
        CurrentTick = 0;
        _stopwatch.Restart();
    }

    /// <summary>停止计时。</summary>
    public void Stop() => _stopwatch.Stop();

    /// <summary>
    /// 计算从上次调用到现在应当推进多少个逻辑帧。
    /// </summary>
    /// <param name="maxTicksPerUpdate">
    /// 单次调用最多推进的帧数。防止进程被挂起（断点/GC 长停顿）后一次性追补大量帧导致雪崩。
    /// </param>
    /// <returns>本帧应当推进的逻辑帧数量。</returns>
    public int ConsumePendingTicks(int maxTicksPerUpdate = 5)
    {
        if (!_stopwatch.IsRunning)
            return 0;

        var elapsed = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();
        _accumulatorSeconds += elapsed;

        var ticks = 0;
        while (_accumulatorSeconds >= _tickIntervalSeconds && ticks < maxTicksPerUpdate)
        {
            _accumulatorSeconds -= _tickIntervalSeconds;
            ticks++;
        }

        // 超出上限的部分直接丢弃，避免"补帧风暴"
        if (_accumulatorSeconds > _tickIntervalSeconds * maxTicksPerUpdate)
        {
            DroppedTicks += (int)(_accumulatorSeconds / _tickIntervalSeconds);
            _accumulatorSeconds = 0;
        }

        CurrentTick += ticks;
        return ticks;
    }

    /// <summary>把逻辑帧号换算为毫秒时间戳（用于快照时间轴对齐）。</summary>
    public static long TickToMs(long tick, int tickRate = SharedInfo.LogicTickRate)
        => tick * 1000L / tickRate;
}
