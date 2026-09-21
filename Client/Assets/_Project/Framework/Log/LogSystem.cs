// ============================================================================
//  NBC.Framework.Log · 日志系统
//  对应需求：FW-M11（分级日志、文件落盘、**战斗中滚动日志**、**网络收发日志开关**）
//
//  ---------------------------------------------------------------------------
//  四件事，各自对应需求的四个要求
//  ---------------------------------------------------------------------------
//    ① **分级**    —— `MinLevel` 以下的直接丢，不拼接、不分配
//    ② **落盘**    —— 挂一个 `FileLogSink`（带滚动，见那个文件）
//    ③ **滚动日志** —— 内存里留最近 N 条（环形缓冲），给游戏内控制台面板读
//    ④ **频道开关** —— 单独关掉"网络收发"，而战斗日志照常
//
//  ---------------------------------------------------------------------------
//  ⚠️ 两条关于性能的硬规矩（写在这里，免得以后忘）
//  ---------------------------------------------------------------------------
//  **① 判断发生在字符串拼接之前。**
//      不要写：
//          LogSystem.Instance.LogInfo(LogChannel.Battle, "位置 " + pos);   // 拼接白做了
//      要写：
//          if (LogSystem.Instance.IsEnabled(LogLevel.Info, LogChannel.Battle))
//              LogSystem.Instance.LogInfo(LogChannel.Battle, "位置 " + pos);
//      热路径上（每帧刷的那种）**必须用后者** —— 否则关闭日志也照样每帧分配字符串。
//
//  **② 每帧刷日志本身就是 bug。**
//      这个实现是同步写文件的，每帧写几百条会把帧率吃光。
//      真要高频记录，应该改成"写进环形缓冲，退出/出错时一次性 dump"。
//      （需求 OPT-26 也点名了"日志用条件编译"，那是 M5 优化的事。）
//
//  ---------------------------------------------------------------------------
//  已知局限（写清楚，不藏）
//  ---------------------------------------------------------------------------
//    · 同步 I/O，高频写会卡（见上面第 ②条）
//    · 没有线程安全保证 —— 目前只有主线程在记日志。
//      将来网络线程要记，得加锁或者改成"每线程队列 + 主线程合并"
//    · 环形缓冲固定容量，超了覆盖最旧的；**不做"溢出条数"的提示**
//      （`TotalCount` 和 `RingCount` 的差值能看出来，但不去打扰使用者）
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework.Log
{
    /// <summary>
    /// 日志系统：分级 + 频道过滤，可挂多个输出目标，内存里保留最近若干条。
    /// </summary>
    public sealed class LogSystem : Singleton<LogSystem>
    {
        /// <summary>默认的滚动日志容量（内存里保留最近多少条）。</summary>
        public const int DefaultRingCapacity = 200;

        /// <summary>频道总数。</summary>
        private static readonly int ChannelCount = Enum.GetValues(typeof(LogChannel)).Length;

        /// <summary>各频道是否启用。</summary>
        private readonly bool[] m_channelEnabled = new bool[ChannelCount];

        /// <summary>输出目标。</summary>
        private readonly List<ILogSink> m_sinks = new List<ILogSink>();

        /// <summary>滚动日志的环形缓冲。</summary>
        private readonly LogEntry[] m_ring;

        private int m_ringStart;
        private int m_ringCount;

        private LogLevel m_minLevel = LogLevel.Info;

        // ====================================================================
        //  查询
        // ====================================================================

        /// <summary>
        /// 最低级别：低于它的日志直接丢弃。
        /// <para>默认 `Info`（即 `Debug` 默认关）。</para>
        /// </summary>
        public LogLevel MinLevel
        {
            get { return m_minLevel; }
            set { m_minLevel = value; }
        }

        /// <summary>挂了多少个输出目标。</summary>
        public int SinkCount
        {
            get { return m_sinks.Count; }
        }

        /// <summary>环形缓冲容量。</summary>
        public int RingCapacity
        {
            get { return m_ring.Length; }
        }

        /// <summary>当前缓冲里有多少条。</summary>
        public int RingCount
        {
            get { return m_ringCount; }
        }

        /// <summary>累计写出去多少条（含已经被覆盖的）。</summary>
        public long TotalCount { get; private set; }

        /// <summary>因为级别不够被丢掉的条数。</summary>
        public long DroppedByLevelCount { get; private set; }

        /// <summary>因为频道关闭被丢掉的条数。</summary>
        public long DroppedByChannelCount { get; private set; }

        // ====================================================================
        //  开关
        // ====================================================================

        /// <summary>
        /// 这一条会不会被记录。
        /// <para>
        /// ⚠️ **热路径上先用它短路，再拼字符串**（见文件头第 ①条）。
        /// </para>
        /// </summary>
        /// <param name="level">级别。</param>
        /// <param name="channel">频道。</param>
        /// <returns>会记录返回 true。</returns>
        public bool IsEnabled(LogLevel level, LogChannel channel)
        {
            if (level < m_minLevel)
            {
                return false;
            }

            return m_channelEnabled[ChannelIndex(channel)];
        }

        /// <summary>单独开关一个频道（FW-M11 的"网络收发日志开关"就是它）。</summary>
        /// <param name="channel">频道。</param>
        /// <param name="enabled">是否启用。</param>
        public void SetChannelEnabled(LogChannel channel, bool enabled)
        {
            m_channelEnabled[ChannelIndex(channel)] = enabled;
        }

        /// <summary>某个频道是否启用。</summary>
        /// <param name="channel">频道。</param>
        /// <returns>启用返回 true。</returns>
        public bool IsChannelEnabled(LogChannel channel)
        {
            return m_channelEnabled[ChannelIndex(channel)];
        }

        // ====================================================================
        //  输出目标
        // ====================================================================

        /// <summary>挂一个输出目标。</summary>
        /// <param name="sink">目标。</param>
        public void AddSink(ILogSink sink)
        {
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            if (!m_sinks.Contains(sink))
            {
                m_sinks.Add(sink);
            }
        }

        /// <summary>摘掉一个输出目标（**不会 Dispose 它**，由调用方决定）。</summary>
        /// <param name="sink">目标。</param>
        /// <returns>确实摘掉了返回 true。</returns>
        public bool RemoveSink(ILogSink sink)
        {
            return sink != null && m_sinks.Remove(sink);
        }

        /// <summary>把所有输出目标刷一遍。**退出前必须调**，否则最后几条会丢。</summary>
        public void Flush()
        {
            for (int i = 0; i < m_sinks.Count; i++)
            {
                m_sinks[i].Flush();
            }
        }

        // ====================================================================
        //  写
        // ====================================================================

        /// <summary>
        /// 写一条日志。
        /// </summary>
        /// <param name="level">级别。</param>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文。**不允许 null** —— 那是调用方的 bug，当场报出来。</param>
        public void Write(LogLevel level, LogChannel channel, string message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(
                    nameof(message),
                    "[LogSystem] 日志正文不能为 null（想记空行请传 string.Empty）。");
            }

            if (level < m_minLevel)
            {
                DroppedByLevelCount++;
                return;
            }

            if (!m_channelEnabled[ChannelIndex(channel)])
            {
                DroppedByChannelCount++;
                return;
            }

            LogEntry entry = new LogEntry(level, channel, message, DateTime.Now);

            TotalCount++;
            PushToRing(entry);

            for (int i = 0; i < m_sinks.Count; i++)
            {
                m_sinks[i].Write(entry);
            }
        }

        /// <summary>记一条 Debug。</summary>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文。</param>
        public void LogDebug(LogChannel channel, string message)
        {
            Write(LogLevel.Debug, channel, message);
        }

        /// <summary>记一条 Info。</summary>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文。</param>
        public void LogInfo(LogChannel channel, string message)
        {
            Write(LogLevel.Info, channel, message);
        }

        /// <summary>记一条 Warning。</summary>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文。</param>
        public void LogWarning(LogChannel channel, string message)
        {
            Write(LogLevel.Warning, channel, message);
        }

        /// <summary>记一条 Error。</summary>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文。</param>
        public void LogError(LogChannel channel, string message)
        {
            Write(LogLevel.Error, channel, message);
        }

        // ====================================================================
        //  滚动日志（游戏内控制台读它）
        // ====================================================================

        /// <summary>
        /// 把最近的日志拷进一个列表。
        /// <para>**新的在前** —— 控制台面板从上往下显示，不必再转一次。</para>
        /// </summary>
        /// <param name="buffer">接收结果的列表（会先清空）。</param>
        public void CopyRecent(List<LogEntry> buffer)
        {
            if (buffer == null)
            {
                return;
            }

            buffer.Clear();

            for (int i = 0; i < m_ringCount; i++)
            {
                // 从最新往回数：ringStart 是最旧的那条
                int index = (m_ringStart + m_ringCount - 1 - i) % m_ring.Length;
                buffer.Add(m_ring[index]);
            }
        }

        /// <summary>清空滚动日志（**不清统计**）。</summary>
        public void ClearRecent()
        {
            Array.Clear(m_ring, 0, m_ring.Length);
            m_ringStart = 0;
            m_ringCount = 0;
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>
        /// 构造：建缓冲、默认全频道打开。
        /// <para>
        /// ⚠️ 必须是 **public** —— 基类的 `where T : new()` 约束要求 public 无参构造。
        /// 外部就算 `new LogSystem()` 也会被基类构造函数当场拒绝（见 A1 的 `Singleton`），
        /// 所以这里不需要（也做不到）用 private 来挡。
        /// </para>
        /// </summary>
        public LogSystem()
        {
            m_ring = new LogEntry[DefaultRingCapacity];

            for (int i = 0; i < m_channelEnabled.Length; i++)
            {
                m_channelEnabled[i] = true;
            }
        }

        /// <summary>把一条压进环形缓冲（满了就覆盖最旧的）。</summary>
        /// <param name="entry">日志条目。</param>
        private void PushToRing(in LogEntry entry)
        {
            if (m_ringCount < m_ring.Length)
            {
                m_ring[(m_ringStart + m_ringCount) % m_ring.Length] = entry;
                m_ringCount++;
                return;
            }

            // 满了：覆盖最旧的那条，并把起点往后挪一格
            m_ring[m_ringStart] = entry;
            m_ringStart = (m_ringStart + 1) % m_ring.Length;
        }

        /// <summary>频道转下标，顺带挡住非法值。</summary>
        /// <param name="channel">频道。</param>
        /// <returns>下标。</returns>
        private static int ChannelIndex(LogChannel channel)
        {
            int index = (int)channel;

            if (index < 0 || index >= ChannelCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(channel), channel, "[LogSystem] 未知的日志频道。");
            }

            return index;
        }

        /// <summary>
        /// 播放模式下自动挂上控制台输出。
        /// <para>
        /// ⚠️ **EditMode 下不挂** —— 否则测试用例自己写进去的 Error
        /// 会变成"未预期的日志"把用例判失败（见 `UnityConsoleLogSink` 的说明）。
        /// </para>
        /// </summary>
        protected override void OnInit()
        {
            if (Application.isPlaying)
            {
                AddSink(new UnityConsoleLogSink());
            }
        }

        /// <summary>销毁时刷出去并释放实现了 `IDisposable` 的目标。</summary>
        protected override void OnDispose()
        {
            Flush();

            for (int i = 0; i < m_sinks.Count; i++)
            {
                IDisposable disposable = m_sinks[i] as IDisposable;

                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }

            m_sinks.Clear();

            ClearRecent();

            m_minLevel = LogLevel.Info;
            TotalCount = 0;
            DroppedByLevelCount = 0;
            DroppedByChannelCount = 0;

            for (int i = 0; i < m_channelEnabled.Length; i++)
            {
                m_channelEnabled[i] = true;
            }
        }
    }
}
