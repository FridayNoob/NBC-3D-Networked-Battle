// ============================================================================
//  NBC.Framework.Fsm · 状态基类（可选）
//  对应需求：FW-M14
//
//  ---------------------------------------------------------------------------
//  为什么要有它，但**不强制**继承
//  ---------------------------------------------------------------------------
//  `IState<S>` 有 6 个成员，其中大多数状态只关心其中一两个。
//  直接实现接口的话每个状态都要写 6 个空方法，噪音很大。
//
//  所以给一个"全空实现"的基类，子类**只覆写关心的那几个**。
//  ⚠️ 但接口本身保留 —— 想把状态做成 `struct`、或者已经继承了别的基类时，
//     直接实现 `IState<S>` 就行，不必被这个基类绑住。
//
//  ---------------------------------------------------------------------------
//  默认的 `CanEnterFrom` 返回 true（这里想清楚过）
//  ---------------------------------------------------------------------------
//  默认"谁都能进来"，因为：
//    · 大部分状态确实不挑来源
//    · 而"挑来源"的规则（受击硬直不许被打断、死亡不可逆）是**少数**，
//      让它们**显式覆写**，比让所有状态都写一遍 `return true` 更清楚
//  ⚠️ 代价："忘了覆写"会表现为"这个状态能被随便打断"，而且**不报错**。
//     所以状态清单里"可被打断"那一列必须和代码对上 —— 这是 M2 写玩家状态时的检查项。
// ============================================================================

#nullable disable
// ↑ 双端共用（服务端也编它，见 Server\NBC.Server.Game.csproj）：服务端开了可空、Unity 没开
//   —— 与 Shared\ 同一处理由（M3-S6b，2026-09-23）。
using NBC.Framework;

namespace NBC.Framework.Fsm
{
    /// <summary>
    /// 状态基类：所有方法都有空实现，子类只覆写关心的。
    /// </summary>
    /// <typeparam name="S">状态标识类型。</typeparam>
    public abstract class StateBase<S> : IState<S>
    {
        /// <summary>状态显示名。默认用类型名，子类可以改成更好读的。</summary>
        public virtual string Name
        {
            get { return GetType().Name; }
        }

        /// <summary>进入状态。默认什么都不做。</summary>
        /// <param name="previous">来源状态。</param>
        public virtual void OnEnter(S previous)
        {
        }

        /// <summary>推进。默认什么都不做。</summary>
        /// <param name="tick">推进信息。</param>
        public virtual void OnUpdate(in StateTick tick)
        {
        }

        /// <summary>离开状态。默认什么都不做。</summary>
        /// <param name="next">目标状态。</param>
        public virtual void OnExit(S next)
        {
        }

        /// <summary>收到事件。默认"跟我无关"。</summary>
        /// <param name="id">事件标识。</param>
        /// <returns>默认返回 false。</returns>
        public virtual bool OnEvent(EventId id)
        {
            return false;
        }

        /// <summary>能不能从某个状态切进来。**默认允许**（见文件头的说明）。</summary>
        /// <param name="from">来源状态。</param>
        /// <returns>默认返回 true。</returns>
        public virtual bool CanEnterFrom(S from)
        {
            return true;
        }

        /// <summary>调试文本。</summary>
        /// <returns>状态名。</returns>
        public override string ToString()
        {
            return Name;
        }
    }
}
