// ============================================================================
//  NBC.Server.Core —— 房间服务：把"房间表"接到"网线"上
//  项目：3D联网战斗Demo   对应：Docs\25 §四 S4、需求文档 §13.3（RoomMgr 的消息面）
//
//  ---------------------------------------------------------------------------
//  一、为什么"房间表"（RoomRegistry）之外还要有这个类
//  ---------------------------------------------------------------------------
//  房间**规则**（谁能进、满没满、房主是谁）是纯逻辑，不该认识 socket ——
//  所以它们在 `RoomRegistry` 里，可以脱离网络直接用用例钉住。
//  但"席位变了要**通知房里的每个人**"这件事必须碰传输层。两者分开之后：
//
//      RoomRegistry  → 规则（可单测，不碰网络）
//      RoomService   → 接线（注册消息处理器 / 整份广播 / 断线自动退房）
//
//  📌 判据（和 M2-C 的装配层同一条）：**`RoomRegistry` 里出现 `Send` 就是放错了地方。**
//
//  ---------------------------------------------------------------------------
//  二、两种下行消息的分工（**别混**，协议里也写了）
//  ---------------------------------------------------------------------------
//      `RoomState`     = 你的房间**现在长什么样**（有变化就整份下发）
//      `ErrorResponse` = 你刚发的那个请求**被拒了**，原因是……
//
//  ⚠️ **"空房间状态"是一条明确的约定**：`room_id` 为空 + `members` 为空
//     = "你现在不在任何房间"。用它来回答"离开成功"，就不需要再为"成功"造一种消息。
//     这条约定由用例/probe 钉住（`NotInRoomState()`）。
//
//  ---------------------------------------------------------------------------
//  三、断开连接 = 自动退房（不这么做会怎样）
//  ---------------------------------------------------------------------------
//  客户端崩溃/拔网线时不会发 `LeaveRoomRequest`。若不在 `SessionClosed` 里把人摘掉：
//      · 席位**永远占着** → 4 人房被 3 个僵尸占住，新的人进不来（M3 的核心体验直接坏掉）
//      · 席位表广播给别人时，还会带上一堆早就断开的人
//  心跳超时（`TcpServerTransport`）负责"发现断开"，这里负责"发现之后把席位收回来"。
//
//  ---------------------------------------------------------------------------
//  四、广播是在 `transport.Pump()` **内部**触发的（有一处需要留意）
//  ---------------------------------------------------------------------------
//  `SessionClosed` 是在 `Pump()` 的收发过程中报出来的，所以 `OnSessionClosed` 里的广播
//  也是在那一刻发生的。这**不会**破坏什么：
//      · 广播只往**别人**的发送队列里放字节（退出者已经不在席位表里了）
//      · 逐个连接的 `FlushPending` 是幂等的，外层循环稍后还会再冲一次
//  但"在 Pump 中途 Send"确实是个需要知道的事实，所以写在这里（将来若改成多线程，
//  这里就是第一个要看的地方）。
// ============================================================================

using System;
using Google.Protobuf;         // `ToByteArray()` 是这里的扩展方法（少了它报 CS1061）
using NBC.Protocol;

namespace NBC.Server.Core;

/// <summary>房间服务：注册进房/离房处理器、广播席位表、断线自动退房。</summary>
public sealed class RoomService
{
    private readonly INetTransport _transport;
    private readonly RoomRegistry _registry;

    /// <summary>建一个房间服务。</summary>
    /// <param name="transport">传输（广播要用它）。</param>
    /// <param name="registry">房间表（规则在里面）。</param>
    public RoomService(INetTransport transport, RoomRegistry registry)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        _transport.SessionClosed += OnSessionClosed;
    }

    /// <summary>值得记一句的事情（房间进出、拒绝原因）。</summary>
    public event Action<string>? Note;

    /// <summary>房间表（日志/统计读它）。</summary>
    public RoomRegistry Registry => _registry;

    /// <summary>累计广播出去多少份席位表。</summary>
    public long StateBroadcasts { get; private set; }

    /// <summary>把进房/离房处理器注册进路由（**显式注册**，见 `ServerMessageRouter`）。</summary>
    /// <param name="router">路由。</param>
    public void RegisterHandlers(ServerMessageRouter router)
    {
        if (router == null)
        {
            throw new ArgumentNullException(nameof(router));
        }

        router.Register(ClientMessage.PayloadOneofCase.JoinRoom, HandleJoinRoom);
        router.Register(ClientMessage.PayloadOneofCase.LeaveRoom, HandleLeaveRoom);
    }

    /// <summary>
    /// 把某个房间的席位表**整份**发给房里的每个人。
    /// </summary>
    /// <param name="room">房间。</param>
    /// <returns>成功发出去几份。</returns>
    public int BroadcastState(Room room)
    {
        RoomState state = room.ToState();
        byte[] payload = new ServerMessage { RoomState = state }.ToByteArray();
        int sent = 0;

        for (int i = 0; i < room.Seats.Count; i++)
        {
            if (_transport.Send(room.Seats[i].Session.SessionId, payload))
            {
                sent++;
            }
        }

        StateBroadcasts++;
        return sent;
    }

    /// <summary>"你不在任何房间"的那份状态（见文件头第二节的约定）。</summary>
    /// <returns>房间状态（房号为空、没有成员）。</returns>
    public static RoomState NotInRoomState()
    {
        return new RoomState
        {
            RoomId = string.Empty,
            DungeonId = 0,
            Capacity = 0,
            Phase = RoomPhase.Waiting,
        };
    }

    /// <summary>处理进房请求。</summary>
    /// <param name="session">会话。</param>
    /// <param name="message">消息。</param>
    /// <returns>分发结果。</returns>
    private DispatchResult HandleJoinRoom(ClientSession session, ClientMessage message)
    {
        JoinRoomRequest request = message.JoinRoom;

        if (request == null)
        {
            return DispatchResult.KickOut("JoinRoom 消息里没有 JoinRoomRequest 字段（协议违规）");
        }

        RoomJoinResult result = _registry.Join(session, session.PlayerName, request.RoomId, request.DungeonId);

        if (!result.Ok)
        {
            // 拒绝了：回一条**说清原因**的 ErrorResponse（见文件头第二节）
            return DispatchResult.ReplyWith(Error(result.ErrorCode, result.Reason!));
        }

        Room room = result.Room!;
        int sent = BroadcastState(room);

        Note?.Invoke($"玩家 {session.PlayerId}（{session.PlayerName}）进入房间 {room.RoomId}" +
                     $"（副本 {room.DungeonId}，{room.SeatCount}/{room.Capacity} 人），广播 {sent} 份席位表");

        return DispatchResult.Handled;
    }

    /// <summary>处理离房请求。</summary>
    /// <param name="session">会话。</param>
    /// <param name="message">消息。</param>
    /// <returns>分发结果。</returns>
    private DispatchResult HandleLeaveRoom(ClientSession session, ClientMessage message)
    {
        Room? room = _registry.Leave(session);

        if (room == null)
        {
            // 不在任何房间里还发"离开"：不是错误（幂等），回一份"你不在房间"就够
            Note?.Invoke($"玩家 {session.PlayerId} 发来离开请求，但它本来就不在任何房间");
            return DispatchResult.ReplyWith(new ServerMessage { RoomState = NotInRoomState() });
        }

        if (!room.IsEmpty)
        {
            BroadcastState(room);
        }

        Note?.Invoke($"玩家 {session.PlayerId}（{session.PlayerName}）离开房间 {room.RoomId}" +
                     (room.IsEmpty ? "（房间已空，已删除）" : $"（还剩 {room.SeatCount} 人）"));

        // 给离开者本人一份"你不在任何房间"（约定见文件头第二节）
        return DispatchResult.ReplyWith(new ServerMessage { RoomState = NotInRoomState() });
    }

    /// <summary>会话断开 → 自动退房 + 通知剩下的人（见文件头第三节）。</summary>
    /// <param name="session">断开的会话。</param>
    /// <param name="reason">断开原因。</param>
    private void OnSessionClosed(ClientSession session, string reason)
    {
        Room? room = _registry.Leave(session);

        if (room == null)
        {
            return;     // 它本来就不在任何房间：没什么要做的
        }

        if (!room.IsEmpty)
        {
            BroadcastState(room);
        }

        Note?.Invoke($"玩家 {session.PlayerId} 断开（{reason}）→ 自动退出房间 {room.RoomId}" +
                     (room.IsEmpty ? "（房间已空，已删除）" : $"（还剩 {room.SeatCount} 人）"));
    }

    /// <summary>造一条 `ErrorResponse`。</summary>
    /// <param name="code">错误码。</param>
    /// <param name="message">人话。</param>
    /// <returns>服务端消息。</returns>
    private static ServerMessage Error(int code, string message)
    {
        return new ServerMessage
        {
            Error = new ErrorResponse { Code = code, Message = message },
        };
    }
}
