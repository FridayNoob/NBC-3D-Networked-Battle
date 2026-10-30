// 这个文件里有：
//   NetSimProfile（结构体）：网络模拟档位，固定延迟 / 抖动（毫秒）与丢包概率（万分比）
//   SimulatedTransport（类）：包住任意 ITransport，按档位延迟或丢弃上下行消息，对外仍是 ITransport
//   InFlight（结构体）：一个还在路上的包，记载荷、送达时刻与本次延迟
//
// 注意：用法必须先 Advance(deltaMs) 再 Pump()，顺序反了这一帧该到的包要等下一帧。
// 注意：当前链路是 TCP，应用层丢一条就是真的永远不到，连握手都会失败；验抗抖动用延迟 / 抖动，丢包留给以后 UDP 式的帧同步与 EditMode 用例（证明模拟器真的在丢）。
// 注意：随机数用自带的 xorshift，不用 System.Random（算法跨 .NET 版本变过，同一颗种子给不出同一串数，测试会偶发红）。
// 注意：这份随机数不与服务端的 BattleRandom 合并 —— 那份属战斗规则、双端要对账，这份只决定本端丢不丢包，两者会朝不同方向演化。
// 注意：不模拟重传 / 乱序重组 / 带宽限制 / 断线，断开只由 Closed 事件表达。

using System;
using System.Collections.Generic;

namespace NBC.Framework.Net.Sim
{
    // 主要功能：描述一条模拟链路有多坏：固定延迟、抖动幅度与丢包概率，充当 SimulatedTransport 的档位参数。
    // 主要数据成员：LatencyMs（毫秒）、JitterMs（毫秒）、LossPerTenThousand（万分比，0~10000）。
    // 主要外部方法：Default（默认档）、ToString（人话描述）；三个字段本身公开，调用方直接改。
    // 谁在用它：SimulatedTransport 读它决定延迟与丢包；EditMode 的 SimulatedTransportTests 构造它来设档。
    /// <summary>网络模拟档位：固定延迟、抖动幅度与丢包概率，字段公开，直接改即可。</summary>
    public struct NetSimProfile
    {
        /// <summary>固定延迟，毫秒；0 表示不延迟。</summary>
        public int LatencyMs;

        /// <summary>抖动幅度，毫秒；实际延迟 = 固定延迟 + [0, JitterMs]，0 表示不抖。</summary>
        public int JitterMs;

        /// <summary>丢包概率，万分比，0~10000（500 = 5%，10000 = 全丢）；TCP 上丢了就是这条消息永远不到。</summary>
        public int LossPerTenThousand;

        // 功能：给出一档「看起来正常」的本机联调参数：延迟 100 毫秒、抖动 40 毫秒、不丢包。
        // 入参：无
        // 返回：填好的档位；每次都是新值，调用方可以随手改。
        // 步骤：
        //   1) 三项写死，不读外部配置 —— 默认档只为本机联调，要别的档位直接设字段
        // 上游：本机联调、EditMode 的 SimulatedTransportTests
        // 下游：无
        /// <summary>默认档：延迟 100 毫秒、抖动 40 毫秒、不丢包，本机联调用。</summary>
        /// <returns>新建的档位，调用方可以直接改。</returns>
        public static NetSimProfile Default()
        {
            return new NetSimProfile { LatencyMs = 100, JitterMs = 40, LossPerTenThousand = 0 };
        }

        // 功能：把档位拼成一句人话；三项全为 0 时说「直连（不模拟）」，否则形如「延迟 100ms±40ms，丢包 5%」。
        // 入参：无
        // 返回：给界面与日志直接显示的字符串；丢包按 LossPerTenThousand / 100 换算成百分比，未开启的项不出现。
        // 步骤：
        //   1) 三项都 <= 0 就按直连处理，免得显示成「延迟 0ms」
        //   2) 否则先拼固定延迟，有抖动才追加 ±，有丢包才追加百分比
        // 上游：SimulatedTransport.Description，以及直接打日志 / 显示档位的地方
        // 下游：无
        /// <summary>拼成一句人话，供界面与日志显示。</summary>
        /// <returns>例如「延迟 100ms±40ms，丢包 5%」；全 0 时为「直连（不模拟）」。</returns>
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

    // 主要功能：包住任意 ITransport，按档位给上下行消息加延迟与抖动、按概率丢弃，对外仍然只是同一条 ITransport。
    // 主要数据成员：m_inner（被包的实现）、m_sending / m_receiving（上下行延迟队列，按模拟时钟排队）、m_clockMs（模拟时钟，毫秒）、m_random（xorshift 状态）、m_profile（当前档位）。
    // 主要外部方法：Advance（推进模拟时钟，必须先于 Pump）、Send / Pump / Connect / Close / Dispose，以及 Profile / ClockMs / FramesDropped / FramesDelayed / InFlightCount / MaxDelayMs 等观测成员。
    // 谁在用它：目前只有 EditMode 的 SimulatedTransportTests（包住 FakeTransport 验延迟与丢包）；上层拿到的是 ITransport，不关心包没包模拟器。
    // 注意：它是有意做的坏网络 —— 好网络测不出同步策略的问题。
    /// <summary>网络模拟器：包住任意 ITransport，按档位延迟或丢弃消息，对外仍是一条 ITransport。</summary>
    public sealed class SimulatedTransport : ITransport
    {
        // 被包住的实现：真实的 TcpTransport，或测试用的 FakeTransport；构造时已判空，之后不再更换
        private readonly ITransport m_inner;

        // 延迟中的上行包：Send 出来、等模拟时钟到点再交给 m_inner
        private readonly List<InFlight> m_sending = new List<InFlight>();

        // 延迟中的下行包：m_inner 收到、等模拟时钟到点再交给上层
        private readonly List<InFlight> m_receiving = new List<InFlight>();

        // 抖动与丢包用的 xorshift32 状态；任意 uint 都行，0 会让输出永远是 0（构造里已避开）
        private uint m_random;

        // 模拟时钟，毫秒；从 0 起只增不减，只由 Advance 推进
        private long m_clockMs;

        // 当前档位；default 全 0 即直连，改 Profile 立刻生效
        private NetSimProfile m_profile;

        // 是否已经 Dispose 过；true 时 Dispose 直接返回，保证只释放一次
        private bool m_disposed;

        // 功能：包住一个真实传输层，接上它的收帧与关闭事件，并把种子换成 xorshift 初值。
        // 入参：inner 被包的实现，不能为 null；seed 随机种子，默认 20260923，0 换成固定初值 0x12345678（同一种子才能复现同一串丢包）
        // 返回：无（构造）
        // 步骤：
        //   1) 先判空：null 存进去要等每次转发才炸，爆点离现场很远
        //   2) seed 为 0 时换成非 0 初值，否则 xorshift 会一直输出 0
        //   3) 订阅 inner 的 FrameReceived / Closed，之后由本类按延迟与丢包重放
        // 上游：SimulatedTransportTests 的 Make
        // 下游：m_inner.FrameReceived、m_inner.Closed
        /// <summary>包住一个传输层，此后收发都先经本类的延迟与丢包模拟。</summary>
        /// <param name="inner">被包的传输层，不能为 null。</param>
        /// <param name="seed">随机种子，默认 20260923；固定种子才能复现同一串丢包，0 按固定初值处理。</param>
        public SimulatedTransport(ITransport inner, int seed = 20260923)
        {
            m_inner = inner ?? throw new ArgumentNullException(nameof(inner));
            m_random = seed == 0 ? 0x1234_5678u : unchecked((uint)seed);

            m_inner.FrameReceived += OnInnerFrameReceived;
            m_inner.Closed += OnInnerClosed;
        }

        /// <summary>当前档位；改它立刻生效，默认全 0 即直连。</summary>
        public NetSimProfile Profile
        {
            get { return m_profile; }
            set { m_profile = value; }
        }

        /// <summary>模拟时钟，毫秒；从 0 起只增不减，只由 Advance 推进。</summary>
        public long ClockMs
        {
            get { return m_clockMs; }
        }

        /// <summary>累计丢弃的帧数，上下行合计；只增不减。</summary>
        public long FramesDropped { get; private set; }

        /// <summary>累计被延迟送达的帧数，上下行合计；延迟为 0 直接送达的不算。</summary>
        public long FramesDelayed { get; private set; }

        /// <summary>当前还在路上的帧数，上下行合计。</summary>
        public int InFlightCount
        {
            get { return m_sending.Count + m_receiving.Count; }
        }

        /// <summary>实测到的最大单次延迟，毫秒；只增不减，改档位也不重置。</summary>
        public int MaxDelayMs { get; private set; }

        /// <summary>m_inner 的状态，原样透传。</summary>
        public ETransportState State
        {
            get { return m_inner.State; }
        }

        /// <summary>m_inner 的收发统计，原样透传；本层自己的丢包与延迟看 FramesDropped / FramesDelayed。</summary>
        public TransportStats Stats
        {
            get { return m_inner.Stats; }
        }

        /// <summary>m_inner 的人话描述，后面拼上本层档位，形如「tcp 127.0.0.1:7777 +模拟[延迟 100ms]」。</summary>
        public string Description
        {
            get { return m_inner.Description + " +模拟[" + m_profile + "]"; }
        }

        /// <summary>m_inner 是否已连接，原样透传。</summary>
        public bool IsConnected
        {
            get { return m_inner.IsConnected; }
        }

        /// <summary>收到一整帧时回调，已按档位延迟；只在 Advance 里、在调用 Advance 的线程上触发。</summary>
        public event Action<byte[]> FrameReceived;

        /// <summary>断开或出错时回调，参数是人话原因；原样透传，不加延迟。</summary>
        public event Action<string> Closed;

        // 功能：把连接请求原样转给 m_inner；本层不握手、不缓存地址。
        /// <summary>发起连接，原样转给 m_inner。</summary>
        /// <param name="host">主机（IP 或域名）。</param>
        /// <param name="port">端口（1~65535）。</param>
        public void Connect(string host, int port)
        {
            m_inner.Connect(host, port);
        }

        // 功能：上行发包 —— 先按丢包率掷一次，没丢就按固定延迟加抖动排进队列，到点再交给 m_inner。
        // 入参：payload 一帧载荷，不能为 null（0 长度帧传空数组，不传 null）
        // 返回：无；丢包与延迟都只是排队或结束，不报错
        // 步骤：
        //   1) 先判空：null 交给 m_inner 会炸在更远的地方
        //   2) 先掷丢包再复制：丢掉的包不必复制，省一次分配
        //   3) 复制一份再延迟：调用方可能复用同一块缓冲，不复制会把后来的内容带给 m_inner
        //   4) 延迟为 0 直接发，省一次队列进出
        //   5) 否则记 FramesDelayed 并按送达时刻入 m_sending，等 Advance 到期再发
        // 上游：NetSession 的每条上行消息（当它被包在 SimulatedTransport 里时）
        // 下游：RollLoss、RollDelay、m_inner.Send、m_sending
        /// <summary>发一条消息：先掷丢包，没丢再按延迟排队后交给 m_inner。</summary>
        /// <param name="payload">一帧载荷，不能为 null；0 长度帧传空数组。</param>
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

        // 功能：推进模拟时钟，把到期的上下行包分别交给 m_inner 与上层。
        // 入参：deltaMs 距上次调用的毫秒数；负数按 0 处理
        // 返回：无
        // 步骤：
        //   1) 负数钳到 0，时钟只前进不后退
        //   2) 时钟先加 deltaMs，这一帧到期的包才算得出来
        //   3) 上行从后往前扫：送出去的包要从队列里移除，倒序才不会漏掉换位上来的元素
        //   4) 下行同样倒序，到期的包交给 RaiseFrameReceived
        // 上游：每帧驱动方（NetSession.Pump 之前）；SimulatedTransportTests 手动调
        // 下游：m_inner.Send、RaiseFrameReceived
        // 注意：必须先 Advance 再 Pump，否则这一帧该到的包要等下一帧；ITransport 里没有时间轴，时间只能由这个额外入口推。
        /// <summary>推进模拟时钟，把到期的包送出去或交上来；必须先于 Pump 调用。</summary>
        /// <param name="deltaMs">距上次调用的毫秒数，负数按 0 处理。</param>
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

        // 功能：把推进一帧原样转给 m_inner；模拟器自己的时间推进走 Advance。
        /// <summary>推进 m_inner 一帧；时间推进用 Advance，不用这里。</summary>
        public void Pump()
        {
            m_inner.Pump();
        }

        // 功能：清掉上下行两个延迟队列，再关闭 m_inner。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先清队列：关掉之后这些包已经不可能送达，留着只会让 InFlightCount 停在非 0
        //   2) 再关 m_inner，让它按自己的契约触发 Closed
        // 上游：NetSession 的主动断开与退出流程
        // 下游：m_inner.Close
        /// <summary>主动关闭：丢弃还在路上的包，再关闭 m_inner。</summary>
        public void Close()
        {
            m_sending.Clear();
            m_receiving.Clear();
            m_inner.Close();
        }

        // 功能：先退订 m_inner 的事件再释放它；重复调用直接返回。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 用 m_disposed 做幂等闸门，第二次起什么都不做
        //   2) 先退订再释放：否则 m_inner 释放时发的 Closed 还会被本类转出去，订阅者会多收一次「释放导致的断开」
        //   3) 清掉两个队列，再释放 m_inner，最后置位 m_disposed
        // 上游：NetSession.Dispose（持有本类的一方收尾）
        // 下游：m_inner.FrameReceived、m_inner.Closed、m_inner.Dispose
        /// <summary>退订事件、清空在路上队列并释放 m_inner；可重复调用。</summary>
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

        // 功能：m_inner 收到一帧时，按同一套丢包与延迟规则处理后交给上层。
        // 入参：payload 收到的一整帧（m_inner 给的数组，本层不复制）
        // 返回：无
        // 步骤：
        //   1) 先掷丢包：丢了就不再排队，直接结束
        //   2) 掷延迟，0 就直接交给上层，省一次队列进出
        //   3) 否则记 FramesDelayed 并按送达时刻入 m_receiving，等 Advance 到点再交
        // 上游：m_inner 的 FrameReceived（即 m_inner.Pump 期间）
        // 下游：RollLoss、RollDelay、RaiseFrameReceived、m_receiving
        // 注意：下行没有复制载荷 —— 若将来 m_inner 复用同一块缓冲，这里必须补一次复制。
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

        // 功能：m_inner 断开时，把原因原样转给本类订阅者。
        // 入参：reason 关闭原因，人话，日志可直接显示
        // 返回：无
        // 步骤：
        //   1) 先把事件复制到局部变量再判空，避免判空与调用之间订阅者被摘掉而空引用
        // 上游：m_inner 的 Closed
        // 下游：本类 Closed 订阅者
        // 注意：断开故意不排队延迟 —— 让上层晚一点知道「说不通」没有意义。
        private void OnInnerClosed(string reason)
        {
            Action<string> handler = Closed;

            if (handler != null)
            {
                handler(reason);
            }
        }

        // 功能：把一帧交给本类订阅者。
        // 入参：payload 要交上去的一整帧
        // 返回：无
        // 步骤：
        //   1) 先复制事件到局部变量再判空，理由同 OnInnerClosed
        // 上游：Advance（到期的下行包）、OnInnerFrameReceived（零延迟）
        // 下游：本类 FrameReceived 订阅者；没有订阅者时这一帧就丢了
        private void RaiseFrameReceived(byte[] payload)
        {
            Action<byte[]> handler = FrameReceived;

            if (handler != null)
            {
                handler(payload);
            }
        }

        // 功能：按档位掷一次丢包，中了就累加 FramesDropped。
        // 入参：direction 方向，取「上行」或「下行」；目前不参与判断，留给日志
        // 返回：true 表示这一帧该丢；概率 <= 0 时恒为 false
        // 步骤：
        //   1) 概率 <= 0 直接放行，省一次随机数与取模
        //   2) 掷 0~9999，>= 阈值算不丢 —— 所以阈值 10000 必丢、500 约丢 5%
        // 上游：Send（上行）、OnInnerFrameReceived（下行）
        // 下游：NextPerTenThousand、FramesDropped
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

        // 功能：算出这一次的延迟，固定延迟加抖动，并更新 MaxDelayMs。
        // 入参：无
        // 返回：延迟毫秒数，已钳到 >= 0
        // 步骤：
        //   1) 固定延迟打底；有抖动才掷随机数，取 [0, JitterMs]（上界 +1 是为了能取到 JitterMs 本身）
        //   2) 负数钳到 0，防止负延迟让包提前到期
        //   3) 更新 MaxDelayMs，供观测
        // 上游：Send、OnInnerFrameReceived
        // 下游：Next、MaxDelayMs
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

        // 功能：取一个 [0, maxExclusive) 的确定性随机整数。
        // 入参：maxExclusive 上界（不含）；<= 0 时直接返回 0
        // 返回：0 ~ maxExclusive-1；上界为 0 时返回 0
        // 步骤：
        //   1) 上界 <= 0 先挡住，否则取模会除零
        //   2) 用 xorshift 的输出对 (uint)maxExclusive 取模，不引入 System.Random
        // 上游：RollDelay、NextPerTenThousand
        // 下游：NextUInt
        private int Next(int maxExclusive)
            => maxExclusive <= 0 ? 0 : (int)(NextUInt() % (uint)maxExclusive);

        // 功能：掷一个 0~9999 的万分比随机数。
        // 入参：无
        // 返回：0~9999，配合 LossPerTenThousand 判断丢包
        // 上游：RollLoss
        // 下游：Next(10000)
        private int NextPerTenThousand() => Next(10000);

        // 功能：推进一步 xorshift32，产出下一个随机数。
        // 入参：无
        // 返回：uint 随机值；状态为 0 时会一直输出 0（构造已保证初值非 0）
        // 步骤：
        //   1) 依次左移 13、右移 17、左移 5 再异或，是 xorshift32 的标准三步
        //   2) 新值写回 m_random，下一次从它继续
        // 上游：Next
        // 下游：无
        private uint NextUInt()
        {
            uint x = m_random;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            m_random = x;
            return x;
        }

        // 主要功能：记下一个还在路上的包，只给 SimulatedTransport 的两个延迟队列用。
        // 主要数据成员：Payload（载荷）、DeliverAtMs（送达时刻，模拟时钟毫秒）、DelayMs（本次延迟，毫秒）。
        // 主要外部方法：只有构造函数；三个字段都是 readonly，建好不再改。
        // 谁在用它：SimulatedTransport 的 Send / OnInnerFrameReceived 入队、Advance 出队；不跨出本类。
        private readonly struct InFlight
        {
            // 载荷；上行是 Send 前复制的副本，下行是 m_inner 给的数组
            public readonly byte[] Payload;

            // 送达时刻，模拟时钟毫秒；Advance 里 <= m_clockMs 即到期
            public readonly long DeliverAtMs;

            // 这一次的延迟，毫秒；目前只记录，没有读取方
            public readonly int DelayMs;

            // 功能：记下一个待送达的包。
            // 入参：payload 载荷（调用方负责复制）；deliverAtMs 送达时刻（模拟时钟，毫秒）；delayMs 本次延迟（毫秒）
            // 返回：无（构造）
            // 步骤：
            //   1) 三个字段都是 readonly，只能在这里一次赋值
            // 上游：SimulatedTransport.Send、SimulatedTransport.OnInnerFrameReceived
            // 下游：无
            public InFlight(byte[] payload, long deliverAtMs, int delayMs)
            {
                Payload = payload;
                DeliverAtMs = deliverAtMs;
                DelayMs = delayMs;
            }
        }
    }
}
