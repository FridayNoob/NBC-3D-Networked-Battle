// ============================================================================
//  M3-S3 客户端半边 · `NetSession` 的测试（**一条真 socket 都不用开**）
//  对应验收：Docs\25 §八、§8.6
//  被测：`Client\Assets\_Project\Game\Net\NetSession.cs`
//
//  ---------------------------------------------------------------------------
//  这一组为什么全用 `FakeTransport`
//  ---------------------------------------------------------------------------
//  S2b 做的那个接缝（`ITransport`）就是为这里准备的：
//    · 握手**超时**、心跳**超时**这种用例，真开 socket 就得 `Thread.Sleep` 等 5 秒，
//      既慢又 flaky；用假传输 + **注入时间**（`Pump(deltaMs)`），
//      "过了 5 秒"就是一行 `session.Pump(5001)`
//    · "服务端发来一条垃圾"、"服务端发来 0 长度帧"这类**负向对照**，
//      真服务端根本发不出来 —— 只有假传输能造
//
//  ⚠️ 真 socket 的那条路在 S3 的端到端探针里已经验过（17/17）：
//     服务端生产代码 + 客户端这份 `TcpTransport`，真端口、真 protobuf。
//     这里测的是**会话层的语义**，不是"网线通不通"。
// ============================================================================

using System;
using System.Collections.Generic;
using Google.Protobuf;
using NBC.Framework.Net;
using NBC.Game.Net;
using NBC.Protocol;
using NBC.Shared.Net;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>M3-S3：客户端会话（握手/心跳/收包）的测试。</summary>
    public sealed class NetSessionTests
    {
        // ====================================================================
        //  一、握手
        // ====================================================================

        /// <summary>连上之后应当立刻发一条握手，且带的是**契约版本**。</summary>
        [Test]
        public void Connect_SendsHandshakeWithContractVersion()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = new NetSession(fake, "测试玩家", "unit-test");

            session.Connect("127.0.0.1", 7777);
            session.Pump(0);

            Assert.AreEqual(ESessionState.Handshaking, session.State, "连上之后应当进入握手中");
            Assert.AreEqual(1, fake.SentFrames.Count, "应当恰好发了一条消息（握手）");

            ClientMessage sent = ClientMessage.Parser.ParseFrom(fake.SentFrames[0]);
            Assert.AreEqual(ClientMessage.PayloadOneofCase.Handshake, sent.PayloadCase);
            Assert.AreEqual(NetContract.Version, sent.Handshake.ProtocolVersion,
                "握手里的版本必须是契约版本，否则服务端一定会拒");
            Assert.AreEqual("测试玩家", sent.Handshake.PlayerName);
            Assert.AreEqual("unit-test", sent.Handshake.ClientVersion);
        }

        /// <summary>握手成功：进 Online、拿到玩家 id、并且立刻测一次 RTT。</summary>
        [Test]
        public void HandshakeAck_Accepted_GoesOnlineAndPingsImmediately()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = new NetSession(fake, "测试玩家");

            session.Connect("127.0.0.1", 7777);
            session.Pump(0);

            fake.PushFrame(Ack(accepted: true, playerId: 7, server: "unit-server"));
            session.Pump(0);

            Assert.AreEqual(ESessionState.Online, session.State);
            Assert.AreEqual(7, session.PlayerId);
            Assert.AreEqual("unit-server", session.Ack.ServerVersion);
            Assert.AreEqual(2, fake.SentFrames.Count, "握手 + 立刻那一次心跳");
            Assert.IsTrue(session.IsOnline);
            StringAssert.Contains("玩家 7", session.Description);
        }

        /// <summary>被服务端拒绝：进 Failed，原因要**带出来**（不能只是"连不上"）。</summary>
        [Test]
        public void HandshakeAck_Rejected_FailsWithServerReason()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = new NetSession(fake, "测试玩家");

            session.Connect("127.0.0.1", 7777);
            session.Pump(0);

            fake.PushFrame(Ack(accepted: false, reason: "协议版本不一致：客户端 v99，服务端 v1"));
            session.Pump(0);

            Assert.AreEqual(ESessionState.Failed, session.State);
            StringAssert.Contains("版本不一致", session.FailureReason);
            Assert.AreEqual(ETransportState.Closed, fake.State,
                "失败之后必须把连接关掉（留半死 socket = 以后 Send 会以为还连着）");
        }

        /// <summary>连着但服务端不说话 → 握手超时（否则界面永远停在"连接中"）。</summary>
        [Test]
        public void HandshakeTimeout_FailsWithReason()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = new NetSession(fake, "测试玩家", "unit-test",
                heartbeatIntervalMs: 1000, heartbeatTimeoutMs: 5000, handshakeTimeoutMs: 5000);

            session.Connect("127.0.0.1", 7777);
            session.Pump(0);
            session.Pump(5001);

            Assert.AreEqual(ESessionState.Failed, session.State);
            StringAssert.Contains("握手超时", session.FailureReason);
            Assert.AreEqual(ETransportState.Closed, fake.State);
        }

        // ====================================================================
        //  二、心跳与 RTT（时间全是**喂**进去的）
        // ====================================================================

        /// <summary>心跳按节奏发：握手后立刻一次，之后每 1 秒一次。</summary>
        [Test]
        public void Heartbeat_IsSentOnSchedule()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            Assert.AreEqual(1, session.PingsSent, "握手成功后应当立刻发过一次");

            // 走 3 秒（每步 250ms）：应当在 1000/2000/3000 各发一次 → 共 1+3 = 4 次
            for (int i = 0; i < 12; i++)
            {
                session.Pump(250);
            }

            Assert.AreEqual(4, session.PingsSent,
                "1 次（握手后）+ 3 次（第 1/2/3 秒），当前 " + session.PingsSent);
            Assert.AreEqual(3000, session.ClockMs);
        }

        /// <summary>RTT = 收到 Pong 的时刻 − Pong 里带回来的发送时刻。</summary>
        [Test]
        public void Pong_ComputesRtt()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            // 握手后那次心跳是在本地时钟 0 发的
            fake.PushFrame(new ServerMessage
            {
                Pong = new Pong { ClientTimeMs = 0, ServerTimeMs = 12345 },
            }.ToByteArray());

            session.Pump(120);

            Assert.AreEqual(120, session.RttMs);
            Assert.AreEqual(1, session.PongsReceived);
            StringAssert.Contains("120ms", session.Description);
        }

        /// <summary>心跳超时：判掉线**并且真的断开**（不能只把状态标一下）。</summary>
        [Test]
        public void HeartbeatTimeout_FailsAndClosesTransport()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            for (int i = 0; i < 21; i++)       // 21 × 250ms = 5250ms > 5000ms
            {
                session.Pump(250);
            }

            Assert.AreEqual(ESessionState.Failed, session.State);
            StringAssert.Contains("心跳超时", session.FailureReason);
            Assert.AreEqual(ETransportState.Closed, fake.State,
                "见 NetSession 文件头 ③：必须真断开，否则下一次 Send 会以为还连着");
        }

        /// <summary>有 Pong 回来就不算超时（负向对照：把"判超时"这条规则钉住）。</summary>
        [Test]
        public void HeartbeatTimeout_DoesNotFireWhilePongsKeepComing()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            for (int i = 0; i < 40; i++)       // 10 秒
            {
                fake.PushFrame(new ServerMessage
                {
                    Pong = new Pong { ClientTimeMs = session.ClockMs },
                }.ToByteArray());

                session.Pump(250);
            }

            Assert.AreEqual(ESessionState.Online, session.State, "一直有 Pong 就不该判掉线");
            Assert.IsNull(session.FailureReason);
        }

        // ====================================================================
        //  三、收包：好包、坏包、空包
        // ====================================================================

        /// <summary>服务端发来的事件要能交给上层（`MessageReceived`）。</summary>
        [Test]
        public void ServerMessage_IsForwardedToUpperLayer()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            List<ServerMessage> got = new List<ServerMessage>();
            session.MessageReceived += got.Add;

            fake.PushFrame(new ServerMessage
            {
                Event = new ServerEvent { Damage = new DamageEvent { AttackerId = 1, TargetId = 2, Applied = 30, RemainingHp = 70 } },
            }.ToByteArray());

            session.Pump(0);

            Assert.AreEqual(1, got.Count, "应当收到一条");
            Assert.AreEqual(ServerMessage.PayloadOneofCase.Event, got[0].PayloadCase);
            Assert.AreEqual(30, got[0].Event.Damage.Applied);
            Assert.AreEqual(ESessionState.Online, session.State, "收到正常消息不该影响状态");
        }

        /// <summary>0 长度帧（空消息）：判协议违规，不是静默忽略。</summary>
        [Test]
        public void EmptyServerFrame_IsProtocolViolation()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            fake.PushFrame(new byte[0]);
            session.Pump(0);

            Assert.AreEqual(ESessionState.Failed, session.State);
            StringAssert.Contains("没有 payload", session.FailureReason);
        }

        /// <summary>解不出来的字节：判协议违规并说清（对端不是本协议 / 数据烂了）。</summary>
        [Test]
        public void GarbageServerFrame_FailsWithReason()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            // 字段 1（长度前缀类型）声称有 5 字节，实际只给了 1 字节 → protobuf 解析必抛
            fake.PushFrame(new byte[] { 0x0A, 0x05, 0x01 });
            session.Pump(0);

            Assert.AreEqual(ESessionState.Failed, session.State);
            StringAssert.Contains("解不出 ServerMessage", session.FailureReason);
        }

        /// <summary>重复的 HandshakeAck：忽略，不许把已上线的会话打回失败。</summary>
        [Test]
        public void DuplicateHandshakeAck_IsIgnored()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            fake.PushFrame(Ack(accepted: true, playerId: 7, server: "unit-server"));
            session.Pump(0);

            Assert.AreEqual(ESessionState.Online, session.State);
            Assert.AreEqual(7, session.PlayerId);
        }

        // ====================================================================
        //  四、发送与断开
        // ====================================================================

        /// <summary>没上线就发：抛异常（不静默丢）。</summary>
        [Test]
        public void Send_BeforeOnline_Throws()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = new NetSession(fake, "测试玩家");

            Assert.Throws<InvalidOperationException>(() => session.Send(new ClientMessage
            {
                Ping = new Ping { ClientTimeMs = 1 },
            }));
        }

        /// <summary>上线之后发：载荷落到传输上，且解出来是那条消息。</summary>
        [Test]
        public void Send_WhileOnline_PutsPayloadOnWire()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            int before = fake.SentFrames.Count;

            session.Send(new ClientMessage
            {
                JoinRoom = new JoinRoomRequest { RoomId = "r1", DungeonId = 1001 },
            });

            Assert.AreEqual(before + 1, fake.SentFrames.Count);

            ClientMessage sent = ClientMessage.Parser.ParseFrom(fake.LastSent);
            Assert.AreEqual(ClientMessage.PayloadOneofCase.JoinRoom, sent.PayloadCase);
            Assert.AreEqual("r1", sent.JoinRoom.RoomId);
        }

        /// <summary>传输层断开（对面关了）：算失败，并把原因带出来。</summary>
        [Test]
        public void TransportClosed_ReportsFailureWithReason()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            fake.FailWith("对端关闭了连接（收到 0 字节）");
            session.Pump(0);

            Assert.AreEqual(ESessionState.Failed, session.State);
            StringAssert.Contains("连接断开", session.FailureReason);
            StringAssert.Contains("对端关闭", session.FailureReason);
        }

        /// <summary>主动断开：幂等，状态回到未连接（不是失败）。</summary>
        [Test]
        public void Disconnect_IsIdempotentAndIsNotAFailure()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            session.Disconnect();
            session.Disconnect();
            session.Disconnect();

            Assert.AreEqual(ESessionState.Disconnected, session.State);
            Assert.IsNull(session.FailureReason, "主动断开不算失败");
            Assert.AreEqual(ETransportState.Closed, fake.State);
        }

        /// <summary>失败之后能重连：上一次的失败原因与心跳计数要**清干净**。</summary>
        [Test]
        public void ReconnectAfterFailure_ResetsState()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = new NetSession(fake, "测试玩家");

            session.Connect("127.0.0.1", 7777);
            session.Pump(0);

            fake.PushFrame(Ack(accepted: false, reason: "协议版本不一致"));
            session.Pump(0);
            Assert.AreEqual(ESessionState.Failed, session.State);

            session.Connect("127.0.0.1", 7777);

            Assert.AreEqual(2, fake.ConnectCount, "第二次 Connect 应当真的发出去了");
            Assert.IsNull(session.FailureReason, "重连后不该还挂着上一次的失败原因");
            Assert.AreEqual(0, session.PingsSent, "重连后心跳计数应当清零");

            session.Pump(0);
            Assert.AreEqual(ESessionState.Handshaking, session.State, "重连后应当重新走握手");
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>造一条服务端答复。</summary>
        /// <param name="accepted">是否接受。</param>
        /// <param name="playerId">分配的玩家 id。</param>
        /// <param name="reason">拒绝原因。</param>
        /// <param name="server">服务端版本标识。</param>
        /// <returns>序列化后的载荷。</returns>
        private static byte[] Ack(bool accepted, long playerId = 1, string reason = "", string server = "test-server")
        {
            return new ServerMessage
            {
                HandshakeAck = new HandshakeAck
                {
                    Accepted = accepted,
                    PlayerId = playerId,
                    ProtocolVersion = NetContract.Version,
                    ServerVersion = server,
                    TickHz = NetContract.TickRate,
                    Reason = reason,
                },
            }.ToByteArray();
        }

        /// <summary>造一个"已经握手完成"的会话（本组用例最常用的开局）。</summary>
        /// <param name="fake">假传输。</param>
        /// <returns>会话（在线，且已经发过一次心跳）。</returns>
        private static NetSession Online(FakeTransport fake)
        {
            NetSession session = new NetSession(fake, "测试玩家");

            session.Connect("127.0.0.1", 7777);
            session.Pump(0);                        // → 发握手

            fake.PushFrame(Ack(accepted: true, playerId: 7, server: "unit-server"));
            session.Pump(0);                        // → Online + 立刻一次心跳

            Assert.AreEqual(ESessionState.Online, session.State, "夹具没把会话带到 Online");

            return session;
        }
    }
}
