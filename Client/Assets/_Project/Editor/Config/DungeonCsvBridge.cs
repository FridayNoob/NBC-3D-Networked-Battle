// 这个文件里有：
//   DungeonCsvBridge（静态类）：DungeonConfig.asset 与 Configs/Design/Dungeon.csv 的双向桥，含拼文本、解析、校验、写盘、调 ConfigKit
//   CsvColumn（数据类）：CSV 一列的元数据，字段名、中文名、类型、规则，供窗口画表头用

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NBC.Game.Config;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NBC.EditorTools
{
    // 主要功能：CSV 一列的说明，用来在窗口里显示中文表头与填写规则。
    // 主要数据成员：Name 是字段名（对应 C# 字段）、Label 是中文名（带单位）、Type 是类型、Rule 是取值范围。
    // 主要外部方法：无，纯数据。
    // 谁在用它：DungeonConfigWindow 画表头；DungeonCsvBridge.ReadColumns 负责造。
    public sealed class CsvColumn
    {
        // 字段名，和 CSV 第一行、C# 字段一致
        public string Name;

        // 中文名，带单位，例如「出生点半径（毫米）」
        public string Label;

        // 类型，例如 int、ref:Monster[]
        public string Type;

        // 规则，例如 range(2,4)，空表示没写
        public string Rule;
    }

    // 主要功能：把 Dungeon 配置资产的行导出成 CSV，或把 CSV 读回来覆盖资产，并在导出前后做校验与重新生成。
    // 主要数据成员：CsvRelativePath（仓库根下的 CSV）、ConfigToolRelativePath（ConfigKit 可执行文件）、表头 4 行常量。
    // 主要外部方法：ResolveCsvPath、BuildCsvText、IsDirty、Validate、Export、Import、RunConfigKit、SplitLine、ReadColumns。
    // 谁在用它：DungeonConfigWindow 的按钮与 DungeonCsvBridgeTests。
    public static class DungeonCsvBridge
    {
        // CSV 源相对仓库根的路径，唯一真值
        public const string CsvRelativePath = "Configs/Design/Dungeon.csv";

        // ConfigKit 可执行文件相对仓库根
        private const string ConfigToolRelativePath = "Tools/ConfigKit/src/ConfigKit.Cli/bin/Debug/net8.0/NBC.ConfigKit.Cli.exe";

        // 源表目录与生成目录，均相对仓库根，和 ConfigKit 默认值一致
        private const string SourceRelativePath = "Configs/Design";

        // 生成目录，供 ConfigKit --out 使用
        private const string GeneratedRelativePath = "Client/Assets/_Project/Game/Config/Generated";

        // 表头行数：字段名、中文名、类型、规则
        private const int HeaderLineCount = 4;

        // 功能：从 Assets 目录上溯找到仓库根下的 Dungeon.csv。
        // 入参：无。
        // 返回含义：绝对路径，找不到返回 null。
        // 步骤：以 dataPath 的上一级为起点逐级上溯最多 8 层。
        // 上游/下游：Export、Import、IsDirty 调用。
        public static string ResolveCsvPath()
        {
            string current = Path.GetDirectoryName(Application.dataPath);

            for (int i = 0; i < 8 && !string.IsNullOrEmpty(current); i++)
            {
                string candidate = Path.Combine(current, CsvRelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = Path.GetDirectoryName(current);
            }

            return null;
        }

        // 功能：把资产的行拼成整份 CSV，表头原样用调用方给的 4 行。
        // 入参：asset 是配置资产；headerBlock 是磁盘表头原文。
        // 返回含义：整份文本，\n 换行、结尾带换行。
        // 步骤：先放表头，再按列顺序逐行写；名字含逗号/引号/换行时加引号，怪物多只时加引号。
        // 上游/下游：Export 与 IsDirty 调用；内部用 AppendField、FormatMonsters。
        public static string BuildCsvText(DungeonConfig asset, string headerBlock)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(headerBlock.Replace("\r\n", "\n").TrimEnd('\n')).Append('\n');

            if (asset != null && asset.rows != null)
            {
                for (int i = 0; i < asset.rows.Count; i++)
                {
                    Config_Dungeon row = asset.rows[i];

                    if (row == null)
                    {
                        continue;
                    }

                    AppendField(builder, row.id.ToString(CultureInfo.InvariantCulture), false);
                    builder.Append(',');
                    AppendField(builder, row.name ?? string.Empty, true);
                    builder.Append(',');
                    AppendField(builder, row.maxPlayers.ToString(CultureInfo.InvariantCulture), false);
                    builder.Append(',');
                    AppendField(builder, row.spawnRadiusMm.ToString(CultureInfo.InvariantCulture), false);
                    builder.Append(',');
                    builder.Append(FormatMonsters(row.monsters)).Append(',');
                    AppendField(builder, row.bossId.ToString(CultureInfo.InvariantCulture), false);
                    builder.Append('\n');
                }
            }

            return builder.ToString();
        }

        // 功能：判断资产与磁盘是否一致。
        // 入参：asset 配置资产；detail 输出差异说明。
        // 返回含义：true 表示有未导出改动或文件读不到。
        // 步骤：读磁盘 → 取前 4 行表头 → 重拼 → 逐字比较。
        // 上游/下游：窗口 OnGUI 调用；内部用 BuildCsvText。
        public static bool IsDirty(DungeonConfig asset, out string detail)
        {
            detail = string.Empty;

            if (asset == null)
            {
                detail = "没有加载到 DungeonConfig 资产";
                return false;
            }

            string path = ResolveCsvPath();

            if (path == null)
            {
                detail = "找不到 " + CsvRelativePath;
                return true;
            }

            string disk = Normalize(File.ReadAllText(path, Encoding.UTF8));

            if (!string.Equals(disk, BuildCsvText(asset, TakeHeaderBlock(disk)), StringComparison.Ordinal))
            {
                detail = "SO 有未导出改动：磁盘 " + (CountDataLines(disk)) + " 行，SO " + asset.rows.Count + " 行";
                return true;
            }

            return false;
        }

        // 功能：校验每一行，把问题按行号收进 errors。
        // 入参：asset 配置资产；errors 收集列表。
        // 返回含义：true 表示全部通过。
        // 步骤：查主键/名字/人数/半径/怪物引用/BOSS 引用，引用用 MonsterConfig 的主键集合。
        // 上游/下游：Export、Import、窗口调用；内部用 LoadMonsterIds。
        public static bool Validate(DungeonConfig asset, List<string> errors)
        {
            if (errors == null)
            {
                return false;
            }

            if (asset == null)
            {
                errors.Add("没有加载到 DungeonConfig 资产");
                return false;
            }

            HashSet<int> monsterIds = LoadMonsterIds();

            if (monsterIds == null)
            {
                errors.Add("读不到 MonsterConfig 资产，无法检查怪物引用");
                return false;
            }

            HashSet<int> seen = new HashSet<int>();

            for (int i = 0; i < asset.rows.Count; i++)
            {
                Config_Dungeon row = asset.rows[i];

                if (row == null)
                {
                    errors.Add("第 " + (i + 1) + " 行是空的");
                    continue;
                }

                string prefix = "第 " + (i + 1) + " 行（id=" + row.id + "）：";

                if (row.id <= 0)
                {
                    errors.Add(prefix + "主键必须大于 0");
                }
                else if (!seen.Add(row.id))
                {
                    errors.Add(prefix + "主键与前面重复");
                }

                if (string.IsNullOrWhiteSpace(row.name))
                {
                    errors.Add(prefix + "名称不能为空");
                }
                else if (row.name.Length > 16)
                {
                    errors.Add(prefix + "名称最多 16 个字");
                }

                if (row.maxPlayers < 2 || row.maxPlayers > 4)
                {
                    errors.Add(prefix + "最大人数只能是 2~4");
                }

                if (row.spawnRadiusMm < 500 || row.spawnRadiusMm > 20000)
                {
                    errors.Add(prefix + "出生半径只能是 500~20000 毫米");
                }

                if (row.monsters == null || row.monsters.Length == 0)
                {
                    errors.Add(prefix + "至少要填一只普通怪");
                }
                else
                {
                    for (int m = 0; m < row.monsters.Length; m++)
                    {
                        if (!monsterIds.Contains(row.monsters[m]))
                        {
                            errors.Add(prefix + "怪物 " + row.monsters[m] + " 不在 Monster 表里");
                        }
                    }
                }

                if (row.bossId != 0 && !monsterIds.Contains(row.bossId))
                {
                    errors.Add(prefix + "BOSS " + row.bossId + " 不在 Monster 表里");
                }
            }

            return errors.Count == 0;
        }

        // 功能：导出 CSV 并让 ConfigKit 重新生成，失败时回滚磁盘。
        // 入参：asset 配置资产；message 输出结果。
        // 返回含义：true 表示导出与生成都成功。
        // 步骤：校验 → 读表头 → 拼文本 → 备份 .bak → 临时文件替换 → 跑 ConfigKit（失败回滚）→ 保存资产。
        // 上游/下游：窗口导出按钮调用；内部用 Validate、BuildCsvText、RunConfigKit。
        public static bool Export(DungeonConfig asset, out string message)
        {
            List<string> errors = new List<string>();

            if (!Validate(asset, errors))
            {
                message = "校验没通过，磁盘没有改动：\n" + string.Join("\n", errors);
                return false;
            }

            string path = ResolveCsvPath();

            if (path == null)
            {
                message = "找不到 " + CsvRelativePath;
                return false;
            }

            string disk = Normalize(File.ReadAllText(path, Encoding.UTF8));
            string text = BuildCsvText(asset, TakeHeaderBlock(disk));
            string backup = path + ".bak";

            File.Copy(path, backup, true);
            File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false));
            File.Copy(path + ".tmp", path, true);
            File.Delete(path + ".tmp");

            if (!RunConfigKit(out string toolMessage))
            {
                File.Copy(backup, path, true);
                message = "导出已回滚（ConfigKit 没跑成功）：\n" + toolMessage;
                return false;
            }

            File.Delete(backup);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            message = "已导出 " + asset.rows.Count + " 行到 " + CsvRelativePath + "，并已重新生成。\n请重启服务端让新配置生效。";
            return true;
        }

        // 功能：把 CSV 读回来覆盖资产的 rows。
        // 入参：asset 配置资产；message 输出结果。
        // 返回含义：true 表示解析、校验、覆盖都成功。
        // 步骤：读全文 → 按表头列名解析 → 临时列表 → 校验 → 通过才覆盖并保存。
        // 上游/下游：窗口导入按钮调用；内部用 SplitLine、Validate。
        public static bool Import(DungeonConfig asset, out string message)
        {
            if (asset == null)
            {
                message = "没有加载到 DungeonConfig 资产";
                return false;
            }

            string path = ResolveCsvPath();

            if (path == null)
            {
                message = "找不到 " + CsvRelativePath;
                return false;
            }

            string[] lines = Normalize(File.ReadAllText(path, Encoding.UTF8)).Split('\n');
            List<Config_Dungeon> parsed = new List<Config_Dungeon>();
            string[] header = SplitLine(lines[0]);

            try
            {
                for (int i = HeaderLineCount; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                    {
                        continue;
                    }

                    string[] cells = SplitLine(lines[i]);
                    parsed.Add(new Config_Dungeon
                    {
                        id = ParseInt(Cell(header, cells, "id")),
                        name = Cell(header, cells, "name"),
                        maxPlayers = ParseInt(Cell(header, cells, "maxPlayers")),
                        spawnRadiusMm = ParseInt(Cell(header, cells, "spawnRadiusMm")),
                        monsters = ParseIntArray(Cell(header, cells, "monsters")),
                        bossId = ParseInt(Cell(header, cells, "bossId")),
                    });
                }
            }
            catch (Exception exception)
            {
                message = "CSV 解析失败（第 " + (parsed.Count + HeaderLineCount + 1) + " 行附近）：" + exception.Message;
                return false;
            }

            List<Config_Dungeon> backupRows = new List<Config_Dungeon>(asset.rows);
            asset.rows.Clear();
            asset.rows.AddRange(parsed);

            List<string> errors = new List<string>();

            if (!Validate(asset, errors))
            {
                asset.rows.Clear();
                asset.rows.AddRange(backupRows);
                message = "CSV 校验没通过，资产没有改动：\n" + string.Join("\n", errors);
                return false;
            }

            asset.RebuildIndex();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            message = "已从 " + CsvRelativePath + " 导入 " + parsed.Count + " 行，资产已保存。";
            return true;
        }

        // 功能：调用 ConfigKit.Cli 重新生成 C#、TSV、JSON。
        // 入参：message 输出工具输出或错误原因。
        // 返回含义：true 表示退出码为 0。
        // 步骤：由 CSV 路径上溯仓库根 → 拼 exe 路径 → 起进程 → 最多等 120 秒 → 看退出码。
        // 上游/下游：Export 调用。
        public static bool RunConfigKit(out string message)
        {
            string csvPath = ResolveCsvPath();

            if (csvPath == null)
            {
                message = "找不到 " + CsvRelativePath;
                return false;
            }

            string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(csvPath)));
            string tool = Path.Combine(root, ConfigToolRelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(tool))
            {
                message = "找不到 ConfigKit 可执行文件，请先编译 Tools/ConfigKit：" + tool;
                return false;
            }

            ProcessStartInfo info = new ProcessStartInfo(tool)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            info.ArgumentList.Add("--source");
            info.ArgumentList.Add(SourceRelativePath);
            info.ArgumentList.Add("--out");
            info.ArgumentList.Add(GeneratedRelativePath);

            using (Process process = Process.Start(info))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();

                if (!process.WaitForExit(120000))
                {
                    TryKill(process);
                    message = "ConfigKit 超过 120 秒没有结束";
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    message = "ConfigKit 退出码 " + process.ExitCode + "\n" + output + error;
                    return false;
                }

                Debug.Log("[DungeonCsvBridge] ConfigKit 输出：\n" + output);
                message = "ConfigKit 已重新生成";
                return true;
            }
        }

        // 功能：按 CSV 规则切一行。
        // 入参：line 一行原始文本。
        // 返回含义：单元格数组。
        // 步骤：逐字符扫描，引号内两个引号落一个字面引号，逗号收格。
        // 上游/下游：Import 与测试调用。
        public static string[] SplitLine(string line)
        {
            List<string> cells = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];

                if (inQuotes)
                {
                    if (ch != '"')
                    {
                        current.Append(ch);
                    }
                    else if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inQuotes = true;
                }
                else if (ch == ',')
                {
                    cells.Add(current.ToString());
                    current.Length = 0;
                }
                else
                {
                    current.Append(ch);
                }
            }

            if (inQuotes)
            {
                throw new FormatException("引号没有闭合");
            }

            cells.Add(current.ToString());
            return cells.ToArray();
        }

        // 功能：读磁盘 CSV 的前 4 行表头，拼成列说明。
        // 入参：error 输出错误原因。
        // 返回含义：列说明列表，读不到返回空列表。
        // 步骤：读文件 → 逐行切分 → 按列下标把字段名/中文名/类型/规则配起来。
        // 上游/下游：DungeonConfigWindow 画表头时调用；内部用 SplitLine。
        public static List<CsvColumn> ReadColumns(out string error)
        {
            error = string.Empty;
            List<CsvColumn> columns = new List<CsvColumn>();
            string path = ResolveCsvPath();

            if (path == null)
            {
                error = "找不到 " + CsvRelativePath;
                return columns;
            }

            string[] lines = Normalize(File.ReadAllText(path, Encoding.UTF8)).Split('\n');

            if (lines.Length < HeaderLineCount)
            {
                error = CsvRelativePath + " 的表头不足 " + HeaderLineCount + " 行";
                return columns;
            }

            string[] names = SplitLine(lines[0]);
            string[] labels = SplitLine(lines[1]);
            string[] types = SplitLine(lines[2]);
            string[] rules = SplitLine(lines[3]);

            for (int i = 0; i < names.Length; i++)
            {
                columns.Add(new CsvColumn
                {
                    Name = names[i],
                    Label = At(labels, i),
                    Type = At(types, i),
                    Rule = At(rules, i),
                });
            }

            return columns;
        }

        // 功能：取一行表头的第 index 格，防止缺列时越界。
        // 入参：cells 是切分后的表头行；index 是列下标。
        // 返回含义：该格文本，取不到给空串。
        // 步骤：比较下标与长度后返回。
        // 上游/下游：ReadColumns 调用。
        private static string At(string[] cells, int index)
        {
            return index < cells.Length ? cells[index] : string.Empty;
        }

        // 功能：把怪物数组写成单元格，多只时加引号。
        // 入参：monsters 怪物编号数组。
        // 返回含义：可直接拼进一行的文本。
        // 步骤：空给空串、单只给数字、多只拼 a,b,c 再加引号。
        // 上游/下游：BuildCsvText 调用。
        private static string FormatMonsters(int[] monsters)
        {
            if (monsters == null || monsters.Length == 0)
            {
                return string.Empty;
            }

            if (monsters.Length == 1)
            {
                return monsters[0].ToString(CultureInfo.InvariantCulture);
            }

            StringBuilder builder = new StringBuilder("\"");

            for (int i = 0; i < monsters.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(monsters[i].ToString(CultureInfo.InvariantCulture));
            }

            return builder.Append('"').ToString();
        }

        // 功能：按需加引号后写入一个单元格。
        // 入参：builder 目标；value 内容；quoteWhenNeeded 为 true 时含特殊字符才加引号。
        // 返回含义：无。
        // 步骤：需要时把内部引号翻倍并包住。
        // 上游/下游：BuildCsvText 调用。
        private static void AppendField(StringBuilder builder, string value, bool quoteWhenNeeded)
        {
            bool needQuote = quoteWhenNeeded && value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0;

            if (!needQuote)
            {
                builder.Append(value);
                return;
            }

            builder.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
        }

        // 功能：取整份文本前 4 行表头。
        // 入参：text 整份 CSV。
        // 返回含义：4 行表头，不含结尾换行。
        // 步骤：切行后取前 4 行，不足就原样返回。
        // 上游/下游：Export 与 IsDirty 调用。
        private static string TakeHeaderBlock(string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            return lines.Length <= HeaderLineCount ? text.TrimEnd('\n') : string.Join("\n", lines, 0, HeaderLineCount);
        }

        // 功能：把换行统一成 \n。
        // 入参：text 原始文本。
        // 返回含义：只含 \n 的文本。
        // 步骤：先换 \r\n 再换单个 \r。
        // 上游/下游：IsDirty、Export、Import 调用。
        private static string Normalize(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        // 功能：数数据行数，仅用于提示。
        // 入参：text 整份 CSV。
        // 返回含义：非空数据行数。
        // 步骤：从第 5 行数到末尾。
        // 上游/下游：IsDirty 调用。
        private static int CountDataLines(string text)
        {
            string[] lines = text.Split('\n');
            int count = 0;

            for (int i = HeaderLineCount; i < lines.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    count++;
                }
            }

            return count;
        }

        // 功能：读 MonsterConfig 收集合法怪物编号。
        // 入参：无。
        // 返回含义：编号集合，资产缺失返回 null。
        // 步骤：按导入器资产目录拼路径加载后遍历。
        // 上游/下游：Validate 调用。
        private static HashSet<int> LoadMonsterIds()
        {
            MonsterConfig config = AssetDatabase.LoadAssetAtPath<MonsterConfig>(ConfigImporter.AssetDirectory + "/MonsterConfig.asset");

            if (config == null)
            {
                return null;
            }

            HashSet<int> ids = new HashSet<int>();

            for (int i = 0; i < config.rows.Count; i++)
            {
                ids.Add(config.rows[i].id);
            }

            return ids;
        }

        // 功能：按列名取单元格。
        // 入参：header 字段名行；cells 数据行；name 字段名。
        // 返回含义：单元格文本，缺列给空串。
        // 步骤：在 header 里找同名下标。
        // 上游/下游：Import 调用。
        private static string Cell(string[] header, string[] cells, string name)
        {
            for (int i = 0; i < header.Length; i++)
            {
                if (string.Equals(header[i], name, StringComparison.Ordinal))
                {
                    return i < cells.Length ? cells[i] : string.Empty;
                }
            }

            return string.Empty;
        }

        // 功能：解析整数，失败给 0。
        // 入参：text 文本。
        // 返回含义：解析结果。
        // 步骤：用固定区域格式解析。
        // 上游/下游：Import 调用。
        private static int ParseInt(string text)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
        }

        // 功能：解析逗号分隔整数数组。
        // 入参：text 文本。
        // 返回含义：数组，空文本给空数组。
        // 步骤：切逗号逐个解析。
        // 上游/下游：Import 调用。
        private static int[] ParseIntArray(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<int>();
            }

            string[] parts = text.Split(',');
            int[] result = new int[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                result[i] = ParseInt(parts[i].Trim());
            }

            return result;
        }

        // 功能：尽力杀掉超时进程。
        // 入参：process 进程。
        // 返回含义：无。
        // 步骤：调用 Kill，异常吞掉。
        // 上游/下游：RunConfigKit 调用。
        private static void TryKill(Process process)
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // 进程已退出时杀不掉，不算失败原因
            }
        }
    }
}
