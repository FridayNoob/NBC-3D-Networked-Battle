// ============================================================================
//  NBC.Framework.Asset · 资源管理器（框架侧门面）
//  替代：唐老师框架 Res/ResMgr.cs（原 47 行，基于 Resources）
//  需求条目：FW-M05（YooAsset 加载 / 引用计数 / 卸载 / 句柄管理）、FW-M17（适配层隔离）
//  接口来源：Docs/02 ADR-001（已冻结）
//
//  ---------------------------------------------------------------------------
//  原版错在哪（FW-08）
//  ---------------------------------------------------------------------------
//      public T Load<T>(string name) where T : Object
//          => Resources.Load<T>(name);              // :18
//      public void LoadAsync<T>(string name, UnityAction<T> cb) ...
//
//  两个致命缺陷：
//    ① **100% 基于 Resources** —— 内容在打包时被固化进包体，**无法热更**（直接违反需求 5）
//    ② **没有任何释放入口** —— 没有引用计数、没有 Unload，框架层根本无法按需卸载单个资源
//  另外它还有一条**隐式契约**（对 GameObject 自动 Instantiate、其他类型返回共享 asset），
//  6 个调用点全都依赖它。适配层必须把这条契约复刻出来，否则调用点行为全错（Docs/06 §6.2）。
//
//  ---------------------------------------------------------------------------
//  职责边界：谁管什么（这是本文件最重要的一张"分工表"）
//  ---------------------------------------------------------------------------
//    | 谁                 | 负责                                                    |
//    | AssetManager（本类）| 缓存、**引用计数**、句柄生命周期、调试统计、失败重试      |
//    | IAssetProvider      | 只负责"按地址把资源捞出来"、"卸载没人用的"                |
//    | YooAssetProvider    | 把 YooAsset v3 的句柄翻译成 IAssetLoadOperation            |
//
//  为什么引用计数、缓存放在**框架侧**而不是适配层：
//    ① 它们与"用哪个资源库"无关，是**资源层的共性逻辑**；
//    ② 放这里就能用**假加载器**在 EditMode 里完整测试 ——
//       不需要 YooAsset、不需要打包、结果完全确定（这是 B2 可验证的关键）。
//
//  ---------------------------------------------------------------------------
//  两级释放（ADR-001 决策 1 的引用计数语义）
//  ---------------------------------------------------------------------------
//      handle.Dispose()          -> 引用计数减一。**不立刻卸载**。
//      ReleaseUnused()           -> 把计数已经为 0 的条目真正释放掉。
//      UnloadUnusedAssets()      -> 再让底层（YooAsset）去做引擎级卸载。
//
//  为什么 Dispose 不立刻卸载：`放下又马上要用` 是很常见的模式
//  （例如关掉一个面板又立刻打开）。立刻卸载会让这种场景反复加载，反而更慢。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NBC.Framework.Asset
{
    /// <summary>
    /// 资源管理器。业务层只跟它打交道，不接触任何具体资源库。
    /// </summary>
    public sealed class AssetManager : Singleton<AssetManager>
    {
        /// <summary>底层加载器。为 null 表示还没装上（未初始化）。</summary>
        private IAssetProvider m_provider;

        /// <summary>按（地址 + 类型）索引的缓存。</summary>
        private readonly Dictionary<AssetKey, Entry> m_cache = new Dictionary<AssetKey, Entry>();

        /// <summary>全部条目，用于遍历回收（字典在遍历时不能改，所以另存一份列表）。</summary>
        private readonly List<Entry> m_entries = new List<Entry>();

        /// <summary>回收时复用的临时列表，避免每次分配。</summary>
        private readonly List<Entry> m_recycleBuffer = new List<Entry>();

        /// <summary>底层加载器是否已装上。</summary>
        public bool HasProvider
        {
            get { return m_provider != null; }
        }

        /// <summary>是否已完成初始化。</summary>
        public bool IsInitialized
        {
            get { return m_provider != null && m_provider.IsInitialized; }
        }

        // ====================================================================
        //  初始化
        // ====================================================================

        /// <summary>
        /// 装上底层加载器。
        /// <para>
        /// 由**引导代码**调用（例如 <c>AssetManager.Instance.SetProvider(new YooAssetProvider())</c>）。
        /// 之所以是"装"而不是"框架自己 new"：框架**不认识** YooAsset，
        /// 依赖方向必须是 适配层 -> 框架（依赖倒置），不能反过来。
        /// </para>
        /// </summary>
        /// <param name="provider">底层加载器。</param>
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

        /// <summary>
        /// 初始化资源层（建包、加载清单等）。
        /// </summary>
        /// <param name="mode">运行模式。</param>
        public async Task InitializeAsync(AssetRuntimeMode mode)
        {
            ThrowIfNoProvider();
            await m_provider.InitializeAsync(mode);
        }

        // ====================================================================
        //  加载
        // ====================================================================

        /// <summary>
        /// 异步加载一个资源。同一个地址 + 类型重复调用会**复用缓存并累加引用计数**。
        /// </summary>
        /// <typeparam name="T">资源类型。</typeparam>
        /// <param name="location">资源地址。</param>
        /// <returns>资源句柄。用完请 <c>Dispose()</c>。</returns>
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

        /// <summary>
        /// 同步加载一个资源。
        /// <para>
        /// ⚠️ **受限接口（ADR-001 决策 5）**：底层资源库的同步加载会**阻塞主线程**，
        /// 在 Player 里对大资源使用就是卡顿来源。播放模式下调用会打一条警告。
        /// 只应在编辑器工具、或极小的资源上使用。
        /// </para>
        /// </summary>
        /// <typeparam name="T">资源类型。</typeparam>
        /// <param name="location">资源地址。</param>
        /// <returns>资源句柄。</returns>
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

        // ====================================================================
        //  释放与观测
        // ====================================================================

        /// <summary>
        /// 释放引用计数已经归零的缓存条目。**不触发引擎级 GC**（那是 <see cref="UnloadUnusedAssets"/> 的事）。
        /// </summary>
        /// <returns>本次真正释放掉的条目数。</returns>
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

        /// <summary>让底层资源库卸载已经没人用的资源（引擎级）。</summary>
        public void UnloadUnusedAssets()
        {
            ReleaseUnused();

            if (m_provider != null)
            {
                m_provider.UnloadUnused();
            }
        }

        /// <summary>
        /// 清空全部缓存并让底层卸载所有资源。
        /// 用途：切场景 / 回大厅这类"整场收尾"。
        /// </summary>
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

        /// <summary>取观测快照（FW-M12 面板用）。</summary>
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

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>句柄 Dispose 时回调进来：只减计数，不立刻卸载。</summary>
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

        /// <summary>建立一个条目：立刻计一次引用，并启动底层加载。</summary>
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

        /// <summary>丢弃一个条目：从缓存移除并释放底层操作。</summary>
        private void DropEntry(Entry entry)
        {
            entry.DisposeOperation();
            m_cache.Remove(entry.Key);
            m_entries.Remove(entry);
        }

        private void ThrowIfNoProvider()
        {
            if (m_provider == null)
            {
                throw new InvalidOperationException(
                    "[AssetManager] 还没有装上底层加载器。请先调用 " +
                    "AssetManager.Instance.SetProvider(...) 再使用（见 Docs/02 ADR-001 决策 6）。");
            }
        }

        /// <summary>
        /// 加载前的检查：**没装加载器 / 没初始化就加载 = 引导流程写错了**，当场报错。
        /// 与其让它走到后面变成一个"资源加载为空"的怪现象，不如在这里就说清楚。
        /// </summary>
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

        private static void ThrowIfBadLocation(string location)
        {
            if (string.IsNullOrEmpty(location))
            {
                throw new ArgumentException("[AssetManager] 资源地址不能为空。", nameof(location));
            }
        }

        /// <summary>缓存键：地址 + 类型。同一个地址可以按不同类型加载，所以要带上类型。</summary>
        internal readonly struct AssetKey : IEquatable<AssetKey>
        {
            /// <summary>资源地址。</summary>
            public readonly string Location;

            /// <summary>资源类型。</summary>
            public readonly Type AssetType;

            /// <summary>构造键。</summary>
            public AssetKey(string location, Type assetType)
            {
                Location = location;
                AssetType = assetType;
            }

            /// <summary>按地址与类型比较。</summary>
            public bool Equals(AssetKey other)
            {
                return string.Equals(Location, other.Location, StringComparison.Ordinal)
                       && AssetType == other.AssetType;
            }

            /// <summary>按地址与类型比较。</summary>
            public override bool Equals(object obj)
            {
                return obj is AssetKey && Equals((AssetKey)obj);
            }

            /// <summary>组合哈希。</summary>
            public override int GetHashCode()
            {
                int h = Location == null ? 0 : Location.GetHashCode();
                return (h * 397) ^ (AssetType == null ? 0 : AssetType.GetHashCode());
            }

            /// <summary>调试用文本。</summary>
            public override string ToString()
            {
                return (AssetType == null ? "?" : AssetType.Name) + " @ " + Location;
            }
        }

        /// <summary>
        /// 一条缓存记录：一个（地址 + 类型）对应一个底层加载操作 + 一个引用计数。
        /// </summary>
        internal sealed class Entry
        {
            private readonly AssetManager m_owner;
            private IAssetLoadOperation m_operation;

            /// <summary>完成通知（句柄会订阅）。</summary>
            public event Action<Entry> Completed;

            /// <summary>缓存键。</summary>
            public readonly AssetKey Key;

            /// <summary>当前引用计数。</summary>
            public int RefCount;

            /// <summary>构造条目。</summary>
            public Entry(AssetKey key, IAssetLoadOperation operation, AssetManager owner)
            {
                Key = key;
                m_operation = operation;
                m_owner = owner;
                m_operation.OnComplete += OnOperationComplete;
            }

            /// <summary>资源地址。</summary>
            public string Location
            {
                get { return Key.Location; }
            }

            /// <summary>当前状态。操作已被释放时视为失败，避免调用方拿到"看起来还在加载"的假象。</summary>
            public AssetStatus Status
            {
                get { return m_operation == null ? AssetStatus.Failed : m_operation.Status; }
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
                        return "底层操作已被释放";
                    }

                    string e = m_operation.Error;
                    return e ?? string.Empty;
                }
            }

            /// <summary>原始资源。</summary>
            public UnityEngine.Object AssetObject
            {
                get { return m_operation == null ? null : m_operation.AssetObject; }
            }

            /// <summary>是否已完成。</summary>
            public bool IsDone
            {
                get { return m_operation == null || m_operation.IsDone; }
            }

            /// <summary>阻塞直到完成。</summary>
            public void WaitForAsyncComplete()
            {
                if (m_operation != null)
                {
                    m_operation.WaitForAsyncComplete();
                }
            }

            /// <summary>释放底层操作。</summary>
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

            /// <summary>增加一次引用。</summary>
            public void Retain()
            {
                RefCount++;
            }

            /// <summary>测试用：暴露内部操作。</summary>
            internal IAssetLoadOperation OperationForTest
            {
                get { return m_operation; }
            }

            /// <summary>测试用：暴露所属管理器。</summary>
            internal AssetManager OwnerForTest
            {
                get { return m_owner; }
            }

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
