// ============================================================================
//  NetSession —— 客户端的一条联机会话：连接 → 握手 → 心跳 → 收包分发
//  项目：3D联网战斗Demo   对应：M3-S3（Docs\25 §八）、需求文档 §13.3
//
//  ---------------------------------------------------------------------------
//  一、它在整条链上的位置
//  ---------------------------------------------------------------------------
//      NetDebugWindow / 将来的战斗流程      ← 上层（点按钮、显示状态）
//                  ↓
//              NetSession                    ← 本文件：会话状态机 + 协议语义
//                  ↓
//        ITransport（接缝）→ TcpTransport    ← S2b：只管"收发一条条载荷"
//
//  ⚠️ 分工的判据：**传输层不认识 protobuf，会话层不认识 socket。**
//     `ITransport` 收发的是**字节载荷**；"这是一条 HandshakeAck / 那是一条 Pong"
//     是**协议语义**，属于本文件。混在一起的话，"换 UDP"与"改协议"两件事会互相绊住。
//
//  ---------------------------------------------------------------------------
//  二、时间**从外面注入**（`Pump(deltaMs)`）—— 这是能测的前提
//  ---------------------------------------------------------------------------
//  心跳节奏、握手超时、心跳超时、RTT 全都基于一个本地时钟，而这个时钟由调用方**喂**进来：
//      Unity 编辑器/播放：`Pump((int)(deltaSeconds * 1000))`
//      EditMode 用例：     `session.Pump(1000)` —— 精确到毫秒，**不用 sleep、不会 flaky**
//  与 E1 的性能看板、A4 的 Mono 宿主同一条思路（本项目的老规矩：能注入的时间就注入）。
//
//  ---------------------------------------------------------------------------
//  三、三个"不写就会以别的 bug 面目出现"的地方
//  ---------------------------------------------------------------------------
//  ① **握手也要有超时**。连上了但服务端不说话（版本不兼容 / 卡住 / 对面根本不是我们的服务端）
//     —— 没有握手超时的话，界面就永远停在"连接中"，日志什么都没有。
//  ② **收到先入队，`transport.Pump()` 返回后再处理**。`FrameReceived` 是在传输层内部触发的，
//     在那里直接改会话状态 = "在收发过程中改状态表"。服务端那份（`ServerMessagePump`）同理，
//     两边保持同一种心智模型，联调时不用切换脑子。
//  ③ **心跳超时要真的断开**，不能只把状态标成"掉线"：留着一条半死的 socket，
//     下一次 `Send` 会以为还连着 —— 那正是"偶尔丢一条"的来源。
// ============================================================================

using System;
using System.Collections.Generic;
using Google.Protobuf;
using NBC.Framework.Input;
using NBC.Framework.Net;
using NBC.Protocol;
using NBC.Shared.Net;

namespace NBC.Game.Net
{
    /// <summary>会话状态。</summary>
    public enum ESessionState
    {
        /// <summary>没连。</summary>
        Disconnected = 0,

        /// <summary>正在连（`TcpTransport` 的非阻塞连接阶段）。</summary>
        Connecting = 1,

        /// <summary>连上了，正在等握手结果。</summary>
        Handshaking = 2,

        /// <summary>握手完成，可以收发。</summary>
        Online = 3,

        /// <summary>失败（原因在 `FailureReason` 里；此时连接已经被关掉）。</summary>
        Failed = 4
    }

    /// <summary>一条联机会话：握手、心跳、收包分发（**不认识 socket**）。</summary>
    public sealed class NetSession : IDisposable
    {
        /// <summary>默认心跳间隔（毫秒）。</summary>
        public const int DefaultHeartbeatIntervalMs = 1000;

        /// <summary>默认心跳超时（毫秒）：这么久没收到 Pong 就判定掉线。</summary>
        public const int DefaultHeartbeatTimeoutMs = 5000;

        /// <summary>默认握手超时（毫秒）。</summary>
        public const int DefaultHandshakeTimeoutMs = 5000;

        /// <summary>界面里最多留几条掉落记录。</summary>
        public const int MaxRecentDrops = 20;

        /// <summary>传输（接缝；测试里塞 `FakeTransport`）。</summary>
        private readonly ITransport m_transport;

        /// <summary>玩家名（M3 还没有账号系统，只用于日志/席位显示）。</summary>
        private readonly string m_playerName;

        /// <summary>客户端版本标识（只用于服务端排查）。</summary>
        private readonly string m_clientVersion;

        private readonly int m_heartbeatIntervalMs;
        private readonly int m_heartbeatTimeoutMs;
        private readonly int m_handshakeTimeoutMs;

        /// <summary>收到的原始载荷，等 `transport.Pump()` 返回之后再处理（见文件头 ②）。</summary>
        private readonly Queue<byte[]> m_inbox = new Queue<byte[]>();

        /// <summary>本地时钟（由调用方每次 `Pump` 喂进来）。</summary>
        private long m_clockMs;

        /// <summary>进入"等握手"的那一刻（算握手超时用）。</summary>
        private long m_handshakeStartedMs = -1;

        /// <summary>上次发心跳的时刻（-1 = 还没发过）。</summary>
        private long m_lastPingSentMs = -1;

        /// <summary>上次收到 Pong 的时刻。</summary>
        private long m_lastPongReceivedMs = -1;

        /// <summary>当前状态。</summary>
        private ESessionState m_state = ESessionState.Disconnected;

        /// <summary>失败原因（人话；非 null = 失败过）。</summary>
        private string m_failureReason;

        /// <summary>握手结果（成功后有）。</summary>
        private HandshakeAck m_ack;

        /// <summary>服务端最后一次下发的房间状态（含"你不在任何房间"的那份）。</summary>
        private RoomState m_room;

        /// <summary>最近一次被拒的错误。</summary>
        private ErrorResponse m_lastError;

        /// <summary>客户端的世界状态（服务端快照的本地副本）。</summary>
        private readonly SnapshotView m_world = new SnapshotView();

        /// <summary>最近几次掉落（新的在前；界面用）。</summary>
        private readonly List<DropEvent> m_recentDrops = new List<DropEvent>();

        /// <summary>最近一条掉落。</summary>
        private DropEvent m_lastDrop;

        /// <summary>收到第一张快照时记一句就够（每帧记会把日志刷爆）。</summary>
        private bool m_loggedFirstSnapshot;

        /// <summary>发出去的输入序号（见 `NextInputSeq`）。</summary>
        private int m_inputSeq;

        /// <summary>最近一次测到的往返延迟（毫秒；-1 = 还没测到）。</summary>
        private int m_rttMs = -1;

        /// <summary>是不是"我们主动关的"（用来区分 `Closed` 事件的两条来路）。</summary>
        private bool m_closingIntentionally;

        /// <summary>是否已经 Dispose。</summary>
        private bool m_disposed;

        /// <summary>
        /// 建一条会话（此时还没连）。
        /// </summary>
        /// <param name="transport">传输实现（Unity 里是 `TcpTransport`，测试里是 `FakeTransport`）。</param>
        /// <param name="playerName">玩家名。</param>
        /// <param name="clientVersion">客户端版本标识。</param>
        /// <param name="heartbeatIntervalMs">心跳间隔。</param>
        /// <param name="heartbeatTimeoutMs">心跳超时。</param>
        /// <param name="handshakeTimeoutMs">握手超时。</param>
        public NetSession(
            ITransport transport,
            string playerName = "玩家",
            string clientVersion = "nbc-unity",
            int heartbeatIntervalMs = DefaultHeartbeatIntervalMs,
            int heartbeatTimeoutMs = DefaultHeartbeatTimeoutMs,
            int handshakeTimeoutMs = DefaultHandshakeTimeoutMs)
        {
            if (transport == null)
            {
                throw new ArgumentNullException(nameof(transport));
            }

            if (heartbeatIntervalMs <= 0 || heartbeatTimeoutMs <= 0 || handshakeTimeoutMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(heartbeatIntervalMs),
                    "[NetSession] 心跳间隔/心跳超时/握手超时都必须是正数。");
            }

            m_transport = transport;
            m_playerName = string.IsNullOrEmpty(playerName) ? "玩家" : playerName;
            m_clientVersion = string.IsNullOrEmpty(clientVersion) ? "nbc-unity" : clientVersion;
            m_heartbeatIntervalMs = heartbeatIntervalMs;
            m_heartbeatTimeoutMs = heartbeatTimeoutMs;
            m_handshakeTimeoutMs = handshakeTimeoutMs;

            m_transport.FrameReceived += OnFrameReceived;
            m_transport.Closed += OnTransportClosed;
        }

        // ====================================================================
        //  查询
        // ====================================================================

        /// <summary>当前状态。</summary>
        public ESessionState State
        {
            get { return m_state; }
        }

        /// <summary>失败原因（null = 没失败过）。</summary>
        public string FailureReason
        {
            get { return m_failureReason; }
        }

        /// <summary>握手结果（成功后有；失败时为 null）。</summary>
        public HandshakeAck Ack
        {
            get { return m_ack; }
        }

        /// <summary>服务端分配的玩家 id（0 = 还没身份）。</summary>
        public long PlayerId
        {
            get { return m_ack == null ? 0 : m_ack.PlayerId; }
        }

        /// <summary>最近一次往返延迟（毫秒；-1 = 还没测到）。</summary>
        public int RttMs
        {
            get { return m_rttMs; }
        }

        /// <summary>本地时钟（毫秒，由 `Pump` 累加）。</summary>
        public long ClockMs
        {
            get { return m_clockMs; }
        }

        /// <summary>发出去过多少个心跳。</summary>
        public long PingsSent { get; private set; }

        /// <summary>发出去过多少条输入（S5b）。</summary>
        public long InputsSent { get; private set; }

        /// <summary>收到过多少个 Pong。</summary>
        public long PongsReceived { get; private set; }

        /// <summary>传输层收发统计。</summary>
        public TransportStats TransportStats
        {
            get { return m_transport.Stats; }
        }

        /// <summary>是不是在线（握手完成且没断）。</summary>
        public bool IsOnline
        {
            get { return m_state == ESessionState.Online; }
        }

        /// <summary>
        /// 当前房间状态（服务端最后一次下发的）。
        /// <para>
        /// ⚠️ 判断"在不在房间里"要用 <see cref="InRoom"/>：服务端离开房间时会下发一份
        /// **房号为空**的状态（约定见 `RoomService` 文件头第二节），所以"有状态"不等于"在房里"。
        /// </para>
        /// </summary>
        public RoomState CurrentRoom
        {
            get { return m_room; }
        }

        /// <summary>现在在不在房间里（房号非空才算）。</summary>
        public bool InRoom
        {
            get { return m_room != null && !string.IsNullOrEmpty(m_room.RoomId); }
        }

        /// <summary>最近一次被拒的错误（null = 还没被拒过）。</summary>
        public ErrorResponse LastError
        {
            get { return m_lastError; }
        }

        /// <summary>最近一次掉落（null = 还没掉过东西）。<b>概率掉落</b>：狼有 35% 什么都不掉（见 DropTable 表）。</summary>
        public DropEvent LastDrop
        {
            get { return m_lastDrop; }
        }

        /// <summary>累计收到多少条掉落事件。</summary>
        public long DropsReceived { get; private set; }

        /// <summary>最近几次掉落（新的在前，最多 `MaxRecentDrops` 条）—— 界面直接显示它。</summary>
        public IReadOnlyList<DropEvent> RecentDrops
        {
            get { return m_recentDrops; }
        }

        /// <summary>
        /// 客户端的世界状态（**只由服务端快照决定**，见 `SnapshotView` 文件头）。
        /// <para>表现层（建 GameObject、播动画）去读它，而不是自己去算。</para>
        /// </summary>
        public SnapshotView World
        {
            get { return m_world; }
        }

        /// <summary>一句人话（编辑器窗口/日志直接用）。</summary>
        public string Description
        {
            get
            {
                switch (m_state)
                {
                    case ESessionState.Online:
                        return "在线（玩家 " + PlayerId + "，RTT " + (m_rttMs < 0 ? "?" : m_rttMs + "ms") + "）";
                    case ESessionState.Failed:
                        return "失败：" + m_failureReason;
                    case ESessionState.Disconnected:
                        return "未连接";
                    default:
                        return m_state == ESessionState.Connecting ? "连接中…" : "握手中…";
                }
            }
        }

        // ====================================================================
        //  事件
        // ====================================================================

        /// <summary>状态变化。</summary>
        public event Action<ESessionState> StateChanged;

        /// <summary>握手成功（带服务端的答复）。</summary>
        public event Action<HandshakeAck> HandshakeCompleted;

        /// <summary>测到新的往返延迟。</summary>
        public event Action<int> RttUpdated;

        /// <summary>收到一条服务端消息（Pong 也会走这里，方便界面显示"有流量"）。</summary>
        public event Action<ServerMessage> MessageReceived;

        /// <summary>房间状态变了（服务端每次都**整份**下发，见 D5）。</summary>
        public event Action<RoomState> RoomChanged;

        /// <summary>请求被服务端拒了（带错误码与人话原因）。</summary>
        public event Action<ErrorResponse> ErrorReceived;

        /// <summary>收到一条掉落事件（**服务端掷的**，客户端只负责显示）。</summary>
        public event Action<DropEvent> DropReceived;

        /// <summary>值得记一句的事情（人话）。</summary>
        public event Action<string> Note;

        // ====================================================================
        //  连接 / 断开
        // ====================================================================

        /// <summary>发起连接（非阻塞；连上与否靠 `Pump` 推进）。</summary>
        /// <param name="host">主机。</param>
        /// <param name="port">端口。</param>
        public void Connect(string host, int port)
        {
            ThrowIfDisposed();

            if (m_state == ESessionState.Connecting || m_state == ESessionState.Handshaking
                || m_state == ESessionState.Online)
            {
                throw new InvalidOperationException(
                    "[NetSession] 已经在连/连上了，先 Disconnect 再连（当前：" + Description + "）。");
            }

            // 重连前把上一次的残留清干净（否则上一局的失败原因会跟着这一局）
            m_inbox.Clear();
            m_ack = null;
            m_room = null;
            m_lastError = null;
            m_world.Clear();
            m_loggedFirstSnapshot = false;
            m_recentDrops.Clear();
            m_lastDrop = null;
            m_failureReason = null;
            m_rttMs = -1;
            m_lastPingSentMs = -1;
            m_lastPongReceivedMs = -1;
            m_handshakeStartedMs = -1;
            m_closingIntentionally = false;
            PingsSent = 0;
            PongsReceived = 0;

            Log("连接 " + host + ":" + port + "…");
            SetState(ESessionState.Connecting);
            m_transport.Connect(host, port);
        }

        /// <summary>主动断开（幂等）。</summary>
        /// <param name="reason">原因（写进日志）。</param>
        public void Disconnect(string reason = "主动断开")
        {
            if (m_state == ESessionState.Disconnected)
            {
                return;
            }

            m_closingIntentionally = true;
            Log(reason);
            m_transport.Close();
            m_inbox.Clear();
            SetState(m_failureReason == null ? ESessionState.Disconnected : ESessionState.Failed);
        }

        /// <summary>释放（等价于断开；之后再 `Connect` 会报错）。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_transport.FrameReceived -= OnFrameReceived;
            m_transport.Closed -= OnTransportClosed;

            Disconnect("会话释放");
            m_transport.Dispose();
            m_disposed = true;
        }

        // ====================================================================
        //  发送
        // ====================================================================

        /// <summary>
        /// 发一条消息（**只有在线才能发**）。
        /// <para>握手/心跳由本类自己按节奏发，调用方不用管。</para>
        /// </summary>
        /// <param name="message">消息。</param>
        /// <returns>发出去返回 true。</returns>
        public bool Send(ClientMessage message)
        {
            ThrowIfDisposed();

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (m_state != ESessionState.Online)
            {
                // 没在线就发 = 调用方的 bug（和 `TcpTransport` 未连接就发同一条判据）
                throw new InvalidOperationException(
                    "[NetSession] 还没上线就 Send（当前：" + Description + "）。" +
                    "静默丢弃只会变成\"偶尔少一条\"，所以这里当场报错。");
            }

            m_transport.Send(message.ToByteArray());
            return true;
        }

        /// <summary>立刻发一次心跳（界面上的"发心跳"按钮用它；平时由 `Pump` 按节奏发）。</summary>
        public void SendPingNow()
        {
            if (m_state != ESessionState.Online)
            {
                return;
            }

            SendPing();
        }

        /// <summary>
        /// 请求进房（`roomId` 留空 = 让服务端分配/找一个）。
        /// <para>服务端的结果可能是 `RoomState`（成功）或 `ErrorResponse`（被拒，原因在 `LastError`）。</para>
        /// </summary>
        /// <param name="roomId">房号（留空 = 让服务端安排）。</param>
        /// <param name="dungeonId">想打的副本（留空房号时服务端按它找房/开房）。</param>
        public void JoinRoom(string roomId, int dungeonId)
        {
            Send(new ClientMessage
            {
                JoinRoom = new JoinRoomRequest
                {
                    RoomId = roomId ?? string.Empty,
                    DungeonId = dungeonId,
                },
            });
        }

        /// <summary>请求离开当前房间（结果同样走 `RoomChanged` / `LastError`）。</summary>
        public void LeaveRoom()
        {
            Send(new ClientMessage { LeaveRoom = new LeaveRoomRequest() });
        }

        /// <summary>
        /// 把一帧输入命令发出去（**只发意图**，D3；服务端决定走多远、打不打得到）。
        /// <para>沿用 A7 的量化约定：`InputCommand` 的轴就是协议里的轴，不需要换算。</para>
        /// </summary>
        /// <param name="command">A7 的输入命令。</param>
        /// <param name="targetEntityId">当前目标（服务端会校验）。</param>
        public void SendInput(InputCommand command, int targetEntityId = 0)
        {
            SendInput(command.MoveX, command.MoveY, command.ActionBits, command.ActionReleaseBits, targetEntityId);
        }

        /// <summary>
        /// 发一帧输入（轴的量化约定见 A7：-1000..1000 = -1..+1，第 0 位 = 技能1/普攻）。
        /// </summary>
        /// <param name="moveX">左右轴（-1000..1000）。</param>
        /// <param name="moveY">前后轴（-1000..1000）。</param>
        /// <param name="actionBits">本帧新按下的动作位。</param>
        /// <param name="actionReleaseBits">本帧新松开的动作位。</param>
        /// <param name="targetEntityId">当前目标（0 = 没有）。</param>
        public void SendInput(int moveX, int moveY, uint actionBits = 0, uint actionReleaseBits = 0,
                              int targetEntityId = 0)
        {
            Send(new ClientMessage
            {
                Input = new PlayerInput
                {
                    PlayerId = PlayerId,
                    ClientTick = NextInputSeq(),
                    MoveX = moveX,
                    MoveY = moveY,
                    ActionBits = actionBits,
                    ActionReleaseBits = actionReleaseBits,
                    TargetEntityId = targetEntityId,
                },
            });

            InputsSent++;
        }

        /// <summary>
        /// 下一条输入的序号。
        /// <para>
        /// ⚠️ 协议里这个字段叫 `client_tick`，但 M3 **没有客户端逻辑帧**（不做预测，D4）——
        /// 所以这里给的是"第几条输入"，只用于服务端日志对账，**不是**权威帧号。
        /// M4 做预测+回滚时，它才会变成真正的客户端帧号。
        /// </para>
        /// </summary>
        /// <returns>序号。</returns>
        private int NextInputSeq()
        {
            m_inputSeq++;
            return m_inputSeq;
        }

        // ====================================================================
        //  每帧推进
        // ====================================================================

        /// <summary>
        /// 推进一帧。**时间由调用方喂进来**（见文件头第二节）。
        /// </summary>
        /// <param name="deltaMs">距上次调用的毫秒数。</param>
        public void Pump(int deltaMs)
        {
            if (m_disposed)
            {
                return;
            }

            if (deltaMs < 0)
            {
                deltaMs = 0;
            }

            m_clockMs += deltaMs;

            // ① 传输层：收字节、拆帧（回调里只入队）
            m_transport.Pump();

            // ② 处理这一帧收到的消息
            DrainInbox();

            if (m_state == ESessionState.Failed || m_state == ESessionState.Disconnected)
            {
                return;
            }

            // ③ 刚连上 → 发握手
            if (m_state == ESessionState.Connecting && m_transport.IsConnected)
            {
                m_handshakeStartedMs = m_clockMs;
                SetState(ESessionState.Handshaking);
                SendHandshake();
            }

            // ④ 握手超时（见文件头 ①）
            if (m_state == ESessionState.Handshaking
                && m_clockMs - m_handshakeStartedMs > m_handshakeTimeoutMs)
            {
                Fail("握手超时（" + m_handshakeTimeoutMs + "ms 没等到 HandshakeAck，" +
                     "服务端可能版本不兼容或不是本协议）");
                return;
            }

            // ⑤ 在线：按节奏发心跳 + 判心跳超时
            if (m_state == ESessionState.Online)
            {
                if (m_lastPingSentMs < 0 || m_clockMs - m_lastPingSentMs >= m_heartbeatIntervalMs)
                {
                    SendPing();
                }

                if (m_clockMs - m_lastPongReceivedMs > m_heartbeatTimeoutMs)
                {
                    // 见文件头 ③：要真的断开，不能只标状态
                    Fail("心跳超时（" + m_heartbeatTimeoutMs + "ms 没收到 Pong）");
                }
            }
        }

        // ====================================================================
        //  内部：收发
        // ====================================================================

        /// <summary>传输层收到一条载荷：**只入队**（见文件头 ②）。</summary>
        /// <param name="payload">载荷。</param>
        private void OnFrameReceived(byte[] payload)
        {
            m_inbox.Enqueue(payload);
        }

        /// <summary>传输层关闭：区分"我们主动关的"和"对面/网络断的"。</summary>
        /// <param name="reason">传输层给的人话原因。</param>
        private void OnTransportClosed(string reason)
        {
            if (m_closingIntentionally)
            {
                return;     // 主动关的：状态由 `Disconnect` 负责
            }

            Fail("连接断开：" + reason);
        }

        /// <summary>把这一帧收到的消息解得处理掉（在 `transport.Pump()` 返回之后）。</summary>
        private void DrainInbox()
        {
            while (m_inbox.Count > 0)
            {
                byte[] payload = m_inbox.Dequeue();

                ServerMessage message;

                try
                {
                    message = ServerMessage.Parser.ParseFrom(payload);
                }
                catch (Exception ex)
                {
                    Fail("服务端发来的东西解不出 ServerMessage：" + ex.Message);
                    return;
                }

                Handle(message);

                if (m_state == ESessionState.Failed)
                {
                    return;
                }
            }
        }

        /// <summary>处理一条服务端消息。</summary>
        /// <param name="message">消息。</param>
        private void Handle(ServerMessage message)
        {
            switch (message.PayloadCase)
            {
                case ServerMessage.PayloadOneofCase.HandshakeAck:
                    HandleHandshakeAck(message.HandshakeAck);
                    break;

                case ServerMessage.PayloadOneofCase.Pong:
                    HandlePong(message.Pong);
                    break;

                case ServerMessage.PayloadOneofCase.RoomState:
                    HandleRoomState(message.RoomState);
                    break;

                case ServerMessage.PayloadOneofCase.Snapshot:
                    HandleSnapshot(message.Snapshot);
                    break;

                case ServerMessage.PayloadOneofCase.Error:
                    HandleError(message.Error);
                    break;

                case ServerMessage.PayloadOneofCase.Event:
                    HandleServerEvent(message.Event);
                    break;

                case ServerMessage.PayloadOneofCase.None:
                    // 0 长度帧解出来就是它：协议违规，别装作没看见
                    Fail("服务端发来一条没有 payload 的消息（协议违规）");
                    return;

                default:
                    if (m_state != ESessionState.Online)
                    {
                        Fail("握手还没完成就收到了 " + message.PayloadCase);
                        return;
                    }

                    Log("收到 " + message.PayloadCase);
                    break;
            }

            Action<ServerMessage> handler = MessageReceived;

            if (handler != null)
            {
                handler(message);
            }
        }

        /// <summary>处理握手结果。</summary>
        /// <param name="ack">服务端答复。</param>
        private void HandleHandshakeAck(HandshakeAck ack)
        {
            if (m_state == ESessionState.Online)
            {
                Log("重复的 HandshakeAck，已忽略");
                return;
            }

            if (ack == null || !ack.Accepted)
            {
                Fail("服务端拒绝握手：" + (ack == null ? "答复是空的" : ack.Reason));
                return;
            }

            m_ack = ack;
            m_lastPongReceivedMs = m_clockMs;
            SetState(ESessionState.Online);

            Log("握手成功 → 玩家 " + ack.PlayerId +
                "（服务端 " + ack.ServerVersion + "，协议 v" + ack.ProtocolVersion +
                "，tick " + ack.TickHz + "Hz）");

            Action<HandshakeAck> handler = HandshakeCompleted;

            if (handler != null)
            {
                handler(ack);
            }

            SendPing();     // 立刻测一次 RTT，界面上不用等一秒
        }

        /// <summary>处理 Pong：算出 RTT。</summary>
        /// <param name="pong">心跳答复。</param>
        private void HandlePong(Pong pong)
        {
            if (pong == null)
            {
                return;
            }

            PongsReceived++;
            m_lastPongReceivedMs = m_clockMs;

            // `client_time_ms` 带的是**我们自己的本地时钟**，所以这一减就是往返延迟
            int rtt = (int)(m_clockMs - pong.ClientTimeMs);

            if (rtt < 0)
            {
                rtt = 0;        // 时钟被外部重置过之类：不报负数（那会污染看板）
            }

            if (rtt != m_rttMs)
            {
                m_rttMs = rtt;

                Action<int> handler = RttUpdated;

                if (handler != null)
                {
                    handler(rtt);
                }
            }
        }

        /// <summary>处理房间状态（服务端整份下发，覆盖本地的即可）。</summary>
        /// <param name="state">新状态。</param>
        private void HandleRoomState(RoomState state)
        {
            if (state == null)
            {
                return;
            }

            m_room = state;

            if (string.IsNullOrEmpty(state.RoomId))
            {
                Log("你不在任何房间");
            }
            else
            {
                // 把席位表拼成人话：`r1（副本 1001）2/4 人：剑士[房主]、法师`
                var text = new System.Text.StringBuilder();
                text.Append("房间 ").Append(state.RoomId)
                    .Append("（副本 ").Append(state.DungeonId).Append("）")
                    .Append(state.Members.Count).Append('/').Append(state.Capacity).Append(" 人：");

                for (int i = 0; i < state.Members.Count; i++)
                {
                    if (i > 0)
                    {
                        text.Append('、');
                    }

                    text.Append(state.Members[i].PlayerName);

                    if (state.Members[i].IsHost)
                    {
                        text.Append("[房主]");
                    }
                }

                Log(text.ToString());
            }

            Action<RoomState> handler = RoomChanged;

            if (handler != null)
            {
                handler(state);
            }
        }

        /// <summary>处理"请求被拒"。</summary>
        /// <param name="error">错误。</param>
        private void HandleError(ErrorResponse error)
        {
            if (error == null)
            {
                return;
            }

            m_lastError = error;
            Log("被拒（" + error.Code + "）：" + error.Message);

            Action<ErrorResponse> handler = ErrorReceived;

            if (handler != null)
            {
                handler(error);
            }
        }

        /// <summary>
        /// 处理服务端事件（M3 只有掉落；伤害/死亡事件见 `ServerEvent` 的其它分支，留 S6+/M4）。
        /// <para>⚠️ **客户端只显示**：掷骰全在服务端（`DungeonBattle.RollDrops`），这里一个随机数都不掷。</para>
        /// </summary>
        /// <param name="serverEvent">事件。</param>
        private void HandleServerEvent(ServerEvent serverEvent)
        {
            if (serverEvent == null || serverEvent.Drop == null)
            {
                return;
            }

            DropEvent drop = serverEvent.Drop;
            DropReceived?.Invoke(drop);

            DropsReceived++;
            m_lastDrop = drop;

            m_recentDrops.Insert(0, drop);

            while (m_recentDrops.Count > MaxRecentDrops)
            {
                m_recentDrops.RemoveAt(m_recentDrops.Count - 1);
            }

            Log($"掉落：物品 {drop.ItemId} × {drop.Count}（归玩家 {drop.WinnerPlayerId}）");
        }

        /// <summary>处理世界快照（交给 `SnapshotView`；这里只管日志与事件）。</summary>
        /// <param name="snapshot">快照。</param>
        private void HandleSnapshot(WorldSnapshot snapshot)
        {
            bool applied = m_world.Apply(snapshot);

            if (!applied)
            {
                // 旧快照（见 `SnapshotView` 文件头第二节）：不刷日志，但值得知道它发生过
                return;
            }

            if (!m_loggedFirstSnapshot)
            {
                m_loggedFirstSnapshot = true;
                Log("收到第一张世界快照：" + m_world.Describe());
            }
        }

        /// <summary>发握手。</summary>
        private void SendHandshake()
        {
            var hello = new ClientMessage
            {
                Handshake = new Handshake
                {
                    ProtocolVersion = NetContract.Version,
                    ClientVersion = m_clientVersion,
                    PlayerName = m_playerName,
                },
            };

            m_transport.Send(hello.ToByteArray());
            Log("已发握手（协议 v" + NetContract.Version + "）");
        }

        /// <summary>发心跳（`client_time_ms` 用本地时钟，回来时一减就是 RTT）。</summary>
        private void SendPing()
        {
            var ping = new ClientMessage
            {
                Ping = new Ping { ClientTimeMs = m_clockMs },
            };

            m_transport.Send(ping.ToByteArray());
            m_lastPingSentMs = m_clockMs;
            PingsSent++;
        }

        // ====================================================================
        //  内部：状态
        // ====================================================================

        /// <summary>失败：记原因、关连接、进失败态（**只报一次**）。</summary>
        /// <param name="reason">人话原因。</param>
        private void Fail(string reason)
        {
            bool first = m_failureReason == null;
            m_failureReason = reason;

            m_closingIntentionally = true;      // 我们自己关的：别再让 Closed 回调再报一次
            m_transport.Close();

            SetState(ESessionState.Failed);
            Log("失败：" + reason);

            if (!first)
            {
                // 已经失败过了：不再重复上报（状态没变，SetState 也不会重复发事件）
                return;
            }
        }

        /// <summary>切状态（变了才发事件）。</summary>
        /// <param name="state">新状态。</param>
        private void SetState(ESessionState state)
        {
            if (m_state == state)
            {
                return;
            }

            m_state = state;

            Action<ESessionState> handler = StateChanged;

            if (handler != null)
            {
                handler(state);
            }
        }

        /// <summary>记一条人话。</summary>
        /// <param name="line">内容。</param>
        private void Log(string line)
        {
            Action<string> handler = Note;

            if (handler != null)
            {
                handler(line);
            }
        }

        /// <summary>已释放还来调：直接报错。</summary>
        private void ThrowIfDisposed()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException(nameof(NetSession),
                    "[NetSession] 已经 Dispose 过了。");
            }
        }
    }
}
