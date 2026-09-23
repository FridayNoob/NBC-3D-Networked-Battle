// ============================================================================
//  ITransport —— 传输层的**接缝**（上层只认"能收发消息"，不认识 TCP/UDP）
//  项目：3D联网战斗Demo   对应：M3-S2、Docs\25 §三 D1
//
//  ---------------------------------------------------------------------------
//  为什么是接缝，而不是"直接在业务代码里 new 一个 TcpClient"
//  ---------------------------------------------------------------------------
//  和 B2 的 `IAssetProvider`、E1 的 `IPerfCounterSource` 同一个套路：
//
//      上层（Game / 同步逻辑）  ->  ITransport  <-  TcpTransport（适配层）
//                                                    ↑ 以后还有 UdpTransport（M4 帧同步）
//                                                                 写测试用的 FakeTransport
//
//  回报有三条，都很具体：
//    · **游戏逻辑能在 EditMode 里测**：塞一个假传输，不用开端口、不用等网络
//    · **M4 要上 UDP 时不用改策略层**：换一个实现类即可（这正是 Q2"混合同步"要的形状）
//    · **网络模拟器（NET-07）能插在中间**：它本身就是一个"包了一层的 `ITransport`"
//
//  ---------------------------------------------------------------------------
//  ⚠️ 它住在 **NBC.Framework.Net**（而不是 `NBC.Framework`）—— 一个实测过的决定
//  ---------------------------------------------------------------------------
//  和 `Framework\Asset\`（接缝）+ `Framework.YooAsset\`（适配）的分法同源，但**多了一条理由**：
//  整个网络层**不许碰引擎**。于是 `NBC.Framework.Net.asmdef` 写的是
//
//      "references": [ "NBC.Shared" ]        ← 不引 NBC.Framework
//      "noEngineReferences": true            ← 连 UnityEngine 都不给
//
//  这两行买到的是**可执行**，不是风格：
//    · `noEngineReferences: true` 让"网络层偷偷用 UnityEngine"变成**编译错误**
//      （否则那种代码只有 Unity 能跑，而我在沙箱里一行都跑不了）
//    · 不引 `NBC.Framework`，于是 `Server\_condition-probe`（.NET 8）能把这份源码
//      **直接编进去并真跑**（回环收发、粘包、对端关闭…）——
//      这是"我改完网络代码当天就能拿到证据"的唯一办法（见 Docs\11 §四）。
//  代价：网络层用不了框架里的日志/事件中心。目前它确实不需要
//  （报错走 `Closed` 的人话原因，上层接到之后自己记日志）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 它是**拉模式（Pump）**，不是"后台线程 + 回调"
//  ---------------------------------------------------------------------------
//  `Pump()` 由调用方**每帧调一次**，一切都在调用它的那个线程上发生：
//    · 和 Unity 的主线程模型一致（回调里可以直接碰 Transform / UI）
//    · 逻辑帧与网络推进**同源**，便于对账（"这一帧收到了什么"是可复现的）
//    · 测试里可以手工 `Pump()` 到某个状态，不需要 sleep
//  代价：网络 IO 的及时性取决于 Pump 频率（30~60Hz 完全够 M3；真要更高吞吐再谈线程）。
//
//  ---------------------------------------------------------------------------
//  收发的是**载荷**，不是"原始字节流"
//  ---------------------------------------------------------------------------
//  分帧（长度前缀）由**实现**负责，而且两端用的是**同一份** `Shared\Net\FrameCodec`：
//      `Send(byte[] payload)`  → 实现内部 `FrameCodec.Encode` → 写 socket
//      `FrameReceived(byte[])` → 实现内部 `FrameDecoder` 拆好之后交出来
//  于是上层只面对"一条条消息"，永远不用管粘包/拆包。
// ============================================================================

using System;

namespace NBC.Framework.Net
{
    /// <summary>传输层：连、发、收、关（**上层只认这个接口**）。</summary>
    public interface ITransport : IDisposable
    {
        /// <summary>当前状态。</summary>
        ETransportState State { get; }

        /// <summary>收发统计（网络模拟器 / 看板 / 排查用）。</summary>
        TransportStats Stats { get; }

        /// <summary>人话描述（日志里用，例如 `tcp 127.0.0.1:7777`）。</summary>
        string Description { get; }

        /// <summary>是不是连上了（等价于 <see cref="State"/> == Connected）。</summary>
        bool IsConnected { get; }

        /// <summary>收到**一整帧**（载荷）时回调。实现保证：只在 <see cref="Pump"/> 里被调。</summary>
        event Action<byte[]> FrameReceived;

        /// <summary>断开或出错时回调（参数是一句人话原因；主动关闭时为"主动关闭"）。**至多触发一次**。</summary>
        event Action<string> Closed;

        /// <summary>
        /// 发起连接（**非阻塞**：立刻返回，连上与否靠 <see cref="Pump"/> 推进）。
        /// <para>连不上**不抛异常** —— 走 <see cref="Closed"/> 报原因（因为此刻还在异步握手中）。</para>
        /// </summary>
        /// <param name="host">主机（如 `127.0.0.1`）。</param>
        /// <param name="port">端口。</param>
        void Connect(string host, int port);

        /// <summary>
        /// 发一条消息（**载荷**；分帧由实现做）。
        /// <para>⚠️ 未连接时**必须明确报错**，不许静默丢 —— "偶尔丢一条"是最难查的一类 bug。</para>
        /// </summary>
        /// <param name="payload">载荷（可以为 null —— 那就是一条 0 长度帧）。</param>
        void Send(byte[] payload);

        /// <summary>推进一帧：收字节 → 拆帧 → 交给 <see cref="FrameReceived"/>；顺便冲刷发送队列。</summary>
        void Pump();

        /// <summary>主动关闭（**可以重复调用**；第一次之后 State 就是 Closed）。</summary>
        void Close();
    }
}
