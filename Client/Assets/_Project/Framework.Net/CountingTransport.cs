// 这个文件里有：
//   CountingTransport（类）：给任意 ITransport 加一层收发业务字节统计的装饰器，其余行为原样转发

using System;

namespace NBC.Framework.Net
{
    // 主要功能：包住任意 ITransport，把收发的业务字节交给 NetRuntimeStats 累计，其余行为原样转发。
    // 主要数据成员：m_inner（被包的传输层）、m_disposed（Dispose 是否已执行过）。
    // 主要外部方法：ITransport 全套（State / Stats / Description / IsConnected / Connect / Send / Pump / Close / Dispose / FrameReceived / Closed）。
    // 谁在用它：组装网络栈的两处 —— Game 的 RuntimeNetDriver.Start、Editor 的 NetDebugWindow.ConnectSession；都是包一层再交给 NetSession。
    // 注意：统计的只是业务字节（payload 长度），不含 TCP 头与分帧头 ⇒ 比抓包工具的数字小，这是预期。
    /// <summary>传输层装饰器：给任意 ITransport 加一层收发字节统计，本层不做协议也不切线程。</summary>
    public sealed class CountingTransport : ITransport
    {
        // 被包的传输层：真实的 TcpTransport，或测试 / 网络模拟器用的 SimulatedTransport；构造时已判空，之后不再更换
        private readonly ITransport m_inner;

        // 是否已经 Dispose 过；取值 true / false，只有 Dispose 自己看它（true 时直接返回）
        private bool m_disposed;

        // 功能：包住一个传输层，把它的收到 / 关闭事件接到本类。
        // 入参：inner 被包的传输层；为 null 时抛 ArgumentNullException（早抛，免得后面每次转发都空引用）
        // 返回：无（构造）
        // 步骤：
        //   1) 先判空再赋值：把 null 存进 m_inner，之后每个转发方法都会炸，且爆点离现场很远
        //   2) 订阅 FrameReceived / Closed：前者统计下行字节，后者保证关闭原因能传到订阅者
        // 上游：RuntimeNetDriver.Start、NetDebugWindow.ConnectSession（都包一层再交给 NetSession）
        // 下游：m_inner.FrameReceived、m_inner.Closed
        // 注意：回调在调用 m_inner.Pump 的那个线程上同步发生，本类不切线程 —— 订阅者要碰 UI 就得自己保证在主线程调 Pump。
        /// <summary>包住一个传输层，此后收发都经本类计数，其余行为原样转发。</summary>
        /// <param name="inner">被包的传输层，不能为 null。</param>
        public CountingTransport(ITransport inner)
        {
            if (inner == null)
            {
                throw new ArgumentNullException(nameof(inner), "[CountingTransport] 被包的传输层不能为 null。");
            }

            m_inner = inner;
            m_inner.FrameReceived += OnInnerFrameReceived;
            m_inner.Closed += OnInnerClosed;
        }

        /// <summary>底层当前状态（连接中 / 已连接 / 已关闭）；原样转发，本类不另存一份。</summary>
        public ETransportState State
        {
            get { return m_inner.State; }
        }

        /// <summary>底层自带的收发统计；本类额外记的字节在 NetRuntimeStats 里，不改这里。</summary>
        public TransportStats Stats
        {
            get { return m_inner.Stats; }
        }

        /// <summary>底层的人话描述（日志用，形如 tcp 127.0.0.1:7777；关闭后带上原因）。</summary>
        public string Description
        {
            get { return m_inner.Description; }
        }

        /// <summary>底层是否已连接（等价于 State == Connected）。</summary>
        public bool IsConnected
        {
            get { return m_inner.IsConnected; }
        }

        /// <summary>收到一整帧时回调；先计数再转发，订阅者抛异常也不会漏计（异常会往上传给底层的 Pump 调用者）。</summary>
        public event Action<byte[]> FrameReceived;

        /// <summary>断开或出错时回调，参数是底层给的人话原因（主动关闭时是「主动关闭」）；同一次连接至多一次，由底层保证。</summary>
        public event Action<string> Closed;

        // 功能：把连接请求原样转给底层；本层不握手、不缓存 host / port，失败也不在这里抛。
        /// <summary>发起连接（非阻塞）：成败看 Closed 或 IsConnected，连不上不抛异常。</summary>
        /// <param name="host">主机（IP 或域名）。</param>
        /// <param name="port">端口（1~65535，取值校验在底层）。</param>
        public void Connect(string host, int port)
        {
            m_inner.Connect(host, port);
        }

        // 功能：记一笔发送的业务字节，再交给底层真正发出去。
        // 入参：payload 业务载荷；null 表示一条 0 长度帧，按 0 字节处理（跳过计数）
        // 返回：无
        // 步骤：
        //   1) 先按 payload.Length 记账（null 直接跳过），让统计落在发送动作之前
        //   2) 再交给底层：分帧、连接检查、未连接报错都在底层做，本层不重复判断
        // 上游：NetSession（每条上行消息都经它）
        // 下游：NetRuntimeStats.AddSent、ITransport.Send
        // 注意：记账在发送之前 —— 底层若因未连接或超限帧抛异常，字节已经记上了，统计会偏大。
        /// <summary>发一条载荷：先记业务字节，再原样交给底层发送。</summary>
        /// <param name="payload">载荷，可以为 null（0 长度帧，不计数）。</param>
        public void Send(byte[] payload)
        {
            if (payload != null)
            {
                NetRuntimeStats.AddSent(payload.Length);
            }

            m_inner.Send(payload);
        }

        // 功能：把「推进一帧」原样转给底层；下行字节在底层的 FrameReceived 里记，这一层不计数。
        /// <summary>推进一帧：收字节、拆帧、冲刷发送队列都由底层做。</summary>
        public void Pump()
        {
            m_inner.Pump();
        }

        // 功能：把主动关闭原样转给底层；底层会当场触发 Closed，本类照常转发给订阅者，本层不另发。
        /// <summary>主动关闭；可重复调用，第一次之后状态就是已关闭。</summary>
        public void Close()
        {
            m_inner.Close();
        }

        // 功能：先退订底层事件，再释放底层；重复调用直接返回，不重复释放。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 用 m_disposed 做幂等闸门，第二次起什么都不做
        //   2) 先退订两个事件再释放：否则底层 Dispose 时发的 Closed 还会被本类转发出去，订阅者会多收一次「释放导致的断开」
        //   3) 最后才释放底层，本类此后不再持有它的引用
        // 上游：NetSession.Dispose（Game 的 RuntimeNetDriver 与 Editor 的 NetDebugWindow 收尾时都走它）
        // 下游：m_inner.FrameReceived、m_inner.Closed、ITransport.Dispose
        // 注意：幂等只覆盖 Dispose 本身 —— 释放之后 Send / Connect 仍会转发给底层，由底层抛 ObjectDisposedException。
        /// <summary>退订事件并释放底层；可重复调用。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;
            m_inner.FrameReceived -= OnInnerFrameReceived;
            m_inner.Closed -= OnInnerClosed;
            m_inner.Dispose();
        }

        // 功能：把收到的一帧先记进下行统计，再抛给本类订阅者。
        // 入参：payload 收到的一整帧（可以为 null = 0 长度帧，此时不计数）
        // 返回：无
        // 步骤：
        //   1) 先记账再抛：订阅者抛异常时字节已经落账，统计不会漏
        //   2) 把事件复制到局部变量再判空调用：判空与调用之间若有订阅者被摘掉，直接读字段会空引用
        // 上游：m_inner 的 FrameReceived（即底层 Pump 期间）
        // 下游：NetRuntimeStats.AddReceived、本类 FrameReceived 订阅者
        // 注意：订阅者抛的异常不会被吞 —— 它穿过底层一路传给 Pump 的调用者（通常是每帧驱动方）。
        private void OnInnerFrameReceived(byte[] payload)
        {
            if (payload != null)
            {
                NetRuntimeStats.AddReceived(payload.Length);
            }

            Action<byte[]> handler = FrameReceived;

            if (handler != null)
            {
                handler(payload);
            }
        }

        // 功能：底层关闭时，把原因原样转给本类订阅者。
        // 入参：reason 底层给的关闭原因（人话，日志可直接显示）
        // 返回：无
        // 步骤：
        //   1) 同样先把事件复制到局部变量再判空，理由同 OnInnerFrameReceived
        //   2) reason 直接往下传，本层不改写也不去重（去重是底层的契约）
        // 上游：m_inner 的 Closed
        // 下游：本类 Closed 订阅者
        private void OnInnerClosed(string reason)
        {
            Action<string> handler = Closed;

            if (handler != null)
            {
                handler(reason);
            }
        }
    }
}
