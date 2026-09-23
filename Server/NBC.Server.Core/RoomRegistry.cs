// ============================================================================
//  NBC.Server.Core —— 一个房间（= 一个副本实例）与它的席位
//  项目：3D联网战斗Demo
//  对应：需求文档 §13.3（RoomMgr）、Docs\25 §三 D6、§四 S4
//
//  ---------------------------------------------------------------------------
//  一、D6 定了什么（一句话）
//  ---------------------------------------------------------------------------
//      **一个房间 = 一个副本实例**；进程内一张房间表；每房 2~4 个席位。
//  所以"进房"不是"挂到某个大厅"，而是"**进入一个即将开打的副本**"，
//  "离开房间"就等于"从这次副本里退出"。
//
//  ---------------------------------------------------------------------------
//  二、为什么席位拿着 `ClientSession` 引用，而不是玩家 id
//  ---------------------------------------------------------------------------
//  因为服务端要**给席位发消息**（席位表变了就得挨个通知），而"发消息"这件事只有
//  `INetTransport`（按 `sessionId`）能做。席位里放一份会话引用，等于把
//  "这个人现在还在线"也一起表达了 —— 断开的会话会被 `RoomService` 立刻移出。
//
//  ⚠️ 反过来说：**房间只活在进程内**（D6 的"进程内 `RoomRegistry`"）。
//     服务端一重启，房间就没了 —— M3 有意不做持久化（那是 M5+ 的事）。
//
//  ---------------------------------------------------------------------------
//  三、房主（`IsHost`）的语义，以及"房主走了怎么办"
//  ---------------------------------------------------------------------------
//  M3 里房主**没有特权**，只是个标记（协议注释里也这么写）。但"标记"也得有确定的规则，
//  否则四个人看到的房主可能不是同一个：
//      · 第一个进房的人当房主
//      · 房主离开 → **按进房顺序的下一个人**接替（`Seats[0]`）
//  这条规则是**确定性**的（不依赖字典遍历顺序、不依赖时间），所以可以写用例钉住。
//
//  ---------------------------------------------------------------------------
//  四、`ToState()` 为什么可以直接产出协议消息
//  ---------------------------------------------------------------------------
//  `RoomState` 就是"房间现在长什么样"的**线格式**（D5：有变化就整份下发）。
//  在房间模型旁边做这一处映射，比"到处各拼一份"要好：将来协议加字段（例如就绪态、
//  副本进度），只需要改这一个地方，**而且编译器会提醒你漏了字段**。
// ============================================================================

using System.Collections.Generic;
using NBC.Protocol;
using NBC.Shared.Net;

namespace NBC.Server.Core;

/// <summary>房间里的一个席位。</summary>
public sealed class RoomSeat
{
    /// <summary>这个席位上的会话（拿着它才能给这个人发消息）。</summary>
    public ClientSession Session { get; }

    /// <summary>玩家名（握手时带过来的；M3 没有账号系统）。</summary>
    public string PlayerName { get; }

    /// <summary>是否已准备（协议里有这个字段；M3 还没做"准备"流程）。</summary>
    public bool Ready { get; set; }

    /// <summary>是不是房主（规则见 `Room.cs` 文件头第三节）。</summary>
    public bool IsHost { get; internal set; }

    /// <summary>造一个席位。</summary>
    /// <param name="session">会话。</param>
    /// <param name="playerName">玩家名。</param>
    public RoomSeat(ClientSession session, string playerName)
    {
        Session = session;
        PlayerName = playerName;
    }
}

/// <summary>一个房间（= 一个副本实例）。</summary>
public sealed class Room
{
    private readonly List<RoomSeat> _seats = new();

    /// <summary>房间号（服务端分配的，例如 `r1`）。</summary>
    public string RoomId { get; }

    /// <summary>要打哪个副本（`Dungeon` 表主键；S6 才会用它去建副本）。</summary>
    public int DungeonId { get; }

    /// <summary>容量（M3 = `NetContract.MaxRoomMembers` = 4）。</summary>
    public int Capacity { get; }

    /// <summary>
    /// 房间阶段。
    /// <para>⚠️ S4 阶段**恒为 `Waiting`** —— 改它的流程属于 S6（副本开始/结束）。
    /// 这里留着字段是因为 `RoomState` 要下发它，而不是先写一个走不到的分支。</para>
    /// </summary>
    public RoomPhase Phase { get; set; } = RoomPhase.Waiting;

    /// <summary>现在几个人。</summary>
    public int SeatCount => _seats.Count;

    /// <summary>席位（按**进房顺序**，所以 `Seats[0]` 是房主）。</summary>
    public IReadOnlyList<RoomSeat> Seats => _seats;

    /// <summary>没人了（`RoomRegistry` 会把它删掉）。</summary>
    public bool IsEmpty => _seats.Count == 0;

    /// <summary>满了。</summary>
    public bool IsFull => _seats.Count >= Capacity;

    /// <summary>造一个房间。</summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="dungeonId">副本编号。</param>
    /// <param name="capacity">容量。</param>
    public Room(string roomId, int dungeonId, int capacity)
    {
        RoomId = roomId;
        DungeonId = dungeonId;
        Capacity = capacity;
    }

    /// <summary>这个会话在不在房里（在就返回它的席位）。</summary>
    /// <param name="session">会话。</param>
    /// <returns>席位或 null。</returns>
    public RoomSeat? Find(ClientSession session)
    {
        for (int i = 0; i < _seats.Count; i++)
        {
            if (_seats[i].Session.SessionId == session.SessionId)
            {
                return _seats[i];
            }
        }

        return null;
    }

    /// <summary>加一个席位（**调用方必须先确认没满、且这个人不在房里**）。</summary>
    /// <param name="session">会话。</param>
    /// <param name="playerName">玩家名。</param>
    /// <returns>新席位。</returns>
    public RoomSeat Add(ClientSession session, string playerName)
    {
        var seat = new RoomSeat(session, string.IsNullOrEmpty(playerName) ? "玩家" + session.SessionId : playerName);

        // 第一个进房的人当房主（确定性规则，见文件头第三节）
        seat.IsHost = _seats.Count == 0;
        _seats.Add(seat);
        return seat;
    }

    /// <summary>把一个人移出房间；房主走了就由下一个人接替。</summary>
    /// <param name="session">会话。</param>
    /// <returns>确实移出去了返回 true。</returns>
    public bool Remove(ClientSession session)
    {
        for (int i = 0; i < _seats.Count; i++)
        {
            if (_seats[i].Session.SessionId != session.SessionId)
            {
                continue;
            }

            bool wasHost = _seats[i].IsHost;
            _seats.RemoveAt(i);

            if (wasHost && _seats.Count > 0)
            {
                _seats[0].IsHost = true;    // 见文件头第三节：按进房顺序接替
            }

            return true;
        }

        return false;
    }

    /// <summary>把当前房间**整份**转成协议消息（D5：有变化就整份下发）。</summary>
    /// <returns>房间状态。</returns>
    public RoomState ToState()
    {
        var state = new RoomState
        {
            RoomId = RoomId,
            DungeonId = DungeonId,
            Capacity = Capacity,
            Phase = Phase,
        };

        for (int i = 0; i < _seats.Count; i++)
        {
            RoomSeat seat = _seats[i];

            state.Members.Add(new RoomMember
            {
                PlayerId = seat.Session.PlayerId,
                PlayerName = seat.PlayerName,
                IsHost = seat.IsHost,
                Ready = seat.Ready,
            });
        }

        return state;
    }
}

/// <summary>
/// 一次"进房请求"的结果。
/// <para>`Ok` 为 false 时带错误码与人话原因 —— 这正是 `ErrorResponse` 要发回去的东西。</para>
/// </summary>
public readonly struct RoomJoinResult
{
    /// <summary>进房成功时的房间（失败为 null）。</summary>
    public readonly Room? Room;

    /// <summary>失败时的错误码（成功为 0）。</summary>
    public readonly int ErrorCode;

    /// <summary>失败时的人话原因（成为 `ErrorResponse.message`）。</summary>
    public readonly string? Reason;

    private RoomJoinResult(Room? room, int errorCode, string? reason)
    {
        Room = room;
        ErrorCode = errorCode;
        Reason = reason;
    }

    /// <summary>成功了吗。</summary>
    public bool Ok => Room != null;

    /// <summary>成功。</summary>
    /// <param name="room">房间。</param>
    /// <returns>结果。</returns>
    public static RoomJoinResult Success(Room room) => new(room, 0, null);

    /// <summary>失败。</summary>
    /// <param name="errorCode">错误码。</param>
    /// <param name="reason">人话原因。</param>
    /// <returns>结果。</returns>
    public static RoomJoinResult Rejected(int errorCode, string reason) => new(null, errorCode, reason);
}

/// <summary>
/// 进程内的房间表（**纯逻辑，不碰网络** —— 所以规则可以直接拿用例钉住）。
/// </summary>
public sealed class RoomRegistry
{
    private readonly Dictionary<string, Room> _rooms = new(System.StringComparer.Ordinal);
    private readonly int _capacity;
    private int _nextRoomNumber = 1;

    /// <summary>建一张房间表。</summary>
    /// <param name="capacity">每房容量（默认 `NetContract.MaxRoomMembers`）。</param>
    public RoomRegistry(int capacity = NetContract.MaxRoomMembers)
    {
        _capacity = capacity <= 0 ? NetContract.MaxRoomMembers : capacity;
    }

    /// <summary>现在有几个房间。</summary>
    public int RoomCount => _rooms.Count;

    /// <summary>每房容量。</summary>
    public int Capacity => _capacity;

    /// <summary>房间表（给日志/统计用）。</summary>
    public IReadOnlyCollection<Room> Rooms => _rooms.Values;

    /// <summary>这个会话在哪个房间（不在任何房间就返回 null）。</summary>
    /// <param name="session">会话。</param>
    /// <returns>房间或 null。</returns>
    public Room? FindBySession(ClientSession session)
    {
        foreach (Room room in _rooms.Values)
        {
            if (room.Find(session) != null)
            {
                return room;
            }
        }

        return null;
    }

    /// <summary>按房间号取。</summary>
    /// <param name="roomId">房间号。</param>
    /// <returns>房间或 null。</returns>
    public Room? Find(string roomId)
    {
        if (string.IsNullOrEmpty(roomId))
        {
            return null;
        }

        Room? room;
        return _rooms.TryGetValue(roomId, out room) ? room : null;
    }

    /// <summary>开一个新房间（房号由服务端分配）。</summary>
    /// <param name="dungeonId">副本编号。</param>
    /// <returns>新房间。</returns>
    public Room Create(int dungeonId)
    {
        string roomId = "r" + _nextRoomNumber;
        _nextRoomNumber++;

        var room = new Room(roomId, dungeonId, _capacity);
        _rooms.Add(roomId, room);
        return room;
    }

    /// <summary>
    /// 进房。三种拒绝都有明确原因（见 `RoomJoinResult`）。
    /// <para>
    /// 语义（`JoinRoomRequest.room_id` 的两种用法）：
    /// · **给了房号** → 必须是已存在的房间，且没满
    /// · **留空** → 找一个"同副本且没满"的房间加入；没有就**开一个新的**
    /// </para>
    /// </summary>
    /// <param name="session">会话。</param>
    /// <param name="playerName">玩家名。</param>
    /// <param name="requestedRoomId">请求的房号（可空）。</param>
    /// <param name="dungeonId">想打的副本（留空房号时用它找/开房）。</param>
    /// <returns>结果。</returns>
    public RoomJoinResult Join(ClientSession session, string playerName, string requestedRoomId, int dungeonId)
    {
        Room? current = FindBySession(session);

        if (current != null)
        {
            // 一个人同时只在一个房间里：不拦的话，同一个 sessionId 会出现在两张席位表上，
            // 之后"谁该收到快照"就说不清了
            return RoomJoinResult.Rejected(
                NetErrors.AlreadyInRoom,
                $"你已经在房间 {current.RoomId} 里了（要先离开才能进别的房间）");
        }

        Room? target;

        if (!string.IsNullOrEmpty(requestedRoomId))
        {
            target = Find(requestedRoomId);

            if (target == null)
            {
                return RoomJoinResult.Rejected(NetErrors.RoomNotFound, $"房间 {requestedRoomId} 不存在");
            }
        }
        else
        {
            target = FindJoinableRoom(dungeonId);

            if (target == null)
            {
                target = Create(dungeonId);
            }
        }

        if (target.IsFull)
        {
            return RoomJoinResult.Rejected(
                NetErrors.RoomFull,
                $"房间 {target.RoomId} 已满（{target.SeatCount}/{target.Capacity}）");
        }

        target.Add(session, playerName);
        return RoomJoinResult.Success(target);
    }

    /// <summary>
    /// 离开房间（幂等）。
    /// </summary>
    /// <param name="session">会话。</param>
    /// <returns>它离开的那个房间；不在任何房间则为 null。房空了会被删掉。</returns>
    public Room? Leave(ClientSession session)
    {
        Room? room = FindBySession(session);

        if (room == null)
        {
            return null;
        }

        room.Remove(session);

        if (room.IsEmpty)
        {
            // 空房间必须删掉：不删的话房号会一直涨，而且"留空自动找房"会挑到一个空房
            _rooms.Remove(room.RoomId);
        }

        return room;
    }

    /// <summary>找一个"同一个副本、还没满"的房间（没有就返回 null）。</summary>
    /// <param name="dungeonId">副本编号。</param>
    /// <returns>房间或 null。</returns>
    private Room? FindJoinableRoom(int dungeonId)
    {
        Room? best = null;

        foreach (Room room in _rooms.Values)
        {
            if (room.DungeonId != dungeonId || room.IsFull || room.IsEmpty)
            {
                continue;
            }

            // 取房号最小的那个（`r2` 应当在 `r10` 前）—— 用数字比而不是字符串比，否则 `r10 < r2`
            if (best == null || RoomNumberOf(room.RoomId) < RoomNumberOf(best.RoomId))
            {
                best = room;
            }
        }

        return best;
    }

    /// <summary>把 `r12` 解析成 12（解析不了就当最大，排到最后）。</summary>
    /// <param name="roomId">房号。</param>
    /// <returns>序号。</returns>
    private static int RoomNumberOf(string roomId)
    {
        if (string.IsNullOrEmpty(roomId) || roomId.Length < 2)
        {
            return int.MaxValue;
        }

        return int.TryParse(roomId.Substring(1), out int number) ? number : int.MaxValue;
    }
}
