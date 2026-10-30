// 这个文件里有：
//   EQuestSyncAction（枚举）：收到权威状态后本地要做的动作 —— 什么都不做 / 跟随 / 回退
//   QuestSyncDecision（结构体）：对一个任务的处理决定，任务编号 + 动作 + 目标状态
//   IQuestFollowSink（接口）：跟随的唯一出口，只有一个「跟随到某个状态」的方法，发不出请求
//   QuestStateReconciler（静态类）：把权威状态算成本地动作的纯决策，只算不做、不发请求
//   QuestStateReconciler.LocalStateLookup（委托）：问本地这个任务当前是什么状态
//   QuestStateReconciler.PendingLookup（委托）：问这个任务的请求是不是还在飞

using System.Collections.Generic;

namespace NBC.Game.Quest
{
    // 主要功能：给「收到权威状态后本地该干什么」定三个动作：不动、跟随、回退。
    // 主要数据成员：None（0，本地已经等于权威）、Adopt（1，跟随到权威状态）、Revert（2，回退到未接取）。
    // 主要外部方法：无（纯枚举，没有成员方法）。
    // 谁在用它：QuestStateReconciler.Decide 产出并装进 QuestSyncDecision.Action；Apply 只跳过 None，其余都交给 IQuestFollowSink；EditMode 的 QuestFollowTests 断言 Action 等于 None。
    // 注意：这只是本地动作编号，没有协议含义；0..2 与任务状态 0..3 不是一回事，别按数字对号入座。
    public enum EQuestSyncAction
    {
        // 什么都不做；值 0，本地已经等于权威（幂等的那一支）
        None = 0,

        // 跟随到权威状态；值 1，只改状态、没有任何本地副作用
        Adopt = 1,

        // 回退到未接取；值 2，服务端明确说未接取而本地接过时用
        Revert = 2
    }

    // 主要功能：把「这个任务该怎么办」算完装成只读值，施加时不必再回查任何状态。
    // 主要数据成员：QuestId（任务编号，正整数）、Action（要做什么）、TargetState（本地应当到达的状态，0..3；Action 为 None 时无意义）。
    // 主要外部方法：构造函数（三个值一次落位，唯一外部方法）。
    // 谁在用它：QuestStateReconciler 的 Decide 造、Plan 装进列表、Apply 按 Action 施加；Editor 的 NetDebugWindow 拿它接 Plan 的结果，EditMode 的 QuestFollowTests 断言它的 Action。
    // 注意：readonly struct，按值复制；Action 为 None 时 TargetState 只是回填的 local 值，别拿它当「目标状态」用。
    public readonly struct QuestSyncDecision
    {
        // 任务编号，正整数（Quest 表主键，演示任务是 3004）
        public readonly int QuestId;

        // 要做什么，取值 EQuestSyncAction 的 0..2
        public readonly EQuestSyncAction Action;

        // 本地应当到达的状态，范围 0..3（0 未接取 / 1 已接 / 2 已完成 / 3 已交付）；Action 为 None 时无意义
        public readonly int TargetState;

        // 功能：把任务编号、动作、目标状态装成一条决定，不做任何判断与换算。
        // 入参：questId 任务编号（正整数）；action 要做什么（EQuestSyncAction）；targetState 本地应当到达的状态（0..3，None 时无意义）
        // 返回：无（构造出的实例按值复制）
        // 步骤：
        //   1) 三个字段直接赋值 —— 判定已经在 Decide 里做完，这里再算一遍只会多一处会漂移的判断
        // 上游：QuestStateReconciler.Decide（唯一构造点）
        // 下游：无
        public QuestSyncDecision(int questId, EQuestSyncAction action, int targetState)
        {
            QuestId = questId;
            Action = action;
            TargetState = targetState;
        }
    }

    // 主要功能：定义跟随的唯一出口 —— 只能把本地任务改到某个状态，发不出请求。
    // 主要数据成员：无（只有签名）。
    // 主要外部方法：FollowToState（本接口唯一的方法）。
    // 谁在用它：QuestStateReconciler.Apply 调它；实现是 Game 的 RuntimeQuestFollowSink（内部转 QuestRuntime.FollowState）；EditMode 的 QuestFollowTests 用真实实现，Editor 的 NetDebugWindow 在接会话时造 RuntimeQuestFollowSink 传进 Apply。
    // 注意：接口故意没有「发请求」的办法 —— 跟随之后再发请求就是回声 / 活锁，两边状态接近时永远停不下来；结构上不给这个入口，比「记得别发」可靠。
    public interface IQuestFollowSink
    {
        // 功能：把本地这个任务跟随到目标状态，实现只许改状态、不许做任何别的动作。
        // 入参：questId 任务编号（正整数）；state 目标状态（0=未接取 1=已接 2=已完成 3=已交付，其余值由实现按 0 处理）
        // 返回：无（接口是 void；真改没改由实现内部决定）
        // 步骤：无（接口不写方法体，跟随的具体动作在实现方，Game 里是 RuntimeQuestFollowSink → QuestRuntime.FollowState）
        // 上游：QuestStateReconciler.Apply（NetDebugWindow 收到 quest_state 后走这条路）
        // 下游：实现方（RuntimeQuestFollowSink）
        // 注意：只许改状态，不许发奖、不许重置进度、不许打「接取成功」这类提示 —— 那些是服务端的事；多做一个动作，跟随就变成了第二次玩家动作。
        void FollowToState(int questId, int state);
    }

    // 主要功能：把一份权威任务状态算成「本地该做什么」（跟随 / 回退 / 什么都不做），只算不做，也不发请求。
    // 主要数据成员：无（静态类，不持有状态；输入是 Entry 列表与两个查状态的委托，输出是决定列表）。
    // 主要外部方法：Decide（单个任务的判定）、Plan（一份全量同步算成决定列表）、Apply（只通过 IQuestFollowSink 施加，返回真跟了几个）。
    // 谁在用它：Editor 的 NetDebugWindow 收到 QuestStateReceived 后走 Plan → Apply（经 RuntimeQuestFollowSink）；EditMode 的 QuestFollowTests 用 Decide 与 Plan / Apply 验幂等、回退与在飞不回退；Boot 不调用；离线 / 单机（M2DemoBehaviour 那条路）没有权威，空同步 ⇒ 一条决定都不产生，行为与以前逐字一致。
    // 注意：本类只依赖 BCL（不引 UnityEngine / 网络 / QuestRuntime），所以探针能编、能跑、能变异 —— 这是把决策抽出来的全部理由，别把 Unity 侧的东西搬进来。
    // 注意：Decide 里判据的顺序就是语义本身（幂等 → 跟随 → 在飞不回退 → 回退），改顺序就会重复接取、清进度或刚点完就弹回去，而且不报错。
    public static class QuestStateReconciler
    {
        // 功能：按「幂等优先、服务端给了具体状态就跟随、请求在飞不回退、否则回退」四条判据，定一个任务该怎么处理。
        // 入参：questId 任务编号（正整数）；authoritative 服务端给的状态（0..3，调用方保证这是服务端确实说了的）；local 本地当前状态（0..3）；pending 这个任务的请求是不是已发、还在等确认
        // 返回：一条决定；None 表示本地不动（TargetState 只是回填 local 的值，无意义），Adopt / Revert 的 TargetState 才是要跟随到的状态
        // 步骤：
        //   1) 先判 authoritative == local 直接 None —— 幂等：同一份状态重复下发时第二次什么都不做，否则会接取两次、进度被清两次，而且不报错
        //   2) authoritative != 0 就 Adopt 跟随 —— 服务端给了具体状态，本地照抄，不发明别的
        //   3) authoritative == 0 且 pending 时留在 None —— 请求还在飞就别回退，否则刚点完就被旧快照弹回去
        //   4) authoritative == 0、本地非 0 时 Revert 回 0 —— 服务端那份是全量的，里面没有它等于明确说没接过，不是还没说
        //   5) 兜底 None（0 == 0 已被第 1 条接住，正常走不到，留着保证每条分支都有返回值）
        // 上游：Plan（本类）；EditMode 的 QuestFollowTests 直接调它验「请求在飞不回退」
        // 下游：无（只造值，不施加）
        // 注意：第 3 条的闸只挡「服务端说 0」这一支；服务端说了 1 / 2 / 3 时即使请求在飞也照跟随 —— 那是一个具体状态，跟着它不会把本地弹回未接取。
        // 注意：代价是若这份同步早于服务端处理玩家请求，会短暂回退、随后被下一份同步纠正回来（界面闪一下）；这是换取「以服务端为准且能收敛」的代价。
        public static QuestSyncDecision Decide(int questId, int authoritative, int local, bool pending)
        {
            if (authoritative == local)
            {
                return new QuestSyncDecision(questId, EQuestSyncAction.None, local);
            }

            if (authoritative != 0)
            {
                return new QuestSyncDecision(questId, EQuestSyncAction.Adopt, authoritative);
            }

            if (pending)
            {
                return new QuestSyncDecision(questId, EQuestSyncAction.None, local);
            }

            if (local != 0)
            {
                return new QuestSyncDecision(questId, EQuestSyncAction.Revert, 0);
            }

            return new QuestSyncDecision(questId, EQuestSyncAction.None, local);
        }

        // 功能：把一份全量同步（任务编号 + 服务端状态）逐条过一遍 Decide，算成本地要做的决定列表。
        // 入参：entries 同步里的条目（复用 QuestSectionPlanner.Entry，那一栏 LocalState 在这里装的是服务端状态，null 当空）；localLookup 查本地状态（0..3，null 当空）；pendingLookup 查请求是否在飞（可以为 null，当没有待确认）
        // 返回：决定列表，顺序与 entries 一致；entries 或 localLookup 为 null 时是空列表，可能要做的事都在里面
        // 步骤：
        //   1) 先建空列表并挡掉 entries / localLookup 为 null —— 返回空列表而不是抛，调用方（NetDebugWindow）不必先判空
        //   2) 按顺序逐条取任务编号与服务端状态，复用 Entry 当载体，省一个只装两个 int 的新类型
        //   3) 逐条查本地状态与在飞标记，再交给 Decide —— 判定全在 Decide 里，这里只喂数据，不自己下结论
        //   4) 保序追加，决定列表与输入同序，调用方不必再对照
        // 上游：Editor 的 NetDebugWindow（收到 QuestStateReceived 后喂同步）；EditMode 的 QuestFollowTests
        // 下游：Decide
        // 注意：只处理同步里出现的任务 —— 全量表里没有它等于未接取，这件事交给面板分区（QuestSectionPlanner）表达，这里不补任务，否则这一层会开始发明任务。
        // 注意：Entry 的字段叫 LocalState，这一栏装的却是服务端状态，别按字段名理解。
        // 注意：pendingLookup 为 null 与返回 false 等价（都当没有待确认），调用方不必先判空。
        /// <summary>把一份全量同步算成本地要做的决定列表，顺序与输入一致，只处理同步里出现的任务。</summary>
        public static List<QuestSyncDecision> Plan(
            IReadOnlyList<QuestSectionPlanner.Entry> entries,
            LocalStateLookup localLookup,
            PendingLookup pendingLookup)
        {
            var decisions = new List<QuestSyncDecision>();

            if (entries == null || localLookup == null)
            {
                return decisions;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                int questId = entries[i].QuestId;
                int authoritative = entries[i].LocalState;

                int local = localLookup(questId);
                bool pending = pendingLookup != null && pendingLookup(questId);

                decisions.Add(Decide(questId, authoritative, local, pending));
            }

            return decisions;
        }

        // 功能：只通过 IQuestFollowSink 把非 None 的决定依次施加下去，统计真的跟了几个。
        // 入参：decisions 决定列表（null 当空，返回 0）；sink 跟随出口（null 当空，返回 0，None 的决定不会调它）
        // 返回：真的跟随了几个任务（调用 FollowToState 的次数）；第二次施加同一份同步必然是 0
        // 步骤：
        //   1) 先挡掉 decisions / sink 为 null —— 返回 0 而不是抛，断线重连时队列为空也不炸
        //   2) 逐条跳过 None —— 幂等的那一支在这里被真正兑现，重复施加同一份同步必然是 0
        //   3) 只调 sink.FollowToState（编号 + 目标状态），不做任何别的事：不发奖、不重置、不提示，也不发请求
        //   4) 只在真的调了之后才 applied++，返回值就是「改了几个任务」
        // 上游：Editor 的 NetDebugWindow（Plan 之后立刻施加）；EditMode 的 QuestFollowTests
        // 下游：IQuestFollowSink.FollowToState（实现是 RuntimeQuestFollowSink → QuestRuntime.FollowState）
        // 注意：这里只许调 FollowToState 这一个方法 —— 多加一个动作，「跟随」就成了第二次玩家动作，会和服务端那份账对不上。
        /// <summary>只通过 sink 施加非 None 的决定并返回真的跟了几个；同一份同步第二次施加必然是 0。</summary>
        public static int Apply(IReadOnlyList<QuestSyncDecision> decisions, IQuestFollowSink sink)
        {
            if (decisions == null || sink == null)
            {
                return 0;
            }

            int applied = 0;

            for (int i = 0; i < decisions.Count; i++)
            {
                QuestSyncDecision decision = decisions[i];

                if (decision.Action == EQuestSyncAction.None)
                {
                    continue;
                }

                sink.FollowToState(decision.QuestId, decision.TargetState);
                applied++;
            }

            return applied;
        }

        // 主要功能：定义「问本地这个任务当前是什么状态」的回调形状。
        // 主要数据成员：无（只有签名）。
        // 主要外部方法：无（委托本身没有方法体）。
        // 谁在用它：Plan 用它查本地状态；Editor 的 NetDebugWindow 传 lambda（问会话里 Quests.StateOf 的结果），EditMode 的 QuestFollowTests 传替身。
        // 注意：本地没有这个任务时必须返回 0（未接取）—— Decide 拿它和 0 比较，返回负数会让「本地没有」被当成「本地接过」而误走回退。
        public delegate int LocalStateLookup(int questId);

        // 主要功能：定义「这个任务的请求是不是已经发出、还在等确认」的回调形状。
        // 主要数据成员：无（只有签名）。
        // 主要外部方法：无（委托本身没有方法体）。
        // 谁在用它：Plan 用它查在飞标记；Editor 的 NetDebugWindow 传 lambda（问面板模型的 IsPending），EditMode 的 QuestFollowTests 传固定 false。
        // 注意：返回 true 只挡「服务端说 0」的回退，不挡跟随；没有待确认机制时整个委托传 null 即可，Plan 会当 false。
        public delegate bool PendingLookup(int questId);
    }
}
