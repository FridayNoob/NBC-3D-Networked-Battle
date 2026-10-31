// 这个文件里有：
//   AssetManager（类）：资源层门面，按「地址 + 类型」缓存条目、管引用计数与句柄生命周期，并负责释放与调试统计
//   AssetKey（结构体）：缓存键，地址加类型；同一地址按不同类型加载会各存一条
//   Entry（类）：一条缓存记录，持有一个底层加载操作与当前引用计数，多个句柄可指向同一条目
// 注意：两级释放 —— 句柄 Dispose 只把引用计数减一，ReleaseUnused 才真正释放计数归零的条目，UnloadUnusedAssets 再让底层做引擎级卸载。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NBC.Framework.Asset
{
    // 主要功能：资源层门面 —— 业务只跟它打交道，它按「地址 + 类型」缓存条目并累加引用计数，句柄释放只减计数，回收与底层卸载分开做。
    // 主要数据成员：m_provider（底层加载器，null 表示没装）、m_cache（按「地址 + 类型」索引条目的字典）、m_entries（全部条目，回收时遍历）、m_recycleBuffer（回收临时表，避免每次分配）。
    // 主要外部方法：SetProvider / InitializeAsync / LoadAssetAsync / LoadAsset / LoadSceneAsync / ReleaseUnused / UnloadUnusedAssets / UnloadAll / GetDebugInfo，以及 HasProvider / Provider / IsInitialized。
    // 谁在用它：Boot 的 AssetBootstrapper 经 YooAssetProvider.Install 装加载器并初始化；Game 的 AssetConfigSource、Framework.UI 的 UIManager、Framework 的 SceneLoader 取资源与场景；Editor 引导菜单与测试直接调。
    /// <summary>资源层门面：按「地址 + 类型」缓存条目并做引用计数，业务只经它取资源，不接触任何具体资源库。</summary>
    public sealed class AssetManager : Singleton<AssetManager>
    {
        private IAssetProvider m_provider;                 // 底层加载器；null 表示还没装上（未初始化）

        private readonly Dictionary<AssetKey, Entry> m_cache = new Dictionary<AssetKey, Entry>();   // 按「地址 + 类型」索引的缓存；一个键一条 Entry，条目数与 m_entries 相同

        private readonly List<Entry> m_entries = new List<Entry>();   // 全部条目，与 m_cache 同步增删，元素数等于缓存条目数；回收时按它遍历（字典遍历中不能改，所以另存一份）

        private readonly List<Entry> m_recycleBuffer = new List<Entry>();   // 回收时复用的临时列表，用前 Clear；元素数 ≤ m_entries.Count

        /// <summary>底层加载器是否已经装上；没装时加载会抛异常，不会走到空引用。</summary>
        // 功能：读 m_provider 是否为空，不触发初始化。
        public bool HasProvider
        {
            get { return m_provider != null; }
        }

        /// <summary>当前装上的底层加载器；只给适配层收尾与引导流程的健康检查用，业务层不要碰。</summary>
        // 功能：把加载器原样交出去，没装时为 null。
        // 注意：拿到的是活引用，别绕过 AssetManager 直接卸载；换加载器必须先 UnloadAll。
        public IAssetProvider Provider
        {
            get { return m_provider; }
        }

        /// <summary>资源层是否已完成初始化；没装加载器或加载器还没初始化都是 false。</summary>
        // 功能：装了加载器并且它自己报已初始化才算 true，加载前的检查就看这个。
        public bool IsInitialized
        {
            get { return m_provider != null && m_provider.IsInitialized; }
        }

        // 初始化

        /// <summary>装上底层加载器；由引导代码调一次，装过就不允许换，换之前必须先 UnloadAll。</summary>
        /// <param name="provider">底层加载器，不能为 null；框架不认识具体资源库，只认这个接口。</param>
        // 功能：把底层加载器挂到门面上，之后所有加载都走它。
        // 入参：provider 底层加载器，null 会抛 ArgumentNullException
        // 返回：无
        // 步骤：
        //   1) 先判 null，早抛比等到第一次加载才发现空引用清楚
        //   2) 已经装过另一个加载器就拒绝 —— 旧加载器手里的资源会变成孤儿，必须先 UnloadAll 再换
        //   3) 记下加载器；真正的初始化留给 InitializeAsync 按运行模式做
        // 上游：YooAssetProvider.Install（由 AssetBootstrapper.InstallAsync 调）
        // 下游：无
        // 注意：同一实例重复装是放行的（ReferenceEquals 判断），换别的实例才抛；换之前不清干净，旧加载器的资源没人释放。
        public void SetProvider(IAssetProvider provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            if (m_provider != null && !ReferenceEquals(m_provider, provider))
            {
                // 换加载器必须先清干净，否则旧加载器持有的资源会变成孤儿
                throw new InvalidOperationException(
                    "[AssetManager] 已经装过加载器了。换加载器前请先 UnloadAll() 并把旧加载器卸下。");
            }

            m_provider = provider;
        }

        /// <summary>初始化资源层（建包、加载清单），await 完成后才能加载资源。</summary>
        /// <param name="mode">运行模式，决定内容来自工程资源、随包内容还是资源服务器。</param>
        /// <returns>初始化任务；await 完成即资源层就绪，失败以异常抛出，没有返回码。</returns>
        // 功能：把初始化转给底层加载器，本层只做「装没装加载器」的前置检查。
        // 入参：mode 运行模式，三个取值之一；联机模式目前适配层会抛 NotSupportedException
        // 返回：Task 完成表示初始化成功；失败通过异常上抛，没有返回码
        // 步骤：
        //   1) 先确认装过加载器，没装就抛「请先 SetProvider」，而不是等空引用
        //   2) 再原样等加载器初始化，本层不加缓存也不重试
        // 上游：AssetBootstrapper.InstallAsync（Boot 启动流程）
        // 下游：IAssetProvider.InitializeAsync（适配层建包并加载清单）
        // 注意：初始化失败必须抛出来，吞掉会让后面的加载全变成「资源为空」的怪现象。
        public async Task InitializeAsync(AssetRuntimeMode mode)
        {
            ThrowIfNoProvider();
            await m_provider.InitializeAsync(mode);
        }

        // 加载

        /// <summary>异步加载一个资源；同一地址加类型重复调用复用缓存并累加引用计数，用完要 Dispose 句柄。</summary>
        /// <typeparam name="T">资源类型；缓存键带上类型，同一地址按不同类型加载各存一条。</typeparam>
        /// <param name="location">资源地址（资源库里的 Address），不能为空。</param>
        /// <returns>资源句柄；加载未必已完成，成败看 Status / IsValid，不用时必须 Dispose。</returns>
        // 功能：查缓存，命中就加计数，没命中或上次失败就新建条目并发起底层加载。
        // 入参：location 资源地址，不能为空；T 期望类型，进缓存键
        // 返回：资源句柄，引用计数已含这一份；加载可能还没完成
        // 步骤：
        //   1) 先做前置检查（装了加载器、已初始化、地址非空），引导流程写错就在这里报
        //   2) 用「地址 + 类型」组键查缓存：命中且没失败就只加计数，复用同一次加载
        //   3) 命中但状态是 Failed 就先丢掉重建 —— 失败条目不复用，给一次自愈机会
        //   4) 没命中就新建条目，计数从 1 起并发起异步加载
        //   5) 用条目造句柄交出去，句柄 Dispose 时回来减计数
        // 上游：UIManager 取 UI 预制体、AssetConfigSource 取配置表、测试
        // 下游：ThrowIfNotInitialized、ThrowIfBadLocation、CreateEntry / DropEntry、AssetHandle 构造
        // 注意：句柄不用时必须 Dispose，否则计数一直不降，条目永远不会被 ReleaseUnused 回收。
        // 注意：拿到的是原始资源不是实例，GameObject 要自己 Instantiate（见 AssetHandle.Asset）。
        public AssetHandle<T> LoadAssetAsync<T>(string location) where T : UnityEngine.Object
        {
            ThrowIfNotInitialized();
            ThrowIfBadLocation(location);

            AssetKey key = new AssetKey(location, typeof(T));

            Entry entry;
            if (m_cache.TryGetValue(key, out entry))
            {
                if (entry.Status == AssetStatus.Failed)
                {
                    // 失败过的条目不复用：丢掉重来一次，给一次自愈机会
                    DropEntry(entry);
                    entry = CreateEntry(key, false);
                }
                else
                {
                    entry.Retain();
                }
            }
            else
            {
                entry = CreateEntry(key, false);
            }

            return new AssetHandle<T>(this, entry);
        }

        /// <summary>同步加载一个资源；会阻塞主线程，只用于编辑器工具或极小资源。</summary>
        /// <typeparam name="T">资源类型；缓存键带上类型。</typeparam>
        /// <param name="location">资源地址，不能为空。</param>
        /// <returns>资源句柄，返回时加载已完成；成败仍读 Status。</returns>
        // 功能：与 LoadAssetAsync 同一套缓存逻辑，只是走加载器的同步接口，并在播放模式下打警告。
        // 入参：location 资源地址，不能为空；T 期望类型，进缓存键
        // 返回：资源句柄，返回时已完成；成功与否看 Status，不能只看调用没抛异常
        // 步骤：
        //   1) 前置检查同异步版（装了加载器、已初始化、地址非空）
        //   2) 播放模式下先打一条警告 —— 同步加载会阻塞主线程，提醒调用方这是受限接口
        //   3) 缓存策略与异步版一致：命中加计数、失败重建、未命中新建
        //   4) 新建时把 sync 置 true，让 CreateEntry 走加载器的同步接口
        // 上游：Editor 的引导菜单、只处理极小资源的编辑器工具
        // 下游：ThrowIfNotInitialized、ThrowIfBadLocation、CreateEntry / DropEntry、AssetHandle 构造
        // 注意：Player 里对大资源同步加载就是卡顿来源，只应用于编辑器工具或极小资源（见 Docs/02）。
        public AssetHandle<T> LoadAsset<T>(string location) where T : UnityEngine.Object
        {
            ThrowIfNotInitialized();
            ThrowIfBadLocation(location);

            if (Application.isPlaying)
            {
                Debug.LogWarning("[AssetManager] 在播放模式下同步加载了资源【" + location +
                                 "】。同步加载会阻塞主线程，只应用于编辑器工具或极小资源（ADR-001 决策 5）。");
            }

            AssetKey key = new AssetKey(location, typeof(T));

            Entry entry;
            if (m_cache.TryGetValue(key, out entry))
            {
                if (entry.Status == AssetStatus.Failed)
                {
                    DropEntry(entry);
                    entry = CreateEntry(key, true);
                }
                else
                {
                    entry.Retain();
                }
            }
            else
            {
                entry = CreateEntry(key, true);
            }

            return new AssetHandle<T>(this, entry);
        }

        // 场景（也走资源层，不直接用 SceneManager.LoadScene）

        /// <summary>异步加载一个场景；场景也走资源层，不做缓存，句柄 Dispose 就卸载场景。</summary>
        /// <param name="location">场景地址，不能为空。</param>
        /// <param name="mode">Single 切到新场景，Additive 叠加到当前场景。</param>
        /// <returns>场景句柄；它的 Dispose 会卸载场景，不是只减引用计数。</returns>
        // 功能：把场景加载转给底层加载器，再用返回的操作包一个场景句柄。
        // 入参：location 场景地址，不能为空；mode 单场景或叠加
        // 返回：场景句柄，加载可能还没完成；进度与成败都读句柄
        // 步骤：
        //   1) 前置检查（装了加载器、已初始化、地址非空）
        //   2) 交给加载器发起场景加载，本层不做缓存 —— 叠加模式本来就要能加载多个
        //   3) 操作返回 null 视为加载器实现错误，当场抛，别把空句柄交出去
        //   4) 用操作造场景句柄，卸载由句柄的 Dispose 触发
        // 上游：SceneLoader.Load（再往上是业务切场景）
        // 下游：IAssetProvider.LoadSceneAsync、SceneHandle 构造
        // 注意：场景没有引用计数 —— 句柄 Dispose 就卸载场景，与资源句柄「只减计数」的语义不同，别混用。
        // 注意：要进度、超时或事件广播请用 SceneLoader，本方法只负责发起加载。
        public ISceneHandle LoadSceneAsync(string location, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            ThrowIfNotInitialized();
            ThrowIfBadLocation(location);

            ISceneLoadOperation operation = m_provider.LoadSceneAsync(location, mode);
            if (operation == null)
            {
                throw new InvalidOperationException(
                    "[AssetManager] 加载器对场景【" + location + "】返回了 null 操作。加载器实现有误。");
            }

            return new SceneHandle(m_provider, operation);
        }

        // 释放与观测

        /// <summary>释放引用计数已归零的缓存条目（不触发引擎级卸载）；返回本次真正释放的条目数。</summary>
        /// <returns>本次释放掉的条目数，0 表示没有可回收的。</returns>
        // 功能：先把计数 ≤ 0 的条目挑进临时表，再逐个丢弃，避免边遍历边改列表。
        // 入参：无
        // 返回：本次真正释放的条目数，0 表示没有可回收的
        // 步骤：
        //   1) 先清临时表，复用它避免每次分配
        //   2) 按 m_entries 挑出 RefCount ≤ 0 的条目 —— 不能边遍历边删
        //   3) 再按下标逐个 DropEntry，真正释放底层操作
        //   4) 记下数量并清空临时表，留给下一轮
        // 上游：UnloadUnusedAssets、测试、切场景 / 回大厅的收尾
        // 下游：DropEntry
        // 注意：只清本层缓存，不碰引擎级资源；要引擎级回收再调 UnloadUnusedAssets。
        // 注意：Dispose 不立刻卸载正是为了「放下又马上要用」（关掉面板又立刻打开），所以回收时机由调用方定，别在每次 Dispose 后都调它。
        public int ReleaseUnused()
        {
            m_recycleBuffer.Clear();
            for (int i = 0; i < m_entries.Count; i++)
            {
                if (m_entries[i].RefCount <= 0)
                {
                    m_recycleBuffer.Add(m_entries[i]);
                }
            }

            for (int i = 0; i < m_recycleBuffer.Count; i++)
            {
                DropEntry(m_recycleBuffer[i]);
            }

            int released = m_recycleBuffer.Count;
            m_recycleBuffer.Clear();
            return released;
        }

        /// <summary>先释放计数归零的条目，再让底层资源库做引擎级卸载；只发起，不等完成。</summary>
        // 功能：分两步收尾 —— 本层先回收零计数条目，再让加载器卸载引擎里没人用的资源。
        // 入参：无
        // 返回：无（只发起，不等待完成）
        // 步骤：
        //   1) 先 ReleaseUnused 清掉本层缓存，否则底层仍会认为有引用
        //   2) 装了加载器再转给它的 UnloadUnused；没装就只做第一步，切场景时也不会炸
        // 上游：切场景 / 回大厅这类整场收尾（目前测试直接调）
        // 下游：ReleaseUnused、IAssetProvider.UnloadUnused
        // 注意：句柄还拿着的条目不会被回收，计数没归零就调它等于什么都没做。
        public void UnloadUnusedAssets()
        {
            ReleaseUnused();

            if (m_provider != null)
            {
                m_provider.UnloadUnused();
            }
        }

        /// <summary>清空全部缓存并让底层卸载所有资源；用于切场景 / 回大厅，调用后先前句柄全部失效。</summary>
        // 功能：把本层所有条目与缓存一次清掉，再通知加载器卸载它手里的全部资源。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先逐个 DisposeOperation 释放底层操作 —— 只清列表不清操作会漏资源
        //   2) 再清条目表与缓存字典，两者必须一起清，否则会留下查不到条目却还在表里的脏数据
        //   3) 最后让加载器卸载全部资源，没装加载器时跳过
        // 上游：切场景 / 回大厅 / 换加载器之前、测试
        // 下游：Entry.DisposeOperation、IAssetProvider.UnloadAll
        // 注意：调用后先前发出的句柄全部失效，再读 Status 会变成 Failed；不要拿它当「释放一部分」用。
        public void UnloadAll()
        {
            for (int i = 0; i < m_entries.Count; i++)
            {
                m_entries[i].DisposeOperation();
            }

            m_entries.Clear();
            m_cache.Clear();

            if (m_provider != null)
            {
                m_provider.UnloadAll();
            }
        }

        /// <summary>取一次观测快照：按引用计数与状态统计条目数，供调试面板与测试查资源泄漏。</summary>
        /// <returns>缓存条目总数、活跃 / 待回收 / 加载中 / 失败条目数与引用计数之和的一次快照。</returns>
        // 功能：一趟遍历把六项计数算出来，交给 AssetDebugInfo 装成快照。
        // 入参：无
        // 返回：六项计数的一次快照（五项条目数加一项引用计数之和），之后缓存再变也不影响它
        // 步骤：
        //   1) 一趟遍历，先按 RefCount 是否大于 0 分活跃 / 待回收
        //   2) 再按 IsDone 与 Status 分加载中 / 失败，未完成的不算失败
        //   3) 顺路累加 RefCount，最后一次性构造快照
        // 上游：调试面板、EditMode 与 PlayMode 测试
        // 下游：AssetDebugInfo 构造
        // 注意：这是快照不是实时值；加载中与失败互斥，同一趟遍历里不会重复计。
        public AssetDebugInfo GetDebugInfo()
        {
            int active = 0;
            int unused = 0;
            int loading = 0;
            int failed = 0;
            int totalRefs = 0;

            for (int i = 0; i < m_entries.Count; i++)
            {
                Entry e = m_entries[i];
                if (e.RefCount > 0)
                {
                    active++;
                }
                else
                {
                    unused++;
                }

                if (!e.IsDone)
                {
                    loading++;
                }
                else if (e.Status == AssetStatus.Failed)
                {
                    failed++;
                }

                totalRefs += e.RefCount;
            }

            return new AssetDebugInfo(m_entries.Count, active, unused, loading, failed, totalRefs);
        }

        // 内部

        // 功能：句柄 Dispose 时把条目的引用计数减一，只减不卸载。
        // 入参：entry 要减计数的条目，null 直接忽略
        // 返回：无
        // 步骤：
        //   1) entry 为 null 直接返回，正常路径不会传 null，这里只做防御
        //   2) 计数大于 0 才减，避免出现负计数把回收逻辑带偏
        // 上游：AssetHandle.Dispose
        // 下游：无（真正释放等 ReleaseUnused）
        // 注意：这里不卸载 —— 放下又马上要用的情况很常见，立刻卸载会让它反复加载，反而更慢。
        internal void Release(Entry entry)
        {
            if (entry == null)
            {
                return;
            }

            if (entry.RefCount > 0)
            {
                entry.RefCount--;
            }
        }

        // 功能：按同步 / 异步发起一次底层加载，建条目、计一次引用，并挂进缓存与条目表。
        // 入参：key 缓存键（地址 + 类型）；sync 为 true 走加载器的同步接口，否则走异步
        // 返回：新建的条目，引用计数已置 1，调用方直接把它交给句柄
        // 步骤：
        //   1) 按 sync 选 LoadSync / LoadAsync，本层不判断资源是否真的存在
        //   2) 操作返回 null 视为加载器实现错误，当场抛 —— 否则后面每个句柄都会是空操作
        //   3) 先造条目（构造时订阅 OnComplete），再把计数置 1
        //   4) 最后才进缓存与条目表，保证两个容器里要么都有、要么都没有
        // 上游：LoadAssetAsync / LoadAsset（缓存未命中或上次失败）
        // 下游：IAssetProvider.LoadAsync / LoadSync、Entry 构造
        // 注意：同步加载可能在这里就已经完成，句柄构造时会补发一次完成通知，别假设回调一定在返回之后。
        private Entry CreateEntry(AssetKey key, bool sync)
        {
            IAssetLoadOperation operation = sync
                ? m_provider.LoadSync(key.Location, key.AssetType)
                : m_provider.LoadAsync(key.Location, key.AssetType);

            if (operation == null)
            {
                throw new InvalidOperationException(
                    "[AssetManager] 加载器对【" + key.Location + "】返回了 null 操作。加载器实现有误。");
            }

            Entry entry = new Entry(key, operation, this);
            entry.RefCount = 1;

            m_cache.Add(key, entry);
            m_entries.Add(entry);
            return entry;
        }

        // 功能：丢掉一个条目：先释放底层操作，再从缓存与条目表同时移除。
        // 入参：entry 要丢弃的条目，必须是本类建过的
        // 返回：无
        // 步骤：
        //   1) 先 DisposeOperation 释放底层句柄，并断掉它对完成事件的订阅
        //   2) 再从两个容器移除；先释放再移除，保证离开缓存前底层操作已经关掉
        // 上游：ReleaseUnused、LoadAssetAsync / LoadAsset 的失败重建
        // 下游：Entry.DisposeOperation
        // 注意：只移除不 DisposeOperation，底层资源会失去唯一句柄，泄漏在资源库里。
        private void DropEntry(Entry entry)
        {
            entry.DisposeOperation();
            m_cache.Remove(entry.Key);
            m_entries.Remove(entry);
        }

        // 功能：没装加载器就抛异常，把「引导流程忘了装」变成一个说得清的错误。
        // 入参：无
        // 返回：无；不抛就表示加载器已装上
        // 步骤：
        //   1) 判 m_provider 是否为空，为空就抛出带「先 SetProvider」提示的异常
        // 上游：InitializeAsync、ThrowIfNotInitialized
        // 下游：无
        // 注意：这是运行期检查，编译期拦不住；漏装加载器的症状就是启动即抛这一句。
        private void ThrowIfNoProvider()
        {
            if (m_provider == null)
            {
                throw new InvalidOperationException(
                    "[AssetManager] 还没有装上底层加载器。请先调用 " +
                    "AssetManager.Instance.SetProvider(...) 再使用（见 Docs/02 ADR-001 决策 6）。");
            }
        }

        // 功能：加载前确认加载器已装且已初始化，把「没初始化就加载」当场报出来。
        // 入参：无
        // 返回：无；不抛就表示可以加载
        // 步骤：
        //   1) 先走 ThrowIfNoProvider，没装加载器时给的是装加载器的提示而不是空引用
        //   2) 再看 IsInitialized，false 就提示先 await InitializeAsync
        // 上游：LoadAssetAsync / LoadAsset / LoadSceneAsync
        // 下游：ThrowIfNoProvider
        // 注意：直接判 IsInitialized 而不先查 m_provider 会空引用，所以顺序不能反。
        private void ThrowIfNotInitialized()
        {
            ThrowIfNoProvider();

            if (!m_provider.IsInitialized)
            {
                throw new InvalidOperationException(
                    "[AssetManager] 资源层尚未初始化。请先 " +
                    "await AssetManager.Instance.InitializeAsync(mode) 再加载资源。");
            }
        }

        // 功能：地址为空或 null 就抛 ArgumentException，避免带空地址进缓存。
        // 入参：location 资源或场景地址，不能为空
        // 返回：无；不抛就表示地址可用
        // 步骤：
        //   1) 用 string.IsNullOrEmpty 判空，null 与空串都挡在这里
        // 上游：LoadAssetAsync / LoadAsset / LoadSceneAsync
        // 下游：无
        // 注意：只挡空地址，不校验地址是否真的存在 —— 写错的地址要到加载失败时才看得出来。
        private static void ThrowIfBadLocation(string location)
        {
            if (string.IsNullOrEmpty(location))
            {
                throw new ArgumentException("[AssetManager] 资源地址不能为空。", nameof(location));
            }
        }

        // 主要功能：当缓存键用 —— 把「地址 + 类型」包成一个可比较、可哈希的只读值。
        // 主要数据成员：Location（资源地址，字符串）、AssetType（资源类型，Type）。
        // 主要外部方法：构造函数、Equals（两个重载）、GetHashCode、ToString。
        // 谁在用它：AssetManager 的缓存字典与 Entry 都用它；内部结构，只有本程序集可见。
        internal readonly struct AssetKey : IEquatable<AssetKey>
        {
            public readonly string Location;   // 资源地址，即加载时传入的 location

            public readonly Type AssetType;    // 资源类型；同一地址按不同类型加载会各存一条

            // 功能：原样记下地址与类型，不做校验也不规范化。
            // 入参：location 资源地址；assetType 资源类型
            // 返回：无（构造函数）
            // 步骤：逐个赋给只读字段，键只用来查询，不在这里判空
            // 上游：AssetManager.LoadAssetAsync / LoadAsset
            // 下游：无
            // 注意：地址按原字符串比较且区分大小写，大小写写错就是两条缓存。
            public AssetKey(string location, Type assetType)
            {
                Location = location;
                AssetType = assetType;
            }

            // 功能：地址按 Ordinal 比较、类型按引用比较，两者都相等才算同一个键。
            // 入参：other 另一个键
            // 返回：true 表示同一个（地址 + 类型）
            // 步骤：先比地址（区分大小写），再比类型，两个都相等才返回 true
            // 上游：字典查找、Equals(object)
            // 下游：string.Equals
            public bool Equals(AssetKey other)
            {
                return string.Equals(Location, other.Location, StringComparison.Ordinal)
                       && AssetType == other.AssetType;
            }

            // 功能：把 object 拆成 AssetKey 再交给强类型版本，供非泛型集合与断言用。
            // 入参：obj 任意对象，不是 AssetKey 时结果为 false
            // 返回：true 表示 obj 是值相等的 AssetKey
            // 步骤：先判类型，再转成 AssetKey 调强类型 Equals
            // 上游：object.Equals 的调用方、测试断言
            // 下游：Equals(AssetKey)
            public override bool Equals(object obj)
            {
                return obj is AssetKey && Equals((AssetKey)obj);
            }

            // 功能：把地址与类型混成一个哈希，供字典分桶。
            // 入参：无
            // 返回：哈希值；地址或类型为 null 时该项按 0 计
            // 步骤：先取地址哈希，再乘 397 异或类型哈希，与 Equals 比较的字段保持一致
            // 上游：Dictionary 查找
            // 下游：string.GetHashCode、Type.GetHashCode
            // 注意：字段改了必须同步改这里，否则字典会找不到已经放进去的键。
            public override int GetHashCode()
            {
                int h = Location == null ? 0 : Location.GetHashCode();
                return (h * 397) ^ (AssetType == null ? 0 : AssetType.GetHashCode());
            }

            // 功能：拼成「类型名 @ 地址」，日志里能直接看出是哪个键。
            // 入参：无（重写 object.ToString）
            // 返回：形如「Texture2D @ ui/panel」的文本；类型为 null 时类型名是 ?，地址为 null 时尾部为空
            // 步骤：按「类型名 @ 地址」拼接，只给调试看，不参与比较
            // 上游：日志、测试失败信息
            // 下游：无
            public override string ToString()
            {
                return (AssetType == null ? "?" : AssetType.Name) + " @ " + Location;
            }
        }

        // 主要功能：一条缓存记录 —— 一个「地址 + 类型」对应一个底层加载操作、一个引用计数，并把完成通知转给句柄。
        // 主要数据成员：Key（缓存键）、m_operation（底层加载操作，释放后置 null）、RefCount（引用计数，≥ 0）、Completed（完成事件）。
        // 主要外部方法：Retain / DisposeOperation / WaitForAsyncComplete，以及只读属性 Status / Progress / Error / AssetObject / IsDone / Location。
        // 谁在用它：AssetManager 建它、缓存它、回收它；AssetHandle 持有它并订阅 Completed；内部类，只有本程序集可见。
        internal sealed class Entry
        {
            private readonly AssetManager m_owner;        // 所属管理器；恒不为 null，只给测试用 OwnerForTest 读
            private IAssetLoadOperation m_operation;      // 底层加载操作；null 表示已释放，读状态一律要兜底

            public event Action<Entry> Completed;         // 完成通知，null 表示无人订阅；底层操作完成时触发一次，释放操作后不再触发

            public readonly AssetKey Key;                 // 缓存键（地址 + 类型），构造后不变

            public int RefCount;                          // 当前引用计数；新建置 1、Retain 加一、Dispose 减一，≥ 0；归 0 后等 ReleaseUnused 回收

            // 功能：记下键、底层操作与所属管理器，并订阅操作的完成通知，好在完成时转给句柄。
            // 入参：key 缓存键；operation 底层加载操作，不能为 null（AssetManager 已判）；owner 所属管理器
            // 返回：无（构造函数）
            // 步骤：
            //   1) 先存三个字段，后面的属性都从它们转发
            //   2) 再订阅 OnComplete —— 要赶在调用方查 IsDone 之前订阅，否则同步完成会漏通知
            // 上游：AssetManager.CreateEntry
            // 下游：无
            // 注意：构造时还没置引用计数，由 CreateEntry 随后置 1；别在这里假设计数已经是 1。
            public Entry(AssetKey key, IAssetLoadOperation operation, AssetManager owner)
            {
                Key = key;
                m_operation = operation;
                m_owner = owner;
                m_operation.OnComplete += OnOperationComplete;
            }

            // 功能：转发缓存键里的地址。
            public string Location
            {
                get { return Key.Location; }
            }

            // 功能：转发底层状态；操作已被释放时按 Failed 处理，避免调用方看到「还在加载」的假象。
            // 注意：操作释放后 Status 会变成 Failed、Error 会变成「底层操作已被释放」，这两个结果都是本类补的，不是加载器报的。
            public AssetStatus Status
            {
                get { return m_operation == null ? AssetStatus.Failed : m_operation.Status; }
            }

            // 功能：转发底层进度，取值 0..1；操作已释放或底层没给进度时为 0。
            public float Progress
            {
                get { return m_operation == null ? 0f : m_operation.Progress; }
            }

            // 功能：转发失败原因；操作已被释放时返回「底层操作已被释放」，底层没给原因时返回空串。
            // 注意：返回值不会是 null，判失败要看 Status，不能靠 Error 是否为空。
            public string Error
            {
                get
                {
                    if (m_operation == null)
                    {
                        return "底层操作已被释放";
                    }

                    string e = m_operation.Error;
                    return e ?? string.Empty;
                }
            }

            // 功能：转发底层加载到的原始资源（不是实例）；未完成、失败或操作已释放时为 null。
            public UnityEngine.Object AssetObject
            {
                get { return m_operation == null ? null : m_operation.AssetObject; }
            }

            // 功能：是否已结束；操作已释放也按已结束算，此时 Status 是 Failed。
            // 注意：成功与失败都算完成，判断加载成功要再看 Status 或 AssetObject。
            public bool IsDone
            {
                get { return m_operation == null || m_operation.IsDone; }
            }

            // 功能：转给底层操作去等，返回时已完成（成功或失败都返回）。
            // 入参：无
            // 返回：无；成败仍要读 Status
            // 步骤：操作还在就转给它等，已被释放就直接返回
            // 上游：编辑器工具、测试
            // 下游：IAssetLoadOperation.WaitForAsyncComplete
            // 注意：只给工具与测试用，Player 里会阻塞主线程。
            public void WaitForAsyncComplete()
            {
                if (m_operation != null)
                {
                    m_operation.WaitForAsyncComplete();
                }
            }

            // 功能：退订完成通知并释放底层操作，再把字段置 null；重复调用安全。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 操作已经是 null 就直接返回，保证重复释放不报错
            //   2) 先退订再 Dispose，避免释放过程中再收到完成通知
            //   3) 最后置 null，之后 Status 走 Failed 分支、资源读出来是 null
            // 上游：AssetManager.DropEntry、AssetManager.UnloadAll
            // 下游：IAssetLoadOperation.Dispose
            // 注意：置 null 之后条目仍可能被句柄持有，所以 Status / Error / Progress 都要按已释放兜底。
            public void DisposeOperation()
            {
                if (m_operation == null)
                {
                    return;
                }

                m_operation.OnComplete -= OnOperationComplete;
                m_operation.Dispose();
                m_operation = null;
            }

            // 功能：引用计数加一，表示又多一个句柄指向同一次加载。
            // 入参：无
            // 返回：无
            // 步骤：直接自增，加完一定大于 0，条目不会被回收
            // 上游：AssetManager.LoadAssetAsync / LoadAsset 命中缓存时
            // 下游：无
            // 注意：这里不做有效性检查，只能在确认条目有效后调；减计数走 AssetManager.Release。
            public void Retain()
            {
                RefCount++;
            }

            // 功能：只给测试读底层操作，业务代码不要用。
            internal IAssetLoadOperation OperationForTest
            {
                get { return m_operation; }
            }

            // 功能：只给测试读所属管理器，业务代码不要用。
            internal AssetManager OwnerForTest
            {
                get { return m_owner; }
            }

            // 功能：把底层完成通知转给订阅了 Completed 的句柄，每份最多一次。
            // 入参：operation 刚完成的底层操作，这里用不到，通知只与条目自身有关
            // 返回：无
            // 步骤：
            //   1) 先把事件字段取到局部变量，避免派发途中有人退订导致遍历失效
            //   2) 有订阅者就同步调用一次，句柄在回调里读 Status 与资源
            // 上游：Entry 构造时订阅的 IAssetLoadOperation.OnComplete
            // 下游：AssetHandle.OnEntryCompleted
            // 注意：同步加载会让回调在调用栈里立刻执行，句柄侧要能处理重入与已释放的情况。
            private void OnOperationComplete(IAssetLoadOperation operation)
            {
                Action<Entry> handlers = Completed;
                if (handlers != null)
                {
                    handlers(this);
                }
            }
        }
    }
}
