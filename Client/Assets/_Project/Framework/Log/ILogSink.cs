// 这个文件里有：
//   ILogSink（接口）：日志输出目标的接缝，落盘 / 控制台 / 测试内存三种实现共用它。

namespace NBC.Framework.Log
{
    // 主要功能：把日志的输出目的地（文件 / 控制台 / 测试内存）抽成可替换的实现，让 LogSystem 只决定记不记、记什么，不管记到哪去。
    // 主要数据成员：无（纯接口；状态由实现方自己持有，例如 FileLogSink 的 StreamWriter 与已写字节数）。
    // 主要外部方法：Write / Flush。
    // 谁在用它：LogSystem 的 AddSink / RemoveSink / Write / Flush 按挂载顺序调它；实现方是 FileLogSink、UnityConsoleLogSink 与 EditMode 测试里的 MemoryLogSink。
    // 注意：这道缝的主要价值是测试与将来的扩展（比如把日志发给服务端），不是像 IInputSource 那样明确的架构替换点。
    /// <summary>日志的输出目的地：LogSystem 把每条日志交给挂上的实现。</summary>
    public interface ILogSink
    {
        // 功能：把一条已经过级别与频道过滤的日志交给具体的目的地。
        // 入参：entry 日志条目（按只读引用传入，级别 / 频道 / 正文 / 时间戳都在里面）
        // 返回：无
        // 步骤：接口方法没有实现步骤；实现方只负责往外倒，级别与频道已在 LogSystem 里判过，不要在这里再判一遍（职责单一，也免得两处规则打架）
        // 上游：LogSystem.Write 对每个挂上的 Sink 各调一次
        // 下游：FileLogSink 写文件、UnityConsoleLogSink 转 Unity Console、MemoryLogSink 存内存
        // 注意：实现里别再往 LogSystem 写日志（一条日志会绕回去自己套自己），也别在 Write 里增删 Sink —— 此时 LogSystem 正按索引遍历 m_sinks，边派发边改会让后面的 Sink 被跳过。
        /// <summary>写一条已通过级别与频道过滤的日志。</summary>
        void Write(in LogEntry entry);

        // 功能：把实现内部缓冲的内容立刻刷到目的地，别把最后几条留在进程内存里。
        // 入参：无
        // 返回：无
        // 步骤：接口方法没有实现步骤；实现方在退出 / 崩溃前把缓冲清空即可
        // 上游：LogSystem.Flush（退出前必须调）、LogSystem.OnDispose
        // 下游：FileLogSink.Flush 刷 StreamWriter；UnityConsoleLogSink 是空实现（Console 不需要手动刷）
        // 注意：不调它会丢最后几条；文件句柄不在这里关 —— FileLogSink 靠 Dispose 关，LogSystem 只在 OnDispose 里释放实现了 IDisposable 的 Sink，RemoveSink 只摘不关。
        /// <summary>把缓冲刷到目的地，退出前必须调，否则最后几条会丢。</summary>
        void Flush();
    }
}
