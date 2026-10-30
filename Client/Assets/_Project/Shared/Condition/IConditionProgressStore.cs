// 这个文件里有：
//   IConditionProgressStore（接口）：条件进度的存放处，按条件编号读写进度与删除记录

// 注意：文件顶部的 nullable 关闭指令让 Unity（未开可空）与服务端（已开可空）按同一套规则编译；不关掉会分别报 CS86xx / CS8632。代价是本目录放弃可空静态检查，将来两端统一开启后删掉它。
#nullable disable

namespace NBC.Shared.Condition
{
    // 主要功能：定义条件进度的存放处，按条件编号读取、写入与删除进度记录，双端共用同一份契约。
    // 主要数据成员：无（纯接口；刻意只用 int 且不带 playerId，避免把实现绑死在某一端；服务端实现把玩家归属绑在构造上）。
    // 主要外部方法：GetProgress / SetProgress / Remove。
    // 谁在用它：ConditionTracker 读写进度；客户端与 EditMode 测试用 InMemoryConditionProgressStore，服务端用 CachingConditionProgressStore。
    /// <summary>条件进度的存放处：以条件行的编号为键，读写累计数量。</summary>
    public interface IConditionProgressStore
    {
        // 功能：读一个条件的累计进度，供条件系统判定是否达成。
        // 入参：conditionKey 条件行的编号（配置表主键，按配置取值）。
        // 返回：已累计的数量；没记录过时返回 0。
        // 步骤：本方法只有声明；实现方查记录，查不到按 0 返回，避免把正常状态当异常。
        // 上游：ConditionTracker 的登记、查询与 Notify 累加前读取。
        // 下游：具体实现（InMemoryConditionProgressStore / CachingConditionProgressStore）。
        // 注意：没记录是正常状态（第一次见到这个条件）；真正的异常（连接断了）由实现自己抛，不要混进返回值。
        /// <summary>读一个条件的进度；没记录过时返回 0，没记录是正常状态而不是错误。</summary>
        int GetProgress(int conditionKey);

        // 功能：写一个条件的累计进度，供条件系统保存推进结果。
        // 入参：conditionKey 条件行的编号；value 已累计的数量（绝对值，不是增量）。
        // 返回：无
        // 步骤：本方法只有声明；实现方用 value 覆盖旧值，避免重复写入被叠成增量。
        // 上游：ConditionTracker 在登记清零与 Notify 累加后调用。
        // 下游：具体实现（InMemoryConditionProgressStore / CachingConditionProgressStore）。
        // 注意：value 是绝对值，钳位由 ConditionTracker 负责；存放处不知道需求值，越权钳位会让职责混乱。
        /// <summary>写一个条件的进度；value 是绝对值，覆盖旧值，钳位由 ConditionTracker 负责。</summary>
        void SetProgress(int conditionKey, int value);

        // 功能：删掉一个条件的进度记录，供重置任务或清档使用。
        // 入参：conditionKey 条件行的编号。
        // 返回：本来有记录、真的删掉了才返回 true；本来没有记录返回 false。
        // 步骤：本方法只有声明；实现方删除记录，并按删除前是否存在决定返回值。
        // 上游：重置任务 / 清档流程；当前仓库内没有正式调用点。
        // 下游：具体实现（InMemoryConditionProgressStore / CachingConditionProgressStore）。
        // 注意：删除必须同步到持久化层（服务端用 0 标脏），否则内存删了库里还在，下次登录进度会复活。
        /// <summary>删掉一个条件的进度；本来有记录才返回 true。</summary>
        bool Remove(int conditionKey);
    }
}
