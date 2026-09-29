// ============================================================================
//  QuestStateBroadcaster —— 把**任务状态**推给对应的那个玩家（M4-S3 §二十六）
//  项目：3D联网战斗Demo
//
//  ---------------------------------------------------------------------------
//  一、为什么单独一个类（而不是写在 `Program.cs` 或探针里）
//  ---------------------------------------------------------------------------
//  与 `ProgressBroadcaster` / `ServerMessagePump` 同一条理由：
//  这段逻辑有**两个调用点**（服务端进程 `NBC.Server.Host` 与端到端探针 `Server\_net-probe`）。
//  本项目的老规矩：**一处规则、两个实现 = 迟早对不上**。
//  抄一份进探针，验的就是**抄本**，而真正跑在生产里的那份没人验。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 它是"成功回包"的**唯一**通路（这一点是刻意的）
//  ---------------------------------------------------------------------------
//  `QuestService` 处理成功时**不自己回包**，只返回 `Handled` —— 因为
//  `QuestAuthority.Accept/Submit` 成功时会在**同一个调用栈里同步**喊 `StateChanged`，
//  于是本类**当场**就把全量状态发了出去。
//
//  好处是**只有一条发送路径**：
//      · 玩家点"接取"        → 权威喊 → 这里发
//      · 条件在战斗里打满了  → 权威喊 → 这里发（**异步**变化也走同一条路）
//  写成"成功回包 + 另外推送"就会**发两遍**，而且两遍的内容来自两次读取
//  （万一同一个 tick 里又变了，客户端会先收到新的、再收到旧的 —— 顺序反了）。
//
//  ⚠️ 代价：**没有接这个广播器时，成功就没有回包**。所以 `QuestService` 在
//     "没接广播器"时会**自己回一条**（否则探针/裁剪部署会表现成"点了没反应"）。
//
//  ---------------------------------------------------------------------------
//  三、三条刻意的决定（与 `ProgressBroadcaster` 逐条对应）
//  ---------------------------------------------------------------------------
//  ① **同步 send**（只入 transport 的发送队列，不等 IO）：`StateChanged` 可能在
//     网络泵或 Tick 线程上触发，那里**绝不能等网络**。
//  ② **找不到会话就安静跳过**：玩家可能刚登录还没进房 —— 那不是错误。
//  ③ **推全量**（不是增量）：与 D5 同一个判据。
// ============================================================================

using System;
using Google.Protobuf;          // `ToByteArray()`
using NBC.Protocol;
using NBC.Server.Core;

namespace NBC.Server.Game
{
    /// <summary>把任务状态推给玩家自己的那条线（Host 与探针共用同一份）。</summary>
    public sealed class QuestStateBroadcaster : IDisposable
    {
        /// <summary>传输（发包用）。</summary>
        private readonly INetTransport m_transport;

        /// <summary>房间表（按玩家找会话用）。</summary>
        private readonly RoomRegistry m_registry;

        /// <summary>任务权威（状态的来源）。</summary>
        private readonly QuestAuthority m_authority;

        /// <summary>Dispose 过没有。</summary>
        private bool m_disposed;

        /// <summary>造一条推送线（**构造即接通**：开始订阅 `StateChanged`）。</summary>
        /// <param name="transport">传输（不能为 null）。</param>
        /// <param name="registry">房间表（不能为 null）。</param>
        /// <param name="authority">任务权威（不能为 null）。</param>
        public QuestStateBroadcaster(INetTransport transport, RoomRegistry registry, QuestAuthority authority)
        {
            if (transport == null) { throw new ArgumentNullException(nameof(transport), "[QuestStateBroadcaster] 传输是 null。"); }
            if (registry == null) { throw new ArgumentNullException(nameof(registry), "[QuestStateBroadcaster] 房间表是 null。"); }
            if (authority == null) { throw new ArgumentNullException(nameof(authority), "[QuestStateBroadcaster] 任务权威是 null。"); }

            m_transport = transport;
            m_registry = registry;
            m_authority = authority;

            m_authority.StateChanged += OnStateChanged;
        }

        /// <summary>值得记一句的事情（发出去了 / 跳过了 / 失败了）。</summary>
        public event Action<string>? Note;

        /// <summary>累计发出去几份任务状态。</summary>
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
            m_authority.StateChanged -= OnStateChanged;
        }

        /// <summary>一句人话（启动统计行/排查用）。</summary>
        /// <returns>例：`任务状态推送：发 2、无会话跳过 1`。</returns>
        public string Describe()
        {
            return "任务状态推送：发 " + Sent + "、无会话跳过 " + SkippedNoSession;
        }

        /// <summary>状态变了 ⇒ 把**全量**状态发给那个玩家。</summary>
        /// <param name="playerId">玩家编号。</param>
        private void OnStateChanged(long playerId)
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

            QuestStateSync view;

            // `serverTick` 恒为 0：只是为了排查"这条是什么时候的"，不参与任何判定
            // （状态内容本身就是全量）。要真给帧号得把 Tick 序号传进来，
            // 那会让权威认识主循环 —— 不值得（见文件头"分层"）。
            if (!m_authority.TryBuildQuestStateView(playerId, 0, out view))
            {
                return;
            }

            if (m_transport.Send(session.SessionId, new ServerMessage { QuestState = view }.ToByteArray()))
            {
                Sent++;
                Note?.Invoke("玩家 " + playerId + " ← 任务状态 " + view.Entries.Count + " 条");
            }
        }

        /// <summary>
        /// 按玩家编号找他的会话（走**房间**找人：本项目"一个席位一个会话"，没有另外维护
        /// 一张 `playerId → session` 的表 —— 少一张表就少一处会不同步的地方）。
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
