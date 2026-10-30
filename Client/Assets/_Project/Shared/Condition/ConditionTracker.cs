// 这个文件里有：
//   ConditionTracker（类）：条件系统的核心，登记条件、喂进玩法事件、按进度判定，并在恰好达成时回调。
//   Completion（结构体）：一次达成派发用的小记录，装条件编号与达成时的进度，只在本文件内使用。

using System;
using System.Collections.Generic;

// 注意：顶部关闭可空是让双端看到同一套规则 —— Unity 侧（NBC.Shared.asmdef）没开可空引用类型，
//     服务端（NBC.Shared.csproj → Server\Directory.Build.props）开了；不关的话服务端会多出一批 CS86xx，
//     而若为了消警告去写可空注解，Unity 侧又会报 CS8632，两头都不干净。
// 注意：代价是本目录放弃可空静态检查，「可能为 null」靠运行期校验兜住；将来两端统一开启可空，删掉那条指令即可。
#nullable disable

namespace NBC.Shared.Condition
{
    // 主要功能：条件系统的核心 —— 登记条件、喂进玩法事件、累加进度，并在恰好跨过需求线的那一次回调。
    // 主要数据成员：m_store（进度存放处）、m_registered（条件编号 → 定义）；进度值是业务次数，没有毫米/米与 tick/秒。
    // 主要外部方法：Register / Unregister / Notify / TryGetProgress / IsMet / Clear / Describe，以及 ConditionMet / ProgressChanged 两个事件。
    // 谁在用它：客户端 QuestRuntime / AchievementRuntime / ConditionEventBridge 与 Editor 调试窗口；服务端 QuestAuthority / AchievementAuthority；EditMode 测试直接构造。
    // 注意：达成只回调一次靠的是「构造进度时钳位」，不需要「已达成」标志位 —— 少一个状态就少一处能不同步的地方。
    // 注意：任务与成就共用本类，唯一差别是 Register 的 resetProgress：任务传 true（接取才清零），成就传 false（用既有进度，注册时已达成要当场回调，否则玩家得再打一只才解锁）。
    // 注意：一次 Notify 只在派发开始那一刻的登记表里找条件，回调里新登记的条件不算这一次 —— 否则刚接的任务会被上一只怪计数，玩家无法理解。
    /// <summary>条件系统核心：登记条件、喂事件、按进度判定，并在恰好达成时回调。</summary>
    public sealed class ConditionTracker
    {
        // 进度存放处；换数据库实现时这里不变，内存与 MySQL 实现都只认 IConditionProgressStore，无单位
        private readonly IConditionProgressStore m_store;

        // 已登记的条件：条件编号（配置表主键，≥ 1）→ 定义，无单位；空表表示没有任何条件关心事件
        private readonly Dictionary<int, ConditionDef> m_registered = new Dictionary<int, ConditionDef>();

        // 注意：用事件而不是构造函数的回调参数，是因为「达成」可以有多个观察者（任务与成就共用本类）；单个回调会让第二个观察者无处安放，还会让构造顺序绕成死结。
        /// <summary>条件达成事件，参数是条件编号与达成时的进度；一条条件只触发一次。</summary>
        public event Action<int, ConditionProgress> ConditionMet;

        // 注意：与 ConditionMet 分工 —— 本事件每次真的推进都发（给追踪条显示 2/3），ConditionMet 只在满了发一次；只发后者的话 UI 只能显示完成/未完成。
        /// <summary>进度推进事件，每次真的涨了都广播，包括正好达成的那一次。</summary>
        public event Action<int, ConditionProgress> ProgressChanged;

        /// <summary>造一个条件系统，进度读写全部委托给 store；store 为 null 时当场抛 ArgumentNullException。</summary>
        /// <param name="store">进度存放处，不能为 null；测试用 InMemoryConditionProgressStore。</param>
        public ConditionTracker(IConditionProgressStore store)
        {
            // 功能：接上进度存放处；没有存放处就当场报错，不留一个跑到一半才炸的实例。
            // 入参：store 进度存放处（不能为 null）
            // 返回：无
            // 步骤：
            //   1) 先查 null —— 后面每次登记与累加都要读它，晚炸不如早炸
            //   2) 通过后再赋值；字段是 readonly，装配期之后不再换
            // 上游：BattleSession 装配、Editor 调试窗口、服务端 QuestAuthority / AchievementAuthority、EditMode 测试
            // 下游：无
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store),
                    "[ConditionTracker] 必须给一个进度存放处（`IConditionProgressStore`）。\n" +
                    "测试里用 InMemoryConditionProgressStore 即可。");
            }

            m_store = store;
        }

        // 功能：直接返回登记表的条数。
        /// <summary>当前登记了多少条条件；只读，不触发进度读取。</summary>
        public int RegisteredCount
        {
            get { return m_registered.Count; }
        }

        /// <summary>已登记条件里已达成多少条，供 UI 与调试显示；要逐条读进度，复杂度 O(n)。</summary>
        public int MetCount
        {
            get
            {
                // 功能：逐条把已存进度与需求值比较，数出已达成的条数。
                // 入参：无
                // 返回：已达成的条数（0..RegisteredCount）
                // 步骤：
                //   1) 从 0 起数，遍历期间不修改任何状态
                //   2) 用 >= 比较 —— 进度本该被钳位，用 >= 能容忍历史数据超出需求值
                // 注意：每访问一次就把存放处整个读一遍，别放进每帧刷新的 UI；它没有缓存，也就不存在缓存过期。
                // 上游：Editor 调试窗口、UI 调试显示
                // 下游：m_store.GetProgress
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

        /// <summary>登记一条条件，之后 Notify 才会算它；同一编号重复登记会抛异常，不清零且已达成时会当场回调一次。</summary>
        /// <param name="conditionKey">条件编号，配置表里的行主键（≥ 1）。</param>
        /// <param name="def">条件定义，不能为 null。</param>
        /// <param name="resetProgress">是否把已有进度清零；任务传 true（接取才开始算），成就传 false（一直生效）。</param>
        public void Register(int conditionKey, ConditionDef def, bool resetProgress)
        {
            // 功能：登记一条条件，并决定是否把已有进度清零；不清零且已经达成时当场回调一次。
            // 入参：conditionKey 条件编号（≥ 1）；def 条件定义（非 null）；resetProgress 是否清零（任务 true，成就 false）
            // 返回：无
            // 步骤：
            //   1) 先查重复登记 —— 同一个编号只该有一个持有者，重复会让进度被悄悄清零、回调发两次
            //   2) 通过后写进登记表，之后 Notify 才会遍历到它
            //   3) resetProgress 为 true 就把进度清零并返回（任务接取才开始算）
            //   4) 否则读既有进度，已达成则当场回调一次（成就：上次已经打够，这次登录就该解锁，不能等再打一只）
            // 注意：重复登记必须当场抛异常，不能静默覆盖 —— 静默覆盖会让「进度被清零 / 回调发两次」永远查不出来。
            // 注意：第 4 步的回调发生在 Notify 之外，此刻没有遍历登记表，所以在回调里增删登记是安全的。
            // 上游：客户端 QuestRuntime / AchievementRuntime 读配置表时登记、Editor 调试窗口、服务端两个权威类、测试
            // 下游：RaiseConditionMet、m_store.SetProgress / GetProgress
            if (m_registered.ContainsKey(conditionKey))
            {
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

            int current = m_store.GetProgress(conditionKey);
            ConditionProgress progress = new ConditionProgress(current, def.RequiredCount);

            if (progress.IsMet)
            {
                RaiseConditionMet(conditionKey, progress);
            }
        }

        /// <summary>注销一条条件，之后 Notify 不再算它；只动登记表，不动已存进度。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <returns>true 表示本来登记过、真的移除了；false 表示本来就没登记。</returns>
        public bool Unregister(int conditionKey)
        {
            // 功能：把一条条件从登记表移除，之后 Notify 不再算它。
            // 入参：conditionKey 条件编号（找不到不算错误）
            // 返回：true 表示本来登记过、真的移除了；false 表示本来就没登记
            // 步骤：
            //   1) 直接从登记表 Remove，返回值就是「原来在不在」
            // 注意：只注销登记、不动进度 —— 进度留在存放处，重新接同一个任务时才可以传 resetProgress = false 接着算。
            // 上游：客户端 QuestRuntime / AchievementRuntime 收尾、服务端两个权威类、测试
            // 下游：无
            return m_registered.Remove(conditionKey);
        }

        /// <summary>这条条件登记了吗。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <returns>登记了返回 true。</returns>
        public bool IsRegistered(int conditionKey)
        {
            // 功能：只查登记状态，不看进度。
            return m_registered.ContainsKey(conditionKey);
        }

        /// <summary>取一条已登记条件的定义。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <param name="def">成功时是定义，失败时是 default。</param>
        /// <returns>登记过返回 true。</returns>
        public bool TryGetDef(int conditionKey, out ConditionDef def)
        {
            // 功能：按编号从登记表取定义，取到就顺带通过 out 返回。
            return m_registered.TryGetValue(conditionKey, out def);
        }

        /// <summary>取一条已登记条件的进度；没登记时返回 false，不抛异常。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <param name="progress">成功时是进度，失败时是 default。</param>
        /// <returns>登记过返回 true。</returns>
        public bool TryGetProgress(int conditionKey, out ConditionProgress progress)
        {
            // 功能：取一条已登记条件的进度，没登记时给 default 并返回 false。
            // 入参：conditionKey 条件编号；progress 输出进度（失败时为 default，需求值为 0）
            // 返回：true 表示登记过、progress 有效；false 表示没登记，不抛异常
            // 步骤：
            //   1) 先查登记表拿定义 —— 没登记就无从知道需求值，只能返回 false
            //   2) 有定义才把「已存进度 + 需求值」组成 ConditionProgress，钳位在构造里完成
            // 注意：失败时 progress 是 default，它的 IsMet 为 true（0 >= 0）；要判「有没有这个条件」得看返回值，别用 IsMet。
            // 上游：客户端 QuestRuntime / UI 追踪条、Editor 调试窗口、服务端下发、测试
            // 下游：m_store.GetProgress、ConditionProgress 构造
            ConditionDef def;

            if (!m_registered.TryGetValue(conditionKey, out def))
            {
                progress = default(ConditionProgress);
                return false;
            }

            progress = new ConditionProgress(m_store.GetProgress(conditionKey), def.RequiredCount);
            return true;
        }

        /// <summary>这条条件达成了吗；没登记时返回 false，不抛异常。</summary>
        /// <param name="conditionKey">条件编号。</param>
        /// <returns>登记过且已存进度够到需求值时返回 true。</returns>
        public bool IsMet(int conditionKey)
        {
            // 功能：查一条条件是否已达成；没登记一律算未达成。
            // 入参：conditionKey 条件编号
            // 返回：true 表示登记过且已存进度 ≥ 需求值；false 表示没登记或还没到
            // 步骤：
            //   1) 先取定义，没登记直接返回 false —— 不抛异常，调用方不必先查存在性
            //   2) 用 >= 比较，容忍历史数据超过需求值
            // 注意：没登记与没达成都是 false；调用方要区分这两者，就用 TryGetProgress。
            // 上游：客户端 QuestRuntime 收尾判定、服务端权威类、测试
            // 下游：m_store.GetProgress
            ConditionDef def;

            if (!m_registered.TryGetValue(conditionKey, out def))
            {
                return false;
            }

            return m_store.GetProgress(conditionKey) >= def.RequiredCount;
        }

        /// <summary>告诉条件系统发生了一件事，推进所有匹配条件的进度并广播；返回这一次让多少条条件达成。</summary>
        /// <param name="eventType">发生了什么，必须是 EConditionEvent 的合法成员。</param>
        /// <param name="targetId">发生在谁身上；0 = 没有具体目标，只有「任意目标」的条件会匹配到。</param>
        /// <param name="count">发生了多少次，必须 ≥ 1（一次击杀传 1）。</param>
        /// <returns>本次调用让多少条条件达成（0 = 只是涨了进度，或根本没有条件匹配）。</returns>
        public int Notify(EConditionEvent eventType, int targetId, int count)
        {
            // 功能：把一次玩法事件推进给所有匹配的条件，先广播进度、再广播达成，返回本次达成的条数。
            // 入参：eventType 事件类型（必须是合法成员）；targetId 发生在谁身上（0 = 任意目标，负数非法）；count 发生次数（≥ 1）
            // 返回：本次调用让多少条条件达成（0 = 只是涨了进度，或根本没有条件匹配）
            // 步骤：
            //   1) 先校验三个入参 —— 枚举强转出来的假值 C# 不报错，只会安静地匹配不到任何条件，必须在入口拦住
            //   2) 没有登记就直接返回 0，避免无谓的遍历与分配
            //   3) 第一段只读登记表、只写进度：跳过错配与已达成的条件，把「变了的」和「正好达成的」分别收进局部列表
            //   4) 第二段才回调：先遍历 changed 发 ProgressChanged，再遍历 completed 发 ConditionMet
            // 注意：必须先收集、后派发 —— 回调里很可能注销/登记别的条件（交任务、接下一个任务、发奖再触发一次事件），边遍历边改登记表会直接抛 Collection was modified。
            // 注意：派发顺序是契约：先全部 ProgressChanged 再全部 ConditionMet，反过来的话 UI 会先收到「完成了」再收到「2/3」，显示上闪一下。
            // 注意：已经达成的条件不再累计 —— 进度是钳位的，再涨也没有意义，而且这样达成天然只回调一次。
            // 注意：本次事件只认派发开始那一刻的登记表，回调里新登记的条件不会被这一次事件计数（刚接的任务不该被上一只怪算进去）。
            // 注意：只有真的有条件被推进时才分配两个列表；绝大多数 Notify（打死一只怪）走的是零分配路径，这点分配留给「有东西达成」的少数情况。
            // 上游：客户端 ConditionEventBridge 把 EventCenter 的事件转进来、Editor 调试窗口、服务端权威类、测试
            // 下游：ConditionDef.Matches、m_store.GetProgress / SetProgress、ProgressChanged、RaiseConditionMet
            if (!ConditionEvents.IsDefined(eventType))
            {
                throw new ArgumentOutOfRangeException(nameof(eventType),
                    "[ConditionTracker] 事件类型不是已知成员：" + (int)eventType + "。\n" +
                    "多半是配置表里写了一个不存在的枚举值（枚举强转在 C# 里不报错，会安静地匹配不到任何条件）。");
            }

            if (count < 1)
            {
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

        private void RaiseConditionMet(int conditionKey, ConditionProgress progress)
        {
            // 功能：先把事件拷到本地再判空调用，没人订阅时什么都不做。
            // 入参：conditionKey 条件编号；progress 达成时的进度
            // 返回：无
            // 步骤：
            //   1) 先把事件拷到局部变量 —— 判空与调用之间有人退订，也不会拿到 null
            //   2) 有订阅者才调用，没有就什么都不做
            // 上游：Register（注册时已达成）、Notify（第二段派发）
            // 下游：订阅了 ConditionMet 的调用方
            Action<int, ConditionProgress> handler = ConditionMet;

            if (handler != null)
            {
                handler(conditionKey, progress);
            }
        }

        /// <summary>清空全部登记，不动已存进度；测试收尾、切账号用。</summary>
        public void Clear()
        {
            // 功能：清空登记表，之后 Notify 不会匹配到任何条件。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 直接清空登记表，只动内存里的那一份
            // 注意：不动已存进度 —— 重新登记时可以接着算；要连进度一起清，得自己调存放处。
            // 上游：BattleSession 收尾、测试
            // 下游：无
            m_registered.Clear();
        }

        /// <summary>把当前登记与进度拼成多行文本，供调试面板与日志用。</summary>
        /// <returns>多行文本，每行一条条件；没有任何登记时只有一行表头。</returns>
        public string Describe()
        {
            // 功能：逐条把编号、定义描述与进度拼成多行文本，并标出已达成的条目。
            // 入参：无
            // 返回：多行文本，每行一条条件；没有登记时只有一行表头
            // 步骤：
            //   1) 先写表头，带上登记条数
            //   2) 逐条取进度（构造时钳位），把编号、定义描述与「当前/需求」按固定顺序拼起来
            //   3) 已达成的那条补一个对勾标记，便于肉眼扫
            // 注意：这里每次都读存放处，只适合调试与日志，不要放进每帧刷新的界面。
            // 上游：Editor 调试窗口、日志输出、测试
            // 下游：ConditionDef.Describe、ConditionProgress.ToString、m_store.GetProgress
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

        // 主要功能：一次派发用的小记录，把「哪条条件」与「达成时的进度」绑在一起，让第二段派发不依赖仍在变的登记表。
        // 主要数据成员：Key（条件编号）、Progress（达成时的进度，此刻已满）。
        // 主要外部方法：无，只有构造与两个只读字段。
        // 谁在用它：本文件的 ConditionTracker.Notify，第一段收集、第二段遍历。
        private readonly struct Completion
        {
            // 条件编号（配置表主键，≥ 1），无单位
            public readonly int Key;

            // 达成时的进度，此刻一定是满的（Current == Required），无单位
            public readonly ConditionProgress Progress;

            public Completion(int key, ConditionProgress progress)
            {
                // 功能：把一次达成的编号与进度装进记录，供第二段派发读取。
                Key = key;
                Progress = progress;
            }
        }
    }
}
