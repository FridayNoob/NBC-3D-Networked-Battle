// ============================================================================
//  ConditionProgress —— 一条条件的「进度」（当前值 + 需求值）
//  项目：3D联网战斗Demo   对应：M2-A1（条件系统）
//
//  ---------------------------------------------------------------------------
//  为什么进度要「钳位」（当前值不超过需求值）
//  ---------------------------------------------------------------------------
//  直觉写法是把击杀数一直累加（打 100 只就是 100），
//  但那样会带来两个问题：
//
//    ① **达成回调会重复触发**：每次 `Notify` 都要判断"这次跨过线了吗"，
//       得额外存一个"已达成"标志位 —— 多一个状态就多一个能不同步的地方
//       （尤其是进度要存进数据库的时候：进度与标志位可能只写进去一个）。
//
//    ② **数值没有上界**：一个"击杀 3 只"的条件，进度可能是 999999。
//       存库、下发给客户端、进 UI 都是浪费，而且**溢出**成了现实风险。
//
//  钳位之后：
//    · 进度只有 `0..requiredCount` 这么多可能的值
//    · "跨过线的那一刻" = `本次之后 == requiredCount && 本次之前 < requiredCount`
//      —— 于是**达成天然只回调一次**，不需要任何标志位
//    · 库里那一列的含义从"打了多少只"变成"这个条件做到哪一步了"，更贴需求
//
//  ⚠️ 代价（如实记）：**钳位会丢掉"超额完成"的信息**。
//     如果将来要做"击杀 10 只额外奖励"这种阶梯奖励，那时得另外记原始计数。
//     M2 的任务没有这个需求，所以选简单的那个。
// ============================================================================

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
    /// <summary>一条条件的进度。</summary>
    public readonly struct ConditionProgress
    {
        /// <summary>已累计的数量（**已钳位**，不会超过 <see cref="Required"/>）。</summary>
        private readonly int m_current;

        /// <summary>需要的数量。</summary>
        private readonly int m_required;

        /// <summary>造一个进度。</summary>
        /// <param name="current">已累计数量（负数会被当成 0）。</param>
        /// <param name="required">需要数量（负数会被当成 0）。</param>
        public ConditionProgress(int current, int required)
        {
            int safeRequired = required < 0 ? 0 : required;
            int safeCurrent = current < 0 ? 0 : current;

            m_required = safeRequired;
            m_current = safeCurrent > safeRequired ? safeRequired : safeCurrent;
        }

        /// <summary>已累计的数量（0 ~ <see cref="Required"/>）。</summary>
        public int Current
        {
            get { return m_current; }
        }

        /// <summary>需要的数量。</summary>
        public int Required
        {
            get { return m_required; }
        }

        /// <summary>达成了没有。</summary>
        public bool IsMet
        {
            get { return m_current >= m_required; }
        }

        /// <summary>还差多少（已达成时为 0）。</summary>
        public int Remaining
        {
            get { return m_required - m_current; }
        }

        /// <summary>转成一句人话。</summary>
        /// <returns>例如「2/3」。</returns>
        public override string ToString()
        {
            return m_current + "/" + m_required;
        }
    }
}
