// ============================================================================
//  NBC.Framework · 纯 C# 单例基类
//  替代：唐老师框架 Base/BaseManager.cs（原 18 行）
//  缺陷编号：FW-01（可被 new 出第二个实例）+ FW-M01（线程 / 场景重载 / 编辑器退出安全）
//  完整记录：Docs/06-框架改造记录.md §三 FW-01
//
//  ---------------------------------------------------------------------------
//  一、原版错在哪
//  ---------------------------------------------------------------------------
//      public class BaseManager<T> where T : new()
//      {
//          private static T instance;
//          public static T GetInstance()
//          {
//              if (instance == null) instance = new T();
//              return instance;
//          }
//      }
//
//  任何人写 new EventCenter() 都能得到一个"和单例不是同一个"的对象。
//  两份状态各改各的，不报错、不警告 —— 排查起来毫无线索。
//
//  ---------------------------------------------------------------------------
//  二、为什么不能简单地"给子类加 private 构造"
//  ---------------------------------------------------------------------------
//  这是本条最关键、也最容易踩的一点：
//      where T : new()  这个约束，反过来【要求 T 具有 public 无参构造】。
//  也就是说 C# 里"禁止外部 new"（private 构造）与"用 new T() 创建"（要求 public 构造）
//  【互相排斥】。想同时做到，只剩一条路：
//      Activator.CreateInstance(typeof(T), nonPublic: true)
//  而那是【反射】。
//
//  本项目已经用实测付过反射的学费：M0 的 D17 实验里，protobuf-net 就是因为
//  在 IL2CPP 上依赖运行时反射建模而不可用（见 Docs/01 §5.4.6 与 Docs/12）。
//  所以这里不用反射。
//
//  ---------------------------------------------------------------------------
//  三、破解办法：一个 [ThreadStatic] 的"内部创建中"标记
//  ---------------------------------------------------------------------------
//  既然不能靠构造可见性，就让【构造函数自己判断"这次 new 是谁发起的"】：
//
//      [ThreadStatic] private static bool t_creatingInsideSingleton;
//
//  只有 Instance 内部在 new 之前把标记置 true，构造完成后复位。
//  于是：
//      · 外部任何 new（包括"在此之前谁都没访问过"的第一次）-> 标记为 false -> 抛异常
//      · 单例自己在创建 -> 标记为 true -> 正常构造
//
//  为什么是 [ThreadStatic] 而不是普通静态 bool：
//      普通静态字段是全线程共享的。若 A 线程正在创建（标记为 true），
//      B 线程此刻 new 一下就会被"误放行"。ThreadStatic 让这个标记成为
//      "本线程正在创建"的局部事实，语义精确，也不会互相干扰。
//
//  这条改动本身也是一个面试点：
//      "编译期禁止 new"需要反射，"不反射"就只能把保证从编译期挪到运行期，
//      而挪到运行期之后，就要把断言做得【没有漏洞、且错误信息指向调用点】。
//
//  早先一版实现用的是 s_created 标志（"已经创建过一次就不许再 new"），
//  它有漏洞：如果第一次访问就是外部 new，那次 new 会被放行，随后 Instance
//  仍然会创建自己的那一个 —— 于是又出现两个实例，而且是静默的。
//  现在的写法把第一次 new 也堵住了。
//
//  ---------------------------------------------------------------------------
//  四、改造后的保证（四条）
//  ---------------------------------------------------------------------------
//  ① 线程安全：无锁快路径 + 加锁慢路径 + 二次检查，s_instance 用 volatile
//     （双重检查锁在锁外读字段，不加 volatile 可能读到"非 null 但未构造完"的对象）。
//  ② 外部 new 一律失败，错误消息点名"请改用 Xxx.Instance"，在调用点暴露。
//  ③ 发布时机正确：OnInit() 执行完毕之后才把实例写进 s_instance，
//     别的线程不会拿到一个"初始化了一半"的单例。
//     代价是需要一个 s_pending 兼顾"OnInit 里重入访问 Instance"的情况。
//  ④ 场景重载安全：关闭 Domain Reload 后静态字段会跨 Play 会话存活，
//     统一由 SingletonRegistry 在 SubsystemRegistration 阶段重置。
//
//  ---------------------------------------------------------------------------
//  五、怎么用
//  ---------------------------------------------------------------------------
//      public sealed class EventCenter : Singleton<EventCenter>
//      {
//          protected override void OnInit()
//          {
//              // 单例第一次被创建时执行；不要写构造函数
//          }
//      }
//
//      EventCenter.Instance.Trigger(...);   // 或者沿用老写法 EventCenter.GetInstance()
//
//  约定：子类一律写 sealed。单例本来就不该被继承 —— 继承会多出一份"新的静态字段"，
//        因为 C# 的泛型静态字段是"每个封闭类型一份"（Singleton<A> 和 Singleton<B> 各存各的）。
// ============================================================================

using System;

namespace NBC.Framework
{
    /// <summary>
    /// 纯 C# 单例基类（CRTP 写法：T 是自己的子类）。
    /// 适用于管理器这类"不需要挂在 GameObject 上"的对象。
    /// 需要 MonoBehaviour 生命周期的请用 <see cref="SingletonMono{T}"/> 或 <see cref="SingletonAutoMono{T}"/>。
    /// </summary>
    /// <typeparam name="T">具体单例类型，必须继承本类并提供无参构造。</typeparam>
    public abstract class Singleton<T> where T : Singleton<T>, new()
    {
        /// <summary>
        /// 单例实例。null 表示尚未创建，或已被重置 / 释放。
        /// <para>
        /// 为什么必须 volatile：这是"双重检查锁"（DCLP）。快路径在【锁外】读取本字段，
        /// 若不加 volatile，编译器 / CPU 可以把"写字段"重排到"构造完成"之前，
        /// 于是另一个线程可能看到一个【非 null 但尚未构造完】的对象。
        /// 加锁只约束锁内外的可见性，管不到锁外那次无锁读，所以这里必须 volatile。
        /// </para>
        /// </summary>
        private static volatile T s_instance;

        /// <summary>
        /// 正在初始化、尚未发布的实例。
        /// 只为一件事存在：OnInit() 里若再次访问 Instance，应当拿到"正在初始化的自己"，
        /// 而不是触发第二次创建（也避免死等自己持有的锁）。
        /// </summary>
        private static volatile T s_pending;

        /// <summary>创建单例时用的锁。</summary>
        private static readonly object s_lock = new object();

        /// <summary>
        /// "本线程正在由 Singleton 内部创建实例"的标记。
        /// 构造函数用它区分"合法的内部创建"与"外部误写的 new"。
        /// [ThreadStatic]：不能有初始值，默认即 false，正好符合需要。
        /// </summary>
        [ThreadStatic]
        private static bool t_creatingInsideSingleton;

        /// <summary>
        /// 获取单例实例（不存在则创建）。
        /// </summary>
        public static T Instance
        {
            get
            {
                // 快路径：已发布就直接返回，不抢锁。绝大多数调用走这里，必须便宜。
                T published = s_instance;
                if (published != null)
                {
                    return published;
                }

                // 慢路径：只有"第一次"或"被重置之后"才会走到这里。
                lock (s_lock)
                {
                    // 二次检查：等锁期间别人可能已经发布好了。
                    if (s_instance != null)
                    {
                        return s_instance;
                    }

                    // 重入：本线程正在跑 OnInit，直接返回那个实例。
                    if (s_pending != null)
                    {
                        return s_pending;
                    }

                    // 创建：先置"内部创建中"，让构造函数放行；无论成败都要复位标记。
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

                    // 先挂到 pending 再跑 OnInit：这样 OnInit 里访问 Instance 不会递归创建。
                    s_pending = created;
                    created.OnInit();
                    s_pending = null;

                    // OnInit 完成之后才发布：别的线程不会拿到半成品。
                    s_instance = created;
                    return created;
                }
            }
        }

        /// <summary>
        /// 获取单例实例。保留这个名字是为了兼容唐老师框架的调用习惯。
        /// </summary>
        /// <returns>单例实例。</returns>
        public static T GetInstance()
        {
            return Instance;
        }

        /// <summary>是否已经存在实例（不触发创建）。调试面板与测试用。</summary>
        public static bool HasInstance
        {
            get { return s_instance != null; }
        }

        /// <summary>
        /// 受保护构造：子类可以声明自己的私有构造，
        /// 但只有 Singleton.Instance 发起的创建会被放行，其它 new 一律抛异常。
        /// </summary>
        protected Singleton()
        {
            if (!t_creatingInsideSingleton)
            {
                throw new InvalidOperationException(
                    "[Singleton] " + typeof(T).Name + " 是单例，不允许 new。请改用 " +
                    typeof(T).Name + ".Instance 或 " + typeof(T).Name + ".GetInstance()。");
            }

            // 登记重置动作：关闭 Domain Reload 时，进入 Play 模式前需要把静态状态清干净。
            SingletonRegistry.Register(ResetStaticState);
        }

        /// <summary>
        /// 单例第一次被创建时调用。子类的初始化逻辑写在这里，不要写构造函数。
        /// </summary>
        protected virtual void OnInit()
        {
        }

        /// <summary>
        /// 释放单例时调用。子类在这里注销监听、关闭连接。
        /// </summary>
        protected virtual void OnDispose()
        {
        }

        /// <summary>
        /// 主动释放单例（编辑器退出、测试收尾、确定不再需要时）。
        /// 释放后再次访问 <see cref="Instance"/> 会创建一个新的实例。
        /// </summary>
        public static void DisposeInstance()
        {
            lock (s_lock)
            {
                T disposing = s_instance;
                if (disposing == null)
                {
                    return;
                }

                // 先清字段再回调：避免 OnDispose 里访问 Instance 又触发一次创建。
                s_instance = null;
                disposing.OnDispose();
            }
        }

        /// <summary>
        /// 把静态状态清空。由 SingletonRegistry 在进入 Play 模式时统一调用，
        /// 也用于 EditMode 测试之间隔离状态。
        /// 注意：这里【故意不调用 OnDispose】。SubsystemRegistration 阶段跑用户代码风险大，
        /// 而此刻本来也没有"活着的业务状态"需要收尾。
        /// </summary>
        private static void ResetStaticState()
        {
            s_instance = null;
            s_pending = null;
        }
    }
}
