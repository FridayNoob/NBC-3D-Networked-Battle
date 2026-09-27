// ============================================================================
//  AccountDirectory —— 账号表（登录时**只查内存**的那一份）
//  项目：3D联网战斗Demo   对应：M4-S3 / SRV-06「真正的登录」，见 `Docs\27` §十九
//
//  ---------------------------------------------------------------------------
//  一、它解决的那个具体欠账
//  ---------------------------------------------------------------------------
//  SRV-06 的"最小版"（`PlayerProfileSlots`）给玩家的编号是**第几个连上来的**：
//      · 连一次拿 1，断开再连可能拿 1，也可能拿 2（取决于有没有人占着）
//      · 名额用完（4 个人连过）⇒ 第 5 个人拿不到档案 ⇒ **战绩明细被外键静默跳过**
//      · 同一台机器上两个人**永远分不清谁是谁**
//  这一版的编号来自**账号自己的档案**（`account → player_profile`）：
//      · 重连还是同一个人 ⇒ 战绩累计到同一个人身上（这才是"账号"的意义）
//      · 名额与连接数**解耦**（账号有几个就能登几个，与"谁先连上来"无关）
//
//  ---------------------------------------------------------------------------
//  二、为什么是"启动时读进内存"（而不是登录时查库）
//  ---------------------------------------------------------------------------
//  登录发生在**网络泵的握手里** —— 那是主循环线程、每帧都跑的地方。
//  在那里等一次 MySQL 往返 = **整个服务端一起卡住**（所有房间、所有玩家都在等这一个人）。
//
//  所以：启动时一次 `account JOIN player_profile` 全读进来（4 行，几毫秒），
//  之后登录就是内存里比一次摘要。**这份数据是只读的**，所以没有锁的问题。
//
//  ⚠️ 代价（如实记录）：**新注册的账号要重启服务端才认**。
//     这是刻意的取舍 —— M4 还没有注册流程，多加一套缓存失效机制是**没人用的复杂度**。
//     与 `PlayerProfileDao` / `PlayerProfileSlots` 是同一套理由，见那两个文件的文件头。
//
//  ---------------------------------------------------------------------------
//  三、为什么用 OrdinalIgnoreCase（这条不是随手选的）
//  ---------------------------------------------------------------------------
//  库里的 `account.username` 是 `utf8mb4_general_ci` 的 **UNIQUE**：
//  也就是说 **MySQL 眼里 `Test01` 和 `test01` 是同一个账号**
//  （`_ci` = case insensitive）。
//
//  如果内存里用**区分大小写**的字典，就会出现最恶心的一类不一致：
//      「库里查得到、内存里查不到」⇒ 表现为"这个账号明明存在，服务端说没有"。
//  ⇒ 内存比较必须跟库一致：**OrdinalIgnoreCase**。
//
//  ⚠️ 而密码摘要的比较**绝不能**忽略大小写（十六进制大写小写是同一个值的两种写法，
//     但**摘要本身必须逐字符恒定时间比较**）—— 见 `PasswordDigest.Verify`。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using Dapper;
using MySqlConnector;
using NBC.Server.Core;
using NBC.Shared.Auth;

namespace NBC.Server.Data
{
    /// <summary>账号 → 玩家身份（实现见文件头）。</summary>
    public sealed class AccountDirectory : IAccountStore
    {
        /// <summary>
        /// 一行账号（Dapper 直接映射的**可变类**）。
        ///
        /// <para>
        /// ⚠️ 必须是**可变类**：Dapper **不能**把行映射到带参构造的 `readonly struct`
        /// —— 它会**静默**返回 default（本项目踩过：探针报"player 1 没有档案"，
        /// 而库里明明有）。同族教训见 `Docs\27` §14。
        /// </para>
        /// </summary>
        private sealed class AccountRow
        {
            /// <summary>`account.account_id`。</summary>
            public long account_id { get; set; }

            /// <summary>`account.username`（**列名必须与 SELECT 的别名一致**）。</summary>
            public string username { get; set; } = string.Empty;

            /// <summary>`account.password_hash` = SHA256(salt + 客户端摘要)。</summary>
            public string password_hash { get; set; } = string.Empty;

            /// <summary>`account.salt`。</summary>
            public string salt { get; set; } = string.Empty;

            /// <summary>`player_profile.player_id`（**LEFT JOIN ⇒ 可能为 null** = 这个账号没档案）。</summary>
            public long? player_id { get; set; }

            /// <summary>`player_profile.nickname`。</summary>
            public string? nickname { get; set; }
        }

        /// <summary>内存里的账号表（key = username，OrdinalIgnoreCase，理由见文件头第三节）。</summary>
        private readonly Dictionary<string, AccountRow> m_byName =
            new Dictionary<string, AccountRow>(StringComparer.OrdinalIgnoreCase);

        /// <summary>总数（= 字典条数，读一次少一次查询）。</summary>
        private readonly int m_count;

        /// <summary>没有档案的账号数（> 0 = 数据不一致，启动横幅会报出来）。</summary>
        private readonly int m_withoutProfile;

        /// <summary>登录成功次数。</summary>
        private long m_loginOk;

        /// <summary>登录被拒次数。</summary>
        private long m_loginRejected;

        /// <summary>造一份账号表（私有：只能从 <see cref="Load"/> 来）。</summary>
        /// <param name="rows">库里的行。</param>
        private AccountDirectory(IReadOnlyList<AccountRow> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                AccountRow row = rows[i];

                if (string.IsNullOrEmpty(row.username))
                {
                    // 静默跳过 = 以后"这个账号登不上，也不知道为什么"，所以这里要报出来
                    throw new InvalidOperationException(
                        $"[AccountDirectory] `account` 表里有一行（account_id={row.account_id}）没有 username。");
                }

                // ⚠️ 重名不许静默覆盖：库里是 UNIQUE，内存里撞了只能是"读到了脏数据"
                if (m_byName.ContainsKey(row.username))
                {
                    throw new InvalidOperationException(
                        $"[AccountDirectory] 账号名重复：\"{row.username}\"（库里是 UNIQUE，出现重复说明数据被绕过约束写进去了）。");
                }

                if (row.player_id == null || row.player_id.Value <= 0)
                {
                    m_withoutProfile++;
                }

                m_byName.Add(row.username, row);
            }

            m_count = m_byName.Count;
        }

        /// <summary>加载了几个账号。</summary>
        public int Count
        {
            get { return m_count; }
        }

        /// <summary>有几个账号没有档案（> 0 = 数据不一致）。</summary>
        public int WithoutProfileCount
        {
            get { return m_withoutProfile; }
        }

        /// <summary>
        /// 启动时从库里读一遍（**这是唯一一次碰数据库**）。
        ///
        /// <para>
        /// ⚠️ 这里是**同步阻塞**的：它发生在主循环之前，几百毫秒换"启动横幅明说账号能不能登"，
        /// 是划算的。主循环里**绝不能**这么写 —— 那正是 `LoginAuditWriter` 存在的理由。
        /// </para>
        /// </summary>
        /// <param name="factory">连接工厂。</param>
        /// <returns>账号表。</returns>
        public static AccountDirectory Load(DbConnectionFactory factory)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory), "[AccountDirectory] 连接工厂是 null。");
            }

            // ⚠️ 列名必须与 `AccountRow` 的属性名一致（Dapper 按名字映射，大小写不敏感）
            const string sql =
                "SELECT a.`account_id` AS `account_id`, " +
                "       a.`username` AS `username`, " +
                "       a.`password_hash` AS `password_hash`, " +
                "       a.`salt` AS `salt`, " +
                "       p.`player_id` AS `player_id`, " +
                "       p.`nickname` AS `nickname` " +
                "FROM `account` a " +
                "LEFT JOIN `player_profile` p ON p.`account_id` = a.`account_id` " +
                "ORDER BY a.`account_id`";

            using (MySqlConnection connection = factory.OpenAsync().GetAwaiter().GetResult())
            {
                List<AccountRow> rows = connection.Query<AccountRow>(
                    new CommandDefinition(sql, commandTimeout: factory.Options.CommandTimeoutSeconds))
                    .AsList();

                return new AccountDirectory(rows);
            }
        }

        /// <summary>
        /// 校验一次登录（**只查内存**，见文件头第二节）。
        /// </summary>
        /// <param name="account">登录名（首尾空白会被忽略）。</param>
        /// <param name="passwordDigest">客户端摘要。</param>
        /// <returns>结果。</returns>
        public LoginResult Login(string account, string passwordDigest)
        {
            string name = (account ?? string.Empty).Trim();

            if (name.Length == 0)
            {
                return LoginResult.Fail(ELoginRejection.BadRequest, "登录名是空的。");
            }

            if (!PasswordDigest.LooksLikeDigest(passwordDigest))
            {
                return LoginResult.Fail(ELoginRejection.BadRequest, "密码摘要的形状不对。");
            }

            AccountRow? row;

            if (!m_byName.TryGetValue(name, out row))
            {
                m_loginRejected++;

                // ⚠️ 取舍：这里**明说"没有这个账号"**（而不是和"密码不对"合并成一句）。
                //    真实系统应当合并 —— 分开说等于送人一个"账号枚举"接口。
                //    本项目选"说人话"：它是本机演示，而负责人要能一眼分清
                //    "用户名打错了"和"密码打错了"（两种原因的修法完全不同）。
                //    取舍写在 `Docs\27` §19.2，别以为这是"忘了"。
                return LoginResult.Fail(ELoginRejection.UnknownAccount, $"没有这个账号：\"{name}\"。");
            }

            // ⚠️ 顺序很重要：**先验密码，再看档案**。
            //    反过来的话，"这个账号没有档案"这句话会送给**任何**知道账号名的人
            //    （等于白送一个"这个账号存不存在 + 状态如何"的探测接口）。
            if (!PasswordDigest.Verify(row.salt, row.password_hash, passwordDigest))
            {
                m_loginRejected++;
                return LoginResult.Fail(ELoginRejection.WrongPassword, $"账号 \"{row.username}\" 的密码不对。");
            }

            if (row.player_id == null || row.player_id.Value <= 0)
            {
                m_loginRejected++;

                // 账号在、密码也对，但没有档案 ⇒ **不能**给一个假 id 糊过去
                return LoginResult.Fail(
                    ELoginRejection.NoProfile,
                    $"账号 \"{row.username}\" 没有玩家档案（`player_profile` 里缺 account_id={row.account_id} 这一行）。" +
                    "这是数据不一致，不是密码问题 —— 请检查 Docs\\08 的种子数据。");
            }

            m_loginOk++;

            return LoginResult.Ok(row.account_id, row.player_id.Value, row.nickname ?? row.username);
        }

        /// <summary>一句话描述（启动横幅 / 退出统计用）。</summary>
        /// <returns>人话。</returns>
        public string Describe()
        {
            var text = new StringBuilder();

            text.Append("账号 ").Append(m_count).Append(" 个");
            text.Append("（可登录 ").Append(m_count - m_withoutProfile).Append(')');
            text.Append("；登录成功 ").Append(m_loginOk);
            text.Append("、被拒 ").Append(m_loginRejected);

            if (m_withoutProfile > 0)
            {
                text.Append("；⚠️ 其中 ").Append(m_withoutProfile)
                    .Append(" 个账号**没有档案**（数据不一致：那些账号登不进来，见上面的告警）");
            }

            return text.ToString();
        }
    }
}
