// ============================================================================
//  DevAccountStore —— **仅调试用**的内存账号表（`--dev-accounts` 的实现）
//  项目：3D联网战斗Demo   对应：`Docs\27` §三十五 / §35.4
//
//  ---------------------------------------------------------------------------
//  一、⚠️⚠️ 这是什么、以及它**不是**什么
//  ---------------------------------------------------------------------------
//  它是 `IAccountStore` 的**另一个实现**：账号只存在于**内存**里，**完全不查数据库**。
//  ⇒ **开了这个开关的服务端不是正常服务端**（谁都能用 `dev1/dev-pw1` 登进来）。
//  ⇒ 所以 Host **必须**打一行**独特**的警告横幅（判据：**同一句日志不许同时表示正常和故障**）。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 为什么要有它（而不是让 `_net-probe` 去连真库）
//  ---------------------------------------------------------------------------
//  端到端要验的是**锁步链**（连接 → 登录 → 建房 → 收 `LockstepStart`/帧 → 对账）。
//  如果为此把**真数据库**拉成前置：① `_net-probe` 从此必须带 DB 密码才能跑
//  （它现在是**不依赖 DB** 的，这是个好属性）；② 共享真库上多一个并发消费者（**W18** 的邻居问题）。
//  真库那条路已经有 `_db-probe` 与负责人手动步骤各自覆盖 ⇒ 端到端**不该**再依赖它。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ 走的是**已有接缝**，不在登录路径里加分支
//  ---------------------------------------------------------------------------
//  Host 里换的是"给路由的那个 `IAccountStore` 是谁"（`NBC.Server.Data.AccountDirectory` ↔ 本类），
//  **登录路径、握手、路由一行都不用改** —— 这正是当初把这个接口抽出来的意义。
//
//  ---------------------------------------------------------------------------
//  四、⚠️ 身份编号规则（W15）
//  ---------------------------------------------------------------------------
//  `player_id` 必须 **> 0**（`> 0` 真账号 / `< 0` 游客 / `0` 无身份）。
//  这里用 **1..4**；与真库的编号**可能重合**，但**两者不会同时生效**（二选一），所以无害。
//  账号名**一看就是调试**：`dev1`/`dev2`/`dev3`/`dev4`（密码 `dev-pw1`…）。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Shared.Auth;

namespace NBC.Server.Core
{
    /// <summary>
    /// **仅调试用**的内存账号表。⚠️ 用它 = 这个服务端不是正常服务端（见文件头一）。
    /// </summary>
    public sealed class DevAccountStore : IAccountStore
    {
        /// <summary>一个调试账号。</summary>
        private sealed class DevAccount
        {
            /// <summary>玩家编号（**必须 > 0**）。</summary>
            public long PlayerId;

            /// <summary>显示名。</summary>
            public string PlayerName = string.Empty;

            /// <summary>明文密码（⚠️ 只在这个**调试**类里存明文；真实现存的是加盐摘要）。</summary>
            public string Password = string.Empty;
        }

        /// <summary>账号名 → 账号（大小写敏感：与真实现一致，别在这里"宽容"）。</summary>
        private readonly Dictionary<string, DevAccount> m_accounts =
            new Dictionary<string, DevAccount>(StringComparer.Ordinal);

        /// <summary>造一个带 4 个调试账号的表。</summary>
        public DevAccountStore()
        {
            Add("dev1", "dev-pw1", 1, "调试玩家1");
            Add("dev2", "dev-pw2", 2, "调试玩家2");
            Add("dev3", "dev-pw3", 3, "调试玩家3");
            Add("dev4", "dev-pw4", 4, "调试玩家4");
        }

        /// <summary>这批调试账号的说明（Host 的横幅/日志用）。</summary>
        /// <returns>人话。</returns>
        public string Describe()
        {
            return "调试账号 " + m_accounts.Count + " 个（dev1…dev4 / dev-pw1…dev-pw4，**不查数据库**）";
        }

        /// <inheritdoc/>
        public LoginResult Login(string account, string passwordDigest)
        {
            // ① 请求形状：与真实现用**同一个**判据（`PasswordDigest.LooksLikeDigest`）
            if (string.IsNullOrEmpty(account) || !PasswordDigest.LooksLikeDigest(passwordDigest))
            {
                return LoginResult.Fail(ELoginRejection.BadRequest, "账号或密码摘要不合法。");
            }

            // ⚠️ `DevAccount?`（可空）：`TryGetValue` 的 out 参数在可空上下文里就是可空的 ——
            //    写成非空会多一条 `CS8600`，把"全量重建警告仍 4 条"（W24）冲破。
            DevAccount? found;

            if (!m_accounts.TryGetValue(account, out found) || found == null)
            {
                // ⚠️ 拒绝原因**不许**带上摘要（接口注释里的硬要求）
                return LoginResult.Fail(ELoginRejection.UnknownAccount, "没有这个账号。");
            }

            // ② 摘要比较：**恒定时间**（与真实现同一个助手，别自己写 `==`）
            string expected = PasswordDigest.FromPassword(found.Password);

            if (!PasswordDigest.FixedTimeEquals(expected, passwordDigest))
            {
                return LoginResult.Fail(ELoginRejection.WrongPassword, "密码不对。");
            }

            // ③ 通过：调试账号**天生有档案**（`NoProfile` 在真实现里是数据不一致，这里不可能）
            return LoginResult.Ok(found.PlayerId, found.PlayerId, found.PlayerName);
        }

        /// <summary>加一个。</summary>
        /// <param name="account">账号名。</param>
        /// <param name="password">明文密码。</param>
        /// <param name="playerId">玩家编号（**必须 > 0**）。</param>
        /// <param name="playerName">显示名。</param>
        private void Add(string account, string password, long playerId, string playerName)
        {
            if (playerId <= 0)
            {
                // ⚠️ W15：≤ 0 会与"游客/无身份"的判据混淆 ⇒ 在这里就炸，别让它流下去
                throw new ArgumentOutOfRangeException(nameof(playerId),
                    "[DevAccountStore] 调试账号的 player_id 必须 > 0（W15：<0 游客、0 无身份）。");
            }

            var dev = new DevAccount();
            dev.PlayerId = playerId;
            dev.PlayerName = playerName;
            dev.Password = password;
            m_accounts[account] = dev;
        }
    }
}
