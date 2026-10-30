// 这个文件里有：
//   EBattleAgentKind（枚举）：战斗单位的种类，玩家英雄或怪物
//   BattleAgent（类）：一个战斗单位的数据与一条「挨打结算」规则，不管事件、AI 与移动

using System;
using NBC.Game.Config;
using NBC.Shared.Battle;

namespace NBC.Game.Battle
{
    // 主要功能：区分玩家英雄与怪物，决定 Attack 有没有意义，以及死亡事件里怎么认出怪物。
    // 主要数据成员：Hero（0）、Monster（1）。
    // 主要外部方法：无。
    // 谁在用它：BattleWorld 筛怪、ServerEventBridge 把死亡事件的种类翻回怪物、EditMode 用例断言种类。
    // 注意：英雄的 Attack 恒为 0，伤害来自技能 —— 别把它当成配置错误。
    public enum EBattleAgentKind
    {
        // 玩家英雄：0，伤害走技能，Attack 恒为 0
        Hero = 0,

        // 怪物：1，Attack 是平砍伤害（点）
        Monster = 1
    }

    // 主要功能：持有一个战斗单位的属性与当前血量，只提供「挨一下伤害」这一条规则。
    // 主要数据成员：m_hp（当前生命值，点）、m_maxHp（最大生命值，点）、m_attack（攻击力，点）、m_skillIds（技能编号列表）。
    // 主要外部方法：InstanceId / Hp / IsAlive / ApplyDamage / ToString（只读属性与实例方法）。
    // 谁在用它：BattleWorld 造与摘，BattleSession 持 Hero 与在场列表，Boot 的演示读 Hero.SkillIds 取技能并把 Hero 直接拼进界面，EditMode 用例断言属性。
    // 注意：它刻意不广播事件、不认识事件中心与配置表 —— 那些是 BattleWorld 的事；单位只管「数据 + 一条规则」，才能脱离事件中心单独测。
    // 注意：它不做 AI、不移动是范围决定（怪就是站着让你打），行为编排留给以后的行为树（详见 Docs\22）。
    // 注意：Attack 对英雄没有意义（读配置时给 0），英雄伤害一律走 BattleWorld.CastSkill。
    public sealed class BattleAgent
    {
        // 运行时唯一编号，由 BattleWorld 递增发号，从 1 起
        private readonly int m_instanceId;

        // 配置表主键：怪物是 Monster 的主键，英雄是 Hero 的主键
        private readonly int m_configId;

        // 种类：玩家英雄或怪物
        private readonly EBattleAgentKind m_kind;

        // 显示名（日志与 UI 用），构造时 null 归一成空串
        private readonly string m_name;

        // 最大生命值（点），构造时要求 ≥ 1
        private readonly int m_maxHp;

        // 攻击力（点），构造时负数按 0；英雄恒为 0，伤害走技能
        private readonly int m_attack;

        // 技能编号列表（Skill 表主键），构造时 null 归一成空数组
        private readonly int[] m_skillIds;

        // 当前生命值（点），范围 0..m_maxHp；只经 DamageMath 结算，0 即死亡
        private int m_hp;

        // 功能：把配置行的属性抄进字段，当前血从满血开始。
        // 入参：instanceId 世界内唯一编号；configId 配置主键；kind 种类；name 显示名（null 按空串）；
        //       maxHp 最大生命值（点，必须 ≥ 1）；attack 攻击力（点，负数按 0）；skillIds 技能编号列表（null 按空数组）
        // 返回：无
        // 步骤：
        //   1) 先校验 maxHp ≥ 1：0 血单位出生即死、还会立刻触发一次死亡事件，几乎总是配置填错（Monster.hp 的规则行是 range(1,999999)）
        //   2) 逐个字段赋值；name 与 skillIds 的 null 在这里归一，之后所有读取都不用再判空
        //   3) 当前血置为最大血 —— 出生即满血
        // 上游：FromMonster / FromHero（只有 BattleWorld 会经它们调）
        // 下游：无
        // 注意：maxHp < 1 抛 ArgumentOutOfRangeException，不静默抬到 1 —— 那样只是把配置错误藏起来。
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

        // 功能：读世界内唯一的实例编号。
        public int InstanceId
        {
            get { return m_instanceId; }
        }

        // 功能：读配置表主键：怪物为 Monster 的主键，英雄为 Hero 的主键。
        public int ConfigId
        {
            get { return m_configId; }
        }

        // 功能：读单位种类（玩家英雄或怪物）。
        public EBattleAgentKind Kind
        {
            get { return m_kind; }
        }

        // 功能：读显示名（构造时已把 null 归一成空串）。
        public string Name
        {
            get { return m_name; }
        }

        // 功能：读最大生命值（点）。
        public int MaxHp
        {
            get { return m_maxHp; }
        }

        // 功能：读当前生命值（点，范围 0..m_maxHp）。
        public int Hp
        {
            get { return m_hp; }
        }

        // 功能：读攻击力（点，英雄恒为 0）。
        public int Attack
        {
            get { return m_attack; }
        }

        // 功能：判断还活着吗（当前血 > 0）。
        public bool IsAlive
        {
            get { return m_hp > 0; }
        }

        /// <summary>技能编号列表，空数组表示没有技能。</summary>
        // 功能：把内部技能编号数组原样交出去（不是副本，调用方别改）。
        public int[] SkillIds
        {
            get { return m_skillIds; }
        }

        // 功能：按共享层 DamageMath 结算一次伤害，并把结果写回自己的血。
        // 入参：damage 伤害值（点，必须 ≥ 0；负数由 DamageMath 抛异常，不在这里圆）
        // 返回：结算结果：Applied 实际扣血、RemainingHp 剩余血、Overkill 过量伤害、IsLethal 这一下是否致死
        // 步骤：
        //   1) 交给 DamageMath.Resolve 算，伤害规则只写在那一边，这里不重复实现
        //   2) 把 RemainingHp 写回 m_hp，本类只负责持有血量
        // 上游：BattleWorld.ApplyDamage（CastSkill 也经它）
        // 下游：DamageMath.Resolve
        // 注意：这里不广播事件 —— 谁死了要告诉谁是 BattleWorld 的事，单位才能脱离事件中心单独测。
        // 注意：打已经 0 血的目标不扣血、也不算致死，这条由 DamageMath 保证，这里不用再判一次。
        public DamageOutcome ApplyDamage(int damage)
        {
            DamageOutcome outcome = DamageMath.Resolve(m_hp, damage);
            m_hp = outcome.RemainingHp;
            return outcome;
        }

        // 功能：用怪物表的一行造一个怪物单位。
        // 入参：instanceId 世界内唯一编号；row 怪物表的行（null 抛 ArgumentNullException）
        // 返回：新单位，还没进世界（登记由 BattleWorld 做）
        // 步骤：
        //   1) 先判 row 为 null：配置错误尽早炸，别等到读字段时才空引用
        //   2) 把 row.id / row.name / row.hp / row.attack / row.skillIds 原样交给构造函数
        // 上游：BattleWorld.SpawnMonster
        // 下游：构造函数
        // 注意：skillIds 由生成物保证不是 null，这里不补默认值；hp 与 attack 的单位都是点。
        internal static BattleAgent FromMonster(int instanceId, Config_Monster row)
        {
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row), "[BattleAgent] 怪物配置行是 null。");
            }

            return new BattleAgent(instanceId, row.id, EBattleAgentKind.Monster,
                                   row.name, row.hp, row.attack, row.skillIds);
        }

        // 功能：用英雄表的一行造一个英雄单位。
        // 入参：instanceId 世界内唯一编号；row 英雄表的行（null 抛 ArgumentNullException）
        // 返回：新单位，还没进世界（登记由 BattleWorld 做）
        // 步骤：
        //   1) 先判 row 为 null：配置错误尽早炸，别等到读字段时才空引用
        //   2) 攻击力固定传 0 —— 英雄的伤害来自技能，Hero 表里根本没有 attack 列
        // 上游：BattleWorld.SpawnHero
        // 下游：构造函数
        // 注意：传 0 不是 bug；哪天做普攻，是给 Hero 表加列再在这里接上，别把 0 当成 bug 修掉。
        internal static BattleAgent FromHero(int instanceId, Config_Hero row)
        {
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row), "[BattleAgent] 英雄配置行是 null。");
            }

            return new BattleAgent(instanceId, row.id, EBattleAgentKind.Hero,
                                   row.name, row.hp, 0, row.skillIds);
        }

        /// <summary>拼成一句人话，给日志与 Boot 的演示界面直接显示。</summary>
        /// <returns>形如「野狼#3（怪物 6001） 220/300」的字符串。</returns>
        // 功能：把名字、实例编号、种类、配置编号与当前血量拼成一句可读文案。
        // 入参：无
        // 返回：形如「野狼#3（怪物 6001） 220/300」；括号里是种类与配置编号，末尾是当前血/最大血
        // 步骤：
        //   1) 名字与实例编号用井号相连，日志里好认是哪一个
        //   2) 括号里写种类与配置编号，同一配置刷出来的多只也能对上
        //   3) 末尾写当前血/最大血，单位是点
        // 上游：日志与 Boot 的演示界面
        // 下游：无
        public override string ToString()
        {
            return m_name + "#" + m_instanceId + "（" +
                   (m_kind == EBattleAgentKind.Monster ? "怪物 " : "英雄 ") + m_configId + "） " +
                   m_hp + "/" + m_maxHp;
        }
    }
}
