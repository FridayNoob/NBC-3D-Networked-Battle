// ============================================================================
//  ConfigKit · 接缝④ 的另一个实现：制表符文本（TSV，给 Unity 导入器读）
//  对应：Docs\17-配置表规范.md §九（生成物）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么是 TSV 而不是 JSON（这是本轮**改掉**的设计）
//  ---------------------------------------------------------------------------
//  原本打算发 JSON 让 Unity 用 `JsonUtility` 读。写之前想清楚两个坑：
//
//    ① **Unity 的序列化器不支持可空值类型**（`int?`）——
//       而本规范 §六 明确允许 `ref:Hero?` 这类可空字段。
//    ② **`JsonUtility` 里 enum 是按整数走的**，可枚举定义在 C# 里，
//       **工具并不知道成员对应的数值**，只能写成员名 → JsonUtility 装不进去。
//
//  而且这两条我**在本机无法实测**（没有 Unity）。**赌一个自己验不了的行为**
//  正是本项目最反对的事。
//
//  换成 TSV 之后这两个问题都不存在了：
//    · Unity 侧只要 `string.Split('\t')`，**不需要任何解析库**
//    · 枚举/可空/数组由**生成的显式加载器**逐个字段处理（`Enum.Parse`、空→0、`1,2`→数组）
//    · 而 TSV 的**文本**我能在这里逐字断言 —— **可验证**
//
//  一句话：**TSV 是"表里到底写了什么"的无损交接**（工具内部本来就是文本），
//  类型转换放在生成的加载器里做，规则与 `CSharpConfigEmitter.LiteralOf` 一致。
//
//  ---------------------------------------------------------------------------
//  一个必须处理的细节：转义
//  ---------------------------------------------------------------------------
//  单元格里如果**真的**有制表符或换行，直接写进 TSV 会让**整行列错位** ——
//  又一个静默错位。所以按 `\` / `\t` / `\r` / `\n` 四个字符转义，加载器反向还原。
//  （CSV 来源允许引号里带换行，所以这是真实可能发生的情况。）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace NBC.ConfigKit
{
    /// <summary>
    /// 生成 `<表名>.tsv`：**表头一行字段名 + 数据行**，供 Unity 侧生成的加载器读。
    /// <para>表头用字段名而不是列序号 —— 这样**改列顺序不会错位**。</para>
    /// </summary>
    public sealed class TsvConfigEmitter : ITableEmitter
    {
        /// <summary>单元格里的制表符。</summary>
        public const char Delimiter = '\t';

        /// <summary>名字。</summary>
        public string Name
        {
            get { return "tsv"; }
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
                new EmittedFile(table.Schema.TableName + ".tsv", EmitTsv(table))
            };
        }

        /// <summary>把一张表写成 TSV 文本。</summary>
        /// <param name="table">表。</param>
        /// <returns>TSV 文本（**用 `\n`，不用 `\r\n`**，W4）。</returns>
        public static string EmitTsv(ConfigTable table)
        {
            TableSchema schema = table.Schema;
            StringBuilder builder = new StringBuilder();

            // 表头：字段名（只写**参与生成**的列，顺序与 schema 一致）
            for (int c = 0; c < schema.Columns.Count; c++)
            {
                if (c > 0)
                {
                    builder.Append(Delimiter);
                }

                builder.Append(schema.Columns[c].Name);
            }

            builder.Append('\n');

            IReadOnlyList<RawRow> rows = table.Raw.Rows;

            for (int r = schema.FirstDataRow - 1; r < rows.Count; r++)
            {
                RawRow row = rows[r];

                if (row.IsBlank)
                {
                    continue;   // 空行不写（流水线保证：有错时根本不会走到这里）
                }

                for (int c = 0; c < schema.Columns.Count; c++)
                {
                    if (c > 0)
                    {
                        builder.Append(Delimiter);
                    }

                    ColumnSchema column = schema.Columns[c];
                    RawCell cell = row.Get(column.Index, row.ExcelRow);

                    builder.Append(Escape(cell.Text.Trim()));
                }

                builder.Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>转义：让单元格里的制表符/换行不会把行拆散（否则会**静默错位**）。</summary>
        /// <param name="text">原文。</param>
        /// <returns>转义后的文本。</returns>
        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(text.Length);

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];

                switch (ch)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\n': builder.Append("\\n"); break;
                    default: builder.Append(ch); break;
                }
            }

            return builder.ToString();
        }
    }
}
