// ============================================================================
//  DatabaseOptions —— 数据层的连接参数（纯 POCO，不认 IConfiguration）
//  项目：3D联网战斗Demo   对应：需求文档 §13.3 SRV-01、§13.4、DB-10
//
//  ---------------------------------------------------------------------------
//  为什么要一个"纯 POCO"，而不是直接吃 IConfiguration
//  ---------------------------------------------------------------------------
//  数据层的测试要能 `new DatabaseOptions { Host = "...", Password = "..." }` ——
//  不需要造一个配置对象、不需要 json 文件。
//  "从 appsettings.json 读"是 **Host** 的事（那边本来就有 Extensions.Configuration）。
//  这是 M1 那几道缝（IConfigSource / IAssetProvider）同一个套路：**把"从哪来"和"是什么"分开**。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 密码的三条规矩（每一条都是踩过的）
//  ---------------------------------------------------------------------------
//  ① **密码不进 Git**：`appsettings.json` 里那格永远是空串，
//     真密码走环境变量 `NBC_DB_PASSWORD` 覆盖（见 §13.3 SRV-01 与 `Docs\11`）。
//  ② **`ToString()` 绝不打印密码**（`Describe()` 只给"设了没有 + 长度"）——
//     日志是最容易把密码漏出去的地方，而"顺手 ToString 一下"几乎必然发生。
//  ③ **没设密码要说清怎么设**（DB-10：不能抛一个看不懂的异常）。
// ============================================================================

using System;
using System.Globalization;
using System.Text;

namespace NBC.Server.Data
{
    /// <summary>数据库连接参数（对应 `appsettings.json` 的 `Database` 段）。</summary>
    public sealed class DatabaseOptions
    {
        /// <summary>真密码的环境变量名（`appsettings.json` 里那格只放占位空串）。</summary>
        public const string PasswordEnvironmentVariable = "NBC_DB_PASSWORD";

        /// <summary>`appsettings.json` 里 `Database` 段的名字。</summary>
        public const string ConfigurationSectionName = "Database";

        /// <summary>提供者名（当前只支持 `MySql`；留着是为了"换了要报错而不是静默走错"）。</summary>
        public string Provider { get; set; } = "MySql";

        /// <summary>主机。</summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>端口。</summary>
        public int Port { get; set; } = 3306;

        /// <summary>库名。</summary>
        public string Database { get; set; } = "nbc_db";

        /// <summary>用户名。</summary>
        public string User { get; set; } = "root";

        /// <summary>密码（**不从 json 读**，见文件头规矩①；由环境变量或调用方注入）。</summary>
        public string? Password { get; set; } = string.Empty;

        /// <summary>连接超时（秒）。</summary>
        public int ConnectionTimeoutSeconds { get; set; } = 5;

        /// <summary>命令超时（秒）。</summary>
        public int CommandTimeoutSeconds { get; set; } = 10;

        /// <summary>连接池上限（DB-03 要求走连接池）。</summary>
        public int MaxPoolSize { get; set; } = 10;

        /// <summary>
        /// 按 DB-10 把环境变量里的密码填进来（**只在配置里没给密码时**）。
        /// <para>⚠️ 顺序很重要：**显式给的值优先**。否则测试里设的密码会被机器上的
        /// 环境变量悄悄覆盖，表现是"测试在别人机器上失败"——极难查。</para>
        /// </summary>
        /// <param name="environmentPassword">环境变量的值（测试可注入；null = 去读进程环境）。</param>
        /// <returns>这次真的用环境变量覆盖了没有。</returns>
        public bool ApplyPasswordFromEnvironment(string? environmentPassword = null)
        {
            if (!string.IsNullOrEmpty(Password))
            {
                return false;   // 显式给过 → 不动
            }

            string? value = environmentPassword ?? Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);

            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            Password = value;
            return true;
        }

        /// <summary>
        /// 拼连接串。
        /// <para>⚠️ 连接池与超时都写进连接串（MySqlConnector 的开关就在这里），
        /// 而不是留在代码里 —— 这样"改了配置要重启"这件事是显式的。</para>
        /// </summary>
        /// <returns>MySqlConnector 用的连接串。</returns>
        /// <exception cref="InvalidOperationException">提供者不是 MySql（**不静默走错**）。</exception>
        public string BuildConnectionString()
        {
            if (!string.Equals(Provider, "MySql", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "[DatabaseOptions] 不支持的数据库提供者「" + Provider + "」。\n" +
                    "本项目只实现 MySql（需求文档 §5.2：Dapper + MySqlConnector）。\n" +
                    "与其静默按 MySql 连、让人以为配置生效了，不如当场说清。");
            }

            var builder = new StringBuilder();
            builder.Append("Server=").Append(Host);
            builder.Append(";Port=").Append(Port.ToString(CultureInfo.InvariantCulture));
            builder.Append(";Database=").Append(Database);
            builder.Append(";User ID=").Append(User);
            builder.Append(";Password=").Append(Password ?? string.Empty);
            builder.Append(";Connection Timeout=").Append(ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            builder.Append(";Default Command Timeout=").Append(CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            builder.Append(";Maximum Pool Size=").Append(MaxPoolSize.ToString(CultureInfo.InvariantCulture));
            builder.Append(";Pooling=true");

            // ⚠️ 字符集必须显式给 utf8mb4：库表都是 utf8mb4，
            //    连接若不是，中文（昵称/物品名）会出现"写进去是问号"这种**不报错**的损坏。
            builder.Append(";Character Set=utf8mb4");

            return builder.ToString();
        }

        /// <summary>
        /// 一句人话描述（**日志/报错用；故意不含密码**）。
        /// <para>密码只报"设了没有 + 长度" —— 够定位"是不是没设环境变量"，
        /// 又不至于把密码写进日志文件。</para>
        /// </summary>
        /// <returns>例如 `MySql root@127.0.0.1:3306/nbc_db（密码：已设，8 位）`。</returns>
        public string Describe()
        {
            return Provider + " " + User + "@" + Host + ":" + Port.ToString(CultureInfo.InvariantCulture) +
                   "/" + Database +
                   "（密码：" + (string.IsNullOrEmpty(Password)
                       ? "**未设**"
                       : "已设，" + Password.Length.ToString(CultureInfo.InvariantCulture) + " 位") + "）";
        }

        /// <summary>**故意遮蔽密码**（见文件头规矩②）。</summary>
        /// <returns>与 <see cref="Describe"/> 相同的文本。</returns>
        public override string ToString()
        {
            return Describe();
        }
    }
}
