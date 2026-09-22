// ============================================================================
//  ConfigKit · 接缝①：表从哪来（ITableSource）+ 中立的内存表（RawTable）
//  对应：Docs\17-配置表规范.md §三（五行表头）
//
//  ---------------------------------------------------------------------------
//  为什么要有 `RawTable` 这一层
//  ---------------------------------------------------------------------------
//  不同来源（xlsx / csv / 数据库 / 手写）读出来长得都不一样，
//  但**下游只该认识一种形状**。所以来源统一归一成：
//
//      RawTable { 文件名, 表名, 行[] }   —— 除文本外，只多带两样东西：
//          ① **Excel 真实的行号 / 列号**（报错定位靠它，见 Diagnostics.cs）
//          ② **单元格的"脏标记"**（是不是公式 / 合并单元格）
//
//  于是"校验"和"代码生成"完全不知道 NPOI 的存在，
//  测试里更是**一个文件都不需要**（用 InMemoryTableSource 摆一张表就行）。
//
//  ⚠️ 这些类型刻意保持"哑"：只存文本 + 位置 + 标记，**不做任何解析**。
//     解析与判断属于 SchemaReader / ValidationEngine（Core 的职责）。
// ============================================================================

using System;
using System.Collections.Generic;

namespace NBC.ConfigKit
{
    /// <summary>单元格的"脏标记"。**来源侧**能提供就提供，提供不了就报 <see cref="None"/>。</summary>
    [Flags]
    public enum RawCellFlags
    {
        /// <summary>普通单元格。</summary>
        None = 0,

        /// <summary>公式（`=SUM(...)`）。</summary>
        Formula = 1,

        /// <summary>合并单元格的一部分。</summary>
        Merged = 2
    }

    /// <summary>一个单元格：**值 + 位置 + 标记**。</summary>
    public sealed class RawCell
    {
        /// <summary>构造。</summary>
        /// <param name="text">原文（未解析）。null 视为空串。</param>
        /// <param name="excelRow">Excel 真实行号（从 1 开始）。</param>
        /// <param name="column">列序号（从 1 开始）。</param>
        /// <param name="flags">脏标记。</param>
        public RawCell(string text, int excelRow, int column, RawCellFlags flags = RawCellFlags.None)
        {
            Text = text ?? string.Empty;
            ExcelRow = excelRow;
            Column = column;
            Flags = flags;
        }

        /// <summary>原文（未解析）。</summary>
        public string Text { get; }

        /// <summary>Excel 真实行号（从 1 开始）。</summary>
        public int ExcelRow { get; }

        /// <summary>列序号（从 1 开始）。</summary>
        public int Column { get; }

        /// <summary>脏标记。</summary>
        public RawCellFlags Flags { get; }

        /// <summary>空着吗（**只判"什么都没写"** —— 写了 `-` 不算空，那是写法错误）。</summary>
        public bool IsBlank
        {
            get { return Text.Length == 0; }
        }
    }

    /// <summary>一行。</summary>
    public sealed class RawRow
    {
        private readonly List<RawCell> m_cells = new List<RawCell>();

        /// <summary>Excel 真实行号。</summary>
        // ⚠️ 必须是 public set：来源实现（如 ConfigKit.Sources.Delimited）在**别的程序集**里，
        //    internal 会让外部来源根本造不出表来。
        public int ExcelRow { get; set; }

        /// <summary>这一行有没有任何非空单元格。</summary>
        public bool IsBlank
        {
            get
            {
                for (int i = 0; i < m_cells.Count; i++)
                {
                    if (!m_cells[i].IsBlank)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>单元格（按加入顺序）。</summary>
        public IReadOnlyList<RawCell> Cells
        {
            get { return m_cells; }
        }

        /// <summary>取第 <paramref name="index"/> 个单元格（0 基）；越界返回空单元格。</summary>
        /// <param name="index">下标（0 基）。</param>
        /// <param name="excelRow">用于构造空单元格的行号。</param>
        /// <returns>单元格。</returns>
        public RawCell Get(int index, int excelRow)
        {
            if (index >= 0 && index < m_cells.Count)
            {
                return m_cells[index];
            }

            return new RawCell(string.Empty, excelRow, index + 1);
        }

        /// <summary>加一个单元格（来源侧用）。</summary>
        /// <param name="cell">单元格。</param>
        public void Add(RawCell cell)
        {
            m_cells.Add(cell);
        }
    }

    /// <summary>
    /// 一张表：**中立的形状**。谁来读都归一成它。
    /// <para>行号是 **Excel 真实行号**，不是下标 —— 这是"报错定位"能说人话的前提。</para>
    /// </summary>
    public sealed class RawTable
    {
        private readonly List<RawRow> m_rows = new List<RawRow>();

        /// <summary>构造。</summary>
        /// <param name="fileName">文件（相对路径，给人看）。</param>
        /// <param name="sheetName">工作表名（= 表名）。</param>
        public RawTable(string fileName, string sheetName)
        {
            FileName = fileName;
            SheetName = sheetName;
        }

        /// <summary>文件（相对路径）。</summary>
        public string FileName { get; }

        /// <summary>工作表名（= 表名）。</summary>
        public string SheetName { get; }

        /// <summary>行（按加入顺序）。</summary>
        public IReadOnlyList<RawRow> Rows
        {
            get { return m_rows; }
        }

        /// <summary>加一行（来源侧用）。</summary>
        /// <param name="row">行。</param>
        public void Add(RawRow row)
        {
            m_rows.Add(row);
        }

        /// <summary>取某一行（0 基）；越界返回 null。</summary>
        /// <param name="index">下标（0 基）。</param>
        /// <returns>行。</returns>
        public RawRow GetRow(int index)
        {
            return index >= 0 && index < m_rows.Count ? m_rows[index] : null;
        }

        /// <summary>造一个位置（表级）。</summary>
        /// <param name="excelRow">行号（0 = 不指某行）。</param>
        /// <returns>位置。</returns>
        public SourceLocation LocationAt(int excelRow)
        {
            return new SourceLocation(FileName, SheetName, excelRow, SourceLocation.NoColumn, null);
        }

        /// <summary>造一个位置（单元格级）。</summary>
        /// <param name="cell">单元格。</param>
        /// <param name="columnName">列名。</param>
        /// <returns>位置。</returns>
        public SourceLocation LocationOf(RawCell cell, string columnName)
        {
            return cell == null
                ? LocationAt(SourceLocation.NoRow)
                : new SourceLocation(FileName, SheetName, cell.ExcelRow, cell.Column, columnName);
        }
    }

    /// <summary>
    /// 接缝①：**表从哪来**。
    /// <para>
    /// 实现：`DelimitedTableSource`（CSV/TSV，零依赖）、`XlsxTableSource`（NPOI）。
    /// 测试用 <see cref="InMemoryTableSource"/>。
    /// </para>
    /// </summary>
    public interface ITableSource
    {
        /// <summary>来源描述（打日志 / 报告里用，例如目录路径）。</summary>
        string Description { get; }

        /// <summary>
        /// 读出**全部**表。
        /// <para>⚠️ 读不动一个文件不该中断整批：把问题写进 <paramref name="diagnostics"/> 继续读下一个。</para>
        /// </summary>
        /// <param name="diagnostics">诊断收集器。</param>
        /// <returns>表集合。</returns>
        IReadOnlyList<RawTable> ReadAll(DiagnosticBag diagnostics);
    }

    /// <summary>
    /// 内存里的表来源：**测试与自测专用**。
    /// <para>它的存在本身就是一条设计证据：**校验逻辑不需要真的 Excel 文件**。</para>
    /// </summary>
    public sealed class InMemoryTableSource : ITableSource
    {
        private readonly List<RawTable> m_tables = new List<RawTable>();

        /// <summary>来源描述。</summary>
        public string Description
        {
            get { return "内存表（测试用）"; }
        }

        /// <summary>加了哪几张表。</summary>
        public IReadOnlyList<RawTable> Tables
        {
            get { return m_tables; }
        }

        /// <summary>读全部（就是把它加进来的那些还回去）。</summary>
        /// <param name="diagnostics">诊断收集器（用不到）。</param>
        /// <returns>表集合。</returns>
        public IReadOnlyList<RawTable> ReadAll(DiagnosticBag diagnostics)
        {
            return m_tables;
        }

        /// <summary>
        /// 用"每行一个字符串数组"的方式摆一张表。
        /// <para>行号从 <paramref name="firstExcelRow"/> 开始算（默认 1，即表头第一行）。</para>
        /// </summary>
        /// <param name="sheetName">表名。</param>
        /// <param name="lines">每行的单元格文本（**必须等长**，短的补空）。</param>
        /// <param name="fileName">文件名（默认用 表名 + .xlsx）。</param>
        /// <returns>这张表（也已被加入来源）。</returns>
        public RawTable AddTable(string sheetName, IReadOnlyList<string[]> lines, string fileName = null)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            RawTable table = new RawTable(fileName ?? (sheetName + ".xlsx"), sheetName);

            for (int r = 0; r < lines.Count; r++)
            {
                string[] cells = lines[r] ?? new string[0];
                RawRow row = new RawRow { ExcelRow = r + 1 };

                for (int c = 0; c < cells.Length; c++)
                {
                    row.Add(new RawCell(cells[c], r + 1, c + 1));
                }

                table.Add(row);
            }

            m_tables.Add(table);
            return table;
        }
    }
}
