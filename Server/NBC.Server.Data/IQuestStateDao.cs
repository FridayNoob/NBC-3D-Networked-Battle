// ============================================================================
//  IQuestStateDao —— 「任务状态怎么读写」的接缝（`CachingQuestStateStore` 只认它）
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十五
//
//  ---------------------------------------------------------------------------
//  为什么要有这道缝：与 `IRewardLedgerDao` / `IConditionProgressDao` 同一条理由
//  ---------------------------------------------------------------------------
//  真正容易错的地方不是 SQL，而是：
//      · **落库失败时那一批有没有回到脏集**（否则"接过/交过"会**永久丢失**）
//      · 单飞（两次落库乱序 ⇒ 旧状态覆盖新状态，表现为"交付过又变回已接取"）
//      · **幂等**：`ON DUPLICATE KEY UPDATE` 重发一次不能写出第二行
//  这三条**不需要数据库就能验**。所以：**这道缝验逻辑（假 DAO），探针验 SQL（真库）**。
// ============================================================================

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NBC.Server.Data
{
    /// <summary>`quest_state` 表的一行（Dapper 直接映射）。</summary>
    public sealed class QuestStateRow
    {
        /// <summary>玩家编号。</summary>
        public long player_id { get; set; }

        /// <summary>任务编号（`Quest` 表主键）。</summary>
        public int quest_id { get; set; }

        /// <summary>状态数字（见 `EQuestStage`：1=已接 2=已完成 3=已交付）。</summary>
        public int state { get; set; }
    }

    /// <summary>任务状态的读写（实现可以是 MySQL，也可以是内存假实现）。</summary>
    public interface IQuestStateDao
    {
        /// <summary>读一个玩家的全部任务状态。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>行（没记录过时是**空表**，不是 null）。</returns>
        Task<IReadOnlyList<QuestStateRow>> LoadByPlayerAsync(
            long playerId, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// 把一批状态写回去（实现应当保证**幂等**）。
        /// <para>⚠️ 这里用**绝对值**（`state = VALUES(state)`），不用增量 ——
        /// 于是"重发一次"结果相同（重试安全）。</para>
        /// </summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="rows">要写的行。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>影响的行数。</returns>
        Task<int> UpsertBatchAsync(
            long playerId, IReadOnlyList<QuestStateRow> rows,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>删掉一个玩家的全部任务状态（清档/测试收尾用）。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>删了几行。</returns>
        Task<int> DeleteByPlayerAsync(long playerId, CancellationToken cancellationToken = default(CancellationToken));
    }
}
