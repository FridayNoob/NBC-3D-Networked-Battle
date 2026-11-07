// 这个文件里有：行为树节点的三态与基类（纯 C#，双端可用；服务端AI在用）。
//   BtStatus（枚举）：Success / Failure / Running。
//   BtNode（抽象类）：所有节点的基类，只有一个 Tick 方法。

namespace NBC.Server.Game.Ai
{
    // 节点执行结果三态（与 R023 编辑器的预览三态一致，便于对照看）
    public enum BtStatus
    {
        // 成功
        Success = 0,

        // 失败
        Failure = 1,

        // 运行中（这一帧没结束）
        Running = 2
    }

    // 主要功能：行为树节点基类 —— 声明「名字 + 一帧执行」这两件事，具体语义交给子类。
    // 主要数据成员：Name（节点类型名，用于日志/调试）。
    // 主要外部方法：Tick（跑一帧）。
    // 谁在用它：组合节点（Selector/Sequence/Parallel）、叶子节点（条件/动作）、MonsterBrain。
    // 注意：所有实现必须是**确定性**的（只用整数与逻辑帧，不用 float 随机、不读真实时间）。
    public abstract class BtNode
    {
        // 节点类型名（与 JSON 的 NodeType 一致；日志与调试用）
        public string Name { get; protected set; } = "Node";

        // 功能：跑一帧。
        // 入参：ctx 行为树上下文（世界/自己/目标/参数）
        // 返回：三态
        // 步骤：由子类实现
        // 上游：父节点或 MonsterBrain
        // 下游：子类自己的世界调用
        public abstract BtStatus Tick(BtContext ctx);
    }
}
