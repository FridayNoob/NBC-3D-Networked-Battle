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
//
// ---------------------------------------------------------------------------
//  ⚠️ 2026-09-23（M3-S5）：**速率与版本号不再各写一个数，改成派生自协议契约**
// ---------------------------------------------------------------------------
//  原来是三处各写一个数：`ContractVersion = 1`、`LogicTickRate = 30`、`SnapshotSendRate = 20`。
//  而同在共享层的 `NetContract` 已经写着 `Version = 1`、`TickRate = 30`。
//  两个来源报同一个数，就一定会有一天各说各话（本项目已经把这条写进 D1 的判据了：
//  **两份拷贝一定会漂移，而漂移不报错**）。
//
//  这次实锤了一次：`SnapshotSendRate = 20` 与 Docs\25 的 D5"**每 tick 全量快照**"（= 30）矛盾，
//  而那个 20 **一行代码都没用到** —— 它唯一的作用就是在日志里显示一个错数字。
//
//  处置（三件）：
//    ① `LogicTickRate` / `ContractVersion` **派生自 `NetContract`** —— 从"两处要对齐"变成"只有一处"
//    ② 删掉 `SnapshotSendRate`：M3 的快照**每 tick 发一张**（30Hz，理由见 Docs\25 §8.5）；
//       将来真要降频（M5 的差分/压缩），那时再**按需要**加一个能兑现的常量
//    ③ 那条"两个常量必须相等"的测试随之删掉（派生之后它恒真 —— **一个永远不会红的检查 = 死检查**），
//       换成"标识里只允许出现一个速率"（防止有人再加一个）
// ============================================================================

using NBC.Shared.Net;

namespace NBC.Shared
{
    /// <summary>
    /// 共享层信息。用于验证双端（Unity 客户端 / .NET 服务端）能引用同一份程序集。
    /// </summary>
    public static class SharedInfo
    {
        /// <summary>共享层名称标识。</summary>
        public const string LayerName = "NBC.Shared";

        /// <summary>
        /// 共享层契约版本。**派生自 <see cref="NetContract.Version"/>**（协议契约是唯一来源）。
        /// </summary>
        public const int ContractVersion = NetContract.Version;

        /// <summary>
        /// 逻辑帧率（Hz）。**派生自 <see cref="NetContract.TickRate"/>**。
        /// <para>客户端与服务端必须一致，否则帧同步必然失败 —— 派生之后它不可能不一致。</para>
        /// </summary>
        public const int LogicTickRate = NetContract.TickRate;

        /// <summary>返回一行可读的版本摘要，便于启动日志输出。</summary>
        /// <returns>版本摘要。</returns>
        public static string Describe()
        {
            return LayerName + " v" + ContractVersion + " (LogicTick=" + LogicTickRate + "Hz)";
        }
    }
}
