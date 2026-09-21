// ============================================================================
//  M1-E2 · LogSystem 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md E2（分级 + 落盘）
//  对应需求：FW-M11
//
//  ---------------------------------------------------------------------------
//  为什么测试里用一个"内存 sink"而不是真写文件
//  ---------------------------------------------------------------------------
//  `LogSystem` 和"记到哪去"是分开的（`ILogSink` 就是那道缝）。
//  所以测**过滤与滚动**的逻辑时，喂一个内存 sink 就够了 ——
//  断言"记了什么"不需要碰文件系统。
//
//  真正落盘的行为由 `FileLogSinkTests` 单独测（那里才需要真文件）。
//  **两件事分开测，各自都确定。**
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Log;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>把日志记在内存里的测试用 sink。</summary>
    internal sealed class MemoryLogSink : ILogSink
    {
        /// <summary>收到的条目（按写入顺序）。</summary>
        public readonly List<LogEntry> Entries = new List<LogEntry>();

        /// <summary>`Flush` 被调用了几次。</summary>
        public int FlushCount;

        /// <summary>写一条。</summary>
        /// <param name="entry">日志条目。</param>
        public void Write(in LogEntry entry)
        {
            Entries.Add(entry);
        }

        /// <summary>刷。</summary>
        public void Flush()
        {
            FlushCount++;
        }
    }

    /// <summary>
    /// E2：日志系统的测试。
    /// </summary>
    public sealed class LogSystemTests
    {
        private LogSystem m_log;
        private MemoryLogSink m_sink;

        /// <summary>重置单例并挂一个内存 sink。</summary>
        [SetUp]
        public void SetUp()
        {
            SingletonRegistry.ResetAll();

            m_log = LogSystem.Instance;
            m_sink = new MemoryLogSink();
            m_log.AddSink(m_sink);
        }

        /// <summary>清理。</summary>
        [TearDown]
        public void TearDown()
        {
            if (LogSystem.HasInstance)
            {
                LogSystem.DisposeInstance();
            }

            SingletonRegistry.ResetAll();
        }

        // ====================================================================
        //  一、分级
        // ====================================================================

        /// <summary>默认只放行 Info 及以上，`Debug` 被丢掉并计数。</summary>
        [Test]
        public void Debug_IsFilteredOutByDefault_AndCounted()
        {
            Assert.AreEqual(LogLevel.Info, m_log.MinLevel);

            m_log.LogDebug(LogChannel.General, "看不见");

            Assert.AreEqual(0, m_sink.Entries.Count);
            Assert.AreEqual(1L, m_log.DroppedByLevelCount);

            m_log.LogInfo(LogChannel.General, "看得见");

            Assert.AreEqual(1, m_sink.Entries.Count);
        }

        /// <summary>调低 `MinLevel` 之后 `Debug` 就出来了。</summary>
        [Test]
        public void LoweringMinLevel_LetsDebugThrough()
        {
            m_log.MinLevel = LogLevel.Debug;
            m_log.LogDebug(LogChannel.General, "现在看得见了");

            Assert.AreEqual(1, m_sink.Entries.Count);
            Assert.AreEqual(LogLevel.Debug, m_sink.Entries[0].Level);
        }

        /// <summary>抬高级别后，低级别全部被挡。</summary>
        [Test]
        public void RaisingMinLevel_BlocksLowerLevels()
        {
            m_log.MinLevel = LogLevel.Error;

            m_log.LogInfo(LogChannel.General, "info");
            m_log.LogWarning(LogChannel.General, "warn");
            m_log.LogError(LogChannel.General, "error");

            Assert.AreEqual(1, m_sink.Entries.Count);
            Assert.AreEqual(LogLevel.Error, m_sink.Entries[0].Level);
            Assert.AreEqual(2L, m_log.DroppedByLevelCount);
        }

        // ====================================================================
        //  二、频道开关（FW-M11 的"网络收发日志开关"）
        // ====================================================================

        /// <summary>
        /// **关掉网络频道之后，网络日志不记，但战斗日志照常** ——
        /// 这正是"级别过滤"做不到的事（两者都是 `Info`）。
        /// </summary>
        [Test]
        public void DisablingNetworkChannel_DoesNotAffectOtherChannels()
        {
            m_log.SetChannelEnabled(LogChannel.Network, false);

            m_log.LogInfo(LogChannel.Network, "收包");
            m_log.LogInfo(LogChannel.Battle, "伤害计算");

            Assert.AreEqual(1, m_sink.Entries.Count);
            Assert.AreEqual(LogChannel.Battle, m_sink.Entries[0].Channel);
            Assert.AreEqual(1L, m_log.DroppedByChannelCount);
            Assert.AreEqual(0L, m_log.DroppedByLevelCount, "这不是级别过滤掉的");
        }

        /// <summary>频道默认全开。</summary>
        [Test]
        public void AllChannelsAreEnabledByDefault()
        {
            foreach (LogChannel channel in Enum.GetValues(typeof(LogChannel)))
            {
                Assert.IsTrue(m_log.IsChannelEnabled(channel), channel + " 应当默认启用");
            }
        }

        /// <summary>重新打开频道之后又能记了。</summary>
        [Test]
        public void ReEnablingChannel_Works()
        {
            m_log.SetChannelEnabled(LogChannel.Network, false);
            m_log.LogInfo(LogChannel.Network, "被挡");
            m_log.SetChannelEnabled(LogChannel.Network, true);
            m_log.LogInfo(LogChannel.Network, "放行");

            Assert.AreEqual(1, m_sink.Entries.Count);
            Assert.AreEqual("放行", m_sink.Entries[0].Message);
        }

        // ====================================================================
        //  三、`IsEnabled` 提前短路（热路径的正确用法）
        // ====================================================================

        /// <summary>
        /// `IsEnabled` 的判断必须和 `Write` 的实际行为**完全一致** ——
        /// 不然"先问再拼字符串"就会拼了却不用，或者该记的没记。
        /// </summary>
        [Test]
        public void IsEnabled_MatchesActualWriteBehavior()
        {
            m_log.SetChannelEnabled(LogChannel.Network, false);
            m_log.MinLevel = LogLevel.Warning;

            LogLevel[] levels = { LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error };
            LogChannel[] channels = { LogChannel.General, LogChannel.Network, LogChannel.Battle };

            for (int i = 0; i < levels.Length; i++)
            {
                for (int j = 0; j < channels.Length; j++)
                {
                    m_sink.Entries.Clear();

                    bool predicted = m_log.IsEnabled(levels[i], channels[j]);
                    m_log.Write(levels[i], channels[j], "x");

                    Assert.AreEqual(predicted, m_sink.Entries.Count == 1,
                        "IsEnabled 与实际行为不一致：" + levels[i] + " / " + channels[j]);
                }
            }
        }

        // ====================================================================
        //  四、滚动日志（战斗中控制台读它）
        // ====================================================================

        /// <summary>`CopyRecent` 给的是**新的在前**。</summary>
        [Test]
        public void CopyRecent_IsNewestFirst()
        {
            m_log.LogInfo(LogChannel.General, "第一条");
            m_log.LogInfo(LogChannel.General, "第二条");
            m_log.LogInfo(LogChannel.General, "第三条");

            List<LogEntry> recent = new List<LogEntry>();
            m_log.CopyRecent(recent);

            Assert.AreEqual(3, recent.Count);
            Assert.AreEqual("第三条", recent[0].Message);
            Assert.AreEqual("第二条", recent[1].Message);
            Assert.AreEqual("第一条", recent[2].Message);
        }

        /// <summary>超过容量后**覆盖最旧的**，总数不再增长。</summary>
        [Test]
        public void RingBuffer_OverwritesOldestWhenFull()
        {
            int capacity = m_log.RingCapacity;

            for (int i = 0; i < capacity + 10; i++)
            {
                m_log.LogInfo(LogChannel.General, "第" + i + "条");
            }

            Assert.AreEqual(capacity, m_log.RingCount, "缓冲不该超过容量");

            List<LogEntry> recent = new List<LogEntry>();
            m_log.CopyRecent(recent);

            Assert.AreEqual("第" + (capacity + 9) + "条", recent[0].Message, "最新的还在");
            Assert.AreEqual("第10条", recent[capacity - 1].Message, "最旧的 10 条已经被覆盖掉了");
        }

        /// <summary>`TotalCount` 记的是**累计**，不受缓冲容量影响。</summary>
        [Test]
        public void TotalCount_CountsEverythingEvenIfOverwritten()
        {
            int capacity = m_log.RingCapacity;

            for (int i = 0; i < capacity + 5; i++)
            {
                m_log.LogInfo(LogChannel.General, "x");
            }

            Assert.AreEqual(capacity + 5L, m_log.TotalCount);
            Assert.AreEqual(capacity, m_log.RingCount);
        }

        /// <summary>`ClearRecent` 只清缓冲，不动统计。</summary>
        [Test]
        public void ClearRecent_KeepsStatistics()
        {
            m_log.LogInfo(LogChannel.General, "x");
            m_log.ClearRecent();

            Assert.AreEqual(0, m_log.RingCount);
            Assert.AreEqual(1L, m_log.TotalCount, "统计不该被清掉");
        }

        /// <summary>记日志**顺序**要保持（环形缓冲最容易写错的就是这里）。</summary>
        [Test]
        public void RingBuffer_PreservesOrderAcrossWraparound()
        {
            int capacity = m_log.RingCapacity;

            // 填满再绕两圈，确保起点的取模逻辑正确
            for (int i = 0; i < capacity * 2 + 3; i++)
            {
                m_log.LogInfo(LogChannel.General, "L" + i);
            }

            List<LogEntry> recent = new List<LogEntry>();
            m_log.CopyRecent(recent);

            for (int i = 0; i < recent.Count; i++)
            {
                int expected = capacity * 2 + 2 - i;

                Assert.AreEqual("L" + expected, recent[i].Message,
                    "第 " + i + " 条顺序不对");
            }
        }

        // ====================================================================
        //  五、输出目标
        // ====================================================================

        /// <summary>多个 sink 都收到，且顺序一致。</summary>
        [Test]
        public void MultipleSinks_AllReceive()
        {
            MemoryLogSink second = new MemoryLogSink();
            m_log.AddSink(second);

            m_log.LogInfo(LogChannel.General, "hello");

            Assert.AreEqual(1, m_sink.Entries.Count);
            Assert.AreEqual(1, second.Entries.Count);
            Assert.AreEqual(2, m_log.SinkCount);
        }

        /// <summary>重复挂同一个 sink 不会挂两遍。</summary>
        [Test]
        public void AddingSameSinkTwice_IsIgnored()
        {
            m_log.AddSink(m_sink);

            Assert.AreEqual(1, m_log.SinkCount);
        }

        /// <summary>摘掉之后就不再收到。</summary>
        [Test]
        public void RemoveSink_StopsDelivery()
        {
            Assert.IsTrue(m_log.RemoveSink(m_sink));

            m_log.LogInfo(LogChannel.General, "hello");

            Assert.AreEqual(0, m_sink.Entries.Count);
            Assert.IsFalse(m_log.RemoveSink(m_sink), "已经摘掉了，再摘一次返回 false");
        }

        /// <summary>挂 null 当场报错。</summary>
        [Test]
        public void AddSink_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => m_log.AddSink(null));
        }

        /// <summary>`Flush` 会传给所有 sink。</summary>
        [Test]
        public void Flush_ReachesAllSinks()
        {
            m_log.Flush();

            Assert.GreaterOrEqual(m_sink.FlushCount, 1);
        }

        // ====================================================================
        //  六、EditMode 下不自动挂控制台 sink
        // ====================================================================

        /// <summary>
        /// **EditMode 下不该自动挂控制台输出。**
        /// <para>
        /// 否则测试用例自己写进去的 `Error` 会变成"未预期的日志"把用例判失败 ——
        /// 那是测试环境被自己的输出污染。所以只挂内存 sink，数量必须是 1。
        /// </para>
        /// </summary>
        [Test]
        public void InEditMode_NoConsoleSinkIsAutoAttached()
        {
            Assert.AreEqual(1, m_log.SinkCount,
                "EditMode 下应当只有测试自己挂的那一个 sink（控制台 sink 只在播放模式挂）");
        }

        // ====================================================================
        //  七、参数校验
        // ====================================================================

        /// <summary>正文为 null 是调用方的 bug，当场报出来。</summary>
        [Test]
        public void Write_NullMessage_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => m_log.LogInfo(LogChannel.General, null));
        }

        /// <summary>非法频道当场抛。</summary>
        [Test]
        public void Write_InvalidChannel_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => m_log.Write(LogLevel.Info, (LogChannel)99, "x"));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => m_log.SetChannelEnabled((LogChannel)99, false));
        }

        /// <summary>空字符串是合法的（想记空行就传它）。</summary>
        [Test]
        public void Write_EmptyMessage_IsAllowed()
        {
            Assert.DoesNotThrow(() => m_log.LogInfo(LogChannel.General, string.Empty));
            Assert.AreEqual(1, m_sink.Entries.Count);
        }

        /// <summary>外部不许直接 `new` 单例（由 A1 的基类守卫拦下）。</summary>
        [Test]
        public void LogSystem_CannotBeConstructedDirectly()
        {
            Assert.Throws<InvalidOperationException>(() => { LogSystem bad = new LogSystem(); });
        }

        // ====================================================================
        //  八、条目的可读性
        // ====================================================================

        /// <summary>`LogEntry.ToString` 带上时间、级别、频道。</summary>
        [Test]
        public void LogEntry_ToString_ContainsLevelAndChannel()
        {
            m_log.LogWarning(LogChannel.Asset, "包没找到");

            string text = m_sink.Entries[0].ToString();

            StringAssert.Contains("Warning", text);
            StringAssert.Contains("Asset", text);
            StringAssert.Contains("包没找到", text);
        }
    }
}
