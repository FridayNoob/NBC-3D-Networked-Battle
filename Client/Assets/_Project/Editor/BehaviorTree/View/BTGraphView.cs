// 这个文件里有：
//   BTGraphView（类）：行为树画布（UI Toolkit / GraphView）。
//
// 【编辑器 UI 代码】画布负责：建节点、连线、删除、复制、以及把画面同步成 BTGraphData。
// 预览运行时是**另一套对象**（BTPreviewRunner），画布只接收它回传的状态来上色。

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace NBC.EditorTools.BehaviorTree
{
    // 主要功能：把 BTGraphData 画出来、把用户操作写回 BTGraphData（唯一的数据源）。
    // 主要数据成员：m_data（树数据）、m_nodeViews（id → 节点视图）、m_initialized（是否装过操作器）。
    // 主要外部方法：ImportData（载入）、ExportData（导出快照）、CreateNode（建节点）、ApplyStatus（预览上色）、ClearStatus。
    // 谁在用它：BehaviorTreeEditorWindow。
    // 注意：连线规则（单父、单子、无环、根无输入）在 OnGraphViewChanged 里**统一校验** —— 别在别处零散地加限制。
    public sealed class BTGraphView : GraphView
    {
        // 拖拽创建节点时用的数据类型标记（节点库 → 画布）
        public const string DragDataType = "NBC_BT_NODE_TYPE";

        // 当前树数据；画布上的一切都同步到它
        private BTGraphData m_data = new BTGraphData();

        // 节点 id → 节点视图
        private readonly Dictionary<string, BTNodeView> m_nodeViews = new Dictionary<string, BTNodeView>();

        // 是否已经装过操作器与样式（构造后只做一次）
        private bool m_initialized;

        // 选中节点变化（窗口用来刷新右侧属性面板）
        public event Action<BTNodeView> NodeSelected;

        // 拖拽连线的起点端口（null = 没在拖）
        private Port m_dragSourcePort;

        // 拖拽时那根预备线的可视元素
        private VisualElement m_dragLine;

        // 预备线的起点与当前点（面板坐标，绘制时再转局部）
        private Vector2 m_dragStart;

        // 预备线的当前端点
        private Vector2 m_dragCurrent;

        // 树数据（只读用；导出请走 ExportData）
        public BTGraphData Data => m_data;

        // 功能：构造画布：装缩放/框选/拖拽操作器、挂样式、监听结构变化与拖放。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 装 ContentDragger / SelectionDragger / RectangleSelector / ContentZoomer
        //   2) 载入 USS（与窗口共用一份）
        //   3) 监听 graphViewChanged（连线/删除的校验入口）
        //   4) 监听拖放事件（从节点库拖进来）
        // 上游：BehaviorTreeEditorWindow.CreateGUI
        // 下游：OnGraphViewChanged / OnDragPerform
        public void Initialize()
        {
            if (m_initialized)
            {
                return;
            }

            m_initialized = true;

            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            this.AddManipulator(new FreehandSelector());

            StyleSheet sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(BTEditorPaths.StyleSheetPath);

            if (sheet != null)
            {
                styleSheets.Add(sheet);
            }

            graphViewChanged += OnGraphViewChanged;

            // 自己实现拖拽连线：GraphView 自带的 EdgeConnector 在「节点 + 自定义端口」组合下不认目标端口，
            // 实测拖出线但落点连不上；这里改成鼠标按下端口 → 移动 → 松开时命中端口/节点即可连。
            // TrickleDown：先于目标元素（端口）收到事件 —— 端口自己的 MouseUp 可能 StopPropagation，
            // 用冒泡阶段就收不到，表现为「松手没反应」。
            RegisterCallback<MouseMoveEvent>(OnConnectDragMove, TrickleDown.TrickleDown);
            RegisterCallback<MouseUpEvent>(OnConnectDragEnd, TrickleDown.TrickleDown);

            RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            RegisterCallback<DragPerformEvent>(OnDragPerform);

            // 选中变化：把当前选中的节点抛给窗口
            RegisterCallback<MouseUpEvent>(_ => NotifySelection());

            // 空白处右键：快速加节点菜单
            RegisterCallback<ContextualMenuPopulateEvent>(OnContextMenu);

            // 背景网格（GraphView 自带 gridBackground）
            Insert(0, new GridBackground());
        }

        // 功能：载入一棵树（导入 JSON 时用）。
        // 入参：data 树数据（内部会接管引用）
        // 返回：无
        // 步骤：
        //   1) 清空画布与字典
        //   2) 逐节点建视图（位置与参数来自数据）
        //   3) 按 Children 连线
        //   4) 统一整理根状态
        // 上游：BehaviorTreeEditorWindow 的导入按钮
        // 下游：CreateNodeView / ConnectFromData
        public void ImportData(BTGraphData data)
        {
            Initialize();
            DeleteElements(graphElements.ToList());
            m_nodeViews.Clear();
            m_data = data ?? new BTGraphData();

            foreach (KeyValuePair<string, BTNodeData> pair in m_data.Nodes)
            {
                BTNodeDefine define = BTNodeRegistry.Find(pair.Value.NodeType);
                BTNodeView view = new BTNodeView(pair.Key, pair.Value, define, false);
                view.SetPosition(new Rect(pair.Value.PosX, pair.Value.PosY, 180f, 100f));
                AddElement(view);
                m_nodeViews[pair.Key] = view;
                HookPorts(view);
            }

            foreach (KeyValuePair<string, BTNodeData> pair in m_data.Nodes)
            {
                BTNodeView parentView = m_nodeViews[pair.Key];

                for (int i = 0; i < pair.Value.Children.Count; i++)
                {
                    if (!m_nodeViews.TryGetValue(pair.Value.Children[i], out BTNodeView childView))
                    {
                        continue;
                    }

                    if (parentView.OutputPort == null || childView.InputPort == null)
                    {
                        continue;
                    }

                    Edge edge = parentView.OutputPort.ConnectTo(childView.InputPort);
                    AddElement(edge);
                }
            }

            RefreshRoots();
            NotifySelection();
        }

        // 功能：把画布导出成数据快照（导出 JSON 时用）。
        // 入参：treeId 树名（为空则用原树名）
        // 返回：树数据（位置/连线/参数都以画面为准）
        // 步骤：
        //   1) 遍历节点视图：写回位置、参数、子节点 id
        //   2) 计算根（没有输入的节点）
        // 上游：BehaviorTreeEditorWindow 的导出按钮
        // 下游：BTJsonUtil.Export
        public BTGraphData ExportData(string treeId)
        {
            BTGraphData result = new BTGraphData
            {
                TreeId = string.IsNullOrEmpty(treeId) ? m_data.TreeId : treeId
            };

            foreach (KeyValuePair<string, BTNodeView> pair in m_nodeViews)
            {
                BTNodeData node = pair.Value.Data;
                node.PosX = pair.Value.GetPosition().x;
                node.PosY = pair.Value.GetPosition().y;
                node.Children.Clear();

                if (pair.Value.OutputPort != null)
                {
                    foreach (Edge edge in pair.Value.OutputPort.connections)
                    {
                        if (edge.input != null && edge.input.node is BTNodeView child)
                        {
                            node.Children.Add(child.NodeId);
                        }
                    }
                }

                result.Nodes[pair.Key] = node;
            }

            result.RootNodeId = result.FindRootId();
            return result;
        }

        // 功能：在指定位置新建一个节点（节点库拖放 / 右键菜单都走它）。
        // 入参：typeName 节点类型；position 画布坐标
        // 返回：新节点视图（类型未注册时返回 null）
        // 步骤：
        //   1) 找定义；没有就报错返回
        //   2) 分配 id
        //   3) 建数据（含默认参数）与视图
        //   4) 入字典、加入画布、整理根状态
        // 上游：OnDragPerform / OnContextMenu
        // 下游：CreateNodeView
        public BTNodeView CreateNode(string typeName, Vector2 position)
        {
            BTNodeDefine define = BTNodeRegistry.Find(typeName);

            if (define == null)
            {
                Debug.LogError("[BT] 未知节点类型：" + typeName);
                return null;
            }

            string id = m_data.NextNodeId();
            BTNodeData data = define.CreateNodeData();
            data.PosX = position.x;
            data.PosY = position.y;

            m_data.Nodes[id] = data;

            BTNodeView view = new BTNodeView(id, data, define, false);
            view.SetPosition(new Rect(position.x, position.y, 180f, 100f));
            AddElement(view);
            m_nodeViews[id] = view;
            HookPorts(view);

            RefreshRoots();
            return view;
        }

        // 功能：给所有节点上色（预览回传状态后调用）。
        // 入参：status 节点 id → 三态
        // 返回：无
        // 步骤：先全部清掉，再按状态设置
        // 上游：BehaviorTreeEditorWindow 的预览 Tick
        // 下游：BTNodeView.SetStatus
        public void ApplyStatus(IReadOnlyDictionary<string, BTPreviewStatus> status)
        {
            foreach (KeyValuePair<string, BTNodeView> pair in m_nodeViews)
            {
                if (status != null && status.TryGetValue(pair.Key, out BTPreviewStatus value))
                {
                    pair.Value.SetStatus(value);
                }
                else
                {
                    pair.Value.SetStatus(null);
                }
            }
        }

        // 功能：清除所有预览上色（停止预览时调用）。
        // 入参：无
        // 返回：无
        // 步骤：逐个 SetStatus(null)
        // 上游：BehaviorTreeEditorWindow 的停止预览
        // 下游：BTNodeView.SetStatus
        public void ClearStatus()
        {
            foreach (KeyValuePair<string, BTNodeView> pair in m_nodeViews)
            {
                pair.Value.SetStatus(null);
            }
        }

        // 功能：取选中的第一个节点（没有就 null）。
        // 入参：无
        // 返回：节点视图
        // 步骤：从 selection 里找 BTNodeView
        // 上游：窗口刷新属性面板
        // 下游：无
        public BTNodeView GetSelectedNode()
        {
            foreach (ISelectable selectable in selection)
            {
                if (selectable is BTNodeView node)
                {
                    return node;
                }
            }

            return null;
        }

        // 功能：删除当前选中的节点（根节点不允许删）。
        // 入参：无
        // 返回：无
        // 步骤：逐个判断是不是根；不是就从画布与数据里移除
        // 上游：窗口的 Delete 按钮 / Delete 键
        // 下游：RemoveNode
        public void DeleteSelection()
        {
            List<BTNodeView> targets = new List<BTNodeView>();

            foreach (ISelectable selectable in selection)
            {
                if (selectable is BTNodeView node)
                {
                    targets.Add(node);
                }
            }

            for (int i = 0; i < targets.Count; i++)
            {
                RemoveNode(targets[i]);
            }
        }

        // 功能：复制当前选中的节点（参数与位置都拷，右上偏移一点）。
        // 入参：无
        // 返回：新节点视图（没有选中返回 null）
        // 步骤：取选中 → 新建同类型节点（参数逐个覆盖）→ 偏移位置
        // 上游：窗口的 Duplicate 按钮 / Ctrl+D
        // 下游：CreateNode
        public BTNodeView DuplicateSelection()
        {
            BTNodeView source = GetSelectedNode();

            if (source == null)
            {
                return null;
            }

            Rect position = source.GetPosition();
            BTNodeView copy = CreateNode(source.Data.NodeType, new Vector2(position.x + 40f, position.y + 40f));

            if (copy == null)
            {
                return null;
            }

            foreach (KeyValuePair<string, BTParamValue> pair in source.Data.Params)
            {
                copy.Data.Params[pair.Key] = pair.Value;
            }

            copy.UpdateSubtitle();
            return copy;
        }

        // 功能：给一个节点的端口挂上「按下开始连线」的钩子（TrickleDown，抢在 GraphView 自带连接器之前）。
        // 入参：view 节点视图
        // 返回：无
        // 步骤：输入/输出端口各挂一个 MouseDown
        // 上游：CreateNode / ImportData
        // 下游：OnConnectDragStart
        private void HookPorts(BTNodeView view)
        {
            if (view.InputPort != null)
            {
                view.InputPort.RegisterCallback<MouseDownEvent>(OnConnectDragStart, TrickleDown.TrickleDown);
            }

            if (view.OutputPort != null)
            {
                view.OutputPort.RegisterCallback<MouseDownEvent>(OnConnectDragStart, TrickleDown.TrickleDown);
            }
        }

        // 功能：在端口上按下左键 → 开始拉线。
        // 入参：evt 鼠标按下事件
        // 返回：无
        // 步骤：记下源端口与起点、建预备线、吃掉事件（不让 GraphView 自带连接器介入）
        // 上游：端口 MouseDown（TrickleDown）
        // 下游：OnConnectDragMove / OnConnectDragEnd
        private void OnConnectDragStart(MouseDownEvent evt)
        {
            if (evt.button != 0 || !(evt.currentTarget is Port port))
            {
                return;
            }

            m_dragSourcePort = port;
            m_dragStart = port.worldBound.center;
            m_dragCurrent = m_dragStart;

            if (m_dragLine == null)
            {
                m_dragLine = new VisualElement();
                m_dragLine.pickingMode = PickingMode.Ignore;
                m_dragLine.style.position = Position.Absolute;
                m_dragLine.style.left = 0f;
                m_dragLine.style.top = 0f;
                m_dragLine.style.right = 0f;
                m_dragLine.style.bottom = 0f;
                m_dragLine.generateVisualContent += OnDrawDragLine;
            }

            contentContainer.Add(m_dragLine);
            m_dragLine.MarkDirtyRepaint();
            evt.StopPropagation();
        }

        // 功能：拖拽移动时刷新预备线。
        // 入参：evt 鼠标移动事件
        // 返回：无
        // 步骤：更新端点并重绘
        // 上游：鼠标移动
        // 下游：OnDrawDragLine
        private void OnConnectDragMove(MouseMoveEvent evt)
        {
            if (m_dragSourcePort == null)
            {
                return;
            }

            m_dragCurrent = evt.mousePosition;
            m_dragLine?.MarkDirtyRepaint();
        }

        // 功能：松开鼠标 → 找落点端口/节点 → 校验 → 连上。
        // 入参：evt 鼠标松开事件
        // 返回：无
        // 步骤：
        //   1) 没有源端口就返回（没在拖）
        //   2) 用 panel.Pick 找落点元素，往上找最近的 Port（找不到就找 BTNodeView 的输入口）
        //   3) 校验方向、自连、成环、单父/单子
        //   4) 连上并清预备线
        // 上游：鼠标松开
        // 下游：CompleteConnection
        private void OnConnectDragEnd(MouseUpEvent evt)
        {
            if (m_dragSourcePort == null)
            {
                return;
            }

            Port source = m_dragSourcePort;
            m_dragSourcePort = null;

            if (m_dragLine != null)
            {
                m_dragLine.RemoveFromHierarchy();
            }

            Port target = FindPortAt(evt.mousePosition);

            if (target == null)
            {
                return;
            }

            CompleteConnection(source, target);
            evt.StopPropagation();
        }

        // 功能：把面板坐标下的落点换算成端口（找不到端口就退而用落点所在节点的输入口）。
        // 入参：panelPosition 鼠标位置（面板坐标）
        // 返回：端口；没有返回 null
        // 步骤：panel.Pick → 沿父链找 Port；退化为找 BTNodeView
        // 上游：OnConnectDragEnd
        // 下游：无
        private Port FindPortAt(Vector2 panelPosition)
        {
            if (panel == null)
            {
                return null;
            }

            VisualElement picked = panel.Pick(panelPosition);
            VisualElement cursor = picked;

            while (cursor != null)
            {
                if (cursor is Port port)
                {
                    return port;
                }

                if (cursor is BTNodeView nodeView)
                {
                    return nodeView.InputPort;
                }

                cursor = cursor.parent;
            }

            return null;
        }

        // 功能：校验并建立一条连线（输出口 → 输入口），同时做单父/单子与成环检查。
        // 入参：source 源端口；target 目标端口
        // 返回：无
        // 步骤：
        //   1) 方向必须是 out → in
        //   2) 不能自己连自己
        //   3) 不能成环（目标是源的祖先）
        //   4) 目标已有父、源已有子 ⇒ 先断开旧的
        //   5) ConnectTo + AddElement
        // 上游：OnConnectDragEnd
        // 下游：BTNodeView 的端口
        private void CompleteConnection(Port source, Port target)
        {
            if (source.direction != Direction.Output || target.direction != Direction.Input)
            {
                // 允许从输入口反向拖到输出口（用户体验更宽容）
                if (source.direction == Direction.Input && target.direction == Direction.Output)
                {
                    Port swap = source;
                    source = target;
                    target = swap;
                }
                else
                {
                    Debug.LogWarning("[BT] 连线必须从输出口连到输入口。");
                    return;
                }
            }

            if (!(source.node is BTNodeView from) || !(target.node is BTNodeView to))
            {
                return;
            }

            if (from == to)
            {
                Debug.LogWarning("[BT] 不能把节点连到自己。");
                return;
            }

            if (IsAncestor(from, to))
            {
                Debug.LogWarning("[BT] 这样连会形成环（" + to.NodeId + " 是 " + from.NodeId + " 的祖先）。");
                return;
            }

            // 单父：目标已连父的，先断
            if (target.connections.Any())
            {
                foreach (Edge existing in target.connections.ToList())
                {
                    existing.input.Disconnect(existing);
                    RemoveElement(existing);
                }
            }

            // 多子还是单子由节点定义决定：组合节点（MaxChildren = -1 或 >1）允许连多个子节点；
            // 只有显式声明 MaxChildren == 1 的节点才「一个输出只留一条」。
            int maxChildren = from.Define == null ? 1 : from.Define.MaxChildren;

            if (maxChildren == 1 && source.connections.Any())
            {
                foreach (Edge existing in source.connections.ToList())
                {
                    existing.output.Disconnect(existing);
                    RemoveElement(existing);
                }
            }

            Edge edge = source.ConnectTo(target);
            AddElement(edge);
            RefreshRoots();
        }

        // 功能：画拖拽时的预备线。
        // 入参：painter 绘制器
        // 返回：无
        // 步骤：把两个面板坐标转成元素局部坐标后画一条黄线
        // 上游：m_dragLine.generateVisualContent
        // 下游：无
        private void OnDrawDragLine(MeshGenerationContext context)
        {
            if (m_dragLine == null)
            {
                return;
            }

            Painter2D painter = context.painter2D;
            Vector2 from = m_dragLine.WorldToLocal(m_dragStart);
            Vector2 to = m_dragLine.WorldToLocal(m_dragCurrent);
            painter.strokeColor = new Color(1f, 0.85f, 0.2f);
            painter.lineWidth = 2f;
            painter.BeginPath();
            painter.MoveTo(from);
            painter.LineTo(to);
            painter.Stroke();
        }
        // 功能：图结构变化的总校验入口（GraphView 的所有增删都会经过这里）。
        // 入参：change 变化描述
        // 返回：修正后的变化（返回 null 表示不应用）
        // 步骤：
        //   1) 过滤要新建的连线：自连、成环、单子（先断旧）一律处理
        //   2) 处理要删除的元素：节点删除时同步清数据；根节点拒删
        // 上游：GraphView 内部
        // 下游：RemoveNode / RefreshRoots
        private GraphViewChange OnGraphViewChanged(GraphViewChange change)
        {
            if (change.edgesToCreate != null && change.edgesToCreate.Count > 0)
            {
                List<Edge> kept = new List<Edge>();

                for (int i = 0; i < change.edgesToCreate.Count; i++)
                {
                    Edge edge = change.edgesToCreate[i];

                    if (!(edge.output?.node is BTNodeView from) || !(edge.input?.node is BTNodeView to))
                    {
                        continue;
                    }

                    if (from == to)
                    {
                        Debug.LogWarning("[BT] 不能把节点连到自己。");
                        continue;
                    }

                    if (IsAncestor(from, to))
                    {
                        Debug.LogWarning("[BT] 这样连会形成环（" + to.NodeId + " 是 " + from.NodeId + " 的祖先）。");
                        continue;
                    }

                    // 单父：目标已经有父连线的，先断掉
                    if (to.InputPort != null && to.InputPort.connections.Any())
                    {
                        foreach (Edge existing in to.InputPort.connections.ToList())
                        {
                            existing.input.Disconnect(existing);
                            RemoveElement(existing);
                        }
                    }

                    // 只有 MaxChildren == 1 的节点才强制「一个输出一条子连线」；组合节点允许多子
                    int maxChildren = from.Define == null ? 1 : from.Define.MaxChildren;

                    if (maxChildren == 1 && from.OutputPort != null && from.OutputPort.connections.Any())
                    {
                        foreach (Edge existing in from.OutputPort.connections.ToList())
                        {
                            existing.output.Disconnect(existing);
                            RemoveElement(existing);
                        }
                    }

                    kept.Add(edge);
                }

                change.edgesToCreate = kept;
            }

            if (change.elementsToRemove != null && change.elementsToRemove.Count > 0)
            {
                List<GraphElement> allowed = new List<GraphElement>();

                for (int i = 0; i < change.elementsToRemove.Count; i++)
                {
                    GraphElement element = change.elementsToRemove[i];

                    if (element is BTNodeView node)
                    {
                        if (IsRootNode(node))
                        {
                            Debug.LogWarning("[BT] 根节点不能删除（先把它连到别的节点下面，或换个根）。");
                            continue;
                        }

                        RemoveNode(node, false);
                        continue;
                    }

                    allowed.Add(element);
                }

                // 节点已经在 RemoveNode 里删掉了，这里只保留非节点元素（连线）
                change.elementsToRemove = allowed.Where(e => !(e is BTNodeView)).ToList();
            }

            RefreshRoots();
            return change;
        }

        // 功能：从画布与数据里移除一个节点（连带它的连线）。
        // 入参：node 节点视图；removeElement 是否由本方法负责从画布移除（GraphView 已移除时传 false）
        // 返回：无
        // 步骤：断所有连线 → 从画布移除 → 从字典与数据移除
        // 上游：DeleteSelection / OnGraphViewChanged
        // 下游：无
        private void RemoveNode(BTNodeView node, bool removeElement = true)
        {
            if (node == null)
            {
                return;
            }

            if (node.InputPort != null)
            {
                foreach (Edge edge in node.InputPort.connections.ToList())
                {
                    edge.input.Disconnect(edge);
                    RemoveElement(edge);
                }
            }

            if (node.OutputPort != null)
            {
                foreach (Edge edge in node.OutputPort.connections.ToList())
                {
                    edge.output.Disconnect(edge);
                    RemoveElement(edge);
                }
            }

            if (removeElement)
            {
                RemoveElement(node);
            }

            m_nodeViews.Remove(node.NodeId);
            m_data.Nodes.Remove(node.NodeId);
        }

        // 功能：判断某节点是不是祖先（用于环检测）。
        // 入参：candidate 候选祖先；node 起点
        // 返回：true = candidate 在 node 的祖先链上
        // 步骤：从 node 顺着输入连线往上走，遇到 candidate 就命中
        // 上游：OnGraphViewChanged
        // 下游：无
        private static bool IsAncestor(BTNodeView candidate, BTNodeView node)
        {
            BTNodeView current = node;

            while (current != null)
            {
                if (current == candidate)
                {
                    return true;
                }

                Edge parentEdge = current.InputPort?.connections.FirstOrDefault();
                current = parentEdge?.output?.node as BTNodeView;
            }

            return false;
        }

        // 功能：判断是否是根（没有任何输入连线）。
        // 入参：node 节点视图
        // 返回：true = 根
        // 步骤：看输入端口有没有连线
        // 上游：OnGraphViewChanged
        // 下游：无
        private static bool IsRootNode(BTNodeView node)
        {
            return node != null && (node.InputPort == null || !node.InputPort.connections.Any());
        }

        // 功能：整理根状态（有输入的节点必须有输入端口；无输入的去掉输入端口）。
        // 入参：无
        // 返回：无
        // 步骤：对每个节点判断 IsRootNode 并 ApplyRootState
        // 上游：所有结构变化之后
        // 下游：BTNodeView.ApplyRootState
        // 注意：需求要求"根节点不能有输入端口"，所以这一步是视觉效果也是规则体现
        private void RefreshRoots()
        {
            List<BTNodeView> roots = new List<BTNodeView>();

            foreach (KeyValuePair<string, BTNodeView> pair in m_nodeViews)
            {
                if (IsRootNode(pair.Value))
                {
                    roots.Add(pair.Value);
                }
            }

            // 只有一个根时才打徽标；多个根说明树还没连好（导出会被 Validate 拦下）
            for (int i = 0; i < roots.Count; i++)
            {
                roots[i].ApplyRootState(roots.Count == 1);
            }

            if (roots.Count > 1)
            {
                Debug.LogWarning("[BT] 当前有 " + roots.Count + " 个根节点（" + roots.Count +
                                 " 个没有父节点）。一棵树只能有一个根，导出前请把多余的连到树上。");
            }
        }

        // 功能：把当前选中的节点通知窗口。
        // 入参：无
        // 返回：无
        // 步骤：取选中节点并触发事件
        // 上游：鼠标抬起
        // 下游：BehaviorTreeEditorWindow.RefreshInspector
        private void NotifySelection()
        {
            NodeSelected?.Invoke(GetSelectedNode());
        }

        // 功能：拖拽进入画布时显示"可放置"光标。
        // 入参：evt 拖拽事件
        // 返回：无
        // 步骤：有我们的数据类型就接受
        // 上游：节点库开始拖拽
        // 下游：无
        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (DragAndDrop.GetGenericData(DragDataType) is string)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                evt.StopPropagation();
            }
        }

        // 功能：在拖放位置创建节点。
        // 入参：evt 拖放事件
        // 返回：无
        // 步骤：读类型名 → 把屏幕坐标转成画布坐标 → CreateNode
        // 上游：节点库拖放
        // 下游：CreateNode
        private void OnDragPerform(DragPerformEvent evt)
        {
            if (!(DragAndDrop.GetGenericData(DragDataType) is string typeName))
            {
                return;
            }

            DragAndDrop.AcceptDrag();
            Vector2 local = this.WorldToLocal(evt.mousePosition);
            CreateNode(typeName, local);
            evt.StopPropagation();
        }

        // 功能：空白处右键弹出"新建节点"菜单。
        // 入参：evt 菜单填充事件
        // 返回：无
        // 步骤：按分类建子菜单，点了就在鼠标位置建节点
        // 上游：右键
        // 下游：CreateNode
        private void OnContextMenu(ContextualMenuPopulateEvent evt)
        {
            Vector2 local = this.WorldToLocal(evt.mousePosition);

            IReadOnlyList<BTNodeDefine> defines = BTNodeRegistry.All;

            for (int i = 0; i < defines.Count; i++)
            {
                string typeName = defines[i].TypeName;
                string label = defines[i].Category + "/" + defines[i].DisplayName;
                evt.menu.AppendAction(label, _ => CreateNode(typeName, local));
            }
        }
    }

    // 编辑器资源路径集中放这里，避免各处写死字符串
    internal static class BTEditorPaths
    {
        // UXML 布局路径
        public const string LayoutPath = "Assets/_Project/Editor/BehaviorTree/Resources/BehaviorTreeEditor.uxml";

        // USS 样式路径
        public const string StyleSheetPath = "Assets/_Project/Editor/BehaviorTree/Resources/BehaviorTreeEditor.uss";
    }
}
