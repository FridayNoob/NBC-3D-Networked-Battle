// ============================================================================
//  QuestService —— 任务动作的**请求处理器**：接取 / 交付（M4-S3 §二十六）
//  项目：3D联网战斗Demo
//
//  ---------------------------------------------------------------------------
//  一、它负责什么（三件事，别扩散）
//  ---------------------------------------------------------------------------
//      · **认身份**：只有登录玩家（`player_id > 0`）能接取/交付
//      · **转给状态机**：`QuestAuthority.Accept` / `Submit`
//      · **把结果说清楚**：失败回 `ErrorResponse`（**带原因原文**），成功由推送回状态
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 游客为什么**必须明确拒绝**（不是静默丢弃、更不是当成功）
//  ---------------------------------------------------------------------------
//  游客（`player_id < 0`）**没有玩家档案**，而 `quest_state.player_id` 有外键指向
//  `player_profile` ⇒ 他的任务状态**无处可挂**（写库会被外键拒掉）。
//
//  三条路都不能走：
//      ❌ **静默丢弃**：玩家点了"接取"却什么都没发生，而且**不报错** —— 最难查的一类问题
//      ❌ **当成功**：客户端会把状态画成"已接取"，而服务端什么都没有
//      ❌ **只记日志**：日志是给**我们**看的，玩家看不到 ⇒ 表现同上
//  ✅ 所以：**回一条说得清的错误**，让他知道"要先用账号登录"。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ "没接权威"是**另一个**错误（别和游客混）
//  ---------------------------------------------------------------------------
//      `QuestUnavailable`（没接数据库） = **服务端配置问题** ⇒ 该修服务端
//      `PlayerMismatch`（游客）          = **身份问题**       ⇒ 该去登录
//  混成一个码，排查时就会走错方向。这是本项目"让错误自己说出来"的具体化。
//
//  ---------------------------------------------------------------------------
//  四、⚠️ 成功时**不自己回包**（回 `Handled`）
//  ---------------------------------------------------------------------------
//  理由写在 `QuestStateBroadcaster` 文件头第二节：权威会在**同一个调用栈里同步**
//  喊 `StateChanged`，广播器当场就把全量状态发了 ⇒ 若这里再回一条，玩家会**收到两遍**。
//
//  ⚠️ **例外**：没接广播器时（探针裁剪部署），成功就**没有回包**了 ——
//     那会表现成"点了没反应"。所以这里在 `m_broadcaster == null` 时**自己回一条**。
// ============================================================================

using System;
using NBC.Protocol;             // `ClientMessage` / `ServerMessage` / `ErrorResponse`
using NBC.Server.Core;          // `ClientSession` / `ServerMessageRouter` / `DispatchResult`
using NBC.Shared.Net;           // `NetErrors`

namespace NBC.Server.Game
{
    /// <summary>任务动作（接取 / 交付）的请求处理器。</summary>
    public sealed class QuestService
    {
        /// <summary>`QuestActionRequest.action` = **接取**。</summary>
        public const int ActionAccept = 1;

        /// <summary>`QuestActionRequest.action` = **交付**。</summary>
        public const int ActionSubmit = 2;

        /// <summary>任务权威（null = 服务端没接数据库）。</summary>
        private readonly QuestAuthority? m_authority;

        /// <summary>状态推送线（null = 没接；成功时要自己回一条，见文件头四）。</summary>
        private readonly QuestStateBroadcaster? m_broadcaster;

        /// <summary>累计接取 / 交付成功。</summary>
        private long m_accepted;

        /// <summary>累计交付成功。</summary>
        private long m_submitted;

        /// <summary>累计拒绝的游客请求（**这个数要看得见**：不然"游客点不动"会被当成 bug）。</summary>
        private long m_rejectedGuests;

        /// <summary>累计状态机拒绝。</summary>
        private long m_rejectedByState;

        /// <summary>造一个处理器。</summary>
        /// <param name="authority">任务权威（null = 没接数据库）。</param>
        /// <param name="broadcaster">状态推送线（null = 没接）。</param>
        public QuestService(QuestAuthority? authority, QuestStateBroadcaster? broadcaster)
        {
            m_authority = authority;
            m_broadcaster = broadcaster;
        }

        /// <summary>值得记一句的事情。</summary>
        public event Action<string>? Note;

        /// <summary>一句人话（统计行用）。</summary>
        /// <returns>例：`任务请求：接取 1、交付 1、拒游客 1、状态机拒 2`。</returns>
        public string Describe()
        {
            return "任务请求：接取 " + m_accepted + "、交付 " + m_submitted +
                   "、拒游客 " + m_rejectedGuests + "、状态机拒 " + m_rejectedByState;
        }

        /// <summary>把处理器注册进路由（**显式注册**，见 `ServerMessageRouter`）。</summary>
        /// <param name="router">路由（不能为 null）。</param>
        public void RegisterHandlers(ServerMessageRouter router)
        {
            if (router == null)
            {
                throw new ArgumentNullException(nameof(router), "[QuestService] 路由是 null。");
            }

            router.Register(ClientMessage.PayloadOneofCase.QuestAction, HandleQuestAction);
        }

        /// <summary>处理一条任务动作请求。</summary>
        /// <param name="session">来源会话。</param>
        /// <param name="message">消息。</param>
        /// <returns>分发结果。</returns>
        private DispatchResult HandleQuestAction(ClientSession session, ClientMessage message)
        {
            QuestActionRequest request = message.QuestAction;

            if (request == null)
            {
                // 协议违规：这条消息的 oneof 说是 QuestAction，却没有这个字段
                return DispatchResult.KickOut("QuestAction 消息里没有 QuestActionRequest 字段（协议违规）");
            }

            // ---- ① 身份：游客**明确拒绝**（见文件头二）----
            if (session.PlayerId <= 0)
            {
                m_rejectedGuests++;
                Note?.Invoke("会话 " + session.SessionId + "（游客 id=" + session.PlayerId +
                             "）请求任务 " + request.QuestId + " 动作 " + request.Action + " ⇒ 已拒绝（没有档案）");

                return DispatchResult.ReplyWith(Error(NetErrors.PlayerMismatch,
                    "你是游客（没有玩家档案），不能接取或交付任务 —— 请先用账号登录。"));
            }

            // ---- ② 权威在不在（**与游客分开的另一个码**，见文件头三）----
            if (m_authority == null)
            {
                Note?.Invoke("会话 " + session.SessionId + " 请求任务动作，但服务端**没接任务权威**");

                return DispatchResult.ReplyWith(Error(NetErrors.QuestUnavailable,
                    "服务端现在不能处理任务动作（没接数据库 / 读不到任务表）—— 这是服务端配置问题，不是你的操作问题。"));
            }

            // ---- ③ 转给状态机 ----
            string reason;
            bool ok;

            switch (request.Action)
            {
                case ActionAccept:
                    ok = m_authority.Accept(session.PlayerId, request.QuestId, out reason);

                    if (ok)
                    {
                        m_accepted++;
                    }

                    break;

                case ActionSubmit:
                    ok = m_authority.Submit(session.PlayerId, request.QuestId, out reason);

                    if (ok)
                    {
                        m_submitted++;
                    }

                    break;

                default:
                    // ⚠️ 未知动作**要说清是哪个数字**：将来加动作时这一句就是线索
                    return DispatchResult.ReplyWith(Error(NetErrors.QuestRejected,
                        "未知的任务动作 " + request.Action + "（当前只有 1 = 接取、2 = 交付）"));
            }

            if (!ok)
            {
                m_rejectedByState++;

                // ⚠️ **原因原文**照搬给玩家（可读、可排查）—— 别换成"操作失败"
                return DispatchResult.ReplyWith(Error(NetErrors.QuestRejected,
                    "任务 " + request.QuestId + " 的动作被拒绝：" + reason));
            }

            // ---- ④ 成功：状态由**推送**发（见文件头四）----
            if (m_broadcaster == null)
            {
                // 没接推送 ⇒ 成功就没人回包了。自己回一条，免得表现成"点了没反应"。
                QuestStateSync view;

                if (m_authority.TryBuildQuestStateView(session.PlayerId, 0, out view))
                {
                    return DispatchResult.ReplyWith(new ServerMessage { QuestState = view });
                }

                return DispatchResult.Warn("任务动作成功，但拍不出状态同步（服务端没接推送线）");
            }

            return DispatchResult.Handled;
        }

        /// <summary>造一条错误**消息**（码 + 人话）；调用点用 `DispatchResult.ReplyWith` 包起来。</summary>
        /// <param name="code">错误码（`NetErrors` 里的）。</param>
        /// <param name="message">给玩家看的原因（**要具体**）。</param>
        /// <returns>消息。</returns>
        private static ServerMessage Error(int code, string message)
        {
            return new ServerMessage
            {
                Error = new ErrorResponse { Code = code, Message = message }
            };
        }
    }
}
