// ============================================================================
//  QuestEvents —— 任务模块对外广播的事件（A3 的 EventId，各模块自己声明自己的）
//  项目：3D联网战斗Demo   对应：M2-A2（任务运行时）、FW-06（string key 的病根）
//
//  ---------------------------------------------------------------------------
//  为什么是 `EventId` 常量而不是字符串
//  ---------------------------------------------------------------------------
//  M1 的 A3 已经把这笔账算清了：原框架用 `string` 当事件名，
//  拼错一个字符就**静默不触发**（FW-06）。改成 `EventId` 之后，
//  引用一个**不存在的常量**是**编译错误**，而不是运行期的悬案。
//
//  约定：**每个模块在自己的文件里声明自己的事件**（所以这里只有任务的）。
//  这样一来两个人分别做任务和 UI，不会因为"都要往同一个 enum 里加一行"而天天冲突。
//
//  ---------------------------------------------------------------------------
//  载荷为什么有的用 `int`、有的用结构体
//  ---------------------------------------------------------------------------
//  · 只有一个字段（任务编号）→ 直接用 `int`，够清楚，零分配
//  · 两个以上字段 → 用一个 `readonly struct`，否则调用方要传两个 `object`，迟早错位
//
//  ⚠️ 载荷结构体是**值类型**：事件中心同步派发（A3 的裁决），
//     所以传值不会出现"回调里拿到的是共享对象、被别人改了"的问题。
// ============================================================================

using NBC.Framework;

namespace NBC.Game.Quest
{
    /// <summary>任务模块的事件标识。</summary>
    public static class QuestEvents
    {
        /// <summary>接取了一个任务。载荷：任务编号（`int`）。</summary>
        public static readonly EventId Accepted = EventId.Declare("Quest.Accepted");

        /// <summary>任务进度变化了（条件涨了进度）。载荷：任务编号（`int`）。</summary>
        public static readonly EventId ProgressChanged = EventId.Declare("Quest.ProgressChanged");

        /// <summary>任务**全部条件达成**、可以交付了。载荷：任务编号（`int`）。</summary>
        public static readonly EventId Completed = EventId.Declare("Quest.Completed");

        /// <summary>任务已交付。载荷：任务编号（`int`）。</summary>
        public static readonly EventId Submitted = EventId.Declare("Quest.Submitted");

        /// <summary>奖励真的发下去了。载荷：<see cref="QuestRewardGrantedPayload"/>。</summary>
        public static readonly EventId RewardGranted = EventId.Declare("Quest.RewardGranted");

        /// <summary>任务被放弃了（演示/重跑用）。载荷：任务编号（`int`）。</summary>
        public static readonly EventId Abandoned = EventId.Declare("Quest.Abandoned");

        /// <summary>
        /// 一次玩家操作被拒绝了（重复接取、条件没达成就要交付……）。
        /// <para>
        /// ⚠️ 为什么"被拒绝"也要发事件：这些错误**不该只出现在日志里**。
        /// 玩家点了按钮却什么都没发生，是最难排查的一类问题
        /// （"我点了呀" → "后台说不能点" → 中间没有任何东西告诉玩家）。
        /// 有了这个事件，UI 就能把原因**显示在玩家眼前**。
        /// </para>
        /// 载荷：<see cref="QuestRejectedPayload"/>。
        /// </summary>
        public static readonly EventId Rejected = EventId.Declare("Quest.Rejected");
    }

    /// <summary>奖励发放的载荷。</summary>
    public readonly struct QuestRewardGrantedPayload
    {
        /// <summary>任务编号。</summary>
        public readonly int QuestId;

        /// <summary>奖励编号（奖励表的行主键）。</summary>
        public readonly int RewardId;

        /// <summary>造一个载荷。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="rewardId">奖励编号。</param>
        public QuestRewardGrantedPayload(int questId, int rewardId)
        {
            QuestId = questId;
            RewardId = rewardId;
        }
    }

    /// <summary>操作被拒绝的载荷。</summary>
    public readonly struct QuestRejectedPayload
    {
        /// <summary>任务编号。</summary>
        public readonly int QuestId;

        /// <summary>一句人话原因（可以直接显示给玩家）。</summary>
        public readonly string Reason;

        /// <summary>造一个载荷。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="reason">原因。</param>
        public QuestRejectedPayload(int questId, string reason)
        {
            QuestId = questId;
            Reason = reason;
        }
    }
}
