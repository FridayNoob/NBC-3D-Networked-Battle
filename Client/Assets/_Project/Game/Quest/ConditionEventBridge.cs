// 这个文件里有：
//   ConditionEventBridge（类）：把事件中心广播的游戏事件按注册映射翻译成条件系统的一次条件事件通知，释放时统一退订

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Shared.Condition;
using UnityEngine.Events;

namespace NBC.Game.Quest
{
    // 主要功能：把事件中心广播的游戏事件，按「注册映射」翻译成条件系统的一次条件事件通知（条件事件类型 + 目标编号 + 次数）。
    // 主要数据成员：m_tracker（条件系统核心）、m_unbinders（每条链路的退订动作，个数即已接通条数）、m_disposed（是否已释放）。
    // 主要外部方法：Bind<T>（接通一条链路）、Dispose（退订全部）、BindCount（已接通条数）。
    // 谁在用它：BattleSession（构造时接线、释放时退订）；Editor 的 NetDebugWindow（建面板时接线、关窗口时退订）。
    // 注意：条件系统在共享层，没有 UnityEngine，服务端也要编同一份源码，它不认识 EventCenter，只认 Notify(条件事件, 目标编号, 次数)，所以必须有本类做翻译。
    // 注意：用注册映射而不是 switch 写死，是为了让发事件的战斗 / 背包模块完全不知道任务的存在，新增链路只在装配处加一行 Bind，本类不随玩法长大而改。
    // 注意：本类订阅了事件中心，必须有人 Dispose；不退订等于对象已经死了还在被回调，会泄漏并可能碰到已销毁的资源。
    /// <summary>把事件中心广播的游戏事件翻译成条件系统能懂的条件事件通知；用完必须 Dispose 退订。</summary>
    public sealed class ConditionEventBridge : IDisposable
    {
        // 条件系统核心，每条链路的通知最终都落到它身上；构造时校验过，不会为 null
        private readonly ConditionTracker m_tracker;

        // 每条链路注册时存下的退订动作，Dispose 时逐个执行；元素个数就是已接通条数，0 起
        private readonly List<Action> m_unbinders = new List<Action>();

        // 是否已经 Dispose 过；初值 false，释放后恒为 true，所以重复 Dispose 无害且不能再 Bind
        private bool m_disposed;

        /// <summary>用给定的条件系统核心造一座桥；构造成功即可 Bind。</summary>
        public ConditionEventBridge(ConditionTracker tracker)
        {
            // 功能：保存条件系统核心，之后每条链路的通知都落到它身上。
            // 入参：tracker 条件系统核心，不能为 null，否则抛 ArgumentNullException。
            // 步骤：
            //   1) 先挡 null：桥没有核心就什么都干不了，早炸比运行期空引用好查
            //   2) 存进只读字段，构造之后不再换核心
            // 返回：无（构造函数；抛异常时对象不会建出来）。
            // 上游：BattleSession 构造时接线；Editor 的 NetDebugWindow 建面板时接线。
            // 下游：无。
            if (tracker == null)
            {
                throw new ArgumentNullException(nameof(tracker),
                    "[ConditionEventBridge] 必须给一个 ConditionTracker。");
            }

            m_tracker = tracker;
        }

        // 功能：已接通的链路条数，装配与测试用它核对 Bind 是否都生效（0 到 Bind 次数）。
        public int BindCount
        {
            get { return m_unbinders.Count; }
        }

        /// <summary>接通一条链路：事件发生时把它算作一次条件事件通知（条件事件类型 + 目标编号 + 次数）。</summary>
        public void Bind<T>(EventId id, EConditionEvent conditionEvent, Func<T, int> targetIdOf)
        {
            // 功能：把「某事件发生」这条链路接到条件系统的一次 Notify，目标编号由 targetIdOf 从载荷里现取。
            // 入参：id 事件标识（须 IsValid，否则抛 ArgumentException）；conditionEvent 条件事件类型（须是 ConditionEvents 已知成员，否则抛 ArgumentOutOfRangeException）；targetIdOf 从载荷取目标编号的方法（不能为 null，不关心目标就传 _ => 0，0 表示任意目标）；T 载荷类型，必须与发布方一致，否则事件中心抛类型不符。
            // 步骤：
            //   1) 先把三个入参在装配期校验完，坏参数立刻炸，不会等到事件来了才炸在回调里
            //   2) 再挡已 Dispose 的桥：否则新订的链路再也退不掉，等于泄漏
            //   3) 包一个闭包，把载荷换算成 Notify(条件事件, 目标编号, 1)
            //   4) 订阅成功后把对应的退订动作记进 m_unbinders，Dispose 时才能原样撤掉
            // 返回：无。
            // 上游：装配处（BattleSession、Editor 的 NetDebugWindow）。
            // 下游：EventCenter.Instance.AddEventListener；事件到达时 ConditionTracker.Notify。
            // 注意：targetIdOf 只在装配时创建一次，之后每次事件复用同一个闭包，不产生额外分配。
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

        /// <summary>退订全部链路并标记为已释放；可重复调用。</summary>
        public void Dispose()
        {
            // 功能：把所有已接通的链路从事件中心摘掉，并让这座桥从此不能再 Bind。
            // 入参：无。
            // 步骤：
            //   1) 已 Dispose 就直接返回，保证重复调用无害
            //   2) 先置位 m_disposed，防止退订途中又 Bind 进来一条再也退不掉的链路
            //   3) 逐个执行退订动作
            //   4) 清空列表，即使将来有别的路径再调也不会重复退订
            // 返回：无。
            // 上游：BattleSession 释放会话时；Editor 的 NetDebugWindow 关窗口时。
            // 下游：EventCenter.Instance.RemoveEventListener。
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
