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
        //  五、房间与席位（S4）
        // ====================================================================

        /// <summary>进房：发出去的应当是 `JoinRoomRequest`，字段原样。</summary>
        [Test]
        public void JoinRoom_SendsJoinRequest()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            session.JoinRoom("r1", 1001);

            ClientMessage sent = ClientMessage.Parser.ParseFrom(fake.LastSent);
            Assert.AreEqual(ClientMessage.PayloadOneofCase.JoinRoom, sent.PayloadCase);
            Assert.AreEqual("r1", sent.JoinRoom.RoomId);
            Assert.AreEqual(1001, sent.JoinRoom.DungeonId);
        }

        /// <summary>收到席位表：`CurrentRoom` 更新、事件发出来、`InRoom` 为真。</summary>
        [Test]
        public void RoomState_UpdatesCurrentRoom()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            List<RoomState> changed = new List<RoomState>();
            session.RoomChanged += changed.Add;

            fake.PushFrame(Room("r1", 1001, 4, new[] { ("剑士", true), ("法师", false) }));
            session.Pump(0);

            Assert.AreEqual(1, changed.Count, "RoomChanged 应当发一次");
            Assert.IsTrue(session.InRoom);
            Assert.AreEqual("r1", session.CurrentRoom.RoomId);
            Assert.AreEqual(1001, session.CurrentRoom.DungeonId);
            Assert.AreEqual(2, session.CurrentRoom.Members.Count);
            Assert.IsTrue(session.CurrentRoom.Members[0].IsHost, "第一个人是房主");
            Assert.IsFalse(session.CurrentRoom.Members[1].IsHost);
        }

        /// <summary>
        /// 服务端下发的"空房号状态" = 你不在任何房间。
        /// <para>⚠️ 这条是**约定**（`RoomService` 文件头第二节）：<c>CurrentRoom != null</c> 不等于"在房里"，
        /// 判断必须用 <c>InRoom</c>。写错的话，离开房间之后界面还会显示房间号。</para>
        /// </summary>
        [Test]
        public void EmptyRoomState_MeansNotInRoom()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            fake.PushFrame(Room("", 0, 0, new (string, bool)[0]));
            session.Pump(0);

            Assert.IsNotNull(session.CurrentRoom, "状态本身是收到了的");
            Assert.IsFalse(session.InRoom, "房号为空 = 不在任何房间");
        }

        /// <summary>被拒：记下错误码与人话，**但状态仍然是在线**（被拒 ≠ 掉线）。</summary>
        [Test]
        public void Error_IsRecordedWithoutDroppingTheSession()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            List<ErrorResponse> errors = new List<ErrorResponse>();
            session.ErrorReceived += errors.Add;

            fake.PushFrame(new ServerMessage
            {
                Error = new ErrorResponse { Code = NetErrors.RoomFull, Message = "房间 r1 已满（4/4）" },
            }.ToByteArray());

            session.Pump(0);

            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual(NetErrors.RoomFull, session.LastError.Code);
            StringAssert.Contains("已满", session.LastError.Message);
            Assert.AreEqual(ESessionState.Online, session.State,
                "被拒只是这个请求没成，会话还好好的 —— 把状态打成失败是过度反应");
        }

        /// <summary>离开房间：发出去的应当是 `LeaveRoomRequest`。</summary>
        [Test]
        public void LeaveRoom_SendsLeaveRequest()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            session.LeaveRoom();

            ClientMessage sent = ClientMessage.Parser.ParseFrom(fake.LastSent);
            Assert.AreEqual(ClientMessage.PayloadOneofCase.LeaveRoom, sent.PayloadCase);
        }

        /// <summary>重连要把房间与错误清干净（否则新一局界面上还挂着上一局的房号）。</summary>
        [Test]
        public void Reconnect_ClearsRoomAndError()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            fake.PushFrame(Room("r1", 1001, 4, new[] { ("剑士", true) }));
            fake.PushFrame(new ServerMessage
            {
                Error = new ErrorResponse { Code = NetErrors.RoomFull, Message = "满了" },
            }.ToByteArray());
            session.Pump(0);

            Assert.IsTrue(session.InRoom);
            Assert.IsNotNull(session.LastError);

            session.Disconnect();
            session.Connect("127.0.0.1", 7777);

            Assert.IsFalse(session.InRoom, "重连后不该还挂着上一局的房间");
            Assert.IsNull(session.CurrentRoom);
            Assert.IsNull(session.LastError);
        }

        // ====================================================================
        //  六、输入上行（S5b：只发意图）
        // ====================================================================

        /// <summary>发输入：字段原样（轴、动作位、目标、player_id 必须是我自己）。</summary>
        [Test]
        public void SendInput_CarriesAxesActionsAndMyPlayerId()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            session.SendInput(moveX: 500, moveY: -250, actionBits: 1u, actionReleaseBits: 0u, targetEntityId: 3);

            ClientMessage sent = ClientMessage.Parser.ParseFrom(fake.LastSent);
            Assert.AreEqual(ClientMessage.PayloadOneofCase.Input, sent.PayloadCase);
            Assert.AreEqual(7, sent.Input.PlayerId, "player_id 必须是服务端发给我自己的那个（夹具里是 7）");
            Assert.AreEqual(500, sent.Input.MoveX);
            Assert.AreEqual(-250, sent.Input.MoveY);
            Assert.AreEqual(1u, sent.Input.ActionBits, "第 0 位 = 技能1/普攻");
            Assert.AreEqual(3, sent.Input.TargetEntityId);
            Assert.AreEqual(1, session.InputsSent);
        }

        /// <summary>A7 的 `InputCommand` 能**直接**转成协议输入（量化约定是同一套，不用换算）。</summary>
        [Test]
        public void SendInput_FromInputCommand_MapsEveryField()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            session.SendInput(new NBC.Framework.Input.InputCommand(
                tick: 42, moveX: 1000, moveY: -1000, actionBits: 2u, actionReleaseBits: 4u));

            ClientMessage sent = ClientMessage.Parser.ParseFrom(fake.LastSent);
            Assert.AreEqual(1000, sent.Input.MoveX, "轴直接就是协议轴（A7 已经量化到 -1000..1000）");
            Assert.AreEqual(-1000, sent.Input.MoveY);
            Assert.AreEqual(2u, sent.Input.ActionBits);
            Assert.AreEqual(4u, sent.Input.ActionReleaseBits);
        }

        /// <summary>`client_tick` 是"第几条输入"（M3 没有客户端逻辑帧，见 `NextInputSeq` 的注释）。</summary>
        [Test]
        public void SendInput_ClientTickIncrementsPerInput()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            session.SendInput(0, 0);
            session.SendInput(0, 0);
            session.SendInput(0, 0);

            Assert.AreEqual(3, session.InputsSent);

            ClientMessage last = ClientMessage.Parser.ParseFrom(fake.LastSent);
            Assert.AreEqual(3, last.Input.ClientTick, "序号应当逐条递增（服务端日志对账用）");
        }

        /// <summary>没上线就发输入：抛异常（和 `Send` 同一条判据，不静默丢）。</summary>
        [Test]
        public void SendInput_BeforeOnline_Throws()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = new NetSession(fake, "测试玩家");

            Assert.Throws<InvalidOperationException>(() => session.SendInput(1000, 0));
            Assert.AreEqual(0, session.InputsSent, "报错了就不该记「发出去过」");
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>造一条 `RoomState` 载荷。</summary>
        /// <param name="roomId">房号。</param>
        /// <param name="dungeonId">副本编号。</param>
        /// <param name="capacity">容量。</param>
        /// <param name="members">成员（名字 + 是否房主）。</param>
        /// <returns>序列化后的载荷。</returns>
        private static byte[] Room(string roomId, int dungeonId, int capacity,
                                   (string Name, bool IsHost)[] members)
        {
            var state = new RoomState
            {
                RoomId = roomId,
                DungeonId = dungeonId,
                Capacity = capacity,
                Phase = RoomPhase.Waiting,
            };

            for (int i = 0; i < members.Length; i++)
            {
                state.Members.Add(new RoomMember
                {
                    PlayerId = i + 1,
                    PlayerName = members[i].Name,
                    IsHost = members[i].IsHost,
                });
            }

            return new ServerMessage { RoomState = state }.ToByteArray();
        }

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
