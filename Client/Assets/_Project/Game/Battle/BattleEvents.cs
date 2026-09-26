// ============================================================================
//  BattleEvents —— 战斗模块对外广播的事件
//  项目：3D联网战斗Demo   对应：M2-B1、A3（EventId 强类型）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么"怪物死了"和"英雄死了"是**两个事件**，而不是一个带 kind 的
//  ---------------------------------------------------------------------------
//  直觉写法是 `AgentDied { InstanceId, Kind, KillerId }`，订阅方自己判 kind。
//  但订阅方里最要紧的一个是**任务系统的条件桥**：
//
//      KillMonster 这个条件事件只能由"怪物死了"触发。
//      如果共用 `AgentDied`，桥里就得写 `p => p.Kind == Monster ? p.ConfigId : 0`。
//      一旦这个判断写错（或者将来加了第三方阵营），
//      "玩家死了"会被当成"击杀了一个目标 0"，而**目标 0 = 任意目标** ——
//      于是**任何**"击杀任意怪 N 只"的任务都会涨进度。**静默、且看起来很正常。**
//
//  两个事件 = 类型系统帮你把这件事钉死，桥里一行过滤都不用写。
//  代价只是多声明一个 `EventId`（一个静态字段）。
//
//  ---------------------------------------------------------------------------
//  载荷为什么要同时带「实例编号」和「配置编号」（一个真会踩的坑）
//  ---------------------------------------------------------------------------
//      ConfigId   = 配置表里的怪物编号（6001 = 野狼）→ **任务条件、掉落、AI 用它**
//      InstanceId = 战斗世界运行期发的唯一号 → **从那一个具体的怪身上做操作时用它**
//
//  M2 一局只刷一只狼，两者看起来可以合并。但同一个 6001 刷两只就会撞号：
//  给 A 上 debuff 会同时影响 B，A 死了会把 B 从世界里摘掉。
//  这类 bug 只在"同屏两只同样的怪"时出现，很难在手测里碰到。
// ============================================================================

using NBC.Framework;

namespace NBC.Game.Battle
{
    /// <summary>战斗模块的事件标识。</summary>
    public static class BattleEvents
    {
        /// <summary>一个怪物死了。载荷：<see cref="MonsterDiedPayload"/>。</summary>
        public static readonly EventId MonsterDied = EventId.Declare("Battle.MonsterDied");

        /// <summary>一个英雄单位死了。载荷：<see cref="HeroDiedPayload"/>。</summary>
        public static readonly EventId HeroDied = EventId.Declare("Battle.HeroDied");

        /// <summary>造成了伤害（**每次扣血都发**，伤害数字飘字靠它）。载荷：<see cref="DamageDealtPayload"/>。</summary>
        public static readonly EventId DamageDealt = EventId.Declare("Battle.DamageDealt");

        /// <summary>技能命中了目标（**扣血之前发**，见 `BattleWorld.CastSkill` 的顺序契约）。载荷：<see cref="SkillHitPayload"/>。</summary>
        public static readonly EventId SkillHit = EventId.Declare("Battle.SkillHit");

        /// <summary>
        /// 我**获得了**掉落物（M4-S1 新增；载荷：<see cref="ItemDroppedPayload"/>）。
        /// <para>⚠️ 只有**归我**的掉落才会发这个事件 —— 理由同文件头那条（"两个事件而不是一个带 kind 的"）：
        /// 若把"别人的掉落"也发出来、让订阅方自己判归属，判错就会让"收集物品"任务**凭空涨进度**
        /// （别人的战利品算到了我头上）。所以归属判断放在**产生事件的地方**（`ServerEventBridge`），
        /// 订阅方拿到就是"我的"。</para>
        /// </summary>
        public static readonly EventId ItemDropped = EventId.Declare("Battle.ItemDropped");
    }

    /// <summary>我获得掉落物的载荷（M4-S1）。</summary>
    public readonly struct ItemDroppedPayload
    {
        /// <summary>物品编号（`Item` 表主键 / 协议里的 `item_id`）。</summary>
        public readonly int ItemId;

        /// <summary>数量。</summary>
        public readonly int Count;

        /// <summary>造一个载荷。</summary>
        /// <param name="itemId">物品编号。</param>
        /// <param name="count">数量。</param>
        public ItemDroppedPayload(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }
    }

    /// <summary>怪物死亡的载荷。</summary>
    public readonly struct MonsterDiedPayload
    {
        /// <summary>这一只怪的**实例编号**（从世界里摘掉它时用）。</summary>
        public readonly int InstanceId;

        /// <summary>这一只怪的**配置编号**（任务条件 / 掉落 / AI 用；6001 = 野狼）。</summary>
        public readonly int MonsterConfigId;

        /// <summary>击杀者的实例编号（0 = 没有击杀者，例如环境伤害）。</summary>
        public readonly int KillerInstanceId;

        /// <summary>造一个载荷。</summary>
        /// <param name="instanceId">实例编号。</param>
        /// <param name="monsterConfigId">配置编号。</param>
        /// <param name="killerInstanceId">击杀者实例编号。</param>
        public MonsterDiedPayload(int instanceId, int monsterConfigId, int killerInstanceId)
        {
            InstanceId = instanceId;
            MonsterConfigId = monsterConfigId;
            KillerInstanceId = killerInstanceId;
        }
    }

    /// <summary>英雄死亡的载荷。</summary>
    public readonly struct HeroDiedPayload
    {
        /// <summary>实例编号。</summary>
        public readonly int InstanceId;

        /// <summary>英雄配置编号（`Hero` 表主键）。</summary>
        public readonly int HeroConfigId;

        /// <summary>击杀者实例编号（0 = 没有）。</summary>
        public readonly int KillerInstanceId;

        /// <summary>造一个载荷。</summary>
        /// <param name="instanceId">实例编号。</param>
        /// <param name="heroConfigId">英雄配置编号。</param>
        /// <param name="killerInstanceId">击杀者实例编号。</param>
        public HeroDiedPayload(int instanceId, int heroConfigId, int killerInstanceId)
        {
            InstanceId = instanceId;
            HeroConfigId = heroConfigId;
            KillerInstanceId = killerInstanceId;
        }
    }

    /// <summary>伤害事件的载荷。</summary>
    public readonly struct DamageDealtPayload
    {
        /// <summary>攻击者实例编号。</summary>
        public readonly int AttackerInstanceId;

        /// <summary>受击者实例编号。</summary>
        public readonly int TargetInstanceId;

        /// <summary>实际扣掉的血。</summary>
        public readonly int Applied;

        /// <summary>结算后剩余的血。</summary>
        public readonly int RemainingHp;

        /// <summary>造一个载荷。</summary>
        /// <param name="attackerInstanceId">攻击者实例编号。</param>
        /// <param name="targetInstanceId">受击者实例编号。</param>
        /// <param name="applied">实际扣血。</param>
        /// <param name="remainingHp">剩余血。</param>
        public DamageDealtPayload(int attackerInstanceId, int targetInstanceId, int applied, int remainingHp)
        {
            AttackerInstanceId = attackerInstanceId;
            TargetInstanceId = targetInstanceId;
            Applied = applied;
            RemainingHp = remainingHp;
        }
    }

    /// <summary>技能命中的载荷。</summary>
    public readonly struct SkillHitPayload
    {
        /// <summary>施法者实例编号。</summary>
        public readonly int CasterInstanceId;

        /// <summary>目标实例编号。</summary>
        public readonly int TargetInstanceId;

        /// <summary>技能编号（`Skill` 表主键）。</summary>
        public readonly int SkillId;

        /// <summary>目标的**配置编号**（任务条件要的是"命中的是谁"，用配置编号）。</summary>
        public readonly int TargetConfigId;

        /// <summary>造一个载荷。</summary>
        /// <param name="casterInstanceId">施法者实例编号。</param>
        /// <param name="targetInstanceId">目标实例编号。</param>
        /// <param name="skillId">技能编号。</param>
        /// <param name="targetConfigId">目标配置编号。</param>
        public SkillHitPayload(int casterInstanceId, int targetInstanceId, int skillId, int targetConfigId)
        {
            CasterInstanceId = casterInstanceId;
            TargetInstanceId = targetInstanceId;
            SkillId = skillId;
            TargetConfigId = targetConfigId;
        }
    }
}
