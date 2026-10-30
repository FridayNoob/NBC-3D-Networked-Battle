// 这个文件里有：
//   AssetConfigSource（类）：IConfigSource 的真实实现，把表名翻成资源地址后经 AssetManager 异步取回配置表，并常驻持有句柄

using System;
using System.Collections.Generic;
using NBC.Framework.Asset;
using UnityEngine;

namespace NBC.Game.Config
{
    /// <summary>实现 IConfigSource：表名按约定后缀拼成资源地址，从 AssetManager 异步取回配置资产。</summary>
    // 主要功能：表名拼上后缀得到资源地址，交给 AssetManager 异步加载成 ScriptableObject，并把句柄常驻持有。
    // 主要数据成员：m_addressSuffix（地址后缀，默认 Config）、m_handles（加载成功后不释放的句柄列表）。
    // 主要外部方法：LoadTable（异步取表，成功与失败必回调其一）、AddressOf（表名转地址）；Description 与 HeldHandleCount 只给报错和调试。
    // 谁在用它：Boot 的 M2DemoBehaviour 装好资源层后 new 出来交给 ConfigMgr.SetSource，之后 ConfigMgr 只经 IConfigSource 调用。
    // 注意：只认框架的 AssetManager 接缝（LoadAssetAsync / OnComplete / IsValid），不认识内容包实现 —— 那层在 YooAsset 里、由 Boot 装上，
    //       所以它住在 Game 而不是适配层：Boot 装加载器、本文件翻地址、ConfigMgr 管缓存，谁也不越界；配置表因此走内容包而不走 Resources。
    // 注意：只能走异步 —— AssetManager.LoadAsset 是受限接口，同步会阻塞主线程，播放模式下还会打警告。
    public sealed class AssetConfigSource : IConfigSource
    {
        // 地址后缀，追加在表名后面（默认 Config，Hero 表 → HeroConfig 地址）；取值为非空字符串，无单位
        private readonly string m_addressSuffix;

        // 已加载且刻意不释放的资产句柄，长度 = 成功加载过的表数，只增不减；无单位
        private readonly List<AssetHandle<ScriptableObject>> m_handles = new List<AssetHandle<ScriptableObject>>();

        /// <summary>创建来源；Boot 用无参构造即可，只有地址规则改过才需要传后缀。</summary>
        // 功能：保存地址后缀，传空时回落到 ConfigMgr.AssetTypeSuffix。
        // 注意：收集器的地址规则若改过，这个后缀要跟着改，否则地址对不上、加载全失败。
        public AssetConfigSource(string addressSuffix = ConfigMgr.AssetTypeSuffix)
        {
            m_addressSuffix = string.IsNullOrEmpty(addressSuffix) ? ConfigMgr.AssetTypeSuffix : addressSuffix;
        }

        // 功能：来源描述，报错与日志里用来说明表是从资源层读的。
        public string Description
        {
            get { return "资源层（AssetManager）"; }
        }

        // 功能：当前持有的句柄数，测试与调试用来确认加载成功后句柄确实留在列表里。
        public int HeldHandleCount
        {
            get { return m_handles.Count; }
        }

        // 功能：按「表名 + 后缀」拼出资源地址（Hero → HeroConfig），不检查资源是否存在。
        public string AddressOf(string tableName)
        {
            return tableName + m_addressSuffix;
        }

        // 功能：把表名翻成地址后异步加载配置资产，成功交出资产，失败交出一句人话原因。
        // 入参：tableName 表名（如 Hero，不带 Config 后缀）；onLoaded 成功回调，交出资产；
        //       onFailed 失败回调，交出可直接展示的原因（为 null 时打到日志）
        // 步骤：
        //   1) 先拼地址：后面报错与加载都用这一个地址，避免两处拼法不一致
        //   2) 先查 HasProvider：没装底层加载器时底层会把原因报成「表不存在」，排查方向完全相反，所以在这里拦下并点明是组合根没跑
        //   3) 异步发起加载并立刻取句柄；句柄为 null 直接算失败，不再注册回调
        //   4) 注册 OnComplete：它在已经完成时会立刻回调一次，所以没有「加载太快、注册太晚」的竞态
        //   5) 用 IsValid（成功且资产非 null）判成功，不看句柄是不是 null —— 句柄非 null 而资产为 null 是真会发生的（类型不对、包里没这个资源）
        //   6) 判失败先 Dispose 再回调，只有成功才把句柄存进 m_handles 常驻
        // 返回：无返回值；成功与失败都会回调，且只在主线程回调一次（接口契约），调用方不用自己判空等
        // 注意：句柄刻意不 Dispose —— 配置表整进程常驻，释放等于让资源系统把表卸掉；将来做热重载才需要真正的释放路径。
        // 上游：ConfigMgr 经 IConfigSource 的加载流程（Boot 预加载那一路）、EditMode 测试直接调
        // 下游：AddressOf、AssetManager.Instance、Fail、TableLoadFailedMessage
        public void LoadTable(string tableName, Action<ScriptableObject> onLoaded, Action<string> onFailed)
        {
            string address = AddressOf(tableName);

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

            handle.OnComplete += completed =>
            {
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

        // 功能：拼出表加载失败的报错，含表名、地址、底层原因与四个常见原因的排查方法。
        // 入参：tableName 表名；address 实际请求的资源地址；reason 底层给的原因，可为空
        // 步骤：
        //   1) 先写清哪张表、哪个地址，否则看报错的人无从复现
        //   2) 底层原因非空才追加，避免多出一行空的「底层原因」
        //   3) 按可能性从高到低列四条最常见原因，并各给一条查法：资产没进包（新加的表尤其）、表没导入成资产、
        //      收集配置没收 config 组、地址规则对不上
        // 返回：给 onFailed 的报错文本（最终经 Fail 交给调用方或日志）
        // 上游：LoadTable 的失败分支
        // 下游：Fail
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

        // 功能：把失败原因交给 onFailed；没人接就打到日志，绝不静默吞掉。
        // 入参：onFailed 失败回调，可为 null；message 失败原因
        // 步骤：
        //   1) 有回调就交出去并返回
        //   2) 没有回调才打 Debug.LogError，并点明这次加载没有任何失败回调
        // 返回：无
        // 上游：LoadTable 的三个失败分支
        // 下游：onFailed / Debug.LogError
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
