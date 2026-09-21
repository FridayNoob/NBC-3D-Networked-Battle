// ============================================================================
//  NBC.Framework.Asset · 场景加载的接缝类型
//  对应需求：YOO-08（场景也走资源层）、FW-M06（场景管理）、ADR-001 里预留的 LoadSceneAsync
//
//  ---------------------------------------------------------------------------
//  为什么场景要单独一套类型，不直接复用 AssetHandle<T>
//  ---------------------------------------------------------------------------
//  场景和普通资源有几处本质差别：
//    ① 它**没有"实例"的概念** —— 加载出来的是 `Scene`（一个已存在的场景），不是可以复制的对象
//    ② 它有**激活时机**：可以先加载好、再决定什么时候切过去（`allowSceneActivation = false`），
//       这是做"加载完成后再黑屏切换"的关键
//    ③ 它**必须显式卸载**（`UnloadSceneAsync`），不会因为引用计数归零就消失
//  硬塞进 `AssetHandle<T>` 会得到一个到处都是"这个字段对场景没意义"的类。
//
//  ---------------------------------------------------------------------------
//  与资源侧一致的两条边界
//  ---------------------------------------------------------------------------
//    · 框架只认 `ISceneLoadOperation`（适配层实现），不认识 YooAsset
//    · 业务只认 `ISceneHandle` + `SceneLoader`，同样不认识 YooAsset
// ============================================================================

using System;
using UnityEngine.SceneManagement;

namespace NBC.Framework.Asset
{
    /// <summary>
    /// 场景句柄：业务层拿这个。
    /// <para>
    /// ⚠️ **`Dispose()` 会卸载场景**（与资源句柄不同 —— 资源句柄只管引用计数）。
    /// 因为场景没有引用计数语义：你说"不要了"，它就是该被卸掉。
    /// </para>
    /// </summary>
    public interface ISceneHandle : IDisposable
    {
        /// <summary>场景地址（YooAsset 里配的 Address）。</summary>
        string Location { get; }

        /// <summary>当前状态。</summary>
        AssetStatus Status { get; }

        /// <summary>是否已完成（成功或失败）。</summary>
        bool IsDone { get; }

        /// <summary>加载进度 0..1。</summary>
        float Progress { get; }

        /// <summary>失败原因；未失败时为空串。</summary>
        string Error { get; }

        /// <summary>加载到的场景。未完成时是无效场景（`IsValid()` 为 false）。</summary>
        Scene Scene { get; }

        /// <summary>
        /// 手动激活场景。
        /// 只在"加载时指定了不自动激活"的情况下需要调用；否则激活是自动的。
        /// </summary>
        /// <returns>请求是否被接受。</returns>
        bool Activate();

        /// <summary>阻塞直到完成（仅编辑器 / 工具）。</summary>
        void WaitForAsyncComplete();
    }

    /// <summary>
    /// 一次底层场景加载。适配层负责把 YooAsset 的 `SceneHandle` 翻译成这个形状。
    /// </summary>
    public interface ISceneLoadOperation : IDisposable
    {
        /// <summary>场景地址。</summary>
        string Location { get; }

        /// <summary>当前状态。</summary>
        AssetStatus Status { get; }

        /// <summary>加载进度 0..1。</summary>
        float Progress { get; }

        /// <summary>失败原因。</summary>
        string Error { get; }

        /// <summary>加载到的场景。</summary>
        Scene Scene { get; }

        /// <summary>是否已完成。</summary>
        bool IsDone { get; }

        /// <summary>完成通知。</summary>
        event Action<ISceneLoadOperation> OnComplete;

        /// <summary>手动激活。</summary>
        /// <returns>请求是否被接受。</returns>
        bool Activate();

        /// <summary>阻塞直到完成（仅编辑器 / 工具）。</summary>
        void WaitForAsyncComplete();
    }

    /// <summary>场景加载的默认实现：把底层操作包一层，并让 `Dispose` 触发卸载。</summary>
    internal sealed class SceneHandle : ISceneHandle
    {
        private readonly IAssetProvider m_owner;
        private ISceneLoadOperation m_operation;

        /// <summary>构造句柄。</summary>
        /// <param name="owner">所属加载器（用于回传卸载请求）。</param>
        /// <param name="operation">底层加载操作。</param>
        public SceneHandle(IAssetProvider owner, ISceneLoadOperation operation)
        {
            m_owner = owner;
            m_operation = operation;
        }

        /// <summary>底层操作（供加载器内部使用）。</summary>
        public ISceneLoadOperation Operation
        {
            get { return m_operation; }
        }

        /// <summary>场景地址。</summary>
        public string Location
        {
            get { return m_operation == null ? string.Empty : m_operation.Location; }
        }

        /// <summary>当前状态。句柄被释放后视为失败，避免拿到"看起来还在加载"的假象。</summary>
        public AssetStatus Status
        {
            get { return m_operation == null ? AssetStatus.Failed : m_operation.Status; }
        }

        /// <summary>是否已完成。</summary>
        public bool IsDone
        {
            get { return m_operation == null || m_operation.IsDone; }
        }

        /// <summary>进度。</summary>
        public float Progress
        {
            get { return m_operation == null ? 0f : m_operation.Progress; }
        }

        /// <summary>失败原因。</summary>
        public string Error
        {
            get
            {
                if (m_operation == null)
                {
                    return "场景句柄已被释放";
                }

                string e = m_operation.Error;
                return e ?? string.Empty;
            }
        }

        /// <summary>场景。</summary>
        public Scene Scene
        {
            get { return m_operation == null ? default(Scene) : m_operation.Scene; }
        }

        /// <summary>手动激活。</summary>
        public bool Activate()
        {
            return m_operation != null && m_operation.Activate();
        }

        /// <summary>阻塞直到完成。</summary>
        public void WaitForAsyncComplete()
        {
            if (m_operation != null)
            {
                m_operation.WaitForAsyncComplete();
            }
        }

        /// <summary>
        /// 卸载场景。**幂等**：重复调用不会重复卸载。
        /// </summary>
        public void Dispose()
        {
            if (m_operation == null)
            {
                return;
            }

            ISceneLoadOperation operation = m_operation;
            m_operation = null;
            m_owner.UnloadScene(operation);
        }
    }
}
