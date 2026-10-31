// 这个文件里有：
//   AssetRuntimeMode（枚举）：资源运行模式，规定内容从工程原始资源、随包内容还是资源服务器来
//   AssetStatus（枚举）：资源加载状态，未开始 / 加载中 / 成功 / 失败用这四个值表示
//   AssetDebugInfo（结构体）：资源层的观测快照，按状态统计缓存条目数，用来查资源泄漏
//   IAssetHandle（接口）：加载结果的只读视图，给出地址、状态、进度、错误、原始资源与引用计数
//   IAssetProvider（接口）：底层加载器的契约，由适配层实现，框架只认这个接口
//   IAssetLoadOperation（接口）：一次底层加载的句柄，适配层把资源库的句柄翻译成这个形状
// 注意：框架侧不认识任何具体资源库，引用计数与句柄生命周期都留在这里，换资源库只动适配层。

using System;
using System.Collections.Generic;

namespace NBC.Framework.Asset
{
    // 主要功能：规定资源内容从哪来 —— 编辑器直接读工程资源、只用随包内容、还是从资源服务器下载。
    // 主要数据成员：三个枚举值 EditorSimulate / Offline / Host，无字段，取值 0..2。
    // 主要外部方法：无（纯枚举）；AssetManager.InitializeAsync 把模式转给加载器，适配层按它决定建包参数。
    // 谁在用它：AssetBootstrapper.InstallAsync 与演示脚本的序列化字段选模式，引导菜单与冒烟测试按它跑指定模式。
    // 注意：Host 目前只定义了入口，适配层还没实现热更，选它会在初始化阶段抛异常。
    /// <summary>资源运行模式：决定内容从工程原始资源、随包内容还是资源服务器来；取值 0..2。</summary>
    public enum AssetRuntimeMode
    {
        /// <summary>编辑器模拟：直接读工程里的原始资源，免打包迭代，只在编辑器内可用。</summary>
        EditorSimulate,

        /// <summary>单机：只用随包内容，不联网也不检查更新。</summary>
        Offline,

        /// <summary>联机：从资源服务器下载更新，是热更的落点。</summary>
        Host,
    }

    // 主要功能：给一次加载定四个状态，业务只依赖这四个值，不接触资源库自己的状态枚举。
    // 主要数据成员：四个枚举值 None / Loading / Succeeded / Failed，无字段，取值 0..3。
    // 主要外部方法：无（纯枚举）；由 IAssetHandle / ISceneHandle / IAssetLoadOperation 的 Status 属性读出。
    // 谁在用它：AssetManager 的缓存条目与失败重试、SceneLoader 的收尾判断、适配层的状态翻译、EditMode 与冒烟测试的断言。
    // 注意：刻意与 YooAsset 的 EOperationStatus 分开（名字与数值都不同），映射只放在适配层，换资源库时业务层不用改。
    // 注意：Succeeded 不等于「资源一定可用」—— AssetObject 仍可能为 null，使用前要判空（AssetHandle.IsValid 就是 Status 与资源一起判）。
    /// <summary>资源加载状态：未开始 / 加载中 / 成功 / 失败；刻意与底层资源库的状态枚举分开。</summary>
    public enum AssetStatus
    {
        /// <summary>尚未开始：操作刚创建、还没进入加载时是这个值；不认识的底层状态也按它兜底。</summary>
        None,

        /// <summary>加载中：完成之前一直停在这个值，此时 AssetObject 还不能用。</summary>
        Loading,

        /// <summary>加载成功：资源已可取，但 AssetObject 仍可能为 null（加载器报告成功却没给出资源时也会走到这里）。</summary>
        Succeeded,

        /// <summary>加载失败：原因读句柄的 Error；失败条目不会被复用，下次同地址加载会丢掉重建、再试一次。</summary>
        Failed,
    }

    // 主要功能：把某一刻缓存条目的数量按状态分组，让调试面板与测试一眼看出有没有资源泄漏。
    // 主要数据成员：六个只读计数 CachedCount / ActiveCount / UnusedCount / LoadingCount / FailedCount / TotalRefCount，都是条目数，只有 TotalRefCount 是引用计数之和。
    // 主要外部方法：构造函数（原样装入六项计数）、ToString（一行摘要）。
    // 谁在用它：AssetManager.GetDebugInfo 组装并返回，性能 / 调试面板与 EditMode、PlayMode 测试读字段或调 ToString。
    // 注意：这是快照，构造之后缓存再怎么变都不会反映到这份数据上；要看最新值就重新取一次。
    /// <summary>资源层的观测快照：六个计数都是某一刻的条目数，用来判断有没有资源泄漏。</summary>
    public readonly struct AssetDebugInfo
    {
        /// <summary>缓存条目总数（每个「地址 + 类型」算一条），≥ 0。</summary>
        public readonly int CachedCount;

        /// <summary>引用计数大于 0 的条目数（正被使用者持有），0..CachedCount；业务方都放手后仍不降，说明有句柄漏了 Dispose。</summary>
        public readonly int ActiveCount;

        /// <summary>引用计数为 0、等待 ReleaseUnused 回收的条目数，0..CachedCount；一直不降说明没人调 ReleaseUnused，条目会一直占内存。</summary>
        public readonly int UnusedCount;

        /// <summary>还没结束（既没成功也没失败）的条目数，0..CachedCount。</summary>
        public readonly int LoadingCount;

        /// <summary>加载失败的条目数，0..CachedCount；失败条目在下次同地址加载时会被丢掉重建。</summary>
        public readonly int FailedCount;

        /// <summary>所有条目的引用计数之和，≥ 0；正常不会小于 ActiveCount。</summary>
        public readonly int TotalRefCount;

        /// <summary>按给定的六项计数构造快照；原样存入，不校验也不重算。</summary>
        // 功能：把六项统计装进只读快照，供面板一次读走。
        // 入参：cachedCount 缓存条目总数；activeCount 引用计数大于 0 的条目数；unusedCount 计数为 0 的条目数；loadingCount 未结束的条目数；failedCount 失败条目数；totalRefCount 引用计数之和（都是条目数，≥ 0）
        // 返回：无（构造函数）
        // 步骤：按参数顺序逐个赋给只读字段，不校验也不重算 —— 六项由 AssetManager.GetDebugInfo 在同一趟遍历里算出，分开算会对不上
        // 上游：AssetManager.GetDebugInfo
        // 下游：无
        // 注意：这里不做一致性校验，手工构造时传错（例如 activeCount 大于 cachedCount）不会被拦下。
        public AssetDebugInfo(int cachedCount, int activeCount, int unusedCount,
                              int loadingCount, int failedCount, int totalRefCount)
        {
            CachedCount = cachedCount;
            ActiveCount = activeCount;
            UnusedCount = unusedCount;
            LoadingCount = loadingCount;
            FailedCount = failedCount;
            TotalRefCount = totalRefCount;
        }

        /// <summary>拼一行摘要（条目 / 活跃 / 待回收 / 加载中 / 失败 / 引用计数合计），面板可直接打印。</summary>
        /// <returns>单行文本，各项都是条目数，不会为 null。</returns>
        // 功能：把六项计数拼成一行可读文本，给调试面板与日志用。
        // 入参：无（重写 object.ToString）
        // 返回：单行文本，形如「条目=2（活跃=1 待回收=1 加载中=0 失败=1）引用计数合计=1」，不会为 null
        // 步骤：按构造参数的顺序拼接，方便与 GetDebugInfo 的字段一一对照；每次调用新建字符串，别在每帧热路径里反复调
        // 上游：调试面板、日志
        // 下游：无
        public override string ToString()
        {
            return "条目=" + CachedCount + "（活跃=" + ActiveCount + " 待回收=" + UnusedCount +
                   " 加载中=" + LoadingCount + " 失败=" + FailedCount + "）引用计数合计=" + TotalRefCount;
        }
    }

    // 主要功能：给一次加载的结果一个只读视图 —— 地址、状态、进度、错误、原始资源与引用计数，Dispose 即释放一份引用。
    // 主要数据成员：无字段（纯接口）；状态与资源在实现类 AssetHandle{T} 里，引用计数由 AssetManager 的缓存条目持有。
    // 主要外部方法：Location / Status / IsDone / Progress / Error / AssetObject / RefCount 七个只读属性，WaitForAsyncComplete，以及继承来的 Dispose。
    // 谁在用它：AssetHandle{T} 是唯一实现；PlayMode 冒烟测试用它与泛型无关地轮询状态，业务层通常直接拿泛型句柄。
    // 注意：句柄释放后再读 Status，底层操作若已被回收会变成 Failed、Error 变成「底层操作已被释放」，释放后别再把句柄当仍然有效的凭据。
    // 注意：Dispose 是幂等的（重复释放安全），但每次只减一次计数；只 Dispose 不卸载，计数归零的条目要等 AssetManager.ReleaseUnused 才真正释放。
    /// <summary>一次加载结果的只读视图：地址、状态、进度、错误、原始资源与引用计数；Dispose 释放一份引用。</summary>
    public interface IAssetHandle : IDisposable
    {
        /// <summary>资源地址（资源库里的 Address），加载时传入、之后不变。</summary>
        string Location { get; }

        /// <summary>当前状态，AssetStatus 四个取值之一；底层操作被回收后会变成 Failed。</summary>
        AssetStatus Status { get; }

        /// <summary>是否已结束（Succeeded 与 Failed 都算），false 时进度还会变。</summary>
        bool IsDone { get; }

        /// <summary>加载进度，0..1 的浮点值；只在 IsDone 为 false 时有推进意义。</summary>
        float Progress { get; }

        /// <summary>失败原因文本；未失败时是空串（不会为 null）。</summary>
        string Error { get; }

        /// <summary>类型擦除后的原始资源（不是实例）；未完成或失败时为 null，GameObject 要调用方自己 Instantiate。</summary>
        UnityEngine.Object AssetObject { get; }

        /// <summary>当前引用计数（含本句柄这一份），正常 ≥ 1；Dispose 后可能归 0，条目要等 ReleaseUnused 才真正释放。</summary>
        int RefCount { get; }

        /// <summary>阻塞到加载结束（成功或失败都返回）；只给编辑器 / 工具用，Player 里会卡主线程。</summary>
        // 功能：把异步加载同步化，让编辑器工具与测试不必写协程就能拿到结果。
        // 入参：无
        // 返回：无；返回只代表加载结束了，成功与否还要读 Status
        // 步骤：1) 直接转给底层操作去等，本层不做超时也不重试 2) 等待结束后不判成败，调用方自己看 Status
        // 上游：编辑器工具、EditMode / PlayMode 测试；Player 运行时代码不应调用
        // 下游：IAssetLoadOperation.WaitForAsyncComplete（再往下是资源库的等待）
        // 注意：Player 里会阻塞主线程，加载大资源就是卡死；这是受限接口，运行时应轮询 IsDone，不要用它。
        void WaitForAsyncComplete();
    }

    // 主要功能：定义底层加载器必须会做的事 —— 初始化、按地址取资源、加载与卸载场景、清掉没人用的资源，框架只经这个接口碰资源库。
    // 主要数据成员：无字段（纯接口）；包、清单与底层句柄由实现方自己持有。
    // 主要外部方法：InitializeAsync / LoadAsync / LoadSync / LoadSceneAsync / UnloadScene / UnloadUnused / UnloadAll，以及 IsInitialized。
    // 谁在用它：AssetManager 持有并调用；生产实现是 YooAssetProvider，AssetBootstrapper 负责安装并初始化；EditMode 与 PlayMode 测试用假实现替换。
    // 注意：换加载器前必须先 UnloadAll 把旧加载器的资源清干净，否则旧加载器持有的资源会变成孤儿（AssetManager.SetProvider 不允许中途换加载器）。
    /// <summary>底层加载器的契约：初始化、加载资源与场景、卸载；由适配层实现，框架只认这个接口。</summary>
    public interface IAssetProvider
    {
        /// <summary>是否已初始化完成；false 时加载一律不该开始（AssetManager 会先抛异常）。</summary>
        bool IsInitialized { get; }

        /// <summary>建包、加载清单，完成之后才能加载资源；内容来源由运行模式决定。</summary>
        /// <param name="mode">资源运行模式：编辑器模拟 / 单机 / 联机，决定读工程资源、随包内容还是远程内容。</param>
        /// <returns>初始化完成的 Task；失败以异常形式抛出，没有返回码。</returns>
        // 功能：按运行模式把资源库准备好（建包、加载清单），失败时抛出而不是留下半初始化状态。
        // 入参：mode 运行模式，三个取值之一
        // 返回：Task 完成表示初始化成功；失败通过 Task 异常抛出，没有返回码
        // 步骤：
        //   1) 按 mode 选包参数（编辑器模拟 / 离线 / 联机），联机模式还要资源服务器与下载器配合
        //   2) 加载版本与清单，任何一步失败都直接抛，不把 IsInitialized 置 true
        //   3) 全部成功后才算就绪，之后 LoadAsync / LoadSync / LoadSceneAsync 才允许开始
        // 上游：AssetManager.InitializeAsync，通常由引导流程 AssetBootstrapper.InstallAsync 调一次
        // 下游：实现方（YooAssetProvider 建包、加载版本与清单）
        // 注意：初始化失败必须抛出来，吞掉会让后面所有加载变成「资源为空」的怪现象，而不是一条清楚的初始化错误。
        System.Threading.Tasks.Task InitializeAsync(AssetRuntimeMode mode);

        /// <summary>按地址异步取一个原始资源；只发起加载，不等待完成，也不做缓存去重（去重在上层 AssetManager）。</summary>
        /// <returns>底层加载操作；不能为 null，调用方拿到后要自己 Dispose（正常路径由 AssetManager 的缓存条目负责）。</returns>
        // 功能：向资源库发起一次异步加载，把句柄包成框架的 IAssetLoadOperation 交出去。
        // 入参：location 资源地址（资源库里的 Address，不能为空）；assetType 期望类型，用于类型校验与上层的缓存键
        // 返回：底层加载操作；返回后加载可能还没完成，结果看 Status 与 OnComplete；返回 null 视为实现错误（AssetManager 会抛）
        // 步骤：
        //   1) 按地址与类型向资源库发起加载，立刻包装返回，不等完成
        //   2) 失败也走同一个完成通知，Status 置 Failed、Error 写原因 —— 异步失败不抛异常
        // 上游：AssetManager.CreateEntry（LoadAssetAsync 首次请求时）
        // 下游：实现方（YooAssetProvider 转 ResourcePackage.LoadAssetAsync）
        // 注意：异步失败不抛异常，只把 Status 置 Failed；调用方必须看 Status / IsDone，不能只等资源。
        // 注意：返回的操作由上层缓存条目持有并负责 Dispose，业务层不要直接释放它 —— 缓存条目会拿着一个已释放的操作，后续读状态与回收都会出错。
        IAssetLoadOperation LoadAsync(string location, Type assetType);

        /// <summary>按地址同步取一个原始资源；返回时已完成，但会阻塞主线程。</summary>
        /// <returns>底层加载操作，返回时已完成；是否成功读 Status。</returns>
        // 功能：同 LoadAsync，但走资源库的同步接口，返回时资源已经加载完。
        // 入参：location 资源地址（不能为空）；assetType 期望类型
        // 返回：底层加载操作，返回时已完成；是否成功读 Status
        // 步骤：1) 按地址与类型向资源库发起同步加载 2) 包装返回，此时 Status 已经冻结在 Succeeded 或 Failed
        // 上游：AssetManager.LoadAsset（同步门面，只在编辑器工具或极小资源上用）
        // 下游：实现方（YooAssetProvider 转 ResourcePackage.LoadAssetSync）
        // 注意：Player 里会阻塞主线程，大资源就是卡顿来源；播放模式下 AssetManager 也会打警告，只应用于编辑器工具或极小资源。
        IAssetLoadOperation LoadSync(string location, Type assetType);

        /// <summary>按地址异步加载一个场景；场景也走资源层，不直接调 SceneManager。</summary>
        /// <param name="mode">加载模式：Single 切到新场景，Additive 叠加到当前场景。</param>
        /// <returns>底层场景操作；场景没有引用计数，句柄 Dispose 时就卸载。</returns>
        // 功能：向资源库发起一次场景加载，把场景句柄包成框架的 ISceneLoadOperation。
        // 入参：location 场景地址（资源库里的 Address，不能为空）；mode 单场景或叠加
        // 返回：底层场景操作；加载完成前场景无效，加载可能还没结束
        // 步骤：
        //   1) 按地址与加载模式向资源库发起场景加载，立刻包装返回
        //   2) 完成通知走 OnComplete；单场景与叠加都走这一条路，不做缓存
        // 上游：AssetManager.LoadSceneAsync（再往上是 SceneLoader.Load）
        // 下游：实现方（YooAssetProvider 转 ResourcePackage.LoadSceneAsync）
        // 注意：场景没有引用计数语义 —— 说不要了就该卸载，所以释放由 ISceneHandle.Dispose 触发 UnloadScene，不走 UnloadUnused。
        ISceneLoadOperation LoadSceneAsync(string location, UnityEngine.SceneManagement.LoadSceneMode mode);

        /// <summary>卸载一个已加载的场景；由 ISceneHandle.Dispose 触发，业务不直接调。</summary>
        /// <param name="operation">当初加载它时返回的那个场景操作；为 null 时什么都不做。</param>
        // 功能：把框架侧的卸载请求落到资源库：真卸载场景，再释放场景句柄。
        // 入参：operation 当初 LoadSceneAsync 返回的操作，不能拿别的操作来代替
        // 返回：无
        // 步骤：
        //   1) 用当初那个操作发起卸载（资源库只认自己的句柄）
        //   2) 卸载与释放句柄一起做，顺序不能反 —— 先释放就再也拿不到卸载操作，场景会一直留在内存里
        // 上游：框架侧 SceneHandle.Dispose（业务经 ISceneHandle.Dispose 间接触发）
        // 下游：实现方（YooAssetProvider 转 YooAssetSceneOperation.UnloadAndDispose）
        // 注意：只释放句柄不会卸载场景，两件事必须都做；同一个操作不要卸载两次。
        void UnloadScene(ISceneLoadOperation operation);

        /// <summary>让底层卸载已经没人用的资源；只发起，不等待完成。</summary>
        // 功能：让资源库回收它认为没人引用的资源（引擎级卸载），框架侧先清掉计数归零的条目再调它。
        // 入参：无
        // 返回：无（异步发起，完成与否不阻塞调用方）
        // 步骤：
        //   1) 实现方发起资源库的无用资源卸载，本层不等待完成
        //   2) 失败由实现方记日志，不在这里抛
        // 上游：AssetManager.UnloadUnusedAssets（切场景、回大厅这类收尾时机）
        // 下游：实现方（YooAssetProvider 转 UnloadUnusedAssetsAsync）
        // 注意：句柄还拿着的条目不会被回收，计数没归零就调它等于什么都没做。
        void UnloadUnused();

        /// <summary>卸载本加载器管理的全部资源（切场景 / 换加载器收尾）；调用后先前发出的操作全部失效。</summary>
        // 功能：把本加载器管的所有资源与缓存一次性清掉，用于整场收尾。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 释放本加载器持有的全部底层句柄与缓存条目
        //   2) 只清资源，不改 IsInitialized，之后仍然可以继续加载
        // 上游：AssetManager.UnloadAll（切场景、回大厅、换加载器之前）
        // 下游：实现方（YooAssetProvider 转 UnloadAllAssetsAsync）
        // 注意：调用后先前发出的句柄与操作全部失效，再读它们的 Status 会变成 Failed；不要把它当「释放一部分」用。
        void UnloadAll();
    }

    // 主要功能：包住一次底层加载 —— 给出地址、状态、进度、错误与原始资源，用 OnComplete 通知结束，Dispose 释放底层句柄。
    // 主要数据成员：无字段（纯接口）；状态、进度与底层句柄由实现方持有。
    // 主要外部方法：Location / Status / Progress / Error / AssetObject / IsDone 六个只读属性，OnComplete 事件，WaitForAsyncComplete 与 Dispose。
    // 谁在用它：AssetManager 的缓存条目 Entry 订阅 OnComplete 并在丢弃条目时 Dispose；YooAssetProvider 造出它；失败重试与调试统计读 Status；测试用假实现造各种完成 / 失败情形。
    // 注意：OnComplete 可能在你订阅之前就已经触发过（底层同步完成时），判断是否结束要以 IsDone 兜底，不能只等事件。
    // 注意：Dispose 释放的是底层句柄，由缓存条目负责；业务层只 Dispose IAssetHandle，不要直接释放本操作。
    /// <summary>一次底层加载的句柄：暴露状态、进度、错误与原始资源，用 OnComplete 通知结束，Dispose 释放底层句柄。</summary>
    public interface IAssetLoadOperation : IDisposable
    {
        /// <summary>资源地址（资源库里的 Address），加载时传入、之后不变。</summary>
        string Location { get; }

        /// <summary>当前状态，AssetStatus 四个取值之一；结束后冻结在 Succeeded 或 Failed。</summary>
        AssetStatus Status { get; }

        /// <summary>加载进度，0..1 的浮点值；完成后不再变化。</summary>
        float Progress { get; }

        /// <summary>失败原因文本；未失败时是空串（实现方会把 null 兜成空串）。</summary>
        string Error { get; }

        /// <summary>加载到的原始资源（不是实例）；未完成或失败时为 null，GameObject 要调用方自己 Instantiate。</summary>
        UnityEngine.Object AssetObject { get; }

        /// <summary>是否已结束，成功与失败都算。</summary>
        bool IsDone { get; }

        /// <summary>完成通知，参数是操作本身；只触发一次，可能早于订阅（同步完成时），订阅方要先用 IsDone 兜底。</summary>
        event Action<IAssetLoadOperation> OnComplete;

        /// <summary>阻塞到加载结束（成功或失败都返回）；只给编辑器 / 工具用，Player 里会卡主线程。</summary>
        // 功能：把异步加载同步化，供编辑器工具与测试直接拿结果。
        // 入参：无
        // 返回：无；返回只代表加载结束，成功与否读 Status
        // 步骤：1) 直接转给资源库的句柄去等，本层不做超时也不重试 2) 等完不判成败，调用方自己读 Status
        // 上游：IAssetHandle.WaitForAsyncComplete（泛型句柄转下来）、编辑器工具与测试
        // 下游：实现方（YooAssetProvider 转底层句柄的 WaitForAsyncComplete）
        // 注意：Player 里会阻塞主线程；运行时应轮询 IsDone 或等 OnComplete。
        void WaitForAsyncComplete();
    }
}
