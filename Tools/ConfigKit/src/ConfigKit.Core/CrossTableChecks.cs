// ============================================================================
//  ConfigKit · 跨表检查（CrossTableChecks）
//  对应：Docs\17-配置表规范.md（单表校验）、Docs\27-M4开工清单.md §八（本次的缘起）
//
//  ---------------------------------------------------------------------------
//  为什么需要它：**单表校验全绿 ≠ 这张表能被玩通**（2026-09-26 实测发现）
//  ---------------------------------------------------------------------------
//  M4-S1 实跑验收通过之后，我读真任务表想接真任务，结果发现**联机路径下 4 个任务一个都完不成**：
//
//      · `QuestCondition` 要 `KillMonster 6001 × 3`，而 `Dungeon` 1001 只刷 **2** 只野狼；
//      · 任务要 `CollectItem 7001`（狼皮），而 `DropTable` **从不掉 7001**（只掉 9001/9002/9003）；
//      · 任务的 `ReachArea` 条件，在**联机路径上没有事件源**（M3 没做区域系统）。
//
//  这三处单表校验**全都是绿的** —— 因为 `range` / `len` / `unique` / `ref:` 都是**单表**判据：
//  每一张表单看都自洽，**合起来却什么都玩不成**，而且**不报错**。
//  这与"闸门绿 ≠ Unity 绿"是同一族：**判据只覆盖了一半，剩下那一半静默通过。**
//
//  ---------------------------------------------------------------------------
//  三条规则（都能机械判定，不需要人看）
//  ---------------------------------------------------------------------------
//      ① `KillMonster X × N`：必须有某个副本能提供 **≥ N 只** X（`Dungeon.monsters` 的重复次数 + `bossId`）
//      ② `CollectItem I`    ：I 必须有**非循环**的来源 ——
//                            出现在 `DropTable.itemId` 里（最实在：打死怪就能拿到），
//                            或者出现在**别的**任务的奖励里。
//                            ⚠️ 只在"**它自己那个任务**的奖励"里出现 = **循环依赖**
//                               （要 2 张狼皮才给 2 张狼皮）⇒ 判为"拿不到"。
//                               —— 这一条是**实测逼出来的**：第一版只写"出现在掉落或奖励里"，
//                                  结果被 `Reward` 里那两条自己给自己的奖励**骗过去了**。
//      ③ `eventType` 的每种  ：当前实现**有没有事件源**（没有 → **警告**，因为这是"实现缺口"而不是"数据写错"）
//      ④ 条件的**持有者**    ：一条 `QuestCondition` 只能被**一个**任务/成就引用 ——
//                             ≥ 2 个时运行期 `ConditionTracker.Register` **直接抛异常**（CFG0023）。
//                             ⚠️ 这条是 **2026-09-26 加成就时**才成立的：只有任务的时候，
//                                "两个任务共用一个条件"本来就该被抓；有了成就，
//                                "任务与成就共用一个条件"变成了一条**新的**、同样会炸的路径。
//
//  ⚠️ 规则 ③ 的"有没有事件源"是**实现状态**，不是数据关系 —— 所以它住在
//     `ConfigPolicy.EventTypesWithoutSource`（**数据**），做完区域系统就从那张表删掉，
//     而不是在这里改一句 `if`。这正是 `ConfigPolicy` 文件头那条判据：
//     "如果一条规则别的项目可能不想要，它就必须在这里"。
//
//  ⚠️ 规则 ①②④ 是 **Error**（会让本次导出**不产出任何文件**）：
//     它们是真正的数据缺陷 —— "条件永远达不成"、"物品永远拿不到"、
//     "运行期直接抛异常"必须当场拦住，否则表现只是"任务卡住"/"接任务闪退"，
//     而**没有一行报错**。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NBC.ConfigKit
{
    /// <summary>跨表检查：把"单看每张表都对、合起来玩不成"的那类问题报出来。</summary>
    public static class CrossTableChecks
    {
        /// <summary>事件类型列里，"击杀怪"的写法（与 `EConditionEvent.KillMonster` 同名）。</summary>
        private const string KillMonsterEvent = "KillMonster";

        /// <summary>事件类型列里，"收集物品"的写法（与 `EConditionEvent.CollectItem` 同名）。</summary>
        private const string CollectItemEvent = "CollectItem";

        /// <summary>物品的来源分类（判据从松到紧，见文件头规则 ②）。</summary>
        private enum EItemSource
        {
            /// <summary>两处都没有。</summary>
            None = 0,

            /// <summary>只在奖励里出现（可能是循环依赖）。</summary>
            OnlyInReward = 1,

            /// <summary>掉落表里有 —— 最实在的来源。</summary>
            Dropped = 2
        }

        /// <summary>
        /// 跑全部跨表规则。
        /// <para>表不全就**安静跳过**（这个项目可能不做任务/副本）—— 但"列不齐"由单表校验报，这里不重复。</para>
        /// </summary>
        /// <param name="set">读到的表集合。</param>
        /// <param name="policy">项目策略（规则 ③ 的可配置部分）。</param>
        /// <param name="diagnostics">诊断收集器。</param>
        public static void Run(ConfigSet set, ConfigPolicy policy, DiagnosticBag diagnostics)
        {
            if (set == null || diagnostics == null)
            {
                return;
            }

            ConfigTable conditions;

            if (!set.TryGet("QuestCondition", out conditions) || conditions.Schema.HasFatalProblem)
            {
                return;     // 没有任务条件表 → 这三条规则无从谈起
            }

            ColumnSchema eventTypeColumn = conditions.Schema.FindColumn("eventType");
            ColumnSchema targetColumn = conditions.Schema.FindColumn("targetId");
            ColumnSchema countColumn = conditions.Schema.FindColumn("requiredCount");
            ColumnSchema keyColumn = conditions.Schema.Key;

            if (eventTypeColumn == null || targetColumn == null || countColumn == null || keyColumn == null)
            {
                return;     // 列不齐时单表校验已经报过了
            }

            // ① 副本能提供多少只怪（怪编号 → 某一个副本里的最大只数）
            Dictionary<long, int> dungeonCapacity = new Dictionary<long, int>();
            BuildDungeonCapacity(set, dungeonCapacity);

            // ② 物品的来源（掉落 + 奖励 + 任务→条件/奖励 的归属关系）
            ItemSources sources = new ItemSources();
            sources.Build(set);

            // ③ 逐行判
            IReadOnlyList<RawRow> rows = conditions.Raw.Rows;
            HashSet<string> warnedEventTypes = new HashSet<string>(StringComparer.Ordinal);

            for (int r = conditions.Schema.FirstDataRow - 1; r < rows.Count; r++)
            {
                RawRow row = rows[r];

                if (row.IsBlank)
                {
                    continue;
                }

                RawCell eventCell = row.Get(eventTypeColumn.Index, row.ExcelRow);
                string eventType = eventCell.Text.Trim();

                if (eventType.Length == 0)
                {
                    continue;       // 空值问题由单表校验报
                }

                long conditionId = ReadLong(row.Get(keyColumn.Index, row.ExcelRow).Text);
                RawCell targetCell = row.Get(targetColumn.Index, row.ExcelRow);
                long targetId = ReadLong(targetCell.Text);

                // ---- 规则 ④：一条条件只能被**一个**持有者引用 ----
                // ⚠️ 为什么它是**错误**（而不是"提醒一下"）：`ConditionTracker.Register`
                //    对同一编号**重复登记会当场抛 `InvalidOperationException`**
                //    （见 `ConditionTracker.cs` 里那段注释：重复登记会让进度被清零、回调发两次，
                //     静默覆盖会让这个 bug 永远查不出来，所以它是"当场报错"而不是"容忍"）。
                //    ⇒ 两个任务共用一个条件、或任务与成就共用一个条件，
                //      **数据单看全都正常，炸的是运行期**：前一个条件被抢走登记，
                //      后一个任务接取时直接抛异常（成就更早，它在启动时就登记了）。
                //  📌 这正是本文件存在的理由："单表全绿 ≠ 合起来能跑"。
                if (sources.OwnerCount(conditionId) > 1)
                {
                    diagnostics.Error(DiagnosticCodes.ConditionSharedByOwners,
                        conditions.Raw.LocationOf(row.Get(keyColumn.Index, row.ExcelRow), keyColumn.Name),
                        "条件 `" + Text(conditionId) + "` 被**多个持有者**引用：" + sources.DescribeOwners(conditionId) + "。\n" +
                        "⇒ 运行期 `ConditionTracker.Register` 会**直接抛异常**（同一个条件编号只允许一个持有者登记）。\n" +
                        "（要么拆成两条条件，要么让其中一方改用别的条件。）",
                        "最多被一个任务/成就引用",
                        sources.DescribeOwners(conditionId));
                }

                // ---- 规则 ③：这个事件类型当前有没有事件源（警告，每种类型只报一次） ----
                if (policy != null && policy.EventTypesWithoutSource != null
                    && Array.IndexOf(policy.EventTypesWithoutSource, eventType) >= 0
                    && warnedEventTypes.Add(eventType))
                {
                    diagnostics.Warning(DiagnosticCodes.EventTypeWithoutSource,
                        conditions.Raw.LocationOf(eventCell, eventTypeColumn.Name),
                        "事件类型 `" + eventType + "` **当前没有任何事件源** —— 用到它的条件永远不会涨进度。\n" +
                        "（若这是「还没做」的系统，把它留在 `ConfigPolicy.EventTypesWithoutSource` 里；" +
                        " 但要知道：**数据里一旦真用了它，那个任务就是死的**；做完那个系统后请把这条从策略里删掉。）",
                        "有事件源的事件类型",
                        eventType);
                }

                // ---- 规则 ①：击杀数量 vs 副本能提供的数量 ----
                // ⚠️ 两条**必须排除**的情况（2026-09-26 加成就时实测出来的）：
                //    · `targetId = 0` 表示"**任意**目标" —— 它当然不在任何副本的怪列表里，
                //      拿"副本刷不刷这种怪"去判它是**纯误报**；
                //    · **只属于成就的条件不检查**：成就用 `resetProgress: false`，是**跨局累计**的
                //      （"累计击杀 10 只野狼"一局只刷 3 只也能达成），而任务必须**一局内做完**。
                //      ⇒ 判据是"一局能提供多少"，只对**任务**成立。
                //  📌 这也是"天天误报的警告等于没有警告"那条：判据要跟着语义走，不能一刀切。
                if (string.Equals(eventType, KillMonsterEvent, StringComparison.Ordinal)
                    && targetId > 0
                    && sources.IsOwnedByQuest(conditionId))
                {
                    long required = ReadLong(row.Get(countColumn.Index, row.ExcelRow).Text);
                    int capacity;

                    if (!dungeonCapacity.TryGetValue(targetId, out capacity))
                    {
                        diagnostics.Error(DiagnosticCodes.QuestKillUnreachable,
                            conditions.Raw.LocationOf(targetCell, targetColumn.Name),
                            "任务条件：击杀怪 `" + targetId + "` × " + Text(required) +
                            "，但**任何副本都不刷这种怪**（看 `Dungeon.monsters` / `bossId`）—— 这个条件永远达不成。",
                            "至少有一个副本会刷这种怪",
                            "没有任何副本刷 " + Text(targetId));
                    }
                    else if (capacity < required)
                    {
                        diagnostics.Error(DiagnosticCodes.QuestKillUnreachable,
                            conditions.Raw.LocationOf(targetCell, targetColumn.Name),
                            "任务条件：击杀怪 `" + Text(targetId) + "` × " + Text(required) +
                            "，但**任何副本最多只提供 " + Text(capacity) + " 只**（看 `Dungeon.monsters` 的重复次数）" +
                            " —— 这个条件永远达不成。",
                            "≤ 某个副本能提供的数量（最多 " + Text(capacity) + "）",
                            Text(required));
                    }
                }

                // ---- 规则 ②：收集物品必须有**非循环**的来源 ----
                if (string.Equals(eventType, CollectItemEvent, StringComparison.Ordinal))
                {
                    EItemSource source = sources.SourceOf(targetId);

                    if (source == EItemSource.Dropped)
                    {
                        continue;       // 掉落表里有 → 最实在的来源，过
                    }

                    // 只在奖励里出现：只有"**别的**任务发它"才算有来源（否则是循环依赖）
                    bool hasOtherQuestSource = sources.IsRewardedByAnotherQuest(conditionId, targetId);

                    if (hasOtherQuestSource)
                    {
                        continue;
                    }

                    diagnostics.Error(DiagnosticCodes.ItemNeverObtainable,
                        conditions.Raw.LocationOf(targetCell, targetColumn.Name),
                        "任务条件：收集物品 `" + Text(targetId) + "` —— " +
                        (source == EItemSource.OnlyInReward
                            ? "它**只出现在奖励里**，而那份奖励正是**完成这个任务才发的**（循环依赖：要 2 张狼皮才给 2 张狼皮）。"
                            : "它**既不在 `DropTable`、也不在任何 `Reward`** 里。") +
                        "\n⇒ 玩家**永远拿不到**，这个条件永远达不成。" +
                        "\n（" + sources.Describe(targetId) + "）",
                        "出现在 DropTable（或**别的**任务的奖励）里",
                        source == EItemSource.None ? "两处都没有" : "只有它自己的那份奖励");
                }
            }
        }

        // ====================================================================
        //  建索引（都是"读真表"，不写死任何编号）
        // ====================================================================

        /// <summary>算"某个怪在单个副本里最多有几只"（`monsters` 的重复次数 + 是不是 BOSS）。</summary>
        /// <param name="set">表集合。</param>
        /// <param name="capacity">输出：怪编号 → 最大只数。</param>
        private static void BuildDungeonCapacity(ConfigSet set, Dictionary<long, int> capacity)
        {
            ConfigTable dungeons;
            IReadOnlyList<RawRow> rows;

            if (!TryOpen(set, "Dungeon", out dungeons, out rows))
            {
                return;
            }

            ColumnSchema monstersColumn = dungeons.Schema.FindColumn("monsters");
            ColumnSchema bossColumn = dungeons.Schema.FindColumn("bossId");

            for (int r = dungeons.Schema.FirstDataRow - 1; r < rows.Count; r++)
            {
                RawRow row = rows[r];

                if (row.IsBlank)
                {
                    continue;
                }

                Dictionary<long, int> inThisDungeon = new Dictionary<long, int>();

                if (monstersColumn != null)
                {
                    AddCounts(inThisDungeon, row.Get(monstersColumn.Index, row.ExcelRow).Text);
                }

                if (bossColumn != null)
                {
                    long bossId = ReadLong(row.Get(bossColumn.Index, row.ExcelRow).Text);
                    AddCounts(inThisDungeon, bossId > 0 ? bossId.ToString(CultureInfo.InvariantCulture) : string.Empty);
                }

                foreach (KeyValuePair<long, int> pair in inThisDungeon)
                {
                    int best;
                    capacity.TryGetValue(pair.Key, out best);

                    if (pair.Value > best)
                    {
                        capacity[pair.Key] = pair.Value;
                    }
                }
            }
        }

        /// <summary>把 `"6001,6001,6002"` 这种写法数成"编号 → 出现次数"，累加到给定字典。</summary>
        /// <param name="counts">累加目标。</param>
        /// <param name="text">单元格文本。</param>
        private static void AddCounts(Dictionary<long, int> counts, string text)
        {
            IReadOnlyList<long> ids = ReadLongList(text);

            for (int i = 0; i < ids.Count; i++)
            {
                int seen;
                counts.TryGetValue(ids[i], out seen);
                counts[ids[i]] = seen + 1;
            }
        }

        /// <summary>打开一张表（拿到数据行）；表不在/表头有致命问题都算打不开。</summary>
        /// <param name="set">表集合。</param>
        /// <param name="tableName">表名。</param>
        /// <param name="table">输出表。</param>
        /// <param name="rows">输出数据行。</param>
        /// <returns>能打开返回 true。</returns>
        private static bool TryOpen(ConfigSet set, string tableName, out ConfigTable table, out IReadOnlyList<RawRow> rows)
        {
            table = null;
            rows = null;

            if (!set.TryGet(tableName, out table) || table.Schema.HasFatalProblem)
            {
                return false;
            }

            rows = table.Raw.Rows;
            return true;
        }

        /// <summary>
        /// "物品从哪来"的索引：掉落、奖励、以及"任务 ↔ 条件 / 奖励"的归属关系。
        /// <para>⚠️ 任务归属是必需的：判"只出现在奖励里"到底算不算来源，必须知道
        /// **那份奖励是不是它自己那个任务的** —— 这正是第一版漏掉的循环依赖。</para>
        /// </summary>
        private sealed class ItemSources
        {
            /// <summary>`DropTable.itemId` 里出现过的物品（最实在的来源）。</summary>
            private readonly HashSet<long> m_dropped = new HashSet<long>();

            /// <summary>所有奖励里出现过的物品（含"自己给自己"的）。</summary>
            private readonly HashSet<long> m_rewarded = new HashSet<long>();

            /// <summary>任务/成就编号 → 它的奖励物品。</summary>
            private readonly Dictionary<long, long> m_ownerRewardItem = new Dictionary<long, long>();

            /// <summary>条件编号 → 哪些**任务或成就**在用它。</summary>
            private readonly Dictionary<long, List<Owner>> m_conditionOwners = new Dictionary<long, List<Owner>>();

            /// <summary>被**任务**（`Quest`）引用的条件编号 —— 只有这些才做"一局能否达成"的检查。</summary>
            private readonly HashSet<long> m_questOwnedConditions = new HashSet<long>();

            /// <summary>这个条件是不是被某个**任务**引用的（成就不算，见 `Run` 里规则 ① 的说明）。</summary>
            /// <param name="conditionId">条件编号。</param>
            /// <returns>是任务引用的返回 true。</returns>
            public bool IsOwnedByQuest(long conditionId)
            {
                return m_questOwnedConditions.Contains(conditionId);
            }

            /// <summary>这个条件被几个持有者引用（正常是 0 或 1；≥ 2 就是 CFG0023）。</summary>
            /// <param name="conditionId">条件编号。</param>
            /// <returns>持有者个数。</returns>
            public int OwnerCount(long conditionId)
            {
                List<Owner> owners;
                return m_conditionOwners.TryGetValue(conditionId, out owners) ? owners.Count : 0;
            }

            /// <summary>把引用这个条件的持有者列成人话（报错信息里用）。</summary>
            /// <param name="conditionId">条件编号。</param>
            /// <returns>例如「任务 3001、成就 9001」。</returns>
            public string DescribeOwners(long conditionId)
            {
                List<Owner> owners;

                if (!m_conditionOwners.TryGetValue(conditionId, out owners) || owners.Count == 0)
                {
                    return "（没有任何任务/成就引用它）";
                }

                System.Text.StringBuilder builder = new System.Text.StringBuilder();

                for (int i = 0; i < owners.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append('、');
                    }

                    builder.Append(owners[i].ToString());
                }

                return builder.ToString();
            }

            /// <summary>建索引。</summary>
            /// <param name="set">表集合。</param>
            public void Build(ConfigSet set)
            {
                // ① 掉落表：itemId
                ConfigTable table;
                IReadOnlyList<RawRow> rows;

                if (TryOpen(set, "DropTable", out table, out rows))
                {
                    ColumnSchema column = table.Schema.FindColumn("itemId");

                    if (column != null)
                    {
                        for (int r = table.Schema.FirstDataRow - 1; r < rows.Count; r++)
                        {
                            AddIfPositive(m_dropped, rows[r].IsBlank ? string.Empty : rows[r].Get(column.Index, rows[r].ExcelRow).Text);
                        }
                    }
                }

                // ② 奖励表：id → itemId
                Dictionary<long, long> rewardItemById = new Dictionary<long, long>();

                if (TryOpen(set, "Reward", out table, out rows))
                {
                    ColumnSchema idColumn = table.Schema.FindColumn("id") ?? table.Schema.Key;
                    ColumnSchema itemColumn = table.Schema.FindColumn("itemId");

                    if (idColumn != null && itemColumn != null)
                    {
                        for (int r = table.Schema.FirstDataRow - 1; r < rows.Count; r++)
                        {
                            if (rows[r].IsBlank)
                            {
                                continue;
                            }

                            long rewardId = ReadLong(rows[r].Get(idColumn.Index, rows[r].ExcelRow).Text);
                            long itemId = ReadLong(rows[r].Get(itemColumn.Index, rows[r].ExcelRow).Text);

                            if (rewardId > 0 && itemId > 0)
                            {
                                rewardItemById[rewardId] = itemId;
                                m_rewarded.Add(itemId);
                            }
                        }
                    }
                }

                // ③ 任务表 + 成就表：rewardId → 物品；conditionIds → 条件归属
                //    ⚠️ 成就也要读进来（2026-09-26 加成就时补的）：
                //      · 它引用的条件同样"有人管"（判"谁拥有这个条件"时要算上它）；
                //      · 它的奖励也可能是某个条件所需物品的**来源**（否则会误报"循环依赖"）。
                ReadOwnerTable(set, "Quest", markAsQuestOwned: true, rewardItemById);
                ReadOwnerTable(set, "Achievement", markAsQuestOwned: false, rewardItemById);
            }

            /// <summary>
            /// 读一张"拥有条件"的表（`Quest` / `Achievement`：都有 `id` / `conditionIds` / `rewardId`）。
            /// </summary>
            /// <param name="set">表集合。</param>
            /// <param name="tableName">表名。</param>
            /// <param name="markAsQuestOwned">是不是任务表（只有任务的条件才做"一局能否达成"的检查）。</param>
            /// <param name="rewardItemById">奖励编号 → 物品编号（前面已经读好）。</param>
            private void ReadOwnerTable(ConfigSet set, string tableName, bool markAsQuestOwned,
                                        Dictionary<long, long> rewardItemById)
            {
                ConfigTable table;
                IReadOnlyList<RawRow> rows;

                if (!TryOpen(set, tableName, out table, out rows))
                {
                    return;
                }

                ColumnSchema idColumn = table.Schema.FindColumn("id") ?? table.Schema.Key;
                ColumnSchema rewardColumn = table.Schema.FindColumn("rewardId");
                ColumnSchema conditionColumn = table.Schema.FindColumn("conditionIds");

                for (int r = table.Schema.FirstDataRow - 1; r < rows.Count; r++)
                {
                    if (rows[r].IsBlank || idColumn == null)
                    {
                        continue;
                    }

                    long ownerId = ReadLong(rows[r].Get(idColumn.Index, rows[r].ExcelRow).Text);

                    if (ownerId <= 0)
                    {
                        continue;
                    }

                    if (rewardColumn != null)
                    {
                        long rewardId = ReadLong(rows[r].Get(rewardColumn.Index, rows[r].ExcelRow).Text);
                        long itemId;

                        if (rewardId > 0 && rewardItemById.TryGetValue(rewardId, out itemId))
                        {
                            m_ownerRewardItem[ownerId] = itemId;
                        }
                    }

                    if (conditionColumn == null)
                    {
                        continue;
                    }

                    IReadOnlyList<long> ids = ReadLongList(rows[r].Get(conditionColumn.Index, rows[r].ExcelRow).Text);

                    for (int i = 0; i < ids.Count; i++)
                    {
                        List<Owner> owners;

                        if (!m_conditionOwners.TryGetValue(ids[i], out owners))
                        {
                            owners = new List<Owner>();
                            m_conditionOwners[ids[i]] = owners;
                        }

                        owners.Add(new Owner(ownerId, markAsQuestOwned));

                        if (markAsQuestOwned)
                        {
                            m_questOwnedConditions.Add(ids[i]);
                        }
                    }
                }
            }

            /// <summary>一个"持有者"：引用了这条条件的**任务**或**成就**。</summary>
            private readonly struct Owner
            {
                /// <summary>持有者编号（`Quest.id` 或 `Achievement.id`）。</summary>
                public readonly long Id;

                /// <summary>是不是任务（false = 成就）。两者**语义不同**，报错时必须说清是哪个。</summary>
                public readonly bool IsQuest;

                /// <summary>造一个持有者。</summary>
                /// <param name="id">编号。</param>
                /// <param name="isQuest">是不是任务。</param>
                public Owner(long id, bool isQuest)
                {
                    Id = id;
                    IsQuest = isQuest;
                }

                /// <summary>人话（例如「任务 3001」）。</summary>
                /// <returns>描述。</returns>
                public override string ToString()
                {
                    return (IsQuest ? "任务 " : "成就 ") + Id.ToString(CultureInfo.InvariantCulture);
                }
            }

            /// <summary>这个物品的来源分类。</summary>
            /// <param name="itemId">物品编号。</param>
            /// <returns>来源分类。</returns>
            public EItemSource SourceOf(long itemId)
            {
                if (m_dropped.Contains(itemId))
                {
                    return EItemSource.Dropped;
                }

                return m_rewarded.Contains(itemId) ? EItemSource.OnlyInReward : EItemSource.None;
            }

            /// <summary>
            /// 这个物品是不是"**别的**任务"发出来的奖励（那就不是循环依赖，算有来源）。
            /// </summary>
            /// <param name="conditionId">当前条件编号。</param>
            /// <param name="itemId">物品编号。</param>
            /// <returns>别的任务发它返回 true。</returns>
            public bool IsRewardedByAnotherQuest(long conditionId, long itemId)
            {
                List<Owner> owners;

                if (!m_conditionOwners.TryGetValue(conditionId, out owners))
                {
                    // 这个条件没被任何任务引用（单表校验会另报"没人用"之类的问题）
                    // ⇒ 只要有任何任务发这个物品就算有来源
                    foreach (KeyValuePair<long, long> pair in m_ownerRewardItem)
                    {
                        if (pair.Value == itemId)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                foreach (KeyValuePair<long, long> pair in m_ownerRewardItem)
                {
                    if (pair.Value != itemId)
                    {
                        continue;
                    }

                    for (int i = 0; i < owners.Count; i++)
                    {
                        if (owners[i].Id != pair.Key)
                        {
                            return true;        // 是**别的**任务发的
                        }
                    }
                }

                return false;
            }

            /// <summary>人话：这个物品在哪些表里出现过（报错信息里带上，省得再去翻）。</summary>
            /// <param name="itemId">物品编号。</param>
            /// <returns>人话。</returns>
            public string Describe(long itemId)
            {
                return "掉落表：" + (m_dropped.Contains(itemId) ? "有" : "没有") +
                       "；奖励表：" + (m_rewarded.Contains(itemId) ? "有" : "没有") +
                       "；被任务引用为奖励：" + (IsAnyQuestRewarded(itemId) ? "有" : "没有");
            }

            /// <summary>有没有任何任务的奖励是这个物品。</summary>
            /// <param name="itemId">物品编号。</param>
            /// <returns>有返回 true。</returns>
            private bool IsAnyQuestRewarded(long itemId)
            {
                foreach (KeyValuePair<long, long> pair in m_ownerRewardItem)
                {
                    if (pair.Value == itemId)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        // ====================================================================
        //  小工具
        // ====================================================================

        /// <summary>把单元格文本里的整数加进集合（≤ 0 的忽略）。</summary>
        /// <param name="target">目标集合。</param>
        /// <param name="text">文本。</param>
        private static void AddIfPositive(HashSet<long> target, string text)
        {
            long value = ReadLong(text);

            if (value > 0)
            {
                target.Add(value);
            }
        }

        /// <summary>把 `"4001,4002"` 读成编号列表（读不出的项忽略 —— 类型错由单表校验报）。</summary>
        /// <param name="text">文本。</param>
        /// <returns>编号列表。</returns>
        private static IReadOnlyList<long> ReadLongList(string text)
        {
            var result = new List<long>();

            if (string.IsNullOrEmpty(text))
            {
                return result;
            }

            string[] parts = text.Split(',');

            for (int i = 0; i < parts.Length; i++)
            {
                long value = ReadLong(parts[i]);

                if (value > 0)
                {
                    result.Add(value);
                }
            }

            return result;
        }

        /// <summary>把单元格文本读成整数（读不出就是 0 —— 类型错由单表校验报）。</summary>
        /// <param name="text">文本。</param>
        /// <returns>数值。</returns>
        private static long ReadLong(string text)
        {
            long value;
            return long.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }

        /// <summary>不变文化的整数写法（报错信息里用，避免千分位之类）。</summary>
        /// <param name="value">数值。</param>
        /// <returns>文本。</returns>
        private static string Text(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
