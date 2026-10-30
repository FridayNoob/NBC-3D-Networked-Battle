// 这个文件里有：
//   BattleSession（类）：一局单机 PVE 的装配与门面，按依赖顺序造出战斗、条件、任务、成就、输入，接好事件桥，再对外提供进关卡、接任务、放技能、交任务

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
using NBC.Shared.Reward;

namespace NBC.Game.GameFlow
{
    // 主要功能：把一局单机 PVE 的依赖按顺序造出来（战斗世界、条件系统、任务运行时、成就运行时、技能施放器），把游戏事件桥接成条件事件，再对外提供进关卡、接任务、放技能、交任务四件事。
    // 主要数据成员：m_world（战斗世界）、m_conditions（任务与成就共用的条件系统）、m_quests（任务运行时）、m_achievements（成就运行时，可为 null）、m_caster（输入到技能）、m_hero（玩家单位）、m_targetInstanceId（当前目标，0 表示没有）。
    // 主要外部方法：FromConfigMgr、EnterLevel、SelectFirstAliveMonster、AcceptQuest、SubmitQuest、HandleInput、Describe、Dispose，以及 World / Quests / Achievements / Conditions / Caster / Hero / CurrentTargetInstanceId 只读属性。
    // 谁在用它：Boot 的 M2DemoBehaviour（运行时的装配点，装配完只调门面四件事）；Editor 的 NetDebugWindow（装一局跑真任务链）；EditMode 的 M2EndToEndTests 用假表装配并断言闭环。
    // 注意：本类只做装配，不做判断也不做计算 —— 伤害在 DamageMath、谁在场与施法在 BattleWorld、条件累加在 ConditionTracker、任务收发奖在 QuestRuntime、键位到技能在 SkillCaster；本类里出现判断或计算，多半是那块逻辑放错了地方。
    // 注意：刻意写成纯 C# 类而不是 MonoBehaviour，这样整条闭环能在 EditMode 里用假表跑通（造一局、接任务、打三下、交任务、断言奖励到账）；读键盘、建场景物体、画 HUD 在 Boot 的 M2DemoBehaviour 里，两件事分开才能各自验证。
    /// <summary>一局单机 PVE 的装配与门面：按依赖顺序造出战斗 / 条件 / 任务 / 成就 / 输入，接好事件桥，再对外提供进关卡、接任务、放技能、交任务；用完必须 Dispose。</summary>
    public sealed class BattleSession : IDisposable
    {
        // 条件系统：任务与成就共用同一份，条件进度都存在它里面；构造后非 null，成就没启用时只有任务在用
        private readonly ConditionTracker m_conditions;

        // 事件桥：把游戏事件（怪物死亡 / 技能命中 / 进区域 / 掉落）翻译成条件事件；持有全部订阅，Dispose 时统一退订
        private readonly ConditionEventBridge m_bridge;

        // 战斗世界：在场单位、刷怪、施法的真实执行者；整局只有这一份
        private readonly BattleWorld m_world;

        // 任务运行时：接任务、交任务、发奖；状态与事件由 UI 直接读它
        private readonly QuestRuntime m_quests;

        // 成就运行时；没给成就表时是 null（这一局就没有成就），调用方必须先判 null 再用
        private readonly AchievementRuntime m_achievements;

        // 输入到技能的施放器：只认「按了哪个技能键 + 打谁」，键位绑定在装配处做
        private readonly SkillCaster m_caster;

        // 玩家的战斗单位；InstanceId 整局不变且大于 0
        private readonly BattleAgent m_hero;

        // 当前目标实例编号；0 表示没有目标，非 0 时必须指向一只活怪（本类的不变量）
        private int m_targetInstanceId;

        // 选目标用的复用缓冲，长度 0 到当前活怪数；复用是为了不在每次选目标时分配一个列表
        private readonly List<BattleAgent> m_aliveScratch = new List<BattleAgent>();

        // 是否已经 Dispose；初值 false，释放后恒为 true，所以重复 Dispose 无害
        private bool m_disposed;

        // 功能：装配一局但不带成就（等价于给下面那个重载传 null 成就表），让不做成就的调用方一行都不用改。
        public BattleSession(QuestConfig quests, QuestConditionConfig conditions, RewardConfig rewards,
                             MonsterConfig monsters, HeroConfig heroes, SkillConfig skills,
                             int heroId, IQuestRewardSink rewardSink)
            : this(quests, conditions, rewards, monsters, heroes, skills, null, heroId, rewardSink, null)
        {
        }

        // 功能：按依赖顺序造出战斗世界、条件系统、任务运行时、成就运行时与技能施放器，并把四条游戏事件接到条件事件上。
        // 入参：quests 任务表；conditions 条件表；rewards 奖励表；monsters 怪物表；heroes 英雄表；skills 技能表；achievements 成就表（null 表示这一局不做成就）；heroId 玩家用哪个英雄（Hero 表主键）；rewardSink 发奖实现（当前是内存版，将来换服务端权威发奖）；rewardLedger 发奖台账（null 时用内存版 InMemoryRewardLedger，只有启用成就时才用到）
        // 返回：无（构造函数；中途抛异常则对象不会建出来，调用方拿不到半成品）
        // 步骤：
        //   1) 先造战斗世界并刷出英雄：后面几步都要世界，英雄的 InstanceId 又是施放器的入参
        //   2) 再造条件系统与任务运行时：两者必须共用同一个 tracker，否则条件进度对不上
        //   3) 再造成就运行时，必须早于建桥接线：成就是构造即登记全部条件，桥一旦开始喂事件，没登记的条件就被漏掉
        //   4) 造技能施放器：只需要世界与英雄编号
        //   5) 最后建桥并接四条链路：怪物死亡到击杀、技能命中到用技能、进区域到到达、物品掉落到收集
        // 上游：本类不带成就的那个重载；FromConfigMgr（运行时入口）；EditMode 用例用假表直接调它
        // 下游：BattleWorld、ConditionTracker、QuestRuntime、AchievementRuntime、SkillCaster、ConditionEventBridge 的构造函数
        // 注意：成就与任务必须共用同一个 m_conditions，不许另造一个 tracker —— 造第二个的后果是任务打死的怪不涨成就进度，而且没有任何报错。
        // 注意：顺序不能反 —— 成就必须早于桥接线，否则表现是「成就好像少算了前几只怪」，这个洞很隐蔽。
        // 注意：台账传 null 会退化成内存版，重启后「不重复发奖」就不成立；接了持久化就必须传持久化实现。
        // 注意：六到七张表刻意由调用方传入、这里不去 ConfigMgr 取，这样本类才能在 EditMode 里用假表跑。
        // 注意：桥是全文件唯一同时认识「战斗 / 世界」和「条件系统」的地方，战斗模块因此完全不知道任务存在。
        public BattleSession(QuestConfig quests, QuestConditionConfig conditions, RewardConfig rewards,
                             MonsterConfig monsters, HeroConfig heroes, SkillConfig skills,
                             AchievementConfig achievements,
                             int heroId, IQuestRewardSink rewardSink, IRewardLedger rewardLedger)
        {
            m_world = new BattleWorld(monsters, heroes, skills);
            m_hero = m_world.SpawnHero(heroId);

            m_conditions = new ConditionTracker(new InMemoryConditionProgressStore());
            m_quests = new QuestRuntime(quests, conditions, rewards, m_conditions, rewardSink);

            m_achievements = achievements == null
                ? null
                : new AchievementRuntime(achievements, conditions, rewards, m_conditions, rewardSink,
                                         rewardLedger ?? new InMemoryRewardLedger());

            m_caster = new SkillCaster(m_world, m_hero.InstanceId);

            m_bridge = new ConditionEventBridge(m_conditions);
            m_bridge.Bind<MonsterDiedPayload>(BattleEvents.MonsterDied,
                EConditionEvent.KillMonster, payload => payload.MonsterConfigId);
            m_bridge.Bind<SkillHitPayload>(BattleEvents.SkillHit,
                EConditionEvent.UseSkill, payload => payload.SkillId);
            m_bridge.Bind<AreaEnteredPayload>(WorldEvents.AreaEntered,
                EConditionEvent.ReachArea, payload => payload.AreaId);

            // 注意：这里不判归属 —— ItemDropped 本身就是「归我的掉落」，归属判断在 ServerEventBridge 里；同一规则两处实现，两边迟早不一致。
            m_bridge.Bind<ItemDroppedPayload>(BattleEvents.ItemDropped,
                EConditionEvent.CollectItem, payload => payload.ItemId);
        }

        /// <summary>战斗世界；演示与调试要看在场单位时读它。</summary>
        public BattleWorld World
        {
            get { return m_world; }
        }

        // 功能：从 ConfigMgr 取全部配置表装一局，缺成就表时整局照常可玩、只是没有成就。
        // 入参：heroId 玩家用哪个英雄（Hero 表主键）；rewardSink 发奖实现
        // 返回：装配好的一局，调用方负责在这一局结束时 Dispose
        // 步骤：
        //   1) 先用 TryGet 取成就表：缺表不算错误，只让这一局没有成就
        //   2) 再用 Get 取其余六张表：缺任何一张就地抛异常
        //   3) 台账参数刻意传 null（等于用内存台账），本方法也刻意不留台账入参
        // 上游：Boot 的 M2DemoBehaviour 装配一局时；Editor 的 NetDebugWindow 装真任务链时
        // 下游：ConfigMgr.Instance.TryGet / Get；本类带成就的构造函数
        // 注意：必须在配置预加载完成之后调用（GameTables.PreloadAll 的回调里）—— 表没加载时 ConfigMgr.Get 会抛异常，并明确提示忘了预加载。
        // 注意：成就表用 TryGet 而不是 Get，且先于其余表取 —— 成就是锦上添花，不该因为它没导入就让整局起不来；而表没加载不会静默，PreloadAll 的失败回调与调试面板都会先报一次。
        // 注意：不给本方法加台账参数的理由一：客户端本来就没有持久化台账，跨进程不重复发奖在这一侧不成立，加一个用不上的参数只是猜测性的通用。
        // 注意：理由二是编译器逼出来的 —— 加参数会让 NBC.Boot 编不过（CS0012：Boot 没引用 NBC.Shared，而 C# 必须解析被调方法的完整签名，含可选参数的默认值）。这个错误单程序集闸门永远抓不到，已由按 asmdef 边界分别编译的 Server\_asmdef-probe 兜住。
        /// <summary>从 ConfigMgr 取配置装配一局（运行时入口）；必须在配置预加载完成之后调用，缺成就表时只是这一局没有成就。</summary>
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
                rewardSink,
                null);
        }

        /// <summary>任务运行时；UI 读它的任务状态与事件。</summary>
        public QuestRuntime Quests
        {
            get { return m_quests; }
        }

        /// <summary>成就运行时；没给成就表时是 null，调用方必须先判 null 再用。</summary>
        public AchievementRuntime Achievements
        {
            get { return m_achievements; }
        }

        // 条件系统：任务与成就的进度都在这一份里；调试面板与用例要看进度时读它。
        public ConditionTracker Conditions
        {
            get { return m_conditions; }
        }

        /// <summary>输入到技能的施放器；键位由装配处在它上面 Bind。</summary>
        public SkillCaster Caster
        {
            get { return m_caster; }
        }

        /// <summary>玩家的战斗单位；演示读它的 SkillIds 取技能、读它的名字显示玩家。</summary>
        public BattleAgent Hero
        {
            get { return m_hero; }
        }

        /// <summary>当前目标实例编号；0 表示没有目标，非 0 时必定指向一只活怪。</summary>
        public int CurrentTargetInstanceId
        {
            get { return m_targetInstanceId; }
        }

        // 门面：一局里玩家会做的四件事（进关卡、接任务、放技能、交任务）

        // 功能：按数量刷怪、把目标切到第一只活怪，并在 areaId 非 0 时广播进入区域事件，让到达区域类条件跟着推进。
        // 入参：monsterId 刷哪种怪（Monster 表主键）；count 刷几只（必须 ≥ 1，否则抛 ArgumentOutOfRangeException）；areaId 进入哪个区域（0 表示不广播区域事件）
        // 返回：刷出来的怪，按刷的顺序，长度等于 count
        // 步骤：
        //   1) 先挡已 Dispose：这一局已经收尾，再刷怪只会造出没人收尾的单位
        //   2) 先校验 count 再刷：参数错就一只都不刷，不留半批怪
        //   3) 按 count 预分配结果列表，再逐只刷
        //   4) 刷完立刻选一次目标，保证进关卡就有目标可打
        //   5) 最后才广播区域事件：这样本方法返回时，击杀与到达两条条件都已经生效
        // 上游：Boot 的 M2DemoBehaviour 点「进关卡」时；EditMode 用例
        // 下游：BattleWorld.SpawnMonster、本类 SelectFirstAliveMonster、EventCenter.Instance.Trigger（AreaEntered）
        // 注意：areaId 传 0 不广播区域事件 —— 用例靠它单独验证「击杀条件涨、区域条件不涨」。
        /// <summary>刷 count 只指定怪物、把目标切到第一只活怪，并在 areaId 非 0 时广播进入区域事件。</summary>
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

        // 功能：把当前目标换成场上第一只活着的怪，没有活怪就清成 0。
        // 入参：无
        // 返回：true 表示选到了（场上有活怪）；false 表示一只活的都没有，此时当前目标已置 0
        // 步骤：
        //   1) 先清空复用缓冲：它跨调用共享，必须每次从空开始
        //   2) 让世界把活怪拷进来（它遍历内部字典，本类不再排序），顺序够用：只取第一只
        //   3) 空表就清 0 并返回 false —— 0 是「没有目标」的唯一合法表示
        //   4) 否则取第一只的实例编号
        // 上游：EnterLevel 刷完怪时、HandleInput 发现没目标或目标死了时；EditMode 用例断言目标切换
        // 下游：BattleWorld.CopyAliveMonsters
        // 注意：挑「第一只」是这一版的目标选择简化（不做点选、自动锁定、最近敌人），别当成正式的选怪逻辑。
        // 注意：「第一只」按世界内部字典的遍历顺序取，不是按编号或距离排的；要定序得改 CopyAliveMonsters 那一侧。
        public bool SelectFirstAliveMonster()
        {
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

        // 功能：转给任务运行时接一个任务，先挡已释放。
        // 入参：questId 任务编号（Quest 表主键，正整数）
        // 返回：结果；Ok 为 false 时 Reason 是一句能显示给玩家的原因（表里没有这个任务、当前状态不允许接、条件定义缺失）
        // 步骤：
        //   1) 挡已 Dispose
        //   2) 原样转给任务运行时，本类不加任何判断
        // 上游：Boot 的 M2DemoBehaviour 点「接任务」时；EditMode 用例
        // 下游：QuestRuntime.Accept
        /// <summary>接一个任务；Ok 为 false 时理由在 Reason 里。</summary>
        public QuestActionResult AcceptQuest(int questId)
        {
            EnsureNotDisposed();
            return m_quests.Accept(questId);
        }

        // 功能：转给任务运行时交一个任务并发奖，先挡已释放。
        // 入参：questId 任务编号（Quest 表主键，正整数）
        // 返回：结果；Ok 为 false 时 Reason 说明为什么没交付（状态不是已完成、表里没有这个任务、奖励表缺这一行，最后一种情况下状态不会改）
        // 步骤：
        //   1) 挡已 Dispose
        //   2) 原样转给任务运行时，本类不加任何判断
        // 上游：Boot 的 M2DemoBehaviour 点「交任务」时；EditMode 用例
        // 下游：QuestRuntime.Submit
        /// <summary>交一个任务并发奖；Ok 为 false 时理由在 Reason 里。</summary>
        public QuestActionResult SubmitQuest(int questId)
        {
            EnsureNotDisposed();
            return m_quests.Submit(questId);
        }

        // 功能：处理一帧输入，把本帧新按下的技能键打向当前目标，返回这一帧放了几发。
        // 入参：command 这一帧的输入命令；results 释放结果会追加进来（可以为 null，不需要结果就传 null）
        // 返回：这一帧放了几发；0 表示没放（没有目标、场上没活怪、本帧没按技能键，都不算错误）
        // 步骤：
        //   1) 挡已 Dispose
        //   2) 目标为空或已经死了就先换一个活的目标；换不到直接返回 0，这一帧什么都不做
        //   3) 把目标编号交给施放器，由它按键位放技能
        //   4) 打完立刻复查目标：这一帧把它打死了就当场换下一个，而不是等下一次按键
        // 上游：Boot 的 M2DemoBehaviour 每帧驱动；EditMode 用例
        // 下游：SkillCaster.Handle、本类 SelectFirstAliveMonster / IsAliveTarget
        // 注意：不变量是「当前目标要么是 0，要么指向一只活怪」；第 4 步就是为了维持它 —— 不这么做，UI 会显示「当前目标 #2」而 #2 已经不在场上了。
        /// <summary>处理一帧输入，放出本帧新按下的技能键；返回这一帧放了几发。</summary>
        public int HandleInput(InputCommand command, List<SkillCastOutcome> results)
        {
            EnsureNotDisposed();

            if (m_targetInstanceId == 0 || !IsAliveTarget(m_targetInstanceId))
            {
                if (!SelectFirstAliveMonster())
                {
                    return 0;
                }
            }

            int cast = m_caster.Handle(command, m_targetInstanceId, results);

            if (cast > 0 && !IsAliveTarget(m_targetInstanceId))
            {
                SelectFirstAliveMonster();
            }

            return cast;
        }

        // 功能：把英雄、场上活怪数、当前目标、任务进度与成就进度拼成一段多行文本，给控制台与 HUD 直接显示。
        // 入参：无
        // 返回：多行文本，每行带换行；没成就时最后一行写明这一局没有成就
        // 步骤：
        //   1) 用 StringBuilder 逐段拼，避免多次字符串相加
        //   2) 先写英雄与「活怪数 / 当前目标」，这两项最能定位当前局面
        //   3) 再接任务的进行中列表
        //   4) 最后接成就；成就运行时为 null 时写明没有成就，免得看的人以为漏了一段
        // 上游：EditMode 用例断言文本内容；调试时手工调用
        // 下游：BattleAgent.ToString、BattleWorld.AliveMonsterCount、QuestRuntime.DescribeActive、AchievementRuntime.Describe
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

        // 功能：拆掉桥的订阅、释放成就与任务、清空条件登记，让这一局彻底收尾。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已 Dispose 就直接返回，保证重复调用无害
        //   2) 先置位 m_disposed，之后 EnterLevel / AcceptQuest / SubmitQuest / HandleInput 都会抛 ObjectDisposedException
        //   3) 先拆订阅方：桥退订全部事件链路（施放器不订阅事件，没有退订动作）
        //   4) 再释放成就与任务：它们各自持有条件登记，必须在清空登记表之前注销
        //   5) 最后清空条件登记，作为粗粒度兜底
        // 上游：Boot 的 M2DemoBehaviour 退出演示时；Editor 的 NetDebugWindow 关窗口时；EditMode 用例的 TearDown
        // 下游：ConditionEventBridge.Dispose、AchievementRuntime.Dispose、QuestRuntime.Dispose、ConditionTracker.Clear
        // 注意：成就与任务都要 Dispose，且都要在 Clear 之前 —— 顺序反了不会立刻报错（Clear 之后注销只返回 false），但「谁登记了什么」这笔账在本类里就对不上了，要保持谁登记谁注销。
        /// <summary>退订事件、释放成就与任务、清空条件登记；可重复调用。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;

            m_bridge.Dispose();

            if (m_achievements != null)
            {
                m_achievements.Dispose();
            }

            m_quests.Dispose();
            m_conditions.Clear();
        }

        // 内部：只给本类自己用

        // 功能：判断给定实例编号的单位还在不在、且活着。
        // 入参：instanceId 实例子编号（0 也照常查，查不到就是 false）
        // 返回：true 表示这个世界里有这个单位且当前血大于 0
        // 步骤：
        //   1) 先让世界按实例编号找单位：找不到就是 false
        //   2) 找到再看它自己的 IsAlive，活着的判定只写在 BattleAgent 一处
        // 上游：HandleInput（换目标前后各查一次）
        // 下游：BattleWorld.TryGetAgent、BattleAgent.IsAlive
        private bool IsAliveTarget(int instanceId)
        {
            BattleAgent agent;
            return m_world.TryGetAgent(instanceId, out agent) && agent.IsAlive;
        }

        // 功能：挡住「Dispose 之后还调门面」的用法，直接抛异常。
        // 入参：无
        // 返回：无；已释放时抛 ObjectDisposedException
        // 步骤：
        //   1) 只看 m_disposed 一个标志，标志在 Dispose 里置位
        //   2) 置位了就抛，消息里写明原因，便于从日志反推是谁在会话结束后还在调
        // 上游：EnterLevel、AcceptQuest、SubmitQuest、HandleInput
        // 下游：无
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
