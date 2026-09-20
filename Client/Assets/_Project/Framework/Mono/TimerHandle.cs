// ============================================================================
//  NBC.Framework · 定时器 / 延迟调用的句柄
//  来源：为 M1-A4 新增；对应需求文档 §7.1.2 FW-M03（定时器、延迟调用）
//
//  ---------------------------------------------------------------------------
//  为什么不用 int 当句柄，而要包一个 struct
//  ---------------------------------------------------------------------------
//  最省事的做法是 `int AddTimer(...)` / `Cancel(int id)`。问题在于：
//    · `Cancel(3)` 里的 3 是你自己猜的，还是上次 AddTimer 的返回值？编译器不知道；
//    · 一个 int 很容易和别的 int 混用（数组下标、数量、id 全是 int）。
//
//  包成 `TimerHandle` 之后，`Cancel(handle)` 只能传句柄，传错类型**编译不过**。
//  这跟 A3 用 `EventId` 代替 string 是同一个思路：**让类型系统替你拦住一类错误**。
//
//  ---------------------------------------------------------------------------
//  语义约定（与池的 handle 不同，这里没有"归还"概念）
//  ---------------------------------------------------------------------------
//    · `default(TimerHandle)`（也就是 `TimerHandle.Invalid`）表示"没有定时器"。
//    · 句柄是**一次性**的：定时器被取消或触发完了，句柄就失效了（`IsValid` 变 false）。
//    · id 单调递增、**不复用** —— 这样"拿着旧句柄去取消新定时器"永远不会误伤。
// ============================================================================

using System;

namespace NBC.Framework
{
    /// <summary>
    /// 定时器 / 延迟调用的句柄。由 <see cref="MonoManager"/> 发放，用于取消。
    /// </summary>
    public readonly struct TimerHandle : IEquatable<TimerHandle>
    {
        /// <summary>空句柄。等价于 <c>default(TimerHandle)</c>。</summary>
        public static readonly TimerHandle Invalid = default(TimerHandle);

        private readonly int m_id;

        /// <summary>构造句柄。仅由 MonoManager 内部调用。</summary>
        internal TimerHandle(int id)
        {
            m_id = id;
        }

        /// <summary>句柄编号。0 表示无效。</summary>
        public int Id
        {
            get { return m_id; }
        }

        /// <summary>是否是有效句柄。</summary>
        public bool IsValid
        {
            get { return m_id != 0; }
        }

        /// <summary>按编号比较。</summary>
        public bool Equals(TimerHandle other)
        {
            return m_id == other.m_id;
        }

        /// <summary>按编号比较。</summary>
        public override bool Equals(object obj)
        {
            return obj is TimerHandle && Equals((TimerHandle)obj);
        }

        /// <summary>编号的哈希值。</summary>
        public override int GetHashCode()
        {
            return m_id;
        }

        /// <summary>调试用文本。</summary>
        public override string ToString()
        {
            return m_id == 0 ? "TimerHandle(Invalid)" : "TimerHandle(" + m_id + ")";
        }

        /// <summary>相等比较。</summary>
        public static bool operator ==(TimerHandle left, TimerHandle right)
        {
            return left.Equals(right);
        }

        /// <summary>不等比较。</summary>
        public static bool operator !=(TimerHandle left, TimerHandle right)
        {
            return !left.Equals(right);
        }
    }
}
