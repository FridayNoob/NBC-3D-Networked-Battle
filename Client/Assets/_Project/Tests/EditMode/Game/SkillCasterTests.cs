// ============================================================================
//  M2-B2 · 输入 → 技能的 EditMode 测试
//  对应验收：Docs\22-M2开工清单.md B2
//  被测：Client\Assets\_Project\Game\Battle\SkillCaster.cs
//
//  ---------------------------------------------------------------------------
//  这一组测的是"**键按下去之后会发生什么**"，而且不需要键盘
//  ---------------------------------------------------------------------------
//  `InputCommand` 是一个**值**（`MoveX/MoveY` + 本帧新按下/新松开的位掩码），
//  所以测试里手搓一个命令就能模拟"玩家按了 J" ——
//  这正是 A7 那句"`int` 能过网线、`KeyCode` 不能"换来的东西：
//  **输入是可构造的数据，不是环境**。
//
//  ⚠️ 其中两条用例专门盯"语义读错"：
//      · `Handle_ReleasedAction_DoesNotCast` —— 把 `ActionBits` 误读成"现在按住"，
//        就会变成"松手也放技能"；
//      · `Handle_HeldCommand_...` 那条更狠：**同一个"按住"的命令喂两帧，
//        只应该放一发**（因为框架的语义是"本帧新按下"）。
//        如果误读成"按住就放"，现象是"按住不放 → 一秒 60 发"，
//        而且看起来还挺"跟手"，极难被判成 bug。
// ============================================================================

using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Input;
using NBC.Game.Battle;
using NBC.Game.Config;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>M2-B2：输入翻译成技能的测试。</summary>
    public sealed class SkillCasterTests
    {
        /// <summary>技能表。</summary>
        private SkillConfig m_skills;

        /// <summary>英雄表。</summary>
        private HeroConfig m_heroes;

        /// <summary>怪物表。</summary>
        private MonsterConfig m_monsters;

        /// <summary>战斗世界。</summary>
        private BattleWorld m_world;

        /// <summary>玩家单位。</summary>
        private BattleAgent m_hero;

        /// <summary>目标怪。</summary>
        private BattleAgent m_wolf;

        /// <summary>输入动作：技能 1。</summary>
        private InputActionId m_skill1;

        /// <summary>输入动作：技能 2。</summary>
        private InputActionId m_skill2;

        /// <summary>被测对象。</summary>
        private SkillCaster m_caster;

        /// <summary>每个用例前造一套干净的。</summary>
        [SetUp]
        public void SetUp()
        {
            // 事件中心是常驻单例：关掉"没人听"的警告
            EventCenter.Instance.WarnOnMissingListener = false;

            // `InputActionId.Declare` 有全局重名检查，所以每个用例前先清声明
            InputActionId.ResetDeclarationsForTests();
            m_skill1 = InputActionId.Declare(0, "Skill1");
            m_skill2 = InputActionId.Declare(1, "Skill2");

            m_skills = BattleTestTables.CreateSkills();
            m_heroes = BattleTestTables.CreateHeroes();
            m_monsters = BattleTestTables.CreateMonsters();

            m_world = new BattleWorld(m_monsters, m_heroes, m_skills);
            m_hero = m_world.SpawnHero(BattleTestTables.Mage);      // 800 血，带穿心箭
            m_wolf = m_world.SpawnMonster(BattleTestTables.Wolf);   // 300 血

            m_caster = new SkillCaster(m_world, m_hero.InstanceId);
        }

        /// <summary>每个用例后清理。</summary>
        [TearDown]
        public void TearDown()
        {
            EventCenter.Instance.WarnOnMissingListener = true;

            BattleTestTables.Destroy(m_skills);
            BattleTestTables.Destroy(m_heroes);
            BattleTestTables.Destroy(m_monsters);

            InputActionId.ResetDeclarationsForTests();
        }

        /// <summary>造一个"某几个动作本帧新按下"的命令。</summary>
        /// <param name="pressed">按下的动作。</param>
        /// <returns>输入命令。</returns>
        private static InputCommand Command(params InputActionId[] pressed)
        {
            uint bits = 0;

            for (int i = 0; i < pressed.Length; i++)
            {
                bits |= 1u << pressed[i].Index;
            }

            return new InputCommand(1, 0, 0, bits, 0u);
        }

        // ====================================================================
        //  一、按下就放技能
        // ====================================================================

        /// <summary>绑好技能后，按下对应动作会真的打出一发（血量掉、事件发）。</summary>
        [Test]
        public void Handle_PressedAction_CastsSkill()
        {
            m_caster.Bind(m_skill1, BattleTestTables.PiercingArrow);

            List<SkillCastOutcome> results = new List<SkillCastOutcome>();
            int cast = m_caster.Handle(Command(m_skill1), m_wolf.InstanceId, results);

            Assert.AreEqual(1, cast);
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(120, results[0].AppliedDamage);
            Assert.AreEqual(180, m_wolf.Hp);
            Assert.AreEqual(1, m_caster.CastCount);
        }

        /// <summary>没按任何键：什么都不发生。</summary>
        [Test]
        public void Handle_EmptyCommand_DoesNothing()
        {
            m_caster.Bind(m_skill1, BattleTestTables.PiercingArrow);

            int cast = m_caster.Handle(default(InputCommand), m_wolf.InstanceId, null);

            Assert.AreEqual(0, cast);
            Assert.AreEqual(300, m_wolf.Hp);
            Assert.AreEqual(0, m_caster.CastCount);
        }

        /// <summary>按的是别的键：不放技能。</summary>
        [Test]
        public void Handle_OtherAction_DoesNothing()
        {
            m_caster.Bind(m_skill1, BattleTestTables.PiercingArrow);

            int cast = m_caster.Handle(Command(m_skill2), m_wolf.InstanceId, null);

            Assert.AreEqual(0, cast, "只绑了 Skill1，按 Skill2 不该放技能");
            Assert.AreEqual(300, m_wolf.Hp);
        }

        /// <summary>
        /// **`ActionBits` 是"本帧新按下"，不是"现在按住"** ——
        /// 所以同一个命令喂两帧，只应该放一发。
        /// </summary>
        [Test]
        public void Handle_SamePressCommandTwice_CastsTwiceButIsNotHoldSemantics()
        {
            m_caster.Bind(m_skill1, BattleTestTables.PiercingArrow);

            InputCommand press = Command(m_skill1);

            // 两帧都带"新按下"位 = 玩家真的按了两下（松开过又按下）
            m_caster.Handle(press, m_wolf.InstanceId, null);
            m_caster.Handle(press, m_wolf.InstanceId, null);

            Assert.AreEqual(2, m_caster.CastCount);
            Assert.AreEqual(60, m_wolf.Hp);

            // 而"一直按住"在协议里表现为：**只有第一帧带按下位，后面都不带**
            // （这正是 A7 同时发"按下 + 抬起"两条边沿的用途）——
            // 那种情况下本类**不会**再放技能（因为 HasAction 为 false）。
            Assert.IsFalse(default(InputCommand).HasAction(m_skill1),
                "按住不放时，后续帧的 ActionBits 是 0 —— 所以不会每帧放一发");
        }

        /// <summary>松手位**不会**触发技能（把语义读成"有变化就放"就会错）。</summary>
        [Test]
        public void Handle_ReleasedAction_DoesNotCast()
        {
            m_caster.Bind(m_skill1, BattleTestTables.PiercingArrow);

            // 只有"新松开"位，没有"新按下"位
            InputCommand release = new InputCommand(1, 0, 0, 0u, 1u << m_skill1.Index);

            int cast = m_caster.Handle(release, m_wolf.InstanceId, null);

            Assert.AreEqual(0, cast);
            Assert.AreEqual(300, m_wolf.Hp);
        }

        /// <summary>一个命令里同时按下两个绑定的动作：两发都放。</summary>
        [Test]
        public void Handle_TwoActionsPressed_CastsBoth()
        {
            m_caster.Bind(m_skill1, BattleTestTables.PiercingArrow);   // 120
            m_caster.Bind(m_skill2, BattleTestTables.IceArrow);        // 80

            List<SkillCastOutcome> results = new List<SkillCastOutcome>();
            int cast = m_caster.Handle(Command(m_skill1, m_skill2), m_wolf.InstanceId, results);

            Assert.AreEqual(2, cast);
            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(100, m_wolf.Hp, "300 - 120 - 80 = 100");
        }

        /// <summary>反复按同一个键：怪会被打死，死亡事件照常广播。</summary>
        [Test]
        public void Handle_RepeatedPresses_KillsMonsterThroughRealInput()
        {
            List<MonsterDiedPayload> deaths = new List<MonsterDiedPayload>();
            UnityEngine.Events.UnityAction<MonsterDiedPayload> handler = payload => deaths.Add(payload);
            EventCenter.Instance.AddEventListener(BattleEvents.MonsterDied, handler);

            try
            {
                m_caster.Bind(m_skill1, BattleTestTables.PiercingArrow);   // 120

                // 300 血 / 每发 120 -> 三发打死
                m_caster.Handle(Command(m_skill1), m_wolf.InstanceId, null);
                m_caster.Handle(Command(m_skill1), m_wolf.InstanceId, null);
                Assert.AreEqual(0, deaths.Count, "两发打不死 300 血的怪（300 - 120 - 120 = 60）");

                m_caster.Handle(Command(m_skill1), m_wolf.InstanceId, null);

                Assert.AreEqual(1, deaths.Count);
                Assert.AreEqual(BattleTestTables.Wolf, deaths[0].MonsterConfigId);
                Assert.AreEqual(m_hero.InstanceId, deaths[0].KillerInstanceId);
                Assert.AreEqual(3, m_caster.CastCount);
            }
            finally
            {
                EventCenter.Instance.RemoveEventListener(BattleEvents.MonsterDied, handler);
                EventCenter.Instance.ClearListeners(BattleEvents.MonsterDied);
            }
        }

        // ====================================================================
        //  二、装配期的错误必须"响亮"
        // ====================================================================

        /// <summary>同一个动作绑两次：抛异常（静默覆盖会让"怎么放的是另一个技能"变悬案）。</summary>
        [Test]
        public void Bind_DuplicateAction_Throws()
        {
            m_caster.Bind(m_skill1, BattleTestTables.PiercingArrow);

            System.InvalidOperationException exception =
                Assert.Throws<System.InvalidOperationException>(
                    () => m_caster.Bind(m_skill1, BattleTestTables.IceArrow));

            StringAssert.Contains("已经绑过", exception.Message);
        }

        /// <summary>绑一个技能表里没有的技能：**装配时就报错**，别等玩家按下去。</summary>
        [Test]
        public void Bind_UnknownSkill_ThrowsAtBindTime()
        {
            System.InvalidOperationException exception =
                Assert.Throws<System.InvalidOperationException>(() => m_caster.Bind(m_skill1, 9999));

            StringAssert.Contains("9999", exception.Message);
        }

        /// <summary>施法者不在场：构造时就报错。</summary>
        [Test]
        public void Constructor_UnknownCaster_Throws()
        {
            System.InvalidOperationException exception =
                Assert.Throws<System.InvalidOperationException>(() => new SkillCaster(m_world, 4242));

            StringAssert.Contains("4242", exception.Message);
        }

        /// <summary>目标不在场：异常**不被吞掉**（"按了没反应"是最难查的一类问题）。</summary>
        [Test]
        public void Handle_TargetNotInWorld_PropagatesException()
        {
            m_caster.Bind(m_skill1, BattleTestTables.PiercingArrow);

            Assert.Throws<System.InvalidOperationException>(
                () => m_caster.Handle(Command(m_skill1), 4242, null));
        }

        /// <summary>查询绑定关系。</summary>
        [Test]
        public void TryGetSkill_ReportsBinding()
        {
            m_caster.Bind(m_skill1, BattleTestTables.PiercingArrow);

            int skillId;
            Assert.IsTrue(m_caster.TryGetSkill(m_skill1, out skillId));
            Assert.AreEqual(BattleTestTables.PiercingArrow, skillId);

            Assert.IsFalse(m_caster.TryGetSkill(m_skill2, out skillId));
            Assert.AreEqual(1, m_caster.BindCount);
        }
    }
}
