// 这个文件里有：
//   AssetHandle<T>（类）：泛型资源句柄，业务层用它读加载状态与资源，用完 Dispose 把引用计数减一
//   AssetHandleAwaiter<T>（结构体）：让 await 句柄可用的零分配 awaiter

using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace NBC.Framework.Asset
{
    // 主要功能：一次资源加载的句柄；把共享条目的状态与资源转给业务层，Dispose 时把引用计数减一。
    // 主要数据成员：m_owner（所属管理器，释放时回减计数）、m_entry（共享缓存条目，状态与资源都在它上面）、m_disposed（是否已释放）、m_onComplete（业务完成回调）、m_continuation（await 续体）。
    // 主要外部方法：Asset / IsValid / Status / Progress / Error / RefCount（只读）、OnComplete（完成事件）、Dispose、GetAwaiter。
    // 谁在用它：由 AssetManager.LoadAssetAsync / LoadAsset 产出；Framework.UI 的 UIManager、Game 的 AssetConfigSource 等业务方持有，用完必须 Dispose；IAssetHandle 视角供统计面板与批量释放。
    /// <summary>泛型资源句柄：一次加载的只读视图，同时是这份引用的释放入口。</summary>
    /// <typeparam name="T">资源类型；与实际条目类型不符时 Asset 为 null。</typeparam>
    public sealed class AssetHandle<T> : IAssetHandle where T : UnityEngine.Object
    {
        private readonly AssetManager m_owner;             // 所属管理器，Dispose 时经它把条目引用计数减一；恒不为 null
        private readonly AssetManager.Entry m_entry;       // 共享缓存条目，一个（地址 + 类型）一条，多个句柄可指向同一条目；恒不为 null

        private event Action<AssetHandle<T>> m_onComplete; // 业务注册的完成回调；null 表示当前无人订阅，派发一次后也会置空

        private Action m_continuation;                     // await 续体；null 表示没有 await 在等，完成时取走并清空

        private bool m_disposed;                           // 本句柄是否已 Dispose；true 之后 Dispose 直接返回

        // 功能：把句柄挂到缓存条目上，并订阅它的完成通知。
        // 入参：owner 所属管理器，Dispose 时用来回减计数；entry 已计过一次引用的缓存条目。
        // 返回：无（构造函数）。
        // 步骤：
        //   1) 先记下 owner 与 entry，之后所有状态读取都转发给 entry
        //   2) 订阅 entry.Completed，否则加载完成时收不到通知
        //   3) 若条目此刻已完成就立刻补发一次 —— 底层同步完成时句柄还没交出去就已经好了，不补发这次通知就漏了
        // 上游：AssetManager.LoadAssetAsync / LoadAsset
        // 下游：OnEntryCompleted
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
        // 功能：原样转发条目上的地址，即加载时传入的 location。
        public string Location
        {
            get { return m_entry.Location; }
        }

        /// <summary>当前加载状态。</summary>
        // 功能：转发条目状态；底层操作已被释放时按 Failed 处理，不会停留在 Loading 的假象。
        public AssetStatus Status
        {
            get { return m_entry.Status; }
        }

        /// <summary>是否已完成，成功与失败都算完成。</summary>
        // 功能：转发条目完成标志；失败也算完成，所以不能拿它判断加载成功。
        public bool IsDone
        {
            get { return m_entry.IsDone; }
        }

        /// <summary>加载进度。</summary>
        // 功能：转发条目进度，取值 0..1；底层操作已被释放时恒为 0。
        public float Progress
        {
            get { return m_entry.Progress; }
        }

        /// <summary>失败原因。</summary>
        // 功能：转发条目失败原因；底层没给原因时是空串，不会是 null，所以判失败要看 Status。
        public string Error
        {
            get { return m_entry.Error; }
        }

        /// <summary>类型擦除后的原始资源引用。</summary>
        // 功能：不做类型转换，直接给出条目持有的资源；未完成或失败时为 null。
        public UnityEngine.Object AssetObject
        {
            get { return m_entry.AssetObject; }
        }

        /// <summary>条目当前的引用计数。</summary>
        // 功能：给出共享条目的总引用数（含本句柄这一份）；0 表示没人持有，等 ReleaseUnused 回收。
        public int RefCount
        {
            get { return m_entry.RefCount; }
        }

        /// <summary>加载到的原始资源，不是实例。</summary>
        // 功能：把条目资源按 T 转换后交出去；未完成、失败或类型不符时为 null。
        // 注意：这是原始资源，GameObject 这类要自己 Instantiate；它不会替你实例化。
        public T Asset
        {
            get { return m_entry.AssetObject as T; }
        }

        /// <summary>是否拿到了可用资源。</summary>
        // 功能：成功且资源非 null 才算可用，判断能不能用资源只看这一个就够。
        public bool IsValid
        {
            get { return m_entry.Status == AssetStatus.Succeeded && m_entry.AssetObject != null; }
        }

        /// <summary>完成回调；已完成时注册会立即回调一次。</summary>
        // 功能：注册加载完成回调，每个订阅最多收到一次通知。
        // 入参：add 订阅者，null 直接忽略；remove 取消订阅。
        // 返回：无。
        // 步骤：
        //   1) 已完成就用当前句柄立刻回调一次，避免加载太快、回调注册太晚而漏掉通知
        //   2) 未完成才挂到 m_onComplete，等 OnEntryCompleted 统一派发
        // 上游：业务层订阅
        // 下游：OnEntryCompleted
        // 注意：已完成时的回调在注册的调用栈里同步执行，回调里改状态要当心重入。
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

        /// <summary>阻塞直到加载完成。</summary>
        // 功能：转给条目的等待，返回时 IsDone 必为 true。
        // 注意：只给工具与编辑器用；Player 里会卡死主线程。
        public void WaitForAsyncComplete()
        {
            m_entry.WaitForAsyncComplete();
        }

        /// <summary>释放本句柄持有的这一次引用；重复调用安全。</summary>
        // 功能：把本句柄的引用计数减一，并断开回调与续体，句柄自此失效。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 已释放过就直接返回，保证重复释放不报错、也不重复减计数
        //   2) 先置 m_disposed 再动其它状态，防止重入时走同一条路径
        //   3) 退订条目完成通知并清空回调与续体，避免释放后还被通知
        //   4) 交给 AssetManager.Release 只减计数、不立刻卸载 —— 刚放下又要用时不必反复加载卸载
        // 上游：业务层，通常是 using 块或面板关闭时
        // 下游：AssetManager.Release
        // 注意：释放后句柄失效，回调与续体都已被清空；条目还要等 ReleaseUnused 才卸载，别在释放后继续读资源。
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;
            m_entry.Completed -= OnEntryCompleted;
            m_onComplete = null;
            m_continuation = null;
            m_owner.Release(m_entry);
        }

        /// <summary>取 await 用的 awaiter。</summary>
        // 功能：交给编译器生成的 await 状态机；回调、轮询、await 三种用法共用同一个句柄。
        public AssetHandleAwaiter<T> GetAwaiter()
        {
            return new AssetHandleAwaiter<T>(this);
        }

        /// <summary>调试用的一行文本。</summary>
        // 功能：拼出类型名、地址、状态、进度百分比与引用计数，便于直接打日志。
        // 返回：一行摘要，进度取整到百分比。
        public override string ToString()
        {
            return typeof(T).Name + " @ " + Location + " [" + Status + " " +
                   (Progress * 100f).ToString("F0") + "%] refs=" + RefCount;
        }

        // 功能：把完成通知派发给业务回调与 await 续体，每份最多一次。
        // 入参：entry 刚完成的条目，这里用不到，通知只与句柄自身有关（同步完成时也会传进来）。
        // 返回：无。
        // 步骤：
        //   1) 先取走回调并置空，保证只派发一次；回调里再订阅会走已完成分支而不是排队
        //   2) 有回调就同步调用一次 —— 失败也走这里，所以回调里必须先查 IsValid
        //   3) 续体同样先取走再调用，避免续体内部 Dispose 造成重入
        // 上游：构造时订阅的条目 Completed 事件
        // 下游：业务回调、awaiter 续体
        // 注意：失败也是 IsDone，回调收到时资源可能是 null，不能假设已经加载成功。
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

        // 功能：登记一个 await 续体，加载完成时执行。
        // 入参：continuation 完成时要跑的动作，由编译器生成，不会为 null。
        // 返回：无。
        // 步骤：
        //   1) 已有续体就追加成多播，多个 awaiter 等同一个句柄时都能被唤醒
        //   2) 没有就直接存
        // 上游：AssetHandleAwaiter.OnCompleted
        // 下游：OnEntryCompleted
        // 注意：这里不会因为已完成就立刻执行；已完成时的短路在 awaiter 的 IsCompleted，不在这一段。
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

    // 主要功能：把句柄包成可 await 的 awaitable，完成时直接执行续体，不分配 Task。
    // 主要数据成员：m_handle（被等待的句柄，null 视为已完成）。
    // 主要外部方法：IsCompleted / OnCompleted / GetResult。
    // 谁在用它：编译器生成的 await 状态机（await 句柄），以及需要手动取 awaiter 的工具与测试。
    /// <summary>零分配的 awaiter：让 await 句柄可用，不走 Task 的堆分配与线程池调度。</summary>
    /// <typeparam name="T">资源类型。</typeparam>
    public readonly struct AssetHandleAwaiter<T> : INotifyCompletion where T : UnityEngine.Object
    {
        private readonly AssetHandle<T> m_handle;          // 被等待的句柄；null 视为已完成，await 不会挂起

        // 功能：记住要等待的句柄。
        internal AssetHandleAwaiter(AssetHandle<T> handle)
        {
            m_handle = handle;
        }

        /// <summary>是否已经可以直接继续。</summary>
        // 功能：句柄为 null 或已完成都为 true，编译器据此决定是否先挂起。
        public bool IsCompleted
        {
            get { return m_handle == null || m_handle.IsDone; }
        }

        /// <summary>注册续体，加载完成后执行。</summary>
        /// <param name="continuation">完成后要执行的动作。</param>
        // 功能：把编译器生成的续体交给句柄，加载完成时唤醒一次。
        // 入参：continuation 完成后要跑的动作，不会为 null。
        // 返回：无。
        // 步骤：
        //   1) 句柄为 null 时直接执行，避免空等待
        //   2) 否则交给句柄统一登记，多个 awaiter 等同一个句柄时会追加成多播
        // 上游：编译器生成的 await 状态机
        // 下游：AssetHandle.AddContinuation
        // 注意：句柄已完成却仍走到这里时续体不会执行 —— 正常路径靠 IsCompleted 先短路，手动调用要自己先判。
        public void OnCompleted(Action continuation)
        {
            if (m_handle == null)
            {
                continuation();
                return;
            }

            m_handle.AddContinuation(continuation);
        }

        /// <summary>取 await 的结果。</summary>
        /// <returns>句柄本身，业务再自己读 Status / Asset。</returns>
        // 功能：把句柄交回去，await 的返回值就是它。
        // 注意：失败不会抛异常，await 之后必须自己查 IsValid。
        public AssetHandle<T> GetResult()
        {
            return m_handle;
        }
    }
}
