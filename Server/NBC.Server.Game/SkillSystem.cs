// 这个文件里有：
//   SkillSystem（类）：服务端的技能规则与命中延迟队列 —— 五道关（活人/有表/配了/目标合法/射程/冷却）、
//   冷却记账、以及"到帧才扣血"的待结算列表。

using System;
using System.Collections.Generic;
using NBC.Shared.Battle;

namespace NBC.Server.Game
{
    // 主要功能：判定一次技能能不能放，放出去之后按命中帧延迟扣血。
    // 主要数据成员：_tables（配置表）、_cooldowns（每对「施法者+技能」剩余逻辑帧）、_pending（待结算队列）。
    // 主要外部方法：TryCast（入口，五道关）、Tick（每帧递减与结算）、CooldownTicksLeft（给界面/探针查剩余帧）。
    // 谁在用它：DungeonBattle（门面转调 + 每帧 Tick）、RoomBattleService（玩家上行）、CastSkillAction（BOSS）。
    // 注意：规则只在这里实现一份；DungeonBattle 不再复制"射程/冷却"判断。
    public sealed class SkillSystem
    {
        // 配置表（技能与单位的技能列表都从这里读）
        private readonly ServerTables m_tables;

        // 冷却表：键是「施法者实例编号 + 技能编号」，值是剩余逻辑帧（0 或不存在表示可用）
        private readonly Dictionary<(int CasterId, int SkillId), int> m_cooldowns = new();

        // 待结算队列（按入队顺序结算，保证同帧多次释放的顺序确定）
        private readonly List<PendingCast> m_pending = new();

        // 一次待结算的命中
        private readonly struct PendingCast
        {
            // 施法者实例编号（0 表示没有）
            public readonly int CasterId;

            // 目标实例编号
            public readonly int TargetId;

            // 伤害（点）
            public readonly int Damage;

            // 还剩几帧生效
            public readonly int TicksLeft;

            // 功能：记一次待结算。
            // 入参：casterId 施法者；targetId 目标；damage 伤害；ticksLeft 剩余帧
            // 返回：无
            // 步骤：逐字段赋值
            // 上游：TryCast
            // 下游：Tick
            public PendingCast(int casterId, int targetId, int damage, int ticksLeft)
            {
                CasterId = casterId;
                TargetId = targetId;
                Damage = damage;
                TicksLeft = ticksLeft;
            }

            // 功能：返回一个"帧数减一"的新值。
            // 入参：无
            // 返回：TicksLeft - 1 的新副本
            // 步骤：构造新结构体（结构体不可变，避免队列里被就地改）
            // 上游：Tick
            // 下游：无
            public PendingCast Decrement()
            {
                return new PendingCast(CasterId, TargetId, Damage, TicksLeft - 1);
            }
        }

        // 功能：构造技能系统。
        // 入参：tables 配置表
        // 返回：无
        // 步骤：只保存引用，不预读任何技能（表可能还没加载完）
        // 上游：DungeonBattle.AttachTables
        // 下游：TryCast / Tick
        public SkillSystem(ServerTables tables)
        {
            m_tables = tables;
        }

        // 已经入队、还没结算的命中数（探针与日志用）
        public int PendingCount => m_pending.Count;

        // 累计成功释放次数（日志用）
        public long CastCount { get; private set; }

        // 功能：查某个单位的某个技能还剩多少冷却帧。
        // 入参：casterId 施法者实例编号；skillId 技能编号
        // 返回：剩余逻辑帧；0 表示可用
        // 步骤：查字典，没有就是 0
        // 上游：DungeonBattle.SkillCooldownTicksLeft、R043a 技能栏
        // 下游：无
        public int CooldownTicksLeft(int casterId, int skillId)
        {
            if (m_cooldowns.TryGetValue((casterId, skillId), out int ticks))
            {
                return ticks;
            }

            return 0;
        }

        // 功能：判定并释放一次技能（五道关全部通过才入队）。
        // 入参：battle 世界；caster 施法者；skillId 技能编号；targetId 目标实例编号；reason 被拒原因（成功为空串）
        // 返回：入队成功返回 true
        // 步骤：
        //   1) 施法者死了 / 表没挂 → 拒
        //   2) 技能不在表里 → 拒
        //   3) 施法者没配这个技能 → 拒（英雄看 Hero.skillIds，怪看 Monster.skillIds）
        //   4) 目标不存在 / 死了 / 同阵营 → 拒
        //   5) 超出技能射程（圆形）→ 拒
        //   6) 冷却未好 → 拒
        //   7) 全过：写冷却、入队（命中帧后扣血）、计数
        // 上游：DungeonBattle.TryCastSkill
        // 下游：BattleRules.WithinRange、PendingCast
        public bool TryCast(DungeonBattle battle, BattleEntity caster, int skillId, int targetId, out string reason)
        {
            reason = string.Empty;

            if (battle == null || caster == null)
            {
                reason = "施法者或世界为空";
                return false;
            }

            if (!caster.Alive)
            {
                reason = "施法者已经死了";
                return false;
            }

            SkillRow? found = m_tables?.FindSkill(skillId);

            if (found == null)
            {
                reason = $"技能 {skillId} 不在 Skill 表里";
                return false;
            }

            SkillRow skill = found.Value;

            if (!ConfiguredFor(caster, skillId))
            {
                reason = $"单位 {caster.Id}（配置 {caster.ConfigId}）没有配置技能 {skillId}";
                return false;
            }

            BattleEntity? target = battle.Find(targetId);

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

            if (target.Kind == caster.Kind)
            {
                reason = "不能对友军放技能";
                return false;
            }

            if (!BattleRules.WithinRange(caster.PosXmm, caster.PosZmm, target.PosXmm, target.PosZmm, skill.RangeMm))
            {
                int actualMm = BattleRules.DistanceMmRounded(caster.PosXmm, caster.PosZmm, target.PosXmm, target.PosZmm);
                reason = $"目标 {targetId} 在技能射程外（{actualMm}mm > {skill.RangeMm}mm）";
                return false;
            }

            int left = CooldownTicksLeft(caster.Id, skillId);

            if (left > 0)
            {
                reason = $"技能 {skillId} 还在冷却（还剩 {left} 帧）";
                return false;
            }

            if (skill.CooldownTicks > 0)
            {
                m_cooldowns[(caster.Id, skillId)] = skill.CooldownTicks;
            }

            m_pending.Add(new PendingCast(caster.Id, targetId, skill.Damage, skill.HitFrame));
            CastCount++;
            return true;
        }

        // 功能：每帧推进 —— 冷却递减、待结算递减到 0 就扣血。
        // 入参：battle 世界
        // 返回：无
        // 步骤：
        //   1) 冷却表逐个减 1，到 0 就移除（用临时列表避免边遍历边改）
        //   2) 待结算队列逐个减 1；到 0 的立刻 ApplyDamage 并移出队列
        // 上游：DungeonBattle.Step
        // 下游：DungeonBattle.ApplyDamage
        public void Tick(DungeonBattle battle)
        {
            if (m_cooldowns.Count > 0)
            {
                var expired = new List<(int CasterId, int SkillId)>();

                foreach (KeyValuePair<(int CasterId, int SkillId), int> pair in m_cooldowns)
                {
                    int next = pair.Value - 1;

                    if (next <= 0)
                    {
                        expired.Add(pair.Key);
                    }
                    else
                    {
                        m_cooldowns[pair.Key] = next;
                    }
                }

                for (int i = 0; i < expired.Count; i++)
                {
                    m_cooldowns.Remove(expired[i]);
                }
            }

            for (int i = m_pending.Count - 1; i >= 0; i--)
            {
                PendingCast cast = m_pending[i];

                if (cast.TicksLeft <= 1)
                {
                    m_pending.RemoveAt(i);

                    if (battle != null)
                    {
                        battle.ApplyDamage(cast.TargetId, cast.Damage, cast.CasterId);
                    }
                }
                else
                {
                    m_pending[i] = cast.Decrement();
                }
            }
        }

        // 功能：判断某个单位有没有在表里配置这个技能。
        // 入参：caster 施法者；skillId 技能编号
        // 返回：配置了返回 true
        // 步骤：
        //   1) 英雄（kind 0）查 Hero 表的 skillIds
        //   2) 怪（kind 1）查 Monster 表的 SkillIds
        //   3) 其它 kind 一律当作没配
        // 上游：TryCast
        // 下游：ServerTables.FindHero / FindMonster
        private bool ConfiguredFor(BattleEntity caster, int skillId)
        {
            int[] ids = Array.Empty<int>();

            if (caster.Kind == 0)
            {
                HeroRow? hero = m_tables?.FindHero(caster.ConfigId);
                ids = hero?.SkillIds ?? Array.Empty<int>();
            }
            else if (caster.Kind == 1)
            {
                MonsterRow? monster = m_tables?.FindMonster(caster.ConfigId);
                ids = monster?.SkillIds ?? Array.Empty<int>();
            }

            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] == skillId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
