// ============================================================================
//  ConditionProgressDao —— 条件进度表的读写（Dapper + 参数化 SQL）
//  项目：3D联网战斗Demo   对应：需求文档 §13.4、DB-03、DB-04
//
//  ---------------------------------------------------------------------------
//  只有两条 SQL，但每条都有理由
//  ---------------------------------------------------------------------------
//      ① `LoadByPlayer`  ：一个玩家的**全部**进度一次读回来
//      ② `UpsertBatch`   ：把一批脏行**一条语句**写回去
//
//  ⚠️ 为什么是"读全部"而不是"按需读一条"：
//     一个玩家的进度行 = 他涉及的**所有条件**（这个项目几十条，最多上百条）。
//     进度是**跨局累计**的，所以进副本时就得知道全部（否则"这个成就早就该解锁"会被漏掉）。
//     一次读回来之后，`GetProgress` 就是**纯内存操作** —— 这正是 DB-03
//     「不在 Tick 主循环里做同步阻塞查询」能成立的前提。
//
//  ⚠️ 为什么是"一条语句写一批"，不是"逐行 UPDATE"：
//     N 条进度就是 N 次往返；而且**部分成功**会留下"一半新一半旧"的中间态。
//     一条 `INSERT ... ON DUPLICATE KEY UPDATE` + 多值 VALUES 是**一次往返、一次事务**。
//
//  ⚠️ 为什么用 `ON DUPLICATE KEY UPDATE` 而不是先查后插：
//     它是**幂等**的 —— 重发一次同一批脏数据结果完全相同（见 `MySqlConditionProgressStore`
//     的"写的是绝对值"那条）。先查后插会把"查"和"插"之间的竞态留给自己。
// ============================================================================

using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;

namespace NBC.Server.Data
{
    /// <summary>条件进度表的一行（Dapper 直接映射）。</summary>
    public sealed class ConditionProgressRow
    {
        /// <summary>玩家编号（`player_profile.player_id`）。</summary>
        public long player_id { get; set; }

        /// <summary>条件编号（`Config_QuestCondition.id`）。</summary>
        public int condition_key { get; set; }

        /// <summary>已累计数量（**绝对值**）。</summary>
        public int progress { get; set; }
    }

    /// <summary>条件进度表的读写。</summary>
    public sealed class ConditionProgressDao : IConditionProgressDao
    {
        /// <summary>连接来源。</summary>
        private readonly DbConnectionFactory m_factory;

        /// <summary>命令超时（秒）—— 从配置来，不写死。</summary>
        private readonly int m_commandTimeoutSeconds;

        /// <summary>造一个 DAO。</summary>
        /// <param name="factory">连接工厂（不能为 null）。</param>
        public ConditionProgressDao(DbConnectionFactory factory)
        {
            if (factory == null)
            {
                throw new System.ArgumentNullException(nameof(factory), "[ConditionProgressDao] 连接工厂是 null。");
            }

            m_factory = factory;
            m_commandTimeoutSeconds = factory.Options.CommandTimeoutSeconds;
        }

        /// <summary>读一个玩家的全部条件进度。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>进度行（没记录过时是**空表**，不是 null）。</returns>
        public async Task<IReadOnlyList<ConditionProgressRow>> LoadByPlayerAsync(
            long playerId, CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql =
                "SELECT `player_id`, `condition_key`, `progress` " +
                "FROM `condition_progress` WHERE `player_id` = @playerId";

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                IEnumerable<ConditionProgressRow> rows = await connection.QueryAsync<ConditionProgressRow>(
                    new CommandDefinition(sql, new { playerId }, commandTimeout: m_commandTimeoutSeconds,
                                          cancellationToken: cancellationToken)).ConfigureAwait(false);

                return new List<ConditionProgressRow>(rows);
            }
        }

        /// <summary>把一批进度写回去（**一条语句、幂等**）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="rows">脏行（条件编号 → 绝对值）。空表时**直接返回**，不发 SQL。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>真的写了几行。</returns>
        public async Task<int> UpsertBatchAsync(
            long playerId, IReadOnlyList<ConditionProgressRow> rows,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (rows == null || rows.Count == 0)
            {
                return 0;   // 没脏数据就别开连接（空转的连接也是开销，还会掩盖"其实没变"这件事）
            }

            var sql = new StringBuilder();
            sql.Append("INSERT INTO `condition_progress` (`player_id`, `condition_key`, `progress`) VALUES ");
            var parameters = new DynamicParameters();
            parameters.Add("playerId", playerId);

            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                // ⚠️ 参数化（不是拼字符串）：这是**防注入**的基本功，
                //    而且顺带让 MySQL 能**复用执行计划**（同形状的语句只解析一次）。
                string keyName = "k" + i;
                string valueName = "v" + i;

                sql.Append("(@playerId, @").Append(keyName).Append(", @").Append(valueName).Append(')');
                parameters.Add(keyName, rows[i].condition_key);
                parameters.Add(valueName, rows[i].progress);
            }

            // 幂等：同一个 (player_id, condition_key) 再来一次就覆盖成**这个绝对值**
            sql.Append(" ON DUPLICATE KEY UPDATE `progress` = VALUES(`progress`)");

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql.ToString(), parameters, commandTimeout: m_commandTimeoutSeconds,
                                          cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }

        /// <summary>删掉一个玩家的全部进度（**清档/测试收尾用**）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>删了几行。</returns>
        public async Task<int> DeleteByPlayerAsync(long playerId, CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = "DELETE FROM `condition_progress` WHERE `player_id` = @playerId";

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { playerId }, commandTimeout: m_commandTimeoutSeconds,
                                          cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }
    }
}
