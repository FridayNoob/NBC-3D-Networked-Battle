// 这个文件里有：
//   SharedInfo（静态类）：双端共享层的名称、契约版本与逻辑帧率标识，外加一行版本摘要，用来证明装配正确。
//
// 注意：本目录是双端共享逻辑的唯一源码位置：Unity 侧由 NBC.Shared.asmdef 编译（noEngineReferences），服务端由 NBC.Shared.csproj 的 Compile Include 指向同一份文件，改一处两边都变，不要拷贝第二份。
// 注意：这里只许用 C# 9 及更早的语法（Unity 2022.3 的上限）；服务端已把 LangVersion 钉成 9.0，写新语法服务端会当场编译失败。

using NBC.Shared.Net;

namespace NBC.Shared
{
    /// <summary>共享层的公开标识常量：名称、契约版本与逻辑帧率，双端读的是同一组值。</summary>
    // 主要功能：用三个常量给出共享层的名称、契约版本与逻辑帧率，再加一行摘要，证明双端编译并引用了同一份 NBC.Shared。
    // 主要数据成员：LayerName（名称标识）、ContractVersion（契约版本，>= 1）、LogicTickRate（逻辑帧率，Hz）。
    // 主要外部方法：Describe（一行版本摘要）。
    // 谁在用它：服务端的 TickScheduler / BattleInstance 取帧率、启动检查打印摘要；Unity 侧的 SharedLayerTests 与 ProtocolTests 也拿它做断言。
    public static class SharedInfo
    {
        /// <summary>共享层名称标识，取值固定为 NBC.Shared，日志与自检拿它比对。</summary>
        public const string LayerName = "NBC.Shared";

        /// <summary>共享层契约版本，>= 1，派生自 NetContract.Version；协议契约是唯一来源，不要再手写第二个数。</summary>
        public const int ContractVersion = NetContract.Version;

        /// <summary>逻辑帧率，单位 Hz，派生自 NetContract.TickRate；双端必须一致，否则帧同步必然失败。</summary>
        public const int LogicTickRate = NetContract.TickRate;

        /// <summary>返回一行可读的版本摘要，供启动日志与自检断言使用。</summary>
        /// <returns>形如 NBC.Shared v1 (LogicTick=30Hz) 的字符串，内容随上方常量变化。</returns>
        // 功能：把名称、契约版本与逻辑帧率拼成一行，让日志里一眼看出双端是否用的是同一套版本与帧率。
        // 入参：无
        // 返回：始终非空的一行文本；只是字符串拼接，不会抛异常。
        // 步骤：
        //   1) 先拼名称与版本（v 加版本号），日志里能认出是哪个共享层
        //   2) 再补逻辑帧率（Hz）：双端帧率对不上时，这行日志就是第一现场
        // 上游：服务端启动检查（Program.cs）、Unity 侧 SharedLayerTests 与 ProtocolTests
        // 下游：无（只读上方三个常量）
        public static string Describe()
        {
            return LayerName + " v" + ContractVersion + " (LogicTick=" + LogicTickRate + "Hz)";
        }
    }
}
