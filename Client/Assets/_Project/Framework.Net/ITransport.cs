// 这个文件里有：
//   ITransport（接口）：传输层的接缝，只约定连接、发送、收帧与关闭，不认 TCP 还是 UDP。
//
// 注意：本程序集是 noEngineReferences 且只引 NBC.Shared —— 网络层一旦碰 UnityEngine 就是编译错误；
//       换来的是这份源码能被 .NET 8 探针直接编进去真跑，改完当天就能拿到回环收发的证据。
// 注意：整套接口是拉模式，FrameReceived / Closed 只在 Pump 里、由调用 Pump 的那条线程触发，
//       所以回调里可以直接碰 Transform 与 UI。
// 注意：收发的是载荷，长度前缀分帧由实现负责，两端共用 Shared\Net\FrameCodec，
//       上层永远不用管粘包与拆包。

using System;

namespace NBC.Framework.Net
{
    // 主要功能：约定一条网络连接的完整生命周期——连接、发载荷、每帧推进收帧、关闭，并暴露状态、统计与人话描述。
    // 主要数据成员：State（取值见 ETransportState）、Stats（收发字节与帧数的累计值）、Description（人话端点描述）、IsConnected（是否已连上）。
    // 主要外部方法：Connect / Send / Pump / Close，两个事件 FrameReceived / Closed，外加继承来的 Dispose。
    // 谁在用它：Game 的 NetSession 持有它并在自己的 Pump 里推进；RuntimeNetDriver 装的是 CountingTransport(TcpTransport)；
    //            Editor 的 NetDebugWindow 再包一层 CountingTransport；测试里的 FakeTransport / SimulatedTransport 也实现这层契约。
    /// <summary>传输层接缝：连、发、收、关，上层只认这个接口，不认具体是 TCP 还是 UDP。</summary>
    public interface ITransport : IDisposable
    {
        /// <summary>当前连接状态；0 未连、1 正在连、2 已连、3 已关闭。</summary>
        ETransportState State { get; }

        /// <summary>收发统计：已发与已收的字节（含帧头）和帧数，供看板与排查读取。</summary>
        TransportStats Stats { get; }

        /// <summary>一句人话描述，供日志使用，例如 tcp 127.0.0.1:7777；关闭后附带原因。</summary>
        string Description { get; }

        /// <summary>是否已连上，等价于 State 为 Connected；只有该状态才能 Send。</summary>
        bool IsConnected { get; }

        /// <summary>收到一整帧载荷时回调；实现保证只在 Pump 里、在调用 Pump 的那条线程上触发。</summary>
        event Action<byte[]> FrameReceived;

        /// <summary>断开或出错时回调，参数是一句人话原因（主动关闭时为「主动关闭」）；至多触发一次。</summary>
        event Action<string> Closed;

        /// <summary>发起连接后立即返回，握手进度靠 Pump 推进；连不上不抛异常，改由 Closed 报原因。</summary>
        /// <param name="host">主机，IP 或域名，不能为空。</param>
        /// <param name="port">端口，取值 1~65535。</param>
        // 功能：把传输层从「未连」切到「正在连」，真正的握手留给后面的 Pump。
        // 入参：host 主机（IP 或域名，不能为空）；port 端口（1~65535）
        // 返回：无
        // 步骤：
        //   1) 已在连接中或已连上就当场报错，避免一个实例背负两条连接
        //   2) 重连前由实现清掉上一局残留的拆帧缓冲，否则上一局的半帧会被当成新连接的开头
        //   3) 记下目标并置为 Connecting；此刻仍在异步握手中，失败只能走 Closed
        // 上游：NetSession 的登录与重连流程
        // 下游：Pump（推进握手）、Closed（报失败原因）
        void Connect(string host, int port);

        /// <summary>发一条载荷，帧头与长度前缀由实现补；未连接时抛异常，不静默丢弃。</summary>
        /// <param name="payload">载荷，允许为 null，表示一条 0 长度帧。</param>
        // 功能：把一段载荷交给实现排队发出，调用方不用碰粘包与拆包。
        // 入参：payload 载荷（null 表示 0 长度帧）；超过单帧上限由实现拒收
        // 返回：无
        // 步骤：
        //   1) 未连上直接抛异常——静默丢会变成偶发少一条消息，是最难查的一类问题
        //   2) 交给实现分帧后入队，长度前缀只加这一次
        //   3) 立刻冲刷一次，免得调用方忘了 Pump 就静默不发
        // 上游：NetSession 的握手、心跳与业务消息发送
        // 下游：实现内的 FrameCodec.Encode 与 socket 写
        void Send(byte[] payload);

        /// <summary>推进一帧：冲刷发送队列，收字节、拆帧并回调 FrameReceived。</summary>
        // 功能：由调用方每帧调一次，把这一帧能收的都收完，并推进尚未完成的握手。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 正在连就先推进握手，没连上直接返回
        //   2) 先冲刷待发队列再读取，让同一帧内的发送先于本轮新到的消息被处理
        //   3) 读到的字节喂给拆帧器，凑满一帧才回调 FrameReceived
        // 上游：NetSession 的每帧驱动
        // 下游：FrameReceived、Closed
        // 注意：回调都在调用 Pump 的这条线程上发生（通常是主线程），所以回调里可以直接碰 Transform 与 UI。
        void Pump();

        /// <summary>主动关闭，可重复调用；第一次之后 State 即为 Closed。</summary>
        // 功能：断开并回收这条连接，同时以「主动关闭」为原因上报一次 Closed。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已经 Closed 就直接返回，重复调用不会再触发 Closed
        //   2) 置为 Closed 并停止发送，未发出的消息随之丢弃，免得排查时误以为已经发过
        //   3) 关掉底层 socket，最后按需回调一次 Closed
        // 上游：NetSession 的主动断开与退出流程
        // 下游：Closed
        void Close();
    }
}
