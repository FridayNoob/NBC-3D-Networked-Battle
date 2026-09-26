// ============================================================================
//  NBC.Server.Game —— 房间战斗服务：把"权威世界"接到"房间成员"上
//  项目：3D联网战斗Demo
//  对应：Docs\25 §四 S5 / S5b、§三 D2（30Hz）/ D3（服务器权威）/ D5（每 tick 全量快照）
//
//  ---------------------------------------------------------------------------
//  一、它每逻辑帧做五件事（顺序有意义）
//  ---------------------------------------------------------------------------
//      ① **收拾**：房间没了（人都走了）就把它的战斗世界删掉
//      ② **对齐**：让"房间席位"和"世界里的英雄"对上 —— 缺的补、走的删（`ReconcileHeroes`）
//      ③ **吃输入**：把每个玩家最近一条输入应用到他的英雄上（**移动是状态**）
//      ④ **推进**：`DungeonBattle.Step()` 走一帧
//      ⑤ **下发**：把整个世界拍成一张快照，发给房里的每个席位
//
//  ⚠️ ② 必须在 ③ 之前：否则"刚进房的人"这一帧没有英雄，输入会**静默丢掉**。
//
//  ⚠️ 快照**序列化一次、发多份**：`ToSnapshot()` → `ToByteArray()` 只做一次，
//     然后同一个 `byte[]` 发给房里所有人。两个人以上的房间不该按人数重复序列化
//     （这是"每 tick 全量"这个决定最容易被写坏的地方：全量 ≠ 每人算一遍）。
//
//  ---------------------------------------------------------------------------
//  二、S5b：输入的"两条不同规则"（这一节是这次最要紧的认知）
//  ---------------------------------------------------------------------------
//      **移动是状态** —— 客户端持续发"我现在按着什么方向"；服务端只留**最新一条**，
//                       每个 tick 应用一次。于是"客户端发得多密"完全不影响速度
//                       （发 60 次/秒 和发 30 次/秒，英雄一秒都只走 30 格）。
//      **动作是事件** —— "我按下了技能1"是一次性事实；服务端**收到就立刻判定**
//                       （射程/冷却/目标活着），不存在"这个动作要不要持续应用"的问题。
//
//  两条规则混起来写，就一定会出现"按住鼠标攻击每秒打 30 下"或者"移动速度取决于网速"这类 bug。
//
//  ⚠️ 还有 **输入的保鲜期**（`InputFreshTicks`）：超过 6 帧（200ms）没收到新输入，
//     就当**没有输入**。没有它的话，"客户端发一次往前、然后崩了"会让英雄永远往前走。
//
//  ---------------------------------------------------------------------------
//  三、两种"拒绝"要分开处理（S5b 定的判据）
//  ---------------------------------------------------------------------------
//      · **权威被违反**（输入里的 `player_id` 不是你）→ 回 `ErrorResponse`（响亮、可测）
//      · **由世界状态决定的拒绝**（射程不够 / 冷却中 / 目标已经死了 / 目标不存在）
//        → **只记日志**，不回错误。理由：客户端自己那份世界（`SnapshotView`）看得到这些，
//          回错误会在"按住攻击键"时变成每帧一条错误 —— 那不是"响亮"，那是刷屏。
//
//  ---------------------------------------------------------------------------
//  四、为什么"房间没了要删战斗"
//  ---------------------------------------------------------------------------
//  两个理由，第二个才是真会出事的那个：
//    · 内存：每个空房间留一个世界，跑一整晚就是一堆没人看的世界
//    · **房号与世界的对应关系会被污染**：`RoomRegistry` 用递增房号（`r1`/`r2`…），
//      但如果哪天改成复用房号，一个"上一局的战斗世界"就会被当成"这一局的"，
//      表现为**刚进副本就看到怪物已经死了**
//  所以清理放在**每帧**做（便宜且不需要谁记得调用）。
//
//  ---------------------------------------------------------------------------
//  五、快照率 = tick 率（30Hz），这是 Docs\25 §8.5 定下的
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
using NBC.Shared.Net;

namespace NBC.Server.Game;

/// <summary>房间 → 权威战斗世界：每帧对齐席位、吃输入、推进并广播全量快照。</summary>
public sealed class RoomBattleService
{
    /// <summary>
    /// 输入的"保鲜期"（帧）。超过这么久没收到新输入，就当**没有输入**（轴归零）。
    /// <para>
    /// ⚠️ 没有它的话："客户端发一次'往前'然后断流"会让英雄**永远往前走** ——
    /// 而客户端此时可能已经崩了。6 帧 = 200ms（30Hz），比一个 RTT 宽、比"人察觉到的卡顿"窄。
    /// </para>
    /// </summary>
    public const int InputFreshTicks = 6;

    private readonly INetTransport _transport;
    private readonly RoomRegistry _registry;
    private readonly ServerTables _tables;
    private readonly Dictionary<string, DungeonBattle> _battles = new(StringComparer.Ordinal);

    /// <summary>每个玩家**最近一条**输入（移动是"状态"，所以只留最新的一条，见文件头第二节）。</summary>
    private readonly Dictionary<long, PendingInput> _inputs = new();

    /// <summary>建一个房间战斗服务。</summary>
    /// <param name="transport">传输（下发快照 / 回错误要用它）。</param>
    /// <param name="registry">房间表（决定"哪些房间还该活着"）。</param>
    /// <param name="tables">配置表（S6 起：阵容与数值都从表里来）。</param>
    public RoomBattleService(INetTransport transport, RoomRegistry registry, ServerTables tables)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
    }

    /// <summary>值得记一句的事情（开打、进出副本、攻击判定、房间清场）。</summary>
    public event Action<string>? Note;

    /// <summary>现在有几个战斗世界。</summary>
    public int BattleCount => _battles.Count;

    /// <summary>累计推进了多少逻辑帧（= 发了多少轮快照）。</summary>
    public long TicksRun { get; private set; }

    /// <summary>累计成功送出的快照**份数**（按席位计，不是按帧）。</summary>
    public long SnapshotsSent { get; private set; }

    /// <summary>累计因为房间没了而丢掉的世界数。</summary>
    public long BattlesDropped { get; private set; }

    /// <summary>累计收到多少条输入。</summary>
    public long InputsReceived { get; private set; }

    /// <summary>累计有几次输入因为 `player_id` 不是这个会话的而被拒（**权威被违反**）。</summary>
    public long InputsRejected { get; private set; }

    /// <summary>累计打中了多少次普攻（判定通过）。</summary>
    public long AttacksLanded { get; private set; }

    /// <summary>累计有几次普攻没打出去（射程外/冷却中/目标没了 —— 只记日志）。</summary>
    public long AttacksRefused { get; private set; }

    /// <summary>把输入处理器注册进路由（**显式注册**，见 `ServerMessageRouter`）。</summary>
    /// <param name="router">路由。</param>
    public void RegisterHandlers(ServerMessageRouter router)
    {
        if (router == null)
        {
            throw new ArgumentNullException(nameof(router));
        }

        router.Register(ClientMessage.PayloadOneofCase.Input, HandleInput);
    }

    /// <summary>取某个房间的战斗世界（没有就返回 null）。</summary>
    /// <param name="roomId">房号。</param>
    /// <returns>世界或 null。</returns>
    public DungeonBattle? Find(string roomId)
    {
        DungeonBattle? battle;
        return _battles.TryGetValue(roomId, out battle) ? battle : null;
    }

    /// <summary>
    /// 推进一帧：收拾空房间 → 对齐英雄 → 应用输入 → 推进世界 → 下发快照。
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

            ReconcileHeroes(battle, room);      // ② 先让"席位"与"英雄"对上，再吃输入
            ApplyInputs(battle, room);          // ③ 移动是状态：每帧用最新那条
            battle.Step();                      // ④
            BroadcastSnapshot(battle, room);    // ⑤
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

        DungeonBattle? built = DungeonBattle.FromDungeon(room.RoomId, room.DungeonId, _tables, out string error);

        if (built == null)
        {
            // 副本人不在表里：**不静默开一个空世界**（那会表现成"进去什么都没有"，极难查）
            Note?.Invoke($"房间 {room.RoomId} 开不了：{error}");
            throw new InvalidOperationException($"[RoomBattleService] 房间 {room.RoomId} 开不了：{error}");
        }

        DungeonBattle battle = built;
        _battles.Add(room.RoomId, battle);

        Note?.Invoke($"房间 {room.RoomId} 开打：副本 {room.DungeonId}（{battle.BossCount} 个 BOSS、" +
                     $"{battle.Entities.Count - battle.BossCount} 个普通怪）");

        return battle;
    }

    /// <summary>
    /// 让"房间席位"和"世界里的英雄"对上：**缺的补、走的删**。
    /// <para>
    /// 一个席位一个英雄（同队打副本）。每帧做一次是刻意的：它**幂等**，
    /// 所以不管席位是"刚加进来"还是"刚掉线"，下一个 tick 世界就自己对齐了 ——
    /// 不需要在进房/退房/断线那些地方各写一遍（那些地方迟早会漏一个）。
    /// </para>
    /// </summary>
    /// <param name="battle">世界。</param>
    /// <param name="room">房间。</param>
    private void ReconcileHeroes(DungeonBattle battle, Room room)
    {
        // ① 席位没有英雄 → 补一个（出生点按席位序号，确定性）
        for (int i = 0; i < room.Seats.Count; i++)
        {
            long playerId = room.Seats[i].Session.PlayerId;

            if (playerId <= 0 || battle.FindHeroOfPlayer(playerId) != null)
            {
                continue;
            }

            (int spawnX, int spawnZ) = DungeonBattle.HeroSpawnPoint(i);
            battle.AddEntity(DungeonBattle.HeroConfigId, 0, battle.HeroMaxHp, spawnX, spawnZ,
                             playerId: playerId);

            Note?.Invoke($"玩家 {playerId}（{room.Seats[i].PlayerName}）进入副本，出生点 ({spawnX}, {spawnZ})mm");
        }

        // ② 世界里有英雄、但它对应的席位没了 → 移出（人走英雄走，不留在场上当靶子）
        for (int i = battle.Entities.Count - 1; i >= 0; i--)
        {
            BattleEntity entity = battle.Entities[i];

            if (entity.Kind != 0 || entity.PlayerId <= 0)
            {
                continue;
            }

            if (room.FindByPlayerId(entity.PlayerId) == null)
            {
                long gone = entity.PlayerId;
                battle.RemoveHeroOfPlayer(gone);
                _inputs.Remove(gone);
                Note?.Invoke($"玩家 {gone} 离开副本，英雄已移出世界");
            }
        }
    }

    /// <summary>
    /// 把"最近一条输入"应用到英雄身上（**移动是状态**：每帧用最新的那条）。
    /// </summary>
    /// <param name="battle">世界。</param>
    /// <param name="room">房间。</param>
    private void ApplyInputs(DungeonBattle battle, Room room)
    {
        for (int i = 0; i < room.Seats.Count; i++)
        {
            long playerId = room.Seats[i].Session.PlayerId;

            if (playerId <= 0)
            {
                continue;
            }

            PendingInput pending;

            if (!_inputs.TryGetValue(playerId, out pending))
            {
                continue;
            }

            if (battle.Tick - pending.ReceivedAtTick > InputFreshTicks)
            {
                // 见 `InputFreshTicks` 的注释：断流了就当没在按，别让英雄一直走
                continue;
            }

            battle.ApplyMoveToHero(playerId, pending.MoveX, pending.MoveY);
        }
    }

    /// <summary>
    /// 处理一条输入（两条规则见文件头第二节：移动是状态、动作是事件）。
    /// </summary>
    /// <param name="session">来源会话。</param>
    /// <param name="message">消息。</param>
    /// <returns>分发结果。</returns>
    private DispatchResult HandleInput(ClientSession session, ClientMessage message)
    {
        PlayerInput input = message.Input;

        if (input == null)
        {
            return DispatchResult.KickOut("Input 消息里没有 PlayerInput 字段（协议违规）");
        }

        InputsReceived++;

        // ① 权威检查：你只能动你自己的英雄（见文件头第三节）
        if (input.PlayerId != session.PlayerId)
        {
            InputsRejected++;
            Note?.Invoke($"会话 {session.SessionId} 发来的输入写着 player_id={input.PlayerId}，" +
                         $"但它是玩家 {session.PlayerId} —— 已忽略");

            return DispatchResult.ReplyWith(Error(NetErrors.PlayerMismatch,
                $"输入里的 player_id={input.PlayerId} 不是你（你是 {session.PlayerId}）"));
        }

        Room? room = _registry.FindBySession(session);

        if (room == null)
        {
            // 不在房间里发输入：不算违规（客户端可能刚退房），忽略即可
            return DispatchResult.Handled;
        }

        DungeonBattle battle = EnsureBattle(room);

        // ② 移动：只记最新的那条；钳位是**不信任客户端**的第一道门
        _inputs[input.PlayerId] = new PendingInput(
            ClampAxis(input.MoveX), ClampAxis(input.MoveY), battle.Tick);

        // ③ 动作：第 0 位 = 技能1（M3 里就是普攻）—— 立刻判定
        if ((input.ActionBits & 1u) != 0)
        {
            ResolveBasicAttack(battle, session, input);
        }

        return DispatchResult.Handled;
    }

    /// <summary>判定一次普攻（服务端说了算；被拒只记日志，不回错误）。</summary>
    /// <param name="battle">世界。</param>
    /// <param name="session">来源会话。</param>
    /// <param name="input">输入。</param>
    private void ResolveBasicAttack(DungeonBattle battle, ClientSession session, PlayerInput input)
    {
        // 除了权威校验，其余校验一律在 `DungeonBattle` 里 —— 规则只有一份实现
        BattleEntity? attacker = battle.FindHeroOfPlayer(session.PlayerId);

        if (attacker == null)
        {
            return;
        }

        string reason;

        if (battle.TryBasicAttack(attacker, input.TargetEntityId, out reason))
        {
            AttacksLanded++;
            Note?.Invoke($"玩家 {session.PlayerId} 普攻打中单位 {input.TargetEntityId}" +
                         $"（{battle.BasicAttackDamage} 点，来自 Skill 表）");
        }
        else
        {
            AttacksRefused++;
            Note?.Invoke($"玩家 {session.PlayerId} 的普攻没打出去：{reason}");
        }
    }

    /// <summary>把客户端给的轴钳到 -1000..1000（**不信任客户端**）。</summary>
    /// <param name="value">轴值。</param>
    /// <returns>钳位后的轴。</returns>
    private static int ClampAxis(int value)
        => value < -1000 ? -1000 : (value > 1000 ? 1000 : value);

    /// <summary>造一条 `ErrorResponse`。</summary>
    /// <param name="code">错误码。</param>
    /// <param name="message">人话。</param>
    /// <returns>服务端消息。</returns>
    private static ServerMessage Error(int code, string message)
    {
        return new ServerMessage
        {
            Error = new ErrorResponse { Code = code, Message = message },
        };
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

    /// <summary>房间没了就把它的世界删掉（见文件头第四节）。</summary>
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

    /// <summary>一条待应用的输入（移动轴 + 它是**哪一帧**收到的，用来判保鲜期）。</summary>
    private readonly struct PendingInput
    {
        /// <summary>左右轴（已钳位）。</summary>
        public readonly int MoveX;

        /// <summary>前后轴（已钳位）。</summary>
        public readonly int MoveY;

        /// <summary>收到它时世界的帧号。</summary>
        public readonly long ReceivedAtTick;

        /// <summary>记一条输入。</summary>
        /// <param name="moveX">左右轴。</param>
        /// <param name="moveY">前后轴。</param>
        /// <param name="receivedAtTick">收到时的帧号。</param>
        public PendingInput(int moveX, int moveY, long receivedAtTick)
        {
            MoveX = moveX;
            MoveY = moveY;
            ReceivedAtTick = receivedAtTick;
        }
    }
}
