// ============================================================================
//  NBC.Framework.Perf · 性能采样器（把计数器组装成一份快照）
//  对应需求：FW-M12（FPS / DrawCall / SetPass / Tris / GC Alloc / 内存）
//
//  ---------------------------------------------------------------------------
//  职责：**只负责"组装"，不负责"从哪读"**
//  ---------------------------------------------------------------------------
//      PerfSampler.Tick(deltaSeconds)
//          ├── 把这一帧的耗时喂给 FpsCounter（纯逻辑，可测）
//          └── 向 IPerfCounterSource 要各个计数器（接缝，可注入）
//
//  于是 EditMode 里能测的东西很多：帧率窗口、NaN 的传播、
//  "读不到"时的行为、"总共推进了多少帧"……
//  真正依赖 Unity Profiler 的只有 `ProfilerRecorderCounterSource` 那一个文件。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 读不到就填 NaN，绝不填 0
//  ---------------------------------------------------------------------------
//  发布版没有渲染统计时，`TryRead` 会返回 false。
//  这时候填 0 的话看板会显示"DrawCall = 0" —— **一个看起来正常的假数字**。
//  所以约定 NaN，显示层渲染成 `N/A`。
//
//  同时记一个 `ReadFailureCount`：看板显示 N/A 时，你能分清
//  "**从来没读到过**"（计数器不可用）和"**偶尔读不到**"（偶发抖动）。
// ============================================================================

using System;

namespace NBC.Framework.Perf
{
    /// <summary>
    /// 性能采样器：每帧产出一份 <see cref="PerfSnapshot"/>。
    /// </summary>
    public sealed class PerfSampler : IDisposable
    {
        /// <summary>默认窗口帧数。</summary>
        public const int DefaultWindowFrames = FpsCounter.DefaultWindowFrames;

        /// <summary>计数器总数（用于遍历）。</summary>
        private static readonly PerfCounterId[] AllCounters = new PerfCounterId[]
        {
            PerfCounterId.DrawCalls,
            PerfCounterId.SetPassCalls,
            PerfCounterId.Triangles,
            PerfCounterId.GcAllocatedInFrame,
            PerfCounterId.TotalMemory,
            PerfCounterId.MonoUsedMemory
        };

        private readonly IPerfCounterSource m_source;
        private readonly FpsCounter m_fps;

        /// <summary>各计数器当前值（读不到的是 NaN）。</summary>
        private readonly float[] m_values = new float[AllCounters.Length];

        private bool m_disposed;

        /// <summary>构造。</summary>
        /// <param name="source">计数器来源。</param>
        /// <param name="fpsWindowFrames">FPS 窗口帧数。</param>
        public PerfSampler(IPerfCounterSource source, int fpsWindowFrames = DefaultWindowFrames)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            m_source = source;
            m_fps = new FpsCounter(fpsWindowFrames);

            for (int i = 0; i < m_values.Length; i++)
            {
                m_values[i] = PerfSnapshot.Unavailable;
            }

            Current = PerfSnapshot.Empty;
            m_source.Start();
        }

        /// <summary>计数器的来源。</summary>
        public IPerfCounterSource Source
        {
            get { return m_source; }
        }

        /// <summary>最新的一份快照。</summary>
        public PerfSnapshot Current { get; private set; }

        /// <summary>累计推进了多少帧。</summary>
        public long TickCount { get; private set; }

        /// <summary>
        /// **读不到**计数器的累计次数。
        /// <para>用来区分"计数器根本不可用"（次数 ≈ 帧数 × 6）和"偶尔读不到"。</para>
        /// </summary>
        public long ReadFailureCount { get; private set; }

        /// <summary>帧率计数（需要更细的统计时可以直接用它）。</summary>
        public FpsCounter Fps
        {
            get { return m_fps; }
        }

        /// <summary>
        /// 推进一帧。
        /// </summary>
        /// <param name="deltaSeconds">这一帧花了多少秒。</param>
        public void Tick(float deltaSeconds)
        {
            ThrowIfDisposed();

            m_fps.AddFrame(deltaSeconds);
            TickCount++;
            Current = Build();
        }

        /// <summary>清空统计（帧号也归零）。</summary>
        public void Reset()
        {
            m_fps.Reset();
            TickCount = 0L;
            ReadFailureCount = 0L;

            for (int i = 0; i < m_values.Length; i++)
            {
                m_values[i] = PerfSnapshot.Unavailable;
            }

            Current = PerfSnapshot.Empty;
        }

        /// <summary>停掉底层采集。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;
            m_source.Stop();
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>读一遍所有计数器，组装成快照。</summary>
        /// <returns>快照。</returns>
        private PerfSnapshot Build()
        {
            for (int i = 0; i < AllCounters.Length; i++)
            {
                float value;

                if (m_source.TryRead(AllCounters[i], out value))
                {
                    m_values[i] = value;
                }
                else
                {
                    m_values[i] = PerfSnapshot.Unavailable;
                    ReadFailureCount++;
                }
            }

            return new PerfSnapshot(
                m_fps.Fps,
                m_fps.AverageFrameMilliseconds,
                m_values[(int)PerfCounterId.DrawCalls],
                m_values[(int)PerfCounterId.SetPassCalls],
                m_values[(int)PerfCounterId.Triangles],
                m_values[(int)PerfCounterId.GcAllocatedInFrame],
                m_values[(int)PerfCounterId.TotalMemory],
                m_values[(int)PerfCounterId.MonoUsedMemory],
                TickCount);
        }

        /// <summary>销毁之后再用就报错（别让它静默地什么都不做）。</summary>
        private void ThrowIfDisposed()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PerfSampler), "[PerfSampler] 已经 Dispose 过了，不能继续 Tick。");
            }
        }
    }
}
