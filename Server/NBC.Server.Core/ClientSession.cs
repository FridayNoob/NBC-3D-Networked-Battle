// ============================================================================
//  NBC.Server.Core —— 服务端会话
//  项目：3D联网战斗Demo   对应需求文档 §13.3（SRV-03 / SRV-05）
//
//  M0 阶段：仅建立类型与边界，不含 Socket 实现。
//  M3 阶段接入：实际连接、登录绑定、心跳超时、断线清理。
// ============================================================================

namespace NBC.Server.Core;

/// <summary>
/// 一个客户端连接的会话。
/// </summary>
/// <remarks>
/// 设计要点（需求文档 §13.3 SRV-04 / SRV-05）：
/// <list type="bullet">
///   <item>网络 IO 回调线程【只做】解包 + 入队，绝不直接改战斗状态</item>
///   <item>所有逻辑在主循环线程串行执行 —— 这是避免多线程 Bug 的关键设计</item>
///   <item>会话状态机：Connecting → Authenticated → InLobby → InRoom → InBattle → Closed</item>
/// </list>
/// </remarks>
public sealed class ClientSession
{
    /// <summary>会话内唯一自增 ID（连接建立时由服务端分配）。</summary>
    public long SessionId { get; init; }

    /// <summary>远端地址，仅用于日志。</summary>
    public string RemoteEndPoint { get; init; } = "unknown";

    /// <summary>登录成功后的玩家 ID；未登录为 0。</summary>
    public long PlayerId { get; set; }

    /// <summary>登录成功后的账号 ID；未登录为 0。</summary>
    public long AccountId { get; set; }

    /// <summary>重连令牌（需求文档 NET-04：会话保留 60s）。</summary>
    public string SessionToken { get; set; } = string.Empty;

    /// <summary>当前会话阶段。</summary>
    public SessionPhase Phase { get; set; } = SessionPhase.Connecting;

    /// <summary>最后一次收到该会话报文的时间（用于心跳超时判定，SRV-03）。</summary>
    public DateTime LastRecvTimeUtc { get; set; } = DateTime.UtcNow;

    /// <summary>是否已通过登录认证。</summary>
    public bool IsAuthenticated => Phase != SessionPhase.Connecting && Phase != SessionPhase.Closed;
}

/// <summary>会话阶段。</summary>
public enum SessionPhase
{
    /// <summary>已连接，未登录。</summary>
    Connecting = 0,

    /// <summary>已登录，在大厅。</summary>
    InLobby = 1,

    /// <summary>已在房间内，未开局。</summary>
    InRoom = 2,

    /// <summary>战斗中。</summary>
    InBattle = 3,

    /// <summary>已关闭 / 待清理。</summary>
    Closed = 4,
}
