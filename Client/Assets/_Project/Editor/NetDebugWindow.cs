// ============================================================================
//  NetDebugWindow —— 编辑器里的网络调试窗口（M3-S3）
//  项目：3D联网战斗Demo   对应：Docs\25 §八 S3 的验收（"负责人亲手连一次"）
//
//  ---------------------------------------------------------------------------
//  为什么是"窗口"而不是一条菜单项
//  ---------------------------------------------------------------------------
//  菜单点一下只能给一个**瞬间**的结论（连上了/报错了），而联调里真正要看的是
//  **持续状态**：RTT 稳不稳、心跳有没有在发、失败原因是什么、收发统计涨不涨。
//  所以这里做一个常驻窗口，把 `NetSession` 的状态摊开显示。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 时间也是"喂"进去的：为什么要把 delta 夹到 250ms
//  ---------------------------------------------------------------------------
//  `NetSession` 的时钟由调用方喂（它的文件头第二节）。编辑器不是游戏循环：
//  窗口失去焦点、编辑器卡顿、断点停住之后，`EditorApplication.update` 的间隔可能有好几秒。
//  如果照实喂进去，`NetSession` 会立刻判"心跳超时"——**假掉线**。
//  所以这里把单次 delta **夹到 250ms**：时钟宁可走慢，也不许跳。
//
//  ⚠️ 已知局限（如实记）：`OnDisable` 会断开。
//     窗口关掉、或进入播放模式触发域重载时，静态/窗口状态会丢，连接随之消失。
//     M3 的客户端会话本来就该挂在**游戏流程**里（S5 接进战斗），这里只是"能点着看"的入口。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;           // M4-S1：`EventCenter`（全局事件中心）
using NBC.Framework.Net.Adapter;
using NBC.Game.Battle;         // M4-S1：`ServerEventBridge`（服务端事件 → 事件中心）
using NBC.Game.Net;
using NBC.Game.Quest;          // M4-S1：`ConditionEventBridge`（事件中心 → 条件系统，M2 就有的那座桥）
using NBC.Shared.Battle;       // M4-S1b：`BattleRules`（射程/冷却——**两端同一个常量**，别在窗口里再写一份）
using NBC.Shared.Condition;    // M4-S1：`ConditionTracker` / `ConditionDef` / `EConditionEvent`
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    /// <summary>编辑器里的网络调试窗口：连接 / 断开 / 看心跳与状态。</summary>
    public sealed class NetDebugWindow : EditorWindow
    {
        /// <summary>单次推进的最大毫秒数（见文件头：防止编辑器卡顿造成"假掉线"）。</summary>
        private const int MaxDeltaMs = 250;

        /// <summary>日志区最多保留多少行。</summary>
        private const int MaxLogLines = 200;

        /// <summary>服务端地址（M3 的默认验收就是本机）。</summary>
        [SerializeField] private string m_host = "127.0.0.1";

        /// <summary>端口（与服务端 `TcpServerTransport.DefaultPort` 一致）。</summary>
        [SerializeField] private int m_port = 7777;

        /// <summary>玩家名（M3 没有账号系统，只用于日志/席位）。</summary>
        [SerializeField] private string m_playerName = "剑士";

        /// <summary>想进哪个副本（`Dungeon` 表主键；S6 才会真用它建副本）。</summary>
        [SerializeField] private int m_dungeonId = 1001;

        /// <summary>指定房号（留空 = 让服务端按副本安排一个）。</summary>
        [SerializeField] private string m_roomId = "";

        /// <summary>输入：左右轴（-1000..1000，沿用 A7 的量化约定）。</summary>
        [SerializeField] private int m_inputX;

        /// <summary>输入：前后轴（-1000..1000）。</summary>
        [SerializeField] private int m_inputY;

        /// <summary>勾上就每帧把当前输入发出去（模拟「一直按着方向」）。</summary>
        [SerializeField] private bool m_inputContinuous;

        /// <summary>当前目标（攻击发给谁；0 = 没目标）。</summary>
        [SerializeField] private int m_targetEntityId;

        /// <summary>
        /// M4-S1b：**自动追击并攻击**（负责人实测"目标不在范围内，难测"才加的）。
        /// <para>⚠️ 它只是**替你按键**：位置、伤害、掉落照样全由服务端算 —— 这不是客户端预测，
        /// 而是个调试工具（和"持续发送"同一性质）。</para>
        /// </summary>
        [SerializeField] private bool m_autoChase;

        /// <summary>自动追击：距离下一次可以按键还剩几帧（与服务端冷却同源）。</summary>
        private int m_autoAttackCooldownTicks;

        /// <summary>自动追击：一共按了多少次普攻（界面上看得见"到底有没有按出去"）。</summary>
        private int m_autoAttackCount;

        /// <summary>会话（没连时为 null）。</summary>
        private NetSession m_session;

        /// <summary>M4-S1：把服务端事件接进事件中心的桥（**联机战斗能推进任务**就靠它）。</summary>
        private ServerEventBridge m_eventBridge;

        /// <summary>M4-S1：演示用的条件系统（登记"击杀野狼 6001 × 3"）。</summary>
        private ConditionTracker m_conditions;

        /// <summary>M4-S1：事件中心 → 条件系统的那座桥（M2 就有的那一个，任务模块不认识网络）。</summary>
        private ConditionEventBridge m_conditionBridge;

        /// <summary>M4-S1：演示条件的编号（真项目里是 `QuestCondition` 表主键）。</summary>
        private const int DemoConditionKey = 5001;

        /// <summary>M4-S1：演示条件打的怪（6001 = 野狼，与 `Monster` 表一致）。</summary>
        private const int DemoMonsterId = 6001;

        /// <summary>M4-S1：演示条件要打几只。</summary>
        private const int DemoKillCount = 3;

        /// <summary>M4-S1：这个演示条件达成过几次（达成回调计数）。</summary>
        private int m_demoConditionMetCount;

        /// <summary>传输（图省事由窗口自己持有，方便 Dispose）。</summary>
        private TcpTransport m_transport;

        /// <summary>界面上的日志（新的在下面）。</summary>
        private readonly List<string> m_log = new List<string>();

        /// <summary>上次推进的时刻（算 delta 用）。</summary>
        private double m_lastUpdateTime;

        /// <summary>日志框的高度（像素）—— 见 `DrawLog`：外层滚动之后必须给固定高度。</summary>
        private const float LogBoxHeight = 170f;

        /// <summary>日志滚动位置。</summary>
        private Vector2 m_logScroll;

        /// <summary>
        /// 整窗滚动位置（2026-09-26 补）。
        /// <para>⚠️ 这个窗口原来是一整列、**没有任何滚动容器**：节一多（状态 / 房间 / 世界 / 掉落 / 输入 /
        /// 任务条件 / 日志），下半截就被窗口边界裁掉，而且**没有办法翻到**——
        /// 负责人实测"全屏也看不到任务完成情况"。加一个外层滚动视图 + 顶部常驻摘要行即可。</para>
        /// </summary>
        private Vector2 m_scroll;

        /// <summary>打开窗口。</summary>
        [MenuItem("Tools/NBC/网络/网络调试窗口")]
        public static void Open()
        {
            NetDebugWindow window = GetWindow<NetDebugWindow>("NBC 网络调试");
            window.minSize = new Vector2(420f, 380f);
            window.Show();
        }

        /// <summary>挂上编辑器帧回调。</summary>
        private void OnEnable()
        {
            m_lastUpdateTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
        }

        /// <summary>摘掉回调并断开（见文件头"已知局限"）。</summary>
        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            CloseSession("窗口关闭");

            // ⚠️ 订阅了就必须退订：这两座桥都挂在全局的 `EventCenter` / `NetSession` 上，
            //    不退订 = 窗口关了回调还在跑（A4/A8 踩过的同一个坑）。
            if (m_conditionBridge != null)
            {
                m_conditionBridge.Dispose();
                m_conditionBridge = null;
            }

            m_conditions = null;
        }

        /// <summary>编辑器帧回调：推进会话并重绘。</summary>
        private void Tick()
        {
            if (m_session == null)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            int deltaMs = (int)Math.Round((now - m_lastUpdateTime) * 1000.0);
            m_lastUpdateTime = now;

            if (deltaMs < 0)
            {
                deltaMs = 0;
            }
            else if (deltaMs > MaxDeltaMs)
            {
                deltaMs = MaxDeltaMs;   // 见文件头：宁可走慢，不许跳
            }

            m_session.Pump(deltaMs);
            SendContinuousInput();
            TickAutoChase();
            Repaint();
        }

        /// <summary>
        /// M4-S1b：自动追击并攻击（每编辑器帧一次；关掉开关就什么都不做）。
        /// <para>做三件事：① 目标死了/没了就换成**最近的活怪**；② 不在射程内就朝它走；
        /// ③ 进了射程就**按一下普攻**再等冷却（和服务端同一个冷却常量）。</para>
        /// <para>⚠️ 距离用**切比雪夫距离** —— 与服务端 `DungeonBattle.DistanceMm` 同一种量法；
        /// 用欧氏距离会在四角误判成"还差一点"，于是你会站在射程边上永远不按攻击。</para>
        /// </summary>
        private void TickAutoChase()
        {
            if (!m_autoChase || m_session == null || !m_session.IsOnline || !m_session.InRoom)
            {
                return;
            }

            SnapshotView world = m_session.World;
            NBC.Protocol.EntitySnapshot mine = world.FindHero(m_session.PlayerId);

            if (mine == null)
            {
                // 快照里还没有"我的英雄"（刚进房 / 已经死了）—— 什么都不发
                m_autoAttackCooldownTicks = 0;
                return;
            }

            NBC.Protocol.EntitySnapshot target = FindEntity(world, m_targetEntityId);

            if (target == null || !target.Alive)
            {
                m_targetEntityId = NearestAliveMonsterId();
                target = FindEntity(world, m_targetEntityId);

                if (target == null)
                {
                    return;     // 没怪可打了（副本清完）
                }
            }

            int distance = Chebyshev(mine, target);

            if (distance > BattleRules.BasicAttackRangeMm)
            {
                int moveX = target.PosXMm == mine.PosXMm ? 0 : (target.PosXMm > mine.PosXMm ? 1000 : -1000);
                int moveY = target.PosZMm == mine.PosZMm ? 0 : (target.PosZMm > mine.PosZMm ? 1000 : -1000);

                m_autoAttackCooldownTicks = 0;      // 赶路时不用按攻击
                m_inputX = moveX;                   // 让滑条跟着动，界面上看得见在往哪走
                m_inputY = moveY;
                m_session.SendInput(moveX, moveY, 0u, 0u, m_targetEntityId);
                return;
            }

            // 射程内：站住 + 按一下普攻（按下 → 下一帧松开 → 等冷却，模拟"点一下"）
            m_inputX = 0;
            m_inputY = 0;

            if (m_autoAttackCooldownTicks > 0)
            {
                bool release = m_autoAttackCooldownTicks == BattleRules.BasicAttackCooldownTicks;
                m_autoAttackCooldownTicks--;
                m_session.SendInput(0, 0, 0u, release ? 1u : 0u, m_targetEntityId);
                return;
            }

            m_session.SendInput(0, 0, 1u, 0u, m_targetEntityId);
            m_autoAttackCount++;
            m_autoAttackCooldownTicks = BattleRules.BasicAttackCooldownTicks;
        }

        /// <summary>
        /// 勾了「持续发送」就每帧发一条输入（模拟"一直按着方向键"）。
        /// <para>
        /// ⚠️ 客户端**发多密都不影响速度** —— 服务端每 tick 只应用一次（移动是状态）。
        /// 这里按编辑器帧率发（可能 60+/秒），正好也是在验证这条限速。
        /// </para>
        /// </summary>
        private void SendContinuousInput()
        {
            if (!m_inputContinuous || m_session == null || !m_session.IsOnline || !m_session.InRoom)
            {
                return;
            }

            m_session.SendInput(m_inputX, m_inputY, 0u, 0u, m_targetEntityId);
        }

        /// <summary>画界面。</summary>
        private void OnGUI()
        {
            // ⚠️ 2026-09-26：这个窗口原来是一整列、**没有滚动**的 —— 节一多（状态/房间/世界/掉落/输入/
            //    任务条件/日志），下半截就被裁掉且**翻不到**（负责人："全屏也看不到任务完成情况"）。
            //    现在分两层：
            //      ① **顶部常驻摘要行**（在滚动视图外面）—— 最要紧的几个数不用滚就能看到；
            //      ② 其余内容统一放进**整窗滚动视图** —— 滚轮 / 拖拽都能翻到底。
            DrawSummaryLine();

            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("服务端", EditorStyles.boldLabel);

            m_host = EditorGUILayout.TextField("地址", m_host);
            m_port = EditorGUILayout.IntField("端口", m_port);
            m_playerName = EditorGUILayout.TextField("玩家名", m_playerName);

            EditorGUILayout.Space();

            bool busy = m_session != null
                        && (m_session.State == ESessionState.Connecting
                            || m_session.State == ESessionState.Handshaking
                            || m_session.State == ESessionState.Online);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(busy))
                {
                    if (GUILayout.Button("连接", GUILayout.Height(24f)))
                    {
                        ConnectSession();
                    }
                }

                using (new EditorGUI.DisabledScope(!busy))
                {
                    if (GUILayout.Button("断开", GUILayout.Height(24f)))
                    {
                        CloseSession("手动断开");
                    }

                    if (GUILayout.Button("发一次心跳", GUILayout.Height(24f)))
                    {
                        m_session.SendPingNow();
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("房间", EditorStyles.boldLabel);

            m_dungeonId = EditorGUILayout.IntField("副本编号", m_dungeonId);
            m_roomId = EditorGUILayout.TextField("房号（留空 = 服务端安排）", m_roomId);

            bool online = m_session != null && m_session.IsOnline;
            bool inRoom = m_session != null && m_session.InRoom;

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!online || inRoom))
                {
                    if (GUILayout.Button("加入房间", GUILayout.Height(22f)))
                    {
                        m_session.JoinRoom(m_roomId, m_dungeonId);
                    }
                }

                using (new EditorGUI.DisabledScope(!inRoom))
                {
                    if (GUILayout.Button("离开房间", GUILayout.Height(22f)))
                    {
                        m_session.LeaveRoom();
                    }
                }
            }

            EditorGUILayout.Space();
            DrawStatus();
            DrawQuestLoop();
            EditorGUILayout.Space();
            DrawLog();

            if (m_session == null)
            {
                EditorGUILayout.HelpBox(
                    "还没连接。先起服务端，再点「连接」：\n\n" +
                    "Server\\NBC.Server.Host\\bin\\Debug\\net8.0\\NBC.Server.Host.exe --port=" + m_port + "\n\n" +
                    "（或在仓库根目录跑 dotnet build Server\\NBC.sln -m:1 之后用上面这个 exe）",
                    MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 顶部**常驻摘要行**：不滚动也能看到的那几个数（会话 / 房间 / 世界 / **任务进度** / 事件计数 / 输入）。
        /// <para>为什么要它：整窗滚动解决了"看不到"，但**最要紧的数仍要滚一下才能看到**；
        /// 而调试时你反复要看的恰恰就是"任务涨没涨、事件有没有来"。所以把它钉在顶上。</para>
        /// </summary>
        private void DrawSummaryLine()
        {
            if (m_session == null)
            {
                EditorGUILayout.LabelField("会话：未连接（点下面的「连接」）", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space();
                return;
            }

            SnapshotView world = m_session.World;

            EditorGUILayout.LabelField(
                "会话：" + m_session.State
                + "（玩家 " + m_session.PlayerId
                + "，RTT " + (m_session.RttMs < 0 ? "?" : m_session.RttMs + "ms") + "）"
                + "　房间：" + (m_session.InRoom
                    ? m_session.CurrentRoom.RoomId + "（" + m_session.CurrentRoom.Members.Count
                      + "/" + m_session.CurrentRoom.Capacity + "）"
                    : "无")
                + "　世界：" + world.AliveCount + "/" + world.EntityCount
                + " 活着（帧 " + world.ServerTick + "）",
                EditorStyles.wordWrappedMiniLabel);

            string quest = "任务：（未开始 —— 先连接）";
            ConditionProgress progress;

            if (m_conditions != null && m_conditions.TryGetProgress(DemoConditionKey, out progress))
            {
                quest = "任务：" + progress + (progress.IsMet ? " ✅ 已达成" : "（还差 " + progress.Remaining + " 只）")
                    + "，达成回调 " + m_demoConditionMetCount + " 次";
            }

            EditorGUILayout.LabelField(
                quest
                + "　服务端事件：伤害 " + m_session.HitsReceived
                + "、死亡 " + m_session.DeathsReceived
                + "、掉落 " + m_session.DropsReceived
                + "　输入已发 " + m_session.InputsSent,
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space();
        }

        /// <summary>画状态区。</summary>
        private void DrawStatus()
        {
            EditorGUILayout.LabelField("状态", EditorStyles.boldLabel);

            if (m_session == null)
            {
                EditorGUILayout.LabelField("会话", "（未创建）");
                return;
            }

            EditorGUILayout.LabelField("状态", m_session.State.ToString());
            EditorGUILayout.LabelField("人话", m_session.Description);
            EditorGUILayout.LabelField("玩家 id", m_session.PlayerId.ToString());
            EditorGUILayout.LabelField("RTT", m_session.RttMs < 0 ? "（还没测到）" : m_session.RttMs + " ms");
            EditorGUILayout.LabelField("心跳", "已发 " + m_session.PingsSent + "，已收 " + m_session.PongsReceived);

            if (m_session.Ack != null)
            {
                EditorGUILayout.LabelField("服务端", m_session.Ack.ServerVersion
                    + "（协议 v" + m_session.Ack.ProtocolVersion + "，tick " + m_session.Ack.TickHz + "Hz）");
            }

            NBC.Framework.Net.TransportStats stats = m_session.TransportStats;
            EditorGUILayout.LabelField("传输", stats.ToString());

            if (!string.IsNullOrEmpty(m_session.FailureReason))
            {
                EditorGUILayout.HelpBox(m_session.FailureReason, MessageType.Error);
            }

            DrawRoom();
        }

        /// <summary>画房间区（席位表 + 最近一次被拒）。</summary>
        private void DrawRoom()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("房间与席位", EditorStyles.boldLabel);
            NBC.Protocol.RoomState room = m_session.CurrentRoom;

            if (room == null || string.IsNullOrEmpty(room.RoomId))
            {
                EditorGUILayout.LabelField("位置", "不在任何房间");
            }
            else
            {
                EditorGUILayout.LabelField("房号", room.RoomId);
                EditorGUILayout.LabelField("副本", room.DungeonId.ToString());
                EditorGUILayout.LabelField("席位", room.Members.Count + " / " + room.Capacity);
                EditorGUILayout.LabelField("阶段", room.Phase.ToString());

                for (int i = 0; i < room.Members.Count; i++)
                {
                    NBC.Protocol.RoomMember member = room.Members[i];

                    EditorGUILayout.LabelField(
                        "  · 玩家 " + member.PlayerId,
                        member.PlayerName + (member.IsHost ? "（房主）" : string.Empty)
                        + (member.Ready ? "（已准备）" : string.Empty));
                }
            }

            if (m_session.LastError != null)
            {
                // 被拒 ≠ 掉线：这里用 Warning 而不是 Error，提示"这个请求没成"
                EditorGUILayout.HelpBox(
                    "上次请求被拒（" + m_session.LastError.Code + "）：" + m_session.LastError.Message,
                    MessageType.Warning);
            }

            DrawWorld();
            DrawDrops();
            DrawInput();
        }

        /// <summary>
        /// 画掉落区（S7/S9）：**客户端只负责显示** —— 掷骰全在服务端。
        /// <para>⚠️ 狼的两条掉落概率是 50% / 30% ⇒ **"什么都没掉"有 35% 的概率**。
        /// 所以"打死了没看到奖励"有两种可能：真没掉，或者没打中那 65%。BOSS 是 10000（必掉）。</para>
        /// </summary>
        private void DrawDrops()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("掉落（服务端掷的）", EditorStyles.boldLabel);

            if (m_session == null)
            {
                EditorGUILayout.LabelField("累计", "0");
                return;
            }

            EditorGUILayout.LabelField("累计收到", m_session.DropsReceived + " 条");

            if (m_session.LastDrop != null)
            {
                EditorGUILayout.LabelField("最近一次",
                    "物品 " + m_session.LastDrop.ItemId + " × " + m_session.LastDrop.Count
                    + "（归玩家 " + m_session.LastDrop.WinnerPlayerId + "）");
            }
            else
            {
                EditorGUILayout.LabelField("最近一次", "（还没掉过）");
            }

            IReadOnlyList<NBC.Protocol.DropEvent> recent = m_session.RecentDrops;

            for (int i = 0; i < recent.Count; i++)
            {
                EditorGUILayout.LabelField(
                    "  · " + (i == 0 ? "最新" : i.ToString()),
                    "物品 " + recent[i].ItemId + " × " + recent[i].Count
                    + " → 玩家 " + recent[i].WinnerPlayerId);
            }

            if (m_session.DropsReceived == 0 && m_session.InRoom)
            {
                EditorGUILayout.HelpBox(
                    "还没掉过东西。注意：狼的掉落概率是 50% / 30% —— 「什么都没掉」有 35% 的概率；\n" +
                    "BOSS（狼王 6003）两条都是 10000（必掉），打它可以稳定看到掉落。",
                    MessageType.Info);
            }
        }

        /// <summary>画输入区（S5b：客户端只发**意图**，能走多远、打不打得到由服务端说了算）。</summary>
        private void DrawInput()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("输入（只发意图）", EditorStyles.boldLabel);

            bool canInput = m_session != null && m_session.IsOnline && m_session.InRoom;

            using (new EditorGUI.DisabledScope(!canInput))
            {
                // ---- M4-S1b：自动追击（负责人实测"目标不在范围内，难测"） ----
                m_autoChase = EditorGUILayout.Toggle("自动追击并攻击（调试用）", m_autoChase);

                if (m_autoChase)
                {
                    EditorGUILayout.HelpBox(
                        "会自动朝目标走过去（按服务端算的切比雪夫距离判断射程），进了 2000mm 就按一下普攻、"
                        + "再等冷却（15 帧）。目标死了会自动换最近的活怪。\n"
                        + "⚠️ 它**只是替你按键**：位置、伤害、掉落照样全由服务端算 —— 这是调试工具，不是客户端预测。",
                        MessageType.None);
                }

                EditorGUILayout.Space();
                m_inputX = EditorGUILayout.IntSlider("左右（-1000..1000）", m_inputX, -1000, 1000);
                m_inputY = EditorGUILayout.IntSlider("前后（-1000..1000）", m_inputY, -1000, 1000);
                m_inputContinuous = EditorGUILayout.Toggle("持续发送（模拟一直按着）", m_inputContinuous);
                m_targetEntityId = EditorGUILayout.IntField("目标实例编号（0 = 不打）", m_targetEntityId);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("发一次输入", GUILayout.Height(22f)))
                    {
                        m_session.SendInput(m_inputX, m_inputY, 0u, 0u, m_targetEntityId);
                    }

                    if (GUILayout.Button("普攻目标（第 0 位）", GUILayout.Height(22f)))
                    {
                        m_session.SendInput(m_inputX, m_inputY, 1u, 0u, m_targetEntityId);
                    }

                    if (GUILayout.Button("选最近的活怪当目标", GUILayout.Height(22f)))
                    {
                        m_targetEntityId = NearestAliveMonsterId();
                    }
                }

                DrawTargetHint();
            }

            if (!canInput)
            {
                EditorGUILayout.LabelField("提示", "要先「连接」并「加入房间」才能发输入");
            }

            EditorGUILayout.LabelField("已发输入", m_session == null ? "0" : m_session.InputsSent.ToString());

            if (m_autoChase && canInput)
            {
                EditorGUILayout.LabelField("自动追击", "已按 " + m_autoAttackCount
                    + " 次普攻；冷却剩余 " + m_autoAttackCooldownTicks + " 帧");
            }
        }

        /// <summary>
        /// 把"目标在不在射程内"直接写出来（原来只有一个目标编号，得自己去算，
        /// 于是现象是"打了没反应"，而且不报错 —— 这正是负责人说的"目标不在范围内，难测"）。
        /// </summary>
        private void DrawTargetHint()
        {
            if (m_session == null || m_targetEntityId == 0)
            {
                return;
            }

            SnapshotView world = m_session.World;
            NBC.Protocol.EntitySnapshot mine = world.FindHero(m_session.PlayerId);
            NBC.Protocol.EntitySnapshot target = FindEntity(world, m_targetEntityId);

            if (mine == null || target == null)
            {
                EditorGUILayout.LabelField("目标", "快照里还没看到" + (mine == null ? "你自己" : "目标")
                    + "（等一张快照 / 先确认自己活着）");
                return;
            }

            int distance = Chebyshev(mine, target);
            bool inRange = distance <= BattleRules.BasicAttackRangeMm;

            EditorGUILayout.LabelField("距离", distance + " mm"
                + (inRange ? "　✅ 在射程内（" + BattleRules.BasicAttackRangeMm + "mm）" : "　❌ 太远，打不到"));

            EditorGUILayout.LabelField("我 / 目标",
                "我 (" + mine.PosXMm + ", " + mine.PosZMm + ") → 目标 (" + target.PosXMm + ", " + target.PosZMm + ")"
                + "，目标 HP " + target.Hp + "/" + target.MaxHp);
        }

        /// <summary>
        /// 从本地世界副本里挑**离我最近的**活怪当目标（省得手填编号）。
        /// <para>⚠️ 原来叫 `FirstAliveMonsterId`：它取"快照里第一只活怪"，而快照顺序是**创建顺序**
        /// ⇒ 常常给你指到地图另一头的那只（负责人就撞上了"目标不在范围内，难测"）。</para>
        /// </summary>
        /// <returns>实例编号；没有就返回 0。</returns>
        private int NearestAliveMonsterId()
        {
            if (m_session == null)
            {
                return 0;
            }

            SnapshotView world = m_session.World;
            NBC.Protocol.EntitySnapshot mine = world.FindHero(m_session.PlayerId);

            int bestId = 0;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < world.Entities.Count; i++)
            {
                NBC.Protocol.EntitySnapshot e = world.Entities[i];

                if (e.Kind != 1 || !e.Alive)
                {
                    continue;
                }

                // 找不到自己的英雄时（快照还没到）退化成"第一只活怪"
                int distance = mine == null ? 0 : Chebyshev(mine, e);

                if (bestId == 0 || distance < bestDistance)
                {
                    bestId = e.EntityId;
                    bestDistance = distance;
                }
            }

            return bestId;
        }

        /// <summary>按实例编号找单位（找不到返回 null）。</summary>
        /// <param name="world">本地世界副本。</param>
        /// <param name="entityId">实例编号。</param>
        /// <returns>单位或 null。</returns>
        private static NBC.Protocol.EntitySnapshot FindEntity(SnapshotView world, int entityId)
        {
            if (world == null || entityId == 0)
            {
                return null;
            }

            for (int i = 0; i < world.Entities.Count; i++)
            {
                if (world.Entities[i].EntityId == entityId)
                {
                    return world.Entities[i];
                }
            }

            return null;
        }

        /// <summary>切比雪夫距离（毫米）——**与服务端 `DungeonBattle.DistanceMm` 同一种量法**。</summary>
        /// <param name="a">甲。</param>
        /// <param name="b">乙。</param>
        /// <returns>距离。</returns>
        private static int Chebyshev(NBC.Protocol.EntitySnapshot a, NBC.Protocol.EntitySnapshot b)
        {
            int dx = Math.Abs(a.PosXMm - b.PosXMm);
            int dz = Math.Abs(a.PosZMm - b.PosZMm);
            return dx > dz ? dx : dz;
        }

        /// <summary>画世界区（服务端快照的本地副本 —— M3 客户端的"画面数据"全在这儿）。</summary>
        private void DrawWorld()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("世界（服务端快照）", EditorStyles.boldLabel);

            SnapshotView world = m_session.World;

            EditorGUILayout.LabelField("服务端帧", world.ServerTick < 0 ? "（还没收到）" : world.ServerTick.ToString());
            EditorGUILayout.LabelField("单位", world.AliveCount + " / " + world.EntityCount + " 个活着");
            EditorGUILayout.LabelField("快照", "采纳 " + world.Applied + "，丢弃旧快照 " + world.StaleIgnored);

            for (int i = 0; i < world.Entities.Count; i++)
            {
                NBC.Protocol.EntitySnapshot e = world.Entities[i];

                EditorGUILayout.LabelField(
                    "  · 实例 " + e.EntityId + "（配置 " + e.ConfigId + "）",
                    (e.Alive ? "HP " + e.Hp + "/" + e.MaxHp : "已死亡")
                    + "　位置 (" + e.PosXMm + ", " + e.PosZMm + ") mm　朝向 " + e.FacingDeg + "°");
            }
        }

        /// <summary>
        /// 画 M4-S1 的任务条件区：**联机打死野狼，这里的进度会涨**。
        /// <para>这条链是：服务端 `DeathEvent` → `NetSession` → `ServerEventBridge` →
        /// `EventCenter` → `ConditionEventBridge` → `ConditionTracker`。
        /// 条件的定义是**造出来的**（不是配置表读的）—— 目的是让"接线对不对"肉眼可见，
        /// 不用先跑通整套配置表（真任务走的是同一个 `ConditionTracker`，见 `QuestRuntime`）。</para>
        /// </summary>
        private void DrawQuestLoop()
        {
            if (m_session == null)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("任务条件（M4-S1：联机也能涨）", EditorStyles.boldLabel);

            EditorGUILayout.LabelField("条件", "击杀野狼 " + DemoMonsterId + " × " + DemoKillCount
                + "（这条是**演示用**的，真任务从 `QuestCondition` 表读）");

            if (m_conditions == null)
            {
                EditorGUILayout.LabelField("进度", "（还没开始 —— 先点「连接」）");
                return;
            }

            ConditionProgress progress;

            if (m_conditions.TryGetProgress(DemoConditionKey, out progress))
            {
                EditorGUILayout.LabelField("进度", progress.ToString()
                    + (progress.IsMet ? "　✅ 已达成" : "　还差 " + progress.Remaining + " 只"));
            }

            EditorGUILayout.LabelField("达成回调", "触发过 " + m_demoConditionMetCount + " 次");

            EditorGUILayout.LabelField("事件中心监听者",
                "MonsterDied = " + EventCenter.Instance.GetListenerCount(BattleEvents.MonsterDied)
                + "，DamageDealt = " + EventCenter.Instance.GetListenerCount(BattleEvents.DamageDealt));

            if (m_eventBridge != null)
            {
                EditorGUILayout.LabelField("桥收到",
                    "伤害 " + m_eventBridge.HitsForwarded
                    + "，怪死 " + m_eventBridge.MonstersDied
                    + "，英雄死 " + m_eventBridge.HeroesDied
                    + "，我的掉落 " + m_eventBridge.DropsClaimed
                    + "，别人的掉落 " + m_eventBridge.DropsOfOthers);
            }

            EditorGUILayout.LabelField("网络层收到",
                "伤害 " + m_session.HitsReceived
                + "，死亡 " + m_session.DeathsReceived
                + "，掉落 " + m_session.DropsReceived);

            if (GUILayout.Button("重置演示条件进度", GUILayout.Height(22f)))
            {
                m_conditions.Unregister(DemoConditionKey);
                m_conditions.Register(DemoConditionKey,
                    new ConditionDef(EConditionEvent.KillMonster, DemoMonsterId, DemoKillCount), true);
                m_demoConditionMetCount = 0;
                AddLog("演示条件进度已重置");
            }

            if (!m_session.InRoom)
            {
                EditorGUILayout.HelpBox(
                    "还没进房。进房后打死的怪才会变成事件 —— 这就是 M3 收关时那条「任务闭环断口」，"
                    + "现在由 `ServerEventBridge` 接上了。",
                    MessageType.Info);
            }
        }

        /// <summary>画日志区（**固定高度的内层滚动**，见 `OnGUI` 里"整窗滚动"的说明）。</summary>
        private void DrawLog()
        {
            EditorGUILayout.LabelField("日志（最近 " + MaxLogLines + " 条；框内可滚轮）", EditorStyles.boldLabel);

            // ⚠️ 原来是 `GUILayout.ExpandHeight(true)` —— 放进**外层滚动视图**之后那个写法会失去意义
            //    （外层高度是无界的）⇒ 日志框塌成 0 高、一条都看不见。改成固定高度：
            //    外层管"整窗翻页"、内层管"日志自己翻"，各管一段，互不打架。
            m_logScroll = EditorGUILayout.BeginScrollView(m_logScroll, GUILayout.Height(LogBoxHeight));

            for (int i = 0; i < m_log.Count; i++)
            {
                EditorGUILayout.LabelField(m_log[i], EditorStyles.miniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>连上去：造传输 + 造会话 + 挂事件。</summary>
        private void ConnectSession()
        {
            CloseSession("重连前先断开");

            m_transport = new TcpTransport();
            m_session = new NetSession(m_transport, m_playerName, "unity-editor/" + Application.unityVersion);

            m_session.Note += line => AddLog(line);
            m_session.StateChanged += state => AddLog("状态 → " + state);
            m_session.HandshakeCompleted += ack =>
                AddLog("握手完成：玩家 " + ack.PlayerId + "，服务端 " + ack.ServerVersion);
            m_session.RttUpdated += rtt => AddLog("RTT " + rtt + "ms");

            m_lastUpdateTime = EditorApplication.timeSinceStartup;
            AddLog("=== 连接 " + m_host + ":" + m_port + "（玩家名 " + m_playerName + "）===");

            m_session.Connect(m_host, m_port);

            StartQuestLoopDemo();
        }

        /// <summary>
        /// M4-S1：接上"联机战斗 → 任务条件"这条链。
        /// <para>⚠️ 条件系统与它的桥**只建一次**（它们挂在全局 `EventCenter` 上）——
        /// 每次连接都建一份的话，同一个事件会被喂进条件系统两次，进度翻倍且看不出来。
        /// 每次连接只重建"跟当前会话绑定"的那座 `ServerEventBridge`。</para>
        /// </summary>
        private void StartQuestLoopDemo()
        {
            if (m_conditions == null)
            {
                m_conditions = new ConditionTracker(new InMemoryConditionProgressStore());

                m_conditionBridge = new ConditionEventBridge(m_conditions);
                m_conditionBridge.Bind<MonsterDiedPayload>(BattleEvents.MonsterDied,
                    EConditionEvent.KillMonster, payload => payload.MonsterConfigId);

                m_conditions.ConditionMet += (key, progress) =>
                {
                    m_demoConditionMetCount++;
                    AddLog("条件达成：" + progress + "（条件编号 " + key + "）");
                };

                m_conditions.Register(DemoConditionKey,
                    new ConditionDef(EConditionEvent.KillMonster, DemoMonsterId, DemoKillCount), true);
            }

            m_eventBridge = new ServerEventBridge(m_session);
        }

        /// <summary>断开并释放。</summary>
        /// <param name="reason">原因（写进日志）。</param>
        private void CloseSession(string reason)
        {
            if (m_session == null)
            {
                return;
            }

            AddLog("=== " + reason + " ===");

            // 先退订桥（它订阅的是这个会话的事件），再 Dispose 会话
            if (m_eventBridge != null)
            {
                m_eventBridge.Dispose();
                m_eventBridge = null;
            }

            m_session.Dispose();
            m_session = null;
            m_transport = null;
        }

        /// <summary>加一行日志（超出上限就丢最旧的）。</summary>
        /// <param name="line">内容。</param>
        private void AddLog(string line)
        {
            m_log.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line);

            while (m_log.Count > MaxLogLines)
            {
                m_log.RemoveAt(0);
            }
        }
    }
}
