// ============================================================================
//  SkillCaster —— 输入 → 技能：把"玩家按了某个键"翻译成"打出一发技能"
//  项目：3D联网战斗Demo   对应：M2-B2
//
//  ---------------------------------------------------------------------------
//  它在整条链上的位置
//  ---------------------------------------------------------------------------
//      A7 `InputManager`（采集 + 生成命令）
//            │  InputCommand：MoveX/MoveY + **本帧新按下**的动作位 + **本帧新松开**的动作位
//            ▼
//      本类：动作 → 技能编号（**注册映射**）→ 调 `BattleWorld.CastSkill`
//            ▼
//      BattleWorld：查技能表 → 广播 SkillHit → 扣血 → （致死）广播死亡
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么不需要自己算"按下沿"（这是 A7 设计得好的一处，值得说清）
//  ---------------------------------------------------------------------------
//  `InputCommand.ActionBits` 的语义是"**本帧新按下**"，不是"现在按住"
//  （`ActionReleaseBits` 是"本帧新松开"）。所以本类直接：
//
//      if (command.HasAction(m_action)) { 放技能 }
//
//  **不需要**自己存上一帧的命令去比边沿。如果哪天有人把它读成"现在按住"，
//  现象就是"按住不放 → 每帧放一发技能"（一秒 60 发）—— 而且看起来还挺"跟手"。
//  这类"语义读错"的 bug 只能靠**把语义写在类型/注释上**来防，本类就是靠 A7 写清了。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么"哪个键放哪个技能"要用注册映射，而不是写死
//  ---------------------------------------------------------------------------
//  和 M2-A 的 `ConditionEventBridge` 同一个理由：
//  技能列表是**配置驱动**的（`Hero.skillIds`），一个英雄可能有 1~4 个技能，
//  写死 `if (key == J) CastSkill(2001)` 会让"加一个技能"变成改代码。
//  注册映射之后，装配处按配置**循环 Bind** 即可。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 它**不订阅**事件中心，也不持有 MonoBehaviour
//  ---------------------------------------------------------------------------
//  `Handle(command, target)` 是纯输入输出：喂一个命令、给一个目标，返回打了几发。
//  于是它能在 EditMode 里用一个**手搓的 `InputCommand`** 直接测 ——
//  不需要键盘、不需要帧循环（A7 那条"把键盘变成可传输的值"的回报）。
//  真要接运行时，装配处订阅 `InputManager.CommandGenerated` 即可。
//
//  ⚠️ "这个技能属于谁"由 `BattleWorld.CasterOwnsSkill` **一处实现**，但有两个调用点
//  ---------------------------------------------------------------------------
//      · `Bind`（**装配期**）：绑错了当场报错 —— 别等玩家按下按钮
//      · `CastSkill`（**结算期**）：兜底 —— 防的是"绕过 SkillCaster 直接调世界"
//
//  这不叫"两套规则"，叫 **fail early + fail loud**：规则只有一份实现，
//  但值得在两个时间点各问一次。
//  实际装配时，绑定是**按配置循环 Bind** 出来的，所以正常流程根本绑不错。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Input;
using NBC.Game.Config;

namespace NBC.Game.Battle
{
    /// <summary>把输入动作翻译成技能释放。</summary>
    public sealed class SkillCaster
    {
        /// <summary>一次动作绑定。</summary>
        private readonly struct Binding
        {
            /// <summary>输入动作。</summary>
            public readonly InputActionId Action;

            /// <summary>技能编号。</summary>
            public readonly int SkillId;

            /// <summary>造一条绑定。</summary>
            /// <param name="action">输入动作。</param>
            /// <param name="skillId">技能编号。</param>
            public Binding(InputActionId action, int skillId)
            {
                Action = action;
                SkillId = skillId;
            }
        }

        /// <summary>战斗世界（真正的结算方）。</summary>
        private readonly BattleWorld m_world;

        /// <summary>施法者的实例编号。</summary>
        private readonly int m_casterInstanceId;

        /// <summary>动作 → 技能的绑定表。</summary>
        private readonly List<Binding> m_bindings = new List<Binding>();

        /// <summary>累计放过几发技能（调试/UI 用）。</summary>
        private int m_castCount;

        /// <summary>造一个施法器。</summary>
        /// <param name="world">战斗世界（不能为 null）。</param>
        /// <param name="casterInstanceId">施法者实例编号（必须在场）。</param>
        public SkillCaster(BattleWorld world, int casterInstanceId)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world), "[SkillCaster] 战斗世界是 null。");
            }

            BattleAgent caster;
            if (!world.TryGetAgent(casterInstanceId, out caster))
            {
                throw new InvalidOperationException(
                    "[SkillCaster] 场上没有实例编号 " + casterInstanceId + " 的单位，不能当施法者。\n" +
                    "先 `SpawnHero(...)` 拿到实例编号，再把它传进来。");
            }

            m_world = world;
            m_casterInstanceId = casterInstanceId;
        }

        /// <summary>绑了几条动作。</summary>
        public int BindCount
        {
            get { return m_bindings.Count; }
        }

        /// <summary>累计放过几发。</summary>
        public int CastCount
        {
            get { return m_castCount; }
        }

        /// <summary>施法者实例编号。</summary>
        public int CasterInstanceId
        {
            get { return m_casterInstanceId; }
        }

        /// <summary>
        /// 绑一条"按这个键 → 放这个技能"。
        /// <para>⚠️ 技能编号**当场校验**（在技能表里查一遍）：装配时就报错，
        /// 好过等到玩家按下按钮才发现放不出来。</para>
        /// </summary>
        /// <param name="action">输入动作。</param>
        /// <param name="skillId">技能编号。</param>
        public void Bind(InputActionId action, int skillId)
        {
            for (int i = 0; i < m_bindings.Count; i++)
            {
                if (m_bindings[i].Action == action)
                {
                    // 同一个动作绑两个技能 = 按一下放两个，几乎总是 bug。
                    // 静默覆盖会让"我明明绑了火球，怎么放的是冰箭"变成悬案。
                    throw new InvalidOperationException(
                        "[SkillCaster] 动作 " + action + " 已经绑过技能 " + m_bindings[i].SkillId + " 了。\n" +
                        "一个动作只能绑一个技能；要放两个技能请绑两个动作。");
                }
            }

            if (!m_world.HasSkill(skillId))
            {
                throw new InvalidOperationException(
                    "[SkillCaster] 技能表 `Skill` 里没有编号 " + skillId + "（动作 " + action + "）。\n" +
                    "先确认 ConfigKit 生成过、并且点过 `Tools/NBC/配置表/导入 TSV` 菜单。");
            }

            // ⚠️ 归属也要在**装配期**查（这正是"早检查"的意义）：
            //    第一版我只查了"技能表里有没有"，没查"这个英雄会不会"，
            //    于是"给法师绑了剑士的穿心箭"要等到**玩家按下按钮**才炸。
            //    这条是负责人跑测试时红出来的（`Handle_TwoActionsPressed_CastsBoth`）——
            //    红的是用例，但**暴露的是这里少了一半检查**。
            if (!m_world.CasterOwnsSkill(m_casterInstanceId, skillId))
            {
                throw new InvalidOperationException(
                    "[SkillCaster] 施法者（实例 #" + m_casterInstanceId + "）的技能列表里没有技能 " +
                    skillId + "（动作 " + action + "）。\n" +
                    "技能归属来自配置（`Hero.skillIds` / `Monster.skillIds`）。\n" +
                    "正常装配应当**按配置循环 Bind**（配置里有几个技能就绑几条），" +
                    "所以走到这里多半是手写死了一个不属于它的技能编号。");
            }

            m_bindings.Add(new Binding(action, skillId));
        }

        /// <summary>这个动作绑了哪个技能。</summary>
        /// <param name="action">输入动作。</param>
        /// <param name="skillId">技能编号。</param>
        /// <returns>绑过返回 true。</returns>
        public bool TryGetSkill(InputActionId action, out int skillId)
        {
            for (int i = 0; i < m_bindings.Count; i++)
            {
                if (m_bindings[i].Action == action)
                {
                    skillId = m_bindings[i].SkillId;
                    return true;
                }
            }

            skillId = 0;
            return false;
        }

        /// <summary>
        /// 处理一帧的输入命令：**本帧新按下**的动作会被释放成技能。
        /// </summary>
        /// <param name="command">这一帧的输入命令（`ActionBits` = 本帧新按下）。</param>
        /// <param name="targetInstanceId">目标实例编号（M2 是单体、手动指定目标）。</param>
        /// <param name="results">每次释放的结果会追加进来（可以为 null）。</param>
        /// <returns>这一帧放了几发。</returns>
        public int Handle(InputCommand command, int targetInstanceId, List<SkillCastOutcome> results)
        {
            int cast = 0;

            for (int i = 0; i < m_bindings.Count; i++)
            {
                Binding binding = m_bindings[i];

                // 语义：HasAction = **本帧新按下**（不是"现在按住"）—— 所以这边不用比边沿
                if (!command.HasAction(binding.Action))
                {
                    continue;
                }

                // ⚠️ 刻意**不 catch**：目标不在场 / 技能表缺行都是程序或配置错误，
                //    吞掉它们会让"按了没反应"变成一类查不出来的问题。
                SkillCastOutcome outcome = m_world.CastSkill(m_casterInstanceId, binding.SkillId, targetInstanceId);

                cast++;
                m_castCount++;

                if (results != null)
                {
                    results.Add(outcome);
                }
            }

            return cast;
        }

        /// <summary>把绑定表拼成一段文本（调试面板用）。</summary>
        /// <returns>多行文本。</returns>
        public string Describe()
        {
            if (m_bindings.Count == 0)
            {
                return "[SkillCaster] 没有绑定任何动作。";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("[SkillCaster] 施法者 #").Append(m_casterInstanceId)
                   .Append("，绑了 ").Append(m_bindings.Count).Append(" 条：\n");

            for (int i = 0; i < m_bindings.Count; i++)
            {
                builder.Append("  · ").Append(m_bindings[i].Action)
                       .Append(" → 技能 ").Append(m_bindings[i].SkillId).Append('\n');
            }

            return builder.ToString();
        }
    }
}
