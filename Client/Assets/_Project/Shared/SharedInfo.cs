// ============================================================================
//  NBC.Shared —— 共享层标识
//  项目：3D联网战斗Demo
//
//  这个类型本身没有业务含义，它的作用是【证明共享层装配正确】：
//  服务端与 Unity 客户端都应能编译并访问到它。
//  若任一侧找不到 SharedInfo，说明 NBC.Shared 的目标框架或引用配置有问题。
//  （需求文档 §4.2 R5：双端共享同一份战斗逻辑）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 本目录（Client\Assets\_Project\Shared）是**双端共享逻辑的唯一源码位置**（2026-09-21 定）
//  ---------------------------------------------------------------------------
//  · **Unity 侧**：由同目录的 `NBC.Shared.asmdef` 编译（`noEngineReferences: true`）
//  · **服务端**：由 `Server\NBC.Shared\NBC.Shared.csproj` 用 `<Compile Include>` **引用同一份文件**
//
//  **只有一份代码，改一处两边都变** —— 这是刻意的：
//  帧同步最怕的就是"两端逻辑不一致"，而两份拷贝一定会漂移。
//
//  ⚠️ **这里只许用 C# 9 及更早的语法**（Unity 2022.3 的上限）。
//     服务端项目已把 `LangVersion` 钉成 9.0，写新语法**服务端会当场编译失败** ——
//     这是刻意的：**让"能编过"这件事本身就代表"Unity 也能编过"**。
//     上一版这里用了文件作用域命名空间（`namespace NBC.Shared;`，C# 10），
//     服务端编得过、**Unity 编不过** —— 那个隐患就是靠钉 LangVersion 消掉的。
// ============================================================================

namespace NBC.Shared
{
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
        /// <returns>版本摘要。</returns>
        public static string Describe()
        {
            return LayerName + " v" + ContractVersion +
                   " (LogicTick=" + LogicTickRate + "Hz, Snapshot=" + SnapshotSendRate + "Hz)";
        }
    }
}
