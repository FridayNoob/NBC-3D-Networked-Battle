// ============================================================================
//  ConfigKit · 校验引擎（ValidationEngine）+ 表集合（ConfigSet）
//  对应：Docs\17-配置表规范.md §五（主键外键）§六（空值策略）§七（规则语法）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么分三趟（pass），而不是一趟扫完
//  ---------------------------------------------------------------------------
//  有些规则**天然需要"看完整张表"甚至"看完所有表"**才能判断：
//
//      趟 A（逐格）   空值 / 占位符写法 / 类型解析 / range·min·max·len
//      趟 B（逐列）   unique / 主键重复        ← 要看过整列才知道
//      趟 C（跨表）   外键存在性               ← 要看**别的表**的主键集合才知道
//
//  硬塞进一趟会变成"边读边判"，于是外键校验只能靠"运气好目标表已经被读过"。
//  分趟的代价是多扫两遍 —— 对配置表这个体量（几千行）完全可以忽略。
//
//  ---------------------------------------------------------------------------
//  三条写死的语义
//  ---------------------------------------------------------------------------
//  ① **一次报出全部错误**（不是遇到第一个就停）—— 策划改一轮就能全改完。
//  ② **有错就不产出任何东西** —— 半成品比没有更危险（游戏能跑、数据是错的）。
//  ③ 外键的"表不存在"和"值不存在"**分开报** —— 前者是表名写错，后者是数据错，
//     混成一句话会让人往错的方向查。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NBC.ConfigKit
{
    /// <summary>一张表：**结构**（schema）+ **原文**（raw）配成对。</summary>
    public sealed class ConfigTable
    {
        /// <summary>构造。</summary>
        /// <param name="schema">结构。</param>
        /// <param name="raw">原文。</param>
        public ConfigTable(TableSchema schema, RawTable raw)
        {
            Schema = schema;
            Raw = raw;
        }

        /// <summary>结构。</summary>
        public TableSchema Schema { get; }

        /// <summary>原文。</summary>
        public RawTable Raw { get; }
    }

    /// <summary>一批表（一次导出的全部输入）。</summary>
    public sealed class ConfigSet
    {
        private readonly List<ConfigTable> m_tables = new List<ConfigTable>();
        private readonly Dictionary<string, ConfigTable> m_byName =
            new Dictionary<string, ConfigTable>(StringComparer.Ordinal);

        /// <summary>表。</summary>
        public IReadOnlyList<ConfigTable> Tables
        {
            get { return m_tables; }
        }

        /// <summary>加一张表。</summary>
        /// <param name="table">表。</param>
        public void Add(ConfigTable table)
        {
            if (table == null || table.Schema == null)
            {
                return;
            }

            m_tables.Add(table);
            m_byName[table.Schema.TableName] = table;
        }

        /// <summary>按表名找。</summary>
        /// <param name="tableName">表名。</param>
        /// <param name="table">表。</param>
        /// <returns>找到没有。</returns>
        public bool TryGet(string tableName, out ConfigTable table)
        {
            table = null;
            return tableName != null && m_byName.TryGetValue(tableName, out table);
        }
    }

    /// <summary>
    /// 校验引擎：把 <see cref="ConfigSet"/> 扫三趟，把问题写进 <see cref="DiagnosticBag"/>。
    /// <para>**它从不抛异常来表达"表有错"** —— 有错就是诊断，不是异常。</para>
    /// </summary>
    public sealed class ValidationEngine
    {
        private readonly ConfigPolicy m_policy;

        /// <summary>构造。</summary>
        /// <param name="policy">项目策略。</param>
        public ValidationEngine(ConfigPolicy policy)
        {
            m_policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        /// <summary>跑完整校验（三趟）。</summary>
        /// <param name="set">表集合。</param>
        /// <param name="diagnostics">诊断收集器。</param>
        public void Validate(ConfigSet set, DiagnosticBag diagnostics)
        {
            if (set == null)
            {
                throw new ArgumentNullException(nameof(set));
            }

            if (diagnostics == null)
            {
                throw new ArgumentNullException(nameof(diagnostics));
            }

            Dictionary<string, Dictionary<long, int>> keyIndex = BuildKeyIndex(set, diagnostics);

            for (int i = 0; i < set.Tables.Count; i++)
            {
                ConfigTable table = set.Tables[i];

                if (table.Schema.HasFatalProblem)
                {
                    // 结构都不成立，再校验数据只会产生一堆连锁误报，把真正的错埋掉
                    continue;
                }

                ValidateValues(table, diagnostics);
                ValidateUniqueness(table, diagnostics);
                ValidateForeignKeys(table, keyIndex, diagnostics);
            }
        }

        // ====================================================================
        //  主键索引（趟 C 要用；顺便在趟 A 之前把主键本身的问题挡掉）
        // ====================================================================

        /// <summary>建"表名 → 主键值 → 首次出现的 Excel 行号"索引。</summary>
        private Dictionary<string, Dictionary<long, int>> BuildKeyIndex(ConfigSet set, DiagnosticBag diagnostics)
        {
            Dictionary<string, Dictionary<long, int>> index =
                new Dictionary<string, Dictionary<long, int>>(StringComparer.Ordinal);

            for (int i = 0; i < set.Tables.Count; i++)
            {
                ConfigTable table = set.Tables[i];
                ColumnSchema key = table.Schema.Key;

                if (key == null || key.Type == null || key.Type.Kind != TypeKind.Int)
                {
                    continue;
                }

                Dictionary<long, int> ids = new Dictionary<long, int>();
                index[table.Schema.TableName] = ids;

                IReadOnlyList<RawRow> rows = table.Raw.Rows;

                for (int r = table.Schema.FirstDataRow - 1; r < rows.Count; r++)
                {
                    RawRow row = rows[r];

                    if (row.IsBlank)
                    {
                        continue;
                    }

                    string text = row.Get(key.Index, row.ExcelRow).Text.Trim();

                    if (text.Length == 0)
                    {
                        continue;
                    }

                    long value;

                    if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        continue;
                    }

                    int firstRow;

                    if (ids.TryGetValue(value, out firstRow))
                    {
                        // ⚠️ 主键重复：**报错指出两行**（第一行在说明里，本行是位置）
                        diagnostics.Error(DiagnosticCodes.DuplicateKey,
                            table.Raw.LocationOf(row.Get(key.Index, row.ExcelRow), key.Name),
                            "主键重复：`" + key.Name + "` = " + value.ToString(CultureInfo.InvariantCulture) +
                            " 已经在第 " + firstRow.ToString(CultureInfo.InvariantCulture) + " 行出现过",
                            "全表唯一",
                            text);
                    }
                    else
                    {
                        ids.Add(value, row.ExcelRow);
                    }
                }
            }

            return index;
        }

        // ====================================================================
        //  趟 A：逐格（空值 / 写法 / 类型 / 数值规则）
        // ====================================================================

        /// <summary>逐格校验。</summary>
        private void ValidateValues(ConfigTable table, DiagnosticBag diagnostics)
        {
            IReadOnlyList<RawRow> rows = table.Raw.Rows;
            IReadOnlyList<ColumnSchema> columns = table.Schema.Columns;

            for (int r = table.Schema.FirstDataRow - 1; r < rows.Count; r++)
            {
                RawRow row = rows[r];

                if (row.IsBlank)
                {
                    continue;
                }

                for (int c = 0; c < columns.Count; c++)
                {
                    ColumnSchema column = columns[c];

                    if (column.Type == null)
                    {
                        continue;
                    }

                    RawCell cell = row.Get(column.Index, row.ExcelRow);
                    SourceLocation location = table.Raw.LocationOf(cell, column.Name);

                    if ((cell.Flags & RawCellFlags.Formula) != 0)
                    {
                        diagnostics.Error(DiagnosticCodes.UnsupportedCell, location,
                            "不允许公式。工具读到的是公式缓存值，Excel 一改就变，导出的结果不可复现",
                            "一个字面值",
                            cell.Text);
                        continue;
                    }

                    if ((cell.Flags & RawCellFlags.Merged) != 0)
                    {
                        diagnostics.Error(DiagnosticCodes.UnsupportedCell, location,
                            "不允许合并单元格（\"这一格的值\"会变成\"哪一格？\"，解析结果取决于读取方式）",
                            "普通单元格",
                            cell.Text);
                        continue;
                    }

                    string text = cell.Text.Trim();

                    if (text.Length == 0)
                    {
                        if (!column.Type.Nullable)
                        {
                            diagnostics.Error(DiagnosticCodes.UnexpectedEmpty, location,
                                "这一格是空的，但这个字段**不允许为空**" +
                                "（空着会被当成 0 / 空串 / false，那是个看起来正常的假值）",
                                column.TypeText + "（要允许空请写成 " + column.TypeText + m_policy.NullableSuffix + "）",
                                "<空>");
                        }

                        continue;
                    }

                    if (IsRejectedPlaceholder(text))
                    {
                        diagnostics.Error(DiagnosticCodes.BadEmptyPlaceholder, location,
                            "`" + text + "` 看着像\"没有\"，但它其实是个**字符串**，会被当成真实数据。" +
                            "要表示\"无\"，请**留空**",
                            "留空（什么都不写）",
                            text);
                        continue;
                    }

                    long intValue;
                    double numberValue;

                    if (!TryParseValue(column, text, location, diagnostics, out intValue, out numberValue))
                    {
                        continue;
                    }

                    // ⚠️ 主键必须是正数：`0` 在 C# 里是 int 的默认值，
                    //    允许 0 就分不清"忘了赋值"和"赋成了 0"（Docs\17 §五）
                    if (column.IsKey && intValue < m_policy.MinKeyValue)
                    {
                        diagnostics.Error(DiagnosticCodes.OutOfRange, location,
                            "主键必须大于等于 " + m_policy.MinKeyValue.ToString(CultureInfo.InvariantCulture) +
                            "（0 与负数保留：`0` 是 int 的默认值，允许它就分不清「忘了赋值」和「赋成了 0」）",
                            column.TypeText + "，>= " + m_policy.MinKeyValue.ToString(CultureInfo.InvariantCulture),
                            text);
                        continue;
                    }

                    ApplyNumericRules(table, column, cell, text, intValue, numberValue, location, diagnostics);
                }
            }
        }

        /// <summary>按类型解析一个值；失败时写诊断并返回 false。</summary>
        private bool TryParseValue(ColumnSchema column, string text, SourceLocation location,
                                   DiagnosticBag diagnostics, out long intValue, out double numberValue)
        {
            intValue = 0L;
            numberValue = 0d;

            switch (column.Type.Kind)
            {
                case TypeKind.String:
                    return true;

                case TypeKind.Int:
                case TypeKind.Long:
                case TypeKind.Ref:
                    if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
                    {
                        diagnostics.Error(DiagnosticCodes.ValueParseFailed, location,
                            "不是合法的整数", column.TypeText, text);
                        return false;
                    }

                    numberValue = intValue;

                    if (column.Type.Kind == TypeKind.Int &&
                        (intValue < int.MinValue || intValue > int.MaxValue))
                    {
                        diagnostics.Error(DiagnosticCodes.OutOfRange, location,
                            "超出 int 的范围（-2147483648 ~ 2147483647）。真的需要这么大就用 `long`",
                            "int 范围内",
                            text);
                        return false;
                    }

                    return true;

                case TypeKind.Float:
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out numberValue))
                    {
                        diagnostics.Error(DiagnosticCodes.ValueParseFailed, location,
                            "不是合法的数字（小数点要用 `.`，不要写千分位逗号）", column.TypeText, text);
                        return false;
                    }

                    return true;

                case TypeKind.Bool:
                    if (IsTrue(text))
                    {
                        intValue = 1L;
                        numberValue = 1d;
                        return true;
                    }

                    if (IsFalse(text))
                    {
                        intValue = 0L;
                        numberValue = 0d;
                        return true;
                    }

                    diagnostics.Error(DiagnosticCodes.ValueParseFailed, location,
                        "布尔只能写 `true` / `false` / `1` / `0`（不接受 是/否/Y/N）",
                        "true / false / 1 / 0", text);
                    return false;

                case TypeKind.Enum:
                    if (!IsIdentifier(text))
                    {
                        diagnostics.Error(DiagnosticCodes.ValueParseFailed, location,
                            "枚举要写**成员名**（不是数字），且必须是合法标识符", column.TypeText, text);
                        return false;
                    }

                    ISet<string> members;

                    if (m_policy.KnownEnumMembers != null &&
                        m_policy.KnownEnumMembers.TryGetValue(column.Type.EnumName ?? string.Empty, out members) &&
                        members != null && !members.Contains(text))
                    {
                        diagnostics.Error(DiagnosticCodes.ValueParseFailed, location,
                            "`" + column.Type.EnumName + "` 里没有成员 `" + text + "`",
                            string.Join(" / ", ToArray(members)), text);
                        return false;
                    }

                    return true;

                case TypeKind.IntArray:
                case TypeKind.RefArray:
                    return TryParseArray(column, text, location, diagnostics);

                default:
                    return true;
            }
        }

        /// <summary>解析数组（逗号分隔）；只检查"每一项是不是合法整数"。</summary>
        private bool TryParseArray(ColumnSchema column, string text, SourceLocation location,
                                   DiagnosticBag diagnostics)
        {
            string[] parts = text.Split(',');

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();

                if (part.Length == 0)
                {
                    diagnostics.Error(DiagnosticCodes.ValueParseFailed, location,
                        "数组里有空项（逗号写多了？）",
                        "形如 1,2,3",
                        text);
                    return false;
                }

                long value;

                if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    diagnostics.Error(DiagnosticCodes.ValueParseFailed, location,
                        "数组的第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 项不是合法整数",
                        "形如 1,2,3",
                        part);
                    return false;
                }
            }

            return true;
        }

        /// <summary>数值规则：`range` / `min` / `max` / `len`。</summary>
        private static void ApplyNumericRules(ConfigTable table, ColumnSchema column, RawCell cell, string text,
                                              long intValue, double numberValue, SourceLocation location,
                                              DiagnosticBag diagnostics)
        {
            RuleSpec range = column.GetRule("range");

            if (range != null && column.Type.IsNumeric)
            {
                double min, max;

                if (TryArg(range, 0, out min) && TryArg(range, 1, out max) &&
                    (numberValue < min || numberValue > max))
                {
                    diagnostics.Error(DiagnosticCodes.OutOfRange, location,
                        "超出允许范围", range.Source, text);
                }
            }

            RuleSpec minRule = column.GetRule("min");

            if (minRule != null && column.Type.IsNumeric)
            {
                double min;

                if (TryArg(minRule, 0, out min) && numberValue < min)
                {
                    diagnostics.Error(DiagnosticCodes.OutOfRange, location,
                        "小于允许的最小值", minRule.Source, text);
                }
            }

            RuleSpec maxRule = column.GetRule("max");

            if (maxRule != null && column.Type.IsNumeric)
            {
                double max;

                if (TryArg(maxRule, 0, out max) && numberValue > max)
                {
                    diagnostics.Error(DiagnosticCodes.OutOfRange, location,
                        "大于允许的最大值", maxRule.Source, text);
                }
            }

            RuleSpec len = column.GetRule("len");

            if (len != null && column.Type.Kind == TypeKind.String)
            {
                double min, max;

                if (TryArg(len, 0, out min) && TryArg(len, 1, out max) &&
                    (text.Length < min || text.Length > max))
                {
                    diagnostics.Error(DiagnosticCodes.LengthOutOfRange, location,
                        "字符串长度超出范围（按**字符数**算，不是字节数）", len.Source, text);
                }
            }
        }

        // ====================================================================
        //  趟 B：逐列（unique）
        // ====================================================================

        /// <summary>`unique` 列的全表唯一性。</summary>
        private static void ValidateUniqueness(ConfigTable table, DiagnosticBag diagnostics)
        {
            IReadOnlyList<ColumnSchema> columns = table.Schema.Columns;

            for (int c = 0; c < columns.Count; c++)
            {
                ColumnSchema column = columns[c];

                if (!column.HasRule("unique") || column.Type == null)
                {
                    continue;
                }

                Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
                IReadOnlyList<RawRow> rows = table.Raw.Rows;

                for (int r = table.Schema.FirstDataRow - 1; r < rows.Count; r++)
                {
                    RawRow row = rows[r];

                    if (row.IsBlank)
                    {
                        continue;
                    }

                    RawCell cell = row.Get(column.Index, row.ExcelRow);
                    string text = cell.Text.Trim();

                    if (text.Length == 0)
                    {
                        continue;
                    }

                    int firstRow;

                    if (seen.TryGetValue(text, out firstRow))
                    {
                        diagnostics.Error(DiagnosticCodes.NotUnique, table.Raw.LocationOf(cell, column.Name),
                            "`unique` 冲突：`" + text + "` 已经在第 " +
                            firstRow.ToString(CultureInfo.InvariantCulture) + " 行出现过",
                            "unique", text);
                    }
                    else
                    {
                        seen.Add(text, row.ExcelRow);
                    }
                }
            }
        }

        // ====================================================================
        //  趟 C：跨表（外键）
        // ====================================================================

        /// <summary>外键存在性。</summary>
        private void ValidateForeignKeys(ConfigTable table, Dictionary<string, Dictionary<long, int>> keyIndex,
                                        DiagnosticBag diagnostics)
        {
            IReadOnlyList<ColumnSchema> columns = table.Schema.Columns;
            IReadOnlyList<RawRow> rows = table.Raw.Rows;

            for (int c = 0; c < columns.Count; c++)
            {
                ColumnSchema column = columns[c];

                if (column.Type == null ||
                    (column.Type.Kind != TypeKind.Ref && column.Type.Kind != TypeKind.RefArray))
                {
                    continue;
                }

                string refTable = column.Type.RefTable;
                Dictionary<long, int> ids;

                if (!keyIndex.TryGetValue(refTable ?? string.Empty, out ids))
                {
                    // ⚠️ "表不存在" 与 "值不存在" 分开报：前者是表名写错，后者是数据错
                    diagnostics.Error(DiagnosticCodes.ForeignKeyMissing,
                        table.Raw.LocationOf(rows.Count > 0 ? rows[0].Get(column.Index, rows[0].ExcelRow) : null, column.Name),
                        "引用的表 `" + refTable + "` 不存在（或它没有可用的 int 主键）",
                        "ref:已存在的表名",
                        column.TypeText);
                    continue;
                }

                for (int r = table.Schema.FirstDataRow - 1; r < rows.Count; r++)
                {
                    RawRow row = rows[r];

                    if (row.IsBlank)
                    {
                        continue;
                    }

                    RawCell cell = row.Get(column.Index, row.ExcelRow);
                    string text = cell.Text.Trim();

                    if (text.Length == 0)
                    {
                        continue;
                    }

                    string[] parts = text.Split(',');

                    for (int p = 0; p < parts.Length; p++)
                    {
                        string part = parts[p].Trim();
                        long value;

                        if (part.Length == 0 ||
                            !long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                        {
                            continue;   // 解析失败已经在趟 A 报过了
                        }

                        if (!ids.ContainsKey(value))
                        {
                            diagnostics.Error(DiagnosticCodes.ForeignKeyMissing,
                                table.Raw.LocationOf(cell, column.Name),
                                "外键指向 `" + refTable + "` 的 " + value.ToString(CultureInfo.InvariantCulture) +
                                "，但那边没有这个主键",
                                "ref:" + refTable + " 里存在的 id",
                                part);
                        }
                    }
                }
            }
        }

        // ====================================================================
        //  小工具
        // ====================================================================

        /// <summary>是不是策略里点名的"假空值写法"。</summary>
        private bool IsRejectedPlaceholder(string text)
        {
            string[] placeholders = m_policy.RejectedEmptyPlaceholders;

            if (placeholders == null)
            {
                return false;
            }

            for (int i = 0; i < placeholders.Length; i++)
            {
                if (string.Equals(placeholders[i], text, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>取规则的数值参数。</summary>
        private static bool TryArg(RuleSpec rule, int index, out double value)
        {
            value = 0d;

            if (rule == null || rule.Args == null || index >= rule.Args.Count)
            {
                return false;
            }

            return double.TryParse(rule.Args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>`true` 的写法。</summary>
        private static bool IsTrue(string text)
        {
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1";
        }

        /// <summary>`false` 的写法。</summary>
        private static bool IsFalse(string text)
        {
            return string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0";
        }

        /// <summary>像不像标识符（枚举成员名）。</summary>
        private static bool IsIdentifier(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (!char.IsLetter(text[0]) && text[0] != '_')
            {
                return false;
            }

            for (int i = 1; i < text.Length; i++)
            {
                if (!char.IsLetterOrDigit(text[i]) && text[i] != '_')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>集合转数组（拼报错信息用，netstandard2.1 没有现成的）。</summary>
        private static string[] ToArray(ISet<string> set)
        {
            string[] result = new string[set.Count];
            set.CopyTo(result, 0);
            return result;
        }
    }
}
