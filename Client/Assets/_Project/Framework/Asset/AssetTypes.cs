// ============================================================================
//  NBC.Framework.Asset · 资源层的公共类型
//  对应需求：FW-M05（AssetManager）、YOO-03（运行模式）
//  接口来源：Docs/02-架构设计文档.md ADR-001（2026-09-20 已冻结）
//
//  ---------------------------------------------------------------------------
//  这一层为什么长这样：**框架不认识 YooAsset**
//  ---------------------------------------------------------------------------
//  ADR-001 决策 3 要求"业务层绝不出现 YooAsset 类型"，决策 6 又要求适配层独立成程序集。
//  于是框架侧只定义**自己的**类型与一个"底层加载器"接口，具体实现由适配层提供：
//
//      业务层  ->  AssetManager  ->  IAssetProvider  <-  YooAssetProvider（适配层程序集）
//      (NBC.Game)   (NBC.Framework)     (接口)            (NBC.Framework.YooAsset)
//
//  这条"依赖倒置"带来一个**很实用的副作用**：
//  引用计数、缓存、句柄生命周期这些**最容易出错的逻辑全在框架侧**，
//  于是可以用一个**假的加载器**（纯内存、立即完成）在 EditMode 里完整地测它们 ——
//  **不需要 YooAsset、不需要打包、结果还完全确定**。
//  这是 B2 能被验证的关键，不是巧合。
// ============================================================================

using System;
using System.Collections.Generic;

namespace NBC.Framework.Asset
{
    /// <summary>
    /// 资源运行模式（对应需求 YOO-03）。
    /// </summary>
    public enum AssetRuntimeMode
    {
        /// <summary>编辑器模拟：直接读工程里的原始资源，免打包迭代。</summary>
        EditorSimulate,

        /// <summary>单机：只用随包内容，不联网。</summary>
        Offline,

        /// <summary>联机：可从资源服务器下载更新（热更的落点）。</summary>
        Host,
    }

    /// <summary>
    /// 资源状态。刻意与底层资源库的枚举**分开**：
    /// 换资源库时业务层不用改（YooAsset 的枚举叫 EOperationStatus，取值也不同）。
    /// </summary>
    public enum AssetStatus
    {
        /// <summary>尚未开始。</summary>
        None,

        /// <summary>加载中。</summary>
        Loading,

        /// <summary>加载成功。</summary>
        Succeeded,

        /// <summary>加载失败。详情见句柄的 <c>Error</c>。</summary>
        Failed,
    }

    /// <summary>
    /// 资源层的观测快照，给性能 / 调试面板用（FW-M12）。
    /// 排查"资源泄漏"时看的就是这里：<c>UnusedCount</c> 一直不降就是有人没 Dispose。
    /// </summary>
    public readonly struct AssetDebugInfo
    {
        /// <summary>缓存条目总数（每个 location + 类型算一条）。</summary>
        public readonly int CachedCount;

        /// <summary>引用计数大于 0 的条目数（正被使用者持有）。</summary>
        public readonly int ActiveCount;

        /// <summary>引用计数为 0、等待 <c>ReleaseUnused</c> 回收的条目数。</summary>
        public readonly int UnusedCount;

        /// <summary>仍在加载中的条目数。</summary>
        public readonly int LoadingCount;

        /// <summary>加载失败的条目数。</summary>
        public readonly int FailedCount;

        /// <summary>所有条目的引用计数之和。</summary>
        public readonly int TotalRefCount;

        /// <summary>构造快照。</summary>
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

        /// <summary>一行摘要，面板可直接打印。</summary>
        public override string ToString()
        {
            return "条目=" + CachedCount + "（活跃=" + ActiveCount + " 待回收=" + UnusedCount +
                   " 加载中=" + LoadingCount + " 失败=" + FailedCount + "）引用计数合计=" + TotalRefCount;
        }
    }

    /// <summary>
    /// 资源加载结果的**只读视图**，供统计面板与批量释放使用（FW-M12）。
    /// 业务层通常不直接用这个接口，而是用泛型句柄 <see cref="AssetHandle{T}"/>。
    /// </summary>
    public interface IAssetHandle : IDisposable
    {
        /// <summary>资源地址。</summary>
        string Location { get; }

        /// <summary>当前状态。</summary>
        AssetStatus Status { get; }

        /// <summary>是否已完成（无论成功还是失败）。</summary>
        bool IsDone { get; }

        /// <summary>加载进度 0..1。</summary>
        float Progress { get; }

        /// <summary>失败原因；未失败时为空串。</summary>
        string Error { get; }

        /// <summary>类型擦除后的资源引用（无泛型视角）。</summary>
        UnityEngine.Object AssetObject { get; }

        /// <summary>当前引用计数。</summary>
        int RefCount { get; }

        /// <summary>阻塞直到加载完成。**只给工具 / 编辑器用**（Player 里会卡主线程）。</summary>
        void WaitForAsyncComplete();
    }

    /// <summary>
    /// 底层加载器：由适配层实现（例如 YooAsset）。框架只认这个接口。
    /// </summary>
    public interface IAssetProvider
    {
        /// <summary>是否已初始化完成。</summary>
        bool IsInitialized { get; }

        /// <summary>初始化（建包、加载清单等）。</summary>
        /// <param name="mode">运行模式。</param>
        System.Threading.Tasks.Task InitializeAsync(AssetRuntimeMode mode);

        /// <summary>异步加载一个资源。</summary>
        /// <param name="location">资源地址。</param>
        /// <param name="assetType">期望类型。</param>
        IAssetLoadOperation LoadAsync(string location, Type assetType);

        /// <summary>同步加载一个资源。**受限**：Player 里会阻塞主线程。</summary>
        /// <param name="location">资源地址。</param>
        /// <param name="assetType">期望类型。</param>
        IAssetLoadOperation LoadSync(string location, Type assetType);

        /// <summary>
        /// 异步加载一个场景（需求 **YOO-08**：场景也走资源层）。
        /// </summary>
        /// <param name="location">场景地址。</param>
        /// <param name="mode">加载模式（单场景 / 叠加）。</param>
        ISceneLoadOperation LoadSceneAsync(string location, UnityEngine.SceneManagement.LoadSceneMode mode);

        /// <summary>
        /// 卸载一个已加载的场景。
        /// 由 <see cref="ISceneHandle.Dispose"/> 触发，业务不直接调。
        /// </summary>
        /// <param name="operation">当初加载它时的那个操作。</param>
        void UnloadScene(ISceneLoadOperation operation);

        /// <summary>卸载底层已经没有人用的资源。</summary>
        void UnloadUnused();

        /// <summary>卸载全部由本加载器管理的资源（切场景收尾用）。</summary>
        void UnloadAll();
    }

    /// <summary>
    /// 一次底层加载。适配层负责把 YooAsset 的句柄翻译成这个形状。
    /// </summary>
    public interface IAssetLoadOperation : IDisposable
    {
        /// <summary>资源地址。</summary>
        string Location { get; }

        /// <summary>当前状态。</summary>
        AssetStatus Status { get; }

        /// <summary>加载进度 0..1。</summary>
        float Progress { get; }

        /// <summary>失败原因；未失败时为空串。</summary>
        string Error { get; }

        /// <summary>加载到的**原始资源**（不是实例）。</summary>
        UnityEngine.Object AssetObject { get; }

        /// <summary>是否已完成。</summary>
        bool IsDone { get; }

        /// <summary>完成通知。</summary>
        event Action<IAssetLoadOperation> OnComplete;

        /// <summary>阻塞直到完成（仅工具 / 编辑器）。</summary>
        void WaitForAsyncComplete();
    }
}
