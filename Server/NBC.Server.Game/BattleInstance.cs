// ============================================================================
//  NBC.Server.Game —— 战斗实例
//  项目：3D联网战斗Demo   对应需求文档 §6.3 / §6.4 / §13.3（SRV-08）
//
//  M0 阶段：仅建立类型与生命周期骨架。
//  M4 阶段接入：引入 NBC.Shared 的战斗核心逻辑，按 Tick 推进并输出快照/帧数据。
// ============================================================================

using NBC.Server.Core;
using NBC.Shared;

namespace NBC.Server.Game;

/// <summary>
/// 一局战斗的权威实例。
/// </summary>
/// <remarks>
/// 三种同步模式共用这个实例，差异体现在 <see cref="SyncMode"/> 与实际的下发策略上
/// （需求文档 §6.1：一套传输层 + 三个可插拔的同步策略）。
/// </remarks>
public sealed class BattleInstance
{
    private readonly TickScheduler _scheduler;

    /// <summary>房间 ID。</summary>
    public long RoomId { get; }

    /// <summary>本局随机种子。帧同步复现与回放依赖它（需求文档 DET-04 / LOCK-08）。</summary>
    public int RandomSeed { get; }

    /// <summary>本局使用的同步模式。</summary>
    public SyncMode SyncMode { get; }

    /// <summary>当前逻辑帧号。</summary>
    public long CurrentTick => _scheduler.CurrentTick;

    /// <summary>本局是否已结束。</summary>
    public bool IsFinished { get; private set; }

    /// <summary>构造一局战斗。</summary>
    /// <param name="roomId">房间 ID。</param>
    /// <param name="syncMode">同步模式。</param>
    /// <param name="randomSeed">随机种子；为 0 时使用当前时间派生。</param>
    public BattleInstance(long roomId, SyncMode syncMode, int randomSeed = 0)
    {
        RoomId = roomId;
        SyncMode = syncMode;
        RandomSeed = randomSeed != 0 ? randomSeed : Environment.TickCount;
        _scheduler = new TickScheduler(SharedInfo.LogicTickRate);
    }

    /// <summary>开始本局（等待所有客户端加载就绪后调用，见 LOCK-05）。</summary>
    public void Start() => _scheduler.Start();

    /// <summary>
    /// 推进逻辑帧。
    /// </summary>
    /// <param name="maxTicksPerUpdate">单次最多推进帧数。</param>
    /// <returns>实际推进的帧数。</returns>
    public int Step(int maxTicksPerUpdate = 5)
    {
        if (IsFinished)
            return 0;

        var ticks = _scheduler.ConsumePendingTicks(maxTicksPerUpdate);

        // TODO(M4): 按 SyncMode 分别处理
        //   State     —— 取出所有玩家输入 → 调用 NBC.Shared 战斗逻辑 → 生成快照（含可见集裁剪）
        //   LockStep  —— 收集本帧输入 → 打包 FrameData 广播（含输入缺失填充策略 LOCK-04）
        //   Hybrid    —— 帧同步 + 每 StateFrameIntervalTicks 帧下发权威关键帧（HYB-01）
        // TODO(M4): 每 30 帧计算状态哈希并广播（DET-V1/V2），用于确定性校验

        return ticks;
    }

    /// <summary>结束本局并产出结算数据（M3 落库用）。</summary>
    public void Finish() => IsFinished = true;
}

/// <summary>同步模式（需求文档 §6.1）。</summary>
public enum SyncMode
{
    /// <summary>状态同步：服务端权威，下发快照。</summary>
    State = 1,

    /// <summary>帧同步：下发输入帧，各端各自演算。</summary>
    LockStep = 2,

    /// <summary>状态帧同步：帧同步 + 周期性权威关键帧校正。</summary>
    Hybrid = 3,
}
