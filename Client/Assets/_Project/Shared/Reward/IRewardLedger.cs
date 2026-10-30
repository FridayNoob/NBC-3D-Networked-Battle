// 这个文件里有：
//   ERewardOwnerKind（枚举）：奖励的来源种类，任务或成就，与数据库 reward_granted.owner_kind 一一对应
//   IRewardLedger（接口）：已发奖励台账的接缝，回答「这份奖励发过没有」并记下「刚刚发了」

namespace NBC.Shared.Reward
{
    /// <summary>奖励的来源种类：任务或成就，值直接落库为 reward_granted.owner_kind。</summary>
    // 主要功能：把奖励按来源分成任务与成就，让台账能用「种类 + 编号」区分同一个编号下的两笔不同奖励。
    // 主要数据成员：Quest（1，任务编号取自 Quest 表主键）、Achievement（2，成就编号取自 Achievement 表主键）。
    // 主要外部方法：无（纯枚举，没有成员方法）。
    // 谁在用它：客户端 AchievementRuntime.Unlock 记账时传 Achievement；服务端 AchievementAuthority / QuestAuthority 按归属传对应值；持久化层拿它与 reward_granted.owner_kind 互转。
    // 注意：1 / 2 会原样落库，只能新增成员、不能改旧值，否则老数据就认不出是谁发的。
    public enum ERewardOwnerKind
    {
        /// <summary>任务发的；取值 1，编号取自 Quest 表主键。</summary>
        Quest = 1,

        /// <summary>成就发的；取值 2，编号取自 Achievement 表主键。</summary>
        Achievement = 2
    }

    /// <summary>已发奖励台账的接缝：查「发过没有」，记「刚刚发了」。</summary>
    // 主要功能：回答「这份奖励发过没有」并记下「刚刚发了」，让重新登录或进程重启后不重发、崩溃后能补上欠的那一次。
    // 主要数据成员：无（纯接缝，记录存在实现方：内存集合或 reward_granted 表）。
    // 主要外部方法：HasGranted（查发过没有）、MarkGranted（记下刚发了）。
    // 谁在用它：客户端 AchievementRuntime.Unlock 发奖前查、发奖后记（BattleSession 把它传进运行时）；服务端 AchievementAuthority / QuestAuthority 经 IPlayerRewardLedger 记账，实现是 NBC.Server.Data 的 MySqlRewardLedger；单机与测试用 InMemoryRewardLedger。
    // 注意：实现必须幂等 —— 同一组 (kind, ownerId) 重复 MarkGranted 不能报错、也不能多出第二条记录，数据库侧靠主键 (player_id, owner_kind, owner_id) 兜住。
    // 注意：进度只能推出「解锁了吗」，推不出「发过奖吗」；已达成且台账没记就该补发，记过就不再发，否则每次重启都会把已完成的成就再发一遍。
    // 注意：这个接缝必须留在双端共享层 —— 实现方是服务端，而 NBC.Server.Data 只引用 NBC.Shared；判断位置时问「谁要实现它」，不只是「谁要用它」。
    public interface IRewardLedger
    {
        /// <summary>这份奖励发过没有；true 表示台账里已有记录。</summary>
        // 功能：查台账，回答这份奖励发过没有。
        // 入参：kind 来源种类（任务 / 成就）；ownerId 发布者编号（任务编号或成就编号，取自各自配置表主键）
        // 返回：true 表示已记过账（发过了）；false 表示没有记录 —— 可能该补发，也可能条件还没达成
        // 步骤：
        //   1) 按 (kind, ownerId) 两个键一起查 —— 只按编号查会把任务与成就里编号相同的两笔混成同一笔
        //   2) 命中即已发；没命中即未发，空台账不能当作「全都发过」
        // 上游：AchievementRuntime.Unlock（客户端发奖前）、服务端 AchievementAuthority / QuestAuthority 的发奖流程
        // 下游：实现方（InMemoryRewardLedger 的集合 / MySqlRewardLedger 载入的内存集合）
        bool HasGranted(ERewardOwnerKind kind, int ownerId);

        /// <summary>记下这份奖励刚刚发过；同一组 (kind, ownerId) 重复调用只算一次。</summary>
        // 功能：把「刚刚发了这份奖励」记进台账，供以后查重。
        // 入参：kind 来源种类（任务 / 成就）；ownerId 发布者编号；rewardId 奖励编号（只留痕供排查，不参与查重）
        // 返回：无；重复调用无副作用
        // 步骤：
        //   1) 按 (kind, ownerId) 定位这一笔，rewardId 只是随记录留痕
        //   2) 没记过就写进去，记过就什么都不做 —— 重试与重启后补发都不会变成两笔
        //   3) 不改已有记录：台账是单调的，记过就永远算发过
        // 上游：AchievementRuntime.Unlock（发奖之后）、服务端 IPlayerRewardLedger.MarkGrantedWithPayout
        // 下游：实现方（InMemoryRewardLedger；MySqlRewardLedger 只改内存，冲库交给它的写回逻辑）
        // 注意：调用方必须先发奖再调这里 —— 反过来的话发奖一旦抛异常，就成了「记了账没发奖」，玩家永远拿不到。
        void MarkGranted(ERewardOwnerKind kind, int ownerId, int rewardId);
    }
}
