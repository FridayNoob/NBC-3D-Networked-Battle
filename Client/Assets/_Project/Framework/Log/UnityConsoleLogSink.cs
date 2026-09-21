// ============================================================================
//  NBC.Framework.Log · 转发到 Unity Console
//  对应需求：FW-M11
//
//  ---------------------------------------------------------------------------
//  ⚠️ 只在播放模式下自动挂上（原因不只是"没必要"）
//  ---------------------------------------------------------------------------
//  EditMode 测试里，**任何一条没被 `LogAssert.Expect` 声明的 Error 都会让用例失败**。
//  如果控制台 sink 在 EditMode 也自动挂上，那么"测 LogSystem 自己"的用例
//  会因为自己写进去的 Error 而变红 —— 那是测试环境被自己的输出污染。
//
//  所以 `LogSystem.OnInit` 里用 `Application.isPlaying` 挡住它，
//  行为与 A5 的 `SceneLoader`、A8 的 `AudioManager` 一致。
//
//  ---------------------------------------------------------------------------
//  级别映射
//  ---------------------------------------------------------------------------
//      Debug   → Debug.Log
//      Info    → Debug.Log
//      Warning → Debug.LogWarning
//      Error   → Debug.LogError
//
//  ⚠️ 之所以把 Debug 也映射到 `Log` 而不是丢弃：**能不能记由 `LogSystem` 决定**
//     （级别过滤在它那里做），sink 只负责"往外倒"。职责单一。
// ============================================================================

using UnityEngine;

namespace NBC.Framework.Log
{
    /// <summary>
    /// 把日志转发到 Unity Console。
    /// </summary>
    public sealed class UnityConsoleLogSink : ILogSink
    {
        /// <summary>写一条。</summary>
        /// <param name="entry">日志条目。</param>
        public void Write(in LogEntry entry)
        {
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

        /// <summary>Unity 的 Console 不需要手动刷。</summary>
        public void Flush()
        {
        }
    }
}
