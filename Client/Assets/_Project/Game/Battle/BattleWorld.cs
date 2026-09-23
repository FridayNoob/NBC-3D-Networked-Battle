// ============================================================================
//  BattleWorld —— 单机战斗世界：刷怪、结算伤害、广播结果
//  项目：3D联网战斗Demo   对应：M2-B1（单机 PVE 最小战斗）
//
//  ---------------------------------------------------------------------------
//  它是这一层的"唯一的权威"
//  ---------------------------------------------------------------------------
//      · 谁在场（单位注册表）        · 谁被打死了（从世界摘掉 + 广播）
//      · 技能怎么结算（查表 → 命中 → 扣血）
//
//  ⚠️ 它**不认识任务、不认识 UI、不认识输入**。它只做两件事：
//     "按配置刷一个单位出来" 和 "把一次伤害/技能结算掉"，然后把结果**广播**出去。
//     任务系统靠 M2-C 的那座桥挂在 `BattleEvents.MonsterDied` 上 ——
//     于是"打怪推进任务"这条链上，**战斗模块完全不知道任务存在**。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 事件顺序是**契约**，不是实现细节（M2-B2 的用例会钉住它）
//  ---------------------------------------------------------------------------
//      CastSkill 的顺序：  SkillHit  →  DamageDealt  →  （致死时）AgentDied
//      即"**发生了什么 → 细节是多少 → 结果是什么**"。
//
//      为什么写死：条件系统按事件累加进度。如果"击杀"事件先于"命中"事件发出，
//      那么一个"先命中 3 次、再击杀 1 只"的任务，在**同一只怪**上就会
//      先完成击杀条件、再完成命中条件 —— 而玩家看到的是"我先杀死了它"。
//      事件顺序不写死，这类问题会以"任务进度偶尔乱跳"的形式出现。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 一条**不变量**：在场 ⇒ 活着（因此这里没有"目标已经死了"的分支）
//  ---------------------------------------------------------------------------
//  第一版我在 `ApplyDamage` / `CastSkill` 里都写了"目标已经死了 -> 什么都不做"的分支，
//  写完才发现**那个分支永远走不到**：单位死的那一刻就被 `OnAgentKilled` 从世界里摘掉了，
//  所以"在场但 0 血"这个状态**不存在**。
//
//  于是把它删掉，并把这条变成一条**不变量**（有用例 `World_NeverHoldsDeadAgent` 钉住）。
//  为什么删而不是留着"以防万一"：
//      **一个永远走不到的分支比没有分支更糟** —— 读代码的人会以为存在那种状态，
//      于是照着它写下别的逻辑（比如"记得判断 IsAlive"），最后大家一起维护一个幻觉。
//
//  ⚠️ 那"尸体被打"这件事由谁防？**由 `DamageMath` 那一层防**
//     （`currentHp == 0` 时明确不判致死），因为它才是持有"血量"这个状态的地方。
//     见 `Shared\Battle\DamageMath.cs` 规则③ 与 `DamageMathTests`。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 两种失败的处理方式不同（沿用 M2-A 的分工）
//  ---------------------------------------------------------------------------
//      · **配置/程序错误**（怪物表里没这行、技能表里没这行、目标不在场）→ **抛异常**
//        这些不可能是玩家操作出来的，越早炸越好
//      · **正常的玩法竞态** → 由**返回值**说明（M2 的最小形态里暂时没有这类情况；
//        M3 有了范围/闪避之后，"没打中"就是这一类）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using NBC.Framework;
using NBC.Game.Config;
using NBC.Shared.Battle;

namespace NBC.Game.Battle
{
    /// <summary>一次技能释放的结果。</summary>
    public readonly struct SkillCastOutcome
    {
        /// <summary>技能编号。</summary>
        public readonly int SkillId;

        /// <summary>实际扣掉的血。</summary>
        public readonly int AppliedDamage;

        /// <summary>目标剩余血。</summary>
        public readonly int RemainingHp;

        /// <summary>这一下是否打死了目标。</summary>
        public readonly bool Killed;

        /// <summary>造一个结果（只有 `BattleWorld` 会调）。</summary>
        /// <param name="skillId">技能编号。</param>
        /// <param name="appliedDamage">实际扣血。</param>
        /// <param name="remainingHp">剩余血。</param>
        /// <param name="killed">是否打死。</param>
        internal SkillCastOutcome(int skillId, int appliedDamage, int remainingHp, bool killed)
        {
            SkillId = skillId;
            AppliedDamage = appliedDamage;
            RemainingHp = remainingHp;
            Killed = killed;
        }

        /// <summary>转成一句人话。</summary>
        /// <returns>描述。</returns>
        public override string ToString()
        {
            return "技能 " + SkillId + " 命中：-" + AppliedDamage + "（剩 " + RemainingHp + "）" +
                   (Killed ? "，击杀" : string.Empty);
        }
    }

    /// <summary>单机战斗世界。</summary>
    public sealed class BattleWorld
    {
        /// <summary>怪物表。</summary>
        private readonly MonsterConfig m_monsters;

        /// <summary>英雄表。</summary>
        private readonly HeroConfig m_heroes;

        /// <summary>技能表。</summary>
        private readonly SkillConfig m_skills;

        /// <summary>在场单位：实例编号 → 单位。</summary>
        private readonly Dictionary<int, BattleAgent> m_agents = new Dictionary<int, BattleAgent>();

        /// <summary>下一个实例编号（只增不减，避免"死掉的号被复用"）。</summary>
        private int m_nextInstanceId = 1;

        /// <summary>造一个战斗世界。</summary>
        /// <param name="monsters">怪物表（不能为 null）。</param>
        /// <param name="heroes">英雄表（不能为 null）。</param>
        /// <param name="skills">技能表（不能为 null）。</param>
        public BattleWorld(MonsterConfig monsters, HeroConfig heroes, SkillConfig skills)
        {
            if (monsters == null) { throw new ArgumentNullException(nameof(monsters), "[BattleWorld] 怪物表是 null。"); }
            if (heroes == null) { throw new ArgumentNullException(nameof(heroes), "[BattleWorld] 英雄表是 null。"); }
            if (skills == null) { throw new ArgumentNullException(nameof(skills), "[BattleWorld] 技能表是 null。"); }

            m_monsters = monsters;
            m_heroes = heroes;
            m_skills = skills;
        }

        /// <summary>在场单位数（含已经死了但还没被摘掉的？不会 —— 死的那一刻就摘掉了）。</summary>
        public int AgentCount
        {
            get { return m_agents.Count; }
        }

        /// <summary>还活着的怪物数量（任务/关卡进度看它）。</summary>
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

        // ====================================================================
        //  刷怪
        // ====================================================================

        /// <summary>
        /// 按怪物表刷一只怪。
        /// <para>⚠️ **每次调用都是一个新单位**（新的实例编号）—— 所以"同一个 6001 刷三只"
        /// 是三个不同的对象，给其中一只上状态不会影响另外两只。</para>
        /// </summary>
        /// <param name="monsterId">怪物表的编号。</param>
        /// <returns>新单位。</returns>
        /// <exception cref="InvalidOperationException">怪物表里没有这个编号时抛（配置错误，不猜）。</exception>
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

        /// <summary>按英雄表刷一个英雄单位（M2 里就是"玩家"）。</summary>
        /// <param name="heroId">英雄表的编号。</param>
        /// <returns>新单位。</returns>
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

        /// <summary>按实例编号取单位。</summary>
        /// <param name="instanceId">实例编号。</param>
        /// <param name="agent">单位。</param>
        /// <returns>在场就返回 true。</returns>
        public bool TryGetAgent(int instanceId, out BattleAgent agent)
        {
            return m_agents.TryGetValue(instanceId, out agent);
        }

        /// <summary>按**配置编号**找第一只还在场的怪（演示/关卡脚本用）。</summary>
        /// <param name="monsterId">怪物配置编号。</param>
        /// <param name="agent">找到的单位。</param>
        /// <returns>找到返回 true。</returns>
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

        /// <summary>技能表里有这个技能吗（**装配时先问一遍**，别等到按下按钮才发现）。</summary>
        /// <param name="skillId">技能编号。</param>
        /// <returns>有就返回 true。</returns>
        public bool HasSkill(int skillId)
        {
            Config_Skill row;
            return m_skills.TryGet(skillId, out row);
        }

        /// <summary>
        /// 这个单位会这个技能吗（技能归属来自配置表 `Hero.skillIds` / `Monster.skillIds`）。
        /// <para>
        /// ⚠️ 它是**公开的**，因为"绑定动作"那一侧（`SkillCaster`）需要在**装配期**就问一遍 ——
        /// 归属规则本身只有一份实现（本方法），但会有**两个调用点**：
        /// 装配时早检查一次，结算时再兜一次底。这不叫"两套规则"，叫 fail early + fail loud。
        /// </para>
        /// </summary>
        /// <param name="instanceId">单位实例编号（必须在场）。</param>
        /// <param name="skillId">技能编号。</param>
        /// <returns>会就返回 true。</returns>
        public bool CasterOwnsSkill(int instanceId, int skillId)
        {
            return OwnsSkill(RequireAgent(instanceId), skillId);
        }

        /// <summary>这个单位会这个技能吗（技能归属来自配置表）。</summary>
        /// <param name="agent">单位。</param>
        /// <param name="skillId">技能编号。</param>
        /// <returns>会就返回 true。</returns>
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

        // ====================================================================
        //  结算
        // ====================================================================

        /// <summary>
        /// 把一次伤害结算到目标身上，并按结果广播事件。
        /// <para>⚠️ 目标**必须在场且活着**（不变量：在场 ⇒ 活着）。</para>
        /// </summary>
        /// <param name="attackerInstanceId">攻击者实例编号（0 = 没有攻击者/环境伤害）。</param>
        /// <param name="targetInstanceId">目标实例编号（必须在场）。</param>
        /// <param name="damage">伤害值（≥ 0）。</param>
        /// <returns>结算结果。</returns>
        /// <exception cref="InvalidOperationException">目标不在场时抛（程序错误）。</exception>
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

        /// <summary>
        /// 释放一次技能（M2 的最小形态：**单体、必中、立即结算**）。
        /// <para>⚠️ 事件顺序契约：`SkillHit` → `DamageDealt` → （致死时）`MonsterDied`/`HeroDied`。</para>
        /// <para>⚠️ "没打中"这种情况 M2 **不存在**（必中）；范围判定、闪避、命中率是 M3 的事。</para>
        /// </summary>
        /// <param name="casterInstanceId">施法者实例编号（必须在场）。</param>
        /// <param name="skillId">技能编号（必须在技能表里）。</param>
        /// <param name="targetInstanceId">目标实例编号（必须在场）。</param>
        /// <returns>释放结果。</returns>
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

            // ⚠️ 归属校验：**只能放自己技能列表里的技能**。
            //    为什么要在这一层挡：技能的归属是**配置驱动**的（`Hero.skillIds` /
            //    `Monster.skillIds`），而绑定动作的人（输入层、AI）很容易绑错一个编号。
            //    不挡的话，"按 J 放出了法师的穿心箭"会**静默生效** ——
            //    表现是"这个技能怎么伤害不对"，而不是任何一条报错。
            if (!OwnsSkill(caster, skillId))
            {
                throw new InvalidOperationException(
                    "[BattleWorld] " + caster.Name + "（实例 #" + casterInstanceId + "，配置 " +
                    caster.ConfigId + "）的技能列表里没有技能 " + skillId + "。\n" +
                    "技能归属来自配置（`Hero.skillIds` / `Monster.skillIds`），" +
                    "所以这多半是**绑定动作时写错了编号**，或者表里少填了一个技能。");
            }

            // 顺序契约第一段：先说"打中了"（任务条件"用技能命中 N 次"靠它）
            EventCenter.Instance.Trigger(BattleEvents.SkillHit,
                new SkillHitPayload(casterInstanceId, targetInstanceId, skillId, target.ConfigId));

            // 第二段：扣血（ApplyDamage 内部会广播 DamageDealt，并可能广播死亡事件）
            DamageOutcome outcome = ApplyDamage(casterInstanceId, targetInstanceId, skill.damage);

            return new SkillCastOutcome(skillId, outcome.Applied, outcome.RemainingHp, outcome.IsLethal);
        }

        /// <summary>把在场单位全部清掉（换关卡/重跑演示用；**不动配置表**）。</summary>
        public void Clear()
        {
            m_agents.Clear();
        }

        /// <summary>把在场单位拼成一段文本（调试面板/控制台用）。</summary>
        /// <returns>多行文本。</returns>
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

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>取一个必须在场的单位（不在场 = 程序错误，直接抛）。</summary>
        /// <param name="instanceId">实例编号。</param>
        /// <returns>单位。</returns>
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

        /// <summary>一个单位被打死之后：从世界摘掉 + 广播对应的死亡事件。</summary>
        /// <param name="agent">死掉的单位。</param>
        /// <param name="killerInstanceId">击杀者实例编号。</param>
        private void OnAgentKilled(BattleAgent agent, int killerInstanceId)
        {
            // 先摘掉再广播：回调里如果去查这个单位（例如"还剩几只怪"），
            // 它**已经不在场**了 —— 这才是符合直觉的答案
            // （反过来的话，回调会看到一个 0 血的单位还杵在场上）
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

        /// <summary>发一个新的实例编号。</summary>
        /// <returns>实例编号。</returns>
        private int NextInstanceId()
        {
            return m_nextInstanceId++;
        }
    }
}
