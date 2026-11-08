// 这个文件里有：
//   三个组合节点的预览求值器：Selector / Sequence / Parallel。
//
// 【仅编辑器】组合节点必须驱动子节点（通过 ctx.TickChildren），它们不关心子节点是什么类型。

namespace NBC.EditorTools.BehaviorTree
{
    // Selector：按顺序跑，第一个非 Failure 的结果就是它的结果
    [BtPreviewKind(BTPreviewKind.CompositeSelector)]
    internal sealed class SelectorPreview : IBtPreviewNode
    {
        // 功能：驱动子节点并做「第一个非失败就赢」的聚合。
        // 入参：ctx 上下文（TickChildren 由 Runner 提供）
        // 返回：第一个非 Failure 的状态；全部失败则 Failure
        // 步骤：调 ctx.TickChildren 拿状态数组 → 顺序找第一个非 Failure
        // 上游：BTPreviewNodeInstance.Tick
        // 下游：ctx.TickChildren
        public BTPreviewStatus Tick(BTPreviewContext ctx)
        {
            BTPreviewStatus[] statuses = ctx.TickChildren != null ? ctx.TickChildren() : new BTPreviewStatus[0];

            for (int i = 0; i < statuses.Length; i++)
            {
                if (statuses[i] != BTPreviewStatus.Failure)
                {
                    return statuses[i];
                }
            }

            return BTPreviewStatus.Failure;
        }

        // 功能：无需重置（无状态）。
        // 入参：无
        // 返回：无
        // 步骤：空实现
        // 上游：BTPreviewRunner.Start
        // 下游：无
        public void Reset()
        {
        }
    }

    // Sequence：按顺序跑，遇 Failure 短路、遇 Running 短路，全成功才成功
    [BtPreviewKind(BTPreviewKind.CompositeSequence)]
    internal sealed class SequencePreview : IBtPreviewNode
    {
        // 功能：驱动子节点并做顺序聚合。
        // 入参：ctx 上下文
        // 返回：Failure / Running / Success
        // 步骤：遍历状态数组，遇到 Failure 或 Running 立即返回
        // 上游：BTPreviewNodeInstance.Tick
        // 下游：ctx.TickChildren
        public BTPreviewStatus Tick(BTPreviewContext ctx)
        {
            BTPreviewStatus[] statuses = ctx.TickChildren != null ? ctx.TickChildren() : new BTPreviewStatus[0];

            for (int i = 0; i < statuses.Length; i++)
            {
                if (statuses[i] == BTPreviewStatus.Failure || statuses[i] == BTPreviewStatus.Running)
                {
                    return statuses[i];
                }
            }

            return BTPreviewStatus.Success;
        }

        // 功能：无需重置（无状态）。
        // 入参：无
        // 返回：无
        // 步骤：空实现
        // 上游：BTPreviewRunner.Start
        // 下游：无
        public void Reset()
        {
        }
    }

    // Parallel：全跑，有 Failure 就失败，全 Success 才成功，否则运行中
    [BtPreviewKind(BTPreviewKind.CompositeParallel)]
    internal sealed class ParallelPreview : IBtPreviewNode
    {
        // 功能：驱动子节点并做并行聚合。
        // 入参：ctx 上下文
        // 返回：Failure / Running / Success
        // 步骤：先看有没有 Failure；再看有没有非 Success
        // 上游：BTPreviewNodeInstance.Tick
        // 下游：ctx.TickChildren
        public BTPreviewStatus Tick(BTPreviewContext ctx)
        {
            BTPreviewStatus[] statuses = ctx.TickChildren != null ? ctx.TickChildren() : new BTPreviewStatus[0];
            bool anyNotSuccess = false;

            for (int i = 0; i < statuses.Length; i++)
            {
                if (statuses[i] == BTPreviewStatus.Failure)
                {
                    return BTPreviewStatus.Failure;
                }

                if (statuses[i] != BTPreviewStatus.Success)
                {
                    anyNotSuccess = true;
                }
            }

            return anyNotSuccess ? BTPreviewStatus.Running : BTPreviewStatus.Success;
        }

        // 功能：无需重置（无状态）。
        // 入参：无
        // 返回：无
        // 步骤：空实现
        // 上游：BTPreviewRunner.Start
        // 下游：无
        public void Reset()
        {
        }
    }
}
