// ============================================================================
//  M3-S2 · 分帧器测试（**纯逻辑，EditMode 里全都能测**）
//  对应验收：Docs\25-M3开工清单.md S2
//  被测：`Client\Assets\_Project\Shared\Net\FrameCodec.cs`（**双端共享**的那一份）
//
//  ---------------------------------------------------------------------------
//  这一组为什么值得写这么多条
//  ---------------------------------------------------------------------------
//  TCP 是**字节流**，粘包/拆包是它的**常态而不是意外**。
//  而分帧一旦写错，症状非常难查：不是崩，而是"偶尔卡住""偶尔读到垃圾"
//  —— 因为**单端测试全绿**、两端各写一份也各自全绿，只有联机才暴露。
//  所以这里把"喂字节的各种切法"都用用例钉住。
//
//  ⚠️ 特别值得说的三条（各自对应一个真会踩的坑）：
//    · **0 长度帧必须合法**：protobuf 空消息就是 0 字节（例如"还没设 payload 的 ClientMessage"）。
//      把它当非法 = 静默丢消息。
//    · **超过上限要判违规**：长度前缀可以撒谎，不设上限就是"一句 N 让本端吃光内存"。
//    · **长度是小端且手写**：不能用 `BitConverter`（它取决于宿主字节序）。
// ============================================================================

using System.Collections.Generic;
using NBC.Shared.Net;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>M3-S2：分帧器的测试。</summary>
    public sealed class FrameCodecTests
    {
        /// <summary>造一段可辨认的载荷（每个字节不同，便于断言"没串位"）。</summary>
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

        // ====================================================================
        //  一、往返与长度前缀
        // ====================================================================

        /// <summary>包一帧再拆回来，载荷一个字节不差。</summary>
        [Test]
        public void EncodeThenDecode_RoundTrips()
        {
            byte[] payload = Payload(32);
            byte[] frame = FrameCodec.Encode(payload);

            Assert.AreEqual(NetContract.FrameLengthPrefixBytes + 32, frame.Length, "帧 = 4 + 载荷");
            Assert.AreEqual(32, FrameCodec.ReadLength(frame, 0), "长度前缀应当是载荷长度");

            FrameDecoder decoder = new FrameDecoder();
            decoder.Append(frame, 0, frame.Length);

            byte[] received;
            string error;
            Assert.IsTrue(decoder.TryDequeue(out received, out error), error);
            CollectionAssert.AreEqual(payload, received);
            Assert.AreEqual(0, decoder.BufferedBytes, "取完应当没有残留");
            Assert.IsNull(decoder.FatalReason);
        }

        /// <summary>长度前缀必须是**小端**，而且不依赖宿主字节序（手写的那四行）。</summary>
        [Test]
        public void LengthPrefix_IsLittleEndian()
        {
            byte[] frame = FrameCodec.Encode(Payload(0x0102));

            // 0x0102 = 258 -> 小端应当是 02 01 00 00
            Assert.AreEqual(0x02, frame[0]);
            Assert.AreEqual(0x01, frame[1]);
            Assert.AreEqual(0x00, frame[2]);
            Assert.AreEqual(0x00, frame[3]);
            Assert.AreEqual(0x0102, FrameCodec.ReadLength(frame, 0));
        }

        /// <summary>**0 长度帧是合法的**（protobuf 空消息序列化出来就是 0 字节）。</summary>
        [Test]
        public void ZeroLengthFrame_IsLegal()
        {
            byte[] frame = FrameCodec.Encode(null);
            Assert.AreEqual(NetContract.FrameLengthPrefixBytes, frame.Length);

            FrameDecoder decoder = new FrameDecoder();
            decoder.Append(frame, 0, frame.Length);

            byte[] received;
            string error;
            Assert.IsTrue(decoder.TryDequeue(out received, out error), "0 长度帧也应当交出来");
            Assert.IsNotNull(received, "载荷应当是**空数组**而不是 null");
            Assert.AreEqual(0, received.Length);
        }

        // ====================================================================
        //  二、粘包 / 拆包（TCP 的常态）
        // ====================================================================

        /// <summary>粘包：两帧一次喂进来，要能依次取出两帧。</summary>
        [Test]
        public void StickyPackets_YieldFramesInOrder()
        {
            byte[] first = Payload(16, 1);
            byte[] second = Payload(8, 100);

            List<byte> stream = new List<byte>();
            stream.AddRange(FrameCodec.Encode(first));
            stream.AddRange(FrameCodec.Encode(second));

            FrameDecoder decoder = new FrameDecoder();
            decoder.Append(stream.ToArray(), 0, stream.Count);

            byte[] frame;
            Assert.IsTrue(decoder.TryDequeue(out frame), "第一帧");
            CollectionAssert.AreEqual(first, frame);

            Assert.IsTrue(decoder.TryDequeue(out frame), "第二帧");
            CollectionAssert.AreEqual(second, frame);

            Assert.IsFalse(decoder.TryDequeue(out frame), "没有第三帧了");
        }

        /// <summary>拆包：一帧分三次喂，前两次"数据不够"，第三次才出来。</summary>
        [Test]
        public void SplitPacket_WaitsUntilComplete()
        {
            byte[] payload = Payload(10);
            byte[] frame = FrameCodec.Encode(payload);

            FrameDecoder decoder = new FrameDecoder();
            byte[] received;

            decoder.Append(frame, 0, 2);
            Assert.IsFalse(decoder.TryDequeue(out received), "连长度都没凑齐");

            decoder.Append(frame, 2, 4);
            Assert.IsFalse(decoder.TryDequeue(out received), "长度够了但载荷不够");
            Assert.AreEqual(6, decoder.BufferedBytes, "缓冲里应当是 6 字节");

            decoder.Append(frame, 6, frame.Length - 6);
            Assert.IsTrue(decoder.TryDequeue(out received), "这下够了");
            CollectionAssert.AreEqual(payload, received);
        }

        /// <summary>极端切法：**一个字节一个字节**喂（真实网络最常见的就是这种碎法）。</summary>
        [Test]
        public void ByteByByteFeed_StillWorks()
        {
            byte[] payload = Payload(64);
            byte[] frame = FrameCodec.Encode(payload);

            FrameDecoder decoder = new FrameDecoder();
            byte[] received = null;

            for (int i = 0; i < frame.Length; i++)
            {
                decoder.Append(frame, i, 1);

                // 最后一字节之前都不该出帧（4 字节长度 + 64 字节载荷）
                if (i < frame.Length - 1)
                {
                    Assert.IsFalse(decoder.TryDequeue(out received), "第 " + (i + 1) + " 字节时不该出帧");
                }
            }

            Assert.IsTrue(decoder.TryDequeue(out received));
            CollectionAssert.AreEqual(payload, received);
        }

        /// <summary>交错切分：半帧 + 一整帧 + 半帧 —— 边界最容易错的地方。</summary>
        [Test]
        public void InterleavedSplits_KeepBoundaries()
        {
            byte[] a = Payload(5, 10);
            byte[] b = Payload(7, 20);
            byte[] c = Payload(3, 30);

            List<byte> stream = new List<byte>();
            stream.AddRange(FrameCodec.Encode(a));
            stream.AddRange(FrameCodec.Encode(b));
            stream.AddRange(FrameCodec.Encode(c));
            byte[] all = stream.ToArray();

            FrameDecoder decoder = new FrameDecoder();
            byte[] frame;

            // 先喂 7 字节（a 的 4+5=9 字节里的前 7 -> 不够）
            decoder.Append(all, 0, 7);
            Assert.IsFalse(decoder.TryDequeue(out frame), "a 还差 2 字节");

            // 再喂到 b 结束（9 + 4 + 7 = 20）
            decoder.Append(all, 7, 13);
            Assert.IsTrue(decoder.TryDequeue(out frame));
            CollectionAssert.AreEqual(a, frame);
            Assert.IsTrue(decoder.TryDequeue(out frame), "b 也完整了");
            CollectionAssert.AreEqual(b, frame);
            Assert.IsFalse(decoder.TryDequeue(out frame), "c 还没开始喂");

            decoder.Append(all, 20, all.Length - 20);
            Assert.IsTrue(decoder.TryDequeue(out frame));
            CollectionAssert.AreEqual(c, frame);
        }

        // ====================================================================
        //  三、协议违规（**必须响亮**，不能 try 恢复）
        // ====================================================================

        /// <summary>超过上限：判违规 + 给出人话原因 + 不崩。</summary>
        [Test]
        public void OversizedFrame_BecomesFatalWithReason()
        {
            FrameDecoder decoder = new FrameDecoder(64);

            // 撒谎的长度前缀：说还有 100000 字节
            byte[] bad = new byte[NetContract.FrameLengthPrefixBytes];
            bad[0] = 0xA0;
            bad[1] = 0x86;
            bad[2] = 0x01;   // 0x0186A0 = 100000
            bad[3] = 0x00;

            decoder.Append(bad, 0, bad.Length);

            byte[] frame;
            string error;
            Assert.IsFalse(decoder.TryDequeue(out frame, out error), "超限不该交出帧");
            Assert.IsNotNull(error, "必须有原因");
            StringAssert.Contains("超过上限", error);
            Assert.IsNotNull(decoder.FatalReason, "FatalReason 也要被设上，上层据此断开");

            // 违规之后再喂也不该改变状态（上层本该断开；这里只保证不崩、不误出帧）
            decoder.Append(new byte[16], 0, 16);
            Assert.IsFalse(decoder.TryDequeue(out frame), "违规后不该再出帧");
        }

        /// <summary>正好等于上限：**合法**（边界不能多算一个字节）。</summary>
        [Test]
        public void FrameExactlyAtLimit_IsAccepted()
        {
            FrameDecoder decoder = new FrameDecoder(16);
            byte[] payload = Payload(16);

            byte[] frame = FrameCodec.Encode(payload);
            decoder.Append(frame, 0, frame.Length);

            byte[] received;
            Assert.IsTrue(decoder.TryDequeue(out received), "16 字节正好是上限，应当合法");
            CollectionAssert.AreEqual(payload, received);
        }

        /// <summary>长度最高位为 1（读成负数）：判违规而不是"等它攒 20 亿字节"。</summary>
        [Test]
        public void NegativeLength_BecomesFatal()
        {
            FrameDecoder decoder = new FrameDecoder();

            byte[] bad = { 0xFF, 0xFF, 0xFF, 0xFF };
            decoder.Append(bad, 0, bad.Length);

            byte[] frame;
            string error;
            Assert.IsFalse(decoder.TryDequeue(out frame, out error));
            Assert.IsNotNull(error);
            StringAssert.Contains("负数", error);
        }

        // ====================================================================
        //  四、缓冲与复位
        // ====================================================================

        /// <summary>大帧（100 KB）能过 —— 缓冲增长 + 压缩这条路要能用。</summary>
        [Test]
        public void LargeFrame_GrowsAndCompacts()
        {
            FrameDecoder decoder = new FrameDecoder(256 * 1024);
            byte[] payload = Payload(100 * 1024, 7);

            byte[] frame = FrameCodec.Encode(payload);
            decoder.Append(frame, 0, frame.Length);

            byte[] received;
            Assert.IsTrue(decoder.TryDequeue(out received));
            CollectionAssert.AreEqual(payload, received);
            Assert.AreEqual(0, decoder.BufferedBytes, "取完应当压缩到 0");

            // 再来一帧，确认压缩之后还能正常工作
            decoder.Append(frame, 0, frame.Length);
            Assert.IsTrue(decoder.TryDequeue(out received));
            CollectionAssert.AreEqual(payload, received);
        }

        /// <summary>`Reset` 清空缓冲与违规状态（重连时用）。</summary>
        [Test]
        public void Reset_ClearsBufferAndError()
        {
            FrameDecoder decoder = new FrameDecoder(8);

            decoder.Append(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0, 4);
            byte[] frame;
            Assert.IsFalse(decoder.TryDequeue(out frame));
            Assert.IsNotNull(decoder.FatalReason);

            decoder.Reset();

            Assert.IsNull(decoder.FatalReason);
            Assert.AreEqual(0, decoder.BufferedBytes);

            // 复位之后能正常收帧
            byte[] good = FrameCodec.Encode(Payload(4));
            decoder.Append(good, 0, good.Length);
            Assert.IsTrue(decoder.TryDequeue(out frame));
        }

        /// <summary>上限必须为正（否则"防吃内存"那条就是摆设）。</summary>
        [Test]
        public void Constructor_RejectsNonPositiveLimit()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new FrameDecoder(0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new FrameDecoder(-1));
        }

        /// <summary>`Encode` 的越界参数要当场报错（而不是悄悄少拷一段）。</summary>
        [Test]
        public void Encode_RejectsOutOfRange()
        {
            byte[] payload = Payload(4);

            Assert.Throws<System.ArgumentOutOfRangeException>(() => FrameCodec.Encode(payload, 2, 10));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => FrameCodec.Encode(payload, -1, 2));
            Assert.Throws<System.ArgumentNullException>(() => FrameCodec.Encode(null, 0, 4));
        }
    }
}
