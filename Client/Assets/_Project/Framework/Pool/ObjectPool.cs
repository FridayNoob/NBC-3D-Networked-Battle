// 这个文件里有：
//   ObjectPool<T>（泛型类）：通用引用类型对象池，注入工厂造对象、Stack 复用（LIFO）、按上限丢弃，自带统计与重复归还防护。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework
{
    // 主要功能：把对象复用与容量治理收进一个池：有闲置就复用、没有才用注入的工厂新建，归还超过上限就销毁；池只认工厂，不碰资源加载，因此不依赖资源层。
    // 主要数据成员：m_factory（创建工厂，不允许 null）、m_destroyer（丢弃动作）、m_isValid（有效性校验）、m_pool（闲置栈，个）、m_inUse（在外集合，个）、m_maxSize（闲置上限，个，小于等于 0 不限）、m_name（池名），以及六项累计与峰值计数。
    // 主要外部方法：Get / Release / Prewarm / Clear / GetStats / CopyInUseTo / Dispose，另有 PoolName / CountInPool / CountInUse / MaxSize 四个只读属性。
    // 谁在用它：GameObjectPool 内部包一个 ObjectPool<GameObject>；Framework 的 AudioManager 自建 ObjectPool<AudioVoice> 做播放器池；EditMode 的池用例直接 new 来验证。
    // 注意：选 Stack 是为了取出 O(1)，代价是 LIFO 而不是 FIFO —— 刚归还的最先被复用；真要 FIFO 应显式换 Queue，别用 List 在头部删除硬凑。
    /// <summary>通用对象池：复用任意引用类型对象，闲置按上限保留；统计与重复归还防护内建。</summary>
    /// <typeparam name="T">池化对象的类型，必须是引用类型。</typeparam>
    public sealed class ObjectPool<T> : IPoolStatsSource, IDisposable where T : class
    {
        // 创建新对象的工厂；池自带对象所必需，不允许为 null，否则构造时直接抛 ArgumentNullException。
        private readonly Func<T> m_factory;

        // 丢弃对象时的处理动作；null 表示只丢掉引用，托管对象交给 GC。
        private readonly Action<T> m_destroyer;

        // 校验池里的对象是否还能用；null 表示不校验。注意：对象可能被外部销毁（典型是场景卸载），栈里会留下已销毁但引用还在的伪 null，GameObject 池传 go != null 就能把它挑出来丢掉。
        private readonly Func<T, bool> m_isValid;

        // 闲置对象栈，个；用 Stack 而不是 List，Push / Pop 均摊 O(1)，代价是 LIFO 而不是 FIFO。
        private readonly Stack<T> m_pool;

        // 当前已取出、尚未归还的对象集合，个；重复归还防护靠它，归还一个不在册的对象当场报错而不是静默损坏池。
        private readonly HashSet<T> m_inUse = new HashSet<T>();

        // 闲置对象数量上限，个；小于等于 0 表示不限制，满时归还的对象直接销毁。
        private readonly int m_maxSize;

        // 池名，统计面板与日志靠它区分；构造时传空则退化为类型名，同一场景内应唯一。
        private readonly string m_name;

        // 是否已释放；true 后 Get / Release / Prewarm 抛 ObjectDisposedException，Dispose 本身可重复调用。
        private bool m_disposed;

        // 累计通过工厂新建的对象数，个；含预热与失效后补建，不等于当前持有数。
        private int m_totalCreated;
        // 累计取出次数，个；复用与新建各算一次。
        private int m_totalSpawned;
        // 累计成功归还次数，个；重复归还或归还别人的对象不计。
        private int m_totalDespawned;
        // 累计被丢弃或销毁的对象数，个；超上限、取出时失效、Clear 与 Dispose 清池都算在内。
        private int m_totalDestroyed;
        // 在用对象数的峰值，个；只增不减。
        private int m_peakInUse;
        // 池内闲置数的峰值，个；只增不减，用来判断池开得够不够大。
        private int m_peakInPool;

        /// <summary>构造一个对象池，并可选预热。</summary>
        /// <param name="name">池名，统计面板与日志靠它区分；空或 null 时用类型名。</param>
        /// <param name="factory">创建新对象的工厂，不允许为 null。</param>
        /// <param name="maxSize">闲置容量上限，个；小于等于 0 表示不限制。</param>
        /// <param name="initialSize">预热数量，个；0 表示不预热，超过 maxSize 会被截断。</param>
        /// <param name="destroyer">丢弃对象时的处理动作；托管对象可以传 null。</param>
        /// <param name="isValid">校验对象是否仍可用；null 表示不校验。</param>
        public ObjectPool(string name, Func<T> factory, int maxSize = 0, int initialSize = 0,
                          Action<T> destroyer = null, Func<T, bool> isValid = null)
        {
            // 功能：存下工厂、销毁器、校验器与上限，建一个空栈，登记到统计名单，最后按 initialSize 预热。
            // 入参：name 池名（空则用类型名）；factory 创建工厂（null 抛 ArgumentNullException）；maxSize 闲置上限，个（小于等于 0 不限）；initialSize 预热数量，个（0 不预热）；destroyer 丢弃动作（null 只丢引用）；isValid 有效性判定（null 不校验）
            // 返回：无（构造函数）
            // 步骤：
            //   1) 先挡 null 工厂：没有工厂池就造不出对象，宁可在构造时失败，也不要等到第一次 Get 才炸
            //   2) 存字段并建空栈，此时池里一个闲置对象都没有
            //   3) 登记到 PoolRegistry，只是让统计面板能枚举到，不转移所有权
            //   4) 最后预热：预热会调工厂，必须等工厂、池名、上限都就绪后再执行
            // 上游：GameObjectPool 的构造函数、AudioManager、EditMode 池用例
            // 下游：Prewarm、PoolRegistry.Register
            // 注意：登记发生在预热之前，若工厂在预热中返回 null 会抛异常，此时池已进名单却没人拿到引用，名单会留一条无法释放的强引用。
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

            PoolRegistry.Register(this);

            if (initialSize > 0)
            {
                Prewarm(initialSize);
            }
        }

        /// <summary>池名，统计面板与日志靠它区分。</summary>
        // 功能：返回构造时定下的池名。
        public string PoolName
        {
            get { return m_name; }
        }

        /// <summary>当前闲置对象数，个。</summary>
        // 功能：返回闲置栈里的对象数。
        public int CountInPool
        {
            get { return m_pool.Count; }
        }

        /// <summary>当前已取出、尚未归还的对象数，个。</summary>
        // 功能：返回在外对象集合的大小。
        public int CountInUse
        {
            get { return m_inUse.Count; }
        }

        /// <summary>闲置容量上限，个；小于等于 0 表示不限制。</summary>
        // 功能：返回构造时定下的闲置上限。
        public int MaxSize
        {
            get { return m_maxSize; }
        }

        /// <summary>取出一个对象：池里有有效闲置的就复用，否则用工厂新建。</summary>
        /// <returns>可用的对象实例，不会是 null；工厂返回 null 时抛 InvalidOperationException。</returns>
        public T Get()
        {
            // 功能：从闲置栈顶取一个仍然有效的对象；没有就新建，并把它记入在外集合与取出统计。
            // 入参：无
            // 返回：可用的对象实例，不会是 null
            // 步骤：
            //   1) 先确认池没释放，释放后再取一律抛异常
            //   2) 从栈顶往下弹，跳过 isValid 判为失效的对象 —— 场景卸载后残留的伪 null 就在这里被丢掉
            //   3) 一个都没剩下就用工厂新建；工厂返回 null 直接抛，避免把 null 交给调用方
            //   4) 先记入 m_inUse 与取出计数、再更新峰值，最后回调 OnSpawned，保证钩子里看到的统计已经算上自己
            // 上游：GameObjectPool.Spawn、AudioManager 的播放器池、EditMode 池用例
            // 下游：m_factory、IPoolable.OnSpawned、ThrowIfDisposed
            // 注意：失效对象只从栈里弹出、不交给 destroyer（它已被外部销毁），但计入 TotalDestroyed —— 自定义 isValid 返回 false 时，对象就是这样被静默丢掉的。
            ThrowIfDisposed();

            T item = null;

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

            IPoolable poolable = item as IPoolable;
            if (poolable != null)
            {
                poolable.OnSpawned();
            }

            return item;
        }

        /// <summary>归还一个对象；超出闲置上限时直接丢弃。</summary>
        /// <param name="item">要归还的对象，必须是本池取出且尚未归还的；null 会打错误日志后忽略。</param>
        public void Release(T item)
        {
            // 功能：把在外对象移回闲置栈；重复归还或归还别人的对象当场报错，池满则销毁。
            // 入参：item 要归还的对象（null 打错误日志后忽略）
            // 返回：无
            // 步骤：
            //   1) 先确认池没释放
            //   2) 挡 null：空引用判断不了归属，只记一条错误日志
            //   3) 从 m_inUse 移除；移除失败说明重复归还或不属于本池，当场报错并放弃，避免同一对象被两个使用者同时拿到
            //   4) 回调 OnDespawned 让对象自己清理，然后才判断上限
            //   5) 池满就交给 destroyer 销毁并计数，否则压回栈并更新闲置峰值
            // 上游：GameObjectPool.Despawn、AudioManager、EditMode 池用例
            // 下游：IPoolable.OnDespawned、DestroyItem
            // 注意：池满（闲置数达到 maxSize）时对象先走 OnDespawned 再被销毁，钩子里别假设它一定会被复用；maxSize 小于等于 0 表示不限制，池会一直增长。
            ThrowIfDisposed();

            if (item == null)
            {
                Debug.LogError("[ObjectPool:" + m_name + "] Release(null) 被忽略。");
                return;
            }

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

        /// <summary>预热：先建若干个闲置对象，避免首次取用时才新建造成卡顿；数量会被上限截断。</summary>
        /// <param name="count">期望的闲置对象数，个；小于等于 0 直接返回，超过 maxSize 会被截断。</param>
        public void Prewarm(int count)
        {
            // 功能：按 count（受 maxSize 截断）补齐闲置对象，只建不用，不产生取出记录。
            // 入参：count 期望的闲置对象数，个（小于等于 0 直接返回）
            // 返回：无
            // 步骤：
            //   1) 先挡非正数：没有可预热的数量
            //   2) 有上限时把目标截到 maxSize，避免建了又马上被丢弃
            //   3) 循环调工厂补到目标数量，工厂返回 null 直接抛
            //   4) 最后更新池内闲置峰值
            // 上游：构造函数（initialSize 大于 0）、AudioManager.Prewarm、EditMode 池用例
            // 下游：m_factory
            // 注意：预热出来的对象不进 m_inUse、不计取出、也不回调 OnSpawned —— 别指望在预热阶段做对象复位。
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

        /// <summary>清掉池中的闲置对象；在外对象不归池管，仍留在使用方手里。</summary>
        /// <param name="destroyItems">true 销毁闲置对象并计数；false 只丢引用，交给 GC。</param>
        public void Clear(bool destroyItems = true)
        {
            // 功能：把闲置栈清空，按 destroyItems 决定是否交给 destroyer 真的销毁。
            // 入参：destroyItems 是否销毁闲置对象（false 表示只清引用）
            // 返回：无
            // 步骤：
            //   1) 一直弹到栈空
            //   2) destroyItems 为 true 时逐个计数并交给 destroyer；为 false 时只让栈丢掉引用
            // 上游：Dispose、GameObjectPool.Clear、EditMode 池用例
            // 下游：DestroyItem
            // 注意：在外（已取出）的对象不在清理范围内 —— 所有权此刻在使用者手上，池无权销毁；它们之后归还时进入一个空池，是正常且安全的。要连它们一起收回，请先在上层调 GameObjectPool.DespawnAll。
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

        /// <summary>取一份统计快照，供面板显示与测试断言。</summary>
        /// <returns>池这一刻的八个计数与复用率；是副本，之后池再变也不影响它。</returns>
        public PoolStats GetStats()
        {
            // 功能：把当前闲置数、在外数与六项累计、峰值装成一份 PoolStats。
            // 入参：无
            // 返回：池这一刻的统计快照；调用方拿到的是副本
            // 步骤：直接构造 PoolStats，参数顺序与其构造函数一致
            // 上游：GameObjectPool.GetStats、调试面板、EditMode 池用例
            // 下游：PoolStats 的构造函数
            // 注意：池本身不加锁，快照按单线程使用设计；要跨线程读，得由调用方自己保证同步。
            return new PoolStats(m_pool.Count, m_inUse.Count, m_totalCreated, m_totalSpawned,
                                 m_totalDespawned, m_totalDestroyed, m_peakInUse, m_peakInPool);
        }

        /// <summary>把当前在外的对象拷进 buffer（先清空），供上层做全部收回。</summary>
        /// <param name="buffer">接收结果的列表，会被先清空；null 直接返回。</param>
        public void CopyInUseTo(List<T> buffer)
        {
            // 功能：拷一份在外对象名单，让上层能遍历着逐个收回。
            // 入参：buffer 接收结果的列表（先清空再填，null 直接返回）
            // 返回：无
            // 步骤：
            //   1) 挡 null，避免空引用
            //   2) 先清空 buffer：不清会把上一轮的残留一起带出去，同一个对象可能被收回两次
            //   3) 遍历 m_inUse 逐个 Add
            // 上游：GameObjectPool.DespawnAll、EditMode 池用例
            // 下游：无
            // 注意：拷出来的是当时的引用快照，之后池再变它不会跟着变；收回时对着这份名单逐个 Release 即可。
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

        /// <summary>释放池：清掉闲置对象并从统计名单注销；之后不可再用。</summary>
        public void Dispose()
        {
            // 功能：置释放标记、清空闲置对象、从 PoolRegistry 注销。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 已释放就直接返回，保证重复 Dispose 安全
            //   2) 先置标记再清池，之后 Get / Release / Prewarm 都会抛 ObjectDisposedException
            //   3) Clear(true) 把闲置对象真的销毁
            //   4) 从名单注销，避免面板枚举到已释放的池
            // 上游：GameObjectPool.Dispose、AudioManager、EditMode 池用例
            // 下游：Clear、PoolRegistry.Unregister
            // 注意：只清闲置对象且不回调 OnDespawned；m_inUse 里的引用故意不清 —— 那些对象的所有权还在外面，池无权销毁，此处只是不再参与统计。
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;
            Clear(true);
            PoolRegistry.Unregister(this);

        }

        // 功能：丢弃单个对象时交给 destroyer；没有 destroyer 就什么都不做，让引用自然被 GC。
        // 入参：item 要丢弃的对象，调用方已保证非 null
        // 返回：无
        // 步骤：destroyer 非空才调用；为空就直接返回，引用离开本方法后即可回收
        // 上游：Release（超上限）、Clear、Dispose
        // 下游：m_destroyer（例如 GameObjectPool.DestroyObject）
        // 注意：这里不递增 m_totalDestroyed，计数由各调用点自己加 —— 三个调用点都加了，改动时别漏。
        private void DestroyItem(T item)
        {
            if (m_destroyer != null)
            {
                m_destroyer(item);
            }
        }

        // 功能：已释放的池直接抛 ObjectDisposedException，而不是静默返回错误结果。
        // 入参：无
        // 返回：无；未释放时正常返回
        // 步骤：只判 m_disposed，为 true 就抛，异常消息带上池名便于定位
        // 上游：Get、Release、Prewarm
        // 下游：无
        // 注意：Clear / GetStats / CopyInUseTo 不调用本方法，已释放后仍能调。
        private void ThrowIfDisposed()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException("ObjectPool:" + m_name);
            }
        }
    }
}
