// ============================================================================
//  NBC.Framework.Asset.Adapter · 把 YooAsset v3 的 SceneHandle 翻译成框架的 ISceneLoadOperation
//  对应需求：YOO-08（场景也走资源层）、FW-M06（场景管理）
//
//  ---------------------------------------------------------------------------
//  两处 v3 特有的细节（都读源码确认过，不是猜的）
//  ---------------------------------------------------------------------------
//  ① **`SceneHandle.WaitForAsyncComplete()` 是 `internal`**（`SceneHandle.cs:51`），
//     注释写明："场景加载不支持异步转同步，因此此方法有意设为 internal"。
//     所以本类的 `WaitForAsyncComplete()` **做不到真的等待** —— 这里选择
//     **打一条警告并返回**，而不是假装成功（静默降级是本项目最忌讳的）。
//
//  ② **`Dispose()` 与 `UnloadSceneAsync()` 是两件事**：
//     · `Dispose()` 只释放句柄（继承自 `HandleBase`，即 `Release()`）
//     · `UnloadSceneAsync()` 才真的卸载场景（`SceneHandle.cs:132`）
//     包装类的 `Dispose()` 只做前者；卸载由 `UnloadAndDispose()` 负责，
//     它由框架侧 `SceneHandle.Dispose()` → `IAssetProvider.UnloadScene(...)` 触发。
// ============================================================================

using System;
using NBC.Framework.Asset;
using UnityEngine;
using YooAsset;

namespace NBC.Framework.Asset.Adapter
{
    /// <summary>
    /// 把 YooAsset 的场景句柄包装成框架的 <see cref="ISceneLoadOperation"/>。
    /// </summary>
    internal sealed class YooAssetSceneOperation : ISceneLoadOperation
    {
        /// <summary>底层 YooAsset 场景句柄。此处全限定，避免与框架侧的 `SceneHandle` 混淆。</summary>
        private readonly YooAsset.SceneHandle m_handle;

        private bool m_disposed;

        /// <summary>构造包装。</summary>
        /// <param name="location">场景地址。</param>
        /// <param name="handle">YooAsset 返回的场景句柄。</param>
        public YooAssetSceneOperation(string location, YooAsset.SceneHandle handle)
        {
            Location = location;
            m_handle = handle;
            m_handle.Completed += HandleCompleted;
        }

        /// <summary>完成通知。</summary>
        public event Action<ISceneLoadOperation> OnComplete;

        /// <summary>场景地址。</summary>
        public string Location { get; private set; }

        /// <summary>状态（翻译成框架自己的枚举）。</summary>
        public AssetStatus Status
        {
            get { return YooAssetLoadOperation.MapStatus(m_handle.Status); }
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

        /// <summary>加载到的场景。</summary>
        public UnityEngine.SceneManagement.Scene Scene
        {
            get { return m_handle.SceneObject; }
        }

        /// <summary>是否已完成。</summary>
        public bool IsDone
        {
            get { return m_handle.IsDone; }
        }

        /// <summary>手动激活场景。</summary>
        public bool Activate()
        {
            return m_handle.ActivateScene();
        }

        /// <summary>
        /// ⚠️ **做不到**。YooAsset 有意把 `SceneHandle.WaitForAsyncComplete()` 设为 `internal`
        /// （`SceneHandle.cs:49-51`：场景加载不支持异步转同步）。
        /// 这里打一条警告并返回，**不假装成功**。
        /// </summary>
        public void WaitForAsyncComplete()
        {
            Debug.LogWarning(
                "[YooAssetSceneOperation] 场景加载不支持『异步转同步』（YooAsset 有意把该方法设为 internal）。" +
                "本次 WaitForAsyncComplete() 未生效，请改用 await / 轮询 IsDone。");
        }

        /// <summary>只释放句柄，**不卸载场景**。</summary>
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
        /// 卸载场景并释放句柄。由 `IAssetProvider.UnloadScene(...)` 调用。
        /// </summary>
        /// <returns>卸载操作（异步）。</returns>
        public UnloadSceneOperation UnloadAndDispose()
        {
            if (m_disposed)
            {
                return null;
            }

            UnloadSceneOperation unloadOperation = m_handle.UnloadSceneAsync();
            Dispose();
            return unloadOperation;
        }

        private void HandleCompleted(YooAsset.SceneHandle handle)
        {
            Action<ISceneLoadOperation> handlers = OnComplete;
            if (handlers != null)
            {
                handlers(this);
            }
        }
    }
}
