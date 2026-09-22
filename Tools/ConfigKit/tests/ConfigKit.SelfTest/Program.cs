// ============================================================================
//  ConfigKit · 自测运行器
//  对应：Docs\16-M1开工清单.md C2 的验收「故意写错一格 → 报错定位到文件+表+行+列」
//
//  跑法：dotnet run --project Tools\ConfigKit\tests\ConfigKit.SelfTest
//  退出码：0 = 全绿；1 = 有红（逐条打印）
//
//  ---------------------------------------------------------------------------
//  这个运行器自己的"正/负对照"
//  ---------------------------------------------------------------------------
//  · 每条断言都有**具体期望值**，不是"没崩就算过"
//  · 架构守卫那两条**先被自己验一遍**（拿一段内含 PackageReference 的合成 XML
//    喂给检查函数，必须报违规）—— 否则"守卫报 0 违规"可能只是它根本没在工作（W9）
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NBC.ConfigKit;
using NBC.ConfigKit.Sources;

namespace NBC.ConfigKit.SelfTest
{
    /// <summary>极简断言运行器。</summary>
    internal static class Program
    {
        private static int s_passed;
        private static int s_failed;

        private static int Main()
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (IOException)
            {
                // 输出被重定向到管道时可能设不了编码 —— 不该因为"显示问题"让自测失败
            }

            Console.WriteLine("=== ConfigKit 自测 ===");

            // ---- 位置与格式化 ----
            Run("列号字母转换（1→A / 26→Z / 27→AA / 52→AZ / 53→BA）", ColumnLetters);
            Run("⚠️ 报错格式契约（Docs\\17 §八 的四要素 + 期望/实际）", DiagnosticFormatContract);
            Run("表级诊断不带「第 0 行」这种废话", TableLevelLocationReadsWell);

            // ---- 表头解析 ----
            Run("合法表：0 错误", ValidSet_HasNoErrors);
            Run("不认识的类型 → CFG0003", UnknownType_IsReported);
            Run("不认识的规则 → CFG0005（拼错不能被静默忽略）", UnknownRule_IsReported);
            Run("规则参数个数不对 → CFG0005", RuleArity_IsReported);
            Run("float 没标 view → CFG0013", FloatWithoutView_IsReported);
            Run("float 标了 view → 通过", FloatWithView_IsAccepted);
            Run("字段名中间断了 → CFG0015", BrokenHeader_IsReported);
            Run("表名不规范 → CFG0002", BadTableName_IsReported);

            // ---- 数据校验 ----
            Run("该填的格子空着 → CFG0006", EmptyRequiredCell_IsReported);
            Run("可空字段留空 → 通过", NullableEmpty_IsAccepted);
            Run("写了 `-` 当空值 → CFG0007", DashPlaceholder_IsReported);
            Run("类型对不上 → CFG0008", BadInteger_IsReported);
            Run("range 越界 → CFG0009", OutOfRange_IsReported);
            Run("int 溢出 → CFG0009", IntOverflow_IsReported);
            Run("len 越界 → CFG0012", Length_IsReported);
            Run("unique 冲突 → CFG0010", NotUnique_IsReported);
            Run("主键重复 → CFG0017，且说明里指出**两行**", DuplicateKey_NamesBothRows);
            Run("外键值不存在 → CFG0011", ForeignKeyValueMissing_IsReported);
            Run("外键**表**不存在 → CFG0011（和值不存在分开报）", ForeignKeyTableMissing_IsReported);
            Run("表中间空行 → CFG0014（不许静默截断）", BlankRowInMiddle_IsReported);
            Run("一次报出全部错误（不是遇到第一个就停）", AllErrors_AreReportedAtOnce);
            Run("有结构错误时不再校验数据（避免连锁误报）", FatalStructure_StopsDataValidation);

            // ---- 排序 ----
            Run("诊断排序：按 文件→表→行→列", Diagnostics_AreSortedByLocation);

            // ---- 分隔符来源 ----
            Run("CSV 拆分：引号 / 转义引号 / 引号内分隔符", Csv_SplitsQuotedCells);

            // ---- 代码生成 ----
            Run("代码生成：字段、类型与可空", CodeGen_FieldsAndTypes);
            Run("代码生成：各类型的字面量（映射与 CSharpTypeOf 一致）", CodeGen_Literals);
            Run("代码生成：SO 含主键索引、Get/TryGet 与 TSV 加载器", CodeGen_ScriptableObjectHasIndex);
            Run("代码生成：注释消毒（不许出现连续两个连字符）", CodeGen_SanitizesDocComments);

            Run("代码生成：注释只出现在「文件头 / 变量名后 / 类名方法名前」（负责人的规则）",
                CodeGen_CommentDensityRule);

            // ---- TSV 中间产物 ----
            Run("TSV：表头是字段名，一行一条数据", Tsv_HeaderAndRows);
            Run("TSV：单元格里的制表符/换行要转义（否则整行静默错位）", Tsv_EscapesTabsAndNewlines);

            // ---- JSON 产物 ----
            Run("JSON：合法（**用 System.Text.Json 反过来解析**，不是我说了算）", Json_IsParsableByJsonParser);
            Run("JSON：字段名当键 + 枚举写成员名 + 数组是数组", Json_ShapeAndValues);
            Run("JSON：转义（引号/反斜杠/换行/控制字符）", Json_Escapes);
            Run("三份产物对「可空 ref」给同一个答案（C# = TSV = JSON）", Emitters_AgreeOnNullableRef);

            // ---- 可空支持范围（被 Unity 序列化限制逼出来的）----
            Run("可空值类型 int? → CFG0019（Unity 序列化不支持可空值类型）", NullableValueType_IsRejected);
            Run("可空 ref / string → 允许", NullableRefAndString_AreAccepted);

            // ---- 流水线 ----
            Run("流水线：成功时每表产出 2 个文件", Pipeline_EmitsTwoFilesPerTable);
            Run("流水线：**有错就一个文件都不产出**", Pipeline_EmitsNothingWhenInvalid);
            Run("端到端：真 CSV 文件目录 → 生成", EndToEnd_CsvDirectory);
            Run("CSV：含逗号却没加引号 → 报错要给出「像是被逗号拆开了」的提示", Csv_UnquotedComma_GivesActionableHint);

            // ---- 架构守卫 ----
            Run("架构守卫：检查函数本身有效（负对照）", ArchitectureGuard_DetectsViolation);
            Run("架构守卫：注释里的字样不算违规（守卫自己也得过这关）", ArchitectureGuard_IgnoresComments);
            Run("架构守卫：ConfigKit.Core 零第三方依赖", Core_HasNoPackageReference);
            Run("架构守卫：ConfigKit.Core 不引用任何 Sources.*", Core_DoesNotReferenceSources);
            Run("架构守卫：**只有** Sources.Xlsx 允许引第三方", OnlyXlsxMayUseThirdParty);

            Console.WriteLine();
            Console.WriteLine($"=== 通过 {s_passed} 条，失败 {s_failed} 条 ===");
            return s_failed == 0 ? 0 : 1;
        }

        // ====================================================================
        //  断言小工具
        // ====================================================================

        private static void Run(string name, Action body)
        {
            try
            {
                body();
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

        private static void CheckContains(string haystack, string needle, string label)
        {
            if (haystack == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
            {
                throw new Exception($"{label}：文本里找不到 <{needle}>\n实际文本：\n{haystack}");
            }
        }

        private static void CheckNotContains(string haystack, string needle, string label)
        {
            if (haystack != null && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0)
            {
                throw new Exception($"{label}：文本里**不该**出现 <{needle}>，但它出现了");
            }
        }

        // ====================================================================
        //  测试夹具：用 `|` 分隔单元格，读起来像表格
        // ====================================================================

        private static IReadOnlyList<string[]> Grid(params string[] rows)
        {
            return rows.Select(row => row.Split('|')).ToArray();
        }

        /// <summary>一张合法的 Skill 表 + Buff 表（供外键引用）。</summary>
        private static InMemoryTableSource ValidSource()
        {
            InMemoryTableSource source = new InMemoryTableSource();

            source.AddTable("Skill", Grid(
                "id|name|damage",
                "编号|名称|伤害",
                "int|string|int",
                "key|len(1,16)|range(1,99999)",
                "2001|火球|100",
                "2002|冰箭|80"));

            source.AddTable("Buff", Grid(
                "id|name",
                "编号|名称",
                "int|string",
                "key|len(1,16)",
                "3001|灼烧",
                "3002|冰冻"));

            return source;
        }

        /// <summary>一张合法的 Hero 表（字段覆盖 int / string / float+view / ref[] / 可空 ref）。</summary>
        private static IReadOnlyList<string[]> ValidHeroRows()
        {
            return Grid(
                "id|name|hp|moveSpeed|critRate|camDist|skillIds|buffId",
                "编号|名称|生命值|移动速度（毫米/秒）|暴击率（万分比）|相机距离（米）|技能列表|增益",
                "int|string|int|int|int|float|ref:Skill[]|ref:Buff?",
                "key|len(1,16)|range(1,999999)|min(0)|range(0,10000)|view||",
                "1001|剑士|1200|5000|1500|8.5|2001,2002|3001",
                "1002|法师|800|4800|500|9.0|2002|");
        }

        /// <summary>跑一遍：解析表头 + 校验数据，把诊断收集器还回来。</summary>
        private static DiagnosticBag Validate(InMemoryTableSource source, ConfigPolicy policy = null)
        {
            ConfigPolicy actualPolicy = policy ?? ConfigPolicy.CreateDefault();
            SchemaReader reader = new SchemaReader(actualPolicy);
            ValidationEngine engine = new ValidationEngine(actualPolicy);

            DiagnosticBag diagnostics = new DiagnosticBag();
            ConfigSet set = new ConfigSet();

            foreach (RawTable raw in source.ReadAll(diagnostics))
            {
                set.Add(new ConfigTable(reader.Read(raw, diagnostics), raw));
            }

            engine.Validate(set, diagnostics);
            return diagnostics;
        }

        /// <summary>诊断里的编号集合（方便断言"有没有报这一类"）。</summary>
        private static List<string> Codes(DiagnosticBag bag)
        {
            return bag.Items.Select(item => item.Code).ToList();
        }

        private static Diagnostic FirstOf(DiagnosticBag bag, string code)
        {
            Diagnostic found = bag.Items.FirstOrDefault(item => item.Code == code);
            Check(found != null, $"没有任何一条 {code} 诊断。实际编号：{string.Join(", ", Codes(bag))}");
            return found;
        }

        // ====================================================================
        //  位置与格式化
        // ====================================================================

        private static void ColumnLetters()
        {
            CheckEqual("A", SourceLocation.ToColumnLetter(1), "第 1 列");
            CheckEqual("Z", SourceLocation.ToColumnLetter(26), "第 26 列");
            CheckEqual("AA", SourceLocation.ToColumnLetter(27), "第 27 列");
            CheckEqual("AZ", SourceLocation.ToColumnLetter(52), "第 52 列");
            CheckEqual("BA", SourceLocation.ToColumnLetter(53), "第 53 列");
            CheckEqual(string.Empty, SourceLocation.ToColumnLetter(0), "第 0 列（不指列）");
        }

        /// <summary>
        /// **这是 C2 的验收标准**：故意写错一格（`hp = 0` 越界），
        /// 报错必须能定位到 文件 + 表 + 行 + 列，而且形状和 `Docs\17` §8.1 一模一样。
        /// </summary>
        private static void DiagnosticFormatContract()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", Grid(
                "id|name|hp|moveSpeed|critRate|camDist|skillIds|buffId",
                "编号|名称|生命值|移动速度（毫米/秒）|暴击率（万分比）|相机距离（米）|技能列表|增益",
                "int|string|int|int|int|float|ref:Skill[]|ref:Buff?",
                "key|len(1,16)|range(1,999999)|min(0)|range(0,10000)|view||",
                "1001|剑士|0|5000|1500|8.5|2001,2002|3001"), "Hero.csv");

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.OutOfRange);

            string text = new TextDiagnosticFormatter().Format(diagnostic);

            string expected =
                "[配置表] Hero.csv › Hero 第 5 行 «hp»（第 3 列，Excel 列号 C）：" + Environment.NewLine +
                "    超出允许范围  [CFG0009]" + Environment.NewLine +
                "    期望：range(1,999999)" + Environment.NewLine +
                "    实际：0";

            CheckEqual(expected, text, "报错文本（契约形状）");

            // 四要素逐个点名断言：即使形状改了，也要保证这四样都在
            CheckContains(text, "Hero.csv", "要素①文件");
            CheckContains(text, "Hero", "要素②表");
            CheckContains(text, "第 5 行", "要素③行（**Excel 真实行号**）");
            CheckContains(text, "Excel 列号 C", "要素④列");
        }

        private static void TableLevelLocationReadsWell()
        {
            SourceLocation location = new SourceLocation("Hero.csv", "heroes",
                SourceLocation.NoRow, SourceLocation.NoColumn, null);

            CheckEqual("Hero.csv › heroes", location.ToString(), "表级位置的可读文本");
        }

        // ====================================================================
        //  表头解析
        // ====================================================================

        private static void ValidSet_HasNoErrors()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", ValidHeroRows(), "Hero.csv");

            DiagnosticBag diagnostics = Validate(source);

            CheckEqual(0, diagnostics.ErrorCount,
                "合法表不该有错误。实际：\n" + Render(diagnostics));
        }

        private static void UnknownType_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|hp",
                "编号|生命值",
                "int|integer",
                "key|range(1,10)",
                "1|5"));

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.UnknownType);
            CheckEqual(2, diagnostic.Location.Column, "出错的是第 2 列");
        }

        private static void UnknownRule_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|hp",
                "编号|生命值",
                "int|int",
                "key|ranage(1,999999)",
                "1|5"));

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.UnknownRule);
            CheckEqual("ranage(1,999999)", diagnostic.Actual, "原样回显写错的规则");
        }

        private static void RuleArity_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|hp",
                "编号|生命值",
                "int|int",
                "key|range(1)",
                "1|5"));

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.UnknownRule);
            CheckContains(diagnostic.Message, "需要 2 个参数", "参数个数提示");
        }

        private static void FloatWithoutView_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|speed",
                "编号|速度",
                "int|float",
                "key|min(0)",
                "1|1.5"));

            DiagnosticBag diagnostics = Validate(source);
            FirstOf(diagnostics, DiagnosticCodes.FloatWithoutViewFlag);
        }

        private static void FloatWithView_IsAccepted()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|speed",
                "编号|速度",
                "int|float",
                "key|min(0);view",
                "1|1.5"));

            DiagnosticBag diagnostics = Validate(source);
            CheckEqual(0, diagnostics.ErrorCount, "标了 view 的 float 应当通过。实际：\n" + Render(diagnostics));
        }

        private static void BrokenHeader_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id||hp",
                "编号||生命值",
                "int||int",
                "key||range(1,10)",
                "1||5"));

            DiagnosticBag diagnostics = Validate(source);
            FirstOf(diagnostics, DiagnosticCodes.BrokenColumnHeader);
        }

        private static void BadTableName_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("hero_table", Grid(
                "id",
                "编号",
                "int",
                "key",
                "1"));

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.Naming);
            CheckEqual(1, diagnostic.Location.Row, "表名问题要指向字段名那一行（表级诊断也要有位置）");
        }

        // ====================================================================
        //  数据校验
        // ====================================================================

        private static void EmptyRequiredCell_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|hp",
                "编号|生命值",
                "int|int",
                "key|range(1,10)",
                "1|"));

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.UnexpectedEmpty);

            CheckEqual(5, diagnostic.Location.Row, "行号是 Excel 真实行号");
            CheckEqual("B", diagnostic.Location.ColumnLetter, "hp 是这一表的第 2 列 → B");
        }

        private static void NullableEmpty_IsAccepted()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", ValidHeroRows(), "Hero.csv");

            DiagnosticBag diagnostics = Validate(source);
            Check(!Codes(diagnostics).Contains(DiagnosticCodes.UnexpectedEmpty),
                "`ref:Buff?` 留空是合法的，不该报 CFG0006");
        }

        private static void DashPlaceholder_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|name",
                "编号|名称",
                "int|string",
                "key|len(1,16)",
                "1|-"));

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.BadEmptyPlaceholder);
            CheckContains(diagnostic.Message, "留空", "提示里要说清正确写法");
        }

        private static void BadInteger_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|hp",
                "编号|生命值",
                "int|int",
                "key|range(1,10)",
                "1|abc"));

            DiagnosticBag diagnostics = Validate(source);
            FirstOf(diagnostics, DiagnosticCodes.ValueParseFailed);
        }

        private static void OutOfRange_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|hp",
                "编号|生命值",
                "int|int",
                "key|range(1,10)",
                "1|99"));

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.OutOfRange);
            CheckEqual("range(1,10)", diagnostic.Expected, "期望里回显规则原文");
            CheckEqual("99", diagnostic.Actual, "实际是单元格原文");
        }

        private static void IntOverflow_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|hp",
                "编号|生命值",
                "int|int",
                "key|",
                "1|99999999999"));

            DiagnosticBag diagnostics = Validate(source);
            FirstOf(diagnostics, DiagnosticCodes.OutOfRange);
        }

        private static void Length_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|name",
                "编号|名称",
                "int|string",
                "key|len(1,3)",
                "1|这个名字太长了"));

            DiagnosticBag diagnostics = Validate(source);
            FirstOf(diagnostics, DiagnosticCodes.LengthOutOfRange);
        }

        private static void NotUnique_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|name",
                "编号|名称",
                "int|string",
                "key|len(1,16);unique",
                "1|同名",
                "2|同名"));

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.NotUnique);
            CheckEqual(6, diagnostic.Location.Row, "冲突报在**后面那一行**上");
            CheckContains(diagnostic.Message, "第 5 行", "说明里指出第一次出现在哪一行");
        }

        private static void DuplicateKey_NamesBothRows()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|name",
                "编号|名称",
                "int|string",
                "key|len(1,16)",
                "1001|甲",
                "1001|乙"));

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.DuplicateKey);

            CheckEqual(6, diagnostic.Location.Row, "重复的那一行");
            CheckContains(diagnostic.Message, "第 5 行", "说明里指出第一行");
            CheckEqual("1001", diagnostic.Actual, "实际值");
        }

        private static void ForeignKeyValueMissing_IsReported()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", Grid(
                "id|skillIds",
                "编号|技能列表",
                "int|ref:Skill[]",
                "key|",
                "1001|2001,9999"), "Hero.csv");

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.ForeignKeyMissing);
            CheckContains(diagnostic.Message, "9999", "指出是哪个值");
            CheckContains(diagnostic.Message, "没有这个主键", "说清是「值不存在」");
        }

        private static void ForeignKeyTableMissing_IsReported()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|itemId",
                "编号|道具",
                "int|ref:Item",
                "key|",
                "1001|1"), "Hero.csv");

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.ForeignKeyMissing);
            CheckContains(diagnostic.Message, "Item", "指出是哪个表");
            CheckContains(diagnostic.Message, "不存在", "说清是\"表不存在\"（和值不存在分开）");
        }

        private static void BlankRowInMiddle_IsReported()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", Grid(
                "id|name",
                "编号|名称",
                "int|string",
                "key|len(1,16)",
                "1001|甲",
                "|",
                "1002|乙"), "Hero.csv");

            DiagnosticBag diagnostics = Validate(source);
            Diagnostic diagnostic = FirstOf(diagnostics, DiagnosticCodes.BlankRowInMiddle);
            CheckEqual(7, diagnostic.Location.Row, "指向空行**之后**那条数据");
        }

        private static void AllErrors_AreReportedAtOnce()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|hp|name",
                "编号|生命值|名称",
                "int|int|string",
                "key|range(1,10)|len(1,2)",
                "0|99|太长了吧"), "Hero.csv");

            DiagnosticBag diagnostics = Validate(source);

            // 三处错误（主键 < 1 / hp 越界 / name 太长）应当一次全报出来
            Check(diagnostics.ErrorCount >= 3,
                $"一次应当报出全部 {3} 类错误，实际只有 {diagnostics.ErrorCount} 条：\n" + Render(diagnostics));
        }

        private static void FatalStructure_StopsDataValidation()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|hp",
                "编号|生命值",
                "int|bogus",
                "key|range(1,10)",
                "9999|9999"), "Hero.csv");

            DiagnosticBag diagnostics = Validate(source);

            Check(Codes(diagnostics).Contains(DiagnosticCodes.UnknownType), "结构错误要报");
            Check(!Codes(diagnostics).Contains(DiagnosticCodes.OutOfRange),
                "结构都不成立时不该再校验数据（否则连锁误报会把真正的错埋掉）");
        }

        private static void Diagnostics_AreSortedByLocation()
        {
            DiagnosticBag bag = new DiagnosticBag();
            bag.Error("X", new SourceLocation("b.csv", "B", 2, 1, "c"), "m");
            bag.Error("X", new SourceLocation("a.csv", "A", 9, 1, "c"), "m");
            bag.Error("X", new SourceLocation("a.csv", "A", 3, 5, "c"), "m");
            bag.SortByLocation();

            CheckEqual("a.csv", bag.Items[0].Location.File, "先按文件");
            CheckEqual(3, bag.Items[0].Location.Row, "同文件按行");
            CheckEqual(9, bag.Items[1].Location.Row, "同文件按行");
            CheckEqual("b.csv", bag.Items[2].Location.File, "后按文件");
        }

        // ====================================================================
        //  分隔符来源
        // ====================================================================

        private static void Csv_SplitsQuotedCells()
        {
            DelimitedTableSource source = new DelimitedTableSource(".");
            IReadOnlyList<string> cells = source.SplitLine("1001,\"a,b\",\"他说\"\"你好\"\"\",5");

            CheckEqual(4, cells.Count, "单元格个数");
            CheckEqual("1001", cells[0], "第 1 格");
            CheckEqual("a,b", cells[1], "引号里的逗号不算分隔符");
            CheckEqual("他说\"你好\"", cells[2], "两个引号 = 一个引号");
            CheckEqual("5", cells[3], "第 4 格");
        }

        // ====================================================================
        //  代码生成
        // ====================================================================

        private static ExportReport Export(ITableSource source)
        {
            ConfigPolicy policy = ConfigPolicy.CreateDefault();
            ExportPipeline pipeline = new ExportPipeline(
                policy, new CSharpConfigEmitter(), new EmitOptions { Namespace = "Test.Config" });

            return pipeline.Run(source);
        }

        private static string FileContent(ExportReport report, string relativePath)
        {
            EmittedFile found = report.Files.FirstOrDefault(file => file.RelativePath == relativePath);

            Check(found != null,
                $"没生成 {relativePath}。实际生成：{string.Join(" / ", report.Files.Select(file => file.RelativePath))}");

            return found.Content;
        }

        private static void CodeGen_FieldsAndTypes()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", ValidHeroRows(), "Hero.csv");

            ExportReport report = Export(source);
            Check(report.Succeeded, "合法表应当导出成功：\n" + Render(report.Diagnostics));

            string code = FileContent(report, "Config_Hero.cs");

            CheckContains(code, "public sealed class Config_Hero", "行类型");
            CheckContains(code, "public int id;", "主键字段");
            CheckContains(code, "public string name;", "字符串字段");
            CheckContains(code, "public float camDist;", "float 字段（非可空）");
            CheckContains(code, "public int[] skillIds;", "数组外键 → int[]");
            // ⚠️ 可空 ref 生成的是 `int`（**不是 `int?`**）：Unity 的序列化器不支持可空值类型
            CheckContains(code, "public int buffId;", "可空外键 → int（0 表示无引用）");
            CheckContains(code, "0 表示无引用", "字段注释要写明 0 的含义");
            CheckContains(code, "namespace Test.Config", "命名空间可配");
            CheckContains(code, "<auto-generated />", "文件头要标自动生成");
        }

        private static void CodeGen_Literals()
        {
            CheckEqual("1001", Literal("int", "1001"), "int 字面量");
            CheckEqual("8.5f", Literal("float", "8.5"), "float 要带 f 后缀");
            CheckEqual("true", Literal("bool", "1"), "bool 认 1");
            CheckEqual("false", Literal("bool", "0"), "bool 认 0");
            CheckEqual("\"剑士\"", Literal("string", "剑士"), "字符串带引号");
            CheckEqual("\"他说\\\"你好\\\"\"", Literal("string", "他说\"你好\""), "字符串里的引号要转义");
            CheckEqual("EDamageType.Fire", Literal("enum:EDamageType", "Fire"), "枚举写成员名");
            CheckEqual("new int[] { 1, 2 }", Literal("int[]", "1,2"), "数组字面量");
            // 空值映射与 CSharpTypeOf 必须一致：可空 ref 的字段类型是 `int`，所以"无引用"是 0
            CheckEqual("0", Literal("ref:Buff?", ""), "可空 ref 留空 → 0");
            CheckEqual("new int[0]", Literal("ref:Skill[]?", ""), "可空 ref 数组留空 → 空数组");
        }

        private static string Literal(string typeText, string text)
        {
            ColumnType type;
            string error;

            Check(ColumnType.TryParse(typeText, out type, out error), $"类型解析失败：{typeText}（{error}）");

            ColumnSchema column = new ColumnSchema(0, "c", "注释", typeText, type,
                new List<RuleSpec>());

            return CSharpConfigEmitter.LiteralOf(column, text);
        }

        private static void CodeGen_ScriptableObjectHasIndex()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", ValidHeroRows(), "Hero.csv");

            string code = FileContent(Export(source), "HeroConfig.cs");

            CheckContains(code, "public sealed class HeroConfig : ScriptableObject", "SO 类型");
            CheckContains(code, "public List<Config_Hero> rows", "行列表");
            CheckContains(code, "Dictionary<int, Config_Hero>", "主键索引（CFG-08 避免线性查找）");
            CheckContains(code, "public Config_Hero Get(int id)", "按主键取");
            CheckContains(code, "public bool TryGet(int id, out Config_Hero row)", "TryGet");
            CheckContains(code, "private void OnEnable()", "Unity 加载时重建索引");
            CheckContains(code, "using UnityEngine;", "SO 需要 UnityEngine");

            // TSV 加载器（Unity 导入器就靠它把数据灌进 SO）
            CheckContains(code, "public void LoadFromTsv(string text)", "TSV 加载器");
            CheckContains(code, "Config_Hero row = new Config_Hero();", "加载器按行类型建对象");
            CheckContains(code, "ParseInt(Cell(cells, header, \"buffId\"))", "可空 ref 空着 → ParseInt 得到 0");
            CheckContains(code, "ParseIntArray(Cell(cells, header, \"skillIds\"))", "数组外键解析");
            CheckContains(code, "Unescape(Cell(cells, header, \"name\"))", "字符串要还原转义");
            // ⚠️ 判据用**调用点**（`JsonUtility.FromJson`）而不是裸词 `JsonUtility` ——
            //    生成的注释里就写着"不用 JsonUtility"，裸词会被自己的注释骗过去。
            //    （同一个坑第 6 次：机械检查命中注释。这次发生在**生成物**里。）
            CheckNotContains(code, "JsonUtility.FromJson", "不该用 JsonUtility 装数据（可空值类型与 enum 都有坑）");
        }

        /// <summary>
        /// 注释消毒：策划在注释行里写连续两个连字符时，生成出来的 `///` 里**不能**出现它。
        /// <para>⚠️ W1：XML 文档注释里出现连续两个连字符会让注释不合法。</para>
        /// </summary>
        private static void CodeGen_SanitizesDocComments()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|hp",
                "编号|生命--值",          // ← 故意在注释里写连续两个连字符
                "int|int",
                "key|range(1,10)",
                "1|5"));

            string code = FileContent(Export(source), "Config_Hero.cs");

            Check(code.Contains("生命—值", StringComparison.Ordinal), "应当被换成长破折号");

            // 逐行检查 `///` 行里没有连续两个连字符
            foreach (string line in code.Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    Check(!trimmed.Contains("--", StringComparison.Ordinal),
                        $"生成的 /// 行里出现了连续两个连字符：{trimmed}");
                }
            }
        }

        // ====================================================================
        //  TSV 中间产物
        // ====================================================================

        private static void Tsv_HeaderAndRows()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", ValidHeroRows(), "Hero.csv");

            ExportReport report = new ExportPipeline(
                ConfigPolicy.CreateDefault(), new TsvConfigEmitter()).Run(source);

            Check(report.Succeeded, "应当成功：\n" + Render(report.Diagnostics));

            string tsv = FileContent(report, "Hero.tsv");
            string[] lines = tsv.Split('\n');

            CheckEqual("id\tname\thp\tmoveSpeed\tcritRate\tcamDist\tskillIds\tbuffId", lines[0],
                "表头是**字段名**（按名字对列，调换列顺序不会错位）");
            CheckEqual(2, report.Tables[0].RowCount, "行数");

            // 第一行数据：逐格核对（`\t` 分隔）
            string[] first = lines[1].Split('\t');
            CheckEqual(8, first.Length, "8 个字段");
            CheckEqual("1001", first[0], "id");
            CheckEqual("剑士", first[1], "name");
            CheckEqual("2001,2002", first[6], "数组原样（类型转换在 Unity 侧做）");
            CheckEqual("3001", first[7], "可空 ref 有值");
        }

        private static void Tsv_EscapesTabsAndNewlines()
        {
            // CSV 允许引号里带换行；如果直接写进 TSV，那一格会把**整行拆散** → 静默错位
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|note",
                "编号|备注",
                "int|string",
                "key|",
                "1|第一行\t带制表符"),
                "Hero.csv");

            ExportReport report = new ExportPipeline(
                ConfigPolicy.CreateDefault(), new TsvConfigEmitter()).Run(source);

            Check(report.Succeeded, "应当成功：\n" + Render(report.Diagnostics));

            string tsv = FileContent(report, "Hero.tsv");
            string[] lines = tsv.Split('\n');

            // 数据行必须仍然是 2 格（制表符被转义，没有把行拆散）
            CheckEqual("1\t第一行\\t带制表符", lines[1], "制表符要转义成 \\t，否则整行错位");
        }

        /// <summary>
        /// **生成代码的注释密度规则**（2026-09-22 由负责人明确）：
        /// <para>
        /// 只有三处**必定**有注释 ——
        /// ① **文件开头**、② **变量名后**（行尾）、③ **类名 / 方法名前**；
        /// **其他地方非必要不加注释。**
        /// </para>
        /// <para>
        /// 这条做成机械检查，因为它太容易被"顺手多写一句"破坏，
        /// 而生成物的注释一旦膨胀，每次导出的 diff 都变脏、也没人读。
        /// </para>
        /// </summary>
        private static void CodeGen_CommentDensityRule()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", ValidHeroRows(), "Hero.csv");

            ExportReport report = Export(source);
            Check(report.Succeeded, "应当成功：\n" + Render(report.Diagnostics));

            AssertCommentRule(FileContent(report, "Config_Hero.cs"), "Config_Hero.cs");
            AssertCommentRule(FileContent(report, "HeroConfig.cs"), "HeroConfig.cs");
        }

        /// <summary>逐行检查生成代码的注释规则。</summary>
        private static void AssertCommentRule(string code, string fileName)
        {
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            bool sawNamespace = false;
            int fieldCount = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("namespace ", StringComparison.Ordinal))
                {
                    sawNamespace = true;
                    continue;
                }

                // ① 文件开头（namespace 之前）允许 `//` 注释
                if (!sawNamespace)
                {
                    continue;
                }

                // ③ 类名 / 方法名前：`///` 允许，但**下一行必须是类或方法**（不能是字段）
                if (trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    string next = NextNonDocLine(lines, i + 1);
                    bool isDeclaration = next.Contains(" class ", StringComparison.Ordinal) ||
                                         next.Contains("(", StringComparison.Ordinal);

                    Check(isDeclaration,
                        $"{fileName} 第 {i + 1} 行的 /// 注释**不是**在类名/方法名前（下一行是「{next.Trim()}」）—— " +
                        "规则：字段的注释要写在**变量名后**的行尾");
                    continue;
                }

                // 其它地方**不许**有独立注释行（② 行尾注释不算，它在代码后面）
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    throw new Exception(
                        $"{fileName} 第 {i + 1} 行有独立注释行「{trimmed}」—— " +
                        "规则：注释只允许在 文件头 / 变量名后 / 类名方法名前");
                }

                // ② 字段声明必须带**行尾注释**
                if (IsFieldDeclaration(trimmed))
                {
                    fieldCount++;
                    Check(line.Contains("//", StringComparison.Ordinal),
                        $"{fileName} 第 {i + 1} 行的字段「{trimmed}」没有行尾注释 —— 规则：变量名后必加注释");
                }
            }

            // 正对照：确实检查到了字段（否则"全部通过"可能只是没扫到东西）
            Check(fieldCount > 0, $"{fileName} 里一个字段都没扫到，检查可能没在工作");
        }

        /// <summary>
        /// 跳过连续的 `///` 行与**特性行**（`[Serializable]` / `[CreateAssetMenu(...)]`），
        /// 返回第一个真正的声明行。
        /// <para>
        /// ⚠️ 跳特性这一步是自测当场暴露的：类名前的注释后面跟的是 `[Serializable]`，
        /// 而"下一行不是声明"被误判成违规。**守卫的判据要覆盖语言的实际写法**，
        /// 不能只覆盖最常见的那种。
        /// </para>
        /// </summary>
        private static string NextNonDocLine(string[] lines, int start)
        {
            for (int i = start; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();

                if (trimmed.StartsWith("///", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    continue;
                }

                return lines[i];
            }

            return string.Empty;
        }

        /// <summary>
        /// 像不像字段声明（`public int id;  // 注释` / `private Dictionary&lt;...&gt; m_x;  // 注释`）。
        /// <para>
        /// ⚠️ **必须先剥掉行尾注释再判断** —— 字段现在以注释结尾，
        /// 直接 `EndsWith(";")` 会永远为假（自测的正对照当场抓到了这一点）。
        /// </para>
        /// </summary>
        private static bool IsFieldDeclaration(string trimmed)
        {
            int comment = trimmed.IndexOf("//", StringComparison.Ordinal);
            string code = (comment >= 0 ? trimmed.Substring(0, comment) : trimmed).TrimEnd();

            if (!code.StartsWith("public ", StringComparison.Ordinal) &&
                !code.StartsWith("private ", StringComparison.Ordinal) &&
                !code.StartsWith("internal ", StringComparison.Ordinal))
            {
                return false;
            }

            return code.EndsWith(";", StringComparison.Ordinal) &&
                   !code.Contains("(", StringComparison.Ordinal) &&
                   !code.Contains(" class ", StringComparison.Ordinal);
        }

        // ====================================================================
        //  JSON 产物
        // ====================================================================

        private static string EmitJsonOf(InMemoryTableSource source, string tableName)
        {
            ExportReport report = new ExportPipeline(
                ConfigPolicy.CreateDefault(), new JsonConfigEmitter()).Run(source);

            Check(report.Succeeded, "应当成功：\n" + Render(report.Diagnostics));
            return FileContent(report, tableName + ".json");
        }

        /// <summary>
        /// **手写的 JSON 必须能被真解析器吃下去。**
        /// <para>
        /// ⚠️ 这条是"验证器思路"的直接应用：**别让"我觉得格式对"当判据**。
        /// .NET 8 自带 `System.Text.Json`，用它解析一遍 ——
        /// 转义漏了、逗号多写了、括号不配平，都会当场抛。
        /// （这也解决了"我手写 JSON 而不引第三方库"的可信度问题：**写可以手写，验必须用真解析器**。）
        /// </para>
        /// </summary>
        private static void Json_IsParsableByJsonParser()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", ValidHeroRows(), "Hero.csv");

            string json = EmitJsonOf(source, "Hero");

            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);

            CheckEqual("Hero", document.RootElement.GetProperty("table").GetString(), "table");
            CheckEqual(8, document.RootElement.GetProperty("fields").GetArrayLength(), "fields");
            CheckEqual(2, document.RootElement.GetProperty("rows").GetArrayLength(), "rows");

            System.Text.Json.JsonElement first = document.RootElement.GetProperty("rows")[0];
            CheckEqual(1001, first.GetProperty("id").GetInt32(), "id");
            CheckEqual("剑士", first.GetProperty("name").GetString(), "name（中文原样，不转 \\u）");
            CheckEqual(1200, first.GetProperty("hp").GetInt32(), "hp");
            CheckEqual(2, first.GetProperty("skillIds").GetArrayLength(), "数组是 JSON 数组");
            CheckEqual(2001, first.GetProperty("skillIds")[0].GetInt32(), "数组元素");
        }

        private static void Json_ShapeAndValues()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("T", Grid(
                "id|rate|flag|kind|ids|refId",
                "编号|比例|开关|类型|列表|引用",
                "int|float|bool|enum:EDamage|int[]|ref:Buff?",
                "key|view|",
                "1|1.5|1|Fire|3,4|"),
                "T.csv");

            // Buff 表（让 ref 能解析；虽然这里留空，但类型仍要存在才不报"表不存在"）
            InMemoryTableSource withBuff = source;
            withBuff.AddTable("Buff", Grid(
                "id|name",
                "编号|名称",
                "int|string",
                "key|",
                "3001|灼烧"), "Buff.csv");

            string json = FileContent(new ExportPipeline(
                ConfigPolicy.CreateDefault(), new JsonConfigEmitter()).Run(withBuff), "T.json");

            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement row = document.RootElement.GetProperty("rows")[0];

            CheckEqual(1.5, row.GetProperty("rate").GetDouble(), "float 是 JSON 数字");
            Check(row.GetProperty("flag").GetBoolean(), "bool 是 true/false");
            CheckEqual("Fire", row.GetProperty("kind").GetString(), "枚举写**成员名**（人可读）");
            CheckEqual(2, row.GetProperty("ids").GetArrayLength(), "int[] 是数组");
            CheckEqual(0, row.GetProperty("refId").GetInt32(),
                "可空 ref 留空 → **0**（与 C# 的 int + 加载器一致，不是 null）");
        }

        private static void Json_Escapes()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("T", Grid(
                "id|note",
                "编号|备注",
                "int|string",
                "key|",
                "1|他说\"你好\"\\还有\t制表符"),
                "T.csv");

            string json = EmitJsonOf(source, "T");

            // 先让真解析器确认合法，再确认还原出来的值**一个字符不差**
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
            string note = document.RootElement.GetProperty("rows")[0].GetProperty("note").GetString();

            CheckEqual("他说\"你好\"\\还有\t制表符", note, "转义后原样还原");
        }

        /// <summary>
        /// **三份产物必须对同一张表给出同一个答案。**
        /// <para>
        /// 否则换一份产物读就会得到不同的值 —— 那是**静默不一致**。
        /// 这条守的是"可空 ref 的三个表现"：C# 是 `int`、TSV 是空串、JSON 是 `0`。
        /// </para>
        /// </summary>
        private static void Emitters_AgreeOnNullableRef()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", ValidHeroRows(), "Hero.csv");

            // C#：字段类型必须是 `int` 而不是 `int?`（Unity 不支持可空值类型）
            ExportReport csharp = new ExportPipeline(
                ConfigPolicy.CreateDefault(), new CSharpConfigEmitter()).Run(source);
            CheckContains(FileContent(csharp, "Config_Hero.cs"), "public int buffId;", "C# 侧");
            CheckContains(FileContent(csharp, "HeroConfig.cs"),
                "ParseInt(Cell(cells, header, \"buffId\"))", "加载器把空串解析成 0");

            // TSV：留空
            ExportReport tsv = new ExportPipeline(
                ConfigPolicy.CreateDefault(), new TsvConfigEmitter()).Run(source);
            string[] lines = FileContent(tsv, "Hero.tsv").Split('\n');
            CheckEqual("", lines[2].Split('\t')[7], "TSV 侧：第 2 条数据的 buffId 是空串");

            // JSON：0（不是 null）
            ExportReport json = new ExportPipeline(
                ConfigPolicy.CreateDefault(), new JsonConfigEmitter()).Run(source);
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(FileContent(json, "Hero.json"));
            CheckEqual(0, document.RootElement.GetProperty("rows")[1].GetProperty("buffId").GetInt32(),
                "JSON 侧：0");
        }

        // ====================================================================
        //  可空支持范围
        // ====================================================================

        private static void NullableValueType_IsRejected()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|bonus",
                "编号|加成",
                "int|int?",
                "key|",
                "1|"), "Hero.csv");

            ExportReport report = Export(source);

            Check(!report.Succeeded, "`int?` 应当被拦下");
            FirstOf(report.Diagnostics, DiagnosticCodes.UnsupportedNullable);
        }

        private static void NullableRefAndString_AreAccepted()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", ValidHeroRows(), "Hero.csv");

            ExportReport report = Export(source);

            // ValidHeroRows 里有 `ref:Skill[]`（不可空）与 `ref:Buff?`（可空）
            Check(report.Succeeded, "可空 ref 应当允许：\n" + Render(report.Diagnostics));
        }

        // ====================================================================
        //  流水线
        // ====================================================================

        private static void Pipeline_EmitsTwoFilesPerTable()
        {
            InMemoryTableSource source = ValidSource();
            source.AddTable("Hero", ValidHeroRows(), "Hero.csv");

            ExportReport report = Export(source);

            CheckEqual(3, report.Tables.Count, "读到 3 张表");
            CheckEqual(6, report.Files.Count, "每张表 2 个文件（行类型 + SO）");
            CheckEqual(2, report.Tables.First(t => t.TableName == "Hero").RowCount, "Hero 有 2 条数据");
        }

        private static void Pipeline_EmitsNothingWhenInvalid()
        {
            InMemoryTableSource source = new InMemoryTableSource();
            source.AddTable("Hero", Grid(
                "id|hp",
                "编号|生命值",
                "int|int",
                "key|range(1,10)",
                "1|999"), "Hero.csv");   // ← hp 越界

            ExportReport report = Export(source);

            Check(!report.Succeeded, "有错时不该算成功");
            CheckEqual(0, report.Files.Count,
                "**有错就一个文件都不产出**（半成品比没有更危险）");
            Check(report.Diagnostics.ErrorCount >= 1, "要报出错误");
        }

        /// <summary>端到端：真的在临时目录里写一个 CSV 文件，走完整条链。</summary>
        private static void EndToEnd_CsvDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "configkit-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                // ⚠️ 含逗号的格子**必须用双引号包起来** —— 否则 CSV 会把它拆成两格
                //    （这条是端到端测试当场抓出来的：`len(1,16)` 被拆成 `len(1` 和 `16)`）
                File.WriteAllText(Path.Combine(directory, "Hero.csv"),
                    "id,name,hp\n" +
                    "编号,名称,生命值\n" +
                    "int,string,int\n" +
                    "key,\"len(1,16)\",\"range(1,999999)\"\n" +
                    "1001,剑士,1200\n",
                    new UTF8Encoding(false));

                ExportReport report = Export(new DelimitedTableSource(directory));

                Check(report.Succeeded, "端到端应当成功：\n" + Render(report.Diagnostics));
                CheckEqual(1, report.Tables.Count, "读到 1 张表");
                CheckEqual("Hero", report.Tables[0].TableName, "**文件名 = 表名**");
                CheckEqual(1, report.Tables[0].RowCount, "1 条数据");

                string code = FileContent(report, "Config_Hero.cs");
                CheckContains(code, "public int hp;", "字段");
                // C# 里只有**结构**（字段 + 注释），数据本身进的是 .asset 资产 ——
                // 所以表里的值不该出现在生成代码里
                CheckNotContains(code, "剑士", "数据不该进 C#");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        /// <summary>
        /// CSV 里含逗号的格子**没加引号**时，报错必须给出"像是被逗号拆开了"的提示。
        /// <para>这条是被端到端测试逼出来的：`len(1,16)` 被拆成 `len(1` / `16)`，
        /// 原来的报错只说"不认识的规则" —— 对，但人会往"规则名拼错了"的方向查。</para>
        /// </summary>
        private static void Csv_UnquotedComma_GivesActionableHint()
        {
            string directory = Path.Combine(Path.GetTempPath(), "configkit-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                File.WriteAllText(Path.Combine(directory, "Hero.csv"),
                    "id,hp\n" +
                    "编号,生命值\n" +
                    "int,int\n" +
                    "key,range(1,999999)\n" +     // ← 故意的：含逗号却没加引号
                    "1,100\n",
                    new UTF8Encoding(false));

                ExportReport report = Export(new DelimitedTableSource(directory));

                Check(!report.Succeeded, "没加引号应当报错");

                Diagnostic diagnostic = FirstOf(report.Diagnostics, DiagnosticCodes.UnknownRule);
                CheckContains(diagnostic.Message, "双引号", "提示里要说清正确写法");
                CheckContains(diagnostic.Message, "逗号拆开", "提示里要点出真正的原因");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        // ====================================================================
        //  架构守卫（⚠️ 守卫自己必须先被验一遍 —— W9）
        // ====================================================================

        /// <summary>
        /// 找出一个工程文件里的违规点（纯函数，方便负对照）。
        /// <para>
        /// ⚠️ **先剥掉 XML 注释再匹配。**
        /// 这条是自测跑出来的：`ConfigKit.Core.csproj` 的**注释里**写着
        /// "不得引用任何 ConfigKit.Sources.*"，于是守卫把这个字样当成了真违规。
        /// 这是本项目的老毛病（机械检查命中注释 —— W9 已经栽过 4 次），
        /// **修法必须落在守卫这一侧**：否则以后谁在注释里提一句就红，
        /// 大家就会开始"改注释让检查过去"，而守卫也就废了。
        /// </para>
        /// </summary>
        private static List<string> FindViolations(string csprojXml, bool allowThirdParty)
        {
            List<string> violations = new List<string>();
            string code = StripXmlComments(csprojXml);

            if (!allowThirdParty &&
                Regex.IsMatch(code, "<PackageReference\\s+Include=", RegexOptions.IgnoreCase))
            {
                violations.Add("出现了 <PackageReference>（本工程必须零第三方依赖）");
            }

            if (Regex.IsMatch(code, "ConfigKit\\.Sources\\.", RegexOptions.IgnoreCase))
            {
                violations.Add("引用了 ConfigKit.Sources.*（Core 不许认识具体来源）");
            }

            return violations;
        }

        /// <summary>
        /// 剥掉 XML 注释。
        /// <para>⚠️ **只留一处实现**：上一轮我在两个地方各写了一遍匹配，
        /// 于是"注释里的字样"这个坑被踩了两次。判据：一个检查逻辑只该存在一处。</para>
        /// </summary>
        private static string StripXmlComments(string csprojXml)
        {
            return Regex.Replace(csprojXml ?? string.Empty, "<!--.*?-->", string.Empty,
                RegexOptions.Singleline);
        }

        /// <summary>负对照：合成一段**含违规**的工程文本，检查函数必须报出来。</summary>
        private static void ArchitectureGuard_DetectsViolation()
        {
            const string bad = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""NPOI"" Version=""2.8.0"" />
    <ProjectReference Include=""..\ConfigKit.Sources.Xlsx\ConfigKit.Sources.Xlsx.csproj"" />
  </ItemGroup>
</Project>";

            List<string> violations = FindViolations(bad, allowThirdParty: false);

            CheckEqual(2, violations.Count, "负对照：两条违规都该被抓到");
            Check(violations[0].Contains("PackageReference"), "第一条是依赖违规");
            Check(violations[1].Contains("Sources"), "第二条是来源耦合违规");

            // 正对照：去掉违规内容后必须干净
            const string good = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <ProjectReference Include=""..\ConfigKit.Core\ConfigKit.Core.csproj"" />
  </ItemGroup>
</Project>";

            CheckEqual(0, FindViolations(good, allowThirdParty: false).Count, "正对照：干净工程不该报违规");
        }

        /// <summary>
        /// **注释里的字样不算违规**（守卫自己必须先过这一关）。
        /// <para>这条是被真实情况逼出来的：Core 的 csproj 注释里就写着那句"不许引用"。</para>
        /// </summary>
        private static void ArchitectureGuard_IgnoresComments()
        {
            const string onlyInComment = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <!-- ⚠️ 本工程【不得】引用任何 ConfigKit.Sources.*，也不许有 PackageReference -->
  <ItemGroup>
    <ProjectReference Include=""..\ConfigKit.Core\ConfigKit.Core.csproj"" />
  </ItemGroup>
</Project>";

            CheckEqual(0, FindViolations(onlyInComment, allowThirdParty: false).Count,
                "只出现在注释里的字样不该被判违规");

            const string realPlusComment = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <!-- 注释里也提一句 ConfigKit.Sources.Xlsx -->
  <ItemGroup>
    <ProjectReference Include=""..\ConfigKit.Sources.Xlsx\ConfigKit.Sources.Xlsx.csproj"" />
  </ItemGroup>
</Project>";

            CheckEqual(1, FindViolations(realPlusComment, allowThirdParty: false).Count,
                "真违规夹在注释旁边时，仍然必须抓到（剥注释不能把真问题一起剥掉）");
        }

        private static void Core_HasNoPackageReference()
        {
            string path = FindProjectFile("ConfigKit.Core");
            string xml = File.ReadAllText(path, Encoding.UTF8);
            List<string> violations = FindViolations(xml, allowThirdParty: false);

            Check(violations.Count == 0, $"ConfigKit.Core 有违规：{string.Join("；", violations)}\n({path})");
            CheckContains(xml, "netstandard2.1", "Core 必须钉在 netstandard2.1（要能丢进 Unity）");
        }

        private static void Core_DoesNotReferenceSources()
        {
            string path = FindProjectFile("ConfigKit.Core");
            string xml = File.ReadAllText(path, Encoding.UTF8);

            // ⚠️ 这里**必须复用会剥注释的守卫函数**，不能自己写一遍 `Contains`。
            //    第一次我就是自己写的，于是同一个坑在两个地方各踩了一遍
            //    （Core 的注释里写着"不许引用 ConfigKit.Sources.*"，被自己的 Contains 命中）。
            //    **判据：一个检查逻辑只该存在一处。**
            //    allowThirdParty: true —— 这条只负责"来源耦合"这一个维度，依赖那条由上面一条守。
            List<string> violations = FindViolations(xml, allowThirdParty: true);

            Check(violations.Count == 0,
                $"ConfigKit.Core 不能认识具体来源：{string.Join("；", violations)}\n({path})");
        }

        /// <summary>从自测程序所在目录往上找仓库里的 Tools\ConfigKit。</summary>
        private static string FindConfigKitRoot()
        {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "Tools", "ConfigKit", "Directory.Build.props");

                if (File.Exists(candidate))
                {
                    return Path.Combine(directory.FullName, "Tools", "ConfigKit");
                }

                directory = directory.Parent;
            }

            throw new Exception($"从 {AppContext.BaseDirectory} 向上找 Tools\\ConfigKit 没找到");
        }

        /// <summary>找某个工程的 csproj（单一实现：基于 <see cref="FindConfigKitRoot"/>）。</summary>
        private static string FindProjectFile(string projectName)
        {
            string candidate = Path.Combine(FindConfigKitRoot(), "src", projectName, projectName + ".csproj");

            if (!File.Exists(candidate))
            {
                throw new Exception($"找不到 {candidate}");
            }

            return candidate;
        }

        /// <summary>列出工具链下所有工程的 csproj（排除 obj 里的生成物）。</summary>
        private static List<string> FindAllProjects()
        {
            string[] files = Directory.GetFiles(FindConfigKitRoot(), "*.csproj", SearchOption.AllDirectories);
            List<string> result = new List<string>();

            for (int i = 0; i < files.Length; i++)
            {
                if (files[i].IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                result.Add(files[i]);
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// **生产代码（`src/`）里只有 `ConfigKit.Sources.Xlsx` 允许引第三方依赖。**
        /// <para>
        /// 这条守卫守的是"接缝优先"的核心收益：**最难搞的依赖被隔离在一个叶子里** ——
        /// 所以 NPOI 没还原时，Core / Delimited / Cli / 自测**照常构建、照常能跑**。
        /// 一旦有人往别的工程顺手加个包，这条就红。
        /// </para>
        /// <para>
        /// ⚠️ **`tests/` 下的探针不受这条限制**，而且必须有例外：
        /// `ConfigKit.XlsxProbe` 的职责就是"**用 NPOI 造一个真 .xlsx** 来喂适配器"，
        /// 没有 NPOI 它就不是探针了。规则要写准 —— 上一版写成"全仓库恰好一个"，
        /// 探针一加进来就红（守卫抓的是真事，但判据太粗）。
        /// </para>
        /// </summary>
        private static void OnlyXlsxMayUseThirdParty()
        {
            const string allowed = "ConfigKit.Sources.Xlsx";
            const string allowedProbe = "ConfigKit.XlsxProbe";

            List<string> projects = FindAllProjects();
            List<string> srcProjects = new List<string>();
            List<string> srcWithPackages = new List<string>();
            List<string> testWithPackages = new List<string>();

            for (int i = 0; i < projects.Count; i++)
            {
                string code = StripXmlComments(File.ReadAllText(projects[i], Encoding.UTF8));
                bool hasPackage = Regex.IsMatch(code, "<PackageReference\\s+Include=", RegexOptions.IgnoreCase);
                string name = Path.GetFileNameWithoutExtension(projects[i]);
                bool isSrc = projects[i].IndexOf("\\src\\", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isSrc)
                {
                    srcProjects.Add(name);

                    if (hasPackage)
                    {
                        srcWithPackages.Add(name);
                    }
                }
                else if (hasPackage)
                {
                    testWithPackages.Add(name);
                }
            }

            // ⚠️ 正对照：先确认"扫到了生产工程、也扫到了 Xlsx 那一个"。
            //    否则"恰好一个引第三方"可能只是"一个都没扫到"这种假绿。
            Check(srcProjects.Count >= 4, $"只扫到 {srcProjects.Count} 个生产工程，明显不对（是不是路径找错了）");
            Check(srcWithPackages.Contains(allowed),
                $"没扫到 {allowed} 的第三方依赖 —— 守卫可能压根没工作。扫到的：{string.Join(", ", srcWithPackages)}");

            CheckEqual(1, srcWithPackages.Count,
                $"生产代码里只允许 {allowed} 引第三方，实际 {srcWithPackages.Count} 个：{string.Join(", ", srcWithPackages)}");

            for (int i = 0; i < testWithPackages.Count; i++)
            {
                Check(testWithPackages[i] == allowedProbe,
                    $"{testWithPackages[i]} 不该引第三方（只有负责造 xlsx 的探针 {allowedProbe} 可以）");
            }
        }

        /// <summary>把诊断全部渲染出来（测试失败时给人看）。</summary>
        private static string Render(DiagnosticBag bag)
        {
            TextDiagnosticFormatter formatter = new TextDiagnosticFormatter();
            StringBuilder builder = new StringBuilder();

            foreach (Diagnostic diagnostic in bag.Items)
            {
                builder.AppendLine(formatter.Format(diagnostic));
            }

            builder.Append(formatter.FormatSummary(bag));
            return builder.ToString();
        }
    }
}
