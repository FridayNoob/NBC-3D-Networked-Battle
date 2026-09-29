// ============================================================================
//  QuestRuntime —— 任务运行时：接取 → 进度 → 完成 → 交付 → 发奖
//  项目：3D联网战斗Demo   对应：M2-A2、Docs\20 §三 M2 的验收线
//
//  ---------------------------------------------------------------------------
//  一条任务的完整生命周期（每一步都写在这里，因为它就是"闭环"本身）
//  ---------------------------------------------------------------------------
//      Accept(3001)
//        ├─ 查配置表 Quest → 拿 conditionIds / rewardId
//        ├─ **先把全部条件都校验一遍**（缺一行就整体不接，见下）
//        ├─ 逐条向 ConditionTracker 登记（resetProgress = true：接了才开始算）
//        └─ 状态 → Accepted，广播 Quest.Accepted
//
//      （玩家去打怪）战斗 → 事件 → ConditionEventBridge → ConditionTracker.Notify
//        └─ 条件满 → ConditionTracker.ConditionMet → 本类 OnConditionMet
//             └─ 该任务**所有**条件都满了 → 状态 → Completed，广播 Quest.Completed
//
//      Submit(3001)
//        ├─ 状态必须是 Completed（否则给一句人话原因）
//        ├─ **先查奖励表**（查不到就整体不交付 —— 不能出现"交付了但没奖励"）
//        ├─ 注销条件、状态 → Submitted、发奖、广播 Quest.Submitted + Quest.RewardGranted
//        └─ 奖励经 IQuestRewardSink 发下去（M4 换成服务端权威实现）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 两条刻意做成"全有或全无"的地方（半成品状态最难查）
//  ---------------------------------------------------------------------------
//  ① `Accept` 如果"登记到第三条才发现条件表里没这行"，会把前两条留在
//     `ConditionTracker` 里 —— 一个**半接取**的任务：UI 上没有它，
//     但它会在后台悄悄累计进度。所以**先全部校验，再统一登记**。
//  ② `Submit` 如果"状态改了但奖励表缺行"，玩家就白白损失一个任务。
//     所以**先查奖励，再改状态**。
//
//  ---------------------------------------------------------------------------
//  ⚠️ `OnConditionMet` 为什么**不能**假设"这个条件一定是任务的"
//  ---------------------------------------------------------------------------
//  任务与成就**共用同一个 `ConditionTracker`**（`Docs\20` §四 的核心复用）。
//  所以达成通知里会混着**成就的**条件编号 —— 本类认不出来时必须**安静地忽略**，
//  而不是抛"未知条件"异常。这是"通用层 + 多个消费者"结构里必须守住的边界。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Game.Config;
using NBC.Shared.Condition;

namespace NBC.Game.Quest
{
    /// <summary>任务运行时（**单机就能闭环**；M4 起进度改由服务端权威校验）。</summary>
    public sealed class QuestRuntime : IDisposable
    {
        /// <summary>任务表。</summary>
        private readonly QuestConfig m_quests;

        /// <summary>条件表。</summary>
        private readonly QuestConditionConfig m_conditions;

        /// <summary>奖励表。</summary>
        private readonly RewardConfig m_rewards;

        /// <summary>条件系统（任务与成就共用）。</summary>
        private readonly ConditionTracker m_tracker;

        /// <summary>发奖的地方。</summary>
        private readonly IQuestRewardSink m_rewardSink;

        /// <summary>每个任务当前的状态（不在表里 = None）。</summary>
        private readonly Dictionary<int, EQuestState> m_states = new Dictionary<int, EQuestState>();

        /// <summary>条件编号 → 它属于哪个任务（用于把达成通知路由回来）。</summary>
        private readonly Dictionary<int, int> m_ownerOfCondition = new Dictionary<int, int>();

        /// <summary>已接取的任务编号（含"已完成待交付"，按接取顺序）。</summary>
        private readonly List<int> m_active = new List<int>();

        /// <summary>Dispose 过没有（重复 Dispose 要无害）。</summary>
        private bool m_disposed;

        /// <summary>造一个任务运行时。</summary>
        /// <param name="quests">任务表（不能为 null）。</param>
        /// <param name="conditions">条件表（不能为 null）。</param>
        /// <param name="rewards">奖励表（不能为 null）。</param>
        /// <param name="tracker">条件系统（不能为 null）。</param>
        /// <param name="rewardSink">发奖的地方（不能为 null；M2 用内存实现）。</param>
        public QuestRuntime(QuestConfig quests, QuestConditionConfig conditions, RewardConfig rewards,
                            ConditionTracker tracker, IQuestRewardSink rewardSink)
        {
            if (quests == null) { throw new ArgumentNullException(nameof(quests), "[QuestRuntime] 任务表是 null。"); }
            if (conditions == null) { throw new ArgumentNullException(nameof(conditions), "[QuestRuntime] 条件表是 null。"); }
            if (rewards == null) { throw new ArgumentNullException(nameof(rewards), "[QuestRuntime] 奖励表是 null。"); }

            if (tracker == null)
            {
                throw new ArgumentNullException(nameof(tracker), "[QuestRuntime] 条件系统是 null。");
            }

            if (rewardSink == null)
            {
                throw new ArgumentNullException(nameof(rewardSink), "[QuestRuntime] 发奖实现是 null。");
            }

            m_quests = quests;
            m_conditions = conditions;
            m_rewards = rewards;
            m_tracker = tracker;
            m_rewardSink = rewardSink;

            // 订阅条件系统的两个通知（见类头说明：达成通知里会有**成就的**条件，认不出来要安静忽略）
            m_tracker.ProgressChanged += OnConditionProgressChanged;
            m_tracker.ConditionMet += OnConditionMet;
        }

        /// <summary>当前接了但还没交付的任务数。</summary>
        public int ActiveCount
        {
            get { return m_active.Count; }
        }

        /// <summary>某个任务当前的状态。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>状态（没接过 = None）。</returns>
        public EQuestState StateOf(int questId)
        {
            EQuestState state;
            return m_states.TryGetValue(questId, out state) ? state : EQuestState.None;
        }

        /// <summary>
        /// 把所有**可接任务**（配置里有、玩家状态是 None）复制进一个列表。
        /// <para>
        /// ⚠️ 为什么这个方法在运行时，而不是让 UI 自己去翻配置表：
        /// "哪些能接"是**玩法规则**（后面前置条件、等级限制、日常限次都会进来），
        /// 而 UI 只该问"现在能接什么"。规则放这里，变化时不会漏到 UI 里去。
        /// </para>
        /// <para>顺序 = 配置表里的顺序（策划排的顺序就是 UI 的顺序）。</para>
        /// </summary>
        /// <param name="buffer">目标列表（会先 Clear）。</param>
        public void CopyOffers(List<QuestOffer> buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            buffer.Clear();

            List<Config_Quest> rows = m_quests.rows;

            if (rows == null)
            {
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                Config_Quest row = rows[i];

                // 接过（含"已完成待交付"与"已交付"）的就不再是"可接"
                if (StateOf(row.id) != EQuestState.None)
                {
                    continue;
                }

                buffer.Add(new QuestOffer(row.id, row.name, row.desc));
            }
        }

        /// <summary>把"已接取的任务编号"复制进一个列表（UI 遍历用；顺序 = 接取顺序）。</summary>
        /// <param name="buffer">目标列表（会先 Clear）。</param>
        public void CopyActiveQuestIds(List<int> buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            buffer.Clear();

            for (int i = 0; i < m_active.Count; i++)
            {
                buffer.Add(m_active[i]);
            }
        }

        // ====================================================================
        //  接取 / 交付 / 放弃
        // ====================================================================

        /// <summary>接取一个任务。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>结果（失败时带一句人话原因）。</returns>
        public QuestActionResult Accept(int questId)
        {
            EnsureNotDisposed();

            Config_Quest quest;

            if (!m_quests.TryGet(questId, out quest))
            {
                return Reject(questId, "配置表 Quest 里没有任务 " + questId + "。" +
                                       "（先跑 ConfigKit 生成、再点配置表导入菜单。）");
            }

            EQuestState state = StateOf(questId);
            string blocked = DescribeBlock(questId, state, EQuestState.None);

            if (blocked != null)
            {
                return Reject(questId, blocked);
            }

            // 解析 + 登记（**先校验，再登记**）。接取要清零；"跟随权威"那条路不清零
            // —— 两份实现迟早漂移，所以这里共用一个帮手（`resetProgress` 是开关）。
            string reason = ResolveAndRegisterConditions(quest, questId, true);

            if (reason != null)
            {
                return Reject(questId, reason);
            }

            m_states[questId] = EQuestState.Accepted;
            m_active.Add(questId);

            EventCenter.Instance.Trigger(QuestEvents.Accepted, questId);
            return QuestActionResult.Success();
        }

        /// <summary>
        /// 解析一个任务的全部条件并登记。**先全部校验、再统一登记**（校验失败时不留半截状态）。
        /// </summary>
        /// <param name="quest">任务（配置行）。</param>
        /// <param name="questId">任务编号（报错文案用）。</param>
        /// <param name="resetProgress">要不要清零进度（接取 = true；跟随权威 = false）。</param>
        /// <returns>成功返回 null；失败返回一句人话原因（调用方决定是 `Reject` 还是静默放弃）。</returns>
        private string ResolveAndRegisterConditions(Config_Quest quest, int questId, bool resetProgress)
        {
            int[] conditionIds = quest.conditionIds;

            if (conditionIds == null || conditionIds.Length == 0)
            {
                return "任务 " + questId + " 在配置表里**一条条件都没有**。\n" +
                       "这种任务接了就永远做不完（没有任何东西能推进它），所以这里直接拒绝。";
            }

            ConditionDef[] defs = new ConditionDef[conditionIds.Length];

            for (int i = 0; i < conditionIds.Length; i++)
            {
                Config_QuestCondition row;

                if (!m_conditions.TryGet(conditionIds[i], out row))
                {
                    return "任务 " + questId + " 的第 " + (i + 1) + " 条条件 " + conditionIds[i] +
                           " 在 QuestCondition 表里找不到。";
                }

                ConditionDef def;
                string error;

                if (!ConditionDef.TryCreate(row.eventType, row.targetId, row.requiredCount, out def, out error))
                {
                    return "任务 " + questId + " 的条件 " + conditionIds[i] + " 配置有错：" + error;
                }

                defs[i] = def;
            }

            for (int i = 0; i < conditionIds.Length; i++)
            {
                m_tracker.Register(conditionIds[i], defs[i], resetProgress);
                m_ownerOfCondition[conditionIds[i]] = questId;
            }

            return null;
        }

        /// <summary>
        /// **跟随权威**：把本地这个任务的状态改成服务端说的那个（M4-S3 `Docs\27` §二十八）。
        ///
        /// <para>⚠️ 它**只改状态**，绝不做本地动作：**不发奖**、**不触发**「接取/交付成功」事件、
        /// **不给玩家任何提示**、而且**不重置进度**（`resetProgress: false`）。
        /// 那些都是服务端的事 —— 本地再做一遍就是"客户端自己当权威"（§二十一修掉的洞）。</para>
        ///
        /// <para>⚠️ **幂等**：本地已经等于目标状态就直接返回 0（"同一件事做两遍"是这一片要防的
        /// 静默错误：不报错，只让玩家状态慢慢跑偏）。</para>
        ///
        /// <para>⚠️ 与 `Accept`/`Submit` 的关系：那两个是"**玩家动作**"（要清零/发奖/触发事件），
        /// 这个是"**追随权威**"（只对齐状态）。别用它去顶替玩家动作。</para>
        /// </summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="authoritativeState">服务端说的状态：1=已接 2=已完成 3=已交付；**0=回退成未接取**。</param>
        /// <returns>真的改变了本地状态返回 1；已经一致、或本地没这个任务返回 0。</returns>
        public int FollowState(int questId, int authoritativeState)
        {
            EnsureNotDisposed();

            Config_Quest quest;

            if (!m_quests.TryGet(questId, out quest))
            {
                return 0;       // 本地配置表里没有 ⇒ 什么都不做（**不许猜**）
            }

            EQuestState target = ToState(authoritativeState);
            EQuestState current = StateOf(questId);

            if (target == current)
            {
                return 0;       // ⚠️ 幂等：这一条就是"同一份同步施加两次 ⇒ 0 次跟随"的保证
            }

            if (target == EQuestState.None)
            {
                // 回退：**只**改状态 + 注销条件登记，**不动进度行**（与"放弃任务没做"同源，见 §28）
                UnregisterConditions(quest);
                m_states[questId] = EQuestState.None;
                m_active.Remove(questId);
                return 1;
            }

            if (current == EQuestState.None)
            {
                // 从"没接过"往上跟随：条件要登记，但**绝不清零**（进度按服务端那份算）
                if (ResolveAndRegisterConditions(quest, questId, false) != null)
                {
                    return 0;   // 配置有问题 ⇒ 静默不跟随（**不触发 Rejected 事件**，那也是一种副作用）
                }

                m_active.Add(questId);
            }

            m_states[questId] = target;

            if (target == EQuestState.Submitted)
            {
                // 与 `Submit` 的收尾一致（注销条件、移出进行中），但**不发奖、不触发事件**
                m_active.Remove(questId);
                UnregisterConditions(quest);
            }

            return 1;
        }

        /// <summary>把协议里的状态数字翻成枚举（0..3；其余一律当 0 = 未接取）。</summary>
        /// <param name="state">状态数字。</param>
        /// <returns>状态。</returns>
        private static EQuestState ToState(int state)
        {
            switch (state)
            {
                case 1: return EQuestState.Accepted;
                case 2: return EQuestState.Completed;
                case 3: return EQuestState.Submitted;
                default: return EQuestState.None;
            }
        }

        /// <summary>交付一个**已完成**的任务并发奖励。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>结果（失败时带一句人话原因）。</returns>
        public QuestActionResult Submit(int questId)
        {
            EnsureNotDisposed();

            EQuestState state = StateOf(questId);
            string blocked = DescribeBlock(questId, state, EQuestState.Completed);

            if (blocked != null)
            {
                return Reject(questId, blocked);
            }

            Config_Quest quest;

            if (!m_quests.TryGet(questId, out quest))
            {
                return Reject(questId, "配置表 Quest 里没有任务 " + questId + "。");
            }

            Config_Reward rewardRow;

            // ⚠️ **先查奖励表，再改状态**：查不到就整体不交付，
            //    否则玩家会处于"任务没了、奖励也没到"的状态（最气人的一种 bug）。
            if (!m_rewards.TryGet(quest.rewardId, out rewardRow))
            {
                return Reject(questId, "任务 " + questId + " 的奖励 " + quest.rewardId +
                                       " 在 Reward 表里找不到，所以**没有交付**（避免\"任务没了奖励也没到\"）。");
            }

            // -------- 注销条件、改状态、发奖 --------
            UnregisterConditions(quest);

            m_states[questId] = EQuestState.Submitted;
            m_active.Remove(questId);

            QuestReward reward = new QuestReward(rewardRow.id, rewardRow.exp, rewardRow.gold,
                                                 rewardRow.itemId, rewardRow.itemCount);

            m_rewardSink.Grant(questId, reward);

            EventCenter.Instance.Trigger(QuestEvents.Submitted, questId);
            EventCenter.Instance.Trigger(QuestEvents.RewardGranted,
                new QuestRewardGrantedPayload(questId, reward.RewardId));

            return QuestActionResult.Success();
        }

        /// <summary>放弃一个任务（**演示/重跑用**：注销条件、状态回到 None）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>结果。</returns>
        public QuestActionResult Abandon(int questId)
        {
            EnsureNotDisposed();

            EQuestState state = StateOf(questId);

            if (state == EQuestState.None)
            {
                return Reject(questId, "任务 " + questId + " 没有接过，没什么可放弃的。");
            }

            if (state == EQuestState.Submitted)
            {
                return Reject(questId, "任务 " + questId + " 已经交付过了，不能放弃（奖励都发了）。");
            }

            Config_Quest quest;

            if (m_quests.TryGet(questId, out quest))
            {
                UnregisterConditions(quest);
            }

            m_states[questId] = EQuestState.None;
            m_active.Remove(questId);

            EventCenter.Instance.Trigger(QuestEvents.Abandoned, questId);
            return QuestActionResult.Success();
        }

        // ====================================================================
        //  追踪视图（给 UI）
        // ====================================================================

        /// <summary>取一个任务的追踪视图。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="tracking">视图（**每次调用新建一个对象**，所以别在每帧里调）。</param>
        /// <returns>配置表里有这个任务就返回 true（**没接过的任务也能取到**，方便"可接列表"）。</returns>
        public bool TryGetTracking(int questId, out QuestTracking tracking)
        {
            Config_Quest quest;

            if (!m_quests.TryGet(questId, out quest))
            {
                tracking = null;
                return false;
            }

            tracking = BuildTracking(quest);
            return true;
        }

        /// <summary>把所有**已接取**任务的追踪视图复制进一个列表。</summary>
        /// <param name="buffer">目标列表（会先 Clear；里面的对象是新建的）。</param>
        public void CopyActiveTrackings(List<QuestTracking> buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            buffer.Clear();

            for (int i = 0; i < m_active.Count; i++)
            {
                Config_Quest quest;

                if (m_quests.TryGet(m_active[i], out quest))
                {
                    buffer.Add(BuildTracking(quest));
                }
            }
        }

        /// <summary>把"已接取任务"的追踪信息拼成一段文本（控制台/调试面板用）。</summary>
        /// <returns>多行文本。</returns>
        public string DescribeActive()
        {
            List<QuestTracking> trackings = new List<QuestTracking>();
            CopyActiveTrackings(trackings);

            if (trackings.Count == 0)
            {
                return "[QuestRuntime] 当前没有已接取的任务。";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("[QuestRuntime] 已接取 ").Append(trackings.Count).Append(" 个任务：\n");

            for (int i = 0; i < trackings.Count; i++)
            {
                builder.Append(trackings[i].ToString());
            }

            return builder.ToString();
        }

        // ====================================================================
        //  条件系统的通知
        // ====================================================================

        /// <summary>某条条件的进度变了（**可能是成就的**，认不出来就忽略）。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <param name="progress">新进度。</param>
        private void OnConditionProgressChanged(int conditionKey, ConditionProgress progress)
        {
            int questId;

            if (!m_ownerOfCondition.TryGetValue(conditionKey, out questId))
            {
                return;   // 不是本类管的（例如成就的条件）—— 安静忽略
            }

            EventCenter.Instance.Trigger(QuestEvents.ProgressChanged, questId);
        }

        /// <summary>某条条件**达成**了：若该任务所有条件都满了，就把它标成"可交付"。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <param name="progress">达成时的进度。</param>
        private void OnConditionMet(int conditionKey, ConditionProgress progress)
        {
            int questId;

            if (!m_ownerOfCondition.TryGetValue(conditionKey, out questId))
            {
                return;   // 不是本类管的（例如成就的条件）—— 安静忽略
            }

            // 已经交付/放弃了就别再改状态（注销与通知之间可能有窗口）
            if (StateOf(questId) != EQuestState.Accepted)
            {
                return;
            }

            Config_Quest quest;

            if (!m_quests.TryGet(questId, out quest) || !AreAllConditionsMet(quest))
            {
                return;
            }

            m_states[questId] = EQuestState.Completed;

            // ⚠️ 这里只广播"可以交付了"，**不自动发奖**：
            //    发奖是玩家的动作（去交任务），自动发会让"任务"这个玩法退化成"计数器"。
            EventCenter.Instance.Trigger(QuestEvents.Completed, questId);
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>这个任务的全部条件都达成了吗。</summary>
        /// <param name="quest">任务配置行。</param>
        /// <returns>都达成了返回 true（**条件为空时返回 false**：没有条件的任务不算完成）。</returns>
        private bool AreAllConditionsMet(Config_Quest quest)
        {
            int[] conditionIds = quest.conditionIds;

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

        /// <summary>注销一个任务的全部条件（**只注销登记，不动进度**）。</summary>
        /// <param name="quest">任务配置行。</param>
        private void UnregisterConditions(Config_Quest quest)
        {
            int[] conditionIds = quest.conditionIds;

            if (conditionIds == null)
            {
                return;
            }

            for (int i = 0; i < conditionIds.Length; i++)
            {
                m_tracker.Unregister(conditionIds[i]);
                m_ownerOfCondition.Remove(conditionIds[i]);
            }
        }

        /// <summary>造一个追踪视图。</summary>
        /// <param name="quest">任务配置行。</param>
        /// <returns>视图。</returns>
        private QuestTracking BuildTracking(Config_Quest quest)
        {
            QuestTracking tracking = new QuestTracking();
            tracking.QuestId = quest.id;
            tracking.Name = quest.name;
            tracking.Description = quest.desc;
            tracking.State = StateOf(quest.id);

            int[] conditionIds = quest.conditionIds;

            if (conditionIds == null)
            {
                return tracking;
            }

            for (int i = 0; i < conditionIds.Length; i++)
            {
                Config_QuestCondition row;
                ConditionDef def;

                if (!m_conditions.TryGet(conditionIds[i], out row) ||
                    !ConditionDef.TryCreate(row.eventType, row.targetId, row.requiredCount, out def, out _))
                {
                    // 配置有错时**照样给出这一行**（描述写清问题），而不是静默少一行
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

            return tracking;
        }

        /// <summary>
        /// 状态不对时给一句人话原因（**说清"现在是什么状态"**，而不只是"不能这么做"）。
        /// </summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="actual">当前状态。</param>
        /// <param name="expected">需要的状态。</param>
        /// <returns>可以继续时返回 null；否则返回原因。</returns>
        private static string DescribeBlock(int questId, EQuestState actual, EQuestState expected)
        {
            if (actual == expected)
            {
                return null;
            }

            if (expected == EQuestState.None)
            {
                switch (actual)
                {
                    case EQuestState.Accepted:
                        return "任务 " + questId + " 已经接过了。";
                    case EQuestState.Completed:
                        return "任务 " + questId + " 已经接过了，而且已经做完了 —— 去交任务吧。";
                    case EQuestState.Submitted:
                        return "任务 " + questId + " 已经交付过了。";
                    default:
                        return "任务 " + questId + " 现在不能接（状态 " + actual + "）。";
                }
            }

            // expected == Completed
            switch (actual)
            {
                case EQuestState.None:
                    return "任务 " + questId + " 还没接，不能交付。";
                case EQuestState.Accepted:
                    return "任务 " + questId + " 还没做完，不能交付（条件见任务追踪）。";
                case EQuestState.Submitted:
                    return "任务 " + questId + " 已经交付过了。";
                default:
                    return "任务 " + questId + " 现在不能交付（状态 " + actual + "）。";
            }
        }

        /// <summary>拒绝一次操作：广播原因（让 UI 能显示）+ 返回带原因的失败结果。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="reason">原因。</param>
        /// <returns>失败结果。</returns>
        private static QuestActionResult Reject(int questId, string reason)
        {
            // ⚠️ 被拒绝的原因**不能只留在返回值里**：调用方很可能是 UI，
            //    它多半只判了 Ok/失败就完事。广播出去，谁都看得见。
            EventCenter.Instance.Trigger(QuestEvents.Rejected, new QuestRejectedPayload(questId, reason));
            return QuestActionResult.Fail(reason);
        }

        /// <summary>Dispose 之后不许再用（防止"对象已经拆了还在被调"）。</summary>
        private void EnsureNotDisposed()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException(nameof(QuestRuntime),
                    "[QuestRuntime] 已经 Dispose 了，不能再调用。");
            }
        }

        /// <summary>退订条件系统的事件、注销全部条件（可以重复调用）。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;

            m_tracker.ProgressChanged -= OnConditionProgressChanged;
            m_tracker.ConditionMet -= OnConditionMet;

            m_ownerOfCondition.Clear();
            m_active.Clear();
        }
    }
}
