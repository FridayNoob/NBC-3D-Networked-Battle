// ============================================================================
//  TcpTransport —— `ITransport` 的 **TCP 实现**（M3 的唯一传输）
//  项目：3D联网战斗Demo   对应：M3-S2b、Docs\25 §三 D1（TCP + 4 字节小端长度前缀 + protobuf）
//
//  ---------------------------------------------------------------------------
//  一、它只用 BCL（`System.Net.Sockets`），一行引擎代码都没有
//  ---------------------------------------------------------------------------
//  `NBC.Framework.Net.asmdef` 是 `noEngineReferences: true` + 只引 `NBC.Shared`，
//  所以"用 UnityEngine"在这里是**编译错误**，不是纪律问题。
//  这样做的直接好处：本文件可以被 `Server\_condition-probe`（.NET 8）**编进去真跑** ——
//  回环收发、粘包、拆包、对端关闭、超限帧，全都在没有 Unity 的情况下拿到证据。
//
//  ---------------------------------------------------------------------------
//  二、三个"不做就会以别的 bug 面目出现"的细节
//  ---------------------------------------------------------------------------
//  ① **非阻塞连接 + `Poll`**（不是 `Connect` 之后 sleep 等）
//     `Blocking = false` 之后 `Connect` 立刻返回（抛 `WouldBlock` 表示"正在连"），
//     之后每次 `Pump` 用 `Poll` 问一句"能写了吗 / 出错了吗"。
//     连不上（对方没监听、主机名解析不了、超时）一律走 `Closed` 报**人话原因** ——
//     不允许"一直停在 Connecting"那种谁也看不懂的卡住。
//
//  ② **`NoDelay = true`（关掉 Nagle）**
//     Nagle 会把小包攒起来再发，最多能压到 ~200ms 才出门。
//     对一个 30Hz 的动作战斗 Demo 来说，这就是"操作延迟忽然一顿一顿的"。
//     代价是包变多 —— 我们的包本来就小、频率也就 30Hz，值得。
//
//  ③ **收到 0 字节 = 对端关了，必须当成断开**
//     `Poll(SelectRead)` 为真但 `Available == 0` 就是 FIN。
//     漏掉这一条的表现是"对方早已退出，本端一直以为自己还连着" ——
//     又一个静默失败，而且只在联机对局里才看得见。
//
//  ---------------------------------------------------------------------------
//  三、粘包 / 拆包怎么处理（上层永远看不到）
//  ---------------------------------------------------------------------------
//  收：`Receive` 到什么就是什么（可能半帧、也可能三帧粘在一起），
//      全部塞给 `FrameDecoder`，然后**循环 `TryDequeue` 直到取不出整帧** ——
//      每取出一条就回调一次 `FrameReceived`。这样"一次 Receive 收到 3 条消息"
//      对上层就是三次回调，符合直觉。
//  发：先 `FrameCodec.Encode` 成帧再入队；`Pump` 里尽力写出，
//      **写了一半也算正常**（TCP 发送缓冲满了），下次 `Pump` 接着从断点写 ——
//      所以队里存的是"帧 + 已写出多少"，不是一个裸的 byte[] 列表。
//
//  ---------------------------------------------------------------------------
//  四、统计口径（如实记，免得看板对不上）
//  ---------------------------------------------------------------------------
//  · `BytesSent` / `FramesSent`：**完整写出去**的帧才算（还躺在队列里的不算）
//  · 统计在每次 `Connect` 时**清零** —— 口径是"这一次连接"，不是"进程生命周期"
//    （重连前后混在一起的话，"这一局丢了多少"就看不出来了）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NBC.Shared.Net;

namespace NBC.Framework.Net.Adapter
{
    /// <summary>
    /// TCP 传输：长度前缀分帧 + 非阻塞连接 + 拉模式收发（`Pump`）。
    /// </summary>
    public sealed class TcpTransport : ITransport
    {
        /// <summary>默认连接超时（毫秒）。</summary>
        public const int DefaultConnectTimeoutMs = 5000;

        /// <summary>接收缓冲大小（一次 `Receive` 最多读这么多）。</summary>
        private const int ReceiveBufferBytes = 16 * 1024;

        /// <summary>连接超时（毫秒）。</summary>
        private readonly int m_connectTimeoutMs;

        /// <summary>单帧上限（发与收同一口径）。</summary>
        private readonly int m_maxFrameBytes;

        /// <summary>拆帧器（粘包/拆包都在它里面）。</summary>
        private readonly FrameDecoder m_decoder;

        /// <summary>待发队列（元素已经是**包好帧**的字节）。</summary>
        private readonly Queue<byte[]> m_pending = new Queue<byte[]>();

        /// <summary>连接计时（算超时用）。</summary>
        private readonly Stopwatch m_connectTimer = new Stopwatch();

        /// <summary>接收缓冲（复用，避免每次收都分配）。</summary>
        private readonly byte[] m_receiveBuffer = new byte[ReceiveBufferBytes];

        /// <summary>套接字（未连接时为 null）。</summary>
        private Socket m_socket;

        /// <summary>正在往外写的帧（部分写出后等下次 `Pump` 接着写）。</summary>
        private byte[] m_writing;

        /// <summary>正在写的那一帧已经写出多少字节。</summary>
        private int m_writingOffset;

        /// <summary>目标主机（只用于日志/统计）。</summary>
        private string m_host;

        /// <summary>目标端口（只用于日志/统计）。</summary>
        private int m_port;

        /// <summary>当前状态。</summary>
        private ETransportState m_state = ETransportState.Disconnected;

        /// <summary>关闭原因（人话；关闭后 `Description` 里会带上它）。</summary>
        private string m_closedReason;

        /// <summary>`Closed` 是否已经报过（**契约：至多一次**）。</summary>
        private bool m_closeNotified;

        /// <summary>是否已经 `Dispose`（之后再 `Connect`/`Send` 就是调用方的 bug）。</summary>
        private bool m_disposed;

        private long m_bytesSent;
        private long m_bytesReceived;
        private int m_framesSent;
        private int m_framesReceived;

        /// <summary>造一个 TCP 传输（此时还没有连接）。</summary>
        /// <param name="connectTimeoutMs">连接超时（毫秒）；不传为 <see cref="DefaultConnectTimeoutMs"/>。</param>
        /// <param name="maxFrameBytes">单帧上限；不传为 <see cref="NetContract.MaxFrameBytes"/>。</param>
        public TcpTransport(int connectTimeoutMs = DefaultConnectTimeoutMs,
                            int maxFrameBytes = NetContract.MaxFrameBytes)
        {
            if (connectTimeoutMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(connectTimeoutMs),
                    "[TcpTransport] 连接超时必须 > 0（当前 " + connectTimeoutMs + "）。");
            }

            if (maxFrameBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFrameBytes),
                    "[TcpTransport] 单帧上限必须 > 0（当前 " + maxFrameBytes + "）。");
            }

            m_connectTimeoutMs = connectTimeoutMs;
            m_maxFrameBytes = maxFrameBytes;
            m_decoder = new FrameDecoder(maxFrameBytes);
        }

        /// <summary>当前状态。</summary>
        public ETransportState State
        {
            get { return m_state; }
        }

        /// <summary>收发统计（口径见文件头第四节）。</summary>
        public TransportStats Stats
        {
            get { return new TransportStats(m_bytesSent, m_bytesReceived, m_framesSent, m_framesReceived); }
        }

        /// <summary>人话描述，例如 `tcp 127.0.0.1:7777`；关闭后形如 `tcp 127.0.0.1:7777（连接超时…）`。</summary>
        public string Description
        {
            get
            {
                string endpoint = string.IsNullOrEmpty(m_host) ? "(未连接)" : m_host + ":" + m_port;
                return m_closedReason == null ? "tcp " + endpoint : "tcp " + endpoint + "（" + m_closedReason + "）";
            }
        }

        /// <summary>是不是连上了。</summary>
        public bool IsConnected
        {
            get { return m_state == ETransportState.Connected; }
        }

        /// <summary>收到一整帧（载荷）时回调；**只在 `Pump` 里被调**。</summary>
        public event Action<byte[]> FrameReceived;

        /// <summary>断开或出错时回调（**至多一次**）。</summary>
        public event Action<string> Closed;

        /// <summary>发起连接（非阻塞；连不上走 `Closed`）。</summary>
        /// <param name="host">主机（IP 或域名）。</param>
        /// <param name="port">端口（1~65535）。</param>
        public void Connect(string host, int port)
        {
            ThrowIfDisposed();

            if (m_state == ETransportState.Connecting || m_state == ETransportState.Connected)
            {
                throw new InvalidOperationException(
                    "[TcpTransport] 已经" + (m_state == ETransportState.Connecting ? "在连" : "连上") +
                    "了，不要再 Connect（当前：" + Description + "）。想换目标先 Close。");
            }

            if (string.IsNullOrEmpty(host))
            {
                throw new ArgumentException("[TcpTransport] 主机名不能为空。", nameof(host));
            }

            if (port <= 0 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port),
                    "[TcpTransport] 端口必须在 1~65535（当前 " + port + "）。");
            }

            // 重连前先把上一次的残留清干净（否则上一局的半帧会被当成这一局的开头）
            m_decoder.Reset();
            m_pending.Clear();
            m_writing = null;
            m_writingOffset = 0;
            m_closedReason = null;
            m_closeNotified = false;
            m_bytesSent = 0;
            m_bytesReceived = 0;
            m_framesSent = 0;
            m_framesReceived = 0;

            m_host = host;
            m_port = port;
            m_state = ETransportState.Connecting;
            m_connectTimer.Restart();

            try
            {
                IPAddress address = ResolveAddress(host);

                m_socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                m_socket.NoDelay = true;        // 见文件头 ②
                m_socket.Blocking = false;      // 见文件头 ①

                try
                {
                    m_socket.Connect(new IPEndPoint(address, port));
                    EnterConnected();           // 极快的情况下会直接连上
                }
                catch (SocketException ex)
                {
                    if (IsConnectPending(ex.SocketErrorCode))
                    {
                        return;                 // 正常：正在连，交给 Pump 推进
                    }

                    if (ex.SocketErrorCode == SocketError.IsConnected)
                    {
                        EnterConnected();
                        return;
                    }

                    CloseWithReason("连接 " + Endpoint + " 失败：" + DescribeError(ex.SocketErrorCode));
                }
            }
            catch (SocketException ex)
            {
                // 多半是域名解析失败（Dns.GetHostAddresses 抛的也是 SocketException）
                CloseWithReason("连接 " + Endpoint + " 失败：" + DescribeError(ex.SocketErrorCode));
            }
            catch (Exception ex)
            {
                CloseWithReason("连接 " + Endpoint + " 失败：" + ex.GetType().Name + "：" + ex.Message);
            }
        }

        /// <summary>发一条消息（载荷；分帧在这里做）。未连接**直接报错**，不静默丢。</summary>
        /// <param name="payload">载荷（可以为 null）。</param>
        public void Send(byte[] payload)
        {
            ThrowIfDisposed();

            if (m_state != ETransportState.Connected)
            {
                throw new InvalidOperationException(
                    "[TcpTransport] 还没连上就 Send（当前：" + Description + "）。" +
                    "静默丢消息只会变成\"偶尔少一条\"这种查不出的 bug，所以这里当场报错。");
            }

            byte[] frame = FrameCodec.Encode(payload);

            if (frame.Length - NetContract.FrameLengthPrefixBytes > m_maxFrameBytes)
            {
                throw new ArgumentException(
                    "[TcpTransport] 要发的载荷 " + (frame.Length - NetContract.FrameLengthPrefixBytes) +
                    " 字节，超过单帧上限 " + m_maxFrameBytes + "（对端会判协议违规直接断开）。",
                    nameof(payload));
            }

            m_pending.Enqueue(frame);

            // 立刻试着发一次：不然"忘了 Pump"就变成静默不发（Pump 里还会再冲刷一遍）
            FlushPending();
        }

        /// <summary>推进一帧：冲刷发送队列 + 收字节拆帧。</summary>
        public void Pump()
        {
            if (m_disposed)
            {
                return;
            }

            if (m_state == ETransportState.Connecting)
            {
                PumpConnecting();
            }

            if (m_state != ETransportState.Connected)
            {
                return;
            }

            FlushPending();

            if (m_state != ETransportState.Connected)
            {
                return;     // 冲刷过程中出错已经关掉了
            }

            ReceiveAvailable();
        }

        /// <summary>主动关闭（可重复调用；第一次之后状态就是 Closed）。</summary>
        public void Close()
        {
            CloseWithReason("主动关闭");
        }

        /// <summary>释放（等价于 `Close`；之后再 `Connect`/`Send` 会报 `ObjectDisposedException`）。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            Close();
            m_disposed = true;
        }

        // ====================================================================
        //  内部：连接推进
        // ====================================================================

        /// <summary>推进"正在连"这个状态（只在 <see cref="Pump"/> 里调）。</summary>
        private void PumpConnecting()
        {
            Socket socket = m_socket;

            if (socket == null)
            {
                // Connect 里分配套接字失败才会走到这（上面已经报了错），这里只兜底
                CloseWithReason("连接 " + Endpoint + " 失败：套接字没有建立");
                return;
            }

            try
            {
                if (socket.Poll(0, SelectMode.SelectError))
                {
                    CloseWithReason("连接 " + Endpoint + " 失败：" +
                                    DescribeError((SocketError)GetSocketError(socket)));
                    return;
                }

                if (socket.Poll(0, SelectMode.SelectWrite))
                {
                    // 可写了不等于连上了：Windows 上"连接被拒"也会让套接字变可写，
                    // 真正的结论在 SO_ERROR 里（0 = 成功）
                    int error = GetSocketError(socket);

                    if (error != 0)
                    {
                        CloseWithReason("连接 " + Endpoint + " 失败：" +
                                        DescribeError((SocketError)error));
                        return;
                    }

                    EnterConnected();
                    return;
                }
            }
            catch (SocketException ex)
            {
                CloseWithReason("连接 " + Endpoint + " 失败：" + DescribeError(ex.SocketErrorCode));
                return;
            }
            catch (ObjectDisposedException)
            {
                return;     // 回调里已经关掉了
            }

            if (m_connectTimer.ElapsedMilliseconds >= m_connectTimeoutMs)
            {
                CloseWithReason("连接 " + Endpoint + " 超时（" + m_connectTimeoutMs + "ms）");
            }
        }

        /// <summary>进入"已连接"。</summary>
        private void EnterConnected()
        {
            m_connectTimer.Stop();
            m_state = ETransportState.Connected;
        }

        // ====================================================================
        //  内部：发送
        // ====================================================================

        /// <summary>尽力把待发队列写出去（写不完就等下次 `Pump`，不是错误）。</summary>
        private void FlushPending()
        {
            Socket socket = m_socket;

            if (socket == null)
            {
                return;
            }

            while (m_state == ETransportState.Connected)
            {
                if (m_writing == null)
                {
                    if (m_pending.Count == 0)
                    {
                        return;
                    }

                    m_writing = m_pending.Dequeue();
                    m_writingOffset = 0;
                }

                int sent;

                try
                {
                    sent = socket.Send(m_writing, m_writingOffset, m_writing.Length - m_writingOffset,
                                       SocketFlags.None);
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.WouldBlock)
                    {
                        return;     // 内核发送缓冲满了：下次 Pump 继续
                    }

                    CloseWithReason("发送失败：" + DescribeError(ex.SocketErrorCode));
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

                m_writingOffset += sent;

                if (m_writingOffset < m_writing.Length)
                {
                    return;         // 部分写出：下次 Pump 从断点接着写
                }

                m_bytesSent += m_writing.Length;
                m_framesSent++;
                m_writing = null;
                m_writingOffset = 0;
            }
        }

        // ====================================================================
        //  内部：接收
        // ====================================================================

        /// <summary>把内核里已经收到的字节全部读干净，并拆成帧交出去。</summary>
        private void ReceiveAvailable()
        {
            Socket socket = m_socket;

            while (m_state == ETransportState.Connected && socket != null)
            {
                bool readable;

                try
                {
                    readable = socket.Poll(0, SelectMode.SelectRead);
                }
                catch (SocketException ex)
                {
                    CloseWithReason("等待接收失败：" + DescribeError(ex.SocketErrorCode));
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (!readable)
                {
                    return;         // 没东西可读，本帧到此为止
                }

                int available;

                try
                {
                    available = socket.Available;
                }
                catch (SocketException ex)
                {
                    CloseWithReason("查询可读字节失败：" + DescribeError(ex.SocketErrorCode));
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (available <= 0)
                {
                    // 见文件头 ③：可读但 0 字节 = 对端发了 FIN
                    CloseWithReason("对端关闭了连接（收到 0 字节）");
                    return;
                }

                int want = available < m_receiveBuffer.Length ? available : m_receiveBuffer.Length;
                int received;

                try
                {
                    received = socket.Receive(m_receiveBuffer, 0, want, SocketFlags.None);
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.WouldBlock)
                    {
                        return;
                    }

                    CloseWithReason("接收失败：" + DescribeError(ex.SocketErrorCode));
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (received <= 0)
                {
                    CloseWithReason("对端关闭了连接（收到 0 字节）");
                    return;
                }

                m_bytesReceived += received;
                m_decoder.Append(m_receiveBuffer, 0, received);
                DispatchFrames();
            }
        }

        /// <summary>把拆帧器里攒够的帧一条条交出去（0 长度帧也算一条）。</summary>
        private void DispatchFrames()
        {
            while (true)
            {
                byte[] frame;
                string error;

                if (!m_decoder.TryDequeue(out frame, out error))
                {
                    if (error != null)
                    {
                        CloseWithReason("分帧违规：" + error);
                    }

                    return;
                }

                m_framesReceived++;

                Action<byte[]> handler = FrameReceived;

                if (handler != null)
                {
                    handler(frame);
                }

                if (m_state != ETransportState.Connected)
                {
                    return;     // 回调里可能已经关掉了（例如收到"踢人"消息）
                }
            }
        }

        // ====================================================================
        //  内部：关闭与杂项
        // ====================================================================

        /// <summary>
        /// 关闭（带原因）。**幂等**：已经是 Closed 就直接返回，不会重复报 `Closed`。
        /// </summary>
        /// <param name="reason">人话原因。</param>
        private void CloseWithReason(string reason)
        {
            if (m_state == ETransportState.Closed)
            {
                return;
            }

            m_state = ETransportState.Closed;
            m_closedReason = reason;
            m_connectTimer.Stop();

            // 没发出去的消息不留着（留着也不会再发了，留着只会让排查时误以为"发过了"）
            m_pending.Clear();
            m_writing = null;
            m_writingOffset = 0;

            Socket socket = m_socket;
            m_socket = null;

            if (socket != null)
            {
                try
                {
                    socket.Close();
                }
                catch (SocketException)
                {
                    // 关的时候报错没有可做的动作（连接已经废了），忽略
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (!m_closeNotified)
            {
                m_closeNotified = true;
                Action<string> handler = Closed;

                if (handler != null)
                {
                    handler(reason);
                }
            }
        }

        /// <summary>把主机名解析成地址（优先 IPv4）。</summary>
        /// <param name="host">主机。</param>
        /// <returns>地址。</returns>
        private static IPAddress ResolveAddress(string host)
        {
            IPAddress literal;

            if (IPAddress.TryParse(host, out literal))
            {
                return literal;
            }

            IPAddress[] all = Dns.GetHostAddresses(host);

            if (all == null || all.Length == 0)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].AddressFamily == AddressFamily.InterNetwork)
                {
                    return all[i];      // 局域网/本机场景下 IPv4 更省事（也更容易看日志）
                }
            }

            return all[0];
        }

        /// <summary>读套接字的 `SO_ERROR`（0 = 没错误）。读不到就当 0。</summary>
        /// <param name="socket">套接字。</param>
        /// <returns>错误码。</returns>
        private static int GetSocketError(Socket socket)
        {
            try
            {
                return (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
            }
            catch (SocketException)
            {
                return 0;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
        }

        /// <summary>这个错误码是不是"连接正在进行中"（非阻塞连接的正常表现）。</summary>
        /// <param name="error">错误码。</param>
        /// <returns>正在进行返回 true。</returns>
        private static bool IsConnectPending(SocketError error)
        {
            return error == SocketError.WouldBlock
                || error == SocketError.InProgress
                || error == SocketError.AlreadyInProgress
                || error == SocketError.TryAgain;
        }

        /// <summary>把套接字错误码说成人话（联机排查时这几句能省很多时间）。</summary>
        /// <param name="error">错误码。</param>
        /// <returns>人话。</returns>
        private static string DescribeError(SocketError error)
        {
            switch (error)
            {
                case SocketError.ConnectionRefused:
                    return "对端没有在监听（连接被拒绝）";
                case SocketError.TimedOut:
                    return "超时";
                case SocketError.HostNotFound:
                    return "找不到这个主机名";
                case SocketError.HostUnreachable:
                    return "主机不可达";
                case SocketError.NetworkUnreachable:
                    return "网络不可达";
                case SocketError.ConnectionReset:
                    return "连接被对端重置（对方进程可能已经没了）";
                case SocketError.ConnectionAborted:
                    return "连接被中止";
                case SocketError.AccessDenied:
                    return "被拒绝（防火墙或权限）";
                case SocketError.AddressAlreadyInUse:
                    return "端口已被占用";
                default:
                    return error + "（" + (int)error + "）";
            }
        }

        /// <summary>`host:port`。</summary>
        private string Endpoint
        {
            get { return (string.IsNullOrEmpty(m_host) ? "(未知主机)" : m_host) + ":" + m_port; }
        }

        /// <summary>已释放还来调：直接报错（这是调用方的 bug，不该悄悄忽略）。</summary>
        private void ThrowIfDisposed()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException(nameof(TcpTransport),
                    "[TcpTransport] 已经 Dispose 过了，不能再 Connect/Send。");
            }
        }
    }
}
