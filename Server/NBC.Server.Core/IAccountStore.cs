// ============================================================================
//  IAccountStore —— 「拿账号换玩家身份」这件事的**接缝**
//  项目：3D联网战斗Demo   对应：M4-S3 / SRV-06「真正的登录」，见 `Docs\27` §十九
//
//  ---------------------------------------------------------------------------
//  它和 `IPlayerSlotProvider` 的分工（**别混**）
//  ---------------------------------------------------------------------------
//      `IPlayerSlotProvider`  游客：**领一个空闲的档案槽位**（"第几个连上来的"的升级版）
//      `IAccountStore`        登录：**账号 → 它自己的那份档案**（重连还是同一个人）
//
//  ⚠️ 登录**不占用也不释放**槽位：身份来自账号，不来自连接。
//     所以"断开连接"对登录玩家没有任何影响 —— 他也就不需要"还回去"。
//     （对比：游客的槽位是**连接级**的，断了必须 `Release`，否则连接几次就把槽位用光了。）
//
//  ---------------------------------------------------------------------------
//  为什么接口只做"同步、只查内存"
//  ---------------------------------------------------------------------------
//  它在**网络泵的握手里**被调用 —— 那是在主循环线程上、每帧都要跑的地方。
//  在那里 `await` 一次数据库 = **整个服务端卡住等 IO**（所有房间、所有玩家一起等）。
//
//  所以真实实现（`Server\NBC.Server.Data\AccountDirectory`）在**启动时**把
//  `account JOIN player_profile` 一次读进内存，之后登录就是内存里比一次摘要。
//  代价：**新注册的账号要重启服务端才认** —— 刻意的取舍，
//  和 `PlayerProfileDao` / `PlayerProfileSlots` 是同一套理由（M4 还没有注册流程）。
// ============================================================================

namespace NBC.Server.Core
{
    /// <summary>登录被拒的原因（**给机器判**；人话在 <see cref="LoginResult.Reason"/>）。</summary>
    public enum ELoginRejection
    {
        /// <summary>没被拒。</summary>
        None = 0,

        /// <summary>请求本身不合法（没给账号 / 没给密码摘要 / 摘要形状不对）。</summary>
        BadRequest = 1,

        /// <summary>没有这个账号。</summary>
        UnknownAccount = 2,

        /// <summary>账号在，但密码摘要对不上。</summary>
        WrongPassword = 3,

        /// <summary>账号在、密码也对，但**这个账号没有档案**（数据不一致，属于真故障）。</summary>
        NoProfile = 4,
    }

    /// <summary>
    /// 一次登录的结果。
    ///
    /// <para>
    /// ⚠️ 这是个 <c>readonly struct</c>：它是**每个连接一次**的短命值，
    /// 不值得为它分配对象（而且它在握手里被构造，握手的分配次数直接进 GC 统计）。
    /// </para>
    /// </summary>
    public readonly struct LoginResult
    {
        /// <summary>通过了吗。</summary>
        public bool Accepted { get; }

        /// <summary>账号编号（`account.account_id`；游客是 0）。</summary>
        public long AccountId { get; }

        /// <summary>这个账号**自己的**玩家编号（`player_profile.player_id`）。</summary>
        public long PlayerId { get; }

        /// <summary>显示名（档案昵称）。</summary>
        public string Nickname { get; }

        /// <summary>被拒的原因分类。</summary>
        public ELoginRejection Rejection { get; }

        /// <summary>人话原因（可以直接显示给玩家 / 写日志）。通过时是空串。</summary>
        public string Reason { get; }

        /// <summary>造一个"通过"。</summary>
        /// <param name="accountId">账号编号。</param>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="nickname">显示名。</param>
        private LoginResult(long accountId, long playerId, string nickname)
        {
            Accepted = true;
            AccountId = accountId;
            PlayerId = playerId;
            Nickname = nickname ?? string.Empty;
            Rejection = ELoginRejection.None;
            Reason = string.Empty;
        }

        /// <summary>造一个"被拒"。</summary>
        /// <param name="rejection">分类。</param>
        /// <param name="reason">人话。</param>
        private LoginResult(ELoginRejection rejection, string reason)
        {
            Accepted = false;
            AccountId = 0;
            PlayerId = 0;
            Nickname = string.Empty;
            Rejection = rejection;
            Reason = reason ?? string.Empty;
        }

        /// <summary>登录通过。</summary>
        /// <param name="accountId">账号编号。</param>
        /// <param name="playerId">玩家编号。</param>
        /// <param name="nickname">显示名。</param>
        /// <returns>结果。</returns>
        public static LoginResult Ok(long accountId, long playerId, string nickname)
        {
            return new LoginResult(accountId, playerId, nickname);
        }

        /// <summary>登录被拒。</summary>
        /// <param name="rejection">分类。</param>
        /// <param name="reason">人话。</param>
        /// <returns>结果。</returns>
        public static LoginResult Fail(ELoginRejection rejection, string reason)
        {
            return new LoginResult(rejection, reason);
        }

        /// <summary>一句话描述（日志用；⚠️ **不含摘要**）。</summary>
        public override string ToString()
        {
            return Accepted
                ? $"账号 {AccountId} → 玩家 {PlayerId}（{Nickname}）"
                : $"被拒({Rejection})：{Reason}";
        }
    }

    /// <summary>账号 → 玩家身份。</summary>
    public interface IAccountStore
    {
        /// <summary>
        /// 校验一次登录。
        ///
        /// <para>
        /// ⚠️ **必须同步、必须只查内存**（理由见本文件头部）：它跑在网络泵的握手里。
        /// </para>
        /// <para>
        /// ⚠️ 实现**绝不能**把 <paramref name="passwordDigest"/> 写进日志 / 异常消息 / <see cref="LoginResult.Reason"/>。
        /// </para>
        /// </summary>
        /// <param name="account">登录名。</param>
        /// <param name="passwordDigest">客户端摘要（`SHA256(明文密码)` 的小写十六进制）。</param>
        /// <returns>结果（通过或带人说人话的拒绝）。</returns>
        LoginResult Login(string account, string passwordDigest);
    }
}
