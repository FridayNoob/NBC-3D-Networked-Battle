// 这个文件里有：
//   ConfigMgr（类）：运行时配置表查询入口，缓存已加载的表并提供同步查询与预加载
//   ConfigMgr.PendingLoad（类）：一次进行中的表加载任务，聚合等待同表的多个回调

using System;
using System.Collections.Generic;
using System.Text;
using NBC.Framework;
using UnityEngine;

namespace NBC.Game.Config
{
    // 主要功能：单例入口，缓存已加载的配置表并提供同步查询；加载交给 IConfigSource，本类只管缓存、去重与报错。
    // 主要数据成员：m_loaded（表名 → 资产，0 到全部表数）、m_pending（表名 → 进行中的任务，同表最多一条）、m_source（资产来源，可为 null）。
    // 主要外部方法：SetSource / Preload / PreloadAll / Get / TryGet / IsLoaded / IsLoading / Unload / Clear。
    // 谁在用它：Boot 的 M2DemoBehaviour 与 Editor 的 NetDebugWindow 装配来源并查表；GameTables 用 PreloadAll 批量预加载；BattleSession.FromConfigMgr 与 EditMode 测试也直接查表。
    /// <summary>运行时配置表查询入口：缓存已加载的表，提供同步查询与预加载，跨程序集供 Boot / Editor 调用。</summary>
    public sealed class ConfigMgr : Singleton<ConfigMgr>
    {
        /// <summary>生成的配置资产类型名的后缀：表「Hero」对应类型「HeroConfig」，当前取值 Config。</summary>
        public const string AssetTypeSuffix = "Config";

        // 已加载的表：表名（Ordinal 区分大小写）→ 生成的配置资产，0 到全部表数；查询只读它，所以不碰 IO。
        private readonly Dictionary<string, ScriptableObject> m_loaded =
            new Dictionary<string, ScriptableObject>(StringComparer.Ordinal);

        // 正在加载的表：表名 → 那一条任务，每个表名最多一条；同表的后续请求挂到它上面。
        private readonly Dictionary<string, PendingLoad> m_pending =
            new Dictionary<string, PendingLoad>(StringComparer.Ordinal);

        // 资产来源：null 表示还没装，必须先由启动流程或测试 SetSource 才能加载。
        private IConfigSource m_source;

        // 当前的资产来源；null 表示还没设置，测试用它确认来源已装上。
        public IConfigSource Source
        {
            get { return m_source; }
        }

        // 已加载表的数量，范围 0 到全部表总数；测试用它确认缓存确实增长。
        public int LoadedTableCount
        {
            get { return m_loaded.Count; }
        }

        // 主要功能：一次进行中的表加载任务，把同表后续请求的回调聚合到同一条任务上。
        // 主要数据成员：OnSuccess（成功回调列表）、OnFailure（失败回调列表）。
        // 主要外部方法：无，只由 ConfigMgr 的 Preload / CompleteLoad / FailLoad 读写。
        // 谁在用它：ConfigMgr.m_pending。
        private sealed class PendingLoad
        {
            // 成功回调：表加载完成后按注册顺序逐个调用，可能为空列表。
            public readonly List<Action<ScriptableObject>> OnSuccess = new List<Action<ScriptableObject>>();

            // 失败回调：加载失败时按注册顺序逐个调用，可能为空列表。
            public readonly List<Action<string>> OnFailure = new List<Action<string>>();
        }

        // 功能：设置配置资产的来源，之后 Preload 都经它加载。
        // 入参：source 资产来源；null 表示清空（测试收尾用），清空后再加载会立刻失败并提示先 SetSource。
        // 返回：无。
        // 步骤：
        //   1) 直接覆盖 m_source —— 允许反复设置、不做相等判断；已缓存的表不受影响，换来源后仍从 m_loaded 命中
        // 上游：Boot 的 M2DemoBehaviour（装好资源层后）、Editor 的 NetDebugWindow（设置编辑器来源后）、EditMode 测试。
        // 下游：无（只写 m_source，不触发加载）。
        // 注意：必须在任何查询或预加载之前调用；来源装晚了，之前失败的请求不会自动重试。
        /// <summary>设置配置资产的来源；必须在任何查询之前调用，传 null 表示清空。</summary>
        public void SetSource(IConfigSource source)
        {
            m_source = source;
        }

        // 功能：表名拼上 Config 后缀，得到生成的资产类型名。
        // 入参：tableName 裸表名（如 Hero，不带 Config 后缀）；空串时结果只是 Config，本方法不校验。
        // 返回：资产类型名（Hero → HeroConfig）；不做存在性校验。
        // 步骤：
        //   1) 直接拼接、不加分隔符 —— 命名约定由 ConfigKit 生成侧保证，这里只负责同一个约定
        // 上游：Editor 的 EditorConfigSource（表名转资产名）、EditMode 测试。
        // 下游：无。
        // 注意：空表名不会被拦下；真正的空表名校验在 Preload(string)。
        /// <summary>表名拼上 Config 后缀得到资产类型名，空表名不校验。</summary>
        public static string AssetNameOf(string tableName)
        {
            return tableName + AssetTypeSuffix;
        }

        // 功能：按命名约定把生成的配置类型名去掉 Config 后缀，得到表名。
        // 入参：无显式入参；T 是生成的配置资产类型（必须继承 ScriptableObject）。
        // 返回：表名（HeroConfig → Hero）；类型名不合约定时抛 ArgumentException，不返回 null。
        // 步骤：
        //   1) 取 typeof(T).Name 这个简单名，不用 FullName —— 嵌套类型的完整名是 Outer+Nested，不适合拼表名
        //   2) 必须以后缀结尾且长度大于后缀 —— 否则裸名 Config 也会被当成合法表名
        //   3) 截掉后缀返回；不做反射、不查表，命名约定就是唯一依据
        // 上游：Preload<T>、Get<T>、TryGet<T>（都靠它把类型翻成表名）、EditMode 测试。
        // 下游：无。
        // 注意：名字不符就当场报错而不是猜 —— 手写类型不属于配置表，报错消息里给出正确形状与生成约定。
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

        // 功能：按泛型类型预加载一张表，成功回调抹掉资产参数，失败原因原样透传。
        public void Preload<T>(Action onDone = null, Action<string> onFailed = null) where T : ScriptableObject
        {
            Preload(TableNameOf<T>(),
                asset => { if (onDone != null) { onDone(); } },
                onFailed);
        }

        // 功能：按表名加载一张表；已缓存就同步回调，正在加载就挂到同一条任务，否则向来源发起一次加载。
        // 入参：tableName 裸表名，空或 null 立刻失败；onLoaded 成功回调，交出资产，可空；onFailed 失败回调，交出一句人话原因，可空。
        // 返回：无；结果走回调，缓存命中或来源同步回调时会在返回前触发。
        // 步骤：
        //   1) 先判空表名并立刻失败 —— 空名字不可能对应一张表，直接失败并给出明确文案
        //   2) 命中 m_loaded 就直接回调并返回 —— 配置预加载后常驻，不重复加载
        //   3) 命中 m_pending 就把两个回调追加到那条任务并返回 —— 同表同时只有一条加载任务，避免重复加载与重复键异常
        //   4) 没来源就失败并提示先 SetSource —— 否则会空引用，且没人告诉调用方要先 SetSource
        //   5) 新建 PendingLoad、登记回调、放进 m_pending，再向 m_source 发起加载
        //   6) 成功走 CompleteLoad、失败走 FailLoad，由它们统一清理 pending 并通知所有等待者
        // 上游：GameTables.PreloadAll（启动流程经它逐张串行）、Preload<T>、PreloadSequential、EditMode 测试。
        // 下游：IConfigSource.LoadTable、CompleteLoad、FailLoad、Fail、NoSourceMessage。
        // 注意：同一个表名同时只有一条加载任务；同帧连点两下若没有这条去重，会加载两遍并可能撞重复键。缓存命中与假来源都可能同步回调。
        public void Preload(string tableName, Action<ScriptableObject> onLoaded = null,
                            Action<string> onFailed = null)
        {
            if (string.IsNullOrEmpty(tableName))
            {
                Fail(onFailed, "[ConfigMgr] 表名是空的。");
                return;
            }

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

        // 功能：把一组表逐张串行预加载，全部成功才回调 onDone，任一张失败就停。
        // 入参：tableNames 表名列表，null 或空视为没有表要加载；onDone 全部成功回调，可空；onFailed 任一失败回调，可空，参数是一句人话原因。
        // 返回：无；结果走回调，表都已缓存时会在返回前同步触发。
        // 步骤：
        //   1) 先处理空清单 —— 没有表要加载就直接成功，不必进入串行流程
        //   2) 转给 PreloadSequential 从下标 0 开始，失败即停由它保证；失败原因已由 Preload 的 onFailed 交出去，本层不重复报
        // 上游：GameTables.PreloadAll（Boot 与 Editor 的启动装配都走那条）。
        // 下游：PreloadSequential、Preload。
        // 注意：是串行不是并发；前一张没成功就不会开始下一张，失败后剩余的表保持未加载。
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

            PreloadSequential(tableNames, 0, onDone, onFailed);
        }

        // 功能：从 index 开始逐张预加载，前一张成功后才请求下一张，任一张失败就停。
        // 入参：tableNames 表名列表；index 当前要加载的下标（0 起，等于 Count 表示已全部完成）；onDone 全部成功回调，可空；onFailed 失败回调，可空，原样透传。
        // 返回：无；走到末尾时回调 onDone。
        // 步骤：
        //   1) 下标越界说明全部完成，回调 onDone 并返回
        //   2) 只请求当前这一张，把「继续下一张」写在成功回调里 —— 回调可能同步发生（缓存命中或假来源），同步来源会当场递归下去，异步来源回来时继续
        // 上游：PreloadAll。
        // 下游：Preload。
        // 注意：不要改成「先调一次看有没有完成、再调一次等回调」的两段式 —— 同一张表会被请求两次，同名只有一条任务的语义就白设了；表只有几张，同步递归深度无所谓。
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

        // 功能：取一张已加载的表；没加载就抛异常，并在消息里说清表名、可能原因与解法。
        // 入参：无显式入参；T 是生成的配置资产类型。
        // 返回：已缓存的资产（强转为 T）；表没加载时抛 InvalidOperationException，不返回 null。
        // 步骤：
        //   1) 先按命名约定把 T 翻成表名
        //   2) 只查 m_loaded，命中就强转返回 —— 配置在启动时预加载，查询同步走内存、不碰 IO
        //   3) 没命中就抛 MissingTableMessage 生成的异常，把「忘了预加载」与「预加载失败」分开说
        // 上游：Boot 的 M2DemoBehaviour、Editor 的 NetDebugWindow、BattleSession.FromConfigMgr（Game 内）、EditMode 测试。
        // 下游：TableNameOf、MissingTableMessage。
        // 注意：配置表小但查得极频繁（战斗结算里每帧都可能查），所以不做异步查询，避免把调用方撕成回调链。
        // 注意：行查询不在这一层做 —— 生成表自带 Get(id) / TryGet(id, out row) 与主键索引，不需要反射或额外接口，用法是 ConfigMgr.Instance.Get<HeroConfig>().Get(1001)。
        /// <summary>取一张已加载的表；没加载就抛异常，消息里给出原因与解法。</summary>
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

        // 功能：试着取一张已加载的表，取不到返回 false，不抛异常。
        // 入参：无显式入参；T 是生成的配置资产类型；table 取到的资产，失败时为 null。
        // 返回：true 表示取到且 table 非 null；false 表示没加载，table 已置 null。
        // 步骤：
        //   1) 先把 table 置 null，保证失败时调用方拿不到旧值
        //   2) 按 TableNameOf 表名查缓存，命中才强转赋值并返回 true
        // 上游：BattleSession.FromConfigMgr（成就表允许缺省）、EditMode 测试。
        // 下游：TableNameOf。
        // 注意：只区分「加载过 / 没加载」，不区分「表不存在 / 忘了预加载」；需要原因时用 Get。
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

        // 功能：判断某张表是否已经加载进缓存。
        // 入参：tableName 裸表名；null 或空串直接返回 false。
        // 返回：true 表示已加载；false 表示未加载或表名非法。
        // 步骤：
        //   1) 先挡掉空表名，再查 m_loaded —— 空名字不会出现在缓存里，没必要进字典
        // 上游：Editor 的 NetDebugWindow（装真任务前确认 Hero 表可用）、EditMode 测试。
        // 下游：无。
        // 注意：只看缓存，不看是否正在加载；要判断加载中请用 IsLoading。
        /// <summary>某张表是否已经加载进缓存；空表名返回 false。</summary>
        public bool IsLoaded(string tableName)
        {
            return !string.IsNullOrEmpty(tableName) && m_loaded.ContainsKey(tableName);
        }

        // 功能：判断某张表是否正有一条加载任务在飞。
        // 入参：tableName 裸表名；null 或空串直接返回 false。
        // 返回：true 表示 m_pending 里有这条任务；成功或失败后都会清掉。
        // 步骤：
        //   1) 先挡掉空表名，再查 m_pending
        // 上游：EditMode 测试（确认失败后 pending 被清掉）。
        // 下游：无。
        // 注意：false 不代表加载成功，只代表当前没有任务在飞；要确认表可用还得看 IsLoaded。
        public bool IsLoading(string tableName)
        {
            return !string.IsNullOrEmpty(tableName) && m_pending.ContainsKey(tableName);
        }

        // 功能：从缓存里移除一张表（测试与切场景收尾用），让它下次 Get 抛异常、Preload 重新加载。
        // 入参：tableName 裸表名；null 或空串返回 false。
        // 返回：true 表示真的移除了；false 表示没加载过或表名非法，不抛异常。
        // 步骤：
        //   1) 挡掉空表名，再直接 Remove 并把结果原样返回；不碰 m_pending
        // 上游：EditMode 测试（验证卸载后不可查）。
        // 下游：无。
        // 注意：只删缓存，不销毁资产、不释放句柄 —— 真正的资产卸载要等资源层释放句柄。
        public bool Unload(string tableName)
        {
            return !string.IsNullOrEmpty(tableName) && m_loaded.Remove(tableName);
        }

        // 功能：清空已加载表与进行中的任务记录，让测试从零开始。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 两个字典都清 —— 只清 m_loaded 会留下挂着的 pending，后续 Preload 会误以为加载还在进行
        // 上游：EditMode 测试的 SetUp / TearDown。
        // 下游：无。
        // 注意：只清记录，不销毁资产、不释放句柄；已经发出的加载回来仍可能写回 m_loaded，所以不要在真实加载途中调用。
        public void Clear()
        {
            m_loaded.Clear();
            m_pending.Clear();
        }

        // 功能：一次加载成功的收尾：资产入缓存、清掉任务，再通知所有等待者。
        // 入参：tableName 表名；asset 来源交回的资产，null 视为失败而不是成功。
        // 返回：无。
        // 步骤：
        //   1) 先取出 pending —— 可能已被 Clear 掉，允许为 null
        //   2) asset 为 null 直接转 FailLoad —— 来源说成功却给 null，不能把 null 塞进缓存
        //   3) 先写 m_loaded、再移除 m_pending，保证回调里查同一张表能立刻命中
        //   4) 按注册顺序逐个回调 OnSuccess，每个都拿到同一个资产
        // 上游：Preload 传给 IConfigSource.LoadTable 的成功回调。
        // 下游：FailLoad、OnSuccess 回调列表。
        // 注意：回调在 CompleteLoad 内部同步执行；回调里再 Preload 同一张表会命中缓存，不会重复加载。
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

        // 功能：一次加载失败的收尾：清掉任务，把带表名的原因交给所有等待者；没人接就打到日志。
        // 入参：tableName 表名；reason 失败原因（来源或内部给出的人话）。
        // 返回：无。
        // 步骤：
        //   1) 取出并移除 pending
        //   2) 拼上 [ConfigMgr] 前缀与表名，保证报错能定位到哪张表
        //   3) 有失败回调就按顺序全部通知并返回
        //   4) 一个失败回调都没有时打 Debug.LogError —— 不能静默吞掉，否则就是一次无声的加载失败
        // 上游：Preload 的失败回调、CompleteLoad 的 null 资产分支。
        // 下游：OnFailure 回调列表、Debug.LogError。
        // 注意：失败的表不会进 m_loaded，pending 也已清掉，之后可以重新 Preload。
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

            Debug.LogError(message + "\n（这次加载没有任何失败回调，所以它只出现在这里。）");
        }

        // 功能：立刻报一个失败；有回调就交出去，没有就打日志，绝不静默。
        // 入参：onFailed 失败回调，可为 null；message 失败原因。
        // 返回：无。
        // 步骤：
        //   1) 有回调就先回调并返回
        //   2) 没有回调才打 Debug.LogError
        // 上游：Preload 的空表名与无来源两个前置失败分支。
        // 下游：onFailed / Debug.LogError。
        // 注意：只用于还没进入加载流程的前置错误，此时没有 pending 可清理。
        private static void Fail(Action<string> onFailed, string message)
        {
            if (onFailed != null)
            {
                onFailed(message);
                return;
            }

            Debug.LogError(message);
        }

        // 功能：拼出「还没设置来源」的报错，并给出启动流程里的正确调用与测试办法。
        // 入参：tableName 表名（写进消息，方便定位是哪次加载）。
        // 返回：含表名、正确调用示例与测试提示的报错文本。
        // 步骤：
        //   1) 一句话说清缺什么
        //   2) 给出启动流程里的 SetSource 调用
        //   3) 补一句测试用假来源即可，避免误以为必须真资产
        // 上游：Preload 的 m_source 为 null 分支。
        // 下游：Fail（由它交给回调或日志）。
        // 注意：这条错误必须说清怎么解决 —— 只报「没来源」会让调用方不知道下一步做什么。
        private static string NoSourceMessage(string tableName)
        {
            return "[ConfigMgr] 还没设置配置来源（IConfigSource），所以无法加载表「" + tableName + "」。\n" +
                   "启动流程里应当先调用：ConfigMgr.Instance.SetSource(new XxxConfigSource());\n" +
                   "（测试里塞一个假来源即可，不需要任何真实资产。）";
        }

        // 功能：拼出缺表报错：哪张表、已加载哪些表、两个最常见原因与正确查表写法。
        // 入参：tableName 表名；typeName 类型的简单名（写进可直接粘贴的调用片段）；fullTypeName 完整名（消歧义，可能带命名空间或嵌套类型的 +）。
        // 返回：完整报错文本。
        // 步骤：
        //   1) 先写清哪张表没加载、类型全名是什么
        //   2) 列出已加载的表，空时写「一张都没有」—— 一眼看出是整体没预加载还是漏了一张
        //   3) 列两个最常见原因：忘了预加载（比「表不存在」常见得多）、预加载失败但没接失败回调
        //   4) 末尾给出 Preload 与 Get(id) 的用法片段
        // 上游：Get<T> 的未命中分支。
        // 下游：无（纯拼字符串）。
        // 注意：代码片段里用简单名而不是 FullName —— 嵌套类型的 FullName 是 Outer+Nested，既难看又不能直接粘进代码；这条是测试当场抓出来的。
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

            builder.Append("  ① **忘了预加载** —— 在启动流程里调用 ConfigMgr.Instance.Preload<")
                   .Append(typeName).Append(">()\n");
            builder.Append("  ② 预加载失败了但没接失败回调（那种情况 Console 里会有一条 LogError）\n");
            builder.Append("查表用法：ConfigMgr.Instance.Get<").Append(typeName).Append(">().Get(id)");

            return builder.ToString();
        }
    }
}
