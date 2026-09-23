// ============================================================================
//  M3-S2b · `TcpTransport` 的测试（**真的开端口**，但只在 127.0.0.1 上）
//  对应验收：Docs\25-M3开工清单.md S2
//  被测：`Client\Assets\_Project\Framework.Net\TcpTransport.cs`
//
//  ---------------------------------------------------------------------------
//  为什么不能只测分帧器（S2a 已经测过 `FrameCodec`）
//  ---------------------------------------------------------------------------
//  `FrameCodecTests` 验的是"喂字节 → 出帧"这个**纯函数**。它管不到：
//    · `Pump` 到底有没有把发出去的东西真的写进 socket
//    · 一次 `Receive` 收到三条消息时会不会只交出一条（**粘包**）
//    · 半帧先到的时候会不会提前交（**拆包**）
//    · **对端关闭**（收到 0 字节）认不认得出来 —— 漏掉的表现是"一直以为自己还连着"
//    · 连不上（对方没监听）会不会**卡在 Connecting** 而不是报人话原因
//  这些只有"真开一个监听端 + 真连上去"才验得出来，所以这一组是真 socket 测试。
//
//  ---------------------------------------------------------------------------
//  三条让这类测试不"看运气"的写法
//  ---------------------------------------------------------------------------
//  ① 端口用 **0**（让系统分配空闲端口）—— 不写死 7777，避免"上次没退干净"或"端口被占"
//  ② 所有等待都是**有界的**（`PumpUntil` 带毫秒上限），失败时给得出"等的是什么"
//  ③ 对面那个"服务端"用**裸 `TcpClient` + `FrameCodec`** 手写，
//     不重用 `TcpTransport` —— 否则就成了"自己验自己"
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NBC.Framework.Net;
using NBC.Framework.Net.Adapter;
using NBC.Shared.Net;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>M3-S2b：TCP 传输的测试（回环真连接）。</summary>
    public sealed class TcpTransportTests
    {
        /// <summary>等待上限（毫秒）。本机回环远用不了这么久，超了就是真出问题。</summary>
        private const int WaitMs = 3000;

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

        /// <summary>反复 `Pump` 直到条件成立（有界等待）。</summary>
        /// <param name="transport">传输。</param>
        /// <param name="condition">条件。</param>
        /// <param name="what">等的是什么（失败信息用）。</param>
        /// <param name="timeoutMs">上限毫秒。</param>
        private static void PumpUntil(ITransport transport, Func<bool> condition, string what,
                                      int timeoutMs = WaitMs)
        {
            Stopwatch watch = Stopwatch.StartNew();

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                transport.Pump();

                if (condition())
                {
                    return;
                }

                Thread.Sleep(1);
            }

            transport.Pump();
            Assert.Fail("等了 " + timeoutMs + "ms 还没等到：" + what + "（当前：" + transport.Description + "）");
        }

        /// <summary>起一个回环监听端，返回端口号。</summary>
        /// <param name="listener">监听端。</param>
        /// <returns>端口。</returns>
        private static int StartLoopback(out TcpListener listener)
        {
            listener = new TcpListener(IPAddress.Loopback, 0);   // 0 = 让系统给个空闲端口
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        /// <summary>接受一个连进来的客户端（有界等待）。</summary>
        /// <param name="listener">监听端。</param>
        /// <returns>客户端。</returns>
        private static TcpClient AcceptWithin(TcpListener listener)
        {
            Assert.IsTrue(listener.Server.Poll(WaitMs * 1000, SelectMode.SelectRead),
                "等了 " + WaitMs + "ms 也没有客户端连进来");
            return listener.AcceptTcpClient();
        }

        /// <summary>从对面读够 <paramref name="count"/> 字节（有界等待）。</summary>
        /// <param name="stream">对面的流。</param>
        /// <param name="count">要读多少。</param>
        /// <param name="timeoutMs">上限毫秒。</param>
        /// <returns>读到的字节。</returns>
        private static byte[] ReadExact(NetworkStream stream, int count, int timeoutMs = WaitMs)
        {
            byte[] buffer = new byte[count];
            int read = 0;
            stream.ReadTimeout = timeoutMs;

            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);

                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            byte[] result = new byte[read];
            Buffer.BlockCopy(buffer, 0, result, 0, read);
            return result;
        }

        // ====================================================================
        //  一、连接
        // ====================================================================

        /// <summary>连到回环监听端：`Pump` 之后进入已连接，描述里带目标地址。</summary>
        [Test]
        public void Connect_ToLoopbackListener_BecomesConnected()
        {
            TcpListener listener;
            int port = StartLoopback(out listener);

            try
            {
                using (TcpTransport transport = new TcpTransport())
                {
                    Assert.AreEqual(ETransportState.Disconnected, transport.State, "刚造出来应当是「没连」");
                    Assert.IsFalse(transport.IsConnected);

                    transport.Connect("127.0.0.1", port);
                    PumpUntil(transport, () => transport.IsConnected, "连上 127.0.0.1:" + port);

                    Assert.AreEqual(ETransportState.Connected, transport.State);
                    StringAssert.Contains("127.0.0.1:" + port, transport.Description,
                        "描述里应当能看出连的是哪儿（排查时全靠它）");
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// 连一个没人监听的端口：必须**落到 Closed 并给人话原因**，不许卡在 Connecting。
        /// <para>（"一直停在连接中"是联机里最难查的一种表现：界面就转圈，日志什么都没有。）</para>
        /// </summary>
        [Test]
        public void Connect_ToClosedPort_ClosesWithHumanReason_NotStuckConnecting()
        {
            // 先占一个端口再放开：几乎必然没人监听（比写死 1 号端口稳）
            TcpListener probe;
            int port = StartLoopback(out probe);
            probe.Stop();

            List<string> reasons = new List<string>();
            using (TcpTransport transport = new TcpTransport())
            {
                transport.Closed += reasons.Add;
                transport.Connect("127.0.0.1", port);
                PumpUntil(transport, () => transport.State == ETransportState.Closed, "连接被拒绝后关闭");
            }

            Assert.AreEqual(1, reasons.Count, "Closed 只该报一次");
            StringAssert.Contains("连接", reasons[0], "原因应当是人话（当前：" + reasons[0] + "）");
        }

        /// <summary>
        /// 连一个不可达地址：必须在超时/报错后关闭，**不许永远停在 Connecting**。
        /// <para>
        /// ⚠️ 本机到底走"超时"还是"网络不可达"取决于路由表，所以这里只钉"一定会关闭 + 有原因"。
        /// 实测走的是哪条路，记在 Docs\27（探针输出）里。
        /// </para>
        /// </summary>
        [Test]
        public void Connect_ToUnreachableAddress_ClosesInsteadOfHanging()
        {
            List<string> reasons = new List<string>();
            using (TcpTransport transport = new TcpTransport(connectTimeoutMs: 300))
            {
                transport.Closed += reasons.Add;
                transport.Connect("10.255.255.1", 9);     // 私有网段里的黑洞地址
                PumpUntil(transport, () => transport.State == ETransportState.Closed,
                    "连不上就关闭（300ms 超时或路由报错）", 5000);
            }

            Assert.AreEqual(1, reasons.Count, "Closed 只该报一次");
            StringAssert.Contains("连接", reasons[0], "原因应当是人话（当前：" + reasons[0] + "）");
        }

        // ====================================================================
        //  二、发送：真的写进 socket，而且是一条**带小端长度前缀**的帧
        // ====================================================================

        /// <summary>发一条消息，对面收到的字节应当正好是 `FrameCodec.Encode` 的结果。</summary>
        [Test]
        public void Send_WritesOneFramedMessage()
        {
            TcpListener listener;
            int port = StartLoopback(out listener);

            try
            {
                using (TcpTransport transport = new TcpTransport())
                {
                    transport.Connect("127.0.0.1", port);
                    PumpUntil(transport, () => transport.IsConnected, "连上");

                    TcpClient peer = AcceptWithin(listener);
                    NetworkStream stream = peer.GetStream();

                    byte[] payload = Payload(16);
                    transport.Send(payload);

                    byte[] expected = FrameCodec.Encode(payload);
                    byte[] actual = ReadExact(stream, expected.Length);

                    CollectionAssert.AreEqual(expected, actual,
                        "对面收到的应当是[4 字节小端长度][载荷]");

                    Assert.AreEqual(1, transport.Stats.FramesSent);
                    Assert.AreEqual(expected.Length, transport.Stats.BytesSent);
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>还没连上就发：抛异常（**不许静默丢**）。</summary>
        [Test]
        public void Send_BeforeConnect_Throws()
        {
            using (TcpTransport transport = new TcpTransport())
            {
                Assert.Throws<InvalidOperationException>(() => transport.Send(Payload(4)));
            }
        }

        /// <summary>超过单帧上限的载荷：`Send` 当场抛（不能等到对面断开才发现）。</summary>
        [Test]
        public void Send_OversizePayload_Throws()
        {
            TcpListener listener;
            int port = StartLoopback(out listener);

            try
            {
                using (TcpTransport transport = new TcpTransport(maxFrameBytes: 64))
                {
                    transport.Connect("127.0.0.1", port);
                    PumpUntil(transport, () => transport.IsConnected, "连上");

                    Assert.Throws<ArgumentException>(() => transport.Send(Payload(65)));
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        // ====================================================================
        //  三、接收：粘包 / 拆包 / 对端关闭 / 协议违规
        // ====================================================================

        /// <summary>对面一次写三条消息粘在一起：本端应当收到**三次**回调，内容各归各。</summary>
        [Test]
        public void Receive_ThreeFramesGluedTogether_DeliversThreeFrames()
        {
            TcpListener listener;
            int port = StartLoopback(out listener);

            try
            {
                using (TcpTransport transport = new TcpTransport())
                {
                    List<byte[]> got = new List<byte[]>();
                    transport.FrameReceived += got.Add;

                    transport.Connect("127.0.0.1", port);
                    PumpUntil(transport, () => transport.IsConnected, "连上");

                    TcpClient peer = AcceptWithin(listener);
                    NetworkStream stream = peer.GetStream();

                    byte[] a = FrameCodec.Encode(Payload(5, 10));
                    byte[] b = FrameCodec.Encode(Payload(0));        // 0 长度帧也得算一条
                    byte[] c = FrameCodec.Encode(Payload(3, 200));

                    stream.Write(a, 0, a.Length);
                    stream.Write(b, 0, b.Length);
                    stream.Write(c, 0, c.Length);
                    stream.Flush();

                    PumpUntil(transport, () => got.Count >= 3, "收到三条消息");

                    Assert.AreEqual(3, got.Count, "粘包必须被拆成三条（当前 " + got.Count + "）");
                    CollectionAssert.AreEqual(Payload(5, 10), got[0]);
                    Assert.AreEqual(0, got[1].Length, "0 长度帧也要交出来");
                    CollectionAssert.AreEqual(Payload(3, 200), got[2]);
                    Assert.AreEqual(3, transport.Stats.FramesReceived);
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>对面把一帧拆成两次写：**前半截不许提前交出来**，补齐后才是一条。</summary>
        [Test]
        public void Receive_HalfFrame_DoesNotDeliverUntilComplete()
        {
            TcpListener listener;
            int port = StartLoopback(out listener);

            try
            {
                using (TcpTransport transport = new TcpTransport())
                {
                    List<byte[]> got = new List<byte[]>();
                    transport.FrameReceived += got.Add;

                    transport.Connect("127.0.0.1", port);
                    PumpUntil(transport, () => transport.IsConnected, "连上");

                    TcpClient peer = AcceptWithin(listener);
                    NetworkStream stream = peer.GetStream();

                    byte[] payload = Payload(12);
                    byte[] frame = FrameCodec.Encode(payload);

                    // 先只发"长度前缀"这一截
                    stream.Write(frame, 0, NetContract.FrameLengthPrefixBytes);
                    stream.Flush();

                    // 给它足够的时间到达本端，并确认**没有**提前交帧
                    Stopwatch watch = Stopwatch.StartNew();

                    while (watch.ElapsedMilliseconds < 200)
                    {
                        transport.Pump();
                        Thread.Sleep(1);
                    }

                    Assert.AreEqual(0, got.Count, "只到了一半，不该交出任何一帧");

                    // 补齐后半截
                    stream.Write(frame, NetContract.FrameLengthPrefixBytes, frame.Length - NetContract.FrameLengthPrefixBytes);
                    stream.Flush();

                    PumpUntil(transport, () => got.Count >= 1, "补齐后收到这一帧");

                    Assert.AreEqual(1, got.Count);
                    CollectionAssert.AreEqual(payload, got[0]);
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// 对端正常关闭（FIN）：本端必须报 `Closed`。
        /// <para>漏掉这条的表现是"对方早就退了，本端还一直以为自己连着"。</para>
        /// </summary>
        [Test]
        public void Receive_PeerCloses_ReportsClosed()
        {
            TcpListener listener;
            int port = StartLoopback(out listener);

            try
            {
                List<string> reasons = new List<string>();
                using (TcpTransport transport = new TcpTransport())
                {
                    transport.Closed += reasons.Add;
                    transport.Connect("127.0.0.1", port);
                    PumpUntil(transport, () => transport.IsConnected, "连上");

                    TcpClient peer = AcceptWithin(listener);
                    peer.Close();

                    PumpUntil(transport, () => transport.State == ETransportState.Closed, "认出对端关闭");
                }

                Assert.AreEqual(1, reasons.Count, "Closed 只该报一次");
                StringAssert.Contains("对端关闭", reasons[0], "原因应当说明是对端关的（当前：" + reasons[0] + "）");
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// 对面的长度前缀撒谎（超过上限）：判**协议违规**并断开，而不是照着这个长度攒内存。
        /// </summary>
        [Test]
        public void Receive_OversizeLengthPrefix_ClosesAsProtocolViolation()
        {
            TcpListener listener;
            int port = StartLoopback(out listener);

            try
            {
                List<string> reasons = new List<string>();
                using (TcpTransport transport = new TcpTransport())
                {
                    transport.Closed += reasons.Add;
                    transport.Connect("127.0.0.1", port);
                    PumpUntil(transport, () => transport.IsConnected, "连上");

                    TcpClient peer = AcceptWithin(listener);
                    NetworkStream stream = peer.GetStream();

                    // 只写一个撒谎的长度前缀（声称比上限还大），后面一个字节都不给
                    byte[] liar = FrameCodec.Encode(Payload(NetContract.MaxFrameBytes + 1));

                    try
                    {
                        stream.Write(liar, 0, NetContract.FrameLengthPrefixBytes);
                        stream.Flush();
                    }
                    catch (Exception)
                    {
                        // 本端可能在对面写完之前就断开了：不影响这条用例要验的事
                    }

                    PumpUntil(transport, () => transport.State == ETransportState.Closed, "判协议违规断开");
                }

                Assert.AreEqual(1, reasons.Count, "Closed 只该报一次");
                StringAssert.Contains("超过上限", reasons[0],
                    "原因应当指出是长度超限（当前：" + reasons[0] + "）");
            }
            finally
            {
                listener.Stop();
            }
        }

        // ====================================================================
        //  四、关闭与统计
        // ====================================================================

        /// <summary>主动关闭：幂等，`Closed` 只报一次，关闭后再 `Send` 抛异常。</summary>
        [Test]
        public void Close_IsIdempotent_AndReportsOnce()
        {
            TcpListener listener;
            int port = StartLoopback(out listener);

            try
            {
                List<string> reasons = new List<string>();
                TcpTransport transport = new TcpTransport();
                transport.Closed += reasons.Add;

                transport.Connect("127.0.0.1", port);
                PumpUntil(transport, () => transport.IsConnected, "连上");

                transport.Close();
                transport.Close();
                transport.Close();

                Assert.AreEqual(ETransportState.Closed, transport.State);
                Assert.AreEqual(1, reasons.Count, "重复 Close 不该重复报");
                Assert.AreEqual("主动关闭", reasons[0]);

                Assert.Throws<InvalidOperationException>(() => transport.Send(Payload(4)),
                    "关掉之后 Send 必须报错，不许静默丢");

                transport.Dispose();
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>收发统计：帧数与字节数（含 4 字节帧头）要对得上。</summary>
        [Test]
        public void Stats_CountFramesAndBytesIncludingPrefix()
        {
            TcpListener listener;
            int port = StartLoopback(out listener);

            try
            {
                using (TcpTransport transport = new TcpTransport())
                {
                    List<byte[]> got = new List<byte[]>();
                    transport.FrameReceived += got.Add;

                    transport.Connect("127.0.0.1", port);
                    PumpUntil(transport, () => transport.IsConnected, "连上");

                    TcpClient peer = AcceptWithin(listener);
                    NetworkStream stream = peer.GetStream();

                    transport.Send(Payload(20, 3));
                    transport.Send(Payload(0));

                    // 对面把两条都收干净（不然本端统计"发出去了"而对面没收到就白测了）
                    ReadExact(stream, (NetContract.FrameLengthPrefixBytes + 20) +
                                     (NetContract.FrameLengthPrefixBytes + 0));

                    byte[] back = FrameCodec.Encode(Payload(7, 90));
                    stream.Write(back, 0, back.Length);
                    stream.Flush();

                    PumpUntil(transport, () => got.Count >= 1, "收到一条");

                    TransportStats stats = transport.Stats;

                    Assert.AreEqual(2, stats.FramesSent);
                    Assert.AreEqual(24 + 4, stats.BytesSent, "含帧头：20+4 和 0+4");
                    Assert.AreEqual(1, stats.FramesReceived);
                    Assert.AreEqual(7 + 4, stats.BytesReceived);
                }
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
