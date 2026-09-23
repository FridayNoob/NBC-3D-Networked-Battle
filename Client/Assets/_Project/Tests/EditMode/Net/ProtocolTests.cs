// ============================================================================
//  M3-S1 · 协议测试：生成物能用 + **契约版本号两处一致**
//  对应验收：Docs\25-M3开工清单.md S1
//  被测：`Protocol\nbc_m3.proto` → protoc 生成的 `NBC.Protocol`（`_Project\Protocol\NbcM3.cs`）
//
//  ---------------------------------------------------------------------------
//  这一组测两件不同的事，别混
//  ---------------------------------------------------------------------------
//  ① **生成物真的能用**：序列化 → 反序列化 → 字段一致；`oneof` 走对分支；
//     `repeated` 保序。这是"协议能过网线"的最小证据。
//  ② **契约版本号两处一致**（`NetContract.Version` ↔ `.proto` 顶部的 `CONTRACT_VERSION`）：
//     这是**机械检查**，不是功能测试 —— "改了协议忘了升版本"是典型的**静默漂移**：
//     两端都能编过、单端测试全绿，只有联机时才表现为"某个字段对不上"。
//
//  ⚠️ 第 ② 条为什么要读那个 `.proto` 文件：因为它与常量是**两份数据**，
//     而两份数据一定会漂移 —— 除非有东西盯着（D1 那条"两份拷贝一定会漂移"的同一族教训）。
// ============================================================================

using System;
using System.IO;
using System.Text.RegularExpressions;
using Google.Protobuf;
using NBC.Protocol;
using NBC.Shared.Net;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>M3-S1：M3 协议的测试。</summary>
    public sealed class ProtocolTests
    {
        /// <summary>仓库根（`Assets` 的上一级再上一级）。</summary>
        private static string RepoRoot
        {
            get
            {
                string assets = UnityEngine.Application.dataPath;
                return Path.GetDirectoryName(Path.GetDirectoryName(assets));
            }
        }

        /// <summary>`.proto` 的路径。</summary>
        private static string ProtoPath
        {
            get { return Path.Combine(RepoRoot, "Protocol", "nbc_m3.proto"); }
        }

        // ====================================================================
        //  一、生成物能用（序列化往返）
        // ====================================================================

        /// <summary>握手消息：序列化再反序列化，字段一个不差。</summary>
        [Test]
        public void Handshake_RoundTrips()
        {
            Handshake sent = new Handshake
            {
                ProtocolVersion = NetContract.Version,
                ClientVersion = "editor-2026.09.23",
                PlayerName = "剑士"
            };

            Handshake received = Handshake.Parser.ParseFrom(sent.ToByteArray());

            Assert.AreEqual(sent.ProtocolVersion, received.ProtocolVersion);
            Assert.AreEqual(sent.ClientVersion, received.ClientVersion);
            Assert.AreEqual("剑士", received.PlayerName, "中文（UTF-8）要能原样过一趟");
        }

        /// <summary>`WorldSnapshot` 里的 `repeated` 要**保序**（表现层按这个顺序建实体）。</summary>
        [Test]
        public void WorldSnapshot_KeepsRepeatedOrder()
        {
            WorldSnapshot sent = new WorldSnapshot { ServerTick = 42 };
            sent.Entities.Add(new EntitySnapshot { EntityId = 3, ConfigId = 6001, Hp = 300, MaxHp = 300, Alive = true });
            sent.Entities.Add(new EntitySnapshot { EntityId = 7, ConfigId = 6002, Hp = 120, MaxHp = 200, Alive = true });
            sent.Entities.Add(new EntitySnapshot { EntityId = 9, ConfigId = 6003, Hp = 0, MaxHp = 2000, Alive = false });

            WorldSnapshot received = WorldSnapshot.Parser.ParseFrom(sent.ToByteArray());

            Assert.AreEqual(42, received.ServerTick);
            Assert.AreEqual(3, received.Entities.Count);
            Assert.AreEqual(3, received.Entities[0].EntityId);
            Assert.AreEqual(6002, received.Entities[1].ConfigId);
            Assert.IsFalse(received.Entities[2].Alive, "第 3 个是死的");

            // 实例编号与配置编号是**两个字段**（M2 的教训：同一个配置刷两只不能撞号）
            Assert.AreEqual(6001, received.Entities[0].ConfigId);
            Assert.AreNotEqual(received.Entities[0].EntityId, received.Entities[0].ConfigId);
        }

        /// <summary>`oneof` 要走对分支，而且**一次只有一件事**。</summary>
        [Test]
        public void ServerMessage_OneofPicksTheRightBranch()
        {
            ServerMessage message = new ServerMessage
            {
                Event = new ServerEvent
                {
                    Drop = new DropEvent { ItemId = 7001, Count = 2, WinnerPlayerId = 12345L }
                }
            };

            ServerMessage received = ServerMessage.Parser.ParseFrom(message.ToByteArray());

            Assert.AreEqual(ServerMessage.PayloadOneofCase.Event, received.PayloadCase);
            Assert.AreEqual(ServerEvent.EventOneofCase.Drop, received.Event.EventCase);
            Assert.AreEqual(7001, received.Event.Drop.ItemId);
            Assert.AreEqual(2, received.Event.Drop.Count);
            Assert.AreEqual(12345L, received.Event.Drop.WinnerPlayerId);

            // 没设的分支应当是 **null**（proto3 的 message 型 oneof 成员：未设置就返回 null），
            //  ⚠️ 不是"上一次的值"、也不是 DefaultInstance（这个 codegen 版本里没有 DefaultInstance）
            Assert.IsNull(received.Event.Damage, "没设的那个分支应当是 null");
        }

        /// <summary>客户端消息同样（`PlayerInput` 是每帧都发的，形状要稳）。</summary>
        [Test]
        public void ClientMessage_CarriesPlayerInput()
        {
            ClientMessage message = new ClientMessage
            {
                Input = new PlayerInput
                {
                    PlayerId = 1L,
                    ClientTick = 99,
                    MoveX = 1000,
                    MoveY = -1000,
                    ActionBits = 0b101u,
                    ActionReleaseBits = 0b010u,
                    TargetEntityId = 7
                }
            };

            ClientMessage received = ClientMessage.Parser.ParseFrom(message.ToByteArray());

            Assert.AreEqual(ClientMessage.PayloadOneofCase.Input, received.PayloadCase);
            Assert.AreEqual(1000, received.Input.MoveX);
            Assert.AreEqual(-1000, received.Input.MoveY);
            Assert.AreEqual(0b101u, received.Input.ActionBits, "位掩码要原样过去（A7 的量化约定）");
            Assert.AreEqual(0b010u, received.Input.ActionReleaseBits);
        }

        /// <summary>空消息也要能过（握手前的第一帧、心跳等场景）。</summary>
        [Test]
        public void EmptyMessage_RoundTrips()
        {
            ClientMessage sent = new ClientMessage();
            ClientMessage received = ClientMessage.Parser.ParseFrom(sent.ToByteArray());

            Assert.AreEqual(ClientMessage.PayloadOneofCase.None, received.PayloadCase);
        }

        // ====================================================================
        //  二、契约版本号：`.proto` 与常量必须一致（**机械检查**）
        // ====================================================================

        /// <summary>
        /// `NetContract.Version` 必须等于 `.proto` 顶部的 `CONTRACT_VERSION`。
        /// <para>
        /// ⚠️ 这条不是为了"测试协议能跑"，而是为了**挡住静默漂移**：
        /// 改了协议不升版本，两端都能编过、单端测试全绿，**只有联机时才暴露**。
        /// </para>
        /// </summary>
        [Test]
        public void ContractVersion_MatchesProtoFile()
        {
            Assert.IsTrue(File.Exists(ProtoPath), "找不到协议文件：" + ProtoPath);

            string text = File.ReadAllText(ProtoPath);
            Match match = Regex.Match(text, @"CONTRACT_VERSION:\s*(\d+)");

            Assert.IsTrue(match.Success,
                "`.proto` 顶部应当有一行 `// CONTRACT_VERSION: N`（机械检查靠它比对）。");

            int inProto = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            Assert.AreEqual(inProto, NetContract.Version,
                "协议版本号不一致：`.proto` 说 " + inProto + "，代码说 " + NetContract.Version + "。\n" +
                "改协议时**两处都要改**（这条检查就是专门盯这件事的）。");
        }

        /// <summary>阳性对照：解析规则本身要能认出版本号（否则上面那条会"永远通过"）。</summary>
        [Test]
        public void VersionRegex_ActuallyWorks()
        {
            Match match = Regex.Match("//  CONTRACT_VERSION: 7\nsyntax = \"proto3\";", @"CONTRACT_VERSION:\s*(\d+)");

            Assert.IsTrue(match.Success, "应当能解析出这条注释");
            Assert.AreEqual("7", match.Groups[1].Value);

            Assert.IsFalse(Regex.IsMatch("// 没有版本号这一行", @"CONTRACT_VERSION:\s*(\d+)"),
                "没有版本号时不该匹配（否则检查会假绿）");
        }

        // ====================================================================
        //  三、常量之间的关系（自己跟自己要对得上）
        // ====================================================================

        /// <summary>
        /// M3-S4 追加的两条消息要能真的过一趟（加消息是**兼容**改动，但生成物得能用）。
        /// <para>
        /// ⚠️ 这条同时钉住一件事：**`CONTRACT_VERSION` 不应该因为它俩而 +1**。
        /// 判据在 `.proto` 顶部 —— 只有"删字段 / 改类型 / 改语义"才叫不兼容，
        /// 加消息、加 oneof 分支都是兼容的（proto3 会忽略未知字段）。
        /// </para>
        /// </summary>
        [Test]
        public void S4Messages_RoundTrip()
        {
            ClientMessage leaving = new ClientMessage { LeaveRoom = new LeaveRoomRequest() };
            ClientMessage back = ClientMessage.Parser.ParseFrom(leaving.ToByteArray());

            Assert.AreEqual(ClientMessage.PayloadOneofCase.LeaveRoom, back.PayloadCase,
                "leave_room 应当落在正确的 oneof 分支上");

            ServerMessage error = new ServerMessage
            {
                Error = new ErrorResponse { Code = NetErrors.RoomFull, Message = "房间 r1 已满（4/4）" },
            };
            ServerMessage errorBack = ServerMessage.Parser.ParseFrom(error.ToByteArray());

            Assert.AreEqual(ServerMessage.PayloadOneofCase.Error, errorBack.PayloadCase);
            Assert.AreEqual(NetErrors.RoomFull, errorBack.Error.Code);
            StringAssert.Contains("已满", errorBack.Error.Message, "中文说明要能原样过网线");
        }

        /// <summary>帧间隔与 tick 率必须自洽（30Hz → 33ms）。</summary>
        [Test]
        public void Constants_AreSelfConsistent()
        {
            Assert.AreEqual(30, NetContract.TickRate, "需求 §6.1 定的是 30Hz");
            Assert.AreEqual(1000 / NetContract.TickRate, NetContract.TickIntervalMs);
            Assert.Greater(NetContract.MaxFrameBytes, 0, "上限必须是正数，否则'防吃内存'那条就是摆设");
            Assert.GreaterOrEqual(NetContract.MaxRoomMembers, 2, "至少得能两个人（M3 的验收就是 2 人）");
        }

        /// <summary>
        /// `SharedInfo` 的标识里**只允许出现一个速率**（30Hz 这件事只能有一个来源）。
        /// <para>
        /// ⚠️ 这条测试的前身是 `TickRate_MatchesSharedLayerLogicRate`（断言两个常量相等）。
        /// M3-S5 把 `SharedInfo.LogicTickRate` 改成**派生自** `NetContract.TickRate` 之后，
        /// 那条断言**恒真**了 —— 一个永远不会红的检查就是**死检查**（本项目"走不到的分支比没有分支更糟"同源），
        /// 所以删掉它，换成这条"防人再加一个速率"的检查。
        /// </para>
        /// <para>
        /// 它抓的是真实事故：`SharedInfo.SnapshotSendRate = 20` 曾经与 D5 的"每 tick 全量快照"（30）
        /// 各说各话，而那个 20 **一行代码都没用到** —— 唯一作用是让启动日志显示一个错数字。
        /// </para>
        /// </summary>
        [Test]
        public void SharedInfoDescribe_MentionsOnlyOneRate()
        {
            string text = NBC.Shared.SharedInfo.Describe();

            StringAssert.Contains("LogicTick=" + NetContract.TickRate + "Hz", text,
                "标识里应当出现协议契约的 tick 率（当前：" + text + "）");

            Assert.IsFalse(text.Contains("Snapshot"),
                "标识里不该再出现第二个速率（快照与 tick 的关系已定：**每 tick 一张**，见 Docs\\25 §8.5）。" +
                "当前：" + text);
        }
    }
}
