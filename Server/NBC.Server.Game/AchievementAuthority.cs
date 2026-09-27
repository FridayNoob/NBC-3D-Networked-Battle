// ============================================================================
//  AchievementAuthority —— **服务端权威**的成就判定（M4-S3 服务端权威化）
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十一
//
//  ---------------------------------------------------------------------------
//  一、它解决的是哪个具体问题
//  ---------------------------------------------------------------------------
//  在这之前，**成就与任务的判定全在客户端**：
//      服务端发事件 → 客户端 `ServerEventBridge` → `EventCenter` → 客户端 `ConditionTracker`
//      → 客户端自己判"解锁了" → 客户端自己"发奖"（演示里就是打条日志）
//
//  后果有三个，都是**真问题**而不是洁癖：
//      ① **改客户端就能改进度**（哪怕本项目的客户端是"好人"，架构上它说了算）
//      ② **进度只在客户端的内存里**：关掉 Unity 就没了 —— "累计击杀 10 只野狼"这种
//         "跨局累计"的设计**根本没有兑现**
//      ③ 服务端**不知道**玩家完成了什么，于是"发奖要防重复"这类事无从谈起
//
//  ⇒ 本类把判定搬到服务端，并且**接上了库里那两张表**：
//      `condition_progress`（进度，绝对值 ⇒ 落库幂等）
//      `reward_granted`    （台账，单调集合 ⇒ 回答"这份奖发过没有"）
//  ⚠️ 这两张表 + 两个 DAO 早就写好了（M4-S3 的数据层），**但一直只有探针在用** ——
//     本类是把它们**第一次接进生产路径**的那根线。
//
//  ---------------------------------------------------------------------------
//  二、⭐ 判定逻辑**一行都没改**（这是"双端共享层"的兑现）
//  ---------------------------------------------------------------------------
//  用的是**共享层**的 `ConditionTracker` / `ConditionDef` / `EConditionEvent` ——
//  和客户端编的是**同一份源码**。所以这里不是"照着客户端再写一遍判定"，
//  而是"**同一套判定换了个执行者**"。
//  ⇒ 这也是本项目"混合同步"设计里的关键假设：**能共享的逻辑只留一份**（同族：`D1`）。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ 三个必须守住的次序（错一个就会静默丢数据）
//  ---------------------------------------------------------------------------
//      ① `LoadAsync()` **必须在 `Register()` 之前**
//         —— 否则登记时读到的是 0，等 `FlushAsync` 就把库里的真实进度**覆盖成 0**
//            （这是最恶劣的一类：数据不是没写进去，是被**改小**了）
//      ② `LoadAsync()` 台账 **必须在第一次 `HasGranted` 之前**
//         —— 否则"登录即解锁"会在每次启动时**再发一遍奖**（M4-B 讲过的那个坑）
//      ③ **登录是同步的（握手不许等 IO），而 Load 是异步的**
//         —— 中间那一小段时间收到的事实要**排队**，不许丢也不许在主循环里等
//
//  ---------------------------------------------------------------------------
//  四、参与者的边界（同 `Docs\27` §二十的约定）
//  ---------------------------------------------------------------------------
//      `playerId > 0`  ⇒ 真实账号档案：**追踪、落库、发奖**
//      `playerId < 0`  ⇒ 游客：**跳过**（没有档案 ⇒ 没有地方记）—— 计数 + 只说一次
//      `playerId == 0` ⇒ 无主：跳过
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NBC.Protocol;
using NBC.Shared.Condition;
using NBC.Shared.Reward;

namespace NBC.Server.Game
{
    /// <summary>服务端权威的成就判定（见文件头）。</summary>
    public sealed class AchievementAuthority : IDisposable
    {
        /// <summary>一个已追踪玩家的全部状态。</summary>
        private sealed class PlayerState : IDisposable
        {
            /// <summary>玩家编号。</summary>
            public long PlayerId;

            /// <summary>进度存放处（可能为 null = 服务端没接数据库）。</summary>
            public IPlayerProgressStore? Store;

            /// <summary>发奖台账（可能为 null = 服务端没接数据库）。</summary>
            public IPlayerRewardLedger? Ledger;

            /// <summary>判定器（**共享层的那个**）。</summary>
            public ConditionTracker? Tracker;

            /// <summary>台账读完了没有（没读完之前不许判"发过没有"，见文件头 ③）。</summary>
            public bool Ready;

            /// <summary>等待处理的加载任务（null = 已经好了）。</summary>
            public Task? Loading;

            /// <summary>加载完成前收到的事实（**排队，不许丢**）。</summary>
            public readonly List<ProgressFact> Pending = new List<ProgressFact>();

            /// <summary>这个玩家解锁了几个成就。</summary>
            public int Unlocked;

            /// <summary>收尾。</summary>
            public void Dispose()
            {
                // ⚠️ 判定器与两个存放处是**一对一**的（一个玩家一套），所以直接丢引用即可；
                //    没有"把事件退订回某个共享对象"的问题（共享对象 = 那三个，谁也不共享）。
                Tracker = null;

                if (Store != null)
                {
                    Store.Dispose();
                    Store = null;
                }

                if (Ledger != null)
                {
                    Ledger.Dispose();
                    Ledger = null;
                }
            }
        }

        /// <summary>排队上限（**有界**：防"一直没加载好"把内存撑爆）。</summary>
        private const int MaxPendingFacts = 1024;

        /// <summary>任务 / 成就 / 条件 / 奖励四张表。</summary>
        private readonly QuestTables m_tables;

        /// <summary>进度存放处的工厂（null = 没接数据库）。</summary>
        private readonly Func<long, IPlayerProgressStore>? m_storeFactory;

        /// <summary>台账的工厂（null = 没接数据库）。</summary>
        private readonly Func<long, IPlayerRewardLedger>? m_ledgerFactory;

        /// <summary>条件编号 → 拥有它的那些"主"（任务/成就）。加载时建一次，之后只读。</summary>
        private readonly Dictionary<int, List<OwnerRow>> m_ownersOfCondition = new Dictionary<int, List<OwnerRow>>();

        /// <summary>成就编号 → 成就行（解锁时取名字与奖励）。</summary>
        private readonly Dictionary<int, OwnerRow> m_achievementById = new Dictionary<int, OwnerRow>();

        /// <summary>已追踪的玩家。</summary>
        private readonly Dictionary<long, PlayerState> m_players = new Dictionary<long, PlayerState>();

        /// <summary>累计处理了几条事实。</summary>
        private long m_factsApplied;

        /// <summary>累计跳过几条游客事实（设计如此）。</summary>
        private long m_guestsSkipped;

        /// <summary>累计跳过几条"没追踪的玩家"的事实（⚠️ 这才是要看的异常）。</summary>
        private long m_untrackedSkipped;

        /// <summary>累计解锁几个成就。</summary>
        private long m_unlocked;

        /// <summary>累计"台账说发过了所以不再发"几次（这是**正确行为**，不是错误）。</summary>
        private long m_duplicateGrantAvoided;

        /// <summary>"游客被跳过"这件事只报一次（避免刷屏）。</summary>
        private int m_reportedGuests;

        /// <summary>"没接数据库"这件事只报一次。</summary>
        private int m_reportedNoDatabase;

        /// <summary>"没追踪的玩家"这件事只报一次。</summary>
        private int m_reportedUntracked;

        /// <summary>Dispose 过没有。</summary>
        private bool m_disposed;

        /// <summary>造一个权威判定器。</summary>
        /// <param name="tables">四张表（不能为 null）。</param>
        /// <param name="storeFactory">
        /// 进度存放处工厂；**传 null = 服务端没接数据库** ⇒ 只判定、不落库
        /// （判定照样对，只是"跨局累计"仍然不成立 —— 这一点会写进 `Describe()`）。
        /// </param>
        /// <param name="ledgerFactory">台账工厂；传 null = 同上。</param>
        public AchievementAuthority(
            QuestTables tables,
            Func<long, IPlayerProgressStore>? storeFactory,
            Func<long, IPlayerRewardLedger>? ledgerFactory)
        {
            if (tables == null)
            {
                throw new ArgumentNullException(nameof(tables));
            }

            m_tables = tables;
            m_storeFactory = storeFactory;
            m_ledgerFactory = ledgerFactory;

            BuildOwnerIndex();
        }

        /// <summary>值得记一句的事情（Host 接到日志上）。</summary>
        public event Action<string>? Note;

        /// <summary>
        /// 某个玩家的**权威进度变了**（参数 = 玩家编号）。
        /// <para>⚠️ 宿主接住它之后要立刻把 `TryBuildProgressView` 的结果**发给那个玩家** ——
        /// 这就是"客户端不再自己算"的那条线（`Docs\27` §21.4 未做#2：收掉"两个账房"）。</para>
        /// <para>⚠️ 刻意**只传玩家编号**、不传进度内容：发什么由 `TryBuildProgressView` 决定，
        /// 于是"什么时候发"与"发什么"分开 —— 将来要改成合并/限流也只动一处。</para>
        /// </summary>
        public event Action<long>? ProgressChanged;

        /// <summary>追踪了几个玩家。</summary>
        public int TrackedPlayers
        {
            get { return m_players.Count; }
        }

        /// <summary>一句人话（统计行用）。</summary>
        /// <returns>例：`成就权威：追踪 2 人、事实 37 条、解锁 1 个（防重复发奖 0 次、游客跳过 12 条）`。</returns>
        public string Describe()
        {
            string text = "成就权威：追踪 " + TrackedPlayers + " 人、事实 " + m_factsApplied + " 条、解锁 " +
                          m_unlocked + " 个（防重复 " + m_duplicateGrantAvoided + " 次、游客跳过 " +
                          m_guestsSkipped + " 条";

            if (m_untrackedSkipped > 0)
            {
                text += "、⚠️没追踪的玩家 " + m_untrackedSkipped + " 条";
            }

            text += "）";

            if (m_storeFactory == null)
            {
                text += "；⚠️ **没接数据库**：成就只在内存里（跨局累计不成立）";
            }

            return text;
        }

        /// <summary>
        /// 开始追踪一个玩家（**登录成功后调**；游客直接返回 false）。
        ///
        /// <para>
        /// ⚠️ 它**不会**阻塞等数据库：读进度是异步的，期间收到的事实会排队（见文件头 ③）。
        /// </para>
        /// </summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="nickname">显示名（只用于日志）。</param>
        /// <param name="loading">
        /// **"进度读完了没"的那张凭据**（没接数据库 / 游客 / 已在追踪时是 null）。
        /// <para>
        /// ⚠️ 为什么要把它交出来：追踪是**异步**的（不在握手/主循环里等 IO），
        /// 而调用方有时**需要**等它（探针要断言"读完了之后的进度"、将来"进副本前刷一次"也要等）。
        /// 不给凭据的话，调用方就只能 `Sleep` 猜 —— 那是本项目的 E1「读不到 ≠ 是 0」那一族。
        /// </para>
        /// </param>
        /// <returns>真的开始追踪返回 true；游客 / 0 / 已在追踪返回 false。</returns>
        public bool Track(long playerId, string nickname, out Task? loading)
        {
            loading = null;

            if (m_disposed || playerId <= 0 || m_players.ContainsKey(playerId))
            {
                return false;
            }

            // 没接数据库 ⇒ **不追踪**（而不是"追踪但什么都不记"）：
            // 那样会造出"判定说解锁了、但重启就没"的假象，而且会把事实记成"没人认领"。
            if (m_storeFactory == null || m_ledgerFactory == null)
            {
                if (System.Threading.Interlocked.Exchange(ref m_reportedNoDatabase, 1) == 0)
                {
                    Note?.Invoke(
                        "成就权威**不工作**：服务端没接数据库。\n" +
                        "    判定的输入（战斗事实）照样产生，但没有任何地方可以记 —— " +
                        "本类**故意不做「只判不记」**，因为那会让人以为成就系统已经好了。");
                }

                return false;
            }

            var state = new PlayerState { PlayerId = playerId };

            state.Store = m_storeFactory(playerId);
            state.Ledger = m_ledgerFactory(playerId);

            // ⚠️ 两个 LoadAsync **必须在 Register 之前完成**（文件头 ①②）
            state.Loading = LoadThenRegisterAsync(state, nickname);
            loading = state.Loading;

            m_players.Add(playerId, state);
            return true;
        }

        /// <summary>
        /// 开始追踪一个玩家（**不等 IO** 的简写版；握手/登录路径用这个）。
        /// </summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="nickname">显示名。</param>
        /// <returns>真的开始追踪返回 true。</returns>
        public bool Track(long playerId, string nickname)
        {
            Task? ignored;
            return Track(playerId, nickname, out ignored);
        }

        /// <summary>
        /// 停止追踪一个玩家（**断开时调**）：先把脏数据冲回库，再释放。
        /// <para>⚠️ 冲库是异步的（fire-and-forget），**不在断开回调里等** ——
        /// 那会卡住消息泵；关服时由 `FlushAllAsync` 兜底。</para>
        /// </summary>
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
            _ = FlushAndDisposeAsync(state);
            return true;
        }

        /// <summary>
        /// 喂一条事实（**从战斗 Tick 里调，绝不阻塞、绝不抛**）。
        /// </summary>
        /// <param name="fact">事实。</param>
        public void ApplyFact(in ProgressFact fact)
        {
            if (m_disposed || fact.Count <= 0)
            {
                return;
            }

            // 游客 / 无主：跳过（**这是设计**，见文件头四）
            if (fact.PlayerId <= 0)
            {
                m_guestsSkipped++;

                if (System.Threading.Interlocked.Exchange(ref m_reportedGuests, 1) == 0)
                {
                    Note?.Invoke(
                        "游客的条件事实按设计**不记账**（游客没有玩家档案）。\n" +
                        "    想让成就/任务进度留下来，就用账号登录（见 `Docs\\27` §十九）。");
                }

                return;
            }

            PlayerState? state;

            if (!m_players.TryGetValue(fact.PlayerId, out state) || state.Tracker == null)
            {
                // ⚠️ **这一条与"游客跳过"完全不同**：玩家有档案却没被追踪 = 真出问题了
                m_untrackedSkipped++;

                if (System.Threading.Interlocked.Exchange(ref m_reportedUntracked, 1) == 0)
                {
                    Note?.Invoke(
                        "⚠️ 有玩家（" + fact.PlayerId + "）的条件事实**没有归属**：他没被追踪。\n" +
                        "    多半是「登录时忘了调 Track」或「先打了怪、后登录」。" +
                        "这条与'游客跳过'不是一回事 —— 游客是设计，这条是漏接线。");
                }

                return;
            }

            // 还没加载完（异步 IO 在路上）⇒ **排队**，不许丢也不许在这里等
            if (!state.Ready)
            {
                if (state.Pending.Count < MaxPendingFacts)
                {
                    state.Pending.Add(fact);
                }
                else
                {
                    Note?.Invoke("⚠️ 玩家 " + fact.PlayerId + " 的待处理事实超过 " + MaxPendingFacts +
                                 " 条（进度加载太慢？），超出部分被丢弃。");
                }

                return;
            }

            Notify(state, fact);
        }

        /// <summary>
        /// 把所有玩家的脏数据冲回库（**一局结束 / 关服时调**）。
        /// </summary>
        /// <param name="timeoutMs">最多等多少毫秒。</param>
        /// <returns>冲完返回 true；超时返回 false。</returns>
        public async Task<bool> FlushAllAsync(int timeoutMs = 3000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            bool allOk = true;

            var states = new List<PlayerState>(m_players.Values);

            for (int i = 0; i < states.Count; i++)
            {
                PlayerState state = states[i];

                if (state.Loading != null && !state.Loading.IsCompleted)
                {
                    continue;       // 还在加载，没什么可冲的
                }

                try
                {
                    if (state.Store != null && state.Store.DirtyCount > 0)
                    {
                        await state.Store.FlushAsync().ConfigureAwait(false);
                    }

                    if (state.Ledger != null && state.Ledger.DirtyCount > 0)
                    {
                        await state.Ledger.FlushAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    allOk = false;
                    Note?.Invoke("❌ 成就进度落库失败（服务端继续跑）：" + ex.Message);
                }

                if (DateTime.UtcNow > deadline)
                {
                    return false;
                }
            }

            return allOk;
        }

        /// <summary>收尾（不自动冲库 —— 由调用方显式 `FlushAllAsync`）。</summary>
        public void Dispose()
        {
            m_disposed = true;

            foreach (KeyValuePair<long, PlayerState> pair in m_players)
            {
                pair.Value.Dispose();
            }

            m_players.Clear();
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>建"条件 → 谁拥有它"的索引（加载时一次）。</summary>
        private void BuildOwnerIndex()
        {
            for (int i = 0; i < m_tables.Achievements.Count; i++)
            {
                OwnerRow owner = m_tables.Achievements[i];
                m_achievementById[owner.Id] = owner;
                IndexOwner(owner);
            }
        }

        /// <summary>把一个 owner 的条件登记进索引。</summary>
        /// <param name="owner">任务或成就。</param>
        private void IndexOwner(OwnerRow owner)
        {
            for (int i = 0; i < owner.ConditionIds.Length; i++)
            {
                int key = owner.ConditionIds[i];
                List<OwnerRow>? list;

                if (!m_ownersOfCondition.TryGetValue(key, out list))
                {
                    list = new List<OwnerRow>();
                    m_ownersOfCondition.Add(key, list);
                }

                list.Add(owner);
            }
        }

        /// <summary>先异步把进度与台账读回来，再登记条件（次序见文件头 ①②）。</summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="nickname">显示名。</param>
        /// <returns>任务。</returns>
        private async Task LoadThenRegisterAsync(PlayerState state, string nickname)
        {
            try
            {
                int progressRows = state.Store == null ? 0 : await state.Store.LoadAsync().ConfigureAwait(false);
                int ledgerRows = state.Ledger == null ? 0 : await state.Ledger.LoadAsync().ConfigureAwait(false);

                Note?.Invoke("玩家 " + state.PlayerId + "（" + nickname + "）的成就进度已读回：" +
                             progressRows + " 条进度、" + ledgerRows + " 条台账。");

                RegisterAll(state, nickname);

                state.Ready = true;

                // 排队的事实现在补上（顺序 = 收到的顺序）
                for (int i = 0; i < state.Pending.Count; i++)
                {
                    Notify(state, state.Pending[i]);
                }

                if (state.Pending.Count > 0)
                {
                    Note?.Invoke("玩家 " + state.PlayerId + " 在加载期间收到的 " + state.Pending.Count +
                                 " 条事实已补记（没丢）。");
                }

                state.Pending.Clear();
            }
            catch (Exception ex)
            {
                // ⚠️ 读不回来**不能**当"进度是 0"继续跑 —— 那会把库里的进度覆盖成 0（文件头 ①）
                Note?.Invoke("❌ 玩家 " + state.PlayerId + " 的成就进度**读不回来**：" + ex.Message + "\n" +
                             "    这一局他的成就判定会被跳过（宁可不记，也不能把库里的进度覆盖成 0）。");

                state.Tracker = null;
                state.Ready = false;
            }
        }

        /// <summary>把**全部成就**的条件登记到这个玩家的判定器上（三阶段，与客户端同一套做法）。</summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="nickname">显示名。</param>
        private void RegisterAll(PlayerState state, string nickname)
        {
            if (state.Store == null)
            {
                return;     // 没接数据库：不判定（避免"判定成功但没有任何地方记"的假象）
            }

            var tracker = new ConditionTracker(state.Store);
            tracker.ProgressChanged += (key, progress) => OnProgressChanged(state, key, progress);
            tracker.ConditionMet += (key, progress) => OnConditionMet(state, key);

            state.Tracker = tracker;

            int registered = 0;

            for (int i = 0; i < m_tables.Achievements.Count; i++)
            {
                OwnerRow owner = m_tables.Achievements[i];

                for (int c = 0; c < owner.ConditionIds.Length; c++)
                {
                    ConditionRow? row = m_tables.FindCondition(owner.ConditionIds[c]);

                    if (row == null)
                    {
                        continue;       // 加载时已经校验过，这里只是防御
                    }

                    // ⚠️ `resetProgress: false`：成就的语义是"**累计**"（进度不清零）。
                    //    代价是"已达成 ⇒ 登记时当场回调" ⇒ 每次启动都会回调一次
                    //    ⇒ **必须靠台账挡住重复发奖**（这正是 M4-B 那个坑，见下面 `OnConditionMet`）。
                    tracker.Register(row.Id, row.Def, false);
                    registered++;
                }
            }

            Note?.Invoke("玩家 " + state.PlayerId + "（" + nickname + "）的成就条件已登记 " + registered +
                         " 条（" + m_tables.AchievementCount + " 个成就），判定逻辑用的是**共享层那一份**。");
        }

        /// <summary>进度变了（只记一条日志；落库交给 `FlushAllAsync`）。</summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="key">条件编号。</param>
        /// <param name="progress">进度。</param>
        /// <summary>
        /// 把某个玩家当前的**权威进度**拍成一条可以下发的消息（客户端只显示、不再自己算）。
        /// <para>⚠️ 内容是"这个玩家关心的**全部条件**的当前值"（= 全量），不是增量 ——
        /// 与 D5（每 tick 全量快照）同一个判据：**全量最省心**，
        /// 增量一旦漏一条就会永久少一格，而且**不报错**。</para>
        /// </summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="serverTick">服务端逻辑帧号（排查"这条是什么时候的"）。</param>
        /// <param name="message">要下发的消息。</param>
        /// <returns>这个玩家被追踪、判定器可用时返回 true；否则 false（**不发**）。</returns>
        public bool TryBuildProgressView(long playerId, long serverTick, out ProgressSync message)
        {
            message = new ProgressSync();

            PlayerState? state;

            if (!m_players.TryGetValue(playerId, out state) || state.Tracker == null)
            {
                return false;
            }

            var seen = new HashSet<int>();

            for (int i = 0; i < m_tables.Achievements.Count; i++)
            {
                OwnerRow owner = m_tables.Achievements[i];
                int[] conditionIds = owner.ConditionIds;

                for (int c = 0; c < conditionIds.Length; c++)
                {
                    // 两个成就可能共用同一条条件 ⇒ 去重，否则客户端会看到重复条目
                    if (!seen.Add(conditionIds[c]))
                    {
                        continue;
                    }

                    ConditionProgress progress;

                    if (!state.Tracker.TryGetProgress(conditionIds[c], out progress))
                    {
                        continue;
                    }

                    message.Entries.Add(new ConditionProgressEntry
                    {
                        ConditionKey = conditionIds[c],
                        Current = progress.Current,
                        Required = progress.Required,
                        Met = progress.IsMet
                    });
                }
            }

            message.ServerTick = serverTick;
            return true;
        }

        /// <summary>条件进度推进了（还没达成的那一条）。</summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="key">条件编号。</param>
        /// <param name="progress">新进度。</param>
        private void OnProgressChanged(PlayerState state, int key, ConditionProgress progress)
        {
            if (progress.IsMet)
            {
                return;     // 达成那条由 `OnConditionMet` 报（避免同一个信息说两遍）
            }

            Note?.Invoke("成就进度：玩家 " + state.PlayerId + " 条件 " + key + " → " +
                         progress.Current + "/" + progress.Required);

            // 进度真的动了 ⇒ 让宿主把**权威值**推给客户端（未做#2）
            ProgressChanged?.Invoke(state.PlayerId);
        }

        /// <summary>
        /// 一个条件达成了 ⇒ 看看是谁的成就，并**在台账允许时**记下发奖。
        ///
        /// <para>
        /// ⚠️ 这里是 M4-B 那个坑的正面写法：`resetProgress: false` 的登记会在**每次启动**时
        /// 把"已达成"的条件再回调一次（这就是"登录即解锁"要的效果）。
        /// 如果这里不查台账，**每重启一次服务端就重复发一遍奖**。
        /// </para>
        /// </summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="key">条件编号。</param>
        private void OnConditionMet(PlayerState state, int key)
        {
            List<OwnerRow>? owners;

            if (!m_ownersOfCondition.TryGetValue(key, out owners))
            {
                return;
            }

            for (int i = 0; i < owners.Count; i++)
            {
                OwnerRow owner = owners[i];

                // 只有"这个 owner 的**全部**条件都达成"才算解锁
                if (!AllConditionsMet(state, owner))
                {
                    continue;
                }

                // ⚠️ 台账说发过了 ⇒ **不发、不重复记、不重复报**
                if (state.Ledger != null &&
                    state.Ledger.HasGranted(ERewardOwnerKind.Achievement, owner.Id))
                {
                    m_duplicateGrantAvoided++;
                    continue;
                }

                RewardRow? reward = owner.RewardId == 0 ? null : m_tables.FindReward(owner.RewardId);

                int expDelta = reward == null ? 0 : reward.Exp;
                int goldDelta = reward == null ? 0 : reward.Gold;

                // ⚠️ 用**带发放**的那个重载（§21.4 未做#1 的收口）：
                //    发放与台账**同生同死** —— 它们在同一次 flush 的同一个事务里落库。
                //    分两次写的后果是"台账记了『发过』、金币却没加"，而台账是**单调**的，
                //    那个玩家**永远不会再补发** ⇒ 静默的数据丢失，只有对账才看得出。
                //    `HasGranted` 上面已经挡过一次重复；这个方法自己也幂等（重复连发放都不排）。
                state.Ledger?.MarkGrantedWithPayout(
                    ERewardOwnerKind.Achievement, owner.Id, owner.RewardId, expDelta, goldDelta);

                state.Unlocked++;
                m_unlocked++;

                Note?.Invoke("🏆 **成就解锁**（服务端权威）：玩家 " + state.PlayerId + " → " +
                             owner.Id + "「" + owner.Name + "」" +
                             (reward == null
                                 ? "（无奖励）"
                                 : "→ 奖励 " + reward + "（exp +" + expDelta + " / gold +" + goldDelta +
                                   "，与台账**同一事务**落库）"));
            }

            // ⚠️ 放在**循环之后**：一条事实可能让好几个成就同时解锁，推一次就够。
            //    而且"条件达成了但成就还没解锁"（别的条件没满）也要推 ——
            //    客户端要看得到 2/3 变成 3/3（它自己不再算了）。
            ProgressChanged?.Invoke(state.PlayerId);
        }

        /// <summary>这个 owner 的所有条件都达成了吗。</summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="owner">任务或成就。</param>
        /// <returns>全部达成为 true。</returns>
        private static bool AllConditionsMet(PlayerState state, OwnerRow owner)
        {
            if (state.Tracker == null)
            {
                return false;
            }

            for (int i = 0; i < owner.ConditionIds.Length; i++)
            {
                if (!state.Tracker.IsMet(owner.ConditionIds[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>把一条事实喂给判定器。</summary>
        /// <param name="state">玩家状态。</param>
        /// <param name="fact">事实。</param>
        private void Notify(PlayerState state, in ProgressFact fact)
        {
            if (state.Tracker == null)
            {
                return;
            }

            m_factsApplied++;

            // ⚠️ `Notify` 的返回值是"动到了几条条件"（0 = 这个玩家没有关心的事件，很正常）
            state.Tracker.Notify(fact.EventType, fact.TargetId, fact.Count);
        }

        /// <summary>冲库并释放（断开时用）。</summary>
        /// <param name="state">玩家状态。</param>
        /// <returns>任务。</returns>
        private async Task FlushAndDisposeAsync(PlayerState state)
        {
            try
            {
                if (state.Loading != null && !state.Loading.IsCompleted)
                {
                    await state.Loading.ConfigureAwait(false);
                }

                if (state.Store != null && state.Store.DirtyCount > 0)
                {
                    await state.Store.FlushAsync().ConfigureAwait(false);
                }

                if (state.Ledger != null && state.Ledger.DirtyCount > 0)
                {
                    await state.Ledger.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Note?.Invoke("❌ 玩家 " + state.PlayerId + " 断开时的成就落库失败：" + ex.Message);
            }
            finally
            {
                state.Dispose();
            }
        }
    }
}
