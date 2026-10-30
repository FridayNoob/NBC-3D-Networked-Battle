// 这个文件里有：
//   FrameCodec（静态类）：把载荷包成「4 字节小端长度 + 载荷」的帧，并提供读长度前缀的入口（写长度只给内部成帧用）。
//   FrameDecoder（类）：增量式拆帧器，把陆续收到的字节攒起来，够一帧就交出一帧。

// 注意：分帧必须两端算出同一个结果 —— 长度字节序、单帧上限、0 长度帧的处理只要有一处不同，联机就会偶尔卡住或读到垃圾，
// 而且单端测试全绿。所以客户端与服务端编的是同一份源码；本层只依赖 BCL，不引 UnityEngine 与 NBC.Protocol。
// 注意：本目录与 Shared\Condition、Shared\Battle 同理 —— Unity 侧未开可空，服务端（Server\Directory.Build.props）开了可空引用；
// 这里显式关掉，让两端按同一套规则编译。
#nullable disable

using System;

namespace NBC.Shared.Net
{
    // 主要功能：做网络分帧 —— 给出「一条消息到哪儿结束」，并挡住负长度、谎报长度与 0 长度帧这三个坑。
    // 主要数据成员：无；长度前缀字节数与单帧上限都取自 NetContract。
    // 主要外部方法：Encode（byte[] 两版 + span 版）、ReadLength；WriteLength 是 internal，只给 Encode 用。
    // 谁在用它：客户端 TcpTransport 发消息、服务端 TcpServerTransport 发消息（span 版）；FrameDecoder 读长度前缀；FrameCodecTests、Server\_net-probe、Server\_condition-probe 也直接调。
    /// <summary>把载荷包成 4 字节小端长度前缀 + 载荷的帧，并读出长度前缀；客户端与服务端共用同一份。</summary>
    public static class FrameCodec
    {
        /// <summary>把一段载荷包成帧：4 字节小端长度前缀 + 载荷副本。</summary>
        /// <param name="payload">载荷；可为 null，此时 offset 与 count 必须都是 0（即一条 0 长度帧）。</param>
        /// <param name="offset">载荷里的起始字节下标（字节）。</param>
        /// <param name="count">取多少个字节；0 合法。</param>
        /// <returns>新分配的帧字节，长度 = 4 + count，可直接写进 socket。</returns>
        public static byte[] Encode(byte[] payload, int offset, int count)
        {
            // 功能：把 payload 从 offset 起的 count 个字节复制到新数组，前面补 4 字节小端长度。
            // 入参：payload 载荷（可为 null，此时 offset/count 必须为 0）；offset 起点（字节）；count 字节数（0 合法）。
            // 返回：新数组，前 4 字节是长度前缀，后面是载荷副本；总长 = FrameLengthPrefixBytes + count。
            // 步骤：
            //   1) null 时先校验 offset/count 是否为 0 —— 这是调用方 bug，早抛异常比悄悄发出错帧好
            //   2) 非 null 时校验 offset/count 不越界，同样早抛
            //   3) 一次分配 4 + count，先把长度写进前 4 字节
            //   4) count 大于 0 才复制载荷；0 长度帧的帧体就只有前缀
            // 注意：长度前缀必须手写小端，不能改用 BitConverter —— 它跟随宿主字节序，换台大端机器两端就会把长度读成天文数字。
            // 注意：0 长度帧是合法的（protobuf 空消息序列化出来就是 0 字节），不要把它当非法丢掉。
            // 上游：客户端 TcpTransport 发消息、服务端探针
            // 下游：FrameCodec.WriteLength、Buffer.BlockCopy（count 为 0 时跳过）
            if (payload == null)
            {
                if (offset != 0 || count != 0)
                {
                    throw new ArgumentNullException(nameof(payload),
                        "[FrameCodec] 载荷是 null，但 offset/count 不是 0（" + offset + "/" + count + "）。");
                }
            }
            else if (offset < 0 || count < 0 || offset + count > payload.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count),
                    "[FrameCodec] offset/count 越界：offset=" + offset + "，count=" + count +
                    "，载荷长度=" + payload.Length + "。");
            }

            byte[] frame = new byte[NetContract.FrameLengthPrefixBytes + count];
            WriteLength(frame, 0, count);

            if (count > 0)
            {
                Buffer.BlockCopy(payload, offset, frame, NetContract.FrameLengthPrefixBytes, count);
            }

            return frame;
        }

        /// <summary>把整段载荷包成帧（等价于 offset = 0、count = payload.Length）。</summary>
        /// <param name="payload">载荷；可为 null，按 0 长度帧处理。</param>
        /// <returns>新分配的帧字节。</returns>
        public static byte[] Encode(byte[] payload)
        {
            // 功能：整段成帧；null 按 0 长度帧处理。
            return Encode(payload, 0, payload == null ? 0 : payload.Length);
        }

        /// <summary>span 版成帧：直接按 span 长度生成帧，省掉调用方为发送先 ToArray 的一次复制。</summary>
        /// <param name="payload">载荷；空 span 即一条 0 长度帧。</param>
        /// <returns>新分配的帧字节，长度 = 4 + payload.Length。</returns>
        public static byte[] Encode(ReadOnlySpan<byte> payload)
        {
            // 功能：按 span 长度写前缀并复制载荷，服务端拿到 ReadOnlySpan 时不必先 ToArray 多复制一次。
            // 入参：payload 载荷（空 span 合法，即一条 0 长度帧）。
            // 返回：新数组，前 4 字节长度前缀，后面是载荷副本；总长 = FrameLengthPrefixBytes + payload.Length。
            // 步骤：
            //   1) 按 4 + payload.Length 一次分配，省掉调用方先 ToArray 的那次复制
            //   2) 先写长度前缀
            //   3) 非空才 CopyTo，空 span 只剩前缀
            // 注意：结果必须与 byte[] 版逐字节一致（长度、字节序、0 长度帧都一样），Server\_net-probe 专门比对这一点。
            // 上游：服务端 TcpServerTransport.Send(long, ReadOnlySpan<byte>)、Server\_net-probe 与 byte[] 版的一致性校验
            // 下游：FrameCodec.WriteLength、ReadOnlySpan.CopyTo
            byte[] frame = new byte[NetContract.FrameLengthPrefixBytes + payload.Length];
            WriteLength(frame, 0, payload.Length);

            if (payload.Length > 0)
            {
                payload.CopyTo(frame.AsSpan(NetContract.FrameLengthPrefixBytes));
            }

            return frame;
        }

        /// <summary>从 buffer[offset] 起读 4 字节小端长度前缀，给拆帧与排错用。</summary>
        /// <param name="buffer">字节缓冲。</param>
        /// <param name="offset">长度前缀的起始下标（字节）。</param>
        /// <returns>按小端拼出的 int；最高位为 1 时为负数，拆帧器据此判协议违规。</returns>
        public static int ReadLength(byte[] buffer, int offset)
        {
            // 功能：把 buffer[offset..offset + 4) 按小端拼成一个 int。
            // 入参：buffer 源字节（不可为 null）；offset 长度前缀的起点（字节）。
            // 返回：拼出的长度；最高位为 1 时是负数 —— 说明对端不是本协议或数据已损坏，不是合法长度。
            // 步骤：
            //   1) 先挡 null 与 offset 越界（offset + 4 超过缓冲长度就抛），别让下面的下标访问越界
            //   2) 逐字节左移 8 / 16 / 24 位后按位或起来，全程不依赖宿主字节序
            // 注意：必须手写小端，不能改用 BitConverter —— 它跟随宿主字节序，换台大端机器就会把长度读成天文数字（与定点数不依赖浮点同一个理由）。
            // 上游：FrameDecoder.TryDequeue 取帧长；FrameCodecTests、Server\_condition-probe 排错时直接读
            // 下游：无
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || offset + NetContract.FrameLengthPrefixBytes > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset),
                    "[FrameCodec] 读长度前缀越界（offset=" + offset + "，长度=" + buffer.Length + "）。");
            }

            return buffer[offset]
                 | (buffer[offset + 1] << 8)
                 | (buffer[offset + 2] << 16)
                 | (buffer[offset + 3] << 24);
        }

        // 功能：把 length 的低 4 字节按小端写进 buffer[offset..offset + 4)，不做任何校验。
        // 入参：buffer 目标字节；offset 写入起点（字节）；length 要写的长度（必须 ≥ 0）。
        // 返回：无。
        // 步骤：
        //   1) 逐字节取 length 的 0 / 8 / 16 / 24 位并 & 0xFF，高位自然截断
        // 注意：本方法不检查缓冲长度与 length 正负，调用方必须先保证 offset + 4 不越界、length ≥ 0 —— 负数会被写成 0xFFFFFFFF 量级的大数。
        // 上游：FrameCodec.Encode 的 byte[] 两版与 span 版
        // 下游：无
        internal static void WriteLength(byte[] buffer, int offset, int length)
        {
            buffer[offset] = (byte)(length & 0xFF);
            buffer[offset + 1] = (byte)((length >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((length >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((length >> 24) & 0xFF);
        }
    }

    // 主要功能：缓存半帧、按 4 字节小端长度前缀切出完整帧，并把负长度与超上限判成不可恢复的协议违规。
    // 主要数据成员：m_maxFrameBytes（字节，单帧上限）、m_buffer（接收缓冲，字节数组，初始 1024）、m_start / m_end（有效区间下标，字节）、m_fatalReason（违规原因，null = 正常）。
    // 主要外部方法：Append / TryDequeue（两个重载）/ Reset / BufferedBytes / FatalReason。
    // 谁在用它：客户端 TcpTransport 与 TcpServerTransport 各为每条连接持有一个；FrameCodecTests 与 Server\_net-probe、Server\_condition-probe 直接驱动。
    /// <summary>增量式拆帧器：把陆续收到的字节喂进来，够一帧就交出一帧；不是线程安全的。</summary>
    public sealed class FrameDecoder
    {
        // 单帧载荷上限，字节；必须 > 0，默认 NetContract.MaxFrameBytes（524288，即 512 KB），超过即判协议违规。
        private readonly int m_maxFrameBytes;

        // 接收缓冲，字节；初始 1024，装不下时翻倍增长，消费后压缩，不会随连接时长无限涨。
        private byte[] m_buffer;

        // 已消费数据的起点，字节偏移；0..m_end。
        private int m_start;

        // 有效数据的终点（不含），字节偏移；m_start..m_buffer.Length。
        private int m_end;

        // 协议违规原因；null = 正常，非 null = 必须断开，别再喂数据。
        private string m_fatalReason;

        /// <summary>造一个拆帧器：记下单帧上限，并分配 1024 字节初始缓冲。</summary>
        /// <param name="maxFrameBytes">单帧载荷上限（字节）；必须 > 0，默认 NetContract.MaxFrameBytes（524288，即 512 KB）。</param>
        public FrameDecoder(int maxFrameBytes = NetContract.MaxFrameBytes)
        {
            // 功能：记下单帧上限并分配 1024 字节初始缓冲，之后 Append 只管往里灌。
            // 入参：maxFrameBytes 单帧载荷上限（字节，必须 > 0）。
            // 返回：无。
            // 步骤：
            //   1) 上限小于等于 0 直接抛 —— 否则任何帧都会被判超限，或上限判断形同虚设
            //   2) 记下上限，供 TryDequeue 比对
            //   3) 初始只给 1024 字节，靠 EnsureCapacity 按需增长，不按上限预分配
            // 注意：上限是防「谎报长度吃内存」的防御值，不是协议协商值；收发两端应当用同一个上限，否则一侧发得出、另一侧直接断开。
            // 上游：客户端 TcpTransport、服务端 TcpServerTransport（每条连接一个）、FrameCodecTests 与探针
            // 下游：无
            if (maxFrameBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFrameBytes),
                    "[FrameDecoder] 单帧上限必须 &gt; 0（当前 " + maxFrameBytes + "）。");
            }

            m_maxFrameBytes = maxFrameBytes;
            m_buffer = new byte[1024];
        }

        /// <summary>缓冲里还没被消费的字节数（字节）。</summary>
        public int BufferedBytes
        {
            get { return m_end - m_start; }
        }

        /// <summary>协议违规原因；null = 正常，非 null 时上层应当断开连接。</summary>
        public string FatalReason
        {
            get { return m_fatalReason; }
        }

        /// <summary>把刚收到的字节追加到缓冲尾部；半帧、整帧、多帧都可以一次喂进来。</summary>
        /// <param name="buffer">源字节。</param>
        /// <param name="offset">源字节里的起点（字节）。</param>
        /// <param name="count">字节数；0 合法（本次没读到数据）。</param>
        public void Append(byte[] buffer, int offset, int count)
        {
            // 功能：把 buffer[offset..offset + count) 复制到缓冲尾部，等 TryDequeue 拆帧。
            // 入参：buffer 源字节（不可为 null）；offset 起点（字节）；count 字节数（0 合法，直接返回）。
            // 返回：无。
            // 步骤：
            //   1) 已经 fatal 就直接返回 —— 上层本该断开；不收数据也不改状态，避免在坏流上继续攒内存
            //   2) 挡 null 与 offset/count 越界，调用方 bug 早抛
            //   3) count 为 0 直接返回，省掉一次扩容判断
            //   4) EnsureCapacity 保证尾部还有 count 字节空间
            //   5) BlockCopy 到 m_end，再把 m_end 前移
            // 注意：fatal 后再喂数据是静默忽略，不抛异常；断开与否由上层读 FatalReason 决定。
            // 上游：客户端 TcpTransport.Pump（socket 收包回调里）、服务端 TcpServerTransport 收包路径、测试与探针
            // 下游：FrameDecoder.EnsureCapacity、Buffer.BlockCopy
            if (m_fatalReason != null)
            {
                return;
            }

            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count),
                    "[FrameDecoder] offset/count 越界：offset=" + offset + "，count=" + count +
                    "，长度=" + buffer.Length + "。");
            }

            if (count == 0)
            {
                return;
            }

            EnsureCapacity(count);
            Buffer.BlockCopy(buffer, offset, m_buffer, m_end, count);
            m_end += count;
        }

        /// <summary>试着从缓冲头部取出一条完整帧。</summary>
        /// <param name="frame">取到的载荷；0 长度帧给空数组，不是 null；取不到时为 null。</param>
        /// <param name="error">协议违规原因；数据不够时为 null，违规时与 FatalReason 相同。</param>
        /// <returns>true = 取到一帧；false 且 error 为 null = 数据还不够，继续喂；false 且 error 非 null = 协议违规，必须断开。</returns>
        public bool TryDequeue(out byte[] frame, out string error)
        {
            // 功能：按「4 字节长度 + 载荷」从缓冲头部切出一帧，同时区分「数据不够」与「协议违规」两种失败。
            // 入参：无（frame 与 error 是输出参数，见返回）。
            // 返回：true 取到一帧；false + error 为 null 是数据不够，继续 Append；false + error 非 null 是协议违规，必须断开。
            // 步骤：
            //   1) 先回填 frame = null、error = m_fatalReason：已 fatal 就直接失败，不再往下解析
            //   2) 连 4 字节长度前缀都没凑齐 → 数据不够，返回 false（error 保持 null）
            //   3) 读长度；负数说明最高位为 1（对端不是本协议或数据损坏），记 fatal 并返回失败
            //   4) 长度超过 m_maxFrameBytes → 记 fatal 并返回失败，不按它分配内存
            //   5) 前缀 + 载荷还没攒齐 → 数据不够，返回 false，缓冲不动
            //   6) 跳过 4 字节前缀，按 length 新分配数组并复制载荷；0 长度也给空数组
            //   7) m_start 前移到帧尾，Compact 腾出已消费空间，返回 true
            // 注意：长度前缀可以撒谎，超上限时必须断开；若继续按它攒内存，一句长度就能把本端内存吃光。
            // 注意：0 长度帧必须照常交出（protobuf 空消息就是 0 字节），丢它等于丢空心跳这类消息。
            // 注意：fatal 一旦置上就不会自愈，之后每次调用都返回 false + 同一 error，只能靠 Reset 清。
            // 注意：每帧都 new byte[length]，不做零分配；30Hz、2~4 人的量级够用，真要零分配再另加借出 / 归还的 API。
            // 上游：客户端 TcpTransport.Pump 的拆帧循环、服务端 TcpServerTransport 收包路径、测试与探针
            // 下游：FrameCodec.ReadLength、FrameDecoder.Fail、FrameDecoder.Compact、Buffer.BlockCopy
            frame = null;
            error = m_fatalReason;

            if (error != null)
            {
                return false;
            }

            if (BufferedBytes < NetContract.FrameLengthPrefixBytes)
            {
                return false;
            }

            int length = FrameCodec.ReadLength(m_buffer, m_start);

            if (length < 0)
            {
                Fail("长度前缀是负数（" + length + "）：对端可能不是本协议，或数据已损坏。");
                error = m_fatalReason;
                return false;
            }

            if (length > m_maxFrameBytes)
            {
                Fail("单帧 " + length + " 字节，超过上限 " + m_maxFrameBytes +
                     "（可能对端不是本协议，或长度前缀被破坏）。");
                error = m_fatalReason;
                return false;
            }

            if (BufferedBytes < NetContract.FrameLengthPrefixBytes + length)
            {
                return false;
            }

            m_start += NetContract.FrameLengthPrefixBytes;

            frame = new byte[length];

            if (length > 0)
            {
                Buffer.BlockCopy(m_buffer, m_start, frame, 0, length);
            }

            m_start += length;
            Compact();
            return true;
        }

        /// <summary>不需要错误信息时的简写：取不到就返回 false，再用 FatalReason 区分数据不够还是协议违规。</summary>
        /// <param name="frame">取到的载荷；0 长度帧给空数组。</param>
        /// <returns>true = 取到一帧；false = 数据不够或协议违规。</returns>
        public bool TryDequeue(out byte[] frame)
        {
            // 功能：转调带 error 的重载，错误原因留给 FatalReason 区分数据不够还是协议违规。
            string error;
            return TryDequeue(out frame, out error);
        }

        /// <summary>清空缓冲与 fatal 状态；重连时复用同一个拆帧器。</summary>
        public void Reset()
        {
            // 功能：把有效区间归零并清掉 fatal，让下一次连接从干净状态开始。
            // 入参：无。
            // 返回：无。
            // 步骤：
            //   1) m_start 与 m_end 归零 —— 丢弃残留字节，但不释放缓冲（下一次还能用）
            //   2) 清空 m_fatalReason，解除拒收状态
            // 注意：不 Reset 就重连，上一局残留的半帧会被当成新连接的开头，表现为第一帧就是垃圾。
            // 上游：客户端 TcpTransport.Connect（重连前）、FrameCodecTests
            // 下游：无
            m_start = 0;
            m_end = 0;
            m_fatalReason = null;
        }

        // 功能：记下协议违规原因；之后 Append 拒收、TryDequeue 持续返回失败。
        // 入参：reason 给人看的原因（会经 FatalReason 直接交给上层，写清是哪种违规）。
        // 返回：无。
        // 步骤：
        //   1) 只写 m_fatalReason，不抛异常也不主动断连接 —— 传输层才知道怎么关，交给它按 FatalReason 处理
        // 注意：一旦置上就不会自愈，只能 Reset。
        // 上游：FrameDecoder.TryDequeue 的负长度与超上限两处分支
        // 下游：无（Append 与 TryDequeue 读 m_fatalReason）
        private void Fail(string reason)
        {
            m_fatalReason = reason;
        }

        // 功能：保证缓冲尾部还能再写 count 字节；不够先压缩，仍不够再翻倍扩容。
        // 入参：count 本次要追加的字节数（> 0，Append 已挡掉 0）。
        // 返回：无。
        // 步骤：
        //   1) 现有空间够就返回，不做任何拷贝
        //   2) 先 Compact 把已消费的前缀挪掉 —— 多数情况这一下就够了，省一次分配
        //   3) 仍不够就按 2 倍增长到能装下 needed，再整体拷贝旧数据
        // 注意：扩容按 2 倍摊还，避免每来一小段就复制整缓冲；增长到能装下 needed 的最小 2 的幂为止，不按单帧上限预分配。
        // 上游：FrameDecoder.Append
        // 下游：FrameDecoder.Compact、Buffer.BlockCopy
        private void EnsureCapacity(int count)
        {
            if (m_end + count <= m_buffer.Length)
            {
                return;
            }

            Compact();

            if (m_end + count <= m_buffer.Length)
            {
                return;
            }

            int needed = m_end + count;
            int size = m_buffer.Length;

            while (size < needed)
            {
                size *= 2;
            }

            byte[] grown = new byte[size];
            Buffer.BlockCopy(m_buffer, 0, grown, 0, m_end);
            m_buffer = grown;
        }

        // 功能：把 [m_start, m_end) 的有效数据挪到缓冲开头，丢掉已消费的前缀。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) m_start 为 0 说明没有已消费前缀，直接返回
        //   2) 还有有效数据才 BlockCopy 到下标 0
        //   3) m_start 归零、m_end 改成剩余长度
        // 注意：源区间与目标区间重叠，靠 BlockCopy 的 memmove 语义；别改成正向逐字节拷贝，会把数据自我覆盖。
        // 上游：FrameDecoder.TryDequeue（每取出一帧）、FrameDecoder.EnsureCapacity（扩容前）
        // 下游：Buffer.BlockCopy
        private void Compact()
        {
            if (m_start == 0)
            {
                return;
            }

            int remaining = m_end - m_start;

            if (remaining > 0)
            {
                Buffer.BlockCopy(m_buffer, m_start, m_buffer, 0, remaining);
            }

            m_start = 0;
            m_end = remaining;
        }
    }
}
