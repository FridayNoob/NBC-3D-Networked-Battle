// ============================================================================
//  NBC.Server.Core —— TCP 服务端传输（`INetTransport` 的真实联机实现）
//  项目：3D联网战斗Demo
//  对应：需求文档 §13.3（SRV-03/04/05）、Docs\25-M3开工清单.md S3、D1（TCP + 4 字节长度前缀）
//
//  ---------------------------------------------------------------------------
//  一、它和客户端那份 `TcpTransport` 的关系：**分帧同一份，职责不同**
//  ---------------------------------------------------------------------------
//  分帧用的是 `Shared\Net\FrameCodec`（**双端共享的唯一一份**）——
//  这正是当初把它放进共享层的原因：两端"各写一份分帧"必然对不上，而且单端测试全绿。
//
//  但两边的**形状**不一样，不能合并：
//      客户端：**一条连接** → `ITransport`（连/发/收/关）
//      服务端：**N 条连接** → `INetTransport`（每条带 sessionId，还要接受新连接）
//
//  ---------------------------------------------------------------------------
//  二、单线程拉模式（`Pump`）：M3 **故意不用网络线程**
//  ---------------------------------------------------------------------------
//  所有事都发生在调用 `Pump()` 的那个线程上（主循环线程）：
//      AcceptPending → 逐会话 ReceivePending → 逐会话 FlushPending → SweepTimeouts → Compact
//  非阻塞 `Poll` 问"有没有东西"，没有就立刻返回。
//  于是：**无锁**、**可复现**（测试里手工 Pump，不 sleep 碰运气）、与客户端同源。
//  理由与代价写在 `INetTransport.cs` 的文件头（M0 原注释设想的是"IO 线程 + 入队"）。
//
//  ---------------------------------------------------------------------------
//  三、三个不做就会以别的 bug 面目出现的细节
//  ---------------------------------------------------------------------------
//  ① **断开前先把发送队列冲出去**（先 `Flush` → 再 `Shutdown(Send)` → 最后 `Close`）
//     顺序反了的话，"你被断开，因为协议版本不一致"这条说明**到不了客户端** ——
//     客户端只看到连接莫名其妙断了。这类"踢人理由丢失"极难排查。
//
//  ② **收到 0 字节 = 对端关闭**（`Poll(SelectRead)` 为真但 `Available == 0` 就是 FIN）
//     和服务端版同一坑：漏掉它，会话表里会攒一堆"早就断了但服务端不知道"的僵尸会话。
//
//  ③ **心跳超时按会话逐个清**（`ClientSession.LastRecvTimeUtc`，M0 就留好了字段）
//     没有它，客户端崩溃/拔网线之后席位永远不释放 —— 2~4 人房间里直接卡死进不来人。
//
//  ---------------------------------------------------------------------------
//  四、容量与上限（都有明确理由，不是随手写的数）
//  ---------------------------------------------------------------------------
//  · 单帧上限 `NetContract.MaxFrameBytes`（512KB）—— 与客户端同一口径，超限判协议违规断开
//  · 会话上限 `DefaultMaxSessions`（16）—— M3 是 2~4 人房间，但"连接数"不等于"房间席位"：
//    留出"有人挂在大厅/多人同时连"的余量。超了**明确拒绝并说明**，不是静默丢弃
//  · 心跳超时 `DefaultHeartbeatTimeoutMs`（10s）—— 30Hz 下 10s 没收到任何消息几乎必是掉线
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using NBC.Shared.Net;

namespace NBC.Server.Core;

/// <summary>
/// TCP 服务端传输：非阻塞接受连接 + 长度前缀分帧 + 单线程 `Pump`。
/// </summary>
public sealed class TcpServerTransport : INetTransport
{
    /// <summary>默认监听端口（客户端 Demo 也用它）。</summary>
    public const int DefaultPort = 7777;

    /// <summary>默认心跳超时（毫秒）：超过这么久没收到**任何**消息就断开。</summary>
    public const int DefaultHeartbeatTimeoutMs = 10_000;

    /// <summary>默认会话上限。</summary>
    public const int DefaultMaxSessions = 16;

    /// <summary>一次 `Receive` 最多读多少字节。</summary>
    private const int ReceiveBufferBytes = 16 * 1024;

    private readonly List<Connection> _connections = new();
    private readonly byte[] _receiveBuffer = new byte[ReceiveBufferBytes];

    private readonly IPAddress _address;
    private readonly int _configuredPort;
    private readonly int _heartbeatTimeoutMs;
    private readonly int _maxFrameBytes;
    private readonly int _maxSessions;

    private TcpListener? _listener;
    private int _port;
    private long _nextSessionId = 1;
    private bool _isRunning;

    /// <summary>建一个服务端传输（此时还没监听）。</summary>
    /// <param name="port">监听端口；传 0 = 让系统分配一个空闲端口（测试用）。</param>
    /// <param name="address">
    /// 监听地址；不传 = `127.0.0.1`（只允许本机连）。
    /// <para>⚠️ 默认**不是** `IPAddress.Any`：写代码这台机器上开 `Any` 会弹防火墙授权框，
    /// 而 M3 的验收（D8：一个 Editor + 一个打包版）本来就是同一台机器。
    /// 要联局域网时显式传 `IPAddress.Any` —— 一行的事，但**要有意识地做**。</para>
    /// </param>
    /// <param name="heartbeatTimeoutMs">心跳超时；传 0 = 关掉超时清理。</param>
    /// <param name="maxFrameBytes">单帧上限。</param>
    /// <param name="maxSessions">会话上限。</param>
    public TcpServerTransport(
        int port = DefaultPort,
        IPAddress? address = null,
        int heartbeatTimeoutMs = DefaultHeartbeatTimeoutMs,
        int maxFrameBytes = NetContract.MaxFrameBytes,
        int maxSessions = DefaultMaxSessions)
    {
        if (port < 0 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 0~65535（0 = 让系统分配）。");

        if (maxFrameBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes), "单帧上限必须为正数。");

        if (maxSessions <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSessions), "会话上限必须为正数。");

        _address = address ?? IPAddress.Loopback;
        _configuredPort = port;
        _port = port;
        _heartbeatTimeoutMs = heartbeatTimeoutMs;
        _maxFrameBytes = maxFrameBytes;
        _maxSessions = maxSessions;
    }

    /// <inheritdoc />
    public bool IsRunning => _isRunning;

    /// <inheritdoc />
    public int Port => _port;

    /// <inheritdoc />
    public int SessionCount => _connections.Count;

    /// <summary>累计接受过多少连接（含已断开的）。</summary>
    public long TotalAccepted { get; private set; }

    /// <summary>累计断开过多少连接。</summary>
    public long TotalClosed { get; private set; }

    /// <summary>累计收/发帧数与字节数（含 4 字节帧头）——看板与排查用。</summary>
    public long BytesIn { get; private set; }

    /// <summary>累计发出去的字节数。</summary>
    public long BytesOut { get; private set; }

    /// <summary>累计收到的帧数。</summary>
    public long FramesIn { get; private set; }

    /// <summary>累计发出去的帧数。</summary>
    public long FramesOut { get; private set; }

    /// <inheritdoc />
    public event Action<ClientSession>? SessionOpened;

    /// <inheritdoc />
    public event Action<ClientSession, string>? SessionClosed;

    /// <inheritdoc />
    public event Action<ClientSession, byte[]>? MessageReceived;

    /// <inheritdoc />
    public void Start()
    {
        if (_isRunning)
            throw new InvalidOperationException("[TcpServerTransport] 已经 Start 过了，不要重复启动。");

        var listener = new TcpListener(_address, _configuredPort);
        listener.Start();

        _listener = listener;
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;   // 端口传 0 时，真实端口在这里才拿得到
        _isRunning = true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;

        // 先关会话（每个都会报 SessionClosed，原因写清是服务端关的）
        for (int i = _connections.Count - 1; i >= 0; i--)
        {
            CloseConnection(_connections[i], "服务端关闭");
        }

        _connections.Clear();

        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // 关监听时出错没有可做的动作
        }

        _listener = null;
    }

    /// <inheritdoc />
    public void Pump()
    {
        if (!_isRunning)
        {
            return;
        }

        AcceptPending();

        // ⚠️ 倒序遍历：收/发过程中会话可能被踢掉（标 Closed），倒着走不会漏也不会越界
        for (int i = _connections.Count - 1; i >= 0; i--)
        {
            Connection conn = _connections[i];

            if (conn.Closed)
            {
                continue;
            }

            ReceivePending(conn);

            if (!conn.Closed)
            {
                FlushPending(conn);
            }
        }

        SweepTimeouts();
        Compact();
    }

    /// <inheritdoc />
    public bool Send(long sessionId, ReadOnlySpan<byte> payload)
    {
        Connection? conn = Find(sessionId);

        if (conn == null || conn.Closed)
        {
            return false;   // 见接口注释：这是正常时序，不是异常
        }

        if (payload.Length > _maxFrameBytes)
        {
            // 对端会把这帧判成协议违规并断开；与其让它去踩，不如在这里就说清
            throw new ArgumentException(
                $"载荷 {payload.Length} 字节超过单帧上限 {_maxFrameBytes}，收方会判协议违规。", nameof(payload));
        }

        conn.Pending.Enqueue(FrameCodec.Encode(payload));

        // 立刻试发一次（主循环下一帧还会再冲刷）
        FlushPending(conn);
        return true;
    }

    /// <inheritdoc />
    public void Disconnect(long sessionId, string reason)
    {
        Connection? conn = Find(sessionId);

        if (conn == null)
        {
            return;         // 已经断了：幂等，不是错误
        }

        CloseConnection(conn, reason);
    }

    // ====================================================================
    //  内部：接受连接
    // ====================================================================

    /// <summary>把已经三次握手的连接全部收进来（非阻塞）。</summary>
    private void AcceptPending()
    {
        TcpListener? listener = _listener;

        if (listener == null)
        {
            return;
        }

        while (listener.Pending())
        {
            Socket socket = listener.AcceptSocket();
            socket.NoDelay = true;          // 关 Nagle：与客户端同一考虑（小包、低延迟）
            socket.Blocking = false;

            var session = new ClientSession
            {
                SessionId = _nextSessionId++,
                RemoteEndPoint = socket.RemoteEndPoint?.ToString() ?? "unknown",
            };

            var conn = new Connection(session, socket, new FrameDecoder(_maxFrameBytes));
            _connections.Add(conn);
            TotalAccepted++;

            SessionOpened?.Invoke(session);

            if (_connections.Count > _maxSessions)
            {
                // 满了就明确拒绝并说明原因（不是静默丢弃）
                CloseConnection(conn, $"服务端连接数已满（上限 {_maxSessions}）");
            }
        }
    }

    // ====================================================================
    //  内部：收
    // ====================================================================

    /// <summary>把某个会话内核里已经收到的字节读干净并拆帧。</summary>
    /// <param name="conn">连接。</param>
    private void ReceivePending(Connection conn)
    {
        Socket socket = conn.Socket;

        while (!conn.Closed)
        {
            bool readable;

            try
            {
                readable = socket.Poll(0, SelectMode.SelectRead);
            }
            catch (SocketException ex)
            {
                CloseConnection(conn, $"等待接收失败：{Describe(ex.SocketErrorCode)}");
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!readable)
            {
                return;
            }

            int available;

            try
            {
                available = socket.Available;
            }
            catch (SocketException ex)
            {
                CloseConnection(conn, $"查询可读字节失败：{Describe(ex.SocketErrorCode)}");
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (available <= 0)
            {
                // 见文件头 ②：可读但 0 字节 = 对端发了 FIN
                CloseConnection(conn, "对端关闭了连接（收到 0 字节）");
                return;
            }

            int want = Math.Min(available, _receiveBuffer.Length);
            int received;

            try
            {
                received = socket.Receive(_receiveBuffer, 0, want, SocketFlags.None);
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.WouldBlock)
                {
                    return;
                }

                CloseConnection(conn, $"接收失败：{Describe(ex.SocketErrorCode)}");
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (received <= 0)
            {
                CloseConnection(conn, "对端关闭了连接（收到 0 字节）");
                return;
            }

            // 心跳判定的依据：**收到任何字节**都算活着（不要求一定是 Ping）
            conn.Session.LastRecvTimeUtc = DateTime.UtcNow;
            BytesIn += received;

            conn.Decoder.Append(_receiveBuffer, 0, received);
            DispatchFrames(conn);
        }
    }

    /// <summary>把拆帧器里攒够的消息一条条交给 `MessageReceived`。</summary>
    /// <param name="conn">连接。</param>
    private void DispatchFrames(Connection conn)
    {
        while (!conn.Closed)
        {
            if (!conn.Decoder.TryDequeue(out byte[]? frame, out string? error))
            {
                if (error != null)
                {
                    CloseConnection(conn, $"分帧违规：{error}");
                }

                return;
            }

            FramesIn++;
            MessageReceived?.Invoke(conn.Session, frame!);
        }
    }

    // ====================================================================
    //  内部：发
    // ====================================================================

    /// <summary>尽力把某个会话的待发队列写出去（写不完等下次 `Pump`，不是错误）。</summary>
    /// <param name="conn">连接。</param>
    private void FlushPending(Connection conn)
    {
        Socket socket = conn.Socket;

        while (true)
        {
            if (conn.Writing == null)
            {
                if (conn.Pending.Count == 0)
                {
                    return;
                }

                conn.Writing = conn.Pending.Dequeue();
                conn.WritingOffset = 0;
            }

            int sent;

            try
            {
                sent = socket.Send(conn.Writing, conn.WritingOffset, conn.Writing.Length - conn.WritingOffset,
                                   SocketFlags.None);
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.WouldBlock)
                {
                    return;     // 内核发送缓冲满了：下次 Pump 接着写
                }

                if (!conn.Closed)
                {
                    CloseConnection(conn, $"发送失败：{Describe(ex.SocketErrorCode)}");
                }

                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (sent <= 0)
            {
                return;         // 理论上不会发生；不推进也不假装成功
            }

            conn.WritingOffset += sent;

            if (conn.WritingOffset < conn.Writing.Length)
            {
                return;         // 部分写出：下次从断点接着写
            }

            BytesOut += conn.Writing.Length;
            FramesOut++;
            conn.Writing = null;
            conn.WritingOffset = 0;
        }
    }

    // ====================================================================
    //  内部：关闭与清理
    // ====================================================================

    /// <summary>
    /// 关闭一个连接（幂等）。**顺序见文件头 ①**：冲队列 → `Shutdown(Send)` → `Close` → 报事件。
    /// </summary>
    /// <param name="conn">连接。</param>
    /// <param name="reason">人话原因（会原样交给 `SessionClosed`）。</param>
    private void CloseConnection(Connection conn, string reason)
    {
        if (conn.Closed)
        {
            return;
        }

        conn.Closed = true;

        FlushPending(conn);         // ⚠️ 必须在关闭之前：否则"为什么被踢"到不了客户端

        try
        {
            conn.Socket.Shutdown(SocketShutdown.Send);   // 发 FIN：告诉对端"我说完了"
        }
        catch (SocketException)
        {
            // 已经断了就断了，没有可做的动作
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            conn.Socket.Close();
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        conn.Pending.Clear();
        conn.Writing = null;
        conn.WritingOffset = 0;
        TotalClosed++;

        SessionClosed?.Invoke(conn.Session, reason);
    }

    /// <summary>清掉心跳超时的会话（文件头 ③）。</summary>
    private void SweepTimeouts()
    {
        if (_heartbeatTimeoutMs <= 0)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;

        for (int i = _connections.Count - 1; i >= 0; i--)
        {
            Connection conn = _connections[i];

            if (conn.Closed)
            {
                continue;
            }

            if ((now - conn.Session.LastRecvTimeUtc).TotalMilliseconds > _heartbeatTimeoutMs)
            {
                CloseConnection(conn, $"心跳超时（{_heartbeatTimeoutMs}ms 没收到任何消息）");
            }
        }
    }

    /// <summary>把已关闭的会话从表里摘掉（在 `Pump` 末尾统一做，避免遍历时改表）。</summary>
    private void Compact()
    {
        for (int i = _connections.Count - 1; i >= 0; i--)
        {
            if (_connections[i].Closed)
            {
                _connections.RemoveAt(i);
            }
        }
    }

    /// <summary>按会话 ID 找连接（找不到返回 null）。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <returns>连接或 null。</returns>
    private Connection? Find(long sessionId)
    {
        for (int i = 0; i < _connections.Count; i++)
        {
            if (_connections[i].Session.SessionId == sessionId)
            {
                return _connections[i];
            }
        }

        return null;
    }

    /// <summary>把套接字错误说成人话（和客户端那份同一套说法，排查时不用切换脑子）。</summary>
    /// <param name="error">错误码。</param>
    /// <returns>人话。</returns>
    private static string Describe(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "对端没有在监听（连接被拒绝）",
        SocketError.TimedOut => "超时",
        SocketError.HostNotFound => "找不到这个主机名",
        SocketError.HostUnreachable => "主机不可达",
        SocketError.NetworkUnreachable => "网络不可达",
        SocketError.ConnectionReset => "连接被对端重置（对方进程可能已经没了）",
        SocketError.ConnectionAborted => "连接被中止",
        SocketError.AccessDenied => "被拒绝（防火墙或权限）",
        _ => $"{error}（{(int)error}）",
    };

    /// <summary>
    /// 一条连接的私有状态（socket / 拆帧器 / 待发队列）。
    /// <para>`ClientSession` 是**对外**的会话信息，这里放的是**传输层私有**的东西 ——
    /// 两者分开，别把 socket 塞进会话对象里（那会让上层不小心碰到它）。</para>
    /// </summary>
    private sealed class Connection
    {
        /// <summary>对外会话信息。</summary>
        public readonly ClientSession Session;

        /// <summary>套接字。</summary>
        public readonly Socket Socket;

        /// <summary>拆帧器（粘包/拆包在它里面）。</summary>
        public readonly FrameDecoder Decoder;

        /// <summary>待发队列（元素已经是**包好帧**的字节）。</summary>
        public readonly Queue<byte[]> Pending = new();

        /// <summary>正在往外写的帧（部分写出后等下次接着写）。</summary>
        public byte[]? Writing;

        /// <summary>正在写的那一帧已经写出多少字节。</summary>
        public int WritingOffset;

        /// <summary>是否已关闭（关闭只做一次）。</summary>
        public bool Closed;

        /// <summary>建一条连接状态。</summary>
        /// <param name="session">对外会话信息。</param>
        /// <param name="socket">套接字。</param>
        /// <param name="decoder">拆帧器。</param>
        public Connection(ClientSession session, Socket socket, FrameDecoder decoder)
        {
            Session = session;
            Socket = socket;
            Decoder = decoder;
        }
    }
}
