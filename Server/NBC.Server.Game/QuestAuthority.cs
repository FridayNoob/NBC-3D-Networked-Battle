// ============================================================================
//  QuestAuthority —— 服务端**任务权威**：接取 → 进度 → 交付（+ 发奖）
//  项目：3D联网战斗Demo
//  对应：`Docs\27` §二十五（§21.4 未做#3 的第一刀）
//
//  ---------------------------------------------------------------------------
//  一、它补的是哪一个洞
//  ---------------------------------------------------------------------------
//  成就没有"接取/交付"这两个动作，所以服务端权威先做完了（§二十一）。
//  任务有**状态机**，所以在此之前：
//      · "接过什么、到哪一步"只活在**客户端内存**里（`QuestRuntime` 自己的账本）
//      · 服务端不知道玩家接过什么 ⇒ 任务进度**没有权威值**可发
//      · ⇒ 界面只能显示"本地预测"（§二十四 如实画出来的那条边界）
//
//  这一刀把状态机搬到服务端：**接取/交付由服务端说了算**，进度由服务端算。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 与 `AchievementAuthority` 的关系：**照搬，不是共用**
//  ---------------------------------------------------------------------------
//  两者结构几乎一样（`PlayerState` + `ConditionTracker` + 进度 store + 台账 + 排队事实），
//  但**刻意分成两个类**，理由有两条，都是硬的：
//    ① `ConditionTracker.Register` **对同一个条件编号重复登记会当场抛异常** ——
//       而"一条条件只能一个持有者"是本项目的**配置规则 CFG0023**。
//       成分开之后，任务条件与成就条件各登记一次，边界清楚。
//       （当前两张表的条件编号是**不重叠**的：任务 4001~4007、成就 4009~4011。
//        将来若有人让它们重叠，**应当在配置检查里挡住**，而不是靠运行期撞异常。）
//    ② 发奖**没有再写一份**：两者都走 `IPlayerRewardLedger.MarkGrantedWithPayout`
//       + `IRewardLedgerDao.InsertBatchAndPayAsync`（§二十二 那一刀做的"同一事务"）。
//       唯一的差别是 `ERewardOwnerKind.Quest` vs `Achievement` —— 台账的
//       `owner_kind` 列本来就是为这件事准备的。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ `resetProgress` 在任务上**两个地方取值不同**（本文件最容易写错的一处）
//  ---------------------------------------------------------------------------
//      **玩家点"接取"**          → `resetProgress: true`  （接了之后打的怪才算）
//      **登录时恢复已接的任务**  → `resetProgress: false` （**必须**保留跨局累计）
//
//  写反的后果：每次重启服务端，**所有已接任务的进度清零** ——
//  玩家会看到"我明明打了 2 只，重启一下变 0 了"，而且**不报错**。
//  这正好复用了 M2-A 那条语义（`Register` 的第三个参数就是"任务 vs 成就"的唯一差别）。
//
//  ---------------------------------------------------------------------------
//  四、⚠️ 交付**必须幂等**（两道闸）
//  ---------------------------------------------------------------------------
//    ① **状态机**：已经不是 `Completed`（比如 `Submitted`）⇒ 直接拒绝，不去发奖
//    ② **台账**：`HasGranted(Quest, questId)` ⇒ 记过就不再发
//  两道都要有：①防"点两次交付"，②防"状态对但台账已经记过"（重启后重放等）。
//  只留一道的话，另一条路径就会**重复发钱**，而且它只在特定时序下出现。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NBC.Shared.Condition;
using NBC.Shared.Reward;

namespace NBC.Server.Game
{
    /// <summary>服务端任务权威：一个登录玩家一份状态，喂事实、判完成、按状态机交付并发奖。</summary>
    public sealed class QuestAuthority : IProgressFactSink, IDisposable
    {
        /// <summary>任务/条件/成就/奖励四张表。</summary>
        private readonly QuestTables m_tables;

        /// <summary>进度存放处的工厂（可能为 null = 服务端没接数据库）。</summary>
        private readonly Func<long, IPlayerProgressStore>? m_progressFactory;

        /// <summary>台账工厂（可能为 null = 服务端没接数据库）。</summary>
        private readonly Func<long, IPlayerRewardLedger>? m_ledgerFactory;

        /// <summary>任务状态存放处的工厂（可能为 null = 服务端没接数据库）。</summary>
        private readonly Func<long, IQuestStateStore>? m_stateFactory;

        /// <summary>在线玩家。</summary>
        private readonly Dictionary<long, PlayerState> m_players = new Dictionary<long, PlayerState>();

        /// <summary>条件 → 它属于哪几个任务（用于把"达成"路由回任务）。</summary>
        private readonly Dictionary<int, List<OwnerRow>> m_ownersOfCondition = new Dictionary<int, List<OwnerRow>>();

        /// <summary>累计接受的接取次数。</summary>
        private long m_accepted;

        /// <summary>累计判定出的"已完成"次数。</summary>
        private long m_completed;

        /// <summary>累计交付次数。</summary>
        private long m_submitted;

        /// <summary>累计挡掉的重复交付次数（"两道闸"要看得见）。</summary>
        private long m_duplicateSubmitAvoided;

        /// <summary>游客被跳过的事实条数。</summary>
        private long m_guestsSkipped;

        /// <summary>"没接数据库"只报一次。</summary>
        private int m_reportedNoDatabase;

        /// <summary>"没被追踪的玩家"只报一次。</summary>
        private int m_reportedUntracked;

        /// <summary>Dispose 过没有。</summary>
        private bool m_disposed;

        /// <summary>造一个任务权威。</summary>
        /// <param name="tables">四张表（不能为 null）。</param>
        /// <param name="progressFactory">进度存放处工厂；传 null = 服务端没接数据库。</param>
        /// <param name="ledgerFactory">台账工厂；传 null = 同上。</param>
        /// <param name="stateFactory">任务状态工厂；传 null = 同上。</param>
        public QuestAuthority(
            QuestTables tables,
            Func<long, IPlayerProgressStore>? progressFactory,
            Func<long, IPlayerRewardLedger>? ledgerFactory,
            Func<long, IQuestStateStore>? stateFactory)
        {
            if (tables == null)
            {
                throw new ArgumentNullException(nameof(tables), "[QuestAuthority] 表是 null。");
            }

            m_tables = tables;
            m_progressFactory = progressFactory;
            m_ledgerFactory = ledgerFactory;
            m_stateFactory = stateFactory;

            // 条件 → 任务 的反查表（建一次，之后每个玩家复用）
            for (int i = 0; i < m_tables.Quests.Count; i++)
            {
                OwnerRow owner = m_tables.Quests[i];

                for (int c = 0; c < owner.ConditionIds.Length; c++)
                {
                    int key = owner.ConditionIds[c];

                    // ⚠️ 声明成**可空**（`TryGetValue` 没找到时会写 null）：否则 CS8600，
                    //    而本项目要求 0 警告。同族：`QuestTables.FindQuest`。
                    List<OwnerRow>? list;

                    if (!m_ownersOfCondition.TryGetValue(key, out list))
                    {
                        list = new List<OwnerRow>(1);
                        m_ownersOfCondition.Add(key, list);
                    }

                    list.Add(owner);
                }
            }
        }

        /// <summary>值得记一句的事情（接取/完成/交付/拒绝原因）。</summary>
        public event Action<string>? Note;

        /// <summary>现在追踪着几个玩家。</summary>
        public int TrackedPlayers
        {
            get { return m_players.Count; }
        }

        /// <summary>一句人话（启动/关服统计用）。</summary>
        /// <returns>例：`任务权威：追踪 1 人、接取 2、完成 1、交付 1（挡重复 0）`。</returns>
        public string Describe()
        {
            return "任务权威：追踪 " + m_players.Count + " 人、接取 " + m_accepted +
                   "、完成 " + m_completed + "、交付 " + m_submitted +
                   "（挡重复 " + m_duplicateSubmitAvoided + " 次、游客跳过 " + m_guestsSkipped + "）";
        }

        // ====================================================================
        //  追踪（登录 / 断开）
        // ====================================================================

        /// <summary>
        /// 开始追踪一个玩家：**异步读**进度、台账、任务状态，然后登记"已接取"任务的条件。
        /// <para>⚠️ 三个 `LoadAsync` **必须在登记之前**完成（否则登记时读到 0，
        /// 冲库会把库里的真实进度覆盖成 0 —— 与成就那边同一个坑，见 §二十五）。</para>
        /// </summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="nickname">显示名（日志用）。</param>
        /// <param name="loading">读盘任务（调用方可以不 await）。</param>
        /// <returns>真的开始追踪返回 true。</returns>
        public bool Track(long playerId, string nickname, out Task? loading)
        {
            loading = null;

            if (m_disposed || playerId <= 0 || m_players.ContainsKey(playerId))
            {
                return false;
            }

            // 没接数据库 ⇒ **不追踪**（而不是"追踪但什么都不记"）：那会造出
            // "判定说完成了、重启就没"的假象。与成就权威同一条取舍。
            if (m_progressFactory == null || m_ledgerFactory == null || m_stateFactory == null)
            {
                if (Interlocked.Exchange(ref m_reportedNoDatabase, 1) == 0)
                {
                    Note?.Invoke(
                        "任务权威**不工作**：服务端没接数据库。\n" +
                        "    判定的输入（战斗事实）照样产生，但没有任何地方可以记 —— " +
                        "本类**故意不做「只判不记」**，因为那会让人以为任务权威已经好了。");
                }

                return false;
            }

            var state = new PlayerState { PlayerId = playerId };

            state.Progress = m_progressFactory(playerId);
            state.Ledger = m_ledgerFactory(playerId);
            state.States = m_stateFactory(playerId);

            state.Loading = LoadThenRegisterAsync(state, nickname);
            loading = state.Loading;

            m_players.Add(playerId, state);
            return true;
        }

        /// <summary>开始追踪（**不等 IO** 的简写版；登录路径用这个）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="nickname">显示名。</param>
        /// <returns>真的开始追踪返回 true。</returns>
        public bool Track(long playerId, string nickname)
        {
            Task? ignored;
            return Track(playerId, nickname, out ignored);
        }

        /// <summary>停止追踪（**断开时调**）：异步冲库，不在断开回调里等 IO。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <returns>本来在追踪、真的停掉了才返回 true。</returns>
        public bool Untrack(long playerId)
        {
            PlayerState? state;

            if (!m_players.TryGetValue(playerId, out state))
            {
                return false;
            }

            m_players.Remove(playerId);
            _ = FlushStateAsync(state);     // fire-and-forget（不在回调里等 IO）
            state.Dispose();
            return true;
        }

        /// <summary>把所有人的脏数据冲回库（**关服时调**）。</summary>
        /// <param name="timeoutMs">整体超时（毫秒）。</param>
        /// <returns>全部冲完返回 true；超时返回 false。</returns>
        public async Task<bool> FlushAllAsync(int timeoutMs = 3000)
        {
            var tasks = new List<Task>(m_players.Count * 3);

            foreach (KeyValuePair<long, PlayerState> pair in m_players)
            {
                PlayerState state = pair.Value;

                if (state.Progress != null) { tasks.Add(state.Progress.FlushAsync()); }
                if (state.Ledger != null) { tasks.Add(state.Ledger.FlushAsync()); }
                if (state.States != null) { tasks.Add(state.States.FlushAsync()); }
            }

            if (tasks.Count == 0)
            {
                return true;
            }

            Task all = Task.WhenAll(tasks);
            Task done = await Task.WhenAny(all, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return done == all;
        }

        // ====================================================================
        //  状态机：接取 / 交付
        // ====================================================================

        /// <summary>
        /// **接取**一个任务（服务端权威）。
        /// <para>校验：任务在表里、至少一条条件、当前状态是"没接过"。</para>
        /// </summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="questId">任务编号。</param>
        /// <param name="reason">失败原因（成功时是空串）。</param>
        /// <returns>接取成功返回 true。</returns>
        public bool Accept(long playerId, int questId, out string reason)
        {
            reason = string.Empty;

            PlayerState? state;

            if (!m_players.TryGetValue(playerId, out state))
            {
                reason = "玩家 " + playerId + " 没被追踪（先登录）";
                return false;
            }

            OwnerRow? quest = m_tables.FindQuest(questId);

            if (quest == null)
            {
                reason = "任务 " + questId + " 不在 `Quest` 表里";
                return false;
            }

            if (quest.ConditionIds.Length == 0)
            {
                // 与客户端 `QuestRuntime.Accept` 同一条判据：接了也永远做不完
                reason = "任务 " + questId + " **一条条件都没有** ⇒ 接了也永远做不完，服务端拒绝";
                return false;
            }

            EQuestStage current = state.States == null ? EQuestStage.None : state.States.GetState(questId);

            if (current != EQuestStage.None)
            {
                reason = "任务 " + questId + " 已经是「" + StageText(current) + "」了";
                return false;
            }

            // ⚠️ 见文件头三：**玩家点接取 ⇒ 清零**（接了之后打的怪才算）
            if (!RegisterConditions(state, quest, true, out reason))
            {
                return false;
            }

            state.States?.SetState(questId, EQuestStage.Accepted);
            state.Accepted++;
            m_accepted++;

            Note?.Invoke("任务接取（服务端权威）：玩家 " + playerId + " → " + questId + "「" + quest.Name + "」");
            return true;
        }

        /// <summary>
        /// **交付**一个任务并**在同一事务**里发奖。
        /// <para>⚠️ 幂等（文件头四的两道闸）：不是 `Completed` 就拒绝；台账记过就不再发。</para>
        /// </summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="questId">任务编号。</param>
        /// <param name="reason">失败原因（成功时是空串）。</param>
        /// <returns>交付成功（含"已经交过"）返回 true。</returns>
        public bool Submit(long playerId, int questId, out string reason)
        {
            reason = string.Empty;

            PlayerState? state;

            if (!m_players.TryGetValue(playerId, out state))
            {
                reason = "玩家 " + playerId + " 没被追踪（先登录）";
                return false;
            }

            EQuestStage current = state.States == null ? EQuestStage.None : state.States.GetState(questId);

            // ---- 闸①：状态机 ----
            if (current == EQuestStage.Submitted)
            {
                // ⚠️ **已经交过 ⇒ 当成成功**（幂等），但**绝不**再发一次奖
                m_duplicateSubmitAvoided++;
                reason = "任务 " + questId + " **已经交付过**了（不重复发奖）";
                Note?.Invoke("任务重复交付被挡住：玩家 " + playerId + " → " + questId);
                return true;
            }

            if (current != EQuestStage.Completed)
            {
                reason = "任务 " + questId + " 现在不能交付（当前状态「" + StageText(current) + "」，要「已完成」）";
                return false;
            }

            OwnerRow? quest = m_tables.FindQuest(questId);

            if (quest == null)
            {
                reason = "任务 " + questId + " 不在 `Quest` 表里";
                return false;
            }

            RewardRow? reward = quest.RewardId == 0 ? null : m_tables.FindReward(quest.RewardId);
            int expDelta = reward == null ? 0 : reward.Exp;
            int goldDelta = reward == null ? 0 : reward.Gold;

            // ---- 闸②：台账 ----
            if (state.Ledger != null && state.Ledger.HasGranted(ERewardOwnerKind.Quest, questId))
            {
                m_duplicateSubmitAvoided++;
                Note?.Invoke("任务 " + questId + " 的奖励**台账里已经记过** ⇒ 只改状态、不重复发");
            }
            else
            {
                // ⚠️ 与成就**共用同一条发放路径**（§二十二）：台账 + exp/gold
                //    在**同一个事务**里落库（`owner_kind = Quest`）。
                state.Ledger?.MarkGrantedWithPayout(
                    ERewardOwnerKind.Quest, questId, quest.RewardId, expDelta, goldDelta);
            }

            state.States?.SetState(questId, EQuestStage.Submitted);

            // ⚠️ **必须注销这个任务的条件**（只注销登记、**不动进度** —— `Unregister` 的语义，
            //    而 `IQuestStateStore.cs` 文件头 §二 明确要求"交付后进度仍然保留"）。
            //
            //    不注销的后果**不是**"多涨一点进度"，而是**两条路径行为不一致**：
            //      · **刚交付**（同一进程内）：条件还登记着 ⇒ 之后每次击杀仍然喂给它；
            //      · **重启读盘后**：`LoadThenRegisterAsync` 跳过 `Submitted` ⇒ 不登记。
            //    同一个玩家状态，行为取决于"服务端有没有重启过" —— 正是本项目最在意的
            //    那类"同一件事两种算法"。修完之后下面那句注释才是真的。
            UnregisterConditions(state, quest);

            state.Submitted++;
            m_submitted++;

            Note?.Invoke("任务交付（服务端权威）：玩家 " + playerId + " → " + questId + "「" + quest.Name + "」" +
                         (reward == null
                             ? "（无奖励）"
                             : "→ 奖励 " + reward + "（exp +" + expDelta + " / gold +" + goldDelta +
                               "，与台账**同一事务**落库）"));

            return true;
        }

        // ====================================================================
        //  喂事实
        // ====================================================================

        /// <summary>告诉权威"发生了一件事"（击杀/拾取……）。</summary>
        /// <param name="fact">事实。</param>
        public void ApplyFact(in ProgressFact fact)
        {
            if (m_disposed || fact.Count <= 0)
            {
                return;
            }

            if (fact.PlayerId <= 0)
            {
                m_guestsSkipped++;
                return;
            }

            PlayerState? state;

            if (!m_players.TryGetValue(fact.PlayerId, out state) || state.Tracker == null)
            {
                // 与"游客跳过"完全不同：有档案却没被追踪 = 漏接线
                if (Interlocked.Exchange(ref m_reportedUntracked, 1) == 0)
                {
                    Note?.Invoke("⚠️ 有玩家（" + fact.PlayerId + "）的任务事实**没有归属**：他没被追踪。");
                }

                return;
            }

            if (!state.Ready)
            {
                // 还没加载完 ⇒ **排队**（不许丢、也不许在这里等）
                state.Pending.Add(fact);
                return;
            }

            Feed(state, fact);
        }

        /// <summary>把一条事实喂给判定器。</summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="fact">事实。</param>
        private static void Feed(PlayerState state, in ProgressFact fact)
        {
            state?.Tracker?.Notify(fact.EventType, fact.TargetId, fact.Count);
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>
        /// **一条条件达成了** ⇒ 看它属于哪个**已接取**的任务；那个任务的**全部**条件都达成时，
        /// 把它推到 <see cref="EQuestStage.Completed"/>（= 可以交付了）。
        ///
        /// <para>
        /// ⚠️ 这里**只改状态、绝不发奖**：发奖是玩家**交付**时的事（<see cref="Submit"/>），
        /// 两道幂等闸（状态机 + 台账）都在那边。把发放也塞进来会造出"任务一完成就自动到账"，
        /// 那是另一个玩法决策，不是这一刀的事。
        /// </para>
        /// <para>
        /// ⚠️ **幂等**：只处理"当前是 Accepted"的任务 —— 已经 `Completed` / `Submitted` 的不再推，
        /// 否则日志会刷屏，而且 `m_completed` 这个计数会虚高（"完成 5 次"其实只有一个任务）。
        /// </para>
        /// </summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="key">刚达成的条件编号。</param>
        private void OnConditionMet(PlayerState state, int key)
        {
            List<OwnerRow>? quests;

            if (state.Tracker == null || !m_ownersOfCondition.TryGetValue(key, out quests))
            {
                return;
            }

            for (int i = 0; i < quests.Count; i++)
            {
                OwnerRow quest = quests[i];

                EQuestStage stage = state.States == null ? EQuestStage.None : state.States.GetState(quest.Id);

                if (stage != EQuestStage.Accepted || !AllConditionsMet(state, quest))
                {
                    continue;
                }

                state.States?.SetState(quest.Id, EQuestStage.Completed);
                m_completed++;

                Note?.Invoke("任务完成（服务端权威）：玩家 " + state.PlayerId + " → " + quest.Id +
                             "「" + quest.Name + "」条件全满 ⇒ **可以交付了**" +
                             "（此刻还不发奖 —— 交付才发，见 `Submit` 的两道幂等闸）");
            }
        }

        /// <summary>这个任务的**所有**条件都达成了吗（"全满才算完成"）。</summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="quest">任务行。</param>
        /// <returns>全部达成为 true。</returns>
        private static bool AllConditionsMet(PlayerState state, OwnerRow quest)
        {
            if (state.Tracker == null)
            {
                return false;
            }

            for (int i = 0; i < quest.ConditionIds.Length; i++)
            {
                if (!state.Tracker.IsMet(quest.ConditionIds[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>读盘（进度 + 台账 + 任务状态）→ 登记"已接取"任务的条件 → 处理排队事实。</summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="nickname">显示名。</param>
        /// <returns>任务。</returns>
        private async Task LoadThenRegisterAsync(PlayerState state, string nickname)
        {
            try
            {
                int progressRows = state.Progress == null ? 0 : await state.Progress.LoadAsync().ConfigureAwait(false);
                int ledgerRows = state.Ledger == null ? 0 : await state.Ledger.LoadAsync().ConfigureAwait(false);
                int stateRows = state.States == null ? 0 : await state.States.LoadAsync().ConfigureAwait(false);

                // ⚠️ 判定器是**共享层那一份**
                state.Tracker = new ConditionTracker(state.Progress);

                // ⚠️ **必须接上"达成"回调**，否则"条件全满 ⇒ Completed"这一步永远不会发生，
                //    于是 `Submit` 永远拒绝（状态一直停在 Accepted）—— 而且是**静默**的：
                //    进度照涨、日志照打，只有交付死活不行。
                state.Tracker.ConditionMet += (key, progress) => OnConditionMet(state, key);

                // ⚠️ 见文件头三：**恢复已接的任务 ⇒ 保留进度**（resetProgress: false）
                for (int i = 0; i < m_tables.Quests.Count; i++)
                {
                    OwnerRow quest = m_tables.Quests[i];

                    EQuestStage stage = state.States == null ? EQuestStage.None : state.States.GetState(quest.Id);

                    if (stage == EQuestStage.None || stage == EQuestStage.Submitted)
                    {
                        // 没接过 / 已交付完的，不登记。
                        // ⚠️ 「已交付完的不登记」**必须**与 `Submit` 里那句 `UnregisterConditions`
                        //    成对存在 —— 两条路径（刚交付 vs 重启读盘）行为必须一致，
                        //    否则同一个玩家状态会因为"服务端有没有重启过"而不同。
                        continue;
                    }

                    string ignored;

                    if (!RegisterConditions(state, quest, false, out ignored))
                    {
                        Note?.Invoke("⚠️ 恢复任务 " + quest.Id + " 失败：" + ignored);
                    }
                }

                state.Ready = true;

                Note?.Invoke("玩家 " + state.PlayerId + "（" + nickname + "）的任务已就绪：读回 " +
                             progressRows + " 条进度、" + ledgerRows + " 条台账、" + stateRows + " 条任务状态。");

                // 排队的事实现在可以喂了
                for (int i = 0; i < state.Pending.Count; i++)
                {
                    Feed(state, state.Pending[i]);
                }

                state.Pending.Clear();
            }
            catch (Exception ex)
            {
                Note?.Invoke("⚠️ 玩家 " + state.PlayerId + " 的任务读盘失败：" + ex.Message);
                state.Ready = true;     // 失败也要放行，否则事实会一直排队
            }
        }

        /// <summary>
        /// 登记一个任务的全部条件。
        /// <para>⚠️ `resetProgress` 的含义见文件头三：**点接取 = true、登录恢复 = false**。</para>
        /// </summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="quest">任务行。</param>
        /// <param name="resetProgress">要不要清零。</param>
        /// <param name="reason">失败原因。</param>
        /// <returns>成功返回 true。</returns>
        private bool RegisterConditions(PlayerState state, OwnerRow quest, bool resetProgress, out string reason)
        {
            reason = string.Empty;

            if (state.Tracker == null)
            {
                reason = "判定器还没就绪";
                return false;
            }

            int[] conditionIds = quest.ConditionIds;

            for (int c = 0; c < conditionIds.Length; c++)
            {
                if (state.Tracker.IsRegistered(conditionIds[c]))
                {
                    continue;       // 已经登记过（例如两个任务共用一条条件）
                }

                ConditionRow? row = m_tables.FindCondition(conditionIds[c]);

                if (row == null)
                {
                    reason = "任务 " + quest.Id + " 的条件 " + conditionIds[c] + " 不在 `QuestCondition` 表里";
                    return false;
                }

                state.Tracker.Register(conditionIds[c], row.Def, resetProgress);
            }

            return true;
        }

        /// <summary>
        /// 注销一个任务的条件（**只注销登记，不动进度**）。
        /// <para>⚠️ 逐条判断"还有没有别的**活着的**任务要用它"：两个都接了的任务共用一条条件时，
        /// 交掉其中一个**不能**把这条条件注销掉 —— 否则另一个任务的进度会**从此不再涨**，
        /// 而且不报错。（同一类内部共用当前被配置检查 CFG0023 挡着，但这里不靠那条规则活着。）</para>
        /// </summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="quest">刚交付的任务。</param>
        private void UnregisterConditions(PlayerState state, OwnerRow quest)
        {
            if (state.Tracker == null)
            {
                return;
            }

            int[] conditionIds = quest.ConditionIds;

            for (int i = 0; i < conditionIds.Length; i++)
            {
                int key = conditionIds[i];

                if (IsConditionStillNeededBy(state, key))
                {
                    continue;       // 还有别的活着的任务用它 ⇒ 不许注销
                }

                // 返回值 false = "本来就没登记"，那不是错误（重复交付已在两道闸里挡过）
                state.Tracker.Unregister(key);
            }
        }

        /// <summary>这条条件还有没有别的**活着**（已接/已完成）的任务在用它。</summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="conditionKey">条件编号。</param>
        /// <returns>还有人要用返回 true。</returns>
        private bool IsConditionStillNeededBy(PlayerState state, int conditionKey)
        {
            List<OwnerRow>? owners;

            if (!m_ownersOfCondition.TryGetValue(conditionKey, out owners) || owners == null)
            {
                return false;
            }

            for (int i = 0; i < owners.Count; i++)
            {
                EQuestStage stage = state.States == null
                    ? EQuestStage.None
                    : state.States.GetState(owners[i].Id);

                if (stage == EQuestStage.Accepted || stage == EQuestStage.Completed)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 当前**登记着的条件总数**（跨所有在线玩家）。
        /// <para>⚠️ 它是"交付后注销"这条不变式的**唯一可观测形式**，所以专门暴露出来：
        /// 交付会让它下降；而"重启读盘后"必须**等于**未重启时的值 ——
        /// 只测一边**测不出**"两条路径不一致"那个 bug。</para>
        /// <para>直接由 `ConditionTracker.RegisteredCount` 累加而来（**不另存一份计数** ——
        /// 另存一份就又多了一处会不同步的地方）。</para>
        /// </summary>
        public int TrackedConditionCount
        {
            get
            {
                int total = 0;

                foreach (KeyValuePair<long, PlayerState> pair in m_players)
                {
                    ConditionTracker? tracker = pair.Value.Tracker;

                    if (tracker != null)
                    {
                        total += tracker.RegisteredCount;
                    }
                }

                return total;
            }
        }

        /// <summary>状态的中文说法（日志用）。</summary>
        /// <param name="stage">状态。</param>
        /// <returns>中文。</returns>
        private static string StageText(EQuestStage stage)
        {
            switch (stage)
            {
                case EQuestStage.Accepted: return "进行中";
                case EQuestStage.Completed: return "已完成（可交付）";
                case EQuestStage.Submitted: return "已交付";
                default: return "未接取";
            }
        }

        /// <summary>只冲任务状态（断开时用）。</summary>
        /// <param name="state">玩家状态。</param>
        /// <returns>任务。</returns>
        private static async Task FlushStateAsync(PlayerState state)
        {
            try
            {
                if (state.States != null) { await state.States.FlushAsync().ConfigureAwait(false); }
            }
            catch
            {
                // 断开路径不许因为冲库失败而把调用方拖下来（关服时 `FlushAllAsync` 兜底）
            }
        }

        /// <summary>释放（不自动落库）。</summary>
        public void Dispose()
        {
            m_disposed = true;
        }

        /// <summary>一个在线玩家的状态。</summary>
        private sealed class PlayerState : IDisposable
        {
            /// <summary>玩家编号。</summary>
            public long PlayerId;

            /// <summary>进度存放处（可能为 null = 服务端没接数据库）。</summary>
            public IPlayerProgressStore? Progress;

            /// <summary>发奖台账（可能为 null = 同上）。</summary>
            public IPlayerRewardLedger? Ledger;

            /// <summary>任务状态存放处（可能为 null = 同上）。</summary>
            public IQuestStateStore? States;

            /// <summary>判定器（**共享层的那个**）。</summary>
            public ConditionTracker? Tracker;

            /// <summary>读盘完成了没有（没完成之前事实排队）。</summary>
            public bool Ready;

            /// <summary>读盘任务（null = 已经好了）。</summary>
            public Task? Loading;

            /// <summary>加载完成前收到的事实（**排队，不许丢**）。</summary>
            public readonly List<ProgressFact> Pending = new List<ProgressFact>();

            /// <summary>累计接取 / 交付（统计用）。</summary>
            public long Accepted;

            /// <summary>累计交付。</summary>
            public long Submitted;

            /// <summary>释放三个存放处。</summary>
            public void Dispose()
            {
                Progress?.Dispose();
                Ledger?.Dispose();
                States?.Dispose();
            }
        }
    }
}
