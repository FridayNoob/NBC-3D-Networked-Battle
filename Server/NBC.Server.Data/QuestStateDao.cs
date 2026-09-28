// ============================================================================
//  QuestStateDao —— `quest_state` 表的读写（Dapper + 参数化 SQL）
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十五、`Docs\08d`
//
//  ---------------------------------------------------------------------------
//  三处与本项目既有 DAO **刻意一致**的写法（照着抄的，别改）
//  ---------------------------------------------------------------------------
//  ① **写用 `ON DUPLICATE KEY UPDATE state = VALUES(state)`，不是 `INSERT IGNORE`**
//     `INSERT IGNORE` 会把**所有**错误降级成警告 —— 包括外键不存在（`player_id` 是假的）！
//     那就成了"接过任务却没写进去、而且不报错"。
//
//  ② **存绝对值而不是增量** ⇒ 落库**幂等**：重发一次结果相同。
//     状态机尤其需要这条：增量式的"状态 +1"重发一次就会跳两步。
//
//  ③ **不带"删除"的语义进脏集**：撤回/放弃任务这一版**没做**（见 §25 的"没做"表），
//     所以这里只 upsert。将来要做"放弃"，得显式给一个 `Delete` 通路，
//     而不是让 `state = None` 悄悄写一行进去 —— 那会破坏"没有行 = 未接取"这条不变式。
// ============================================================================

using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;

namespace NBC.Server.Data
{
    /// <summary>任务状态的读写。</summary>
    public sealed class QuestStateDao : IQuestStateDao
    {
        /// <summary>连接来源。</summary>
        private readonly DbConnectionFactory m_factory;

        /// <summary>命令超时（秒）。</summary>
        private readonly int m_commandTimeoutSeconds;

        /// <summary>造一个 DAO。</summary>
        /// <param name="factory">连接工厂（不能为 null）。</param>
        public QuestStateDao(DbConnectionFactory factory)
        {
            if (factory == null)
            {
                throw new System.ArgumentNullException(nameof(factory), "[QuestStateDao] 连接工厂是 null。");
            }

            m_factory = factory;
            m_commandTimeoutSeconds = factory.Options.CommandTimeoutSeconds;
        }

        /// <summary>读一个玩家的全部任务状态。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>行（没记录过时是空表）。</returns>
        public async Task<IReadOnlyList<QuestStateRow>> LoadByPlayerAsync(
            long playerId, CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql =
                "SELECT `player_id`, `quest_id`, `state` FROM `quest_state` WHERE `player_id` = @playerId";

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                IEnumerable<QuestStateRow> rows = await connection.QueryAsync<QuestStateRow>(
                    new CommandDefinition(sql, new { playerId }, commandTimeout: m_commandTimeoutSeconds,
                                          cancellationToken: cancellationToken)).ConfigureAwait(false);

                return new List<QuestStateRow>(rows);
            }
        }

        /// <summary>把一批状态写回去（**一条语句、幂等**）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="rows">要写的行。空表时**直接返回**，不发 SQL。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>影响的行数。</returns>
        public async Task<int> UpsertBatchAsync(
            long playerId, IReadOnlyList<QuestStateRow> rows,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (rows == null || rows.Count == 0)
            {
                return 0;
            }

            var sql = new StringBuilder();
            sql.Append("INSERT INTO `quest_state` (`player_id`, `quest_id`, `state`) VALUES ");

            var parameters = new DynamicParameters();
            parameters.Add("playerId", playerId);

            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                string questName = "q" + i;
                string stateName = "s" + i;

                sql.Append("(@playerId, @").Append(questName).Append(", @").Append(stateName).Append(')');

                parameters.Add(questName, rows[i].quest_id);
                parameters.Add(stateName, rows[i].state);
            }

            // ⚠️ 见文件头 ①：只容忍主键冲突（「玩家 + 任务」），其余错误照常抛出
            sql.Append(" ON DUPLICATE KEY UPDATE `state` = VALUES(`state`), " +
                       "`submitted_at` = IF(VALUES(`state`) = 3, NOW(), `submitted_at`)");

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql.ToString(), parameters, commandTimeout: m_commandTimeoutSeconds,
                                          cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }

        /// <summary>删掉一个玩家的全部任务状态（**清档/测试收尾用**）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>删了几行。</returns>
        public async Task<int> DeleteByPlayerAsync(
            long playerId, CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = "DELETE FROM `quest_state` WHERE `player_id` = @playerId";

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { playerId }, commandTimeout: m_commandTimeoutSeconds,
                                          cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }
    }
}
