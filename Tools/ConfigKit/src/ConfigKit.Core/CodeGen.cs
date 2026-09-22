// ============================================================================
//  ConfigKit · 接缝④ 的实现：代码生成（C#）
//  对应：Docs\17-配置表规范.md §九（生成物与目录）、Docs\01 §10.5 的 CFG-02 / CFG-03 / CFG-08
//
//  ---------------------------------------------------------------------------
//  ⚠️ 生成器**相信校验已经过了**（这是一个明确的前提，不是疏忽）
//  ---------------------------------------------------------------------------
//  它把单元格原文直接写成 C# 字面量，**不自己再解析一遍**。
//  理由：再解析一遍就是"同一个判断存在两处"，而本项目已经吃过这个亏
//  （见 §4.7：架构守卫的坑我踩了两次）。
//
//  代价：**调用方必须先确认 `!diagnostics.HasErrors`**。
//  `ExportPipeline` 就是这么做的；直接调用本类的人要自己保证。
//
//  ---------------------------------------------------------------------------
//  一条顺手做掉的小事：给生成的注释消毒
//  ---------------------------------------------------------------------------
//  W1 规定 `///` 注释里不许出现连续两个连字符（XML 文档注释的硬性规定）。
//  而策划写在注释行里的字**完全可能**有 `--`。
//  所以生成时把它换成一个长破折号 —— **生成器要保证自己产出的东西是合法的**，
//  不能要求"策划以后别写 `--`"。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NBC.ConfigKit
{
    /// <summary>一个生成出来的文件（**还没有落盘**）。</summary>
    public sealed class EmittedFile
    {
        /// <summary>构造。</summary>
        /// <param name="relativePath">相对输出目录的路径。</param>
        /// <param name="content">内容。</param>
        public EmittedFile(string relativePath, string content)
        {
            RelativePath = relativePath;
            Content = content;
        }

        /// <summary>相对输出目录的路径。</summary>
        public string RelativePath { get; }

        /// <summary>内容。</summary>
        public string Content { get; }
    }

    /// <summary>生成选项。</summary>
    public sealed class EmitOptions
    {
        /// <summary>生成代码的命名空间。</summary>
        public string Namespace = "NBC.Game.Config";

        /// <summary>生成器名字（写进文件头，方便人知道是谁生成的）。</summary>
        public string GeneratorName = "ConfigKit";

        /// <summary>输出目录（写进文件头的相对路径，仅供人看）。</summary>
        public string OutputDirectoryHint = "Assets/_Project/Game/Config/Generated";
    }

    /// <summary>接缝④ 的一半：**把一张表变成文件**。换实现就换产物格式。</summary>
    public interface ITableEmitter
    {
        /// <summary>名字（命令行里选实现用）。</summary>
        string Name { get; }

        /// <summary>生成这张表对应的文件。</summary>
        /// <param name="table">表。</param>
        /// <param name="options">选项。</param>
        /// <returns>生成出来的文件。</returns>
        IReadOnlyList<EmittedFile> Emit(ConfigTable table, EmitOptions options);
    }

    /// <summary>
    /// 生成 C#：每张表两个文件 —— 行类型 `Config_&lt;表&gt;.cs` + SO 类型 `&lt;表&gt;Config.cs`。
    /// </summary>
    public sealed class CSharpConfigEmitter : ITableEmitter
    {
        /// <summary>名字。</summary>
        public string Name
        {
            get { return "csharp"; }
        }

        /// <summary>生成。</summary>
        /// <param name="table">表。</param>
        /// <param name="options">选项。</param>
        /// <returns>两个文件。</returns>
        public IReadOnlyList<EmittedFile> Emit(ConfigTable table, EmitOptions options)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            EmitOptions actual = options ?? new EmitOptions();
            string tableName = table.Schema.TableName;

            return new List<EmittedFile>
            {
                new EmittedFile("Config_" + tableName + ".cs", EmitRowType(table, actual)),
                new EmittedFile(tableName + "Config.cs", EmitScriptableObject(table, actual))
            };
        }

        // ====================================================================
        //  行类型
        // ====================================================================

        /// <summary>生成行类型（一组字段 + 注释）。</summary>
        private static string EmitRowType(ConfigTable table, EmitOptions options)
        {
            TableSchema schema = table.Schema;
            StringBuilder builder = new StringBuilder();

            AppendHeader(builder, options, schema.TableName, "行类型");

            builder.AppendLine("using System;");
            builder.AppendLine();
            builder.Append("namespace ").AppendLine(options.Namespace);
            builder.AppendLine("{");
            builder.Append("    /// <summary>").Append(schema.TableName).AppendLine(" 表的一行（对应一行数据）。</summary>");
            builder.AppendLine("    [Serializable]");
            builder.Append("    public sealed class Config_").Append(schema.TableName).AppendLine();
            builder.AppendLine("    {");

            for (int i = 0; i < schema.Columns.Count; i++)
            {
                ColumnSchema column = schema.Columns[i];

                builder.Append("        public ").Append(CSharpTypeOf(column.Type)).Append(' ')
                       .Append(column.Name).Append(";  // ")
                       .Append(Sanitize(column.Comment))
                       .Append("（").Append(Sanitize(column.TypeText)).Append("）")
                       .Append(NullableHint(column))
                       .AppendLine();
            }

            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        // ====================================================================
        //  SO 类型
        // ====================================================================

        /// <summary>生成 SO 类型（行列表 + 主键索引）。</summary>
        private static string EmitScriptableObject(ConfigTable table, EmitOptions options)
        {
            TableSchema schema = table.Schema;
            ColumnSchema key = schema.Key;
            string tableName = schema.TableName;
            string rowType = "Config_" + tableName;
            string soType = tableName + "Config";

            StringBuilder builder = new StringBuilder();

            AppendHeader(builder, options, tableName, "配置资产");

            builder.AppendLine("using System.Collections.Generic;");
            builder.AppendLine("using UnityEngine;");
            builder.AppendLine();
            builder.Append("namespace ").AppendLine(options.Namespace);
            builder.AppendLine("{");
            builder.Append("    /// <summary>").Append(tableName).AppendLine(" 表的配置资产。</summary>");
            builder.Append("    [CreateAssetMenu(fileName = \"").Append(soType)
                   .Append("\", menuName = \"配置/").Append(tableName).AppendLine("\")]");
            builder.Append("    public sealed class ").Append(soType).AppendLine(" : ScriptableObject");
            builder.AppendLine("    {");
            builder.Append("        public List<").Append(rowType).Append("> rows = new List<").Append(rowType)
                   .AppendLine(">();  // 全部数据行");

            if (key != null)
            {
                builder.Append("        private Dictionary<int, ").Append(rowType)
                       .AppendLine("> m_index;  // 主键索引（不序列化，加载时重建）");
                builder.AppendLine();
                builder.AppendLine("        /// <summary>Unity 加载资产时重建索引。</summary>");
                builder.AppendLine("        private void OnEnable()");
                builder.AppendLine("        {");
                builder.AppendLine("            RebuildIndex();");
                builder.AppendLine("        }");
                builder.AppendLine();
                builder.AppendLine("        /// <summary>重建主键索引（数据变动后手动调）。</summary>");
                builder.AppendLine("        public void RebuildIndex()");
                builder.AppendLine("        {");
                builder.Append("            m_index = new Dictionary<int, ").Append(rowType).Append(">(")
                       .Append("rows == null ? 0 : rows.Count);").AppendLine();
                builder.AppendLine();
                builder.AppendLine("            if (rows == null)");
                builder.AppendLine("            {");
                builder.AppendLine("                return;");
                builder.AppendLine("            }");
                builder.AppendLine();
                builder.AppendLine("            for (int i = 0; i < rows.Count; i++)");
                builder.AppendLine("            {");
                builder.Append("                m_index[rows[i].").Append(key.Name).AppendLine("] = rows[i];");
                builder.AppendLine("            }");
                builder.AppendLine("        }");
                builder.AppendLine();
                builder.AppendLine("        /// <summary>按主键取一行；取不到抛异常。</summary>");
                builder.Append("        public ").Append(rowType).AppendLine(" Get(int id)");
                builder.AppendLine("        {");
                builder.Append("            ").Append(rowType).AppendLine(" row;");
                builder.AppendLine();
                builder.AppendLine("            if (m_index == null)");
                builder.AppendLine("            {");
                builder.AppendLine("                RebuildIndex();");
                builder.AppendLine("            }");
                builder.AppendLine();
                builder.AppendLine("            if (!m_index.TryGetValue(id, out row))");
                builder.AppendLine("            {");
                builder.Append("                throw new KeyNotFoundException(\"[").Append(soType)
                       .Append("] 没有主键 \"").Append(" + id);").AppendLine();
                builder.AppendLine("            }");
                builder.AppendLine();
                builder.AppendLine("            return row;");
                builder.AppendLine("        }");
                builder.AppendLine();
                builder.AppendLine("        /// <summary>试着按主键取一行（配置缺失时用它，别用异常控流程）。</summary>");
                builder.Append("        public bool TryGet(int id, out ").Append(rowType).AppendLine(" row)");
                builder.AppendLine("        {");
                builder.AppendLine("            if (m_index == null)");
                builder.AppendLine("            {");
                builder.AppendLine("                RebuildIndex();");
                builder.AppendLine("            }");
                builder.AppendLine();
                builder.AppendLine("            return m_index.TryGetValue(id, out row);");
                builder.AppendLine("        }");
            }

            AppendTsvLoader(builder, schema, rowType);

            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        /// <summary>生成"从 TSV 文本填数据"的加载器（Editor 导入器用它）。</summary>
        private static void AppendTsvLoader(StringBuilder builder, TableSchema schema, string rowType)
        {
            builder.AppendLine();
            builder.AppendLine("        /// <summary>从 ConfigKit 生成的 .tsv 文本填充数据（Editor 导入器用；第一行是字段名）。</summary>");
            builder.AppendLine("        public void LoadFromTsv(string text)");
            builder.AppendLine("        {");
            builder.AppendLine("            rows.Clear();");
            builder.AppendLine();
            builder.AppendLine("            if (string.IsNullOrEmpty(text))");
            builder.AppendLine("            {");
            builder.AppendLine("                RebuildIndex();");
            builder.AppendLine("                return;");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            string[] lines = text.Replace(\"\\r\\n\", \"\\n\").Replace('\\r', '\\n').Split('\\n');");
            builder.AppendLine("            string[] header = lines[0].Split('\\t');");
            builder.AppendLine();
            builder.AppendLine("            for (int i = 1; i < lines.Length; i++)");
            builder.AppendLine("            {");
            builder.AppendLine("                if (lines[i].Length == 0)");
            builder.AppendLine("                {");
            builder.AppendLine("                    continue;");
            builder.AppendLine("                }");
            builder.AppendLine();
            builder.AppendLine("                string[] cells = lines[i].Split('\\t');");
            builder.Append("                ").Append(rowType).AppendLine(" row = new " + rowType + "();");

            for (int c = 0; c < schema.Columns.Count; c++)
            {
                ColumnSchema column = schema.Columns[c];
                builder.Append("                row.").Append(column.Name).Append(" = ")
                       .Append(AssignExpression(column))
                       .AppendLine(";");
            }

            builder.AppendLine();
            builder.AppendLine("                rows.Add(row);");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            RebuildIndex();");
            builder.AppendLine("        }");
            builder.AppendLine();

            AppendLoaderHelpers(builder);
        }

        /// <summary>某个字段的赋值表达式（按类型选解析函数）。</summary>
        private static string AssignExpression(ColumnSchema column)
        {
            string cell = "Cell(cells, header, \"" + column.Name + "\")";

            switch (column.Type.Kind)
            {
                case TypeKind.String:
                    return "Unescape(" + cell + ")";

                case TypeKind.Int:
                case TypeKind.Ref:
                    return "ParseInt(" + cell + ")";

                case TypeKind.Long:
                    return "ParseLong(" + cell + ")";

                case TypeKind.Float:
                    return "ParseFloat(" + cell + ")";

                case TypeKind.Bool:
                    return "ParseBool(" + cell + ")";

                case TypeKind.Enum:
                    return "(" + column.Type.EnumName + ")System.Enum.Parse(typeof(" + column.Type.EnumName +
                           "), " + cell + ", true)";

                case TypeKind.IntArray:
                case TypeKind.RefArray:
                    return "ParseIntArray(" + cell + ")";

                default:
                    return cell;
            }
        }

        /// <summary>生成加载器用到的小工具函数（每个 SO 各自一份，保持"生成物自足"）。</summary>
        private static void AppendLoaderHelpers(StringBuilder builder)
        {
            builder.AppendLine("        /// <summary>按字段名取单元格（按名字对列，所以调换列顺序不会错位）。</summary>");
            builder.AppendLine("        private static string Cell(string[] cells, string[] header, string name)");
            builder.AppendLine("        {");
            builder.AppendLine("            for (int i = 0; i < header.Length; i++)");
            builder.AppendLine("            {");
            builder.AppendLine("                if (header[i] == name)");
            builder.AppendLine("                {");
            builder.AppendLine("                    return i < cells.Length ? cells[i] : string.Empty;");
            builder.AppendLine("                }");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            return string.Empty;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        /// <summary>解析 int（失败给 0）。</summary>");
            builder.AppendLine("        private static int ParseInt(string text)");
            builder.AppendLine("        {");
            builder.AppendLine("            int value;");
            builder.AppendLine("            return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : 0;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        /// <summary>解析 long（失败给 0）。</summary>");
            builder.AppendLine("        private static long ParseLong(string text)");
            builder.AppendLine("        {");
            builder.AppendLine("            long value;");
            builder.AppendLine("            return long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : 0L;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        /// <summary>解析 float（失败给 0）。</summary>");
            builder.AppendLine("        private static float ParseFloat(string text)");
            builder.AppendLine("        {");
            builder.AppendLine("            float value;");
            builder.AppendLine("            return float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : 0f;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        /// <summary>解析 bool（`1` / `true`）。</summary>");
            builder.AppendLine("        private static bool ParseBool(string text)");
            builder.AppendLine("        {");
            builder.AppendLine("            return text == \"1\" || string.Equals(text, \"true\", System.StringComparison.OrdinalIgnoreCase);");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        /// <summary>解析逗号分隔的整数数组。</summary>");
            builder.AppendLine("        private static int[] ParseIntArray(string text)");
            builder.AppendLine("        {");
            builder.AppendLine("            if (string.IsNullOrEmpty(text))");
            builder.AppendLine("            {");
            builder.AppendLine("                return new int[0];");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            string[] parts = text.Split(',');");
            builder.AppendLine("            int[] result = new int[parts.Length];");
            builder.AppendLine();
            builder.AppendLine("            for (int i = 0; i < parts.Length; i++)");
            builder.AppendLine("            {");
            builder.AppendLine("                result[i] = ParseInt(parts[i].Trim());");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            return result;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        /// <summary>还原 TSV 的转义。</summary>");
            builder.AppendLine("        private static string Unescape(string text)");
            builder.AppendLine("        {");
            builder.AppendLine("            if (string.IsNullOrEmpty(text) || text.IndexOf('\\\\') < 0)");
            builder.AppendLine("            {");
            builder.AppendLine("                return text;");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            System.Text.StringBuilder builder = new System.Text.StringBuilder(text.Length);");
            builder.AppendLine();
            builder.AppendLine("            for (int i = 0; i < text.Length; i++)");
            builder.AppendLine("            {");
            builder.AppendLine("                if (text[i] != '\\\\' || i + 1 >= text.Length)");
            builder.AppendLine("                {");
            builder.AppendLine("                    builder.Append(text[i]);");
            builder.AppendLine("                    continue;");
            builder.AppendLine("                }");
            builder.AppendLine();
            builder.AppendLine("                i++;");
            builder.AppendLine();
            builder.AppendLine("                switch (text[i])");
            builder.AppendLine("                {");
            builder.AppendLine("                    case 't': builder.Append('\\t'); break;");
            builder.AppendLine("                    case 'r': builder.Append('\\r'); break;");
            builder.AppendLine("                    case 'n': builder.Append('\\n'); break;");
            builder.AppendLine("                    case '\\\\': builder.Append('\\\\'); break;");
            builder.AppendLine("                    default: builder.Append(text[i]); break;");
            builder.AppendLine("                }");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            return builder.ToString();");
            builder.AppendLine("        }");
        }

        // ====================================================================
        //  字面量与类型
        // ====================================================================

        /// <summary>把一个单元格原文写成 C# 字面量（**前提：校验已通过**）。</summary>
        /// <param name="column">列。</param>
        /// <param name="text">原文。</param>
        /// <returns>C# 字面量文本。</returns>
        public static string LiteralOf(ColumnSchema column, string text)
        {
            string value = text == null ? string.Empty : text.Trim();

            if (value.Length == 0)
            {
                // 只有可空字段才可能空着（校验保证了这一点）
                // ⚠️ 映射必须和 CSharpTypeOf 一致：可空 ref 生成的是 `int` / `int[]`，
                //    所以"无引用"是 **0** / 空数组，而不是 null
                if (column.Type.Kind == TypeKind.Ref)
                {
                    return "0";
                }

                if (column.Type.Kind == TypeKind.RefArray)
                {
                    return "new int[0]";
                }

                return "null";
            }

            switch (column.Type.Kind)
            {
                case TypeKind.String:
                    return "\"" + Escape(value) + "\"";

                case TypeKind.Bool:
                    return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                        ? "true"
                        : "false";

                case TypeKind.Float:
                    return value + "f";

                case TypeKind.Enum:
                    return column.Type.EnumName + "." + value;

                case TypeKind.IntArray:
                case TypeKind.RefArray:
                    return ArrayLiteral(value);

                default:
                    // int / long / ref
                    return value;
            }
        }

        /// <summary>
        /// 字段的 C# 类型名。
        /// <para>
        /// ⚠️ **可空一律不加 `?`**（连 `string?` 也不加）：Unity 的序列化器
        /// **不支持可空值类型**，而生成的类型必须能被 Unity 序列化。
        /// 可空的语义由"约定"承担，并写在字段注释里：
        /// </para>
        /// <list type="bullet">
        /// <item><description><c>ref:</c> / <c>ref:[]</c> 可空 → `int` / `int[]`，**`0` 表示无引用**（安全：主键 ≥ 1）</description></item>
        /// <item><description><c>string?</c> → `string`，`null` 就是 `null`</description></item>
        /// <item><description>其它类型的可空**会被 <see cref="SchemaReader"/> 当错误拦下**（`CFG0019`）</description></item>
        /// </list>
        /// </summary>
        /// <param name="type">类型。</param>
        /// <returns>C# 类型名。</returns>
        public static string CSharpTypeOf(ColumnType type)
        {
            string core;

            switch (type.Kind)
            {
                case TypeKind.Int:
                case TypeKind.Ref:
                    core = "int";
                    break;

                case TypeKind.Long:
                    core = "long";
                    break;

                case TypeKind.Float:
                    core = "float";
                    break;

                case TypeKind.Bool:
                    core = "bool";
                    break;

                case TypeKind.String:
                    core = "string";
                    break;

                case TypeKind.Enum:
                    core = type.EnumName;
                    break;

                case TypeKind.IntArray:
                case TypeKind.RefArray:
                    return "int[]";     // 数组本身就可以是 null，不需要再加 `?`

                default:
                    core = "object";
                    break;
            }

            // 刻意不加 `?`：见方法注释（Unity 不支持可空值类型）
            return core;
        }

        /// <summary>字段注释里的可空约定提示（没有可空就返回空串）。</summary>
        /// <param name="column">列。</param>
        /// <returns>提示文本。</returns>
        private static string NullableHint(ColumnSchema column)
        {
            if (column.Type == null || !column.Type.Nullable)
            {
                return string.Empty;
            }

            if (column.Type.Kind == TypeKind.Ref || column.Type.Kind == TypeKind.RefArray)
            {
                return "，**0 表示无引用**（主键永远 >= 1，所以 0 不会被误认）";
            }

            if (column.Type.Kind == TypeKind.String)
            {
                return "，可为 null";
            }

            return string.Empty;
        }

        /// <summary>把 `1,2,3` 写成 `new int[] { 1, 2, 3 }`。</summary>
        private static string ArrayLiteral(string value)
        {
            string[] parts = value.Split(',');
            StringBuilder builder = new StringBuilder("new int[] { ");

            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(parts[i].Trim());
            }

            builder.Append(" }");
            return builder.ToString();
        }

        /// <summary>C# 字符串转义。</summary>
        private static string Escape(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length + 8);

            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];

                switch (ch)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default: builder.Append(ch); break;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// 注释消毒：把连续两个连字符换成一个长破折号。
        /// <para>⚠️ W1：`///` 里出现连续两个连字符会让 XML 注释不合法。</para>
        /// </summary>
        internal static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("--", "—");
        }

        /// <summary>文件头（`<auto-generated>` 标记 + 来源说明）。</summary>
        private static void AppendHeader(StringBuilder builder, EmitOptions options,
                                         string tableName, string what)
        {
            builder.AppendLine("// <auto-generated />");
            builder.Append("// 由 ").Append(options.GeneratorName).Append(" 生成：")
                   .Append(tableName).Append(" 表的").Append(what).AppendLine("。");
            builder.AppendLine("// ⚠️ 手改会被下次导出覆盖。要改数据请改源表。");
            builder.AppendLine();
        }
    }
}
