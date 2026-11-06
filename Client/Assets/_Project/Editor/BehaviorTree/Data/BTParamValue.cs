// 这个文件里有：
//   BTParamType（枚举）：参数类型（float / int / bool / string 四型）。
//   BTParamValue（结构体）：一个参数的取值，带类型标签，导出 JSON 时保持原生类型。

using System;
using System.Globalization;

namespace NBC.EditorTools.BehaviorTree
{
    // 参数类型。JSON 里要写成原生值（数字/布尔/字符串），所以类型必须跟着值一起存。
    public enum BTParamType
    {
        // 浮点数，JSON 里是数字
        Float = 0,

        // 整数，JSON 里是数字（不带小数点）
        Int = 1,

        // 布尔，JSON 里是 true / false
        Bool = 2,

        // 字符串，JSON 里是带引号的字符串
        String = 3
    }

    // 主要功能：装一个参数的「类型 + 值」，是数据模型与 JSON 之间的唯一转换点。
    // 主要数据成员：Type（类型标签）与四个类型的取值字段。
    // 主要外部方法：FromFloat / FromInt / FromBool / FromString（构造）、AsFloat / AsInt / AsBool / AsString（读取）、ToInvariant。
    // 谁在用它：BTNodeData.Params、BTInspectorView（按类型出控件）、BTJsonUtil（写成 JSON 原生值）。
    // 注意：结构体是值类型，改它要先取出来、改完再放回字典，别指望"改副本能改到字典里"。
    [Serializable]
    public struct BTParamValue
    {
        // 这个值是什么类型；决定 JSON 里输出成数字、布尔还是字符串
        public BTParamType Type;

        // 浮点取值（Type == Float 时有效）
        public float FloatValue;

        // 整数取值（Type == Int 时有效）
        public int IntValue;

        // 布尔取值（Type == Bool 时有效）
        public bool BoolValue;

        // 字符串取值（Type == String 时有效）
        public string StringValue;

        // 功能：造一个浮点参数。
        // 入参：value 值
        // 返回：带 Float 类型标签的参数
        // 步骤：设类型与对应字段
        // 上游：节点定义里的默认值、属性面板改值
        // 下游：字典写入
        public static BTParamValue FromFloat(float value)
        {
            return new BTParamValue { Type = BTParamType.Float, FloatValue = value };
        }

        // 功能：造一个整数参数。
        // 入参：value 值
        // 返回：带 Int 类型标签的参数
        // 步骤：设类型与对应字段
        // 上游：节点定义里的默认值、属性面板改值
        // 下游：字典写入
        public static BTParamValue FromInt(int value)
        {
            return new BTParamValue { Type = BTParamType.Int, IntValue = value };
        }

        // 功能：造一个布尔参数。
        // 入参：value 值
        // 返回：带 Bool 类型标签的参数
        // 步骤：设类型与对应字段
        // 上游：节点定义里的默认值、属性面板改值
        // 下游：字典写入
        public static BTParamValue FromBool(bool value)
        {
            return new BTParamValue { Type = BTParamType.Bool, BoolValue = value };
        }

        // 功能：造一个字符串参数。
        // 入参：value 值
        // 返回：带 String 类型标签的参数
        // 步骤：设类型与对应字段
        // 上游：节点定义里的默认值、属性面板改值
        // 下游：字典写入
        public static BTParamValue FromString(string value)
        {
            return new BTParamValue { Type = BTParamType.String, StringValue = value ?? string.Empty };
        }

        // 功能：按浮点读值（别的类型会尽量转换）。
        // 入参：无
        // 返回：浮点值
        // 步骤：按类型分支转换
        // 上游：预览运行时的执行器
        // 下游：无
        public float AsFloat()
        {
            switch (Type)
            {
                case BTParamType.Float:
                    return FloatValue;
                case BTParamType.Int:
                    return IntValue;
                case BTParamType.Bool:
                    return BoolValue ? 1f : 0f;
                default:
                    float.TryParse(StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed);
                    return parsed;
            }
        }

        // 功能：按整数读值。
        // 入参：无
        // 返回：整数值（浮点会四舍五入）
        // 步骤：按类型分支转换
        // 上游：预览运行时的执行器
        // 下游：无
        public int AsInt()
        {
            switch (Type)
            {
                case BTParamType.Int:
                    return IntValue;
                case BTParamType.Float:
                    return (int)Math.Round(FloatValue);
                case BTParamType.Bool:
                    return BoolValue ? 1 : 0;
                default:
                    int.TryParse(StringValue, out int parsed);
                    return parsed;
            }
        }

        // 功能：按布尔读值。
        // 入参：无
        // 返回：布尔值（数字非 0 视为 true）
        // 步骤：按类型分支转换
        // 上游：预览运行时的执行器
        // 下游：无
        public bool AsBool()
        {
            switch (Type)
            {
                case BTParamType.Bool:
                    return BoolValue;
                case BTParamType.Int:
                    return IntValue != 0;
                case BTParamType.Float:
                    return Math.Abs(FloatValue) > float.Epsilon;
                default:
                    return bool.TryParse(StringValue, out bool parsed) && parsed;
            }
        }

        // 功能：按字符串读值。
        // 入参：无
        // 返回：字符串（数字用不变文化格式化）
        // 步骤：按类型分支格式化
        // 上游：属性面板显示、JSON 输出
        // 下游：无
        public string AsString()
        {
            switch (Type)
            {
                case BTParamType.String:
                    return StringValue ?? string.Empty;
                case BTParamType.Int:
                    return IntValue.ToString(CultureInfo.InvariantCulture);
                case BTParamType.Bool:
                    return BoolValue ? "true" : "false";
                default:
                    return FloatValue.ToString("0.###", CultureInfo.InvariantCulture);
            }
        }

        // 功能：给属性面板/节点标题用的一行文本。
        // 入参：无
        // 返回：「类型:值」
        // 步骤：拼类型名与 AsString
        // 上游：BTNodeView 标题
        // 下游：无
        public string ToDisplay()
        {
            return Type + ":" + AsString();
        }
    }
}
