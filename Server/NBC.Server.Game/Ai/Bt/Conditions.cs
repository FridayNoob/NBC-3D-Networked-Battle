// 这个文件里有：
//   六个条件节点：IsDead / HasTarget / InAttackRange / AttackReady / HPBelow / HomeFar。
//
// 条件节点只回答"是/否"：成功表示成立、失败表示不成立，绝不返回 Running，也不修改 ctx 或世界。
// 参数在 Configure 里读进字段，Tick 内不再分配、不再读参数。

using System;

namespace NBC.Server.Game.Ai.Bt
{
    // 主要功能：自己是否已经死了（HP <= 0）。
    // 主要数据成员：无。
    // 主要外部方法：Tick。
    // 谁在用它：怪物树的第一个分支（死了就什么都不做）。
    // 注意：用 Alive 而不是 Hp，语义与实体一致。
    [BtNode("IsDead")]
    public sealed class IsDeadCondition : BtNode
    {
        // 类型名
        public override string TypeName => "IsDead";

        // 功能：判断自己是否死亡。
        // 入参：ctx 上下文
        // 返回：死亡 → Success；否则 Failure
        // 步骤：读 Self.Alive 取反
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            if (ctx?.Self == null)
            {
                return BtStatus.Failure;
            }

            return ctx.Self.Alive ? BtStatus.Failure : BtStatus.Success;
        }
    }

    // 主要功能：当前是否有活着的目标。
    // 主要数据成员：无。
    // 主要外部方法：Tick。
    // 谁在用它：追击与攻击分支的前置判断。
    // 注意：目标可能刚被打死但引用还在，所以必须同时判断 Alive。
    [BtNode("HasTarget")]
    public sealed class HasTargetCondition : BtNode
    {
        // 类型名
        public override string TypeName => "HasTarget";

        // 功能：判断有没有活着的目标。
        // 入参：ctx 上下文
        // 返回：有 → Success；没有 → Failure
        // 步骤：判空 + 判存活
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            return ctx?.Target != null && ctx.Target.Alive ? BtStatus.Success : BtStatus.Failure;
        }
    }

    // 主要功能：目标是否在射程内（圆形射程，整数毫米）。
    // 主要数据成员：m_rangeMm（射程，0 表示用默认 2000）。
    // 主要外部方法：Configure、Tick。
    // 谁在用它：攻击分支的前置判断。
    // 注意：射程边界包含等于（2000 算"在"），与客户端 InAttackRange 的预期一致。
    [BtNode("InAttackRange")]
    public sealed class InAttackRangeCondition : BtNode
    {
        // 射程（毫米）；0 表示用默认值
        private int m_rangeMm;

        // 类型名
        public override string TypeName => "InAttackRange";

        // 功能：读 rangeMm 参数。
        // 入参：source 参数来源
        // 返回：无
        // 步骤：读不到就保持 0（默认射程）
        // 上游：R022-3 建树
        // 下游：Tick
        public override void Configure(IBtParamSource source)
        {
            if (source != null && source.TryGetInt("rangeMm", out int rangeMm))
            {
                m_rangeMm = rangeMm;
            }
        }

        // 功能：判断目标是否在射程内。
        // 入参：ctx 上下文
        // 返回：在 → Success；不在或没目标 → Failure
        // 步骤：定射程 → 算距离 → 比较（含等于）
        // 上游：父节点
        // 下游：BtOps.DistanceTo
        public override BtStatus Tick(BtContext ctx)
        {
            if (ctx?.Target == null || !ctx.Target.Alive)
            {
                return BtStatus.Failure;
            }

            int range = m_rangeMm > 0 ? m_rangeMm : BtOps.DefaultAttackRangeMm;
            return BtOps.DistanceTo(ctx) <= range ? BtStatus.Success : BtStatus.Failure;
        }
    }

    // 主要功能：指定技能的冷却是否已经好了（普攻 skillId = 0）。
    // 主要数据成员：m_skillId（技能编号）、s_warned（是否已提示过技能未实现）。
    // 主要外部方法：Configure、Tick。
    // 谁在用它：攻击/技能分支的前置判断。
    // 注意：技能系统还没做，skillId 大于 0 时一律返回 Failure 并只提示一次，避免刷屏。
    [BtNode("AttackReady")]
    public sealed class AttackReadyCondition : BtNode
    {
        // 技能编号（0 = 普攻）
        private int m_skillId;

        // 是否已经提示过"技能冷却未实现"
        private static bool s_warned;

        // 类型名
        public override string TypeName => "AttackReady";

        // 功能：读 skillId 参数。
        // 入参：source 参数来源
        // 返回：无
        // 步骤：读不到就用 0（普攻）
        // 上游：R022-3 建树
        // 下游：Tick
        public override void Configure(IBtParamSource source)
        {
            if (source != null && source.TryGetInt("skillId", out int skillId))
            {
                m_skillId = skillId;
            }
        }

        // 功能：判断冷却是否已好。
        // 入参：ctx 上下文
        // 返回：好了 → Success；没好或技能未实现 → Failure
        // 步骤：技能编号大于 0 时提示并失败 → 普攻看 AttackCooldownTicksLeft
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            if (ctx?.Self == null)
            {
                return BtStatus.Failure;
            }

            if (m_skillId > 0)
            {
                if (!s_warned)
                {
                    s_warned = true;
                    Console.WriteLine("[BT] 服务端还没有技能系统：AttackReady(skillId=" + m_skillId + ") 暂时恒为失败。");
                }

                return BtStatus.Failure;
            }

            return ctx.Self.AttackCooldownTicksLeft <= 0 ? BtStatus.Success : BtStatus.Failure;
        }
    }

    // 主要功能：血量是否低于阈值（可选按百分比）。
    // 主要数据成员：m_hpThreshold（阈值）、m_percentOfMax（是否按百分比）。
    // 主要外部方法：Configure、Tick。
    // 谁在用它：BOSS 分阶段、残血逃跑等分支。
    // 注意：比较是严格小于（HP 等于阈值不算"低于"），与客户端 HPBelow 一致。
    [BtNode("HPBelow")]
    public sealed class HpBelowCondition : BtNode
    {
        // 阈值：绝对值或百分比（1~100）
        private int m_hpThreshold;

        // 是否按最大血量的百分比理解
        private bool m_percentOfMax;

        // 类型名
        public override string TypeName => "HPBelow";

        // 功能：读 hpThreshold 与 percentOfMax。
        // 入参：source 参数来源
        // 返回：无
        // 步骤：先读阈值再读开关，读不到用默认（0 / false）
        // 上游：R022-3 建树
        // 下游：Tick
        public override void Configure(IBtParamSource source)
        {
            if (source == null)
            {
                return;
            }

            if (source.TryGetInt("hpThreshold", out int threshold))
            {
                m_hpThreshold = threshold;
            }

            if (source.TryGetBool("percentOfMax", out bool percent))
            {
                m_percentOfMax = percent;
            }
        }

        // 功能：判断血量是否低于阈值。
        // 入参：ctx 上下文
        // 返回：低于 → Success；否则 Failure
        // 步骤：百分比时用 hp*100 与 threshold*maxHp 比较（避免浮点）→ 否则直接比 hp
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            if (ctx?.Self == null)
            {
                return BtStatus.Failure;
            }

            if (m_percentOfMax)
            {
                long left = (long)ctx.Self.Hp * 100L;
                long right = (long)m_hpThreshold * ctx.Self.MaxHp;
                return left < right ? BtStatus.Success : BtStatus.Failure;
            }

            return ctx.Self.Hp < m_hpThreshold ? BtStatus.Success : BtStatus.Failure;
        }
    }

    // 主要功能：是否离家（出生点）太远。
    // 主要数据成员：无（半径来自 ctx.Profile）。
    // 主要外部方法：Tick。
    // 谁在用它：BOSS 回位分支、巡逻怪的 leash。
    // 注意：等于半径不算"太远"，必须严格大于。
    [BtNode("HomeFar")]
    public sealed class HomeFarCondition : BtNode
    {
        // 类型名
        public override string TypeName => "HomeFar";

        // 功能：判断离家距离是否超过回位半径。
        // 入参：ctx 上下文
        // 返回：超过 → Success；否则 Failure
        // 步骤：算离家距离 → 与 Profile.LeashRadiusMm 比较
        // 上游：父节点
        // 下游：BtOps.DistanceToHome
        public override BtStatus Tick(BtContext ctx)
        {
            if (ctx?.Self == null)
            {
                return BtStatus.Failure;
            }

            return BtOps.DistanceToHome(ctx) > ctx.Profile.LeashRadiusMm ? BtStatus.Success : BtStatus.Failure;
        }
    }
}
