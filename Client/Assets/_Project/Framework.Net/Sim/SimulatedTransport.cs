// ============================================================================
//  SimulatedTransport —— 网络模拟器（`ITransport` 外面包一层，可注入延迟/抖动/丢包）
//  项目：3D联网战斗Demo   对应：需求 NET-07、Docs\25 §四 S8
//
//  ---------------------------------------------------------------------------
//  一、它是什么（一句话）
//  ---------------------------------------------------------------------------
//      **它也是一个 `ITransport`** —— 所以能插在**任何**实现外面：
//          真实：  NetSession → SimulatedTransport → TcpTransport → 网线
//          测试：  NetSession → SimulatedTransport → FakeTransport（纯内存）
//      上层一行都不用改（这就是当初留 `ITransport` 接缝的回报之一）。
//
//  ⚠️ 它是**故意的坏网络**：好网络测不出同步策略的问题。面试里问"你怎么验证抗抖动"，
//     答案就是"我能把延迟/抖动/丢包调出来，然后证明它仍然对得上"。
//
//  ---------------------------------------------------------------------------
//  二、为什么它多了一个 `Advance(deltaMs)`（`ITransport` 里没有时间）
//  ---------------------------------------------------------------------------
//  延迟必须有**时间轴**，而 `ITransport.Pump()` 是"没有时间的推一帧"（那样两端同源、可复现）。
//  所以这里**不污染接缝**，而是在实现上多加一个入口：
//
//      sim.Advance(deltaMs);   // ① 推进模拟器的时钟（延迟到期、丢包结算）
//      session.Pump(deltaMs);  // ② 会话推进：内部会调 sim.Pump()
//
//  ⚠️ 顺序不能反：先推进时钟再 Pump，否则"这一帧该到的包"要等下一帧。
//
//  ---------------------------------------------------------------------------
//  三、丢包在 M3 的真实含义（⚠️ 这条必须说清，否则会误用）
//  ---------------------------------------------------------------------------
//  M3 走的是 **TCP**：可靠性（重传）是 TCP 自己保证的，**应用层丢一条就是真丢了**。
//  所以：
//      · **延迟 / 抖动**：TCP 完全容忍 → 可以直接用来验"抗抖动"（探针里就这么用）
//      · **丢包**：在 TCP 上等于"**这条消息永远不到**" → 连握手都会失败（协议不允许缺消息）
//        ⇒ 它的正确用途是 **M4 的帧同步（UDP 式）** 与"客户端容错"的验证；
//          M3 的探针只用延迟/抖动，丢包留给 EditMode 用例单独验（证明模拟器真的在丢）
//
//  ---------------------------------------------------------------------------
//  四、随机数：自己写（同一个理由，但**故意各写一份**）
//  ---------------------------------------------------------------------------
//  用 xorshift（与 `Server\NBC.Server.Game\BattleRandom.cs` 同一套算法），**不用 `System.Random`**
//  （它的算法在 .NET 版本之间变过 → 同一个种子给不出同一串数 → 测试会"偶尔红"）。
//  ⚠️ **为什么这里不共用服务端那一份**：那份是**战斗规则**的一部分（双端要对账），
//     这份只是"本端什么时候丢一个包"（只影响本端、只要可复现即可）—— 两者会朝不同方向演化，
//     硬合并反而会让"改模拟器"顺手改到"战斗随机"。**该分的地方分，该合的地方合**（这里是该分）。
//
//  ---------------------------------------------------------------------------
//  五、它不做什么（如实写）
//  ---------------------------------------------------------------------------
//  · **不做重传/乱序重组**：TCP 已经保证了；UDP 的那套（序号 + 冗余重传）属 M4
//  · **不做带宽限制/大包切片**：M3 的包很小（几百字节），切片是 M5 优化的事
//  · **不模拟"连接断开"**：那由 `Closed` 事件表达，模拟器只延迟与丢弃
// ============================================================================

using System;
using System.Collections.Generic;

namespace NBC.Framework.Net.Sim
{
    /// <summary>模拟参数（全部可空手调；单位见名字）。</summary>
    public struct NetSimProfile
    {
        /// <summary>固定延迟（毫秒）。0 = 不延迟。</summary>
        public int LatencyMs;

        /// <summary>抖动幅度（毫秒）：实际延迟 = 固定延迟 + [0, JitterMs]。</summary>
        public int JitterMs;

        /// <summary>丢包概率（**万分比**：10000 = 全丢，500 = 5%）。见文件头第三节：M3 的 TCP 上丢包=消息永远不到。</summary>
        public int LossPerTenThousand;

        /// <summary>一个"看起来正常"的默认档（本机联调时用）。</summary>
        /// <returns>参数。</returns>
        public static NetSimProfile Default()
        {
            return new NetSimProfile { LatencyMs = 100, JitterMs = 40, LossPerTenThousand = 0 };
        }

        /// <summary>一句人话（界面/日志直接用）。</summary>
        /// <returns>描述。</returns>
        public override string ToString()
        {
            if (LatencyMs <= 0 && JitterMs <= 0 && LossPerTenThousand <= 0)
            {
                return "直连（不模拟）";
            }

            return "延迟 " + LatencyMs + "ms" +
                   (JitterMs > 0 ? "±" + JitterMs + "ms" : string.Empty) +
                   (LossPerTenThousand > 0 ? "，丢包 " + (LossPerTenThousand / 100.0) + "%" : string.Empty);
        }
    }

    /// <summary>
    /// 网络模拟器：包住任意 `ITransport`，按参数延迟/丢弃消息。
    /// <para>⚠️ 用法是 **先 `Advance(deltaMs)` 再 `Pump()`**（见文件头第二节）。</para>
    /// </summary>
    public sealed class SimulatedTransport : ITransport
    {
        /// <summary>被包住的那个真实实现。</summary>
        private readonly ITransport m_inner;

        /// <summary>延迟中的**上行**包（`Send` 出去的，等待送达 inner）。</summary>
        private readonly List<InFlight> m_sending = new List<InFlight>();

        /// <summary>延迟中的**下行**包（inner 收到的，等待交给上层）。</summary>
        private readonly List<InFlight> m_receiving = new List<InFlight>();

        /// <summary>抖动/丢包用的确定性随机数（见文件头第四节）。</summary>
        private uint m_random;

        /// <summary>模拟时钟（由 `Advance` 累加）。</summary>
        private long m_clockMs;

        /// <summary>当前参数。</summary>
        private NetSimProfile m_profile;

        /// <summary>是否已经连上（透传 inner）。</summary>
        private bool m_disposed;

        /// <summary>建一个模拟器（默认直连）。</summary>
        /// <param name="inner">被包住的实现。</param>
        /// <param name="seed">随机种子（固定值 → 可复现；不同连接用不同种子即可）。</param>
        public SimulatedTransport(ITransport inner, int seed = 20260923)
        {
            m_inner = inner ?? throw new ArgumentNullException(nameof(inner));
            m_random = seed == 0 ? 0x1234_5678u : unchecked((uint)seed);

            m_inner.FrameReceived += OnInnerFrameReceived;
            m_inner.Closed += OnInnerClosed;
        }

        /// <summary>当前参数（改它立刻生效）。</summary>
        public NetSimProfile Profile
        {
            get { return m_profile; }
            set { m_profile = value; }
        }

        /// <summary>模拟时钟（毫秒）。</summary>
        public long ClockMs
        {
            get { return m_clockMs; }
        }

        /// <summary>累计丢了多少条（上行 + 下行）。</summary>
        public long FramesDropped { get; private set; }

        /// <summary>累计延迟送达了多少条。</summary>
        public long FramesDelayed { get; private set; }

        /// <summary>当前还在"路上"的包数（上行 + 下行）。</summary>
        public int InFlightCount
        {
            get { return m_sending.Count + m_receiving.Count; }
        }

        /// <summary>实测到的最大单程延迟（毫秒）。</summary>
        public int MaxDelayMs { get; private set; }

        /// <summary>透传：inner 的状态。</summary>
        public ETransportState State
        {
            get { return m_inner.State; }
        }

        /// <summary>透传：inner 的统计（模拟器自己的丢包/延迟另有计数）。</summary>
        public TransportStats Stats
        {
            get { return m_inner.Stats; }
        }

        /// <summary>人话描述（带上模拟档位）。</summary>
        public string Description
        {
            get { return m_inner.Description + " +模拟[" + m_profile + "]"; }
        }

        /// <summary>透传。</summary>
        public bool IsConnected
        {
            get { return m_inner.IsConnected; }
        }

        /// <summary>收到一整帧（**延迟之后**才触发）。</summary>
        public event Action<byte[]> FrameReceived;

        /// <summary>断开（透传）。</summary>
        public event Action<string> Closed;

        /// <summary>发起连接（透传给 inner）。</summary>
        /// <param name="host">主机。</param>
        /// <param name="port">端口。</param>
        public void Connect(string host, int port)
        {
            m_inner.Connect(host, port);
        }

        /// <summary>
        /// 发一条消息：**先按丢包概率决定要不要丢**，没丢就延迟 `Latency + 抖动` 之后再交给 inner。
        /// </summary>
        /// <param name="payload">载荷。</param>
        public void Send(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (RollLoss("上行"))
            {
                return;
            }

            byte[] copy = (byte[])payload.Clone();
            int delay = RollDelay();

            if (delay <= 0)
            {
                m_inner.Send(copy);
                return;
            }

            FramesDelayed++;
            m_sending.Add(new InFlight(copy, m_clockMs + delay, delay));
        }

        /// <summary>推进模拟器的时钟：把"该到了"的包送出去 / 交上来（见文件头第二节）。</summary>
        /// <param name="deltaMs">距上次调用的毫秒数。</param>
        public void Advance(int deltaMs)
        {
            if (deltaMs < 0)
            {
                deltaMs = 0;
            }

            m_clockMs += deltaMs;

            // ① 上行：到期的交给 inner
            for (int i = m_sending.Count - 1; i >= 0; i--)
            {
                if (m_sending[i].DeliverAtMs <= m_clockMs)
                {
                    byte[] payload = m_sending[i].Payload;
                    m_sending.RemoveAt(i);
                    m_inner.Send(payload);
                }
            }

            // ② 下行：到期的交给上层
            for (int i = m_receiving.Count - 1; i >= 0; i--)
            {
                if (m_receiving[i].DeliverAtMs <= m_clockMs)
                {
                    byte[] payload = m_receiving[i].Payload;
                    m_receiving.RemoveAt(i);
                    RaiseFrameReceived(payload);
                }
            }
        }

        /// <summary>推进 inner（模拟器自己的推进请用 `Advance`）。</summary>
        public void Pump()
        {
            m_inner.Pump();
        }

        /// <summary>主动关闭（透传）。</summary>
        public void Close()
        {
            m_sending.Clear();
            m_receiving.Clear();
            m_inner.Close();
        }

        /// <summary>释放（同时释放 inner）。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_inner.FrameReceived -= OnInnerFrameReceived;
            m_inner.Closed -= OnInnerClosed;
            m_sending.Clear();
            m_receiving.Clear();
            m_inner.Dispose();
            m_disposed = true;
        }

        /// <summary>inner 收到一帧：按参数延迟之后才交给上层。</summary>
        /// <param name="payload">载荷。</param>
        private void OnInnerFrameReceived(byte[] payload)
        {
            if (RollLoss("下行"))
            {
                return;
            }

            int delay = RollDelay();

            if (delay <= 0)
            {
                RaiseFrameReceived(payload);
                return;
            }

            FramesDelayed++;
            m_receiving.Add(new InFlight(payload, m_clockMs + delay, delay));
        }

        /// <summary>inner 断开：直接透传（断开不模拟延迟 —— "说不通"比"晚一点说不通"更重要）。</summary>
        /// <param name="reason">原因。</param>
        private void OnInnerClosed(string reason)
        {
            Action<string> handler = Closed;

            if (handler != null)
            {
                handler(reason);
            }
        }

        /// <summary>把一帧交给上层（并记下实测延迟）。</summary>
        /// <param name="payload">载荷。</param>
        private void RaiseFrameReceived(byte[] payload)
        {
            Action<byte[]> handler = FrameReceived;

            if (handler != null)
            {
                handler(payload);
            }
        }

        /// <summary>掷一次丢包。</summary>
        /// <param name="direction">方向（日志用）。</param>
        /// <returns>该丢返回 true。</returns>
        private bool RollLoss(string direction)
        {
            if (m_profile.LossPerTenThousand <= 0)
            {
                return false;
            }

            if (NextPerTenThousand() >= m_profile.LossPerTenThousand)
            {
                return false;
            }

            FramesDropped++;
            return true;
        }

        /// <summary>掷一次延迟（固定 + 抖动）。</summary>
        /// <returns>毫秒。</returns>
        private int RollDelay()
        {
            int delay = m_profile.LatencyMs;

            if (m_profile.JitterMs > 0)
            {
                delay += Next(m_profile.JitterMs + 1);
            }

            if (delay < 0)
            {
                delay = 0;
            }

            if (delay > MaxDelayMs)
            {
                MaxDelayMs = delay;
            }

            return delay;
        }

        /// <summary>确定性随机：`[0, maxExclusive)`。</summary>
        /// <param name="maxExclusive">上界。</param>
        /// <returns>值。</returns>
        private int Next(int maxExclusive)
            => maxExclusive <= 0 ? 0 : (int)(NextUInt() % (uint)maxExclusive);

        /// <summary>确定性随机：万分比掷骰（0..9999）。</summary>
        /// <returns>值。</returns>
        private int NextPerTenThousand() => Next(10000);

        /// <summary>xorshift32（与 `BattleRandom` 同一套算法，见文件头第四节）。</summary>
        /// <returns>随机数。</returns>
        private uint NextUInt()
        {
            uint x = m_random;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            m_random = x;
            return x;
        }

        /// <summary>一个"在路上"的包。</summary>
        private readonly struct InFlight
        {
            /// <summary>载荷。</summary>
            public readonly byte[] Payload;

            /// <summary>什么时候该送达（模拟时钟，毫秒）。</summary>
            public readonly long DeliverAtMs;

            /// <summary>这一包被延迟了多少毫秒。</summary>
            public readonly int DelayMs;

            /// <summary>记一包。</summary>
            /// <param name="payload">载荷。</param>
            /// <param name="deliverAtMs">送达时刻。</param>
            /// <param name="delayMs">延迟。</param>
            public InFlight(byte[] payload, long deliverAtMs, int delayMs)
            {
                Payload = payload;
                DeliverAtMs = deliverAtMs;
                DelayMs = delayMs;
            }
        }
    }
}
