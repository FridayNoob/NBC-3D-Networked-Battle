// 这个文件里有：
//   BTNodeCategory（枚举）：节点分类（组合 / 条件 / 动作），决定左栏分组。
//   BTParamDefine（结构体）：一个参数的定义（键、显示名、类型、默认值）。
//   BTNodeDefine（类）：一种节点的定义（类型名、显示名、分类、子节点上限、参数表）。
//   BTNodeRegistry（静态类）：全局节点注册表 + 内置节点（新增自定义节点的唯一入口）。

using System.Collections.Generic;
using System.Globalization;

namespace NBC.EditorTools.BehaviorTree
{
    // 节点分类：左栏按这个分组，也用于快速判断端口规则
    public enum BTNodeCategory
    {
        // 组合节点（有子节点）：Selector / Sequence / Parallel
        Composite = 0,

        // 条件节点（无子节点）：HPBelow / HasTarget
        Condition = 1,

        // 动作节点（无子节点）：MoveTo / CastSkill / Wait
        Action = 2
    }

    // 一个参数的定义：告诉属性面板「出什么控件」、导出时「什么类型"
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
    // 主要外部方法：无（纯数据）。
    // 谁在用它：BTNodeRegistry、BTNodePalette、BTInspectorView、BTGraphView（端口规则）。
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

    // 主要功能：全局节点注册表 —— 所有可用节点类型都登记在这里；新增自定义节点只改这一处。
    // 主要数据成员：s_defines（类型名 → 定义）、All（按注册顺序的只读列表）。
    // 主要外部方法：Register（新增/覆盖）、Find（按类型名查）、RegisterDefaults（内置节点）。
    // 谁在用它：左栏、属性面板、画布（端口规则）、预览运行时（按类型找执行器）。
    // 注意：Dialogue 里没有"自动扫描程序集"——故意用显式注册，避免编辑器启动时扫全部程序集拖慢。
    public static class BTNodeRegistry
    {
        // 类型名 → 定义；用字典保证 Find 是 O(1)
        private static readonly Dictionary<string, BTNodeDefine> s_defines =
            new Dictionary<string, BTNodeDefine>();

        // 按注册顺序保存，左栏分组显示时顺序稳定
        private static readonly List<BTNodeDefine> s_order = new List<BTNodeDefine>();

        // 是否已经注册过内置节点
        private static bool s_initialized;

        // 所有已注册节点（只读）
        public static IReadOnlyList<BTNodeDefine> All
        {
            get
            {
                EnsureInitialized();
                return s_order;
            }
        }

        // 功能：登记一种节点（同类型名会覆盖旧的 —— 方便热重载后更新定义）。
        // 入参：define 节点定义
        // 返回：无
        // 步骤：类型名非法就抛；已存在则覆盖并保留原顺序，否则追加
        // 上游：RegisterDefaults、以后的自定义节点注册处
        // 下游：All / Find
        public static void Register(BTNodeDefine define)
        {
            if (define == null || string.IsNullOrEmpty(define.TypeName))
            {
                throw new System.ArgumentException("[BT] 注册节点必须给 TypeName。");
            }

            if (s_defines.ContainsKey(define.TypeName))
            {
                s_defines[define.TypeName] = define;

                for (int i = 0; i < s_order.Count; i++)
                {
                    if (s_order[i].TypeName == define.TypeName)
                    {
                        s_order[i] = define;
                        break;
                    }
                }

                return;
            }

            s_defines[define.TypeName] = define;
            s_order.Add(define);
        }

        // 功能：按类型名找定义。
        // 入参：typeName 类型名
        // 返回：定义；找不到返回 null
        // 步骤：确保内置已注册后查字典
        // 上游：导入 JSON、画布重建、属性面板
        // 下游：无
        public static BTNodeDefine Find(string typeName)
        {
            EnsureInitialized();

            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            s_defines.TryGetValue(typeName, out BTNodeDefine define);
            return define;
        }

        // 功能：首次访问时注册内置节点（幂等）。
        // 入参：无
        // 返回：无
        // 步骤：置标记 → 调 RegisterDefaults
        // 上游：All / Find
        // 下游：Register
        public static void EnsureInitialized()
        {
            if (s_initialized)
            {
                return;
            }

            s_initialized = true;
            RegisterDefaults();
        }

        // 功能：注册内置节点：3 个组合、2 个条件、3 个动作。
        // 入参：无
        // 返回：无
        // 步骤：逐个 new BTNodeDefine 并 Register
        // 上游：EnsureInitialized
        // 下游：Register
        // 注意：新增节点就在这里加一条；参数字段要与 BTPreviewNode 的执行器对上（键名一致）。
        private static void RegisterDefaults()
        {
            Register(new BTNodeDefine
            {
                TypeName = "Selector",
                DisplayName = "Selector（选择）",
                Category = BTNodeCategory.Composite,
                MaxChildren = -1,
                Params = new BTParamDefine[0]
            });

            Register(new BTNodeDefine
            {
                TypeName = "Sequence",
                DisplayName = "Sequence（顺序）",
                Category = BTNodeCategory.Composite,
                MaxChildren = -1,
                Params = new BTParamDefine[0]
            });

            Register(new BTNodeDefine
            {
                TypeName = "Parallel",
                DisplayName = "Parallel（并行）",
                Category = BTNodeCategory.Composite,
                MaxChildren = -1,
                Params = new BTParamDefine[0]
            });

            Register(new BTNodeDefine
            {
                TypeName = "HPBelow",
                DisplayName = "HPBelow（血量低于）",
                Category = BTNodeCategory.Condition,
                MaxChildren = 0,
                Params = new[]
                {
                    new BTParamDefine
                    {
                        Key = "hpThreshold",
                        Label = "血量阈值",
                        Type = BTParamType.Int,
                        DefaultValue = "30"
                    }
                }
            });

            Register(new BTNodeDefine
            {
                TypeName = "HasTarget",
                DisplayName = "HasTarget（有目标）",
                Category = BTNodeCategory.Condition,
                MaxChildren = 0,
                Params = new BTParamDefine[0]
            });

            Register(new BTNodeDefine
            {
                TypeName = "MoveTo",
                DisplayName = "MoveTo（移动到）",
                Category = BTNodeCategory.Action,
                MaxChildren = 0,
                Params = new[]
                {
                    new BTParamDefine
                    {
                        Key = "speed",
                        Label = "移动速度",
                        Type = BTParamType.Float,
                        DefaultValue = "3"
                    }
                }
            });

            Register(new BTNodeDefine
            {
                TypeName = "CastSkill",
                DisplayName = "CastSkill（放技能）",
                Category = BTNodeCategory.Action,
                MaxChildren = 0,
                Params = new[]
                {
                    new BTParamDefine
                    {
                        Key = "skillId",
                        Label = "技能编号",
                        Type = BTParamType.Int,
                        DefaultValue = "1"
                    },
                    new BTParamDefine
                    {
                        Key = "cooldownSeconds",
                        Label = "冷却秒数",
                        Type = BTParamType.Float,
                        DefaultValue = "2"
                    }
                }
            });

            Register(new BTNodeDefine
            {
                TypeName = "Wait",
                DisplayName = "Wait（等待）",
                Category = BTNodeCategory.Action,
                MaxChildren = 0,
                Params = new[]
                {
                    new BTParamDefine
                    {
                        Key = "seconds",
                        Label = "等待秒数",
                        Type = BTParamType.Float,
                        DefaultValue = "1"
                    }
                }
            });
        }
    }
}
