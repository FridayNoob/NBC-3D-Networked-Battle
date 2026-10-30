// 这个文件里有：
//   NetContract（静态类）：双端共享的协议契约常量 —— 版本号、tick 频率、分帧长度前缀与上限、房间容量，外加一句人话描述

// 与 Shared\Condition\、Shared\Battle\ 同一处理：本目录在 Unity（未开可空）与服务端（Server\Directory.Build.props 开了可空引用）下规则不同。
// 显式关掉，让两端看到同一套规则（详见 ConditionTracker.cs 顶部那段）。
#nullable disable

namespace NBC.Shared.Net
{
    /// <summary>双端共享的协议契约常量：版本号、tick 频率、分帧上限与房间容量。</summary>
    // 主要功能：把两端必须完全一致的协议数字集中到一处，改协议时只改这里。
    // 主要数据成员：Version（协议版本，正整数）、TickRate（tick 频率，Hz）、TickIntervalMs（每帧毫秒）、MaxRoomMembers（房间上限，人）、FrameLengthPrefixBytes（长度前缀字节数）、MaxFrameBytes（单帧上限，字节）。
    // 主要外部方法：Describe（把上面几个数拼成一句人话）；其余成员都是常量，没有方法。
    // 谁在用它：两端共用 —— 客户端 NetSession 握手时填 Version，TcpTransport 与 FrameCodec 读分帧常量；服务端 ServerMessageRouter 校验版本，RoomRegistry 用房间容量，DungeonBattle 用 TickRate 折算每 tick 位移，RoomBattleService 用 TickIntervalMs；ProtocolTests 与 SkeletonSelfCheck 拿它与 .proto、SharedInfo 对账。
    // 注意：改 Protocol\nbc_m3.proto 顶部的 CONTRACT_VERSION 时必须同步这里的 Version —— Tests\EditMode\Net\ProtocolTests.cs 直接读那个 .proto 文件比对，不靠记性。
    // 注意：加字段是兼容的（proto3 未知字段会被忽略）；删字段、改字段类型、改字段语义（例如把毫米改成厘米）都不兼容，必须把版本 +1。
    public static class NetContract
    {
        /// <summary>协议版本号，正整数（从 1 起）；不兼容改动时 +1，并与 .proto 顶部的 CONTRACT_VERSION 一起改。</summary>
        public const int Version = 1;

        /// <summary>逻辑 tick 频率，单位 Hz；取值 30，服务端按它广播、客户端按它步进，改它等于改协议。</summary>
        public const int TickRate = 30;

        /// <summary>一帧的毫秒数，单位 ms；由 1000 / TickRate 推出（30Hz 时是 33），不另外填。</summary>
        public const int TickIntervalMs = 1000 / TickRate;

        /// <summary>房间容量上限，单位人；取值 2..4（当前定为 4）。</summary>
        public const int MaxRoomMembers = 4;

        /// <summary>长度前缀占几个字节，单位字节；固定 4（小端 uint32），收发两端同一口径。</summary>
        public const int FrameLengthPrefixBytes = 4;

        /// <summary>单条消息的最大字节数，单位字节；524288（512 KB），防止一个坏长度前缀把内存吃光。</summary>
        public const int MaxFrameBytes = 1024 * 512;

        /// <summary>把协议版本、tick 频率、每帧毫秒与房间容量拼成一句人话，供启动横幅与日志用。</summary>
        /// <returns>一句中文描述，只由本类常量拼成，随常量变化。</returns>
        // 功能：把版本号、tick 频率、每帧毫秒与房间容量拼成一句话，便于两端日志直接对照。
        // 入参：无
        // 返回：一句中文描述；不含时间戳等易变信息，两端版本不同时差异只落在版本号上。
        // 步骤：
        //   1) 以「协议 vN」开头 —— 握手被拒时第一眼看的就是版本号
        //   2) 再接 tick 频率与每帧毫秒（后者由前者算出，两者不会互相打架）
        //   3) 最后接房间容量
        // 上游：服务端启动横幅（NBC.Server.Host 的 Program）、联调探针（Server\_net-probe 的 Program）
        // 下游：无（只读本类的常量）
        public static string Describe()
        {
            return "协议 v" + Version + "，tick " + TickRate + "Hz（每帧 " + TickIntervalMs +
                   "ms），房间上限 " + MaxRoomMembers + " 人";
        }
    }
}
