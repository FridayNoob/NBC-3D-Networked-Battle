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
using NBC.Framework.Net.Adapter;
using NBC.Framework.Net.Sim;   // S8：网络模拟器（SimulatedTransport / NetSimProfile）
using NBC.Game.Net;
using NBC.Game.Quest;           // M4-S3 收口（§二十四）：进度「是谁说的」那一层（纯 C#，可在探针里真跑）
using NBC.Protocol;
using NBC.Server.Core;
using NBC.Server.Game;          // S5：`DungeonBattle` / `RoomBattleService`
using NBC.Shared;               // M4-S4 S4-a：`Fix64` / `FixMath` / `FixVector3`（共享层确定性数学）
using NBC.Shared.Auth;          // M4-S3：账号登录的摘要配方（**双端同一份实现**）
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

                // 出生点在 (-1000, 0)，狼在 (≈2000, 1000)：切比雪夫距离 3000 > 射程 2000
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

                Registry = new RoomRegistry(roomCapacity);
                Rooms = new RoomService(Server, Registry);
                Rooms.RegisterHandlers(Router);
                Rooms.Note += line => RoomNotes.Add(line);

                // 配置表（S6）：副本阵容与数值都从表里来 —— 探针也读**真表**
                string configDir;
                if (!ServerTables.TryResolveConfigDir(out configDir) ||
                    !ServerTables.TryLoad(configDir, out Tables, out string tablesError))
                {
                    throw new InvalidOperationException("探针读不到配置表：" + (configDir ?? "(没找到目录)"));
                }

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

                    if (DungeonBattle.DistanceMm(hero, target) <= DungeonBattle.BasicAttackRangeMm)
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
