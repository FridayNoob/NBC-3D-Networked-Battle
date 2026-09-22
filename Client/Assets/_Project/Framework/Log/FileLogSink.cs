// ============================================================================
//  NBC.Framework.Log · 文件落盘
//  对应需求：FW-M11（文件落盘）
//
//  ---------------------------------------------------------------------------
//  滚动（rotation）怎么做
//  ---------------------------------------------------------------------------
//  日志文件不能无限长 —— 一局战斗刷几十 MB，跑一天就把磁盘写满。
//  所以超过 `maxFileBytes` 就**换一个文件**：
//
//      nbc.log  →  nbc.1.log  →  nbc.2.log  →  （最旧的被删掉）
//
//  保留 `maxFileCount` 个，超出的删最旧的。这样"最近一段时间的日志"永远在，
//  而占用的磁盘是有上限的。
//
//  ---------------------------------------------------------------------------
//  三个刻意的选择
//  ---------------------------------------------------------------------------
//  ① **打开时带 `FileShare.Read`** —— 允许别的进程（比如记事本、tail 工具）
//     在游戏运行时**读**这个文件。不加的话"边跑边看日志"会失败。
//  ② **UTF-8 无 BOM** —— 和本项目所有文本文件保持一致（有 BOM 时某些工具会显示乱码）。
//  ③ **同步写** —— 简单、可测、崩溃前不会丢。
//     ⚠️ 代价：每条日志都进一次系统调用，热路径上会很慢。
//        所以**不该在每帧刷日志**；真需要时再考虑"缓冲 + 定时 flush"（属于 M5 优化）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 滚动的时机：**在写之前检查**，不是写之后
//  ---------------------------------------------------------------------------
//  写之后检查有个别扭的后果：如果最后一次写刚好触发滚动，当前文件会被改名归档，
//  而**不会再有新的当前文件** —— 那一刻 tail 日志的人会看到"文件突然不见了"。
//  放在写之前，"**当前文件永远存在**"就成了一条简单的不变式。
//  （这是首跑测试抓出来的，见 `Write` 的注释。）
// ============================================================================

using System;
using System.IO;
using System.Text;

namespace NBC.Framework.Log
{
    /// <summary>
    /// 把日志写到文件，超长自动滚动。
    /// </summary>
    public sealed class FileLogSink : ILogSink, IDisposable
    {
        /// <summary>默认单文件上限：4 MB。</summary>
        public const long DefaultMaxFileBytes = 4L * 1024L * 1024L;

        /// <summary>默认保留文件数。</summary>
        public const int DefaultMaxFileCount = 3;

        private readonly string m_directory;
        private readonly string m_fileName;
        private readonly long m_maxFileBytes;
        private readonly int m_maxFileCount;
        private readonly Encoding m_encoding = new UTF8Encoding(false);

        private StreamWriter m_writer;

        /// <summary>
        /// **自己记的**已写入字节数。
        /// <para>
        /// ⚠️ 为什么不用 `m_writer.BaseStream.Length`：`StreamWriter` 是**带缓冲**的，
        /// 写进去的内容要等缓冲满（默认 1 KB）才落到流上 ——
        /// 也就是说 `BaseStream.Length` **落后于**实际写入量。
        /// </para>
        /// <para>
        /// 这个坑是测试暴露出来的，而且暴露的方式很典型：
        /// **第一版实现（写完再检查）其实也不可靠，只是恰好走运** ——
        /// 20 条日志刚好跨过一次缓冲 flush，于是它"看起来能工作"。
        /// 改成"写之前检查"之后侥幸没了，问题才现形（`RotationCount` 一直是 0）。
        /// </para>
        /// </summary>
        private long m_writtenBytes;

        /// <summary>构造（**不立刻建文件**，第一条日志时才建）。</summary>
        /// <param name="directory">目录（不存在会自动创建）。</param>
        /// <param name="fileName">文件名，例如 `nbc.log`。</param>
        /// <param name="maxFileBytes">单文件上限；小于等于 0 表示用默认值。</param>
        /// <param name="maxFileCount">保留文件数（含当前文件）；小于 1 视为 1。</param>
        public FileLogSink(string directory, string fileName = "nbc.log",
                           long maxFileBytes = DefaultMaxFileBytes,
                           int maxFileCount = DefaultMaxFileCount)
        {
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException("[FileLogSink] 目录不能为空。", nameof(directory));
            }

            if (string.IsNullOrEmpty(fileName))
            {
                throw new ArgumentException("[FileLogSink] 文件名不能为空。", nameof(fileName));
            }

            m_directory = directory;
            m_fileName = fileName;
            m_maxFileBytes = maxFileBytes > 0 ? maxFileBytes : DefaultMaxFileBytes;
            m_maxFileCount = maxFileCount >= 1 ? maxFileCount : 1;
        }

        /// <summary>当前正在写的文件路径。</summary>
        public string CurrentFilePath
        {
            get { return PathForIndex(0); }
        }

        /// <summary>已经滚动过几次。</summary>
        public int RotationCount { get; private set; }

        /// <summary>当前有没有打开文件。</summary>
        public bool IsOpen
        {
            get { return m_writer != null; }
        }

        /// <summary>写一条。</summary>
        /// <param name="entry">日志条目。</param>
        public void Write(in LogEntry entry)
        {
            EnsureOpen();

            // ⚠️ **在写之前**检查是否该滚动，而不是写之后。
            //
            //    写之后检查有个很别扭的后果：如果**最后一次写**刚好触发滚动，
            //    当前文件就被改名归档了、而不会再有新的当前文件 ——
            //    那一刻正在 tail 日志的人会看到"文件突然不见了"。
            //
            //    放在写之前，"**当前文件永远存在**"就成了一条简单的不变式。
            //    （这个顺序问题是首跑测试 `Rotation_HappensWhenFileGrowsTooLarge` 抓出来的：
            //      它断言滚动之后 `nbc.log` 仍然在，而当时的实现让它不在。）
            //
            // ⚠️ 判断用的是**自己记的字节数**，不是 `BaseStream.Length` ——
            //    后者被 `StreamWriter` 的缓冲拖着走，见 `m_writtenBytes` 的说明。
            if (m_writtenBytes >= m_maxFileBytes)
            {
                Rotate();
                EnsureOpen();
            }

            string line = entry.ToString();

            m_writer.WriteLine(line);
            m_writtenBytes += m_encoding.GetByteCount(line) + NewlineByteCount;
        }

        /// <summary>刷缓冲。</summary>
        public void Flush()
        {
            if (m_writer != null)
            {
                m_writer.Flush();
            }
        }

        /// <summary>关闭文件。</summary>
        public void Dispose()
        {
            Close();
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>确保文件已打开。</summary>
        private void EnsureOpen()
        {
            if (m_writer != null)
            {
                return;
            }

            if (!Directory.Exists(m_directory))
            {
                Directory.CreateDirectory(m_directory);
            }

            // FileShare.Read：允许别的进程在游戏运行时读它（"边跑边看日志"）
            FileStream stream = new FileStream(
                CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);

            // UTF-8 **不带 BOM**：和本项目所有文本文件一致
            m_writer = new StreamWriter(stream, m_encoding);

            // ⚠️ 从**已有文件的真实长度**起步。
            //    否则"追加到一个本来就很大的日志文件"会一直不滚动 ——
            //    因为自己记的计数从 0 开始，永远追不上上限。
            m_writtenBytes = stream.Length;
        }

        /// <summary>关闭文件。</summary>
        private void Close()
        {
            if (m_writer == null)
            {
                return;
            }

            m_writer.Flush();
            m_writer.Dispose();
            m_writer = null;
        }

        /// <summary>滚动：当前文件往后挪一位，最旧的删掉。</summary>
        private void Rotate()
        {
            Close();

            // 从最旧的一端开始删，然后逐个往后挪
            string oldest = PathForIndex(m_maxFileCount - 1);

            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (int i = m_maxFileCount - 2; i >= 0; i--)
            {
                string from = PathForIndex(i);
                string to = PathForIndex(i + 1);

                if (File.Exists(from))
                {
                    if (File.Exists(to))
                    {
                        File.Delete(to);
                    }

                    File.Move(from, to);
                }
            }

            // ⚠️ 计数归零：新文件从 0 开始，否则下一次写会立刻又触发滚动（无限滚动）
            m_writtenBytes = 0;

            RotationCount++;
        }

        /// <summary>换行符占几个字节（`WriteLine` 用的是平台换行）。</summary>
        private static int NewlineByteCount
        {
            get { return Environment.NewLine.Length; }
        }

        /// <summary>第 index 个文件的路径（0 是当前文件）。</summary>
        /// <param name="index">序号。</param>
        /// <returns>路径。</returns>
        private string PathForIndex(int index)
        {
            string path = m_directory;

            if (index <= 0)
            {
                return Path.Combine(path, m_fileName);
            }

            string name = Path.GetFileNameWithoutExtension(m_fileName);
            string extension = Path.GetExtension(m_fileName);

            return Path.Combine(path, name + "." + index + extension);
        }
    }
}
