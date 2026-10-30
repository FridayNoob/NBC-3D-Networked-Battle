// 这个文件里有：
//   SingletonAutoMono<T>（泛型抽象类）：自动创建型 MonoBehaviour 单例基类，场景里已有就复用、没有才新建宿主对象，并保证全局唯一。

using System;
using UnityEngine;

namespace NBC.Framework
{
    /// <summary>自动创建型 MonoBehaviour 单例基类：场景里已有实例就复用，没有才新建一个跨场景常驻的宿主对象，供全局单例宿主继承。</summary>
    // 主要功能：把「按需创建、全局唯一、跨场景常驻」这套 MonoBehaviour 单例样板收进一个泛型基类，子类不用在场景里摆对象也能拿到实例。
    // 主要数据成员：s_instance（实例引用，null 表示尚未创建或已被销毁）、s_lock（创建锁）、s_quitting（退出中标记）、s_warnedDuplicate（重复告警是否已发过）。
    // 主要外部方法：Instance / GetInstance / HasInstance；子类初始化写 OnAwake，清理写 OnBeforeDestroy。
    // 谁在用它：本程序集的 MonoManager 继承它做协程宿主；Framework.UI / Framework.Audio 等经 MonoManager 间接用到；Tests 的探针单例直接验证重复创建。
    public abstract class SingletonAutoMono<T> : MonoBehaviour where T : SingletonAutoMono<T>
    {
        private static volatile T s_instance;   // 单例实例；取值 null 或唯一实例，null 表示尚未创建或已被销毁
        private static readonly object s_lock = new object();   // 创建 / 查找单例时用的锁对象，整个进程只 new 一次
        private static bool s_quitting;   // 应用是否正在退出；取值 true / false，true 期间 Instance 一律返回 null 且不再创建
        private static bool s_warnedDuplicate;   // 「场景里已存在同名单例」的告警是否已发过；true 后不再刷屏

        /// <summary>取单例实例，没有就当场创建一个并设为跨场景常驻。</summary>
        /// <returns>唯一实例；应用正在退出时返回 null，且不会创建。</returns>
        // 功能：取单例实例，没有就按「先找场景里现成的、再新建」的顺序造一个。
        // 入参：无
        // 返回：唯一实例；应用正在退出时返回 null，且不创建。
        // 步骤：
        //   1) 快路径直接返回已有实例，避免每次访问都抢锁
        //   2) 退出中直接返回 null，防止关服阶段又造出新对象
        //   3) 加锁后二次检查，补上等锁期间别人已经建好的情况
        //   4) 先在场景里找现成的，美术手摆的对象要复用，不能再建第二个
        //   5) 真没有才新建 GameObject 并挂上 T；DontDestroyOnLoad 只在运行时调用，让宿主跨场景存活
        // 上游：子类的静态入口、Boot / Game 的启动流程
        // 下游：FindFirstObjectByType、AddComponent（会同步触发 Awake 里的注册）
        // 注意：AddComponent 会同步触发 Awake，此时 s_instance 仍是 null，所以 Awake 里的重复检测不会误报。
        // 注意：FindFirstObjectByType 有遍历场景的开销，但只在 s_instance 为 null 时执行，不落在每帧路径上。
        public static T Instance
        {
            get
            {
                // 快路径
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
                    // 二次检查：等锁期间可能已经有人建好了
                    if (s_instance != null)
                    {
                        return s_instance;
                    }

                    // 第一步：场景里已经有了就直接复用，避免建出第二个
                    s_instance = FindFirstObjectByType<T>();
                    if (s_instance != null)
                    {
                        if (!s_warnedDuplicate)
                        {
                            s_warnedDuplicate = true;
                            Debug.LogWarning("[SingletonAutoMono] 场景中已存在 " + typeof(T).Name +
                                             "，直接复用。若这是美术手动摆放的对象，请确认它是有意为之。");
                        }

                        return s_instance;
                    }

                    // 第二步：真的没有，才创建
                    GameObject go = new GameObject(typeof(T).Name);

                    // DontDestroyOnLoad 只在运行时有效；EditMode 测试里调用它没有意义（编辑器会报错）
                    if (Application.isPlaying)
                    {
                        DontDestroyOnLoad(go);
                    }

                    // AddComponent 会【同步触发】Awake，此时 s_instance 仍是 null，
                    // 所以 Awake 里的「重复检测」不会误报。
                    s_instance = go.AddComponent<T>();
                    return s_instance;
                }
            }
        }

        /// <summary>取单例实例，等价于 Instance，只是保留旧框架的调用写法。</summary>
        // 功能：取单例实例，等价于 Instance，只是保留旧框架的调用写法。
        public static T GetInstance()
        {
            return Instance;
        }

        /// <summary>只查询是否已有实例，不会触发创建。</summary>
        // 功能：只查询实例是否已经存在，不会触发创建。
        // 入参：无
        // 返回：true 表示已有实例；false 表示没有。
        // 步骤：
        //   1) 直接读一个引用，不抢锁，因为只读不会建出对象
        // 上游：需要在取实例前先判断「有没有」的调用方（退出流程、测试清理）
        // 下游：无
        public static bool HasInstance
        {
            get { return s_instance != null; }
        }

        // 功能：把本对象登记为单例，并把静态状态复位与退出通知挂到 SingletonRegistry。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先做类型校验，this as T 为 null 说明挂错了类型，报错后不再往下走
        //   2) 已有别的实例就报错并返回，让先注册的那个继续生效，重复对象不抢位
        //   3) 登记成功后才挂 ResetStaticState 与退出处理器，重复对象不会重复登记
        //   4) 最后回调 OnAwake，子类初始化时已经能安全访问 Instance
        // 上游：Unity 生命周期；AddComponent 或场景加载时由引擎回调
        // 下游：SingletonRegistry.Register / RegisterQuitHandler、子类覆写的 OnAwake
        // 注意：同名的重复对象只报错、不接管也不销毁，s_instance 仍指向先注册的那个，多余对象要人工清理。
        // 注意：Awake 是非虚的，子类不要覆写它；初始化请写 OnAwake，单例注册只在这个方法里跑。
        private void Awake()
        {
            T self = this as T;

            if (self == null)
            {
                Debug.LogError("[SingletonAutoMono] 类型参数不匹配：" + GetType().Name +
                               " 不能注册为 " + typeof(T).Name + "。");
                return;
            }

            if (s_instance != null && !ReferenceEquals(s_instance, self))
            {
                Debug.LogError("[SingletonAutoMono] 已经存在 " + typeof(T).Name +
                               "，本对象（" + name + "）是多余的。请检查是否手动摆了重复对象。", this);
                return;
            }

            s_instance = self;
            SingletonRegistry.Register(ResetStaticState);
            SingletonRegistry.RegisterQuitHandler(OnApplicationQuitInternal);

            OnAwake();
        }

        // 功能：先给子类清理机会，再在「本对象仍是登记的那个实例」时把静态引用清空。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先回调 OnBeforeDestroy，让子类注销事件、停协程时实例还可用
        //   2) 只有 s_instance 仍指向自己才置 null，避免后销毁的重复对象把真正的实例清掉
        // 上游：Unity 生命周期（对象销毁 / 退出播放 / 切场景）
        // 下游：子类覆写的 OnBeforeDestroy；下一次访问 Instance 会重新创建
        // 注意：退出播放时 Unity 不保证各对象的销毁顺序，所以这里只认引用相等，不能假设自己一定最后被销毁。
        private void OnDestroy()
        {
            OnBeforeDestroy();

            if (ReferenceEquals(s_instance, this as T))
            {
                s_instance = null;
            }
        }

        // 功能：单例注册完成后的初始化钩子，子类的初始化写在这里。
        // 入参：无
        // 返回：无
        // 步骤：无（基类空实现，由子类覆写）
        // 上游：基类 Awake 的最后一步调用
        // 下游：子类覆写
        // 注意：回调到这里时 Instance 已经指向自己，可以放心被其它单例反向访问。
        protected virtual void OnAwake()
        {
        }

        // 功能：对象销毁前的清理钩子，子类在这里注销事件监听、停掉协程。
        // 入参：无
        // 返回：无
        // 步骤：无（基类空实现，由子类覆写）
        // 上游：基类 OnDestroy 的第一步调用
        // 下游：子类覆写
        // 注意：此时 Instance 仍指向本对象，清理逻辑里还能正常取到实例；引用要等基类这一步之后才清空。
        protected virtual void OnBeforeDestroy()
        {
        }

        // 功能：收到应用退出通知后把实例清空，并竖起「退出中」标记，禁止再创建。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先置 s_quitting，保证之后访问 Instance 直接返回 null
        //   2) 再清 s_instance，让已经拿到引用的调用方尽早看到失效
        // 上游：SingletonRegistry 的 Application.quitting 通知（由 RegisterQuitHandler 登记）
        // 下游：无
        // 注意：退出阶段对象销毁与 quitting 回调的先后不确定，先置标记再清引用，两种顺序都不会在退出时再造出新对象。
        private static void OnApplicationQuitInternal()
        {
            s_quitting = true;
            s_instance = null;
        }

        // 功能：把静态字段复位，让下一次访问重新创建实例。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 清空实例引用
        //   2) 复位退出标记与重复告警标记，下一次 Play 会话才能重新建、重新报
        // 上游：SingletonRegistry 在进入 Play 模式的最早阶段统一调用；测试之间也可手动调用
        // 下游：无
        // 注意：编辑器关闭 Domain Reload 时静态字段会跨 Play 会话存活，不靠这里复位就会带着上一次的实例进入下一次运行。
        private static void ResetStaticState()
        {
            s_instance = null;
            s_quitting = false;
            s_warnedDuplicate = false;
        }
    }
}
