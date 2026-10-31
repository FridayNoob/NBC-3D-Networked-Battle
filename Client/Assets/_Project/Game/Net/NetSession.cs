// 这个文件里有：
//   ESessionState（枚举）：一条会话的五个状态，没连 / 连接中 / 握手中 / 在线 / 失败。
//   NetSession（类）：客户端的一条联机会话，负责握手、心跳与收包分发，不认识 socket。

using System;
using System.Collections.Generic;
using Google.Protobuf;
using NBC.Framework.Input;
using NBC.Framework.Net;
using NBC.Protocol;
using NBC.Shared;
using NBC.Shared.Auth;
using NBC.Shared.Net;
using NBC.Shared.Sim;

namespace NBC.Game.Net
{
    // 主要功能：一条联机会话的状态机 —— 连接、握手、心跳、收包分发，全部时间由调用方 Pump 喂进来。
    // 主要数据成员：m_transport（字节载荷接缝）、m_clockMs（本地毫秒时钟）、m_state（会话状态）、m_lockstep（每局一个的锁步运行器）。
    // 主要外部方法：Connect / Disconnect / Pump / SendInput / SendLockstepInput / FindQuestState / FindProgress。
    // 谁在用它：Editor 的 NetDebugWindow、Game 的 RuntimeNetDriver 与 ServerEventBridge、EditMode 用例。
    // 注意：传输层不认识 protobuf、会话层不认识 socket —— 混在一起的话，换 UDP 与改协议两件事会互相绊住。
    // 注意：心跳节奏、握手超时、心跳超时、RTT 全基于调用方喂进来的本地时钟，所以测试能精确到毫秒、不用 sleep 也不会 flaky。
    // 注意：握手必须有超时，否则连上了但服务端不说话时，界面永远停在连接中、日志什么都没有。
    // 注意：收到先入队、等 transport.Pump() 返回后再处理，避免在收发过程中改状态表。
    // 注意：心跳超时必须真的断开，只把状态标成掉线会留下半死的 socket，下一次 Send 会以为还连着。
    /// <summary>客户端的一条联机会话：负责握手、心跳与收包分发，不认识 socket。</summary>
    public sealed class NetSession : IDisposable
    {
        /// <summary>默认心跳间隔，毫秒（1000 = 每秒一次）。</summary>
        public const int DefaultHeartbeatIntervalMs = 1000;

        /// <summary>默认心跳超时，毫秒：这么久没收到 Pong 就判定掉线。</summary>
        public const int DefaultHeartbeatTimeoutMs = 5000;

        /// <summary>默认握手超时，毫秒：发出握手后等这么久没有答复就判失败。</summary>
        public const int DefaultHandshakeTimeoutMs = 5000;

        // 界面里最多留几条掉落记录，新的在前，超出丢最旧的。
        public const int MaxRecentDrops = 20;

        // 注意：服务端第 N 帧在 ServerTick >= N + 2 时到点就广播，到点之后再来的输入一律作废。
        //   客户端拿到第 N 帧时服务端已经在等着发 N+1，若只按「下一帧」打帧号，就只剩一个
        //   tick（30Hz 下约 33ms）的网络预算，稍一抖这一帧就被判缺人（Missing）。
        // 注意：取 1 是把预算变成两个 tick（约 66ms），又不至于让按键到画面之间拖出可感知的延迟
        //   （它正好是服务端那 2 tick 固定延迟里的一半）。
        // 注意：这份预算有限且会被 RTT 吃光 —— 2 × 单程 + 一次泵间隔 > 66ms 时照样被判缺人，
        //   而本地 LockstepInputsSkipped 仍是 0（本地的闸只管「帧号已经落后」这一种）；
        //   想抗更大的 RTT 就得再加提前量，代价是按键到画面的延迟同步变大。
        /// <summary>锁步上行的提前量，帧：默认给「下一帧 + 这么多帧」打帧号。</summary>
        public const int LockstepInputLeadFrames = 1;

        // 传输接缝：Unity 里是 TcpTransport，测试里塞 FakeTransport。
        private readonly ITransport m_transport;

        // 玩家名，游客用；登录成功后界面显示的是服务端给的档案昵称。
        private readonly string m_playerName;

        // 登录名；空串 = 游客。
        private readonly string m_account;

        // 密码摘要（SHA256(明文密码) 的小写十六进制）；空串 = 游客。
        // 注意：这里只存摘要不存明文 —— 明文只在构造函数那一瞬间存在，算完摘要就丢。
        //   会话比登录活得久，字段会被日志、调试器、崩溃转储翻出来，没有任何理由让它拿着明文密码。
        private readonly string m_passwordDigest;

        // 客户端版本标识，只用于服务端排查。
        private readonly string m_clientVersion;

        // 心跳间隔，毫秒（必须大于 0，构造时校验）。
        private readonly int m_heartbeatIntervalMs;
        // 心跳超时，毫秒：这么久没收到 Pong 就判掉线。
        private readonly int m_heartbeatTimeoutMs;
        // 握手超时，毫秒：发出握手后等这么久没有答复就判失败。
        private readonly int m_handshakeTimeoutMs;

        // 收到的原始载荷队列，等 transport.Pump() 返回之后再处理（原因见类头的注意）。
        private readonly Queue<byte[]> m_inbox = new Queue<byte[]>();

        // 本地时钟，毫秒；由调用方每次 Pump 累加喂进来。
        private long m_clockMs;

        // 进入等握手那一刻的本地时钟，毫秒；-1 = 没在握手。
        private long m_handshakeStartedMs = -1;

        // 上次发心跳的时刻，毫秒；-1 = 还没发过。
        private long m_lastPingSentMs = -1;

        // 上次收到 Pong 的时刻，毫秒；-1 = 还没收到过。
        private long m_lastPongReceivedMs = -1;

        // 当前会话状态。
        private ESessionState m_state = ESessionState.Disconnected;

        // 失败原因（人话）；null = 没失败过。
        private string m_failureReason;

        // 握手结果；null = 还没握手成功。
        private HandshakeAck m_ack;

        // 服务端最后一次下发的房间状态，含「你不在任何房间」的那份。
        private RoomState m_room;

        // 最近一次被拒的错误；null = 还没被拒过。
        private ErrorResponse m_lastError;

        // 客户端世界状态，服务端快照的本地副本。
        private readonly SnapshotView m_world = new SnapshotView();

        // 最近几次掉落，新的在前，最多 MaxRecentDrops 条。
        private readonly List<DropEvent> m_recentDrops = new List<DropEvent>();

        // 最近一条掉落事件；null = 还没掉过东西。
        private DropEvent m_lastDrop;

        // 最近一条伤害事件；null = 还没收到过。
        private DamageEvent m_lastDamage;

        // 最近一条死亡事件；null = 还没收到过。
        private DeathEvent m_lastDeath;

        // 最近一次收到的服务端权威进度；null = 还没收到过。
        private ProgressSync m_lastProgress;

        // 累计收到多少条权威进度同步，条。
        private long m_progressSyncs;

        // 最近一次收到的任务状态；null = 还没收到过（见 FindQuestState 的返回语义）。
        private QuestStateSync m_lastQuestStates;

        // 累计收到多少条任务状态同步，条。
        private long m_questStatesReceived;

        // 锁步运行器；懒建 —— 只有真的收到锁步消息才建，不玩锁步的会话不多一份对象。
        // 注意：一局一个对象，每收到 lockstep_start 就重建（不 reset 旧的），于是 DesyncCount、
        //   MatchMismatchCount、Outcome、RejectedFrames、StepsExecuted 这些数天然是本局的，
        //   旧局的车轮不会跟着走。
        private LockstepClient? m_lockstep;

        // 本会话打到第几局，每收到一条 lockstep_start +1；还没打过 = 0，跨局累计。
        // 注意：这是「换了一局」的显式信号，表现层靠它换适配器，不再靠帧号有没有倒退去猜 ——
        //   那个猜法会漏掉「上一局末帧 0、新一局从 0 起」这一格。
        private int m_matchSerial;

        // 第一张快照只记一句日志就够；每帧记会把日志刷爆。
        private bool m_loggedFirstSnapshot;

        // 已发出的输入条数，见 NextInputSeq；从 1 起。
        private int m_inputSeq;

        // 已发出的锁步上行序号，协议字段 seq；从 1 起。
        private int m_lockstepInputSeq;

        // 最近一次测到的往返延迟，毫秒；-1 = 还没测到。
        private int m_rttMs = -1;

        // 是不是我们主动关的，用来区分 Closed 事件的两条来路。
        private bool m_closingIntentionally;

        // 是否已经 Dispose。
        private bool m_disposed;

        /// <summary>建一条会话，此时还没连；账号一经构造就固定，换账号要新建一个 NetSession。</summary>
        public NetSession(
            ITransport transport,
            string playerName = "玩家",
            string clientVersion = "nbc-unity",
            int heartbeatIntervalMs = DefaultHeartbeatIntervalMs,
            int heartbeatTimeoutMs = DefaultHeartbeatTimeoutMs,
            int handshakeTimeoutMs = DefaultHandshakeTimeoutMs,
            string account = "",
            string password = "")
        {
            // 功能：把传输、身份、三个超时与账号摘要装进会话，并订阅传输层的收帧与关闭事件。
            // 入参：transport 传输实现（Unity 里 TcpTransport，测试里 FakeTransport）；
            //       playerName 游客名（空则用「玩家」）；clientVersion 客户端版本标识（空则用 nbc-unity）；
            //       heartbeatIntervalMs 心跳间隔、heartbeatTimeoutMs 心跳超时、handshakeTimeoutMs 握手超时
            //       （三者都必须大于 0，单位毫秒）；account 登录名（空 = 游客）；
            //       password 明文密码，只在这里活一瞬间，算完摘要就丢。
            // 返回：无（构造）。
            // 步骤：
            //   1) 先校验传输非空与三个超时为正数，坏参数当场抛，别等到发心跳才炸
            //   2) 空名字回落到默认值
            //   3) 账号非空才算密码摘要：只给账号不给密码一定会被服务端拒，这里先说清楚，不用等一个往返
            //   4) 最后才订阅 FrameReceived / Closed，上面任何一步抛异常都不会留下半挂的订阅
            // 上游：NetDebugWindow 的连接按钮、RuntimeNetDriver、EditMode 用例
            // 下游：PasswordDigest.FromPassword、ITransport.FrameReceived / ITransport.Closed
            // 注意：账号是会话级的 —— 换账号要新造一个 NetSession，Connect 只负责连，
            //   否则会出现连到一半改了身份这种鬼状态。
            // 注意：明文密码永远不会被写进日志，会话只把摘要发出去。
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

            // 账号给了就要给密码：只给账号不给密码一定会被服务端拒（那边会说密码摘要形状不对），
            // 这里先说清楚，别等一个往返
            m_account = (account ?? string.Empty).Trim();
            m_passwordDigest = m_account.Length == 0
                ? string.Empty
                : PasswordDigest.FromPassword(password ?? string.Empty);

            m_transport.FrameReceived += OnFrameReceived;
            m_transport.Closed += OnTransportClosed;
        }

        // 查询

        /// <summary>当前会话状态，供编辑器窗口与引导流程判断会话走到哪一段。</summary>
        public ESessionState State
        {
            get { return m_state; }
        }

        // 登录名；空串 = 游客。
        public string Account
        {
            get { return m_account; }
        }

        // 是不是游客；等价于登录名为空。
        public bool IsGuest
        {
            get { return m_account.Length == 0; }
        }

        /// <summary>失败原因，null = 没失败过；失败时连接已经被关掉。</summary>
        public string FailureReason
        {
            get { return m_failureReason; }
        }

        /// <summary>握手结果，成功后有，失败时为 null。</summary>
        public HandshakeAck Ack
        {
            get { return m_ack; }
        }

        /// <summary>服务端分配的玩家 id，0 = 还没身份。</summary>
        public long PlayerId
        {
            get { return m_ack == null ? 0 : m_ack.PlayerId; }
        }

        /// <summary>最近一次往返延迟，毫秒；-1 = 还没测到。</summary>
        public int RttMs
        {
            get { return m_rttMs; }
        }

        // 本地时钟，毫秒，由 Pump 累加；测试用它对齐时间。
        public long ClockMs
        {
            get { return m_clockMs; }
        }

        /// <summary>已发出的心跳条数。</summary>
        public long PingsSent { get; private set; }

        /// <summary>已发出的状态同步输入条数。</summary>
        public long InputsSent { get; private set; }

        /// <summary>已收到的 Pong 条数。</summary>
        public long PongsReceived { get; private set; }

        /// <summary>传输层收发统计，直接取 ITransport.Stats。</summary>
        public TransportStats TransportStats
        {
            get { return m_transport.Stats; }
        }

        /// <summary>是不是在线：握手完成且没断。</summary>
        public bool IsOnline
        {
            get { return m_state == ESessionState.Online; }
        }

        // 注意：判断在不在房间里要用 InRoom —— 服务端离开房间时会下发一份房号为空的状态，
        //   所以「有状态」不等于「在房里」。
        /// <summary>服务端最后一次下发的房间状态，可能是「房号为空」的那一份。</summary>
        public RoomState CurrentRoom
        {
            get { return m_room; }
        }

        /// <summary>现在在不在房间里，房号非空才算。</summary>
        public bool InRoom
        {
            get { return m_room != null && !string.IsNullOrEmpty(m_room.RoomId); }
        }

        /// <summary>最近一次被拒的错误，null = 还没被拒过。</summary>
        public ErrorResponse LastError
        {
            get { return m_lastError; }
        }

        // 注意：掉落是概率的，狼有 35% 什么都不掉（见 DropTable 表）。
        /// <summary>最近一次掉落，null = 还没掉过东西。</summary>
        public DropEvent LastDrop
        {
            get { return m_lastDrop; }
        }

        /// <summary>累计收到的掉落事件条数。</summary>
        public long DropsReceived { get; private set; }

        // 最近一次伤害事件；null = 还没挨过打，也没打过人。
        public DamageEvent LastDamage
        {
            get { return m_lastDamage; }
        }

        /// <summary>累计收到的伤害事件条数。</summary>
        public long HitsReceived { get; private set; }

        // 最近一次死亡事件；null = 还没人死过。
        public DeathEvent LastDeath
        {
            get { return m_lastDeath; }
        }

        /// <summary>累计收到的死亡事件条数。</summary>
        public long DeathsReceived { get; private set; }

        // 最近一次收到的服务端权威进度；null = 还没收到过。
        // 注意：它是全量 —— 这个玩家关心的全部条件加当前值；界面以它为准，
        //   不要再拿本地算出来的数去显示。
        public ProgressSync LastProgress
        {
            get { return m_lastProgress; }
        }

        // 累计收到的权威进度同步条数。
        public long ProgressSyncsReceived
        {
            get { return m_progressSyncs; }
        }

        // 最近一次收到的任务状态；null = 还没收到过。
        public QuestStateSync LastQuestStates
        {
            get { return m_lastQuestStates; }
        }

        // 累计收到的任务状态同步条数。
        public long QuestStatesReceived
        {
            get { return m_questStatesReceived; }
        }

        // 注意：这是帧同步的报警器 —— 大于 0 说明「我算出来的世界与服务端不一样」，
        //   而不是「网络卡了一下」。
        // 注意：它是本局的数，新一局会换新运行器所以从 0 开始。
        /// <summary>锁步分歧次数（本局），委托给运行器；0 = 还没收到过锁步消息或一直一致。</summary>
        public int DesyncCount
        {
            get { return m_lockstep == null ? 0 : m_lockstep.DesyncCount; }
        }

        /// <summary>锁步运行器，null = 还没收到过任何锁步消息。</summary>
        public LockstepClient Lockstep
        {
            get { return m_lockstep; }
        }

        // 注意：跨局累计（与 DesyncCount、MatchMismatchCount 相反，别混），用途只有一个 ——
        //   让表现层看得出「换了一局」。
        /// <summary>本会话打到第几局，每收到一条 lockstep_start +1；还没打过 = 0。</summary>
        public int MatchSerial
        {
            get { return m_matchSerial; }
        }

        // 锁步上行

        // 注意：「落定」的口径是「服务端宣布了且我复算一致」，不是单纯服务端宣布了 ——
        //   复算不一致的那一局里 Outcome 为 null，这道闸不会落下来。
        // 注意：调用方必须按「帧号有没有前进」节流，一次推进只发一条；每个 tick 都硬调就会对同一帧
        //   连发好几条，服务端只认第一条，其余按重复丢掉并回 ErrorResponse。
        // 注意：别指望「帧号已经广播过」那道闸兜底 —— 默认帧号恒为已推进帧 + 1 + 提前量，
        //   永远大于已推进帧，只要调用方不停手，那道闸永远不会响。
        // 注意：宣布结果之后服务端停止推进，再往未来帧里塞输入只会堆在待发桶里，一帧都不会被用上。
        /// <summary>现在能不能发锁步上行：在线 + 已开局 + 这一局还没落定结果。</summary>
        public bool CanSendLockstepInput
        {
            get
            {
                return m_state == ESessionState.Online
                    && m_lockstep != null
                    && m_lockstep.Started
                    && m_lockstep.Outcome == null;
            }
        }

        // 注意：有运行器但还没开局成功（initial_hash 对不上）时它返回 1（LastSteppedFrame 还是 -1），
        //   那种帧发不出去，会被 CanSendLockstepInput 挡掉。
        /// <summary>默认给哪一帧打帧号（= 已推进帧 + 1 + 提前量）；连运行器都没有时返回 0。</summary>
        public int NextLockstepFrame
        {
            get
            {
                return m_lockstep == null
                    ? 0
                    : m_lockstep.LastSteppedFrame + 1 + LockstepInputLeadFrames;
            }
        }

        /// <summary>累计发出去的锁步上行条数，被挡下的不算。</summary>
        public int LockstepInputsSent { get; private set; }

        // 注意：这个数必须看得见 —— 它非 0 就等于「我按了键，但这一帧的服务端输入是空的」，
        //   而那在画面上只表现为「我怎么不动」，不说清就永远查不出来。
        // 注意：反过来不成立 —— 它为 0 不代表每一帧都送到了；RTT 吃光 2 tick 预算时服务端
        //   照样判缺人，而本地一道闸都不会响。
        /// <summary>累计被挡下的锁步上行次数：没在线 / 没开局 / 结果已落定 / 帧号已广播过。</summary>
        public int LockstepInputsSkipped { get; private set; }


        // 功能：按任务编号取服务端的权威状态，把「服务端没说」与「明确说没接过」分开。
        // 入参：questId 任务编号。
        // 返回：null = 还没收到过任务状态同步（服务端没说）；0 = 收到过全量同步但里面没有它
        //   （明确未接取）；1/2/3 = 服务端说这个任务在哪个阶段。
        // 步骤：
        //   1) 没有快照就返回 null，绝不许当成未接取 —— 界面要退回本地那份并标成预测
        //   2) 在全量条目里按 questId 线性找，找到就返回
        //   3) 找不到返回 0：服务端那条是全量的（遍历所有任务只跳过 None），
        //      所以收到过同步之后「不在里面」就是「没接过」
        // 上游：NetSessionQuestStateAuthority、任务面板
        // 下游：无
        // 注意：玩家一个任务都没接过时服务端根本不发这条（视图为空就没东西可发），
        //   客户端会一直停在 null、显示本地预测 —— 这不是 bug。
        public int? FindQuestState(int questId)
        {
            QuestStateSync snapshot = m_lastQuestStates;

            if (snapshot == null)
            {
                return null;
            }

            for (int i = 0; i < snapshot.Entries.Count; i++)
            {
                if (snapshot.Entries[i].QuestId == questId)
                {
                    return snapshot.Entries[i].State;
                }
            }

            // 收到过全量同步，里面没有它，就是服务端明确表示没接过
            return 0;
        }

        // 功能：按条件编号取服务端的权威进度条目。
        // 入参：conditionKey 条件编号（QuestCondition 表主键）。
        // 返回：条目；还没收到过、或这一条里没有该条件时返回 null
        //   （null 表示服务端没说，不是进度 0）。
        // 步骤：
        //   1) 没有快照直接返回 null
        //   2) 线性找 conditionKey，找到就返回
        //   3) 找不到也返回 null —— 界面不能把「还没同步」画成「一格都没做」
        // 上游：NetSessionProgressAuthority
        // 下游：无
        public ConditionProgressEntry FindProgress(int conditionKey)
        {
            ProgressSync snapshot = m_lastProgress;

            if (snapshot == null)
            {
                return null;
            }

            for (int i = 0; i < snapshot.Entries.Count; i++)
            {
                if (snapshot.Entries[i].ConditionKey == conditionKey)
                {
                    return snapshot.Entries[i];
                }
            }

            return null;
        }

        /// <summary>最近几次掉落，新的在前，最多 MaxRecentDrops 条，界面直接显示它。</summary>
        public IReadOnlyList<DropEvent> RecentDrops
        {
            get { return m_recentDrops; }
        }

        /// <summary>客户端世界状态，只由服务端快照决定；表现层读它而不是自己算。</summary>
        public SnapshotView World
        {
            get { return m_world; }
        }

        /// <summary>一句人话，编辑器窗口与日志直接用。</summary>
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

        // 事件

        /// <summary>会话状态变了；切到同一状态不发。</summary>
        public event Action<ESessionState> StateChanged;

        /// <summary>握手成功，带服务端的答复。</summary>
        public event Action<HandshakeAck> HandshakeCompleted;

        /// <summary>测到新的往返延迟，毫秒。</summary>
        public event Action<int> RttUpdated;

        // 每收到一条服务端消息都发一次（Pong 也算），方便界面显示有流量。
        public event Action<ServerMessage> MessageReceived;

        // 房间状态变了；服务端每次都整份下发，覆盖本地即可。
        public event Action<RoomState> RoomChanged;

        /// <summary>请求被服务端拒了，带错误码与人话原因。</summary>
        public event Action<ErrorResponse> ErrorReceived;

        // 收到一条掉落事件；服务端掷的，客户端只负责显示。
        public event Action<DropEvent> DropReceived;

        // 收到一条伤害事件，每次扣血一条，表现层的飘字靠它。
        public event Action<DamageEvent> DamageReceived;

        // 注意：在此之前任务与成就进度客户端自己也算一份（预测），两边数值一致却仍是两个账房 ——
        //   配置、时序或「哪个事件喂了几次」有半点不同，就会出现客户端显示已完成、服务端说没达成，
        //   而且不报错；收到这条之后客户端以它为准，本地那份退化成预测与兜底。
        // 注意：全量语义 —— 它带的是这个玩家关心的全部条件的当前值，客户端可以直接覆盖，
        //   不需要自己合并增量。
        /// <summary>收到一条服务端权威进度（全量），客户端以它为准。</summary>
        public event Action<ProgressSync> ProgressReceived;

        // 注意：包括「服务端说这个任务没接过」（全量里没有它，FindQuestState 返回 0）这种往回退的
        //   情况，界面必须跟着退回去，不能因为本地以为接过就留住绿色。
        /// <summary>收到一条任务状态同步（全量），收到就以服务端为准落定。</summary>
        public event Action<QuestStateSync> QuestStateReceived;

        // 锁步分歧：我算出来的世界哈希与服务端广播里的不一致。
        // 注意：必须带上帧号与两个哈希（LockstepDesync），只说「不一致」排查不动；而且必须做成
        //   事件而不是只进日志，帧同步跑偏了要能立刻被人看见。
        public event Action<LockstepDesync> DesyncDetected;



        // 收到一条死亡事件，怪与英雄都走这里，按 kind 分派。
        public event Action<DeathEvent> DeathReceived;

        /// <summary>值得记一句的事情（人话），日志面板靠它。</summary>
        public event Action<string> Note;

        // 连接 / 断开

        /// <summary>发起连接（非阻塞），连上与否靠后续 Pump 推进；重连即新会话。</summary>
        public void Connect(string host, int port)
        {
            // 功能：清掉上一次的残留、进入连接中状态，再交给传输层去连。
            // 入参：host 主机；port 端口（1..65535）。
            // 返回：无；已经在连或已连上时抛 InvalidOperationException。
            // 步骤：
            //   1) 已经在连或连上就直接抛，先 Disconnect 再连
            //   2) 清残留：收件箱、握手结果、房间、错误、世界、掉落、最近事件
            //   3) 连运行器与局号一起清掉，重连等于新会话
            //   4) 最后才置 Connecting 并让传输层开连，状态机不会停在半路
            // 上游：NetDebugWindow 的连接按钮、RuntimeNetDriver
            // 下游：SetState、ITransport.Connect
            // 注意：不清运行器会拿着上一局的世界继续喂表现层，那正是「世界不换」那类静默故障。
            ThrowIfDisposed();

            if (m_state == ESessionState.Connecting || m_state == ESessionState.Handshaking
                || m_state == ESessionState.Online)
            {
                throw new InvalidOperationException(
                    "[NetSession] 已经在连/连上了，先 Disconnect 再连（当前：" + Description + "）。");
            }

            // 重连前把上一次的残留清干净，否则上一局的失败原因会跟着这一局
            m_inbox.Clear();
            m_ack = null;
            m_room = null;
            m_lastError = null;
            m_world.Clear();
            m_loggedFirstSnapshot = false;
            m_recentDrops.Clear();
            m_lastDrop = null;
            m_lastDamage = null;
            m_lastDeath = null;

            // 重连等于新会话：运行器与局号一起清掉，否则会拿着上一局的世界继续喂表现层
            m_lockstep = null;
            m_matchSerial = 0;
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

        // 功能：主动断开，幂等。
        // 入参：reason 原因，写进日志。
        // 返回：无。
        // 步骤：
        //   1) 已经断开就返回
        //   2) 先标记这是主动关的，免得 Closed 回调再报一次失败
        //   3) 关传输层、清收件箱，再按失败原因落在 Disconnected 还是 Failed
        // 上游：RuntimeNetDriver、EditMode 用例
        // 下游：ITransport.Close、SetState
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

        /// <summary>释放会话，等价于断开；之后再 Connect 会报错。</summary>
        public void Dispose()
        {
            // 功能：退订传输层事件、断开、释放传输。
            // 入参：无。
            // 返回：无。
            // 步骤：
            //   1) 已经释放过就返回
            //   2) 先退订再断开，避免断开过程中的回调打到已释放的对象
            //   3) 释放标志置在最后
            // 上游：NetDebugWindow、宿主
            // 下游：Disconnect、ITransport.Dispose
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

        // 发送

        // 功能：把一条客户端消息序列化发出去，只有在线才能发。
        // 入参：message 消息，非 null。
        // 返回：发出去返回 true；不在线直接抛，不会静默丢弃。
        // 步骤：
        //   1) 已释放就抛
        //   2) 空消息抛
        //   3) 不在线抛 —— 静默丢弃只会变成「偶尔少一条」，所以当场报错
        // 上游：本类的 JoinRoom / LeaveRoom / SendInput / SendQuestAction
        // 下游：ITransport.Send
        // 注意：握手与心跳由本类自己按节奏发，调用方不用管。
        public bool Send(ClientMessage message)
        {
            ThrowIfDisposed();

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (m_state != ESessionState.Online)
            {
                // 没在线就发是调用方的 bug，和 TcpTransport 未连接就发同一条判据
                throw new InvalidOperationException(
                    "[NetSession] 还没上线就 Send（当前：" + Description + "）。" +
                    "静默丢弃只会变成\"偶尔少一条\"，所以这里当场报错。");
            }

            m_transport.Send(message.ToByteArray());
            return true;
        }

        // 功能：立刻发一次心跳，界面上的「发心跳」按钮用它。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 不在线就直接返回
        //   2) 在线就交给 SendPing
        // 上游：NetDebugWindow
        // 下游：SendPing
        public void SendPingNow()
        {
            if (m_state != ESessionState.Online)
            {
                return;
            }

            SendPing();
        }

        // 功能：请求进房，房号留空就让服务端按副本号分配或找房。
        // 入参：roomId 房号（留空 = 让服务端安排）；dungeonId 想打的副本（留空房号时服务端按它找房或开房）。
        // 返回：无；结果走 RoomChanged 或 LastError。
        // 步骤：
        //   1) 空房号统一成空串再发，避免 null 进协议
        // 上游：NetDebugWindow 的进房按钮
        // 下游：Send
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

        // 功能：请求离开当前房间，结果同样走 RoomChanged / LastError。
        public void LeaveRoom()
        {
            Send(new ClientMessage { LeaveRoom = new LeaveRoomRequest() });
        }

        // 功能：把一条输入命令发出去，只发意图，走多远、打不打得到由服务端决定。
        // 入参：command 输入命令（轴沿用协议量化，不需要换算）；
        //   targetEntityId 当前目标（0 = 没有，服务端会校验）。
        // 返回：无。
        // 步骤：
        //   1) 拆出轴、动作位与目标，转给重载
        // 上游：战斗流程与输入层
        // 下游：SendInput(int, int, uint, uint, int)
        public void SendInput(InputCommand command, int targetEntityId = 0)
        {
            SendInput(command.MoveX, command.MoveY, command.ActionBits, command.ActionReleaseBits, targetEntityId);
        }

        /// <summary>发一帧输入：轴是协议量化轴，-1000..1000 对应 -1..+1，第 0 位是技能1与普攻。</summary>
        public void SendInput(int moveX, int moveY, uint actionBits = 0, uint actionReleaseBits = 0,
                              int targetEntityId = 0)
        {
            // 功能：把一帧的移动轴、按键与目标打成 PlayerInput 发出去，并累加计数。
            // 入参：moveX 左右轴（-1000..1000）；moveY 前后轴（-1000..1000）；
            //   actionBits 本帧新按下的动作位；actionReleaseBits 本帧新松开的动作位；
            //   targetEntityId 当前目标（0 = 没有）。
            // 返回：无。
            // 步骤：
            //   1) client_tick 用 NextInputSeq 的条数序号，本版没有客户端逻辑帧，它只用于服务端日志对账
            //   2) 目标为 0 也照发，服务端按 0 处理
            // 上游：NetDebugWindow、RuntimeNetDriver
            // 下游：Send、NextInputSeq
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

        // 功能：取下一条输入序号，从 1 开始。
        // 入参：无。
        // 返回：本次的序号，也就是第几条输入。
        // 步骤：
        //   1) 先自增再返回，所以第一条是 1
        // 上游：SendInput
        // 下游：无
        // 注意：协议字段叫 client_tick，但本版没有客户端逻辑帧（不做预测），所以它是「第几条输入」，
        //   不是权威帧号；等做预测与回滚时，它才会变成真正的客户端帧号。
        private int NextInputSeq()
        {
            m_inputSeq++;
            return m_inputSeq;
        }

        /// <summary>发一条锁步上行（我这一帧想怎么动、打谁）；被挡下时返回 false 并计数，不抛异常。</summary>
        /// <returns>真的发出去了返回 true；被挡下返回 false，原因见步骤 2 与 4。</returns>
        public bool SendLockstepInput(int moveX, int moveY, uint buttons = 0,
                                      int targetEntityId = 0, int frame = -1)
        {
            // 功能：把这一帧的移动与目标打成 LockstepInput 发出去，帧号默认按提前量算。
            // 入参：moveX 左右轴、moveY 前后轴（-1000..1000，越界夹住）；buttons 按键位掩码
            //   （第 0 位 = 普攻，与状态同步链同一套位定义）；targetEntityId 要打的目标（0 = 不打）；
            //   frame 指定帧号（小于 0 = 用 NextLockstepFrame，指定了就不再叠提前量）。
            // 返回：真的发出去了返回 true；被挡下返回 false（没在线 / 没开局 / 已宣布胜负 /
            //   指定帧号已广播过），同时 LockstepInputsSkipped 加 1。
            // 步骤：
            //   1) 先取局部变量再判空，可空引用类型开着或关着都不会告警
            //   2) 用 CanSendLockstepInput 与运行器判一次，挡下就计数返回 —— 不抛异常，
            //      因为调用方每帧都会问，抛了会把「还没开局」变成崩溃
            //   3) 算帧号：默认已推进帧 + 1 + 提前量；客户端不需要知道服务端时钟，
            //      只要知道自己收到第几帧，服务端保证第 N 帧广播过之后 N 就不可改
            //   4) 帧号不大于已推进帧就挡掉：服务端只会回一条作废，本地先拦是不让注定作废的包上路，
            //      不是替服务端判
            //   5) 轴先夹到正负 1000，再用 Fix64.FromInt 换定点，一个 float 都不碰
            // 上游：NetDebugWindow 的锁步上行、战斗输入
            // 下游：Send、CanSendLockstepInput、NextLockstepFrame、Fix64.FromInt
            // 注意：这条链与 SendInput 不是一回事：SendInput 发的是意图，服务端每 tick 应用一次再回快照；
            //   这条发的是「第几帧」的输入，服务端只转手广播，谁都不许改，改了就是两端各算各的、对账当场红。
            // 注意：轴只有方向有意义（服务端按 WorldStep 归一化后乘速度），所以 1000 还是 1.0 不影响结果。
            ThrowIfDisposed();

            // 取成局部变量再判空：可空引用类型开着或关着都不会告警，也不指望属性里那次判空被编译器看穿
            LockstepClient client = m_lockstep;

            if (!CanSendLockstepInput || client == null)
            {
                LockstepInputsSkipped++;
                return false;
            }

            int stamped = frame < 0 ? client.LastSteppedFrame + 1 + LockstepInputLeadFrames : frame;

            // 已经广播过的帧服务端只会回一条作废，所以本地先挡：不是替服务端判，是不让注定作废的包上路
            if (stamped <= client.LastSteppedFrame)
            {
                LockstepInputsSkipped++;
                return false;
            }

            m_lockstepInputSeq++;

            Send(new ClientMessage
            {
                LockstepInput = new LockstepInput
                {
                    Frame = stamped,
                    Seq = m_lockstepInputSeq,
                    MoveXRaw = Fix64.FromInt(ClampAxis(moveX)).RawValue,
                    MoveYRaw = Fix64.FromInt(ClampAxis(moveY)).RawValue,
                    Buttons = (int)buttons,
                    TargetEntityId = targetEntityId,
                },
            });

            LockstepInputsSent++;
            return true;
        }

        // 功能：把轴夹进量化范围 -1000..1000。
        // 入参：axis 原始轴值。
        // 返回：夹住之后的轴值，超上界给 1000、超下界给 -1000，否则原样。
        // 步骤：
        //   1) 先判上界再判下界，都不超就原样返回
        // 上游：SendLockstepInput
        // 下游：无
        private static int ClampAxis(int axis)
        {
            if (axis > 1000)
            {
                return 1000;
            }

            if (axis < -1000)
            {
                return -1000;
            }

            return axis;
        }

        // 每帧推进

        /// <summary>推进一帧；时间由调用方喂进来，心跳与各种超时全按这个毫秒数算。</summary>
        public void Pump(int deltaMs)
        {
            // 功能：累加本地时钟，收包并处理，再按状态推握手、判超时、发心跳。
            // 入参：deltaMs 距上次调用的毫秒数（负数按 0 处理）。
            // 返回：无。
            // 步骤：
            //   1) 已释放就返回；负的 delta 归零，防止时钟倒退
            //   2) 先让传输层收字节拆帧（回调里只入队），再处理收件箱 —— 原因见类头的注意
            //   3) 已经失败或断开就到此为止，后面的状态推进没意义
            //   4) 刚连上就记下握手开始时刻、进入握手中并发握手
            //   5) 握手超时判失败：服务端不说话时不能永远停在连接中
            //   6) 在线才有心跳：按间隔发 Ping，超过超时没收到 Pong 就判掉线，并且真的断开
            // 上游：NetDebugWindow 每帧、RuntimeNetDriver
            // 下游：ITransport.Pump、DrainInbox、SendHandshake、SendPing、Fail
            if (m_disposed)
            {
                return;
            }

            if (deltaMs < 0)
            {
                deltaMs = 0;
            }

            m_clockMs += deltaMs;

            // 1) 传输层收字节、拆帧，回调里只入队
            m_transport.Pump();

            // 2) 处理这一帧收到的消息
            DrainInbox();

            if (m_state == ESessionState.Failed || m_state == ESessionState.Disconnected)
            {
                return;
            }

            // 3) 刚连上就发握手
            if (m_state == ESessionState.Connecting && m_transport.IsConnected)
            {
                m_handshakeStartedMs = m_clockMs;
                SetState(ESessionState.Handshaking);
                SendHandshake();
            }

            // 4) 握手超时
            if (m_state == ESessionState.Handshaking
                && m_clockMs - m_handshakeStartedMs > m_handshakeTimeoutMs)
            {
                Fail("握手超时（" + m_handshakeTimeoutMs + "ms 没等到 HandshakeAck，" +
                     "服务端可能版本不兼容或不是本协议）");
                return;
            }

            // 5) 在线才按节奏发心跳，并判心跳超时
            if (m_state == ESessionState.Online)
            {
                if (m_lastPingSentMs < 0 || m_clockMs - m_lastPingSentMs >= m_heartbeatIntervalMs)
                {
                    SendPing();
                }

                if (m_clockMs - m_lastPongReceivedMs > m_heartbeatTimeoutMs)
                {
                    // 要真的断开，不能只标状态
                    Fail("心跳超时（" + m_heartbeatTimeoutMs + "ms 没收到 Pong）");
                }
            }
        }

        // 内部：收发

        // 功能：传输层收到一条载荷，只入队，不在这里改会话状态。
        // 入参：payload 字节载荷。
        // 返回：无。
        // 步骤：
        //   1) 原样入队，等 transport.Pump() 返回后由 DrainInbox 处理
        // 上游：ITransport.FrameReceived
        // 下游：DrainInbox
        // 注意：在收发过程中改状态等于在收发过程中改状态表；服务端那份也是先入队后处理，
        //   两边保持同一种心智模型。
        private void OnFrameReceived(byte[] payload)
        {
            m_inbox.Enqueue(payload);
        }

        // 功能：传输层关闭时区分「我们主动关的」与「对面或网络断的」。
        // 入参：reason 传输层给的人话原因。
        // 返回：无。
        // 步骤：
        //   1) 主动关的直接返回，状态由 Disconnect 负责
        //   2) 否则判失败，把原因带上
        // 上游：ITransport.Closed
        // 下游：Fail
        private void OnTransportClosed(string reason)
        {
            if (m_closingIntentionally)
            {
                return;     // 主动关的，状态由 Disconnect 负责
            }

            Fail("连接断开：" + reason);
        }

        // 功能：把这一帧收到的载荷逐条解析成 ServerMessage 并处理。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 一条条出队，解析失败就判失败并停手 —— 后面的字节已经不可信
        //   2) 交给 Handle；一旦状态变成失败也立刻停，别继续喂后面的消息
        // 上游：Pump（transport.Pump() 返回之后）
        // 下游：ServerMessage.Parser.ParseFrom、Handle、Fail
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

        // 功能：按 oneof 分派一条服务端消息，最后统一发 MessageReceived。
        // 入参：message 服务端消息。
        // 返回：无。
        // 步骤：
        //   1) 按 PayloadCase 转到各自的处理器
        //   2) 空 payload 直接判失败 —— 0 长度帧解出来就是它，协议违规，别装作没看见
        //   3) 未知类型：还没握手就判失败，已在线只记一句
        //   4) 分派完统一发事件，界面才能看到有流量
        // 上游：DrainInbox
        // 下游：各个 HandleXxx、MessageReceived
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

                case ServerMessage.PayloadOneofCase.ProgressSync:
                    HandleProgressSync(message.ProgressSync);
                    break;

                case ServerMessage.PayloadOneofCase.QuestState:
                    HandleQuestState(message.QuestState);
                    break;

                case ServerMessage.PayloadOneofCase.LockstepStart:
                    HandleLockstepStart(message.LockstepStart);
                    break;

                case ServerMessage.PayloadOneofCase.LockstepFrame:
                    HandleLockstepFrame(message.LockstepFrame);
                    break;

                case ServerMessage.PayloadOneofCase.MatchResult:
                    HandleMatchResult(message.MatchResult);
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

        // 功能：处理握手答复：成功就上线、发事件并立刻测一次 RTT。
        // 入参：ack 服务端答复。
        // 返回：无。
        // 步骤：
        //   1) 已经在线就忽略重复的 HandshakeAck
        //   2) 答复为空或未接受就判失败，原因带上
        //   3) 记下握手结果，并把上次 Pong 时刻重置为现在，否则心跳超时会从建会话那一刻开始算
        //   4) 先切在线再发事件，回调里看到的状态才是对的
        //   5) 立刻发一次 Ping，界面不用等一秒才看到 RTT
        // 上游：Handle
        // 下游：SetState、HandshakeCompleted、SendPing
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
                "（" + (IsGuest ? "游客" : "账号 " + m_account) +
                "，昵称 " + (string.IsNullOrEmpty(ack.Nickname) ? m_playerName : ack.Nickname) +
                "；服务端 " + ack.ServerVersion + "，协议 v" + ack.ProtocolVersion +
                "，tick " + ack.TickHz + "Hz）");

            Action<HandshakeAck> handler = HandshakeCompleted;

            if (handler != null)
            {
                handler(ack);
            }

            SendPing();     // 立刻测一次 RTT，界面不用等一秒
        }

        // 功能：处理 Pong，用本地时钟差算出往返延迟。
        // 入参：pong 心跳答复。
        // 返回：无。
        // 步骤：
        //   1) 空答复忽略
        //   2) 记数并刷新上次 Pong 时刻，心跳超时靠它
        //   3) 用 client_time_ms（带的是我们自己的本地时钟）一减就是 RTT
        //   4) 负数归零 —— 时钟被外部重置过时不报负数，否则看板被污染
        //   5) 只有变化了才发事件，别每秒刷一次界面
        // 上游：Handle
        // 下游：RttUpdated
        private void HandlePong(Pong pong)
        {
            if (pong == null)
            {
                return;
            }

            PongsReceived++;
            m_lastPongReceivedMs = m_clockMs;

            // client_time_ms 带的是我们自己的本地时钟，所以这一减就是往返延迟
            int rtt = (int)(m_clockMs - pong.ClientTimeMs);

            if (rtt < 0)
            {
                rtt = 0;        // 时钟被外部重置过之类，不报负数，否则污染看板
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

        // 功能：覆盖本地房间状态，并把席位表拼成人话写日志。
        // 入参：state 新状态。
        // 返回：无。
        // 步骤：
        //   1) 空状态忽略
        //   2) 整份覆盖：服务端每次都下发全量，不需要合并
        //   3) 房号为空只记一句「你不在任何房间」；否则拼「房号（副本）人数：成员」再记
        //   4) 最后发事件，界面拿到的是已经存好的那份
        // 上游：Handle
        // 下游：RoomChanged、Log
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
                // 把席位表拼成人话，例如 r1（副本 1001）2/4 人：剑士[房主]、法师
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

        // 功能：记下最近一次被拒并转发事件。
        // 入参：error 错误。
        // 返回：无。
        // 步骤：
        //   1) 空错误忽略
        //   2) 先存下来再记日志
        //   3) 发事件
        // 上游：Handle
        // 下游：ErrorReceived、Log
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

        // 功能：按事件类型转给伤害、死亡、掉落的处理器。
        // 入参：serverEvent 事件信封。
        // 返回：无。
        // 步骤：
        //   1) 按 EventCase 分派，未知类型直接丢
        // 上游：Handle
        // 下游：HandleDamage、HandleDeath、HandleDrop
        // 注意：客户端只显示 —— 伤害多少、谁死了、掉什么全是服务端算的，这里一个随机数都不掷，
        //   也不做「我猜它应该死了」的推断。
        // 注意：翻译成游戏事件是 ServerEventBridge 的活（它才知道掉落归谁这类规则），
        //   这一层保持纯网络。
        // 注意：两个 oneof 不叫同一个名字，写错就是 CS0117：信封是 ServerMessage.PayloadCase，
        //   事件是 ServerEvent.EventCase。
        private void HandleServerEvent(ServerEvent serverEvent)
        {
            if (serverEvent == null)
            {
                return;
            }

            switch (serverEvent.EventCase)
            {
                case ServerEvent.EventOneofCase.Damage:
                    HandleDamage(serverEvent.Damage);
                    return;

                case ServerEvent.EventOneofCase.Death:
                    HandleDeath(serverEvent.Death);
                    return;

                case ServerEvent.EventOneofCase.Drop:
                    HandleDrop(serverEvent.Drop);
                    return;

                default:
                    return;
            }
        }

        // 功能：记数、留最近一条、发事件，不打日志。
        // 入参：damage 伤害事件。
        // 返回：无。
        // 步骤：
        //   1) 空事件忽略
        //   2) 先发事件，再记数、留最近一条
        // 上游：HandleServerEvent
        // 下游：DamageReceived
        // 注意：每次扣血都打日志会把日志刷爆，所以这里刻意不记。
        private void HandleDamage(DamageEvent damage)
        {
            if (damage == null)
            {
                return;
            }

            DamageReceived?.Invoke(damage);
            HitsReceived++;
            m_lastDamage = damage;
        }

        // 功能：记数、留最近一条、发事件、记一句日志。
        // 入参：death 死亡事件。
        // 返回：无。
        // 步骤：
        //   1) 空事件忽略
        //   2) 先发事件，再记数、留最近一条
        //   3) 最后写日志，带上单位、实例、kind 与击杀者
        // 上游：HandleServerEvent
        // 下游：DeathReceived、Log
        // 注意：这里故意不解释 kind（不写「1 就是怪」）：本目录是引擎无关子集，
        //   探针 Server\_net-probe 直接编它，而 EBattleAgentKind 在战斗层，
        //   一引用探针当场编不过；分派成 MonsterDied / HeroDied 是 ServerEventBridge 的活。
        private void HandleDeath(DeathEvent death)
        {
            if (death == null)
            {
                return;
            }

            DeathReceived?.Invoke(death);
            DeathsReceived++;
            m_lastDeath = death;

            Log($"死亡：单位 {death.ConfigId}（实例 {death.EntityId}，kind {death.Kind}，" +
                $"击杀者 {death.KillerId}）");
        }

        /// <summary>发一条任务动作请求（接取或交付），结果走服务端的任务状态同步。</summary>
        public bool SendQuestAction(int questId, int action)
        {
            // 功能：把接取或交付请求组包发出去。
            // 入参：questId 任务编号；action 动作（1 = 接取，2 = 交付）。
            // 返回：Send 的结果；没在线时 Send 会抛，不会静默丢。
            // 步骤：
            //   1) 直接组包交给 Send，本地不改任何任务状态
            // 上游：NetDebugWindow 任务面板、QuestPanelModel
            // 下游：Send
            // 注意：本地不是权威，回来的是任务状态同步；界面只置一个「待确认」标记，
            //   标记只用于提示，不会被当成已接取。
            return Send(new ClientMessage
            {
                QuestAction = new QuestActionRequest { QuestId = questId, Action = action }
            });
        }

        // 功能：收到锁步开局：重建运行器，把协议实体转成普通数据再开局。
        // 入参：start 开局消息。
        // 返回：无。
        // 步骤：
        //   1) 每条 lockstep_start 就是一局的开头，先给局号加 1
        //   2) 重建运行器而不是「null 才建」：上一版用后者时，同一连接打第二局会让 ApplyStart
        //      撞上「重复开局」被拒，于是永远停滞、永远回退到快照
        //   3) 经 LockstepWire 转成普通实体，再交给运行器开局
        //   4) 记一句日志，开局失败也要看得出来
        // 上游：Handle
        // 下游：NewLockstepClient、LockstepWire.ToPlainEntities、LockstepClient.ApplyStart
        // 注意：运行器住在 Shared\Sim，不认协议，所以转换只能放网络层（NBC.Protocol 与 NBC.Shared
        //   两边都引得到）；放在 LockstepWire 是因为探针也编得到，能被逐字段验证。
        private void HandleLockstepStart(LockstepStart start)
        {
            if (start == null)
            {
                return;
            }

            // 每一条 lockstep_start 就是一局的开头（服务端一局只发一次，TCP 保序），所以这里重建
            // 运行器而不是「null 才建」：否则第二局 ApplyStart 会撞「重复开局」，导致永远停滞
            m_matchSerial++;
            m_lockstep = NewLockstepClient();

            // 转换在 LockstepWire：NBC.Protocol 与 NBC.Shared 两边都引得到，探针也编得到，
            // 能被逐字段验证；别搬进 Shared\Sim
            List<SimEntity> entities = LockstepWire.ToPlainEntities(start);

            bool ok = m_lockstep.ApplyStart(start.Frame, start.InitialHash, entities);

            Log("收到**锁步开局**（本会话第 " + m_matchSerial + " 局）：帧 " + start.Frame + "、" +
                entities.Count + " 个实体 ⇒ " +
                (ok ? "已开局" : "**开局失败**（initial_hash 对不上 —— 见分歧事件）"));
        }

        // 功能：把锁步一帧转成普通数据后喂给运行器，由它自己 Step 并比哈希。
        // 入参：frame 帧消息。
        // 返回：无。
        // 步骤：
        //   1) 还没有运行器就先懒建一个，帧可能先于开局到达
        //   2) 转换后用运行器收下，帧号与哈希一起交给它
        // 上游：Handle
        // 下游：EnsureLockstep、LockstepWire.ToPlainInputs、LockstepClient.ApplyFrame
        private void HandleLockstepFrame(LockstepFrame frame)
        {
            if (frame == null)
            {
                return;
            }

            EnsureLockstep();

            List<FrameInput> inputs = LockstepWire.ToPlainInputs(frame);

            if (m_lockstep != null)
            {
                m_lockstep.ApplyFrame(frame.Frame, inputs, frame.ServerHash);
            }
        }

        // 功能：从房间成员名单推出队伍映射，同步给运行器。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 没有运行器或没有房间状态就直接返回
        //   2) 按成员顺序取出玩家 id，交给 SortedTeamMap
        // 上游：HandleMatchResult
        // 下游：LockstepClient.SetTeams
        // 注意：仍然不加协议字段 —— 两端从同一份成员名单加同一个纯函数推出同一张映射。
        private void SyncLockstepTeams()
        {
            if (m_lockstep == null || m_room == null)
            {
                return;
            }

            var ids = new List<long>(m_room.Members.Count);

            for (int i = 0; i < m_room.Members.Count; i++)
            {
                ids.Add(m_room.Members[i].PlayerId);
            }

            m_lockstep.SetTeams(new SortedTeamMap(ids));
        }

        // 功能：收到胜负结果：协议转普通数据，转发给运行器复算并比对。
        // 入参：result 结果消息。
        // 返回：无。
        // 步骤：
        //   1) 先确保有运行器
        //   2) 顺手同步队伍映射，这样不依赖 room_state 与结果谁先到
        //   3) 交给运行器复算比对，日志里写一致还是不一致
        // 上游：Handle
        // 下游：EnsureLockstep、SyncLockstepTeams、LockstepClient.ApplyMatchResult、MatchRules
        // 注意：结果用 MatchRules.FromNumber 转，客户端不另写一份 switch；复算与比对留在
        //   LockstepClient，放这里就只能靠起真服务验。
        private void HandleMatchResult(MatchResult result)
        {
            if (result == null)
            {
                return;
            }

            EnsureLockstep();

            // 在这里同步队伍映射，就不依赖消息到达顺序：room_state 先来后到都能推到同一张映射
            SyncLockstepTeams();

            if (m_lockstep == null)
            {
                return;
            }

            bool agreed = m_lockstep.ApplyMatchResult(
                (int)result.Frame, (int)result.Outcome, (int)result.KillsA, (int)result.KillsB);

            Log("收到**胜负结果**：第 " + result.Frame + " 帧 ⇒ " + MatchRules.Describe(MatchRules.FromNumber((int)result.Outcome)) +
                "（击杀 " + result.KillsA + "/" + result.KillsB + "）⇒ " +
                (agreed ? "复算一致" : "**复算不一致**（见不一致事件）") +
                "；" + m_lockstep.Describe());
        }

        // 注意：本局的数 —— 新一局换新运行器所以从 0 开始，要跨局累计得在会话外记。
        /// <summary>与服务端胜负判定不一致的次数（本局），委托给运行器。</summary>
        public int MatchMismatchCount
        {
            get { return m_lockstep == null ? 0 : m_lockstep.MatchMismatchCount; }
        }

        /// <summary>落定的结果，还没落定 = null；本局的数。</summary>
        public EMatchOutcome? MatchOutcome
        {
            get { return m_lockstep == null ? null : m_lockstep.Outcome; }
        }

        // 服务端宣布的结果与我复算的不一致。
        // 注意：故意做成会话上的字段式事件 —— 运行器每局换一个，若订阅时把句柄挂到当时的运行器上，
        //   第二局就会悄悄失效；现在由 NewLockstepClient 每次新建时把事件转发到会话。
        public event Action<LockstepMatchMismatch> MatchMismatchDetected;

        // 功能：懒建运行器，并把它的两种上报接到本会话。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 已经有就返回
        //   2) 否则造一个新的，内部会接好转发
        // 上游：HandleLockstepFrame、HandleMatchResult
        // 下游：NewLockstepClient
        private void EnsureLockstep()
        {
            if (m_lockstep != null)
            {
                return;
            }

            m_lockstep = NewLockstepClient();
        }

        // 功能：造一个新运行器，并把它的两种上报转发到本会话自己的事件上。
        // 入参：无。
        // 返回：新运行器（非 null）。
        // 步骤：
        //   1) 新建运行器
        //   2) 分歧先记日志再转发，日志里带帧号与两个哈希
        //   3) 判定不一致直接转发
        // 上游：HandleLockstepStart、EnsureLockstep
        // 下游：DesyncDetected、MatchMismatchDetected
        // 注意：转发必须每次都重新接 —— 旧运行器随上一局一起丢掉，而订阅者订阅的是 NetSession
        //   的事件；否则第二局开始后分歧与判定不一致就再也不会有人知道。
        private LockstepClient NewLockstepClient()
        {
            var client = new LockstepClient();

            client.DesyncDetected += d =>
            {
                Log("⚠️ **锁步分歧**：" + d);
                DesyncDetected?.Invoke(d);          // 本会话的事件（字段式），跨局一直有效
            };

            client.MatchMismatchDetected += m => MatchMismatchDetected?.Invoke(m);

            return client;
        }

        // 功能：存下任务状态、记数、发事件、写日志。
        // 入参：states 任务状态（null 会被忽略，0 长度帧解出来就是 null）。
        // 返回：无。
        // 步骤：
        //   1) 空值忽略
        //   2) 整体替换而不是合并：服务端发的是全量，合并增量漏一条就永久少一个任务的状态
        //   3) 再记数、发事件、写日志
        // 上游：Handle
        // 下游：QuestStateReceived、Log
        private void HandleQuestState(QuestStateSync states)        {
            if (states == null)
            {
                return;
            }

            // 整体替换而不是合并：服务端发的是全量，合并增量漏一条就永久少一个任务的状态，而且不报错
            m_lastQuestStates = states;
            m_questStatesReceived++;

            QuestStateReceived?.Invoke(states);

            Log("收到**任务状态**：服务端帧 " + states.ServerTick + "，" + states.Entries.Count + " 个任务");
        }

        // 功能：存下权威进度、记数、发事件、写日志。
        // 入参：progress 权威进度（null 会被忽略，一条 0 长度帧解出来就是 null）。
        // 返回：无。
        // 步骤：
        //   1) 空值忽略
        //   2) 整体替换而不是合并：服务端发的是全量，合并增量漏一条就永久少一格
        //   3) 再记数、发事件、写日志
        // 上游：Handle
        // 下游：ProgressReceived、Log
        private void HandleProgressSync(ProgressSync progress)
        {
            if (progress == null)
            {
                return;
            }

            // 整体替换而不是合并：服务端发的是全量，合并增量漏一条就永久少一格，而且不报错
            m_lastProgress = progress;
            m_progressSyncs++;

            ProgressReceived?.Invoke(progress);

            Log("收到**权威进度**：服务端帧 " + progress.ServerTick + "，" + progress.Entries.Count + " 条条件");
        }

        // 功能：记数、留最近几条、发事件、写日志。
        // 入参：drop 掉落事件。
        // 返回：无。
        // 步骤：
        //   1) 空事件忽略
        //   2) 先发事件，再记数、留最近一条
        //   3) 新的插入队首，超过 MaxRecentDrops 就从尾部删
        //   4) 最后记一句日志，带物品、数量与归属
        // 上游：HandleServerEvent
        // 下游：DropReceived、Log
        private void HandleDrop(DropEvent drop)
        {
            if (drop == null)
            {
                return;
            }

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

        // 功能：把快照交给 SnapshotView，并在第一张时记一句日志。
        // 入参：snapshot 世界快照。
        // 返回：无。
        // 步骤：
        //   1) 先应用；旧快照会被拒，直接返回不刷日志
        //   2) 只有第一张成功的快照记日志，每帧记会把日志刷爆
        // 上游：Handle
        // 下游：SnapshotView.Apply、Log
        private void HandleSnapshot(WorldSnapshot snapshot)
        {
            bool applied = m_world.Apply(snapshot);

            if (!applied)
            {
                // 旧快照不刷日志，但值得知道它发生过
                return;
            }

            if (!m_loggedFirstSnapshot)
            {
                m_loggedFirstSnapshot = true;
                Log("收到第一张世界快照：" + m_world.Describe());
            }
        }

        // 功能：把握手消息发给服务端。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 组包：协议版本、客户端版本、玩家名、账号与密码摘要
        //   2) 游客时账号与摘要都是空串，服务端走老路
        //   3) 直接走传输层发，此刻还没上线，不能走 Send
        // 上游：Pump 刚连上时
        // 下游：ITransport.Send
        // 注意：日志里只有账号名与带没带密码，永远没有摘要或明文。
        private void SendHandshake()
        {
            var hello = new ClientMessage
            {
                Handshake = new Handshake
                {
                    ProtocolVersion = NetContract.Version,
                    ClientVersion = m_clientVersion,
                    PlayerName = m_playerName,

                    // 游客时这两个都是空串，服务端走老路
                    Account = m_account,
                    PasswordDigest = m_passwordDigest,
                },
            };

            m_transport.Send(hello.ToByteArray());

            // 日志里只有账号名与带没带密码，永远没有摘要或明文
            Log(IsGuest
                ? "已发握手（协议 v" + NetContract.Version + "，游客）"
                : "已发握手（协议 v" + NetContract.Version + "，账号 " + m_account + "）");
        }

        // 功能：发一次心跳，client_time_ms 用本地时钟，回来一减就是 RTT。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 组包后直接走传输层发
        //   2) 记下发送时刻（超时判定靠它）并累加已发条数
        // 上游：Pump 按节奏、SendPingNow、握手成功后
        // 下游：ITransport.Send
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

        // 内部：状态

        // 功能：记失败原因、主动关掉连接、切到失败态；状态没变时不会重复通知。
        // 入参：reason 人话原因。
        // 返回：无。
        // 步骤：
        //   1) 先看是不是第一次失败（失败原因为空），重复调用不再重复上报
        //   2) 标记这是主动关的，免得 Closed 回调再报一次
        //   3) 关传输层，再切状态、写日志
        // 上游：握手超时、心跳超时、解析失败、传输层断开
        // 下游：ITransport.Close、SetState、Log
        private void Fail(string reason)
        {
            bool first = m_failureReason == null;
            m_failureReason = reason;

            m_closingIntentionally = true;      // 我们自己关的，别再让 Closed 回调报一次
            m_transport.Close();

            SetState(ESessionState.Failed);
            Log("失败：" + reason);

            if (!first)
            {
                // 已经失败过了就不再重复上报，状态没变 SetState 也不会发事件
                return;
            }
        }

        // 功能：切状态，变了才发事件。
        // 入参：state 新状态。
        // 返回：无。
        // 步骤：
        //   1) 相同状态直接返回
        //   2) 先改字段再发事件，回调里读到的才是新状态
        // 上游：Connect、HandshakeAck、Disconnect、Fail
        // 下游：StateChanged
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

        // 功能：把一句人话转给 Note 事件。
        // 入参：line 内容。
        // 返回：无。
        // 步骤：
        //   1) 取本地副本再判空，订阅者中途退订也不会出问题
        //   2) 有订阅者就转发
        // 上游：本类各处
        // 下游：Note
        private void Log(string line)
        {
            Action<string> handler = Note;

            if (handler != null)
            {
                handler(line);
            }
        }

        // 功能：已释放还来调用就直接抛。
        // 入参：无。
        // 返回：无；已释放时抛 ObjectDisposedException。
        // 步骤：
        //   1) 只判 m_disposed，抛异常时带类型名
        // 上游：Connect、Send、SendLockstepInput
        // 下游：无
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
