// 这个文件里有：
//   ISceneHandle（接口）：业务层持有的场景句柄，摊平状态、进度、场景与失败原因，Dispose 即卸载场景。
//   ISceneLoadOperation（接口）：适配层实现的一次底层场景加载，暴露状态、进度、场景与完成通知，并支持手动激活。
//   SceneHandle（类）：框架侧默认的句柄实现，包住底层操作，Dispose 时经加载器回传卸载请求。
//
// 注意：场景与普通资源有三处本质差别，所以单独一套类型 —— 场景没有「实例」概念（加载出来的是已存在的 Scene），
// 有激活时机（可以先加载好、再决定什么时候切过去），且必须显式卸载，不会因为引用计数归零就消失。

using System;
using UnityEngine.SceneManagement;

namespace NBC.Framework.Asset
{
    // 主要功能：业务层持有的场景句柄，把一次场景加载的状态、进度、场景与失败原因摊成只读契约，Dispose 时卸载场景。
    // 主要数据成员：Location（场景地址）、Status（AssetStatus）、Progress（0..1）、Error（失败原因，未失败为空串）、Scene（加载到的场景）。
    // 主要外部方法：Activate（手动激活）、WaitForAsyncComplete（编辑器 / 工具的阻塞等待），以及继承自 IDisposable 的 Dispose。
    // 谁在用它：AssetManager.LoadSceneAsync 造出实现并返回；SceneLoader.Load 拿到后推进进度、判超时与取消。
    /// <summary>
    /// 场景句柄：业务层拿这个。
    /// 注意：Dispose 会卸载场景（资源句柄只减引用计数），因为场景没有引用计数语义 —— 你说不要了，它就该被卸掉。
    /// </summary>
    public interface ISceneHandle : IDisposable
    {
        /// <summary>场景地址（YooAsset 清单里配的 Address）。</summary>
        string Location { get; }

        /// <summary>当前加载状态。</summary>
        AssetStatus Status { get; }

        /// <summary>是否已完成，成功与失败都算完成。</summary>
        bool IsDone { get; }

        /// <summary>加载进度，0..1。</summary>
        float Progress { get; }

        /// <summary>失败原因；未失败时为空串，实现方保证不为 null。</summary>
        string Error { get; }

        /// <summary>加载到的场景；未完成时是无效场景（IsValid 为 false），用之前先看 IsDone。</summary>
        Scene Scene { get; }

        /// <summary>手动激活场景：只在加载时要求不自动激活才需要调用；否则激活是自动的，再调没有意义。</summary>
        /// <returns>true 表示请求被接受；false 表示没被接受，本层不重试。</returns>
        bool Activate();

        /// <summary>阻塞直到加载完成，只给编辑器与工具用；底层不支持时可能立即返回，调用后仍需自行确认 IsDone。</summary>
        void WaitForAsyncComplete();
    }

    // 主要功能：一次底层场景加载的抽象，把状态、进度、场景与完成通知定成契约，框架与业务都不必认识具体资源库。
    // 主要数据成员：Location（场景地址）、Status（AssetStatus）、Progress（0..1）、Error（失败原因）、Scene（场景）、OnComplete（完成通知）。
    // 主要外部方法：Activate / WaitForAsyncComplete / Dispose，以及只读的 Location / Status / Progress / Error / Scene / IsDone、事件 OnComplete。
    // 谁在用它：适配层（NBC.Framework.YooAsset 的 YooAssetSceneOperation）实现它；AssetManager 拿到后交给 SceneHandle 包成 ISceneHandle。
    /// <summary>
    /// 一次底层场景加载。适配层负责把具体资源库的场景句柄翻译成这个形状，框架只认它。
    /// </summary>
    public interface ISceneLoadOperation : IDisposable
    {
        /// <summary>场景地址。</summary>
        string Location { get; }

        /// <summary>当前状态。</summary>
        AssetStatus Status { get; }

        /// <summary>加载进度，0..1。</summary>
        float Progress { get; }

        /// <summary>失败原因；未失败时为空串。</summary>
        string Error { get; }

        /// <summary>加载到的场景；未完成时是无效场景。</summary>
        Scene Scene { get; }

        /// <summary>是否已完成，成功与失败都算完成。</summary>
        bool IsDone { get; }

        /// <summary>完成通知：加载结束时触发一次，成功与失败都会走这里。</summary>
        event Action<ISceneLoadOperation> OnComplete;

        /// <summary>手动激活场景：只在加载时要求不自动激活才需要调用；否则底层自己会激活。</summary>
        /// <returns>true 表示请求被接受；false 表示没被接受，本层不重试。</returns>
        bool Activate();

        /// <summary>阻塞直到加载完成，只给编辑器与工具用；底层不支持时可能立即返回，调用后仍需自行确认 IsDone。</summary>
        void WaitForAsyncComplete();
    }

    // 主要功能：ISceneHandle 的框架侧默认实现 —— 把底层 ISceneLoadOperation 包一层，读取全部转发，Dispose 时经加载器卸载场景。
    // 主要数据成员：m_owner（IAssetProvider，卸载请求的回传目标）、m_operation（底层操作，Dispose 之后为 null）。
    // 主要外部方法：Activate / WaitForAsyncComplete / Dispose，以及只读的 Operation / Location / Status / IsDone / Progress / Error / Scene。
    // 谁在用它：AssetManager.LoadSceneAsync 构造并当 ISceneHandle 返回；业务侧经 SceneLoader 间接持有。
    // 注意：这里不缓存、不引用计数 —— 一次加载一个句柄，Dispose 后 m_operation 为 null，所有读取都走空值分支，句柄不会复活。
    internal sealed class SceneHandle : ISceneHandle
    {
        // 所属加载器，卸载请求回传给谁；构造时给定，之后不变，框架路径不会传 null
        private readonly IAssetProvider m_owner;
        // 底层场景加载操作；加载期间非 null，Dispose 置 null 即代表句柄失效，此后所有读取都按失效状态处理
        private ISceneLoadOperation m_operation;

        // 功能：把所属加载器与底层操作绑到句柄上；不改动底层状态，也不发起加载。
        // 入参：owner 所属加载器，Dispose 时用它回传卸载请求；operation 底层加载操作，不能为 null
        // 返回：无
        // 步骤：
        //   1) 先落 owner，保证 Dispose 时一定有回传目标
        //   2) 再落 operation，此后所有读取都经它转发
        // 上游：AssetManager.LoadSceneAsync（它已校验 operation 非 null）
        // 下游：无
        public SceneHandle(IAssetProvider owner, ISceneLoadOperation operation)
        {
            m_owner = owner;
            m_operation = operation;
        }

        // 功能：把底层操作原样交出去，供加载器内部取用。
        public ISceneLoadOperation Operation
        {
            get { return m_operation; }
        }

        // 功能：给出场景地址；句柄失效后返回空串。
        public string Location
        {
            get { return m_operation == null ? string.Empty : m_operation.Location; }
        }

        // 功能：给出当前状态；句柄失效后一律算 Failed，宁可报失败也不给「看起来还在加载」的假象。
        public AssetStatus Status
        {
            get { return m_operation == null ? AssetStatus.Failed : m_operation.Status; }
        }

        // 功能：回答加载是否结束；句柄失效后恒为 true。
        public bool IsDone
        {
            get { return m_operation == null || m_operation.IsDone; }
        }

        // 功能：给出加载进度，0..1；句柄失效后为 0。
        public float Progress
        {
            get { return m_operation == null ? 0f : m_operation.Progress; }
        }

        // 功能：给出失败原因；句柄失效时返回「场景句柄已被释放」，底层给 null 时也返回空串，调用方不必判空。
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

        // 功能：给出加载到的场景；句柄失效后是默认（无效）场景。
        public Scene Scene
        {
            get { return m_operation == null ? default(Scene) : m_operation.Scene; }
        }

        // 功能：把激活请求转给底层操作；句柄失效时返回 false，不重试。
        public bool Activate()
        {
            return m_operation != null && m_operation.Activate();
        }

        // 功能：把阻塞等待转给底层操作；句柄失效时什么都不做。
        // 注意：底层可能不支持同步等待（YooAsset 就如此），那里只打警告后立即返回，返回不代表场景已就绪。
        public void WaitForAsyncComplete()
        {
            if (m_operation != null)
            {
                m_operation.WaitForAsyncComplete();
            }
        }

        // 功能：卸载场景 —— 先摘掉底层操作让句柄失效，再让加载器去卸，重复调用不会重复卸载。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已经没有底层操作就直接返回，保证幂等
        //   2) 先把操作挪到局部变量、再把字段置 null，此刻句柄即失效
        //   3) 最后让加载器卸它 —— 置空在前，卸载回调里再进 Dispose 也只会走第 1 步
        // 上游：业务层，以及 SceneLoader 的取消与超时收尾
        // 下游：IAssetProvider.UnloadScene（适配层里 UnloadSceneAsync 再释放句柄）
        // 注意：与资源句柄不同，这个 Dispose 就等于卸载场景；只丢句柄不卸载，场景会一直占着内存。
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
