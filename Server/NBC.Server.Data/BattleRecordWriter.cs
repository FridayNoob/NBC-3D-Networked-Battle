// ============================================================================
//  BattleRecordWriter —— 战绩落库的"排队 + 后台写"（**主循环绝不等它**）
//  项目：3D联网战斗Demo   对应：需求文档 SRV-13、DB-04、DB-10、SRV-17
//
//  ---------------------------------------------------------------------------
//  它解决的三个具体问题
//  ---------------------------------------------------------------------------
//  ① **主循环不许阻塞**（DB-04）
//     `RoomBattleService` 在 Tick 线程上触发 `BattleFinished`。那里直接 `await` 一次
//     数据库往返 = **一局结束的瞬间卡住整个服务端**（所有房间一起卡）。
//     ⇒ 本类**只入队**，写库在别的线程上。
//
//  ② **连不上库不能让服务端倒**（DB-10）
//     需求原文：**"数据库不可用时服务端仍能启动，只让存档功能报错"**。
//     ⇒ 写失败只**计数 + 报一次**，绝不往上抛（上面是 Tick 循环）。
//
//  ③ **同一个错误不要刷屏**
//     库挂了之后每一局都失败 ⇒ 每局一条错误日志会把日志文件冲爆，反而看不见别的问题。
//     ⇒ "同一种失败只说一次，恢复后再说一次"（下面的 `_reportedFailure`）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么是"入队 + 单飞排空"，而不是"每局 Task.Run 一个"
//  ---------------------------------------------------------------------------
//  · 每局 `Task.Run` ⇒ 同一时刻可能有好几条写并发跑，事务之间抢连接池；
//    而且**完成顺序不可控**（`record_id` 的先后就不再对应开打的先后）。
//  · 单飞排空 ⇒ 串行写，顺序 = 入队顺序。**代价**是排队，但一局一次、每次几十毫秒，
//    队列不可能积压到需要并发的程度。**先选简单的正确，再谈吞吐。**
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NBC.Server.Game;

namespace NBC.Server.Data
{
    /// <summary>战绩落库的排队与后台写。</summary>
    public sealed class BattleRecordWriter : IDisposable
    {
        /// <summary>真正的写库实现（**null = 这个服务端没接数据库**，见 `Disabled`）。</summary>
        private readonly BattleRecordDao? m_dao;

        /// <summary>待写的局（先进先出）。</summary>
        private readonly ConcurrentQueue<BattleRecordDraft> m_queue = new ConcurrentQueue<BattleRecordDraft>();

        /// <summary>当前有没有一次排空在跑（**单飞**，见文件头）。</summary>
        private int m_draining;

        /// <summary>已经上报过当前这种失败没有（避免刷屏，见文件头 ③）。</summary>
        private int m_reportedFailure;

        /// <summary>累计交上来几局。</summary>
        private int m_submitted;

        /// <summary>累计写成功几局。</summary>
        private int m_written;

        /// <summary>累计写失败几局。</summary>
        private int m_failed;

        /// <summary>累计被跳过的"游客不记明细"数（**设计如此**，见 `BattleRecordDao` 文件头）。</summary>
        private int m_skippedGuests;

        /// <summary>累计被跳过的"有正数 id 却查不到档案"玩家数（⚠️ **数据问题**）。</summary>
        private int m_skippedUnknownPlayers;

        /// <summary>Dispose 过没有。</summary>
        private bool m_disposed;

        /// <summary>
        /// 造一个写入器。
        /// <para>⚠️ `dao` 传 null 表示**这个服务端的数据库没配好**（DB-10 的降级）——
        /// 那时 `Submit` 只计数、不写库，而且**只在第一局**报一次。这样服务端照样能跑完整局。</para>
        /// </summary>
        /// <param name="dao">写库实现；null = 没接数据库。</param>
        public BattleRecordWriter(BattleRecordDao? dao)
        {
            m_dao = dao;
        }

        /// <summary>值得记一句的事情（落库结果、失败原因）。</summary>
        public event Action<string>? Note;

        /// <summary>没接数据库（降级模式）。</summary>
        public bool Disabled
        {
            get { return m_dao == null; }
        }

        /// <summary>累计交上来几局。</summary>
        public int Submitted
        {
            get { return Volatile.Read(ref m_submitted); }
        }

        /// <summary>累计写成功几局。</summary>
        public int Written
        {
            get { return Volatile.Read(ref m_written); }
        }

        /// <summary>累计写失败几局。</summary>
        public int Failed
        {
            get { return Volatile.Read(ref m_failed); }
        }

        /// <summary>排队中还有几局（收尾时看它是不是 0）。</summary>
        public int PendingCount
        {
            get { return m_queue.Count; }
        }

        /// <summary>一句人话（启动横幅/统计行用）。</summary>
        /// <returns>例：`战绩落库：提交 3、成功 3、失败 0（无档案跳过 0）`。</returns>
        public string DescribeStats()
        {
            if (Disabled)
            {
                return "战绩落库：**未接数据库**（降级：服务端照常跑，战绩不落库）";
            }

            return "战绩落库：提交 " + Submitted + "、成功 " + Written + "、失败 " + Failed +
                   "（游客不记明细 " + Volatile.Read(ref m_skippedGuests) + " 人、" +
                   "⚠️有 id 无档案 " + Volatile.Read(ref m_skippedUnknownPlayers) + " 人）";
        }

        /// <summary>
        /// 交一局战绩（**从 Tick 线程调用，绝不阻塞、绝不抛**）。
        /// <para>⚠️ 它内部只做两件事：入队、必要时踢一次后台排空。
        /// 这一条是本类存在的全部理由 —— 别在这里加任何 `await`。</para>
        /// </summary>
        /// <param name="draft">战绩草稿。</param>
        public void Submit(BattleRecordDraft draft)
        {
            if (draft == null || m_disposed)
            {
                return;
            }

            Interlocked.Increment(ref m_submitted);
            m_queue.Enqueue(draft);

            if (Disabled)
            {
                // 降级：不写库，但**要让人知道**（只说一次，见文件头 ③）
                if (Interlocked.Exchange(ref m_reportedFailure, 1) == 0)
                {
                    Note?.Invoke(
                        "⚠️ 战绩**不落库**：这个服务端没接数据库（`NBC_DB_PASSWORD` 没设，或启动时连不上）。\n" +
                        "    服务端照常运行 —— 这是需求 DB-10 要的降级行为。\n" +
                        "    想让战绩进库：设好 `NBC_DB_PASSWORD` 后重启服务端。");
                }

                return;
            }

            KickDrain();
        }

        /// <summary>
        /// 等队列排空（**优雅关闭用**，SRV-17）。
        /// <para>⚠️ 带超时：关服务端不该因为"库连不上"而卡住不退出。</para>
        /// </summary>
        /// <param name="timeoutMs">最多等多少毫秒。</param>
        /// <returns>排空返回 true；超时返回 false（还有没写完的）。</returns>
        public async Task<bool> FlushAsync(int timeoutMs = 5000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (m_queue.Count > 0 || Volatile.Read(ref m_draining) != 0)
            {
                if (DateTime.UtcNow > deadline)
                {
                    return false;
                }

                await Task.Delay(20).ConfigureAwait(false);
            }

            return true;
        }

        /// <summary>收尾（**不自动排空** —— 排空由调用方显式 `FlushAsync`，见那个方法的说明）。</summary>
        public void Dispose()
        {
            m_disposed = true;
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>踢一次后台排空（已经在跑就直接返回 —— **单飞**）。</summary>
        private void KickDrain()
        {
            if (Interlocked.CompareExchange(ref m_draining, 1, 0) != 0)
            {
                return;     // 已经有一次在跑，它会顺手把新入队的也写掉
            }

            // ⚠️ fire-and-forget 是**故意**的：调用方在 Tick 线程上，不能等。
            //    异常在 `DrainAsync` 内部全部吃掉（只计数 + 报一次），
            //    所以这里不会出现"未观察的 Task 异常"。
            _ = Task.Run(DrainAsync);
        }

        /// <summary>把队列里的都写掉。</summary>
        private async Task DrainAsync()
        {
            try
            {
                while (true)
                {
                    BattleRecordDraft draft;

                    if (!m_queue.TryDequeue(out draft))
                    {
                        return;     // 排空了
                    }

                    await WriteOneAsync(draft).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref m_draining, 0);
            }
        }

        /// <summary>写一局（**失败只记账 + 报一次**，绝不抛）。</summary>
        /// <param name="draft">草稿。</param>
        private async Task WriteOneAsync(BattleRecordDraft draft)
        {
            if (m_dao == null)
            {
                return;
            }

            try
            {
                BattleRecordWriteResult result = await m_dao.SaveAsync(draft).ConfigureAwait(false);

                Interlocked.Increment(ref m_written);
                Interlocked.Add(ref m_skippedGuests, result.SkippedGuests);
                Interlocked.Add(ref m_skippedUnknownPlayers, result.SkippedUnknownPlayers);

                // 失败过一次、现在好了 ⇒ 允许下次失败再报一次（见文件头 ③）
                Interlocked.Exchange(ref m_reportedFailure, 0);

                Note?.Invoke("战绩已落库：" + result);

                if (result.SkippedGuests > 0)
                {
                    // ✅ **这是设计，不是问题**：游客没有档案 ⇒ 没人可以记。
                    //    ⚠️ 它必须**说得像正常**，否则"游客不记明细"会把真正的故障淹掉。
                    Note?.Invoke(
                        "游客 " + result.SkippedGuests + " 人**按设计不记明细**（`battle_record` 那条汇总照写）：\n" +
                        "    游客的 `player_id` 是**负数**（没有账号 ⇒ 没有 `player_profile`），" +
                        "而明细表有外键指向档案。\n" +
                        "    想让战绩进库就**用账号登录**（M4-S3，见 `Docs\\27` §十九）。");
                }

                if (result.SkippedUnknownPlayers > 0)
                {
                    // ⚠️ **这一条要显眼**：正数 id 却在 `player_profile` 里查不到 = 数据不一致
                    Note?.Invoke(
                        "❌ 有 " + result.SkippedUnknownPlayers + " 个玩家的战绩**没写进库**：" +
                        "他们拿着**正数** player_id，但 `player_profile` 里没有对应档案。\n" +
                        "    这不是游客（游客是负数）—— 这是**数据不一致**：请查 `player_profile` 与账号绑定。");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref m_failed);

                // ⚠️ 只说一次（见文件头 ③）：库挂了之后每局都失败，刷屏会把别的问题淹掉
                if (Interlocked.Exchange(ref m_reportedFailure, 1) == 0)
                {
                    Note?.Invoke(
                        "❌ 战绩落库失败（**服务端继续跑**，这是 DB-10 要的降级）：" + ex.Message + "\n" +
                        "    后续同类失败**不再重复刷屏**；恢复成功一次之后才会再报。");
                }
            }
        }
    }
}
