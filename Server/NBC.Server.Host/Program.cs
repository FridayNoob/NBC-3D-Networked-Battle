// ============================================================================
//  NBC.Server.Host —— 服务端进程入口
//  项目：3D联网战斗Demo
//  对应需求文档：Docs/01-项目需求文档.md §13.3、Docs\25-M3开工清单.md S3
//
//  ---------------------------------------------------------------------------
//  这一版做了 M0 留下的"M3 再接入"里的第一件：**网络监听 + 30Hz 主循环**
//  ---------------------------------------------------------------------------
//  M0 时它只打印版本就退出。现在它：
//      起监听 → 收握手（版本校验）→ 回心跳 → 每帧推 30Hz 逻辑帧 → Ctrl+C 优雅退出
//
//  ---------------------------------------------------------------------------
//  主循环的形状（照 M0 定下的规矩 SRV-04，一字不改）
//  ---------------------------------------------------------------------------
//      while (跑着) {
//          pump.Pump();        // 网络：收字节拆帧 → **只入队**；随后按路由回包/断开
//          Tick();             // 逻辑：按 TickScheduler 推进定长帧（30Hz）
//          Sleep(1);           // 别空转烧 CPU
//      }
//  ⚠️ "收→路由→回包"那段的**唯一实现**在 `NBC.Server.Core\ServerMessagePump.cs`
//     （探针用的是同一份）。这里只负责：起监听、打日志、推进逻辑帧、优雅退出。
//
//  ---------------------------------------------------------------------------
//  命令行（特意留了"跑 N 帧就退出"，好做自动化验证）
//  ---------------------------------------------------------------------------
//      dotnet run --project Server\NBC.Server.Host --no-build -- --port=7777
//      dotnet run --project NBC.Server.Host.dll -- --port=7777 --ticks=60
//  （命令写在 md 里，不写进 csproj 注释 —— 见 `_condition-probe` 那个 W1 记录）
// ============================================================================

using System.Reflection;
using NBC.Protocol;
using NBC.Server.Core;
using NBC.Server.Game;          // S5：`RoomBattleService`（权威世界 + 每 tick 快照下发）
using NBC.Shared;
using NBC.Shared.Net;

namespace NBC.Server.Host;

internal static class Program
{
    /// <summary>
    /// 这个 exe 真正会加载的四个程序集（"上一次编译"的判据来源，见 `LastBuildTime`）。
    /// </summary>
    private static readonly string[] ServerBinaries =
    {
        "NBC.Shared.dll",
        "NBC.Server.Core.dll",
        "NBC.Server.Game.dll",
        "NBC.Server.Host.dll",
    };

    /// <summary>
    /// 服务端进程入口。
    /// </summary>
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int port = ReadIntArg(args, "--port", TcpServerTransport.DefaultPort);
        int tickLimit = ReadIntArg(args, "--ticks", 0);          // 0 = 一直跑
        bool quiet = HasFlag(args, "--quiet");

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        Console.WriteLine("==============================================");
        Console.WriteLine("  NBC Server (Networked Battle Combat)");
        Console.WriteLine($"  Version : {version}");
        Console.WriteLine($"  Runtime : {Environment.Version}");
        Console.WriteLine($"  Built   : {BuildTime()}");
        Console.WriteLine($"  Machine : {MachineName()}");
        Console.WriteLine("==============================================");
        Console.WriteLine();
        Console.WriteLine($"[Check] NBC.Shared 可引用，共享层版本标识：{SharedInfo.Describe()}");
        Console.WriteLine($"[Check] 协议：{NetContract.Describe()}");

        // ⚠️ 负责人是**双击 bin 里的 exe** 启动的 —— 双击**永远不会编译**。
        //    所以"改了服务端代码却没重编"是**必然会发生**的事（2026-09-26 真发生过一次），
        //    而且**不报任何错**。这里改成主动报警：源码比产物新 → 大声喊出来。
        WarnIfStale();

        // ⚠️ 这里原来有一段"共享层 LogicTickRate 与协议 TickRate 不一致就警告"的运行时检查。
        //    编译器用 **CS0162（无法访问的代码）** 把它否掉了 —— 两个都是 `const`，
        //    比较在编译期就有结果，那段检查**永远不执行**。
        //    这正是"走不到的分支比没有分支更糟"。它该待的地方是测试，见
        //    `Tests\EditMode\Net\ProtocolTests.cs` 的 `TickRate_MatchesSharedLayerLogicRate`。

        // 配置表（S6）：副本阵容与数值都从表里来 —— 读不到就**不启动**（见下面 try/catch）
        if (!ServerTables.TryResolveConfigDir(out string configDir))
        {
            Console.WriteLine("[致命] 找不到配置表目录 `Configs\\Design`（从程序集所在目录往上找了 8 层）。");
            return 3;
        }

        if (!ServerTables.TryLoad(configDir, out ServerTables tables, out string tablesError))
        {
            Console.WriteLine("[致命] " + tablesError);
            return 3;
        }

        Console.WriteLine($"[Check] {tables.Describe()}");

        // ⚠️ 把"服务端**真正读到**的掉落配置"印出来（2026-09-23 负责人踩过这个坑）：
        //    服务端读的是 `Configs\Design\*.csv`（真源），而 Unity 侧读的是生成的 SO ——
        //    只改 SO 的话服务端**看不到**，表现是"打死了什么都不掉"，且不报任何错。
        foreach (int monsterId in tables.MonsterIdsWithDrops)
        {
            Console.WriteLine($"        掉落：{tables.DescribeDropsOf(monsterId)}");
        }

        // ⚠️ 同上，**阵容数值也自述**（2026-09-26 同一个坑的第二次：负责人在 SO 里把狼王血量
        //    改成 1000，跑起来还是 2000 —— 因为服务端读的是源 CSV，它看不到 SO）。
        //    ⇒ 横幅上直接念出"服务端读到的血量/攻击/移速"，改错地方一眼就能看出来。
        foreach (int monsterId in tables.MonsterIds)
        {
            Console.WriteLine($"        阵容：{tables.DescribeMonster(monsterId)}");
        }

        Console.WriteLine($"        阵容：{tables.DescribeHero(NBC.Server.Game.DungeonBattle.HeroConfigId)}");

        // ⚠️ 这里曾经想加一句"掉落 0 行就警告"，**实测发现走不到**，已删：
        //    `CsvSheet.LoadMany` 对"表头 4 行 + 至少 1 行数据"是硬校验，
        //    把 DropTable.csv 砍成只剩表头 → 直接 `[致命] DropTable.csv 少于 5 行` 且 return 3。
        //    也就是说"0 行"在启动前就被挡住了 —— 再加个兜底分支只是**看着像有保护**（见 CS0162 那次教训）。
        //    真要怀疑掉落没生效，看上面这几行就够：**服务端念的就是它真正读到的值**。

        var transport = new TcpServerTransport(port);
        var router = new ServerMessageRouter($"nbc-server/{version}");
        var pump = new ServerMessagePump(transport, router);

        // 心跳：显式注册（这就是 NET-02 要的"注册表"长什么样）
        router.Register(ClientMessage.PayloadOneofCase.Ping, HandlePing);

        // 房间与席位（S4）：规则在 `RoomRegistry`，接线与广播在 `RoomService`
        var rooms = new RoomService(transport, new RoomRegistry());
        rooms.RegisterHandlers(router);
        rooms.Note += line => Log(quiet, "[房间] " + line);

        // 状态同步（S5）：每个房间一个权威世界，每逻辑帧推进并下发全量快照（30Hz）
        var battles = new RoomBattleService(transport, rooms.Registry, tables);
        battles.Note += line => Log(quiet, "[战斗] " + line);

        // 输入上行（S5b）：客户端只发**意图**，服务端说了算（动多远、打不打得到）
        battles.RegisterHandlers(router);

        // 后续切片的消息先不注册 —— 客户端真发了会得到一句"还没实现 X"（不是静默丢弃）

        transport.SessionOpened += session =>
            Log(quiet, $"[接入] 会话 {session.SessionId} 来自 {session.RemoteEndPoint}");

        transport.SessionClosed += (session, reason) =>
            Log(quiet, $"[断开] 会话 {session.SessionId}" +
                       (session.PlayerId > 0 ? $"（玩家 {session.PlayerId}）" : "（还没握手）") +
                       $"：{reason}");

        pump.Note += line => Log(quiet, "[提示] " + line);

        try
        {
            transport.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[致命] 监听 {port} 失败：{ex.GetType().Name}：{ex.Message}");
            Console.WriteLine("       端口被占用？换一个：--port=7778");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine($"[就绪] 已监听 {transport.Port}（端口传 0 时由系统分配）");
        Console.WriteLine($"       路由已注册 {router.HandlerCount} 种消息：Handshake（内建）+ Ping + JoinRoom + LeaveRoom + Input");
        Console.WriteLine($"       每房 {rooms.Registry.Capacity} 个席位（D6：一个房间 = 一个副本实例）");
        Console.WriteLine("       按 Ctrl+C 退出。");
        Console.WriteLine();

        bool running = true;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;        // 别让运行时直接把进程砍掉，我们要优雅收尾
            running = false;
        };

        var scheduler = new TickScheduler(NetContract.TickRate);
        scheduler.Start();

        while (running)
        {
            // ---- ① 网络：收字节拆帧（只入队）→ 路由 → 回包/断开 --------------
            pump.Pump();

            // ---- ② 逻辑：推进定长帧（每帧 = 推进世界 + 下发一张快照） -------
            int ticks = scheduler.ConsumePendingTicks();

            for (int i = 0; i < ticks; i++)
            {
                battles.Tick();
            }

            if (tickLimit > 0 && scheduler.CurrentTick >= tickLimit)
            {
                // 自动化验证用：跑够 N 帧就自己退出（见文件头"命令行"）
                Log(quiet, $"[tick] 已达 --ticks={tickLimit}，退出。");
                running = false;
            }

            Thread.Sleep(1);
        }

        Console.WriteLine();
        Console.WriteLine("[收尾] 正在关闭监听……");
        transport.Stop();

        Console.WriteLine($"[统计] 接受连接 {transport.TotalAccepted}，断开 {transport.TotalClosed}，" +
                          $"握手成功 {pump.HandshakesAccepted}，被拒 {pump.HandshakesRejected}，" +
                          $"踢出 {pump.Kicks}，解不出 {pump.Undecodable}");
        Console.WriteLine($"[统计] 房间 {rooms.Registry.RoomCount} 个，席位表广播 {rooms.StateBroadcasts} 份");
        Console.WriteLine($"[统计] 战斗世界 {battles.BattleCount} 个，推进 {battles.TicksRun} 帧，" +
                          $"快照送出 {battles.SnapshotsSent} 份，回收世界 {battles.BattlesDropped} 个");
        Console.WriteLine($"[统计] 收到输入 {battles.InputsReceived} 条（拒绝 {battles.InputsRejected}），" +
                          $"普攻命中 {battles.AttacksLanded} 次（未打出去 {battles.AttacksRefused} 次）");
        Console.WriteLine($"[统计] 事件：伤害 {battles.HitsSent} 条、死亡 {battles.DeathsSent} 条、掉落 {battles.DropsSent} 条（M4-S1 起伤害/死亡也下发）");
        Console.WriteLine($"[统计] 收 {transport.FramesIn} 帧/{transport.BytesIn} B，" +
                          $"发 {transport.FramesOut} 帧/{transport.BytesOut} B，" +
                          $"逻辑帧 {scheduler.CurrentTick}");
        Console.WriteLine("[退出] 正常结束。");

        return 0;
    }

    // ====================================================================
    //  处理器
    // ====================================================================

    /// <summary>心跳：把客户端时间**原样**回去，另附服务端时间（客户端据此算 RTT）。</summary>
    /// <param name="session">会话。</param>
    /// <param name="message">消息。</param>
    /// <returns>结果。</returns>
    private static DispatchResult HandlePing(ClientSession session, ClientMessage message)
    {
        var pong = new Pong
        {
            ClientTimeMs = message.Ping.ClientTimeMs,
            ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        return DispatchResult.ReplyWith(new ServerMessage { Pong = pong });
    }

    // ====================================================================
    //  小工具
    // ====================================================================

    /// <summary>日志（`--quiet` 时什么都不打）。</summary>
    /// <param name="quiet">是否安静模式。</param>
    /// <param name="line">内容。</param>
    private static void Log(bool quiet, string line)
    {
        if (!quiet)
        {
            Console.WriteLine(line);
        }
    }

    /// <summary>读 `--key=value` 形式的整数参数。</summary>
    /// <param name="args">命令行。</param>
    /// <param name="key">键（含 `--`）。</param>
    /// <param name="fallback">缺省值。</param>
    /// <returns>值。</returns>
    private static int ReadIntArg(string[] args, string key, int fallback)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string text = args[i];

            if (text.StartsWith(key + "=", StringComparison.Ordinal) &&
                int.TryParse(text.Substring(key.Length + 1), out int value))
            {
                return value;
            }
        }

        return fallback;
    }

    /// <summary>命令行里有没有这个开关。</summary>
    /// <param name="args">命令行。</param>
    /// <param name="flag">开关。</param>
    /// <returns>有返回 true。</returns>
    private static bool HasFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// **源码比产物新**就大声警告（= 你双击的是一个旧 exe）。
    /// <para>2026-09-26 负责人报「重启服务端后，没打印新加的掉落日志」——根因就是他双击的
    /// `bin\...\NBC.Server.Host.exe` 是 08:56 编的，而源码 10:35 改的（我只做了 `-t:Compile`，
    /// **没写输出目录**）。他把服务端重启了，但**重启的是同一个旧 exe**，全程没有一行报错。</para>
    /// <para>⚠️ 判据只用**服务端源码（`Server\**\*.cs`）**，**不含** `Configs\Design\*.csv` ——
    /// 表是**运行时读**的，表比产物新是**正常**的，拿它报警会天天误报。
    /// ⚠️ 也**只扫 Host 真正依赖的四个工程**：`_net-probe` / `_condition-probe` / `NBC.Server.Lab`
    /// 这些"随手改、编了也不进 bin"的目录排除在外，否则我一改探针它就误报。</para>
    /// <para>部署环境（旁边没有源码）会静默跳过 —— 那是正常情况，不该报警。</para>
    /// </summary>
    private static void WarnIfStale()
    {
        try
        {
            string? root = FindRepoRoot();

            if (root == null)
            {
                return;
            }

            // ① 最新的一份服务端源码
            string[] projects = { "NBC.Shared", "NBC.Server.Core", "NBC.Server.Game", "NBC.Server.Host" };
            DateTime newestSource = DateTime.MinValue;
            string newestFile = string.Empty;

            foreach (string project in projects)
            {
                string dir = Path.Combine(root, "Server", project);

                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    if (file.Contains(@"\bin\") || file.Contains(@"\obj\"))
                    {
                        continue;
                    }

                    DateTime written = File.GetLastWriteTime(file);

                    if (written > newestSource)
                    {
                        newestSource = written;
                        newestFile = file;
                    }
                }
            }

            // ② 产物是什么时候编的
            DateTime newestBinary = LastBuildTime();

            if (newestSource == DateTime.MinValue || newestBinary == DateTime.MinValue || newestSource <= newestBinary)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
            Console.WriteLine("[警告] 你双击的是**旧的 exe**：源码比产物新，改的代码没有生效！");
            Console.WriteLine($"        最新源码：{newestFile}");
            Console.WriteLine($"                  {newestSource:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"        产物时间：{newestBinary:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine(@"        先编译再启动：dotnet build Server\NBC.Server.Host\NBC.Server.Host.csproj -m:1");
            Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
            Console.WriteLine();
        }
        catch (IOException)
        {
            // 取不到时间不算错，静默跳过（诊断信息**绝不该**把服务端拦在门外）
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 从程序集所在目录往上找**仓库根**（判据：`Server\NBC.Server.Host\Program.cs` 存在）。
    /// <para>往上找的层数与 <see cref="ServerTables.TryResolveConfigDir"/> 一致（8 层），
    /// 免得两个"找根"的算法各写一套、迟早不一致。</para>
    /// </summary>
    /// <returns>仓库根；找不到返回 null（部署环境就是这样）。</returns>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (int depth = 0; depth < 8 && dir != null; depth++)
        {
            string marker = Path.Combine(dir.FullName, "Server", "NBC.Server.Host", "Program.cs");

            if (File.Exists(marker))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// **上一次编译的时间**（四个 dll 里最新的那个时间戳）；取不到返回 <see cref="DateTime.MinValue"/>。
    /// <para>⚠️ 为什么取"最新"而不是"本程序集自己的时间"：改了 `NBC.Server.Game` 的代码时，
    /// MSBuild 只重编并拷贝 `NBC.Server.Game.dll`，**`NBC.Server.Host.dll` 可以一动不动**
    /// （实测 10:56:03 改源码、10:56:04 编完，Host.dll 还停在 10:55:56）。
    /// 那样横幅上的时间就会比真实编译时间**偏早**，和"我刚改代码的时间"一比容易得出错的结论。
    /// 也不能取"最旧"的：没改动的工程 MSBuild **跳过编译与拷贝**，其 dll 时间戳停在很久以前
    /// （实测 `NBC.Shared.dll` 一直停在 10:55:38）→ 天天误报，而**天天误报的警告等于没有警告**。</para>
    /// <para>⚠️ 这个函数是横幅 `Built :` 与"旧 exe 警告"的**唯一**判据来源：
    /// 同一个事实两处各算一遍，迟早会出现"横幅说新、警告说旧"。</para>
    /// </summary>
    /// <returns>编译时间；取不到返回 <see cref="DateTime.MinValue"/>。</returns>
    private static DateTime LastBuildTime()
    {
        DateTime newest = DateTime.MinValue;

        try
        {
            foreach (string name in ServerBinaries)
            {
                string path = Path.Combine(AppContext.BaseDirectory, name);

                if (!File.Exists(path))
                {
                    continue;
                }

                DateTime written = File.GetLastWriteTime(path);

                if (written > newest)
                {
                    newest = written;
                }
            }

            if (newest != DateTime.MinValue)
            {
                return newest;
            }

            // 兜底：四个 dll 一个都没找到（比如单文件发布）→ 用程序集自己的时间戳
            string self = Assembly.GetExecutingAssembly().Location;

            if (!string.IsNullOrEmpty(self) && File.Exists(self))
            {
                return File.GetLastWriteTime(self);
            }
        }
        catch (IOException)
        {
            // 取不到时间不算错（诊断信息**绝不该**把服务端拦在门外）
        }
        catch (UnauthorizedAccessException)
        {
        }

        return DateTime.MinValue;
    }

    /// <summary>
    /// 写给横幅看的"这个 exe 是什么时候编译出来的"。
    /// <para>为什么要打在横幅上：2026-09-26 负责人报"重启服务端后，没打印新加的掉落日志"，
    /// 根因不是代码 —— 是**他双击的是旧的 exe**（我上一次只做了 `-t:Compile` 编译校验，
    /// 没写输出目录，`bin` 里还是几小时前的那份）。这类"改了却没生效"**不会报任何错**，
    /// 只能靠"运行时自述它是谁"来抓。</para>
    /// </summary>
    /// <returns>本地时间字符串；取不到返回 `unknown`。</returns>
    private static string BuildTime()
    {
        DateTime built = LastBuildTime();
        return built == DateTime.MinValue ? "unknown" : built.ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>机器名（`Environment.MachineName` 在某些容器里会抛，所以兜一下）。</summary>
    /// <returns>名字。</returns>
    private static string MachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }
}
