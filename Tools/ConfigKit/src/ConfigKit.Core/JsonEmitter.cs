// ============================================================================
//  ConfigKit · 接缝④ 的第三个实现：JSON
//  对应：Docs\17-配置表规范.md §九（生成物）
//
//  ---------------------------------------------------------------------------
//  它给谁用（**不是给 Unity 导入器用的**）
//  ---------------------------------------------------------------------------
//  导入器吃的是 `<表>.tsv`（原因见 TsvEmitter 的文件头：Unity 的 `JsonUtility`
//  不支持可空值类型、enum 按整数走，而这两条我在本机验不了）。
//
//  JSON 这份产物解决的是**别的**诉求：
//    · **可移植**：外部工具 / 别的语言 / 别的引擎都能读
//    · **可读**：人直接看得懂，评审时不用先解释 TSV 是什么
//    · **可扩展**：以后配置里出现嵌套结构（`Vector3`、子表、参数字典）时，
//      扁平的 TSV 装不下，JSON 装得下
//
//  📌 **三种边界的格式选择**（面试可以直接讲）：
//      网络协议        → 二进制（本项目用 Google.Protobuf）
//      运行时存档/设置 → JSON
//      **构建期"表→资产"交接** → TSV（导入器）/ JSON（对外）**两份都出**
//
//  ---------------------------------------------------------------------------
//  一个刻意的设计：**字段名当键**（而不是数组下标）
//  ---------------------------------------------------------------------------
//  和 TSV 的表头一样，JSON 也用字段名当键 —— 于是**表里调换列顺序不会错位**，
//  而且这份 JSON 自己说明了每个数是什么。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 空值的三份产物必须**给出同一个答案**
//  ---------------------------------------------------------------------------
//  同一张表，C# 字段 / TSV / JSON 对"没填的可空 ref"必须说同一件事，否则
//  换一份产物读就会得到不同的值 —— 那是**静默不一致**。
//  约定（三处一致，有自测钉住）：
//      · 可空 `ref:`        → C# `int`、TSV 空串、JSON **`0`**（0 = 无引用，主键永远 ≥ 1）
//      · 可空 `string`      → C# `string`、TSV 空串、JSON **`null`**
//      · 可空 `ref:[]`      → C# `int[]`、TSV 空串、JSON **`[]`**
//  ==========================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NBC.ConfigKit
{
    /// <summary>
    /// 生成 `<表名>.json`：字段名当键，供**外部工具 / 别的语言 / 人**读。
    /// <para>⚠️ Unity 导入器吃的是 TSV（原因见 <see cref="TsvConfigEmitter"/> 的文件头）。</para>
    /// </summary>
    public sealed class JsonConfigEmitter : ITableEmitter
    {
        /// <summary>缩进（生成物要入库，**给人看的 diff 比体积重要**）。</summary>
        private const string Indent = "  ";

        /// <summary>名字。</summary>
        public string Name
        {
            get { return "json"; }
        }

        /// <summary>生成。</summary>
        /// <param name="table">表。</param>
        /// <param name="options">选项。</param>
        /// <returns>一个文件。</returns>
        public IReadOnlyList<EmittedFile> Emit(ConfigTable table, EmitOptions options)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            return new List<EmittedFile>
            {
                new EmittedFile(table.Schema.TableName + ".json", EmitJson(table))
            };
        }

        /// <summary>把一张表写成 JSON 文本。</summary>
        /// <param name="table">表。</param>
        /// <returns>JSON 文本。</returns>
        public static string EmitJson(ConfigTable table)
        {
            TableSchema schema = table.Schema;
            StringBuilder builder = new StringBuilder();

            builder.Append("{\n");
            builder.Append(Indent).Append("\"table\": ").Append(Quote(schema.TableName)).Append(",\n");

            // 字段名清单：**顺序也在里面**，方便消费者校验自己认不认得这张表
            builder.Append(Indent).Append("\"fields\": [");
            for (int c = 0; c < schema.Columns.Count; c++)
            {
                if (c > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(Quote(schema.Columns[c].Name));
            }

            builder.Append("],\n");

            builder.Append(Indent).Append("\"rows\": [\n");

            IReadOnlyList<RawRow> rows = table.Raw.Rows;
            bool first = true;

            for (int r = schema.FirstDataRow - 1; r < rows.Count; r++)
            {
                RawRow row = rows[r];

                if (row.IsBlank)
                {
                    continue;   // 空行不写（有错时流水线根本不会走到这里）
                }

                if (!first)
                {
                    builder.Append(",\n");
                }

                first = false;

                builder.Append(Indent).Append(Indent).Append("{ ");

                for (int c = 0; c < schema.Columns.Count; c++)
                {
                    if (c > 0)
                    {
                        builder.Append(", ");
                    }

                    ColumnSchema column = schema.Columns[c];
                    RawCell cell = row.Get(column.Index, row.ExcelRow);

                    builder.Append(Quote(column.Name)).Append(": ").Append(ValueOf(column, cell.Text));
                }

                builder.Append(" }");
            }

            if (!first)
            {
                builder.Append('\n');
            }

            builder.Append(Indent).Append("]\n");
            builder.Append("}\n");

            return builder.ToString();
        }

        // ====================================================================
        //  取值
        // ====================================================================

        /// <summary>
        /// 把一个单元格原文写成 JSON 值（**前提：校验已通过**）。
        /// <para>空值的三份产物约定见文件头。</para>
        /// </summary>
        /// <param name="column">列。</param>
        /// <param name="rawText">原文。</param>
        /// <returns>JSON 值文本。</returns>
        public static string ValueOf(ColumnSchema column, string rawText)
        {
            string text = rawText == null ? string.Empty : rawText.Trim();

            if (text.Length == 0)
            {
                switch (column.Type.Kind)
                {
                    case TypeKind.Ref:
                        return "0";             // 与 C# 的 `int` + 加载器一致
                    case TypeKind.RefArray:
                        return "[]";
                    case TypeKind.IntArray:
                        return "[]";
                    default:
                        return "null";          // string 可空 → null（与 C# 的 string 一致）
                }
            }

            switch (column.Type.Kind)
            {
                case TypeKind.String:
                case TypeKind.Enum:
                    // ⚠️ 枚举写**成员名**（人类可读）。工具并不知道成员的数值，
                    //    所以消费者要做一次名字→值的转换（生成的加载器就是这么做的）。
                    return Quote(text);

                case TypeKind.Bool:
                    return text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                        ? "true"
                        : "false";

                case TypeKind.Float:
                    return text;

                case TypeKind.IntArray:
                case TypeKind.RefArray:
                    return ArrayValue(text);

                default:
                    // int / long / ref
                    return text;
            }
        }

        /// <summary>把 `1,2,3` 写成 `[1, 2, 3]`。</summary>
        private static string ArrayValue(string text)
        {
            string[] parts = text.Split(',');
            StringBuilder builder = new StringBuilder("[ ");

            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(parts[i].Trim());
            }

            builder.Append(" ]");
            return builder.ToString();
        }

        /// <summary>
        /// JSON 字符串转义。
        /// <para>转义 `"` `\` 与所有控制字符（含换行/制表符）；**中文原样输出**（合法且可读）。</para>
        /// </summary>
        /// <param name="text">原文。</param>
        /// <returns>带引号的 JSON 字符串。</returns>
        public static string Quote(string text)
        {
            StringBuilder builder = new StringBuilder((text == null ? 0 : text.Length) + 2);
            builder.Append('"');

            if (!string.IsNullOrEmpty(text))
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char ch = text[i];

                    switch (ch)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;

                        default:
                            if (ch < ' ')
                            {
                                builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                builder.Append(ch);
                            }

                            break;
                    }
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}
