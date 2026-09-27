// ============================================================================
//  IRewardLedgerDao —— 「台账怎么读写」的接缝（`MySqlRewardLedger` 只认它）
//  项目：3D联网战斗Demo   对应：M4-S3
//
//  ---------------------------------------------------------------------------
//  为什么要有这道缝：与 `IConditionProgressDao` **同一条理由**（见那个文件头）
//  ---------------------------------------------------------------------------
//  台账里真正容易错的地方不是 SQL，而是：
//
//      · **落库失败时那一批有没有回到脏集** —— `MarkGranted` 是"内存里已经有它就不再
//        标脏"，所以脏集一旦被取走又没写成功，那个键**再也不会被标脏**
//        ⇒ "发了奖但台账没落库"**永久丢失** ⇒ 下次重启**再发一遍**（等于台账白装）
//      · 单飞（两次落库乱序）
//      · 单调性（重复标记不产生第二条）
//
//  这三条**不需要数据库就能验**。所以逻辑归 `MySqlRewardLedger`（可假造 DAO 来测）、
//  SQL 归 `RewardLedgerDao`（由 `_db-probe` 对真库验）。
//  ⇒ **这道缝验逻辑，探针验 SQL。**
// ============================================================================

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NBC.Server.Data
{
    /// <summary>已发奖励台账的读写（实现可以是 MySQL，也可以是内存假实现）。</summary>
    public interface IRewardLedgerDao
    {
        /// <summary>读一个玩家的全部台账。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>台账行（没记录过时是**空表**，不是 null）。</returns>
        Task<IReadOnlyList<RewardGrantedRow>> LoadByPlayerAsync(
            long playerId, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>把一批台账行写回去（实现应当保证**幂等**）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="rows">要写的行。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>影响的行数。</returns>
        Task<int> InsertBatchAsync(
            long playerId, IReadOnlyList<RewardGrantedRow> rows,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// **台账 + 实际发放**：把这一批台账行写下，并把这笔奖励（exp/gold）加到 `player_profile`，
        /// 两件事在**同一个事务**里（要么都成，要么都不成）。
        /// <para>
        /// ⚠️ 为什么必须同一事务（这是本方法的全部理由）：
        /// 分两次写就会出现「**台账记了"发过"、金币却没加**」——
        /// 而且台账是**单调**的（发过就是发过、不会撤销），所以那个玩家**永远拿不到这笔钱**，
        /// 也永远不会再补发。这是**静默的数据丢失**，只有对账才看得出。
        /// </para>
        /// </summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="rows">要写的台账行。</param>
        /// <param name="expDelta">这笔要加的 exp（0 = 不加）。</param>
        /// <param name="goldDelta">这笔要加的 gold（0 = 不加）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>写下的台账行数。</returns>
        /// <exception cref="ProfileMissingException">
        /// 有实际发放、但 `player_profile` 里没有这个 `player_id` ⇒ **整体回滚**。
        /// 静默跳过会变成"台账说发过、钱没到账"，正是本方法要防的那件事。
        /// </exception>
        Task<int> InsertBatchAndPayAsync(
            long playerId, IReadOnlyList<RewardGrantedRow> rows,
            int expDelta, int goldDelta,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>删掉一个玩家的全部台账（清档/测试收尾用）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>删了几行。</returns>
        Task<int> DeleteByPlayerAsync(long playerId, CancellationToken cancellationToken = default(CancellationToken));
    }
}
