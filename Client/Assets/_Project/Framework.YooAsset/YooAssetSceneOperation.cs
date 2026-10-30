// 这个文件里有：
//   YooAssetSceneOperation（类）：把 YooAsset v3 的场景句柄包成框架的 ISceneLoadOperation，翻译状态、转发进度与场景，并管住句柄释放与场景卸载。

using System;
using NBC.Framework.Asset;
using UnityEngine;
using YooAsset;

namespace NBC.Framework.Asset.Adapter
{
    // 主要功能：把 YooAsset v3 的 SceneHandle 包成框架的 ISceneLoadOperation，状态翻成框架枚举，并把「释放句柄」与「卸载场景」分成两步。
    // 主要数据成员：m_handle（底层 YooAsset 场景句柄）、m_disposed（是否已释放，true / false）、OnComplete（完成通知，无订阅时为 null）。
    // 主要外部方法：Activate / WaitForAsyncComplete / Dispose / UnloadAndDispose，以及只读的 Location / Status / Progress / Error / Scene / IsDone。
    // 谁在用它：YooAssetProvider.LoadSceneAsync 造出它并当 ISceneLoadOperation 交出去；卸载由 YooAssetProvider.UnloadScene 调 UnloadAndDispose（框架侧 SceneHandle.Dispose 触发）。
    // 注意：本类是适配层内部实现，只由 YooAssetProvider 使用；卸载时机由框架侧 SceneHandle.Dispose 决定，业务不直接触发。
    internal sealed class YooAssetSceneOperation : ISceneLoadOperation
    {
        // 底层 YooAsset 场景句柄；全限定是为与框架侧同名类型 SceneHandle 区分。构造后不为 null，Dispose 之后不再使用
        private readonly YooAsset.SceneHandle m_handle;

        // 是否已释放句柄；取值 true / false，为 true 后 Dispose 与 UnloadAndDispose 都直接返回，用来挡住重复释放
        private bool m_disposed;

        // 功能：把场景地址与句柄存下来，并把本对象的完成回调挂到句柄上。
        // 入参：location 场景地址（YooAsset 里配的 Address）；handle YooAsset 返回的场景句柄（不能为 null）
        // 返回：无
        // 步骤：1) 先落 location 与句柄；2) 再把 HandleCompleted 挂到句柄的 Completed 上，加载一完成就由它转成 OnComplete
        // 上游：YooAssetProvider.LoadSceneAsync
        // 下游：无
        public YooAssetSceneOperation(string location, YooAsset.SceneHandle handle)
        {
            Location = location;
            m_handle = handle;
            m_handle.Completed += HandleCompleted;
        }

        // 完成通知；底层句柄完成时触发一次，没有订阅者为 null
        public event Action<ISceneLoadOperation> OnComplete;

        // 场景地址（YooAsset 里配的 Address）；构造时给定，之后不再变
        public string Location { get; private set; }

        // 功能：每次读都把 YooAsset 的操作状态翻成框架的 AssetStatus，业务层因此看不到 EOperationStatus。
        public AssetStatus Status
        {
            get { return YooAssetLoadOperation.MapStatus(m_handle.Status); }
        }

        // 功能：透传底层句柄的加载进度，取值 0..1。
        public float Progress
        {
            get { return m_handle.Progress; }
        }

        // 功能：给出失败原因，未失败时是空串；底层给 null 时也返回空串，调用方不必判空。
        public string Error
        {
            get
            {
                string e = m_handle.Error;
                return e ?? string.Empty;
            }
        }

        // 功能：给出加载到的场景；未完成时是无效场景（IsValid 为 false），用之前先看 IsDone。
        public UnityEngine.SceneManagement.Scene Scene
        {
            get { return m_handle.SceneObject; }
        }

        // 功能：回答加载是否已经结束，成功与失败都算结束。
        public bool IsDone
        {
            get { return m_handle.IsDone; }
        }

        // 功能：请求底层激活已加载好的场景，供「先加载、后切场景」的流程用。
        // 入参：无
        // 返回：true 表示激活请求被接受；false 表示没被接受（请求是否落地由底层决定，这里不重试）
        // 步骤：1) 直接把请求转给底层句柄，本层不缓存「已激活」状态
        // 上游：框架侧 SceneHandle.Activate（业务最终经 ISceneHandle.Activate 调）
        // 下游：底层 SceneHandle.ActivateScene
        // 注意：只有加载时指定了不自动激活才需要调；否则 YooAsset 自己会激活，再调一次没有意义。
        public bool Activate()
        {
            return m_handle.ActivateScene();
        }

        // 功能：本应阻塞到场景加载完成，但 YooAsset v3 有意封了这条路，这里只能打一条警告后立即返回。
        // 入参：无
        // 返回：无（返回不代表加载已经结束）
        // 步骤：1) 直接打警告说明本次等待未生效，并提示改用 await 或轮询 IsDone
        // 上游：框架侧 SceneHandle.WaitForAsyncComplete（编辑器与工具用）
        // 下游：无
        // 注意：YooAsset 的 SceneHandle.WaitForAsyncComplete() 是 internal（源码注释写明场景加载不支持异步转同步），调它编译不过；这里选择警告而不是假装成功，静默降级会让人以为场景已经就绪。
        public void WaitForAsyncComplete()
        {
            Debug.LogWarning(
                "[YooAssetSceneOperation] 场景加载不支持『异步转同步』（YooAsset 有意把该方法设为 internal）。" +
                "本次 WaitForAsyncComplete() 未生效，请改用 await / 轮询 IsDone。");
        }

        // 功能：只释放底层 YooAsset 句柄并退订完成回调，不卸载场景；重复调用没有副作用。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已释放过就直接返回，保证幂等
        //   2) 先置 m_disposed 再退订与释放，释放途中再进来都走第 1 步
        //   3) 退订 Completed 后调句柄的 Dispose —— 它继承自 HandleBase，内部就等于 Release
        // 上游：UnloadAndDispose（本类）；框架侧正常路径不直接调它，卸载一律走 IAssetProvider.UnloadScene
        // 下游：底层 SceneHandle.Dispose
        // 注意：这里只释放句柄，场景还在内存里；要真卸载必须走 UnloadAndDispose。
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

        // 功能：先让 YooAsset 异步卸载场景，再释放句柄，两件事一次做完。
        // 入参：无
        // 返回：YooAsset 的异步卸载操作；已经释放过时返回 null，调用方据此跳过挂回调
        // 步骤：
        //   1) 已释放过就直接返回 null，避免重复卸载
        //   2) 先发起 UnloadSceneAsync，此刻句柄还有效
        //   3) 再 Dispose 释放句柄 —— 顺序反了就再也拿不到卸载操作，场景会一直留着
        // 上游：YooAssetProvider.UnloadScene（框架侧 SceneHandle.Dispose 触发）
        // 下游：底层 SceneHandle.UnloadSceneAsync、Dispose
        // 注意：Dispose 与 UnloadSceneAsync 是两件事，只做前者不卸载场景；这里两件都做，且卸载必须在前。
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

        // 功能：把底层句柄的完成回调转成框架侧的 OnComplete 通知。
        // 入参：handle 完成通知带过来的底层场景句柄；本方法不用它，场景信息一律走 m_handle
        // 返回：无
        // 步骤：1) 先把 OnComplete 拷到局部变量再判空调用 —— 事件可能在通知过程中被退订
        // 上游：底层 SceneHandle.Completed（构造函数里挂上）
        // 下游：OnComplete 的订阅方
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
