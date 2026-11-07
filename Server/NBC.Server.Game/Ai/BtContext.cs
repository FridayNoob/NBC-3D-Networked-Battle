// 这个文件里有：
//   BtContext（类）：一次执行的上下文（世界、自己、出生点、目标、帧号、AI 参数、是否在追击）。
//   BtOps（静态类）：叶子节点用到的「世界操作」薄封装（查最近敌人、移动、普攻、巡逻开关）。

using System.Collections.Generic;

namespace NBC.Server.Game.Ai
{
    // AI 参数档位：同一种树，靠参数区分「普通怪 / BOSS」
    public readonly struct MonsterAiProfile
    {
        // 仇恨半径，毫米（进入这个范围就锁定玩家）
        public readonly int AggroRangeMm;

        // 移动速度，毫米/逻辑帧
        public readonly int MoveSpeedMmPerTick;

        // 离家超过这个距离就回位，毫米（BOSS 用；普通怪可设很大表示不回家）
        public readonly int LeashRadiusMm;

        // 没有目标时是否巡逻（普通怪 true，BOSS false = 站桩）
        public readonly bool PatrolWhenIdle;

        // 功能：构造参数档位。
        // 入参：aggroRangeMm 仇恨半径；moveSpeedMmPerTick 移速；leashRadiusMm 回位半径；patrolWhenIdle 无目标是否巡逻
        // 返回：无
        // 步骤：逐字段赋值
        // 上游：MonsterAiProfile.普通怪 / BOSS
        // 下游：BtContext
        public MonsterAiProfile(int aggroRangeMm, int moveSpeedMmPerTick, int leashRadiusMm, bool patrolWhenIdle)
        {
            AggroRangeMm = aggroRangeMm;
            MoveSpeedMmPerTick = moveSpeedMmPerTick;
            LeashRadiusMm = leashRadiusMm;
            PatrolWhenIdle = patrolWhenIdle;
        }

        // 普通近战怪：4 米仇恨、按表移速、不主动回家（家就是巡逻区）
        public static MonsterAiProfile Melee => new MonsterAiProfile(4000, 40, 100000, true);

        // BOSS：4 米仇恨、按表移速、**离家超过松弛量（200mm）就回位**、不巡逻（守住出生点）
        // 注：这里直接对齐 DungeonBattle.BossLeashSlackMm，否则会被判「离家 1 米仍站着不动」
        public static MonsterAiProfile Boss => new MonsterAiProfile(4000, 40, DungeonBattle.BossLeashSlackMm, false);
    }

    // 主要功能：装一次 Tick 需要的所有东西，节点只读它、不自己去找世界。
    // 主要数据成员：World / Self / HomeXmm / HomeZmm / Target / Tick / Profile。
    // 主要外部方法：无（公开字段，节点直接读）。
    // 谁在用它：BtNode 的所有子类。
    // 注意：`Target` 由 DetectTarget 节点每帧刷新；其它节点只读它，保证同一帧内决策一致。
    public sealed class BtContext
    {
        // 世界（服务端权威战斗）
        public DungeonBattle World;

        // 这个大脑所控制的单位
        public BattleEntity Self;

        // 出生点 X（毫米）
        public int HomeXmm;

        // 出生点 Z（毫米）
        public int HomeZmm;

        // 当前目标（可为 null）
        public BattleEntity Target;

        // 当前逻辑帧号
        public int Tick;

        // AI 参数档位
        public MonsterAiProfile Profile;

        // 这一帧是否处在「追击」状态（为 true 时 BattleEntity.Step 跳过自己的巡逻位移）
        public bool Chasing;

        // 最近一个执行到的动作名（由动作节点写入；大脑据此维护 StateName）
        public string LastAction;
    }

    // 主要功能：把「行为树要做的世界操作」集中成几个小函数，节点里只调这些，不直接碰 DungeonBattle 细节。
    // 主要数据成员：无（纯静态）。
    // 主要外部方法：FindNearestHero / DistanceTo / MoveTowards / TryAttack / DistanceToHome。
    // 谁在用它：BtLeaves 里的各叶子节点。
    // 注意：全部复用 DungeonBattle 已有能力（NearestAliveHero / MoveTowardsPoint / TryBasicAttack），保证规则只有一份。
    internal static class BtOps
    {
        // 功能：找仇恨范围内最近的活英雄。
        // 入参：ctx 上下文
        // 返回：目标；仇恨范围内没有就 null
        // 步骤：用世界的 NearestAliveHero + 距离比较（超出仇恨半径视为没看见）
        // 上游：DetectTarget 节点
        // 下游：DungeonBattle.NearestAliveHero / DistanceMm
        public static BattleEntity FindNearestHero(BtContext ctx)
        {
            BattleEntity nearest = ctx.World.NearestAliveHero(ctx.Self);

            if (nearest == null)
            {
                return null;
            }

            int distance = DungeonBattle.DistanceMm(ctx.Self, nearest);
            return distance <= ctx.Profile.AggroRangeMm ? nearest : null;
        }

        // 功能：算到目标的水平距离（毫米）。
        // 入参：ctx 上下文；target 目标
        // 返回：距离
        // 步骤：直接调世界的距离函数
        // 上游：各条件节点
        // 下游：DungeonBattle.DistanceMm
        public static int DistanceTo(BtContext ctx, BattleEntity target)
        {
            return target == null ? int.MaxValue : DungeonBattle.DistanceMm(ctx.Self, target);
        }

        // 功能：朝目标走一帧。
        // 入参：ctx 上下文；target 目标
        // 返回：true = 真的移动了
        // 步骤：调世界的 MoveTowardsPoint（内部按移速走一格，并维护朝向）
        // 上游：Chase 节点
        // 下游：DungeonBattle.MoveTowardsPoint
        public static bool MoveTowards(BtContext ctx, BattleEntity target)
        {
            return target != null && ctx.World.MoveTowardsPoint(ctx.Self, target.PosXmm, target.PosZmm);
        }

        // 功能：朝出生点走一帧。
        // 入参：ctx 上下文
        // 返回：true = 真的移动了
        // 步骤：调 MoveTowardsPoint(ctx.HomeXmm, ctx.HomeZmm)
        // 上游：ReturnHome 节点
        // 下游：DungeonBattle.MoveTowardsPoint
        public static bool MoveHome(BtContext ctx)
        {
            return ctx.World.MoveTowardsPoint(ctx.Self, ctx.HomeXmm, ctx.HomeZmm);
        }

        // 功能：对目标发起一次普攻（服务端规则说了算：冷却/射程/存活）。
        // 入参：ctx 上下文；target 目标
        // 返回：true = 打出去了
        // 步骤：调世界 TryBasicAttack，理由串忽略（内部已记日志）
        // 上游：BasicAttack 节点
        // 下游：DungeonBattle.TryBasicAttack
        public static bool TryAttack(BtContext ctx, BattleEntity target)
        {
            if (target == null)
            {
                return false;
            }

            return ctx.World.TryBasicAttack(ctx.Self, target.Id, out _);
        }

        // 功能：算离家距离（毫米）。
        // 入参：ctx 上下文
        // 返回：距离
        // 步骤：调世界的点到点距离
        // 上游：ReturnHome 条件
        // 下游：DungeonBattle.DistanceMmToPoint
        public static int DistanceToHome(BtContext ctx)
        {
            return DungeonBattle.DistanceMmToPoint(ctx.Self, ctx.HomeXmm, ctx.HomeZmm);
        }
    }
}
