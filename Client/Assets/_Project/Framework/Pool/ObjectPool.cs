// ============================================================================
//  NBC.Framework · 通用对象池（纯 C#，可装任何引用类型）
//  替代：唐老师框架 Pool/PoolMgr.cs 的 PoolData（原 56 行）
//  缺陷编号：FW-04（RemoveAt(0) 是 O(n)）、FW-05（无上限、无回收）
//            + 审计新增 P-11（public 容器暴露内部状态）
//  完整记录：Docs/06-框架改造记录.md §三 FW-04 / FW-05、§四 P-11
//
//  ---------------------------------------------------------------------------
//  一、原版错在哪
//  ---------------------------------------------------------------------------
//      public class PoolData
//      {
//          public GameObject fatherObj;
//          public List<GameObject> poolList;          // <- public 容器
//          public GameObject GetObj()
//          {
//              GameObject obj = poolList[0];
//              poolList.RemoveAt(0);                  // <- O(n)
//              ...
//          }
//          public void PushObj(GameObject obj) { poolList.Add(obj); }   // <- 无上限
//      }
//
//  ① **FW-04**：用 List 实现"先进先出"的队列语义，而 List 的头删要把后面所有元素前移。
//     池里有 N 个对象时，取出 N 次的总代价是 O(N²)。子弹、飘字这种"每帧取几十个"的池
//     会直接吃满 CPU。这是**数据结构选错**，不是代码写得丑。
//  ② **FW-05**：只实现了"复用"，没实现"治理"。池的收益是省下 Instantiate，
//     代价是对象常驻内存；没有上限时收益与代价就失衡了。
//  ③ **P-11**：`poolList` / `fatherObj` 都是 public，外部可以绕过 Get/Push 直接改，
//     父子关系维护立刻失效。
//
//  ---------------------------------------------------------------------------
//  二、改造要点
//  ---------------------------------------------------------------------------
//  ① 用 `Stack<T>` 代替 List：Push/Pop 均摊 O(1)。
//     注意这带来一个**语义变化**：LIFO（后进先出）而不是 FIFO。
//     对"刚回收的对象优先复用"这件事，LIFO 反而对 CPU cache 更友好；
//     但如果哪天真的需要 FIFO，请显式换 `Queue<T>`，不要用 List 硬凑。
//  ② 容量上限 `maxSize`：归还时若池已满，直接丢弃（交给 destroyer 处理）。
//  ③ 统计：新建 / 取出 / 归还 / 销毁 / 峰值 / **复用率**，直接服务验收 V8。
//  ④ 内部容器一律 private，对外只给 `PoolStats` 快照。
//  ⑤ **重复归还防护**：用 `HashSet<T>` 记录"当前在外的对象"。
//     重复归还会让同一个对象进池两次，之后被两个使用者同时拿到 —— 这类 Bug 极难查，
//     宁可在这里多一次哈希查找，也要让它**当场报错**而不是静默损坏。
//
//  ---------------------------------------------------------------------------
//  三、为什么用"注入工厂"而不是"按资源路径加载"
//  ---------------------------------------------------------------------------
//  池的职责是"复用对象"，不是"加载资源"。原版把 `Resources` 路径烧进了池里
//  （`PoolMgr.cs:85`），导致池与资源层硬耦合。改造后池只认一个 `Func<T>`：
//
//      var pool = new ObjectPool<Bullet>("Bullet", () => CreateBullet(), maxSize: 64);
//
//  好处：① 池不依赖资源层，M1-A2 现在就能完成（`AssetManager` 还没写）；
//        ② 测试可以注入一个计数工厂，把"到底新建了几个"变成可断言的数字。
//  这与 A2 的三条接口决策一致（注入工厂 / 系统自己持有池 / 同步 Get-Release）。
//
//  ---------------------------------------------------------------------------
//  四、依赖说明
//  ---------------------------------------------------------------------------
//  本类使用了 `UnityEngine.Debug` 报错（重复归还等）。如果要让**服务端**也能用同一份池
//  （帧同步的输入队列、状态快照都可能需要），应把本类搬进 `NBC.Model` / `NBC.Shared`
//  并把日志改成注入式 —— 那是一个明确的未来动作，不是现在的需求。
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework
{
    /// <summary>
    /// 通用对象池。可装任何引用类型：GameObject、纯数据类、第三方对象。
    /// </summary>
    /// <typeparam name="T">池化对象类型，必须是引用类型。</typeparam>
    public sealed class ObjectPool<T> : IPoolStatsSource, IDisposable where T : class
    {
        /// <summary>创建新对象的工厂。池自带对象所必需，不允许为 null。</summary>
        private readonly Func<T> m_factory;

        /// <summary>丢弃对象时的处理方式。为 null 表示"只是丢掉引用"（托管对象交给 GC）。</summary>
        private readonly Action<T> m_destroyer;

        /// <summary>
        /// 校验池中取出的对象是否仍然可用。为 null 表示"不做校验"。
        /// <para>
        /// 为什么需要：池里的对象可能被**外部**销毁。最典型的场景是场景卸载 ——
        /// 池的挂在场景里的父节点会被一起销毁，池的 Stack 里就留下一堆
        /// "已销毁但引用还在"的对象。取出来直接用就会炸在离原因很远的地方。
        /// Unity 的对象有"伪 null"（已销毁的对象 == null 为 true），
        /// 所以 GameObject 池传入 `go =&gt; go != null` 就能识别并丢弃这类残留。
        /// </para>
        /// </summary>
        private readonly Func<T, bool> m_isValid;

        /// <summary>闲置对象栈。选 Stack 而不是 List 就是为了 O(1)（FW-04）。</summary>
        private readonly Stack<T> m_pool;

        /// <summary>当前在外（已取出未归还）的对象集合，用于重复归还防护。</summary>
        private readonly HashSet<T> m_inUse = new HashSet<T>();

        /// <summary>容量上限。小于等于 0 表示不限制。</summary>
        private readonly int m_maxSize;

        /// <summary>池名，用于统计面板区分。</summary>
        private readonly string m_name;

        private bool m_disposed;

        private int m_totalCreated;
        private int m_totalSpawned;
        private int m_totalDespawned;
        private int m_totalDestroyed;
        private int m_peakInUse;
        private int m_peakInPool;

        /// <summary>
        /// 构造一个对象池。
        /// </summary>
        /// <param name="name">池名，用于统计面板区分；同一个场景内应唯一。</param>
        /// <param name="factory">创建新对象的工厂，不允许为 null。</param>
        /// <param name="maxSize">闲置容量上限；小于等于 0 表示不限制。</param>
        /// <param name="initialSize">预热数量；0 表示不预热。</param>
        /// <param name="destroyer">丢弃对象时的处理动作；托管对象可以传 null。</param>
        /// <param name="isValid">校验对象是否仍然可用；null 表示不校验。</param>
        public ObjectPool(string name, Func<T> factory, int maxSize = 0, int initialSize = 0,
                          Action<T> destroyer = null, Func<T, bool> isValid = null)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            m_name = string.IsNullOrEmpty(name) ? typeof(T).Name : name;
            m_factory = factory;
            m_destroyer = destroyer;
            m_isValid = isValid;
            m_maxSize = maxSize;
            m_pool = new Stack<T>();

            // 自愿登记到名单，供统计面板枚举。注意这只是"名单"，不是所有权转移。
            PoolRegistry.Register(this);

            if (initialSize > 0)
            {
                Prewarm(initialSize);
            }
        }

        /// <summary>池名。</summary>
        public string PoolName
        {
            get { return m_name; }
        }

        /// <summary>当前闲置对象数。</summary>
        public int CountInPool
        {
            get { return m_pool.Count; }
        }

        /// <summary>当前在外对象数。</summary>
        public int CountInUse
        {
            get { return m_inUse.Count; }
        }

        /// <summary>容量上限（小于等于 0 表示不限制）。</summary>
        public int MaxSize
        {
            get { return m_maxSize; }
        }

        /// <summary>
        /// 取出一个对象。池里有就复用，没有就通过工厂新建。
        /// </summary>
        /// <returns>可用的对象实例，不会是 null。</returns>
        public T Get()
        {
            ThrowIfDisposed();

            T item = null;

            // 从栈顶往下找第一个"仍然有效"的对象。
            // 失效的对象（例如随场景一起被销毁的 GameObject）直接丢掉，不算复用。
            while (m_pool.Count > 0)
            {
                T candidate = m_pool.Pop();
                if (m_isValid == null || m_isValid(candidate))
                {
                    item = candidate;
                    break;
                }

                m_totalDestroyed++;
            }

            if (item == null)
            {
                item = m_factory();
                if (item == null)
                {
                    throw new InvalidOperationException(
                        "[ObjectPool:" + m_name + "] 工厂返回了 null，池无法工作。");
                }

                m_totalCreated++;
            }

            m_inUse.Add(item);
            m_totalSpawned++;
            if (m_inUse.Count > m_peakInUse)
            {
                m_peakInUse = m_inUse.Count;
            }

            // 钩子：让对象自己复位（可选接口，没实现就跳过）
            IPoolable poolable = item as IPoolable;
            if (poolable != null)
            {
                poolable.OnSpawned();
            }

            return item;
        }

        /// <summary>
        /// 归还一个对象。超出容量上限时会被丢弃（交给 destroyer）。
        /// </summary>
        /// <param name="item">要归还的对象，必须是本池取出且尚未归还的。</param>
        public void Release(T item)
        {
            ThrowIfDisposed();

            if (item == null)
            {
                Debug.LogError("[ObjectPool:" + m_name + "] Release(null) 被忽略。");
                return;
            }

            // 重复归还 / 归还别人的对象：当场报错，不静默损坏池
            if (!m_inUse.Remove(item))
            {
                Debug.LogError("[ObjectPool:" + m_name +
                               "] 归还了一个不在使用中的对象（重复归还，或该对象不属于本池）。已忽略。");
                return;
            }

            m_totalDespawned++;

            IPoolable poolable = item as IPoolable;
            if (poolable != null)
            {
                poolable.OnDespawned();
            }

            // FW-05：容量上限。满了就丢弃，而不是无限增长。
            if (m_maxSize > 0 && m_pool.Count >= m_maxSize)
            {
                m_totalDestroyed++;
                DestroyItem(item);
                return;
            }

            m_pool.Push(item);
            if (m_pool.Count > m_peakInPool)
            {
                m_peakInPool = m_pool.Count;
            }
        }

        /// <summary>
        /// 预热：先创建若干个闲置对象，避免运行中第一次使用时才新建（造成卡顿）。
        /// 预热数量会被容量上限截断。
        /// </summary>
        /// <param name="count">期望的闲置对象数量。</param>
        public void Prewarm(int count)
        {
            ThrowIfDisposed();

            if (count <= 0)
            {
                return;
            }

            int target = count;
            if (m_maxSize > 0 && target > m_maxSize)
            {
                target = m_maxSize;
            }

            while (m_pool.Count < target)
            {
                T item = m_factory();
                if (item == null)
                {
                    throw new InvalidOperationException(
                        "[ObjectPool:" + m_name + "] 工厂返回了 null，池无法工作。");
                }

                m_totalCreated++;
                m_pool.Push(item);
            }

            if (m_pool.Count > m_peakInPool)
            {
                m_peakInPool = m_pool.Count;
            }
        }

        /// <summary>
        /// 清理池中【闲置】的对象。
        /// <para>
        /// 原版的 `Clear()` 只做了 `poolDic.Clear()`，池里的 GameObject 一个都没销毁，
        /// 全部变成场景孤儿（审计 P-04）。这里会真的把它们交给 destroyer。
        /// </para>
        /// <para>
        /// 注意：**在外（已取出）的对象不在清理范围内** —— 它们的所有权此刻在使用者手上，
        /// 池无权销毁。它们之后归还时会进入一个已经空掉的池，这是正常且安全的。
        /// 需要把在外的也一并收回时，请在更上一层处理（例如 <see cref="GameObjectPool.DespawnAll"/>）。
        /// </para>
        /// </summary>
        /// <param name="destroyItems">是否连同对象一起销毁；false 表示只丢弃引用。</param>
        public void Clear(bool destroyItems = true)
        {
            while (m_pool.Count > 0)
            {
                T item = m_pool.Pop();
                if (destroyItems)
                {
                    m_totalDestroyed++;
                    DestroyItem(item);
                }
            }
        }

        /// <summary>取统计快照。</summary>
        public PoolStats GetStats()
        {
            return new PoolStats(m_pool.Count, m_inUse.Count, m_totalCreated, m_totalSpawned,
                                 m_totalDespawned, m_totalDestroyed, m_peakInUse, m_peakInPool);
        }

        /// <summary>
        /// 把当前在外的对象拷进 buffer（先清空 buffer）。
        /// 供上层做"全部收回"（例如切场景）时使用。
        /// </summary>
        /// <param name="buffer">接收结果的列表。</param>
        public void CopyInUseTo(List<T> buffer)
        {
            if (buffer == null)
            {
                return;
            }

            buffer.Clear();
            foreach (T item in m_inUse)
            {
                buffer.Add(item);
            }
        }

        /// <summary>
        /// 释放池：清掉闲置对象，并从统计名单里注销。
        /// 释放后不可再使用，否则抛 <see cref="ObjectDisposedException"/>。
        /// </summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;
            Clear(true);
            PoolRegistry.Unregister(this);

            // 注意：不清理 m_inUse 里的引用。那些对象的所有权还在外面，
            // 池无权处置；此处只是不再参与统计。
        }

        /// <summary>销毁单个对象：交给 destroyer，没有 destroyer 就只丢引用。</summary>
        private void DestroyItem(T item)
        {
            if (m_destroyer != null)
            {
                m_destroyer(item);
            }
        }

        /// <summary>已释放的池不允许再操作：与其静默返回错误结果，不如明确失败。</summary>
        private void ThrowIfDisposed()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException("ObjectPool:" + m_name);
            }
        }
    }
}
