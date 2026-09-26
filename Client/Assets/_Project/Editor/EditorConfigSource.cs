// ============================================================================
//  EditorConfigSource —— 编辑器里"直读 .asset"的配置来源（M4-S1c）
//  项目：3D联网战斗Demo   对应：`Docs\27-M4开工清单.md` §八 第 ③ 步
//
//  ---------------------------------------------------------------------------
//  它补的是哪个缺口
//  ---------------------------------------------------------------------------
//  M4-S1 接通的"服务端事件 → 任务进度"用的是**造出来的**演示条件（不读配置表）——
//  目的是先让"接线对不对"可见。要验证**真任务**（`Quest` / `QuestCondition` / `Reward` 三张表
//  驱动的"接取 → 进度 → 完成 → 交付 → 发奖"），就得先把配置表喂进 `ConfigMgr`。
//
//  运行时的正式来源是 `AssetConfigSource`，它经 `AssetManager`（YooAsset）取资产 ——
//  而**在编辑器里、没进 Play、资源层没装配**时那条路走不通。
//  本类就是编辑器版：**`AssetDatabase` 同步直读** `Assets/_Project/Configs/<表>Config.asset`。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 两条如实记录的取舍
//  ---------------------------------------------------------------------------
//  ① **它是同步回调的**（`onLoaded` 在 `LoadTable` 里当场调）。这正是它在编辑器里好用的原因
//     （不用等资源层），也是它**不能进包**的原因 —— 本文件在 `NBC.Editor` 程序集里，
//     而编辑器程序集**不进包**（`includePlatforms: ["Editor"]`），所以这事是**程序集边界**保证的，
//     不是"记得别用"。
//  ② 它**不预加载、不缓存**：`ConfigMgr` 那一层已经在缓存；这里每次就 `LoadAssetAtPath` 一下。
//
//  ⚠️ 资产路径复用 `ConfigImporter.AssetDirectory`（`Assets/_Project/Configs`）——
//     导入器写资产、这里读资产，**两处必须指向同一个目录**，所以只留一个真值。
// ============================================================================

using System;
using NBC.Game.Config;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    /// <summary>编辑器专用的配置来源：用 `AssetDatabase` 直读导入好的 `.asset`。</summary>
    public sealed class EditorConfigSource : IConfigSource
    {
        /// <summary>来源描述（报错与日志里用）。</summary>
        public string Description
        {
            get { return "编辑器直读资产（AssetDatabase）"; }
        }

        /// <summary>已经加载成功过几张表（调试窗上显示用）。</summary>
        public int LoadedCount { get; private set; }

        /// <summary>
        /// 加载一张表。
        /// <para>⚠️ **回调是同步的**（见文件头取舍①）：成功与失败至少会有一个被调用，
        /// 且只调一次 —— 这是 `IConfigSource` 的契约。</para>
        /// </summary>
        /// <param name="tableName">表名（如 `Quest`）。</param>
        /// <param name="onLoaded">成功：交出资产。</param>
        /// <param name="onFailed">失败：交出一句人话。</param>
        public void LoadTable(string tableName, Action<ScriptableObject> onLoaded, Action<string> onFailed)
        {
            if (string.IsNullOrEmpty(tableName))
            {
                if (onFailed != null)
                {
                    onFailed("表名是空的 —— 调用方给错了（看 `GameTables.All`）。");
                }

                return;
            }

            string assetName = ConfigMgr.AssetNameOf(tableName);
            string path = ConfigImporter.AssetDirectory + "/" + assetName + ".asset";

            ScriptableObject asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);

            if (asset == null)
            {
                if (onFailed != null)
                {
                    onFailed("读不到配置资产 `" + path + "`。\n" +
                             "  · 第一次跑？先执行 ConfigKit 生成 TSV，再点菜单 `Tools/NBC/配置表/导入 TSV → ScriptableObject`；\n" +
                             "  · 改过源表？同样要重新生成 + 重新导入（源 CSV 是真源，资产是生成物）。");
                }

                return;
            }

            LoadedCount++;

            if (onLoaded != null)
            {
                onLoaded(asset);
            }
        }
    }
}
