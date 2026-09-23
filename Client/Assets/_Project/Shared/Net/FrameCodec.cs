// ============================================================================
//  FrameCodec —— TCP 的「分帧」：长度前缀 + 载荷（**双端共享**）
//  项目：3D联网战斗Demo   对应：M3-S2、Docs\25 §三 D1
//
//  ---------------------------------------------------------------------------
//  一、为什么需要"分帧"（说人话）
//  ---------------------------------------------------------------------------
//  TCP 是**字节流**，不是"消息流"。你 `Send` 三次，对面可能：
//      · 收到一次（**粘包**：三次的数据连在一起）
//      · 收到三次但边界和你发的不一样（**拆包**：一条消息被切成两半先后到）
//  所以"一条消息到哪儿结束"**必须自己带信息**。最省的做法是**长度前缀**：
//
//      帧 = [4 字节小端长度 N][N 字节载荷]
//
//  于是接收侧只要"读到 4 字节 → 知道还差多少 → 攒够就交出一条完整帧"。
//
//  ---------------------------------------------------------------------------
//  二、为什么它住在**共享层**（`Shared\`）
//  ---------------------------------------------------------------------------
//  和条件系统、伤害结算是同一条判据：**两端必须算出同一个结果**。
//  分帧是"两边各写一份就一定会对不上"的典型 —— 只要长度字节序、上限、
//  对 0 长度帧的处理有一点不同，联机就会表现为"偶尔卡住/偶尔读到垃圾"，
//  而且**单端测试全绿**。
//  所以服务端（`NBC.Server.Core`）与客户端用**同一份** `FrameCodec`。
//
//  ---------------------------------------------------------------------------
//  三、三条写死的约定（每一条都对应一个"踩过就难忘"的坑）
//  ---------------------------------------------------------------------------
//  ① **长度是 4 字节小端，手写字节而不是 `BitConverter`**
//     `BitConverter` 的结果**取决于宿主字节序**：本机 x86 是小端，
//     但如果哪天跑在大端平台上，两端就会互相读成天文数字。
//     分帧是"两端必须一致"的东西，**不能依赖宿主环境**（和 D2 定点数是同一个理由）。
//
//  ② **0 长度帧是合法的**
//     protobuf 的空消息序列化出来就是 **0 字节**（例如"还没设 payload 的 ClientMessage"）。
//     如果这里把 0 当非法，那么"空心跳"这类消息就会被静默丢掉 —— 又一个静默失败。
//
//  ③ **超过上限 = 协议违规，必须断开**
//     长度前缀可以撒谎（或对端有 bug）。若不设上限，一句 `N` 就能让本端
//     `new byte[N]` 把内存吃光。所以超限时**不 try 恢复**，直接标记 fatal 让上层断开
//     （`FatalReason` 说清为什么，便于排查）。
//
//  ---------------------------------------------------------------------------
//  四、空间与分配（如实记）
//  ---------------------------------------------------------------------------
//  · 接收缓冲会**按需增长**，消费后**压缩**（把已消费的部分挪掉），不会无限涨
//  · `TryDequeue` 每次**新分配一个 byte[]** 交出载荷 —— M3（30Hz、2~4 人）这个量级完全够用；
//    真要做零分配，再加一个"借出缓冲 + 用完归还"的 API（M5 优化时再说）
// ============================================================================

// 与 Shared\Condition\ 同一处理由：本目录在 Unity（未开可空）与服务端（`Server\Directory.Build.props`
// 开了 `<Nullable>enable</Nullable>`）下规则不同。显式关掉，让**两端看到同一套规则**。
#nullable disable

using System;

namespace NBC.Shared.Net
{
    /// <summary>把载荷包成帧 / 从字节流里切出帧（**双端共享**）。</summary>
    public static class FrameCodec
    {
        /// <summary>把一段载荷包成帧（长度前缀 + 载荷）。</summary>
        /// <param name="payload">载荷（可以为 null / 空 —— 那就是一条 0 长度帧）。</param>
        /// <param name="offset">从载荷的第几个字节开始。</param>
        /// <param name="count">取多少字节。</param>
        /// <returns>可以直接写进 socket 的字节。</returns>
        public static byte[] Encode(byte[] payload, int offset, int count)
        {
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

        /// <summary>把一段载荷包成帧（整段）。</summary>
        /// <param name="payload">载荷（可以为 null）。</param>
        /// <returns>帧字节。</returns>
        public static byte[] Encode(byte[] payload)
        {
            return Encode(payload, 0, payload == null ? 0 : payload.Length);
        }

        /// <summary>读一处长度前缀（**小端**）。给测试与排错用。</summary>
        /// <param name="buffer">字节。</param>
        /// <param name="offset">起点。</param>
        /// <returns>长度。</returns>
        public static int ReadLength(byte[] buffer, int offset)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || offset + NetContract.FrameLengthPrefixBytes > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset),
                    "[FrameCodec] 读长度前缀越界（offset=" + offset + "，长度=" + buffer.Length + "）。");
            }

            // 手写小端：**不要用 BitConverter**（它取决于宿主字节序，见文件头约定①）
            return buffer[offset]
                 | (buffer[offset + 1] << 8)
                 | (buffer[offset + 2] << 16)
                 | (buffer[offset + 3] << 24);
        }

        /// <summary>写一处长度前缀（**小端**）。</summary>
        /// <param name="buffer">字节。</param>
        /// <param name="offset">起点。</param>
        /// <param name="length">长度（必须 ≥ 0）。</param>
        internal static void WriteLength(byte[] buffer, int offset, int length)
        {
            buffer[offset] = (byte)(length & 0xFF);
            buffer[offset + 1] = (byte)((length >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((length >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((length >> 24) & 0xFF);
        }
    }

    /// <summary>
    /// 增量式拆帧器：把"陆续收到的字节"喂进来，够一帧就交出一帧。
    /// <para>⚠️ **不是线程安全的**：只在网络泵（一个线程/一帧）里用。</para>
    /// </summary>
    public sealed class FrameDecoder
    {
        /// <summary>单帧载荷上限（超过就判定协议违规）。</summary>
        private readonly int m_maxFrameBytes;

        /// <summary>接收缓冲（按需增长，消费后压缩）。</summary>
        private byte[] m_buffer;

        /// <summary>有效数据的起点（已消费的部分在这里之前）。</summary>
        private int m_start;

        /// <summary>有效数据的终点。</summary>
        private int m_end;

        /// <summary>协议违规原因（非 null = 必须断开，别再喂数据）。</summary>
        private string m_fatalReason;

        /// <summary>造一个拆帧器。</summary>
        /// <param name="maxFrameBytes">单帧上限；不传则用 <see cref="NetContract.MaxFrameBytes"/>。</param>
        public FrameDecoder(int maxFrameBytes = NetContract.MaxFrameBytes)
        {
            if (maxFrameBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFrameBytes),
                    "[FrameDecoder] 单帧上限必须 &gt; 0（当前 " + maxFrameBytes + "）。");
            }

            m_maxFrameBytes = maxFrameBytes;
            m_buffer = new byte[1024];
        }

        /// <summary>缓冲里还有多少**没消费**的字节。</summary>
        public int BufferedBytes
        {
            get { return m_end - m_start; }
        }

        /// <summary>协议违规原因（null = 正常）。非 null 时上层应当断开连接。</summary>
        public string FatalReason
        {
            get { return m_fatalReason; }
        }

        /// <summary>喂入**刚收到**的字节（可以是半帧、也可以是好几帧）。</summary>
        /// <param name="buffer">收到的字节。</param>
        /// <param name="offset">起点。</param>
        /// <param name="count">多少字节。</param>
        public void Append(byte[] buffer, int offset, int count)
        {
            if (m_fatalReason != null)
            {
                // 已经协议违规了还继续喂：直接忽略（上层本该断开），但不静默改状态
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

        /// <summary>
        /// 试着取出一条完整帧。
        /// <para>
        /// 返回值与 <paramref name="error"/> 的组合是**契约**：
        /// · `true`  → 取到一帧（`frame` 非 null）
        /// · `false` + `error == null` → **数据还不够**，继续喂
        /// · `false` + `error != null` → **协议违规**，必须断开（`FatalReason` 也会被设上）
        /// </para>
        /// </summary>
        /// <param name="frame">取到的载荷（0 长度帧时是**空数组**，不是 null）。</param>
        /// <param name="error">违规原因；正常时为 null。</param>
        /// <returns>取到一帧返回 true。</returns>
        public bool TryDequeue(out byte[] frame, out string error)
        {
            frame = null;
            error = m_fatalReason;

            if (error != null)
            {
                return false;
            }

            if (BufferedBytes < NetContract.FrameLengthPrefixBytes)
            {
                return false;   // 连长度都没凑齐
            }

            int length = FrameCodec.ReadLength(m_buffer, m_start);

            if (length < 0)
            {
                // 长度是 uint32 读进来的，最高位为 1 会变成负数 —— 说明对端不是这套协议（或数据烂了）
                Fail("长度前缀是负数（" + length + "）：对端可能不是本协议，或数据已损坏。");
                error = m_fatalReason;
                return false;
            }

            if (length > m_maxFrameBytes)
            {
                // ⚠️ 这里**不 try 恢复**：长度可以撒谎，继续按它攒内存就是被远程吃光内存
                Fail("单帧 " + length + " 字节，超过上限 " + m_maxFrameBytes +
                     "（可能对端不是本协议，或长度前缀被破坏）。");
                error = m_fatalReason;
                return false;
            }

            if (BufferedBytes < NetContract.FrameLengthPrefixBytes + length)
            {
                return false;   // 载荷还没攒够
            }

            m_start += NetContract.FrameLengthPrefixBytes;

            // 0 长度帧也要交出去（protobuf 空消息就是 0 字节，见文件头约定②）
            frame = new byte[length];

            if (length > 0)
            {
                Buffer.BlockCopy(m_buffer, m_start, frame, 0, length);
            }

            m_start += length;
            Compact();
            return true;
        }

        /// <summary>取一条完整帧（不需要错误信息时的简写）。</summary>
        /// <param name="frame">载荷。</param>
        /// <returns>取到返回 true；数据不够或违规都返回 false（用 <see cref="FatalReason"/> 区分）。</returns>
        public bool TryDequeue(out byte[] frame)
        {
            string error;
            return TryDequeue(out frame, out error);
        }

        /// <summary>清空缓冲与错误状态（重连时用）。</summary>
        public void Reset()
        {
            m_start = 0;
            m_end = 0;
            m_fatalReason = null;
        }

        /// <summary>标记一次协议违规。</summary>
        /// <param name="reason">原因。</param>
        private void Fail(string reason)
        {
            m_fatalReason = reason;
        }

        /// <summary>保证还能再塞进 <paramref name="count"/> 个字节。</summary>
        /// <param name="count">要写入的字节数。</param>
        private void EnsureCapacity(int count)
        {
            if (m_end + count <= m_buffer.Length)
            {
                return;
            }

            // 先看压缩能不能腾出地方（已消费的部分挪掉）
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

        /// <summary>把已消费的前缀挪掉（避免缓冲只涨不降）。</summary>
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
