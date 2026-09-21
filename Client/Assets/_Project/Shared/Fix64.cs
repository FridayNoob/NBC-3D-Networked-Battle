// ============================================================================
//  NBC.Shared.Fix64 —— 确定性定点数（Q32.32）
//  需求依据：FW-M16、§6.4 DET-01（确定性数学）、DET-02（不依赖 UnityEngine）
//
//  ---------------------------------------------------------------------------
//  为什么不能用 float / double（一句话）
//  ---------------------------------------------------------------------------
//  帧同步要求"**同样的输入序列 → 同样的结果**"。
//  而 IEEE 浮点数在不同平台 / 不同 AOT 编译器 / 不同优化开关下，
//  **最后几位可能不一样** —— 一次不同，之后每一步都会放大，两端越跑越远。
//
//  定点数用整数运算，**位级别确定**：在任何平台、任何编译器下结果完全一致。
//
//  ---------------------------------------------------------------------------
//  表示法：Q32.32
//  ---------------------------------------------------------------------------
//  用 64 位有符号整数存"原始值"，小数点固定在第 32 位：
//
//      1.0   →  0x0000000100000000   (== 1L << 32 == 4294967296)
//      0.5   →  0x0000000080000000
//      -2.25 →  0xFFFFFFFD_C0000000
//
//  范围：约 -2147483648.0 ~ +2147483647.0，精度 1/2^32 ≈ 2.3e-10。
//  ⚠️ 范围比 float 小得多 —— 这是帧同步游戏的**常规取舍**：
//     地图坐标、速度、时间都在几万以内，不需要 float 那种指数范围，
//     但**需要的是确定性和够用的精度**。
//
//  ---------------------------------------------------------------------------
//  溢出的处理：**饱和（saturate），不抛异常、不回绕**
//  ---------------------------------------------------------------------------
//  - **回绕**（wrap）绝对不行：那会让"变大"突然变成"变成负数"，
//    在物理/位移里表现为"角色突然瞬移到地图另一头"，而且**不报错**。
//  - **抛异常**也不行：这是逐帧跑的仿真代码，服务端为了一个数值溢出崩掉，代价太大。
//  - **饱和**是确定性的（同样输入必得同样结果），而且行为可预期（"顶到上限"）。
//
//  ⚠️ 代价：**溢出不报错**，会被悄悄夹住。所以要靠测试守住边界（见 Fix64Tests）。
//     唯一的例外是**除以 0** —— 那是逻辑错误，当场抛异常。
// ============================================================================

using System;

namespace NBC.Shared
{
    /// <summary>
    /// 确定性定点数（Q32.32）。**位级别可复现**，用于帧同步逻辑。
    /// </summary>
    public readonly struct Fix64 : IEquatable<Fix64>, IComparable<Fix64>, IComparable
    {
        /// <summary>小数位数（32 位小数、32 位整数部分）。</summary>
        public const int FractionalBits = 32;

        /// <summary>1.0 的原始值。</summary>
        public const long OneRaw = 1L << FractionalBits;

        /// <summary>原始值（Q32.32）。</summary>
        private readonly long m_raw;

        /// <summary>0。</summary>
        public static readonly Fix64 Zero = new Fix64(0L);

        /// <summary>1。</summary>
        public static readonly Fix64 One = new Fix64(OneRaw);

        /// <summary>-1。</summary>
        public static readonly Fix64 MinusOne = new Fix64(-OneRaw);

        /// <summary>0.5。</summary>
        public static readonly Fix64 Half = new Fix64(OneRaw >> 1);

        /// <summary>能表示的最大值。</summary>
        public static readonly Fix64 MaxValue = new Fix64(long.MaxValue);

        /// <summary>能表示的最小值。</summary>
        public static readonly Fix64 MinValue = new Fix64(long.MinValue);

        /// <summary>最小正数（1 / 2^32）。</summary>
        public static readonly Fix64 Epsilon = new Fix64(1L);

        /// <summary>圆周率（截断到 32 位小数）。</summary>
        public static readonly Fix64 Pi = new Fix64(13493037705L);

        /// <summary>私有构造：只接受**原始值**，避免"传了 3 却以为是 3.0"这种错。</summary>
        /// <param name="raw">原始值。</param>
        private Fix64(long raw)
        {
            m_raw = raw;
        }

        /// <summary>直接取原始值。</summary>
        public long RawValue
        {
            get { return m_raw; }
        }

        /// <summary>是否是 0。</summary>
        public bool IsZero
        {
            get { return m_raw == 0L; }
        }

        /// <summary>是否是正数。</summary>
        public bool IsPositive
        {
            get { return m_raw > 0L; }
        }

        /// <summary>是否是负数。</summary>
        public bool IsNegative
        {
            get { return m_raw < 0L; }
        }

        // ====================================================================
        //  构造 / 转换
        // ====================================================================

        /// <summary>从原始值构造（**高级用法**：知道自己在填 Q32.32 时用）。</summary>
        /// <param name="raw">原始值。</param>
        /// <returns>定点数。</returns>
        public static Fix64 FromRaw(long raw)
        {
            return new Fix64(raw);
        }

        /// <summary>从整数构造（精确，不会丢精度）。</summary>
        /// <param name="value">整数值。</param>
        /// <returns>定点数。</returns>
        public static Fix64 FromInt(int value)
        {
            return new Fix64((long)value << FractionalBits);
        }

        /// <summary>从长整数构造（溢出则饱和）。</summary>
        /// <param name="value">长整数值。</param>
        /// <returns>定点数。</returns>
        public static Fix64 FromLong(long value)
        {
            if (value > (long.MaxValue >> FractionalBits))
            {
                return MaxValue;
            }

            if (value < (long.MinValue >> FractionalBits))
            {
                return MinValue;
            }

            return new Fix64(value << FractionalBits);
        }

        /// <summary>
        /// 从浮点数构造。**显式转换** —— 会丢精度，不该悄悄发生。
        /// <para>⚠️ **只该在"外部输入进来"或"表现层"用**（比如读配置表、读摇杆）。
        /// 逻辑内部不许出现浮点。</para>
        /// </summary>
        /// <param name="value">浮点值。</param>
        /// <returns>定点数。</returns>
        public static Fix64 FromFloat(float value)
        {
            return FromDouble(value);
        }

        /// <summary>
        /// 从双精度构造。**显式转换** —— 会丢精度。
        /// <para>⚠️ 这里用 `double` 相乘是**一次性**的转换，不参与后续逻辑运算，
        /// 所以不会破坏确定性。转换结果只取决于输入值本身，是确定的。</para>
        /// </summary>
        /// <param name="value">双精度值。</param>
        /// <returns>定点数。</returns>
        public static Fix64 FromDouble(double value)
        {
            if (double.IsNaN(value))
            {
                throw new ArgumentException("[Fix64] NaN 不能转成定点数。", nameof(value));
            }

            if (value >= 2147483647.0)
            {
                return MaxValue;
            }

            if (value <= -2147483648.0)
            {
                return MinValue;
            }

            return new Fix64((long)(value * OneRaw));
        }

        /// <summary>转成 float。**只该在表现层用**（渲染、UI）。</summary>
        /// <returns>浮点值。</returns>
        public float ToFloat()
        {
            return (float)m_raw / OneRaw;
        }

        /// <summary>转成 double。**只该在表现层或测试里用**。</summary>
        /// <returns>双精度值。</returns>
        public double ToDouble()
        {
            return (double)m_raw / OneRaw;
        }

        /// <summary>
        /// 转成整数（**向零截断**，不是四舍五入、也不是向下取整）。
        /// <para>
        /// ⚠️ 这里踩过一次坑：第一版写的是 `m_raw >> 32` —— 但 C# 的 `>>` 对负数是**算术右移**，
        /// 也就是**朝负无穷**取整。于是 `(-1.5).ToInt()` 得到 **-2**，而契约是 -1。
        /// 改成 `m_raw / OneRaw`：C# 的整数除法就是**向零截断**，正是要的语义。
        /// </para>
        /// <para>
        /// 和 `Floor` 的区别：`Floor(-1.5) == -2`（朝负无穷），`ToInt()` 给 -1（朝零）。
        /// **两个都要有，别混用。**
        /// </para>
        /// </summary>
        /// <returns>整数值（已饱和到 int 范围）。</returns>
        public int ToInt()
        {
            long value = m_raw / OneRaw;

            if (value > int.MaxValue)
            {
                return int.MaxValue;
            }

            if (value < int.MinValue)
            {
                return int.MinValue;
            }

            return (int)value;
        }

        /// <summary>
        /// 转成长整数（**向零截断**）。
        /// <para>⚠️ 与 <see cref="ToInt"/> 同一个坑：**不是**算术右移。</para>
        /// </summary>
        /// <returns>长整数值。</returns>
        public long ToLong()
        {
            return m_raw / OneRaw;
        }

        // ====================================================================
        //  运算符：加减
        // ====================================================================

        /// <summary>加法（溢出则饱和）。</summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>和。</returns>
        public static Fix64 operator +(Fix64 a, Fix64 b)
        {
            long result = unchecked(a.m_raw + b.m_raw);

            // 有符号加法溢出判据：两个操作数符号相同、且与结果符号不同
            if (((a.m_raw ^ result) & (b.m_raw ^ result)) < 0L)
            {
                return a.m_raw >= 0L ? MaxValue : MinValue;
            }

            return new Fix64(result);
        }

        /// <summary>减法（溢出则饱和）。</summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>差。</returns>
        public static Fix64 operator -(Fix64 a, Fix64 b)
        {
            long result = unchecked(a.m_raw - b.m_raw);

            // 有符号减法溢出判据
            if (((a.m_raw ^ b.m_raw) & (a.m_raw ^ result)) < 0L)
            {
                return a.m_raw >= 0L ? MaxValue : MinValue;
            }

            return new Fix64(result);
        }

        /// <summary>取负（`MinValue` 取负会饱和到 `MaxValue`）。</summary>
        /// <param name="value">操作数。</param>
        /// <returns>相反数。</returns>
        public static Fix64 operator -(Fix64 value)
        {
            if (value.m_raw == long.MinValue)
            {
                return MaxValue;
            }

            return new Fix64(-value.m_raw);
        }

        /// <summary>取正（原样返回）。</summary>
        /// <param name="value">操作数。</param>
        /// <returns>原值。</returns>
        public static Fix64 operator +(Fix64 value)
        {
            return value;
        }

        // ====================================================================
        //  运算符：乘除
        // ====================================================================

        /// <summary>
        /// 乘法（溢出则饱和）。
        /// <para>
        /// ⚠️ 用 **128 位中间结果**：两个 Q32.32 的原始值相乘是 Q64.64，
        /// 直接 `a * b >> 32` 会在 `a * b` 那一步就溢出。
        /// 而 `Math.BigMul` 在 netstandard2.1 / Unity 里**没有**，所以手写 64x64 拆成 32 位肢。
        /// </para>
        /// </summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>积。</returns>
        public static Fix64 operator *(Fix64 a, Fix64 b)
        {
            return new Fix64(MulShift32(a.m_raw, b.m_raw));
        }

        /// <summary>
        /// 除法（溢出则饱和；**除以 0 抛异常**）。
        /// </summary>
        /// <param name="a">被除数。</param>
        /// <param name="b">除数。</param>
        /// <returns>商。</returns>
        public static Fix64 operator /(Fix64 a, Fix64 b)
        {
            return new Fix64(DivShift32(a.m_raw, b.m_raw));
        }

        /// <summary>取余（只对原始值取余即可，见实现说明）。</summary>
        /// <param name="a">被除数。</param>
        /// <param name="b">除数。</param>
        /// <returns>余数。</returns>
        public static Fix64 operator %(Fix64 a, Fix64 b)
        {
            if (b.m_raw == 0L)
            {
                throw new DivideByZeroException("[Fix64] 取余的除数不能为 0。");
            }

            // a = a_raw/S，b = b_raw/S ⇒ trunc(a/b) 与 trunc(a_raw/b_raw) 是同一个整数
            // ⇒ 余数 = (a_raw % b_raw) / S  ✔
            return new Fix64(a.m_raw % b.m_raw);
        }

        // ====================================================================
        //  比较
        // ====================================================================

        /// <summary>相等。</summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>是否相等。</returns>
        public static bool operator ==(Fix64 a, Fix64 b)
        {
            return a.m_raw == b.m_raw;
        }

        /// <summary>不等。</summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>是否不等。</returns>
        public static bool operator !=(Fix64 a, Fix64 b)
        {
            return a.m_raw != b.m_raw;
        }

        /// <summary>小于。</summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>a 是否小于 b。</returns>
        public static bool operator <(Fix64 a, Fix64 b)
        {
            return a.m_raw < b.m_raw;
        }

        /// <summary>大于。</summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>a 是否大于 b。</returns>
        public static bool operator >(Fix64 a, Fix64 b)
        {
            return a.m_raw > b.m_raw;
        }

        /// <summary>小于等于。</summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>a 是否小于等于 b。</returns>
        public static bool operator <=(Fix64 a, Fix64 b)
        {
            return a.m_raw <= b.m_raw;
        }

        /// <summary>大于等于。</summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>a 是否大于等于 b。</returns>
        public static bool operator >=(Fix64 a, Fix64 b)
        {
            return a.m_raw >= b.m_raw;
        }

        /// <summary>相等。</summary>
        /// <param name="other">另一个定点数。</param>
        /// <returns>是否相等。</returns>
        public bool Equals(Fix64 other)
        {
            return m_raw == other.m_raw;
        }

        /// <summary>相等。</summary>
        /// <param name="obj">另一个对象。</param>
        /// <returns>是否相等。</returns>
        public override bool Equals(object obj)
        {
            return obj is Fix64 && Equals((Fix64)obj);
        }

        /// <summary>哈希。</summary>
        /// <returns>哈希值。</returns>
        public override int GetHashCode()
        {
            return m_raw.GetHashCode();
        }

        /// <summary>比较（用于排序）。</summary>
        /// <param name="other">另一个定点数。</param>
        /// <returns>负数表示小、0 表示相等、正数表示大。</returns>
        public int CompareTo(Fix64 other)
        {
            if (m_raw < other.m_raw)
            {
                return -1;
            }

            return m_raw > other.m_raw ? 1 : 0;
        }

        /// <summary>
        /// 非泛型比较（排序、非泛型集合、以及**测试框架的 `Assert.Greater`** 都要它）。
        /// <para>
        /// ⚠️ 只实现 `IComparable&lt;T&gt;` 是不够的：NUnit 的 `Assert.Greater(a, b)` 走的是
        /// **非泛型** `IComparable` 那个重载；缺了它就会去匹配 `(int, int)` 的重载，
        /// 然后报"无法从 Fix64 转换为 int"。这个缺口是被测试的编译错误逼出来的 ——
        /// **编译错误也是一种有效的检查手段**。
        /// </para>
        /// </summary>
        /// <param name="obj">另一个对象。</param>
        /// <returns>比较结果。</returns>
        public int CompareTo(object obj)
        {
            if (obj == null)
            {
                return 1;   // 与 .NET 惯例一致：任何实例都大于 null
            }

            if (!(obj is Fix64))
            {
                throw new ArgumentException("[Fix64] 只能和 Fix64 比较。", nameof(obj));
            }

            return CompareTo((Fix64)obj);
        }

        // ====================================================================
        //  隐式 / 显式转换
        // ====================================================================

        /// <summary>整数 → 定点数（**隐式**：精确，不会丢信息）。</summary>
        /// <param name="value">整数值。</param>
        /// <returns>定点数。</returns>
        public static implicit operator Fix64(int value)
        {
            return FromInt(value);
        }

        /// <summary>浮点 → 定点（**显式**：会丢精度，必须写转换）。</summary>
        /// <param name="value">浮点值。</param>
        /// <returns>定点数。</returns>
        public static explicit operator Fix64(float value)
        {
            return FromFloat(value);
        }

        /// <summary>定点 → 浮点（**显式**：只该在表现层用）。</summary>
        /// <param name="value">定点数。</param>
        /// <returns>浮点值。</returns>
        public static explicit operator float(Fix64 value)
        {
            return value.ToFloat();
        }

        /// <summary>定点 → 整数（**显式**：向零截断，会丢小数）。</summary>
        /// <param name="value">定点数。</param>
        /// <returns>整数值。</returns>
        public static explicit operator int(Fix64 value)
        {
            return value.ToInt();
        }

        // ====================================================================
        //  常用数学
        // ====================================================================

        /// <summary>绝对值。</summary>
        /// <param name="value">定点数。</param>
        /// <returns>绝对值。</returns>
        public static Fix64 Abs(Fix64 value)
        {
            if (value.m_raw == long.MinValue)
            {
                return MaxValue;
            }

            return new Fix64(value.m_raw < 0L ? -value.m_raw : value.m_raw);
        }

        /// <summary>符号（-1 / 0 / +1）。</summary>
        /// <param name="value">定点数。</param>
        /// <returns>符号。</returns>
        public static int Sign(Fix64 value)
        {
            if (value.m_raw > 0L)
            {
                return 1;
            }

            return value.m_raw < 0L ? -1 : 0;
        }

        /// <summary>取小。</summary>
        /// <param name="a">a。</param>
        /// <param name="b">b。</param>
        /// <returns>较小的那个。</returns>
        public static Fix64 Min(Fix64 a, Fix64 b)
        {
            return a.m_raw <= b.m_raw ? a : b;
        }

        /// <summary>取大。</summary>
        /// <param name="a">a。</param>
        /// <param name="b">b。</param>
        /// <returns>较大的那个。</returns>
        public static Fix64 Max(Fix64 a, Fix64 b)
        {
            return a.m_raw >= b.m_raw ? a : b;
        }

        /// <summary>钳制到区间。</summary>
        /// <param name="value">值。</param>
        /// <param name="min">下界。</param>
        /// <param name="max">上界。</param>
        /// <returns>钳制后的值。</returns>
        public static Fix64 Clamp(Fix64 value, Fix64 min, Fix64 max)
        {
            if (value.m_raw < min.m_raw)
            {
                return min;
            }

            return value.m_raw > max.m_raw ? max : value;
        }

        /// <summary>
        /// 向下取整（朝负无穷）。
        /// <para>
        /// ⚠️ 实现要点：**把低 32 位掩掉**就等于朝负无穷取整 ——
        /// 补码下掩掉低位永远是"往更小那边靠"。
        /// </para>
        /// <para>
        /// 这里踩过一次坑：第一版写的是 `raw >> 32` 再判断负数减 1。
        /// 但 C# 的 `>>` 对负数是**算术右移**，**本身已经是朝负无穷取整**，
        /// 那一步多减了 —— 结果是 `floor(-1.5)` 得到 -3。
        /// 这个 bug 对**所有负的非整数**都成立，而且不报错。
        /// （是 .NET 数值对拍探针把它抓出来的，不是"看代码看出来的"。）
        /// </para>
        /// </summary>
        /// <param name="value">值。</param>
        /// <returns>取整结果。</returns>
        public static Fix64 Floor(Fix64 value)
        {
            return new Fix64(value.m_raw & ~(OneRaw - 1L));
        }

        /// <summary>向上取整（朝正无穷）。</summary>
        /// <param name="value">值。</param>
        /// <returns>取整结果。</returns>
        public static Fix64 Ceiling(Fix64 value)
        {
            long floored = value.m_raw & ~(OneRaw - 1L);

            if (floored == value.m_raw)
            {
                return new Fix64(floored);
            }

            // 有小数部分才需要 +1；快溢出时饱和。
            if (floored > long.MaxValue - OneRaw)
            {
                return MaxValue;
            }

            return new Fix64(floored + OneRaw);
        }

        /// <summary>
        /// 四舍五入（**远离零**：0.5 → 1，-0.5 → -1）。
        /// <para>⚠️ 明确写死方向：`Math.Round` 默认是"银行家舍入"，两端的实现细节不一致会破坏确定性。</para>
        /// </summary>
        /// <param name="value">值。</param>
        /// <returns>取整结果。</returns>
        public static Fix64 Round(Fix64 value)
        {
            Fix64 half = Half;

            if (value.m_raw >= 0L)
            {
                return Floor(value + half);
            }

            return Ceiling(value - half);
        }

        /// <summary>线性插值（**不钳制 t**，需要钳制请自己 `Clamp`）。</summary>
        /// <param name="a">起点。</param>
        /// <param name="b">终点。</param>
        /// <param name="t">插值系数。</param>
        /// <returns>`a + (b - a) * t`。</returns>
        public static Fix64 Lerp(Fix64 a, Fix64 b, Fix64 t)
        {
            return a + (b - a) * t;
        }

        /// <summary>
        /// 平方根（负数抛异常）。
        /// <para>
        /// 做法：`sqrt(x_raw / S) = sqrt(x_raw) / 2^16` ⇒ **对原始值开整数平方根，再左移 16 位**。
        /// 这样不需要 128 位中间结果（`x_raw &lt;&lt; 32` 会溢出）。
        /// </para>
        /// <para>
        /// ⚠️ 精度：整数平方根截断后再移位，绝对误差约 `2^-16`。
        /// 对"归一化方向向量"这类用途足够；需要更高精度可以再做牛顿迭代。
        /// </para>
        /// </summary>
        /// <param name="value">值。</param>
        /// <returns>平方根。</returns>
        public static Fix64 Sqrt(Fix64 value)
        {
            if (value.m_raw < 0L)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value.ToDouble(), "[Fix64] 不能对负数开平方。");
            }

            if (value.m_raw == 0L)
            {
                return Zero;
            }

            return new Fix64((long)(Isqrt((ulong)value.m_raw) << 16));
        }

        // ====================================================================
        //  内部：128 位中间结果的乘除
        // ====================================================================

        /// <summary>把有符号数转成绝对值（无符号），并正确处理 `long.MinValue`。</summary>
        /// <param name="value">有符号值。</param>
        /// <returns>绝对值。</returns>
        private static ulong UnsignedAbs(long value)
        {
            if (value >= 0L)
            {
                return (ulong)value;
            }

            // ⚠️ 不能写 (ulong)(-value)：long.MinValue 取负会溢出。
            return (ulong)(-(value + 1L)) + 1UL;
        }

        /// <summary>
        /// 计算 `(a * b) >> 32`，**带饱和**。
        /// <para>用 32 位肢拼出 128 位中间结果 —— `Math.BigMul` 在 netstandard2.1 / Unity 里没有。</para>
        /// </summary>
        /// <param name="a">左操作数（原始值）。</param>
        /// <param name="b">右操作数（原始值）。</param>
        /// <returns>结果原始值（饱和）。</returns>
        private static long MulShift32(long a, long b)
        {
            if (a == 0L || b == 0L)
            {
                return 0L;
            }

            bool negative = (a < 0L) != (b < 0L);

            ulong ua = UnsignedAbs(a);
            ulong ub = UnsignedAbs(b);

            ulong aLo = ua & 0xFFFFFFFFUL;
            ulong aHi = ua >> 32;
            ulong bLo = ub & 0xFFFFFFFFUL;
            ulong bHi = ub >> 32;

            ulong p0 = aLo * bLo;
            ulong p1 = aHi * bLo;
            ulong p2 = aLo * bHi;
            ulong p3 = aHi * bHi;

            // 128 位结果 = hi:lo
            ulong lo = p0;
            ulong hi = p3;

            // 把 (p1 + p2) << 32 累加进去；mid 最大约 3 * 2^32，不会溢出 ulong
            ulong mid = (lo >> 32) + (p1 & 0xFFFFFFFFUL) + (p2 & 0xFFFFFFFFUL);
            lo = (lo & 0xFFFFFFFFUL) | (mid << 32);
            hi += (p1 >> 32) + (p2 >> 32) + (mid >> 32);

            // 现在要 (hi:lo) >> 32。若 hi 的高 32 位非 0，说明结果超过 64 位 → 饱和。
            if ((hi >> 32) != 0UL)
            {
                return negative ? long.MinValue : long.MaxValue;
            }

            ulong magnitude = (hi << 32) | (lo >> 32);

            return ApplySign(magnitude, negative);
        }

        /// <summary>
        /// 计算 `(a &lt;&lt; 32) / b`（向零截断），**带饱和**；`b == 0` 抛异常。
        /// <para>
        /// 用"整数部分 + 逐位求 32 位小数"的二进制长除法，
        /// **避免把被除数左移 32 位**（那会溢出 64 位）。
        /// </para>
        /// </summary>
        /// <param name="a">被除数（原始值）。</param>
        /// <param name="b">除数（原始值）。</param>
        /// <returns>结果原始值（饱和）。</returns>
        private static long DivShift32(long a, long b)
        {
            if (b == 0L)
            {
                throw new DivideByZeroException(
                    "[Fix64] 除数不能为 0。这是逻辑错误，不做饱和处理。");
            }

            if (a == 0L)
            {
                return 0L;
            }

            bool negative = (a < 0L) != (b < 0L);

            ulong ua = UnsignedAbs(a);
            ulong ub = UnsignedAbs(b);

            ulong quotient = ua / ub;
            ulong remainder = ua % ub;

            // 整数部分超过 31 位 → (quotient << 32) 必然超出 64 位 → 饱和
            if (quotient > 0x7FFFFFFFUL)
            {
                return negative ? long.MinValue : long.MaxValue;
            }

            // 逐位求 32 位小数：标准二进制长除法。
            // ⚠️ 代价：32 次循环。除法本来就不是最热的运算，用确定性换这点开销是划算的。
            ulong fraction = 0UL;

            for (int i = 0; i < FractionalBits; i++)
            {
                remainder <<= 1;
                fraction <<= 1;

                if (remainder >= ub)
                {
                    remainder -= ub;
                    fraction |= 1UL;
                }
            }

            return ApplySign((quotient << FractionalBits) | fraction, negative);
        }

        /// <summary>把"绝对值 + 符号"还原成有符号原始值，**带饱和检查**。</summary>
        /// <param name="magnitude">绝对值。</param>
        /// <param name="negative">是否是负数。</param>
        /// <returns>有符号原始值。</returns>
        private static long ApplySign(ulong magnitude, bool negative)
        {
            if (!negative)
            {
                return magnitude > (ulong)long.MaxValue ? long.MaxValue : (long)magnitude;
            }

            if (magnitude > 0x8000000000000000UL)
            {
                return long.MinValue;
            }

            if (magnitude == 0x8000000000000000UL)
            {
                return long.MinValue;
            }

            return -(long)magnitude;
        }

        /// <summary>整数平方根（向下取整）。牛顿迭代，位级别确定。</summary>
        /// <param name="value">无符号值。</param>
        /// <returns>floor(sqrt(value))。</returns>
        private static ulong Isqrt(ulong value)
        {
            if (value == 0UL)
            {
                return 0UL;
            }

            // 初始猜测：2^ceil(bits/2)，保证 >= 真值，牛顿迭代从上方单调收敛
            ulong x = value;
            ulong result = 1UL;

            while (x > 0UL)
            {
                x >>= 2;
                result <<= 1;
            }

            // 牛顿迭代：result = (result + value / result) / 2
            while (true)
            {
                ulong next = (result + value / result) >> 1;

                if (next >= result)
                {
                    break;
                }

                result = next;
            }

            // 收敛后可能比真值大 1，收一下
            while (result > 0UL && result * result > value)
            {
                result--;
            }

            return result;
        }

        // ====================================================================
        //  字符串
        // ====================================================================

        /// <summary>
        /// 调试文本（保留 6 位小数）。
        /// <para>⚠️ **只用于日志 / 调试**，不要拿它做逻辑判断。</para>
        /// </summary>
        /// <returns>可读文本。</returns>
        public override string ToString()
        {
            // 用整数运算拼字符串，不经过浮点，避免调试输出在不同平台上不一样。
            long raw = m_raw;
            bool negative = raw < 0L;
            ulong magnitude = UnsignedAbs(raw);

            ulong integerPart = magnitude >> FractionalBits;
            ulong fraction = magnitude & (OneRaw - 1UL);

            // 取 6 位小数：fraction / 2^32 ≈ 小数部分
            ulong scaled = 0UL;

            for (int i = 0; i < 6; i++)
            {
                fraction *= 10UL;
                scaled = scaled * 10UL + (fraction >> FractionalBits);
                fraction &= (OneRaw - 1UL);
            }

            string text = integerPart.ToString() + "." + scaled.ToString("D6");

            return negative ? "-" + text : text;
        }
    }
}
