// ============================================================================
//  NetContract —— 双端共享的**协议契约常量**
//  项目：3D联网战斗Demo   对应：M3-S1、Docs\25 §三 D1
//
//  ---------------------------------------------------------------------------
//  它为什么住在共享层（`Shared\`）
//  ---------------------------------------------------------------------------
//  和条件系统、伤害结算是同一个理由：**两端必须算出同一个值**。
//  协议版本不一致 = "两端说的不是同一种话"，必须**在握手时就拒绝**，
//  而不是让它跑下去、在某个字段上安静地对不上。
//
//      客户端：Handshake{ protocol_version = NetContract.Version }
//      服务端：`ack.Accepted = handshake.ProtocolVersion == NetContract.Version`
//
//  ---------------------------------------------------------------------------
//  ⚠️ 改 `.proto` 时必须同步这里（**有机械检查盯着**）
//  ---------------------------------------------------------------------------
//  `Protocol\nbc_m3.proto` 顶部有一行 `// CONTRACT_VERSION: N`。
//  **不兼容改动**（删字段 / 改类型 / 改语义）要把它 +1，并同步本文件的 `Version`。
//
//  这两处一致由 `Tests\EditMode\Net\ProtocolTests.cs` **直接读那个 .proto 文件比对** ——
//  不靠记性（"改了协议忘了升版本"是典型的静默漂移：两端都能编过，跑起来才发现对不上）。
//
//  ⚠️ 什么算"不兼容"：**加字段是兼容的**（proto3 未知字段会被忽略），
//     删字段、改字段类型、改字段语义（例如把"毫米"改成"厘米"）都**不兼容**。
// ============================================================================

// 与 Shared\Condition\、Shared\Battle\ 同一处理由：本目录在 Unity（未开可空）
// 与服务端（`Server\Directory.Build.props` 开了 `<Nullable>enable</Nullable>`）下规则不同。
// 显式关掉，让**两端看到同一套规则**（详见 ConditionTracker.cs 顶部那段）。
#nullable disable

namespace NBC.Shared.Net
{
    /// <summary>协议契约常量（两端共享）。</summary>
    public static class NetContract
    {
        /// <summary>
        /// 协议版本。**必须与 `Protocol\nbc_m3.proto` 顶部的 `CONTRACT_VERSION` 一致。**
        /// </summary>
        public const int Version = 1;

        /// <summary>服务端 tick 频率（Hz）。需求 §6.1 定的是 30。</summary>
        public const int TickRate = 30;

        /// <summary>一帧的毫秒数（由 <see cref="TickRate"/> 推出来，不另外填）。</summary>
        public const int TickIntervalMs = 1000 / TickRate;

        /// <summary>房间容量上限（Q3 的决定：2~4 人）。</summary>
        public const int MaxRoomMembers = 4;

        /// <summary>一条消息的**长度前缀**占几个字节（小端 uint32）。</summary>
        public const int FrameLengthPrefixBytes = 4;

        /// <summary>单条消息的最大字节数（防"一个坏长度前缀把内存吃光"）。</summary>
        public const int MaxFrameBytes = 1024 * 512;

        /// <summary>转成一句人话（日志/报错用）。</summary>
        /// <returns>描述。</returns>
        public static string Describe()
        {
            return "协议 v" + Version + "，tick " + TickRate + "Hz（每帧 " + TickIntervalMs +
                   "ms），房间上限 " + MaxRoomMembers + " 人";
        }
    }
}
