// ============================================================================
//  QuestProgressOverlay —— 把**权威进度**盖到要显示的那些条件行上（界面唯一的入口）
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十四
//
//  ---------------------------------------------------------------------------
//  一、它保证的那条不变式（本片的全部价值）
//  ---------------------------------------------------------------------------
//      每一行显示的数字，**要么是服务端说的、要么被标成「本地预测」** ——
//      **绝不出现"看不出是谁说的"的数字**。
//
//  于是玩家与排查的人都能一眼看出：这一格是事实，还是客户端自己的猜测。
//  （在此之前两者长得一模一样 —— 一致性只是"碰巧"，不一致时也**不报错**。）
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 三条刻意写死的语义
//  ---------------------------------------------------------------------------
//  ① **只在 `TryGetCondition` 返回 true 时才覆盖**：false = 服务端没说
//     ⇒ 保留**本地那份**并标 `LocalPrediction`。
//     ❌ 不许把 false 当成 0 —— 那会把"还没同步"画成"进度被清零"。
//
//  ② **`authority == null` 也要走同一条路**（全部标成本地预测）：
//     "没接权威"和"权威还没说话"在界面上是**同一件事**（都不可当真），
//     合成一条分支就少一处会写错的地方。
//
//  ③ **标注文案只在这里定义一次**：两个显示点（任务面板 / 成就）共用它，
//     免得一处写「本地预测」、另一处写「预测中」，玩家以为是两种状态。
// ============================================================================

using System.Collections.Generic;

namespace NBC.Game.Quest
{
    /// <summary>把权威进度盖到条件行上（任务面板与成就显示共用同一份）。</summary>
    public static class QuestProgressOverlay
    {
        /// <summary>本地预测的标注（**只在这里定义一次**，见文件头 ③）。</summary>
        public const string LocalMark = "（本地预测）";

        /// <summary>
        /// 按权威进度修正一批条件行，并**逐行**标出它的来源。
        /// </summary>
        /// <param name="lines">要显示的条件行（**原地修改**）。</param>
        /// <param name="authority">权威来源；传 null = 没接权威（全部按本地预测处理）。</param>
        /// <returns>有几行拿到了权威值（0 = 这批全是预测，界面应当看得出）。</returns>
        public static int Apply(List<QuestConditionLine> lines, IQuestProgressAuthority authority)
        {
            if (lines == null)
            {
                return 0;
            }

            int authoritative = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                QuestConditionLine line = lines[i];

                if (line == null)
                {
                    continue;
                }

                AuthoritativeProgress progress;

                // 见文件头 ②：没接权威 —— 与"权威没说话"同一条路
                if (authority == null || !authority.TryGetCondition(line.ConditionId, out progress))
                {
                    line.Source = EProgressSource.LocalPrediction;
                    continue;
                }

                line.Current = progress.Current;
                line.Required = progress.Required;
                line.IsMet = progress.Met;
                line.Source = EProgressSource.ServerAuthoritative;
                authoritative++;
            }

            return authoritative;
        }

        /// <summary>这个来源要不要在界面上标注（**权威不标、预测才标**）。</summary>
        /// <param name="source">来源。</param>
        /// <returns>要标的文案；权威来源返回空串。</returns>
        public static string Mark(EProgressSource source)
        {
            return source == EProgressSource.LocalPrediction ? LocalMark : string.Empty;
        }
    }
}
