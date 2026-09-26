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
        Console.WriteLine($"  Machine : {MachineName()}");
        Console.WriteLine("==============================================");
        Console.WriteLine();
        Console.WriteLine($"[Check] NBC.Shared 可引用，共享层版本标识：{SharedInfo.Describe()}");
        Console.WriteLine($"[Check] 协议：{NetContract.Describe()}");

        // ⚠️ 这里原来有一段"共享层 LogicTickRate 与协议 TickRate 不一致就警告"的运行时检查。
        //    编译器用 **CS0162（无法访问的代码）** 把它否掉了 —— 两个都是 `const`，
        //    比较在编译期就有结果，那段检查**永远不执行**。
        //    这正是"走不到的分支比没有分支更糟"。它该待的地方是测试，见
        //    `Tests\EditMode\Net\ProtocolTests.cs` 的 `TickRate_MatchesSharedLayerLogicRate`。

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
        var battles = new RoomBattleService(transport, rooms.Registry);
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
