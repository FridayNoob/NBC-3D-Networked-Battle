// ============================================================================
//  NBC.Shared.FixVector3 —— 确定性三维向量
//  需求依据：FW-M16、§6.4 DET-01（确定性数学）
//
//  ---------------------------------------------------------------------------
//  它和 UnityEngine.Vector3 的关系：**没有关系**
//  ---------------------------------------------------------------------------
//  本程序集是 `noEngineReferences: true` —— **根本不认识 UnityEngine**。
//  这是帧同步的硬前提（服务端没有 Unity 环境）。
//
//  所以**这里没有 `ToVector3()` / `FromVector3()`**。
//  需要转换时由**表现层**自己写：
//
//      // 表现层（有 UnityEngine 的地方）
//      Vector3 view = new Vector3((float)pos.X, (float)pos.Y, (float)pos.Z);
//
//  ⚠️ 转换是**单向的泄洪口**：逻辑算完 → 转成 float 交给渲染。
//     **绝不允许**"从 float 转回来继续算逻辑" —— 那会把不确定性引进逻辑层。
//
//  ---------------------------------------------------------------------------
//  为什么全是 Fix64 而不是分开存 float
//  ---------------------------------------------------------------------------
//  向量运算里**乘法和开方最多**，而这两处正是浮点最先出分歧的地方。
//  三个分量都用 Fix64，整条运算链就是位级别确定的。
// ============================================================================

using System;

namespace NBC.Shared
{
    /// <summary>
    /// 确定性三维向量。**纯整数运算，位级别可复现**。
    /// </summary>
    public readonly struct FixVector3 : IEquatable<FixVector3>
    {
        /// <summary>X 分量。</summary>
        public readonly Fix64 X;

        /// <summary>Y 分量。</summary>
        public readonly Fix64 Y;

        /// <summary>Z 分量。</summary>
        public readonly Fix64 Z;

        /// <summary>零向量。</summary>
        public static readonly FixVector3 Zero = new FixVector3(Fix64.Zero, Fix64.Zero, Fix64.Zero);

        /// <summary>全 1 向量。</summary>
        public static readonly FixVector3 One = new FixVector3(Fix64.One, Fix64.One, Fix64.One);

        /// <summary>单位向上（+Y）。</summary>
        public static readonly FixVector3 Up = new FixVector3(Fix64.Zero, Fix64.One, Fix64.Zero);

        /// <summary>单位向下（-Y）。</summary>
        public static readonly FixVector3 Down = new FixVector3(Fix64.Zero, Fix64.MinusOne, Fix64.Zero);

        /// <summary>单位向前（+Z）。</summary>
        public static readonly FixVector3 Forward = new FixVector3(Fix64.Zero, Fix64.Zero, Fix64.One);

        /// <summary>单位向右（+X）。</summary>
        public static readonly FixVector3 Right = new FixVector3(Fix64.One, Fix64.Zero, Fix64.Zero);

        /// <summary>构造。</summary>
        /// <param name="x">X 分量。</param>
        /// <param name="y">Y 分量。</param>
        /// <param name="z">Z 分量。</param>
        public FixVector3(Fix64 x, Fix64 y, Fix64 z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>用整数构造。</summary>
        /// <param name="x">X 分量。</param>
        /// <param name="y">Y 分量。</param>
        /// <param name="z">Z 分量。</param>
        public FixVector3(int x, int y, int z)
        {
            X = Fix64.FromInt(x);
            Y = Fix64.FromInt(y);
            Z = Fix64.FromInt(z);
        }

        // ====================================================================
        //  基本属性
        // ====================================================================

        /// <summary>是否是零向量。</summary>
        public bool IsZero
        {
            get { return X.IsZero && Y.IsZero && Z.IsZero; }
        }

        /// <summary>
        /// 长度的平方。
        /// <para>⚠️ **比较距离时优先用它**：省一次开方，而且**没有开方带来的精度损失**。</para>
        /// </summary>
        public Fix64 SqrMagnitude
        {
            get { return X * X + Y * Y + Z * Z; }
        }

        /// <summary>
        /// 长度。
        /// <para>
        /// ⚠️ 用了 <see cref="Fix64.Sqrt"/>，精度约 2^-16（见那里的说明）。
        /// 只是"比大小"的场合请用 <see cref="SqrMagnitude"/>。
        /// </para>
        /// </summary>
        public Fix64 Magnitude
        {
            get { return Fix64.Sqrt(SqrMagnitude); }
        }

        // ====================================================================
        //  归一化
        // ====================================================================

        /// <summary>
        /// 归一化。
        /// <para>
        /// ⚠️ **零向量返回零向量，不抛异常、也不返回 NaN。**
        /// 理由：零向量在游戏里是**正常情况**（比如"还没有移动方向"），
        /// 让它抛异常会让每个调用点都要先判空，反而更容易漏。
        /// 返回零向量是"可预期的安全值"。
        /// </para>
        /// </summary>
        /// <returns>单位向量；零向量时返回零向量。</returns>
        public FixVector3 Normalized()
        {
            Fix64 length = Magnitude;

            if (length.IsZero)
            {
                return Zero;
            }

            return new FixVector3(X / length, Y / length, Z / length);
        }

        // ====================================================================
        //  运算符
        // ====================================================================

        /// <summary>向量加法。</summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>和。</returns>
        public static FixVector3 operator +(FixVector3 a, FixVector3 b)
        {
            return new FixVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        /// <summary>向量减法。</summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>差。</returns>
        public static FixVector3 operator -(FixVector3 a, FixVector3 b)
        {
            return new FixVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        /// <summary>取负。</summary>
        /// <param name="v">向量。</param>
        /// <returns>相反向量。</returns>
        public static FixVector3 operator -(FixVector3 v)
        {
            return new FixVector3(-v.X, -v.Y, -v.Z);
        }

        /// <summary>数乘（标量在前）。</summary>
        /// <param name="scalar">标量。</param>
        /// <param name="v">向量。</param>
        /// <returns>结果。</returns>
        public static FixVector3 operator *(Fix64 scalar, FixVector3 v)
        {
            return new FixVector3(v.X * scalar, v.Y * scalar, v.Z * scalar);
        }

        /// <summary>数乘（标量在后）。</summary>
        /// <param name="v">向量。</param>
        /// <param name="scalar">标量。</param>
        /// <returns>结果。</returns>
        public static FixVector3 operator *(FixVector3 v, Fix64 scalar)
        {
            return new FixVector3(v.X * scalar, v.Y * scalar, v.Z * scalar);
        }

        /// <summary>
        /// 数除。
        /// <para>⚠️ `scalar` 为 0 时抛异常（和整数的除法直觉一致）。</para>
        /// </summary>
        /// <param name="v">向量。</param>
        /// <param name="scalar">标量。</param>
        /// <returns>结果。</returns>
        public static FixVector3 operator /(FixVector3 v, Fix64 scalar)
        {
            if (scalar.IsZero)
            {
                throw new DivideByZeroException("[FixVector3] 向量不能除以 0。");
            }

            return new FixVector3(v.X / scalar, v.Y / scalar, v.Z / scalar);
        }

        /// <summary>逐分量相等。</summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>是否相等。</returns>
        public static bool operator ==(FixVector3 a, FixVector3 b)
        {
            return a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        }

        /// <summary>不等。</summary>
        /// <param name="a">左操作数。</param>
        /// <param name="b">右操作数。</param>
        /// <returns>是否不等。</returns>
        public static bool operator !=(FixVector3 a, FixVector3 b)
        {
            return !(a == b);
        }

        /// <summary>逐分量相等。</summary>
        /// <param name="other">另一个向量。</param>
        /// <returns>是否相等。</returns>
        public bool Equals(FixVector3 other)
        {
            return this == other;
        }

        /// <summary>逐分量相等。</summary>
        /// <param name="obj">另一个对象。</param>
        /// <returns>是否相等。</returns>
        public override bool Equals(object obj)
        {
            return obj is FixVector3 && Equals((FixVector3)obj);
        }

        /// <summary>组合哈希。</summary>
        /// <returns>哈希值。</returns>
        public override int GetHashCode()
        {
            int h = X.GetHashCode();
            h = (h * 397) ^ Y.GetHashCode();
            h = (h * 397) ^ Z.GetHashCode();
            return h;
        }

        // ====================================================================
        //  常用运算
        // ====================================================================

        /// <summary>
        /// 点积。
        /// <para>⚠️ **不要**用它当"夹角大小"——点积是余弦的**长度乘积倍**，不是角度。</para>
        /// </summary>
        /// <param name="a">a。</param>
        /// <param name="b">b。</param>
        /// <returns>点积。</returns>
        public static Fix64 Dot(FixVector3 a, FixVector3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        /// <summary>
        /// 叉积（**左手坐标系**，和 Unity 一致）。
        /// </summary>
        /// <param name="a">a。</param>
        /// <param name="b">b。</param>
        /// <returns>叉积。</returns>
        public static FixVector3 Cross(FixVector3 a, FixVector3 b)
        {
            return new FixVector3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        /// <summary>距离的平方（比较远近时优先用它）。</summary>
        /// <param name="a">a。</param>
        /// <param name="b">b。</param>
        /// <returns>距离平方。</returns>
        public static Fix64 DistanceSquared(FixVector3 a, FixVector3 b)
        {
            return (a - b).SqrMagnitude;
        }

        /// <summary>距离。</summary>
        /// <param name="a">a。</param>
        /// <param name="b">b。</param>
        /// <returns>距离。</returns>
        public static Fix64 Distance(FixVector3 a, FixVector3 b)
        {
            return (a - b).Magnitude;
        }

        /// <summary>线性插值（**不钳制 t**，需要钳制请自己 `Clamp`）。</summary>
        /// <param name="a">起点。</param>
        /// <param name="b">终点。</param>
        /// <param name="t">插值系数。</param>
        /// <returns>结果。</returns>
        public static FixVector3 Lerp(FixVector3 a, FixVector3 b, Fix64 t)
        {
            return new FixVector3(
                Fix64.Lerp(a.X, b.X, t),
                Fix64.Lerp(a.Y, b.Y, t),
                Fix64.Lerp(a.Z, b.Z, t));
        }

        /// <summary>逐分量取小。</summary>
        /// <param name="a">a。</param>
        /// <param name="b">b。</param>
        /// <returns>结果。</returns>
        public static FixVector3 Min(FixVector3 a, FixVector3 b)
        {
            return new FixVector3(Fix64.Min(a.X, b.X), Fix64.Min(a.Y, b.Y), Fix64.Min(a.Z, b.Z));
        }

        /// <summary>逐分量取大。</summary>
        /// <param name="a">a。</param>
        /// <param name="b">b。</param>
        /// <returns>结果。</returns>
        public static FixVector3 Max(FixVector3 a, FixVector3 b)
        {
            return new FixVector3(Fix64.Max(a.X, b.X), Fix64.Max(a.Y, b.Y), Fix64.Max(a.Z, b.Z));
        }

        /// <summary>逐分量取绝对值。</summary>
        /// <param name="v">向量。</param>
        /// <returns>结果。</returns>
        public static FixVector3 Abs(FixVector3 v)
        {
            return new FixVector3(Fix64.Abs(v.X), Fix64.Abs(v.Y), Fix64.Abs(v.Z));
        }

        /// <summary>
        /// 把长度钳到 `maxLength` 以内（**超出才缩放，不超就原样返回**）。
        /// <para>
        /// ⚠️ 用**平方**比较，避免为了比较而开方 —— 开方既慢又有精度损失。
        /// </para>
        /// </summary>
        /// <param name="v">向量。</param>
        /// <param name="maxLength">最大长度。</param>
        /// <returns>结果。</returns>
        public static FixVector3 ClampMagnitude(FixVector3 v, Fix64 maxLength)
        {
            if (maxLength <= Fix64.Zero)
            {
                return Zero;
            }

            Fix64 sqrMagnitude = v.SqrMagnitude;
            Fix64 sqrMax = maxLength * maxLength;

            if (sqrMagnitude <= sqrMax)
            {
                return v;
            }

            Fix64 magnitude = Fix64.Sqrt(sqrMagnitude);

            if (magnitude.IsZero)
            {
                return Zero;
            }

            return v / magnitude * maxLength;
        }

        // ====================================================================
        //  调试
        // ====================================================================

        /// <summary>
        /// 调试文本。
        /// <para>⚠️ **只用于日志**，不要拿它做逻辑判断（它经过字符串格式化）。</para>
        /// </summary>
        /// <returns>可读文本。</returns>
        public override string ToString()
        {
            return "(" + X + ", " + Y + ", " + Z + ")";
        }
    }
}
