// 这个文件里有：
//   NetSessionProgressAuthority（类）：把 NetSession 收下的权威进度接成 IQuestProgressAuthority，并把「服务端没说」翻译成 false

using NBC.Game.Net;

namespace NBC.Game.Quest
{
    // 主要功能：按条件编号向网络会话查权威进度，再包成 AuthoritativeProgress 交给任务与成就界面。
    // 主要数据成员：m_session（权威进度的来源会话，构造后保证非 null）。
    // 主要外部方法：TryGetCondition（唯一查询入口，只读、无副作用）。
    // 谁在用它：任务与成就界面经 IQuestProgressAuthority 这道缝调用，由 Game 层内部组装，Boot / Editor 程序集不碰它。
    // 注意：它住在 Game\Quest 而不是 Game\Net，是因为实现必须认识 NetSession，放进网络层会让网络层反过来依赖任务层。
    public sealed class NetSessionProgressAuthority : IQuestProgressAuthority
    {
        // 权威进度的来源；构造时已挡掉 null，值域非 null
        private readonly NetSession m_session;

        // 功能：绑定权威进度的来源会话，并提前挡掉 null，让后面每次查询都不必再判空。
        // 入参：session 网络会话，权威进度的来源，不能为 null
        // 步骤：
        //   1) 先判 null 抛 ArgumentNullException —— 早炸在组装处，好过查询时抛 NullReference 找不到源头
        //   2) 存进 m_session 供 TryGetCondition 使用
        // 返回：无
        // 上游：任务与成就界面组装权威查询时创建
        // 下游：无
        public NetSessionProgressAuthority(NetSession session)
        {
            if (session == null)
            {
                throw new System.ArgumentNullException(nameof(session),
                    "[NetSessionProgressAuthority] 必须给一个 NetSession（权威进度的来源）。");
            }

            m_session = session;
        }

        // 功能：按条件编号取服务端下发的权威值，取不到就明确告诉调用方「服务端没说」，而不是给一个 0。
        // 入参：conditionId 条件编号（QuestCondition 表主键，>= 0）；progress 出参，返回 false 时内容无意义
        // 步骤：
        //   1) 先把 progress 置成默认值，避免返回 false 时调用方读到上一次的残留
        //   2) 向会话查这一条；没收到过同步、或这条不在服务端管的那批里，FindProgress 都返回 null
        //   3) 查不到就立刻返回 false —— 这一步是整条接缝里唯一把 null 翻成 false 的地方
        //   4) 查到了才用条目的累计、需求、是否达成三个字段建 AuthoritativeProgress 并返回 true
        // 返回：true 表示服务端确实说过这条，progress 可以当事实用；false 表示服务端没说，绝不是「进度是 0」
        // 上游：任务与成就界面经 IQuestProgressAuthority 调用
        // 下游：NetSession.FindProgress
        // 注意：不许写成 new AuthoritativeProgress(entry?.Current ?? 0, ...)，也不许对结果再补 0 —— 那正好就是把「服务端没说」画成「0/3」的那个 bug。
        public bool TryGetCondition(int conditionId, out AuthoritativeProgress progress)
        {
            progress = default(AuthoritativeProgress);

            // 关键：这里 null 必须返回 false。写成 ?? 0 就是把「服务端没说」画成「进度是 0」。
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
