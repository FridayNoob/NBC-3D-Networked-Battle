// ============================================================================
//  M1-D2 · Fix64 确定性定点数的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md D2
//  需求依据：FW-M16、§6.4 DET-01
//
//  ---------------------------------------------------------------------------
//  这组测试的两个来源（都要留）
//  ---------------------------------------------------------------------------
//  ① **手写用例**：钉死具体的边界与语义（负数取整、饱和、除以 0、截断方向…）
//  ② **随机对拍**：用 `double` 当参考，跑几万组随机输入，把**误差量出来**。
//
//  ⚠️ 第 ② 类不可省。定点数的 bug 有个共同特点：
//     **在"顺手写的那几个用例"上全对，在日常数值上错**。
//     本项目已经真实发生过一次 —— 第一版 `Floor`/`Ceiling` 对所有**负的非整数**都错
//     （`floor(-1.5)` 得到 -3），而 `floor(1.5)` 是对的，看代码看不出来。
//     是 .NET 侧的数值对拍探针把它抓出来的。
//
//  ⚠️ 对拍时**判据要选对**：定点数的固有量化是**绝对**量（2^-32）。
//     结果越小，相对误差看起来越大 —— 但那是表示法的性质，不是 bug。
//     所以除法的判据是"**绝对误差 ≤ 1 个 ULP**"，相对误差只在大结果上看。
//     （第一版判据写成相对误差，误报过一次。）
// ============================================================================

using System;
using NBC.Shared;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// D2：定点数学的测试。
    /// </summary>
    public sealed class Fix64Tests
    {
        // ====================================================================
        //  一、表示法
        // ====================================================================

        /// <summary>1.0 的原始值就是 2^32 —— 整个表示法的基准。</summary>
        [Test]
        public void One_HasRawValueOfTwoTo32()
        {
            Assert.AreEqual(4294967296L, Fix64.One.RawValue);
            Assert.AreEqual(2147483648L, Fix64.Half.RawValue);
            Assert.AreEqual(0L, Fix64.Zero.RawValue);
            Assert.AreEqual(-4294967296L, Fix64.MinusOne.RawValue);
            Assert.AreEqual(Fix64.FractionalBits, 32);
        }

        /// <summary>整数构造是精确的，不丢精度。</summary>
        [Test]
        public void FromInt_IsExact()
        {
            Assert.AreEqual(3L * Fix64.OneRaw, Fix64.FromInt(3).RawValue);
            Assert.AreEqual(-7L * Fix64.OneRaw, Fix64.FromInt(-7).RawValue);
            Assert.AreEqual(0L, Fix64.FromInt(0).RawValue);
        }

        /// <summary>和浮点互转是**显式**的（会丢精度，不该悄悄发生）。</summary>
        [Test]
        public void FloatConversions_AreExplicit()
        {
            Fix64 fromFloat = (Fix64)2.5f;
            Assert.AreEqual(Fix64.FromRaw(Fix64.OneRaw * 5 / 2), fromFloat);

            float back = (float)fromFloat;
            Assert.AreEqual(2.5f, back, 1e-6f);

            // 隐式转换只允许 int（精确的）
            Fix64 fromInt = 5;
            Assert.AreEqual(Fix64.FromInt(5), fromInt);
        }

        /// <summary>NaN 不许悄悄变成某个数。</summary>
        [Test]
        public void FromDouble_RejectsNaN()
        {
            Assert.Throws<ArgumentException>(() => Fix64.FromDouble(double.NaN));
        }

        /// <summary>
        /// **`ToInt` 与 `Floor` 是两回事**，别混用：
        /// `ToInt()` 朝**零**截断（-1.5 → -1），`Floor()` 朝**负无穷**（-1.5 → -2）。
        /// <para>
        /// ⚠️ 第一版 `ToInt` 用的是算术右移，也就是 `Floor` 的语义 —— 契约写的是"向零截断"，
        /// 实现却给了 -2。是这条测试抓出来的。
        /// （顺带记一笔：我那个"几百万组随机对拍"的数值探针**没抓到它**，
        ///   因为探针压根没测 `ToInt` —— **再强的随机测试也覆盖不到它没调用的函数**。）
        /// </para>
        /// </summary>
        [Test]
        public void ToInt_TruncatesTowardZeroNotFloor()
        {
            Fix64 positive = Fix64.FromRaw(Fix64.OneRaw * 3 / 2);
            Fix64 negative = Fix64.FromRaw(-(Fix64.OneRaw * 3 / 2));

            Assert.AreEqual(1, positive.ToInt());
            Assert.AreEqual(-1, negative.ToInt(), "(-1.5).ToInt() 必须是 -1（朝零），不是 -2（朝负无穷）");
            Assert.AreEqual(2, Fix64.FromInt(2).ToInt());
            Assert.AreEqual(-2, Fix64.FromInt(-2).ToInt());

            Assert.AreEqual(1L, positive.ToLong());
            Assert.AreEqual(-1L, negative.ToLong());

            // 和 Floor 对照：同一个输入，两个函数结果**故意不同**
            Assert.AreEqual(-2, Fix64.Floor(negative).ToInt(), "Floor(-1.5) 是 -2");
            Assert.AreEqual(-1, negative.ToInt(), "ToInt 是 -1");
        }

        // ====================================================================
        //  二、加减乘除的基本语义
        // ====================================================================

        /// <summary>加减。</summary>
        [Test]
        public void AddAndSubtract()
        {
            Assert.AreEqual(Fix64.FromInt(2), Fix64.One + Fix64.One);
            Assert.AreEqual(Fix64.FromInt(-2), Fix64.FromInt(3) - Fix64.FromInt(5));
            Assert.AreEqual(Fix64.Zero, Fix64.One - Fix64.One);
            Assert.AreEqual(Fix64.FromInt(-5), -Fix64.FromInt(5));
        }

        /// <summary>乘法：小数乘小数。</summary>
        [Test]
        public void Multiply()
        {
            Assert.AreEqual(Fix64.FromRaw(Fix64.OneRaw / 4), Fix64.Half * Fix64.Half);
            Assert.AreEqual(Fix64.One, Fix64.MinusOne * Fix64.MinusOne);
            Assert.AreEqual(Fix64.FromInt(6), Fix64.FromInt(2) * Fix64.FromInt(3));
            Assert.AreEqual(Fix64.Zero, Fix64.FromInt(12345) * Fix64.Zero);
        }

        /// <summary>除法，含负数与**向零截断**。</summary>
        [Test]
        public void Divide()
        {
            Assert.AreEqual(Fix64.FromRaw(Fix64.OneRaw * 3 / 2), Fix64.FromInt(3) / Fix64.FromInt(2));
            Assert.AreEqual(Fix64.FromRaw(-(Fix64.OneRaw * 3 / 2)), Fix64.FromInt(-3) / Fix64.FromInt(2));

            // 1/3 的原始值（向零截断）
            Assert.AreEqual(1431655765L, (Fix64.One / Fix64.FromInt(3)).RawValue);
            Assert.AreEqual(-1431655765L, (Fix64.MinusOne / Fix64.FromInt(3)).RawValue);
        }

        /// <summary>除以 0 是**逻辑错误**，当场抛，不做饱和。</summary>
        [Test]
        public void DivideByZero_Throws()
        {
            Assert.Throws<DivideByZeroException>(() => { Fix64 bad = Fix64.One / Fix64.Zero; });
            Assert.Throws<DivideByZeroException>(() => { Fix64 bad = Fix64.One % Fix64.Zero; });
        }

        /// <summary>取余跟 C# 整数的语义一致（符号跟被除数）。</summary>
        [Test]
        public void Remainder()
        {
            Assert.AreEqual(Fix64.One, Fix64.FromInt(7) % Fix64.FromInt(3));
            Assert.AreEqual(Fix64.MinusOne, Fix64.FromInt(-7) % Fix64.FromInt(3));
            Assert.AreEqual(Fix64.One, Fix64.FromInt(7) % Fix64.FromInt(-3));
        }

        // ====================================================================
        //  三、饱和（溢出不回绕、不抛异常）
        // ====================================================================

        /// <summary>
        /// 溢出**饱和**，不回绕、不抛异常。
        /// <para>回绕会让"变大"突然变成负数 —— 在位移里表现为"角色瞬移到地图另一头"，且不报错。</para>
        /// </summary>
        [Test]
        public void Overflow_Saturates()
        {
            Assert.AreEqual(Fix64.MaxValue, Fix64.MaxValue + Fix64.One);
            Assert.AreEqual(Fix64.MinValue, Fix64.MinValue - Fix64.One);
            Assert.AreEqual(Fix64.MaxValue, Fix64.MaxValue * Fix64.FromInt(2));
            Assert.AreEqual(Fix64.MinValue, Fix64.MinValue * Fix64.FromInt(2));
            Assert.AreEqual(Fix64.MaxValue, -Fix64.MinValue);
        }

        /// <summary>负数饱和方向别搞反（`MinValue` 越界应当到 `MinValue`）。</summary>
        [Test]
        public void NegativeOverflow_SaturatesToMinValue()
        {
            Fix64 veryNegative = Fix64.FromInt(-2000000000);

            Assert.AreEqual(Fix64.MinValue, veryNegative * Fix64.FromInt(100));
            Assert.AreEqual(Fix64.MinValue, Fix64.MinValue + Fix64.MinusOne);
        }

        // ====================================================================
        //  四、取整（负数那几个是最容易错的）
        // ====================================================================

        /// <summary>
        /// 向下取整，**含负数**。
        /// <para>
        /// ⚠️ 第一版这里对**所有负的非整数**都是错的（`floor(-1.5)` 得到 -3）：
        /// 我写了 `raw >> 32` 又判断负数减 1，但 C# 的 `>>` 对负数本来就是算术右移（朝负无穷），
        /// 那一步多减了。而 `floor(1.5)` 是对的 —— **看代码看不出来**。
        /// </para>
        /// </summary>
        [Test]
        public void Floor_HandlesNegativeCorrectly()
        {
            Fix64 onePointFive = Fix64.FromRaw(Fix64.OneRaw * 3 / 2);

            Assert.AreEqual(Fix64.One, Fix64.Floor(onePointFive));
            Assert.AreEqual(Fix64.FromInt(-2), Fix64.Floor(-onePointFive),
                "floor(-1.5) 必须是 -2（朝负无穷），不是 -1 也不是 -3");
            Assert.AreEqual(Fix64.FromInt(2), Fix64.Floor(Fix64.FromInt(2)), "整数应当原样返回");
            Assert.AreEqual(Fix64.FromInt(-2), Fix64.Floor(Fix64.FromInt(-2)));
            Assert.AreEqual(Fix64.Zero, Fix64.Floor(Fix64.FromRaw(Fix64.OneRaw / 3)));
        }

        /// <summary>向上取整，**含负数**。</summary>
        [Test]
        public void Ceiling_HandlesNegativeCorrectly()
        {
            Fix64 onePointFive = Fix64.FromRaw(Fix64.OneRaw * 3 / 2);

            Assert.AreEqual(Fix64.FromInt(2), Fix64.Ceiling(onePointFive));
            Assert.AreEqual(Fix64.MinusOne, Fix64.Ceiling(-onePointFive),
                "ceiling(-1.5) 必须是 -1（朝正无穷）");
            Assert.AreEqual(Fix64.FromInt(2), Fix64.Ceiling(Fix64.FromInt(2)));
            Assert.AreEqual(Fix64.FromInt(-2), Fix64.Ceiling(Fix64.FromInt(-2)));
            Assert.AreEqual(Fix64.One, Fix64.Ceiling(Fix64.FromRaw(Fix64.OneRaw / 3)));
        }

        /// <summary>四舍五入：**远离零**（0.5 → 1，-0.5 → -1），方向写死。</summary>
        [Test]
        public void Round_IsAwayFromZero()
        {
            Assert.AreEqual(Fix64.One, Fix64.Round(Fix64.Half));
            Assert.AreEqual(Fix64.MinusOne, Fix64.Round(-Fix64.Half));
            Assert.AreEqual(Fix64.One, Fix64.Round(Fix64.FromRaw(Fix64.OneRaw * 7 / 5)));
            Assert.AreEqual(Fix64.FromInt(2), Fix64.Round(Fix64.FromRaw(Fix64.OneRaw * 8 / 5)));
            Assert.AreEqual(Fix64.FromInt(-2), Fix64.Round(Fix64.FromRaw(-(Fix64.OneRaw * 8 / 5))));
        }

        // ====================================================================
        //  五、开方
        // ====================================================================

        /// <summary>开方的基本值。</summary>
        [Test]
        public void Sqrt_BasicValues()
        {
            Assert.AreEqual(Fix64.Zero, Fix64.Sqrt(Fix64.Zero));
            Assert.AreEqual(Fix64.One, Fix64.Sqrt(Fix64.One));
            Assert.AreEqual(Fix64.FromInt(2), Fix64.Sqrt(Fix64.FromInt(4)));
            Assert.AreEqual(Fix64.FromInt(3), Fix64.Sqrt(Fix64.FromInt(9)));
        }

        /// <summary>负数开方抛异常（不是返回 NaN）。</summary>
        [Test]
        public void Sqrt_OfNegative_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => { Fix64 bad = Fix64.Sqrt(Fix64.MinusOne); });
        }

        /// <summary>
        /// 开方精度：**绝对**误差不超过 2^-16（整数平方根截断后左移 16 位的理论界）。
        /// </summary>
        [Test]
        public void Sqrt_AbsoluteErrorWithinTheoreticalBound()
        {
            const double bound = 1.0 / 65536.0;   // 2^-16
            double worst = 0.0;

            for (int i = 1; i <= 20000; i++)
            {
                Fix64 x = Fix64.FromRaw((long)i * 987654321L);

                if (x.RawValue <= 0L)
                {
                    continue;
                }

                double expected = Math.Sqrt(x.ToDouble());
                double actual = Fix64.Sqrt(x).ToDouble();
                double error = Math.Abs(actual - expected);

                if (error > worst)
                {
                    worst = error;
                }
            }

            Assert.LessOrEqual(worst, bound + 1e-12,
                "开方绝对误差应当不超过理论界 2^-16，实测最大的一个是 " + worst);
        }

        // ====================================================================
        //  六、随机对拍（**量误差**，不是"看起来对"）
        // ====================================================================

        /// <summary>
        /// 乘法对拍：随机几万组，检查相对误差。
        /// <para>定点乘法的理论相对误差量级约 2^-32 ≈ 2.3e-10，留足余量到 1e-8。</para>
        /// </summary>
        [Test]
        public void Multiply_MatchesDoubleWithinTolerance()
        {
            // 用固定种子，保证**可复现** —— 随机测试不可复现就等于没有测试。
            Random rng = new Random(20260921);
            double worst = 0.0;

            for (int i = 0; i < 50000; i++)
            {
                long ra = (long)((rng.NextDouble() - 0.5) * 2.0 * 10000.0 * Fix64.OneRaw);
                long rb = (long)((rng.NextDouble() - 0.5) * 2.0 * 10000.0 * Fix64.OneRaw);

                Fix64 a = Fix64.FromRaw(ra);
                Fix64 b = Fix64.FromRaw(rb);

                double expected = a.ToDouble() * b.ToDouble();

                if (Math.Abs(expected) < 1e-3)
                {
                    continue;
                }

                double actual = (a * b).ToDouble();
                double rel = Math.Abs(actual - expected) / Math.Abs(expected);

                if (rel > worst)
                {
                    worst = rel;
                }
            }

            Assert.Less(worst, 1e-8, "乘法最大相对误差应当远小于 1e-8，实测 " + worst);
        }

        /// <summary>
        /// 除法对拍：判据是**绝对误差 ≤ 1 个 ULP**（见文件头说明为什么不用相对误差）。
        /// </summary>
        [Test]
        public void Divide_AbsoluteErrorWithinOneUlp()
        {
            Random rng = new Random(20260922);
            double worstRaw = 0.0;

            for (int i = 0; i < 50000; i++)
            {
                long ra = (long)((rng.NextDouble() - 0.5) * 2.0 * 10000.0 * Fix64.OneRaw);
                long rb = (long)((rng.NextDouble() - 0.5) * 2.0 * 10000.0 * Fix64.OneRaw);

                if (rb == 0L || Math.Abs(rb) < (Fix64.OneRaw / 1000))
                {
                    continue;
                }

                Fix64 a = Fix64.FromRaw(ra);
                Fix64 b = Fix64.FromRaw(rb);

                double expected = a.ToDouble() / b.ToDouble();
                double actual = (a / b).ToDouble();

                // 折回"原始值"比较，误差应当不超过 1 个 ULP
                double rawError = Math.Abs((actual - expected) * Fix64.OneRaw);

                if (rawError > worstRaw)
                {
                    worstRaw = rawError;
                }
            }

            Assert.LessOrEqual(worstRaw, 1.0 + 1e-9,
                "除法绝对误差应当不超过 1 个 ULP，实测最大 " + worstRaw + " ULP");
        }

        // ====================================================================
        //  七、确定性（这是整个模块存在的理由）
        // ====================================================================

        /// <summary>
        /// **同样的输入，位级别同样的输出。**
        /// <para>
        /// 这条是帧同步的地基：只要有一处不确定性，两端就会越跑越远。
        /// 反复算同一个表达式，原始值必须**完全相等**（不是"约等于"）。
        /// </para>
        /// </summary>
        [Test]
        public void SameInputs_ProduceBitIdenticalResults()
        {
            Fix64 a = Fix64.FromRaw(1234567890123L);
            Fix64 b = Fix64.FromRaw(-987654321098L);
            Fix64 c = Fix64.FromRaw(31415926535L);

            long first = (((a * b) + c) / (b - c)).RawValue;

            for (int i = 0; i < 1000; i++)
            {
                long again = (((a * b) + c) / (b - c)).RawValue;

                Assert.AreEqual(first, again,
                    "同样的输入必须得到位级别相同的结果 —— 这是帧同步的地基");
            }
        }

        /// <summary>
        /// 整数的加、减、乘是**精确**的；除法只在**整除**时与整数结果相同。
        /// <para>
        /// ⚠️ 第一版这里断言"整数除法两边应当相等"，那是**错的**：
        /// `(Fix64)x / (Fix64)y` 会**保留小数**（-1000/3 = -333.333…），
        /// 而 `(Fix64)(x / y)` 是整数除法（-333）。**它们本来就不该相等** ——
        /// Fix64 的除法**比整数除法更精确**，不是不同。
        /// </para>
        /// </summary>
        [Test]
        public void SmallIntegerArithmetic_IsExact()
        {
            Random rng = new Random(20260925);
            int divisibleSamples = 0;
            double worstUlp = 0.0;

            for (int i = 0; i < 20000; i++)
            {
                int x = rng.Next(-100000, 100000);
                int y = rng.Next(-1000, 1000);

                if (y == 0)
                {
                    continue;
                }

                Assert.AreEqual((Fix64)(x + y), (Fix64)x + (Fix64)y, "整数加法应当精确");
                Assert.AreEqual((Fix64)(x * y), (Fix64)x * (Fix64)y, "整数乘法应当精确");

                if (x % y == 0)
                {
                    // 整除时：Fix64 的结果应当**恰好**等于整数结果
                    Assert.AreEqual((Fix64)(x / y), (Fix64)x / (Fix64)y,
                        "整除时 Fix64 除法应当精确：(" + x + " / " + y + ")");
                    divisibleSamples++;
                }
                else
                {
                    // 除不尽时：应当逼近**精确有理数**（误差 ≤ 1 个 ULP），而不是整数商
                    double exact = (double)x / (double)y;
                    double actual = ((Fix64)x / (Fix64)y).ToDouble();
                    double ulp = Math.Abs(actual - exact) * Fix64.OneRaw;

                    if (ulp > worstUlp)
                    {
                        worstUlp = ulp;
                    }
                }
            }

            Assert.Greater(divisibleSamples, 0, "随机样本里应当有整除的情况（否则这条没测到）");
            Assert.LessOrEqual(worstUlp, 1.0 + 1e-9,
                "除不尽时应当逼近精确有理数，最大误差 " + worstUlp + " 个 ULP");
        }

        // ====================================================================
        //  八、ToString 走整数路径（不经过浮点）
        // ====================================================================

        /// <summary>
        /// 调试文本必须**可复现** —— 所以实现里走的是整数拼字符串，不经过浮点。
        /// </summary>
        [Test]
        public void ToString_UsesIntegerPathOnly()
        {
            Assert.AreEqual("1.000000", Fix64.One.ToString());
            Assert.AreEqual("0.500000", Fix64.Half.ToString());
            Assert.AreEqual("-2.250000", Fix64.FromRaw(-(Fix64.OneRaw * 9 / 4)).ToString());
            Assert.AreEqual("0.000000", Fix64.Zero.ToString());
            Assert.AreEqual("0.333333", (Fix64.One / Fix64.FromInt(3)).ToString());
        }
    }
}
