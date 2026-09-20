// ============================================================================
//  NBC.Framework · 池统计快照
//  来源：为 M1-A2（ObjectPool）新增；对应需求文档 §7.1.2 FW-M04、验收 V8
//
//  为什么统计要"快照"（readonly struct）而不是让外部直接读池的字段：
//    ① 池的字段是内部状态，直接读会让外部有机会改（原框架 `public Dictionary poolDic`
//       就是这个问题，见 Docs/06 §四 P-11）；
//    ② 快照是一份【一致的值】：调用方拿到的所有数字来自同一时刻，
//       不会出现"读了一半，池又变了"的错乱。
//
//  统计的意义不只是"给面板看"，它同时是**验收证据**：
//    V8 要求"池统计面板可看复用率"，而复用率必须由数字算出来，不能靠眼看。
// ============================================================================

namespace NBC.Framework
{
    /// <summary>
    /// 某一时刻的池状态快照。只读值类型：创建后不可改，外部拿到的是副本。
    /// </summary>
    public readonly struct PoolStats
    {
        /// <summary>当前闲置在池里的对象数。</summary>
        public readonly int InPool;

        /// <summary>当前已被取出、尚未归还的对象数。</summary>
        public readonly int InUse;

        /// <summary>累计通过工厂新建的对象数。</summary>
        public readonly int TotalCreated;

        /// <summary>累计取出次数（包括复用与新建）。</summary>
        public readonly int TotalSpawned;

        /// <summary>累计归还次数。</summary>
        public readonly int TotalDespawned;

        /// <summary>累计被丢弃 / 销毁的对象数（超出容量上限，或 Clear 时被清理）。</summary>
        public readonly int TotalDestroyed;

        /// <summary>在用的峰值对象数。</summary>
        public readonly int PeakInUse;

        /// <summary>池内闲置数的峰值。它直接反映"池开得够不够大"。</summary>
        public readonly int PeakInPool;

        /// <summary>构造快照。由池内部调用。</summary>
        public PoolStats(int inPool, int inUse, int totalCreated, int totalSpawned,
                         int totalDespawned, int totalDestroyed, int peakInUse, int peakInPool)
        {
            InPool = inPool;
            InUse = inUse;
            TotalCreated = totalCreated;
            TotalSpawned = totalSpawned;
            TotalDespawned = totalDespawned;
            TotalDestroyed = totalDestroyed;
            PeakInUse = peakInUse;
            PeakInPool = peakInPool;
        }

        /// <summary>
        /// 复用率：取出操作里有多大比例是"直接从池里拿"而不是"新建"。
        /// 0 表示一次都没复用上（池白建了）；接近 1 表示池在有效工作。
        /// </summary>
        public float ReuseRate
        {
            get
            {
                if (TotalSpawned <= 0)
                {
                    return 0f;
                }

                return 1f - (float)TotalCreated / TotalSpawned;
            }
        }

        /// <summary>一行摘要，给调试面板直接用。</summary>
        public override string ToString()
        {
            return "池内=" + InPool + " 在用=" + InUse + " 累计新建=" + TotalCreated +
                   " 取出=" + TotalSpawned + " 归还=" + TotalDespawned + " 销毁=" + TotalDestroyed +
                   " 峰值(在用/池内)=" + PeakInUse + "/" + PeakInPool +
                   " 复用率=" + (ReuseRate * 100f).ToString("F1") + "%";
        }
    }

    /// <summary>
    /// 能提供统计数据的池。调试面板只认这个接口，
    /// 于是"面板"与"池的具体实现（纯 C# 池 / GameObject 池）"互不认识 —— 这就是接口的价值。
    /// </summary>
    public interface IPoolStatsSource
    {
        /// <summary>池的名字，用于在面板上区分。同一个场景里应唯一。</summary>
        string PoolName { get; }

        /// <summary>取当前统计快照。</summary>
        PoolStats GetStats();
    }
}
