// ============================================================================
//  NBC.Framework · MonoBehaviour 单例基类（"我把它摆在场景里，框架负责唯一性"）
//  替代：唐老师框架 Base/SingletonMono.cs（原 25 行）
//  缺陷编号：FW-02（GetInstance 可能返回 null）+ FW-M01
//  完整记录：Docs/06-框架改造记录.md §三 FW-02
//
//  ---------------------------------------------------------------------------
//  一、原版错在哪
//  ---------------------------------------------------------------------------
//      public class SingletonMono<T> : MonoBehaviour where T : MonoBehaviour
//      {
//          private static T instance;
//          public static T GetInstance() { return instance; }   // ← 只有这一行
//          protected virtual void Awake() { instance = this as T; }
//      }
//
//  静态字段【只由 Awake 赋值】，没有任何兜底。于是：
//      · 对象挂在未激活的 GameObject 上  -> Awake 不执行 -> 返回 null
//      · 对象被销毁后再取                -> 返回 null
//      · 第一次访问发生在 Awake 之前      -> 返回 null
//  而所有这些情况下，报错都发生在【离原因很远的地方】（调用方空引用）。
//
//  附带事实（审计实测）：这个类在 FrameTang 工程里【零子类、零引用】，
//  所以它是一颗"潜伏的雷"，而不是已经炸过的事故。同一份审计还发现
//  SingletonAutoMono 也是零子类 —— 也就是说原框架的这两块代码【从未被真正使用过】。
//  这也是为什么本项目要求：每个模块都必须配 EditMode 测试（M1 验收 V1），
//  否则"移植完成"和"死代码"在肉眼上无法区分（见 Docs/06 §四 P-15）。
//
//  ---------------------------------------------------------------------------
//  二、改造后的保证
//  ---------------------------------------------------------------------------
//  ① 找不到就主动找：用 FindFirstObjectByType<T>() 兜底，覆盖"对象在场景里但 Awake 还没跑"。
//     （API 核对方式：读本机 Unity 程序集的元数据 / 用 Server/_api-probe 编译验证，
//       不靠记忆 —— FindObjectOfType 在 2022.3 已标过时，新 API 是 2022.2+ 才有的。）
//  ② 销毁时自愈：OnDestroy 里【仅当 instance 就是自己】时才清空。
//     这一步很关键：原版若加上"销毁就置 null"，会出现
//     "旧对象销毁时把新对象注册的单例清掉"的经典 Bug。
//  ③ 重复实例会被【明确报错】，而不是安静地各自为政。
//  ④ 关闭 Domain Reload 时静态字段由 SingletonRegistry 统一重置。
//
//  ---------------------------------------------------------------------------
//  三、子类注意（重要，这是本项目从 P-08 学到的）
//  ---------------------------------------------------------------------------
//  Awake 在基类里是【非虚】的，子类【不要】写自己的 Awake 去"接管"初始化：
//  Unity 只会调用派生类里那一个 Awake，基类的就被静默跳过，单例注册随之失效。
//  需要初始化逻辑请覆写 OnAwake()。若真的误写了，Instance 会给出明确报错。
// ============================================================================

using System;
using UnityEngine;

namespace NBC.Framework
{
    /// <summary>
    /// MonoBehaviour 单例基类：对象由场景（或预制体）提供，框架只负责"唯一 + 可获取 + 自愈"。
    /// 自动创建型的单例请用 <see cref="SingletonAutoMono{T}"/>。
    /// </summary>
    /// <typeparam name="T">具体单例类型，必须是继承本类的 MonoBehaviour。</typeparam>
    public abstract class SingletonMono<T> : MonoBehaviour where T : SingletonMono<T>
    {
        /// <summary>单例实例。volatile 的理由同 <see cref="Singleton{T}"/>：双重检查锁的锁外读必须可见有序。</summary>
        private static volatile T s_instance;

        /// <summary>创建 / 查找单例时用的锁。</summary>
        private static readonly object s_lock = new object();

        /// <summary>应用是否正在退出。退出过程中禁止再创建新对象。</summary>
        private static bool s_quitting;

        /// <summary>是否已经就"Awake 没执行"报过一次错，避免每帧刷屏。</summary>
        private static bool s_warnedMissingAwake;

        /// <summary>
        /// 本实例是否已经走过基类的 Awake 注册流程。
        /// <para>
        /// 为什么是【实例字段】而不是静态标志：静态标志描述的是"这种类型曾经 Awake 过"，
        /// 一旦静态状态被重置（关闭 Domain Reload 时每次进 Play 都会重置），
        /// 场里存在的对象就会把标志连累成 false，于是误报。实例字段描述的是"这个对象自己"，
        /// 与任何重置无关。这是把"事实挂在正确的所有者身上"的一个具体例子。
        /// </para>
        /// </summary>
        private bool m_registered;

        /// <summary>
        /// 获取单例实例。实例不存在时会在场景中查找；仍然找不到则返回 null。
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
                    if (s_instance != null)
                    {
                        return s_instance;
                    }

                    // 兜底查找：覆盖"对象在场景里，但 Awake 还没执行"的情况
                    s_instance = FindFirstObjectByType<T>();

                    if (s_instance != null && !s_instance.m_registered && !s_warnedMissingAwake)
                    {
                        s_warnedMissingAwake = true;
                        Debug.LogError(
                            "[SingletonMono] 找到了 " + typeof(T).Name + "，但基类的 Awake 从未执行过。" +
                            "最常见的原因是子类自己写了 private void Awake()，把基类的 Awake 隐藏了。" +
                            "请把子类的初始化逻辑改写到 protected override void OnAwake() 里。");
                    }

                    return s_instance;
                }
            }
        }

        /// <summary>获取单例实例。兼容唐老师框架的调用习惯。</summary>
        /// <returns>单例实例；场景中不存在时返回 null。</returns>
        public static T GetInstance()
        {
            return Instance;
        }

        /// <summary>是否已经存在实例（不触发查找与创建）。</summary>
        public static bool HasInstance
        {
            get { return s_instance != null; }
        }

        /// <summary>
        /// 注册单例。【非虚】：子类不要覆写，否则基类逻辑会被静默跳过。子类初始化请覆写 OnAwake。
        /// </summary>
        private void Awake()
        {
            T self = this as T;

            if (self == null)
            {
                // 只有 T 写错（例如 class Foo : SingletonMono<Bar>）才会发生
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

        /// <summary>
        /// 清理引用。【非虚】，逻辑同 Awake。
        /// </summary>
        private void OnDestroy()
        {
            OnBeforeDestroy();

            // 只清"自己那一个"：旧对象销毁时不能把新对象注册的单例清掉
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
        /// 应用退出：清空实例并禁止后续创建，避免退出过程中 new 出对象。
        /// </summary>
        private static void OnApplicationQuitInternal()
        {
            s_quitting = true;
            s_instance = null;
        }

        /// <summary>
        /// 清空静态状态（进入 Play 模式时由 SingletonRegistry 调用，测试之间也可手动调用）。
        /// 注意：m_registered 是实例字段，不在清理范围内 —— 它描述的是"某个对象自己"，
        /// 静态重置不该改变这个事实。
        /// </summary>
        private static void ResetStaticState()
        {
            s_instance = null;
            s_quitting = false;
            s_warnedMissingAwake = false;
        }
    }
}
