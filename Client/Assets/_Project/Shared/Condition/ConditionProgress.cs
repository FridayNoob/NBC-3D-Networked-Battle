// 这个文件里有：
//   ConditionProgress（结构体）：一条条件的进度（当前值 + 需求值），造出来就把当前值钳在需求值以内。

// 注意：顶部关闭 nullable 是让双端看到同一套规则 —— Unity 侧（NBC.Shared.asmdef）没开可空引用类型，
//     服务端（NBC.Shared.csproj → Server\Directory.Build.props）开了；不关的话服务端会多出一批 CS86xx，
//     而若为了消警告去写可空注解，Unity 侧又会报 CS8632，两头都不干净。
// 注意：代价是本目录放弃可空静态检查，「可能为 null」靠 XML 注释与运行期校验兜住；将来两端统一开启可空，删掉那条指令即可。

#nullable disable

namespace NBC.Shared.Condition
{
    // 主要功能：表示一条条件的进度 —— 当前累计值与需求值，并回答「达成没有、还差多少」。
    // 主要数据成员：m_current、m_required（业务次数，构造后 0 ≤ m_current ≤ m_required）。
    // 主要外部方法：Current / Required / IsMet / Remaining 四个只读属性，ToString 出「当前/需求」文本。
    // 谁在用它：ConditionTracker 造出来再回调给上层；QuestRuntime / AchievementRuntime 在进度缺失时补 0；
    //     服务端 AchievementAuthority 转成下发条目；Editor 调试窗口与 EditMode 测试读取显示与断言。
    // 注意：构造即钳位，进度只有 0..Required 这么多种取值，「跨过需求线」只可能发生一次，
    //     所以不需要「已达成」标志位；不钳位的话每次推进都得额外记一个标志位，数值也没有上界（存库与下发都浪费）。
    //     代价是丢掉超额完成的信息（打到 999 也只记 Required），将来要做阶梯奖励，得另外记原始计数。
    // 注意：数量全程用 int，不碰浮点，客户端与服务端算出来逐位一致。
    /// <summary>一条条件的进度：当前累计值 + 需求值，构造时把当前值钳在需求值以内。</summary>
    public readonly struct ConditionProgress
    {
        // 已累计的数量，构造时钳在 0..m_required；单位是业务次数（不是毫米 / tick）
        private readonly int m_current;

        // 需要凑够的数量，构造时负数按 0；单位是业务次数，取值 0..int.MaxValue
        private readonly int m_required;

        // 功能：造一条进度，两个值先各自按 0 兜底，再把当前值钳到需求值以内。
        // 入参：current 已累计数量（负数按 0）；required 需要数量（负数按 0）
        // 返回：无
        // 步骤：
        //   1) 先把 required 的负数兜成 0 —— 后面那一步钳位要有上界
        //   2) 再把 current 的负数兜成 0 —— 避免出现负进度
        //   3) 最后把 current 压到 required 以内；达成只回调一次，靠的就是这一步
        // 上游：ConditionTracker 登记、查询与累加时构造；QuestRuntime / AchievementRuntime 补 0 进度时构造
        // 下游：无（只写本结构的两个字段）
        /// <summary>造一条进度：两个参数先各自按 0 兜底，再把当前值钳到需求值以内。</summary>
        /// <param name="current">已累计的数量；负数按 0 处理，超过 required 时钳到 required。</param>
        /// <param name="required">需要的数量；负数按 0 处理。</param>
        public ConditionProgress(int current, int required)
        {
            int safeRequired = required < 0 ? 0 : required;
            int safeCurrent = current < 0 ? 0 : current;

            m_required = safeRequired;
            m_current = safeCurrent > safeRequired ? safeRequired : safeCurrent;
        }

        // 功能：读已累计的数量。
        /// <summary>已累计的数量，构造后落在 0..Required。</summary>
        public int Current
        {
            get { return m_current; }
        }

        // 功能：读需要的数量。
        /// <summary>需要的数量；构造时负数按 0。</summary>
        public int Required
        {
            get { return m_required; }
        }

        // 功能：判达成没有 —— 当前值够到需求值就算达成。
        // 注意：default(ConditionProgress) 的需求值是 0，IsMet 会返回 true；要判「有没有这个条件」得看
        //     TryGetProgress 的返回值，别拿 IsMet 当存在性检查。
        /// <summary>达成了没有；需求值为 0 时任何进度都算达成。</summary>
        public bool IsMet
        {
            get { return m_current >= m_required; }
        }

        // 功能：算距离达成还差多少；构造时已钳位，所以不会出现负数。
        /// <summary>还差多少；已达成时为 0。</summary>
        public int Remaining
        {
            get { return m_required - m_current; }
        }

        // 功能：拼成「当前/需求」的文本，供日志、报错与调试面板显示。
        /// <summary>转成「当前/需求」的文本，供日志与报错显示。</summary>
        public override string ToString()
        {
            return m_current + "/" + m_required;
        }
    }
}
