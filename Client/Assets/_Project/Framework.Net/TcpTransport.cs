// 这个文件里有：
//   TcpTransport（类）：把 TCP 套接字包成 ITransport，负责长度前缀分帧、非阻塞连接与每帧拉取式收发。
//
// 注意：本程序集是 noEngineReferences 且只引 NBC.Shared，这里只能出现 BCL，写一行 UnityEngine 就是编译错误；
//       换来的是这份实现能被 .NET 8 探针直接编进去真跑，回环收发与粘包拆包都不需要 Unity 也能拿到证据。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NBC.Shared.Net;

namespace NBC.Framework.Net.Adapter
{
    // 主要功能：用 TCP 承载帧流——4 字节小端长度前缀分帧、非阻塞连接、每帧 Pump 里收字节拆帧并回调。
    // 主要数据成员：m_socket（套接字，未连为 null）、m_pending / m_writing / m_writingOffset（待发队列与正在写的帧）、m_decoder（拆帧器）、m_state（连接状态）。
    // 主要外部方法：Connect / Send / Pump / Close / Dispose，State / Stats / Description / IsConnected，事件 FrameReceived / Closed。
    // 谁在用它：Game 的 RuntimeNetDriver 以 CountingTransport(TcpTransport) 装配；Editor 的 NetDebugWindow 也 new 一个（外面同样包 CountingTransport）再交给 NetSession；TcpTransportTests 走真端口验证。
    /// <summary>TCP 传输实现：4 字节小端长度前缀分帧、非阻塞连接、每帧 Pump 拉取式收发。</summary>
    public sealed class TcpTransport : ITransport
    {
        /// <summary>默认连接超时，毫秒；取 5000，即 5 秒。</summary>
        public const int DefaultConnectTimeoutMs = 5000;

        // 接收缓冲大小，字节；一次 Receive 最多读这么多（16 KiB），整条连接复用同一块数组，避免每帧分配。
        private const int ReceiveBufferBytes = 16 * 1024;

        // 连接超时，毫秒；取值必须 > 0，构造时校验，Pump 推进握手时用它判超时。
        private readonly int m_connectTimeoutMs;

        // 单帧载荷上限，字节；收发同一口径，默认 NetContract.MaxFrameBytes（524288，即 512 KiB）。
        private readonly int m_maxFrameBytes;

        // 拆帧器；粘包、拆包与协议违规判断都在它里面，重连时 Reset。
        private readonly FrameDecoder m_decoder;

        // 待发队列；元素是已经包好 4 字节长度前缀的整帧，不是裸载荷。
        private readonly Queue<byte[]> m_pending = new Queue<byte[]>();

        // 连接计时器，毫秒；Connect 时 Restart、连上或关闭时 Stop，用来判连接超时。
        private readonly Stopwatch m_connectTimer = new Stopwatch();

        // 接收缓冲，字节；长度 16 KiB，整条连接复用，避免每次收都新分配。
        private readonly byte[] m_receiveBuffer = new byte[ReceiveBufferBytes];

        // 底层套接字；未连接或已关闭时为 null。
        private Socket m_socket;

        // 正在往外写的那一帧（已包好帧头）；写了一半就留到下次 Pump 接着写，空闲为 null。
        private byte[] m_writing;

        // m_writing 里已经写出的字节数，范围 0..m_writing.Length；0 表示还没开始写。
        private int m_writingOffset;

        // 目标主机（IP 或域名）；只用于日志与端点描述，null 表示从未连接。
        private string m_host;

        // 目标端口，取值 1~65535；只用于日志与端点描述。
        private int m_port;

        // 当前连接状态，取值见 ETransportState；初值 Disconnected。
        private ETransportState m_state = ETransportState.Disconnected;

        // 关闭原因（人话）；null 表示还没关闭，非 null 时 Description 会把它带出来。
        private string m_closedReason;

        // Closed 是否已经报过；契约是至多一次，关闭路径靠它去重。
        private bool m_closeNotified;

        // 是否已经 Dispose；置上之后再 Connect / Send 就是调用方的 bug，会抛 ObjectDisposedException。
        private bool m_disposed;

        // 本次连接累计发出的字节数（含 4 字节帧头），单位字节；Connect 时清零。
        private long m_bytesSent;
        // 本次连接累计收到的字节数（含 4 字节帧头），单位字节；Connect 时清零。
        private long m_bytesReceived;
        // 本次连接完整写出的帧数，单位帧；只算整帧写完的，Connect 时清零。
        private int m_framesSent;
        // 本次连接收到的完整帧数，单位帧；0 长度帧也算一帧，Connect 时清零。
        private int m_framesReceived;

        /// <summary>造一个 TCP 传输，此时还没有连接；参数越界当场抛。</summary>
        /// <param name="connectTimeoutMs">连接超时（毫秒），必须 > 0；默认 DefaultConnectTimeoutMs。</param>
        /// <param name="maxFrameBytes">单帧载荷上限（字节），必须 > 0；默认 NetContract.MaxFrameBytes。</param>
        public TcpTransport(int connectTimeoutMs = DefaultConnectTimeoutMs,
                            int maxFrameBytes = NetContract.MaxFrameBytes)
        {
            // 功能：校验参数后记下连接超时与单帧上限，并造好配套的拆帧器。
            // 入参：connectTimeoutMs 连接超时（毫秒，必须 > 0）；maxFrameBytes 单帧载荷上限（字节，必须 > 0）
            // 返回：无
            // 步骤：
            //   1) 先挡两个非正数：超时 <= 0 会让连接立刻判超时，上限 <= 0 会让任何帧都算超限
            //   2) 记下两个值，供 Connect 判超时与 Send 判上限
            //   3) 拆帧器与发送端用同一个上限，保证「发得出」与「收得下」是同一口径
            // 上游：Game 的 RuntimeNetDriver（装在 CountingTransport 里）、Editor 的 NetDebugWindow、TcpTransportTests
            // 下游：FrameDecoder 构造
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

        /// <summary>当前连接状态，取值见 ETransportState。</summary>
        public ETransportState State
        {
            get { return m_state; }
        }

        /// <summary>本次连接的收发统计快照；字节含帧头，Connect 时清零，口径是「这一次连接」而不是进程生命周期。</summary>
        public TransportStats Stats
        {
            get { return new TransportStats(m_bytesSent, m_bytesReceived, m_framesSent, m_framesReceived); }
        }

        /// <summary>一句人话端点描述，形如 tcp 127.0.0.1:7777；未连接时为 tcp (未连接)，关闭后附带原因。</summary>
        public string Description
        {
            get
            {
                string endpoint = string.IsNullOrEmpty(m_host) ? "(未连接)" : m_host + ":" + m_port;
                return m_closedReason == null ? "tcp " + endpoint : "tcp " + endpoint + "（" + m_closedReason + "）";
            }
        }

        /// <summary>是否已连上，等价于 State 为 Connected；只有该状态才能 Send。</summary>
        public bool IsConnected
        {
            get { return m_state == ETransportState.Connected; }
        }

        /// <summary>收到一整帧载荷时回调；只在 Pump 里、由调用 Pump 的那条线程触发，0 长度帧也会回调。</summary>
        public event Action<byte[]> FrameReceived;

        /// <summary>断开或出错时回调，参数是一句人话原因；至多触发一次，主动关闭时原因是「主动关闭」。</summary>
        public event Action<string> Closed;

        /// <summary>发起连接并立即返回；握手进度靠 Pump 推进，连不上不抛异常，改由 Closed 报原因。</summary>
        /// <param name="host">主机，IP 或域名，不能为空。</param>
        /// <param name="port">端口，取值 1~65535。</param>
        public void Connect(string host, int port)
        {
            // 功能：把传输层从「未连」切到「正在连」，建好非阻塞套接字，真正的握手留给后面的 Pump。
            // 入参：host 主机（IP 或域名，不能为空）；port 端口（1~65535）
            // 返回：无
            // 步骤：
            //   1) 已释放、已在连或已连上都当场抛错，避免一个实例背负两条连接
            //   2) 校验主机与端口，空主机与越界端口在这里就挡住
            //   3) 清掉上一局的拆帧缓冲、待发队列、统计与关闭标记——否则上一局的半帧会被当成这一局的开头
            //   4) 记下目标、置为 Connecting 并启动计时器；此刻仍在异步握手中，失败只能走 Closed
            //   5) 解析地址后建非阻塞套接字：关掉 Nagle，并让 Connect 立刻返回
            //   6) 非阻塞下「正在进行」是正常结果，交给 Pump；已经连上的极快情况直接进 Connected
            //   7) 其余错误一律转成 Closed 的人话原因，不允许停在 Connecting；域名解析失败抛的也是 SocketException，所以外层套接字与其它异常两个 catch 都要兜
            // 注意：Blocking 为 false 时 Connect 不会等待，调用方别 sleep 等连接；连不上必须走 Closed，否则会一直停在 Connecting，谁也看不懂。
            // 注意：NoDelay 关掉 Nagle，否则小包会被攒起来、最多约 200ms 才出门，30Hz 的动作战斗里就是操作一顿一顿；代价是包变多，但包本来就小、频率也就 30Hz，值得。
            // 上游：NetSession 的登录与重连流程、Editor 的 NetDebugWindow
            // 下游：Pump（推进握手）、CloseWithReason、Closed
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
                m_socket.NoDelay = true;
                m_socket.Blocking = false;

                try
                {
                    m_socket.Connect(new IPEndPoint(address, port));
                    EnterConnected();
                }
                catch (SocketException ex)
                {
                    if (IsConnectPending(ex.SocketErrorCode))
                    {
                        return;
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
                CloseWithReason("连接 " + Endpoint + " 失败：" + DescribeError(ex.SocketErrorCode));
            }
            catch (Exception ex)
            {
                CloseWithReason("连接 " + Endpoint + " 失败：" + ex.GetType().Name + "：" + ex.Message);
            }
        }

        /// <summary>发一条载荷，分帧在这里做；未连接直接抛异常，不静默丢。</summary>
        /// <param name="payload">载荷，可以为 null，表示一条 0 长度帧。</param>
        public void Send(byte[] payload)
        {
            // 功能：把一段载荷包上 4 字节长度前缀后入队，并立刻尝试写出去。
            // 入参：payload 载荷（可以为 null，表示一条 0 长度帧）；载荷超过单帧上限会被拒收
            // 返回：无
            // 步骤：
            //   1) 已释放、未连上都当场抛错——静默丢会变成偶发少一条消息，是最难查的一类问题
            //   2) 先编码成帧，再按「帧长 - 4 字节帧头」比对上限，保证本端发得出的对端收得下
            //   3) 入队后立刻冲刷一次，免得调用方忘了 Pump 就静默不发
            // 注意：上限比的是载荷长度，不含 4 字节帧头，与 FrameDecoder 的口径一致；超限当场抛，别等对端按协议违规断开。
            // 上游：NetSession 的握手、心跳与业务消息发送
            // 下游：FrameCodec.Encode、FlushPending
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

            FlushPending();
        }

        /// <summary>推进一帧：推进握手、冲刷发送队列、收字节并拆帧。</summary>
        public void Pump()
        {
            // 功能：每帧调一次，把这一帧能做的都做掉：推进握手、冲刷待发、读干净内核里已到的字节并拆帧。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 已释放就直接返回，不碰任何状态
            //   2) 正在连就先推进握手；没连上就此打住，本帧不做事
            //   3) 先冲刷待发再读取，让本轮之前排队的消息优先出门
            //   4) 冲刷过程中可能因出错而关闭，所以再查一次状态
            //   5) 读干净内核缓冲并拆帧，每凑齐一帧回调一次 FrameReceived
            // 注意：回调只在调用 Pump 的这条线程上发生（通常是主线程），所以回调里可以直接碰 Transform 与 UI；也意味着别的线程不能调 Pump。
            // 上游：NetSession.Pump 每帧调用（由 NetDebugWindow 与 RuntimeNetDriver 驱动）
            // 下游：PumpConnecting / FlushPending / ReceiveAvailable / DispatchFrames、Closed
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
                return;
            }

            ReceiveAvailable();
        }

        /// <summary>主动关闭，可重复调用；第一次之后状态就是 Closed。</summary>
        public void Close()
        {
            // 功能：以「主动关闭」为原因关闭连接，未发出的消息随之丢弃，并至多上报一次 Closed。
            CloseWithReason("主动关闭");
        }

        /// <summary>释放：等价于 Close；之后再 Connect / Send 会抛 ObjectDisposedException。</summary>
        public void Dispose()
        {
            // 功能：先关闭连接，再把自己标记成已释放；重复调用没有副作用。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 已经释放就直接返回，保证幂等
            //   2) 先 Close 再置标记：Close 里还要用套接字，顺序反了会漏关
            // 上游：using 语句、NetSession 与 NetDebugWindow 的销毁流程
            // 下游：Close
            if (m_disposed)
            {
                return;
            }

            Close();
            m_disposed = true;
        }

        // 功能：在 Connecting 状态下问一次套接字「出错了吗 / 能写了吗」，据此连上、报错或继续等。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 套接字为 null 说明 Connect 里连分配都没成功（那边已经报过错），这里只兜底关闭
        //   2) 先查 SelectError 再查 SelectWrite，出错优先于可写
        //   3) 可写不等于连上：Windows 上「连接被拒」也会让套接字变可写，真正的结论在 SO_ERROR 里，非 0 即失败
        //   4) 两项都过才进 Connected
        //   5) 最后拿计时器判超时，超时就关掉并报出毫秒数
        // 注意：这里只 Poll 一次、不等待，握手靠每次 Pump 推进；调用方不每帧调 Pump，连接会一直停在 Connecting，连超时都判不出来。
        // 上游：Pump
        // 下游：EnterConnected、CloseWithReason、GetSocketError、DescribeError
        private void PumpConnecting()
        {
            Socket socket = m_socket;

            if (socket == null)
            {
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
                return;
            }

            if (m_connectTimer.ElapsedMilliseconds >= m_connectTimeoutMs)
            {
                CloseWithReason("连接 " + Endpoint + " 超时（" + m_connectTimeoutMs + "ms）");
            }
        }

        // 功能：把状态切到 Connected 并停掉连接计时器。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先停计时器，之后不再用连接超时
        //   2) 再改状态，保证状态一变成 Connected 时计时器已经不会再跳
        // 上游：Connect（极快连上时）、PumpConnecting
        // 下游：无
        private void EnterConnected()
        {
            m_connectTimer.Stop();
            m_state = ETransportState.Connected;
        }

        // 功能：按队列顺序把整帧尽量写出去，写不完就停在断点等下次 Pump。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 套接字为 null 说明没连上或已关，直接返回
        //   2) 没有正在写的帧就从队列取一个，取出即视为开始写
        //   3) 从 m_writingOffset 处续写，Send 只发剩下的部分
        //   4) WouldBlock 是正常情况：内核发送缓冲满了，保留断点等下次
        //   5) 整帧写满才累计字节与帧数——统计口径是「完整写出去」；Send 返回 <= 0 理论上不会发生，不推进也不假装成功
        // 注意：部分写出是正常的，不是错误；把「没写完」当失败关连接，弱网下会频繁掉线。
        // 注意：统计在整帧写完后才加，别在入队时加，否则看板会把还没发出去的消息算进去。
        // 上游：Send、Pump
        // 下游：Socket.Send
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
                        return;
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
                    return;
                }

                m_writingOffset += sent;

                if (m_writingOffset < m_writing.Length)
                {
                    return;
                }

                m_bytesSent += m_writing.Length;
                m_framesSent++;
                m_writing = null;
                m_writingOffset = 0;
            }
        }

        // 功能：把内核里当前已经到达的字节读干净，喂给拆帧器并逐帧回调。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 状态不是 Connected 或套接字已空就停
        //   2) 用 Poll(SelectRead) 问有没有东西；没有就结束本帧
        //   3) 可读但 Available 为 0，说明对端发了 FIN，必须按断开处理
        //   4) 一次最多读满接收缓冲，读到的字节先计入统计再喂给拆帧器
        //   5) 立刻拆帧，把凑齐的帧交出去
        // 注意：可读但收到 0 字节就是对端关闭；漏掉这条会表现成「对方早已退出，本端还一直以为自己连着」，而且只在联机对局里才看得见。
        // 注意：一次 Receive 可能只拿到半帧，也可能三帧粘在一起，边界全部交给 FrameDecoder，上层永远看不到粘包与拆包。
        // 上游：Pump
        // 下游：FrameDecoder.Append、DispatchFrames、CloseWithReason
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
                    return;
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

        // 功能：把拆帧器里攒够的帧逐条回调给 FrameReceived，直到取不出整帧。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 循环 TryDequeue，取不到就停；若带 error 说明协议违规，按违规关闭
        //   2) 每交出一条先记一帧，再把事件取成本地快照后调用，回调里退订也不影响本次调用
        //   3) 回调里可能已经关闭（例如收到踢人消息），所以每次回调后都重查状态
        // 注意：0 长度帧也算一条，必须照常回调——protobuf 的空消息就是 0 字节，丢了等于丢空心跳。
        // 上游：ReceiveAvailable
        // 下游：FrameReceived、CloseWithReason
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
                    return;
                }
            }
        }

        // 功能：按给定原因关闭连接，清掉未发数据并至多上报一次 Closed。
        // 入参：reason 关闭原因（人话，会进 Description，也会作为 Closed 的参数）
        // 返回：无
        // 步骤：
        //   1) 已经是 Closed 就直接返回，保证幂等、不重复报 Closed
        //   2) 先置状态与原因、再停计时器，避免关闭过程中还有代码按旧状态动作
        //   3) 清空待发队列与正在写的帧——留着也不会再发，只会让排查时误以为已经发过
        //   4) 先摘掉套接字引用再关它；关闭时的报错一律忽略，连接已经废了，没有可做的动作
        //   5) 最后按 m_closeNotified 报一次 Closed；回调里可能重入 Close，所以先置标记再取快照
        // 注意：Closed 的契约是至多一次，重连时要靠 Connect 复位 m_closeNotified，漏了复位会让新连接的关闭原因永远报不出来。
        // 上游：Connect、PumpConnecting、FlushPending、ReceiveAvailable、DispatchFrames、Close
        // 下游：Closed
        private void CloseWithReason(string reason)
        {
            if (m_state == ETransportState.Closed)
            {
                return;
            }

            m_state = ETransportState.Closed;
            m_closedReason = reason;
            m_connectTimer.Stop();

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

        // 功能：把主机名解析成地址，优先挑 IPv4。
        // 入参：host 主机（IP 字面量或域名，不能为空）
        // 返回：解析出的地址；域名一个地址都解析不出来时抛 SocketException(HostNotFound)
        // 步骤：
        //   1) 能直接解析成 IP 就原样返回，省掉一次 DNS
        //   2) 否则查 DNS；结果为空就按 HostNotFound 抛，让上层统一转成 Closed 的原因
        //   3) 优先返回 IPv4：局域网与本机场景更省事，日志也更好看
        //   4) 没有 IPv4 才退回第一个地址
        // 上游：Connect
        // 下游：Dns.GetHostAddresses
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
                    return all[i];
                }
            }

            return all[0];
        }

        // 功能：读套接字的 SO_ERROR，用来判断非阻塞连接到底成没成。
        // 入参：socket 套接字
        // 返回：错误码，0 = 没错误；读不到也按 0 处理
        // 步骤：
        //   1) 直接读 SocketOptionName.Error
        //   2) 读取时套接字已被关或出错就返回 0，不在已经出错的路径上再抛一次
        // 上游：PumpConnecting
        // 下游：无
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

        // 功能：判断错误码是不是「非阻塞连接正在进行中」的正常表现。
        // 入参：error 套接字错误码
        // 返回：true = 还在握手，交给 Pump 推进；false = 真的失败了
        // 步骤：
        //   1) WouldBlock、InProgress、AlreadyInProgress、TryAgain 都算进行中——各平台报的不一样，四个都要认
        // 上游：Connect
        // 下游：无
        private static bool IsConnectPending(SocketError error)
        {
            return error == SocketError.WouldBlock
                || error == SocketError.InProgress
                || error == SocketError.AlreadyInProgress
                || error == SocketError.TryAgain;
        }

        // 功能：把套接字错误码翻成一句中文人话，供日志与 Closed 的原因使用。
        // 入参：error 套接字错误码
        // 返回：人话描述；没收录的码退回成「枚举（整数值）」，不丢信息
        // 步骤：
        //   1) 常见联机故障逐一映射：拒绝、超时、DNS、不可达、重置、中止、权限、端口占用
        //   2) 没收录的码把枚举名与整数值都打出来，方便对照文档查
        // 上游：Connect、PumpConnecting、FlushPending、ReceiveAvailable
        // 下游：无
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

        // host:port 形式的端点文本，只用于日志与错误消息；主机为空时形如 (未知主机):7777。
        private string Endpoint
        {
            get { return (string.IsNullOrEmpty(m_host) ? "(未知主机)" : m_host) + ":" + m_port; }
        }

        // 功能：已释放还来调时当场抛错，把调用方的 bug 暴露在调用点。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 只查 m_disposed；置上之后 Connect 与 Send 都先过这一关
        // 上游：Connect、Send
        // 下游：无
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
