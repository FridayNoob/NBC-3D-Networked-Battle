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
using Google.Protobuf;
using NBC.Framework.Net.Adapter;
using NBC.Framework.Net.Sim;   // S8：网络模拟器（SimulatedTransport / NetSimProfile）
using NBC.Game.Net;
using NBC.Protocol;
using NBC.Server.Core;
using NBC.Server.Game;          // S5：`DungeonBattle` / `RoomBattleService`
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
                return SessionSmoke(sessionPort);
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
                ack != null && ack.Accepted && ack.PlayerId > 0 && ack.TickHz == NetContract.TickRate,
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
        private static int SessionSmoke(int port)
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
                session.PlayerId > 0 && session.Ack != null && !string.IsNullOrEmpty(session.Ack.ServerVersion),
                "player_id=" + session.PlayerId + "，server=" + (session.Ack == null ? "无" : session.Ack.ServerVersion));

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
                          && ack.PlayerId == 1
                          && ack.ProtocolVersion == NetContract.Version
                          && ack.TickHz == NetContract.TickRate
                          && ack.ServerVersion == "probe";

                Check("握手成功：accepted + 玩家号 1 + 版本回显 + tick 率", ok, Describe(ack));

                Check("握手成功后会话推进到 InLobby（M0 的会话状态机真的动了）",
                    h.Sessions.Count == 1 && h.Sessions[0].Phase == SessionPhase.InLobby,
                    h.Sessions.Count == 1 ? ("Phase=" + h.Sessions[0].Phase) : "会话数=" + h.Sessions.Count);

                Check("会话拿到了 player_id（不是 0）",
                    h.Sessions.Count == 1 && h.Sessions[0].PlayerId == 1,
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

            /// <summary>建夹具并开始监听。</summary>
            /// <param name="heartbeatTimeoutMs">心跳超时（毫秒）。</param>
            /// <param name="roomCapacity">每房容量。</param>
            public Harness(int heartbeatTimeoutMs = TcpServerTransport.DefaultHeartbeatTimeoutMs,
                           int roomCapacity = NetContract.MaxRoomMembers)
            {
                Server = new TcpServerTransport(0, IPAddress.Loopback, heartbeatTimeoutMs);
                Router = new ServerMessageRouter("probe");
                Pump = new ServerMessagePump(Server, Router);

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
