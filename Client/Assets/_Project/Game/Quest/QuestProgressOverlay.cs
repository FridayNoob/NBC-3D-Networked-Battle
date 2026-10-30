// 这个文件里有：
//   QuestProgressOverlay（静态类）：把权威进度盖到要显示的条件行上，并逐行标出这个数字是服务端说的还是本地预测

using System.Collections.Generic;

namespace NBC.Game.Quest
{
    // 主要功能：把权威进度盖到一批条件行上，逐行记住来源，并统一给出「本地预测」的标注文案。
    // 主要数据成员：LocalMark（本地预测的标注文案，权威来源不显示）。
    // 主要外部方法：Apply（原地覆盖并把来源写进每行，返回拿到权威值的行数）、Mark（按来源给出要显示的标注）。
    // 谁在用它：任务面板经 QuestPanelModel.CopyTrackingRows / BuildTrackingRow、成就经 AchievementRuntime.BuildTracking 调用，都是 Game 层内部使用，Boot 与 Editor 不直接调用。
    // 注意：它保证的不变式是每个显示出来的数字要么是服务端说的、要么被标成本地预测，绝不出现看不出是谁说的数字。
    public static class QuestProgressOverlay
    {
        // 本地预测的标注文案；只在这里定义一次，各显示点都取这一份，免得一处写「本地预测」、另一处写「预测中」被当成两种状态
        public const string LocalMark = "（本地预测）";

        // 功能：按条件编号逐行取权威值覆盖条件行，取不到就保留本地那份并标成本地预测，最后返回拿到权威值的行数。
        // 入参：lines 要显示的条件行，原地修改，null 表示没有可盖的行；authority 权威来源，null 表示还没接权威，与「权威没说话」同样处理
        // 步骤：
        //   1) lines 为 null 直接返回 0 —— 没东西可盖，不值得再往下走
        //   2) 逐行跳过 null 行，避免一行脏数据中断整批显示
        //   3) 没接权威、或服务端没提过这条，都走同一条路：保留本地数字，只把来源标成本地预测
        //   4) 只有确实查到权威值才覆盖累计、需求、是否达成，并把来源标成服务端权威、计数加一
        // 返回：拿到权威值的行数；0 表示这批全是本地预测，界面应当看得出
        // 上游：QuestPanelModel.CopyTrackingRows / BuildTrackingRow、AchievementRuntime.BuildTracking，都在拼显示文字之前调用
        // 下游：IQuestProgressAuthority.TryGetCondition
        // 注意：TryGetCondition 返回 false 只表示服务端没说，不许当成 0 —— 当成 0 会把「还没同步」画成「进度被清零」。
        // 注意：authority 为 null 也走同一条分支 —— 没接权威与权威还没说话在界面上都不可当真，合成一条就少一处会写错的地方。
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

                // 没接权威与权威没说话合并成一条：两种情况下这个数字都不可当真，都只标成本地预测
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

        // 功能：按进度来源返回界面要显示的标注，本地预测加标注、服务端权威不标。
        // 入参：source 这一行数字的来源（本地预测或服务端权威）
        // 步骤：
        //   1) 只有本地预测需要标注，权威是默认事实，到处挂字反而淹掉真正的预测
        // 返回：本地预测返回「（本地预测）」，服务端权威返回空串（不是 null，调用方可以直接拼接）
        // 上游：QuestPanelModel.BuildDetail 拼详情行时逐行调用
        // 下游：LocalMark
        public static string Mark(EProgressSource source)
        {
            return source == EProgressSource.LocalPrediction ? LocalMark : string.Empty;
        }
    }
}
