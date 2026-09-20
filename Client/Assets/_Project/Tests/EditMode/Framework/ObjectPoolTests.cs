// ============================================================================
//  M1-A2 · 通用对象池的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2 / V8
//  对应记录：Docs/06-框架改造记录.md §三 FW-04 / FW-05、§四 P-04 / P-11
//
//  为什么这些在 EditMode：`ObjectPool<T>` 是**纯 C#** 类，不碰 MonoBehaviour 生命周期，
//  所以不依赖播放模式（这条划分是 A1 用实验定下来的，见 Docs/06 §9.7）。
//  GameObject 池因为要碰 SetActive / 父子关系 / 销毁，放在 PlayMode。
// ============================================================================

using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using NBC.Framework;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace NBC.Tests.EditMode
{
    /// <summary>可实现池钩子的测试对象。</summary>
    public sealed class ProbePoolItem : IPoolable
    {
        public int SpawnedCount;
        public int DespawnedCount;

        public void OnSpawned()
        {
            SpawnedCount++;
        }

        public void OnDespawned()
        {
            DespawnedCount++;
        }
    }

    /// <summary>带"失效"标记的测试对象，用于验证池的有效性校验。</summary>
    public sealed class ProbeValidityItem
    {
        public bool Dead;
    }

    [TestFixture]
    public class ObjectPoolTests
    {
        [SetUp]
        public void SetUp()
        {
            PoolRegistry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            PoolRegistry.Clear();
        }

        // --------------------------------------------------------------------
        //  基础复用（池存在的意义）
        // --------------------------------------------------------------------

        [Test]
        public void Get_WhenPoolEmpty_CreatesViaFactory()
        {
            ObjectPool<object> pool = new ObjectPool<object>("t", () => new object());

            object a = pool.Get();

            Assert.IsNotNull(a);
            Assert.AreEqual(1, pool.GetStats().TotalCreated);
            Assert.AreEqual(1, pool.CountInUse);
            Assert.AreEqual(0, pool.CountInPool);
        }

        [Test]
        public void Release_ThenGet_ReusesSameInstance()
        {
            ObjectPool<object> pool = new ObjectPool<object>("t", () => new object(), maxSize: 4);

            object a = pool.Get();
            pool.Release(a);
            object b = pool.Get();

            Assert.AreSame(a, b, "归还后应当复用同一个对象，而不是新建");
            Assert.AreEqual(1, pool.GetStats().TotalCreated, "整个过程只应新建 1 个对象");
        }

        [Test]
        public void Get_ThenReleaseRepeatedly_DoesNotGrow()
        {
            ObjectPool<ProbePoolItem> pool =
                new ObjectPool<ProbePoolItem>("t", () => new ProbePoolItem(), maxSize: 4);

            for (int i = 0; i < 100; i++)
            {
                ProbePoolItem item = pool.Get();
                pool.Release(item);
            }

            PoolStats stats = pool.GetStats();
            Assert.AreEqual(1, stats.TotalCreated, "100 次取用只应新建 1 个对象");
            Assert.AreEqual(100, stats.TotalSpawned);
            Assert.AreEqual(100, stats.TotalDespawned);
            Assert.AreEqual(1, stats.InPool);
        }

        // --------------------------------------------------------------------
        //  FW-05：容量上限
        // --------------------------------------------------------------------

        [Test]
        public void Release_BeyondMaxSize_DropsInsteadOfGrowing()
        {
            int destroyed = 0;
            ObjectPool<object> pool = new ObjectPool<object>(
                "cap", () => new object(), maxSize: 2, initialSize: 0, destroyer: _ => destroyed++);

            object[] items = new object[5];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = pool.Get();
            }

            for (int i = 0; i < items.Length; i++)
            {
                pool.Release(items[i]);
            }

            Assert.AreEqual(2, pool.CountInPool, "池内闲置数不应该超过上限");
            Assert.AreEqual(3, destroyed, "超出上限的 3 个应被交给 destroyer");
            Assert.AreEqual(3, pool.GetStats().TotalDestroyed);
        }

        [Test]
        public void Prewarm_RespectsMaxSize()
        {
            ObjectPool<object> pool = new ObjectPool<object>("t", () => new object(), maxSize: 3);

            pool.Prewarm(10);

            Assert.AreEqual(3, pool.CountInPool);
            Assert.AreEqual(3, pool.GetStats().TotalCreated);
        }

        [Test]
        public void Prewarm_CreatesRequestedCount()
        {
            ObjectPool<object> pool = new ObjectPool<object>("t", () => new object());

            pool.Prewarm(8);

            Assert.AreEqual(8, pool.CountInPool);
            Assert.AreEqual(8, pool.GetStats().TotalCreated);
            Assert.AreEqual(8, pool.GetStats().PeakInPool);
        }

        // --------------------------------------------------------------------
        //  P-04：Clear 必须真的清理
        // --------------------------------------------------------------------

        [Test]
        public void Clear_DestroysIdleItems()
        {
            int destroyed = 0;
            ObjectPool<object> pool = new ObjectPool<object>(
                "clear", () => new object(), maxSize: 0, initialSize: 0, destroyer: _ => destroyed++);

            pool.Prewarm(5);
            Assert.AreEqual(5, pool.CountInPool);

            pool.Clear(true);

            Assert.AreEqual(0, pool.CountInPool);
            Assert.AreEqual(5, destroyed, "原版 Clear 只清字典、对象变孤儿；这里必须真的清理");
            Assert.AreEqual(5, pool.GetStats().TotalDestroyed);
        }

        [Test]
        public void Clear_DoesNotTouchInUseItems_AndTheyReturnNormally()
        {
            ObjectPool<object> pool = new ObjectPool<object>("clear2", () => new object(), maxSize: 8);

            object outside = pool.Get();
            pool.Prewarm(3);

            pool.Clear(true);

            Assert.AreEqual(0, pool.CountInPool, "闲置的被清掉");
            Assert.AreEqual(1, pool.CountInUse, "在外的对象所有权不在池手上，不能动");

            // 之后正常归还：进的是一个已经空掉的池，这是安全且正常的
            pool.Release(outside);
            Assert.AreEqual(1, pool.CountInPool);
        }

        // --------------------------------------------------------------------
        //  重复归还 / 非法归还：必须当场报错，不静默损坏
        // --------------------------------------------------------------------

        [Test]
        public void Release_SameObjectTwice_LogsErrorAndKeepsPoolConsistent()
        {
            ObjectPool<object> pool = new ObjectPool<object>("dup", () => new object(), maxSize: 4);

            object a = pool.Get();
            pool.Release(a);

            // 重复归还会让同一个对象进池两次，之后被两个使用者同时拿到 —— 极难查的 Bug。
            // 这里要求它**当场报错**。
            LogAssert.Expect(LogType.Error, new Regex("重复归还"));
            pool.Release(a);

            Assert.AreEqual(1, pool.CountInPool, "非法归还不得改变池的状态");
            Assert.AreEqual(0, pool.CountInUse, "非法归还不得让池以为对象还在使用中");
        }

        [Test]
        public void Release_ForeignObject_LogsError()
        {
            ObjectPool<object> pool = new ObjectPool<object>("foreign", () => new object(), maxSize: 4);

            LogAssert.Expect(LogType.Error, new Regex("重复归还"));
            pool.Release(new object());

            Assert.AreEqual(0, pool.CountInPool);
        }

        [Test]
        public void Release_Null_LogsError()
        {
            ObjectPool<object> pool = new ObjectPool<object>("null", () => new object(), maxSize: 4);

            LogAssert.Expect(LogType.Error, new Regex("Release\\(null\\)"));
            pool.Release(null);

            Assert.AreEqual(0, pool.CountInPool);
        }

        // --------------------------------------------------------------------
        //  有效性校验：池里的对象可能被外部销毁
        // --------------------------------------------------------------------

        [Test]
        public void Get_SkipsItemsThatBecameInvalid()
        {
            ObjectPool<ProbeValidityItem> pool = new ObjectPool<ProbeValidityItem>(
                "valid", () => new ProbeValidityItem(), maxSize: 4,
                destroyer: null, isValid: item => !item.Dead);

            ProbeValidityItem a = pool.Get();
            a.Dead = true;          // 模拟"被外部销毁"
            pool.Release(a);

            ProbeValidityItem b = pool.Get();

            Assert.AreNotSame(a, b, "已失效的对象不应该被复用");
            PoolStats stats = pool.GetStats();
            Assert.AreEqual(1, stats.TotalDestroyed, "失效对象应被丢弃并计数");
            Assert.AreEqual(2, stats.TotalCreated, "应新建一个补上");
        }

        // --------------------------------------------------------------------
        //  IPoolable 钩子
        // --------------------------------------------------------------------

        [Test]
        public void Poolable_HooksAreCalledOnGetAndRelease()
        {
            ObjectPool<ProbePoolItem> pool =
                new ObjectPool<ProbePoolItem>("hooks", () => new ProbePoolItem(), maxSize: 4);

            ProbePoolItem item = pool.Get();
            Assert.AreEqual(1, item.SpawnedCount, "取出时应触发 OnSpawned");
            Assert.AreEqual(0, item.DespawnedCount);

            pool.Release(item);
            Assert.AreEqual(1, item.DespawnedCount, "归还时应触发 OnDespawned");

            ProbePoolItem again = pool.Get();
            Assert.AreSame(item, again);
            Assert.AreEqual(2, again.SpawnedCount, "复用时应再次触发 OnSpawned（复位机会）");
        }

        // --------------------------------------------------------------------
        //  统计（验收 V8：面板要能看复用率）
        // --------------------------------------------------------------------

        [Test]
        public void Stats_ReuseRate_ReflectsActualReuse()
        {
            ObjectPool<object> pool = new ObjectPool<object>("stats", () => new object(), maxSize: 4);

            // 只取出、不归还：每次都得新建，复用率应为 0
            object x = pool.Get();
            object y = pool.Get();
            Assert.AreEqual(0f, pool.GetStats().ReuseRate, 0.0001f);

            // 归还两个（池里就有货了），再取两次：这两次全是复用
            pool.Release(x);
            pool.Release(y);
            pool.Get();
            pool.Get();

            PoolStats stats = pool.GetStats();
            Assert.AreEqual(2, stats.TotalCreated, "整个过程只应新建 2 个");
            Assert.AreEqual(4, stats.TotalSpawned);
            Assert.AreEqual(0.5f, stats.ReuseRate, 0.0001f, "4 次取用里 2 次是复用");
            Assert.IsTrue(stats.ToString().Contains("%"), "ToString 应给面板一行可读摘要");
        }

        [Test]
        public void Stats_PeakInUse_IsTracked()
        {
            ObjectPool<object> pool = new ObjectPool<object>("peak", () => new object());

            List<object> hold = new List<object>();
            for (int i = 0; i < 7; i++)
            {
                hold.Add(pool.Get());
            }

            for (int i = 0; i < hold.Count; i++)
            {
                pool.Release(hold[i]);
            }

            Assert.AreEqual(7, pool.GetStats().PeakInUse);
            Assert.AreEqual(0, pool.GetStats().InUse);
        }

        // --------------------------------------------------------------------
        //  生命周期：Dispose 必须注销登记（否则名单里留强引用）
        // --------------------------------------------------------------------

        [Test]
        public void Dispose_UnregistersFromRegistry()
        {
            int before = PoolRegistry.Count;

            ObjectPool<object> pool = new ObjectPool<object>("dispose", () => new object());
            Assert.AreEqual(before + 1, PoolRegistry.Count, "池创建时应自愿登记，供统计面板枚举");

            pool.Dispose();
            Assert.AreEqual(before, PoolRegistry.Count, "Dispose 之后不应残留登记（否则是强引用泄漏）");
        }

        [Test]
        public void Dispose_ClearsIdleItems_AndFurtherUseThrows()
        {
            int destroyed = 0;
            ObjectPool<object> pool = new ObjectPool<object>(
                "dispose2", () => new object(), maxSize: 0, initialSize: 3, destroyer: _ => destroyed++);

            Assert.AreEqual(3, pool.CountInPool);

            pool.Dispose();

            Assert.AreEqual(3, destroyed);
            Assert.Throws<System.ObjectDisposedException>(() => pool.Get());
            Assert.Throws<System.ObjectDisposedException>(() => pool.Release(new object()));
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            ObjectPool<object> pool = new ObjectPool<object>("dispose3", () => new object());
            pool.Dispose();
            Assert.DoesNotThrow(() => pool.Dispose(), "重复 Dispose 不应报错");
        }

        // --------------------------------------------------------------------
        //  FW-04 的证据：O(1) 对比 O(n) 基线
        // --------------------------------------------------------------------

        /// <summary>
        /// 把"改造前"和"改造后"放在同一个进程里对比。
        /// <para>
        /// 基线是原版 `PoolData.GetObj` 的写法：`poolList[0]` 取值 + `RemoveAt(0)` 改列表。
        /// 后者要把后面所有元素整体前移，于是"取出 N 次"的总代价是 O(N²)。
        /// 改造后用 `Stack` 的 Pop/Push，均摊 O(1)。
        /// </para>
        /// <para>
        /// 断言留了 5 倍余量：不是测"快多少"，而是测"**渐进复杂度不同**"这个事实。
        /// 两者在这个 N 上相差三个数量级，5 倍余量足以排除机器噪声，不会变成偶发红灯。
        /// </para>
        /// </summary>
        [Test]
        public void GetRelease_IsFarFasterThan_ListRemoveAtZeroBaseline()
        {
            const int N = 20000;

            // ---- 基线：List 头取 + RemoveAt(0)（= 原版做法） ----
            List<object> naive = new List<object>(N);
            for (int i = 0; i < N; i++)
            {
                naive.Add(new object());
            }

            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                object o = naive[0];
                naive.RemoveAt(0);
                naive.Add(o);
            }

            sw.Stop();
            long naiveTicks = sw.ElapsedTicks;

            // ---- 改造后：Stack 池 ----
            ObjectPool<object> pool = new ObjectPool<object>("bench", () => new object(), maxSize: N);
            pool.Prewarm(N);

            sw.Restart();
            for (int i = 0; i < N; i++)
            {
                object o = pool.Get();
                pool.Release(o);
            }

            sw.Stop();
            long poolTicks = sw.ElapsedTicks;
            pool.Dispose();

            double times = poolTicks > 0 ? (double)naiveTicks / poolTicks : double.MaxValue;
            Debug.Log("[POOL-BENCH] N=" + N +
                      "  List.RemoveAt(0) 基线=" + naiveTicks + " ticks" +
                      "  Stack 池=" + poolTicks + " ticks" +
                      "  快了 " + times.ToString("F1") + " 倍");

            Assert.Less(poolTicks * 5, naiveTicks,
                "Stack 池应当比 List.RemoveAt(0) 快至少 5 倍（N=" + N +
                "：基线 " + naiveTicks + " ticks, 池 " + poolTicks + " ticks）");
        }

        // --------------------------------------------------------------------
        //  注册表本身
        // --------------------------------------------------------------------

        [Test]
        public void Registry_CopyTo_ReturnsRegisteredPools()
        {
            ObjectPool<object> a = new ObjectPool<object>("ra", () => new object());
            ObjectPool<object> b = new ObjectPool<object>("rb", () => new object());

            List<IPoolStatsSource> buffer = new List<IPoolStatsSource>();
            PoolRegistry.CopyTo(buffer);

            Assert.AreEqual(2, buffer.Count);
            Assert.IsTrue(buffer.Contains(a));
            Assert.IsTrue(buffer.Contains(b));

            a.Dispose();
            b.Dispose();
        }
    }
}
