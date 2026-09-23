// ============================================================================
//  NBC.Server.Core —— 网络传输抽象
//  项目：3D联网战斗Demo   对应需求文档 §13.5（方式 4）、§15.1 模式 13（适配器）
//
//  ⚠️ 这个接口是需求文档 §13.5「单机模式」得以成立的关键：
//     单机模式不是"另写一套逻辑"，而是【换一个 INetTransport 实现】。
//
//     TcpServerTransport —— 真实联机（M3-S3 实现，本文件旁边那个）
//     LocalTransport     —— 同进程内存队列（单机模式，日后实现）
//
//  ---------------------------------------------------------------------------
//  M3-S3 的补记：M0 留下的骨架【少了半边】
//  ---------------------------------------------------------------------------
//  M0 只写了"启动/停止/发送/断开"，**没有"收到消息"的出口** ——
//  而服务端最要紧的恰恰是收。2026-09-23 开工 S3 时读源码才发现（Docs\25 §八），
//  于是补上三样：
//    · `Pump()`                —— 单线程推进（接受连接 / 收 / 发都在它里面发生）
//    · `MessageReceived`       —— 收到一条**完整载荷**（分帧已经做完）
//    · `SessionOpened/Closed`  —— 会话建立与断开（断开带人话原因）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么是 `Pump()`（单线程拉模式），而不是"网络线程 + 回调改战斗状态"
//  ---------------------------------------------------------------------------
//  M0 的注释里写的是"网络 IO 回调线程只做解包 + 入队" —— 那个设计需要线程与锁。
//  M3 改成**根本没有第二个线程**：`Pump()` 由主循环每帧调一次，用非阻塞 `Poll`
//  收与发。买到的是三样：
//    ① **无锁**：状态只有一个线程碰过，"加锁漏了一处"这类 bug 不存在
//    ② **可复现**：同一串字节喂进 `Pump()` 结果一样，测试里不用 sleep 碰运气
//    ③ **与客户端同源**：客户端 `ITransport.Pump()`（`Framework.Net`）是同一形状，
//       两端的心智模型一致，联调时不用在两种模型之间切换
//  代价：IO 及时性取决于 Pump 频率（30Hz 主循环完全够 M3）。
//  仍然保留"收到先入队、逻辑在循环里做"的写法（见 `NBC.Server.Host`）——
//  这样将来真要换成多线程，只需把队列换成线程安全队列，别处不用动。
// ============================================================================

using System;

namespace NBC.Server.Core;

/// <summary>
/// 网络传输抽象。屏蔽"数据从哪来"的差异。
/// </summary>
public interface INetTransport
{
    /// <summary>该传输是否已就绪（`Start` 之后、`Stop` 之前为 true）。</summary>
    bool IsRunning { get; }

    /// <summary>
    /// 实际监听端口。
    /// <para>构造时传 0 表示"让系统分配一个空闲端口"，起来之后从这里读真实端口（测试要用）。</para>
    /// </summary>
    int Port { get; }

    /// <summary>当前还活着的会话数。</summary>
    int SessionCount { get; }

    /// <summary>启动传输（TCP 为监听端口；本地传输为建队列）。</summary>
    void Start();

    /// <summary>停止传输并释放资源（会关闭所有会话）。</summary>
    void Stop();

    /// <summary>
    /// 推进一帧：接受新连接 → 收字节拆帧 → 冲发送队列 → 清理超时会话。**都在调用它的那个线程上发生**。
    /// </summary>
    void Pump();

    /// <summary>
    /// 发送一段已序列化的报文。
    /// </summary>
    /// <param name="sessionId">目标会话 ID。</param>
    /// <param name="payload">已序列化的字节（长度前缀由实现层负责附加）。</param>
    /// <returns>入队成功返回 true；会话已经不在了返回 false（**不抛异常**）。</returns>
    /// <remarks>
    /// ⚠️ 为什么"会话不在"是返回值而不是异常：主循环里"某个会话刚被心跳超时踢掉"与
    /// "正要给它回包"撞上，属于**正常时序**，不该让服务端进程崩。
    /// （对比客户端 `ITransport.Send`：那里"还没连上就发"是调用方的 bug，所以抛。）
    /// </remarks>
    bool Send(long sessionId, ReadOnlySpan<byte> payload);

    /// <summary>
    /// 关闭指定会话。
    /// <para>⚠️ 实现必须先把它**已入队但还没发出去**的数据冲出去再关 ——
    /// 否则"为什么被踢"这类说明会到不了客户端（客户端只看到连接莫名其妙断了）。</para>
    /// </summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="reason">关闭原因，用于日志。</param>
    void Disconnect(long sessionId, string reason);

    /// <summary>有新连接进来（此时还没握手）。</summary>
    event Action<ClientSession>? SessionOpened;

    /// <summary>会话断开（人话原因；主动 `Stop()` 时为"服务端关闭"）。**至多一次**。</summary>
    event Action<ClientSession, string>? SessionClosed;

    /// <summary>收到一条**完整消息**（载荷；粘包/拆包已由实现处理）。</summary>
    event Action<ClientSession, byte[]>? MessageReceived;
}
