// ============================================================================
//  RuntimeQuestFollowSink —— 把"跟随权威"接到**本地任务运行时**上
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十八（本地状态机跟随权威）
//
//  ---------------------------------------------------------------------------
//  一、它为什么这么小
//  ---------------------------------------------------------------------------
//  决策（"本地该做什么"）全在 `QuestStateReconciler` 里 —— 那是**引擎无关**的纯逻辑，
//  所以探针能编译它、【二十】那 8 条能跑、变异能真红。
//  这一层只做一件事：把 `FollowToState` 转发给 `QuestRuntime.FollowState`。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 为什么它在**这个**方向依赖（Game\Quest → 引擎侧运行时）
//  ---------------------------------------------------------------------------
//  `IQuestFollowSink` 定义在纯逻辑那一侧，它**不认识** `QuestRuntime`；
//  由这个类把两者接起来 ⇒ 纯逻辑那一侧保持引擎无关（否则它进不了探针，
//  那一片就再也做不出"可运行的变异"了）。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ 只有"改状态"这一个能力（故意的）
//  ---------------------------------------------------------------------------
//  `IQuestFollowSink` 上**只有** `FollowToState`：本地跟随**不许**发明奖励、不许提示玩家、
//  不许重置进度。接口上不给这些入口，比"记得别调"可靠（见 `Docs\27` §28.2）。
// ============================================================================

namespace NBC.Game.Quest
{
    /// <summary>
    /// 把"跟随权威"施加到本地 <see cref="QuestRuntime"/> 上（只改状态，见文件头三）。
    /// </summary>
    public sealed class RuntimeQuestFollowSink : IQuestFollowSink
    {
        /// <summary>本地任务运行时。</summary>
        private readonly QuestRuntime m_runtime;

        /// <summary>造一个出口。</summary>
        /// <param name="runtime">本地任务运行时（不能为 null）。</param>
        public RuntimeQuestFollowSink(QuestRuntime runtime)
        {
            if (runtime == null)
            {
                throw new System.ArgumentNullException(nameof(runtime),
                    "[RuntimeQuestFollowSink] 必须给一个 QuestRuntime。");
            }

            m_runtime = runtime;
        }

        /// <summary>把某个任务在本地跟到目标状态。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="state">目标状态（0=未接取 1=已接 2=已完成 3=已交付）。</param>
        /// <remarks>⚠️ 接口返回 `void`（这一片只关心"改了没有"，次数由 `QuestStateReconciler.Apply` 统计）。</remarks>
        public void FollowToState(int questId, int state)
        {
            m_runtime.FollowState(questId, state);
        }
    }
}
