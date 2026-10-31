// 这个文件里有：
//   StateBase<S>（抽象类）：IState<S> 的全空实现基类，子类只覆写自己关心的状态回调

#nullable disable
// 注意：双端共用（服务端也编它，见 Server\NBC.Server.Game.csproj）：服务端开了可空、Unity 没开，
//   这里统一关掉，与 Shared\ 是同一条理由。
using NBC.Framework;

namespace NBC.Framework.Fsm
{
    // 主要功能：给 IState<S> 提供全空实现的基类，让只关心一两个回调的状态不必写满六个空方法。
    // 主要数据成员：无字段；显示名由 Name 属性按运行时类型名给出。
    // 主要外部方法：Name / OnEnter / OnUpdate / OnExit / OnEvent / CanEnterFrom / ToString（均为 public virtual）。
    // 谁在用它：目前只有 EditMode 测试的 ProbeState<S> 继承它；两个状态机只依赖 IState<S>，不依赖本类。
    // 注意：基类不是强制的 —— 状态要做成 struct、或已经继承别的基类时，直接实现 IState<S> 即可，不被本类绑住。
    // 注意：状态机不可重入 —— 在 OnEnter / OnExit 里请求转移只会被排队，等本次转移完整结束后才执行，别假设当场生效。
    // 注意：CanEnterFrom 默认放行任何来源，是为了让不挑来源的状态少写代码；代价是忘了覆写不会报错，只会表现为任何状态都能打断它，状态清单里的「可被打断」项必须和代码对上。
    /// <summary>状态基类：所有方法都有空实现，子类只覆写关心的。</summary>
    /// <typeparam name="S">状态标识类型（通常是游戏层声明的 enum）。</typeparam>
    public abstract class StateBase<S> : IState<S>
    {
        /// <summary>状态显示名；默认用运行时类型名，子类可改成更好读的名字。</summary>
        public virtual string Name
        {
            get { return GetType().Name; }
        }

        /// <summary>进入状态时的入口；默认空实现。</summary>
        /// <param name="previous">来源状态；Start 首次进入时是初始状态自身。</param>
        public virtual void OnEnter(S previous)
        {
            // 功能：状态被切入时的回调，本类什么都不做。
            // 入参：previous 来源状态标识，本类不使用。
            // 返回：无。
            // 步骤：留空交给子类覆写 —— 基类不预设初始化顺序，子类只写自己关心的那一两个回调。
            // 上游：StateMachine / HierarchicalStateMachine 的 Start 与转移流程
            // 下游：无（子类覆写后自行决定）
        }

        /// <summary>每逻辑帧推进一次；默认空实现。</summary>
        /// <param name="tick">本帧的逻辑帧号与相隔帧数；不含秒，持续时长要按帧数算。</param>
        public virtual void OnUpdate(in StateTick tick)
        {
            // 功能：逻辑帧推进回调，本类什么都不做。
            // 入参：tick 逻辑帧号与相隔帧数，由逻辑层给，不是 Unity 的帧。
            // 返回：无。
            // 步骤：留空交给子类覆写；帧号怎么用由子类决定，本类不消耗、不累积。
            // 上游：StateMachine / HierarchicalStateMachine 的 Tick，每逻辑帧一次；分层时先下层后上层
            // 下游：无
            // 注意：这里不许用 Time.deltaTime；「持续 N 秒」要换算成帧数，否则两端逻辑会不同步。
        }

        /// <summary>离开状态时的入口；默认空实现。</summary>
        public virtual void OnExit(S next)
        {
            // 功能：状态被切走时的回调，本类什么都不做。
            // 入参：next 目标状态标识，本类不使用。
            // 返回：无。
            // 步骤：留空交给子类覆写；清理动作由子类自己安排，基类不替它做。
            // 上游：StateMachine / HierarchicalStateMachine 在切入新状态之前调用
            // 下游：无
        }

        /// <summary>处理一个事件；默认表示「跟我无关」。</summary>
        /// <returns>true 表示已处理，分层派发时不再往上层传；默认 false。</returns>
        public virtual bool OnEvent(EventId id)
        {
            // 功能：接收一个事件，默认表示不处理。
            // 注意：返回 bool 是为分层状态机准备的 —— 下层返回 false 上层才有机会处理；若处理了却返回 false，上下两层会各处理一次。
            return false;
        }

        /// <summary>能不能从 from 切进本状态；默认允许任何来源。</summary>
        /// <returns>默认 true，表示不挑来源。</returns>
        public virtual bool CanEnterFrom(S from)
        {
            // 功能：判断是否允许从 from 切进本状态，默认放行。
            // 注意：问的是目标状态（本状态），所以「谁能打断谁」的规则集中在目标状态里；默认放行的代价见类头。
            return true;
        }

        /// <summary>调试文本；直接返回 Name，日志与调试面板可直接打印。</summary>
        public override string ToString()
        {
            // 功能：直接返回 Name，默认即类型名。
            return Name;
        }
    }
}
