// ============================================================================
//  CachingQuestStateStore —— 任务状态的**写回缓存**（内存权威 + 批量落库）
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十五
//
//  ---------------------------------------------------------------------------
//  一、为什么是写回缓存，而不是"每次 Set 就 UPDATE"
//  ---------------------------------------------------------------------------
//  与 `CachingConditionProgressStore` 同一条理由（`Docs\27` §11.4）：
//  **主循环里不许做 IO**。接取/交付是由客户端消息触发的，而消息在**网络泵**里处理 ——
//  在那条路径上查库/写库会把整个 30Hz 循环拖死。
//  代价（如实记）：进程被杀时，**最后几分钟的接取/交付会丢**（还没冲库）。
//  缓解：一局结束、断开连接、关服三处都会冲一次（`QuestAuthority.FlushAllAsync`）。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 三条写死的不变式（都有用例/探针盯着）
//  ---------------------------------------------------------------------------
//  ① **落库失败必须把那一批放回脏集** ——
//     否则"接过/交过"这件事**永久丢失**：`SetState` 是"值没变就不标脏"，
//     所以脏集一旦被取走又没写成功，那个键**再也不会被标脏**。
//  ② **单飞**：两次落库同时在飞会乱序 ⇒ 旧状态可能最后落地
//     （表现是"交付过又变回已接取"，而且**不报错**）。
//  ③ **只 upsert，不删除**：`None` **不写库** —— "没有这一行 = 未接取"是不变式，
//     写一行 `state = 0` 进去会把它破坏掉（见 §25 的"没做"表：放弃任务没做）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NBC.Server.Game;      // `IQuestStateStore` / `EQuestStage`（接缝住在 Game，实现住在 Data）

namespace NBC.Server.Data
{
    /// <summary>把任务状态放在内存里、按批写回库的实现。</summary>
    public sealed class CachingQuestStateStore : IQuestStateStore
    {
        /// <summary>读写（换数据库实现时这里不变）。</summary>
        private readonly IQuestStateDao m_dao;

        /// <summary>这个存档属于哪个玩家。</summary>
        private readonly long m_playerId;

        /// <summary>内存里的权威值（任务编号 → 状态）。只放 `!= None` 的。</summary>
        private readonly Dictionary<int, int> m_states = new Dictionary<int, int>();

        /// <summary>还没落库的键（任务编号 → 状态）。</summary>
        private readonly Dictionary<int, int> m_dirty = new Dictionary<int, int>();

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

        /// <summary>造一个属于某个玩家的任务状态缓存。</summary>
        /// <param name="dao">读写实现（不能为 null）。</param>
        /// <param name="playerId">玩家编号（必须 &gt; 0；**负数 = 游客，没有档案，建不了**）。</param>
        public CachingQuestStateStore(IQuestStateDao dao, long playerId)
        {
            if (dao == null)
            {
                throw new ArgumentNullException(nameof(dao), "[CachingQuestStateStore] 读写实现是 null。");
            }

            if (playerId <= 0)
            {
                // 同进度 store / 台账：静默放行会让所有玩家共用一个 0 号存档 ⇒ **串档**。
                // ⚠️ 负数现在是**游客**（SRV-17a）：`quest_state.player_id` 有外键指向
                //    `player_profile`，游客没有档案 ⇒ 这里当场炸是**对的**。
                throw new ArgumentOutOfRangeException(nameof(playerId),
                    "[CachingQuestStateStore] 玩家编号必须 > 0（当前 " + playerId + "）。" +
                    "负数 = 游客：游客没有玩家档案，任务状态无处可挂。");
            }

            m_dao = dao;
            m_playerId = playerId;
        }

        /// <summary>这个存档属于谁。</summary>
        public long PlayerId
        {
            get { return m_playerId; }
        }

        /// <summary>读一个任务的状态；没记录过时是 `None`（**不是异常**）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>状态。</returns>
        public EQuestStage GetState(int questId)
        {
            lock (m_gate)
            {
                int value;

                return m_states.TryGetValue(questId, out value) ? (EQuestStage)value : EQuestStage.None;
            }
        }

        /// <summary>
        /// 写一个任务的状态（**只改内存**，标脏等 `FlushAsync`）。
        /// <para>⚠️ 传 `None` 会**清掉内存里的记录**，但**不会**给它标脏 ——
        /// 见文件头 ③："没有行 = 未接取"。</para>
        /// </summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="state">状态。</param>
        public void SetState(int questId, EQuestStage state)
        {
            int value = (int)state;

            lock (m_gate)
            {
                int existing;

                if (m_states.TryGetValue(questId, out existing) && existing == value)
                {
                    return;     // 值没变 ⇒ 不标脏（免得每帧重复登记也写库）
                }

                if (value == (int)EQuestStage.None)
                {
                    m_states.Remove(questId);
                    return;
                }

                m_states[questId] = value;
                m_dirty[questId] = value;   // 绝对值 ⇒ 重复标脏结果一样
            }
        }

        /// <summary>还有几条脏数据没落库。</summary>
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

        /// <summary>从库里把这个玩家的任务状态读进内存（**登录时调一次**）。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>读回来几条。</returns>
        public async Task<int> LoadAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            EnsureNotDisposed();

            IReadOnlyList<QuestStateRow> rows =
                await m_dao.LoadByPlayerAsync(m_playerId, cancellationToken).ConfigureAwait(false);

            lock (m_gate)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    QuestStateRow row = rows[i];

                    // ⚠️ **不要覆盖"正在飞"的本地改动**：读回来的是**上次冲库前**的快照，
                    //    如果这中间玩家已经接过/交过（脏集里有它），以**内存**为准。
                    if (m_dirty.ContainsKey(row.quest_id))
                    {
                        continue;
                    }

                    m_states[row.quest_id] = row.state;
                }
            }

            return rows.Count;
        }

        /// <summary>把脏集写回库（**单飞**：已有一次在飞就直接返回 0）。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>影响了几行。</returns>
        public async Task<int> FlushAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            EnsureNotDisposed();

            if (Interlocked.CompareExchange(ref m_flushInFlight, 1, 0) != 0)
            {
                return 0;
            }

            List<QuestStateRow>? batch = null;

            try
            {
                batch = TakeDirtyBatch();

                if (batch.Count == 0)
                {
                    return 0;
                }

                int written = await m_dao.UpsertBatchAsync(m_playerId, batch, cancellationToken).ConfigureAwait(false);

                m_flushCount++;
                m_flushedRowCount += batch.Count;
                return written;
            }
            catch
            {
                // 见文件头 ①：失败必须放回，否则这件事**永久丢失**
                RestoreDirty(batch);
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref m_flushInFlight, 0);
            }
        }

        /// <summary>一句人话（落库统计）。</summary>
        /// <returns>例：`任务状态：写 1 次/1 行、脏 0`。</returns>
        public string DescribeFlushStats()
        {
            lock (m_gate)
            {
                return "任务状态：写 " + m_flushCount + " 次/" + m_flushedRowCount + " 行、脏 " + m_dirty.Count;
            }
        }

        /// <summary>收尾（**不自动落库**，同其它 store）。</summary>
        public void Dispose()
        {
            m_disposed = true;
        }

        /// <summary>把一批行**无条件放回**脏集（落库失败时用）。</summary>
        /// <param name="rows">要放回的行。</param>
        private void RestoreDirty(List<QuestStateRow>? rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            lock (m_gate)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    QuestStateRow row = rows[i];

                    // 无条件放回：状态是**绝对值**，重发幂等 ⇒ 不需要"比一下值再决定"
                    m_dirty[row.quest_id] = row.state;
                }
            }
        }

        /// <summary>把脏集拷一份出来并清空（锁里只拷贝）。</summary>
        /// <returns>要落库的行。</returns>
        private List<QuestStateRow> TakeDirtyBatch()
        {
            lock (m_gate)
            {
                if (m_dirty.Count == 0)
                {
                    return new List<QuestStateRow>(0);
                }

                var batch = new List<QuestStateRow>(m_dirty.Count);

                foreach (KeyValuePair<int, int> pair in m_dirty)
                {
                    batch.Add(new QuestStateRow
                    {
                        player_id = m_playerId,
                        quest_id = pair.Key,
                        state = pair.Value
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
                throw new ObjectDisposedException(nameof(CachingQuestStateStore),
                    "[CachingQuestStateStore] 已经 Dispose 了，不能再调用。");
            }
        }
    }
}
