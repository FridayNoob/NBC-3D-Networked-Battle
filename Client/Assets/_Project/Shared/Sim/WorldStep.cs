// ============================================================================
//  WorldStep —— 帧同步的**一帧推进**（M4-S4 S4-b）
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十九 S4-b、§三十一
//
//  ---------------------------------------------------------------------------
//  一、它的形状：`frame N+1 = Step(frame N, inputs[N])`
//  ---------------------------------------------------------------------------
//  确定性帧同步的全部要求就是这句：**给定同样的世界与同样的输入，任何一端算出的
//  frame N+1 必须逐位相同**。所以这里：
//      · 只用 `Fix64` / `FixVector3` / `int`（**没有 float/double**，`Shared\Sim\` 在
//        `Check-FloatFree` 的扫描范围里 —— 写一个 double 进去那条闸门就会红）
//      · **遍历顺序确定**：一律用 `world.SnapshotSorted()`（见 `WorldState` 文件头）
//      · 循环边界用**排序后的快照**，不用 `world.Count`（后者是存储顺序，会受登记顺序影响）
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 伤害结算**复用** `DamageMath`（别写第二份）
//  ---------------------------------------------------------------------------
//  `Shared\Battle\DamageMath.Resolve(currentHp, damage)` 已经是**纯整数**结算
//  （`Applied` / `RemainingHp` / `Overkill` / `IsLethal`），状态同步那条链就用它。
//  这里**照用同一份** —— 两份实现迟早会因为"边界怎么算"分叉，而且不报错。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ 时间用**整数 tick**，不用"定点秒"
//  ---------------------------------------------------------------------------
//  `BattleRules.BasicAttackCooldownTicks = 15` 就是这样定的。少引入一种时间单位
//  就少一处换算错；`dtTicks` 直接是"这一帧推进几个 tick"（锁步里恒为 1，留参数
//  是为了将来能一次补多帧）。
//
//  移动速度：`SpeedMmPerTick`（**毫米/tick**，整数）⇒ 换算成米/tick 后乘 `dtTicks`。
// ============================================================================

using System.Collections.Generic;
using NBC.Shared.Battle;

namespace NBC.Shared.Sim
{
    /// <summary>一帧里某个实体收到的输入。</summary>
    public struct SimInput
    {
        /// <summary>哪个实体。</summary>
        public int EntityId;

        /// <summary>这一帧的移动方向（**单位向量语义**；零向量 = 不动）。</summary>
        public FixVector3 MoveDirection;

        /// <summary>这一帧要打的目标编号（0 = 不攻击）。</summary>
        public int AttackTargetId;
    }

    /// <summary>确定性的一帧推进。</summary>
    public static class WorldStep
    {
        /// <summary>移动速度（**毫米/tick**）—— 与 `BattleRules` 的距离单位一致。</summary>
        public const int SpeedMmPerTick = 100;

        /// <summary>一次攻击的伤害（固定值：本片只验确定性，不做伤害掷骰）。</summary>
        public const int AttackDamage = 25;

        /// <summary>把毫米换成米（`Fix64`）。</summary>
        /// <param name="mm">毫米数。</param>
        /// <returns>米数。</returns>
        public static Fix64 MmToMeters(int mm)
        {
            return Fix64.FromInt(mm) / Fix64.FromInt(1000);
        }

        /// <summary>
        /// 推进一帧：移动 → 攻击判定 → 伤害结算。
        ///
        /// <para>⚠️ **遍历顺序**：一律走 `world.SnapshotSorted()`；`inputs` 只被**按实体编号查找**，
        /// 所以调用方传进来的输入顺序**不影响**结果（这也是"顺序无关性"那条用例的前提）。</para>
        /// </summary>
        /// <param name="world">世界（会被推进）。</param>
        /// <param name="inputs">这一帧的输入。</param>
        /// <param name="dtTicks">推进几个 tick（锁步里恒为 1）。</param>
        /// <returns>这一帧造成的**伤害次数**（给探针/日志用，不参与哈希）。</returns>
        public static int Step(WorldState world, IReadOnlyList<SimInput> inputs, int dtTicks)
        {
            if (world == null || dtTicks <= 0)
            {
                return 0;
            }

            int nextTick = world.Tick + dtTicks;
            int hits = 0;

            // ⚠️ 按 Id 升序的快照 —— **不要**改成 world.GetAt(i) 循环（那是登记顺序）
            SimEntity[] snapshot = world.SnapshotSorted();

            for (int i = 0; i < snapshot.Length; i++)
            {
                SimEntity entity = snapshot[i];

                if (entity.Hp <= 0)
                {
                    continue;       // 死了就不动、不打（结算顺序因此也与快照一致）
                }

                SimInput input = FindInput(inputs, entity.Id);

                // ---- ① 移动 ----
                if (!input.MoveDirection.IsZero)
                {
                    FixVector3 step = input.MoveDirection.Normalized() *
                                      MmToMeters(SpeedMmPerTick) *
                                      Fix64.FromInt(dtTicks);

                    entity.Position = entity.Position + step;
                }

                // ---- ② 攻击判定（射程 + 冷却）----
                if (input.AttackTargetId != 0 && nextTick >= entity.AttackReadyTick)
                {
                    int targetIndex = world.IndexOf(input.AttackTargetId);

                    if (targetIndex >= 0)
                    {
                        // 目标位置取**当前**世界里的值（快照可能是旧的：同帧内前面已移动过）
                        SimEntity target = world.GetAt(targetIndex);

                        Fix64 range = MmToMeters(BattleRules.BasicAttackRangeMm);
                        FixVector3 delta = target.Position - entity.Position;

                        if (delta.SqrMagnitude <= range * range)
                        {
                            // ⚠️ 复用共享层的伤害结算（见文件头第二节）
                            DamageOutcome outcome = DamageMath.Resolve(target.Hp, AttackDamage);

                            target.Hp = outcome.RemainingHp;
                            world.SetAt(targetIndex, target);

                            entity.AttackReadyTick = nextTick + BattleRules.BasicAttackCooldownTicks;
                            hits++;
                        }
                    }
                }

                // 写回（位置/冷却都可能变了）
                int selfIndex = world.IndexOf(entity.Id);

                if (selfIndex >= 0)
                {
                    world.SetAt(selfIndex, entity);
                }
            }

            world.SetTick(nextTick);
            return hits;
        }

        /// <summary>按实体编号找输入（**线性查找**：与输入列表的顺序无关）。</summary>
        /// <param name="inputs">输入列表（可以为 null = 这一帧没输入）。</param>
        /// <param name="entityId">实体编号。</param>
        /// <returns>输入；没找到时返回一个"什么都不做"的输入。</returns>
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
