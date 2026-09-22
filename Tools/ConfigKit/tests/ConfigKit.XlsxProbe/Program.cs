// ============================================================================
//  ConfigKit · xlsx 适配器的可复现验证探针
//
//  跑法：dotnet build 之后直接运行
//        Tools\ConfigKit\tests\ConfigKit.XlsxProbe\bin\Debug\net8.0\NBC.ConfigKit.XlsxProbe.exe
//  退出码：0 = 全绿；1 = 有红
//
//  ---------------------------------------------------------------------------
//  它在验什么（**用 NPOI 造真文件，喂给适配器读**）
//  ---------------------------------------------------------------------------
//  ① 多 sheet → 多表，表名 = sheet 名，文件名 = 文件名
//  ② **数值格的"显示格式"不该影响解析**（hp 列设成 `0.00` 时，1200 必须还是 1200）
//  ③ 公式格 → 标 Formula → 上层报 CFG0016
//  ④ 合并单元格 → 标 Merged → 上层报 CFG0016
//  ⑤ Excel 的锁文件 `~$xxx.xlsx` 要被跳过（否则会报一个和真实问题无关的"文件损坏"）
//  ⑥ 报错定位在 xlsx 这条路上**同样是 Excel 真实行号 + 字母列号**（C2 的验收）
//
//  ⚠️ **每个场景一个独立子目录**（good / tricky / lock / bad）：
//     流水线的语义是"**整批有错就一个文件都不产出**"，
//     所以好表和坏表**不能放在同一个目录里** —— 否则"好表该通过"这类断言永远红。
//     （第一版就是这么写的，探针当场把这个测试设计错误抓了出来。）
//
//  ⚠️ 它故意**不在**零依赖的 SelfTest 里：SelfTest 要能在没还原 NPOI 时照跑，
//     而本探针必须有 NPOI。两个工具的分工写在 README 里。
// ============================================================================

using System.Text;
using NBC.ConfigKit;
using NBC.ConfigKit.Sources;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;

namespace NBC.ConfigKit.XlsxProbe
{
    /// <summary>探针入口。</summary>
    internal static class Program
    {
        private static int s_passed;
        private static int s_failed;

        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (IOException)
            {
            }

            // 造示例表的模式：`--emit <目录>` —— 把探针用的那几张表写到指定目录后退出。
            // 两个用途：① 当测试夹具（喂给 Cli 的 xlsx 路径）；② 你要一张"格式正确的示例表"时直接拿。
            if (args.Length >= 2 && args[0] == "--emit")
            {
                string target = args[1];
                Directory.CreateDirectory(target);
                BuildHeroWorkbook(Path.Combine(target, "Hero.xlsx"));
                BuildTrickyWorkbook(Path.Combine(target, "Tricky.xlsx"));
                Console.WriteLine("已写出示例表到 " + target);
                Console.WriteLine("  Hero.xlsx   （两个 sheet：Hero + Skill，格式完全合规范）");
                Console.WriteLine("  Tricky.xlsx （故意含公式与合并单元格，用来演示报错）");
                return 0;
            }

            string root = Path.Combine(Path.GetTempPath(), "configkit-xlsxprobe-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Sub(root, "good"));
            Directory.CreateDirectory(Sub(root, "tricky"));
            Directory.CreateDirectory(Sub(root, "lock"));
            Directory.CreateDirectory(Sub(root, "bad"));

            try
            {
                BuildHeroWorkbook(Path.Combine(Sub(root, "good"), "Hero.xlsx"));
                BuildTrickyWorkbook(Path.Combine(Sub(root, "tricky"), "Tricky.xlsx"));

                // 锁文件场景：一个好文件 + 一个 Excel 开着时留下的 ~$ 锁文件
                BuildHeroWorkbook(Path.Combine(Sub(root, "lock"), "Hero.xlsx"));
                File.WriteAllText(Path.Combine(Sub(root, "lock"), "~$Hero.xlsx"), "这不是一个 xlsx", new UTF8Encoding(false));

                // ⚠️ **正对照**：同样的坏内容，但**不带** `~$` 前缀 —— 它**必须**被报出来。
                //    没有这一条，"锁文件被跳过"就只是"我们没看到它的报错"，
                //    而那可能意味着**它压根没被扫到**（假绿）。
                File.WriteAllText(Path.Combine(Sub(root, "lock"), "Broken.xlsx"), "这不是一个 xlsx", new UTF8Encoding(false));

                BuildBadWorkbook(Path.Combine(Sub(root, "bad"), "Bad.xlsx"));

                Console.WriteLine("=== ConfigKit xlsx 探针 ===");
                Console.WriteLine("测试目录：" + root);
                Console.WriteLine();

                Run("读到 2 张表（一个 xlsx 的多个 sheet → 多表）", ReadsAllSheets, root);
                Run("表名 = sheet 名；文件名 = 文件名", TableNames, root);
                Run("⚠️ 数值格式是 `0.00` 时，1200 仍解析成 1200", NumericFormatDoesNotBreakIntegers, root);
                Run("合法表：整批通过并产出代码", ValidBatchSucceeds, root);
                Run("公式格 → CFG0016，且位置是 Excel 真实行号", FormulaIsFlagged, root);
                Run("合并单元格 → CFG0016", MergedIsFlagged, root);
                Run("Excel 锁文件 `~$` 被跳过（不产生任何诊断）", LockFileIsSkipped, root);
                Run("错误定位：Excel 行号 + 字母列号（C2 验收）", ErrorLocationOnXlsxPath, root);
                Run("**整批有错就一个文件都不产出**", BadBatchEmitsNothing, root);

                Console.WriteLine();
                Console.WriteLine($"=== 通过 {s_passed} 条，失败 {s_failed} 条 ===");
                return s_failed == 0 ? 0 : 1;
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        // ====================================================================
        //  场景目录
        // ====================================================================

        private static string Sub(string root, string name)
        {
            return Path.Combine(root, name);
        }

        // ====================================================================
        //  造文件（用 NPOI 写真的 .xlsx）
        // ====================================================================

        /// <summary>Hero.xlsx：两个 sheet（Hero + Skill）。</summary>
        private static void BuildHeroWorkbook(string path)
        {
            using XSSFWorkbook workbook = new XSSFWorkbook();

            ISheet hero = workbook.CreateSheet("Hero");
            WriteRow(hero, 0, "id", "name", "hp", "moveSpeed", "skillIds");
            WriteRow(hero, 1, "编号", "名称", "生命值", "移动速度（毫米/秒）", "技能列表");
            WriteRow(hero, 2, "int", "string", "int", "int", "ref:Skill[]");
            WriteRow(hero, 3, "key", "len(1,16)", "range(1,999999)", "min(0)", "");
            WriteRow(hero, 4, "1001", "剑士", "1200", "5000", "2001,2002");
            WriteRow(hero, 5, "1002", "法师", "800", "4800", "2002");

            // ⚠️ 关键一格：hp 列设成 "0.00" 显示格式 —— 值仍是 1200
            ICellStyle twoDecimals = workbook.CreateCellStyle();
            twoDecimals.DataFormat = workbook.CreateDataFormat().GetFormat("0.00");
            hero.GetRow(4).GetCell(2).CellStyle = twoDecimals;
            hero.GetRow(5).GetCell(2).CellStyle = twoDecimals;

            ISheet skill = workbook.CreateSheet("Skill");
            WriteRow(skill, 0, "id", "name", "damage");
            WriteRow(skill, 1, "编号", "名称", "伤害");
            WriteRow(skill, 2, "int", "string", "int");
            WriteRow(skill, 3, "key", "len(1,16)", "range(1,99999)");
            WriteRow(skill, 4, "2001", "火球", "100");
            WriteRow(skill, 5, "2002", "冰箭", "80");

            using FileStream stream = File.Create(path);
            workbook.Write(stream);
        }

        /// <summary>Tricky.xlsx：公式格 + 合并单元格。</summary>
        private static void BuildTrickyWorkbook(string path)
        {
            using XSSFWorkbook workbook = new XSSFWorkbook();
            ISheet sheet = workbook.CreateSheet("Tricky");

            WriteRow(sheet, 0, "id", "value", "note");
            WriteRow(sheet, 1, "编号", "数值", "备注");
            WriteRow(sheet, 2, "int", "int", "string");
            WriteRow(sheet, 3, "key", "range(1,10)", "");

            WriteRow(sheet, 4, "1", "", "公式格");
            sheet.GetRow(4).GetCell(1).CellFormula = "1+1";            // ← 公式

            WriteRow(sheet, 5, "2", "5", "合并格");
            sheet.AddMergedRegion(new CellRangeAddress(5, 5, 1, 2));   // ← 合并 B6:C6

            using FileStream stream = File.Create(path);
            workbook.Write(stream);
        }

        /// <summary>Bad.xlsx：故意把 hp 写成 0（越界），验报错定位。</summary>
        private static void BuildBadWorkbook(string path)
        {
            using XSSFWorkbook workbook = new XSSFWorkbook();
            ISheet sheet = workbook.CreateSheet("Bad");

            WriteRow(sheet, 0, "id", "hp");
            WriteRow(sheet, 1, "编号", "生命值");
            WriteRow(sheet, 2, "int", "int");
            WriteRow(sheet, 3, "key", "range(1,999999)");
            WriteRow(sheet, 4, "1001", "0");

            using FileStream stream = File.Create(path);
            workbook.Write(stream);
        }

        private static void WriteRow(ISheet sheet, int rowIndex, params string[] values)
        {
            IRow row = sheet.CreateRow(rowIndex);

            for (int c = 0; c < values.Length; c++)
            {
                ICell cell = row.CreateCell(c);

                // 纯数字写成**数值格**（这样才真的测到 Numeric 路径）
                if (long.TryParse(values[c], out long number))
                {
                    cell.SetCellValue((double)number);
                }
                else
                {
                    cell.SetCellValue(values[c]);
                }
            }
        }

        // ====================================================================
        //  断言
        // ====================================================================

        private static void Run(string name, Action<string> body, string root)
        {
            try
            {
                body(root);
                s_passed++;
                Console.WriteLine("  [绿] " + name);
            }
            catch (Exception exception)
            {
                s_failed++;
                Console.WriteLine("  [红] " + name);
                Console.WriteLine("       " + exception.Message.Replace("\n", "\n       "));
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception(message);
            }
        }

        private static void CheckEqual(object expected, object actual, string label)
        {
            if (!Equals(expected, actual))
            {
                throw new Exception($"{label}：期望 <{expected}>，实际 <{actual}>");
            }
        }

        /// <summary>跑一次适配器 + 流水线。</summary>
        private static ExportReport Export(string directory)
        {
            ExportPipeline pipeline = new ExportPipeline(
                ConfigPolicy.CreateDefault(),
                new CSharpConfigEmitter(),
                new EmitOptions { Namespace = "Test.Config" });

            return pipeline.Run(new XlsxTableSource(directory));
        }

        private static string Render(ExportReport report)
        {
            return report.Render(new TextDiagnosticFormatter());
        }

        private static string FileContent(ExportReport report, string relativePath)
        {
            foreach (EmittedFile file in report.Files)
            {
                if (file.RelativePath == relativePath)
                {
                    return file.Content;
                }
            }

            throw new Exception($"没生成 {relativePath}。生成报告：\n{Render(report)}");
        }

        private static Diagnostic FirstOf(ExportReport report, string code)
        {
            foreach (Diagnostic diagnostic in report.Diagnostics.Items)
            {
                if (diagnostic.Code == code)
                {
                    return diagnostic;
                }
            }

            throw new Exception($"没有任何 {code} 诊断。实际：\n{Render(report)}");
        }

        // ====================================================================
        //  逐条
        // ====================================================================

        private static void ReadsAllSheets(string root)
        {
            ExportReport report = Export(Sub(root, "good"));
            CheckEqual(2, report.Tables.Count, "Hero.xlsx 有 2 个 sheet → 2 张表");

            bool hero = false;
            bool skill = false;

            foreach (TableReport table in report.Tables)
            {
                hero |= table.TableName == "Hero";
                skill |= table.TableName == "Skill";
            }

            Check(hero && skill, "应当同时读到 Hero 与 Skill");
        }

        private static void TableNames(string root)
        {
            foreach (TableReport table in Export(Sub(root, "good")).Tables)
            {
                if (table.TableName == "Hero")
                {
                    CheckEqual("Hero.xlsx", table.FileName, "文件名");
                    CheckEqual(2, table.RowCount, "Hero 的数据行数");
                    return;
                }
            }

            throw new Exception("没读到 Hero 表");
        }

        /// <summary>
        /// ⚠️ 本轮最重要的一条：`hp` 列在 Excel 里显示格式是 `0.00`。
        /// 如果适配器用 `DataFormatter` 取文本，会得到 `"1200.00"` → int 解析失败 →
        /// 报一个**看起来像工具 bug** 的错。它必须解析成 1200。
        /// </summary>
        private static void NumericFormatDoesNotBreakIntegers(string root)
        {
            ExportReport report = Export(Sub(root, "good"));

            foreach (Diagnostic diagnostic in report.Diagnostics.Items)
            {
                Check(diagnostic.Location.Column != 3 || diagnostic.Location.Sheet != "Hero",
                    $"Hero 的 hp 不该因为显示格式是 0.00 而报错：{diagnostic.Message}");
            }

            string code = FileContent(report, "Config_Hero.cs");
            Check(code.Contains("public int hp;", StringComparison.Ordinal), "hp 字段应当在");
        }

        private static void ValidBatchSucceeds(string root)
        {
            ExportReport report = Export(Sub(root, "good"));

            Check(report.Succeeded, "合法表应当整批通过：\n" + Render(report));
            CheckEqual(4, report.Files.Count, "2 张表 × 2 个文件");
        }

        private static void FormulaIsFlagged(string root)
        {
            ExportReport report = Export(Sub(root, "tricky"));
            bool found = false;

            foreach (Diagnostic diagnostic in report.Diagnostics.Items)
            {
                if (diagnostic.Code == DiagnosticCodes.UnsupportedCell &&
                    diagnostic.Location.Sheet == "Tricky" && diagnostic.Location.Row == 5)
                {
                    found = true;
                    CheckEqual("B", diagnostic.Location.ColumnLetter, "公式在 B 列");
                }
            }

            Check(found, "公式格应当报 CFG0016（Excel 第 5 行）：\n" + Render(report));
        }

        private static void MergedIsFlagged(string root)
        {
            ExportReport report = Export(Sub(root, "tricky"));
            int merged = 0;

            foreach (Diagnostic diagnostic in report.Diagnostics.Items)
            {
                if (diagnostic.Code == DiagnosticCodes.UnsupportedCell &&
                    diagnostic.Location.Sheet == "Tricky" && diagnostic.Location.Row == 6)
                {
                    merged++;
                }
            }

            Check(merged >= 2, $"合并区 B6:C6 两个格子都该报 CFG0016，实际 {merged} 条：\n" + Render(report));
        }

        private static void LockFileIsSkipped(string root)
        {
            ExportReport report = Export(Sub(root, "lock"));
            bool brokenReported = false;

            // ⚠️ 遍历诊断时**不要**在消息里调 Render —— 消息是**先算好**再传进 Check 的。
            //    （第一版就是这么写的：Render 当时会就地排序 → Collection was modified。
            //     产品侧已修成用副本排序，但这条写法本身也该避免。）
            foreach (Diagnostic diagnostic in report.Diagnostics.Items)
            {
                string file = diagnostic.Location.File ?? string.Empty;

                Check(file.IndexOf("~$", StringComparison.Ordinal) < 0,
                    "锁文件 ~$Hero.xlsx 不该被读，更不该报错：" + diagnostic.Code);

                if (file == "Broken.xlsx")
                {
                    brokenReported = true;
                }
            }

            // **正对照**：不带 ~$ 的同内容坏文件必须被报出来 ——
            // 否则"锁文件被跳过"只是"我们没看到它"，可能压根没扫到（假绿）
            Check(brokenReported, "正对照失败：Broken.xlsx（同样坏、但没有 ~$ 前缀）应当被报出来：" +
                                  "说明扫描确实覆盖了这个目录，锁文件是被**主动跳过**的");

            // Hero.xlsx 自己有 2 个 sheet（Hero + Skill），所以是 2 张表；
            // 锁文件与坏文件都不该变成额外的表
            CheckEqual(2, report.Tables.Count, "只该有 Hero.xlsx 的两个 sheet");
        }

        private static void ErrorLocationOnXlsxPath(string root)
        {
            ExportReport report = Export(Sub(root, "bad"));
            Diagnostic diagnostic = FirstOf(report, DiagnosticCodes.OutOfRange);

            CheckEqual("Bad.xlsx", diagnostic.Location.File, "文件");
            CheckEqual("Bad", diagnostic.Location.Sheet, "表（sheet 名）");
            CheckEqual(5, diagnostic.Location.Row, "**Excel 真实行号**：数据从第 5 行开始");
            CheckEqual("B", diagnostic.Location.ColumnLetter, "hp 是第 2 列 → B");
            CheckEqual("0", diagnostic.Actual, "单元格原文");
        }

        private static void BadBatchEmitsNothing(string root)
        {
            ExportReport report = Export(Sub(root, "bad"));

            Check(!report.Succeeded, "有错时不该算成功");
            CheckEqual(0, report.Files.Count, "**有错就一个文件都不产出**");
        }
    }
}
