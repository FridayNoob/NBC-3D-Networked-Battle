// ============================================================================
//  NBC.NetProbe —— 端到端网络探针（M3-S3 实跑）
//  项目：3D联网战斗Demo
//
//  跑的是**真东西**：真 socket（127.0.0.1）+ 真 protobuf + 服务端生产代码
//  （`TcpServerTransport` / `ServerMessagePump` / `ServerMessageRouter`）
//  + 客户端那份 `TcpTransport`（Unity 侧源码直接编进来）。
//
//  全在一个线程上：`Harness.PumpOnce()` = 服务端 Pump 一次 + 每个客户端 Pump 一次。
//  这本身就是"这套网络层不需要第二个线程"的实证。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;                // 对账"服务端自述的数值 = 源 CSV"要直接读文件
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using NBC.Framework.Net;         // M4-S4 S4-c：`ITransport`（假传输要实现它来驱动真 `NetSession`）
using NBC.Framework.Net.Adapter;
using NBC.Framework.Net.Sim;   // S8：网络模拟器（SimulatedTransport / NetSimProfile）
using NBC.Game.Net;
using NBC.Game.Quest;           // M4-S3 收口（§二十四）：进度「是谁说的」那一层（纯 C#，可在探针里真跑）
using NBC.Protocol;
using NBC.Server.Core;
using NBC.Server.Game;          // S5：`DungeonBattle` / `RoomBattleService`
using NBC.Shared;
using NBC.Shared.Sim;           // M4-S4 S4-b：`WorldState` / `WorldStep` / `WorldStateHash`（确定性内核）               // M4-S4 S4-a：`Fix64` / `FixMath` / `FixVector3`（共享层确定性数学）
using NBC.Shared.Auth;          // M4-S3：账号登录的摘要配方（**双端同一份实现**）
using NBC.Shared.Battle;        // 2026-10-20：圆形射程判据（`BattleRules.WithinBasicAttackRange`）
using NBC.Shared.Net;

namespace NBC.NetProbe
{
    /// <summary>探针入口。</summary>
    public static class Program
    {
        private static int s_passed;
        private static int s_failed;

        /// <summary>跑全部用例。</summary>
        /// <param name="args">命令行；`--connect=PORT` 时改成"连一个已经在跑的服务端进程"。</param>
        /// <returns>全绿返回 0。</returns>
        public static int Main(string[] args)
        {
            int connectPort = ReadIntArg(args, "--connect", 0);

            if (connectPort > 0)
            {
                // 「两个进程真的互通了吗」只有这一条路能证明：
                // 另起一个真进程（NBC.Server.Host）在听，这里当客户端连过去。
                return ConnectSmoke(connectPort);
            }

            int sessionPort = ReadIntArg(args, "--session", 0);

            if (sessionPort > 0)
            {
                // 用**客户端会话层**（`NBC.Game.Net.NetSession`）去连真服务端：
                // 它在 FakeTransport 上有一堆用例，但"真 socket 上跑得动吗"只有这里能证。
                //
                // ⚠️ `--login=test01:123456` 时**额外**验一遍跨进程的账号登录
                //    （要服务端接了数据库；不填就只验游客）。
                string login = ReadStringArg(args, "--login", string.Empty);
                int colon = login.IndexOf(':');

                return colon > 0
                    ? SessionSmoke(sessionPort, login.Substring(0, colon), login.Substring(colon + 1))
                    : SessionSmoke(sessionPort, string.Empty, string.Empty);
            }

            Console.WriteLine("=== 端到端网络探针（服务端 Core + 客户端 Framework.Net + 真 protobuf）===");
            Console.WriteLine("运行时：" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Console.WriteLine("协议：" + NetContract.Describe());
            Console.WriteLine();

            Console.WriteLine("【一】握手与心跳");
            HandshakeAccepted();
            PingPong();
            VersionMismatchIsRejectedWithReason();
            EmptyMessageIsProtocolViolation();
            UnhandshakedSessionIsKicked();
            // ⚠️ `UnimplementedMessageIsNotedNotKicked()` 于 2026-09-23 **退役**（不调用）：
            //    S5b 把最后一种 `ClientMessage`（Input）也实现掉了 → 没有"还没实现的消息"可发。
            //    函数体保留作样板，S6 加新消息时补一条对应用例。理由写在那个函数的注释里。

            Console.WriteLine();
            Console.WriteLine("【二】多客户端与会话生命周期");
            TwoClientsDoNotCrossTalk();
            PeerCloseIsCleanedUp();
            HeartbeatTimeoutReleasesTheSeat();

            Console.WriteLine();
            Console.WriteLine("【三】协议违规与分帧（复用共享层）");
            OversizePrefixIsFatal();
            SpanEncodeMatchesArrayEncode();

            Console.WriteLine();
            Console.WriteLine("【四】房间规则（纯逻辑，不碰网络）");
            RoomRules_HostIsTheFirstOne();
            RoomRules_FullIsRejected();
            RoomRules_AlreadyInRoomIsRejected();
            RoomRules_UnknownRoomIsRejected();
            RoomRules_AutoAssignPrefersSameDungeon();
            RoomRules_LeavingTransfersHostAndDeletesEmptyRoom();

            Console.WriteLine();
            Console.WriteLine("【五】房间与席位（走网线，真 socket）");
            RoomOverWire_TwoClientsShareOneSeatTable();
            RoomOverWire_FourthClientIsRejected();
            RoomOverWire_LeaveClearsBothSides();
            RoomOverWire_DisconnectFreesTheSeat();

            Console.WriteLine();
            Console.WriteLine("【六】状态同步（30Hz 全量快照，真 socket）");
            Snapshot_TwoClientsSeeTheSameWorld();
            Snapshot_WorldActuallyMoves();
            Snapshot_DamageIsAuthoritative();
            Snapshot_RateIsOnePerTick();
            Snapshot_BattleIsRecycledWhenRoomDies();

            Console.WriteLine();
            Console.WriteLine("【七】输入上行（只发意图，服务端说了算）");
            Input_MovesOnlyYourOwnHero();
            Input_ServerRateLimitsToOneStepPerTick();
            Input_ExpiresWhenTheClientStopsSending();
            Input_PlayerMismatchIsRejected();
            Attack_LandsInRangeAndRespectsCooldown();
            Attack_OutOfRangeIsRefusedSilently();
            BasicAttackRange_IsCircular();
            BasicAttackRange_ServerUsesCircle();
            Heroes_FollowSeats();

            Console.WriteLine();
            Console.WriteLine("【八】副本来自配置表（S6）");
            Tables_AreLoadedFromTheRealCsv();
            Tables_SelfReportMatchesTheSourceCsv();
            Tables_DecideTheDungeonLineup();
            Tables_UnknownDungeonFailsLoudly();
            Tables_DecideHeroStatsAndAttackDamage();

            Console.WriteLine();
            Console.WriteLine("【九】BOSS AI（A10 状态机驱动：仇恨范围内追击 / 够得着就打 / 出圈回家）");
            Boss_ChasesTheHero();
            Boss_ReturnsHomeAfterLosingAggro();
            Boss_SpawnPointsAreOutsideAggroRange();
            Boss_AttacksInRange();
            Boss_StopsWhenNoHeroIsAlive();
            Boss_BrainRunsOnTheStateMachine();

            Console.WriteLine();
            // ⚠️ 这一行 2026-09-23 被我自己"替换锚点时"误删过一次（S6a）——
            //    结果那几轮的探针输出里**没有通过数**，只是我没注意。补回来，并记着：
            //    **改替换锚点时要确认没把别的东西一起吃掉**（回读输出）。
            Console.WriteLine();
            Console.WriteLine("【十】掉落一致（S7：服务端掷、两端收到同一份）");
            Drops_BossAlwaysDropsBoth();
            Drops_TwoClientsReceiveTheSameDrop();
            Drops_WinnerIsTheKiller();
            Drops_AreDeterministicForTheSameRoom();

            Console.WriteLine();
            Console.WriteLine("【十一】网络模拟器（S8：注入延迟后仍能跑通）");
            Sim_LatencyStillWorks();

            Console.WriteLine();
            Console.WriteLine("【十二】端到端验收（S9：用输入打死怪 → 掉落一致 → 副本结束）");
            EndToEnd_KillOneMonsterViaInput();
            EndToEnd_BothClientsSeeTheSameEnding();
            EndToEnd_FinishTheRun();

            Console.WriteLine();
            Console.WriteLine("【十三】M4-S1：伤害 / 死亡事件下发（服务端事件 → 客户端事件中心）");
            Events_EveryHitIsBroadcastToBothClients();
            Events_DeathCarriesKindAndConfigId();
            Events_DamageComesBeforeDeathInTheSameTick();
            Events_CountersMatchWhatWasSent();

            Console.WriteLine();
            Console.WriteLine("【十四】M4-S1b：快照带 owner_player_id（客户端能认出「哪个英雄是我」）");
            Snapshots_CarryOwnerPlayerId();

            Console.WriteLine();
            Console.WriteLine("【十五】M4-S3：账号登录（SRV-06 真正的登录 —— 身份来自账号，不来自「第几个连上来的」）");
            Login_SameAccountGetsSamePlayerId();
            Login_WrongPasswordIsRejectedWithHumanReason();
            Login_UnknownAccountIsRejectedWithHumanReason();
            Login_GuestStillWorksOldWay();
            Login_GuestIdReachesTheBattleDraft();
            Login_NoAccountStoreSaysWhyInsteadOfSilentlyBecomingGuest();
            Login_DigestNeverAppearsInServerNotes();

            Console.WriteLine();
            Console.WriteLine("【十六】M4-S3 服务端权威：判定搬到服务端（同一份共享层逻辑，换了个执行者）");
            Authority_LoggedInPlayerUnlocksFromItsOwnBattle();
            Authority_GuestFactsAreNotRecorded();
            Authority_NoDatabaseSaysWhyInsteadOfPretending();

            Console.WriteLine();
            Console.WriteLine("【十七】M4-S3 §二十五：任务权威的**加载时校验**（共用条件必须被挡住）");
            Quest_SharedConditionIsRejected_ButCleanTablesLoad();
            Quest_SubmitUnregisters_SoBothPathsAgree();

            Console.WriteLine();
            Console.WriteLine("【十八】M4-S3 §二十六：任务动作**走网线**（接取 ⇒ 状态同步；拒绝要说清；游客要挡住）");
            QuestAction_SocketRoundTrip();

            Console.WriteLine();
            Console.WriteLine("【十九】M4-S3 §二十七：面板**分桶**（不丢行不变式 + 三值语义；纯逻辑，不碰库）");
            QuestSectionPlanner_BucketsAndNeverLosesRows();

            Console.WriteLine();
            Console.WriteLine("【二十】M4-S3 §二十八：**本地跟随权威**（幂等 / 不回路 / 无副作用；纯逻辑）");
            QuestStateReconciler_FollowsAuthorityWithoutSideEffects();

            Console.WriteLine();
            Console.WriteLine("【二十一】M4-S4 S4-a：**定点数学**纳入自动化验证（Fix64 Q32.32 / FixMath / FixVector3）");
            FixedPoint_ArithmeticBoundaries_AndDeterminism();

            Console.WriteLine();
            Console.WriteLine("【二十二】M4-S4 S4-b：**确定性内核 + 状态哈希**（逐帧相等 / 顺序无关 / 分歧能抓到）");
            DeterministicWorld_SameInputsSameHashes();

            Console.WriteLine();
            Console.WriteLine("【二十三】M4-S4 S4-c：**锁步调度**（逐帧对账 / 缺输入 / 去重 / 乱序；纯逻辑直驱）");
            LockstepScheduler_FramesMatchAcrossPeers();

            Console.WriteLine();
            Console.WriteLine("【二十四】M4-S4 S4-c 第二半·第 1 步：`LockstepService` 开局（直驱，不接 Host）");
            LockstepService_StartAndInitialHash();

            Console.WriteLine();
            Console.WriteLine("【二十五】M4-S4 S4-c 收尾：**客户端锁步运行器 + 三方对账**（含阳性对照）");
            LockstepClient_ThreeWayReconcile();

            Console.WriteLine();
            Console.WriteLine("【二十六】M4-S4 S4-c 收尾：**假传输驱动真 `NetSession`**（喂协议 → 转普通数据 → 喂运行器）");
            NetSession_LockstepWireUp();

            Console.WriteLine();
            Console.WriteLine("【二十七】M4-S4 S4-d：2v2 **队伍与胜负**（共享层纯函数，直驱）");
            MatchRules_TeamsAndVerdict();

            Console.WriteLine();
            Console.WriteLine("【二十八】M4-S4 S4-d 第 3 件：房间相位（满员 ⇒ Running；离开不回退）");
            RoomPhase_FullBecomesRunning();

            Console.WriteLine();
            Console.WriteLine("【二十九】M4-S4 S4-d 收尾：服务端宣布胜负 + 客户端复算比对");
            MatchResult_AnnounceAndRecheck();

            Console.WriteLine();
            Console.WriteLine("【三十】M4-S4 S4-d 收尾：**真实路径**（假 ITRANSPORT 驱真 NetSession）上的胜负复算");
            NetSession_MatchResultPath();

            Console.WriteLine();
            Console.WriteLine("【三十一】M4-S4 S4-e：锁步世界 → `WorldSnapshot` 适配器（喂真 `SnapshotView`）");
            LockstepSnapshotAdapter_Checks();

            Console.WriteLine();
            Console.WriteLine("【三十二】M4-S4 S4-e：机器人席位（`--fill-seats` 的底座）+ **开局名单 == 下发名单**");
            RoomBots_FillSeats();

            Console.WriteLine();
            Console.WriteLine("【三十三】P0（§三十六）：**未知副本号打不挂服务端** + 合法副本照常开打 + 补席位 ⇒ 锁步开局");
            RoomGuard_UnknownDungeonNeverKillsTheHost();

            Console.WriteLine();
            Console.WriteLine("【三十四】M4-S4 S4-e 收尾：**真分流**（喂给表现层的那份数据由开关决定）+ 3D 接线的那一层");
            PresentationSource_Checks();

            Console.WriteLine();
            Console.WriteLine("【三十五】验收修正：**沉默冻结**（停滞回退）/ 回退口径 / 世界可用 / 只读 / 零分配 / 换局");
            PresentationSource_StallAndZeroAlloc_Checks();

            Console.WriteLine();
            Console.WriteLine("【三十六】第二轮验收：**第二局真的能打** / 停滞滞回 / 零分配（实体数变化）/ `CopySortedTo` 同序");
            SecondMatchAndHysteresis_Checks();

            Console.WriteLine();
            Console.WriteLine("【S4e3】端到端：`NetSession` + **真 `TcpTransport`** 连**真 Host**（默认不启用）");
            EndToEnd_LockstepOverRealSocket(
                Array.IndexOf(args, "--e2e") >= 0, ReadIntArg(args, "--e2e-port", 9000));

            Console.WriteLine("通过 " + s_passed + "，失败 " + s_failed + "。");
            Console.WriteLine(s_failed == 0 ? "结果：✅ 全绿" : "结果：❌ 有红");
            return s_failed == 0 ? 0 : 1;
        }

        // ====================================================================
        //  客户端模式：连一个**已经在跑**的服务端进程
        // ====================================================================

        /// <summary>
        /// 当一个真客户端，连到指定端口上的服务端进程：握手 → 心跳 → 报告结果。
        /// <para>
        /// 这是"**两个进程互通**"（S3 的验收）唯一能自证的方式 ——
        /// 进程内的用例再全，也证不了"另起一个进程、跨进程边界"这件事。
        /// </para>
        /// </summary>
        /// <param name="port">服务端端口。</param>
        /// <returns>全绿返回 0。</returns>
        private static int ConnectSmoke(int port)
        {
            Console.WriteLine($"=== 客户端模式：连 127.0.0.1:{port}（对面应当是一个真在跑的 NBC.Server.Host）===");

            var received = new List<ServerMessage>();
            var closes = new List<string>();
            var transport = new TcpTransport();

            transport.FrameReceived += payload => received.Add(ServerMessage.Parser.ParseFrom(payload));
            transport.Closed += reason => closes.Add(reason);

            transport.Connect("127.0.0.1", port);

            if (!PumpUntil(transport, () => transport.IsConnected, "连上服务端", 3000))
            {
                Check("客户端模式：连上服务端", false, "3 秒内没连上（服务端起了吗？端口对吗？）");
                Console.WriteLine($"通过 {s_passed}，失败 {s_failed}。");
                return 1;
            }

            Check("客户端模式：连上服务端进程", true, string.Empty);

            HandshakeAck ack = null;

            transport.Send(new ClientMessage
            {
                Handshake = new Handshake
                {
                    ProtocolVersion = NetContract.Version,
                    ClientVersion = "net-probe(client mode)",
                    PlayerName = "探针",
                },
            }.ToByteArray());

            PumpUntil(transport, () => FindAck(received) != null, "收到 HandshakeAck", 3000);
            ack = FindAck(received);

            Check("客户端模式：握手成功（跨进程）",
                ack != null && ack.Accepted && ack.PlayerId < 0 && ack.TickHz == NetContract.TickRate,
                Describe(ack));

            const long sent = 424242L;
            transport.Send(new ClientMessage { Ping = new Ping { ClientTimeMs = sent } }.ToByteArray());

            PumpUntil(transport, () => FindPong(received) != null, "收到 Pong", 3000);
            Pong pong = FindPong(received);

            Check("客户端模式：心跳往返（跨进程）",
                pong != null && pong.ClientTimeMs == sent && pong.ServerTimeMs > 0,
                pong == null ? "没收到 Pong" : ("client_time=" + pong.ClientTimeMs + "，server_time=" + pong.ServerTimeMs));

            Check("客户端模式：连接全程没被断开", closes.Count == 0,
                closes.Count == 0 ? string.Empty : string.Join(" | ", closes));

            transport.Dispose();

            Console.WriteLine($"通过 {s_passed}，失败 {s_failed}。");
            Console.WriteLine();
            Console.WriteLine("【九】BOSS AI（A10 状态机驱动：仇恨范围内追击 / 够得着就打 / 出圈回家）");
            Boss_ChasesTheHero();
            Boss_ReturnsHomeAfterLosingAggro();
            Boss_SpawnPointsAreOutsideAggroRange();
            Boss_AttacksInRange();
            Boss_StopsWhenNoHeroIsAlive();
            Boss_BrainRunsOnTheStateMachine();

            Console.WriteLine();
            // ⚠️ 这一行 2026-09-23 被我自己"替换锚点时"误删过一次（S6a）——
            //    结果那几轮的探针输出里**没有通过数**，只是我没注意。补回来，并记着：
            //    **改替换锚点时要确认没把别的东西一起吃掉**（回读输出）。
            Console.WriteLine("通过 " + s_passed + "，失败 " + s_failed + "。");
            Console.WriteLine(s_failed == 0 ? "结果：✅ 全绿" : "结果：❌ 有红");
            return s_failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// 用**客户端会话层**（`NBC.Game.Net.NetSession`）连真服务端进程：握手 → RTT → 心跳节奏。
        /// <para>
        /// 为什么值得单独有一段：`NetSession` 在 EditMode 里有一堆 `FakeTransport` 用例，
        /// 但**真 socket 上跑不跑得动**是另一件事（分帧、非阻塞连接、粘包、时间注入的 delta 全都在里面）。
        /// </para>
        /// </summary>
        /// <param name="port">服务端端口。</param>
        /// <returns>全绿返回 0。</returns>
        private static int SessionSmoke(int port, string account, string password)
        {
            Console.WriteLine($"=== 会话层模式：用 NetSession 连 127.0.0.1:{port} ===");

            var transport = new TcpTransport();
            var session = new NetSession(transport, "探针会话", "net-probe/session");

            session.Note += line => Console.WriteLine("      · " + line);

            session.Connect("127.0.0.1", port);

            bool online = PumpSessionUntil(session, () => session.IsOnline, 3000);

            Check("会话层：连上并完成握手（跨进程）", online, "3 秒内没到 Online，当前 " + session.Description);

            if (!online)
            {
                Console.WriteLine($"通过 {s_passed}，失败 {s_failed}。");
                return 1;
            }

            Check("会话层：拿到 player_id 与服务端版本",
                session.PlayerId < 0 && session.Ack != null && !string.IsNullOrEmpty(session.Ack.ServerVersion),
                "player_id=" + session.PlayerId + "，server=" + (session.Ack == null ? "无" : session.Ack.ServerVersion));

            // ------------------------------------------------------------------
            //  M4-S3：跨进程的**真账号登录**（要服务端接了数据库）
            // ------------------------------------------------------------------
            //  ⚠️ 这一段是全链路里唯一"客户端的 `NetSession` 真的把账号/摘要发出去、
            //     真的服务端用真的 `AccountDirectory` 查了真的库"的地方。
            //     进程内那 106 条验的是"路由器 + 假账号表"；这里验的是**接线**。
            if (account.Length > 0)
            {
                var loginTransport = new TcpTransport();
                var login = new NetSession(loginTransport, "不该用到这个名字", "net-probe/login", 1000, 5000, 5000,
                                           account, password);

                login.Note += line => Console.WriteLine("      · " + line);
                login.Connect("127.0.0.1", port);

                bool loginOk = PumpSessionUntil(login, () => login.IsOnline || login.FailureReason != null, 3000);

                Check("跨进程登录：用 `NetSession` + 账号登录成功",
                    loginOk && login.IsOnline && login.PlayerId > 0,
                    "online=" + login.IsOnline + "，player_id=" + login.PlayerId +
                    "，失败原因=" + (login.FailureReason ?? "无"));

                Check("跨进程登录：拿到的是**账号档案里的昵称**（不是客户端发来的名字）",
                    login.Ack != null && login.Ack.Nickname == "测试玩家一",
                    "nickname=\"" + (login.Ack == null ? "无" : login.Ack.Nickname) + "\"");

                Check("跨进程登录：客户端自己知道这是登录、不是游客",
                    !login.IsGuest && login.Account == account,
                    "IsGuest=" + login.IsGuest + "，Account=\"" + login.Account + "\"");

                // ⚠️ 一次**反向**检查：错的密码必须被拒，而且原因要能看懂
                var badTransport = new TcpTransport();
                var bad = new NetSession(badTransport, "不该用到这个名字", "net-probe/login", 1000, 5000, 5000,
                                         account, password + "-错的");

                bad.Note += line => Console.WriteLine("      · " + line);
                bad.Connect("127.0.0.1", port);

                PumpSessionUntil(bad, () => bad.IsOnline || bad.FailureReason != null, 3000);

                Check("跨进程登录：**错的密码跨进程也被拒**，且失败原因说人话",
                    !bad.IsOnline && bad.FailureReason != null && bad.FailureReason.Contains("密码"),
                    "online=" + bad.IsOnline + "，失败原因=" + (bad.FailureReason ?? "无"));

                bad.Dispose();
                login.Dispose();
            }
            else
            {
                Console.WriteLine("  ⏭️  没给 `--login=账号:密码`，跳过跨进程登录（要服务端接了数据库才验得了）。");
            }

            // 等一次 Pong 回来（握手后会自动发一次心跳）
            PumpSessionUntil(session, () => session.RttMs >= 0, 2000);

            Check("会话层：RTT 测出来了（Pong 回来了）", session.RttMs >= 0, "RttMs=" + session.RttMs);

            // 再跑 1.2 秒，看心跳是不是按节奏自己发（不用手工调）
            DateTime until = DateTime.UtcNow.AddMilliseconds(1200);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            while (DateTime.UtcNow < until)
            {
                int delta = (int)watch.ElapsedMilliseconds;
                watch.Restart();
                session.Pump(delta);
                Thread.Sleep(10);
            }

            Check("会话层：1.2 秒里又自动发了心跳、也收到了 Pong",
                session.PingsSent >= 2 && session.PongsReceived >= 2,
                "发 " + session.PingsSent + "，收 " + session.PongsReceived);

            Check("会话层：全程没被判失败", session.FailureReason == null,
                session.FailureReason ?? string.Empty);

            // 房间：进房 → 席位表 → 离开（走真服务端）
            session.JoinRoom(string.Empty, 1001);
            PumpSessionUntil(session, () => session.InRoom, 2000);

            Check("会话层：进房并收到席位表（真服务端）",
                session.InRoom && session.CurrentRoom.Members.Count == 1
                && session.CurrentRoom.Capacity == NetContract.MaxRoomMembers,
                session.InRoom
                    ? ("房号 " + session.CurrentRoom.RoomId + "，" + session.CurrentRoom.Members.Count + " 人")
                    : "2 秒内没进房");

            session.LeaveRoom();
            PumpSessionUntil(session, () => !session.InRoom, 2000);

            Check("会话层：离开后「不在任何房间」（房号为空，约定见 RoomService 文件头）",
                !session.InRoom && session.CurrentRoom != null
                && string.IsNullOrEmpty(session.CurrentRoom.RoomId),
                session.CurrentRoom == null ? "没收到任何房间状态" : ("房号=\"" + session.CurrentRoom.RoomId + "\""));

            session.Disconnect();
            Check("会话层：主动断开后状态回到 Disconnected",
                session.State == ESessionState.Disconnected, session.State.ToString());

            session.Dispose();

            Console.WriteLine($"通过 {s_passed}，失败 {s_failed}。");
            Console.WriteLine();
            Console.WriteLine("【九】BOSS AI（A10 状态机驱动：仇恨范围内追击 / 够得着就打 / 出圈回家）");
            Boss_ChasesTheHero();
            Boss_ReturnsHomeAfterLosingAggro();
            Boss_SpawnPointsAreOutsideAggroRange();
            Boss_AttacksInRange();
            Boss_StopsWhenNoHeroIsAlive();
            Boss_BrainRunsOnTheStateMachine();

            Console.WriteLine();
            // ⚠️ 这一行 2026-09-23 被我自己"替换锚点时"误删过一次（S6a）——
            //    结果那几轮的探针输出里**没有通过数**，只是我没注意。补回来，并记着：
            //    **改替换锚点时要确认没把别的东西一起吃掉**（回读输出）。
            Console.WriteLine("通过 " + s_passed + "，失败 " + s_failed + "。");
            Console.WriteLine(s_failed == 0 ? "结果：✅ 全绿" : "结果：❌ 有红");
            return s_failed == 0 ? 0 : 1;
        }

        /// <summary>用真实流逝时间喂 `NetSession`，推到条件成立（有界）。</summary>
        /// <param name="session">会话。</param>
        /// <param name="condition">条件。</param>
        /// <param name="timeoutMs">上限毫秒。</param>
        /// <returns>成立返回 true。</returns>
        private static bool PumpSessionUntil(NetSession session, Func<bool> condition, int timeoutMs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < until)
            {
                int delta = (int)watch.ElapsedMilliseconds;
                watch.Restart();
                session.Pump(delta);

                if (condition())
                {
                    return true;
                }

                Thread.Sleep(5);
            }

            session.Pump(0);
            return condition();
        }

        /// <summary>反复 Pump 直到条件成立（有界）。</summary>
        /// <param name="transport">传输。</param>
        /// <param name="condition">条件。</param>
        /// <param name="what">等的是什么。</param>
        /// <param name="timeoutMs">上限毫秒。</param>
        /// <returns>成立返回 true。</returns>
        private static bool PumpUntil(TcpTransport transport, Func<bool> condition, string what, int timeoutMs)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < until)
            {
                transport.Pump();

                if (condition())
                {
                    return true;
                }

                Thread.Sleep(1);
            }

            transport.Pump();
            return condition();
        }

        /// <summary>从收到的消息里找握手结果。</summary>
        /// <param name="received">收到的消息。</param>
        /// <returns>握手结果或 null。</returns>
        private static HandshakeAck FindAck(List<ServerMessage> received)
        {
            for (int i = 0; i < received.Count; i++)
            {
                if (received[i].HandshakeAck != null)
                {
                    return received[i].HandshakeAck;
                }
            }

            return null;
        }

        /// <summary>从收到的消息里找 Pong。</summary>
        /// <param name="received">收到的消息。</param>
        /// <returns>Pong 或 null。</returns>
        private static Pong FindPong(List<ServerMessage> received)
        {
            for (int i = 0; i < received.Count; i++)
            {
                if (received[i].Pong != null)
                {
                    return received[i].Pong;
                }
            }

            return null;
        }

        /// <summary>读 `--key=value` 形式的整数参数。</summary>
        /// <param name="args">命令行。</param>
        /// <param name="key">键。</param>
        /// <param name="fallback">缺省值。</param>
        /// <returns>值。</returns>
        private static int ReadIntArg(string[] args, string key, int fallback)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(key + "=", StringComparison.Ordinal) &&
                    int.TryParse(args[i].Substring(key.Length + 1), out int value))
                {
                    return value;
                }
            }

            return fallback;
        }

        /// <summary>读 `--key=value` 形式的字符串参数。</summary>
        /// <param name="args">命令行。</param>
        /// <param name="key">键。</param>
        /// <param name="fallback">缺省值。</param>
        /// <returns>值。</returns>
        private static string ReadStringArg(string[] args, string key, string fallback)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(key + "=", StringComparison.Ordinal))
                {
                    return args[i].Substring(key.Length + 1);
                }
            }

            return fallback;
        }

        // ====================================================================
        //  一、握手与心跳
        // ====================================================================

        /// <summary>握手成功：版本一致 → 分配玩家号 → 会话进入 InLobby。</summary>
        private static void HandshakeAccepted()
        {
            using (var h = new Harness())
            {
                Client c = h.Connect();
                h.WaitUntil(() => c.Transport.IsConnected, "客户端连上服务端");
                c.Send(new ClientMessage
                {
                    Handshake = new Handshake
                    {
                        ProtocolVersion = NetContract.Version,
                        ClientVersion = "probe",
                        PlayerName = "剑士",
                    },
                });

                h.WaitUntil(() => c.Received.Count > 0, "收到 HandshakeAck");

                HandshakeAck ack = c.Last != null ? c.Last.HandshakeAck : null;
                bool ok = ack != null && ack.Accepted
                          && ack.PlayerId < 0
                          && ack.ProtocolVersion == NetContract.Version
                          && ack.TickHz == NetContract.TickRate
                          && ack.ServerVersion == "probe";

                Check("握手成功：accepted + **游客编号是负数** + 版本回显 + tick 率", ok, Describe(ack));

                Check("握手成功后会话推进到 InLobby（M0 的会话状态机真的动了）",
                    h.Sessions.Count == 1 && h.Sessions[0].Phase == SessionPhase.InLobby,
                    h.Sessions.Count == 1 ? ("Phase=" + h.Sessions[0].Phase) : "会话数=" + h.Sessions.Count);

                Check("会话拿到了 player_id（游客是负数，**不是 0**）",
                    h.Sessions.Count == 1 && h.Sessions[0].PlayerId < 0,
                    h.Sessions.Count == 1 ? ("player_id=" + h.Sessions[0].PlayerId) : "没有会话");
            }
        }

        /// <summary>心跳：客户端时间原样回来 + 服务端时间有效。</summary>
        private static void PingPong()
        {
            using (var h = new Harness())
            {
                Client c = h.ConnectAndHandshake();
                const long sent = 1234567890123L;
                c.Send(new ClientMessage { Ping = new Ping { ClientTimeMs = sent } });

                h.WaitUntil(() => c.FindPong() != null, "收到 Pong");
                Pong pong = c.FindPong();

                Check("心跳：client_time 原样回 + server_time 有效",
                    pong != null && pong.ClientTimeMs == sent && pong.ServerTimeMs > 0,
                    pong == null
                        ? "没收到 Pong"
                        : ("client_time=" + pong.ClientTimeMs + "，server_time=" + pong.ServerTimeMs));
            }
        }

        /// <summary>
        /// 版本不一致：**必须收到"为什么被拒"的说明**，然后才断开。
        /// <para>这条同时验了服务端的"先回包再断开"顺序 —— 顺序反了这条会红。</para>
        /// </summary>
        private static void VersionMismatchIsRejectedWithReason()
        {
            using (var h = new Harness())
            {
                Client c = h.Connect();
                h.WaitUntil(() => c.Transport.IsConnected, "客户端连上");
                c.Send(new ClientMessage
                {
                    Handshake = new Handshake
                    {
                        ProtocolVersion = NetContract.Version + 998,
                        ClientVersion = "老客户端",
                    },
                });

                h.WaitUntil(() => c.Received.Count > 0 && c.CloseReasons.Count > 0,
                    "收到拒绝说明，随后连接被断开");

                HandshakeAck ack = c.Received.Count > 0 ? c.Received[0].HandshakeAck : null;

                Check("版本不一致：先收到 accepted=false 的说明（不是莫名其妙掉线）",
                    ack != null && !ack.Accepted && ack.Reason.Contains("版本不一致"),
                    ack == null ? "一条都没收到" : ("accepted=" + ack.Accepted + "，reason=" + ack.Reason));

                Check("版本不一致：服务端确实把会话断开了（并说明了原因）",
                    h.SessionClosings.Exists(s => s.Contains("版本不一致")),
                    h.SessionClosings.Count == 0 ? "服务端没有任何断开记录" : string.Join(" | ", h.SessionClosings));

                Check("版本不一致：客户端这边也感知到了断开",
                    c.CloseReasons.Count > 0,
                    c.CloseReasons.Count == 0 ? "客户端还一直以为连着" : c.CloseReasons[0]);
            }
        }

        /// <summary>0 长度帧（空 protobuf 消息）→ 服务端判协议违规（不是静默忽略）。</summary>
        private static void EmptyMessageIsProtocolViolation()
        {
            using (var h = new Harness())
            {
                Client c = h.ConnectAndHandshake();
                c.Transport.Send(new byte[0]);      // 0 长度帧：分帧层合法，但语义上是空消息

                h.WaitUntil(() => h.SessionClosings.Count > 0, "服务端因空消息断开");
                Check("空 payload 的消息被判协议违规（不是装作没看见）",
                    h.SessionClosings.Exists(s => s.Contains("没有 payload")),
                    h.SessionClosings.Count == 0 ? "服务端没反应" : string.Join(" | ", h.SessionClosings));
            }
        }

        /// <summary>没握手就发别的 → 断开，并说清"第一条必须是 Handshake"。</summary>
        private static void UnhandshakedSessionIsKicked()
        {
            using (var h = new Harness())
            {
                Client c = h.Connect();
                h.WaitUntil(() => c.Transport.IsConnected, "客户端连上");
                c.Send(new ClientMessage { Ping = new Ping { ClientTimeMs = 1 } });

                h.WaitUntil(() => h.SessionClosings.Count > 0, "服务端断开未握手的会话");
                Check("没握手就发 Ping → 被断开且说明原因（握手门真的在）",
                    h.SessionClosings.Exists(s => s.Contains("还没握手")),
                    h.SessionClosings.Count == 0 ? "服务端没反应" : string.Join(" | ", h.SessionClosings));
            }
        }

        /// <summary>
        /// 还没实现的消息：记一句说明、**不断开**。
        /// <para>
        /// ⚠️ 2026-09-23 这条用例**已经退役**（保留函数体作"下次怎么用"的样板，暂时不调用）。
        /// 它履约过两次，每次都是因为**功能追上了**：
        /// · 最初发 `JoinRoom` → S4 把 JoinRoom 实现掉 → 换对象
        /// · 改用 `PlayerInput` → S5b 又把 Input 实现掉 → **五种 `ClientMessage` 全部有处理器了**
        ///   （`Handshake` / `Ping` / `JoinRoom` / `LeaveRoom` / `Input`）
        /// ⇒ 现在**没有"还没实现的消息"可发**了，硬留着只会是一条恒红的用例。
        ///   服务端那条 `Warn("服务端还没实现 X")` 分支**保留**（S6 加 `UseItemRequest` 之类时会用到），
        ///   届时补一条对应的用例即可。
        /// </para>
        /// </summary>
        private static void UnimplementedMessageIsNotedNotKicked()
        {
            using (var h = new Harness())
            {
                List<string> notes = new List<string>();
                h.Pump.Note += notes.Add;

                Client c = h.ConnectAndHandshake();
                c.Send(new ClientMessage
                {
                    Input = new PlayerInput { PlayerId = 1, ClientTick = 1, MoveX = 500 },
                });

                h.PumpFor(200);

                Check("还没实现的消息：记一句「还没实现 Input」，但不断开",
                    notes.Exists(n => n.Contains("还没实现")) && h.SessionClosings.Count == 0 && c.Transport.IsConnected,
                    "notes=" + notes.Count + "，closings=" + h.SessionClosings.Count +
                    "，stillConnected=" + c.Transport.IsConnected);
            }
        }

        // ====================================================================
        //  二、多客户端与会话生命周期
        // ====================================================================

        /// <summary>两个客户端各自握手、各自心跳 —— **不许串台**。</summary>
        private static void TwoClientsDoNotCrossTalk()
        {
            using (var h = new Harness())
            {
                Client a = h.ConnectAndHandshake();
                Client b = h.ConnectAndHandshake();

                a.Send(new ClientMessage { Ping = new Ping { ClientTimeMs = 111 } });
                b.Send(new ClientMessage { Ping = new Ping { ClientTimeMs = 222 } });

                h.WaitUntil(() => a.FindPong() != null && b.FindPong() != null, "两个客户端都收到 Pong");

                bool ok = a.FindPong() != null && a.FindPong().ClientTimeMs == 111
                          && b.FindPong() != null && b.FindPong().ClientTimeMs == 222
                          && a.Ack != null && b.Ack != null
                          && a.Ack.PlayerId != b.Ack.PlayerId;

                Check("两个客户端各收各的（不串台），玩家号不同", ok,
                    "a.pong=" + (a.FindPong() != null ? a.FindPong().ClientTimeMs.ToString() : "无") +
                    "，b.pong=" + (b.FindPong() != null ? b.FindPong().ClientTimeMs.ToString() : "无") +
                    "，a.player=" + (a.Ack != null ? a.Ack.PlayerId.ToString() : "无") +
                    "，b.player=" + (b.Ack != null ? b.Ack.PlayerId.ToString() : "无"));

                Check("服务端同时持有 2 个会话（Q3 的 2~4 人下限）",
                    h.Server.SessionCount == 2, "SessionCount=" + h.Server.SessionCount);
            }
        }

        /// <summary>客户端拔线：服务端认出来、清掉会话（席位要能释放）。</summary>
        private static void PeerCloseIsCleanedUp()
        {
            using (var h = new Harness())
            {
                Client c = h.ConnectAndHandshake();
                c.Transport.Close();

                h.WaitUntil(() => h.Server.SessionCount == 0, "服务端清掉断开的会话");

                Check("客户端关闭 → 服务端认出「对端关闭」并释放会话",
                    h.Server.SessionCount == 0 && h.SessionClosings.Exists(s => s.Contains("对端关闭")),
                    "SessionCount=" + h.Server.SessionCount + "，closings=" + string.Join(" | ", h.SessionClosings));
            }
        }

        /// <summary>心跳超时：客户端不发任何东西 → 服务端主动踢掉（否则席位永久占用）。</summary>
        private static void HeartbeatTimeoutReleasesTheSeat()
        {
            using (var h = new Harness(heartbeatTimeoutMs: 250))
            {
                Client c = h.ConnectAndHandshake();

                h.WaitUntil(() => h.SessionClosings.Count > 0, "服务端按心跳超时断开", 3000);

                Check("心跳超时：服务端主动断开并说明（席位能释放）",
                    h.SessionClosings.Exists(s => s.Contains("心跳超时")),
                    h.SessionClosings.Count == 0 ? "等了 3 秒服务端没反应" : string.Join(" | ", h.SessionClosings));

                Check("心跳超时后客户端也感知到断开",
                    c.CloseReasons.Count > 0,
                    c.CloseReasons.Count == 0 ? "客户端还一直以为连着" : c.CloseReasons[0]);
            }
        }

        // ====================================================================
        //  三、协议违规与分帧
        // ====================================================================

        /// <summary>撒谎的长度前缀：服务端判协议违规断开（不照着这个长度攒内存）。</summary>
        private static void OversizePrefixIsFatal()
        {
            using (var h = new Harness())
            {
                var raw = new TcpClient();
                raw.Connect(IPAddress.Loopback, h.Server.Port);

                h.WaitUntil(() => h.Server.SessionCount == 1, "服务端接受了裸连接");

                byte[] liar = FrameCodec.Encode(new byte[NetContract.MaxFrameBytes + 1]);

                try
                {
                    raw.GetStream().Write(liar, 0, NetContract.FrameLengthPrefixBytes);
                    raw.GetStream().Flush();
                }
                catch (Exception)
                {
                    // 服务端可能先断开了，不影响本条要验的事
                }

                h.WaitUntil(() => h.SessionClosings.Count > 0, "服务端判协议违规断开");

                Check("撒谎的长度前缀 → 服务端判违规断开（不照它攒内存）",
                    h.SessionClosings.Exists(s => s.Contains("超过上限")),
                    h.SessionClosings.Count == 0 ? "服务端没反应" : string.Join(" | ", h.SessionClosings));

                raw.Close();
            }
        }

        /// <summary>新增的 span 版 `FrameCodec.Encode` 与 byte[] 版结果必须完全一致。</summary>
        private static void SpanEncodeMatchesArrayEncode()
        {
            byte[] payload = new byte[32];

            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i * 5 + 1);
            }

            byte[] fromArray = FrameCodec.Encode(payload);
            byte[] fromSpan = FrameCodec.Encode(new ReadOnlySpan<byte>(payload));
            byte[] emptyArray = FrameCodec.Encode(new byte[0]);
            byte[] emptySpan = FrameCodec.Encode(ReadOnlySpan<byte>.Empty);

            Check("FrameCodec：span 版与 byte[] 版结果一致（含 0 长度帧）",
                Same(fromArray, fromSpan) && Same(emptyArray, emptySpan)
                    && emptySpan.Length == NetContract.FrameLengthPrefixBytes,
                "array=" + fromArray.Length + "，span=" + fromSpan.Length +
                "，empty=" + emptySpan.Length + "，内容一致=" + Same(fromArray, fromSpan));
        }

        // ====================================================================
        //  四、房间规则（纯逻辑：`RoomRegistry`，不碰网络）
        // ====================================================================

        /// <summary>房间规则：第一个进房的人是房主。</summary>
        private static void RoomRules_HostIsTheFirstOne()
        {
            var registry = new RoomRegistry(4);
            ClientSession a = NewSession(1, "甲");
            ClientSession b = NewSession(2, "乙");

            RoomJoinResult first = registry.Join(a, a.PlayerName, string.Empty, 1001);
            RoomJoinResult second = registry.Join(b, b.PlayerName, string.Empty, 1001);

            Check("房间规则：第一个进房的人是房主，第二个不是",
                first.Ok && second.Ok
                && first.Room!.RoomId == second.Room!.RoomId
                && first.Room.SeatCount == 2
                && first.Room.Seats[0].IsHost && !first.Room.Seats[1].IsHost
                && first.Room.Seats[0].PlayerName == "甲",
                first.Ok && second.Ok
                    ? ("房号 " + first.Room!.RoomId + "，房主 " + first.Room.Seats[0].PlayerName)
                    : ("进房失败：" + (first.Reason ?? second.Reason)));
        }

        /// <summary>房间规则：满了之后**指定房号**进房会被拒，且不会偷偷另开一间。</summary>
        private static void RoomRules_FullIsRejected()
        {
            var registry = new RoomRegistry(4);
            Room room = null;

            for (int i = 1; i <= 4; i++)
            {
                ClientSession s = NewSession(i, "玩家" + i);
                RoomJoinResult r = registry.Join(s, s.PlayerName, string.Empty, 1001);
                room = r.Room;
            }

            ClientSession fifth = NewSession(5, "戊");
            RoomJoinResult rejected = registry.Join(fifth, fifth.PlayerName, room!.RoomId, 1001);

            Check("房间规则：满员后按房号进房 → 拒绝，错误码 1001，人话说明",
                !rejected.Ok && rejected.ErrorCode == NetErrors.RoomFull
                && rejected.Reason!.Contains("已满") && registry.RoomCount == 1,
                (rejected.Ok ? "居然进来了" : ("code=" + rejected.ErrorCode + "，reason=" + rejected.Reason))
                + "，房间数=" + registry.RoomCount);
        }

        /// <summary>房间规则：一个人不能同时占两个房的席位。</summary>
        private static void RoomRules_AlreadyInRoomIsRejected()
        {
            var registry = new RoomRegistry(4);
            ClientSession a = NewSession(1, "甲");

            registry.Join(a, a.PlayerName, string.Empty, 1001);
            RoomJoinResult again = registry.Join(a, a.PlayerName, string.Empty, 1001);

            Check("房间规则：已经在房里再进一次 → 拒绝（错误码 1002），房间数不变",
                !again.Ok && again.ErrorCode == NetErrors.AlreadyInRoom && registry.RoomCount == 1,
                (again.Ok ? "居然又进了一次" : ("code=" + again.ErrorCode + "，reason=" + again.Reason))
                + "，房间数=" + registry.RoomCount);
        }

        /// <summary>房间规则：指定了一个不存在的房号 → 拒绝，并说清是"不存在"。</summary>
        private static void RoomRules_UnknownRoomIsRejected()
        {
            var registry = new RoomRegistry(4);
            ClientSession a = NewSession(1, "甲");

            RoomJoinResult rejected = registry.Join(a, a.PlayerName, "r99", 1001);

            Check("房间规则：房号不存在 → 拒绝（错误码 1003），不是「悄悄开一间新的」",
                !rejected.Ok && rejected.ErrorCode == NetErrors.RoomNotFound
                && rejected.Reason!.Contains("不存在") && registry.RoomCount == 0,
                (rejected.Ok ? "居然开了一间" : ("code=" + rejected.ErrorCode + "，reason=" + rejected.Reason))
                + "，房间数=" + registry.RoomCount);
        }

        /// <summary>房间规则：留空房号时，优先加入**同一个副本**且没满的房间。</summary>
        private static void RoomRules_AutoAssignPrefersSameDungeon()
        {
            var registry = new RoomRegistry(4);
            ClientSession a = NewSession(1, "甲");
            ClientSession b = NewSession(2, "乙");
            ClientSession c = NewSession(3, "丙");

            Room roomA = registry.Join(a, a.PlayerName, string.Empty, 1001).Room!;
            Room roomB = registry.Join(b, b.PlayerName, string.Empty, 2002).Room!;
            Room roomC = registry.Join(c, c.PlayerName, string.Empty, 1001).Room!;

            Check("房间规则：留空房号 → 优先进同副本未满的房间（副本不同才新开）",
                roomA.RoomId != roomB.RoomId && roomC.RoomId == roomA.RoomId
                && roomA.SeatCount == 2 && roomB.SeatCount == 1 && registry.RoomCount == 2,
                "甲=" + roomA.RoomId + "，乙=" + roomB.RoomId + "，丙=" + roomC.RoomId
                + "，房间数=" + registry.RoomCount);
        }

        /// <summary>房间规则：房主走了要移交；房空了要删掉；离开是幂等的。</summary>
        private static void RoomRules_LeavingTransfersHostAndDeletesEmptyRoom()
        {
            var registry = new RoomRegistry(4);
            ClientSession a = NewSession(1, "甲");
            ClientSession b = NewSession(2, "乙");

            Room room = registry.Join(a, a.PlayerName, string.Empty, 1001).Room!;
            registry.Join(b, b.PlayerName, string.Empty, 1001);

            Room left = registry.Leave(a);
            bool hostTransferred = left!.SeatCount == 1 && left.Seats[0].IsHost && left.Seats[0].PlayerName == "乙";

            registry.Leave(b);
            Room nullAgain = registry.Leave(b);

            Check("房间规则：房主离开 → 下一个人接替房主；房空了 → 房间被删；再离开 → 幂等返回 null",
                hostTransferred && registry.RoomCount == 0 && nullAgain == null
                && registry.FindBySession(b) == null,
                "房主移交=" + hostTransferred + "，房间数=" + registry.RoomCount
                + "，重复离开=" + (nullAgain == null ? "null" : "非 null"));
        }

        // ====================================================================
        //  五、房间与席位（走网线）
        // ====================================================================

        /// <summary>两个客户端进同一个房间 → 两边收到**同一份**席位表。</summary>
        private static void RoomOverWire_TwoClientsShareOneSeatTable()
        {
            using (var h = new Harness())
            {
                Client a = h.ConnectAndHandshake("剑士");
                Client b = h.ConnectAndHandshake("法师");

                a.JoinRoom(string.Empty, 1001);
                h.WaitUntil(() => a.LastRoom() != null, "甲收到席位表");

                string roomId = a.LastRoom().RoomId;
                b.JoinRoom(roomId, 1001);

                h.WaitUntil(() => b.LastRoom() != null && b.LastRoom().Members.Count == 2,
                    "乙也进房后两边都是 2 人");

                RoomState seenByA = a.LastRoom();

                Check("走网线：两个客户端进同一个房间 → 都收到 2 人席位表（房号一致、容量 4、房主是第一个）",
                    seenByA.RoomId == roomId && b.LastRoom().RoomId == roomId
                    && seenByA.Members.Count == 2 && b.LastRoom().Members.Count == 2
                    && seenByA.Capacity == NetContract.MaxRoomMembers
                    && seenByA.Members[0].IsHost && !seenByA.Members[1].IsHost
                    && seenByA.Members[0].PlayerName == "剑士"
                    && seenByA.Members[1].PlayerName == "法师",
                    "房号=" + seenByA.RoomId + "，甲看到 " + seenByA.Members.Count + " 人，乙看到 "
                    + b.LastRoom().Members.Count + " 人，names=" + Names(seenByA));

                Check("走网线：服务端房间表里也是 2 个席位",
                    h.Registry.RoomCount == 1 && h.Registry.Find(roomId)!.SeatCount == 2,
                    "房间数=" + h.Registry.RoomCount);
            }
        }

        /// <summary>房间满了：第三个客户端收到"说清原因"的拒绝，而且**没进房**。</summary>
        private static void RoomOverWire_FourthClientIsRejected()
        {
            using (var h = new Harness(roomCapacity: 2))
            {
                Client a = h.ConnectAndHandshake("剑士");
                Client b = h.ConnectAndHandshake("法师");
                Client c = h.ConnectAndHandshake("弓手");

                a.JoinRoom(string.Empty, 1001);
                h.WaitUntil(() => a.LastRoom() != null, "甲收到席位表");

                string roomId = a.LastRoom().RoomId;
                b.JoinRoom(roomId, 1001);
                h.WaitUntil(() => b.LastRoom() != null && b.LastRoom().Members.Count == 2, "房间满（2/2）");

                c.JoinRoom(roomId, 1001);
                h.WaitUntil(() => c.LastError() != null, "丙收到拒绝");

                Check("走网线：房间满 → 第三个客户端收到 ErrorResponse（1001 + 人话）",
                    c.LastError().Code == NetErrors.RoomFull && c.LastError().Message.Contains("已满"),
                    "code=" + c.LastError().Code + "，message=" + c.LastError().Message);

                Check("走网线：被拒的客户端**没进房**（一份含自己的席位表都没收到）",
                    c.LastRoom() == null && h.Registry.Find(roomId)!.SeatCount == 2,
                    "丙收到的席位表=" + (c.LastRoom() == null ? "无" : c.LastRoom().Members.Count + " 人")
                    + "，服务端席位数=" + h.Registry.Find(roomId)!.SeatCount);
            }
        }

        /// <summary>主动离开：离开者收到"你不在任何房间"，留下的人收到 1 人席位表。</summary>
        private static void RoomOverWire_LeaveClearsBothSides()
        {
            using (var h = new Harness())
            {
                Client a = h.ConnectAndHandshake("剑士");
                Client b = h.ConnectAndHandshake("法师");

                a.JoinRoom(string.Empty, 1001);
                h.WaitUntil(() => a.LastRoom() != null, "甲收到席位表");
                b.JoinRoom(a.LastRoom().RoomId, 1001);
                h.WaitUntil(() => a.LastRoom().Members.Count == 2, "两人都在房里");

                b.LeaveRoom();

                h.WaitUntil(() => b.LastRoom() != null && string.IsNullOrEmpty(b.LastRoom().RoomId)
                                  && a.LastRoom().Members.Count == 1,
                    "乙收到空房间状态、甲收到 1 人席位表");

                Check("走网线：主动离开 → 离开者收到「你不在任何房间」（房号为空），留下的人收到 1 人席位表",
                    string.IsNullOrEmpty(b.LastRoom().RoomId) && b.LastRoom().Members.Count == 0
                    && a.LastRoom().Members.Count == 1 && a.LastRoom().Members[0].PlayerName == "剑士",
                    "乙的房号=\"" + b.LastRoom().RoomId + "\"，甲的席位数=" + a.LastRoom().Members.Count);

                Check("走网线：离开后服务端房间还在（还有人），席位表广播计数增加",
                    h.Registry.RoomCount == 1 && h.Registry.Rooms.Count == 1 && h.Rooms.StateBroadcasts >= 3,
                    "房间数=" + h.Registry.RoomCount + "，广播次数=" + h.Rooms.StateBroadcasts);
            }
        }

        /// <summary>客户端断开（不发离开请求）：席位必须自动释放，房空了要删掉。</summary>
        private static void RoomOverWire_DisconnectFreesTheSeat()
        {
            using (var h = new Harness())
            {
                Client a = h.ConnectAndHandshake("剑士");
                Client b = h.ConnectAndHandshake("法师");

                a.JoinRoom(string.Empty, 1001);
                h.WaitUntil(() => a.LastRoom() != null, "甲收到席位表");
                b.JoinRoom(a.LastRoom().RoomId, 1001);
                h.WaitUntil(() => a.LastRoom().Members.Count == 2, "两人都在房里");

                b.Transport.Close();        // 拔线：不发 LeaveRoom

                h.WaitUntil(() => a.LastRoom().Members.Count == 1, "甲收到只剩自己的席位表");

                Check("走网线：客户端拔线 → 席位自动释放（不留僵尸占位），甲收到 1 人席位表",
                    a.LastRoom().Members.Count == 1 && a.LastRoom().Members[0].PlayerName == "剑士"
                    && h.Registry.RoomCount == 1,
                    "甲的席位数=" + a.LastRoom().Members.Count + "，房间数=" + h.Registry.RoomCount);

                a.LeaveRoom();
                h.WaitUntil(() => h.Registry.RoomCount == 0, "房空之后被删掉");

                Check("走网线：最后一个人离开 → 空房间被删掉（房号不会一直涨）",
                    h.Registry.RoomCount == 0,
                    "房间数=" + h.Registry.RoomCount);
            }
        }

        // ====================================================================
        //  六、状态同步（S5）
        // ====================================================================

        /// <summary>两个客户端在同一个房间里 —— 收到的世界必须**逐字一致**。</summary>
        private static void Snapshot_TwoClientsSeeTheSameWorld()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                Client b = h.JoinSameRoom(h.ConnectAndHandshake("法师"));

                h.TickBattles(60);

                WorldSnapshot sa = a.LastSnapshot();
                WorldSnapshot sb = b.LastSnapshot();

                bool sameTick = sa != null && sb != null && sa.ServerTick == sb.ServerTick;
                bool sameWorld = sameTick && SameWorld(sa, sb);
                bool matchesServer = sameTick && SameWorld(sa, h.Battles.Find(a.RoomId)!.ToSnapshot());

                Check("状态同步：两个客户端收到的最后一张快照帧号相同、内容逐字段一致",
                    sameTick && sameWorld,
                    sa == null || sb == null
                        ? "有一边没收到快照"
                        : $"帧 {sa.ServerTick}/{sb.ServerTick}，单位 {sa.Entities.Count}/{sb.Entities.Count}，内容一致={sameWorld}");

                Check("状态同步：客户端收到的快照 = 服务端世界里那一下的真实状态",
                    matchesServer,
                    matchesServer ? "" : "客户端内容与服务端 ToSnapshot() 不一致");

                Check("状态同步：一局里的怪数量 = `Dungeon` 表写的数量（S5b 起英雄另由席位决定）",
                    sa != null && CountKind(sa, 1) == MonstersInDungeon(h, 1001) && h.Battles.Find(a.RoomId)!.AliveMonsterCount == MonstersInDungeon(h, 1001)
                    && CountKind(sa, 0) == 2,
                    sa == null
                        ? "没收到快照"
                        : $"怪 {CountKind(sa, 1)} 只，英雄 {CountKind(sa, 0)} 个（两个客户端各一个）");
            }
        }

        /// <summary>世界得**真的在动**（否则"同步"了一张静止的图，什么也证明不了）。</summary>
        private static void Snapshot_WorldActuallyMoves()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));

                h.TickBattles(5);
                int earlyX = FindEntity(a.LastSnapshot(), 2)?.PosXMm ?? int.MinValue;

                h.TickBattles(25);
                int lateX = FindEntity(a.LastSnapshot(), 2)?.PosXMm ?? int.MinValue;

                h.TickBattles(1);
                int lastX = FindEntity(a.LastSnapshot(), 2)?.PosXMm ?? int.MinValue;

                Check("状态同步：怪物的位置随帧变化（世界真的在跑，不是一张静止图）",
                    earlyX != int.MinValue && lateX != int.MinValue && earlyX != lateX,
                    $"第 5 帧 x={earlyX}，第 30 帧 x={lateX}，第 31 帧 x={lastX}");

                Check("状态同步：巡逻是**确定性**的（超过上限会掉头，不会一直往外走）",
                    Math.Abs(lastX - h.Tables.FindDungeon(1001)!.Value.SpawnRadiusMm) <= 500,
                    "第 31 帧 x=" + lastX + "（应当在 1500~2500 的巡逻区间里）");
            }
        }

        /// <summary>伤害由**服务端**算（复用共享层 `DamageMath`），两个客户端看到同一个结果。</summary>
        private static void Snapshot_DamageIsAuthoritative()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                Client b = h.JoinSameRoom(h.ConnectAndHandshake("法师"));

                h.TickBattles(2);

                DungeonBattle battle = h.Battles.Find(a.RoomId)!;
                DamageResult result = battle.ApplyDamage(targetId: 2, damage: 9999, attackerId: 1);

                Check("状态同步：伤害走共享层 `DamageMath`（过量伤害单独算，血不为负）",
                    result.Ok && result.Outcome.IsLethal && result.Outcome.RemainingHp == 0
                    && result.Outcome.Applied == WolfHp(h) && result.Outcome.Overkill == 9999 - WolfHp(h),
                    result.Ok
                        ? $"applied={result.Outcome.Applied}，overkill={result.Outcome.Overkill}，remaining={result.Outcome.RemainingHp}"
                        : "结算失败（找不到目标）");

                h.TickBattles(1);

                EntitySnapshot ea = FindEntity(a.LastSnapshot(), 2);
                EntitySnapshot eb = FindEntity(b.LastSnapshot(), 2);

                Check("状态同步：打死之后两个客户端看到的血与存活状态一致（且都来自服务端）",
                    ea != null && eb != null && ea.Hp == 0 && eb.Hp == 0 && !ea.Alive && !eb.Alive,
                    ea == null || eb == null
                        ? "有一边没收到快照"
                        : $"甲看到 hp={ea.Hp}/alive={ea.Alive}，乙看到 hp={eb.Hp}/alive={eb.Alive}");

                Check("状态同步：怪物少了一只（服务端权威的世界里也是）",
                    battle.AliveMonsterCount == MonstersInDungeon(h, 1001) - 1,
                    "活着的老怪数=" + battle.AliveMonsterCount);
            }
        }

        /// <summary>快照率 = tick 率：推 60 帧，每边就该收到 60 张。</summary>
        private static void Snapshot_RateIsOnePerTick()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));

                h.TickBattles(60);

                Check("状态同步：推 60 逻辑帧 → 客户端收到 60 张快照（快照率 = tick 率 = 30Hz）",
                    a.SnapshotCount() == 60 && h.Battles.TicksRun == 60,
                    $"客户端收到 {a.SnapshotCount()} 张，服务端推进 {h.Battles.TicksRun} 帧");

                Check("状态同步：帧号单调递增（客户端不会看到倒流的帧）",
                    MonotonicTicks(a),
                    "帧号序列里出现了倒退或重复");
            }
        }

        /// <summary>房间没了 → 战斗世界要被回收（否则就是内存泄漏 + 房号污染）。</summary>
        private static void Snapshot_BattleIsRecycledWhenRoomDies()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));

                h.TickBattles(3);
                Check("状态同步：房间里有人时会开一局（世界数 = 1）",
                    h.Battles.BattleCount == 1, "世界数=" + h.Battles.BattleCount);

                a.LeaveRoom();
                h.WaitUntil(() => h.Registry.RoomCount == 0, "房空了");
                h.TickBattles(1);

                Check("状态同步：房空之后战斗世界被回收（不会留下没人看的世界）",
                    h.Battles.BattleCount == 0 && h.Battles.BattlesDropped == 1,
                    $"世界数={h.Battles.BattleCount}，回收数={h.Battles.BattlesDropped}");
            }
        }

        /// <summary>两张快照的世界内容是否逐字段一致。</summary>
        /// <param name="a">甲。</param>
        /// <param name="b">乙。</param>
        /// <returns>一致返回 true。</returns>
        private static bool SameWorld(WorldSnapshot a, WorldSnapshot b)
        {
            if (a == null || b == null || a.ServerTick != b.ServerTick || a.Entities.Count != b.Entities.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Entities.Count; i++)
            {
                EntitySnapshot x = a.Entities[i];
                EntitySnapshot y = b.Entities[i];

                if (x.EntityId != y.EntityId || x.ConfigId != y.ConfigId || x.Kind != y.Kind
                    || x.Hp != y.Hp || x.MaxHp != y.MaxHp || x.PosXMm != y.PosXMm
                    || x.PosZMm != y.PosZMm || x.FacingDeg != y.FacingDeg || x.Alive != y.Alive)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>从快照里找某个实例。</summary>
        /// <param name="snapshot">快照。</param>
        /// <param name="entityId">实例编号。</param>
        /// <returns>条目或 null。</returns>
        private static EntitySnapshot FindEntity(WorldSnapshot snapshot, int entityId)
        {
            if (snapshot == null)
            {
                return null;
            }

            for (int i = 0; i < snapshot.Entities.Count; i++)
            {
                if (snapshot.Entities[i].EntityId == entityId)
                {
                    return snapshot.Entities[i];
                }
            }

            return null;
        }

        /// <summary>客户端收到的帧号序列是否单调递增。</summary>
        /// <param name="client">客户端。</param>
        /// <returns>单调返回 true。</returns>
        private static bool MonotonicTicks(Client client)
        {
            int last = -1;

            for (int i = 0; i < client.Received.Count; i++)
            {
                if (client.Received[i].Snapshot == null)
                {
                    continue;
                }

                int tick = client.Received[i].Snapshot.ServerTick;

                if (tick <= last)
                {
                    return false;
                }

                last = tick;
            }

            return true;
        }

        // ====================================================================
        //  七、输入上行（S5b）
        // ====================================================================

        /// <summary>输入只动**自己**的英雄（不串台）。</summary>
        private static void Input_MovesOnlyYourOwnHero()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                Client b = h.JoinSameRoom(h.ConnectAndHandshake("法师"));

                h.TickBattles(1);       // 先让 `ReconcileHeroes` 把英雄放出来
                h.MoveFor(a, moveX: 1000, moveY: 0, ticks: 10);

                BattleEntity heroA = h.HeroOf(a);
                BattleEntity heroB = h.HeroOf(b);

                // 期望值**从表里算**：英雄出生点 + 10 帧 × （Hero.moveSpeed ÷ 30Hz）
                int step = h.Tables.FindHero(DungeonBattle.HeroConfigId)!.Value.MoveSpeedMmPerSec
                           / NBC.Shared.Net.NetContract.TickRate;
                int expectedX = DungeonBattle.HeroSpawnPoint(0).X + 10 * step;

                Check($"输入上行：A 一直往右按 → A 的英雄往右走（10 帧 × {step}mm = {10 * step}mm）",
                    heroA != null && heroA.PosXmm == expectedX,
                    heroA == null ? "A 没有英雄" : ($"A 的英雄 x={heroA.PosXmm}（期望 {expectedX}）"));

                // ⚠️ 期望值**从 `HeroSpawnPoint` 算**，不要抄坐标：2026-09-26 出生点半径从
                //    1000mm 收到 500mm（把玩家移出 BOSS 仇恨圈）时，这条硬编码的 1000 就红了 ——
                //    红得对（它确实变了），但**断言里不该出现手抄的坐标**。
                (int spawnBX, int spawnBZ) = DungeonBattle.HeroSpawnPoint(1);

                Check("输入上行：B 没发输入 → B 的英雄原地不动（不串台）",
                    heroB != null && heroB.PosXmm == spawnBX && heroB.PosZmm == spawnBZ,
                    heroB == null
                        ? "B 没有英雄"
                        : ($"B 的英雄 ({heroB.PosXmm}, {heroB.PosZmm})（期望 B 的出生点 ({spawnBX}, {spawnBZ})）"));
            }
        }

        /// <summary>服务端**限速**：客户端一帧发 10 条输入，英雄也只走一格。</summary>
        private static void Input_ServerRateLimitsToOneStepPerTick()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(1);

                int before = h.HeroOf(a)!.PosXmm;

                // 连发 10 条（模拟"客户端 60Hz 发送"或"网络抖动后一次到 10 条"）
                for (int i = 0; i < 10; i++)
                {
                    a.SendInput(moveX: 1000, moveY: 0);
                }

                h.TickBattles(1);

                int after = h.HeroOf(a)!.PosXmm;

                Check("输入上行：一帧收到 10 条输入 → 只走一格（移动是状态，发得密不影响速度）",
                    after - before == h.Tables.FindHero(DungeonBattle.HeroConfigId)!.Value.MoveSpeedMmPerSec / NetContract.TickRate,
                    $"走了 {after - before}mm（期望 {h.Tables.FindHero(DungeonBattle.HeroConfigId)!.Value.MoveSpeedMmPerSec / NetContract.TickRate}mm）");
            }
        }

        /// <summary>
        /// 输入的**保鲜期**：停发之后最多再走 `InputFreshTicks` 帧，然后**必须停下**。
        /// <para>
        /// ⚠️ 这条用例第一版我写成"停发就立刻不动" —— **断言错了**。保鲜期是**有意**的
        /// （容忍网络抖动/丢一条输入），代价就是"松手后还会滑最多 6 帧"。
        /// 正确的契约有两条：① 保鲜期走完必须彻底停；② 滑出去的步数不超过保鲜期。
        /// </para>
        /// </summary>
        private static void Input_ExpiresWhenTheClientStopsSending()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(1);

                h.MoveFor(a, moveX: 1000, moveY: 0, ticks: 3);
                int afterLastInput = h.HeroOf(a)!.PosXmm;

                h.TickBattles(RoomBattleService.InputFreshTicks + 2);      // 把保鲜期走完
                int afterFreshWindow = h.HeroOf(a)!.PosXmm;

                h.TickBattles(10);                                          // 再推 10 帧，看它是否真的停了
                int afterSilence = h.HeroOf(a)!.PosXmm;

                int overshoot = afterFreshWindow - afterLastInput;

                Check("输入上行：停发之后最多再滑 6 帧（保鲜期是有意的，容忍抖动/丢一条输入）",
                    overshoot > 0 && overshoot <= RoomBattleService.InputFreshTicks * h.Tables.FindHero(DungeonBattle.HeroConfigId)!.Value.MoveSpeedMmPerSec / NetContract.TickRate,
                    $"最后一次输入后 x={afterLastInput}，保鲜期走完 x={afterFreshWindow}（滑了 {overshoot}mm）");

                Check("输入上行：保鲜期走完之后彻底停下（不会一直往前走）",
                    afterSilence == afterFreshWindow,
                    $"保鲜期后 x={afterFreshWindow}，再推 10 帧 x={afterSilence}（期望不变）");
            }
        }

        /// <summary>冒名输入（`player_id` 不是自己的）→ 被拒 + 收到错误，且谁都别动。</summary>
        private static void Input_PlayerMismatchIsRejected()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                Client b = h.JoinSameRoom(h.ConnectAndHandshake("法师"));
                h.TickBattles(1);

                int heroABefore = h.HeroOf(a)!.PosXmm;
                int heroBBefore = h.HeroOf(b)!.PosXmm;

                // A 假装自己是 B（作弊/客户端 bug）
                a.SendInputAs(playerId: b.Ack.PlayerId, moveX: 1000, moveY: 0);
                h.TickBattles(2);
                h.WaitUntil(() => a.LastError() != null, "A 收到拒绝");

                Check("输入上行：冒名输入（player_id 不是自己的）→ 回 ErrorResponse{1004}",
                    a.LastError() != null && a.LastError().Code == NetErrors.PlayerMismatch,
                    a.LastError() == null ? "没收到错误" : ("code=" + a.LastError().Code));

                Check("输入上行：冒名输入不会动任何人的英雄",
                    h.HeroOf(a)!.PosXmm == heroABefore && h.HeroOf(b)!.PosXmm == heroBBefore,
                    $"A={h.HeroOf(a)!.PosXmm}（原 {heroABefore}），B={h.HeroOf(b)!.PosXmm}（原 {heroBBefore}）");
            }
        }

        /// <summary>普攻：走近 → 打中 → 掉血；冷却期间再打不生效。</summary>
        private static void Attack_LandsInRangeAndRespectsCooldown()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(1);

                DungeonBattle battle = h.BattleOf(a)!;
                int wolfId = FirstAliveWolfId(battle);

                bool inRange = h.MoveUntilInRange(a, wolfId, maxTicks: 40);
                int hpBefore = battle.Find(wolfId)!.Hp;

                a.SendInput(moveX: 0, moveY: 0, actionBits: 1u, targetEntityId: wolfId);
                h.TickBattles(1);

                int hpAfter = battle.Find(wolfId)!.Hp;

                Check("输入上行：走近之后普攻打中 → 狼掉血（伤害由服务端算）",
                    inRange && hpAfter == hpBefore - h.Tables.FindSkill(h.Tables.FindHero(DungeonBattle.HeroConfigId)!.Value.SkillIds[0])!.Value.Damage,
                    $"射程内={inRange}，hp {hpBefore} → {hpAfter}（期望 -{h.Tables.FindSkill(h.Tables.FindHero(DungeonBattle.HeroConfigId)!.Value.SkillIds[0])!.Value.Damage}）");

                // 冷却中再打一次：不该再掉血
                a.SendInput(moveX: 0, moveY: 0, actionBits: 1u, targetEntityId: wolfId);
                h.TickBattles(1);
                int hpAfterSpam = battle.Find(wolfId)!.Hp;

                Check("输入上行：冷却中再按攻击 → 不掉血（防住「按住攻击每秒 30 下」）",
                    hpAfterSpam == hpAfter,
                    $"hp={hpAfterSpam}（期望仍是 {hpAfter}），冷却剩余={battle.Find(wolfId)!.Hp}");

                // 等冷却走完（15 帧）再打 → 又能打中
                int attackerCooldown = h.HeroOf(a)!.AttackCooldownTicksLeft;
                h.SendIdleInputFor(a, attackerCooldown + 2);
                a.SendInput(moveX: 0, moveY: 0, actionBits: 1u, targetEntityId: wolfId);
                h.TickBattles(1);

                Check("输入上行：冷却走完（15 帧 = 0.5 秒）再打 → 又能打中",
                    battle.Find(wolfId)!.Hp == hpAfter - h.Tables.FindSkill(h.Tables.FindHero(DungeonBattle.HeroConfigId)!.Value.SkillIds[0])!.Value.Damage,
                    $"hp={battle.Find(wolfId)!.Hp}（期望 {hpAfter - h.Tables.FindSkill(h.Tables.FindHero(DungeonBattle.HeroConfigId)!.Value.SkillIds[0])!.Value.Damage}）");
            }
        }

        /// <summary>射程外攻击：不掉血，**也不回错误**（按 S5b 定的判据：只记日志）。</summary>
        private static void Attack_OutOfRangeIsRefusedSilently()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(1);

                DungeonBattle battle = h.BattleOf(a)!;
                int wolfId = FirstAliveWolfId(battle);
                int hpBefore = battle.Find(wolfId)!.Hp;

                // 出生点在 (-1000, 0)，狼在 (≈2000, 1000)：**圆判据**下欧氏距离 ≈2236 > 射程 2000
                // （旧切比雪夫判据是 3000，两者结论一致；对角方向才会分歧，见 BasicAttackRange_IsCircular）
                a.SendInput(moveX: 0, moveY: 0, actionBits: 1u, targetEntityId: wolfId);
                h.TickBattles(2);

                Check("输入上行：射程外攻击 → 不掉血",
                    battle.Find(wolfId)!.Hp == hpBefore,
                    $"hp={battle.Find(wolfId)!.Hp}（期望不变 {hpBefore}）");

                Check("输入上行：射程外被拒**只记日志、不回错误**（回错误会变成每帧刷屏）",
                    a.LastError() == null && h.Battles.AttacksRefused >= 1,
                    $"客户端收到错误={a.LastError() != null}，服务端拒绝计数={h.Battles.AttacksRefused}");
            }
        }

        /// <summary>
        /// 射程是**圆**（2026-10-20 改）：判据是距离平方，不是切比雪夫。
        /// <para>⚠️ 这条是**改动本身的判据**：把 `WithinBasicAttackRange` 退回切比雪夫（`max(|dx|,|dz|)`）
        /// ⇒ 下面「对角 1500/1500 应在射程外」那条**必须红**（切比雪夫 1500 ≤ 2000 会判"能打"）。</para>
        /// </summary>
        private static void BasicAttackRange_IsCircular()
        {
            int range = BattleRules.BasicAttackRangeMm;

            // ① 轴向上刚好等于射程 ⇒ 在射程内（圆的边界上）
            Check("尺子（圆）⭐ 轴向 " + range + "mm ⇒ 在射程内（边界上）",
                BattleRules.WithinBasicAttackRange(0, 0, range, 0),
                "WithinBasicAttackRange(0,0," + range + ",0)");

            // ② 轴向上多 1mm ⇒ 射程外
            Check("尺子（圆）⭐ 轴向 " + (range + 1) + "mm ⇒ 射程外",
                !BattleRules.WithinBasicAttackRange(0, 0, range + 1, 0),
                "WithinBasicAttackRange(0,0," + (range + 1) + ",0)");

            // ③ ⭐⭐ 对角：切比雪夫 1500 ≤ 2000（旧判据会放行），但欧氏 2121 > 2000 ⇒ 必须在射程外
            Check("尺子（圆）⭐⭐ **对角 1500/1500 ⇒ 射程外**（切比雪夫会误判成「能打」）",
                !BattleRules.WithinBasicAttackRange(0, 0, 1500, 1500),
                "切比雪夫=1500（旧判据≤2000），欧氏≈2121（新判据>2000）");

            // ④ 对角刚好在圆里：1414/1414 ⇒ 欧氏 1999.6 ⇒ 在射程内
            Check("尺子（圆）⭐ 对角 1414/1414 ⇒ 在射程内（约 1999.6mm）",
                BattleRules.WithinBasicAttackRange(0, 0, 1414, 1414),
                "WithinBasicAttackRange(0,0,1414,1414)");

            // ⑤ 对称性：谁甲谁乙不影响
            Check("尺子（圆）：甲乙互换 ⇒ 结果相同",
                BattleRules.WithinBasicAttackRange(0, 0, 1500, 1500)
                    == BattleRules.WithinBasicAttackRange(1500, 1500, 0, 0),
                "对称性");

            // ⑥ 显示用距离：四舍五入的整数欧氏（1500/1500 ⇒ 2121）
            Check("尺子：显示距离 = 整数欧氏（1500/1500 ⇒ 2121，不引浮点）",
                BattleRules.DistanceMmRounded(0, 0, 1500, 1500) == 2121,
                "实际=" + BattleRules.DistanceMmRounded(0, 0, 1500, 1500));

            // ⑦ 显示距离的边界：0 ⇒ 0；（射程,0）⇒ 射程；（射程,1）⇒ 四舍五入仍是射程（出界时靠调用方 +1 显示）
            Check("尺子：显示距离的边界（0 ⇒ 0；(2000,0) ⇒ 2000；(2000,1) ⇒ 2000 四舍五入）",
                BattleRules.DistanceMmRounded(0, 0, 0, 0) == 0
                    && BattleRules.DistanceMmRounded(0, 0, range, 0) == range
                    && BattleRules.DistanceMmRounded(0, 0, range, 1) == range,
                $"0⇒{BattleRules.DistanceMmRounded(0, 0, 0, 0)}，" +
                $"2000⇒{BattleRules.DistanceMmRounded(0, 0, range, 0)}，" +
                $"2000/1⇒{BattleRules.DistanceMmRounded(0, 0, range, 1)}");
        }

        /// <summary>
        /// **服务端整合判据**（2026-10-20 补）：上一版这 7 条全都直接调 `BattleRules`，
        /// **没有一条穿过 `DungeonBattle`** ⇒ 只改共享层、忘了改服务端，探针照样全绿。
        /// <para>这一节直接造实体、走 `InAttackRange` 与 `TryBasicAttack`（真业务路径）。</para>
        /// </summary>
        private static void BasicAttackRange_ServerUsesCircle()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(1);

                DungeonBattle battle = h.BattleOf(a)!;
                BattleEntity hero = h.HeroOf(a)!;
                int range = DungeonBattle.BasicAttackRangeMm;

                // 造 4 只狼，位置都相对英雄，覆盖：对角 1500/1500、对角 1414/1414、轴向射程、轴向射程+1
                BattleEntity diag1500 = battle.AddEntity(6001, 1, 300, hero.PosXmm + 1500, hero.PosZmm + 1500);
                BattleEntity diag1414 = battle.AddEntity(6001, 1, 300, hero.PosXmm + 1414, hero.PosZmm + 1414);
                BattleEntity axisRange = battle.AddEntity(6001, 1, 300, hero.PosXmm + range, hero.PosZmm);
                BattleEntity axisOver = battle.AddEntity(6001, 1, 300, hero.PosXmm + range + 1, hero.PosZmm);

                Check("服务端⭐ **对角 1500/1500 ⇒ 不在射程内**（真业务路径 `InAttackRange`）",
                    !battle.InAttackRange(hero, diag1500),
                    "InAttackRange(hero, 对角1500/1500)=" + battle.InAttackRange(hero, diag1500));

                Check("服务端⭐ 对角 1414/1414 ⇒ 在射程内（约 1999.6mm）",
                    battle.InAttackRange(hero, diag1414),
                    "InAttackRange(hero, 对角1414/1414)=" + battle.InAttackRange(hero, diag1414));

                Check("服务端⭐ 轴向刚好射程 ⇒ 在射程内（边界 `<=`）",
                    battle.InAttackRange(hero, axisRange),
                    "InAttackRange(hero, 轴向" + range + ")=" + battle.InAttackRange(hero, axisRange));

                Check("服务端⭐ 轴向射程+1 ⇒ 在射程外",
                    !battle.InAttackRange(hero, axisOver),
                    "InAttackRange(hero, 轴向" + (range + 1) + ")=" + battle.InAttackRange(hero, axisOver));

                // ⭐⭐ 这条才是"服务端真的用了圆"的硬证据：走 `TryBasicAttack`（含日志与拒绝原因）
                bool attacked = battle.TryBasicAttack(hero, diag1500.Id, out string reason);
                Check("服务端⭐⭐ **`TryBasicAttack` 对角目标 ⇒ 拒绝**且原因点明射程外",
                    !attacked && reason.Contains("射程外"),
                    "attacked=" + attacked + "，原因=" + reason);

                // AI 度量钉死：仇恨/回家用的是切比雪夫，别哪天被"顺手统一成欧氏"
                Check("服务端⭐ **AI 度量仍是切比雪夫**：`DistanceMm` 对角 1500/1500 ⇒ 1500",
                    DungeonBattle.DistanceMm(hero, diag1500) == 1500,
                    "DistanceMm=" + DungeonBattle.DistanceMm(hero, diag1500));
            }
        }

        /// <summary>席位 ↔ 英雄：一个席位一个英雄，人走英雄走。</summary>
        private static void Heroes_FollowSeats()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(1);

                Check("输入上行：一个席位一个英雄（进房即有）",
                    CountKind(a.LastSnapshot(), 0) == 1,
                    "英雄数=" + CountKind(a.LastSnapshot(), 0));

                Client b = h.JoinSameRoom(h.ConnectAndHandshake("法师"));
                h.TickBattles(2);

                Check("输入上行：第二个人进房 → 世界里多一个英雄（出生点不同）",
                    CountKind(b.LastSnapshot(), 0) == 2
                    && h.HeroOf(a)!.PosXmm != h.HeroOf(b)!.PosXmm,
                    $"英雄数={CountKind(b.LastSnapshot(), 0)}，A=({h.HeroOf(a)!.PosXmm},{h.HeroOf(a)!.PosZmm})，" +
                    $"B=({h.HeroOf(b)!.PosXmm},{h.HeroOf(b)!.PosZmm})");

                int goneEntityId = h.HeroOf(b)!.Id;
                b.LeaveRoom();
                h.TickBattles(2);

                Check("输入上行：有人离房 → 他的英雄被移出世界（不留在场上当靶子）",
                    CountKind(a.LastSnapshot(), 0) == 1 && FindEntity(a.LastSnapshot(), goneEntityId) == null,
                    $"英雄数={CountKind(a.LastSnapshot(), 0)}，离场英雄还在={FindEntity(a.LastSnapshot(), goneEntityId) != null}");
            }
        }

        /// <summary>保鲜期的帧数（写在这里省得每个用例都抄一遍常量）。</summary>
        /// <returns>保鲜期帧数 + 5（留点余量）。</returns>
        private static int DungeonBattleBattleFreshWindow() => RoomBattleService.InputFreshTicks + 5;

        // ====================================================================
        //  八、副本来自配置表（S6）
        // ====================================================================

        /// <summary>四张表都读到了（而且行数对得上）。</summary>
        private static void Tables_AreLoadedFromTheRealCsv()
        {
            using (var h = new Harness())
            {
                ServerTables t = h.Tables;

                Check("配置表：Dungeon / Monster / Hero / Skill 四张表都从**源 CSV**读到了",
                    t.DungeonCount >= 2 && t.MonsterCount >= 3 && t.HeroCount >= 3 && t.SkillCount >= 3
                    && t.FindDungeon(1001) != null && t.FindMonster(6003) != null && t.FindHero(1001) != null,
                    t.Describe());
            }
        }

        /// <summary>
        /// **服务端自述的数值 = 源 CSV 里写的**（2026-09-26，同一个坑第二次：负责人改了 Unity 的
        /// `MonsterConfig.asset` 里狼王血量 → 服务端纹丝不动，因为它读的是源 CSV，看不到 SO）。
        /// <para>⚠️ 断言**直接读源 CSV 文本**对账，而不是再抄一遍数字 ——
        /// 这样"表改了、自述没跟上"也会红。</para>
        /// </summary>
        private static void Tables_SelfReportMatchesTheSourceCsv()
        {
            using (var h = new Harness())
            {
                string path = Path.Combine(h.Tables.SourceDirectory, "Monster.csv");
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);

                // 第 1 行是列名，数据从第 5 行起（4 行表头 —— 见 `CsvSheet.Load`）
                string[] header = lines[0].Split(',');
                int idAt = Array.IndexOf(header, "id");
                int hpAt = Array.IndexOf(header, "hp");
                int attackAt = Array.IndexOf(header, "attack");

                int csvHp = -1;
                int csvAttack = -1;

                for (int i = 4; i < lines.Length; i++)
                {
                    string[] cells = lines[i].Split(',');

                    if (cells.Length > attackAt && int.Parse(cells[idAt]) == 6003)
                    {
                        csvHp = int.Parse(cells[hpAt]);
                        csvAttack = int.Parse(cells[attackAt]);
                    }
                }

                MonsterRow? row = h.Tables.FindMonster(6003);
                string report = h.Tables.DescribeMonster(6003);

                Check("配置表：服务端**自述的数值** = `Configs\\Design\\Monster.csv` 里写的（狼王 6003）",
                    row != null && row.Value.Hp == csvHp && row.Value.Attack == csvAttack
                    && report.Contains("hp " + csvHp) && report.Contains("攻 " + csvAttack),
                    $"CSV: hp {csvHp} / 攻 {csvAttack}；ServerTables: "
                    + (row == null ? "（没读到 6003）" : $"hp {row.Value.Hp} / 攻 {row.Value.Attack}")
                    + "；自述：「" + report + "」");
            }
        }

        /// <summary>阵容由 `Dungeon` 表决定（普通怪写重复＝多只 + 一只 BOSS），血量来自 `Monster` 表。</summary>
        private static void Tables_DecideTheDungeonLineup()
        {
            using (var h = new Harness())
            {
                DungeonRow? row = h.Tables.FindDungeon(1001);
                DungeonBattle battle = DungeonBattle.FromDungeon("probe", 1001, h.Tables, out string error);

                if (battle == null)
                {
                    Check("配置表：副本 1001 能开起来", false, error);
                    return;
                }

                int expectedMonsters = row!.Value.Monsters.Length;
                int expectedAll = expectedMonsters + 1;     // + BOSS

                Check("配置表：副本 1001 的怪 = 表里写的（普通怪按重复次数刷 + 1 只 BOSS）",
                    battle.Entities.Count == expectedAll && battle.BossCount == 1
                    && battle.AliveMonsterCount == expectedAll,
                    $"世界里 {battle.Entities.Count} 个怪（表里写 {expectedMonsters} 普通怪 + 1 BOSS），BOSS {battle.BossCount} 只");

                // 血量逐只对表（普通怪用 Monster.hp；这里同一只怪刷了两只，所以只比第一只）
                MonsterRow? firstMonster = h.Tables.FindMonster(row.Value.Monsters[0]);
                MonsterRow? boss = h.Tables.FindMonster(row.Value.BossId);

                BattleEntity wolf = battle.Entities[0];
                BattleEntity bossEntity = battle.Entities[battle.Entities.Count - 1];

                Check("配置表：每只怪的血量来自 `Monster.hp`（含 BOSS 那 2000 血）",
                    firstMonster != null && wolf.MaxHp == firstMonster.Value.Hp
                    && boss != null && bossEntity.MaxHp == boss.Value.Hp && bossEntity.IsBoss,
                    $"普通怪 {wolf.MaxHp}（表 {firstMonster?.Hp}），BOSS {bossEntity.MaxHp}（表 {boss?.Hp}）");

                Check("配置表：怪的出生点用 `Dungeon.spawnRadiusMm`（BOSS 在对面）",
                    wolf.PosXmm == row.Value.SpawnRadiusMm && bossEntity.PosXmm == -row.Value.SpawnRadiusMm,
                    $"普通怪 x={wolf.PosXmm}，BOSS x={bossEntity.PosXmm}（半径 {row.Value.SpawnRadiusMm}）");
            }
        }

        /// <summary>
        /// **负向对照**：副本不在表里就必须**开不起来**（而不是开一个空世界）。
        /// <para>开空世界的症状是"进去什么都没有"，玩家只会以为游戏坏了 —— 所以这里必须响亮地失败。</para>
        /// </summary>
        private static void Tables_UnknownDungeonFailsLoudly()
        {
            using (var h = new Harness())
            {
                DungeonBattle battle = DungeonBattle.FromDungeon("probe", 9999, h.Tables, out string error);

                Check("配置表：副本 9999 不在表里 → 开不了世界，且说明原因（不静默开空世界）",
                    battle == null && error.Contains("不在"),
                    battle != null ? "居然开出来了" : ("error=" + error));
            }
        }

        /// <summary>英雄的血 / 移速 / 普攻伤害都由表决定。</summary>
        private static void Tables_DecideHeroStatsAndAttackDamage()
        {
            using (var h = new Harness())
            {
                HeroRow? hero = h.Tables.FindHero(DungeonBattle.HeroConfigId);
                DungeonBattle battle = DungeonBattle.FromDungeon("probe", 1001, h.Tables, out _);

                if (hero == null || battle == null)
                {
                    Check("配置表：英雄数值能从表里算出来", false, "英雄行或世界没建起来");
                    return;
                }

                int expectedStep = hero.Value.MoveSpeedMmPerSec / NetContract.TickRate;
                SkillRow? skill = h.Tables.FindSkill(hero.Value.SkillIds[0]);

                Check("配置表：英雄血量/移速来自 `Hero` 表（移速 = 毫米每秒 ÷ 30Hz）",
                    battle.HeroMaxHp == hero.Value.Hp && battle.HeroMoveMmPerTick == expectedStep,
                    $"血量 {battle.HeroMaxHp}（表 {hero.Value.Hp}），每帧 {battle.HeroMoveMmPerTick}mm" +
                    $"（表 {hero.Value.MoveSpeedMmPerSec}mm/s ÷ {NetContract.TickRate}）");

                Check("配置表：普攻伤害来自 `Skill` 表（英雄第一个技能的 damage）",
                    skill != null && battle.BasicAttackDamage == skill.Value.Damage && battle.BasicAttackDamage > 0,
                    $"普攻 {battle.BasicAttackDamage}（技能 {hero.Value.SkillIds[0]} 表值 {skill?.Damage}）");
            }
        }

        // ====================================================================
        //  九、BOSS AI（S6b：A10 状态机驱动）
        // ====================================================================

        /// <summary>
        /// BOSS 的**仇恨范围**（2026-09-26 补）：进圈才追、出圈就回家。
        /// <para>⚠️ 这条规则是因为负责人实测"老是被 BOSS 打死、没法测"才补的 ——
        /// 原来 BOSS 无脑全图锁人，副本里"先清小怪再打 BOSS"根本做不到。</para>
        /// </summary>
        private static void Boss_ChasesTheHero()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(1);

                DungeonBattle battle = h.BattleOf(a)!;
                BattleEntity boss = FindBoss(battle)!;
                BattleEntity hero = h.HeroOf(a)!;

                // ① 出生点必须在仇恨范围**外**（否则一进房就被贴脸）
                int spawnDistance = DungeonBattle.DistanceMm(boss, hero);

                Check("BOSS AI：出生点不在 BOSS 的仇恨范围内（进房是安全的）",
                    spawnDistance > DungeonBattle.BossAggroRangeMm,
                    $"出生点距离 {spawnDistance}mm > 仇恨范围 {DungeonBattle.BossAggroRangeMm}mm");

                // ② 站着不动若干帧：BOSS 既不追也不动（站桩）
                int idleBefore = DungeonBattle.DistanceMm(boss, hero);
                h.SendIdleInputFor(a, 20);
                int idleAfter = DungeonBattle.DistanceMm(boss, hero);

                Check("BOSS AI：英雄在圈外时 BOSS **不追**（距离不变、状态站桩）",
                    idleBefore == idleAfter && battle.Brains[0].StateName == "站桩",
                    $"距离 {idleBefore}mm → {idleAfter}mm，状态={battle.Brains[0].StateName}");

                // ③ 英雄往 -x 走（朝 BOSS 去）进圈 → BOSS 必须**有反应**
                //    ⚠️ 断言不能只写"距离变小"：英雄这一走很可能**直接落进射程**（2000mm），
                //       那时 BOSS 的行为是"打"而不是"走近"，距离当然不变 —— 我第一版就是这么误判的。
                //       正确的判据是：**进圈了** 且 **状态机切到追击/攻击** 且 **确实发生了交战**
                //       （拉近距离，或者你已经在掉血）。
                h.MoveFor(a, moveX: -1000, moveY: 0, ticks: 4);

                int before = DungeonBattle.DistanceMm(boss, hero);
                int heroHpBefore = hero.Hp;
                h.SendIdleInputFor(a, 20);
                int after = DungeonBattle.DistanceMm(boss, hero);

                bool inAggro = before <= DungeonBattle.BossAggroRangeMm;
                bool engaged = battle.Brains[0].StateName == "追击" || battle.Brains[0].StateName == "攻击";
                bool closing = after < before;
                bool takingHits = hero.Hp < heroHpBefore;

                Check("BOSS AI：英雄进圈之后 BOSS 有反应（拉近距离，或已经能打到你）",
                    inAggro && engaged && (closing || takingHits),
                    $"进圈距离 {before}mm（仇恨 {DungeonBattle.BossAggroRangeMm}mm），20 帧后 {after}mm；" +
                    $"状态={battle.Brains[0].StateName}；英雄 HP {heroHpBefore} → {hero.Hp}");
            }
        }

        /// <summary>
        /// BOSS 的 **leash（回位）**：英雄跑出仇恨范围后，BOSS 走回自己的出生点。
        /// </summary>
        private static void Boss_ReturnsHomeAfterLosingAggro()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(1);

                DungeonBattle battle = h.BattleOf(a)!;
                BossBrain brain = battle.Brains[0];
                BattleEntity boss = FindBoss(battle)!;
                BattleEntity hero = h.HeroOf(a)!;

                // 先把 BOSS 从家里**挪开**（不依赖"风筝"那套操作）：
                // ⚠️ 原来我用"把英雄引出来让 BOSS 追"来制造位移，结果 BOSS 一进射程就**打人而不是追人**，
                //    只挪了 90mm（一个 tick），用例前提压根没成立。造场面就用造场面的手段。
                for (int i = 0; i < 12; i++)
                {
                    battle.MoveTowardsPoint(boss, brain.HomeXmm + 2000, 0);
                }

                int strayed = DungeonBattle.DistanceMmToPoint(boss, brain.HomeXmm, brain.HomeZmm);

                // 再让英雄往反方向跑掉（+x），跑到仇恨圈外
                h.MoveFor(a, moveX: 1000, moveY: 0, ticks: 30);

                int heroDistance = DungeonBattle.DistanceMm(boss, hero);

                // 给 BOSS 足够帧数走回家
                h.SendIdleInputFor(a, 60);

                int homeDistance = DungeonBattle.DistanceMmToPoint(boss, brain.HomeXmm, brain.HomeZmm);

                Check("BOSS AI：英雄跑出仇恨范围后，BOSS **走回出生点**（leash）",
                    strayed > DungeonBattle.BossLeashSlackMm
                    && heroDistance > DungeonBattle.BossAggroRangeMm
                    && homeDistance <= DungeonBattle.BossLeashSlackMm
                    && brain.StateName == "站桩",
                    $"曾离家 {strayed}mm（英雄在 {heroDistance}mm 外）；回位后离家 {homeDistance}mm" +
                    $"（松弛量 {DungeonBattle.BossLeashSlackMm}mm），状态={brain.StateName}");
            }
        }

        /// <summary>四个席位的出生点**都在 BOSS 仇恨圈外**（用真实表的 `spawnRadiusMm` 算）。</summary>
        private static void Boss_SpawnPointsAreOutsideAggroRange()
        {
            using (var h = new Harness())
            {
                int radius = h.Tables.FindDungeon(1001)!.Value.SpawnRadiusMm;
                int bossX = -radius;            // `FromDungeon` 里 BOSS 摆在 -radius
                int nearest = int.MaxValue;

                for (int seat = 0; seat < 4; seat++)
                {
                    (int x, int z) = DungeonBattle.HeroSpawnPoint(seat);
                    int distance = Math.Max(Math.Abs(x - bossX), Math.Abs(z - 0));

                    if (distance < nearest)
                    {
                        nearest = distance;
                    }
                }

                Check("BOSS AI：四个席位的出生点都在仇恨圈外（用表里的 `spawnRadiusMm` 算）",
                    nearest > DungeonBattle.BossAggroRangeMm,
                    $"BOSS 在 x={bossX}；最近的席位出生点距它 {nearest}mm > 仇恨范围 {DungeonBattle.BossAggroRangeMm}mm");
            }
        }

        /// <summary>够得着就打：英雄掉血，且掉的是 `Monster.attack` 的整数倍。</summary>
        private static void Boss_AttacksInRange()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(1);

                DungeonBattle battle = h.BattleOf(a)!;
                BattleEntity hero = h.HeroOf(a)!;
                BattleEntity boss = FindBoss(battle)!;
                int bossAttack = BossAttack(battle);

                // ⚠️ 2026-09-26 起**出生点不在 BOSS 射程内**了（把玩家移出仇恨圈），
                //    所以要先自己走过去 —— 顺带也就验证了"是玩家决定何时开打"。
                //    ⚠️ 方向必须是 **-x**（BOSS 在 -3000 那边）；默认的 +x 是给右边的狼用的。
                bool reached = h.MoveUntilInRange(a, boss.Id, 40, moveX: -1000);

                int distance = DungeonBattle.DistanceMm(hero, boss);
                int hpBefore = hero.Hp;

                // 站住别动，推 32 帧 ≈ 1 秒 → 应当挨 2~3 下
                h.SendIdleInputFor(a, 32);

                int hpAfter = hero.Hp;
                int lost = hpBefore - hpAfter;

                Check("BOSS AI：走进射程后站着不动会挨打（掉血），且掉的是 `Monster.attack` 的整数倍",
                    reached && lost > 0 && lost % bossAttack == 0,
                    $"走到 {distance}mm（射程 {DungeonBattle.BasicAttackRangeMm}mm）后 hp {hpBefore} → {hpAfter}" +
                    $"（掉了 {lost}，BOSS 攻击 {bossAttack}，整除={lost % bossAttack == 0}）");

                Check("BOSS AI：冷却生效（1 秒内挨的次数 ≤ 3，不是每帧一下）",
                    lost / bossAttack <= 3,
                    $"1 秒挨了 {lost / bossAttack} 下（冷却 15 帧 = 0.5 秒 → 上限 3）");
            }
        }

        /// <summary>没有活着的英雄 → BOSS 停手（状态回到站桩，也不再移动）。</summary>
        private static void Boss_StopsWhenNoHeroIsAlive()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(1);

                DungeonBattle battle = h.BattleOf(a)!;
                BattleEntity boss = FindBoss(battle)!;
                BattleEntity hero = h.HeroOf(a)!;

                battle.ApplyDamage(hero.Id, 99999, boss.Id);   // 服务端直接打死英雄
                int bossX = boss.PosXmm;
                int bossZ = boss.PosZmm;

                h.SendIdleInputFor(a, 10);

                Check("BOSS AI：英雄死了 → BOSS 回到「站桩」且不再移动",
                    battle.Brains[0].StateName == "站桩"
                    && boss.PosXmm == bossX && boss.PosZmm == bossZ,
                    $"状态={battle.Brains[0].StateName}，位置 ({bossX},{bossZ}) → ({boss.PosXmm},{boss.PosZmm})");
            }
        }

        /// <summary>大脑确实挂在 A10 状态机上（三个状态都注册了、有过状态切换）。</summary>
        private static void Boss_BrainRunsOnTheStateMachine()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(1);

                // 英雄往 BOSS 那边走（进仇恨圈）→ 状态机必然发生 Idle → Chase（可能还有 → Attack）
                h.MoveFor(a, moveX: -1000, moveY: 0, ticks: 6);
                h.SendIdleInputFor(a, 12);

                DungeonBattle battle = h.BattleOf(a)!;
                BossBrain brain = battle.Brains[0];

                Check("BOSS AI：大脑用 A10 的状态机（有状态名、发生过状态切换）",
                    brain.TransitionCount > 0 && !string.IsNullOrEmpty(brain.StateName),
                    $"状态={brain.StateName}，切换次数={brain.TransitionCount}");            }
        }

        // ====================================================================
        //  十、掉落一致（S7）
        // ====================================================================

        /// <summary>BOSS 那两条概率都是 10000（万分比）→ **必掉**，且数量在表里的区间内。</summary>
        private static void Drops_BossAlwaysDropsBoth()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(2);

                DungeonBattle battle = h.BattleOf(a)!;
                BattleEntity boss = FindBoss(battle)!;
                int bossConfigId = boss.ConfigId;
                int before = a.DropCount();

                battle.ApplyDamage(boss.Id, 999999, h.HeroOf(a)!.Id);   // 服务端直接打死 BOSS
                h.TickBattles(1);

                List<DropEvent> drops = a.DropsSince(before);
                IReadOnlyList<DropRow> rows = h.Tables.FindDrops(bossConfigId);

                bool allInRange = true;
                for (int i = 0; i < drops.Count; i++)
                {
                    DropRow row = FindDropRow(h.Tables, bossConfigId, drops[i].ItemId);
                    if (row.ItemId == 0 || drops[i].Count < row.CountMin || drops[i].Count > row.CountMax)
                    {
                        allInRange = false;
                    }
                }

                Check("掉落：BOSS 两条概率都是 10000（万分比）→ **必掉**，且数量落在表里区间内",
                    drops.Count == rows.Count && drops.Count >= 2 && allInRange,
                    $"表里 {rows.Count} 条，实际掉了 {drops.Count} 条：" + DescribeDrops(drops));
            }
        }

        /// <summary>两个客户端收到**同一份**掉落（同一个 payload、同一份字节）。</summary>
        private static void Drops_TwoClientsReceiveTheSameDrop()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                Client b = h.JoinSameRoom(h.ConnectAndHandshake("法师"));
                h.TickBattles(2);

                DungeonBattle battle = h.BattleOf(a)!;
                int beforeA = a.DropCount();
                int beforeB = b.DropCount();

                battle.ApplyDamage(FindBoss(battle)!.Id, 999999, h.HeroOf(a)!.Id);
                h.TickBattles(1);

                List<DropEvent> da = a.DropsSince(beforeA);
                List<DropEvent> db = b.DropsSince(beforeB);

                Check("掉落：两个客户端收到的掉落**逐字段一致**（服务端只序列化一次）",
                    da.Count == db.Count && da.Count > 0 && SameDrops(da, db),
                    $"甲 {da.Count} 条，乙 {db.Count} 条；甲=" + DescribeDrops(da) + "；乙=" + DescribeDrops(db));
            }
        }

        /// <summary>`winner_player_id` = 击杀者（M3 的规则：谁打死归谁）。</summary>
        private static void Drops_WinnerIsTheKiller()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(2);

                DungeonBattle battle = h.BattleOf(a)!;
                long killerPlayerId = h.HeroOf(a)!.PlayerId!;
                int before = a.DropCount();

                battle.ApplyDamage(FindBoss(battle)!.Id, 999999, h.HeroOf(a)!.Id);
                h.TickBattles(1);

                List<DropEvent> drops = a.DropsSince(before);
                bool allMine = drops.Count > 0;

                for (int i = 0; i < drops.Count; i++)
                {
                    if (drops[i].WinnerPlayerId != killerPlayerId)
                    {
                        allMine = false;
                    }
                }

                Check("掉落：`winner_player_id` = 击杀者（M3 的规则）",
                    allMine,
                    $"击杀者是玩家 {killerPlayerId}，掉落归：" + DescribeDrops(drops));
            }
        }

        /// <summary>
        /// **确定性**：同一个房号的两局，同样的击杀序列 → 同样的掉落。
        /// <para>这是"掉落能对账"的前提；也是**不能用 `System.Random`** 的理由（见 `BattleRandom` 文件头）。</para>
        /// </summary>
        private static void Drops_AreDeterministicForTheSameRoom()
        {
            using (var h = new Harness())
            {
                DungeonBattle one = DungeonBattle.FromDungeon("same-room", 1001, h.Tables, out _)!;
                DungeonBattle two = DungeonBattle.FromDungeon("same-room", 1001, h.Tables, out _)!;

                // 同样的怪、同样的伤害、同样的顺序
                for (int i = 0; i < one.Entities.Count; i++)
                {
                    one.ApplyDamage(one.Entities[0].Id, 999999, 0);
                    two.ApplyDamage(two.Entities[0].Id, 999999, 0);
                }

                var list1 = new List<DropEvent>();
                var list2 = new List<DropEvent>();
                one.CopyPendingDrops(list1);
                two.CopyPendingDrops(list2);

                // 顺带验一下 PRNG 本身：同种子 → 同序列
                var r1 = new BattleRandom(20260923);
                var r2 = new BattleRandom(20260923);
                bool sameRng = true;

                for (int i = 0; i < 16; i++)
                {
                    if (r1.NextPerTenThousand() != r2.NextPerTenThousand())
                    {
                        sameRng = false;
                    }
                }

                Check("掉落：同一房号 + 同样击序列 → **同样的掉落**（可对账）",
                    SameDrops(list1, list2) && list1.Count > 0 && sameRng,
                    $"第一局 {list1.Count} 条，第二局 {list2.Count} 条，PRNG 同序列={sameRng}");
            }
        }

        /// <summary>从表里找某怪 + 某物品的掉落行（找不到返回 default）。</summary>
        private static DropRow FindDropRow(ServerTables tables, int monsterId, int itemId)
        {
            IReadOnlyList<DropRow> rows = tables.FindDrops(monsterId);

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].ItemId == itemId)
                {
                    return rows[i];
                }
            }

            return default;
        }

        /// <summary>两组掉落是否逐字段相同。</summary>
        private static bool SameDrops(List<DropEvent> a, List<DropEvent> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].ItemId != b[i].ItemId || a[i].Count != b[i].Count
                    || a[i].WinnerPlayerId != b[i].WinnerPlayerId)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>把掉落串成人话（失败信息用）。</summary>
        private static string DescribeDrops(List<DropEvent> drops)
        {
            var text = new System.Text.StringBuilder();

            for (int i = 0; i < drops.Count; i++)
            {
                if (i > 0)
                {
                    text.Append('、');
                }

                text.Append("物品").Append(drops[i].ItemId).Append('×').Append(drops[i].Count)
                    .Append("→玩家").Append(drops[i].WinnerPlayerId);
            }

            return text.Length == 0 ? "（没有掉落）" : text.ToString();
        }

        // ====================================================================
        //  十一、网络模拟器（S8 / NET-07）
        // ====================================================================

        /// <summary>
        /// 注入 **200ms 延迟**之后，握手 / 心跳 / 进房 / 快照**仍然全部工作**（只是变慢）。
        /// <para>
        /// ⚠️ M3 走 TCP，所以这里**只注入延迟/抖动，不注入丢包** ——
        /// 应用层丢一条 = 这条消息永远不到（连握手都会失败），那是 M4 帧同步才该验的。
        /// 见 `SimulatedTransport` 文件头第三节。
        /// </para>
        /// </summary>
        private static void Sim_LatencyStillWorks()
        {
            using (var h = new Harness())
            {
                var sim = new SimulatedTransport(new TcpTransport())
                {
                    Profile = new NetSimProfile { LatencyMs = 200, JitterMs = 0 },
                };

                var session = new NetSession(sim, "延迟测试", "net-probe/sim");
                session.Connect("127.0.0.1", h.Server.Port);

                var watch = System.Diagnostics.Stopwatch.StartNew();

                // 真时间驱动：**先 Advance 再 Pump**（顺序见 SimulatedTransport 文件头第二节）
                bool PumpUntil(Func<bool> condition, int timeoutMs)
                {
                    DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);

                    while (DateTime.UtcNow < until)
                    {
                        int delta = (int)watch.ElapsedMilliseconds;
                        watch.Restart();
                        sim.Advance(delta);
                        session.Pump(delta);
                        h.Pump.Pump();

                        if (condition())
                        {
                            return true;
                        }

                        Thread.Sleep(1);
                    }

                    return condition();
                }

                bool online = PumpUntil(() => session.IsOnline, 8000);

                Check("网络模拟器：注入 200ms 延迟后仍能完成握手", online, "当前：" + session.Description);

                if (!online)
                {
                    session.Dispose();
                    return;
                }

                // 等一次心跳回来（握手后会自动发一次）
                PumpUntil(() => session.RttMs >= 0, 4000);

                Check("网络模拟器：测出来的 RTT 反映注入的延迟（单程 200ms → RTT ≥ 200ms）",
                    session.RttMs >= 200,
                    $"RTT={session.RttMs}ms（注入的是单程 200ms）");

                // 进房 + 收快照（证明业务消息也照样跑）
                session.JoinRoom(string.Empty, 1001);
                bool inRoom = PumpUntil(() => session.InRoom, 8000);

                // 服务端要推 tick 才会有快照
                for (int i = 0; i < 20; i++)
                {
                    h.Battles!.Tick();
                }

                bool hasWorld = PumpUntil(() => session.World.HasWorld, 8000);

                Check("网络模拟器：延迟下面进房与快照照样工作（不是「连上就完事」）",
                    inRoom && hasWorld && session.CurrentRoom != null && session.CurrentRoom.Members.Count >= 1,
                    $"进房={inRoom}，收到世界={hasWorld}，房号={(session.CurrentRoom == null ? "无" : session.CurrentRoom.RoomId)}");

                sim.Dispose();
            }
        }

        // ====================================================================
        //  十二、端到端验收（S9）
        // ====================================================================

        /// <summary>
        /// **用输入打死一只怪**（走完整通路：客户端意图 → 服务端判定 → 快照 + 掉落事件）。
        /// <para>
        /// ⚠️ 目标选**普通怪**（300 血 / 100 伤害 = 3 下）而不是 BOSS（2000 血 = 20 下），
        /// 是为了让探针快（每 tick 一次真 pump）；BOSS 那半边在下一个用例里用服务端直接结算。
        /// </para>
        /// </summary>
        private static void EndToEnd_KillOneMonsterViaInput()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                Client b = h.JoinSameRoom(h.ConnectAndHandshake("法师"));
                h.TickBattles(1);

                DungeonBattle battle = h.BattleOf(a)!;
                int wolfId = FirstAliveWolfId(battle);
                int dropsA = a.DropCount();
                int dropsB = b.DropCount();

                bool inRange = h.MoveUntilInRange(a, wolfId, maxTicks: 60);
                long landedBefore = h.Battles.AttacksLanded;

                // **边追边打**：狼在巡逻，站着不动会出射程；冷却 15 帧，所以要打够 `血 ÷ 伤害` 下
                // （期望值**算出来**：300 血 ÷ 100 伤害 = 3 下，不是拍脑袋写的次数）
                for (int i = 0; i < 120 && battle.Find(wolfId)!.Alive; i++)
                {
                    BattleEntity hero = h.HeroOf(a)!;
                    BattleEntity wolf = battle.Find(wolfId)!;

                    if (!battle.InAttackRange(hero, wolf))
                    {
                        h.MoveFor(a, moveX: 1000, moveY: 0, ticks: 1);     // 追
                    }
                    else
                    {
                        a.SendInput(moveX: 0, moveY: 0, actionBits: 1u, targetEntityId: wolfId);
                        h.TickBattles(1);
                    }
                }

                long landed = h.Battles.AttacksLanded - landedBefore;
                int hitsNeeded = WolfHp(h) / (battle.BasicAttackDamage <= 0 ? 1 : battle.BasicAttackDamage);

                bool wolfDead = !battle.Find(wolfId)!.Alive;
                EntitySnapshot seenByA = FindEntity(a.LastSnapshot(), wolfId);
                EntitySnapshot seenByB = FindEntity(b.LastSnapshot(), wolfId);

                Check($"端到端：走到射程内、用输入把一只怪打死（该打 {hitsNeeded} 下 = 血 {WolfHp(h)} ÷ 伤害 {battle.BasicAttackDamage}）",
                    inRange && wolfDead && landed == hitsNeeded,
                    $"射程内={inRange}，打中 {landed} 下（期望 {hitsNeeded}），怪活着={!wolfDead}");

                Check("端到端：怪死了这件事**两个客户端都看到**（快照里 alive=false）",
                    seenByA != null && seenByB != null && !seenByA.Alive && !seenByB.Alive,
                    seenByA == null || seenByB == null
                        ? "有一边没收到快照"
                        : $"甲 alive={seenByA.Alive}，乙 alive={seenByB.Alive}");

                // ⚠️ **狼是概率掉落**（`DropTable` 里两条：50% 与 30%）→ "什么都没掉"有 35% 的概率。
                //    所以这里**不能**断言"必掉"（我第一版就是这么写的，红给我看）——
                //    能断言的契约是：**两边收到的掉落必须一模一样**（可能 0 条，但两边都得是 0 条）。
                //    "必掉"那条由 BOSS（概率 10000）覆盖，见下一个用例。
                List<DropEvent> wolfDropsA = a.DropsSince(dropsA);
                List<DropEvent> wolfDropsB = b.DropsSince(dropsB);

                Check("端到端：掉落事件两边**必须一致**（狼是概率掉落，可能 0 条 —— 但两边要一样）",
                    SameDrops(wolfDropsA, wolfDropsB) && a.DropCount() == b.DropCount(),
                    $"甲新收 {wolfDropsA.Count} 条（{DescribeDrops(wolfDropsA)}），" +
                    $"乙新收 {wolfDropsB.Count} 条（{DescribeDrops(wolfDropsB)}）");
            }
        }

        /// <summary>结束时两个客户端看到的世界**逐字段一致**（收尾对账）。</summary>
        private static void EndToEnd_BothClientsSeeTheSameEnding()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                Client b = h.JoinSameRoom(h.ConnectAndHandshake("法师"));
                h.TickBattles(2);

                DungeonBattle battle = h.BattleOf(a)!;

                // 服务端直接结算完剩下的怪（缩短探针耗时；权威仍在服务端）
                for (int i = 0; i < battle.Entities.Count; i++)
                {
                    if (battle.Entities[i].Kind == 1)
                    {
                        battle.ApplyDamage(battle.Entities[i].Id, 999999, h.HeroOf(a)!.Id);
                    }
                }

                h.TickBattles(2);

                Check("端到端：全灭之后两个客户端看到的快照**逐字段一致**",
                    SameWorld(a.LastSnapshot(), b.LastSnapshot())
                    && CountKind(a.LastSnapshot(), 1) == MonstersInDungeon(h, 1001)
                    && a.LastSnapshot().Entities.Count > 0,
                    "甲的怪数=" + CountKind(a.LastSnapshot(), 1) + "，乙的怪数=" + CountKind(b.LastSnapshot(), 1));
            }
        }

        /// <summary>副本结束：怪全清 → `IsFinished`；BOSS 的掉落也到了两边。</summary>
        private static void EndToEnd_FinishTheRun()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                Client b = h.JoinSameRoom(h.ConnectAndHandshake("法师"));
                h.TickBattles(2);

                DungeonBattle battle = h.BattleOf(a)!;
                int dropsA = a.DropCount();
                int dropsB = b.DropCount();

                for (int i = 0; i < battle.Entities.Count; i++)
                {
                    if (battle.Entities[i].Kind == 1)
                    {
                        battle.ApplyDamage(battle.Entities[i].Id, 999999, h.HeroOf(a)!.Id);
                    }
                }

                h.TickBattles(2);

                Check("端到端：怪全死 → 本局结束（`IsFinished`）",
                    battle.IsFinished && battle.AliveMonsterCount == 0,
                    $"结束={battle.IsFinished}，活怪={battle.AliveMonsterCount}");

                Check("端到端：BOSS 掉落（必掉）两边都收到、内容一致 → **M3 的验收达成**",
                    a.DropCount() > dropsA && b.DropCount() > dropsB
                    && SameDrops(a.DropsSince(dropsA), b.DropsSince(dropsB)),
                    "甲的掉落：" + DescribeDrops(a.DropsSince(dropsA)) +
                    "；乙的：" + DescribeDrops(b.DropsSince(dropsB)));
            }
        }

        // ====================================================================
        //  【十三】M4-S1：伤害 / 死亡事件下发
        // ====================================================================

        /// <summary>
        /// 每一次扣血都下发一条 `DamageEvent`，且**两个客户端收到的是同一份**。
        /// <para>⚠️ 断言里用的是"打出去多少就扣了多少"（这一发是全额命中），不是随手写的数。</para>
        /// </summary>
        private static void Events_EveryHitIsBroadcastToBothClients()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                Client b = h.JoinSameRoom(h.ConnectAndHandshake("法师"));
                h.TickBattles(2);

                DungeonBattle battle = h.BattleOf(a)!;
                BattleEntity hero = h.HeroOf(a)!;

                // 打 BOSS 一发（不是致死），三个不同 tick 各一发 → 三条伤害事件
                int beforeA = a.HitCount();
                int beforeB = b.HitCount();
                const int damage = 10;

                BattleEntity boss = FindBoss(battle)!;
                int hpBefore = boss.Hp;

                battle.ApplyDamage(boss.Id, damage, hero.Id);
                battle.ApplyDamage(boss.Id, damage, hero.Id);
                battle.ApplyDamage(boss.Id, damage, hero.Id);
                h.TickBattles(1);

                List<DamageEvent> da = a.HitsSince(beforeA);
                List<DamageEvent> db = b.HitsSince(beforeB);

                bool same = da.Count == db.Count && da.Count == 3;

                for (int i = 0; same && i < da.Count; i++)
                {
                    same = da[i].TargetId == boss.Id
                        && da[i].AttackerId == hero.Id
                        && da[i].Applied == damage
                        && da[i].TargetId == db[i].TargetId
                        && da[i].Applied == db[i].Applied
                        && da[i].RemainingHp == db[i].RemainingHp;
                }

                Check("M4-S1：每次扣血都下发一条伤害事件，且**两端逐字段一致**",
                    same,
                    $"甲 {da.Count} 条、乙 {db.Count} 条（期望各 3 条）；甲=" + DescribeHits(da));

                Check("M4-S1：`applied` 是**实际扣掉的血**（这一发全额命中，所以等于打出去的伤害）",
                    da.Count == 3 && da[0].RemainingHp == hpBefore - damage,
                    $"打之前 {hpBefore}，打 {damage} ×3 → 第一条 remaining_hp 期望 {hpBefore - damage}，实际 " +
                    (da.Count == 0 ? "（没有事件）" : da[0].RemainingHp.ToString()));
            }
        }

        /// <summary>死亡事件带 `kind` 与 `config_id`（客户端靠它分派成 MonsterDied / HeroDied）。</summary>
        private static void Events_DeathCarriesKindAndConfigId()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                Client b = h.JoinSameRoom(h.ConnectAndHandshake("法师"));
                h.TickBattles(2);

                DungeonBattle battle = h.BattleOf(a)!;
                BattleEntity hero = h.HeroOf(a)!;
                BattleEntity boss = FindBoss(battle)!;

                int beforeA = a.DeathCount();
                int beforeB = b.DeathCount();

                battle.ApplyDamage(boss.Id, 999999, hero.Id);
                h.TickBattles(1);

                List<DeathEvent> da = a.DeathsSince(beforeA);
                List<DeathEvent> db = b.DeathsSince(beforeB);

                bool ok = da.Count == 1 && db.Count == 1
                    && da[0].EntityId == boss.Id
                    && da[0].ConfigId == boss.ConfigId
                    && da[0].Kind == 1
                    && da[0].KillerId == hero.Id
                    && db[0].ConfigId == da[0].ConfigId;

                Check("M4-S1：死亡事件带 kind=1（怪）/ config_id / killer_id，两端一致",
                    ok,
                    da.Count == 0
                        ? "没收到死亡事件"
                        : $"甲：实体 {da[0].EntityId}、配置 {da[0].ConfigId}、kind {da[0].Kind}、击杀者 {da[0].KillerId}；" +
                          $"乙：配置 {(db.Count == 0 ? -1 : db[0].ConfigId)}");
            }
        }

        /// <summary>
        /// 同一帧里**先伤害、后死亡**（客户端按这个顺序发游戏事件：
        /// `DamageDealt` → `MonsterDied`，与 M2 本地战斗的因果顺序一致）。
        /// </summary>
        private static void Events_DamageComesBeforeDeathInTheSameTick()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(2);

                DungeonBattle battle = h.BattleOf(a)!;
                BattleEntity hero = h.HeroOf(a)!;
                BattleEntity boss = FindBoss(battle)!;

                int before = a.EventOrderCount();
                battle.ApplyDamage(boss.Id, 999999, hero.Id);
                h.TickBattles(1);

                List<string> order = a.EventKindsSince(before);

                Check("M4-S1：同一帧里**伤害在前、死亡在后**（因果顺序）",
                    order.Count >= 2 && order[0] == "damage" && order[1] == "death",
                    "这一帧收到的事件顺序：" + string.Join(" → ", order));
            }
        }

        /// <summary>服务端的计数如实（每次扣血一条伤害、死亡一条）。</summary>
        private static void Events_CountersMatchWhatWasSent()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                h.TickBattles(2);

                DungeonBattle battle = h.BattleOf(a)!;
                BattleEntity hero = h.HeroOf(a)!;

                long hitsBefore = h.Battles.HitsSent;
                long deathsBefore = h.Battles.DeathsSent;

                // 两只普通怪：一只打半血、一只打死（打死那只还会有掉落，但这里只看事件）
                BattleEntity first = FindMonster(battle, skip: 0)!;
                BattleEntity second = FindMonster(battle, skip: 1)!;

                battle.ApplyDamage(first.Id, 1, hero.Id);              // 不死
                battle.ApplyDamage(second.Id, 999999, hero.Id);        // 死
                h.TickBattles(1);

                long hits = h.Battles.HitsSent - hitsBefore;
                long deaths = h.Battles.DeathsSent - deathsBefore;

                Check("M4-S1：服务端计数如实（伤害 = 扣血次数，死亡 = 死的单位数）",
                    hits == 2 && deaths == 1,
                    $"这一帧伤害 {hits} 条（期望 2）、死亡 {deaths} 条（期望 1）");
            }
        }

        /// <summary>
        /// M4-S1b：快照里**英雄带 `owner_player_id`、怪是 0** —— 客户端靠它认出"哪个英雄是我"。
        /// <para>没有它的话，两个客户端时只能靠 `kind == 0` 猜；猜错的表现是
        /// "调试工具在操纵别人的角色"，**静默且看着很正常**（和 M2 那两个事件的取舍同源）。</para>
        /// </summary>
        private static void Snapshots_CarryOwnerPlayerId()
        {
            using (var h = new Harness())
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("剑士"));
                Client b = h.JoinSameRoom(h.ConnectAndHandshake("法师"));
                h.TickBattles(2);

                BattleEntity heroA = h.HeroOf(a)!;
                BattleEntity heroB = h.HeroOf(b)!;

                WorldSnapshot sa = a.LastSnapshot();
                WorldSnapshot sb = b.LastSnapshot();

                // ⚠️ 不能取"快照里第一个英雄的 owner" —— 快照里**两个英雄都在**，
                //    第一个永远是先入职的那个（我第一版就这么误判成"乙看到的主人是 1"）。
                //    正确的判据是：**快照里存在属于我的那个英雄**。
                List<long> ownersSeenByA = HeroOwners(sa);
                List<long> ownersSeenByB = HeroOwners(sb);

                int monstersWithOwner = 0;

                for (int i = 0; sa != null && i < sa.Entities.Count; i++)
                {
                    if (sa.Entities[i].Kind == 1 && sa.Entities[i].OwnerPlayerId != 0)
                    {
                        monstersWithOwner++;
                    }
                }

                Check("M4-S1b：快照里英雄带 `owner_player_id`（怪是 0）—— 客户端能认出自己",
                    ownersSeenByA.Contains(heroA.PlayerId)
                    && ownersSeenByA.Contains(heroB.PlayerId)
                    && ownersSeenByB.Contains(heroA.PlayerId)
                    && ownersSeenByB.Contains(heroB.PlayerId)
                    && heroA.PlayerId != heroB.PlayerId
                    && monstersWithOwner == 0,
                    $"甲的快照里英雄主人 = [" + string.Join(",", ownersSeenByA) + "]（服务端：甲 " + heroA.PlayerId +
                    "、乙 " + heroB.PlayerId + "）；乙的 = [" + string.Join(",", ownersSeenByB) +
                    "]；带主人的怪 " + monstersWithOwner + " 个");
            }
        }

        // ====================================================================
        //  【十五】M4-S3：账号登录
        // ====================================================================

        /// <summary>
        /// 同一个账号连两次 ⇒ **同一个 `player_id`**。
        ///
        /// <para>
        /// 这是整件事的**核心断言**：SRV-06 之前，`player_id` 是"第几个连上来的"
        /// （断开再连就可能换人），战绩会记到别人头上。
        /// </para>
        /// </summary>
        private static void Login_SameAccountGetsSamePlayerId()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                Client first = h.ConnectAndLogin("alice", "pw-alice");
                HandshakeAck firstAck = AckOf(first, "alice 首次登录");
                long idFirst = firstAck.PlayerId;

                Check("M4-S3：登录成功（不是被拒）且拿到正数编号",
                    firstAck.Accepted && idFirst > 0,
                    $"accepted={firstAck.Accepted}、player_id={idFirst}、reason=\"{firstAck.Reason}\"");

                Check("M4-S3：登录玩家的昵称来自**账号档案**（不是客户端发来的 player_name）",
                    firstAck.Nickname == "爱丽丝",
                    $"nickname=\"{firstAck.Nickname}\"（期望 爱丽丝；客户端发的是「不该用到这个名字」）");

                // 断开再连（重连）：走**真 socket 的关闭**，让服务端把这个人当成"走了"
                first.Transport.Close();
                h.PumpFor(150);

                Client again = h.ConnectAndLogin("alice", "pw-alice");
                HandshakeAck againAck = AckOf(again, "alice 重连");

                Check("M4-S3：**同一个账号重连还是同一个 player_id**（这才是账号的意义）",
                    againAck.Accepted && againAck.PlayerId == idFirst,
                    $"第一次 {idFirst}，第二次 {againAck.PlayerId}");

                // 换一个账号必须是**另一个人**
                Client bob = h.ConnectAndLogin("bob", "pw-bob");
                HandshakeAck bobAck = AckOf(bob, "bob 登录");

                Check("M4-S3：不同账号拿到**不同**的 player_id",
                    bobAck.Accepted && bobAck.PlayerId != idFirst,
                    $"alice={idFirst}、bob={bobAck.PlayerId}");

                // 而且**同一账号可以同时在线**（身份属于账号，不属于连接）
                Client aliceAgain = h.ConnectAndLogin("alice", "pw-alice");
                HandshakeAck aliceAgainAck = AckOf(aliceAgain, "alice 第二条连接");

                Check("M4-S3：同一账号可以同时开两条连接（身份属于账号，不属于连接）",
                    aliceAgainAck.Accepted && aliceAgainAck.PlayerId == idFirst,
                    $"第三条连接 player_id={aliceAgainAck.PlayerId}（alice={idFirst}）");
            }
        }

        /// <summary>密码不对 ⇒ 被拒，而且是**说人话**的拒。</summary>
        private static void Login_WrongPasswordIsRejectedWithHumanReason()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                Client c = h.ConnectAndLogin("alice", "不是这个密码");
                HandshakeAck ack = AckOf(c, "密码不对");
                string reason = ack.Reason;

                Check("M4-S3：密码不对 ⇒ 被拒（且 `player_id` 是 0，不是「随便给一个」）",
                    !ack.Accepted && ack.PlayerId == 0,
                    $"accepted={ack.Accepted}、player_id={ack.PlayerId}");

                Check("M4-S3：密码不对时说的是「密码不对」（不是「没有这个账号」）",
                    reason.Contains("密码"),
                    $"reason=\"{reason}\"");

                Check("M4-S3：被拒的答复里**不含**摘要/密码（敏感信息不上错误消息）",
                    !reason.Contains(PasswordDigest.FromPassword("不是这个密码")) &&
                    !reason.Contains(PasswordDigest.FromPassword("pw-alice")),
                    $"reason=\"{reason}\"");
            }
        }

        /// <summary>没有这个账号 ⇒ 被拒，而且和「密码不对」**分得开**（本项目的取舍）。</summary>
        private static void Login_UnknownAccountIsRejectedWithHumanReason()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                Client c = h.ConnectAndLogin("查无此人", "随便");
                HandshakeAck ack = AckOf(c, "账号不存在");

                Check("M4-S3：没有这个账号 ⇒ 被拒，且说清是「没有这个账号」",
                    !ack.Accepted && ack.Reason.Contains("没有这个账号"),
                    $"accepted={ack.Accepted}、reason=\"{ack.Reason}\"");
            }
        }

        /// <summary>
        /// 游客拿的是**负数编号**（SRV-17a，2026-09-27 起）—— 这条是"游客不污染账号"的守门人。
        ///
        /// <para>
        /// ⚠️ 为什么这条这么重要：旧写法给游客发 **1、2、3…**，而那恰好是
        /// `player_profile` 里**真实账号的档案 id** ⇒ 游客的战绩被记进了**别人的**累计里
        /// （本项目真的发生过：库里那几条战绩记录全是游客打出来的，却记在 test01/test02 名下）。
        /// </para>
        /// </summary>
        private static void Login_GuestStillWorksOldWay()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                Client a = h.ConnectAndHandshake("游客甲");
                Client b = h.ConnectAndHandshake("游客乙");
                HandshakeAck ackA = AckOf(a, "游客甲");
                HandshakeAck ackB = AckOf(b, "游客乙");

                Check("M4-S3：游客的编号是**负数**（= 不会和任何账号档案撞号）",
                    ackA.PlayerId < 0 && ackB.PlayerId < 0,
                    $"甲={ackA.PlayerId}、乙={ackB.PlayerId}");

                Check("M4-S3：两个游客**不会同号**（从 -1 往下发，一人一个）",
                    ackA.PlayerId != ackB.PlayerId,
                    $"甲={ackA.PlayerId}、乙={ackB.PlayerId}");

                Check("M4-S3：游客的昵称就是发来的 player_name（没有档案可查，也就不去查）",
                    ackA.Nickname == "游客甲" && ackB.Nickname == "游客乙",
                    $"甲=\"{ackA.Nickname}\"、乙=\"{ackB.Nickname}\"");

                // ⚠️ 反向对照：**登录玩家的编号必须是正数**。
                //    两条合起来才是「正数 ⟺ 真实账号」这条约定的完整守门人 ——
                //    只验"游客是负数"的话，有人把登录也改成负数这条不会红。
                Client logged = h.ConnectAndLogin("alice", "pw-alice");
                HandshakeAck loggedAck = AckOf(logged, "登录玩家");

                Check("M4-S3：**登录玩家的编号是正数**（正数 ⟺ 真实账号档案）",
                    loggedAck.Accepted && loggedAck.PlayerId > 0,
                    $"player_id={loggedAck.PlayerId}");
            }
        }

        /// <summary>
        /// **草稿里要有游客**（负数 id）—— 这是"游客不落库"那条链的**前半段**。
        ///
        /// <para>
        /// ⚠️ 为什么必须单独验这一段：过滤掉落不了库的东西是**数据层**的活（`BattleRecordDao`），
        /// 而**战斗层必须如实记账**。如果战斗层偷偷把游客丢掉（比如判据写成 `PlayerId > 0`），
        /// 结果看起来"也对"（库里反正没有游客），但你会**永远发现不了**：
        /// ① 数据层的"游客 N 人"那条日志再也不会出现；
        /// ② 一旦以后要支持"游客也能进排行榜"，账已经丢了。
        /// ⇒ **分工要能被观察到**，而不是"反正结果一样"。
        /// </para>
        /// </summary>
        private static void Login_GuestIdReachesTheBattleDraft()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                Client a = h.JoinSameRoom(h.ConnectAndHandshake("游客甲"));
                Client b = h.JoinSameRoom(h.ConnectAndHandshake("游客乙"));
                h.TickBattles(2);

                DungeonBattle battle = h.BattleOf(a)!;
                BattleEntity heroA = h.HeroOf(a)!;

                int monsters = 0;

                for (int i = 0; i < battle.Entities.Count; i++)
                {
                    if (battle.Entities[i].Kind == 1)
                    {
                        battle.ApplyDamage(battle.Entities[i].Id, 999999, heroA.Id);
                        monsters++;
                    }
                }

                h.TickBattles(2);

                Check("草稿：这一局真的交上来了（`BattleFinished`）",
                    h.Drafts.Count == 1, "收到 " + h.Drafts.Count + " 份草稿");

                if (h.Drafts.Count == 0)
                {
                    return;
                }

                BattleRecordDraft draft = h.Drafts[0];

                Check("草稿：**两个游客都在草稿里，而且 id 是负数**（过滤是数据层的活，战斗层不许偷偷丢）",
                    draft.Players.Count == 2 && draft.Players[0].PlayerId < 0 && draft.Players[1].PlayerId < 0,
                    "草稿里 " + draft.Players.Count + " 个玩家：" +
                    string.Join(",", System.Linq.Enumerable.Select(draft.Players, p => p.PlayerId)));

                // ⚠️ 这条盯的是 `DungeonBattle` 里那几个 `PlayerId != 0` 的判据：
                //    写成 `> 0` 的话游客**一个击杀都不记**，草稿里是 0。
                BattlePlayerDraft killer = draft.Players[0].PlayerId == heroA.PlayerId
                    ? draft.Players[0]
                    : draft.Players[1];

                Check("草稿：游客打死的怪**如实算在游客头上**（"+ monsters + " 个）",
                    killer.Kill == monsters,
                    "草稿里记了 " + killer.Kill + " 个击杀");

                Check("草稿：这一局的胜负也对（怪全死 ⇒ win）",
                    draft.IsWin, "IsWin=" + draft.IsWin);
            }
        }

        // ====================================================================
        //  【十六】M4-S3 服务端权威：成就判定
        // ====================================================================

        /// <summary>
        /// **登录玩家**打一局 → 服务端**自己**判定并解锁（不再依赖客户端）。
        ///
        /// <para>
        /// 判据挑的是"**能一眼看出是服务端做的**"那几条：
        /// ① 台账里出现了 `(成就 9002, 奖励 5003)` 这一行 —— 只有服务端会写台账；
        /// ② 进度表里 `条件 4010`（击杀狼王 1 只）= 1；
        /// ③ `AchievementAuthority.Describe()` 说解锁 1 个。
        /// </para>
        /// </summary>
        private static void Authority_LoggedInPlayerUnlocksFromItsOwnBattle()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                QuestTables tables = LoadQuestTables();
                var store = new FakeProgressStore();
                var ledger = new FakeRewardLedger();

                using (var authority = new AchievementAuthority(tables, _ => store, _ => ledger))
                {
                    h.Battles.Achievements = authority;
                    authority.Note += line => h.AuthorityNotes.Add(line);

                    // `FakeAccounts` 里 alice = 玩家 11（> 0 = 真实账号档案）
                    Client a = h.ConnectAndLogin("alice", "pw-alice");
                    long aliceId = AckOf(a, "alice 登录").PlayerId;

                    Check("权威：登录玩家被追踪（游客不会被追踪）",
                        authority.Track(aliceId, "爱丽丝") && authority.TrackedPlayers == 1,
                        "TrackedPlayers=" + authority.TrackedPlayers);

                    h.JoinSameRoom(a);
                    h.TickBattles(2);

                    DungeonBattle battle = h.BattleOf(a)!;
                    BattleEntity hero = h.HeroOf(a)!;

                    // 打死狼王（`Dungeon 1001` 的 bossId = 6003）—— 成就 9002 要的就是它
                    BattleEntity boss = FindBoss(battle)!;
                    int bossConfigId = boss.ConfigId;

                    battle.ApplyDamage(boss.Id, 999999, hero.Id);
                    h.TickBattles(2);

                    Check("权威：**服务端自己**从战斗里判定出解锁（台账里有一行成就 9002）",
                        ledger.HasGranted(NBC.Shared.Reward.ERewardOwnerKind.Achievement, 9002),
                        "台账内容：" + ledger.Describe() + "；服务端日志：" + DescribeLines(h.AuthorityNotes));

                    Check("权威：进度也落在**服务端**的存放处（条件 4010 = 击杀狼王）",
                        store.GetProgress(4010) == 1,
                        "条件 4010 的进度 = " + store.GetProgress(4010));

                    Check("权威：解锁计数如实（1 个）",
                        authority.Describe().Contains("解锁 1 个"), authority.Describe());

                    Check("权威：狼王的配置号就是表里的 6003（判据不是我随手写的数）",
                        bossConfigId == 6003, "配置号 = " + bossConfigId);

                    // ================================================================
                    //  M4-S3 收口（`Docs\27` §21.4 未做#2）：权威进度**下发给客户端**
                    //  ⚠️ 用的是 Host 那份**同一个** `ProgressBroadcaster` ——
                    //     在探针里另抄一份"建消息 → 找会话 → 发"，验的就是抄本
                    //     （M3-A 的 `ServerMessagePump` / §12.3 3h 同一条教训）。
                    // ================================================================
                    using (var push = new ProgressBroadcaster(h.Server, h.Registry, authority))
                    {
                        // 再打死一只**活着的**怪，让进度**变化**一次（推送是在"进度变了"时触发的）。
                        // ⚠️ 判据必须是 `Kind == 1 && Alive`：服务端的 `DungeonBattle` **保留尸体**
                        //    （Hp 归零但仍在 `Entities` 里），所以"找到 boss 就打"会打在尸体上 ——
                        //    那样**不产生击杀事实**，也就没有进度变化。第一版就是这么写的，探针当场红了。
                        for (int i = 0; i < battle.Entities.Count; i++)
                        {
                            if (battle.Entities[i].Kind == 1 && battle.Entities[i].Alive)
                            {
                                battle.ApplyDamage(battle.Entities[i].Id, 999999, hero.Id);
                                break;
                            }
                        }

                        h.TickBattles(2);

                        // ⚠️ 这里**不写** `ServerMessage?`：本文件没有 `#nullable` 上下文，
                        //    那个 `?` 会报 CS8632（纯噪音）。引用类型赋 null 在本文件是合法的。
                        ServerMessage sync = null;

                        for (int i = 0; i < a.Received.Count; i++)
                        {
                            if (a.Received[i].PayloadCase == ServerMessage.PayloadOneofCase.ProgressSync)
                            {
                                sync = a.Received[i];
                            }
                        }

                        ConditionProgressEntry entry4010 = null;

                        if (sync != null)
                        {
                            for (int i = 0; i < sync.ProgressSync.Entries.Count; i++)
                            {
                                if (sync.ProgressSync.Entries[i].ConditionKey == 4010)
                                {
                                    entry4010 = sync.ProgressSync.Entries[i];
                                }
                            }
                        }

                        Check("推送⭐：客户端**真的收到**了权威进度（`progress_sync` 那一条）",
                            sync != null,
                            "收到 " + a.Received.Count + " 条消息，没有一条是 progress_sync；" +
                            "服务端说明：" + DescribeLines(h.AuthorityNotes));

                        Check("推送⭐：里面的条件 4010 是**服务端权威值**（1/1、已达成）",
                            entry4010 != null && entry4010.Current == 1 && entry4010.Required == 1 && entry4010.Met,
                            entry4010 == null
                                ? "这条进度里没有条件 4010"
                                : entry4010.Current + "/" + entry4010.Required + "，met=" + entry4010.Met);

                        Check("推送：推的是**全量**（这个玩家关心的条件都在里面，不止 4010）",
                            sync != null && sync.ProgressSync.Entries.Count >= 2,
                            "只有 " + (sync == null ? 0 : sync.ProgressSync.Entries.Count) + " 条");

                        Check("推送：发出去的份数如实（`Sent` 与收到的一致）",
                            push.Sent >= 1, "Sent=" + push.Sent + "；" + push.Describe());
                    }

                    // ================================================================
                    //  M4-S3 收口（`Docs\27` §二十四）：进度**是谁说的**
                    //  ⚠️ 这一组盯的是本片**最容易回归**的地方：`TryGetCondition` 返回 false
                    //     意思是「**服务端没说**」，**不是**「进度是 0」。
                    //     把它当成 0 ⇒ 界面会画出「0/3」，玩家以为进度被清零了。
                    // ================================================================
                    var line = new QuestConditionLine { ConditionId = 4001, Current = 2, Required = 3 };

                    int applied = QuestProgressOverlay.Apply(
                        new List<QuestConditionLine> { line }, new SilentAuthority());

                    Check("来源⭐：服务端**没说**这条 ⇒ 保留本地那个数（**不是 0**）",
                        line.Current == 2 && line.Required == 3,
                        "被改成了 " + line.Current + "/" + line.Required + "（2/3 才是对的）");

                    Check("来源⭐：而且标成**本地预测**（界面要一眼看得出）",
                        line.Source == EProgressSource.LocalPrediction &&
                        QuestProgressOverlay.Mark(line.Source) == QuestProgressOverlay.LocalMark,
                        "Source=" + line.Source + "；标注=" + QuestProgressOverlay.Mark(line.Source));

                    Check("来源：一条都没拿到权威值 ⇒ 返回 0（调用方据此知道这批全是预测）",
                        applied == 0, "applied=" + applied);

                    QuestProgressOverlay.Apply(
                        new List<QuestConditionLine> { line }, new SpeakingAuthority(4001, 3, 3, true));

                    Check("来源：服务端**说了** ⇒ 以它为准，而且**不标**预测",
                        line.Current == 3 && line.IsMet &&
                        line.Source == EProgressSource.ServerAuthoritative &&
                        QuestProgressOverlay.Mark(line.Source).Length == 0,
                        "current=" + line.Current + "；met=" + line.IsMet + "；Source=" + line.Source);

                    var untouched = new QuestConditionLine { ConditionId = 4002, Current = 5, Required = 9 };

                    QuestProgressOverlay.Apply(new List<QuestConditionLine> { untouched }, null);

                    Check("来源：没接权威（null）⇒ 与「权威没说话」同一条路：保持预测且不改数",
                        untouched.Current == 5 && untouched.Source == EProgressSource.LocalPrediction,
                        "current=" + untouched.Current + "；Source=" + untouched.Source);
                }
            }
        }

        /// <summary>
        /// **游客**的事实**一条都不记**（设计如此）—— 而且这件事要能看见（不是静默）。
        /// </summary>
        private static void Authority_GuestFactsAreNotRecorded()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                QuestTables tables = LoadQuestTables();
                var store = new FakeProgressStore();
                var ledger = new FakeRewardLedger();

                using (var authority = new AchievementAuthority(tables, _ => store, _ => ledger))
                {
                    h.Battles.Achievements = authority;
                    authority.Note += line => h.AuthorityNotes.Add(line);

                    Client a = h.JoinSameRoom(h.ConnectAndHandshake("游客甲"));
                    h.TickBattles(2);

                    DungeonBattle battle = h.BattleOf(a)!;
                    BattleEntity hero = h.HeroOf(a)!;

                    for (int i = 0; i < battle.Entities.Count; i++)
                    {
                        if (battle.Entities[i].Kind == 1)
                        {
                            battle.ApplyDamage(battle.Entities[i].Id, 999999, hero.Id);
                        }
                    }

                    h.TickBattles(2);

                    Check("权威：游客（负数 id）的事实**一条都没记进进度与台账**",
                        store.Count == 0 && ledger.Count == 0,
                        "进度 " + store.Count + " 条、台账 " + ledger.Count + " 条");

                    Check("权威：游客被跳过这件事**说得出来**（不是静默丢掉）",
                        authority.Describe().Contains("游客跳过") &&
                        DescribeLines(h.AuthorityNotes).Contains("游客"),
                        authority.Describe());
                }
            }
        }

        /// <summary>
        /// 服务端**没接数据库**时，权威必须**明确不工作**，而不是"只判不记"。
        /// </summary>
        private static void Authority_NoDatabaseSaysWhyInsteadOfPretending()
        {
            QuestTables tables = LoadQuestTables();
            var notes = new List<string>();

            using (var authority = new AchievementAuthority(tables, null, null))
            {
                authority.Note += line => notes.Add(line);

                bool tracked = authority.Track(11, "爱丽丝");

                Check("权威：没接数据库 ⇒ **不追踪**（拒绝「只判不记」）",
                    !tracked && authority.TrackedPlayers == 0,
                    "tracked=" + tracked + "、TrackedPlayers=" + authority.TrackedPlayers);

                Check("权威：而且要说清「为什么」，不是安静地什么都不做",
                    DescribeLines(notes).Contains("没接数据库"), DescribeLines(notes));
            }
        }

        /// <summary>读真表（`Configs\Design`）—— 判据用的是**真源**，不是探针自己编的表。</summary>
        /// <returns>四张表。</returns>
        /// <summary>
        /// ⚠️ **共用条件必须在加载时被挡住**（阴性/阳性对照，§二十五）。
        ///
        /// <para>为什么值得一条对照：条件进度按「玩家 + 条件编号」存在**同一张表**里，
        /// 而任务权威与成就权威**各自**为同一玩家建一份写回缓存 ⇒ 一个条件被两边共用
        /// 就是 **丢更新**（各写各的绝对值，进度少涨，**而且不报错**）。</para>
        ///
        /// <para>📌 **阳性对照不可省**：它证明"真的读到了东西"。
        /// 本项目今天已经出现两次"没读到被判成没问题"的假绿，所以先证明读到了，再看阴性。</para>
        ///
        /// <para>⚠️ **不改真源**：把 4 张 CSV 拷到临时目录，只动那一份。</para>
        /// </summary>
        private static void Quest_SharedConditionIsRejected_ButCleanTablesLoad()
        {
            string sourceDir;

            if (!ServerTables.TryResolveConfigDir(out sourceDir))
            {
                Check("§25 加载校验：找得到配置表目录（否则下面全是假绿）", false, "找不到配置表目录");
                return;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "nbc_shared_cond_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string[] files = { "Quest.csv", "QuestCondition.csv", "Achievement.csv", "Reward.csv" };

                for (int i = 0; i < files.Length; i++)
                {
                    File.Copy(Path.Combine(sourceDir, files[i]), Path.Combine(tempDir, files[i]), true);
                }

                // ---- 阳性：原样 ⇒ 必须加载成功，而且**真的读到了表** ----
                QuestTables clean;
                string cleanError;

                bool cleanOk = QuestTables.TryLoad(tempDir, out clean, out cleanError);

                Check("§25 加载校验·阳性：不共用 ⇒ `TryLoad` **成功**", cleanOk, "居然失败：" + cleanError);

                Check("§25 加载校验·阳性：而且**真的读到了**任务/成就/条件（防假绿）",
                    cleanOk && clean.QuestCount > 0 && clean.AchievementCount > 0 && clean.ConditionCount > 0,
                    cleanOk
                        ? "任务 " + clean.QuestCount + "、成就 " + clean.AchievementCount +
                          "、条件 " + clean.ConditionCount
                        : "上面那条已经失败了");

                // ---- 阴性：让成就 9002 也引用任务 3003 的条件 4005 ⇒ 必须加载失败 ----
                string achievementPath = Path.Combine(tempDir, "Achievement.csv");
                string text = File.ReadAllText(achievementPath);

                const string before = "9002,狼王终结者,击杀狼王,4010,5003";
                const string after = "9002,狼王终结者,击杀狼王,\"4010,4005\",5003";

                if (!text.Contains(before))
                {
                    // ⚠️ 源表改了要让这条**红**，而不是静默跳过（静默跳过 = 用例假绿）
                    Check("§25 加载校验·阴性：找得到要改的那一行成就配置", false,
                        "Achievement.csv 里没有「" + before + "」—— 源表变了，请同步这条用例");
                    return;
                }

                File.WriteAllText(achievementPath, text.Replace(before, after));

                QuestTables dirty;
                string dirtyError;

                bool dirtyOk = QuestTables.TryLoad(tempDir, out dirty, out dirtyError);

                Check("§25 加载校验·阴性⭐：条件被任务+成就共用 ⇒ `TryLoad` **失败**",
                    !dirtyOk, "居然成功了 —— 共用条件会**丢更新**，必须在加载时就挡住");

                Check("§25 加载校验·阴性⭐：报错**点名**那个条件编号（4005）",
                    !dirtyOk && dirtyError.Contains("4005"),
                    "报错里没有 4005：" + dirtyError);

                Check("§25 加载校验·阴性：报错说清了**为什么**（丢更新 / 写回缓存）",
                    !dirtyOk && (dirtyError.Contains("丢更新") || dirtyError.Contains("缓存")),
                    "报错没说原因：" + dirtyError);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* 删不掉不影响结论 */ }
            }
        }

        /// <summary>假的任务状态存放处：**跨"重启"共用同一份 backing**（模拟库）。</summary>
        private sealed class FakeQuestStateStore : IQuestStateStore
        {
            /// <summary>模拟"库"（多个实例共用同一份 = 同一个玩家同一张表）。</summary>
            private readonly Dictionary<int, int> _backing;

            /// <summary>内存里的权威值。</summary>
            private readonly Dictionary<int, int> _memory = new Dictionary<int, int>();

            /// <summary>脏键。</summary>
            private readonly HashSet<int> _dirty = new HashSet<int>();

            /// <summary>造一个。</summary>
            /// <param name="backing">模拟库。</param>
            public FakeQuestStateStore(Dictionary<int, int> backing)
            {
                _backing = backing;
            }

            /// <summary>读盘次数（证明真的读了）。</summary>
            public int LoadCount { get; private set; }

            /// <inheritdoc/>
            public int DirtyCount { get { return _dirty.Count; } }

            /// <inheritdoc/>
            public EQuestStage GetState(int questId)
            {
                int v;
                return _memory.TryGetValue(questId, out v) ? (EQuestStage)v : EQuestStage.None;
            }

            /// <inheritdoc/>
            public void SetState(int questId, EQuestStage state)
            {
                _memory[questId] = (int)state;
                _dirty.Add(questId);
            }

            /// <inheritdoc/>
            public Task<int> LoadAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                LoadCount++;
                _memory.Clear();

                foreach (KeyValuePair<int, int> pair in _backing)
                {
                    _memory[pair.Key] = pair.Value;
                }

                return Task.FromResult(_backing.Count);
            }

            /// <inheritdoc/>
            public Task<int> FlushAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                int written = 0;

                foreach (int key in _dirty)
                {
                    _backing[key] = _memory[key];
                    written++;
                }

                _dirty.Clear();
                return Task.FromResult(written);
            }

            /// <inheritdoc/>
            public string DescribeFlushStats()
            {
                return "假任务状态：脏 " + _dirty.Count;
            }

            /// <inheritdoc/>
            public void Dispose()
            {
            }
        }

        /// <summary>
        /// ⚠️ **"刚交付"与"重启读盘后"两条路径必须一致**（§二十五的最有价值的一条）。
        ///
        /// <para>不注销条件时：刚交付 ⇒ 条件**还登记着**（之后每次击杀还喂给它）；
        /// 重启读盘后 ⇒ `LoadThenRegisterAsync` 跳过 `Submitted` ⇒ **不登记**。
        /// 同一个玩家状态，行为取决于"服务端有没有重启过" —— 而 `TrackedConditionCount`
        /// 正是这条不变式的**可观测形式**。</para>
        ///
        /// <para>📌 判据必须是"**两边相等**"：只测一边**测不出**这个 bug。</para>
        /// </summary>
        private static void Quest_SubmitUnregisters_SoBothPathsAgree()
        {
            QuestTables tables = LoadQuestTables();

            var backing = new Dictionary<int, int>();       // 模拟库（跨"重启"共用）
            var progress = new FakeProgressStore();
            var ledger = new FakeRewardLedger();

            using (var first = new QuestAuthority(tables, _ => progress, _ => ledger,
                                                  _ => new FakeQuestStateStore(backing)))
            {
                Task loading;
                first.Track(1, "爱丽丝", out loading);

                if (loading != null)
                {
                    loading.GetAwaiter().GetResult();
                }

                Check("§25 注销不变式：登录后还没接任务 ⇒ 登记 0 条条件",
                    first.TrackedConditionCount == 0, "实际 " + first.TrackedConditionCount);

                string reason;
                bool accepted = first.Accept(1, 3003, out reason);

                Check("§25 注销不变式：接取 3003 成功", accepted, reason);

                int afterAccept = first.TrackedConditionCount;

                Check("§25 注销不变式：接取后**登记数上升**（说明条件真的登记了）",
                    afterAccept > 0, "接取后 " + afterAccept + "（本用例靠这个数才有意义）");

                first.ApplyFact(new ProgressFact(1, NBC.Shared.Condition.EConditionEvent.KillMonster, 6003, 1));

                bool submitted = first.Submit(1, 3003, out reason);

                Check("§25 注销不变式：条件打满后**交付成功**", submitted, reason);

                int afterSubmit = first.TrackedConditionCount;

                Check("§25 注销不变式⭐：交付后**登记数下降**（条件已注销）",
                    afterSubmit < afterAccept,
                    "交付前 " + afterAccept + " → 交付后 " + afterSubmit);

                // ---- 模拟重启：新权威 + **全新的状态 store**（从同一份 backing 读回来）----
                var secondState = new FakeQuestStateStore(backing);

                using (var second = new QuestAuthority(tables, _ => progress, _ => ledger, _ => secondState))
                {
                    Task loading2;
                    second.Track(1, "爱丽丝", out loading2);

                    if (loading2 != null)
                    {
                        loading2.GetAwaiter().GetResult();
                    }

                    Check("§25 注销不变式：重启后**真的读了库**（不是空跑）",
                        secondState.LoadCount > 0, "LoadCount=" + secondState.LoadCount);

                    Check("§25 注销不变式⭐：**重启读盘后**的登记数 == 未重启时交付后的登记数",
                        second.TrackedConditionCount == afterSubmit,
                        "未重启交付后 " + afterSubmit + " vs 重启后 " + second.TrackedConditionCount +
                        "（两边不等 = 两条路径行为不一致）");
                }
            }
        }

        /// <summary>从客户端收到的消息里找**最后一条**任务状态同步（没有则 null）。</summary>
        /// <param name="client">客户端。</param>
        /// <returns>状态同步或 null。</returns>
        private static QuestStateSync FindQuestState(Client client)
        {
            for (int i = client.Received.Count - 1; i >= 0; i--)
            {
                QuestStateSync sync = client.Received[i].QuestState;

                if (sync != null)
                {
                    return sync;
                }
            }

            return null;
        }

        /// <summary>状态同步里某个任务的状态；没有这个任务时返回 -999（刻意不是 0）。</summary>
        /// <param name="sync">状态同步。</param>
        /// <param name="questId">任务编号。</param>
        /// <returns>状态数字。</returns>
        private static int StateOf(QuestStateSync sync, int questId)
        {
            if (sync == null)
            {
                return -999;
            }

            for (int i = 0; i < sync.Entries.Count; i++)
            {
                if (sync.Entries[i].QuestId == questId)
                {
                    return sync.Entries[i].State;
                }
            }

            return -999;
        }

        /// <summary>客户端收到几条错误。</summary>
        /// <param name="client">客户端。</param>
        /// <returns>条数。</returns>
        private static int CountErrors(Client client)
        {
            int n = 0;

            for (int i = 0; i < client.Received.Count; i++)
            {
                if (client.Received[i].Error != null)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>客户端收到的**最后一条**错误（没有则 null）。</summary>
        /// <param name="client">客户端。</param>
        /// <returns>错误或 null。</returns>
        private static ErrorResponse LastError(Client client)
        {
            for (int i = client.Received.Count - 1; i >= 0; i--)
            {
                ErrorResponse error = client.Received[i].Error;

                if (error != null)
                {
                    return error;
                }
            }

            return null;
        }

        /// <summary>
        /// 【十八】任务动作走**真网线**（§二十六）。
        ///
        /// <para>⚠️ 前提（我在交付简报里点明过）：**成功路径是"推送"发的** ——
        /// `QuestService` 成功时只返回 `Handled`，状态由 `QuestStateBroadcaster` 发。
        /// 所以这套用例**必须**自己构造广播器并注册处理器，否则断言的是"没人回包"。</para>
        ///
        /// <para>⚠️ 每条都要有**阳性对照**：①里同时断言"确实收到了消息"，
        /// 免得把"一条都没收到"当成通过（假绿）。</para>
        /// </summary>
        private static void QuestAction_SocketRoundTrip()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                QuestTables tables = LoadQuestTables();

                var backing = new Dictionary<int, int>();
                var progress = new FakeProgressStore();
                var ledger = new FakeRewardLedger();

                using (var authority = new QuestAuthority(tables, _ => progress, _ => ledger,
                                                          _ => new FakeQuestStateStore(backing)))
                using (var push = new QuestStateBroadcaster(h.Server, h.Registry, authority))
                {
                    var service = new QuestService(authority, push);
                    service.RegisterHandlers(h.Router);

                    // `FakeAccounts` 里 alice = 玩家 11（> 0 = 真实账号档案）
                    Client a = h.ConnectAndLogin("alice", "pw-alice");
                    long aliceId = AckOf(a, "alice 登录").PlayerId;

                    // ⚠️ 必须进房：广播器靠**房间**找会话（本项目"一个席位一个会话"）
                    h.JoinSameRoom(a);

                    Task? loading;
                    authority.Track(aliceId, "爱丽丝", out loading);

                    if (loading != null)
                    {
                        loading.GetAwaiter().GetResult();
                    }

                    // ---- ① 接取 ⇒ 收到 quest_state 且 state = 1 ----
                    a.Send(new ClientMessage { QuestAction = new QuestActionRequest { QuestId = 3003, Action = 1 } });
                    h.PumpFor(300);

                    Check("①：阳性对照 —— 确实收到了服务端消息（不是「什么都没收到」被算通过）",
                        a.Received.Count > 0, "收到 " + a.Received.Count + " 条");

                    QuestStateSync sync = FindQuestState(a);

                    Check("①⭐：登录玩家发「接取」⇒ 收到 `quest_state`", sync != null,
                        "没收到状态同步；共 " + a.Received.Count + " 条消息，错误 " + CountErrors(a) + " 条");

                    Check("①⭐：而且任务 3003 的 state = 1（Accepted）",
                        StateOf(sync, 3003) == 1, "state=" + StateOf(sync, 3003) + "（-999 = 没这条）");

                    // ---- ② 条件没满就交付 ⇒ 收到错误（不是成功、不是静默）----
                    int errorsBefore = CountErrors(a);

                    a.Send(new ClientMessage { QuestAction = new QuestActionRequest { QuestId = 3003, Action = 2 } });
                    h.PumpFor(300);

                    ErrorResponse rejected = LastError(a);

                    Check("②⭐：条件没满就「交付」⇒ **收到错误**（不是成功、不是静默）",
                        CountErrors(a) > errorsBefore && rejected != null,
                        "错误条数 " + errorsBefore + " → " + CountErrors(a));

                    Check("②：错误码是 `QuestRejected`（1006），不是别的",
                        rejected != null && rejected.Code == NBC.Shared.Net.NetErrors.QuestRejected,
                        rejected == null ? "没有错误" : "code=" + rejected.Code);

                    Check("②⭐：错误里带**原因原文**（说清「现在不能交付」）",
                        rejected != null && rejected.Message.Contains("不能交付"),
                        rejected == null ? "没有错误" : ("Message=" + rejected.Message));

                    // ---- ③ 游客发「接取」⇒ **明确拒绝**（与"没接权威"分开）----
                    Client guest = h.ConnectAndHandshake("游客乙");
                    HandshakeAck guestAck = AckOf(guest, "游客握手");

                    Check("③：阳性对照 —— 这个连接**确实是游客**（负 id）",
                        guestAck.PlayerId <= 0, "player_id=" + guestAck.PlayerId + "（不是游客的话这条用例没意义）");

                    guest.Send(new ClientMessage { QuestAction = new QuestActionRequest { QuestId = 3003, Action = 1 } });
                    h.PumpFor(300);

                    ErrorResponse guestRejected = LastError(guest);

                    Check("③⭐：游客发「接取」⇒ **被明确拒绝**（不是静默丢弃、不是当成功）",
                        guestRejected != null, "游客一条错误都没收到");

                    Check("③⭐：而且用的是**身份**码（`PlayerMismatch`=1004），**不是**「没接权威」那个码",
                        guestRejected != null && guestRejected.Code == NBC.Shared.Net.NetErrors.PlayerMismatch,
                        guestRejected == null ? "没有错误" : "code=" + guestRejected.Code);

                    Check("③：错误里告诉了玩家**该怎么办**（「先用账号登录」）",
                        guestRejected != null && guestRejected.Message.Contains("登录"),
                        guestRejected == null ? "没有错误" : ("Message=" + guestRejected.Message));

                    // ---- ④ 让它真的完成并交付 ⇒ 再交付一次必须幂等 ----
                    authority.ApplyFact(new ProgressFact(aliceId, NBC.Shared.Condition.EConditionEvent.KillMonster, 6003, 1));

                    a.Send(new ClientMessage { QuestAction = new QuestActionRequest { QuestId = 3003, Action = 2 } });
                    h.PumpFor(300);

                    QuestStateSync afterSubmit = FindQuestState(a);

                    Check("④：条件打满后「交付」⇒ 收到 state = 3（Submitted）",
                        StateOf(afterSubmit, 3003) == 3, "state=" + StateOf(afterSubmit, 3003));

                    long paidExp = ledger.PaidExp;
                    long paidGold = ledger.PaidGold;

                    Check("④：阳性对照 —— 交付**真的发了奖**（钱不是 0，否则下面那条测不出东西）",
                        paidExp != 0 && paidGold != 0, "exp=" + paidExp + " gold=" + paidGold);

                    int errorsBefore2 = CountErrors(a);

                    a.Send(new ClientMessage { QuestAction = new QuestActionRequest { QuestId = 3003, Action = 2 } });
                    h.PumpFor(300);

                    Check("④⭐：**重复交付** ⇒ 钱**一点没变**（不重复发奖）",
                        ledger.PaidExp == paidExp && ledger.PaidGold == paidGold,
                        "exp " + paidExp + " → " + ledger.PaidExp + "；gold " + paidGold + " → " + ledger.PaidGold);

                    Check("④：而且重复交付**没有被当成错误**（幂等语义：仍然成功）",
                        CountErrors(a) == errorsBefore2,
                        "错误条数 " + errorsBefore2 + " → " + CountErrors(a));

                    Check("④：台账里**只有一行**任务奖励（`owner_kind=1` / quest 3003）",
                        ledger.HasGranted(NBC.Shared.Reward.ERewardOwnerKind.Quest, 3003),
                        "台账：" + ledger.Describe());
                }
            }
        }

        /// <summary>分桶结果里某个任务的归属（找不到时返回一个明确的哨兵）。</summary>
        /// <param name="plan">分桶结果。</param>
        /// <param name="questId">任务编号。</param>
        /// <returns>归属（找不到时 Section = Done+100，见断言里的提示）。</returns>
        private static QuestSectionPlanner.Placement FindPlacement(
            List<QuestSectionPlanner.Placement> plan, int questId)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                if (plan[i].QuestId == questId)
                {
                    return plan[i];
                }
            }

            // 哨兵：调用方用 `Section` 断言时它会明显不等于任何期望值
            return new QuestSectionPlanner.Placement(questId, (EQuestSection)99, false, -1);
        }

        /// <summary>
        /// 【十九】任务面板**分桶**的纯逻辑（§二十七）。
        ///
        /// <para>⚠️ 这一节存在的意义：这条"不丢行"不变式原本长在 Unity 侧的
        /// `QuestPanelModel` 里 ⇒ **探针编不进去** ⇒ 只能靠 EditMode 验、**做不出可运行的变异**。
        /// 抽成 `QuestSectionPlanner`（纯 BCL）之后，它**当场能红**（见 §27.4 的变异）。</para>
        ///
        /// <para>📌 阳性对照不可省：`N > 0` 否则"一个都不丢"是空话。</para>
        /// </summary>
        private static void QuestSectionPlanner_BucketsAndNeverLosesRows()
        {
            // 输入：本地已接 2 个（1001 进行中、1002 已完成），本地认为可接 3 个（2001/2002/2003）
            var tracked = new List<QuestSectionPlanner.Entry>
            {
                new QuestSectionPlanner.Entry(1001, 1),
                new QuestSectionPlanner.Entry(1002, 2),
            };

            var offers = new List<int> { 2001, 2002, 2003 };

            // 权威：2001 说"已接"、2002 说"已交付"、2003 **明确说未接取(0)**；**1001/1002 一律沉默**
            var said = new Dictionary<int, int> { { 2001, 1 }, { 2002, 3 }, { 2003, 0 } };

            QuestSectionPlanner.StateQuery query = (int id, out int st) => said.TryGetValue(id, out st);

            const int expected = 5;     // 2 个本地已接 + 3 个本地可接

            List<QuestSectionPlanner.Placement> plan = QuestSectionPlanner.Plan(tracked, offers, query);

            Check("§27 分桶：**阳性对照** —— 输入不是空的（N > 0）",
                plan.Count > 0, "plan=" + plan.Count + "（空的话下面全是空话）");

            Check("§27⭐ **不丢行**：产出的条数 == 输入个数（" + expected + "）",
                plan.Count == expected, "plan=" + plan.Count + "（少于输入 = 有任务被挪丢了）");

            var seen = new HashSet<int>();
            bool duplicated = false;

            for (int i = 0; i < plan.Count; i++)
            {
                if (!seen.Add(plan[i].QuestId))
                {
                    duplicated = true;
                }
            }

            Check("§27⭐ **不丢行**：每个任务**恰好出现一次**（没有重复）",
                !duplicated && seen.Count == expected,
                "去重后 " + seen.Count + " 个；有重复=" + duplicated);

            // ---- 三值分桶 ----
            Check("§27：服务端说 1 ⇒ 进行中",
                FindPlacement(plan, 2001).Section == EQuestSection.Active,
                "Section=" + FindPlacement(plan, 2001).Section);

            Check("§27：服务端说 3 ⇒ 已交付",
                FindPlacement(plan, 2002).Section == EQuestSection.Done,
                "Section=" + FindPlacement(plan, 2002).Section);

            Check("§27⭐：服务端**明确说 0**（未接取）⇒ 可接，而且**算权威说的**（不标预测）",
                FindPlacement(plan, 2003).Section == EQuestSection.Offer &&
                FindPlacement(plan, 2003).FromAuthority,
                "Section=" + FindPlacement(plan, 2003).Section +
                "；FromAuthority=" + FindPlacement(plan, 2003).FromAuthority);

            Check("§27⭐：服务端**没说** ⇒ 按**本地**（本地已接且进行中 ⇒ 进行中）",
                FindPlacement(plan, 1001).Section == EQuestSection.Active,
                "Section=" + FindPlacement(plan, 1001).Section);

            Check("§27⭐：而且标成**本地预测**（`FromAuthority == false`）—— `null` 不许当成 `0`",
                !FindPlacement(plan, 1001).FromAuthority,
                "FromAuthority=" + FindPlacement(plan, 1001).FromAuthority);

            Check("§27：本地已接且已完成 ⇒ 可交付（同样按本地，因为服务端沉默）",
                FindPlacement(plan, 1002).Section == EQuestSection.Ready,
                "Section=" + FindPlacement(plan, 1002).Section);

            // ---- 一个都不许丢：并集 == 输入集合 ----
            bool allPresent = seen.Contains(1001) && seen.Contains(1002) &&
                              seen.Contains(2001) && seen.Contains(2002) && seen.Contains(2003);

            Check("§27⭐ **不丢行**：并集 == 输入集合（5 个任务一个不少）",
                allPresent, "缺了任务（见上面两个逐号检查）");
        }

        /// <summary>
        /// 记录"跟随"调用的假出口（§二十八）。
        /// <para>⚠️ **它故意只记"跟随到哪"，没有任何发送能力** —— 这正是"不许回路"的结构性保证。</para>
        /// </summary>
        private sealed class FollowSink : IQuestFollowSink
        {
            /// <summary>每次跟随的参数（任务号, 状态）。</summary>
            public readonly List<string> Calls = new List<string>();

            /// <inheritdoc/>
            public void FollowToState(int questId, int state)
            {
                Calls.Add(questId + "→" + state);
            }
        }

        /// <summary>
        /// 【二十】本地跟随权威（§二十八）。
        ///
        /// <para>⚠️ 这一节盯的是三条**静默**错误（都不会报错，只会让本地与服务端长期对不上）：</para>
        /// <list type="number">
        ///   <item><description>**不许把服务端状态当成本地操作** ⇒ 跟随只改状态，没有副作用
        ///   （出口 `IQuestFollowSink` **没有**发奖/重置/提示，也没有发请求）</description></item>
        ///   <item><description>**幂等** ⇒ 同一份同步施加两次，第二次 `Apply` 必须返回 0</description></item>
        ///   <item><description>**不许回路** ⇒ 跟随路径上**没有任何发送**（结构性：接口上没有那个方法）</description></item>
        /// </list>
        /// </summary>
        private static void QuestStateReconciler_FollowsAuthorityWithoutSideEffects()
        {
            // ---- ① 服务端说"已接取(1)"，本地是"未接取(0)" ⇒ 跟随到 1 ----
            QuestSyncDecision adopt = QuestStateReconciler.Decide(3003, 1, 0, false);

            Check("§28⭐：服务端说已接取 ⇒ 本地**跟随**到 1（Adopt）",
                adopt.Action == EQuestSyncAction.Adopt && adopt.TargetState == 1,
                "Action=" + adopt.Action + "；Target=" + adopt.TargetState);

            // ---- ② 幂等：本地已经等于权威 ⇒ **什么都不做** ----
            QuestSyncDecision same = QuestStateReconciler.Decide(3003, 1, 1, false);

            Check("§28⭐ **幂等**：本地已等于权威 ⇒ `None`（重复下发**什么都不做**）",
                same.Action == EQuestSyncAction.None,
                "Action=" + same.Action + "（不是 None 就会「接取两次」且不报错）");

            // ---- ③ 服务端**明确说未接取(0)**、本地接过 ⇒ **回退**（含往回退，取舍见文件头）----
            QuestSyncDecision revert = QuestStateReconciler.Decide(3003, 0, 1, false);

            Check("§28⭐：服务端**明确说未接取**而本地已接 ⇒ **回退**到 0（以服务端为准含往回退）",
                revert.Action == EQuestSyncAction.Revert && revert.TargetState == 0,
                "Action=" + revert.Action + "；Target=" + revert.TargetState);

            // ---- ④ 请求在飞（待确认）⇒ **先别回退**（免得"刚点完就弹回去"）----
            QuestSyncDecision pending = QuestStateReconciler.Decide(3003, 0, 1, true);

            Check("§28：**请求在飞时不回退**（否则玩家刚点完就被弹回去，界面闪一下）",
                pending.Action == EQuestSyncAction.None,
                "Action=" + pending.Action);

            // ---- ⑤ 离线/单机：没有权威 ⇒ 根本没有条目 ⇒ 一条决定都不产生 ----
            var empty = new List<QuestSectionPlanner.Entry>();
            List<QuestSyncDecision> none = QuestStateReconciler.Plan(empty, _ => 1, _ => false);

            Check("§28：**离线/单机**（没有权威、同步为空）⇒ 一条决定都不产生（行为与以前逐字一致）",
                none.Count == 0, "decisions=" + none.Count);

            // ---- ⑥ 施加：只跟随、且**第二次是 0**（幂等到"施加"这一层）----
            var entries = new List<QuestSectionPlanner.Entry>
            {
                new QuestSectionPlanner.Entry(3003, 1),     // 服务端说已接取
                new QuestSectionPlanner.Entry(3004, 0),     // 服务端明确说未接取
            };

            // 本地：3003 未接、3004 已接 ⇒ 一个要 Adopt、一个要 Revert
            var local = new Dictionary<int, int> { { 3003, 0 }, { 3004, 1 } };

            List<QuestSyncDecision> plan = QuestStateReconciler.Plan(
                entries, id => local.TryGetValue(id, out int s) ? s : 0, _ => false);

            var sink = new FollowSink();
            int applied = QuestStateReconciler.Apply(plan, sink);

            Check("§28：阳性对照 —— 计划里真的有要做的事（否则下面全是空话）",
                applied > 0, "applied=" + applied);

            Check("§28⭐：跟随**只调 `FollowToState`**，而且参数就是权威状态（无副作用可言）",
                sink.Calls.Count == applied &&
                sink.Calls.Contains("3003→1") && sink.Calls.Contains("3004→0"),
                "调用：" + string.Join("、", sink.Calls.ToArray()));

            // 把本地更新到"跟随之后"的样子，再施加**同一份**同步 ⇒ 必须是 0（幂等）
            local[3003] = 1;
            local[3004] = 0;

            List<QuestSyncDecision> plan2 = QuestStateReconciler.Plan(
                entries, id => local.TryGetValue(id, out int s2) ? s2 : 0, _ => false);

            var sink2 = new FollowSink();
            int applied2 = QuestStateReconciler.Apply(plan2, sink2);

            Check("§28⭐ **幂等（端到端）**：同一份同步施加第二次 ⇒ **0 次跟随**",
                applied2 == 0 && sink2.Calls.Count == 0,
                "applied2=" + applied2 + "；调用：" + string.Join("、", sink2.Calls.ToArray()));
        }

        /// <summary>【二十一】定点数学（S4-a：**只验证，不改实现**）。</summary>
        private static void FixedPoint_ArithmeticBoundaries_AndDeterminism()
        {
            // ---- ① 常规算术 ----
            Fix64 a = Fix64.FromInt(3);
            Fix64 b = Fix64.FromInt(4);

            Check("§S4a：`FromInt(3) * FromInt(4) == 12`（定点乘法）",
                (a * b) == Fix64.FromInt(12), "得到 " + (a * b).ToDouble());

            Check("§S4a：`One` 是乘法单位元、`Zero` 是加法单位元",
                (a * Fix64.One) == a && (a + Fix64.Zero) == a,
                "a*One=" + (a * Fix64.One).ToDouble() + "；a+Zero=" + (a + Fix64.Zero).ToDouble());

            Check("§S4a：负数与减法（`Half` 存在且 = 0.5）",
                (Fix64.Zero - a) == (-a) && Fix64.Half == (Fix64.One / Fix64.FromInt(2)),
                "-a=" + (-a).ToDouble() + "；Half=" + Fix64.Half.ToDouble());

            Check("§S4a：`MinusOne` 与 `Abs`",
                Fix64.MinusOne == -Fix64.One && Fix64.Abs(Fix64.MinusOne) == Fix64.One,
                "Abs(-1)=" + Fix64.Abs(Fix64.MinusOne).ToDouble());

            // ---- ② 溢出边界：**既有取舍是饱和**（不是抛）----
            Check("§S4a⭐：乘法溢出 ⇒ **饱和**到 `MaxValue`（不抛）",
                (Fix64.MaxValue * Fix64.FromInt(2)) == Fix64.MaxValue,
                "MaxValue*2 = raw " + (Fix64.MaxValue * Fix64.FromInt(2)).RawValue +
                "（期望 " + Fix64.MaxValue.RawValue + "）");

            Check("§S4a⭐：负向溢出 ⇒ 饱和到 `MinValue`",
                (Fix64.MinValue * Fix64.FromInt(2)) == Fix64.MinValue,
                "MinValue*2 = raw " + (Fix64.MinValue * Fix64.FromInt(2)).RawValue);

            // ---- ③ FromInt/ToInt：**范围内向零截断**、**越界饱和** ----
            Check("§S4a⭐：`ToInt` 在范围内**向零截断**（3.9 → 3、-3.9 → -3）",
                ((int)Fix64.FromDouble(3.9)) == 3 && ((int)Fix64.FromDouble(-3.9)) == -3,
                "3.9→" + ((int)Fix64.FromDouble(3.9)) + "；-3.9→" + ((int)Fix64.FromDouble(-3.9)));

            Check("§S4a：`ToInt` 越界 ⇒ **饱和**到 int 边界（不是回绕）",
                ((int)Fix64.MaxValue) == int.MaxValue && ((int)Fix64.MinValue) == int.MinValue,
                "Max→" + ((int)Fix64.MaxValue) + "；Min→" + ((int)Fix64.MinValue));

            // ---- ④ FromFloat/FromDouble：NaN 有守卫、无穷饱和 ----
            bool nanThrew = false;
            try { Fix64.FromDouble(double.NaN); } catch (System.ArgumentException) { nanThrew = true; }

            Check("§S4a⭐：`FromDouble(NaN)` **抛** `ArgumentException`（不静默给个数）",
                nanThrew, "没抛 —— 那会把 NaN 变成一个看起来正常的值");

            Check("§S4a：`FromDouble(±∞)` 饱和到 `MaxValue`/`MinValue`",
                Fix64.FromDouble(double.PositiveInfinity) == Fix64.MaxValue &&
                Fix64.FromDouble(double.NegativeInfinity) == Fix64.MinValue,
                "∞→raw " + Fix64.FromDouble(double.PositiveInfinity).RawValue);

            // ---- ⑤ 除零：**除法与取余都要抛** ----
            bool divThrew = false;
            bool modThrew = false;

            try { Fix64 unused = a / Fix64.Zero; } catch (System.DivideByZeroException) { divThrew = true; }
            try { Fix64 unused = a % Fix64.Zero; } catch (System.DivideByZeroException) { modThrew = true; }

            Check("§S4a⭐：除法除零 ⇒ 抛 `DivideByZeroException`", divThrew, "没抛（会给出一个假结果）");
            Check("§S4a⭐：取余除零 ⇒ 也抛 `DivideByZeroException`", modThrew, "没抛");

            // ---- ⑥ Sqrt：三条判据 ----
            Check("§S4a⭐：`Sqrt` 完全平方数**精确**（4→2、9→3、16→4、0→0、1→1）",
                Fix64.Sqrt(Fix64.FromInt(4)) == Fix64.FromInt(2) &&
                Fix64.Sqrt(Fix64.FromInt(9)) == Fix64.FromInt(3) &&
                Fix64.Sqrt(Fix64.FromInt(16)) == Fix64.FromInt(4) &&
                Fix64.Sqrt(Fix64.Zero) == Fix64.Zero &&
                Fix64.Sqrt(Fix64.One) == Fix64.One,
                "√4=" + Fix64.Sqrt(Fix64.FromInt(4)).ToDouble() +
                "；√9=" + Fix64.Sqrt(Fix64.FromInt(9)).ToDouble() +
                "；√16=" + Fix64.Sqrt(Fix64.FromInt(16)).ToDouble());

            // 不变式：Sqrt(x)^2 <= x < (Sqrt(x)+1)^2 —— 扫一批值（含非完全平方与大值）
            int violated = 0;
            int scanned = 0;
            string firstBad = string.Empty;

            for (int i = 0; i <= 400; i++)
            {
                Fix64 x = Fix64.FromInt(i) + Fix64.FromRaw(123456789L);   // 带上小数部分，避免都是整数
                Fix64 root = Fix64.Sqrt(x);
                Fix64 low = root * root;
                Fix64 high = (root + Fix64.One) * (root + Fix64.One);

                scanned++;

                if (!(low <= x && x < high) && violated == 0)
                {
                    firstBad = "x=" + x.ToDouble() + " root=" + root.ToDouble() +
                               " root²=" + low.ToDouble() + " (root+1)²=" + high.ToDouble();
                }

                if (!(low <= x && x < high))
                {
                    violated++;
                }
            }

            Check("§S4a：扫描了**足够多**的值（否则下面的不变式是空话）", scanned >= 400, "scanned=" + scanned);

            Check("§S4a⭐ 不变式：`Sqrt(x)² <= x < (Sqrt(x)+1)²`（401 个值，含小数与大值）",
                violated == 0, violated + " 个违反；第一个：" + firstBad);

            bool sqrtNegativeThrew = false;
            try { Fix64.Sqrt(Fix64.MinusOne); } catch (System.ArgumentOutOfRangeException) { sqrtNegativeThrew = true; }

            Check("§S4a：对负数开方 ⇒ 抛（不返回 NaN 似的假值）", sqrtNegativeThrew, "没抛");

            // ---- ⑦ FixVector3：点积/长度平方/归一化 ----
            var v = new FixVector3(3, 4, 0);
            var w = new FixVector3(1, 2, 2);

            Check("§S4a：`(3,4,0)·(1,2,2) == 11`（与整数算例一致）",
                FixVector3.Dot(v, w) == Fix64.FromInt(11), "=" + FixVector3.Dot(v, w).ToDouble());

            Check("§S4a：`(3,4,0)` 的长度平方 = 25、长度 = 5",
                v.SqrMagnitude == Fix64.FromInt(25) && v.Magnitude == Fix64.FromInt(5),
                "sqr=" + v.SqrMagnitude.ToDouble() + "；len=" + v.Magnitude.ToDouble());

            // ⚠️ 容差说明：开方是**牛顿迭代**，末位可能差几个 raw 单位（1 raw = 2^-32 ≈ 2.3e-10）。
            //    取 16 raw ≈ 3.7e-9：够装下迭代末位误差，又远小于"真的算错"（那会差好几个数量级）。
            Fix64 unitTolerance = Fix64.FromRaw(16);
            FixVector3 n = new FixVector3(3, 4, 0).Normalized();
            Fix64 error = Fix64.Abs(n.SqrMagnitude - Fix64.One);

            Check("§S4a⭐：归一化后长度 ≈ `One`（容差 16 raw ≈ 3.7e-9）",
                error <= unitTolerance,
                "|len²-1| = " + error.ToDouble() + "（raw " + error.RawValue + "，容差 raw 16）");

            Check("§S4a：归一化方向对（(3,4,0)→(0.6,0.8,0)）",
                Fix64.Abs(n.X - Fix64.FromDouble(0.6)) <= unitTolerance &&
                Fix64.Abs(n.Y - Fix64.FromDouble(0.8)) <= unitTolerance,
                "n=(" + n.X.ToDouble() + ", " + n.Y.ToDouble() + ", " + n.Z.ToDouble() + ")");

            // ---- ⑧ CORDIC 已知值（既然在用，就不能不验）----
            // ⚠️ 容差说明：CORDIC 32 次迭代 + 定点舍入，误差通常在 1e-5 量级。
            //    取 1<<16 raw ≈ 1.5e-5：够装下 CORDIC 误差，又能抓住"公式写反/象限错"那类真错。
            Fix64 trigTolerance = Fix64.FromRaw(1L << 16);

            Check("§S4a：`Sin(0) == 0`（精确）", FixMath.Sin(Fix64.Zero) == Fix64.Zero,
                "=" + FixMath.Sin(Fix64.Zero).ToDouble());

            Check("§S4a：`Cos(0) == One`（精确）", FixMath.Cos(Fix64.Zero) == Fix64.One,
                "=" + FixMath.Cos(Fix64.Zero).ToDouble());

            Check("§S4a⭐：`Sin(π/2) ≈ One`（容差 1.5e-5）",
                Fix64.Abs(FixMath.Sin(FixMath.HalfPi) - Fix64.One) <= trigTolerance,
                "=" + FixMath.Sin(FixMath.HalfPi).ToDouble());

            Check("§S4a⭐：`Cos(π) ≈ -One`（容差 1.5e-5）",
                Fix64.Abs(FixMath.Cos(FixMath.Pi) + Fix64.One) <= trigTolerance,
                "=" + FixMath.Cos(FixMath.Pi).ToDouble());

            // 恒等式 sin²+cos²≈1（扫几个角度，含非特殊角）
            int identityBad = 0;
            string identityFirst = string.Empty;

            for (int i = 0; i <= 16; i++)
            {
                Fix64 angle = FixMath.Pi * Fix64.FromInt(i) / Fix64.FromInt(8);   // 0, π/8, ..., 2π
                Fix64 s = FixMath.Sin(angle);
                Fix64 c = FixMath.Cos(angle);
                Fix64 sum = s * s + c * c;

                if (Fix64.Abs(sum - Fix64.One) > trigTolerance)
                {
                    identityBad++;

                    if (identityFirst.Length == 0)
                    {
                        identityFirst = "角度 " + angle.ToDouble() + " ⇒ sin²+cos²=" + sum.ToDouble();
                    }
                }
            }

            Check("§S4a⭐ 恒等式：`sin²+cos² ≈ One`（0..2π 取 17 个角）",
                identityBad == 0, identityBad + " 个越界；第一个：" + identityFirst);

            // ---- ⑨ ⭐ 逐位确定性（帧同步的地基）----
            // 同一串表达式跑**两遍**，要求 RawValue **逐位相等**。
            // 这不是"差不多就行"：两遍不等 ⇒ 两端跑同一个输入会得到不同世界。
            Fix64 d1 = ((Fix64.FromInt(7) * Fix64.FromInt(3) + Fix64.Half) / Fix64.FromInt(5)) -
                       Fix64.Sqrt(Fix64.FromInt(2));
            Fix64 d2 = ((Fix64.FromInt(7) * Fix64.FromInt(3) + Fix64.Half) / Fix64.FromInt(5)) -
                       Fix64.Sqrt(Fix64.FromInt(2));

            Check("§S4a⭐ **逐位确定性**：同一表达式跑两遍 ⇒ `RawValue` 完全相等",
                d1.RawValue == d2.RawValue,
                "第一遍 raw " + d1.RawValue + " vs 第二遍 raw " + d2.RawValue);

            // 再来一遍"链式"的（含 CORDIC）：这类最容易因为内部缓存/静态状态而不确定
            Fix64 c1 = FixMath.Cos(FixMath.Pi / Fix64.FromInt(3)) * Fix64.FromInt(100);
            Fix64 c2 = FixMath.Cos(FixMath.Pi / Fix64.FromInt(3)) * Fix64.FromInt(100);

            Check("§S4a⭐ **逐位确定性（含 CORDIC）**：`cos(π/3)*100` 两遍逐位相等",
                c1.RawValue == c2.RawValue,
                "raw " + c1.RawValue + " vs " + c2.RawValue);

            // 换一个"顺序"重算同一个值 ⇒ 也必须相等（确定性不允许受调用历史影响）
            Fix64 c3 = FixMath.Cos(FixMath.Pi / Fix64.FromInt(3)) * Fix64.FromInt(100);

            Check("§S4a⭐ **逐位确定性（不受调用历史影响）**：第三次结果仍逐位相等",
                c3.RawValue == c1.RawValue,
                "raw " + c1.RawValue + " → " + c3.RawValue);
        }

        /// <summary>造一个"标准开局"世界（实体登记顺序 = 参数顺序）。</summary>
        /// <param name="order">按什么顺序登记这三个实体。</param>
        /// <returns>世界。</returns>
        private static WorldState NewStandardWorld(int[] order)
        {
            var world = new WorldState();

            for (int i = 0; i < order.Length; i++)
            {
                switch (order[i])
                {
                    case 1: world.Add(1, new FixVector3(0, 0, 0), 100); break;
                    case 2: world.Add(2, new FixVector3(1, 0, 0), 100); break;
                    default: world.Add(3, new FixVector3(5, 0, 0), 100); break;
                }
            }

            return world;
        }

        /// <summary>
        /// 【二十二】确定性战斗内核 + 状态哈希（S4-b）。
        ///
        /// <para>⚠️ 这一节的核心判据是**"两个独立世界跑同一批输入，逐帧哈希相等"**；
        /// 但**光有它不够** —— 如果哈希恒为常量，它也"相等"。所以还必须有一条
        /// **阳性对照**：故意让两边分歧 ⇒ 哈希**必须不等**。没有那条，前面全是假绿。</para>
        /// </summary>
        private static void DeterministicWorld_SameInputsSameHashes()
        {
            // ---- ① 具体算例（整数可手算，断言**精确相等**）----
            WorldState one = NewStandardWorld(new[] { 1, 2, 3 });

            Check("§S4b：世界里有 3 个实体、帧号从 0 开始",
                one.Count == 3 && one.Tick == 0,
                "count=" + one.Count + "；tick=" + one.Tick);

            // 实体 1 朝 +X 走一步；实体 2 打实体 1（相距 1 米 < 射程 2 米）
            var inputs1 = new List<SimInput>
            {
                new SimInput { EntityId = 1, MoveDirection = new FixVector3(1, 0, 0), AttackTargetId = 0 },
                new SimInput { EntityId = 2, MoveDirection = FixVector3.Zero, AttackTargetId = 1 },
            };

            int hits = WorldStep.Step(one, inputs1, 1);

            Check("§S4b：射程内攻击 ⇒ 计 1 次命中", hits == 1, "hits=" + hits);

            SimEntity afterMove = one.GetAt(one.IndexOf(1));

            Check("§S4b：移动 1 tick ⇒ X **精确等于** `MmToMeters(100)`（0.1 米）",
                afterMove.Position.X == WorldStep.MmToMeters(WorldStep.SpeedMmPerTick),
                "X=" + afterMove.Position.X.ToDouble() +
                "（期望 raw " + WorldStep.MmToMeters(WorldStep.SpeedMmPerTick).RawValue +
                "，实得 raw " + afterMove.Position.X.RawValue + "）");

            Check("§S4b：移动只改 X（Y/Z 仍为 0）",
                afterMove.Position.Y == Fix64.Zero && afterMove.Position.Z == Fix64.Zero,
                "Y=" + afterMove.Position.Y.ToDouble() + "；Z=" + afterMove.Position.Z.ToDouble());

            // ⚠️ 挨打的是**实体 1**（输入里是"实体 2 打实体 1"）—— 第一版我错写成实体 2，
            //    当场红了；红的是断言不是产品（`hits==1` 与冷却那条都是绿的）。
            SimEntity victim = one.GetAt(one.IndexOf(1));

            Check("§S4b：伤害结算**复用** `DamageMath`（100 − 25 = 75）",
                victim.Hp == 100 - WorldStep.AttackDamage,
                "hp=" + victim.Hp + "（期望 " + (100 - WorldStep.AttackDamage) + "）");

            SimEntity attacker = one.GetAt(one.IndexOf(2));

            Check("§S4b：冷却按 `BattleRules.BasicAttackCooldownTicks` 记（下一帧不能连打）",
                attacker.AttackReadyTick == 1 + NBC.Shared.Battle.BattleRules.BasicAttackCooldownTicks,
                "AttackReadyTick=" + attacker.AttackReadyTick);

            Check("§S4b：帧号推进了 1", one.Tick == 1, "tick=" + one.Tick);

            // ---- ② ⭐ 两个独立世界 + 同一批输入 + 跑 64 帧 ⇒ 每帧哈希相等 ----
            WorldState a = NewStandardWorld(new[] { 1, 2, 3 });
            WorldState b = NewStandardWorld(new[] { 1, 2, 3 });

            int mismatchedFrame = -1;
            ulong firstMismatchA = 0;
            ulong firstMismatchB = 0;
            const int frames = 64;

            for (int frame = 0; frame < frames; frame++)
            {
                // ⚠️ 输入**每帧重新构造**（两个世界拿到的是同一批值，不是同一个对象）
                List<SimInput> stepInputs = BuildFrameInputs(frame);

                WorldStep.Step(a, stepInputs, 1);
                WorldStep.Step(b, stepInputs, 1);

                ulong ha = WorldStateHash.Compute(a);
                ulong hb = WorldStateHash.Compute(b);

                if (ha != hb && mismatchedFrame < 0)
                {
                    mismatchedFrame = frame;
                    firstMismatchA = ha;
                    firstMismatchB = hb;
                }
            }

            Check("§S4b：真的跑了足够多帧（否则下面的「逐帧相等」是空话）",
                a.Tick == frames && b.Tick == frames,
                "a.tick=" + a.Tick + "；b.tick=" + b.Tick);

            Check("§S4b⭐ **逐帧哈希相等**：两个独立世界 + 同一批输入 + " + frames + " 帧",
                mismatchedFrame < 0,
                "第 " + mismatchedFrame + " 帧开始不等：" + firstMismatchA + " vs " + firstMismatchB);

            // ---- ③ ⭐ 登记顺序无关（判据②的可观测形式）----
            WorldState ordered = NewStandardWorld(new[] { 1, 2, 3 });
            WorldState shuffled = NewStandardWorld(new[] { 3, 1, 2 });

            Check("§S4b⭐ **顺序无关**：同一批实体换登记顺序 ⇒ **初始哈希就相等**",
                WorldStateHash.Compute(ordered) == WorldStateHash.Compute(shuffled),
                "顺序登记 " + WorldStateHash.Compute(ordered) + " vs 打乱登记 " + WorldStateHash.Compute(shuffled));

            for (int frame = 0; frame < 16; frame++)
            {
                List<SimInput> stepInputs = BuildFrameInputs(frame);
                WorldStep.Step(ordered, stepInputs, 1);
                WorldStep.Step(shuffled, stepInputs, 1);
            }

            Check("§S4b⭐ **顺序无关（跑 16 帧后仍相等）**：登记顺序不影响任何结果",
                WorldStateHash.Compute(ordered) == WorldStateHash.Compute(shuffled),
                "跑完 16 帧：" + WorldStateHash.Compute(ordered) + " vs " + WorldStateHash.Compute(shuffled));

            // ---- ④ ⭐ 阳性对照：分歧**必须**能被抓到（否则上面三条可能是假绿）----
            WorldState diverge = NewStandardWorld(new[] { 1, 2, 3 });

            for (int frame = 0; frame < 16; frame++)
            {
                List<SimInput> stepInputs = BuildFrameInputs(frame);
                WorldStep.Step(diverge, stepInputs, 1);
            }

            // 让它在同一帧"少走一步"：输入不同 ⇒ 状态不同
            var nudged = new List<SimInput>
            {
                new SimInput { EntityId = 1, MoveDirection = new FixVector3(0, 1, 0), AttackTargetId = 0 },
            };

            WorldStep.Step(diverge, nudged, 1);

            Check("§S4b⭐ **阳性对照**：故意让一边多走一帧不同的输入 ⇒ 哈希**必须不等**",
                WorldStateHash.Compute(diverge) != WorldStateHash.Compute(ordered),
                "两边算出同一个哈希 = 哈希恒为常量 ⇒ 前面那些「相等」全是假绿");

            // 再来一条更细的：只差一个位置分量
            WorldState probeA = NewStandardWorld(new[] { 1, 2, 3 });
            WorldState probeB = NewStandardWorld(new[] { 1, 2, 3 });

            WorldStep.Step(probeA, new List<SimInput>
            {
                new SimInput { EntityId = 1, MoveDirection = new FixVector3(1, 0, 0) },
            }, 1);

            WorldStep.Step(probeB, new List<SimInput>
            {
                new SimInput { EntityId = 1, MoveDirection = new FixVector3(1, 1, 0) },
            }, 1);

            Check("§S4b⭐ 阳性对照（细）：只差一个方向分量 ⇒ 哈希也**必须不等**",
                WorldStateHash.Compute(probeA) != WorldStateHash.Compute(probeB),
                "两个不同方向算出了同一个哈希 ⇒ 哈希丢了位置信息");

            // 空世界 vs 有实体的世界：哈希不能相同（"少一个实体"要能被发现）
            Check("§S4b：空世界与有实体的世界哈希不同（实体个数进哈希了）",
                WorldStateHash.Compute(new WorldState()) != WorldStateHash.Compute(NewStandardWorld(new[] { 1, 2, 3 })),
                "空世界哈希与 3 实体世界相同 ⇒ 实体个数没进哈希");
        }

        /// <summary>造第 N 帧的输入（两边必须拿到**同一批值**）。</summary>
        /// <param name="frame">帧号。</param>
        /// <returns>输入列表。</returns>
        private static List<SimInput> BuildFrameInputs(int frame)
        {
            // 前 8 帧：1 朝 +X 走；2 一直打 1；3 站着不动
            // 之后：1 停住、2 继续打、3 朝 −X 走（让位置与血量的组合随时间变化）
            var list = new List<SimInput>(3);

            if (frame < 8)
            {
                list.Add(new SimInput { EntityId = 1, MoveDirection = new FixVector3(1, 0, 0), AttackTargetId = 0 });
            }
            else
            {
                list.Add(new SimInput { EntityId = 1, MoveDirection = FixVector3.Zero, AttackTargetId = 0 });
            }

            list.Add(new SimInput { EntityId = 2, MoveDirection = FixVector3.Zero, AttackTargetId = 1 });
            list.Add(new SimInput
            {
                EntityId = 3,
                MoveDirection = frame < 8 ? FixVector3.Zero : new FixVector3(-1, 0, 0),
                AttackTargetId = 0
            });

            return list;
        }

        /// <summary>造一个锁步用的世界（两个实体：玩家 1 在原点、玩家 2 在 1 米外）。</summary>
        /// <returns>世界。</returns>
        private static WorldState NewLockstepWorld()
        {
            var world = new WorldState();
            world.Add(1, new FixVector3(0, 0, 0), 100);
            world.Add(2, new FixVector3(1, 0, 0), 100);
            return world;
        }

        /// <summary>
        /// 【二十三】锁步调度（S4-c：**直驱纯逻辑，不开 socket**）。
        ///
        /// <para>⚠️ 这一节盯的是三条**静默**语义（`LockstepScheduler` 文件头写死了）：
        /// 缺输入填默认 + `Missing`、重复/乱序按帧号处理、**同一帧只推进一次**。
        /// 最后那条是锁步最经典的 bug：破了它两边的帧数会**悄悄错开**。</para>
        /// </summary>
        private static void LockstepScheduler_FramesMatchAcrossPeers()
        {
            var roster = new List<long> { 1, 2 };
            const int frames = 24;

            // 两个"端"：各自一个世界 + 一个调度器（模拟服务端与客户端各算一份）
            WorldState serverWorld = NewLockstepWorld();
            WorldState clientWorld = NewLockstepWorld();

            var server = new LockstepScheduler(serverWorld, roster, LockstepScheduler.DefaultInputDelayTicks);
            var client = new LockstepScheduler(clientWorld, roster, LockstepScheduler.DefaultInputDelayTicks);

            int broadcastFrames = 0;
            int hashMismatch = -1;
            ulong mismatchA = 0;
            ulong mismatchB = 0;

            // ⚠️ 预交前 frames 帧的输入（两个端交**同一批值**）
            for (int f = 0; f < frames; f++)
            {
                string ignored;

                // 玩家 1 一直朝 +X 走
                server.Submit(1, f, new FixVector3(1, 0, 0), 0, 0, out ignored);
                client.Submit(1, f, new FixVector3(1, 0, 0), 0, 0, out ignored);

                // 玩家 2 打玩家 1
                server.Submit(2, f, FixVector3.Zero, 0, 1, out ignored);
                client.Submit(2, f, FixVector3.Zero, 0, 1, out ignored);
            }

            // 跑足够多 tick，让所有帧都到点广播
            for (int t = 0; t < frames + LockstepScheduler.DefaultInputDelayTicks + 2; t++)
            {
                LockstepFramePlan planA;
                LockstepFramePlan planB;

                bool steppedA = server.AdvanceTick(out planA);
                bool steppedB = client.AdvanceTick(out planB);

                if (steppedA != steppedB)
                {
                    Check("§S4c⭐：两个端**推进节奏一致**（同一 tick 都到点或都没到点）", false,
                        "A=" + steppedA + " B=" + steppedB);
                    return;
                }

                if (!steppedA)
                {
                    continue;
                }

                broadcastFrames++;

                // ⭐ 逐帧对账：服务端算的哈希 == 客户端自己算的哈希
                if (planA.ServerHash != planB.ServerHash && hashMismatch < 0)
                {
                    hashMismatch = planA.Frame;
                    mismatchA = planA.ServerHash;
                    mismatchB = planB.ServerHash;
                }
            }

            Check("§S4c：阳性对照 —— 真的广播了足够多帧（否则下面全是空话）",
                broadcastFrames >= frames, "广播了 " + broadcastFrames + " 帧（期望 ≥ " + frames + "）");

            Check("§S4c⭐ **逐帧哈希对账**：服务端与客户端**每一帧都相等**（" + broadcastFrames + " 帧）",
                hashMismatch < 0,
                "第 " + hashMismatch + " 帧开始不等：" + mismatchA + " vs " + mismatchB);

            Check("§S4c⭐：而且广播里的哈希 == 该世界独立算出来的哈希（不是另算一份）",
                WorldStateHash.Compute(serverWorld) == WorldStateHash.Compute(clientWorld),
                "终局哈希：" + WorldStateHash.Compute(serverWorld) + " vs " + WorldStateHash.Compute(clientWorld));

            // ---- ⭐ 同一帧只推进一次（去重）----
            Check("§S4c⭐ **同一帧只推进一次**：推进帧数 == 广播帧数（没有因为重复输入多推）",
                server.StepsExecuted == broadcastFrames,
                "StepsExecuted=" + server.StepsExecuted + " vs 广播 " + broadcastFrames + " 帧");

            int rejectedBefore = server.RejectedInputs;
            string dupReason;
            bool dupAccepted = server.Submit(1, 5, new FixVector3(0, 1, 0), 0, 0, out dupReason);

            Check("§S4c⭐ **重复输入被丢掉**：同一 (帧, 玩家) 再交一次 ⇒ 不采纳",
                !dupAccepted && server.RejectedInputs > rejectedBefore,
                "accepted=" + dupAccepted + "；原因=" + dupReason);

            Check("§S4c⭐：而且**重复输入没有让世界多推一帧**（这就是那道闸）",
                server.StepsExecuted == broadcastFrames,
                "StepsExecuted=" + server.StepsExecuted + "（多出来 = 两边帧数悄悄错开）");

            // ---- ⭐ 缺输入：玩家 2 从第 10 帧起不发 ----
            WorldState missWorld = NewLockstepWorld();
            var miss = new LockstepScheduler(missWorld, roster, LockstepScheduler.DefaultInputDelayTicks);

            for (int f = 0; f < 12; f++)
            {
                string ignored;
                miss.Submit(1, f, new FixVector3(1, 0, 0), 0, 0, out ignored);
                // ⚠️ 玩家 2 故意**一条都不发**
            }

            int missingSeen = 0;
            int framesSeen = 0;

            for (int t = 0; t < 12 + LockstepScheduler.DefaultInputDelayTicks + 2; t++)
            {
                LockstepFramePlan plan;

                if (!miss.AdvanceTick(out plan))
                {
                    continue;
                }

                framesSeen++;

                for (int i = 0; i < plan.Inputs.Count; i++)
                {
                    if (plan.Inputs[i].PlayerId == 2 && plan.Inputs[i].Missing)
                    {
                        missingSeen++;
                    }
                }
            }

            Check("§S4c⭐ **缺输入看得见**：玩家 2 从没发输入 ⇒ 每一帧都带 `Missing = true`",
                framesSeen > 0 && missingSeen == framesSeen,
                "广播 " + framesSeen + " 帧，其中标了 Missing 的 " + missingSeen + " 条");

            Check("§S4c：缺输入那帧仍然推进（不卡住）且 `Missing` 不阻止这一帧广播",
                miss.StepsExecuted == framesSeen,
                "StepsExecuted=" + miss.StepsExecuted + " vs 广播 " + framesSeen + " 帧");

            // ---- ⭐ 乱序：第 N+2 帧的输入先到 ⇒ 缓冲，不许提前用 ----
            WorldState orderA = NewLockstepWorld();
            WorldState orderB = NewLockstepWorld();

            var inOrder = new LockstepScheduler(orderA, roster, LockstepScheduler.DefaultInputDelayTicks);
            var shuffled = new LockstepScheduler(orderB, roster, LockstepScheduler.DefaultInputDelayTicks);

            // A：按 0,1,2 顺序交
            for (int f = 0; f < 3; f++)
            {
                string ignored;
                inOrder.Submit(1, f, f == 2 ? new FixVector3(0, 1, 0) : new FixVector3(1, 0, 0), 0, 0, out ignored);
            }

            // B：**倒着交**（第 2 帧先到，然后 1、0）
            {
                string ignored;
                shuffled.Submit(1, 2, new FixVector3(0, 1, 0), 0, 0, out ignored);
                shuffled.Submit(1, 1, new FixVector3(1, 0, 0), 0, 0, out ignored);
                shuffled.Submit(1, 0, new FixVector3(1, 0, 0), 0, 0, out ignored);
            }

            bool sameEveryFrame = true;
            string firstDiff = string.Empty;

            for (int t = 0; t < 3 + LockstepScheduler.DefaultInputDelayTicks + 2; t++)
            {
                LockstepFramePlan pa;
                LockstepFramePlan pb;

                bool sa = inOrder.AdvanceTick(out pa);
                bool sb = shuffled.AdvanceTick(out pb);

                if (sa != sb || (sa && pa.ServerHash != pb.ServerHash))
                {
                    sameEveryFrame = false;

                    if (firstDiff.Length == 0)
                    {
                        firstDiff = "tick " + t + "：A=" + (sa ? pa.ServerHash.ToString() : "无") +
                                    " B=" + (sb ? pb.ServerHash.ToString() : "无");
                    }
                }
            }

            Check("§S4c⭐ **乱序输入不提前生效**：倒着交与顺着交 ⇒ 逐帧哈希相同",
                sameEveryFrame, firstDiff);

            // ---- ⭐ 阳性对照：分歧必须能被抓到 ----
            WorldState divergeWorld = NewLockstepWorld();
            var diverge = new LockstepScheduler(divergeWorld, roster, LockstepScheduler.DefaultInputDelayTicks);

            for (int f = 0; f < frames; f++)
            {
                string ignored;

                // ⚠️ 只差**一个方向分量**（0,1,0 vs 1,0,0）
                diverge.Submit(1, f, new FixVector3(0, 1, 0), 0, 0, out ignored);
                diverge.Submit(2, f, FixVector3.Zero, 0, 1, out ignored);
            }

            ulong divergedHash = 0;

            for (int t = 0; t < frames + LockstepScheduler.DefaultInputDelayTicks + 2; t++)
            {
                LockstepFramePlan plan;

                if (diverge.AdvanceTick(out plan))
                {
                    divergedHash = plan.ServerHash;
                }
            }

            Check("§S4c⭐ **阳性对照**：一个端改一个输入分量 ⇒ 终局哈希**必须不等**",
                divergedHash != WorldStateHash.Compute(serverWorld),
                "改过的一端 " + divergedHash + " vs 正常端 " + WorldStateHash.Compute(serverWorld) +
                "（相同 = 哈希恒为常量 ⇒ 前面那些「相等」全是假绿）");
        }

        /// <summary>造一个测试实体。</summary>
        /// <param name="id">编号。</param>
        /// <param name="x">X 位置（米）。</param>
        /// <param name="hp">血量。</param>
        /// <returns>实体。</returns>
        private static SimEntity MakeEntity(int id, int x, int hp)
        {
            SimEntity e;
            e.Id = id;
            e.Position = new FixVector3(x, 0, 0);
            e.Hp = hp;
            e.MaxHp = hp;
            e.AttackReadyTick = 0;
            return e;
        }

        /// <summary>找客户端收到的最后一条 `lockstep_start`。</summary>
        /// <param name="client">客户端。</param>
        /// <returns>开局消息或 null。</returns>
        private static LockstepStart FindLockstepStart(Client client)
        {
            for (int i = client.Received.Count - 1; i >= 0; i--)
            {
                if (client.Received[i].LockstepStart != null)
                {
                    return client.Received[i].LockstepStart;
                }
            }

            return null;
        }

        /// <summary>
        /// 【二十四·第 1 步】**只验一条**：`StartRoom` ⇒ 会话收到 `lockstep_start`
        /// 且 `InitialHash` == 服务端按同一份实体建出来的世界哈希（"**能不能开始**"的判据）。
        ///
        /// <para>⚠️ 刻意**最小**：先让这一条跑通，再逐条加其余的。
        /// 上一版一次加四条，撞上 `LockstepService.Tick` 的 `while (AdvanceTick)` 死循环
        /// ⇒ 挂 600s，而且 `dotnet build` **全绿**（连失败都吐不出来）。
        /// ⇒ **加检查要一条一条来；跑的时候必须能看出"是挂还是慢"。**</para>
        /// </summary>
        private static void LockstepService_StartAndInitialHash()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                var service = new LockstepService(h.Server, h.Registry);
                service.RegisterHandlers(h.Router);

                Client a = h.ConnectAndLogin("alice", "pw-alice");
                long aliceId = AckOf(a, "alice 登录").PlayerId;
                h.JoinSameRoom(a);

                Room room = null;

                foreach (Room r in h.Registry.Rooms)
                {
                    room = r;
                    break;
                }

                Check("§S4c2：阳性对照 —— 拿到了房间（拿不到就无从广播）", room != null, "房间表为空");

                if (room == null)
                {
                    return;
                }

                var entities = new List<SimEntity>
                {
                    MakeEntity((int)aliceId, 0, 100),
                    MakeEntity((int)aliceId + 1, 1, 100),
                };

                Check("§S4c2：`StartRoom` 返回成功（房号认得出来）",
                    service.StartRoom(room.RoomId, entities, 2), "返回 false");

                h.PumpFor(200);

                LockstepStart start = FindLockstepStart(a);

                Check("§S4c2⭐：会话**收到了 `lockstep_start`**（阳性对照：真的收到了东西）",
                    start != null, "一条都没收到；共 " + a.Received.Count + " 条消息");

                // 独立建一份世界（**故意反序登记**，顺带证明顺序不影响哈希）
                var mirror = new WorldState();
                mirror.Add(entities[1].Id, entities[1].Position, entities[1].Hp);
                mirror.Add(entities[0].Id, entities[0].Position, entities[0].Hp);
                mirror.SetTick(0);

                Check("§S4c2⭐：`InitialHash` == 服务端按同一份实体建出来的世界哈希（**能不能开始**的判据）",
                    start != null && start.InitialHash == WorldStateHash.Compute(mirror),
                    start == null
                        ? "没收到 start"
                        : "消息里 " + start.InitialHash + " vs 独立算 " + WorldStateHash.Compute(mirror));

                Check("§S4c2：开局消息里的实体按 `entity_id` **升序**（不依赖调用方给的顺序）",
                    start != null && start.Entities.Count == 2 &&
                    start.Entities[0].EntityId < start.Entities[1].EntityId,
                    start == null ? "没收到 start" : ("条数 " + start.Entities.Count));

                // ================================================================
                //  第 2 步：交输入 → 单调 + 广播数一致 + 缺输入 + 失败路径
                // ================================================================
                ClientSession mine = h.Sessions[h.Sessions.Count - 1];
                const int framesToRun = 6;
                int accepted = 0;

                for (int f = 0; f < framesToRun; f++)
                {
                    var msg = new ClientMessage
                    {
                        LockstepInput = new LockstepInput
                        {
                            Frame = f,
                            Seq = f,
                            MoveXRaw = Fix64.One.RawValue,   // 朝 +X 走
                            MoveYRaw = 0,
                            Buttons = 0,
                            TargetEntityId = 0,
                        }
                    };

                    // ⚠️ 阳性对照：合法输入**不回错误**（下面的四条失败路径都以此为基准）
                    if (h.Router.Dispatch(mine, msg).Reply == null)
                    {
                        accepted++;
                    }
                }

                Check("§S4c2：阳性对照 —— 合法的同一个请求形状是被**接受**的（没回错误）",
                    accepted == framesToRun, "被接受 " + accepted + " / " + framesToRun);

                // delay = 2 ⇒ tick 8 次刚好广播帧 0..5（`Tick` 现在是**单次**调用，不再 while）
                for (int t = 0; t < framesToRun + 2; t++)
                {
                    service.Tick();
                }

                h.PumpFor(300);

                var frames = new List<LockstepFrame>();

                for (int i = 0; i < a.Received.Count; i++)
                {
                    if (a.Received[i].LockstepFrame != null)
                    {
                        frames.Add(a.Received[i].LockstepFrame);
                    }
                }

                Check("§S4c2：阳性对照 —— 真的收到了帧（否则下面的单调/对账是空话）",
                    frames.Count > 0, "收到 " + frames.Count + " 帧");

                bool monotonic = true;

                for (int i = 1; i < frames.Count; i++)
                {
                    if (frames[i].Frame <= frames[i - 1].Frame)
                    {
                        monotonic = false;
                    }
                }

                Check("§S4c2⭐ 帧号**单调递增**", monotonic,
                    frames.Count == 0 ? "没有帧" : ("共 " + frames.Count + " 帧"));

                Check("§S4c2⭐ **广播帧数一致**：服务端说的帧数 == 客户端收到的帧数（没漏发/多发）",
                    service.BroadcastFrames == frames.Count,
                    "服务端 " + service.BroadcastFrames + " vs 客户端 " + frames.Count);

                int missingEntries = 0;

                for (int i = 0; i < frames.Count; i++)
                {
                    for (int k = 0; k < frames[i].Inputs.Count; k++)
                    {
                        if (frames[i].Inputs[k].PlayerId == entities[1].Id && frames[i].Inputs[k].Missing)
                        {
                            missingEntries++;
                        }
                    }
                }

                Check("§S4c2⭐ 缺输入：没人控制的玩家**每一帧都标 `Missing = true`**（缺人看得见）",
                    frames.Count > 0 && missingEntries == frames.Count,
                    "广播 " + frames.Count + " 帧，标了 Missing 的 " + missingEntries + " 条");

                // 独立复算同样 6 帧 ⇒ 终局哈希必须等于**最后一帧广播里的**
                var recompute = new WorldState();
                recompute.Add(entities[0].Id, entities[0].Position, entities[0].Hp);
                recompute.Add(entities[1].Id, entities[1].Position, entities[1].Hp);
                recompute.SetTick(0);

                // ⚠️ 复算的步数必须 == **服务端真的广播了几帧**（这里是 `frames.Count`，不是 `framesToRun`）：
                //    第一版我写死 `framesToRun`(6)，而服务端广播了 7 帧（第 7 帧连 alice 也缺输入）
                //    ⇒ 对账那条红，**红的是我的算术不是产品**。这条注释留着免得下次再犯。
                for (int f = 0; f < frames.Count; f++)
                {
                    // 前 `framesToRun` 帧 alice 朝 +X 走；之后她也没再交 ⇒ 默认输入（不动）
                    FixVector3 move = f < framesToRun ? new FixVector3(1, 0, 0) : FixVector3.Zero;

                    var stepInputs = new List<SimInput>
                    {
                        new SimInput { EntityId = entities[0].Id, MoveDirection = move, AttackTargetId = 0 },
                        new SimInput { EntityId = entities[1].Id, MoveDirection = FixVector3.Zero, AttackTargetId = 0 },
                    };

                    WorldStep.Step(recompute, stepInputs, 1);
                }

                ulong lastHash = frames.Count == 0 ? 0UL : frames[frames.Count - 1].ServerHash;

                Check("§S4c2⭐ 对账：最后一帧广播里的 `server_hash` == **独立复算**出来的世界哈希",
                    frames.Count > 0 && lastHash == WorldStateHash.Compute(recompute),
                    "广播 " + lastHash + " vs 独立算 " + WorldStateHash.Compute(recompute));

                // ---- 失败路径（**各自**断言，且都有上面的阳性对照）----
                DispatchResult dup = h.Router.Dispatch(mine, new ClientMessage
                {
                    LockstepInput = new LockstepInput { Frame = 0, Seq = 99, MoveXRaw = 0, MoveYRaw = 0 }
                });

                Check("§S4c2⭐ 失败①：**重复帧** ⇒ 回 `ErrorResponse` 且原因可读",
                    dup.Reply != null && dup.Reply.Error != null && dup.Reply.Error.Message.Contains("拒绝"),
                    dup.Reply == null || dup.Reply.Error == null
                        ? "没回错误（静默了）"
                        : ("code=" + dup.Reply.Error.Code + "；" + dup.Reply.Error.Message));

                DispatchResult negative = h.Router.Dispatch(mine, new ClientMessage
                {
                    LockstepInput = new LockstepInput { Frame = -3, Seq = 1, MoveXRaw = 0, MoveYRaw = 0 }
                });

                Check("§S4c2⭐ 失败②：**帧号为负** ⇒ 回 `ErrorResponse` 且**点名那个帧号**",
                    negative.Reply != null && negative.Reply.Error != null &&
                    negative.Reply.Error.Message.Contains("-3"),
                    negative.Reply == null || negative.Reply.Error == null
                        ? "没回错误（静默了）"
                        : negative.Reply.Error.Message);

                Client guest = h.ConnectAndHandshake("游客锁步");
                HandshakeAck guestAck = AckOf(guest, "游客握手");
                ClientSession guestSession = h.Sessions[h.Sessions.Count - 1];

                Check("§S4c2：阳性对照 —— 这个连接**确实是游客**（负 id）",
                    guestAck.PlayerId <= 0, "player_id=" + guestAck.PlayerId);

                DispatchResult asGuest = h.Router.Dispatch(guestSession, new ClientMessage
                {
                    LockstepInput = new LockstepInput { Frame = 0, Seq = 1, MoveXRaw = 0, MoveYRaw = 0 }
                });

                Check("§S4c2⭐ 失败③：**游客** ⇒ 回 `ErrorResponse`、用**身份**码、说明不能参与",
                    asGuest.Reply != null && asGuest.Reply.Error != null &&
                    asGuest.Reply.Error.Code == NBC.Shared.Net.NetErrors.PlayerMismatch &&
                    asGuest.Reply.Error.Message.Contains("游客"),
                    asGuest.Reply == null || asGuest.Reply.Error == null
                        ? "没回错误（静默了）"
                        : ("code=" + asGuest.Reply.Error.Code + "；" + asGuest.Reply.Error.Message));

                Check("§S4c2：拒绝计数如实（三条失败都记上了）",
                    service.RejectedInputs >= 3, "RejectedInputs=" + service.RejectedInputs);

                // ---- 失败④：**已登录但不在锁步房间**（用 `bob`；他登录了但没进房）----
                //  ⚠️ 上一版我怀疑"同账号二次登录会挂" ⇒ 负责人已确认那是**误判**
                //     （真因是 `while` 死循环；而"同账号二次登录"在 §十五 早已测过、不挂）。
                Client bob = h.ConnectAndLogin("bob", "pw-bob");
                HandshakeAck bobAck = AckOf(bob, "bob 登录");
                h.PumpFor(150);
                ClientSession bobSession = h.Sessions[h.Sessions.Count - 1];

                Check("§S4c2：阳性对照 —— bob **确实已登录**（正 id，不是游客）",
                    bobAck.PlayerId > 0, "player_id=" + bobAck.PlayerId);

                Check("§S4c2：阳性对照 —— bob **确实不在那个房间里**（否则这条测的不是我们要的分支）",
                    h.Registry.FindBySession(bobSession) == null,
                    "bob 竟然在房间里");

                DispatchResult noRoom = h.Router.Dispatch(bobSession, new ClientMessage
                {
                    LockstepInput = new LockstepInput { Frame = 0, Seq = 1, MoveXRaw = 0, MoveYRaw = 0 }
                });

                Check("§S4c2⭐ 失败④：**不在锁步房间** ⇒ 回 `ErrorResponse` 且原因点名「锁步房间」",
                    noRoom.Reply != null && noRoom.Reply.Error != null &&
                    noRoom.Reply.Error.Message.Contains("锁步房间"),
                    noRoom.Reply == null || noRoom.Reply.Error == null
                        ? "没回错误（静默了）"
                        : ("code=" + noRoom.Reply.Error.Code + "；" + noRoom.Reply.Error.Message));

                Check("§S4c2：拒绝计数如实（**四条**失败都记上了）",
                    service.RejectedInputs >= 4, "RejectedInputs=" + service.RejectedInputs);

                Check("§S4c2：**单飞闸一次都没挡**（主循环单线程 ⇒ 正常应恒为 0）",
                    service.GuardBlocked == 0, "GuardBlocked=" + service.GuardBlocked + "；" + service.Describe());

                // ================================================================
                //  开局实体表那个**纯函数**（`LockstepRoster.Build`，在共享层 ⇒ 能在这里验）
                // ================================================================
                var messy = new List<long> { 42, 7, 7, 0, -3, 19 };
                List<SimEntity> built = LockstepRoster.Build(messy);

                Check("§S4c3⭐ 实体表：**按 id 升序** + **非正数跳过** + **重号去重**（42,7,7,0,-3,19 ⇒ 7/19/42）",
                    built.Count == 3 && built[0].Id == 7 && built[1].Id == 19 && built[2].Id == 42,
                    "条数 " + built.Count +
                    (built.Count > 0 ? ("；首个 id=" + built[0].Id) : ""));

                Check("§S4c3：出生点按**排序后的序号**分配（第一个人永远在槽位 0）",
                    built.Count == 3 &&
                    built[0].Position.X.RawValue == SpawnLayout.PointAt(0).X.RawValue &&
                    built[0].Position.Z.RawValue == SpawnLayout.PointAt(0).Z.RawValue &&
                    built[1].Position.X.RawValue == SpawnLayout.PointAt(1).X.RawValue,
                    built.Count == 0 ? "空" : ("第 0 个 X=" + built[0].Position.X.ToDouble()));

                // ⭐ 顺序无关：同一批成员**反过来**给
                var forward = new List<long> { -3, 19, 7, 0, 42 };
                List<SimEntity> built2 = LockstepRoster.Build(forward);

                Check("§S4c3⭐ **顺序无关**：反过来给同一批成员 ⇒ 逐字段相同（id 与出生点都一致）",
                    built.Count == built2.Count && built.Count == 3 &&
                    built[0].Id == built2[0].Id && built[1].Id == built2[1].Id && built[2].Id == built2[2].Id &&
                    built[0].Position.X.RawValue == built2[0].Position.X.RawValue &&
                    built[2].Position.Z.RawValue == built2[2].Position.Z.RawValue,
                    "条数 " + built.Count + " vs " + built2.Count);

                Check("§S4c3：血量/上限/冷却都来自**共享常量**（两端同一个值 ⇒ 第 0 帧哈希才可能相同）",
                    built.Count == 3 && built[0].Hp == SpawnLayout.SpawnHp &&
                    built[0].MaxHp == SpawnLayout.SpawnHp && built[0].AttackReadyTick == 0,
                    built.Count == 0 ? "空" : ("hp=" + built[0].Hp + " max=" + built[0].MaxHp));
            }
        }

        /// <summary>把开局消息里的一个实体转成**普通数据**（这正是 `NetSession` 要做的转换）。</summary>
        /// <param name="raw">协议实体。</param>
        /// <returns>普通实体。</returns>
        private static SimEntity ToPlainEntity(LockstepStartEntity raw)
        {
            SimEntity e;
            e.Id = (int)raw.EntityId;
            e.Position = new FixVector3(
                Fix64.FromRaw(raw.PosXRaw), Fix64.FromRaw(raw.PosYRaw), Fix64.FromRaw(raw.PosZRaw));
            e.Hp = raw.Hp;
            e.MaxHp = raw.MaxHp;
            e.AttackReadyTick = raw.AttackReadyTick;
            return e;
        }

        /// <summary>把帧消息里的一条输入转成**普通数据**。</summary>
        /// <param name="raw">协议输入。</param>
        /// <returns>普通输入。</returns>
        private static FrameInput ToPlainInput(LockstepFrameInput raw)
        {
            FrameInput f;
            f.PlayerId = raw.PlayerId;
            f.MoveDirection = new FixVector3(Fix64.FromRaw(raw.MoveXRaw), Fix64.FromRaw(raw.MoveYRaw), Fix64.Zero);
            f.Buttons = raw.Buttons;
            f.TargetEntityId = raw.TargetEntityId;
            f.Missing = raw.Missing;
            return f;
        }

        /// <summary>
        /// 【二十五】客户端锁步运行器 + **三方对账**（S4-c 收尾的核心）。
        ///
        /// <para>⚠️ 核心是**阳性对照**：让其中一个客户端**漏应用一帧** ⇒ **它自己**必须报出分歧
        /// （帧号 + 两哈希），而**服务端与另一个客户端仍然一致**。
        /// 没有这条，"三方相等"可能只是哈希恒为常量（假绿）。</para>
        /// </summary>
        private static void LockstepClient_ThreeWayReconcile()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                var service = new LockstepService(h.Server, h.Registry);
                service.RegisterHandlers(h.Router);

                Client a = h.ConnectAndLogin("alice", "pw-alice");
                long aliceId = AckOf(a, "alice 登录").PlayerId;
                h.JoinSameRoom(a);

                Room room = null;

                foreach (Room r in h.Registry.Rooms)
                {
                    room = r;
                    break;
                }

                if (room == null)
                {
                    Check("§S4c4：阳性对照 —— 拿到房间", false, "房间表为空");
                    return;
                }

                var entities = new List<SimEntity>
                {
                    MakeEntity((int)aliceId, 0, 100),
                    MakeEntity((int)aliceId + 1, 1, 100),
                };

                service.StartRoom(room.RoomId, entities, 2);

                const int framesToRun = 6;
                ClientSession mine = h.Sessions[h.Sessions.Count - 1];

                for (int f = 0; f < framesToRun; f++)
                {
                    h.Router.Dispatch(mine, new ClientMessage
                    {
                        LockstepInput = new LockstepInput
                        {
                            Frame = f, Seq = f, MoveXRaw = Fix64.One.RawValue, MoveYRaw = 0,
                            Buttons = 0, TargetEntityId = 0,
                        }
                    });
                }

                for (int t = 0; t < framesToRun + 2; t++)
                {
                    service.Tick();     // ⚠️ **单次**（外面绝不套 while）
                }

                h.PumpFor(300);

                LockstepStart startMsg = FindLockstepStart(a);
                var framesMsg = new List<LockstepFrame>();

                for (int i = 0; i < a.Received.Count; i++)
                {
                    if (a.Received[i].LockstepFrame != null)
                    {
                        framesMsg.Add(a.Received[i].LockstepFrame);
                    }
                }

                Check("§S4c4：阳性对照 —— 拿到了开局 + 至少一帧（否则下面全是空话）",
                    startMsg != null && framesMsg.Count > 0,
                    "start=" + (startMsg != null) + "；帧数=" + framesMsg.Count);

                if (startMsg == null || framesMsg.Count == 0)
                {
                    return;
                }

                var plainStart = new List<SimEntity>();

                for (int i = 0; i < startMsg.Entities.Count; i++)
                {
                    plainStart.Add(ToPlainEntity(startMsg.Entities[i]));
                }

                ulong finalServerHash = framesMsg[framesMsg.Count - 1].ServerHash;

                // ---- ① 两个客户端都完整应用 ⇒ 三方一致 ----
                var clientA = new LockstepClient();
                var clientB = new LockstepClient();

                bool startedA = clientA.ApplyStart(startMsg.Frame, startMsg.InitialHash, plainStart);
                bool startedB = clientB.ApplyStart(startMsg.Frame, startMsg.InitialHash, plainStart);

                Check("§S4c4⭐ 开局：两个客户端都**开始成功**（`initialHash` 对得上）",
                    startedA && startedB, "A=" + startedA + " B=" + startedB);

                for (int i = 0; i < framesMsg.Count; i++)
                {
                    var plain = new List<FrameInput>();

                    for (int k = 0; k < framesMsg[i].Inputs.Count; k++)
                    {
                        plain.Add(ToPlainInput(framesMsg[i].Inputs[k]));
                    }

                    clientA.ApplyFrame(framesMsg[i].Frame, plain, framesMsg[i].ServerHash);
                    clientB.ApplyFrame(framesMsg[i].Frame, plain, framesMsg[i].ServerHash);
                }

                Check("§S4c4⭐ **三方一致**：完整应用后两个客户端都**零分歧**",
                    clientA.DesyncCount == 0 && clientB.DesyncCount == 0,
                    "A=" + clientA.DesyncCount + " B=" + clientB.DesyncCount + "；" + clientA.Describe());

                Check("§S4c4⭐：两个客户端的终局哈希 == 服务端最后一帧广播里的哈希",
                    clientA.World != null && clientB.World != null &&
                    WorldStateHash.Compute(clientA.World) == WorldStateHash.Compute(clientB.World) &&
                    WorldStateHash.Compute(clientA.World) == finalServerHash,
                    "A=" + (clientA.World == null ? 0UL : WorldStateHash.Compute(clientA.World)) +
                    " B=" + (clientB.World == null ? 0UL : WorldStateHash.Compute(clientB.World)) +
                    " 服务端=" + finalServerHash);

                Check("§S4c4：推进次数 == 收到的帧数（没多推/少推）",
                    clientA.StepsExecuted == framesMsg.Count && clientB.StepsExecuted == framesMsg.Count,
                    "A=" + clientA.StepsExecuted + " B=" + clientB.StepsExecuted + " 帧数=" + framesMsg.Count);

                // ---- ② ⭐ 阳性对照：其中一个客户端**漏应用一帧** ----
                var lame = new LockstepClient();
                var spared = new LockstepClient();

                Check("§S4c4：阳性对照 —— 两个客户端开局都成功（否则测的不是我们要的分支）",
                    lame.ApplyStart(startMsg.Frame, startMsg.InitialHash, plainStart) &&
                    spared.ApplyStart(startMsg.Frame, startMsg.InitialHash, plainStart),
                    "开局失败");

                for (int i = 0; i < framesMsg.Count; i++)
                {
                    var plain = new List<FrameInput>();

                    for (int k = 0; k < framesMsg[i].Inputs.Count; k++)
                    {
                        plain.Add(ToPlainInput(framesMsg[i].Inputs[k]));
                    }

                    spared.ApplyFrame(framesMsg[i].Frame, plain, framesMsg[i].ServerHash);

                    if (i == 2)
                    {
                        continue;       // ⚠️ 故意漏掉第 2 帧
                    }

                    lame.ApplyFrame(framesMsg[i].Frame, plain, framesMsg[i].ServerHash);
                }

                Check("§S4c4⭐ **阳性对照**：漏应用一帧的那个客户端**自己报出了分歧**",
                    lame.DesyncCount > 0, "DesyncCount=" + lame.DesyncCount);

                Check("§S4c4⭐：`LastDesync` 里**帧号 + 两个哈希**都可读（否则排查不动）",
                    lame.HasDesync && lame.LastDesync.Mine != lame.LastDesync.Server &&
                    lame.LastDesync.Frame >= 0 && !lame.LastDesync.AtStart,
                    lame.HasDesync ? lame.LastDesync.ToString() : "没有分歧记录");

                Check("§S4c4⭐：而**另一个客户端仍与服务端一致**（不许把两边带歪）",
                    spared.DesyncCount == 0 && spared.World != null &&
                    WorldStateHash.Compute(spared.World) == finalServerHash,
                    "spared 分歧 " + spared.DesyncCount +
                    "；终局 " + (spared.World == null ? 0UL : WorldStateHash.Compute(spared.World)) +
                    " vs 服务端 " + finalServerHash);

                // ---- ③ 开局哈希故意错 ⇒ **不许开始** ----
                var victim = new LockstepClient();
                bool badStart = victim.ApplyStart(startMsg.Frame, startMsg.InitialHash ^ 1UL, plainStart);

                Check("§S4c4⭐ 开局哈希不对 ⇒ **不许开始**（`Started` 保持 false）",
                    !badStart && !victim.Started, "返回 " + badStart + "；Started=" + victim.Started);

                Check("§S4c4⭐：而且报了一条**帧 0 的分歧**并计数",
                    victim.HasDesync && victim.DesyncCount == 1 && victim.LastDesync.AtStart &&
                    victim.LastDesync.Mine != victim.LastDesync.Server,
                    victim.HasDesync ? victim.LastDesync.ToString() : "没有分歧记录");

                var preStart = new List<FrameInput>();
                bool applied = victim.ApplyFrame(framesMsg[0].Frame, preStart, framesMsg[0].ServerHash);

                Check("§S4c4⭐ 未开局收到帧 ⇒ **忽略 + `IgnoredBeforeStart` 涨**（不拿空世界去算）",
                    !applied && victim.IgnoredBeforeStart == 1 && victim.StepsExecuted == 0,
                    "applied=" + applied + "；忽略 " + victim.IgnoredBeforeStart + "；Step " + victim.StepsExecuted);

                // ---- ④ 重复帧 / 倒退帧 ⇒ 不重复 Step、计数涨 ----
                var dup = new LockstepClient();
                dup.ApplyStart(startMsg.Frame, startMsg.InitialHash, plainStart);

                var firstPlain = new List<FrameInput>();

                for (int k = 0; k < framesMsg[0].Inputs.Count; k++)
                {
                    firstPlain.Add(ToPlainInput(framesMsg[0].Inputs[k]));
                }

                bool ok1 = dup.ApplyFrame(framesMsg[0].Frame, firstPlain, framesMsg[0].ServerHash);
                int afterFirst = dup.StepsExecuted;

                bool again = dup.ApplyFrame(framesMsg[0].Frame, firstPlain, framesMsg[0].ServerHash);
                bool backwards = dup.ApplyFrame(framesMsg[0].Frame, firstPlain, framesMsg[0].ServerHash);

                Check("§S4c4⭐：**重复帧 / 倒退帧** ⇒ 拒绝且**不重复 `Step`**、`RejectedFrames` 涨",
                    ok1 && dup.StepsExecuted == afterFirst && afterFirst == 1 &&
                    !again && !backwards && dup.RejectedFrames >= 2,
                    "Step 0→" + afterFirst + "→" + dup.StepsExecuted + "；拒 " + dup.RejectedFrames);

                bool restart = dup.ApplyStart(startMsg.Frame, startMsg.InitialHash, plainStart);

                Check("§S4c4⭐：**重复开局** ⇒ 拒绝、**不重建世界**（已推进的帧不被抹掉）",
                    !restart && dup.StepsExecuted == afterFirst && dup.World != null,
                    "返回 " + restart + "；Step " + dup.StepsExecuted);
            }
        }

        /// <summary>
        /// 假传输（**最小版**，照 `Tests\EditMode\Net\FakeTransport.cs` 的形状）。
        /// <para>⚠️ 它能驱动**真的 `NetSession`**，靠的是 `ITransport` 这个**公开契约** ——
        /// 而不是给产品加测试后门。这就是"有公开的输入入口"那一半。</para>
        /// </summary>
        private sealed class FakeNetTransport : ITransport
        {
            /// <summary>待交给会话的帧。</summary>
            private readonly Queue<byte[]> m_inbox = new Queue<byte[]>();

            /// <summary>会话发出去的帧（握手等；本用例不解析，只留着排查）。</summary>
            public readonly List<byte[]> SentFrames = new List<byte[]>();

            /// <summary>传输状态（照样本：初始 Disconnected）。</summary>
            private ETransportState m_state = ETransportState.Disconnected;

            /// <summary>发出去多少字节（`Stats` 要用）。</summary>
            private int m_bytesSent;

            /// <inheritdoc/>
            public string Description { get { return "探针假传输"; } }

            /// <inheritdoc/>
            public ETransportState State { get { return m_state; } }

            /// <inheritdoc/>
            public bool IsConnected { get { return m_state == ETransportState.Connected; } }

            /// <inheritdoc/>
            public TransportStats Stats
            {
                get { return new TransportStats(m_bytesSent, 0, SentFrames.Count, 0); }
            }

            /// <inheritdoc/>
            public event Action<byte[]> FrameReceived;

            /// <inheritdoc/>
            public event Action<string> Closed;

            /// <inheritdoc/>
            public void Connect(string host, int port)
            {
                m_state = ETransportState.Connected;
            }

            /// <inheritdoc/>
            public void Send(byte[] payload)
            {
                SentFrames.Add(payload);
                m_bytesSent += payload.Length;
            }

            /// <summary>把一条**服务端消息**塞进收件箱（`Pump` 时交给会话）。</summary>
            /// <param name="message">消息。</param>
            public void Push(ServerMessage message)
            {
                m_inbox.Enqueue(message.ToByteArray());
            }

            /// <inheritdoc/>
            public void Pump()
            {
                // ⚠️ 条件与样本一致：**连上了才出队**（会话可能在收到某条消息后自己断开）
                while (m_state == ETransportState.Connected && m_inbox.Count > 0)
                {
                    byte[] payload = m_inbox.Dequeue();
                    Action<byte[]> handler = FrameReceived;

                    if (handler != null)
                    {
                        handler(payload);
                    }
                }
            }

            /// <inheritdoc/>
            public void Close()
            {
                m_state = ETransportState.Closed;
            }

            /// <inheritdoc/>
            public void Dispose()
            {
            }
        }

        /// <summary>造一条握手通过的消息（字段照 `NetSessionTests` 的样本）。</summary>
        /// <param name="playerId">给你的玩家编号。</param>
        /// <returns>消息。</returns>
        private static ServerMessage MakeHandshakeAck(long playerId)
        {
            var ack = new HandshakeAck();
            ack.Accepted = true;
            ack.PlayerId = playerId;
            ack.ProtocolVersion = NBC.Shared.Net.NetContract.Version;
            ack.ServerVersion = "net-probe/lockstep";
            ack.TickHz = NBC.Shared.Net.NetContract.TickRate;
            ack.Reason = string.Empty;

            return new ServerMessage { HandshakeAck = ack };
        }

        /// <summary>
        /// 【二十六】用**假传输驱动真 `NetSession`**（S4-c 收尾的验收）。
        ///
        /// <para>⚠️ 顺序：先握手推成 Online，**业务消息才认**（否则 `NetSession` 直接忽略）。</para>
        /// <para>⚠️ 服务端那两条消息**不起真服务**：照 `LockstepService.StartRoom` 的做法手工造 ——
        /// 用 `LockstepRoster` + `WorldStep` + `WorldStateHash` 算出**真的** `initial_hash` 与逐帧 `server_hash`。</para>
        /// </summary>
        private static void NetSession_LockstepWireUp()
        {
            var fake = new FakeNetTransport();
            var session = new NetSession(fake, "锁步探针", "net-probe/lockstep");

            session.Connect("127.0.0.1", 7777);
            session.Pump(0);

            // ⚠️ **必须先握手**：`NetSession` 在 Online 之前不认业务消息
            fake.Push(MakeHandshakeAck(1));
            session.Pump(0);

            Check("§S4c5：阳性对照 —— 假传输把会话推成了 **Online**（否则下面全被忽略）",
                session.IsOnline, "状态=" + session.State);

            // ---- 手工造开局（照 `StartRoom` 的做法，用**同一份**共享逻辑）----
            var roster = new List<long> { 1, 2 };
            List<SimEntity> entities = LockstepRoster.Build(roster);

            Check("§S4c5：阳性对照 —— 阵容非空（否则建不出世界）",
                entities.Count == 2, "实体数=" + entities.Count);

            var serverWorld = new WorldState();

            for (int i = 0; i < entities.Count; i++)
            {
                serverWorld.Add(entities[i].Id, entities[i].Position, entities[i].Hp);
            }

            serverWorld.SetTick(0);

            var start = new LockstepStart();
            start.Frame = 0;
            start.InitialHash = WorldStateHash.Compute(serverWorld);

            for (int i = 0; i < entities.Count; i++)
            {
                var e = new LockstepStartEntity();
                e.EntityId = entities[i].Id;
                e.PosXRaw = entities[i].Position.X.RawValue;
                e.PosYRaw = entities[i].Position.Y.RawValue;
                e.PosZRaw = entities[i].Position.Z.RawValue;
                e.Hp = entities[i].Hp;
                e.MaxHp = entities[i].MaxHp;
                e.AttackReadyTick = entities[i].AttackReadyTick;
                start.Entities.Add(e);
            }

            fake.Push(new ServerMessage { LockstepStart = start });
            session.Pump(0);

            const int frames = 16;
            var serverFrames = new List<LockstepFrame>();

            for (int f = 0; f < frames; f++)
            {
                // 服务端推进一帧（**同一批输入**），算出这一帧的 `server_hash`
                var stepInputs = new List<SimInput>
                {
                    new SimInput { EntityId = 1, MoveDirection = new FixVector3(1, 0, 0), AttackTargetId = 0 },
                    new SimInput { EntityId = 2, MoveDirection = FixVector3.Zero, AttackTargetId = 0 },
                };

                WorldStep.Step(serverWorld, stepInputs, 1);

                var frame = new LockstepFrame();
                frame.Frame = f;
                frame.ServerHash = WorldStateHash.Compute(serverWorld);

                var in1 = new LockstepFrameInput();
                in1.PlayerId = 1;
                in1.MoveXRaw = Fix64.One.RawValue;
                in1.MoveYRaw = 0;
                in1.Buttons = 7;
                in1.TargetEntityId = 2;
                in1.Missing = false;
                frame.Inputs.Add(in1);

                var in2 = new LockstepFrameInput();
                in2.PlayerId = 2;
                in2.MoveXRaw = 0;
                in2.MoveYRaw = 0;
                in2.Buttons = 0;
                in2.TargetEntityId = 0;
                in2.Missing = true;         // ⚠️ 缺输入：转换必须把它带过来
                frame.Inputs.Add(in2);

                serverFrames.Add(frame);
            }

            // ---- ① ⭐ 好的一条：喂完 16 帧 ⇒ 零分歧，而且**确实收到了** ----
            for (int i = 0; i < serverFrames.Count; i++)
            {
                fake.Push(new ServerMessage { LockstepFrame = serverFrames[i] });
                session.Pump(0);
            }

            Check("§S4c5⭐ **阳性对照：好的一条不许自己报分歧**", session.DesyncCount == 0,
                "DesyncCount=" + session.DesyncCount + "；" +
                (session.Lockstep == null ? "没有运行器" : session.Lockstep.Describe()));

            // ⚠️ 光断 `DesyncCount == 0` 不够：**压根没接收也会绿** ⇒ 必须有"确实收到了"的正面证据
            Check("§S4c5⭐：而且**真的建了世界、逐帧推进**（正面证据，防「没接收也算绿」）",
                session.Lockstep != null && session.Lockstep.Started &&
                session.Lockstep.StepsExecuted == frames &&
                session.Lockstep.LastSteppedFrame == frames - 1,
                session.Lockstep == null
                    ? "运行器没建起来"
                    : ("Started=" + session.Lockstep.Started + "；推进 " + session.Lockstep.StepsExecuted +
                       "；末帧 " + session.Lockstep.LastSteppedFrame));

            Check("§S4c5：客户端的终局哈希 == 服务端最后那帧的哈希",
                session.Lockstep != null && session.Lockstep.World != null &&
                WorldStateHash.Compute(session.Lockstep.World) == serverFrames[frames - 1].ServerHash,
                session.Lockstep == null || session.Lockstep.World == null
                    ? "没有世界"
                    : (WorldStateHash.Compute(session.Lockstep.World) + " vs " + serverFrames[frames - 1].ServerHash));

            // ---- ② ⭐ 改一位 server_hash ⇒ 分歧必须被抓到，且**恰好一次** ----
            LockstepFrame tampered = serverFrames[frames - 1].Clone();
            tampered.Frame = frames;                 // 新的一帧（否则会被"重复帧"挡掉，测不到分歧）
            tampered.ServerHash = tampered.ServerHash ^ 1UL;

            WorldStep.Step(serverWorld, new List<SimInput>
            {
                new SimInput { EntityId = 1, MoveDirection = new FixVector3(1, 0, 0), AttackTargetId = 0 },
            }, 1);

            ulong expectedMine = WorldStateHash.Compute(serverWorld);

            int events = 0;
            LockstepDesync captured = default(LockstepDesync);
            session.DesyncDetected += d => { events++; captured = d; };

            fake.Push(new ServerMessage { LockstepFrame = tampered });
            session.Pump(0);

            Check("§S4c5⭐ **改一位 `server_hash`** ⇒ `DesyncCount == 1`",
                session.DesyncCount == 1, "DesyncCount=" + session.DesyncCount);

            Check("§S4c5⭐：`DesyncDetected` **恰好触发一次**", events == 1, "次数=" + events);

            Check("§S4c5⭐：事件里**帧号 + 我的哈希 + 服务端哈希**都可读且与预期一致",
                events == 1 && captured.Frame == frames && !captured.AtStart &&
                captured.Mine == expectedMine && captured.Server == tampered.ServerHash,
                events != 1
                    ? "事件没触发"
                    : ("帧 " + captured.Frame + "：我 " + captured.Mine + "（期望 " + expectedMine +
                       "）vs 服务端 " + captured.Server + "（期望 " + tampered.ServerHash + "）"));

            // ---- ③ 转换**逐字段**断言（别只断"没抛异常"）----
            SimEntity plainEntity = LockstepWire.ToPlainEntity(start.Entities[0]);

            Check("§S4c5：`ToPlainEntity` **逐字段**（id / 位置三 raw / hp / max_hp / attack_ready_tick）",
                plainEntity.Id == (int)start.Entities[0].EntityId &&
                plainEntity.Position.X.RawValue == start.Entities[0].PosXRaw &&
                plainEntity.Position.Y.RawValue == start.Entities[0].PosYRaw &&
                plainEntity.Position.Z.RawValue == start.Entities[0].PosZRaw &&
                plainEntity.Hp == start.Entities[0].Hp &&
                plainEntity.MaxHp == start.Entities[0].MaxHp &&
                plainEntity.AttackReadyTick == start.Entities[0].AttackReadyTick,
                "id=" + plainEntity.Id + " X=" + plainEntity.Position.X.RawValue +
                " hp=" + plainEntity.Hp + " max=" + plainEntity.MaxHp + " cd=" + plainEntity.AttackReadyTick);

            FrameInput plainInput = LockstepWire.ToPlainInput(serverFrames[0].Inputs[0]);

            Check("§S4c5：`ToPlainInput` **逐字段**（player_id / 位置两 raw / buttons / target / **Missing**）",
                plainInput.PlayerId == 1 &&
                plainInput.MoveDirection.X.RawValue == Fix64.One.RawValue &&
                plainInput.MoveDirection.Y.RawValue == 0 &&
                plainInput.Buttons == 7 &&
                plainInput.TargetEntityId == 2 &&
                !plainInput.Missing,
                "player=" + plainInput.PlayerId + " X=" + plainInput.MoveDirection.X.RawValue +
                " buttons=" + plainInput.Buttons + " target=" + plainInput.TargetEntityId +
                " missing=" + plainInput.Missing);

            FrameInput plainMissing = LockstepWire.ToPlainInput(serverFrames[0].Inputs[1]);

            Check("§S4c5⭐：`Missing` **真的被带过来了**（最容易在转换里丢的一条）",
                plainMissing.PlayerId == 2 && plainMissing.Missing,
                "player=" + plainMissing.PlayerId + "；Missing=" + plainMissing.Missing);

            session.Dispose();
        }

        /// <summary>探针用的「实体 → 队号」查询（照 `EntityTeam` 接口实现）。</summary>
        private sealed class ProbeTeam : EntityTeam
        {
            /// <summary>映射。</summary>
            private readonly Dictionary<int, int> m_map = new Dictionary<int, int>();

            /// <summary>设一条。</summary>
            /// <param name="entityId">实体编号。</param>
            /// <param name="team">队号。</param>
            public void Set(int entityId, int team)
            {
                m_map[entityId] = team;
            }

            /// <inheritdoc/>
            public int TeamOf(int entityId)
            {
                int team;
                return m_map.TryGetValue(entityId, out team) ? team : -1;
            }
        }

        /// <summary>
        /// 【二十七】2v2 的**队伍与胜负**（S4-d 第一步：共享层纯函数，直驱）。
        ///
        /// <para>⚠️ 每一条都要能「因为错而红」：第 ④ 条是**阳性对照**（别让「任何世界都判赢」也能过）。</para>
        /// </summary>
        private static void MatchRules_TeamsAndVerdict()
        {
            // ---- ① 队伍分配：**顺序无关**（含重复号、含 0/负数）----
            List<long> orderedA;
            List<long> orderedB;

            List<int> teamA = LockstepTeams.BuildTeamIndex(new List<long> { 4, 1, 3, 2 }, out orderedA);
            List<int> teamB = LockstepTeams.BuildTeamIndex(new List<long> { 2, 4, 3, 1, 1, 0, -5 }, out orderedB);

            Check("§S4d：阳性对照 —— 名单非空（否则下面的「顺序无关」是空话）",
                orderedA.Count == 4, "规范化后有 " + orderedA.Count + " 个成员");

            bool sameOrder = orderedA.Count == orderedB.Count;
            bool sameTeam = sameOrder && teamA.Count == teamB.Count;

            if (sameOrder)
            {
                for (int i = 0; i < orderedA.Count; i++)
                {
                    if (orderedA[i] != orderedB[i])
                    {
                        sameOrder = false;
                    }

                    if (teamA[i] != teamB[i])
                    {
                        sameTeam = false;
                    }
                }
            }

            Check("§S4d⭐ **队伍分配顺序无关**：打乱（且带重复号/0/负数）⇒ 规范化名单逐项相同",
                sameOrder, "A=" + string.Join(",", orderedA.ConvertAll(x => x.ToString()).ToArray()) +
                " B=" + string.Join(",", orderedB.ConvertAll(x => x.ToString()).ToArray()));

            Check("§S4d⭐：而且**队号也逐项相同**（1,2 → A；3,4 → B，按 id 升序交替）",
                sameTeam && teamA.Count == 4 && teamA[0] == 0 && teamA[1] == 1 && teamA[2] == 0 && teamA[3] == 1,
                "队号=" + string.Join(",", teamA.ConvertAll(x => x.ToString()).ToArray()));

            // ---- ② 按击杀判胜（含「同 tick 两队都达到」这个最容易写错的分支）----
            Check("§S4d⭐ A 队击杀达标 ⇒ **A 胜**",
                MatchRules.Decide(MatchRules.KillsToWin, 0, 0) == EMatchOutcome.TeamA,
                "判定=" + MatchRules.Decide(MatchRules.KillsToWin, 0, 0));

            Check("§S4d⭐ B 队击杀达标 ⇒ **B 胜**",
                MatchRules.Decide(0, MatchRules.KillsToWin, 0) == EMatchOutcome.TeamB,
                "判定=" + MatchRules.Decide(0, MatchRules.KillsToWin, 0));

            Check("§S4d⭐ **同 tick 两队都达标、击杀数相等 ⇒ 平**（最易写错的分支）",
                MatchRules.Decide(MatchRules.KillsToWin, MatchRules.KillsToWin, 0) == EMatchOutcome.Draw,
                "判定=" + MatchRules.Decide(MatchRules.KillsToWin, MatchRules.KillsToWin, 0));

            Check("§S4d⭐ **同 tick 两队都达标、A 多一个 ⇒ A 胜**",
                MatchRules.Decide(MatchRules.KillsToWin + 1, MatchRules.KillsToWin, 0) == EMatchOutcome.TeamA,
                "判定=" + MatchRules.Decide(MatchRules.KillsToWin + 1, MatchRules.KillsToWin, 0));

            // ---- ③ 按限时判胜 ----
            Check("§S4d⭐ **跑满限时**、A 击杀多 ⇒ A 胜",
                MatchRules.Decide(3, 1, MatchRules.MaxTicks) == EMatchOutcome.TeamA,
                "判定=" + MatchRules.Decide(3, 1, MatchRules.MaxTicks));

            Check("§S4d⭐ **跑满限时**、击杀相等 ⇒ 平",
                MatchRules.Decide(2, 2, MatchRules.MaxTicks) == EMatchOutcome.Draw,
                "判定=" + MatchRules.Decide(2, 2, MatchRules.MaxTicks));

            // ---- ④ 未结束（**阳性对照**）----
            Check("§S4d⭐ **阳性对照**：谁都没达标、也没到限时 ⇒ **未结束**（别让「任何世界都判赢」也能过）",
                MatchRules.Decide(0, 0, 0) == EMatchOutcome.Undecided &&
                MatchRules.Decide(MatchRules.KillsToWin - 1, MatchRules.KillsToWin - 1, MatchRules.MaxTicks - 1)
                    == EMatchOutcome.Undecided,
                "判定=" + MatchRules.Decide(0, 0, 0) + " / " +
                MatchRules.Decide(MatchRules.KillsToWin - 1, MatchRules.KillsToWin - 1, MatchRules.MaxTicks - 1));

            // ---- ⑤ CountKills 与**登记顺序**无关 ----
            var team = new ProbeTeam();
            team.Set(1, 0);
            team.Set(2, 0);
            team.Set(3, 1);
            team.Set(4, 1);

            var worldA = new WorldState();
            worldA.Add(1, new FixVector3(0, 0, 0), 0);      // 死（A 队）⇒ B 得一分
            worldA.Add(2, new FixVector3(1, 0, 0), 100);    // 活
            worldA.Add(3, new FixVector3(2, 0, 0), 0);      // 死（B 队）⇒ A 得一分
            worldA.Add(4, new FixVector3(3, 0, 0), 0);      // 死（B 队）⇒ A 再得一分

            var worldB = new WorldState();
            worldB.Add(4, new FixVector3(3, 0, 0), 0);      // **登记顺序打乱**
            worldB.Add(3, new FixVector3(2, 0, 0), 0);
            worldB.Add(2, new FixVector3(1, 0, 0), 100);
            worldB.Add(1, new FixVector3(0, 0, 0), 0);

            int killsA1;
            int killsB1;
            int killsA2;
            int killsB2;

            MatchRules.CountKills(worldA, team, out killsA1, out killsB1);
            MatchRules.CountKills(worldB, team, out killsA2, out killsB2);

            Check("§S4d：阳性对照 —— 击杀数不是全 0（否则下面的「顺序无关」是空话）",
                killsA1 > 0 && killsB1 > 0, "A=" + killsA1 + " B=" + killsB1);

            Check("§S4d⭐ `CountKills` **与登记顺序无关**（走 `SnapshotSorted`）：2/1 与打乱后相同",
                killsA1 == killsA2 && killsB1 == killsB2 && killsA1 == 2 && killsB1 == 1,
                "顺序 A=" + killsA1 + "/" + killsB1 + "；打乱 " + killsA2 + "/" + killsB2);
        }

        /// <summary>
        /// 【二十八】房间相位（S4-d 第 3 件）：**满员 ⇒ `Running`**，且「离开一人」**不回退**。
        ///
        /// <para>⚠️ 直驱 `Room.Add` / `Room.Remove` —— 相位状态机就设**在那一层**（状态变化处），
        /// 所以这里验的就是真代码，不是 Host 循环里的复制品。</para>
        /// </summary>
        private static void RoomPhase_FullBecomesRunning()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                // 拿 4 个会话（用游客即可：`Room.Add` 不关心玩家编号）
                for (int i = 0; i < 4; i++)
                {
                    h.ConnectAndHandshake("相位" + i);
                }

                Check("§S4d2：阳性对照 —— 备齐了 4 个会话（否则下面测不了满员）",
                    h.Sessions.Count >= 4, "会话数=" + h.Sessions.Count);

                if (h.Sessions.Count < 4)
                {
                    return;
                }

                int n = h.Sessions.Count;
                var room = new Room("phase-probe", 0, 4);

                Check("§S4d2：新房间初始相位是 `Waiting`（阳性对照：别一建就是 Running）",
                    room.Phase == RoomPhase.Waiting, "相位=" + room.Phase);

                // 前 3 个人进来 ⇒ **还不该** Running
                room.Add(h.Sessions[n - 4], "p0");
                room.Add(h.Sessions[n - 3], "p1");
                room.Add(h.Sessions[n - 2], "p2");

                Check("§S4d2：3/4 人时相位**仍是 `Waiting`**（没满就不许开）",
                    room.Phase == RoomPhase.Waiting && !room.IsFull,
                    "相位=" + room.Phase + "；满=" + room.IsFull + "；席位=" + room.SeatCount);

                // 第 4 个人进来 ⇒ **满员 ⇒ Running**
                room.Add(h.Sessions[n - 1], "p3");

                Check("§S4d2⭐ **满员（4/4）⇒ 相位变 `Running`**（这一刀的核心）",
                    room.Phase == RoomPhase.Running && room.IsFull,
                    "相位=" + room.Phase + "；满=" + room.IsFull + "；席位=" + room.SeatCount);

                // ⚠️ 「离开一人」的语义（**我定的**，见 §34.2）：Running **黏性**，不回退
                bool removed = room.Remove(h.Sessions[n - 4]);

                Check("§S4d2⭐ **离开一人 ⇒ 相位仍是 `Running`**（黏性：相位说的是「这局开没开」，不是「现在满不满」）",
                    removed && room.Phase == RoomPhase.Running && !room.IsFull,
                    "移出=" + removed + "；相位=" + room.Phase + "；满=" + room.IsFull + "；席位=" + room.SeatCount);

                // 再有人进来 ⇒ 已经 Running 了，**不许**因为"又满了一次"而反复变
                room.Add(h.Sessions[n - 4], "p0-again");

                Check("§S4d2：重新满员 ⇒ 相位**还是 `Running`**（不会反复变）",
                    room.Phase == RoomPhase.Running && room.IsFull,
                    "相位=" + room.Phase + "；席位=" + room.SeatCount);
            }
        }

        /// <summary>找客户端收到的最后一条 `match_result`。</summary>
        /// <param name="client">客户端。</param>
        /// <returns>结果或 null。</returns>
        private static MatchResult FindMatchResult(Client client)
        {
            for (int i = client.Received.Count - 1; i >= 0; i--)
            {
                if (client.Received[i].MatchResult != null)
                {
                    return client.Received[i].MatchResult;
                }
            }

            return null;
        }

        /// <summary>数客户端一共收到几条 `match_result`（「只宣布一次」要数它）。</summary>
        /// <param name="client">客户端。</param>
        /// <returns>条数。</returns>
        private static int CountMatchResults(Client client)
        {
            int n = 0;

            for (int i = 0; i < client.Received.Count; i++)
            {
                if (client.Received[i].MatchResult != null)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// 【二十九】服务端宣布胜负 + 客户端复算比对（S4-d 收尾）。
        ///
        /// <para>⭐ 关键便利：`StartRoom` 的实体表**是调用方造的** ⇒ 把 **B 队两人的 `Hp` 直接设成 0**，
        /// 一开局 `CountKills` 就数出 `killsA = 2 == KillsToWin` ⇒ **完全不碰攻击冷却**
        /// （否则要摸清 `AttackReadyTick` 去凑 4×N 下，脆且慢）。</para>
        /// </summary>
        private static void MatchResult_AnnounceAndRecheck()
        {
            // ⭐ W25：规则阈值**必须在真实参数下可达**（守的是「规则①活着」，不是数值好看）
            Check("§S4d3⭐ **可达性断言**：`KillsToWin <= TeamSize`（否则规则①在 2v2 里是死分支 —— 第一版我写了 5）",
                MatchRules.KillsToWin <= LockstepTeams.TeamSize,
                "KillsToWin=" + MatchRules.KillsToWin + " TeamSize=" + LockstepTeams.TeamSize);

            // ================= 场景 A：**未结束** ⇒ 不广播（阳性对照）=================
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                var service = new LockstepService(h.Server, h.Registry);
                service.RegisterHandlers(h.Router);

                Client a = h.ConnectAndLogin("alice", "pw-alice");
                long id0 = AckOf(a, "alice 登录").PlayerId;
                h.JoinSameRoom(a);

                Room room = null;

                foreach (Room r in h.Registry.Rooms)
                {
                    room = r;
                    break;
                }

                if (room == null)
                {
                    Check("§S4d3：阳性对照 —— 拿到房间", false, "房间表为空");
                    return;
                }

                // 全员满血（没人死）⇒ 击杀 0/0 ⇒ **未结束**
                var alive = new List<SimEntity>
                {
                    MakeEntity((int)id0, 0, 100),
                    MakeEntity((int)id0 + 1, 5, 100),
                    MakeEntity((int)id0 + 2, -5, 100),
                    MakeEntity((int)id0 + 3, 0, 100),
                };

                service.StartRoom(room.RoomId, alive, 2);

                for (int t = 0; t < 8; t++)
                {
                    service.Tick();
                }

                h.PumpFor(300);

                Check("§S4d3⭐ **阳性对照：未结束 ⇒ 不广播结果**（别让「任何世界都判赢」也能过）",
                    service.MatchesAnnounced == 0 && CountMatchResults(a) == 0,
                    "宣布=" + service.MatchesAnnounced + "；收到结果=" + CountMatchResults(a) +
                    "；广播帧=" + service.BroadcastFrames);

                Check("§S4d3：阳性对照 —— 但**帧确实在广播**（证明上面不是「什么都没跑」）",
                    service.BroadcastFrames > 0 && CountMatchResults(a) == 0,
                    "广播帧=" + service.BroadcastFrames);
            }

            // ================= 场景 B：**已达标** ⇒ 宣布一次 + 停止推进 + 客户端复算 =================
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                var service = new LockstepService(h.Server, h.Registry);
                service.RegisterHandlers(h.Router);

                Client a = h.ConnectAndLogin("alice", "pw-alice");
                long id0 = AckOf(a, "alice 登录").PlayerId;
                h.JoinSameRoom(a);

                Room room = null;

                foreach (Room r in h.Registry.Rooms)
                {
                    room = r;
                    break;
                }

                if (room == null)
                {
                    Check("§S4d3：阳性对照 —— 场景 B 拿到房间", false, "房间表为空");
                    return;
                }

                // 按 id 升序 ⇒ 队号 = 序号 % 2：第 0、2 个是 A 队，第 1、3 个是 B 队。
                // ⚠️ **把 B 队两人 Hp 设成 0** ⇒ 一开局 A 队击杀就有 2 个 == `KillsToWin`
                var rosterIds = new List<long> { id0, id0 + 1, id0 + 2, id0 + 3 };

                var decided = new List<SimEntity>
                {
                    MakeEntity((int)id0, 0, 100),
                    MakeEntity((int)id0 + 1, 5, 0),      // B 队（序号 1）—— 已经死了
                    MakeEntity((int)id0 + 2, -5, 100),
                    MakeEntity((int)id0 + 3, 0, 0),      // B 队（序号 3）—— 已经死了
                };

                service.StartRoom(room.RoomId, decided, 2);

                // 跑够帧数（delay=2 ⇒ 第 3 次 tick 会广播帧 0 ⇒ 那一刻判胜负）
                for (int t = 0; t < 6; t++)
                {
                    service.Tick();
                }

                h.PumpFor(300);

                MatchResult result = FindMatchResult(a);

                Check("§S4d3⭐ **服务端广播了结果**（阳性对照：真的收到了那条消息）",
                    result != null, "收到结果条数=" + CountMatchResults(a));

                Check("§S4d3⭐：结果里的 `kills_a/kills_b` 与**独立数出来**的一致",
                    result != null && result.KillsA == 2 && result.KillsB == 0 &&
                    result.Outcome == MatchRules.ToNumber(EMatchOutcome.TeamA),
                    result == null
                        ? "没收到"
                        : ("kills=" + result.KillsA + "/" + result.KillsB + " outcome=" + result.Outcome));

                Check("§S4d3⭐ **只宣布一次**：`MatchesAnnounced == 1` 且客户端只收到 1 条",
                    service.MatchesAnnounced == 1 && CountMatchResults(a) == 1,
                    "宣布=" + service.MatchesAnnounced + "；收到=" + CountMatchResults(a) + "；" + service.Describe());

                // ⭐ **宣布后不再推进**：再 tick 一批，广播帧数不许再涨
                long framesAtAnnounce = service.BroadcastFrames;

                for (int t = 0; t < 10; t++)
                {
                    service.Tick();
                }

                h.PumpFor(200);

                Check("§S4d3⭐ **宣布后停止推进**：再 tick 10 次，广播帧数**不再涨**",
                    service.BroadcastFrames == framesAtAnnounce && CountMatchResults(a) == 1,
                    "宣布时 " + framesAtAnnounce + " ⇒ 现在 " + service.BroadcastFrames +
                    "；结果条数=" + CountMatchResults(a));

                // ---- 客户端：收到同一批消息 ⇒ 自己复算 ----
                LockstepStart startMsg = FindLockstepStart(a);
                var framesMsg = new List<LockstepFrame>();

                for (int i = 0; i < a.Received.Count; i++)
                {
                    if (a.Received[i].LockstepFrame != null)
                    {
                        framesMsg.Add(a.Received[i].LockstepFrame);
                    }
                }

                Check("§S4d3：阳性对照 —— 客户端侧拿到了开局 + 至少一帧（否则复算无从谈起）",
                    startMsg != null && framesMsg.Count > 0,
                    "start=" + (startMsg != null) + "；帧数=" + framesMsg.Count);

                if (startMsg == null || framesMsg.Count == 0 || result == null)
                {
                    return;
                }

                var client = new LockstepClient();

                // ⚠️ 队伍映射用**同一个** `SortedTeamMap`（与客户端将来从 `RoomState` 成员名单推的是同一个函数）
                client.SetTeams(new SortedTeamMap(rosterIds));
                client.ApplyStart(startMsg.Frame, startMsg.InitialHash, LockstepWire.ToPlainEntities(startMsg));

                for (int i = 0; i < framesMsg.Count; i++)
                {
                    client.ApplyFrame(framesMsg[i].Frame, LockstepWire.ToPlainInputs(framesMsg[i]), framesMsg[i].ServerHash);
                }

                bool agreed = client.ApplyMatchResult(
                    (int)result.Frame, (int)result.Outcome, (int)result.KillsA, (int)result.KillsB);

                Check("§S4d3⭐ 客户端复算**一致** ⇒ 落定结果、零不一致",
                    agreed && client.MatchMismatchCount == 0 && client.Outcome == EMatchOutcome.TeamA,
                    "一致=" + agreed + "；不一致=" + client.MatchMismatchCount +
                    "；结果=" + (client.Outcome == null ? "未落定" : MatchRules.Describe(client.Outcome.Value)));

                // ⭐ 改一位 outcome ⇒ 必须报不一致（事件一次 + 计数涨 + 三样可读）
                var victim = new LockstepClient();
                victim.SetTeams(new SortedTeamMap(rosterIds));
                victim.ApplyStart(startMsg.Frame, startMsg.InitialHash, LockstepWire.ToPlainEntities(startMsg));

                for (int i = 0; i < framesMsg.Count; i++)
                {
                    victim.ApplyFrame(framesMsg[i].Frame, LockstepWire.ToPlainInputs(framesMsg[i]), framesMsg[i].ServerHash);
                }

                int events = 0;
                LockstepMatchMismatch captured = default(LockstepMatchMismatch);
                victim.MatchMismatchDetected += m => { events++; captured = m; };

                int wrongOutcome = MatchRules.ToNumber(EMatchOutcome.TeamB);
                bool tamperedAgreed = victim.ApplyMatchResult(
                    (int)result.Frame, wrongOutcome, (int)result.KillsA, (int)result.KillsB);

                Check("§S4d3⭐ **改一位结果（A 说成 B）⇒ 客户端必须报不一致**",
                    !tamperedAgreed && victim.MatchMismatchCount == 1 && events == 1,
                    "一致=" + tamperedAgreed + "；不一致=" + victim.MatchMismatchCount + "；事件=" + events);

                Check("§S4d3⭐：而且**三样都可读**（我的判定 / 服务端判定 / 两队击杀）",
                    events == 1 && captured.Mine == EMatchOutcome.TeamA &&
                    captured.Server == EMatchOutcome.TeamB &&
                    captured.MyKillsA == 2 && captured.ServerKillsA == result.KillsA,
                    events == 1 ? captured.ToString() : "事件没触发");
            }
        }

        /// <summary>
        /// 【三十】**真实 socket 路径**上的胜负：假 `ITransport` 驱真 `NetSession`
        /// （`room_state` → `lockstep_start` → `lockstep_frame` → `match_result`）。
        ///
        /// <para>⚠️ 这一节补的正是"直驱 `LockstepClient` 验不到"的那块：**接线**（分发分支 + 转换 + 转发 + 队伍映射）。</para>
        /// </summary>
        private static void NetSession_MatchResultPath()
        {
            var fake = new FakeNetTransport();
            var session = new NetSession(fake, "锁步探针", "net-probe/lockstep");

            session.Connect("127.0.0.1", 7777);
            session.Pump(0);
            fake.Push(MakeHandshakeAck(11));
            session.Pump(0);

            Check("§S4d4：阳性对照 —— 会话已 Online（否则下面的业务消息全被忽略）",
                session.IsOnline, "状态=" + session.State);

            const int baseId = 100;
            var rosterIds = new List<long> { baseId, baseId + 1, baseId + 2, baseId + 3 };

            // ---- ① 先喂 `room_state`（成员名单）⇒ 会话据此推队伍映射（**不加协议字段**）----
            var roomState = new RoomState();
            roomState.RoomId = "probe-room";
            roomState.DungeonId = 0;
            roomState.Capacity = 4;

            for (int i = 0; i < rosterIds.Count; i++)
            {
                var m = new RoomMember();
                m.PlayerId = rosterIds[i];
                m.PlayerName = "p" + i;
                m.IsHost = i == 0;
                m.Ready = true;
                roomState.Members.Add(m);
            }

            fake.Push(new ServerMessage { RoomState = roomState });
            session.Pump(0);

            Check("§S4d4：阳性对照 —— 会话确实拿到了房间与成员名单（拿不到就推不出队伍）",
                session.CurrentRoom != null && session.CurrentRoom.Members.Count == 4,
                session.CurrentRoom == null ? "没有房间" : ("成员=" + session.CurrentRoom.Members.Count));

            // ---- ② 造开局（B 队两人 Hp = 0 ⇒ 一开局 A 队击杀就有 2 == KillsToWin）----
            var entities = new List<SimEntity>
            {
                MakeEntity(baseId, 0, 100),          // 序号 0 → A 队
                MakeEntity(baseId + 1, 5, 0),        // 序号 1 → B 队（已死）
                MakeEntity(baseId + 2, -5, 100),     // 序号 2 → A 队
                MakeEntity(baseId + 3, 0, 0),        // 序号 3 → B 队（已死）
            };

            var serverWorld = new WorldState();

            for (int i = 0; i < entities.Count; i++)
            {
                serverWorld.Add(entities[i].Id, entities[i].Position, entities[i].Hp);
                int idx = serverWorld.IndexOf(entities[i].Id);
                SimEntity fix = serverWorld.GetAt(idx);
                fix.MaxHp = entities[i].MaxHp;
                fix.AttackReadyTick = entities[i].AttackReadyTick;
                serverWorld.SetAt(idx, fix);
            }

            serverWorld.SetTick(0);

            var startMsg = new LockstepStart();
            startMsg.Frame = 0;
            startMsg.InitialHash = WorldStateHash.Compute(serverWorld);

            for (int i = 0; i < entities.Count; i++)
            {
                var e = new LockstepStartEntity();
                e.EntityId = entities[i].Id;
                e.PosXRaw = entities[i].Position.X.RawValue;
                e.PosYRaw = entities[i].Position.Y.RawValue;
                e.PosZRaw = entities[i].Position.Z.RawValue;
                e.Hp = entities[i].Hp;
                e.MaxHp = entities[i].MaxHp;
                e.AttackReadyTick = entities[i].AttackReadyTick;
                startMsg.Entities.Add(e);
            }

            fake.Push(new ServerMessage { LockstepStart = startMsg });
            session.Pump(0);

            // ---- ③ 喂几帧（每帧推进服务端那份世界并算 server_hash）----
            const int frames = 4;

            for (int f = 0; f < frames; f++)
            {
                WorldStep.Step(serverWorld, new List<SimInput>
                {
                    new SimInput { EntityId = baseId, MoveDirection = new FixVector3(1, 0, 0), AttackTargetId = 0 },
                    new SimInput { EntityId = baseId + 1, MoveDirection = FixVector3.Zero, AttackTargetId = 0 },
                    new SimInput { EntityId = baseId + 2, MoveDirection = FixVector3.Zero, AttackTargetId = 0 },
                    new SimInput { EntityId = baseId + 3, MoveDirection = FixVector3.Zero, AttackTargetId = 0 },
                }, 1);

                var frame = new LockstepFrame();
                frame.Frame = f;
                frame.ServerHash = WorldStateHash.Compute(serverWorld);
                frame.Inputs.Add(new LockstepFrameInput { PlayerId = baseId, MoveXRaw = Fix64.One.RawValue, Missing = false });
                frame.Inputs.Add(new LockstepFrameInput { PlayerId = baseId + 1, Missing = true });
                frame.Inputs.Add(new LockstepFrameInput { PlayerId = baseId + 2, Missing = true });
                frame.Inputs.Add(new LockstepFrameInput { PlayerId = baseId + 3, Missing = true });

                fake.Push(new ServerMessage { LockstepFrame = frame });
                session.Pump(0);
            }

            Check("§S4d4：阳性对照 —— 会话侧的运行器真的开局并推进了（防「压根没接收」也算绿）",
                session.Lockstep != null && session.Lockstep.Started &&
                session.Lockstep.StepsExecuted == frames,
                session.Lockstep == null ? "没有运行器" : session.Lockstep.Describe());

            // ---- ④ 服务端宣布：A 队胜（击杀 2/0）----
            int killsA;
            int killsB;
            MatchRules.CountKills(serverWorld, new SortedTeamMap(rosterIds), out killsA, out killsB);

            EMatchOutcome outcome = MatchRules.Decide(killsA, killsB, serverWorld.Tick);

            var result = new MatchResult();
            result.Frame = serverWorld.Tick;
            result.Outcome = MatchRules.ToNumber(outcome);
            result.KillsA = killsA;
            result.KillsB = killsB;

            int events = 0;
            LockstepMatchMismatch captured = default(LockstepMatchMismatch);
            session.MatchMismatchDetected += m => { events++; captured = m; };

            fake.Push(new ServerMessage { MatchResult = result });
            session.Pump(0);

            Check("§S4d4⭐ 真实路径：**好的一条零不一致**（接线正确 ⇒ 复算与服务端一致）",
                session.MatchMismatchCount == 0 && session.MatchOutcome == EMatchOutcome.TeamA,
                "不一致=" + session.MatchMismatchCount +
                "；结果=" + (session.MatchOutcome == null ? "未落定" : MatchRules.Describe(session.MatchOutcome.Value)));

            Check("§S4d4⭐：而且**有「确实收到了结果」的正面证据**（落定了结果，不是「没收到」混过去）",
                session.MatchOutcome != null && events == 0,
                "落定=" + (session.MatchOutcome != null) + "；事件=" + events);

            // ---- ⑤ 改一位结果 ⇒ **恰好一次**不一致 + 三样可读 ----
            MatchResult tampered = result.Clone();
            tampered.Outcome = MatchRules.ToNumber(EMatchOutcome.TeamB);   // A 说成 B

            fake.Push(new ServerMessage { MatchResult = tampered });
            session.Pump(0);

            Check("§S4d4⭐ 真实路径：**改一位结果 ⇒ 恰好一次不一致** + 事件一次",
                session.MatchMismatchCount == 1 && events == 1,
                "不一致=" + session.MatchMismatchCount + "；事件=" + events);

            Check("§S4d4⭐：**三样可读**（我的判定 / 服务端判定 / 两队击杀）",
                events == 1 && captured.Mine == EMatchOutcome.TeamA &&
                captured.Server == EMatchOutcome.TeamB &&
                captured.MyKillsA == killsA && captured.ServerKillsA == (int)tampered.KillsA,
                events == 1 ? captured.ToString() : "事件没触发");

            // ---- ⑥ 没有 `room_state`（拿不到队伍映射）⇒ **不许假装一致** ----
            var lonely = new FakeNetTransport();
            var lonelySession = new NetSession(lonely, "锁步探针", "net-probe/lockstep");

            lonelySession.Connect("127.0.0.1", 7778);
            lonelySession.Pump(0);
            lonely.Push(MakeHandshakeAck(11));
            lonelySession.Pump(0);

            // ⚠️ **故意不喂 `room_state`**：没有成员名单 ⇒ 推不出队伍映射
            lonely.Push(new ServerMessage { LockstepStart = startMsg });
            lonelySession.Pump(0);

            var lonelyFrame = new LockstepFrame();
            lonelyFrame.Frame = 0;
            lonelyFrame.ServerHash = WorldStateHash.Compute(serverWorld);
            lonely.Push(new ServerMessage { LockstepFrame = lonelyFrame });
            lonelySession.Pump(0);

            lonely.Push(new ServerMessage { MatchResult = result });
            lonelySession.Pump(0);

            Check("§S4d4⭐ **没有队伍映射 ⇒ 不许假装一致**：结果不落定、也不谎报不一致",
                lonelySession.MatchOutcome == null && lonelySession.MatchMismatchCount == 0,
                "落定=" + (lonelySession.MatchOutcome != null) +
                "；不一致=" + lonelySession.MatchMismatchCount +
                "；房间=" + (lonelySession.CurrentRoom == null));

            session.Dispose();
            lonelySession.Dispose();
        }

        /// <summary>
        /// 【三十一】锁步世界 → `WorldSnapshot` 的适配器（S4-e 第 ② 件，**纯转换 ⇒ 可直驱验**）。
        ///
        /// <para>⭐ 最强的一条：把它喂给**真的 `SnapshotView`** ⇒ 证明**两种模式共用同一套渲染**是通的。</para>
        /// </summary>
        private static void LockstepSnapshotAdapter_Checks()
        {
            var world = new WorldState();
            world.Add(1, new FixVector3(1, 0, 2), 100);      // 「我」（玩家 1）
            world.Add(2, new FixVector3(-1, 0, 3), 0);       // 已死
            world.SetTick(5);

            var adapter = new NBC.Game.Net.LockstepSnapshotAdapter();
            var snapshot = new WorldSnapshot();

            bool ok = adapter.ApplyTo(world, 7, 1, snapshot);

            Check("§S4e⭐ 转换成功 + 实体数对得上",
                ok && snapshot.Entities.Count == 2 && adapter.LastFrame == 7,
                "ok=" + ok + "；实体=" + snapshot.Entities.Count);

            int alive = 0;

            for (int i = 0; i < snapshot.Entities.Count; i++)
            {
                if (snapshot.Entities[i].Alive)
                {
                    alive++;
                }
            }

            Check("§S4e⭐：**存活标记**按 `Hp > 0` 算（活 1 / 死 1）",
                alive == 1, "活着=" + alive);

            EntitySnapshot me = null;
            EntitySnapshot other = null;

            for (int i = 0; i < snapshot.Entities.Count; i++)
            {
                if (snapshot.Entities[i].EntityId == 1) { me = snapshot.Entities[i]; }
                if (snapshot.Entities[i].EntityId == 2) { other = snapshot.Entities[i]; }
            }

            Check("§S4e⭐ **`owner_player_id`**：我的实体带我的编号、别人的是 0（猜错会「操纵别人的角色」）",
                me != null && other != null && me.OwnerPlayerId == 1 && other.OwnerPlayerId == 0,
                me == null || other == null ? "缺实体" : ("我=" + me.OwnerPlayerId + " 他=" + other.OwnerPlayerId));

            Check("§S4e⭐：**位置从「米（定点）」换成「毫米（整数）」**、不引浮点（1 米 ⇒ 1000）",
                me != null && me.PosXMm == 1000 && me.PosZMm == 2000 && other != null && other.PosXMm == -1000,
                me == null ? "缺实体" : ("我 X=" + me.PosXMm + " Z=" + me.PosZMm));

            Check("§S4e：血量/上限逐项带过来",
                me != null && me.Hp == 100 && me.MaxHp == 100 && other != null && other.Hp == 0,
                me == null ? "缺实体" : ("我 hp=" + me.Hp + "/" + me.MaxHp));

            // ⭐ 喂给**真的 `SnapshotView`** ⇒ 证明「两模式共用一套渲染」是通的
            var view = new NBC.Game.Net.SnapshotView();
            view.Apply(snapshot);

            Check("§S4e⭐ **喂给真 `SnapshotView`**：被接受（`Applied == 1`、**没被当过过期丢掉**）",
                view.Applied == 1 && view.StaleIgnored == 0 && view.EntityCount == 2 &&
                view.AliveCount == 1 && view.ServerTick == 7,
                "Applied=" + view.Applied + " Stale=" + view.StaleIgnored +
                " 实体=" + view.EntityCount + " 帧=" + view.ServerTick);

            // ⭐ 帧号**不递增** ⇒ 必须被我自己挡下（否则会被 `SnapshotView` 静静丢掉 ⇒ 假故障）
            bool again = adapter.ApplyTo(world, 7, 1, snapshot);

            Check("§S4e⭐ **帧号不递增会被挡下**（重复帧 ⇒ false + 计数涨；不挡就会被渲染静静丢掉）",
                !again && adapter.NonMonotonicBlocked == 1 && adapter.LastFrame == 7,
                "返回=" + again + "；挡下=" + adapter.NonMonotonicBlocked);

            // 阳性对照：空世界 ⇒ 不炸、清空
            var emptyAdapter = new NBC.Game.Net.LockstepSnapshotAdapter();
            var emptySnapshot = new WorldSnapshot();
            emptySnapshot.Entities.Add(new EntitySnapshot { EntityId = 99 });

            bool emptyOk = emptyAdapter.ApplyTo(null, 1, 1, emptySnapshot);

            Check("§S4e：阳性对照 —— **空世界 ⇒ 不炸**、且把目标清空（`EntityCount == 0`）",
                emptyOk && emptySnapshot.Entities.Count == 0,
                "ok=" + emptyOk + "；实体=" + emptySnapshot.Entities.Count);
        }

        /// <summary>
        /// 【三十二】`--fill-seats` 的底座：**机器人席位**（S4-e 第 ① 件）。
        ///
        /// <para>⚠️ 只验**能验的部分**：机器人在 `Room`/`RoomRegistry` 这一层的行为 + ⭐
        /// "开局名单 == 下发名单"那条等式。**`--fill-seats` 的参数解析在 Host 里、探针够不到** ——
        /// 那一条靠 `§35.0` 的人工步骤。</para>
        /// </summary>
        private static void RoomBots_FillSeats()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                for (int i = 0; i < 4; i++)
                {
                    h.ConnectAndHandshake("补位" + i);
                }

                if (h.Sessions.Count < 4)
                {
                    Check("§S4e2：阳性对照 —— 备齐 4 个会话", false, "会话数=" + h.Sessions.Count);
                    return;
                }

                int n = h.Sessions.Count;
                var room = new Room("bot-probe", 0, 4);

                // ---- ① 机器人 = 0 时：`IsFull` 与"只看真人"**逐位等价**（默认零行为变化）----
                room.Add(h.Sessions[n - 4], "p0");
                room.Add(h.Sessions[n - 3], "p1");
                room.Add(h.Sessions[n - 2], "p2");

                Check("§S4e2⭐ **机器人 = 0 时行为不变**：3/4 真人 ⇒ 没满、相位仍是 `Waiting`",
                    !room.IsFull && room.Phase == RoomPhase.Waiting && room.BotCount == 0,
                    "满=" + room.IsFull + "；相位=" + room.Phase + "；机器人=" + room.BotCount);

                // ---- ② 补到 4 ⇒ 满员 ⇒ `Running`（这就是"一个人也能开局"的机制）----
                room.AddBot(Room.BotIdBase, "Bot1");

                Check("§S4e2⭐ **机器人也计入满员**：补 1 个 ⇒ 4/4 ⇒ 满、相位置 `Running`",
                    room.IsFull && room.Phase == RoomPhase.Running && room.BotCount == 1 &&
                    room.SeatCount == 3,
                    "满=" + room.IsFull + "；相位=" + room.Phase + "；真人=" + room.SeatCount +
                    "；机器人=" + room.BotCount);

                // ---- ③ 保留段 + 不与真人撞 ----
                bool inRange = true;
                bool clash = false;

                for (int i = 0; i < room.Bots.Count; i++)
                {
                    if (room.Bots[i].PlayerId < Room.BotIdBase)
                    {
                        inRange = false;
                    }

                    for (int s = 0; s < room.Seats.Count; s++)
                    {
                        if (room.Seats[s].Session.PlayerId == room.Bots[i].PlayerId)
                        {
                            clash = true;
                        }
                    }
                }

                Check("§S4e2⭐ 机器人编号落在**保留段**（`>= BotIdBase`）且**不与真人撞**",
                    inRange && !clash && Room.BotIdBase > 0,
                    "段起点=" + Room.BotIdBase + "；越界=" + (!inRange) + "；撞=" + clash);

                // ---- ④ 机器人**不可被会话查找**（`Find`/`FindByPlayerId` 只看 `Seats`）----
                Check("§S4e2：机器人**不可被会话查找**（`FindByPlayerId` 只看 `Seats`）",
                    room.FindByPlayerId(Room.BotIdBase) == null,
                    "居然查到了 ⇒ 说明机器人混进了 `Seats`");

                // ---- ⑤ ⭐⭐ 本轮最重要的一条：**开局名单 == 下发名单**（同一个集合）----
                List<long> roster = room.RosterIds;                 // Host 开局用的
                RoomState state = room.ToState();                   // 下发给客户端的

                var fromRoster = new List<long>(roster);
                var fromState = new List<long>(state.Members.Count);

                for (int i = 0; i < state.Members.Count; i++)
                {
                    fromState.Add(state.Members[i].PlayerId);
                }

                fromRoster.Sort();
                fromState.Sort();

                bool same = fromRoster.Count == fromState.Count;

                if (same)
                {
                    for (int i = 0; i < fromRoster.Count; i++)
                    {
                        if (fromRoster[i] != fromState[i])
                        {
                            same = false;
                        }
                    }
                }

                Check("§S4e2⭐⭐ **开局名单 == 下发名单**（同一个集合）—— 不一致 ⇒ 两端队伍映射不同 ⇒ **假分歧**",
                    same && fromRoster.Count == 4,
                    "roster=" + string.Join(",", fromRoster.ConvertAll(x => x.ToString()).ToArray()) +
                    " 下发=" + string.Join(",", fromState.ConvertAll(x => x.ToString()).ToArray()));

                // ⚠️ **长度闸**（补的）：没有它，下面那条"队伍逐项相同"对"名单长度不同"不敏感 ——
                //    变异 V（`ToState()` 不写机器人）实测时那条**保持绿**，只有"集合相等"抓到。
                Check("§S4e2⭐⭐ **两份名单的元素个数相同**（⭐ 补的长度闸：缺它就抓不到「少一个机器人」）",
                    fromRoster.Count == fromState.Count &&
                    fromRoster.Count == room.SeatCount + room.BotCount,
                    "roster=" + fromRoster.Count + " 下发=" + fromState.Count +
                    " 真人=" + room.SeatCount + " 机器人=" + room.BotCount);

                // ⭐ 而且两端用**同一个纯函数**推出的队伍也一致（这才是"假分歧"真正要防的）
                var teamFromRoster = new SortedTeamMap(roster);
                var teamFromState = new SortedTeamMap(fromState);

                bool teamsSame = fromRoster.Count == fromState.Count;   // ⚠️ **先卡长度**

                for (int i = 0; i < fromState.Count; i++)
                {
                    int id = (int)fromState[i];

                    if (teamFromRoster.TeamOf(id) != teamFromState.TeamOf(id))
                    {
                        teamsSame = false;
                    }
                }

                Check("§S4e2⭐⭐：而且两端推出的**队伍映射逐项相同**（同一个 `SortedTeamMap` + 同一份名单）",
                    teamsSame, "两端队伍映射不一致 ⇒ 客户端会报假分歧");
            }
        }

        /// <summary>
        /// 【三十三】**P0（Docs\27 §三十六）：未知副本号不许打挂服务端**。
        ///
        /// <para>现场（负责人真 Host 的逐字输出）：客户端进房时发 `dungeon_id=0` ⇒
        /// `RoomBattleService.EnsureBattle` 建世界时 `throw` ⇒ **异常抛在主循环里 ⇒ 整个 Host 进程死**。
        /// 任何人发一个坏副本号就能做到 ⇒ 所以这一节验**两段**，缺一段都不算修好：</para>
        /// <para>① **进房那一步就拒**（`ErrorResponse` + 可读原因，不建房、不进房）；
        /// ⭐ 而且判据不是「回了错误」，是「**服务端还活着**」：同一条连接随后 Ping 仍收到 Pong。</para>
        /// <para>② 万一有房间**绕过了**那道校验（修之前建出来的、或将来别处新建的），
        /// `RoomBattleService` 也只做到**房间级失败**（摘掉、不崩），**不静默开空世界**
        /// —— 两条都要守：崩了是 P0，静默开空世界是「进去什么都没有」那种极难查的假故障。</para>
        /// <para>⚠️ 断言全部用协议消息 / 对象状态（W26），不抓日志中文。</para>
        /// <para>⚠️ 这一节**不连真 Host**（不用 `--e2e`）：它跑在探针进程内的生产类上，
        /// 所以「服务端还活着」= 同一条 socket 连接还能问出 Pong。</para>
        /// </summary>
        private static void RoomGuard_UnknownDungeonNeverKillsTheHost()
        {
            // ================================================================
            //  ① 进房那一步：未知副本号 ⇒ ErrorResponse，且**服务端还活着**
            // ================================================================
            using (var h = new Harness())
            {
                Client bad = h.ConnectAndHandshake("坏副本号");

                bad.JoinRoom(string.Empty, 0);      // ⚠️ 0 = `Dungeon` 表里没有的那一行
                h.WaitUntil(() => bad.LastError() != null, "未知副本号的拒绝答复");

                ErrorResponse rejected = bad.LastError();

                Check("§P0⭐ 未知副本号（0）进房 ⇒ 收到 `ErrorResponse`，且原因**可读**（有副本号、说「不存在」）",
                    rejected != null && rejected.Code == NetErrors.DungeonNotFound
                    && rejected.Message != null && rejected.Message.Contains("0")
                    && rejected.Message.Contains("不存在"),
                    rejected == null
                        ? "一条错误都没收到（服务端可能已经死了）"
                        : "code=" + rejected.Code + "，message=" + rejected.Message);

                Check("§P0⭐ 被拒之后**什么都没建**：房间数 0、一份席位表都没收到",
                    h.Registry.RoomCount == 0 && bad.LastRoom() == null,
                    "房间数=" + h.Registry.RoomCount +
                    "；收到的席位表=" + (bad.LastRoom() == null ? "无" : bad.LastRoom().Members.Count + " 人"));

                // ⭐⭐ 这条才是「打不死」的判据：前面那条只证明「回了错误」，
                //    证明不了「进程还活着」（抛异常也可能在回包之后才发生）。
                bad.Send(new ClientMessage { Ping = new Ping { ClientTimeMs = 424242 } });
                h.WaitUntil(() => bad.FindPong() != null, "被拒之后的心跳答复");

                Pong pong = bad.FindPong();

                Check("§P0⭐⭐ **服务端还活着**：同一条连接随后 Ping ⇒ 收到 Pong（一个坏副本号打不死它）",
                    pong != null && pong.ClientTimeMs == 424242 && bad.Transport.IsConnected,
                    pong == null
                        ? "没收到 Pong（服务端多半已经死了）"
                        : "client_time=" + pong.ClientTimeMs + "；stillConnected=" + bad.Transport.IsConnected);

                // ⭐ 把**实际收到的原文**打出来（给负责人对照「原因到底长什么样」，失败时也看得到）
                Console.WriteLine("  [观察] 未知副本的拒绝答复：code=" +
                                  (rejected == null ? "（没收到）" : rejected.Code + "，message=" + rejected.Message) +
                                  "；随后 Ping ⇒ client_time=" +
                                  (pong == null ? "（没收到 Pong）" : pong.ClientTimeMs.ToString()));

                // ---- 阳性对照：**同一条连接**改发合法副本 1001 ⇒ 照常建起来、照常开打 ----
                bad.JoinRoom(string.Empty, 1001);
                h.WaitUntil(() => bad.LastRoom() != null, "合法副本的席位表");
                h.TickBattles(3);

                DungeonBattle opened = h.BattleOf(bad);

                Check("§P0⭐ 阳性对照：合法副本 1001 ⇒ 进房成功、战斗世界照常开（副本号对、活怪数对、有快照）",
                    opened != null && opened.DungeonId == 1001
                    && opened.AliveMonsterCount == MonstersInDungeon(h, 1001)
                    && bad.SnapshotCount() > 0,
                    "世界=" + (opened == null ? "没建" : "副本 " + opened.DungeonId) +
                    "；活怪=" + (opened == null ? -1 : opened.AliveMonsterCount) +
                    "（表里 " + MonstersInDungeon(h, 1001) + "）；快照=" + bad.SnapshotCount() +
                    "；最后一次错误=" + (bad.LastError() == null ? "无" : bad.LastError().Message));

                // ============================================================
                //  ② 房间级失败：**绕过进房校验**造一个坏房间 ⇒ 不崩、摘掉、不连累别人
                //     ⚠️ `RoomRegistry.Create` 是「直接建」（校验在 `Join` 里）⇒ 正好用来
                //        模拟「修之前建出来的坏房间」，证明第二道闸真的在。
                // ============================================================
                Room broken = h.Registry.Create(0);
                broken.Add(h.Sessions[h.Sessions.Count - 1], "坏房间里的玩家");

                int snapshotsBefore = bad.SnapshotCount();
                bool threw = false;
                string throwText = string.Empty;

                try
                {
                    h.TickBattles(3);       // ⚠️ 包起来：变异（把 throw 放回去）时**要能读出红**，别让探针自己崩掉
                }
                catch (Exception ex)
                {
                    threw = true;
                    throwText = ex.GetType().Name + "：" + ex.Message;
                }

                Check("§P0⭐ 坏房间在推进里 ⇒ **不抛异常**（房间级失败，不是进程级）",
                    !threw, threw ? "抛了：" + throwText : "没抛（正确）");

                Check("§P0⭐ 坏房间**被摘掉**：没有战斗世界、且计入 `UnopenableRoomCount`（不静默开空世界）",
                    h.Battles.Find(broken.RoomId) == null && h.Battles.UnopenableRoomCount == 1,
                    "世界=" + (h.Battles.Find(broken.RoomId) == null ? "没建" : "居然建了") +
                    "；开不了计数=" + h.Battles.UnopenableRoomCount);

                Check("§P0⭐ **失败不连累别人**：同一批 Tick 里合法房间的快照**照常在涨**",
                    bad.SnapshotCount() > snapshotsBefore,
                    "快照 " + snapshotsBefore + " → " + bad.SnapshotCount());
            }

            // ================================================================
            //  ③ 合法副本 + `--fill-seats` 那条路：补席位 ⇒ 满员 ⇒ Running ⇒ 锁步**真的**开局
            //     ⚠️ 这里调的是 `RoomSetup`（**Host 主循环用的就是它**）——
            //        不是探针自己抄一段，否则验的是抄本（§三十六 就是为了这个把两段搬出来的）。
            // ================================================================
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                var lockstep = new LockstepService(h.Server, h.Registry);
                lockstep.RegisterHandlers(h.Router);

                // ⚠️ **必须用真登录（不是游客）**：`LockstepRoster.Build` 会把**非正数编号**
                //    （= 游客）**跳过**（见那个类的文件头第二节）⇒ 游客进房只能得到 3 个机器人实体，
                //    「实体数 = 4」那条就验不到「真人也在开局名单里」。这是 W15（登录 ⇒ 正数编号）的现场。
                Client solo = h.ConnectAndLogin("alice", "pw-alice");
                HandshakeAck soloAck = AckOf(solo, "alice 登录");

                Check("§P0：阳性对照 —— 单排是**真登录**（playerId > 0，否则下面「实体数 4」验不到真人）",
                    soloAck != null && soloAck.Accepted && soloAck.PlayerId > 0,
                    soloAck == null ? "没收到握手答复" : Describe(soloAck));

                solo.JoinRoom(string.Empty, 1001);
                h.WaitUntil(() => solo.LastRoom() != null, "单排进房");

                Room room = h.Registry.Find(solo.RoomId);

                if (room == null)
                {
                    Check("§P0：阳性对照 —— 单排拿到了房间（拿不到就无从补席位）", false,
                        "房号=" + (solo.RoomId ?? "无"));
                    return;
                }

                // ⚠️ 顺序**必须与 Host 主循环一致**（`Program.cs` 的 ②″/②′/锁步 tick）：
                //    状态同步链推进 → 补席位 → 相位消费（开局）→ 锁步推进。
                for (int tick = 0; tick < 12; tick++)
                {
                    h.TickBattles(1);
                    RoomSetup.FillSeats(h.Rooms, 4);                                        // ≈ `--fill-seats=4`
                    RoomSetup.StartLockstepForRunningRooms(lockstep, h.Registry, 2);        // 相位 ⇒ 开一局
                    lockstep.Tick();
                }

                h.PumpFor(50);      // 把最后几帧泵回客户端

                Check("§P0⭐ 合法副本 1001 + 补席位 ⇒ **满员 4/4 ⇒ `RoomPhase.Running`**（1 真人 + 3 机器人）",
                    room.IsFull && room.Phase == RoomPhase.Running
                    && room.SeatCount == 1 && room.BotCount == 3,
                    "满=" + room.IsFull + "；相位=" + room.Phase +
                    "；真人=" + room.SeatCount + "；机器人=" + room.BotCount);

                // ⭐ 2026-09-28 修的那条小问题：机器人编号**从保留段的第一个开始**
                //    （原来 `slot` 用的是「槽位」⇒ 1 真人时从 1 起 ⇒ 9002..9004，永远用不到 9001）
                var botIds = new List<long>(room.BotCount);

                for (int i = 0; i < room.Bots.Count; i++)
                {
                    botIds.Add(room.Bots[i].PlayerId);
                }

                botIds.Sort();

                bool botIdsContiguous = botIds.Count == 3;

                for (int i = 0; botIdsContiguous && i < botIds.Count; i++)
                {
                    if (botIds[i] != Room.BotIdBase + i)
                    {
                        botIdsContiguous = false;
                    }
                }

                Check("§S4e4⭐ 机器人编号**从保留段第一个开始连续**（9001,9002,9003 —— 原来偏一位成了 9002..9004）",
                    botIdsContiguous,
                    "机器人=" + string.Join(",", botIds.ConvertAll(x => x.ToString()).ToArray()) +
                    "（期望 " + Room.BotIdBase + ".." + (Room.BotIdBase + 2) + "）");

                Check("§P0⭐ 锁步**真的开局**了（不只是相位变了）：`HasRoom` 为真、服务端广播了帧",
                    lockstep.HasRoom(room.RoomId) && lockstep.BroadcastFrames >= 3,
                    "HasRoom=" + lockstep.HasRoom(room.RoomId) +
                    "；广播帧=" + lockstep.BroadcastFrames + "；" + lockstep.Describe());

                // ⭐ 客户端手里的名单也必须是 4 人 —— 补席位**只加人、不下发**是个真坑：
                //    服务端开局用 4 个（`RosterIds`）、客户端还是"只有我一个" ⇒ 两端队伍映射不同 ⇒ 假分歧。
                //    （这条是 2026-09-28 真 Host 端到端实测抓到的；抓到时本节的 4 条**全绿** —— 见 §36.5）
                RoomState seenBySolo = solo.LastRoom();

                Check("§P0⭐ 补席位之后**席位表也下发到了客户端**（4 人名单 ⇒ 两端队伍映射同源）",
                    seenBySolo != null && seenBySolo.Members.Count == 4 && seenBySolo.DungeonId == 1001,
                    seenBySolo == null
                        ? "一份席位表都没收到"
                        : "名单人数=" + seenBySolo.Members.Count + "（期望 4），副本=" + seenBySolo.DungeonId);

                LockstepStart start = FindLockstepStart(solo);

                Check("§P0⭐ 客户端收到 `LockstepStart`，且**实体数 = 4**（真人在内 ⇒ 开局名单就是 `RosterIds`）",
                    start != null && start.Entities != null && start.Entities.Count == 4,
                    start == null ? "一条 start 都没收到" : "实体数=" + start.Entities.Count);

                int frames = CountLockstepFrames(solo);
                int lastFrame = LastLockstepFrame(solo);

                Check("§P0⭐ 客户端**收到锁步帧**且在推进（帧号在涨）",
                    frames >= 3 && lastFrame >= 2,
                    "收帧数=" + frames + "；末帧=" + lastFrame);
            }
        }

        /// <summary>
        /// 【三十四】S4-e 收尾：**真分流** —— 「客户端喂给表现层的那份数据」由开关决定。
        ///
        /// <para>被验的是两个**纯 C#**的小件（都在 `Game\Net\`，探针通配编得到 ⇒ 能直驱、能变异）：</para>
        /// <list type="bullet">
        /// <item>`ViewSourceDecision.ChooseSource(bool lockstepMode, bool lockstepStarted, bool worldUsable)` —— 选源那条规则
        /// （第三个参数是 §S4e5 补的：**世界可用 = 非 null 且 实体数 > 0 且 已开局**）</item>
        /// <item>`PresentationFeed` —— 把选中的源喂进**同一个** `SnapshotView`（3D 那一层读的就是它）</item>
        /// </list>
        ///
        /// <para>⭐ 三件事，每件都对应一类**静默**错误：</para>
        /// <list type="number">
        /// <item>**世界不可用时回退**：锁步模式下但还没开局 / 世界是 0 实体 ⇒ **不许**用那份数据盖掉快照
        /// （盖掉的表现是「场景里东西全没了」，不报错、计数也不涨）</item>
        /// <item>**帧号单调**：两套帧号坐标系不同 ⇒ 切源时表现层**自己发帧号**，
        /// `SnapshotView.StaleIgnored` 必须一直是 0（否则数据被**静静丢掉** ⇒ 画面停住）</item>
        /// <item>**只读 + 复用**：`LockstepClient.World` 一个字段都不写、`EntitySnapshot` 跨帧是同一批对象</item>
        /// </list>
        ///
        /// <para>⚠️ 这一节**不碰 Unity**（视图接管那个 MonoBehaviour 归 `_api-probe` 管编译，靠 §35.0 人工看）。</para>
        /// <para>⚠️ 停滞回退 / 回退口径 / 0 实体 / 只读 / 零分配 / 换局 那六条在**下一节【三十五】**（§S4e5）。</para>
        /// </summary>
        private static void PresentationSource_Checks()
        {
            // ================================================================
            //  ① 纯决策：四种组合逐一钉住（阳性对照就是「开但未开局」）
            // ================================================================
            Check("§S4e4：关 ⇒ 快照（开局与未开局**都是**快照 —— 关着的时候锁步世界再新也不用它）",
                ViewSourceDecision.ChooseSource(false, false, true) == EViewSource.Snapshot &&
                ViewSourceDecision.ChooseSource(false, true, true) == EViewSource.Snapshot,
                "未开局=" + ViewSourceDecision.ChooseSource(false, false, true) +
                " 已开局=" + ViewSourceDecision.ChooseSource(false, true, true));

            Check("§S4e4⭐ **阳性对照：开但未开局 ⇒ 回退到快照**（去掉这条就会拿空世界盖掉快照）",
                ViewSourceDecision.ChooseSource(true, false, true) == EViewSource.Snapshot,
                "开+未开局 ⇒ " + ViewSourceDecision.ChooseSource(true, false, true));

            Check("§S4e4：开且已开局 ⇒ 用锁步世界",
                ViewSourceDecision.ChooseSource(true, true, true) == EViewSource.Lockstep,
                "开+已开局 ⇒ " + ViewSourceDecision.ChooseSource(true, true, true));

            // ⭐ §S4e5：第三个判据 —— **世界可用**（非 null 且 实体数 > 0 且 已开局）。
            //    上一版只判 `!= null`，于是「0 实体的世界」会被当成可用 ⇒ 空快照盖掉画面。
            Check("§S4e5⭐ **世界可用 = 非 null 且 实体数 > 0 且 已开局**（0 实体 ≠ 可用；开+已开局也得回退）",
                !ViewSourceDecision.IsWorldUsable(null, true) &&
                !ViewSourceDecision.IsWorldUsable(new WorldState(), true) &&
                !ViewSourceDecision.IsWorldUsable(MakeOneEntityWorld(), false) &&
                ViewSourceDecision.IsWorldUsable(MakeOneEntityWorld(), true) &&
                ViewSourceDecision.ChooseSource(true, true, false) == EViewSource.Snapshot,
                "null=" + ViewSourceDecision.IsWorldUsable(null, true) +
                " 0实体=" + ViewSourceDecision.IsWorldUsable(new WorldState(), true) +
                " 未开局=" + ViewSourceDecision.IsWorldUsable(MakeOneEntityWorld(), false));

            // ================================================================
            //  ② 喂给表现层：快照那条
            // ================================================================
            SnapshotView serverView = MakeServerSnapshotView(5, 7, 50, 1);

            var lockstepWorld = new WorldState();
            lockstepWorld.Add(1, new FixVector3(1, 0, 2), 100);       // 我
            lockstepWorld.Add(9001, new FixVector3(-1, 0, 3), 0);     // 已死
            lockstepWorld.SetTick(0);

            var feed = new PresentationFeed();

            bool fedSnapshot = feed.Refresh(1, serverView, lockstepWorld, -1, false);
            EntitySnapshot snapMe = feed.View.Find(7);

            Check("§S4e4⭐ 关 ⇒ 表现层拿到**服务端快照**（单位 7、HP 50、`owner_player_id` 原样照抄）",
                fedSnapshot && feed.Source == EViewSource.Snapshot && feed.FramesFromSnapshot == 1 &&
                feed.View.EntityCount == 1 && snapMe != null && snapMe.Hp == 50 &&
                snapMe.OwnerPlayerId == 1 && feed.FallbackEvents == 0,
                "源=" + feed.Source + "；" + feed.Describe());

            // ---- ③ 同一张快照再喂一遍 ⇒ 不重复采纳 ----
            long appliedBefore = feed.View.Applied;
            bool fedAgain = feed.Refresh(1, serverView, lockstepWorld, -1, false);

            Check("§S4e4：源帧号没变 ⇒ **不重喂**（`Applied` 不涨；否则它就是个假指标）",
                !fedAgain && feed.View.Applied == appliedBefore,
                "返回=" + fedAgain + "；Applied=" + feed.View.Applied);

            // ================================================================
            //  ④ ⭐⭐ 本节的 **star**：开锁步但**还没开局** ⇒ 回退，而且**绝不盖掉**已有的快照
            // ================================================================
            feed.UseLockstep = true;

            long tickBeforeFallback = feed.View.ServerTick;
            bool fedFallback = feed.Refresh(1, serverView, lockstepWorld, 0, false);

            Check("§S4e4⭐⭐ **开但未开局 ⇒ 回退**：世界**没被盖掉**（单位 7 还在、HP 仍是 50）、回退计数 +1",
                !fedFallback && feed.Source == EViewSource.Snapshot && feed.FallbackEvents == 1 &&
                feed.View.EntityCount == 1 && feed.View.Find(7) != null && feed.View.Find(7).Hp == 50 &&
                feed.View.ServerTick == tickBeforeFallback && feed.View.StaleIgnored == 0,
                "源=" + feed.Source + "；实体=" + feed.View.EntityCount +
                "；表现帧=" + feed.View.ServerTick + "（回退前 " + tickBeforeFallback + "）；" + feed.Describe());

            // ================================================================
            //  ⑤ 开局了 ⇒ 换成锁步世界（位置 / 血量 / `owner_player_id` 全来自锁步）
            // ================================================================
            bool fedLockstep = feed.Refresh(1, serverView, lockstepWorld, 0, true);
            EntitySnapshot lockstepMe = feed.View.Find(1);
            EntitySnapshot lockstepDead = feed.View.Find(9001);

            Check("§S4e4⭐ 开且已开局 ⇒ 表现层拿到**锁步世界**（单位 1 在、位置 1000/2000 毫米、owner=1、9001 已死）",
                fedLockstep && feed.Source == EViewSource.Lockstep && feed.FramesFromLockstep == 1 &&
                feed.View.EntityCount == 2 && lockstepMe != null && lockstepMe.PosXMm == 1000 &&
                lockstepMe.PosZMm == 2000 && lockstepMe.OwnerPlayerId == 1 && lockstepMe.Alive &&
                lockstepDead != null && !lockstepDead.Alive,
                "源=" + feed.Source + "；" + feed.Describe());

            // ================================================================
            //  ⑥ ⭐⭐ 切源来回（快照 → 锁步 → 快照 → 锁步）⇒ 帧号**不倒退**
            //     ⚠️ 两套帧号（服务端 tick / 锁步帧）没有可比性：直接用源帧号当 `ServerTick`
            //        就会在这一步被 `SnapshotView` 静静丢掉 ⇒ 画面停住
            // ================================================================
            SnapshotView serverView2 = MakeServerSnapshotView(6, 7, 40, 1);

            long appliedBeforeSwitch = feed.View.Applied;

            feed.UseLockstep = false;
            bool back = feed.Refresh(1, serverView2, lockstepWorld, 0, true);      // 关 = 用快照（帧 6）

            feed.UseLockstep = true;
            bool again1 = feed.Refresh(1, serverView2, lockstepWorld, 1, true);    // 锁步帧 1
            bool again2 = feed.Refresh(1, serverView2, lockstepWorld, 2, true);    // 锁步帧 2

            Check("§S4e4⭐⭐ **切源不倒退**：`StaleIgnored == 0`、`Applied` 恰好 +3（表现层自己发帧号）",
                back && again1 && again2 && feed.View.StaleIgnored == 0 &&
                feed.View.Applied == appliedBeforeSwitch + 3 &&
                feed.SourceSwitches >= 2,
                "Applied " + appliedBeforeSwitch + "→" + feed.View.Applied +
                "；Stale=" + feed.View.StaleIgnored + "；" + feed.Describe());

            // ================================================================
            //  ⑦ ⭐ 不每帧分配：同一个单位跨帧是**同一个 `EntitySnapshot` 对象**（池子复用）
            // ================================================================
            EntitySnapshot pooledBefore = feed.View.Find(1);

            feed.Refresh(1, serverView2, lockstepWorld, 3, true);
            feed.Refresh(1, serverView2, lockstepWorld, 4, true);

            EntitySnapshot pooledAfter = feed.View.Find(1);

            Check("§S4e4⭐ **不每帧分配**：同一单位跨帧是**同一个对象**（`EntitySnapshot` 池子复用）",
                ReferenceEquals(pooledBefore, pooledAfter) && pooledAfter != null,
                "同一个=" + ReferenceEquals(pooledBefore, pooledAfter));

            // ================================================================
            //  ⑧ ⭐ 换一局（**新世界对象** + 帧号回到 0）⇒ 仍然能喂进去
            //     ⚠️ 适配器的单调闸记着上一局的末帧；不换适配器 ⇒ 新一局第 0 帧被挡 ⇒ **表现层永远不更新**
            // ================================================================
            var secondMatch = new WorldState();
            secondMatch.Add(1, new FixVector3(0, 0, 0), 100);
            secondMatch.Add(9001, new FixVector3(0, 0, 0), 100);
            secondMatch.SetTick(0);

            bool secondFed = feed.Refresh(1, serverView2, secondMatch, 0, true);
            EntitySnapshot secondBot = feed.View.Find(9001);

            Check("§S4e4⭐ **换一局**（新世界 + 帧号回到 0）⇒ 仍然喂得进去（适配器跟着换）",
                secondFed && feed.Source == EViewSource.Lockstep && feed.View.EntityCount == 2 &&
                secondBot != null && secondBot.Alive && feed.View.StaleIgnored == 0,
                "喂进去了=" + secondFed + "；" + feed.Describe());

            Check("§S4e4：整节跑完 `StaleIgnored` 仍是 **0**（一次都没被当成过期丢掉）",
                feed.View.StaleIgnored == 0,
                "Stale=" + feed.View.StaleIgnored);
        }

        /// <summary>
        /// 【三十五】验收修正：五个问题各一条**可直驱**的检查（全部走 `PresentationFeed.Refresh`，不碰 Unity）。
        ///
        /// <para>为什么要有这一节：上一轮的【三十四】把"真分流"验得很细，但有五个洞是**读代码才看得出来**的
        /// ——它们都表现为「不报错、计数也不涨」，只有把它们做成**能当场变红**的检查才算修好：</para>
        /// <list type="number">
        /// <item>⭐⭐ **沉默冻结**：`Started` 之后帧不再前进（一局结束 / 服务端停播）⇒ 上一版**永远停在上一帧**
        /// （回退 0、`StaleIgnored` 0、`Applied` 不动）。现在必须**自动回退到快照**并计数。</item>
        /// <item>**回退口径**：上一版每帧 `++` ⇒ 一分钟上万 ⇒「回退 1 次」和「回退 1 帧」分不清。
        /// 现在**事件数**（跃迁才 +1）与**帧数**（每帧 +1）分开，两个都要钉住。</item>
        /// <item>**0 实体不算可用**：`world != null` 但 0 实体 ⇒ 不许拿它盖掉快照（上一版只判 `null`）。</item>
        /// <item>**只读**：`WorldStateHash.Compute(world)` 喂前喂后必须一样（上一版只靠人读代码）。</item>
        /// <item>**零分配**：`GC.GetAllocatedBytesForCurrentThread()` 的差值是 **0**（上一版每帧一个 `SimEntity[]`）。</item>
        /// </list>
        /// <para>外加**换局**两种形态：换了对象、以及**同一个对象但帧号回到 0**（后者上一版会把新一局第 0 帧挡掉）。</para>
        /// </summary>
        private static void PresentationSource_StallAndZeroAlloc_Checks()
        {
            // ================================================================
            //  ① 回退口径：**事件数** vs **帧数**（上一版混成一个数 ⇒ 判据失效）
            // ================================================================
            var eventFeed = new PresentationFeed();
            eventFeed.UseLockstep = true;

            for (int i = 0; i < 10; i++)
            {
                eventFeed.Refresh(1, null, null, -1, false);     // 未开局、也没有快照 ⇒ 一次都没喂
            }

            Check("§S4e5⭐ **回退口径**：未开局连调 10 次 `Refresh` ⇒ 回退**事件**数 == 1、回退**帧**数 == 10",
                eventFeed.FallbackEvents == 1 && eventFeed.FallbackFrames == 10 &&
                eventFeed.FallbackReason == EFallbackReason.NotStarted &&
                eventFeed.Source == EViewSource.Snapshot,
                "事件=" + eventFeed.FallbackEvents + " 帧=" + eventFeed.FallbackFrames +
                " 原因=" + ViewSourceDecision.Describe(eventFeed.FallbackReason) + "；" + eventFeed.Describe());

            eventFeed.Refresh(1, null, null, -1, false);

            Check("§S4e5：同一段回退里再调 1 次 ⇒ **事件数仍是 1**（跃迁才 +1）、帧数 11（这一条把两个口径钉开）",
                eventFeed.FallbackEvents == 1 && eventFeed.FallbackFrames == 11,
                "事件=" + eventFeed.FallbackEvents + " 帧=" + eventFeed.FallbackFrames);

            // ================================================================
            //  ② ⭐⭐ 停滞回退：`Started` 之后**不再喂新帧**（一局结束 / 服务端停播的现场）
            //     ⚠️ 判据是**刷新计数**（`StallRefreshThreshold`），不是墙上时钟 ⇒ 这里能直接驱动
            // ================================================================
            var stallFeed = new PresentationFeed();
            stallFeed.UseLockstep = true;

            var stallWorld = new WorldState();
            stallWorld.Add(1, new FixVector3(3, 0, 4), 100);
            stallWorld.Add(9001, new FixVector3(-2, 0, 1), 0);
            stallWorld.SetTick(5);

            bool stallFirstFed = stallFeed.Refresh(
                1, MakeServerSnapshotView(100, 7, 50, 1), stallWorld, 5, true);

            bool stallFirstWasLockstep = stallFeed.Source == EViewSource.Lockstep &&
                                         stallFeed.View.Find(1) != null;

            long appliedAtStall = stallFeed.View.Applied;

            // 之后帧号**一直是 5**，而服务端快照链照常在推（它没停 —— 停的只有锁步那条）
            int stallRefreshes = PresentationFeed.StallRefreshThreshold + 2;

            for (int i = 0; i < stallRefreshes; i++)
            {
                stallFeed.Refresh(1, MakeServerSnapshotView(101 + i, 7, 50, 1), stallWorld, 5, true);
            }

            Check("§S4e5⭐⭐ **停滞回退**：`Started` 但帧连续 N 次 `Refresh` 没前进 ⇒ 自动回退到快照（不再沉默冻结）",
                stallFirstFed && stallFirstWasLockstep &&
                stallFeed.Stalled && stallFeed.FallbackReason == EFallbackReason.Stalled &&
                stallFeed.Source == EViewSource.Snapshot && stallFeed.Chosen == EViewSource.Snapshot &&
                stallFeed.View.Find(7) != null && stallFeed.View.Find(1) == null &&
                stallFeed.View.Applied > appliedAtStall && stallFeed.View.StaleIgnored == 0,
                "停滞=" + stallFeed.Stalled +
                "；原因=" + ViewSourceDecision.Describe(stallFeed.FallbackReason) +
                "；源=" + stallFeed.Source + "；Applied " + appliedAtStall + "→" + stallFeed.View.Applied +
                "；" + stallFeed.Describe());

            Check("§S4e5⭐⭐ 停滞这一段**只算 1 个回退事件**（帧数 = 从判停那一帧起每帧 +1 = N+2−N+1）",
                stallFeed.FallbackEvents == 1 &&
                stallFeed.FallbackFrames == stallRefreshes - PresentationFeed.StallRefreshThreshold + 1,
                "事件=" + stallFeed.FallbackEvents + " 帧=" + stallFeed.FallbackFrames +
                "（期望帧 " + (stallRefreshes - PresentationFeed.StallRefreshThreshold + 1) + "）");

            // ⭐ §S4e6（必修 2）：回锁步要**滞回** —— 前进 **1** 帧不够（否则 30/1/30 抖动会稳定地闪）。
            bool stallFirstAdvance = stallFeed.Refresh(
                1, MakeServerSnapshotView(200, 7, 50, 1), stallWorld, 6, true);

            Check("§S4e6⭐⭐ **滞回**：判停后只前进 1 帧（5 → 6）⇒ **仍在回退**（`Stalled` 仍是 true、数据源还是快照）",
                stallFirstAdvance && stallFeed.Stalled &&
                stallFeed.Source == EViewSource.Snapshot &&
                stallFeed.FallbackReason == EFallbackReason.Stalled &&
                stallFeed.StallRecoverProgress == 1 &&
                stallFeed.View.Find(1) == null && stallFeed.View.Find(7) != null,
                "前进 1 帧后：停滞=" + stallFeed.Stalled +
                "；恢复进度=" + stallFeed.StallRecoverProgress + "/" + PresentationFeed.StallRecoverThreshold +
                "；源=" + stallFeed.Source + "；" + stallFeed.Describe());

            bool stallAdvance2 = stallFeed.Refresh(
                1, MakeServerSnapshotView(201, 7, 50, 1), stallWorld, 7, true);
            bool stallAdvance3 = stallFeed.Refresh(
                1, MakeServerSnapshotView(202, 7, 50, 1), stallWorld, 8, true);
            bool stallAdvance4 = stallFeed.Refresh(
                1, MakeServerSnapshotView(203, 7, 50, 1), stallWorld, 9, true);

            Check("§S4e6⭐ 滞回：连续 " + PresentationFeed.StallRecoverThreshold +
                  " 次真前进（6→7→8→9）⇒ 才回到锁步、`Stalled` 复位（不用人点、也不用重开一局）",
                stallAdvance2 && stallAdvance3 && stallAdvance4 &&
                !stallFeed.Stalled && stallFeed.Source == EViewSource.Lockstep &&
                stallFeed.FallbackReason == EFallbackReason.None &&
                stallFeed.View.Find(1) != null && stallFeed.View.Find(7) == null,
                "回锁步=" + stallAdvance4 + "；停滞=" + stallFeed.Stalled +
                "；" + stallFeed.Describe());

            // ================================================================
            //  ③ ⭐ 空世界不盖：`world` 非 null 但 **0 实体** ⇒ 不许拿它盖掉快照
            //     （上一版只判 `!= null` ⇒ 空快照会把 `View` 清空，而且不报错）
            // ================================================================
            var emptyFeed = new PresentationFeed();
            SnapshotView emptyServer = MakeServerSnapshotView(20, 7, 50, 1);

            bool emptyFirstFed = emptyFeed.Refresh(1, emptyServer, null, -1, false);
            long emptyTick = emptyFeed.View.ServerTick;

            emptyFeed.UseLockstep = true;

            var emptyWorld = new WorldState();          // 非 null，但**一个实体都没有**
            bool emptyFed = emptyFeed.Refresh(1, emptyServer, emptyWorld, 0, true);

            Check("§S4e5⭐ **0 实体的世界不算可用**：不喂空快照、单位 7 的 HP 50 **仍在**、回退原因 = 世界不可用",
                emptyFirstFed && !emptyFed &&
                emptyFeed.FallbackReason == EFallbackReason.WorldUnusable &&
                emptyFeed.FallbackEvents == 1 &&
                emptyFeed.Source == EViewSource.Snapshot && emptyFeed.Chosen == EViewSource.Snapshot &&
                emptyFeed.View.EntityCount == 1 && emptyFeed.View.Find(7) != null &&
                emptyFeed.View.Find(7).Hp == 50 && emptyFeed.View.ServerTick == emptyTick &&
                emptyFeed.View.StaleIgnored == 0,
                "喂进去了=" + emptyFed +
                "；原因=" + ViewSourceDecision.Describe(emptyFeed.FallbackReason) +
                "；实体=" + emptyFeed.View.EntityCount + "；" + emptyFeed.Describe());

            // ================================================================
            //  ④ 只读：喂之前 / 喂之后 `WorldStateHash.Compute(world)` **必须相同**
            //     （上一版这条只靠人读代码 —— 现在钉进探针，反手就能红）
            // ================================================================
            var readOnlyWorld = new WorldState();
            readOnlyWorld.Add(1, new FixVector3(1, 0, 2), 100);
            readOnlyWorld.Add(9002, new FixVector3(-3, 0, 0), 60);
            readOnlyWorld.SetTick(7);

            ulong hashBefore = WorldStateHash.Compute(readOnlyWorld);

            var readOnlyFeed = new PresentationFeed();
            readOnlyFeed.UseLockstep = true;
            SnapshotView readOnlyServer = MakeServerSnapshotView(300, 7, 50, 1);

            for (int i = 0; i < 20; i++)
            {
                readOnlyFeed.Refresh(1, readOnlyServer, readOnlyWorld, 7 + i, true);   // 真喂 20 帧
            }

            readOnlyFeed.Refresh(1, readOnlyServer, null, -1, true);                    // 「世界不可用」那条回退也走一遍
            readOnlyFeed.Refresh(1, readOnlyServer, readOnlyWorld, 26, false);          // 「没开局」那条也走一遍

            ulong hashAfter = WorldStateHash.Compute(readOnlyWorld);

            Check("§S4e5⭐ **只读**：20 帧锁步 + 两条回退走完之后，`WorldStateHash` / `Tick` / 实体数**一位都没变**",
                hashBefore == hashAfter && readOnlyWorld.Tick == 7 && readOnlyWorld.Count == 2 &&
                readOnlyFeed.FramesFromLockstep == 20,
                "前=" + hashBefore + " 后=" + hashAfter + " tick=" + readOnlyWorld.Tick +
                " 实体=" + readOnlyWorld.Count);

            // ================================================================
            //  ⑤ 零分配：warm-up 之后 1000 次 `Refresh`（锁步已开局）⇒ **0 字节**
            //     （上一版每帧 `world.SnapshotSorted()` ⇒ 每帧 new 一个 `SimEntity[]`）
            // ================================================================
            var allocFeed = new PresentationFeed();
            allocFeed.UseLockstep = true;

            var allocWorld = new WorldState();
            allocWorld.Add(1, new FixVector3(2, 0, 3), 100);
            allocWorld.Add(2, new FixVector3(5, 0, 1), 80);
            allocWorld.Add(3, new FixVector3(0, 0, 0), 60);

            SnapshotView allocServer = MakeServerSnapshotView(1, 7, 50, 1);

            // warm-up：池子/List 容量/排序路径涨到位，顺便让 JIT 从 tier0 升上去（否则测的是"编译分配"）
            for (int i = 0; i < 600; i++)
            {
                allocFeed.Refresh(1, allocServer, allocWorld, i, true);
            }

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 600; i < 1600; i++)
            {
                allocFeed.Refresh(1, allocServer, allocWorld, i, true);
            }

            long allocatedDelta = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Check("§S4e5⭐⭐ **稳态零分配**：warm-up 后 1000 次 `Refresh`（锁步已开局）⇒ 分配 **0 字节**",
                allocatedDelta == 0 && allocFeed.FramesFromLockstep == 1600,
                "1000 次分配=" + allocatedDelta + " 字节；采纳锁步=" + allocFeed.FramesFromLockstep);

            // ⭐ 快照那条也量一遍：它踩的是**同一个陷阱**（`RepeatedField<T>.Clear()` 对非空字段实测分配
            //    88 字节/次）—— 只量锁步那条的话，快照那条照样会偷偷每帧分配。
            var snapFeed = new PresentationFeed();

            var snapSnapshot = new WorldSnapshot();
            snapSnapshot.Entities.Add(new EntitySnapshot
            {
                EntityId = 7,
                Kind = 0,
                Hp = 50,
                MaxHp = 50,
                Alive = true,
                OwnerPlayerId = 1,
                PosXMm = 1000,
                PosZMm = 2000,
            });

            var snapView = new SnapshotView();

            for (int i = 0; i < 600; i++)           // warm-up
            {
                snapSnapshot.ServerTick = i + 1;
                snapView.Apply(snapSnapshot);
                snapFeed.Refresh(1, snapView, null, -1, false);
            }

            long snapAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 600; i < 1600; i++)
            {
                snapSnapshot.ServerTick = 1000 + i;
                snapView.Apply(snapSnapshot);
                snapFeed.Refresh(1, snapView, null, -1, false);
            }

            long snapAllocatedDelta = GC.GetAllocatedBytesForCurrentThread() - snapAllocatedBefore;

            Check("§S4e5⭐⭐ **快照那条也零分配**：warm-up 后 1000 次 `Refresh`（关 / 快照源）⇒ 分配 **0 字节**",
                snapAllocatedDelta == 0 && snapFeed.FramesFromSnapshot == 1600,
                "1000 次分配=" + snapAllocatedDelta + " 字节；采纳快照=" + snapFeed.FramesFromSnapshot);


            // ================================================================
            //  ⑥ 换局：新 `WorldState` + 帧号回 0 ⇒ 仍能喂进去
            //     **并且**：同一个对象、帧号回到 0 也要能喂（上一版会把它当成"倒退"挡掉）
            // ================================================================
            var matchFeed = new PresentationFeed();
            matchFeed.UseLockstep = true;

            var matchA = new WorldState();
            matchA.Add(1, new FixVector3(0, 0, 0), 100);
            matchA.Add(9001, new FixVector3(1, 0, 1), 100);
            matchA.SetTick(0);

            SnapshotView matchServer = MakeServerSnapshotView(400, 7, 50, 1);

            matchFeed.Refresh(1, matchServer, matchA, 0, true);
            matchFeed.Refresh(1, matchServer, matchA, 1, true);

            var matchB = new WorldState();
            matchB.Add(1, new FixVector3(0, 0, 0), 100);
            matchB.Add(9002, new FixVector3(2, 0, 2), 100);
            matchB.SetTick(0);

            bool matchBFed = matchFeed.Refresh(1, matchServer, matchB, 0, true);     // 换对象 + 帧号回 0 = 新一局

            Check("§S4e5⭐ **换一局（换了对象 + 帧号回 0）** ⇒ 仍然喂得进去（适配器跟着换）",
                matchBFed && matchFeed.FramesFromLockstep == 3 && matchFeed.View.Find(9002) != null &&
                matchFeed.View.Find(9001) == null && matchFeed.View.StaleIgnored == 0,
                "喂进去了=" + matchBFed + "；" + matchFeed.Describe());

            bool sameObjectAdvanced = matchFeed.Refresh(1, matchServer, matchB, 2, true);
            bool sameObjectReset = matchFeed.Refresh(1, matchServer, matchB, 0, true);   // 同一对象、帧号回 0

            Check("§S4e5⭐ **同一个对象、帧号回到 0** ⇒ 也喂得进去（`frame < 上次` 同样算换了一局）",
                sameObjectAdvanced && sameObjectReset && matchFeed.FramesFromLockstep == 5 &&
                matchFeed.View.EntityCount == 2 && matchFeed.View.StaleIgnored == 0,
                "中间帧=" + sameObjectAdvanced + " 回 0=" + sameObjectReset + "；" + matchFeed.Describe());
        }

        // ====================================================================
        //  【三十六】第二轮独立验收：第二局 / 滞回 / 零分配（实体数变化） / 排序同序
        // ====================================================================

        /// <summary>
        /// 【三十六】第二轮独立验收的 4 条（对应 `Docs\27` §35.9）。
        ///
        /// <para>四条都对应一类**静默**故障 —— 上一版**不会报红**，只有做成能直驱的检查才算修好：</para>
        /// <list type="number">
        /// <item>⭐⭐ **同一连接的第二局根本走不到**：`LockstepClient.ApplyStart` 拒绝第二次开局
        /// + `NetSession.EnsureLockstep` 只在 null 时新建 ⇒ 开局被拒、世界不换、帧号不复位
        /// ⇒ 新一局 `0..n` 全被"帧号不递增"挡掉 ⇒ **永远停滞、永远回退到快照**。
        /// 本节用**真 `NetSession` + 假传输**（喂真协议消息）把两局都跑完。</item>
        /// <item>⭐⭐ **停滞抖动**：退出回退只要 1 帧前进 ⇒「30 次回退 → 1 帧恢复 → 30 次回退」稳定成循环
        /// ⇒ `FallbackEvents` 每周期 +1（≈1800/分钟）、`View` 在两个世界间来回翻 ⇒ **画面闪烁**。
        /// 本节用 100 轮那个形状直驱，断言**回退事件只 +1**、且判停后 `View` **一次都没翻回锁步**。</item>
        /// <item>⭐ **零分配只验了"长大"**：warm-up 里实体数 3→2→4 交替（覆盖 `RemoveAt` 与"缩后重涨"），
        /// 稳态 1000 次 `Refresh` 的 `GC.GetAllocatedBytesForCurrentThread()` 差值必须**严格 0**。</item>
        /// <item>⭐ **`CopySortedTo` 与 `SnapshotSorted()` 同序**没有断言：用**乱序登记**（9001,1,9002,0）
        /// 造样本，逐元素比 Id / HP / 位置 / MaxHp / 冷却。</item>
        /// </list>
        /// </summary>
        private static void SecondMatchAndHysteresis_Checks()
        {
            Section_SecondMatchOverRealSession();
            Section_SameFrameNewMatch();
            Section_FlutterHysteresis();
            Section_ZeroAllocWithEntityCountChanges();
            Section_CopySortedToSameOrder();
        }

        /// <summary>
        /// ⭐ 小坑 5：**上一局末帧恰好是 0、新一局也从 0 起**那一格。
        /// <para>上一版只用 `frame < m_lastLockstepFrame` 判换局 ⇒ 这一格被当"重复帧"丢掉（新一局第 0 帧丢 1 帧，
        /// 而且只丢 1 帧 —— 最难看见的那种）。现在换局由**局号**（`NetSession.MatchSerial`）说明
        /// ⇒ 同一个世界对象、同一个帧号也能喂进去。</para>
        /// </summary>
        private static void Section_SameFrameNewMatch()
        {
            var feed = new PresentationFeed();
            feed.UseLockstep = true;

            var world = new WorldState();
            world.Add(1, new FixVector3(0, 0, 0), 100);

            bool first = feed.Refresh(1, null, world, 0, true, 1);      // 第一局：只喂到第 0 帧
            bool second = feed.Refresh(1, null, world, 0, true, 2);     // 第二局：同一个对象、也从 0 起

            Check("§S4e6⭐ **上一局末帧 0 / 新一局也从 0 起 + 局号变了 ⇒ 喂得进去**（上一版把这 1 帧当重复丢掉）",
                first && second && feed.FramesFromLockstep == 2 && feed.View.StaleIgnored == 0 &&
                feed.View.Find(1) != null,
                "第一帧=" + first + "；第二局第 0 帧=" + second +
                "；采纳锁步=" + feed.FramesFromLockstep + "；" + feed.Describe());

            // ⚠️ 反向：**同一局**里同一帧再喂一遍 ⇒ 仍然不重喂（不是"只要帧号相等就重来"）
            bool duplicate = feed.Refresh(1, null, world, 0, true, 2);

            Check("§S4e6⭐ 同局里同一帧再喂一遍 ⇒ 仍然**不重喂**（`frame <= 上次` 那条守卫还在）",
                !duplicate && feed.FramesFromLockstep == 2,
                "重复帧喂进去了=" + duplicate + "；采纳锁步=" + feed.FramesFromLockstep);
        }

        /// <summary>
        /// ⭐⭐ 必修 1：**真 `NetSession` 路径上的第二局**（假传输 → 真协议消息 → 真 `LockstepClient`）。
        /// <para>断言四件事：① 第二局开局被接受（新运行器、新世界）；② **每局计数清零**；
        /// ③ 第二局 `0..n` 帧**真的推进**（0 帧被拒）；④ 第二局自己的分歧/判定不一致只算第二局，
        /// 而且**事件转发跨局还是活的**（上一版把订阅挂到运行器上 ⇒ 第二局会静默失效）。</para>
        /// </summary>
        private static void Section_SecondMatchOverRealSession()
        {
            var fake = new FakeNetTransport();
            var session = new NetSession(fake, "第二局探针", "net-probe/second-match");

            session.Connect("127.0.0.1", 7799);
            session.Pump(0);
            fake.Push(MakeHandshakeAck(21));
            session.Pump(0);

            Check("§S4e6 阳性对照：会话已 Online（否则下面的业务消息全被忽略）",
                session.IsOnline, "状态=" + session.State);

            int desyncEvents = 0;
            int mismatchEvents = 0;

            session.DesyncDetected += d => desyncEvents++;
            session.MatchMismatchDetected += m => mismatchEvents++;

            // ---- 第一局：4 人名单、开局、喂 4 帧、造 1 条分歧 + 1 条判定不一致 ----
            var rosterA = new List<long> { 100, 101, 102, 103 };
            fake.Push(new ServerMessage { RoomState = MakeRoomState("probe-match", rosterA) });
            session.Pump(0);

            var entitiesA = new List<SimEntity>
            {
                MakeEntity(100, 0, 100),
                MakeEntity(101, 4, 100),
                MakeEntity(102, -4, 100),
                MakeEntity(103, 8, 100),
            };

            WorldState serverWorldA = BuildServerWorld(entitiesA);
            fake.Push(new ServerMessage { LockstepStart = MakeLockstepStart(entitiesA, serverWorldA, 0) });
            session.Pump(0);

            LockstepClient clientA = session.Lockstep;

            Check("§S4e6 阳性对照：第一局开局成功（`Started`、局号 1、世界 4 人）",
                clientA != null && clientA.Started && session.MatchSerial == 1 &&
                clientA.World != null && clientA.World.Count == 4,
                clientA == null ? "没有运行器" : clientA.Describe());

            var inputsA = new List<SimInput>
            {
                new SimInput { EntityId = 100, MoveDirection = new FixVector3(1, 0, 0), AttackTargetId = 0 },
                new SimInput { EntityId = 101, MoveDirection = FixVector3.Zero, AttackTargetId = 0 },
                new SimInput { EntityId = 102, MoveDirection = FixVector3.Zero, AttackTargetId = 0 },
                new SimInput { EntityId = 103, MoveDirection = FixVector3.Zero, AttackTargetId = 0 },
            };

            const int framesA = 4;

            for (int f = 0; f < framesA; f++)
            {
                fake.Push(new ServerMessage { LockstepFrame = MakeLockstepFrame(serverWorldA, f, inputsA) });
                session.Pump(0);
            }

            // 分歧：一条**新的**帧（帧号 framesA），但把 `server_hash` 改一位
            LockstepFrame tamperedA = MakeLockstepFrame(serverWorldA, framesA, inputsA);
            tamperedA.ServerHash ^= 1UL;
            fake.Push(new ServerMessage { LockstepFrame = tamperedA });
            session.Pump(0);

            // 判定不一致：客户端复算 = **未结束**（0 击杀、tick 远小于 1800），服务端却宣布 A 队胜
            var resultA = new MatchResult();
            resultA.Frame = framesA + 1;
            resultA.Outcome = MatchRules.ToNumber(EMatchOutcome.TeamA);
            resultA.KillsA = 0;
            resultA.KillsB = 0;
            fake.Push(new ServerMessage { MatchResult = resultA });
            session.Pump(0);

            Check("§S4e6 阳性对照：第一局留下了 1 条分歧 + 1 条判定不一致（第二局必须把它们清零）",
                session.DesyncCount == 1 && session.MatchMismatchCount == 1 &&
                desyncEvents == 1 && mismatchEvents == 1,
                "分歧=" + session.DesyncCount + "；不一致=" + session.MatchMismatchCount +
                "；事件 分歧/不一致=" + desyncEvents + "/" + mismatchEvents +
                "；" + (session.Lockstep == null ? "没有运行器" : session.Lockstep.Describe()));

            // ---- 第二局：**同一连接**、新名单（3 人）、新世界 ----
            var rosterB = new List<long> { 200, 201, 202 };
            fake.Push(new ServerMessage { RoomState = MakeRoomState("probe-match", rosterB) });
            session.Pump(0);

            var entitiesB = new List<SimEntity>
            {
                MakeEntity(200, 1, 100),
                MakeEntity(201, 2, 100),
                MakeEntity(202, 3, 100),
            };

            WorldState serverWorldB = BuildServerWorld(entitiesB);
            fake.Push(new ServerMessage { LockstepStart = MakeLockstepStart(entitiesB, serverWorldB, 0) });
            session.Pump(0);

            LockstepClient clientB = session.Lockstep;

            Check("§S4e6⭐⭐ **第二局开局被接受**（上一版 `ApplyStart` 直接拒绝）：新运行器、`Started`、局号 2、世界 3 人",
                clientB != null && clientB.Started && !ReferenceEquals(clientA, clientB) &&
                session.MatchSerial == 2 && clientB.World != null && clientB.World.Count == 3 &&
                !ReferenceEquals(clientA.World, clientB.World),
                "开局=" + (clientB != null && clientB.Started) + "；局号=" + session.MatchSerial +
                "；" + (clientB == null ? "没有运行器" : clientB.Describe()));

            Check("§S4e6⭐⭐ **每局计数清零**（本局语义）：第二局开局后 `DesyncCount` / `MatchMismatchCount` / `Outcome` 全归 0",
                session.DesyncCount == 0 && session.MatchMismatchCount == 0 && session.MatchOutcome == null &&
                clientB.RejectedFrames == 0 && clientB.StepsExecuted == 0 && clientB.LastSteppedFrame == -1,
                "分歧=" + session.DesyncCount + "；不一致=" + session.MatchMismatchCount +
                "；拒=" + clientB.RejectedFrames + "；推进=" + clientB.StepsExecuted +
                "；末帧=" + clientB.LastSteppedFrame);

            var inputsB = new List<SimInput>
            {
                // ⚠️ 只用**平面两轴**（X/Y）：wire 只有 `MoveXRaw` / `MoveYRaw`，`LockstepWire.ToPlainInput`
                //    把 Z 硬写成 0（本片是平面移动）。这里塞一个 Z 方向的输入 ⇒ 客户端收到的是 (0,1,0)
                //    ⇒ 两端 Step 的输入不同 ⇒ 每帧都报假分歧（我自己踩过，见 §35.9）。
                new SimInput { EntityId = 200, MoveDirection = new FixVector3(0, 1, 0), AttackTargetId = 0 },
                new SimInput { EntityId = 201, MoveDirection = FixVector3.Zero, AttackTargetId = 0 },
                new SimInput { EntityId = 202, MoveDirection = FixVector3.Zero, AttackTargetId = 0 },
            };

            const int framesB = 6;
            LockstepFrame lastFrameB = null;

            for (int f = 0; f < framesB; f++)
            {
                lastFrameB = MakeLockstepFrame(serverWorldB, f, inputsB);
                fake.Push(new ServerMessage { LockstepFrame = lastFrameB });
                session.Pump(0);
            }

            Check("§S4e6⭐⭐ **第二局真的能推进**：从第 0 帧起喂 " + framesB + " 帧 ⇒ 推进 " + framesB +
                  " 帧、末帧 " + (framesB - 1) + "、**0 帧被拒**（上一版全被「帧号不递增」挡掉）",
                clientB.StepsExecuted == framesB && clientB.LastSteppedFrame == framesB - 1 &&
                clientB.RejectedFrames == 0 && session.DesyncCount == 0,
                "推进=" + clientB.StepsExecuted + "；末帧=" + clientB.LastSteppedFrame +
                "；拒=" + clientB.RejectedFrames + "；" + clientB.Describe());

            Check("§S4e6⭐ 第二局的哈希也对得上（客户端 `World` 终态 == 服务端那份 ⇒ 不是「推进了但算错」）",
                clientB.World != null &&
                WorldStateHash.Compute(clientB.World) == WorldStateHash.Compute(serverWorldB),
                clientB.World == null
                    ? "没有世界"
                    : (WorldStateHash.Compute(clientB.World) + " vs " + WorldStateHash.Compute(serverWorldB)));

            // ⭐ "不重复喂"的守卫：同一帧再喂一遍 ⇒ 只 +1 拒、不 Step
            fake.Push(new ServerMessage { LockstepFrame = lastFrameB });
            session.Pump(0);

            Check("§S4e6⭐ **不重复喂**：第二局最后一帧再喂一遍 ⇒ `StepsExecuted` / `LastSteppedFrame` 不动、只 +1 拒",
                clientB.StepsExecuted == framesB && clientB.LastSteppedFrame == framesB - 1 &&
                clientB.RejectedFrames == 1,
                "推进=" + clientB.StepsExecuted + "；末帧=" + clientB.LastSteppedFrame +
                "；拒=" + clientB.RejectedFrames);

            // ---- 第二局自己的账：分歧 1（不是 2）、判定不一致 1（不是 2），事件跨局仍然转发 ----
            LockstepFrame tamperedB = MakeLockstepFrame(serverWorldB, framesB, inputsB);
            tamperedB.ServerHash ^= 1UL;
            fake.Push(new ServerMessage { LockstepFrame = tamperedB });
            session.Pump(0);

            var resultB = new MatchResult();
            resultB.Frame = framesB + 1;
            resultB.Outcome = MatchRules.ToNumber(EMatchOutcome.TeamB);
            resultB.KillsA = 0;
            resultB.KillsB = 0;
            fake.Push(new ServerMessage { MatchResult = resultB });
            session.Pump(0);

            Check("§S4e6⭐⭐ 第二局自己的分歧/判定不一致各 1（**不是 2**）⇒ 上一局的账没跟着走；" +
                  "而且**事件转发跨局还是活的**（分歧 2 次、不一致 2 次）",
                session.DesyncCount == 1 && session.MatchMismatchCount == 1 &&
                desyncEvents == 2 && mismatchEvents == 2,
                "分歧=" + session.DesyncCount + "；不一致=" + session.MatchMismatchCount +
                "；事件 分歧/不一致=" + desyncEvents + "/" + mismatchEvents +
                "；" + (session.Lockstep == null ? "没有运行器" : session.Lockstep.Describe()));

            session.Dispose();
        }

        /// <summary>
        /// ⭐⭐ 必修 2：**停滞滞回**。抖动形状 = 100 轮「`StallRefreshThreshold` 次 frame=5 + 1 次 frame=6」。
        /// <para>无滞回时：每轮「判停 → 1 帧前进就恢复 → 再判停」⇒ `FallbackEvents` ≈ 100、
        /// `View` 在两个世界间来回翻（闪烁）。有滞回时：**只 +1**，且判停后 `View` **一次都没翻回锁步**。</para>
        /// </summary>
        private static void Section_FlutterHysteresis()
        {
            var feed = new PresentationFeed();
            feed.UseLockstep = true;

            var world = new WorldState();
            world.Add(1, new FixVector3(5, 0, 5), 100);
            world.SetTick(5);

            int snapshotTick = 1;

            // 第一帧：锁步喂一次（阳性对照：它真的用了锁步世界）
            feed.Refresh(1, MakeServerSnapshotView(snapshotTick++, 7, 50, 1), world, 5, true, 1);

            long switchesAtStall = -1;
            long lockstepFramesAtStall = -1;

            const int rounds = 100;
            int refreshes = 0;

            for (int round = 0; round < rounds; round++)
            {
                for (int i = 0; i < PresentationFeed.StallRefreshThreshold; i++)
                {
                    feed.Refresh(1, MakeServerSnapshotView(snapshotTick++, 7, 50, 1), world, 5, true, 1);
                    refreshes++;

                    if (feed.Stalled && switchesAtStall < 0)
                    {
                        // 判停那一刻的现场（后面的 100 轮绝不许离开这里）
                        switchesAtStall = feed.SourceSwitches;
                        lockstepFramesAtStall = feed.FramesFromLockstep;
                    }
                }

                feed.Refresh(1, MakeServerSnapshotView(snapshotTick++, 7, 50, 1), world, 6, true, 1);
                refreshes++;
            }

            Check("§S4e6⭐⭐ **抖动下 `FallbackEvents` 只 +1**（" + rounds +
                  " 轮「" + PresentationFeed.StallRefreshThreshold + "×frame=5 + 1×frame=6」⇒ 无滞回时约 " + rounds + " 次）",
                feed.FallbackEvents == 1 && feed.Stalled &&
                feed.Source == EViewSource.Snapshot && feed.FallbackReason == EFallbackReason.Stalled,
                "事件=" + feed.FallbackEvents + "；停滞=" + feed.Stalled +
                "；源=" + feed.Source + "；" + feed.Describe());

            Check("§S4e6⭐⭐ **`View` 判停后一次都没翻回锁步世界**（换源次数与采纳锁步数在判停后都不再变）",
                switchesAtStall > 0 && lockstepFramesAtStall > 0 &&
                feed.SourceSwitches == switchesAtStall &&
                feed.FramesFromLockstep == lockstepFramesAtStall &&
                feed.FallbackFrames == refreshes - PresentationFeed.StallRefreshThreshold + 1,
                "换源 " + switchesAtStall + "→" + feed.SourceSwitches +
                "；锁步采纳 " + lockstepFramesAtStall + "→" + feed.FramesFromLockstep +
                "；回退帧=" + feed.FallbackFrames + "（期望 " +
                (refreshes - PresentationFeed.StallRefreshThreshold + 1) + "）；" + feed.Describe());
        }

        /// <summary>
        /// ⭐ 必修 3.1：**零分配覆盖"实体数变化"**（3→2→4 交替 ⇒ 走到 `RemoveAt` 与"缩后重涨"）。
        /// <para>warm-up 那 600 帧就让实体数一直变（池子 / `RepeatedField` 容量 / `List` 容量涨到位），
        /// 再量 1000 帧：`GC.GetAllocatedBytesForCurrentThread()` 差值必须**严格 0**。</para>
        /// </summary>
        private static void Section_ZeroAllocWithEntityCountChanges()
        {
            var feed = new PresentationFeed();
            feed.UseLockstep = true;

            var world = new WorldState();
            SetWorldEntityCount(world, 3);

            SnapshotView server = MakeServerSnapshotView(1, 7, 50, 1);

            const int warmup = 600;

            // warm-up：实体数 3 → 2 → 4 交替（**缩后重涨**也在里面）
            for (int i = 0; i < warmup; i++)
            {
                SetWorldEntityCount(world, CountByIndex(i));
                feed.Refresh(1, server, world, i, true, 1);
            }

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            int seenTwo = -1;
            int seenFour = -1;

            for (int i = warmup; i < warmup + 1000; i++)
            {
                int count = CountByIndex(i);
                SetWorldEntityCount(world, count);
                feed.Refresh(1, server, world, i, true, 1);

                if (count == 2)
                {
                    seenTwo = feed.View.EntityCount;
                }
                else if (count == 4)
                {
                    seenFour = feed.View.EntityCount;
                }
            }

            long allocatedDelta = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Check("§S4e6⭐⭐ **零分配覆盖实体数变化**：warm-up 后 1000 次 `Refresh`（3→2→4 交替、含 `RemoveAt`）⇒ 分配 **" +
                  allocatedDelta + " 字节**（期望 0）",
                allocatedDelta == 0 && feed.FramesFromLockstep == warmup + 1000 &&
                seenTwo == 2 && seenFour == 4,
                "分配=" + allocatedDelta + " 字节；采纳锁步=" + feed.FramesFromLockstep +
                "；2 人时表现层=" + seenTwo + "、4 人时=" + seenFour);
        }

        /// <summary>
        /// ⭐ 必修 3.2：`WorldState.CopySortedTo` 与 `SnapshotSorted()` **逐元素同序**。
        /// <para>样本用**乱序登记**（9001, 1, 9002, 0）⇒ 只要有"按登记顺序返回"那一类错误就红。
        /// 比 Id / HP / MaxHp / Position 三轴 raw / AttackReadyTick（不是只比 Id）。</para>
        /// </summary>
        private static void Section_CopySortedToSameOrder()
        {
            var world = new WorldState();

            // ⚠️ **乱序登记**（这正是要造的形状：9001 先进、0 最后进）
            world.Add(9001, new FixVector3(9, 0, 1), 30);
            world.Add(1, new FixVector3(1, 0, 2), 100);
            world.Add(9002, new FixVector3(-3, 0, -1), 0);
            world.Add(0, new FixVector3(0, 0, 7), 5);
            world.SetTick(3);

            // 让 MaxHp / 冷却与 Hp 不同（免得"只比 Hp"也能过）
            for (int i = 0; i < world.Count; i++)
            {
                SimEntity e = world.GetAt(i);
                e.MaxHp = e.Hp + 11;
                e.AttackReadyTick = e.Id % 7;
                world.SetAt(i, e);
            }

            SimEntity[] snapshot = world.SnapshotSorted();

            var copied = new List<SimEntity>();
            copied.Add(new SimEntity { Id = -12345 });          // 哨兵：证明 `CopySortedTo` 会**清空重填**
            int copiedCount = world.CopySortedTo(copied);

            bool same = copiedCount == snapshot.Length && copiedCount == 4 &&
                        copied[0].Id != -12345;

            if (same)
            {
                for (int i = 0; i < snapshot.Length; i++)
                {
                    if (copied[i].Id != snapshot[i].Id ||
                        copied[i].Hp != snapshot[i].Hp ||
                        copied[i].MaxHp != snapshot[i].MaxHp ||
                        copied[i].AttackReadyTick != snapshot[i].AttackReadyTick ||
                        copied[i].Position.X.RawValue != snapshot[i].Position.X.RawValue ||
                        copied[i].Position.Y.RawValue != snapshot[i].Position.Y.RawValue ||
                        copied[i].Position.Z.RawValue != snapshot[i].Position.Z.RawValue)
                    {
                        same = false;
                        break;
                    }
                }
            }

            bool ascending = snapshot.Length == 4 &&
                             snapshot[0].Id == 0 && snapshot[1].Id == 1 &&
                             snapshot[2].Id == 9001 && snapshot[3].Id == 9002;

            Check("§S4e6⭐ `CopySortedTo` 与 `SnapshotSorted()` **逐元素同序**（Id/HP/MaxHp/位置三轴/冷却；乱序登记 9001,1,9002,0）",
                same && ascending,
                "同序=" + same + "；升序=" + ascending + "；条数=" + copiedCount +
                "；Id 序列=" + DescribeEntityIds(copied));
        }

        /// <summary>warm-up / 测量两边共用的实体数节拍（3 → 2 → 4）。</summary>
        /// <param name="index">帧序号。</param>
        /// <returns>这一帧的实体数。</returns>
        private static int CountByIndex(int index)
        {
            int phase = index % 3;
            return phase == 0 ? 3 : (phase == 1 ? 2 : 4);
        }

        /// <summary>
        /// 把世界的实体数调成 `count`（**同一个世界对象**，只增删实体）。
        /// <para>⚠️ 必须用同一个对象：换世界对象会顺带换适配器 ⇒ 测到的就不是 `RemoveAt` 那条路径了。</para>
        /// </summary>
        /// <param name="world">世界。</param>
        /// <param name="count">要几个实体（1..4 号）。</param>
        private static void SetWorldEntityCount(WorldState world, int count)
        {
            for (int id = 4; id >= 1; id--)
            {
                if (id > count)
                {
                    world.Remove(id);
                }
            }

            for (int id = 1; id <= count; id++)
            {
                if (world.IndexOf(id) < 0)
                {
                    world.Add(id, new FixVector3(id, 0, id * 2), 100 - id);
                }
            }
        }

        /// <summary>把实体编号串成一行（失败时看得见实际序列）。</summary>
        /// <param name="entities">实体表。</param>
        /// <returns>例：`0,1,9001,9002`。</returns>
        private static string DescribeEntityIds(IReadOnlyList<SimEntity> entities)
        {
            var builder = new StringBuilder();

            for (int i = 0; i < entities.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(entities[i].Id);
            }

            return builder.ToString();
        }

        /// <summary>按实体表建一个服务端世界（`Server\Sim` 那一侧，用来算每帧 `server_hash`）。</summary>
        /// <param name="entities">实体表（含 MaxHp / 冷却）。</param>
        /// <returns>世界（tick 0）。</returns>
        private static WorldState BuildServerWorld(IReadOnlyList<SimEntity> entities)
        {
            var world = new WorldState();

            for (int i = 0; i < entities.Count; i++)
            {
                world.Add(entities[i].Id, entities[i].Position, entities[i].Hp);

                int index = world.IndexOf(entities[i].Id);
                SimEntity fixedUp = world.GetAt(index);
                fixedUp.MaxHp = entities[i].MaxHp;
                fixedUp.AttackReadyTick = entities[i].AttackReadyTick;
                world.SetAt(index, fixedUp);
            }

            world.SetTick(0);
            return world;
        }

        /// <summary>造一条 `lockstep_start`（哈希取服务端世界的，和真实广播同一种算法）。</summary>
        /// <param name="entities">开局实体表。</param>
        /// <param name="serverWorld">服务端世界（只用来算 `initial_hash`）。</param>
        /// <param name="frame">开局帧号。</param>
        /// <returns>消息。</returns>
        private static LockstepStart MakeLockstepStart(
            IReadOnlyList<SimEntity> entities, WorldState serverWorld, int frame)
        {
            var start = new LockstepStart();
            start.Frame = frame;
            start.InitialHash = WorldStateHash.Compute(serverWorld);

            for (int i = 0; i < entities.Count; i++)
            {
                var e = new LockstepStartEntity();
                e.EntityId = entities[i].Id;
                e.PosXRaw = entities[i].Position.X.RawValue;
                e.PosYRaw = entities[i].Position.Y.RawValue;
                e.PosZRaw = entities[i].Position.Z.RawValue;
                e.Hp = entities[i].Hp;
                e.MaxHp = entities[i].MaxHp;
                e.AttackReadyTick = entities[i].AttackReadyTick;
                start.Entities.Add(e);
            }

            return start;
        }

        /// <summary>
        /// 造一条 `lockstep_frame`：**先把服务端世界推一帧**，再按同一批输入写 wire 字段。
        /// <para>⚠️ 客户端的 `ApplyFrame` 只吃 `player_id` / 移动 raw / `target`（不吃 `buttons`），
        /// 所以这里逐字段照那三个写 ⇒ 两端 `Step` 的输入**完全相同**。</para>
        /// </summary>
        /// <param name="serverWorld">服务端世界（会被推进一帧）。</param>
        /// <param name="frame">帧号。</param>
        /// <param name="inputs">这一帧的输入。</param>
        /// <returns>消息。</returns>
        private static LockstepFrame MakeLockstepFrame(
            WorldState serverWorld, int frame, IReadOnlyList<SimInput> inputs)
        {
            WorldStep.Step(serverWorld, inputs, 1);

            var message = new LockstepFrame();
            message.Frame = frame;
            message.ServerHash = WorldStateHash.Compute(serverWorld);

            for (int i = 0; i < inputs.Count; i++)
            {
                var wire = new LockstepFrameInput();
                wire.PlayerId = inputs[i].EntityId;
                wire.MoveXRaw = inputs[i].MoveDirection.X.RawValue;
                wire.MoveYRaw = inputs[i].MoveDirection.Y.RawValue;
                wire.Buttons = 0;
                wire.TargetEntityId = inputs[i].AttackTargetId;
                wire.Missing = false;
                message.Inputs.Add(wire);
            }

            return message;
        }

        /// <summary>造一份席位表（队伍映射由它推出来 —— 判胜负要用）。</summary>
        /// <param name="roomId">房号。</param>
        /// <param name="playerIds">成员编号。</param>
        /// <returns>消息。</returns>
        private static RoomState MakeRoomState(string roomId, IReadOnlyList<long> playerIds)
        {
            var state = new RoomState();
            state.RoomId = roomId;
            state.DungeonId = 1001;
            state.Capacity = 4;

            for (int i = 0; i < playerIds.Count; i++)
            {
                var member = new RoomMember();
                member.PlayerId = playerIds[i];
                member.PlayerName = "p" + playerIds[i];
                member.IsHost = i == 0;
                member.Ready = true;
                state.Members.Add(member);
            }

            return state;
        }

        /// <summary>造一个「可用」的锁步世界（1 个实体、tick 0）—— 【三十五】的纯决策检查用。</summary>
        /// <returns>世界。</returns>
        private static WorldState MakeOneEntityWorld()
        {
            var world = new WorldState();
            world.Add(1, new FixVector3(0, 0, 0), 100);
            world.SetTick(0);
            return world;
        }


        /// <summary>
        /// 造一份「服务端快照的本地副本」（`SnapshotView` 只能靠 `Apply` 填 ⇒ 这里喂一张真的）。
        /// <para>⚠️ `WorldSnapshot.ServerTick` 是 **`int`**（协议里那个字段），而 `SnapshotView.ServerTick` 是 `long`。</para>
        /// </summary>
        /// <param name="tick">服务端 tick。</param>
        /// <param name="entityId">单位编号。</param>
        /// <param name="hp">血量（`&lt;= 0` ⇒ 死）。</param>
        /// <param name="ownerPlayerId">`owner_player_id`。</param>
        /// <returns>副本。</returns>
        private static SnapshotView MakeServerSnapshotView(int tick, int entityId, int hp, long ownerPlayerId)
        {
            var view = new SnapshotView();
            var snapshot = new WorldSnapshot();

            snapshot.ServerTick = tick;
            snapshot.Entities.Add(new EntitySnapshot
            {
                EntityId = entityId,
                Kind = 0,
                Hp = hp,
                MaxHp = hp,
                Alive = hp > 0,
                OwnerPlayerId = ownerPlayerId,
                PosXMm = 1000,
                PosZMm = 2000,
            });

            view.Apply(snapshot);
            return view;
        }

        /// <summary>收过几条 `lockstep_frame`（【三十三】用）。</summary>
        /// <param name="client">客户端。</param>
        /// <returns>条数。</returns>
        private static int CountLockstepFrames(Client client)
        {
            int count = 0;

            for (int i = 0; i < client.Received.Count; i++)
            {
                if (client.Received[i].LockstepFrame != null)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>最后一条 `lockstep_frame` 的帧号（一条都没收到返回 -1）。</summary>
        /// <param name="client">客户端。</param>
        /// <returns>帧号。</returns>
        private static int LastLockstepFrame(Client client)
        {
            for (int i = client.Received.Count - 1; i >= 0; i--)
            {
                if (client.Received[i].LockstepFrame != null)
                {
                    return client.Received[i].LockstepFrame.Frame;
                }
            }

            return -1;
        }

        /// <summary>
        /// 【S4e3】**端到端**：`NetSession` + **真 `TcpTransport`** 连**真 Host**（不直驱、不假传输）。
        ///
        /// <para>⚠️ 三态：**未启用** / **不可达** / **通过**。前两种都打
        /// <c>[SKIP] 端到端：未启用/不可达 ⇒ 本节未验</c>，且**一条都不计入「通过」数**
        /// （不许静默跳过，更不许算绿）。</para>
        /// <para>⚠️ 断言**全部**用协议消息 / 对象状态，**不抓日志中文**（W26）。</para>
        /// <para>⚠️ Host 由**外部**起：`NBC.Server.Host.exe --port=9000 --dev-accounts --fill-seats=4`。</para>
        /// </summary>
        /// <param name="enabled">是否带 `--e2e`。</param>
        /// <param name="port">端口（`--e2e-port`，默认 9000）。</param>
        private static void EndToEnd_LockstepOverRealSocket(bool enabled, int port)
        {
            const string skipLine = "[SKIP] 端到端：未启用/不可达 ⇒ 本节未验";

            if (!enabled)
            {
                Console.WriteLine(skipLine);
                Console.WriteLine("       （启用方式：先起 Host `NBC.Server.Host.exe --port=" + port +
                                  " --dev-accounts --fill-seats=4`，再跑本探针加 `--e2e`）");
                Console.WriteLine("端到端：**0 验 / 1 未验**（未计入通过数）");
                return;
            }

            var transport = new TcpTransport();
            var session = new NetSession(
                transport, "e2e探针", "net-probe/e2e", account: "dev1", password: "dev-pw1");

            var clock = new System.Diagnostics.Stopwatch();
            clock.Start();

            session.Connect("127.0.0.1", port);

            // ---- ① 等 Online（握手 + 登录）最多 10s ----
            while (clock.ElapsedMilliseconds < 10000 && !session.IsOnline)
            {
                session.Pump(16);
                System.Threading.Thread.Sleep(5);
            }

            if (!session.IsOnline)
            {
                // ⚠️ 连不上 = **未验**，不是失败（Host 没起不该把探针判红）
                Console.WriteLine(skipLine + "（10 秒内没连上 127.0.0.1:" + port +
                                  "；最后状态 " + session.State + "）");
                Console.WriteLine("端到端：**0 验 / 1 未验**（未计入通过数）");
                session.Disconnect("e2e 探测结束（未连上）");
                return;
            }

            Check("[E2E] 连上真 Host 且**用调试账号 `dev1` 登录成功**（playerId > 0，W15）",
                session.PlayerId > 0, "playerId=" + session.PlayerId);

            // ---- ② 加入房间（房号留空 = 让服务端安排；`--fill-seats=4` 会补足到 4 席位）----
            // ⚠️ 副本号**必须是合法的 1001**（`Dungeon` 表里有的那行）。
            //    原来这里写的是 `0` ⇒ 服务端建战斗世界时抛异常 ⇒ **整个 Host 死**，
            //    这条端到端就「永远撞在崩溃上」（§三十六 P0 的现场证据就是这么来的）。
            session.JoinRoom(string.Empty, 1001);

            // ---- ③ 等锁步开局 + ≥10 帧；总超时 60s（⚠️ 超时先打状态再失败）----
            clock.Restart();
            LockstepClient? client = null;

            while (clock.ElapsedMilliseconds < 60000)
            {
                session.Pump(16);
                System.Threading.Thread.Sleep(5);

                client = session.Lockstep;

                if (client != null && client.Started && client.StepsExecuted >= 10)
                {
                    break;
                }
            }

            client = session.Lockstep;

            // ⚠️ **诊断信息先打**（别再挂住不出声）
            Console.WriteLine("[E2E] 观察到的状态：" +
                              "房间成员=" + (session.CurrentRoom == null ? -1 : session.CurrentRoom.Members.Count) +
                              "；运行器=" + (client == null ? "未创建" : "已创建") +
                              "；已开局=" + (client != null && client.Started) +
                              "；收帧数=" + (client == null ? 0 : client.StepsExecuted) +
                              "；最后帧号=" + (client == null ? -1 : client.LastSteppedFrame) +
                              "；分歧=" + (client == null ? -1 : client.DesyncCount) +
                              "；会话状态=" + session.State);

            if (client == null || !client.Started)
            {
                Check("[E2E] 收到 `LockstepStart` 并且**开局成功**（`initial_hash` == 客户端自算 —— `ApplyStart` 的核心就是比它）",
                    client != null && client.Started,
                    "运行器=" + (client == null ? "未创建" : client.Describe()));
                Console.WriteLine("端到端：**1 验 / 0 未验**（**未通过**）");
                session.Disconnect("e2e 探测结束（未开局）");
                return;
            }

            Check("[E2E] ⭐ `LockstepStart` 已收到且**开局成功**（⇒ `initial_hash` == 客户端按同一份实体自算）",
                client.Started, client.Describe());

            // 世界实体：4 个、id 升序、**与 `room_state` 的成员集合相同**（= 服务端开局名单）
            SimEntity[] world = client.World == null ? new SimEntity[0] : client.World.SnapshotSorted();

            bool ascending = true;

            for (int i = 1; i < world.Length; i++)
            {
                if (world[i].Id <= world[i - 1].Id)
                {
                    ascending = false;
                }
            }

            Check("[E2E] ⭐ 世界有 **4 个实体**且 id **严格升序**",
                world.Length == 4 && ascending, "实体数=" + world.Length + "；升序=" + ascending);

            // ⭐ 客户端从 `room_state` 推的名单 == 世界里的实体集合（服务端开局用的那份）
            var fromRoom = new List<long>();
            var fromWorld = new List<long>();

            if (session.CurrentRoom != null)
            {
                for (int i = 0; i < session.CurrentRoom.Members.Count; i++)
                {
                    fromRoom.Add(session.CurrentRoom.Members[i].PlayerId);
                }
            }

            for (int i = 0; i < world.Length; i++)
            {
                fromWorld.Add(world[i].Id);
            }

            fromRoom.Sort();
            fromWorld.Sort();

            bool sameRoster = fromRoom.Count == fromWorld.Count && fromRoom.Count == 4;

            if (sameRoster)
            {
                for (int i = 0; i < fromRoom.Count; i++)
                {
                    if (fromRoom[i] != fromWorld[i])
                    {
                        sameRoster = false;
                    }
                }
            }

            Check("[E2E] ⭐ **客户端从 `room_state` 推的名单 == 开局世界里的实体集合**（= 服务端 `RosterIds` 那份）",
                sameRoster,
                "room_state=" + string.Join(",", fromRoom.ConvertAll(x => x.ToString()).ToArray()) +
                " 世界=" + string.Join(",", fromWorld.ConvertAll(x => x.ToString()).ToArray()));

            // ⭐ 帧数 / 单调 / 分歧 / 未开局忽略
            Check("[E2E] ⭐ **≥10 条 `LockstepFrame`** 且推进次数在涨",
                client.StepsExecuted >= 10, "收帧数=" + client.StepsExecuted);

            Check("[E2E] ⭐ 帧号**严格单调递增**（没有任何一帧被拒 ⇒ 没有重复/倒退）",
                client.RejectedFrames == 0 && client.LastSteppedFrame >= client.StepsExecuted - 1,
                "末帧=" + client.LastSteppedFrame + "；推进=" + client.StepsExecuted +
                "；被拒=" + client.RejectedFrames);

            Check("[E2E] ⭐ **`DesyncCount == 0`**（客户端自算的哈希与服务端广播的逐帧一致）",
                client.DesyncCount == 0, "分歧=" + client.DesyncCount + "；" + client.Describe());

            Check("[E2E] ⭐ **`IgnoredBeforeStart == 0`**（没有「开局前就收到帧」⇒ 开局时序正确）",
                client.IgnoredBeforeStart == 0, "未开局忽略=" + client.IgnoredBeforeStart);

            // ⭐ 通过时把**观察值**打出来（给负责人手动判据对照）
            var team = new SortedTeamMap(fromRoom);
            int a = 0;
            int b = 0;

            for (int i = 0; i < fromRoom.Count; i++)
            {
                if (team.TeamOf((int)fromRoom[i]) == 0)
                {
                    a++;
                }
                else if (team.TeamOf((int)fromRoom[i]) == 1)
                {
                    b++;
                }
            }

            Console.WriteLine("=== 端到端观察值 ===");
            Console.WriteLine("  收帧数=" + client.StepsExecuted +
                              "；帧号区间=[" + (client.StepsExecuted > 0 ? client.LastSteppedFrame - client.StepsExecuted + 1 : -1) +
                              ".." + client.LastSteppedFrame + "]");
            Console.WriteLine("  分歧计数=" + client.DesyncCount +
                              "；未开局忽略=" + client.IgnoredBeforeStart +
                              "；重复/倒退被拒=" + client.RejectedFrames);
            Console.WriteLine("  房间成员=" + fromRoom.Count + "（A 队 " + a + " / B 队 " + b + "）" +
                              "；世界实体=" + world.Length + "（id " +
                              string.Join(",", fromWorld.ConvertAll(x => x.ToString()).ToArray()) + "）");
            Console.WriteLine("  一句话：" + client.Describe());
            Console.WriteLine("端到端：**8 验 / 0 未验**");

            session.Disconnect("e2e 探测结束（通过）");
        }

        private static QuestTables LoadQuestTables()
        {
            string dir;

            if (!ServerTables.TryResolveConfigDir(out dir))
            {
                throw new InvalidOperationException("探针找不到配置表目录 `Configs\\Design`。");
            }

            QuestTables tables;
            string error;

            if (!QuestTables.TryLoad(dir, out tables, out error))
            {
                throw new InvalidOperationException("探针读不到任务/成就表（" + dir + "）：" + error);
            }

            return tables;
        }

        /// <summary>把若干条说明拼成一行（断言失败时看得到）。</summary>
        /// <param name="lines">说明。</param>
        /// <returns>拼接结果。</returns>
        private static string DescribeLines(List<string> lines)
        {
            return lines.Count == 0 ? "(没有说明)" : string.Join(" | ", lines);
        }

        /// <summary>
        /// 探针用的**假进度存放处**：内存里存，`LoadAsync`/`FlushAsync` 立即完成。
        /// <para>⚠️ 判定的"对不对"不归它验（那是共享层的 `ConditionTracker` 的事），
        /// 它只负责"服务端有没有把该记的记下来"。</para>
        /// </summary>
        private sealed class FakeProgressStore : IPlayerProgressStore
        {
            private readonly Dictionary<int, int> _values = new();

            /// <summary>存了几条。</summary>
            public int Count
            {
                get { return _values.Count; }
            }

            /// <summary>这个存放处属于谁（探针里不关心）。</summary>
            public long PlayerId
            {
                get { return 11; }
            }

            /// <summary>脏数据条数（内存实现永远是 0）。</summary>
            public int DirtyCount
            {
                get { return 0; }
            }

            /// <inheritdoc/>
            public int GetProgress(int conditionKey)
            {
                int value;
                return _values.TryGetValue(conditionKey, out value) ? value : 0;
            }

            /// <inheritdoc/>
            public void SetProgress(int conditionKey, int value)
            {
                _values[conditionKey] = value;
            }

            /// <inheritdoc/>
            public bool Remove(int conditionKey)
            {
                return _values.Remove(conditionKey);
            }

            /// <inheritdoc/>
            public Task<int> LoadAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                return Task.FromResult(0);
            }

            /// <inheritdoc/>
            public Task<int> FlushAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                return Task.FromResult(0);
            }

            /// <inheritdoc/>
            public string DescribeFlushStats()
            {
                return "假存放处：" + Count + " 条";
            }

            /// <inheritdoc/>
            public void Dispose()
            {
            }
        }

        /// <summary>探针用的**假发奖台账**（内存里记"发过哪些"）。</summary>
        private sealed class FakeRewardLedger : IPlayerRewardLedger
        {
            private readonly HashSet<string> _granted = new();

            private readonly List<string> _detail = new();

            /// <summary>发过几条。</summary>
            public int Count
            {
                get { return _granted.Count; }
            }

            /// <summary>这个台账属于谁。</summary>
            public long PlayerId
            {
                get { return 11; }
            }

            /// <summary>脏数据条数。</summary>
            public int DirtyCount
            {
                get { return 0; }
            }

            /// <inheritdoc/>
            public bool HasGranted(NBC.Shared.Reward.ERewardOwnerKind kind, int ownerId)
            {
                return _granted.Contains(kind + ":" + ownerId);
            }

            /// <inheritdoc/>
            public void MarkGranted(NBC.Shared.Reward.ERewardOwnerKind kind, int ownerId, int rewardId)
            {
                if (_granted.Add(kind + ":" + ownerId))
                {
                    _detail.Add(ownerId + "→奖励" + rewardId);
                }
            }

            /// <summary>累计排进来的 exp（验"权威真的把发放排上了"）。</summary>
            public long PaidExp { get; private set; }

            /// <summary>累计排进来的 gold。</summary>
            public long PaidGold { get; private set; }

            /// <inheritdoc/>
            public void MarkGrantedWithPayout(
                NBC.Shared.Reward.ERewardOwnerKind kind, int ownerId, int rewardId, int exp, int gold)
            {
                // 与真实现同一套幂等语义：已经有记录 ⇒ **连发放也不排**
                if (!_granted.Add(kind + ":" + ownerId))
                {
                    return;
                }

                _detail.Add(ownerId + "→奖励" + rewardId + "(exp+" + exp + ",gold+" + gold + ")");
                PaidExp += exp;
                PaidGold += gold;
            }

            /// <inheritdoc/>
            public Task<int> LoadAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                return Task.FromResult(0);
            }

            /// <inheritdoc/>
            public Task<int> FlushAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                return Task.FromResult(0);
            }

            /// <summary>一句人话（探针失败时看得到台账里有什么）。</summary>
            /// <returns>例：`9002→奖励5003`。</returns>
            public string Describe()
            {
                return _detail.Count == 0 ? "(空)" : string.Join(",", _detail);
            }

            /// <inheritdoc/>
            public string DescribeFlushStats()
            {
                return "假台账：" + Count + " 条";
            }

            /// <inheritdoc/>
            public void Dispose()
            {
            }
        }

        /// <summary>
        /// 服务端**没有账号表**时，带账号的握手必须**明确报错**，而不是悄悄当游客。
        ///
        /// <para>
        /// ⚠️ 这条是「身份不许静默降级」的守门人：一旦有人为了「方便」把失败改成
        /// 「那就当游客吧」，这条会红。
        /// </para>
        /// </summary>
        private static void Login_NoAccountStoreSaysWhyInsteadOfSilentlyBecomingGuest()
        {
            using (var h = new Harness())     // ⚠️ 不传账号表 = 没接数据库
            {
                Client c = h.ConnectAndLogin("alice", "pw-alice");
                HandshakeAck ack = AckOf(c, "没接数据库时登录");

                Check("M4-S3：没接数据库时登录被**明确拒绝**（不是静默变游客）",
                    !ack.Accepted && ack.PlayerId == 0 && ack.Reason.Contains("数据库"),
                    $"accepted={ack.Accepted}、player_id={ack.PlayerId}、reason=\"{ack.Reason}\"");

                Check("M4-S3：拒绝理由要给出**下一步怎么做**（留空用游客 / 设 NBC_DB_PASSWORD）",
                    ack.Reason.Contains("游客") && ack.Reason.Contains("NBC_DB_PASSWORD"),
                    $"reason=\"{ack.Reason}\"");
            }
        }

        /// <summary>
        /// 拿一条握手答复（**没收到也不炸**：给一张"全默认"的空答复，让断言自己红）。
        /// </summary>
        /// <param name="client">客户端。</param>
        /// <param name="what">用例名（写失败详情用）。</param>
        /// <returns>答复；没收到时是一张 Accepted=false / PlayerId=0 的空答复。</returns>
        private static HandshakeAck AckOf(Client client, string what)
        {
            if (client.Ack != null)
            {
                return client.Ack;
            }

            Check(what + "：收到握手答复", false, "一条 HandshakeAck 都没收到（服务端没回？还是连接就断了？）");
            return new HandshakeAck();
        }

        /// <summary>摘要**不许出现在服务端日志里**（结构性检查：翻一遍所有 Note）。</summary>
        private static void Login_DigestNeverAppearsInServerNotes()
        {
            using (var h = new Harness(accounts: new FakeAccounts()))
            {
                string digest = PasswordDigest.FromPassword("pw-alice");
                string wrongDigest = PasswordDigest.FromPassword("故意错的密码");

                h.ConnectAndLogin("alice", "pw-alice");
                h.ConnectAndLogin("alice", "故意错的密码");

                bool leaked = false;
                string hit = string.Empty;

                for (int i = 0; i < h.Notes.Count; i++)
                {
                    string line = h.Notes[i] ?? string.Empty;

                    if (line.Contains(digest) || line.Contains(wrongDigest))
                    {
                        leaked = true;
                        hit = line;
                    }
                }

                Check("M4-S3：服务端日志里**没有**密码摘要（失败与成功都不许泄漏）",
                    !leaked,
                    leaked ? ("泄漏在：" + hit) : ($"翻了 {h.Notes.Count} 条 Note，都没有摘要（摘要长度 {digest.Length}）"));
            }
        }

        /// <summary>
        /// 探针用的假账号表（**不碰数据库**）：两个账号 + 一个"没有档案"的账号。
        ///
        /// <para>
        /// ⚠️ 它必须和真实现用**同一份配方**（`PasswordDigest`）——
        /// 否则探针验的是"假实现能不能登进去"，与生产无关。
        /// </para>
        /// </summary>
        private sealed class FakeAccounts : IAccountStore
        {
            /// <summary>账号名 → （账号编号, 玩家编号, 昵称, salt, 明文密码）。</summary>
            private readonly Dictionary<string, (long AccountId, long PlayerId, string Nickname, string Salt, string Password)>
                _accounts = new()
                {
                    { "alice", (1, 11, "爱丽丝", "salt-alice", "pw-alice") },
                    { "bob", (2, 22, "鲍勃", "salt-bob", "pw-bob") },
                    { "noprofile", (3, 0, "没档案的人", "salt-np", "pw-np") },   // player_id = 0 ⇒ 没有档案
                };

            /// <summary>被问过几次（用来证明"服务端真的查了账号表"）。</summary>
            public int Calls { get; private set; }

            /// <inheritdoc/>
            public LoginResult Login(string account, string passwordDigest)
            {
                Calls++;

                if (!_accounts.TryGetValue(account ?? string.Empty, out var row))
                {
                    return LoginResult.Fail(ELoginRejection.UnknownAccount, $"没有这个账号：\"{account}\"。");
                }

                string stored = PasswordDigest.StoredHash(row.Salt, PasswordDigest.FromPassword(row.Password));

                if (!PasswordDigest.FixedTimeEquals(stored, PasswordDigest.StoredHash(row.Salt, passwordDigest)))
                {
                    return LoginResult.Fail(ELoginRejection.WrongPassword, $"账号 \"{row.Nickname}\" 的密码不对。");
                }

                if (row.PlayerId <= 0)
                {
                    return LoginResult.Fail(ELoginRejection.NoProfile, $"账号 \"{row.Nickname}\" 没有玩家档案。");
                }

                return LoginResult.Ok(row.AccountId, row.PlayerId, row.Nickname);
            }
        }

        /// <summary>快照里所有英雄单位的 `owner_player_id`（按快照顺序）。</summary>
        /// <param name="snapshot">快照。</param>
        /// <returns>主人列表。</returns>
        private static List<long> HeroOwners(WorldSnapshot snapshot)
        {
            var owners = new List<long>();

            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Entities.Count; i++)
                {
                    if (snapshot.Entities[i].Kind == 0)
                    {
                        owners.Add(snapshot.Entities[i].OwnerPlayerId);
                    }
                }
            }

            return owners;
        }

        /// <summary>找第 skip 个**普通怪**（非 BOSS、非英雄）。</summary>
        /// <param name="battle">世界。</param>
        /// <param name="skip">跳过几个。</param>
        /// <returns>怪或 null。</returns>
        private static BattleEntity FindMonster(DungeonBattle battle, int skip)
        {
            int seen = 0;

            for (int i = 0; i < battle.Entities.Count; i++)
            {
                BattleEntity entity = battle.Entities[i];

                if (entity.Kind != 1 || entity.IsBoss)
                {
                    continue;
                }

                if (seen == skip)
                {
                    return entity;
                }

                seen++;
            }

            return null;
        }

        /// <summary>把伤害事件串成人话（断言失败时看得见数）。</summary>
        /// <param name="hits">伤害事件。</param>
        /// <returns>人话。</returns>
        private static string DescribeHits(List<DamageEvent> hits)
        {
            if (hits.Count == 0)
            {
                return "（无）";
            }

            var text = new System.Text.StringBuilder();

            for (int i = 0; i < hits.Count; i++)
            {
                if (i > 0)
                {
                    text.Append("、");
                }

                text.Append("打 ").Append(hits[i].TargetId)
                    .Append(" 扣 ").Append(hits[i].Applied)
                    .Append("（剩 ").Append(hits[i].RemainingHp).Append('）');
            }

            return text.ToString();
        }

        /// <summary>找本局的 BOSS 单位。</summary>
        /// <param name="battle">世界。</param>
        /// <returns>BOSS 或 null。</returns>
        private static BattleEntity FindBoss(DungeonBattle battle)
        {
            for (int i = 0; i < battle.Entities.Count; i++)
            {
                if (battle.Entities[i].IsBoss)
                {
                    return battle.Entities[i];
                }
            }

            return null;
        }

        /// <summary>本局 BOSS 的攻击力（从表里算）。</summary>
        /// <param name="battle">世界。</param>
        /// <returns>攻击力。</returns>
        private static int BossAttack(DungeonBattle battle)
        {
            BattleEntity boss = FindBoss(battle);
            return boss == null ? 0 : boss.AttackDamage;
        }

        /// <summary>副本里有多少个怪（普通怪 + BOSS）—— **从表里算**，不手抄数字。</summary>
        /// <param name="h">夹具。</param>
        /// <param name="dungeonId">副本编号。</param>
        /// <returns>怪的数量。</returns>
        private static int MonstersInDungeon(Harness h, int dungeonId)
        {
            DungeonRow? row = h.Tables.FindDungeon(dungeonId);
            return row == null ? 0 : row.Value.Monsters.Length + 1;
        }

        /// <summary>副本里第一只普通怪的血量 —— **从表里算**。</summary>
        /// <param name="h">夹具。</param>
        /// <returns>血量。</returns>
        private static int WolfHp(Harness h)
        {
            DungeonRow? row = h.Tables.FindDungeon(1001);

            if (row == null || row.Value.Monsters.Length == 0)
            {
                return 0;
            }

            MonsterRow? monster = h.Tables.FindMonster(row.Value.Monsters[0]);
            return monster == null ? 0 : monster.Value.Hp;
        }

        /// <summary>数一数快照里某类单位有多少（0 英雄 / 1 怪物）。</summary>        /// <param name="snapshot">快照。</param>
        /// <param name="kind">类型。</param>
        /// <returns>个数。</returns>
        private static int CountKind(WorldSnapshot snapshot, int kind)
        {
            if (snapshot == null)
            {
                return 0;
            }

            int count = 0;

            for (int i = 0; i < snapshot.Entities.Count; i++)
            {
                if (snapshot.Entities[i].Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>找一个还活着的怪（M3 的临时关卡里就是狼）。</summary>
        /// <param name="battle">世界。</param>
        /// <returns>实例编号；找不到返回 0。</returns>
        private static int FirstAliveWolfId(DungeonBattle battle)
        {
            for (int i = 0; i < battle.Entities.Count; i++)
            {
                if (battle.Entities[i].Kind == 1 && battle.Entities[i].Alive)
                {
                    return battle.Entities[i].Id;
                }
            }

            return 0;
        }

        // ====================================================================
        //  夹具：服务端 + 若干客户端，全在一个线程上推
        // ====================================================================

        /// <summary>一套端到端夹具。</summary>
        private sealed class Harness : IDisposable
        {
            private readonly List<Client> _clients = new();

            /// <summary>当前这批客户端所在的房间（第一个客户端进房时记下来）。</summary>
            private string _currentRoomId;

            /// <summary>服务端传输（生产代码）。</summary>
            public readonly TcpServerTransport Server;

            /// <summary>路由（生产代码）。</summary>
            public readonly ServerMessageRouter Router;

            /// <summary>消息泵（生产代码；Host 用的就是它）。</summary>
            public readonly ServerMessagePump Pump;

            /// <summary>配置表（生产代码；S6 起阵容与数值都从它来）。</summary>
            public readonly ServerTables Tables;

            /// <summary>房间表（生产代码；纯规则）。</summary>
            public readonly RoomRegistry Registry;

            /// <summary>房间服务（生产代码；注册处理器 + 广播 + 断线退房）。</summary>
            public readonly RoomService Rooms;

            /// <summary>房间战斗服务（生产代码；权威世界 + 每帧快照下发）。</summary>
            public readonly RoomBattleService Battles;

            /// <summary>服务端侧的会话（握手后能看 Phase / PlayerId）。</summary>
            public readonly List<ClientSession> Sessions = new();

            /// <summary>服务端侧的断开记录。</summary>
            public readonly List<string> SessionClosings = new();

            /// <summary>房间服务记下的说明（进出房、拒绝原因）。</summary>
            public readonly List<string> RoomNotes = new();

            /// <summary>消息泵记下的说明（握手被拒、踢人原因……）。</summary>
            public readonly List<string> Notes = new();

            /// <summary>服务端权威（成就）记下的说明（【十六】要看它）。</summary>
            public readonly List<string> AuthorityNotes = new();

            /// <summary>
            /// 服务端**交上来的战绩草稿**（`RoomBattleService.BattleFinished`）。
            /// <para>⚠️ 有了它才能验"**草稿里到底有没有游客**" —— 那是"游客不落库"这条链的**前半段**：
            /// 战斗层负责**如实记账**（含游客），数据层负责**过滤掉落不了库的**。</para>
            /// </summary>
            public readonly List<BattleRecordDraft> Drafts = new();

            /// <summary>建夹具并开始监听。</summary>
            /// <param name="heartbeatTimeoutMs">心跳超时（毫秒）。</param>
            /// <param name="roomCapacity">每房容量。</param>
            /// <param name="accounts">
            /// 账号表（M4-S3）。**默认 null = 没接数据库** ⇒ 带账号的握手会被拒并说清原因。
            /// </param>
            public Harness(int heartbeatTimeoutMs = TcpServerTransport.DefaultHeartbeatTimeoutMs,
                           int roomCapacity = NetContract.MaxRoomMembers,
                           IAccountStore? accounts = null)
            {
                Server = new TcpServerTransport(0, IPAddress.Loopback, heartbeatTimeoutMs);
                Router = new ServerMessageRouter("probe", accounts);
                Pump = new ServerMessagePump(Server, Router);
                Pump.Note += line => Notes.Add(line);

                Router.Register(ClientMessage.PayloadOneofCase.Ping, HandlePing);

                // 配置表（S6）：副本阵容与数值都从表里来 —— 探针也读**真表**
                // ⚠️ P0（§三十六）：读表**必须排在房间表之前** —— `RoomRegistry` 要拿
                //    「这个副本号在 `Dungeon` 表里吗」这个谓词，接线方式与 Host 一模一样
                //    （不传谓词 = 不校验 ⇒ 探针就验不到「未知副本会被拒」这条）。
                string configDir;
                if (!ServerTables.TryResolveConfigDir(out configDir) ||
                    !ServerTables.TryLoad(configDir, out Tables, out string tablesError))
                {
                    throw new InvalidOperationException("探针读不到配置表：" + (configDir ?? "(没找到目录)"));
                }

                Registry = new RoomRegistry(roomCapacity, id => Tables.FindDungeon(id) != null);
                Rooms = new RoomService(Server, Registry);
                Rooms.RegisterHandlers(Router);
                Rooms.Note += line => RoomNotes.Add(line);

                Battles = new RoomBattleService(Server, Registry, Tables);
                Battles.Note += line => RoomNotes.Add(line);
                Battles.BattleFinished += draft => Drafts.Add(draft);   // SRV-17a：验草稿里有没有游客

                // ⚠️ 忘了这一行 = 所有输入用例**静默失效**（服务端根本没处理 Input 消息）。
                //    2026-09-23 第一版就漏了，9 条用例一起红给我看，根因就在这里。
                Battles.RegisterHandlers(Router);

                Server.SessionOpened += s => Sessions.Add(s);
                Server.SessionClosed += (s, reason) => SessionClosings.Add("会话 " + s.SessionId + "：" + reason);

                Server.Start();
            }

            /// <summary>连一个客户端（还没握手）。</summary>
            /// <returns>客户端。</returns>
            public Client Connect()
            {
                var c = new Client(Server.Port);
                _clients.Add(c);
                return c;
            }

            /// <summary>连一个客户端并握手（最常用的开局）。</summary>
            /// <param name="playerName">玩家名（席位表里靠它区分谁是谁）。</param>
            /// <returns>客户端。</returns>
            public Client ConnectAndHandshake(string playerName = "玩家")
            {
                Client c = Connect();
                WaitUntil(() => c.Transport.IsConnected, "客户端连上");
                c.Send(new ClientMessage
                {
                    Handshake = new Handshake
                    {
                        ProtocolVersion = NetContract.Version,
                        ClientVersion = "probe",
                        PlayerName = playerName,
                    },
                });
                WaitUntil(() => c.Ack != null, "握手完成");
                return c;
            }

            /// <summary>
            /// 连一个客户端并**用账号登录**（M4-S3）。
            /// <para>
            /// ⚠️ 这里刻意走**和客户端同一条路**：摘要用共享层的
            /// <see cref="PasswordDigest.FromPassword"/> 算 ——
            /// 探针要是自己拼一个字符串，验的就不是"客户端能不能登进去"了。
            /// </para>
            /// </summary>
            /// <param name="account">登录名。</param>
            /// <param name="password">明文密码（探针里传，好读）。</param>
            /// <returns>客户端（`Ack` 里就是服务端的答复，**可能是拒绝**）。</returns>
            public Client ConnectAndLogin(string account, string password)
            {
                Client c = Connect();
                WaitUntil(() => c.Transport.IsConnected, "客户端连上");
                c.Send(new ClientMessage
                {
                    Handshake = new Handshake
                    {
                        ProtocolVersion = NetContract.Version,
                        ClientVersion = "probe",
                        PlayerName = "不该用到这个名字",
                        Account = account,
                        PasswordDigest = PasswordDigest.FromPassword(password),
                    },
                });
                WaitUntil(() => c.Ack != null, "登录答复");
                return c;
            }

            /// <summary>推一帧：服务端一次 + 每个客户端一次。</summary>
            public void PumpOnce()
            {
                Pump.Pump();

                for (int i = 0; i < _clients.Count; i++)
                {
                    _clients[i].Transport.Pump();
                }
            }

            /// <summary>
            /// 推 N 个逻辑帧（每帧推进世界并下发一张快照），最后把结果泵回客户端。
            /// <para>
            /// ⚠️ **顺序必须与 Host 主循环一致：先收包派发、再推进逻辑**。
            /// 我第一版写成了"先 Tick 再 Pump"，于是"这一帧发出去的输入"要等下一帧才被处理 ——
            /// 表现是一堆用例莫名其妙地红（输入像是丢了、离房的人英雄还留在场上）。
            /// **探针里的推帧顺序如果和主循环不一致，验出来的就不是生产里跑的那个东西。**
            /// </para>
            /// </summary>
            /// <param name="ticks">帧数。</param>
            public void TickBattles(int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    PumpOnce();         // ① 网络：收 + 派发（与 Host 同序）
                    Battles.Tick();     // ② 逻辑：对齐英雄 → 吃输入 → 推进 → 下发快照
                }

                PumpFor(10);            // 把结果泵回客户端
            }

            /// <summary>连一个客户端并**让它进第一个房间**（S5 用例最常用的开局）。</summary>
            /// <param name="client">已经握手完成的客户端。</param>
            /// <returns>同一个客户端（返回它是为了写成链式调用）。</returns>
            public Client JoinSameRoom(Client client)
            {
                if (string.IsNullOrEmpty(_currentRoomId))
                {
                    client.JoinRoom(string.Empty, 1001);
                    WaitUntil(() => client.LastRoom() != null, "收到席位表");
                    _currentRoomId = client.LastRoom().RoomId;
                }
                else
                {
                    client.JoinRoom(_currentRoomId, 1001);
                    WaitUntil(() => client.LastRoom() != null && client.LastRoom().Members.Count >= 2,
                        "进同一个房间");
                }

                return client;
            }

            /// <summary>取这个客户端所在房间的战斗世界。</summary>
            /// <param name="client">客户端。</param>
            /// <returns>世界或 null。</returns>
            public DungeonBattle BattleOf(Client client)
                => string.IsNullOrEmpty(client.RoomId) ? null : Battles.Find(client.RoomId);

            /// <summary>取这个客户端对应的英雄（世界里那个单位）。</summary>
            /// <param name="client">客户端。</param>
            /// <returns>英雄或 null。</returns>
            public BattleEntity HeroOf(Client client)
            {
                DungeonBattle battle = BattleOf(client);
                return battle == null || client.Ack == null ? null : battle.FindHeroOfPlayer(client.Ack.PlayerId);
            }

            /// <summary>一边发输入一边推帧（模拟"客户端每帧发一条"）。</summary>
            /// <param name="client">客户端。</param>
            /// <param name="moveX">左右轴。</param>
            /// <param name="moveY">前后轴。</param>
            /// <param name="ticks">推几帧。</param>
            public void MoveFor(Client client, int moveX, int moveY, int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    client.SendInput(moveX, moveY);
                    TickBattles(1);
                }
            }

            /// <summary>一直往目标走，直到进入普攻射程（有上限，走不到就返回 false）。</summary>
            /// <param name="client">客户端。</param>
            /// <param name="targetId">目标实例编号。</param>
            /// <param name="maxTicks">最多走几帧。</param>
            /// <returns>进射程了返回 true。</returns>
            public bool MoveUntilInRange(Client client, int targetId, int maxTicks, int moveX = 1000)
            {
                for (int i = 0; i < maxTicks; i++)
                {
                    DungeonBattle battle = BattleOf(client);
                    BattleEntity hero = HeroOf(client);
                    BattleEntity target = battle == null ? null : battle.Find(targetId);

                    if (hero == null || target == null)
                    {
                        return false;
                    }

                    // ⚠️ 用**服务端同一个判定**（`InAttackRange` = 圆形射程），不要自己拿切比雪夫判：
                    //    2026-10-20 射程改成圆之后，工具若还用旧判据，会"走到圆外就停下"⇒
                    //    攻击被判越界，表现为"走近了却打不中" —— 那种红会让人怀疑业务代码。
                    if (battle.InAttackRange(hero, target))
                    {
                        return true;
                    }

                    // ⚠️ 方向**必须能指定**：2026-09-26 这条原来写死"往 +X 走"
                    //    （当时场上只有右边的狼），拿它去追左边（-X）的 BOSS 会让英雄**反向跑**，
                    //    报错形状是"走到 8000mm 还没进射程"—— 一眼看不出是工具的问题。
                    MoveFor(client, moveX, 0, 1);
                }

                return false;
            }

            /// <summary>发 N 帧"什么都不按"的输入（用来等冷却走完）。</summary>
            /// <param name="client">客户端。</param>
            /// <param name="ticks">帧数。</param>
            public void SendIdleInputFor(Client client, int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    client.SendInput(0, 0);
                    TickBattles(1);
                }
            }

            /// <summary>推一段时间。</summary>
            /// <param name="ms">毫秒。</param>
            public void PumpFor(int ms)
            {
                DateTime until = DateTime.UtcNow.AddMilliseconds(ms);

                while (DateTime.UtcNow < until)
                {
                    PumpOnce();
                    Thread.Sleep(1);
                }
            }

            /// <summary>推到条件成立（有界），不成立直接判失败。</summary>
            /// <param name="condition">条件。</param>
            /// <param name="what">等的是什么。</param>
            /// <param name="timeoutMs">上限毫秒。</param>
            public void WaitUntil(Func<bool> condition, string what, int timeoutMs = 3000)
            {
                DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);

                while (DateTime.UtcNow < until)
                {
                    PumpOnce();

                    if (condition())
                    {
                        return;
                    }

                    Thread.Sleep(1);
                }

                PumpOnce();

                if (!condition())
                {
                    Check("等待「" + what + "」", false, timeoutMs + "ms 内没等到");
                }
            }

            /// <summary>关掉所有客户端与服务端。</summary>
            public void Dispose()
            {
                for (int i = 0; i < _clients.Count; i++)
                {
                    _clients[i].Transport.Dispose();
                }

                Server.Stop();
            }
        }

        /// <summary>一个客户端（真 `TcpTransport`，Unity 侧那份源码）。</summary>
        /// <summary>假权威：**什么都不认识**（= 服务端一条都没提过）。</summary>
        private sealed class SilentAuthority : NBC.Game.Quest.IQuestProgressAuthority
        {
            /// <summary>被问过几次（证明"真的去问了"，而不是压根没接）。</summary>
            public int Asked { get; private set; }

            /// <inheritdoc/>
            public bool TryGetCondition(int conditionId, out NBC.Game.Quest.AuthoritativeProgress progress)
            {
                Asked++;
                progress = default(NBC.Game.Quest.AuthoritativeProgress);
                return false;
            }
        }

        /// <summary>假权威：**只回答一条**，其余沉默 —— 用来验"逐条判断"而不是"整批一刀切"。</summary>
        private sealed class SpeakingAuthority : NBC.Game.Quest.IQuestProgressAuthority
        {
            /// <summary>我"说过"的那一条。</summary>
            private readonly int m_conditionId;

            /// <summary>我说的值。</summary>
            private readonly NBC.Game.Quest.AuthoritativeProgress m_progress;

            /// <summary>造一个。</summary>
            /// <param name="conditionId">我认识的条件编号。</param>
            /// <param name="current">已累计。</param>
            /// <param name="required">需求值。</param>
            /// <param name="met">达成了没有。</param>
            public SpeakingAuthority(int conditionId, int current, int required, bool met)
            {
                m_conditionId = conditionId;
                m_progress = new NBC.Game.Quest.AuthoritativeProgress(current, required, met);
            }

            /// <inheritdoc/>
            public bool TryGetCondition(int conditionId, out NBC.Game.Quest.AuthoritativeProgress progress)
            {
                progress = m_progress;
                return conditionId == m_conditionId;
            }
        }

        private sealed class Client
        {
            /// <summary>收到的服务端消息（已解出 protobuf）。</summary>
            public readonly List<ServerMessage> Received = new();

            /// <summary>客户端侧的断开原因。</summary>
            public readonly List<string> CloseReasons = new();

            /// <summary>传输（客户端实现）。</summary>
            public readonly TcpTransport Transport;

            /// <summary>连到指定端口（非阻塞：要靠 Pump 推进）。</summary>
            /// <param name="port">端口。</param>
            public Client(int port)
            {
                Transport = new TcpTransport();
                Transport.FrameReceived += payload => Received.Add(ServerMessage.Parser.ParseFrom(payload));
                Transport.Closed += reason => CloseReasons.Add(reason);
                Transport.Connect("127.0.0.1", port);
            }

            /// <summary>最后收到的那条。</summary>
            public ServerMessage Last => Received.Count == 0 ? null : Received[Received.Count - 1];

            /// <summary>握手结果（收到过才有）。</summary>
            public HandshakeAck Ack
            {
                get
                {
                    for (int i = 0; i < Received.Count; i++)
                    {
                        if (Received[i].HandshakeAck != null)
                        {
                            return Received[i].HandshakeAck;
                        }
                    }

                    return null;
                }
            }

            /// <summary>收到的 Pong（收到过才有）。</summary>
            public Pong FindPong()
            {
                for (int i = 0; i < Received.Count; i++)
                {
                    if (Received[i].Pong != null)
                    {
                        return Received[i].Pong;
                    }
                }

                return null;
            }

            /// <summary>最后收到的那份席位表（收到过才有）。</summary>
            /// <returns>房间状态或 null。</returns>
            public RoomState LastRoom()
            {
                RoomState found = null;

                for (int i = 0; i < Received.Count; i++)
                {
                    if (Received[i].RoomState != null)
                    {
                        found = Received[i].RoomState;
                    }
                }

                return found;
            }

            /// <summary>现在所在的房号（不在房里返回 null —— 注意"收到过空房号状态"也算不在房里）。</summary>
            public string RoomId
            {
                get
                {
                    RoomState room = LastRoom();
                    return room == null || string.IsNullOrEmpty(room.RoomId) ? null : room.RoomId;
                }
            }

            /// <summary>最后收到的那张世界快照（收到过才有）。</summary>
            /// <returns>快照或 null。</returns>
            public WorldSnapshot LastSnapshot()
            {
                WorldSnapshot found = null;

                for (int i = 0; i < Received.Count; i++)
                {
                    if (Received[i].Snapshot != null)
                    {
                        found = Received[i].Snapshot;
                    }
                }

                return found;
            }

            /// <summary>收到过多少张世界快照。</summary>
            /// <returns>张数。</returns>
            public int SnapshotCount()
            {
                int count = 0;

                for (int i = 0; i < Received.Count; i++)
                {
                    if (Received[i].Snapshot != null)
                    {
                        count++;
                    }
                }

                return count;
            }

            /// <summary>最后收到的那条拒绝（收到过才有）。</summary>
            /// <returns>错误或 null。</returns>
            public ErrorResponse LastError()
            {
                ErrorResponse found = null;

                for (int i = 0; i < Received.Count; i++)
                {
                    if (Received[i].Error != null)
                    {
                        found = Received[i].Error;
                    }
                }

                return found;
            }

            /// <summary>请求进房。</summary>
            /// <param name="roomId">房号（留空 = 让服务端安排）。</param>
            /// <param name="dungeonId">副本编号。</param>
            public void JoinRoom(string roomId, int dungeonId)
            {
                Send(new ClientMessage
                {
                    JoinRoom = new JoinRoomRequest { RoomId = roomId ?? string.Empty, DungeonId = dungeonId },
                });
            }

            /// <summary>请求离开房间。</summary>
            public void LeaveRoom()
            {
                Send(new ClientMessage { LeaveRoom = new LeaveRoomRequest() });
            }

            /// <summary>发一帧输入（**用自己真实的 player_id**）。</summary>
            /// <param name="moveX">左右轴。</param>
            /// <param name="moveY">前后轴。</param>
            /// <param name="actionBits">新按下的动作位（第 0 位 = 普攻）。</param>
            /// <param name="targetEntityId">目标实例编号。</param>
            public void SendInput(int moveX, int moveY, uint actionBits = 0, int targetEntityId = 0)
            {
                SendInputAs(Ack == null ? 0 : Ack.PlayerId, moveX, moveY, actionBits, targetEntityId);
            }

            /// <summary>发一帧**冒名**输入（`player_id` 写成别人的；用来测权威校验）。</summary>
            /// <param name="playerId">冒用的玩家 id。</param>
            /// <param name="moveX">左右轴。</param>
            /// <param name="moveY">前后轴。</param>
            /// <param name="actionBits">新按下的动作位。</param>
            /// <param name="targetEntityId">目标实例编号。</param>
            public void SendInputAs(long playerId, int moveX, int moveY, uint actionBits = 0, int targetEntityId = 0)
            {
                InputsSent++;
                Send(new ClientMessage
                {
                    Input = new PlayerInput
                    {
                        PlayerId = playerId,
                        ClientTick = InputsSent,
                        MoveX = moveX,
                        MoveY = moveY,
                        ActionBits = actionBits,
                        TargetEntityId = targetEntityId,
                    },
                });
            }

            /// <summary>发出去过多少条输入（对账用）。</summary>
            public int InputsSent { get; private set; }

            /// <summary>收到过多少条掉落事件（S7）。</summary>
            /// <returns>条数。</returns>
            public int DropCount()
            {
                int count = 0;

                for (int i = 0; i < Received.Count; i++)
                {
                    if (Received[i].Event != null && Received[i].Event.Drop != null)
                    {
                        count++;
                    }
                }

                return count;
            }

            /// <summary>取"第 index 条之后"收到的掉落事件（按接收顺序；用来只看这一帧新掉的）。</summary>
            /// <param name="index">之前已经看过几条。</param>
            /// <returns>掉落事件。</returns>
            public List<DropEvent> DropsSince(int index)
            {
                var list = new List<DropEvent>();
                int seen = 0;

                for (int i = 0; i < Received.Count; i++)
                {
                    if (Received[i].Event == null || Received[i].Event.Drop == null)
                    {
                        continue;
                    }

                    if (seen >= index)
                    {
                        list.Add(Received[i].Event.Drop);
                    }

                    seen++;
                }

                return list;
            }

            /// <summary>发一条消息。</summary>
            /// <param name="message">消息。</param>
            public void Send(ClientMessage message) => Transport.Send(message.ToByteArray());

            /// <summary>收到过多少条伤害事件（M4-S1）。</summary>
            /// <returns>条数。</returns>
            public int HitCount()
            {
                int count = 0;

                for (int i = 0; i < Received.Count; i++)
                {
                    if (Received[i].Event != null && Received[i].Event.Damage != null)
                    {
                        count++;
                    }
                }

                return count;
            }

            /// <summary>取"第 index 条之后"收到的伤害事件。</summary>
            /// <param name="index">之前已经看过几条。</param>
            /// <returns>伤害事件。</returns>
            public List<DamageEvent> HitsSince(int index)
            {
                var list = new List<DamageEvent>();
                int seen = 0;

                for (int i = 0; i < Received.Count; i++)
                {
                    if (Received[i].Event == null || Received[i].Event.Damage == null)
                    {
                        continue;
                    }

                    if (seen >= index)
                    {
                        list.Add(Received[i].Event.Damage);
                    }

                    seen++;
                }

                return list;
            }

            /// <summary>收到过多少条死亡事件（M4-S1）。</summary>
            /// <returns>条数。</returns>
            public int DeathCount()
            {
                int count = 0;

                for (int i = 0; i < Received.Count; i++)
                {
                    if (Received[i].Event != null && Received[i].Event.Death != null)
                    {
                        count++;
                    }
                }

                return count;
            }

            /// <summary>取"第 index 条之后"收到的死亡事件。</summary>
            /// <param name="index">之前已经看过几条。</param>
            /// <returns>死亡事件。</returns>
            public List<DeathEvent> DeathsSince(int index)
            {
                var list = new List<DeathEvent>();
                int seen = 0;

                for (int i = 0; i < Received.Count; i++)
                {
                    if (Received[i].Event == null || Received[i].Event.Death == null)
                    {
                        continue;
                    }

                    if (seen >= index)
                    {
                        list.Add(Received[i].Event.Death);
                    }

                    seen++;
                }

                return list;
            }

            /// <summary>收到过多少条**战斗事件**（伤害或死亡，不含掉落）—— 用来编号排序。</summary>
            /// <returns>条数。</returns>
            public int EventOrderCount()
            {
                int count = 0;

                for (int i = 0; i < Received.Count; i++)
                {
                    if (IsCombatEvent(Received[i]))
                    {
                        count++;
                    }
                }

                return count;
            }

            /// <summary>取"第 index 条战斗事件之后"收到的事件种类顺序（`damage` / `death`）。</summary>
            /// <param name="index">之前已经看过几条。</param>
            /// <returns>种类名。</returns>
            public List<string> EventKindsSince(int index)
            {
                var list = new List<string>();
                int seen = 0;

                for (int i = 0; i < Received.Count; i++)
                {
                    if (!IsCombatEvent(Received[i]))
                    {
                        continue;
                    }

                    if (seen >= index)
                    {
                        list.Add(Received[i].Event.Damage != null ? "damage" : "death");
                    }

                    seen++;
                }

                return list;
            }

            /// <summary>这条消息是不是战斗事件（伤害或死亡）。</summary>
            /// <param name="message">服务端消息。</param>
            /// <returns>是返回 true。</returns>
            private static bool IsCombatEvent(ServerMessage message)
            {
                return message.Event != null
                    && (message.Event.Damage != null || message.Event.Death != null);
            }
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>心跳处理器（与 Host 里那份同一个形状）。</summary>
        /// <param name="session">会话。</param>
        /// <param name="message">消息。</param>
        /// <returns>结果。</returns>
        private static DispatchResult HandlePing(ClientSession session, ClientMessage message)
        {
            return DispatchResult.ReplyWith(new ServerMessage
            {
                Pong = new Pong
                {
                    ClientTimeMs = message.Ping.ClientTimeMs,
                    ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            });
        }

        /// <summary>造一个会话（纯房间规则用例用：不连网，只要 id 与名字）。</summary>
        /// <param name="id">会话 id / 玩家 id。</param>
        /// <param name="name">玩家名。</param>
        /// <returns>会话。</returns>
        private static ClientSession NewSession(long id, string name)
        {
            return new ClientSession
            {
                SessionId = id,
                PlayerId = id,
                PlayerName = name,
            };
        }

        /// <summary>把席位表里的名字拼成一行（失败信息里看得见"谁在房里"）。</summary>
        /// <param name="state">房间状态。</param>
        /// <returns>名字串。</returns>
        private static string Names(RoomState state)
        {
            var text = new System.Text.StringBuilder();

            for (int i = 0; i < state.Members.Count; i++)
            {
                if (i > 0)
                {
                    text.Append('、');
                }

                text.Append(state.Members[i].PlayerName);
            }

            return text.ToString();
        }

        /// <summary>断言一次。</summary>
        /// <param name="name">用例名。</param>
        /// <param name="ok">通过了吗。</param>
        /// <param name="detail">细节。</param>
        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                s_passed++;
                Console.WriteLine("  ✅ " + name);
                return;
            }

            s_failed++;
            Console.WriteLine("  ❌ " + name + "   —— " + detail);

            // ⭐ ASCII 标记（控制台把 ✅/❌ 吐成 `?` 时靠它抓）：
            //    `[FAIL] <检查名>`。**全绿时一行都不该出现** —— 否则这个标记本身就是噪音。
            Console.WriteLine("[FAIL] " + name);
        }

        /// <summary>两段字节是否相同。</summary>
        /// <param name="a">甲。</param>
        /// <param name="b">乙。</param>
        /// <returns>相同返回 true。</returns>
        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>把握手结果打成人话。</summary>
        /// <param name="ack">握手结果。</param>
        /// <returns>人话。</returns>
        private static string Describe(HandshakeAck ack)
        {
            if (ack == null)
            {
                return "没收到 HandshakeAck";
            }

            return "accepted=" + ack.Accepted + "，player_id=" + ack.PlayerId +
                   "，version=" + ack.ProtocolVersion + "，tick=" + ack.TickHz +
                   "，server=" + ack.ServerVersion;
        }
    }
}
