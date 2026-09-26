// ============================================================================
//  NBC.Server.Game —— 服务端权威的副本战斗世界（M3 状态同步）
//  项目：3D联网战斗Demo
//  对应：需求文档 §6.3 / §13.3（SRV-08）、Docs\25 §三 D3/D5、§四 S5
//
//  ---------------------------------------------------------------------------
//  一、它在整条链上的位置（和 M0 那个 `BattleInstance` 什么关系）
//  ---------------------------------------------------------------------------
//      `DungeonBattle`（本文件） = **一个世界**：有哪些单位、各自多少血、在哪儿、谁死了
//      `BattleInstance`（M0 骨架）= **同步策略的宿主**：状态同步 / 帧同步 / 状态帧同步（M4 的事）
//
//  M3 只做状态同步，所以现在需要的是"一个能跑、能被拍快照的世界"，而不是三种策略的分发器。
//  两者共用 `TickScheduler` 与**共享层的结算逻辑**；M4 做帧同步时，由 `BattleInstance` 去驱动它。
//  （把这条写清楚，是为了避免以后出现"两个战斗世界"而没人知道该用哪个。）
//
//  ---------------------------------------------------------------------------
//  二、D3 的服务端权威，落到代码上就是三句话
//  ---------------------------------------------------------------------------
//    ① **伤害在这里算**（调共享层 `DamageMath.Resolve`），客户端只发意图
//    ② **位置在这里推进**（整数毫米、按 tick 走），客户端只按快照显示
//    ③ **世界只有一个**：客户端那边没有"自己的那份血条"，只有服务端发来的快照
//
//  ---------------------------------------------------------------------------
//  三、为什么位置用**整数毫米**、巡逻用**毫米/帧**
//  ---------------------------------------------------------------------------
//  和 A7 的输入量化、D2 的定点数是同一条判据：**要过网线、要两端一致的东西不许是浮点**。
//  `float` 在不同运行时（Mono / IL2CPP / .NET 8）的舍入可以不同，一次不同之后每一步都会放大。
//  所以：位置是 `int`（毫米），速度是"每逻辑帧多少毫米"（`int`），移动就是整数加减与钳位 ——
//  同一个输入序列，在任何机器上跑出来都一样。
//
//  ⚠️ M3 **不做客户端预测**（D4）：所以客户端的表现必然滞后一个 RTT，这是**有意**的取舍，
//     预测 + 回滚属于 M4 的帧同步。这一条在验收时要主动说明，别被当成 bug。
//
//  ---------------------------------------------------------------------------
//  四、关卡从哪儿来（S6 要接的地方）
//  ---------------------------------------------------------------------------
//  `CreateStarterDungeon` 现在**写死了三个单位**（1 个英雄 + 2 个怪）—— 因为 `Dungeon` / `DropTable`
//  配置表是 S6 的事。这里刻意只写"最小可信的一局"，把"从配置表读关卡"留成一个明确的 TODO，
//  而不是现在就假装有配置系统。
// ============================================================================

using System.Collections.Generic;
using NBC.Protocol;
using NBC.Shared.Battle;

namespace NBC.Server.Game;

/// <summary>世界里的一个单位（服务端权威的那一份数据）。</summary>
public sealed class BattleEntity
{
    /// <summary>运行时实例编号（同一个 `ConfigId` 刷两只也不会撞号 —— M2 记过这条）。</summary>
    public int Id { get; }

    /// <summary>配置表编号（表现层用它找模型/名字）。</summary>
    public int ConfigId { get; }

    /// <summary>0 = 英雄，1 = 怪物（对应 `EBattleAgentKind`）。</summary>
    public int Kind { get; }

    /// <summary>
    /// 这个单位属于哪个玩家（**0 = 无主**，例如怪物）。
    /// <para>M3-S5b 加的：一个席位一个英雄，服务端靠它把"收到的输入"对到"该动哪个单位"上。</para>
    /// </summary>
    public long PlayerId { get; }

    /// <summary>当前血量（**永不为负**；归零即死亡）。</summary>
    public int Hp { get; private set; }

    /// <summary>最大血量。</summary>
    public int MaxHp { get; }

    /// <summary>位置 X（毫米）。</summary>
    public int PosXmm { get; private set; }

    /// <summary>位置 Z（毫米）。M3 先在平面上跑，Y 轴留空。</summary>
    public int PosZmm { get; private set; }

    /// <summary>朝向（度，0..359）。</summary>
    public int FacingDeg { get; private set; }

    /// <summary>还活着吗。</summary>
    public bool Alive => Hp > 0;

    /// <summary>巡逻速度（毫米/帧；0 = 不动的单位）。</summary>
    public int PatrolSpeedMmPerTick { get; }

    /// <summary>巡逻区间（毫米）。</summary>
    public int PatrolMinMm { get; }

    /// <summary>巡逻区间（毫米）。</summary>
    public int PatrolMaxMm { get; }

    /// <summary>当前巡逻方向：+1 / -1。</summary>
    public int PatrolDir { get; private set; } = 1;

    /// <summary>普攻冷却还剩几帧（&gt; 0 时打不出来）。**由 `Step()` 每帧递减**。</summary>
    public int AttackCooldownTicksLeft { get; private set; }

    /// <summary>造一个单位。</summary>
    /// <param name="id">实例编号。</param>
    /// <param name="configId">配置编号。</param>
    /// <param name="kind">0 英雄 / 1 怪物。</param>
    /// <param name="maxHp">最大血量。</param>
    /// <param name="posXmm">初始 X。</param>
    /// <param name="posZmm">初始 Z。</param>
    /// <param name="patrolSpeedMmPerTick">巡逻速度（毫米/帧）。</param>
    /// <param name="patrolMinMm">巡逻下限。</param>
    /// <param name="patrolMaxMm">巡逻上限。</param>
    /// <param name="playerId">所属玩家（0 = 无主）。</param>
    public BattleEntity(int id, int configId, int kind, int maxHp, int posXmm, int posZmm,
                        int patrolSpeedMmPerTick = 0, int patrolMinMm = 0, int patrolMaxMm = 0,
                        long playerId = 0)
    {
        Id = id;
        ConfigId = configId;
        Kind = kind;
        MaxHp = maxHp;
        Hp = maxHp;
        PosXmm = posXmm;
        PosZmm = posZmm;
        PatrolSpeedMmPerTick = patrolSpeedMmPerTick;
        PatrolMinMm = patrolMinMm;
        PatrolMaxMm = patrolMaxMm;
        PlayerId = playerId;
    }

    /// <summary>推进一帧（冷却倒计时 + 巡逻移动 + 朝向）。</summary>
    public void Step()
    {
        if (AttackCooldownTicksLeft > 0)
        {
            AttackCooldownTicksLeft--;
        }

        if (!Alive || PatrolSpeedMmPerTick == 0)
        {
            return;
        }

        PosXmm += PatrolSpeedMmPerTick * PatrolDir;

        // 撞到区间边界就掉头（**钳位**，避免"速度大于区间"时越走越远）
        if (PosXmm >= PatrolMaxMm)
        {
            PosXmm = PatrolMaxMm;
            PatrolDir = -1;
        }
        else if (PosXmm <= PatrolMinMm)
        {
            PosXmm = PatrolMinMm;
            PatrolDir = 1;
        }

        FacingDeg = PatrolDir > 0 ? 90 : 270;
    }

    /// <summary>扣血（只由 <see cref="DungeonBattle.ApplyDamage"/> 调，保证口径一致）。</summary>
    /// <param name="remainingHp">结算后的血量。</param>
    public void SetHp(int remainingHp) => Hp = remainingHp < 0 ? 0 : remainingHp;

    /// <summary>开始一次普攻冷却（判定通过之后由 `DungeonBattle` 调）。</summary>
    /// <param name="ticks">冷却帧数。</param>
    public void StartAttackCooldown(int ticks) => AttackCooldownTicksLeft = ticks;

    /// <summary>
    /// 按量化输入移动一格（**整数运算**：轴是千分之一，速度是"毫米/帧"）。
    /// <para>
    /// 斜向不超速：两轴都非零时按 `1000/1414` 缩放（√2 的整数近似），
    /// 于是"合速度"仍然是一个 `speedMmPerTick`，不会出现"斜着走更快"这种手感 bug。
    /// </para>
    /// </summary>
    /// <param name="moveX">左右轴（-1000..1000）。</param>
    /// <param name="moveY">前后轴（-1000..1000）。</param>
    /// <param name="speedMmPerTick">满速时的毫米/帧。</param>
    /// <param name="halfExtentMm">场地半径（毫米），出界钳位。</param>
    public void ApplyMove(int moveX, int moveY, int speedMmPerTick, int halfExtentMm)
    {
        if (!Alive)
        {
            return;
        }

        int axis = Math.Abs(moveX) > Math.Abs(moveY) ? Math.Abs(moveX) : Math.Abs(moveY);

        if (axis == 0)
        {
            return;
        }

        if (axis > 1000)
        {
            axis = 1000;        // 越界的轴直接当满速（服务端不信任客户端给的数）
        }

        int dx = moveX * speedMmPerTick / axis;
        int dz = moveY * speedMmPerTick / axis;

        if (moveX != 0 && moveY != 0)
        {
            dx = dx * 1000 / 1414;
            dz = dz * 1000 / 1414;
        }

        PosXmm = Clamp(PosXmm + dx, -halfExtentMm, halfExtentMm);
        PosZmm = Clamp(PosZmm + dz, -halfExtentMm, halfExtentMm);

        if (dx != 0 || dz != 0)
        {
            FacingDeg = FacingOf(dx, dz);
        }
    }

    /// <summary>取整数。</summary>
    /// <param name="value">值。</param>
    /// <param name="min">下限。</param>
    /// <param name="max">上限。</param>
    /// <returns>钳位后的值。</returns>
    private static int Clamp(int value, int min, int max)
        => value < min ? min : (value > max ? max : value);

    /// <summary>把位移方向变成 0..359 的朝向（八向就够；M3 不做平滑转身，两家也不必一致）。</summary>
    /// <param name="dx">X 位移。</param>
    /// <param name="dz">Z 位移。</param>
    /// <returns>朝向（度）。</returns>
    private static int FacingOf(int dx, int dz)
    {
        if (dx == 0)
        {
            return dz > 0 ? 0 : 180;
        }

        if (dz == 0)
        {
            return dx > 0 ? 90 : 270;
        }

        if (dx > 0)
        {
            return dz > 0 ? 45 : 135;
        }

        return dz > 0 ? 315 : 225;
    }

    /// <summary>转成协议里的快照条目。</summary>
    /// <returns>快照条目。</returns>
    public EntitySnapshot ToSnapshot()
    {
        return new EntitySnapshot
        {
            EntityId = Id,
            ConfigId = ConfigId,
            Kind = Kind,
            Hp = Hp,
            MaxHp = MaxHp,
            PosXMm = PosXmm,
            PosZMm = PosZmm,
            FacingDeg = FacingDeg,
            Alive = Alive,
        };
    }
}

/// <summary>一次伤害结算的结果（服务端广播事件与日志都用它）。</summary>
public readonly struct DamageResult
{
    /// <summary>被打的目标（找不到则为 null）。</summary>
    public readonly BattleEntity? Target;

    /// <summary>结算结果（目标不存在时是默认值，用 <see cref="Ok"/> 判断）。</summary>
    public readonly DamageOutcome Outcome;

    /// <summary>算出这次伤害的攻击者（可能为 0 = 环境）。</summary>
    public readonly int AttackerId;

    /// <summary>成功结算了吗。</summary>
    public readonly bool Ok;

    private DamageResult(BattleEntity? target, DamageOutcome outcome, int attackerId, bool ok)
    {
        Target = target;
        Outcome = outcome;
        AttackerId = attackerId;
        Ok = ok;
    }

    /// <summary>成功。</summary>
    /// <param name="target">目标。</param>
    /// <param name="outcome">结算结果。</param>
    /// <param name="attackerId">攻击者。</param>
    /// <returns>结果。</returns>
    public static DamageResult Success(BattleEntity target, DamageOutcome outcome, int attackerId)
        => new(target, outcome, attackerId, true);

    /// <summary>目标不存在。</summary>
    /// <returns>结果。</returns>
    public static DamageResult Missing() => new(null, default, 0, false);
}

/// <summary>一局副本战斗的**权威世界**（M3 状态同步）。</summary>
public sealed class DungeonBattle
{
    /// <summary>英雄满速移动（毫米/帧）。150 × 30Hz = 4.5 米/秒 —— 走路偏快、跑步偏慢，先这么定。</summary>
    public const int HeroMoveMmPerTick = 150;

    /// <summary>场地半径（毫米）：出界钳位。5 米见方，够 M3 演示走位。</summary>
    public const int PlayAreaHalfExtentMm = 5000;

    /// <summary>普攻伤害。TODO(S6)：接 `Skill` 配置表之后从表里读。</summary>
    public const int BasicAttackDamage = 30;

    /// <summary>普攻射程（毫米，切比雪夫距离）。</summary>
    public const int BasicAttackRangeMm = 2000;

    /// <summary>普攻冷却（帧）。15 帧 = 0.5 秒（30Hz）。</summary>
    public const int BasicAttackCooldownTicks = 15;

    /// <summary>英雄的配置编号。TODO(S6)：从 `Dungeon`/`Hero` 表里读。</summary>
    public const int HeroConfigId = 1001;

    /// <summary>英雄的最大血量。</summary>
    public const int HeroMaxHp = 300;

    private readonly List<BattleEntity> _entities = new();
    private int _nextEntityId = 1;

    /// <summary>房号（和 `Room.RoomId` 对应）。</summary>
    public string RoomId { get; }

    /// <summary>副本编号。</summary>
    public int DungeonId { get; }

    /// <summary>当前逻辑帧号（从 0 开始，每 `Step` 一次 +1）。</summary>
    public long Tick { get; private set; }

    /// <summary>世界里的单位（顺序稳定 = 快照顺序稳定）。</summary>
    public IReadOnlyList<BattleEntity> Entities => _entities;

    /// <summary>活着的怪物数。</summary>
    public int AliveMonsterCount
    {
        get
        {
            int count = 0;

            for (int i = 0; i < _entities.Count; i++)
            {
                if (_entities[i].Kind == 1 && _entities[i].Alive)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>活着的英雄数。</summary>
    public int AliveHeroCount
    {
        get
        {
            int count = 0;

            for (int i = 0; i < _entities.Count; i++)
            {
                if (_entities[i].Kind == 0 && _entities[i].Alive)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>本局结束了吗（怪物清空 或 英雄全灭）。</summary>
    public bool IsFinished => AliveMonsterCount == 0 || AliveHeroCount == 0;

    /// <summary>造一个世界。</summary>
    /// <param name="roomId">房号。</param>
    /// <param name="dungeonId">副本编号。</param>
    public DungeonBattle(string roomId, int dungeonId)
    {
        RoomId = roomId;
        DungeonId = dungeonId;
    }

    /// <summary>加一个单位。</summary>
    /// <param name="configId">配置编号。</param>
    /// <param name="kind">0 英雄 / 1 怪物。</param>
    /// <param name="maxHp">最大血量。</param>
    /// <param name="posXmm">初始 X（毫米）。</param>
    /// <param name="posZmm">初始 Z（毫米）。</param>
    /// <param name="patrolSpeedMmPerTick">巡逻速度（毫米/帧）。</param>
    /// <param name="patrolMinMm">巡逻下限。</param>
    /// <param name="patrolMaxMm">巡逻上限。</param>
    /// <param name="playerId">所属玩家（0 = 无主）。</param>
    /// <returns>新单位。</returns>
    public BattleEntity AddEntity(int configId, int kind, int maxHp, int posXmm, int posZmm,
                                  int patrolSpeedMmPerTick = 0, int patrolMinMm = 0, int patrolMaxMm = 0,
                                  long playerId = 0)
    {
        var entity = new BattleEntity(_nextEntityId, configId, kind, maxHp, posXmm, posZmm,
                                      patrolSpeedMmPerTick, patrolMinMm, patrolMaxMm, playerId);
        _nextEntityId++;
        _entities.Add(entity);
        return entity;
    }

    /// <summary>找某个玩家的英雄（没有就返回 null）。</summary>
    /// <param name="playerId">玩家 id。</param>
    /// <returns>英雄或 null。</returns>
    public BattleEntity? FindHeroOfPlayer(long playerId)
    {
        for (int i = 0; i < _entities.Count; i++)
        {
            if (_entities[i].Kind == 0 && _entities[i].PlayerId == playerId)
            {
                return _entities[i];
            }
        }

        return null;
    }

    /// <summary>把某个玩家的英雄移出世界（他离开房间了）。</summary>
    /// <param name="playerId">玩家 id。</param>
    /// <returns>确实移除了返回 true。</returns>
    public bool RemoveHeroOfPlayer(long playerId)
    {
        for (int i = 0; i < _entities.Count; i++)
        {
            if (_entities[i].Kind == 0 && _entities[i].PlayerId == playerId)
            {
                _entities.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 给某个玩家的英雄施加一帧移动（**服务端权威**：这里决定走多远、能不能走）。
    /// </summary>
    /// <param name="playerId">玩家 id。</param>
    /// <param name="moveX">左右轴（-1000..1000）。</param>
    /// <param name="moveY">前后轴（-1000..1000）。</param>
    /// <returns>确实移动了返回 true（英雄不存在或已死返回 false）。</returns>
    public bool ApplyMoveToHero(long playerId, int moveX, int moveY)
    {
        BattleEntity? hero = FindHeroOfPlayer(playerId);

        if (hero == null)
        {
            return false;
        }

        hero.ApplyMove(moveX, moveY, HeroMoveMmPerTick, PlayAreaHalfExtentMm);
        return true;
    }

    /// <summary>
    /// 普攻判定（**服务端说了算**）：目标存在、活着、在射程内、冷却已好 —— 四条都满足才扣血。
    /// <para>拒绝理由会写进 `reason`（人话），由调用方记日志；**不**回 `ErrorResponse`
    /// （原因由世界状态决定，客户端自己那份世界看得到，回错误会变成每帧刷屏）。</para>
    /// </summary>
    /// <param name="attacker">出手的单位。</param>
    /// <param name="targetId">目标实例编号。</param>
    /// <param name="reason">被拒的原因（成功时为空串）。</param>
    /// <returns>真的打中了返回 true。</returns>
    public bool TryBasicAttack(BattleEntity attacker, int targetId, out string reason)
    {
        reason = string.Empty;

        if (!attacker.Alive)
        {
            reason = "出手的单位已经死了";
            return false;
        }

        if (attacker.AttackCooldownTicksLeft > 0)
        {
            reason = $"普攻还在冷却（还剩 {attacker.AttackCooldownTicksLeft} 帧）";
            return false;
        }

        BattleEntity? target = Find(targetId);

        if (target == null)
        {
            reason = $"目标 {targetId} 不存在";
            return false;
        }

        if (!target.Alive)
        {
            reason = $"目标 {targetId} 已经死了";
            return false;
        }

        if (target.Kind == attacker.Kind)
        {
            reason = "不能打自己人";
            return false;
        }

        if (DistanceMm(attacker, target) > BasicAttackRangeMm)
        {
            reason = $"目标 {targetId} 在射程外（{DistanceMm(attacker, target)}mm > {BasicAttackRangeMm}mm）";
            return false;
        }

        ApplyDamage(targetId, BasicAttackDamage, attacker.Id);
        attacker.StartAttackCooldown(BasicAttackCooldownTicks);
        return true;
    }

    /// <summary>两个单位在平面上的整数距离（毫米，向下取整的近似：先比平方再开方太费，M3 用切比雪夫距离）。</summary>
    /// <param name="a">甲。</param>
    /// <param name="b">乙。</param>
    /// <returns>距离（毫米）。</returns>
    public static int DistanceMm(BattleEntity a, BattleEntity b)
    {
        int dx = Math.Abs(a.PosXmm - b.PosXmm);
        int dz = Math.Abs(a.PosZmm - b.PosZmm);
        return dx > dz ? dx : dz;
    }

    /// <summary>第 `index` 个席位的出生点（确定性：同一席位每次都在同一个地方）。</summary>
    /// <param name="index">席位序号（0 起）。</param>
    /// <returns>(x, z) 毫米。</returns>
    public static (int X, int Z) HeroSpawnPoint(int index)
    {
        switch (index & 3)
        {
            case 0: return (-1000, 0);
            case 1: return (1000, 0);
            case 2: return (0, -1000);
            default: return (0, 1000);
        }
    }

    /// <summary>按实例编号找单位。</summary>
    /// <param name="entityId">实例编号。</param>
    /// <returns>单位或 null。</returns>
    public BattleEntity? Find(int entityId)
    {
        for (int i = 0; i < _entities.Count; i++)
        {
            if (_entities[i].Id == entityId)
            {
                return _entities[i];
            }
        }

        return null;
    }

    /// <summary>推进一帧（服务端权威）。</summary>
    public void Step()
    {
        Tick++;

        for (int i = 0; i < _entities.Count; i++)
        {
            _entities[i].Step();
        }
    }

    /// <summary>
    /// 结算一次伤害（**复用共享层 `DamageMath`** —— 与客户端同一份规则）。
    /// <para>⚠️ 目标已经死了就不再结算（"打尸体"不该产生第二次致死；M2 已经把这条钉在共享层里）。</para>
    /// </summary>
    /// <param name="targetId">被打的实例编号。</param>
    /// <param name="damage">伤害值（≥ 0）。</param>
    /// <param name="attackerId">攻击者实例编号（0 = 环境/未知）。</param>
    /// <returns>结算结果。</returns>
    public DamageResult ApplyDamage(int targetId, int damage, int attackerId = 0)
    {
        BattleEntity? target = Find(targetId);

        if (target == null)
        {
            return DamageResult.Missing();
        }

        DamageOutcome outcome = DamageMath.Resolve(target.Hp, damage);
        target.SetHp(outcome.RemainingHp);

        return DamageResult.Success(target, outcome, attackerId);
    }

    /// <summary>把整个世界拍成一张快照（D5：**每 tick 全量**）。</summary>
    /// <returns>快照。</returns>
    public WorldSnapshot ToSnapshot()
    {
        var snapshot = new WorldSnapshot { ServerTick = (int)Tick };

        for (int i = 0; i < _entities.Count; i++)
        {
            snapshot.Entities.Add(_entities[i].ToSnapshot());
        }

        return snapshot;
    }

    /// <summary>
    /// M3 的临时关卡：**只有怪**（2 只巡逻的狼）。
    /// <para>
    /// ⚠️ 2026-09-23（S5b）改过：原来这里顺手放了一个"英雄"，现在**英雄由席位决定**
    /// （`RoomBattleService` 每个席位补一个英雄、席位走了就移出）——
    /// 否则 2 人房里会多出一个没人控制的幽灵英雄。
    /// </para>
    /// <para>⚠️ TODO(S6)：改从 `Dungeon` / `Monster` 配置表生成（S6 加表**不改工具代码**）。</para>
    /// </summary>
    /// <param name="roomId">房号。</param>
    /// <param name="dungeonId">副本编号。</param>
    /// <returns>世界。</returns>
    public static DungeonBattle CreateStarterDungeon(string roomId, int dungeonId)
    {
        var battle = new DungeonBattle(roomId, dungeonId);

        // 两只狼：在 2 米区间里来回巡逻（60 毫米/帧 ≈ 1.8 米/秒）
        battle.AddEntity(configId: 6001, kind: 1, maxHp: 120, posXmm: 2000, posZmm: 1000,
                         patrolSpeedMmPerTick: 60, patrolMinMm: 1500, patrolMaxMm: 2500);
        battle.AddEntity(configId: 6001, kind: 1, maxHp: 120, posXmm: 2000, posZmm: -1000,
                         patrolSpeedMmPerTick: 40, patrolMinMm: 1000, patrolMaxMm: 2500);

        return battle;
    }
}
