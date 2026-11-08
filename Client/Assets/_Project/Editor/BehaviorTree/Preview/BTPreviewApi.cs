// 这个文件里有：
//   BTPreviewStatus（枚举）：预览四态（成功 / 失败 / 运行中 / 未实现）。
//   BTPreviewContext（类）：一次求值需要的上下文（黑板、帧间隔、节点数据、定义、驱动子节点）。
//   IBtPreviewNode（接口）：预览求值器的契约（Tick + Reset）。
//   BtPreviewKindAttribute（特性）：把求值器注册到某个 BTPreviewKind。
//   BTPreviewRegistry（静态类）：扫描并缓存「配方 → 求值器工厂」。
//
// 【仅编辑器】本文件在 NBC.Editor 里，不参与打包；它只是"给策划看的假执行"。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NBC.EditorTools.BehaviorTree
{
    // 预览四态：比三态多一个「未实现」（灰），用于"策划先加定义、程序后补行为"的阶段
    public enum BTPreviewStatus
    {
        // 成功（绿）
        Success = 0,

        // 失败（红）
        Failure = 1,

        // 运行中（黄）
        Running = 2,

        // 未实现（灰）：没有对应求值器，或求值器明确返回它；**不算失败**
        NotPreviewed = 3
    }

    // 一次求值需要的全部上下文（由 BTPreviewNodeInstance 组装，求值器只读）
    public sealed class BTPreviewContext
    {
        // 黑板（用户填的测试值）
        public BTBlackboard Blackboard;

        // 距上一帧的秒数（200ms 节奏由 Runner 控制）
        public float DeltaSeconds;

        // 当前节点的数据（读 Params）
        public BTNodeData Data;

        // 当前节点的 SO 定义（读 PreviewKey / CompareOp / CompareParamKey 等配方字段）
        public BTNodeEntry Entry;

        // 组合节点用：驱动所有子节点并返回它们的状态（顺序与 Children 一致）
        public Func<BTPreviewStatus[]> TickChildren;

        // 求值器可以往这里写一句说明（状态栏显示）
        public string Detail;
    }

    // 预览求值器契约
    public interface IBtPreviewNode
    {
        // 功能：算这一帧的状态。
        // 入参：ctx 上下文
        // 返回：四态之一
        // 步骤：由实现决定
        // 上游：BTPreviewNodeInstance.Tick
        // 下游：无
        // 注意：实现**不得抛异常**；算不出来就返回 NotPreviewed（Runner 会把异常兜成 Failure 并写日志）
        BTPreviewStatus Tick(BTPreviewContext ctx);

        // 功能：重置内部计时/缓存（开始新一轮预览时调）。
        // 入参：无
        // 返回：无
        // 步骤：由实现决定
        // 上游：BTPreviewRunner.Start
        // 下游：无
        void Reset();
    }

    // 把求值器挂到某个配方上
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BtPreviewKindAttribute : Attribute
    {
        // 这个求值器负责的配方
        public BTPreviewKind Kind { get; }

        // 功能：构造。
        // 入参：kind 配方
        // 返回：无
        // 步骤：记下配方
        // 上游：求值器类的声明处
        // 下游：BTPreviewRegistry 扫描
        public BtPreviewKindAttribute(BTPreviewKind kind)
        {
            Kind = kind;
        }
    }

    // 主要功能：把「配方 → 求值器」的映射用特性扫描建起来，Runner 只按配方取，不写 switch。
    // 主要数据成员：s_factories（配方 → 创建函数）、s_scanned（是否已扫描）。
    // 主要外部方法：Resolve（取新实例；没有返回 null ⇒ 灰态）、Invalidate（域重载后重扫）。
    // 谁在用它：BTPreviewNodeInstance。
    // 注意：每次 Resolve 都 new 一个实例 —— 求值器可能有自己的计时状态（如 Timed），不能共享。
    public static class BTPreviewRegistry
    {
        // 配方 → 创建函数
        private static readonly Dictionary<BTPreviewKind, Func<IBtPreviewNode>> s_factories =
            new Dictionary<BTPreviewKind, Func<IBtPreviewNode>>();

        // 是否已扫描
        private static bool s_scanned;

        // 功能：按配方取一个**新**求值器实例。
        // 入参：kind 配方
        // 返回：实例；没有对应求值器返回 null（调用方当灰态处理）
        // 步骤：确保已扫描 → 查表 → 调用工厂
        // 上游：BTPreviewNodeInstance 构造
        // 下游：无
        public static IBtPreviewNode Resolve(BTPreviewKind kind)
        {
            EnsureScanned();

            return s_factories.TryGetValue(kind, out Func<IBtPreviewNode> factory) ? factory() : null;
        }

        // 功能：清空扫描缓存（Unity 域重载后调用）。
        // 入参：无
        // 返回：无
        // 步骤：清表 + 置未扫描
        // 上游：预览开始前 / 初始化
        // 下游：无
        public static void Invalidate()
        {
            s_factories.Clear();
            s_scanned = false;
        }

        // 功能：扫描当前程序集里带 [BtPreviewKind] 的类并登记。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 遍历本程序集类型
        //   2) 找实现 IBtPreviewNode 且带特性、有公共无参构造的类型
        //   3) 登记创建函数（同配方后出现的覆盖先出现的，便于扩展）
        // 上游：Resolve
        // 下游：无
        private static void EnsureScanned()
        {
            if (s_scanned)
            {
                return;
            }

            s_scanned = true;

            Assembly assembly = typeof(BTPreviewRegistry).Assembly;
            Type[] types = assembly.GetTypes();

            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];

                if (type.IsAbstract || !typeof(IBtPreviewNode).IsAssignableFrom(type))
                {
                    continue;
                }

                BtPreviewKindAttribute attribute = type.GetCustomAttribute<BtPreviewKindAttribute>();

                if (attribute == null)
                {
                    continue;
                }

                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    continue;
                }

                BTPreviewKind kind = attribute.Kind;
                s_factories[kind] = () => (IBtPreviewNode)Activator.CreateInstance(type);
            }
        }
    }
}
