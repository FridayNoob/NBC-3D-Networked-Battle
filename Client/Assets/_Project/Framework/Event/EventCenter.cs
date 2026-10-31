// 这个文件里有：
//   EventCenter（sealed 类）：进程唯一的事件中心，按 EventId 登记强类型监听、同步派发，并做类型校验与缺监听告警
//   EventChannel（嵌套私有抽象类）：通道基类，让不同参数类型的通道能放进同一个字典
//   EventChannel<T>（嵌套私有类）：带参数事件的通道，保存一份 UnityAction<T> 监听链
//   VoidEventChannel（嵌套私有类）：无参数事件的通道，单独成类型，避免与带参数通道共用 key 空间

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace NBC.Framework
{
    // 主要功能：进程唯一的事件中心，按 EventId 登记强类型监听，同步派发并隔离监听者异常。
    // 主要数据成员：m_channels（已登记通道）、m_warnedMissingListener（已就缺监听告警过的 id）、m_dispatchDepth（当前派发嵌套深度，0..8）。
    // 主要外部方法：AddEventListener / RemoveEventListener / Trigger / ClearListeners / Clear / GetListenerCount / CopyChannelStats，以及 WarnOnMissingListener、ChannelCount 两个属性。
    // 谁在用它：Game、Editor、Framework.UI 与 EditMode 测试经 EventCenter.Instance 取用，Framework 内部也直接使用；事件常量由各模块的 *Events 类用 EventId.Declare 声明。
    // 注意：派发是同步的，并用写时复制快照锁定本次名单 —— 派发中新增的监听者收不到本次事件，派发中移除的监听者仍会收到本次事件（与 C# 原生 event 一致）。
    // 注意：监听者抛异常会中断本次派发中它后面的监听者，异常被 catch 后走 Debug.LogException，不会污染事件中心自身状态。
    // 注意：A 触发 B、B 再触发 A 这类递归靠 8 层深度上限拦下，超过就报错并放弃本次派发 —— 否则栈溢出的报错几乎无法定位。
    // 注意：名字或类型写错不再静默：未注册事件每个 id 只警告一次，同 key 异类型在注册或派发时抛带双方类型的异常。
    // 为什么同步派发而不是需求文档写的异步队列：见 Docs\06-框架改造记录.md 的事件中心一节。
    /// <summary>事件中心：强类型 EventId、类型安全注册、同步派发，并隔离监听者异常。</summary>
    public sealed class EventCenter : Singleton<EventCenter>
    {
        // 派发深度上限，取值 8；超过即判定为递归触发，报错并放弃本次派发。
        private const int MaxDispatchDepth = 8;

        // 已登记的事件通道，key 是 EventId，value 是带参数或不带参数的通道；数量 0..当前存活的事件数。
        private readonly Dictionary<EventId, EventChannel> m_channels = new Dictionary<EventId, EventChannel>();

        // 已经就「没有监听者」警告过的 id，同一 id 至多一条；注册或清理该 id 时移除。
        private readonly HashSet<EventId> m_warnedMissingListener = new HashSet<EventId>();

        // 当前派发嵌套深度，取值 0..MaxDispatchDepth；进入派发 +1，finally 里 -1。
        private int m_dispatchDepth;

        // 功能：读 / 写「触发无监听者事件时是否警告」的开关；只在事件中心内部触发告警时读。
        /// <summary>触发一个没有任何监听者的事件时是否输出警告；默认 true，开发期建议开、发布包可关。</summary>
        public bool WarnOnMissingListener { get; set; } = true;

        /// <summary>当前登记的事件通道数量，0..已声明事件数；调试面板与 EditMode 测试用。</summary>
        public int ChannelCount
        {
            // 功能：返回字典里现有通道的条目数。
            // 入参：无
            // 返回：已登记通道数，0..当前存活的事件数；只查询，不触发清理。
            // 步骤：直接读 m_channels.Count，不遍历、不加锁 —— 调用方要的是瞬时值。
            // 上游：调试面板、EditMode 的 EventCenterTests
            // 下游：无
            get { return m_channels.Count; }
        }

        // 注册 / 注销

        /// <summary>注册一个带参数的事件监听；同一 id 只能对应一种参数类型。</summary>
        /// <typeparam name="T">事件参数类型。</typeparam>
        /// <param name="id">事件标识，必须来自 EventId.Declare。</param>
        /// <param name="action">处理函数；为 null 时抛 ArgumentNullException。</param>
        public void AddEventListener<T>(EventId id, UnityAction<T> action)
        {
            // 功能：把带参数的监听追加到 id 对应的通道，通道不存在时先建。
            // 入参：id 事件标识；action 处理函数，null 会立刻抛 ArgumentNullException
            // 返回：无
            // 步骤：
            //   1) 先判 action 是否为 null —— 早失败，免得空委托存进通道直到派发时才炸
            //   2) GetOrCreateChannel 按 id 取通道，顺带校验类型一致性
            //   3) 追加到监听链，并清掉该 id 的无监听告警标记，让下次真没人听时还能再提醒
            // 上游：Game / Editor / Framework.UI 与 Framework 内部的订阅方
            // 下游：GetOrCreateChannel；类型冲突时抛 BuildTypeMismatch 造的异常
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            EventChannel<T> channel = GetOrCreateChannel<T>(id);
            channel.Handlers += action;
            m_warnedMissingListener.Remove(id);
        }

        /// <summary>注册一个不带参数的事件监听；同一 id 只能对应一种参数类型。</summary>
        /// <param name="id">事件标识，必须来自 EventId.Declare。</param>
        /// <param name="action">处理函数；为 null 时抛 ArgumentNullException。</param>
        public void AddEventListener(EventId id, UnityAction action)
        {
            // 功能：把无参数监听追加到 id 对应的无参通道，通道不存在时先建。
            // 入参：id 事件标识；action 处理函数，null 会立刻抛 ArgumentNullException
            // 返回：无
            // 步骤：
            //   1) 先判 action 是否为 null —— 早失败，免得空委托存进通道直到派发时才炸
            //   2) GetOrCreateVoidChannel 按 id 取无参通道，顺带校验类型一致性
            //   3) 追加到监听链，并清掉该 id 的无监听告警标记
            // 上游：Game / Editor / Framework.UI 与 Framework 内部的订阅方
            // 下游：GetOrCreateVoidChannel；类型冲突时抛 BuildTypeMismatch 造的异常
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            VoidEventChannel channel = GetOrCreateVoidChannel(id);
            channel.Handlers += action;
            m_warnedMissingListener.Remove(id);
        }

        /// <summary>注销一个带参数的事件监听；id 未登记时静默返回，类型不符时抛异常。</summary>
        /// <typeparam name="T">事件参数类型，必须与注册时一致。</typeparam>
        /// <param name="id">事件标识。</param>
        /// <param name="action">当初注册的同一个委托实例；为 null 时直接返回。</param>
        public void RemoveEventListener<T>(EventId id, UnityAction<T> action)
        {
            // 功能：从 id 的带参数通道里移除一个监听，通道空了就把 key 一起清掉。
            // 入参：id 事件标识；action 当初注册的委托实例，null 视为无需注销
            // 返回：无
            // 步骤：
            //   1) action 为 null 直接返回 —— 注销空委托没有意义
            //   2) 字典里没有该 id 也直接返回 —— 没注册过不算错误
            //   3) 取出通道并校验类型，类型不符抛明确异常，避免拿错重载导致注销静默失败
            //   4) 从监听链移除，空了交给 CleanUpIfEmpty 清 key
            // 上游：Game / Editor / Framework.UI 与 Framework 内部的退订方（Dispose / OnDestroy）
            // 下游：CleanUpIfEmpty；类型冲突时抛 BuildTypeMismatch 造的异常
            // 注意：类型不符必须报 —— 用无参重载去注销带参事件时若静默返回，监听会一直留着，表现为回调重复执行。
            if (action == null)
            {
                return;
            }

            EventChannel existing;
            if (!m_channels.TryGetValue(id, out existing))
            {
                return;
            }

            EventChannel<T> typed = existing as EventChannel<T>;
            if (typed == null)
            {
                throw BuildTypeMismatch(id, existing, typeof(T));
            }

            typed.Handlers -= action;
            CleanUpIfEmpty(id, typed);
        }

        /// <summary>注销一个不带参数的事件监听；id 未登记时静默返回，类型不符时抛异常。</summary>
        /// <param name="id">事件标识。</param>
        /// <param name="action">当初注册的同一个委托实例；为 null 时直接返回。</param>
        public void RemoveEventListener(EventId id, UnityAction action)
        {
            // 功能：从 id 的无参通道里移除一个监听，通道空了就把 key 一起清掉。
            // 入参：id 事件标识；action 当初注册的委托实例，null 视为无需注销
            // 返回：无
            // 步骤：
            //   1) action 为 null 直接返回 —— 注销空委托没有意义
            //   2) 字典里没有该 id 也直接返回 —— 没注册过不算错误
            //   3) 取出通道并校验类型，类型不符抛明确异常，避免拿错重载导致注销静默失败
            //   4) 从监听链移除，空了交给 CleanUpIfEmpty 清 key
            // 上游：Game / Editor / Framework.UI 与 Framework 内部的退订方（Dispose / OnDestroy）
            // 下游：CleanUpIfEmpty；类型冲突时抛 BuildTypeMismatch 造的异常
            // 注意：无参与带参重载共用同一个 key 空间，传错重载会抛异常而不是静默失败。
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

        // 触发

        /// <summary>同步触发一个带参数的事件；监听者异常只记日志，不会向外抛。</summary>
        /// <typeparam name="T">事件参数类型，必须与注册时一致。</typeparam>
        /// <param name="id">事件标识。</param>
        /// <param name="payload">事件参数，原样传给每个监听者。</param>
        public void Trigger<T>(EventId id, T payload)
        {
            // 功能：同步把 payload 派发给 id 上登记的带参数监听者。
            // 入参：id 事件标识；payload 事件参数，原样传递
            // 返回：无
            // 步骤：
            //   1) 先 EnsureValidId，default(EventId) 直接抛，避免拿空 id 查字典
            //   2) 字典里没有该 id 就按「无监听者」告警后返回，每个 id 只提示一次
            //   3) 取出通道并校验类型，类型不符抛带双方类型的异常（原版在这里得到的是 NRE）
            //   4) 把监听链读进局部快照 —— 委托不可变，这次读取就锁定了本次派发的名单
            //   5) 进入派发深度检查，超限就放弃；try 里 Invoke，catch 记日志，finally 退深度
            // 上游：Game / Editor / Framework.UI 与 Framework 内部的触发方
            // 下游：各监听者、WarnMissingListener、Debug.LogException
            // 注意：直接对快照 Invoke，不调 GetInvocationList —— 后者每次派发都分配一个 Delegate[]，在每帧几十次事件的热路径上会造成 GC 压力。
            // 注意：快照语义是「派发中新增的收不到、派发中移除的仍会收到」，与 C# 原生 event 一致；它不是 Bug，已被测试固定成契约。
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
                throw BuildTypeMismatch(id, existing, typeof(T));
            }

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
                Debug.LogException(e);
            }
            finally
            {
                ExitDispatch();
            }
        }

        /// <summary>同步触发一个不带参数的事件；监听者异常只记日志，不会向外抛。</summary>
        /// <param name="id">事件标识。</param>
        public void Trigger(EventId id)
        {
            // 功能：同步把事件派发给 id 上登记的无参监听者。
            // 入参：id 事件标识
            // 返回：无
            // 步骤：
            //   1) 先 EnsureValidId，default(EventId) 直接抛，避免拿空 id 查字典
            //   2) 字典里没有该 id 就按「无监听者」告警后返回，每个 id 只提示一次
            //   3) 取出无参通道并校验类型，类型不符抛带双方类型的异常
            //   4) 把监听链读进局部快照，锁定本次派发的名单
            //   5) 进入派发深度检查，超限就放弃；try 里 Invoke，catch 记日志，finally 退深度
            // 上游：Game / Editor / Framework.UI 与 Framework 内部的触发方
            // 下游：各监听者、WarnMissingListener、Debug.LogException
            // 注意：快照语义是「派发中新增的收不到、派发中移除的仍会收到」，与 C# 原生 event 一致；它不是 Bug，已被测试固定成契约。
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

        // 清理与调试

        /// <summary>移除某个事件的全部监听者，并把空 key 一起清掉。</summary>
        /// <param name="id">事件标识。</param>
        public void ClearListeners(EventId id)
        {
            // 功能：清空 id 上的全部监听者，并把该通道从字典移除。
            // 入参：id 事件标识；没登记也安全
            // 返回：无
            // 步骤：
            //   1) 有通道才清监听并 Remove key —— 原版只清监听者、key 一直留着，调试数量会虚高
            //   2) 无论有没有都清掉告警标记，让后续注册或触发回到干净状态
            // 上游：场景切换、模块卸载时的统一退订
            // 下游：EventChannel.ClearListeners
            // 注意：派发中清空不影响本次派发 —— 快照已经锁定，被移除的监听者仍会收到本次事件。
            EventChannel existing;
            if (m_channels.TryGetValue(id, out existing))
            {
                existing.ClearListeners();
                m_channels.Remove(id);
            }

            m_warnedMissingListener.Remove(id);
        }

        /// <summary>清空全部事件与监听者，并重置无监听者告警记录。</summary>
        public void Clear()
        {
            // 功能：一次性清空所有通道与告警记录，常用于退出战斗或测试收尾。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 先清通道字典 —— 监听链随通道一起丢弃，不需要逐个退订
            //   2) 再清告警集合 —— 不清的话，下次同名事件触发不会再提示
            // 上游：场景卸载、测试收尾、编辑器重启播放前
            // 下游：无
            // 注意：派发中调用只影响后续派发，本次派发仍持有旧快照，会继续执行完。
            m_channels.Clear();
            m_warnedMissingListener.Clear();
        }

        /// <summary>取某个事件当前的监听者数量。</summary>
        /// <param name="id">事件标识。</param>
        /// <returns>监听者数量；事件未登记时为 0。</returns>
        public int GetListenerCount(EventId id)
        {
            // 功能：查 id 上当前有几个监听者。
            // 入参：id 事件标识
            // 返回：监听者数量，0..委托链长度；未登记或监听链为空都返回 0
            // 步骤：
            //   1) 字典里没有该 id 直接返回 0，不建通道
            //   2) 取通道的 ListenerCount，由具体通道数委托链长度
            // 上游：调试面板、EditMode 的 EventCenterTests
            // 下游：EventChannel.ListenerCount
            // 注意：这条路径会调用 GetInvocationList，产生一个 Delegate[] 分配，只适合调试断言，不要放在每帧热路径上。
            EventChannel existing;
            if (!m_channels.TryGetValue(id, out existing))
            {
                return 0;
            }

            return existing.ListenerCount;
        }

        /// <summary>把当前所有通道的状态拷进 buffer（先清空），供调试或性能面板显示，也供测试断言。</summary>
        /// <param name="buffer">接收结果的列表；传 null 直接返回，高频调用请复用同一个列表。</param>
        public void CopyChannelStats(List<EventChannelStat> buffer)
        {
            // 功能：把每个已登记通道的 id、参数类型与监听者数量拷成只读快照列表。
            // 入参：buffer 接收结果的列表，会被先清空；null 直接返回
            // 返回：无
            // 步骤：
            //   1) buffer 为 null 直接返回，不抛异常
            //   2) 先 Clear 再逐项 Add，保证结果与当前字典一致，不会混进上一次的数据
            //   3) 每项构造 EventChannelStat，只拷贝值，不把内部通道引用交出去
            // 上游：EditMode 的 EventCenterTests；调试 / 性能面板按需调用
            // 下游：EventChannelStat 构造函数、EventChannel.PayloadType / ListenerCount
            // 注意：拷贝过程中每项都要数监听链，会产生 Delegate[] 分配；只适合低频刷新，别在战斗热路径每帧调用。
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

        // 内部

        // 功能：按 id 取带参数通道，没有就新建；已有通道类型不符则抛明确异常。
        // 入参：id 事件标识，必须有效
        // 返回：该 id 上类型为 T 的通道，一定非 null
        // 步骤：
        //   1) 先校验 id 有效，default(EventId) 不许进字典
        //   2) 已存在就做 as 转换，转换失败说明同 key 被别的类型先注册了，立刻抛（原版的修法核心）
        //   3) 不存在才 new 并登记，创建与登记在同一步完成，避免出现半登记状态
        // 上游：AddEventListener<T>
        // 下游：EnsureValidId、BuildTypeMismatch、EventChannel<T> 构造函数
        // 注意：类型必须在注册时就拦下；等到派发再查，拿到的会是原版那种指向 .actions 的 NRE，排查方向会被带偏。
        private EventChannel<T> GetOrCreateChannel<T>(EventId id)
        {
            EnsureValidId(id);

            EventChannel existing;
            if (m_channels.TryGetValue(id, out existing))
            {
                EventChannel<T> typed = existing as EventChannel<T>;
                if (typed == null)
                {
                    throw BuildTypeMismatch(id, existing, typeof(T));
                }

                return typed;
            }

            EventChannel<T> created = new EventChannel<T>();
            m_channels.Add(id, created);
            return created;
        }

        // 功能：按 id 取无参数通道，没有就新建；已有通道类型不符则抛明确异常。
        // 入参：id 事件标识，必须有效
        // 返回：该 id 上的无参通道，一定非 null
        // 步骤：
        //   1) 先校验 id 有效，default(EventId) 不许进字典
        //   2) 已存在就做 as 转换，转换失败说明同 key 被带参数事件先注册了，立刻抛
        //   3) 不存在才 new 并登记，创建与登记在同一步完成，避免出现半登记状态
        // 上游：AddEventListener(EventId, UnityAction)
        // 下游：EnsureValidId、BuildTypeMismatch、VoidEventChannel 构造函数
        // 注意：无参事件若复用了带参数通道的 key，这里必须报错而不是返回 null，否则后面每次 Trigger 都会静默失败。
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

        // 功能：监听者被清空后把 key 一并移除，避免字典里堆积空条目。
        // 入参：id 事件标识；channel 刚做完移除的通道
        // 返回：无
        // 步骤：
        //   1) 先看 ListenerCount，为 0 才 Remove key，非空不能动
        //   2) 无论空不空都清掉告警标记，让下次缺监听时还能提示
        // 上游：两个 RemoveEventListener
        // 下游：m_channels.Remove
        // 注意：原版移除监听后不清理空 key，通道数量只增不减，调试面板看到的是假数字。
        private void CleanUpIfEmpty(EventId id, EventChannel channel)
        {
            if (channel.ListenerCount == 0)
            {
                m_channels.Remove(id);
            }

            m_warnedMissingListener.Remove(id);
        }

        // 功能：校验 EventId 是 Declare 出来的有效标识，default(EventId) 直接抛。
        // 入参：id 待校验的事件标识
        // 返回：无；无效时抛 ArgumentException
        // 步骤：直接查 IsValid，异常消息里点明必须用 EventId.Declare，省得排查时猜
        // 上游：Add / Remove / Trigger 与两个 GetOrCreate 入口
        // 下游：无
        private static void EnsureValidId(EventId id)
        {
            if (!id.IsValid)
            {
                throw new ArgumentException(
                    "[EventCenter] 收到一个未初始化的 EventId（default(EventId)）。" +
                    "事件标识必须来自 EventId.Declare 声明出来的常量。", nameof(id));
            }
        }

        // 功能：拼一条带事件名、已注册类型与本次类型的异常，让同 key 异类型一眼定位。
        // 入参：id 事件标识；existing 已登记的通道；requested 本次请求的参数类型，null 表示无参数
        // 返回：构造好的 InvalidOperationException，由调用方 throw
        // 步骤：
        //   1) 把两边的类型名转成可读文本，null 显示「（无参数）」
        //   2) 拼进带事件名的消息，并点明原版在这里抛 NRE、堆栈指向 .actions 容易被误判成事件没注册
        // 上游：Trigger、AddEventListener、RemoveEventListener 的类型冲突分支
        // 下游：无
        // 注意：返回值由调用方 throw，异常堆栈会指向真正的冲突点，而不是拼消息的地方。
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

        // 功能：对「触发了但没有任何监听者」的 id 打一次警告，同一 id 只打一次。
        // 入参：id 被触发的事件标识
        // 返回：无
        // 步骤：
        //   1) WarnOnMissingListener 关着就什么都不做，发布包可整体关掉
        //   2) HashSet.Add 返回 true 才真的打日志 —— 用集合去重，避免每帧刷屏淹掉 Console
        //   3) 日志里点明可能是拼写或注册时机问题
        // 上游：两个 Trigger 的「无通道」与「通道为空」分支
        // 下游：Debug.LogWarning
        // 注意：每个 id 只提示一次，注册或清理后才会重新提示；不要拿这条日志判断当前是否真的没人监听。
        private void WarnMissingListener(EventId id)
        {
            if (!WarnOnMissingListener)
            {
                return;
            }

            if (m_warnedMissingListener.Add(id))
            {
                Debug.LogWarning("[EventCenter] 事件【" + id.Name + "】被触发，但当前没有任何监听者。" +
                                 "可能是拼写 / 注册时机问题（此警告对同一事件只提示一次）。");
            }
        }

        // 功能：进入一层派发，超过深度上限就报错并拒绝本次派发。
        // 入参：id 本次触发的事件标识，只用于报错文案
        // 返回：true 表示已进入、调用方必须配一次 ExitDispatch；false 表示超限、本次派发要放弃
        // 步骤：
        //   1) 先比当前深度与上限，超了打错误并返回 false，深度保持不变
        //   2) 没超才 +1，保证失败路径不会把深度弄脏
        // 上游：两个 Trigger 在拿到非空快照之后
        // 下游：Debug.LogError
        // 注意：上限用来把 A 触发 B、B 再触发 A 的递归变成一条明确错误，而不是栈溢出后拿到几乎无法定位的堆栈。
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

        // 功能：退出派发，把深度减回去。
        // 入参：无
        // 返回：无
        // 步骤：直接 -1，不做下溢检查 —— 只与 EnterDispatch 返回 true 的分支配对，且放在 finally 里保证异常也会执行
        // 上游：两个 Trigger 的 finally
        // 下游：无
        private void ExitDispatch()
        {
            m_dispatchDepth--;
        }

        // 通道实现

        // 主要功能：通道基类，让不同参数类型的通道能放进同一个字典，只暴露参数类型、监听者数量与清空。
        // 主要数据成员：无字段，具体状态在子类。
        // 主要外部方法：PayloadType、ListenerCount、ClearListeners 三个抽象成员。
        // 谁在用它：EventCenter 的字典 value 与调试统计；EventChannel<T> 与 VoidEventChannel 继承它，私有嵌套不跨程序集。
        private abstract class EventChannel
        {
            // 功能：取参数类型；无参数事件返回 null。
            public abstract Type PayloadType { get; }

            // 功能：取当前监听者数量，0..监听链长度。
            public abstract int ListenerCount { get; }

            // 功能：清空监听链。
            public abstract void ClearListeners();
        }

        // 主要功能：带参数事件的通道，用一份 UnityAction<T> 委托链保存监听者。
        // 主要数据成员：Handlers（监听链，null 表示没有监听者）。
        // 主要外部方法：PayloadType、ListenerCount、ClearListeners 三个重写。
        // 谁在用它：EventCenter 的 GetOrCreateChannel 创建、Add / Remove 挂载、Trigger<T> 派发；私有嵌套不跨程序集。
        private sealed class EventChannel<T> : EventChannel
        {
            // 监听链；取值 null（无监听者）或非空委托链，长度随注册 / 注销变化。
            public UnityAction<T> Handlers;

            // 功能：返回本通道的参数类型 T。
            // 入参：无
            // 返回：typeof(T)，永远非 null
            // 步骤：直接返回 typeof(T)，类型在编译期已确定，不需要缓存
            // 上游：CopyChannelStats、BuildTypeMismatch 的报错文案
            // 下游：无
            public override Type PayloadType
            {
                get { return typeof(T); }
            }

            // 功能：数当前监听链里有几个监听者。
            // 入参：无
            // 返回：监听者数量，0..委托链长度；空链返回 0
            // 步骤：空链直接返回 0，否则用 GetInvocationList 数长度
            // 上游：GetListenerCount、CopyChannelStats、CleanUpIfEmpty
            // 下游：无
            // 注意：GetInvocationList 每次调用都会分配一个 Delegate[]，只适合调试与清理判断，别放进每帧热路径。
            public override int ListenerCount
            {
                get
                {
                    return Handlers == null ? 0 : Handlers.GetInvocationList().Length;
                }
            }

            // 功能：把监听链置空，等价于移除全部监听者。
            // 入参：无
            // 返回：无
            // 步骤：直接把 Handlers 置 null —— 委托不可变，不需要逐个 -=
            // 上游：EventCenter.ClearListeners
            // 下游：无
            public override void ClearListeners()
            {
                Handlers = null;
            }
        }

        // 主要功能：无参数事件的通道，用一份 UnityAction 委托链保存监听者。
        // 主要数据成员：Handlers（监听链，null 表示没有监听者）。
        // 主要外部方法：PayloadType、ListenerCount、ClearListeners 三个重写。
        // 谁在用它：EventCenter 的 GetOrCreateVoidChannel 创建、Add / Remove 挂载、Trigger(EventId) 派发；私有嵌套不跨程序集。
        // 注意：必须与带参数通道分成两个类型，否则无参事件与带参事件共用 key 时无法区分类型。
        private sealed class VoidEventChannel : EventChannel
        {
            // 监听链；取值 null（无监听者）或非空委托链，长度随注册 / 注销变化。
            public UnityAction Handlers;

            // 功能：无参数事件没有参数类型，恒返回 null。
            // 入参：无
            // 返回：null；CopyChannelStats 与本类都据此判断无参数
            // 步骤：直接返回 null，不缓存
            // 上游：CopyChannelStats、BuildTypeMismatch 的报错文案
            // 下游：无
            public override Type PayloadType
            {
                get { return null; }
            }

            // 功能：数当前监听链里有几个监听者。
            // 入参：无
            // 返回：监听者数量，0..委托链长度；空链返回 0
            // 步骤：空链直接返回 0，否则用 GetInvocationList 数长度
            // 上游：GetListenerCount、CopyChannelStats、CleanUpIfEmpty
            // 下游：无
            // 注意：GetInvocationList 每次调用都会分配一个 Delegate[]，只适合调试与清理判断，别放进每帧热路径。
            public override int ListenerCount
            {
                get
                {
                    return Handlers == null ? 0 : Handlers.GetInvocationList().Length;
                }
            }

            // 功能：把监听链置空，等价于移除全部监听者。
            // 入参：无
            // 返回：无
            // 步骤：直接把 Handlers 置 null —— 委托不可变，不需要逐个 -=
            // 上游：EventCenter.ClearListeners
            // 下游：无
            public override void ClearListeners()
            {
                Handlers = null;
            }
        }
    }
}
