// ============================================================================
//  NBC.Framework · 公共 Mono 宿主（每帧回调 / 定时器 / 延迟调用 / 协程）
//  替代：唐老师框架 Mono/MonoMgr.cs + Mono/MonoController.cs（原 54 + 44 行）
//  需求条目：FW-M03（Update/LateUpdate/FixedUpdate、定时器、延迟调用、协程、可暂停）
//            FW-M13（CoroutineRunner 统一宿主）
//  缺陷编号：审计新增 P-13（宿主创建窗口 + 过时的 StartCoroutine 重载）
//            + 审计新增 P-05（订阅 / 退订不对称）
//  完整记录：Docs/06-框架改造记录.md §四 P-05 / P-13、§十二
//
//  ---------------------------------------------------------------------------
//  一、原版错在哪
//  ---------------------------------------------------------------------------
//  原版是"**纯 C# 单例 + 另一个 MonoBehaviour 宿主**"两个对象：
//
//      public class MonoMgr : BaseManager<MonoMgr>          // 纯 C# 单例
//      {
//          public MonoMgr()
//          {
//              // :15 在构造函数里 new 一个场景对象出来
//              GameObject obj = new GameObject("MonoController");
//              controller = obj.AddComponent<MonoController>();
//          }
//          ...
//      }
//      public class MonoController : MonoBehaviour
//      {
//          private void Start() { DontDestroyOnLoad(gameObject); }   // :17-19 在 Start 里才保护
//          private void Update() { updateEvent?.Invoke(); }
//      }
//
//  **P-13**：宿主对象在构造函数里创建，但 `DontDestroyOnLoad` 要等到 `Start` 才调用。
//  于是存在一个"对象已经建好、但还没受保护"的一帧窗口 —— 如果这一帧内发生场景切换，
//  宿主就会被销毁，之后所有注册进来的每帧回调**静默失效**。
//
//  另外 `StartCoroutine(string methodName, object value)` 两个重载转发的是**过时 API**
//  （Unity 早已不推荐按方法名启动协程），基本没用。
//
//  **P-05**：`MusicMgr` / `InputMgr` 在**构造函数里** `AddUpdateListener(Update)`，
//  而全工程 grep 不到任何 `RemoveUpdateListener` —— **订阅与退订不对称**。
//  当前因为单例永驻没有泄漏，但这个模式一旦被业务复制就是稳定的泄漏源。
//
//  ---------------------------------------------------------------------------
//  二、改造的两条主线
//  ---------------------------------------------------------------------------
//  ① **宿主与门面合并**：直接用 A1 的 `SingletonAutoMono<T>` ——
//     门面对象本身就是那个 MonoBehaviour。于是：
//       · 不再有"构造函数里 new 场景对象"这种写法
//       · `DontDestroyOnLoad` 在创建那一刻就调用（A1 的实现里就是这么写的），
//         **P-13 的一帧窗口直接消失**
//       · 少一个对象、少一层转发
//
//  ② **订阅 / 退订对称，并且可见**：
//     每个 `AddXxxListener` 都配一个 `RemoveXxxListener`；
//     另提供 `UpdateListenerCount` 等计数接口，接到 PerfHUD（E1）上就能**看见是否在涨**。
//     "不对称"这件事从"靠自觉"变成"屏幕上有个数字"。
//
//  ---------------------------------------------------------------------------
//  三、派发语义：与 A3 事件中心保持一致
//  ---------------------------------------------------------------------------
//  每帧回调的列表也是"有人在遍历时被改"的经典场景，所以沿用 A3 定下的同一条契约：
//
//      · 派发中**新增**的监听者 -> 下一帧生效
//      · 派发中**移除**的监听者 -> 下一帧生效（本次仍会被调用）
//
//  实现方式与 A3 不同（A3 靠"委托不可变"，这里靠"待处理队列"），
//  但**对外语义一致** —— 学一套规则就够，不用记两套。
//
//  ---------------------------------------------------------------------------
//  四、关于"可暂停"的确切含义（写清楚，避免猜）
//  ---------------------------------------------------------------------------
//      IsPaused = true 时：**定时器与延迟调用停止推进**，每帧回调**照常执行**。
//
//  为什么每帧回调不一起停：它们是"游戏的帧驱动"。真要把整个游戏冻住，
//  应该用 `Time.timeScale = 0`（那是 Unity 的机制，物理、动画、协程都会跟着停）。
//  把这个类里的 IsPaused 定义成"只停时间相关的东西"，语义单一、可预期。
//
//  协程**不受 IsPaused 影响** —— 它们用的是 Unity 自己的时间（`WaitForSeconds`
//  受 timeScale 影响，`WaitForSecondsRealtime` 不受）。
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace NBC.Framework
{
    /// <summary>
    /// 公共 Mono 宿主：给"不是 MonoBehaviour 的代码"提供每帧回调、定时器、延迟调用与协程。
    /// </summary>
    public sealed class MonoManager : SingletonAutoMono<MonoManager>
    {
        /// <summary>Update 回调列表。</summary>
        private readonly ListenerList m_update = new ListenerList("Update");

        /// <summary>LateUpdate 回调列表。</summary>
        private readonly ListenerList m_lateUpdate = new ListenerList("LateUpdate");

        /// <summary>FixedUpdate 回调列表。</summary>
        private readonly ListenerList m_fixedUpdate = new ListenerList("FixedUpdate");

        /// <summary>全部定时器。数量通常只有几十个，所以取消用线性查找即可。</summary>
        private readonly List<Timer> m_timers = new List<Timer>();

        /// <summary>本帧到期的定时器（复用同一个列表，避免每帧分配）。</summary>
        private readonly List<Timer> m_expired = new List<Timer>();

        /// <summary>判断定时器是否已死的缓存委托，避免 RemoveAll 每次分配闭包。</summary>
        private static readonly Predicate<Timer> s_isDead = t => t.Dead;

        private int m_nextTimerId = 1;

        /// <summary>
        /// 是否暂停。true 时定时器 / 延迟调用停止推进，每帧回调不受影响（见文件头说明）。
        /// </summary>
        public bool IsPaused { get; set; }

        // ====================================================================
        //  每帧回调
        // ====================================================================

        /// <summary>当前 Update 回调数量。接到 PerfHUD 上可以看见监听是否在泄漏。</summary>
        public int UpdateListenerCount
        {
            get { return m_update.Count; }
        }

        /// <summary>当前 LateUpdate 回调数量。</summary>
        public int LateUpdateListenerCount
        {
            get { return m_lateUpdate.Count; }
        }

        /// <summary>当前 FixedUpdate 回调数量。</summary>
        public int FixedUpdateListenerCount
        {
            get { return m_fixedUpdate.Count; }
        }

        /// <summary>当前未完成的定时器数量。</summary>
        public int TimerCount
        {
            get { return m_timers.Count; }
        }

        /// <summary>注册一个每帧回调。</summary>
        /// <param name="action">回调。不允许为 null。</param>
        public void AddUpdateListener(UnityAction action)
        {
            m_update.Add(action);
        }

        /// <summary>注销一个每帧回调。</summary>
        /// <param name="action">之前注册的回调。</param>
        public void RemoveUpdateListener(UnityAction action)
        {
            m_update.Remove(action);
        }

        /// <summary>注册一个帧末回调（LateUpdate）。</summary>
        /// <param name="action">回调。不允许为 null。</param>
        public void AddLateUpdateListener(UnityAction action)
        {
            m_lateUpdate.Add(action);
        }

        /// <summary>注销一个帧末回调。</summary>
        /// <param name="action">之前注册的回调。</param>
        public void RemoveLateUpdateListener(UnityAction action)
        {
            m_lateUpdate.Remove(action);
        }

        /// <summary>注册一个物理帧回调（FixedUpdate）。</summary>
        /// <param name="action">回调。不允许为 null。</param>
        public void AddFixedUpdateListener(UnityAction action)
        {
            m_fixedUpdate.Add(action);
        }

        /// <summary>注销一个物理帧回调。</summary>
        /// <param name="action">之前注册的回调。</param>
        public void RemoveFixedUpdateListener(UnityAction action)
        {
            m_fixedUpdate.Remove(action);
        }

        // ====================================================================
        //  定时器 / 延迟调用
        // ====================================================================

        /// <summary>
        /// 延迟若干秒后执行一次。
        /// </summary>
        /// <param name="delaySeconds">延迟秒数（受 IsPaused 影响）。</param>
        /// <param name="action">到点后执行的动作。</param>
        /// <returns>可用于取消的句柄。</returns>
        public TimerHandle CallLater(float delaySeconds, UnityAction action)
        {
            return AddTimer(delaySeconds, delaySeconds, false, action);
        }

        /// <summary>
        /// 每隔若干秒执行一次，直到被取消。
        /// </summary>
        /// <param name="intervalSeconds">间隔秒数（受 IsPaused 影响）。</param>
        /// <param name="action">每次间隔到点时执行的动作。</param>
        /// <returns>可用于取消的句柄。</returns>
        public TimerHandle CallEvery(float intervalSeconds, UnityAction action)
        {
            return AddTimer(intervalSeconds, intervalSeconds, true, action);
        }

        /// <summary>
        /// 取消一个定时器。取消不存在的句柄是安全的（不会报错）。
        /// </summary>
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
                    // 只打标记不立即移除：可能正在遍历中，立即移除会让下标错位。
                    // 真正移除发生在 UpdateTimers 的收尾压缩里。
                    m_timers[i].Dead = true;
                    return;
                }
            }
        }

        // ====================================================================
        //  协程（FW-M13：统一宿主）
        // ====================================================================

        /// <summary>
        /// 启动一个协程，宿主就是这个对象（常驻、跨场景不销毁）。
        /// </summary>
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

        /// <summary>停止一个由 <see cref="Run"/> 启动的协程。</summary>
        /// <param name="routine">协程句柄。</param>
        public void Stop(Coroutine routine)
        {
            if (routine != null)
            {
                StopCoroutine(routine);
            }
        }

        /// <summary>停止本宿主上的全部协程。切场景 / 收尾时用。</summary>
        public void StopAll()
        {
            StopAllCoroutines();
        }

        /// <summary>
        /// 清空三种时机的全部每帧回调。
        /// 用途：切场景、回大厅这类"整场收尾"的场合，以及测试之间隔离状态。
        /// 有了它，"世界重启"不必依赖对象销毁（对象是常驻的，不会自己销毁）。
        /// </summary>
        public void RemoveAllListeners()
        {
            m_update.Clear();
            m_lateUpdate.Clear();
            m_fixedUpdate.Clear();
        }

        /// <summary>取消全部未完成的定时器与延迟调用。</summary>
        public void CancelAllTimers()
        {
            m_timers.Clear();
            m_expired.Clear();
        }

        // ====================================================================
        //  Unity 生命周期
        // ====================================================================

        private void Update()
        {
            m_update.Dispatch();
            UpdateTimers(Time.deltaTime);
        }

        private void LateUpdate()
        {
            m_lateUpdate.Dispatch();
        }

        private void FixedUpdate()
        {
            m_fixedUpdate.Dispatch();
        }

        /// <summary>
        /// 对象销毁前清理。
        /// ⚠️ 注意这里覆写的是 `OnBeforeDestroy` 而不是 `OnDestroy`：
        /// 基类 `SingletonAutoMono&lt;T&gt;` 里的 `OnDestroy` 是**非虚**的，
        /// 如果这里自己写一个 `private void OnDestroy()`，会把基类的隐藏掉，
        /// 单例的注销逻辑就**静默失效**了（这正是原框架 BasePanel 的病，审计 P-08）。
        /// </summary>
        protected override void OnBeforeDestroy()
        {
            RemoveAllListeners();
            CancelAllTimers();
        }

        // ====================================================================
        //  内部实现
        // ====================================================================

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
            m_nextTimerId++;                 // id 单调递增、不复用
            m_timers.Add(timer);
            return new TimerHandle(timer.Id);
        }

        /// <summary>
        /// 推进定时器。
        /// 分成两趟：先收集到期的，再统一触发 —— 因为触发回调时可能又增删定时器，
        /// 在遍历中直接改列表会让下标错位。
        /// </summary>
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
                    continue;               // 可能在别的回调里已经被取消了
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

        /// <summary>
        /// 调用一个回调并兜住异常。
        /// 为什么这里做逐回调容错，而 A3 的事件中心没有：
        /// 这里是我们自己用 List 手动遍历的，容错**不产生任何额外分配**；
        /// 而事件中心用的是委托链，要做到逐监听者容错必须调 `GetInvocationList()`（每次派发分配一个数组）。
        /// **取舍取决于数据结构，不是随手定的。**
        /// </summary>
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

        /// <summary>单个定时器的数据。</summary>
        private sealed class Timer
        {
            public readonly int Id;
            public readonly float Interval;
            public readonly bool Repeat;
            public readonly UnityAction Callback;

            public float Remaining;
            public bool Dead;

            public Timer(int id, float remaining, float interval, bool repeat, UnityAction callback)
            {
                Id = id;
                Remaining = remaining;
                Interval = interval;
                Repeat = repeat;
                Callback = callback;
            }
        }

        /// <summary>
        /// 一组"每帧要被调用的回调"，自带派发中增删的安全处理。
        /// 三个时机（Update / LateUpdate / FixedUpdate）各持一个实例。
        /// </summary>
        private sealed class ListenerList
        {
            private readonly string m_name;
            private readonly List<UnityAction> m_items = new List<UnityAction>();
            private readonly List<UnityAction> m_pendingAdd = new List<UnityAction>();
            private readonly List<UnityAction> m_pendingRemove = new List<UnityAction>();
            private bool m_dispatching;

            public ListenerList(string name)
            {
                m_name = name;
            }

            public int Count
            {
                get { return m_items.Count; }
            }

            public void Add(UnityAction action)
            {
                if (action == null)
                {
                    throw new ArgumentNullException("action");
                }

                // 派发中新增 -> 攒起来，本轮结束后再生效（下一帧才会被调用）
                if (m_dispatching)
                {
                    m_pendingAdd.Add(action);
                    return;
                }

                m_items.Add(action);
            }

            public void Remove(UnityAction action)
            {
                if (action == null)
                {
                    return;
                }

                // 派发中移除 -> 同样攒起来，本轮已经锁定的名单不受影响
                if (m_dispatching)
                {
                    m_pendingRemove.Add(action);
                    return;
                }

                m_items.Remove(action);
            }

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

            public void Clear()
            {
                m_items.Clear();
                m_pendingAdd.Clear();
                m_pendingRemove.Clear();
            }

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
