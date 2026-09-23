// ============================================================================
//  NBC.Server.Core —— 消息泵：把"收字节 → 路由 → 回包/断开"串成一条线
//  项目：3D联网战斗Demo   对应：需求文档 §13.3 SRV-04、Docs\25 S3
//
//  ---------------------------------------------------------------------------
//  为什么单独有这个类（而不是写在 `Program.cs` 里）
//  ---------------------------------------------------------------------------
//  这段逻辑有**两个调用点**：服务端进程（`NBC.Server.Host`）和端到端探针
//  （`Server\_net-probe`，验"两个客户端真连上来会怎样"）。
//  本项目有条规矩：**一处规则、两个实现 = 迟早对不上**。
//  如果探针里再抄一份"收→路由→回包"，那探针验的就是**抄本**，
//  而真正跑在生产里的那份没人验 —— "闸门绿不等于 Unity 绿"的同一个形状。
//  所以：逻辑写在这里，两个调用点都用它。
//
//  ---------------------------------------------------------------------------
//  它做什么（三步，顺序有意义）
//  ---------------------------------------------------------------------------
//      ① `transport.Pump()`   —— 网络 IO：收字节、拆帧，**只把消息塞进队列**（M0 的 SRV-04）
//      ② 逐条解出 `ClientMessage`（解不出 = 协议违规 → 断开并说清）
//      ③ `router.Dispatch` → 按结果：**先回包、再断开**（顺序见 `TcpServerTransport` 文件头 ①）
//
//  ⚠️ 第 ① 步和第 ③ 步**不能**合成一步：`MessageReceived` 是在 `transport.Pump()` 内部触发的，
//     若在那里直接处理并回包，就等于"在收发过程中改会话表" ——
//     单线程下不会崩，但会让"哪些状态在 Pump 中途被改过"变得难以推理。
//     先入队、Pump 返回后再处理，是**刻意**留出的分界线（将来换线程也就换这一个队列）。
// ============================================================================

using System;
using System.Collections.Generic;
using Google.Protobuf;
using NBC.Protocol;

namespace NBC.Server.Core;

/// <summary>消息泵：推进网络 + 把收到的消息交给路由处理。</summary>
public sealed class ServerMessagePump
{
    private readonly INetTransport _transport;
    private readonly ServerMessageRouter _router;
    private readonly Queue<(ClientSession Session, byte[] Payload)> _inbox = new();

    /// <summary>建一个消息泵（会订阅传输的收包事件）。</summary>
    /// <param name="transport">传输。</param>
    /// <param name="router">路由。</param>
    public ServerMessagePump(INetTransport transport, ServerMessageRouter router)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _router = router ?? throw new ArgumentNullException(nameof(router));

        _transport.MessageReceived += OnMessageReceived;
    }

    /// <summary>值得记一句的事情（日志用）：握手成功/被拒、踢人、解不出的消息、还没实现的消息。</summary>
    public event Action<string>? Note;

    /// <summary>握手成功的次数（累计）。</summary>
    public long HandshakesAccepted { get; private set; }

    /// <summary>握手被拒的次数（版本不一致）。</summary>
    public long HandshakesRejected { get; private set; }

    /// <summary>被踢掉的会话数（协议违规 / 未握手先发别的 / 重复握手以外的违规）。</summary>
    public long Kicks { get; private set; }

    /// <summary>解不出 `ClientMessage` 的消息条数（对端不是本协议 / 数据烂了）。</summary>
    public long Undecodable { get; private set; }

    /// <summary>队列里还有多少条没处理（正常情况下每帧清空）。</summary>
    public int PendingCount => _inbox.Count;

    /// <summary>推进一帧：收网络 + 处理队列。</summary>
    public void Pump()
    {
        _transport.Pump();
        DrainInbox();
    }

    /// <summary>把队列里的消息全部处理掉（**必须在 `transport.Pump()` 返回之后调**，见文件头）。</summary>
    public void DrainInbox()
    {
        while (_inbox.Count > 0)
        {
            (ClientSession session, byte[] payload) = _inbox.Dequeue();
            Handle(session, payload);
        }
    }

    /// <summary>收到字节：**只入队**（SRV-04）。</summary>
    /// <param name="session">来源会话。</param>
    /// <param name="payload">载荷。</param>
    private void OnMessageReceived(ClientSession session, byte[] payload) => _inbox.Enqueue((session, payload));

    /// <summary>处理一条消息：解包 → 路由 → 按结果回包/断开。</summary>
    /// <param name="session">来源会话。</param>
    /// <param name="payload">载荷。</param>
    private void Handle(ClientSession session, byte[] payload)
    {
        ClientMessage message;

        try
        {
            message = ClientMessage.Parser.ParseFrom(payload);
        }
        catch (Exception ex)
        {
            Undecodable++;
            _transport.Disconnect(session.SessionId, "消息不是合法的 ClientMessage");
            Note?.Invoke($"会话 {session.SessionId} 的消息解不出 ClientMessage：{ex.Message}");
            return;
        }

        DispatchResult result = _router.Dispatch(session, message);

        // ⚠️ 顺序：先回包、再断开 —— 反过来的话，"为什么被拒"永远到不了客户端
        if (result.Reply != null)
        {
            _transport.Send(session.SessionId, result.Reply.ToByteArray());
        }

        if (result.Note != null)
        {
            Note?.Invoke($"会话 {session.SessionId}：{result.Note}");
        }

        if (result.Kick != null)
        {
            Kicks++;
            _transport.Disconnect(session.SessionId, result.Kick);
            Note?.Invoke($"会话 {session.SessionId} 被断开：{result.Kick}");
        }

        if (message.PayloadCase == ClientMessage.PayloadOneofCase.Handshake)
        {
            if (result.Reply?.HandshakeAck?.Accepted == true)
            {
                HandshakesAccepted++;
                HandshakeAck ack = result.Reply.HandshakeAck;
                Note?.Invoke($"会话 {session.SessionId} 握手成功 → 玩家 {ack.PlayerId}" +
                             $"（客户端 v{message.Handshake.ProtocolVersion}，tick {ack.TickHz}Hz）");
            }
            else
            {
                HandshakesRejected++;
            }
        }
    }
}
