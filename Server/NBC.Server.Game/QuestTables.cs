// ============================================================================
//  QuestTables —— 服务端读**任务 / 成就 / 条件 / 奖励**四张表（M4-S3 服务端权威化）
//  项目：3D联网战斗Demo   对应：`Docs\27` §二十一
//
//  ---------------------------------------------------------------------------
//  它和 `ServerTables` 是**两个东西**，故意不合并
//  ---------------------------------------------------------------------------
//      `ServerTables`  战斗要用：`Dungeon` / `Monster` / `Hero` / `Skill` / `DropTable`
//      `QuestTables`   任务与成就要用：`Quest` / `QuestCondition` / `Achievement` / `Reward`
//
//  ⚠️ 分开的理由不是"文件太大"，而是**它们的读者不同、失败后果也不同**：
//      · 战斗表读不到 ⇒ 进副本就没怪，**必须拒绝启动**（M3 已经这么做了）
//      · 任务表读不到 ⇒ 只是"成就判定没有配置"，服务端**照样能打**
//    硬塞进一个类，会让"哪个错误该拦住启动"变成一个需要读代码才能回答的问题。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么服务端要**自己再读一遍 CSV**（而不是复用客户端生成的代码）
//  ---------------------------------------------------------------------------
//  客户端读的是 `ConfigKit` 生成的 SO / TSV（`Client\Assets\_Project\Game\Config\Generated\`），
//  它依赖 `UnityEngine`。而服务端**绝不能引 Unity**（本工程的硬约束）。
//  ⇒ 两端的**唯一共同真源是 `Configs\Design\*.csv`**（见 `Docs\17` 与 `Docs\00` 的"唯一真源"）。
//     所以这不是"重复"，而是"各自从真源生成自己能吃的形态"。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 枚举名走**共享层**解析（`ConditionEvents.TryParse`），不自己 `Enum.Parse`
//  ---------------------------------------------------------------------------
//  `QuestCondition.csv` 里 `eventType` 填的是名字（`KillMonster`）。
//  名字的"官方拼法"定义在**共享层**（`Client\Assets\_Project\Shared\Condition\EConditionEvent.cs`），
//  两端都编同一份 ⇒ 服务端不认识的名字与客户端不认识的名字**永远是同一批**。
//  自己写 `Enum.Parse` 的坏处：它宽松（认 `killmonster`、认数字），
//  于是"表里拼错了"会在一端静默生效、在另一端报错。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 跨表引用在**加载时**就校验（不是等运行到那一行才炸）
//  ---------------------------------------------------------------------------
//  `Quest.conditionIds` / `Achievement.conditionIds` 指向 `QuestCondition.id`，
//  而 `rewardId` 指向 `Reward.id`。**引用不存在**是配置事故里最常见的一种，
//  而且它的表现是"这个任务永远做不完"（最难往配置上想）。
//  ⇒ 本类在 `TryLoad` 里**当场逐条校验**，报出"哪一行、哪个 id、指向了什么"。
// ============================================================================

using System.Collections.Generic;
using NBC.Shared.Condition;

namespace NBC.Server.Game
{
    /// <summary>一条条件（`QuestCondition` 表的一行）。</summary>
    public sealed class ConditionRow
    {
        /// <summary>条件编号（`QuestCondition.id`）。</summary>
        public readonly int Id;

        /// <summary>定义（事件类型 + 目标 + 数量）—— 直接就是共享层认的那个结构。</summary>
        public readonly ConditionDef Def;

        /// <summary>表里填的事件类型名字（报错时说得出"你填的是哪个名字"）。</summary>
        public readonly string EventTypeName;

        /// <summary>说明（`note` 列，只用于日志）。</summary>
        public readonly string Note;

        /// <summary>造一行。</summary>
        /// <param name="id">条件编号。</param>
        /// <param name="def">定义。</param>
        /// <param name="eventTypeName">事件类型名字。</param>
        /// <param name="note">说明。</param>
        public ConditionRow(int id, ConditionDef def, string eventTypeName, string note)
        {
            Id = id;
            Def = def;
            EventTypeName = eventTypeName;
            Note = note;
        }

        /// <summary>一句人话。</summary>
        /// <returns>例：`4009=KillMonster(6001)×10 累计击杀 10 只野狼`。</returns>
        public override string ToString()
        {
            return Id + "=" + EventTypeName + "(" + Def.TargetId + ")×" + Def.RequiredCount +
                   (string.IsNullOrEmpty(Note) ? string.Empty : " " + Note);
        }
    }

    /// <summary>一条任务或成就（两张表**形状相同**，所以共用一个行类型）。</summary>
    public sealed class OwnerRow
    {
        /// <summary>编号（任务编号 / 成就编号）。</summary>
        public readonly int Id;

        /// <summary>名字。</summary>
        public readonly string Name;

        /// <summary>描述。</summary>
        public readonly string Desc;

        /// <summary>完成条件编号（≥ 1 条；0 条在加载时就会被拒绝）。</summary>
        public readonly int[] ConditionIds;

        /// <summary>奖励编号（0 = 没有奖励）。</summary>
        public readonly int RewardId;

        /// <summary>造一行。</summary>
        /// <param name="id">编号。</param>
        /// <param name="name">名字。</param>
        /// <param name="desc">描述。</param>
        /// <param name="conditionIds">条件编号。</param>
        /// <param name="rewardId">奖励编号。</param>
        public OwnerRow(int id, string name, string desc, int[] conditionIds, int rewardId)
        {
            Id = id;
            Name = name;
            Desc = desc;
            ConditionIds = conditionIds;
            RewardId = rewardId;
        }

        /// <summary>一句人话。</summary>
        /// <returns>例：`9003 猎手的直觉（条件 4011，奖励 5002）`。</returns>
        public override string ToString()
        {
            return Id + " " + Name + "（条件 " + string.Join(",", ConditionIds) + "，奖励 " + RewardId + "）";
        }
    }

    /// <summary>一份奖励（`Reward` 表的一行）。</summary>
    public sealed class RewardRow
    {
        /// <summary>奖励编号。</summary>
        public readonly int Id;

        /// <summary>经验。</summary>
        public readonly int Exp;

        /// <summary>金币。</summary>
        public readonly int Gold;

        /// <summary>物品编号（0 = 无物品）。</summary>
        public readonly int ItemId;

        /// <summary>物品数量。</summary>
        public readonly int ItemCount;

        /// <summary>造一行。</summary>
        /// <param name="id">编号。</param>
        /// <param name="exp">经验。</param>
        /// <param name="gold">金币。</param>
        /// <param name="itemId">物品编号。</param>
        /// <param name="itemCount">物品数量。</param>
        public RewardRow(int id, int exp, int gold, int itemId, int itemCount)
        {
            Id = id;
            Exp = exp;
            Gold = gold;
            ItemId = itemId;
            ItemCount = itemCount;
        }

        /// <summary>一句人话。</summary>
        /// <returns>例：`5002（exp 80、gold 40、物品 7002×1）`。</returns>
        public override string ToString()
        {
            return Id + "（exp " + Exp + "、gold " + Gold +
                   (ItemId > 0 ? "、物品 " + ItemId + "×" + ItemCount : string.Empty) + "）";
        }
    }

    /// <summary>服务端侧的"任务 / 成就 / 条件 / 奖励"四张表（见文件头）。</summary>
    public sealed class QuestTables
    {
        /// <summary>条件表（key = 条件编号）。</summary>
        private readonly Dictionary<int, ConditionRow> m_conditions = new Dictionary<int, ConditionRow>();

        /// <summary>任务表（key = 任务编号）。</summary>
        private readonly Dictionary<int, OwnerRow> m_quests = new Dictionary<int, OwnerRow>();

        /// <summary>成就表（key = 成就编号）。</summary>
        private readonly Dictionary<int, OwnerRow> m_achievements = new Dictionary<int, OwnerRow>();

        /// <summary>奖励表（key = 奖励编号）。</summary>
        private readonly Dictionary<int, RewardRow> m_rewards = new Dictionary<int, RewardRow>();

        /// <summary>成就列表（**按编号升序**：登记的先后顺序会影响事件顺序，确定性优先）。</summary>
        private readonly List<OwnerRow> m_achievementList = new List<OwnerRow>();

        /// <summary>任务列表（**按编号升序**，理由同成就列表）。</summary>
        private readonly List<OwnerRow> m_questList = new List<OwnerRow>();

        /// <summary>条件个数。</summary>
        public int ConditionCount
        {
            get { return m_conditions.Count; }
        }

        /// <summary>任务个数。</summary>
        public int QuestCount
        {
            get { return m_quests.Count; }
        }

        /// <summary>成就个数。</summary>
        public int AchievementCount
        {
            get { return m_achievements.Count; }
        }

        /// <summary>奖励个数。</summary>
        public int RewardCount
        {
            get { return m_rewards.Count; }
        }

        /// <summary>全部成就（升序）。</summary>
        public IReadOnlyList<OwnerRow> Achievements
        {
            get { return m_achievementList; }
        }

        /// <summary>全部任务（升序）；服务端任务权威按它枚举（§二十五）。</summary>
        public IReadOnlyList<OwnerRow> Quests
        {
            get { return m_questList; }
        }

        /// <summary>取一个任务（没有则 null）。</summary>
        /// <param name="id">任务编号。</param>
        /// <returns>行；没有则 null。</returns>
        public OwnerRow? FindQuest(int id)
        {
            // ⚠️ 必须声明成**可空**：`TryGetValue` 的 `out` 参数在"没找到"时写 null，
            //    写成 `OwnerRow value` 会让编译器报 CS8600（本项目的闸门是 0 警告）。
            //    同族：`AccountDirectory.Login` 里同一个坑（那里我改成 `AccountRow? row`）。
            OwnerRow? value;
            return m_quests.TryGetValue(id, out value) ? value : null;
        }

        /// <summary>取一条条件。</summary>
        /// <param name="id">条件编号。</param>
        /// <returns>行；没有则 null。</returns>
        public ConditionRow? FindCondition(int id)
        {
            ConditionRow? row;
            return m_conditions.TryGetValue(id, out row) ? row : null;
        }

        /// <summary>取一份奖励。</summary>
        /// <param name="id">奖励编号。</param>
        /// <returns>行；没有则 null。</returns>
        public RewardRow? FindReward(int id)
        {
            RewardRow? row;
            return m_rewards.TryGetValue(id, out row) ? row : null;
        }

        /// <summary>
        /// 从目录读四张表（**读不到 / 配置有错都返回 false + 人话原因**）。
        /// </summary>
        /// <param name="directory">表目录（`Configs\Design`）。</param>
        /// <param name="tables">读出来的表（失败时是 null）。</param>
        /// <param name="error">失败原因（成功时是空串）。</param>
        /// <returns>成功返回 true。</returns>
        public static bool TryLoad(string directory, out QuestTables tables, out string error)
        {
            tables = null!;
            error = string.Empty;

            if (string.IsNullOrEmpty(directory) || !System.IO.Directory.Exists(directory))
            {
                error = "配置表目录不存在：" + (directory ?? "(空)");
                return false;
            }

            var result = new QuestTables();

            try
            {
                foreach (CsvSheet sheet in CsvSheet.LoadMany(
                             directory, "QuestCondition", "Quest", "Achievement", "Reward"))
                {
                    switch (sheet.TableName)
                    {
                        case "QuestCondition":
                            if (!result.LoadConditions(sheet, out error))
                            {
                                return false;
                            }

                            break;

                        case "Quest":
                            if (!result.LoadOwners(sheet, "任务", result.m_quests, out error))
                            {
                                return false;
                            }

                            break;

                        case "Achievement":
                            if (!result.LoadOwners(sheet, "成就", result.m_achievements, out error))
                            {
                                return false;
                            }

                            break;

                        case "Reward":
                            if (!result.LoadRewards(sheet, out error))
                            {
                                return false;
                            }

                            break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                error = "读表时抛异常：" + ex.Message;
                return false;
            }

            // 成就列表按编号升序（确定性优先；登记顺序会影响事件顺序）
            foreach (KeyValuePair<int, OwnerRow> pair in result.m_achievements)
            {
                result.m_achievementList.Add(pair.Value);
            }

            result.m_achievementList.Sort((a, b) => a.Id.CompareTo(b.Id));

            // ⚠️ 任务列表同样按编号升序（§二十五 加的）：服务端权威要给**已接取**的任务
            //    登记条件，登记顺序会决定"同一条事实喂给谁先" ⇒ 必须确定，不能靠字典顺序。
            foreach (KeyValuePair<int, OwnerRow> pair in result.m_quests)
            {
                result.m_questList.Add(pair.Value);
            }

            result.m_questList.Sort((a, b) => a.Id.CompareTo(b.Id));

            // 跨表引用校验（**加载时**就查，理由见文件头）
            if (!result.ValidateReferences(out error))
            {
                return false;
            }

            tables = result;
            return true;
        }

        /// <summary>一句人话（启动横幅用）。</summary>
        /// <returns>例：`任务 4、成就 3、条件 8、奖励 3`。</returns>
        public string Describe()
        {
            return "任务 " + QuestCount + "、成就 " + AchievementCount +
                   "、条件 " + ConditionCount + "、奖励 " + RewardCount;
        }

        // ====================================================================
        //  内部：逐张表加载
        // ====================================================================

        /// <summary>读条件表。</summary>
        /// <param name="sheet">表。</param>
        /// <param name="error">失败原因。</param>
        /// <returns>成功返回 true。</returns>
        private bool LoadConditions(CsvSheet sheet, out string error)
        {
            error = string.Empty;

            for (int r = 0; r < sheet.RowCount; r++)
            {
                int id = sheet.Int(r, "id");
                string eventTypeName = sheet.Str(r, "eventType");
                int targetId = sheet.Int(r, "targetId");
                int requiredCount = sheet.Int(r, "requiredCount");

                EConditionEvent eventType;

                if (!ConditionEvents.TryParse(eventTypeName, out eventType))
                {
                    error = "QuestCondition.csv 第 " + (r + 1) + " 行（id=" + id + "）的事件类型 " +
                            "\"" + eventTypeName + "\" 不认识。能填的是：" + ConditionEvents.DescribeAllNames();
                    return false;
                }

                ConditionDef def;
                string defError;

                if (!ConditionDef.TryCreate(eventType, targetId, requiredCount, out def, out defError))
                {
                    error = "QuestCondition.csv 第 " + (r + 1) + " 行（id=" + id + "）配置有错：" + defError;
                    return false;
                }

                if (m_conditions.ContainsKey(id))
                {
                    error = "QuestCondition.csv 里条件编号重复：" + id;
                    return false;
                }

                m_conditions.Add(id, new ConditionRow(id, def, eventTypeName, sheet.Str(r, "note")));
            }

            return true;
        }

        /// <summary>读任务表或成就表（两张表形状相同）。</summary>
        /// <param name="sheet">表。</param>
        /// <param name="kind">"任务" / "成就"（只用于报错）。</param>
        /// <param name="target">装到哪个字典。</param>
        /// <param name="error">失败原因。</param>
        /// <returns>成功返回 true。</returns>
        private bool LoadOwners(CsvSheet sheet, string kind, Dictionary<int, OwnerRow> target, out string error)
        {
            error = string.Empty;

            for (int r = 0; r < sheet.RowCount; r++)
            {
                int id = sheet.Int(r, "id");
                int[] conditionIds = sheet.IntList(r, "conditionIds");

                // ⚠️ 0 条条件 = "接了就直接完成"，那是配置事故，不是"简单任务"
                if (conditionIds.Length == 0)
                {
                    error = sheet.TableName + ".csv 第 " + (r + 1) + " 行（" + kind + " " + id +
                            "）**一条条件都没填**。0 条条件会被判定为「立刻完成」——那是配置事故，不是简单任务。";
                    return false;
                }

                if (target.ContainsKey(id))
                {
                    error = sheet.TableName + ".csv 里编号重复：" + id;
                    return false;
                }

                target.Add(id, new OwnerRow(
                    id, sheet.Str(r, "name"), sheet.Str(r, "desc"), conditionIds, sheet.Int(r, "rewardId")));
            }

            return true;
        }

        /// <summary>读奖励表。</summary>
        /// <param name="sheet">表。</param>
        /// <param name="error">失败原因。</param>
        /// <returns>成功返回 true。</returns>
        private bool LoadRewards(CsvSheet sheet, out string error)
        {
            error = string.Empty;

            for (int r = 0; r < sheet.RowCount; r++)
            {
                int id = sheet.Int(r, "id");

                if (m_rewards.ContainsKey(id))
                {
                    error = "Reward.csv 里奖励编号重复：" + id;
                    return false;
                }

                m_rewards.Add(id, new RewardRow(
                    id, sheet.Int(r, "exp"), sheet.Int(r, "gold"),
                    sheet.Int(r, "itemId"), sheet.Int(r, "itemCount")));
            }

            return true;
        }

        /// <summary>校验跨表引用（条件 / 奖励必须在各自表里存在 + **条件不许被两类 owner 共用**）。</summary>
        /// <param name="error">失败原因。</param>
        /// <returns>全部有效返回 true。</returns>
        private bool ValidateReferences(out string error)
        {
            error = string.Empty;

            if (!ValidateOwnerReferences("Quest", m_quests, out error))
            {
                return false;
            }

            if (!ValidateOwnerReferences("Achievement", m_achievements, out error))
            {
                return false;
            }

            return ValidateNoSharedConditions(out error);
        }

        /// <summary>
        /// ⚠️ **同一个条件编号不许同时被"任务"和"成就"引用**（M4-S3 §二十五 加）。
        ///
        /// <para>为什么这是**加载时**就要挡住的一条（而不是运行期容忍）：</para>
        /// <para>
        /// 条件进度是按「玩家 + **条件编号**」存在**一张表**里的（`condition_progress`），
        /// 而任务权威与成就权威是**两个类**，各自为同一个玩家建**自己那份**
        /// `IPlayerProgressStore`（= 一份写回缓存）。
        /// </para>
        /// <para>
        /// 一旦某个条件被两边共用，两个缓存就会覆盖**同一批行**、各写各的绝对值：
        /// 一边算 0→1、另一边也算 0→1，最后库里停在 **1**（本该是 2）——
        /// 这就是 **lost update**。⚠️ 而它**不报错**：两边日志都正常、
        /// 只是进度**少涨**。数值悄悄不对、日志全绿，正是本项目最怕的一类 bug。
        /// </para>
        /// <para>
        /// ⇒ 想共用不是不行，但**得先让两者共用一个进度存放处**（那样才只有一个缓存、
        /// 一份权威）—— 那是**另一刀**的事，在那之前这条配置必须被拒绝，
        /// 而且要在**加载时**就拒绝，不能等玩家打怪时才发现进度对不上。
        /// </para>
        /// </summary>
        /// <param name="error">失败原因（会点名那个条件编号）。</param>
        /// <returns>没有共用返回 true。</returns>
        private bool ValidateNoSharedConditions(out string error)
        {
            error = string.Empty;

            // 条件编号 → 先看到它的那个 owner 的**标签**（例如「任务 3003」）
            var seen = new Dictionary<int, string>(m_conditions.Count);

            if (!CollectOwnerConditions(m_quests, "任务", seen, out error))
            {
                return false;
            }

            return CollectOwnerConditions(m_achievements, "成就", seen, out error);
        }

        /// <summary>把一类 owner 的条件收进 `seen`；发现被另一类先占就失败。</summary>
        /// <param name="owners">owner 集合。</param>
        /// <param name="kindName">这一类的名字（「任务」/「成就」）。</param>
        /// <param name="seen">已经见过的条件（条件编号 → 先占者的标签）。</param>
        /// <param name="error">失败原因。</param>
        /// <returns>没有冲突返回 true。</returns>
        private static bool CollectOwnerConditions(
            Dictionary<int, OwnerRow> owners, string kindName, Dictionary<int, string> seen, out string error)
        {
            error = string.Empty;

            foreach (KeyValuePair<int, OwnerRow> pair in owners)
            {
                OwnerRow owner = pair.Value;
                string label = kindName + " " + owner.Id + "（" + owner.Name + "）";

                for (int i = 0; i < owner.ConditionIds.Length; i++)
                {
                    int key = owner.ConditionIds[i];
                    string? firstLabel;

                    if (seen.TryGetValue(key, out firstLabel))
                    {
                        // 标签以类名开头（「任务 3003…」/「成就 9002…」），所以"不是这一类"
                        // 就等于"被另一类先占了"。用 StartsWith 而不是取首字符：
                        // 空串不会炸，而且意图一眼看得懂。
                        if (!firstLabel.StartsWith(kindName, System.StringComparison.Ordinal))
                        {
                            error =
                                "条件 " + key + " **被任务和成就共用了**：" + firstLabel + " 与 " + label + "。\n" +
                                "    为什么必须挡住：条件进度按「玩家 + 条件编号」存在**同一张表**里，\n" +
                                "    而任务权威与成就权威**各自**为同一玩家建一份写回缓存 ⇒\n" +
                                "    两个缓存覆盖同一批行、各写各的绝对值 ⇒ **丢更新**（进度少涨，而且不报错）。\n" +
                                "    要共用就得先让两者**共用一个进度存放处**（另一刀），在那之前本表拒绝加载。";
                            return false;
                        }

                        // 同一类内部共用是允许的（那件事归配置检查 CFG0023）
                        continue;
                    }

                    seen.Add(key, label);
                }
            }

            return true;
        }

        /// <summary>校验一批 owner（任务或成就）的条件与奖励引用。</summary>
        /// <param name="tableName">表名（报错用）。</param>
        /// <param name="owners">要校验的 owner。</param>
        /// <param name="error">失败原因。</param>
        /// <returns>全部有效返回 true。</returns>
        private bool ValidateOwnerReferences(string tableName, Dictionary<int, OwnerRow> owners, out string error)
        {
            error = string.Empty;

            foreach (KeyValuePair<int, OwnerRow> pair in owners)
            {
                OwnerRow owner = pair.Value;

                for (int i = 0; i < owner.ConditionIds.Length; i++)
                {
                    if (!m_conditions.ContainsKey(owner.ConditionIds[i]))
                    {
                        error = tableName + " " + owner.Id + "（" + owner.Name + "）的第 " + (i + 1) +
                                " 条条件 " + owner.ConditionIds[i] + " 在 QuestCondition.csv 里**不存在**。";
                        return false;
                    }
                }

                if (owner.RewardId != 0 && !m_rewards.ContainsKey(owner.RewardId))
                {
                    error = tableName + " " + owner.Id + "（" + owner.Name + "）的奖励 " + owner.RewardId +
                            " 在 Reward.csv 里**不存在**。";
                    return false;
                }
            }

            return true;
        }
    }
}
