// ============================================================================
//  M2-C1 · `AssetConfigSource` 的 EditMode 测试（**ConfigMgr + 真加载路径**）
//  对应验收：Docs\22-M2开工清单.md C1
//  被测：Client\Assets\_Project\Game\Config\AssetConfigSource.cs
//
//  ---------------------------------------------------------------------------
//  为什么"走资源层的来源"能在 EditMode 里测（不需要打包、不需要内容包）
//  ---------------------------------------------------------------------------
//  因为 `AssetConfigSource` **只用框架的接缝**（`AssetManager`），而 `AssetManager`
//  的底层是一个可替换的 `IAssetProvider`。于是测试里塞一个**假加载器**：
//
//      真链路：AssetConfigSource -> AssetManager -> [IAssetProvider] -> 内容包
//      测试：  AssetConfigSource -> AssetManager -> [假加载器]     -> 内存里的资产
//
//  📌 这就是 B2 那条"依赖倒置换来的可测性"的**第二次兑现**
//     （第一次是 `AssetManagerTests` 不需要内容包就能验缓存/引用计数）。
//
//  ⚠️ 真正"内容包那一段通不通"仍然要靠 PlayMode / 你点菜单跑一次 ——
//     本文件不假装覆盖了它（M1-B3 的 PlayMode 冒烟测试负责那一段）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NBC.Framework.Asset;
using NBC.Framework;
using NBC.Game.Config;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;

// ⚠️ 这个文件同时 `using System;` 和 `using UnityEngine;`，于是裸写 `Object`
//    会**歧义**（`System.Object` vs `UnityEngine.Object`）→ CS0104。
//    用别名把它定死；比到处写 `UnityEngine.Object` 干净，也避免以后有人漏写一处。
using Object = UnityEngine.Object;

namespace NBC.Tests.EditMode
{
    /// <summary>M2-C1：配置表的资源层来源。</summary>
    public sealed class AssetConfigSourceTests
    {
        /// <summary>被测来源。</summary>
        private AssetConfigSource m_source;

        /// <summary>假加载器。</summary>
        private FakeProvider m_provider;

        /// <summary>造出来的资产（收尾要销毁）。</summary>
        private readonly List<Object> m_created = new List<Object>();

        /// <summary>每个用例前：把所有单例重置干净（含 `AssetManager`），于是**没有加载器**。</summary>
        [SetUp]
        public void SetUp()
        {
            // ⚠️ 用 `SingletonRegistry.ResetAll()` 而不是 `SetProvider(null)`：
            //    后者**会抛异常**（框架不让静默卸加载器，那会把旧加载器持有的资源变成孤儿）。
            //    重置单例才是测试之间正确的隔离手段（`AssetManagerTests` 也是这么做的）。
            SingletonRegistry.ResetAll();
            m_created.Clear();
        }

        /// <summary>每个用例后：重置单例、销毁测试里造的资产。</summary>
        [TearDown]
        public void TearDown()
        {
            SingletonRegistry.ResetAll();

            for (int i = 0; i < m_created.Count; i++)
            {
                if (m_created[i] != null)
                {
                    Object.DestroyImmediate(m_created[i]);
                }
            }

            m_created.Clear();
        }

        /// <summary>装一个假加载器并初始化资源层，然后造出来源（多数用例都要）。</summary>
        private void InstallFakeProvider()
        {
            m_provider = new FakeProvider();
            AssetManager.Instance.SetProvider(m_provider);

            // 假加载器是同步完成的，所以这样调不会卡（真实现要走帧循环，那是 PlayMode 的事）
            AssetManager.Instance.InitializeAsync(AssetRuntimeMode.EditorSimulate).GetAwaiter().GetResult();

            m_source = new AssetConfigSource();
        }

        /// <summary>造一个假的 Hero 配置资产。</summary>
        /// <returns>资产。</returns>
        private HeroConfig CreateHeroAsset()
        {
            HeroConfig asset = ScriptableObject.CreateInstance<HeroConfig>();
            m_created.Add(asset);
            return asset;
        }

        // ====================================================================
        //  一、地址约定
        // ====================================================================

        /// <summary>表名 → 资源地址（与 `ConfigMgr` 的命名约定一致）。</summary>
        [Test]
        public void AddressOf_FollowsConfigMgrNaming()
        {
            InstallFakeProvider();

            Assert.AreEqual("HeroConfig", m_source.AddressOf("Hero"));
            Assert.AreEqual("QuestConditionConfig", m_source.AddressOf("QuestCondition"));
            Assert.AreEqual(ConfigMgr.AssetNameOf("Hero"), m_source.AddressOf("Hero"),
                "地址规则必须和 ConfigMgr 的资产命名约定一致，否则两边对不上");
        }

        // ====================================================================
        //  二、没装加载器时报错**方向要对**
        // ====================================================================

        /// <summary>
        /// 没装底层加载器时，报错要**指向"组合根没跑"**，而不是"表不存在"。
        /// <para>这两件事的排查方向完全相反 —— 报错方向错了比没报错更费时间。</para>
        /// </summary>
        [Test]
        public void LoadTable_WithoutProvider_ReportsMissingCompositionRoot()
        {
            // ⚠️ **不要**写 `AssetManager.Instance.SetProvider(null)` —— 框架会抛
            //    ArgumentNullException（不让静默卸加载器，那会把旧加载器持有的资源变成孤儿）。
            //    本用例靠的是 `SetUp` 里的 `SingletonRegistry.ResetAll()`：
            //    重置之后 `AssetManager` 是全新的，**本来就没有加载器**。
            string failure = null;
            m_source.LoadTable("Hero", asset => Assert.Fail("不该成功"), reason => failure = reason);

            Assert.IsNotNull(failure);
            StringAssert.Contains("AssetBootstrapper", failure);
            StringAssert.Contains("Hero", failure);
        }

        // ====================================================================
        //  三、成功路径
        // ====================================================================

        /// <summary>加载成功：交出资产，并**持有句柄**（配置表常驻，不释放）。</summary>
        [Test]
        public void LoadTable_Success_HandsOverAssetAndKeepsHandle()
        {
            InstallFakeProvider();

            HeroConfig asset = CreateHeroAsset();
            m_provider.Put("HeroConfig", asset);

            ScriptableObject loaded = null;
            string failure = null;
            m_source.LoadTable("Hero", value => loaded = value, reason => failure = reason);

            Assert.IsNull(failure);
            Assert.AreSame(asset, loaded);
            Assert.AreEqual(1, m_source.HeldHandleCount, "句柄要一直持有（配置表在进程内常驻）");
        }

        /// <summary>
        /// **假成功防线**：句柄拿得到、但资产是 null 时，必须报失败。
        /// <para>
        /// 这条是 M1-B5 的教训：我当时用"句柄非 null"当成功判据，
        /// 于是把一次真失败报成了 "✅ 取到句柄（Asset = null）"。
        /// 框架已经把这个教训写进 `IsValid`，本用例盯住"来源确实用了它"。
        /// </para>
        /// </summary>
        [Test]
        public void LoadTable_HandleWithoutAsset_ReportsFailure()
        {
            InstallFakeProvider();

            // ⚠️ 这条要模拟的是"**底层报成功、但没给资产**"这种自相矛盾的加载 ——
            //    M1-B5 我就是被它骗过（打出过 "✅ 取到句柄（Asset = null）"）。
            //    第一版我用"放一个类型不对的资产"来造这个场景，**造不出来**：
            //    来源请求的是 `ScriptableObject`，任何 SO 都是它的实例，
            //    所以 `IsInstanceOfType` 为真、加载**真的成功了**，用例反而红。
            //    正确做法是让假加载器**故意矛盾**（状态说成功、资产给 null）——
            //    这正是 `IsValid` 要挡的形状。
            m_provider.PutSucceededWithoutAsset("HeroConfig");

            string failure = null;
            m_source.LoadTable("Hero", asset => Assert.Fail("资产是 null 时不该算成功"), reason => failure = reason);

            Assert.IsNotNull(failure, "句柄非 null 但资产是 null —— 必须报失败，不能算成功");
            StringAssert.Contains("HeroConfig", failure);
        }

        /// <summary>加载失败：报错要说清三个常见原因（导入 / 打包 / 地址规则）。</summary>
        [Test]
        public void LoadTable_MissingAsset_ReportsThreeCommonCauses()
        {
            InstallFakeProvider();

            // 什么都不放 —— 假加载器会给出"找不到"的失败
            string failure = null;
            m_source.LoadTable("Quest", asset => Assert.Fail("不该成功"), reason => failure = reason);

            Assert.IsNotNull(failure);
            StringAssert.Contains("导入 TSV", failure);
            StringAssert.Contains("BundleCollectorSetting", failure);
            StringAssert.Contains("QuestConfig", failure);
        }

        /// <summary>失败的句柄要**释放掉**（不留在引用计数里）。</summary>
        [Test]
        public void LoadTable_Failure_ReleasesHandle()
        {
            InstallFakeProvider();

            // ⚠️ **必须给失败回调**：`IConfigSource` 的契约是"两个回调至少来一个"，
            //    而这个来源在"没人接失败"时会打 `Debug.LogError`。
            //    在 Unity 测试里**一条意外的 LogError 就算失败**（LogAssert）——
            //    第一版这里传了 null，于是用例红在一句"没人接失败回调"上。
            string failure = null;
            m_source.LoadTable("Quest", null, reason => failure = reason);

            Assert.IsNotNull(failure);
            Assert.AreEqual(0, m_source.HeldHandleCount, "失败时不该持有句柄");
        }

        // ====================================================================
        //  四、和 ConfigMgr 串起来（**这才是 C1 的验收**）
        // ====================================================================

        /// <summary>
        /// 把来源装进 `ConfigMgr`，预加载一张表，然后**像业务代码那样查表**。
        /// <para>全程不需要内容包、不需要打包 —— 但走的是真实的那条代码路径。</para>
        /// </summary>
        [Test]
        public void ConfigMgr_WithAssetSource_PreloadsAndQueries()
        {
            InstallFakeProvider();

            HeroConfig asset = CreateHeroAsset();
            asset.rows = new List<Config_Hero>
            {
                new Config_Hero
                {
                    id = 1001, name = "剑士", hp = 1200, moveSpeed = 5000,
                    critRatePerTenThousand = 0, camDist = 8.5f, skillIds = new[] { 2001 }
                }
            };
            asset.RebuildIndex();
            m_provider.Put("HeroConfig", asset);

            ConfigMgr.Instance.SetSource(m_source);

            bool done = false;
            string failure = null;

            ConfigMgr.Instance.Preload<HeroConfig>(() => done = true, reason => failure = reason);

            Assert.IsNull(failure);
            Assert.IsTrue(done, "假加载器是同步完成的，回调应当当场触发");

            // **业务代码的用法**（M1-C4 定的形状，一行都没变）
            HeroConfig table = ConfigMgr.Instance.Get<HeroConfig>();
            Assert.AreEqual("剑士", table.Get(1001).name);
        }

        // ====================================================================
        //  假加载器（实现框架的 IAssetProvider / IAssetLoadOperation）
        // ====================================================================

        /// <summary>放在内存里的假加载器。</summary>
        private sealed class FakeProvider : IAssetProvider
        {
            /// <summary>地址 → 资产。</summary>
            private readonly Dictionary<string, Object> m_assets = new Dictionary<string, Object>(StringComparer.Ordinal);

            /// <summary>这些地址要"**状态说成功、资产给 null**"（模拟自相矛盾的底层）。</summary>
            private readonly HashSet<string> m_succeededWithoutAsset = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>是否已初始化。</summary>
            public bool IsInitialized { get; private set; }

            /// <summary>放一个资产进去。</summary>
            /// <param name="location">地址。</param>
            /// <param name="asset">资产。</param>
            public void Put(string location, Object asset)
            {
                m_assets[location] = asset;
            }

            /// <summary>让这个地址"报成功但不给资产"（见 <see cref="AssetHandle{T}.IsValid"/> 的讨论）。</summary>
            /// <param name="location">地址。</param>
            public void PutSucceededWithoutAsset(string location)
            {
                m_succeededWithoutAsset.Add(location);
            }

            /// <summary>初始化（假实现：立刻成功）。</summary>
            /// <param name="mode">模式（忽略）。</param>
            /// <returns>已完成的任务。</returns>
            public Task InitializeAsync(AssetRuntimeMode mode)
            {
                IsInitialized = true;
                return Task.CompletedTask;
            }

            /// <summary>异步加载（假实现：**同步完成**，于是测试不需要帧循环）。</summary>
            /// <param name="location">地址。</param>
            /// <param name="assetType">期望类型。</param>
            /// <returns>加载操作。</returns>
            public IAssetLoadOperation LoadAsync(string location, Type assetType)
            {
                // "报成功但不给资产"这条要单独造：它模拟的是底层自相矛盾时的形状
                if (m_succeededWithoutAsset.Contains(location))
                {
                    return new FakeOperation(location, assetType, null, true);
                }

                return new FakeOperation(location, assetType, Resolve(location, assetType), false);
            }

            /// <summary>同步加载（同异步）。</summary>
            /// <param name="location">地址。</param>
            /// <param name="assetType">期望类型。</param>
            /// <returns>加载操作。</returns>
            public IAssetLoadOperation LoadSync(string location, Type assetType)
            {
                return LoadAsync(location, assetType);
            }

            /// <summary>场景加载：本文件用不到，直接报"没实现"而不是静默返回 null。</summary>
            /// <param name="location">地址。</param>
            /// <param name="mode">模式。</param>
            /// <returns>永远抛。</returns>
            public ISceneLoadOperation LoadSceneAsync(string location, LoadSceneMode mode)
            {
                throw new NotSupportedException("假加载器不实现场景加载。");
            }

            /// <summary>卸载场景。</summary>
            /// <param name="operation">操作。</param>
            public void UnloadScene(ISceneLoadOperation operation)
            {
            }

            /// <summary>卸载没人用的。</summary>
            public void UnloadUnused()
            {
            }

            /// <summary>卸载全部。</summary>
            public void UnloadAll()
            {
            }

            /// <summary>按地址取资产（类型对不上时给 null，模拟"句柄成功但资产为 null"）。</summary>
            /// <param name="location">地址。</param>
            /// <param name="assetType">期望类型。</param>
            /// <returns>资产或 null。</returns>
            private Object Resolve(string location, Type assetType)
            {
                Object asset;

                if (!m_assets.TryGetValue(location, out asset))
                {
                    return null;
                }

                // 类型不匹配 -> 给 null（这正是"看起来成功其实是失败"的那种情形）
                return assetType.IsInstanceOfType(asset) ? asset : null;
            }
        }

        /// <summary>一次假加载（**注册回调时如果已经完成会立刻回调**，与真实现一致）。</summary>
        private sealed class FakeOperation : IAssetLoadOperation
        {
            /// <summary>结果资产（null = 失败）。</summary>
            private readonly Object m_asset;

            /// <summary>失败原因。</summary>
            private readonly string m_error;

            /// <summary>造一次假加载。</summary>
            /// <param name="location">地址。</param>
            /// <param name="assetType">期望类型。</param>
            /// <param name="asset">结果资产。</param>
            /// <param name="succeedWithoutAsset">
            /// true = **故意矛盾**：状态报成功、资产给 null（用来验 `IsValid` 那道防线）。
            /// </param>
            public FakeOperation(string location, Type assetType, Object asset, bool succeedWithoutAsset = false)
            {
                Location = location;
                m_asset = asset;

                if (asset != null)
                {
                    Status = AssetStatus.Succeeded;
                    m_error = string.Empty;
                    return;
                }

                // 两种"没资产"要区分开：正常的失败 vs 自相矛盾的成功
                Status = succeedWithoutAsset ? AssetStatus.Succeeded : AssetStatus.Failed;
                m_error = succeedWithoutAsset
                    ? "假加载器：故意报成功但不给资产（模拟底层自相矛盾）"
                    : "假加载器：地址「" + location + "」没有可用的 " + assetType.Name + " 资产。";
            }

            /// <summary>地址。</summary>
            public string Location { get; private set; }

            /// <summary>状态。</summary>
            public AssetStatus Status { get; private set; }

            /// <summary>进度（已完成的假加载，恒为 1）。</summary>
            public float Progress
            {
                get { return 1f; }
            }

            /// <summary>失败原因。</summary>
            public string Error
            {
                get { return m_error; }
            }

            /// <summary>资产。</summary>
            public Object AssetObject
            {
                get { return m_asset; }
            }

            /// <summary>已完成。</summary>
            public bool IsDone
            {
                get { return true; }
            }

            /// <summary>完成通知（**已完成的注册会立刻回调**，与真实现一致）。</summary>
            public event Action<IAssetLoadOperation> OnComplete
            {
                add
                {
                    if (value != null)
                    {
                        value(this);
                    }
                }
                remove { }
            }

            /// <summary>阻塞（假加载已完成，什么都不用做）。</summary>
            public void WaitForAsyncComplete()
            {
            }

            /// <summary>释放（假实现：无害的空操作）。</summary>
            public void Dispose()
            {
            }
        }
    }
}
