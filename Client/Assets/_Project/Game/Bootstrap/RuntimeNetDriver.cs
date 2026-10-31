// 这个文件里有：
//   ERuntimeSyncMode（枚举）、RuntimeNetDriver（类）：运行时的会话驱动（自动连服/进房/发输入/喂表现）

using NBC.Framework.Input;
using NBC.Framework.Net;
using NBC.Framework.Net.Adapter;
using NBC.Game.Net;
using NBC.Game.View;
using NBC.Shared.Sim;
using UnityEngine;

namespace NBC.Game.Bootstrap
{
    // 运行时用哪种同步方式。只影响「客户端读哪一份世界 + 发哪条上行」，服务端两条链都在跑。
    public enum ERuntimeSyncMode
    {
        // 状态同步：读服务端快照，上行 SendInput
        StateSync = 0,

        // 帧同步：读锁步世界，上行 SendLockstepInput
        Lockstep = 1
    }

    // 主要功能：Play 后自动建会话、连接、进房、按模式发输入、把数据喂给表现层，并把统计写进 HUD。
    // 主要数据成员：m_host / m_port / m_account / m_password / m_dungeonId / m_mode / m_inputSendInterval。
    // 主要外部方法：IsOnline / IsJoined / LastError（只读状态）。
    // 谁在用它：由 Boot 侧（HpBarDemoBehaviour）在资源层装好后 AddComponent 挂上，或场景里手挂。
    public sealed class RuntimeNetDriver : MonoBehaviour
    {
        // 服务器地址
        [SerializeField] private string m_host = "127.0.0.1";

        // 服务器端口（9000 在 Windows 保留段 8945-9044 里，别用）
        [SerializeField] private int m_port = 7778;

        // 开发账号（服务端 --dev-accounts 时可用 dev1..dev4）
        [SerializeField] private string m_account = "dev1";

        // 账号密码
        [SerializeField] private string m_password = "dev-pw1";

        // 副本编号（房号留空 = 服务端安排）
        [SerializeField] private int m_dungeonId = 1001;

        // 运行时同步方式
        [SerializeField] private ERuntimeSyncMode m_mode = ERuntimeSyncMode.StateSync;

        // 上行节流间隔（秒）。20Hz 对状态同步足够；锁步同样按这个节奏问一次。
        [SerializeField] private float m_inputSendInterval = 0.05f;

        // 会话（本组件的唯一持有者）
        private NetSession m_session;

        // 表现层数据源（快照 / 锁步二选一，复用 Editor 同一条路径）
        private PresentationFeed m_feed;

        // 3D 绑定器（缓存，不每帧找）
        private SnapshotSceneBinder m_binder;

        // 是否已经发过进房请求（只发一次）
        private bool m_joinRequested;

        // 发送节流累加器
        private float m_sendAccumulator;

        // 最近一次错误（空 = 没出错）
        private string m_lastError = string.Empty;

        // 断线重连策略（退避与次数上限都在它里面，本组件只执行它给的动作）
        private NetReconnector m_reconnector;

        // 上一帧是否在房间里（用来识别「重新进房」这个事件，重连回来时据此重置表现层）
        private bool m_wasInRoom;

        // 功能：告诉调用方是否已握手成功。
        // 入参：无
        // 返回：是否已握手成功
        public bool IsOnline
        {
            get { return m_session != null && m_session.IsOnline; }
        }

        // 功能：告诉调用方是否已进房。
        // 入参：无
        // 返回：是否已进房
        public bool IsJoined
        {
            get { return m_session != null && m_session.InRoom; }
        }

        // 功能：返回最近一次错误描述。
        // 入参：无
        // 返回：最近一次错误描述（空 = 没出错）
        public string LastError
        {
            get { return m_lastError; }
        }

        // 功能：建传输层与会话，订阅状态事件，并发起连接。
        // 入参：无（引擎在开始帧调一次）
        // 返回：无
        // 步骤：
        //   1) 建表现层数据源
        //   2) 把传输层包上统计装饰器（HUD 的带宽靠它）
        //   3) 建会话并订阅状态/握手事件
        //   4) 发起连接（非阻塞，连上与否靠每帧 Pump 推进）
        // 上游：Unity 引擎
        // 下游：CountingTransport、NetSession.Connect
        private void Start()
        {
            m_feed = new PresentationFeed();
            m_reconnector = new NetReconnector(5, m_dungeonId);

            ITransport transport = new CountingTransport(new TcpTransport());

            // 先清零：上一次运行（或 Editor 窗口）留下的统计不该带过来
            NetRuntimeStats.Reset();

            m_session = new NetSession(
                transport,
                "runtime-" + m_account,
                "unity-runtime/" + Application.unityVersion,
                NetSession.DefaultHeartbeatIntervalMs,
                NetSession.DefaultHeartbeatTimeoutMs,
                NetSession.DefaultHandshakeTimeoutMs,
                m_account,
                m_password);

            m_session.StateChanged += OnStateChanged;
            m_session.HandshakeCompleted += OnHandshakeCompleted;

            m_session.Connect(m_host, m_port);
        }

        // 功能：每帧驱动会话，并在需要时进房、发输入、喂表现层、写统计。
        // 入参：无（引擎每帧调）
        // 返回：无
        // 步骤：
        //   1) 会话没了就返回
        //   2) Pump：收字节 → 拆帧 → 处理消息（锁步帧在这一步就步进了）→ 心跳
        //   3) 在线且没进房 ⇒ 发一次进房请求
        //   4) 已进房 ⇒ 按节流发本地输入
        //   5) 选数据源并喂表现层 + 缓存 3D 绑定器
        //   6) 写 HUD 统计（模式 / RTT / 帧号）
        // 上游：Unity 引擎
        // 下游：NetSession.Pump / JoinRoom / SendInput / SendLockstepInput、PresentationFeed、SnapshotSceneBinder、NetRuntimeStats
        // 注意：deltaMs 必须是真实毫秒。传固定值会把会话内部时钟拖慢，心跳与超时全偏
        private void Update()
        {
            if (m_session == null)
            {
                return;
            }

            int deltaMs = Mathf.RoundToInt(Time.deltaTime * 1000f);
            m_session.Pump(deltaMs);

            DriveReconnect();

            if (m_session.IsOnline && !m_joinRequested)
            {
                m_joinRequested = true;

                // 房号留空 = 服务端安排；副本编号决定用哪张图
                m_session.JoinRoom(string.Empty, m_dungeonId);
            }

            // 必须同时「在线」才发输入：InRoom 只看房间字段，断线后它仍是 true（见 NetSession.InRoom）
            if (m_session.IsOnline && m_session.InRoom)
            {
                NoteJoinedNow();
                PumpInput();
            }

            RefreshPresentation();
            PushStats();
        }

        // 功能：每帧把会话状态喂给重连策略，并执行它给的下一步动作。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没有策略就返回（Start 之前不会发生）
        //   2) 取策略的下一步动作与放弃原因
        //   3) 按动作执行：Connect 重连、JoinRoom 回原房、GiveUp 记错误并停手
        //   4) 阶段变了就打一行日志（只在变化时打，避免刷屏）
        // 上游：Update（Pump 之后）
        // 下游：NetSession.Connect / JoinRoom、NetReconnector.Tick
        // 注意：Connect 用的是同一个 NetSession —— 账号与密码摘要在构造时就固定，不需要也不应该重建会话
        private void DriveReconnect()
        {
            if (m_reconnector == null)
            {
                return;
            }

            bool online = m_session.State == ESessionState.Online;
            string reason;

            // 把会话状态（而不是布尔「在线」）喂给策略：它要区分「连接进行中」与「连接已失败」，
            // 否则会对着一个还在 Connecting 的会话重复 Connect，直接抛「已经在连」
            EReconnectAction action = m_reconnector.Tick(m_session.State, m_session.InRoom, Time.deltaTime, out reason);

            if (action == EReconnectAction.Connect)
            {
                Debug.Log("[Reconnect] 第 " + m_reconnector.Attempt + " 次重连：" + m_host + ":" + m_port);
                m_session.Connect(m_host, m_port);
            }
            else if (action == EReconnectAction.JoinRoom)
            {
                Debug.Log("[Reconnect] 已重连，回房：" + m_reconnector.LastRoomId);
                m_session.JoinRoom(m_reconnector.LastRoomId, m_dungeonId);
            }
            else if (action == EReconnectAction.Reset)
            {
                // 连接卡住超过 3 秒：先断掉，下一帧状态回到 Disconnected 才允许再 Connect
                Debug.LogWarning("[Reconnect] 连接超时，重置会话后重试");
                m_session.Disconnect("重连超时，重置连接");
            }
            else if (action == EReconnectAction.GiveUp)
            {
                m_lastError = reason;
                Debug.LogWarning("[Reconnect] 放弃重连：" + reason);
            }

            // 断线期间要把「在房」标记清掉，这样重连回来时才算一次「重新进房」（表现层才重置）。
            // ⚠️ 判据必须是「是否在线」：InRoom 只看房间字段，断开后它仍然是 true，用它判永远清不掉
            if (!online)
            {
                m_wasInRoom = false;
            }
        }

        // 功能：进房成功时把房号记给重连策略，并在「这是重连回来的」时重置表现层。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 房号为空（服务端还没下发 RoomState）就直接返回
        //   2) 之前已在房里 ⇒ 说明是第一次进房，只记房号
        //   3) 之前不在房里 ⇒ 这是重连回来：清表现层缓存、让绑定器重建
        //   4) 记房号（策略据此在下次断线时回原房）
        // 上游：Update（InRoom 为真时每帧调）
        // 下游：PresentationFeed.Clear、NetReconnector.NoteJoined
        // 注意：这里必须清表现层 —— 上一局的帧号与换局计数会让新会话被判成「帧号倒退」或「假分歧」
        private void NoteJoinedNow()
        {
            if (!m_session.InRoom || m_reconnector == null)
            {
                return;
            }

            string roomId = m_session.CurrentRoom == null ? string.Empty : m_session.CurrentRoom.RoomId;

            if (string.IsNullOrEmpty(roomId))
            {
                return;
            }

            if (!m_wasInRoom)
            {
                m_wasInRoom = true;

                // 阶段还在重连三态之一 ⇒ 这次进房是重连回来的，必须清表现层
                EReconnectPhase phase = m_reconnector.Phase;
                bool rejoined = phase == EReconnectPhase.Waiting
                    || phase == EReconnectPhase.Connecting
                    || phase == EReconnectPhase.Joining;

                if (rejoined)
                {
                    m_feed.Clear();
                    m_binder = null;
                    Debug.Log("[Reconnect] 已回到房间 " + roomId + "，表现层已重置");
                }
            }

            m_reconnector.NoteJoined(roomId);
        }
        // 功能：退订事件、释放会话，并把统计清零。
        // 入参：无
        // 返回：无
        // 上游：Update
        // 下游：NetSession.Disconnect、NetRuntimeStats.Reset
        private void OnDestroy()
        {
            m_joinRequested = false;
            m_wasInRoom = false;

            if (m_reconnector != null)
            {
                m_reconnector.NoteIntentionalDisconnect();
            }

            if (m_session != null)
            {
                m_session.StateChanged -= OnStateChanged;
                m_session.HandshakeCompleted -= OnHandshakeCompleted;
                m_session.Dispose();
                m_session = null;
            }

            NetRuntimeStats.Reset();
        }

        // 功能：按节流间隔把本地输入发出去（锁步或状态同步二选一）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 累加时间，不到间隔就不发
        //   2) 读本地输入（InputManager 每帧自己 Tick，这里只取当前值）
        //   3) 按模式二选一发上行（两条链不能同时喂，否则权威数据打架）
        // 上游：Update
        // 下游：NetSession.SendInput / NetSession.SendLockstepInput
        private void PumpInput()
        {
            m_sendAccumulator += Time.deltaTime;

            if (m_sendAccumulator < m_inputSendInterval)
            {
                return;
            }

            m_sendAccumulator = 0f;

            InputManager input = InputManager.Instance;

            if (input == null || !input.IsEnabled)
            {
                return;
            }

            InputCommand command = input.Current;

            if (m_mode == ERuntimeSyncMode.Lockstep)
            {
                // 锁步：被挡下（没开局/重复帧）会返回 false，这里不当作错误
                m_session.SendLockstepInput(command.MoveX, command.MoveY, command.ActionBits);
            }
            else
            {
                m_session.SendInput(command);
            }
        }

        // 功能：选好数据源并刷新表现层，必要时创建 3D 绑定器。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 把开关交给数据源（决定读快照还是读锁步世界）
        //   2) 刷新（内部处理换局、卡帧回退、不重复喂）
        //   3) 第一次拿到数据时创建/缓存 3D 绑定器
        // 上游：Update
        // 下游：PresentationFeed.Refresh、SnapshotSceneBinder.Ensure
        private void RefreshPresentation()
        {
            LockstepClient client = m_session.Lockstep;

            m_feed.UseLockstep = m_mode == ERuntimeSyncMode.Lockstep;

            m_feed.Refresh(
                m_session.PlayerId,
                m_session.World,
                client == null ? null : client.World,
                client == null ? -1 : client.LastSteppedFrame,
                client != null && client.Started,
                m_session.MatchSerial);

            if (m_binder == null && m_feed.View != null)
            {
                m_binder = SnapshotSceneBinder.Ensure(m_feed.View, m_session.PlayerId);
            }
        }

        // 功能：把模式、RTT、帧号写进 HUD 只读的静态统计。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 模式与 RTT 直接取会话的
        //   2) 帧号按模式取：状态同步 = 快照 tick；帧同步 = 已步进帧
        // 上游：Update
        // 下游：NetRuntimeStats（HUD 只读它）
        // 注意：不写这里，HUD 会一直显示「离线」—— 它只读静态统计，不认识会话
        private void PushStats()
        {
            NetRuntimeStats.SetRtt(m_session.RttMs);

            if (m_mode == ERuntimeSyncMode.Lockstep)
            {
                LockstepClient client = m_session.Lockstep;
                NetRuntimeStats.SetFrame(client == null ? -1 : client.LastSteppedFrame);
                NetRuntimeStats.SetMode(client != null && client.Started ? NetSyncMode.Lockstep : NetSyncMode.Offline);
            }
            else
            {
                NetRuntimeStats.SetFrame((int)m_session.World.ServerTick);
                NetRuntimeStats.SetMode(m_session.IsOnline ? NetSyncMode.StateSync : NetSyncMode.Offline);
            }
        }

        // 功能：会话失败或断开时把模式改回离线。
        // 入参：state 新状态
        // 返回：无
        // 上游：NetSession.StateChanged
        // 下游：无
        private void OnStateChanged(ESessionState state)
        {
            if (state == ESessionState.Failed || state == ESessionState.Disconnected)
            {
                NetRuntimeStats.SetMode(NetSyncMode.Offline);
            }
        }

        // 功能：握手成功后按当前模式点亮统计里的模式。
        // 入参：ack 握手结果
        // 返回：无
        // 上游：NetSession.HandshakeCompleted
        // 下游：无
        private void OnHandshakeCompleted(NBC.Protocol.HandshakeAck ack)
        {
            m_lastError = string.Empty;

            NetRuntimeStats.SetMode(m_mode == ERuntimeSyncMode.Lockstep
                ? NetSyncMode.Lockstep
                : NetSyncMode.StateSync);
        }
    }
}
