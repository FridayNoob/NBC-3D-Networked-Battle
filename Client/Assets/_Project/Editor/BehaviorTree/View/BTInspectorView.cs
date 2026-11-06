// 这个文件里有：
//   BTInspectorView（类）：右侧属性面板（按节点定义动态生成控件）。
//
// 【编辑器 UI 代码】只负责把数值显示出来、把用户改动写回 BTNodeData。

using System.Collections.Generic;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace NBC.EditorTools.BehaviorTree
{
    // 主要功能：选中一个节点后，按它的 BTNodeDefine.Params 生成输入控件（float/int/bool/string），改值实时写回数据。
    // 主要数据成员：m_node（当前绑定的节点视图）、m_slot（放控件的容器）。
    // 主要外部方法：Bind（换绑定的节点）。
    // 谁在用它：BehaviorTreeEditorWindow（选中变化时调 Bind）。
    // 注意：控件是"每次重建"的（节点类型不同参数就不同）；这点开销在编辑器里可以忽略。
    public sealed class BTInspectorView : VisualElement
    {
        // 当前绑定的节点（null = 没选中）
        private BTNodeView m_node;

        // 控件容器（每次 Bind 清空重建）
        private readonly VisualElement m_slot;

        // 功能：构造面板。
        // 入参：无
        // 返回：无
        // 步骤：建容器、加入自身、显示占位提示
        // 上游：BehaviorTreeEditorWindow.CreateGUI
        // 下游：Bind
        public BTInspectorView()
        {
            m_slot = new VisualElement();
            m_slot.AddToClassList("bt-inspector");
            Add(m_slot);
            ShowPlaceholder();
        }

        // 功能：绑定一个节点并重建控件。
        // 入参：node 节点视图（null = 清空）
        // 返回：无
        // 步骤：
        //   1) 记住节点
        //   2) 清空容器
        //   3) 没有节点或没有定义 ⇒ 占位提示
        //   4) 否则逐参数建控件（按类型）
        // 上游：窗口的 NodeSelected 回调
        // 下游：AddField
        public void Bind(BTNodeView node)
        {
            m_node = node;
            m_slot.Clear();

            if (node == null || node.Define == null)
            {
                ShowPlaceholder();
                return;
            }

            Label header = new Label(node.NodeId + "  ·  " + node.Data.NodeType);
            header.AddToClassList("bt-inspector-title");
            m_slot.Add(header);

            if (!string.IsNullOrEmpty(node.Data.NodeType))
            {
                Label idLabel = new Label("节点 id：" + node.NodeId);
                idLabel.AddToClassList("bt-inspector-hint");
                m_slot.Add(idLabel);
            }

            if (node.Define.Params.Length == 0)
            {
                Label none = new Label("该节点没有可配置参数。");
                none.AddToClassList("bt-inspector-hint");
                m_slot.Add(none);
                return;
            }

            for (int i = 0; i < node.Define.Params.Length; i++)
            {
                AddField(node, node.Define.Params[i]);
            }
        }

        // 功能：按参数类型建一个控件并接好"改值即写回"。
        // 入参：node 节点视图；param 参数定义
        // 返回：无
        // 步骤：取当前值 → 建对应控件 → 注册回调写回数据并刷新副标题
        // 上游：Bind
        // 下游：BTNodeData.Params
        private void AddField(BTNodeView node, BTParamDefine param)
        {
            BTParamValue current = node.Data.Params.TryGetValue(param.Key, out BTParamValue value)
                ? value
                : param.CreateDefault();

            switch (param.Type)
            {
                case BTParamType.Int:
                {
                    IntegerField field = new IntegerField(param.Label) { value = current.AsInt() };
                    field.RegisterValueChangedCallback(evt =>
                    {
                        node.Data.Params[param.Key] = BTParamValue.FromInt(evt.newValue);
                        node.UpdateSubtitle();
                    });
                    m_slot.Add(field);
                    break;
                }

                case BTParamType.Bool:
                {
                    Toggle field = new Toggle(param.Label) { value = current.AsBool() };
                    field.RegisterValueChangedCallback(evt =>
                    {
                        node.Data.Params[param.Key] = BTParamValue.FromBool(evt.newValue);
                        node.UpdateSubtitle();
                    });
                    m_slot.Add(field);
                    break;
                }

                case BTParamType.String:
                {
                    TextField field = new TextField(param.Label) { value = current.AsString() };
                    field.RegisterValueChangedCallback(evt =>
                    {
                        node.Data.Params[param.Key] = BTParamValue.FromString(evt.newValue);
                        node.UpdateSubtitle();
                    });
                    m_slot.Add(field);
                    break;
                }

                default:
                {
                    FloatField field = new FloatField(param.Label) { value = current.AsFloat() };
                    field.RegisterValueChangedCallback(evt =>
                    {
                        node.Data.Params[param.Key] = BTParamValue.FromFloat(evt.newValue);
                        node.UpdateSubtitle();
                    });
                    m_slot.Add(field);
                    break;
                }
            }
        }

        // 功能：显示"没选中节点"的占位提示。
        // 入参：无
        // 返回：无
        // 步骤：清空后加一行灰字
        // 上游：构造 / Bind(null)
        // 下游：无
        private void ShowPlaceholder()
        {
            m_slot.Clear();
            Label hint = new Label("选中画布上的一个节点，这里会显示它的参数。");
            hint.AddToClassList("bt-inspector-hint");
            m_slot.Add(hint);
        }
    }
}
