// ============================================================================
//  ConfigMgr —— 运行时配置表查询入口
//  项目：3D联网战斗Demo   对应：CFG-R1（统一入口）、CFG-R2（走 AB）、CFG-R4（缺表报错友好）
//
//  ---------------------------------------------------------------------------
//  职责边界（一句话）
//  ---------------------------------------------------------------------------
//      **管缓存 + 提供查询**；"怎么把资产读进来"交给 `IConfigSource`（见那个文件的说明）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 三条写死的语义（避免猜）
//  ---------------------------------------------------------------------------
//  ① **`Get<T>()` 是同步的**：配置在启动时**预加载**进内存，之后查表不再碰 IO。
//     理由：配置表小（几百行）、查得极频繁（战斗结算里每帧都可能查），
//     让每次查询都异步既没收益、又会把调用方代码撕成回调链。
//
//  ② **同一个表名同时只有一条加载任务**：第二次请求**挂到同一条任务上**，
//     而不是再加载一次。（这条语义是从 A9 的 `UIManager` 抄来的，
//     那边踩过"同帧连点两下 = 加载两遍 + 重复键异常"。）
//
//  ③ **行查询不在这一层做**：生成的 `<表>Config` 自己带 `Get(id)` / `TryGet(id, out row)`
//     与主键索引（ConfigKit 生成）。所以这里的用法是：
//         ConfigMgr.Instance.Get<HeroConfig>().Get(1001)
//     —— **不需要反射、也不需要额外接口**，而且类型安全。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 缺表报错必须"友好"（CFG-R4 的点名要求）
//  ---------------------------------------------------------------------------
//  "找不到配置"这类错误最常见的原因是"**忘了预加载**"，而不是"表真的不存在"。
//  所以报错要说清三件事：**哪张表**、**可能是什么原因**、**怎么解决**。
//  这和 A9 那条"让错误自己说出来"是同一条原则。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using NBC.Framework;
using UnityEngine;

namespace NBC.Game.Config
{
    /// <summary>运行时配置表查询入口（**单例**）。</summary>
    public sealed class ConfigMgr : Singleton<ConfigMgr>
    {
        /// <summary>生成的资产类型名的后缀（`HeroConfig` ↔ 表 `Hero`）。</summary>
        public const string AssetTypeSuffix = "Config";

        /// <summary>已加载的表：表名 → 资产。</summary>
        private readonly Dictionary<string, ScriptableObject> m_loaded =
            new Dictionary<string, ScriptableObject>(StringComparer.Ordinal);

        /// <summary>正在加载的表：表名 → 那一条任务（语义②）。</summary>
        private readonly Dictionary<string, PendingLoad> m_pending =
            new Dictionary<string, PendingLoad>(StringComparer.Ordinal);

        /// <summary>资产来源。默认没有 —— 必须由启动流程或测试塞进来。</summary>
        private IConfigSource m_source;

        /// <summary>来源描述（打印日志用）。</summary>
        public IConfigSource Source
        {
            get { return m_source; }
        }

        /// <summary>已经加载了几张表。</summary>
        public int LoadedTableCount
        {
            get { return m_loaded.Count; }
        }

        /// <summary>一次"正在加载中"的请求（语义②：同名的后续请求挂到这一条上）。</summary>
        private sealed class PendingLoad
        {
            /// <summary>成功回调。</summary>
            public readonly List<Action<ScriptableObject>> OnSuccess = new List<Action<ScriptableObject>>();

            /// <summary>失败回调。</summary>
            public readonly List<Action<string>> OnFailure = new List<Action<string>>();
        }

        /// <summary>设置资产来源（启动流程在做任何查询之前调用一次）。</summary>
        /// <param name="source">来源；传 null 表示清空（测试收尾用）。</param>
        public void SetSource(IConfigSource source)
        {
            m_source = source;
        }

        /// <summary>表名转生成的资产类型名（`Hero` → `HeroConfig`）。</summary>
        /// <param name="tableName">表名。</param>
        /// <returns>资产类型名。</returns>
        public static string AssetNameOf(string tableName)
        {
            return tableName + AssetTypeSuffix;
        }

        /// <summary>
        /// 泛型类型转表名（`HeroConfig` → `Hero`）。
        /// <para>⚠️ 按**命名约定**推，不做反射 —— 名字不符就当场报错，而不是猜。</para>
        /// </summary>
        /// <typeparam name="T">生成的配置资产类型。</typeparam>
        /// <returns>表名。</returns>
        public static string TableNameOf<T>() where T : ScriptableObject
        {
            string typeName = typeof(T).Name;

            if (!typeName.EndsWith(AssetTypeSuffix, StringComparison.Ordinal) ||
                typeName.Length == AssetTypeSuffix.Length)
            {
                throw new ArgumentException(
                    "[ConfigMgr] 类型名必须是 `<表名>" + AssetTypeSuffix + "` 的形状（例如 HeroConfig ↔ 表 Hero），" +
                    "实际是 `" + typeName + "`。\n" +
                    "生成的资产类型由 ConfigKit 按这个约定命名；手写的类型不属于配置表。");
            }

            return typeName.Substring(0, typeName.Length - AssetTypeSuffix.Length);
        }

        // ====================================================================
        //  预加载
        // ====================================================================

        /// <summary>预加载一张表（按类型）。</summary>
        /// <typeparam name="T">生成的配置资产类型。</typeparam>
        /// <param name="onDone">成功回调（可空）。</param>
        /// <param name="onFailed">失败回调（可空）；参数是一句人话原因。</param>
        public void Preload<T>(Action onDone = null, Action<string> onFailed = null) where T : ScriptableObject
        {
            Preload(TableNameOf<T>(),
                asset => { if (onDone != null) { onDone(); } },
                onFailed);
        }

        /// <summary>预加载一张表（按表名）。</summary>
        /// <param name="tableName">表名。</param>
        /// <param name="onLoaded">成功回调（可空）。</param>
        /// <param name="onFailed">失败回调（可空）。</param>
        public void Preload(string tableName, Action<ScriptableObject> onLoaded = null,
                            Action<string> onFailed = null)
        {
            if (string.IsNullOrEmpty(tableName))
            {
                Fail(onFailed, "[ConfigMgr] 表名是空的。");
                return;
            }

            // 已经加载过：直接回调（**不重复加载**）
            ScriptableObject cached;

            if (m_loaded.TryGetValue(tableName, out cached))
            {
                if (onLoaded != null)
                {
                    onLoaded(cached);
                }

                return;
            }

            PendingLoad pending;

            if (m_pending.TryGetValue(tableName, out pending))
            {
                // 语义②：挂到已有任务上
                if (onLoaded != null)
                {
                    pending.OnSuccess.Add(onLoaded);
                }

                if (onFailed != null)
                {
                    pending.OnFailure.Add(onFailed);
                }

                return;
            }

            if (m_source == null)
            {
                Fail(onFailed, NoSourceMessage(tableName));
                return;
            }

            pending = new PendingLoad();

            if (onLoaded != null)
            {
                pending.OnSuccess.Add(onLoaded);
            }

            if (onFailed != null)
            {
                pending.OnFailure.Add(onFailed);
            }

            m_pending.Add(tableName, pending);

            m_source.LoadTable(
                tableName,
                asset => CompleteLoad(tableName, asset),
                reason => FailLoad(tableName, reason));
        }

        /// <summary>把若干张表一次性预加载完（全部成功才算成功）。</summary>
        /// <param name="tableNames">表名。</param>
        /// <param name="onDone">全部成功回调（可空）。</param>
        /// <param name="onFailed">任一失败回调（可空）。</param>
        public void PreloadAll(IReadOnlyList<string> tableNames, Action onDone = null,
                               Action<string> onFailed = null)
        {
            if (tableNames == null || tableNames.Count == 0)
            {
                if (onDone != null)
                {
                    onDone();
                }

                return;
            }

            // 逐张串行；任一张失败就停（那条错误已经通过 onFailed 交出去了）
            PreloadSequential(tableNames, 0, onDone, onFailed);
        }

        /// <summary>
        /// 逐张**串行**预加载（前一张成功再下一张；任一张失败就停）。
        /// <para>
        /// ⚠️ 写法上只有一个要点：**回调可能是同步发生的**（缓存命中、或假来源），
        /// 所以"继续下一张"必须写在成功回调里 —— 同步来源会当场递归下去
        /// （表只有几张，递归深度无所谓），异步来源则在回来时继续。
        /// **不要**试图用"先调一次看有没有完成、再调一次等回调"那种两段式写法：
        /// 那会让同一张表被请求两次，语义②（同名只有一条任务）就白设了。
        /// </para>
        /// </summary>
        private void PreloadSequential(IReadOnlyList<string> tableNames, int index,
                                      Action onDone, Action<string> onFailed)
        {
            if (index >= tableNames.Count)
            {
                if (onDone != null)
                {
                    onDone();
                }

                return;
            }

            Preload(tableNames[index],
                asset => PreloadSequential(tableNames, index + 1, onDone, onFailed),
                onFailed);
        }

        // ====================================================================
        //  查询
        // ====================================================================

        /// <summary>取一张已加载的表（取不到**抛异常**，消息里说清原因与解法）。</summary>
        /// <typeparam name="T">生成的配置资产类型。</typeparam>
        /// <returns>资产。</returns>
        public T Get<T>() where T : ScriptableObject
        {
            string tableName = TableNameOf<T>();

            ScriptableObject cached;

            if (m_loaded.TryGetValue(tableName, out cached))
            {
                return (T)(object)cached;
            }

            throw new InvalidOperationException(
                MissingTableMessage(tableName, typeof(T).Name, typeof(T).FullName));
        }

        /// <summary>试着取一张已加载的表（**不抛异常**）。</summary>
        /// <typeparam name="T">生成的配置资产类型。</typeparam>
        /// <param name="table">资产。</param>
        /// <returns>取到没有。</returns>
        public bool TryGet<T>(out T table) where T : ScriptableObject
        {
            table = null;

            ScriptableObject cached;

            if (!m_loaded.TryGetValue(TableNameOf<T>(), out cached))
            {
                return false;
            }

            table = (T)(object)cached;
            return true;
        }

        /// <summary>某张表加载好了没有。</summary>
        /// <param name="tableName">表名。</param>
        /// <returns>加载好了吗。</returns>
        public bool IsLoaded(string tableName)
        {
            return !string.IsNullOrEmpty(tableName) && m_loaded.ContainsKey(tableName);
        }

        /// <summary>正在加载中吗。</summary>
        /// <param name="tableName">表名。</param>
        /// <returns>在加载吗。</returns>
        public bool IsLoading(string tableName)
        {
            return !string.IsNullOrEmpty(tableName) && m_pending.ContainsKey(tableName);
        }

        /// <summary>卸掉一张表（**测试与切场景收尾用**）。</summary>
        /// <param name="tableName">表名。</param>
        /// <returns>真的卸掉了没有。</returns>
        public bool Unload(string tableName)
        {
            return !string.IsNullOrEmpty(tableName) && m_loaded.Remove(tableName);
        }

        /// <summary>清空全部缓存（**测试用**；真实的资产卸载要等 B4/B5 的句柄释放）。</summary>
        public void Clear()
        {
            m_loaded.Clear();
            m_pending.Clear();
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>一次加载成功。</summary>
        private void CompleteLoad(string tableName, ScriptableObject asset)
        {
            PendingLoad pending;
            m_pending.TryGetValue(tableName, out pending);

            if (asset == null)
            {
                FailLoad(tableName, "来源回调说成功，但给了一个 null 资产（表 " + tableName + "）。");
                return;
            }

            m_loaded[tableName] = asset;

            if (pending != null)
            {
                m_pending.Remove(tableName);

                for (int i = 0; i < pending.OnSuccess.Count; i++)
                {
                    pending.OnSuccess[i](asset);
                }
            }
        }

        /// <summary>一次加载失败。</summary>
        private void FailLoad(string tableName, string reason)
        {
            PendingLoad pending;
            m_pending.TryGetValue(tableName, out pending);

            if (pending != null)
            {
                m_pending.Remove(tableName);
            }

            string message = "[ConfigMgr] 加载表「" + tableName + "」失败：" + reason;

            if (pending != null && pending.OnFailure.Count > 0)
            {
                for (int i = 0; i < pending.OnFailure.Count; i++)
                {
                    pending.OnFailure[i](message);
                }

                return;
            }

            // 没人接失败回调：**不能静默吞掉**（否则就是一次无声的加载失败）
            Debug.LogError(message + "\n（这次加载没有任何失败回调，所以它只出现在这里。）");
        }

        /// <summary>立刻报一个失败（还没进入加载流程的那些前置错误）。</summary>
        private static void Fail(Action<string> onFailed, string message)
        {
            if (onFailed != null)
            {
                onFailed(message);
                return;
            }

            Debug.LogError(message);
        }

        /// <summary>没设置来源时的报错（**要说清怎么解决**）。</summary>
        private static string NoSourceMessage(string tableName)
        {
            return "[ConfigMgr] 还没设置配置来源（IConfigSource），所以无法加载表「" + tableName + "」。\n" +
                   "启动流程里应当先调用：ConfigMgr.Instance.SetSource(new XxxConfigSource());\n" +
                   "（测试里塞一个假来源即可，不需要任何真实资产。）";
        }

        /// <summary>缺表时的报错：**哪张表 + 可能原因 + 怎么解决**（CFG-R4）。</summary>
        /// <param name="tableName">表名。</param>
        /// <param name="typeName">类型的**简单名**（写进"该怎么改"的代码片段里）。</param>
        /// <param name="fullTypeName">类型的完整名（消歧义用，可能带命名空间 / 嵌套）。</param>
        private static string MissingTableMessage(string tableName, string typeName, string fullTypeName)
        {
            StringBuilder builder = new StringBuilder();

            builder.Append("[ConfigMgr] 表「").Append(tableName).Append("」还没加载（").Append(fullTypeName).Append("）。\n");
            builder.Append("已加载的表：");

            if (Instance.m_loaded.Count == 0)
            {
                builder.Append("（一张都没有）");
            }
            else
            {
                bool first = true;

                foreach (KeyValuePair<string, ScriptableObject> pair in Instance.m_loaded)
                {
                    if (!first)
                    {
                        builder.Append("、");
                    }

                    first = false;
                    builder.Append(pair.Key);
                }
            }

            builder.Append("\n最常见的两个原因：\n");

            // ⚠️ 代码片段里用**简单名**（`Preload<HeroConfig>()`）而不是 FullName：
            //    FullName 对嵌套类型是 `Outer+Nested`，既难看又不能直接粘进代码。
            //    （这条是测试当场抓出来的：我原先用了 FullName，消息里出现了 `+HeroConfig`。）
            builder.Append("  ① **忘了预加载** —— 在启动流程里调用 ConfigMgr.Instance.Preload<")
                   .Append(typeName).Append(">()\n");
            builder.Append("  ② 预加载失败了但没接失败回调（那种情况 Console 里会有一条 LogError）\n");
            builder.Append("查表用法：ConfigMgr.Instance.Get<").Append(typeName).Append(">().Get(id)");

            return builder.ToString();
        }
    }
}
