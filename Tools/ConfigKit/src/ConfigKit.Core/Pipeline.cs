// ============================================================================
//  ConfigKit · 导出流水线（把接缝串起来）
//  对应：Docs\17-配置表规范.md §八（报错契约）§九（生成物）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 这一层**不碰文件系统**（除了从 ITableSource 读输入）
//  ---------------------------------------------------------------------------
//  它产出的是 `EmittedFile`（**相对路径 + 内容**），**落盘由宿主决定**。
//  两个好处：
//    ① **可测**：自测里能把生成结果当字符串断言，不写任何文件、不清理临时目录
//    ② **可复用**：同一个流水线将来可以"生成到内存直接给 Unity Editor 用"，
//       也可以"生成到临时目录做 CI 校验"，宿主不同，流水线不变
//
//  ---------------------------------------------------------------------------
//  一条写死的语义：**有错就不产出任何东西**
//  ---------------------------------------------------------------------------
//  半成品比没有更危险：游戏能跑、数据是错的，而且要等到运行时才发现。
//  所以 `HasErrors` 时 `Files` 一定是空的。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NBC.ConfigKit
{
    /// <summary>一张表的导出结果。</summary>
    public sealed class TableReport
    {
        /// <summary>构造。</summary>
        /// <param name="tableName">表名。</param>
        /// <param name="rowCount">数据行数。</param>
        /// <param name="fileName">来源文件。</param>
        public TableReport(string tableName, int rowCount, string fileName)
        {
            TableName = tableName;
            RowCount = rowCount;
            FileName = fileName;
        }

        /// <summary>表名。</summary>
        public string TableName { get; }

        /// <summary>数据行数。</summary>
        public int RowCount { get; }

        /// <summary>来源文件。</summary>
        public string FileName { get; }
    }

    /// <summary>一次导出的完整报告。</summary>
    public sealed class ExportReport
    {
        private readonly List<TableReport> m_tables = new List<TableReport>();
        private readonly List<EmittedFile> m_files = new List<EmittedFile>();

        /// <summary>诊断。</summary>
        public DiagnosticBag Diagnostics { get; } = new DiagnosticBag();

        /// <summary>逐表统计。</summary>
        public IReadOnlyList<TableReport> Tables
        {
            get { return m_tables; }
        }

        /// <summary>生成出来的文件（**有错时一定是空的**）。</summary>
        public IReadOnlyList<EmittedFile> Files
        {
            get { return m_files; }
        }

        /// <summary>这次导出成不成功。</summary>
        public bool Succeeded
        {
            get { return !Diagnostics.HasErrors; }
        }

        /// <summary>加一张表的统计。</summary>
        /// <param name="report">统计。</param>
        public void AddTable(TableReport report)
        {
            m_tables.Add(report);
        }

        /// <summary>加一个生成出来的文件。</summary>
        /// <param name="file">文件。</param>
        public void AddFile(EmittedFile file)
        {
            m_files.Add(file);
        }

        /// <summary>人话报告（命令行直接打印）。</summary>
        /// <param name="formatter">诊断排版器。</param>
        /// <returns>多行文本。</returns>
        public string Render(IDiagnosticFormatter formatter)
        {
            IDiagnosticFormatter actual = formatter ?? new TextDiagnosticFormatter();
            StringBuilder builder = new StringBuilder();

            builder.AppendLine("=== 配置表导出报告 ===");
            builder.Append("来源：").AppendLine(SourceDescription ?? "<未知>");
            builder.AppendLine();

            builder.AppendLine("表名                 行数   文件");
            for (int i = 0; i < m_tables.Count; i++)
            {
                TableReport table = m_tables[i];
                builder.Append(table.TableName.PadRight(20))
                       .Append(table.RowCount.ToString(CultureInfo.InvariantCulture).PadLeft(5))
                       .Append("   ")
                       .AppendLine(table.FileName);
            }

            builder.AppendLine();
            builder.Append("生成文件：").Append(m_files.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" 个");
            for (int i = 0; i < m_files.Count; i++)
            {
                builder.Append("  ").AppendLine(m_files[i].RelativePath);
            }

            if (Diagnostics.Items.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("--- 问题 ---");
                Diagnostics.SortByLocation();

                for (int i = 0; i < Diagnostics.Items.Count; i++)
                {
                    builder.AppendLine(actual.Format(Diagnostics.Items[i]));
                }
            }

            builder.AppendLine();
            builder.AppendLine(actual.FormatSummary(Diagnostics));
            builder.Append(Succeeded ? "结果：成功" : "结果：**失败（未产出任何文件）**");
            return builder.ToString();
        }

        /// <summary>来源描述（打报告用）。</summary>
        public string SourceDescription { get; set; }
    }

    /// <summary>
    /// 导出流水线：**读 → 解析 → 校验 → 生成**。
    /// <para>四个接缝都从这里穿过去，所以它自己也很好测：给个内存来源、给个发射器，就能断言结果。</para>
    /// </summary>
    public sealed class ExportPipeline
    {
        private readonly ConfigPolicy m_policy;
        private readonly ITableEmitter m_emitter;
        private readonly EmitOptions m_options;

        /// <summary>构造。</summary>
        /// <param name="policy">项目策略。</param>
        /// <param name="emitter">产物发射器。</param>
        /// <param name="options">生成选项。</param>
        public ExportPipeline(ConfigPolicy policy, ITableEmitter emitter, EmitOptions options = null)
        {
            m_policy = policy ?? throw new ArgumentNullException(nameof(policy));
            m_emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
            m_options = options ?? new EmitOptions();
        }

        /// <summary>跑一次。</summary>
        /// <param name="source">表来源。</param>
        /// <returns>报告。</returns>
        public ExportReport Run(ITableSource source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            ExportReport report = new ExportReport { SourceDescription = source.Description };
            DiagnosticBag diagnostics = report.Diagnostics;

            // ① 读（读不动一个文件不中断整批）
            IReadOnlyList<RawTable> raws = source.ReadAll(diagnostics);

            if (raws.Count == 0)
            {
                diagnostics.Error(DiagnosticCodes.Source,
                    new SourceLocation(source.Description, "<来源>", SourceLocation.NoRow, SourceLocation.NoColumn, null),
                    "一张表都没读到", "至少一张表", "0 张");
                return report;
            }

            // ② 解析表头
            SchemaReader reader = new SchemaReader(m_policy);
            ConfigSet set = new ConfigSet();

            for (int i = 0; i < raws.Count; i++)
            {
                TableSchema schema = reader.Read(raws[i], diagnostics);
                set.Add(new ConfigTable(schema, raws[i]));
            }

            // ③ 校验（三趟）
            ValidationEngine engine = new ValidationEngine(m_policy);
            engine.Validate(set, diagnostics);

            // 统计（即使失败也给，方便人看"读到几张表"）
            for (int i = 0; i < set.Tables.Count; i++)
            {
                ConfigTable table = set.Tables[i];
                report.AddTable(new TableReport(table.Schema.TableName, CountDataRows(table), table.Raw.FileName));
            }

            // ④ 有错就到此为止 —— **绝不产出半成品**
            if (diagnostics.HasErrors)
            {
                return report;
            }

            for (int i = 0; i < set.Tables.Count; i++)
            {
                IReadOnlyList<EmittedFile> files = m_emitter.Emit(set.Tables[i], m_options);

                for (int f = 0; f < files.Count; f++)
                {
                    report.AddFile(files[f]);
                }
            }

            return report;
        }

        /// <summary>数一张表的数据行（跳过表头、空行、以及空行之后被拦下的部分）。</summary>
        private static int CountDataRows(ConfigTable table)
        {
            int count = 0;
            IReadOnlyList<RawRow> rows = table.Raw.Rows;

            for (int i = table.Schema.FirstDataRow - 1; i < rows.Count; i++)
            {
                if (!rows[i].IsBlank)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
