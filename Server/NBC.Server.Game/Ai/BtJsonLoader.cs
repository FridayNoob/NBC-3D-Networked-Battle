// 这个文件里有：
//   BtJsonLoader（静态类）：把 R023 编辑器导出的 JSON 读成行为树节点（System.Text.Json，无需第三方包）。
//
// ⚠️ NodeType 字符串是**两端的契约**：编辑器里改名字会破坏这里，加节点时两边都要加。
//   服务端独有（编辑器暂时没有）的节点：IsDead / DetectTarget / InAttackRange / AttackReady / HomeFar / Chase / BasicAttack / ReturnHome / Patrol / Idle
//   编辑器已有、这里做语义映射：MoveTo→Chase、CastSkill→BasicAttack、Wait→等待占位（服务端无等待语义）

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NBC.Server.Game.Ai
{
    // 主要功能：JSON → 行为树；未知 NodeType 直接抛（不静默跳过）。
    // 主要数据成员：无（纯静态）。
    // 主要外部方法：LoadFromFile / TryLoadFromFile / Build。
    // 谁在用它：MonsterBrain 在构造时按 aiKind 加载树。
    // 注意：只读一次（构造期），运行期不碰文件系统。
    internal static class BtJsonLoader
    {
        // 功能：从文件加载树；文件不存在或格式不对就抛。
        // 入参：path 文件路径
        // 返回：根节点
        // 步骤：读文本 → Build
        // 上游：MonsterBrain
        // 下游：Build
        public static BtNode LoadFromFile(string path)
        {
            string json = File.ReadAllText(path);
            return Build(json);
        }

        // 功能：尝试从文件加载（失败返回 null，不抛）。
        // 入参：path 文件路径
        // 返回：根节点；文件不存在/解析失败 → null
        // 步骤：判断文件存在 → try LoadFromFile → catch 返回 null
        // 上游：MonsterBrain（先试文件，失败回落内置树）
        // 下游：LoadFromFile
        public static BtNode TryLoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                return LoadFromFile(path);
            }
            catch (Exception exception)
            {
                Console.WriteLine("[AI] 读取行为树失败（回落到内置树）：" + path + " → " + exception.Message);
                return null;
            }
        }

        // 功能：从 JSON 文本构建树。
        // 入参：json 文本（R023 Schema：TreeId / RootNodeId / Nodes）
        // 返回：根节点
        // 步骤：
        //   1) 解析文档
        //   2) 读 Nodes 字典与 RootNodeId（为空就用 FindRoot：没有父节点的那个）
        //   3) 从根递归 BuildNode
        // 上游：LoadFromFile
        // 下游：BuildNode / CreateLeaf
        public static BtNode Build(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement rootElement = document.RootElement;

            if (!rootElement.TryGetProperty("Nodes", out JsonElement nodes) ||
                nodes.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("[AI] JSON 里缺少 Nodes 对象。");
            }

            string rootId = rootElement.TryGetProperty("RootNodeId", out JsonElement rootIdElement)
                ? rootIdElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrEmpty(rootId))
            {
                rootId = FindRootId(nodes);
            }

            if (string.IsNullOrEmpty(rootId))
            {
                throw new InvalidOperationException("[AI] JSON 里找不到根节点（RootNodeId 为空且没有唯一无父节点）。");
            }

            return BuildNode(nodes, rootId);
        }

        // 功能：找根节点 id（没有任何 Children 引用它的那个）。
        // 入参：nodes JSON 对象
        // 返回：根 id；找不到返回空串
        // 步骤：收集所有被引用 id，再找没被引用的
        // 上游：Build
        // 下游：无
        private static string FindRootId(JsonElement nodes)
        {
            HashSet<string> referenced = new HashSet<string>();

            foreach (JsonProperty node in nodes.EnumerateObject())
            {
                if (!node.Value.TryGetProperty("Children", out JsonElement children) ||
                    children.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement child in children.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.String)
                    {
                        referenced.Add(child.GetString() ?? string.Empty);
                    }
                }
            }

            string found = string.Empty;
            int count = 0;

            foreach (JsonProperty node in nodes.EnumerateObject())
            {
                if (!referenced.Contains(node.Name))
                {
                    found = node.Name;
                    count++;
                }
            }

            return count == 1 ? found : string.Empty;
        }

        // 功能：递归把一个节点 JSON 变成 BtNode。
        // 入参：nodes JSON 对象；id 节点 id
        // 返回：节点
        // 步骤：
        //   1) 取 NodeType
        //   2) 组合节点建 Selector/Sequence/Parallel 并递归子节点
        //   3) 叶子节点按类型建（参数从 Params 读）
        // 上游：Build / 自己（递归）
        // 下游：CreateLeaf
        private static BtNode BuildNode(JsonElement nodes, string id)
        {
            if (!nodes.TryGetProperty(id, out JsonElement node) || node.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("[AI] JSON 里找不到节点 " + id + "。");
            }

            string typeName = node.TryGetProperty("NodeType", out JsonElement typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrEmpty(typeName))
            {
                throw new InvalidOperationException("[AI] 节点 " + id + " 缺少 NodeType。");
            }

            JsonElement parameters = node.TryGetProperty("Params", out JsonElement p) ? p : default;

            BtNode result;

            switch (typeName)
            {
                case "Selector":
                    result = new BtSelector();
                    break;

                case "Sequence":
                    result = new BtSequence();
                    break;

                case "Parallel":
                    result = new BtParallel();
                    break;

                default:
                    result = CreateLeaf(typeName, parameters);
                    break;
            }

            // 组合节点递归装子节点（叶子忽略 Children）
            if (typeName == "Selector" || typeName == "Sequence" || typeName == "Parallel")
            {
                if (node.TryGetProperty("Children", out JsonElement children) &&
                    children.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement child in children.EnumerateArray())
                    {
                        string childId = child.GetString() ?? string.Empty;

                        BtNode childNode = BuildNode(nodes, childId);

                        switch (result)
                        {
                            case BtSelector selector:
                                selector.AddChild(childNode);
                                break;

                            case BtSequence sequence:
                                sequence.AddChild(childNode);
                                break;

                            case BtParallel parallel:
                                parallel.AddChild(childNode);
                                break;
                        }
                    }
                }
            }

            return result;
        }

        // 功能：按类型建叶子节点（含编辑器类型的语义映射）。
        // 入参：typeName 类型名；parameters Params 元素
        // 返回：叶子节点
        // 步骤：switch typeName；未知类型抛出并列出已知类型
        // 上游：BuildNode
        // 下游：无
        // 注意：MoveTo 映射成 Chase、CastSkill 映射成 BasicAttack、Wait 映射成占位 —— 服务端没有"动画时间"概念，
        //       动作的时长由服务端冷却与移速决定，所以这两个编辑器节点在服务端只是"做同一件事"。
        private static BtNode CreateLeaf(string typeName, JsonElement parameters)
        {
            switch (typeName)
            {
                case "IsDead":
                    return new BtIsDead();

                case "DetectTarget":
                    return new BtDetectTarget();

                case "HasTarget":
                    return new BtHasTarget();

                case "InAttackRange":
                    return new BtTargetInAttackRange();

                case "AttackReady":
                    return new BtAttackReady();

                case "HPBelow":
                    return new BtHpBelow(ReadInt(parameters, "hpThreshold", 30));

                case "HomeFar":
                    return new BtHomeFar();

                case "Chase":
                case "MoveTo":
                    return new BtChase();

                case "BasicAttack":
                case "CastSkill":
                    return new BtBasicAttack();

                case "ReturnHome":
                    return new BtReturnHome();

                case "Patrol":
                    return new BtPatrol();

                case "Idle":
                case "Wait":
                    return new BtIdle();

                default:
                    throw new InvalidOperationException(
                        "[AI] 未知节点类型 " + typeName +
                        "（已知：Selector/Sequence/Parallel/IsDead/DetectTarget/HasTarget/InAttackRange/AttackReady/HPBelow/HomeFar/" +
                        "Chase/MoveTo/BasicAttack/CastSkill/ReturnHome/Patrol/Idle/Wait）。请两边同步注册。");
            }
        }

        // 功能：从 Params 里读一个整数（缺省用 defaultValue）。
        // 入参：parameters Params 元素；key 键；defaultValue 缺省值
        // 返回：整数值
        // 步骤：Params 是对象且有该键且是数字 → 读；否则缺省
        // 上游：CreateLeaf
        // 下游：无
        private static int ReadInt(JsonElement parameters, string key, int defaultValue)
        {
            if (parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty(key, out JsonElement value))
            {
                return defaultValue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetInt32(out int parsed) ? parsed : (int)value.GetDouble(),
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                JsonValueKind.String => int.TryParse(value.GetString(), out int parsed) ? parsed : defaultValue,
                _ => defaultValue
            };
        }
    }
}
