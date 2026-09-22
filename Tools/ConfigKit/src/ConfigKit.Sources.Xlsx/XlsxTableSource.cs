// ============================================================================
//  ConfigKit · 接缝① 的实现：.xlsx（NPOI 2.8.0）
//  对应：Docs\17-配置表规范.md §二（一个 sheet = 一张表）、§三（五行表头）
//
//  ---------------------------------------------------------------------------
//  ⚠️⚠️ 本文件写于"NPOI 还没还原"的时候 —— 也就是说它【尚未被编译器验证过】
//  ---------------------------------------------------------------------------
//  2026-09-22 实测：本沙箱 `dotnet restore` 失败（NU1301，无网络），
//  所以 NPOI 拿不到。**代码是先写的，编译要等还原之后。**
//
//  为了让那一次编译尽量一次过，这里做了两件事：
//    ① **把对 NPOI 的接触面压到最小**，并逐条列在下面（只有 12 个成员）；
//    ② 修法只可能落在这一个文件里 —— 别的工程一个都不认识 NPOI。
//
//  ============================ NPOI 接触面清单 ============================
//    using NPOI.SS.UserModel   →  IWorkbook / ISheet / IRow / ICell /
//                                  CellType（枚举）/ DataFormatter
//    using NPOI.SS.Util        →  CellRangeAddress
//    using NPOI.XSSF.UserModel →  XSSFWorkbook(Stream)
//
//    ① new XSSFWorkbook(Stream)
//    ② workbook.NumberOfSheets
//    ③ workbook.GetSheetAt(int)
//    ④ sheet.SheetName
//    ⑤ sheet.LastRowNum
//    ⑥ sheet.GetRow(int)              （整行没数据时返回 null）
//    ⑦ row.LastCellNum                （short；**是"最后一个单元格下标 + 1"**）
//    ⑧ row.GetCell(int)               （格子不存在时返回 null）
//    ⑨ cell.CellType                  （== CellType.Formula 判公式）
//    ⑩ formatter.FormatCellValue(cell)（**统一走它**：按 Excel 显示格式转文本）
//    ⑪ sheet.NumMergedRegions / sheet.GetMergedRegion(int)
//    ⑫ region.FirstRow / LastRow / FirstColumn / LastColumn
//  ==========================================================================
//
//  ---------------------------------------------------------------------------
//  两个"看起来多余的"决定，都是为了不产生静默错误
//  ---------------------------------------------------------------------------
//  **① 一律用 `DataFormatter.FormatCellValue` 取文本，不自己拼数字。**
//     `NumericCellValue.ToString()` 会把 `1500` 变成 `1500`、把 `8.5` 变成 `8.5`，
//     但遇到"单元格格式是 0.00"时会得到 `1500.00` —— 于是 `int` 解析失败。
//     DataFormatter 按**Excel 里看到的**转，和策划看到的一致。
//
//  **② 跳过 Excel 的锁文件 `~$xxx.xlsx`。**
//     策划开着 Excel 时，目录里会多一个 `~$` 开头的隐藏文件。
//     不跳的话工具会去读它，然后报一个"文件损坏"的错 ——
//     **一个和真实问题毫无关系的报错**，最费时间。
// ==========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;

namespace NBC.ConfigKit.Sources
{
    /// <summary>
    /// 从目录里读 `.xlsx`：**一个 sheet = 一张表**（表名 = sheet 名）。
    /// <para>⚠️ 只支持 `.xlsx`。`.xls` 是旧格式，会报一条能看懂的错误让你另存。</para>
    /// </summary>
    public sealed class XlsxTableSource : ITableSource
    {
        private readonly string m_directory;

        /// <summary>构造。</summary>
        /// <param name="directory">目录。</param>
        public XlsxTableSource(string directory)
        {
            m_directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        /// <summary>来源描述。</summary>
        public string Description
        {
            get { return "xlsx 目录：" + m_directory; }
        }

        /// <summary>
        /// 读出目录下全部 `.xlsx` 的全部 sheet。
        /// <para>⚠️ 单个文件读不动**不中断整批**：写条诊断继续下一个。</para>
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

            string[] files = Directory.GetFiles(m_directory, "*.xlsx");
            Array.Sort(files, StringComparer.Ordinal);   // 顺序稳定 → 输出可复现

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                string fileName = Path.GetFileName(file);

                // Excel 开着文件时的锁文件：读了只会得到一个和真实问题无关的报错
                if (fileName.StartsWith("~$", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using (FileStream stream = File.OpenRead(file))
                    using (XSSFWorkbook workbook = new XSSFWorkbook(stream))
                    {
                        ReadWorkbook(workbook, fileName, tables);
                    }
                }
                catch (Exception exception)
                {
                    // ⚠️ 这里刻意捕获 Exception：**输入文件是用户给的**，
                    //    NPOI 遇到损坏文件会抛它自己的一堆异常类型。
                    //    但**把异常类型名写进诊断** —— 否则真出了我自己的 bug，
                    //    会被伪装成"文件坏了"。
                    diagnostics.Error(DiagnosticCodes.Source,
                        new SourceLocation(fileName, "<文件>", SourceLocation.NoRow, SourceLocation.NoColumn, null),
                        "读文件失败（" + exception.GetType().Name + "）：" + exception.Message,
                        "一个合法的 .xlsx",
                        fileName);
                }
            }

            return tables;
        }

        /// <summary>读一个工作簿里的所有 sheet。</summary>
        private static void ReadWorkbook(IWorkbook workbook, string fileName, List<RawTable> tables)
        {
            DataFormatter formatter = new DataFormatter();

            for (int s = 0; s < workbook.NumberOfSheets; s++)
            {
                ISheet sheet = workbook.GetSheetAt(s);

                if (sheet == null)
                {
                    continue;
                }

                tables.Add(ReadSheet(sheet, fileName, formatter));
            }
        }

        /// <summary>读一个 sheet。</summary>
        private static RawTable ReadSheet(ISheet sheet, string fileName, DataFormatter formatter)
        {
            // ⚠️ 表名 = sheet 名（Docs\17 §二：一个 sheet = 一张表）
            RawTable table = new RawTable(fileName, sheet.SheetName);

            int lastRowIndex = sheet.LastRowNum;          // ⑤ 空表时是 -1
            ISet<long> merged = CollectMergedCells(sheet); // ⑪⑫

            // 先扫一遍拿到列数：各行"最后单元格 + 1"的最大值
            int columnCount = 0;

            for (int r = 0; r <= lastRowIndex; r++)
            {
                IRow probe = sheet.GetRow(r);             // ⑥

                if (probe == null)
                {
                    continue;
                }

                int lastCellNum = probe.LastCellNum;      // ⑦（short：末格下标 + 1）

                if (lastCellNum > columnCount)
                {
                    columnCount = lastCellNum;
                }
            }

            for (int r = 0; r <= lastRowIndex; r++)
            {
                IRow row = sheet.GetRow(r);
                RawRow raw = new RawRow { ExcelRow = r + 1 };   // ★ Excel 真实行号

                for (int c = 0; c < columnCount; c++)
                {
                    ICell cell = row == null ? null : row.GetCell(c);   // ⑧
                    string text = cell == null ? string.Empty : formatter.FormatCellValue(cell);
                    RawCellFlags flags = RawCellFlags.None;

                    if (IsFormula(cell))
                    {
                        flags |= RawCellFlags.Formula;
                    }

                    if (merged.Contains(Key(r, c)))
                    {
                        flags |= RawCellFlags.Merged;
                    }

                    raw.Add(new RawCell(text, r + 1, c + 1, flags));
                }

                table.Add(raw);
            }

            return table;
        }

        /// <summary>是不是公式（⑨）。</summary>
        private static bool IsFormula(ICell cell)
        {
            return cell != null && cell.CellType == CellType.Formula;
        }

        /// <summary>把所有合并区域里的格子收进一个集合（⑪⑫）。</summary>
        private static ISet<long> CollectMergedCells(ISheet sheet)
        {
            HashSet<long> cells = new HashSet<long>();
            int count = sheet.NumMergedRegions;

            for (int i = 0; i < count; i++)
            {
                CellRangeAddress region = sheet.GetMergedRegion(i);

                if (region == null)
                {
                    continue;
                }

                for (int r = region.FirstRow; r <= region.LastRow; r++)
                {
                    for (int c = region.FirstColumn; c <= region.LastColumn; c++)
                    {
                        cells.Add(Key(r, c));
                    }
                }
            }

            return cells;
        }

        /// <summary>把 (行, 列) 打成一个 long，用作集合键。</summary>
        private static long Key(int row, int column)
        {
            return ((long)row << 32) | (uint)column;
        }
    }
}
