// 这个文件里有：
//   RuntimeQuestFollowSink（类）：把跟随权威转发给本地 QuestRuntime，只改状态、不做任何本地副作用

namespace NBC.Game.Quest
{
    // 主要功能：把 QuestStateReconciler 算出的目标状态转发给 QuestRuntime.FollowState，本类不做决策、不做幂等判断。
    // 主要数据成员：m_runtime（本地任务运行时，构造时保证非 null）。
    // 主要外部方法：FollowToState（跟随到目标状态，一行转发）。
    // 谁在用它：Editor 的 NetDebugWindow 接会话时造一个交给 QuestStateReconciler.Apply；EditMode 的 QuestFollowTests 也这样用。
    // 注意：本类故意这么薄——决策全在引擎无关的 QuestStateReconciler 里，那一侧才能进探针、跑用例、让变异真的变红。
    // 注意：依赖方向只能是本类引用 QuestRuntime：IQuestFollowSink 定义在纯逻辑侧、不认识 QuestRuntime，反过来接会让纯逻辑不再引擎无关。
    // 注意：跟随只许改状态，不许发明奖励、不许提示玩家、不许重置进度；接口上不给这些入口，比「记得别调」可靠。
    /// <summary>把权威状态施加到本地任务运行时上，只改状态、不做本地副作用。</summary>
    public sealed class RuntimeQuestFollowSink : IQuestFollowSink
    {
        // 本地任务运行时；构造时挡掉 null，之后不可变、非 null
        private readonly QuestRuntime m_runtime;

        /// <summary>绑定本地任务运行时，供 FollowToState 转发目标状态。</summary>
        /// <param name="runtime">本地任务运行时，不能为 null。</param>
        public RuntimeQuestFollowSink(QuestRuntime runtime)
        {
            // 功能：校验运行时非空后存进 m_runtime。
            // 步骤：
            //   1) 先判 null 抛 ArgumentNullException —— 早炸在组装处，好过每次跟随时在 FollowState 里炸、看不出是谁没传
            //   2) 存字段，之后只读
            // 返回：无
            // 上游：NetDebugWindow 接会话时、QuestFollowTests 组装追随链路时
            // 下游：无
            if (runtime == null)
            {
                throw new System.ArgumentNullException(nameof(runtime),
                    "[RuntimeQuestFollowSink] 必须给一个 QuestRuntime。");
            }

            m_runtime = runtime;
        }

        // 功能：把目标状态原样转给 QuestRuntime.FollowState，本地状态的实际改写都由它完成。
        // 入参：questId 任务编号（配置表 Quest 的主键，>= 0）；state 目标状态，取值 0..3：0=未接取 1=已接取 2=已完成 3=已交付，其余值按 0 处理
        // 步骤：只有一行转发，本层不做决策也不做幂等判断——那些在 QuestStateReconciler 与 FollowState 里，这里再写一遍只会多一处会漂移的判断
        // 返回：无；接口是 void，是否真的改了状态看 FollowState 的返回值 1/0，实际跟随条数由 QuestStateReconciler.Apply 统计
        // 上游：QuestStateReconciler.Apply（NetDebugWindow 收到 quest_state 后走这条路，QuestFollowTests 也走）
        // 下游：QuestRuntime.FollowState
        // 注意：这里只许改状态，不许发奖、不许弹提示、不许重置进度——多做一个动作，「跟随」就变成了第二次玩家动作
        public void FollowToState(int questId, int state)
        {
            m_runtime.FollowState(questId, state);
        }
    }
}
