// ============================================================================
//  ConfigKit · 诊断与位置（报错定位的实现基础）
//  对应：Docs\17-配置表规范.md §八「报错格式契约」
//
//  ---------------------------------------------------------------------------
//  设计要点：**位置从"读的那一刻"就带上，不是最后再回头查**
//  ---------------------------------------------------------------------------
//  `RawCell` 里就带 Excel 真实行号 / 列号 / 列名，一路传到 `Diagnostic`。
//  这样"报错定位到文件+表+行+列"不是某个格式化函数的功能，而是**数据本身的形状**。
//
//  ---------------------------------------------------------------------------
//  另一条：诊断是**结构化的**，排版是**可换的**
//  ---------------------------------------------------------------------------
//  `ValidationEngine` 只产出 `Diagnostic` 对象；"排成人话"是 `IDiagnosticFormatter` 的事。
//  以后想输出 JSON 给 IDE、给 CI 做行内注释，只要换一个 Formatter，
//  **校验逻辑一行都不用改**。
//
//  ⚠️ 为什么不用 `record`：需要 `System.Runtime.CompilerServices.IsExternalInit`，
//     而 netstandard2.1 里没有（本工程要能原样丢进 Unity 编译，见 Directory.Build.props）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NBC.ConfigKit
{
    /// <summary>诊断的严重程度。**警告不阻止导出，错误阻止**。</summary>
    public enum DiagnosticSeverity
    {
        /// <summary>警告：能用，但不明确。不阻止导出。</summary>
        Warning = 1,

        /// <summary>错误：必须修。**有错就不生成任何产物**（避免半成品）。</summary>
        Error = 2
    }

    /// <summary>
    /// 诊断的稳定编号。**测试按编号断言，不按文案断言**（文案会改，编号不该改）。
    /// </summary>
    public static class DiagnosticCodes
    {
        /// <summary>表头结构不对（缺行、列数不齐）。</summary>
        public const string HeaderStructure = "CFG0001";

        /// <summary>命名不符合规范（表名 / 字段名）。</summary>
        public const string Naming = "CFG0002";

        /// <summary>类型写法无法识别。</summary>
        public const string UnknownType = "CFG0003";

        /// <summary>主键问题（缺失 / 多个 / 名字不对）。</summary>
        public const string KeyDeclaration = "CFG0004";

        /// <summary>校验规则无法识别（**不忽略，直接报错**）。</summary>
        public const string UnknownRule = "CFG0005";

        /// <summary>该填的格子空着（且类型没标可空）。</summary>
        public const string UnexpectedEmpty = "CFG0006";

        /// <summary>空值的**写法**不对（写了 `-` / `null` / `无` 之类）。</summary>
        public const string BadEmptyPlaceholder = "CFG0007";

        /// <summary>值和类型对不上（`abc` 塞给 `int`）。</summary>
        public const string ValueParseFailed = "CFG0008";

        /// <summary>`range` / `min` / `max` 越界。</summary>
        public const string OutOfRange = "CFG0009";

        /// <summary>`unique` 冲突。</summary>
        public const string NotUnique = "CFG0010";

        /// <summary>外键指向的键不存在。</summary>
        public const string ForeignKeyMissing = "CFG0011";

        /// <summary>`len` 长度越界。</summary>
        public const string LengthOutOfRange = "CFG0012";

        /// <summary>`float` 字段没标 `view`（战斗数值不许用浮点）。</summary>
        public const string FloatWithoutViewFlag = "CFG0013";

        /// <summary>表中间出现空行（后面的数据会被静默丢弃 —— 所以是错误）。</summary>
        public const string BlankRowInMiddle = "CFG0014";

        /// <summary>字段名中断后右边又有内容（**有一列会被静默丢弃**）。</summary>
        public const string BrokenColumnHeader = "CFG0015";

        /// <summary>合并单元格 / 公式（一律不允许）。</summary>
        public const string UnsupportedCell = "CFG0016";

        /// <summary>主键重复（报错时指出**两行**）。</summary>
        public const string DuplicateKey = "CFG0017";

        /// <summary>来源本身的问题（文件读不了、sheet 名不合法等）。</summary>
        public const string Source = "CFG0018";

        /// <summary>
        /// 可空用法不受支持：**Unity 的序列化器不支持可空值类型**（`int?` / `float?` / `bool?`）。
        /// <para>可空只允许用在 `ref:` / `ref:[]` / `string` 上 —— 详见本条的使用说明。</para>
        /// </summary>
        public const string UnsupportedNullable = "CFG0019";

        /// <summary>
        /// **跨表**：任务条件的击杀数量超过"任何副本能提供的数量"（条件永远达不成）。见 `CrossTableChecks`。
        /// </summary>
        public const string QuestKillUnreachable = "CFG0020";

        /// <summary>
        /// **跨表**：任务要收集的物品既不在 `DropTable`、也不在 `Reward` 里（永远拿不到）。
        /// </summary>
        public const string ItemNeverObtainable = "CFG0021";

        /// <summary>
        /// **跨表**：用到了"当前没有事件源"的事件类型（**警告** —— 这是实现缺口，不是数据写错）。
        /// </summary>
        public const string EventTypeWithoutSource = "CFG0022";

        /// <summary>
        /// **跨表**：同一条 `QuestCondition` 被**多个持有者**（两个任务 / 任务 + 成就）引用。
        /// <para>⇒ 运行期 `ConditionTracker.Register` **直接抛 `InvalidOperationException`**
        /// （一个条件编号只允许一个持有者登记）。数据看着完全正常，炸的是运行期。</para>
        /// </summary>
        public const string ConditionSharedByOwners = "CFG0023";
    }

    /// <summary>
    /// 一处位置：**文件 + 表 + 行 + 列**（`Docs\17` §八 要求四要素缺一不可）。
    /// </summary>
    public sealed class SourceLocation
    {
        /// <summary>表级诊断（没有具体列）时的列号约定。</summary>
        public const int NoColumn = 0;

        /// <summary>Excel 真实行号（数据从第 5 行开始 → 第一条数据的行号就是 5）。</summary>
        public const int NoRow = 0;

        /// <summary>构造。</summary>
        /// <param name="file">文件（相对路径，给人看）。</param>
        /// <param name="sheet">工作表名（= 表名）。</param>
        /// <param name="row">Excel 真实行号；0 表示不指某一行。</param>
        /// <param name="column">列序号（1 基）；0 表示不指某一列。</param>
        /// <param name="columnName">列名（字段名）；没有则 null。</param>
        public SourceLocation(string file, string sheet, int row, int column, string columnName)
        {
            File = file;
            Sheet = sheet;
            Row = row;
            Column = column;
            ColumnName = columnName;
        }

        /// <summary>文件（相对路径）。</summary>
        public string File { get; }

        /// <summary>工作表名（= 表名）。</summary>
        public string Sheet { get; }

        /// <summary>Excel 真实行号；0 表示不指某一行。</summary>
        public int Row { get; }

        /// <summary>列序号（1 基）；0 表示不指某一列。</summary>
        public int Column { get; }

        /// <summary>列名（字段名）；没有则 null。</summary>
        public string ColumnName { get; }

        /// <summary>这一处位置有没有具体的行列。</summary>
        public bool HasCell
        {
            get { return Row != NoRow && Column != NoColumn; }
        }

        /// <summary>
        /// Excel 的列号字母（1 → `A`，27 → `AA`）。
        /// <para>⚠️ 策划在 Excel 里看到的是字母，不是"第 5 列"。两个都给。</para>
        /// </summary>
        public string ColumnLetter
        {
            get { return Column == NoColumn ? string.Empty : ToColumnLetter(Column); }
        }

        /// <summary>把列序号转成 Excel 的字母列号（双射 26 进制）。</summary>
        /// <param name="column">列序号（1 基）。</param>
        /// <returns>字母列号。</returns>
        public static string ToColumnLetter(int column)
        {
            if (column <= 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            int value = column;

            while (value > 0)
            {
                int remainder = (value - 1) % 26;
                builder.Insert(0, (char)('A' + remainder));
                value = (value - 1) / 26;
            }

            return builder.ToString();
        }

        /// <summary>可读描述（用于日志与自测）。</summary>
        public override string ToString()
        {
            if (Row == NoRow)
            {
                // 表级诊断：说"第 0 行"是废话，直接给文件 + 表
                return File + " › " + Sheet;
            }

            if (Column == NoColumn)
            {
                return File + " › " + Sheet + " 第 " + Row.ToString(CultureInfo.InvariantCulture) + " 行";
            }

            return File + " › " + Sheet + " 第 " + Row.ToString(CultureInfo.InvariantCulture) +
                   " 行 «" + (ColumnName ?? "?") + "»（第 " + Column.ToString(CultureInfo.InvariantCulture) +
                   " 列，Excel 列号 " + ColumnLetter + "）";
        }
    }

    /// <summary>一条诊断。**结构化**，排版交给 <see cref="IDiagnosticFormatter"/>。</summary>
    public sealed class Diagnostic
    {
        /// <summary>构造。</summary>
        /// <param name="severity">严重程度。</param>
        /// <param name="code">稳定编号（见 <see cref="DiagnosticCodes"/>）。</param>
        /// <param name="location">位置。</param>
        /// <param name="message">一句话说明。</param>
        /// <param name="expected">期望（规则 / 类型）；没有则 null。</param>
        /// <param name="actual">实际（单元格原文）；没有则 null。</param>
        public Diagnostic(DiagnosticSeverity severity, string code, SourceLocation location,
                          string message, string expected, string actual)
        {
            Severity = severity;
            Code = code;
            Location = location;
            Message = message;
            Expected = expected;
            Actual = actual;
        }

        /// <summary>严重程度。</summary>
        public DiagnosticSeverity Severity { get; }

        /// <summary>稳定编号。</summary>
        public string Code { get; }

        /// <summary>位置。</summary>
        public SourceLocation Location { get; }

        /// <summary>一句话说明。</summary>
        public string Message { get; }

        /// <summary>期望。</summary>
        public string Expected { get; }

        /// <summary>实际。</summary>
        public string Actual { get; }

        /// <summary>是不是错误。</summary>
        public bool IsError
        {
            get { return Severity == DiagnosticSeverity.Error; }
        }
    }

    /// <summary>
    /// 一次导出过程中收集到的**全部**诊断。
    /// <para>⚠️ 契约：**一次报出全部错误**，不是遇到第一个就停（`Docs\17` §8.2 第 2 条）。</para>
    /// </summary>
    public sealed class DiagnosticBag
    {
        private readonly List<Diagnostic> m_items = new List<Diagnostic>();

        /// <summary>全部诊断（按加入顺序）。</summary>
        public IReadOnlyList<Diagnostic> Items
        {
            get { return m_items; }
        }

        /// <summary>错误数。</summary>
        public int ErrorCount { get; private set; }

        /// <summary>警告数。</summary>
        public int WarningCount { get; private set; }

        /// <summary>有没有错误（有错就不该产出任何东西）。</summary>
        public bool HasErrors
        {
            get { return ErrorCount > 0; }
        }

        /// <summary>加入一条错误。</summary>
        /// <param name="code">编号。</param>
        /// <param name="location">位置。</param>
        /// <param name="message">说明。</param>
        /// <param name="expected">期望。</param>
        /// <param name="actual">实际。</param>
        public void Error(string code, SourceLocation location, string message,
                          string expected = null, string actual = null)
        {
            Add(new Diagnostic(DiagnosticSeverity.Error, code, location, message, expected, actual));
        }

        /// <summary>加入一条警告。</summary>
        /// <param name="code">编号。</param>
        /// <param name="location">位置。</param>
        /// <param name="message">说明。</param>
        /// <param name="expected">期望。</param>
        /// <param name="actual">实际。</param>
        public void Warning(string code, SourceLocation location, string message,
                            string expected = null, string actual = null)
        {
            Add(new Diagnostic(DiagnosticSeverity.Warning, code, location, message, expected, actual));
        }

        /// <summary>加入一条现成的诊断。</summary>
        /// <param name="diagnostic">诊断。</param>
        public void Add(Diagnostic diagnostic)
        {
            if (diagnostic == null)
            {
                return;
            }

            m_items.Add(diagnostic);

            if (diagnostic.IsError)
            {
                ErrorCount++;
            }
            else
            {
                WarningCount++;
            }
        }

        /// <summary>
        /// 按"位置"排序（文件 → 表 → 行 → 列），**就地改动本集合**。
        /// <para>⚠️ 用**稳定排序**：同一格的错误保持产生顺序（否则每次跑输出顺序会变，没法 Diff）。</para>
        /// <para>⚠️ 只在你**拥有**这个集合时调用它。要"看一眼排好序的"请用
        /// <see cref="SortedByLocation"/> —— 报告类代码**不该改数据**。</para>
        /// </summary>
        public void SortByLocation()
        {
            m_items.Sort(CompareByLocation);
        }

        /// <summary>
        /// 按位置排序后的**副本**（不改动本集合）。
        /// <para>
        /// ⚠️ 这个方法是被 XlsxProbe 逼出来的：报告渲染原先调的是 <see cref="SortByLocation"/>，
        /// 于是"调用方一边 `foreach` 遍历 `Items`、一边调 `Render`"会当场抛
        /// `Collection was modified`。**一个"输出报告"的方法去改输入数据是设计错误** ——
        /// 它让 Render 变成非幂等的，还给了调用方一个很难查的雷。
        /// </para>
        /// </summary>
        /// <returns>排好序的副本。</returns>
        public IReadOnlyList<Diagnostic> SortedByLocation()
        {
            List<Diagnostic> sorted = new List<Diagnostic>(m_items);
            sorted.Sort(CompareByLocation);
            return sorted;
        }

        /// <summary>稳定比较：位置相同则保持原顺序。</summary>
        private static int CompareByLocation(Diagnostic a, Diagnostic b)
        {
            // ⚠️ 防御性：位置理论上不该是 null，但排序在报告阶段跑，
            //    这里让"报告"永远不因为数据形状不完美而崩 —— 报错工具自己崩掉是最差的结果
            string fileA = a.Location == null ? string.Empty : a.Location.File ?? string.Empty;
            string fileB = b.Location == null ? string.Empty : b.Location.File ?? string.Empty;
            string sheetA = a.Location == null ? string.Empty : a.Location.Sheet ?? string.Empty;
            string sheetB = b.Location == null ? string.Empty : b.Location.Sheet ?? string.Empty;
            int rowA = a.Location == null ? 0 : a.Location.Row;
            int rowB = b.Location == null ? 0 : b.Location.Row;
            int colA = a.Location == null ? 0 : a.Location.Column;
            int colB = b.Location == null ? 0 : b.Location.Column;

            int result = string.CompareOrdinal(fileA, fileB);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(sheetA, sheetB);
            if (result != 0)
            {
                return result;
            }

            result = rowA.CompareTo(rowB);
            if (result != 0)
            {
                return result;
            }

            return colA.CompareTo(colB);
        }
    }

    /// <summary>把诊断排成人话。**换实现就能换输出格式**（控制台 / JSON / IDE 行内注释）。</summary>
    public interface IDiagnosticFormatter
    {
        /// <summary>排版一条诊断。</summary>
        /// <param name="diagnostic">诊断。</param>
        /// <returns>多行文本。</returns>
        string Format(Diagnostic diagnostic);

        /// <summary>排版汇总（共几个错、几个警告）。</summary>
        /// <param name="bag">诊断集合。</param>
        /// <returns>一行文本。</returns>
        string FormatSummary(DiagnosticBag bag);
    }

    /// <summary>
    /// 人话格式（`Docs\17` §8.1 的契约）。
    /// <para>
    /// 形状：<br/>
    /// <c>[配置表] 文件 › 表 第 N 行 «字段»（第 N 列，Excel 列号 X）：</c><br/>
    /// <c>    说明</c><br/>
    /// <c>    期望：…</c><br/>
    /// <c>    实际：…</c>
    /// </para>
    /// </summary>
    public sealed class TextDiagnosticFormatter : IDiagnosticFormatter
    {
        /// <summary>空单元格在"实际"里的显示。</summary>
        public const string EmptyDisplay = "<空>";

        /// <summary>排版一条诊断。</summary>
        /// <param name="diagnostic">诊断。</param>
        /// <returns>多行文本。</returns>
        public string Format(Diagnostic diagnostic)
        {
            if (diagnostic == null)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            builder.Append("[配置表] ");
            builder.Append(diagnostic.Location == null ? "<无位置>" : diagnostic.Location.ToString());
            builder.Append("：");
            builder.AppendLine();
            builder.Append("    ");
            builder.Append(diagnostic.Message);
            builder.Append("  [");
            builder.Append(diagnostic.Code);
            builder.Append(']');

            if (!string.IsNullOrEmpty(diagnostic.Expected))
            {
                builder.AppendLine();
                builder.Append("    期望：");
                builder.Append(diagnostic.Expected);
            }

            if (diagnostic.Expected != null || diagnostic.Actual != null)
            {
                builder.AppendLine();
                builder.Append("    实际：");
                builder.Append(string.IsNullOrEmpty(diagnostic.Actual) ? EmptyDisplay : diagnostic.Actual);
            }

            return builder.ToString();
        }

        /// <summary>排版汇总。</summary>
        /// <param name="bag">诊断集合。</param>
        /// <returns>一行文本。</returns>
        public string FormatSummary(DiagnosticBag bag)
        {
            if (bag == null)
            {
                return string.Empty;
            }

            return "共 " + bag.ErrorCount.ToString(CultureInfo.InvariantCulture) + " 个错误、" +
                   bag.WarningCount.ToString(CultureInfo.InvariantCulture) + " 个警告。";
        }
    }
}
