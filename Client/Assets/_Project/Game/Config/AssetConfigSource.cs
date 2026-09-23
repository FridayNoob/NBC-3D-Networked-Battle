// ============================================================================
//  AssetConfigSource —— `IConfigSource` 的**真实现**：从资源层取配置表
//  项目：3D联网战斗Demo   对应：M2-C1、CFG-R2（走内容包而不是 Resources）
//
//  ---------------------------------------------------------------------------
//  它是 M1-C4 留的那道缝的**兑现**
//  ---------------------------------------------------------------------------
//  M1-C4 里 `ConfigMgr` 只定义了接缝（`IConfigSource`），并明确写了：
//
//      "真正的实现（走资源层）等打包做完再接，**一行都不用改 ConfigMgr**"
//
//  这个文件就是那次兑现。**`ConfigMgr` 确实一行都没改**（可以 `git log` 对）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 它为什么住在 `NBC.Game`，而不是适配层
//  ---------------------------------------------------------------------------
//  因为它**完全不认识第三方**：它只用框架的 `AssetManager` 接缝
//  （`LoadAssetAsync<T>` / `OnComplete` / `IsValid`）。
//  "第三方"（内容包的实现）在 `NBC.Framework.YooAsset` 里，由组合根 `NBC.Boot` 装上去。
//  三层各管一段，谁也不越界：
//
//      NBC.Boot          装底层加载器（唯一认识实现的地方）
//      NBC.Game（本文件） 把"表名"翻译成"资源地址"，交给 AssetManager
//      ConfigMgr         管缓存 + 提供查询（**从头到尾不知道资源系统存在**）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 三个刻意的选择
//  ---------------------------------------------------------------------------
//  ① **用异步而不是同步**：`AssetManager.LoadAsset<T>` 是**受限接口**
//     （ADR-001 决策 5：同步会阻塞主线程，播放模式下还会打警告）。
//     配置表虽然小，但"启动时一次加载好几张"正是最不该阻塞的那一段，
//     而 `IConfigSource` 本来就是回调式的 —— 用异步**零成本**。
//
//  ② **句柄一直持有、不 Dispose**：配置表在进程生命周期内常驻，
//     释放句柄 = 让资源系统把它卸掉。这是**有意为之**，不是漏了；
//     将来若要做"配置热重载"，那时才需要一条真正的释放路径（M2 明确不做）。
//
//  ③ **判成功用 `IsValid`，不看句柄是不是 null**：句柄非 null 但资产是 null
//     是**真实会发生的**（M1-B5 我就把这种"假成功"报成过 ✅）。
//     `IsValid` = `Status == Succeeded && AssetObject != null` ——
//     框架已经把这个教训写进属性里了，这里直接用。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework.Asset;
using UnityEngine;

namespace NBC.Game.Config
{
    /// <summary>从资源层（`AssetManager`）取配置表的来源实现。</summary>
    public sealed class AssetConfigSource : IConfigSource
    {
        /// <summary>地址前缀/后缀用的资产类型后缀（`Hero` → `HeroConfig`）。</summary>
        private readonly string m_addressSuffix;

        /// <summary>**常驻**的句柄（见文件头选择②：配置表不卸载）。</summary>
        private readonly List<AssetHandle<ScriptableObject>> m_handles = new List<AssetHandle<ScriptableObject>>();

        /// <summary>造一个来源。</summary>
        /// <param name="addressSuffix">
        /// 资源地址的后缀，默认与 `ConfigMgr` 的命名约定一致（`Hero` → `HeroConfig`）。
        /// 收集配置里的地址规则若改过，把它一起改。
        /// </param>
        public AssetConfigSource(string addressSuffix = ConfigMgr.AssetTypeSuffix)
        {
            m_addressSuffix = string.IsNullOrEmpty(addressSuffix) ? ConfigMgr.AssetTypeSuffix : addressSuffix;
        }

        /// <summary>来源描述（报错与日志里用）。</summary>
        public string Description
        {
            get { return "资源层（AssetManager）"; }
        }

        /// <summary>已经持有了几个句柄（测试/调试用）。</summary>
        public int HeldHandleCount
        {
            get { return m_handles.Count; }
        }

        /// <summary>表名 → 资源地址（`Hero` → `HeroConfig`）。</summary>
        /// <param name="tableName">表名。</param>
        /// <returns>资源地址。</returns>
        public string AddressOf(string tableName)
        {
            return tableName + m_addressSuffix;
        }

        /// <summary>
        /// 加载一张表的配置资产。
        /// <para>⚠️ 成功与失败**都会**回调，且只在主线程回调一次（接口契约）。</para>
        /// </summary>
        /// <param name="tableName">表名（如 `Hero`）。</param>
        /// <param name="onLoaded">成功：交出资产。</param>
        /// <param name="onFailed">失败：交出一句人话原因。</param>
        public void LoadTable(string tableName, Action<ScriptableObject> onLoaded, Action<string> onFailed)
        {
            string address = AddressOf(tableName);

            // 先看底层加载器装了没有：没装的话，后面拿到的错误会**看起来像"表不存在"**，
            // 而真实原因是"组合根没跑"。这两件事的排查方向完全相反，所以要在这里分开。
            if (!AssetManager.Instance.HasProvider)
            {
                Fail(onFailed,
                    "[AssetConfigSource] 资源层还没装底层加载器（`IAssetProvider`），所以读不了配置表「" +
                    tableName + "」。\n" +
                    "启动流程里应当先经组合根装配：AssetBootstrapper.InstallAsync(mode)。\n" +
                    "（这条报错以前是\"表不存在\"，方向完全跑偏 —— 现在先把它分开。）");
                return;
            }

            AssetHandle<ScriptableObject> handle =
                AssetManager.Instance.LoadAssetAsync<ScriptableObject>(address);

            if (handle == null)
            {
                Fail(onFailed,
                    "[AssetConfigSource] 资源层对地址「" + address + "」返回了 null 句柄（表「" + tableName + "」）。");
                return;
            }

            // ⚠️ `OnComplete` 在**已经完成**时会立刻回调一次，所以这里没有
            //    "加载太快、回调注册太晚"的竞态（框架在属性 setter 里保证了，见 AssetHandle）。
            handle.OnComplete += completed =>
            {
                // ⚠️ 判成功用 `IsValid`（= 成功 且 资产非 null），**不看句柄是不是 null**：
                //    句柄非 null 但资产是 null 是真会发生的（类型不对、包里没这个资源）。
                if (!completed.IsValid)
                {
                    completed.Dispose();
                    Fail(onFailed, TableLoadFailedMessage(tableName, address, completed.Error));
                    return;
                }

                m_handles.Add(completed);
                onLoaded(completed.Asset);
            };
        }

        /// <summary>表加载失败的报错：**哪张表 + 地址 + 三个常见原因 + 怎么查**。</summary>
        /// <param name="tableName">表名。</param>
        /// <param name="address">资源地址。</param>
        /// <param name="reason">底层给的原因（可能为空）。</param>
        /// <returns>报错文本。</returns>
        private static string TableLoadFailedMessage(string tableName, string address, string reason)
        {
            return "[AssetConfigSource] 读配置表「" + tableName + "」失败（地址「" + address + "」）。\n" +
                   (string.IsNullOrEmpty(reason) ? string.Empty : "底层原因：" + reason + "\n") +
                   "四个常见原因（**从最可能的开始看**）：\n" +
                   "  ① **资产没被打进包 / 模拟清单里没有它** —— 尤其是**新加的表**：" +
                   "M2 加了 Monster/Quest/QuestCondition/Reward 四张，旧的包与旧的模拟清单里都没有它们。\n" +
                   "      · 用 EditorSimulate：`YooAsset → Bundle Builder` → Pipeline 选 " +
                   "`EditorSimulateBuildPipeline` → 绿色 `Click Build`\n" +
                   "      · 用 Offline：Pipeline 选 `ScriptableBuildPipeline` → 构建一次" +
                   "（见 Docs\\18 §七）\n" +
                   "  ② **还没把表导入成资产** —— 点一次 `Tools/NBC/配置表/导入 TSV → ScriptableObject`\n" +
                   "  ③ **收集配置没包含它** —— `BundleCollectorSetting` 里 `config` 组要收 `Assets/_Project/Configs`\n" +
                   "  ④ **地址规则对不上** —— 地址是表名 + 后缀（`" + address + "`）；" +
                   "收集器若用了别的地址规则，改 `AssetConfigSource` 的构造参数";
        }

        /// <summary>没人接失败回调时**不能静默吞掉**（否则就是一次无声的加载失败）。</summary>
        /// <param name="onFailed">失败回调。</param>
        /// <param name="message">原因。</param>
        private static void Fail(Action<string> onFailed, string message)
        {
            if (onFailed != null)
            {
                onFailed(message);
                return;
            }

            Debug.LogError(message + "\n（这次加载没有任何失败回调，所以它只出现在这里。）");
        }
    }
}
