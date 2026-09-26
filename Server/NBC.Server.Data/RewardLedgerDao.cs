// ============================================================================
//  RewardLedgerDao —— 已发奖励台账的读写（Dapper + 参数化 SQL）
//  项目：3D联网战斗Demo   对应：M4-S3、需求文档 §13.4
//
//  ---------------------------------------------------------------------------
//  与 `ConditionProgressDao` 的三处**刻意不同**（每处都有理由）
//  ---------------------------------------------------------------------------
//  ① **写用的是 `ON DUPLICATE KEY UPDATE`，不是 `INSERT IGNORE`**
//     `INSERT IGNORE` 会把**所有**错误降级成警告 —— 包括外键不存在（`player_id` 是假的）！
//     那就成了"发奖记录悄悄没写进去、而且不报错"。
//     `ON DUPLICATE KEY UPDATE reward_id = VALUES(reward_id)` 只容忍**主键冲突**这一种情况，
//     其余错误照常抛出。（这一格的差别很细，但它决定"台账到底可不可信"。）
//
//  ② **没有"批量大小"上限**：一个玩家的成就数是个位数、任务数十级，
//     一次 flush 的行数天然很小。进度表那边同理 —— 所以两边都没有分片逻辑。
//
//  ③ **没有"更新已有行"的语义**：台账是**单调的**（发过就是发过，不会撤销）。
//     所以它没有"值回退"那一类 bug —— 这是它与进度表最本质的区别，
//     也是 `MySqlRewardLedger` 可以比 `CachingConditionProgressStore` 简单得多的原因。
// ============================================================================

using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;

namespace NBC.Server.Data
{
    /// <summary>已发奖励台账的一行（Dapper 直接映射）。</summary>
    public sealed class RewardGrantedRow
    {
        /// <summary>玩家编号。</summary>
        public long player_id { get; set; }

        /// <summary>谁发的（1 = 任务、2 = 成就，与 `ERewardOwnerKind` 一一对应）。</summary>
        public int owner_kind { get; set; }

        /// <summary>发布者编号（任务编号 / 成就编号）。</summary>
        public int owner_id { get; set; }

        /// <summary>奖励编号（留痕/排查用）。</summary>
        public int reward_id { get; set; }
    }

    /// <summary>已发奖励台账的读写。</summary>
    public sealed class RewardLedgerDao : IRewardLedgerDao
    {
        /// <summary>连接来源。</summary>
        private readonly DbConnectionFactory m_factory;

        /// <summary>命令超时（秒）。</summary>
        private readonly int m_commandTimeoutSeconds;

        /// <summary>造一个 DAO。</summary>
        /// <param name="factory">连接工厂（不能为 null）。</param>
        public RewardLedgerDao(DbConnectionFactory factory)
        {
            if (factory == null)
            {
                throw new System.ArgumentNullException(nameof(factory), "[RewardLedgerDao] 连接工厂是 null。");
            }

            m_factory = factory;
            m_commandTimeoutSeconds = factory.Options.CommandTimeoutSeconds;
        }

        /// <summary>读一个玩家的全部台账。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>台账行（没记录过时是**空表**，不是 null）。</returns>
        public async Task<IReadOnlyList<RewardGrantedRow>> LoadByPlayerAsync(
            long playerId, CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql =
                "SELECT `player_id`, `owner_kind`, `owner_id`, `reward_id` " +
                "FROM `reward_granted` WHERE `player_id` = @playerId";

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                IEnumerable<RewardGrantedRow> rows = await connection.QueryAsync<RewardGrantedRow>(
                    new CommandDefinition(sql, new { playerId }, commandTimeout: m_commandTimeoutSeconds,
                                          cancellationToken: cancellationToken)).ConfigureAwait(false);

                return new List<RewardGrantedRow>(rows);
            }
        }

        /// <summary>把一批台账行写回去（**一条语句、幂等**）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="rows">要写的行。空表时**直接返回**，不发 SQL。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>影响的行数。</returns>
        public async Task<int> InsertBatchAsync(
            long playerId, IReadOnlyList<RewardGrantedRow> rows,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (rows == null || rows.Count == 0)
            {
                return 0;
            }

            var sql = new StringBuilder();
            sql.Append("INSERT INTO `reward_granted` (`player_id`, `owner_kind`, `owner_id`, `reward_id`) VALUES ");
            var parameters = new DynamicParameters();
            parameters.Add("playerId", playerId);

            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                string kindName = "k" + i;
                string ownerName = "o" + i;
                string rewardName = "r" + i;

                sql.Append("(@playerId, @").Append(kindName).Append(", @").Append(ownerName)
                   .Append(", @").Append(rewardName).Append(')');

                parameters.Add(kindName, rows[i].owner_kind);
                parameters.Add(ownerName, rows[i].owner_id);
                parameters.Add(rewardName, rows[i].reward_id);
            }

            // ⚠️ 见文件头 ①：只容忍主键冲突，**不用 `INSERT IGNORE`**
            //    （那会把"外键不存在"这类真错误一起吞掉）
            sql.Append(" ON DUPLICATE KEY UPDATE `reward_id` = VALUES(`reward_id`)");

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql.ToString(), parameters, commandTimeout: m_commandTimeoutSeconds,
                                          cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }

        /// <summary>删掉一个玩家的全部台账（**清档/测试收尾用**）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>删了几行。</returns>
        public async Task<int> DeleteByPlayerAsync(long playerId, CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = "DELETE FROM `reward_granted` WHERE `player_id` = @playerId";

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { playerId }, commandTimeout: m_commandTimeoutSeconds,
                                          cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }
    }
}
