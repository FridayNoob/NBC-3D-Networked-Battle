// ============================================================================
//  NBC.Shared.FixMath —— 确定性三角函数 / 反三角函数
//  需求依据：FW-M16、§6.4 DET-01（确定性数学）
//
//  ---------------------------------------------------------------------------
//  算法：CORDIC（纯整数移位 + 加法）
//  ---------------------------------------------------------------------------
//  CORDIC 的核心思想：**用"旋转一串预先算好的固定角度"来逼近目标角度**，
//  而这些角度的正切值恰好是 2 的幂 —— 于是"乘以 tan"退化成**移位**，不需要乘法器。
//
//  · 只需要 **32 个 `atan(2^-i)` 常量**（见文件末尾的表）
//  · 全程整数移位与加减，**没有一次浮点**
//  · 迭代 32 次，角度残差约 `atan(2^-32) ≈ 2.3e-10`
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么不用"查表 + 插值"，也不用"运行时生成表"
//  ---------------------------------------------------------------------------
//  **运行时生成表是绝对不行的**：如果表是用 `Math.Sin` 在启动时算出来的，
//  那么服务端（.NET）和客户端（Mono/IL2CPP）的 BCL 实现**可能不同**，
//  两端就会得到**不同的表** —— 那正好毁掉帧同步，而且**几乎不可能查出来**。
//
//  硬编码表也可以，但 CORDIC 更省：**32 个常量** vs 几百上千个采样值，
//  而且精度更高（迭代到 2^-32，查表+线性插值的误差在 1e-7 量级）。
//
//  ⚠️ 硬编码常量是**离线生成**的（`Math.PI` / `Math.Atan` 各算一次，然后写死）。
//     生成器在 `.dsh-tmp/fixprobe`，不参与构建。
//
//  ---------------------------------------------------------------------------
//  单位：**弧度**
//  ---------------------------------------------------------------------------
//  对外接口收的是 `Fix64` 弧度。要角度请用 `DegToRad` / `SinDeg` 这类辅助。
//  ⚠️ 不用角度当默认单位，是因为物理公式（角速度、角加速度）本来就在弧度下。
// ============================================================================

using System;

namespace NBC.Shared
{
    /// <summary>
    /// 确定性数学函数。**纯整数运算，位级别可复现**。
    /// </summary>
    public static class FixMath
    {
        /// <summary>π/2 的原始值（离线生成后硬编码）。</summary>
        public const long HalfPiRaw = 6746518852L;

        /// <summary>CORDIC 增益 K = ∏ 1/sqrt(1 + 2^-2i) ≈ 0.6072529350088814（离线生成）。</summary>
        public const long CordicGainRaw = 2608131496L;

        /// <summary>CORDIC 迭代次数（32 次，角度残差约 2.3e-10）。</summary>
        public const int CordicIterations = 32;

        /// <summary>π。</summary>
        public static readonly Fix64 Pi = Fix64.Pi;

        /// <summary>π/2。</summary>
        public static readonly Fix64 HalfPi = Fix64.FromRaw(HalfPiRaw);

        /// <summary>
        /// 2π。
        /// <para>
        /// ⚠️ 定义为 `Pi * 2` 而不是单独取整的一个常量 ——
        /// 两个"本该相关"的常量各自独立取整，就会**不一致**，
        /// 这种不一致将来一定会咬人（比如 360° 归约不到位）。
        /// </para>
        /// </summary>
        public static readonly Fix64 TwoPi = Fix64.FromRaw(Fix64.Pi.RawValue * 2L);

        /// <summary>圆周率的一半的相反数（少写几个负号）。</summary>
        public static readonly Fix64 MinusHalfPi = -HalfPi;

        // ====================================================================
        //  角度 / 弧度换算
        // ====================================================================

        /// <summary>角度 → 弧度。</summary>
        /// <param name="degrees">角度。</param>
        /// <returns>弧度。</returns>
        public static Fix64 DegToRad(Fix64 degrees)
        {
            return degrees * Pi / Fix64.FromInt(180);
        }

        /// <summary>弧度 → 角度。</summary>
        /// <param name="radians">弧度。</param>
        /// <returns>角度。</returns>
        public static Fix64 RadToDeg(Fix64 radians)
        {
            return radians * Fix64.FromInt(180) / Pi;
        }

        /// <summary>角度的正弦。</summary>
        /// <param name="degrees">角度。</param>
        /// <returns>正弦值。</returns>
        public static Fix64 SinDeg(Fix64 degrees)
        {
            return Sin(DegToRad(degrees));
        }

        /// <summary>角度的余弦。</summary>
        /// <param name="degrees">角度。</param>
        /// <returns>余弦值。</returns>
        public static Fix64 CosDeg(Fix64 degrees)
        {
            return Cos(DegToRad(degrees));
        }

        // ====================================================================
        //  三角
        // ====================================================================

        /// <summary>正弦。</summary>
        /// <param name="radians">弧度。</param>
        /// <returns>正弦值。</returns>
        public static Fix64 Sin(Fix64 radians)
        {
            Fix64 cos;
            Fix64 sin;

            SinCos(radians, out cos, out sin);
            return sin;
        }

        /// <summary>余弦。</summary>
        /// <param name="radians">弧度。</param>
        /// <returns>余弦值。</returns>
        public static Fix64 Cos(Fix64 radians)
        {
            Fix64 cos;
            Fix64 sin;

            SinCos(radians, out cos, out sin);
            return cos;
        }

        /// <summary>
        /// 同时求正弦余弦（**只跑一次 CORDIC** —— 需要两个值时用它，省一半）。
        /// </summary>
        /// <param name="radians">弧度。</param>
        /// <param name="cos">余弦值。</param>
        /// <param name="sin">正弦值。</param>
        public static void SinCos(Fix64 radians, out Fix64 cos, out Fix64 sin)
        {
            // ⓪ 精确快路径：0 弧度在数学上是**精确**的（cos=1, sin=0），
            //    不该让 CORDIC 的固有残差（约 1e-9）污染它。
            //    否则 `Cos(0) == 1` / `Sin(0) == 0` 这种恒等式会失效 ——
            //    而"没有旋转"是游戏里最常见的路径之一。
            if (radians.IsZero)
            {
                cos = Fix64.One;
                sin = Fix64.Zero;
                return;
            }

            // ① 把角度归约到 [-π, π]：turns 是最接近的整圈数
            Fix64 turns = Fix64.Round(radians / TwoPi);
            Fix64 reduced = radians - turns * TwoPi;

            // ② 取象限，把角度进一步压到 [-π/4, π/4] —— CORDIC 在这个区间最准最快
            int quadrant = Fix64.Round(reduced / HalfPi).ToInt();
            Fix64 r = reduced - Fix64.FromInt(quadrant) * HalfPi;

            // ③ CORDIC 旋转
            Fix64 c;
            Fix64 s;

            CordicRotate(r, out c, out s);

            // ④ 按象限映射回去
            switch (((quadrant % 4) + 4) % 4)
            {
                case 0:
                    cos = c;
                    sin = s;
                    return;

                case 1:
                    cos = -s;
                    sin = c;
                    return;

                case 2:
                    cos = -c;
                    sin = -s;
                    return;

                default:
                    cos = s;
                    sin = -c;
                    return;
            }
        }

        /// <summary>
        /// 正切。
        /// <para>
        /// ⚠️ `cos` 为 0 时**会抛 `DivideByZeroException`**；
        /// 接近 π/2 时结果会非常大（那是数学事实，不是 bug）。
        /// </para>
        /// </summary>
        /// <param name="radians">弧度。</param>
        /// <returns>正切值。</returns>
        public static Fix64 Tan(Fix64 radians)
        {
            Fix64 cos;
            Fix64 sin;

            SinCos(radians, out cos, out sin);

            return sin / cos;
        }

        // ====================================================================
        //  反三角
        // ====================================================================

        /// <summary>
        /// 反正切（结果在 `(-π/2, π/2)`）。
        /// </summary>
        /// <param name="value">值。</param>
        /// <returns>弧度。</returns>
        public static Fix64 Atan(Fix64 value)
        {
            return Atan2(value, Fix64.One);
        }

        /// <summary>
        /// 双参数反正切 —— **游戏里最常用的那个**（"朝向目标要转多少度"就是它）。
        /// <para>
        /// ⚠️ **`Atan2(0, 0)` 约定返回 0**（而不是抛异常）。
        /// 因为"没有方向向量"在游戏里是正常情况；抛异常会逼每个调用点都判空。
        /// </para>
        /// <para>结果范围 `(-π, π]`。</para>
        /// </summary>
        /// <param name="y">Y 分量。</param>
        /// <param name="x">X 分量。</param>
        /// <returns>弧度。</returns>
        public static Fix64 Atan2(Fix64 y, Fix64 x)
        {
            // 退化情况：先处理掉
            if (x.IsZero)
            {
                if (y.IsZero)
                {
                    return Fix64.Zero;   // 约定：没有方向就是 0
                }

                return y.IsPositive ? HalfPi : MinusHalfPi;
            }

            // ⓪ 坐标轴上的精确快路径。
            //    ⚠️ 不能省：CORDIC 向量模式在 `y == 0` 时会在零点附近**来回振荡**
            //    （y 恰好为 0 时走 else 分支反而把 y 推成非零），
            //    结果是 `Atan2(0, 1)` 给出约 1e-9 而不是精确的 0。
            //    而"正右方/正左方"是最常见的方向之一，值得精确。
            if (y.IsZero)
            {
                return x.IsPositive ? Fix64.Zero : Pi;
            }

            bool negativeX = x.IsNegative;
            bool negativeY = y.IsNegative;

            // 只算第一象限：atan(|y| / |x|) ∈ [0, π/2]
            Fix64 a = CordicVector(Fix64.Abs(x), Fix64.Abs(y));

            if (!negativeX)
            {
                return negativeY ? -a : a;
            }

            return negativeY ? (a - Pi) : (Pi - a);
        }

        /// <summary>
        /// 反正弦。**要求 |value| ≤ 1**，否则抛异常。
        /// </summary>
        /// <param name="value">值。</param>
        /// <returns>弧度，范围 `[-π/2, π/2]`。</returns>
        public static Fix64 Asin(Fix64 value)
        {
            RequireUnitDomain(value, nameof(Asin));

            // asin(v) = atan2(v, sqrt(1 - v²))
            Fix64 root = Fix64.Sqrt(Fix64.One - value * value);

            return Atan2(value, root);
        }

        /// <summary>
        /// 反余弦。**要求 |value| ≤ 1**，否则抛异常。
        /// </summary>
        /// <param name="value">值。</param>
        /// <returns>弧度，范围 `[0, π]`。</returns>
        public static Fix64 Acos(Fix64 value)
        {
            RequireUnitDomain(value, nameof(Acos));

            // acos(v) = atan2(sqrt(1 - v²), v)
            Fix64 root = Fix64.Sqrt(Fix64.One - value * value);

            return Atan2(root, value);
        }

        // ====================================================================
        //  CORDIC 内核
        // ====================================================================

        /// <summary>
        /// CORDIC **旋转模式**：已知角度，求 cos / sin。
        /// <para>⚠️ 前提：`|angle| ≤ π/4`（调用方 <see cref="SinCos"/> 负责归约）。</para>
        /// </summary>
        /// <param name="angle">角度（弧度）。</param>
        /// <param name="cos">余弦。</param>
        /// <param name="sin">正弦。</param>
        private static void CordicRotate(Fix64 angle, out Fix64 cos, out Fix64 sin)
        {
            // 初值取 (K, 0)：K 是 CORDIC 旋转的固定增益的倒数，
            // 先乘掉它，最后得到的就直接是单位向量（不必每步再校正）
            Fix64 x = Fix64.FromRaw(CordicGainRaw);
            Fix64 y = Fix64.Zero;
            Fix64 z = angle;

            for (int i = 0; i < CordicIterations; i++)
            {
                Fix64 xShift = ShiftRight(x, i);
                Fix64 yShift = ShiftRight(y, i);
                Fix64 step = Fix64.FromRaw(AtanTableRaw[i]);

                if (z.RawValue >= 0)
                {
                    x = x - yShift;
                    y = y + xShift;
                    z = z - step;
                }
                else
                {
                    x = x + yShift;
                    y = y - xShift;
                    z = z + step;
                }
            }

            cos = x;
            sin = y;
        }

        /// <summary>
        /// CORDIC **向量模式**：已知向量，求它的角度（atan）。
        /// <para>传入的 `x` 必须为正（调用方已取绝对值）。返回 `atan(y/x)`，范围 `[0, π/2]`。</para>
        /// <para>
        /// ⚠️ **z 的符号要盯紧**（这里踩过一次）：旋转 `-atan(2^-i)` 用的是
        /// `x' = x + y·2^-i`、`y' = y - x·2^-i`，即**顺时针**转；
        /// 而 `z` 记的是"累计转过的角度"，所以顺时针时 `z` 要**加** `atan(2^-i)`
        /// ——累加的是**角度本身**，不是旋转量的负值。
        /// 写反的话，`Atan2` 会在"接近垂直"的向量上给出**符号相反**的结果（差 π）。
        /// </para>
        /// </summary>
        /// <param name="x">X 分量（正数）。</param>
        /// <param name="y">Y 分量。</param>
        /// <returns>弧度。</returns>
        private static Fix64 CordicVector(Fix64 x, Fix64 y)
        {
            Fix64 z = Fix64.Zero;

            for (int i = 0; i < CordicIterations; i++)
            {
                Fix64 xShift = ShiftRight(x, i);
                Fix64 yShift = ShiftRight(y, i);
                Fix64 step = Fix64.FromRaw(AtanTableRaw[i]);

                // 把向量转到正 X 轴上：
                //   y > 0 → 顺时针转（x' = x + y·2^-i），累计角度 +atan
                //   y < 0 → 逆时针转（x' = x - y·2^-i），累计角度 -atan
                if (y.RawValue > 0)
                {
                    x = x + yShift;
                    y = y - xShift;
                    z = z + step;
                }
                else
                {
                    x = x - yShift;
                    y = y + xShift;
                    z = z - step;
                }
            }

            return z;
        }

        /// <summary>
        /// 定点数右移（即乘 2^-bits）。
        /// <para>
        /// ⚠️ 对负数是**算术右移**（朝负无穷取整），会引入不到 1 个 raw 单位的截断误差 ——
        /// 迭代 32 次后累积仍在可接受范围，探针会把总量出来。
        /// </para>
        /// </summary>
        /// <param name="value">值。</param>
        /// <param name="bits">移多少位。</param>
        /// <returns>结果。</returns>
        private static Fix64 ShiftRight(Fix64 value, int bits)
        {
            return Fix64.FromRaw(value.RawValue >> bits);
        }

        /// <summary>检查定义域是否是 [-1, 1]。</summary>
        /// <param name="value">值。</param>
        /// <param name="name">函数名（报错用）。</param>
        private static void RequireUnitDomain(Fix64 value, string name)
        {
            if (value > Fix64.One || value < Fix64.MinusOne)
            {
                throw new ArgumentOutOfRangeException(
                    name, value.ToDouble(),
                    "[FixMath." + name + "] 定义域是 [-1, 1]，传进来的是 " + value + "。");
            }
        }

        // ====================================================================
        //  CORDIC 常量表（**离线生成后硬编码**，运行时不许再算）
        // ====================================================================
        //
        //  atan(2^-i)，i = 0..31。
        //  ⚠️ 从 i = 12 起，atan(2^-i) 与 2^-i 在 Fix64 精度下已经**没有区别**
        //     （atan(x) ≈ x - x³/3，三次项低于 2^-32），所以后半段就是 2 的幂。
        //     这不是"表退化了"，而是数学事实。
        private static readonly long[] AtanTableRaw =
        {
            3373259426L,   // atan(2^0)  = 0.7853981633974483
            1991351318L,   // atan(2^-1) = 0.4636476090008061
            1052175346L,   // atan(2^-2) = 0.24497866312686414
            534100635L,    // atan(2^-3) = 0.12435499454676144
            268086748L,    // atan(2^-4) = 0.06241880999595735
            134174063L,    // atan(2^-5) = 0.031239833430268277
            67103403L,     // atan(2^-6) = 0.015623728620476831
            33553749L,     // atan(2^-7) = 0.007812341060101111
            16777131L,     // atan(2^-8) = 0.0039062301319669718
            8388597L,      // atan(2^-9) = 0.0019531225164788188
            4194303L,      // atan(2^-10) = 0.0009765621895593195
            2097152L,      // atan(2^-11) = 0.0004882812111948983
            1048576L,      // atan(2^-12) = 0.00024414062014936177
            524288L,       // atan(2^-13) = 0.00012207031189367021
            262144L,       // atan(2^-14) = 6.103515617420877E-05
            131072L,       // atan(2^-15) = 3.0517578115526096E-05
            65536L,        // atan(2^-16) = 1.5258789061315762E-05
            32768L,        // atan(2^-17) = 7.62939453110197E-06
            16384L,        // atan(2^-18) = 3.814697265606496E-06
            8192L,         // atan(2^-19) = 1.907348632810187E-06
            4096L,         // atan(2^-20) = 9.536743164059608E-07
            2048L,         // atan(2^-21) = 4.7683715820308884E-07
            1024L,         // atan(2^-22) = 2.3841857910155797E-07
            512L,          // atan(2^-23) = 1.1920928955078068E-07
            256L,          // atan(2^-24) = 5.960464477539055E-08
            128L,          // atan(2^-25) = 2.9802322387695303E-08
            64L,           // atan(2^-26) = 1.4901161193847655E-08
            32L,           // atan(2^-27) = 7.450580596923828E-09
            16L,           // atan(2^-28) = 3.725290298461914E-09
            8L,            // atan(2^-29) = 1.862645149230957E-09
            4L,            // atan(2^-30) = 9.313225746154785E-10
            2L,            // atan(2^-31) = 4.656612873077393E-10
        };
    }
}
