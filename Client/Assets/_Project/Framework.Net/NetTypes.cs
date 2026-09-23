// ============================================================================
//  NetTypes —— 网络层的公共小类型（**不认识任何具体传输**）
//  项目：3D联网战斗Demo   对应：M3-S2
//
//  ⚠️ 它属于 **NBC.Framework.Net**，不属于 `NBC.Framework`。理由写在 `ITransport.cs`
//     的文件头（一句话：整个网络层是**不碰引擎**的，这样才能在 .NET 8 探针里真跑）。
// ============================================================================

namespace NBC.Framework.Net
{
    /// <summary>传输层的连接状态。</summary>
    public enum ETransportState
    {
        /// <summary>没连（初始状态，或断开之后）。</summary>
        Disconnected = 0,

        /// <summary>正在连（非阻塞：连上与否靠 `Pump` 推进）。</summary>
        Connecting = 1,

        /// <summary>连上了，可以收发。</summary>
        Connected = 2,

        /// <summary>已关闭（主动关或出错关）；`Closed` 事件里会带原因。</summary>
        Closed = 3
    }

    /// <summary>传输层的收发统计（**观测点**：网络模拟器与性能看板都读它）。</summary>
    public readonly struct TransportStats
    {
        /// <summary>发出去多少**字节**（含帧头）。</summary>
        public readonly long BytesSent;

        /// <summary>收到多少**字节**（含帧头）。</summary>
        public readonly long BytesReceived;

        /// <summary>发出去多少**帧**。</summary>
        public readonly int FramesSent;

        /// <summary>收到多少**帧**。</summary>
        public readonly int FramesReceived;

        /// <summary>造一份统计。</summary>
        /// <param name="bytesSent">发送字节。</param>
        /// <param name="bytesReceived">接收字节。</param>
        /// <param name="framesSent">发送帧数。</param>
        /// <param name="framesReceived">接收帧数。</param>
        public TransportStats(long bytesSent, long bytesReceived, int framesSent, int framesReceived)
        {
            BytesSent = bytesSent;
            BytesReceived = bytesReceived;
            FramesSent = framesSent;
            FramesReceived = framesReceived;
        }

        /// <summary>转成一句人话。</summary>
        /// <returns>描述。</returns>
        public override string ToString()
        {
            return "发 " + FramesSent + " 帧/" + BytesSent + " B，收 " + FramesReceived + " 帧/" + BytesReceived + " B";
        }
    }
}
