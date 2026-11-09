// 这个文件里有：
//   BtActionOps（静态类）：动作节点用的世界操作（找目标、移动、回位、普攻）。
//   ChaseAction / BasicAttackAction / CastSkillAction / ReturnHomeAction / PatrolAction / IdleAction / WaitAction（类）：7 个动作节点。
//
// 动作节点是唯一允许修改 ctx.Chasing 与世界的节点类型；条件节点保持只读。

using System;
using System.Collections.Generic;

namespace NBC.Server.Game.Ai.Bt
{
    // 主要功能：动作节点能用的世界操作 —— 找目标、朝目标走、回家、普攻。
    // 主要数据成员：无（全静态）。
    // 主要外部方法：FindNearestHero / MoveTowards / MoveHome / TryAttack。
    // 谁在用它：7 个动作节点、MonsterBrain（每帧刷新目标）。
    // 注意：所有规则（射程、冷却、钳位）仍由 DungeonBattle 判，这里只负责发起。
    public static class BtActionOps
    {
        // 功能：找仇恨范围内最近的活英雄。
        // 入参：ctx 上下文
        // 返回：英雄；范围内没有返回 null
        // 步骤：取最近英雄 → 超过 Profile.AggroRangeMm 就丢弃
        // 上游：MonsterBrain.Think
        // 下游：DungeonBattle.NearestAliveHero / DistanceMm
        public static BattleEntity FindNearestHero(BtContext ctx)
        {
            if (ctx?.World == null || ctx.Self == null)
            {
                return null;
            }

            BattleEntity hero = ctx.World.NearestAliveHero(ctx.Self);

            if (hero == null)
            {
                return null;
            }

            int range = ctx.Profile.AggroRangeMm;

            if (range > 0 && DungeonBattle.DistanceMm(ctx.Self, hero) > range)
            {
                return null;
            }

            return hero;
        }

        // 功能：朝目标移动一帧。
        // 入参：ctx 上下文
        // 返回：无
        // 步骤：目标空就返回 → 调世界移动
        // 上游：ChaseAction
        // 下游：DungeonBattle.MoveTowards
        public static void MoveTowards(BtContext ctx)
        {
            if (ctx?.Self == null || ctx.Target == null)
            {
                return;
            }

            ctx.World.MoveTowards(ctx.Self, ctx.Target);
        }

        // 功能：朝出生点移动一帧。
        // 入参：ctx 上下文
        // 返回：无
        // 步骤：调世界按点移动（世界负责钳位）
        // 上游：ReturnHomeAction
        // 下游：DungeonBattle.MoveTowardsPoint
        public static void MoveHome(BtContext ctx)
        {
            if (ctx?.Self == null)
            {
                return;
            }

            ctx.World.MoveTowardsPoint(ctx.Self, ctx.HomeXmm, ctx.HomeZmm);
        }

        // 功能：对目标发起一次普攻（世界判射程与冷却）。
        // 入参：ctx 上下文
        // 返回：true = 真的打出去了
        // 步骤：目标空就失败 → 调世界普攻
        // 上游：BasicAttackAction
        // 下游：DungeonBattle.TryBasicAttack
        public static bool TryAttack(BtContext ctx)
        {
            if (ctx?.Self == null || ctx.Target == null)
            {
                return false;
            }

            return ctx.World.TryBasicAttack(ctx.Self, ctx.Target.Id, out _);
        }
    }

    // 主要功能：追击 —— 设置 Chasing 并朝目标走一帧，贴到射程内就成功。
    // 主要数据成员：无。
    // 主要外部方法：Tick。
    // 谁在用它：wolf / boss 的「有目标」分支。
    // 注意：**没贴脸时必须返回 Running**；返回 Success 会让 Selector 吃掉后面的攻击分支。
    [BtNode("Chase")]
    public sealed class ChaseAction : BtNode
    {
        // 类型名
        public override string TypeName => "Chase";

        // 功能：追一帧。
        // 入参：ctx 上下文
        // 返回：贴到射程内 → Success（并清 Chasing）；否则 Running
        // 步骤：设 Chasing → 判距离 → 没到就走一帧
        // 上游：父节点
        // 下游：BtActionOps.MoveTowards
        public override BtStatus Tick(BtContext ctx)
        {
            if (ctx?.Self == null || ctx.Target == null)
            {
                return BtStatus.Failure;
            }

            int range = ctx.Profile.AttackRangeMm > 0 ? ctx.Profile.AttackRangeMm : BtOps.DefaultAttackRangeMm;

            // 贴脸缓冲：停在射程的 90% 处，别卡在"2000/2001"的边界上来回抖
            int stopRange = range - range / 10;

            if (stopRange < 1)
            {
                stopRange = range;
            }

            if (BtOps.WithinRange(ctx, stopRange))
            {
                ctx.Chasing = false;
                ctx.LastAction = "追击";
                return BtStatus.Success;
            }

            ctx.Chasing = true;
            ctx.LastAction = "追击";
            BtActionOps.MoveTowards(ctx);
            return BtStatus.Running;
        }
    }

    // 主要功能：普攻 —— 多段时按帧分段，每段仍受世界普攻冷却约束。
    // 主要数据成员：m_hits（总段数）、m_intervalTicks（段最小间隔）、m_done（已打出的段数）、m_waitTicks。
    // 主要外部方法：Configure、Tick。
    // 谁在用它：wolf（1 段）/ boss（3 段）。
    // 注意：世界冷却（15 tick）大于段间隔时，实际节奏由世界冷却决定 —— 不重复实现伤害规则。
    [BtNode("BasicAttack")]
    public sealed class BasicAttackAction : BtNode
    {
        // 总段数（默认 1）
        private int m_hits = 1;

        // 段最小间隔（逻辑帧，默认 5）
        private int m_intervalTicks = 5;

        // 已打出的段数
        private int m_done;

        // 距下一段的等待帧数
        private int m_waitTicks;

        // 类型名
        public override string TypeName => "BasicAttack";

        // 功能：读 hits 与 intervalTicks。
        // 入参：source 参数来源
        // 返回：无
        // 步骤：读两个整数，非法值夹到 >=1
        // 上游：R022-3 建树
        // 下游：Tick
        public override void Configure(IBtParamSource source)
        {
            if (source == null)
            {
                return;
            }

            if (source.TryGetInt("hits", out int hits) && hits >= 1)
            {
                m_hits = hits;
            }

            if (source.TryGetInt("intervalTicks", out int interval) && interval >= 1)
            {
                m_intervalTicks = interval;
            }
        }

        // 功能：打一段或等冷却。
        // 入参：ctx 上下文
        // 返回：全部打完 → Success；还差几段 → Running；没目标/超射程 → Failure
        // 步骤：无目标或超射程就重置并失败 → 等待帧未到返回 Running → 尝试普攻
        // 上游：父节点
        // 下游：BtActionOps.TryAttack
        public override BtStatus Tick(BtContext ctx)
        {
            if (ctx?.Self == null || ctx.Target == null)
            {
                ResetCombo();
                return BtStatus.Failure;
            }

            if (!BtOps.WithinRange(ctx, GetRange(ctx)))
            {
                ResetCombo();
                return BtStatus.Failure;
            }

            if (m_waitTicks > 0)
            {
                m_waitTicks--;
                ctx.LastAction = "普攻间隔";
                return BtStatus.Running;
            }

            if (BtActionOps.TryAttack(ctx))
            {
                m_done++;
                m_waitTicks = m_intervalTicks;
                ctx.LastAction = m_hits > 1 ? "普攻 " + m_done + "/" + m_hits : "普攻";
            }
            else
            {
                // 冷却还没好：本帧没打出去，下一帧再试（不重置段数）
                ctx.LastAction = "等普攻冷却";
                return BtStatus.Running;
            }

            if (m_done >= m_hits)
            {
                ResetCombo();
                return BtStatus.Success;
            }

            return BtStatus.Running;
        }

        // 功能：当前射程（表里的 attackRangeMm）。
        // 入参：ctx 上下文
        // 返回：射程（毫米）
        // 步骤：表值 <=0 时回落默认 2000
        // 上游：Tick
        // 下游：无
        private static int GetRange(BtContext ctx)
        {
            return ctx.Profile.AttackRangeMm > 0 ? ctx.Profile.AttackRangeMm : BtOps.DefaultAttackRangeMm;
        }

        // 功能：清零多段状态。
        // 入参：无
        // 返回：无
        // 步骤：段数与等待都归零
        // 上游：Tick
        // 下游：无
        private void ResetCombo()
        {
            m_done = 0;
            m_waitTicks = 0;
        }
    }

    // 主要功能：放技能 —— 服务端技能系统未实现前的占位，恒失败。
    // 主要数据成员：m_skillId、s_warned（只告警一次）。
    // 主要外部方法：Configure、Tick。
    // 谁在用它：暂时没有树用它（BOSS 树里不放），留给技能系统落地后接。
    // 注意：恒 Failure 是刻意的；一旦技能系统可用，只改这里。
    [BtNode("CastSkill")]
    public sealed class CastSkillAction : BtNode
    {
        // 技能编号
        private int m_skillId;

        // 是否已告警
        private static bool s_warned;

        // 类型名
        public override string TypeName => "CastSkill";

        // 功能：读 skillId。
        // 入参：source 参数来源
        // 返回：无
        // 步骤：读不到就用 0
        // 上游：R022-3 建树
        // 下游：Tick
        public override void Configure(IBtParamSource source)
        {
            if (source != null && source.TryGetInt("skillId", out int skillId))
            {
                m_skillId = skillId;
            }
        }

        // 功能：恒失败（技能系统未做）。
        // 入参：ctx 上下文
        // 返回：Failure
        // 步骤：首次调用打一条告警
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            if (!s_warned)
            {
                s_warned = true;
                Console.WriteLine("[BT] 服务端还没有技能系统：CastSkill(skillId=" + m_skillId + ") 恒为失败。");
            }

            return BtStatus.Failure;
        }
    }

    // 主要功能：回位 —— 朝出生点走，进了容差就成功。
    // 主要数据成员：无（容差用常量 200mm）。
    // 主要外部方法：Tick。
    // 谁在用它：boss 的 HomeFar 分支、普通怪的 leash。
    // 注意：容差 200mm 与 BOSS 的回位半径一致；不要写成 0（会永远差一点点）。
    [BtNode("ReturnHome")]
    public sealed class ReturnHomeAction : BtNode
    {
        // 到家容差（毫米）
        private const int HomeToleranceMm = 200;

        // 类型名
        public override string TypeName => "ReturnHome";

        // 功能：回一帧。
        // 入参：ctx 上下文
        // 返回：到家 → Success；否则 Running
        // 步骤：算离家距离 → 够近就成功并清 Chasing → 否则走一帧
        // 上游：父节点
        // 下游：BtActionOps.MoveHome
        public override BtStatus Tick(BtContext ctx)
        {
            if (ctx?.Self == null)
            {
                return BtStatus.Failure;
            }

            if (BtOps.DistanceToHome(ctx) <= HomeToleranceMm)
            {
                ctx.Chasing = false;
                ctx.LastAction = "回位到位";
                return BtStatus.Success;
            }

            ctx.Chasing = true;
            ctx.LastAction = "回位";
            BtActionOps.MoveHome(ctx);
            return BtStatus.Running;
        }
    }

    // 主要功能：巡逻 —— 清掉 Chasing，交给实体自身的巡逻位移。
    // 主要数据成员：无。
    // 主要外部方法：Tick。
    // 谁在用它：wolf 的默认分支。
    // 注意：不清 Chasing 的话，实体的 Step 会跳过巡逻，表现为"站着不动"。
    [BtNode("Patrol")]
    public sealed class PatrolAction : BtNode
    {
        // 类型名
        public override string TypeName => "Patrol";

        // 功能：允许巡逻。
        // 入参：ctx 上下文
        // 返回：恒 Success
        // 步骤：清 Chasing + 记动作名
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            if (ctx != null)
            {
                ctx.Chasing = false;
                ctx.LastAction = "巡逻";
            }

            return BtStatus.Success;
        }
    }

    // 主要功能：站桩 —— 清掉 Chasing，且不允许巡逻。
    // 主要数据成员：无。
    // 主要外部方法：Tick。
    // 谁在用它：boss 的默认分支、PatrolWhenIdle=false 的怪。
    // 注意：与 Patrol 的区别只有"实体是否自己巡逻"，这里靠 Profile.PatrolWhenIdle 决定是否额外拦住。
    [BtNode("Idle")]
    public sealed class IdleAction : BtNode
    {
        // 类型名
        public override string TypeName => "Idle";

        // 功能：站住。
        // 入参：ctx 上下文
        // 返回：恒 Success
        // 步骤：清 Chasing + 记动作名
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            if (ctx != null)
            {
                ctx.Chasing = false;
                ctx.LastAction = "站桩";
            }

            return BtStatus.Success;
        }
    }

    // 主要功能：等待 —— 按逻辑帧计时。
    // 主要数据成员：m_seconds（秒）、m_framesLeft（剩余帧）。
    // 主要外部方法：Configure、Tick。
    // 谁在用它：技能后摇、段间隔等（当前默认树未用）。
    // 注意：30Hz 换算，向上取整；每帧只减一，不用真实时间。
    [BtNode("Wait")]
    public sealed class WaitAction : BtNode
    {
        // 每逻辑帧的帧率（与服务端一致）
        private const int TickRate = 30;

        // 总帧数（由秒数换算）
        private int m_totalFrames = TickRate;

        // 剩余帧数
        private int m_framesLeft;

        // 类型名
        public override string TypeName => "Wait";

        // 功能：读秒数并换算帧数。
        // 入参：source 参数来源
        // 返回：无
        // 步骤：读 seconds → 乘 30 向上取整 → 夹到至少 1 帧
        // 上游：R022-3 建树
        // 下游：Tick
        public override void Configure(IBtParamSource source)
        {
            if (source == null || !source.TryGetFloat("seconds", out float seconds))
            {
                return;
            }

            int frames = (int)Math.Ceiling(seconds * TickRate);
            m_totalFrames = frames < 1 ? 1 : frames;
        }

        // 功能：计时。
        // 入参：ctx 上下文
        // 返回：到点 → Success；否则 Running
        // 步骤：本段开始时装载剩余帧 → 每帧减一
        // 上游：父节点
        // 下游：无
        public override BtStatus Tick(BtContext ctx)
        {
            if (m_framesLeft <= 0)
            {
                m_framesLeft = m_totalFrames;
            }

            m_framesLeft--;

            if (ctx != null)
            {
                ctx.LastAction = "等待";
            }

            return m_framesLeft > 0 ? BtStatus.Running : BtStatus.Success;
        }
    }
}
