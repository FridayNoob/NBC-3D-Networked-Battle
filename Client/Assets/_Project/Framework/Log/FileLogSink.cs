// 这个文件里有：
//   FileLogSink（类）：把日志按行同步追加到文件，超过单文件上限就滚动归档，并按保留份数删掉最旧的

using System;
using System.IO;
using System.Text;

namespace NBC.Framework.Log
{
    /// <summary>把日志按行同步追加到文件；单文件超过上限就滚动归档，并按保留份数删掉最旧的。</summary>
    // 主要功能：把日志逐行追加到 nbc.log；单文件超过上限时滚动成 nbc.1.log、nbc.2.log，超出保留份数就删最旧的。
    // 主要数据成员：m_maxFileBytes（单文件上限，字节）、m_maxFileCount（保留份数，含当前文件）、m_writtenBytes（自己记的已写入字节数，字节）、m_writer（当前文件流，null 表示没打开）。
    // 主要外部方法：Write（写一条）、Flush（刷缓冲）、Dispose（关文件，之后还能写）、CurrentFilePath / RotationCount / IsOpen（只读状态）。
    // 谁在用它：目前只有 EditMode 的 FileLogSinkTests 直接构造；对外是 NBC.Framework 的公开类型，供 Game / Boot 经 LogSystem.AddSink 挂进 Sink 链（LogSystem 目前只自动挂 UnityConsoleLogSink）。
    // 注意：没有加锁，只能由一个写入者使用（现在只有主线程写日志）；多线程写要各自一个实例或外部加锁。
    // 注意：同步写、没有后台线程，缓冲满 1 KB 就落一次盘；高频写仍然很慢，不该每帧刷日志，真要高频记录得改成更大的缓冲 + 定时 flush。
    public sealed class FileLogSink : ILogSink, IDisposable
    {
        /// <summary>默认单文件上限：4 MB。</summary>
        // 单位字节：4 × 1024 × 1024 = 4194304，写到这个量级就滚动
        public const long DefaultMaxFileBytes = 4L * 1024L * 1024L;

        /// <summary>默认保留文件数（含当前文件）：3 个。</summary>
        // 含当前文件共 3 份（nbc.log / nbc.1.log / nbc.2.log），再旧的在滚动时删掉
        public const int DefaultMaxFileCount = 3;

        private readonly string m_directory;       // 日志目录路径；为空在构造时就抛异常
        private readonly string m_fileName;        // 当前日志文件名（默认 nbc.log）；滚动时在扩展名前插序号
        private readonly long m_maxFileBytes;      // 单文件上限，字节；构造时把 <= 0 换成 DefaultMaxFileBytes
        private readonly int m_maxFileCount;       // 保留文件数（含当前文件），>= 1；构造时把 < 1 钳成 1
        private readonly Encoding m_encoding = new UTF8Encoding(false);  // 落盘编码：UTF-8 且不写 BOM

        private StreamWriter m_writer;             // 当前打开的文件流；null 表示还没打开（懒创建，Close 后也回到 null）

        // 自己记的已写入字节数，字节；滚动判断只看它 —— StreamWriter 带缓冲，BaseStream.Length 会落后（见 Write 与 EnsureOpen 的注意）
        private long m_writtenBytes;

        /// <summary>构造；不立刻建目录与文件，第一条日志写时才建。</summary>
        /// <param name="directory">日志目录，不存在时在首次写入前创建。</param>
        /// <param name="fileName">文件名，默认 nbc.log；滚动时在扩展名前插 .1 / .2。</param>
        /// <param name="maxFileBytes">单文件上限（字节）；<= 0 时改用 DefaultMaxFileBytes。</param>
        /// <param name="maxFileCount">保留文件数（含当前文件）；< 1 时按 1 处理。</param>
        // 功能：只记下配置，目录与文件都推迟到第一条日志时再建，免得没人写日志也留一堆空文件。
        // 入参：directory 日志目录（null 或空串抛 ArgumentException）；fileName 文件名（同上）；maxFileBytes 单文件上限，字节（<= 0 用默认 4 MB）；maxFileCount 保留份数（< 1 钳成 1）
        // 返回：无
        // 步骤：
        //   1) 先校验目录与文件名非空 —— 空值拼不出路径，早抛比写到一半失败好查
        //   2) 再存配置：上限 <= 0 换默认值，份数 < 1 钳成 1，保证后面按份数减 1 取最旧文件时不会落到当前文件上
        // 上游：EditMode 的 FileLogSinkTests（临时目录）
        // 下游：无
        // 注意：构造不碰文件系统，目录建不出来、没权限，都要到第一条 Write 才暴露。
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

        /// <summary>当前正在写的文件路径（序号 0）。</summary>
        // 功能：给出当前文件的完整路径 —— 滚动之后它仍指向 nbc.log，旧内容已改名到 .1。
        // 入参：无
        // 返回：目录与文件名拼出的路径；文件可能还不存在（懒创建）
        // 步骤：
        //   1) 转给 PathForIndex(0)，让当前文件与归档文件共用同一套命名规则
        // 上游：EnsureOpen 打开文件时取它；测试断言文件位置
        // 下游：PathForIndex
        public string CurrentFilePath
        {
            get { return PathForIndex(0); }
        }

        /// <summary>已经滚动过几次（本实例累计，含单文件模式下的清空重开）。</summary>
        // 功能：记本实例触发过多少次滚动，用来判断滚动逻辑到底跑没跑。
        // 入参：无
        // 返回：滚动次数，0 表示还没滚动过；只增不减，Dispose 后仍保留
        // 步骤：
        //   1) 直接返回自动属性存的值 —— 只在 Rotate 末尾自增
        // 上游：FileLogSinkTests 断言「应当滚动过」
        // 下游：无
        // 注意：它只是本实例的计数，不等于磁盘上现有几个文件；Dispose 重开后计数也不清零。
        public int RotationCount { get; private set; }

        /// <summary>当前有没有打开文件（没打开时下一条 Write 会建）。</summary>
        // 功能：回答「现在是不是占着文件句柄」；Dispose 后为 false，之后再写会重新打开。
        // 入参：无
        // 返回：m_writer 非 null 返回 true；构造后、Close / Dispose 后返回 false
        // 步骤：
        //   1) 只看 m_writer 是否为 null —— 这是句柄在不在的唯一依据
        // 上游：测试断言（Dispose 之后必须为 false，否则目录删不掉）
        // 下游：无
        public bool IsOpen
        {
            get { return m_writer != null; }
        }

        /// <summary>写一条日志；写之前先判断是否该滚动。</summary>
        // 功能：确保文件已打开，在写之前判断该不该滚动，再把条目拼成一行追加并累加字节数。
        // 入参：entry 日志条目，只读引用传入
        // 返回：无
        // 步骤：
        //   1) EnsureOpen 建目录、开文件 —— 第一条日志才真正落盘
        //   2) 写之前先看自己记的字节数够没够上限，够了就 Rotate 并重新打开（顺序不能反，见注意）
        //   3) entry.ToString() 成一行，用 WriteLine 追加
        //   4) 按这一行的 UTF-8 字节数加换行符长度累加计数，供下一次判断
        // 上游：LogSystem.Write 遍历 Sink 时调用（经 ILogSink）；测试直接调
        // 下游：EnsureOpen / Rotate
        // 注意：滚动必须在写之前检查 —— 放在写之后，最后一次写触发滚动会把当前文件改名归档、不留新的当前文件，正在 tail 的人会以为文件不见了。
        // 注意：判断必须用自己记的字节数 —— StreamWriter 的缓冲让 BaseStream.Length 落后，拿它判断会长时间不滚动。
        public void Write(in LogEntry entry)
        {
            EnsureOpen();

            if (m_writtenBytes >= m_maxFileBytes)
            {
                Rotate();
                EnsureOpen();
            }

            string line = entry.ToString();

            m_writer.WriteLine(line);
            m_writtenBytes += m_encoding.GetByteCount(line) + NewlineByteCount;
        }

        /// <summary>把 StreamWriter 的缓冲刷到磁盘；没打开时什么也不做。</summary>
        // 功能：把缓冲里最后几条真正写到磁盘，退出前要调，否则最后几条会丢。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没打开就直接返回 —— 没有缓冲可刷，也不为刷缓冲去建文件
        //   2) 打开了就 Flush，不关句柄，后面还要继续写
        // 上游：LogSystem.Flush 遍历 Sink 时调用；测试
        // 下游：StreamWriter.Flush
        public void Flush()
        {
            if (m_writer != null)
            {
                m_writer.Flush();
            }
        }

        /// <summary>关闭文件、释放句柄；之后仍可继续 Write（会重新打开）。</summary>
        // 功能：把缓冲刷掉并关掉文件句柄，让 Windows 上能删 / 改名这个目录。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 转给 Close：先 Flush 再 Dispose StreamWriter，最后把 m_writer 置 null
        // 上游：using 块结束；LogSystem.OnDispose 对实现 IDisposable 的 Sink 调
        // 下游：Close
        // 注意：Dispose 不是终态 —— 没有 disposed 标志，之后再写会重新打开文件。
        public void Dispose()
        {
            Close();
        }


        // 功能：保证文件已打开；没打开就建目录、按追加模式打开、并从已有文件的真实长度接管计数。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已经打开就立刻返回 —— 每条日志都走这里，不能重复开
        //   2) 目录不存在先 CreateDirectory，否则 FileStream 会抛
        //   3) 用 FileMode.Append + FileShare.Read 打开：追加写，同时允许别的进程读（见注意）
        //   4) 套上 UTF-8 无 BOM 的 StreamWriter（见注意）
        //   5) 计数从 stream.Length 起步（见注意）
        // 上游：Write（开头与 Rotate 之后各调一次）
        // 下游：CurrentFilePath / StreamWriter
        // 注意：不加 FileShare.Read 的话，游戏跑着时记事本 / tail 打不开日志，边跑边看就做不了。
        // 注意：UTF-8 必须不带 BOM，有 BOM 某些工具会显示乱码，git diff 里也会多出看不见的字符。
        // 注意：计数起点必须是已有文件的真实长度，从 0 开始的话，追加到本来就很大的文件会永远追不上上限、再也不滚动。
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

            FileStream stream = new FileStream(
                CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);

            m_writer = new StreamWriter(stream, m_encoding);

            m_writtenBytes = stream.Length;
        }

        // 功能：刷缓冲、释放 StreamWriter 并清空引用，让下一次写重新打开。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没打开就返回 —— 幂等，Dispose 与 Rotate 都可以随手调
        //   2) 先 Flush 再 Dispose，保证缓冲里的行不丢
        //   3) 引用置 null，IsOpen 随之变 false
        // 上游：Dispose、Rotate（滚动前必须先放句柄）
        // 下游：无
        // 注意：必须先关再改名 / 删文件 —— Windows 上句柄没放，File.Move / File.Delete 会失败。
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

        // 功能：把当前文件改名为 .1、.1 挪成 .2……删掉最旧的一份，再清空计数等下一次写重开。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先 Close 放掉句柄，否则后面的删除 / 改名在 Windows 上会失败
        //   2) 从最旧的一端（序号 = 保留份数 - 1）开始删，给后面的挪位腾地方
        //   3) 从倒数第二份往 0 倒着挪：先删目标再 Move；正着挪会把自己刚写的那份覆盖掉
        //   4) 计数归零、RotationCount 自增；下一次 Write 会 EnsureOpen 建新的当前文件
        // 上游：Write 判断到超过上限时调用
        // 下游：Close / PathForIndex
        // 注意：保留份数为 1 时删的就是当前文件，效果是清空重开，不留备份。
        // 注意：计数不归零的话，下一次写立刻又满足上限，会无限滚动。
        private void Rotate()
        {
            Close();

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

            m_writtenBytes = 0;

            RotationCount++;
        }

        // 功能：给出换行符占的字节数，用在滚动计数上。
        // 入参：无
        // 返回：Environment.NewLine 的长度；Windows 上 2（CRLF），类 Unix 上 1（LF）
        // 步骤：
        //   1) 直接取 Environment.NewLine.Length —— WriteLine 写的就是它
        // 上游：Write 累加 m_writtenBytes
        // 下游：无
        // 注意：换行符在 UTF-8 里都是单字节，所以字符长度可以直接当字节数用。
        private static int NewlineByteCount
        {
            get { return Environment.NewLine.Length; }
        }

        // 功能：按序号拼路径 —— 0 是当前文件，n 是在扩展名前插了 .n 的归档文件。
        // 入参：index 序号，>= 0；负数按 0 处理
        // 返回：目录与文件名拼出的完整路径；对应文件不保证存在
        // 步骤：
        //   1) 以目录打底；0 及负数直接拼原文件名 —— 当前文件不带序号
        //   2) 否则拆出无扩展名的主名与扩展名，拼成「主名.序号扩展名」，如 nbc.1.log
        // 上游：CurrentFilePath、EnsureOpen、Rotate
        // 下游：Path.Combine / GetFileNameWithoutExtension / GetExtension
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
