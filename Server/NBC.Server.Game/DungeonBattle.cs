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
using NBC.Shared.Net;

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

    /// <summary>是不是 BOSS（S6：BOSS 也是怪，但"+只数一个"用得上这个标记）。</summary>
    public bool IsBoss { get; private set; }

    /// <summary>它一次普攻的伤害（英雄来自 `Skill` 表，怪来自 `Monster.attack`）。**每个单位各一份**。</summary>
    public int AttackDamage { get; }

    /// <summary>它的移动速度（毫米/帧）。英雄来自 `Hero` 表；怪用常量（表里还没有这一列，见 TODO）。</summary>
    public int MoveSpeedMmPerTick { get; }

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
    /// <param name="isBoss">是不是 BOSS。</param>
    /// <param name="attackDamage">普攻伤害。</param>
    /// <param name="moveSpeedMmPerTick">移动速度（毫米/帧）。</param>
    public BattleEntity(int id, int configId, int kind, int maxHp, int posXmm, int posZmm,
                        int patrolSpeedMmPerTick = 0, int patrolMinMm = 0, int patrolMaxMm = 0,
                        long playerId = 0, bool isBoss = false,
                        int attackDamage = 0, int moveSpeedMmPerTick = 0)
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
        IsBoss = isBoss;
        AttackDamage = attackDamage;
        MoveSpeedMmPerTick = moveSpeedMmPerTick;
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
    /// <summary>场地半径（毫米）：出界钳位。5 米见方，够 M3 演示走位。</summary>
    public const int PlayAreaHalfExtentMm = 5000;

    /// <summary>
    /// 普攻射程（毫米，切比雪夫距离）。
    /// <para>⚠️ TODO(S6+)：表里还没有"射程"这一列（`Skill` 表只有伤害与命中帧）。
    /// 加起来是"加表"的事，等真需要不同技能不同射程时再做 —— **别提前占坑**。</para>
    /// </summary>
    public const int BasicAttackRangeMm = 2000;

    /// <summary>普攻冷却（帧）。15 帧 = 0.5 秒（30Hz）。</summary>
    public const int BasicAttackCooldownTicks = 15;

    /// <summary>
    /// 普通怪的移速（毫米/帧）：**表里还没有这一列**。
    /// <para>⚠️ TODO(S6+)：`Monster` 表加一列 `moveSpeed` 之后改从表读（加列 = 加表，工具不用改）。
    /// 现在先用常量，并在文件头/文档里记着 —— **别让它悄悄变成"没人知道的魔法数"**。</para>
    /// </summary>
    public const int MonsterMoveMmPerTick = 40;

    /// <summary>BOSS 的追击速度（毫米/帧）。TODO(S6+)：同上，等 `Monster.moveSpeed` 那一列。</summary>
    public const int BossMoveMmPerTick = 90;

    /// <summary>英雄的配置编号（M3 固定用剑士）。TODO(S6+)：让它跟玩家选的英雄走。</summary>
    public const int HeroConfigId = 1001;

    private readonly List<BattleEntity> _entities = new();

    /// <summary>BOSS 大脑（每 tick 想一次，见 `BossBrain`）。</summary>
    private readonly List<BossBrain> _brains = new();

    /// <summary>待广播的服务端事件（掉落等；由 `RoomBattleService` 每帧取走，S7）。</summary>
    private readonly List<DropEvent> _pendingDrops = new();

    /// <summary>待广播的伤害事件（M4-S1：每一次扣血一条）。</summary>
    private readonly List<DamageEvent> _pendingHits = new();

    /// <summary>待广播的死亡事件（M4-S1：英雄与怪都发，客户端按 `kind` 分派）。</summary>
    private readonly List<DeathEvent> _pendingDeaths = new();

    /// <summary>本局的**确定性**随机数（见 `BattleRandom` 的注释：为什么不能用 `System.Random`）。</summary>
    private readonly BattleRandom _random;

    /// <summary>配置表（掷掉落要用；手工造的世界可以是 null = 不掉落）。</summary>
    private ServerTables? _tables;

    private int _nextEntityId = 1;

    /// <summary>英雄满速移动（毫米/帧）—— **来自 `Hero` 表**（毫米/秒 ÷ 30Hz）。</summary>
    public int HeroMoveMmPerTick { get; }

    /// <summary>英雄最大血量 —— **来自 `Hero` 表**。</summary>
    public int HeroMaxHp { get; }

    /// <summary>英雄普攻伤害 —— **来自 `Skill` 表**（英雄的第一个技能）。</summary>
    public int BasicAttackDamage { get; }

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

    /// <summary>本局所有 BOSS 大脑（探针/调试面板看 AI 状态用）。</summary>
    public IReadOnlyList<BossBrain> Brains => _brains;

    /// <summary>BOSS 数量（`Kind == 1` 且配置号等于 `Dungeon.bossId` 的单位）。</summary>
    public int BossCount
    {
        get
        {
            int count = 0;

            for (int i = 0; i < _entities.Count; i++)
            {
                if (_entities[i].IsBoss)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>本局结束了吗（怪物清空 或 英雄全灭）。</summary>
    public bool IsFinished => AliveMonsterCount == 0 || AliveHeroCount == 0;

    /// <summary>造一个世界（数值全来自配置表，见 <see cref="FromDungeon"/>）。</summary>
    /// <param name="roomId">房号。</param>
    /// <param name="dungeonId">副本编号。</param>
    /// <param name="heroMoveMmPerTick">英雄移速（毫米/帧，来自 `Hero` 表）。</param>
    /// <param name="heroMaxHp">英雄血量（来自 `Hero` 表）。</param>
    /// <param name="basicAttackDamage">普攻伤害（来自 `Skill` 表）。</param>
    public DungeonBattle(string roomId, int dungeonId,
                         int heroMoveMmPerTick, int heroMaxHp, int basicAttackDamage)
    {
        RoomId = roomId;
        DungeonId = dungeonId;
        HeroMoveMmPerTick = heroMoveMmPerTick;
        HeroMaxHp = heroMaxHp;
        BasicAttackDamage = basicAttackDamage;

        // 种子：**同一房间的同一局要能复现**（M3 用固定常数 + 房号派生；历史对局的复现要记种子，属 M5）
        _random = new BattleRandom(0x5EED_2026 ^ StableHash(roomId));
    }

    /// <summary>把配置表挂上来（`FromDungeon` 会调；挂上之后"怪死"才会掷掉落）。</summary>
    /// <param name="tables">配置表。</param>
    public void AttachTables(ServerTables tables) => _tables = tables;

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
    /// <param name="isBoss">是不是 BOSS。</param>
    /// <returns>新单位。</returns>
    public BattleEntity AddEntity(int configId, int kind, int maxHp, int posXmm, int posZmm,
                                  int patrolSpeedMmPerTick = 0, int patrolMinMm = 0, int patrolMaxMm = 0,
                                  long playerId = 0, bool isBoss = false,
                                  int attackDamage = 0, int moveSpeedMmPerTick = 0)
    {
        var entity = new BattleEntity(_nextEntityId, configId, kind, maxHp, posXmm, posZmm,
                                      patrolSpeedMmPerTick, patrolMinMm, patrolMaxMm, playerId, isBoss,
                                      attackDamage, moveSpeedMmPerTick);
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

        hero.ApplyMove(moveX, moveY, hero.MoveSpeedMmPerTick, PlayAreaHalfExtentMm);
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

        ApplyDamage(targetId, attacker.AttackDamage, attacker.Id);
        attacker.StartAttackCooldown(BasicAttackCooldownTicks);
        return true;
    }

    /// <summary>找**最近的活英雄**（BOSS 选目标用）。没有就返回 null。</summary>
    /// <param name="from">从谁那里量距离。</param>
    /// <returns>英雄或 null。</returns>
    public BattleEntity? NearestAliveHero(BattleEntity from)
    {
        BattleEntity? best = null;
        int bestDistance = int.MaxValue;

        for (int i = 0; i < _entities.Count; i++)
        {
            BattleEntity candidate = _entities[i];

            if (candidate.Kind != 0 || !candidate.Alive)
            {
                continue;
            }

            int distance = DistanceMm(from, candidate);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>够不够得着（切比雪夫距离 ≤ 普攻射程）。</summary>
    /// <param name="a">甲。</param>
    /// <param name="b">乙。</param>
    /// <returns>够得着返回 true。</returns>
    public bool InAttackRange(BattleEntity a, BattleEntity b)
        => DistanceMm(a, b) <= BasicAttackRangeMm;

    /// <summary>
    /// 朝目标走一格（**整数轴**：把方向变成 ±1000 的量化轴，交给 `ApplyMove` 统一处理斜向不超速）。
    /// </summary>
    /// <param name="mover">要移动的单位。</param>
    /// <param name="target">目标。</param>
    /// <returns>确实移动了返回 true。</returns>
    public bool MoveTowards(BattleEntity mover, BattleEntity target)
    {
        int dx = target.PosXmm - mover.PosXmm;
        int dz = target.PosZmm - mover.PosZmm;

        if (dx == 0 && dz == 0)
        {
            return false;
        }

        int moveX = dx == 0 ? 0 : (dx > 0 ? 1000 : -1000);
        int moveY = dz == 0 ? 0 : (dz > 0 ? 1000 : -1000);

        mover.ApplyMove(moveX, moveY, mover.MoveSpeedMmPerTick, PlayAreaHalfExtentMm);
        return true;
    }

    /// <summary>把两个单位在平面上的整数距离（毫米，向下取整的近似：先比平方再开方太费，M3 用切比雪夫距离）。</summary>
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

        // AI 在"世界推进一步之后"想：它看到的是**本帧的位置**，决策也在本帧生效
        for (int i = 0; i < _brains.Count; i++)
        {
            _brains[i].Think(this);
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

        bool wasAlive = target.Alive;

        DamageOutcome outcome = DamageMath.Resolve(target.Hp, damage);
        target.SetHp(outcome.RemainingHp);

        // M4-S1：**每一次扣血**都记一条待广播的伤害事件（表现层的飘字、以及"造成伤害"类条件都靠它）。
        // ⚠️ 记的是 `outcome.Applied`（**实际扣掉的血**），不是传进来的 `damage` ——
        //    打只剩 5 血的怪，打出去 100 也只该显示 5。
        _pendingHits.Add(new DamageEvent
        {
            AttackerId = attackerId,
            TargetId = targetId,
            Applied = outcome.Applied,
            RemainingHp = outcome.RemainingHp,
        });

        // **从活着打到死**那一下才掷掉落（打尸体不重复掉，M2 已经在共享层钉过"打 0 血不判致死"）
        if (wasAlive && !target.Alive)
        {
            // M4-S1：死亡事件**两种单位都发**（英雄也会死）。
            // ⚠️ 客户端那边靠 `kind` 分派成两个不同的游戏事件（`MonsterDied` / `HeroDied`）——
            //    理由见 `BattleEvents.cs` 文件头：合并成一个"带 kind 的死亡事件"会让
            //    "玩家死了"被误当成"击杀了一个目标 0"，而 0 = 任意目标 ⇒ 任何击杀任务都会涨进度。
            _pendingDeaths.Add(new DeathEvent
            {
                EntityId = target.Id,
                ConfigId = target.ConfigId,
                Kind = target.Kind,
                KillerId = attackerId,
            });

            if (target.Kind == 1 && _tables != null)
            {
                BattleEntity? attacker = Find(attackerId);
                long winner = attacker == null ? 0 : attacker.PlayerId;
                RollDrops(_tables, target.ConfigId, winner);
            }
        }

        return DamageResult.Success(target, outcome, attackerId);
    }

    /// <summary>
    /// 取走待广播的**伤害事件**（取走即清空；`RoomBattleService` 每帧调一次，M4-S1）。
    /// </summary>
    /// <param name="buffer">接收结果的表（会先清空）。</param>
    public void CopyPendingHits(List<DamageEvent> buffer)
    {
        buffer.Clear();
        buffer.AddRange(_pendingHits);
        _pendingHits.Clear();
    }

    /// <summary>
    /// 取走待广播的**死亡事件**（取走即清空；`RoomBattleService` 每帧调一次，M4-S1）。
    /// </summary>
    /// <param name="buffer">接收结果的表（会先清空）。</param>
    public void CopyPendingDeaths(List<DeathEvent> buffer)
    {
        buffer.Clear();
        buffer.AddRange(_pendingDeaths);
        _pendingDeaths.Clear();
    }

    /// <summary>
    /// 取走待广播的掉落事件（**取走即清空**；`RoomBattleService` 每帧调一次）。
    /// </summary>
    /// <param name="buffer">接收结果的表（会先清空）。</param>
    public void CopyPendingDrops(List<DropEvent> buffer)
    {
        buffer.Clear();

        for (int i = 0; i < _pendingDrops.Count; i++)
        {
            buffer.Add(_pendingDrops[i]);
        }

        _pendingDrops.Clear();
    }

    /// <summary>
    /// 掷这个怪掉什么（**服务端权威**，S7）。概率是万分比；数量在 `countMin..countMax` 之间。
    /// <para>⚠️ 只有**从活着打到死**那一下才掷（`killed` 为 true），打尸体不重复掉。</para>
    /// </summary>
    /// <param name="tables">配置表。</param>
    /// <param name="monsterConfigId">死的那个怪的配置号。</param>
    /// <param name="winnerPlayerId">归谁（M3 = 击杀者）。</param>
    /// <returns>掉了几种（0 = 什么都没掉）。</returns>
    public int RollDrops(ServerTables tables, int monsterConfigId, long winnerPlayerId)
    {
        IReadOnlyList<DropRow> rows = tables.FindDrops(monsterConfigId);
        int rolled = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            DropRow row = rows[i];

            if (row.ChancePerTenThousand <= 0)
            {
                continue;       // 概率 0 = 这个怪这一条永不掉
            }

            if (_random.NextPerTenThousand() >= row.ChancePerTenThousand)
            {
                continue;
            }

            int min = row.CountMin < 1 ? 1 : row.CountMin;
            int max = row.CountMax < min ? min : row.CountMax;
            int count = min + _random.Next(max - min + 1);

            _pendingDrops.Add(new DropEvent
            {
                ItemId = row.ItemId,
                Count = count,
                WinnerPlayerId = winnerPlayerId,
            });

            rolled++;
        }

        return rolled;
    }

    /// <summary>把字符串房号搅成一个稳定的整数（同一房号永远同一个值）。</summary>
    /// <param name="roomId">房号。</param>
    /// <returns>散列值。</returns>
    private static int StableHash(string roomId)
    {
        int hash = 17;

        for (int i = 0; i < roomId.Length; i++)
        {
            hash = unchecked(hash * 31 + roomId[i]);
        }

        return hash;
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
    /// **按配置表造一局**（M3-S6 起）：阵容、血量、移速、普攻伤害**全部来自表**。
    /// <para>
    /// 表 → 世界的对应关系（一眼看懂）：
    /// · `Dungeon.monsters`（可重复 = 多只）→ 普通怪，摆在出生点半径附近、在 ±500mm 内巡逻
    /// · `Dungeon.bossId` → BOSS，摆在场地另一端
    /// · `Monster.hp` → 每只怪的最大血量
    /// · `Hero.hp` / `Hero.moveSpeed` → 英雄的血与移速（移速 **毫米/秒 ÷ 30Hz** = 毫米/帧）
    /// · `Hero.skillIds[0]` → `Skill.damage`，M3 当"普攻"用
    /// </para>
    /// <para>
    /// ⚠️ **英雄自己不在关卡里**（由 `RoomBattleService` 按席位补）—— 这是 S5b 定下的，见那个类的注释。
    /// </para>
    /// </summary>
    /// <param name="roomId">房号。</param>
    /// <param name="dungeonId">副本编号。</param>
    /// <param name="tables">配置表。</param>
    /// <param name="error">失败原因（副本人不在表里 / 怪的编号查不到）。</param>
    /// <returns>世界；失败返回 null。</returns>
    public static DungeonBattle? FromDungeon(string roomId, int dungeonId, ServerTables tables, out string error)
    {
        error = string.Empty;

        DungeonRow? dungeon = tables.FindDungeon(dungeonId);

        if (dungeon == null)
        {
            error = $"副本 {dungeonId} 不在 `Dungeon` 表里";
            return null;
        }

        // 英雄数值：表和"英雄配置编号"绑死（TODO(S6+)：跟玩家选的英雄走）
        HeroRow? hero = tables.FindHero(HeroConfigId);

        if (hero == null)
        {
            error = $"英雄 {HeroConfigId} 不在 `Hero` 表里";
            return null;
        }

        int heroMoveMmPerTick = hero.Value.MoveSpeedMmPerSec / NetContract.TickRate;

        if (heroMoveMmPerTick <= 0)
        {
            error = $"英雄 {HeroConfigId} 的 moveSpeed={hero.Value.MoveSpeedMmPerSec} 太小，除以 {NetContract.TickRate}Hz 之后是 0";
            return null;
        }

        int attackDamage = 0;
        int[] heroSkills = hero.Value.SkillIds;

        if (heroSkills.Length > 0)
        {
            SkillRow? skill = tables.FindSkill(heroSkills[0]);
            attackDamage = skill?.Damage ?? 0;
        }

        if (attackDamage <= 0)
        {
            error = $"英雄 {HeroConfigId} 的普攻伤害算出来是 {attackDamage}（检查 `Hero.skillIds[0]` 与 `Skill.damage`）";
            return null;
        }

        var battle = new DungeonBattle(roomId, dungeonId, heroMoveMmPerTick, hero.Value.Hp, attackDamage);

        // 普通怪：写重复就是多只；沿 Z 轴一字排开（确定性），每只在自己的出生点附近 ±500mm 巡逻
        int[] monsters = dungeon.Value.Monsters;
        int radius = dungeon.Value.SpawnRadiusMm;

        for (int i = 0; i < monsters.Length; i++)
        {
            MonsterRow? monster = tables.FindMonster(monsters[i]);

            if (monster == null)
            {
                error = $"副本 {dungeonId} 里的怪 {monsters[i]} 不在 `Monster` 表里";
                return null;
            }

            int z = (i - (monsters.Length - 1) / 2) * 1500;
            int spawnX = radius;

            battle.AttachTables(tables);
        battle.AddEntity(monster.Value.Id, 1, monster.Value.Hp, spawnX, z,
                             patrolSpeedMmPerTick: MonsterMoveMmPerTick,
                             patrolMinMm: spawnX - 500, patrolMaxMm: spawnX + 500,
                             attackDamage: monster.Value.Attack,
                             moveSpeedMmPerTick: MonsterMoveMmPerTick);
        }

        // BOSS：摆在对面（x = -radius），把"血量厚、攻击高"从表里带出来
        MonsterRow? boss = tables.FindMonster(dungeon.Value.BossId);

        if (boss == null)
        {
            error = $"副本 {dungeonId} 的 BOSS {dungeon.Value.BossId} 不在 `Monster` 表里";
            return null;
        }

        BattleEntity bossEntity = battle.AddEntity(boss.Value.Id, 1, boss.Value.Hp, -radius, 0,
                                                  isBoss: true,
                                                  attackDamage: boss.Value.Attack,
                                                  moveSpeedMmPerTick: BossMoveMmPerTick);

        // BOSS 由状态机驱动（见 `BossBrain`：Idle / Chase / Attack）
        battle._brains.Add(new BossBrain(bossEntity));

        return battle;
    }
}
