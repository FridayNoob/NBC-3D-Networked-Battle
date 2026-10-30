// 这个文件里有：
//   CountingTransport（类）：给任意 ITransport 加一层收发字节统计

using System;

namespace NBC.Framework.Net
{
    // 主要功能：包住任意 ITransport，把发送 / 收到的 payload 字节数交给 NetRuntimeStats，其余行为原样转发。
    // 主要数据成员：m_inner（被包的传输层）。
    // 主要外部方法：ITransport 全套（Connect / Send / Pump / Close / IsConnected / 两个事件）。
    // 谁在用它：建 transport 的地方（今天 Editor 窗口，以后运行时启动器）。
    // 注意：统计的是「业务字节」（payload 长度），不含 TCP 头与分帧头 ⇒ 比抓包工具的数字小，这是预期。
    public sealed class CountingTransport : ITransport
    {
        // 被包的传输层（真实的 TcpTransport，或测试用的 SimulatedTransport）
        private readonly ITransport m_inner;

        // 已经 Dispose 就不再转发（避免二次释放）
        private bool m_disposed;

        // 功能：包住一个传输层，挂上收发计数并转发关闭事件。
        // 入参：inner 被包的传输层
        // 返回：无
        // 上游：建 transport 的地方
        // 下游：无
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

        // 功能：转发底层的连接状态枚举。
        // 入参：无
        // 返回：底层当前状态（连接中 / 已连接 / 已关闭）
        public ETransportState State
        {
            get { return m_inner.State; }
        }

        // 功能：转发底层自带的收发统计。
        // 入参：无
        // 返回：底层自带的收发统计（原样转发；本装饰器只额外记字节，不改它的统计）
        public TransportStats Stats
        {
            get { return m_inner.Stats; }
        }

        // 功能：转发底层的人话描述。
        // 入参：无
        // 返回：底层的人话描述（日志用）
        public string Description
        {
            get { return m_inner.Description; }
        }

        // 功能：转发底层的「是否已连接」。
        // 入参：无
        // 返回：底层是否已连接
        public bool IsConnected
        {
            get { return m_inner.IsConnected; }
        }

        // 收到一帧时抛出（先计数再抛，保证统计不漏）
        public event Action<byte[]> FrameReceived;

        // 连接关闭时抛出
        public event Action<string> Closed;

        // 功能：把连接请求转给底层（非阻塞）。
        // 入参：host / port
        // 返回：无（连接是异步的，结果看 Closed 或 IsConnected）
        public void Connect(string host, int port)
        {
            m_inner.Connect(host, port);
        }

        // 功能：记一笔发送字节后再交给底层发送。
        // 入参：payload 业务负载
        // 返回：无
        // 步骤：1) 先记字节数；2) 再交给底层发送
        // 上游：NetSession
        // 下游：ITransport.Send
        public void Send(byte[] payload)
        {
            if (payload != null)
            {
                NetRuntimeStats.AddSent(payload.Length);
            }

            m_inner.Send(payload);
        }

        // 功能：把「推进一帧」转给底层。
        // 入参：无
        // 返回：无
        public void Pump()
        {
            m_inner.Pump();
        }

        // 功能：把主动关闭转给底层。
        // 入参：无
        // 返回：无
        public void Close()
        {
            m_inner.Close();
        }

        // 功能：退订事件并释放底层，重复调用安全。
        // 入参：无
        // 返回：无
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

        // 功能：收到一帧时先计数，再抛给订阅者。
        // 入参：payload 收到的一帧
        // 返回：无
        // 步骤：1) 记字节数；2) 再抛给订阅者（订阅者抛异常也不会漏计）
        // 上游：被包的传输层
        // 下游：FrameReceived 订阅者
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

        // 功能：底层关闭时把原因抛给订阅者。
        // 入参：reason 关闭原因
        // 返回：无
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
