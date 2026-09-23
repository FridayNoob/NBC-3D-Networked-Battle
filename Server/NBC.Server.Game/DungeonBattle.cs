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
    public BattleEntity(int id, int configId, int kind, int maxHp, int posXmm, int posZmm,
                        int patrolSpeedMmPerTick = 0, int patrolMinMm = 0, int patrolMaxMm = 0)
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
    }

    /// <summary>推进一帧（巡逻移动 + 朝向）。</summary>
    public void Step()
    {
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
    /// <returns>新单位。</returns>
    public BattleEntity AddEntity(int configId, int kind, int maxHp, int posXmm, int posZmm,
                                  int patrolSpeedMmPerTick = 0, int patrolMinMm = 0, int patrolMaxMm = 0)
    {
        var entity = new BattleEntity(_nextEntityId, configId, kind, maxHp, posXmm, posZmm,
                                      patrolSpeedMmPerTick, patrolMinMm, patrolMaxMm);
        _nextEntityId++;
        _entities.Add(entity);
        return entity;
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
    /// M3 的临时关卡：1 个英雄 + 2 个巡逻的狼。
    /// <para>⚠️ TODO(S6)：改从 `Dungeon` / `Monster` 配置表生成（S6 加表**不改工具代码**）。</para>
    /// </summary>
    /// <param name="roomId">房号。</param>
    /// <param name="dungeonId">副本编号。</param>
    /// <returns>世界。</returns>
    public static DungeonBattle CreateStarterDungeon(string roomId, int dungeonId)
    {
        var battle = new DungeonBattle(roomId, dungeonId);

        // 英雄：站在原点，不动（M3 还没有输入上行驱动它 —— 那是 S5 的后半段）
        battle.AddEntity(configId: 1001, kind: 0, maxHp: 300, posXmm: 0, posZmm: 0);

        // 两只狼：在 2 米区间里来回巡逻（60 毫米/帧 ≈ 1.8 米/秒）
        battle.AddEntity(configId: 6001, kind: 1, maxHp: 120, posXmm: 2000, posZmm: 1000,
                         patrolSpeedMmPerTick: 60, patrolMinMm: 1500, patrolMaxMm: 2500);
        battle.AddEntity(configId: 6001, kind: 1, maxHp: 120, posXmm: 2000, posZmm: -1000,
                         patrolSpeedMmPerTick: 40, patrolMinMm: 1000, patrolMaxMm: 2500);

        return battle;
    }
}
