// ============================================================================
//  NBC.Server.Core —— 服务端消息路由（握手门 + 显式注册表）
//  项目：3D联网战斗Demo
//  对应：需求文档 §13.2（MsgId 注册表：**显式注册，不用反射** —— NET-02）、Docs\25 S3
//
//  ---------------------------------------------------------------------------
//  一、为什么是"显式注册"而不是反射扫一遍所有 Handler
//  ---------------------------------------------------------------------------
//  反射扫程序集的做法有三个后果，而且都在**运行期**才暴露：
//    · 改了方法名/签名，编译期一切正常，收到消息才"没有处理器" —— 静默失败
//    · 谁被注册、谁没被注册，读代码看不出来（要跑起来才知道）
//    · IL2CPP 下反射裁剪是另一个坑（客户端那边尤其）
//  所以这里就是一张显式的表：**注册了什么，一眼看得见；没注册就明确报"还没实现"**。
//
//  ---------------------------------------------------------------------------
//  二、一次分发有三种结果（写清楚，免得调用方漏处理）
//  ---------------------------------------------------------------------------
//      Reply  —— 要回一条消息（可以同时有 Kick：**先发再断**，顺序由调用方保证）
//      Kick   —— 断开这个会话（原因写清为什么；客户端能不能收到说明，取决于先发后断）
//      Note   —— 只记一条日志，不断开（例如"服务端还没实现 JoinRoom"）
//
//  ⚠️ 为什么 `Note` 单独存在：M3 是分片做的，客户端可能先发了 S4/S5 的消息。
//     那时**不能装作没看见**（静默失败），也不该直接把客户端踢掉（调试期太粗暴），
//     而是明确记一句"还没实现 X"。
//
//  ---------------------------------------------------------------------------
//  三、握手门（M0 的会话状态机在这里落地）
//  ---------------------------------------------------------------------------
//  `ClientSession.Phase` 从 M0 起就有：Connecting → InLobby → InRoom → InBattle → Closed。
//  规则一句话：**Connecting 阶段的会话只许发 Handshake，别的直接断开**。
//  没有这道门的话，服务端会在"玩家还没身份"的状态下处理进房/输入 ——
//  那种 bug 表现为"偶尔有个 player_id 为 0 的单位"，非常难查。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Protocol;
using NBC.Shared.Auth;
using NBC.Shared.Net;

namespace NBC.Server.Core;

/// <summary>一次消息分发的三种结果（见文件头第二节）。</summary>
public readonly struct DispatchResult
{
    /// <summary>要回给客户端的消息（null = 不回）。</summary>
    public readonly ServerMessage? Reply;

    /// <summary>断开这个会话的人话原因（null = 不断开）。</summary>
    public readonly string? Kick;

    /// <summary>只记一条日志的说明（null = 没什么要说的）。</summary>
    public readonly string? Note;

    private DispatchResult(ServerMessage? reply, string? kick, string? note)
    {
        Reply = reply;
        Kick = kick;
        Note = note;
    }

    /// <summary>回一条消息。</summary>
    /// <param name="reply">消息。</param>
    /// <returns>结果。</returns>
    public static DispatchResult ReplyWith(ServerMessage reply) => new(reply, null, null);

    /// <summary>断开会话（原因必须是人话）。</summary>
    /// <param name="reason">原因。</param>
    /// <returns>结果。</returns>
    public static DispatchResult KickOut(string reason) => new(null, reason, null);

    /// <summary>先回一条消息，再断开（例如"版本不一致"的 `HandshakeAck` + 断开）。</summary>
    /// <param name="reply">消息。</param>
    /// <param name="reason">断开原因。</param>
    /// <returns>结果。</returns>
    public static DispatchResult ReplyThenKick(ServerMessage reply, string reason) => new(reply, reason, null);

    /// <summary>只记日志。</summary>
    /// <param name="note">说明。</param>
    /// <returns>结果。</returns>
    public static DispatchResult Warn(string note) => new(null, null, note);

    /// <summary>什么都不做（消息被正常处理且不需要回包）。</summary>
    public static DispatchResult Handled => new(null, null, null);
}

/// <summary>服务端消息路由：握手内建 + 其余显式注册（不用反射）。</summary>
public sealed class ServerMessageRouter
{
    /// <summary>处理器表：一条消息类型 → 一个处理器。</summary>
    private readonly Dictionary<ClientMessage.PayloadOneofCase, Func<ClientSession, ClientMessage, DispatchResult>>
        _handlers = new();

    private readonly string _serverVersion;
    private long _nextPlayerId = 1;

    /// <summary>
    /// 玩家档案槽位（**可空**：没接数据库时为 null ⇒ 退回"第几个连上来的"）。
    /// <para>⚠️ 它是 `IPlayerSlotProvider`（Core 里的接缝），实现住在 `NBC.Server.Data` ——
    /// 网络层**不许**认识 MySQL。见那个接口的文件头。</para>
    /// </summary>
    private readonly IPlayerSlotProvider? _slots;

    /// <summary>
    /// 账号表（**可空**：没接数据库时为 null ⇒ 账号登录用不了，游客照旧）。
    /// <para>⚠️ 与 <see cref="_slots"/> 一样是 Core 里的接缝，实现住在 `NBC.Server.Data`。</para>
    /// </summary>
    private readonly IAccountStore? _accounts;

    /// <summary>
    /// 登录成功（参数是完整结果：账号编号 / 玩家编号 / 昵称）。
    /// <para>
    /// ⚠️ 为什么把整个 <see cref="LoginResult"/> 传出去、而不是只传一个 `account_id`：
    /// 监听方（Host）既要**记审计**又要**写日志**，日志想写"账号 → 玩家（昵称）"。
    /// 只给一个 id 的话监听方得自己再查一遍 —— 那正是「在信息最全的地方记账」的反面。
    /// </para>
    /// <para>⚠️ 监听方**只许入队**，不许在握手里等 IO。</para>
    /// </summary>
    public event Action<LoginResult>? AccountLoggedIn;

    /// <summary>建一个路由。</summary>
    /// <param name="serverVersion">服务端版本标识（只进日志与 `HandshakeAck`，用于排查）。</param>
    /// <param name="slots">
    /// 玩家档案槽位；**传 null = 没有数据库**，玩家编号退回"第几个连上来的"计数器。
    /// <para>⚠️ 这个降级路径是**必须保留**的：`_net-probe` 在没有数据库的环境里跑，
    /// 而它的用例断言 `PlayerId == 1`、`== 2`。退回老行为，那 93 条才不会红。</para>
    /// </param>
    /// <param name="accounts">
    /// 账号表；**传 null = 没有数据库** ⇒ 带账号的握手会被拒（并说清"为什么用不了"），
    /// 不带账号的游客握手**一字不变**。
    /// <para>⚠️ 为什么这里**不**"登录失败就退回游客"：那会让玩家以为自己登录了
    /// （界面显示已登录，战绩却记到别人头上）。**身份这种事不许静默降级。**</para>
    /// </param>
    public ServerMessageRouter(
        string? serverVersion = null,
        IPlayerSlotProvider? slots = null,
        IAccountStore? accounts = null)
    {
        _serverVersion = string.IsNullOrEmpty(serverVersion) ? "nbc-server/unknown" : serverVersion!;
        _slots = slots;
        _accounts = accounts;

        // 握手是**内建**的：既不能被替换，也不可能忘记注册
        _handlers[ClientMessage.PayloadOneofCase.Handshake] = HandleHandshake;
    }

    /// <summary>已经注册了几种消息（含内建的握手）。</summary>
    public int HandlerCount => _handlers.Count;

    /// <summary>服务端版本标识。</summary>
    public string ServerVersion => _serverVersion;

    /// <summary>
    /// 注册一种消息的处理器（**显式注册**，见文件头第一节）。
    /// </summary>
    /// <param name="kind">消息类型。</param>
    /// <param name="handler">处理器。</param>
    public void Register(
        ClientMessage.PayloadOneofCase kind,
        Func<ClientSession, ClientMessage, DispatchResult> handler)
    {
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        if (kind == ClientMessage.PayloadOneofCase.Handshake)
        {
            throw new InvalidOperationException(
                "[ServerMessageRouter] 握手是内建的，不允许替换（换掉它等于把版本校验这道门拆了）。");
        }

        if (kind == ClientMessage.PayloadOneofCase.None)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "None 不是一个可注册的消息类型。");
        }

        if (_handlers.ContainsKey(kind))
        {
            throw new InvalidOperationException(
                $"[ServerMessageRouter] {kind} 已经注册过了（重复注册会让「到底哪份生效」变成猜谜）。");
        }

        _handlers[kind] = handler;
    }

    /// <summary>是不是注册过某种消息。</summary>
    /// <param name="kind">消息类型。</param>
    /// <returns>注册过返回 true。</returns>
    public bool IsRegistered(ClientMessage.PayloadOneofCase kind) => _handlers.ContainsKey(kind);

    /// <summary>
    /// 分发一条消息。**不碰 socket**：要回什么、要不要断开，都由调用方按结果执行
    /// （这样路由本身是纯逻辑，可以在没有网络的情况下测）。
    /// </summary>
    /// <param name="session">来源会话。</param>
    /// <param name="message">消息。</param>
    /// <returns>三种结果（见 <see cref="DispatchResult"/>）。</returns>
    public DispatchResult Dispatch(ClientSession session, ClientMessage message)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        ClientMessage.PayloadOneofCase kind = message.PayloadCase;

        if (kind == ClientMessage.PayloadOneofCase.None)
        {
            // 一条空消息（0 字节帧解出来就是它）—— 协议违规，别装作没看见
            return DispatchResult.KickOut("收到一条没有 payload 的消息（协议违规）");
        }

        if (session.Phase == SessionPhase.Closed)
        {
            return DispatchResult.KickOut("会话已经是关闭状态，不再处理消息");
        }

        // 握手门：见文件头第三节
        if (!session.IsAuthenticated && kind != ClientMessage.PayloadOneofCase.Handshake)
        {
            return DispatchResult.KickOut($"还没握手就发了 {kind}（第一条消息必须是 Handshake）");
        }

        if (!_handlers.TryGetValue(kind, out var handler))
        {
            return DispatchResult.Warn($"服务端还没实现 {kind}（M3 的后续切片里补）");
        }

        return handler(session, message);
    }

    // ====================================================================
    //  内建：握手
    // ====================================================================

    /// <summary>
    /// 握手：版本不一致就**回一条说明 + 断开**；一致则分配 `player_id` 并把会话推进到 InLobby。
    /// </summary>
    /// <param name="session">会话。</param>
    /// <param name="message">消息。</param>
    /// <returns>结果。</returns>
    private DispatchResult HandleHandshake(ClientSession session, ClientMessage message)
    {
        Handshake hello = message.Handshake;

        if (hello == null)
        {
            return DispatchResult.KickOut("Handshake 消息里没有 Handshake 字段（协议违规）");
        }

        if (session.IsAuthenticated)
        {
            // 重复握手不改任何状态：身份已经发过了，再发一次只能是客户端 bug
            return DispatchResult.Warn(
                $"会话 {session.SessionId}（玩家 {session.PlayerId}）重复握手，已忽略");
        }

        if (hello.ProtocolVersion != NetContract.Version)
        {
            var rejected = new HandshakeAck
            {
                Accepted = false,
                ProtocolVersion = NetContract.Version,
                ServerVersion = _serverVersion,
                PlayerId = 0,
                TickHz = NetContract.TickRate,
                Reason = $"协议版本不一致：客户端 v{hello.ProtocolVersion}，服务端 v{NetContract.Version}。" +
                         "两端必须用同一份 Protocol/nbc_m3.proto 生成代码。",
            };

            // ⚠️ 先回后断：这条 Ack 是"被拒"的唯一说明，断了就再也发不出去了
            return DispatchResult.ReplyThenKick(
                new ServerMessage { HandshakeAck = rejected },
                $"协议版本不一致（客户端 v{hello.ProtocolVersion} ≠ 服务端 v{NetContract.Version}）");
        }

        // ⚠️ 身份只有两条路，**没有第三种**（身份这种事不许静默降级）：
        //    ① 给了账号 ⇒ **必须登进去**（失败就拒，并说清为什么）
        //    ② 没给账号 ⇒ **游客**，走 M3 的老路（槽位 / 计数器）
        if (!string.IsNullOrEmpty(hello.Account))
        {
            return HandleAccountLogin(session, hello);
        }

        // 玩家编号从哪来（见 `IPlayerSlotProvider` 文件头）：
        //   ① 接了数据库 ⇒ **领一个真实存在的档案槽位**（断开时还回去 ⇒ 反复连接会复用 1~4）
        //   ② 没接数据库 / 槽位用完了 ⇒ 退回"第几个连上来的"计数器（老行为）
        //
        // ⚠️ ②**必须保留**：`_net-probe` 没有数据库，而它断言 `PlayerId == 1`、`== 2`。
        // ⚠️ 退回计数器时**可能撞上真实档案 id**（比如槽位发完了，计数器的 5 又发出去）
        //    —— 那正是"战绩明细会被外键拒绝"的老问题。所以 Host 那边会把
        //    `ExhaustedCount > 0` 报出来（见 `PlayerProfileSlots.Describe`）。
        long claimedPlayerId;

        if (_slots != null && _slots.TryClaim(out claimedPlayerId))
        {
            session.PlayerId = claimedPlayerId;
        }
        else
        {
            // M3 的老语义：player_id 就是"第几个连上来的"
            session.PlayerId = _nextPlayerId++;
        }

        session.PlayerName = string.IsNullOrEmpty(hello.PlayerName) ? "玩家" + session.PlayerId : hello.PlayerName;
        session.Phase = SessionPhase.InLobby;

        var accepted = new HandshakeAck
        {
            Accepted = true,
            ProtocolVersion = NetContract.Version,
            ServerVersion = _serverVersion,
            PlayerId = session.PlayerId,
            TickHz = NetContract.TickRate,
            Reason = string.Empty,
            Nickname = session.PlayerName,
        };

        return DispatchResult.ReplyWith(new ServerMessage { HandshakeAck = accepted });
    }

    /// <summary>
    /// 账号登录（M4-S3 / SRV-06 真正的登录）。
    ///
    /// <para>
    /// 成功时 `player_id` 来自**账号自己的档案**（`player_profile.player_id`）——
    /// 所以**重连还是同一个人**，战绩累计到同一个人身上。
    /// 这是它与游客槽位（"第几个连上来的"）最本质的区别。
    /// </para>
    /// </summary>
    /// <param name="session">会话。</param>
    /// <param name="hello">握手内容（已确认 `Account` 非空）。</param>
    /// <returns>结果。</returns>
    private DispatchResult HandleAccountLogin(ClientSession session, Handshake hello)
    {
        // ① 服务端有没有能力做账号登录（没接数据库就没有）
        if (_accounts == null)
        {
            return RejectLogin(session, hello,
                "服务端没有接数据库，账号登录用不了。请把登录名留空、用游客进；" +
                "或在服务端设环境变量 NBC_DB_PASSWORD 后重启（见 Docs\\11 §三）。");
        }

        // ② 客户端有没有带密码摘要
        //    ⚠️ 摘要形状先自检：**别把畸形输入送给比较函数**（那样只会得到一句"密码不对"，
        //       而真正的原因是客户端根本没发密码 —— 两种原因的修法完全不同）
        if (!PasswordDigest.LooksLikeDigest(hello.PasswordDigest))
        {
            return RejectLogin(session, hello,
                $"登录请求里的密码摘要形状不对（应当是 {PasswordDigest.HexLength} 个小写十六进制字符，" +
                $"实际 {(string.IsNullOrEmpty(hello.PasswordDigest) ? "为空" : hello.PasswordDigest.Length + " 个字符")}）。" +
                "客户端应当用共享层 NBC.Shared.Auth.PasswordDigest.FromPassword 算摘要。");
        }

        // ③ 真正校验（实现只查内存，见 IAccountStore 文件头）
        LoginResult login = _accounts.Login(hello.Account, hello.PasswordDigest);

        if (!login.Accepted)
        {
            // ⚠️ 这里**只把 Reason 发出去**：摘要 / 密码 / salt **一个字都不进日志与错误消息**
            return RejectLogin(session, hello, login.Reason);
        }

        session.AccountId = login.AccountId;
        session.PlayerId = login.PlayerId;
        session.PlayerName = login.Nickname;
        session.Phase = SessionPhase.InLobby;

        Action<LoginResult>? loggedIn = AccountLoggedIn;

        if (loggedIn != null)
        {
            // ⚠️ 监听方只许入队（`LoginAuditWriter`），不许在这里等数据库
            loggedIn(login);
        }

        var accepted = new HandshakeAck
        {
            Accepted = true,
            ProtocolVersion = NetContract.Version,
            ServerVersion = _serverVersion,
            PlayerId = session.PlayerId,
            TickHz = NetContract.TickRate,
            Reason = string.Empty,
            Nickname = session.PlayerName,
        };

        return DispatchResult.ReplyWith(new ServerMessage { HandshakeAck = accepted });
    }

    /// <summary>拒一次登录（先回说明再断开 —— 老规矩，Ack 是"被拒"的唯一说明）。</summary>
    /// <param name="session">会话。</param>
    /// <param name="hello">握手内容。</param>
    /// <param name="reason">人话原因。</param>
    /// <returns>结果。</returns>
    private DispatchResult RejectLogin(ClientSession session, Handshake hello, string reason)
    {
        var rejected = new HandshakeAck
        {
            Accepted = false,
            ProtocolVersion = NetContract.Version,
            ServerVersion = _serverVersion,
            PlayerId = 0,
            TickHz = NetContract.TickRate,
            Reason = reason,
        };

        // ⚠️ 日志里是**账号名**（便于排查"是不是把用户名打错了"），
        //    但**绝不含**摘要/密码/salt
        return DispatchResult.ReplyThenKick(
            new ServerMessage { HandshakeAck = rejected },
            $"账号登录被拒（会话 {session.SessionId}，账号 \"{hello.Account}\"）：{reason}");
    }
}
