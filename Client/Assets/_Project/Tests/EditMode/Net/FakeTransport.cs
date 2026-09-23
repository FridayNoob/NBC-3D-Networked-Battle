// ============================================================================
//  FakeTransport —— `ITransport` 的**测试替身**（不开端口、不等网络）
//  项目：3D联网战斗Demo   对应：M3-S2b
//
//  ---------------------------------------------------------------------------
//  它存在的理由
//  ---------------------------------------------------------------------------
//  接缝（`ITransport`）的价值有一半就体现在这里：**业务逻辑的测试不该依赖真实网络**。
//  有了它，M3-S4 之后的"房间/快照/伤害同步"那些测试可以：
//    · 手工决定"这一帧收到了什么"（`PushFrame`）—— 网络时序变成参数，而不是运气
//    · 手工决定"什么时候掉线"（`FailWith`）—— 断线分支不再"走不到"
//    · 断言"我发了什么"（`SentFrames`）—— 不需要真的有个服务端在听
//
//  ---------------------------------------------------------------------------
//  ⚠️ 两条与真实实现**故意对齐**的行为（不对齐的话测试会给出假信心）
//  ---------------------------------------------------------------------------
//  ① **未连接时 `Send` 抛异常**，不是静默丢 —— 与 `TcpTransport` 同一条契约
//     （`FakeTransportContracts` 里有一条用例专门钉它）
//  ② **收到的一帧只在 `Pump` 里交出去** —— 与 `ITransport` 的文档一致，
//     这样"每帧收一次"的调用方写法在两种实现下都是对的
//
//  另外它**不做分帧**：`SentFrames` 里存的就是载荷本身。
//  所以它**不能**代替 `FrameCodecTests` / `TcpTransportTests` 去验粘包拆包
//  （分帧是实现的职责，这里刻意不算进假实现里，免得"假绿"）。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework.Net;
using NBC.Shared.Net;

namespace NBC.Tests.EditMode
{
    /// <summary>内存里的假传输：想收什么就 `PushFrame`，想断就 `FailWith`。</summary>
    public sealed class FakeTransport : ITransport
    {
        /// <summary>发出去的载荷（按顺序）。</summary>
        private readonly List<byte[]> m_sent = new List<byte[]>();

        /// <summary>等着在 `Pump` 里交出去的载荷。</summary>
        private readonly Queue<byte[]> m_incoming = new Queue<byte[]>();

        /// <summary>当前状态。</summary>
        private ETransportState m_state = ETransportState.Disconnected;

        /// <summary>关闭原因。</summary>
        private string m_closedReason;

        /// <summary>`Closed` 是否报过（契约：至多一次）。</summary>
        private bool m_closeNotified;

        private long m_bytesSent;
        private long m_bytesReceived;

        /// <summary>被 `Connect` 的目标主机（断言用）。</summary>
        public string Host { get; private set; }

        /// <summary>被 `Connect` 的目标端口（断言用）。</summary>
        public int Port { get; private set; }

        /// <summary>`Connect` 被调了几次。</summary>
        public int ConnectCount { get; private set; }

        /// <summary>收到一整帧时回调。</summary>
        public event Action<byte[]> FrameReceived;

        /// <summary>关闭时回调（至多一次）。</summary>
        public event Action<string> Closed;

        /// <summary>当前状态。</summary>
        public ETransportState State
        {
            get { return m_state; }
        }

        /// <summary>收发统计。</summary>
        public TransportStats Stats
        {
            get
            {
                return new TransportStats(m_bytesSent, m_bytesReceived, m_sent.Count, FramesReceived);
            }
        }

        /// <summary>人话描述（关闭后带上原因，和真实实现一个形状）。</summary>
        public string Description
        {
            get
            {
                string endpoint = Host == null ? "(未连接)" : Host + ":" + Port;
                return m_closedReason == null ? "fake " + endpoint : "fake " + endpoint + "（" + m_closedReason + "）";
            }
        }

        /// <summary>是不是连上了。</summary>
        public bool IsConnected
        {
            get { return m_state == ETransportState.Connected; }
        }

        /// <summary>在 `Pump` 里交出去过多少帧。</summary>
        public int FramesReceived { get; private set; }

        /// <summary>发出去的载荷（**只读**；不含帧头，假实现不分帧）。</summary>
        public IReadOnlyList<byte[]> SentFrames
        {
            get { return m_sent; }
        }

        /// <summary>最后发出去的那一条（没发过则为 null）。</summary>
        public byte[] LastSent
        {
            get { return m_sent.Count == 0 ? null : m_sent[m_sent.Count - 1]; }
        }

        /// <summary>连接：**立刻**进入已连接（测试里不需要等）。</summary>
        /// <param name="host">主机。</param>
        /// <param name="port">端口。</param>
        public void Connect(string host, int port)
        {
            Host = host;
            Port = port;
            ConnectCount++;
            m_state = ETransportState.Connected;
            m_closedReason = null;
            m_closeNotified = false;
        }

        /// <summary>发一条消息（未连接时抛异常 —— 与真实实现同一条契约）。</summary>
        /// <param name="payload">载荷。</param>
        public void Send(byte[] payload)
        {
            if (m_state != ETransportState.Connected)
            {
                throw new InvalidOperationException(
                    "[FakeTransport] 还没连上就 Send（当前：" + Description + "）。");
            }

            byte[] copy = payload == null ? new byte[0] : (byte[])payload.Clone();
            m_sent.Add(copy);
            m_bytesSent += NetContract.FrameLengthPrefixBytes + copy.Length;
        }

        /// <summary>推进一帧：把排队的"对面发来的消息"交出去。</summary>
        public void Pump()
        {
            while (m_state == ETransportState.Connected && m_incoming.Count > 0)
            {
                byte[] payload = m_incoming.Dequeue();
                m_bytesReceived += NetContract.FrameLengthPrefixBytes + payload.Length;
                FramesReceived++;

                Action<byte[]> handler = FrameReceived;

                if (handler != null)
                {
                    handler(payload);
                }

                if (m_state != ETransportState.Connected)
                {
                    return;     // 回调里可能已经断开了
                }
            }
        }

        /// <summary>主动关闭（幂等）。</summary>
        public void Close()
        {
            CloseWithReason("主动关闭");
        }

        /// <summary>释放（等价于关闭）。</summary>
        public void Dispose()
        {
            Close();
        }

        // ====================================================================
        //  测试用的驱动口
        // ====================================================================

        /// <summary>模拟"对面发来一条消息"（在下次 `Pump` 里交出去）。</summary>
        /// <param name="payload">载荷。</param>
        public void PushFrame(byte[] payload)
        {
            m_incoming.Enqueue(payload == null ? new byte[0] : (byte[])payload.Clone());
        }

        /// <summary>模拟一次错误断开（`Closed` 会带这句原因）。</summary>
        /// <param name="reason">原因。</param>
        public void FailWith(string reason)
        {
            CloseWithReason(reason);
        }

        /// <summary>清掉"发出去过"的记录（只影响断言，不影响状态）。</summary>
        public void ClearSent()
        {
            m_sent.Clear();
        }

        /// <summary>关掉并报一次原因。</summary>
        /// <param name="reason">原因。</param>
        private void CloseWithReason(string reason)
        {
            if (m_state == ETransportState.Closed)
            {
                return;
            }

            m_state = ETransportState.Closed;
            m_closedReason = reason;
            m_incoming.Clear();

            if (!m_closeNotified)
            {
                m_closeNotified = true;
                Action<string> handler = Closed;

                if (handler != null)
                {
                    handler(reason);
                }
            }
        }
    }
}
