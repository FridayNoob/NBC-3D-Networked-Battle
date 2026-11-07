// 这个文件里有：
//   BTNodeData（类）：一个节点的纯数据（类型、画布位置、参数、子节点 id 列表）。
//   BTGraphData（类）：整棵树的数据（TreeId / RootNodeId / Nodes）+ 校验。

using System;
using System.Collections.Generic;
using System.Text;

namespace NBC.EditorTools.BehaviorTree
{
    // 主要功能：描述「画布上的一个节点」而不含任何 UI 对象 —— 这是导出 JSON 与预览运行时共用的唯一模型。
    // 主要数据成员：NodeType（类型名）、PosX/PosY（画布位置）、Params（参数表）、Children（有序子节点 id）。
    // 主要外部方法：Clone（复制节点时用）。
    // 谁在用它：BTGraphView（增删改）、BTInspectorView（改参数）、BTJsonUtil（读写）、BTPreviewRunner（快照）。
    // 注意：Params 用字典而不是字段列表 —— 节点类型是运行时才知道的，参数集随类型变化。
    public sealed class BTNodeData
    {
        // 节点类型名，必须能在 BTNodeRegistry 里找到（同时是 JSON 里的 NodeType 字符串）
        public string NodeType = string.Empty;

        // 画布上的 X 位置（GraphView 的坐标系，单位：像素）
        public float PosX;

        // 画布上的 Y 位置（GraphView 的坐标系，单位：像素）
        public float PosY;

        // 参数表：键是参数定义里的 Key，值是带类型标签的值
        public Dictionary<string, BTParamValue> Params = new Dictionary<string, BTParamValue>();

        // 子节点 id 列表；**顺序有意义**（Selector/Sequence 按顺序跑），叶子为空表
        public List<string> Children = new List<string>();

        // 功能：深拷贝一个节点（复制节点用；避免两个节点共享同一个参数字典）。
        // 入参：无
        // 返回：新节点，字段逐项复制
        // 步骤：新建对象 → 复制基础字段 → 逐个复制参数（值类型直接赋） → 复制子节点 id
        // 上游：BTGraphView.DuplicateSelection
        // 下游：无
        public BTNodeData Clone()
        {
            BTNodeData copy = new BTNodeData
            {
                NodeType = NodeType,
                PosX = PosX,
                PosY = PosY
            };

            foreach (KeyValuePair<string, BTParamValue> pair in Params)
            {
                copy.Params[pair.Key] = pair.Value;
            }

            copy.Children.AddRange(Children);
            return copy;
        }

        // 功能：按节点定义补齐缺失的参数（导入旧 JSON、或节点定义新增参数时用）。
        // 入参：define 节点定义（可为 null，null 时什么都不做）
        // 返回：无
        // 步骤：遍历定义里的参数，字典里没有就填默认值
        // 上游：BTGraphView.CreateNode / BTJsonUtil.Import
        // 下游：Params
        // 注意：只补不覆盖 —— 已有值更可信（人是手调过的）
        public void EnsureDefaults(BTNodeDefine define)
        {
            if (define == null)
            {
                return;
            }

            for (int i = 0; i < define.Params.Length; i++)
            {
                BTParamDefine param = define.Params[i];

                if (!Params.ContainsKey(param.Key))
                {
                    Params[param.Key] = param.CreateDefault();
                }
            }
        }
    }

    // 主要功能：装一整棵树的数据，并提供「找根 / 校验（唯一根、环、悬空引用）」。
    // 主要数据成员：TreeId、RootNodeId、Nodes（id → 节点）。
    // 主要外部方法：FindRootId、Validate、Clone、NextNodeId。
    // 谁在用它：BTGraphView、BTJsonUtil、BTPreviewRunner。
    // 注意：RootNodeId 是**导出时计算出来的**；画布上允许暂时为 0 个根（还没连），导出前必须校验。
    public sealed class BTGraphData
    {
        // 树的名字；导出 JSON 的 TreeId
        public string TreeId = "new_tree";

        // 根节点 id；空表示"还没确定根"（由 FindRootId 推出来）
        public string RootNodeId = string.Empty;

        // 所有节点：键是节点 id（n1、n2…），值是该节点的数据
        public Dictionary<string, BTNodeData> Nodes = new Dictionary<string, BTNodeData>();

        // 功能：找出根节点 id —— 即「没有被任何节点当作子节点」的那个。
        // 入参：无
        // 返回：根 id；没有或有多个都返回空串（交给 Validate 报错）
        // 步骤：
        //   1) 把所有 Children 里的 id 收进一个集合
        //   2) 遍历 Nodes，找不在集合里的节点
        //   3) 恰好一个才是根
        // 上游：ExportData / Validate
        // 下游：无
        public string FindRootId()
        {
            List<string> roots = FindRootIds();
            return roots.Count == 1 ? roots[0] : string.Empty;
        }

        // 功能：列出所有根节点 id（没有父节点引用的那些）。
        // 入参：无
        // 返回：根 id 列表（可能是 0 个、1 个或多个）
        // 步骤：
        //   1) 把所有 Children 里的 id 收进集合
        //   2) 遍历 Nodes，不在集合里的就是根
        // 上游：FindRootId / Validate（报错要列出是哪几个）
        // 下游：无
        public List<string> FindRootIds()
        {
            HashSet<string> referenced = new HashSet<string>();

            foreach (KeyValuePair<string, BTNodeData> pair in Nodes)
            {
                for (int i = 0; i < pair.Value.Children.Count; i++)
                {
                    referenced.Add(pair.Value.Children[i]);
                }
            }

            List<string> roots = new List<string>();

            foreach (KeyValuePair<string, BTNodeData> pair in Nodes)
            {
                if (!referenced.Contains(pair.Key))
                {
                    roots.Add(pair.Key);
                }
            }

            return roots;
        }

        // 功能：生成下一个可用的节点 id（n1、n2…）。
        // 入参：无
        // 返回：没被占用的 id
        // 步骤：从 1 开始逐个试探，直到 Nodes 里没有
        // 上游：BTGraphView.CreateNode / Duplicate
        // 下游：Nodes
        public string NextNodeId()
        {
            int index = 1;

            while (true)
            {
                string candidate = "n" + index;

                if (!Nodes.ContainsKey(candidate))
                {
                    return candidate;
                }

                index++;
            }
        }

        // 功能：导出前的结构校验。
        // 入参：error 失败原因（成功时为空串）
        // 返回：true = 可以导出
        // 步骤：
        //   1) 空树直接失败
        //   2) 必须恰好一个根
        //   3) 所有 Children 引用必须存在
        //   4) 从根做一次 DFS，必须能到达所有节点（否则有环或被孤立的环）
        // 上游：BehaviorTreeEditorWindow 导出按钮
        // 下游：无
        public bool Validate(out string error)
        {
            if (Nodes.Count == 0)
            {
                error = "画布是空的，先拖一个节点进来。";
                return false;
            }

            string root = FindRootId();

            if (string.IsNullOrEmpty(root))
            {
                List<string> roots = FindRootIds();
                StringBuilder list = new StringBuilder();

                for (int i = 0; i < roots.Count; i++)
                {
                    if (i > 0) { list.Append(", "); }
                    list.Append(roots[i]).Append('(').Append(Nodes[roots[i]].NodeType).Append(')');
                }

                error = "必须恰好有一个根节点，当前有 " + roots.Count + " 个：" + list +
                        "。把多余的节点连到树上，或删掉它们。";
                return false;
            }

            foreach (KeyValuePair<string, BTNodeData> pair in Nodes)
            {
                for (int i = 0; i < pair.Value.Children.Count; i++)
                {
                    string child = pair.Value.Children[i];

                    if (!Nodes.ContainsKey(child))
                    {
                        error = "节点 " + pair.Key + " 引用了不存在的子节点 " + child + "。";
                        return false;
                    }
                }
            }

            HashSet<string> visited = new HashSet<string>();
            Stack<string> pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                if (!visited.Add(current))
                {
                    error = "检测到环：节点 " + current + " 被重复访问。行为树必须是有向无环图。";
                    return false;
                }

                BTNodeData node = Nodes[current];

                for (int i = 0; i < node.Children.Count; i++)
                {
                    pending.Push(node.Children[i]);
                }
            }

            if (visited.Count != Nodes.Count)
            {
                StringBuilder missing = new StringBuilder();

                foreach (KeyValuePair<string, BTNodeData> pair in Nodes)
                {
                    if (!visited.Contains(pair.Key))
                    {
                        missing.Append(pair.Key).Append(' ');
                    }
                }

                error = "有节点无法从根到达（被孤立的子图或环）：" + missing;
                return false;
            }

            error = string.Empty;
            return true;
        }

        // 功能：深拷贝整棵树（预览运行时用它做快照，避免与画布共享数据）。
        // 入参：无
        // 返回：新树
        // 步骤：复制标量 → 逐节点 Clone
        // 上游：BTPreviewRunner.Start
        // 下游：无
        public BTGraphData Clone()
        {
            BTGraphData copy = new BTGraphData
            {
                TreeId = TreeId,
                RootNodeId = RootNodeId
            };

            foreach (KeyValuePair<string, BTNodeData> pair in Nodes)
            {
                copy.Nodes[pair.Key] = pair.Value.Clone();
            }

            return copy;
        }
    }
}
