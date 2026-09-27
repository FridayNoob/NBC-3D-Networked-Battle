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
using NBC.Server.Game;
using NBC.Shared.Reward;

namespace NBC.Server.Data
{
    /// <summary>MySQL 版的已发奖励台账（**一个实例对应一个玩家**）。</summary>
    public sealed class MySqlRewardLedger : IRewardLedger, IPlayerRewardLedger, IDisposable
    {
        /// <summary>读写。</summary>
        private readonly IRewardLedgerDao m_dao;

        /// <summary>这个台账属于哪个玩家。</summary>
        private readonly long m_playerId;

        /// <summary>已发放的键（`种类:编号`）→ 奖励编号（留着排查用）。</summary>
        private readonly Dictionary<string, int> m_granted = new Dictionary<string, int>();

        /// <summary>还没落库的键（键 → 要写的行）。</summary>
        private readonly Dictionary<string, RewardGrantedRow> m_dirty = new Dictionary<string, RewardGrantedRow>();

        /// <summary>
        /// 还没落库的**实际发放**（键 → exp/gold）。与 `m_dirty` **同生同死**：
        /// 两者一起被取走、一起写进**同一个事务**、失败时一起放回（见 `FlushAsync`）。
        /// <para>⚠️ 为什么发放不能当场写库：登录是同步的、握手不许等 IO（见文件头），
        /// 所以发放只能跟着台账一起排队，冲库时一并落。这也正是"同一事务"的由来。</para>
        /// </summary>
        private readonly Dictionary<string, PendingPayout> m_pendingPayout = new Dictionary<string, PendingPayout>();

        /// <summary>保护上面两个字典。</summary>
        private readonly object m_gate = new object();

        /// <summary>单飞闸门。</summary>
        private int m_flushInFlight;

        /// <summary>累计落库次数/行数（"到底有没有真的写库"要看得见）。</summary>
        private int m_flushCount;

        /// <summary>累计落库行数。</summary>
        private int m_flushedRowCount;

        /// <summary>累计发出去多少 exp / gold（"到底有没有真的发钱"要看得见）。</summary>
        private long m_paidExp;

        /// <summary>累计发出去多少 gold。</summary>
        private long m_paidGold;

        /// <summary>Dispose 过没有。</summary>
        private bool m_disposed;

        /// <summary>造一个属于某个玩家的台账。</summary>
        /// <param name="dao">读写实现（不能为 null）。</param>
        /// <param name="playerId">玩家编号（必须 &gt; 0；**负数 = 游客，没有档案，建不了**）。</param>
        public MySqlRewardLedger(IRewardLedgerDao dao, long playerId)
        {
            if (dao == null)
            {
                throw new ArgumentNullException(nameof(dao), "[MySqlRewardLedger] 读写实现是 null。");
            }

            if (playerId <= 0)
            {
                // 同进度 store：静默放行会让所有玩家共用一个 0 号台账 ⇒ **串档**。
                // ⚠️ 负数现在是**游客**（SRV-17a）：`reward_granted.player_id` 有外键指向
                //    `player_profile`，游客没有档案 ⇒ 这里当场炸是**对的**。
                throw new ArgumentOutOfRangeException(nameof(playerId),
                    "[MySqlRewardLedger] 玩家编号必须 > 0（当前 " + playerId + "）。" +
                    "负数 = 游客：游客没有玩家档案，领奖台账无处可挂（先做注册流程才有）。");
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

        /// <summary>
        /// 记下"刚刚发了这份奖励"，并把这笔**实际发放**（exp/gold）排进**同一个事务**。
        /// <para>⚠️ 幂等：这个键已经有记录时**什么都不做**（连发放也不排）——
        /// 否则"重复解锁"会重复发钱，而台账那边看不出异常（它本来就是单调的）。</para>
        /// </summary>
        /// <param name="kind">谁发的。</param>
        /// <param name="ownerId">发布者编号。</param>
        /// <param name="rewardId">奖励编号。</param>
        /// <param name="exp">这笔要加的 exp。</param>
        /// <param name="gold">这笔要加的 gold。</param>
        public void MarkGrantedWithPayout(ERewardOwnerKind kind, int ownerId, int rewardId, int exp, int gold)
        {
            string key = KeyOf(kind, ownerId);

            lock (m_gate)
            {
                if (m_granted.ContainsKey(key))
                {
                    return;     // 见上面的幂等说明：连发放也不排
                }

                m_granted[key] = rewardId;
                m_dirty[key] = new RewardGrantedRow
                {
                    player_id = m_playerId,
                    owner_kind = (int)kind,
                    owner_id = ownerId,
                    reward_id = rewardId
                };

                if (exp != 0 || gold != 0)
                {
                    m_pendingPayout[key] = new PendingPayout(exp, gold);
                }
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
            List<KeyValuePair<string, PendingPayout>>? payouts = null;

            try
            {
                batch = TakeDirtyBatch();
                payouts = TakePendingPayouts(batch);

                if (batch.Count == 0 && payouts.Count == 0)
                {
                    return 0;
                }

                int sumExp = 0;
                int sumGold = 0;

                for (int i = 0; i < payouts.Count; i++)
                {
                    sumExp += payouts[i].Value.Exp;
                    sumGold += payouts[i].Value.Gold;
                }

                // ⚠️ 台账与实际发放走**同一个事务**（§21.4 未做#1 收口）：
                //    分两次写会留下"台账说发过、金币没加"，而台账是单调的 ⇒ 那个玩家**永远不会再补发**。
                int written = await m_dao.InsertBatchAndPayAsync(
                    m_playerId, batch, sumExp, sumGold, cancellationToken).ConfigureAwait(false);

                m_flushCount++;
                m_flushedRowCount += batch.Count;
                m_paidExp += sumExp;
                m_paidGold += sumGold;
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
                //
                //    ⚠️ 发放也必须一起放回：台账与发放是同一事务，回滚了两边都没发生，
                //       所以"放回"不会造成重复发放（下一次重试恰好补上）。
                RestoreDirty(batch);
                RestorePayouts(payouts);
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

        /// <summary>
        /// 把这一批行对应的**发放**从待发集合里取走（锁里只拷贝/删除）。
        /// <para>⚠️ 只取**这一批行**对应的那些键：发放与台账行必须成对搬运，
        /// 否则会出现"行写进去了、钱留着下次发"或反过来。</para>
        /// </summary>
        /// <param name="rows">这一批要落库的行。</param>
        /// <returns>取走的发放（键 → exp/gold）。</returns>
        private List<KeyValuePair<string, PendingPayout>> TakePendingPayouts(List<RewardGrantedRow> rows)
        {
            var taken = new List<KeyValuePair<string, PendingPayout>>(0);

            if (rows.Count == 0 || m_pendingPayout.Count == 0)
            {
                return taken;
            }

            lock (m_gate)
            {
                taken = new List<KeyValuePair<string, PendingPayout>>(rows.Count);

                for (int i = 0; i < rows.Count; i++)
                {
                    string key = ((int)rows[i].owner_kind) + ":" + rows[i].owner_id;
                    PendingPayout payout;

                    if (m_pendingPayout.TryGetValue(key, out payout))
                    {
                        taken.Add(new KeyValuePair<string, PendingPayout>(key, payout));
                        m_pendingPayout.Remove(key);
                    }
                }
            }

            return taken;
        }

        /// <summary>落库失败时把发放**无条件放回**（与 `RestoreDirty` 同一理由）。</summary>
        /// <param name="payouts">要放回的发放。</param>
        private void RestorePayouts(List<KeyValuePair<string, PendingPayout>>? payouts)
        {
            if (payouts == null || payouts.Count == 0)
            {
                return;
            }

            lock (m_gate)
            {
                for (int i = 0; i < payouts.Count; i++)
                {
                    m_pendingPayout[payouts[i].Key] = payouts[i].Value;
                }
            }
        }

        /// <summary>把脏集拷一份出来并清空（锁里只拷贝）。</summary>
        /// <returns>要落库的行。</returns>
        private List<RewardGrantedRow> TakeDirtyBatch()        {
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

        /// <summary>一笔还没落库的**实际发放**（exp/gold）。</summary>
        private readonly struct PendingPayout
        {
            /// <summary>要加的 exp。</summary>
            public readonly int Exp;

            /// <summary>要加的 gold。</summary>
            public readonly int Gold;

            /// <summary>造一笔。</summary>
            /// <param name="exp">exp。</param>
            /// <param name="gold">gold。</param>
            public PendingPayout(int exp, int gold)
            {
                Exp = exp;
                Gold = gold;
            }
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
