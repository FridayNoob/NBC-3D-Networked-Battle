// ============================================================================
//  ConfigKit · 类型系统 + 表头解析（SchemaReader）
//  对应：Docs\17-配置表规范.md §三（五行表头）§四（类型系统）§五（主键）§七（规则语法）
//
//  ---------------------------------------------------------------------------
//  这一层的职责：把"一堆文本格子"变成"有类型的表结构"
//  ---------------------------------------------------------------------------
//      RawTable（纯文本）  ──SchemaReader──►  TableSchema（列名/类型/规则/主键）
//
//  它**只做结构**，不碰数据值 —— "第 12 行的 hp 是不是 0" 是 ValidationEngine 的事。
//  分开的理由：结构错了要一次说清（少一行表头、类型拼错），
//  而结构不对的时候去校验数据只会产生一堆**连锁误报**，把真正的错埋掉。
//
//  ⚠️ 规则名不认识时**报错，不忽略**（`CFG0005`）：
//     `ranage(1,100)` 拼错如果被忽略，这条校验就**静默消失**了，表照样导出成功 ——
//     这正是本项目最讨厌的那类失败。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NBC.ConfigKit
{
    /// <summary>字段的类型种类。</summary>
    public enum TypeKind
    {
        /// <summary>不认识（解析失败时的兜底，**不该出现在正常的 schema 里**）。</summary>
        Unknown = 0,

        /// <summary>32 位整数。</summary>
        Int = 1,

        /// <summary>64 位整数。</summary>
        Long = 2,

        /// <summary>单精度浮点（**默认只允许表现层**，见 <see cref="FloatPolicy"/>）。</summary>
        Float = 3,

        /// <summary>布尔。</summary>
        Bool = 4,

        /// <summary>字符串。</summary>
        String = 5,

        /// <summary>枚举（成员名，不是数字）。</summary>
        Enum = 6,

        /// <summary>整数数组（逗号分隔）。</summary>
        IntArray = 7,

        /// <summary>外键（指向另一张表的主键）。</summary>
        Ref = 8,

        /// <summary>外键数组。</summary>
        RefArray = 9
    }

    /// <summary>字段类型：种类 + 可空 + 数组 + 枚举名 / 外键表名。</summary>
    public sealed class ColumnType
    {
        private ColumnType(TypeKind kind, bool nullable, string enumName, string refTable)
        {
            Kind = kind;
            Nullable = nullable;
            EnumName = enumName;
            RefTable = refTable;
        }

        /// <summary>种类。</summary>
        public TypeKind Kind { get; }

        /// <summary>可空（类型后面带 `?`）。</summary>
        public bool Nullable { get; }

        /// <summary>枚举类型名（<see cref="TypeKind.Enum"/> 时非空）。</summary>
        public string EnumName { get; }

        /// <summary>被引用的表名（<see cref="TypeKind.Ref"/> / <see cref="TypeKind.RefArray"/> 时非空）。</summary>
        public string RefTable { get; }

        /// <summary>是不是数组（<see cref="TypeKind.IntArray"/> / <see cref="TypeKind.RefArray"/>）。</summary>
        public bool IsArray
        {
            get { return Kind == TypeKind.IntArray || Kind == TypeKind.RefArray; }
        }

        /// <summary>是不是数值（用于 `range` / `min` / `max`）。</summary>
        public bool IsNumeric
        {
            get { return Kind == TypeKind.Int || Kind == TypeKind.Long || Kind == TypeKind.Float; }
        }

        /// <summary>
        /// 解析类型写法。**不认识的写法返回 false 并给出人话原因**。
        /// </summary>
        /// <param name="text">写法（如 `ref:Hero[]?`）。</param>
        /// <param name="type">解析结果。</param>
        /// <param name="error">失败原因。</param>
        /// <returns>是否解析成功。</returns>
        public static bool TryParse(string text, out ColumnType type, out string error)
        {
            type = null;
            error = null;

            if (string.IsNullOrEmpty(text))
            {
                error = "类型是空的";
                return false;
            }

            string work = text.Trim();
            bool nullable = false;

            if (work.EndsWith("?", StringComparison.Ordinal))
            {
                nullable = true;
                work = work.Substring(0, work.Length - 1).Trim();
            }

            bool isArray = false;
            if (work.EndsWith("[]", StringComparison.Ordinal))
            {
                isArray = true;
                work = work.Substring(0, work.Length - 2).Trim();
            }

            switch (work)
            {
                case "int":
                    type = new ColumnType(isArray ? TypeKind.IntArray : TypeKind.Int, nullable, null, null);
                    return true;

                case "long":
                    if (isArray)
                    {
                        error = "`long[]` 暂不支持（数组只支持 `int[]` 与 `ref:表名[]`）";
                        return false;
                    }

                    type = new ColumnType(TypeKind.Long, nullable, null, null);
                    return true;

                case "float":
                    if (isArray)
                    {
                        error = "`float[]` 暂不支持（数组只支持 `int[]` 与 `ref:表名[]`）";
                        return false;
                    }

                    type = new ColumnType(TypeKind.Float, nullable, null, null);
                    return true;

                case "bool":
                    type = new ColumnType(TypeKind.Bool, nullable, null, null);
                    return true;

                case "string":
                    type = new ColumnType(TypeKind.String, nullable, null, null);
                    return true;
            }

            if (work.StartsWith("enum:", StringComparison.Ordinal))
            {
                string enumName = work.Substring(5).Trim();

                if (enumName.Length == 0)
                {
                    error = "`enum:` 后面要写枚举类型名";
                    return false;
                }

                if (isArray)
                {
                    error = "`enum:...[]` 暂不支持";
                    return false;
                }

                type = new ColumnType(TypeKind.Enum, nullable, enumName, null);
                return true;
            }

            if (work.StartsWith("ref:", StringComparison.Ordinal))
            {
                string refTable = work.Substring(4).Trim();

                if (refTable.Length == 0)
                {
                    error = "`ref:` 后面要写被引用的表名";
                    return false;
                }

                type = new ColumnType(isArray ? TypeKind.RefArray : TypeKind.Ref, nullable, null, refTable);
                return true;
            }

            error = "只支持 int / long / float / bool / string / enum:名 / int[] / ref:表名 / ref:表名[]（可加 `?`）";
            return false;
        }

        /// <summary>写回规范里的写法（报错时显示"期望"用）。</summary>
        public override string ToString()
        {
            string core;

            switch (Kind)
            {
                case TypeKind.Int: core = "int"; break;
                case TypeKind.Long: core = "long"; break;
                case TypeKind.Float: core = "float"; break;
                case TypeKind.Bool: core = "bool"; break;
                case TypeKind.String: core = "string"; break;
                case TypeKind.Enum: core = "enum:" + EnumName; break;
                case TypeKind.IntArray: core = "int[]"; break;
                case TypeKind.Ref: core = "ref:" + RefTable; break;
                case TypeKind.RefArray: core = "ref:" + RefTable + "[]"; break;
                default: core = "unknown"; break;
            }

            return Nullable ? core + "?" : core;
        }
    }

    /// <summary>一条校验规则（`range(1,999)` / `key` / `unique`）。</summary>
    public sealed class RuleSpec
    {
        /// <summary>构造。</summary>
        /// <param name="name">规则名。</param>
        /// <param name="args">参数（已去空白）。</param>
        /// <param name="source">原始写法（报错时原样回显）。</param>
        public RuleSpec(string name, IReadOnlyList<string> args, string source)
        {
            Name = name;
            Args = args;
            Source = source;
        }

        /// <summary>规则名。</summary>
        public string Name { get; }

        /// <summary>参数。</summary>
        public IReadOnlyList<string> Args { get; }

        /// <summary>原始写法。</summary>
        public string Source { get; }

        /// <summary>
        /// 把一个格子的规则文本拆成规则列表。
        /// <para>多个规则用 `;` 分隔；空段忽略；`name` 或 `name(a,b)` 两种形状。</para>
        /// </summary>
        /// <param name="text">规则文本。</param>
        /// <returns>规则列表。</returns>
        public static IReadOnlyList<RuleSpec> ParseAll(string text)
        {
            List<RuleSpec> result = new List<RuleSpec>();

            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            string[] segments = text.Split(';');

            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i].Trim();

                if (segment.Length == 0)
                {
                    continue;
                }

                int open = segment.IndexOf('(');

                if (open < 0)
                {
                    result.Add(new RuleSpec(segment, new string[0], segment));
                    continue;
                }

                int close = segment.LastIndexOf(')');

                if (close < open)
                {
                    // 括号没闭合：整段当一个"名字不认识"的规则交出去，让上层报错
                    result.Add(new RuleSpec(segment, new string[0], segment));
                    continue;
                }

                string name = segment.Substring(0, open).Trim();
                string body = segment.Substring(open + 1, close - open - 1);

                string[] rawArgs = body.Length == 0 ? new string[0] : body.Split(',');
                List<string> args = new List<string>(rawArgs.Length);

                for (int a = 0; a < rawArgs.Length; a++)
                {
                    args.Add(rawArgs[a].Trim());
                }

                result.Add(new RuleSpec(name, args, segment));
            }

            return result;
        }
    }

    /// <summary>一列（字段）。</summary>
    public sealed class ColumnSchema
    {
        /// <summary>构造。</summary>
        /// <param name="index">列下标（0 基）。</param>
        /// <param name="name">字段名。</param>
        /// <param name="comment">中文注释。</param>
        /// <param name="typeText">类型原文。</param>
        /// <param name="type">解析出来的类型；失败时 null。</param>
        /// <param name="rules">规则列表。</param>
        public ColumnSchema(int index, string name, string comment, string typeText,
                            ColumnType type, IReadOnlyList<RuleSpec> rules)
        {
            Index = index;
            Name = name;
            Comment = comment;
            TypeText = typeText;
            Type = type;
            Rules = rules;
            IsKey = HasRule("key");
        }

        /// <summary>列下标（0 基）。</summary>
        public int Index { get; }

        /// <summary>字段名。</summary>
        public string Name { get; }

        /// <summary>中文注释。</summary>
        public string Comment { get; }

        /// <summary>类型原文。</summary>
        public string TypeText { get; }

        /// <summary>解析出来的类型（失败时 null）。</summary>
        public ColumnType Type { get; }

        /// <summary>规则列表。</summary>
        public IReadOnlyList<RuleSpec> Rules { get; }

        /// <summary>是不是主键（写了 `key` 规则）。</summary>
        public bool IsKey { get; }

        /// <summary>有没有某条规则。</summary>
        /// <param name="ruleName">规则名。</param>
        /// <returns>有没有。</returns>
        public bool HasRule(string ruleName)
        {
            if (Rules == null)
            {
                return false;
            }

            for (int i = 0; i < Rules.Count; i++)
            {
                if (string.Equals(Rules[i].Name, ruleName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>取某条规则；没有则 null。</summary>
        /// <param name="ruleName">规则名。</param>
        /// <returns>规则。</returns>
        public RuleSpec GetRule(string ruleName)
        {
            if (Rules == null)
            {
                return null;
            }

            for (int i = 0; i < Rules.Count; i++)
            {
                if (string.Equals(Rules[i].Name, ruleName, StringComparison.Ordinal))
                {
                    return Rules[i];
                }
            }

            return null;
        }
    }

    /// <summary>一张表的结构。</summary>
    public sealed class TableSchema
    {
        private readonly List<ColumnSchema> m_columns = new List<ColumnSchema>();

        /// <summary>构造。</summary>
        /// <param name="tableName">表名。</param>
        /// <param name="fileName">文件名。</param>
        /// <param name="sheetName">工作表名。</param>
        public TableSchema(string tableName, string fileName, string sheetName)
        {
            TableName = tableName;
            FileName = fileName;
            SheetName = sheetName;
        }

        /// <summary>表名。</summary>
        public string TableName { get; }

        /// <summary>文件名。</summary>
        public string FileName { get; }

        /// <summary>工作表名。</summary>
        public string SheetName { get; }

        /// <summary>列。</summary>
        public IReadOnlyList<ColumnSchema> Columns
        {
            get { return m_columns; }
        }

        /// <summary>主键列；没找到或不合格时 null。</summary>
        public ColumnSchema Key { get; internal set; }

        /// <summary>数据起始行（Excel 真实行号）。</summary>
        public int FirstDataRow { get; internal set; }

        /// <summary>结构本身有没有致命问题（有就不该继续校验数据）。</summary>
        public bool HasFatalProblem { get; internal set; }

        /// <summary>加一列。</summary>
        /// <param name="column">列。</param>
        public void AddColumn(ColumnSchema column)
        {
            m_columns.Add(column);
        }

        /// <summary>找一列。</summary>
        /// <param name="name">字段名。</param>
        /// <returns>列；没有则 null。</returns>
        public ColumnSchema FindColumn(string name)
        {
            for (int i = 0; i < m_columns.Count; i++)
            {
                if (string.Equals(m_columns[i].Name, name, StringComparison.Ordinal))
                {
                    return m_columns[i];
                }
            }

            return null;
        }
    }

    /// <summary>表头解析器：`RawTable`（纯文本） → `TableSchema`（有类型）。</summary>
    public sealed class SchemaReader
    {
        private readonly ConfigPolicy m_policy;

        /// <summary>构造。</summary>
        /// <param name="policy">项目策略。</param>
        public SchemaReader(ConfigPolicy policy)
        {
            m_policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        /// <summary>
        /// 解析一张表的表头。
        /// <para>结构有致命问题时仍然返回 schema（并置 <see cref="TableSchema.HasFatalProblem"/>），
        /// 这样上层能一次性看到所有结构问题，而不是"修一个冒一个"。</para>
        /// </summary>
        /// <param name="table">原始表。</param>
        /// <param name="diagnostics">诊断收集器。</param>
        /// <returns>表结构。</returns>
        public TableSchema Read(RawTable table, DiagnosticBag diagnostics)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            if (diagnostics == null)
            {
                throw new ArgumentNullException(nameof(diagnostics));
            }

            TableSchema schema = new TableSchema(table.SheetName, table.FileName, table.SheetName)
            {
                FirstDataRow = m_policy.FirstDataRow
            };

            CheckTableName(schema, diagnostics);

            // 表头四行必须都在
            if (table.Rows.Count < m_policy.HeaderRuleRow)
            {
                schema.HasFatalProblem = true;
                diagnostics.Error(DiagnosticCodes.HeaderStructure, table.LocationAt(table.Rows.Count),
                    "表头不足 " + m_policy.HeaderRuleRow.ToString(CultureInfo.InvariantCulture) +
                    " 行（字段名 / 注释 / 类型 / 规则各一行）",
                    "至少 " + m_policy.HeaderRuleRow.ToString(CultureInfo.InvariantCulture) + " 行表头",
                    "实际 " + table.Rows.Count.ToString(CultureInfo.InvariantCulture) + " 行");
                return schema;
            }

            RawRow fieldRow = table.GetRow(m_policy.HeaderFieldRow - 1);
            RawRow commentRow = table.GetRow(m_policy.HeaderCommentRow - 1);
            RawRow typeRow = table.GetRow(m_policy.HeaderTypeRow - 1);
            RawRow ruleRow = table.GetRow(m_policy.HeaderRuleRow - 1);

            int keyCount = 0;

            for (int c = 0; c < fieldRow.Cells.Count; c++)
            {
                RawCell fieldCell = fieldRow.Get(c, m_policy.HeaderFieldRow);
                string rawName = fieldCell.Text.Trim();

                // 空列：字段名到头了。右边还有东西就是"中间断了一列"（会静默丢数据）
                if (rawName.Length == 0)
                {
                    if (HasContentAfter(fieldRow, c))
                    {
                        schema.HasFatalProblem = true;
                        diagnostics.Error(DiagnosticCodes.BrokenColumnHeader, table.LocationOf(fieldCell, null),
                            "字段名在中间断了：这一列空着，右边却还有字段。" +
                            "（继续解析会把右边的列**静默丢掉**，所以这里直接拦下）",
                            "字段名必须连续",
                            "<空>");
                    }

                    break;
                }

                // `#` 开头 = 整列忽略（策划临时停用一列）
                if (rawName.StartsWith(m_policy.IgnorePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!Regex.IsMatch(rawName, m_policy.FieldNamePattern))
                {
                    diagnostics.Error(DiagnosticCodes.Naming, table.LocationOf(fieldCell, rawName),
                        "字段名不符合规范（小驼峰英文，禁止中文 / 空格 / 大写开头）",
                        m_policy.FieldNamePattern,
                        rawName);
                }

                string comment = commentRow.Get(c, m_policy.HeaderCommentRow).Text.Trim();
                if (comment.Length == 0)
                {
                    // ⚠️ 这里刻意是**警告**而不是错误：注释内容工具不解析，
                    //    缺注释不影响数据正确性，不该拦住导出（Docs\17 §8.3：警告不阻止导出）。
                    diagnostics.Warning(DiagnosticCodes.HeaderStructure, table.LocationOf(fieldCell, rawName),
                        "这一列没有中文注释。半年后没人知道它的单位/含义（工具不解析注释，但要求它存在）",
                        "一个中文注释",
                        "<空>");
                }

                string typeText = typeRow.Get(c, m_policy.HeaderTypeRow).Text.Trim();
                ColumnType type = null;

                if (!ColumnType.TryParse(typeText, out type, out string typeError))
                {
                    schema.HasFatalProblem = true;
                    diagnostics.Error(DiagnosticCodes.UnknownType, table.LocationOf(typeRow.Get(c, m_policy.HeaderTypeRow), rawName),
                        "类型写法无法识别：" + typeError,
                        "int / long / float / bool / string / enum:名 / int[] / ref:表名 / ref:表名[]（可加 `?`）",
                        string.IsNullOrEmpty(typeText) ? "<空>" : typeText);
                }

                IReadOnlyList<RuleSpec> rules = RuleSpec.ParseAll(ruleRow.Get(c, m_policy.HeaderRuleRow).Text);
                ColumnSchema column = new ColumnSchema(c, rawName, comment, typeText, type, rules);

                CheckRules(table, column, typeRow, ruleRow, diagnostics);
                CheckFloatPolicy(table, column, typeRow, diagnostics);

                if (column.IsKey)
                {
                    keyCount++;
                }

                schema.AddColumn(column);
            }

            ResolveKey(schema, table, fieldRow, keyCount, diagnostics);
            CheckBlankRowInMiddle(table, diagnostics);

            return schema;
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>表名规范。</summary>
        private void CheckTableName(TableSchema schema, DiagnosticBag diagnostics)
        {
            if (!Regex.IsMatch(schema.TableName ?? string.Empty, m_policy.TableNamePattern))
            {
                // ⚠️ 位置**永远不传 null**：表级诊断也要有个位置，
                //    否则格式化与排序阶段会 NRE —— 报错工具自己崩掉是最差的结果。
                //    指向"字段名那一行"最有用：改表名的人就在那一行附近。
                diagnostics.Error(DiagnosticCodes.Naming,
                    new SourceLocation(schema.FileName ?? "<内存表>", schema.SheetName,
                        m_policy.HeaderFieldRow, SourceLocation.NoColumn, null),
                    "表名（工作表名）不符合规范：要**大驼峰、单数**，只允许字母数字",
                    m_policy.TableNamePattern,
                    schema.TableName);
            }
        }

        /// <summary>字段名中断后右边还有没有内容。</summary>
        private static bool HasContentAfter(RawRow row, int index)
        {
            for (int c = index + 1; c < row.Cells.Count; c++)
            {
                if (!string.IsNullOrWhiteSpace(row.Cells[c].Text))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>规则名与参数个数检查（**不认识就报错**）。</summary>
        private static void CheckRules(RawTable table, ColumnSchema column, RawRow typeRow,
                                       RawRow ruleRow, DiagnosticBag diagnostics)
        {
            for (int i = 0; i < column.Rules.Count; i++)
            {
                RuleSpec rule = column.Rules[i];
                string error = ValidationRuleCatalog.DescribeArityError(rule.Name, rule.Args.Count);

                if (error != null)
                {
                    diagnostics.Error(DiagnosticCodes.UnknownRule,
                        table.LocationOf(ruleRow.Get(column.Index, 0), column.Name),
                        error,
                        ValidationRuleCatalog.DescribeAll(),
                        rule.Source);
                }
            }
        }

        /// <summary>浮点策略检查（`float` 必须标 `view`）。</summary>
        private void CheckFloatPolicy(RawTable table, ColumnSchema column, RawRow typeRow, DiagnosticBag diagnostics)
        {
            if (column.Type == null || column.Type.Kind != TypeKind.Float)
            {
                return;
            }

            if (m_policy.Float == FloatPolicy.Allowed)
            {
                return;
            }

            if (m_policy.Float == FloatPolicy.Forbidden)
            {
                diagnostics.Error(DiagnosticCodes.FloatWithoutViewFlag,
                    table.LocationOf(typeRow.Get(column.Index, 0), column.Name),
                    "本项目的策略是**禁止使用浮点**（战斗结算要位级别确定，见 Docs\\06 §十九 D2）",
                    "整数（int / long）",
                    column.TypeText);
                return;
            }

            if (!column.HasRule(m_policy.ViewFlag))
            {
                diagnostics.Error(DiagnosticCodes.FloatWithoutViewFlag,
                    table.LocationOf(typeRow.Get(column.Index, 0), column.Name),
                    "`float` 只允许用于**表现层**数值，所以规则行必须写 `" + m_policy.ViewFlag + "` 明确声明。" +
                    "（战斗结算用的数值请改成整数 + 单位，见 Docs\\17 §4.1）",
                    m_policy.ViewFlag + "（写在规则行）",
                    "<空>");
            }
        }

        /// <summary>定位主键列。</summary>
        private void ResolveKey(TableSchema schema, RawTable table, RawRow fieldRow, int keyCount,
                               DiagnosticBag diagnostics)
        {
            if (keyCount > 1)
            {
                schema.HasFatalProblem = true;

                for (int i = 0; i < schema.Columns.Count; i++)
                {
                    if (schema.Columns[i].IsKey)
                    {
                        diagnostics.Error(DiagnosticCodes.KeyDeclaration,
                            table.LocationOf(fieldRow.Get(schema.Columns[i].Index, 0), schema.Columns[i].Name),
                            "有多个列写了 `key`。**主键只能有一个**",
                            "恰好一列写 key",
                            schema.Columns[i].Name);
                    }
                }

                return;
            }

            if (keyCount == 1)
            {
                for (int i = 0; i < schema.Columns.Count; i++)
                {
                    if (!schema.Columns[i].IsKey)
                    {
                        continue;
                    }

                    schema.Key = schema.Columns[i];

                    if (schema.Key.Type != null && schema.Key.Type.Kind != TypeKind.Int)
                    {
                        schema.HasFatalProblem = true;
                        diagnostics.Error(DiagnosticCodes.KeyDeclaration,
                            table.LocationOf(fieldRow.Get(schema.Key.Index, 0), schema.Key.Name),
                            "主键的类型必须是 `int`",
                            "int",
                            schema.Key.TypeText);
                    }

                    return;
                }

                return;
            }

            // 没写 `key`：按"字段名叫 id"推断，但报一条**警告**（能用，但不明确）
            ColumnSchema guess = schema.FindColumn(m_policy.KeyColumnName);

            if (guess == null)
            {
                schema.HasFatalProblem = true;
                diagnostics.Error(DiagnosticCodes.KeyDeclaration, table.LocationAt(m_policy.HeaderFieldRow),
                    "找不到主键：既没有列写 `key`，也没有叫 `" + m_policy.KeyColumnName + "` 的列",
                    "一列写 key（通常就是 " + m_policy.KeyColumnName + "）",
                    "<无>");
                return;
            }

            schema.Key = guess;
            diagnostics.Warning(DiagnosticCodes.KeyDeclaration,
                table.LocationOf(fieldRow.Get(guess.Index, 0), guess.Name),
                "主键是**推断**出来的（列名叫 `" + m_policy.KeyColumnName + "` 但规则行没写 `key`）。能用，但建议写上",
                "key",
                guess.GetRule("key") == null ? "<空>" : guess.GetRule("key").Source);
        }

        /// <summary>
        /// 表中间的空行：**必须报错**。
        /// <para>不报的话，后面几十行数据会被静默丢掉，而且看上去"导出成功"。</para>
        /// </summary>
        private void CheckBlankRowInMiddle(RawTable table, DiagnosticBag diagnostics)
        {
            bool sawBlank = false;

            for (int i = m_policy.FirstDataRow - 1; i < table.Rows.Count; i++)
            {
                RawRow row = table.Rows[i];

                if (row.IsBlank)
                {
                    sawBlank = true;
                    continue;
                }

                if (sawBlank)
                {
                    diagnostics.Error(DiagnosticCodes.BlankRowInMiddle, table.LocationAt(row.ExcelRow),
                        "表中间有空行。**空行之后的数据会被静默丢弃**，所以这里直接报错",
                        "数据行连续（空行只能出现在末尾）",
                        "第 " + row.ExcelRow.ToString(CultureInfo.InvariantCulture) + " 行又有数据");
                }
            }
        }
    }

    /// <summary>
    /// 内置规则目录：**名字 → 参数个数**。
    /// <para>这是"封闭集合"的落点：不在这里的名字一律报 <see cref="DiagnosticCodes.UnknownRule"/>。</para>
    /// <para>想加规则？加进这张表 + 在 <see cref="ValidationEngine"/> 里实现，**不用改表头解析**。</para>
    /// </summary>
    public static class ValidationRuleCatalog
    {
        private static readonly Dictionary<string, int> Arity =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "key", 0 },
                { "unique", 0 },
                { "min", 1 },
                { "max", 1 },
                { "range", 2 },
                { "len", 2 },
                { "view", 0 }
            };

        /// <summary>所有规则名的可读清单（报错时告诉用户有哪些）。</summary>
        /// <returns>清单文本。</returns>
        public static string DescribeAll()
        {
            List<string> names = new List<string>(Arity.Keys);
            names.Sort(StringComparer.Ordinal);
            return string.Join(" / ", names.ToArray());
        }

        /// <summary>规则名认识吗。</summary>
        /// <param name="name">规则名。</param>
        /// <returns>认识吗。</returns>
        public static bool IsKnown(string name)
        {
            return name != null && Arity.ContainsKey(name);
        }

        /// <summary>
        /// 检查"规则名 + 参数个数"是否合法；合法返回 null，否则返回人话原因。
        /// </summary>
        /// <param name="name">规则名。</param>
        /// <param name="argCount">参数个数。</param>
        /// <returns>失败原因；合法时 null。</returns>
        public static string DescribeArityError(string name, int argCount)
        {
            if (name == null)
            {
                return "规则名是空的";
            }

            int expected;

            if (!Arity.TryGetValue(name, out expected))
            {
                return "不认识的规则 `" + name + "`。**拼错的规则会被静默忽略，所以这里直接报错**";
            }

            if (expected != argCount)
            {
                return "规则 `" + name + "` 需要 " + expected.ToString(CultureInfo.InvariantCulture) +
                       " 个参数，实际给了 " + argCount.ToString(CultureInfo.InvariantCulture) + " 个";
            }

            return null;
        }
    }
}
