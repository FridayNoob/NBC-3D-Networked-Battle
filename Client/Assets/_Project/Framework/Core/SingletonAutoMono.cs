// ============================================================================
//  NBC.Framework · MonoBehaviour 单例基类（"我什么都不用摆，框架自己造一个"）
//  替代：唐老师框架 Base/SingletonAutoMono.cs（原 28 行）
//  缺陷编号：FW-03（缺并发 / 重入保护）+ FW-M01
//  完整记录：Docs/06-框架改造记录.md §三 FW-03
//
//  ---------------------------------------------------------------------------
//  一、先纠正一处需求文档的措辞（审计修正）
//  ---------------------------------------------------------------------------
//  需求文档 §7.1.1 原文写"未做【重复创建保护】"。逐行读过代码后确认：
//  这句话【不准确】—— 原版是有判空的：
//
//      public static T GetInstance()
//      {
//          if ( instance == null )          // ← 这就是"重复创建保护"
//          {
//              GameObject obj = new GameObject();
//              obj.name = typeof(T).ToString();
//              DontDestroyOnLoad(obj);
//              instance = obj.AddComponent<T>();
//          }
//          return instance;
//      }
//
//  真正缺的是【并发 / 重入保护】：没有 lock、没有二次检查，
//  "判空"与"赋值"之间存在竞态窗口（check-then-act 非原子）。
//
//  同时也要诚实说明：Unity 的 new GameObject() / AddComponent 只能在主线程调用，
//  多线程走到这里会直接抛异常，而不会安静地建出两个对象。所以
//  "多线程建多个实例"在本工程内【不可复现】。
//  这一条修的是【模式正确性】（FW-M01 明确要求线程安全），不是"已经复现的 Bug"。
//  把后果写重是有代价的：会让人按错误的现象去复现，找不到就以为缺陷不存在。
//
//  ---------------------------------------------------------------------------
//  二、改造后的保证（比原版多出来的四件事）
//  ---------------------------------------------------------------------------
//  ① 先找再建：场景里已经有（比如美术手动摆了一个）就【直接用】，不建第二个。
//     原版只管"没有就建"，不管"其实已经有了"。
//  ② 加锁 + 二次检查：把 check-then-act 变成原子的。
//  ③ 重入安全：Awake 里再调一次 GetInstance 也不会建出第二个。
//  ④ 重复实例明确报错（名字冲突时给出提示），退出时不再创建新对象。
//
//  ---------------------------------------------------------------------------
//  三、代价（要能讲出来）
//  ---------------------------------------------------------------------------
//  FindFirstObjectByType<T>() 会遍历场景中的对象，是有成本的。
//  但它只在【s_instance 为 null】时才执行，也就是"本次运行第一次访问"或"对象被销毁之后"，
//  不在每帧路径上。这是可接受的代价，代价换来的是"不会静默返回 null"。
// ============================================================================

using System;
using UnityEngine;

namespace NBC.Framework
{
    /// <summary>
    /// 自动创建型 MonoBehaviour 单例基类：不需要在场景里摆对象，第一次访问时自动建。
    /// 适用于"全局唯一、与具体场景无关"的宿主（例如协程宿主、音频宿主）。
    /// </summary>
    /// <typeparam name="T">具体单例类型，必须是继承本类的 MonoBehaviour。</typeparam>
    public abstract class SingletonAutoMono<T> : MonoBehaviour where T : SingletonAutoMono<T>
    {
        /// <summary>单例实例。volatile 的理由同 <see cref="Singleton{T}"/>：双重检查锁的锁外读必须可见有序。</summary>
        private static volatile T s_instance;

        /// <summary>创建 / 查找单例时用的锁。</summary>
        private static readonly object s_lock = new object();

        /// <summary>应用是否正在退出。退出过程中禁止再创建新对象。</summary>
        private static bool s_quitting;

        /// <summary>是否已经就"场景里存在多个同名单例"报过警，避免刷屏。</summary>
        private static bool s_warnedDuplicate;

        /// <summary>
        /// 获取单例实例：不存在则自动创建（带 DontDestroyOnLoad）。
        /// </summary>
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

                    // 第一步：场景里已经有了就用它（这是原版没有的一步）
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
                    // 所以 Awake 里的"重复检测"不会误报。
                    s_instance = go.AddComponent<T>();
                    return s_instance;
                }
            }
        }

        /// <summary>获取单例实例。兼容唐老师框架的调用习惯。</summary>
        /// <returns>单例实例（不存在则创建，因此不会返回 null，除非正在退出）。</returns>
        public static T GetInstance()
        {
            return Instance;
        }

        /// <summary>是否已经存在实例（不触发创建）。</summary>
        public static bool HasInstance
        {
            get { return s_instance != null; }
        }

        /// <summary>
        /// 注册单例。【非虚】：子类不要覆写，初始化请覆写 OnAwake。
        /// </summary>
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

        /// <summary>
        /// 清理引用。【非虚】，逻辑同 Awake。
        /// </summary>
        private void OnDestroy()
        {
            OnBeforeDestroy();

            if (ReferenceEquals(s_instance, this as T))
            {
                s_instance = null;
            }
        }

        /// <summary>
        /// 单例注册完成后的初始化钩子。子类的初始化写在这里，不要写 Awake。
        /// </summary>
        protected virtual void OnAwake()
        {
        }

        /// <summary>
        /// 对象销毁前的钩子。子类在这里注销事件监听、停止协程。
        /// </summary>
        protected virtual void OnBeforeDestroy()
        {
        }

        /// <summary>
        /// 应用退出：清空实例并禁止后续创建。
        /// </summary>
        private static void OnApplicationQuitInternal()
        {
            s_quitting = true;
            s_instance = null;
        }

        /// <summary>
        /// 清空静态状态（进入 Play 模式时由 SingletonRegistry 调用，测试之间也可手动调用）。
        /// </summary>
        private static void ResetStaticState()
        {
            s_instance = null;
            s_quitting = false;
            s_warnedDuplicate = false;
        }
    }
}
