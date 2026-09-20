// ============================================================================
//  M1-A1 · 纯 C# 单例的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2（FW-01、FW-M01）
//  对应记录：Docs/06-框架改造记录.md §三 FW-01、§9.7
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么这个文件里【只有纯 C# 单例】，没有 SingletonMono / SingletonAutoMono
//  ---------------------------------------------------------------------------
//  2026-09-20 实测（`AwakeDispatchDiagnostic.cs`，5 个对照）：
//
//      [DIAG] A | Awake 调用次数 = 0 | 普通 MonoBehaviour + private Awake（基线）
//      [DIAG] B | Awake 调用次数 = 0 | 非泛型基类 + private Awake
//      [DIAG] C | Awake 调用次数 = 0 | 泛型基类 + private Awake
//      [DIAG] D | Awake 调用次数 = 0 | 泛型基类 + protected virtual Awake
//      [DIAG] E | Awake 调用次数 = 0 | 非泛型宿主 + 泛型中间层 + private Awake
//
//  **连基线 A 都是 0** —— 说明 EditMode 下 `AddComponent` 根本不触发 `Awake`
//  （编辑模式没有玩家循环）。这不是 bug，是 Unity 的设计。
//
//  于是得到一个必须记住的划分：
//      EditMode  -> 纯逻辑 / 纯 C# / 算法 / 配置。**不碰 MonoBehaviour 生命周期。**
//      PlayMode  -> Awake / OnEnable / Start / Update / 协程 / DontDestroyOnLoad / 物理。
//
//  最初我把 MonoBehaviour 的用例写在 EditMode 里，4 个用例红了；那不是代码错，
//  是我把测试放错了环境。相关用例已移到
//  `Tests\PlayMode\Framework\SingletonMonoTests.cs`。
//
//  教训（写在这里，因为下次还会有人犯）：**基线塌了的对照实验没有信息量** ——
//  第一版实验 A~E 全 0，五组被同一个因素压平，从里面读不出任何关于"泛型基类"的结论。
//  正确反应不是"从 0 里读出结论"，而是换一个基线能立起来的环境重做。
// ============================================================================

using System.Threading.Tasks;
using NBC.Framework;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>纯 C# 单例的测试替身。计数器用来验证 OnInit / OnDispose 被调用了几次。</summary>
    public sealed class ProbePureSingleton : Singleton<ProbePureSingleton>
    {
        public int InitCount;
        public int DisposeCount;

        protected override void OnInit()
        {
            InitCount++;
        }

        protected override void OnDispose()
        {
            DisposeCount++;
        }
    }

    [TestFixture]
    public class SingletonTests
    {
        [SetUp]
        public void SetUp()
        {
            // EditMode 里不会发生"进入 Play 模式"的自动重置，所以用例之间靠手动清状态隔离
            SingletonRegistry.ResetAll();
        }

        [TearDown]
        public void TearDown()
        {
            SingletonRegistry.ResetAll();
        }

        // --------------------------------------------------------------------
        //  FW-01：唯一性
        // --------------------------------------------------------------------

        [Test]
        public void GetInstance_CalledManyTimes_ReturnsSameInstance()
        {
            ProbePureSingleton a = ProbePureSingleton.GetInstance();
            ProbePureSingleton b = ProbePureSingleton.Instance;
            ProbePureSingleton c = ProbePureSingleton.GetInstance();

            Assert.IsNotNull(a, "GetInstance 不应该返回 null");
            Assert.AreSame(a, b, "Instance 与 GetInstance 必须返回同一个对象");
            Assert.AreSame(a, c, "多次调用必须返回同一个对象");
        }

        [Test]
        public void GetInstance_FirstAccess_RunsOnInitExactlyOnce()
        {
            ProbePureSingleton a = ProbePureSingleton.Instance;
            ProbePureSingleton b = ProbePureSingleton.Instance;

            Assert.AreSame(a, b);
            Assert.AreEqual(1, a.InitCount, "OnInit 只应在第一次创建时执行一次");
        }

        // --------------------------------------------------------------------
        //  FW-01 的核心：外部 new 必须失败
        // --------------------------------------------------------------------

        [Test]
        public void NewOutsideSingleton_AlwaysThrows()
        {
            // 原版 new 出来的对象与单例不是同一个，且不报错。改造后要求在调用点抛异常。
            Assert.Throws<System.InvalidOperationException>(() => new ProbePureSingleton());
        }

        [Test]
        public void NewOutsideSingleton_EvenBeforeAnyAccess_Throws()
        {
            // 这一条专门堵"第一次访问就是外部 new"的漏洞。
            // 早先一版实现用 "已经创建过就不许再 new" 做判断，这种情况下会放行 new，
            // 随后 Instance 又创建自己的那一个，于是【静默地】出现两个实例。
            // 现在的实现用 [ThreadStatic] 的"内部创建中"标记，把这个洞堵住了。
            SingletonRegistry.ResetAll();
            Assert.Throws<System.InvalidOperationException>(() => new ProbePureSingleton());
        }

        // --------------------------------------------------------------------
        //  FW-M01：释放、场景重载（Domain Reload 关闭）、线程安全
        // --------------------------------------------------------------------

        [Test]
        public void DisposeInstance_ClearsState_AndAllowsRecreation()
        {
            ProbePureSingleton first = ProbePureSingleton.Instance;
            ProbePureSingleton.DisposeInstance();

            Assert.IsFalse(ProbePureSingleton.HasInstance, "释放后不应再持有实例");
            Assert.AreEqual(1, first.DisposeCount, "OnDispose 应被调用一次");

            ProbePureSingleton second = ProbePureSingleton.Instance;
            Assert.AreNotSame(first, second, "释放后再次访问应创建新实例");
            Assert.AreEqual(1, second.InitCount);
        }

        [Test]
        public void ResetAll_SimulatesDomainReloadDisabled_AndClearsStaticState()
        {
            // 关闭 Domain Reload（Enter Play Mode Options）后，静态字段会跨 Play 会话存活。
            // SingletonRegistry 在 SubsystemRegistration 阶段清空它们，这里用 ResetAll 模拟。
            ProbePureSingleton before = ProbePureSingleton.Instance;
            Assert.IsTrue(ProbePureSingleton.HasInstance);

            SingletonRegistry.ResetAll();

            Assert.IsFalse(ProbePureSingleton.HasInstance, "重置后不应残留上一次运行的实例");
            ProbePureSingleton after = ProbePureSingleton.Instance;
            Assert.AreNotSame(before, after);
        }

        [Test]
        public void Instance_AccessedFromManyThreads_ReturnsSameInstance()
        {
            const int ThreadCount = 64;
            ProbePureSingleton[] results = new ProbePureSingleton[ThreadCount];

            Parallel.For(0, ThreadCount, i =>
            {
                results[i] = ProbePureSingleton.Instance;
            });

            Assert.IsNotNull(results[0]);
            for (int i = 1; i < results.Length; i++)
            {
                Assert.AreSame(results[0], results[i], "并发访问必须拿到同一个实例（第 " + i + " 个不同）");
            }

            Assert.AreEqual(1, results[0].InitCount, "并发下 OnInit 也只能执行一次");
        }
    }
}
