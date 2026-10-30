// 这个文件里有：
//   EQuestSection（枚举）：任务面板的四个分区，可接 / 进行中 / 可交付 / 已交付
//   QuestSectionPlanner（静态类）：把两组任务按权威状态分到四个区，保证每个任务恰好产出一条归属
//   QuestSectionPlanner.StateQuery（委托）：问服务端这个任务是什么状态，false 表示服务端没说
//   QuestSectionPlanner.Entry（结构体）：一条分桶输入，任务编号 + 本地状态
//   QuestSectionPlanner.Placement（结构体）：一条分桶结果，落在哪个区、这个归属是不是服务端说的

using System.Collections.Generic;

namespace NBC.Game.Quest
{
    // 主要功能：给任务面板定四个分区，分桶结果用它回答任务该显示在哪一栏。
    // 主要数据成员：Offer（0，可接）、Active（1，进行中）、Ready（2，可交付）、Done（3，已交付）。
    // 主要外部方法：无（纯枚举，没有成员方法）。
    // 谁在用它：QuestSectionPlanner 把归属装进 Placement.Section；QuestPanelModel.CopySections 按区号挑缓冲，并按区号决定「可接区才能接、可交付区才能交」；探针的分桶用例也按它断言。
    // 注意：枚举值必须与状态数字 0..3 对齐 —— 面板直接拿这个数字判断，改值会让按钮与分区错位。
    public enum EQuestSection
    {
        // 可接：本地没接，或服务端明确说没接过；值 0
        Offer = 0,

        // 进行中：已接、条件还没全达成；值 1
        Active = 1,

        // 可交付：条件全达成、还没交；值 2
        Ready = 2,

        // 已交付：交完了；值 3
        Done = 3
    }

    // 主要功能：把本地已接与本地可接两组任务按权威状态分到四个区，保证每个输入任务恰好产出一条归属。
    // 主要数据成员：无（静态类，不持有状态；输入输出都是 Entry / Placement 值对象）。
    // 主要外部方法：Plan（批量分桶）、Decide（单个判定）、SectionOf（状态数字转区）。
    // 谁在用它：QuestPanelModel.CopySections 调 Plan，再把分桶结果翻译成给玩家看的行（分桶决策全在本类）；QuestStateReconciler 与 Editor 的 NetDebugWindow 也复用 Entry 当「任务编号 + 状态」的载体（那边装的是服务端状态）；探针直接编本文件验「不丢行」与三值语义；Boot 不直接调用。
    // 注意：本类只依赖 BCL（不引 UnityEngine / EventCenter / QuestRuntime），所以探针能编、能跑、能变异 —— 这是把分桶从 QuestPanelModel 抽出来的全部理由。
    // 注意：不变式是「四个区的并集等于输入集合，且每个任务恰好出现一次」；从 A 桶删掉忘了加进 B 桶，任务会从界面消失且不报错。
    public static class QuestSectionPlanner
    {
        // 主要功能：定义「问服务端这个任务是什么状态」的回调形状，服务端没表态时明确说没有。
        // 主要数据成员：无（只有签名）。
        // 主要外部方法：无（委托本身没有方法体）。
        // 谁在用它：Plan 与 Decide 先问它再定归属；QuestPanelModel.QueryStateAuthority 就是这个签名的实现，转发给 IQuestStateAuthority.TryGetState；探针用 lambda 造替身。
        // 注意：返回 false 表示服务端没说（不是「未接取」），此时 state 出参无意义；把它当 0 会把「还没同步」画成「服务端说没接取」。
        public delegate bool StateQuery(int questId, out int state);

        // 主要功能：把一条分桶输入（任务编号 + 本地状态）装成只读值，分桶时不必再碰 QuestTracking。
        // 主要数据成员：QuestId（任务编号，Quest 表主键）、LocalState（本地状态，0..3）。
        // 主要外部方法：构造函数（唯一外部方法，两个值一次落位）。
        // 谁在用它：QuestPanelModel.CopySections 用本地 tracking 造；QuestStateReconciler.Plan 读它（那一栏装的是服务端状态），Editor 的 NetDebugWindow 与探针直接 new 出它。
        // 注意：字段名叫 LocalState，但 QuestStateReconciler 那一栏装的是服务端状态（见它的 Plan），别按名字理解。
        /// <summary>一条分桶输入：任务编号 + 本地状态，跨程序集供 Editor 构造。</summary>
        public readonly struct Entry
        {
            // 任务编号，正整数（Quest 表主键，演示任务是 3004）
            public readonly int QuestId;

            // 本地状态，范围 0..3（0 未接取 / 1 已接 / 2 已完成 / 3 已交付）
            public readonly int LocalState;

            /// <summary>造一条分桶输入。</summary>
            public Entry(int questId, int localState)
            {
                // 功能：把任务编号与本地状态装成一条分桶输入。
                // 入参：questId 任务编号（Quest 表主键，正整数）；localState 本地状态（0..3）
                // 步骤：
                //   1) 两个字段直接赋值，不做校验 —— 值由调用方保证，分桶不替上游兜错
                // 返回：无（构造出的实例按值复制）
                // 上游：QuestPanelModel.CopySections、Editor 的 NetDebugWindow、探针
                // 下游：无
                QuestId = questId;
                LocalState = localState;
            }
        }

        // 主要功能：装一条分桶结果 —— 这个任务落在哪个区、这个归属是不是服务端说的。
        // 主要数据成员：QuestId（任务编号）、Section（区，EQuestSection）、FromAuthority（归属来源）、LocalState（本地状态）。
        // 主要外部方法：构造函数（唯一外部方法，四个值一次落位）。
        // 谁在用它：Plan 与 Decide 产出；QuestPanelModel.CopySections 按 Section 挑缓冲、按 LocalState 决定造 tracking 行还是 offer 行；探针读 Section 与 FromAuthority 验三值语义。
        public readonly struct Placement
        {
            // 任务编号，正整数（Quest 表主键）
            public readonly int QuestId;

            // 落在哪个区，取值 EQuestSection 的 0..3
            public readonly EQuestSection Section;

            // 这个归属是不是服务端说的：true 服务端明确表态 / false 按本地判断（不是本地一定对）；界面目前不读它，探针拿它验三值语义
            public readonly bool FromAuthority;

            // 本地状态，范围 0..3；造行时用它判断本地有没有这个任务的 tracking
            public readonly int LocalState;

            // 功能：把一条分桶结果装成只读值。
            // 入参：questId 任务编号（Quest 表主键，正整数）；section 落在哪个区（EQuestSection）；fromAuthority 这个归属是不是服务端说的；localState 本地状态（0..3）
            // 步骤：
            //   1) 四个字段直接赋值，不做判断与换算 —— 判定已经在 Decide 里做完
            // 返回：无（构造出的实例按值复制）
            // 上游：Decide（本类）、探针（造哨兵用）
            // 下游：无
            public Placement(int questId, EQuestSection section, bool fromAuthority, int localState)
            {
                QuestId = questId;
                Section = section;
                FromAuthority = fromAuthority;
                LocalState = localState;
            }
        }

        // 功能：把本地已接与本地可接两组输入各过一遍，给每个任务算出唯一归属，保证不丢行也不重复。
        // 入参：localTracked 本地已接的任务（带本地状态，null 当空）；localOffers 本地认为可接的任务编号（本地状态按 0 处理，null 当空）；query 权威查询（null 表示没接权威，全部按本地判断）
        // 步骤：
        //   1) 先建 placements 与 placed，placed 是「每个任务只产出一条」的唯一保证
        //   2) 先遍历 localTracked：它带着本地状态，信息比 localOffers 全，同号时以它为准
        //   3) 重号就跳过，输入自己重号也只算一次，否则「恰好一次」不成立
        //   4) 再遍历 localOffers：已在 placed 里的编号跳过，同一个任务才不会被排进两个区
        //   5) 每条都交给 Decide 定区与来源，产出一条归属
        // 返回：归属列表，按编号去重后每个任务恰好一条（输入自己重号的只算一次）；顺序先本地已接、再本地可接；两组都为 null 时是空列表而不是 null
        // 上游：QuestPanelModel.CopySections（拼显示行之前）；探针的 QuestSectionPlanner_BucketsAndNeverLosesRows 直接编本文件
        // 下游：Decide
        // 注意：四个区的并集必须等于输入集合 —— 从 A 桶删掉忘了加进 B 桶，任务会从界面消失且不报错，所以「挪」只在写 placements 时发生一次，没有先删后加两步。
        // 注意：两组输入都允许为 null（分别当空），没接权威时 query 传 null 即可。
        public static List<Placement> Plan(
            IReadOnlyList<Entry> localTracked, IReadOnlyList<int> localOffers, StateQuery query)
        {
            var placements = new List<Placement>();

            var placed = new HashSet<int>();

            if (localTracked != null)
            {
                for (int i = 0; i < localTracked.Count; i++)
                {
                    Entry entry = localTracked[i];

                    if (!placed.Add(entry.QuestId))
                    {
                        continue;
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

        // 功能：按「权威优先、服务端没表态才用本地」定一个任务去哪个区，并记下这个归属是不是服务端说的。
        // 入参：questId 任务编号（Quest 表主键，正整数）；localState 本地状态（0..3，0 表示本地当它可接）；query 权威查询（null 表示没接权威）
        // 步骤：
        //   1) 只有 query 非 null 且返回 true 才采信服务端，避免把「没说」当成「说了 0」
        //   2) 采信时拿服务端的状态数字过 SectionOf，FromAuthority 记 true
        //   3) 不采信时退回本地状态，FromAuthority 记 false，这一条就是本地预测
        //   4) 两条路都把 localState 原样带走 —— 造行时要靠它判断本地有没有这个任务的 tracking
        // 返回：一条归属；FromAuthority 为 true 表示服务端明确表过态，false 表示按本地判断的预测
        // 上游：Plan（本类）
        // 下游：SectionOf
        // 注意：query 返回 false 是「服务端没说」，不是「服务端说 0」；把它当 0 会把「还没同步」画成「服务端说没接取」。
        // 注意：服务端明确说 0 走的是另一条路 —— 区是 Offer，但 FromAuthority 仍是 true；那是真的「没接过」，不是「没说」。
        // 注意：query 为 null 与返回 false 走同一条路（都按本地），调用方不必先判空。
        public static Placement Decide(int questId, int localState, StateQuery query)
        {
            int authoritative;

            if (query != null && query(questId, out authoritative))
            {
                return new Placement(questId, SectionOf(authoritative), true, localState);
            }

            return new Placement(questId, SectionOf(localState), false, localState);
        }

        // 功能：把状态数字翻译成面板上的四个区之一。
        // 入参：state 状态数字（服务端或本地给的状态，正常是 0..3：0 未接取 / 1 已接 / 2 已完成 / 3 已交付）
        // 步骤：
        //   1) 1 / 2 / 3 分别翻成进行中 / 可交付 / 已交付
        //   2) 其余（含 0 与 0..3 之外的值）一律当可接，一条脏数据不会把任务弄丢
        // 返回：区；未知状态返回 EQuestSection.Offer，不抛异常
        // 上游：Decide（本类）
        // 下游：无
        // 注意：0..3 之外按 0 处理是有意的 —— 抛异常会让一条脏数据中断整批分桶，界面上其它任务也跟着不显示。
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
