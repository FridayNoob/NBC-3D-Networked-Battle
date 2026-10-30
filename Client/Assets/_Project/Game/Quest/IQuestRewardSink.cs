// 这个文件里有：
//   IQuestRewardSink（接口）：奖励发下去的接缝 —— 只管「发」，不管「该不该发」

namespace NBC.Game.Quest
{
    // 主要功能：把一份已判定应发的奖励交给实现方（加经验、发物品由实现决定）。
    // 主要数据成员：无。
    // 主要外部方法：Grant（发一份奖励）。
    // 谁在用它：QuestRuntime 在交付判定通过后调用；M2 是内存实现，M4 换成服务端实现。
    // 注意：实现不要在这里抛异常拒绝 —— 走到这里说明「该不该发」已经判定完了，
    //       抛异常会让玩家卡在「任务已交付但奖励没到」的中间态；要拒绝请在前一步拒绝。
    // 注意：参数用中性结构体 QuestReward，不收配置表生成类型 —— 奖励表改字段时只影响 QuestRuntime 一处。
    public interface IQuestRewardSink
    {
        // 功能：把一份奖励交给实现方落下（加经验、进背包、发物品都由此实现决定）。
        // 入参：questId 任务编号（实现方记日志用）；reward 奖励内容
        // 返回：无
        // 上游：QuestRuntime 的交付流程
        // 下游：实现方（内存实现 / 以后的服务端实现）
        void Grant(int questId, QuestReward reward);
    }
}
