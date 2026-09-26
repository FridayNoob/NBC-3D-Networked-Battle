// ============================================================================
//  IConditionProgressDao —— 「进度怎么读写」的接缝（`MySqlConditionProgressStore` 只认它）
//  项目：3D联网战斗Demo   对应：M4-S3
//
//  ---------------------------------------------------------------------------
//  为什么要有这道缝（不是为了"以后好换数据库"这种空话）
//  ---------------------------------------------------------------------------
//  `MySqlConditionProgressStore` 里**最容易出错的部分不是 SQL**，而是：
//
//      · 脏集怎么合并（同一次落库期间又被改过的键）
//      · 单飞闸门（两次落库乱序落地 = 静默数据回退）
//      · `Remove` 之后要写 0（否则"我明明重置过，怎么又有了"）
//      · 落库失败后哪些键该重新标脏、哪些**绝不能**标脏（会把新值覆盖成旧值）
//
//  这些逻辑**根本不需要数据库就能验**。如果 store 直接依赖具体的 MySQL DAO，
//  那"想验一个脏集合并规则，得先有 MySQL 和密码" —— 和 M1 那几道缝
//  （`IAssetProvider` / `IInputSource` / `IConfigSource`）是**同一条理由**：
//  **把"逻辑"和"IO"分开，逻辑才能在没有 IO 环境的地方被验。**
//
//  ⚠️ 真实 SQL 的正确性**当然**还要真库验 —— 那是 `Server\_db-probe` 里
//     "对真 MySQL 跑一遍建表→写→读回→模拟重启"那几个用例的事（需求 §13.1 第 2 条
//     点名要求"真实的增删改查，不是假数据"）。
//     两者分工：**这道缝验逻辑，探针验 SQL。**
// ============================================================================

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NBC.Server.Data
{
    /// <summary>条件进度的读写（实现可以是 MySQL，也可以是内存假实现）。</summary>
    public interface IConditionProgressDao
    {
        /// <summary>读一个玩家的全部条件进度。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>进度行（没记录过时是**空表**，不是 null）。</returns>
        Task<IReadOnlyList<ConditionProgressRow>> LoadByPlayerAsync(
            long playerId, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>把一批进度写回去（实现应当保证**幂等**）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="rows">脏行。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>真的写了几行。</returns>
        Task<int> UpsertBatchAsync(
            long playerId, IReadOnlyList<ConditionProgressRow> rows,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>删掉一个玩家的全部进度（清档/测试收尾用）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>删了几行。</returns>
        Task<int> DeleteByPlayerAsync(long playerId, CancellationToken cancellationToken = default(CancellationToken));
    }
}
