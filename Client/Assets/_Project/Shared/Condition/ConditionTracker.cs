// ============================================================================
//  ConditionTracker —— 条件系统的核心：订阅 → 累加 → 判定 → 回调
//  项目：3D联网战斗Demo   对应：M2-A1（条件系统）、Docs\20 §四
//
//  ---------------------------------------------------------------------------
//  它干的事（一句话）
//  ---------------------------------------------------------------------------
//      有人告诉它"发生了一件事"（`Notify`）
//      → 它找出**哪些已登记的条件**关心这件事
//      → 把进度往前推
//      → **恰好跨过需求线的那一次**，回调一次
//
//  它**不知道**进度是任务的还是成就的，也不知道达成之后要发奖励还是弹窗 ——
//  那些都在 `Action<int, ConditionProgress>` 这个回调的另一头。
//
//  ---------------------------------------------------------------------------
//  三条写死的语义（**每一条都有理由，别改**）
//  ---------------------------------------------------------------------------
//  ① **进度钳位**（见 `ConditionProgress` 的说明）→ 达成**只回调一次**，
//     不需要任何"已达成"标志位。少一个状态就少一处能不同步的地方。
//
//  ② **`Register(..., resetProgress)` 的那个 bool 就是"任务"与"成就"的唯一差别**：
//        任务（接取才开始算）：`resetProgress = true`  → 进度清零
//        成就（一直生效）      ：`resetProgress = false` → 用既有进度；
//                               **如果注册时就已经达成，当场回调一次**
//        —— 这条对"上次已经打了 10 只、这次登录该解锁成就"是必须的，
//           否则成就要等玩家**再打一只**才会解锁（一个很典型的、看起来像玄学的 bug）。
//
//  ③ **一次 `Notify` 里，条件只在"派发开始那一刻"的登记表里找**：
//     回调里新登记的条件**不会**被这一次事件影响。
//     （理由：回调里可能接下一个任务，而"刚接的任务被上一个任务的那只怪计数"
//       是玩家完全无法理解的。这条语义要和 A3 事件中心的"派发中增删"契约保持一致。）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么回调要"先收集、后派发"（一个真踩过的坑）
//  ---------------------------------------------------------------------------
//  M1-C2 里我写过一段"一边遍历诊断集合、一边调用渲染（渲染会动那个集合）"的代码，
//  运行期直接抛 `Collection was modified`。
//  这里形状一模一样：**达成回调里很可能去注销/登记别的条件**
//  （交任务、接下一个任务、发奖励触发 `CollectItem` 事件再 `Notify` 一次）。
//
//  所以 `Notify` 分成两段：
//      第一段：只做"读 + 写进度"，把**恰好达成的那些**收集到一个局部列表
//      第二段：遍历那个列表逐个回调 —— 此刻随便增删登记表都安全
//  代价是"有东西达成时"要分配一个小列表；没东西达成时**零分配**
//  （绝大多数 `Notify` 都属于后者：打一只怪不会有任务完成）。
// ============================================================================

using System;
using System.Collections.Generic;

// ============================================================================
//  ⚠️ `#nullable disable` —— 让**双端看到同一套规则**（D1 的"不许漂移"）
// ============================================================================
//  同一份源码在两个 nullable 设置**不同**的工程里编译：
//      · Unity 侧（`NBC.Shared.asmdef`）      ：没开可空引用类型
//      · 服务端（`NBC.Shared.csproj` → `Server\Directory.Build.props`）：`<Nullable>enable</Nullable>`
//
//  不处理的话会出现**两头都不干净**：
//      · 服务端多出一批 CS86xx（事件没初始化、局部变量赋 null……）
//      · 而如果为了消警告去写 `string?` / `List<T>?`，**Unity 侧**又会因为
//        "可空注解出现在未开启可空上下文的文件里"报 **CS8632**
//
//  所以这里显式关掉：**两边都按"没有可空注解"这套规则编译**。
//  这不是"为了消警告而关检查"，而是**把环境差异钉死在一处** ——
//  否则"服务端编得过、Unity 编不过"（或反过来）正是 D1 要防的那种漂移。
//
//  ⚠️ 代价（如实记）：本目录里**放弃**了可空引用类型的静态检查，
//     "可能为 null"要靠 XML 注释和运行期校验（本目录两个都做了）。
//     若将来两端统一开启可空，把这几行删掉即可 —— 它们集中且显眼。
// ============================================================================

#nullable disable

namespace NBC.Shared.Condition
{
    /// <summary>条件系统核心：登记条件、喂事件、达成时回调。</summary>
    public sealed class ConditionTracker
    {
        /// <summary>进度存放处（换数据库实现时这里不变）。</summary>
        private readonly IConditionProgressStore m_store;

        /// <summary>已登记的条件：条件编号 → 定义。</summary>
        private readonly Dictionary<int, ConditionDef> m_registered = new Dictionary<int, ConditionDef>();

        /// <summary>
        /// 达成事件（参数：条件编号、达成时的进度）。
        /// <para>
        /// ⚠️ **为什么是"事件"而不是构造函数的回调参数**（两个理由，第二个才是关键）：
        /// ① 构造顺序会变成死结：`QuestRuntime` 需要 `ConditionTracker`，
        ///    而 `ConditionTracker` 的回调又要指向 `QuestRuntime`；
        /// ② 更重要的：**达成这件事可以有多个观察者**。
        ///    M4 的任务与成就要**共用这一套条件系统**，它们会同时关心同一批条件 ——
        ///    单个回调参数会让"第二个观察者"无处安放。
        /// </para>
        /// </summary>
        public event Action<int, ConditionProgress> ConditionMet;

        /// <summary>
        /// 进度变化事件（**每次真的推进了**都广播，包括"这一下正好达成"的那一次）。
        /// <para>
        /// ⚠️ 它和 <see cref="ConditionMet"/> 的分工要分清：
        ///   · `ProgressChanged` = "**2/3 了**" —— 给追踪条、进度条用，每次推进都发
        ///   · `ConditionMet`     = "**满了**" —— 给"任务完成/成就解锁"用，一条条件只发一次
        /// 只提供后者的话，UI 就只能显示"完成/未完成"，中间那格数字是死的。
        /// </para>
        /// <para>派发顺序：**先全部 `ProgressChanged`，再全部 `ConditionMet`**（见 `Notify`）。</para>
        /// </summary>
        public event Action<int, ConditionProgress> ProgressChanged;

        /// <summary>造一个条件系统。</summary>
        /// <param name="store">进度存放处（不能为 null）。</param>
        public ConditionTracker(IConditionProgressStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store),
                    "[ConditionTracker] 必须给一个进度存放处（`IConditionProgressStore`）。\n" +
                    "测试里用 InMemoryConditionProgressStore 即可。");
            }

            m_store = store;
        }

        /// <summary>当前登记了多少条条件。</summary>
        public int RegisteredCount
        {
            get { return m_registered.Count; }
        }

        /// <summary>当前登记的条件里，已经达成的有多少条（UI/调试用）。</summary>
        public int MetCount
        {
            get
            {
                int count = 0;

                foreach (KeyValuePair<int, ConditionDef> pair in m_registered)
                {
                    if (m_store.GetProgress(pair.Key) >= pair.Value.RequiredCount)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        // ====================================================================
        //  登记 / 注销
        // ====================================================================

        /// <summary>
        /// 登记一条条件（之后 `Notify` 才会算它）。
        /// </summary>
        /// <param name="conditionKey">条件编号（配置表里的行主键）。</param>
        /// <param name="def">条件定义。</param>
        /// <param name="resetProgress">
        /// 是否把已有进度清零。任务传 true（接取才开始算），成就传 false（一直生效）。
        /// </param>
        /// <exception cref="InvalidOperationException">同一个编号**重复登记**时抛。</exception>
        public void Register(int conditionKey, ConditionDef def, bool resetProgress)
        {
            if (m_registered.ContainsKey(conditionKey))
            {
                // 重复登记几乎总是 bug：两处都以为自己在管这条条件，
                // 于是进度被别人悄悄清零 / 回调发两次。
                // 静默覆盖会让这个 bug 永远查不出来，所以当场报错。
                throw new InvalidOperationException(
                    "[ConditionTracker] 条件 " + conditionKey + " 已经登记过了，不能重复登记。\n" +
                    "同一个条件编号只应该被一个持有者登记（重复登记会让进度被清零、或回调发两次）。\n" +
                    "如果确实要重新开始，先 Unregister(" + conditionKey + ")。");
            }

            m_registered.Add(conditionKey, def);

            if (resetProgress)
            {
                m_store.SetProgress(conditionKey, 0);
                return;
            }

            // 语义②：不清零时，如果**本来就已经达成**，当场回调一次。
            // （成就："上次已经打了 10 只，这次登录就该解锁"。不这么做的话，
            //   玩家得再打一只才解锁 —— 看起来像玄学。）
            int current = m_store.GetProgress(conditionKey);
            ConditionProgress progress = new ConditionProgress(current, def.RequiredCount);

            if (progress.IsMet)
            {
                RaiseConditionMet(conditionKey, progress);
            }
        }

        /// <summary>注销一条条件（之后 `Notify` 不再算它）。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <returns>本来登记过、真的注销了才返回 true。</returns>
        public bool Unregister(int conditionKey)
        {
            // ⚠️ **只注销登记，不动进度**：进度留在存放处，
            //    这样"重新接同一个任务"能选择接着算（传 resetProgress = false）。
            return m_registered.Remove(conditionKey);
        }

        /// <summary>这条条件登记了吗。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <returns>登记了返回 true。</returns>
        public bool IsRegistered(int conditionKey)
        {
            return m_registered.ContainsKey(conditionKey);
        }

        /// <summary>取一条已登记条件的定义。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <param name="def">定义。</param>
        /// <returns>登记过返回 true。</returns>
        public bool TryGetDef(int conditionKey, out ConditionDef def)
        {
            return m_registered.TryGetValue(conditionKey, out def);
        }

        // ====================================================================
        //  进度查询
        // ====================================================================

        /// <summary>取一条已登记条件的进度（**没登记时返回 false**，不抛异常）。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <param name="progress">进度。</param>
        /// <returns>登记过返回 true。</returns>
        public bool TryGetProgress(int conditionKey, out ConditionProgress progress)
        {
            ConditionDef def;

            if (!m_registered.TryGetValue(conditionKey, out def))
            {
                progress = default(ConditionProgress);
                return false;
            }

            progress = new ConditionProgress(m_store.GetProgress(conditionKey), def.RequiredCount);
            return true;
        }

        /// <summary>这条条件达成了吗（**没登记时返回 false**）。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <returns>达成了返回 true。</returns>
        public bool IsMet(int conditionKey)
        {
            ConditionDef def;

            if (!m_registered.TryGetValue(conditionKey, out def))
            {
                return false;
            }

            return m_store.GetProgress(conditionKey) >= def.RequiredCount;
        }

        // ====================================================================
        //  喂事件
        // ====================================================================

        /// <summary>
        /// 告诉条件系统"发生了一件事"。
        /// </summary>
        /// <param name="eventType">发生了什么（必须是合法成员）。</param>
        /// <param name="targetId">发生在谁身上（0 = 没有具体目标，只有"任意"条件会匹配到）。</param>
        /// <param name="count">发生了多少次，必须 ≥ 1（一次击杀就是 1）。</param>
        /// <returns>**这一次调用**让多少条条件达成（0 = 只是涨了进度）。</returns>
        public int Notify(EConditionEvent eventType, int targetId, int count)
        {
            if (!ConditionEvents.IsDefined(eventType))
            {
                throw new ArgumentOutOfRangeException(nameof(eventType),
                    "[ConditionTracker] 事件类型不是已知成员：" + (int)eventType + "。\n" +
                    "多半是配置表里写了一个不存在的枚举值（枚举强转在 C# 里不报错，会安静地匹配不到任何条件）。");
            }

            if (count < 1)
            {
                // "发生了 0 次"不是一个事件，通常是调用方算错了。
                // 静默忽略它会让"任务怎么不涨进度"变成悬案。
                throw new ArgumentOutOfRangeException(nameof(count),
                    "[ConditionTracker] count 必须 ≥ 1（当前 " + count + "）。\n" +
                    "一次击杀传 1；要一次加多次请传实际次数，不要传 0 或负数。");
            }

            if (targetId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetId),
                    "[ConditionTracker] targetId 不能是负数（当前 " + targetId + "）。\n" +
                    "没有具体目标时传 0 —— 只有\"任意目标\"的条件会匹配到 0。");
            }

            if (m_registered.Count == 0)
            {
                return 0;
            }

            // -------- 第一段：读 + 写进度，把"变了的"和"正好达成的"收集起来 --------
            // ⚠️ 只有在**真的有条件被推进**时才分配这两个列表。
            //    这里的调用频率是"每次玩法事件"（打死一只怪、捡到一件物品），
            //    **不是每帧** —— 所以这点分配可以接受，换来的是"回调里随便增删登记表"的安全性，
            //    以及"达成只回调一次"不需要额外标志位。
            List<Completion> changed = null;
            List<Completion> completed = null;

            foreach (KeyValuePair<int, ConditionDef> pair in m_registered)
            {
                ConditionDef def = pair.Value;

                if (!def.Matches(eventType, targetId))
                {
                    continue;
                }

                int before = m_store.GetProgress(pair.Key);

                // 已经达成了：**不再累计**（进度是钳位的，再涨也没有意义）
                if (before >= def.RequiredCount)
                {
                    continue;
                }

                ConditionProgress after = new ConditionProgress(before + count, def.RequiredCount);
                m_store.SetProgress(pair.Key, after.Current);

                if (changed == null)
                {
                    changed = new List<Completion>(1);
                }

                changed.Add(new Completion(pair.Key, after));

                if (!after.IsMet)
                {
                    continue;
                }

                if (completed == null)
                {
                    completed = new List<Completion>(1);
                }

                completed.Add(new Completion(pair.Key, after));
            }

            if (changed == null)
            {
                return 0;
            }

            // -------- 第二段：派发（此刻增删登记表都安全） --------
            // 顺序是契约的一部分：**先把所有"进度变了"发完，再发"达成"**。
            // 反过来的话，UI 会先收到"任务完成了"、再收到"2/3"，显示上会闪一下。
            for (int i = 0; i < changed.Count; i++)
            {
                Action<int, ConditionProgress> handler = ProgressChanged;

                if (handler != null)
                {
                    handler(changed[i].Key, changed[i].Progress);
                }
            }

            if (completed == null)
            {
                return 0;
            }

            for (int i = 0; i < completed.Count; i++)
            {
                RaiseConditionMet(completed[i].Key, completed[i].Progress);
            }

            return completed.Count;
        }

        /// <summary>安全地广播一次"达成"（没人订阅时什么都不做）。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <param name="progress">达成时的进度。</param>
        private void RaiseConditionMet(int conditionKey, ConditionProgress progress)
        {
            Action<int, ConditionProgress> handler = ConditionMet;

            if (handler != null)
            {
                handler(conditionKey, progress);
            }
        }

        /// <summary>清空全部登记（**不动进度**；测试收尾、切账号用）。</summary>
        public void Clear()
        {
            m_registered.Clear();
        }

        /// <summary>把当前登记与进度拼成一段文本（调试面板/日志用）。</summary>
        /// <returns>多行文本。</returns>
        public string Describe()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("[ConditionTracker] 登记 ").Append(m_registered.Count).Append(" 条：\n");

            foreach (KeyValuePair<int, ConditionDef> pair in m_registered)
            {
                ConditionProgress progress =
                    new ConditionProgress(m_store.GetProgress(pair.Key), pair.Value.RequiredCount);

                builder.Append("  · ").Append(pair.Key).Append("  ")
                       .Append(pair.Value.Describe()).Append("  → ").Append(progress.ToString())
                       .Append(progress.IsMet ? "  ✅" : string.Empty).Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>一条"刚刚达成的条件"（第一段收集、第二段派发，所以要一个小结构）。</summary>
        private readonly struct Completion
        {
            /// <summary>条件编号。</summary>
            public readonly int Key;

            /// <summary>达成时的进度（此刻一定是满的）。</summary>
            public readonly ConditionProgress Progress;

            /// <summary>造一条记录。</summary>
            /// <param name="key">条件编号。</param>
            /// <param name="progress">达成时的进度。</param>
            public Completion(int key, ConditionProgress progress)
            {
                Key = key;
                Progress = progress;
            }
        }
    }
}
