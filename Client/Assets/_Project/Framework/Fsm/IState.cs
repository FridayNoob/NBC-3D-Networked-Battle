// ============================================================================
//  NBC.Framework.Fsm · 状态接口
//  对应需求：FSM-02（`OnEnter` / `OnUpdate` / `OnExit` / `OnEvent` / `CanEnterFrom`）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 一处**刻意偏离需求原文**的地方（`OnEvent` 加了返回值）
//  ---------------------------------------------------------------------------
//  FSM-02 原文写的是 `OnEvent(e)`，没有返回值。我把它做成 **`bool OnEvent(EventId)`**：
//      true  = "我处理了，不用再往上传"
//      false = "跟我无关"
//
//  **为什么**：分层状态机需要知道"事件有没有被下面的层消化掉"。
//  没有这个返回值的话，两级只能**都收到同一个事件**，
//  于是"按下闪避"可能被下层和上层各处理一次 —— 那是一种很难查的重复触发。
//
//  ⚠️ 这是**对需求签名的扩展，不是偷偷改**。如果不同意，把返回值去掉即可，
//     代价是分层派发只能改成"广播给两级"。
//
//  ---------------------------------------------------------------------------
//  泛型 S 是什么
//  ---------------------------------------------------------------------------
//  `S` 是**状态标识**，通常是游戏层自己声明的 `enum`：
//      enum PlayerState { Idle, Run, Attack1, ... }
//      var machine = new StateMachine<PlayerState>();
//
//  用泛型 + enum 而不是字符串，理由和 A3 一样：**拼错编译不过**。
//  而且 enum 是值类型，当字典键不会每次分配。
// ============================================================================

using NBC.Framework;

namespace NBC.Framework.Fsm
{
    /// <summary>
    /// 一个状态。
    /// <para>⚠️ **纯 C#，不是 MonoBehaviour**（FSM-09）—— 服务端要能复用同一份代码。</para>
    /// </summary>
    /// <typeparam name="S">状态标识类型（通常是游戏层声明的 enum）。</typeparam>
    public interface IState<S>
    {
        /// <summary>状态的显示名（调试面板 / 转移记录用）。</summary>
        string Name { get; }

        /// <summary>
        /// 进入这个状态时调用。
        /// </summary>
        /// <param name="previous">从哪个状态切过来的。</param>
        void OnEnter(S previous);

        /// <summary>
        /// 每次推进时调用。
        /// <para>
        /// ⚠️ **不许用 `Time.deltaTime`**（那会引入依赖真实时间的不确定性）。
        /// "持续 N 秒"请换算成帧数，用帧计数器判断。
        /// </para>
        /// </summary>
        /// <param name="tick">推进信息（逻辑帧号 / 相隔帧数）。</param>
        void OnUpdate(in StateTick tick);

        /// <summary>
        /// 离开这个状态时调用。
        /// </summary>
        /// <param name="next">要切到哪个状态。</param>
        void OnExit(S next);

        /// <summary>
        /// 收到一个事件。
        /// </summary>
        /// <param name="id">事件标识（A3 的强类型 <see cref="EventId"/>，拼错编译不过）。</param>
        /// <returns>
        /// true 表示"我处理了"。
        /// <para>
        /// ⚠️ 这个返回值是为**分层状态机**准备的：下层返回 false 时，上层才有机会处理。
        /// 单层状态机不用管它（返回 false 也不影响）。
        /// </para>
        /// </returns>
        bool OnEvent(EventId id);

        /// <summary>
        /// 能不能从 <paramref name="from"/> 切进**我**这个状态。
        /// <para>
        /// ⚠️ 问的是**目标状态**（"你允许从那边过来吗"），不是当前状态。
        /// 这样"谁能打断谁"的规则集中在**目标状态**里，而不是散落在每个状态身上。
        /// </para>
        /// </summary>
        /// <param name="from">来源状态。</param>
        /// <returns>允许返回 true。</returns>
        bool CanEnterFrom(S from);
    }
}
