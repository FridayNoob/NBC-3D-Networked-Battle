// ============================================================================
//  QuestStateReconciler —— 「权威说 X ⇒ 本地该怎么办」的**纯决策**（M4-S3 §二十八）
//  项目：3D联网战斗Demo
//
//  ---------------------------------------------------------------------------
//  一、它只**算**，不做（这是它能被验的前提）
//  ---------------------------------------------------------------------------
//  这个文件回答一个问题：**收到一份 `quest_state` 之后，本地该做什么**。
//  它**不碰** `QuestRuntime`、不碰网络、不碰 Unity（只依赖 BCL）——
//  所以探针能编、能跑、**能变异**（照 §二十七 `QuestSectionPlanner` 的做法）。
//  真正"改本地"的那一下由 `IQuestFollowSink` 的实现去做（Unity 侧，很薄）。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 三条**静默**错误，是这一片存在的全部理由（都写死在这个文件里）
//  ---------------------------------------------------------------------------
//  ① **不许把"服务端状态"当成"本地操作"**：
//     跟随只能**改状态**，**不许**产生任何本地副作用 —— 不发明奖励、**不重置进度**、
//     不许打"接取成功"这种会骗人的提示（发奖与提示都是**服务端**的事）。
//     ⇒ 出口 `IQuestFollowSink` **故意只有"跟随到某个状态"这一个方法**（见下面 ③）。
//
//  ② **幂等**：同一份 `quest_state` 收到两次，第二次必须是**什么都不做**。
//     ⇒ `Decide` 的第一条判据就是 `local == authoritative ⇒ None`。
//     没有这一条，重复下发就会"接取两次""进度被清两次"，而且**不报错**。
//
//  ③ **不许回路**：跟随**绝不能**再发一次请求给服务端。
//     否则"收到状态 → 本地跟随 → 又发请求 → 又收到状态"就是**回声/活锁**。
//     ⇒ 结构性保证：`IQuestFollowSink` **没有**"发请求"这种东西，
//        这个文件也**不引用**任何发送路径 —— 想发都发不出去（比"记得别发"可靠）。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ 「服务端说 0（明确未接取）而本地是已接」怎么办：**回退**（取舍如下）
//  ---------------------------------------------------------------------------
//  **选的语义：回退到未接取。** 理由：
//    · "以服务端为准"如果**只进不退**，本地就会长期停在"已接取"而服务端说没接过 ——
//      那又是**两个账房**，而且是**永不收敛**的那种（每次同步都对不上）。
//    · 服务端那句话是**全量**的（§二十六）："里面没有它" = **明确**说没接过，不是"还没说"。
//
//  **代价（如实记）**：如果玩家本地接过、而这份同步是**早于**服务端处理他请求的快照，
//    就会**短暂地回退一下**、随后被下一份同步纠正回来（界面闪一下）。
//    ⇒ 所以加了一条**闸**：**请求在飞（`pending`）时不回退**（见 `Decide`），
//      把最难看的那种"刚点完就弹回去"挡掉。
//
//  ⚠️ 另一条**必须保住**的边界：**离线 / 单机**（`M2DemoBehaviour` 那条路）**根本没有权威**
//     ⇒ `Plan` 拿到的是空同步、`Decide` 永远不会被调用 ⇒ **行为与以前逐字一致**。
// ============================================================================

using System.Collections.Generic;

namespace NBC.Game.Quest
{
    /// <summary>收到权威状态后，本地要做的动作。</summary>
    public enum EQuestSyncAction
    {
        /// <summary>什么都不做（**幂等**的那一支：本地已经等于权威）。</summary>
        None = 0,

        /// <summary>本地**跟随**到权威状态（只改状态，无副作用）。</summary>
        Adopt = 1,

        /// <summary>服务端**明确说未接取** ⇒ 本地**回退**到未接取（同样无副作用）。</summary>
        Revert = 2
    }

    /// <summary>对一个任务的处理决定。</summary>
    public readonly struct QuestSyncDecision
    {
        /// <summary>任务编号。</summary>
        public readonly int QuestId;

        /// <summary>要做什么。</summary>
        public readonly EQuestSyncAction Action;

        /// <summary>本地应当到达的状态（`None` 时无意义）。</summary>
        public readonly int TargetState;

        /// <summary>造一条决定。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="action">动作。</param>
        /// <param name="targetState">目标状态。</param>
        public QuestSyncDecision(int questId, EQuestSyncAction action, int targetState)
        {
            QuestId = questId;
            Action = action;
            TargetState = targetState;
        }
    }

    /// <summary>
    /// 跟随的**唯一出口**。
    /// <para>⚠️ **它没有"发请求"这种东西，这是刻意的**（见文件头 ③）：</para>
    /// <para>跟随是"被动接受服务端的结果"，一旦它能发请求，"收到状态 ⇒ 跟随 ⇒ 又发请求"
    /// 就成了回声/活锁，而且在两边状态接近时**永远不会停下来**。</para>
    /// </summary>
    public interface IQuestFollowSink
    {
        /// <summary>
        /// 把本地这个任务**跟随**到某个状态。
        /// <para>⚠️ 实现**只许改状态**：不发明奖励、不重置进度、不打"接取成功"这类提示
        /// （见文件头 ①）。</para>
        /// </summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="state">目标状态（0=未接取 1=已接 2=已完成 3=已交付）。</param>
        void FollowToState(int questId, int state);
    }

    /// <summary>权威状态 ⇒ 本地动作的纯决策（引擎无关，所以探针能编能跑能变异）。</summary>
    public static class QuestStateReconciler
    {
        /// <summary>
        /// 决定一个任务该怎么办。
        ///
        /// <para>⚠️ 判据的顺序**就是语义本身**（别改顺序）：</para>
        /// <list type="number">
        ///   <item><description>`authoritative == local` ⇒ **None**（幂等：重复下发什么都不做）</description></item>
        ///   <item><description>`authoritative != 0` ⇒ **Adopt**（跟随）</description></item>
        ///   <item><description>`authoritative == 0 && pending` ⇒ **None**（请求在飞，**别回退**，见文件头三的代价）</description></item>
        ///   <item><description>`authoritative == 0 && local != 0` ⇒ **Revert**（以服务端为准，**含往回退**）</description></item>
        ///   <item><description>其余（`0 == 0`，被第 1 条接住）不会走到这里</description></item>
        /// </list>
        /// </summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="authoritative">服务端给的状态（0..3；调用方保证这条是"服务端确实说了"）。</param>
        /// <param name="local">本地当前状态（0..3）。</param>
        /// <param name="pending">这个任务是不是"请求已发、等确认"。</param>
        /// <returns>决定。</returns>
        public static QuestSyncDecision Decide(int questId, int authoritative, int local, bool pending)
        {
            // ① 幂等：本地已经等于权威 ⇒ 什么都不做。
            //    ⚠️ 没有这一条，同一份状态重复下发就会"接取两次""进度被清两次"，而且不报错。
            if (authoritative == local)
            {
                return new QuestSyncDecision(questId, EQuestSyncAction.None, local);
            }

            // ② 服务端说了一个具体状态 ⇒ 跟随
            if (authoritative != 0)
            {
                return new QuestSyncDecision(questId, EQuestSyncAction.Adopt, authoritative);
            }

            // ③ 请求在飞 ⇒ **先别回退**（见文件头三的代价：否则会"刚点完就弹回去"）
            if (pending)
            {
                return new QuestSyncDecision(questId, EQuestSyncAction.None, local);
            }

            // ④ 服务端**明确说未接取**、本地却接过 ⇒ 回退（以服务端为准，含往回退）
            if (local != 0)
            {
                return new QuestSyncDecision(questId, EQuestSyncAction.Revert, 0);
            }

            return new QuestSyncDecision(questId, EQuestSyncAction.None, local);
        }

        /// <summary>
        /// 把一份**全量** `quest_state` 算成"本地该做什么"（顺序与输入一致）。
        /// <para>⚠️ **只处理同步里出现的任务**："里面没有它"意味着"服务端说未接取"这件事
        /// 由**调用方**用 `QuestSectionPlanner`（面板分区）去表达；这里不越权替它决定
        /// （否则这层会开始"发明"任务）。</para>
        /// </summary>
        /// <param name="entries">同步里的条目（任务编号 + 服务端状态）。</param>
        /// <param name="localLookup">查本地状态（不能为 null）。</param>
        /// <param name="pendingLookup">查"请求在飞"（可以为 null = 没有待确认）。</param>
        /// <returns>决定列表（可能要做的都在里面）。</returns>
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
                int authoritative = entries[i].LocalState;   // 复用那个结构：这里装的是**服务端**给的状态

                int local = localLookup(questId);
                bool pending = pendingLookup != null && pendingLookup(questId);

                decisions.Add(Decide(questId, authoritative, local, pending));
            }

            return decisions;
        }

        /// <summary>
        /// 施加决定：**只**通过 `IQuestFollowSink` 跟随，**不做**别的事。
        /// </summary>
        /// <param name="decisions">决定列表。</param>
        /// <param name="sink">跟随出口（`None` 的决定**不会**被调）。</param>
        /// <returns>真的跟随了几个（**第二次施加同一份同步必然是 0** —— 幂等）。</returns>
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

        /// <summary>查本地状态。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>本地状态（0..3）。</returns>
        public delegate int LocalStateLookup(int questId);

        /// <summary>查"这个任务的请求是不是还在飞"。</summary>
        /// <param name="questId">任务编号。</param>
        /// <returns>在飞返回 true。</returns>
        public delegate bool PendingLookup(int questId);
    }
}
