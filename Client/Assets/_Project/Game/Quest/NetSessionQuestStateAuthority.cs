// ============================================================================
//  NetSessionQuestStateAuthority —— 把 `NetSession` 收下来的任务状态接成上面那道缝
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十六
//
//  ---------------------------------------------------------------------------
//  它做的全部事情：把 `FindQuestState` 的 **null 语义**翻译成 **false**
//  ---------------------------------------------------------------------------
//      `NetSession.FindQuestState(key)` 返回 **null**  = 服务端**没说**
//      `IQuestStateAuthority.TryGetState` 返回 **false** = 同一件事
//
//  ⚠️ 这个翻译看着"什么都没做"，但它是本片**唯一**会把 null 变成 0 的地方
//     （与 §二十四 的 `NetSessionProgressAuthority` 逐字同一个理由）——
//     所以它单独一个类、单独一条用例：
//     **绝不许**写成 `state = m_session.FindQuestState(id) ?? 0`（那就是把 null 画成 0）。
// ============================================================================

using NBC.Game.Net;

namespace NBC.Game.Quest
{
    /// <summary>把网络会话收下来的任务状态，接成任务界面要的那道缝。</summary>
    public sealed class NetSessionQuestStateAuthority : IQuestStateAuthority
    {
        /// <summary>网络会话（任务状态的来源）。</summary>
        private readonly NetSession m_session;

        /// <summary>造一个适配器。</summary>
        /// <param name="session">网络会话（不能为 null）。</param>
        public NetSessionQuestStateAuthority(NetSession session)
        {
            if (session == null)
            {
                throw new System.ArgumentNullException(nameof(session),
                    "[NetSessionQuestStateAuthority] 必须给一个 NetSession（任务状态的来源）。");
            }

            m_session = session;
        }

        /// <summary>
        /// 取一个任务的权威状态。
        /// <para>⚠️ 会话里没有说过这个任务时返回 **false**，**不是**"未接取"（见文件头）。</para>
        /// </summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="state">状态数字。</param>
        /// <returns>服务端确实表态过时返回 true。</returns>
        public bool TryGetState(int questId, out int state)
        {
            state = 0;

            // ⚠️ 关键的一行：**null 直接返回 false**（`?? 0` 就是把"没说"画成"未接取"）
            int? authoritative = m_session.FindQuestState(questId);

            if (!authoritative.HasValue)
            {
                return false;
            }

            state = authoritative.Value;
            return true;
        }
    }
}
