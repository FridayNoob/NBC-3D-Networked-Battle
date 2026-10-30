// 这个文件里有：
//   YooAssetProvider（类）：用 YooAsset v3 实现的底层加载器，负责三段式初始化包、资源与场景的加载 / 卸载、收尾。

using System;
using System.Threading.Tasks;
using NBC.Framework.Asset;
using UnityEngine;
using YooAsset;

namespace NBC.Framework.Asset.Adapter
{
    // 主要功能：用 YooAsset v3 实现底层加载器接缝，负责包的三段式初始化、资源与场景的加载 / 卸载、收尾。
    // 主要数据成员：m_packageName（包名）、m_editorSimulatePackageRoot（模拟清单目录，仅编辑器模拟模式用）、ManifestLoadTimeoutSeconds（清单超时，秒）、m_package（YooAsset 包对象）、IsInitialized（三段是否都成功）。
    // 主要外部方法：Install / InitializeAsync / LoadAsync / LoadSync / LoadSceneAsync / UnloadScene / UnloadUnused / UnloadAll / Shutdown / ShutdownInstalled，属性 PackageName / IsInitialized。
    // 谁在用它：NBC.Boot 的 AssetBootstrapper 用 Install 装上并初始化；AssetManager 只经 IAssetProvider 调它；PlayMode 冒烟测试直接 Install。
    // 注意：本程序集是唯一接触 YooAsset 的地方；命名空间叫 Asset.Adapter 是为了避开与第三方 YooAsset 的同名遮蔽；本文件用的都是 YooAsset v3 的类型名，v2.3 的名字默认不参与编译。
    /// <summary>底层加载器接缝的 YooAsset v3 实现。</summary>
    public sealed class YooAssetProvider : IAssetProvider
    {
        // 包名，必须与 YooAsset 编辑器窗口里配置的包名一致；取值范围为非空字符串，空串在构造时被拒
        private readonly string m_packageName;

        // 编辑器模拟模式的模拟清单目录（YooAsset「模拟构建」的输出目录）；只有 EditorSimulate 模式需要，其它模式可为空串
        private readonly string m_editorSimulatePackageRoot;

        // 加载包裹清单的超时时间，单位秒；固定 60
        private const int ManifestLoadTimeoutSeconds = 60;

        // YooAsset 的包对象；未初始化或 Shutdown 之后为 null
        private ResourcePackage m_package;

        /// <summary>构造加载器；此时还没有碰 YooAsset，真正建包在 InitializeAsync。</summary>
        /// <param name="packageName">YooAsset 的包名，必须是编辑器窗口里配置过的那个。</param>
        /// <param name="editorSimulatePackageRoot">编辑器模拟模式的模拟清单目录；只有 EditorSimulate 模式需要，其它模式传空串。</param>
        // 功能：存下包名与模拟清单目录，并挡住空包名。
        // 入参：packageName 包名，非空；editorSimulatePackageRoot 模拟清单目录，可为空（null 按空串存）
        // 返回：无
        // 步骤：1) 先校验包名，空则直接抛，免得后面在 YooAsset 里拿到一个无意义的包；2) 再存字段
        // 上游：Install，以及测试里的直接 new
        // 下游：无
        public YooAssetProvider(string packageName, string editorSimulatePackageRoot = "")
        {
            if (string.IsNullOrEmpty(packageName))
            {
                throw new ArgumentException("[YooAssetProvider] 包名不能为空。", nameof(packageName));
            }

            m_packageName = packageName;
            m_editorSimulatePackageRoot = editorSimulatePackageRoot ?? string.Empty;
        }

        /// <summary>本加载器使用的包名，就是构造时传入的那个。</summary>
        public string PackageName
        {
            get { return m_packageName; }
        }

        /// <summary>是否已完成三段式初始化；文件系统、版本、清单都成功后才为 true。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>一步完成装配：造一个加载器并装到 AssetManager 上，返回它。</summary>
        /// <param name="packageName">包名。</param>
        /// <param name="editorSimulatePackageRoot">编辑器模拟清单目录；只有 EditorSimulate 模式需要。</param>
        /// <returns>装好的加载器；后续初始化仍要调 AssetManager.InitializeAsync。</returns>
        // 功能：造加载器并装到 AssetManager，让引导代码只写一行。
        // 入参：packageName 包名；editorSimulatePackageRoot 模拟清单目录，默认空串
        // 返回：刚装上的加载器（还没初始化）
        // 步骤：1) 先 new，校验包名；2) 再 SetProvider 装上；3) 返回同一个实例给调用方留着直接调
        // 上游：AssetBootstrapper.InstallAsync、PlayMode 冒烟测试
        // 下游：AssetManager.SetProvider
        public static YooAssetProvider Install(string packageName, string editorSimulatePackageRoot = "")
        {
            YooAssetProvider provider = new YooAssetProvider(packageName, editorSimulatePackageRoot);
            AssetManager.Instance.SetProvider(provider);
            return provider;
        }

        /// <summary>初始化包：建文件系统、请求包裹版本、加载清单，三段缺一不可。</summary>
        /// <param name="mode">运行模式；Host（联机热更）当前会抛 NotSupportedException。</param>
        // 功能：把包从「有对象」带到「能加载资源」：先建文件系统，再请求版本，最后加载并激活清单。
        // 入参：mode 运行模式，决定文件系统怎么建（见 BuildOptions）
        // 返回：无；成功后 IsInitialized 为 true，失败抛 InvalidOperationException 并带上 YooAsset 的 Error
        // 步骤：
        //   1) YooAssets 没初始化就先初始化（全局只做一次）
        //   2) 包已存在就复用，没有才创建（包名在 YooAsset 里是全局键，重复 CreatePackage 会打架）
        //   3) 已初始化过的包跳过文件系统这一段：InitializePackageAsync 对已初始化的包会抛 already initialized，而引导流程可能被调两次，这里必须幂等
        //   4) 已经有活动清单（PackageValid 就是 ActiveManifest 不为 null）就直接收工 —— 这是重复初始化安全的关键
        //   5) 请求包裹版本；失败时提示去模拟清单目录里找 {包名}.version
        //   6) 加载清单 —— 只有这一步会装「活动清单」
        // 上游：AssetManager.InitializeAsync（引导由 AssetBootstrapper 发起，测试直接调）
        // 下游：BuildOptions、YooAssets 与 ResourcePackage 的异步操作
        // 注意：v3 的 InitializePackageAsync 只建文件系统，不等价于 v2 的一步到位。只做第一段时
        //   InitializeStatus 会报 Succeeded，但 ActiveManifest 仍是 null，加载时报 Active package manifest not found，
        //   而且从报错看不出缺了哪一段 —— 所以三段必须按顺序走完。
        public async Task InitializeAsync(AssetRuntimeMode mode)
        {
            if (!YooAssets.IsInitialized)
            {
                YooAssets.Initialize();
            }

            ResourcePackage existing;
            if (YooAssets.TryGetPackage(m_packageName, out existing))
            {
                m_package = existing;

            }
            else
            {
                m_package = YooAssets.CreatePackage(m_packageName);
            }

            if (m_package.InitializeStatus != EOperationStatus.Succeeded)
            {
                InitializePackageOptions options = BuildOptions(mode);

                InitializePackageOperation initOperation = m_package.InitializePackageAsync(options);
                await initOperation;

                if (initOperation.Status != EOperationStatus.Succeeded)
                {
                    throw new InvalidOperationException(
                        "[YooAssetProvider] 包【" + m_packageName + "】文件系统初始化失败：" + initOperation.Error);
                }
            }

            if (m_package.PackageValid)
            {
                IsInitialized = true;
                return;
            }

            RequestPackageVersionOperation versionOperation = m_package.RequestPackageVersionAsync();
            await versionOperation;

            if (versionOperation.Status != EOperationStatus.Succeeded)
            {
                throw new InvalidOperationException(
                    "[YooAssetProvider] 包【" + m_packageName + "】请求版本失败：" + versionOperation.Error +
                    "（EditorSimulate 模式下请确认模拟清单目录里有 {包名}.version 文件）");
            }

            LoadPackageManifestOperation manifestOperation = m_package.LoadPackageManifestAsync(
                new LoadPackageManifestOptions(versionOperation.PackageVersion, ManifestLoadTimeoutSeconds));
            await manifestOperation;

            if (manifestOperation.Status != EOperationStatus.Succeeded)
            {
                throw new InvalidOperationException(
                    "[YooAssetProvider] 包【" + m_packageName + "】加载清单失败（版本 " +
                    versionOperation.PackageVersion + "）：" + manifestOperation.Error);
            }

            IsInitialized = true;
        }

        /// <summary>异步加载一个资源，拿到的是原始资源而不是实例，实例化由调用方显式做。</summary>
        /// <param name="location">YooAsset 的资源地址。</param>
        /// <param name="assetType">期望的资源类型。</param>
        // 功能：把一次异步加载包成框架的加载操作，句柄的释放留给操作自己的 Dispose。
        // 入参：location 资源地址；assetType 期望类型
        // 返回：包装后的加载操作（未完成时先返回，完成 / 进度 / 状态都从它读）
        // 步骤：1) 先确认已就绪，没就绪直接抛；2) 交给 YooAsset 拿句柄；3) 用适配对象包起来
        // 上游：AssetManager.LoadAssetAsync（泛型门面）经 IAssetProvider 调到这里
        // 下游：ThrowIfNotReady、ResourcePackage.LoadAssetAsync、YooAssetLoadOperation
        public IAssetLoadOperation LoadAsync(string location, Type assetType)
        {
            ThrowIfNotReady();
            AssetHandle handle = m_package.LoadAssetAsync(location, assetType);
            return new YooAssetLoadOperation(location, handle);
        }

        /// <summary>同步加载一个资源；会阻塞主线程，只给工具 / 编辑器用。</summary>
        /// <param name="location">YooAsset 的资源地址。</param>
        /// <param name="assetType">期望的资源类型。</param>
        // 功能：同 LoadAsync，但走 YooAsset 的同步接口，返回时资源已经加载完。
        // 入参：location 资源地址；assetType 期望类型
        // 返回：包装后的加载操作（此时已完成，状态可直接读）
        // 步骤：1) 先确认已就绪；2) 调同步加载拿句柄；3) 包成适配对象
        // 上游：AssetManager.LoadAsset（同步门面，只在工具 / 编辑器路径上）
        // 下游：ThrowIfNotReady、ResourcePackage.LoadAssetSync、YooAssetLoadOperation
        // 注意：同步加载会卡住主线程，Player 里不要走这条路。
        public IAssetLoadOperation LoadSync(string location, Type assetType)
        {
            ThrowIfNotReady();
            AssetHandle handle = m_package.LoadAssetSync(location, assetType);
            return new YooAssetLoadOperation(location, handle);
        }

        /// <summary>异步加载一个场景（场景也走资源层，而不是直接调 SceneManager）。</summary>
        /// <param name="location">场景地址。</param>
        /// <param name="mode">加载模式（单场景 / 叠加）。</param>
        // 功能：把一次场景加载包成框架的场景操作，场景句柄的释放与卸载交给适配对象。
        // 入参：location 场景地址；mode 单场景或叠加
        // 返回：包装后的场景操作（加载完成前场景无效）
        // 步骤：1) 先确认已就绪；2) 交给 YooAsset 拿场景句柄；3) 包成 YooAssetSceneOperation
        // 上游：AssetManager.LoadSceneAsync
        // 下游：ThrowIfNotReady、ResourcePackage.LoadSceneAsync、YooAssetSceneOperation
        public ISceneLoadOperation LoadSceneAsync(string location, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            ThrowIfNotReady();
            YooAsset.SceneHandle handle = m_package.LoadSceneAsync(location, mode);
            return new YooAssetSceneOperation(location, handle);
        }

        /// <summary>卸载一个已加载的场景：真卸载场景并释放句柄，后台卸载失败会记日志。</summary>
        /// <param name="operation">当初加载它时返回的场景操作；为 null 时什么都不做。</param>
        // 功能：把框架侧的卸载请求落到 YooAsset，两件事都做：UnloadSceneAsync 真卸载、Dispose 释放句柄。
        // 入参：operation 当初 LoadSceneAsync 返回的操作
        // 返回：无
        // 步骤：
        //   1) 是适配层自己的场景操作才走真卸载，否则退回只 Dispose
        //   2) UnloadAndDispose 内部先卸载再释放句柄，已经释放过的返回 null
        //   3) 异步卸载的失败挂到统一回调上记日志
        // 上游：框架侧 SceneHandle.Dispose 触发，业务不直接调
        // 下游：YooAssetSceneOperation.UnloadAndDispose、OnBackgroundOperationCompleted
        // 注意：YooAsset 里 Dispose 只释放句柄，UnloadSceneAsync 才真的卸载场景；只做前者场景会一直留在内存里。
        public void UnloadScene(ISceneLoadOperation operation)
        {
            YooAssetSceneOperation sceneOperation = operation as YooAssetSceneOperation;
            if (sceneOperation != null)
            {
                var unloadOperation = sceneOperation.UnloadAndDispose();
                if (unloadOperation != null)
                {
                    unloadOperation.Completed += OnBackgroundOperationCompleted;
                }

                return;
            }

            if (operation != null)
            {
                operation.Dispose();
            }
        }

        /// <summary>让 YooAsset 卸载已经没人引用的资源；只发起，不等待完成。</summary>
        // 功能：发起一次「卸载无用资源」，失败交给统一回调记日志。
        // 入参：无
        // 返回：无（异步，完成与否不阻塞调用方）
        // 步骤：1) 包为空直接返回，Shutdown 之后再调也安全；2) 发起异步卸载；3) 挂完成回调
        // 上游：AssetManager.UnloadUnusedAssets
        // 下游：ResourcePackage.UnloadUnusedAssetsAsync、OnBackgroundOperationCompleted
        // 注意：底层是异步的，这里只负责发起；失败会打一条错误日志，不静默吞掉。
        public void UnloadUnused()
        {
            if (m_package == null)
            {
                return;
            }

            var operation = m_package.UnloadUnusedAssetsAsync();
            operation.Completed += OnBackgroundOperationCompleted;
        }

        /// <summary>卸载本包全部资源（切场景 / 整场收尾）；同样是异步发起。</summary>
        // 功能：发起一次「卸载本包全部资源」，失败交给统一回调记日志。
        // 入参：无
        // 返回：无（异步，完成与否不阻塞调用方）
        // 步骤：1) 包为空直接返回；2) 发起异步卸载；3) 挂完成回调
        // 上游：AssetManager.UnloadAll（切场景 / 回大厅）
        // 下游：ResourcePackage.UnloadAllAssetsAsync、OnBackgroundOperationCompleted
        // 注意：只卸载本包，不影响别的包；Shutdown 之后包为 null，调用会直接返回。
        public void UnloadAll()
        {
            if (m_package == null)
            {
                return;
            }

            var operation = m_package.UnloadAllAssetsAsync();
            operation.Completed += OnBackgroundOperationCompleted;
        }

        /// <summary>收尾：卸下本包并关掉 YooAsset 的全局状态，之后 IsInitialized 回到 false。</summary>
        // 功能：清掉本对象的包与已初始化标志，并销毁 YooAssets 的全局状态。
        // 入参：无
        // 返回：无
        // 步骤：1) 先清包与标志，保证之后任何加载都被 ThrowIfNotReady 挡住；2) 再销毁全局状态
        // 上游：ShutdownInstalled（当前没有生产调用点）
        // 下游：YooAssets.Destroy
        // 注意：YooAssets.Destroy 是全局的，会把所有包一起关掉；当前只有一个包所以没问题，将来多包要改成按包收尾。
        public void Shutdown()
        {
            m_package = null;
            IsInitialized = false;

            if (YooAssets.IsInitialized)
            {
                YooAssets.Destroy();
            }
        }

        /// <summary>把当前装在 AssetManager 上的加载器收尾掉；没装或装的不是本适配器时返回 false。</summary>
        /// <returns>真的调了 Shutdown 才返回 true。</returns>
        // 功能：给不想引用 YooAsset 类型的调用方一个静态收尾入口。
        // 入参：无
        // 返回：真收尾了返回 true；AssetManager 没有实例、加载器不是 YooAssetProvider、或没有加载器时返回 false
        // 步骤：1) 先看 AssetManager 有没有实例；2) 再取当前加载器并转成本类型；3) 都对上才 Shutdown
        // 上游：设计给测试 / 退出流程这类不该引用 YooAsset 的调用方（当前没有生产调用点）
        // 下游：Shutdown
        // 注意：这个静态入口是为「调用方不该引用 YooAsset」而存在的 —— asmdef 引用不传递，
        //   测试引用了适配层也看不到适配层引用的 YooAsset，直接写 YooAsset 类型根本编译不过。
        //   注意：Shutdown 之后再重新初始化会得到畸形状态（InitializeStatus 报 Succeeded 但 ActiveManifest 为 null），
        //   所以 PlayMode 测试刻意不在 TearDown 里调它。
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

        // 内部

        // 功能：按运行模式给出 YooAsset 的初始化参数：编辑器模拟、离线各一套。
        // 入参：mode 运行模式
        // 返回：InitializePackageOptions（具体是 EditorSimulateModeOptions 或 OfflinePlayModeOptions）
        // 步骤：
        //   1) EditorSimulate 必须给模拟清单目录，空则抛 —— 否则 YooAsset 会在错误的目录里找清单
        //   2) Offline 用内置文件系统（StreamingAssets），不需要目录
        //   3) Host 直接抛 NotSupportedException —— 热更需要资源服务器与下载器，现在给一个看起来能用的实现只会误事
        //   4) 其余值抛 ArgumentOutOfRangeException，挡住新增枚举没接线的漏网
        // 上游：InitializeAsync
        // 下游：YooAsset 的 FileSystemParameters 工厂
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
                    throw new NotSupportedException(
                        "[YooAssetProvider] 联机（热更）模式将在 M5 实现 —— 它需要资源服务器与下载器服务。");

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的运行模式");
            }
        }

        // 功能：后台卸载操作的完成回调，只在失败时打一条错误日志。
        // 入参：operation 已完成的 YooAsset 异步操作
        // 返回：无
        // 步骤：先看状态，失败才取 Error 打日志（成功不打，避免刷屏）
        // 上游：UnloadScene / UnloadUnused / UnloadAll 挂上的 Completed
        // 下游：Debug.LogError
        private static void OnBackgroundOperationCompleted(AsyncOperationBase operation)
        {
            if (operation.Status == EOperationStatus.Failed)
            {
                Debug.LogError("[YooAssetProvider] 后台卸载失败：" + operation.Error);
            }
        }

        // 功能：加载前的前置检查，没就绪就抛一句带指引的异常。
        // 入参：无
        // 返回：无；未初始化或包为 null 时抛 InvalidOperationException
        // 步骤：一次挡住两种没就绪的状态：还没初始化完，或 Shutdown 之后包被置空
        // 上游：LoadAsync / LoadSync / LoadSceneAsync
        // 下游：无
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
