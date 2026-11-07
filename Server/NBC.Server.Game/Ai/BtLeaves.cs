// 这个文件里有：通用叶子节点（条件 / 动作）—— 每种怪的行为树都由这些节点拼出来。
//   条件：BtIsDead、BtHasTarget、BtTargetInAttackRange、BtAttackReady、BtHpBelow、BtHomeFar
//   动作：BtDetectTarget、BtChase、BtBasicAttack、BtReturnHome、BtPatrol、BtIdle

namespace NBC.Server.Game.Ai
{
    // 条件：自己死了就 Success（放在 Selector 最前面 ⇒ 优先于一切行为）
    public sealed class BtIsDead : BtNode
    {
        // 功能：构造（设名字）。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：Tick
        public BtIsDead()
        {
            Name = "IsDead";
        }

        // 功能：判断自己是否已死亡。
        // 入参：ctx 上下文
        // 返回：死了 Success，活着 Failure
        // 步骤：读 Self.Alive
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            return ctx.Self.Alive ? BtStatus.Failure : BtStatus.Success;
        }
    }

    // 动作：每帧刷新目标（仇恨范围内最近的活英雄）
    public sealed class BtDetectTarget : BtNode
    {
        // 功能：构造（设名字）。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：Tick
        public BtDetectTarget()
        {
            Name = "DetectTarget";
        }

        // 功能：兼容编辑器节点用的空操作（**目标检测已由 MonsterBrain 每帧自动完成**）。
        // 入参：ctx 上下文
        // 返回：恒 Failure
        // 步骤：不改 ctx.Target，直接返回 Failure
        // 上游：父节点（只在 JSON 树里出现时）
        // 下游：无
        // 注意：这里**故意返回 Failure**：Selector 里 Failure 会被跳过，不会抢占后面的分支；
        //       早期把它做成返回 Success 的动作节点，导致 BOSS 只会「检测」而永远不追不攻击（踩过）。
        public override BtStatus Tick(BtContext ctx)
        {
            return BtStatus.Failure;
        }
    }

    // 条件：有目标
    public sealed class BtHasTarget : BtNode
    {
        // 功能：构造（设名字）。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：Tick
        public BtHasTarget()
        {
            Name = "HasTarget";
        }

        // 功能：判断上下文里有没有目标。
        // 入参：ctx 上下文
        // 返回：有 Success，没有 Failure
        // 步骤：读 ctx.Target 与存活
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            return ctx.Target != null && ctx.Target.Alive ? BtStatus.Success : BtStatus.Failure;
        }
    }

    // 条件：目标在普攻射程内
    public sealed class BtTargetInAttackRange : BtNode
    {
        // 功能：构造（设名字）。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：Tick
        public BtTargetInAttackRange()
        {
            Name = "InAttackRange";
        }

        // 功能：判断距离是否在射程内。
        // 入参：ctx 上下文
        // 返回：在射程内 Success，否则 Failure
        // 步骤：BtOps.DistanceTo 与 BasicAttackRangeMm 比较
        // 上游：父节点
        // 下游：BtOps.DistanceTo
        public override BtStatus Tick(BtContext ctx)
        {
            return BtOps.DistanceTo(ctx, ctx.Target) <= DungeonBattle.BasicAttackRangeMm
                ? BtStatus.Success
                : BtStatus.Failure;
        }
    }

    // 条件：普攻冷却好了
    public sealed class BtAttackReady : BtNode
    {
        // 功能：构造（设名字）。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：Tick
        public BtAttackReady()
        {
            Name = "AttackReady";
        }

        // 功能：判断冷却。
        // 入参：ctx 上下文
        // 返回：冷却好了 Success，否则 Failure
        // 步骤：读 Self.AttackCooldownTicksLeft
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            return ctx.Self.AttackCooldownTicksLeft <= 0 ? BtStatus.Success : BtStatus.Failure;
        }
    }

    // 条件：自己血量低于阈值（严格小于；阈值来自节点参数 hpThreshold）
    public sealed class BtHpBelow : BtNode
    {
        // 血量阈值；节点的 hpThreshold 参数
        private readonly int m_threshold;

        // 功能：构造。
        // 入参：threshold 血量阈值
        // 返回：无
        // 步骤：存阈值、设名字
        // 上游：树构建 / JSON 加载
        // 下游：Tick
        public BtHpBelow(int threshold)
        {
            m_threshold = threshold;
            Name = "HPBelow";
        }

        // 功能：判断血量是否严格小于阈值。
        // 入参：ctx 上下文
        // 返回：满足 Success，否则 Failure
        // 步骤：Self.Hp < threshold
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            return ctx.Self.Hp < m_threshold ? BtStatus.Success : BtStatus.Failure;
        }
    }

    // 条件：离家太远（超过 profile 的回位半径）
    public sealed class BtHomeFar : BtNode
    {
        // 功能：构造（设名字）。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：Tick
        public BtHomeFar()
        {
            Name = "HomeFar";
        }

        // 功能：判断离家距离是否超过回位半径。
        // 入参：ctx 上下文
        // 返回：超过 Success，否则 Failure
        // 步骤：BtOps.DistanceToHome > Profile.LeashRadiusMm
        // 上游：父节点
        // 下游：BtOps.DistanceToHome
        public override BtStatus Tick(BtContext ctx)
        {
            return BtOps.DistanceToHome(ctx) > ctx.Profile.LeashRadiusMm ? BtStatus.Success : BtStatus.Failure;
        }
    }

    // 动作：朝目标移动一帧
    public sealed class BtChase : BtNode
    {
        // 功能：构造（设名字）。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：Tick
        public BtChase()
        {
            Name = "Chase";
        }

        // 功能：追一帧。
        // 入参：ctx 上下文
        // 返回：移动了 Success，否则 Failure
        // 步骤：置 Chasing=true（让实体自己的巡逻让位）→ BtOps.MoveTowards
        // 上游：父节点
        // 下游：BtOps.MoveTowards
        public override BtStatus Tick(BtContext ctx)
        {
            ctx.Chasing = true;
            ctx.LastAction = "追击";
            return BtOps.MoveTowards(ctx, ctx.Target) ? BtStatus.Success : BtStatus.Failure;
        }
    }

    // 动作：普攻一次
    public sealed class BtBasicAttack : BtNode
    {
        // 功能：构造（设名字）。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：Tick
        public BtBasicAttack()
        {
            Name = "Attack";
        }

        // 功能：攻击一次。
        // 入参：ctx 上下文
        // 返回：打出去 Success，被拒 Failure
        // 步骤：Chasing=true（贴身也要停住，避免边打边蹭）→ BtOps.TryAttack
        // 上游：父节点
        // 下游：BtOps.TryAttack
        public override BtStatus Tick(BtContext ctx)
        {
            ctx.Chasing = true;
            ctx.LastAction = "攻击";
            return BtOps.TryAttack(ctx, ctx.Target) ? BtStatus.Success : BtStatus.Failure;
        }
    }

    // 动作：回出生点
    public sealed class BtReturnHome : BtNode
    {
        // 功能：构造（设名字）。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：Tick
        public BtReturnHome()
        {
            Name = "ReturnHome";
        }

        // 功能：朝出生点走一帧。
        // 入参：ctx 上下文
        // 返回：到家 Success，否则 Running
        // 步骤：BtOps.MoveHome → 判断是否已在家（LeashSlack 内）
        // 上游：父节点
        // 下游：BtOps.MoveHome
        public override BtStatus Tick(BtContext ctx)
        {
            ctx.Chasing = true;
            ctx.LastAction = "回位";

            if (BtOps.DistanceToHome(ctx) <= DungeonBattle.BossLeashSlackMm)
            {
                ctx.Chasing = false;
                return BtStatus.Success;
            }

            BtOps.MoveHome(ctx);
            return BtStatus.Running;
        }
    }

    // 动作：巡逻（交给实体自己的 PatrolDir 巡逻，行为树只负责"允许巡逻"）
    public sealed class BtPatrol : BtNode
    {
        // 功能：构造（设名字）。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：Tick
        public BtPatrol()
        {
            Name = "Patrol";
        }

        // 功能：允许巡逻。
        // 入参：ctx 上下文
        // 返回：恒 Success
        // 步骤：Chasing=false（让 BattleEntity.Step 走自己的巡逻逻辑）
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            ctx.Chasing = false;
            ctx.LastAction = "巡逻";
            return BtStatus.Success;
        }
    }

    // 动作：站桩（BOSS 没目标时用）
    public sealed class BtIdle : BtNode
    {
        // 功能：构造（设名字）。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：Tick
        public BtIdle()
        {
            Name = "Idle";
        }

        // 功能：站着不动。
        // 入参：ctx 上下文
        // 返回：恒 Success
        // 步骤：Chasing=true（禁止巡逻位移）
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            ctx.Chasing = true;
            ctx.LastAction = "站桩";
            return BtStatus.Success;
        }
    }
}
