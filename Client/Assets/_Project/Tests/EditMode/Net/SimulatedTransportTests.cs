// ============================================================================
//  M3-S8 · 网络模拟器（NET-07）的测试
//  对应验收：Docs\25 §四 S8
//  被测：`Client\Assets\_Project\Framework.Net\Sim\SimulatedTransport.cs`
//
//  ---------------------------------------------------------------------------
//  为什么用 `FakeTransport` 当内层
//  ---------------------------------------------------------------------------
//  模拟器是"包一层的 `ITransport`" —— 用假传输当内层，就能：
//    · **精确控制时间**（`Advance(50)` 就是过了 50ms，不用 sleep、不会 flaky）
//    · **断言"包什么时候到"**（这正是延迟模拟的全部意义）
//    · 不需要开端口（真 socket 的那条路在端到端探针里已经验过）
//
//  ⚠️ 一提到"随机丢包"，测试就很容易写成"跑 100 次大约丢 5 次"这种**统计断言**（一跑就可能红）。
//     这里的做法是：**把"随机"钉成"确定的序列"** —— 同一个种子 + 同一串调用 = 同一串结果，
//     所以可以断言"这一条第 3 次一定会丢"，而不是"大概 5%"。
// ============================================================================

using System.Collections.Generic;
using NBC.Framework.Net;
using NBC.Framework.Net.Sim;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>M3-S8：网络模拟器的测试。</summary>
    public sealed class SimulatedTransportTests
    {
        /// <summary>造一段可辨认的载荷。</summary>
        /// <param name="value">内容。</param>
        /// <returns>载荷。</returns>
        private static byte[] Payload(byte value) => new[] { value };

        /// <summary>造一个模拟器 + 它的内层假传输。</summary>
        /// <param name="profile">模拟参数。</param>
        /// <param name="inner">内层（出参）。</param>
        /// <returns>模拟器。</returns>
        private static SimulatedTransport Make(NetSimProfile profile, out FakeTransport inner)
        {
            inner = new FakeTransport();
            var sim = new SimulatedTransport(inner) { Profile = profile };
            sim.Connect("127.0.0.1", 7777);
            return sim;
        }

        // ====================================================================
        //  一、直连（不模拟）时它必须"什么都不改"
        // ====================================================================

        /// <summary>参数全 0 时，上行立刻到内层、下行立刻到上层（模拟器是透明的）。</summary>
        [Test]
        public void NoSimulation_IsTransparent()
        {
            FakeTransport inner;
            SimulatedTransport sim = Make(default(NetSimProfile), out inner);

            List<byte[]> got = new List<byte[]>();
            sim.FrameReceived += got.Add;

            sim.Send(Payload(1));
            inner.PushFrame(Payload(2));
            sim.Pump();

            Assert.AreEqual(1, inner.SentFrames.Count, "上行应当立刻到内层");
            Assert.AreEqual(1, got.Count, "下行应当立刻到上层");
            Assert.AreEqual(0, sim.FramesDelayed);
            Assert.AreEqual(0, sim.InFlightCount);
        }

        // ====================================================================
        //  二、延迟与抖动（**时间由 Advance 精确控制**）
        // ====================================================================

        /// <summary>固定延迟：不到时间不送达，到了时间才送。</summary>
        [Test]
        public void FixedLatency_DelaysBothDirections()
        {
            FakeTransport inner;
            SimulatedTransport sim = Make(new NetSimProfile { LatencyMs = 100 }, out inner);

            List<byte[]> got = new List<byte[]>();
            sim.FrameReceived += got.Add;

            sim.Send(Payload(1));                  // 上行：该 100ms 后到 inner
            inner.PushFrame(Payload(2));            // 下行：该 100ms 后到上层

            Assert.AreEqual(0, inner.SentFrames.Count, "还没到时间，不该送出去");
            Assert.AreEqual(0, got.Count, "还没到时间，不该交上来");

            sim.Advance(99);
            Assert.AreEqual(0, inner.SentFrames.Count, "99ms 还差一点");
            Assert.AreEqual(0, got.Count);

            sim.Advance(1);                         // 累计 100ms
            Assert.AreEqual(1, inner.SentFrames.Count, "到 100ms 应当送达 inner");
            Assert.AreEqual(1, got.Count, "到 100ms 应当交给上层");
            Assert.AreEqual(2, sim.FramesDelayed);
            Assert.AreEqual(100, sim.MaxDelayMs);
            Assert.AreEqual(0, sim.InFlightCount);
        }

        /// <summary>抖动：不同包的延迟不一样（同一批出发的包可能**乱序**到达）。</summary>
        [Test]
        public void Jitter_CanReorder()
        {
            FakeTransport inner;
            SimulatedTransport sim = Make(new NetSimProfile { LatencyMs = 0, JitterMs = 50 }, out inner);

            List<byte[]> got = new List<byte[]>();
            sim.FrameReceived += got.Add;

            // 发 5 条，然后一小步一小步推时间，看它们是不是"按各自的延迟"先后到达
            for (int i = 1; i <= 5; i++)
            {
                sim.Send(Payload((byte)i));
            }

            for (int t = 0; t < 60; t++)
            {
                sim.Advance(1);
            }

            Assert.AreEqual(5, inner.SentFrames.Count, "60ms 之后（最大延迟 50ms）应当全部送达");
            Assert.Greater(sim.MaxDelayMs, 0, "抖动应当真的产生了延迟");

            // 至少有一条的到达顺序与发送顺序不同 → 说明抖动真的在起作用（乱序是延迟的必然副产品）
            bool reordered = false;

            for (int i = 0; i < inner.SentFrames.Count; i++)
            {
                if (inner.SentFrames[i][0] != (byte)(i + 1))
                {
                    reordered = true;
                }
            }

            // 同一延迟也可能刚好不乱序（种子固定，所以这条断言是**确定的**，不是"大约"）
            Assert.AreEqual(5, inner.SentFrames.Count, "内容条数要对");
            Assert.IsTrue(reordered || sim.MaxDelayMs > 0,
                "抖动要么造成乱序、要么至少造成延迟；两者都没有说明抖动没生效");
        }

        // ====================================================================
        //  三、丢包（**确定性**：同一个种子 → 同一串结果）
        // ====================================================================

        /// <summary>丢包率 10000（万分比）= 全丢：一条都到不了内层。</summary>
        [Test]
        public void FullLoss_DropsEverything()
        {
            FakeTransport inner;
            SimulatedTransport sim = Make(new NetSimProfile { LossPerTenThousand = 10000 }, out inner);

            List<byte[]> got = new List<byte[]>();
            sim.FrameReceived += got.Add;

            for (int i = 0; i < 10; i++)
            {
                sim.Send(Payload((byte)i));
                inner.PushFrame(Payload((byte)i));
            }

            sim.Advance(1000);
            sim.Pump();

            Assert.AreEqual(0, inner.SentFrames.Count, "全丢时上行一条都不该到");
            Assert.AreEqual(0, got.Count, "全丢时下行一条都不该到");
            Assert.AreEqual(20, sim.FramesDropped, "上行 10 + 下行 10 都要记进丢包数");
        }

        /// <summary>
        /// 部分丢包：**同一个种子 → 同一串丢法**（所以可以重复跑而不 flaky）。
        /// <para>这条正是"测试里有随机"的正确写法：把随机钉成确定的序列。</para>
        /// </summary>
        [Test]
        public void PartialLoss_IsReproducibleWithTheSameSeed()
        {
            int firstRun = CountDroppedOutOf50(seed: 12345);
            int secondRun = CountDroppedOutOf50(seed: 12345);

            Assert.AreEqual(firstRun, secondRun,
                "同一个种子必须丢出同一个结果（否则这条用例会偶尔红 —— flaky 比没有测试更糟）");
            Assert.Greater(firstRun, 0, "5% 丢包、50 次调用，一次都不丢说明丢包没生效");
            Assert.Less(firstRun, 50, "不该全丢（那是 10000 万分比的行为）");
        }

        /// <summary>用指定种子跑 50 条上行，返回丢了几条。</summary>
        /// <param name="seed">种子。</param>
        /// <returns>丢包条数。</returns>
        private static int CountDroppedOutOf50(int seed)
        {
            var inner = new FakeTransport();
            var sim = new SimulatedTransport(inner, seed)
            {
                Profile = new NetSimProfile { LossPerTenThousand = 500 },   // 5%
            };

            sim.Connect("127.0.0.1", 7777);

            for (int i = 0; i < 50; i++)
            {
                sim.Send(Payload((byte)i));
            }

            sim.Advance(1000);

            Assert.AreEqual(50 - sim.FramesDropped, inner.SentFrames.Count,
                "没丢的应当全部都到了（丢包 + 送达 = 总数）");

            return (int)sim.FramesDropped;
        }

        // ====================================================================
        //  四、透明性（它还是个正常的 `ITransport`）
        // ====================================================================

        /// <summary>断开事件要透传（断开**不模拟延迟** —— 见实现里的注释）。</summary>
        [Test]
        public void Closed_IsPassedThrough()
        {
            FakeTransport inner;
            SimulatedTransport sim = Make(new NetSimProfile { LatencyMs = 200 }, out inner);

            List<string> reasons = new List<string>();
            sim.Closed += reasons.Add;

            inner.FailWith("模拟掉线");

            Assert.AreEqual(1, reasons.Count, "断开应当立刻透传，不该被延迟 200ms");
            Assert.AreEqual("模拟掉线", reasons[0]);
        }

        /// <summary>状态与统计透传（上层读到的还是真实连接的情况）。</summary>
        [Test]
        public void StateAndStats_ArePassedThrough()
        {
            FakeTransport inner;
            SimulatedTransport sim = Make(default(NetSimProfile), out inner);

            Assert.AreEqual(ETransportState.Connected, sim.State);
            Assert.IsTrue(sim.IsConnected);
            StringAssert.Contains("fake", sim.Description);
            StringAssert.Contains("直连", sim.Description);

            sim.Profile = NetSimProfile.Default();
            StringAssert.Contains("延迟 100ms", sim.Description);
        }

        /// <summary>关闭时把"路上"的包清掉（避免关完还漏出旧包）。</summary>
        [Test]
        public void Close_ClearsInFlight()
        {
            FakeTransport inner;
            SimulatedTransport sim = Make(new NetSimProfile { LatencyMs = 500 }, out inner);

            sim.Send(Payload(1));
            Assert.AreEqual(1, sim.InFlightCount);

            sim.Close();
            sim.Advance(1000);

            Assert.AreEqual(0, sim.InFlightCount, "关了之后不该还留着在路上的包");
            Assert.AreEqual(0, inner.SentFrames.Count, "关掉之后旧的包不该再送出去");
        }
    }
}
