// ============================================================================
//  M1-D2 · FixMath（CORDIC 三角）的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md D2
//  需求依据：FW-M16、§6.4 DET-01
//
//  ---------------------------------------------------------------------------
//  这些测试和"数值探针"的分工
//  ---------------------------------------------------------------------------
//  · **探针**（`.dsh-tmp/fixprobe`，不属于交付物）：跑几十万组随机输入，
//    和 `Math.Sin` / `Math.Atan2` 对拍，**把误差量出来**。
//    它抓到过两个真 bug（`CordicVector` 的 z 符号反了、`Atan2(0,1)` 在零点振荡）。
//  · **这个文件**：把**契约**钉死 —— 特殊值、定义域、异常、确定性。
//
//  ⚠️ 两类缺一不可：探针只能告诉你"误差多大"，告诉不了你"该不该抛异常"；
//     而手写用例只能覆盖你想得到的输入。
// ============================================================================

using System;
using NBC.Shared;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// D2：确定性三角函数的测试。
    /// </summary>
    public sealed class FixMathTests
    {
        /// <summary>浮点比较容差（CORDIC 32 次迭代的残差在 1e-8 量级）。</summary>
        private const double Tolerance = 1e-6;

        // ====================================================================
        //  一、特殊值（数学上精确的场合就该精确）
        // ====================================================================

        /// <summary>`Sin(0)` / `Cos(0)` 必须**精确**。</summary>
        [Test]
        public void SinCos_AtZero_AreExact()
        {
            Assert.AreEqual(Fix64.Zero, FixMath.Sin(Fix64.Zero), "Sin(0) 必须是精确的 0");
            Assert.AreEqual(Fix64.One, FixMath.Cos(Fix64.Zero), "Cos(0) 必须是精确的 1");

            // 角度制也一样
            Assert.AreEqual(Fix64.One, FixMath.CosDeg(Fix64.Zero));
            Assert.AreEqual(Fix64.Zero, FixMath.SinDeg(Fix64.Zero));
        }

        /// <summary>
        /// π/2 与 π 处的值（这里**不能**要求精确 —— π 本身在定点下就有截断）。
        /// </summary>
        [Test]
        public void SinCos_AtHalfPiAndPi_AreWithinTolerance()
        {
            Assert.AreEqual(1.0, FixMath.Sin(FixMath.HalfPi).ToDouble(), Tolerance);
            Assert.AreEqual(0.0, FixMath.Cos(FixMath.HalfPi).ToDouble(), 1e-6);
            Assert.AreEqual(-1.0, FixMath.Cos(FixMath.Pi).ToDouble(), Tolerance);
            Assert.AreEqual(0.0, FixMath.Sin(FixMath.Pi).ToDouble(), 1e-6);
        }

        /// <summary>`SinCos` 一次算两个，结果必须和单独调用一致。</summary>
        [Test]
        public void SinCos_MatchesIndividualCalls()
        {
            Fix64[] angles =
            {
                Fix64.Zero, Fix64.FromRaw(123456789L), Fix64.FromRaw(-987654321L),
                FixMath.HalfPi, FixMath.Pi, -FixMath.HalfPi
            };

            for (int i = 0; i < angles.Length; i++)
            {
                Fix64 cos;
                Fix64 sin;

                FixMath.SinCos(angles[i], out cos, out sin);

                Assert.AreEqual(FixMath.Cos(angles[i]), cos, "cos 不一致（角度 #" + i + "）");
                Assert.AreEqual(FixMath.Sin(angles[i]), sin, "sin 不一致（角度 #" + i + "）");
            }
        }

        // ====================================================================
        //  二、恒等式
        // ====================================================================

        /// <summary>
        /// `sin² + cos² = 1` —— 这是三角函数最基本的恒等式，
        /// 也是**精度最容易暴露**的地方（两个近似值的平方和）。
        /// </summary>
        [Test]
        public void PythagoreanIdentity_HoldsWithinTolerance()
        {
            Random rng = new Random(20261003);
            double worst = 0.0;

            for (int i = 0; i < 20000; i++)
            {
                double angle = (rng.NextDouble() - 0.5) * 2.0 * Math.PI;
                Fix64 a = Fix64.FromDouble(angle);

                Fix64 cos;
                Fix64 sin;

                FixMath.SinCos(a, out cos, out sin);

                double sum = (cos * cos + sin * sin).ToDouble();

                if (Math.Abs(sum - 1.0) > worst)
                {
                    worst = Math.Abs(sum - 1.0);
                }
            }

            Assert.Less(worst, 1e-6, "sin²+cos² 应当接近 1，实测最大偏差 " + worst);
        }

        /// <summary>奇偶性：sin 是奇函数、cos 是偶函数。</summary>
        [Test]
        public void SinIsOdd_CosIsEven()
        {
            Random rng = new Random(20261004);

            for (int i = 0; i < 5000; i++)
            {
                double angle = (rng.NextDouble() - 0.5) * 20.0;
                Fix64 a = Fix64.FromDouble(angle);

                Assert.AreEqual(-FixMath.Sin(a).ToDouble(), FixMath.Sin(-a).ToDouble(), 1e-6);
                Assert.AreEqual(FixMath.Cos(a).ToDouble(), FixMath.Cos(-a).ToDouble(), 1e-6);
            }
        }

        /// <summary>周期性：加一圈不变。</summary>
        [Test]
        public void Periodicity_Holds()
        {
            Random rng = new Random(20261005);

            for (int i = 0; i < 5000; i++)
            {
                double angle = (rng.NextDouble() - 0.5) * 20.0;
                Fix64 a = Fix64.FromDouble(angle);

                Assert.AreEqual(FixMath.Sin(a).ToDouble(), FixMath.Sin(a + FixMath.TwoPi).ToDouble(), 1e-5);
                Assert.AreEqual(FixMath.Cos(a).ToDouble(), FixMath.Cos(a + FixMath.TwoPi).ToDouble(), 1e-5);
            }
        }

        // ====================================================================
        //  三、与 double 对拍（探针的等价物，样本少一些）
        // ====================================================================

        /// <summary>sin / cos 与 `Math.Sin` / `Math.Cos` 对拍。</summary>
        [Test]
        public void SinCos_MatchDoubleWithinTolerance()
        {
            Random rng = new Random(20261006);
            double worstSin = 0.0;
            double worstCos = 0.0;

            for (int i = 0; i < 50000; i++)
            {
                // 覆盖小角度、一圈内、以及几十圈（考验角度归约）
                double angle = (rng.NextDouble() - 0.5) * 200.0;
                Fix64 a = Fix64.FromDouble(angle);

                double eSin = Math.Abs(FixMath.Sin(a).ToDouble() - Math.Sin(angle));
                double eCos = Math.Abs(FixMath.Cos(a).ToDouble() - Math.Cos(angle));

                if (eSin > worstSin)
                {
                    worstSin = eSin;
                }

                if (eCos > worstCos)
                {
                    worstCos = eCos;
                }
            }

            Assert.Less(worstSin, 1e-6, "sin 误差实测最大 " + worstSin);
            Assert.Less(worstCos, 1e-6, "cos 误差实测最大 " + worstCos);
        }

        /// <summary>`Atan2` 与 `Math.Atan2` 对拍（含负象限与接近垂直的向量）。</summary>
        [Test]
        public void Atan2_MatchesDoubleWithinTolerance()
        {
            Random rng = new Random(20261007);
            double worst = 0.0;

            for (int i = 0; i < 50000; i++)
            {
                double y = (rng.NextDouble() - 0.5) * 20000.0;
                double x = (rng.NextDouble() - 0.5) * 20000.0;

                if (Math.Abs(y) < 1e-6 && Math.Abs(x) < 1e-6)
                {
                    continue;
                }

                double expected = Math.Atan2(y, x);
                double actual = FixMath.Atan2(Fix64.FromDouble(y), Fix64.FromDouble(x)).ToDouble();
                double error = Math.Abs(actual - expected);

                // π 与 -π 是同一个方向，跨过边界时差值接近 2π
                if (error > Math.PI)
                {
                    error = Math.Abs(error - 2.0 * Math.PI);
                }

                if (error > worst)
                {
                    worst = error;
                }
            }

            Assert.Less(worst, 1e-6, "atan2 误差实测最大 " + worst + " 弧度");
        }

        // ====================================================================
        //  四、Atan2 的退化情况（这些最容易写错）
        // ====================================================================

        /// <summary>
        /// `Atan2(0, 0)` 约定返回 0（**不抛异常**）。
        /// <para>因为"没有方向向量"在游戏里是正常情况，抛异常会逼每个调用点判空。</para>
        /// </summary>
        [Test]
        public void Atan2_AtOrigin_ReturnsZero()
        {
            Assert.AreEqual(Fix64.Zero, FixMath.Atan2(Fix64.Zero, Fix64.Zero));
        }

        /// <summary>
        /// **坐标轴上的四个方向必须精确**。
        /// <para>
        /// ⚠️ CORDIC 向量模式在 `y == 0` 时会在零点附近**来回振荡**（实测给出 1e-9 而不是 0），
        /// 所以这里加了精确快路径。这条测试就是钉住它。
        /// </para>
        /// </summary>
        [Test]
        public void Atan2_OnAxes_IsExact()
        {
            Assert.AreEqual(Fix64.Zero, FixMath.Atan2(Fix64.Zero, Fix64.One), "正右方应当是精确的 0");
            Assert.AreEqual(FixMath.Pi, FixMath.Atan2(Fix64.Zero, Fix64.MinusOne), "正左方应当是精确的 π");

            Assert.AreEqual(FixMath.HalfPi, FixMath.Atan2(Fix64.One, Fix64.Zero));
            Assert.AreEqual(FixMath.MinusHalfPi, FixMath.Atan2(Fix64.MinusOne, Fix64.Zero));
        }

        /// <summary>四个象限的符号要对。</summary>
        [Test]
        public void Atan2_QuadrantsHaveCorrectSigns()
        {
            Assert.AreEqual(Math.PI / 4.0, FixMath.Atan2(Fix64.One, Fix64.One).ToDouble(), Tolerance);
            Assert.AreEqual(3.0 * Math.PI / 4.0, FixMath.Atan2(Fix64.One, Fix64.MinusOne).ToDouble(), Tolerance);
            Assert.AreEqual(-Math.PI / 4.0, FixMath.Atan2(Fix64.MinusOne, Fix64.One).ToDouble(), Tolerance);
            Assert.AreEqual(-3.0 * Math.PI / 4.0, FixMath.Atan2(Fix64.MinusOne, Fix64.MinusOne).ToDouble(), Tolerance);
        }

        /// <summary>
        /// **接近垂直**的向量（`|y| &gt;&gt; |x|`）必须给对符号。
        /// <para>
        /// ⚠️ 这条是那个真 bug 的回归测试：`CordicVector` 的 z 累加符号写反时，
        /// 这类输入会给出**符号相反**的结果（差 π），而其它输入看起来都正常。
        /// </para>
        /// </summary>
        [Test]
        public void Atan2_NearlyVertical_KeepsCorrectSign()
        {
            // x 很小、y 很大 → 角度接近 +π/2
            Assert.AreEqual(Math.PI / 2.0,
                FixMath.Atan2(Fix64.FromInt(100000), Fix64.FromRaw(294L)).ToDouble(), 1e-3);

            // 负 y → 角度接近 -π/2（符号必须反过来）
            Assert.AreEqual(-Math.PI / 2.0,
                FixMath.Atan2(Fix64.FromInt(-100000), Fix64.FromRaw(294L)).ToDouble(), 1e-3);

            // 第三/第四象限同理
            Assert.AreEqual(-Math.PI / 2.0,
                FixMath.Atan2(Fix64.FromInt(-100000), Fix64.FromRaw(-294L)).ToDouble(), 1e-3);
        }

        /// <summary>`Atan` 的值域在 `(-π/2, π/2)`。</summary>
        [Test]
        public void Atan_StaysWithinRange()
        {
            Assert.AreEqual(0.0, FixMath.Atan(Fix64.Zero).ToDouble(), Tolerance);
            Assert.AreEqual(Math.PI / 4.0, FixMath.Atan(Fix64.One).ToDouble(), Tolerance);
            Assert.AreEqual(-Math.PI / 4.0, FixMath.Atan(Fix64.MinusOne).ToDouble(), Tolerance);
            Assert.Less(Math.Abs(FixMath.Atan(Fix64.FromInt(100000)).ToDouble()), Math.PI / 2.0);
        }

        // ====================================================================
        //  五、Asin / Acos 与定义域
        // ====================================================================

        /// <summary>asin / acos 的关键值。</summary>
        [Test]
        public void AsinAcos_KeyValues()
        {
            Assert.AreEqual(0.0, FixMath.Asin(Fix64.Zero).ToDouble(), Tolerance);
            Assert.AreEqual(Math.PI / 2.0, FixMath.Asin(Fix64.One).ToDouble(), 1e-6);
            Assert.AreEqual(-Math.PI / 2.0, FixMath.Asin(Fix64.MinusOne).ToDouble(), 1e-6);

            Assert.AreEqual(Math.PI / 2.0, FixMath.Acos(Fix64.Zero).ToDouble(), Tolerance);
            Assert.AreEqual(0.0, FixMath.Acos(Fix64.One).ToDouble(), 1e-6);
            Assert.AreEqual(Math.PI, FixMath.Acos(Fix64.MinusOne).ToDouble(), 1e-6);
        }

        /// <summary>超出定义域**抛异常**，不返回 NaN。</summary>
        [Test]
        public void AsinAcos_OutOfDomain_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => { Fix64 bad = FixMath.Asin(Fix64.FromInt(2)); });
            Assert.Throws<ArgumentOutOfRangeException>(() => { Fix64 bad = FixMath.Asin(Fix64.FromInt(-2)); });
            Assert.Throws<ArgumentOutOfRangeException>(() => { Fix64 bad = FixMath.Acos(Fix64.FromInt(2)); });
            Assert.Throws<ArgumentOutOfRangeException>(() => { Fix64 bad = FixMath.Acos(Fix64.FromInt(-2)); });

            // 边界上不该抛
            Assert.DoesNotThrow(() => { Fix64 ok = FixMath.Asin(Fix64.One); });
            Assert.DoesNotThrow(() => { Fix64 ok = FixMath.Acos(Fix64.MinusOne); });
        }

        /// <summary>asin / acos 互补：`asin(v) + acos(v) = π/2`。</summary>
        [Test]
        public void AsinAcos_AreComplementary()
        {
            Random rng = new Random(20261008);

            for (int i = 0; i < 5000; i++)
            {
                double v = rng.NextDouble() * 1.8 - 0.9;
                Fix64 fv = Fix64.FromDouble(v);

                double sum = FixMath.Asin(fv).ToDouble() + FixMath.Acos(fv).ToDouble();

                Assert.AreEqual(Math.PI / 2.0, sum, 1e-6);
            }
        }

        // ====================================================================
        //  六、角度换算
        // ====================================================================

        /// <summary>角度与弧度互转。</summary>
        [Test]
        public void DegreeRadianConversion()
        {
            Assert.AreEqual(Math.PI, FixMath.DegToRad(Fix64.FromInt(180)).ToDouble(), 1e-8);
            Assert.AreEqual(180.0, FixMath.RadToDeg(FixMath.Pi).ToDouble(), 1e-6);
            Assert.AreEqual(0.0, FixMath.DegToRad(Fix64.Zero).ToDouble(), 0);
            Assert.AreEqual(90.0, FixMath.RadToDeg(FixMath.HalfPi).ToDouble(), 1e-6);
        }

        /// <summary>常用角度的正弦值。</summary>
        [Test]
        public void SinDeg_CommonAngles()
        {
            Assert.AreEqual(1.0, FixMath.SinDeg(Fix64.FromInt(90)).ToDouble(), Tolerance);
            Assert.AreEqual(0.0, FixMath.SinDeg(Fix64.FromInt(180)).ToDouble(), Tolerance);
            Assert.AreEqual(-1.0, FixMath.SinDeg(Fix64.FromInt(270)).ToDouble(), Tolerance);
            Assert.AreEqual(0.5, FixMath.SinDeg(Fix64.FromInt(30)).ToDouble(), 1e-6);
            Assert.AreEqual(1.0, FixMath.CosDeg(Fix64.FromInt(0)).ToDouble(), 0);
        }

        // ====================================================================
        //  七、确定性（整个模块存在的理由）
        // ====================================================================

        /// <summary>同样的角度 → **位级别相同**的结果。</summary>
        [Test]
        public void SameAngle_ProducesBitIdenticalResults()
        {
            Fix64 angle = Fix64.FromRaw(987654321L);

            Fix64 cos;
            Fix64 sin;
            FixMath.SinCos(angle, out cos, out sin);

            long firstCos = cos.RawValue;
            long firstSin = sin.RawValue;

            for (int i = 0; i < 1000; i++)
            {
                Fix64 c;
                Fix64 s;
                FixMath.SinCos(angle, out c, out s);

                Assert.AreEqual(firstCos, c.RawValue, "cos 必须位级别相同");
                Assert.AreEqual(firstSin, s.RawValue, "sin 必须位级别相同");
            }
        }

        /// <summary>
        /// 常量表必须是**硬编码**的，不能运行时用 `Math.Sin` 算 ——
        /// 那会让服务端和客户端得到**不同的表**，直接毁掉帧同步。
        /// <para>这条用"常量值本身"来钉：如果谁把它改成运行时计算，下面的值就会漂。</para>
        /// </summary>
        [Test]
        public void CordicConstantsAreHardcoded()
        {
            Assert.AreEqual(6746518852L, FixMath.HalfPiRaw);
            Assert.AreEqual(2608131496L, FixMath.CordicGainRaw);
            Assert.AreEqual(32, FixMath.CordicIterations);

            // TwoPi 必须是 Pi 的两倍（两个"本该相关"的常量各自取整就会不一致）
            Assert.AreEqual(Fix64.Pi.RawValue * 2L, FixMath.TwoPi.RawValue,
                "TwoPi 应当正好是 Pi 的两倍，而不是各自独立取整的近似值");
        }
    }
}
