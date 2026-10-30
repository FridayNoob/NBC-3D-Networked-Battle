// 这个文件里有：
//   AchievementRuntime（类）：成就运行时 —— 构造时登记全部成就条件，条件一齐当场解锁并发奖，进度跨局保留

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Game.Config;
using NBC.Game.Quest;
using NBC.Shared.Condition;
using NBC.Shared.Reward;

namespace NBC.Game.Achievement
{
    // 主要功能：构造时把全部成就的条件登记进与任务共用的条件系统，某成就的条件凑齐就当场解锁并发奖；没有接取与交付，只有锁着 / 解锁两态。
    // 主要数据成员：m_achievements / m_conditions / m_rewards（成就、条件、奖励三张表）、m_tracker（与任务共用的条件系统）、m_ledger（已发奖励台账）、m_unlocked / m_unlockedOrder（已解锁编号与顺序）、m_ownerOfCondition（条件到成就的归属）。
    // 主要外部方法：Authority（权威进度来源）、AchievementCount / UnlockedCount / LastProblem（只读属性）、IsUnlocked、CopyUnlockedIds、TryGetTracking、CopyTrackings、Describe、Dispose。
    // 谁在用它：BattleSession 装配一局时创建并持有；Editor 的 NetDebugWindow 成就页读已解锁数与追踪视图；EditMode 的 AchievementRuntimeTests 用假表断言闭环。
    // 注意：成就是 QuestRuntime 的弟弟，只有三处不同 —— 构造时全部登记、resetProgress 传 false（进度不清零、已达成时登记当场回调）、条件一齐当场解锁发奖；所以没有 Accept / Submit，也没有 EAchievementState。
    // 注意：与任务共用同一个 ConditionTracker，两边的回调都会收到对方条件编号；本类认不出来时静默忽略，绝不抛未知条件。一条条件只允许一个持有者（ConfigKit 的 CFG0023 保证），抢同一条会被 Register 当场抛异常。
    // 注意：项目规矩是玩家能触发的失败走返回值 + 一句人话、程序 / 配置错误抛异常；成就没有玩家的那一下点击，返回值没人看，只能抛，消息必须点名哪个成就、哪条条件、哪个奖励 —— 运行期这道闸门与 CFG0023 的导出期闸门互补（防有人绕过流水线手改 SO）。
    // 注意：构造里的登记有副作用且会重入回调，顺序必须是「先记全部归属 → 再逐条登记 → 最后统一评一遍」，详见构造函数。
    /// <summary>成就运行时：构造时登记全部成就条件，条件凑齐当场解锁并发奖，进度跨局保留；用完必须 Dispose。</summary>
    public sealed class AchievementRuntime : IDisposable
    {
        // 成就表：几行就是几个成就，0 行合法（这个项目可以不做成就）；构造时非 null
        private readonly AchievementConfig m_achievements;

        // 条件表：与任务共用同一张；构造时非 null
        private readonly QuestConditionConfig m_conditions;

        // 奖励表：构造时非 null；某成就的 rewardId 缺行会在构造时抛异常
        private readonly RewardConfig m_rewards;

        // 权威进度来源：默认 null（还没连会话），可事后设置；只盖 BuildTracking 的显示值、不参与判定（判定在服务端）；做成可写属性是为了不逼既有调用点（含用例）改签名
        public IQuestProgressAuthority Authority { get; set; }

        // 条件系统：与任务共用同一个实例（由 BattleSession 传入），两边进度都在这份里
        private readonly ConditionTracker m_tracker;

        // 已发奖励台账：必填，防「重启后把已完成的成就再发一遍奖」；刻意不给默认内存台账，否则重启重复发奖会静默留在代码里
        private readonly IRewardLedger m_ledger;

        // 发奖接缝：类型名 IQuestRewardSink 是历史遗留，本质通用；刻意不另造同形状接口，否则以后奖励挪服务端要改两处
        private readonly IQuestRewardSink m_rewardSink;

        // 已解锁的成就编号：判断是否解锁、是否该重复发奖都查它
        private readonly HashSet<int> m_unlocked = new HashSet<int>();

        // 已解锁的成就编号按解锁顺序记：UI 想显示「最近解锁」时不用再排
        private readonly List<int> m_unlockedOrder = new List<int>();

        // 条件编号 → 所属成就编号：把条件通知路由回成就；构造时先填满它再登记，重入回调才认得出自己的条件
        private readonly Dictionary<int, int> m_ownerOfCondition = new Dictionary<int, int>();

        // 最近一次解锁失败的原因，调试面板显示用；没有问题时是 null
        private string m_lastProblem;

        // Dispose 过没有；重复 Dispose 要无害，所以先查它再动手
        private bool m_disposed;

        // 功能：接好三张表、共用条件系统、发奖接缝与台账，订阅通知，校验全部配置后登记全部成就并统一评一遍 —— 构造即生效。
        // 入参：achievements 成就表；conditions 条件表；rewards 奖励表；tracker 条件系统（必须与任务同一个）；rewardSink 发奖接缝；ledger 已发奖励台账。六者都不能为 null，否则抛 ArgumentNullException。
        // 返回：无（构造函数）；配置有错抛 InvalidOperationException，调用方拿不到半成品。
        // 步骤：
        //   1) 逐个判 null 并给出能照着修的消息 —— 台账为什么必填见字段注释
        //   2) 先订阅 ProgressChanged / ConditionMet 再登记：resetProgress 为 false 的登记会当场回调，订阅晚了就漏掉
        //   3) ValidateAll 先走完全部配置：登记有副作用，边校验边登记会在中途失败时留下「看不见却在后台涨进度」的半成品
        //   4) RegisterAll 记归属并登记，最后 EvaluateAll 收尾，兜住多条条件后登记才凑齐的情况
        // 上游：BattleSession 的构造函数；EditMode 的 AchievementRuntimeTests 用假表直接调它。
        // 下游：ValidateAll、RegisterAll、EvaluateAll；ConditionTracker.ProgressChanged / ConditionMet。
        // 注意：登记顺序是本片最容易写错的地方 —— 若「登记一条 → 记一条归属」，登记当场回调时归属表还没写，认不出自己的条件就静默 return，表现是登录后永不解锁且一行日志都没有。
        // 注意：第 4 步的收尾不是冗余 —— 还没登记的条件 IsMet 恒为 false，多条条件的成就在登记循环里每条单独触发都凑不齐。
        // 注意：两个成就抢同一条条件时 ConditionTracker.Register 会当场抛异常（它故意不静默覆盖），这里提前查出来才能点名是哪两个成就。
        public AchievementRuntime(AchievementConfig achievements, QuestConditionConfig conditions,
                                  RewardConfig rewards, ConditionTracker tracker, IQuestRewardSink rewardSink,
                                  IRewardLedger ledger)
        {
            if (achievements == null) { throw new ArgumentNullException(nameof(achievements), "[AchievementRuntime] 成就表是 null。"); }
            if (conditions == null) { throw new ArgumentNullException(nameof(conditions), "[AchievementRuntime] 条件表是 null。"); }
            if (rewards == null) { throw new ArgumentNullException(nameof(rewards), "[AchievementRuntime] 奖励表是 null。"); }

            if (tracker == null)
            {
                throw new ArgumentNullException(nameof(tracker), "[AchievementRuntime] 条件系统是 null。");
            }

            if (rewardSink == null)
            {
                throw new ArgumentNullException(nameof(rewardSink), "[AchievementRuntime] 发奖实现是 null。");
            }

            if (ledger == null)
            {
                throw new ArgumentNullException(nameof(ledger),
                    "[AchievementRuntime] 台账是 null。\n" +
                    "它防的是「重启后重复发奖」（持久化之后必然发生的那个 bug）。\n" +
                    "单机/测试里传 `new InMemoryRewardLedger()`；接了数据库就传持久化实现。");
            }

            m_achievements = achievements;
            m_conditions = conditions;
            m_rewards = rewards;
            m_tracker = tracker;
            m_rewardSink = rewardSink;
            m_ledger = ledger;

            m_tracker.ProgressChanged += OnConditionProgressChanged;
            m_tracker.ConditionMet += OnConditionMet;

            ValidateAll();

            RegisterAll();
            EvaluateAll();
        }

        /// <summary>成就总数：配置表里有几行就是几，表未加载时 0。</summary>
        public int AchievementCount
        {
            get { return m_achievements.rows == null ? 0 : m_achievements.rows.Count; }
        }

        /// <summary>已经解锁的成就数。</summary>
        public int UnlockedCount
        {
            get { return m_unlocked.Count; }
        }

        /// <summary>最近一次解锁失败的原因；没有问题时是 null。</summary>
        public string LastProblem
        {
            get { return m_lastProblem; }
        }

        // 功能：查内存里的已解锁集合，判断这个成就解锁了没有。
        public bool IsUnlocked(int achievementId)
        {
            return m_unlocked.Contains(achievementId);
        }

        // 功能：按解锁顺序把已解锁的成就编号复制进调用方的列表。
        // 入参：buffer 目标列表，不能为 null（否则抛 ArgumentNullException）；会先被 Clear。
        // 返回：无。
        // 步骤：
        //   1) 先判 null：宁可抛，也不让调用方以为复制成功
        //   2) Clear 后再逐条 Add，顺序与 m_unlockedOrder 一致
        // 上游：目前没有调用点，预留给成就界面一次取走全部编号。
        // 下游：m_unlockedOrder。
        public void CopyUnlockedIds(List<int> buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            buffer.Clear();

            for (int i = 0; i < m_unlockedOrder.Count; i++)
            {
                buffer.Add(m_unlockedOrder[i]);
            }
        }

        // 追踪视图：给 UI 与调试面板看，不参与解锁判定。

        // 功能：按编号取一个成就的追踪视图；配置表里没有这个成就时返回 false。
        // 入参：achievementId 成就编号；tracking 取到的视图，每次调用都新建（别在每帧里调）。
        // 返回：true 表示配置里有这个成就且 tracking 非 null；false 表示编号不存在，tracking 为 null。
        // 步骤：
        //   1) 先在成就表里 TryGet，查不到就返回 false（不抛）
        //   2) 查到就 BuildTracking 造视图
        // 上游：EditMode 的 AchievementRuntimeTests；成就界面将来可用。
        // 下游：BuildTracking。
        public bool TryGetTracking(int achievementId, out AchievementTracking tracking)
        {
            Config_Achievement row;

            if (!m_achievements.TryGet(achievementId, out row))
            {
                tracking = null;
                return false;
            }

            tracking = BuildTracking(row);
            return true;
        }

        /// <summary>把全部成就（含未解锁）复制进列表，元素是新造的追踪视图；列表先 Clear。</summary>
        // 功能：把配置表里每一个成就都造一份追踪视图，按表内顺序放进调用方列表。
        // 入参：buffer 目标列表，不能为 null（否则抛 ArgumentNullException）；会先被 Clear。
        // 返回：无；成就表没加载时列表为空。
        // 步骤：
        //   1) 判 null、Clear，保证调用方拿到的是完整一批而不是叠加
        //   2) 取 rows；为 null 直接返回（没有成就不是错误）
        //   3) 逐行 BuildTracking —— 视图是新建的，调用方可以放心持有
        // 上游：Editor 的 NetDebugWindow 成就页；本类 Describe。
        // 下游：BuildTracking。
        public void CopyTrackings(List<AchievementTracking> buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            buffer.Clear();

            List<Config_Achievement> rows = m_achievements.rows;

            if (rows == null)
            {
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                buffer.Add(BuildTracking(rows[i]));
            }
        }

        // 功能：把全部成就拼成一段多行人话（总数、每个成就与逐条条件、最近的失败原因），供控制台与调试面板直接输出。
        // 入参：无。
        // 返回：多行文本；一个成就都没有时返回一句提示。
        // 步骤：
        //   1) 先 CopyTrackings 拿一份当前视图
        //   2) 空表直接给一句提示，避免输出只有标题
        //   3) 首行拼「已解锁 / 总数」，再逐行追加成就
        //   4) 有 m_lastProblem 才追加，没失败就不写
        // 上游：BattleSession.Describe；EditMode 的 AchievementRuntimeTests 断言文本。
        // 下游：CopyTrackings、AchievementTracking.ToString。
        public string Describe()
        {
            var trackings = new List<AchievementTracking>();
            CopyTrackings(trackings);

            if (trackings.Count == 0)
            {
                return "[AchievementRuntime] 配置表里一个成就都没有。";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("[AchievementRuntime] 成就 ").Append(m_unlocked.Count)
                   .Append('/').Append(trackings.Count).Append(" 已解锁：\n");

            for (int i = 0; i < trackings.Count; i++)
            {
                builder.Append(trackings[i].ToString()).Append('\n');
            }

            if (!string.IsNullOrEmpty(m_lastProblem))
            {
                builder.Append("⚠️ 最近一次解锁失败：").Append(m_lastProblem).Append('\n');
            }

            return builder.ToString();
        }

        // 条件系统的通知：订阅 ConditionTracker，只认自己的条件编号。

        // 功能：条件进度变化时，把属于本类的条件翻译成成就进度事件广播出去；不是本类的条件静默忽略。
        // 入参：conditionKey 条件编号；progress 新进度（本方法只确认事件来自条件系统，不读其内容）。
        // 返回：无。
        // 步骤：
        //   1) 查 m_ownerOfCondition，查不到说明是任务的条件（两边共用 tracker），直接返回
        //   2) 查到就广播 AchievementEvents.ProgressChanged，载荷是成就编号
        // 上游：ConditionTracker.ProgressChanged（构造时订阅）。
        // 下游：EventCenter、AchievementEvents.ProgressChanged。
        // 注意：绝不能对未知条件抛异常 —— 共用 tracker 意味着回调一定会收到对方编号，这里只是「通用层 + 多个消费者」的边界。
        private void OnConditionProgressChanged(int conditionKey, ConditionProgress progress)
        {
            int achievementId;

            if (!m_ownerOfCondition.TryGetValue(conditionKey, out achievementId))
            {
                return;
            }

            EventCenter.Instance.Trigger(AchievementEvents.ProgressChanged, achievementId);
        }

        // 功能：某条条件达成时，若它属于某个成就就评该成就是否该解锁。
        // 入参：conditionKey 条件编号；progress 达成时的进度（本方法不读，判定统一走 tracker）。
        // 返回：无。
        // 步骤：
        //   1) 查归属，查不到是任务的条件，静默返回
        //   2) 查到就 Evaluate 该成就，是否全达成、是否已解锁都由 Evaluate 判断
        // 上游：ConditionTracker.ConditionMet（构造时订阅）。
        // 下游：Evaluate。
        private void OnConditionMet(int conditionKey, ConditionProgress progress)
        {
            int achievementId;

            if (!m_ownerOfCondition.TryGetValue(conditionKey, out achievementId))
            {
                return;
            }

            Evaluate(achievementId);
        }

        // 内部：校验 / 登记 / 评定 / 解锁。

        // 功能：在登记之前把全部成就配置校验一遍，配置有错就抛异常，避免留下登记到一半的半成品。
        // 入参：无（读 m_achievements / m_conditions / m_rewards）。
        // 返回：无；配置有错抛 InvalidOperationException，消息点名成就、条件或奖励。
        // 步骤：
        //   1) 表为空直接返回 —— 没成就不是错误（这个项目可能不做成就）
        //   2) 逐行查：至少有一条条件、每条条件在条件表里能找到且能解析成 ConditionDef
        //   3) 用 seenConditions 抓「两个成就引用同一条条件」，报错才能点名两个成就
        //   4) 最后查奖励行：先查奖励再登记，因为登记之后条件一齐就会发奖，而「要发奖时才发现缺行」没法回头（成就自动解锁，没有取消交付）
        // 上游：构造函数。
        // 下游：m_conditions.TryGet、ConditionDef.TryCreate、m_rewards.TryGet。
        // 注意：不能边校验边登记 —— 登记会写进 ConditionTracker，校验到第 3 个成就才发现错时，前两个已经登记进去，会在后台悄悄涨进度，玩家却看不到。
        private void ValidateAll()
        {
            List<Config_Achievement> rows = m_achievements.rows;

            if (rows == null || rows.Count == 0)
            {
                return;
            }

            var seenConditions = new Dictionary<int, int>();

            for (int i = 0; i < rows.Count; i++)
            {
                Config_Achievement row = rows[i];

                if (row == null)
                {
                    continue;
                }

                if (row.conditionIds == null || row.conditionIds.Length == 0)
                {
                    throw new InvalidOperationException(
                        "[AchievementRuntime] 成就 " + row.id + " 在配置表里**一条条件都没有**。\n" +
                        "这种成就是死的（永远没有任何东西能解锁它）。\n" +
                        "要么给它加条件，要么把这一行删掉。");
                }

                for (int c = 0; c < row.conditionIds.Length; c++)
                {
                    int conditionId = row.conditionIds[c];
                    Config_QuestCondition conditionRow;

                    if (!m_conditions.TryGet(conditionId, out conditionRow))
                    {
                        throw new InvalidOperationException(
                            "[AchievementRuntime] 成就 " + row.id + " 的第 " + (c + 1) + " 条条件 " + conditionId +
                            " 在 QuestCondition 表里找不到。\n" +
                            "（先跑 ConfigKit 生成、再点配置表导入菜单。）");
                    }

                    ConditionDef def;
                    string error;

                    if (!ConditionDef.TryCreate(conditionRow.eventType, conditionRow.targetId,
                                                conditionRow.requiredCount, out def, out error))
                    {
                        throw new InvalidOperationException(
                            "[AchievementRuntime] 成就 " + row.id + " 的条件 " + conditionId + " 配置有错：" + error);
                    }

                    int firstOwner;

                    if (seenConditions.TryGetValue(conditionId, out firstOwner))
                    {
                        throw new InvalidOperationException(
                            "[AchievementRuntime] 条件 " + conditionId + " 被**两个成就**引用了：" +
                            "成就 " + firstOwner + " 和成就 " + row.id + "。\n" +
                            "`ConditionTracker` 只允许一个持有者登记同一条条件（重复登记会让进度被清零），所以这里直接拒绝启动。\n" +
                            "（ConfigKit 的 CFG0023 会在导出配置表时先抓到这件事。）");
                    }

                    seenConditions.Add(conditionId, row.id);
                }

                Config_Reward rewardRow;

                if (!m_rewards.TryGet(row.rewardId, out rewardRow))
                {
                    throw new InvalidOperationException(
                        "[AchievementRuntime] 成就 " + row.id + " 的奖励 " + row.rewardId +
                        " 在 Reward 表里找不到。\n" +
                        "宁可**不解锁**也不能出现「成就亮了但奖励没到」—— 那种状态玩家会当成 bug 却看不出原因。");
                }
            }
        }

        // 功能：把全部成就的条件登记进条件系统，resetProgress 传 false，让进度跨局保留且登记时已达成会当场回调。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 先把全部条件 → 成就的归属记满：Register 会当场回调，回调里要靠这张表认出自己的条件
        //   2) 再逐条 Register；TryGet / TryCreate 失败就跳过（ValidateAll 已保证成立，这里只是再取值）
        // 上游：构造函数（ValidateAll 之后）。
        // 下游：ConditionTracker.Register。
        // 注意：false 就是任务与成就的唯一差别 —— 任务接取时传 true（接了才开始算），成就传 false（一直生效、不清零；已达成时登记当场回调，是为了「上次已打满、这次登录就解锁」，否则玩家得再打一只）。
        // 注意：顺序不能颠倒 —— 先登记再记归属，重入回调会认不出条件而静默返回，表现为登录后永不解锁且无日志。
        private void RegisterAll()
        {
            List<Config_Achievement> rows = m_achievements.rows;

            if (rows == null)
            {
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                Config_Achievement row = rows[i];

                if (row == null || row.conditionIds == null)
                {
                    continue;
                }

                for (int c = 0; c < row.conditionIds.Length; c++)
                {
                    m_ownerOfCondition[row.conditionIds[c]] = row.id;
                }
            }

            for (int i = 0; i < rows.Count; i++)
            {
                Config_Achievement row = rows[i];

                if (row == null || row.conditionIds == null)
                {
                    continue;
                }

                for (int c = 0; c < row.conditionIds.Length; c++)
                {
                    Config_QuestCondition conditionRow;
                    ConditionDef def;

                    if (!m_conditions.TryGet(row.conditionIds[c], out conditionRow))
                    {
                        continue;
                    }

                    if (!ConditionDef.TryCreate(conditionRow.eventType, conditionRow.targetId,
                                                conditionRow.requiredCount, out def, out _))
                    {
                        continue;
                    }

                    m_tracker.Register(row.conditionIds[c], def, false);
                }
            }
        }

        // 功能：把全部成就统一评一遍，兜住「多条条件、后登记的那条才凑齐」的情况。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 遍历配置行，非 null 的逐个 Evaluate
        // 上游：构造函数最后一步。
        // 下游：Evaluate。
        // 注意：这一步不是冗余 —— 逐条登记时，还没登记的条件 IsMet 恒为 false，两条都已达成的成就在登记循环里每条单独触发都凑不齐。
        private void EvaluateAll()
        {
            List<Config_Achievement> rows = m_achievements.rows;

            if (rows == null)
            {
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null)
                {
                    Evaluate(rows[i].id);
                }
            }
        }

        // 功能：评一个成就是不是该解锁；已解锁的什么都不做，条件没齐的也什么都不做。
        // 入参：achievementId 成就编号。
        // 返回：无。
        // 步骤：
        //   1) 先查 m_unlocked：已解过就返回，绝不能重复发奖
        //   2) 查配置行并检查全部条件；查不到或没齐都返回
        //   3) 都满足才 Unlock
        // 上游：OnConditionMet（条件达成）、EvaluateAll（构造收尾）。
        // 下游：AreAllConditionsMet、Unlock。
        private void Evaluate(int achievementId)
        {
            if (m_unlocked.Contains(achievementId))
            {
                return;
            }

            Config_Achievement row;

            if (!m_achievements.TryGet(achievementId, out row) || !AreAllConditionsMet(row))
            {
                return;
            }

            Unlock(row);
        }

        // 功能：判断这个成就的全部条件是不是都达成了。
        // 入参：row 成就配置行。
        // 返回：true 表示至少有一条条件且全部达成；false 表示一条条件都没有，或还有条件没达成。
        // 步骤：
        //   1) 条件为空直接返回 false —— 空列表不算「全部达成」
        //   2) 逐条查 m_tracker.IsMet，遇到没达成的立刻返回 false
        // 上游：Evaluate。
        // 下游：ConditionTracker.IsMet。
        private bool AreAllConditionsMet(Config_Achievement row)
        {
            int[] conditionIds = row.conditionIds;

            if (conditionIds == null || conditionIds.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < conditionIds.Length; i++)
            {
                if (!m_tracker.IsMet(conditionIds[i]))
                {
                    return false;
                }
            }

            return true;
        }

        // 功能：把一个已判定满足条件的成就标记成已解锁，并按台账决定要不要发奖。
        // 入参：row 成就配置行。
        // 返回：无；奖励缺行时记录 m_lastProblem 并广播 UnlockFailed，不抛异常。
        // 步骤：
        //   1) 查奖励行；缺行就记录失败并返回 —— 此刻在 ConditionTracker.Notify 的派发循环里，抛出去会打断别人的进度派发
        //   2) 查台账：已发过就只点亮成就、不广播 Unlocked —— 这一局刚达成与重启后读回早已达成，区别只在台账
        //   3) 没发过则先标记已解锁、再造 QuestReward、Grant、MarkGranted，最后广播 Unlocked
        // 上游：Evaluate。
        // 下游：m_rewards.TryGet、m_ledger.HasGranted / MarkGranted、m_rewardSink.Grant、EventCenter。
        // 注意：台账命中时仍然要标记成已解锁（玩家看到的状态必须对），不发的只是奖励；刻意不广播 Unlocked —— 那是「刚刚解锁」的庆祝信号，重启时重放会让玩家以为又拿了一次，「恢复状态」与「发生事件」是两件事。
        // 注意：必须先改状态再发奖 —— 发奖可能触发订阅方回头查「这个成就解锁了吗」，那时必须已经是已解锁。
        // 注意：必须先发奖再记账 —— 反过来 Grant 抛异常就变成「记了账但没发」，玩家永远拿不到；现在最坏是「发了没记上」，重启会补发一次（多给，不少给，且多给还能从日志查出来）。
        private void Unlock(Config_Achievement row)
        {
            Config_Reward rewardRow;

            if (!m_rewards.TryGet(row.rewardId, out rewardRow))
            {
                m_lastProblem = "成就 " + row.id + " 该解锁，但奖励 " + row.rewardId +
                                " 在 Reward 表里找不到 —— **没有解锁**（避免出现「成就亮了奖励没到」）。";

                EventCenter.Instance.Trigger(AchievementEvents.UnlockFailed,
                    new AchievementUnlockFailedPayload(row.id, m_lastProblem));
                return;
            }

            if (m_ledger.HasGranted(ERewardOwnerKind.Achievement, row.id))
            {
                if (m_unlocked.Add(row.id))
                {
                    m_unlockedOrder.Add(row.id);
                }

                m_lastProblem = null;
                return;
            }

            m_unlocked.Add(row.id);
            m_unlockedOrder.Add(row.id);
            m_lastProblem = null;

            QuestReward reward = new QuestReward(rewardRow.id, rewardRow.exp, rewardRow.gold,
                                                 rewardRow.itemId, rewardRow.itemCount);

            m_rewardSink.Grant(row.id, reward);

            m_ledger.MarkGranted(ERewardOwnerKind.Achievement, row.id, reward.RewardId);

            EventCenter.Instance.Trigger(AchievementEvents.Unlocked,
                new AchievementUnlockedPayload(row.id, reward.RewardId));
        }

        // 功能：把一个成就配置行加上实时进度，摊平成追踪视图；配置有错的行也照样给出并标出问题。
        // 入参：row 成就配置行。
        // 返回：新建的追踪视图，条件顺序与配置一致；每次调用都新建对象。
        // 步骤：
        //   1) 先填编号、名字、描述与解锁状态
        //   2) 没有条件就返回只有成就信息的视图
        //   3) 逐条取条件配置与进度；配置错就造一行「配置有错」而不是静默少一行
        //   4) 取不到进度按 0/N 显示 —— 追踪视图是给人看的，看板自己崩掉最差
        //   5) 最后用 QuestProgressOverlay.Apply 把权威值盖上去：成就是服务端权威真的管到的那一批（服务端目前只登记成就条件），服务端没提过的条件保留本地值并标成 LocalPrediction
        // 上游：TryGetTracking、CopyTrackings。
        // 下游：m_conditions.TryGet、ConditionDef.TryCreate、m_tracker.TryGetProgress、QuestProgressOverlay.Apply。
        private AchievementTracking BuildTracking(Config_Achievement row)
        {
            AchievementTracking tracking = new AchievementTracking();
            tracking.AchievementId = row.id;
            tracking.Name = row.name;
            tracking.Description = row.desc;
            tracking.IsUnlocked = m_unlocked.Contains(row.id);

            int[] conditionIds = row.conditionIds;

            if (conditionIds == null)
            {
                return tracking;
            }

            for (int i = 0; i < conditionIds.Length; i++)
            {
                Config_QuestCondition conditionRow;
                ConditionDef def;

                if (!m_conditions.TryGet(conditionIds[i], out conditionRow) ||
                    !ConditionDef.TryCreate(conditionRow.eventType, conditionRow.targetId,
                                            conditionRow.requiredCount, out def, out _))
                {
                    QuestConditionLine broken = new QuestConditionLine();
                    broken.ConditionId = conditionIds[i];
                    broken.Description = "⚠️ 条件 " + conditionIds[i] + " 配置有错";
                    tracking.Conditions.Add(broken);
                    continue;
                }

                ConditionProgress progress;

                if (!m_tracker.TryGetProgress(conditionIds[i], out progress))
                {
                    progress = new ConditionProgress(0, def.RequiredCount);
                }

                QuestConditionLine line = new QuestConditionLine();
                line.ConditionId = conditionIds[i];
                line.Description = def.Describe();
                line.Current = progress.Current;
                line.Required = progress.Required;
                line.IsMet = progress.IsMet;
                tracking.Conditions.Add(line);
            }

            QuestProgressOverlay.Apply(tracking.Conditions, Authority);

            return tracking;
        }

        // 功能：退订条件系统事件并注销全部条件登记；可以重复调用。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 已 Dispose 过就直接返回，保证重复调用无害
        //   2) 先退订两个事件，再逐个 Unregister 归属表里的条件，最后 Clear
        // 上游：BattleSession.Dispose；EditMode 用例的清理。
        // 下游：ConditionTracker.Unregister。
        // 注意：只注销登记、不动进度 —— 成就进度跨局保留，重进一局要能接着算，这正是 resetProgress 传 false 的意义。
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;

            m_tracker.ProgressChanged -= OnConditionProgressChanged;
            m_tracker.ConditionMet -= OnConditionMet;

            foreach (KeyValuePair<int, int> pair in m_ownerOfCondition)
            {
                m_tracker.Unregister(pair.Key);
            }

            m_ownerOfCondition.Clear();
        }
    }
}
