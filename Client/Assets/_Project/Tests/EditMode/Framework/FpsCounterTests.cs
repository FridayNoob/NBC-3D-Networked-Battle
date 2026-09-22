// ============================================================================
//  M1-E1 · FpsCounter 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md E1（**FPS**）
//
//  ---------------------------------------------------------------------------
//  为什么这个能在 EditMode 测（而且**本来就该**在这里测）
//  ---------------------------------------------------------------------------
//  `FpsCounter` 里**没有一行 UnityEngine** —— 它只接收"这一帧花了多少秒"。
//  所以测试可以**构造任意帧序列**喂进去，断言统计结果，完全确定。
//
//  ⚠️ 如果哪天这些用例需要 PlayMode 才能跑，说明有人往 FpsCounter 里塞了
//     `Time.deltaTime` —— 那它就退化成"只能在真帧里测"了，**测试价值直接归零**。
// ============================================================================

using NBC.Framework.Perf;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// E1：帧率统计的测试。
    /// </summary>
    public sealed class FpsCounterTests
    {
        private const float SixtyFpsFrame = 1f / 60f;
        private const float ThirtyFpsFrame = 1f / 30f;

        /// <summary>还没喂任何帧时，FPS 是 0（不是 NaN）。</summary>
        [Test]
        public void Empty_ReportsZeroFps()
        {
            FpsCounter counter = new FpsCounter();

            Assert.AreEqual(0f, counter.Fps);
            Assert.AreEqual(0f, counter.AverageFrameMilliseconds);
            Assert.AreEqual(0f, counter.WorstFrameMilliseconds);
            Assert.AreEqual(0, counter.SampleCount);
        }

        /// <summary>一帧 1/60 秒 → 60 FPS。</summary>
        [Test]
        public void SingleFrame_ReportsItsRate()
        {
            FpsCounter counter = new FpsCounter();

            counter.AddFrame(SixtyFpsFrame);

            Assert.AreEqual(60f, counter.Fps, 0.01f);
        }

        /// <summary>稳定 60 帧/秒 → 平均也是 60。</summary>
        [Test]
        public void SteadySixty_StaysSixty()
        {
            FpsCounter counter = new FpsCounter();

            for (int i = 0; i < counter.WindowFrames; i++)
            {
                counter.AddFrame(SixtyFpsFrame);
            }

            Assert.AreEqual(60f, counter.Fps, 0.01f);
            Assert.AreEqual(1000f / 60f, counter.AverageFrameMilliseconds, 0.01f);
            Assert.AreEqual(1000f / 60f, counter.WorstFrameMilliseconds, 0.01f);
        }

        /// <summary>
        /// **滑动**：窗口里全是快帧时是 60，之后连续喂慢帧，平均会**渐变**到 30 ——
        /// 而不是一帧就跳过去（那正是"用瞬时值"的毛病）。
        /// </summary>
        [Test]
        public void Window_SlidesGraduallyTowardNewRate()
        {
            FpsCounter counter = new FpsCounter(windowFrames: 10);

            for (int i = 0; i < 10; i++)
            {
                counter.AddFrame(SixtyFpsFrame);
            }

            Assert.AreEqual(60f, counter.Fps, 0.01f);

            // 换成一帧 30 FPS 的节奏，喂满整个窗口
            for (int i = 0; i < 10; i++)
            {
                counter.AddFrame(ThirtyFpsFrame);

                // 中途应当落在 30~60 之间（正在被替换）
                Assert.Greater(counter.Fps, 29f, "第 " + i + " 帧时不该掉到 30 以下");
            }

            Assert.AreEqual(30f, counter.Fps, 0.01f, "窗口被换满之后应当稳定在 30");
        }

        /// <summary>窗口容量有上限：样本数不会超过窗口帧数。</summary>
        [Test]
        public void SampleCount_IsCappedByWindow()
        {
            FpsCounter counter = new FpsCounter(windowFrames: 5);

            for (int i = 0; i < 50; i++)
            {
                counter.AddFrame(SixtyFpsFrame);
            }

            Assert.AreEqual(5, counter.SampleCount);
            Assert.AreEqual(5, counter.WindowFrames);
        }

        /// <summary>窗口只保留最近的帧（旧的被挤掉）。</summary>
        [Test]
        public void Window_DropsOldestFrames()
        {
            FpsCounter counter = new FpsCounter(windowFrames: 4);

            // 先喂 4 帧极慢（1 秒），再喂 4 帧 60 FPS —— 慢帧应当全被挤掉
            for (int i = 0; i < 4; i++)
            {
                counter.AddFrame(1f);
            }

            Assert.AreEqual(1f, counter.Fps, 0.01f);

            for (int i = 0; i < 4; i++)
            {
                counter.AddFrame(SixtyFpsFrame);
            }

            Assert.AreEqual(60f, counter.Fps, 0.01f, "慢帧应当已经被挤出窗口");
        }

        /// <summary>非正的帧耗时被忽略（暂停帧、或者调用方算错了）。</summary>
        [Test]
        public void NonPositiveDelta_IsIgnored()
        {
            FpsCounter counter = new FpsCounter();

            counter.AddFrame(0f);
            counter.AddFrame(-1f);

            Assert.AreEqual(0, counter.SampleCount);
            Assert.AreEqual(0f, counter.Fps);
        }

        /// <summary>
        /// **超长帧被夹到 1 秒**：编辑器断点停一下、切出去再回来，单帧可能"耗时"好几秒。
        /// 不夹的话那一帧会**污染整个窗口**，接下来一整窗都显示成 0.2 FPS。
        /// </summary>
        [Test]
        public void HugeFrame_IsClampedSoItDoesNotPoisonTheWindow()
        {
            FpsCounter counter = new FpsCounter(windowFrames: 4);

            counter.AddFrame(10f);   // 模拟编辑器里停了 10 秒

            Assert.AreEqual(1000f, counter.WorstFrameMilliseconds, 0.1f,
                "应当被夹到 1 秒（1000ms），而不是 10000ms");
            Assert.AreEqual(1f, counter.Fps, 0.01f);
        }

        /// <summary>
        /// **平均值会掩盖卡顿，最差值不会** ——
        /// 平均 60 FPS 完全可以藏着每 3 秒一次 200ms 的卡顿，所以两样都要看。
        /// </summary>
        [Test]
        public void WorstFrame_ExposesHitchesThatAverageHides()
        {
            FpsCounter counter = new FpsCounter(windowFrames: 60);

            for (int i = 0; i < 59; i++)
            {
                counter.AddFrame(SixtyFpsFrame);
            }

            counter.AddFrame(0.2f);   // 一帧卡了 200ms

            // 平均仍然接近 60 —— 这一帧只把平均值拉低一点点
            Assert.Greater(counter.Fps, 55f, "平均值应当仍然很高");
            Assert.AreEqual(200f, counter.WorstFrameMilliseconds, 0.1f,
                "最差帧必须如实反映那次卡顿");
        }

        /// <summary>`Reset` 之后回到初始状态。</summary>
        [Test]
        public void Reset_ClearsEverything()
        {
            FpsCounter counter = new FpsCounter();

            for (int i = 0; i < 10; i++)
            {
                counter.AddFrame(SixtyFpsFrame);
            }

            counter.Reset();

            Assert.AreEqual(0, counter.SampleCount);
            Assert.AreEqual(0f, counter.Fps);
            Assert.AreEqual(0f, counter.WorstFrameMilliseconds);
        }

        /// <summary>窗口帧数非法时退回默认值（而不是建一个 0 长度数组然后除零）。</summary>
        [Test]
        public void InvalidWindowSize_FallsBackToDefault()
        {
            Assert.AreEqual(FpsCounter.DefaultWindowFrames, new FpsCounter(0).WindowFrames);
            Assert.AreEqual(FpsCounter.DefaultWindowFrames, new FpsCounter(-5).WindowFrames);
        }

        /// <summary>抖动的一串帧：平均应当落在合理范围（不是被某一帧主导）。</summary>
        [Test]
        public void NoisyFrames_AverageIsReasonable()
        {
            FpsCounter counter = new FpsCounter(windowFrames: 100);

            // 在 16ms 附近抖动 ±3ms
            float[] noise = { 13f, 19f, 15f, 18f, 16f, 14f, 17f, 16f, 20f, 12f };

            for (int i = 0; i < 100; i++)
            {
                counter.AddFrame(noise[i % noise.Length] / 1000f);
            }

            // 平均约 16ms → 约 62.5 FPS
            Assert.Greater(counter.Fps, 55f);
            Assert.Less(counter.Fps, 70f);

            // 最差的一帧是 20ms
            Assert.AreEqual(20f, counter.WorstFrameMilliseconds, 0.1f);
        }
    }
}
