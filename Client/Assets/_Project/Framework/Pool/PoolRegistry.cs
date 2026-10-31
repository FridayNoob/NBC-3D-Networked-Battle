// 这个文件里有：
//   PoolRegistry（静态类）：池的自愿登记名单，只增删与拷贝名单，不持有池的控制权

using System.Collections.Generic;

namespace NBC.Framework
{
    // 主要功能：维护在册池的名单，让统计面板能枚举全部池；只有名单、没有分发权，池仍由使用它的系统自己持有。
    // 主要数据成员：s_pools（在册池的强引用列表，元素个数即 Count）、s_lock（保护名单的锁）。
    // 主要外部方法：Register / Unregister / CopyTo / Clear / Count。
    // 谁在用它：ObjectPool<T> 与 GameObjectPool 在构造与 Dispose 时登记、注销；测试与统计面板读名单。
    /// <summary>池的登记名单。只增删名单，不持有池的控制权。</summary>
    public static class PoolRegistry
    {
        // 在册池的强引用名单；元素个数即 Count（登记条数，不是池内对象数），取值 0 到已登记数
        private static readonly List<IPoolStatsSource> s_pools = new List<IPoolStatsSource>();

        // 保护名单的锁；池主要在主线程建，但名单本身按线程安全做
        private static readonly object s_lock = new object();

        /// <summary>当前登记在册的池数量，供测试与统计面板读取。</summary>
        public static int Count
        {
            get
            {
                // 功能：加锁读一次名单长度，供断言与面板显示。
                // 入参：无
                // 返回：在册池数量，0 表示当前没有任何池登记。
                // 步骤：加锁读 Count，避免与登记、注销并发时读到中间态。
                // 上游：测试（登记与注销的断言）、统计面板
                // 下游：无
                // 注意：口径是登记条数、不是池里对象数；重复登记被忽略，同一个池只算一次。
                lock (s_lock)
                {
                    return s_pools.Count;
                }
            }
        }

        /// <summary>把一个池登记进名单；重复登记同一对象会被忽略。</summary>
        public static void Register(IPoolStatsSource pool)
        {
            // 功能：把池加进名单，让统计面板能枚举到它；由池的构造函数调用，通常不用手工调。
            // 入参：pool 要登记的池，null 直接忽略
            // 返回：无
            // 步骤：
            //   1) 先挡 null：名单里不放空引用，后面 CopyTo 的消费方就不用再判空
            //   2) 加锁后判重再 Add：重复登记会让面板重复显示、Count 也多算，所以重复的忽略
            // 上游：ObjectPool<T>、GameObjectPool 的构造函数
            // 下游：s_pools 名单
            // 注意：名单持的是强引用，池用完不 Dispose 就会一直留在名单里（等同泄漏）；谁创建池谁负责 Dispose。
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

        /// <summary>把池从名单里移除；由池的 Dispose 调用，重复注销不报错。</summary>
        public static void Unregister(IPoolStatsSource pool)
        {
            // 功能：把池从名单移除，避免池已销毁而面板还能枚举到它。
            // 入参：pool 要注销的池，null 直接忽略
            // 返回：无
            // 步骤：
            //   1) 先挡 null，与 Register 对称
            //   2) 加锁后 Remove：List.Remove 找不到时静默返回，所以重复注销不会抛
            // 上游：ObjectPool<T>、GameObjectPool 的 Dispose
            // 下游：s_pools 名单
            // 注意：注销只删名单，不回收池里的对象；漏调的话名单残留的是已销毁池的强引用，池再也回收不掉。
            if (pool == null)
            {
                return;
            }

            lock (s_lock)
            {
                s_pools.Remove(pool);
            }
        }

        /// <summary>把当前在册的池拷进 buffer；拷前会清空 buffer，面板请复用同一个 buffer。</summary>
        public static void CopyTo(List<IPoolStatsSource> buffer)
        {
            // 功能：给面板与测试一份当前名单的拷贝，调用方拿到的是副本，改它不影响登记表。
            // 入参：buffer 接收结果的列表，会被先清空再填充；null 直接返回
            // 返回：无
            // 步骤：
            //   1) 先挡 null，避免空引用
            //   2) 先清空 buffer 再填：不清会把上一帧的残留一起带出去，名单越积越长
            //   3) 在锁内 AddRange，保证拷到的是同一时刻的名单
            // 上游：统计面板（每帧复用同一个 buffer）、测试
            // 下游：无
            // 注意：面板每帧新建 List 会持续产生垃圾，请复用同一个 buffer。
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

        /// <summary>清空名单，不销毁池本身；用于退出播放模式的整体清理与测试用例之间的隔离。</summary>
        public static void Clear()
        {
            // 功能：一次性清掉整张名单，用于退出播放模式或测试之间的隔离。
            // 入参：无
            // 返回：无
            // 步骤：加锁后直接清空列表；只清名单、不调池的 Dispose，因为注册表不是池的所有者
            // 上游：退出播放模式时的整体清理、测试的每个用例前后
            // 下游：无
            // 注意：Clear 之后池仍照常在跑，清理方要自己负责 Dispose，否则这些池会变成没人持有的孤儿。
            lock (s_lock)
            {
                s_pools.Clear();
            }
        }
    }
}
