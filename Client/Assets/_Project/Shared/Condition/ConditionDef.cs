// 这个文件里有：
//   ConditionDef（结构体）：一条「条件」的不可变定义，只装事件类型、目标编号与需要次数，刻意不含任何业务语义。

using System;

// 注意：#nullable disable 是故意的 —— Unity 侧未开可空，服务端开了 Nullable enable，同一份源码必须按同一套规则编译；
// 不关的话服务端多出一批 CS86xx，而改写成 string? 又会让 Unity 侧报 CS8632。
// 代价是本目录放弃可空引用的静态检查，「可能为 null」只能靠运行期校验兜住。
#nullable disable

namespace NBC.Shared.Condition
{
    // 主要功能：把「玩家做了什么」压成一条不可变数据（事件 + 目标 + 次数），供条件追踪器登记、匹配与判定。
    // 主要数据成员：m_eventType（事件类型，0..ConditionEvents.Count-1）、m_targetId（目标编号，0 = 任意）、m_requiredCount（需要次数，≥ 1）；本文件不涉及毫米/米与 tick/秒，只有编号与次数。
    // 主要外部方法：构造、Validate / TryCreate（校验与安全构造）、Matches（匹配事件）、Describe（一句人话），以及 EventType / TargetId / RequiredCount / IsAnyTarget 四个只读属性。
    // 谁在用它：客户端 QuestRuntime 与 AchievementRuntime、Editor 的 NetDebugWindow、服务端 QuestTables；两端编译同一份源码。
    /// <summary>一条条件的不可变定义：事件类型 + 目标编号 + 需要次数，构造时即校验。</summary>
    public readonly struct ConditionDef
    {
        private readonly EConditionEvent m_eventType;    // 事件类型，「玩家做了什么」；取值范围是 EConditionEvent 的合法成员（0..ConditionEvents.Count-1），无单位。
        private readonly int m_targetId;                 // 目标编号，「做在谁身上」；0 = 任意目标，其余为配置表主键（≥ 1），无单位。
        private readonly int m_requiredCount;            // 需要次数，「做多少次」；必须 ≥ 1，填 0 会让条件一造出来就是已达成，无长度/时间单位。

        /// <summary>造一条条件定义，并在造出来的当下校验三个字段。</summary>
        /// <param name="eventType">事件类型，必须是 EConditionEvent 的合法成员。</param>
        /// <param name="targetId">目标编号；0 = 任意目标，负数非法。</param>
        /// <param name="requiredCount">需要次数，必须 ≥ 1。</param>
        /// <exception cref="ArgumentException">字段不合法时抛，消息里说清是哪个字段、值是多少。</exception>
        public ConditionDef(EConditionEvent eventType, int targetId, int requiredCount)
        {
            // 功能：造一条条件定义，并在赋值之前把三个字段校验一遍。
            // 入参：eventType 事件类型；targetId 目标编号（0 = 任意）；requiredCount 需要次数（≥ 1）。
            // 返回：无，构造出不可变实例。
            // 步骤：
            //   1) 先调 Validate 拿原因，校验规则与配置表共用同一份，避免出现两套判断
            //   2) 有原因就抛 ArgumentException，消息里带上字段名与当前值
            //   3) 校验通过后再赋值，非法值不会有机会留在实例里
            // 注意：必须当场校验 —— requiredCount = 0 的条件会立刻满足（0 >= 0），表现是「刚接到任务就完成了」，这种 bug 的现场离原因极远。
            // 注意：targetId = 0 表示任意目标是刻意约定 —— 配置表每个单元格都必须填值，而主键永远 ≥ 1，用 0 不会和真实编号撞车。
            // 上游：客户端 QuestRuntime / AchievementRuntime 读配置表、Editor 调试窗、服务端 QuestTables；测试直接构造
            // 下游：Validate
            string error = Validate(eventType, targetId, requiredCount);

            if (error != null)
            {
                throw new ArgumentException(error);
            }

            m_eventType = eventType;
            m_targetId = targetId;
            m_requiredCount = requiredCount;
        }

        /// <summary>事件类型，取值范围是 EConditionEvent 的合法成员（0..ConditionEvents.Count-1）。</summary>
        public EConditionEvent EventType
        {
            get { return m_eventType; }
        }

        /// <summary>目标编号；0 表示任意目标，其余是配置表主键（≥ 1）。</summary>
        public int TargetId
        {
            get { return m_targetId; }
        }

        /// <summary>需要次数，≥ 1；进度到达这个次数即判定为达成。</summary>
        public int RequiredCount
        {
            get { return m_requiredCount; }
        }

        /// <summary>目标是不是「任意」；等价于 TargetId 为 0。</summary>
        public bool IsAnyTarget
        {
            get { return m_targetId == 0; }
        }

        /// <summary>校验三个字段，构造与配置表共用这一份规则。</summary>
        /// <param name="eventType">事件类型，必须是合法成员。</param>
        /// <param name="targetId">目标编号；0 = 任意目标，负数非法。</param>
        /// <param name="requiredCount">需要次数，必须 ≥ 1。</param>
        /// <returns>合法时返回 null；不合法时返回一句可直接显示的中文原因。</returns>
        public static string Validate(EConditionEvent eventType, int targetId, int requiredCount)
        {
            // 功能：校验事件类型、目标编号与需要次数，返回第一处不合法的原因。
            // 入参：eventType 事件类型；targetId 目标编号（0 = 任意，负数非法）；requiredCount 需要次数（≥ 1）。
            // 返回：合法返回 null；不合法返回一句人话，说清是哪个字段、值是多少。
            // 步骤：
            //   1) 先查事件类型是否已知：强转出来的假值（例如 42）C# 不报错，会安静地匹配不到任何条件
            //   2) 再查目标编号非负：负数没有约定含义，0 才是「任意目标」
            //   3) 最后查需要次数 ≥ 1：0 会让条件立刻达成
            // 注意：不要改成「用的时候再校验」—— requiredCount = 0 造成的「刚接任务就完成」很难往配置上想，报错必须发生在造出来的那一刻。
            // 上游：ConditionDef 构造、ConditionDef.TryCreate、配置表逐行解析
            // 下游：ConditionEvents.IsDefined
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

        /// <summary>不抛异常地构造一条条件定义，供逐行读配置表使用。</summary>
        /// <param name="eventType">事件类型。</param>
        /// <param name="targetId">目标编号；0 = 任意目标。</param>
        /// <param name="requiredCount">需要次数，≥ 1。</param>
        /// <param name="def">成功时是造出来的定义，失败时是 default。</param>
        /// <param name="error">失败原因；成功时为 null。</param>
        /// <returns>成功返回 true；失败返回 false 且不抛异常。</returns>
        public static bool TryCreate(EConditionEvent eventType, int targetId, int requiredCount,
                                     out ConditionDef def, out string error)
        {
            // 功能：先校验再构造，把「配置写错」变成返回值而不是异常。
            // 入参：eventType 事件类型；targetId 目标编号（0 = 任意）；requiredCount 需要次数（≥ 1）；def 输出定义；error 输出失败原因。
            // 返回：成功 true 且 def 有效、error 为 null；失败 false 且 def 为 default、error 是原因。
            // 步骤：
            //   1) 先调 Validate，有原因就直接返回 false，不进入构造
            //   2) 失败时把 def 置 default，避免调用方误用半成品
            //   3) 成功路径才 new；构造内部会再走同一份 Validate，不会出现两套规则
            // 上游：QuestRuntime / AchievementRuntime 逐行读配置表、服务端 QuestTables、测试
            // 下游：Validate、构造
            error = Validate(eventType, targetId, requiredCount);

            if (error != null)
            {
                def = default(ConditionDef);
                return false;
            }

            def = new ConditionDef(eventType, targetId, requiredCount);
            return true;
        }

        /// <summary>判断这条定义是否匹配某次事件；本条目标为 0 时视作任意目标。</summary>
        /// <param name="eventType">实际发生的事件类型。</param>
        /// <param name="targetId">实际发生的事件目标编号。</param>
        /// <returns>事件类型相同，且本条目标为任意或与传入编号相同时返回 true。</returns>
        public bool Matches(EConditionEvent eventType, int targetId)
        {
            // 功能：判断「某个目标身上发生的某件事」算不算这条条件。
            // 入参：eventType 实际事件类型；targetId 实际目标编号（0 表示没有具体目标）。
            // 返回：事件类型相同、且本条目标为 0 或与传入编号相等时为 true。
            // 步骤：
            //   1) 先比事件类型，必须一模一样，不做模糊匹配
            //   2) 再看目标：本条为 0 表示任意目标，直接算匹配；否则要求编号精确相等
            // 注意：targetId 传 0 只匹配「任意目标」的条件；具体编号的条件（如 6001）不会被 0 匹配到。
            // 上游：ConditionTracker.Notify 遍历登记表时逐条调用
            // 下游：无
            return m_eventType == eventType && (m_targetId == 0 || m_targetId == targetId);
        }

        /// <summary>拼一句人话，用于报错、日志与 UI 追踪条。</summary>
        /// <returns>形如「击杀怪物 ×3（目标 6001）」的文本；目标为任意时写「任意目标」。</returns>
        public string Describe()
        {
            // 功能：把事件类型、需要次数与目标拼成一句中文描述。
            // 入参：无
            // 返回：形如「击杀怪物 ×3（目标 6001）」的文本；目标为 0 时写「任意目标」。
            // 步骤：
            //   1) 先把目标编号翻成人话，0 单独写「任意目标」，避免显示成「目标 0」
            //   2) 事件名交给 ConditionEvents.Describe，描述口径只维护一处
            //   3) 按「事件 ×次数（目标）」的固定顺序拼接
            // 上游：ConditionTracker.Describe、QuestRuntime 的配置错误提示、UI 追踪条
            // 下游：ConditionEvents.Describe
            string target = m_targetId == 0 ? "任意目标" : "目标 " + m_targetId;
            return ConditionEvents.Describe(m_eventType) + " ×" + m_requiredCount + "（" + target + "）";
        }

        /// <summary>等价于 Describe，便于日志与调试输出直接打印。</summary>
        public override string ToString()
        {
            // 功能：直接转发 Describe，保证 ToString 与界面文案是同一句。
            return Describe();
        }
    }
}
