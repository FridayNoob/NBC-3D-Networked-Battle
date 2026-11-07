// 这个文件里有：
//   BTJsonUtil（静态类）：把 BTGraphData 写成约定 Schema 的 JSON，以及从 JSON 读回来。
//   BtJsonValue（类）：自研极简 JSON DOM（对象/数组/字符串/数字/布尔/空）。
//   BtJsonParser（类）：递归下降解析器，报错带行列。
//   BtJsonException（异常）：导入失败时抛它，窗口直接把消息给用户看。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NBC.EditorTools.BehaviorTree
{
    // 导入失败时抛这个；消息里带行列，方便定位
    public sealed class BtJsonException : Exception
    {
        // 功能：构造异常。
        // 入参：message 消息
        // 返回：无
        // 步骤：转基类
        // 上游：BtJsonParser
        // 下游：BehaviorTreeEditorWindow 的 catch
        public BtJsonException(string message) : base(message)
        {
        }
    }

    // 极简 JSON DOM：只做本项目 Schema 需要的几件事，不追求通用
    internal sealed class BtJsonValue
    {
        // 值类型
        public enum EKind
        {
            Object,
            Array,
            String,
            Number,
            Bool,
            Null
        }

        // 这个值是什么类型
        public EKind Kind = EKind.Null;

        // Kind == Object 时的键值表
        public Dictionary<string, BtJsonValue> Object = new Dictionary<string, BtJsonValue>();

        // Kind == Array 时的元素表
        public List<BtJsonValue> Array = new List<BtJsonValue>();

        // Kind == String 时的文本
        public string Text = string.Empty;

        // Kind == Number 时的原始数字文本（保留是否带小数点，导入时用来判断 Int/Float）
        public string RawNumber = "0";

        // Kind == Bool 时的值
        public bool Bool;

        // 功能：取子对象里的成员，不存在返回 null。
        // 入参：key 键
        // 返回：成员或 null
        // 步骤：类型不是 Object 返回 null；否则查字典
        // 上游：BTJsonUtil.Import
        // 下游：无
        public BtJsonValue Get(string key)
        {
            if (Kind != EKind.Object)
            {
                return null;
            }

            Object.TryGetValue(key, out BtJsonValue value);
            return value;
        }
    }

    // 递归下降 JSON 解析器：支持对象/数组/字符串/数字/true/false/null，忽略空白
    internal sealed class BtJsonParser
    {
        // 待解析文本
        private readonly string m_text;

        // 当前读到的位置
        private int m_index;

        // 当前行号（1 基，报错用）
        private int m_line = 1;

        // 当前列号（1 基，报错用）
        private int m_column = 1;

        // 功能：构造解析器。
        // 入参：text JSON 文本
        // 返回：无
        // 步骤：存文本
        // 上游：BTJsonUtil.Import
        // 下游：Parse
        public BtJsonParser(string text)
        {
            m_text = text ?? string.Empty;
        }

        // 功能：解析整段文本。
        // 入参：无
        // 返回：根值
        // 步骤：跳空白 → 解析值 → 跳空白 → 必须到结尾
        // 上游：BTJsonUtil.Import
        // 下游：ParseValue
        public BtJsonValue Parse()
        {
            SkipWhitespace();
            BtJsonValue value = ParseValue();
            SkipWhitespace();

            if (!AtEnd)
            {
                throw Error("JSON 结尾还有多余内容");
            }

            return value;
        }

        // 是否已经读到结尾
        private bool AtEnd => m_index >= m_text.Length;

        // 当前字符（越界返回 '\0'）
        private char Current => AtEnd ? '\0' : m_text[m_index];

        // 功能：跳过一个字符并维护行列号。
        // 入参：无
        // 返回：无
        // 步骤：换行就 line++ 且 column 归 1，否则 column++
        // 上游：所有读取处
        // 下游：无
        private void Advance()
        {
            if (AtEnd)
            {
                return;
            }

            if (m_text[m_index] == '\n')
            {
                m_line++;
                m_column = 1;
            }
            else
            {
                m_column++;
            }

            m_index++;
        }

        // 功能：跳过空白字符。
        // 入参：无
        // 返回：无
        // 步骤：循环 Advance 直到非空白
        // 上游：Parse / ParseValue
        // 下游：无
        private void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(Current))
            {
                Advance();
            }
        }

        // 功能：造一个带行列的异常。
        // 入参：message 原因
        // 返回：异常对象
        // 步骤：拼行列
        // 上游：各解析失败处
        // 下游：调用方 throw
        private BtJsonException Error(string message)
        {
            return new BtJsonException("[BT JSON] 第 " + m_line + " 行第 " + m_column + " 列：" + message);
        }

        // 功能：解析一个值（按首字符派发）。
        // 入参：无
        // 返回：解析结果
        // 步骤：{ → 对象，[ → 数组，" → 字符串，t/f → 布尔，n → null，其它 → 数字
        // 上游：Parse / ParseObject / ParseArray
        // 下游：对应子方法
        private BtJsonValue ParseValue()
        {
            SkipWhitespace();

            if (AtEnd)
            {
                throw Error("文本意外结束，期待一个值");
            }

            char c = Current;

            if (c == '{')
            {
                return ParseObject();
            }

            if (c == '[')
            {
                return ParseArray();
            }

            if (c == '"')
            {
                return new BtJsonValue { Kind = BtJsonValue.EKind.String, Text = ParseString() };
            }

            if (c == 't' || c == 'f')
            {
                return ParseBool();
            }

            if (c == 'n')
            {
                return ParseNull();
            }

            return ParseNumber();
        }

        // 功能：解析对象。
        // 入参：无
        // 返回：Object 值
        // 步骤：读完 '{' → 循环「字符串 : 值」→ 逗号分隔 → 遇到 '}' 结束
        // 上游：ParseValue
        // 下游：ParseString / ParseValue
        private BtJsonValue ParseObject()
        {
            BtJsonValue result = new BtJsonValue { Kind = BtJsonValue.EKind.Object };
            Advance();      // '{'
            SkipWhitespace();

            if (Current == '}')
            {
                Advance();
                return result;
            }

            while (true)
            {
                SkipWhitespace();

                if (Current != '"')
                {
                    throw Error("对象的键必须是字符串");
                }

                string key = ParseString();
                SkipWhitespace();

                if (Current != ':')
                {
                    throw Error("键 \"" + key + "\" 后面缺少冒号");
                }

                Advance();
                result.Object[key] = ParseValue();
                SkipWhitespace();

                if (Current == ',')
                {
                    Advance();
                    continue;
                }

                if (Current == '}')
                {
                    Advance();
                    return result;
                }

                throw Error("对象成员之间必须是逗号，或者是结束的 '}'");
            }
        }

        // 功能：解析数组。
        // 入参：无
        // 返回：Array 值
        // 步骤：读完 '[' → 循环值 → 逗号分隔 → 遇到 ']' 结束
        // 上游：ParseValue
        // 下游：ParseValue
        private BtJsonValue ParseArray()
        {
            BtJsonValue result = new BtJsonValue { Kind = BtJsonValue.EKind.Array };
            Advance();      // '['
            SkipWhitespace();

            if (Current == ']')
            {
                Advance();
                return result;
            }

            while (true)
            {
                result.Array.Add(ParseValue());
                SkipWhitespace();

                if (Current == ',')
                {
                    Advance();
                    continue;
                }

                if (Current == ']')
                {
                    Advance();
                    return result;
                }

                throw Error("数组元素之间必须是逗号，或者是结束的 ']'");
            }
        }

        // 功能：解析字符串（支持 \" \\ \/ \n \t \r \b \f 与 \uXXXX）。
        // 入参：无
        // 返回：字符串内容
        // 步骤：读完开引号 → 逐字符拼 → 遇到闭引号结束
        // 上游：ParseValue / ParseObject
        // 下游：无
        private string ParseString()
        {
            StringBuilder builder = new StringBuilder();
            Advance();      // 开引号

            while (true)
            {
                if (AtEnd)
                {
                    throw Error("字符串没有结束的引号");
                }

                char c = Current;

                if (c == '"')
                {
                    Advance();
                    return builder.ToString();
                }

                if (c == '\\')
                {
                    Advance();

                    if (AtEnd)
                    {
                        throw Error("转义符后面没有字符");
                    }

                    char escape = Current;

                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            builder.Append(ParseUnicode());
                            continue;
                        default:
                            throw Error("不认识的转义 \\" + escape);
                    }

                    Advance();
                    continue;
                }

                builder.Append(c);
                Advance();
            }
        }

        // 功能：解析 \uXXXX 的四位十六进制（当前字符是 'u'）。
        // 入参：无
        // 返回：对应字符
        // 步骤：跳过 u → 读 4 位 → 转 char
        // 上游：ParseString
        // 下游：无
        private char ParseUnicode()
        {
            Advance();      // 'u'

            if (m_index + 4 > m_text.Length)
            {
                throw Error("\\u 后面必须跟 4 位十六进制");
            }

            string hex = m_text.Substring(m_index, 4);

            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
            {
                throw Error("\\u" + hex + " 不是合法的十六进制");
            }

            for (int i = 0; i < 4; i++)
            {
                Advance();
            }

            return (char)code;
        }

        // 功能：解析 true / false。
        // 入参：无
        // 返回：Bool 值
        // 步骤：比对文本前缀再前进
        // 上游：ParseValue
        // 下游：无
        private BtJsonValue ParseBool()
        {
            if (Match("true"))
            {
                return new BtJsonValue { Kind = BtJsonValue.EKind.Bool, Bool = true };
            }

            if (Match("false"))
            {
                return new BtJsonValue { Kind = BtJsonValue.EKind.Bool, Bool = false };
            }

            throw Error("期待 true 或 false");
        }

        // 功能：解析 null。
        // 入参：无
        // 返回：Null 值
        // 步骤：比对 null 文本
        // 上游：ParseValue
        // 下游：无
        private BtJsonValue ParseNull()
        {
            if (!Match("null"))
            {
                throw Error("期待 null");
            }

            return new BtJsonValue { Kind = BtJsonValue.EKind.Null };
        }

        // 功能：解析数字（保留原始文本，导入时判断 Int/Float）。
        // 入参：无
        // 返回：Number 值
        // 步骤：吃掉 - + 数字 + . + e/E 符号
        // 上游：ParseValue
        // 下游：无
        private BtJsonValue ParseNumber()
        {
            int start = m_index;

            if (Current == '-')
            {
                Advance();
            }

            while (!AtEnd && (char.IsDigit(Current) || Current == '.' || Current == 'e' || Current == 'E' ||
                              Current == '+' || Current == '-'))
            {
                Advance();
            }

            string raw = m_text.Substring(start, m_index - start);

            if (raw.Length == 0)
            {
                throw Error("期待一个数字");
            }

            return new BtJsonValue { Kind = BtJsonValue.EKind.Number, RawNumber = raw };
        }

        // 功能：尝试匹配一段字面量并前进。
        // 入参：literal 期望的字面量
        // 返回：true = 匹配并已前进
        // 步骤：Substring 比对
        // 上游：ParseBool / ParseNull
        // 下游：无
        private bool Match(string literal)
        {
            if (m_index + literal.Length > m_text.Length)
            {
                return false;
            }

            if (string.CompareOrdinal(m_text, m_index, literal, 0, literal.Length) != 0)
            {
                return false;
            }

            for (int i = 0; i < literal.Length; i++)
            {
                Advance();
            }

            return true;
        }
    }

    // 主要功能：行为树数据的 JSON 读写（Schema 固定，参数保持原生 JSON 类型）。
    // 主要数据成员：无（纯静态）。
    // 主要外部方法：Export / Import。
    // 谁在用它：BehaviorTreeEditorWindow 的导入导出按钮。
    // 注意：这里**不引入任何 JSON 库**（工程没有 Newtonsoft，JsonUtility 又不支持字典与动态类型）。
    public static class BTJsonUtil
    {
        // 缩进空格数（导出可读性）
        private const int Indent = 2;

        // 功能：把整棵树写成 JSON。
        // 入参：data 树数据
        // 返回：JSON 文本（带缩进、字段顺序固定：TreeId → RootNodeId → Nodes）
        // 步骤：
        //   1) 先算根 id（若 data.RootNodeId 为空则用 FindRootId 推断）
        //   2) 写 TreeId / RootNodeId
        //   3) 按 id 排序写 Nodes（保证两次导出文本一致，便于比对）
        // 上游：导出按钮
        // 下游：无
        public static string Export(BTGraphData data)
        {
            if (data == null)
            {
                throw new BtJsonException("[BT JSON] 没有可导出的数据。");
            }

            string rootId = string.IsNullOrEmpty(data.RootNodeId) ? data.FindRootId() : data.RootNodeId;
            StringBuilder builder = new StringBuilder();
            builder.Append('{').Append('\n');
            builder.Append(' ', Indent).Append("\"TreeId\": ").Append(Quote(data.TreeId)).Append(',').Append('\n');
            builder.Append(' ', Indent).Append("\"RootNodeId\": ").Append(Quote(rootId)).Append(',').Append('\n');
            builder.Append(' ', Indent).Append("\"Nodes\": {").Append('\n');

            List<string> ids = new List<string>(data.Nodes.Keys);
            ids.Sort(StringComparer.Ordinal);

            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                BTNodeData node = data.Nodes[id];
                builder.Append(' ', Indent * 2).Append(Quote(id)).Append(": {").Append('\n');
                builder.Append(' ', Indent * 3).Append("\"NodeType\": ").Append(Quote(node.NodeType)).Append(',').Append('\n');
                builder.Append(' ', Indent * 3).Append("\"Pos\": { \"x\": ")
                    .Append(Number(node.PosX)).Append(", \"y\": ").Append(Number(node.PosY)).Append(" },").Append('\n');
                builder.Append(' ', Indent * 3).Append("\"Params\": ");
                WriteParams(builder, node, Indent * 3);
                builder.Append(',').Append('\n');
                builder.Append(' ', Indent * 3).Append("\"Children\": ");
                WriteChildren(builder, node);
                builder.Append('\n');
                builder.Append(' ', Indent * 2).Append('}');

                if (i < ids.Count - 1)
                {
                    builder.Append(',');
                }

                builder.Append('\n');
            }

            builder.Append(' ', Indent).Append('}').Append('\n');
            builder.Append('}').Append('\n');
            return builder.ToString();
        }

        // 功能：从 JSON 文本读出树数据。
        // 入参：json 文本
        // 返回：树数据（已按节点定义补齐默认参数）
        // 步骤：
        //   1) 解析成 DOM
        //   2) 读 TreeId / RootNodeId / Nodes
        //   3) 每个节点读 NodeType / Pos / Params / Children，并按定义补默认参数
        // 上游：导入按钮
        // 下游：无
        // 注意：未知 NodeType 会抛异常（不静默丢弃，否则一导入就丢节点）
        public static BTGraphData Import(string json)
        {
            BtJsonValue root = new BtJsonParser(json).Parse();

            if (root.Kind != BtJsonValue.EKind.Object)
            {
                throw new BtJsonException("[BT JSON] 顶层必须是一个对象。");
            }

            BTGraphData data = new BTGraphData();
            BtJsonValue treeId = root.Get("TreeId");
            data.TreeId = treeId != null && treeId.Kind == BtJsonValue.EKind.String ? treeId.Text : "new_tree";

            BtJsonValue rootNodeId = root.Get("RootNodeId");
            data.RootNodeId = rootNodeId != null && rootNodeId.Kind == BtJsonValue.EKind.String
                ? rootNodeId.Text
                : string.Empty;

            BtJsonValue nodes = root.Get("Nodes");

            if (nodes == null || nodes.Kind != BtJsonValue.EKind.Object)
            {
                throw new BtJsonException("[BT JSON] 缺少 Nodes 对象。");
            }

            foreach (KeyValuePair<string, BtJsonValue> pair in nodes.Object)
            {
                data.Nodes[pair.Key] = ReadNode(pair.Key, pair.Value);
            }

            return data;
        }

        // 功能：读一个节点。
        // 入参：id 节点 id；value 该节点的 JSON 对象
        // 返回：节点数据
        // 步骤：读四个字段；参数值按定义类型强转
        // 上游：Import
        // 下游：ReadParam
        private static BTNodeData ReadNode(string id, BtJsonValue value)
        {
            if (value.Kind != BtJsonValue.EKind.Object)
            {
                throw new BtJsonException("[BT JSON] 节点 " + id + " 必须是对象。");
            }

            BTNodeData node = new BTNodeData();

            BtJsonValue nodeType = value.Get("NodeType");
            node.NodeType = nodeType != null && nodeType.Kind == BtJsonValue.EKind.String ? nodeType.Text : string.Empty;

            BTNodeDefine define = BTNodeRegistry.Find(node.NodeType);

            if (define == null)
            {
                throw new BtJsonException("[BT JSON] 节点 " + id + " 的类型 \"" + node.NodeType +
                                          "\" 没有注册（检查 BTNodeRegistry）。");
            }

            BtJsonValue pos = value.Get("Pos");

            if (pos != null && pos.Kind == BtJsonValue.EKind.Object)
            {
                node.PosX = ReadNumber(pos.Get("x"));
                node.PosY = ReadNumber(pos.Get("y"));
            }

            BtJsonValue parms = value.Get("Params");

            if (parms != null && parms.Kind == BtJsonValue.EKind.Object)
            {
                foreach (KeyValuePair<string, BtJsonValue> pair in parms.Object)
                {
                    node.Params[pair.Key] = ReadParam(pair.Key, pair.Value);
                }
            }

            node.EnsureDefaults(define);
            CoerceParamsToDefinition(node, define);

            BtJsonValue children = value.Get("Children");

            if (children != null && children.Kind == BtJsonValue.EKind.Array)
            {
                for (int i = 0; i < children.Array.Count; i++)
                {
                    BtJsonValue child = children.Array[i];

                    if (child.Kind != BtJsonValue.EKind.String)
                    {
                        throw new BtJsonException("[BT JSON] 节点 " + id + " 的 Children 里必须都是字符串。");
                    }

                    node.Children.Add(child.Text);
                }
            }

            return node;
        }

        // 功能：把已读到的参数值强制转成**节点定义里声明的类型**。
        // 入参：node 节点数据；define 节点定义
        // 返回：无
        // 步骤：遍历定义里的参数，类型不一致就按定义转换
        // 上游：ReadNode
        // 下游：BTParamValue.FromXxx
        // 注意：JSON 里 `1` 和 `1.0` 在文本上都可能被写成 `1`（我导出浮点时用 "R"，
        //      整数值的 float 也会写成 `1`），所以**不能靠小数点猜类型**，必须以节点定义为准。
        private static void CoerceParamsToDefinition(BTNodeData node, BTNodeDefine define)
        {
            if (define == null)
            {
                return;
            }

            for (int i = 0; i < define.Params.Length; i++)
            {
                BTParamDefine paramDefine = define.Params[i];

                if (!node.Params.TryGetValue(paramDefine.Key, out BTParamValue value) || value.Type == paramDefine.Type)
                {
                    continue;
                }

                switch (paramDefine.Type)
                {
                    case BTParamType.Int:
                        node.Params[paramDefine.Key] = BTParamValue.FromInt(value.AsInt());
                        break;

                    case BTParamType.Bool:
                        node.Params[paramDefine.Key] = BTParamValue.FromBool(value.AsBool());
                        break;

                    case BTParamType.String:
                        node.Params[paramDefine.Key] = BTParamValue.FromString(value.AsString());
                        break;

                    default:
                        node.Params[paramDefine.Key] = BTParamValue.FromFloat(value.AsFloat());
                        break;
                }
            }
        }
        // 功能：读一个参数值，并尽量按定义里的类型还原。
        // 入参：key 参数键；value JSON 值
        // 返回：参数值
        // 步骤：布尔 → Bool；字符串 → String；数字 → 整数看有没有小数点定 Int/Float
        // 上游：ReadNode
        // 下游：无
        private static BTParamValue ReadParam(string key, BtJsonValue value)
        {
            switch (value.Kind)
            {
                case BtJsonValue.EKind.Bool:
                    return BTParamValue.FromBool(value.Bool);

                case BtJsonValue.EKind.String:
                    return BTParamValue.FromString(value.Text);

                case BtJsonValue.EKind.Number:
                    if (value.RawNumber.IndexOf('.') >= 0 || value.RawNumber.IndexOf('e') >= 0 ||
                        value.RawNumber.IndexOf('E') >= 0)
                    {
                        float.TryParse(value.RawNumber, NumberStyles.Float, CultureInfo.InvariantCulture,
                            out float floatValue);
                        return BTParamValue.FromFloat(floatValue);
                    }

                    int.TryParse(value.RawNumber, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int intValue);
                    return BTParamValue.FromInt(intValue);

                default:
                    throw new BtJsonException("[BT JSON] 参数 \"" + key + "\" 的类型不支持（只支持数字/布尔/字符串）。");
            }
        }

        // 功能：读一个数字（缺省 0）。
        // 入参：value JSON 值
        // 返回：浮点
        // 步骤：不是数字就返回 0
        // 上游：ReadNode
        // 下游：无
        private static float ReadNumber(BtJsonValue value)
        {
            if (value == null || value.Kind != BtJsonValue.EKind.Number)
            {
                return 0f;
            }

            float.TryParse(value.RawNumber, NumberStyles.Float, CultureInfo.InvariantCulture, out float result);
            return result;
        }

        // 功能：写 Params 对象（值按类型写成 JSON 原生值）。
        // 入参：builder 目标；node 节点；indent 缩进
        // 返回：无
        // 步骤：空表写 {}；否则逐项写（键排序保证稳定）
        // 上游：Export
        // 下游：无
        private static void WriteParams(StringBuilder builder, BTNodeData node, int indent)
        {
            if (node.Params.Count == 0)
            {
                builder.Append("{ }");
                return;
            }

            List<string> keys = new List<string>(node.Params.Keys);
            keys.Sort(StringComparer.Ordinal);
            builder.Append('{').Append('\n');

            for (int i = 0; i < keys.Count; i++)
            {
                BTParamValue value = node.Params[keys[i]];
                builder.Append(' ', indent + Indent).Append(Quote(keys[i])).Append(": ");
                WriteParamValue(builder, value);

                if (i < keys.Count - 1)
                {
                    builder.Append(',');
                }

                builder.Append('\n');
            }

            builder.Append(' ', indent).Append('}');
        }

        // 功能：按类型写一个参数值。
        // 入参：builder 目标；value 参数值
        // 返回：无
        // 步骤：Float/Int → 数字；Bool → true/false；String → 带引号
        // 上游：WriteParams
        // 下游：无
        private static void WriteParamValue(StringBuilder builder, BTParamValue value)
        {
            switch (value.Type)
            {
                case BTParamType.Int:
                    builder.Append(value.IntValue.ToString(CultureInfo.InvariantCulture));
                    break;

                case BTParamType.Bool:
                    builder.Append(value.BoolValue ? "true" : "false");
                    break;

                case BTParamType.String:
                    builder.Append(Quote(value.StringValue));
                    break;

                default:
                    builder.Append(Number(value.FloatValue));
                    break;
            }
        }

        // 功能：写 Children 数组。
        // 入参：builder 目标；node 节点
        // 返回：无
        // 步骤：空表写 []；否则 ["a", "b"]
        // 上游：Export
        // 下游：无
        private static void WriteChildren(StringBuilder builder, BTNodeData node)
        {
            builder.Append('[');

            for (int i = 0; i < node.Children.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(Quote(node.Children[i]));
            }

            builder.Append(']');
        }

        // 功能：把浮点写成不变文化的紧凑数字。
        // 入参：value 数值
        // 返回：文本（整数值不带小数点）
        // 步骤：用 "R" 格式化
        // 上游：Export
        // 下游：无
        private static string Number(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        // 功能：把字符串包成 JSON 字符串（转义引号、反斜杠、换行等）。
        // 入参：text 原文
        // 返回：带引号的文本
        // 步骤：逐字符转义
        // 上游：Export
        // 下游：无
        private static string Quote(string text)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append('"');

            if (!string.IsNullOrEmpty(text))
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];

                    switch (c)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default: builder.Append(c); break;
                    }
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}
