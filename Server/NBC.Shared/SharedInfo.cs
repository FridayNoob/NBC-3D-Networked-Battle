// ============================================================================
//  NBC.Shared —— 共享层标识
//  项目：3D联网战斗Demo
//
//  这个类型本身没有业务含义，它的作用是【证明共享层装配正确】：
//  服务端与 Unity 客户端都应能编译并访问到它。
//  若任一侧找不到 SharedInfo，说明 NBC.Shared 的目标框架或引用配置有问题。
//  （需求文档 §4.2 R5：双端共享同一份战斗逻辑）
// ============================================================================

namespace NBC.Shared;

/// <summary>
/// 共享层信息。用于验证双端（Unity 客户端 / .NET 服务端）能引用同一份程序集。
/// </summary>
public static class SharedInfo
{
    /// <summary>共享层名称标识。</summary>
    public const string LayerName = "NBC.Shared";

    /// <summary>共享层契约版本。双端不一致时应拒绝联机（协议版本握手，见 PROTO-04）。</summary>
    public const int ContractVersion = 1;

    /// <summary>逻辑帧率（Hz）。客户端与服务端必须一致，否则帧同步必然失败。</summary>
    public const int LogicTickRate = 30;

    /// <summary>状态同步快照下发率（Hz）。</summary>
    public const int SnapshotSendRate = 20;

    /// <summary>返回一行可读的版本摘要，便于启动日志输出。</summary>
    public static string Describe()
        => $"{LayerName} v{ContractVersion} (LogicTick={LogicTickRate}Hz, Snapshot={SnapshotSendRate}Hz)";
}
