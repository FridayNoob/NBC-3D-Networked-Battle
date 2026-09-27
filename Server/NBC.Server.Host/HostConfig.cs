// ============================================================================
//  HostConfig —— 服务端启动配置：`命令行 > appsettings.json > 内置默认值`
//  项目：3D联网战斗Demo   对应：需求文档 SRV-01
//
//  ---------------------------------------------------------------------------
//  为什么要有这个类（在这之前 Host **根本没读 appsettings.json**）
//  ---------------------------------------------------------------------------
//  它只认命令行（`--port` / `--ticks` / `--quiet`），而数据库那一块用的是
//  `DatabaseOptions` 的内置默认值 —— 那些默认值**恰好**和 `appsettings.json` 里的一致。
//
//      ⚠️ "恰好一致"是最坏的一种对：改配置的人改了 `appsettings.json` 里的端口/库名，
//         **什么都不会发生，而且什么都不会报**。他会去查代码、查网络、查防火墙……
//        （同族：`Docs\排查手册\06` 那条"改了却没生效"。）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 优先级：**命令行 > 配置文件 > 内置默认值**，而且**必须把来源印出来**
//  ---------------------------------------------------------------------------
//  · 命令行优先：`--port=7778` 是**运维/自动化**用的（`_net-probe` 与手顺书都靠它），
//    不能被一个配置文件悄悄盖掉。
//  · **来源要印出来**（`端口 9000（appsettings.json）`）：
//    否则"我改的到底生效了没有"又要靠猜 —— 而这正是本类存在的理由。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 它顺手**让一个死掉的检查复活**（值得单独说）
//  ---------------------------------------------------------------------------
//  `Program.cs` 里原来有一段"共享层 `LogicTickRate` 与协议 `TickRate` 不一致就警告"，
//  被编译器用 **CS0162（无法访问的代码）** 否掉了 —— 两边都是 `const`，
//  比较在编译期就有结果，那段代码**永远不执行**。
//
//  ⇒ 现在 `Simulation.LogicTickRate` 是**运行期从文件读来的**，
//     "文件里写的"和"协议常量"**真的可能不一致**了 —— 那个检查**重新有意义**。
//     （配置文件里也能改协议常量做不到的事：让人把 30 写成 20。）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using NBC.Server.Data;
using NBC.Shared.Net;

namespace NBC.Server.Host;

/// <summary>服务端启动配置（见文件头：优先级与"来源要印出来"）。</summary>
internal sealed class HostConfig
{
    /// <summary>监听端口。</summary>
    public int ListenPort { get; private set; } = 9000;

    /// <summary>端口是哪来的（`命令行` / `appsettings.json` / `内置默认值`）。</summary>
    public string ListenPortSource { get; private set; } = "内置默认值";

    /// <summary>配置文件里写的 tick 率（0 = 文件里没写）。</summary>
    public int ConfiguredTickRate { get; private set; }

    /// <summary>日志级别（原样读出，只用于印出来 + 推 quiet）。</summary>
    public string LogLevel { get; private set; } = "(未配置)";

    /// <summary>配置里要求安静模式（`Logging.Level` 是 Warning/Error 时）。</summary>
    public bool QuietByConfig { get; private set; }

    /// <summary>数据库连接参数（已按优先级填好）。</summary>
    public DatabaseOptions Database { get; } = new DatabaseOptions();

    /// <summary>实际读到的配置文件路径（没找到时为 null）。</summary>
    public string? ConfigFilePath { get; private set; }

    /// <summary>读配置过程中的提示（启动时打印；**不静默**）。</summary>
    public readonly List<string> Notes = new List<string>();

    /// <summary>
    /// 按 `命令行 > appsettings.json > 内置默认值` 组装配置。
    /// <para>⚠️ 全部读失败也**不抛**：配置读不出来不该让服务端起不来（它有一堆默认值），
    /// 但每一处降级都会往 <see cref="Notes"/> 里写一条。</para>
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>配置。</returns>
    public static HostConfig Load(string[] args)
    {
        var config = new HostConfig();
        IConfigurationRoot? file = config.TryOpenAppSettings();

        // ---- ① 先从文件读（最低优先级的那一层）----
        if (file != null)
        {
            string? portText = file["Server:ListenPort"];

            if (int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int filePort)
                && filePort > 0 && filePort <= 65535)
            {
                config.ListenPort = filePort;
                config.ListenPortSource = "appsettings.json";
            }
            else if (!string.IsNullOrWhiteSpace(portText))
            {
                config.Notes.Add($"⚠️ appsettings.json 里的 `Server.ListenPort` = 「{portText}」不是合法端口，**已忽略**（用默认 {config.ListenPort}）。");
            }

            string? tickText = file["Simulation:LogicTickRate"];

            if (int.TryParse(tickText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fileTick))
            {
                config.ConfiguredTickRate = fileTick;
            }

            config.LogLevel = file["Logging:Level"] ?? "(未配置)";
            config.QuietByConfig =
                string.Equals(config.LogLevel, "Warning", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(config.LogLevel, "Error", StringComparison.OrdinalIgnoreCase);

            // ---- 数据库（**这才是"改 appsettings 没生效"最容易踩的那一块**）----
            config.ReadDatabase(file);
        }

        // ---- ② 命令行盖上去（最高优先级）----
        int cliPort = ReadIntArg(args, "--port", 0);

        if (cliPort > 0)
        {
            config.ListenPort = cliPort;
            config.ListenPortSource = "命令行 --port";
        }

        // ---- ③ 环境变量的密码（它比文件优先，且**绝不进 Git**）----
        if (config.Database.ApplyPasswordFromEnvironment())
        {
            config.Notes.Add("数据库密码取自环境变量 `NBC_DB_PASSWORD`（不在配置文件里，也不会进仓库）。");
        }

        return config;
    }

    /// <summary>
    /// 检查配置文件里的 tick 率与**协议常量**是否一致。
    /// <para>⚠️ 这个检查在"两边都是 `const`"的年代**永远执行不到**（CS0162）——
    /// 现在一边来自文件，它才真的可能触发。见文件头。</para>
    /// </summary>
    /// <returns>不一致时返回一句人话；一致或文件没写时返回 null。</returns>
    public string? DescribeTickRateMismatch()
    {
        if (ConfiguredTickRate <= 0 || ConfiguredTickRate == NetContract.TickRate)
        {
            return null;
        }

        return $"⚠️ appsettings.json 的 `Simulation.LogicTickRate` = {ConfiguredTickRate}，" +
               $"而协议常量 `NetContract.TickRate` = {NetContract.TickRate}。\n" +
               $"        服务端**用协议常量**（{NetContract.TickRate}Hz）—— 那个值会随 `HandshakeAck` 发给客户端，\n" +
               "        改它等于改协议（还要动 `Shared\\Net\\NetContract.cs` 并升 `CONTRACT_VERSION`）。\n" +
               "        ⇒ 配置文件里这个字段只是**文档**；两边不一致说明有一边该改。";
    }

    /// <summary>数据库配置的来源描述（印出来用）。</summary>
    /// <returns>例如 `Database 段（appsettings.json）` 或 `内置默认值`。</returns>
    public string DescribeDatabaseSource()
    {
        return ConfigFilePath != null ? "Database 段（appsettings.json）" : "内置默认值";
    }

    /// <summary>打开 `appsettings.json`（找不到就返回 null 并记一条提示）。</summary>
    /// <returns>配置根；没找到返回 null。</returns>
    private IConfigurationRoot? TryOpenAppSettings()
    {
        // ⚠️ 先看**程序集所在目录**（csproj 里 `CopyToOutputDirectory=PreserveNewest` 会把它拷过来），
        //    再看当前工作目录 —— 这样"双击 exe"和"从仓库根 dotnet run"两种起法都能找到。
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
        };

        string? found = null;

        for (int i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
            {
                found = candidates[i];
                break;
            }
        }

        if (found == null)
        {
            Notes.Add("⚠️ 没找到 `appsettings.json`（找过程序集目录与当前目录）⇒ **全部用内置默认值**。\n" +
                      "        想改端口/数据库，请在 exe 旁边放一份配置文件（或用命令行 `--port`）。");
            return null;
        }

        ConfigFilePath = found;

        try
        {
            return new ConfigurationBuilder()
                .AddJsonFile(found, optional: true, reloadOnChange: false)
                .Build();
        }
        catch (Exception ex)
        {
            // ⚠️ 配置读不出来**不该让服务端起不来**（它有一堆默认值），但**必须说出来**
            Notes.Add($"⚠️ `{found}` 解析失败，**已忽略并改用默认值**：{ex.Message}");
            ConfigFilePath = null;
            return null;
        }
    }

    /// <summary>把 `Database` 段读进 <see cref="Database"/>（缺项保持默认值）。</summary>
    /// <param name="file">配置根。</param>
    private void ReadDatabase(IConfigurationRoot file)
    {
        Database.Host = ReadString(file, "Database:Host", Database.Host);
        Database.Database = ReadString(file, "Database:Database", Database.Database);
        Database.User = ReadString(file, "Database:User", Database.User);

        // ⚠️ **密码不从文件读**（文件会进 Git）—— 只认环境变量，见 `DatabaseOptions` 文件头。
        //    文件里那一格永远是空串；万一有人填了，这里**故意不采信**并提醒一句。
        string? filePassword = file["Database:Password"];

        if (!string.IsNullOrEmpty(filePassword))
        {
            Notes.Add("⚠️ `appsettings.json` 的 `Database.Password` 里填了东西 —— **本程序不采信它**（那会进 Git）。\n" +
                      "        密码请走环境变量 `NBC_DB_PASSWORD`。");
        }

        Database.Port = ReadInt(file, "Database:Port", Database.Port);
        Database.ConnectionTimeoutSeconds = ReadInt(file, "Database:ConnectionTimeoutSeconds", Database.ConnectionTimeoutSeconds);
        Database.CommandTimeoutSeconds = ReadInt(file, "Database:CommandTimeoutSeconds", Database.CommandTimeoutSeconds);
        Database.MaxPoolSize = ReadInt(file, "Database:MaxPoolSize", Database.MaxPoolSize);
    }

    /// <summary>读一个字符串项（空/缺失时用兜底值）。</summary>
    /// <param name="file">配置根。</param>
    /// <param name="key">键。</param>
    /// <param name="fallback">兜底值。</param>
    /// <returns>值。</returns>
    private static string ReadString(IConfigurationRoot file, string key, string fallback)
    {
        string? value = file[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value!;
    }

    /// <summary>读一个整数项（解析不出来时用兜底值，并记一条提示）。</summary>
    /// <param name="file">配置根。</param>
    /// <param name="key">键。</param>
    /// <param name="fallback">兜底值。</param>
    /// <returns>值。</returns>
    private int ReadInt(IConfigurationRoot file, string key, int fallback)
    {
        string? text = file[key];

        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        Notes.Add($"⚠️ `appsettings.json` 的 `{key}` = 「{text}」不是整数，**已忽略**（用 {fallback}）。");
        return fallback;
    }

    /// <summary>读 `--name=N` 形式的整数参数（没有/解析不出来则用兜底值）。</summary>
    /// <param name="args">命令行。</param>
    /// <param name="name">参数名（含 `--`）。</param>
    /// <param name="fallback">兜底值。</param>
    /// <returns>值。</returns>
    private static int ReadIntArg(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];

            if (!a.StartsWith(name, StringComparison.Ordinal))
            {
                continue;
            }

            string tail = a.Substring(name.Length).TrimStart('=', ':');

            if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
        }

        return fallback;
    }
}
