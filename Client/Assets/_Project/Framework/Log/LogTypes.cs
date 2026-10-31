// 这个文件里有：
//   LogLevel（枚举）：日志级别，数值越大越严重，最低级别过滤按数值比较
//   LogChannel（枚举）：日志来源频道，用来单独开或关某一类日志
//   LogEntry（结构体）：一条日志的级别、频道、正文与记录时刻，只读值类型，滚动日志里存的每一条就是它
//
// 注意：级别和频道是两个维度，都得能单独关 —— 只剩级别的话，想把网络收发关掉就只能连正常的 Info 一起关。
// 注意：判断「要不要记」必须发生在拼字符串之前 —— 否则关了日志也照样分配字符串。
// 注意：共享层（NBC.Shared）一行日志都不许有 —— 它要在服务端跑，帧同步的输出不能被日志开关改变。

using System;

namespace NBC.Framework.Log
{
    // 主要功能：日志级别，数值越大越严重，过滤时按数值比大小，不看名字。
    // 主要数据成员：Debug = 0、Info = 1、Warning = 2、Error = 3，共四档。
    // 主要外部方法：无（纯枚举）。
    // 谁在用它：LogSystem 的 MinLevel 与 IsEnabled 拿它做比较、Write 据此丢弃；UnityConsoleLogSink 按它选 Unity 的普通 / 警告 / 错误三个输出口。
    /// <summary>日志级别，数值越大越严重；低于 LogSystem.MinLevel 的直接丢弃。</summary>
    public enum LogLevel
    {
        /// <summary>开发期细节，默认关（MinLevel 默认 Info 时就记不下来）。</summary>
        Debug = 0,

        /// <summary>常规信息，联机里的状态播报大多落在这一档。</summary>
        Info = 1,

        /// <summary>不影响运行、但值得看一眼；UnityConsoleLogSink 会把它转成警告。</summary>
        Warning = 2,

        /// <summary>出错；UnityConsoleLogSink 会把它转成 Unity Console 的错误。</summary>
        Error = 3
    }

    // 主要功能：日志来源频道，用来单独开或关某一类日志，跟级别互不替代。
    // 主要数据成员：General = 0、Network = 1、Battle = 2、Asset = 3、Ui = 4，共五个。
    // 主要外部方法：无（纯枚举）。
    // 谁在用它：LogSystem 用 Enum.GetValues 取总数建频道开关数组，SetChannelEnabled / IsChannelEnabled 按它开关；网络收发日志的那个开关就是其中的 Network。
    // 注意：越界的频道值会被 LogSystem 的 ChannelIndex 当场抛 ArgumentOutOfRangeException，不静默吞掉 —— 抓到的是调用方的 bug。
    /// <summary>日志来源频道，用于按来源单独开关；取值 0..4。</summary>
    public enum LogChannel
    {
        /// <summary>杂项，不属于其它频道的消息放这里。</summary>
        General = 0,

        /// <summary>网络收发；需求点名要能单独关掉的就是它。</summary>
        Network = 1,

        /// <summary>战斗逻辑。</summary>
        Battle = 2,

        /// <summary>资源加载。</summary>
        Asset = 3,

        /// <summary>界面。</summary>
        Ui = 4
    }

    // 主要功能：装一条日志的级别、频道、正文与时刻；只读值类型，滚动日志里会存很多条，所以不逐条分配对象。
    // 主要数据成员：Level（0..3）、Channel（0..4）、Message（正文）、Timestamp（本地时间，只用于显示）。
    // 主要外部方法：构造函数；ToString（拼单行文本）。
    // 谁在用它：LogSystem.Write 每记一条 new 一个压进环形缓冲，CopyRecent 按新的在前拷出去；FileLogSink 与 UnityConsoleLogSink 的 Write 只读消费。
    /// <summary>一条日志的完整内容；不可变，构造后不再修改。</summary>
    public readonly struct LogEntry
    {
        /// <summary>级别，取值 0..3（Debug..Error），越大越严重；决定 Sink 用哪种输出。</summary>
        public readonly LogLevel Level;

        /// <summary>频道，取值 0..4（General..Ui）；决定这条日志受哪个开关管，与级别互不替代。</summary>
        public readonly LogChannel Channel;

        /// <summary>正文，经 LogSystem 写出时保证非 null（想记空行传 string.Empty）；构造函数本身不拦 null。</summary>
        public readonly string Message;

        /// <summary>记录时刻，本地时间（DateTime.Now），精确到毫秒；只用于显示，不参与逻辑判断 —— 拿它做分支等于往确定性里插一刀。</summary>
        public readonly DateTime Timestamp;

        /// <summary>按给定的级别、频道、正文与时刻构造一条日志。</summary>
        /// <param name="timestamp">记录时刻，本地时间；LogSystem 传 DateTime.Now。</param>
        public LogEntry(LogLevel level, LogChannel channel, string message, DateTime timestamp)
        {
            // 功能：把四个值原样存下来，拼成一条不可变的日志条目。
            // 入参：level 级别（0..3）；channel 频道（0..4）；message 正文；timestamp 记录时刻（本地时间，只用于显示）
            // 返回：无
            // 步骤：按参数顺序逐个赋给 readonly 字段 —— 全是 readonly，只有构造函数能写，顺序跟参数一致，读起来不会对错位。
            // 上游：LogSystem.Write（每条 new 一次），测试里也直接 new
            // 下游：无
            // 注意：这里不校验 message 为 null —— 判空在 LogSystem.Write 里做，绕过它直接 new 一条空正文的条目没人拦。
            Level = level;
            Channel = channel;
            Message = message;
            Timestamp = timestamp;
        }

        /// <summary>拼成单行文本，文件与控制台都用它。</summary>
        /// <returns>形如 [12:34:56.789][Info][Network] 正文 的一行。</returns>
        public override string ToString()
        {
            // 功能：把时刻、级别、频道、正文拼成一行文本。
            // 入参：无
            // 返回：形如 [12:34:56.789][Info][Network] 正文 的一行；级别与频道用枚举名，时刻精确到毫秒
            // 步骤：按 时刻 → 级别 → 频道 → 正文 的固定顺序拼接 —— 顺序统一，文件与控制台的行格式才一致，日志才能按行筛选。
            // 上游：FileLogSink.Write 与 UnityConsoleLogSink.Write（都直接拿它当整行）
            // 下游：无
            return "[" + Timestamp.ToString("HH:mm:ss.fff") + "][" + Level + "][" + Channel + "] " + Message;
        }
    }
}
