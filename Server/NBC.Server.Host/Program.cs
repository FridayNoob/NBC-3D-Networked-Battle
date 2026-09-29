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
using Google.Protobuf;           // `ToByteArray()`（少了它报 CS1061 —— 本项目撞过好几次）
using NBC.Protocol;
using NBC.Framework.Log;        // SRV-14：日志（**与客户端编同一份源码**，见 NBC.Server.Core.csproj）
using NBC.Server.Core;
using NBC.Server.Data;          // SRV-13：战绩落库（`BattleRecordDao` / `BattleRecordWriter`）
using NBC.Server.Game;
using NBC.Shared.Sim;      // M4-S4 S4-c：`LockstepRoster`（按 player_id 升序生成开局实体表）          // S5：`RoomBattleService`（权威世界 + 每 tick 快照下发）
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
    /// <summary>
    /// 服务端日志（**唯一写日志的出口**）。
    /// <para>⚠️ 它让"控制台"与"日志文件"内容一致 —— 不会出现"控制台有、文件里没有"
    /// 这种排查时最气人的事。`Log()` 与启动横幅都走它。</para>
    /// <para>⚠️ 静态字段：`Main` 里的一堆 lambda 要能拿到它。控制台程序的入口，
    /// 生命周期与进程相同，可以接受。</para>
    /// </summary>
    private static ServerLog s_log = new ServerLog(LogLevel.Info, "Logs", false, true);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // SRV-01：配置来源 = `命令行 > appsettings.json > 内置默认值`（见 `HostConfig` 文件头）
        HostConfig config = HostConfig.Load(args);

        // SRV-14：日志出口 —— 级别/落盘/按天分文件全部来自配置。
        // ⚠️ 命令行 `--quiet` 与配置里的 `Level=Warning` 是**同一个旋钮**（都抬到 Warning）。
        LogLevel effectiveLevel = HasFlag(args, "--quiet") || config.QuietByConfig
            ? LogLevel.Warning
            : config.ParsedLogLevel;

        s_log = new ServerLog(effectiveLevel, config.LogDirectory, config.WriteLogToFile, config.SplitLogByDay);
        s_log.Write(LogLevel.Info, LogChannel.General,
            "===== 服务端启动 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " =====");
        s_log.Write(LogLevel.Info, LogChannel.General, "日志目标：" + s_log.Describe());

        // ⚠️ 兜底：无论从哪条 return 出去（含 `[致命]` 的早退），都刷一次日志缓冲。
        //    `Main` 里有好几个 return 点，逐个补 Flush 迟早漏一个。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => s_log.Flush();
        int port = config.ListenPort;
        int tickLimit = ReadIntArg(args, "--ticks", 0);          // 0 = 一直跑
        bool quiet = HasFlag(args, "--quiet") || config.QuietByConfig;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        // ⚠️ 横幅**先拼成一段文本**，再同时写控制台与日志文件 ——
        //    这样"文件里有没有这次启动的横幅"不再靠人记得去补一句。
        var banner = new System.Text.StringBuilder();
        banner.AppendLine("==============================================");
        banner.AppendLine("  NBC Server (Networked Battle Combat)");
        banner.AppendLine($"  Version : {version}");
        banner.AppendLine($"  Runtime : {Environment.Version}");
        banner.AppendLine($"  Built   : {BuildTime()}");
        banner.AppendLine($"  Machine : {MachineName()}");
        banner.AppendLine("==============================================");
        banner.AppendLine($"[Check] NBC.Shared 可引用，共享层版本标识：{SharedInfo.Describe()}");
        banner.Append($"[Check] 协议：{NetContract.Describe()}");

        Say(banner.ToString());
        Say(string.Empty);
        s_log.Write(LogLevel.Info, LogChannel.General, banner.ToString());

        // ⚠️ **配置的来源要印出来** —— 否则"我改了 appsettings 到底生效没有"又要靠猜，
        //    而那正是 `HostConfig` 存在的理由（改了没生效是本项目踩过的坑）。
        Say($"[配置] 文件：{config.ConfigFilePath ?? "**没找到**（全部用内置默认值）"}");
        Say($"[配置] 监听端口 {port}（来源：{config.ListenPortSource}）");
        Say($"[配置] 数据库参数来源：{config.DescribeDatabaseSource()}；日志级别：{config.LogLevelText}" +
                          (config.QuietByConfig ? "（⇒ 安静模式）" : string.Empty));

        foreach (string note in config.Notes)
        {
            Say("       " + note.Replace("\n", "\n       "));
        }

        // ⚠️ 这个检查在"两边都是 const"的年代**永远执行不到**（编译器 CS0162 直接判死），
        //    因为 `Simulation.LogicTickRate` 现在是从文件读来的，它**重新有意义**了。见 `HostConfig` 文件头。
        string? tickMismatch = config.DescribeTickRateMismatch();

        if (tickMismatch != null)
        {
            Say("       " + tickMismatch.Replace("\n", "\n       "));
        }

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
            Say("[致命] 找不到配置表目录 `Configs\\Design`（从程序集所在目录往上找了 8 层）。");
            return 3;
        }

        if (!ServerTables.TryLoad(configDir, out ServerTables tables, out string tablesError))
        {
            Say("[致命] " + tablesError);
            return 3;
        }

        Say($"[Check] {tables.Describe()}");

        // 任务 / 成就 / 条件 / 奖励四张表（M4-S3 服务端权威化）。
        // ⚠️ 与战斗表**分开读、失败后果也不同**：战斗表读不到**拒绝启动**（进副本没怪），
        //    而这四张表读不到只是"成就判定没有配置"——服务端照样能打，所以只**警告**。
        QuestTables? questTables = null;

        if (QuestTables.TryLoad(configDir, out QuestTables loadedQuests, out string questError))
        {
            questTables = loadedQuests;
            Say($"[Check] 任务成就表：{loadedQuests.Describe()}");
        }
        else
        {
            Say("[警告] 读不到任务/成就/奖励表，**服务端权威的成就判定不会工作**：");
            Say("       " + questError);
        }

        // ⚠️ 把"服务端**真正读到**的掉落配置"印出来（2026-09-23 负责人踩过这个坑）：
        //    服务端读的是 `Configs\Design\*.csv`（真源），而 Unity 侧读的是生成的 SO ——
        //    只改 SO 的话服务端**看不到**，表现是"打死了什么都不掉"，且不报任何错。
        foreach (int monsterId in tables.MonsterIdsWithDrops)
        {
            Say($"        掉落：{tables.DescribeDropsOf(monsterId)}");
        }

        // ⚠️ 同上，**阵容数值也自述**（2026-09-26 同一个坑的第二次：负责人在 SO 里把狼王血量
        //    改成 1000，跑起来还是 2000 —— 因为服务端读的是源 CSV，它看不到 SO）。
        //    ⇒ 横幅上直接念出"服务端读到的血量/攻击/移速"，改错地方一眼就能看出来。
        foreach (int monsterId in tables.MonsterIds)
        {
            Say($"        阵容：{tables.DescribeMonster(monsterId)}");
        }

        Say($"        阵容：{tables.DescribeHero(NBC.Server.Game.DungeonBattle.HeroConfigId)}");

        // ⚠️ 这里曾经想加一句"掉落 0 行就警告"，**实测发现走不到**，已删：
        //    `CsvSheet.LoadMany` 对"表头 4 行 + 至少 1 行数据"是硬校验，
        //    把 DropTable.csv 砍成只剩表头 → 直接 `[致命] DropTable.csv 少于 5 行` 且 return 3。
        //    也就是说"0 行"在启动前就被挡住了 —— 再加个兜底分支只是**看着像有保护**（见 CS0162 那次教训）。
        //    真要怀疑掉落没生效，看上面这几行就够：**服务端念的就是它真正读到的值**。

        var transport = new TcpServerTransport(port);

        // ------------------------------------------------------------------
        //  数据库（账号表 + 战绩落库）
        // ------------------------------------------------------------------
        //  历史：这一段原来是"玩家档案槽位"（SRV-06 的最小可用版）——
        //  用来解决"累计连过 4 次之后战绩明细被外键静默跳过"那个会咬人的欠账。
        //  ⚠️ 2026-09-27 **SRV-17a 把它退役了**：真正的原因不是"槽位不够"，
        //     而是**游客拿了账号的档案 id**（见下面的说明）。现在游客发负数编号。
        //
        //  ⚠️ 配置来源：`appsettings.json` 的 `Database` 段（SRV-01 起**真的读了**），
        //     密码走环境变量 `NBC_DB_PASSWORD` —— 见 `HostConfig` 与 `DatabaseOptions`。
        //     命令行 `--port` 仍然优先于配置文件（运维/自动化靠它）。
        //
        //  ⚠️ 启动时**同步探一次活**（`.GetAwaiter().GetResult()`）：这发生在**主循环之前**，
        //     阻塞几百毫秒换来"启动横幅明说数据库通不通"，是划算的。
        //     主循环里**绝不能**这么写 —— 那正是 `BattleRecordWriter` 存在的理由。
        var dbOptions = config.Database;

        DbConnectionFactory? dbFactory = null;
        BattleRecordDao? recordDao = null;
        AccountDirectory? accounts = null;
        AccountDao? accountDao = null;

        if (string.IsNullOrEmpty(dbOptions.Password))
        {
            Say("[数据库] 未接：没设 NBC_DB_PASSWORD（战绩不落库；**账号登录用不了**，只能以游客进）。");
        }
        else
        {
            try
            {
                dbFactory = new DbConnectionFactory(dbOptions);
                PingResult ping = dbFactory.PingAsync().GetAwaiter().GetResult();

                if (ping.Ok)
                {
                    recordDao = new BattleRecordDao(dbFactory);

                    // ⚠️ 账号表在**启动时读一次**（M4-S3）：登录跑在网络泵的握手里，
                    //    在那里等一次 MySQL 往返会把整个服务端卡住。
                    //    代价：**新注册的账号要重启服务端才认**（见 `AccountDirectory` 文件头）。
                    accounts = AccountDirectory.Load(dbFactory);
                    accountDao = new AccountDao(dbFactory);

                    Say($"[数据库] 已接：{dbOptions.Describe()}");
                    Say($"[数据库] {accounts.Describe()}");

                    if (accounts.WithoutProfileCount > 0)
                    {
                        // ⚠️ **要显眼**：这些账号永远登不进来，而粗看只会以为"密码错了"
                        Say($"[数据库] ⚠️ 有 {accounts.WithoutProfileCount} 个账号**没有玩家档案** —— " +
                            "它们登不进来（不是密码问题）。请检查 Docs\\08 的种子数据。");
                    }
                }
                else
                {
                    Say("[数据库] 连不上，**战绩不落库、账号登录用不了**（服务端照常跑）：");
                    Say("         " + (ping.Reason ?? string.Empty).Replace("\n", "\n         "));
                }
            }
            catch (Exception ex)
            {
                Say("[数据库] 初始化失败，**降级运行**（服务端照常跑）：" + ex.Message);
                recordDao = null;
                accounts = null;
                accountDao = null;
            }
        }

        // ⚠️ 2026-09-27（SRV-17a）：这里**原来还有** `PlayerProfileSlots`（"领一个空闲档案槽位"）。
        //    它随真实登录一起退役了 —— 游客现在拿**负数编号**，见 `ServerMessageRouter.HandleHandshake`。
        //    一句话理由：那个池子发出去的 1~4 恰好是**账号的档案 id**，
        //    于是游客的战绩被记进了别人的累计里（真实发生过的数据污染）。
        var router = new ServerMessageRouter($"nbc-server/{version}", accounts);
        var pump = new ServerMessagePump(transport, router);

        // 心跳：显式注册（这就是 NET-02 要的"注册表"长什么样）
        router.Register(ClientMessage.PayloadOneofCase.Ping, HandlePing);

        // 房间与席位（S4）：规则在 `RoomRegistry`，接线与广播在 `RoomService`
        var rooms = new RoomService(transport, new RoomRegistry());
        rooms.RegisterHandlers(router);
        rooms.Note += line => Log(quiet, "[房间] " + line);

        // 状态同步（S5）：每个房间一个权威世界，每逻辑帧推进并下发全量快照（30Hz）
        var battles = new RoomBattleService(transport, rooms.Registry, tables);

        // ==================================================================
        //  M4-S4 S4-c：**锁步（帧同步）服务端**（§三十三）
        //  ⚠️ 它**不依赖数据库** ⇒ 与成就/任务那两块分开构造（那两块没库就不接）。
        //  ⚠️ tick 归属：挂进现有主循环同一格（**不另起计时器**）—— 两个计时源会在同一房间
        //     交错调 AdvanceTick，`StepsExecuted` 与广播帧数立刻分叉。
        // ==================================================================
        var lockstep = new LockstepService(transport, rooms.Registry);
        lockstep.Note += line => Log(quiet, "[锁步] " + line);
        lockstep.RegisterHandlers(router);

        // 固定输入延迟（tick）；与 LockstepScheduler 的默认语义一致（见 §32.2）
        const int lockstepDelayTicks = 2;
        battles.Note += line => Log(quiet, "[战斗] " + line);

        // 输入上行（S5b）：客户端只发**意图**，服务端说了算（动多远、打不打得到）
        battles.RegisterHandlers(router);

        // ------------------------------------------------------------------
        //  SRV-13：把战绩接到数据层上
        // ------------------------------------------------------------------
        //  ⚠️ 数据库的初始化已经**挪到上面**（和账号表一起）——
        //     两处各读一次配置迟早不一致（"为什么战绩能写、账号表读不到"）。
        //     这里只做**接线**：一局打完 → 入队 → 后台写。
        var recordWriter = new BattleRecordWriter(recordDao);
        recordWriter.Note += line => Log(quiet, "[战绩] " + line);
        battles.BattleFinished += recordWriter.Submit;   // ⚠️ 这一行才是 SRV-13 真正"生效"的地方

        // ------------------------------------------------------------------
        //  M4-S3 服务端权威：成就判定（§二十一）
        // ------------------------------------------------------------------
        //  ⚠️ **判定逻辑一行没改** —— 用的还是共享层的 `ConditionTracker`（和客户端同一份源码），
        //     只是换了个执行者：**服务端说了算**，并且接上了 `condition_progress` + `reward_granted`。
        //  ⚠️ 没接数据库时**故意不接**（`Track` 直接返回 false）：只判不记会让人以为成就系统好了。
        AchievementAuthority? achievements = null;

        // M4-S3 收口（§21.4 未做#3 的第一刀）：**任务权威**（接取/完成/交付 + 发奖）
        QuestAuthority? quests = null;

        // M4-S3 收口（未做#2）：把权威进度推给客户端的那条线（**Host 与探针共用**）
        ProgressBroadcaster? progress = null;

        // M4-S3 §二十六：任务动作（接取 / 交付）处理器 + 任务状态推送线
        // ⚠️ 两者都**只在这里构造一次**，与探针共用同一份类（别抄一份进探针）。
        QuestStateBroadcaster? questStates = null;
        QuestService? questService = null;

        if (questTables != null && dbFactory != null)
        {
            DbConnectionFactory liveFactory = dbFactory;

            achievements = new AchievementAuthority(
                questTables,
                playerId => new CachingConditionProgressStore(new ConditionProgressDao(liveFactory), playerId),
                playerId => new MySqlRewardLedger(new RewardLedgerDao(liveFactory), playerId));

            achievements.Note += line => Log(quiet, "[成就] " + line);
            battles.Achievements = achievements;

            // ================================================================
            //  §二十五：任务权威（接取 / 完成 / 交付 + 发奖）
            //  ⚠️ 它要的是**同一批战斗事实** —— 通过 `IProgressFactSink` 注册，
            //     而不是在 `RoomBattleService` 里再写一遍 `ApplyFact`（加消费者不用改那个类）。
            //  ⚠️ 三个存放处缺一不可：进度（条件累计）、台账（发奖幂等）、**任务状态**（到哪一步了）。
            //     少一个就退化成"判定说完成了、重启就没"。
            // ================================================================
            quests = new QuestAuthority(
                questTables,
                playerId => new CachingConditionProgressStore(new ConditionProgressDao(liveFactory), playerId),
                playerId => new MySqlRewardLedger(new RewardLedgerDao(liveFactory), playerId),
                playerId => new CachingQuestStateStore(new QuestStateDao(liveFactory), playerId));

            quests.Note += line => Log(quiet, "[任务] " + line);
            battles.AddFactSink(quests);

            // ================================================================
            //  M4-S3 收口（§21.4 未做#2）：把**权威进度**推给客户端
            //  —— 收掉"两个账房"：在此之前任务/成就进度**客户端自己也算一份**，
            //     两边一致只是"碰巧"，一旦不一致**不报错**。
            // ================================================================
            // ⚠️ 逻辑在 `ProgressBroadcaster` 里，**Host 与探针共用同一份** ——
            //    抄一份进探针，验的就是抄本（M3-A 的 `ServerMessagePump` 同一条理由）。
            progress = new ProgressBroadcaster(transport, rooms.Registry, achievements);
            progress.Note += line => Log(quiet, "[进度] " + line);

            // ================================================================
            //  §二十六：任务动作（接取 / 交付）+ **任务状态同步**
            //  ⚠️ 成功时处理器**不回包**：权威在同一调用栈里同步喊 `StateChanged`，
            //     广播器当场发全量 ⇒ "点接取"与"条件在战斗里打满"是**同一条发送路径**。
            //     （回包 + 另外推送会**发两遍**，而且两次读取可能顺序相反。）
            // ================================================================
            questStates = new QuestStateBroadcaster(transport, rooms.Registry, quests);
            questStates.Note += line => Log(quiet, "[任务状态] " + line);

            questService = new QuestService(quests, questStates);
            questService.Note += line => Log(quiet, "[任务请求] " + line);
            questService.RegisterHandlers(router);

            // 一局结束就冲一次库（**fire-and-forget**：这里是 Tick 线程，绝不能等 IO）
            // ⚠️ lambda 的参数**不能叫 `_`** —— 那样里面的 `_ = 任务` 会被解析成"给这个参数赋值"，
            //    报一句完全看不懂的 `无法将 Task<bool> 转换为 BattleRecordDraft`（本次实测踩到）。
            battles.BattleFinished += draft =>
            {
                _ = achievements.FlushAllAsync(2000);
                _ = quests.FlushAllAsync(2000);
            };

            Say($"[成就] 服务端权威判定已接：{questTables.Describe()}");
            Say($"[任务] 权威 + 动作路由（接取/交付）已接：{questTables.Describe()}");
        }
        else
        {
            Say($"[成就] **没接**：{(questTables == null ? "读不到任务成就表" : "服务端没接数据库")} —— " +
                "成就判定仍然只发生在客户端（这是 M4-S3 之前的旧行为）。");
        }

        // ------------------------------------------------------------------
        //  M4-S3：登录审计（SRV-06）
        // ------------------------------------------------------------------
        //  登录本身只查内存（`AccountDirectory`）；这里记的是
        //  `account.last_login_at` —— 让"登录确实发生过"在**库里**留下证据。
        //  ⚠️ 与战绩同一个形状：**只入队**，写库在别的线程，失败不让服务端倒。
        var loginAudit = new LoginAuditWriter(accountDao);
        loginAudit.Note += line => Log(quiet, "[登录] " + line);

        // ⚠️ 这一行才是登录审计"生效"的地方；顺便让**日志里留一条登录记录**
        //    （库里那条 `last_login_at` 是"证据"，日志这条是"现场"）。
        router.AccountLoggedIn += result =>
        {
            loginAudit.Submit(result.AccountId);
            Log(quiet, $"[登录] 账号 {result.AccountId} 登录成功 → 玩家 {result.PlayerId}（{result.Nickname}）");

            // M4-S3 权威：开始追踪这个玩家的成就（**异步读进度**，中间收到的事实会排队 —— 见那个类的文件头）
            achievements?.Track(result.PlayerId, result.Nickname);

            // 任务权威同理（游客不会走到这里：登录才有档案）
            quests?.Track(result.PlayerId, result.Nickname);
        };

        // 后续切片的消息先不注册 —— 客户端真发了会得到一句"还没实现 X"（不是静默丢弃）

        transport.SessionOpened += session =>
            Log(quiet, $"[接入] 会话 {session.SessionId} 来自 {session.RemoteEndPoint}");

        transport.SessionClosed += (session, reason) =>
        {
            // ⚠️ 2026-09-27（SRV-17a）：这里**原来有一句 `slots?.Release(session.PlayerId)`**。
            //    现在**什么都不用做** —— 游客编号属于**连接**，连接没了就没了，不需要归还。
            //    （旧写法必须记得还，漏一次就永久泄漏；"不需要还"是这次改动顺带买到的。
            //     登录玩家的身份属于**账号**，本来就跟连接无关。）
            // M4-S3 权威：断开就停止追踪（内部会**异步**把脏进度冲回库，不在断开回调里等 IO）
            achievements?.Untrack(session.PlayerId);
            quests?.Untrack(session.PlayerId);

            Log(quiet, $"[断开] 会话 {session.SessionId}" +
                       (session.PlayerId != 0 ? $"（玩家 {session.PlayerId}）" : "（还没握手）") +
                       $"：{reason}");
        };

        pump.Note += line => Log(quiet, "[提示] " + line);

        try
        {
            transport.Start();
        }
        catch (Exception ex)
        {
            Say($"[致命] 监听 {port} 失败：{ex.GetType().Name}：{ex.Message}");
            Say("       端口被占用？换一个：--port=7778");
            return 2;
        }

        Say(string.Empty);
        Say($"[就绪] 已监听 {transport.Port}（端口传 0 时由系统分配）");
        Say($"       路由已注册 {router.HandlerCount} 种消息：Handshake（内建）+ Ping + JoinRoom + LeaveRoom + Input + QuestAction（§二十六）+ LockstepInput（§三十三）");
        Say($"       每房 {rooms.Registry.Capacity} 个席位（D6：一个房间 = 一个副本实例）");
        Say("       按 Ctrl+C 退出。");
        Say(string.Empty);

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

                // ---- ②′ 锁步：同一格里推进（每 tick **一次**）----------------
                // ⚠️ **不要**在外面套 `while`：`LockstepService.Tick()` 内部已按房间各调一次
                //    `AdvanceTick`，而 `AdvanceTick` 自己会把服务端时钟 ++ ⇒ 稳态恒返回 true
                //    （2026-09-28 修掉的死循环就是这个形状）。
                // ⚠️ 开局**懒执行**：房间满了、且还没开过局 ⇒ 开一次。
                //    "只开局一次"的闸在 `LockstepService.StartRoom` 里（调用方会忘，闸不会忘）。
                foreach (Room lockstepRoom in rooms.Registry.Rooms)
                {
                    if (!lockstepRoom.IsFull || lockstep.HasRoom(lockstepRoom.RoomId))
                    {
                        continue;
                    }

                    var members = new List<long>(lockstepRoom.SeatCount);

                    for (int s = 0; s < lockstepRoom.Seats.Count; s++)
                    {
                        members.Add(lockstepRoom.Seats[s].Session.PlayerId);
                    }

                    lockstep.StartRoom(lockstepRoom.RoomId, LockstepRoster.Build(members), lockstepDelayTicks);
                }

                lockstep.Tick();
            }

            if (tickLimit > 0 && scheduler.CurrentTick >= tickLimit)
            {
                // 自动化验证用：跑够 N 帧就自己退出（见文件头"命令行"）
                Log(quiet, $"[tick] 已达 --ticks={tickLimit}，退出。");
                running = false;
            }

            Thread.Sleep(1);
        }

        Say(string.Empty);
        Say("[收尾] 正在关闭监听……");

        // ⚠️ 退订要**在 `Stop()` 之前**：`Stop()` 会关掉所有会话，
        //    之后再触发的进度变化会找不到会话（那倒也不致命，但会白记一次"跳过"）。
        progress?.Dispose();

        transport.Stop();

        // SRV-17：优雅关闭要**把战绩写完**再退。⚠️ 带超时 ——
        // 关服务端不该因为"库连不上"而卡住不退出。
        if (recordWriter.PendingCount > 0 || recordWriter.Submitted > 0)
        {
            bool drained = recordWriter.FlushAsync(3000).GetAwaiter().GetResult();
            Say("[收尾] 战绩落库" + (drained ? "已排空。" : "**超时**（还有没写完的）—— 见上面的失败原因。"));
        }

        // M4-S3 权威：把成就进度冲干净（**这一步不做就等于"这一局的累计白打"**）
        if (achievements != null && achievements.TrackedPlayers > 0)
        {
            bool drained = achievements.FlushAllAsync(3000).GetAwaiter().GetResult();
            Say("[收尾] 成就进度落库" + (drained ? "已排空。" : "**超时**（还有没写完的）。"));
        }

        // 任务同理：状态 + 进度 + 台账都要冲干净（最后那个是"发了奖要留下记录"）
        if (quests != null && quests.TrackedPlayers > 0)
        {
            bool drained = quests.FlushAllAsync(3000).GetAwaiter().GetResult();
            Say("[收尾] 任务状态落库" + (drained ? "已排空。" : "**超时**（还有没写完的）。"));
        }

        // 推送线退订（它挂着 `QuestAuthority.StateChanged`；不退订就是泄漏）
        questStates?.Dispose();

        // 登录审计同理：别把"谁登录过"丢在队列里
        if (loginAudit.PendingCount > 0 || loginAudit.Submitted > 0)
        {
            bool drained = loginAudit.FlushAsync(2000).GetAwaiter().GetResult();
            Say("[收尾] 登录审计" + (drained ? "已排空。" : "**超时**（还有没写完的）—— 见上面的失败原因。"));
        }

        Say($"[统计] 接受连接 {transport.TotalAccepted}，断开 {transport.TotalClosed}，" +
                          $"握手成功 {pump.HandshakesAccepted}，被拒 {pump.HandshakesRejected}，" +
                          $"踢出 {pump.Kicks}，解不出 {pump.Undecodable}");
        Say($"[统计] 房间 {rooms.Registry.RoomCount} 个，席位表广播 {rooms.StateBroadcasts} 份");
        Say($"[统计] 战斗世界 {battles.BattleCount} 个，推进 {battles.TicksRun} 帧，" +
                          $"快照送出 {battles.SnapshotsSent} 份，回收世界 {battles.BattlesDropped} 个");
        Say($"[统计] 收到输入 {battles.InputsReceived} 条（拒绝 {battles.InputsRejected}），" +
                          $"普攻命中 {battles.AttacksLanded} 次（未打出去 {battles.AttacksRefused} 次）");
        Say($"[统计] 事件：伤害 {battles.HitsSent} 条、死亡 {battles.DeathsSent} 条、掉落 {battles.DropsSent} 条（M4-S1 起伤害/死亡也下发）");
        Say($"[统计] 打完 {battles.BattlesFinished} 局；{recordWriter.DescribeStats()}");
        if (accounts != null)
        {
            Say($"[统计] {accounts.Describe()}");
        }
        Say($"[统计] {loginAudit.DescribeStats()}");
        Say($"[统计] {lockstep.Describe()}");
        if (achievements != null)
        {
            Say($"[统计] {achievements.Describe()}");
        }
        if (quests != null)
        {
            Say($"[统计] {quests.Describe()}");
        }
        if (questService != null)
        {
            Say($"[统计] {questService.Describe()}");
        }
        if (questStates != null)
        {
            Say($"[统计] {questStates.Describe()}");
        }
        Say($"[统计] 收 {transport.FramesIn} 帧/{transport.BytesIn} B，" +
                          $"发 {transport.FramesOut} 帧/{transport.BytesOut} B，" +
                          $"逻辑帧 {scheduler.CurrentTick}");
        Say($"[统计] 日志：{s_log.Describe()}；共 {s_log.Count} 条（按级别丢弃 {s_log.DroppedByLevel} 条）");
        s_log.Flush();
        Say("[退出] 正常结束。");

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
    /// <summary>
    /// 写一行**给人看的**输出：控制台 + 日志文件**都要有**。
    /// <para>⚠️ 这就是"文件包含控制台的每一行"那条不变式的落点：
    /// 横幅 / 配置来源 / 统计 / `[致命]` 这些行**以前只进控制台**，
    /// 结果事后翻日志会缺"什么时候起的、最终统计是多少"这些最要紧的上下文。</para>
    /// <para>控制台这一次**不带** `[时间][级别]` 前缀（排版更好看），文件里带。</para>
    /// </summary>
    /// <param name="text">正文（空串 = 空行）。</param>
    private static void Say(string text)
    {
        Console.WriteLine(text);
        s_log.WriteToFile(LogLevel.Info, LogChannel.General, text);
    }

    /// <summary>
    /// 写一条"值得记一句"的运行信息。
    /// <para>⚠️ `quiet` 参数**保留了**（13 处调用点），但它现在只是"命令行强制安静"的意思；
    /// 真正的级别过滤在 `ServerLog` 里（`Logging.Level` 也能达到同样效果）。</para>
    /// </summary>
    /// <param name="quiet">命令行 `--quiet`（强制安静）。</param>
    /// <param name="line">正文。</param>
    private static void Log(bool quiet, string line)
    {
        if (quiet)
        {
            // ⚠️ 安静模式下**仍然进日志文件**（只是不刷控制台）——
            //    否则"我开了安静模式，结果什么日志都没有"。
            s_log.Write(LogLevel.Debug, LogChannel.General, line);
            return;
        }

        s_log.Info(line);
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

            Say(string.Empty);
            Say("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
            Say("[警告] 你双击的是**旧的 exe**：源码比产物新，改的代码没有生效！");
            Say($"        最新源码：{newestFile}");
            Say($"                  {newestSource:yyyy-MM-dd HH:mm:ss}");
            Say($"        产物时间：{newestBinary:yyyy-MM-dd HH:mm:ss}");
            Say(@"        先编译再启动：dotnet build Server\NBC.Server.Host\NBC.Server.Host.csproj -m:1");
            Say("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
            Say(string.Empty);
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
