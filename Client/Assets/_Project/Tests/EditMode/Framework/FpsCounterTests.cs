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
        /// **平均值会掩盖卡顿，最差值不会** —— 这是这一组里最该记住的一条。
        /// <para>
        /// ⚠️ 这条测试的期望值是**算出来的，不是估的**（第一版我拍了个 `> 55`，
        /// 结果实际是 50.7 —— 因为 200ms 卡顿在**1 秒窗口**里要掉掉约 9 FPS）。
        /// </para>
        /// <para>
        /// 算术：5 秒窗口（300 帧）= 299 帧 × 1/60 + 1 帧 × 0.2s = 4.983 + 0.2 = 5.183s
        /// ⇒ FPS = 300 / 5.183 ≈ 57.9。**平均只掉了 2 FPS**，但那一帧实实在在是 200ms。
        /// </para>
        /// <para>
        /// 📌 窗口越短，单次卡顿越掩盖不住：
        /// 同样的 200ms 卡顿，在 **1 秒窗口**里会让 FPS 掉到 50.7，在 **5 秒窗口**里只掉到 57.9。
        /// 所以"看平均"永远只能看出持续性的问题，**突发卡顿必须看最差帧**。
        /// </para>
        /// </summary>
        [Test]
        public void WorstFrame_ExposesHitchesThatAverageHides()
        {
            // 5 秒窗口：让"平均"有机会掩盖一次突发卡顿
            FpsCounter counter = new FpsCounter(windowFrames: 300);

            for (int i = 0; i < 299; i++)
            {
                counter.AddFrame(SixtyFpsFrame);
            }

            counter.AddFrame(0.2f);   // 一帧卡了 200ms

            // 算出来的期望：300 / (299/60 + 0.2) ≈ 57.9
            Assert.Greater(counter.Fps, 57f, "5 秒窗口下，一次 200ms 卡顿只让平均掉约 2 FPS");
            Assert.Less(counter.Fps, 60f, "但确实掉了一点，不是完全没影响");

            Assert.AreEqual(200f, counter.WorstFrameMilliseconds, 0.1f,
                "⚠️ 最差帧必须如实报出那次卡顿 —— 光看平均值你会以为一切正常");
        }

        /// <summary>
        /// **窗口长短本身就是一个取舍**，这条把它钉住：
        /// 窗口短 → 反应快但数字抖；窗口长 → 数字稳但**会把突发卡顿抹平**。
        /// <para>
        /// 所以"该用多长的窗口"没有标准答案 —— 但**必须同时看最差帧**，
        /// 否则长窗口下你会以为一切正常。
        /// </para>
        /// </summary>
        [Test]
        public void WindowLength_TradesResponsivenessAgainstHitchVisibility()
        {
            // 同一个 200ms 卡顿，分别放进 60 帧窗口和 600 帧窗口
            float shortWindowFps = FpsWithOneHitch(windowFrames: 60);
            float longWindowFps = FpsWithOneHitch(windowFrames: 600);

            // 60 帧 ≈ 1 秒：卡顿占掉 1/6 的时间 → 掉到约 50.7
            Assert.AreEqual(50.7f, shortWindowFps, 0.2f);

            // 600 帧 ≈ 10 秒：卡顿只占 2% → 平均几乎看不出来
            Assert.Greater(longWindowFps, 59f, "长窗口下平均值几乎看不出这次卡顿");

            Assert.Less(shortWindowFps, longWindowFps,
                "窗口越短，单次卡顿在平均里占比越大、越藏不住");
        }

        /// <summary>
        /// 造一个"满是 60 FPS 的帧 + 最后卡一帧 200ms"的序列，返回平均 FPS。
        /// </summary>
        /// <param name="windowFrames">窗口帧数。</param>
        /// <returns>平均 FPS。</returns>
        private static float FpsWithOneHitch(int windowFrames)
        {
            FpsCounter counter = new FpsCounter(windowFrames);

            for (int i = 0; i < windowFrames - 1; i++)
            {
                counter.AddFrame(SixtyFpsFrame);
            }

            counter.AddFrame(0.2f);

            return counter.Fps;
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
