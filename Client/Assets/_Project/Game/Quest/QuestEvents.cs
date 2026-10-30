// 这个文件里有：
//   QuestEvents（静态类）：任务模块对外广播的七个事件标识，接取、进度、可交付、交付、发奖、放弃、被拒绝各一个
//   QuestRewardGrantedPayload（结构体）：奖励发放的载荷，带上任务编号与真正发下去的奖励编号
//   QuestRejectedPayload（结构体）：一次操作被拒绝的载荷，带上任务编号与一句人话原因

using NBC.Framework;

namespace NBC.Game.Quest
{
    // 主要功能：声明任务模块对外广播的七个事件，让接取、进度变化、可交付、交付、发奖、放弃与被拒绝都能被订阅。
    // 主要数据成员：Accepted、ProgressChanged、Completed、Submitted、RewardGranted、Abandoned、Rejected（七个 EventId 常量，事件名形如「Quest.Accepted」）。
    // 主要外部方法：无（这里只放事件标识，真正的触发都在 QuestRuntime）。
    // 谁在用它：QuestRuntime 触发；QuestPanel 订阅 Accepted、ProgressChanged、Completed、Submitted（载荷都是 int）；Rejected 目前只有 EditMode 测试订阅；RewardGranted 与 Abandoned 暂无订阅方。
    // 注意：事件标识用 EventId 常量而不是字符串，拼错是编译错误，不会像字符串那样在运行期静默不触发。
    // 注意：每个模块只在自己的文件里声明自己的事件，两个人才不会因为「都要往同一个 enum 里加一行」天天冲突。
    // 注意：载荷类型必须与订阅方写的泛型一致（EventCenter 按 EventId 记通道，类型不符直接抛异常，不静默丢事件），单字段传 int 零分配，两个以上字段才用 readonly struct，否则调用方要传多个 object，迟早错位。
    // 注意：载荷结构体是值类型，事件中心同步派发，订阅方手里的不会是被别人改掉的共享对象。
    public static class QuestEvents
    {
        // 任务接取成功时触发；载荷是任务编号（int，Quest 表主键，演示任务是 3004）。
        public static readonly EventId Accepted = EventId.Declare("Quest.Accepted");

        // 本任务名下某条条件的进度变了时触发（成就在自己那边发的进度不算）；载荷是任务编号（int）。
        public static readonly EventId ProgressChanged = EventId.Declare("Quest.ProgressChanged");

        // 全部条件达成、可以交付时触发（只是「能交了」，不自动发奖，发奖要玩家去交）；载荷是任务编号（int）。
        public static readonly EventId Completed = EventId.Declare("Quest.Completed");

        // 任务已交付、状态落到 Submitted 时触发；载荷是任务编号（int）。
        public static readonly EventId Submitted = EventId.Declare("Quest.Submitted");

        // 奖励真的发下去之后才触发（先查奖励表，查不到就整体不交付，所以发这个事件必然发奖成功）；载荷是 QuestRewardGrantedPayload。
        public static readonly EventId RewardGranted = EventId.Declare("Quest.RewardGranted");

        // 任务被放弃、状态回到 None 时触发（演示与重跑用）；载荷是任务编号（int）。
        public static readonly EventId Abandoned = EventId.Declare("Quest.Abandoned");

        // 一次玩家操作被拒绝时触发（重复接取、条件没达成就要交付……）；载荷是 QuestRejectedPayload，带人话原因。
        // 注意：被拒绝也必须发事件 —— 这类错误只留在返回值与日志里，玩家只会看到「点了没反应」，是最难排查的一类问题；广播出去 UI 才能把原因显示在玩家眼前。
        public static readonly EventId Rejected = EventId.Declare("Quest.Rejected");
    }

    // 主要功能：把「哪个任务、发了哪条奖励」打包成一次发奖的快照，给需要记账或飘字的订阅方。
    // 主要数据成员：QuestId（任务编号）、RewardId（奖励编号）。
    // 主要外部方法：构造函数。
    // 谁在用它：QuestRuntime.Submit 在奖励表查到行、奖励交给 IQuestRewardSink 之后造；目前没有订阅方（UI 与调试窗口都走 IQuestRewardSink 直接拿奖励回执）。
    public readonly struct QuestRewardGrantedPayload
    {
        // 任务编号（int，Quest 表主键，演示任务是 3004）。
        public readonly int QuestId;

        // 奖励编号（int，Reward 表主键，就是真正发下去的那一行）。
        public readonly int RewardId;

        // 功能：把任务编号与奖励编号装成一次发奖事件的载荷。
        // 入参：questId 任务编号（Quest 表主键）；rewardId 奖励编号（Reward 表主键）
        // 步骤：
        //   1) 两个编号直接写进只读字段，不做校验 —— 传错编号这里不会拦，订阅方拿到什么就是什么
        //   2) 字段只读，载荷造好即固定，订阅方看到的是快照
        // 返回：无（构造函数不返回值）
        // 上游：QuestRuntime.Submit（奖励表已查到行、奖励已交给 IQuestRewardSink）
        // 下游：无
        public QuestRewardGrantedPayload(int questId, int rewardId)
        {
            QuestId = questId;
            RewardId = rewardId;
        }
    }

    // 主要功能：把「哪个任务被拒、为什么」打包成一次拒绝的快照，让 UI 能把原因摆在玩家眼前。
    // 主要数据成员：QuestId（任务编号）、Reason（给人看的一句原因）。
    // 主要外部方法：构造函数。
    // 谁在用它：QuestRuntime.Reject 造（接取、交付、放弃的失败都走这一条）；EditMode 的 QuestRuntimeTests 与 QuestFollowTests 订阅 Quest.Rejected 后读 Reason。
    public readonly struct QuestRejectedPayload
    {
        // 任务编号（int，Quest 表主键，演示任务是 3004）。
        public readonly int QuestId;

        // 一句人话原因（直接显示给玩家用，可能含换行，按多行排版）。
        public readonly string Reason;

        // 功能：把任务编号与拒绝原因装成一次被拒事件的载荷。
        // 入参：questId 任务编号（Quest 表主键）；reason 一句人话原因（会原样显示给玩家，别塞英文报错）
        // 步骤：
        //   1) 编号与原因直接写进只读字段，不做校验 —— 原因已由调用方拼好
        //   2) 字段只读，订阅方拿到就能直接显示，不必再加工
        // 返回：无（构造函数不返回值）
        // 上游：QuestRuntime.Reject（接取 / 交付 / 放弃失败时）
        // 下游：无
        public QuestRejectedPayload(int questId, string reason)
        {
            QuestId = questId;
            Reason = reason;
        }
    }
}
