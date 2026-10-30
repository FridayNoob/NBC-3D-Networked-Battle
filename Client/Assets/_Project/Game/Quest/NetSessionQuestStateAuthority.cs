// 这个文件里有：
//   NetSessionQuestStateAuthority（类）：把网络会话收下来的任务状态接成 IQuestStateAuthority，并守住「服务端没说」与「明确未接取」的差别

using NBC.Game.Net;

namespace NBC.Game.Quest
{
    // 主要功能：把 NetSession.FindQuestState 的 null（服务端还没说过）翻成 TryGetState 的 false，有值时原样给出状态数字。
    // 主要数据成员：m_session（网络会话，任务状态的唯一来源，非 null）。
    // 主要外部方法：TryGetState（取一个任务的权威状态）。
    // 谁在用它：NetDebugWindow 装配任务面板时创建并挂到 QuestPanelModel.StateAuthority，QuestPanelModel 经接口读取。
    // 注意：任务状态的 null 语义只在这里被翻译一次（与 NetSessionProgressAuthority 同一个理由），所以单独成类并有专门用例；null 只能翻成 false，绝不许写成 FindQuestState(id) ?? 0（那会把「没说」画成「未接取 0」）。
    /// <summary>把网络会话收下来的任务状态，接成任务界面要的那道缝。</summary>
    public sealed class NetSessionQuestStateAuthority : IQuestStateAuthority
    {
        // 网络会话；任务状态的唯一来源，非 null（构造时已校验）
        private readonly NetSession m_session;

        /// <summary>造一个适配器；session 是任务状态的来源，不能为 null。</summary>
        public NetSessionQuestStateAuthority(NetSession session)
        {
            // 功能：校验并保存任务状态的来源会话。
            // 入参：session 网络会话（不能为 null）
            // 步骤：1) null 当场抛 ArgumentNullException 并指出缺的是 NetSession，避免之后在 TryGetState 里空引用；2) 存进 m_session
            // 返回：无
            // 上游：NetDebugWindow.WireQuestPanelToServer（调试面板装配任务模型时）
            // 下游：无
            if (session == null)
            {
                throw new System.ArgumentNullException(nameof(session),
                    "[NetSessionQuestStateAuthority] 必须给一个 NetSession（任务状态的来源）。");
            }

            m_session = session;
        }

        // 功能：取一个任务的权威状态，并告诉调用方服务端到底表过态没有。
        // 入参：questId 任务编号；state 输出状态数字（false 时为 0，调用方不应使用）
        // 步骤：
        //   1) 先把 state 置 0，保证返回 false 时也有确定值
        //   2) 问会话要状态；null 表示服务端还没同步过 ⇒ 直接 false，不能当成未接取
        //   3) 有值才写 state 并返回 true，收到过全量后「表里没有」的 0 也走这里
        // 返回：true = 服务端确实表过态，state 为状态数字（0=未接取 1=已接 2=已完成 3=已交付）；false = 服务端没说，state 无意义
        // 上游：QuestPanelModel 的任务分区与按钮判断（经 IQuestStateAuthority）
        // 下游：NetSession.FindQuestState
        // 注意：收到过全量同步后「表里没有它」会返回 0，那是服务端明确表态，这里必须仍然是 true 而不是 false。
        public bool TryGetState(int questId, out int state)
        {
            state = 0;

            // 注意：null 直接 false；写成 ?? 0 就是把「没说」画成「未接取」
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
