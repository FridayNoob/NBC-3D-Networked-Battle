// ============================================================================
//  ConfigStalenessCheck —— 检查「配置表资产」是不是比「源表」旧
//  项目：3D联网战斗Demo   对应：M2-C 收尾（一个**真发生过两次**的坑）
//
//  ---------------------------------------------------------------------------
//  为什么需要它（不是"想多做点工具"，是踩了两次）
//  ---------------------------------------------------------------------------
//  配置表的流水线是**两步、且第二步要人手点**：
//
//      CSV（策划改）  →【ConfigKit.Cli 生成】→  .tsv / .cs
//                                              ↓
//                             【Tools/NBC/配置表/导入 TSV → SO】← **人手点**
//                                              ↓
//                                        <表>Config.asset
//
//  而"忘了点第二步"**不会报任何错**：`ConfigMgr` 照样能加载资产、照样能查表，
//  只是那份资产是**旧快照**。M2 期间这个坑踩了**两次**：
//
//     ① 模拟清单是快照：M2 加了 4 张表，旧的清单里没有它们 -> 加载报错
//     ② 配置资产是快照：给 `Quest` 加了任务 3004，忘了重新导入 ->
//        资产里根本没有 3004，运行时才报"配置表 Quest 里没有任务 3004"
//
//  两次的形状完全一样：**"手工步骤 + 没有提示"= 一定会在某天以别的 bug 的面目出现。**
//  所以给它一个**能一眼看出来**的检查。
//
//  ---------------------------------------------------------------------------
//  判据：比 mtime（并如实说明这条判据的边界）
//  ---------------------------------------------------------------------------
//  只要 `*.tsv` 比 `<表>Config.asset` 新，就提示"需要重新导入"。
//
//  ⚠️ **已知局限（写清楚，免得被当成精确工具）**：
//    · mtime 是**启发式**：碰一下 TSV 但内容没变，也会被报成"过期"（假阳性 —— 无害，
//      点一下导入就好）；反过来，**手工改过 .asset** 而 TSV 没动，这里**查不出来**
//      （那种情况要靠"生成物与资产都入库 + Git diff"来看）
//    · 它只比时间，**不做内容对比**。真要做内容对比，得把 TSV 解析一遍再和资产比 ——
//      那是 CFG-06（增量导出）该干的事，M2 不做
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么新开一个菜单，而不是改 `ConfigImporter`
//  ---------------------------------------------------------------------------
//  `ConfigImporter` 是 M1-C3 已收关的模块（W10：尽量避免改已收关的代码，优先"新增"）。
//  它已经把两个目录路径开成了 `public const`，所以本文件**直接复用**，
//  一行都不用动它。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    /// <summary>配置表资产与源表的"新鲜度"检查（只报告，不改任何东西）。</summary>
    public static class ConfigStalenessCheck
    {
        /// <summary>过期（源表比资产新）的表。</summary>
        private sealed class StaleTable
        {
            /// <summary>表名。</summary>
            public string Table;

            /// <summary>状态描述。</summary>
            public string Status;

            /// <summary>源表时间。</summary>
            public DateTime SourceTime;

            /// <summary>资产时间（资产不存在时为默认值）。</summary>
            public DateTime AssetTime;

            /// <summary>资产存在吗。</summary>
            public bool AssetExists;
        }

        /// <summary>检查全部表的资产是否过期（菜单入口）。</summary>
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

        /// <summary>拼报告。</summary>
        /// <param name="stale">过期的表。</param>
        /// <param name="missing">资产不存在的表数。</param>
        /// <param name="ok">最新的表数。</param>
        /// <param name="total">总表数。</param>
        /// <returns>报告文本。</returns>
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

        /// <summary>把 "Assets/..." 换成绝对路径（`Application.dataPath` 去掉末尾的 /Assets）。</summary>
        /// <param name="assetRelativePath">以 Assets/ 开头的路径。</param>
        /// <returns>绝对路径。</returns>
        private static string ToAbsolute(string assetRelativePath)
        {
            string projectRoot = Application.dataPath.Substring(
                0, Application.dataPath.Length - "/Assets".Length);

            return Path.Combine(projectRoot, assetRelativePath).Replace('\\', '/');
        }
    }
}
