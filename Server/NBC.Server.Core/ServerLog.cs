// ============================================================================
//  ServerLog —— 服务端日志入口（控制台 + 可选落盘）
//  项目：3D联网战斗Demo   对应：需求文档 SRV-14（结构化日志、按天分文件）、SRV-01（日志级别）
//
//  ---------------------------------------------------------------------------
//  它复用了客户端的什么（**同一个源码文件，不是抄一份**）
//  ---------------------------------------------------------------------------
//      `NBC.Framework.Log.LogTypes`    —— `LogLevel` / `LogChannel` / `LogEntry`
//      `NBC.Framework.Log.ILogSink`    —— 落盘接缝
//      `NBC.Framework.Log.FileLogSink` —— **带滚动的文件落盘**（263 行）
//
//  这三个文件是**纯 BCL**，所以 `NBC.Server.Core.csproj` 用 `<Compile Include>` 引了它们
//  —— 与 D1「一份源码两边都编」完全同一个套路。见那个 csproj 里的注释（含"为什么不拷一份"）。
//
//  ---------------------------------------------------------------------------
//  它自己只做两件事（都是客户端那一侧**用不了**的）
//  ---------------------------------------------------------------------------
//  ① **控制台 sink**：客户端的 `UnityConsoleLogSink` 用 `UnityEngine.Debug`，服务端没有它。
//     服务端就是 `Console.WriteLine` —— 而且要注意**颜色/编码**：本机控制台是 GBK 代码页，
//     所以 Program.cs 一开头就设了 `Console.OutputEncoding = UTF8`（不然中文全是乱码）。
//
//  ② **按天分文件**（SRV-14 点名要的）：把日期写进**文件名** ——
//         `nbc-server-20260927.log`
//     于是"每一天一个新文件"是**换名字**的自然结果，而不是另一套滚动逻辑。
//     ⚠️ 与 `FileLogSink` 的**大小滚动**互不冲突：同一天内写太大仍会滚成 `.1` / `.2`。
//        两件事分开做，各自都简单；混在一起（"按天 + 按大小 + 保留 N 天"）就是三维修罗场。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么不做"日志缓冲 + 定时刷"
//  ---------------------------------------------------------------------------
//  `FileLogSink` 是**同步写**（它文件头写明了取舍：简单、可测、崩溃前不丢）。
//  服务端的日志量是"每帧几条事件"级别，不是每帧几百条 —— 同步写的代价可以接受。
//  ⇒ **先要"崩溃前不丢"**，将来真到热路径再谈缓冲（那是 M5 的优化）。
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using NBC.Framework.Log;

namespace NBC.Server.Core
{
    /// <summary>
    /// 服务端日志。`Log()` 走这里，于是**控制台与日志文件内容一致**
    /// （不会出现"控制台有、文件里没有"这种排查时最气人的事）。
    /// </summary>
    public sealed class ServerLog : IDisposable
    {
        /// <summary>文件 sink（没开落盘时为 null）。</summary>
        private readonly FileLogSink? m_fileSink;

        /// <summary>最低输出级别（低于它的**直接丢掉**）。</summary>
        private readonly LogLevel m_minLevel;

        /// <summary>要不要写控制台。</summary>
        private readonly bool m_writeConsole;

        /// <summary>Dispose 过没有。</summary>
        private bool m_disposed;

        /// <summary>累计写了几条。</summary>
        private long m_count;

        /// <summary>累计因为级别被丢掉几条。</summary>
        private long m_droppedByLevel;

        /// <summary>
        /// 建一个服务端日志。
        /// </summary>
        /// <param name="minLevel">最低级别（低于它的丢掉）。</param>
        /// <param name="directory">落盘目录（`WriteToFile=false` 时忽略）。</param>
        /// <param name="writeToFile">要不要落盘。</param>
        /// <param name="splitByDay">文件名要不要带日期（SRV-14）。</param>
        /// <param name="writeConsole">要不要写控制台。</param>
        /// <param name="now">取"今天"用的时间（测试可注入；默认 `DateTime.Now`）。</param>
        public ServerLog(LogLevel minLevel, string directory, bool writeToFile, bool splitByDay,
                         bool writeConsole = true, Func<DateTime>? now = null)
        {
            m_minLevel = minLevel;
            m_writeConsole = writeConsole;

            if (writeToFile && !string.IsNullOrWhiteSpace(directory))
            {
                string fileName = BuildFileName(splitByDay, (now ?? (() => DateTime.Now))());
                m_fileSink = new FileLogSink(directory, fileName);
            }
        }

        /// <summary>最低输出级别。</summary>
        public LogLevel MinLevel
        {
            get { return m_minLevel; }
        }

        /// <summary>开了落盘没有。</summary>
        public bool WritesToFile
        {
            get { return m_fileSink != null; }
        }

        /// <summary>当前日志文件路径（没落盘时为 null）。</summary>
        public string? CurrentFilePath
        {
            get { return m_fileSink?.CurrentFilePath; }
        }

        /// <summary>累计写了几条。</summary>
        public long Count
        {
            get { return m_count; }
        }

        /// <summary>累计因为级别被丢掉几条（**不是"错误"，是配置生效的证据**）。</summary>
        public long DroppedByLevel
        {
            get { return m_droppedByLevel; }
        }

        /// <summary>
        /// 写一条。
        /// <para>⚠️ 它**吞掉 IO 异常**：磁盘满/文件被独占**不该让服务端倒**。
        /// 但吞掉之后要**往控制台喊一声**（否则就是"日志悄悄没了"）。</para>
        /// </summary>
        /// <param name="level">级别。</param>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文。</param>
        public void Write(LogLevel level, LogChannel channel, string message)
        {
            if (m_disposed || level < m_minLevel)
            {
                m_droppedByLevel++;
                return;
            }

            m_count++;

            var entry = new LogEntry(level, channel, message, DateTime.Now);

            if (m_writeConsole)
            {
                // ⚠️ 直接 Console.WriteLine：本机控制台是 GBK 代码页，
                //    而 Program.cs 一开头就设了 `Console.OutputEncoding = UTF8`。
                //    这里**不再**自己转编码 —— 一处设置，处处生效。
                Console.WriteLine(entry.ToString());
            }

            if (m_fileSink == null)
            {
                return;
            }

            try
            {
                m_fileSink.Write(entry);

                // ⚠️ **每条都 Flush** —— 这不是"求稳"，是**必须**：
                //    `FileLogSink` 内部是 `StreamWriter`（**带缓冲**，默认 1KB），
                //    `WriteLine` **不会**把它刷到磁盘。不 Flush 的后果我第一次接线就撞上了：
                //    **日志文件建出来了、但是 0 字节** —— 看起来在写、其实什么都没有，
                //    而进程一退缓冲就没了。
                //    （`FileLogSink` 文件头写着"同步写…崩溃前不会丢"，那句话**只在调用方 Flush 的前提下**成立。
                //      这一点值得记：一句"不会丢"的承诺，可能把责任推给了调用方。）
                // ⚠️ 代价：每条日志一次系统调用。服务端的日志量是"每帧几条事件"级别，
                //    不是热路径 —— 用这点开销换"崩溃前真的不丢"是划算的。
                m_fileSink.Flush();
            }
            catch (Exception ex)
            {
                ReportFileFailureOnce(ex);
            }
        }

        /// <summary>
        /// **只写文件、不写控制台**。
        /// <para>⚠️ 给谁用：启动横幅 / 配置来源 / 统计这些行，调用方**已经**自己
        /// `Console.WriteLine` 过了（那样控制台的排版更好看），但**文件里也得有** ——
        /// 否则事后翻日志会缺"服务端什么时候起的、最终统计是多少"这些最要紧的上下文。</para>
        /// <para>⇒ 于是不变式是：**文件包含控制台的每一行**（可能多一层时间/级别前缀），
        /// 反过来不保证（安静模式下控制台更少）。</para>
        /// </summary>
        /// <param name="level">级别（低于最低级别的**不写**）。</param>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文。</param>
        public void WriteToFile(LogLevel level, LogChannel channel, string message)
        {
            if (m_disposed || m_fileSink == null || level < m_minLevel)
            {
                return;
            }

            m_count++;

            try
            {
                m_fileSink.Write(new LogEntry(level, channel, message, DateTime.Now));
                m_fileSink.Flush();
            }
            catch (Exception ex)
            {
                ReportFileFailureOnce(ex);
            }
        }

        /// <summary>落盘失败只报一次（磁盘满之后每条都会失败，刷屏会把别的问题淹掉）。</summary>
        /// <param name="ex">异常。</param>
        private void ReportFileFailureOnce(Exception ex)
        {
            if (m_fileFailed)
            {
                return;
            }

            m_fileFailed = true;
            Console.WriteLine("❌ 日志落盘失败（**服务端继续跑**）：" + ex.Message);
            Console.WriteLine("    后续同类失败**不再重复刷屏**。");
        }

        /// <summary>落盘失败报过没有（避免刷屏）。</summary>
        private bool m_fileFailed;

        /// <summary>写一条信息。</summary>
        /// <param name="message">正文。</param>
        public void Info(string message)
        {
            Write(LogLevel.Info, LogChannel.General, message);
        }

        /// <summary>写一条警告。</summary>
        /// <param name="message">正文。</param>
        public void Warn(string message)
        {
            Write(LogLevel.Warning, LogChannel.General, message);
        }

        /// <summary>写一条错误。</summary>
        /// <param name="message">正文。</param>
        public void Error(string message)
        {
            Write(LogLevel.Error, LogChannel.General, message);
        }

        /// <summary>刷缓冲（优雅关闭时调）。</summary>
        public void Flush()
        {
            m_fileSink?.Flush();
        }

        /// <summary>一句人话（启动横幅/统计行用）。</summary>
        /// <returns>例如 `控制台 + 文件 Logs\nbc-server-20260927.log（级别 >= Debug）`。</returns>
        public string Describe()
        {
            string target = m_writeConsole ? "控制台" : "（不写控制台）";

            if (m_fileSink != null)
            {
                target += " + 文件 " + m_fileSink.CurrentFilePath;
            }

            return target + "（级别 >= " + m_minLevel + "）";
        }

        /// <summary>关闭文件。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;
            m_fileSink?.Dispose();
        }

        /// <summary>按配置拼日志文件名（`SplitByDay` 时带日期）。</summary>
        /// <param name="splitByDay">要不要带日期。</param>
        /// <param name="now">今天。</param>
        /// <returns>文件名。</returns>
        private static string BuildFileName(bool splitByDay, DateTime now)
        {
            if (!splitByDay)
            {
                return "nbc-server.log";
            }

            // ⚠️ 用 `yyyyMMdd` 而不是 `yyyy-MM-dd`：文件名里少一种分隔符，
            //    `*.log` 的通配与按名排序都更省事（按名排序即按时间排序）。
            return "nbc-server-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";
        }

        /// <summary>把配置里的级别文本转成枚举（认不出来就给 Info 并说一句）。</summary>
        /// <param name="text">配置里的文本。</param>
        /// <param name="note">认不出来时的提示（认得出时为 null）。</param>
        /// <returns>级别。</returns>
        public static LogLevel ParseLevel(string? text, out string? note)
        {
            note = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                return LogLevel.Info;
            }

            // ⚠️ 同时认 .NET 那套名字（Trace/Debug/Information/Warning/Error/Critical），
            //    因为 `appsettings.json` 的 `Logging.Level` 用的是 .NET 的写法 ——
            //    只认自己那套的话，用户按 .NET 习惯写 `Information` 会被**静默降级成 Info**
            //    （碰巧对），而写 `Critical` 就会**静默变成 Info**（错得看不出来）。
            switch (text.Trim().ToLowerInvariant())
            {
                case "debug":
                case "trace":
                case "verbose":
                    return LogLevel.Debug;

                case "info":
                case "information":
                    return LogLevel.Info;

                case "warn":
                case "warning":
                    return LogLevel.Warning;

                case "error":
                case "critical":
                case "fatal":
                    return LogLevel.Error;

                default:
                    note = $"⚠️ `Logging.Level` = 「{text}」不认识（认 Debug/Info/Warning/Error 及其 .NET 别名）" +
                           "，**已按 Info 处理**。";
                    return LogLevel.Info;
            }
        }
    }
}
