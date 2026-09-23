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
using NBC.Framework.Net.Adapter;
using NBC.Game.Net;
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

        /// <summary>会话（没连时为 null）。</summary>
        private NetSession m_session;

        /// <summary>传输（图省事由窗口自己持有，方便 Dispose）。</summary>
        private TcpTransport m_transport;

        /// <summary>界面上的日志（新的在下面）。</summary>
        private readonly List<string> m_log = new List<string>();

        /// <summary>上次推进的时刻（算 delta 用）。</summary>
        private double m_lastUpdateTime;

        /// <summary>日志滚动位置。</summary>
        private Vector2 m_logScroll;

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
            Repaint();
        }

        /// <summary>画界面。</summary>
        private void OnGUI()
        {
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

        /// <summary>画日志区。</summary>
        private void DrawLog()
        {
            EditorGUILayout.LabelField("日志（最近 " + MaxLogLines + " 条）", EditorStyles.boldLabel);

            m_logScroll = EditorGUILayout.BeginScrollView(m_logScroll, GUILayout.ExpandHeight(true));

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
