// 这个文件里有：
//   MonoManager（类）：常驻的 Mono 宿主，给不是 MonoBehaviour 的代码提供每帧回调、定时器 / 延迟调用与协程
//   MonoManager.Timer（类）：单个定时器的数据，含编号、间隔、剩余时间、重复标志与回调
//   MonoManager.ListenerList（类）：一组每帧回调的容器，派发中增删推迟到本轮结束再生效

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace NBC.Framework
{
    // 主要功能：把每帧回调、定时器 / 延迟调用与协程收拢到一个常驻的 MonoBehaviour 上，让不是 MonoBehaviour 的代码也能用这些能力。
    // 主要数据成员：m_update / m_lateUpdate / m_fixedUpdate（三组每帧回调）、m_timers（未完成的定时器，通常几十个）、m_expired（本帧到期的复用列表）。
    // 主要外部方法：AddUpdateListener / RemoveUpdateListener 等三对、CallLater / CallEvery / Cancel、Run / Stop / StopAll、RemoveAllListeners / CancelAllTimers。
    // 谁在用它：NBC.Framework 内部的 InputManager / AudioManager / SceneLoader 挂每帧回调；NBC.Framework.UI 的 PerfHudPanel 与 PlayMode 测试跨程序集调用。
    /// <summary>公共 Mono 宿主：给不是 MonoBehaviour 的代码提供每帧回调、定时器、延迟调用与协程。</summary>
    public sealed class MonoManager : SingletonAutoMono<MonoManager>
    {
        // Update 时机的回调列表，数量从 0 起。
        private readonly ListenerList m_update = new ListenerList("Update");

        // LateUpdate 时机的回调列表，数量从 0 起。
        private readonly ListenerList m_lateUpdate = new ListenerList("LateUpdate");

        // FixedUpdate 时机的回调列表，数量从 0 起。
        private readonly ListenerList m_fixedUpdate = new ListenerList("FixedUpdate");

        // 全部未完成的定时器，数量通常只有几十个；取消时用线性查找就够。
        private readonly List<Timer> m_timers = new List<Timer>();

        // 本帧到期的定时器，长度 0..m_timers.Count；复用同一个列表，避免每帧分配。
        private readonly List<Timer> m_expired = new List<Timer>();

        // 判断定时器是否已死的缓存委托，避免 RemoveAll 每次分配闭包。
        private static readonly Predicate<Timer> s_isDead = t => t.Dead;

        // 下一个要发放的定时器编号，从 1 起单调递增、不复用；0 留给无效句柄。
        private int m_nextTimerId = 1;

        // 是否暂停：true 时定时器与延迟调用停止推进，每帧回调照常执行。
        /// <summary>是否暂停；true 时只停定时器与延迟调用，每帧回调照常执行。</summary>
        public bool IsPaused { get; set; }


        // 当前 Update 回调数量，范围 0 起；接到 PerfHUD 上能看见监听是否在泄漏。
        /// <summary>当前 Update 回调数量，用于观察监听是否泄漏。</summary>
        public int UpdateListenerCount
        {
            get { return m_update.Count; }
        }

        // 当前 LateUpdate 回调数量，范围 0 起。
        /// <summary>当前 LateUpdate 回调数量。</summary>
        public int LateUpdateListenerCount
        {
            get { return m_lateUpdate.Count; }
        }

        // 当前 FixedUpdate 回调数量，范围 0 起。
        /// <summary>当前 FixedUpdate 回调数量。</summary>
        public int FixedUpdateListenerCount
        {
            get { return m_fixedUpdate.Count; }
        }

        // 当前未完成的定时器数量，范围 0 起；接入 PerfHUD 可观察定时器是否堆积。
        /// <summary>当前未完成的定时器数量。</summary>
        public int TimerCount
        {
            get { return m_timers.Count; }
        }

        // 功能：把一个每帧回调挂到 Update 时机。
        /// <summary>注册一个每帧回调；派发中注册则下一帧生效。</summary>
        /// <param name="action">回调，不允许为 null。</param>
        public void AddUpdateListener(UnityAction action)
        {
            m_update.Add(action);
        }

        // 功能：注销一个 Update 回调；派发中注销则本轮结束后才真正移除。
        /// <summary>注销一个每帧回调。</summary>
        /// <param name="action">之前注册的回调。</param>
        public void RemoveUpdateListener(UnityAction action)
        {
            m_update.Remove(action);
        }

        // 功能：把一个每帧回调挂到 LateUpdate 时机。
        /// <summary>注册一个帧末回调（LateUpdate）。</summary>
        /// <param name="action">回调，不允许为 null。</param>
        public void AddLateUpdateListener(UnityAction action)
        {
            m_lateUpdate.Add(action);
        }

        // 功能：注销一个 LateUpdate 回调；派发中注销则本轮结束后才真正移除。
        /// <summary>注销一个帧末回调。</summary>
        /// <param name="action">之前注册的回调。</param>
        public void RemoveLateUpdateListener(UnityAction action)
        {
            m_lateUpdate.Remove(action);
        }

        // 功能：把一个每帧回调挂到 FixedUpdate 时机。
        /// <summary>注册一个物理帧回调（FixedUpdate）。</summary>
        /// <param name="action">回调，不允许为 null。</param>
        public void AddFixedUpdateListener(UnityAction action)
        {
            m_fixedUpdate.Add(action);
        }

        // 功能：注销一个 FixedUpdate 回调；派发中注销则本轮结束后才真正移除。
        /// <summary>注销一个物理帧回调。</summary>
        /// <param name="action">之前注册的回调。</param>
        public void RemoveFixedUpdateListener(UnityAction action)
        {
            m_fixedUpdate.Remove(action);
        }


        // 功能：延迟若干秒后执行一次。
        /// <summary>延迟若干秒后执行一次。</summary>
        /// <param name="delaySeconds">延迟秒数，小于 0 按 0 处理；受 IsPaused 影响。</param>
        /// <param name="action">到点后执行的动作，不允许为 null。</param>
        /// <returns>可用于 Cancel 的句柄。</returns>
        public TimerHandle CallLater(float delaySeconds, UnityAction action)
        {
            return AddTimer(delaySeconds, delaySeconds, false, action);
        }

        // 功能：每隔若干秒执行一次，直到被 Cancel 掉。
        /// <summary>每隔若干秒执行一次，直到被取消。</summary>
        /// <param name="intervalSeconds">间隔秒数，受 IsPaused 影响；小于等于 0 时每帧都会触发。</param>
        /// <param name="action">每次间隔到点时执行的动作，不允许为 null。</param>
        /// <returns>可用于 Cancel 的句柄。</returns>
        public TimerHandle CallEvery(float intervalSeconds, UnityAction action)
        {
            return AddTimer(intervalSeconds, intervalSeconds, true, action);
        }

        // 功能：按句柄取消一个定时器，取消不存在的句柄是安全的。
        // 入参：handle 之前由 CallLater / CallEvery 返回的句柄；无效或已过期的句柄调用也安全（编号不复用，不会误伤别的定时器）。
        // 返回：无。
        // 步骤：
        //   1) 句柄无效就直接返回，不报错 —— 重复取消是预期用法
        //   2) 线性查到同编号的定时器，只把 Dead 置 true，不立即从列表移除
        // 上游：业务收尾、切场景、对象销毁前
        // 下游：UpdateTimers 的收尾压缩（RemoveAll）才真正移除
        // 注意：不能在这里直接 Remove —— 可能正在遍历中，立即移除会让下标错位、漏掉后面的定时器。
        /// <summary>取消一个定时器；取消不存在的句柄不会报错。</summary>
        /// <param name="handle">之前返回的句柄。</param>
        public void Cancel(TimerHandle handle)
        {
            if (!handle.IsValid)
            {
                return;
            }

            for (int i = 0; i < m_timers.Count; i++)
            {
                if (m_timers[i].Id == handle.Id)
                {
                    m_timers[i].Dead = true;
                    return;
                }
            }
        }


        // 功能：在一个常驻宿主上启动协程，协程跨场景不销毁。
        // 入参：routine 协程体，不允许为 null（为 null 抛 ArgumentNullException）。
        // 返回：协程句柄，可交给 Stop 停止。
        // 步骤：
        //   1) 先挡 null —— 宁可当场报错，也不要让 StartCoroutine 抛一个难以定位的异常
        //   2) 交给本对象的 StartCoroutine，宿主就是单例对象本身
        // 上游：需要等帧或等秒数、但自身不是 MonoBehaviour 的代码
        // 下游：MonoBehaviour.StartCoroutine
        // 注意：协程不受 IsPaused 影响，走 Unity 自己的时间（WaitForSeconds 受 timeScale 影响）。
        /// <summary>启动一个协程，宿主就是这个常驻、跨场景不销毁的对象。</summary>
        /// <param name="routine">协程体。</param>
        /// <returns>协程句柄，可用于停止。</returns>
        public Coroutine Run(IEnumerator routine)
        {
            if (routine == null)
            {
                throw new ArgumentNullException(nameof(routine));
            }

            return StartCoroutine(routine);
        }

        // 功能：停止一个由 Run 启动的协程。
        // 入参：routine 协程句柄；null 直接忽略。
        // 返回：无。
        // 步骤：
        //   1) 判 null 再调 StopCoroutine —— 传 null 进去没有意义，也避免把错误留在引擎侧
        // 上游：启动过协程的业务代码
        // 下游：MonoBehaviour.StopCoroutine
        /// <summary>停止一个由 Run 启动的协程。</summary>
        /// <param name="routine">协程句柄；null 直接忽略。</param>
        public void Stop(Coroutine routine)
        {
            if (routine != null)
            {
                StopCoroutine(routine);
            }
        }

        // 功能：停止本宿主上的全部协程（切场景或收尾时用，会把别人挂的协程一起停掉）。
        /// <summary>停止本宿主上的全部协程，切场景或收尾时用。</summary>
        public void StopAll()
        {
            StopAllCoroutines();
        }

        // 功能：清空 Update / LateUpdate / FixedUpdate 三组每帧回调。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 三组列表各自 Clear，连派发中攒下的待加 / 待删队列一起清
        // 上游：切场景、回大厅这类整场收尾的场合，以及测试之间隔离状态
        // 下游：ListenerList.Clear
        // 注意：宿主是 DontDestroyOnLoad 的常驻对象，不会随场景销毁，所以「世界重启」必须显式调它，不能指望对象销毁自动清理。
        /// <summary>清空三种时机的全部每帧回调，用于整场收尾或测试隔离。</summary>
        public void RemoveAllListeners()
        {
            m_update.Clear();
            m_lateUpdate.Clear();
            m_fixedUpdate.Clear();
        }

        // 功能：取消全部未完成的定时器与延迟调用。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 直接清空主列表与到期缓存 —— 不走逐个 Cancel，省掉线性查找
        // 上游：切场景或收尾、OnBeforeDestroy
        // 下游：无
        // 注意：清空后旧句柄全部失去意义，但编号不复用，拿着旧句柄也不会误伤新定时器。
        /// <summary>取消全部未完成的定时器与延迟调用。</summary>
        public void CancelAllTimers()
        {
            m_timers.Clear();
            m_expired.Clear();
        }


        // 功能：每个渲染帧先派发 Update 回调，再推进定时器。
        // 入参：无（Unity 每帧调用）。
        // 返回：无。
        // 步骤：
        //   1) 先派发回调，让业务逻辑跑完，回调里新增 / 取消定时器都不影响本次派发
        //   2) 再用 Time.deltaTime 推进定时器；IsPaused 只拦这一步，不拦回调
        // 上游：Unity 引擎每帧
        // 下游：ListenerList.Dispatch、UpdateTimers
        private void Update()
        {
            m_update.Dispatch();
            UpdateTimers(Time.deltaTime);
        }

        // 功能：每个渲染帧的末尾派发 LateUpdate 回调。
        // 入参：无（Unity 每帧末尾调用）。
        // 返回：无。
        // 步骤：
        //   1) 只派发本时机的回调，不推进定时器 —— 定时器统一由 Update 推进，一帧只推进一次
        // 上游：Unity 引擎每帧末尾
        // 下游：ListenerList.Dispatch
        private void LateUpdate()
        {
            m_lateUpdate.Dispatch();
        }

        // 功能：每个物理帧派发 FixedUpdate 回调。
        // 入参：无（Unity 每个物理帧调用）。
        // 返回：无。
        // 步骤：
        //   1) 只派发本时机的回调，不推进定时器 —— 物理帧频率与渲染帧无关，计时统一走 Update
        // 上游：Unity 引擎每个物理帧
        // 下游：ListenerList.Dispatch
        private void FixedUpdate()
        {
            m_fixedUpdate.Dispatch();
        }

        // 功能：对象销毁前清空回调与定时器，避免常驻宿主留着已销毁对象的委托。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 先清三组每帧回调
        //   2) 再取消全部定时器
        // 上游：基类 SingletonAutoMono 的非虚 OnDestroy
        // 下游：RemoveAllListeners、CancelAllTimers
        // 注意：必须覆写 OnBeforeDestroy，不要自己写 private void OnDestroy() —— 基类的 OnDestroy 是非虚的，子类再写一个会把它隐藏掉，单例注销逻辑静默失效。
        // 注意：退出播放时宿主可能先于面板 / 管理器被销毁，所以别处摘回调前要先判 MonoManager.HasInstance。
        protected override void OnBeforeDestroy()
        {
            RemoveAllListeners();
            CancelAllTimers();
        }


        // 功能：创建一个定时器并返回句柄，是 CallLater 与 CallEvery 唯一的创建入口。
        // 入参：delaySeconds 首次触发的延迟，秒（小于 0 按 0 处理）；intervalSeconds 重复间隔，秒；repeat 是否重复；action 到期要执行的动作，不允许为 null。
        // 返回：新定时器的句柄；编号从 1 起单调递增、不复用。
        // 步骤：
        //   1) 先挡 null、再钳负延迟 —— 校验早于分配，避免建出跑不起来的定时器
        //   2) 用当前编号造 Timer，编号自增后才入表
        //   3) 入表并返回句柄；此时定时器还没被推进过
        // 上游：CallLater、CallEvery
        // 下游：Timer 构造函数、List.Add
        // 注意：编号单调递增、不复用，这样拿着旧句柄去 Cancel 新定时器永远不会误伤。
        private TimerHandle AddTimer(float delaySeconds, float intervalSeconds, bool repeat, UnityAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (delaySeconds < 0f)
            {
                delaySeconds = 0f;
            }

            Timer timer = new Timer(m_nextTimerId, delaySeconds, intervalSeconds, repeat, action);
            m_nextTimerId++;
            m_timers.Add(timer);
            return new TimerHandle(timer.Id);
        }

        // 功能：按 deltaTime 推进所有定时器，收集到期的，再统一触发。
        // 入参：deltaTime 本帧经过的秒数，播放模式来自 Time.deltaTime。
        // 返回：无。
        // 步骤：
        //   1) 暂停或没有定时器时直接返回
        //   2) 第一趟只收集剩余时间小于等于 0 的定时器，先不改列表
        //   3) 第二趟逐个触发；重复的加上 Interval，一次性的置 Dead
        //   4) 触发完再 RemoveAll 压缩一次 —— 分两趟是为了回调里增删定时器时下标不错位
        // 上游：Update
        // 下游：SafeInvoke、Timer 的字段
        // 注意：Dead 的定时器可能在别的回调里已经被取消，触发前要再判一次。
        private void UpdateTimers(float deltaTime)
        {
            if (IsPaused || m_timers.Count == 0)
            {
                return;
            }

            m_expired.Clear();
            for (int i = 0; i < m_timers.Count; i++)
            {
                Timer timer = m_timers[i];
                if (timer.Dead)
                {
                    continue;
                }

                timer.Remaining -= deltaTime;
                if (timer.Remaining <= 0f)
                {
                    m_expired.Add(timer);
                }
            }

            for (int i = 0; i < m_expired.Count; i++)
            {
                Timer timer = m_expired[i];
                if (timer.Dead)
                {
                    continue;
                }

                if (timer.Repeat)
                {
                    timer.Remaining += timer.Interval;
                }
                else
                {
                    timer.Dead = true;
                }

                SafeInvoke(timer.Callback);
            }

            if (m_expired.Count > 0)
            {
                m_timers.RemoveAll(s_isDead);
            }
        }

        // 功能：调用一个回调并吞掉异常，保证单个回调抛错不会打断本轮其余回调。
        // 入参：action 要调用的回调，调用方保证非 null。
        // 返回：无。
        // 步骤：
        //   1) 在 try 里 Invoke
        //   2) catch 住 Exception 交给 Debug.LogException，保留堆栈
        // 上游：ListenerList.Dispatch、UpdateTimers
        // 下游：Debug.LogException
        // 注意：这里逐回调容错不产生额外分配（自己用 List 手动遍历）；不要把同样的写法套到委托链上，那要 GetInvocationList、每次派发都会分配一个数组。
        private static void SafeInvoke(UnityAction action)
        {
            try
            {
                action.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        // 主要功能：单个定时器的数据载体，只存字段，不自己推进时间。
        // 主要数据成员：Id（编号）、Interval（秒）、Remaining（秒）、Repeat、Dead、Callback。
        // 主要外部方法：只有构造函数；字段由 MonoManager 的 AddTimer 与 UpdateTimers 直接读写。
        // 谁在用它：MonoManager.m_timers、m_expired 与 UpdateTimers。
        private sealed class Timer
        {
            // 编号：从 1 起单调递增、不复用，0 留给无效句柄。
            public readonly int Id;
            // 重复间隔，秒；一次性的定时器也会存首轮延迟，但不再使用。
            public readonly float Interval;
            // 是否重复：true 表示每次到期后按 Interval 再来一轮。
            public readonly bool Repeat;
            // 到期时要执行的动作。
            public readonly UnityAction Callback;

            // 距离下次到期还剩多少秒，小于等于 0 时本帧触发。
            public float Remaining;
            // 是否已死：被取消或一次性触发完置 true，等 RemoveAll 收尾移除。
            public bool Dead;

            // 功能：填好一个定时器的全部初始状态。
            // 入参：id 编号（从 1 起）；remaining 首次剩余秒数；interval 重复间隔秒数；repeat 是否重复；callback 到期动作。
            // 返回：无（构造函数不返回值）。
            // 步骤：
            //   1) 五个入参直接落字段，不做校验 —— 校验由 AddTimer 负责
            // 上游：AddTimer
            // 下游：无
            public Timer(int id, float remaining, float interval, bool repeat, UnityAction callback)
            {
                Id = id;
                Remaining = remaining;
                Interval = interval;
                Repeat = repeat;
                Callback = callback;
            }
        }

        // 主要功能：一组每帧回调，负责注册、注销与派发，并保证派发中增删不会错位。
        // 主要数据成员：m_items（当前名单）、m_pendingAdd / m_pendingRemove（派发中攒下的增删）、m_dispatching（是否正在派发）。
        // 主要外部方法：Add / Remove / Dispatch / Clear 与只读的 Count。
        // 谁在用它：MonoManager 的 m_update / m_lateUpdate / m_fixedUpdate 各持一个。
        private sealed class ListenerList
        {
            // 这组回调的名字（Update / LateUpdate / FixedUpdate），构造时传入。
            private readonly string m_name;
            // 当前名单，派发时按这个顺序调用；数量从 0 起。
            private readonly List<UnityAction> m_items = new List<UnityAction>();
            // 派发中新增的回调，本轮结束后追加进名单，所以下一帧才生效。
            private readonly List<UnityAction> m_pendingAdd = new List<UnityAction>();
            // 派发中移除的回调，本轮结束后从名单删掉，所以本次仍会被调用。
            private readonly List<UnityAction> m_pendingRemove = new List<UnityAction>();
            // 是否正在派发：true 时增删走待处理队列，避免遍历中改列表。
            private bool m_dispatching;

            // 功能：给这组回调起个名字，方便定位是哪个时机的名单。
            // 入参：name 时机名（Update / LateUpdate / FixedUpdate）。
            // 返回：无（构造函数不返回值）。
            // 步骤：
            //   1) 只存名字，不注册任何东西
            // 上游：MonoManager 的字段初始化
            // 下游：无
            public ListenerList(string name)
            {
                m_name = name;
            }

            // 当前名单数量，范围 0 起；不含待处理的新增与移除。
            public int Count
            {
                get { return m_items.Count; }
            }

            // 功能：往名单里加一个回调；派发中加则推迟到本轮结束，下一帧生效。
            // 入参：action 回调，不允许为 null（为 null 抛 ArgumentNullException）。
            // 返回：无。
            // 步骤：
            //   1) 先挡 null —— 让错误在注册点暴露，而不是等到派发那天
            //   2) 正在派发就攒进待加队列，避免遍历中 List 增长
            //   3) 否则直接加入名单
            // 上游：MonoManager.AddUpdateListener / AddLateUpdateListener / AddFixedUpdateListener
            // 下游：List.Add
            public void Add(UnityAction action)
            {
                if (action == null)
                {
                    throw new ArgumentNullException("action");
                }

                if (m_dispatching)
                {
                    m_pendingAdd.Add(action);
                    return;
                }

                m_items.Add(action);
            }

            // 功能：从名单里去掉一个回调；派发中去掉则推迟到本轮结束，本次仍会被调用。
            // 入参：action 之前注册的回调；null 直接忽略。
            // 返回：无。
            // 步骤：
            //   1) null 直接返回、不抛异常 —— 与 Add 不对称是有意的，退订做成幂等更好用
            //   2) 正在派发就攒进待删队列，本轮名单不动
            //   3) 否则直接从名单移除
            // 上游：MonoManager.RemoveUpdateListener / RemoveLateUpdateListener / RemoveFixedUpdateListener
            // 下游：List.Remove
            // 注意：同一个回调注册两次就要退订两次，List.Remove 每次只删一个。
            public void Remove(UnityAction action)
            {
                if (action == null)
                {
                    return;
                }

                if (m_dispatching)
                {
                    m_pendingRemove.Add(action);
                    return;
                }

                m_items.Remove(action);
            }

            // 功能：按当前名单顺序逐个调用回调，结束后再应用本轮攒下的增删。
            // 入参：无。
            // 返回：无。
            // 步骤：
            //   1) 名单为空时只应用待处理项就返回，省掉一次派发状态置位
            //   2) 置 m_dispatching 后遍历调用；在 finally 里复位并 ApplyPending，回调抛错也能复位
            // 上游：MonoManager 的 Update / LateUpdate / FixedUpdate
            // 下游：SafeInvoke、ApplyPending
            public void Dispatch()
            {
                if (m_items.Count == 0)
                {
                    ApplyPending();
                    return;
                }

                m_dispatching = true;
                try
                {
                    for (int i = 0; i < m_items.Count; i++)
                    {
                        SafeInvoke(m_items[i]);
                    }
                }
                finally
                {
                    m_dispatching = false;
                    ApplyPending();
                }
            }

            // 功能：清空当前名单与待处理队列。
            // 入参：无。
            // 返回：无。
            // 步骤：
            //   1) 三个列表一起清 —— 只清名单会留下待处理项，下一轮又被加回来
            // 上游：MonoManager.RemoveAllListeners
            // 下游：无
            public void Clear()
            {
                m_items.Clear();
                m_pendingAdd.Clear();
                m_pendingRemove.Clear();
            }

            // 功能：把本轮攒下的移除与新增应用到名单。
            // 入参：无。
            // 返回：无。
            // 步骤：
            //   1) 先应用移除、再应用新增 —— 顺序固定，本轮新增的回调不会被本轮移除吞掉
            //   2) 每应用完一类就清空对应队列
            // 上游：Dispatch、名单为空时的提前返回
            // 下游：List.Remove、List.AddRange
            private void ApplyPending()
            {
                if (m_pendingRemove.Count > 0)
                {
                    for (int i = 0; i < m_pendingRemove.Count; i++)
                    {
                        m_items.Remove(m_pendingRemove[i]);
                    }

                    m_pendingRemove.Clear();
                }

                if (m_pendingAdd.Count > 0)
                {
                    m_items.AddRange(m_pendingAdd);
                    m_pendingAdd.Clear();
                }
            }
        }
    }
}
