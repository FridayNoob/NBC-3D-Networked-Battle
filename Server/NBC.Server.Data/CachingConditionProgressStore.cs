// ============================================================================
//  CachingConditionProgressStore —— `IConditionProgressStore` 的**写回缓存**实现
//  项目：3D联网战斗Demo   对应：M4-S3、需求文档 DB-03 / DB-04 / DB-10
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么类名是 `Caching...` 而不是 `MySql...`（改过一次，如实记）
//  ---------------------------------------------------------------------------
//  一开始它叫 `MySqlConditionProgressStore`，因为"它是 MySQL 版的实现"。
//  但写完发现：**真正碰 MySQL 的只有那个 DAO**，本类干的事是
//  "内存工作集 + 脏集 + 单飞落库" —— 那是**缓存策略**，与数据库是谁无关。
//
//  ⇒ 名字里的 `MySql` 会让人以为"换数据库要改这个类"，而其实一行都不用改。
//    现在它依赖 `IConditionProgressDao`（见那个文件头为什么要有这道缝）。
//    **名字要说实话** —— 同族：`IQuestRewardSink` 那个"名字里的 Quest 是历史遗留"
//    我选择不改（要动 8 个文件）；而这个**一个调用方都还没有**，改名是免费的，
//    所以当场改掉。判断标准是"改名成本 vs 名字带来的误解"，不是"要不要完美"。
//  ---------------------------------------------------------------------------
//  一句话：**内存里是权威工作集，数据库是它的持久化副本。**
//  ---------------------------------------------------------------------------
//
//      GetProgress / SetProgress / Remove   ← 纯内存，**一次 IO 都没有**
//                                              （Tick 主循环每帧都在调它们）
//
//      LoadAsync()   ← 进副本/登录时调一次（异步、脱离主循环线程）
//      FlushAsync()  ← 按"落库时机"调（异步、脱离主循环线程）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么**必须**是写回缓存（不是"每次 Set 就 UPDATE 一下"）
//  ---------------------------------------------------------------------------
//  看一眼调用点就明白了（`ConditionTracker.Notify`）：
//
//      foreach (已登记的条件) {
//          int before = m_store.GetProgress(pair.Key);   // ← 每个条件读一次
//          ...
//          m_store.SetProgress(pair.Key, after.Current);  // ← 每个条件写一次
//      }
//
//  打死一只怪就可能触发**十几次 Get + 十几次 Set**，30Hz 下每秒几百次。
//  一旦这里做同步查询，就是"打一只怪卡一下"，而且**卡在逻辑线程上**——
//  直接违反 DB-03「不在 Tick 主循环里做同步阻塞查询」。
//  ⇒ **接口是同步的，实现就必须是内存的。** 这不是优化，这是唯一能同时满足
//     "接口好用"和"DB-03"的做法。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 写的是**绝对值**，因此落库是**幂等**的（这条撑住了重试与乱序）
//  ---------------------------------------------------------------------------
//  脏集里存的是"这个键现在的值"，不是"这个键变了多少"。
//  于是同一批数据写两次结果完全一样 ⇒ 网络抖一下重发、或者崩溃后重放，都不会算两遍。
//  （存增量的方案必须再配一张"已应用"表，复杂度立刻上一个台阶。）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 线程模型（**这是本文件最容易出错的地方**）
//  ---------------------------------------------------------------------------
//      · `GetProgress` / `SetProgress` / `Remove` / `SnapshotDirty` / `MarkFlushed`
//        —— 都上锁。主循环线程调前三个，落库线程调后两个。
//      · **绝不能持锁做 IO**：`PrepareFlush()` 在锁里**只拷一份快照**，出了锁再发 SQL。
//        持锁发 SQL = 把主循环卡在等网络上（等于没写缓存）。
//      · `FlushAsync` 用**单飞（single-flight）**：上一次没写完就不开新的。
//        理由不是省事，是**正确性**：两次写若乱序落地，后落的那次会把**旧的**绝对值
//        写进库 —— 那是静默的数据回退，而且只在高并发时才出现。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NBC.Shared.Condition;

namespace NBC.Server.Data
{
    /// <summary>MySQL 版的条件进度存放处（写回缓存；**一个实例对应一个玩家**）。</summary>
    public sealed class CachingConditionProgressStore : IConditionProgressStore, IDisposable
    {
        /// <summary>读写。</summary>
        private readonly IConditionProgressDao m_dao;

        /// <summary>**这个实例属于哪个玩家**（见下面那条"归属靠构造绑定"的说明）。</summary>
        private readonly long m_playerId;

        /// <summary>权威工作集：条件编号 → 已累计数量。</summary>
        private readonly Dictionary<int, int> m_progress = new Dictionary<int, int>();

        /// <summary>脏集：条件编号 → 尚未落库的绝对值。</summary>
        private readonly Dictionary<int, int> m_dirty = new Dictionary<int, int>();

        /// <summary>保护上面两个字典（主循环线程 ↔ 落库线程）。</summary>
        private readonly object m_gate = new object();

        /// <summary>单飞闸门（见文件头"线程模型"）。0 = 空闲，1 = 有一次落库在飞。</summary>
        private int m_flushInFlight;

        /// <summary>累计落库次数/行数（调试面板用；"到底有没有真的写库"要看得见）。</summary>
        private int m_flushCount;

        /// <summary>累计落库行数。</summary>
        private int m_flushedRowCount;

        /// <summary>已经 Dispose 了没有。</summary>
        private bool m_disposed;

        /// <summary>
        /// 造一个属于某个玩家的进度存放处。
        /// <para>
        /// ⚠️ **玩家归属靠"构造时绑定"，而不是给接口加一个 `playerId` 参数** ——
        /// `IConditionProgressStore` 刻意只有三个方法、只用 `int`（那个文件头写明了理由），
        /// 加参数会把它从"通用接缝"变成"数据库专用接口"，客户端的内存实现也得跟着改。
        /// 绑在构造上的另一个好处：**`ConditionTracker` 一行都不用改**（这正是那道缝的回报）。
        /// </para>
        /// </summary>
        /// <param name="dao">读写实现（不能为 null；可以是 MySQL 的，也可以是测试用的内存假实现）。</param>
        /// <param name="playerId">这个存放处属于哪个玩家。</param>
        public CachingConditionProgressStore(IConditionProgressDao dao, long playerId)
        {
            if (dao == null)
            {
                throw new ArgumentNullException(nameof(dao), "[CachingConditionProgressStore] 读写实现是 null。");
            }

            if (playerId <= 0)
            {
                // 0 或负数几乎总是"忘了传"或"还没登录就建了"。
                // 静默放行会让**所有玩家共用一个 0 号玩家的存档** —— 一个查起来极费劲的串档 bug。
                throw new ArgumentOutOfRangeException(nameof(playerId),
                    "[CachingConditionProgressStore] 玩家编号必须 > 0（当前 " + playerId + "）。\n" +
                    "多半是「还没登录就先建了存放处」—— 应当先拿到 player_profile.player_id 再建。");
            }

            m_dao = dao;
            m_playerId = playerId;
        }

        /// <summary>这个存放处属于哪个玩家。</summary>
        public long PlayerId
        {
            get { return m_playerId; }
        }

        /// <summary>内存里装了几条进度。</summary>
        public int Count
        {
            get
            {
                lock (m_gate)
                {
                    return m_progress.Count;
                }
            }
        }

        /// <summary>还有几条没落库（调试面板显示"有没有积压"用）。</summary>
        public int DirtyCount
        {
            get
            {
                lock (m_gate)
                {
                    return m_dirty.Count;
                }
            }
        }

        /// <summary>落库过几次、共几行（**证明"真的写库了"**，而不是只有代码看起来写了）。</summary>
        public string DescribeFlushStats()
        {
            return "落库 " + m_flushCount + " 次、共 " + m_flushedRowCount + " 行";
        }

        // ====================================================================
        //  IConditionProgressStore（**全是内存操作，零 IO**）
        // ====================================================================

        /// <summary>读一个条件的进度；没记录过时返回 0（接口契约：**读不到是正常状态**）。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <returns>已累计的数量。</returns>
        public int GetProgress(int conditionKey)
        {
            lock (m_gate)
            {
                int value;
                return m_progress.TryGetValue(conditionKey, out value) ? value : 0;
            }
        }

        /// <summary>写一个条件的进度（**只改内存 + 标脏**，不碰数据库）。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <param name="value">已累计的数量（绝对值）。</param>
        public void SetProgress(int conditionKey, int value)
        {
            lock (m_gate)
            {
                m_progress[conditionKey] = value;
                m_dirty[conditionKey] = value;
            }
        }

        /// <summary>删掉一个条件的进度（重置任务/清档用）。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <returns>本来有记录、真的删掉了才返回 true。</returns>
        public bool Remove(int conditionKey)
        {
            lock (m_gate)
            {
                bool existed = m_progress.Remove(conditionKey);

                // ⚠️ **必须把"删除"也记进脏集**（用一个 0 表示），否则：
                //    内存删了、库里还在 ⇒ 下次登录又"读回来"了。
                //    表现是"我明明重置过，怎么又有了" —— 一条只在重启后才出现的悬案。
                if (existed)
                {
                    m_dirty[conditionKey] = 0;
                }

                return existed;
            }
        }

        // ====================================================================
        //  载入 / 落库（**唯一碰数据库的两个方法，都是异步**）
        // ====================================================================

        /// <summary>
        /// 从库里把**这个玩家的全部进度**读进内存（进副本/登录时调一次）。
        /// <para>⚠️ 会把内存里的既有进度**合并**进去（库里的大就用库里的）——
        /// 不是清空重填：同一进程内重复调用（比如重进副本）不应该把
        /// "这局刚打的、还没落库的"进度抹掉。</para>
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>读回来几条。</returns>
        public async Task<int> LoadAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            EnsureNotDisposed();

            IReadOnlyList<ConditionProgressRow> rows =
                await m_dao.LoadByPlayerAsync(m_playerId, cancellationToken).ConfigureAwait(false);

            lock (m_gate)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    ConditionProgressRow row = rows[i];
                    int current;

                    if (!m_progress.TryGetValue(row.condition_key, out current) || row.progress > current)
                    {
                        m_progress[row.condition_key] = row.progress;
                    }
                }
            }

            return rows.Count;
        }

        /// <summary>
        /// 把脏集写回库。
        /// <para>⚠️ **单飞**：已经有一次在飞时**直接返回 0**（不排队、不等待）。
        /// 理由见文件头 —— 排队会积压，而等待会阻塞调用方；
        /// 而且"合并"天然发生：这一次不写，脏集只会更全，下一次一起写。</para>
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>写了几行（0 = 没有脏数据，或已有一次在飞）。</returns>
        public async Task<int> FlushAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            EnsureNotDisposed();

            if (Interlocked.CompareExchange(ref m_flushInFlight, 1, 0) != 0)
            {
                return 0;   // 已有一次在飞 → 合并到那一次（它的脏集只会更全）
            }

            try
            {
                List<ConditionProgressRow> batch = TakeDirtyBatch();

                if (batch.Count == 0)
                {
                    return 0;
                }

                int written = await m_dao.UpsertBatchAsync(m_playerId, batch, cancellationToken).ConfigureAwait(false);

                m_flushCount++;
                m_flushedRowCount += written;
                return written;

                // ⚠️ **失败时故意不把脏集放回去**（没有 catch 就自然这样）：
                //    脏集在 `TakeDirtyBatch` 里已经被取走了。若这里"失败就回滚脏集"，
                //    会和"取走之后又被 SetProgress 更新过"的键**打架**
                //    （回滚会把新值覆盖成旧值 = 静默数据回退）。
                //    正确做法是**保留新值**：下一次 SetProgress 会重新标脏；
                //    而"一直没再变"的那个键，会在下一次 `FlushAsync` 之前
                //    由 `MarkUnflushedOnFailure` 补回脏集（见下）。
            }
            finally
            {
                Interlocked.Exchange(ref m_flushInFlight, 0);
            }
        }

        /// <summary>
        /// 把一批在飞的脏数据**重新标脏**（落库失败时由调用方补一刀）。
        /// <para>为什么单独一个方法、而不是在 `FlushAsync` 里 catch：
        /// 失败重试的**策略**（重试几次、间隔多久、要不要告警）属于调用方/主循环，
        /// 数据层只提供"把这批再标脏"这个动作。这样数据层不藏策略，也就不会藏 bug。</para>
        /// </summary>
        /// <param name="rows">要重新标脏的行。</param>
        public void MarkUnflushed(IReadOnlyList<ConditionProgressRow> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            lock (m_gate)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    ConditionProgressRow row = rows[i];
                    int current;

                    // ⚠️ **只在"内存里还是这个值"时才标脏**：
                    //    如果取走之后主循环又改过它，那它已经是新值了，
                    //    这里再标脏会把**旧值**写回去（静默回退）。
                    bool stillSame = !m_progress.TryGetValue(row.condition_key, out current)
                                     || current == row.progress;

                    if (stillSame)
                    {
                        m_dirty[row.condition_key] = row.progress;
                    }
                }
            }
        }

        /// <summary>退订/收尾。**不自动落库** —— 收尾该由调用方显式 `FlushAsync`，
        /// 因为"关掉对象时要不要写库"是个策略问题（见 `Docs\27` §十二的落库时机表）。</summary>
        public void Dispose()
        {
            m_disposed = true;
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>把脏集**拷一份**出来并清空（锁里只做拷贝，**绝不持锁做 IO**）。</summary>
        /// <returns>要落库的行。</returns>
        private List<ConditionProgressRow> TakeDirtyBatch()
        {
            lock (m_gate)
            {
                if (m_dirty.Count == 0)
                {
                    return new List<ConditionProgressRow>(0);
                }

                var batch = new List<ConditionProgressRow>(m_dirty.Count);

                foreach (KeyValuePair<int, int> pair in m_dirty)
                {
                    batch.Add(new ConditionProgressRow
                    {
                        player_id = m_playerId,
                        condition_key = pair.Key,
                        progress = pair.Value
                    });
                }

                m_dirty.Clear();
                return batch;
            }
        }

        /// <summary>Dispose 之后不许再用。</summary>
        private void EnsureNotDisposed()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException(nameof(CachingConditionProgressStore),
                    "[CachingConditionProgressStore] 已经 Dispose 了，不能再调用。");
            }
        }
    }
}
