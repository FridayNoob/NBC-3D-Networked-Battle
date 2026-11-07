// 这个文件里有：
//   BehaviorTreeEditorWindow（类）：行为树编辑器主窗口（菜单入口 + 三栏装配 + 预览驱动 + 导入导出）。
//
// 【编辑器 UI 代码】本文件只做"装配与调度"：画布在 BTGraphView、属性面板在 BTInspectorView、
// 预览运行时在 BTPreviewRunner（仅编辑器）。窗口本身在任何平台都不进包（NBC.Editor 只在编辑器编译）。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace NBC.EditorTools.BehaviorTree
{
    // 主要功能：行为树编辑器窗口 —— 工具栏（导入/导出/预览/停止/删除/复制）、左中右三栏、200ms 预览驱动。
    // 主要数据成员：m_graphView、m_inspector、m_runner、m_blackboardFields、m_lastTick。
    // 主要外部方法：OpenWindow（菜单入口）。
    // 谁在用它：菜单「AI工具/BehaviorTree Editor」。
    // 注意：预览用 EditorApplication.update 驱动（窗口关闭要解绑，否则会空引用）。
    public sealed class BehaviorTreeEditorWindow : EditorWindow
    {
        // 画布
        private BTGraphView m_graphView;

        // 右侧属性面板
        private BTInspectorView m_inspector;

        // 预览运行时（仅编辑器）
        private BTPreviewRunner m_runner;

        // 预览驱动用的上次时间戳
        private double m_lastTickTime;

        // 状态栏
        private Label m_statusLabel;

        // 树名输入框
        private TextField m_treeIdField;

        // 黑板输入框（键 → 控件），用于预览时读值
        private readonly Dictionary<string, VisualElement> m_blackboardFields =
            new Dictionary<string, VisualElement>();

        // 功能：菜单入口。
        // 入参：无
        // 返回：无
        // 步骤：取已有窗口或新建并显示
        // 上游：Unity 菜单
        // 下游：CreateGUI
        [MenuItem("AI工具/BehaviorTree Editor")]
        public static void OpenWindow()
        {
            BehaviorTreeEditorWindow window = GetWindow<BehaviorTreeEditorWindow>();
            window.titleContent = new GUIContent("行为树编辑器");
            window.minSize = new Vector2(1100f, 620f);
            window.Show();
        }

        // 功能：装配整个窗口（UXML + 三栏 + 事件）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 载入 UXML（失败就退化成纯代码布局）
        //   2) 取出各占位容器，建画布/属性面板/节点库/黑板
        //   3) 接工具栏按钮
        //   4) 启动预览驱动（EditorApplication.update）
        // 上游：Unity 创建窗口
        // 下游：BuildPalette / BuildBlackboard / BindToolbar
        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.Clear();

            // 样式表必须加在**窗口根节点**上：只加给 GraphView 的话，工具栏/三栏的 flex 规则全部不生效，
            // 界面会塌成竖排、画布高度变成 0（第一次验收就是这样）。
            StyleSheet sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(BTEditorPaths.StyleSheetPath);

            if (sheet != null && !root.styleSheets.Contains(sheet))
            {
                root.styleSheets.Add(sheet);
            }

            VisualTreeAsset layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(BTEditorPaths.LayoutPath);

            if (layout != null)
            {
                layout.CloneTree(root);
            }
            else
            {
                Debug.LogWarning("[BT] 找不到布局文件 " + BTEditorPaths.LayoutPath + "，用纯代码布局兜底。");
                root.Add(new Label("（找不到 UXML，界面已退化为纯代码布局）"));
            }

            VisualElement canvasSlot = root.Q<VisualElement>("canvas-slot");

            if (canvasSlot == null)
            {
                canvasSlot = new VisualElement();
                root.Add(canvasSlot);
            }

            m_graphView = new BTGraphView();
            m_graphView.Initialize();
            m_graphView.style.flexGrow = 1f;
            canvasSlot.Add(m_graphView);

            VisualElement inspectorSlot = root.Q<VisualElement>("inspector-slot");
            m_inspector = new BTInspectorView();

            if (inspectorSlot != null)
            {
                inspectorSlot.Add(m_inspector);
            }

            m_graphView.NodeSelected += node => m_inspector.Bind(node);

            m_runner = new BTPreviewRunner();
            BuildPalette(root);
            BuildBlackboard(root);
            BindToolbar(root);

            m_lastTickTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += OnEditorUpdate;
            SetStatus("就绪：从左侧拖节点到画布，连线后再点【预览】。");
        }

        // 功能：窗口关闭时解绑预览驱动。
        // 入参：无
        // 返回：无
        // 步骤：解绑 update、停止预览
        // 上游：Unity 销毁窗口
        // 下游：BTPreviewRunner.Stop
        public void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;

            if (m_runner != null)
            {
                m_runner.Stop();
            }
        }

        // 功能：每帧驱动预览（内部按 200ms 限流），并把状态刷到节点上。
        // 入参：无
        // 返回：无
        // 步骤：算 delta → runner.Tick → 真的 Tick 了才刷颜色
        // 上游：EditorApplication.update
        // 下游：BTGraphView.ApplyStatus
        private void OnEditorUpdate()
        {
            if (m_runner == null || m_graphView == null || !m_runner.IsRunning)
            {
                m_lastTickTime = EditorApplication.timeSinceStartup;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            float delta = (float)(now - m_lastTickTime);
            m_lastTickTime = now;

            if (m_runner.Tick(delta))
            {
                m_graphView.ApplyStatus(m_runner.Status);
            }
        }

        // 功能：建左侧节点库（按分类分组，可拖到画布）。
        // 入参：root 根元素
        // 返回：无
        // 步骤：取 palette-slot；按 Category 分组加标题；每个节点一个可拖拽的按钮
        // 上游：CreateGUI
        // 下游：StartPaletteDrag
        private void BuildPalette(VisualElement root)
        {
            VisualElement slot = root.Q<VisualElement>("palette-slot");

            if (slot == null)
            {
                return;
            }

            slot.Clear();

            IReadOnlyList<BTNodeDefine> defines = BTNodeRegistry.All;
            BTNodeCategory currentCategory = (BTNodeCategory)(-1);

            for (int i = 0; i < defines.Count; i++)
            {
                BTNodeDefine define = defines[i];

                if (define.Category != currentCategory)
                {
                    currentCategory = define.Category;
                    Label header = new Label(currentCategory.ToString());
                    header.AddToClassList("bt-palette-header");
                    slot.Add(header);
                }

                Button item = new Button(() => AddNodeAtCenter(define.TypeName))
                {
                    text = define.DisplayName
                };

                item.AddToClassList("bt-palette-item");
                string typeName = define.TypeName;
                item.RegisterCallback<MouseDownEvent>(evt => StartPaletteDrag(item, typeName, evt));
                slot.Add(item);
            }

            Label hint = new Label("把上面的节点拖到画布，或点击在画布中心新建。");
            hint.AddToClassList("bt-inspector-hint");
            slot.Add(hint);
        }

        // 功能：开始拖拽一个节点类型（供画布接收）。
        // 入参：source 拖拽源元素；typeName 节点类型；evt 鼠标事件
        // 返回：无
        // 步骤：PrepareStartDrag → 塞入数据类型 → StartDrag
        // 上游：节点库的 MouseDown
        // 下游：BTGraphView.OnDragUpdated / OnDragPerform
        private void StartPaletteDrag(VisualElement source, string typeName, MouseDownEvent evt)
        {
            if (evt.button != 0)
            {
                return;
            }

            DragAndDrop.PrepareStartDrag();
            DragAndDrop.SetGenericData(BTGraphView.DragDataType, typeName);
            DragAndDrop.objectReferences = new UnityEngine.Object[0];
            DragAndDrop.StartDrag(typeName);
            evt.StopPropagation();
        }

        // 功能：在画布中心创建节点（点击节点库时的兜底操作，不用拖也能加）。
        // 入参：typeName 节点类型
        // 返回：无
        // 步骤：取画布中心坐标 → CreateNode
        // 上游：节点库按钮点击
        // 下游：BTGraphView.CreateNode
        private void AddNodeAtCenter(string typeName)
        {
            if (m_graphView == null)
            {
                return;
            }

            Vector2 center = new Vector2(m_graphView.layout.width * 0.4f, m_graphView.layout.height * 0.4f);
            m_graphView.CreateNode(typeName, center);
            SetStatus("已新建节点：" + typeName);
        }

        // 功能：建预览用黑板（hp / targetId / skillCd 三个常用键）。
        // 入参：root 根元素
        // 返回：无
        // 步骤：取 blackboard-slot；按类型建控件；改动写进 runner.Blackboard
        // 上游：CreateGUI
        // 下游：BTBlackboard.Set
        private void BuildBlackboard(VisualElement root)
        {
            VisualElement slot = root.Q<VisualElement>("blackboard-slot");

            if (slot == null)
            {
                return;
            }

            slot.Clear();
            m_blackboardFields.Clear();

            AddBlackboardInt(slot, "hp", "玩家血量", 100);
            AddBlackboardInt(slot, "targetId", "目标 id（0=没有）", 0);
            AddBlackboardFloat(slot, "skillCd", "技能冷却（秒）", 0f);
        }

        // 功能：加一个整数黑板项。
        // 入参：slot 容器；key 键；label 显示名；defaultValue 默认值
        // 返回：无
        // 步骤：建 IntegerField → 注册回调写黑板 → 加进容器
        // 上游：BuildBlackboard
        // 下游：BTBlackboard.Set
        private void AddBlackboardInt(VisualElement slot, string key, string label, int defaultValue)
        {
            IntegerField field = new IntegerField(label) { value = defaultValue };
            field.RegisterValueChangedCallback(evt =>
            {
                m_runner?.Blackboard.Set(key, BTParamValue.FromInt(evt.newValue));
            });
            slot.Add(field);
            m_blackboardFields[key] = field;
            m_runner?.Blackboard.Set(key, BTParamValue.FromInt(defaultValue));
        }

        // 功能：加一个浮点黑板项。
        // 入参：slot 容器；key 键；label 显示名；defaultValue 默认值
        // 返回：无
        // 步骤：建 FloatField → 注册回调写黑板 → 加进容器
        // 上游：BuildBlackboard
        // 下游：BTBlackboard.Set
        private void AddBlackboardFloat(VisualElement slot, string key, string label, float defaultValue)
        {
            FloatField field = new FloatField(label) { value = defaultValue };
            field.RegisterValueChangedCallback(evt =>
            {
                m_runner?.Blackboard.Set(key, BTParamValue.FromFloat(evt.newValue));
            });
            slot.Add(field);
            m_blackboardFields[key] = field;
            m_runner?.Blackboard.Set(key, BTParamValue.FromFloat(defaultValue));
        }

        // 功能：接好工具栏按钮与树名输入框。
        // 入参：root 根元素
        // 返回：无
        // 步骤：逐个 Q 按钮并注册点击；状态栏初始化
        // 上游：CreateGUI
        // 下游：各种 OnXxx 方法
        private void BindToolbar(VisualElement root)
        {
            m_statusLabel = root.Q<Label>("status");
            m_treeIdField = root.Q<TextField>("tree-id");

            if (m_treeIdField != null)
            {
                m_treeIdField.value = "new_tree";
            }

            root.Q<Button>("btn-import")?.RegisterCallback<ClickEvent>(_ => OnImport());
            root.Q<Button>("btn-export")?.RegisterCallback<ClickEvent>(_ => OnExport());
            root.Q<Button>("btn-preview")?.RegisterCallback<ClickEvent>(_ => OnPreview());
            root.Q<Button>("btn-stop")?.RegisterCallback<ClickEvent>(_ => OnStopPreview());
            root.Q<Button>("btn-delete")?.RegisterCallback<ClickEvent>(_ => OnDelete());
            root.Q<Button>("btn-duplicate")?.RegisterCallback<ClickEvent>(_ => OnDuplicate());
        }

        // 功能：导出当前树为 JSON 文件。
        // 入参：无
        // 返回：无
        // 步骤：取树名 → 导出数据 → 校验 → 选保存路径 → 写文件
        // 上游：工具栏【导出JSON】
        // 下游：BTJsonUtil.Export
        private void OnExport()
        {
            string treeId = m_treeIdField != null ? m_treeIdField.value : "new_tree";
            BTGraphData data = m_graphView.ExportData(treeId);

            if (!data.Validate(out string error))
            {
                SetStatus("❌ 导出失败：" + error);
                Debug.LogWarning("[BT] 导出失败：" + error);
                return;
            }

            string defaultDir = "Assets/BehaviorTrees";

            if (!Directory.Exists(defaultDir))
            {
                Directory.CreateDirectory(defaultDir);
            }

            string path = EditorUtility.SaveFilePanel("导出行为树 JSON", defaultDir, treeId + ".json", "json");

            if (string.IsNullOrEmpty(path))
            {
                SetStatus("已取消导出。");
                return;
            }

            try
            {
                File.WriteAllText(path, BTJsonUtil.Export(data));
                AssetDatabase.Refresh();
                SetStatus("✅ 已导出：" + path);
            }
            catch (Exception exception)
            {
                SetStatus("❌ 导出失败：" + exception.Message);
                Debug.LogException(exception);
            }
        }

        // 功能：从 JSON 文件导入。
        // 入参：无
        // 返回：无
        // 步骤：选文件 → 读文本 → 解析 → 载入画布 → 刷树名与属性面板
        // 上游：工具栏【导入JSON】
        // 下游：BTJsonUtil.Import、BTGraphView.ImportData
        private void OnImport()
        {
            string path = EditorUtility.OpenFilePanel("导入行为树 JSON", "Assets/BehaviorTrees", "json");

            if (string.IsNullOrEmpty(path))
            {
                SetStatus("已取消导入。");
                return;
            }

            try
            {
                string json = File.ReadAllText(path);
                BTGraphData data = BTJsonUtil.Import(json);
                m_graphView.ImportData(data);

                if (m_treeIdField != null)
                {
                    m_treeIdField.value = data.TreeId;
                }

                m_inspector.Bind(null);
                SetStatus("✅ 已导入：" + path);
            }
            catch (BtJsonException exception)
            {
                SetStatus("❌ 导入失败：" + exception.Message);
                Debug.LogWarning("[BT] 导入失败：" + exception.Message);
            }
            catch (Exception exception)
            {
                SetStatus("❌ 导入失败：" + exception.Message);
                Debug.LogException(exception);
            }
        }

        // 功能：开始预览。
        // 入参：无
        // 返回：无
        // 步骤：导出快照 → runner.Start → 失败就提示原因
        // 上游：工具栏【预览】
        // 下游：BTPreviewRunner.Start
        private void OnPreview()
        {
            BTGraphData data = m_graphView.ExportData(m_treeIdField != null ? m_treeIdField.value : "preview");

            if (!m_runner.Start(data, out string error))
            {
                SetStatus("❌ 无法预览：" + error);
                Debug.LogWarning("[BT] 无法预览：" + error);
                return;
            }

            m_lastTickTime = EditorApplication.timeSinceStartup;
            SetStatus("▶ 预览中（200ms 一帧）：绿=成功 红=失败 黄=运行中。改黑板数值看条件变化。");
        }

        // 功能：停止预览并清颜色。
        // 入参：无
        // 返回：无
        // 步骤：runner.Stop → 画布清状态
        // 上游：工具栏【停止预览】
        // 下游：BTGraphView.ClearStatus
        private void OnStopPreview()
        {
            m_runner.Stop();
            m_graphView.ClearStatus();
            SetStatus("已停止预览。");
        }

        // 功能：删除选中节点（根节点会被拒绝，原因打在状态栏）。
        // 入参：无
        // 返回：无
        // 步骤：调画布删除 → 刷属性面板
        // 上游：工具栏【删除节点】
        // 下游：BTGraphView.DeleteSelection
        private void OnDelete()
        {
            m_graphView.DeleteSelection();
            m_inspector.Bind(null);
            SetStatus("已删除选中节点（根节点不会被删除）。");
        }

        // 功能：复制选中节点。
        // 入参：无
        // 返回：无
        // 步骤：调画布复制 → 选中新节点 → 刷属性面板
        // 上游：工具栏【复制节点】
        // 下游：BTGraphView.DuplicateSelection
        private void OnDuplicate()
        {
            BTNodeView copy = m_graphView.DuplicateSelection();

            if (copy == null)
            {
                SetStatus("没有选中可复制的节点。");
                return;
            }

            m_graphView.ClearSelection();
            m_graphView.AddToSelection(copy);
            m_inspector.Bind(copy);
            SetStatus("已复制为 " + copy.NodeId);
        }

        // 功能：更新状态栏文字。
        // 入参：text 文案
        // 返回：无
        // 步骤：有标签就写
        // 上游：各操作
        // 下游：m_statusLabel
        private void SetStatus(string text)
        {
            if (m_statusLabel != null)
            {
                m_statusLabel.text = text;
            }
        }
    }
}
