// ============================================================================
//  NBC.Server.Game —— 房间战斗服务：把"权威世界"接到"房间成员"上
//  项目：3D联网战斗Demo
//  对应：Docs\25 §四 S5、§三 D2（30Hz）/ D5（每 tick 全量快照）
//
//  ---------------------------------------------------------------------------
//  一、它做三件事（每逻辑帧一次）
//  ---------------------------------------------------------------------------
//      ① **收拾**：房间没了（人都走了）就把它的战斗世界删掉
//      ② **推进**：对每个还有人的房间，`DungeonBattle.Step()` 走一帧
//      ③ **下发**：把整个世界拍成一张快照，发给房里的每个席位
//
//  ⚠️ 快照**序列化一次、发多份**：`ToSnapshot()` → `ToByteArray()` 只做一次，
//     然后同一个 `byte[]` 发给房里所有人。两个人以上的房间不该按人数重复序列化
//     （这是"每 tick 全量"这个决定最容易被写坏的地方：全量 ≠ 每人算一遍）。
//
//  ---------------------------------------------------------------------------
//  二、为什么"房间没了要删战斗"
//  ---------------------------------------------------------------------------
//  两个理由，第二个才是真会出事的那个：
//    · 内存：每个空房间留一个世界，跑一整晚就是一堆没人看的世界
//    · **房号与世界的对应关系会被污染**：`RoomRegistry` 用递增房号（`r1`/`r2`…），
//      但如果哪天改成复用房号（或者服务端重启后接着编号），
//      一个"上一局的战斗世界"就会被当成"这一局的"，表现为**刚进副本就看到怪物已经死了**
//  所以清理放在**每帧**做（便宜且不需要谁记得调用）。
//
//  ---------------------------------------------------------------------------
//  三、快照率 = tick 率（30Hz），这是 Docs\25 §8.5 定下的
//  ---------------------------------------------------------------------------
//  M3 不做插值、也不做预测（D4），所以"发得越勤，画面越顺"。
//  一帧一张的代价：2~4 人、十几个单位，每张几百字节 → 每秒十几 KB，可以忽略。
//  ⇒ 共享层原来那个 `SnapshotSendRate = 20` 已删除（它和这条决定矛盾，而且没人用）。
// ============================================================================

using System;
using System.Collections.Generic;
using Google.Protobuf;         // `ToByteArray()`
using NBC.Protocol;
using NBC.Server.Core;

namespace NBC.Server.Game;

/// <summary>房间 → 权威战斗世界：每帧推进并广播全量快照。</summary>
public sealed class RoomBattleService
{
    private readonly INetTransport _transport;
    private readonly RoomRegistry _registry;
    private readonly Dictionary<string, DungeonBattle> _battles = new(StringComparer.Ordinal);

    /// <summary>建一个房间战斗服务。</summary>
    /// <param name="transport">传输（下发快照要用它）。</param>
    /// <param name="registry">房间表（决定"哪些房间还该活着"）。</param>
    public RoomBattleService(INetTransport transport, RoomRegistry registry)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>值得记一句的事情（开打、房间清场、结束）。</summary>
    public event Action<string>? Note;

    /// <summary>现在有几个战斗世界。</summary>
    public int BattleCount => _battles.Count;

    /// <summary>累计推进了多少逻辑帧（= 发了多少轮快照）。</summary>
    public long TicksRun { get; private set; }

    /// <summary>累计成功送出的快照**份数**（按席位计，不是按帧）。</summary>
    public long SnapshotsSent { get; private set; }

    /// <summary>累计因为房间没了而丢掉的世界数。</summary>
    public long BattlesDropped { get; private set; }

    /// <summary>取某个房间的战斗世界（没有就返回 null）。</summary>
    /// <param name="roomId">房号。</param>
    /// <returns>世界或 null。</returns>
    public DungeonBattle? Find(string roomId)
    {
        DungeonBattle? battle;
        return _battles.TryGetValue(roomId, out battle) ? battle : null;
    }

    /// <summary>
    /// 推进一帧：收拾空房间 → 推进每个世界 → 下发快照。
    /// <para>由主循环**每逻辑帧**调一次（30Hz 就是每秒 30 次）。</para>
    /// </summary>
    public void Tick()
    {
        DropBattlesOfDeadRooms();

        foreach (Room room in _registry.Rooms)
        {
            if (room.IsEmpty)
            {
                continue;       // 没人看的世界不用推进（房间会在离开时被删，这里只是兜底）
            }

            DungeonBattle battle = EnsureBattle(room);
            battle.Step();
            BroadcastSnapshot(battle, room);
            TicksRun++;
        }
    }

    /// <summary>
    /// 取这个房间的战斗世界（第一次调用时按 `DungeonId` 建一局）。
    /// </summary>
    /// <param name="room">房间。</param>
    /// <returns>世界。</returns>
    public DungeonBattle EnsureBattle(Room room)
    {
        DungeonBattle? existing;

        if (_battles.TryGetValue(room.RoomId, out existing))
        {
            return existing;
        }

        // TODO(S6)：`CreateStarterDungeon` 现在写死 1 英雄 + 2 狼，将来按 `room.DungeonId` 读配置表
        DungeonBattle battle = DungeonBattle.CreateStarterDungeon(room.RoomId, room.DungeonId);
        _battles.Add(room.RoomId, battle);

        Note?.Invoke($"房间 {room.RoomId} 开打：副本 {room.DungeonId}，{battle.Entities.Count} 个单位");

        return battle;
    }

    /// <summary>把一张快照发给房里的每个人（**序列化一次**，见文件头 ①）。</summary>
    /// <param name="battle">世界。</param>
    /// <param name="room">房间。</param>
    /// <returns>成功发出去几份。</returns>
    private int BroadcastSnapshot(DungeonBattle battle, Room room)
    {
        byte[] payload = new ServerMessage { Snapshot = battle.ToSnapshot() }.ToByteArray();
        int sent = 0;

        for (int i = 0; i < room.Seats.Count; i++)
        {
            if (_transport.Send(room.Seats[i].Session.SessionId, payload))
            {
                sent++;
            }
        }

        SnapshotsSent += sent;
        return sent;
    }

    /// <summary>房间没了就把它的世界删掉（见文件头 ②）。</summary>
    private void DropBattlesOfDeadRooms()
    {
        if (_battles.Count == 0)
        {
            return;
        }

        // 先收集要删的 key，再删：**不要在遍历字典的过程中改字典**
        List<string>? dead = null;

        foreach (KeyValuePair<string, DungeonBattle> pair in _battles)
        {
            if (_registry.Find(pair.Key) == null)
            {
                dead ??= new List<string>();
                dead.Add(pair.Key);
            }
        }

        if (dead == null)
        {
            return;
        }

        for (int i = 0; i < dead.Count; i++)
        {
            _battles.Remove(dead[i]);
            BattlesDropped++;
            Note?.Invoke($"房间 {dead[i]} 已经没人了，战斗世界已回收");
        }
    }
}
