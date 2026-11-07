// 这个文件里有：
//   BtSelector / BtSequence / BtParallel（组合节点）：决定子节点的执行顺序与短路规则。

using System.Collections.Generic;

namespace NBC.Server.Game.Ai
{
    // 主要功能：按顺序试子节点，**第一个不是 Failure 的结果就是它的结果**（都失败才失败）。
    // 主要数据成员：Children（子节点列表，顺序有意义）。
    // 主要外部方法：AddChild、Tick。
    // 谁在用它：DefaultAiTrees / BtJsonLoader 组装树。
    // 注意：Running 也算"不是失败"，会立刻返回 —— 这样同一帧不会继续试后面的分支。
    public sealed class BtSelector : BtNode
    {
        // 子节点（顺序即优先级）
        public readonly List<BtNode> Children = new List<BtNode>();

        // 功能：构造。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：无
        public BtSelector()
        {
            Name = "Selector";
        }

        // 功能：加一个子节点。
        // 入参：child 子节点
        // 返回：this（便于链式组装）
        // 步骤：加入列表
        // 上游：树构建
        // 下游：Tick
        public BtSelector AddChild(BtNode child)
        {
            Children.Add(child);
            return this;
        }

        // 功能：跑一帧。
        // 入参：ctx 上下文
        // 返回：第一个非 Failure 的子节点结果；全失败则 Failure
        // 步骤：顺序遍历，命中就返回
        // 上游：父节点 / MonsterBrain
        // 下游：各子节点 Tick
        public override BtStatus Tick(BtContext ctx)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                BtStatus status = Children[i].Tick(ctx);

                if (status != BtStatus.Failure)
                {
                    return status;
                }
            }

            return BtStatus.Failure;
        }
    }

    // 主要功能：按顺序跑子节点，**遇到 Failure 立刻失败，遇到 Running 立刻返回运行中**，全成功才成功。
    // 主要数据成员：Children。
    // 主要外部方法：AddChild、Tick。
    // 谁在用它：树的「前置条件 + 动作」组合（如：有目标 && 在射程 && 冷却好 → 攻击）。
    // 注意：Sequence 里的**条件节点不应有副作用**（否则会被反复执行）。
    public sealed class BtSequence : BtNode
    {
        // 子节点（顺序即执行顺序）
        public readonly List<BtNode> Children = new List<BtNode>();

        // 功能：构造。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：无
        public BtSequence()
        {
            Name = "Sequence";
        }

        // 功能：加一个子节点。
        // 入参：child 子节点
        // 返回：this（链式）
        // 步骤：加入列表
        // 上游：树构建
        // 下游：Tick
        public BtSequence AddChild(BtNode child)
        {
            Children.Add(child);
            return this;
        }

        // 功能：跑一帧。
        // 入参：ctx 上下文
        // 返回：Success / Failure / Running
        // 步骤：顺序执行，Failure 短路、Running 短路
        // 上游：父节点
        // 下游：各子节点 Tick
        public override BtStatus Tick(BtContext ctx)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                BtStatus status = Children[i].Tick(ctx);

                if (status == BtStatus.Failure)
                {
                    return BtStatus.Failure;
                }

                if (status == BtStatus.Running)
                {
                    return BtStatus.Running;
                }
            }

            return BtStatus.Success;
        }
    }

    // 主要功能：一帧内跑所有子节点，**有 Failure 就失败，全 Success 才成功，否则运行中**。
    // 主要数据成员：Children。
    // 主要外部方法：AddChild、Tick。
    // 谁在用它：需要同一帧同时推进的行为（如「一边走一边检测」）。
    // 注意：和 Selector/Sequence 一样，每次 Tick 都会访问所有子节点（成本随子节点数线性增长）。
    public sealed class BtParallel : BtNode
    {
        // 子节点
        public readonly List<BtNode> Children = new List<BtNode>();

        // 功能：构造。
        // 入参：无
        // 返回：无
        // 步骤：设名字
        // 上游：树构建
        // 下游：无
        public BtParallel()
        {
            Name = "Parallel";
        }

        // 功能：加一个子节点。
        // 入参：child 子节点
        // 返回：this（链式）
        // 步骤：加入列表
        // 上游：树构建
        // 下游：Tick
        public BtParallel AddChild(BtNode child)
        {
            Children.Add(child);
            return this;
        }

        // 功能：跑一帧。
        // 入参：ctx 上下文
        // 返回：有失败→Failure；全成功→Success；否则 Running
        // 步骤：遍历所有子节点，记录是否还有 Running
        // 上游：父节点
        // 下游：各子节点 Tick
        public override BtStatus Tick(BtContext ctx)
        {
            bool anyRunning = false;

            for (int i = 0; i < Children.Count; i++)
            {
                BtStatus status = Children[i].Tick(ctx);

                if (status == BtStatus.Failure)
                {
                    return BtStatus.Failure;
                }

                if (status == BtStatus.Running)
                {
                    anyRunning = true;
                }
            }

            return anyRunning ? BtStatus.Running : BtStatus.Success;
        }
    }
}
