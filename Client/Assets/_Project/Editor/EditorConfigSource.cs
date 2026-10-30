// 这个文件里有：
//   EditorConfigSource（类）：编辑器里用 AssetDatabase 同步直读配置资产的 IConfigSource 实现，只在没进 Play 时顶替运行时来源

using System;
using NBC.Game.Config;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    // 主要功能：编辑器版的 IConfigSource —— 运行时来源 AssetConfigSource 经 YooAsset 取资产，没进 Play、资源层没装配时由本类用 AssetDatabase 直读顶上。
    // 主要数据成员：无自有字段；Description 是固定来源描述，LoadedCount 是已成功加载的表数（从 0 起，每成功一张 +1）。
    // 主要外部方法：LoadTable（按表名读一张表，成功与失败各回调一次）、Description、LoadedCount（只读）。
    // 谁在用它：本程序集的 NetDebugWindow.LoadConfigs —— SetSource 装来源，再由 GameTables.PreloadAll 逐表调 LoadTable。
    // 注意：回调是同步的（onLoaded 在 LoadTable 里当场调），这正是它在编辑器里好用的原因，也是它不能进包的原因 —— 本文件在 NBC.Editor 程序集，该程序集只编编辑器，进包由程序集边界保证，不靠「记得别用」。
    // 注意：不预加载、不缓存 —— ConfigMgr 那一层已经缓存，这里每次只 LoadAssetAtPath 一下。
    // 注意：资产路径复用 ConfigImporter.AssetDirectory（Assets/_Project/Configs）—— 导入器写资产、这里读资产，两处必须指向同一目录，所以只留一个真值。
    public sealed class EditorConfigSource : IConfigSource
    {
        // 来源描述，写日志与报错时用；目前固定为编辑器直读资产（AssetDatabase）。
        public string Description
        {
            get { return "编辑器直读资产（AssetDatabase）"; }
        }

        // 已成功加载的表数，从 0 起，每次成功加载一张 +1；调试窗显示用。
        public int LoadedCount { get; private set; }

        // 功能：按表名拼出资产路径并用 AssetDatabase 同步读进来，成功交资产、失败交一句能照着做的原因。
        // 入参：tableName 表名（如 Quest，不带 Config 后缀），空表名直接回调失败；onLoaded 成功回调（交出资产）；onFailed 失败回调（交出原因，可为 null）。
        // 返回：无（结果全走回调）。
        // 步骤：
        //   1) 先拦空表名 —— 空表名拼出的路径必然读不到，不如当场告诉调用方表名给错了
        //   2) 用 ConfigMgr.AssetNameOf 拼资产名，再拼上 ConfigImporter.AssetDirectory 与 .asset 后缀 —— 目录只认导入器那一个真值，读写才不会指向两处
        //   3) 同步读资产；读不到时把能照着做的下一步（先生成 TSV 再走菜单导入）写进失败原因，而不是只报一个路径
        //   4) 读到了才把 LoadedCount 加一，再回调 —— 计数只反映真正成功的表
        // 上游：ConfigMgr 的加载流程（GameTables.PreloadAll 逐表调 LoadTable），本程序集里由 NetDebugWindow.LoadConfigs 触发。
        // 下游：ConfigImporter.AssetDirectory（目录）、ConfigMgr.AssetNameOf（表名转资产名）、AssetDatabase.LoadAssetAtPath（读资产）。
        // 注意：同步回调，且成功与失败必有一个被调用、只调一次 —— 这是 IConfigSource 的契约，调用方不能当异步等。
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
