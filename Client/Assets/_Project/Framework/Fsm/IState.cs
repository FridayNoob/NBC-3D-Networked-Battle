// 这个文件里有：
//   IState（接口）：一个状态的契约，状态机只经它进入、推进、离开、派发事件与做准入判断

#nullable disable
// 注意：双端共用（服务端也编它，见 Server\NBC.Server.Game.csproj）：服务端开了可空、Unity 没开，
//   这里统一关掉，与 Shared\ 是同一条理由。
using NBC.Framework;

namespace NBC.Framework.Fsm
{
    // 主要功能：定义「一个状态」的契约 —— 状态机只经这六个成员驱动它，不认具体状态类。
    // 主要数据成员：无字段；唯一的成员数据是 Name（状态显示名，调试面板与转移记录用），其余状态数据由实现类自己持有。
    // 主要外部方法：OnEnter / OnUpdate / OnExit / OnEvent / CanEnterFrom，以及只读属性 Name。
    // 谁在用它：StateMachine 与 HierarchicalStateMachine 负责调用；StateBase 是全空实现的默认基类；这是全项目唯一一套通用状态机，但目前只有 EditMode 的 StateMachineTests / HierarchicalStateMachineTests 实现它，生产代码还没有状态类。
    // 注意：接口刻意做成纯 C#（实现类不能是 MonoBehaviour）—— 服务端也要编同一份源码，状态机靠外部注入的逻辑帧推进，不碰 Unity 的时间。
    /// <summary>一个状态的契约：状态机只经这六个成员驱动它；实现类必须是纯 C#，服务端与客户端编同一份源码。</summary>
    /// <typeparam name="S">状态标识类型，通常用游戏层自己的 enum；拼错编译不过，当字典键也不分配。</typeparam>
    public interface IState<S>
    {
        // 功能：给调试面板与转移记录一个可读名字，不参与任何状态比较。
        // 返回：取值为非空字符串，建议用类型名或中文状态名；转移历史只存这个名字并按容量只保留最近若干条，所以要能长期看懂
        /// <summary>状态显示名，调试面板与转移记录直接显示；不参与状态判断。</summary>
        string Name { get; }

        // 功能：让实现类在进入状态时重置自己的数据，并可按来源状态决定表现。
        // 入参：previous 是切过来的来源状态；Start 首次进入时等于自己
        // 返回：无
        // 步骤：1) 状态机先调旧状态的 OnExit(next)，再改当前状态，最后调本方法 —— 离开时旧状态还是「当前」
        //   2) 所以进入时当前状态已经指向自己，可按 previous 决定「从哪来」的表现（例如从 Run 进来就不重播待机）
        //   3) 只做本状态初始化；要请求转移请留给 OnUpdate 或外部驱动
        // 上游：StateMachine.Start / Apply，HierarchicalStateMachine.Start / Apply
        // 下游：实现类（StateBase 的默认空实现，或游戏层自己的状态类）
        // 注意：状态机不可重入 —— 在 OnEnter / OnExit 里再请求转移不会立刻执行，只会排队到本次转移完整结束后；连续多次请求只保留最后一次，后面的覆盖前面的。
        /// <summary>进入状态：此时当前状态已改成自己，旧状态也收完了 OnExit。</summary>
        /// <param name="previous">来源状态；首次进入时是自己。</param>
        void OnEnter(S previous);

        // 功能：每个逻辑帧推进一次，实现类在这里跑自己的计时与状态逻辑。
        // 入参：tick 逻辑帧号与相隔帧数；没有秒，也没有真实时间
        // 返回：无
        // 步骤：1) 状态机每 Tick(frame) 先把帧号差成相隔帧数，再调本方法 —— 时间只有一个来源
        //   2) 单层直接调当前状态；分层先调下层、再调上层，两层收到同一份 tick
        //   3) 实现类按 tick 里的帧号判断「持续 N 帧」，不用真实时间
        // 上游：StateMachine.Tick，HierarchicalStateMachine.Tick
        // 下游：实现类（StateBase 的默认空实现，或游戏层自己的状态类）
        // 注意：不许用 Time.deltaTime 或协程计时 —— 那会让推进依赖真实时间，客户端与服务端算不出同一结果；单机看不出来，一进帧同步就是两端不同步。
        /// <summary>推进一帧：用 tick 里的帧号判断时长，不要读真实时间。</summary>
        /// <param name="tick">逻辑帧号与相隔帧数；帧号只增不减，倒退会在状态机里抛异常。</param>
        void OnUpdate(in StateTick tick);

        // 功能：让实现类在离开状态时收尾，清掉本状态的临时数据。
        // 入参：next 是接下来要去的目标状态；此时当前状态仍是自己
        // 返回：无
        // 步骤：1) 状态机在改当前状态之前调本方法 —— 这里读当前状态还是自己
        //   2) 分层切换时先退下层、再退上层；进入反过来，先进上层、再进下层（只换一层时只动那一层）
        //   3) 只做清理，不要假设新状态已经 OnEnter 过
        // 上游：StateMachine.Apply，HierarchicalStateMachine.Apply
        // 下游：实现类（StateBase 的默认空实现，或游戏层自己的状态类）
        // 注意：清理要彻底 —— 同一个状态对象会被反复进入，残留的计时或引用会带到下一次进入；在这里请求转移同样只会排队，不会立刻执行。
        /// <summary>离开状态：当前状态仍是自己，新状态还没 OnEnter。</summary>
        /// <param name="next">目标状态；指的是同一层的下一个状态。</param>
        void OnExit(S next);

        // 功能：把一个事件交给本状态处理，并回答自己有没有消化掉。
        // 入参：id 事件标识，是强类型 EventId，拼错编译不过
        // 返回：true 表示「我处理了」；false 表示「跟我无关」
        // 步骤：1) 单层状态机直接把事件转给当前状态，返回值原样透出
        //   2) 分层状态机先问下层，下层返回 false 才问上层 —— 避免同一个事件被两层各处理一次
        // 上游：StateMachine.SendEvent，HierarchicalStateMachine.SendEvent
        // 下游：实现类（StateBase 的默认空实现，或游戏层自己的状态类）
        // 注意：返回值是分层派发的关键 —— 下层返回 true 就停、不再往上层传；没有它，两层只能都收到同一个事件，「按下闪避」可能被下层和上层各处理一次。单层状态机不用管返回值，一直返回 false 也不影响。
        /// <summary>处理一个事件；返回 true 表示已消化，分层派发就不再往上层传。</summary>
        /// <param name="id">事件标识；无效标识在进入本方法之前就会被状态机拒绝。</param>
        /// <returns>true 表示本状态处理了该事件；false 表示与本状态无关。</returns>
        bool OnEvent(EventId id);

        // 功能：回答「允不允许从 from 切进我」，是状态转移唯一的准入检查。
        // 入参：from 来源状态，指的是旧状态，不是当前状态
        // 返回：true 允许转移；false 拒绝，状态机留在原状态并触发 TransitionRejected
        // 步骤：1) 状态机在切换前问目标状态的 CanEnterFrom，而不是问当前状态放不放行 —— 「谁能打断谁」的规则因此集中在目标状态
        //   2) 换上层问新上层，只换下层问新下层，两层各管各的
        //   3) 允许才执行 OnExit / OnEnter；拒绝就停在原地
        // 上游：StateMachine.Apply（TryChangeState 时），HierarchicalStateMachine.Apply（换上层或只换下层时）；Start 首次进入与 ForceChangeState / ForceChange 都跳过本方法
        // 下游：实现类（StateBase 默认允许，或游戏层自己的状态类）
        // 注意：规则集中在目标状态里，别写进来源状态 —— 忘了覆写会退化成谁都能打断，而且不报错（StateBase 默认返回 true）。
        /// <summary>准入判断：我是目标状态，回答允不允许从 from 进来。</summary>
        /// <param name="from">来源状态；指的是旧状态，不是当前状态。</param>
        /// <returns>true 允许转移；false 拒绝，状态机保持原状态。</returns>
        bool CanEnterFrom(S from);
    }
}
