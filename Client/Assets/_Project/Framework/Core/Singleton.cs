// 这个文件里有：
//   Singleton<T>（抽象类）：纯 C# 单例基类，保证每个子类全进程唯一、外部 new 立即失败，并在发布前跑完 OnInit

using System;

namespace NBC.Framework
{
    // 主要功能：用 CRTP 写法的纯 C# 泛型单例基类，保证每个子类全进程只有一个实例，外部 new 当场抛异常。
    // 主要数据成员：s_instance（已发布的实例）、s_pending（OnInit 期间暂存的实例）、s_lock（创建 / 释放锁）、t_creatingInsideSingleton（本线程内部创建标记）。
    // 主要外部方法：Instance（取单例，不存在则创建）、GetInstance（旧写法转发）、HasInstance、DisposeInstance。
    // 谁在用它：Framework 的 EventCenter / UIManager / SceneLoader / LogSystem / InputManager / AssetManager / AudioManager 与 Game 的 ConfigMgr 继承它；Game / Boot / Editor 与测试经它取单例。
    // 注意：子类一律写 sealed —— 泛型静态字段是每个封闭类型一份，被继承会多出一份状态；为什么不用反射见 Docs\06-框架改造记录.md 的「单例三件套」一节。
    /// <summary>
    /// 纯 C# 单例基类（T 是自己的子类），适用于不需要挂在 GameObject 上的管理器。
    /// 需要 MonoBehaviour 生命周期的请用 <see cref="SingletonMono{T}"/> 或 <see cref="SingletonAutoMono{T}"/>。
    /// </summary>
    /// <typeparam name="T">具体单例类型，必须继承本类并提供 public 无参构造。</typeparam>
    public abstract class Singleton<T> where T : Singleton<T>, new()
    {
        // 已发布的单例实例；取值是 null 或已构造完成的 T。
        private static volatile T s_instance;

        // OnInit 执行期间暂存的新实例；非 null 表示本线程正在初始化，重入 Instance 时直接返回它。
        private static volatile T s_pending;

        // 创建 / 释放单例时抢的锁，只保护慢路径，快路径无锁。
        private static readonly object s_lock = new object();

        // 本线程是否正在由 Singleton 内部创建实例；[ThreadStatic] 让它只属于当前线程，取值 true / false，默认 false 正好拦住外部 new。
        [ThreadStatic]
        private static bool t_creatingInsideSingleton;

        /// <summary>获取单例实例；不存在则创建，创建完成之后才对外发布。</summary>
        public static T Instance
        {
            get
            {
                // 功能：取单例；没建过就现在建，建好且 OnInit 跑完之后才对外发布。
                // 入参：无
                // 返回：唯一的 T 实例；首次访问会触发创建，DisposeInstance 之后再访问会重新创建。
                // 步骤：
                //   1) 先无锁读 s_instance，已发布就直接返回（绝大多数调用走这里，必须便宜）
                //   2) 没读到才加锁，锁内二次检查：等锁期间可能已被别的线程发布
                //   3) s_pending 非空说明本线程正在跑 OnInit，直接返回它，避免递归创建或死等自己持有的锁
                //   4) 置本线程「内部创建中」标记再 new T()，让构造函数放行，finally 里无论成败都复位
                //   5) 先挂 s_pending 再跑 OnInit，最后才写 s_instance：别的线程与 OnInit 里的重入都拿不到半成品
                // 上游：所有子类的调用方（Game / Boot / Editor），GetInstance 也转发到这里
                // 下游：子类构造函数、OnInit、SingletonRegistry.Register
                // 注意：s_instance 必须 volatile —— 第 1 步在锁外读它，不加可能读到「非 null 但尚未构造完」的对象。
                T published = s_instance;
                if (published != null)
                {
                    return published;
                }

                lock (s_lock)
                {
                    if (s_instance != null)
                    {
                        return s_instance;
                    }

                    if (s_pending != null)
                    {
                        return s_pending;
                    }

                    T created;
                    t_creatingInsideSingleton = true;
                    try
                    {
                        created = new T();
                    }
                    finally
                    {
                        t_creatingInsideSingleton = false;
                    }

                    s_pending = created;
                    created.OnInit();
                    s_pending = null;

                    s_instance = created;
                    return created;
                }
            }
        }

        /// <summary>获取单例实例，等价于 <see cref="Instance"/>；保留旧框架的调用写法。</summary>
        public static T GetInstance()
        {
            // 功能：一行转发到 Instance。
            return Instance;
        }

        /// <summary>是否已有已发布的实例；只查询，不触发创建。调试面板与测试用。</summary>
        public static bool HasInstance
        {
            // 功能：读 s_instance 判断是否已发布，不会创建实例。
            // 入参：无
            // 返回：true 表示已有已发布实例；false 表示尚未创建或已被释放。
            // 步骤：直接比较字段，不加锁，只读一次 volatile 字段拿到瞬时值即可。
            // 上游：调试面板、测试与业务里的防御性判断
            // 下游：无
            get { return s_instance != null; }
        }

        // 功能：拦住外部 new，保证单例唯一；同时向 SingletonRegistry 登记跨 Play 会话的重置动作。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 检查本线程的 t_creatingInsideSingleton：不是 Instance 发起的创建就抛异常，消息直接点名「改用 Xxx.Instance」
        //   2) 合法创建才登记 ResetStaticState，供关闭 Domain Reload 时在 SubsystemRegistration 阶段清空静态状态
        // 上游：只有 Singleton<T>.Instance 内部的 new T() 会进入
        // 下游：SingletonRegistry.Register(ResetStaticState)
        // 注意：不能靠 private 构造禁 new —— where T : new() 反过来要求 public 无参构造，不想用反射就只能运行期拦。
        protected Singleton()
        {
            if (!t_creatingInsideSingleton)
            {
                throw new InvalidOperationException(
                    "[Singleton] " + typeof(T).Name + " 是单例，不允许 new。请改用 " +
                    typeof(T).Name + ".Instance 或 " + typeof(T).Name + ".GetInstance()。");
            }

            SingletonRegistry.Register(ResetStaticState);
        }

        // 功能：初始化钩子，单例第一次创建时、发布之前调用一次。
        // 入参：无
        // 返回：无
        // 步骤：空实现，留给子类覆写；初始化写在这里而不是构造函数，创建窗口由基类控制。
        // 上游：Instance 在 new T() 之后、写 s_instance 之前调用
        // 下游：子类覆写
        protected virtual void OnInit()
        {
        }

        // 功能：释放钩子，交给子类注销监听、关闭连接。
        // 入参：无
        // 返回：无
        // 步骤：空实现，留给子类覆写。
        // 上游：DisposeInstance 清空 s_instance 之后调用
        // 下游：子类覆写
        protected virtual void OnDispose()
        {
        }

        /// <summary>主动释放单例：清空实例并调用 OnDispose；之后再访问 Instance 会重新创建。</summary>
        public static void DisposeInstance()
        {
            // 功能：释放已发布的单例，让子类收尾（编辑器退出、测试收尾、确定不再需要时）。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 加锁后先取 s_instance，为空就直接返回，没有可释放的对象
            //   2) 先把 s_instance 置空再回调 OnDispose：期间访问 Instance 会新建实例，而不是拿到正在释放的旧对象
            // 上游：编辑器退出、EditMode 测试收尾
            // 下游：子类 OnDispose
            // 注意：OnDispose 在锁内执行，回调里不要长时间阻塞，也不要依赖旧实例还留在字段里。
            lock (s_lock)
            {
                T disposing = s_instance;
                if (disposing == null)
                {
                    return;
                }

                s_instance = null;
                disposing.OnDispose();
            }
        }

        // 功能：清空本封闭类型的静态状态，供进入 Play 模式前统一重置，也用于 EditMode 测试之间隔离状态。
        // 入参：无
        // 返回：无
        // 步骤：只置空 s_instance 与 s_pending，故意不调用 OnDispose。
        // 上游：SingletonRegistry 在 SubsystemRegistration 阶段统一调用（EditMode 测试需手动 ResetAll）
        // 下游：无
        // 注意：关闭 Domain Reload 后静态字段会跨 Play 会话存活，不重置就会把上一场的实例带进下一场；而 SubsystemRegistration 阶段跑用户代码风险大，所以这里只清字段、不做收尾。
        private static void ResetStaticState()
        {
            s_instance = null;
            s_pending = null;
        }
    }
}
