// 这个文件里有：
//   BTNodeView（类）：画布上的一个节点（UI Toolkit / GraphView.Node）。
//
// 【编辑器 UI 代码】本文件只负责"画"，不负责"行为"；数据在 BTNodeData 里。

using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace NBC.EditorTools.BehaviorTree
{
    // 主要功能：把一个 BTNodeData 画成图上的节点 —— 标题、输入/输出端口、预览状态配色。
    // 主要数据成员：NodeId（节点 id）、Data（它对应的数据）、InputPort / OutputPort（端口）。
    // 主要外部方法：SetStatus（预览三色）、UpdateTitle（参数变化后刷新副标题）。
    // 谁在用它：BTGraphView（创建/连线/删除）、BTPreviewRunner（回传状态）。
    // 注意：节点位置存在 GraphView 的 transform 里，导出时再读回来写进 Data.PosX/PosY。
    public sealed class BTNodeView : Node
    {
        // 该节点的 id（与 BTGraphData.Nodes 的键一致）
        public string NodeId { get; }

        // 该节点的数据（参数、类型、子节点）；画布操作直接改它，保证"所见即数据"
        public BTNodeData Data { get; }

        // 节点定义（决定端口数量与副标题）
        public BTNodeDefine Define { get; }

        // 输入端口；根节点为 null（需求：根节点不能有输入端口）
        public Port InputPort { get; private set; }

        // 输出端口；叶子节点（条件/动作）为 null
        public Port OutputPort { get; private set; }

        // 标题下面那行小字（显示参数摘要）
        private readonly Label m_subtitle;

        // 「根」徽标：标出当前这棵树的根节点（没有父节点的那个）
        private readonly Label m_rootBadge;

        // 功能：构造节点视图。
        // 入参：nodeId 节点 id；data 节点数据；define 节点定义；isRoot 是否作为根创建
        // 返回：无
        // 步骤：
        //   1) 设标题与样式类
        //   2) 建端口（根不要输入端口；叶子不要输出端口）
        //   3) 建副标题并刷新
        // 上游：BTGraphView.CreateNodeView / ImportData
        // 下游：无
        public BTNodeView(string nodeId, BTNodeData data, BTNodeDefine define, bool isRoot)
        {
            NodeId = nodeId;
            Data = data;
            Define = define;

            title = define != null ? define.DisplayName : data.NodeType;
            AddToClassList("bt-node");
            AddToClassList("bt-node-" + (define != null ? define.Category.ToString().ToLowerInvariant() : "action"));

            // 输入端口：**始终创建**。节点刚拖进来时没有父节点，如果这时就把端口删掉，用户根本没法连线。
            // 「只能有一个根」改为：用标题上的「根」徽标标出当前根 + 导出前校验根的数量（见 RefreshRoots）。
            InputPort = Port.Create<Edge>(Orientation.Horizontal, Direction.Input, Port.Capacity.Single,
                typeof(bool));
            InputPort.portName = "in";
            inputContainer.Add(InputPort);

            // 输出端口：只有组合节点才有子节点
            if (define == null || define.MaxChildren != 0)
            {
                OutputPort = Port.Create<Edge>(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi,
                    typeof(bool));
                OutputPort.portName = "out";
                outputContainer.Add(OutputPort);
            }

            m_subtitle = new Label(string.Empty);
            m_subtitle.AddToClassList("bt-node-subtitle");
            titleContainer.Add(m_subtitle);

            m_rootBadge = new Label("根");
            m_rootBadge.AddToClassList("bt-root-badge");
            m_rootBadge.style.display = DisplayStyle.None;
            titleContainer.Add(m_rootBadge);

            UpdateSubtitle();
            RefreshExpandedState();
            RefreshPorts();
        }

        // 功能：把节点标成根/非根（只改「根」徽标）。
        // 入参：isRoot 是否是根
        // 返回：无
        // 步骤：显示或隐藏标题旁的「根」徽标
        // 上游：BTGraphView.ImportData / 唯一根校验后
        // 下游：无
        public void ApplyRootState(bool isRoot)
        {
            // 只改徽标，**不动端口** —— 端口常驻，否则节点没连上之前就没法连。
            if (m_rootBadge == null)
            {
                return;
            }

            m_rootBadge.style.display = isRoot ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // 功能：设置预览状态配色。
        // 入参：status 三态；null = 清除（停止预览）
        // 返回：无
        // 步骤：先清三个样式类，再按状态加一个
        // 上游：BehaviorTreeEditorWindow 的预览刷新
        // 下游：无
        // 注意：只改样式类，**不改数据**（预览与画布分离）
        public void SetStatus(BTPreviewStatus? status)
        {
            RemoveFromClassList("bt-status-success");
            RemoveFromClassList("bt-status-failure");
            RemoveFromClassList("bt-status-running");

            if (status == null)
            {
                return;
            }

            switch (status.Value)
            {
                case BTPreviewStatus.Success:
                    AddToClassList("bt-status-success");
                    break;

                case BTPreviewStatus.Failure:
                    AddToClassList("bt-status-failure");
                    break;

                default:
                    AddToClassList("bt-status-running");
                    break;
            }
        }

        // 功能：刷新标题下的参数摘要。
        // 入参：无
        // 返回：无
        // 步骤：按定义顺序拼「键=值」，最多 3 项
        // 上游：BTInspectorView 改参数后
        // 下游：m_subtitle
        public void UpdateSubtitle()
        {
            if (m_subtitle == null || Define == null)
            {
                return;
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            int shown = 0;

            for (int i = 0; i < Define.Params.Length && shown < 3; i++)
            {
                BTParamDefine param = Define.Params[i];

                if (!Data.Params.TryGetValue(param.Key, out BTParamValue value))
                {
                    continue;
                }

                if (shown > 0)
                {
                    builder.Append("  ");
                }

                builder.Append(param.Key).Append('=').Append(value.AsString());
                shown++;
            }

            m_subtitle.text = builder.ToString();
        }
    }
}
