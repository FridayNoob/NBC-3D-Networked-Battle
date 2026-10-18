// ============================================================================
//  RoomSetup —— 主循环里那两段「房间 → 开局」的装配（Host 与探针**共用同一份**）
//  项目：3D联网战斗Demo
//  对应：Docs\27 §三十五（S4-e `--fill-seats`）、§三十六（P0：未知副本号）
//
//  ---------------------------------------------------------------------------
//  一、为什么把这两段从 Host 的 `while` 里搬出来
//  ---------------------------------------------------------------------------
//  这两段原来是**内联在** `Server\NBC.Server.Host\Program.cs` 的主循环里的：
//      ① `--fill-seats=N`：房间里有第一个真人之后补足到 N 个机器人席位
//      ② 锁步开局：房间进入 `RoomPhase.Running` 且还没开过局 ⇒ 开一次
//  探针**够不到** Host 的 `while`（它是进程入口，不是库）⇒ 探针只能：
//      · 要么**照抄**一遍这两段（那就变成"验抄本"，真正跑在生产里的那份没人验）；
//      · 要么干脆不验（§35.0 就是这么过来的：靠人工步骤）。
//  搬到这里之后：**Host 与探针调的是同一份代码**（与 `ProgressBroadcaster` /
//  `QuestStateBroadcaster` / `ServerMessagePump` 同一条理由）。
//
//  ---------------------------------------------------------------------------
//  二、语义**逐条未变**（搬运算术，不是改行为）
//  ---------------------------------------------------------------------------
//  · 补席位：**逐个房间**、只补有真人的房间、编号从 `Room.BotIdBase` 起、
//    判据 `slot = 真人 + 机器人 的当前总数`，补到 `targetSeats`；`targetSeats <= 0` ⇒ 什么都不做
//    （= Host 原来 `if (fillSeats > 0)` 那道闸）。
//    ⭐ **唯一新增的一步**：补完**广播一次席位表**（2026-09-28 真 Host 端到端抓到的漏播 ——
//    名单变了不广播 ⇒ 客户端推出来的队伍与服务端开局名单不同 ⇒ 假分歧。见 `FillSeats` 的 ⚠️）。
//  · 锁步开局：判据是**相位**（`RoomPhase.Running`）而**不是**"人数够了" ——
//    相位由 `Room.Add` / `Room.AddBot` 在满员那一刻设（状态机只有那一处），这里只**消费**它。
//    两道闸都在：`HasRoom`（先问再开，免得每 tick 撞里面的闸刷计数）+ `StartRoom` 内部的闸。
//  · 开局名单用 `Room.RosterIds`（真人 + 机器人）—— 必须与 `ToState()` 下发给客户端的
//    那份是**同一个集合**，否则两端用同一个纯函数推出**不同**的队伍 ⇒ 客户端报假分歧。
//  · 两段都**没有日志**（原来就没有）。开局的日志由 `LockstepService.StartRoom` 自己打。
// ============================================================================

using NBC.Server.Core;
using NBC.Shared.Sim;

namespace NBC.Server.Game;

/// <summary>
/// 主循环里"房间 → 补席位 → 锁步开局"那两段装配（Host 与探针共用）。
/// <para>⚠️ 调用时机与原来内联时**完全一样**：都在同一个逻辑帧里、都在
/// `RoomBattleService.Tick()` **之后**、`LockstepService.Tick()` **之前**。</para>
/// </summary>
public static class RoomSetup
{
    /// <summary>
    /// `--fill-seats=N`：给**每个有真人的房间**补足到 N 个席位（多出来的是机器人）。
    /// <para>⚠️ 机器人进 `Room.Bots`（**不进 `Seats`**）—— 见 `Room.AddBot` 的说明：
    /// 状态同步链遍历的是 `Seats` ⇒ 对它们不可见 ⇒ 那条链零改动。</para>
    /// <para>⭐ 补完之后**必须把席位表广播一次**：客户端是从 `RoomState.Members` 推**队伍映射**的
    /// （`ToState()` 也把机器人写进去了，正是为了这件事）。不广播的后果是**两端名单不同** ——
    /// 服务端开局用 4 个（`RosterIds`）、客户端手里还是"只有我一个"⇒ 报**假分歧**。
    /// ⚠️ 这条是 2026-09-28 真 Host 端到端实测抓到的：补席位那半段原来**只加人、不广播**。</para>
    /// </summary>
    /// <param name="rooms">房间服务（要它的房间表 + 广播）。</param>
    /// <param name="targetSeats">目标席位数（`<= 0` = 什么都不做，默认就是 0）。</param>
    /// <returns>这一轮补了几个机器人。</returns>
    public static int FillSeats(RoomService rooms, int targetSeats)
    {
        if (rooms == null || targetSeats <= 0)
        {
            return 0;
        }

        int added = 0;

        foreach (Room room in rooms.Registry.Rooms)
        {
            if (room.SeatCount == 0)
            {
                continue;   // 没有真人 ⇒ 不补（别凭空把空房间填满）
            }

            int addedHere = 0;

            // ⚠️ 编号用**保留段** `Room.BotIdBase` 起，且只在本房内唯一
            //    （队伍映射与锁步世界都是**按房**算的 ⇒ 跨房重号无害）
            for (int slot = room.SeatCount + room.BotCount; slot < targetSeats; slot++)
            {
                room.AddBot(Room.BotIdBase + slot, "Bot" + slot);
                addedHere++;
            }

            if (addedHere > 0)
            {
                // ⭐ 有人的名单变了就要整份下发（D5：有变化就整份发）—— 见上面的 ⚠️
                rooms.BroadcastState(room);
            }

            added += addedHere;
        }

        return added;
    }

    /// <summary>
    /// 给**已经进入 `RoomPhase.Running` 且还没开过局**的房间开一局锁步。
    /// <para>⚠️ 判据是**相位**，不是"人数够了"：相位由 `Room.Add` / `Room.AddBot` 在满员那一刻设
    /// （状态机只有那一处），这里只是**消费**它 —— 两处都判满员就是两处规则。</para>
    /// </summary>
    /// <param name="lockstep">锁步服务。</param>
    /// <param name="registry">房间表。</param>
    /// <param name="delayTicks">固定输入延迟（tick）。</param>
    /// <returns>这一轮开了几个房。</returns>
    public static int StartLockstepForRunningRooms(LockstepService lockstep, RoomRegistry registry, int delayTicks)
    {
        if (lockstep == null || registry == null)
        {
            return 0;
        }

        int started = 0;

        foreach (Room room in registry.Rooms)
        {
            if (room.Phase != NBC.Protocol.RoomPhase.Running || lockstep.HasRoom(room.RoomId))
            {
                continue;
            }

            // ⭐ 用 `RosterIds`（真人 + 机器人）—— **必须与 `ToState()` 下发给客户端的
            //    那份名单是同一个集合**，否则两端用同一个纯函数推出**不同**的队伍
            //    ⇒ 客户端报"分歧"（假警报，很难查）。探针里有专门一条断言这个等式。
            if (lockstep.StartRoom(room.RoomId, LockstepRoster.Build(room.RosterIds), delayTicks))
            {
                started++;
            }
        }

        return started;
    }
}
