// ============================================================================
//  M2-C2 · **端到端闭环**测试：进关卡 -> 打怪 -> 任务完成 -> 交付 -> 奖励到账
//  对应验收：Docs\22-M2开工清单.md C2、Docs\20 §三 M2 的验收线（V5/V6）
//
//  ---------------------------------------------------------------------------
//  这一组和前面几组的区别：**它不测任何单个零件**
//  ---------------------------------------------------------------------------
//      M2-A：条件系统 + 任务运行时（各自的用例）
//      M2-B：伤害结算 + 战斗世界 + 输入→技能（各自的用例）
//      本文件：**上面全部接在一起**，用"玩家会做的四件事"驱动：
//
//          进关卡（EnterLevel） -> 接任务（AcceptQuest） -> 打怪（HandleInput） -> 交任务（SubmitQuest）
//
//  ⚠️ 关键：全程**没有一句手搓的 `Notify`**。
//     进度是靠 `Battle.MonsterDied` / `World.AreaEntered` 这两个**真事件**，
//     经事件桥流进条件系统的 —— 这才叫"真事件驱动"（V5 的原话）。
//
//  📌 它能在 EditMode 里跑，靠的是前面每一个模块都留了接缝：
//     假的配置资产（不需要打包）、纯 C# 的 session（不需要帧循环）、
//     手搓的 `InputCommand`（不需要键盘）。
// ============================================================================

using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Input;
using NBC.Game.Battle;
using NBC.Game.Config;
using NBC.Game.GameFlow;
using NBC.Game.Quest;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>M2-C2：单机 PVE + 任务系统的端到端闭环。</summary>
    public sealed class M2EndToEndTests
    {
        /// <summary>演示任务的编号（击杀 3 只野狼 + 抵达一号区域）。</summary>
        private const int DemoQuest = 3004;

        /// <summary>演示任务的两条条件。</summary>
        private const int KillWolfCondition = 4007;

        /// <summary>演示任务的区域条件。</summary>
        private const int ReachAreaCondition = 4008;

        /// <summary>演示任务的奖励。</summary>
        private const int DemoReward = 5001;

        /// <summary>任务表。</summary>
        private QuestConfig m_quests;

        /// <summary>条件表。</summary>
        private QuestConditionConfig m_conditions;

        /// <summary>奖励表。</summary>
        private RewardConfig m_rewards;

        /// <summary>怪物表。</summary>
        private MonsterConfig m_monsters;

        /// <summary>英雄表。</summary>
        private HeroConfig m_heroes;

        /// <summary>技能表。</summary>
        private SkillConfig m_skills;

        /// <summary>发奖实现（要断言"到账的数字"）。</summary>
        private InMemoryQuestRewardSink m_sink;

        /// <summary>被测的一局。</summary>
        private BattleSession m_session;

        /// <summary>记录：收到过哪些任务事件。</summary>
        private List<string> m_questEvents;

        /// <summary>技能键。</summary>
        private InputActionId m_skill1;

        // ====================================================================
        //  夹具
        // ====================================================================

        /// <summary>每个用例前：造表、装一局。</summary>
        [SetUp]
        public void SetUp()
        {
            EventCenter.Instance.WarnOnMissingListener = false;

            InputActionId.ResetDeclarationsForTests();
            m_skill1 = InputActionId.Declare(0, "Skill1");

            m_questEvents = new List<string>();

            m_skills = BattleTestTables.CreateSkills();
            m_heroes = BattleTestTables.CreateHeroes();
            m_monsters = BattleTestTables.CreateMonsters();
            BuildQuestTables();

            m_sink = new InMemoryQuestRewardSink();

            // 法师（1002）：800 血，配置里有穿心箭 2003（120 伤害）
            m_session = new BattleSession(m_quests, m_conditions, m_rewards,
                                         m_monsters, m_heroes, m_skills,
                                         BattleTestTables.Mage, m_sink);

            m_session.Caster.Bind(m_skill1, BattleTestTables.PiercingArrow);

            EventCenter.Instance.AddEventListener<int>(QuestEvents.Completed, OnCompleted);
            EventCenter.Instance.AddEventListener<int>(QuestEvents.Submitted, OnSubmitted);
        }

        /// <summary>每个用例后：拆一局、销毁资产。</summary>
        [TearDown]
        public void TearDown()
        {
            if (m_session != null)
            {
                m_session.Dispose();
                m_session = null;
            }

            EventCenter.Instance.ClearListeners(QuestEvents.Completed);
            EventCenter.Instance.ClearListeners(QuestEvents.Submitted);
            EventCenter.Instance.ClearListeners(QuestEvents.Rejected);
            EventCenter.Instance.WarnOnMissingListener = true;

            BattleTestTables.Destroy(m_quests);
            BattleTestTables.Destroy(m_conditions);
            BattleTestTables.Destroy(m_rewards);
            BattleTestTables.Destroy(m_monsters);
            BattleTestTables.Destroy(m_heroes);
            BattleTestTables.Destroy(m_skills);

            InputActionId.ResetDeclarationsForTests();
        }

        /// <summary>造任务 / 条件 / 奖励三张表。</summary>
        private void BuildQuestTables()
        {
            m_conditions = ScriptableObject.CreateInstance<QuestConditionConfig>();
            m_conditions.rows = new List<Config_QuestCondition>
            {
                Condition(KillWolfCondition, NBC.Shared.Condition.EConditionEvent.KillMonster,
                          BattleTestTables.Wolf, 3),
                Condition(ReachAreaCondition, NBC.Shared.Condition.EConditionEvent.ReachArea, 1, 1)
            };
            m_conditions.RebuildIndex();

            m_rewards = ScriptableObject.CreateInstance<RewardConfig>();
            m_rewards.rows = new List<Config_Reward> { Reward(DemoReward, 100, 50, 7001, 2) };
            m_rewards.RebuildIndex();

            m_quests = ScriptableObject.CreateInstance<QuestConfig>();
            m_quests.rows = new List<Config_Quest>
            {
                Quest(DemoQuest, "清剿野狼", new[] { KillWolfCondition, ReachAreaCondition }, DemoReward)
            };
            m_quests.RebuildIndex();
        }

        /// <summary>造一行条件。</summary>
        /// <param name="id">编号。</param>
        /// <param name="eventType">事件类型。</param>
        /// <param name="targetId">目标编号。</param>
        /// <param name="required">需要数量。</param>
        /// <returns>行。</returns>
        private static Config_QuestCondition Condition(int id, NBC.Shared.Condition.EConditionEvent eventType,
                                                      int targetId, int required)
        {
            Config_QuestCondition row = new Config_QuestCondition();
            row.id = id;
            row.eventType = eventType;
            row.targetId = targetId;
            row.requiredCount = required;
            row.note = "端到端测试";
            return row;
        }

        /// <summary>造一行奖励。</summary>
        /// <param name="id">编号。</param>
        /// <param name="exp">经验。</param>
        /// <param name="gold">金币。</param>
        /// <param name="itemId">物品编号。</param>
        /// <param name="itemCount">物品数量。</param>
        /// <returns>行。</returns>
        private static Config_Reward Reward(int id, int exp, int gold, int itemId, int itemCount)
        {
            Config_Reward row = new Config_Reward();
            row.id = id;
            row.exp = exp;
            row.gold = gold;
            row.itemId = itemId;
            row.itemCount = itemCount;
            return row;
        }

        /// <summary>造一行任务。</summary>
        /// <param name="id">编号。</param>
        /// <param name="name">名称。</param>
        /// <param name="conditionIds">条件编号。</param>
        /// <param name="rewardId">奖励编号。</param>
        /// <returns>行。</returns>
        private static Config_Quest Quest(int id, string name, int[] conditionIds, int rewardId)
        {
            Config_Quest row = new Config_Quest();
            row.id = id;
            row.name = name;
            row.desc = name;
            row.conditionIds = conditionIds;
            row.rewardId = rewardId;
            return row;
        }

        /// <summary>记录任务完成。</summary>
        /// <param name="questId">任务编号。</param>
        private void OnCompleted(int questId) { m_questEvents.Add("completed:" + questId); }

        /// <summary>记录任务交付。</summary>
        /// <param name="questId">任务编号。</param>
        private void OnSubmitted(int questId) { m_questEvents.Add("submitted:" + questId); }

        /// <summary>造一个"技能键本帧新按下"的命令。</summary>
        /// <param name="tick">帧号。</param>
        /// <returns>输入命令。</returns>
        private InputCommand PressSkill(int tick)
        {
            return new InputCommand(tick, 0, 0, 1u << m_skill1.Index, 0u);
        }

        /// <summary>按一次技能键（当作"玩家点了一下"）。</summary>
        /// <param name="tick">帧号。</param>
        /// <returns>放了几发。</returns>
        private int Press(int tick)
        {
            return m_session.HandleInput(PressSkill(tick), null);
        }

        // ====================================================================
        //  一、完整闭环（**M2 的验收线**）
        // ====================================================================

        /// <summary>
        /// 接任务 -> 进关卡 -> 打够 3 只野狼 -> 任务完成 -> 交付 -> 奖励到账。
        /// <para>全程**没有手搓的 `Notify`**：进度来自真实战斗事件与区域事件。</para>
        /// </summary>
        [Test]
        public void FullLoop_AcceptEnterKillCompleteSubmit_RewardArrives()
        {
            // ① 接任务
            QuestActionResult accepted = m_session.AcceptQuest(DemoQuest);
            Assert.IsTrue(accepted.Ok, accepted.Reason);
            Assert.AreEqual(EQuestState.Accepted, m_session.Quests.StateOf(DemoQuest));

            // ② 进关卡：刷 3 只野狼 + 广播"进入 1 号区域"
            List<BattleAgent> wolves = m_session.EnterLevel(BattleTestTables.Wolf, 3, 1);
            Assert.AreEqual(3, wolves.Count);

            // 区域条件（4008）应当**已经达成** —— 它来自 World.AreaEntered 事件
            Assert.IsTrue(m_session.Conditions.IsMet(ReachAreaCondition),
                "\"抵达区域\"这条条件应当被进区域事件直接满足");

            // 击杀条件（4007）还是 0/3
            NBC.Shared.Condition.ConditionProgress progress;
            m_session.Conditions.TryGetProgress(KillWolfCondition, out progress);
            Assert.AreEqual(0, progress.Current);
            Assert.AreEqual(EQuestState.Accepted, m_session.Quests.StateOf(DemoQuest));

            // ③ 打怪：每只野狼 300 血、每发 120 -> 每只 3 发
            int tick = 1;
            for (int i = 0; i < 6; i++)
            {
                Assert.AreEqual(1, Press(tick++), "每一发都该放出去");
            }

            m_session.Conditions.TryGetProgress(KillWolfCondition, out progress);
            Assert.AreEqual(2, progress.Current, "打死 2 只 -> 2/3");
            Assert.AreEqual(EQuestState.Accepted, m_session.Quests.StateOf(DemoQuest),
                "还有一条条件没满（其实这条已经满了，但击杀数没到）");

            // 打死第三只 -> 两条条件都满 -> 可交付
            for (int i = 0; i < 3; i++)
            {
                Press(tick++);
            }

            Assert.AreEqual(EQuestState.Completed, m_session.Quests.StateOf(DemoQuest));
            CollectionAssert.Contains(m_questEvents, "completed:3004");
            Assert.AreEqual(0, m_session.World.AliveMonsterCount, "三只都死了");
            Assert.AreEqual(0, m_sink.GrantCount, "**完成不等于发奖** —— 奖励要等交付");

            // ④ 交付
            QuestActionResult submitted = m_session.SubmitQuest(DemoQuest);
            Assert.IsTrue(submitted.Ok, submitted.Reason);
            Assert.AreEqual(EQuestState.Submitted, m_session.Quests.StateOf(DemoQuest));
            CollectionAssert.Contains(m_questEvents, "submitted:3004");

            // ⑤ **奖励真的到账**（断言数字，不是"调用过"）
            Assert.AreEqual(1, m_sink.GrantCount);
            Assert.AreEqual(100, m_sink.TotalExp);
            Assert.AreEqual(50, m_sink.TotalGold);
            Assert.AreEqual(2, m_sink.ItemCountOf(7001));
        }

        // ====================================================================
        //  二、反例：条件没全满 -> 不能交付
        // ====================================================================

        /// <summary>
        /// 只打怪、**不进区域**：击杀条件满了，任务仍然停在"已接取"。
        /// <para>这条验的是"**全部条件都满了才算完成**" —— 只满足一部分不算。</para>
        /// </summary>
        [Test]
        public void WithoutAreaEvent_QuestStaysAccepted()
        {
            Assert.IsTrue(m_session.AcceptQuest(DemoQuest).Ok);

            // areaId 传 0 = **不广播区域事件**
            m_session.EnterLevel(BattleTestTables.Wolf, 3, 0);

            int tick = 1;
            for (int i = 0; i < 9; i++)
            {
                Press(tick++);
            }

            Assert.IsTrue(m_session.Conditions.IsMet(KillWolfCondition), "击杀条件应当满了");
            Assert.IsFalse(m_session.Conditions.IsMet(ReachAreaCondition), "区域条件应当还没满");

            Assert.AreEqual(EQuestState.Accepted, m_session.Quests.StateOf(DemoQuest),
                "只满足一条条件不能算完成");

            QuestActionResult submitted = m_session.SubmitQuest(DemoQuest);
            Assert.IsFalse(submitted.Ok);
            StringAssert.Contains("还没做完", submitted.Reason);
            Assert.AreEqual(0, m_sink.GrantCount);
        }

        /// <summary>没接任务就打怪：击杀不会算进任何任务（接了之后才重新计）。</summary>
        [Test]
        public void KillBeforeAccept_DoesNotCount()
        {
            m_session.EnterLevel(BattleTestTables.Wolf, 1, 1);

            int tick = 1;
            for (int i = 0; i < 3; i++)
            {
                Press(tick++);
            }

            Assert.IsTrue(m_session.AcceptQuest(DemoQuest).Ok);

            NBC.Shared.Condition.ConditionProgress progress;
            m_session.Conditions.TryGetProgress(KillWolfCondition, out progress);
            Assert.AreEqual(0, progress.Current, "接取会清零 —— 事先打的怪不算");
        }

        // ====================================================================
        //  三、输入与目标的边界
        // ====================================================================

        /// <summary>场上没有怪时按技能键：什么都不发生，**也不抛异常**（不是错误）。</summary>
        [Test]
        public void Press_WithoutMonster_DoesNothing()
        {
            Assert.AreEqual(0, Press(1));
            Assert.AreEqual(0, m_session.CurrentTargetInstanceId);
        }

        /// <summary>打死当前目标后，下一次按键**自动换到下一只怪**（不需要玩家重新选目标）。</summary>
        [Test]
        public void Press_AutoRetargetsAfterKill()
        {
            List<BattleAgent> wolves = m_session.EnterLevel(BattleTestTables.Wolf, 2, 0);

            Assert.AreEqual(wolves[0].InstanceId, m_session.CurrentTargetInstanceId);

            int tick = 1;
            for (int i = 0; i < 3; i++)
            {
                Press(tick++);
            }

            Assert.IsFalse(wolves[0].IsAlive, "第一只应当已被打死");
            Assert.AreEqual(wolves[1].InstanceId, m_session.CurrentTargetInstanceId,
                "下一次按键应当已经自动换到第二只");
            Assert.AreEqual(300, wolves[1].Hp, "换目标之前不该误伤第二只");

            Press(tick);
            Assert.AreEqual(180, wolves[1].Hp);
        }

        // ====================================================================
        //  四、装配层的错误与生命周期
        // ====================================================================

        /// <summary>重复接取：给原因，不抛异常。</summary>
        [Test]
        public void AcceptTwice_SecondIsRejectedWithReason()
        {
            Assert.IsTrue(m_session.AcceptQuest(DemoQuest).Ok);

            QuestActionResult second = m_session.AcceptQuest(DemoQuest);

            Assert.IsFalse(second.Ok);
            StringAssert.Contains("已经接过", second.Reason);
        }

        /// <summary>`Describe` 能一眼看出"现在有几个任务、场上几只怪"（控制台/HUD 用）。</summary>
        [Test]
        public void Describe_ReportsHeroQuestAndMonsters()
        {
            m_session.AcceptQuest(DemoQuest);
            m_session.EnterLevel(BattleTestTables.Wolf, 2, 1);

            string text = m_session.Describe();

            StringAssert.Contains("法师", text);
            StringAssert.Contains("场上活怪：2", text);
            StringAssert.Contains("清剿野狼", text);
        }

        /// <summary>Dispose 之后调用门面方法要**明确报错**（而不是行为诡异）。</summary>
        [Test]
        public void AfterDispose_MethodsThrow()
        {
            BattleSession session = new BattleSession(m_quests, m_conditions, m_rewards,
                                                     m_monsters, m_heroes, m_skills,
                                                     BattleTestTables.Mage, m_sink);
            session.Dispose();

            Assert.Throws<System.ObjectDisposedException>(() => session.AcceptQuest(DemoQuest));
        }
    }
}
