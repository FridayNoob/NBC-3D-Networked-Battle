// ============================================================================
//  NBC.Framework.Asset · 资源句柄
//  对应需求：FW-M05（引用计数、句柄管理）、ADR-001 决策 1 / 2
//
//  ---------------------------------------------------------------------------
//  为什么"必须"有句柄，而不是直接返回资源对象
//  ---------------------------------------------------------------------------
//  原框架 `ResMgr` 的致命缺陷之一就是：**加载了却没有任何释放入口**
//  （没有引用计数、没有 Unload）。项目越大，内存只涨不落。
//  句柄就是那个"释放入口"：`Dispose()` 表示"我用完了"，引用计数减一。
//
//  ---------------------------------------------------------------------------
//  同一个对象，三种消费方式（ADR-001 决策 2）
//  ---------------------------------------------------------------------------
//      // A 回调
//      var h = AssetManager.Instance.LoadAssetAsync<GameObject>("ui_login");
//      h.OnComplete += x => { if (x.IsValid) Instantiate(x.Asset); };
//
//      // B 轮询
//      while (!h.IsDone) { bar.value = h.Progress; yield return null; }
//
//      // C await
//      var h2 = AssetManager.Instance.LoadAssetAsync<GameObject>("ui_login");
//      await h2;
//
//  三种用的是**同一个句柄对象**，不是三套 API。
//  其中 `await` 走的是自定义 awaitable（见文件末尾），**不产生 Task 分配** ——
//  ADR-001 的理由是：M2 的战斗流程会大量异步，`Task` 在 IL2CPP 上可用但有堆分配。
// ============================================================================

using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace NBC.Framework.Asset
{
    /// <summary>
    /// 泛型资源句柄：业务层拿这个。
    /// <para>
    /// 生命周期：拿到句柄即在引用计数上加一；<see cref="Dispose"/> 减一。
    /// 减到 0 之后资源**不会立刻卸载**，而是等 <c>AssetManager.ReleaseUnused()</c> ——
    /// 这样"刚放下又马上要用"的场景不会反复加载卸载。
    /// </para>
    /// </summary>
    /// <typeparam name="T">资源类型。</typeparam>
    public sealed class AssetHandle<T> : IAssetHandle where T : UnityEngine.Object
    {
        private readonly AssetManager m_owner;
        private readonly AssetManager.Entry m_entry;

        /// <summary>业务注册的完成回调。</summary>
        private event Action<AssetHandle<T>> m_onComplete;

        /// <summary>await 的续体。</summary>
        private Action m_continuation;

        private bool m_disposed;

        /// <summary>构造句柄。仅供 AssetManager 调用。</summary>
        internal AssetHandle(AssetManager owner, AssetManager.Entry entry)
        {
            m_owner = owner;
            m_entry = entry;
            m_entry.Completed += OnEntryCompleted;

            // 极端情况：底层是同步完成的，句柄还没交出去就已经好了
            if (m_entry.IsDone)
            {
                OnEntryCompleted(m_entry);
            }
        }

        /// <summary>资源地址。</summary>
        public string Location
        {
            get { return m_entry.Location; }
        }

        /// <summary>当前状态。</summary>
        public AssetStatus Status
        {
            get { return m_entry.Status; }
        }

        /// <summary>是否已完成（成功或失败都算）。</summary>
        public bool IsDone
        {
            get { return m_entry.IsDone; }
        }

        /// <summary>加载进度 0..1。</summary>
        public float Progress
        {
            get { return m_entry.Progress; }
        }

        /// <summary>失败原因；未失败时为空串。</summary>
        public string Error
        {
            get { return m_entry.Error; }
        }

        /// <summary>类型擦除视图。</summary>
        public UnityEngine.Object AssetObject
        {
            get { return m_entry.AssetObject; }
        }

        /// <summary>当前引用计数（含本句柄这一份）。</summary>
        public int RefCount
        {
            get { return m_entry.RefCount; }
        }

        /// <summary>
        /// 加载到的资源。**注意：这是"原始资源"，不是实例。**
        /// GameObject 这类需要实例化的资源，请自己 <c>Instantiate</c>
        /// （或使用上层封装；<c>ResMgr</c> 当年是自动实例化的，见 Docs/06 §6.2）。
        /// 未完成或失败时为 null。
        /// </summary>
        public T Asset
        {
            get { return m_entry.AssetObject as T; }
        }

        /// <summary>是否拿到了可用资源。</summary>
        public bool IsValid
        {
            get { return m_entry.Status == AssetStatus.Succeeded && m_entry.AssetObject != null; }
        }

        /// <summary>
        /// 完成回调。
        /// <para>
        /// **已经完成时注册会立即被调用一次** —— 这样"加载太快、回调注册太晚"的竞态就不会漏掉通知
        /// （原框架的事件中心没有这个保证，见 Docs/06 §四 P-18 的讨论）。
        /// </para>
        /// </summary>
        public event Action<AssetHandle<T>> OnComplete
        {
            add
            {
                if (value == null)
                {
                    return;
                }

                if (IsDone)
                {
                    value(this);
                    return;
                }

                m_onComplete += value;
            }
            remove { m_onComplete -= value; }
        }

        /// <summary>阻塞直到完成。**只给工具 / 编辑器用**，Player 里会卡主线程。</summary>
        public void WaitForAsyncComplete()
        {
            m_entry.WaitForAsyncComplete();
        }

        /// <summary>
        /// 释放一次引用。**重复 Dispose 是安全的**（幂等），这是 ADR-001 约束 C6 的要求。
        /// </summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;                     // 幂等：重复释放不报错、也不重复减计数
            }

            m_disposed = true;
            m_entry.Completed -= OnEntryCompleted;
            m_onComplete = null;
            m_continuation = null;
            m_owner.Release(m_entry);
        }

        /// <summary>取 await 用的 awaiter。</summary>
        public AssetHandleAwaiter<T> GetAwaiter()
        {
            return new AssetHandleAwaiter<T>(this);
        }

        /// <summary>调试用文本。</summary>
        public override string ToString()
        {
            return typeof(T).Name + " @ " + Location + " [" + Status + " " +
                   (Progress * 100f).ToString("F0") + "%] refs=" + RefCount;
        }

        /// <summary>底层加载完成时：转发给业务回调与 await 续体。</summary>
        private void OnEntryCompleted(AssetManager.Entry entry)
        {
            Action<AssetHandle<T>> callbacks = m_onComplete;
            m_onComplete = null;
            if (callbacks != null)
            {
                callbacks(this);
            }

            Action continuation = m_continuation;
            m_continuation = null;
            if (continuation != null)
            {
                continuation();
            }
        }

        /// <summary>注册 await 的续体。仅供 awaiter 调用。</summary>
        internal void AddContinuation(Action continuation)
        {
            if (m_continuation == null)
            {
                m_continuation = continuation;
            }
            else
            {
                m_continuation += continuation;
            }
        }
    }

    /// <summary>
    /// 零分配的 awaiter：让 <c>await handle;</c> 可用。
    /// <para>
    /// 为什么不用 <c>Task</c>：`Task` 每次都会在堆上分配 + 走线程池调度，
    /// 而这里只是"等一个已经在跑的操作完成"，用自定义 awaitable 就够了。
    /// 这是 ADR-001 决策 2 的实现。
    /// </para>
    /// </summary>
    /// <typeparam name="T">资源类型。</typeparam>
    public readonly struct AssetHandleAwaiter<T> : INotifyCompletion where T : UnityEngine.Object
    {
        private readonly AssetHandle<T> m_handle;

        /// <summary>构造 awaiter。</summary>
        internal AssetHandleAwaiter(AssetHandle<T> handle)
        {
            m_handle = handle;
        }

        /// <summary>是否已经可以继续。</summary>
        public bool IsCompleted
        {
            get { return m_handle == null || m_handle.IsDone; }
        }

        /// <summary>注册续体。</summary>
        /// <param name="continuation">完成后要执行的动作。</param>
        public void OnCompleted(Action continuation)
        {
            if (m_handle == null)
            {
                continuation();
                return;
            }

            m_handle.AddContinuation(continuation);
        }

        /// <summary>取结果：把句柄本身交回去（业务需要读 Status / Asset）。</summary>
        public AssetHandle<T> GetResult()
        {
            return m_handle;
        }
    }
}
