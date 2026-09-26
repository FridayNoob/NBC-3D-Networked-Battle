// ============================================================================
//  BattleSession —— 一局单机 PVE 的**装配**：把配置、战斗、任务、输入接成一个闭环
//  项目：3D联网战斗Demo   对应：M2-C2（端到端闭环）、Docs\20 §三 M2 的验收线
//
//  ---------------------------------------------------------------------------
//  它是"装配"，不是"新逻辑"（这一点必须说清，否则会变成第二个上帝类）
//  ---------------------------------------------------------------------------
//  本类**自己不算任何一件事**：
//
//      伤害怎么算     -> Shared\Battle\DamageMath
//      谁在场/谁死了  -> BattleWorld
//      条件累加       -> Shared\Condition\ConditionTracker
//      任务的接/交/发奖 -> QuestRuntime
//      键位到技能     -> SkillCaster
//
//  它只做**三件装配的事**：
//      ① 按依赖顺序把上面这些造出来（谁需要谁就先造谁）
//      ② 把该接的线接上（事件桥：怪物死亡 -> 击杀条件；进区域 -> 到达条件）
//      ③ 提供一层"给调用方用"的门面（接任务 / 放技能 / 交任务 / 进区域）
//
//  📌 判据：**如果本类里出现了"判断"或"计算"，那它多半放错了地方。**
//     这条判据在面试里很好用 —— 它界定了"装配层"和"逻辑层"的边界。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么是纯 C# 类，而不是 MonoBehaviour
//  ---------------------------------------------------------------------------
//  因为**整条端到端闭环要能在 EditMode 里跑**（不需要 Unity、不需要帧循环）：
//
//      EditMode 用例：造一个 BattleSession -> 接任务 -> 打三下 -> 交任务 -> 断言奖励到账
//
//  而 MonoBehaviour 那一侧（读键盘、建场景物体、画 HUD）是**另一件事**，
//  在 `M2DemoBehaviour` 里。两者分开之后，**闭环的正确性**和**能不能点着玩**
//  就变成两个可以各自验证的东西。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 目标选择的简化（M2 明确不做锁定/选怪 UI）
//  ---------------------------------------------------------------------------
//  `CurrentTargetInstanceId` 由 `SelectFirstAliveMonster` 决定（"打第一只活着的怪"）。
//  真正的目标选择（点选 / 自动锁定 / 最近敌人）属 M3 的战斗手感。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Input;
using NBC.Game.Achievement;
using NBC.Game.Battle;
using NBC.Game.Config;
using NBC.Game.Quest;
using NBC.Game.World;
using NBC.Shared.Condition;

namespace NBC.Game.GameFlow
{
    /// <summary>一局单机 PVE 的装配与门面。</summary>
    public sealed class BattleSession : IDisposable
    {
        /// <summary>条件系统（任务与成就共用；M2 只有任务在用）。</summary>
        private readonly ConditionTracker m_conditions;

        /// <summary>事件桥（游戏事件 → 条件事件）。</summary>
        private readonly ConditionEventBridge m_bridge;

        /// <summary>战斗世界。</summary>
        private readonly BattleWorld m_world;

        /// <summary>任务运行时。</summary>
        private readonly QuestRuntime m_quests;

        /// <summary>
        /// 成就运行时（**可以是 null** —— 没给成就表时就没有成就，见 ctor 重载的说明）。
        /// </summary>
        private readonly AchievementRuntime m_achievements;

        /// <summary>输入 → 技能。</summary>
        private readonly SkillCaster m_caster;

        /// <summary>玩家单位。</summary>
        private readonly BattleAgent m_hero;

        /// <summary>当前目标（0 = 没有目标）。</summary>
        private int m_targetInstanceId;

        /// <summary>目标选择用的复用缓冲（避免每次选目标都分配一个列表）。</summary>
        private readonly List<BattleAgent> m_aliveScratch = new List<BattleAgent>();

        /// <summary>Dispose 过没有（重复 Dispose 要无害）。</summary>
        private bool m_disposed;

        /// <summary>
        /// 装配一局（**不带成就** —— 等价于 <paramref name="achievements"/> 传 null）。
        /// <para>保留这个重载是为了"这个项目不做成就也能跑"，也让 M2 那批调用点一行都不改。</para>
        /// </summary>
        /// <param name="quests">任务表。</param>
        /// <param name="conditions">条件表。</param>
        /// <param name="rewards">奖励表。</param>
        /// <param name="monsters">怪物表。</param>
        /// <param name="heroes">英雄表。</param>
        /// <param name="skills">技能表。</param>
        /// <param name="heroId">玩家用哪个英雄（`Hero` 表主键）。</param>
        /// <param name="rewardSink">发奖实现（M2 内存版；M4 换服务端权威）。</param>
        public BattleSession(QuestConfig quests, QuestConditionConfig conditions, RewardConfig rewards,
                             MonsterConfig monsters, HeroConfig heroes, SkillConfig skills,
                             int heroId, IQuestRewardSink rewardSink)
            : this(quests, conditions, rewards, monsters, heroes, skills, null, heroId, rewardSink)
        {
        }

        /// <summary>
        /// 装配一局。
        /// <para>⚠️ 参数是**六~七张表 + 一个英雄编号**：本类刻意不自己去 `ConfigMgr` 拿，
        /// 那样它就能在 EditMode 里用假表跑（同 M2-A / M2-B 的做法）。</para>
        /// </summary>
        /// <param name="quests">任务表。</param>
        /// <param name="conditions">条件表。</param>
        /// <param name="rewards">奖励表。</param>
        /// <param name="monsters">怪物表。</param>
        /// <param name="heroes">英雄表。</param>
        /// <param name="skills">技能表。</param>
        /// <param name="achievements">成就表；**传 null 表示这一局不做成就**。</param>
        /// <param name="heroId">玩家用哪个英雄（`Hero` 表主键）。</param>
        /// <param name="rewardSink">发奖实现（M2 内存版；M4 换服务端权威）。</param>
        public BattleSession(QuestConfig quests, QuestConditionConfig conditions, RewardConfig rewards,
                             MonsterConfig monsters, HeroConfig heroes, SkillConfig skills,
                             AchievementConfig achievements,
                             int heroId, IQuestRewardSink rewardSink)
        {
            // -------- ① 造依赖（顺序就是依赖顺序） --------
            m_world = new BattleWorld(monsters, heroes, skills);
            m_hero = m_world.SpawnHero(heroId);

            m_conditions = new ConditionTracker(new InMemoryConditionProgressStore());
            m_quests = new QuestRuntime(quests, conditions, rewards, m_conditions, rewardSink);

            // ⚠️ 成就与任务**共用同一个 `ConditionTracker`**（`Docs\20` §四 的核心复用）——
            //    所以这里传的是 `m_conditions`，**不是新造一个**。
            //    造第二个 tracker 的后果：任务打死的怪不涨成就进度，而**没有任何报错**。
            //
            // ⚠️ 顺序：成就必须在**桥接线之前**造好。成就是"构造即登记全部条件"，
            //    而桥一旦开始喂事件，还没登记的条件就会被漏掉 —— 这个洞很隐蔽
            //    （表现是"成就好像少算了前几只怪"）。
            m_achievements = achievements == null
                ? null
                : new AchievementRuntime(achievements, conditions, rewards, m_conditions, rewardSink);

            m_caster = new SkillCaster(m_world, m_hero.InstanceId);

            // -------- ② 接线：游戏事件 -> 条件事件 --------
            // 桥是**唯一**同时认识"战斗/世界"和"条件系统"的地方 —— 战斗模块不知道任务存在
            m_bridge = new ConditionEventBridge(m_conditions);
            m_bridge.Bind<MonsterDiedPayload>(BattleEvents.MonsterDied,
                EConditionEvent.KillMonster, payload => payload.MonsterConfigId);
            m_bridge.Bind<SkillHitPayload>(BattleEvents.SkillHit,
                EConditionEvent.UseSkill, payload => payload.SkillId);
            m_bridge.Bind<AreaEnteredPayload>(WorldEvents.AreaEntered,
                EConditionEvent.ReachArea, payload => payload.AreaId);

            // M4-S1：掉落也算一条链路（"收集 N 个物品"）。
            // ⚠️ 这里**不判归属** —— `ItemDropped` 本身就是"归我的掉落"
            //    （归属判断在 `ServerEventBridge` 里，见那个文件头）。
            //    若在这里再写一次归属判断，就成了"同一规则两处实现"，两边迟早不一致。
            m_bridge.Bind<ItemDroppedPayload>(BattleEvents.ItemDropped,
                EConditionEvent.CollectItem, payload => payload.ItemId);
        }

        /// <summary>战斗世界（演示/调试要看在场单位时用）。</summary>
        public BattleWorld World
        {
            get { return m_world; }
        }

        /// <summary>
        /// 从 `ConfigMgr` 装配一局（**运行时的入口**）。
        /// <para>
        /// ⚠️ 它必须在**配置预加载完成之后**调用（`GameTables.PreloadAll` 的回调里）——
        /// `ConfigMgr.Get&lt;T&gt;()` 在表没加载时**抛异常**，而且报错会明确告诉你
        /// "忘了预加载"（M1-C4 定的报错三要素）。
        /// </para>
        /// <para>
        /// ⚠️ 成就表**故意用 `TryGet`**（而不是 `Get`）：缺成就表时这一局就没有成就
        /// （<see cref="Achievements"/> 为 null），但**其余部分照常可玩**。
        /// 理由：成就是"锦上添花"，不该因为它没导入就让整局起不来。
        /// 而"表没加载"这件事**不会静默** —— `GameTables.PreloadAll` 的失败回调会先报一次，
        /// 调试面板也会把"成就：未加载"写在明面上（见 `NetDebugWindow`）。
        /// </para>
        /// </summary>
        /// <param name="heroId">玩家用哪个英雄。</param>
        /// <param name="rewardSink">发奖实现。</param>
        /// <returns>装配好的一局。</returns>
        public static BattleSession FromConfigMgr(int heroId, IQuestRewardSink rewardSink)
        {
            AchievementConfig achievements;
            ConfigMgr.Instance.TryGet<AchievementConfig>(out achievements);

            return new BattleSession(
                ConfigMgr.Instance.Get<QuestConfig>(),
                ConfigMgr.Instance.Get<QuestConditionConfig>(),
                ConfigMgr.Instance.Get<RewardConfig>(),
                ConfigMgr.Instance.Get<MonsterConfig>(),
                ConfigMgr.Instance.Get<HeroConfig>(),
                ConfigMgr.Instance.Get<SkillConfig>(),
                achievements,
                heroId,
                rewardSink);
        }

        /// <summary>任务运行时（UI 要接它的状态与事件）。</summary>
        public QuestRuntime Quests
        {
            get { return m_quests; }
        }

        /// <summary>
        /// 成就运行时（**没给成就表时是 null** —— 调用方必须先判 null，见 `FromConfigMgr`）。
        /// </summary>
        public AchievementRuntime Achievements
        {
            get { return m_achievements; }
        }

        /// <summary>条件系统（调试面板要看进度时用）。</summary>
        public ConditionTracker Conditions
        {
            get { return m_conditions; }
        }

        /// <summary>输入 → 技能（装配处在这里 `Bind` 键位）。</summary>
        public SkillCaster Caster
        {
            get { return m_caster; }
        }

        /// <summary>玩家单位。</summary>
        public BattleAgent Hero
        {
            get { return m_hero; }
        }

        /// <summary>当前目标实例编号（0 = 没有目标）。</summary>
        public int CurrentTargetInstanceId
        {
            get { return m_targetInstanceId; }
        }

        // ====================================================================
        //  门面：一局里玩家会做的四件事
        // ====================================================================

        /// <summary>进关卡：刷几只怪，并广播"进入区域"（于是"到达区域"类条件会推进）。</summary>
        /// <param name="monsterId">刷哪种怪。</param>
        /// <param name="count">刷几只（必须 ≥ 1）。</param>
        /// <param name="areaId">进入哪个区域（0 = 不广播区域事件）。</param>
        /// <returns>刷出来的怪（按刷的顺序）。</returns>
        public List<BattleAgent> EnterLevel(int monsterId, int count, int areaId)
        {
            EnsureNotDisposed();

            if (count < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(count),
                    "[BattleSession] 刷怪数量必须 ≥ 1（当前 " + count + "）。");
            }

            List<BattleAgent> spawned = new List<BattleAgent>(count);

            for (int i = 0; i < count; i++)
            {
                spawned.Add(m_world.SpawnMonster(monsterId));
            }

            SelectFirstAliveMonster();

            if (areaId != 0)
            {
                EventCenter.Instance.Trigger(WorldEvents.AreaEntered, new AreaEnteredPayload(areaId));
            }

            return spawned;
        }

        /// <summary>把目标设成"第一只还活着的怪"（M2 的目标选择简化，见文件头）。</summary>
        /// <returns>选到了返回 true（场上有活怪）。</returns>
        public bool SelectFirstAliveMonster()
        {
            // 复用同一个 buffer（不在每帧分配）；见 `BattleWorld.CopyAliveMonsters` 的说明
            m_aliveScratch.Clear();
            m_world.CopyAliveMonsters(m_aliveScratch);

            if (m_aliveScratch.Count == 0)
            {
                m_targetInstanceId = 0;
                return false;
            }

            m_targetInstanceId = m_aliveScratch[0].InstanceId;
            return true;
        }

        /// <summary>接任务。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>结果（失败时带原因）。</returns>
        public QuestActionResult AcceptQuest(int questId)
        {
            EnsureNotDisposed();
            return m_quests.Accept(questId);
        }

        /// <summary>交任务（发奖）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>结果（失败时带原因）。</returns>
        public QuestActionResult SubmitQuest(int questId)
        {
            EnsureNotDisposed();
            return m_quests.Submit(questId);
        }

        /// <summary>
        /// 处理一帧输入：把"本帧新按下"的技能键放出去。
        /// <para>
        /// ⚠️ **不变量：`CurrentTargetInstanceId` 要么是 0，要么指向一只活怪。**
        /// 所以打完这一帧之后如果目标死了，**当场**就换下一个（而不是等下一次按键）——
        /// 否则 UI 会显示"当前目标 #2"，而 #2 已经不在场上了（一个查起来很费劲的显示 bug）。
        /// </para>
        /// </summary>
        /// <param name="command">这一帧的输入命令。</param>
        /// <param name="results">释放结果会追加进来（可以为 null）。</param>
        /// <returns>这一帧放了几发。</returns>
        public int HandleInput(InputCommand command, List<SkillCastOutcome> results)
        {
            EnsureNotDisposed();

            if (m_targetInstanceId == 0 || !IsAliveTarget(m_targetInstanceId))
            {
                if (!SelectFirstAliveMonster())
                {
                    return 0;   // 场上没活怪：这一帧什么都不做（**不是错误**）
                }
            }

            int cast = m_caster.Handle(command, m_targetInstanceId, results);

            // 打死了就当场换目标（维持上面那条不变量）
            if (cast > 0 && !IsAliveTarget(m_targetInstanceId))
            {
                SelectFirstAliveMonster();
            }

            return cast;
        }

        /// <summary>把一局的状态拼成一段文本（控制台/HUD 用）。</summary>
        /// <returns>多行文本。</returns>
        public string Describe()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("[BattleSession] ").Append(m_hero.ToString()).Append('\n');
            builder.Append("  场上活怪：").Append(m_world.AliveMonsterCount)
                   .Append("，当前目标 #").Append(m_targetInstanceId).Append('\n');
            builder.Append(m_quests.DescribeActive());

            if (m_achievements != null)
            {
                builder.Append(m_achievements.Describe());
            }
            else
            {
                builder.Append("[AchievementRuntime] 这一局没有成就（没给成就表）。\n");
            }

            return builder.ToString();
        }

        /// <summary>退订事件、注销条件（可以重复调用）。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;

            // 顺序：先拆"订阅方"，再拆"被订阅方"，最后清"共享状态"
            // （`SkillCaster` 不订阅事件，所以没有它的退订动作）
            m_bridge.Dispose();

            // ⚠️ 成就与任务**都要 Dispose**，而且都在 `m_conditions.Clear()` **之前**：
            //    它们各自持有条件登记，`Clear()` 是"连登记表一起清掉"的粗粒度兜底。
            //    顺序反了不会立刻出错（Clear 之后 Unregister 只是返回 false），
            //    但会让"谁登记了什么"这笔账**在本类里对不上** —— 保持"谁登记谁注销"。
            if (m_achievements != null)
            {
                m_achievements.Dispose();
            }

            m_quests.Dispose();
            m_conditions.Clear();
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>目标还活着吗。</summary>
        /// <param name="instanceId">实例编号。</param>
        /// <returns>活着返回 true。</returns>
        private bool IsAliveTarget(int instanceId)
        {
            BattleAgent agent;
            return m_world.TryGetAgent(instanceId, out agent) && agent.IsAlive;
        }

        /// <summary>Dispose 之后不许再用。</summary>
        private void EnsureNotDisposed()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException(nameof(BattleSession),
                    "[BattleSession] 已经 Dispose 了，不能再调用。");
            }
        }
    }
}
