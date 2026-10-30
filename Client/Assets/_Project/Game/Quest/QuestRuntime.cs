// 这个文件里有：
//   QuestRuntime（类）：任务运行时，管接取、累计进度、判定完成、交付发奖的整条闭环

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Game.Config;
using NBC.Shared.Condition;

namespace NBC.Game.Quest
{
    /// <summary>任务运行时：任务状态与奖励的唯一入口，接取、进度、完成、交付、发奖都经过这里。</summary>
    // 主要功能：管一条任务从接取、累计进度、判定完成、交付到发奖的全过程，不碰界面与网络。
    // 主要数据成员：三张配置表（任务 / 条件 / 奖励）、与成就共用的 ConditionTracker、发奖接缝 IQuestRewardSink、每个任务的状态与接取顺序。
    // 主要外部方法：Accept / Submit / Abandon / FollowState / CopyOffers / CopyActiveQuestIds / CopyActiveTrackings / TryGetTracking / DescribeActive / StateOf / Dispose。
    // 谁在用它：BattleSession 造一个并转成玩法入口；QuestPanelModel 拿它刷新任务面板；Boot 的 M2DemoBehaviour 与 Editor 的 NetDebugWindow 直接调其中少数几个公开方法；EditMode 用例直接构造来断言状态迁移。
    // 注意：接取必须先把全部条件校验一遍、再统一登记，否则失败时会留下界面看不见、却在后台累计进度的半接取任务。
    // 注意：交付必须先查奖励表、再改状态，否则会出现任务已经没了、奖励也没到。
    // 注意：条件系统与成就共用，达成通知里混着成就的条件编号，本类认不出来时必须安静忽略，不能抛未知条件异常。
    // 注意：单机即可闭环；M4 起进度改由服务端权威校验，本地只跟随状态（见 FollowState）。
    public sealed class QuestRuntime : IDisposable
    {
        // 任务表：任务编号 → 配置行，构造后不变
        private readonly QuestConfig m_quests;

        // 条件表：条件编号 → 配置行，任务与成就共用同一份，构造后不变
        private readonly QuestConditionConfig m_conditions;

        // 奖励表：奖励编号 → 配置行，交付时按任务行的 rewardId 查
        private readonly RewardConfig m_rewards;

        // 条件系统：任务与成就共用的那个实例，本类只登记自己的条件编号
        private readonly ConditionTracker m_tracker;

        // 发奖出口：M2 是内存实现，M4 换服务端权威实现
        private readonly IQuestRewardSink m_rewardSink;

        // 每个任务当前的状态（取值 0..3），键为任务编号；不在表里等于 EQuestState.None
        private readonly Dictionary<int, EQuestState> m_states = new Dictionary<int, EQuestState>();

        // 条件编号 → 所属任务编号，只装本类登记过的条件，用于把达成通知路由回来
        private readonly Dictionary<int, int> m_ownerOfCondition = new Dictionary<int, int>();

        // 已接取的任务编号（含已完成待交付），按接取顺序排列；元素个数即 ActiveCount
        private readonly List<int> m_active = new List<int>();

        // 是否已 Dispose；true 之后除 Dispose 外的公开方法都抛 ObjectDisposedException
        private bool m_disposed;

        // 功能：收下三张配置表、共用的条件系统与发奖出口，并订阅条件的进度与达成通知。
        // 入参：quests 任务表（不能为 null）；conditions 条件表（不能为 null）；rewards 奖励表（不能为 null）；tracker 条件系统（不能为 null，与成就共用）；rewardSink 发奖出口（不能为 null，M2 用内存实现）
        // 返回：无
        // 步骤：
        //   1) 五个依赖逐个查空，缺一个就抛 ArgumentNullException 并在提示里点名，避免半装配的对象跑到运行期
        //   2) 先赋值、最后才订阅：订阅在前的话，回调里读到的会是空字段
        //   3) 订阅 ProgressChanged 与 ConditionMet 两个通知，任务侧只被动接收，不轮询条件系统
        // 上游：BattleSession 的构造函数（Game 内唯一装配点）
        // 下游：ConditionTracker 的两个事件
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

            m_tracker.ProgressChanged += OnConditionProgressChanged;
            m_tracker.ConditionMet += OnConditionMet;
        }

        // 功能：报告当前接了但还没交付的任务个数。
        // 入参：无
        // 返回：已接取任务数（含已完成待交付），0 表示手上没有任务
        // 上游：EditMode 用例断言手上任务数；任务面板不读它，直接走 CopyActiveTrackings
        // 下游：m_active
        public int ActiveCount
        {
            get { return m_active.Count; }
        }

        /// <summary>查一个任务当前的状态；没接过的任务返回 None。</summary>
        // 功能：把任务编号翻成状态，查不到就当作没接过。
        // 入参：questId 任务编号
        // 返回：状态值（0..3）；没有记录时为 EQuestState.None
        // 步骤：
        //   1) 用 TryGetValue 一次取值，未命中就落到 None —— 不区分没接过与放弃过，两者本来就同义
        // 上游：任务面板分桶、Editor 的 NetDebugWindow 跟随权威时读本地状态
        // 下游：无
        public EQuestState StateOf(int questId)
        {
            EQuestState state;
            return m_states.TryGetValue(questId, out state) ? state : EQuestState.None;
        }

        // 功能：把配置里存在、玩家还没接过的任务按配置表顺序填进调用方的列表。
        // 入参：buffer 目标列表（不能为 null；会先 Clear，列表由调用方复用）
        // 返回：无（结果都写进 buffer）
        // 步骤：
        //   1) 先查空并抛 ArgumentNullException，免得把 null 带到别的调用方
        //   2) 先 Clear 再填：调用方复用的是同一个列表，不清会重复追加
        //   3) 配置行集合为 null 时直接返回，空表与没有表同样处理
        //   4) 按配置表顺序遍历，跳过状态不是 None 的：接过、可交付、已交付都不再算可接
        //   5) 剩下的包成 QuestOffer 加进 buffer，顺序即策划排的顺序，也就是界面顺序
        // 上游：QuestPanelModel 刷新可接列表
        // 下游：StateOf
        // 注意：为什么在运行时判定、而不是让界面自己去翻配置表 —— 哪些能接是玩法规则（前置条件、等级限制、日常限次都会进来），规则留在这里，变化时不会漏到界面里去。
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

                if (StateOf(row.id) != EQuestState.None)
                {
                    continue;
                }

                buffer.Add(new QuestOffer(row.id, row.name, row.desc));
            }
        }

        // 功能：把已接取的任务编号按接取顺序填进调用方的列表。
        // 入参：buffer 目标列表（不能为 null；会先 Clear）
        // 返回：无（结果都写进 buffer）
        // 步骤：
        //   1) 先查空、再 Clear，理由与 CopyOffers 相同
        //   2) 按 m_active 的下标顺序逐个拷进 buffer，这个顺序就是接取顺序
        // 上游：任务面板与调试输出遍历手上的任务
        // 下游：无
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


        // 功能：把一个任务接到玩家身上：校验配置、登记条件、置为已接取并广播接取成功。
        // 入参：questId 任务编号（必须在任务表里，且当前状态为 None）
        // 返回：QuestActionResult；Ok 为 true 表示已接取并登记了条件，失败时 Reason 是一句给玩家看的原因
        // 步骤：
        //   1) 先 EnsureNotDisposed，拆掉的对象不许再改状态
        //   2) 查任务配置，查不到就把「先跑 ConfigKit 生成、再点配置表导入菜单」写进原因，省得报错只剩一个编号
        //   3) 用 DescribeBlock 判是否可接，已接、可交付、已交付一律拒绝
        //   4) 解析并登记条件，resetProgress 传 true：玩家主动接取要从零开始算
        //   5) 全部成功后才改状态、记进 m_active，最后广播 Quest.Accepted
        // 上游：BattleSession.AcceptQuest、QuestPanelModel（玩家点接取）
        // 下游：DescribeBlock、ResolveAndRegisterConditions、Reject、EventCenter
        // 注意：先校验、再登记 —— 若登记到第三条才发现条件表缺行，前两条会留在 ConditionTracker 里，变成界面看不见却仍在累计的半接取任务。
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

        // 功能：把一个任务的全部条件解析成 ConditionDef 并登记；失败时不留下任何登记。
        // 入参：quest 任务配置行；questId 任务编号（只用于报错文案）；resetProgress 是否清零进度（接取传 true，跟随权威传 false）
        // 返回：成功返回 null；失败返回一句人话原因，由调用方决定走 Reject 还是静默放弃
        // 步骤：
        //   1) 条件数组为 null 或空时直接报错：没有条件的任务接了就永远做不完，没有任何东西能推进它
        //   2) 第一轮只解析与校验：条件表缺行、ConditionDef 建不出来都直接返回原因，此时一条都还没登记
        //   3) 第二轮才统一登记：m_tracker.Register 加记下条件归属，两轮分开正是「先校验、再登记」的落点
        // 上游：Accept（resetProgress 为 true）、FollowState（false 不清零）；两条路共用这一个帮手，免得两份实现迟早漂移
        // 下游：ConditionTracker.Register
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

        // 功能：跟随权威 —— 本地状态向服务端看齐，本类不做任何本地副作用。
        // 入参：questId 任务编号；authoritativeState 服务端状态：1 已接、2 已完成、3 已交付、0 回退成未接取，其余值一律当 0
        // 返回：本地状态真的变了返回 1；已经一致、或本地配置表里没有这个任务返回 0
        // 步骤：
        //   1) 配置表里没有就返回 0，不猜也不新建
        //   2) 目标与当前相同直接返回 0，这就是幂等：同一份同步施加两次等于 0 次跟随
        //   3) 目标是 0 时只改状态并注销条件登记，不动进度行，与放弃任务同源
        //   4) 本地原本是 None 时，先把条件登记上但不许清零（进度以服务端那份为准）；登记失败就静默返回 0，不广播 Rejected —— 那也是一种副作用
        //   5) 目标是已交付时，照 Submit 的收尾移出进行中并注销条件，但不发奖、不广播
        // 上游：RuntimeQuestFollowSink.FollowToState（IQuestFollowSink 的实现）→ QuestStateReconciler.Apply
        // 下游：ResolveAndRegisterConditions、UnregisterConditions、ToState
        // 注意：它只改状态，绝不做本地动作 —— 不发奖、不触发接取或交付成功事件、不给玩家提示、也不重置进度；那些都是服务端的事，本地再做一遍就是客户端自己当权威。
        // 注意：别拿它顶替玩家的 Accept / Submit：那两个要清零、发奖、触发事件，这个只对齐状态。
        public int FollowState(int questId, int authoritativeState)
        {
            EnsureNotDisposed();

            Config_Quest quest;

            if (!m_quests.TryGet(questId, out quest))
            {
                return 0;
            }

            EQuestState target = ToState(authoritativeState);
            EQuestState current = StateOf(questId);

            if (target == current)
            {
                return 0;
            }

            if (target == EQuestState.None)
            {
                UnregisterConditions(quest);
                m_states[questId] = EQuestState.None;
                m_active.Remove(questId);
                return 1;
            }

            if (current == EQuestState.None)
            {
                if (ResolveAndRegisterConditions(quest, questId, false) != null)
                {
                    return 0;
                }

                m_active.Add(questId);
            }

            m_states[questId] = target;

            if (target == EQuestState.Submitted)
            {
                m_active.Remove(questId);
                UnregisterConditions(quest);
            }

            return 1;
        }

        // 功能：把协议里的状态数字翻成本地的状态。
        // 入参：state 状态数字（1 已接、2 已完成、3 已交付，其余一律当 0）
        // 返回：对应枚举；0 与任何未定义值都返回 None
        // 步骤：
        //   1) 只认 1 / 2 / 3，default 落到 None：服务端多送一个新状态时，本地宁可当没接过也不猜
        // 上游：FollowState
        // 下游：无
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

        // 功能：交付一个已完成的任务，并把奖励交给发奖接缝。
        // 入参：questId 任务编号（当前状态必须是 Completed）
        // 返回：QuestActionResult；Ok 为 true 表示已交付且奖励已发出，失败时 Reason 是一句人话
        // 步骤：
        //   1) 先 EnsureNotDisposed
        //   2) 用 DescribeBlock 判是否可交付，没接、没做完、已交付各给一句对应原因
        //   3) 再查任务配置与奖励表；奖励查不到就整体不交付，状态一个字都不动
        //   4) 成功才注销条件、把状态改成已交付、移出进行中列表
        //   5) 把奖励行包成中性的 QuestReward，交给 IQuestRewardSink 发下去
        //   6) 最后广播 Quest.Submitted 与 Quest.RewardGranted
        // 上游：BattleSession.SubmitQuest、QuestPanelModel（玩家点交付）
        // 下游：UnregisterConditions、IQuestRewardSink.Grant、EventCenter
        // 注意：先查奖励、再改状态 —— 否则奖励缺行时玩家会处在任务没了、奖励也没到的状态，这是最气人的一种 bug。
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

            if (!m_rewards.TryGet(quest.rewardId, out rewardRow))
            {
                return Reject(questId, "任务 " + questId + " 的奖励 " + quest.rewardId +
                                       " 在 Reward 表里找不到，所以**没有交付**（避免\"任务没了奖励也没到\"）。");
            }

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

        /// <summary>放弃一个任务：注销条件、状态回到 None；已交付的不能放弃。</summary>
        // 功能：把手上一个任务丢掉，注销条件并广播放弃，主要给演示与重跑用。
        // 入参：questId 任务编号（不能是没接过或已交付）
        // 返回：QuestActionResult；Ok 为 true 表示状态已回到 None
        // 步骤：
        //   1) 先 EnsureNotDisposed
        //   2) 没接过、已交付都拒绝：前者没什么可放弃，后者奖励已经发出去、不能靠放弃收回
        //   3) 配置行还在就注销条件登记，配置被别人删掉时也不至于崩
        //   4) 状态回 None、移出进行中列表，广播 Quest.Abandoned
        // 上游：Boot 的 M2DemoBehaviour（演示重跑）、EditMode 用例
        // 下游：UnregisterConditions、Reject、EventCenter
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


        // 功能：取一个任务的追踪视图，供任务面板与调试窗口显示。
        // 入参：questId 任务编号；tracking 输出视图（配置表里没有这个任务时为 null）
        // 返回：配置表里有这个任务返回 true；没接过的任务也能取到，方便可接列表
        // 步骤：
        //   1) 查配置行，查不到就把 tracking 置 null 并返回 false
        //   2) 查到就现造一个视图，所以别在每帧里调它
        // 上游：EditMode 用例按编号取单个视图；任务面板走 CopyActiveTrackings 批量取
        // 下游：BuildTracking
        // 注意：每次调用都新建对象，做成 Try 只是为了不靠异常表达「查不到」。
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

        /// <summary>把已接取任务的追踪视图按接取顺序填进列表；每次调用都新建视图对象。</summary>
        // 功能：一次性把手上的任务都变成可显示的追踪视图。
        // 入参：buffer 目标列表（不能为 null；会先 Clear，里面的视图是新造的）
        // 返回：无（结果都写进 buffer）
        // 步骤：
        //   1) 查空并 Clear，列表由调用方复用，每次刷新不产生新的列表分配
        //   2) 按 m_active 的顺序取配置行，配置缺失的任务跳过 —— 任务被删了也不让面板崩
        //   3) 每个都现造视图加进 buffer
        // 上游：QuestPanelModel 刷新追踪区、Boot 的 M2DemoBehaviour 打印、EditMode 用例
        // 下游：BuildTracking
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

        /// <summary>把已接取任务拼成一段多行文本，控制台与调试面板直接打印。</summary>
        // 功能：给调试输出一条现成的任务总览，省得调用方自己拼。
        // 入参：无
        // 返回：多行文本；一个任务都没有时返回「当前没有已接取的任务。」
        // 步骤：
        //   1) 先用一个临时列表取全部追踪视图
        //   2) 空就直接给一句人话，避免只打印一个标题
        //   3) 用 StringBuilder 拼标题与每一行，行数就是手上的任务数
        // 上游：Editor 的 NetDebugWindow、BattleSession 的调试输出
        // 下游：CopyActiveTrackings
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


        // 功能：条件进度一变就转成任务侧的 Quest.ProgressChanged 广播。
        // 入参：conditionKey 条件编号；progress 新进度（本方法不读它，只负责转发）
        // 返回：无
        // 步骤：
        //   1) 查条件归属，不是本类登记的（例如成就的）就安静返回，不抛未知条件异常
        //   2) 认得出的才按任务编号广播，界面据此重画进度
        // 上游：ConditionTracker.ProgressChanged
        // 下游：EventCenter（Quest.ProgressChanged）
        private void OnConditionProgressChanged(int conditionKey, ConditionProgress progress)
        {
            int questId;

            if (!m_ownerOfCondition.TryGetValue(conditionKey, out questId))
            {
                return;
            }

            EventCenter.Instance.Trigger(QuestEvents.ProgressChanged, questId);
        }

        // 功能：某个条件达成后检查该任务是否全部达成，是就把状态推到可交付并广播。
        // 入参：conditionKey 条件编号；progress 达成时的进度（本方法不读它，判定以 ConditionTracker.IsMet 为准）
        // 返回：无
        // 步骤：
        //   1) 查条件归属，不是本类的就安静忽略 —— 成就的条件编号也会走到这里
        //   2) 状态不是已接取就直接返回：注销与通知之间可能有窗口，已交付或已放弃的不能再被改
        //   3) 配置行缺失、或不是所有条件都达成，都直接返回
        //   4) 全部达成才把状态改成可交付，并广播 Quest.Completed
        // 上游：ConditionTracker.ConditionMet
        // 下游：AreAllConditionsMet、EventCenter（Quest.Completed）
        // 注意：这里只广播可以交付了，不自动发奖 —— 发奖是玩家去交任务的动作，自动发会让任务这个玩法退化成计数器。
        private void OnConditionMet(int conditionKey, ConditionProgress progress)
        {
            int questId;

            if (!m_ownerOfCondition.TryGetValue(conditionKey, out questId))
            {
                return;
            }

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

            EventCenter.Instance.Trigger(QuestEvents.Completed, questId);
        }


        // 功能：判断一个任务的条件是否全部达成。
        // 入参：quest 任务配置行
        // 返回：全部达成返回 true；条件数组为 null 或空时返回 false（没有条件的任务不算完成）
        // 步骤：
        //   1) 空条件先返回 false，与 ResolveAndRegisterConditions 的拒绝口径保持一致
        //   2) 逐个问 ConditionTracker.IsMet，遇到一个没达成就提前返回
        // 上游：OnConditionMet
        // 下游：ConditionTracker.IsMet
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

        // 功能：注销一个任务的全部条件登记。
        // 入参：quest 任务配置行
        // 返回：无
        // 步骤：
        //   1) 条件数组为 null 就返回，没登记过也就没什么可注销
        //   2) 逐条 Unregister 并删掉归属记录；只动登记，不动进度行
        // 上游：Submit、Abandon、FollowState（回退与已交付两条分支）
        // 下游：ConditionTracker.Unregister
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

        // 功能：把一个任务配置行连同当前进度装成一个追踪视图。
        // 入参：quest 任务配置行
        // 返回：新造的 QuestTracking；条件行按配置顺序排列，缺进度时按 0 兜底
        // 步骤：
        //   1) 先把编号、名字、描述与当前状态填进视图
        //   2) 没有条件就直接返回，免得下面的循环空转
        //   3) 逐条查条件表并建 ConditionDef；配置有错时照样给一行，描述写成条件编号配置有错，而不是静默少一行
        //   4) 取不到进度就按 0 / 需求数兜底，保证界面有东西可显示
        //   5) 把描述、当前、需求、是否达成填进条件行
        // 上游：TryGetTracking、CopyActiveTrackings
        // 下游：ConditionTracker.TryGetProgress、ConditionDef.TryCreate / Describe
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

        // 功能：状态不对时给出「现在是什么状态」的一句人话，而不只是「不能这么做」。
        // 入参：questId 任务编号（拼文案用）；actual 当前状态；expected 需要的状态（None 表示接取，Completed 表示交付）
        // 返回：状态已经对上返回 null；否则返回一句给玩家看的原因
        // 步骤：
        //   1) actual 等于 expected 就放行，返回 null
        //   2) expected 为 None 时，按已接、可交付、已交付分别给话
        //   3) 剩下的只可能是要交付，按没接、没做完、已交付分别给话
        //   4) 未定义状态给一句带状态名的兜底，不至于返回空原因
        // 上游：Accept、Submit
        // 下游：无
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

        // 功能：拒绝一次操作：广播原因并返回带原因的失败结果。
        // 入参：questId 任务编号；reason 原因（一句给玩家看的中文）
        // 返回：Ok 为 false、Reason 为 reason 的结果
        // 步骤：
        //   1) 先广播 Quest.Rejected，再返回失败结果
        // 上游：Accept、Submit、Abandon 的业务拒绝分支
        // 下游：EventCenter、QuestActionResult.Fail
        // 注意：原因不能只留在返回值里 —— 调用方很可能是界面，它多半只判了 Ok 就完事；广播出去谁都看得见。
        private static QuestActionResult Reject(int questId, string reason)
        {
            EventCenter.Instance.Trigger(QuestEvents.Rejected, new QuestRejectedPayload(questId, reason));
            return QuestActionResult.Fail(reason);
        }

        // 功能：确认对象还没被 Dispose，拆掉的对象不许再被调用。
        // 入参：无
        // 返回：无；已 Dispose 时抛 ObjectDisposedException
        // 步骤：
        //   1) 只看 m_disposed 一个标志，命中就抛，异常里带类型名与一句人话
        // 上游：Accept、Submit、Abandon、FollowState 的入口
        // 下游：无
        private void EnsureNotDisposed()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException(nameof(QuestRuntime),
                    "[QuestRuntime] 已经 Dispose 了，不能再调用。");
            }
        }

        // 功能：退订条件系统的两个事件，并清空本地的归属表与已接列表。
        // 入参：无
        // 返回：无；重复调用必须无害
        // 步骤：
        //   1) 已经 Dispose 过就直接返回
        //   2) 先置标志再退订，避免退订过程中被重入
        //   3) 退订 ProgressChanged 与 ConditionMet，最后清空归属表与已接列表
        // 上游：BattleSession.Dispose、EditMode 用例
        // 下游：ConditionTracker 的事件
        // 注意：这里不逐条注销条件，注销只发生在状态迁移处（Submit、Abandon、FollowState），别把清理条件寄托在 Dispose 上。
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
