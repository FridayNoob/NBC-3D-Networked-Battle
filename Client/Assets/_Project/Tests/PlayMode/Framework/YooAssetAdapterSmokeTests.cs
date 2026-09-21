// ============================================================================
//  M1-B3 · YooAsset 适配层 + AssetManager 的端到端冒烟测试（PlayMode）
//  对应验收：Docs/16-M1开工清单.md V3（AssetManager 可用：编辑器模拟模式）
//  对应记录：Docs/16 §10.4（B3 操作单）
//
//  ---------------------------------------------------------------------------
//  为什么必须在 PlayMode（这次是实测结论，不是习惯）
//  ---------------------------------------------------------------------------
//  `YooAssets.Initialize()` 会 `AddComponent<YooAssetsDriver>()`（`YooAssets.cs:58`），
//  而所有异步操作的推进都靠这个 MonoBehaviour 的 **`Update()`**（`YooAssetsDriver.cs:21-25`）。
//
//  **EditMode 下 `Update` 不会执行** —— 这一条我们在 A1 用 5 组对照实验实测过
//  （连最普通的 MonoBehaviour 都拿不到 Awake，见 Docs/06 §9.7）。
//  所以放 EditMode 里跑，异步操作会**永远不完成**，测试只会超时。
//
//  这正是那条测试划分规则的价值：它让"该放哪"变成一个**有依据的判断**，而不是习惯。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 前置条件（不满足时本测试会给出明确指引，而不是莫名其妙地红）
//  ---------------------------------------------------------------------------
//  需要你在 Unity 里跑过一次**编辑器模拟构建**，并且：
//    · 包名   = DefaultPackage
//    · 版本号 = **Simulate**（刻意固定！默认的 `2026-09-21-729` 那种是自动生成的，
//      每次构建都变，路径就不稳定了。构建窗口里有 `BuildVersion` 输入框可改，
//      见 EditorSimulateBuildPipelineViewer.cs:95）
//
//  产物应当在：Client/Bundles/StandaloneWindows64/DefaultPackage/Simulate/
//
//  ---------------------------------------------------------------------------
//  这个测试要证明什么（不只是"没报错"）
//  ---------------------------------------------------------------------------
//    ① `packageRoot` 推导正确 —— 能真的找到清单文件并初始化成功
//    ② 框架侧 → 适配层 → YooAsset v3 → 真实资源，**整条链路打通**
//    ③ 加载回来的是**真实内容**（断言 TextAsset 的文本），不是"拿到了个非 null"
//    ④ **拿到的是原始资源，不是实例**（这是我们有意偏离原框架的语义，见 Docs/06 §13.7）
// ============================================================================

using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NBC.Framework;
using NBC.Framework.Asset;
using NBC.Framework.Asset.Adapter;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YooAsset;

namespace NBC.Tests.PlayMode
{
    [TestFixture]
    public class YooAssetAdapterSmokeTests
    {
        /// <summary>包名（构建窗口里填的那个）。</summary>
        private const string PackageName = "DefaultPackage";

        /// <summary>**固定**的包版本。改这里就要同步改构建窗口里的版本号。</summary>
        private const string PackageVersion = "Simulate";

        /// <summary>构建目标名（构建窗口里选的目标）。</summary>
        private const string BuildTargetName = "StandaloneWindows64";

        /// <summary>
        /// 测试资源的 Address。
        /// ⚠️ Address 由收集器的 AddressRule 决定，**以收集器窗口里那行显示的值为准**。
        /// 默认规则通常取文件名（不含扩展名），所以大概率是 "hello"。
        /// </summary>
        private const string TestAssetAddress = "hello";

        /// <summary>测试资源里写死的内容，用来断言"拿到的是真东西"。</summary>
        private const string ExpectedText = "NBC-ASSET-SMOKE-TEST-OK";

        /// <summary>Unity 工程根目录（`Application.dataPath` 的上一级）。</summary>
        private static string ProjectRoot
        {
            get { return Directory.GetParent(Application.dataPath).FullName; }
        }

        /// <summary>
        /// 模拟清单目录。
        /// ⚠️ 必须是**绝对路径**：`EditorFileSystem.OnCreate` 直接把它存进字段、
        /// 再用 `PathUtility.Combine` 拼文件名，**不做任何规范化**（`EditorFileSystem.cs:220`），
        /// 传相对路径就会按进程工作目录解析。
        /// </summary>
        private static string SimulatePackageRoot
        {
            get
            {
                return Path.Combine(ProjectRoot, "Bundles", BuildTargetName, PackageName, PackageVersion);
            }
        }

        [TearDown]
        public void TearDown()
        {
            SingletonRegistry.ResetAll();

            // YooAsset 的全局状态也要收掉，否则这个用例第二次跑会撞上"包已存在/已初始化"
            if (YooAssets.IsInitialized)
            {
                YooAssets.Destroy();
            }
        }

        [UnityTest]
        public IEnumerator EditorSimulate_InitializesAndLoadsRealContent()
        {
            Assert.IsTrue(
                Directory.Exists(SimulatePackageRoot),
                "找不到模拟清单目录：\n  " + SimulatePackageRoot + "\n\n" +
                "请先按 Docs\\16 §10.4 在 Unity 里跑一次编辑器模拟构建，并且：\n" +
                "  · 包名   = " + PackageName + "\n" +
                "  · 版本号 = " + PackageVersion + "（**要固定**，不要用自动生成的时间戳版本）\n" +
                "  · 构建管线 = EditorSimulateBuildPipeline\n" +
                "菜单入口：YooAsset/Bundle Builder");

            SingletonRegistry.ResetAll();
            AssetManager manager = AssetManager.Instance;

            // —— 装适配层。注意这一步就把 packageRoot 交给它了 ——
            YooAssetProvider provider = YooAssetProvider.Install(PackageName, SimulatePackageRoot);

            // —— 初始化 ——
            Task init = manager.InitializeAsync(AssetRuntimeMode.EditorSimulate);
            yield return WaitForTask(init, "初始化");

            Assert.IsTrue(manager.IsInitialized, "InitializeAsync 跑完了但 IsInitialized 仍是 false");
            Assert.AreEqual(PackageName, provider.PackageName);
        }

        [UnityTest]
        public IEnumerator EditorSimulate_LoadsTextAssetWithRealContent()
        {
            Assert.IsTrue(Directory.Exists(SimulatePackageRoot), "先跑一次模拟构建，见上一个用例的提示");

            SingletonRegistry.ResetAll();
            AssetManager manager = AssetManager.Instance;
            YooAssetProvider.Install(PackageName, SimulatePackageRoot);

            yield return WaitForTask(manager.InitializeAsync(AssetRuntimeMode.EditorSimulate), "初始化");

            AssetHandle<TextAsset> handle = manager.LoadAssetAsync<TextAsset>(TestAssetAddress);
            yield return WaitForHandle(handle, "加载 " + TestAssetAddress);

            Assert.AreEqual(
                AssetStatus.Succeeded, handle.Status,
                "加载失败：" + handle.Error + "\n" +
                "若提示找不到地址，请把收集器窗口里显示的 Address 填进 TestAssetAddress（当前是 \"" +
                TestAssetAddress + "\"）");

            Assert.IsTrue(handle.IsValid);
            Assert.IsNotNull(handle.Asset, "Asset 为 null");

            // 断言**真实内容** —— 这才叫"链路通了"，而不是"拿到了一个非 null 对象"
            StringAssert.Contains(
                ExpectedText, handle.Asset.text,
                "加载到的内容不对。确认 Client/Assets/_Project/TestAssets/hello.txt 的内容没被改过");

            handle.Dispose();
            manager.UnloadUnusedAssets();

            yield return null;

            Assert.AreEqual(0, manager.GetDebugInfo().CachedCount, "UnloadUnusedAssets 之后缓存应被清空");
        }

        [UnityTest]
        public IEnumerator LoadAssetAsync_ReturnsRawAsset_NotInstance()
        {
            // 这条守的是我们**有意偏离**原框架的那个语义（Docs/06 §13.7）：
            // 原版 ResMgr 对 GameObject 会自动 Instantiate；我们改成返回原始资源。
            // 这个用例用 TextAsset 演示同一件事：句柄给的是资源本身，
            // 想要实例（GameObject 才有"实例"概念）得自己 Instantiate。
            Assert.IsTrue(Directory.Exists(SimulatePackageRoot), "先跑一次模拟构建");

            SingletonRegistry.ResetAll();
            AssetManager manager = AssetManager.Instance;
            YooAssetProvider.Install(PackageName, SimulatePackageRoot);

            yield return WaitForTask(manager.InitializeAsync(AssetRuntimeMode.EditorSimulate), "初始化");

            AssetHandle<TextAsset> first = manager.LoadAssetAsync<TextAsset>(TestAssetAddress);
            yield return WaitForHandle(first, "第一次加载");

            AssetHandle<TextAsset> second = manager.LoadAssetAsync<TextAsset>(TestAssetAddress);
            yield return WaitForHandle(second, "第二次加载");

            // 同一个地址 + 同一类型 -> **同一个对象**（共享资源），引用计数为 2
            Assert.AreSame(first.Asset, second.Asset, "同一地址应返回同一个共享资源对象");
            Assert.AreEqual(2, first.RefCount, "两次加载应把引用计数累加到 2");

            first.Dispose();
            Assert.AreEqual(1, second.RefCount, "释放一次后计数减到 1");

            second.Dispose();
            manager.UnloadUnusedAssets();
        }

        // ====================================================================
        //  工具
        // ====================================================================

        /// <summary>等一个 Task 完成（带超时，避免测试永远挂着）。</summary>
        private static IEnumerator WaitForTask(Task task, string what)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Assert.Fail(what + " 超时（30 秒）。若卡在初始化，检查 packageRoot 路径是否正确。");
                }

                yield return null;
            }

            if (task.IsFaulted)
            {
                Assert.Fail(what + " 抛出异常：" + task.Exception);
            }
        }

        /// <summary>等一个资源句柄完成。</summary>
        private static IEnumerator WaitForHandle(IAssetHandle handle, string what)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!handle.IsDone)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Assert.Fail(what + " 超时（30 秒）");
                }

                yield return null;
            }
        }
    }
}
