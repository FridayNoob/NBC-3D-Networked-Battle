// ============================================================================
//  AchievementEvents —— 成就模块对外广播的事件（各模块自己声明自己的）
//  项目：3D联网战斗Demo   对应：M4-S2（成就）
//
//  约定与 `QuestEvents` 完全一致：`EventId` 常量（不是字符串，拼错就是编译错误），
//  一个字段用 `int`、多个字段用 `readonly struct`。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么成就要有 `UnlockFailed` 这个事件（任务那边叫 `Rejected`）
//  ---------------------------------------------------------------------------
//  任务是玩家点了按钮 → 可以**当场**把原因显示给玩家。成就**没有按钮**
//  （条件一齐就自动解锁），所以它出问题时**没有人站在那儿看返回值**。
//  如果这里只是 `return false`，表现就是"我明明打了 10 只狼，成就没解"——
//  一条**完全没有痕迹**的悬案。所以哪怕没有玩家操作，也要广播出去：
//  至少调试面板/日志能显示"成就 9001 解锁失败：奖励 5003 在 Reward 表里找不到"。
//
//  这和 `QuestEvents.Rejected` 是同一个原则：**被拒绝的原因不能只留在返回值里**。
// ============================================================================

using NBC.Framework;

namespace NBC.Game.Achievement
{
    /// <summary>成就模块的事件标识。</summary>
    public static class AchievementEvents
    {
        /// <summary>某个成就的进度变了。载荷：成就编号（`int`）。</summary>
        public static readonly EventId ProgressChanged = EventId.Declare("Achievement.ProgressChanged");

        /// <summary>成就**解锁**了（奖励已发）。载荷：<see cref="AchievementUnlockedPayload"/>。</summary>
        public static readonly EventId Unlocked = EventId.Declare("Achievement.Unlocked");

        /// <summary>成就该解锁但**没解开**（配置缺奖励之类）。载荷：<see cref="AchievementUnlockFailedPayload"/>。</summary>
        public static readonly EventId UnlockFailed = EventId.Declare("Achievement.UnlockFailed");
    }

    /// <summary>成就解锁的载荷。</summary>
    public readonly struct AchievementUnlockedPayload
    {
        /// <summary>成就编号。</summary>
        public readonly int AchievementId;

        /// <summary>发下去的奖励编号（奖励表的行主键）。</summary>
        public readonly int RewardId;

        /// <summary>造一个载荷。</summary>
        /// <param name="achievementId">成就编号。</param>
        /// <param name="rewardId">奖励编号。</param>
        public AchievementUnlockedPayload(int achievementId, int rewardId)
        {
            AchievementId = achievementId;
            RewardId = rewardId;
        }
    }

    /// <summary>成就解锁失败的载荷。</summary>
    public readonly struct AchievementUnlockFailedPayload
    {
        /// <summary>成就编号。</summary>
        public readonly int AchievementId;

        /// <summary>一句人话原因（可以直接显示出来）。</summary>
        public readonly string Reason;

        /// <summary>造一个载荷。</summary>
        /// <param name="achievementId">成就编号。</param>
        /// <param name="reason">原因。</param>
        public AchievementUnlockFailedPayload(int achievementId, string reason)
        {
            AchievementId = achievementId;
            Reason = reason;
        }
    }
}
