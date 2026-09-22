// ============================================================================
//  ConfigKit · 命令行宿主
//  用法：
//    dotnet run --project Tools\ConfigKit\src\ConfigKit.Cli -- \
//        --source <表目录> --out <生成目录> [--ns <命名空间>] [--check]
//
//  退出码约定（**这三个码是有意义的，不是随手定的**）：
//      0  成功
//      2  **表里有错**（校验失败）—— CI 里可以直接用这个码判断"策划填错了"
//      1  用法错误 / 读不了（环境问题，不是表的问题）
//
//  ⚠️ 这个程序**只做三件事**：解析参数、写文件、给退出码。
//     "读表 / 解析表头 / 校验 / 生成"全在 Core 里 —— 所以那些逻辑
//     不依赖这个进程也能被自测跑（见 tests\ConfigKit.SelfTest）。
// ============================================================================

using System.Text;
using NBC.ConfigKit;
using NBC.ConfigKit.Sources;

namespace NBC.ConfigKit.Cli
{
    /// <summary>命令行入口。</summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (IOException)
            {
                // 输出被重定向到管道时可能设不了编码，不该因此失败
            }

            Options options;

            if (!Options.TryParse(args, out options, out string usageError))
            {
                Console.Error.WriteLine("用法错误：" + usageError);
                Console.Error.WriteLine();
                Console.Error.WriteLine(Options.Usage);
                return 1;
            }

            if (options.ShowHelp)
            {
                Console.WriteLine(Options.Usage);
                return 0;
            }

            try
            {
                return Run(options);
            }
            catch (Exception exception)
            {
                // 环境问题（目录权限、路径不存在…）：**用 1**，和"表填错了（2）"分开
                Console.Error.WriteLine("执行失败：" + exception.Message);
                return 1;
            }
        }

        private static int Run(Options options)
        {
            ConfigPolicy policy = ConfigPolicy.CreateDefault();
            ExportPipeline pipeline = new ExportPipeline(
                policy,
                CreateEmitter(options),
                new EmitOptions { Namespace = options.Namespace });

            ITableSource source = CreateSource(options);

            ExportReport report = pipeline.Run(source);

            Console.WriteLine(report.Render(new TextDiagnosticFormatter()));

            if (!report.Succeeded)
            {
                // ⚠️ 有错就不写任何文件（流水线保证 Files 是空的），直接给 2
                return 2;
            }

            if (options.CheckOnly)
            {
                Console.WriteLine("（--check：只校验，不落盘）");
                return 0;
            }

            Directory.CreateDirectory(options.OutputDirectory);

            for (int i = 0; i < report.Files.Count; i++)
            {
                EmittedFile file = report.Files[i];
                string path = Path.Combine(options.OutputDirectory, file.RelativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? options.OutputDirectory);

                // ⚠️ 显式 UTF-8 **无 BOM + LF**（和仓库的 W4 规则一致）：
                //    生成物要入库，带 BOM 或 CRLF 会让每次导出的 diff 都是整文件
                File.WriteAllText(path, NormalizeNewLines(file.Content), new UTF8Encoding(false));
            }

            Console.WriteLine();
            Console.WriteLine($"已写出 {report.Files.Count} 个文件到 {options.OutputDirectory}");
            return 0;
        }

        /// <summary>
        /// 按 format 参数选来源；auto（默认）看目录里有什么。
        /// <para>
        /// ⚠️ 这就是"接缝①"在命令行上的样子：**加一种来源只改这一处**，
        /// 读表/校验/生成一行都不用动。
        /// </para>
        /// </summary>
        private static ITableSource CreateSource(Options options)
        {
            string format = options.Format ?? "auto";

            if (format == "csv")
            {
                return new DelimitedTableSource(options.SourceDirectory, DelimiterKind.Comma);
            }

            if (format == "tsv")
            {
                return new DelimitedTableSource(options.SourceDirectory, DelimiterKind.Tab);
            }

            if (format == "xlsx")
            {
                return new XlsxTableSource(options.SourceDirectory);
            }

            // auto：目录里有 .xlsx 就用 xlsx，否则按分隔符文本
            if (Directory.Exists(options.SourceDirectory) &&
                Directory.GetFiles(options.SourceDirectory, "*.xlsx").Length > 0)
            {
                return new XlsxTableSource(options.SourceDirectory);
            }

            return new DelimitedTableSource(options.SourceDirectory, DelimiterKind.Comma);
        }

        /// <summary>
        /// 按 <c>--emitter</c> 选产物。
        /// <para>
        /// 默认 <c>all</c>：**C# 类型 + TSV + JSON**。
        /// </para>
        /// <list type="bullet">
        /// <item><description><b>C# 类型</b>：让工程编译得过（含 <c>LoadFromTsv</c>）</description></item>
        /// <item><description><b>TSV</b>：**Unity 导入器读它**建 `.asset`（原因见 TsvConfigEmitter 文件头）</description></item>
        /// <item><description><b>JSON</b>：给外部工具 / 别的语言 / 人看，也可扩展嵌套结构</description></item>
        /// </list>
        /// </summary>
        private static ITableEmitter CreateEmitter(Options options)
        {
            switch (options.Emitter ?? "all")
            {
                case "csharp":
                    return new CSharpConfigEmitter();

                case "tsv":
                    return new TsvConfigEmitter();

                case "json":
                    return new JsonConfigEmitter();

                default:
                    return new CompositeEmitter(
                        new CSharpConfigEmitter(), new TsvConfigEmitter(), new JsonConfigEmitter());
            }
        }

        /// <summary>把多个发射器的产物合起来（宿主侧的编排，不属于 Core）。</summary>
        private sealed class CompositeEmitter : ITableEmitter
        {
            private readonly ITableEmitter[] m_emitters;

            public CompositeEmitter(params ITableEmitter[] emitters)
            {
                m_emitters = emitters;
            }

            public string Name
            {
                get { return "composite"; }
            }

            public IReadOnlyList<EmittedFile> Emit(ConfigTable table, EmitOptions options)
            {
                List<EmittedFile> files = new List<EmittedFile>();

                for (int i = 0; i < m_emitters.Length; i++)
                {
                    IReadOnlyList<EmittedFile> produced = m_emitters[i].Emit(table, options);

                    for (int f = 0; f < produced.Count; f++)
                    {
                        files.Add(produced[f]);
                    }
                }

                return files;
            }
        }

        /// <summary>统一换行成 LF（跨平台 + diff 稳定）。</summary>
        private static string NormalizeNewLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// <summary>命令行参数。</summary>
        private sealed class Options
        {
            public const string Usage = @"ConfigKit —— 配置表工具（.xlsx / .csv / .tsv → 强类型 C#）

用法：
  ConfigKit.Cli --source <表目录> [--out <生成目录>] [--format <auto|xlsx|csv|tsv>] [--ns <命名空间>] [--check]

参数：
  --source <目录>     必填。表所在目录
                        · .xlsx：**一个 sheet = 一张表**（表名 = sheet 名）
                        · .csv/.tsv：**文件名 = 表名**
  --format <格式>     auto（默认，看目录里有什么）/ xlsx / csv / tsv
  --emitter <产物>    all（默认：C# 类型 + TSV + JSON）/ csharp / tsv / json
                      · C# 类型：Config_<表>.cs + <表>Config.cs（含 LoadFromTsv）
                      · TSV 数据：<表>.tsv —— **Unity 侧导入器读它建 .asset**
                      · JSON    ：<表>.json —— 给外部工具 / 别的语言 / 人看（嵌套结构也能装）
                      三种边界各用什么格式，见 Tools\ConfigKit\README.md §九
  --out <目录>        生成目录。默认 Assets/_Project/Game/Config/Generated
  --ns <命名空间>     生成代码的命名空间。默认 NBC.Game.Config
  --check             只校验，不写文件
  --help              显示本说明

退出码：
  0 成功   2 表里有错（未产出任何文件）   1 用法错误 / 读不了

说明：
  · 约定与校验规则见 Docs\17-配置表规范.md
  · ⚠️ `.csv` 里**含逗号的格子必须用双引号包起来**（如 ""range(1,10)""、""2001,2002""）——
    否则会被逗号拆成两格。`.xlsx` 没有这个问题
  · 需要一张格式正确的示例表：ConfigKit.XlsxProbe.exe --emit <目录>";

            public string SourceDirectory = string.Empty;
            public string OutputDirectory = "Assets/_Project/Game/Config/Generated";
            public string Namespace = "NBC.Game.Config";
            public string Format;
            public string Emitter;
            public bool CheckOnly;
            public bool ShowHelp;

            public static bool TryParse(string[] args, out Options options, out string error)
            {
                options = new Options();
                error = null;

                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];

                    switch (arg)
                    {
                        case "--help":
                        case "-h":
                            options.ShowHelp = true;
                            return true;

                        case "--check":
                            options.CheckOnly = true;
                            continue;

                        case "--source":
                            if (!TryNext(args, ref i, out options.SourceDirectory))
                            {
                                error = "--source 后面要跟一个目录";
                                return false;
                            }

                            continue;

                        case "--out":
                            if (!TryNext(args, ref i, out options.OutputDirectory))
                            {
                                error = "--out 后面要跟一个目录";
                                return false;
                            }

                            continue;

                        case "--ns":
                            if (!TryNext(args, ref i, out options.Namespace))
                            {
                                error = "--ns 后面要跟一个命名空间";
                                return false;
                            }

                            continue;

                        case "--format":
                            if (!TryNext(args, ref i, out options.Format))
                            {
                                error = "--format 后面要跟 auto / xlsx / csv / tsv";
                                return false;
                            }

                            if (options.Format != "auto" && options.Format != "xlsx" &&
                                options.Format != "csv" && options.Format != "tsv")
                            {
                                error = "不认识的格式 " + options.Format + "（只支持 auto / xlsx / csv / tsv）";
                                return false;
                            }

                            continue;

                        case "--emitter":
                            if (!TryNext(args, ref i, out options.Emitter))
                            {
                                error = "--emitter 后面要跟 all / csharp / tsv / json";
                                return false;
                            }

                            if (options.Emitter != "all" && options.Emitter != "csharp" &&
                                options.Emitter != "tsv" && options.Emitter != "json")
                            {
                                error = "不认识的产物 " + options.Emitter +
                                        "（只支持 all / csharp / tsv / json）";
                                return false;
                            }

                            continue;

                        default:
                            error = "不认识的参数 " + arg;
                            return false;
                    }
                }

                if (options.SourceDirectory.Length == 0)
                {
                    error = "缺少 --source";
                    return false;
                }

                return true;
            }

            private static bool TryNext(string[] args, ref int index, out string value)
            {
                value = null;

                if (index + 1 >= args.Length)
                {
                    return false;
                }

                index++;
                value = args[index];
                return true;
            }
        }
    }
}
