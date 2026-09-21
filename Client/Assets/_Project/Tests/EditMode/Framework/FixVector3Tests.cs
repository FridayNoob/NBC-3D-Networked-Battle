// ============================================================================
//  M1-D2 · FixVector3 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md D2
//
//  ---------------------------------------------------------------------------
//  ⚠️ 一个必须记住的边界：这里**测不到 UnityEngine**
//  ---------------------------------------------------------------------------
//  `NBC.Shared` 是 `noEngineReferences: true` —— 它**根本不认识 UnityEngine**。
//  所以本文件里**不能**出现 `Vector3`。这不是"忘了测"，而是**设计本身**：
//  共享逻辑一旦能引用 UnityEngine，服务端就编不过了。
//
//  转换的事由表现层做（`new Vector3((float)v.X, ...)`），
//  而且**只允许单向**：逻辑 → 表现。反过来会把不确定性引回逻辑层。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Shared;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// D2：确定性向量的测试。
    /// </summary>
    public sealed class FixVector3Tests
    {
        private static readonly FixVector3 V34 = new FixVector3(Fix64.FromInt(3), Fix64.FromInt(4), Fix64.Zero);

        // ====================================================================
        //  一、基本运算
        // ====================================================================

        /// <summary>加减与数乘。</summary>
        [Test]
        public void AddSubtractAndScale()
        {
            FixVector3 a = new FixVector3(1, 2, 3);
            FixVector3 b = new FixVector3(4, 5, 6);

            Assert.AreEqual(new FixVector3(5, 7, 9), a + b);
            Assert.AreEqual(new FixVector3(-3, -3, -3), a - b);
            Assert.AreEqual(new FixVector3(-1, -2, -3), -a);
            Assert.AreEqual(new FixVector3(2, 4, 6), a * Fix64.FromInt(2));
            Assert.AreEqual(new FixVector3(2, 4, 6), Fix64.FromInt(2) * a);
            Assert.AreEqual(new FixVector3(1, 2, 3), a);
        }

        /// <summary>除以 0 抛异常。</summary>
        [Test]
        public void DivideByZeroScalar_Throws()
        {
            Assert.Throws<DivideByZeroException>(() => { FixVector3 bad = new FixVector3(1, 1, 1) / Fix64.Zero; });
        }

        /// <summary>相等的语义是**逐分量精确相等**。</summary>
        [Test]
        public void Equality_IsExactPerComponent()
        {
            Assert.IsTrue(new FixVector3(1, 2, 3) == new FixVector3(1, 2, 3));
            Assert.IsTrue(new FixVector3(1, 2, 3) != new FixVector3(1, 2, 4));

            // 差一个最小单位也必须判为不等
            FixVector3 almost = new FixVector3(Fix64.One, Fix64.One, Fix64.One);
            FixVector3 off = new FixVector3(Fix64.One, Fix64.One, Fix64.FromRaw(Fix64.OneRaw + 1));

            Assert.AreNotEqual(almost, off);
        }

        // ====================================================================
        //  二、长度与归一化
        // ====================================================================

        /// <summary>3-4-5 直角三角形：长度正好是 5。</summary>
        [Test]
        public void Magnitude_Of3And4IsExactly5()
        {
            Assert.AreEqual(Fix64.FromInt(25), V34.SqrMagnitude);
            Assert.AreEqual(Fix64.FromInt(5), V34.Magnitude);
        }

        /// <summary>
        /// **零向量归一化返回零向量**，不抛异常、也不是 NaN。
        /// <para>因为"还没有移动方向"在游戏里是**正常情况**；让它抛异常会逼每个调用点都判空。</para>
        /// </summary>
        [Test]
        public void Normalized_OnZeroVector_ReturnsZero()
        {
            FixVector3 result = FixVector3.Zero.Normalized();

            Assert.AreEqual(FixVector3.Zero, result);
            Assert.IsTrue(result.X.IsZero, "X 分量必须是 0");
            Assert.IsTrue(result.Y.IsZero, "Y 分量必须是 0");
            Assert.IsTrue(result.Z.IsZero, "Z 分量必须是 0");
        }

        /// <summary>归一化后的长度接近 1（精度受 `Sqrt` 的 2^-16 限制）。</summary>
        [Test]
        public void Normalized_HasUnitLengthWithinSqrtBound()
        {
            Random rng = new Random(20260928);
            double worst = 0.0;

            for (int i = 0; i < 20000; i++)
            {
                FixVector3 v = new FixVector3(
                    Fix64.FromRaw((long)((rng.NextDouble() - 0.5) * 2000.0 * Fix64.OneRaw)),
                    Fix64.FromRaw((long)((rng.NextDouble() - 0.5) * 2000.0 * Fix64.OneRaw)),
                    Fix64.FromRaw((long)((rng.NextDouble() - 0.5) * 2000.0 * Fix64.OneRaw)));

                if (v.IsZero)
                {
                    continue;
                }

                double length = v.Normalized().Magnitude.ToDouble();
                double error = Math.Abs(length - 1.0);

                if (error > worst)
                {
                    worst = error;
                }
            }

            // Sqrt 的绝对误差界是 2^-16；归一化后 |v| = 1，所以长度误差也在这个量级
            Assert.Less(worst, 1e-4, "归一化后长度误差应当很小，实测最大 " + worst);
        }

        /// <summary>长度与 double 对拍（相对误差）。</summary>
        [Test]
        public void Magnitude_MatchesDoubleWithinTolerance()
        {
            Random rng = new Random(20260929);
            double worst = 0.0;

            for (int i = 0; i < 20000; i++)
            {
                FixVector3 v = new FixVector3(
                    Fix64.FromRaw((long)((rng.NextDouble() - 0.5) * 2000.0 * Fix64.OneRaw)),
                    Fix64.FromRaw((long)((rng.NextDouble() - 0.5) * 2000.0 * Fix64.OneRaw)),
                    Fix64.FromRaw((long)((rng.NextDouble() - 0.5) * 2000.0 * Fix64.OneRaw)));

                double expected = Math.Sqrt(
                    v.X.ToDouble() * v.X.ToDouble() +
                    v.Y.ToDouble() * v.Y.ToDouble() +
                    v.Z.ToDouble() * v.Z.ToDouble());

                if (expected <= 1.0)
                {
                    continue;
                }

                double rel = Math.Abs(v.Magnitude.ToDouble() - expected) / expected;

                if (rel > worst)
                {
                    worst = rel;
                }
            }

            Assert.Less(worst, 1e-5, "长度相对误差应当很小，实测最大 " + worst);
        }

        // ====================================================================
        //  三、点积与叉积
        // ====================================================================

        /// <summary>正交向量的点积是 0。</summary>
        [Test]
        public void Dot_OfPerpendicularAxes_IsZero()
        {
            Assert.AreEqual(Fix64.Zero, FixVector3.Dot(FixVector3.Right, FixVector3.Up));
            Assert.AreEqual(Fix64.Zero, FixVector3.Dot(FixVector3.Right, FixVector3.Forward));
            Assert.AreEqual(Fix64.One, FixVector3.Dot(FixVector3.Right, FixVector3.Right));
            Assert.AreEqual(Fix64.MinusOne, FixVector3.Dot(FixVector3.Right, -FixVector3.Right));
        }

        /// <summary>
        /// **叉积是左手系，和 Unity 一致**：`Right × Up == Forward`。
        /// <para>
        /// ⚠️ 这条必须钉住：右手系的话 `Right × Up` 是 `-Forward`。
        /// 搞反了的表现是"角色朝反方向转"，而且**不报错**。
        /// </para>
        /// </summary>
        [Test]
        public void Cross_FollowsUnityLeftHandedConvention()
        {
            Assert.AreEqual(FixVector3.Forward, FixVector3.Cross(FixVector3.Right, FixVector3.Up),
                "Unity 是左手系：Right × Up 应当等于 Forward");
            Assert.AreEqual(-FixVector3.Forward, FixVector3.Cross(FixVector3.Up, FixVector3.Right));
            Assert.AreEqual(FixVector3.Right, FixVector3.Cross(FixVector3.Up, FixVector3.Forward));
        }

        /// <summary>叉积与自己为 0。</summary>
        [Test]
        public void Cross_OfParallelVectors_IsZero()
        {
            FixVector3 v = new FixVector3(2, 3, 4);

            Assert.AreEqual(FixVector3.Zero, FixVector3.Cross(v, v));
            Assert.AreEqual(FixVector3.Zero, FixVector3.Cross(v, v * Fix64.FromInt(3)));
        }

        /// <summary>点积对拍（误差用 ULP 量）。</summary>
        [Test]
        public void Dot_MatchesDoubleWithinTolerance()
        {
            Random rng = new Random(20260930);
            double worst = 0.0;

            for (int i = 0; i < 20000; i++)
            {
                FixVector3 a = new FixVector3(
                    Fix64.FromRaw((long)((rng.NextDouble() - 0.5) * 2000.0 * Fix64.OneRaw)),
                    Fix64.FromRaw((long)((rng.NextDouble() - 0.5) * 2000.0 * Fix64.OneRaw)),
                    Fix64.FromRaw((long)((rng.NextDouble() - 0.5) * 2000.0 * Fix64.OneRaw)));

                FixVector3 b = new FixVector3(
                    Fix64.FromRaw((long)((rng.NextDouble() - 0.5) * 2000.0 * Fix64.OneRaw)),
                    Fix64.FromRaw((long)((rng.NextDouble() - 0.5) * 2000.0 * Fix64.OneRaw)),
                    Fix64.FromRaw((long)((rng.NextDouble() - 0.5) * 2000.0 * Fix64.OneRaw)));

                double expected = a.X.ToDouble() * b.X.ToDouble()
                                + a.Y.ToDouble() * b.Y.ToDouble()
                                + a.Z.ToDouble() * b.Z.ToDouble();

                if (Math.Abs(expected) <= 1.0)
                {
                    continue;
                }

                double actual = FixVector3.Dot(a, b).ToDouble();
                double ulp = Math.Abs(actual - expected) / Math.Abs(expected) * Fix64.OneRaw;

                if (ulp > worst)
                {
                    worst = ulp;
                }
            }

            Assert.Less(worst, 100.0, "点积误差应当很小，实测最大 " + worst + " ULP");
        }

        // ====================================================================
        //  四、距离 / 插值 / 钳制
        // ====================================================================

        /// <summary>距离与距离平方。</summary>
        [Test]
        public void DistanceAndSquared()
        {
            FixVector3 a = new FixVector3(0, 0, 0);
            FixVector3 b = new FixVector3(3, 4, 0);

            Assert.AreEqual(Fix64.FromInt(25), FixVector3.DistanceSquared(a, b));
            Assert.AreEqual(Fix64.FromInt(5), FixVector3.Distance(a, b));
        }

        /// <summary>线性插值。</summary>
        [Test]
        public void Lerp()
        {
            FixVector3 a = new FixVector3(0, 0, 0);
            FixVector3 b = new FixVector3(10, 20, 30);

            Assert.AreEqual(a, FixVector3.Lerp(a, b, Fix64.Zero));
            Assert.AreEqual(b, FixVector3.Lerp(a, b, Fix64.One));
            Assert.AreEqual(new FixVector3(5, 10, 15), FixVector3.Lerp(a, b, Fix64.Half));

            // 不钳制 t
            Assert.AreEqual(new FixVector3(20, 40, 60), FixVector3.Lerp(a, b, Fix64.FromInt(2)));
        }

        /// <summary>长度钳制：超了才缩放。</summary>
        [Test]
        public void ClampMagnitude()
        {
            FixVector3 far = new FixVector3(100, 0, 0);

            FixVector3 clamped = FixVector3.ClampMagnitude(far, Fix64.FromInt(10));

            Assert.AreEqual(Fix64.FromInt(10), clamped.Magnitude);
            Assert.IsTrue(clamped.X > Fix64.Zero, "方向应当保持不变");

            // 不超限时原样返回（不该有一次多余的乘除，避免引入误差）
            FixVector3 near = new FixVector3(3, 0, 0);
            Assert.AreEqual(near, FixVector3.ClampMagnitude(near, Fix64.FromInt(10)));

            // 上限为 0 或负 → 零向量
            Assert.AreEqual(FixVector3.Zero, FixVector3.ClampMagnitude(far, Fix64.Zero));
            Assert.AreEqual(FixVector3.Zero, FixVector3.ClampMagnitude(far, Fix64.FromInt(-5)));
        }

        /// <summary>逐分量取小/取大/取绝对值。</summary>
        [Test]
        public void ComponentWiseMinMaxAbs()
        {
            FixVector3 a = new FixVector3(-1, 5, 3);
            FixVector3 b = new FixVector3(2, -2, 3);

            Assert.AreEqual(new FixVector3(-1, -2, 3), FixVector3.Min(a, b));
            Assert.AreEqual(new FixVector3(2, 5, 3), FixVector3.Max(a, b));
            Assert.AreEqual(new FixVector3(1, 5, 3), FixVector3.Abs(a));
        }

        // ====================================================================
        //  五、确定性（整个模块存在的理由）
        // ====================================================================

        /// <summary>同样的输入 → **位级别相同**的输出（不是"约等于"）。</summary>
        [Test]
        public void SameInputs_ProduceBitIdenticalResults()
        {
            FixVector3 a = new FixVector3(Fix64.FromRaw(123456789L), Fix64.FromRaw(-987654321L), Fix64.FromRaw(55555555L));
            FixVector3 b = new FixVector3(Fix64.FromRaw(-111111111L), Fix64.FromRaw(222222222L), Fix64.FromRaw(-333333333L));

            FixVector3 first = (a * Fix64.FromRaw(314159265L) + b).Normalized();

            for (int i = 0; i < 1000; i++)
            {
                FixVector3 again = (a * Fix64.FromRaw(314159265L) + b).Normalized();

                Assert.AreEqual(first.X.RawValue, again.X.RawValue, "X 分量必须位级别相同");
                Assert.AreEqual(first.Y.RawValue, again.Y.RawValue, "Y 分量必须位级别相同");
                Assert.AreEqual(first.Z.RawValue, again.Z.RawValue, "Z 分量必须位级别相同");
            }
        }

        /// <summary>向量运算不含任何浮点：整数分量在范围内加乘是精确的。</summary>
        [Test]
        public void IntegerVectors_AreExact()
        {
            Random rng = new Random(20261001);

            for (int i = 0; i < 20000; i++)
            {
                int x = rng.Next(-10000, 10000);
                int y = rng.Next(-10000, 10000);
                int z = rng.Next(-10000, 10000);
                int k = rng.Next(-100, 100);

                FixVector3 v = new FixVector3(x, y, z);

                Assert.AreEqual(new FixVector3(x * k, y * k, z * k), v * Fix64.FromInt(k));
                Assert.AreEqual(new FixVector3(x + k, y + k, z + k), v + Fix64.FromInt(k) * FixVector3.One);
            }
        }

        // ====================================================================
        //  六、调试文本
        // ====================================================================

        /// <summary>`ToString` 可读且稳定。</summary>
        [Test]
        public void ToString_IsReadable()
        {
            Assert.AreEqual("(1.000000, 2.000000, 3.000000)", new FixVector3(1, 2, 3).ToString());
            Assert.AreEqual("(0.000000, 0.000000, 0.000000)", FixVector3.Zero.ToString());
        }
    }
}
