// ============================================================================
//  DbConnectionFactory —— 开连接 + **把连接失败翻译成人话**（DB-10 的点名要求）
//  项目：3D联网战斗Demo   对应：需求文档 DB-03（连接池）、DB-10（数据库不可用时的行为）
//
//  ---------------------------------------------------------------------------
//  为什么"翻译错误"值得单独一个类
//  ---------------------------------------------------------------------------
//  DB-10 原文：**"数据库不可用时服务端仍能启动，只让存档功能报错"**，
//  而 `Docs\11` 把它展开成一句更具体的要求：
//
//      "连接失败时要给出**明确的错误提示**（`数据库连接失败：请检查 appsettings.json
//        的 Database 配置与环境变量 NBC_DB_PASSWORD`），而不是抛一个看不懂的异常。"
//
//  `MySqlConnector` 原生抛的是 `MySqlException: Access denied for user 'root'@'localhost'
//  (using password: NO)` —— 对一个**没写过数据库代码的人**来说，"using password: NO"
//  根本看不出"你该去设环境变量"。
//
//  ⇒ 所以这里把常见的四种失败**各自翻译成一句"该怎么办"**（和 M1-A9「让错误自己说出来」同一条原则）：
//      ① 密码没给         → 告诉你环境变量叫什么、怎么设
//      ② 密码不对         → 告诉你密码从哪来、显式值优先
//      ③ 服务没起 / 端口不通 → 告诉你这是"连不上"，不是"密码错"
//      ④ 库不存在         → 告诉你该跑哪个脚本
//
//  ⚠️ 翻译**不吞掉原始异常**：原文放在 `InnerException` 与消息末尾 ——
//     翻译是为了让人看懂，不是为了掩盖证据（W9「别自研判据」）。
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace NBC.Server.Data
{
    /// <summary>数据库连接串 → 连接（带人话错误）。</summary>
    public sealed class DbConnectionFactory
    {
        /// <summary>连接参数（**已注入密码**，见 `DatabaseOptions.ApplyPasswordFromEnvironment`）。</summary>
        private readonly DatabaseOptions m_options;

        /// <summary>连接串（构造时拼一次；`MySqlConnector` 自己维护连接池，不需要我们缓存连接）。</summary>
        private readonly string m_connectionString;

        /// <summary>造一个连接工厂。</summary>
        /// <param name="options">连接参数（不能为 null）。</param>
        public DbConnectionFactory(DatabaseOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options), "[DbConnectionFactory] 连接参数是 null。");
            }

            m_options = options;
            m_connectionString = options.BuildConnectionString();
        }

        /// <summary>连接参数（只读用途，例如日志里 `Describe()`）。</summary>
        public DatabaseOptions Options
        {
            get { return m_options; }
        }

        /// <summary>
        /// 异步开一个连接。
        /// <para>⚠️ **连接池由 `MySqlConnector` 管**（连接串里 `Pooling=true`）：
        /// 这里 `OpenAsync` 拿到的多半是池里已有的连接，不产生 TCP 握手。
        /// 所以"每次操作开一个连接"是**对的用法**，不要自己去缓存 `MySqlConnection`
        /// —— 那会和连接池打架（DB-03 要的是池，不是复用对象）。</para>
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>已打开的连接（**调用方负责 Dispose**）。</returns>
        /// <exception cref="DatabaseUnavailableException">连不上，消息里带"该怎么办"。</exception>
        public async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            MySqlConnection connection = new MySqlConnection(m_connectionString);

            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch (Exception ex)
            {
                connection.Dispose();
                throw new DatabaseUnavailableException(Explain(ex), ex);
            }
        }

        /// <summary>
        /// 探活：能不能连上（**服务端启动时用一次**）。
        /// <para>⚠️ 按 DB-10，**返回结果而不是抛** —— 调用方据此决定
        /// "降级启动"（存档功能报错、其余照常），而不是让整个进程起不来。</para>
        /// <para>⚠️ 为什么返回一个结构体而不是 `out string`：**`async` 方法不允许 `out` 参数**
        /// （CS1988，闸门当场抓到）。也刻意不用"返回 null 表示成功"——
        /// 那种约定在调用点读起来是 `if (reason == null)`，语义反直觉。</para>
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>探活结果（成功时 `Reason` 是 null）。</returns>
        public async Task<PingResult> PingAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                using (MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
                {
                    using (MySqlCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT 1";
                        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                return PingResult.Success();
            }
            catch (Exception ex)
            {
                return PingResult.Failure(ex.Message);
            }
        }

        /// <summary>把异常翻译成"出了什么事 + 该怎么办"。</summary>
        /// <param name="ex">原始异常。</param>
        /// <returns>多行人话。</returns>
        private string Explain(Exception ex)
        {
            MySqlException? mysql = FindMySqlException(ex);
            string head = "数据库连接失败：" + m_options.Describe() + "\n";

            if (mysql == null)
            {
                return head +
                       "原因：" + ex.Message + "\n" +
                       "（这不是 MySQL 自己报的错，多半是连接串或网络栈的问题。）";
            }

            // MySqlConnector 的错误号：与 MySQL 服务端一致（1045 = 认证失败，1049 = 库不存在）
            string how;

            switch (mysql.Number)
            {
                case 1045:
                    how = string.IsNullOrEmpty(m_options.Password)
                        ? "**密码没给**（`using password: NO`）。两种给法：\n" +
                          "  ① 设环境变量（推荐，密码不进 Git）：`$env:" + DatabaseOptions.PasswordEnvironmentVariable + " = \"你的密码\"`\n" +
                          "  ② 直接在 `DatabaseOptions.Password` 里给（**别写进 appsettings.json 提交**）"
                        : "**密码不对**。注意：**显式给的值优先于环境变量** ——\n" +
                          "  如果你在代码/测试里给了密码，它会盖掉 `" + DatabaseOptions.PasswordEnvironmentVariable + "`。";
                    break;

                case 1049:
                    how = "**这个库不存在**。跑一次建库脚本（注意它**会清空** `nbc_db`）：\n" +
                          "  cmd> mysql -u root -p < \"Docs\\08-数据库脚本.sql\"";
                    break;

                case 1044:
                    how = "**这个账号没有访问该库的权限**。检查 `Database.User` 与 MySQL 的授权。";
                    break;

                default:
                    if (mysql.Number == 0 || mysql.Number == 1042 || IsConnectFailure(mysql))
                    {
                        how = "**连不上**（不是密码问题）。按顺序查三件事：\n" +
                              "  ① MySQL 服务起没起：`Get-Service MySQL80`（应当是 Running）\n" +
                              "  ② 端口通不通：`Test-NetConnection " + m_options.Host + " -Port " + m_options.Port + "`\n" +
                              "  ③ `Database.Host` / `Database.Port` 写对没有（默认 `127.0.0.1:3306`）";
                    }
                    else
                    {
                        how = "MySQL 报了错误号 " + mysql.Number + "（见下面的原文）。";
                    }

                    break;
            }

            return head + how + "\n原始错误：[" + mysql.Number + "] " + mysql.Message;
        }

        /// <summary>在异常链里找 `MySqlException`（它可能被包在别的异常里）。</summary>
        /// <param name="ex">异常链的头。</param>
        /// <returns>找到返回它，否则 null。</returns>
        private static MySqlException? FindMySqlException(Exception ex)
        {
            for (Exception? current = ex; current != null; current = current.InnerException)
            {
                MySqlException? mysql = current as MySqlException;

                if (mysql != null)
                {
                    return mysql;
                }
            }

            return null;
        }

        /// <summary>是不是"连不上"这一类（MySqlConnector 对连接失败不一定给服务端错误号）。</summary>
        /// <param name="ex">异常。</param>
        /// <returns>是连接问题返回 true。</returns>
        private static bool IsConnectFailure(MySqlException ex)
        {
            // ⚠️ **按异常类型判，不按消息文本判**：消息是可本地化的（本机就是中文），
            //    拿 `Contains("timeout")` 当判据会在中文环境下失效 —— 而且**失效是静默的**
            //    （只是少给了一条提示）。同族：W9「别自研判据」。
            return ex.InnerException is System.Net.Sockets.SocketException
                || ex.InnerException is TimeoutException
                || ex.InnerException is System.IO.IOException;
        }
    }

    /// <summary>
    /// 数据库连不上（**消息里已经带了"该怎么办"**，见 `DbConnectionFactory.Explain`）。
    /// <para>单独一个类型是为了让调用方能**精确捕获"数据库不可用"**并按 DB-10 降级 ——
    /// 捕 `Exception` 会把"SQL 写错了"也当成"数据库不可用"，把真 bug 藏起来。</para>
    /// </summary>
    public sealed class DatabaseUnavailableException : Exception
    {
        /// <summary>造一个。</summary>
        /// <param name="message">人话（已含"该怎么办"）。</param>
        /// <param name="inner">原始异常（**不吞掉证据**；没有底层异常时可为 null）。</param>
        public DatabaseUnavailableException(string message, Exception? inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>一次数据库探活的结果（**不用 `out` 参数**，见 `PingAsync` 的说明）。</summary>
    public readonly struct PingResult
    {
        /// <summary>连上了吗。</summary>
        private readonly bool m_ok;

        /// <summary>没连上时的原因（连上时是 null）。</summary>
        private readonly string? m_reason;

        /// <summary>造一个。</summary>
        /// <param name="ok">连上了吗。</param>
        /// <param name="reason">没连上时的原因。</param>
        private PingResult(bool ok, string? reason)
        {
            m_ok = ok;
            m_reason = reason;
        }

        /// <summary>连上了吗。</summary>
        public bool Ok
        {
            get { return m_ok; }
        }

        /// <summary>没连上时的原因（连上时是 null）。</summary>
        public string? Reason
        {
            get { return m_reason; }
        }

        /// <summary>连上了。</summary>
        /// <returns>结果。</returns>
        public static PingResult Success()
        {
            return new PingResult(true, null);
        }

        /// <summary>没连上。</summary>
        /// <param name="reason">原因（**必须给人话**）。</param>
        /// <returns>结果。</returns>
        public static PingResult Failure(string reason)
        {
            return new PingResult(false, reason);
        }
    }
}
