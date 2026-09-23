// ============================================================================
//  BattleAgent —— 一个战斗单位（玩家或怪物）
//  项目：3D联网战斗Demo   对应：M2-B1
//
//  ---------------------------------------------------------------------------
//  它**只做三件事**，而且刻意不做别的
//  ---------------------------------------------------------------------------
//      ① 记住自己的属性（最大血 / 攻击 / 技能列表）
//      ② 记住自己现在剩多少血
//      ③ 提供一个"被打一下"的入口，内部调共享层的 `DamageMath` 结算
//
//  ⚠️ 它**不广播事件、不认识事件中心、不认识配置表**：
//     这些都是 `BattleWorld` 的事。理由：
//     · 单位对象是"数据 + 一条规则"，能脱离事件中心单独测（本项目的可测性套路）
//     · "谁死了要告诉谁"是**世界级**的判断（要知道是谁打死的、死了要不要从世界摘掉）
//
//  ⚠️ 它**不做 AI / 不移动**（M2 的范围决定，见 `Docs\22` B 组）：
//     怪的行为编排（巡逻/追击/技能循环）属 M3 的 BOSS 与行为树，
//     M2 的怪就是"站着让你打"，因为这一轮的验收是**任务闭环**，不是战斗手感。
//     如实记在这里，免得以后以为漏了。
//
//  ---------------------------------------------------------------------------
//  ⚠️ `Attack` 对英雄是没有意义的（读配置时给 0），这不是 bug
//  ---------------------------------------------------------------------------
//  `Hero` 表里**没有 attack 列** —— 英雄的伤害来自**技能**（`Skill.damage`），
//  而不是平砍。所以英雄的 `Attack` 是 0，伤害一律走 `BattleWorld.CastSkill`。
//  （如果哪天要做普攻，那是给 `Hero` 表加列 + 在这里接上，不是"把 0 当成 bug 修掉"。）
// ============================================================================

using System;
using NBC.Game.Config;
using NBC.Shared.Battle;

namespace NBC.Game.Battle
{
    /// <summary>战斗单位的种类。</summary>
    public enum EBattleAgentKind
    {
        /// <summary>玩家英雄。</summary>
        Hero = 0,

        /// <summary>怪物。</summary>
        Monster = 1
    }

    /// <summary>一个战斗单位。</summary>
    public sealed class BattleAgent
    {
        /// <summary>运行时唯一编号（由 `BattleWorld` 发号）。</summary>
        private readonly int m_instanceId;

        /// <summary>配置表编号（怪物 = `Monster` 主键；英雄 = `Hero` 主键）。</summary>
        private readonly int m_configId;

        /// <summary>种类。</summary>
        private readonly EBattleAgentKind m_kind;

        /// <summary>显示名（日志/UI 用）。</summary>
        private readonly string m_name;

        /// <summary>最大生命值。</summary>
        private readonly int m_maxHp;

        /// <summary>攻击力（**英雄恒为 0**，见文件头说明）。</summary>
        private readonly int m_attack;

        /// <summary>技能列表（可能为空数组）。</summary>
        private readonly int[] m_skillIds;

        /// <summary>当前生命值（**永远 ≥ 0**，由 `DamageMath` 保证）。</summary>
        private int m_hp;

        /// <summary>造一个战斗单位（只有 `BattleWorld` 会调）。</summary>
        /// <param name="instanceId">实例编号。</param>
        /// <param name="configId">配置编号。</param>
        /// <param name="kind">种类。</param>
        /// <param name="name">显示名。</param>
        /// <param name="maxHp">最大生命值（必须 ≥ 1）。</param>
        /// <param name="attack">攻击力。</param>
        /// <param name="skillIds">技能列表（可以为 null）。</param>
        internal BattleAgent(int instanceId, int configId, EBattleAgentKind kind, string name,
                            int maxHp, int attack, int[] skillIds)
        {
            if (maxHp < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxHp),
                    "[BattleAgent] 最大生命值必须 ≥ 1（当前 " + maxHp + "）。\n" +
                    "0 血的单位出生即死亡、还会立刻触发一次死亡事件 —— " +
                    "几乎总是配置填错了（`Monster.hp` 的规则行是 `range(1,999999)`）。");
            }

            m_instanceId = instanceId;
            m_configId = configId;
            m_kind = kind;
            m_name = name ?? string.Empty;
            m_maxHp = maxHp;
            m_attack = attack < 0 ? 0 : attack;
            m_skillIds = skillIds ?? new int[0];

            m_hp = maxHp;
        }

        /// <summary>实例编号（世界内唯一）。</summary>
        public int InstanceId
        {
            get { return m_instanceId; }
        }

        /// <summary>配置编号。</summary>
        public int ConfigId
        {
            get { return m_configId; }
        }

        /// <summary>种类。</summary>
        public EBattleAgentKind Kind
        {
            get { return m_kind; }
        }

        /// <summary>显示名。</summary>
        public string Name
        {
            get { return m_name; }
        }

        /// <summary>最大生命值。</summary>
        public int MaxHp
        {
            get { return m_maxHp; }
        }

        /// <summary>当前生命值。</summary>
        public int Hp
        {
            get { return m_hp; }
        }

        /// <summary>攻击力（英雄为 0）。</summary>
        public int Attack
        {
            get { return m_attack; }
        }

        /// <summary>还活着吗（HP &gt; 0）。</summary>
        public bool IsAlive
        {
            get { return m_hp > 0; }
        }

        /// <summary>技能列表（只读视图；空数组表示没有技能）。</summary>
        public int[] SkillIds
        {
            get { return m_skillIds; }
        }

        /// <summary>
        /// 挨一下伤害：内部走共享层的 <see cref="DamageMath"/> 结算并改自己的血。
        /// <para>⚠️ **不广播事件**（那是 `BattleWorld` 的事），但**会**拒绝非法输入。</para>
        /// </summary>
        /// <param name="damage">伤害值（≥ 0）。</param>
        /// <returns>结算结果。</returns>
        public DamageOutcome ApplyDamage(int damage)
        {
            DamageOutcome outcome = DamageMath.Resolve(m_hp, damage);
            m_hp = outcome.RemainingHp;
            return outcome;
        }

        /// <summary>从配置表的一行怪物造一个单位（**只有 `BattleWorld` 会调**）。</summary>
        /// <param name="instanceId">实例编号。</param>
        /// <param name="row">怪物表的行。</param>
        /// <returns>战斗单位。</returns>
        internal static BattleAgent FromMonster(int instanceId, Config_Monster row)
        {
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row), "[BattleAgent] 怪物配置行是 null。");
            }

            // 怪物表没有攻击外的技能之外的属性；skillIds 由生成物保证不是 null
            return new BattleAgent(instanceId, row.id, EBattleAgentKind.Monster,
                                   row.name, row.hp, row.attack, row.skillIds);
        }

        /// <summary>从配置表的一行英雄造一个单位（**只有 `BattleWorld` 会调**）。</summary>
        /// <param name="instanceId">实例编号。</param>
        /// <param name="row">英雄表的行。</param>
        /// <returns>战斗单位。</returns>
        internal static BattleAgent FromHero(int instanceId, Config_Hero row)
        {
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row), "[BattleAgent] 英雄配置行是 null。");
            }

            // ⚠️ 攻击力传 0：英雄的伤害来自技能，`Hero` 表里根本没有 attack 列（见文件头）
            return new BattleAgent(instanceId, row.id, EBattleAgentKind.Hero,
                                   row.name, row.hp, 0, row.skillIds);
        }

        /// <summary>转成一句人话。</summary>
        /// <returns>例如「野狼#3（怪物 6001） 220/300」。</returns>
        public override string ToString()
        {
            return m_name + "#" + m_instanceId + "（" +
                   (m_kind == EBattleAgentKind.Monster ? "怪物 " : "英雄 ") + m_configId + "） " +
                   m_hp + "/" + m_maxHp;
        }
    }
}
