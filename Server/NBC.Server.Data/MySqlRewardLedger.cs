// ============================================================================
//  MySqlRewardLedger —— `IRewardLedger` 的持久化实现（内存集合 + 写回）
//  项目：3D联网战斗Demo   对应：M4-S3、`Docs\27` §11.5 / §11.10
//
//  ---------------------------------------------------------------------------
//  它为什么比 `CachingConditionProgressStore` 简单得多（不是偷懒，是语义不同）
//  ---------------------------------------------------------------------------
//                      进度表                          台账
//      值的性质        **会变**（1→2→3，还可能被 Remove）  **单调**（发过就是发过）
//      写冲突          "旧值覆盖新值" = **静默回退**      不存在：重复写同一行无副作用
//      合并规则        "同一次落库期间又被改过"很要命        不需要：后写的和先写的一样
//      ⇒ 需要的机制    脏集 + 单飞 + `MarkUnflushed` 值比较   只需要"脏集 + 单飞"
//
//  ⇒ **同一个模式，硬件的复杂度取决于数据语义**。把台账写成一个缩水版的进度缓存
//    不是"复制粘贴"，而是"这个数据本来就不需要那些机制"。
//    （反过来也成立：如果哪天台账要支持"撤销发奖"，它就必须补上那套值比较。）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 它和进度 store 必须**成对使用**（这个约束要写清，否则会漏）
//  ---------------------------------------------------------------------------
//  "重启不重复发奖"要求两件事**都**活着：
//
//      ① 进度活着（`MySqlConditionProgressStore`）→ 否则重启后条件回到 0，根本不会触发解锁
//      ② 台账活着（本类）                          → 否则触发了就又发一遍
//
//  只上其中一个都是**半成品**，而且表现不一样：
//      只有①：重启后条件满 → **重复发奖**（就是 §11.5 那个 bug）
//      只有②：重启后条件回到 0 → **成就不解锁**（玩家觉得"我明明打够了"）
//
//  ⇒ 所以 `Docs\27` §11.7 那件事（给密码 + 跑迁移）是**两件事的前提**，
//    不是"可选的一步验证"。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 载入时机：**必须在任何 `HasGranted` 之前**
//  ---------------------------------------------------------------------------
//  没载入就当作"什么都没发过" ⇒ 会把已经发过的奖**再发一遍**（而且台账本身是空的可信证据）。
//  所以 `LoadAsync` 要么成功、要么**让调用方知道失败**：
//  本类**不吞异常**（连不上就抛 `DatabaseUnavailableException`，见 `DbConnectionFactory`），
//  由调用方按 DB-10 决定"降级启动"还是"这一局不发奖"。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NBC.Shared.Reward;

namespace NBC.Server.Data
{
    /// <summary>MySQL 版的已发奖励台账（**一个实例对应一个玩家**）。</summary>
    public sealed class MySqlRewardLedger : IRewardLedger, IDisposable
    {
        /// <summary>读写。</summary>
        private readonly IRewardLedgerDao m_dao;

        /// <summary>这个台账属于哪个玩家。</summary>
        private readonly long m_playerId;

        /// <summary>已发放的键（`种类:编号`）→ 奖励编号（留着排查用）。</summary>
        private readonly Dictionary<string, int> m_granted = new Dictionary<string, int>();

        /// <summary>还没落库的键（键 → 要写的行）。</summary>
        private readonly Dictionary<string, RewardGrantedRow> m_dirty = new Dictionary<string, RewardGrantedRow>();

        /// <summary>保护上面两个字典。</summary>
        private readonly object m_gate = new object();

        /// <summary>单飞闸门。</summary>
        private int m_flushInFlight;

        /// <summary>累计落库次数/行数（"到底有没有真的写库"要看得见）。</summary>
        private int m_flushCount;

        /// <summary>累计落库行数。</summary>
        private int m_flushedRowCount;

        /// <summary>Dispose 过没有。</summary>
        private bool m_disposed;

        /// <summary>造一个属于某个玩家的台账。</summary>
        /// <param name="dao">读写实现（不能为 null）。</param>
        /// <param name="playerId">玩家编号（必须 &gt; 0）。</param>
        public MySqlRewardLedger(IRewardLedgerDao dao, long playerId)
        {
            if (dao == null)
            {
                throw new ArgumentNullException(nameof(dao), "[MySqlRewardLedger] 读写实现是 null。");
            }

            if (playerId <= 0)
            {
                // 同进度 store：静默放行会让所有玩家共用一个 0 号台账 ⇒ **串档**
                throw new ArgumentOutOfRangeException(nameof(playerId),
                    "[MySqlRewardLedger] 玩家编号必须 > 0（当前 " + playerId + "）。");
            }

            m_dao = dao;
            m_playerId = playerId;
        }

        /// <summary>这个台账属于哪个玩家。</summary>
        public long PlayerId
        {
            get { return m_playerId; }
        }

        /// <summary>内存里装了几条已发放记录。</summary>
        public int Count
        {
            get
            {
                lock (m_gate)
                {
                    return m_granted.Count;
                }
            }
        }

        /// <summary>还有几条没落库。</summary>
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

        /// <summary>落库过几次、共几行。</summary>
        public string DescribeFlushStats()
        {
            return "台账落库 " + m_flushCount + " 次、共 " + m_flushedRowCount + " 行";
        }

        // ====================================================================
        //  IRewardLedger（**全是内存操作，零 IO**）
        // ====================================================================

        /// <summary>这份奖励发过没有。</summary>
        /// <param name="kind">谁发的。</param>
        /// <param name="ownerId">发布者编号。</param>
        /// <returns>发过返回 true。</returns>
        public bool HasGranted(ERewardOwnerKind kind, int ownerId)
        {
            lock (m_gate)
            {
                return m_granted.ContainsKey(KeyOf(kind, ownerId));
            }
        }

        /// <summary>记下"刚刚发了这份奖励"（内存 + 标脏）。</summary>
        /// <param name="kind">谁发的。</param>
        /// <param name="ownerId">发布者编号。</param>
        /// <param name="rewardId">奖励编号。</param>
        public void MarkGranted(ERewardOwnerKind kind, int ownerId, int rewardId)
        {
            string key = KeyOf(kind, ownerId);

            lock (m_gate)
            {
                // ⚠️ 已经有记录就**什么都不做**（台账是单调的，见文件头）。
                //    这样"重复标记"天然幂等，也就不需要进度那边那套值比较。
                if (m_granted.ContainsKey(key))
                {
                    return;
                }

                m_granted[key] = rewardId;
                m_dirty[key] = new RewardGrantedRow
                {
                    player_id = m_playerId,
                    owner_kind = (int)kind,
                    owner_id = ownerId,
                    reward_id = rewardId
                };
            }
        }

        // ====================================================================
        //  载入 / 落库
        // ====================================================================

        /// <summary>
        /// 从库里把这个玩家的全部台账读进内存（**必须在任何 `HasGranted` 之前调**，见文件头）。
        /// <para>⚠️ 与进度 store 不同：这里**不做"取大"合并** —— 台账只有"有/没有"，
        /// 库里有就是有（它是**单调**的事实记录，不存在谁更新的问题）。</para>
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>读回来几条。</returns>
        public async Task<int> LoadAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            EnsureNotDisposed();

            IReadOnlyList<RewardGrantedRow> rows =
                await m_dao.LoadByPlayerAsync(m_playerId, cancellationToken).ConfigureAwait(false);

            lock (m_gate)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    RewardGrantedRow row = rows[i];
                    m_granted[((int)row.owner_kind) + ":" + row.owner_id] = row.reward_id;
                }
            }

            return rows.Count;
        }

        /// <summary>把脏集写回库（**单飞**：已有一次在飞就直接返回 0）。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>影响了几行。</returns>
        /// <exception cref="DatabaseUnavailableException">连不上库（**这一批会留在脏集里**，下次会重试）。</exception>
        public async Task<int> FlushAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            EnsureNotDisposed();

            if (Interlocked.CompareExchange(ref m_flushInFlight, 1, 0) != 0)
            {
                return 0;
            }

            List<RewardGrantedRow>? batch = null;

            try
            {
                batch = TakeDirtyBatch();

                if (batch.Count == 0)
                {
                    return 0;
                }

                int written = await m_dao.InsertBatchAsync(m_playerId, batch, cancellationToken).ConfigureAwait(false);

                m_flushCount++;
                m_flushedRowCount += batch.Count;
                return written;
            }
            catch
            {
                // ⚠️ **失败必须把这一批放回脏集**（这是台账与进度 store 最关键的差别）：
                //    `MarkGranted` 是"内存里已经有它就不再标脏"——所以脏集一旦被取走又没写成功，
                //    那个键就**再也不会被标脏**了 ⇒ "发了奖但台账没落库"**永久丢失**
                //    ⇒ 下次重启会**再发一遍**，等于台账白装。
                //
                //    进度那边要"比一下值再决定标不标"（怕旧值覆盖新值）；
                //    台账是**单调**的，所以**无条件放回**就是对的 —— 落库语句本身幂等。
                RestoreDirty(batch);
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref m_flushInFlight, 0);
            }
        }

        /// <summary>收尾（**不自动落库**，同进度 store）。</summary>
        public void Dispose()
        {
            m_disposed = true;
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>
        /// 把一批行**无条件放回**脏集（落库失败时用）。
        /// <para>⚠️ 与进度 store 的 `MarkUnflushed` 差别在这里：那边要**先比一下值**
        /// （怕把"取走之后又被改过"的新值覆盖成旧值）；台账是**单调**的，
        /// 同一个键放回几次结果都一样 ⇒ **无条件放回就是对的**，也就不需要那套比较。</para>
        /// </summary>
        /// <param name="rows">要放回的行。</param>
        private void RestoreDirty(List<RewardGrantedRow>? rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            lock (m_gate)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    RewardGrantedRow row = rows[i];
                    m_dirty[((int)row.owner_kind) + ":" + row.owner_id] = row;
                }
            }
        }

        /// <summary>把脏集拷一份出来并清空（锁里只拷贝）。</summary>
        /// <returns>要落库的行。</returns>
        private List<RewardGrantedRow> TakeDirtyBatch()
        {
            lock (m_gate)
            {
                if (m_dirty.Count == 0)
                {
                    return new List<RewardGrantedRow>(0);
                }

                var batch = new List<RewardGrantedRow>(m_dirty.Count);

                foreach (KeyValuePair<string, RewardGrantedRow> pair in m_dirty)
                {
                    batch.Add(pair.Value);
                }

                m_dirty.Clear();
                return batch;
            }
        }

        /// <summary>键的拼法（与 `InMemoryRewardLedger` 保持一致：`种类:编号`）。</summary>
        /// <param name="kind">谁发的。</param>
        /// <param name="ownerId">发布者编号。</param>
        /// <returns>键。</returns>
        private static string KeyOf(ERewardOwnerKind kind, int ownerId)
        {
            return ((int)kind) + ":" + ownerId;
        }

        /// <summary>Dispose 之后不许再用。</summary>
        private void EnsureNotDisposed()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException(nameof(MySqlRewardLedger),
                    "[MySqlRewardLedger] 已经 Dispose 了，不能再调用。");
            }
        }
    }
}
