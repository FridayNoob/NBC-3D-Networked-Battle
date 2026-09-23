// ============================================================================
//  M2-B1 · 单机战斗世界的 EditMode 测试
//  对应验收：Docs\22-M2开工清单.md B1（V4）+ B2（V5 的一半：真事件驱动条件）
//  被测：Client\Assets\_Project\Game\Battle\
//
//  ---------------------------------------------------------------------------
//  这一组测的是"世界级"的行为，不是"某个单位"
//  ---------------------------------------------------------------------------
//      · 刷怪：同一个配置编号刷两只，**必须是两个独立对象**
//      · 结算：扣血 -> 广播 -> 致死则摘掉并广播死亡
//      · **事件顺序**：SkillHit -> DamageDealt -> MonsterDied（契约，不是细节）
//      · 打尸体：**一个事件都不许发**（发了任务进度就会多）
//
//  ⚠️ "打尸体不许发事件"这条是本文件里最重要的用例。
//     因为"多算一次击杀"这种错**不会崩、不会报错、只在统计里慢慢偏**，
//     正是本项目一直在防的那类问题。
//
//  📌 配置表还是用 `ScriptableObject.CreateInstance` 直接填 `rows`（同 M2-A 的做法）：
//     EditMode 不需要 YooAsset、不需要打包、不需要点菜单。
// ============================================================================

using System.Collections.Generic;
using NBC.Framework;
using NBC.Game.Battle;
using NBC.Game.Config;
using NBC.Game.Quest;
using NBC.Shared.Condition;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>M2-B1：战斗世界的测试。</summary>
    public sealed class BattleWorldTests
    {
        /// <summary>怪物表。</summary>
        private MonsterConfig m_monsters;

        /// <summary>英雄表。</summary>
        private HeroConfig m_heroes;

        /// <summary>技能表。</summary>
        private SkillConfig m_skills;

        /// <summary>被测的战斗世界。</summary>
        private BattleWorld m_world;

        /// <summary>记录：收到过的事件（按顺序，形如 `hit:2003`）。</summary>
        private List<string> m_events;

        /// <summary>记录：怪物死亡的载荷。</summary>
        private List<MonsterDiedPayload> m_monsterDeaths;

        /// <summary>记录：英雄死亡的载荷。</summary>
        private List<HeroDiedPayload> m_heroDeaths;

        // ====================================================================
        //  夹具
        // ====================================================================

        /// <summary>每个用例前造一套干净的配置 + 世界。</summary>
        [SetUp]
        public void SetUp()
        {
            EventCenter.Instance.WarnOnMissingListener = false;

            m_events = new List<string>();
            m_monsterDeaths = new List<MonsterDiedPayload>();
            m_heroDeaths = new List<HeroDiedPayload>();

            BuildTables();

            m_world = new BattleWorld(m_monsters, m_heroes, m_skills);

            EventCenter.Instance.AddEventListener<SkillHitPayload>(BattleEvents.SkillHit, OnSkillHit);
            EventCenter.Instance.AddEventListener<DamageDealtPayload>(BattleEvents.DamageDealt, OnDamageDealt);
            EventCenter.Instance.AddEventListener<MonsterDiedPayload>(BattleEvents.MonsterDied, OnMonsterDied);
            EventCenter.Instance.AddEventListener<HeroDiedPayload>(BattleEvents.HeroDied, OnHeroDied);
        }

        /// <summary>每个用例后退订、清场、销毁测试里造的资产。</summary>
        [TearDown]
        public void TearDown()
        {
            EventCenter.Instance.ClearListeners(BattleEvents.SkillHit);
            EventCenter.Instance.ClearListeners(BattleEvents.DamageDealt);
            EventCenter.Instance.ClearListeners(BattleEvents.MonsterDied);
            EventCenter.Instance.ClearListeners(BattleEvents.HeroDied);
            EventCenter.Instance.WarnOnMissingListener = true;

            Destroy(m_monsters);
            Destroy(m_heroes);
            Destroy(m_skills);

            m_monsters = null;
            m_heroes = null;
            m_skills = null;
        }

        /// <summary>销毁一个测试里造出来的资产。</summary>
        /// <param name="asset">资产。</param>
        private static void Destroy(Object asset)
        {
            if (asset != null)
            {
                Object.DestroyImmediate(asset);
            }
        }

        /// <summary>造三张表（**共用夹具**，见 `BattleTestTables`）。</summary>
        private void BuildTables()
        {
            m_skills = BattleTestTables.CreateSkills();
            m_heroes = BattleTestTables.CreateHeroes();
            m_monsters = BattleTestTables.CreateMonsters();
        }

        /// <summary>记录技能命中。</summary>
        /// <param name="payload">载荷。</param>
        private void OnSkillHit(SkillHitPayload payload)
        {
            m_events.Add("hit:" + payload.SkillId);
        }

        /// <summary>记录伤害。</summary>
        /// <param name="payload">载荷。</param>
        private void OnDamageDealt(DamageDealtPayload payload)
        {
            m_events.Add("damage:" + payload.Applied);
        }

        /// <summary>记录怪物死亡。</summary>
        /// <param name="payload">载荷。</param>
        private void OnMonsterDied(MonsterDiedPayload payload)
        {
            m_events.Add("monsterDied:" + payload.MonsterConfigId);
            m_monsterDeaths.Add(payload);
        }

        /// <summary>记录英雄死亡。</summary>
        /// <param name="payload">载荷。</param>
        private void OnHeroDied(HeroDiedPayload payload)
        {
            m_events.Add("heroDied:" + payload.HeroConfigId);
            m_heroDeaths.Add(payload);
        }

        // ====================================================================
        //  一、刷怪
        // ====================================================================

        /// <summary>按表刷怪：血量/攻击/技能都来自配置。</summary>
        [Test]
        public void SpawnMonster_TakesStatsFromConfig()
        {
            BattleAgent wolf = m_world.SpawnMonster(6001);

            Assert.AreEqual(EBattleAgentKind.Monster, wolf.Kind);
            Assert.AreEqual(6001, wolf.ConfigId);
            Assert.AreEqual("野狼", wolf.Name);
            Assert.AreEqual(300, wolf.MaxHp);
            Assert.AreEqual(300, wolf.Hp);
            Assert.AreEqual(20, wolf.Attack);
            CollectionAssert.AreEqual(new[] { 2003 }, wolf.SkillIds);
            Assert.AreEqual(1, m_world.AgentCount);
            Assert.AreEqual(1, m_world.AliveMonsterCount);
        }

        /// <summary>
        /// **同一个配置编号刷两只 = 两个独立对象**（各挨各的打）。
        /// <para>这条就是"实例编号 ≠ 配置编号"的理由。</para>
        /// </summary>
        [Test]
        public void SpawnMonster_TwiceSameConfig_AreIndependentInstances()
        {
            BattleAgent first = m_world.SpawnMonster(6001);
            BattleAgent second = m_world.SpawnMonster(6001);

            Assert.AreNotEqual(first.InstanceId, second.InstanceId, "实例编号必须唯一");

            m_world.ApplyDamage(0, first.InstanceId, 100);

            Assert.AreEqual(200, first.Hp);
            Assert.AreEqual(300, second.Hp, "打 A 不该影响 B");
            Assert.AreEqual(2, m_world.AgentCount);
        }

        /// <summary>刷不存在的怪：**当场报错**（不刷一个空怪出来）。</summary>
        [Test]
        public void SpawnMonster_UnknownId_ThrowsActionableMessage()
        {
            System.InvalidOperationException exception =
                Assert.Throws<System.InvalidOperationException>(() => m_world.SpawnMonster(9999));

            StringAssert.Contains("9999", exception.Message);
            StringAssert.Contains("Monster", exception.Message);
            Assert.AreEqual(0, m_world.AgentCount);
        }

        /// <summary>刷英雄：血量来自 Hero 表，**攻击是 0**（英雄伤害来自技能）。</summary>
        [Test]
        public void SpawnHero_TakesHpFromConfigAndHasZeroAttack()
        {
            BattleAgent hero = m_world.SpawnHero(1001);

            Assert.AreEqual(EBattleAgentKind.Hero, hero.Kind);
            Assert.AreEqual(1200, hero.Hp);
            Assert.AreEqual(0, hero.Attack, "Hero 表没有 attack 列，英雄的伤害来自技能");
            CollectionAssert.AreEqual(new[] { 2001, 2002 }, hero.SkillIds);
        }

        /// <summary>按配置编号找场上的怪（演示脚本用）。</summary>
        [Test]
        public void TryFindFirstMonster_FindsByConfigId()
        {
            m_world.SpawnMonster(6002);
            BattleAgent wolf = m_world.SpawnMonster(6001);

            BattleAgent found;
            Assert.IsTrue(m_world.TryFindFirstMonster(6001, out found));
            Assert.AreEqual(wolf.InstanceId, found.InstanceId);

            Assert.IsFalse(m_world.TryFindFirstMonster(6003, out found));
            Assert.IsNull(found);
        }

        // ====================================================================
        //  二、结算与广播
        // ====================================================================

        /// <summary>扣血会广播 `Battle.DamageDealt`，载荷里的数字要对得上。</summary>
        [Test]
        public void ApplyDamage_BroadcastsDamageDealtWithNumbers()
        {
            BattleAgent wolf = m_world.SpawnMonster(6001);

            m_world.ApplyDamage(0, wolf.InstanceId, 80);

            Assert.AreEqual(220, wolf.Hp);
            CollectionAssert.AreEqual(new[] { "damage:80" }, m_events);
        }

        /// <summary>致死那一下：广播死亡事件、**从场上摘掉**、击杀者编号正确。</summary>
        [Test]
        public void ApplyDamage_Lethal_BroadcastsDeathAndRemovesFromWorld()
        {
            BattleAgent hero = m_world.SpawnHero(1001);
            BattleAgent wolf = m_world.SpawnMonster(6001);

            m_world.ApplyDamage(hero.InstanceId, wolf.InstanceId, 300);

            CollectionAssert.AreEqual(new[] { "damage:300", "monsterDied:6001" }, m_events);
            Assert.AreEqual(1, m_monsterDeaths.Count);
            Assert.AreEqual(6001, m_monsterDeaths[0].MonsterConfigId);
            Assert.AreEqual(wolf.InstanceId, m_monsterDeaths[0].InstanceId);
            Assert.AreEqual(hero.InstanceId, m_monsterDeaths[0].KillerInstanceId);

            BattleAgent gone;
            Assert.IsFalse(m_world.TryGetAgent(wolf.InstanceId, out gone),
                "死的那一刻就该从世界摘掉 —— 回调里查\"还剩几只怪\"才会得到符合直觉的答案");
            Assert.AreEqual(0, m_world.AliveMonsterCount);
            Assert.AreEqual(1, m_world.AgentCount, "英雄还在场");
        }

        /// <summary>
        /// **不变量：在场 ⇒ 活着。**
        /// <para>
        /// 第一版我在 `BattleWorld` 里写了"目标已经死了就什么都不做"的分支，
        /// 写完才发现**它永远走不到**（死的单位当场就被摘掉了），于是删掉了那个分支，
        /// 并把这条变成不变量 —— 由本用例钉住。
        /// </para>
        /// <para>一个永远走不到的分支比没有分支更糟：读代码的人会以为存在那种状态。</para>
        /// </summary>
        [Test]
        public void World_NeverHoldsDeadAgent()
        {
            // ⚠️ 用**法师（1002）**：只有他配置里有穿心箭 2003。
            //    技能归属是配置驱动的（`Hero.skillIds`），`CastSkill` 会校验它 ——
            //    所以"拿剑士去放穿心箭"会被当场拒绝（有用例专门验这条）。
            BattleAgent hero = m_world.SpawnHero(1002);
            BattleAgent first = m_world.SpawnMonster(6001);
            BattleAgent second = m_world.SpawnMonster(6002);

            // 打死第一只（300 血，穿心箭 120 -> 要点三下；**算术要算出来**）
            m_world.CastSkill(hero.InstanceId, 2003, first.InstanceId);
            m_world.CastSkill(hero.InstanceId, 2003, first.InstanceId);
            m_world.CastSkill(hero.InstanceId, 2003, first.InstanceId);

            BattleAgent gone;
            Assert.IsFalse(m_world.TryGetAgent(first.InstanceId, out gone),
                "死掉的单位必须**立刻**离开世界（不能以 0 血的状态留着）");

            // 把**每一个**在场单位都查一遍：不允许出现 IsAlive == false
            Assert.IsTrue(m_world.TryGetAgent(hero.InstanceId, out hero));
            Assert.IsTrue(m_world.TryGetAgent(second.InstanceId, out second));
            Assert.IsTrue(hero.IsAlive, "英雄全程没挨打（这三下是打怪的）");
            Assert.IsTrue(second.IsAlive, "蜘蛛没被打过");
            Assert.AreEqual(2, m_world.AgentCount, "场上剩 2 个（英雄 + 蜘蛛）");
            Assert.AreEqual(1, m_world.AliveMonsterCount);
        }

        /// <summary>
        /// **"尸体被打"这件事由血量那一层防**：同一个人被连续打死两次，第二次是空操作。
        /// <para>
        /// 世界这一层走不到这种情况（死即摘掉），但 `BattleAgent` / `DamageMath`
        /// 是可能被连着调两次的（那才是真正持有血量的地方）。
        /// </para>
        /// </summary>
        [Test]
        public void Agent_SecondLethalHitIsNoOp()
        {
            BattleAgent wolf = m_world.SpawnMonster(6001);

            NBC.Shared.Battle.DamageOutcome first = wolf.ApplyDamage(300);
            Assert.IsTrue(first.IsLethal);
            Assert.AreEqual(0, wolf.Hp);
            Assert.IsFalse(wolf.IsAlive);

            NBC.Shared.Battle.DamageOutcome second = wolf.ApplyDamage(300);

            Assert.AreEqual(0, second.Applied, "对 0 血的单位不该再扣血");
            Assert.IsFalse(second.IsLethal, "不能\"再死一次\" —— 否则击杀数会凭空多出来");
            Assert.AreEqual(0, wolf.Hp);
        }

        /// <summary>对不在场的实例做操作：抛异常，且消息里点出两个常见原因。</summary>
        [Test]
        public void ApplyDamage_UnknownInstance_Throws()
        {
            System.InvalidOperationException exception =
                Assert.Throws<System.InvalidOperationException>(() => m_world.ApplyDamage(0, 4242, 10));

            StringAssert.Contains("4242", exception.Message);
            StringAssert.Contains("已经死了", exception.Message);
        }

        /// <summary>英雄死亡走的是 `Battle.HeroDied`，**不是** `MonsterDied`。</summary>
        [Test]
        public void ApplyDamage_HeroLethal_BroadcastsHeroDiedNotMonsterDied()
        {
            BattleAgent hero = m_world.SpawnHero(1002);

            m_world.ApplyDamage(0, hero.InstanceId, 800);

            Assert.AreEqual(1, m_heroDeaths.Count);
            Assert.AreEqual(0, m_monsterDeaths.Count, "英雄死了不能被当成怪物击杀");
            Assert.AreEqual(1002, m_heroDeaths[0].HeroConfigId);
        }

        // ====================================================================
        //  三、技能：事件顺序是**契约**
        // ====================================================================

        /// <summary>
        /// 技能命中的事件顺序：`SkillHit` → `DamageDealt` → （致死时）`MonsterDied`。
        /// <para>
        /// ⚠️ 顺序不写死会怎样：条件系统按事件累加进度。
        /// 如果"击杀"先于"命中"发出，一个"先命中 3 次再击杀 1 只"的任务
        /// 在**同一只怪**上会先完成击杀条件 —— 玩家看到的是"我先杀死了它"。
        /// </para>
        /// <para>
        /// ⚠️ 算术要算出来（这里 300 血挨 120 要点**三下**才死：
        /// 300 → 180 → 60 → 0）。第一版我把第二下写成了致死，用例本身是错的。
        /// </para>
        /// </summary>
        [Test]
        public void CastSkill_EventOrderIsSkillHitThenDamageThenDeath()
        {
            BattleAgent hero = m_world.SpawnHero(1002);   // 法师：配置里有穿心箭 2003
            BattleAgent wolf = m_world.SpawnMonster(6001);

            // 第一下：300 -> 180
            SkillCastOutcome first = m_world.CastSkill(hero.InstanceId, 2003, wolf.InstanceId);

            Assert.AreEqual(120, first.AppliedDamage);
            Assert.AreEqual(180, first.RemainingHp);
            Assert.IsFalse(first.Killed);
            CollectionAssert.AreEqual(new[] { "hit:2003", "damage:120" }, m_events);

            // 第二下：180 -> 60（**还没死**）
            m_events.Clear();
            SkillCastOutcome second = m_world.CastSkill(hero.InstanceId, 2003, wolf.InstanceId);

            Assert.IsFalse(second.Killed);
            Assert.AreEqual(60, second.RemainingHp);
            CollectionAssert.AreEqual(new[] { "hit:2003", "damage:120" }, m_events);

            // 第三下：60 -> 0，致死 —— 顺序仍然是 命中 -> 扣血 -> 死亡
            m_events.Clear();
            SkillCastOutcome kill = m_world.CastSkill(hero.InstanceId, 2003, wolf.InstanceId);

            Assert.IsTrue(kill.Killed);
            Assert.AreEqual(60, kill.AppliedDamage, "实扣只有 60（剩余血），不是 120");
            CollectionAssert.AreEqual(new[] { "hit:2003", "damage:60", "monsterDied:6001" }, m_events);
        }

        /// <summary>施法者不在场：抛异常。</summary>
        [Test]
        public void CastSkill_UnknownCaster_Throws()
        {
            BattleAgent wolf = m_world.SpawnMonster(6001);

            Assert.Throws<System.InvalidOperationException>(
                () => m_world.CastSkill(777, 2003, wolf.InstanceId));
        }

        /// <summary>技能表里没有这个技能：抛异常（配置错误，不猜）。</summary>
        [Test]
        public void CastSkill_UnknownSkill_Throws()
        {
            BattleAgent hero = m_world.SpawnHero(1001);
            BattleAgent wolf = m_world.SpawnMonster(6001);

            System.InvalidOperationException exception =
                Assert.Throws<System.InvalidOperationException>(
                    () => m_world.CastSkill(hero.InstanceId, 9999, wolf.InstanceId));

            StringAssert.Contains("9999", exception.Message);
            StringAssert.Contains("Skill", exception.Message);
        }

        /// <summary>
        /// **只能放自己技能列表里的技能**（归属来自配置表）。
        /// <para>
        /// 不挡的话，"按 J 放出了法师的穿心箭"会**静默生效** ——
        /// 表现是"这个技能怎么伤害不对"，而不是任何一条报错。
        /// 绑定动作的人（输入层 / AI）很容易绑错编号，所以这条规则要挡在结算入口。
        /// </para>
        /// </summary>
        [Test]
        public void CastSkill_SkillNotOwnedByCaster_Throws()
        {
            BattleAgent knight = m_world.SpawnHero(1001);   // 剑士：配置里只有 2001/2002
            BattleAgent wolf = m_world.SpawnMonster(6001);

            System.InvalidOperationException exception =
                Assert.Throws<System.InvalidOperationException>(
                    () => m_world.CastSkill(knight.InstanceId, 2003, wolf.InstanceId));

            StringAssert.Contains("2003", exception.Message);
            StringAssert.Contains("技能列表", exception.Message);
            Assert.AreEqual(300, wolf.Hp, "被拒绝时不能扣血、也不能发命中事件");
            CollectionAssert.IsEmpty(m_events);
        }

        /// <summary>技能命中里带上了目标的**配置编号**（任务条件要的是它）。</summary>
        [Test]
        public void CastSkill_HitPayloadCarriesTargetConfigId()
        {
            BattleAgent hero = m_world.SpawnHero(1002);   // 法师：有穿心箭 2003
            BattleAgent spider = m_world.SpawnMonster(6002);

            List<int> targetConfigIds = new List<int>();
            UnityEngine.Events.UnityAction<SkillHitPayload> handler =
                payload => targetConfigIds.Add(payload.TargetConfigId);

            EventCenter.Instance.AddEventListener(BattleEvents.SkillHit, handler);

            try
            {
                m_world.CastSkill(hero.InstanceId, 2003, spider.InstanceId);
            }
            finally
            {
                EventCenter.Instance.RemoveEventListener(BattleEvents.SkillHit, handler);
            }

            CollectionAssert.AreEqual(new[] { 6002 }, targetConfigIds);
        }

        // ====================================================================
        //  四、和任务系统接上（**真事件驱动条件**，V5 的核心）
        // ====================================================================

        /// <summary>
        /// 把 `Battle.MonsterDied` 用条件桥接到条件系统之后，
        /// **打死怪这个动作本身**就推进了任务进度 —— 不需要任何人手搓 `Notify`。
        /// </summary>
        [Test]
        public void MonsterDied_DrivesQuestConditionThroughBridge()
        {
            InMemoryConditionProgressStore store = new InMemoryConditionProgressStore();
            ConditionTracker tracker = new ConditionTracker(store);
            ConditionEventBridge bridge = new ConditionEventBridge(tracker);

            try
            {
                // 装配处写一行：怪物死亡 -> 击杀条件事件（目标编号取"怪物配置编号"）
                bridge.Bind<MonsterDiedPayload>(BattleEvents.MonsterDied,
                    EConditionEvent.KillMonster, payload => payload.MonsterConfigId);

                tracker.Register(4001, new ConditionDef(EConditionEvent.KillMonster, 6001, 2), true);

                BattleAgent hero = m_world.SpawnHero(1002);   // 法师：有穿心箭 2003
                BattleAgent first = m_world.SpawnMonster(6001);
                BattleAgent second = m_world.SpawnMonster(6001);

                // 第一只：打 3 下打死（每下 120，300 血）
                m_world.CastSkill(hero.InstanceId, 2003, first.InstanceId);
                m_world.CastSkill(hero.InstanceId, 2003, first.InstanceId);
                m_world.CastSkill(hero.InstanceId, 2003, first.InstanceId);

                ConditionProgress progress;
                tracker.TryGetProgress(4001, out progress);
                Assert.AreEqual(1, progress.Current, "打死了 1 只 -> 进度 1/2");
                Assert.IsFalse(progress.IsMet);

                // 第二只：死 -> 条件达成
                m_world.CastSkill(hero.InstanceId, 2003, second.InstanceId);
                m_world.CastSkill(hero.InstanceId, 2003, second.InstanceId);
                m_world.CastSkill(hero.InstanceId, 2003, second.InstanceId);

                Assert.IsTrue(tracker.IsMet(4001), "打死第二只 -> 2/2 达成");
                Assert.AreEqual(0, m_world.AliveMonsterCount);
            }
            finally
            {
                bridge.Dispose();
            }
        }

        /// <summary>打别的怪不会推进"击杀 6001"的条件（目标匹配要精确）。</summary>
        [Test]
        public void MonsterDied_OtherMonster_DoesNotAdvanceCondition()
        {
            InMemoryConditionProgressStore store = new InMemoryConditionProgressStore();
            ConditionTracker tracker = new ConditionTracker(store);
            ConditionEventBridge bridge = new ConditionEventBridge(tracker);

            try
            {
                bridge.Bind<MonsterDiedPayload>(BattleEvents.MonsterDied,
                    EConditionEvent.KillMonster, payload => payload.MonsterConfigId);

                tracker.Register(4001, new ConditionDef(EConditionEvent.KillMonster, 6001, 1), true);

                BattleAgent hero = m_world.SpawnHero(1002);   // 法师：有穿心箭 2003
                BattleAgent spider = m_world.SpawnMonster(6002);   // 200 血

                m_world.CastSkill(hero.InstanceId, 2003, spider.InstanceId);
                m_world.CastSkill(hero.InstanceId, 2003, spider.InstanceId);

                Assert.IsFalse(tracker.IsMet(4001), "打死了蜘蛛不该算\"击杀野狼\"");
            }
            finally
            {
                bridge.Dispose();
            }
        }

        /// <summary>英雄死亡**不会**推进"击杀怪物"的条件（桥只挂了 MonsterDied）。</summary>
        [Test]
        public void HeroDied_DoesNotAdvanceKillMonsterCondition()
        {
            InMemoryConditionProgressStore store = new InMemoryConditionProgressStore();
            ConditionTracker tracker = new ConditionTracker(store);
            ConditionEventBridge bridge = new ConditionEventBridge(tracker);

            try
            {
                bridge.Bind<MonsterDiedPayload>(BattleEvents.MonsterDied,
                    EConditionEvent.KillMonster, payload => payload.MonsterConfigId);

                tracker.Register(4001, new ConditionDef(EConditionEvent.KillMonster, 0, 1), true);

                BattleAgent hero = m_world.SpawnHero(1002);      // 800 血
                m_world.ApplyDamage(0, hero.InstanceId, 800);

                Assert.IsFalse(tracker.IsMet(4001),
                    "英雄死了不能被当成\"击杀了任意怪\" —— 这就是两个事件而不是一个带 kind 的理由");
            }
            finally
            {
                bridge.Dispose();
            }
        }
    }
}
