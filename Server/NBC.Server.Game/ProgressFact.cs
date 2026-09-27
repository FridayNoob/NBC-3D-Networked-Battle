// ============================================================================
//  ProgressFact —— 一条"**发生过的事实**"，喂给服务端权威的条件系统
//  项目：3D联网战斗Demo   对应：M4-S3 服务端权威化，见 `Docs\27` §二十一
//
//  ---------------------------------------------------------------------------
//  为什么不是直接用下发给客户端的那些事件（`DeathEvent` / `DropEvent`）
//  ---------------------------------------------------------------------------
//  ① `DeathEvent.KillerId` 是**实体编号**（实例），而条件系统要的是**玩家编号**；
//     实体 → 玩家的映射**只有在 `ApplyDamage` 那一刻是现成的**（两个实体都在手上）。
//  ② 事件是"**给客户端看的**"（字段会随着协议演进），事实是"**服务端自己记账要的**"
//     （字段只服务条件系统）。把记账挂在下发协议上，等于让"改协议"顺手改坏记账。
//  ⇒ 所以事实在**产生它的那一行**就被记下来（"在信息最全的地方记账"），
//     然后**一份事实、两个消费者**：客户端拿到的是事件（预测/显示），服务端拿到的是它（记账）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 两种"消费者"的顺序值得写清楚
//  ---------------------------------------------------------------------------
//      服务端权威：**权威**（唯一真源，落库、发奖、判解锁）
//      客户端本地：**预测/显示**（同一份事实推出来的，所以两边对得上；接 UI 是下一刀）
// ============================================================================

using NBC.Shared.Condition;

namespace NBC.Server.Game
{
    /// <summary>一条条件事实（`EConditionEvent` + 目标 + 数量 + 是谁）。</summary>
    public readonly struct ProgressFact
    {
        /// <summary>玩家编号（`> 0` = 账号档案；`< 0` = 游客，权威系统会**跳过**它）。</summary>
        public readonly long PlayerId;

        /// <summary>事件类型（击杀 / 拾取……）。</summary>
        public readonly EConditionEvent EventType;

        /// <summary>目标编号（怪物编号 / 物品编号；0 = 任意）。</summary>
        public readonly int TargetId;

        /// <summary>数量（一次击杀 = 1；掉落一堆 = 那一堆的数量）。</summary>
        public readonly int Count;

        /// <summary>造一条事实。</summary>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="eventType">事件类型。</param>
        /// <param name="targetId">目标编号。</param>
        /// <param name="count">数量。</param>
        public ProgressFact(long playerId, EConditionEvent eventType, int targetId, int count)
        {
            PlayerId = playerId;
            EventType = eventType;
            TargetId = targetId;
            Count = count;
        }

        /// <summary>一句人话（日志用）。</summary>
        /// <returns>例：`玩家 1 KillMonster(6001)×1`。</returns>
        public override string ToString()
        {
            return "玩家 " + PlayerId + " " + ConditionEvents.NameOf(EventType) +
                   "(" + TargetId + ")×" + Count;
        }
    }
}
