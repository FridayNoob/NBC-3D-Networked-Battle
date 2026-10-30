// 这个文件里有：
//   SingletonRegistry（类）：单例静态状态的集中管理点，登记重置与退出动作，并在进入 Play 模式时统一清空

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework
{
    // 主要功能：集中管理各单例的静态状态，登记重置与退出通知动作，并在固定时机统一触发。
    // 主要数据成员：s_resetters（重置动作表）、s_quitHandlers（退出通知表）、s_lock（保护两张表的锁）、s_quitHooked（退出是否已挂接）。
    // 主要外部方法：Register / RegisterQuitHandler / ResetAll（internal，仅框架与测试可见）。
    // 谁在用它：Singleton<T> 构造时登记重置；SingletonMono<T> / SingletonAutoMono<T> 在 Awake 里登记重置与退出；测试用 ResetAll 隔离用例。
    internal static class SingletonRegistry
    {
        // 已登记的重置动作表；元素为 Action，按登记顺序执行，去重后同一方法只保留一份
        private static readonly List<Action> s_resetters = new List<Action>();

        // 已登记的退出通知动作表；元素为 Action，按登记顺序执行，去重后同一方法只保留一份
        private static readonly List<Action> s_quitHandlers = new List<Action>();

        // 保护两张表的锁对象；不同泛型类型的单例各持有自己的锁，两个类型在不同线程同时构造会并发到这里，而 List 不是线程安全容器
        private static readonly object s_lock = new object();

        // 是否已经挂接 Application.quitting；取值为 false / true，关闭 Domain Reload 时靠它保证只订阅一次
        private static bool s_quitHooked;

        // 功能：第一次访问本类时挂接退出通知，让没用到单例的程序不付这份开销。
        static SingletonRegistry()
        {
            HookQuitting();
        }

        // 功能：登记一个把某单例的静态状态清干净的重置动作，供进入 Play 模式与测试统一调用。
        // 入参：reset 重置动作，通常是访问该泛型类型静态字段的静态方法；为 null 时直接忽略。
        // 返回：无
        // 步骤：
        //   1) 先挡掉 null，避免之后执行空委托
        //   2) 在 s_lock 内判断是否已登记，未登记才加入，避免同一方法重复执行
        // 上游：Singleton<T> 的受保护构造
        // 下游：ResetAll 遍历执行
        internal static void Register(Action reset)
        {
            if (reset == null)
            {
                return;
            }

            lock (s_lock)
            {
                if (!s_resetters.Contains(reset))
                {
                    s_resetters.Add(reset);
                }
            }
        }

        // 功能：登记一个应用即将退出时要执行的动作，由退出通知统一触发。
        // 入参：handler 退出通知动作；为 null 时直接忽略。
        // 返回：无
        // 步骤：
        //   1) 先挡掉 null，避免之后执行空委托
        //   2) 在 s_lock 内去重后加入，避免同一方法重复执行
        // 上游：SingletonMono<T> / SingletonAutoMono<T> 的 Awake
        // 下游：NotifyQuitting
        internal static void RegisterQuitHandler(Action handler)
        {
            if (handler == null)
            {
                return;
            }

            lock (s_lock)
            {
                if (!s_quitHandlers.Contains(handler))
                {
                    s_quitHandlers.Add(handler);
                }
            }
        }

        // 功能：Unity 进入 Play 模式的最早阶段（SubsystemRegistration）转调 ResetAll，清掉上一轮会话残留的静态状态。
        // 注意：这个回调必须留在非泛型类上——Unity 的 RuntimeInitializeOnLoadMethod 扫描不会为开放泛型类型实例化，写在 Singleton<T> 里可能根本不执行，而且失败是静默的。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetAllOnEnterPlayMode()
        {
            ResetAll();
        }

        // 功能：重置全部单例状态，让下一次访问重新创建实例。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 在 s_lock 内把重置表拷成数组，先拿到快照
        //   2) 解锁后逐个执行；单个失败只记日志，不拦断其它单例
        // 上游：ResetAllOnEnterPlayMode 自动调用；EditMode 与 PlayMode 测试直接调用（EditMode 下 Unity 不会自动触发）
        // 下游：各单例登记的重置委托
        // 注意：必须先快照再执行——重置动作可能访问其它单例并触发新的登记，直接遍历活列表会撞上「遍历中修改集合」。
        // 注意：关闭 Domain Reload 后静态字段会跨 Play 会话存活，不清就会把上一场战斗的数据带进下一次运行。
        internal static void ResetAll()
        {
            Action[] snapshot;
            lock (s_lock)
            {
                snapshot = s_resetters.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i]();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        // 功能：应用退出时遍历退出通知表，让每个已登记的单例自行清理。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 在 s_lock 内把退出通知表拷成数组
        //   2) 解锁后逐个执行；单个失败只记日志，不拦断其它
        // 上游：Application.quitting 事件（由 HookQuitting 挂接）
        // 下游：各单例登记的退出委托
        // 注意：退出播放时对象销毁顺序不受控，处理器里不要假设别的单例或场景对象还活着。
        private static void NotifyQuitting()
        {
            Action[] snapshot;
            lock (s_lock)
            {
                snapshot = s_quitHandlers.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i]();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        // 功能：挂接 Application.quitting，保证整个会话只订阅一次。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已挂接就直接返回
        //   2) 先把 s_quitHooked 置 true 再订阅，避免重入时重复挂接
        // 上游：SingletonRegistry 的静态构造
        // 下游：Application.quitting → NotifyQuitting
        // 注意：关闭 Domain Reload 时静态字段跨 Play 会话存活，s_quitHooked 就是为幂等而留的。
        private static void HookQuitting()
        {
            if (s_quitHooked)
            {
                return;
            }

            s_quitHooked = true;
            Application.quitting += NotifyQuitting;
        }
    }
}
