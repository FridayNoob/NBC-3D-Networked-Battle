// ============================================================================
//  ProgressBroadcaster —— 把**权威进度**推给对应的那个玩家（M4-S3 收口）
//  项目：3D联网战斗Demo
//  对应：`Docs\25` D3（服务器权威）、`Docs\27` §21.4 未做#2
//
//  ---------------------------------------------------------------------------
//  一、它解决什么（一句话）
//  ---------------------------------------------------------------------------
//  在此之前任务/成就进度**客户端自己也算一份**（= 预测）。两边数值一致，但**仍是两个账房** ——
//  只要配置、时序、或"哪个事件喂了几次"有半点不同，就会出现
//  「客户端显示已完成、服务端说没达成」，而且**不报错**。
//  这一层就是"谁是权威"的那条线：**服务端说了算，客户端只显示**。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 为什么单独一个类（而不是写在 `Program.cs` 里）
//  ---------------------------------------------------------------------------
//  这段逻辑有**两个调用点**：服务端进程（`NBC.Server.Host`）与端到端探针（`Server\_net-probe`）。
//  本项目的老规矩：**一处规则、两个实现 = 迟早对不上**（M3-A 的 `ServerMessagePump` 就是这么来的；
//  `Docs\27` §12.3 3h 也栽过"探针与主循环推帧顺序不一致"）。
//  如果探针里再抄一份"建消息 → 找会话 → 发"，那探针验的是**抄本**，
//  而真正跑在生产里的那份没人验。⇒ 逻辑写在这里，两个调用点都用它。
//
//  ---------------------------------------------------------------------------
//  三、三条**刻意的**决定
//  ---------------------------------------------------------------------------
//  ① **同步 send**（只入 transport 的发送队列，不等 IO）：
//     `ProgressChanged` 是在 Tick 线程上触发的，那里**绝不能等网络**。
//  ② **找不到会话就安静跳过**：玩家可能刚登录还没进房 —— 那不是错误。
//     他进房后该有的都会有，而进度会在**下一次变化**时再推。
//  ③ **推全量**（不是增量）：与 D5（每 tick 全量快照）同一个判据 ——
//     增量一旦漏一条就会永久少一格，而且**不报错**。
// ============================================================================

using System;
using Google.Protobuf;          // `ToByteArray()`
using NBC.Protocol;
using NBC.Server.Core;

namespace NBC.Server.Game
{
    /// <summary>把权威进度推给玩家自己的那条线（Host 与探针共用同一份）。</summary>
    public sealed class ProgressBroadcaster : IDisposable
    {
        /// <summary>传输（发包用）。</summary>
        private readonly INetTransport m_transport;

        /// <summary>房间表（按玩家找会话用）。</summary>
        private readonly RoomRegistry m_registry;

        /// <summary>权威判定器（进度内容的来源）。</summary>
        private readonly AchievementAuthority m_authority;

        /// <summary>Dispose 过没有。</summary>
        private bool m_disposed;

        /// <summary>造一条推送线（**构造即接通**：开始订阅 `ProgressChanged`）。</summary>
        /// <param name="transport">传输（不能为 null）。</param>
        /// <param name="registry">房间表（不能为 null）。</param>
        /// <param name="authority">权威判定器（不能为 null）。</param>
        public ProgressBroadcaster(INetTransport transport, RoomRegistry registry, AchievementAuthority authority)
        {
            if (transport == null) { throw new ArgumentNullException(nameof(transport), "[ProgressBroadcaster] 传输是 null。"); }
            if (registry == null) { throw new ArgumentNullException(nameof(registry), "[ProgressBroadcaster] 房间表是 null。"); }
            if (authority == null) { throw new ArgumentNullException(nameof(authority), "[ProgressBroadcaster] 权威判定器是 null。"); }

            m_transport = transport;
            m_registry = registry;
            m_authority = authority;

            m_authority.ProgressChanged += OnProgressChanged;
        }

        /// <summary>值得记一句的事情（发出去了、跳过了、失败了）。</summary>
        public event Action<string>? Note;

        /// <summary>累计发出去几份权威进度。</summary>
        public long Sent { get; private set; }

        /// <summary>累计因为"找不到会话"跳过几次（**不是错误**，见文件头 ②）。</summary>
        public long SkippedNoSession { get; private set; }

        /// <summary>退订（可以重复调用）。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;
            m_authority.ProgressChanged -= OnProgressChanged;
        }

        /// <summary>一句人话（启动统计行/排查用）。</summary>
        /// <returns>例：`权威进度：发 3、无会话跳过 1`。</returns>
        public string Describe()
        {
            return "权威进度：发 " + Sent + "、无会话跳过 " + SkippedNoSession;
        }

        /// <summary>进度变了 ⇒ 推给那个玩家。</summary>
        /// <param name="playerId">玩家编号。</param>
        private void OnProgressChanged(long playerId)
        {
            if (m_disposed)
            {
                return;
            }

            ClientSession? session = FindSessionOf(playerId);

            if (session == null)
            {
                SkippedNoSession++;
                return;
            }

            ProgressSync view;

            // `serverTick` 用"推进过多少逻辑帧"：只是为了排查"这条是什么时候的"，
            // 不参与任何判定（进度内容本身就是全量）。
            if (!m_authority.TryBuildProgressView(playerId, 0, out view))
            {
                return;
            }

            if (m_transport.Send(session.SessionId, new ServerMessage { ProgressSync = view }.ToByteArray()))
            {
                Sent++;
                Note?.Invoke("玩家 " + playerId + " ← 权威进度 " + view.Entries.Count + " 条");
            }
        }

        /// <summary>
        /// 按玩家编号找他的会话。
        /// <para>⚠️ 走**房间**找人：本项目"一个席位一个会话"（`Room` 里 席位↔会话 是一一对应的），
        /// 所以没有另外维护一张 `playerId → session` 的表 —— 少一张表就少一处会不同步的地方。</para>
        /// </summary>
        /// <param name="playerId">玩家编号。</param>
        /// <returns>会话；不在任何房间时返回 null。</returns>
        private ClientSession? FindSessionOf(long playerId)
        {
            foreach (Room room in m_registry.Rooms)
            {
                RoomSeat? seat = room.FindByPlayerId(playerId);

                if (seat != null)
                {
                    return seat.Session;
                }
            }

            return null;
        }
    }
}
