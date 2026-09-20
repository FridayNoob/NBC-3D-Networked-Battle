// ============================================================================
//  NBC.Framework.Asset.Adapter · 把 YooAsset v3 的句柄翻译成框架的加载操作
//  对应需求：FW-M17（把 YooAsset 的异步句柄适配成框架接口）
//  程序集：NBC.Framework.YooAsset（**只有这里**允许出现 YooAsset 类型）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 写这个文件前必须知道的两件事（都实测过，别凭记忆）
//  ---------------------------------------------------------------------------
//  ① **装的是 YooAsset 3.0.5，是重构后的 v3 API。**
//     v2.3 那套（`InitializeAsync` / `EditorSimulateModeParameters` / `RawFileHandle`）
//     被 `#if YOOASSET_LEGACY_API` 包着，而**这个宏包里没定义、工程里也没定义**
//     → 那些文件不参与编译，写了就是"找不到类型"。
//     完整对照表见 `Docs\05-依赖清单.md` 的「一·补」。
//
//  ② **`AssetHandle.AssetObject` 是"原始资源"，不是实例。**
//     证据：`AssetProvider.cs:42` 直接赋值加载产物；`AssetHandle.cs:56` 只做转发；
//     全 Runtime 目录 `Object.Instantiate` **只**出现在 `InstantiateOperation.cs`；
//     而 v3 把实例化做成了**显式独立步骤**（`InstantiateSync()` / `InstantiateAsync()`）。
//
//  ---------------------------------------------------------------------------
//  ③ 这个适配层【不】自动实例化 GameObject —— 一处刻意不照搬原框架的地方
//  ---------------------------------------------------------------------------
//  原框架 `ResMgr` 有一条**隐式契约**：`Load<GameObject>` 返回的是**实例化后的对象**。
//  审计当时提醒说"适配层必须复刻这条契约，否则 6 个调用点行为全错"（`Docs\06` §6.2）。
//
//  但那条提醒基于一个前提：**调用点已经存在、不能改**。而实际上——
//  那 6 个调用点全在**原框架自己**的 `PoolMgr` / `UIManager` / `MusicMgr` 里，
//  而这些模块我们**正在重写**（A2 已完成、A8/A9 待做）。所以没有兼容包袱。
//
//  于是这里选择**返回原始资源**，把"实例化"变成调用方的显式动作。理由：
//    ① 语义清晰："加载资源"和"创建实例"是两件事，原版把它们混在一起正是麻烦的来源；
//    ② **能和 A2 的池自然组合**：`new GameObjectPool(name, () => Instantiate(prefab), ...)`
//       —— 池本来就通过"工厂"拿对象，正好对上；
//    ③ 避免"资源引用计数"与"实例生命周期"两套东西纠缠在一起
//       （实例谁销毁？和 asset 的引用计数什么关系？原版根本没回答）。
//
//  ⚠️ 这条是对原版行为的**有意改变**，已记录在 `Docs\06` §十四。A8/A9 写调用方时必须按新语义来。
// ============================================================================

using System;
using NBC.Framework.Asset;
using YooAsset;

namespace NBC.Framework.Asset.Adapter
{
    /// <summary>
    /// 把 YooAsset 的资源句柄包装成框架的 <see cref="IAssetLoadOperation"/>。
    /// </summary>
    internal sealed class YooAssetLoadOperation : IAssetLoadOperation
    {
        /// <summary>底层 YooAsset 句柄。</summary>
        private readonly AssetHandle m_handle;

        private bool m_disposed;

        /// <summary>构造包装。</summary>
        /// <param name="location">资源地址（YooAsset 的 location）。</param>
        /// <param name="handle">YooAsset 返回的句柄。</param>
        public YooAssetLoadOperation(string location, AssetHandle handle)
        {
            Location = location;
            m_handle = handle;
            m_handle.Completed += HandleCompleted;
        }

        /// <summary>完成通知。</summary>
        public event Action<IAssetLoadOperation> OnComplete;

        /// <summary>资源地址。</summary>
        public string Location { get; private set; }

        /// <summary>状态（已把 YooAsset 的枚举翻译成框架自己的枚举）。</summary>
        public AssetStatus Status
        {
            get { return MapStatus(m_handle.Status); }
        }

        /// <summary>进度 0..1。</summary>
        public float Progress
        {
            get { return m_handle.Progress; }
        }

        /// <summary>失败原因。</summary>
        public string Error
        {
            get
            {
                string e = m_handle.Error;
                return e ?? string.Empty;
            }
        }

        /// <summary>加载到的**原始资源**（不是实例，见文件头第 ③ 条）。</summary>
        public UnityEngine.Object AssetObject
        {
            get { return m_handle.AssetObject; }
        }

        /// <summary>是否已完成。</summary>
        public bool IsDone
        {
            get { return m_handle.IsDone; }
        }

        /// <summary>阻塞直到完成（仅编辑器 / 工具）。</summary>
        public void WaitForAsyncComplete()
        {
            m_handle.WaitForAsyncComplete();
        }

        /// <summary>
        /// 释放底层句柄。
        /// <para>
        /// ⚠️ 只调 `Dispose()`，**不**再调 `Release()` ——
        /// `HandleBase.cs:42` 里 `Dispose()` 的实现就是 `this.Release()`，两个都调等于释放两次。
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;
            m_handle.Completed -= HandleCompleted;
            m_handle.Dispose();
        }

        /// <summary>
        /// YooAsset 的状态枚举 → 框架的状态枚举。
        /// 两个枚举**刻意不共用**：业务层不该看见 `EOperationStatus`（YOO-09）。
        /// </summary>
        internal static AssetStatus MapStatus(EOperationStatus status)
        {
            switch (status)
            {
                case EOperationStatus.None:
                    return AssetStatus.None;
                case EOperationStatus.Processing:
                    return AssetStatus.Loading;
                case EOperationStatus.Succeeded:
                    return AssetStatus.Succeeded;
                case EOperationStatus.Failed:
                    return AssetStatus.Failed;
                default:
                    return AssetStatus.None;
            }
        }

        private void HandleCompleted(AssetHandle handle)
        {
            Action<IAssetLoadOperation> handlers = OnComplete;
            if (handlers != null)
            {
                handlers(this);
            }
        }
    }
}
