// ============================================================================
//  PlayerProfileDao —— 玩家档案的读取（目前只有一件事：**把 id 列表读出来**）
//  项目：3D联网战斗Demo   对应：SRV-06 的最小可用版、`Docs\27` §14.2
//
//  ---------------------------------------------------------------------------
//  为什么只有"读 id"这一个方法
//  ---------------------------------------------------------------------------
//  `PlayerProfileSlots` 需要的**只是"有哪些玩家档案"**（一次查询，启动时跑一次），
//  之后握手就全在内存里做 —— 这样握手**不需要数据库**（它跑在网络泵里，
//  不该为了一次分配去等一次 IO）。
//
//  ⚠️ 那"档案被新建/删除"怎么办：**重启服务端即可**。
//     这是**刻意的取舍**，因为本项目还没有注册流程（SRV-06 只做到绑定，没做注册）。
//     等注册做完，这里要么加一个"刷新"，要么改成按需查 —— 到那时再说，
//     现在多加一套缓存失效机制只是**没人用的复杂度**。
// ============================================================================

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;

namespace NBC.Server.Data
{
    /// <summary>玩家档案的读取。</summary>
    public sealed class PlayerProfileDao
    {
        /// <summary>连接来源。</summary>
        private readonly DbConnectionFactory m_factory;

        /// <summary>命令超时（秒）。</summary>
        private readonly int m_commandTimeoutSeconds;

        /// <summary>造一个 DAO。</summary>
        /// <param name="factory">连接工厂（不能为 null）。</param>
        public PlayerProfileDao(DbConnectionFactory factory)
        {
            if (factory == null)
            {
                throw new System.ArgumentNullException(nameof(factory), "[PlayerProfileDao] 连接工厂是 null。");
            }

            m_factory = factory;
            m_commandTimeoutSeconds = factory.Options.CommandTimeoutSeconds;
        }

        /// <summary>把全部玩家档案 id 读出来（升序）。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>玩家编号（没档案时是**空表**，不是 null）。</returns>
        public async Task<IReadOnlyList<long>> LoadIdsAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = "SELECT `player_id` FROM `player_profile` ORDER BY `player_id`";

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                IEnumerable<long> ids = await connection.QueryAsync<long>(
                    new CommandDefinition(sql, commandTimeout: m_commandTimeoutSeconds,
                                          cancellationToken: cancellationToken)).ConfigureAwait(false);

                return new List<long>(ids);
            }
        }
    }
}
