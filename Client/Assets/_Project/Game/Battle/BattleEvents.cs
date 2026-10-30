// 这个文件里有：
//   BattleEvents（静态类）：战斗模块对外广播的五个事件标识，怪物死、英雄死、伤害、技能命中与我的掉落各一个
//   ItemDroppedPayload（结构体）：归我的掉落载荷，带物品编号与数量
//   MonsterDiedPayload（结构体）：怪物死亡载荷，带实例编号、配置编号与击杀者编号
//   HeroDiedPayload（结构体）：英雄死亡载荷，带实例编号、配置编号与击杀者编号
//   DamageDealtPayload（结构体）：一次伤害结算的载荷，带攻守双方编号、实际扣血与剩余血
//   SkillHitPayload（结构体）：技能命中的载荷，带施法者、目标、技能编号与目标配置编号

using NBC.Framework;

namespace NBC.Game.Battle
{
    // 主要功能：声明战斗模块对外广播的五个事件，让死亡、伤害、命中与掉落都能被订阅。
    // 主要数据成员：MonsterDied、HeroDied、DamageDealt、SkillHit、ItemDropped（五个只读 EventId，事件名形如「Battle.MonsterDied」）。
    // 主要外部方法：无（这里只放事件标识，真正的触发在 BattleWorld / ServerEventBridge）。
    // 谁在用它：BattleWorld 与 ServerEventBridge 触发；BattleSession 的条件桥订阅 MonsterDied、SkillHit、ItemDropped；Editor 的 NetDebugWindow 读 MonsterDied / DamageDealt 的监听数并绑定 MonsterDiedPayload。
    // 注意：怪物死与英雄死必须是两个事件而不是一个带 kind 的 —— 任务条件桥若共用事件，会把玩家死亡当成击杀任意目标（编号 0），让击杀任务静默涨进度。
    /// <summary>战斗模块对外广播的事件标识：怪物死、英雄死、伤害、技能命中与我的掉落。</summary>
    public static class BattleEvents
    {
        /// <summary>怪物死亡；载荷是 MonsterDiedPayload，死亡结算后由战斗世界或服务端事件桥发出。</summary>
        public static readonly EventId MonsterDied = EventId.Declare("Battle.MonsterDied");

        /// <summary>英雄死亡；载荷是 HeroDiedPayload，与怪物死亡分开，避免任务把玩家死亡算成击杀。</summary>
        public static readonly EventId HeroDied = EventId.Declare("Battle.HeroDied");

        /// <summary>造成了伤害；每次扣血都发，伤害数字与统计靠它，载荷是 DamageDealtPayload。</summary>
        public static readonly EventId DamageDealt = EventId.Declare("Battle.DamageDealt");

        /// <summary>技能命中目标；在扣血之前发（顺序契约见 BattleWorld.CastSkill），载荷是 SkillHitPayload。</summary>
        public static readonly EventId SkillHit = EventId.Declare("Battle.SkillHit");

        /// <summary>我获得掉落物；只有归属我的掉落才发（判断在 ServerEventBridge），载荷是 ItemDroppedPayload。</summary>
        public static readonly EventId ItemDropped = EventId.Declare("Battle.ItemDropped");
    }

    // 主要功能：把「我拿到了什么物品、几个」打包成一次掉落事件的载荷。
    // 主要数据成员：ItemId（物品编号，Item 表主键，正整数）、Count（数量，个，正常 ≥ 1）。
    // 主要外部方法：构造函数。
    // 谁在用它：ServerEventBridge.OnDrop 确认掉落归我之后造；BattleSession 的条件桥订阅 ItemDropped 后读 ItemId，测试也订阅。
    // 注意：只有归我的掉落才发这个事件（归属判断在 ServerEventBridge）—— 若把别人的掉落也发出来让订阅方自己判，判错会让收集任务凭空涨进度。
    /// <summary>我获得掉落物的载荷（物品编号 + 数量）。</summary>
    public readonly struct ItemDroppedPayload
    {
        /// <summary>物品编号（Item 表主键，正整数；协议字段 item_id）。</summary>
        public readonly int ItemId;

        /// <summary>数量（个；服务端下发的掉落数量，正常 ≥ 1，这里不校验）。</summary>
        public readonly int Count;

        // 功能：把物品编号与数量装成一次掉落事件的载荷。
        // 入参：itemId 物品编号（Item 表主键，正整数）；count 数量（个，正常 ≥ 1）
        // 步骤：
        //   1) 两个值按字段顺序原样写进只读字段，不做校验 —— 掉落数量已由服务端钳到 ≥ 1，这里不替上游兜底
        //   2) 字段只读，载荷造好即固定，订阅方拿到的是值拷贝，不会被后续改动影响
        // 返回：无（构造函数不返回值）
        // 上游：ServerEventBridge.OnDrop（已确认掉落归我）
        // 下游：EventCenter 的 ItemDropped 订阅方（BattleSession 条件桥、测试）
        public ItemDroppedPayload(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }
    }

    // 主要功能：把「哪一只怪死了、谁杀的」打包成一次怪物死亡事件的载荷。
    // 主要数据成员：InstanceId（实例编号，正整数，单机世界从 1 起递增）、MonsterConfigId（配置编号，Monster 表主键，6001 = 野狼）、KillerInstanceId（击杀者实例编号，0 = 无击杀者）。
    // 主要外部方法：构造函数。
    // 谁在用它：BattleWorld.OnAgentKilled 与 ServerEventBridge.OnDeath 造；BattleSession 的条件桥订阅 MonsterDied 后读 MonsterConfigId，Editor 的 NetDebugWindow 也绑定它，测试订阅后核对编号。
    // 注意：InstanceId 与 MonsterConfigId 不能合并 —— 同一配置刷两只怪时，合并会让 debuff 与死亡摘除作用到错误的那一只。
    /// <summary>怪物死亡的载荷（实例编号 + 配置编号 + 击杀者实例编号）。</summary>
    public readonly struct MonsterDiedPayload
    {
        /// <summary>这一只怪的实例编号（正整数；单机世界由 BattleWorld 从 1 起发号，对具体这一只做操作或从世界摘掉它时用）。</summary>
        public readonly int InstanceId;

        /// <summary>这一只怪的配置编号（Monster 表主键，正整数；6001 = 野狼，任务条件、掉落与 AI 用）。</summary>
        public readonly int MonsterConfigId;

        /// <summary>击杀者的实例编号（正整数；0 = 没有击杀者，例如环境伤害）。</summary>
        public readonly int KillerInstanceId;

        // 功能：把怪物、配置与击杀者三个编号装成一次怪物死亡事件的载荷。
        // 入参：instanceId 怪物实例编号（正整数）；monsterConfigId 怪物配置编号（Monster 表主键，正整数）；killerInstanceId 击杀者实例编号（0 = 无击杀者）
        // 步骤：
        //   1) 三个编号按字段顺序原样写入，不做校验 —— 0 对 killerInstanceId 是合法值（环境伤害），不能当非法值拦掉
        //   2) 字段只读，载荷造好即固定，订阅方拿到的是值拷贝，不会被后续改动影响
        // 返回：无（构造函数不返回值）
        // 上游：BattleWorld.OnAgentKilled（单机结算）、ServerEventBridge.OnDeath（联机转发）
        // 下游：EventCenter 的 MonsterDied 订阅方（BattleSession 条件桥、NetDebugWindow、测试）
        public MonsterDiedPayload(int instanceId, int monsterConfigId, int killerInstanceId)
        {
            InstanceId = instanceId;
            MonsterConfigId = monsterConfigId;
            KillerInstanceId = killerInstanceId;
        }
    }

    // 主要功能：把「哪一个英雄死了、谁杀的」打包成一次英雄死亡事件的载荷。
    // 主要数据成员：InstanceId（实例编号，正整数，单机世界从 1 起递增）、HeroConfigId（配置编号，Hero 表主键，正整数）、KillerInstanceId（击杀者实例编号，0 = 无击杀者）。
    // 主要外部方法：构造函数。
    // 谁在用它：BattleWorld.OnAgentKilled 与 ServerEventBridge.OnDeath 造；测试订阅核对（任务条件桥刻意只订阅 MonsterDied，英雄死亡不会推进击杀任务）。
    // 注意：这个事件不能并进 MonsterDied —— 任务条件桥不认识 kind，合并后英雄死亡会被当成击杀了一个目标。
    /// <summary>英雄死亡的载荷（实例编号 + 配置编号 + 击杀者实例编号）。</summary>
    public readonly struct HeroDiedPayload
    {
        /// <summary>这个英雄的实例编号（正整数；单机世界由 BattleWorld 从 1 起发号）。</summary>
        public readonly int InstanceId;

        /// <summary>英雄配置编号（Hero 表主键，正整数）。</summary>
        public readonly int HeroConfigId;

        /// <summary>击杀者的实例编号（正整数；0 = 没有击杀者）。</summary>
        public readonly int KillerInstanceId;

        // 功能：把英雄、配置与击杀者三个编号装成一次英雄死亡事件的载荷。
        // 入参：instanceId 英雄实例编号（正整数）；heroConfigId 英雄配置编号（Hero 表主键，正整数）；killerInstanceId 击杀者实例编号（0 = 无击杀者）
        // 步骤：
        //   1) 三个编号按字段顺序原样写入，不做校验 —— 0 对 killerInstanceId 是合法值，不能当非法值拦掉
        //   2) 字段只读，载荷造好即固定，订阅方拿到的是值拷贝，不会被后续改动影响
        // 返回：无（构造函数不返回值）
        // 上游：BattleWorld.OnAgentKilled（单机结算）、ServerEventBridge.OnDeath（联机转发）
        // 下游：EventCenter 的 HeroDied 订阅方（当前主要是测试；任务条件桥只订阅 MonsterDied）
        public HeroDiedPayload(int instanceId, int heroConfigId, int killerInstanceId)
        {
            InstanceId = instanceId;
            HeroConfigId = heroConfigId;
            KillerInstanceId = killerInstanceId;
        }
    }

    // 主要功能：把一次伤害结算的结果打包成载荷，供飘字、统计与测试读取。
    // 主要数据成员：AttackerInstanceId（攻击者实例编号，0 = 无攻击者）、TargetInstanceId（受击者实例编号，正整数）、Applied（实际扣血，点，0 到结算前的 HP）、RemainingHp（结算后剩余血，点，≥ 0）。
    // 主要外部方法：构造函数。
    // 谁在用它：BattleWorld.ApplyDamage 与 ServerEventBridge.OnDamage 造；测试订阅核对字段，伤害飘字与统计按设计从这里取数。
    // 注意：每次扣血都发，包括致死那一击；致死之后还会再发 MonsterDied 或 HeroDied，订阅方不能假设这是最后一条伤害事件。
    /// <summary>一次伤害结算的载荷（攻守双方、实际扣血、剩余血）。</summary>
    public readonly struct DamageDealtPayload
    {
        /// <summary>攻击者的实例编号（正整数；0 = 没有攻击者，例如环境伤害）。</summary>
        public readonly int AttackerInstanceId;

        /// <summary>受击者的实例编号（正整数，结算时必须在场）。</summary>
        public readonly int TargetInstanceId;

        /// <summary>实际扣掉的血（点，0 到结算前的 HP；过量伤害不计入这里）。</summary>
        public readonly int Applied;

        /// <summary>结算后的剩余血（点，≥ 0）。</summary>
        public readonly int RemainingHp;

        // 功能：把攻守双方、实际扣血与剩余血装成一次伤害事件的载荷。
        // 入参：attackerInstanceId 攻击者实例编号（0 = 无攻击者）；targetInstanceId 受击者实例编号（正整数）；applied 实际扣血（点，0 到结算前的 HP）；remainingHp 剩余血（点，≥ 0）
        // 步骤：
        //   1) 四个值按字段顺序原样写进只读字段，不做校验 —— 钳位与过量伤害已由伤害结算层算好，这里只搬运
        //   2) 字段只读，载荷造好即固定，订阅方拿到的是值拷贝，不会被后续改动影响
        // 返回：无（构造函数不返回值）
        // 上游：BattleWorld.ApplyDamage（单机结算）、ServerEventBridge.OnDamage（联机转发）
        // 下游：EventCenter 的 DamageDealt 订阅方（当前测试；伤害飘字与统计的设计入口）
        public DamageDealtPayload(int attackerInstanceId, int targetInstanceId, int applied, int remainingHp)
        {
            AttackerInstanceId = attackerInstanceId;
            TargetInstanceId = targetInstanceId;
            Applied = applied;
            RemainingHp = remainingHp;
        }
    }

    // 主要功能：把「谁对谁放了哪个技能」打包成一次技能命中事件的载荷。
    // 主要数据成员：CasterInstanceId（施法者实例编号，正整数）、TargetInstanceId（目标实例编号，正整数）、SkillId（技能编号，Skill 表主键，正整数）、TargetConfigId（目标配置编号，正整数）。
    // 主要外部方法：构造函数。
    // 谁在用它：BattleWorld.CastSkill 在扣血之前造；BattleSession 的条件桥订阅 SkillHit 后读 SkillId，测试也订阅。
    // 注意：本事件必须先于 DamageDealt 发 —— 否则致死那一击会先发死亡事件，任务侧会看到「先死再命中」的倒序。
    /// <summary>技能命中的载荷（施法者、目标、技能编号、目标配置编号）。</summary>
    public readonly struct SkillHitPayload
    {
        /// <summary>施法者的实例编号（正整数）。</summary>
        public readonly int CasterInstanceId;

        /// <summary>目标的实例编号（正整数，结算时必须在场）。</summary>
        public readonly int TargetInstanceId;

        /// <summary>技能编号（Skill 表主键，正整数）。</summary>
        public readonly int SkillId;

        /// <summary>目标的配置编号（Monster 或 Hero 表主键，正整数；任务条件判断命中的是谁时用它，不用实例编号）。</summary>
        public readonly int TargetConfigId;

        // 功能：把施法者、目标、技能与目标配置四个编号装成一次技能命中事件的载荷。
        // 入参：casterInstanceId 施法者实例编号（正整数）；targetInstanceId 目标实例编号（正整数）；skillId 技能编号（Skill 表主键，正整数）；targetConfigId 目标配置编号（Monster 或 Hero 表主键，正整数）
        // 步骤：
        //   1) 四个编号按字段顺序原样写入，不做校验 —— 技能归属与目标在场已由 BattleWorld 在发事件前挡过
        //   2) 字段只读，载荷造好即固定，订阅方拿到的是值拷贝，不会被后续改动影响
        // 返回：无（构造函数不返回值）
        // 上游：BattleWorld.CastSkill（扣血之前，顺序契约的第一段）
        // 下游：EventCenter 的 SkillHit 订阅方（BattleSession 条件桥、测试）
        public SkillHitPayload(int casterInstanceId, int targetInstanceId, int skillId, int targetConfigId)
        {
            CasterInstanceId = casterInstanceId;
            TargetInstanceId = targetInstanceId;
            SkillId = skillId;
            TargetConfigId = targetConfigId;
        }
    }
}
