// ============================================================================
//  LoginAuditWriter —— 登录审计的"排队 + 后台写"（**握手里绝不等它**）
//  项目：3D联网战斗Demo   对应：M4-S3 / SRV-06，见 `Docs\27` §十九
//
//  ---------------------------------------------------------------------------
//  它和 `BattleRecordWriter` 是**同一套形状**（这不是巧合，是刻意复用）
//  ---------------------------------------------------------------------------
//  两个场景的约束一模一样：
//      ① 调用方在**主循环线程**上（那边是 Tick，这边是网络泵的握手）
//         ⇒ 只许入队，写库在别的线程
//      ② 库连不上**不能让服务端倒**（DB-10）
//         ⇒ 失败只计数 + 报一次，绝不往上抛（上面是消息泵，抛出去会踢连接）
//      ③ 同一种失败**不要刷屏**
//         ⇒ "同一种失败只说一次，恢复后再说一次"
//
//  ⚠️ 复制形状而不是复制代码：两个写入器的**载荷**（战绩草稿 vs 一个 account_id）
//     和**失败语义**都不同，硬抽一个泛型基类只会让两边都变难读。
//     "看起来像"不等于"应该共用"—— 判断依据是**将来会不会一起改**。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 它只记"谁登录了"，**不记密码、不记摘要**
//  ---------------------------------------------------------------------------
//  入队的是 `account_id`（一个数字）。摘要从头到尾没有离开过 `IAccountStore.Login`
//  的栈帧 —— 这是"敏感信息不进日志"这条规则的**结构性**保证，
//  而不是"我记得别写进去"。
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NBC.Server.Data
{
    /// <summary>登录审计的排队与后台写。</summary>
    public sealed class LoginAuditWriter : IDisposable
    {
        /// <summary>真正的写库实现（**null = 这个服务端没接数据库**）。</summary>
        private readonly AccountDao? m_dao;

        /// <summary>待写的账号（先进先出）。</summary>
        private readonly ConcurrentQueue<long> m_queue = new ConcurrentQueue<long>();

        /// <summary>当前有没有一次排空在跑（单飞）。</summary>
        private int m_draining;

        /// <summary>已经上报过当前这种失败没有（避免刷屏）。</summary>
        private int m_reportedFailure;

        /// <summary>累计入队几次。</summary>
        private int m_submitted;

        /// <summary>累计写成功几次。</summary>
        private int m_written;

        /// <summary>累计写失败几次。</summary>
        private int m_failed;

        /// <summary>累计遇到"账号不在库里"几次（`UPDATE` 影响 0 行）。</summary>
        private int m_missingAccount;

        /// <summary>Dispose 过没有。</summary>
        private bool m_disposed;

        /// <summary>
        /// 造一个写入器。
        /// </summary>
        /// <param name="dao">写库实现；null = 没接数据库（降级：只计数、报一次）。</param>
        public LoginAuditWriter(AccountDao? dao)
        {
            m_dao = dao;
        }

        /// <summary>值得记一句的事情。</summary>
        public event Action<string>? Note;

        /// <summary>没接数据库（降级模式）。</summary>
        public bool Disabled
        {
            get { return m_dao == null; }
        }

        /// <summary>累计入队几次。</summary>
        public int Submitted
        {
            get { return Volatile.Read(ref m_submitted); }
        }

        /// <summary>累计写成功几次。</summary>
        public int Written
        {
            get { return Volatile.Read(ref m_written); }
        }

        /// <summary>累计写失败几次。</summary>
        public int Failed
        {
            get { return Volatile.Read(ref m_failed); }
        }

        /// <summary>排队中还有几条（收尾时看它是不是 0）。</summary>
        public int PendingCount
        {
            get { return m_queue.Count; }
        }

        /// <summary>一句人话（统计行用）。</summary>
        /// <returns>例：`登录审计：入队 2、成功 2、失败 0`。</returns>
        public string DescribeStats()
        {
            if (Disabled)
            {
                return "登录审计：**未接数据库**（降级：登录照常，只是不记 last_login_at）";
            }

            string text = "登录审计：入队 " + Submitted + "、成功 " + Written + "、失败 " + Failed;

            int missing = Volatile.Read(ref m_missingAccount);

            if (missing > 0)
            {
                text += "；⚠️ 其中 " + missing + " 次" + "找不到账号（UPDATE 影响 0 行）";
            }

            return text;
        }

        /// <summary>
        /// 记一次登录（**从消息泵线程调用，绝不阻塞、绝不抛**）。
        /// </summary>
        /// <param name="accountId">账号编号。</param>
        public void Submit(long accountId)
        {
            if (accountId <= 0 || m_disposed)
            {
                return;
            }

            Interlocked.Increment(ref m_submitted);
            m_queue.Enqueue(accountId);

            if (Disabled)
            {
                if (Interlocked.Exchange(ref m_reportedFailure, 1) == 0)
                {
                    Note?.Invoke(
                        "登录审计**不落库**：这个服务端没接数据库。登录本身照常（它只查内存）。");
                }

                return;
            }

            KickDrain();
        }

        /// <summary>等队列排空（优雅关闭用，SRV-17）。</summary>
        /// <param name="timeoutMs">最多等多少毫秒。</param>
        /// <returns>排空返回 true；超时返回 false。</returns>
        public async Task<bool> FlushAsync(int timeoutMs = 3000)
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

        /// <summary>收尾（不自动排空）。</summary>
        public void Dispose()
        {
            m_disposed = true;
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>踢一次后台排空（单飞）。</summary>
        private void KickDrain()
        {
            if (Interlocked.CompareExchange(ref m_draining, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(DrainAsync);
        }

        /// <summary>把队列里的都写掉。</summary>
        private async Task DrainAsync()
        {
            try
            {
                while (true)
                {
                    long accountId;

                    if (!m_queue.TryDequeue(out accountId))
                    {
                        return;
                    }

                    await WriteOneAsync(accountId).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref m_draining, 0);
            }
        }

        /// <summary>写一条（失败只记账 + 报一次，绝不抛）。</summary>
        /// <param name="accountId">账号编号。</param>
        private async Task WriteOneAsync(long accountId)
        {
            if (m_dao == null)
            {
                return;
            }

            try
            {
                int affected = await m_dao.TouchLastLoginAsync(accountId).ConfigureAwait(false);

                Interlocked.Increment(ref m_written);
                Interlocked.Exchange(ref m_reportedFailure, 0);

                if (affected == 0)
                {
                    // ⚠️ **不能当成功**：内存里明明有这个账号，库里却 UPDATE 不到 ⇒ 数据不一致
                    Interlocked.Increment(ref m_missingAccount);

                    Note?.Invoke(
                        "⚠️ 登录审计：`account` 表里 UPDATE 不到 account_id=" + accountId +
                        "（影响 0 行）。内存里有、库里没有 = 数据不一致，请查库。");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref m_failed);

                if (Interlocked.Exchange(ref m_reportedFailure, 1) == 0)
                {
                    Note?.Invoke(
                        "❌ 登录审计落库失败（**服务端继续跑**，DB-10 的降级）：" + ex.Message + "\n" +
                        "    后续同类失败不再重复刷屏；恢复成功一次之后才会再报。");
                }
            }
        }
    }
}
