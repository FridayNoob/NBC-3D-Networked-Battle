// ============================================================================
//  NBC.Framework · 池注册表（轻量，只服务于统计面板）
//  来源：为 M1-A2 新增；对应验收 V8（池统计面板可看复用率）
//
//  ---------------------------------------------------------------------------
//  为什么"不做中心管理器"，却又要一个注册表？
//  ---------------------------------------------------------------------------
//  原框架的 PoolMgr 是【中心管理器】：所有池塞进一个 `Dictionary<string, PoolData>`，
//  用资源路径字符串当 key。这带来三个问题（详见 Docs/06 §四）：
//    · P-11：`public Dictionary poolDic` 把内部容器直接暴露出去
//    · FW-06 同类：字符串 key 拼错不报错
//    · 池的生命周期被全局单例绑死，谁都能取到别人的池
//
//  本项目的做法是：**池由使用它的系统自己持有**（`ObjectPool<T>` 是普通对象，
//  不是单例）。这样类型安全、作用域清晰、也好测。
//
//  但"自己持有"会带来一个新问题：**调试面板怎么枚举所有池？**
//  答案是让池在创建时【自愿登记】到一个极薄的注册表里 —— 面板只需要一份名单，
//  不需要（也不应该）拥有池的控制权：
//
//      池 <-持有-> 使用它的系统        （所有权）
//      池 ->登记-> PoolRegistry       （只是一张名单，单向）
//
//  这就是"注册表"和"中心管理器"的区别：**注册表没有分发权，管理器有。**
//
//  ---------------------------------------------------------------------------
//  ⚠️ 一条纪律：池要 Dispose
//  ---------------------------------------------------------------------------
//  注册表持的是【强引用】。池用完不 `Dispose()`，名单里就会残留一条永远不释放的记录。
//  所以本框架的约定是：**谁创建池，谁负责 Dispose**（系统销毁 / 场景卸载时）。
//  A2 的测试里有一条专门盯这件事（`Dispose_UnregistersFromRegistry`）。
// ============================================================================

using System.Collections.Generic;

namespace NBC.Framework
{
    /// <summary>
    /// 池的登记名单。只增删名单，不持有池的控制权。
    /// </summary>
    public static class PoolRegistry
    {
        private static readonly List<IPoolStatsSource> s_pools = new List<IPoolStatsSource>();

        /// <summary>保护名单的锁。池一般在主线程建，但登记表本身没有理由不是线程安全的。</summary>
        private static readonly object s_lock = new object();

        /// <summary>当前登记在册的池数量。测试与面板用。</summary>
        public static int Count
        {
            get
            {
                lock (s_lock)
                {
                    return s_pools.Count;
                }
            }
        }

        /// <summary>
        /// 登记一个池。由池的构造函数调用，通常不需要手工调用。
        /// 重复登记同一个对象会被忽略。
        /// </summary>
        /// <param name="pool">要登记的池。</param>
        public static void Register(IPoolStatsSource pool)
        {
            if (pool == null)
            {
                return;
            }

            lock (s_lock)
            {
                if (!s_pools.Contains(pool))
                {
                    s_pools.Add(pool);
                }
            }
        }

        /// <summary>
        /// 注销一个池。由池的 Dispose 调用。
        /// </summary>
        /// <param name="pool">要注销的池。</param>
        public static void Unregister(IPoolStatsSource pool)
        {
            if (pool == null)
            {
                return;
            }

            lock (s_lock)
            {
                s_pools.Remove(pool);
            }
        }

        /// <summary>
        /// 把当前所有池拷进 buffer（先清空 buffer）。
        /// 调试面板每帧调用时请复用同一个 buffer，避免每帧产生垃圾。
        /// </summary>
        /// <param name="buffer">接收结果的列表。</param>
        public static void CopyTo(List<IPoolStatsSource> buffer)
        {
            if (buffer == null)
            {
                return;
            }

            buffer.Clear();
            lock (s_lock)
            {
                buffer.AddRange(s_pools);
            }
        }

        /// <summary>
        /// 清空名单。
        /// 用途：退出播放模式时的整体清理、以及测试用例之间的隔离。
        /// 注意它【不会】销毁池本身 —— 名单不是所有者。
        /// </summary>
        public static void Clear()
        {
            lock (s_lock)
            {
                s_pools.Clear();
            }
        }
    }
}
