// ============================================================================
//  NBC.Framework.Log · 日志的基础类型
//  对应需求：FW-M11（分级日志、文件落盘、战斗中滚动日志、网络收发日志开关）
//
//  ---------------------------------------------------------------------------
//  为什么要"分级"和"频道"两个维度
//  ---------------------------------------------------------------------------
//  只按级别过滤是不够的。联机项目里最典型的场景是：
//
//      "我想看网络收发，但不想被战斗逻辑的刷屏淹掉"
//
//  这不是级别问题（两种情况都是 Info），而是**来源**问题。
//  所以拆成两个维度：
//    · **级别（Level）**：这条消息有多严重 —— Debug / Info / Warning / Error
//    · **频道（Channel）**：这条消息是谁发的 —— General / Network / Battle / Asset / Ui
//
//  ⚠️ 两个维度**都要能关**。只给级别的话，"网络收发日志开关"（FW-M11 明写的要求）
//     就只能靠级别近似替代 —— 那会把网络的正常 Info 一起关掉。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 日志**不许影响逻辑**
//  ---------------------------------------------------------------------------
//  两条硬规矩：
//    ① **共享层（`NBC.Shared`）里一行日志都不许有** —— 它要能在服务端跑，
//       而且帧同步逻辑的输出不能被"日志开没开"改变。
//    ② 判断"要不要记"必须发生在**字符串拼接之前**。
//       否则 `Log.Info("位置 " + pos)` 这句里的拼接白做了 —— 而这是热路径上的分配。
//       所以提供了 `IsEnabled(level, channel)` 让调用方**提前短路**。
// ============================================================================

using System;

namespace NBC.Framework.Log
{
    /// <summary>
    /// 日志级别。**数值越大越严重。**
    /// </summary>
    public enum LogLevel
    {
        /// <summary>开发期细节（默认关）。</summary>
        Debug = 0,

        /// <summary>常规信息。</summary>
        Info = 1,

        /// <summary>警告：不影响运行，但值得看一眼。</summary>
        Warning = 2,

        /// <summary>错误。</summary>
        Error = 3
    }

    /// <summary>
    /// 日志频道（**来源**）。用来单独开关某一类日志。
    /// </summary>
    public enum LogChannel
    {
        /// <summary>杂项。</summary>
        General = 0,

        /// <summary>网络收发（FW-M11 点名要能单独开关的就是它）。</summary>
        Network = 1,

        /// <summary>战斗逻辑。</summary>
        Battle = 2,

        /// <summary>资源加载。</summary>
        Asset = 3,

        /// <summary>界面。</summary>
        Ui = 4
    }

    /// <summary>
    /// 一条日志。**不可变值类型** —— 滚动日志里会存很多条，不该每条都分配对象。
    /// </summary>
    public readonly struct LogEntry
    {
        /// <summary>级别。</summary>
        public readonly LogLevel Level;

        /// <summary>频道。</summary>
        public readonly LogChannel Channel;

        /// <summary>正文。</summary>
        public readonly string Message;

        /// <summary>
        /// 记录时刻（本地时间）。
        /// <para>
        /// ⚠️ 它**不参与任何逻辑判断**，只用于显示 —— 所以用真实时间没有确定性风险。
        /// （如果哪天有人拿它做逻辑分支，那就是在往确定性里插一刀。）
        /// </para>
        /// </summary>
        public readonly DateTime Timestamp;

        /// <summary>构造。</summary>
        /// <param name="level">级别。</param>
        /// <param name="channel">频道。</param>
        /// <param name="message">正文。</param>
        /// <param name="timestamp">时刻。</param>
        public LogEntry(LogLevel level, LogChannel channel, string message, DateTime timestamp)
        {
            Level = level;
            Channel = channel;
            Message = message;
            Timestamp = timestamp;
        }

        /// <summary>单行文本（文件与控制台都用它）。</summary>
        /// <returns>形如 `[12:34:56.789][Info][Network] 正文`。</returns>
        public override string ToString()
        {
            return "[" + Timestamp.ToString("HH:mm:ss.fff") + "][" + Level + "][" + Channel + "] " + Message;
        }
    }
}
