// 这个文件里有：
//   BTNodeCategory（枚举）：节点分类（组合 / 条件 / 动作），决定左栏分组。
//   BTParamDefine（结构体）：一个参数的定义（键、显示名、类型、默认值）。
//   BTNodeDefine（类）：一种节点的运行时值对象（从 SO 定义映射而来）。
//
// 注意：节点定义的**数据源是 BTNodeDefinitionAsset（SO）**，由 BTNodeRegistry 加载；
//       本文件只保留"值对象"与"按定义造节点数据"这两件事，不再硬编码任何节点类型。

using System.Globalization;

namespace NBC.EditorTools.BehaviorTree
{
    // 节点分类：左栏按这个分组，也用于快速判断端口规则
    public enum BTNodeCategory
    {
        // 组合节点（有子节点）：Selector / Sequence / Parallel
        Composite = 0,

        // 条件节点（无子节点）：IsDead / HasTarget / InAttackRange / AttackReady / HPBelow / HomeFar
        Condition = 1,

        // 动作节点（无子节点）：Chase / BasicAttack / CastSkill / ReturnHome / Patrol / Idle / Wait
        Action = 2
    }

    // 一个参数的定义：告诉属性面板「出什么控件」、导出时「什么类型」
    public struct BTParamDefine
    {
        // 参数键（JSON 里 Params 的键）
        public string Key;

        // 属性面板上显示的名字（可以带中文说明）
        public string Label;

        // 参数类型：决定控件与 JSON 原生类型
        public BTParamType Type;

        // 默认值的字符串形式（新建节点时用它初始化）
        public string DefaultValue;

        // 功能：按定义造出默认参数值。
        // 入参：无
        // 返回：带类型的参数值
        // 步骤：按 Type 解析 DefaultValue，解析失败就用类型零值
        // 上游：BTNodeData.EnsureDefaults、BTGraphView.CreateNode
        // 下游：无
        public BTParamValue CreateDefault()
        {
            switch (Type)
            {
                case BTParamType.Int:
                    int.TryParse(DefaultValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue);
                    return BTParamValue.FromInt(intValue);

                case BTParamType.Bool:
                    bool.TryParse(DefaultValue, out bool boolValue);
                    return BTParamValue.FromBool(boolValue);

                case BTParamType.String:
                    return BTParamValue.FromString(DefaultValue ?? string.Empty);

                default:
                    float.TryParse(DefaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue);
                    return BTParamValue.FromFloat(floatValue);
            }
        }
    }

    // 主要功能：描述一种节点「长什么样、能配什么参数」，左栏与属性面板都靠它自动生成。
    // 主要数据成员：TypeName / DisplayName / Category / MaxChildren / Params。
    // 主要外部方法：CreateNodeData（造带默认参数的节点数据）。
    // 谁在用它：BTNodeRegistry（产出）、BTNodePalette、BTInspectorView、BTGraphView（端口规则）。
    // 注意：TypeName 就是 JSON 里的 NodeType，**改动会破坏已导出的树**，别随手改名。
    public sealed class BTNodeDefine
    {
        // 类型名（JSON 的 NodeType）；服务端按这个字符串找节点工厂
        public string TypeName = string.Empty;

        // 左栏/节点标题显示的名字
        public string DisplayName = string.Empty;

        // 分类（决定左栏分组与节点配色）
        public BTNodeCategory Category = BTNodeCategory.Action;

        // 最多能连几个子节点：-1 = 不限（组合节点），0 = 叶子（条件/动作）
        public int MaxChildren = -1;

        // 参数定义表；顺序即属性面板显示顺序
        public BTParamDefine[] Params = new BTParamDefine[0];

        // 功能：造一个带默认参数的节点数据。
        // 入参：无
        // 返回：新节点（参数已按定义填好）
        // 步骤：建节点 → 逐个参数填默认值
        // 上游：BTGraphView.CreateNode
        // 下游：BTNodeData.EnsureDefaults（幂等）
        public BTNodeData CreateNodeData()
        {
            BTNodeData data = new BTNodeData { NodeType = TypeName };
            data.EnsureDefaults(this);
            return data;
        }
    }
}
