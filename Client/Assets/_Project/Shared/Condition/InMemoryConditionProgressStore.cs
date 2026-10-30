// 这个文件里有：
//   InMemoryConditionProgressStore（类）：把条件进度放在内存字典里的存放处实现，读不到给 0、写入即生效、进程一关就丢。

using System.Collections.Generic;

// 注意：#nullable disable 是让双端看到同一套规则 —— Unity 侧（NBC.Shared.asmdef）没开可空引用类型，
//     服务端（Server\Directory.Build.props）开了；不关的话服务端会多出一批 CS86xx，
//     而为消警告去写可空注解（如 string?），Unity 侧又会报 CS8632，两头都不干净。
// 注意：代价是本目录放弃可空静态检查，「可能为 null」靠 XML 注释与运行期校验兜住；
//     将来两端统一开启可空，把这一行删掉即可。

#nullable disable

namespace NBC.Shared.Condition
{
    // 主要功能：把条件进度存在内存字典里 —— 读不到给 0、写入即生效、进程一关就丢。
    // 主要数据成员：m_progress（条件编号 → 已累计数量）。
    // 主要外部方法：GetProgress / SetProgress / Remove（IConditionProgressStore 那三件）、Clear（清空全部）、Count（记录条数）。
    // 谁在用它：BattleSession 与 Editor 的 NetDebugWindow 造 ConditionTracker 时传进来；EditMode 测试也直接拿它断言进度。
    // 注意：本类不做钳位 —— 钳位是 ConditionTracker 的职责（那儿才知道需求值），存放处越权做业务判断就是职责混乱。
    // 注意：M2 的进度就存在这个进程里，一关就没（单机验收有意如此，持久化归 M4），别拿它当持久化。
    // 注意：换落库实现时以这里的语义逐条对答案（读不到给 0、不钳位），否则会出现「换库之后行为变了」。
    /// <summary>把条件进度放在字典里的内存实现；读不到给 0，且不做钳位。</summary>
    public sealed class InMemoryConditionProgressStore : IConditionProgressStore
    {
        // 条件编号（配置表主键）→ 已累计数量；单位随条件本身（次数 / 件数），正常值在 0..需求值，这个范围由 ConditionTracker 保证
        private readonly Dictionary<int, int> m_progress = new Dictionary<int, int>();

        // 功能：读当前有多少条记录，等于写过多少种不同的条件（调试与测试用）。
        /// <summary>当前记录条数：写过几种不同的条件。</summary>
        public int Count
        {
            get { return m_progress.Count; }
        }

        // 功能：按条件编号取已累计数量，没有记录就给 0。
        // 入参：conditionKey 条件编号（配置表主键）；任意 int 都合法，查不到不抛异常
        // 返回：已累计的数量；0 既可能是「还没开始做」也可能是「存过 0」，本类不区分
        // 步骤：
        //   1) 一次 TryGetValue 取值，省掉「先 ContainsKey 再索引」的两次哈希
        //   2) 查不到直接给 0 —— 没记录只是还没开始做这个条件，是正常状态，不是要掩盖错误
        // 上游：ConditionTracker 的 Register / Notify / IsMet / TryGetProgress / MetCount / Describe；EditMode 测试直接查
        // 下游：m_progress
        /// <summary>读一个条件的进度；没记录过时返回 0，不是错误。</summary>
        /// <returns>已累计的数量；0 既可能是没做过，也可能是存过 0。</returns>
        public int GetProgress(int conditionKey)
        {
            int value;

            return m_progress.TryGetValue(conditionKey, out value) ? value : 0;
        }

        // 功能：把一个条件的数量写进字典 —— 没记录就新建，已有就覆盖（不做钳位，越界值也照存）。
        /// <summary>写一个条件的进度；越界值也照存，钳位不在这里做。</summary>
        public void SetProgress(int conditionKey, int value)
        {
            m_progress[conditionKey] = value;
        }

        // 功能：删掉一个条件的进度，返回是否本来有记录 —— 真的删掉了才为 true。
        /// <summary>删掉一个条件的进度（重置任务 / 清档用）。</summary>
        /// <returns>本来有记录、真的删掉了返回 true。</returns>
        public bool Remove(int conditionKey)
        {
            return m_progress.Remove(conditionKey);
        }

        // 功能：清空全部记录，让同一进程里的下一局或下一次测试从干净状态开始。
        /// <summary>清空全部记录，供测试收尾与切账号使用。</summary>
        public void Clear()
        {
            m_progress.Clear();
        }
    }
}
