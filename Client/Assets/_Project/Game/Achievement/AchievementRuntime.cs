// ============================================================================
//  AchievementRuntime —— 成就运行时：登记 → 累加 → 解锁 → 发奖
//  项目：3D联网战斗Demo   对应：M4-S2、Docs\20 §四「任务与成就共用一套条件系统」
//
//  ---------------------------------------------------------------------------
//  一句话：它是 `QuestRuntime` 的**弟弟**，三处不同，其余全部一样
//  ---------------------------------------------------------------------------
//                      任务（QuestRuntime）          成就（本类）
//      登记时机        玩家点「接取」时               **构造时全部登记**
//      resetProgress   true（接了才开始算）           **false（一直生效）**
//      结束方式        玩家点「交付」才发奖           **条件一齐当场解锁发奖**
//
//  ⇒ 所以本类**没有 Accept / Submit**，也就**没有 `EAchievementState`**（只有锁 / 解锁两态）。
//    这不是"少写了"，而是这三件事在成就上**不存在**。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 本片最容易写错的一处：**登记顺序**（一个"看起来像玄学"的 bug）
//  ---------------------------------------------------------------------------
//  `ConditionTracker` 的语义②说得很清楚：`resetProgress: false` 时，
//  **如果注册的那一刻就已经达成，当场回调一次**。这条是**故意**的 ——
//  "上次已经打了 10 只、这次登录就该解锁"，否则玩家得**再打一只**才解锁。
//
//  但"当场回调"意味着：**构造函数里的 `Register` 会重入到 `OnConditionMet`**。
//  于是有一个真实会踩的顺序坑：
//
//      ❌ 写成"登记一条 → 记一条归属"：
//          `Register(4009, ..., false)` 当场回调 → 回调里查 `m_ownerOfCondition[4009]`
//          → **还没记进去** → 认不出这是自己的条件 → `return`（静默）
//          ⇒ 成就**永远不会在登录时解锁**，而且**一行日志都没有**。
//
//      ✅ 写成三段（本类的做法）：
//          ① 先把**全部归属**记好
//          ② 再**逐条登记**（此刻重入回调已经认得出来）
//          ③ 全部登记完再**统一评一遍**（兜住"多条条件、后登记的那条才凑齐"的情况）
//
//  第 ③ 步不是冗余：`AreAllConditionsMet` 走的是 `m_tracker.IsMet`，
//  而**还没登记的条件 `IsMet` 恒为 false** —— 所以"两条都早已达成"的成就，
//  在②的循环里**每条单独触发时都凑不齐**，只能靠③收尾。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 配置错误为什么是**抛异常**（而任务那边是返回值 + 原因）
//  ---------------------------------------------------------------------------
//  项目的规矩是：**玩家能触发的失败 → 返回值 + 一句人话；程序/配置错误 → 抛异常。**
//  成就**没有玩家的那一下点击**（构造时就自动跑），所以"返回值给谁看"这件事不成立 ——
//  只能抛，而且消息里必须点名**是哪个成就、哪条条件、哪个奖励**。
//  这也是与 `ConfigKit` 的 CFG0023 配成**两道闸门**：配置表那道在导出时拦，
//  这道在运行期拦（万一有人绕过流水线手改 SO）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 与任务**共用同一个 `ConditionTracker`**（`Docs\20` §四 的核心复用）
//  ---------------------------------------------------------------------------
//  于是两边的回调都会收到**对方的**条件编号。本类和 `QuestRuntime` 一样，
//  认不出来时**安静忽略**（绝不抛"未知条件"）—— 这是"通用层 + 多个消费者"必须守住的边界。
//  真正保证"不会串"的是 **CFG0023**：一条条件只允许一个持有者，所以编号天然不重叠。
//
//  ⚠️ 那如果"任务与成就抢同一条条件"呢？`ConditionTracker.Register` 会**当场抛异常**
//     （它故意不静默覆盖）。CFG0023 就是**为了在配置阶段抓住这件事**才存在的。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Game.Config;
using NBC.Game.Quest;
using NBC.Shared.Condition;

namespace NBC.Game.Achievement
{
    /// <summary>成就运行时（**一直生效**、条件一齐当场解锁发奖）。</summary>
    public sealed class AchievementRuntime : IDisposable
    {
        /// <summary>成就表。</summary>
        private readonly AchievementConfig m_achievements;

        /// <summary>条件表（与任务共用同一张）。</summary>
        private readonly QuestConditionConfig m_conditions;

        /// <summary>奖励表。</summary>
        private readonly RewardConfig m_rewards;

        /// <summary>条件系统（**与任务共用同一个实例**）。</summary>
        private readonly ConditionTracker m_tracker;

        /// <summary>
        /// 发奖的地方。
        /// <para>⚠️ 类型名叫 `IQuestRewardSink`（历史遗留，见那个文件头）：它本质是一道
        /// **通用的"奖励接缝"**，成就是第二个使用者。这里刻意**不新建一个同形状的接口** ——
        /// 两道一模一样的缝只会让"以后奖励要挪到服务端"变成要改两处。</para>
        /// </summary>
        private readonly IQuestRewardSink m_rewardSink;

        /// <summary>已解锁的成就编号。</summary>
        private readonly HashSet<int> m_unlocked = new HashSet<int>();

        /// <summary>按解锁顺序记（UI 想显示"最近解锁"时不用再排）。</summary>
        private readonly List<int> m_unlockedOrder = new List<int>();

        /// <summary>条件编号 → 它属于哪个成就（用于把达成通知路由回来）。</summary>
        private readonly Dictionary<int, int> m_ownerOfCondition = new Dictionary<int, int>();

        /// <summary>最近一次解锁失败的原因（调试面板显示用；没问题时是 null）。</summary>
        private string m_lastProblem;

        /// <summary>Dispose 过没有（重复 Dispose 要无害）。</summary>
        private bool m_disposed;

        /// <summary>
        /// 造一个成就运行时：**构造即登记全部成就**。
        /// </summary>
        /// <param name="achievements">成就表（不能为 null）。</param>
        /// <param name="conditions">条件表（不能为 null）。</param>
        /// <param name="rewards">奖励表（不能为 null）。</param>
        /// <param name="tracker">条件系统（不能为 null；**应当与任务用的是同一个**）。</param>
        /// <param name="rewardSink">发奖的地方（不能为 null）。</param>
        /// <exception cref="ArgumentNullException">有参数为 null。</exception>
        /// <exception cref="InvalidOperationException">配置有错（缺条件行 / 条件解析失败 / 缺奖励行 / 两个成就抢同一条条件）。</exception>
        public AchievementRuntime(AchievementConfig achievements, QuestConditionConfig conditions,
                                  RewardConfig rewards, ConditionTracker tracker, IQuestRewardSink rewardSink)
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

            m_achievements = achievements;
            m_conditions = conditions;
            m_rewards = rewards;
            m_tracker = tracker;
            m_rewardSink = rewardSink;

            // 订阅必须在登记**之前** —— 因为 `resetProgress: false` 的登记会当场回调（见类头）。
            m_tracker.ProgressChanged += OnConditionProgressChanged;
            m_tracker.ConditionMet += OnConditionMet;

            ValidateAll();

            // ① 先记全部归属（重入回调要查得到）
            // ② 再逐条登记（`resetProgress: false` = 成就与任务的**唯一**差别）
            // ③ 全部登记完统一评一遍（收尾，见类头）
            RegisterAll();
            EvaluateAll();
        }

        /// <summary>一共几个成就（配置表里有几行就是几）。</summary>
        public int AchievementCount
        {
            get { return m_achievements.rows == null ? 0 : m_achievements.rows.Count; }
        }

        /// <summary>已经解锁了几个。</summary>
        public int UnlockedCount
        {
            get { return m_unlocked.Count; }
        }

        /// <summary>最近一次解锁失败的原因（没有问题时是 null）。</summary>
        public string LastProblem
        {
            get { return m_lastProblem; }
        }

        /// <summary>这个成就解锁了没有。</summary>
        /// <param name="achievementId">成就编号。</param>
        /// <returns>解锁了返回 true。</returns>
        public bool IsUnlocked(int achievementId)
        {
            return m_unlocked.Contains(achievementId);
        }

        /// <summary>按解锁顺序复制已解锁的成就编号。</summary>
        /// <param name="buffer">目标列表（会先 Clear）。</param>
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

        // ====================================================================
        //  追踪视图（给 UI / 调试面板）
        // ====================================================================

        /// <summary>取一个成就的追踪视图。</summary>
        /// <param name="achievementId">成就编号。</param>
        /// <param name="tracking">视图（**每次调用新建**，所以别在每帧里调）。</param>
        /// <returns>配置表里有这个成就就返回 true。</returns>
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

        /// <summary>把**全部**成就（解锁的与没解锁的都算）复制进一个列表。</summary>
        /// <param name="buffer">目标列表（会先 Clear；里面的对象是新建的）。</param>
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

        /// <summary>把全部成就拼成一段文本（控制台 / 调试面板用）。</summary>
        /// <returns>多行文本。</returns>
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

        // ====================================================================
        //  条件系统的通知
        // ====================================================================

        /// <summary>某条条件进度变了（**可能是任务的**，认不出来就静默忽略）。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <param name="progress">新进度。</param>
        private void OnConditionProgressChanged(int conditionKey, ConditionProgress progress)
        {
            int achievementId;

            if (!m_ownerOfCondition.TryGetValue(conditionKey, out achievementId))
            {
                return;   // 不是本类的（例如任务的条件）—— 安静忽略
            }

            EventCenter.Instance.Trigger(AchievementEvents.ProgressChanged, achievementId);
        }

        /// <summary>某条条件**达成**了：若该成就全部条件都满，就解锁并发奖。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <param name="progress">达成时的进度。</param>
        private void OnConditionMet(int conditionKey, ConditionProgress progress)
        {
            int achievementId;

            if (!m_ownerOfCondition.TryGetValue(conditionKey, out achievementId))
            {
                return;   // 不是本类的（例如任务的条件）—— 安静忽略
            }

            Evaluate(achievementId);
        }

        // ====================================================================
        //  内部：校验 / 登记 / 评定 / 解锁
        // ====================================================================

        /// <summary>
        /// **先把全部配置校验一遍，再动手登记**（与 `QuestRuntime.Accept` 同一个形状）。
        /// <para>⚠️ 为什么不能"边校验边登记"：登记是**有副作用**的（写进 `ConditionTracker`），
        /// 校验到第 3 个成就才发现奖励表缺行，前两个成就就已经登记进去了 ——
        /// 那是一个**半成品状态**：玩家看不到它，但它会在后台悄悄涨进度。</para>
        /// </summary>
        private void ValidateAll()
        {
            List<Config_Achievement> rows = m_achievements.rows;

            if (rows == null || rows.Count == 0)
            {
                return;     // 没成就不是错误（这个项目可能不做成就）
            }

            var seenConditions = new Dictionary<int, int>();    // 条件编号 → 先占住它的成就编号

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

                    // ⚠️ 两个成就抢同一条条件 → `ConditionTracker.Register` 会**当场抛异常**，
                    //    但那时候抛出来的消息里没有"是哪两个成就"。
                    //    这里提前抓，才能**点名两个成就**（报错要说清是谁跟谁）。
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

                // ⚠️ **先查奖励，再登记**：登记之后条件一齐就会去发奖，
                //    而"要发奖时才发现奖励表没有这一行"是**没法回头**的
                //    （成就是自动解锁的，没有"取消交付"这个动作）。
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

        /// <summary>把全部成就的条件登记进条件系统（`resetProgress: false`）。</summary>
        private void RegisterAll()
        {
            List<Config_Achievement> rows = m_achievements.rows;

            if (rows == null)
            {
                return;
            }

            // ① 先把**全部归属**记好：`Register` 会当场回调（见类头），
            //    回调里要靠这张表认出"这是我的条件"。
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

            // ② 再逐条登记
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

                    // ValidateAll 已经保证这两步都成立（这里只是再走一遍取值）
                    if (!m_conditions.TryGet(row.conditionIds[c], out conditionRow))
                    {
                        continue;
                    }

                    if (!ConditionDef.TryCreate(conditionRow.eventType, conditionRow.targetId,
                                                conditionRow.requiredCount, out def, out _))
                    {
                        continue;
                    }

                    // ⚠️ **这一行就是"任务"与"成就"的唯一差别**：false = 不清零、且已达成时当场回调。
                    m_tracker.Register(row.conditionIds[c], def, false);
                }
            }
        }

        /// <summary>
        /// 把全部成就**统一评一遍**。
        /// <para>必要性见类头第 ③ 段：多条条件的成就，在逐条登记的过程中
        /// 每条单独回调时都凑不齐（后面的条件还没登记，`IsMet` 恒 false）。</para>
        /// </summary>
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

        /// <summary>评一个成就是不是该解锁了（该解就解；已解过就什么都不做）。</summary>
        /// <param name="achievementId">成就编号。</param>
        private void Evaluate(int achievementId)
        {
            if (m_unlocked.Contains(achievementId))
            {
                return;     // 已经解过 —— **不能重复发奖**
            }

            Config_Achievement row;

            if (!m_achievements.TryGet(achievementId, out row) || !AreAllConditionsMet(row))
            {
                return;
            }

            Unlock(row);
        }

        /// <summary>这个成就的全部条件都达成了吗。</summary>
        /// <param name="row">成就配置行。</param>
        /// <returns>都达成了返回 true（条件为空时返回 false）。</returns>
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

        /// <summary>解锁一个成就并发奖。</summary>
        /// <param name="row">成就配置行。</param>
        private void Unlock(Config_Achievement row)
        {
            Config_Reward rewardRow;

            if (!m_rewards.TryGet(row.rewardId, out rewardRow))
            {
                // ⚠️ 这里**不能抛异常**：我们此刻在 `ConditionTracker.Notify` 的派发循环里
                //    （一次击杀可能同时推进好几条条件），抛出去会把**别人**的进度派发一起打断。
                //    所以改成"记录 + 广播"，让问题**有痕迹**但不扩大伤害。
                m_lastProblem = "成就 " + row.id + " 该解锁，但奖励 " + row.rewardId +
                                " 在 Reward 表里找不到 —— **没有解锁**（避免出现「成就亮了奖励没到」）。";

                EventCenter.Instance.Trigger(AchievementEvents.UnlockFailed,
                    new AchievementUnlockFailedPayload(row.id, m_lastProblem));
                return;
            }

            m_unlocked.Add(row.id);
            m_unlockedOrder.Add(row.id);
            m_lastProblem = null;

            QuestReward reward = new QuestReward(rewardRow.id, rewardRow.exp, rewardRow.gold,
                                                 rewardRow.itemId, rewardRow.itemCount);

            // ⚠️ 顺序（与 `QuestRuntime.Submit` 同一个理由）：**先改状态、再发奖**。
            //    成就的"状态"只有内存里这一个集合，改它是不会失败的；
            //    而发奖可能触发订阅方（比如背包满、UI 弹窗）再反过来查"这个成就解锁了吗"——
            //    那时必须已经是"已解锁"。
            m_rewardSink.Grant(row.id, reward);

            EventCenter.Instance.Trigger(AchievementEvents.Unlocked,
                new AchievementUnlockedPayload(row.id, reward.RewardId));
        }

        /// <summary>造一个追踪视图（**每次新建对象**）。</summary>
        /// <param name="row">成就配置行。</param>
        /// <returns>视图。</returns>
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
                    // 配置有错时**照样给出这一行**（说清问题），而不是静默少一行
                    QuestConditionLine broken = new QuestConditionLine();
                    broken.ConditionId = conditionIds[i];
                    broken.Description = "⚠️ 条件 " + conditionIds[i] + " 配置有错";
                    tracking.Conditions.Add(broken);
                    continue;
                }

                ConditionProgress progress;

                if (!m_tracker.TryGetProgress(conditionIds[i], out progress))
                {
                    // ⚠️ 正常不该发生（构造时就登记了）。但"取不到"要**如实显示成 0/N**，
                    //    而不是抛异常 —— 追踪视图是给**看**的，看板自己崩掉最差。
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

            return tracking;
        }

        /// <summary>退订事件、注销全部条件（可以重复调用）。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;

            m_tracker.ProgressChanged -= OnConditionProgressChanged;
            m_tracker.ConditionMet -= OnConditionMet;

            // ⚠️ **只注销登记，不动进度**（`ConditionTracker.Unregister` 的契约）：
            //    成就的进度是跨局的，重进一局要能接着算 —— 这正是 `resetProgress: false` 的意义。
            foreach (KeyValuePair<int, int> pair in m_ownerOfCondition)
            {
                m_tracker.Unregister(pair.Key);
            }

            m_ownerOfCondition.Clear();
        }
    }
}
