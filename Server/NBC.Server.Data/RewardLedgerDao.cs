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

            string sql = BuildInsertSql(playerId, rows, out DynamicParameters parameters);

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                return await connection.ExecuteAsync(
                    new CommandDefinition(sql, parameters, commandTimeout: m_commandTimeoutSeconds,
                                          cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }

        /// <summary>**台账 + 实际发放**：两件事在同一个事务里（要么都成，要么都不成）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="rows">要写的台账行（可以为空表 —— 那时只发钱）。</param>
        /// <param name="expDelta">要加的 exp（0 = 不加）。</param>
        /// <param name="goldDelta">要加的 gold（0 = 不加）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>写下的台账行数。</returns>
        /// <exception cref="ProfileMissingException">有实际发放、但档案不存在（整体回滚）。</exception>
        public async Task<int> InsertBatchAndPayAsync(
            long playerId, IReadOnlyList<RewardGrantedRow> rows,
            int expDelta, int goldDelta,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            bool hasRows = rows != null && rows.Count > 0;
            bool paying = expDelta != 0 || goldDelta != 0;

            if (!hasRows && !paying)
            {
                return 0;
            }

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            using (MySqlTransaction transaction = connection.BeginTransaction())
            {
                int ledgerRows = 0;

                // ⚠️ 这里**必须直接判 null**（不要用上面那个 `hasRows` 布尔量）：
                //    编译器不认"布尔量蕴含非空"，会报 CS8604（本项目的闸门是 0 警告）。
                if (rows != null && rows.Count > 0)
                {
                    string sql = BuildInsertSql(playerId, rows, out DynamicParameters parameters);

                    ledgerRows = await connection.ExecuteAsync(
                        new CommandDefinition(sql, parameters, transaction: transaction,
                                              commandTimeout: m_commandTimeoutSeconds,
                                              cancellationToken: cancellationToken)).ConfigureAwait(false);
                }

                if (paying)
                {
                    // ⚠️ §21.4 那句"`affected_rows` 的语义要单独设计"，说的就是下面这个判断。
                    //    这里写成 `exp = exp + @d`：**匹配到的行一定发生变化**，
                    //    所以 `affected == 0` **只可能**是"没有这个档案" —— 那才是真错误。
                    //    （反面教材：若写成 `SET exp = @d` 而值恰好没变，MySQL 默认语义下 affected 也是 0，
                    //      那时把 0 当错误就会**误报**。判据的形状决定了它能不能当判据。）
                    const string paySql =
                        "UPDATE `player_profile` SET `exp` = `exp` + @expDelta, `gold` = `gold` + @goldDelta " +
                        "WHERE `player_id` = @playerId";

                    int paid = await connection.ExecuteAsync(
                        new CommandDefinition(paySql, new { playerId, expDelta, goldDelta },
                                              transaction: transaction,
                                              commandTimeout: m_commandTimeoutSeconds,
                                              cancellationToken: cancellationToken)).ConfigureAwait(false);

                    if (paid < 1)
                    {
                        throw new ProfileMissingException(
                            "[RewardLedgerDao] 要给玩家 " + playerId + " 发 " + expDelta + " exp / " + goldDelta +
                            " gold，但 `player_profile` 里没有这个玩家 ⇒ **整笔回滚**（台账也不会写进去）。" +
                            "静默跳过会留下『台账说发过、钱没到账』——而台账是单调的，那个玩家永远不会再补发。");
                    }
                }

                transaction.Commit();
                return ledgerRows;
            }
        }

        /// <summary>
        /// 拼那条**幂等**的台账 INSERT —— 两条写入路径（只记台账 / 台账加发放）**共用同一份 SQL**。
        /// <para>⚠️ 一处分实现迟早会漂移：这条 SQL 里 `ON DUPLICATE KEY UPDATE` 是"台账可不可信"的地基，
        /// 两处各写一遍就会出现"一条路幂等、另一条路不幂等"。</para>
        /// </summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="rows">行。</param>
        /// <param name="parameters">拼出来的参数。</param>
        /// <returns>SQL。</returns>
        private static string BuildInsertSql(
            long playerId, IReadOnlyList<RewardGrantedRow> rows, out DynamicParameters parameters)
        {
            var sql = new StringBuilder();
            sql.Append("INSERT INTO `reward_granted` (`player_id`, `owner_kind`, `owner_id`, `reward_id`) VALUES ");

            parameters = new DynamicParameters();
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
            return sql.ToString();
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
