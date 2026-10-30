// 这个文件里有：
//   FixMath（静态类）：CORDIC 定点三角函数与反三角函数，纯整数移位与加减，位级别可复现。

using System;

namespace NBC.Shared
{
    // 主要功能：用 CORDIC 求正弦、余弦、正切与反三角，全程整数移位与加减，没有一次浮点，两端逐位相同。
    // 主要数据成员：AtanTableRaw（atan(2^-i) 的定点表，单位弧度，i = 0..31）、CordicIterations（迭代次数，单位次，固定 32）、CordicGainRaw 与 HalfPiRaw（CORDIC 增益与 π/2 的 raw 值）、Pi / HalfPi / TwoPi / MinusHalfPi（常用弧度常量）。
    // 主要外部方法：Sin / Cos / SinCos / Tan / Atan / Atan2 / Asin / Acos / DegToRad / RadToDeg / SinDeg / CosDeg。
    // 谁在用它：Unity 侧 Tests\EditMode\Framework 的 FixMathTests、服务端 Server\_net-probe 的定点数学自检直接调用；本 Demo 的 Sim 与 Game 层目前没有调用点。
    // 注意：角度一律用弧度（单位 rad）；手上是角度就先转（DegToRad），或直接走 SinDeg / CosDeg。
    // 注意：atan 表必须离线生成后硬编码 —— 用 Math.Sin / Math.Atan 在启动时现算，服务端 .NET 与客户端 Mono / IL2CPP 的 BCL 实现可能不同，两端会拿到不同的表，帧同步就此坏掉而且几乎查不出来。
    // 注意：所以既不用「查表 + 插值」，也不在运行时生成表；硬编码 32 个常量更省也更准（迭代到 2^-32，查表插值的误差在 1e-7 量级）。
    /// <summary>确定性数学函数：纯整数运算，位级别可复现。</summary>
    public static class FixMath
    {
        /// <summary>π/2 的 raw 值（Q32.32 原始值），单位弧度；取值 6746518852，离线生成后硬编码。</summary>
        public const long HalfPiRaw = 6746518852L;

        /// <summary>CORDIC 增益 K = ∏ 1/sqrt(1 + 2^-2i) ≈ 0.6072529350088814 的 raw 值；取值 2608131496，无单位，用作旋转前的初值缩放。</summary>
        public const long CordicGainRaw = 2608131496L;

        /// <summary>CORDIC 迭代次数，单位次；固定 32，角度残差约 atan(2^-32) ≈ 2.3e-10 弧度，改小掉精度、改大在 Fix64 精度下没有收益。</summary>
        public const int CordicIterations = 32;

        /// <summary>π 的定点值，单位弧度；直接取 Fix64.Pi，不要另写一份。</summary>
        public static readonly Fix64 Pi = Fix64.Pi;

        /// <summary>π/2 的定点值，单位弧度；由 HalfPiRaw 还原，与常量表同源。</summary>
        public static readonly Fix64 HalfPi = Fix64.FromRaw(HalfPiRaw);

        /// <summary>2π 的定点值，单位弧度；按 Pi 的 raw 值乘 2 定义，而不是单独取整 —— 两个本该相关的常量各自取整就会不一致，360° 归约不到位这类坑由此而来。</summary>
        public static readonly Fix64 TwoPi = Fix64.FromRaw(Fix64.Pi.RawValue * 2L);

        /// <summary>−π/2 的定点值，单位弧度；由 HalfPi 取负，只为少写几个负号。</summary>
        public static readonly Fix64 MinusHalfPi = -HalfPi;

        // 功能：把角度乘 π 再除以 180，换成定点弧度。
        // 入参：degrees 角度（单位度，任意大小，不先归约）
        // 返回：弧度（单位 rad）
        // 步骤：
        //   1) 先乘 π 再除 180 —— 反过来先除会把很小的角度直接截成 0，精度损失更大
        // 注意：乘积会随角度变大而线性变大，角度到几亿度量级时 Fix64 乘法会饱和，结果不再等于真实换算；正常游戏角度远达不到。
        // 上游：SinDeg、CosDeg，以及把配置里的角度转成弧度的调用点
        // 下游：Pi
        /// <summary>角度换算成弧度（度 × π / 180）。</summary>
        /// <param name="degrees">角度，单位度。</param>
        /// <returns>弧度，单位 rad。</returns>
        public static Fix64 DegToRad(Fix64 degrees)
        {
            return degrees * Pi / Fix64.FromInt(180);
        }

        // 功能：把弧度乘 180 再除以 π，换成角度。
        // 入参：radians 弧度（单位 rad，任意大小）
        // 返回：角度（单位度）
        // 步骤：
        //   1) 先乘 180 再除 π —— 与 DegToRad 同理，先除会先丢精度
        // 上游：需要把内部弧度显示成角度的调用点
        // 下游：Pi
        /// <summary>弧度换算成角度（弧度 × 180 / π）。</summary>
        /// <param name="radians">弧度，单位 rad。</param>
        /// <returns>角度，单位度。</returns>
        public static Fix64 RadToDeg(Fix64 radians)
        {
            return radians * Fix64.FromInt(180) / Pi;
        }

        // 功能：把角度换成弧度后求正弦。
        /// <summary>角度制正弦：先转弧度再走 Sin。</summary>
        /// <param name="degrees">角度，单位度。</param>
        /// <returns>正弦值，范围 -1..1；0 度是精确的 0。</returns>
        public static Fix64 SinDeg(Fix64 degrees)
        {
            return Sin(DegToRad(degrees));
        }

        // 功能：把角度换成弧度后求余弦。
        /// <summary>角度制余弦：先转弧度再走 Cos。</summary>
        /// <param name="degrees">角度，单位度。</param>
        /// <returns>余弦值，范围 -1..1；0 度是精确的 1。</returns>
        public static Fix64 CosDeg(Fix64 degrees)
        {
            return Cos(DegToRad(degrees));
        }

        // 功能：求正弦，实际转给 SinCos 只跑一次 CORDIC。
        // 入参：radians 弧度（单位 rad，任意大小，内部会归约到 [-π, π]）
        // 返回：正弦值（范围 -1..1，定点精度内误差约 1e-9；0 弧度是精确的 0）
        // 步骤：
        //   1) 转给 SinCos 一次算出余弦与正弦 —— 想要两个值时分两次调会把 CORDIC 白跑一遍
        // 上游：SinDeg；同层需要正弦的调用点
        // 下游：SinCos
        /// <summary>定点正弦，入参弧度。</summary>
        /// <param name="radians">弧度，单位 rad。</param>
        /// <returns>正弦值，范围 -1..1；0 弧度返回精确的 0。</returns>
        public static Fix64 Sin(Fix64 radians)
        {
            Fix64 cos;
            Fix64 sin;

            SinCos(radians, out cos, out sin);
            return sin;
        }

        // 功能：求余弦，实际转给 SinCos 只跑一次 CORDIC。
        // 入参：radians 弧度（单位 rad，任意大小，内部会归约到 [-π, π]）
        // 返回：余弦值（范围 -1..1，定点精度内误差约 1e-9；0 弧度是精确的 1）
        // 步骤：
        //   1) 转给 SinCos 一次算出余弦与正弦 —— 想要两个值时分两次调会把 CORDIC 白跑一遍
        // 上游：CosDeg；同层需要余弦的调用点
        // 下游：SinCos
        /// <summary>定点余弦，入参弧度。</summary>
        /// <param name="radians">弧度，单位 rad。</param>
        /// <returns>余弦值，范围 -1..1；0 弧度返回精确的 1。</returns>
        public static Fix64 Cos(Fix64 radians)
        {
            Fix64 cos;
            Fix64 sin;

            SinCos(radians, out cos, out sin);
            return cos;
        }

        // 功能：一次 CORDIC 同时求出余弦与正弦，需要两个值时用它，省一半迭代。
        // 入参：radians 弧度（单位 rad，任意大小，内部先归约到 [-π, π]，再压到 [-π/4, π/4]）
        // 返回：无；结果从 cos 与 sin 两个 out 参数带出，范围都是 -1..1
        // 步骤：
        //   1) 0 弧度直接给 (1, 0) —— 数学上精确，不让 CORDIC 约 1e-9 的固有残差污染 Cos(0) 与 Sin(0)，而「没有旋转」是最常见的路径之一
        //   2) 减去最接近的整圈数，把角度归约到 [-π, π] —— 大角度直接喂给 CORDIC 会掉精度
        //   3) 按 π/2 取象限，把剩余角度压到 [-π/4, π/4] —— CORDIC 在这个区间最准最快
        //   4) 跑 CordicRotate 拿单位向量
        //   5) 按象限把分量交换 / 取负映射回去
        // 注意：象限下标用 ((quadrant % 4) + 4) % 4 归一化 —— C# 里负数取模仍为负，少了这一层，负角度会落进 default 分支，sin 与 cos 直接对调。
        // 上游：Sin / Cos / Tan
        // 下游：CordicRotate
        /// <summary>一次 CORDIC 同时求余弦与正弦，需要两个值时用它，省一半迭代。</summary>
        /// <param name="radians">弧度，单位 rad。</param>
        /// <param name="cos">输出余弦，范围 -1..1。</param>
        /// <param name="sin">输出正弦，范围 -1..1。</param>
        public static void SinCos(Fix64 radians, out Fix64 cos, out Fix64 sin)
        {
            if (radians.IsZero)
            {
                cos = Fix64.One;
                sin = Fix64.Zero;
                return;
            }

            Fix64 turns = Fix64.Round(radians / TwoPi);
            Fix64 reduced = radians - turns * TwoPi;

            int quadrant = Fix64.Round(reduced / HalfPi).ToInt();
            Fix64 r = reduced - Fix64.FromInt(quadrant) * HalfPi;

            Fix64 c;
            Fix64 s;

            CordicRotate(r, out c, out s);

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

        // 功能：用 sin 除以 cos 求正切。
        // 入参：radians 弧度（单位 rad，任意大小）
        // 返回：正切值（无界；接近 ±π/2 时数值非常大）
        // 步骤：
        //   1) 一次 SinCos 同时拿到 cos 与 sin
        //   2) 直接相除 —— Fix64 除零会抛异常，本方法不兜底
        // 注意：cos 恰为 0 时会抛 DivideByZeroException；接近 π/2 时结果极大是数学事实，不是精度 bug。
        // 上游：需要正切的调用点
        // 下游：SinCos
        /// <summary>定点正切；cos 为 0 时抛 DivideByZeroException。</summary>
        /// <param name="radians">弧度，单位 rad。</param>
        /// <returns>正切值，无界；接近 ±π/2 时非常大。</returns>
        public static Fix64 Tan(Fix64 radians)
        {
            Fix64 cos;
            Fix64 sin;

            SinCos(radians, out cos, out sin);

            return sin / cos;
        }

        // 功能：求反正切，把 x 当成 1 转给 Atan2。
        /// <summary>反正切（结果在 -π/2 到 π/2 之间），等价于 Atan2(value, 1)。</summary>
        /// <param name="value">正切值，无单位。</param>
        /// <returns>弧度，单位 rad，范围 (-π/2, π/2)。</returns>
        public static Fix64 Atan(Fix64 value)
        {
            return Atan2(value, Fix64.One);
        }

        // 功能：按 (y, x) 求方向角，覆盖四个象限与坐标轴上的退化情况。
        // 入参：y 与 x 方向分量（无单位，只要求同单位；两者都为 0 时返回 0）
        // 返回：弧度（单位 rad，范围 (-π, π]；正右方是精确的 0，正左方是 π）
        // 步骤：
        //   1) x 为 0 时向量模式无法旋转，直接按 y 的符号给 ±π/2；y 也为 0 时按约定给 0，不抛异常（游戏里「没有方向」是正常情况，抛异常会逼每个调用点都判空）
        //   2) y 为 0 单独给精确值 —— CORDIC 向量模式在 y 恰为 0 时会来回振荡，走下面的分支反而把 y 推成非零
        //   3) 记下两个符号后取绝对值，只算第一象限的 atan(abs(y) / abs(x))，落在 [0, π/2]
        //   4) 按符号映射回四个象限：x < 0 用 π 补角，y < 0 再取负
        // 注意：第 2 步不能省 —— 少了它，Atan2(0, 1) 会给出约 1e-9 而不是精确的 0；而「正右方 / 正左方」是最常见的方向之一，残差会让断言与哈希抖动。
        // 上游：Atan / Asin / Acos，以及任何算朝向的地方
        // 下游：CordicVector、Pi、HalfPi、MinusHalfPi
        /// <summary>双参数反正切：给出 (y, x) 方向的角度，Atan2(0, 0) 约定返回 0。</summary>
        /// <param name="y">Y 分量，无单位（与 x 同单位即可）。</param>
        /// <param name="x">X 分量，无单位（与 y 同单位即可）。</param>
        /// <returns>弧度，单位 rad，范围 (-π, π]；Atan2(0, 0) 返回 0。</returns>
        public static Fix64 Atan2(Fix64 y, Fix64 x)
        {
            if (x.IsZero)
            {
                if (y.IsZero)
                {
                    return Fix64.Zero;
                }

                return y.IsPositive ? HalfPi : MinusHalfPi;
            }

            if (y.IsZero)
            {
                return x.IsPositive ? Fix64.Zero : Pi;
            }

            bool negativeX = x.IsNegative;
            bool negativeY = y.IsNegative;

            Fix64 a = CordicVector(Fix64.Abs(x), Fix64.Abs(y));

            if (!negativeX)
            {
                return negativeY ? -a : a;
            }

            return negativeY ? (a - Pi) : (Pi - a);
        }

        // 功能：用 atan2(v, sqrt(1 - v²)) 求反正弦。
        // 入参：value 正弦值（范围 -1..1；越界直接抛，不做钳位）
        // 返回：弧度（单位 rad，范围 [-π/2, π/2]）
        // 步骤：
        //   1) 先校验 value 的绝对值 ≤ 1 —— 越界时 sqrt 会拿到负数，静默钳位会把调用方的错藏起来
        //   2) 算 sqrt(1 - v²) 当邻边，再交给 Atan2 —— 端点 ±1 处邻边为 0，正好走 Atan2 的坐标轴精确路径
        // 注意：开方误差会直接变成角度误差 —— 本式把 Fix64.Sqrt 的精度当成了上限，端点 ±1 附近邻边趋近 0，最敏感。
        // 上游：需要由正弦反推角度的调用点
        // 下游：RequireUnitDomain、Fix64.Sqrt、Atan2
        /// <summary>反正弦；要求 value 的绝对值 ≤ 1，越界抛 ArgumentOutOfRangeException。</summary>
        /// <param name="value">正弦值，取值范围 -1..1（越界抛异常）。</param>
        /// <returns>弧度，单位 rad，范围 [-π/2, π/2]。</returns>
        public static Fix64 Asin(Fix64 value)
        {
            RequireUnitDomain(value, nameof(Asin));

            Fix64 root = Fix64.Sqrt(Fix64.One - value * value);

            return Atan2(value, root);
        }

        // 功能：用 atan2(sqrt(1 - v²), v) 求反余弦。
        // 入参：value 余弦值（范围 -1..1；越界直接抛，不做钳位）
        // 返回：弧度（单位 rad，范围 [0, π]）
        // 步骤：
        //   1) 先校验 value 的绝对值 ≤ 1 —— 越界时 sqrt 会拿到负数，静默钳位会把调用方的错藏起来
        //   2) 算 sqrt(1 - v²) 当对边，再交给 Atan2 —— 与 Asin 只差两个参数的顺序
        // 注意：开方误差会直接变成角度误差 —— 本式把 Fix64.Sqrt 的精度当成了上限，端点 ±1 附近邻边趋近 0，最敏感。
        // 上游：需要由余弦反推角度的调用点
        // 下游：RequireUnitDomain、Fix64.Sqrt、Atan2
        /// <summary>反余弦；要求 value 的绝对值 ≤ 1，越界抛 ArgumentOutOfRangeException。</summary>
        /// <param name="value">余弦值，取值范围 -1..1（越界抛异常）。</param>
        /// <returns>弧度，单位 rad，范围 [0, π]。</returns>
        public static Fix64 Acos(Fix64 value)
        {
            RequireUnitDomain(value, nameof(Acos));

            Fix64 root = Fix64.Sqrt(Fix64.One - value * value);

            return Atan2(root, value);
        }

        // 功能：已知角度，用旋转模式跑 32 次 CORDIC，得到余弦与正弦。
        // 入参：angle 角度（单位 rad；前提是 angle 的绝对值 ≤ π/4，归约由调用方 SinCos 负责）
        // 返回：无；cos 与 sin 两个 out 参数带出单位向量分量，范围 -1..1
        // 步骤：
        //   1) 初值取 (K, 0) —— K 是旋转增益的倒数，先乘掉它，最后得到的就是单位向量，不必每步再校正
        //   2) 每轮按剩余角度 z 的符号决定转正还是转负；旋转的乘数正好是 tan(atan(2^-i)) = 2^-i，于是乘法退化成右移 i 位，z 同步减去 / 加上 atan(2^-i)
        //   3) 角度残差每轮至少减半，32 轮后约 2.3e-10
        // 注意：前提是 angle 的绝对值 ≤ π/4，归约由 SinCos 保证；绕开 SinCos 直接传大角度会掉精度，象限映射也对不上。
        // 上游：SinCos 归约之后
        // 下游：ShiftRight、AtanTableRaw
        private static void CordicRotate(Fix64 angle, out Fix64 cos, out Fix64 sin)
        {
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

        // 功能：已知向量，用向量模式跑 32 次 CORDIC，求出 atan(y / x)。
        // 入参：x 的 X 分量（无单位；必须为正，调用方已取绝对值）；y 的 Y 分量（无单位）
        // 返回：弧度（单位 rad，范围 [0, π/2]，即 atan(y / x)）
        // 步骤：
        //   1) 每轮按 y 的符号决定把向量往正 X 轴转的方向，同时累积转过的角度 z
        //   2) y > 0 时顺时针转（x' = x + y·2^-i，y' = y - x·2^-i），z 加 atan(2^-i)
        //   3) y ≤ 0 时逆时针转，z 减 atan(2^-i)
        // 注意：z 的符号要盯紧 —— 顺时针转时 z 加的是 atan(2^-i)（记的是累计转角本身，不是旋转量的负值）；写反后 Atan2 会在接近垂直的向量上给出符号相反的结果（差 π）。
        // 注意：x 必须为正；x 为 0 时没有正 X 轴可转，必须由 Atan2 在第 1 步先拦掉。
        // 上游：Atan2（只传 abs(x) 且 y ≠ 0）
        // 下游：ShiftRight、AtanTableRaw
        private static Fix64 CordicVector(Fix64 x, Fix64 y)
        {
            Fix64 z = Fix64.Zero;

            for (int i = 0; i < CordicIterations; i++)
            {
                Fix64 xShift = ShiftRight(x, i);
                Fix64 yShift = ShiftRight(y, i);
                Fix64 step = Fix64.FromRaw(AtanTableRaw[i]);

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

        // 功能：把 raw 值右移 bits 位，等价于乘 2^-bits，给 CORDIC 当移位乘法用。
        // 注意：对负数是算术右移（朝负无穷取整），每步引入不到 1 个 raw 单位的截断误差；32 轮累积后仍在可接受范围，探针会把总量量出来。
        // 上游：CordicRotate、CordicVector
        private static Fix64 ShiftRight(Fix64 value, int bits)
        {
            return Fix64.FromRaw(value.RawValue >> bits);
        }

        // 功能：校验定义域是否落在 [-1, 1]，越界抛 ArgumentOutOfRangeException。
        // 入参：value 待校验的值（无单位）；name 函数名（拼进报错文案，调用方用 nameof 传入）
        // 返回：无；越界就抛
        // 步骤：
        //   1) 直接比上下界，不做钳位 —— 钳位会把调用方的错藏成悬案
        //   2) 报错带上函数名与实际值 —— 只说越界无法定位是哪个调用点
        // 注意：这里一律抛，不静默修正 —— Asin / Acos 的定义域是数学事实，放行越界值只会让错误在下游以更难查的形式出现。
        // 上游：Asin、Acos
        // 下游：无
        private static void RequireUnitDomain(Fix64 value, string name)
        {
            if (value > Fix64.One || value < Fix64.MinusOne)
            {
                throw new ArgumentOutOfRangeException(
                    name, value.ToDouble(),
                    "[FixMath." + name + "] 定义域是 [-1, 1]，传进来的是 " + value + "。");
            }
        }

        // atan(2^-i) 的定点表，单位弧度，i = 0..31 共 32 项；离线生成后硬编码，运行时不许再算（现算会因 BCL 实现差异让两端拿到不同的表）；从 i = 12 起 atan(2^-i) 与 2^-i 在 Fix64 精度下已无区别（三次项低于 2^-32），后半段就是 2 的幂，这是数学事实而非表退化。
        private static readonly long[] AtanTableRaw =
        {
            3373259426L,
            1991351318L,
            1052175346L,
            534100635L,
            268086748L,
            134174063L,
            67103403L,
            33553749L,
            16777131L,
            8388597L,
            4194303L,
            2097152L,
            1048576L,
            524288L,
            262144L,
            131072L,
            65536L,
            32768L,
            16384L,
            8192L,
            4096L,
            2048L,
            1024L,
            512L,
            256L,
            128L,
            64L,
            32L,
            16L,
            8L,
            4L,
            2L,
        };
    }
}
