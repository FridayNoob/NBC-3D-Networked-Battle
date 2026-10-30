// 这个文件里有：
//   SkillCastOutcome（结构体）：一次技能释放的结果快照，只读，结算完就固定
//   BattleWorld（类）：单机战斗世界，刷怪、结算伤害与技能、广播结果，是这一层唯一的权威

using System;
using System.Collections.Generic;
using System.Text;
using NBC.Framework;
using NBC.Game.Config;
using NBC.Shared.Battle;

namespace NBC.Game.Battle
{
    // 主要功能：一次技能释放的结果快照（技能编号、实际扣血、剩余血、是否击杀），构造后只读。
    // 主要数据成员：SkillId（Skill 表主键）、AppliedDamage（点，≥ 0）、RemainingHp（点，≥ 0）、Killed（这一下是否打死目标）。
    // 主要外部方法：无（只读字段加一个 ToString；构造是 internal，只有 BattleWorld 造得出来）。
    // 谁在用它：BattleWorld.CastSkill 造；SkillCaster 与 BattleSession 原样透给调用方；EditMode 的 BattleWorldTests / SkillCasterTests 断言字段。
    // 注意：它是快照不是引用 —— RemainingHp 是那一下结算后的血量，世界之后再变，它也不会跟着变，要最新血量得重新问世界。
    public readonly struct SkillCastOutcome
    {
        // Skill 表主键，> 0，能在技能表里查到
        public readonly int SkillId;

        // 这一下实际扣掉的血，点，≥ 0（在 DamageMath 里已钳到不超过目标剩余血）
        public readonly int AppliedDamage;

        // 结算后目标的剩余血，点，≥ 0；0 表示这一下打死了、目标已从世界摘掉
        public readonly int RemainingHp;

        // 这一下是否致死；true 时世界已经摘掉目标并广播过死亡事件
        public readonly bool Killed;

        // 功能：把一次结算的四个结果抄成只读快照。
        // 入参：skillId 技能编号；appliedDamage 实际扣血（点，≥ 0）；remainingHp 剩余血（点，≥ 0）；killed 是否打死
        // 返回：无
        // 步骤：
        //   1) 四个字段按参数原样赋值，不校验也不钳制 —— 数值在 DamageMath 那一层已经算好、钳好
        // 上游：BattleWorld.CastSkill
        // 下游：无
        // 注意：构造刻意是 internal：结果只能由世界结算产生，别处不该凭空拼一个出来。
        internal SkillCastOutcome(int skillId, int appliedDamage, int remainingHp, bool killed)
        {
            SkillId = skillId;
            AppliedDamage = appliedDamage;
            RemainingHp = remainingHp;
            Killed = killed;
        }

        // 功能：转成一句人话（「技能 2003 命中：-12（剩 0），击杀」），给调试、日志与测试断言看。
        // 入参：无
        // 返回：描述这一下的单行文本；Killed 为 true 时末尾多一句「，击杀」
        // 步骤：
        //   1) 先拼「技能编号 / 扣血 / 剩余血」，致死与否只决定末尾补不补那一句
        // 上游：BattleWorld.Describe（逐单位拼文本）、调试面板、EditMode 用例
        // 下游：无
        public override string ToString()
        {
            return "技能 " + SkillId + " 命中：-" + AppliedDamage + "（剩 " + RemainingHp + "）" +
                   (Killed ? "，击杀" : string.Empty);
        }
    }

    // 主要功能：单机战斗世界 —— 按配置刷单位、把一次伤害或技能结算掉、再把结果广播出去；谁在场、谁被打死、技能怎么结算都只有这一份实现。
    // 主要数据成员：m_monsters / m_heroes / m_skills（三张只读配置表）、m_agents（实例编号 → 在场单位）、m_nextInstanceId（下一个实例编号，从 1 起只增不减）。
    // 主要外部方法：SpawnMonster / SpawnHero / TryGetAgent / CopyAliveMonsters / TryFindFirstMonster / HasSkill / CasterOwnsSkill / ApplyDamage / CastSkill / Clear / Describe，以及 AgentCount / AliveMonsterCount 只读属性。
    // 谁在用它：BattleSession 造一局并转发刷怪与施法；SkillCaster 装配期查归属、结算期放技能；Boot 的 M2DemoBehaviour 读 AliveMonsterCount、重跑演示前调 Clear；EditMode 的 BattleWorldTests / SkillCasterTests 用假表直接驱动。
    // 注意：它不认识任务、不认识 UI、不认识输入 —— 只做「按配置刷一个单位」和「把一次伤害 / 技能结算掉」两件事；任务侧靠 BattleSession 的条件桥订阅 MonsterDied，所以战斗模块完全不知道任务存在。
    // 注意：两类失败分得很清 —— 配置或程序错误（表里没这行、目标不在场）一律抛异常，这类错不可能是玩家操作出来的，越早炸越好；正常的玩法竞态才由返回值说明（M2 还没有这类，M3 有了范围 / 闪避之后「没打中」就属于这一类）。
    // 注意：在场 ⇒ 活着是一条不变量，单位死的那一刻就被摘掉，所以「在场但 0 血」不存在，这里刻意没有「目标已经死了」的分支：一个永远走不到的分支比没有分支更糟，读代码的人会以为存在那种状态，再照着它写别的判断；这条不变量有用例 World_NeverHoldsDeadAgent 钉住。尸体被打由 DamageMath 那一层防（currentHp 为 0 时不判致死），因为血量状态在它手上。
    public sealed class BattleWorld
    {
        // 怪物表（Monster）：配置主键 → 行，刷怪时查；构造时非 null
        private readonly MonsterConfig m_monsters;

        // 英雄表（Hero）：配置主键 → 行，刷英雄时查
        private readonly HeroConfig m_heroes;

        // 技能表（Skill）：配置主键 → 行，技能伤害与归属校验都从它取
        private readonly SkillConfig m_skills;

        // 在场单位：实例编号（> 0）→ 单位；死的那一刻就摘掉，所以这里没有 0 血单位
        private readonly Dictionary<int, BattleAgent> m_agents = new Dictionary<int, BattleAgent>();

        // 下一个要发的实例编号，从 1 起、只增不减（死掉的号不复用，免得旧编号被新单位顶替）
        private int m_nextInstanceId = 1;

        // 功能：存下三张配置表，造一个空世界（0 个单位）。
        // 入参：monsters 怪物表；heroes 英雄表；skills 技能表 —— 三者都不能为 null
        // 返回：无（构造函数；三张表任一为 null 就抛，对象不会建出来）
        // 步骤：
        //   1) 先逐个判 null 并抛 ArgumentNullException：报错要点名是哪张表缺了，别拖到刷怪时才发现
        //   2) 再赋给三个只读字段；在场表从空字典起步，实例编号从 1 起
        // 上游：BattleSession 的构造函数（整局只有这一处）；EditMode 用例用假表直接 new
        // 下游：无
        // 注意：这里不校验表的内容（有没有某个编号、数值合不合法都在用到时查）—— 表是外部导入的，构造函数只负责装配不负责审表。
        public BattleWorld(MonsterConfig monsters, HeroConfig heroes, SkillConfig skills)
        {
            if (monsters == null) { throw new ArgumentNullException(nameof(monsters), "[BattleWorld] 怪物表是 null。"); }
            if (heroes == null) { throw new ArgumentNullException(nameof(heroes), "[BattleWorld] 英雄表是 null。"); }
            if (skills == null) { throw new ArgumentNullException(nameof(skills), "[BattleWorld] 技能表是 null。"); }

            m_monsters = monsters;
            m_heroes = heroes;
            m_skills = skills;
        }

        // 功能：读在场单位数（英雄 + 怪，不含尸体 —— 死的那一刻就摘掉了）。
        public int AgentCount
        {
            get { return m_agents.Count; }
        }

        // 功能：数一遍在场单位里有几只是活着的怪，任务与关卡进度看它。
        // 入参：无（只读属性）
        // 返回：活怪只数，0..AgentCount；英雄与尸体都不计入
        // 步骤：
        //   1) 遍历内部字典，同时要求种类是 Monster 且 IsAlive —— 两个条件缺一不可，只看种类会把尸体算进去
        //   2) 满足就 +1，遍历完返回
        // 上游：Boot 的 M2DemoBehaviour 在 OnGUI 里显示「场上活怪」；BattleSession.Describe
        // 下游：BattleAgent.Kind / IsAlive
        // 注意：每次读都现数一遍，O(单位数)；只给演示与调试用，别在每帧的战斗循环里反复调。
        /// <summary>当前还活着的怪物只数（英雄不计）；每次读取都遍历一遍在场表，供演示与调试显示。</summary>
        public int AliveMonsterCount
        {
            get
            {
                int count = 0;

                foreach (KeyValuePair<int, BattleAgent> pair in m_agents)
                {
                    if (pair.Value.Kind == EBattleAgentKind.Monster && pair.Value.IsAlive)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        // 功能：按怪物表刷一只新怪进世界，并把它返回。
        // 入参：monsterId 怪物表（Monster）主键，不是实例编号
        // 返回：新单位，已经登记进世界，InstanceId 是新发的
        // 步骤：
        //   1) 先查表：查不到直接抛 —— 刷怪是关卡流程驱动的，静默刷不出来会让「这一关怎么打不完」变成悬案
        //   2) 发一个新实例编号，按这一行配置造单位
        //   3) 先登记进在场表再返回，保证调用方拿到的单位一定在场
        // 上游：BattleSession.EnterLevel（进关卡时按数量刷）
        // 下游：NextInstanceId、BattleAgent.FromMonster
        // 注意：每次调用都是一个新单位（新的实例编号）——「同一个 6001 刷三只」是三个不同对象，给其中一只上状态不会影响另外两只。
        // 注意：查不到就抛、不刷一个空怪出来；报错里点名要确认 ConfigKit 生成过，并点过 Tools/NBC/配置表/导入 TSV 菜单。
        public BattleAgent SpawnMonster(int monsterId)
        {
            Config_Monster row;

            if (!m_monsters.TryGet(monsterId, out row))
            {
                throw new InvalidOperationException(
                    "[BattleWorld] 怪物表 `Monster` 里没有编号 " + monsterId + "。\n" +
                    "刷怪是关卡流程驱动的，所以这里**直接报错**而不是刷一个空怪出来 —— " +
                    "静默刷不出来会让\"这一关怎么打不完\"变成悬案。\n" +
                    "先确认 ConfigKit 生成过、并且点过 `Tools/NBC/配置表/导入 TSV` 菜单。");
            }

            BattleAgent agent = BattleAgent.FromMonster(NextInstanceId(), row);
            m_agents.Add(agent.InstanceId, agent);
            return agent;
        }

        // 功能：按英雄表刷一个英雄单位（M2 里这就是「玩家」）。
        // 入参：heroId 英雄表（Hero）主键
        // 返回：新单位，已经登记进世界
        // 步骤：
        //   1) 先查英雄表，查不到抛 InvalidOperationException（配置错误不猜）
        //   2) 发新实例编号造单位，登记后返回
        // 上游：BattleSession 的构造函数（一局只刷一个）
        // 下游：NextInstanceId、BattleAgent.FromHero
        // 注意：英雄与怪共用同一个发号器和同一张在场表，实例编号在全场唯一 —— 别按「英雄的编号是 1」去写逻辑。
        public BattleAgent SpawnHero(int heroId)
        {
            Config_Hero row;

            if (!m_heroes.TryGet(heroId, out row))
            {
                throw new InvalidOperationException(
                    "[BattleWorld] 英雄表 `Hero` 里没有编号 " + heroId + "。");
            }

            BattleAgent agent = BattleAgent.FromHero(NextInstanceId(), row);
            m_agents.Add(agent.InstanceId, agent);
            return agent;
        }

        // 功能：按实例编号取单位，取不到不抛。
        // 入参：instanceId 实例编号（0 一定取不到）
        // 返回：在场为 true 并带出单位；不在场为 false，agent 为 null（这不算错误）
        // 上游：BattleSession 的目标合法性复查、SkillCaster 装配期校验；EditMode 用例
        // 下游：Dictionary.TryGetValue
        public bool TryGetAgent(int instanceId, out BattleAgent agent)
        {
            return m_agents.TryGetValue(instanceId, out agent);
        }

        // 功能：把还活着的怪复制进调用方给的列表，供目标选择、AI、UI 遍历。
        // 入参：buffer 目标列表（会先 Clear；为 null 抛 ArgumentNullException）
        // 返回：无（结果写在 buffer 里；顺序按字典遍历，不保证稳定）
        // 步骤：
        //   1) 先判 null 并抛：给 null 不该静默变成「场上没怪」
        //   2) 先 Clear 再填 —— 「复制进来」的语义是不留调用方上一次的内容
        //   3) 逐个筛种类是 Monster 且活着，只把引用放进列表，不复制单位本身
        // 上游：BattleSession.SelectFirstAliveMonster（选目标）
        // 下游：无
        // 注意：刻意不把内部字典交出去（把容器交出去等于外部也能改它）；形状与 QuestRuntime.CopyActiveQuestIds 一致，列表由调用方复用，所以每次调用不产生新的列表分配。
        public void CopyAliveMonsters(List<BattleAgent> buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            buffer.Clear();

            foreach (KeyValuePair<int, BattleAgent> pair in m_agents)
            {
                if (pair.Value.Kind == EBattleAgentKind.Monster && pair.Value.IsAlive)
                {
                    buffer.Add(pair.Value);
                }
            }
        }

        // 功能：按配置编号找第一只还在场的怪，演示与关卡脚本用。
        // 入参：monsterId 怪物配置编号（不是实例编号）
        // 返回：找到为 true 并带出那一只；一只都没有为 false，agent 为 null
        // 步骤：
        //   1) 遍历在场表，第一只种类是 Monster 且配置编号相等的就返回
        //   2) 扫完没有就置 null 返回 false
        // 上游：EditMode 用例；关卡脚本按配置编号找目标时
        // 下游：无
        // 注意：找的是「还在场」的怪，同配置的第二只不会返回；要全部请用 CopyAliveMonsters 自己筛。
        public bool TryFindFirstMonster(int monsterId, out BattleAgent agent)
        {
            foreach (KeyValuePair<int, BattleAgent> pair in m_agents)
            {
                if (pair.Value.Kind == EBattleAgentKind.Monster && pair.Value.ConfigId == monsterId)
                {
                    agent = pair.Value;
                    return true;
                }
            }

            agent = null;
            return false;
        }

        // 功能：查技能表里有没有这个编号。
        // 入参：skillId 技能编号（Skill 表主键）
        // 返回：表里有就 true，没有就 false（不抛）
        // 上游：SkillCaster 在装配期先问一遍，别等玩家按下按钮才发现绑错了编号
        // 下游：SkillConfig.TryGet
        public bool HasSkill(int skillId)
        {
            Config_Skill row;
            return m_skills.TryGet(skillId, out row);
        }

        // 功能：问一个在场单位会不会某个技能（技能归属来自配置里的 Hero.skillIds / Monster.skillIds）。
        // 入参：instanceId 单位实例编号（必须在场，不在场抛）；skillId 技能编号
        // 返回：会就 true，不会就 false
        // 步骤：
        //   1) 先用 RequireAgent 取单位：不在场就没有归属可谈，当场抛
        //   2) 再转给 OwnsSkill 逐个比对编号
        // 上游：SkillCaster 装配期（绑动作时）早检查一次
        // 下游：RequireAgent、OwnsSkill
        // 注意：它公开是有意的 —— 归属规则只该有一份实现、却有两个调用点（装配期早检查 + 结算期兜底），这不叫两套规则，叫 fail early + fail loud。
        public bool CasterOwnsSkill(int instanceId, int skillId)
        {
            return OwnsSkill(RequireAgent(instanceId), skillId);
        }

        // 功能：在单位的技能列表里线性找技能编号。
        // 入参：agent 单位（非 null）；skillId 技能编号
        // 返回：列表里有就 true
        // 步骤：
        //   1) 先取出 SkillIds（构造时已归一成非 null 数组，这里不用判空）
        //   2) 逐个比较，命中立即返回 true；扫完返回 false
        // 上游：CasterOwnsSkill、CastSkill
        // 下游：BattleAgent.SkillIds
        private static bool OwnsSkill(BattleAgent agent, int skillId)
        {
            int[] owned = agent.SkillIds;

            for (int i = 0; i < owned.Length; i++)
            {
                if (owned[i] == skillId)
                {
                    return true;
                }
            }

            return false;
        }

        // 功能：把一次伤害结算到目标身上，广播 DamageDealt，并在致死时走死亡流程。
        // 入参：attackerInstanceId 攻击者实例编号（0 表示没有攻击者 / 环境伤害）；targetInstanceId 目标实例编号（必须在场）；damage 伤害值（点，≥ 0）
        // 返回：这一次的结算结果（实际扣血、剩余血、是否致死）
        // 步骤：
        //   1) 先 RequireAgent 取目标：不在场是程序错误，直接抛
        //   2) 交给 BattleAgent.ApplyDamage 算（钳位与致死判定都在 DamageMath）
        //   3) 先广播 DamageDealt 再处理死亡：事件顺序要「发生了什么 → 细节是多少 → 结果是什么」
        //   4) 只有致死才走 OnAgentKilled（摘掉 + 广播死亡）
        // 上游：CastSkill（技能伤害）；调试面板与 EditMode 用例直接改血时也调
        // 下游：RequireAgent、BattleAgent.ApplyDamage、EventCenter.Trigger、OnAgentKilled
        // 注意：目标必须在场且活着（不变量：在场 ⇒ 活着），所以这里没有「目标已经死了」的分支 —— 那个分支永远走不到，留着只会让读代码的人以为存在那种状态。
        // 注意：「尸体被打」由 DamageMath 那一层防（currentHp 为 0 时明确不判致死，见 Shared\Battle\DamageMath.cs 规则③，用例 DamageMathTests 钉住），因为持有血量状态的是它。
        public DamageOutcome ApplyDamage(int attackerInstanceId, int targetInstanceId, int damage)
        {
            BattleAgent target = RequireAgent(targetInstanceId);

            DamageOutcome outcome = target.ApplyDamage(damage);

            EventCenter.Instance.Trigger(BattleEvents.DamageDealt,
                new DamageDealtPayload(attackerInstanceId, targetInstanceId, outcome.Applied, outcome.RemainingHp));

            if (outcome.IsLethal)
            {
                OnAgentKilled(target, attackerInstanceId);
            }

            return outcome;
        }

        // 功能：释放一次技能（M2 的最小形态：单体、必中、立即结算），按契约顺序广播事件并返回结果。
        // 入参：casterInstanceId 施法者实例编号（必须在场、且会这个技能）；skillId 技能编号（必须在技能表里）；targetInstanceId 目标实例编号（必须在场）
        // 返回：这次释放的结果（技能编号、实际扣血、剩余血、是否击杀）
        // 步骤：
        //   1) 先取施法者再取目标：任一不在场就抛，别拿着半个上下文去查表
        //   2) 查技能表，没有就抛（配置错误不猜伤害）
        //   3) 校验归属：只能放自己技能列表里的技能。归属是配置驱动的，绑定动作的人（输入层、AI）很容易绑错编号；不挡的话，按 J 放出法师的穿心箭会静默生效，表现成「这个技能怎么伤害不对」而不是任何一条报错
        //   4) 先广播 SkillHit（顺序契约第一段：任务条件「用技能命中 N 次」靠它）
        //   5) 再走 ApplyDamage 扣血（它内部广播 DamageDealt，致死时再广播死亡事件）
        // 上游：SkillCaster.Handle（按键 → 技能编号）；BattleSession.HandleInput；EditMode 用例
        // 下游：RequireAgent、SkillConfig.TryGet、OwnsSkill、EventCenter.Trigger、ApplyDamage、SkillCastOutcome 的构造
        // 注意：事件顺序是契约不是实现细节：SkillHit → DamageDealt →（致死时）MonsterDied / HeroDied，即「发生了什么 → 细节是多少 → 结果是什么」；用例 CastSkill_EventOrderIsSkillHitThenDamageThenDeath 钉住它。
        // 注意：顺序写死的原因是条件系统按事件累加进度 —— 击杀先于命中发出，一个「先命中 3 次、再击杀 1 只」的任务会在同一只怪上先完成击杀条件，而玩家看到的是「我先杀死了它」，表现为任务进度偶尔乱跳。
        // 注意：M2 里「没打中」不存在（必中）；范围判定、闪避、命中率是 M3 的事。
        public SkillCastOutcome CastSkill(int casterInstanceId, int skillId, int targetInstanceId)
        {
            BattleAgent caster = RequireAgent(casterInstanceId);

            BattleAgent target = RequireAgent(targetInstanceId);

            Config_Skill skill;

            if (!m_skills.TryGet(skillId, out skill))
            {
                throw new InvalidOperationException(
                    "[BattleWorld] 技能表 `Skill` 里没有编号 " + skillId + "。");
            }

            if (!OwnsSkill(caster, skillId))
            {
                throw new InvalidOperationException(
                    "[BattleWorld] " + caster.Name + "（实例 #" + casterInstanceId + "，配置 " +
                    caster.ConfigId + "）的技能列表里没有技能 " + skillId + "。\n" +
                    "技能归属来自配置（`Hero.skillIds` / `Monster.skillIds`），" +
                    "所以这多半是**绑定动作时写错了编号**，或者表里少填了一个技能。");
            }

            EventCenter.Instance.Trigger(BattleEvents.SkillHit,
                new SkillHitPayload(casterInstanceId, targetInstanceId, skillId, target.ConfigId));

            DamageOutcome outcome = ApplyDamage(casterInstanceId, targetInstanceId, skill.damage);

            return new SkillCastOutcome(skillId, outcome.Applied, outcome.RemainingHp, outcome.IsLethal);
        }

        // 功能：把在场单位全部清掉（换关卡、重跑演示用），配置表不动。
        // 入参：无
        // 返回：无
        // 上游：Boot 的 M2DemoBehaviour 重置演示时
        // 下游：无
        // 注意：不动实例编号计数器 —— 清空后编号继续往下发，旧编号不会被新单位复用，也不会让别人手里的旧引用突然对上另一个单位。
        /// <summary>清空在场单位（换关卡或重跑演示用）；配置表与发号器都不动，实例编号继续往下发。</summary>
        public void Clear()
        {
            m_agents.Clear();
        }

        // 功能：把在场单位拼成一段多行文本，给调试面板与控制台看。
        // 入参：无
        // 返回：场上是空的时候只给一句话；否则首行报总数，之后每个单位一行
        // 步骤：
        //   1) 空世界先短路：省掉一次拼接，也给出一句明确的话
        //   2) 首行写总数，再用各单位的 ToString 逐行拼
        // 上游：BattleSession.Describe（调试输出）；EditMode 用例断言文本
        // 下游：BattleAgent.ToString
        public string Describe()
        {
            if (m_agents.Count == 0)
            {
                return "[BattleWorld] 场上是空的。";
            }

            StringBuilder builder = new StringBuilder();
            builder.Append("[BattleWorld] 在场 ").Append(m_agents.Count).Append(" 个单位：\n");

            foreach (KeyValuePair<int, BattleAgent> pair in m_agents)
            {
                builder.Append("  · ").Append(pair.Value.ToString()).Append('\n');
            }

            return builder.ToString();
        }

        // 功能：取一个必须在场的单位，不在场就抛 —— 这是本类的不变量守卫。
        // 入参：instanceId 实例编号
        // 返回：在场的那只单位
        // 步骤：
        //   1) 在在场表里查，查到直接返回
        //   2) 查不到就抛，报错里点出两个常见原因：已经死了被摘掉、或把配置编号当实例编号用
        // 上游：CasterOwnsSkill、ApplyDamage、CastSkill
        // 下游：无
        // 注意：这里刻意不返回 null —— 缺了这个单位，后面每一步都没有意义，早炸比带着 null 往下走省事。
        private BattleAgent RequireAgent(int instanceId)
        {
            BattleAgent agent;

            if (!m_agents.TryGetValue(instanceId, out agent))
            {
                throw new InvalidOperationException(
                    "[BattleWorld] 场上没有实例编号 " + instanceId + " 的单位。\n" +
                    "两个常见原因：\n" +
                    "  ① 它已经死了 —— 死的那一刻单位就被**从世界里摘掉**了\n" +
                    "     （所以\"打尸体\"在世界这一层根本走不到；血量那一层由 DamageMath 防）\n" +
                    "  ② 实例编号传错了 —— 注意别把**配置编号**（6001）当实例编号用");
            }

            return agent;
        }

        // 功能：单位被打死之后从世界摘掉，再按种类广播 MonsterDied 或 HeroDied。
        // 入参：agent 死掉的单位；killerInstanceId 击杀者实例编号（0 表示没有击杀者）
        // 返回：无
        // 步骤：
        //   1) 先摘掉再广播：回调里如果去查这个单位（例如问「还剩几只怪」），得到的应该是「已经不在场」
        //   2) 按种类分流：怪发 MonsterDied（任务击杀条件靠它），英雄发 HeroDied
        // 上游：ApplyDamage（只在致死时调）
        // 下游：EventCenter.Trigger
        // 注意：顺序反过来，回调会看到一个 0 血的单位还杵在场上 —— 那不符合直觉。
        // 注意：任务条件桥只订阅 MonsterDied，英雄死亡不会推进击杀任务。
        private void OnAgentKilled(BattleAgent agent, int killerInstanceId)
        {
            m_agents.Remove(agent.InstanceId);

            if (agent.Kind == EBattleAgentKind.Monster)
            {
                EventCenter.Instance.Trigger(BattleEvents.MonsterDied,
                    new MonsterDiedPayload(agent.InstanceId, agent.ConfigId, killerInstanceId));
                return;
            }

            EventCenter.Instance.Trigger(BattleEvents.HeroDied,
                new HeroDiedPayload(agent.InstanceId, agent.ConfigId, killerInstanceId));
        }

        // 功能：发一个新的实例编号并让计数器加一。
        // 入参：无
        // 返回：本次的编号，从 1 起、每次调用递增，永不复用
        // 上游：SpawnMonster、SpawnHero
        // 下游：无
        private int NextInstanceId()
        {
            return m_nextInstanceId++;
        }
    }
}
