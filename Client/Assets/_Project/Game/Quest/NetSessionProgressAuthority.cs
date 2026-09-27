// ============================================================================
//  NetSessionProgressAuthority —— 把 `NetSession` 收下来的权威进度接成上面那道缝
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十四
//
//  ---------------------------------------------------------------------------
//  一、它做的全部事情：把 `FindProgress` 的 **null 语义**翻译成 **false**
//  ---------------------------------------------------------------------------
//      `NetSession.FindProgress(key)` 返回 **null**  = 服务端**没说**这条条件
//      `IQuestProgressAuthority.TryGetCondition` 返回 **false** = 同一件事
//
//  ⚠️ 这个翻译看起来"什么都没做"，但它是本片**唯一**会把 null 变成 0 的地方 ——
//     所以它值得单独一个类、单独一条用例：
//     **绝不许**写成 `new AuthoritativeProgress(entry?.Current ?? 0, ...)`
//     （那正好就是"把 null 画成 0/3"那个 bug）。
//
//  ---------------------------------------------------------------------------
//  二、为什么它不住在 `Game\Net\`
//  ---------------------------------------------------------------------------
//  见 `IQuestProgressAuthority.cs` 文件头第三节：网络层不该反过来依赖任务层。
// ============================================================================

using NBC.Game.Net;

namespace NBC.Game.Quest
{
    /// <summary>把网络会话收下来的权威进度，接成任务/成就界面要的那道缝。</summary>
    public sealed class NetSessionProgressAuthority : IQuestProgressAuthority
    {
        /// <summary>网络会话（权威进度的来源）。</summary>
        private readonly NetSession m_session;

        /// <summary>造一个适配器。</summary>
        /// <param name="session">网络会话（不能为 null）。</param>
        public NetSessionProgressAuthority(NetSession session)
        {
            if (session == null)
            {
                throw new System.ArgumentNullException(nameof(session),
                    "[NetSessionProgressAuthority] 必须给一个 NetSession（权威进度的来源）。");
            }

            m_session = session;
        }

        /// <summary>
        /// 取一条条件的权威值。
        /// <para>⚠️ 会话里没有这条条件（= 服务端没说）时返回 **false**，**不是** 0（见文件头）。</para>
        /// </summary>
        /// <param name="conditionId">条件编号。</param>
        /// <param name="progress">权威值。</param>
        /// <returns>服务端确实说过这条时返回 true。</returns>
        public bool TryGetCondition(int conditionId, out AuthoritativeProgress progress)
        {
            progress = default(AuthoritativeProgress);

            // ⚠️ 关键的一行：**null 直接返回 false**。
            //    写成 `?? 0` 就是把"服务端没说"画成"进度是 0"。
            var entry = m_session.FindProgress(conditionId);

            if (entry == null)
            {
                return false;
            }

            progress = new AuthoritativeProgress(entry.Current, entry.Required, entry.Met);
            return true;
        }
    }
}
