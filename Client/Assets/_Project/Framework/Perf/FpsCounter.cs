// ============================================================================
//  NBC.Framework.Perf · 帧率计数（**纯逻辑，不依赖 Unity**）
//  对应需求：FW-M12（FPS）
//
//  ---------------------------------------------------------------------------
//  为什么不能直接显示 `1 / Time.deltaTime`
//  ---------------------------------------------------------------------------
//  那个值是**瞬时**的：一帧 16.6ms、下一帧 17.1ms，显示出来的 FPS 就在 58~60 之间
//  疯狂跳动，**根本看不清**。而且它会被单帧的抖动完全主导。
//
//  所以要看的是**一段时间内的平均** —— 这就是"滑动窗口"：
//  保留最近 N 帧的耗时，用 `N / Σ耗时` 算平均 FPS。
//
//  ⚠️ 另外单看平均会把"偶尔卡一下"抹平（平均 60 但每 3 秒卡 200ms）。
//     所以还提供 `WorstFrameMilliseconds` —— **卡顿要看最差的那一帧，不是平均**。
//
//  ---------------------------------------------------------------------------
//  这个类是纯逻辑（没有一行 UnityEngine），所以 EditMode 能直接测：
//      喂一串构造好的帧耗时 → 断言 FPS
//  不需要真跑一帧。这和 A5 的 `Tick(deltaTime)`、A10 的帧号是同一个手法。
// ============================================================================

namespace NBC.Framework.Perf
{
    /// <summary>
    /// 滑动窗口帧率统计。
    /// </summary>
    public sealed class FpsCounter
    {
        /// <summary>默认窗口大小（帧）。</summary>
        public const int DefaultWindowFrames = 60;

        /// <summary>
        /// 单帧耗时的**上限**（秒）。
        /// <para>
        /// ⚠️ 为什么要夹住：编辑器里断点停一下、切出去再切回来，
        /// 单帧可能"耗时"好几秒。不夹的话，那一帧会**污染整个窗口** ——
        /// 接下来一整个窗口的 FPS 都显示成 0.2，看着像卡死了。
        /// 夹到 1 秒相当于"这一帧很慢"，但不会毁掉统计。
        /// </para>
        /// </summary>
        public const float MaxTrackedFrameSeconds = 1f;

        private readonly float[] m_frameSeconds;

        private int m_nextIndex;
        private int m_count;
        private float m_sum;

        /// <summary>构造。</summary>
        /// <param name="windowFrames">窗口帧数；小于 1 时用默认值。</param>
        public FpsCounter(int windowFrames = DefaultWindowFrames)
        {
            m_frameSeconds = new float[windowFrames >= 1 ? windowFrames : DefaultWindowFrames];
        }

        /// <summary>窗口帧数。</summary>
        public int WindowFrames
        {
            get { return m_frameSeconds.Length; }
        }

        /// <summary>窗口里现在有多少个样本（小于窗口帧数说明还在预热）。</summary>
        public int SampleCount
        {
            get { return m_count; }
        }

        /// <summary>
        /// 平均帧率。
        /// <para>还没有样本时是 0（而不是 `NaN` —— 显示层直接用就行）。</para>
        /// </summary>
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

        /// <summary>窗口内的平均帧耗时（毫秒）。</summary>
        public float AverageFrameMilliseconds
        {
            get { return m_count == 0 ? 0f : (m_sum / m_count) * 1000f; }
        }

        /// <summary>
        /// 窗口内**最慢那一帧**的耗时（毫秒）。
        /// <para>⚠️ 看卡顿要看它，不能只看平均值 —— 平均 60 FPS 完全可以掩盖每 3 秒卡 200ms。</para>
        /// </summary>
        public float WorstFrameMilliseconds
        {
            get
            {
                if (m_count == 0)
                {
                    return 0f;
                }

                float worst = 0f;

                // 只有窗口大小这个量级（默认 60），每次算一遍的开销可以忽略
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

        /// <summary>
        /// 记一帧。
        /// </summary>
        /// <param name="deltaSeconds">这一帧花了多少秒。</param>
        public void AddFrame(float deltaSeconds)
        {
            // 非正数没有意义（暂停帧、或者调用方算错了）—— 丢掉而不是记进去，
            // 否则会出现"除以 0"或者"负耗时"这种说不清的统计
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
                // 窗口满了：把最旧的那个换掉
                m_sum -= m_frameSeconds[m_nextIndex];
                m_frameSeconds[m_nextIndex] = deltaSeconds;
                m_sum += deltaSeconds;
            }

            m_nextIndex = (m_nextIndex + 1) % m_frameSeconds.Length;
        }

        /// <summary>清空统计。</summary>
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
