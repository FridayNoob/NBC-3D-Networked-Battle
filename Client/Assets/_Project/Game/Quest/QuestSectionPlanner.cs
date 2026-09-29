// ============================================================================
//  QuestSectionPlanner —— 任务面板**分桶**的纯逻辑（M4-S3 §二十七）
//  项目：3D联网战斗Demo
//
//  ---------------------------------------------------------------------------
//  一、为什么把它单独抽出来（这一片存在的全部理由）
//  ---------------------------------------------------------------------------
//  这条逻辑原本长在 `QuestPanelModel.CopySections` 里 —— 那是 **Unity 侧**代码
//  （它要 `QuestRuntime` 的配置去造行文字），所以：
//      · **探针编不进去** ⇒ "不丢行"那条不变式**只能在 EditMode 里验**
//      · ⇒ 也就**做不出可运行的变异**（我方无法自己证明它红）
//
//  把"**分桶决策**"抽到这里之后，它是**纯 BCL**（不引 UnityEngine / EventCenter /
//  QuestRuntime）⇒ 探针能编、能跑、能变异；而"造行文字"仍留在模型里。
//
//      QuestSectionPlanner（本文件）  = **谁去哪个桶**（纯逻辑，可测可变异）
//      QuestPanelModel.CopySections   = 把桶里的任务**翻译成给玩家看的行**
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 它保证的那条不变式：**不许挪丢**
//  ---------------------------------------------------------------------------
//      **四个桶的并集 == 输入集合，而且每个任务恰好出现一次。**
//
//  这是"多视图挪列"最经典的 bug：从 A 桶删掉了、忘了加进 B 桶 ⇒
//  那个任务**从界面上消失**，而且**不报错**。
//
//  做法：`Plan` 里用一个 `placed` 集合 + **一次遍历**决定每个任务的归属 ——
//  "挪"这件事在结构上**只发生一次**（写进 `placements`），**没有**"先删后加"两步。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ 三值语义（`null` ≠ `0`，本片最容易被写错的一处）
//  ---------------------------------------------------------------------------
//      `query` 返回 **false** = **服务端没说** ⇒ 按**本地**判断，且 `FromAuthority = false`
//                            （界面要标「（本地预测）」）
//      `query` 返回 **true**  = 服务端**明确表态** ⇒ 用它的数字，`FromAuthority = true`
//          · `0` ⇒ **可接**（服务端明确说"没接过"）
//          · `1` ⇒ 进行中；`2` ⇒ 可交付；`3` ⇒ 已交付
//
//  ❌ 把 false 当成 0：界面会把"还没同步"画成"服务端说没接取"
// ============================================================================

using System.Collections.Generic;

namespace NBC.Game.Quest
{
    /// <summary>任务在面板上属于哪个区。</summary>
    public enum EQuestSection
    {
        /// <summary>可接（本地没接 / 服务端明确说没接过）。</summary>
        Offer = 0,

        /// <summary>进行中。</summary>
        Active = 1,

        /// <summary>已完成、可交付。</summary>
        Ready = 2,

        /// <summary>已交付。</summary>
        Done = 3
    }

    /// <summary>
    /// 分桶的纯逻辑（**引擎无关**：只依赖 BCL，所以探针能编能跑）。
    /// </summary>
    public static class QuestSectionPlanner
    {
        /// <summary>
        /// 问"服务端对这个任务说了什么"。
        /// <para>⚠️ 返回 **false = 服务端没说**（不是"未接取"）。</para>
        /// </summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="state">服务端给的状态（返回 false 时无意义）。</param>
        /// <returns>服务端确实表态过时返回 true。</returns>
        public delegate bool StateQuery(int questId, out int state);

        /// <summary>一条输入：任务 + **本地**认为的状态（0 = 本地当它是可接）。</summary>
        public readonly struct Entry
        {
            /// <summary>任务编号。</summary>
            public readonly int QuestId;

            /// <summary>本地状态（0=未接取 1=已接 2=已完成 3=已交付）。</summary>
            public readonly int LocalState;

            /// <summary>造一条输入。</summary>
            /// <param name="questId">任务编号。</param>
            /// <param name="localState">本地状态。</param>
            public Entry(int questId, int localState)
            {
                QuestId = questId;
                LocalState = localState;
            }
        }

        /// <summary>一个任务最终落在哪儿。</summary>
        public readonly struct Placement
        {
            /// <summary>任务编号。</summary>
            public readonly int QuestId;

            /// <summary>落在哪个区。</summary>
            public readonly EQuestSection Section;

            /// <summary>这个归属是不是**服务端说的**（false = 本地预测，界面要标）。</summary>
            public readonly bool FromAuthority;

            /// <summary>本地状态（造行时用来判断"本地有没有它的 tracking"）。</summary>
            public readonly int LocalState;

            /// <summary>造一条归属。</summary>
            /// <param name="questId">任务编号。</param>
            /// <param name="section">区。</param>
            /// <param name="fromAuthority">是不是服务端说的。</param>
            /// <param name="localState">本地状态。</param>
            public Placement(int questId, EQuestSection section, bool fromAuthority, int localState)
            {
                QuestId = questId;
                Section = section;
                FromAuthority = fromAuthority;
                LocalState = localState;
            }
        }

        /// <summary>
        /// 分桶：**每个输入任务恰好产出一条归属**（这就是"不丢行"的实现）。
        ///
        /// <para>⚠️ `localTracked` **先**遍历：它带着本地状态，信息更全；
        /// `localOffers` 里跟它重号的任务**不会**产出第二条（`placed` 挡住）——
        /// "同一个任务在界面上出现两次"与"消失"是同一类 bug 的两面。</para>
        /// </summary>
        /// <param name="localTracked">本地已接的任务（带本地状态）。</param>
        /// <param name="localOffers">本地认为可接的任务（本地状态按 0 处理）。</param>
        /// <param name="query">权威查询（可以为 null = 没接权威 ⇒ 全部按本地）。</param>
        /// <returns>归属列表（顺序：先本地已接，再本地可接）。</returns>
        public static List<Placement> Plan(
            IReadOnlyList<Entry> localTracked, IReadOnlyList<int> localOffers, StateQuery query)
        {
            var placements = new List<Placement>();

            // ⚠️ 这个集合就是"不丢行 / 不重复"的保证 —— 见文件头第二节
            var placed = new HashSet<int>();

            if (localTracked != null)
            {
                for (int i = 0; i < localTracked.Count; i++)
                {
                    Entry entry = localTracked[i];

                    if (!placed.Add(entry.QuestId))
                    {
                        continue;   // 输入里自己重号 ⇒ 只算一次（否则"恰好一次"就不成立了）
                    }

                    placements.Add(Decide(entry.QuestId, entry.LocalState, query));
                }
            }

            if (localOffers != null)
            {
                for (int i = 0; i < localOffers.Count; i++)
                {
                    int questId = localOffers[i];

                    if (!placed.Add(questId))
                    {
                        continue;
                    }

                    placements.Add(Decide(questId, 0, query));
                }
            }

            return placements;
        }

        /// <summary>决定一个任务去哪个桶（权威优先，服务端没说才用本地）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="localState">本地状态。</param>
        /// <param name="query">权威查询（可为 null）。</param>
        /// <returns>归属。</returns>
        public static Placement Decide(int questId, int localState, StateQuery query)
        {
            int authoritative;

            // ⚠️ 三值语义：false = 服务端**没说**（不是"未接取"）⇒ 退回本地并标预测
            if (query != null && query(questId, out authoritative))
            {
                return new Placement(questId, SectionOf(authoritative), true, localState);
            }

            return new Placement(questId, SectionOf(localState), false, localState);
        }

        /// <summary>状态数字 → 区（**0..3 之外一律当 0**，免得脏数据把任务弄丢）。</summary>
        /// <param name="state">状态数字。</param>
        /// <returns>区。</returns>
        public static EQuestSection SectionOf(int state)
        {
            switch (state)
            {
                case 1: return EQuestSection.Active;
                case 2: return EQuestSection.Ready;
                case 3: return EQuestSection.Done;
                default: return EQuestSection.Offer;
            }
        }
    }
}
