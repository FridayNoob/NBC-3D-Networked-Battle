// ============================================================================
//  NBC.Server.Core —— 网络传输抽象
//  项目：3D联网战斗Demo   对应需求文档 §13.5（方式 4）、§15.1 模式 13（适配器）
//
//  ⚠️ 这个接口是需求文档 §13.5「单机模式」得以成立的关键：
//     单机模式不是"另写一套逻辑"，而是【换一个 INetTransport 实现】。
//
//     TcpTransport  —— 真实联机（M3 实现）
//     LocalTransport —— 同进程内存队列（单机模式，M3 实现）
// ============================================================================

namespace NBC.Server.Core;

/// <summary>
/// 网络传输抽象。屏蔽"数据从哪来"的差异。
/// </summary>
public interface INetTransport
{
    /// <summary>该传输是否已就绪。</summary>
    bool IsRunning { get; }

    /// <summary>启动传输（TCP 为监听端口；本地传输为建队列）。</summary>
    void Start();

    /// <summary>停止传输并释放资源。</summary>
    void Stop();

    /// <summary>
    /// 发送一段已序列化的报文。
    /// </summary>
    /// <param name="sessionId">目标会话 ID。</param>
    /// <param name="payload">已序列化的字节（长度前缀由实现层负责附加）。</param>
    void Send(long sessionId, ReadOnlySpan<byte> payload);

    /// <summary>关闭指定会话。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="reason">关闭原因，用于日志。</param>
    void Disconnect(long sessionId, string reason);
}
