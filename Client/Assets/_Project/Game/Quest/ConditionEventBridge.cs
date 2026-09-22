// ============================================================================
//  ConditionEventBridge —— 「游戏里发生的事」 → 「条件系统的事件」的那座桥
//  项目：3D联网战斗Demo   对应：M2-A2、Docs\20 §四（事件源 → 条件系统）
//
//  ---------------------------------------------------------------------------
//  为什么需要它（这是 M2 里最能讲取舍的一处）
//  ---------------------------------------------------------------------------
//  条件系统（在共享层）**不认识 `EventCenter`** —— 它没有 `UnityEngine`，
//  服务端也要编同一份源码。所以它只暴露 `Notify(事件类型, 目标, 次数)`。
//
//  而游戏里发生的事是通过 M1 的 `EventCenter` 广播的（`EventId` + 载荷）。
//  两者之间必须有人翻译，这个人就是本类：
//
//      战斗：怪物死了 → BattleEvents.MonsterDied(载荷里有怪物编号)
//                              │  桥（本类）
//                              ▼
//      条件系统：Notify(KillMonster, 怪物编号, 1)  → 累加"击杀 3 只野狼"
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么用「注册映射」而不是「一个 switch 写死」
//  ---------------------------------------------------------------------------
//  写死的话，每加一个能推进任务的事件都要改这个文件 ——
//  而"加事件的人"往往在做战斗/背包，根本不知道任务模块在哪。
//  改成 `Bind<T>(事件, 条件事件类型, 从载荷里取目标编号)` 之后：
//
//      · **生产事件的模块完全不知道任务的存在**（它只管发自己的事件）
//      · 装配处（组合根 / M2-C）写一行 `Bind` 就接通了一条新链路
//      · 桥本身对"有哪些事件"零知识 —— 于是它不需要随着玩法长大而变
//
//  `Bind` 是泛型的，因为**每个事件的载荷类型不同**（怪物死亡带怪物编号，
//  拾取带物品编号）。`targetIdOf` 那个 `Func<T,int>` 就是"怎么从载荷里取出目标编号"，
//  它在**装配时**创建一次，之后每次事件只是调用它 —— 不产生额外分配。
//
//  ⚠️ 生命周期：它订阅了事件中心，所以**必须有人 Dispose**
//     （A4/A8 那两轮踩过的同一个坑：订阅不退订 = 对象"死了"还在被回调）。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Shared.Condition;
using UnityEngine.Events;

namespace NBC.Game.Quest
{
    /// <summary>把事件中心里的事件转成条件系统能懂的事件。</summary>
    public sealed class ConditionEventBridge : IDisposable
    {
        /// <summary>条件系统核心。</summary>
        private readonly ConditionTracker m_tracker;

        /// <summary>退订动作（Dispose 时逐个执行）。</summary>
        private readonly List<Action> m_unbinders = new List<Action>();

        /// <summary>已经 Dispose 过没有（重复 Dispose 要无害）。</summary>
        private bool m_disposed;

        /// <summary>造一座桥。</summary>
        /// <param name="tracker">条件系统核心（不能为 null）。</param>
        public ConditionEventBridge(ConditionTracker tracker)
        {
            if (tracker == null)
            {
                throw new ArgumentNullException(nameof(tracker),
                    "[ConditionEventBridge] 必须给一个 ConditionTracker。");
            }

            m_tracker = tracker;
        }

        /// <summary>已经接通了多少条链路。</summary>
        public int BindCount
        {
            get { return m_unbinders.Count; }
        }

        /// <summary>
        /// 接通一条链路：**某个事件发生时，当成一次条件事件喂给条件系统**。
        /// </summary>
        /// <typeparam name="T">事件的载荷类型（必须与触发方一致，否则事件中心会抛类型不符）。</typeparam>
        /// <param name="id">事件标识（如 `BattleEvents.MonsterDied`）。</param>
        /// <param name="conditionEvent">对应的条件事件类型（如 `EConditionEvent.KillMonster`）。</param>
        /// <param name="targetIdOf">怎么从载荷里取出"目标编号"。</param>
        public void Bind<T>(EventId id, EConditionEvent conditionEvent, Func<T, int> targetIdOf)
        {
            if (!id.IsValid)
            {
                throw new ArgumentException(
                    "[ConditionEventBridge] 事件标识无效（`default(EventId)`？）。", nameof(id));
            }

            if (!ConditionEvents.IsDefined(conditionEvent))
            {
                throw new ArgumentOutOfRangeException(nameof(conditionEvent),
                    "[ConditionEventBridge] 条件事件类型不是已知成员：" + (int)conditionEvent);
            }

            if (targetIdOf == null)
            {
                throw new ArgumentNullException(nameof(targetIdOf),
                    "[ConditionEventBridge] 必须给一个\"从载荷里取目标编号\"的方法。\n" +
                    "如果这个事件不关心具体目标，传 `_ => 0`（0 = 任意目标）。");
            }

            if (m_disposed)
            {
                throw new ObjectDisposedException(nameof(ConditionEventBridge),
                    "[ConditionEventBridge] 这座桥已经 Dispose 了，不能再 Bind。");
            }

            UnityAction<T> handler = payload => m_tracker.Notify(conditionEvent, targetIdOf(payload), 1);

            EventCenter.Instance.AddEventListener(id, handler);

            m_unbinders.Add(() => EventCenter.Instance.RemoveEventListener(id, handler));
        }

        /// <summary>退订全部链路（可以重复调用）。</summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;

            for (int i = 0; i < m_unbinders.Count; i++)
            {
                m_unbinders[i]();
            }

            m_unbinders.Clear();
        }
    }
}
