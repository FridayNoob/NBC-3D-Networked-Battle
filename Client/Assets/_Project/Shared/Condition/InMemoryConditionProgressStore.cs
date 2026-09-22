// ============================================================================
//  InMemoryConditionProgressStore —— 进度的内存实现（M2 用这个；M4 换数据库实现）
//  项目：3D联网战斗Demo   对应：M2-A1
//
//  ---------------------------------------------------------------------------
//  它的两个身份（都要说清楚，否则容易被误用）
//  ---------------------------------------------------------------------------
//  ① **单机/测试**：M2 的任务进度就存在这里，进程一关就没了 ——
//     这在 M2 是**有意**的（M2 的验收是"单机一局里能完成任务"，
//     持久化明写是 M4 的事，见 `Docs\20` §三 M4 行）。
//  ② **将来数据库实现的"参照实现"**：语义以它为准（读不到给 0、钳位由 Tracker 负责），
//     换实现时对着它逐条对答案，就不会出现"换库之后行为变了"。
//
//  ⚠️ 它**不做钳位** —— 钳位是 `ConditionTracker` 的职责（那儿才知道需求值）。
//     存放处只管"把数存下来"，越权做业务判断是典型的职责混乱。
// ============================================================================

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
    /// <summary>把条件进度放在字典里的实现（M2 默认用这个）。</summary>
    public sealed class InMemoryConditionProgressStore : IConditionProgressStore
    {
        /// <summary>条件编号 → 已累计数量。</summary>
        private readonly Dictionary<int, int> m_progress = new Dictionary<int, int>();

        /// <summary>当前有多少条记录（调试/测试用）。</summary>
        public int Count
        {
            get { return m_progress.Count; }
        }

        /// <summary>读一个条件的进度；没记录过时返回 0。</summary>
        /// <param name="conditionKey">条件行的编号。</param>
        /// <returns>已累计的数量。</returns>
        public int GetProgress(int conditionKey)
        {
            int value;

            // ⚠️ 这里**不是**在掩盖错误：没记录 = 还没开始做这个条件 = 0，是正常状态
            return m_progress.TryGetValue(conditionKey, out value) ? value : 0;
        }

        /// <summary>写一个条件的进度。</summary>
        /// <param name="conditionKey">条件行的编号。</param>
        /// <param name="value">已累计的数量。</param>
        public void SetProgress(int conditionKey, int value)
        {
            m_progress[conditionKey] = value;
        }

        /// <summary>删掉一个条件的进度。</summary>
        /// <param name="conditionKey">条件行的编号。</param>
        /// <returns>本来有记录、真的删掉了才返回 true。</returns>
        public bool Remove(int conditionKey)
        {
            return m_progress.Remove(conditionKey);
        }

        /// <summary>清空全部记录（测试收尾、切账号用）。</summary>
        public void Clear()
        {
            m_progress.Clear();
        }
    }
}
