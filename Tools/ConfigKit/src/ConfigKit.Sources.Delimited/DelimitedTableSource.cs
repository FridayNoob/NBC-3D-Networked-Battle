// ============================================================================
//  ConfigKit · 接缝① 的实现：分隔符文本（CSV / TSV）
//  对应：Docs\01 §10.5 CFG-10（`.csv` 来源）、Docs\17-配置表规范.md §三
//
//  ---------------------------------------------------------------------------
//  为什么先做它（而不是直接做 xlsx）
//  ---------------------------------------------------------------------------
//  2026-09-22 实测：本沙箱**还原不了任何 NuGet 包**（NU1301，无网络），
//  所以 NPOI 暂时用不了。但"**故意写错一格 → 报错定位到文件+表+行+列**"
//  是 C2 的验收标准 —— **验收标准必须能被跑一遍看到**。
//
//  于是先用零依赖的来源把全流程跑通：策划在 Excel 里"另存为 CSV（UTF-8）"即可。
//  `Docs\01` 的 CFG-10 本来就把 `.csv` 列进了路线图，只是它从"顺便支持"变成"先支持"。
//
//  ---------------------------------------------------------------------------
//  一个刻意的取舍：**行 = 行，不做多行单元格**
//  ---------------------------------------------------------------------------
//  CSV 标准允许单元格里带换行（用引号包起来）。本实现**支持引号包裹**，
//  但**行号一律按物理行算** —— 因为报错要说"第 12 行"，而那个 12 必须是
//  **策划在 Excel / 记事本里看到的行号**。多行单元格会让这个数字失去意义。
//  真需要多行文本的场景，用它自己的表（或换 xlsx 来源）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NBC.ConfigKit.Sources
{
    /// <summary>分隔符类型。</summary>
    public enum DelimiterKind
    {
        /// <summary>逗号（`.csv`）。</summary>
        Comma = 0,

        /// <summary>制表符（`.tsv`）。</summary>
        Tab = 1
    }

    /// <summary>
    /// 从目录里读 `.csv` / `.tsv`，每个文件一张表，**文件名（去扩展名）= 表名**。
    /// <para>
    /// ⚠️ 这里和 xlsx 来源有个差别：xlsx 里"文件名"与"sheet 名"是两个东西，
    /// 而 CSV 没有 sheet 概念，所以约定 **文件名 = 表名**。
    /// </para>
    /// </summary>
    public sealed class DelimitedTableSource : ITableSource
    {
        private readonly string m_directory;
        private readonly DelimiterKind m_delimiter;
        private readonly string m_searchPattern;

        /// <summary>构造。</summary>
        /// <param name="directory">目录。</param>
        /// <param name="delimiter">分隔符（默认按扩展名自动判断）。</param>
        public DelimitedTableSource(string directory, DelimiterKind? delimiter = null)
        {
            m_directory = directory ?? throw new ArgumentNullException(nameof(directory));
            m_delimiter = delimiter ?? DelimiterKind.Comma;
            m_searchPattern = delimiter == DelimiterKind.Tab ? "*.tsv" : "*.csv";
        }

        /// <summary>来源描述。</summary>
        public string Description
        {
            get { return "分隔符文本目录：" + m_directory; }
        }

        /// <summary>
        /// 读出目录下的全部表。
        /// <para>⚠️ 单个文件读不动**不中断整批**：把问题写进诊断，继续读下一个。</para>
        /// </summary>
        /// <param name="diagnostics">诊断收集器。</param>
        /// <returns>表集合。</returns>
        public IReadOnlyList<RawTable> ReadAll(DiagnosticBag diagnostics)
        {
            if (diagnostics == null)
            {
                throw new ArgumentNullException(nameof(diagnostics));
            }

            List<RawTable> tables = new List<RawTable>();

            if (!Directory.Exists(m_directory))
            {
                diagnostics.Error(DiagnosticCodes.Source,
                    new SourceLocation(m_directory, "<目录>", SourceLocation.NoRow, SourceLocation.NoColumn, null),
                    "目录不存在", "一个存在的目录", m_directory);
                return tables;
            }

            string[] files = Directory.GetFiles(m_directory, m_searchPattern);
            Array.Sort(files, StringComparer.Ordinal);   // 顺序稳定 → 输出可复现

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                string tableName = Path.GetFileNameWithoutExtension(file);
                string display = Path.GetFileName(file);

                try
                {
                    tables.Add(ReadFile(file, display, tableName));
                }
                catch (IOException exception)
                {
                    diagnostics.Error(DiagnosticCodes.Source,
                        new SourceLocation(display, tableName, SourceLocation.NoRow, SourceLocation.NoColumn, null),
                        "读文件失败：" + exception.Message, "可读的文本文件", display);
                }
                catch (UnauthorizedAccessException exception)
                {
                    diagnostics.Error(DiagnosticCodes.Source,
                        new SourceLocation(display, tableName, SourceLocation.NoRow, SourceLocation.NoColumn, null),
                        "没有权限读文件：" + exception.Message, "可读的文本文件", display);
                }
            }

            return tables;
        }

        /// <summary>读一个文件成一张表。</summary>
        /// <param name="file">完整路径。</param>
        /// <param name="display">给人看的文件名。</param>
        /// <param name="tableName">表名。</param>
        /// <returns>表。</returns>
        public RawTable ReadFile(string file, string display, string tableName)
        {
            RawTable table = new RawTable(display, tableName);

            // ⚠️ 按**物理行**读，并且**显式 UTF-8**：
            //    中文注释 + 默认编码 = 乱码，而乱码不会报错（W3）
            string[] lines = File.ReadAllLines(file, new UTF8Encoding(false));

            for (int i = 0; i < lines.Length; i++)
            {
                RawRow row = new RawRow { ExcelRow = i + 1 };
                IReadOnlyList<string> cells = SplitLine(lines[i]);

                for (int c = 0; c < cells.Count; c++)
                {
                    row.Add(new RawCell(cells[c], i + 1, c + 1));
                }

                table.Add(row);
            }

            return table;
        }

        /// <summary>
        /// 拆一行。**支持双引号包裹**（引号内允许分隔符，`""` 表示一个引号）。
        /// </summary>
        /// <param name="line">行文本。</param>
        /// <returns>单元格。</returns>
        public IReadOnlyList<string> SplitLine(string line)
        {
            List<string> cells = new List<string>();

            if (line == null)
            {
                cells.Add(string.Empty);
                return cells;
            }

            char separator = m_delimiter == DelimiterKind.Tab ? '\t' : ',';
            StringBuilder current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];

                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        // `""` 表示一个字面引号
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(ch);
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inQuotes = true;
                    continue;
                }

                if (ch == separator)
                {
                    cells.Add(current.ToString());
                    current.Length = 0;
                    continue;
                }

                current.Append(ch);
            }

            cells.Add(current.ToString());
            return cells;
        }

        /// <summary>把表名转成"文件名 = 表名"的默认文件名（方便生成侧统一）。</summary>
        /// <param name="tableName">表名。</param>
        /// <returns>文件名。</returns>
        public string FileNameFor(string tableName)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}.{1}",
                tableName, m_delimiter == DelimiterKind.Tab ? "tsv" : "csv");
        }
    }
}
