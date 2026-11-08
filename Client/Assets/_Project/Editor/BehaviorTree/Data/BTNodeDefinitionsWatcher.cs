// 这个文件里有：
//   BTNodeDefinitionsWatcher（类）：监听节点定义资产的变化，通知注册表重载与窗口刷新。
//
// 【仅编辑器】AssetPostprocessor 只在编辑器里执行；它解决的是「策划改了 SO，但编辑器还拿着旧缓存」。

using UnityEditor;

namespace NBC.EditorTools.BehaviorTree
{
    // 主要功能：SO 被改动后自动让 BTNodeRegistry 失效，并通知已打开的行为树编辑器窗口重建左栏/属性面板。
    // 主要数据成员：无。
    // 主要外部方法：OnPostprocessAllAssets（Unity 在资源导入后回调）。
    // 谁在用它：Unity 的资源导入管线。
    // 注意：只认 BTNodeDefinitions.asset 这一条路径；其它资产变化不受影响（避免无谓重载）。
    internal sealed class BTNodeDefinitionsWatcher : AssetPostprocessor
    {
        // 功能：资源导入结束后检查是否是节点定义资产。
        // 入参：importedAssets 新导入/修改的资产路径；deletedAssets 删除的路径
        // 返回：无
        // 步骤：
        //   1) 遍历导入与删除的路径，命中 BTNodeDefinitions.asset 就继续
        //   2) 让注册表失效（下次访问重新读 SO）
        //   3) 通知所有已打开的行为树窗口刷新左栏与属性面板
        // 上游：Unity 资源导入管线
        // 下游：BTNodeRegistry.Reload、BehaviorTreeEditorWindow.RefreshAllWindows
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (!ContainsDefinitions(importedAssets) && !ContainsDefinitions(deletedAssets)
                && !ContainsDefinitions(movedAssets))
            {
                return;
            }

            BTNodeRegistry.Reload();
            BehaviorTreeEditorWindow.RefreshAllWindows();
        }

        // 功能：判断路径数组里有没有节点定义资产。
        // 入参：paths 资产路径数组（可能为 null）
        // 返回：true = 命中
        // 步骤：逐个比较（忽略大小写，兼容 Windows）
        // 上游：OnPostprocessAllAssets
        // 下游：无
        private static bool ContainsDefinitions(string[] paths)
        {
            if (paths == null)
            {
                return false;
            }

            for (int i = 0; i < paths.Length; i++)
            {
                if (string.Equals(paths[i], BTNodeRegistry.AssetPath,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
