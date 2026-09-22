// ============================================================================
//  ConditionDef —— 一条「条件」的定义（数据，不是行为）
//  项目：3D联网战斗Demo   对应：M2-A1（条件系统）、Docs\20 §四
//
//  ---------------------------------------------------------------------------
//  一条条件只有三个字段
//  ---------------------------------------------------------------------------
//      事件类型   eventType      「玩家做了什么」      KillMonster
//      目标编号   targetId       「做在谁身上」        6001（野狼）；0 = 任意
//      需要数量   requiredCount  「做多少次」          3
//
//  于是"击杀 3 只野狼"就是 `new ConditionDef(KillMonster, 6001, 3)`。
//
//  ⚠️ **它刻意不认识"任务"或"成就"这两个词。**
//     任务的条件与成就的条件**结构完全一样**，区别只在两头：
//         · 来源：任务要"接取"之后才开始计数；成就一直生效
//         · 达成后：任务发奖励；成就解锁 + 展示
//     把这两头留在外面，中间这层就能被两边共用（`Docs\20` §四 的设计要点）。
//     这和 M1 里 `ConfigPolicy`（项目知识不进 Core）是**同一种思路**。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么 `targetId == 0` 表示"任意"
//  ---------------------------------------------------------------------------
//  因为**配置表里必须填东西**（空单元格是报错，见 `Docs\17` §六）。
//  "任意怪都算"这种需求也得有个写法，用 0 是最省的（主键永远 ≥ 1，不会撞车）。
//
//  ⚠️ 为什么构造时**当场校验**而不是"用的时候再说"
//     一条 `requiredCount = 0` 的条件会**立刻判定为已达成**（0 >= 0），
//     表现是"刚接到任务就完成了" —— 这种 bug 的现场离原因极远。
//     所以在**造出来的那一刻**就报错，报错里带上是哪个字段、值是多少。
// ============================================================================

using System;

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
    /// <summary>一条条件的定义（事件 + 目标 + 数量）。</summary>
    public readonly struct ConditionDef
    {
        /// <summary>事件类型（"玩家做了什么"）。</summary>
        private readonly EConditionEvent m_eventType;

        /// <summary>目标编号（"做在谁身上"；0 = 任意）。</summary>
        private readonly int m_targetId;

        /// <summary>需要数量（"做多少次"，必须 ≥ 1）。</summary>
        private readonly int m_requiredCount;

        /// <summary>造一条条件定义。</summary>
        /// <param name="eventType">事件类型。</param>
        /// <param name="targetId">目标编号；<c>0</c> = 任意目标。</param>
        /// <param name="requiredCount">需要数量，必须 ≥ 1。</param>
        /// <exception cref="ArgumentException">字段非法时抛（消息里说清哪个字段、值是多少）。</exception>
        public ConditionDef(EConditionEvent eventType, int targetId, int requiredCount)
        {
            string error = Validate(eventType, targetId, requiredCount);

            if (error != null)
            {
                throw new ArgumentException(error);
            }

            m_eventType = eventType;
            m_targetId = targetId;
            m_requiredCount = requiredCount;
        }

        /// <summary>事件类型。</summary>
        public EConditionEvent EventType
        {
            get { return m_eventType; }
        }

        /// <summary>目标编号（<c>0</c> = 任意目标）。</summary>
        public int TargetId
        {
            get { return m_targetId; }
        }

        /// <summary>需要数量（≥ 1）。</summary>
        public int RequiredCount
        {
            get { return m_requiredCount; }
        }

        /// <summary>目标是不是"任意"。</summary>
        public bool IsAnyTarget
        {
            get { return m_targetId == 0; }
        }

        /// <summary>
        /// 校验三个字段。**合法返回 null，不合法返回一句人话原因。**
        /// <para>让"校验"和"构造"共用同一份规则：配置表那边也走这里，不会出现两套判断。</para>
        /// </summary>
        /// <param name="eventType">事件类型。</param>
        /// <param name="targetId">目标编号。</param>
        /// <param name="requiredCount">需要数量。</param>
        /// <returns>合法返回 null；否则返回原因。</returns>
        public static string Validate(EConditionEvent eventType, int targetId, int requiredCount)
        {
            if (!ConditionEvents.IsDefined(eventType))
            {
                return "「事件类型」不是已知成员：" + (int)eventType +
                       "。合法的值见 NBC.Shared.Condition.EConditionEvent（0~" +
                       (ConditionEvents.Count - 1) + "）。";
            }

            if (targetId < 0)
            {
                return "「目标编号」不能是负数（当前 " + targetId + "）；要表示\"任意目标\"请填 0。";
            }

            if (requiredCount < 1)
            {
                return "「需要数量」必须 ≥ 1（当前 " + requiredCount + "）。\n" +
                       "填 0 的条件会**立刻判定为已达成**，表现是\"刚接到任务就完成了\"，而且很难往配置上想。";
            }

            return null;
        }

        /// <summary>
        /// 试着造一条条件定义（**不抛异常**的版本，给"逐行读配置表"用）。
        /// </summary>
        /// <param name="eventType">事件类型。</param>
        /// <param name="targetId">目标编号。</param>
        /// <param name="requiredCount">需要数量。</param>
        /// <param name="def">造出来的定义（失败时是 default）。</param>
        /// <param name="error">失败原因（成功时是 null）。</param>
        /// <returns>成功返回 true。</returns>
        public static bool TryCreate(EConditionEvent eventType, int targetId, int requiredCount,
                                     out ConditionDef def, out string error)
        {
            error = Validate(eventType, targetId, requiredCount);

            if (error != null)
            {
                def = default(ConditionDef);
                return false;
            }

            def = new ConditionDef(eventType, targetId, requiredCount);
            return true;
        }

        /// <summary>这个定义匹配不匹配"某个目标身上发生的某件事"。</summary>
        /// <param name="eventType">发生了什么。</param>
        /// <param name="targetId">发生在谁身上。</param>
        /// <returns>匹配返回 true。</returns>
        public bool Matches(EConditionEvent eventType, int targetId)
        {
            // 事件类型必须一模一样；目标编号允许"任意"
            return m_eventType == eventType && (m_targetId == 0 || m_targetId == targetId);
        }

        /// <summary>转成一句人话（报错、日志、UI 追踪条都用它）。</summary>
        /// <returns>例如「击杀怪物 ×3（目标 6001）」。</returns>
        public string Describe()
        {
            string target = m_targetId == 0 ? "任意目标" : "目标 " + m_targetId;
            return ConditionEvents.Describe(m_eventType) + " ×" + m_requiredCount + "（" + target + "）";
        }

        /// <summary>转成一句人话。</summary>
        /// <returns>描述文本。</returns>
        public override string ToString()
        {
            return Describe();
        }
    }
}
