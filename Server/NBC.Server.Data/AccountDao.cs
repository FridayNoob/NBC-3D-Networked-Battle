// ============================================================================
//  AccountDao —— 账号表的**写**（目前只有一件事：记一次登录时间）
//  项目：3D联网战斗Demo   对应：M4-S3 / SRV-06「真正的登录」，见 `Docs\27` §十九
//
//  ---------------------------------------------------------------------------
//  为什么只有"写一个时间戳"这一件事
//  ---------------------------------------------------------------------------
//  登录的**读**全在内存里（`AccountDirectory`，理由见那个文件的文件头），
//  真正需要落库的只有"这个账号最后一次登录是什么时候"这个**审计信息**。
//
//  价值不在于这个字段本身，而在于：**它能在库里留下"登录确实发生过"的证据**。
//  否则"登录成功了"这句话只有服务端日志能证明 —— 而日志是可以看错的
//  （本项目已经因为"日志大小 0 B"误报过一次，见 `Docs\00` v1.9.26）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么用 `NOW()` 而不是从 C# 传时间
//  ---------------------------------------------------------------------------
//  库里的 `battle_record.created_at` 用的是 `CURRENT_TIMESTAMP`（**数据库服务器的时钟**）。
//  如果这里从 C# 传 `DateTime.UtcNow`，同一张库上就会有两种时区的值：
//      battle_record.created_at = 本地时间（10:46）
//      account.last_login_at    = UTC（02:46）
//  人工对时间线时**一定会看错**（而且不会报错）。
//  ⇒ 统一让**数据库自己打时间**：`NOW()`。
// ============================================================================

using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;

namespace NBC.Server.Data
{
    /// <summary>账号表的写。</summary>
    public sealed class AccountDao
    {
        /// <summary>连接来源。</summary>
        private readonly DbConnectionFactory m_factory;

        /// <summary>命令超时（秒）。</summary>
        private readonly int m_commandTimeoutSeconds;

        /// <summary>造一个 DAO。</summary>
        /// <param name="factory">连接工厂（不能为 null）。</param>
        public AccountDao(DbConnectionFactory factory)
        {
            if (factory == null)
            {
                throw new System.ArgumentNullException(nameof(factory), "[AccountDao] 连接工厂是 null。");
            }

            m_factory = factory;
            m_commandTimeoutSeconds = factory.Options.CommandTimeoutSeconds;
        }

        /// <summary>
        /// 记一次登录时间。
        /// </summary>
        /// <param name="accountId">账号编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>影响的行数（**0 表示这个账号不在库里** —— 上层要当失败报出来）。</returns>
        public async Task<int> TouchLastLoginAsync(
            long accountId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = "UPDATE `account` SET `last_login_at` = NOW() WHERE `account_id` = @accountId";

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { accountId }, commandTimeout: m_commandTimeoutSeconds,
                                          cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }
    }
}
