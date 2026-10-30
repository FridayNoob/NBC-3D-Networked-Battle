// 这个文件里有：
//   Fix64（结构体）：Q32.32 定点数，64 位整数存原始值，加减乘除与开方全走整数运算，跨平台位级别一致。

using System;

namespace NBC.Shared
{
    // 主要功能：Q32.32 定点数，用 64 位整数存原始值，加减乘除、取整、开方与浮点互转都走确定性路径，供帧同步仿真使用。
    // 主要数据成员：m_raw（原始值，1.0 对应 4294967296，精度 1/2^32 ≈ 2.3e-10，范围约 ±2147483647）、FractionalBits / OneRaw（换算常量）、Zero / One / Half / Pi 等常量。
    // 主要外部方法：FromRaw / FromInt / FromFloat / ToInt / ToFloat、+ - * / % 与比较运算符、Abs / Min / Max / Clamp / Floor / Ceiling / Round / Lerp / Sqrt。
    // 谁在用它：NBC.Shared 的 FixVector3、FixMath、SimEntity.Position、WorldState、WorldStep；NBC.Game 的 LockstepWire 从协议 raw 装回、NetSession 把输入转成 raw；Fix64Tests 逐项验算。
    // 注意：绝不能改用 float / double —— 同样的输入在不同平台、不同 AOT 编译器下末位可能不同，帧同步会把差异逐步放大。
    // 注意：溢出一律饱和，不抛异常也不回绕；唯一例外是除以 0，那是逻辑错误，当场抛异常。
    /// <summary>确定性定点数（Q32.32）：64 位整数存原始值，位级别可复现，不依赖 UnityEngine。</summary>
    public readonly struct Fix64 : IEquatable<Fix64>, IComparable<Fix64>, IComparable
    {
        /// <summary>小数位数；固定 32，即低 32 位是小数、高 32 位是整数与符号。</summary>
        public const int FractionalBits = 32;

        /// <summary>1.0 的原始值；等于 4294967296，定点与整数换算的基准。</summary>
        public const long OneRaw = 1L << FractionalBits;

        // 原始值（Q32.32）：低 32 位是小数、高 32 位是整数与符号；取值就是 long 全域。
        private readonly long m_raw;

        /// <summary>0；原始值 0，判断是否为零请用 IsZero 或直接与它比较。</summary>
        public static readonly Fix64 Zero = new Fix64(0L);

        /// <summary>1.0；原始值 4294967296，对应锁步世界的 1 米或 1 个无量纲单位。</summary>
        public static readonly Fix64 One = new Fix64(OneRaw);

        /// <summary>-1.0；原始值 -4294967296，方向取反与符号判断常用。</summary>
        public static readonly Fix64 MinusOne = new Fix64(-OneRaw);

        /// <summary>0.5；原始值 2147483648，四舍五入的半单位基准。</summary>
        public static readonly Fix64 Half = new Fix64(OneRaw >> 1);

        /// <summary>最大值，约 +2147483647.0（原始值 long.MaxValue）；再加会饱和到这里。</summary>
        public static readonly Fix64 MaxValue = new Fix64(long.MaxValue);

        /// <summary>最小值，约 -2147483648.0（原始值 long.MinValue）；再减会饱和到这里。</summary>
        public static readonly Fix64 MinValue = new Fix64(long.MinValue);

        /// <summary>最小正数 1/2^32 ≈ 2.3e-10（原始值 1）；比它小的正量会被量化为 0。</summary>
        public static readonly Fix64 Epsilon = new Fix64(1L);

        /// <summary>圆周率，截断到 1/2^32 精度（原始值 13493037705）；FixMath.Pi 直接复用它。</summary>
        public static readonly Fix64 Pi = new Fix64(13493037705L);

        // 功能：用原始值构造，只给本文件内部与 FromRaw 用。
        // 入参：raw 原始值（Q32.32），1.0 对应 4294967296
        // 返回：无
        // 步骤：
        //   1) 原样存进 m_raw —— 不做换算，也不做范围检查
        // 注意：私有构造只收原始值，避免外部把 3 当 3.0 传进来。
        // 上游：FromRaw、FromInt、FromLong、FromDouble 及本文件内部的运算符与数学方法
        // 下游：无
        private Fix64(long raw)
        {
            m_raw = raw;
        }

        /// <summary>原始值（Q32.32）；低 32 位是小数、高 32 位是整数与符号，序列化与哈希都靠它。</summary>
        // 功能：原样返回内部的 64 位原始值。
        public long RawValue
        {
            get { return m_raw; }
        }

        /// <summary>是否恰好为 0；定点数按原始值精确判断，没有容差。</summary>
        // 功能：判断原始值是否为 0。
        public bool IsZero
        {
            get { return m_raw == 0L; }
        }

        /// <summary>是否大于 0；最小的正数 Epsilon 也算正。</summary>
        // 功能：判断原始值是否大于 0。
        public bool IsPositive
        {
            get { return m_raw > 0L; }
        }

        /// <summary>是否小于 0；-Epsilon 也算负。</summary>
        // 功能：判断原始值是否小于 0。
        public bool IsNegative
        {
            get { return m_raw < 0L; }
        }

        /// <summary>用原始值构造，高级用法：调用方必须知道自己在填 Q32.32。</summary>
        /// <param name="raw">原始值；1.0 对应 4294967296，单位由调用方约定。</param>
        // 功能：把原始值直接包成 Fix64，不做换算。
        public static Fix64 FromRaw(long raw)
        {
            return new Fix64(raw);
        }

        /// <summary>从整数构造，左移 32 位，精确且不会丢精度。</summary>
        /// <param name="value">整数值，单位由调用方约定；锁步世界坐标用米。</param>
        /// <returns>对应的定点数；范围受 int 限制，不会饱和。</returns>
        // 功能：把整数按 2 的 32 次方放大成定点数。
        // 入参：value 整数，范围 int.MinValue..int.MaxValue，单位由调用方约定
        // 返回：value 的定点表示，精确无误差
        // 步骤：
        //   1) 先转 long 再左移 32 位 —— 直接在 int 上移会先溢出
        // 上游：FixVector3 的整数构造、WorldStep.MmToMeters、FixMath 的常量与角度换算、NBC.Game 的 NetSession 与 LockstepSnapshotAdapter
        // 下游：私有构造
        public static Fix64 FromInt(int value)
        {
            return new Fix64((long)value << FractionalBits);
        }

        /// <summary>从长整数构造，超出定点范围时饱和到 MaxValue / MinValue。</summary>
        /// <param name="value">长整数值；超出 [-2147483648, 2147483647] 会被夹住。</param>
        /// <returns>对应的定点数；溢出时饱和。</returns>
        // 功能：把长整数按 2 的 32 次方放大成定点数，溢出饱和。
        // 入参：value 长整数，单位由调用方约定
        // 返回：value 的定点表示；超出约 ±2147483647 时饱和到 MaxValue / MinValue
        // 步骤：
        //   1) 先与 long.MaxValue >> 32 比较 —— 大于它左移一定溢出，直接返回 MaxValue
        //   2) 再与 long.MinValue >> 32 比较 —— 小于它左移一定溢出，直接返回 MinValue
        //   3) 否则左移 32 位
        // 注意：边界比较必须在移位之前做，移完再看就晚了；这里的饱和是确定性的，两端结果一致。
        // 上游：暂无调用（保留给整数坐标或时间的定点转换）
        // 下游：私有构造
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

        /// <summary>从 float 构造，显式转换且会丢精度；只该在外部输入或表现层用，逻辑内部绝不能出现浮点。</summary>
        /// <param name="value">浮点值；NaN 会抛 ArgumentException，超出范围会饱和。</param>
        /// <returns>最接近的定点数；低位抖动不可避免。</returns>
        // 功能：转调 FromDouble，把浮点值转成最接近的定点数。
        public static Fix64 FromFloat(float value)
        {
            return FromDouble(value);
        }

        /// <summary>从 double 构造，显式转换且会丢精度；一次性的边界转换不影响确定性。</summary>
        /// <param name="value">双精度值；NaN 会抛 ArgumentException，超出范围会饱和。</param>
        /// <returns>按 1/2^32 截断的定点数；向零取整。</returns>
        // 功能：把 double 乘 2 的 32 次方后截断成定点数。
        // 入参：value 双精度值，范围约 ±2147483647；NaN 非法
        // 返回：转换后的定点数；超出范围饱和到 MaxValue / MinValue
        // 步骤：
        //   1) 先拦 NaN —— 它转不成整数，必须当场抛异常
        //   2) 达到 2147483647 或低于 -2147483648 就饱和，避免后面的乘法溢出
        //   3) 乘 2 的 32 次方后转 long —— 向零截断，正负一致
        // 注意：这是一次性转换，结果只取决于输入值本身，后续不参与浮点运算，所以不破坏确定性；但输入若来自算出来的浮点，末位差异仍会带进来。
        // 上游：FromFloat、Fix64Tests
        // 下游：无
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

        /// <summary>转成 float，只该在表现层用；float 精度有限，末位可能与其它平台不同。</summary>
        /// <returns>最接近的浮点值。</returns>
        // 功能：把原始值除以 2 的 32 次方转成 float。
        // 入参：无（读自身）
        // 返回：最接近的浮点值，精度受 float 的 24 位有效位限制
        // 步骤：
        //   1) 原始值与 4294967296 都转成 float 后相除 —— 除法也在 float 里做，只降一次精度
        // 注意：只用于渲染、UI 或日志；转回逻辑层会把浮点误差带进来。
        // 上游：显式转 float 运算符；目前无其它调用
        // 下游：无
        public float ToFloat()
        {
            return (float)m_raw / OneRaw;
        }

        /// <summary>转成 double，只该在表现层或测试里用，不参与逻辑运算。</summary>
        /// <returns>双精度值。</returns>
        // 功能：把原始值除以 2 的 32 次方转成 double。
        // 入参：无（读自身）
        // 返回：最接近的双精度值
        // 步骤：
        //   1) 原始值转 double 后除以 2 的 32 次方 —— 不经过 float，少一次精度损失
        // 注意：原始值最多 63 位，double 只有 53 位有效位，超出部分会丢；只用于显示与测试。
        // 上游：FixMath 的错误信息、Fix64Tests
        // 下游：无
        public double ToDouble()
        {
            return (double)m_raw / OneRaw;
        }

        /// <summary>转成 int，向零截断；超出 int 范围时饱和。</summary>
        /// <returns>整数值；-1.5 得 -1，不是 -2。</returns>
        // 功能：把定点数转成 int，向零截断。
        // 入参：无（读自身）
        // 返回：整数部分；超出 int 范围饱和到 int.MaxValue / int.MinValue
        // 步骤：
        //   1) 用原始值除以 2 的 32 次方 —— C# 整数除法向零截断，正是契约
        //   2) 结果超出 int 范围就饱和 —— 定点范围比 int 大，必须夹
        // 注意：不能用 m_raw >> 32，算术右移对负数是朝负无穷取整，-1.5 会得 -2；与 Floor 的区别就在这里，两个都要有，别混用。
        // 上游：FixMath.SinCos 的角度归约、Fix64Tests
        // 下游：无
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

        /// <summary>转成 long，向零截断；结果一定落在 long 范围内。</summary>
        /// <returns>整数部分；-1.5 得 -1。</returns>
        // 功能：把定点数转成 long，向零截断。
        // 入参：无（读自身）
        // 返回：整数部分，范围约 ±2147483647，必然落在 long 内
        // 步骤：
        //   1) 原始值除以 2 的 32 次方 —— 向零截断
        // 注意：与 ToInt 同一个坑，不能用算术右移代替除法。
        // 上游：Fix64Tests
        // 下游：无
        public long ToLong()
        {
            return m_raw / OneRaw;
        }

        /// <summary>加法，溢出饱和到 MaxValue / MinValue，不回绕。</summary>
        // 功能：两个定点数相加，溢出饱和。
        // 入参：a 左操作数；b 右操作数（单位必须一致，米加米）
        // 返回：和的定点数；溢出时返回同号的 MaxValue / MinValue
        // 步骤：
        //   1) 用 unchecked 相加 —— 溢出交给下面的判据处理，不让运行时抛
        //   2) 判溢出：两个操作数同号且结果异号 —— 只有这种情况才是真溢出
        //   3) 溢出按 a 的符号饱和 —— 正溢出取 MaxValue，负溢出取 MinValue
        // 注意：溢出不报错，会被悄悄夹住；靠测试守边界，别拿它做需要报错的数值。
        // 上游：FixVector3 的向量加法（WorldStep 的位置累加走它）、Fix64Tests
        // 下游：私有构造
        public static Fix64 operator +(Fix64 a, Fix64 b)
        {
            long result = unchecked(a.m_raw + b.m_raw);

            if (((a.m_raw ^ result) & (b.m_raw ^ result)) < 0L)
            {
                return a.m_raw >= 0L ? MaxValue : MinValue;
            }

            return new Fix64(result);
        }

        /// <summary>减法，溢出饱和到 MaxValue / MinValue，不回绕。</summary>
        // 功能：两个定点数相减，溢出饱和。
        // 入参：a 左操作数；b 右操作数（单位必须一致）
        // 返回：差的定点数；溢出时返回同号的 MaxValue / MinValue
        // 步骤：
        //   1) 用 unchecked 相减 —— 溢出交给下面的判据处理，不让运行时抛
        //   2) 判溢出：两个操作数异号且结果与 a 异号 —— 只有这种情况才是真溢出
        //   3) 溢出按 a 的符号饱和
        // 注意：溢出不报错，会被悄悄夹住；靠测试守边界。
        // 上游：FixVector3 的向量减法（WorldStep 的差向量计算走它）
        // 下游：私有构造
        public static Fix64 operator -(Fix64 a, Fix64 b)
        {
            long result = unchecked(a.m_raw - b.m_raw);

            if (((a.m_raw ^ b.m_raw) & (a.m_raw ^ result)) < 0L)
            {
                return a.m_raw >= 0L ? MaxValue : MinValue;
            }

            return new Fix64(result);
        }

        /// <summary>取负；MinValue 取负会饱和到 MaxValue。</summary>
        // 功能：把定点数取相反数。
        // 入参：value 操作数
        // 返回：相反数；value 为 MinValue 时饱和到 MaxValue
        // 步骤：
        //   1) 先单独处理 long.MinValue —— 它取负会溢出，直接饱和
        //   2) 其余情况直接取负原始值
        // 注意：MinValue 取负不是正数本身，饱和是契约。
        // 上游：FixMath、FixVector3 的取负、测试
        // 下游：私有构造
        public static Fix64 operator -(Fix64 value)
        {
            if (value.m_raw == long.MinValue)
            {
                return MaxValue;
            }

            return new Fix64(-value.m_raw);
        }

        /// <summary>取正，原样返回，不做复制也不做检查。</summary>
        // 功能：原样返回操作数。
        public static Fix64 operator +(Fix64 value)
        {
            return value;
        }

        /// <summary>乘法，用 128 位中间结果，溢出饱和。</summary>
        // 功能：两个定点数相乘，结果仍为 Q32.32。
        public static Fix64 operator *(Fix64 a, Fix64 b)
        {
            return new Fix64(MulShift32(a.m_raw, b.m_raw));
        }

        /// <summary>除法，向零截断；溢出饱和，除以 0 抛 DivideByZeroException。</summary>
        // 功能：两个定点数相除，结果仍为 Q32.32。
        public static Fix64 operator /(Fix64 a, Fix64 b)
        {
            return new Fix64(DivShift32(a.m_raw, b.m_raw));
        }

        /// <summary>取余，向零截断；除数为 0 时抛 DivideByZeroException。</summary>
        // 功能：求两个定点数相除的余数。
        // 入参：a 被除数；b 除数（不能为 0）
        // 返回：余数，符号随被除数，绝对值小于除数
        // 步骤：
        //   1) 先判 b 为 0 —— 取余也没有良定义的零除，当场抛异常
        //   2) 直接对原始值取余 —— 两边同除 2 的 32 次方，截断整数商相同，余数再除 2 的 32 次方就是结果
        // 注意：余数符号跟随 C# 的 %，负数取余会得到负数。
        // 上游：调用方做周期或取模（目前主要是测试）
        // 下游：私有构造
        public static Fix64 operator %(Fix64 a, Fix64 b)
        {
            if (b.m_raw == 0L)
            {
                throw new DivideByZeroException("[Fix64] 取余的除数不能为 0。");
            }

            return new Fix64(a.m_raw % b.m_raw);
        }

        /// <summary>相等；直接比原始值，精确比较没有容差。</summary>
        // 功能：判断两个定点数的原始值是否相等。
        // 入参：a 左操作数；b 右操作数
        // 返回：原始值相等为 true，否则为 false
        // 步骤：
        //   1) 直接比 m_raw —— 定点数没有 NaN 和正负零，按位比较就是数学相等
        // 上游：状态比较、Equals、Fix64Tests
        // 下游：无
        public static bool operator ==(Fix64 a, Fix64 b)
        {
            return a.m_raw == b.m_raw;
        }

        /// <summary>不等；等价于对 == 取反。</summary>
        // 功能：判断两个定点数的原始值是否不等。
        // 入参：a 左操作数；b 右操作数
        // 返回：原始值不等为 true，相等为 false
        // 步骤：
        //   1) 直接比 m_raw 是否不等 —— 与 == 同一判据
        // 上游：状态比较、测试
        // 下游：无
        public static bool operator !=(Fix64 a, Fix64 b)
        {
            return a.m_raw != b.m_raw;
        }

        /// <summary>小于；原始值是有符号数，顺序与数学大小一致。</summary>
        // 功能：判断 a 是否小于 b。
        // 入参：a 左操作数；b 右操作数
        // 返回：a 小于 b 为 true，否则为 false
        // 步骤：
        //   1) 直接比 m_raw —— Q32.32 的原始值顺序就是数值顺序
        // 上游：FixMath 的定义域判断、Fix64Tests
        // 下游：无
        public static bool operator <(Fix64 a, Fix64 b)
        {
            return a.m_raw < b.m_raw;
        }

        /// <summary>大于；原始值是有符号数，顺序与数学大小一致。</summary>
        // 功能：判断 a 是否大于 b。
        // 入参：a 左操作数；b 右操作数
        // 返回：a 大于 b 为 true，否则为 false
        // 步骤：
        //   1) 直接比 m_raw —— Q32.32 的原始值顺序就是数值顺序
        // 上游：FixMath 的定义域判断、Fix64Tests
        // 下游：无
        public static bool operator >(Fix64 a, Fix64 b)
        {
            return a.m_raw > b.m_raw;
        }

        /// <summary>小于等于；直接比原始值。</summary>
        // 功能：判断 a 是否小于等于 b。
        // 入参：a 左操作数；b 右操作数
        // 返回：a 小于或等于 b 为 true，否则为 false
        // 步骤：
        //   1) 直接比 m_raw —— 与小于同一判据
        // 上游：FixVector3.ClampMagnitude 的区间判断、Fix64Tests
        // 下游：无
        public static bool operator <=(Fix64 a, Fix64 b)
        {
            return a.m_raw <= b.m_raw;
        }

        /// <summary>大于等于；直接比原始值。</summary>
        // 功能：判断 a 是否大于等于 b。
        // 入参：a 左操作数；b 右操作数
        // 返回：a 大于或等于 b 为 true，否则为 false
        // 步骤：
        //   1) 直接比 m_raw —— 与大于同一判据
        // 上游：暂无直接调用（保留给范围判断）
        // 下游：无
        public static bool operator >=(Fix64 a, Fix64 b)
        {
            return a.m_raw >= b.m_raw;
        }

        /// <summary>相等；与 == 同一判据，实现 IEquatable 让泛型集合不走装箱。</summary>
        // 功能：比较两个定点数的原始值是否相等。
        // 入参：other 另一个定点数
        // 返回：原始值相等为 true
        // 步骤：
        //   1) 直接比 m_raw —— 判据与 == 保持一致
        // 上游：泛型集合、Equals(object)、测试
        // 下游：无
        public bool Equals(Fix64 other)
        {
            return m_raw == other.m_raw;
        }

        /// <summary>装箱比较：obj 是 Fix64 且原始值相等时为 true。</summary>
        /// <param name="obj">待比较对象，可为 null 或其它类型。</param>
        /// <returns>是 Fix64 且原始值相等为 true；null 或类型不符为 false。</returns>
        // 功能：先认类型，再转泛型 Equals 比较。
        // 入参：obj 任意对象，可为 null
        // 返回：obj 是 Fix64 且原始值相等为 true；null 或类型不符为 false
        // 步骤：
        //   1) 先用 is 判类型 —— 不是 Fix64 直接 false，避免拆箱异常
        //   2) 再转泛型 Equals —— 判据只有一份
        // 注意：null 是 false，不等于零值；零值要和 Zero 相等才算。
        // 上游：object 比较、非泛型集合
        // 下游：Equals(Fix64)
        public override bool Equals(object obj)
        {
            return obj is Fix64 && Equals((Fix64)obj);
        }

        /// <summary>原始值的哈希；相等定点数的哈希一定相同。</summary>
        /// <returns>哈希值；不等定点数也可能碰撞，不能反过来用哈希判等。</returns>
        // 功能：直接返回原始值的哈希。
        // 入参：无（读自身）
        // 返回：m_raw 的哈希；相等值必同哈希
        // 步骤：
        //   1) 转调 long.GetHashCode —— 不用自己混位，相等判据只有 m_raw
        // 注意：哈希只用于容器查表，判等要用 == 或 Equals。
        // 上游：Dictionary / HashSet 等哈希容器
        // 下游：无
        public override int GetHashCode()
        {
            return m_raw.GetHashCode();
        }

        /// <summary>比较大小，供排序使用。</summary>
        /// <param name="other">另一个定点数。</param>
        /// <returns>负数表示更小、0 表示相等、正数表示更大。</returns>
        // 功能：按原始值比较两个定点数。
        // 入参：other 另一个定点数
        // 返回：本值更小返回 -1，相等返回 0，更大返回 1
        // 步骤：
        //   1) 先比小于 —— 原始值就是有符号定点数的自然顺序
        //   2) 再比大于，剩下就是相等
        // 上游：排序、Fix64Tests
        // 下游：无
        public int CompareTo(Fix64 other)
        {
            if (m_raw < other.m_raw)
            {
                return -1;
            }

            return m_raw > other.m_raw ? 1 : 0;
        }

        /// <summary>非泛型比较，排序与非泛型集合要用；NUnit 的 Assert.Greater 也走这条重载。</summary>
        /// <param name="obj">另一个对象；null 视为更小。</param>
        /// <returns>任何实例都大于 null；类型不符抛 ArgumentException。</returns>
        // 功能：先处理 null 与类型，再转泛型 CompareTo。
        // 入参：obj 任意对象，可为 null
        // 返回：null 返回 1；类型不符抛 ArgumentException；否则返回泛型比较结果
        // 步骤：
        //   1) null 直接返回 1 —— 与 .NET 惯例一致，实例大于 null
        //   2) 类型不符抛 ArgumentException —— 和不同单位比较没有意义
        //   3) 转泛型 CompareTo —— 判据只有一份
        // 注意：只实现泛型 IComparable 不够，NUnit 的 Assert.Greater(a, b) 走非泛型重载，缺了会报无法从 Fix64 转 int；这个缺口是被测试的编译错误逼出来的。
        // 上游：排序、非泛型集合、Fix64Tests
        // 下游：CompareTo(Fix64)
        public int CompareTo(object obj)
        {
            if (obj == null)
            {
                return 1;
            }

            if (!(obj is Fix64))
            {
                throw new ArgumentException("[Fix64] 只能和 Fix64 比较。", nameof(obj));
            }

            return CompareTo((Fix64)obj);
        }

        /// <summary>整数隐式转定点数，精确，不会丢信息。</summary>
        /// <param name="value">整数值，单位由调用方约定。</param>
        // 功能：转调 FromInt，把整数转成定点数。
        public static implicit operator Fix64(int value)
        {
            return FromInt(value);
        }

        /// <summary>float 显式转定点，会丢精度，必须写转换。</summary>
        /// <param name="value">浮点值。</param>
        // 功能：转调 FromFloat，把浮点值转成定点数。
        public static explicit operator Fix64(float value)
        {
            return FromFloat(value);
        }

        /// <summary>定点显式转 float，只该在表现层用。</summary>
        /// <param name="value">定点数。</param>
        // 功能：转调 ToFloat，把定点数转成 float。
        public static explicit operator float(Fix64 value)
        {
            return value.ToFloat();
        }

        /// <summary>定点显式转 int，向零截断，会丢小数。</summary>
        /// <param name="value">定点数。</param>
        // 功能：转调 ToInt，把定点数转成整数。
        public static explicit operator int(Fix64 value)
        {
            return value.ToInt();
        }

        /// <summary>绝对值；MinValue 取绝对值会饱和到 MaxValue。</summary>
        // 功能：取定点数的绝对值。
        // 入参：value 定点数
        // 返回：非负值；value 为 MinValue 时饱和到 MaxValue
        // 步骤：
        //   1) 先处理 long.MinValue —— 取负会溢出
        //   2) 负数取反，非负数原样返回
        // 注意：MinValue 的绝对值不是正数本身，饱和是契约。
        // 上游：FixMath.Atan2、FixVector3.Abs、测试
        // 下游：私有构造
        public static Fix64 Abs(Fix64 value)
        {
            if (value.m_raw == long.MinValue)
            {
                return MaxValue;
            }

            return new Fix64(value.m_raw < 0L ? -value.m_raw : value.m_raw);
        }

        /// <summary>符号：返回 -1、0 或 +1。</summary>
        // 功能：判断定点数是负、零还是正。
        // 入参：value 定点数
        // 返回：正数返回 1，负数返回 -1，零返回 0
        // 步骤：
        //   1) 先判大于 0
        //   2) 再判小于 0，剩下就是 0
        // 上游：暂无调用（保留给方向或符号判断）
        // 下游：无
        public static int Sign(Fix64 value)
        {
            if (value.m_raw > 0L)
            {
                return 1;
            }

            return value.m_raw < 0L ? -1 : 0;
        }

        /// <summary>取两个定点数中较小的一个；相等时返回 a。</summary>
        // 功能：比较原始值取小。
        // 入参：a 第一个值；b 第二个值（单位需一致）
        // 返回：较小的那个；相等返回 a
        // 步骤：
        //   1) 直接比原始值 —— 定点顺序与 long 一致，不需要容差
        // 上游：FixVector3.Min
        // 下游：无
        public static Fix64 Min(Fix64 a, Fix64 b)
        {
            return a.m_raw <= b.m_raw ? a : b;
        }

        /// <summary>取两个定点数中较大的一个；相等时返回 a。</summary>
        // 功能：比较原始值取大。
        // 入参：a 第一个值；b 第二个值（单位需一致）
        // 返回：较大的那个；相等返回 a
        // 步骤：
        //   1) 直接比原始值 —— 定点顺序与 long 一致，不需要容差
        // 上游：FixVector3.Max
        // 下游：无
        public static Fix64 Max(Fix64 a, Fix64 b)
        {
            return a.m_raw >= b.m_raw ? a : b;
        }

        /// <summary>把值钳到 min..max；调用方须保证 min 不大于 max。</summary>
        // 功能：把值夹进闭区间。
        // 入参：value 待钳值；min 下界；max 上界（三者单位需一致，且 min 不应大于 max）
        // 返回：低于 min 得 min，高于 max 得 max，否则原样返回
        // 步骤：
        //   1) 先比下界 —— 低于下界直接返回 min
        //   2) 再比上界 —— 高于上界返回 max，否则原样返回
        // 注意：本方法不检查 min 是否大于 max，也不交换；区间反了会得到意外结果，由调用方保证。
        // 上游：暂无调用（保留给坐标、速度或比例的钳制）
        // 下游：无
        public static Fix64 Clamp(Fix64 value, Fix64 min, Fix64 max)
        {
            if (value.m_raw < min.m_raw)
            {
                return min;
            }

            return value.m_raw > max.m_raw ? max : value;
        }

        /// <summary>向下取整（朝负无穷）；-1.5 得 -2。</summary>
        /// <param name="value">待取整的定点数。</param>
        /// <returns>不大于 value 的最大整数（仍是定点表示）。</returns>
        // 功能：把低 32 位小数部分抹掉，朝负无穷取整。
        // 入参：value 待取整的定点数
        // 返回：不大于 value 的最大整数，仍是 Fix64
        // 步骤：
        //   1) 原始值与 ~(2 的 32 次方 - 1) 按位与 —— 补码下抹掉低位就是朝负无穷靠
        // 注意：不能改成 raw >> 32 再给负数减 1；算术右移本身已朝负无穷，多减一步会让 floor(-1.5) 得 -3，而且不报错。
        // 上游：Round、Fix64Tests
        // 下游：私有构造
        public static Fix64 Floor(Fix64 value)
        {
            return new Fix64(value.m_raw & ~(OneRaw - 1L));
        }

        /// <summary>向上取整（朝正无穷）；-1.5 得 -1。</summary>
        // 功能：取不小于 value 的最小整数。
        // 入参：value 待取整的定点数
        // 返回：不小于 value 的最小整数，仍是 Fix64；接近上限时饱和
        // 步骤：
        //   1) 先按 Floor 的方式抹掉小数
        //   2) 没有小数部分就原样返回
        //   3) 有小数就加 1 个整数单位，加之前先判溢出并饱和
        // 注意：加 1 之前必须先判 long.MaxValue - 2 的 32 次方，否则会回绕成负数。
        // 上游：Round、Fix64Tests
        // 下游：私有构造
        public static Fix64 Ceiling(Fix64 value)
        {
            long floored = value.m_raw & ~(OneRaw - 1L);

            if (floored == value.m_raw)
            {
                return new Fix64(floored);
            }

            if (floored > long.MaxValue - OneRaw)
            {
                return MaxValue;
            }

            return new Fix64(floored + OneRaw);
        }

        /// <summary>四舍五入，远离零：0.5 得 1，-0.5 得 -1。</summary>
        // 功能：按远离零的方向四舍五入到整数。
        // 入参：value 待取整的定点数
        // 返回：最接近的整数，仍是 Fix64；正好 .5 时远离零
        // 步骤：
        //   1) 非负数先加 0.5 再 Floor —— 0.5 进位到 1
        //   2) 负数先减 0.5 再 Ceiling —— -0.5 退到 -1
        // 注意：必须写死远离零，不能用 Math.Round 的银行家舍入；两端实现细节不一致会破坏确定性。
        // 上游：FixMath.SinCos 的角度归约、测试
        // 下游：Half、Floor、Ceiling
        public static Fix64 Round(Fix64 value)
        {
            Fix64 half = Half;

            if (value.m_raw >= 0L)
            {
                return Floor(value + half);
            }

            return Ceiling(value - half);
        }

        /// <summary>线性插值 a + (b - a) * t，不钳制 t。</summary>
        /// <param name="a">起点；t 为 0 时返回它。</param>
        /// <param name="b">终点；t 为 1 时返回它。</param>
        /// <param name="t">插值系数；不钳制，超出 0..1 会外插。</param>
        /// <returns>插值结果。</returns>
        // 功能：按 t 在 a 与 b 之间线性插值。
        // 入参：a 起点；b 终点；t 系数（不钳制，0 得 a、1 得 b）
        // 返回：a + (b - a) * t
        // 步骤：
        //   1) 先算 b - a
        //   2) 乘 t 后加 a —— 顺序固定，保证两端逐位一致
        // 注意：传 2 会得到 b 之外的点；需要钳制请调用方自己先 Clamp。
        // 上游：FixVector3.Lerp
        // 下游：operator +、operator -、operator *
        public static Fix64 Lerp(Fix64 a, Fix64 b, Fix64 t)
        {
            return a + (b - a) * t;
        }

        /// <summary>平方根；负数抛 ArgumentOutOfRangeException。</summary>
        /// <param name="value">非负定点数。</param>
        /// <returns>平方根；0 返回 0，结果误差接近 1/2^32。</returns>
        // 功能：用整数开方加一次牛顿迭代求平方根。
        // 入参：value 非负定点数
        // 返回：平方根；0 返回 0
        // 步骤：
        //   1) 负数直接抛异常 —— 定点数没有虚数，这是逻辑错误
        //   2) 0 直接返回 Zero，避免后面的除法
        //   3) 初值：对原始值开整数平方根再左移 16 位 —— 这样不需要 128 位中间结果
        //   4) 一次牛顿迭代 (y + x/y) / 2 —— 误差平方级收敛，把初值的约 2^-16 压到接近定点分辨率
        // 注意：加牛顿迭代有实际理由，FixMath.Asin / Acos 走 sqrt(1 - v²)，开方误差会直接变成角度误差；代价是每次多一次乘法加一次除法。
        // 上游：FixVector3.Magnitude、FixMath.Asin / Acos、测试
        // 下游：Isqrt、operator /、operator +、私有构造
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

            Fix64 estimate = new Fix64((long)(Isqrt((ulong)value.m_raw) << 16));

            Fix64 refined = new Fix64((estimate + value / estimate).RawValue >> 1);

            return refined;
        }

        // 功能：求有符号 long 的绝对值，按无符号返回。
        // 入参：value 有符号值
        // 返回：绝对值，范围 0..2^63
        // 步骤：
        //   1) 非负直接转 ulong
        //   2) 负数先加 1 再取负再转 ulong，最后加 1 —— 绕开 long.MinValue 取负溢出的坑
        // 注意：不能直接写 (ulong)(-value)，long.MinValue 取负会溢出。
        // 上游：MulShift32、DivShift32、ToString
        // 下游：无
        private static ulong UnsignedAbs(long value)
        {
            if (value >= 0L)
            {
                return (ulong)value;
            }

            return (ulong)(-(value + 1L)) + 1UL;
        }

        // 功能：算 (a * b) >> 32，用 32 位肢拼出 128 位中间结果，溢出饱和。
        // 入参：a 左操作数原始值；b 右操作数原始值
        // 返回：结果原始值；超出 long 范围时饱和到 MinValue / MaxValue
        // 步骤：
        //   1) 任一为 0 直接返回 0
        //   2) 取绝对值并记下符号 —— 后面用无符号乘，避免中间结果溢出
        //   3) 各拆成高低 32 位，乘出 p0 到 p3 四项
        //   4) 把 p1、p2 的低位并入 lo，进位与 p3 累进 hi，得到 128 位的 hi:lo
        //   5) 若 hi 的高 32 位非 0，说明结果超过 64 位，按符号饱和
        //   6) 否则把 (hi << 32) 与 (lo >> 32) 拼成绝对值，再按符号还原
        // 注意：不能直接写 a * b >> 32，a * b 那一步就溢出；Math.BigMul 在 netstandard2.1 / Unity 里没有，只能手写。
        // 上游：operator *
        // 下游：UnsignedAbs、ApplySign
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

            ulong lo = p0;
            ulong hi = p3;

            ulong mid = (lo >> 32) + (p1 & 0xFFFFFFFFUL) + (p2 & 0xFFFFFFFFUL);
            lo = (lo & 0xFFFFFFFFUL) | (mid << 32);
            hi += (p1 >> 32) + (p2 >> 32) + (mid >> 32);

            if ((hi >> 32) != 0UL)
            {
                return negative ? long.MinValue : long.MaxValue;
            }

            ulong magnitude = (hi << 32) | (lo >> 32);

            return ApplySign(magnitude, negative);
        }

        // 功能：算 (a << 32) / b，向零截断，溢出饱和，b 为 0 抛异常。
        // 入参：a 被除数原始值；b 除数原始值（不能为 0）
        // 返回：结果原始值；商超出 64 位时饱和到 MinValue / MaxValue
        // 步骤：
        //   1) b 为 0 当场抛异常 —— 除零是逻辑错误，不做饱和
        //   2) a 为 0 直接返回 0
        //   3) 取绝对值并记下符号
        //   4) 先算整数商与余数；整数商超过 31 位时直接饱和，因为后面要左移 32 位
        //   5) 余数逐位左移，求 32 位小数 —— 标准二进制长除法，避免把被除数左移 32 位
        //   6) 整数部分与小数部分拼起来，按符号还原并饱和
        // 注意：逐位求小数要循环 32 次；除法不是最热的运算，用这点开销换确定性与不溢出是划算的。
        // 上游：operator /
        // 下游：UnsignedAbs、ApplySign
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

            if (quotient > 0x7FFFFFFFUL)
            {
                return negative ? long.MinValue : long.MaxValue;
            }

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

        // 功能：把绝对值加符号还原成有符号原始值，并做饱和检查。
        // 入参：magnitude 绝对值；negative 是否为负
        // 返回：有符号原始值
        // 步骤：
        //   1) 非负：超过 long.MaxValue 返回 MaxValue
        //   2) 负：超过 2^63 返回 MinValue
        //   3) 正好等于 2^63 返回 MinValue —— 它是 long 能表示的负数
        //   4) 其余取负
        // 注意：2^63 是 long.MinValue 的绝对值，不能直接取负；这里是唯一能正确表示它的分支。
        // 上游：MulShift32、DivShift32
        // 下游：无
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

        // 功能：整数平方根，向下取整，牛顿迭代。
        // 入参：value 无符号整数
        // 返回：floor(sqrt(value))
        // 步骤：
        //   1) 0 直接返回 0
        //   2) 用 value 的有效位数右移两位求初值，保证初值不小于真值，牛顿迭代从上方单调收敛
        //   3) 反复取 (result + value / result) / 2，直到不再变小
        //   4) 收敛后可能比真值大 1，再递减修正
        // 注意：全程整数运算，位级别确定；不要换成 Math.Sqrt，那会把浮点差异带进逻辑。
        // 上游：Sqrt
        // 下游：无
        private static ulong Isqrt(ulong value)
        {
            if (value == 0UL)
            {
                return 0UL;
            }

            ulong x = value;
            ulong result = 1UL;

            while (x > 0UL)
            {
                x >>= 2;
                result <<= 1;
            }

            while (true)
            {
                ulong next = (result + value / result) >> 1;

                if (next >= result)
                {
                    break;
                }

                result = next;
            }

            while (result > 0UL && result * result > value)
            {
                result--;
            }

            return result;
        }

        /// <summary>调试文本，保留 6 位小数；只用于日志与调试，不能参与逻辑判断。</summary>
        /// <returns>可读文本，例如 1.234567 或 -0.500000。</returns>
        // 功能：用整数运算把定点数拼成保留 6 位小数的字符串。
        // 入参：无（读自身）
        // 返回：十进制文本，固定 6 位小数；负数带前导负号
        // 步骤：
        //   1) 取绝对值并拆出整数部分与小数部分 —— 不让负号干扰取位
        //   2) 小数部分连续乘 10 取整数位，共 6 次，得到 6 位十进制小数
        //   3) 拼成 整数.小数 的文本，负数补负号；小数不足 6 位时补零
        // 注意：全程整数运算，不经过浮点，调试输出才能跨平台一致；不要拿字符串做逻辑判断。
        // 上游：日志、调试输出、FixVector3.ToString
        // 下游：UnsignedAbs
        public override string ToString()
        {
            long raw = m_raw;
            bool negative = raw < 0L;
            ulong magnitude = UnsignedAbs(raw);

            ulong integerPart = magnitude >> FractionalBits;
            ulong fraction = magnitude & (OneRaw - 1UL);

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
