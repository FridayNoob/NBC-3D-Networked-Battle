// ============================================================================
//  M1-E2 · FileLogSink 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md E2（分级 + **落盘**）
//  对应需求：FW-M11
//
//  ---------------------------------------------------------------------------
//  这里为什么必须碰真实文件系统
//  ---------------------------------------------------------------------------
//  "落盘"这个行为的全部意义就在文件系统上 —— 用假实现测等于什么都没测。
//  所以这一组用**真实的临时目录**，写完再读回来断言内容。
//
//  ⚠️ 代价：这些用例比纯逻辑的慢、而且依赖文件系统权限。
//     所以它们和 `LogSystemTests`（纯内存）**分开两个文件** ——
//     哪边红了能立刻看出"是过滤逻辑错了"还是"是写文件错了"。
// ============================================================================

using System;
using System.IO;
using System.Text;
using NBC.Framework.Log;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// E2：文件落盘的测试。
    /// </summary>
    public sealed class FileLogSinkTests
    {
        private string m_dir;

        /// <summary>每个用例一个全新的临时目录（互不干扰）。</summary>
        [SetUp]
        public void SetUp()
        {
            m_dir = Path.Combine(Path.GetTempPath(), "nbc-log-" + Guid.NewGuid().ToString("N"));
        }

        /// <summary>删掉临时目录。</summary>
        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(m_dir))
            {
                Directory.Delete(m_dir, true);
            }
        }

        // ====================================================================
        //  一、基本落盘
        // ====================================================================

        /// <summary>目录不存在会自动建，第一条日志就落盘。</summary>
        [Test]
        public void Write_CreatesDirectoryAndFile()
        {
            using (FileLogSink sink = new FileLogSink(m_dir))
            {
                Assert.IsFalse(Directory.Exists(m_dir), "还没写之前不该建目录");
                Assert.IsFalse(sink.IsOpen, "还没写之前不该开文件");

                sink.Write(new LogEntry(LogLevel.Info, LogChannel.General, "第一条", DateTime.Now));

                Assert.IsTrue(sink.IsOpen);
            }

            Assert.IsTrue(File.Exists(Path.Combine(m_dir, "nbc.log")));
        }

        /// <summary>内容确实写进去了（读回来断言）。</summary>
        [Test]
        public void Write_ContentIsReadable()
        {
            using (FileLogSink sink = new FileLogSink(m_dir))
            {
                sink.Write(new LogEntry(LogLevel.Warning, LogChannel.Network, "收包失败", DateTime.Now));
                sink.Flush();
            }

            string text = File.ReadAllText(Path.Combine(m_dir, "nbc.log"));

            StringAssert.Contains("Warning", text);
            StringAssert.Contains("Network", text);
            StringAssert.Contains("收包失败", text);
        }

        /// <summary>多条日志按顺序落盘。</summary>
        [Test]
        public void Write_MultipleEntriesPreserveOrder()
        {
            using (FileLogSink sink = new FileLogSink(m_dir))
            {
                for (int i = 0; i < 5; i++)
                {
                    sink.Write(new LogEntry(LogLevel.Info, LogChannel.General, "L" + i, DateTime.Now));
                }
            }

            string[] lines = File.ReadAllLines(Path.Combine(m_dir, "nbc.log"));

            Assert.AreEqual(5, lines.Length);

            for (int i = 0; i < 5; i++)
            {
                StringAssert.Contains("L" + i, lines[i]);
            }
        }

        /// <summary>
        /// 文件必须是 **UTF-8 无 BOM**（和本项目所有文本文件一致）。
        /// <para>有 BOM 的话某些工具会显示乱码，git diff 也会多出看不见的字符。</para>
        /// </summary>
        [Test]
        public void File_IsUtf8WithoutBom()
        {
            using (FileLogSink sink = new FileLogSink(m_dir))
            {
                sink.Write(new LogEntry(LogLevel.Info, LogChannel.General, "中文也要正常", DateTime.Now));
            }

            byte[] bytes = File.ReadAllBytes(Path.Combine(m_dir, "nbc.log"));

            Assert.Greater(bytes.Length, 3);
            Assert.IsFalse(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "不该有 UTF-8 BOM");
        }

        // ====================================================================
        //  二、滚动
        // ====================================================================

        /// <summary>
        /// **当前文件必须一直存在** —— 即使最后一次写刚好触发了滚动。
        /// <para>
        /// ⚠️ 这条是首跑失败后补上的：原实现在**写之后**检查长度，
        /// 于是"最后一次写触发滚动"会把当前文件改名归档、却不留新的当前文件 ——
        /// 那一刻正在 tail 日志的人会看到"文件突然不见了"。
        /// 改成**写之前**检查之后，"当前文件永远存在"才成立。
        /// </para>
        /// </summary>
        [Test]
        public void CurrentFile_AlwaysExists_EvenWhenLastWriteTriggersRotation()
        {
            using (FileLogSink sink = new FileLogSink(m_dir, "nbc.log", maxFileBytes: 100, maxFileCount: 3))
            {
                for (int i = 0; i < 12; i++)
                {
                    sink.Write(new LogEntry(LogLevel.Info, LogChannel.General,
                        "这条够长足以触发滚动 " + i, DateTime.Now));
                }

                Assert.Greater(sink.RotationCount, 0, "应当滚动过（否则这条没测到东西）");
                Assert.IsTrue(File.Exists(Path.Combine(m_dir, "nbc.log")),
                    "当前文件必须一直存在，不能因为滚动而消失");
            }
        }

        /// <summary>超过单文件上限就滚动，并把旧文件改名保留。</summary>
        [Test]
        public void Rotation_HappensWhenFileGrowsTooLarge()
        {
            // 上限设得很小，几条就超
            using (FileLogSink sink = new FileLogSink(m_dir, "nbc.log", maxFileBytes: 200, maxFileCount: 3))
            {
                for (int i = 0; i < 20; i++)
                {
                    sink.Write(new LogEntry(LogLevel.Info, LogChannel.General,
                        "这是一条比较长的日志用来尽快触发滚动 " + i, DateTime.Now));
                }

                Assert.Greater(sink.RotationCount, 0, "应当滚动过");
            }

            Assert.IsTrue(File.Exists(Path.Combine(m_dir, "nbc.log")));
            Assert.IsTrue(File.Exists(Path.Combine(m_dir, "nbc.1.log")), "应当有滚动出来的 .1 文件");
        }

        /// <summary>保留文件数有上限：最旧的被删掉。</summary>
        [Test]
        public void Rotation_KeepsAtMostMaxFileCount()
        {
            using (FileLogSink sink = new FileLogSink(m_dir, "nbc.log", maxFileBytes: 150, maxFileCount: 2))
            {
                for (int i = 0; i < 40; i++)
                {
                    sink.Write(new LogEntry(LogLevel.Info, LogChannel.General,
                        "填满它填满它填满它填满它 " + i, DateTime.Now));
                }
            }

            string[] files = Directory.GetFiles(m_dir, "nbc*.log");

            Assert.LessOrEqual(files.Length, 2,
                "保留文件数应当是 2，实际有 " + files.Length + " 个：" + string.Join(", ", files));
        }

        /// <summary>`maxFileCount = 1` 时只保留一个文件（直接截断重开）。</summary>
        [Test]
        public void Rotation_WithSingleFile_DoesNotKeepBackups()
        {
            using (FileLogSink sink = new FileLogSink(m_dir, "nbc.log", maxFileBytes: 150, maxFileCount: 1))
            {
                for (int i = 0; i < 40; i++)
                {
                    sink.Write(new LogEntry(LogLevel.Info, LogChannel.General,
                        "填满它填满它填满它填满它 " + i, DateTime.Now));
                }
            }

            string[] files = Directory.GetFiles(m_dir, "*.log");

            Assert.AreEqual(1, files.Length, "只该留下当前那个文件");
        }

        // ====================================================================
        //  三、生命周期
        // ====================================================================

        /// <summary>`Dispose` 之后文件句柄要释放（否则 Windows 上删不掉目录）。</summary>
        [Test]
        public void Dispose_ReleasesFileHandle()
        {
            FileLogSink sink = new FileLogSink(m_dir);
            sink.Write(new LogEntry(LogLevel.Info, LogChannel.General, "x", DateTime.Now));

            sink.Dispose();

            Assert.IsFalse(sink.IsOpen);

            // 能删掉就说明句柄真的放了
            Assert.DoesNotThrow(() => Directory.Delete(m_dir, true));
        }

        /// <summary>释放之后还能继续写（会重新打开）。</summary>
        [Test]
        public void WriteAfterDispose_ReopensFile()
        {
            FileLogSink sink = new FileLogSink(m_dir);

            sink.Write(new LogEntry(LogLevel.Info, LogChannel.General, "第一条", DateTime.Now));
            sink.Dispose();
            sink.Write(new LogEntry(LogLevel.Info, LogChannel.General, "第二条", DateTime.Now));

            Assert.IsTrue(sink.IsOpen);

            sink.Dispose();

            string text = File.ReadAllText(Path.Combine(m_dir, "nbc.log"));

            StringAssert.Contains("第一条", text);
            StringAssert.Contains("第二条", text);
        }

        /// <summary>追加模式：已有的内容不会被覆盖。</summary>
        [Test]
        public void Write_AppendsToExistingFile()
        {
            using (FileLogSink first = new FileLogSink(m_dir))
            {
                first.Write(new LogEntry(LogLevel.Info, LogChannel.General, "上一次运行", DateTime.Now));
            }

            using (FileLogSink second = new FileLogSink(m_dir))
            {
                second.Write(new LogEntry(LogLevel.Info, LogChannel.General, "这一次运行", DateTime.Now));
            }

            string text = File.ReadAllText(Path.Combine(m_dir, "nbc.log"));

            StringAssert.Contains("上一次运行", text);
            StringAssert.Contains("这一次运行", text);
        }

        // ====================================================================
        //  四、参数校验
        // ====================================================================

        /// <summary>目录与文件名不能为空。</summary>
        [Test]
        public void Constructor_RejectsEmptyArguments()
        {
            Assert.Throws<ArgumentException>(() => new FileLogSink(null));
            Assert.Throws<ArgumentException>(() => new FileLogSink(string.Empty));
            Assert.Throws<ArgumentException>(() => new FileLogSink(m_dir, string.Empty));
        }

        /// <summary>接上 `LogSystem` 之后，过滤逻辑照样生效（sink 只管往外倒）。</summary>
        [Test]
        public void WiredIntoLogSystem_RespectsFiltering()
        {
            NBC.Framework.SingletonRegistry.ResetAll();

            LogSystem log = LogSystem.Instance;
            FileLogSink sink = new FileLogSink(m_dir);

            try
            {
                log.AddSink(sink);
                log.LogDebug(LogChannel.General, "debug 应当被级别挡掉");
                log.LogInfo(LogChannel.General, "info 应当落盘");
            }
            finally
            {
                sink.Dispose();
                LogSystem.DisposeInstance();
                NBC.Framework.SingletonRegistry.ResetAll();
            }

            string text = File.ReadAllText(Path.Combine(m_dir, "nbc.log"));

            StringAssert.Contains("info 应当落盘", text);
            StringAssert.DoesNotContain("debug 应当被级别挡掉", text);
        }
    }
}
