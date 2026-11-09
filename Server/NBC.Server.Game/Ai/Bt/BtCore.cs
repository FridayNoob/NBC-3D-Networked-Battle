// 这个文件里有：
//   BtStatus（枚举）：节点一次 Tick 的三种结果。
//   IBtParamSource（接口）：节点读参数的抽象来源（JSON 解析在 R022-3 提供实现）。
//   BtNodeAttribute（特性）：把节点类登记到某个类型名，类型名与客户端 BTNodeDefinitions 一致。
//   BtNode（抽象类）：所有行为树节点的基类，只要求 Tick；参数在读配置时一次性读进字段。

using System;

namespace NBC.Server.Game.Ai.Bt
{
    // 一次 Tick 的三种结果：动作没结束时用 Running，让上层知道"本帧还没完"
    public enum BtStatus
    {
        // 成功
        Success = 0,

        // 失败
        Failure = 1,

        // 运行中（本帧未结束）
        Running = 2
    }

    // 参数来源：节点只通过它读参数，不关心背后是 JSON、字典还是测试桩
    public interface IBtParamSource
    {
        // 功能：读一个整数参数。
        // 入参：key 参数键；value 读到的值
        // 返回：true 表示存在且可解析
        // 步骤：由实现决定
        // 上游：BtNode.Configure
        // 下游：无
        bool TryGetInt(string key, out int value);

        // 功能：读一个布尔参数。
        // 入参：key 参数键；value 读到的值
        // 返回：true 表示存在且可解析
        // 步骤：由实现决定
        // 上游：BtNode.Configure
        // 下游：无
        bool TryGetBool(string key, out bool value);

        // 功能：读一个浮点参数。
        // 入参：key 参数键；value 读到的值
        // 返回：true 表示存在且可解析
        // 步骤：由实现决定
        // 上游：BtNode.Configure
        // 下游：无
        bool TryGetFloat(string key, out float value);
    }

    // 节点类型名：与客户端 BTNodeDefinitions.asset 里的 TypeName 完全一致，发布后不许改名
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class BtNodeAttribute : Attribute
    {
        // 类型名（JSON 的 NodeType）
        public string TypeName { get; }

        // 功能：登记类型名。
        // 入参：typeName 与客户端一致的节点类型名
        // 返回：无
        // 步骤：保存
        // 上游：节点类声明处
        // 下游：R022-3 的工厂扫描
        public BtNodeAttribute(string typeName)
        {
            TypeName = typeName;
        }
    }

    // 主要功能：所有节点的基类 —— 只要求实现 Tick，参数在读配置时读进字段。
    // 主要数据成员：无（子类自己存参数）。
    // 主要外部方法：Configure（读参数）、Tick（跑一帧）。
    // 谁在用它：BtComposites、Conditions、R022-3 的工厂、R022-2 的大脑。
    // 注意：GetTypeName 是抽象属性而不是字段，避免子类忘记赋值；条件节点 Tick 内不得修改 ctx 或世界。
    public abstract class BtNode
    {
        // 节点类型名（与特性上的名字一致，写日志用）
        public abstract string TypeName { get; }

        // 功能：读参数（只在建树时调用一次）。
        // 入参：source 参数来源
        // 返回：无
        // 步骤：由子类实现；本基类默认什么都不读
        // 上游：R022-3 建节点后立即调用
        // 下游：子类字段
        public virtual void Configure(IBtParamSource source)
        {
        }

        // 功能：跑一帧。
        // 入参：ctx 执行上下文
        // 返回：三态之一
        // 步骤：由子类实现
        // 上游：父节点或大脑的根
        // 下游：无
        public abstract BtStatus Tick(BtContext ctx);
    }
}
