// 这个文件里有：
//   IQuestStateAuthority（接口）：任务状态的权威来源 —— 能表达「服务端还没说过」

namespace NBC.Game.Quest
{
    // 主要功能：回答「这个任务到哪一步了」，并且把「服务端没说过」与「明确说没接过」区分开。
    // 主要数据成员：无。
    // 主要外部方法：TryGetState（取权威状态）。
    // 谁在用它：QuestRuntime 与 QuestPanelModel 读取服务端权威状态。
    // 注意：它与 IQuestProgressAuthority 是两道缝，刻意不合并 ——
    //       后者是「条件进度」（只增不减），前者是「任务状态」（可回退，且有两个字段）。
    // 注意：返回 false = 服务端没说，不是「未接取」；实现不许在拿不到数据时编一个 0 出来。
    public interface IQuestStateAuthority
    {
        // 功能：取出某个任务的权威状态，并告诉调用方「服务端到底说过没有」。
        // 入参：questId 任务编号
        // 返回：服务端确实表态过返回 true，state 为状态数字（0=未接取 1=已接 2=已完成 3=已交付）；否则 false
        // 上游：QuestRuntime 的权威同步流程
        // 下游：QuestPanelModel 显示（拿不到就退回本地那份并标注「本地预测」）
        bool TryGetState(int questId, out int state);
    }
}
