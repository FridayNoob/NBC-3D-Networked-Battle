// 这个文件里有：
//   PoolStats（结构体）：某一时刻池状态的一致快照，八个计数加复用率，构造后不可改。
//   IPoolStatsSource（接口）：能出统计快照的池的契约，调试面板顺着它枚举所有池。

namespace NBC.Framework
{
    // 主要功能：把池在某一刻的闲置数、在外数、四项累计与两个峰值打成一个只读值，调用方拿到的是副本而不是池的内部容器。
    // 主要数据成员：InPool / InUse（个）、TotalCreated / TotalSpawned / TotalDespawned / TotalDestroyed（累计，个）、PeakInUse / PeakInPool（峰值，个）、ReuseRate（比例，取值见属性注释）。
    // 主要外部方法：ReuseRate（属性）、ToString；构造由池的 GetStats 完成。
    // 谁在用它：ObjectPool<T> 与 GameObjectPool 的 GetStats 负责构造；EditMode 与 PlayMode 的池用例直接断言字段。
    // 注意：必须是拷贝出来的快照，不能把池的内部容器交出去 —— 否则外部能改池的状态，面板遍历途中还会读到不一致的数字。
    /// <summary>某一时刻的池状态快照：只读值类型，创建后不可改，外部拿到的是副本。</summary>
    public readonly struct PoolStats
    {
        /// <summary>当前闲置在池里的对象数，个，≥ 0。</summary>
        public readonly int InPool;

        /// <summary>当前已被取出、尚未归还的对象数，个，≥ 0。</summary>
        public readonly int InUse;

        /// <summary>累计通过工厂新建的对象数，个；含预热与失效后的补建，不等于当前持有数。</summary>
        public readonly int TotalCreated;

        /// <summary>累计取出次数，个；复用与新建各算一次。</summary>
        public readonly int TotalSpawned;

        /// <summary>累计成功归还次数，个；重复归还或归还别人的对象不计。</summary>
        public readonly int TotalDespawned;

        /// <summary>累计被丢弃 / 销毁的对象数，个；超容量、失效丢弃、Clear 与 Dispose 清池都算在内。</summary>
        public readonly int TotalDestroyed;

        /// <summary>在用的峰值对象数，个，只增不减。</summary>
        public readonly int PeakInUse;

        /// <summary>池内闲置数的峰值，个，只增不减；直接反映池开得够不够大。</summary>
        public readonly int PeakInPool;

        /// <summary>把池某一时刻的八个计数装进一份只读快照，只由池的 GetStats 调用。</summary>
        public PoolStats(int inPool, int inUse, int totalCreated, int totalSpawned,
                         int totalDespawned, int totalDestroyed, int peakInUse, int peakInPool)
        {
            // 功能：把池传进来的八个计数原样收进快照，之后谁也不能改。
            // 入参：inPool 闲置数；inUse 在外数；totalCreated 累计新建；totalSpawned 累计取出；totalDespawned 累计归还；totalDestroyed 累计销毁；peakInUse / peakInPool 峰值；单位均为个
            // 返回：无（构造函数）
            // 步骤：逐个字段赋值，不换算也不校验 —— 八个数字取自池的同一时刻，一致性由池在取快照那一刻保证
            // 上游：ObjectPool<T>.GetStats、GameObjectPool.GetStats
            // 下游：无
            // 注意：这里不校验也不钳制，传错值会原样变成快照；所以只该由池构造，别在池外 new。
            InPool = inPool;
            InUse = inUse;
            TotalCreated = totalCreated;
            TotalSpawned = totalSpawned;
            TotalDespawned = totalDespawned;
            TotalDestroyed = totalDestroyed;
            PeakInUse = peakInUse;
            PeakInPool = peakInPool;
        }

        /// <summary>复用率：取出操作里直接从池里拿的比例；0 表示一次没复用上，1 表示全是复用。</summary>
        public float ReuseRate
        {
            get
            {
                // 功能：用「1 - 累计新建 / 累计取出」算出复用比例。
                // 入参：无
                // 返回：复用比例，正常在 0..1；累计取出为 0 时返回 0；预热会抬高 TotalCreated 而不增加取出，所以可能算出负值
                // 步骤：
                //   1) 累计取出 ≤ 0 时直接返回 0，避免除零
                //   2) 否则用累计新建除以累计取出，再从 1 里减掉
                // 上游：调试面板、ToString、EditMode 的复用率用例
                // 下游：无
                // 注意：看的是累计值而不是当前闲置数 —— 取出后立刻归还也会把复用率推高，别把它当成「池里现在有多少可用」。
                // 注意：预热计入 TotalCreated 却不计入 TotalSpawned，所以先预热再取用的池，复用率会偏低甚至为负。
                if (TotalSpawned <= 0)
                {
                    return 0f;
                }

                return 1f - (float)TotalCreated / TotalSpawned;
            }
        }

        /// <summary>拼一行中文摘要，给调试面板直接显示。</summary>
        public override string ToString()
        {
            // 功能：把八个计数与复用率百分比拼成一行可读文本。
            // 入参：无
            // 返回：形如「池内=… 在用=… 累计新建=… 取出=… 归还=… 销毁=… 峰值(在用/池内)=…/… 复用率=…%」的一行，复用率保留 1 位小数
            // 步骤：
            //   1) 按固定顺序拼计数，便于面板横向对比
            //   2) 复用率乘 100 后按 F1 格式化，避免显示一长串小数
            // 上游：调试面板 / 日志输出
            // 下游：ReuseRate
            // 注意：每次调用都会新建字符串，别在每帧的热路径里反复拼。
            return "池内=" + InPool + " 在用=" + InUse + " 累计新建=" + TotalCreated +
                   " 取出=" + TotalSpawned + " 归还=" + TotalDespawned + " 销毁=" + TotalDestroyed +
                   " 峰值(在用/池内)=" + PeakInUse + "/" + PeakInPool +
                   " 复用率=" + (ReuseRate * 100f).ToString("F1") + "%";
        }
    }

    // 主要功能：把「能出统计快照」这件事抽成契约，调试面板只认它，不认纯 C# 池或 GameObject 池的具体类型。
    // 主要数据成员：无（纯接口；池名与计数都在实现方手里）。
    // 主要外部方法：PoolName（属性）、GetStats。
    // 谁在用它：ObjectPool<T> 与 GameObjectPool 实现它；PoolRegistry 按它登记池（Register / Unregister / CopyTo），EditMode 用例经 CopyTo 验证名单。
    // 注意：面板只拿到快照，拿不到池的控制权 —— 别在这里加「清空 / 回收」这类会改池状态的方法。
    /// <summary>能提供统计快照的池：调试面板只认这个接口，从而不认识池的具体实现。</summary>
    public interface IPoolStatsSource
    {
        // 功能：给池一个可显示的名字，面板上靠它区分同一个场景里的多个池。
        // 入参：无
        // 返回：池名，同一场景内应唯一；空名不会在这里被拦下
        // 步骤：接口没有实现步骤；实现方返回构造时存下的名字即可
        // 上游：调试面板、PoolRegistry 的名单枚举方
        // 下游：无
        // 注意：重名会让面板上的两行数字分不清是谁的，池名最好带上归属系统。
        /// <summary>池的名字，用于在面板上区分；同一场景内应唯一。</summary>
        string PoolName { get; }

        // 功能：取一份当前统计快照。
        // 入参：无
        // 返回：池在这一刻的 PoolStats 副本；调用方拿到后池再变也不影响这份值
        // 步骤：接口没有实现步骤；实现方按「闲置数、在外数、累计、峰值」的顺序填快照
        // 上游：调试面板的刷新、EditMode 与 PlayMode 的池用例
        // 下游：PoolStats 的构造函数
        // 注意：每调一次就是一次新的取值，不要在同一帧里为了几个字段反复调；要么取一份用到底，要么自己缓存。
        /// <summary>取当前统计快照；调用方拿到的是副本，之后池再变也不影响它。</summary>
        PoolStats GetStats();
    }
}
