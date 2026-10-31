// 这个文件里有：
//   FpsCounter（类）：滑动窗口帧率统计，用最近若干帧的耗时算平均 FPS、平均与最差帧耗时

namespace NBC.Framework.Perf
{
    // 主要功能：维护最近 N 帧的耗时，算出平均帧率、平均帧耗时与最差帧耗时。
    // 主要数据成员：m_frameSeconds（环形缓冲，秒）、m_count（有效样本数，帧）、m_sum（窗口内耗时和，秒）。
    // 主要外部方法：AddFrame / Reset / Fps / AverageFrameMilliseconds / WorstFrameMilliseconds。
    // 谁在用它：PerfSampler 每帧喂一次 AddFrame；EditMode 测试直接构造它验算统计结果。
    /// <summary>滑动窗口帧率统计：只接收每帧耗时（秒），不依赖 Unity，可在 EditMode 里直接测。</summary>
    public sealed class FpsCounter
    {
        /// <summary>默认窗口帧数，60 帧（60 FPS 下约 1 秒）。</summary>
        public const int DefaultWindowFrames = 60;

        /// <summary>单帧耗时上限，秒；超过按 1 秒记。</summary>
        public const float MaxTrackedFrameSeconds = 1f;

        // 环形缓冲，存放最近各帧的耗时，秒；长度 = 窗口帧数
        private readonly float[] m_frameSeconds;

        // 下一个要写入的槽位下标，0..窗口帧数-1；满窗后它指向最旧的样本
        private int m_nextIndex;
        // 当前有效样本数，帧，0..窗口帧数；未满说明还在预热
        private int m_count;
        // 窗口内所有样本的耗时和，秒；必须与槽位内容同步，否则 FPS 会偏
        private float m_sum;

        // 功能：按窗口帧数分配环形缓冲；帧数不合法时退回默认值。
        // 入参：windowFrames 采样窗口的帧数，小于 1 时按默认 60 帧处理。
        // 返回：无（构造函数不返回值）。
        // 步骤：
        //   1) 按 windowFrames 分配缓冲
        //   2) windowFrames 小于 1 时改用默认 60 帧，因为长度 0 的数组会让 AddFrame 取模时除以零
        // 注意：窗口帧数决定能看到多长时间的抖动；窗口太短 FPS 跳动，太长又会把卡顿抹平。
        // 上游：PerfSampler 构造时传入窗口帧数；测试直接 new。
        // 下游：无。
        /// <summary>按窗口帧数分配环形缓冲，帧数小于 1 时退回默认值。</summary>
        /// <param name="windowFrames">采样窗口的帧数，小于 1 时按默认 60 帧处理。</param>
        public FpsCounter(int windowFrames = DefaultWindowFrames)
        {
            m_frameSeconds = new float[windowFrames >= 1 ? windowFrames : DefaultWindowFrames];
        }

        // 功能：取窗口容量，创建后固定不变。
        // 返回：窗口最多容纳的帧数，帧。
        /// <summary>窗口容量，帧。</summary>
        public int WindowFrames
        {
            get { return m_frameSeconds.Length; }
        }

        // 功能：取当前已经收进来的帧数。
        // 返回：窗口内样本数，帧；小于窗口容量说明还在预热。
        // 上游：EditMode 测试断言预热进度（也会经 PerfSampler.Fps 间接读）。
        // 下游：无。
        /// <summary>窗口里当前的样本数，帧；小于窗口容量说明还在预热。</summary>
        public int SampleCount
        {
            get { return m_count; }
        }

        // 功能：用样本数除以窗口内耗时和，算平均帧率。
        // 返回：平均帧率，帧/秒；没有样本或耗时和非正时返回 0。
        // 步骤：
        //   1) 没有样本或耗时和非正，直接返回 0
        //   2) 样本数 ÷ 耗时和（秒）= 帧/秒
        // 注意：没有样本时返回 0 而不是 NaN，显示层可以直接用，也顺手挡住了除以零。
        // 上游：PerfSampler.Build 每次产出快照时读。
        // 下游：HUD 只读快照，不直接碰它。
        /// <summary>窗口内的平均帧率，帧/秒；没有样本时为 0。</summary>
        public float Fps
        {
            get
            {
                if (m_count == 0 || m_sum <= 0f)
                {
                    return 0f;
                }

                return m_count / m_sum;
            }
        }

        // 功能：把窗口内耗时和除以样本数，换算成单帧平均耗时。
        // 返回：平均帧耗时，毫秒；没有样本时为 0。
        // 步骤：
        //   1) 样本数为 0 先返回 0，避免除以零
        //   2) 耗时和 ÷ 样本数得到秒，再乘 1000 换成毫秒
        // 上游：PerfSampler.Build。
        // 下游：无。
        /// <summary>窗口内的平均单帧耗时，毫秒；没有样本时为 0。</summary>
        public float AverageFrameMilliseconds
        {
            get { return m_count == 0 ? 0f : (m_sum / m_count) * 1000f; }
        }

        // 功能：找出窗口里最慢那一帧的耗时。
        // 返回：最差帧耗时，毫秒；没有样本时为 0。
        // 步骤：
        //   1) 没有样本直接返回 0
        //   2) 遍历 m_count 个有效样本取最大值（窗口只有 60 帧量级，每次重算的开销可忽略）
        //   3) 秒乘 1000 换成毫秒
        // 注意：看卡顿要看它，平均 60 FPS 完全可能掩盖每 3 秒卡 200 毫秒。
        // 上游：EditMode 测试直接断言；PerfSampler 的快照暂未接入。
        // 下游：无。
        /// <summary>窗口内最慢一帧的耗时，毫秒；没有样本时为 0。</summary>
        public float WorstFrameMilliseconds
        {
            get
            {
                if (m_count == 0)
                {
                    return 0f;
                }

                float worst = 0f;

                for (int i = 0; i < m_count; i++)
                {
                    if (m_frameSeconds[i] > worst)
                    {
                        worst = m_frameSeconds[i];
                    }
                }

                return worst * 1000f;
            }
        }

        // 功能：把一帧耗时记进环形窗口，满窗时用新值替换最旧值，并同步维护样本数与耗时和。
        // 入参：deltaSeconds 本帧耗时，秒；非正数丢弃，超过上限按 1 秒记。
        // 返回：无。
        // 步骤：
        //   1) 非正数直接返回，不记进窗口
        //   2) 超过上限先夹到 MaxTrackedFrameSeconds，再入窗
        //   3) 窗口未满：写入当前槽位、累加耗时和、样本数加一
        //   4) 窗口已满：先从耗时和里减掉该槽位的旧值，再覆盖并累加新值
        //   5) 写指针环形前移，取模回绕到 0
        // 注意：非正耗时会让 Fps 除以零或出现负值；编辑器断点、切出再切回可能产生数秒的假耗时，不夹上限会让整个窗口的 FPS 失真到像卡死。
        // 上游：PerfSampler.Tick 每帧调用一次。
        // 下游：无。
        /// <summary>记一帧耗时；非正数丢弃，超过上限按上限记。</summary>
        /// <param name="deltaSeconds">这一帧的耗时，秒。</param>
        public void AddFrame(float deltaSeconds)
        {
            if (deltaSeconds <= 0f)
            {
                return;
            }

            if (deltaSeconds > MaxTrackedFrameSeconds)
            {
                deltaSeconds = MaxTrackedFrameSeconds;
            }

            if (m_count < m_frameSeconds.Length)
            {
                m_frameSeconds[m_nextIndex] = deltaSeconds;
                m_sum += deltaSeconds;
                m_count++;
            }
            else
            {
                m_sum -= m_frameSeconds[m_nextIndex];
                m_frameSeconds[m_nextIndex] = deltaSeconds;
                m_sum += deltaSeconds;
            }

            m_nextIndex = (m_nextIndex + 1) % m_frameSeconds.Length;
        }

        // 功能：清空窗口，回到没有样本的预热状态。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 逐个槽位清零，避免残留旧帧耗时
        //   2) 写指针、样本数、耗时和一并归零，让下一次 AddFrame 从 0 号槽重新开始
        // 上游：PerfSampler.Reset（重开统计时）。
        // 下游：无。
        /// <summary>清空窗口，回到没有样本的预热状态。</summary>
        public void Reset()
        {
            for (int i = 0; i < m_frameSeconds.Length; i++)
            {
                m_frameSeconds[i] = 0f;
            }

            m_nextIndex = 0;
            m_count = 0;
            m_sum = 0f;
        }
    }
}
