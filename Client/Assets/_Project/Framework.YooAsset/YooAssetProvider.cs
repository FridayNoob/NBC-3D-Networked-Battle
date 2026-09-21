// ============================================================================
//  NBC.Framework.Asset.Adapter · YooAsset v3 的加载器实现
//  对应需求：FW-M05 / FW-M17 / YOO-03（运行模式）/ YOO-09（业务层不出现 YooAsset）
//  程序集：NBC.Framework.YooAsset（**只有这里**允许出现 YooAsset 类型）
//
//  ---------------------------------------------------------------------------
//  为什么要单独一个程序集
//  ---------------------------------------------------------------------------
//  ADR-001 决策 6：`NBC.Framework` 的 `references` 必须为空（底座不依赖任何东西，含第三方）。
//  于是"要接触 YooAsset 的代码"只能放在这里：
//
//      NBC.Game  ->  NBC.Framework  <-  NBC.Framework.YooAsset  ->  YooAsset
//      (业务)        (AssetManager)      (本程序集)                 (第三方)
//
//  换资源方案时，**删掉本程序集即可**，框架与业务零改动。
//
//  ---------------------------------------------------------------------------
//  命名空间为什么叫 Adapter 而不是 ...YooAsset
//  ---------------------------------------------------------------------------
//  如果命名空间写成 `NBC.Framework.Asset.YooAsset`，那么在本文件里写 `YooAsset.ResourcePackage`
//  会先解析到**外层命名空间自己**，和 `using YooAsset;` 引入的第三方命名空间打架
//  （C# 里这类同名遮蔽问题排查起来很费时间）。所以刻意避开同名：
//  **程序集名** `NBC.Framework.YooAsset`（一眼看出它管什么），**命名空间** `...Asset.Adapter`。
//
//  ---------------------------------------------------------------------------
//  三种运行模式（YOO-03）
//  ---------------------------------------------------------------------------
//    EditorSimulate -> `EditorSimulateModeOptions` + `CreateDefaultEditorFileSystemParameters(packageRoot)`
//                      （编辑器里免打包迭代；`packageRoot` 是**模拟清单所在目录**，必须显式给）
//    Offline        -> `OfflinePlayModeOptions`  + `CreateDefaultBuiltinFileSystemParameters()`
//    Host           -> M5 热更阶段再实现（需要 `IRemoteService` 与资源服务器），当前明确抛异常
//
//  ⚠️ 这些类型名都是 **v3** 的。v2.3 的 `EditorSimulateModeParameters` 等默认不参与编译，见 `Docs\05`「一·补」。
// ============================================================================

using System;
using System.Threading.Tasks;
using NBC.Framework.Asset;
using UnityEngine;
using YooAsset;

namespace NBC.Framework.Asset.Adapter
{
    /// <summary>
    /// 用 YooAsset v3 实现的底层加载器。
    /// </summary>
    public sealed class YooAssetProvider : IAssetProvider
    {
        /// <summary>包名。</summary>
        private readonly string m_packageName;

        /// <summary>编辑器模拟模式用的模拟清单目录。</summary>
        private readonly string m_editorSimulatePackageRoot;

        /// <summary>YooAsset 的包对象。</summary>
        private ResourcePackage m_package;

        /// <summary>
        /// 构造加载器。
        /// </summary>
        /// <param name="packageName">YooAsset 的包名（在 YooAsset 编辑器窗口里配置的那个）。</param>
        /// <param name="editorSimulatePackageRoot">
        /// 编辑器模拟模式的模拟清单目录。**只在 EditorSimulate 模式需要**；
        /// 具体取值由 YooAsset 的"模拟构建"输出决定（见 B3 操作单）。
        /// </param>
        public YooAssetProvider(string packageName, string editorSimulatePackageRoot = "")
        {
            if (string.IsNullOrEmpty(packageName))
            {
                throw new ArgumentException("[YooAssetProvider] 包名不能为空。", nameof(packageName));
            }

            m_packageName = packageName;
            m_editorSimulatePackageRoot = editorSimulatePackageRoot ?? string.Empty;
        }

        /// <summary>包名。</summary>
        public string PackageName
        {
            get { return m_packageName; }
        }

        /// <summary>是否已初始化完成。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>
        /// 一步完成引导：造加载器 + 装到 <c>AssetManager</c> 上。
        /// 引导代码里写一行就够：<c>YooAssetProvider.Install("NBCMain", root);</c>
        /// </summary>
        /// <param name="packageName">包名。</param>
        /// <param name="editorSimulatePackageRoot">编辑器模拟清单目录。</param>
        /// <returns>装好的加载器。</returns>
        public static YooAssetProvider Install(string packageName, string editorSimulatePackageRoot = "")
        {
            YooAssetProvider provider = new YooAssetProvider(packageName, editorSimulatePackageRoot);
            AssetManager.Instance.SetProvider(provider);
            return provider;
        }

        /// <summary>
        /// 初始化：全局初始化 YooAsset → 建包（或取已有包）→ 按模式初始化包。
        /// </summary>
        /// <param name="mode">运行模式。</param>
        public async Task InitializeAsync(AssetRuntimeMode mode)
        {
            if (!YooAssets.IsInitialized)
            {
                YooAssets.Initialize();
            }

            ResourcePackage existing;
            m_package = YooAssets.TryGetPackage(m_packageName, out existing)
                ? existing
                : YooAssets.CreatePackage(m_packageName);

            InitializePackageOptions options = BuildOptions(mode);

            // `InitializePackageOperation` 继承自 `AsyncOperationBase`，而后者的
            // `GetAwaiter()` 返回公开的 `OperationAwaiter` 结构 —— 所以可以直接 await。
            InitializePackageOperation operation = m_package.InitializePackageAsync(options);
            await operation;

            if (operation.Status != EOperationStatus.Succeeded)
            {
                throw new InvalidOperationException(
                    "[YooAssetProvider] 包【" + m_packageName + "】初始化失败：" + operation.Error);
            }

            IsInitialized = true;
        }

        /// <summary>异步加载一个资源（拿到的是**原始资源**，不是实例）。</summary>
        /// <param name="location">资源地址。</param>
        /// <param name="assetType">期望类型。</param>
        public IAssetLoadOperation LoadAsync(string location, Type assetType)
        {
            ThrowIfNotReady();
            AssetHandle handle = m_package.LoadAssetAsync(location, assetType);
            return new YooAssetLoadOperation(location, handle);
        }

        /// <summary>同步加载一个资源（**受限**：会阻塞主线程，见 ADR-001 决策 5）。</summary>
        /// <param name="location">资源地址。</param>
        /// <param name="assetType">期望类型。</param>
        public IAssetLoadOperation LoadSync(string location, Type assetType)
        {
            ThrowIfNotReady();
            AssetHandle handle = m_package.LoadAssetSync(location, assetType);
            return new YooAssetLoadOperation(location, handle);
        }

        /// <summary>
        /// 让 YooAsset 卸载已经没人用的资源。
        /// <para>
        /// ⚠️ 底层是**异步**的：这里只负责发起，完成与否不阻塞调用方；
        /// 失败会打一条错误日志（不静默吞掉）。
        /// </para>
        /// </summary>
        public void UnloadUnused()
        {
            if (m_package == null)
            {
                return;
            }

            var operation = m_package.UnloadUnusedAssetsAsync();
            operation.Completed += OnBackgroundOperationCompleted;
        }

        /// <summary>卸载本包全部资源（切场景 / 整场收尾）。同样是异步发起。</summary>
        public void UnloadAll()
        {
            if (m_package == null)
            {
                return;
            }

            var operation = m_package.UnloadAllAssetsAsync();
            operation.Completed += OnBackgroundOperationCompleted;
        }

        /// <summary>
        /// 收尾：卸下本包，并关掉 YooAsset 的全局状态。
        /// <para>
        /// 用途：**测试之间的隔离**、以及进程退出前的清理。
        /// 调过之后 <see cref="IsInitialized"/> 变回 false，再加载会抛带指引的异常。
        /// </para>
        /// <para>
        /// ⚠️ `YooAssets.Destroy()` 是**全局**的：它会把所有包一起关掉。
        /// 本项目 M1 只有一个包，所以这样没问题；将来有多包时要改成按包收尾。
        /// </para>
        /// </summary>
        public void Shutdown()
        {
            m_package = null;
            IsInitialized = false;

            if (YooAssets.IsInitialized)
            {
                YooAssets.Destroy();
            }
        }

        /// <summary>
        /// 把**当前装在 `AssetManager` 上的**加载器收尾掉（如果有）。
        /// <para>
        /// 这个静态入口是为"调用方不想、也不应该引用 YooAsset 类型"而存在的：
        /// 例如测试的 TearDown 需要重置全局状态，但它不该因此去 `using YooAsset;`
        /// —— 那既破坏"只有适配层接触 YooAsset"的边界，**而且根本编译不过**
        /// （Unity 的 asmdef 引用**不传递**：测试引用了适配层，也看不到适配层引用的 YooAsset）。
        /// </para>
        /// </summary>
        /// <returns>真的收尾了才返回 true。</returns>
        public static bool ShutdownInstalled()
        {
            if (!AssetManager.HasInstance)
            {
                return false;
            }

            YooAssetProvider provider = AssetManager.Instance.Provider as YooAssetProvider;
            if (provider == null)
            {
                return false;
            }

            provider.Shutdown();
            return true;
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>按运行模式构造 YooAsset 的初始化参数。</summary>
        private InitializePackageOptions BuildOptions(AssetRuntimeMode mode)
        {
            switch (mode)
            {
                case AssetRuntimeMode.EditorSimulate:
                    if (string.IsNullOrEmpty(m_editorSimulatePackageRoot))
                    {
                        throw new InvalidOperationException(
                            "[YooAssetProvider] 编辑器模拟模式必须提供模拟清单目录（editorSimulatePackageRoot）。" +
                            "它由 YooAsset 的『模拟构建』输出决定，见 Docs\\16 的 B3 操作单。");
                    }

                    return new EditorSimulateModeOptions
                    {
                        EditorFileSystemParameters =
                            FileSystemParameters.CreateDefaultEditorFileSystemParameters(m_editorSimulatePackageRoot),
                    };

                case AssetRuntimeMode.Offline:
                    return new OfflinePlayModeOptions
                    {
                        BuiltinFileSystemParameters = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters(),
                    };

                case AssetRuntimeMode.Host:
                    // 联机模式需要资源服务器地址 + IRemoteService（下载器）。
                    // 这是 M5（热更）的内容，现在明确报错而不是给一个"看起来能用"的实现。
                    throw new NotSupportedException(
                        "[YooAssetProvider] 联机（热更）模式将在 M5 实现 —— 它需要资源服务器与下载器服务。");

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的运行模式");
            }
        }

        private static void OnBackgroundOperationCompleted(AsyncOperationBase operation)
        {
            if (operation.Status == EOperationStatus.Failed)
            {
                Debug.LogError("[YooAssetProvider] 后台卸载失败：" + operation.Error);
            }
        }

        private void ThrowIfNotReady()
        {
            if (!IsInitialized || m_package == null)
            {
                throw new InvalidOperationException(
                    "[YooAssetProvider] 还没初始化完成，不能加载。请先 await AssetManager.Instance.InitializeAsync(mode)。");
            }
        }
    }
}
