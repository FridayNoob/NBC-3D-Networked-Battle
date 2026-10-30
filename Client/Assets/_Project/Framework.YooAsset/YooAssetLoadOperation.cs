// 这个文件里有：
//   YooAssetLoadOperation（类）：把 YooAsset v3 的 AssetHandle 包成框架的 IAssetLoadOperation，并把状态枚举翻译过来

using System;
using NBC.Framework.Asset;
using YooAsset;

namespace NBC.Framework.Asset.Adapter
{
    // 主要功能：把 YooAsset v3 的资源句柄包成框架的 IAssetLoadOperation，状态 / 进度 / 错误 / 资源都转成框架自己的形状。
    // 主要数据成员：m_handle（底层 YooAsset 句柄）、m_disposed（Dispose 是否已执行过）。
    // 主要外部方法：Location / Status / Progress / Error / AssetObject / IsDone / WaitForAsyncComplete / Dispose，以及给场景适配用的静态 MapStatus。
    // 谁在用它：YooAssetProvider.LoadAsync / LoadSync 造它；AssetManager 的缓存条目 Entry 订阅 OnComplete 并负责调 Dispose。
    // 注意：本类只给原始资源，不自动实例化 —— 实例化是调用方的显式动作，池正好用工厂拿实例，两边不纠缠引用计数。
    // 注意：装的是 YooAsset 3.0.5 的 v3 API，v2.3 那批名字被 YOOASSET_LEGACY_API 包着且工程未定义，写了编不过。
    internal sealed class YooAssetLoadOperation : IAssetLoadOperation
    {
        // 底层 YooAsset 句柄，构造时订阅它的 Completed，Dispose 之前一直有效
        private readonly AssetHandle m_handle;

        // 是否已经 Dispose 过；取值 true / false，只有 Dispose 自己看它
        private bool m_disposed;

        // 功能：存下资源地址与底层句柄，并订阅句柄的完成事件，把结果转成本类的 OnComplete。
        // 入参：location 资源地址（YooAsset 的 location）；handle YooAsset 已返回的资源句柄
        // 返回：无
        // 步骤：
        //   1) 先存地址与句柄，后面所有成员都读这两个字段
        //   2) 当场订阅 Completed：这是本类唯一的完成来源，订阅不能拖到构造返回之后
        // 上游：YooAssetProvider.LoadAsync / LoadSync（异步与同步两条路都走这个构造）
        // 下游：m_handle.Completed
        // 注意：句柄若在订阅前就已加载完，YooAsset 的 Completed 会当场同步回调（它的 add 访问器判到 IsDone 就直接 Invoke），
        //   这条通知早于调用方订阅 OnComplete —— 所以判断是否完成要以 IsDone 兜底，不能只等 OnComplete。
        public YooAssetLoadOperation(string location, AssetHandle handle)
        {
            Location = location;
            m_handle = handle;
            m_handle.Completed += HandleCompleted;
        }

        // 完成通知，底层句柄完成时回调一次，订阅者拿到的是本操作本身
        public event Action<IAssetLoadOperation> OnComplete;

        // 资源地址（YooAsset 的 location），构造时传入，之后不变
        public string Location { get; private set; }

        // 当前状态，每次读都由 MapStatus 从 YooAsset 的 EOperationStatus 现翻译；取值 None / Loading / Succeeded / Failed
        public AssetStatus Status
        {
            get { return MapStatus(m_handle.Status); }
        }

        // 加载进度，0..1 的浮点值，由 YooAsset 句柄给出
        public float Progress
        {
            get { return m_handle.Progress; }
        }

        // 失败原因，未失败时为空串（句柄给 null 也兜成空串，调用方不用判空）
        public string Error
        {
            get
            {
                string e = m_handle.Error;
                return e ?? string.Empty;
            }
        }

        // 加载到的原始资源，不是实例；未加载完成时为 null，实例化由调用方自己调 Instantiate
        public UnityEngine.Object AssetObject
        {
            get { return m_handle.AssetObject; }
        }

        // 是否已完成，成功与失败都算完成
        public bool IsDone
        {
            get { return m_handle.IsDone; }
        }

        // 功能：把「阻塞等到加载完成」原样转给 YooAsset 句柄；只给编辑器 / 工具用，Player 里会卡住主线程。
        public void WaitForAsyncComplete()
        {
            m_handle.WaitForAsyncComplete();
        }

        // 功能：退订完成事件并释放底层 YooAsset 句柄，重复调用只生效一次。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 用 m_disposed 做幂等闸门：句柄释放与缓存条目丢弃都会调到这儿，重复调用是常态
        //   2) 先退订 Completed 再释放：否则释放途中若发完成通知，会把已经作废的操作回调给订阅者
        //   3) 最后只调句柄的 Dispose，不调 Release
        // 上游：AssetManager.Entry.DisposeOperation（缓存条目被丢弃时）
        // 下游：m_handle.Completed、m_handle.Dispose
        // 注意：YooAsset 的 HandleBase.Dispose 内部就是调 Release，两个都调等于释放两次。
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

        // 功能：把 YooAsset 的 EOperationStatus 翻译成框架的 AssetStatus。
        // 入参：status YooAsset 的操作状态，正常是 None / Processing / Succeeded / Failed 四个之一
        // 返回：框架状态；不认识的取值一律按 None（当作还没开始），不会抛
        // 步骤：
        //   1) 逐个显式映射，不靠枚举数值强转：数值对得上只是巧合，YooAsset 以后插入取值就会静默错位
        //   2) 留 default 兜底，遇到不认识的状态按 None 处理而不是抛
        // 上游：本类 Status 属性、YooAssetSceneOperation.Status
        // 下游：无
        // 注意：两个枚举刻意不共用，业务层不该看见 EOperationStatus。
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

        // 功能：把底层句柄的完成事件转成本类的 OnComplete 通知。
        // 入参：handle 已完成的 YooAsset 句柄，本类只用它触发，不读内容
        // 返回：无
        // 步骤：
        //   1) 先把事件复制到局部变量再判空：判空与调用之间若有订阅者被摘掉，直接读字段会空引用
        //   2) 用 this 回调，让订阅者拿到操作本身去读结果
        // 上游：m_handle 的 Completed
        // 下游：OnComplete 的订阅者（AssetManager 的 Entry）
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
