// 这个文件里有：
//   MonsterAiCsvWriter（静态类）：把某只怪的三个判定长度写回 Configs/Design/Monster.csv。
//
// 【仅编辑器】只改三列数值，其它列（含带逗号的 skillIds）原样保留；写完提示重生成命令，不自动调用 CLI。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace NBC.EditorTools
{
    // 主要功能：按 Monster 主键定位 CSV 行，改 aggroRangeMm / attackRangeMm / groundLineMm 三列。
    // 主要数据成员：CsvRelativePath、RegenerateCommand。
    // 主要外部方法：TryWrite。
    // 谁在用它：MonsterAiDebugViewEditor 的「写回」按钮。
    // 注意：解析按引号感知切分（skillIds 里有逗号），未触碰的列连引号一起原样写回。
    public static class MonsterAiCsvWriter
    {
        // CSV 相对项目根的路径（真值源）
        public const string CsvRelativePath = "Configs/Design/Monster.csv";

        // 写回后要执行的重生成命令（只提示，不代跑）
        public const string RegenerateCommand =
            "Tools\\ConfigKit\\src\\ConfigKit.Cli\\bin\\Debug\\net8.0\\NBC.ConfigKit.Cli.exe " +
            "--source Configs\\Design --out Client\\Assets\\_Project\\Game\\Config\\Generated";

        // 列名常量（与 CSV 表头一致）
        private const string ColumnAggro = "aggroRangeMm";
        private const string ColumnAttack = "attackRangeMm";
        private const string ColumnGround = "groundLineMm";

        // 功能：写回三列。
        // 入参：monsterId 主键；attackMm 攻击半径；aggroMm 检测半径；groundMm 接地线长
        // 返回：true = 写入成功；false = 失败且 message 说明原因
        // 步骤：
        //   1) 校验三个值都 > 0
        //   2) 读 CSV（保留原换行风格）
        //   3) 解析表头找列号；找数据行
        //   4) 引号感知切分 → 改三格 → 拼回
        //   5) 写文件（UTF-8 无 BOM）并返回"下一步"
        // 上游：MonsterAiDebugViewEditor
        // 下游：File.WriteAllText
        public static bool TryWrite(int monsterId, int attackMm, int aggroMm, int groundMm, out string message)
        {
            if (monsterId <= 0)
            {
                message = "怪物编号非法：" + monsterId;
                return false;
            }

            if (attackMm <= 0 || aggroMm <= 0 || groundMm <= 0)
            {
                message = "三个长度都必须大于 0（当前：" + attackMm + " / " + aggroMm + " / " + groundMm + "）";
                return false;
            }

            string path = ResolveCsvPath();

            if (!File.Exists(path))
            {
                message = "找不到配置表：" + path;
                return false;
            }

            string text = File.ReadAllText(path);
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string[] lines = text.Replace("\r\n", "\n").Split('\n');

            if (lines.Length < 5)
            {
                message = "配置表格式不对（至少要 4 行表头 + 1 行数据）";
                return false;
            }

            List<string> header = SplitFields(lines[0]);
            int aggroIndex = header.IndexOf(ColumnAggro);
            int attackIndex = header.IndexOf(ColumnAttack);
            int groundIndex = header.IndexOf(ColumnGround);

            if (aggroIndex < 0 || attackIndex < 0 || groundIndex < 0)
            {
                message = "表头缺少列：需要 " + ColumnAggro + " / " + ColumnAttack + " / " + ColumnGround;
                return false;
            }

            int rowIndex = -1;

            for (int i = 4; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                List<string> fields = SplitFields(lines[i]);

                if (fields.Count > 0 && fields[0].Trim() == monsterId.ToString(CultureInfo.InvariantCulture))
                {
                    rowIndex = i;
                    break;
                }
            }

            if (rowIndex < 0)
            {
                message = "CSV 里没有编号为 " + monsterId + " 的行";
                return false;
            }

            List<string> cells = SplitFields(lines[rowIndex]);

            if (cells.Count <= groundIndex)
            {
                message = "该行列数不足（" + cells.Count + " 列），无法写回";
                return false;
            }

            cells[aggroIndex] = aggroMm.ToString(CultureInfo.InvariantCulture);
            cells[attackIndex] = attackMm.ToString(CultureInfo.InvariantCulture);
            cells[groundIndex] = groundMm.ToString(CultureInfo.InvariantCulture);
            lines[rowIndex] = string.Join(",", cells);

            File.WriteAllText(path, string.Join(newline, lines), new UTF8Encoding(false));

            message = "已写回 Monster.csv（编号 " + monsterId + "：" + aggroMm + " / " + attackMm + " / " + groundMm + "）"
                      + "\n\n下一步（要你手动执行）：\n1) 重生成配置：\n" + RegenerateCommand
                      + "\n2) 重启服务端（否则判定半径仍是旧值）";
            return true;
        }

        // 功能：拼出 CSV 的绝对路径。
        // 入参：无
        // 返回：绝对路径
        // 步骤：Application.dataPath 的上一级（项目根）+ 相对路径
        // 上游：TryWrite
        // 下游：无
        private static string ResolveCsvPath()
        {
            // CSV 在**仓库根**（Unity 工程在 Client/ 子目录），所以要一层层往上找，
            // 不能只取 Application.dataPath 的上一级（那会得到 Client/Configs/...）
            DirectoryInfo dir = new DirectoryInfo(Application.dataPath);
            string relative = CsvRelativePath.Replace('/', Path.DirectorySeparatorChar);

            for (int i = 0; i < 5 && dir != null; i++)
            {
                string candidate = Path.Combine(dir.FullName, relative);

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            // 兜底：按 Unity 工程上一级拼（正常情况下上面的循环已经命中）
            return Path.Combine(Directory.GetParent(Application.dataPath).FullName, relative);
        }

        // 功能：按逗号切分一行，但**双引号里的逗号不算分隔符**；切出的字段保留原有引号。
        // 入参：line 一行文本
        // 返回：字段列表
        // 步骤：逐字符扫描 → 引号内不切 → 引号字符原样保留
        // 上游：TryWrite
        // 下游：无
        private static List<string> SplitFields(string line)
        {
            List<string> fields = new List<string>();
            StringBuilder buffer = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    buffer.Append(c);
                    continue;
                }

                if (c == ',' && !inQuotes)
                {
                    fields.Add(buffer.ToString());
                    buffer.Clear();
                    continue;
                }

                buffer.Append(c);
            }

            fields.Add(buffer.ToString());
            return fields;
        }
    }
}
