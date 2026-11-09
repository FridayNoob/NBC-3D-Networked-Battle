// 这个文件里有：
//   BTBlackboard（类）：预览时的简易黑板（由用户在右侧面板手填测试值）。
//   BTPreviewNodeInstance（类）：运行时的一棵预览树节点（数据 + 求值器 + 子节点）。
//   BTPreviewRunner（类）：按 200ms 驱动预览、回收四态。
//
// 【仅编辑器】只是"给策划看的假执行"，绝不参与正式游戏与服务端逻辑。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.EditorTools.BehaviorTree
{
    // 主要功能：预览时条件节点读的「黑板」——用户在右侧面板手填测试值（hp / targetId / skillCd / inRange / homeFar）。
    // 主要数据成员：m_values（键 → 参数值）。
    // 主要外部方法：Set / GetInt / GetFloat / TryGet / Clear。
    // 谁在用它：预览求值器、BTBlackboardView（编辑）。
    // 注意：黑板只在预览期有效，**不序列化、不导出**。
    public sealed class BTBlackboard
    {
        // 键 → 值
        private readonly Dictionary<string, BTParamValue> m_values = new Dictionary<string, BTParamValue>();

        // 功能：设置一个黑板值。
        // 入参：key 键；value 值
        // 返回：无
        // 步骤：直接写字典
        // 上游：黑板面板 / 求值器写回
        // 下游：求值器读取
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
        // 上游：求值器
        // 下游：无
        public int GetInt(string key)
        {
            return m_values.TryGetValue(key, out BTParamValue value) ? value.AsInt() : 0;
        }

        // 功能：读浮点（缺省 0）。
        // 入参：key 键
        // 返回：浮点值
        // 步骤：查字典，命中就 AsFloat
        // 上游：求值器
        // 下游：无
        public float GetFloat(string key)
        {
            return m_values.TryGetValue(key, out BTParamValue value) ? value.AsFloat() : 0f;
        }

        // 功能：取原始参数值（界面显示用）。
        // 入参：key 键；value 命中的值
        // 返回：true = 键存在
        // 步骤：查字典
        // 上游：黑板面板 / Flag 求值器
        // 下游：无
        public bool TryGet(string key, out BTParamValue value)
        {
            return m_values.TryGetValue(key, out value);
        }

        // 功能：清空黑板。
        // 入参：无
        // 返回：无
        // 步骤：清字典
        // 上游：窗口关闭 / 停止预览（注意：Stop 不清，只有清空画布等显式操作才清）
        // 下游：无
        public void Clear()
        {
            m_values.Clear();
        }
    }

    // 运行时的一棵预览树节点：持有数据、SO 定义、求值器与子节点
    internal sealed class BTPreviewNodeInstance
    {
        // 节点 id（回传状态用）
        public string NodeId;

        // 节点数据（读 Params）
        public BTNodeData Data;

        // SO 定义（读预览配方）
        public BTNodeEntry Entry;

        // 求值器；null = 未实现（灰态）
        public IBtPreviewNode Behavior;

        // 子节点（顺序与数据一致）
        public List<BTPreviewNodeInstance> Children = new List<BTPreviewNodeInstance>();

        // 功能：跑一帧并把状态回写给调用方。
        // 入参：blackboard 黑板；deltaSeconds 帧间隔；statusMap 状态收集表
        // 返回：本节点状态
        // 步骤：
        //   1) 没有求值器 ⇒ NotPreviewed（灰）
        //   2) 组装上下文（含驱动子节点的委托）
        //   3) 调求值器；异常兜成 Failure 并写日志
        //   4) 成功时按定义做「写回黑板」（如 CastSkill 写 skillCd）
        //   5) 记录到 statusMap
        // 上游：BTPreviewRunner.Tick（根）/ 父节点
        // 下游：IBtPreviewNode.Tick
        public BTPreviewStatus Tick(BTBlackboard blackboard, float deltaSeconds,
            Dictionary<string, BTPreviewStatus> statusMap)
        {
            if (Behavior == null)
            {
                statusMap[NodeId] = BTPreviewStatus.NotPreviewed;
                return BTPreviewStatus.NotPreviewed;
            }

            BTPreviewContext context = new BTPreviewContext
            {
                Blackboard = blackboard,
                DeltaSeconds = deltaSeconds,
                Data = Data,
                Entry = Entry,
                TickChildren = () =>
                {
                    BTPreviewStatus[] result = new BTPreviewStatus[Children.Count];

                    for (int i = 0; i < Children.Count; i++)
                    {
                        result[i] = Children[i].Tick(blackboard, deltaSeconds, statusMap);
                    }

                    return result;
                }
            };

            BTPreviewStatus status;

            try
            {
                status = Behavior.Tick(context);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[BT] 预览求值器异常（按失败处理）：" + NodeId + " → " + exception.Message);
                status = BTPreviewStatus.Failure;
            }

            if (status == BTPreviewStatus.Success && Entry != null && !string.IsNullOrEmpty(Entry.WriteBackKey))
            {
                float value = string.IsNullOrEmpty(Entry.WriteBackParamKey)
                    ? Entry.WriteBackConstFloat
                    : (float)BtPreviewMath.ReadParam(context, Entry.WriteBackParamKey, Entry.WriteBackConstFloat);

                blackboard.Set(Entry.WriteBackKey, BTParamValue.FromFloat(value));
            }

            statusMap[NodeId] = status;
            return status;
        }

        // 功能：重置整棵子树的求值器状态。
        // 入参：无
        // 返回：无
        // 步骤：Behavior.Reset + 递归子节点
        // 上游：BTPreviewRunner.Start
        // 下游：无
        public void Reset()
        {
            Behavior?.Reset();

            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].Reset();
            }
        }
    }

    // 主要功能：预览驱动器 —— 从画布快照建预览树，按 200ms 驱动，回收四态。
    // 主要数据成员：m_root（预览树根）、m_status（节点 id → 四态）、m_blackboard、m_accumulator。
    // 主要外部方法：Start / Tick / Stop。
    // 谁在用它：BehaviorTreeEditorWindow。
    // 注意：只跑在编辑器；**不写回画布数据**（状态只用于上色）。
    public sealed class BTPreviewRunner
    {
        // 预览树根；null = 没在预览
        private BTPreviewNodeInstance m_root;

        // 节点 id → 最近一次状态
        private readonly Dictionary<string, BTPreviewStatus> m_status = new Dictionary<string, BTPreviewStatus>();

        // 黑板
        private readonly BTBlackboard m_blackboard = new BTBlackboard();

        // Tick 间隔（秒）：200ms
        private const float IntervalSeconds = 0.2f;

        // 累积时间
        private float m_accumulator;

        // 是否正在预览
        public bool IsRunning => m_root != null;

        // 黑板
        public BTBlackboard Blackboard => m_blackboard;

        // 最近一次状态表
        public IReadOnlyDictionary<string, BTPreviewStatus> Status => m_status;

        // 最近一次状态里有没有「未实现」的节点（窗口用来提示一次）
        public bool HasNotPreviewed
        {
            get
            {
                foreach (KeyValuePair<string, BTPreviewStatus> pair in m_status)
                {
                    if (pair.Value == BTPreviewStatus.NotPreviewed)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        // 功能：用画布数据快照开始预览。
        // 入参：data 树数据（内部 Clone，彻底与画布分离）；error 失败原因
        // 返回：true = 已开始
        // 步骤：校验树 → 找根 → 递归建预览树 → 清状态与计时
        // 上游：窗口的【预览】按钮
        // 下游：BuildInstance
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

              // 先克隆再清理脏引用，最后才校验 —— 否则「删节点留下的脏引用」会直接卡在校验上（就是报错截图那种）
              BTGraphData snapshot = data.Clone();

              int dangling = snapshot.RemoveDanglingReferences();

              if (dangling > 0)
              {
              Debug.LogWarning("[BT] 预览前清理了 " + dangling + " 处指向已删除节点的引用（源数据仍脏，建议重新导入或重建）。");
              }

              if (!snapshot.Validate(out error))
              {
              return false;
              }

              string rootId = string.IsNullOrEmpty(snapshot.RootNodeId) ? snapshot.FindRootId() : snapshot.RootNodeId;

            if (string.IsNullOrEmpty(rootId) || !snapshot.Nodes.ContainsKey(rootId))
            {
                error = "找不到根节点。";
                return false;
            }

            m_root = BuildInstance(snapshot, rootId);
            m_root.Reset();
            error = string.Empty;
            return true;
        }

        // 功能：停止预览（**不清黑板**：黑板是用户填的测试值）。
        // 入参：无
        // 返回：无
        // 步骤：丢根、清状态与计时
        // 上游：窗口的【停止预览】/ OnDisable
        // 下游：无
        public void Stop()
        {
            m_root = null;
            m_status.Clear();
            m_accumulator = 0f;
        }

        // 功能：按 200ms 间隔推进一帧。
        // 入参：deltaSeconds 距上一帧的秒数
        // 返回：true = 这一次真的 Tick 了
        // 步骤：累积时间 → 不到间隔返回 → 扣技能冷却 → Tick 根 → 收集状态
        // 上游：窗口的 EditorApplication.update
        // 下游：BTPreviewNodeInstance.Tick
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

            float cooldown = m_blackboard.GetFloat("skillCd");

            if (cooldown > 0f)
            {
                m_blackboard.Set("skillCd", BTParamValue.FromFloat(Math.Max(0f, cooldown - step)));
            }

            m_status.Clear();
            m_root.Tick(m_blackboard, step, m_status);
            return true;
        }

        // 功能：按数据递归建预览树（取定义 → 取求值器 → 递归子节点）。
        // 入参：data 快照；id 节点 id
        // 返回：预览树节点
        // 步骤：
        //   1) 取节点数据与 SO 定义
        //   2) 按定义的 PreviewKind 找求值器（找不到 ⇒ null ⇒ 灰态）
        //   3) 递归子节点
        // 上游：Start
        // 下游：BTPreviewRegistry.Resolve
        private BTPreviewNodeInstance BuildInstance(BTGraphData data, string id)
        {
            BTPreviewNodeInstance instance = new BTPreviewNodeInstance { NodeId = id };

            if (!data.Nodes.TryGetValue(id, out BTNodeData nodeData))
            {
                return instance;
            }

            instance.Data = nodeData;

            BTNodeEntry entry = BTNodeRegistry.FindEntry(nodeData.NodeType);
            instance.Entry = entry;

            if (entry != null && entry.PreviewKind != BTPreviewKind.None)
            {
                instance.Behavior = BTPreviewRegistry.Resolve(entry.PreviewKind);
            }

            for (int i = 0; i < nodeData.Children.Count; i++)
            {
                instance.Children.Add(BuildInstance(data, nodeData.Children[i]));
            }

            return instance;
        }
    }
}
