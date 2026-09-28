// ============================================================================
//  IQuestStateStore —— 「某个玩家接过哪些任务、到哪一步了」的存放处接缝
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十五（§21.4 未做#3 的第一刀）
//
//  ---------------------------------------------------------------------------
//  一、它为什么必须存在（这一刀的全部理由）
//  ---------------------------------------------------------------------------
//  在此之前"接取/交付"只活在**客户端内存**里（`QuestRuntime` 自己一个账本）。
//  服务端不知道玩家接过什么 ⇒ 任务进度**没有权威值**可发 ⇒ 界面只能显示"本地预测"。
//  成就没有"接取"这个动作，所以它先做完了（§二十一）；任务有状态机，留到了这一刀。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 它与 `IPlayerProgressStore` 的分工（别合并）
//  ---------------------------------------------------------------------------
//      `IPlayerProgressStore`（`condition_progress`） = "**某个条件累计了多少次**"
//      `IQuestStateStore`（`quest_state`）            = "**这个任务到哪一步了**"
//
//  两张表各管一件事，于是"进度"与"状态机"不会互相覆盖：
//  一个任务可以 `Completed`（条件都满了）而进度停在需求值（钳位），
//  也可以 `Submitted` 之后进度**仍然保留**（重新接同一个任务还能接着算）。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ 接口故意是同步的，IO 全在 `LoadAsync` / `FlushAsync` 里
//  ---------------------------------------------------------------------------
//  与 `IPlayerProgressStore` 同一条理由（`Docs\27` §十九）：**握手在网上跑，
//  登录路径绝对不能查库**。所以：
//      · `GetState` / `SetState` 只碰内存（主循环里可以随便调）
//      · `LoadAsync` 在登录时调一次（异步，结果通过 Task 回）
//      · `FlushAsync` 在一局结束 / 断开 / 关服时调
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;

namespace NBC.Server.Game
{
    /// <summary>
    /// 任务的**状态机取值**（服务端权威那一份）。
    /// <para>⚠️ 它与客户端的 `NBC.Game.Quest.EQuestState` 是**同一套语义**，
    /// 数字一一对应（1=已接 2=已完成 3=已交付），而且**落库的就是这几个数字**
    /// （见 `Docs\08d` 的 `state` 列注释）。</para>
    /// <para>⚠️ 为什么服务端不复用客户端那个枚举：客户端的类型在 `NBC.Game`（Unity 侧），
    /// 服务端编不到它 —— 而"把客户端程序集拖进服务端"正是本项目一直在避免的方向。
    /// 两边靠**数字与语义**对齐（有探针对账），不靠共享类型。</para>
    /// <para>⚠️ **故意没有 0**：0 = "表里没这一行" = 未接取。
    /// 把"没有记录"和一个具体状态混成一个值，是这类状态表最常见的坑。</para>
    /// </summary>
    public enum EQuestStage
    {
        /// <summary>没接过（= 表里没有这一行）。**不落库**。</summary>
        None = 0,

        /// <summary>已接取，条件还在累计。</summary>
        Accepted = 1,

        /// <summary>条件全满 = 可以交付（但还没交付）。</summary>
        Completed = 2,

        /// <summary>已交付（奖励已发；再交付**不许**再发一次）。</summary>
        Submitted = 3
    }

    /// <summary>一个玩家的任务状态存放处。</summary>
    public interface IQuestStateStore : System.IDisposable
    {
        /// <summary>读一个任务的状态；没记录过时返回 <see cref="EQuestStage.None"/>。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>状态。</returns>
        EQuestStage GetState(int questId);

        /// <summary>写一个任务的状态（**只改内存**，标脏等 `FlushAsync`）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="state">状态。</param>
        void SetState(int questId, EQuestStage state);

        /// <summary>还有几条脏数据没落库。</summary>
        int DirtyCount { get; }

        /// <summary>把库里的状态读进来（**登录时调一次**）。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>读回来几条。</returns>
        Task<int> LoadAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>把脏数据写回库（一局结束 / 断开 / 关服时调）。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>写回去几条。</returns>
        Task<int> FlushAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>一句人话（落库统计）。</summary>
        /// <returns>例：`任务状态：读 2、写 1、脏 0`。</returns>
        string DescribeFlushStats();
    }
}
