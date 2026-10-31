// 这个文件里有：
//   UnityConsoleLogSink（类）：把日志按级别转发到 Unity Console 的日志输出目标，不缓冲、不过滤、不持有状态。

using UnityEngine;

namespace NBC.Framework.Log
{
    // 主要功能：把一条日志按级别转发到 Unity Console，不缓冲、不过滤，自身不持有状态。
    // 主要数据成员：无 —— 没有字段，每条日志即时转发。
    // 主要外部方法：Write（转发一条）、Flush（空实现，满足 ILogSink 的契约）。
    // 谁在用它：LogSystem.OnInit 在播放模式下 new 出来挂进 sink 链；EditMode 故意不挂。
    // 注意：EditMode 挂上会让测试环境被自己的输出污染 —— 测试里任何没被 LogAssert.Expect 声明的 Error 都会把用例判失败。
    /// <summary>
    /// 把日志按级别转发到 Unity Console 的输出目标；不缓冲、不过滤。
    /// </summary>
    public sealed class UnityConsoleLogSink : ILogSink
    {
        /// <summary>把一条日志按级别转发到 Unity Console。</summary>
        /// <param name="entry">要转发的一条日志，含级别、频道、正文与时刻。</param>
        public void Write(in LogEntry entry)
        {
            // 功能：把日志压成单行文本，按级别转发到 Unity Console；Debug 与 Info 都走 Log。
            // 入参：entry 要转发的一条日志（含级别、频道、正文与时刻），按 in 引用传入不复制
            // 返回：无
            // 步骤：
            //   1) 先用 entry.ToString() 拼成「[时刻][级别][频道] 正文」的单行，文件与控制台共用同一格式
            //   2) 按级别分派：Warning 走 LogWarning，Error 走 LogError，其余（Debug / Info）走 Log
            // 上游：LogSystem.Write 遍历 sink 链时调用
            // 下游：UnityEngine.Debug.Log / LogWarning / LogError
            // 注意：Debug 也走 Log 而不是丢弃 —— 级别过滤在 LogSystem，sink 只负责往外倒，职责单一。
            string line = entry.ToString();

            switch (entry.Level)
            {
                case LogLevel.Warning:
                    Debug.LogWarning(line);
                    return;

                case LogLevel.Error:
                    Debug.LogError(line);
                    return;

                default:
                    Debug.Log(line);
                    return;
            }
        }

        /// <summary>空实现：Unity Console 没有待刷的缓冲，保留以满足 ILogSink 的契约。</summary>
        public void Flush()
        {
            // 功能：空实现 —— Unity Console 自己即时输出，没有待刷的缓冲。
            // 入参：无
            // 返回：无
            // 步骤：无 —— 不碰任何状态。
            // 上游：LogSystem.Flush 在退出或销毁前遍历 sink 链时调用
            // 下游：无
        }
    }
}
