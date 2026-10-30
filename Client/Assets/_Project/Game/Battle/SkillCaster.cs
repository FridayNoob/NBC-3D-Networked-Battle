// 这个文件里有：
//   SkillCaster（类）：把输入命令里「本帧新按下」的动作翻成技能释放，并累计放过几发
//   SkillCaster.Binding（只读结构体）：一条「输入动作 → 技能编号」的绑定，只在本类内部用

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Input;
using NBC.Game.Config;

namespace NBC.Game.Battle
{
    // 主要功能：把「玩家按了某个键」翻译成「打出一发技能」—— 动作进、技能编号出，结算全部交给 BattleWorld。
    // 主要数据成员：m_world（真正的结算方）、m_casterInstanceId（施法者实例编号，正整数）、m_bindings（动作 → 技能编号的绑定表）、m_castCount（累计发数，发）。
    // 主要外部方法：Bind / TryGetSkill / Handle / Describe，以及只读属性 BindCount / CastCount / CasterInstanceId。
    // 谁在用它：BattleSession 装配时造它、每帧经 HandleInput 把输入转给 Handle；Boot 的 M2DemoBehaviour 经 BattleSession.Caster 给玩家技能键绑一条；EditMode 用例直接造它验证绑定与放技能。
    // 注意：它不订阅事件中心、也不持有 MonoBehaviour：Handle 是纯输入输出，能用手搓的 InputCommand 在 EditMode 直接测；运行时由 BattleSession.HandleInput 把 InputManager 的命令喂进来。
    // 注意：「哪个动作放哪个技能」用注册映射而不是写死：技能列表由配置驱动（Hero.skillIds，一个英雄 1~4 个技能），写死会让「加一个技能」变成改代码。
    public sealed class SkillCaster
    {
        // 主要功能：把「按哪个动作」与「放哪个技能编号」捆成一条，绑定表只存这个。
        // 主要数据成员：Action（输入动作）、SkillId（技能编号，Skill 表主键，正整数）。
        // 主要外部方法：构造函数（只在本类内部调用）。
        // 谁在用它：SkillCaster 的绑定表 m_bindings —— Bind 往里加，Handle / TryGetSkill / Describe 读。
        private readonly struct Binding
        {
            // 输入动作：InputManager 声明的动作编号，一条绑定只认一个
            public readonly InputActionId Action;

            // 技能编号：Skill 表主键，正整数；Bind 时已校验在表里且属于施法者
            public readonly int SkillId;

            // 功能：把动作与技能编号装成一条绑定。
            // 入参：action 输入动作；skillId 技能编号（Skill 表主键，正整数）
            // 返回：无
            // 步骤：
            //   1) 两个值按字段顺序原样写入 —— 校验都在 SkillCaster.Bind 做，这里不重复
            // 上游：SkillCaster.Bind
            // 下游：无
            public Binding(InputActionId action, int skillId)
            {
                Action = action;
                SkillId = skillId;
            }
        }

        // 战斗世界：技能表查询、归属校验与实际结算都在它那边
        private readonly BattleWorld m_world;

        // 施法者的实例编号（正整数，构造时必须在场）
        private readonly int m_casterInstanceId;

        // 动作 → 技能编号的绑定表，装配期只增不改，条数见 BindCount
        private readonly List<Binding> m_bindings = new List<Binding>();

        // 累计放过几发技能（发），只增不减，调试/UI 用
        private int m_castCount;

        // 功能：记下战斗世界与施法者编号，并确认这个编号在场，把装配错误拦在构造时。
        // 入参：world 战斗世界（null 抛 ArgumentNullException）；casterInstanceId 施法者实例编号（必须是已在场的单位）
        // 返回：无
        // 步骤：
        //   1) 先判 world 为 null：后面每次结算都要用它，空引用不如在这里就炸
        //   2) 再查编号在不在场（BattleWorld.TryGetAgent）：不在场就没有技能归属与血量可谈，当场抛 InvalidOperationException
        //   3) 两步都过了才写字段，构造出来的对象一定可用
        // 上游：BattleSession 的构造函数（用刚 SpawnHero 出来的玩家英雄编号）；EditMode 用例
        // 下游：BattleWorld.TryGetAgent
        // 注意：编号要先用 SpawnHero 拿到再传进来；传错编号时这里是唯一一次能报清楚的地方。
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

        // 功能：读绑定表里已经绑了几条动作。
        public int BindCount
        {
            get { return m_bindings.Count; }
        }

        // 功能：读累计放过几发技能（发）。
        public int CastCount
        {
            get { return m_castCount; }
        }

        // 功能：读施法者的实例编号（正整数）。
        public int CasterInstanceId
        {
            get { return m_casterInstanceId; }
        }

        /// <summary>绑一条「按这个动作放这个技能」；技能不在表里或施法者不会，装配当场抛异常。</summary>
        // 功能：把动作与技能绑进表里，并在装配期把「技能表里有」与「施法者会」两件事都校验掉。
        // 入参：action 输入动作（一个动作只能绑一次）；skillId 技能编号（Skill 表主键，正整数）
        // 返回：无
        // 步骤：
        //   1) 先扫绑定表，同一个动作重复绑直接抛异常 —— 静默覆盖会让「明明绑了火球，怎么放的是冰箭」变成悬案
        //   2) 查技能表有没有这个编号：配置没导入时也走到这里，报错才会有人去点导入 TSV 菜单
        //   3) 再查施法者的技能归属（BattleWorld.CasterOwnsSkill）：绑错英雄或怪物的技能当场炸，别等玩家按下按钮
        //   4) 三项都过了才写进绑定表 —— 校验失败时不留半条绑定
        // 上游：Boot 的 M2DemoBehaviour（经 BattleSession.Caster 绑玩家的技能键）；EditMode 用例
        // 下游：BattleWorld.HasSkill / BattleWorld.CasterOwnsSkill
        // 注意：归属这一步第一版漏了，只查了技能表有没有，于是「给法师绑了剑士的穿心箭」要等玩家按下按钮才炸。
        // 注意：正常装配是按配置循环 Bind（Hero.skillIds / Monster.skillIds 里有几个技能绑几条），走到归属报错多半是手写死了一个不属于它的技能编号。
        // 注意：归属规则只有 BattleWorld.CasterOwnsSkill 一份实现，这里与结算期各问一次不是两套规则，是 fail early + fail loud。
        public void Bind(InputActionId action, int skillId)
        {
            for (int i = 0; i < m_bindings.Count; i++)
            {
                if (m_bindings[i].Action == action)
                {
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

        // 功能：查这个动作绑的是哪个技能编号。
        // 入参：action 输入动作；skillId 绑过则回填技能编号，没绑回填 0
        // 返回：这个动作绑过返回 true 且 skillId 是技能编号；没绑过返回 false，skillId 为 0
        // 步骤：
        //   1) 顺序扫绑定表，命中就回填并返回 —— 同一个动作不许绑第二次（Bind 已拦），所以不会有第二条
        //   2) 扫完没有就回填 0 并返回 false：out 参数必须赋值，也给调用方一个确定的「没有」
        // 上游：EditMode 用例；运行时暂无调用点
        // 下游：无
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

        // 功能：处理一帧输入，把本帧新按下的动作逐个放成技能，返回这一帧放了几发。
        // 入参：command 这一帧的输入命令（ActionBits = 本帧新按下，ActionReleaseBits = 本帧新松开）；targetInstanceId 目标实例编号（必须在场，这一版是单体、由调用方指定）；results 每次释放的结果追加进来（可以为 null）
        // 返回：这一帧放了几发（0 表示这一帧没有绑定的动作被按下）
        // 步骤：
        //   1) 按绑定表顺序逐条看动作位是否在本帧新按下，没按就跳过 —— 不比边沿，语义由 InputManager 的 ActionBits 负责
        //   2) 按下就调 BattleWorld.CastSkill 结算，结果在 results 非 null 时追加
        //   3) 两个计数各加一：本帧发数与累计发数 —— 只有真的结算完才算一发
        // 上游：BattleSession.HandleInput（Boot 的 M2DemoBehaviour 每帧把 InputManager 的命令喂进来）；EditMode 用例
        // 下游：BattleWorld.CastSkill
        // 注意：ActionBits 的语义是「本帧新按下」而不是「现在按住」；若哪天读成按住，现象是按住不放每帧放一发（一秒 60 发），而且看起来还挺跟手。
        // 注意：刻意不 catch：目标不在场、技能表缺行都是程序或配置错误，吞掉会让「按了没反应」变成一类查不出来的问题。
        public int Handle(InputCommand command, int targetInstanceId, List<SkillCastOutcome> results)
        {
            int cast = 0;

            for (int i = 0; i < m_bindings.Count; i++)
            {
                Binding binding = m_bindings[i];

                if (!command.HasAction(binding.Action))
                {
                    continue;
                }

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

        // 功能：把绑定表拼成多行文本，给调试面板看施法者是谁、绑了哪些动作。
        // 入参：无
        // 返回：多行文本；一条都没绑时只有一句「没有绑定任何动作」
        // 步骤：
        //   1) 空表先单独给一句话，免得面板上只有一个空表头
        //   2) 表头写施法者实例编号与绑定条数
        //   3) 每条绑定一行「动作 → 技能编号」，前缀用顿点，长表也能一眼扫下来
        // 上游：调试与日志用（当前仓库内暂无调用点）
        // 下游：无
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
