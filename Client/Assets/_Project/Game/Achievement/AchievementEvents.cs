// 这个文件里有：
//   AchievementEvents（静态类）：成就模块对外广播的事件标识，进度变化、解锁、解锁失败各一个
//   AchievementUnlockedPayload（结构体）：成就解锁的载荷，带上成就编号与发下去的奖励编号
//   AchievementUnlockFailedPayload（结构体）：成就解锁失败的载荷，带上成就编号与一句人话原因

using NBC.Framework;

namespace NBC.Game.Achievement
{
    // 主要功能：声明成就模块对外广播的三个事件，让每次进度变化、解锁与失败都有可订阅的痕迹。
    // 主要数据成员：ProgressChanged、Unlocked、UnlockFailed（三个 EventId 常量）。
    // 主要外部方法：无（这里只有事件标识，真正触发在 AchievementRuntime）。
    // 谁在用它：AchievementRuntime 触发；NetDebugWindow（Editor 程序集）与成就测试订阅。
    // 注意：成就不像任务有玩家按钮，失败原因若只留在返回值里就没人当场看见（玩家只会觉得「我明明打了 10 只狼，成就没解」），必须广播给调试面板与日志（与 QuestEvents.Rejected 同一个原则）。
    // 注意：事件标识用 EventId 常量而不是字符串，拼错就是编译错误；单字段载荷直接传 int，多字段才用 readonly struct。
    /// <summary>成就模块对外广播的事件标识：进度变化、解锁、解锁失败。</summary>
    public static class AchievementEvents
    {
        /// <summary>某个成就的进度变了；载荷是成就编号（int）。</summary>
        public static readonly EventId ProgressChanged = EventId.Declare("Achievement.ProgressChanged");

        /// <summary>成就解锁且奖励已发；载荷：AchievementUnlockedPayload。</summary>
        public static readonly EventId Unlocked = EventId.Declare("Achievement.Unlocked");

        /// <summary>成就该解锁但没解开（多为奖励表缺行）；载荷：AchievementUnlockFailedPayload。</summary>
        public static readonly EventId UnlockFailed = EventId.Declare("Achievement.UnlockFailed");
    }

    // 主要功能：把「哪个成就解锁了、发了哪条奖励」打包成一次解锁的快照。
    // 主要数据成员：AchievementId（成就编号）、RewardId（奖励编号）。
    // 主要外部方法：构造函数。
    // 谁在用它：AchievementRuntime.Unlock 造；NetDebugWindow 与成就测试订阅 Unlocked 后读。
    /// <summary>成就解锁的载荷（成就编号 + 已发奖励编号）。</summary>
    public readonly struct AchievementUnlockedPayload
    {
        /// <summary>成就编号（Achievement 表主键，正整数）。</summary>
        public readonly int AchievementId;

        /// <summary>发下去的奖励编号（Reward 表主键，正整数）。</summary>
        public readonly int RewardId;

        // 功能：造一个成就解锁事件的载荷。
        // 入参：achievementId 成就编号（Achievement 表主键，正整数）；rewardId 奖励编号（Reward 表主键，正整数）
        // 步骤：
        //   1) 把两个编号直接写进只读字段（不做校验，传错编号这里不会拦）
        //   2) 字段只读，载荷造好即固定，订阅方看到的是快照
        // 返回：无（构造函数不返回值）
        // 上游：AchievementRuntime.Unlock
        // 下游：无
        public AchievementUnlockedPayload(int achievementId, int rewardId)
        {
            AchievementId = achievementId;
            RewardId = rewardId;
        }
    }

    // 主要功能：把「哪个成就该解锁却没解开、为什么」打包成一次失败的快照。
    // 主要数据成员：AchievementId（成就编号）、Reason（给人看的原因）。
    // 主要外部方法：构造函数。
    // 谁在用它：AchievementRuntime.Unlock 在奖励表缺行时造；NetDebugWindow 与成就测试订阅 UnlockFailed 后读。
    /// <summary>成就解锁失败的载荷（成就编号 + 一句人话原因）。</summary>
    public readonly struct AchievementUnlockFailedPayload
    {
        /// <summary>成就编号（Achievement 表主键，正整数）。</summary>
        public readonly int AchievementId;

        /// <summary>可以直接显示给人看的原因（运行时不会传 null，可能为空串）。</summary>
        public readonly string Reason;

        // 功能：造一个成就解锁失败事件的载荷。
        // 入参：achievementId 成就编号（Achievement 表主键，正整数）；reason 失败原因（会原样显示，不要塞英文报错）
        // 步骤：
        //   1) 把成就编号与原因文本直接写进只读字段（不做校验，运行时保证原因不为 null）
        //   2) 字段只读，订阅方拿到后直接显示即可
        // 返回：无（构造函数不返回值）
        // 上游：AchievementRuntime.Unlock（奖励表缺行时）
        // 下游：无
        public AchievementUnlockFailedPayload(int achievementId, string reason)
        {
            AchievementId = achievementId;
            Reason = reason;
        }
    }
}
