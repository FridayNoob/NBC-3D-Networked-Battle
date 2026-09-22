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

                builder.Append("        /// <summary>")
                       .Append(Sanitize(column.Comment))
                       .Append("（").Append(Sanitize(column.TypeText)).Append("）</summary>")
                       .AppendLine();
                builder.Append("        public ").Append(CSharpTypeOf(column.Type)).Append(' ')
                       .Append(column.Name).AppendLine(";");

                if (i < schema.Columns.Count - 1)
                {
                    builder.AppendLine();
                }
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

            AppendHeader(builder, options, tableName, "ScriptableObject（运行时载体）");

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
            builder.Append("        /// <summary>全部数据行。</summary>").AppendLine();
            builder.Append("        public List<").Append(rowType).Append("> rows = new List<").Append(rowType).AppendLine(">();");

            if (key != null)
            {
                builder.AppendLine();
                builder.AppendLine("        /// <summary>主键索引（**不序列化**，加载时重建）。</summary>");
                builder.Append("        private Dictionary<int, ").Append(rowType).AppendLine("> m_index;");
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
                builder.Append("        /// <summary>按主键取一行；取不到抛异常（**不返回 null**，"
                             + "null 会让调用点在很远的地方才崩）。</summary>").AppendLine();
                builder.AppendLine("        /// <param name=\"id\">主键。</param>");
                builder.AppendLine("        /// <returns>数据行。</returns>");
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
                builder.Append("        /// <summary>试着按主键取一行。**配置缺失时用它，别用异常控流程**。</summary>").AppendLine();
                builder.AppendLine("        /// <param name=\"id\">主键。</param>");
                builder.AppendLine("        /// <param name=\"row\">数据行。</param>");
                builder.AppendLine("        /// <returns>取到没有。</returns>");
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

            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
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

        /// <summary>字段的 C# 类型名。</summary>
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

            return type.Nullable && type.Kind != TypeKind.String ? core + "?" : core;
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
            builder.Append("// 本文件由 ").Append(options.GeneratorName)
                   .Append(" 生成（").Append(tableName).Append(" 表的").Append(what).Append("）。")
                   .AppendLine();
            builder.AppendLine("// ⚠️ 手改会被下次导出覆盖。要改数据请改源表，要改行为请改别的文件。");
            builder.AppendLine();
        }
    }
}
