// 这个文件里有：
//   BTPreviewStatus（枚举）：预览用的三态（成功/失败/运行中）。
//   BTBlackboard（类）：预览时的简易黑板（数值/布尔/字符串，由用户在属性面板手填）。
//   BTPreviewNode（类）：【仅编辑器】简化版行为树运行时节点（按 NodeType 字符串执行）。
//   BTPreviewRunner（类）：【仅编辑器】持有根节点并驱动 Tick。
//
// ⚠️ 本文件的代码**只在编辑器里跑**（所在程序集 NBC.Editor 的 includePlatforms = Editor）。
//    它只是给策划看"树会怎么跑"，绝不参与正式游戏与服务端逻辑。

using System;
using System.Collections.Generic;

namespace NBC.EditorTools.BehaviorTree
{
    // 预览三态
    public enum BTPreviewStatus
    {
        // 成功（节点视图显示绿色）
        Success = 0,

        // 失败（红色）
        Failure = 1,

        // 运行中（黄色）
        Running = 2
    }

    // 主要功能：预览时条件节点读的「黑板」——用户在右侧面板手填测试值（hp / targetId / skillCd）。
    // 主要数据成员：m_values（键 → 参数值）。
    // 主要外部方法：Set / GetInt / GetFloat / TryGet / Clear。
    // 谁在用它：BTPreviewNode（条件/动作）、BTBlackboardView（编辑）。
    // 注意：黑板只在预览期有效，**不序列化、不导出**。
    public sealed class BTBlackboard
    {
        // 键 → 值
        private readonly Dictionary<string, BTParamValue> m_values = new Dictionary<string, BTParamValue>();

        // 功能：设置一个黑板值。
        // 入参：key 键；value 值
        // 返回：无
        // 步骤：直接写字典
        // 上游：BTBlackboardView 的输入框
        // 下游：BTPreviewNode 读取
        public void Set(string key, BTParamValue value)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            m_values[key] = value;
        }

        // 功能：读整数（缺省 0）。
        // 入参：key 键
        // 返回：整数值
        // 步骤：查字典，命中就 AsInt
        // 上游：BTPreviewNode
        // 下游：无
        public int GetInt(string key)
        {
            return m_values.TryGetValue(key, out BTParamValue value) ? value.AsInt() : 0;
        }

        // 功能：读浮点（缺省 0）。
        // 入参：key 键
        // 返回：浮点值
        // 步骤：查字典，命中就 AsFloat
        // 上游：BTPreviewNode
        // 下游：无
        public float GetFloat(string key)
        {
            return m_values.TryGetValue(key, out BTParamValue value) ? value.AsFloat() : 0f;
        }

        // 功能：取原始参数值（给界面显示用）。
        // 入参：key 键；value 命中的值
        // 返回：true = 键存在
        // 步骤：查字典
        // 上游：BTBlackboardView
        // 下游：无
        public bool TryGet(string key, out BTParamValue value)
        {
            return m_values.TryGetValue(key, out value);
        }

        // 功能：清空黑板（停止预览时复位）。
        // 入参：无
        // 返回：无
        // 步骤：清字典
        // 上游：BTPreviewRunner.Stop
        // 下游：无
        public void Clear()
        {
            m_values.Clear();
        }
    }

    // 主要功能：一棵**预览专用**的运行时树节点 —— 只按 NodeType 执行简化行为，不含任何游戏逻辑。
    // 主要数据成员：NodeId / NodeType / Params / Children / m_elapsed（Wait/MoveTo 的计时）。
    // 主要外部方法：Tick（跑一帧，返回三态）、Reset（重置计时）。
    // 谁在用它：BTPreviewRunner（根）、自己的 Children（递归）。
    // 注意：这是**另一套对象**，与画布的 BTNodeData 完全分离；预览不会改画布数据。
    public sealed class BTPreviewNode
    {
        // 对应画布节点的 id（回传状态用）
        public string NodeId = string.Empty;

        // 节点类型（Selector / Sequence / HPBelow / …）
        public string NodeType = string.Empty;

        // 该节点的参数（从画布数据拷来的副本）
        public Dictionary<string, BTParamValue> Params = new Dictionary<string, BTParamValue>();

        // 子节点（顺序与 Children 一致）
        public List<BTPreviewNode> Children = new List<BTPreviewNode>();

        // Wait / MoveTo 用掉的秒数
        private float m_elapsed;

        // 最近一次 Tick 的结果（组合节点与叶子都记，供 UI 上色）
        public BTPreviewStatus LastStatus { get; private set; } = BTPreviewStatus.Failure;

        // 功能：跑一帧。
        // 入参：blackboard 黑板；deltaSeconds 距上一帧的秒数
        // 返回：三态
        // 步骤：按 NodeType 分派到对应的简化实现
        // 上游：BTPreviewRunner.Tick（根）/ 父节点（子节点）
        // 下游：Children 的 Tick
        // 注意：未知类型一律 Failure（并在控制台提示一次由 Runner 负责）
        public BTPreviewStatus Tick(BTBlackboard blackboard, float deltaSeconds)
        {
            LastStatus = TickCore(blackboard, deltaSeconds);
            return LastStatus;
        }

        // 功能：真正的分派（由 Tick 包一层，保证 LastStatus 一定被记录）。
        // 入参：blackboard 黑板；deltaSeconds 距上一帧的秒数
        // 返回：三态
        // 步骤：按 NodeType 分派
        // 上游：Tick
        // 下游：各 TickXxx
        private BTPreviewStatus TickCore(BTBlackboard blackboard, float deltaSeconds)
        {
            switch (NodeType)
            {
                case "Selector":
                    return TickSelector(blackboard, deltaSeconds);

                case "Sequence":
                    return TickSequence(blackboard, deltaSeconds);

                case "Parallel":
                    return TickParallel(blackboard, deltaSeconds);

                case "HPBelow":
                    return blackboard.GetInt("hp") < GetInt("hpThreshold") ? BTPreviewStatus.Success : BTPreviewStatus.Failure;

                case "HasTarget":
                    return blackboard.GetInt("targetId") > 0 ? BTPreviewStatus.Success : BTPreviewStatus.Failure;

                case "MoveTo":
                    return TickTimed(GetFloat("speed") <= 0f ? 0.4f : 0.4f, deltaSeconds);

                case "CastSkill":
                    return TickCastSkill(blackboard);

                case "Wait":
                    return TickTimed(GetFloat("seconds"), deltaSeconds);

                default:
                    return BTPreviewStatus.Failure;
            }
        }

        // 功能：重置计时状态（重建运行时树时用）。
        // 入参：无
        // 返回：无
        // 步骤：清零 elapsed 并递归子节点
        // 上游：BTPreviewRunner.Start
        // 下游：无
        public void Reset()
        {
            m_elapsed = 0f;

            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].Reset();
            }
        }

        // Selector：依次跑，第一个非 Failure 就返回（都失败才 Failure）
        private BTPreviewStatus TickSelector(BTBlackboard blackboard, float deltaSeconds)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                BTPreviewStatus status = Children[i].Tick(blackboard, deltaSeconds);

                if (status != BTPreviewStatus.Failure)
                {
                    return status;
                }
            }

            return BTPreviewStatus.Failure;
        }

        // Sequence：依次跑，遇到 Failure 立刻 Failure，遇到 Running 就 Running（全成功才 Success）
        private BTPreviewStatus TickSequence(BTBlackboard blackboard, float deltaSeconds)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                BTPreviewStatus status = Children[i].Tick(blackboard, deltaSeconds);

                if (status == BTPreviewStatus.Failure)
                {
                    return BTPreviewStatus.Failure;
                }

                if (status == BTPreviewStatus.Running)
                {
                    return BTPreviewStatus.Running;
                }
            }

            return BTPreviewStatus.Success;
        }

        // Parallel：全部跑；有 Failure 就 Failure，全 Success 才 Success，否则 Running
        private BTPreviewStatus TickParallel(BTBlackboard blackboard, float deltaSeconds)
        {
            bool anyRunning = false;

            for (int i = 0; i < Children.Count; i++)
            {
                BTPreviewStatus status = Children[i].Tick(blackboard, deltaSeconds);

                if (status == BTPreviewStatus.Failure)
                {
                    return BTPreviewStatus.Failure;
                }

                if (status == BTPreviewStatus.Running)
                {
                    anyRunning = true;
                }
            }

            return anyRunning ? BTPreviewStatus.Running : BTPreviewStatus.Success;
        }

        // 计时型动作：跑满 duration 秒就 Success，期间 Running
        private BTPreviewStatus TickTimed(float duration, float deltaSeconds)
        {
            m_elapsed += deltaSeconds;

            if (m_elapsed >= duration)
            {
                m_elapsed = 0f;
                return BTPreviewStatus.Success;
            }

            return BTPreviewStatus.Running;
        }

        // CastSkill：冷却没到就 Failure；到了就扣冷却并 Success
        private BTPreviewStatus TickCastSkill(BTBlackboard blackboard)
        {
            float cooldown = blackboard.GetFloat("skillCd");

            if (cooldown > 0f)
            {
                return BTPreviewStatus.Failure;
            }

            blackboard.Set("skillCd", BTParamValue.FromFloat(GetFloat("cooldownSeconds")));
            return BTPreviewStatus.Success;
        }

        // 读整数参数（缺省 0）
        private int GetInt(string key)
        {
            return Params.TryGetValue(key, out BTParamValue value) ? value.AsInt() : 0;
        }

        // 读浮点参数（缺省 0）
        private float GetFloat(string key)
        {
            return Params.TryGetValue(key, out BTParamValue value) ? value.AsFloat() : 0f;
        }
    }

    // 主要功能：预览驱动器 —— 从画布快照建运行时树，按固定间隔 Tick，回收每帧状态。
    // 主要数据成员：m_root（运行时根）、m_status（节点 id → 三态）、m_blackboard、m_interval、m_accumulator。
    // 主要外部方法：Start（建树）、Tick（由窗口定时调）、Stop（清状态）。
    // 谁在用它：BehaviorTreeEditorWindow。
    // 注意：只跑在编辑器；**不写回画布数据**（三色只给节点视图加样式类）。
    public sealed class BTPreviewRunner
    {
        // 运行时根节点；null = 没在预览
        private BTPreviewNode m_root;

        // 节点 id → 最近一次状态（窗口据此上色）
        private readonly Dictionary<string, BTPreviewStatus> m_status =
            new Dictionary<string, BTPreviewStatus>();

        // 黑板（用户填的测试值）
        private readonly BTBlackboard m_blackboard = new BTBlackboard();

        // 每次 Tick 的间隔（秒）；需求要求 200ms
        private const float IntervalSeconds = 0.2f;

        // 距上次 Tick 累积的时间（秒）
        private float m_accumulator;

        // 是否正在预览
        public bool IsRunning => m_root != null;

        // 黑板（窗口暴露给右侧面板）
        public BTBlackboard Blackboard => m_blackboard;

        // 最近一次状态表（窗口拿去上色）
        public IReadOnlyDictionary<string, BTPreviewStatus> Status => m_status;

        // 功能：用画布数据快照开始预览。
        // 入参：data 树数据（内部会再 Clone 一次，确保与画布彻底分离）；error 失败原因
        // 返回：true = 已开始
        // 步骤：
        //   1) 校验树结构（唯一根、无环）
        //   2) 递归建运行时节点
        //   3) 清状态、清计时
        // 上游：窗口的【预览】按钮
        // 下游：BuildNode
        public bool Start(BTGraphData data, out string error)
        {
            m_status.Clear();
            m_accumulator = 0f;
            m_root = null;

            if (data == null)
            {
                error = "没有可预览的树数据。";
                return false;
            }

            if (!data.Validate(out error))
            {
                return false;
            }

            BTGraphData snapshot = data.Clone();
            string rootId = string.IsNullOrEmpty(snapshot.RootNodeId) ? snapshot.FindRootId() : snapshot.RootNodeId;

            if (string.IsNullOrEmpty(rootId) || !snapshot.Nodes.ContainsKey(rootId))
            {
                error = "找不到根节点。";
                return false;
            }

            m_root = BuildNode(snapshot, rootId);
            m_root.Reset();
            error = string.Empty;
            return true;
        }

        // 功能：停止预览并清空运行状态（**不动黑板**）。
        // 入参：无
        // 返回：无
        // 步骤：丢根、清状态与计时
        // 上游：窗口的【停止预览】按钮 / OnDisable
        // 下游：无
        // 注意：黑板是用户填的测试值，停预览不该清 —— 清了会出现「面板显示 100，条件读到 0」。
        public void Stop()
        {
            m_root = null;
            m_status.Clear();
            m_accumulator = 0f;
        }

        // 功能：按 200ms 间隔推进一帧（窗口每帧调它，内部自己限流）。
        // 入参：deltaSeconds 距上一帧的秒数
        // 返回：true = 这一次真的 Tick 了（窗口据此刷新颜色）
        // 步骤：
        //   1) 没在预览就返回 false
        //   2) 累积时间；不到间隔也返回 false
        //   3) 扣黑板冷却、跑根节点、记录每个节点状态
        // 上游：窗口的 EditorApplication.update
        // 下游：BTPreviewNode.Tick
        public bool Tick(float deltaSeconds)
        {
            if (m_root == null)
            {
                return false;
            }

            m_accumulator += deltaSeconds;

            if (m_accumulator < IntervalSeconds)
            {
                return false;
            }

            float step = m_accumulator;
            m_accumulator = 0f;

            // 技能冷却随时间回落（预览用的简化规则）
            float cooldown = m_blackboard.GetFloat("skillCd");

            if (cooldown > 0f)
            {
                m_blackboard.Set("skillCd", BTParamValue.FromFloat(Math.Max(0f, cooldown - step)));
            }

            m_status.Clear();
            m_root.Tick(m_blackboard, step);
            CollectStatus(m_root);
            return true;
        }

        // 功能：递归收集每个节点的最近状态（组合节点也要上色）。
        // 入参：node 运行时节点
        // 返回：该节点的状态
        // 步骤：先跑一次拿状态（子节点状态已在前面跑到）→ 对每个子节点递归
        // 上游：Tick
        // 下游：m_status
        // 注意：组合节点的状态取"再跑一次"的结果会重复执行动作节点，所以这里**只回填已算过的状态**：
        //       父节点在 Tick 时会把子节点状态留在 m_status 里，这里只补"没被记录"的组合节点。
        private void CollectStatus(BTPreviewNode node)
        {
            m_status[node.NodeId] = node.LastStatus;

            for (int i = 0; i < node.Children.Count; i++)
            {
                CollectStatus(node.Children[i]);
            }
        }

        // 功能：按画布数据递归建运行时节点。
        // 入参：data 树快照；id 当前节点 id
        // 返回：运行时节点
        // 步骤：建节点 → 拷类型与参数 → 递归子节点
        // 上游：Start
        // 下游：无
        // 注意：用 visited 防环（数据已校验过，这里只是兜底）
        private BTPreviewNode BuildNode(BTGraphData data, string id)
        {
            BTPreviewNode node = new BTPreviewNode { NodeId = id };

            if (!data.Nodes.TryGetValue(id, out BTNodeData source))
            {
                return node;
            }

            node.NodeType = source.NodeType;

            foreach (KeyValuePair<string, BTParamValue> pair in source.Params)
            {
                node.Params[pair.Key] = pair.Value;
            }

            for (int i = 0; i < source.Children.Count; i++)
            {
                node.Children.Add(BuildNode(data, source.Children[i]));
            }

            return node;
        }
    }
}
