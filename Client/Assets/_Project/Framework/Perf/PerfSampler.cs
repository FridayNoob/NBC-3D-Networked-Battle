// 这个文件里有：
//   PerfSampler（类）：性能采样器，每帧把帧耗时与六项计数器组装成一份快照
// 注意：读不到就填 NaN，绝不填 0 —— 0 会显示成看起来正常的假数字，显示层要把 NaN 渲染成 N/A。

using System;

namespace NBC.Framework.Perf
{
    // 主要功能：每帧收一次帧耗时、读一遍六项性能计数器，组装成不可变快照并维护累计统计。
    // 主要数据成员：m_fps（滑动窗口帧率统计）、m_values（六项计数器最近读数，读不到是 NaN）、TickCount（累计帧数）、ReadFailureCount（读失败次数）。
    // 主要外部方法：Tick / Reset / Dispose，只读属性 Source / Current / Fps / TickCount / ReadFailureCount。
    // 谁在用它：NBC.Framework.UI 的 PerfHudPanel 每帧调 Tick 并读 Current 显示；EditMode 测试注入假计数器源验证读数与失败计数。
    /// <summary>性能采样器：每帧读六项计数器配合帧耗时产出一份快照，读不到填 NaN 并累计失败次数。</summary>
    public sealed class PerfSampler : IDisposable
    {
        /// <summary>默认帧率窗口帧数，60 帧（60 FPS 下约 1 秒）；也是 FpsCounter 的默认窗口。</summary>
        public const int DefaultWindowFrames = FpsCounter.DefaultWindowFrames;

        // 六项计数器的遍历表，顺序与 PerfCounterId 的枚举值 0..5 一一对应，同时是 m_values 的下标，顺序不能改
        private static readonly PerfCounterId[] AllCounters = new PerfCounterId[]
        {
            PerfCounterId.DrawCalls,
            PerfCounterId.SetPassCalls,
            PerfCounterId.Triangles,
            PerfCounterId.GcAllocatedInFrame,
            PerfCounterId.TotalMemory,
            PerfCounterId.MonoUsedMemory
        };

        // 计数器来源，构造时注入；本类只在构造末尾 Start、在 Dispose 里 Stop
        private readonly IPerfCounterSource m_source;
        // 帧率统计，窗口帧数构造后固定；进参按秒，对外给帧/秒与毫秒
        private readonly FpsCounter m_fps;

        // 六项计数器最近一次读到的值，下标同 AllCounters；读不到是 NaN（PerfSnapshot.Unavailable），单位随项是个/帧或字节
        private readonly float[] m_values = new float[AllCounters.Length];

        // 是否已 Dispose；true 之后再 Tick 抛异常，再 Dispose 直接返回
        private bool m_disposed;

        // 功能：接上计数器来源、按窗口帧数建帧率统计，把六项值先填成 NaN，最后启动采集。
        // 入参：source 计数器来源，null 直接抛 ArgumentNullException；fpsWindowFrames 帧率窗口帧数，默认 60 帧，小于 1 时 FpsCounter 内部退回默认值
        // 返回：无（构造函数）
        // 步骤：
        //   1) 先校验 source 非 null，在这里炸掉之后每帧读值就不用反复判空
        //   2) 用 fpsWindowFrames 建 FpsCounter，窗口长度此时固定
        //   3) 六项值先填 Unavailable（NaN），避免第一帧之前把 0 当成真实读数
        //   4) Current 置空快照，末尾再调 Start —— 部件都就绪后才开始采集
        // 上游：PerfHudPanel.OnInit（播放模式下 new ProfilerRecorderCounterSource）、EditMode 测试注入假源
        // 下游：FpsCounter 构造函数、IPerfCounterSource.Start
        // 注意：窗口帧数决定 FPS 的平滑程度与覆盖时长（默认 60 帧，60 FPS 下约 1 秒）：太小 FPS 乱跳，太大把卡顿抹平。
        /// <summary>接上来源、按窗口帧数建帧率统计，填好初始 NaN 后启动采集。</summary>
        /// <param name="source">计数器来源，null 抛 ArgumentNullException。</param>
        /// <param name="fpsWindowFrames">帧率窗口帧数，默认 60 帧；小于 1 时退回默认值。</param>
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

        // 功能：交出构造时注入的计数器来源，外部要直接读某个计数器时用。
        /// <summary>构造时注入的计数器来源；本类在 Dispose 时对它调 Stop。</summary>
        public IPerfCounterSource Source
        {
            get { return m_source; }
        }

        // 功能：取最新一份快照，显示层每帧读它。
        // 返回：最近一次 Tick 产出的不可变快照；构造后与 Reset 后是 PerfSnapshot.Empty
        /// <summary>最新一份快照；还没 Tick 过时是空快照。</summary>
        public PerfSnapshot Current { get; private set; }

        // 功能：取累计推进的帧数，从构造或 Reset 起算。
        // 返回：累计帧数，帧，≥ 0；每次 Tick 加一，Reset 归零
        /// <summary>累计推进的帧数，帧；Reset 归零。</summary>
        public long TickCount { get; private set; }

        // 功能：累计读不到计数器的次数，用来区分计数器根本不可用与偶发抖动。
        // 返回：读失败次数，次，≥ 0；每帧最多加六次（六个计数器各一次），Reset 归零
        // 注意：次数约等于帧数 × 6 说明计数器根本不可用；只是偶尔增加说明偶发抖动。
        /// <summary>读不到计数器的累计次数，次；约等于帧数 × 6 说明计数器不可用，偶尔增加说明偶发抖动。</summary>
        public long ReadFailureCount { get; private set; }

        // 功能：交出内部帧率统计，需要窗口样本数、最差帧耗时等细节时直接用它。
        /// <summary>帧率统计；需要最差帧耗时、窗口样本数等细节时直接读它。</summary>
        public FpsCounter Fps
        {
            get { return m_fps; }
        }

        // 功能：推进一帧：把本帧耗时喂给帧率窗口，帧号加一，再重新组装一份快照。
        // 入参：deltaSeconds 本帧耗时，秒；非正数被丢弃，超过 1 秒按 1 秒记
        // 返回：无
        // 步骤：
        //   1) 先查是否已 Dispose，已释放就抛异常 —— 别让 Tick 在释放后静默空转
        //   2) 耗时记进帧率窗口；FPS 要每一帧都喂，漏一帧就偏
        //   3) 帧号加一，再读一遍六项计数器组装快照
        // 上游：PerfHudPanel.Tick（每帧回调，传 unscaledDeltaTime）、EditMode 测试直接调
        // 下游：ThrowIfDisposed、FpsCounter.AddFrame、Build
        /// <summary>推进一帧：记一帧耗时、帧号加一，并刷新 Current 快照。</summary>
        /// <param name="deltaSeconds">本帧耗时，秒；非正数丢弃，超过 1 秒按 1 秒记。</param>
        public void Tick(float deltaSeconds)
        {
            ThrowIfDisposed();

            m_fps.AddFrame(deltaSeconds);
            TickCount++;
            Current = Build();
        }

        // 功能：清空统计：帧率窗口、帧号、读失败次数与六项值全部回到初始状态。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 清空帧率窗口，让它回到预热状态
        //   2) 帧号与读失败次数归零，从 0 重新计数
        //   3) 六项值重新填 NaN，避免上一轮的值留在快照里
        //   4) Current 置空快照，下一帧 Tick 之前读到的是空值
        // 上游：需要重开统计的调用方与 EditMode 测试
        // 下游：FpsCounter.Reset
        // 注意：Reset 不 Stop 也不 Start 计数器来源 —— 底层采集一直在跑，这里只清统计。
        /// <summary>清空统计：帧率窗口、帧号、读失败次数与六项值都回到初始状态。</summary>
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

        // 功能：停掉底层采集并标记已释放，重复调用直接返回。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已释放就直接返回，保证幂等
        //   2) 先置 m_disposed 再调 Stop：之后 Tick 抛异常，重复 Dispose 也不会再 Stop 一次
        // 上游：PerfHudPanel.ReleaseSampler（OnDestroy 或测试显式调用）、EditMode 测试的 finally
        // 下游：IPerfCounterSource.Stop
        // 注意：调用方必须先停掉每帧驱动再 Dispose —— 反过来的话释放后仍有回调进来，Tick 会抛 ObjectDisposedException。
        /// <summary>停掉底层采集；可重复调用，之后 Tick 会抛 ObjectDisposedException。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;
            m_source.Stop();
        }

        // 内部

        // 功能：读一遍六个计数器，读到的更新缓存，读不到就填 NaN 并累加失败次数，最后把缓存拼成快照。
        // 入参：无（计数器来源与缓存都在字段上）
        // 返回：本帧的快照；六项中读不到的是 NaN，FPS 与平均帧耗时来自帧率窗口，帧号取 TickCount
        // 步骤：
        //   1) 按 AllCounters 的顺序逐项 TryRead，成功就存进 m_values
        //   2) 失败存 NaN 并给 ReadFailureCount 加一 —— 绝不填 0 冒充
        //   3) 最后按固定顺序把帧率、平均帧耗时与六项值装进快照，帧号用 TickCount
        // 上游：Tick 每帧调一次
        // 下游：IPerfCounterSource.TryRead、PerfSnapshot 构造函数
        // 注意：发布版（非开发版）大部分渲染统计不可用、EditMode 下 Profiler 也没在跑，读不到是正常状态而不是错误；这时对应项是 NaN，看板显示 N/A。
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

        // 功能：已 Dispose 还来 Tick 时报错，别让它静默地什么都不做。
        // 入参：无
        // 返回：无
        // 步骤：只查 m_disposed，true 就抛 ObjectDisposedException 并把类型名写进消息，方便定位是谁在释放后还在用
        // 上游：Tick
        // 下游：无
        // 注意：故意抛异常而不是静默返回 —— 静默会让释放后还在每帧调用的地方永远发现不了自己的生命周期用错了。
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
