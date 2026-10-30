// 这个文件里有：
//   TimerHandle（结构体）：定时器 / 延迟调用的句柄，由 MonoManager 发放，交给 Cancel 取消

using System;

namespace NBC.Framework
{
    // 主要功能：把定时器的编号包成独立类型 —— 取消定时器时只能传句柄，误传成别的 int 编译不过。
    // 主要数据成员：m_id（定时器编号，int，1 起单调递增、不复用；0 表示无效句柄）。
    // 主要外部方法：Invalid（空句柄常量）、Id / IsValid（只读属性）、== / != / Equals / GetHashCode / ToString（按编号比较与显示）。
    // 谁在用它：MonoManager 的 CallLater / CallEvery 返回它、Cancel 收它；PlayMode 的 MonoManagerTests 直接构造句柄与传 Invalid。
    // 注意：IsValid 只表示「编号不是 0」，不代表定时器还活着 —— 定时器触发完或被取消后编号不变，句柄仍是 IsValid == true，
    //   此时再 Cancel 只是安全的空操作；要判断定时器还在不在，得问 MonoManager。
    // 注意：编号单调递增、不复用（MonoManager 的 m_nextTimerId 从 1 起），所以拿旧句柄去取消永远不会误伤后建的定时器；
    //   但编号不是数组下标，也不要拿它去数定时器个数。
    /// <summary>定时器 / 延迟调用的句柄，由 MonoManager 的 CallLater / CallEvery 发放，交给 Cancel 取消。</summary>
    public readonly struct TimerHandle : IEquatable<TimerHandle>
    {
        /// <summary>空句柄，含义是「没有定时器」；等价于 default(TimerHandle)，编号为 0。</summary>
        // 注意：default(TimerHandle) 与它等值；把它交给 Cancel 是安全的空操作，不会抛异常。
        public static readonly TimerHandle Invalid = default(TimerHandle);

        // 定时器编号；int，1 起单调递增、永不复用，0 表示无效句柄
        private readonly int m_id;

        // 功能：把发放方给的编号包成句柄，是造出有效句柄的唯一入口。
        // 入参：id 定时器编号，取 MonoManager 分配的递增值；传 0 造出来的就是无效句柄
        // 返回：无
        // 步骤：
        //   1) 直接存进 m_id，不做校验 —— 编号合法性由发放方保证，这里再判会让 Cancel 的语义多一层分支
        // 上游：MonoManager.AddTimer（经 CallLater / CallEvery）；PlayMode 测试经 InternalsVisibleTo 直接构造不存在的编号
        // 下游：无（只写本结构的字段）
        internal TimerHandle(int id)
        {
            m_id = id;
        }

        // 功能：读句柄里的定时器编号。
        /// <summary>句柄编号，int；0 表示无效句柄，1 起为发放方给的真实编号。</summary>
        public int Id
        {
            get { return m_id; }
        }

        // 功能：判这个句柄是不是「有定时器」—— 只看编号是否为 0。
        // 注意：不跟踪定时器死活：定时器触发完或被取消后编号没变，这里仍返回 true。
        /// <summary>是不是有效句柄；等价于 Id != 0，不代表定时器还在运行。</summary>
        public bool IsValid
        {
            get { return m_id != 0; }
        }

        // 功能：只比编号，编号相同即相等 —— 结构体除编号外没有别的状态要参与比较。
        // 入参：other 待比较的句柄；default(TimerHandle) 与 Invalid 等值
        // 返回：编号相同为 true，不同为 false
        // 步骤：
        //   1) 直接比 m_id，不做空判 —— 结构体不是引用，不可能为 null
        // 上游：== 运算符、Equals(object)
        // 下游：无
        /// <summary>按编号比较，编号相同即为相等。</summary>
        public bool Equals(TimerHandle other)
        {
            return m_id == other.m_id;
        }

        // 功能：先确认装箱对象确实是句柄，再交给 Equals(TimerHandle) 比编号。
        // 入参：obj 任意装箱对象；null 或别的类型直接判不等，不抛异常
        // 返回：确实是句柄且编号相同为 true，其余一律 false
        // 步骤：
        //   1) 用 is 一次完成类型判断与拆箱 —— 先强转再判类型，遇到别的类型会抛 InvalidCastException
        //   2) 确认是句柄后才比编号
        // 上游：object.Equals、Dictionary / List 的默认比较
        // 下游：Equals(TimerHandle)
        /// <summary>装箱对象的比较；不是 TimerHandle 或编号不同都返回 false，不抛异常。</summary>
        public override bool Equals(object obj)
        {
            return obj is TimerHandle && Equals((TimerHandle)obj);
        }

        // 功能：直接拿编号当哈希值，保证与 Equals 同口径 —— 编号相同的句柄哈希必然相同。
        // 入参：无
        // 返回：编号本身，int；无效句柄返回 0
        // 步骤：
        //   1) 不做异或、乘质数这类混合 —— 编号本身就是唯一标识，混合只会让调试时对不上数
        // 上游：Dictionary / HashSet 存句柄时
        // 下游：无
        /// <summary>编号的哈希值；直接返回 Id，与 Equals 保持一致。</summary>
        public override int GetHashCode()
        {
            return m_id;
        }

        // 功能：拼一句调试文本，无效句柄带 Invalid 字样，免得日志里只剩一个看不出含义的 0。
        // 入参：无
        // 返回：「TimerHandle(编号)」或「TimerHandle(Invalid)」，不会是 null
        // 步骤：
        //   1) 先判编号是否为 0 —— 0 直接显示 Invalid，比显示 0 更容易认
        //   2) 有效句柄把编号拼进括号里，日志里能直接对照 Cancel 的入参
        // 上游：日志与调试输出
        // 下游：无
        /// <summary>调试用文本：有效句柄带编号，无效句柄显示 Invalid。</summary>
        public override string ToString()
        {
            return m_id == 0 ? "TimerHandle(Invalid)" : "TimerHandle(" + m_id + ")";
        }

        // 功能：两个句柄编号相同即为 true，内部转给 Equals(TimerHandle)。
        /// <summary>相等比较，按编号判定。</summary>
        public static bool operator ==(TimerHandle left, TimerHandle right)
        {
            return left.Equals(right);
        }

        // 功能：== 取反，同样按编号判定。
        /// <summary>不等比较，按编号判定。</summary>
        public static bool operator !=(TimerHandle left, TimerHandle right)
        {
            return !left.Equals(right);
        }
    }
}
