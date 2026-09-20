// ============================================================================
//  NBC.Framework · 事件中心
//  替代：唐老师框架 Event/EventCenter.cs（原 151 行）
//  缺陷编号：FW-06（string key）、FW-07（同 key 异类型 NRE）
//            + 审计新增 P-16（未注册事件无反馈 / 空 key 不清理）
//            + 审计新增 P-18（派发中增删监听的语义不确定）
//  完整记录：Docs/06-框架改造记录.md §三 FW-06 / FW-07、§四 P-16 / P-18
//
//  ---------------------------------------------------------------------------
//  一、原版错在哪（FW-07 最值得讲）
//  ---------------------------------------------------------------------------
//      (eventDic[name] as EventInfo<T>).actions += action;      // :57
//      (eventDic[name] as EventInfo<T>).actions.Invoke(info);   // :121
//
//  用的是 **`as`**，不是强制转换。于是类型不匹配时得到的是 **null**，
//  紧接着访问 `.actions` -> **NullReferenceException**。
//
//  为什么这个区别重要：NRE 的堆栈指向 `.actions` 那一行，
//  看起来像"事件根本没注册"，而真实原因是"同名的 key 被另一个类型先注册了"。
//  排查方向会被带偏。原版还有**两条**触发路径：
//    ① 同名 key 先以 <int> 监听、再以 <string> 触发；
//    ② 泛型与非泛型**共用同一个 key 空间**（`:77` 的 `as EventInfo` 同样得 null）。
//
//  改造后：注册时就校验类型一致性，抛**明确异常**，消息里带上
//  "key + 已存在的类型 + 本次的类型"，一眼定位。
//
//  ---------------------------------------------------------------------------
//  二、派发模型（2026-09-20 负责人确认：同步派发 + 写时复制快照）
//  ---------------------------------------------------------------------------
//  需求文档 FW-M02 字面写的是"**异步派发队列**"。本项目**刻意偏离**它，理由：
//
//    真异步（下一帧派发）会给战斗逻辑**加一帧延迟**。本项目要做帧同步与客户端预测
//    （需求 2/6），时序确定性是硬约束 —— 一帧延迟会让"输入→表现"的因果链变复杂，
//    也让单测难写。为了消除一个可以靠快照解决的隐患而付这个代价，不划算。
//
//  改用"同步派发 + 写时复制快照"，同样达成 FW-M02 的**目标**（派发中增删监听不出事）：
//
//    · C# 的委托是**不可变**的：`+=` / `-=` 生成的是**新**委托实例，不会改动旧的。
//      所以只要在派发前把字段读进一个局部变量，这次派发就锁定了自己的监听者名单，
//      期间任何人增删都影响不到它 —— 这就是快照，且**零额外分配**（不需要拷贝数组）。
//    · 由此得到两条**确定**语义（下面第三条会解释为什么要写清楚）：
//        1) 派发中**新加**的监听者，**不会**收到本次事件（下次才会）；
//        2) 派发中**移除**的监听者，**仍会**收到本次事件（因为本次名单已锁定）。
//      这两条与 C# 原生 `event` 的行为一致 —— 选"和语言一致"是为了让人不用背新规则。
//
//  ⚠️ 为什么第 2 条要写进文档并配一条测试：
//     它容易被当成 Bug。把它**明确成契约**，以后有人遇到就不会误判。
//     "行为有测试盯着"和"行为碰巧如此"是两回事 —— 这也是 P-18 真正的问题所在：
//     原版不是错，而是**没人知道它会怎样**。
//
//  ---------------------------------------------------------------------------
//  三、热路径上的取舍：不为了"容错"产生 GC
//  ---------------------------------------------------------------------------
//  派发时直接用 `snapshot.Invoke(payload)`，**不**调 `GetInvocationList()`
//  （后者每次派发都会分配一个 Delegate[]，在每帧几十次事件的热路径上是稳定的 GC 压力）。
//  代价：某个监听者抛异常会中断本次派发（与 C# 原生 event 行为一致）。
//  这里把异常 catch 住并 `LogException`，至少保证错误不会被吞掉、也不会污染事件中心自身状态。
//
//  ---------------------------------------------------------------------------
//  四、三层防护（都是原版没有的）
//  ---------------------------------------------------------------------------
//    ① **未注册事件告警**（P-16）：触发一个没有任何监听者的事件 -> 每个 id 只警告一次
//       （用 HashSet 记录，避免每帧刷屏），可整体关闭。
//    ② **派发深度上限**（P-18 的延伸）：A 触发 B、B 触发 A 的无限递归会让栈溢出，
//       而栈溢出的报错信息几乎无法定位。这里限制深度并报明确错误。
//    ③ **异常隔离**：见上一条。
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace NBC.Framework
{
    /// <summary>
    /// 事件中心。同步派发、强类型标识、类型安全注册。
    /// </summary>
    public sealed class EventCenter : Singleton<EventCenter>
    {
        /// <summary>派发深度上限。超过即判定为递归触发，报错并放弃本次派发。</summary>
        private const int MaxDispatchDepth = 8;

        private readonly Dictionary<EventId, EventChannel> m_channels = new Dictionary<EventId, EventChannel>();

        /// <summary>已经就"没有监听者"警告过的 id，避免每帧刷屏。</summary>
        private readonly HashSet<EventId> m_warnedMissingListener = new HashSet<EventId>();

        private int m_dispatchDepth;

        /// <summary>
        /// 触发一个没有任何监听者的事件时，是否输出警告。
        /// 开发期建议开（这是 FW-06 想解决的问题：事件没触发却毫无提示）；
        /// 发布包可以关掉。
        /// </summary>
        public bool WarnOnMissingListener { get; set; } = true;

        /// <summary>当前登记的事件数量（含监听者已被清空但 key 仍在的）。调试面板用。</summary>
        public int ChannelCount
        {
            get { return m_channels.Count; }
        }

        // ====================================================================
        //  注册 / 注销
        // ====================================================================

        /// <summary>注册一个带参数的事件监听。</summary>
        /// <typeparam name="T">事件参数类型。</typeparam>
        /// <param name="id">事件标识。</param>
        /// <param name="action">处理函数。</param>
        public void AddEventListener<T>(EventId id, UnityAction<T> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            EventChannel<T> channel = GetOrCreateChannel<T>(id);
            channel.Handlers += action;
            m_warnedMissingListener.Remove(id);
        }

        /// <summary>注册一个不带参数的事件监听。</summary>
        /// <param name="id">事件标识。</param>
        /// <param name="action">处理函数。</param>
        public void AddEventListener(EventId id, UnityAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            VoidEventChannel channel = GetOrCreateVoidChannel(id);
            channel.Handlers += action;
            m_warnedMissingListener.Remove(id);
        }

        /// <summary>注销一个带参数的事件监听。</summary>
        /// <typeparam name="T">事件参数类型。</typeparam>
        /// <param name="id">事件标识。</param>
        /// <param name="action">之前注册的处理函数。</param>
        public void RemoveEventListener<T>(EventId id, UnityAction<T> action)
        {
            if (action == null)
            {
                return;
            }

            EventChannel existing;
            if (!m_channels.TryGetValue(id, out existing))
            {
                return;
            }

            // 类型不符说明调用的不是当初注册的那个重载，明确报出来（FW-07 的另一半）
            EventChannel<T> typed = existing as EventChannel<T>;
            if (typed == null)
            {
                throw BuildTypeMismatch(id, existing, typeof(T));
            }

            typed.Handlers -= action;
            CleanUpIfEmpty(id, typed);
        }

        /// <summary>注销一个不带参数的事件监听。</summary>
        /// <param name="id">事件标识。</param>
        /// <param name="action">之前注册的处理函数。</param>
        public void RemoveEventListener(EventId id, UnityAction action)
        {
            if (action == null)
            {
                return;
            }

            EventChannel existing;
            if (!m_channels.TryGetValue(id, out existing))
            {
                return;
            }

            VoidEventChannel typed = existing as VoidEventChannel;
            if (typed == null)
            {
                throw BuildTypeMismatch(id, existing, null);
            }

            typed.Handlers -= action;
            CleanUpIfEmpty(id, typed);
        }

        // ====================================================================
        //  触发
        // ====================================================================

        /// <summary>触发一个带参数的事件。</summary>
        /// <typeparam name="T">事件参数类型。</typeparam>
        /// <param name="id">事件标识。</param>
        /// <param name="payload">事件参数。</param>
        public void Trigger<T>(EventId id, T payload)
        {
            EnsureValidId(id);

            EventChannel existing;
            if (!m_channels.TryGetValue(id, out existing))
            {
                WarnMissingListener(id);
                return;
            }

            EventChannel<T> typed = existing as EventChannel<T>;
            if (typed == null)
            {
                // 触发参数的类型与注册时的类型不一致。原版在这里会得到 NRE；
                // 改造后抛出带双方类型的明确异常（FW-07）。
                throw BuildTypeMismatch(id, existing, typeof(T));
            }

            // 写时复制快照：委托不可变，这一次读取就锁定了本次派发的监听者名单。
            UnityAction<T> snapshot = typed.Handlers;
            if (snapshot == null)
            {
                WarnMissingListener(id);
                return;
            }

            if (!EnterDispatch(id))
            {
                return;
            }

            try
            {
                snapshot.Invoke(payload);
            }
            catch (Exception e)
            {
                // 不吞掉错误，但也不让它破坏事件中心自身状态。
                Debug.LogException(e);
            }
            finally
            {
                ExitDispatch();
            }
        }

        /// <summary>触发一个不带参数的事件。</summary>
        /// <param name="id">事件标识。</param>
        public void Trigger(EventId id)
        {
            EnsureValidId(id);

            EventChannel existing;
            if (!m_channels.TryGetValue(id, out existing))
            {
                WarnMissingListener(id);
                return;
            }

            VoidEventChannel typed = existing as VoidEventChannel;
            if (typed == null)
            {
                throw BuildTypeMismatch(id, existing, null);
            }

            UnityAction snapshot = typed.Handlers;
            if (snapshot == null)
            {
                WarnMissingListener(id);
                return;
            }

            if (!EnterDispatch(id))
            {
                return;
            }

            try
            {
                snapshot.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                ExitDispatch();
            }
        }

        // ====================================================================
        //  清理与调试
        // ====================================================================

        /// <summary>移除某个事件的全部监听者（P-16：原版移除后不清理空 key）。</summary>
        /// <param name="id">事件标识。</param>
        public void ClearListeners(EventId id)
        {
            EventChannel existing;
            if (m_channels.TryGetValue(id, out existing))
            {
                existing.ClearListeners();
                m_channels.Remove(id);
            }

            m_warnedMissingListener.Remove(id);
        }

        /// <summary>清空全部事件与监听者。</summary>
        public void Clear()
        {
            m_channels.Clear();
            m_warnedMissingListener.Clear();
        }

        /// <summary>取某个事件的监听者数量。调试面板用。</summary>
        /// <param name="id">事件标识。</param>
        /// <returns>监听者数量；事件不存在时为 0。</returns>
        public int GetListenerCount(EventId id)
        {
            EventChannel existing;
            if (!m_channels.TryGetValue(id, out existing))
            {
                return 0;
            }

            return existing.ListenerCount;
        }

        /// <summary>
        /// 把当前所有事件的状态拷进 buffer（先清空 buffer），供性能/调试面板显示（验收 V7）。
        /// </summary>
        /// <param name="buffer">接收结果的列表。每帧调用时请复用同一个列表，避免产生垃圾。</param>
        public void CopyChannelStats(List<EventChannelStat> buffer)
        {
            if (buffer == null)
            {
                return;
            }

            buffer.Clear();
            foreach (KeyValuePair<EventId, EventChannel> pair in m_channels)
            {
                buffer.Add(new EventChannelStat(pair.Key, pair.Value.PayloadType, pair.Value.ListenerCount));
            }
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>取（必要时创建）指定参数类型的通道。类型不符时抛明确异常。</summary>
        private EventChannel<T> GetOrCreateChannel<T>(EventId id)
        {
            EnsureValidId(id);

            EventChannel existing;
            if (m_channels.TryGetValue(id, out existing))
            {
                EventChannel<T> typed = existing as EventChannel<T>;
                if (typed == null)
                {
                    // 这就是 FW-07 的修法：在**注册**时就把类型冲突拦下来，
                    // 而不是等到派发时得到一个 NRE。
                    throw BuildTypeMismatch(id, existing, typeof(T));
                }

                return typed;
            }

            EventChannel<T> created = new EventChannel<T>();
            m_channels.Add(id, created);
            return created;
        }

        /// <summary>取（必要时创建）无参数通道。类型不符时抛明确异常。</summary>
        private VoidEventChannel GetOrCreateVoidChannel(EventId id)
        {
            EnsureValidId(id);

            EventChannel existing;
            if (m_channels.TryGetValue(id, out existing))
            {
                VoidEventChannel typed = existing as VoidEventChannel;
                if (typed == null)
                {
                    throw BuildTypeMismatch(id, existing, null);
                }

                return typed;
            }

            VoidEventChannel created = new VoidEventChannel();
            m_channels.Add(id, created);
            return created;
        }

        /// <summary>监听者被清空后把 key 一并移除，避免字典里堆积空条目（P-16）。</summary>
        private void CleanUpIfEmpty(EventId id, EventChannel channel)
        {
            if (channel.ListenerCount == 0)
            {
                m_channels.Remove(id);
            }

            m_warnedMissingListener.Remove(id);
        }

        private static void EnsureValidId(EventId id)
        {
            if (!id.IsValid)
            {
                throw new ArgumentException(
                    "[EventCenter] 收到一个未初始化的 EventId（default(EventId)）。" +
                    "事件标识必须来自 EventId.Declare 声明出来的常量。", nameof(id));
            }
        }

        /// <summary>构造 FW-07 要的那种"一眼能定位"的异常消息。</summary>
        private static InvalidOperationException BuildTypeMismatch(EventId id, EventChannel existing, Type requested)
        {
            string existingName = existing.PayloadType == null ? "（无参数）" : existing.PayloadType.Name;
            string requestedName = requested == null ? "（无参数）" : requested.Name;

            return new InvalidOperationException(
                "[EventCenter] 事件类型冲突：" + id.Name +
                " 已经以【" + existingName + "】注册过，但这次用的是【" + requestedName + "】。" +
                "同一个事件标识只能对应一种参数类型（原版在这里会抛 NullReferenceException，" +
                "堆栈指向 .actions，容易被误判成『事件没注册』）。");
        }

        private void WarnMissingListener(EventId id)
        {
            if (!WarnOnMissingListener)
            {
                return;
            }

            if (m_warnedMissingListener.Add(id))
            {
                // 每个 id 只警告一次：既要让你知道"这个事件没人听"，
                // 又不能每帧刷屏把 Console 淹掉。
                Debug.LogWarning("[EventCenter] 事件【" + id.Name + "】被触发，但当前没有任何监听者。" +
                                 "可能是拼写 / 注册时机问题（此警告对同一事件只提示一次）。");
            }
        }

        /// <summary>进入派发。返回 false 表示超过深度上限、本次派发被放弃。</summary>
        private bool EnterDispatch(EventId id)
        {
            if (m_dispatchDepth >= MaxDispatchDepth)
            {
                Debug.LogError("[EventCenter] 派发深度超过 " + MaxDispatchDepth + " 层，疑似递归触发（" + id.Name +
                               "）。已放弃本次派发 —— 继续下去会栈溢出，而栈溢出的报错几乎无法定位。");
                return false;
            }

            m_dispatchDepth++;
            return true;
        }

        private void ExitDispatch()
        {
            m_dispatchDepth--;
        }

        // ====================================================================
        //  通道实现
        // ====================================================================

        /// <summary>通道基类：让不同参数类型的事件能放进同一个字典。</summary>
        private abstract class EventChannel
        {
            /// <summary>参数类型；无参数事件返回 null。</summary>
            public abstract Type PayloadType { get; }

            /// <summary>当前监听者数量。</summary>
            public abstract int ListenerCount { get; }

            /// <summary>清空监听者。</summary>
            public abstract void ClearListeners();
        }

        /// <summary>带参数的事件通道。</summary>
        private sealed class EventChannel<T> : EventChannel
        {
            public UnityAction<T> Handlers;

            public override Type PayloadType
            {
                get { return typeof(T); }
            }

            public override int ListenerCount
            {
                get
                {
                    return Handlers == null ? 0 : Handlers.GetInvocationList().Length;
                }
            }

            public override void ClearListeners()
            {
                Handlers = null;
            }
        }

        /// <summary>无参数的事件通道。单独一个类型，避免与 `EventChannel<T>` 的 key 空间混淆。</summary>
        private sealed class VoidEventChannel : EventChannel
        {
            public UnityAction Handlers;

            public override Type PayloadType
            {
                get { return null; }
            }

            public override int ListenerCount
            {
                get
                {
                    return Handlers == null ? 0 : Handlers.GetInvocationList().Length;
                }
            }

            public override void ClearListeners()
            {
                Handlers = null;
            }
        }
    }
}
