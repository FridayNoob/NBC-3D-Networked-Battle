// ============================================================================
//  NBC.Framework · 单例注册表（非泛型，Unity 生命周期回调的落点）
//  来源：唐老师框架 Base/BaseManager.cs 的替代品之一（见 Docs/06-框架改造记录.md FW-01 / FW-M01）
//
//  为什么需要这个类？
//    单例的状态保存在【泛型类的静态字段】里，例如 Singleton<T> 的 s_instance。
//    C# 的泛型是"每个封闭类型一份静态字段"（Singleton<A> 与 Singleton<B> 各有各的），
//    这本身是我们想要的。但它带来两个麻烦：
//
//    麻烦 1：Unity 的 [RuntimeInitializeOnLoadMethod] 扫描【不会为开放泛型类型实例化】。
//            把该特性写在 Singleton<T> 里，存在"Unity 根本不调用"的风险，而且
//            失败是【静默的】—— 这正是本项目最想避免的一类缺陷（见 Docs/06 §五）。
//            所以把回调放在这个【非泛型】类里，各单例把自己的"重置动作"注册进来。
//
//    麻烦 2：编辑器关闭 Domain Reload（Enter Play Mode Options）后，静态字段
//            会跨 Play 会话存活，上一次战斗的数据会带进下一次运行。
//            标准解法就是在 SubsystemRegistration 阶段把静态状态清干净。
//
//  设计取舍：注册的是 Action（重置委托），不是反射调用。
//            本项目在 IL2CPP 路径上已实测过反射的代价（D17 / protobuf-net），
//            能用委托解决的地方不用反射。
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework
{
    /// <summary>
    /// 单例静态状态的集中管理点。
    /// 只做三件事：登记重置动作、统一重置、统一挂接 Application.quitting。
    /// 它是 internal 的：业务层不需要知道它的存在。
    /// </summary>
    internal static class SingletonRegistry
    {
        /// <summary>已登记的重置动作（去重，避免重复登记同一方法）。</summary>
        private static readonly List<Action> s_resetters = new List<Action>();

        /// <summary>已登记的退出通知动作。</summary>
        private static readonly List<Action> s_quitHandlers = new List<Action>();

        /// <summary>
        /// 保护两张表的锁。
        /// 为什么需要：不同单例类型有各自的锁，所以"两个类型在不同线程上同时构造"
        /// 会并发走到这里。List 不是线程安全容器，必须自己加锁。
        /// </summary>
        private static readonly object s_lock = new object();

        /// <summary>是否已经挂接过 Application.quitting，避免重复订阅。</summary>
        private static bool s_quitHooked;

        /// <summary>
        /// 静态构造函数：第一次访问本类时执行一次。
        /// 在这里挂接退出通知，保证"只有真的用了单例才付这份开销"。
        /// </summary>
        static SingletonRegistry()
        {
            HookQuitting();
        }

        /// <summary>
        /// 登记一个"把某单例的静态状态清干净"的动作。
        /// </summary>
        /// <param name="reset">重置动作，通常是一个访问该泛型类型静态字段的静态方法。</param>
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

        /// <summary>
        /// 登记一个"应用即将退出"时要执行的动作。
        /// </summary>
        /// <param name="handler">退出通知动作。</param>
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

        /// <summary>
        /// 清空所有单例的静态状态。
        /// 由 Unity 在进入 Play 模式的最早阶段（SubsystemRegistration）自动调用一次。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetAllOnEnterPlayMode()
        {
            ResetAll();
        }

        /// <summary>
        /// 重置全部单例状态。测试里可以直接调用（Unity 的自动调用在 EditMode 测试中不会发生）。
        /// </summary>
        internal static void ResetAll()
        {
            // 先快照再执行：重置动作本身可能访问其它单例（进而触发登记），
            // 直接遍历活列表会撞上"遍历中修改集合"。
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
                    // 重置失败不能拦断其它单例，但必须留下痕迹（不静默）
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>
        /// 通知所有单例"应用要退出了"。
        /// </summary>
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

        /// <summary>
        /// 挂接 Application.quitting（幂等）。
        /// 说明：静态字段在 Domain Reload 关闭时会存活，所以这里用 s_quitHooked 保证只订阅一次。
        /// </summary>
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
