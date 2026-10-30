// 这个文件里有：
//   ETransportState（枚举）：传输层连接的四个状态，从没连到已关闭
//   TransportStats（结构体）：一条传输的累计收发字节与帧数快照，只读且不可变

namespace NBC.Framework.Net
{
    // 主要功能：描述一条传输连接从创建到关闭的四个阶段，供上层判断能否建连与收发。
    // 主要数据成员：Disconnected / Connecting / Connected / Closed 四个枚举值，取值 0..3。
    // 主要外部方法：无（纯枚举，经 ITransport.State 暴露）。
    // 谁在用它：Framework.Net 的 TcpTransport 维护，SimulatedTransport / CountingTransport 原样转发；Tests 直接断言状态迁移。
    /// <summary>传输层连接的四个状态，供上层判断能否建连与收发。</summary>
    public enum ETransportState
    {
        /// <summary>未连接：初始状态，或断开之后。</summary>
        Disconnected = 0,

        /// <summary>正在连接；注意：非阻塞，连上与否要靠 Pump 推进。</summary>
        Connecting = 1,

        /// <summary>已连接，可以收发。</summary>
        Connected = 2,

        /// <summary>已关闭（主动关闭或出错关闭）；ITransport.Closed 事件会带原因。</summary>
        Closed = 3
    }

    // 主要功能：把一条传输的累计收发字节数与帧数打成一份不可变快照，供看板与排查读取。
    // 主要数据成员：BytesSent / BytesReceived（单位字节，含帧头）、FramesSent / FramesReceived（单位帧，一帧一条完整载荷），均只增不减。
    // 主要外部方法：构造 TransportStats(...)、ToString()。
    // 谁在用它：TcpTransport.Stats 产出；SimulatedTransport / CountingTransport 原样转发；Game 的 NetSession.TransportStats 转发，Editor 的 NetDebugWindow 显示，Tests 断言计数。
    /// <summary>一条传输的累计收发计数快照，只读且不可变。</summary>
    public readonly struct TransportStats
    {
        /// <summary>累计发送字节数（含帧头），单位：字节。</summary>
        public readonly long BytesSent;

        /// <summary>累计接收字节数（含帧头），单位：字节。</summary>
        public readonly long BytesReceived;

        /// <summary>累计发送帧数（一帧一条完整载荷），单位：帧。</summary>
        public readonly int FramesSent;

        /// <summary>累计接收帧数（一帧一条完整载荷），单位：帧。</summary>
        public readonly int FramesReceived;

        /// <summary>用四个累计计数造一份统计快照。</summary>
        public TransportStats(long bytesSent, long bytesReceived, int framesSent, int framesReceived)
        {
            // 功能：把四个累计计数原样装进只读结构，造一份不可变快照。
            // 入参：bytesSent 累计发送字节（含帧头）；bytesReceived 累计接收字节（含帧头）；framesSent 累计发送帧数；framesReceived 累计接收帧数
            // 返回：无（构造出实例）
            // 步骤：
            //   1) 四个字段逐一赋值，不校验、不换算——单位与口径由调用方保证
            // 上游：TcpTransport.Stats 与 Tests 的 FakeTransport
            // 下游：无
            BytesSent = bytesSent;
            BytesReceived = bytesReceived;
            FramesSent = framesSent;
            FramesReceived = framesReceived;
        }

        /// <summary>转成一行人类可读的收发摘要，供看板与日志显示。</summary>
        /// <returns>形如「发 3 帧/120 B，收 2 帧/80 B」的文本；仅用于显示，格式不保证可解析。</returns>
        public override string ToString()
        {
            // 功能：按「发…收…」的固定格式拼接四个计数，得到一行看板文本。
            // 入参：无
            // 返回：拼好的单行文本，仅用于显示；格式不保证稳定，别拿它做解析。
            // 步骤：
            //   1) 固定「先发后收」的顺序，和看板上的列顺序一致，便于对照
            //   2) 帧数在前、字节在后，两段各自成组，避免看板列串位
            // 上游：Editor 的 NetDebugWindow（每帧画「传输」行）
            // 下游：无
            return "发 " + FramesSent + " 帧/" + BytesSent + " B，收 " + FramesReceived + " 帧/" + BytesReceived + " B";
        }
    }
}
