// 这个文件里有：
//   LogSystem（类）：日志系统的单例入口，按级别与频道过滤，条目写进环形缓冲并分发给各输出目标

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework.Log
{
    // 主要功能：日志的唯一入口，先按最低级别与频道开关过滤，再把条目存进内存环形缓冲并分发给挂载的输出目标。
    // 主要数据成员：m_minLevel（最低级别，默认 Info）、m_channelEnabled（各频道开关）、m_sinks（输出目标链）、m_ring / m_ringStart / m_ringCount（200 条环形缓冲）。
    // 主要外部方法：IsEnabled / SetChannelEnabled / IsChannelEnabled、AddSink / RemoveSink / Flush、Write 与 LogDebug / LogInfo / LogWarning / LogError、CopyRecent / ClearRecent。
    // 谁在用它：Game / Boot / Editor 跨程序集经 LogSystem.Instance 写日志、查滚动缓冲；EditMode 测试直接取单例并注入内存 Sink 断言过滤与滚动。
    // 注意：热路径必须先用 IsEnabled 短路再拼字符串，否则关了日志也照样每帧分配。
    // 注意：同步写文件且没有线程安全保证，目前只允许主线程记；每帧刷几百条会把帧率吃光。
    /// <summary>日志系统单例：按级别与频道过滤，条目写进输出目标链并在内存里保留最近若干条。</summary>
    public sealed class LogSystem : Singleton<LogSystem>
    {
        // 环形缓冲的默认容量，单位是条；构造时按它定长分配，之后不扩容。
        /// <summary>滚动日志默认保留的条数。</summary>
        public const int DefaultRingCapacity = 200;

        // 频道总数，等于 LogChannel 的枚举项数（当前 5），只用来给 m_channelEnabled 定长，运行期不变。
        private static readonly int ChannelCount = Enum.GetValues(typeof(LogChannel)).Length;

        // 各频道是否启用，下标是 (int)LogChannel（0..ChannelCount-1），一项一票，默认全部为 true。
        private readonly bool[] m_channelEnabled = new bool[ChannelCount];

        // 输出目标链，按挂载顺序接收放行的每一条；数量 0 起，播放模式下默认有一个控制台目标。
        private readonly List<ILogSink> m_sinks = new List<ILogSink>();

        // 滚动日志的环形缓冲，定长 DefaultRingCapacity（200 条）；构造完成后不再为 null。
        private readonly LogEntry[] m_ring;

        // 环形缓冲里最旧一条的下标，取值 0..m_ring.Length-1；空缓冲时读它没有意义。
        private int m_ringStart;
        // 环形缓冲当前条数，取值 0..m_ring.Length；到容量后不再增长，改由 m_ringStart 前移表示覆盖。
        private int m_ringCount;

        // 最低记录级别，取值 LogLevel.Debug..Error，默认 Info（即 Debug 默认关）；低于它的条目直接丢。
        private LogLevel m_minLevel = LogLevel.Info;


        // 最低记录级别，默认 Info；运行中可随时改，只影响之后写入的条目。
        /// <summary>最低记录级别：低于它的日志直接丢弃；默认 Info（即 Debug 默认关）。</summary>
        public LogLevel MinLevel
        {
            get { return m_minLevel; }
            set { m_minLevel = value; }
        }

        // 当前挂了多少个输出目标，范围 0 起；只读。
        /// <summary>当前挂载的输出目标数量。</summary>
        public int SinkCount
        {
            get { return m_sinks.Count; }
        }

        // 环形缓冲容量，固定为 DefaultRingCapacity（200 条），运行期不变；只读。
        /// <summary>环形缓冲容量（条）。</summary>
        public int RingCapacity
        {
            get { return m_ring.Length; }
        }

        // 当前缓冲里的条数，范围 0..RingCapacity；满了以后保持上限，靠 m_ringStart 前移覆盖旧条。
        /// <summary>当前缓冲里的条数。</summary>
        public int RingCount
        {
            get { return m_ringCount; }
        }

        // 累计写出去的条目数，从 0 起单调加，包含已被环形缓冲覆盖的；OnDispose 时归零。
        /// <summary>累计写出去的条目数（含已被覆盖的）。</summary>
        public long TotalCount { get; private set; }

        // 因级别低于 m_minLevel 被丢掉的条目数，从 0 起；不含频道关闭与参数异常。
        /// <summary>因级别不够被丢弃的条目数。</summary>
        public long DroppedByLevelCount { get; private set; }

        // 因频道被关闭而丢掉的条目数，从 0 起；级别过滤发生在它之前，两者不重叠。
        /// <summary>因频道关闭被丢弃的条目数。</summary>
        public long DroppedByChannelCount { get; private set; }


        /// <summary>判断这一条会不会被记录；热路径上先用它短路，再拼字符串。</summary>
        /// <param name="level">级别。</param>
        /// <param name="channel">频道。</param>
        /// <returns>会记录返回 true。</returns>
        public bool IsEnabled(LogLevel level, LogChannel channel)
        {
            // 功能：只查级别与频道开关，不做任何拼接与分配，供调用方在拼字符串之前短路。
            // 入参：level 级别（Debug..Error）；channel 频道，非法值由 ChannelIndex 抛异常
            // 返回：级别够且频道开着返回 true；否则 false
            // 步骤：
            //   1) 先比级别：低于 m_minLevel 直接返回 false，连频道都不查
            //   2) 再过频道：用 ChannelIndex 把枚举转下标，非法值当场抛 ArgumentOutOfRangeException
            // 上游：热路径调用方（Game / Boot / Editor）与逐格核对的 EditMode 测试
            // 下游：ChannelIndex
            // 注意：热路径必须先用它短路再拼字符串，否则关了日志也照样每帧分配字符串。
            if (level < m_minLevel)
            {
                return false;
            }

            return m_channelEnabled[ChannelIndex(channel)];
        }

        /// <summary>单独开关一个频道；网络收发日志就是这样只关一类、其它照常。</summary>
        /// <param name="channel">频道。</param>
        /// <param name="enabled">true 为放行，false 为丢弃。</param>
        public void SetChannelEnabled(LogChannel channel, bool enabled)
        {
            // 功能：把某个频道的开关写进 m_channelEnabled，之后该类日志按新状态放行或丢弃。
            // 入参：channel 频道，非法值由 ChannelIndex 抛异常；enabled true 放行、false 丢弃
            // 返回：无
            // 步骤：先用 ChannelIndex 校验并转下标，再赋值，非法频道在赋值前就抛出，不会改坏状态
            // 上游：Boot / Game 的日志设置、EditMode 测试
            // 下游：ChannelIndex、之后的 IsEnabled 与 Write
            // 注意：只影响之后写入的条目，缓冲里已有的内容不追溯。
            m_channelEnabled[ChannelIndex(channel)] = enabled;
        }

        /// <summary>查一个频道当前是否开着。</summary>
        /// <param name="channel">频道。</param>
        /// <returns>开着返回 true。</returns>
        public bool IsChannelEnabled(LogChannel channel)
        {
            // 功能：只读查频道开关，供设置面板与测试确认当前状态。
            // 入参：channel 频道，非法值由 ChannelIndex 抛异常
            // 返回：该频道开着返回 true，关着返回 false
            // 步骤：转下标后直接读数组，没有副作用
            // 上游：日志设置面板、EditMode 测试
            // 下游：ChannelIndex
            return m_channelEnabled[ChannelIndex(channel)];
        }


        /// <summary>挂一个输出目标；同一个实例重复挂载只保留一份。</summary>
        /// <param name="sink">输出目标，不允许为 null。</param>
        public void AddSink(ILogSink sink)
        {
            // 功能：把输出目标接进 Sink 链，之后每条放行的日志都会给它一份。
            // 入参：sink 输出目标，非 null；同一实例重复挂载是幂等的
            // 返回：无；sink 为 null 抛 ArgumentNullException
            // 步骤：
            //   1) 先挡 null：链里留一条 null，之后每条日志都会在 Write 时崩
            //   2) 用 Contains 去重，避免同一目标把同一条写两遍
            // 上游：LogSystem.OnInit（挂控制台）、游戏层（挂文件 Sink）、EditMode 测试（挂内存 Sink）
            // 下游：Write / Flush / OnDispose 遍历 m_sinks
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            if (!m_sinks.Contains(sink))
            {
                m_sinks.Add(sink);
            }
        }

        /// <summary>摘掉一个输出目标；不会替调用方 Dispose 它。</summary>
        /// <param name="sink">输出目标。</param>
        /// <returns>确实摘掉了返回 true。</returns>
        public bool RemoveSink(ILogSink sink)
        {
            // 功能：从 Sink 链里摘掉一个目标，交还调用方决定是否 Dispose。
            // 入参：sink 输出目标，null 视为没摘到
            // 返回：链里本来有并已移除返回 true；null 或不在链里返回 false
            // 步骤：先用短路判断挡住 null，再 Remove 一次遍历完成
            // 上游：游戏层、EditMode 测试收尾
            // 下游：之后的 Write / Flush / OnDispose 遍历
            // 注意：不会 Dispose 目标，文件句柄归调用方自己关；摘掉后环形缓冲与统计都不受影响。
            return sink != null && m_sinks.Remove(sink);
        }

        /// <summary>把所有输出目标刷一遍；退出前必须调，否则缓冲里的最后几条会丢。</summary>
        public void Flush()
        {
            // 功能：按挂载顺序逐个刷 Sink，把各自缓冲里的内容落到目的地。
            // 入参：无
            // 返回：无
            // 步骤：正序遍历 m_sinks 逐个 Flush，先挂的目标先落盘
            // 上游：调用方在退出前显式调用、Singleton.DisposeInstance（经 OnDispose）、测试收尾
            // 下游：各 ILogSink.Flush
            // 注意：文件 Sink 内部有 StreamWriter 缓冲，不调它最后几条会留在缓冲里丢掉。
            for (int i = 0; i < m_sinks.Count; i++)
            {
                m_sinks[i].Flush();
            }
        }


        /// <summary>写一条日志：先按级别与频道过滤，再进环形缓冲并分发到各输出目标。</summary>
        /// <param name="level">级别。</param>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文，不允许为 null；要空行请传 string.Empty。</param>
        public void Write(LogLevel level, LogChannel channel, string message)
        {
            // 功能：日志的唯一入口，过滤后生成条目、累加计数、压进环形缓冲并分发给每个 Sink。
            // 入参：level 级别；channel 频道；message 正文，非 null（null 视为调用方 bug 当场抛）
            // 返回：无；正文为 null 抛 ArgumentNullException，非法频道抛 ArgumentOutOfRangeException
            // 步骤：
            //   1) 先挡 null：异常消息带 [LogSystem] 前缀，并提示想记空行请传 string.Empty
            //   2) 级别不够就丢掉并累加 DroppedByLevelCount，不生成条目、不碰 Sink
            //   3) 频道关着就丢掉并累加 DroppedByChannelCount
            //   4) 先 new LogEntry（时间取本机本地时间）再累加 TotalCount，让计数与条目一一对应
            //   5) 先进环形缓冲再分发 Sink：文件与控制台慢，缓冲先保住最近记录
            //   6) 正序遍历 m_sinks 逐个写，Sink 的异常不在这里吞
            // 上游：LogDebug / LogInfo / LogWarning / LogError，以及直接调 Write 的调用方
            // 下游：PushToRing、各 ILogSink.Write
            // 注意：Sink 里不要再调 LogSystem 写日志，会无限递归；Sink 抛异常会中断后面的目标并把异常冒给调用方。
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

        /// <summary>记一条 Debug 级日志；默认级别下会被过滤掉。</summary>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文。</param>
        public void LogDebug(LogChannel channel, string message)
        {
            // 功能：一行转发到 Write，级别固定为 Debug。
            Write(LogLevel.Debug, channel, message);
        }

        /// <summary>记一条 Info 级日志。</summary>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文。</param>
        public void LogInfo(LogChannel channel, string message)
        {
            // 功能：一行转发到 Write，级别固定为 Info。
            Write(LogLevel.Info, channel, message);
        }

        /// <summary>记一条 Warning 级日志。</summary>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文。</param>
        public void LogWarning(LogChannel channel, string message)
        {
            // 功能：一行转发到 Write，级别固定为 Warning。
            Write(LogLevel.Warning, channel, message);
        }

        /// <summary>记一条 Error 级日志。</summary>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文。</param>
        public void LogError(LogChannel channel, string message)
        {
            // 功能：一行转发到 Write，级别固定为 Error。
            Write(LogLevel.Error, channel, message);
        }


        /// <summary>把最近的日志拷进列表，最新的在前，供游戏内控制台直接显示。</summary>
        /// <param name="buffer">接收结果的列表；会先清空，null 时直接返回。</param>
        public void CopyRecent(List<LogEntry> buffer)
        {
            // 功能：按新的在前把环形缓冲里的条目拷进调用方的列表，控制台从上往下显示不用再转。
            // 入参：buffer 接收列表，非 null 时先清空再填；null 时静默返回
            // 返回：无
            // 步骤：
            //   1) buffer 为 null 直接返回：查询类接口不值得为 null 抛异常
            //   2) 先 Clear，避免把上一次的内容续在后面
            //   3) 从最新往回数：m_ringStart 是最旧一条，最新一条在 start + count - 1 处，用 % 长度绕回
            // 上游：游戏内控制台面板、EditMode 测试
            // 下游：m_ring 的读取
            // 注意：拷出的是 LogEntry 值类型副本，之后 ClearRecent 不影响已经拷走的内容。
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

        /// <summary>清空环形缓冲，不清累计统计与 Sink 链。</summary>
        public void ClearRecent()
        {
            // 功能：把环形缓冲整体清空，让控制台从零开始；累计计数与输出目标不动。
            // 入参：无
            // 返回：无
            // 步骤：先 Array.Clear 抹掉引用（LogEntry 里含 string，帮 GC），再把起点与条数归零
            // 上游：控制台面板的清屏按钮、测试用例之间隔离、OnDispose
            // 下游：CopyRecent / PushToRing
            // 注意：不清 TotalCount 与 DroppedBy 计数，要区分清屏与重置统计。
            Array.Clear(m_ring, 0, m_ring.Length);
            m_ringStart = 0;
            m_ringCount = 0;
        }


        /// <summary>构造：建环形缓冲并把所有频道默认打开。</summary>
        public LogSystem()
        {
            // 功能：建环形缓冲、默认全频道打开；公开只为满足基类 where T : new() 的约束。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 按 DefaultRingCapacity 分配定长数组，之后不再扩容
            //   2) 逐个把频道开关置 true，默认不屏蔽任何来源
            // 上游：Singleton<LogSystem>.Instance 第一次访问时 new
            // 下游：无
            // 注意：必须是 public —— 基类的 where T : new() 要求 public 无参构造；外部直接 new 会被 Singleton 的构造当场拒绝。
            m_ring = new LogEntry[DefaultRingCapacity];

            for (int i = 0; i < m_channelEnabled.Length; i++)
            {
                m_channelEnabled[i] = true;
            }
        }

        private void PushToRing(in LogEntry entry)
        {
            // 功能：把一条压进环形缓冲，满了就覆盖最旧的一条并把起点前移。
            // 入参：entry 日志条目，按 in 传入不复制
            // 返回：无
            // 步骤：
            //   1) 未满：写在 (start + count) % 长度 处，再把 count 加一
            //   2) 已满：直接覆盖 start 位置（最旧），start 前移一格，count 保持满值
            // 上游：Write
            // 下游：CopyRecent 读取
            // 注意：满了是静默覆盖，不做溢出条数提示；TotalCount 与 RingCount 的差值能看出来。
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

        private static int ChannelIndex(LogChannel channel)
        {
            // 功能：把频道枚举转成数组下标，并挡住越界值。
            // 入参：channel 频道
            // 返回：0..ChannelCount-1 的下标
            // 步骤：
            //   1) 先转 int
            //   2) 小于 0 或大于等于 ChannelCount 就抛 ArgumentOutOfRangeException，把非法值带进异常
            //   3) 合法值原样返回
            // 上游：IsEnabled / SetChannelEnabled / IsChannelEnabled / Write
            // 下游：无
            int index = (int)channel;

            if (index < 0 || index >= ChannelCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(channel), channel, "[LogSystem] 未知的日志频道。");
            }

            return index;
        }

        protected override void OnInit()
        {
            // 功能：播放模式下自动挂上控制台 Sink，让日志出现在 Unity Console。
            // 入参：无
            // 返回：无
            // 步骤：只在 Application.isPlaying 时 AddSink 一个 UnityConsoleLogSink
            // 上游：Singleton<LogSystem>.Instance 第一次创建时调用
            // 下游：AddSink(new UnityConsoleLogSink())
            // 注意：EditMode 下不能挂 —— 测试自己写进去的 Error 会变成未预期的日志，把用例判失败。
            if (Application.isPlaying)
            {
                AddSink(new UnityConsoleLogSink());
            }
        }

        protected override void OnDispose()
        {
            // 功能：销毁时先 Flush 所有 Sink，再 Dispose 实现了 IDisposable 的目标，最后把统计与开关复位。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 先 Flush：把各 Sink 缓冲里的最后几条落盘
            //   2) 遍历 m_sinks，能转成 IDisposable 的就 Dispose，文件句柄在这里关
            //   3) 清空 Sink 链与环形缓冲
            //   4) 级别复位 Info、三个计数归零、频道全开，回到刚构造的状态
            // 上游：Singleton.DisposeInstance（编辑器退出、测试收尾）
            // 下游：各 ILogSink.Flush / Dispose、ClearRecent
            // 注意：先 Flush 再 Dispose，给 Sink 最后一次刷缓冲的机会；m_sinks 清空后旧目标不再收到日志。
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
