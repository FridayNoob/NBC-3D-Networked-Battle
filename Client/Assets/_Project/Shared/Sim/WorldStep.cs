// 这个文件里有：
//   SimInput（结构体）：一帧里某个实体收到的输入，移动方向加攻击目标，是 Step 的唯一输入形状。
//   WorldStep（静态类）：帧同步的一帧推进，按固定顺序做移动、攻击判定与伤害结算，两端结果必须逐位相同。

using System.Collections.Generic;
using NBC.Shared.Battle;

namespace NBC.Shared.Sim
{
    // 主要功能：描述一帧里某个实体收到的输入，把移动方向与攻击目标按实体编号打包。
    // 主要数据成员：EntityId（实体编号，正整数）、MoveDirection（单位向量，无单位）、AttackTargetId（目标编号，0 = 不攻击）。
    // 主要外部方法：无，纯数据；由 LockstepClient 与 LockstepScheduler 填好后交给 WorldStep.Step。
    // 谁在用它：NBC.Shared 的 LockstepClient 与 LockstepScheduler 逐帧组装；服务端联调探针直接构造它驱动 Step。
    /// <summary>一帧里某个实体收到的输入：往哪走、打谁；零方向与 0 目标表示这一帧不动也不打。</summary>
    public struct SimInput
    {
        /// <summary>实体编号，对应世界实体表里的 Id；锁步名单只收正整数，0 与负数没有实体。</summary>
        public int EntityId;

        /// <summary>移动方向，无单位，按单位向量语义传；零向量表示不动，非零向量在 Step 里会先归一化。</summary>
        public FixVector3 MoveDirection;

        /// <summary>这一帧要攻击的目标实体编号，正整数；0 表示不攻击。</summary>
        public int AttackTargetId;
    }

    // 主要功能：把一帧推进拆成固定顺序的三段（移动、攻击判定、伤害结算），同样的世界加同样的输入，两端必须算出逐位相同的结果。
    // 主要数据成员：SpeedMmPerTick（毫米/tick）、AttackDamage（点，固定值）。
    // 主要外部方法：Step（推进一帧，返回命中次数）、MmToMeters（毫米换米）。
    // 谁在用它：NBC.Shared 的 LockstepClient 与 LockstepScheduler 每帧调用；服务端联调探针直接驱动它做确定性验收。
    /// <summary>确定性的一帧推进：给定同一份世界与输入，任何一端算出的下一帧必须逐位相同。</summary>
    public static class WorldStep
    {
        /// <summary>移动速度，单位毫米/tick，取 100；与 BattleRules 的距离单位同为毫米，Step 里先除以 1000 换成米/tick 再乘 dtTicks。</summary>
        public const int SpeedMmPerTick = 100;

        /// <summary>一次普攻的伤害，单位点，固定 25，不做伤害掷骰；只改目标 Hp。</summary>
        public const int AttackDamage = 25;

        /// <summary>把毫米换算成锁步世界用的米（定点），1000 毫米 = 1 米。</summary>
        /// <param name="mm">毫米数，可负；来自协议或战斗规则的距离与速度。</param>
        /// <returns>米数（Fix64 定点）。</returns>
        // 功能：把协议与战斗规则用的整数毫米换成锁步世界与 Unity 场景用的米。
        // 入参：mm 毫米数（可负；调用方传的是协议或战斗规则那套毫米）
        // 返回：米数（Fix64 定点）；1000 毫米 = 1 米，不足 1 米的零头保留在定点小数里
        // 步骤：
        //   1) 先把整数转成 Fix64，避免 mm / 1000 走整数除法把不足 1 米的零头截掉
        //   2) 再除以定点 1000，得到米；定点除法结果确定，两端同值
        // 上游：WorldStep.Step（每 tick 位移与射程）、服务端联调探针
        // 下游：Fix64.FromInt
        // 注意：本文件的毫米到米只在这一处换算，1000 这个系数别在别处再抄一份；协议与战斗规则用毫米，锁步世界与 Unity 场景用米。
        public static Fix64 MmToMeters(int mm)
        {
            return Fix64.FromInt(mm) / Fix64.FromInt(1000);
        }

        /// <summary>推进一帧：移动、攻击判定、伤害结算，最后把世界时钟拨到下一帧。</summary>
        /// <param name="world">要推进的世界，会被原地修改；为 null 时直接返回 0。</param>
        /// <param name="inputs">这一帧的输入，可为 null；只按实体编号查找，列表顺序不影响结果。</param>
        /// <param name="dtTicks">推进几个 tick，锁步恒为 1；小于等于 0 时直接返回 0。</param>
        /// <returns>这一帧实际造成的伤害次数，只给探针与日志用，不参与哈希；没有推进时返回 0。</returns>
        // 功能：把世界推进 dtTicks 个 tick，按 Id 升序让活着的实体移动、攻击并结算伤害，最后拨快时钟。
        // 入参：world 世界（原地修改，null 直接返回 0）；inputs 这一帧的输入（可为 null，只按 EntityId 查找）；dtTicks 推进几个 tick（锁步恒 1，<= 0 直接返回 0）
        // 返回：本帧真的打中的次数（给探针与日志用，不参与哈希）；world 为 null 或 dtTicks <= 0 时为 0
        // 步骤：
        //   1) 先挡 null 世界与非正 dtTicks，避免推进出无意义的帧
        //   2) 先算 nextTick = Tick + dtTicks，攻击冷却与最终时钟都以它为准（这时 world.Tick 还没变）
        //   3) 取 SnapshotSorted 的按 Id 升序快照再遍历，结果只由 Id 决定，与登记顺序无关
        //   4) 跳过 Hp <= 0 的实体：死了不动也不打，结算顺序因此与快照一致
        //   5) 按 Id 线性找输入，找不到就用 FindInput 的「不动不打」默认值
        //   6) 方向先归一化再乘「毫米/tick 换成米/tick」与 dtTicks，位移落在米上
        //   7) 攻击要同时满足有目标且 nextTick 已到冷却；目标位置读当前世界，不读旧快照（同帧内前面可能已移动）
        //   8) 射程比平方（不开方），伤害交给 DamageMath.Resolve；命中才写回目标、把自身冷却推到 nextTick 加 BasicAttackCooldownTicks（15 tick）、命中数加一
        //   9) 最后按 Id 找回自身下标写回位置与冷却，再 SetTick(nextTick)
        // 上游：LockstepClient.ApplyFrame 与 LockstepScheduler.BuildAndStep（由 AdvanceTick 驱动）每帧各走一次；服务端联调探针直接驱动做确定性验收
        // 下游：WorldState 的 SnapshotSorted / IndexOf / GetAt / SetAt / SetTick、FindInput、MmToMeters、DamageMath.Resolve、BattleRules
        // 注意：数值只用 Fix64 / FixVector3 / int，不许混进 float 或 double —— Shared\Sim 在 Check-FloatFree 的扫描范围里，混一个 double 闸门就红，两端结果也会分叉。
        // 注意：遍历必须走 SnapshotSorted，别改成 world.GetAt(i) 的存储顺序 —— 两端登记顺序不同就会静默分叉，顺序无关性用例会红。
        // 注意：伤害结算必须复用 DamageMath.Resolve，别写第二份 —— 两份实现的边界迟早分叉，而且不报错。
        // 注意：时间只用整数 tick，不引入定点秒；15 tick = 0.5 秒，dtTicks 是这一帧推进几个 tick（锁步恒 1，留参数是为了将来一次补多帧）。
        // 注意：inputs 的顺序不影响结果（只按 EntityId 查找），这是「顺序无关性」用例的前提。
        // 注意：目标位置必须读当前世界而不是快照，同帧内前面的实体可能已经移动过，读旧值会让射程判定偏差。
        // 注意：跨层数值按 1000 换算，协议与战斗规则毫米 ÷ 1000 = 锁步世界米；位置与射程都不能直接拿毫米比。
        public static int Step(WorldState world, IReadOnlyList<SimInput> inputs, int dtTicks)
        {
            if (world == null || dtTicks <= 0)
            {
                return 0;
            }

            int nextTick = world.Tick + dtTicks;
            int hits = 0;

            SimEntity[] snapshot = world.SnapshotSorted();

            for (int i = 0; i < snapshot.Length; i++)
            {
                SimEntity entity = snapshot[i];

                if (entity.Hp <= 0)
                {
                    continue;
                }

                SimInput input = FindInput(inputs, entity.Id);

                if (!input.MoveDirection.IsZero)
                {
                    FixVector3 step = input.MoveDirection.Normalized() *
                                      MmToMeters(SpeedMmPerTick) *
                                      Fix64.FromInt(dtTicks);

                    entity.Position = entity.Position + step;
                }

                if (input.AttackTargetId != 0 && nextTick >= entity.AttackReadyTick)
                {
                    int targetIndex = world.IndexOf(input.AttackTargetId);

                    if (targetIndex >= 0)
                    {
                        SimEntity target = world.GetAt(targetIndex);

                        Fix64 range = MmToMeters(BattleRules.BasicAttackRangeMm);
                        FixVector3 delta = target.Position - entity.Position;

                        if (delta.SqrMagnitude <= range * range)
                        {
                            DamageOutcome outcome = DamageMath.Resolve(target.Hp, AttackDamage);

                            target.Hp = outcome.RemainingHp;
                            world.SetAt(targetIndex, target);

                            entity.AttackReadyTick = nextTick + BattleRules.BasicAttackCooldownTicks;
                            hits++;
                        }
                    }
                }

                int selfIndex = world.IndexOf(entity.Id);

                if (selfIndex >= 0)
                {
                    world.SetAt(selfIndex, entity);
                }
            }

            world.SetTick(nextTick);
            return hits;
        }

        // 功能：按实体编号线性找这一帧的输入，找不到就返回一个「不动不打」的默认输入。
        // 入参：inputs 输入列表（可为 null，表示这一帧没有任何输入）；entityId 实体编号
        // 返回：命中的输入；inputs 为 null 或没有该编号时，返回 EntityId 已填好、方向为零、目标为 0 的默认值
        // 步骤：
        //   1) inputs 为 null 直接落到默认值，调用方不必自己判空
        //   2) 只按 EntityId 线性匹配 —— 实体只有几个，线性比字典更确定，也与输入列表顺序无关
        //   3) 没命中才构造默认值；默认值也带上 entityId，调试时能对上是谁
        // 上游：WorldStep.Step
        // 下游：无
        private static SimInput FindInput(IReadOnlyList<SimInput> inputs, int entityId)
        {
            if (inputs != null)
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    if (inputs[i].EntityId == entityId)
                    {
                        return inputs[i];
                    }
                }
            }

            var idle = new SimInput();
            idle.EntityId = entityId;
            idle.MoveDirection = FixVector3.Zero;
            idle.AttackTargetId = 0;
            return idle;
        }
    }
}
