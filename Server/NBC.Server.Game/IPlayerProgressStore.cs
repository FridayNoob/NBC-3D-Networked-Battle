// ============================================================================
//  IPlayerProgressStore / IPlayerRewardLedger —— 服务端权威要的**两个接缝**
//  项目：3D联网战斗Demo   对应：M4-S3 服务端权威化，见 `Docs\27` §二十一
//
//  ---------------------------------------------------------------------------
//  为什么要在共享层的接口之上再包一层
//  ---------------------------------------------------------------------------
//  共享层（`NBC.Shared.Condition` / `NBC.Shared.Reward`）里的两个接口是**同步的、纯内存的**：
//      `IConditionProgressStore` : Get / Set / Remove
//      `IRewardLedger`          : HasGranted / MarkGranted
//  ⚠️ 那是**故意**的 —— 它们跑在主循环与 Tick 里，接口说话的单位就不能是 `Task`。
//
//  但"什么时候把内存里的脏数据写回库"（`LoadAsync` / `FlushAsync`）**只有实现知道**，
//  它不在共享接口里。而服务端权威（`AchievementAuthority`）**恰恰要管这件事**：
//      · 登录/进房时 `LoadAsync`（把上一局的进度读回来）
//      · 一局结束 / 断开 / 关服时 `FlushAsync`（把这一局的进度写回去）
//
//  ⇒ 所以在**使用它的那一层**（Game）声明这两个"带 IO 的版本"。
//     实现仍然住在 `NBC.Server.Data`（`CachingConditionProgressStore` / `MySqlRewardLedger`
//     本来就已经有这几个方法，加上接口名是**一行**的事）。
//
//  ⚠️ 为什么不把这些方法直接加到共享接口上：那会让**客户端**也必须实现
//     `LoadAsync`/`FlushAsync` —— 而客户端那份是内存实现，它没有"库"可读可写。
//     接口只该声明"调用方真正需要的能力"（同族教训：`M4-B` 的接缝放错程序集）。
// ============================================================================

using System.Threading;
using System.Threading.Tasks;
using NBC.Shared.Condition;
using NBC.Shared.Reward;

namespace NBC.Server.Game
{
    /// <summary>玩家进度存放处（共享接口 + **带 IO 的三个方法**）。</summary>
    public interface IPlayerProgressStore : IConditionProgressStore, System.IDisposable
    {
        /// <summary>这个存放处属于哪个玩家。</summary>
        long PlayerId { get; }

        /// <summary>还有几条脏数据没落库（收尾时看它是不是 0）。</summary>
        int DirtyCount { get; }

        /// <summary>把库里的进度读进来（**登录/进房时调一次**，必须在 `Register` 之前）。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>读回来几条。</returns>
        Task<int> LoadAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>把脏数据写回库（一局结束 / 断开 / 关服时调）。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>写回去几条。</returns>
        Task<int> FlushAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>一句人话（落库统计）。</summary>
        /// <returns>例：`写回缓存：读 3、写 2、脏 0`。</returns>
        string DescribeFlushStats();
    }

    /// <summary>玩家发奖台账（共享接口 + **带 IO 的三个方法**）。</summary>
    public interface IPlayerRewardLedger : IRewardLedger, System.IDisposable
    {
        /// <summary>这个台账属于哪个玩家。</summary>
        long PlayerId { get; }

        /// <summary>还有几条脏数据没落库。</summary>
        int DirtyCount { get; }

        /// <summary>把库里的台账读进来（**登录时调一次**，必须在判定"发过没有"之前）。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>读回来几条。</returns>
        Task<int> LoadAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>把"刚发过"的写回库（**不写回去 = 重启后重复发奖**）。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>写回去几条。</returns>
        Task<int> FlushAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// 记下"刚刚发了这份奖励"，并把这笔**实际发放**（exp/gold）排进**同一个事务**
        /// （落库时与台账行一起提交，要么都成、要么都不成）。
        /// <para>⚠️ 幂等：这个 `(kind, ownerId)` 已经有记录时**连发放也不排** ——
        /// 否则"重复解锁"会重复发钱，而台账那边看不出异常（它本来就是单调的）。</para>
        /// </summary>
        /// <param name="kind">谁发的。</param>
        /// <param name="ownerId">发布者编号。</param>
        /// <param name="rewardId">奖励编号（留痕/排查用）。</param>
        /// <param name="exp">这笔要加的 exp（0 = 不加）。</param>
        /// <param name="gold">这笔要加的 gold（0 = 不加）。</param>
        void MarkGrantedWithPayout(ERewardOwnerKind kind, int ownerId, int rewardId, int exp, int gold);

        /// <summary>一句人话（落库统计）。</summary>
        /// <returns>例：`台账：读 1、写 1、脏 0`。</returns>
        string DescribeFlushStats();
    }
}
