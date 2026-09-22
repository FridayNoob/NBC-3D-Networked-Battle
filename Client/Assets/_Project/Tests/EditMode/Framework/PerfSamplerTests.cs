// ============================================================================
//  M1-E1 · PerfSampler 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md E1（**FPS + DrawCall + GC Alloc**）
//
//  ---------------------------------------------------------------------------
//  这组测试的重点：**"读不到"是一种正常状态，必须如实传播**
//  ---------------------------------------------------------------------------
//  发布版里渲染统计拿不到、EditMode 里 Profiler 没在跑 —— 这些都不是错误。
//  真正危险的是**用 0 冒充**：看板显示"DrawCall = 0"，
//  一个**看起来正常的假数字**，比"看不见"难查得多。
//
//  所以有整整一组用例专门盯着 NaN 的传播。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework.Perf;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>可控的假计数器源。</summary>
    internal sealed class FakePerfCounterSource : IPerfCounterSource
    {
        /// <summary>各计数器的值；不在表里 = 读不到。</summary>
        public readonly Dictionary<PerfCounterId, float> Values = new Dictionary<PerfCounterId, float>();

        /// <summary>`Start` 被调用了几次。</summary>
        public int StartCount;

        /// <summary>`Stop` 被调用了几次。</summary>
        public int StopCount;

        /// <summary>开始采集。</summary>
        public void Start()
        {
            StartCount++;
        }

        /// <summary>停止采集。</summary>
        public void Stop()
        {
            StopCount++;
        }

        /// <summary>读一个计数器。</summary>
        /// <param name="id">标识。</param>
        /// <param name="value">值。</param>
        /// <returns>表里有就返回 true。</returns>
        public bool TryRead(PerfCounterId id, out float value)
        {
            return Values.TryGetValue(id, out value);
        }
    }

    /// <summary>
    /// E1：性能采样器的测试。
    /// </summary>
    public sealed class PerfSamplerTests
    {
        private const float SixtyFpsFrame = 1f / 60f;

        private FakePerfCounterSource m_source;

        /// <summary>建一个喂满所有计数器的假源。</summary>
        [SetUp]
        public void SetUp()
        {
            m_source = new FakePerfCounterSource();

            m_source.Values[PerfCounterId.DrawCalls] = 42f;
            m_source.Values[PerfCounterId.SetPassCalls] = 7f;
            m_source.Values[PerfCounterId.Triangles] = 12345f;
            m_source.Values[PerfCounterId.GcAllocatedInFrame] = 1024f;
            m_source.Values[PerfCounterId.TotalMemory] = 200f * 1024f * 1024f;
            m_source.Values[PerfCounterId.MonoUsedMemory] = 50f * 1024f * 1024f;
        }

        // ====================================================================
        //  一、组装
        // ====================================================================

        /// <summary>构造时就开始采集，并把计数器读进快照。</summary>
        [Test]
        public void Tick_AssemblesAllCounters()
        {
            PerfSampler sampler = new PerfSampler(m_source);

            try
            {
                Assert.AreEqual(1, m_source.StartCount, "构造时就该 Start");

                sampler.Tick(SixtyFpsFrame);

                PerfSnapshot snapshot = sampler.Current;

                Assert.AreEqual(60f, snapshot.Fps, 0.01f);
                Assert.AreEqual(42f, snapshot.DrawCalls);
                Assert.AreEqual(7f, snapshot.SetPassCalls);
                Assert.AreEqual(12345f, snapshot.Triangles);
                Assert.AreEqual(1024f, snapshot.GcAllocatedInFrame);
                Assert.AreEqual(200f * 1024f * 1024f, snapshot.TotalMemory);
                Assert.AreEqual(1L, snapshot.FrameCount);
            }
            finally
            {
                sampler.Dispose();
            }
        }

        /// <summary>帧号随 `Tick` 递增。</summary>
        [Test]
        public void Tick_AdvancesFrameCount()
        {
            PerfSampler sampler = new PerfSampler(m_source);

            try
            {
                for (int i = 0; i < 5; i++)
                {
                    sampler.Tick(SixtyFpsFrame);
                }

                Assert.AreEqual(5L, sampler.TickCount);
                Assert.AreEqual(5L, sampler.Current.FrameCount);
            }
            finally
            {
                sampler.Dispose();
            }
        }

        /// <summary>计数器值每帧都会重读（不是只读一次）。</summary>
        [Test]
        public void Tick_RereadsCountersEveryFrame()
        {
            PerfSampler sampler = new PerfSampler(m_source);

            try
            {
                sampler.Tick(SixtyFpsFrame);
                Assert.AreEqual(42f, sampler.Current.DrawCalls);

                m_source.Values[PerfCounterId.DrawCalls] = 100f;

                sampler.Tick(SixtyFpsFrame);
                Assert.AreEqual(100f, sampler.Current.DrawCalls);
            }
            finally
            {
                sampler.Dispose();
            }
        }

        // ====================================================================
        //  二、"读不到"必须如实传播成 NaN
        // ====================================================================

        /// <summary>
        /// **读不到就填 NaN，绝不填 0。**
        /// <para>填 0 的话看板会显示"DrawCall = 0" —— 一个看起来正常的假数字。</para>
        /// </summary>
        [Test]
        public void UnavailableCounter_BecomesNaN_NotEmptyZero()
        {
            m_source.Values.Remove(PerfCounterId.DrawCalls);

            PerfSampler sampler = new PerfSampler(m_source);

            try
            {
                sampler.Tick(SixtyFpsFrame);

                Assert.IsTrue(float.IsNaN(sampler.Current.DrawCalls), "读不到必须是 NaN");
                Assert.IsFalse(sampler.Current.HasRenderStats);
                Assert.Greater(sampler.ReadFailureCount, 0L, "读失败要被计数");
            }
            finally
            {
                sampler.Dispose();
            }
        }

        /// <summary>读失败次数用来区分"根本不可用"和"偶尔读不到"。</summary>
        [Test]
        public void ReadFailureCount_DistinguishesPermanentFromTransient()
        {
            m_source.Values.Remove(PerfCounterId.Triangles);

            PerfSampler sampler = new PerfSampler(m_source);

            try
            {
                // 每次 Tick 有 6 个计数器，少 1 个 → 每帧失败 1 次
                for (int i = 0; i < 10; i++)
                {
                    sampler.Tick(SixtyFpsFrame);
                }

                Assert.AreEqual(10L, sampler.ReadFailureCount, "10 帧 × 每帧缺 1 个");
            }
            finally
            {
                sampler.Dispose();
            }
        }

        /// <summary>一个计数器从"读不到"变成"读得到"之后要恢复正常。</summary>
        [Test]
        public void CounterBecomingAvailable_RecoversFromNaN()
        {
            m_source.Values.Remove(PerfCounterId.DrawCalls);

            PerfSampler sampler = new PerfSampler(m_source);

            try
            {
                sampler.Tick(SixtyFpsFrame);
                Assert.IsTrue(float.IsNaN(sampler.Current.DrawCalls));

                m_source.Values[PerfCounterId.DrawCalls] = 88f;
                sampler.Tick(SixtyFpsFrame);

                Assert.AreEqual(88f, sampler.Current.DrawCalls);
            }
            finally
            {
                sampler.Dispose();
            }
        }

        /// <summary>全部读不到时，快照里应当全是 NaN，但 FPS 仍然正常（它不依赖计数器）。</summary>
        [Test]
        public void AllCountersUnavailable_FpsStillWorks()
        {
            m_source.Values.Clear();

            PerfSampler sampler = new PerfSampler(m_source);

            try
            {
                for (int i = 0; i < 10; i++)
                {
                    sampler.Tick(SixtyFpsFrame);
                }

                Assert.AreEqual(60f, sampler.Current.Fps, 0.01f, "FPS 自己算，不依赖 Profiler");
                Assert.IsTrue(float.IsNaN(sampler.Current.DrawCalls));
                Assert.IsTrue(float.IsNaN(sampler.Current.TotalMemory));
                Assert.IsFalse(sampler.Current.HasRenderStats);
                Assert.IsFalse(sampler.Current.HasMemoryStats);
            }
            finally
            {
                sampler.Dispose();
            }
        }

        /// <summary>"本帧零分配"是一个**有意义的结论**，要能和"读不到"区分开。</summary>
        [Test]
        public void ZeroGcAllocation_IsDistinctFromUnavailable()
        {
            PerfSampler sampler = new PerfSampler(m_source);

            try
            {
                m_source.Values[PerfCounterId.GcAllocatedInFrame] = 0f;
                sampler.Tick(SixtyFpsFrame);

                Assert.IsTrue(sampler.Current.IsGcAllocationFree, "0 分配应当被判为'无分配'");
                Assert.IsFalse(float.IsNaN(sampler.Current.GcAllocatedInFrame));

                m_source.Values.Remove(PerfCounterId.GcAllocatedInFrame);
                sampler.Tick(SixtyFpsFrame);

                Assert.IsFalse(sampler.Current.IsGcAllocationFree, "读不到**不是**'无分配'");
                Assert.IsTrue(float.IsNaN(sampler.Current.GcAllocatedInFrame));
            }
            finally
            {
                sampler.Dispose();
            }
        }

        // ====================================================================
        //  三、生命周期
        // ====================================================================

        /// <summary>构造时 `Start`，`Dispose` 时 `Stop`（各一次）。</summary>
        [Test]
        public void Dispose_StopsTheSourceOnce()
        {
            PerfSampler sampler = new PerfSampler(m_source);

            sampler.Dispose();
            sampler.Dispose();   // 重复 Dispose 应当无害

            Assert.AreEqual(1, m_source.StopCount);
        }

        /// <summary>销毁之后 `Tick` 要报错，而不是静默什么都不做。</summary>
        [Test]
        public void TickAfterDispose_Throws()
        {
            PerfSampler sampler = new PerfSampler(m_source);

            sampler.Dispose();

            Assert.Throws<ObjectDisposedException>(() => sampler.Tick(SixtyFpsFrame));
        }

        /// <summary>来源不能为 null。</summary>
        [Test]
        public void Constructor_NullSource_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new PerfSampler(null));
        }

        /// <summary>`Reset` 清空统计。</summary>
        [Test]
        public void Reset_ClearsStatistics()
        {
            PerfSampler sampler = new PerfSampler(m_source);

            try
            {
                sampler.Tick(SixtyFpsFrame);
                sampler.Reset();

                Assert.AreEqual(0L, sampler.TickCount);
                Assert.AreEqual(0L, sampler.ReadFailureCount);
                Assert.AreEqual(0, sampler.Fps.SampleCount);
                Assert.AreEqual(0f, sampler.Current.Fps);
            }
            finally
            {
                sampler.Dispose();
            }
        }

        // ====================================================================
        //  四、显示格式化（`N/A` 不能变成 `0`）
        // ====================================================================

        /// <summary>`N/A` 的渲染：不可用时必须显示 `N/A`，不能是 `0`。</summary>
        [Test]
        public void Formatting_RendersUnavailableAsNa()
        {
            Assert.AreEqual("N/A", PerfSnapshot.FormatValue(PerfSnapshot.Unavailable));
            Assert.AreEqual("N/A", PerfSnapshot.FormatBytes(PerfSnapshot.Unavailable));

            Assert.AreEqual("42", PerfSnapshot.FormatValue(42f));
            Assert.AreEqual("512 B", PerfSnapshot.FormatBytes(512f));
            Assert.AreEqual("1.0 KB", PerfSnapshot.FormatBytes(1024f));
            Assert.AreEqual("1.0 MB", PerfSnapshot.FormatBytes(1024f * 1024f));
        }

        /// <summary>`ToString` 对不可用的项也显示 `N/A`。</summary>
        [Test]
        public void ToString_ShowsNaForUnavailable()
        {
            PerfSnapshot snapshot = PerfSnapshot.Empty;

            string text = snapshot.ToString();

            StringAssert.Contains("N/A", text);
            StringAssert.DoesNotContain("DC 0", text, "不可用不能显示成 0");
        }

        /// <summary>`PerfSnapshot.Empty` 是"什么都还没有"，不是"数值是 0"。</summary>
        [Test]
        public void EmptySnapshot_ReportsNothingAvailable()
        {
            PerfSnapshot empty = PerfSnapshot.Empty;

            Assert.AreEqual(0f, empty.Fps);
            Assert.AreEqual(0L, empty.FrameCount);
            Assert.IsFalse(empty.HasRenderStats);
            Assert.IsFalse(empty.HasMemoryStats);
            Assert.IsFalse(empty.IsGcAllocationFree);
        }
    }
}
