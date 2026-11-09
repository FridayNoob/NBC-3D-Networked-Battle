// 这个文件里有：
//   BtSelector（类）：选择节点，按顺序跑，第一个非失败的结果就是它的结果。
//   BtSequence（类）：顺序节点，遇到失败或运行中就短路，全成功才成功。
//   BtParallel（类）：并行节点，全部跑一遍后聚合：有失败即失败，全成功才成功，否则运行中。
//
// 三个组合节点都只负责"遍历 + 聚合"，不含任何具体行为；子节点按添加顺序执行。

using System.Collections.Generic;

namespace NBC.Server.Game.Ai.Bt
{
    // 主要功能：选择节点 —— 按顺序跑子节点，第一个非 Failure 的结果直接作为结果。
    // 主要数据成员：m_children（子节点，按添加顺序）。
    // 主要外部方法：AddChild、Tick。
    // 谁在用它：R022-3 建树、R022-2 的怪物大脑。
    // 注意：子节点若返回 Success 就会打断后续分支，所以条件节点必须无副作用、动作节点不能"假装成功"。
    [BtNode("Selector")]
    public sealed class BtSelector : BtNode
    {
        // 子节点（顺序即优先级）
        private readonly List<BtNode> m_children = new List<BtNode>();

        // 类型名
        public override string TypeName => "Selector";

        // 功能：追加一个子节点。
        // 入参：child 子节点
        // 返回：无
        // 步骤：判空后加入列表
        // 上游：R022-3 建树
        // 下游：Tick
        public void AddChild(BtNode child)
        {
            if (child != null)
            {
                m_children.Add(child);
            }
        }

        // 功能：按顺序跑子节点，第一个非 Failure 即返回。
        // 入参：ctx 执行上下文
        // 返回：第一个非 Failure 的状态；全部失败则 Failure
        // 步骤：遍历子节点 → 非 Failure 立即返回 → 全失败返回 Failure
        // 上游：父节点或大脑
        // 下游：子节点.Tick
        public override BtStatus Tick(BtContext ctx)
        {
            for (int i = 0; i < m_children.Count; i++)
            {
                BtStatus status = m_children[i].Tick(ctx);

                if (status != BtStatus.Failure)
                {
                    return status;
                }
            }

            return BtStatus.Failure;
        }
    }

    // 主要功能：顺序节点 —— 依次跑子节点，遇 Failure 或 Running 立刻短路。
    // 主要数据成员：m_children（子节点）。
    // 主要外部方法：AddChild、Tick。
    // 谁在用它：R022-3 建树、R022-2 的怪物大脑。
    // 注意：Running 也短路，否则"追到一半"会把后面的攻击也执行了。
    [BtNode("Sequence")]
    public sealed class BtSequence : BtNode
    {
        // 子节点（顺序即执行顺序）
        private readonly List<BtNode> m_children = new List<BtNode>();

        // 类型名
        public override string TypeName => "Sequence";

        // 功能：追加一个子节点。
        // 入参：child 子节点
        // 返回：无
        // 步骤：判空后加入列表
        // 上游：R022-3 建树
        // 下游：Tick
        public void AddChild(BtNode child)
        {
            if (child != null)
            {
                m_children.Add(child);
            }
        }

        // 功能：依次跑子节点，遇 Failure 或 Running 立即返回。
        // 入参：ctx 执行上下文
        // 返回：Failure / Running / Success
        // 步骤：遍历子节点 → 非 Success 立即返回 → 全成功返回 Success
        // 上游：父节点或大脑
        // 下游：子节点.Tick
        public override BtStatus Tick(BtContext ctx)
        {
            for (int i = 0; i < m_children.Count; i++)
            {
                BtStatus status = m_children[i].Tick(ctx);

                if (status != BtStatus.Success)
                {
                    return status;
                }
            }

            return BtStatus.Success;
        }
    }

    // 主要功能：并行节点 —— 所有子节点都跑一遍，再按"有失败即失败、全成功才成功"聚合。
    // 主要数据成员：m_children（子节点）。
    // 主要外部方法：AddChild、Tick。
    // 谁在用它：R022-3 建树、R022-2 的怪物大脑。
    // 注意：并行只是"都跑一遍"，不是多线程；同一帧内顺序执行，保证确定性。
    [BtNode("Parallel")]
    public sealed class BtParallel : BtNode
    {
        // 子节点
        private readonly List<BtNode> m_children = new List<BtNode>();

        // 类型名
        public override string TypeName => "Parallel";

        // 功能：追加一个子节点。
        // 入参：child 子节点
        // 返回：无
        // 步骤：判空后加入列表
        // 上游：R022-3 建树
        // 下游：Tick
        public void AddChild(BtNode child)
        {
            if (child != null)
            {
                m_children.Add(child);
            }
        }

        // 功能：跑全部子节点后聚合。
        // 入参：ctx 执行上下文
        // 返回：有 Failure → Failure；全 Success → Success；否则 Running
        // 步骤：遍历累计 → 先判失败再判未完成 → 否则成功
        // 上游：父节点或大脑
        // 下游：子节点.Tick
        public override BtStatus Tick(BtContext ctx)
        {
            bool anyNotSuccess = false;

            for (int i = 0; i < m_children.Count; i++)
            {
                BtStatus status = m_children[i].Tick(ctx);

                if (status == BtStatus.Failure)
                {
                    return BtStatus.Failure;
                }

                if (status != BtStatus.Success)
                {
                    anyNotSuccess = true;
                }
            }

            return anyNotSuccess ? BtStatus.Running : BtStatus.Success;
        }
    }
}
