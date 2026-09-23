// ============================================================================
//  NBC.Server.Tests —— 骨架自检
//  项目：3D联网战斗Demo
//
//  因为没有引入 xUnit（M0 阶段刻意不加包，保证首次 build 零还原），
//  这里先放一个普通静态类做"编译期自检"：它被 Tests 程序集引用，
//  从而机械地证明 NBC.Shared 能被纯 .NET 程序集引用与调用。
//
//  M1 接入 xUnit 后，会把这些检查改写为真正的测试方法（[Fact]）。
// ============================================================================

using NBC.Server.Core;
using NBC.Server.Game;
using NBC.Shared;
using NBC.Shared.Net;      // M3-S5：断言用 `NetContract.TickRate`（速率的唯一来源）

namespace NBC.Server.Tests;

/// <summary>
/// 骨架自检。M1 会替换为 xUnit 测试。
/// </summary>
public static class SkeletonSelfCheck
{
    /// <summary>
    /// 执行一次最小自检，返回是否全部通过。
    /// </summary>
    /// <remarks>
    /// 检查项：
    /// <list type="number">
    ///   <item>NBC.Shared 可被纯 .NET 程序集引用（架构约束 R5 的机械验证）</item>
    ///   <item>逻辑帧率常量在合理范围</item>
    ///   <item>TickScheduler 的帧号换算正确</item>
    ///   <item>BattleInstance 可被构造（同步模式枚举贯通）</item>
    /// </list>
    /// </remarks>
    public static bool RunAll()
    {
        var ok = true;

        // 1. 共享层可访问
        ok &= SharedInfo.LayerName == "NBC.Shared";
        ok &= SharedInfo.ContractVersion >= 1;

        // 2. 帧率常量 —— ⚠️ 2026-09-23（M3-S5）改过：
        //    原来这里断言的是"字面量 30 / 20"，其中 20 是 `SnapshotSendRate`（一个**没人用的常量**，
        //    而且和 D5"每 tick 全量快照"矛盾）。现在共享层的速率**派生自协议契约**，
        //    所以这里断言真正的不变量：**共享层的 tick 率就是协议契约的 tick 率**。
        ok &= SharedInfo.LogicTickRate == NetContract.TickRate;
        ok &= SharedInfo.ContractVersion == NetContract.Version;

        // 3. 帧号 → 毫秒换算：30 帧/秒，第 30 帧应为 1000ms
        ok &= TickScheduler.TickToMs(0) == 0;
        ok &= TickScheduler.TickToMs(30) == 1000;
        ok &= TickScheduler.TickToMs(15) == 500;

        // 4. 战斗实例可构造
        var battle = new BattleInstance(roomId: 1, syncMode: SyncMode.LockStep, randomSeed: 12345);
        ok &= battle.CurrentTick == 0;
        ok &= battle.SyncMode == SyncMode.LockStep;
        ok &= battle.RandomSeed == 12345;

        // 5. 会话默认状态
        var session = new ClientSession { SessionId = 1, RemoteEndPoint = "127.0.0.1:0" };
        ok &= !session.IsAuthenticated;
        ok &= session.Phase == SessionPhase.Connecting;

        return ok;
    }
}
