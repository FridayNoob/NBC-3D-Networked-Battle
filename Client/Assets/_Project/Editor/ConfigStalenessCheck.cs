// 这个文件里有：
//   ConfigStalenessCheck（静态类）：把生成目录里的 .tsv 与同名 Config.asset 比修改时间，报告哪些资产需要重新导入
//   StaleTable（私有嵌套类）：一条过期记录，装表名、状态文案、源表时间、资产时间与资产是否存在
//
// 注意：配置表流水线是两步，第二步「导入 TSV → SO」要人手点；忘了点不会报任何错，ConfigMgr 照样能查表，只是查到的是旧快照（模拟清单缺 4 张表、Quest 加了任务 3004 没重导，都出自这个坑）。
// 注意：判据只比文件修改时间，是启发式：碰一下 TSV 但内容没变会被报成过期（假阳性，导入一次即可）；反过来手工改过 .asset 而 TSV 没动，这里查不出来，要看 Git diff。
// 注意：本文件不去改 ConfigImporter（它已收关），只复用它的两个 public const 目录路径，因此是纯新增代码。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    // 主要功能：把生成目录里的 .tsv 与资产目录里同名资产（表名 + Config.asset）逐个比修改时间，汇报缺资产与源表更新的表。
    // 主要数据成员：无实例字段，只有局部量（表名、两侧文件时间、计数）。
    // 主要外部方法：Check（菜单入口）。
    // 谁在用它：只由 Unity 菜单 Tools/NBC/配置表/检查资产是否过期（只报告）触发。
    public static class ConfigStalenessCheck
    {
        // 主要功能：一条过期记录，纯粹给 Render 拼报告用。
        // 主要数据成员：Table（表名）、Status（状态文案）、SourceTime 与 AssetTime（文件修改时间，本地时区）、AssetExists（资产存在吗）。
        // 主要外部方法：无，纯数据容器。
        // 谁在用它：Check 逐表填充，Render 读取。
        private sealed class StaleTable
        {
            // 表名：.tsv 的文件名去掉扩展名，非空
            public string Table;

            // 状态文案：只取 Check 里赋的两种值——资产不存在、源表比资产新
            public string Status;

            // 源表 .tsv 的最后修改时间：本地时区，来自 File.GetLastWriteTime
            public DateTime SourceTime;

            // 资产 .asset 的最后修改时间：本地时区；资产不存在时保持默认值（DateTime.MinValue）
            public DateTime AssetTime;

            // 资产文件存在吗：true 表示已导入过、时间可比；false 表示缺资产
            public bool AssetExists;
        }

        // 功能：扫生成目录里的每个 .tsv，与同名资产比修改时间，拼出「哪些表要重新导入」的报告打到 Console。
        // 入参：无（菜单无参入口，两个目录取自 ConfigImporter 的 public const）
        // 返回：无
        // 步骤：
        //   1) 先把两个 Assets 相对路径转成绝对路径，后面只用绝对路径，不再依赖当前工作目录
        //   2) 生成目录不存在、或里面没有 .tsv，就直接报错 / 警告退出——没有源表时比时间没有意义
        //   3) 先把 .tsv 文件列表排序，让报告顺序稳定、两次运行可比
        //   4) 逐表查资产：缺资产单独计数，源表时间更晚记为过期，其余计入最新
        //   5) 全部收集完只渲染一次并输出，避免一张表一行日志刷屏
        // 上游：Unity 菜单 Tools/NBC/配置表/检查资产是否过期（只报告）
        // 下游：ToAbsolute（转路径）、Render（拼报告）
        [MenuItem("Tools/NBC/配置表/检查资产是否过期（只报告）")]
        private static void Check()
        {
            string tsvDirectory = ToAbsolute(ConfigImporter.TsvDirectory);
            string assetDirectory = ToAbsolute(ConfigImporter.AssetDirectory);

            if (!Directory.Exists(tsvDirectory))
            {
                Debug.LogError(
                    "[配置过期检查] 找不到生成目录 " + ConfigImporter.TsvDirectory + "。\n" +
                    "先在命令行跑一次 ConfigKit.Cli（见 Tools\\ConfigKit\\README.md §八）。");
                return;
            }

            string[] tsvFiles = Directory.GetFiles(tsvDirectory, "*.tsv");
            Array.Sort(tsvFiles, StringComparer.Ordinal);

            if (tsvFiles.Length == 0)
            {
                Debug.LogWarning("[配置过期检查] " + ConfigImporter.TsvDirectory + " 里没有 .tsv。");
                return;
            }

            List<StaleTable> stale = new List<StaleTable>();
            int missing = 0;
            int ok = 0;

            for (int i = 0; i < tsvFiles.Length; i++)
            {
                string table = Path.GetFileNameWithoutExtension(tsvFiles[i]);
                string assetPath = Path.Combine(assetDirectory, table + "Config.asset");

                DateTime sourceTime = File.GetLastWriteTime(tsvFiles[i]);

                if (!File.Exists(assetPath))
                {
                    missing++;
                    stale.Add(new StaleTable
                    {
                        Table = table,
                        Status = "资产**不存在** -> 需要导入",
                        SourceTime = sourceTime,
                        AssetExists = false
                    });
                    continue;
                }

                DateTime assetTime = File.GetLastWriteTime(assetPath);

                if (sourceTime > assetTime)
                {
                    stale.Add(new StaleTable
                    {
                        Table = table,
                        Status = "源表比资产**新** -> 需要重新导入",
                        SourceTime = sourceTime,
                        AssetTime = assetTime,
                        AssetExists = true
                    });
                    continue;
                }

                ok++;
            }

            Debug.Log(Render(stale, missing, ok, tsvFiles.Length));
        }

        // 功能：把过期清单拼成一段给人看的报告文本，含顶部统计、逐表时间与末尾处理办法。
        // 入参：stale 过期与缺资产的表（可为空）；missing 其中缺资产的数量；ok 最新的表数；total 总表数
        // 返回：报告全文；stale 为空时是一句「全部最新」的结论
        // 步骤：
        //   1) 先写表头与两个目录，出问题时能一眼看出查的是哪份配置
        //   2) 接着写统计行，stale 为空就直接提前返回，省掉后面的明细
        //   3) 有明细时逐表输出；资产不存在的不打印资产时间，免得看到默认时间误解
        //   4) 末尾补上处理办法与判据说明，让看日志的人不用再翻代码——判据只比修改时间，手工改过 .asset 而源表没动的情况查不出来，要看 Git diff
        // 上游：Check 最后一次性调用
        // 下游：无
        private static string Render(List<StaleTable> stale, int missing, int ok, int total)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("=== 配置表过期检查 ===\n");
            builder.Append("生成目录：").Append(ConfigImporter.TsvDirectory).Append('\n');
            builder.Append("资产目录：").Append(ConfigImporter.AssetDirectory).Append('\n');
            builder.Append("共 ").Append(total).Append(" 张表：")
                   .Append(ok).Append(" 最新、").Append(stale.Count - missing).Append(" 过期、")
                   .Append(missing).Append(" 缺资产\n");

            if (stale.Count == 0)
            {
                builder.Append("✅ 全部资产的导入时间不早于源表 —— 不需要重新导入。");
                return builder.ToString();
            }

            builder.Append("\n需要处理的表：\n");

            for (int i = 0; i < stale.Count; i++)
            {
                StaleTable item = stale[i];
                builder.Append("  · ").Append(item.Table).Append("　").Append(item.Status).Append('\n');
                builder.Append("      源表 ").Append(item.SourceTime.ToString("MM-dd HH:mm:ss"));

                if (item.AssetExists)
                {
                    builder.Append("　资产 ").Append(item.AssetTime.ToString("MM-dd HH:mm:ss"));
                }

                builder.Append('\n');
            }

            builder.Append("\n处理办法：菜单 `Tools/NBC/配置表/导入 TSV → ScriptableObject`。\n");
            builder.Append("⚠️ 提醒：改过 CSV 之后**必须先跑 ConfigKit.Cli 生成**，再导入；");
            builder.Append("否则导入的是上一版 .tsv。\n");
            builder.Append("⚠️ 判据说明：这里只比文件的**修改时间**（启发式）——");
            builder.Append("手工改过 .asset 而源表没动的情况查不出来（那种情况看 Git diff）。");

            return builder.ToString();
        }

        // 功能：把以 Assets/ 开头的工程相对路径换成绝对路径。
        // 入参：assetRelativePath 以 Assets/ 开头的工程内路径，如 Assets/_Project/Config
        // 返回：绝对路径，分隔符统一成正斜杠
        // 步骤：
        //   1) Application.dataPath 结尾就是 /Assets，截掉这一段得到工程根目录，避免依赖当前工作目录
        //   2) 拼上相对路径，再把反斜杠换成斜杠，免得同一路径在日志里出现两种写法
        // 上游：Check 开头转两个目录
        // 下游：无
        private static string ToAbsolute(string assetRelativePath)
        {
            string projectRoot = Application.dataPath.Substring(
                0, Application.dataPath.Length - "/Assets".Length);

            return Path.Combine(projectRoot, assetRelativePath).Replace('\\', '/');
        }
    }
}
