// 这个文件里有：ConfigColumn（一列的表头信息）、ConfigTableBridge（任意配置表的 SO 与 CSV 互转、校验、导出导入）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    // 功能：描述 CSV 的一列（字段名、中文名、类型、校验规则）。
    // 主要数据成员：Name/Label/Type/Rule。
    // 主要外部方法：无，纯数据。
    // 谁在用它：ConfigTableBridge 与 R052 的窗口画表头。
    public struct ConfigColumn
    {
        // 字段名，与 Config_X 的字段同名
        public string Name;

        // 中文名，来自 CSV 第 2 行
        public string Label;

        // 类型，来自 CSV 第 3 行，如 int / int[] / enum:... / ref:Monster[]
        public string Type;

        // 校验规则，来自 CSV 第 4 行，如 key / len(1,16) / range(2,4)
        public string Rule;
    }

    // 功能：把任意 Config_X 的 SO 与对应 CSV 互转，并在导出后重跑 ConfigKit。
    // 主要数据成员：s_fields 字段缓存、s_references 引用表主键缓存。
    // 主要外部方法：ReadColumns、BuildCsvText、Validate、Export、Import、IsDirty。
    // 谁在用它：R052 的配置表窗口；ConfigTableBridgeTests。
    public static class ConfigTableBridge
    {
        // CSV 表头行数（字段名/中文名/类型/规则）
        public const int HeaderLineCount = 4;

        // 读文件失败时的重试次数（Excel 占用时会短暂失败）
        private const int ReadRetryCount = 5;

        // 每次重试前等待的毫秒数
        private const int ReadRetryDelayMs = 80;

        // ConfigKit 可执行文件的仓库相对路径
        private const string ConfigToolRelativePath =
            "Tools/ConfigKit/src/ConfigKit.Cli/bin/Debug/net8.0/NBC.ConfigKit.Cli.exe";

        // 字段缓存：行类型 → 字段名 → FieldInfo
        private static readonly Dictionary<Type, Dictionary<string, FieldInfo>> s_fields =
            new Dictionary<Type, Dictionary<string, FieldInfo>>();

        // 引用表主键缓存：表名 → 主键集合
        private static readonly Dictionary<string, HashSet<int>> s_references =
            new Dictionary<string, HashSet<int>>();

        // 功能：由 Unity 的 Assets 目录上溯到仓库根。
        // 入参：无。
        // 返回含义：仓库根绝对路径。
        // 步骤：Application.dataPath 是 <仓库>/Client/Assets，上两级即仓库根。
        // 上游/下游：Export、Import、RunConfigKit 调用。
        public static string ResolveRepoRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        }

        // 功能：把仓库相对路径转成绝对路径。
        // 入参：relativePath 仓库相对路径。
        // 返回含义：绝对路径。
        // 步骤：仓库根 + 相对路径。
        // 上游/下游：Export、Import 调用。
        public static string ResolveAbsolute(string relativePath)
        {
            return Path.GetFullPath(Path.Combine(ResolveRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        // 功能：读 CSV 文本，自动识别 UTF-8 / UTF-16 / GBK 并容忍别的程序占用。
        // 入参：path 绝对路径；encodingNote 输出识别到的编码名。
        // 返回含义：文件文本（已去掉 BOM）。
        // 步骤：共享读并重试 → 看 BOM → 无 BOM 严格 UTF-8 → 解不开按 GBK。
        // 上游/下游：Import、IsDirty 调用。
        public static string ReadCsvText(string path, out string encodingNote)
        {
            byte[] bytes = ReadAllBytesShared(path);

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encodingNote = "UTF-8";
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encodingNote = "UTF-16LE";
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encodingNote = "UTF-16BE";
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            try
            {
                string utf8 = new UTF8Encoding(false, true).GetString(bytes);
                encodingNote = "UTF-8";
                return utf8.TrimStart('\uFEFF');
            }
            catch (DecoderFallbackException)
            {
                encodingNote = "GBK";
                return Encoding.GetEncoding(936).GetString(bytes);
            }
        }

        // 功能：判断一个 CSV 文件当前是不是 UTF-8 带 BOM。
        // 入参：path 绝对路径。
        // 返回含义：true 表示前三字节是 EF BB BF。
        // 步骤：读前 3 字节比较；文件不存在或读不到都返回 false。
        // 上游/下游：Export 决定写回时要不要带 BOM。
        private static bool HasUtf8Bom(string path)
        {
            try
            {
                byte[] head = new byte[3];
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Read(head, 0, 3) < 3)
                    {
                        return false;
                    }
                }

                return head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // 功能：共享方式读全部字节，遇到占用重试几次。
        // 入参：path 绝对路径。
        // 返回含义：文件字节。
        // 步骤：FileShare.ReadWrite|Delete 打开 → IOException 时睡 80ms 重试，最多 5 次。
        // 上游/下游：ReadCsvText 调用。
        private static byte[] ReadAllBytesShared(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    {
                        byte[] buffer = new byte[stream.Length];
                        int offset = 0;

                        while (offset < buffer.Length)
                        {
                            int read = stream.Read(buffer, offset, buffer.Length - offset);

                            if (read <= 0)
                            {
                                break;
                            }

                            offset += read;
                        }

                        return buffer;
                    }
                }
                catch (IOException) when (attempt < ReadRetryCount)
                {
                    Thread.Sleep(ReadRetryDelayMs);
                }
            }
        }

        // 功能：读某张表的列定义。
        // 入参：entry 表登记；error 输出错误。
        // 返回含义：列列表，读不到返回空列表。
        // 步骤：读文件 → 切 4 行表头 → 按列下标配对。
        // 上游/下游：Validate、窗口、测试调用。
        public static List<ConfigColumn> ReadColumns(ConfigTableEntry entry, out string error)
        {
            error = string.Empty;
            List<ConfigColumn> columns = new List<ConfigColumn>();

            string path = ResolveAbsolute(entry.CsvPath);

            if (!File.Exists(path))
            {
                error = "找不到 " + entry.CsvPath;
                return columns;
            }

            string[] lines;

            try
            {
                lines = Normalize(ReadCsvText(path, out _)).Split('\n');
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                error = "读不到 CSV（可能正被 Excel 占用）：" + exception.Message;
                return columns;
            }

            if (lines.Length < HeaderLineCount)
            {
                error = entry.CsvPath + " 的表头不足 " + HeaderLineCount + " 行";
                return columns;
            }

            string[] names = SplitLine(lines[0]);
            string[] labels = SplitLine(lines[1]);
            string[] types = SplitLine(lines[2]);
            string[] rules = SplitLine(lines[3]);

            for (int i = 0; i < names.Length; i++)
            {
                columns.Add(new ConfigColumn
                {
                    Name = names[i],
                    Label = i < labels.Length ? labels[i] : names[i],
                    Type = i < types.Length ? types[i] : string.Empty,
                    Rule = i < rules.Length ? rules[i] : string.Empty,
                });
            }

            return columns;
        }

        // 功能：把 SO 的行渲染成 CSV 文本（表头原样拼在前面）。
        // 入参：entry 表登记；rows 行列表；headerBlock 磁盘上原来的 4 行表头。
        // 返回含义：完整 CSV 文本。
        // 步骤：逐行逐列取字段值 → FormatCell → 逗号拼接。
        // 上游/下游：Export、IsDirty 调用。
        public static string BuildCsvText(ConfigTableEntry entry, IList rows, string headerBlock)
        {
            List<ConfigColumn> columns = ReadColumns(entry, out _);
            StringBuilder builder = new StringBuilder();
            builder.Append(headerBlock.TrimEnd('\r', '\n')).Append('\n');

            foreach (object row in rows)
            {
                for (int i = 0; i < columns.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(FormatCell(GetFieldValue(entry, row, columns[i].Name), columns[i].Type));
                }

                builder.Append('\n');
            }

            return builder.ToString();
        }

        // 功能：把一个字段值格式化成 CSV 单元格。
        // 入参：value 字段值；columnType 列类型（用来识别可空引用与小数格式）。
        // 返回含义：单元格文本（该加引号时已加）。
        // 步骤：null/空数组给空串；可空引用为 0 写空；int[] 逗号连接（多元素才加引号）；enum 写名字；string 需要时加引号；数字用不变文化。
        // 上游/下游：BuildCsvText 调用。
        public static string FormatCell(object value, string columnType = null)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(columnType) && columnType.EndsWith("?", StringComparison.Ordinal) &&
                value is int intValue && intValue == 0)
            {
                return string.Empty;
            }

            Type type = value.GetType();

            if (type.IsArray)
            {
                Array array = (Array)value;

                if (array.Length == 0)
                {
                    return string.Empty;
                }

                StringBuilder joined = new StringBuilder();

                for (int i = 0; i < array.Length; i++)
                {
                    if (i > 0)
                    {
                        joined.Append(',');
                    }

                    joined.Append(Convert.ToString(array.GetValue(i), CultureInfo.InvariantCulture));
                }

                if (array.Length == 1)
                {
                    return joined.ToString();
                }

                return "\"" + joined + "\"";
            }

            if (type.IsEnum)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            if (value is float || value is double || value is decimal)
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.0###", CultureInfo.InvariantCulture);
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);

            if (text.Length == 0)
            {
                return string.Empty;
            }

            bool needsQuote = text.IndexOf(',') >= 0 || text.IndexOf('，') >= 0 ||
                              text.IndexOf('"') >= 0 || text.IndexOf('\t') >= 0 ||
                              text.IndexOf('\n') >= 0 || text.Trim().Length != text.Length;

            return needsQuote ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        }

        // 功能：把 SO 重拼的 CSV 与磁盘比较，判断有没有未导出的改动。
        // 入参：entry 表登记；asset SO；detail 输出差异说明。
        // 返回含义：true 表示磁盘缺了 SO 的改动（需要导出）。
        // 步骤：读磁盘 → 取表头 → 重拼 → 逐字比较；读不到就当「脏」但不抛。
        // 上游/下游：窗口顶部红字调用。
        public static bool IsDirty(ConfigTableEntry entry, ScriptableObject asset, out string detail)
        {
            detail = string.Empty;
            IList rows = GetRows(asset);

            if (rows == null)
            {
                detail = entry.Name + " 资产上没有 rows 字段";
                return false;
            }

            string disk;

            try
            {
                disk = Normalize(ReadCsvText(ResolveAbsolute(entry.CsvPath), out _));
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                detail = "读不到 CSV（可能正被 Excel 占用）：" + exception.Message;
                return false;
            }

            string text = BuildCsvText(entry, rows, TakeHeaderBlock(disk));

            if (string.Equals(disk, text, StringComparison.Ordinal))
            {
                return false;
            }

            detail = entry.Name + " 的 SO 与 CSV 不一致（" + rows.Count + " 行）";
            return true;
        }

        // 功能：校验整张表，返回人类可读的问题列表。
        // 入参：entry 表登记；rows 行列表；errors 收集问题。
        // 返回含义：true 表示没问题。
        // 步骤：列名都能对上字段 → 主键唯一且 >0 → len/range/min 规则 → ref 列的值必须在目标表里。
        // 上游/下游：Export、Import 调用。
        public static bool Validate(ConfigTableEntry entry, IList rows, List<string> errors)
        {
            List<ConfigColumn> columns = ReadColumns(entry, out string columnError);

            if (!string.IsNullOrEmpty(columnError))
            {
                errors.Add(columnError);
                return false;
            }

            foreach (ConfigColumn column in columns)
            {
                if (GetField(entry, column.Name) == null)
                {
                    errors.Add(entry.Name + " 的 CSV 列 " + column.Name + " 在 " + entry.RowType.Name + " 上找不到同名字段");
                }
            }

            if (errors.Count > 0)
            {
                return false;
            }

            HashSet<int> ids = new HashSet<int>();

            for (int r = 0; r < rows.Count; r++)
            {
                object row = rows[r];
                string tag = entry.Name + " 第 " + (r + 1) + " 行";

                if (columns.Count > 0 && string.Equals(columns[0].Name, "id", StringComparison.Ordinal))
                {
                    int id = Convert.ToInt32(GetFieldValue(entry, row, "id"), CultureInfo.InvariantCulture);
                    tag += "（id=" + id + "）";

                    if (id <= 0)
                    {
                        errors.Add(tag + "：主键必须大于 0");
                    }
                    else if (!ids.Add(id))
                    {
                        errors.Add(tag + "：主键重复");
                    }
                }

                foreach (ConfigColumn column in columns)
                {
                    object value = GetFieldValue(entry, row, column.Name);
                    string problem = CheckRule(column, value);

                    if (problem != null)
                    {
                        errors.Add(tag + " 列 " + column.Name + "：" + problem);
                    }
                }

                foreach (ConfigColumn column in columns)
                {
                    if (column.Type.StartsWith("ref:", StringComparison.Ordinal) && column.Rule.IndexOf("view", StringComparison.Ordinal) >= 0)
                    {
                        continue;
                    }

                    if (column.Type.StartsWith("ref:", StringComparison.Ordinal))
                    {
                        string tableName = column.Type.Substring(4).TrimEnd('?', '[', ']');
                        HashSet<int> known = GetReferenceIds(tableName);
                        object value = GetFieldValue(entry, row, column.Name);

                        if (known != null && value is Array array)
                        {
                            foreach (object item in array)
                            {
                                CheckReference(item, tableName, known, tag, column.Name, errors);
                            }
                        }
                        else if (known != null && value != null)
                        {
                            CheckReference(value, tableName, known, tag, column.Name, errors);
                        }
                    }
                }
            }

            return errors.Count == 0;
        }

        // 功能：按表头规则校验单个值。
        // 入参：column 列定义；value 字段值。
        // 返回含义：没问题返回 null，否则返回问题描述。
        // 步骤：len(min,max) → range(min,max) → min(x)。
        // 上游/下游：Validate 调用。
        private static string CheckRule(ConfigColumn column, object value)
        {
            string rule = column.Rule == null ? string.Empty : column.Rule.Trim();

            if (rule.Length == 0 || value == null)
            {
                return null;
            }

            if (rule.StartsWith("len(", StringComparison.Ordinal))
            {
                int[] parts = ParseRuleNumbers(rule);

                if (parts == null || !(value is string text))
                {
                    return null;
                }

                if (text.Length < parts[0] || (parts.Length > 1 && text.Length > parts[1]))
                {
                    return "长度必须是 " + parts[0] + "~" + (parts.Length > 1 ? parts[1] : parts[0]) + " 个字符";
                }

                return null;
            }

            if (rule.StartsWith("range(", StringComparison.Ordinal) || rule.StartsWith("min(", StringComparison.Ordinal))
            {
                int[] parts = ParseRuleNumbers(rule);

                if (parts == null || value is Array)
                {
                    return null;
                }

                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);

                if (number < parts[0] || (parts.Length > 1 && number > parts[1]))
                {
                    return "取值范围 " + parts[0] + "~" + (parts.Length > 1 ? parts[1].ToString(CultureInfo.InvariantCulture) : "∞") +
                           "，当前 " + number.ToString(CultureInfo.InvariantCulture);
                }
            }

            return null;
        }

        // 功能：从规则文本里抠出数字。
        // 入参：rule 如 len(1,16) / range(2,4) / min(0)。
        // 返回含义：数字数组；抠不到返回 null。
        // 步骤：去掉括号与前缀 → 按逗号切 → 逐个解析，失败返回 null。
        // 上游/下游：CheckRule 调用。
        private static int[] ParseRuleNumbers(string rule)
        {
            int open = rule.IndexOf('(');
            int close = rule.IndexOf(')');

            if (open < 0 || close < open)
            {
                return null;
            }

            string body = rule.Substring(open + 1, close - open - 1);
            string[] parts = body.Split(',');

            if (parts.Length == 0 || parts.Length > 2)
            {
                return null;
            }

            int[] numbers = new int[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
                {
                    return null;
                }
            }

            return numbers;
        }

        // 功能：校验一个引用值是否存在于目标表。
        // 入参：value 引用值；tableName 目标表；known 目标表主键；tag 行标识；column 列名；errors 收集。
        // 返回含义：无。
        // 步骤：0 视为空引用直接放行；否则必须命中。
        // 上游/下游：Validate 调用。
        private static void CheckReference(object value, string tableName, HashSet<int> known, string tag, string column,
            List<string> errors)
        {
            int id = Convert.ToInt32(value, CultureInfo.InvariantCulture);

            if (id == 0)
            {
                return;
            }

            if (!known.Contains(id))
            {
                errors.Add(tag + " 列 " + column + "：" + tableName + " 表里没有编号 " + id);
            }
        }

        // 功能：拿目标表的主键集合（带缓存）。
        // 入参：tableName 目标表名。
        // 返回含义：主键集合；找不到表返回 null。
        // 步骤：先查缓存 → 找登记 → 读 CSV → 取 id 列。
        // 上游/下游：Validate 调用。
        private static HashSet<int> GetReferenceIds(string tableName)
        {
            if (s_references.TryGetValue(tableName, out HashSet<int> cached))
            {
                return cached;
            }

            ConfigTableEntry entry = ConfigTableRegistry.Find(tableName);

            if (entry == null)
            {
                return null;
            }

            HashSet<int> ids = new HashSet<int>();
            List<ConfigColumn> columns = ReadColumns(entry, out _);
            string text = Normalize(ReadCsvText(ResolveAbsolute(entry.CsvPath), out _));
            string[] lines = text.Split('\n');
            int idIndex = 0;

            for (int i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i].Name, "id", StringComparison.Ordinal))
                {
                    idIndex = i;
                    break;
                }
            }

            for (int i = HeaderLineCount; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                string[] cells = SplitLine(lines[i]);

                if (idIndex < cells.Length && int.TryParse(cells[idIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                {
                    ids.Add(id);
                }
            }

            s_references[tableName] = ids;
            return ids;
        }

        // 功能：清空引用缓存（改过 CSV 后要重读）。
        // 入参：无。
        // 返回含义：无。
        // 步骤：清两个字典。
        // 上游/下游：Export 成功后调用。
        public static void InvalidateCaches()
        {
            s_references.Clear();
        }

        // 功能：导出 SO 到 CSV 并重跑 ConfigKit，失败回滚。
        // 入参：entry 表登记；asset SO；message 输出结果。
        // 返回含义：true 表示成功。
        // 步骤：校验 → 读磁盘表头 → 重拼 → 备份 → 临时文件 → 替换 → ConfigKit → 失败回滚。
        // 上游/下游：窗口按钮调用。
        public static bool Export(ConfigTableEntry entry, ScriptableObject asset, out string message)
        {
            message = string.Empty;
            IList rows = GetRows(asset);
            List<string> errors = new List<string>();
            List<ConfigColumn> columns = ReadColumns(entry, out string columnError);

            if (!string.IsNullOrEmpty(columnError))
            {
                message = columnError;
                return false;
            }

            if (columns.Count == 0 || !string.Equals(columns[0].Name, "id", StringComparison.Ordinal))
            {
                message = entry.CsvPath + " 的第 1 列必须是 id";
                return false;
            }

            if (!Validate(entry, rows, errors))
            {
                message = "校验没通过，磁盘没有改动：\n" + string.Join("\n", errors);
                return false;
            }

            string path = ResolveAbsolute(entry.CsvPath);
            string disk;
            string sourceEncoding;

            try
            {
                disk = Normalize(ReadCsvText(path, out sourceEncoding));
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                message = "读不到 CSV（可能正被 Excel 打开）：" + exception.Message + "\n请先关闭 Excel 再导出。";
                return false;
            }

            string text = BuildCsvText(entry, rows, TakeHeaderBlock(disk));
            string backup = path + ".bak";
            bool writeBom = !File.Exists(path) || HasUtf8Bom(path);

            try
            {
                File.Copy(path, backup, true);
                File.WriteAllText(path + ".tmp", text, new UTF8Encoding(writeBom));
                File.Copy(path + ".tmp", path, true);
                File.Delete(path + ".tmp");
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                message = "写不进 CSV（可能正被 Excel 打开）：" + exception.Message + "\n请先关闭 Excel 再导出。";
                return false;
            }

            if (!RunConfigKit(out string toolMessage))
            {
                File.Copy(backup, path, true);
                message = "ConfigKit 失败，已回滚 CSV：" + toolMessage;
                return false;
            }

            File.Delete(backup);
            InvalidateCaches();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            message = "已导出 " + rows.Count + " 行到 " + entry.CsvPath + "（原文件编码 " + sourceEncoding +
                      (writeBom ? "，已统一成 UTF-8 带 BOM" : "，保持 UTF-8 无 BOM") + "），并已重新生成。\n" + toolMessage;
            return true;
        }

        // 功能：从 CSV 导入到 SO，校验不过就不动资产。
        // 入参：entry 表登记；asset SO；message 输出结果。
        // 返回含义：true 表示成功。
        // 步骤：读 → 逐行逐列解析（失败报行与列）→ 整体校验 → 通过才替换 rows。
        // 上游/下游：窗口按钮调用。
        public static bool Import(ConfigTableEntry entry, ScriptableObject asset, out string message)
        {
            message = string.Empty;
            string path = ResolveAbsolute(entry.CsvPath);
            string csvText;
            string sourceEncoding;

            try
            {
                csvText = ReadCsvText(path, out sourceEncoding);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                message = "读不到 CSV（可能正被 Excel 打开）：" + exception.Message + "\n请先关闭 Excel 再导入。";
                return false;
            }

            string[] lines = Normalize(csvText).Split('\n');
            List<ConfigColumn> columns = ReadColumns(entry, out string columnError);

            if (!string.IsNullOrEmpty(columnError))
            {
                message = columnError;
                return false;
            }

            List<object> parsed = new List<object>();
            List<string> errors = new List<string>();

            for (int i = HeaderLineCount; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                string[] cells = SplitLine(lines[i]);
                object row = Activator.CreateInstance(entry.RowType);

                for (int c = 0; c < columns.Count && c < cells.Length; c++)
                {
                    FieldInfo field = GetField(entry, columns[c].Name);

                    if (field == null)
                    {
                        errors.Add("第 " + (i + 1) + " 行：列 " + columns[c].Name + " 在 " + entry.RowType.Name + " 上没有同名字段");
                        continue;
                    }

                    if (!TryParseCell(cells[c], field.FieldType, out object value, out string problem))
                    {
                        errors.Add("第 " + (i + 1) + " 行 列 " + columns[c].Name + "：值「" + cells[c] + "」" + problem);
                        continue;
                    }

                    field.SetValue(row, value);
                }

                parsed.Add(row);
            }

            if (errors.Count > 0)
            {
                message = "CSV 解析失败，资产没有改动：\n" + string.Join("\n", errors);
                return false;
            }

            IList rows = GetRows(asset);
            List<object> backup = new List<object>();

            foreach (object row in rows)
            {
                backup.Add(row);
            }

            rows.Clear();

            foreach (object row in parsed)
            {
                rows.Add(row);
            }

            List<string> validateErrors = new List<string>();

            if (!Validate(entry, rows, validateErrors))
            {
                rows.Clear();

                foreach (object row in backup)
                {
                    rows.Add(row);
                }

                message = "CSV 校验没通过，资产没有改动：\n" + string.Join("\n", validateErrors);
                return false;
            }

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            InvalidateCaches();
            message = "已从 " + entry.CsvPath + " 导入 " + parsed.Count + " 行（文件编码 " + sourceEncoding + "），资产已保存。";
            return true;
        }

        // 功能：把单元格文本转成字段类型的值。
        // 入参：text 单元格；type 字段类型；value 输出值；problem 输出失败原因。
        // 返回含义：true 表示解析成功。
        // 步骤：int/float 用不变文化；int[] 切逗号；enum 先名字后数字；string 原样。
        // 上游/下游：Import 调用。
        public static bool TryParseCell(string text, Type type, out object value, out string problem)
        {
            value = null;
            problem = string.Empty;
            string trimmed = text.Trim();

            if (type == typeof(int))
            {
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                {
                    problem = " 不是 int";
                    return false;
                }

                value = number;
                return true;
            }

            if (type == typeof(float))
            {
                if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                {
                    problem = " 不是 float";
                    return false;
                }

                value = number;
                return true;
            }

            if (type == typeof(string))
            {
                value = text;
                return true;
            }

            if (type == typeof(int[]))
            {
                if (trimmed.Length == 0)
                {
                    value = Array.Empty<int>();
                    return true;
                }

                string[] parts = trimmed.Split(',');
                int[] numbers = new int[parts.Length];

                for (int i = 0; i < parts.Length; i++)
                {
                    if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
                    {
                        problem = " 里有不是 int 的项「" + parts[i] + "」";
                        return false;
                    }
                }

                value = numbers;
                return true;
            }

            if (type.IsEnum)
            {
                if (trimmed.Length == 0)
                {
                    value = Enum.ToObject(type, 0);
                    return true;
                }

                try
                {
                    value = Enum.Parse(type, trimmed, true);
                    return true;
                }
                catch (ArgumentException)
                {
                    if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                    {
                        value = Enum.ToObject(type, number);
                        return true;
                    }

                    problem = " 不是 " + type.Name + " 的取值（可选：" + string.Join("/", Enum.GetNames(type)) + "）";
                    return false;
                }
            }

            problem = " 的类型 " + type.Name + " 暂不支持";
            return false;
        }

        // 功能：跑 ConfigKit 重新生成两端代码。
        // 入参：message 输出工具输出或错误。
        // 返回含义：true 表示退出码为 0。
        // 步骤：拼 exe 与参数 → 起进程 → 最多等 120 秒 → 退出码判断。
        // 上游/下游：Export 调用。
        public static bool RunConfigKit(out string message)
        {
            string root = ResolveRepoRoot();
            string tool = Path.Combine(root, ConfigToolRelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(tool))
            {
                message = "找不到 ConfigKit：" + ConfigToolRelativePath;
                return false;
            }

            System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = tool,
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("--source");
            info.ArgumentList.Add("Configs/Design");
            info.ArgumentList.Add("--out");
            info.ArgumentList.Add("Client/Assets/_Project/Game/Config/Generated");

            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(info))
            {
                if (process == null)
                {
                    message = "ConfigKit 启动失败";
                    return false;
                }

                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

                if (!process.WaitForExit(120000))
                {
                    process.Kill();
                    message = "ConfigKit 超过 120 秒未结束";
                    return false;
                }

                message = output.Trim();
                return process.ExitCode == 0;
            }
        }

        // 功能：反射拿 SO 上的 rows。
        // 入参：asset SO。
        // 返回含义：rows 的 IList；没有该字段返回 null。
        // 步骤：GetField("rows") → 取 GetValue。
        // 上游/下游：IsDirty、Export、Import 调用。
        private static IList GetRows(ScriptableObject asset)
        {
            FieldInfo field = asset.GetType().GetField("rows", BindingFlags.Public | BindingFlags.Instance);
            return field == null ? null : field.GetValue(asset) as IList;
        }

        // 功能：按列名拿行类型的字段（带缓存）。
        // 入参：entry 表登记；name 列名。
        // 返回含义：FieldInfo 或 null。
        // 步骤：先查缓存 → 建行类型的字段字典。
        // 上游/下游：BuildCsvText、Validate、Import 调用。
        private static FieldInfo GetField(ConfigTableEntry entry, string name)
        {
            if (!s_fields.TryGetValue(entry.RowType, out Dictionary<string, FieldInfo> map))
            {
                map = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

                foreach (FieldInfo field in entry.RowType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    map[field.Name] = field;
                }

                s_fields[entry.RowType] = map;
            }

            map.TryGetValue(name, out FieldInfo found);
            return found;
        }

        // 功能：取某一行的某个字段值。
        // 入参：entry 表登记；row 行；name 列名。
        // 返回含义：字段值；字段不存在返回 null。
        // 步骤：GetField → GetValue。
        // 上游/下游：BuildCsvText、Validate 调用。
        private static object GetFieldValue(ConfigTableEntry entry, object row, string name)
        {
            FieldInfo field = GetField(entry, name);
            return field == null ? null : field.GetValue(row);
        }

        // 功能：取磁盘文本的前 4 行表头。
        // 入参：text 磁盘文本。
        // 返回含义：前 4 行（含换行）。
        // 步骤：按行切分后取前 4 行再拼回。
        // 上游/下游：BuildCsvText 的表头来源。
        private static string TakeHeaderBlock(string text)
        {
            string[] lines = Normalize(text).Split('\n');
            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < HeaderLineCount && i < lines.Length; i++)
            {
                builder.Append(lines[i]).Append('\n');
            }

            return builder.ToString();
        }

        // 功能：统一换行与去尾部空行。
        // 入参：text 文本。
        // 返回含义：LF 结尾的文本。
        // 步骤：CRLF → LF，TrimEnd。
        // 上游/下游：读写路径调用。
        private static string Normalize(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        }

        // 功能：切一行 CSV，支持引号与 "" 转义。
        // 入参：line 一行文本。
        // 返回含义：单元格数组。
        // 步骤：逐字符扫，引号内逗号不切；未闭合抛 FormatException。
        // 上游/下游：ReadColumns、Validate、Import 调用。
        public static string[] SplitLine(string line)
        {
            List<string> cells = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
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
                        current.Append(c);
                    }

                    continue;
                }

                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    cells.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            if (inQuotes)
            {
                throw new FormatException("引号没有闭合：" + line);
            }

            cells.Add(current.ToString());
            return cells.ToArray();
        }
    }
}
