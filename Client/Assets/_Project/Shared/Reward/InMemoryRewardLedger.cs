// ============================================================================
//  InMemoryRewardLedger —— 把"已发奖励"记在内存里的台账（测试 / 单机用）
//  项目：3D联网战斗Demo   对应：M4-S3
//
//  ---------------------------------------------------------------------------
//  ⚠️ 它不是"占位符"，而是**能被断言的那个实现**
//  ---------------------------------------------------------------------------
//  和 `InMemoryQuestRewardSink` 同一个理由：验收线是"**重启之后没有重复发奖**"，
//  所以这个实现必须能回答"到底发过几次" —— 否则测试只能断言"MarkGranted 被调用过"，
//  而**调用过 ≠ 次数对**（M1-B5 的教训）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 它救不了"进程重启"这件事（如实写清，别让人误以为够用）
//  ---------------------------------------------------------------------------
//  `InMemory` 的意思就是"进程一没就没了"。所以：
//
//      · **单机 / EditMode 测试**：够用（同一个进程里模拟"第二局"完全没问题）
//      · **服务端真持久化**：**不够** —— 进程重启后台账也清空，
//        "重启不重复发奖"就失效了。那种场景要用 MySQL 版的实现
//        （`NBC.Server.Data` 里的，对应 `reward_granted` 表）。
//
//  ⇒ 所以**谁用它、能不能用它**是一个必须显式决定的事，这正是 M4-S3 把它做成
//     **构造函数的必填参数**（而不是给个默认值）的原因：让"我没接台账"变成一个
//     编译期就看得见的决定，而不是一个运行期才发现的行为。
// ============================================================================

using System.Collections.Generic;

namespace NBC.Shared.Reward
{
    /// <summary>内存版台账（**跨进程不管用**，见文件头）。</summary>
    public sealed class InMemoryRewardLedger : IRewardLedger
    {
        /// <summary>已发放的记录（键 = "种类:编号"）。</summary>
        private readonly HashSet<string> m_granted = new HashSet<string>();

        /// <summary>按顺序记下每一笔（测试断言"发了几次、分别是谁"用）。</summary>
        private readonly List<string> m_history = new List<string>();

        /// <summary>一共记了几笔（**注意：这是"记账次数"，不是"发奖次数"**）。</summary>
        public int Count
        {
            get { return m_history.Count; }
        }

        /// <summary>这份奖励发过没有。</summary>
        /// <param name="kind">谁发的。</param>
        /// <param name="ownerId">发布者编号。</param>
        /// <returns>发过返回 true。</returns>
        public bool HasGranted(ERewardOwnerKind kind, int ownerId)
        {
            return m_granted.Contains(KeyOf(kind, ownerId));
        }

        /// <summary>记下"刚刚发了这份奖励"（**幂等**）。</summary>
        /// <param name="kind">谁发的。</param>
        /// <param name="ownerId">发布者编号。</param>
        /// <param name="rewardId">奖励编号。</param>
        public void MarkGranted(ERewardOwnerKind kind, int ownerId, int rewardId)
        {
            string key = KeyOf(kind, ownerId);

            // ⚠️ `Add` 返回 false 表示"本来就有"。**不报错、也不重复记**：
            //    台账的契约是幂等，"重复标记"是调用方的正常可能（比如重试），不是错误。
            if (m_granted.Add(key))
            {
                m_history.Add(key + "→" + rewardId);
            }
        }

        /// <summary>清空（测试收尾用）。</summary>
        public void Clear()
        {
            m_granted.Clear();
            m_history.Clear();
        }

        /// <summary>转成一句人话。</summary>
        /// <returns>例如「已发 2 笔：成就:9001→5001、任务:3001→5002」。</returns>
        public override string ToString()
        {
            return "已发 " + m_history.Count + " 笔：" +
                   (m_history.Count == 0 ? "（无）" : string.Join("、", m_history));
        }

        /// <summary>键的拼法集中在这里（**只有一处**，免得两处拼得不一样）。</summary>
        /// <param name="kind">谁发的。</param>
        /// <param name="ownerId">发布者编号。</param>
        /// <returns>键。</returns>
        private static string KeyOf(ERewardOwnerKind kind, int ownerId)
        {
            // ⚠️ 用 `(int)kind` 而不是枚举名：数据库那边存的就是这个数（`owner_kind`），
            //    两边用同一个数字，排查时对得上。
            return ((int)kind) + ":" + ownerId;
        }
    }
}
