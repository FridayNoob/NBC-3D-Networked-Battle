// 这个文件里有：
//   SingletonMono<T>（抽象类）：MonoBehaviour 单例基类，实例由场景或预制体提供，基类只负责唯一、可获取与自愈。
//
// 设计背景与历史缺陷的完整记录见 Docs\06-框架改造记录.md，代码里只留结论。

using System;
using UnityEngine;

namespace NBC.Framework
{
    // 主要功能：让挂在场景或预制体上的 MonoBehaviour 成为唯一实例，并在「Awake 没跑」「对象被销毁」「退出播放」这些边界上给出明确结果。
    // 主要数据成员：s_instance（静态单例引用）、m_registered（本对象是否走过注册）、s_quitting（是否正在退出应用）。
    // 主要外部方法：Instance / GetInstance() / HasInstance 三个静态只读入口；子类只覆写 OnAwake 与 OnBeforeDestroy。
    // 谁在用它：框架内由 SingletonRegistry 在重置与退出时回调；实际子类由各模块自行声明，当前示例见 Tests 的 PlayMode 探针。
    /// <summary>
    /// MonoBehaviour 单例基类：实例由场景（或预制体）摆好，基类只负责「唯一、可获取、自愈」。
    /// 需要自动创建实例的请用 <see cref="SingletonAutoMono{T}"/>。
    /// </summary>
    /// <typeparam name="T">具体的单例类型，必须是继承本类的 MonoBehaviour。</typeparam>
    public abstract class SingletonMono<T> : MonoBehaviour where T : SingletonMono<T>
    {
        // 静态单例引用；null 表示当前没有实例。锁外快路径要读它，所以必须 volatile，否则可能读到「非 null 但构造未完成」的对象。
        private static volatile T s_instance;

        // 创建与查找单例时用的锁；只有慢路径会抢，快路径不进锁。
        private static readonly object s_lock = new object();

        // 应用是否正在退出；true 时 Instance 不再查找、也不再创建新实例，避免退出过程中 new 出对象。
        private static bool s_quitting;

        // 是否已经就「Awake 没执行」报过一次错；true 后不再重复报，避免每帧刷屏。
        private static bool s_warnedMissingAwake;

        // 本对象是否已走过基类 Awake 的注册流程；false 说明子类把 Awake 隐藏了。
        // 注意：必须是实例字段而不是静态标志——关闭 Domain Reload 时静态状态会被重置，静态标志会把场里已存在的对象误判成「没注册过」。
        private bool m_registered;

        /// <summary>取单例实例：已有就直接返回，没有就按类型在场景里找一次，仍没有则报错并返回 null。</summary>
        /// <returns>单例实例；正在退出应用或场景中不存在时为 null（本类不主动创建对象）。</returns>
        public static T Instance
        {
            get
            {
                // 功能：取当前单例，取不到就按 T 在场景里找一次，仍没有就返回 null 并报错。
                // 入参：无
                // 返回：单例实例；退出播放期间或场景中没有该类型时为 null。
                // 步骤：
                //   1) 先读静态字段走快路径，绝大多数调用到这里就返回，不抢锁
                //   2) 退出播放期间直接返回 null，避免销毁顺序里再摸到半死的对象
                //   3) 进锁后二次检查，防止等锁期间别人已经注册好
                //   4) 用 FindFirstObjectByType 兜底，覆盖「对象在场景里但 Awake 还没跑」的情况
                //   5) 找到了却没注册过就报一次错，指向子类隐藏 Awake 这个最常见原因
                //   6) 一个都没有就报错并返回 null，不做自动创建
                // 上游：各程序集取单例的调用方，以及 GetInstance
                // 下游：FindFirstObjectByType、SingletonRegistry（间接）
                // 注意：找不到时不抛异常而是返回 null，调用方必须先判空——这是本类最需要防的坑。
                if (s_instance != null)
                {
                    return s_instance;
                }

                if (s_quitting)
                {
                    return null;
                }

                lock (s_lock)
                {
                    if (s_instance != null)
                    {
                        return s_instance;
                    }

                    s_instance = FindFirstObjectByType<T>();

                    if (s_instance != null && !s_instance.m_registered && !s_warnedMissingAwake)
                    {
                        s_warnedMissingAwake = true;
                        Debug.LogError(
                            "[SingletonMono] 找到了 " + typeof(T).Name + "，但基类的 Awake 从未执行过。" +
                            "最常见的原因是子类自己写了 private void Awake()，把基类的 Awake 隐藏了。" +
                            "请把子类的初始化逻辑改写到 protected override void OnAwake() 里。");
                    }
                    if(s_instance == null) 
                    {
                        Debug.LogError("[SingletonMono] 场景中没有 " + typeof(T).Name + "，返回 null。");
                    }
                    return s_instance;
                }
            }
        }

        /// <summary>取单例实例，等价于 Instance；保留旧框架的写法以便迁移。</summary>
        /// <returns>单例实例；场景中不存在时返回 null。</returns>
        public static T GetInstance()
        {
            // 功能：转发给 Instance，只为兼容旧调用习惯。
            return Instance;
        }

        /// <summary>是否已有实例；只读静态字段，不触发查找，可用于在取实例前先判存在。</summary>
        public static bool HasInstance
        {
            get { return s_instance != null; }
        }

        // 功能：把本对象登记成 T 的唯一实例，并在类型参数写错或场景里有重复时明确报错。
        // 入参：无（Unity 生命周期回调，基类里是 private 且非虚的）
        // 返回：无
        // 步骤：
        //   1) 先做 this as T，T 写错时（例如 class Foo : SingletonMono<Bar>）这里就是 null，只能报错退出
        //   2) 已经有别的实例就不再覆盖，保留先注册的那个，避免两个对象都以为自己是单例
        //   3) 写入静态字段并置 m_registered，供 Instance 判断 Awake 跑过没有
        //   4) 向 SingletonRegistry 登记静态重置与退出回调，关闭 Domain Reload 时由它统一清静态字段
        //   5) 最后才调 OnAwake，让子类在单例已就位之后再初始化
        // 上游：Unity 在对象激活时调用一次
        // 下游：OnAwake、SingletonRegistry.Register、SingletonRegistry.RegisterQuitHandler
        // 注意：Awake 在基类里是非虚的，子类不要写自己的 Awake 去接管初始化——Unity 只调派生类那一个，基类的会被静默跳过，单例注册随之失效。
        private void Awake()
        {
            T self = this as T;

            if (self == null)
            {
                Debug.LogError("[SingletonMono] 类型参数不匹配：" + GetType().Name +
                               " 不能注册为 " + typeof(T).Name + "。");
                return;
            }

            if (s_instance != null && !ReferenceEquals(s_instance, self))
            {
                Debug.LogError("[SingletonMono] 场景中存在多个 " + typeof(T).Name +
                               "：已保留先注册的那个，重复对象为 " + name + "。请检查场景与预制体。", this);
                return;
            }

            s_instance = self;
            m_registered = true;
            s_warnedMissingAwake = false;
            SingletonRegistry.Register(ResetStaticState);
            SingletonRegistry.RegisterQuitHandler(OnApplicationQuitInternal);

            OnAwake();
        }

        // 功能：对象销毁时先给子类收尾钩子，然后只清掉「自己那一个」静态引用。
        // 入参：无（Unity 生命周期回调，基类里是 private 且非虚的）
        // 返回：无
        // 步骤：
        //   1) 先调 OnBeforeDestroy，让子类在单例还在时注销监听、停止协程
        //   2) 只在当前实例就是自己时才清空
        // 上游：Unity 在对象销毁时调用，退出播放也走这里
        // 下游：OnBeforeDestroy
        // 注意：必须带「是不是自己」的判断——旧对象销毁时若无条件置 null，会把新对象刚注册的单例一起清掉，这是最典型的销毁顺序坑。
        private void OnDestroy()
        {
            OnBeforeDestroy();

            if (ReferenceEquals(s_instance, this as T))
            {
                s_instance = null;
            }
        }

        // 功能：单例注册完成后的初始化钩子，空实现；子类的初始化写这里，不要写 Awake。
        // 入参：无
        // 返回：无
        // 步骤：无（由子类填充）
        // 上游：基类 Awake 在登记完成后调用
        // 下游：无
        protected virtual void OnAwake()
        {
        }

        // 功能：对象销毁前的收尾钩子，空实现；子类在这里注销事件监听、停止协程。
        // 入参：无
        // 返回：无
        // 步骤：无（由子类填充）
        // 上游：基类 OnDestroy 最先调用
        // 下游：无
        protected virtual void OnBeforeDestroy()
        {
        }

        // 功能：应用退出时清空实例并立起「正在退出」标记，避免退出过程中再创建对象。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先置 s_quitting，之后 Instance 直接返回 null，不再触发查找
        //   2) 再清 s_instance，松开对即将销毁对象的引用
        // 上游：SingletonRegistry.RegisterQuitHandler 在 Application.quitting 时回调
        // 下游：无
        private static void OnApplicationQuitInternal()
        {
            s_quitting = true;
            s_instance = null;
        }

        // 功能：把三个静态字段恢复成初始值，让下一次进入 Play 从「没有实例」开始。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 清 s_instance 与 s_quitting，让新的会话重新走注册
        //   2) 复位 s_warnedMissingAwake，否则上一次会话的报错会压制这一次的提示
        // 上游：SingletonRegistry 在 SubsystemRegistration 阶段统一调用；EditMode 测试之间也经它隔离状态
        // 下游：无
        // 注意：m_registered 是实例字段，故意不在清理范围内——它描述的是「某个对象自己」的事实，静态重置不该改它。
        private static void ResetStaticState()
        {
            s_instance = null;
            s_quitting = false;
            s_warnedMissingAwake = false;
        }
    }
}
