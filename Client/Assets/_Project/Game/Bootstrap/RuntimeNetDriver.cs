// 这个文件里有：
//   ERuntimeSyncMode（枚举）、RuntimeNetDriver（类）：运行时的会话驱动（自动连服/进房/发输入/喂表现）

using System;
using System.Collections.Generic;
using NBC.Framework.Input;
using NBC.Framework.Net;
using NBC.Framework.Net.Adapter;
using NBC.Game.Net;
using NBC.Protocol;
using NBC.Game.Player;
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

        // 敌人模型 prefab（由装配者设置；null = 表现层回落胶囊）
        private GameObject m_monsterPrefab;

        // 攻击动作位：服务端约定「第 0 位 = 普攻」（RoomBattleService 里 ActionBits & 1u）
        private const uint AttackBitMask = 1u;

        // 客户端选目标的半径，毫米；服务端普攻射程 2000，这里留 1 米余量
        private const int AttackTargetRangeMm = 3000;

        // 本地玩家的动画组件（播攻击用）；只查一次
        private PlayerAnimation m_playerAnimation;

        // 剑上的 Trigger 碰撞体（R019：用「剑碰到谁」决定打谁）；只找/建一次
        private Collider m_swordCollider;

        // 是否已经找过剑（避免每次都深搜层级）
        private bool m_swordLooked;

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

        // 功能：设置敌人模型 prefab（必须在本组件 Start 之前设置，或在 Play 前配好）。
        // 入参：prefab 敌人模型；null = 回落胶囊
        // 返回：无
        // 步骤：直接赋值；表现层下次 Bind 时取用
        // 上游：HpBarDemoBehaviour 装配时
        // 下游：SnapshotSceneBinder.Ensure
        public GameObject MonsterPrefab
        {
            get { return m_monsterPrefab; }
            set { m_monsterPrefab = value; }
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

        // 功能：本帧若按下了攻击键，就选最近的怪并把「攻击位 + 目标」发出去，同时播本地攻击动画。
        // 入参：无
        // 返回：true = 本帧发了攻击（调用方这一帧就不要再发普通输入了）
        // 步骤：
        //   1) 读当前输入；没有动作位就直接返回 false
        //   2) 只有第 0 位（普攻）算攻击 —— 与服务端约定一致
        //   3) 选最近的活怪；没有目标就不发（免得服务端一直刷「目标 0 不存在」）
        //   4) 状态同步模式下带 targetId 发送；锁步模式暂不发攻击（动作位处理未确认）
        //   5) 播本地攻击动画（命中仍由服务端裁决）
        // 上游：PumpInput
        // 下游：NetSession.SendInput、PlayerAnimation.PlayAttack
        // 注意：不能用「按住」判 ——— ActionBits 是「本帧新按下」，按住不会重复置位
        private bool TrySendAttack()
        {
            InputManager input = InputManager.Instance;

            if (input == null || !input.IsEnabled)
            {
                return false;
            }

            InputCommand command = input.Current;

            if ((command.ActionBits & AttackBitMask) == 0u)
            {
                return false;
            }

            // 先用「剑碰到谁」选目标（R019 修订）；剑没碰到任何怪才回落到「最近怪」
            int targetId = FindSwordHitTarget();

            if (targetId == 0)
            {
                targetId = FindNearestMonster();
            }

            if (targetId == 0)
            {
                return false;
            }

            if (m_mode == ERuntimeSyncMode.Lockstep)
            {
                // 锁步链的远程攻击还没做（服务端只处理了状态同步的输入）
                return false;
            }

            m_session.SendInput(command.MoveX, command.MoveY, command.ActionBits, command.ActionReleaseBits, targetId);

            if (m_playerAnimation == null)
            {
                m_playerAnimation = UnityEngine.Object.FindObjectOfType<PlayerAnimation>();
            }

            if (m_playerAnimation != null)
            {
                m_playerAnimation.PlayAttack();
            }

            return true;
        }

        // 功能：用「剑的碰撞盒」找出这一帧被剑碰到的怪（只决定「打谁」，命中仍由服务端裁决）。
        // 入参：无
        // 返回：被碰到的怪里离玩家最近的那个实体编号；剑不存在或没碰到 ⇒ 0
        // 步骤：
        //   1) 第一次调用时找武器骨骼并确保它有一个 Trigger BoxCollider
        //   2) 用该碰撞盒的**当帧世界包围盒**做 OverlapBox
        //   3) 对每个相交的碰撞体，往前找 MonsterSlotTag（拿实体编号）
        //   4) 多个命中时取离玩家最近的（避免一次挥剑打到三只时乱选）
        // 上游：TrySendAttack
        // 下游：Physics.OverlapBox、MonsterSlotTag
        // 注意：只在按下那一帧采样 —— 剑在动画里会大幅摆动，持续检测会把「扫过」也算命中
        private int FindSwordHitTarget()
        {
            EnsureSwordCollider();

            if (m_swordCollider == null || !m_swordCollider.enabled)
            {
                return 0;
            }

            Bounds box = m_swordCollider.bounds;

            // QueryTriggerInteraction.Collide：剑上的碰撞体通常是 Trigger，不显式允许就查不到
            Collider[] hits = Physics.OverlapBox(box.center, box.extents, Quaternion.identity, ~0,
                QueryTriggerInteraction.Collide);

            if (hits == null || hits.Length == 0)
            {
                return 0;
            }

            Transform player = m_swordCollider.transform.root;
            int bestId = 0;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < hits.Length; i++)
            {
                MonsterSlotTag tag = hits[i].GetComponentInParent<MonsterSlotTag>();

                if (tag == null || tag.EntityId == 0)
                {
                    continue;
                }

                float distance = Vector3.Distance(player.position, tag.transform.position);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestId = tag.EntityId;
                }
            }

            return bestId;
        }

        // 功能：找到武器骨骼并在它身上准备一个 Trigger BoxCollider（只做一次）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 找玩家物体（第一个 PlayerAnimation）
        //   2) 深度搜一个名字含 Weapon_Sword 且激活的骨骼；找不到就退到名字含 hand_r 的骨骼
        //   3) 该骨骼已有 BoxCollider 就复用；没有就按它下面 Renderer 的包围盒建一个 Trigger 盒
        // 上游：FindSwordHitTarget
        // 下游：Physics.OverlapBox
        // 注意：换模型后若没有这些骨骼，这里会静默留空 —— 选目标回落到「最近怪」，不会崩
        private void EnsureSwordCollider()
        {
            if (m_swordLooked)
            {
                return;
            }

            m_swordLooked = true;

            if (m_playerAnimation == null)
            {
                m_playerAnimation = UnityEngine.Object.FindObjectOfType<PlayerAnimation>();
            }

            if (m_playerAnimation == null)
            {
                return;
            }

            Transform weapon = FindBone(m_playerAnimation.transform, "weapon_sword");

            if (weapon == null)
            {
                weapon = FindBone(m_playerAnimation.transform, "hand_r");
            }

            if (weapon == null)
            {
                return;
            }

            // 优先用已经挂好的碰撞体（负责人会在 prefab 的 Weapon_Sword 上加），任何类型都接受
            Collider existing = weapon.GetComponent<Collider>();

            if (existing == null)
            {
                existing = weapon.GetComponentInChildren<Collider>(true);
            }

            if (existing == null)
            {
                // 一个都没有才自己建：用骨骼下 Renderer 的包围盒定尺寸，并设成 Trigger（不参与推挤）
                BoxCollider box = weapon.gameObject.AddComponent<BoxCollider>();
                box.isTrigger = true;

                Renderer[] renderers = weapon.GetComponentsInChildren<Renderer>(true);
                Bounds local = new Bounds(Vector3.zero, new Vector3(0.3f, 0.3f, 1.0f));

                if (renderers != null && renderers.Length > 0)
                {
                    local = renderers[0].bounds;

                    for (int i = 1; i < renderers.Length; i++)
                    {
                        local.Encapsulate(renderers[i].bounds);
                    }
                }

                box.center = weapon.InverseTransformPoint(local.center);
                box.size = local.size;
                existing = box;
            }

            // 剑的碰撞体只用来「查询打到谁」，不能参与推挤：狼是动态刚体，非 Trigger 的剑会把它推走，
            // 而且骨骼每帧移动的静态碰撞体在物理里开销很大。prefab 里勾没勾都按 Trigger 用。
            if (!existing.isTrigger)
            {
                existing.isTrigger = true;
                Debug.Log("[攻击] 剑上的 " + existing.GetType().Name + " 原本不是 Trigger，已在运行时按 Trigger 使用" +
                          "（否则会推动带刚体的怪）。要改请在 prefab 上勾 Is Trigger。", existing);
            }

            m_swordCollider = existing;
        }

        // 功能：在层级里按名字片段找一个激活的骨骼。
        // 入参：root 搜索起点；fragment 名字片段（小写）
        // 返回：第一个命中的 Transform；没有返回 null
        // 步骤：遍历全部子物体（含隐藏），名字转小写比片段，并要求该物体是激活的
        // 上游：EnsureSwordCollider
        // 下游：无
        private static Transform FindBone(Transform root, string fragment)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);

            for (int i = 0; i < all.Length; i++)
            {
                if (!all[i].gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (all[i].name.ToLower().Contains(fragment))
                {
                    return all[i];
                }
            }

            return null;
        }
        // 功能：在表现层的快照里找最近的「活着的怪」（OwnerPlayerId == 0），限定半径内。
        // 入参：无（半径用常量 AttackTargetRangeMm）
        // 返回：目标实例编号；没有目标返回 0
        // 步骤：
        //   1) 没有数据或没有本地英雄位置 ⇒ 0（无法算距离）
        //   2) 遍历实体：跳过自己、跳过别人的英雄（OwnerPlayerId != 0）、跳过死的
        //   3) 算水平距离（毫米），取最近且不超过半径的
        // 上游：TrySendAttack
        // 下游：无
        // 注意：这里只负责「选一个看起来能打到的」，命中判定仍然在服务端（客户端不做预测）
        private int FindNearestMonster()
        {
            if (m_feed == null || m_feed.View == null)
            {
                return 0;
            }

            EntitySnapshot hero = m_feed.View.FindHero(m_session.PlayerId);

            if (hero == null)
            {
                return 0;
            }

            IReadOnlyList<EntitySnapshot> entities = m_feed.View.Entities;
            int bestId = 0;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < entities.Count; i++)
            {
                EntitySnapshot entity = entities[i];

                if (!entity.Alive || entity.OwnerPlayerId != 0L)
                {
                    continue;
                }

                int dx = entity.PosXMm - hero.PosXMm;
                int dz = entity.PosZMm - hero.PosZMm;
                int distance = (int)Math.Sqrt((double)dx * dx + (double)dz * dz);

                if (distance > AttackTargetRangeMm)
                {
                    continue;
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestId = entity.EntityId;
                }
            }

            return bestId;
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
            // 攻击：立即发，不套 50ms 节流（按键可能只持续一两帧，套节流会丢手感）
            if (TrySendAttack())
            {
                m_sendAccumulator = 0f;
                return;
            }

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
                m_binder = SnapshotSceneBinder.Ensure(m_feed.View, m_session.PlayerId, m_monsterPrefab);
            }
            else if (m_binder != null)
            {
                // 每帧同步玩家编号：绑定只发生一次，而握手完成前它是 0 —— 不同步就永远跳不掉本地英雄
                m_binder.SetOwnerPlayerId(m_session.PlayerId);
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
