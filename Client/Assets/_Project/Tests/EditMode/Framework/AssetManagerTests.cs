// ============================================================================
//  M1-B2 · AssetManager（框架侧）的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V3 / V6（资源层）
//  对应记录：Docs/06-框架改造记录.md §三 FW-08、§6.2（ResMgr 的隐式契约）
//
//  ---------------------------------------------------------------------------
//  这组测试为什么全在 EditMode，而且**不需要 YooAsset**
//  ---------------------------------------------------------------------------
//  因为在 B2 里我们把"底层怎么捞资源"抽成了一个接口 `IAssetProvider`：
//
//      AssetManager（缓存 / 引用计数 / 句柄）  ->  IAssetProvider  <-  YooAssetProvider
//
//  于是引用计数、缓存复用、失败重试、释放时机这些**最容易出错、也最值得测**的逻辑，
//  可以喂一个**假加载器**（纯内存、完成时机由测试决定）来测：
//      · 不需要 YooAsset、不需要打包、不需要真资源
//      · 完成时机可控 → **结果完全确定**，不会 flaky
//
//  这是"依赖倒置"带来的可测性红利。真实 YooAssetProvider 的验证在 B3（需要包配置）。
//
//  ---------------------------------------------------------------------------
//  一个操作细节：测试里只用 `TextAsset` 作为被测资源类型
//  ---------------------------------------------------------------------------
//  不用自定义 `ScriptableObject` 子类 —— 那种类如果和文件名不一致，Unity 会给出
//  "脚本名与类名不匹配" 的告警。`TextAsset` 有公开构造函数（`new TextAsset(text)`），
//  在 EditMode 测试里造起来最省事、也不引入这类噪音。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NBC.Framework;
using NBC.Framework.Asset;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>可手动控制完成时机的假加载操作。</summary>
    internal sealed class FakeLoadOperation : IAssetLoadOperation
    {
        public event Action<IAssetLoadOperation> OnComplete;

        private AssetStatus m_status = AssetStatus.Loading;

        public FakeLoadOperation(string location, Type assetType)
        {
            Location = location;
            AssetType = assetType;
        }

        public string Location { get; private set; }

        public Type AssetType { get; private set; }

        public AssetStatus Status
        {
            get { return m_status; }
        }

        public float Progress { get; private set; }

        public string Error { get; private set; } = string.Empty;

        public UnityEngine.Object AssetObject { get; private set; }

        public bool IsDone
        {
            get { return m_status == AssetStatus.Succeeded || m_status == AssetStatus.Failed; }
        }

        public bool Disposed { get; private set; }

        public bool WaitForAsyncCompleteCalled { get; private set; }

        public void SetProgress(float progress)
        {
            Progress = progress;
        }

        public void Complete(UnityEngine.Object asset)
        {
            AssetObject = asset;
            Progress = 1f;
            m_status = AssetStatus.Succeeded;
            Raise();
        }

        public void Fail(string error)
        {
            Error = error;
            AssetObject = null;
            m_status = AssetStatus.Failed;
            Raise();
        }

        public void WaitForAsyncComplete()
        {
            WaitForAsyncCompleteCalled = true;
        }

        public void Dispose()
        {
            Disposed = true;
        }

        private void Raise()
        {
            Action<IAssetLoadOperation> handlers = OnComplete;
            if (handlers != null)
            {
                handlers(this);
            }
        }
    }

    /// <summary>假加载器：记录被调用的次数，并按测试的指示完成。</summary>
    internal sealed class FakeAssetProvider : IAssetProvider
    {
        public readonly List<FakeLoadOperation> Operations = new List<FakeLoadOperation>();

        public bool IsInitialized { get; private set; }

        public int UnloadUnusedCalls { get; private set; }

        public int UnloadAllCalls { get; private set; }

        public AssetRuntimeMode LastMode { get; private set; }

        public Task InitializeAsync(AssetRuntimeMode mode)
        {
            LastMode = mode;
            IsInitialized = true;
            return Task.CompletedTask;
        }

        public IAssetLoadOperation LoadAsync(string location, Type assetType)
        {
            FakeLoadOperation op = new FakeLoadOperation(location, assetType);
            Operations.Add(op);
            return op;
        }

        public IAssetLoadOperation LoadSync(string location, Type assetType)
        {
            return LoadAsync(location, assetType);
        }

        public void UnloadUnused()
        {
            UnloadUnusedCalls++;
        }

        public void UnloadAll()
        {
            UnloadAllCalls++;
        }
    }

    [TestFixture]
    public class AssetManagerTests
    {
        private AssetManager m_assets;
        private FakeAssetProvider m_provider;
        private readonly List<UnityEngine.Object> m_created = new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            SingletonRegistry.ResetAll();

            m_assets = AssetManager.Instance;
            m_provider = new FakeAssetProvider();
            m_assets.SetProvider(m_provider);
        }

        [TearDown]
        public void TearDown()
        {
            m_assets.UnloadAll();

            for (int i = 0; i < m_created.Count; i++)
            {
                if (m_created[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(m_created[i]);
                }
            }

            m_created.Clear();
            SingletonRegistry.ResetAll();
        }

        private TextAsset NewAsset()
        {
            TextAsset asset = new TextAsset("probe");
            m_created.Add(asset);
            return asset;
        }

        private void Initialize()
        {
            m_assets.InitializeAsync(AssetRuntimeMode.EditorSimulate).GetAwaiter().GetResult();
        }

        // ====================================================================
        //  引导流程：没装加载器 / 没初始化就加载，必须当场说清楚
        // ====================================================================

        [Test]
        public void LoadAssetAsync_WithoutProvider_ThrowsWithActionableMessage()
        {
            SingletonRegistry.ResetAll();
            AssetManager fresh = AssetManager.Instance;

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => fresh.LoadAssetAsync<TextAsset>("any"));

            StringAssert.Contains("SetProvider", ex.Message, "报错要告诉人怎么做");
        }

        [Test]
        public void LoadAssetAsync_BeforeInitialize_Throws()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => m_assets.LoadAssetAsync<TextAsset>("any"));

            StringAssert.Contains("InitializeAsync", ex.Message, "报错要告诉人怎么做");
        }

        [Test]
        public void SetProvider_TwiceWithDifferentProvider_Throws()
        {
            Assert.Throws<InvalidOperationException>(
                () => m_assets.SetProvider(new FakeAssetProvider()));
        }

        [Test]
        public void InitializeAsync_PassesModeToProvider()
        {
            Initialize();

            Assert.IsTrue(m_assets.IsInitialized);
            Assert.AreEqual(AssetRuntimeMode.EditorSimulate, m_provider.LastMode);
        }

        // ====================================================================
        //  缓存 + 引用计数
        // ====================================================================

        [Test]
        public void LoadAssetAsync_ReturnsHandleWithRefCountOne()
        {
            Initialize();

            AssetHandle<TextAsset> handle = m_assets.LoadAssetAsync<TextAsset>("a");

            Assert.AreEqual(1, handle.RefCount);
            Assert.AreEqual("a", handle.Location);
            Assert.AreEqual(AssetStatus.Loading, handle.Status);
            Assert.IsFalse(handle.IsDone);
            Assert.IsFalse(handle.IsValid, "还没加载完，不应算有效");

            handle.Dispose();
        }

        [Test]
        public void LoadAssetAsync_SameLocationTwice_ReusesEntryAndRefCountReachesTwo()
        {
            Initialize();

            AssetHandle<TextAsset> first = m_assets.LoadAssetAsync<TextAsset>("a");
            AssetHandle<TextAsset> second = m_assets.LoadAssetAsync<TextAsset>("a");

            Assert.AreEqual(2, first.RefCount);
            Assert.AreEqual(2, second.RefCount);
            Assert.AreEqual(1, m_provider.Operations.Count, "同一个地址只应触发一次底层加载");

            first.Dispose();
            second.Dispose();
        }

        [Test]
        public void LoadAssetAsync_SameLocationDifferentType_DoesNotReuse()
        {
            Initialize();

            AssetHandle<TextAsset> typed = m_assets.LoadAssetAsync<TextAsset>("a");
            AssetHandle<UnityEngine.Object> erased = m_assets.LoadAssetAsync<UnityEngine.Object>("a");

            Assert.AreEqual(2, m_provider.Operations.Count, "类型不同应各加载一次（缓存键含类型）");

            typed.Dispose();
            erased.Dispose();
        }

        [Test]
        public void Dispose_DecrementsRefCount_AndIsIdempotent()
        {
            Initialize();

            AssetHandle<TextAsset> first = m_assets.LoadAssetAsync<TextAsset>("a");
            AssetHandle<TextAsset> second = m_assets.LoadAssetAsync<TextAsset>("a");

            first.Dispose();
            Assert.AreEqual(1, second.RefCount);

            first.Dispose();
            first.Dispose();
            Assert.AreEqual(1, second.RefCount, "重复 Dispose 不应重复减计数（ADR-001 约束 C6）");

            second.Dispose();
            Assert.AreEqual(0, second.RefCount);
        }

        // ====================================================================
        //  两级释放：Dispose 只减计数，ReleaseUnused 才真释放
        // ====================================================================

        [Test]
        public void Dispose_DoesNotReleaseOperationImmediately()
        {
            Initialize();

            AssetHandle<TextAsset> handle = m_assets.LoadAssetAsync<TextAsset>("a");
            FakeLoadOperation op = m_provider.Operations[0];

            handle.Dispose();

            Assert.IsFalse(op.Disposed, "Dispose 只减计数；真正释放发生在 ReleaseUnused（ADR-001 决策 1）");
            Assert.AreEqual(1, m_assets.GetDebugInfo().UnusedCount);
        }

        [Test]
        public void ReleaseUnused_ReleasesOnlyZeroRefEntries()
        {
            Initialize();

            AssetHandle<TextAsset> keep = m_assets.LoadAssetAsync<TextAsset>("keep");
            AssetHandle<TextAsset> drop = m_assets.LoadAssetAsync<TextAsset>("drop");
            FakeLoadOperation keepOp = m_provider.Operations[0];
            FakeLoadOperation dropOp = m_provider.Operations[1];

            drop.Dispose();
            int released = m_assets.ReleaseUnused();

            Assert.AreEqual(1, released, "只应释放计数归零的那一个");
            Assert.IsTrue(dropOp.Disposed, "计数归零的条目应被真正释放");
            Assert.IsFalse(keepOp.Disposed, "还有引用的条目不能被释放");
            Assert.AreEqual(1, m_assets.GetDebugInfo().CachedCount);

            keep.Dispose();
        }

        [Test]
        public void ReloadAfterRelease_TriggersNewProviderLoad()
        {
            Initialize();

            AssetHandle<TextAsset> first = m_assets.LoadAssetAsync<TextAsset>("a");
            first.Dispose();
            m_assets.ReleaseUnused();

            AssetHandle<TextAsset> again = m_assets.LoadAssetAsync<TextAsset>("a");

            Assert.AreEqual(2, m_provider.Operations.Count, "释放之后再加载应当重新向底层要");
            again.Dispose();
        }

        // ====================================================================
        //  完成通知：回调 / 轮询 / await 三种消费方式
        // ====================================================================

        [Test]
        public void OnComplete_IsInvokedWhenOperationCompletes()
        {
            Initialize();

            AssetHandle<TextAsset> handle = m_assets.LoadAssetAsync<TextAsset>("a");
            TextAsset asset = NewAsset();

            bool called = false;
            handle.OnComplete += h =>
            {
                called = true;
                Assert.IsTrue(h.IsValid);
                Assert.AreSame(asset, h.Asset);
            };

            m_provider.Operations[0].Complete(asset);

            Assert.IsTrue(called);
            handle.Dispose();
        }

        [Test]
        public void OnComplete_RegisteredAfterCompletion_IsInvokedImmediately()
        {
            // 这条守的是"加载太快、回调注册太晚"的竞态：
            // 已经完成时注册必须**立刻**被调用，否则这次通知就永远丢了。
            Initialize();

            AssetHandle<TextAsset> handle = m_assets.LoadAssetAsync<TextAsset>("a");
            m_provider.Operations[0].Complete(NewAsset());

            bool called = false;
            handle.OnComplete += h => called = true;

            Assert.IsTrue(called, "已完成时注册回调应当立即触发");
            handle.Dispose();
        }

        [Test]
        public void Awaiter_ContinuationRunsOnCompletion()
        {
            Initialize();

            AssetHandle<TextAsset> handle = m_assets.LoadAssetAsync<TextAsset>("a");
            AssetHandleAwaiter<TextAsset> awaiter = handle.GetAwaiter();

            Assert.IsFalse(awaiter.IsCompleted);

            bool continued = false;
            awaiter.OnCompleted(() => continued = true);

            m_provider.Operations[0].Complete(NewAsset());

            Assert.IsTrue(continued, "await 的续体应在完成后执行");
            Assert.IsTrue(awaiter.IsCompleted);
            Assert.AreSame(handle, awaiter.GetResult());
            handle.Dispose();
        }

        [Test]
        public void ProgressAndWaitForAsyncComplete_AreForwarded()
        {
            Initialize();

            AssetHandle<TextAsset> handle = m_assets.LoadAssetAsync<TextAsset>("a");
            FakeLoadOperation op = m_provider.Operations[0];

            op.SetProgress(0.5f);
            Assert.AreEqual(0.5f, handle.Progress, 0.0001f);

            handle.WaitForAsyncComplete();
            Assert.IsTrue(op.WaitForAsyncCompleteCalled);

            handle.Dispose();
        }

        // ====================================================================
        //  失败与自愈
        // ====================================================================

        [Test]
        public void FailedLoad_HandleIsInvalid_AndErrorIsExposed()
        {
            Initialize();

            AssetHandle<TextAsset> handle = m_assets.LoadAssetAsync<TextAsset>("bad");
            m_provider.Operations[0].Fail("假加载器：故意失败");

            Assert.AreEqual(AssetStatus.Failed, handle.Status);
            Assert.IsTrue(handle.IsDone);
            Assert.IsFalse(handle.IsValid);
            Assert.IsNull(handle.Asset);
            StringAssert.Contains("故意失败", handle.Error);

            handle.Dispose();
        }

        [Test]
        public void FailedEntry_IsNotReused_NextLoadRetries()
        {
            Initialize();

            AssetHandle<TextAsset> first = m_assets.LoadAssetAsync<TextAsset>("bad");
            m_provider.Operations[0].Fail("失败");
            first.Dispose();

            // 失败过的条目不该被复用，否则这个资源就永远加载不出来了
            AssetHandle<TextAsset> second = m_assets.LoadAssetAsync<TextAsset>("bad");

            Assert.AreEqual(2, m_provider.Operations.Count, "失败的条目应当被丢掉并重试一次");
            second.Dispose();
        }

        // ====================================================================
        //  观测与整场收尾
        // ====================================================================

        [Test]
        public void GetDebugInfo_ReflectsCurrentState()
        {
            Initialize();

            AssetHandle<TextAsset> held = m_assets.LoadAssetAsync<TextAsset>("held");
            AssetHandle<TextAsset> dropped = m_assets.LoadAssetAsync<TextAsset>("dropped");
            m_provider.Operations[1].Fail("失败");

            dropped.Dispose();

            AssetDebugInfo info = m_assets.GetDebugInfo();
            Assert.AreEqual(2, info.CachedCount);
            Assert.AreEqual(1, info.ActiveCount, "held 还持有引用");
            Assert.AreEqual(1, info.UnusedCount, "dropped 计数已归零");
            Assert.AreEqual(1, info.FailedCount);
            Assert.IsTrue(info.ToString().Contains("条目="), "面板要能直接打印一行摘要");

            held.Dispose();
        }

        [Test]
        public void UnloadAll_ClearsCacheAndTellsProvider()
        {
            Initialize();

            m_assets.LoadAssetAsync<TextAsset>("a");
            m_assets.LoadAssetAsync<TextAsset>("b");

            m_assets.UnloadAll();

            Assert.AreEqual(0, m_assets.GetDebugInfo().CachedCount);
            Assert.AreEqual(1, m_provider.UnloadAllCalls, "应当通知底层卸载");
        }

        [Test]
        public void UnloadUnusedAssets_ReleasesThenAsksProvider()
        {
            Initialize();

            AssetHandle<TextAsset> handle = m_assets.LoadAssetAsync<TextAsset>("a");
            handle.Dispose();

            m_assets.UnloadUnusedAssets();

            Assert.AreEqual(0, m_assets.GetDebugInfo().CachedCount);
            Assert.AreEqual(1, m_provider.UnloadUnusedCalls);
        }

        // ====================================================================
        //  交叉验证：AssetManager 也是 A1 的单例
        // ====================================================================

        [Test]
        public void AssetManager_CannotBeConstructedDirectly()
        {
            Assert.Throws<InvalidOperationException>(() => new AssetManager());
        }
    }
}
