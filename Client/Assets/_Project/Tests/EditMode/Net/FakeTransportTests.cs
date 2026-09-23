// ============================================================================
//  M3-S2b · `FakeTransport` 自己的测试（**测试替身也要有对照**）
//  对应验收：Docs\25-M3开工清单.md S2
//
//  ---------------------------------------------------------------------------
//  为什么"假传输"也要写用例
//  ---------------------------------------------------------------------------
//  它是后面 M3-S4~S9 那一堆"房间/快照/伤害同步"用例的**地基**：
//  如果这个替身悄悄宽容了点什么（例如"没连上也能 Send，默默记下来"），
//  那么建在它上面的用例就会**全绿但无意义** —— 这种假信心比没有测试更糟。
//  所以这里钉住三件与真实实现**故意对齐**的行为：
//
//    ① 没连上就 `Send` → 抛异常（和 `TcpTransport` 同一条契约）
//    ② 收到的一帧**只在 `Pump` 里**交出来（调用方的"每帧收一次"写法两种实现都成立）
//    ③ 进出都**拷贝载荷** —— 不然用例里改一下手里的数组就能改到"对面收到的内容"，
//       那是"测试自己给自己放水"的经典形态
// ============================================================================

using System.Collections.Generic;
using NBC.Framework.Net;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>M3-S2b：假传输的测试（它自己也得守契约）。</summary>
    public sealed class FakeTransportTests
    {
        /// <summary>造一段可辨认的载荷。</summary>
        /// <param name="length">长度。</param>
        /// <param name="seed">起点值。</param>
        /// <returns>载荷。</returns>
        private static byte[] Payload(int length, int seed = 1)
        {
            byte[] result = new byte[length];

            for (int i = 0; i < length; i++)
            {
                result[i] = (byte)((seed + i) & 0xFF);
            }

            return result;
        }

        /// <summary>与真实实现同一条契约：没连上就发 → 抛异常（不是静默记下来）。</summary>
        [Test]
        public void Send_BeforeConnect_Throws()
        {
            FakeTransport transport = new FakeTransport();

            Assert.AreEqual(ETransportState.Disconnected, transport.State);
            Assert.Throws<System.InvalidOperationException>(() => transport.Send(Payload(4)));
            Assert.AreEqual(0, transport.SentFrames.Count, "报错了就不该留下「发过了」的记录");
        }

        /// <summary>收到的一帧只在 `Pump` 里交出去，而且按顺序。</summary>
        [Test]
        public void Pump_DeliversQueuedFramesInOrder()
        {
            FakeTransport transport = new FakeTransport();
            transport.Connect("127.0.0.1", 7777);

            List<byte[]> got = new List<byte[]>();
            transport.FrameReceived += got.Add;

            transport.PushFrame(Payload(3, 10));
            transport.PushFrame(Payload(5, 20));

            Assert.AreEqual(0, got.Count, "没 Pump 之前一条都不该交出去");

            transport.Pump();

            Assert.AreEqual(2, got.Count);
            CollectionAssert.AreEqual(Payload(3, 10), got[0]);
            CollectionAssert.AreEqual(Payload(5, 20), got[1]);
            Assert.AreEqual(2, transport.FramesReceived);
        }

        /// <summary>`PushFrame` 会拷贝载荷：之后改手里的数组不影响"对面发来的内容"。</summary>
        [Test]
        public void PushFrame_ClonesPayload()
        {
            FakeTransport transport = new FakeTransport();
            transport.Connect("127.0.0.1", 7777);

            List<byte[]> got = new List<byte[]>();
            transport.FrameReceived += got.Add;

            byte[] original = Payload(4, 1);
            transport.PushFrame(original);
            original[0] = 0xFF;         // 发送方改了自己的缓冲（真实网络里当然不会传到对面）

            transport.Pump();

            Assert.AreEqual(1, got.Count);
            Assert.AreEqual(1, got[0][0], "交出来的应当是**当时那一刻**的内容");
        }

        /// <summary>`Send` 会拷贝载荷，而且记在 `SentFrames` / `LastSent` 里。</summary>
        [Test]
        public void Send_RecordsPayloadCopy()
        {
            FakeTransport transport = new FakeTransport();
            transport.Connect("127.0.0.1", 7777);

            byte[] payload = Payload(6, 30);
            transport.Send(payload);
            payload[0] = 0xFF;

            Assert.AreEqual(1, transport.SentFrames.Count);
            Assert.AreEqual(30, transport.SentFrames[0][0], "记下来的应当是发出去那一刻的内容");
            Assert.AreSame(transport.SentFrames[0], transport.LastSent);

            transport.ClearSent();
            Assert.AreEqual(0, transport.SentFrames.Count);
            Assert.IsNull(transport.LastSent);
        }

        /// <summary>断线：报一次原因，之后不再交出任何帧。</summary>
        [Test]
        public void FailWith_ReportsReasonOnce_AndStopsDelivery()
        {
            FakeTransport transport = new FakeTransport();
            transport.Connect("127.0.0.1", 7777);

            List<string> reasons = new List<string>();
            List<byte[]> got = new List<byte[]>();
            transport.Closed += reasons.Add;
            transport.FrameReceived += got.Add;

            transport.PushFrame(Payload(4));
            transport.FailWith("模拟掉线");
            transport.FailWith("再报一次试试");
            transport.Pump();

            Assert.AreEqual(ETransportState.Closed, transport.State);
            Assert.AreEqual(1, reasons.Count, "Closed 至多一次");
            Assert.AreEqual("模拟掉线", reasons[0]);
            Assert.AreEqual(0, got.Count, "已经断了就不该再交帧");
            StringAssert.Contains("模拟掉线", transport.Description);
        }

        /// <summary>`Connect` 记下目标（用例要断言"连的是哪儿"时全靠它）。</summary>
        [Test]
        public void Connect_RecordsEndpointAndReachesConnected()
        {
            FakeTransport transport = new FakeTransport();
            transport.Connect("127.0.0.1", 7788);

            Assert.AreEqual("127.0.0.1", transport.Host);
            Assert.AreEqual(7788, transport.Port);
            Assert.AreEqual(1, transport.ConnectCount);
            Assert.IsTrue(transport.IsConnected);
            StringAssert.Contains("127.0.0.1:7788", transport.Description);
        }
    }
}
