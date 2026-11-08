// 这个文件里有：
//   BTNodeRegistry（静态类）：从 ScriptableObject 加载节点定义、校验、对外提供查询。
//
// 节点定义的唯一权威在这里：左栏、属性面板、端口规则、预览配方都通过它取。

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools.BehaviorTree
{
    // 主要功能：把 SO 里的节点定义读成 BTNodeDefine 并缓存；加载失败时抛带路径的异常（不静默空表）。
    // 主要数据成员：s_defines（类型名 → 定义）、s_order（保持顺序）、s_loaded（是否已加载）。
    // 主要外部方法：All / Find / Reload。
    // 谁在用它：BTGraphView、BTInspectorView、BTPreviewRunner、BehaviorTreeEditorWindow。
    // 注意：定义在 SO 里，改定义不需要改代码；但 TypeName 改名会破坏已导出的 JSON。
    public static class BTNodeRegistry
    {
        // 节点定义资产路径（固定位置；策划在这里编辑）
        public const string AssetPath = "Assets/_Project/Editor/BehaviorTree/Resources/BTNodeDefinitions.asset";

        // 类型名 → 定义
        private static readonly Dictionary<string, BTNodeDefine> s_defines =
            new Dictionary<string, BTNodeDefine>(StringComparer.Ordinal);

        // 按 SO 里的顺序保存（左栏显示顺序）
        private static readonly List<BTNodeDefine> s_order = new List<BTNodeDefine>();

        // 是否已加载过
        private static bool s_loaded;

        // 全部定义（只读，按 SO 顺序）
        public static IReadOnlyList<BTNodeDefine> All
        {
            get
            {
                EnsureLoaded();
                return s_order;
            }
        }

        // 功能：提供「该节点定义的 SO 条目」（预览配方要用）。
        // 入参：typeName 类型名
        // 返回：SO 条目；找不到返回 null
        // 步骤：查缓存里的映射
        // 上游：BTPreviewRunner
        // 下游：无
        // 注意：BTNodeDefine 是运行时值对象，SO 条目只用于预览配方；两者由 Reload 同步。
        public static BTNodeEntry FindEntry(string typeName)
        {
            EnsureLoaded();

            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            s_entries.TryGetValue(typeName, out BTNodeEntry entry);
            return entry;
        }

        // 类型名 → SO 条目（与 s_defines 同步维护）
        private static readonly Dictionary<string, BTNodeEntry> s_entries =
            new Dictionary<string, BTNodeEntry>(StringComparer.Ordinal);

        // 功能：按类型名取定义。
        // 入参：typeName 类型名
        // 返回：定义；找不到返回 null
        // 步骤：确保已加载 → 查字典
        // 上游：画布 / 属性面板 / 导入
        // 下游：无
        public static BTNodeDefine Find(string typeName)
        {
            EnsureLoaded();

            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            s_defines.TryGetValue(typeName, out BTNodeDefine define);
            return define;
        }

        // 功能：强制重新加载（改 SO 后、域重载后调用）。
        // 入参：无
        // 返回：无
        // 步骤：清缓存 → 置未加载标记（下次访问重新读）
        // 上游：AssetPostprocessor / 窗口 OnEnable
        // 下游：EnsureLoaded
        public static void Reload()
        {
            s_defines.Clear();
            s_entries.Clear();
            s_order.Clear();
            s_loaded = false;
        }

        // 功能：懒加载 SO 并做校验。
        // 入参：无
        // 返回：无；加载失败抛 InvalidOperationException（消息含资产路径）
        // 步骤：
        //   1) 读 SO；不存在 → 抛错并给出期望路径
        //   2) 遍历条目：TypeName 非空、不重复、MaxChildren 合法、参数键非空且不重复
        //   3) 预习配方校验：CompareParamKey / WriteBackParamKey 必须存在于参数表
        //   4) 建 BTNodeDefine 值对象并缓存
        // 上游：All / Find / FindEntry
        // 下游：无
        private static void EnsureLoaded()
        {
            if (s_loaded)
            {
                return;
            }

            s_loaded = true;

            BTNodeDefinitionAsset asset = AssetDatabase.LoadAssetAtPath<BTNodeDefinitionAsset>(AssetPath);

            if (asset == null)
            {
                throw new InvalidOperationException(
                    "[BT] 找不到节点定义资产：\n  " + AssetPath +
                    "\n请在 Project 里右键 → Create → AI工具/行为树节点定义，创建在该路径下（或用菜单自动创建）。");
            }

            if (asset.Nodes == null || asset.Nodes.Count == 0)
            {
                throw new InvalidOperationException("[BT] 节点定义资产里没有任何节点：" + AssetPath);
            }

            for (int i = 0; i < asset.Nodes.Count; i++)
            {
                BTNodeEntry entry = asset.Nodes[i];
                ValidateEntry(entry, i, asset.Nodes);

                BTNodeDefine define = ToDefine(entry);
                s_defines[define.TypeName] = define;
                s_entries[define.TypeName] = entry;
                s_order.Add(define);
            }
        }

        // 功能：校验单个条目（抛错时带条目序号与类型名）。
        // 入参：entry 条目；index 序号；all 全部条目（查重名）
        // 返回：无；非法抛 InvalidOperationException
        // 步骤：逐项检查类型名、重复、子节点上限、参数键、引用键
        // 上游：EnsureLoaded
        // 下游：无
        private static void ValidateEntry(BTNodeEntry entry, int index, List<BTNodeEntry> all)
        {
            if (entry == null)
            {
                throw new InvalidOperationException("[BT] 第 " + index + " 个节点定义是空的（null）。");
            }

            if (string.IsNullOrEmpty(entry.TypeName))
            {
                throw new InvalidOperationException("[BT] 第 " + index + " 个节点定义缺少 TypeName。");
            }

            for (int i = 0; i < index; i++)
            {
                if (all[i] != null && all[i].TypeName == entry.TypeName)
                {
                    throw new InvalidOperationException("[BT] 节点类型重复：" + entry.TypeName + "（第 " + index + " 个与第 " + i + " 个）。");
                }
            }

            if (entry.MaxChildren < -1)
            {
                throw new InvalidOperationException("[BT] 节点 " + entry.TypeName + " 的 MaxChildren 非法（只能 -1 或 >= 0）。");
            }

            if (entry.Params == null)
            {
                entry.Params = new List<BTParamEntry>();
            }

            for (int i = 0; i < entry.Params.Count; i++)
            {
                BTParamEntry param = entry.Params[i];

                if (param == null || string.IsNullOrEmpty(param.Key))
                {
                    throw new InvalidOperationException("[BT] 节点 " + entry.TypeName + " 的第 " + i + " 个参数缺少 Key。");
                }

                for (int j = 0; j < i; j++)
                {
                    if (entry.Params[j] != null && entry.Params[j].Key == param.Key)
                    {
                        throw new InvalidOperationException("[BT] 节点 " + entry.TypeName + " 的参数键重复：" + param.Key);
                    }
                }
            }

            ValidateParamReference(entry, entry.CompareParamKey);
            ValidateParamReference(entry, entry.WriteBackParamKey);
        }

        // 功能：校验「引用了某个参数键」的字段确实存在于参数表。
        // 入参：entry 条目；key 引用的参数键（空 = 不检查）
        // 返回：无；不存在抛异常
        // 步骤：空直接返回；否则在 Params 里找
        // 上游：ValidateEntry
        // 下游：无
        private static void ValidateParamReference(BTNodeEntry entry, string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            for (int i = 0; i < entry.Params.Count; i++)
            {
                if (entry.Params[i] != null && entry.Params[i].Key == key)
                {
                    return;
                }
            }

            throw new InvalidOperationException(
                "[BT] 节点 " + entry.TypeName + " 引用了不存在的参数键：" + key + "（预览配方配置错误）。");
        }

        // 功能：把 SO 条目转成运行时值对象。
        // 入参：entry SO 条目
        // 返回：BTNodeDefine
        // 步骤：复制基础字段 + 参数表（类型与默认值原样带过去）
        // 上游：EnsureLoaded
        // 下游：无
        private static BTNodeDefine ToDefine(BTNodeEntry entry)
        {
            BTParamDefine[] parameters = new BTParamDefine[entry.Params.Count];

            for (int i = 0; i < entry.Params.Count; i++)
            {
                BTParamEntry param = entry.Params[i];
                parameters[i] = new BTParamDefine
                {
                    Key = param.Key,
                    Label = param.Label,
                    Type = param.Type,
                    DefaultValue = param.Default
                };
            }

            return new BTNodeDefine
            {
                TypeName = entry.TypeName,
                DisplayName = entry.DisplayName,
                Category = entry.Category,
                MaxChildren = entry.MaxChildren,
                Params = parameters
            };
        }
    }
}
