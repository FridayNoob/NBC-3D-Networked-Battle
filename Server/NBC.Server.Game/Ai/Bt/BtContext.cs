// 这个文件里有：
//   MonsterAiProfile（结构体）：每种怪的 AI 参数（仇恨范围、移速、回位半径、是否巡逻）。
//   BtContext（类）：一次 Tick 需要的全部上下文（世界、自己、出生点、目标、帧号、参数）。
//   BtOps（静态类）：节点能用的世界操作薄封装（找最近英雄、算距离、移动、普攻、回位）。
//
// 本文件只做"数据 + 读世界/操作世界"的薄封装，不含任何具体节点行为。

namespace NBC.Server.Game.Ai.Bt
{
    // 一种怪的 AI 参数（数值都带单位：毫米 / 毫米每逻辑帧）
    public readonly struct MonsterAiProfile
    {
        // 仇恨范围（毫米）：英雄进入这个范围就被盯上
        public readonly int AggroRangeMm;

        // 移动速度（毫米每逻辑帧，30Hz 下 40 约等于 1.2 米每秒）
        public readonly int MoveSpeedMmPerTick;

        // 回位半径（毫米）：离家超过它就回位
        public readonly int LeashRadiusMm;

        // 普攻射程（毫米）：来自 Monster 表的 attackRangeMm；树里显式写了 rangeMm 时以树为准
        public readonly int AttackRangeMm;

        // 空闲时是否允许巡逻（BOSS 为 false，守着出生点）
        public readonly bool PatrolWhenIdle;

        // 近战普通怪兜底参数：4 米仇恨、2 米射程、40mm/帧、基本不回位、允许巡逻（真实值来自 Monster 表）
        public static MonsterAiProfile Melee => new MonsterAiProfile(4000, 40, 100000, 2000, true);

        // BOSS 兜底参数：4 米仇恨、2 米射程、40mm/帧、离家 200mm 回位、不巡逻（真实值来自 Monster 表）
        public static MonsterAiProfile Boss => new MonsterAiProfile(4000, 40, 200, 2000, false);

        // 功能：构造参数。
        // 入参：aggroRangeMm 仇恨范围；moveSpeedMmPerTick 移速；leashRadiusMm 回位半径；attackRangeMm 普攻射程；patrolWhenIdle 是否巡逻
        // 返回：无
        // 步骤：逐个赋值
        // 上游：DungeonBattle（从 Monster 表读）、Melee / Boss 兜底
        // 下游：BtContext.Profile
        public MonsterAiProfile(int aggroRangeMm, int moveSpeedMmPerTick, int leashRadiusMm,
            int attackRangeMm, bool patrolWhenIdle)
        {
            AggroRangeMm = aggroRangeMm;
            MoveSpeedMmPerTick = moveSpeedMmPerTick;
            LeashRadiusMm = leashRadiusMm;
            AttackRangeMm = attackRangeMm;
            PatrolWhenIdle = patrolWhenIdle;
        }
    }

    // 主要功能：一次 Tick 的上下文 —— 世界、自己、出生点、目标、帧号、AI 参数。
    // 主要数据成员：World / Self / HomeXmm / HomeZmm / Target / Tick / Profile / Chasing / LastAction。
    // 主要外部方法：无（纯数据，节点只读）。
    // 谁在用它：所有节点的 Tick。
    // 注意：每只怪一个实例、跨帧复用；条件节点不得修改这里任何字段（Chasing/LastAction 只由动作节点写）。
    public sealed class BtContext
    {
        // 世界（DungeonBattle）
        public DungeonBattle World;

        // 自己
        public BattleEntity Self;

        // 出生点 X（毫米）
        public int HomeXmm;

        // 出生点 Z（毫米）
        public int HomeZmm;

        // 当前目标（由大脑每帧刷新；null 表示没有）
        public BattleEntity Target;

        // 当前逻辑帧号
        public int Tick;

        // AI 参数
        public MonsterAiProfile Profile;

        // 是否正在追击（动作节点写，实体 Step 据此让位）
        public bool Chasing;

        // 上一次动作名（日志用）
        public string LastAction;
    }

    // 主要功能：节点能用的世界操作 —— 找目标、算距离、移动、攻击、回位。
    // 主要数据成员：无（全静态）。
    // 主要外部方法：DistanceTo / DistanceToHome（动作类操作在 R022-2 再加）。
    // 谁在用它：条件节点（本需求）；R022-2 的动作节点会在此基础上补移动/攻击操作。
    // 注意：所有距离一律整数毫米；不在这里做任何规则判断（射程/冷却都在 DungeonBattle）。
    public static class BtOps
    {
        // 默认普攻射程（毫米）：节点参数 rangeMm 为 0 时用它
        public const int DefaultAttackRangeMm = 2000;


        // 功能：自己到目标的距离。
        // 入参：ctx 上下文
        // 返回：距离（毫米）；没有目标返回 int.MaxValue
        // 步骤：目标为空就返回极大值 → 否则算距离
        // 上游：条件节点 InAttackRange
        // 下游：DungeonBattle.DistanceMm
        public static int DistanceTo(BtContext ctx)
        {
            if (ctx?.Self == null || ctx.Target == null)
            {
                return int.MaxValue;
            }

            return DungeonBattle.DistanceMm(ctx.Self, ctx.Target);
        }

        // 功能：自己到目标的**距离平方**（圆形判定用；整数运算，不开方）。
        // 入参：ctx 上下文
        // 返回：距离平方（无目标返回 long.MaxValue）
        // 步骤：算 dx/dz → 平方和（用 long 防溢出）
        // 上游：WithinRange
        // 下游：无
        // 注意：不要用 DungeonBattle.DistanceMm —— 那是**切比雪夫**距离，是 AI 度量，不是射程判据。
        public static long SquaredDistanceTo(BtContext ctx)
        {
            if (ctx?.Self == null || ctx.Target == null)
            {
                return long.MaxValue;
            }

            long dx = ctx.Self.PosXmm - ctx.Target.PosXmm;
            long dz = ctx.Self.PosZmm - ctx.Target.PosZmm;
            return dx * dx + dz * dz;
        }

        // 功能：目标是否在指定圆形射程内（含边界）。
        // 入参：ctx 上下文；rangeMm 射程（毫米，<=0 用默认 2000）
        // 返回：true = 在射程内
        // 步骤：定射程 → 距离平方 <= 射程平方
        // 上游：InAttackRange 条件、Chase/BasicAttack 动作
        // 下游：SquaredDistanceTo
        public static bool WithinRange(BtContext ctx, int rangeMm)
        {
            int range = rangeMm > 0 ? rangeMm : DefaultAttackRangeMm;
            long square = (long)range * range;
            return SquaredDistanceTo(ctx) <= square;
        }
        // 功能：自己到出生点的距离。
        // 入参：ctx 上下文
        // 返回：距离（毫米）
        // 步骤：直接算点到点距离
        // 上游：条件节点 HomeFar
        // 下游：DungeonBattle.DistanceMmToPoint
        public static int DistanceToHome(BtContext ctx)
        {
            if (ctx?.Self == null)
            {
                return int.MaxValue;
            }

            return DungeonBattle.DistanceMmToPoint(ctx.Self, ctx.HomeXmm, ctx.HomeZmm);
        }




    }
}
