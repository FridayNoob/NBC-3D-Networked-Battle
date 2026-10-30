// 这个文件里有：
//   FixVector3（结构体）：确定性三维向量，三个分量都是 Fix64，加减乘除与开方全走整数运算。

using System;

namespace NBC.Shared
{
    // 主要功能：三维定点向量，提供分量运算、点积叉积、归一化、长度钳制与插值，整条运算链跨端位级别可复现。
    // 主要数据成员：X / Y / Z（Fix64，锁步世界坐标时单位米）、Zero / One / Up / Down / Forward / Right 六个常量向量。
    // 主要外部方法：Normalized / Dot / Cross / Distance / DistanceSquared / Lerp / Min / Max / Abs / ClampMagnitude，以及 + - * / == != 运算符。
    // 谁在用它：NBC.Shared 的 SimEntity.Position、WorldStep、LockstepScheduler、SpawnLayout 直接用它；NBC.Game 的 LockstepWire 从协议 raw 装回分量，LockstepSnapshotAdapter 再把位置换成毫米写快照；FixVector3Tests 逐项验算。
    // 注意：本程序集不引用 UnityEngine，这里刻意没有 ToVector3 / FromVector3；转 float 只由表现层单向做，绝不允许把 float 转回来继续参与逻辑，否则不确定性会进逻辑层。
    // 注意：三个分量都用 Fix64 而不是 float —— 向量运算里乘法与开方最多，正是浮点最先分叉的地方。
    /// <summary>确定性三维向量：三个分量都是 Fix64，不依赖 UnityEngine，整条运算链位级别可复现。</summary>
    public readonly struct FixVector3 : IEquatable<FixVector3>
    {
        /// <summary>X 分量；锁步世界坐标时单位米（Q32.32 定点，范围约 ±2147483647 米），方向向量时无量纲。</summary>
        public readonly Fix64 X;

        /// <summary>Y 分量；锁步世界坐标时单位米（范围同 X），方向向量时无量纲。</summary>
        public readonly Fix64 Y;

        /// <summary>Z 分量；锁步世界坐标时单位米（范围同 X），方向向量时无量纲；左手坐标系里 +Z 是前方。</summary>
        public readonly Fix64 Z;

        /// <summary>零向量 (0, 0, 0)；归一化零向量时返回它。</summary>
        public static readonly FixVector3 Zero = new FixVector3(Fix64.Zero, Fix64.Zero, Fix64.Zero);

        /// <summary>全 1 向量 (1, 1, 1)；长度是根号 3，不是单位向量。</summary>
        public static readonly FixVector3 One = new FixVector3(Fix64.One, Fix64.One, Fix64.One);

        /// <summary>单位向上 +Y（0, 1, 0）；方向向量，无量纲。</summary>
        public static readonly FixVector3 Up = new FixVector3(Fix64.Zero, Fix64.One, Fix64.Zero);

        /// <summary>单位向下 -Y（0, -1, 0）；方向向量，无量纲。</summary>
        public static readonly FixVector3 Down = new FixVector3(Fix64.Zero, Fix64.MinusOne, Fix64.Zero);

        /// <summary>单位向前 +Z（0, 0, 1）；左手坐标系，与 Unity 的 forward 同向。</summary>
        public static readonly FixVector3 Forward = new FixVector3(Fix64.Zero, Fix64.Zero, Fix64.One);

        /// <summary>单位向右 +X（1, 0, 0）；左手坐标系，与 Unity 的 right 同向。</summary>
        public static readonly FixVector3 Right = new FixVector3(Fix64.One, Fix64.Zero, Fix64.Zero);

        /// <summary>按三个定点分量构造，不做范围检查，单位由调用方约定。</summary>
        /// <param name="x">X 分量；锁步世界坐标用米，方向向量无量纲。</param>
        /// <param name="y">Y 分量，单位同 x。</param>
        /// <param name="z">Z 分量，单位同 x。</param>
        // 功能：用三个 Fix64 分量直接装成一个向量。
        // 入参：x / y / z 三个分量，单位由调用方约定（锁步世界坐标用米，方向向量无量纲）
        // 返回：无
        // 步骤：
        //   1) 三个分量原样赋值 —— 不做范围检查也不做单位换算，精度与单位由调用方保证
        // 上游：本文件的运算符与 ClampMagnitude 等返回值、NBC.Game 的 LockstepWire 从协议 raw 装回、FixVector3Tests
        // 下游：无
        public FixVector3(Fix64 x, Fix64 y, Fix64 z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>用三个整数分量构造，每个分量精确转成定点，不会丢精度。</summary>
        /// <param name="x">X 分量，单位由调用方约定；锁步世界坐标用米。</param>
        /// <param name="y">Y 分量，单位同 x。</param>
        /// <param name="z">Z 分量，单位同 x。</param>
        // 功能：把三个整数分量分别转成 Fix64 后装成向量。
        // 入参：x / y / z 三个整数分量，单位由调用方约定（锁步世界坐标用米）
        // 返回：无
        // 步骤：
        //   1) 三个整数各自走 Fix64.FromInt 左移 32 位 —— 整数到定点是精确的，不能先过浮点
        // 注意：必须走 FromInt；经 float 中转时超过 2^24 的整数会丢位，坐标直接不准。
        // 上游：SpawnLayout.PointAt 按整数米坐标建点、FixVector3Tests
        // 下游：Fix64.FromInt
        public FixVector3(int x, int y, int z)
        {
            X = Fix64.FromInt(x);
            Y = Fix64.FromInt(y);
            Z = Fix64.FromInt(z);
        }

        /// <summary>三个分量是否都为 0；逐分量精确比较，没有容差。</summary>
        // 功能：判断三个分量是否同时为 0。
        public bool IsZero
        {
            get { return X.IsZero && Y.IsZero && Z.IsZero; }
        }

        /// <summary>长度的平方；比较远近时优先用它，省一次开方，也没有开方误差。分量接近 Fix64 上限时平方和会饱和。</summary>
        // 功能：三个分量各自平方后相加，不做开方。
        public Fix64 SqrMagnitude
        {
            get { return X * X + Y * Y + Z * Z; }
        }

        /// <summary>长度；开方有舍入误差（初值约 2^-16，一次牛顿迭代后接近定点分辨率），只比大小时请用 SqrMagnitude。</summary>
        // 功能：先取 SqrMagnitude，再调 Fix64.Sqrt 开方。
        public Fix64 Magnitude
        {
            get { return Fix64.Sqrt(SqrMagnitude); }
        }

        /// <summary>归一化；零向量返回零向量而不抛异常，结果长度约为 1（受定点舍入影响）。</summary>
        /// <returns>单位向量；零向量时返回 Zero。</returns>
        // 功能：把向量缩放到长度 1，零向量原样返回零。
        // 入参：无（读自身）
        // 返回：单位向量，长度约 1；零向量返回 Zero，不抛异常，也不会产生 NaN（定点数没有 NaN）
        // 步骤：
        //   1) 先求长度 —— 零向量必须在除法之前单独放行，否则会除以 0
        //   2) 长度为 0 直接返回 Zero —— 零向量是正常情况（比如还没有移动方向），抛异常会逼每个调用点先判空，反而更容易漏
        //   3) 三个分量各除以长度 —— 定点除法位级别确定
        // 注意：零向量返回零向量是契约，不是漏判；调用方可以放心直接乘，不必先判空。
        // 上游：WorldStep.Step（把输入方向归一化后乘速度）
        // 下游：Magnitude、Fix64 除法
        public FixVector3 Normalized()
        {
            Fix64 length = Magnitude;

            if (length.IsZero)
            {
                return Zero;
            }

            return new FixVector3(X / length, Y / length, Z / length);
        }

        /// <summary>向量加法，三个分量逐项相加；分量溢出由 Fix64 饱和，不回绕。</summary>
        // 功能：两个向量逐分量相加。
        // 入参：a 左操作数；b 右操作数（两者单位必须一致，米加米，方向加方向）
        // 返回：逐分量和
        // 步骤：
        //   1) 三个分量分别相加 —— 分量之间互不影响，顺序固定为 X、Y、Z；溢出由 Fix64 饱和
        // 上游：WorldStep.Step（位置 + 一步位移）、FixVector3Tests
        // 下游：Fix64 加法
        public static FixVector3 operator +(FixVector3 a, FixVector3 b)
        {
            return new FixVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        /// <summary>向量减法，三个分量逐项相减；分量溢出由 Fix64 饱和，不回绕。</summary>
        // 功能：两个向量逐分量相减。
        // 入参：a 左操作数；b 右操作数（两者单位必须一致）
        // 返回：逐分量差
        // 步骤：
        //   1) 三个分量分别相减 —— 分量之间互不影响，顺序固定为 X、Y、Z；溢出由 Fix64 饱和
        // 上游：WorldStep.Step（目标位置 - 自身位置）、Distance、DistanceSquared
        // 下游：Fix64 减法
        public static FixVector3 operator -(FixVector3 a, FixVector3 b)
        {
            return new FixVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        /// <summary>逐分量取负；Fix64 最小值取负会饱和到最大值。</summary>
        // 功能：把三个分量都取相反数。
        // 入参：v 原向量（单位不变）
        // 返回：相反向量
        // 步骤：
        //   1) 三个分量分别取负 —— Fix64 最小值取负会饱和到最大值，不回绕
        // 上游：FixVector3Tests、调用方要反向向量时
        // 下游：Fix64 取负
        public static FixVector3 operator -(FixVector3 v)
        {
            return new FixVector3(-v.X, -v.Y, -v.Z);
        }

        /// <summary>标量乘向量，三个分量分别乘同一个标量；溢出由 Fix64 饱和。</summary>
        // 功能：标量在前，逐分量乘出向量。
        // 入参：scalar 标量（可负，单位由调用方约定）；v 向量
        // 返回：逐分量乘积
        // 步骤：
        //   1) 三个分量分别乘 scalar —— 标量在前只为让 2 * v 也能写，算法与 v * 2 完全一致
        // 上游：WorldStep.Step（归一化方向 * 速度）、FixVector3Tests
        // 下游：Fix64 乘法
        public static FixVector3 operator *(Fix64 scalar, FixVector3 v)
        {
            return new FixVector3(v.X * scalar, v.Y * scalar, v.Z * scalar);
        }

        /// <summary>标量乘向量，三个分量分别乘同一个标量；溢出由 Fix64 饱和。</summary>
        // 功能：标量在后，逐分量乘出向量。
        // 入参：v 向量；scalar 标量（可负，单位由调用方约定）
        // 返回：逐分量乘积
        // 步骤：
        //   1) 三个分量分别乘 scalar —— 与标量在前的重载同一算法，避免两个方向的行为漂移
        // 上游：ClampMagnitude（v / 长度 * 上限）、FixVector3Tests
        // 下游：Fix64 乘法
        public static FixVector3 operator *(FixVector3 v, Fix64 scalar)
        {
            return new FixVector3(v.X * scalar, v.Y * scalar, v.Z * scalar);
        }

        /// <summary>数除，三个分量分别除以标量；标量为 0 时抛 DivideByZeroException。</summary>
        // 功能：向量逐分量除以一个标量。
        // 入参：v 被除向量；scalar 除数（不能为 0）
        // 返回：逐分量商
        // 步骤：
        //   1) 先判 scalar 是否为 0 —— 除零是逻辑错误，当场抛异常，不做饱和
        //   2) 三个分量分别除以 scalar —— 定点除法向零截断，结果位级别确定
        // 注意：scalar 为 0 一定抛异常；别拿未初始化的方向或速度为 0 当除数。
        // 上游：Normalized、ClampMagnitude
        // 下游：Fix64 除法
        public static FixVector3 operator /(FixVector3 v, Fix64 scalar)
        {
            if (scalar.IsZero)
            {
                throw new DivideByZeroException("[FixVector3] 向量不能除以 0。");
            }

            return new FixVector3(v.X / scalar, v.Y / scalar, v.Z / scalar);
        }

        /// <summary>逐分量严格相等；定点数按原始值比较，没有容差。</summary>
        // 功能：判断两个向量的三个分量是否全部相等。
        // 入参：a 左操作数；b 右操作数
        // 返回：三个分量都相等为 true，任一不等为 false
        // 步骤：
        //   1) 三个分量依次比较，全部相等才为 true —— 短路求值，第一个不等就返回
        // 注意：这是精确比较，不设 epsilon；差一个最小定点单位（1/2^32）也算不等，要近似相等得自己算差值。
        // 上游：Equals、FixVector3Tests、状态比较
        // 下游：Fix64 相等
        public static bool operator ==(FixVector3 a, FixVector3 b)
        {
            return a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        }

        /// <summary>逐分量不等，等价于对 == 取反。</summary>
        // 功能：判断两个向量是否至少有一个分量不等（直接对 == 取反）。
        public static bool operator !=(FixVector3 a, FixVector3 b)
        {
            return !(a == b);
        }

        /// <summary>逐分量相等，与 == 同一判据；实现 IEquatable 让泛型集合不走装箱。</summary>
        // 功能：转调 == 比较两个向量。
        public bool Equals(FixVector3 other)
        {
            return this == other;
        }

        /// <summary>装箱比较：obj 是 FixVector3 且逐分量相等时为 true。</summary>
        /// <param name="obj">待比较对象，可为 null 或其它类型。</param>
        /// <returns>是 FixVector3 且三个分量都相等为 true；null 或类型不符为 false。</returns>
        // 功能：先认类型，再转泛型 Equals 比较分量。
        // 入参：obj 任意对象（可为 null）
        // 返回：obj 是 FixVector3 且分量相等为 true；null 或类型不符为 false
        // 步骤：
        //   1) 先用 is 判类型 —— 不是 FixVector3 直接 false，避免拆箱异常
        //   2) 再转泛型 Equals 比较分量 —— 判据仍然只有一份
        // 注意：null 是 false，不等于零向量；零向量要和 Zero 逐分量相等才算。
        // 上游：object 比较、非泛型集合
        // 下游：Equals(FixVector3)
        public override bool Equals(object obj)
        {
            return obj is FixVector3 && Equals((FixVector3)obj);
        }

        /// <summary>三个分量按固定顺序混合出的哈希；相等向量的哈希一定相同。</summary>
        /// <returns>哈希值；不等向量也可能碰撞，不能反过来用哈希判等。</returns>
        // 功能：把三个分量的哈希混成一个整数。
        // 入参：无（读自身）
        // 返回：哈希值；相等向量必得同一哈希，不等向量也可能碰撞
        // 步骤：
        //   1) 先取 X 的哈希
        //   2) 依次把 Y、Z 混进来（每次乘 397 再异或）—— 顺序固定，相等向量才会落到同一个值
        // 注意：哈希只用于查表；向量是否相等要用 == 或 Equals 判，不能拿哈希代替。
        // 上游：Dictionary / HashSet 等哈希容器
        // 下游：Fix64.GetHashCode
        public override int GetHashCode()
        {
            int h = X.GetHashCode();
            h = (h * 397) ^ Y.GetHashCode();
            h = (h * 397) ^ Z.GetHashCode();
            return h;
        }

        /// <summary>点积；结果是两向量长度与夹角余弦的乘积，不是夹角。</summary>
        // 功能：三个分量逐对相乘再求和。
        // 入参：a 第一个向量；b 第二个向量（单位需一致）
        // 返回：点积，量纲是两向量单位之积（同为米时是平方米）
        // 步骤：
        //   1) 三个分量逐对相乘后相加 —— 定点乘加不开方，结果确定
        // 注意：不要拿点积当夹角大小 —— 它还要乘两个长度；要夹角得先各自归一化再 Dot。
        // 上游：调用方做朝向判断或投影（目前只有 FixVector3Tests 直接调）
        // 下游：Fix64 乘法与加法
        public static Fix64 Dot(FixVector3 a, FixVector3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        /// <summary>叉积，按左手坐标系（与 Unity 一致）计算。</summary>
        // 功能：算出同时垂直于 a 与 b 的向量。
        // 入参：a 第一个向量；b 第二个向量（单位需一致）
        // 返回：叉积向量；a 与 b 严格成比例（含任一为零向量）时为零向量
        // 步骤：
        //   1) 按左手坐标系的展开式逐分量算 —— 每个分量都是另外两轴的交叉项相减
        //   2) 不交换 a 与 b —— 交换会得到反方向
        // 注意：这是左手坐标系（与 Unity 一致），方向和右手系的教科书公式相反；照抄右手公式会让朝向整体翻面。
        // 上游：调用方算朝向或平面法线（目前只有 FixVector3Tests 直接调）
        // 下游：Fix64 乘法与减法
        public static FixVector3 Cross(FixVector3 a, FixVector3 b)
        {
            return new FixVector3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        /// <summary>两点距离的平方；比较远近时优先用它。</summary>
        // 功能：先做差向量，再取长度的平方。
        // 入参：a 起点；b 终点（单位需一致，锁步世界坐标用米）
        // 返回：距离平方，非负；坐标用米时单位是平方米
        // 步骤：
        //   1) 先算 a - b 得差向量 —— 差向量与输入同单位（米）
        //   2) 再取 SqrMagnitude —— 不开方，只比远近时没有开方误差
        // 注意：只比远近就用它，别先开方再比；平方和接近 Fix64 上限时会饱和，坐标量级要心里有数。
        // 上游：调用方比射程或远近（目前只有 FixVector3Tests 直接调；WorldStep 的射程判定是手写差向量加 SqrMagnitude）
        // 下游：operator -、SqrMagnitude
        public static Fix64 DistanceSquared(FixVector3 a, FixVector3 b)
        {
            return (a - b).SqrMagnitude;
        }

        /// <summary>两点距离；坐标用米时单位米。</summary>
        // 功能：先做差向量，再取长度。
        // 入参：a 起点；b 终点（单位需一致，锁步世界坐标用米）
        // 返回：距离，非负；同一点为 0
        // 步骤：
        //   1) 先算 a - b 得差向量
        //   2) 再取 Magnitude —— 开方有舍入误差
        // 注意：只比远近请用 DistanceSquared；这里多一次开方，也多一份误差。
        // 上游：调用方要真实距离时（目前只有 FixVector3Tests 直接调）
        // 下游：operator -、Magnitude
        public static Fix64 Distance(FixVector3 a, FixVector3 b)
        {
            return (a - b).Magnitude;
        }

        /// <summary>线性插值，不钳制 t；t 为 0 得 a、为 1 得 b。</summary>
        // 功能：按 t 在 a 与 b 之间逐分量插值。
        // 入参：a 起点；b 终点；t 插值系数（不钳制，0 得 a、1 得 b，超出 0..1 会外插）
        // 返回：插值结果
        // 步骤：
        //   1) 三个分量分别调 Fix64.Lerp —— 三个轴用同一个 t，不会出现某轴不同步
        //   2) 本方法不钳 t —— 钳制属于调用方语义，这里只做纯粹插值
        // 注意：传 2 会得到 b 之外的点；需要钳制请调用方自己先 Clamp。
        // 上游：调用方做平滑或插值（目前只有 FixVector3Tests 直接调）
        // 下游：Fix64.Lerp
        public static FixVector3 Lerp(FixVector3 a, FixVector3 b, Fix64 t)
        {
            return new FixVector3(
                Fix64.Lerp(a.X, b.X, t),
                Fix64.Lerp(a.Y, b.Y, t),
                Fix64.Lerp(a.Z, b.Z, t));
        }

        /// <summary>逐分量取小。</summary>
        // 功能：三个分量分别取较小值。
        // 入参：a 第一个向量；b 第二个向量（单位需一致）
        // 返回：逐分量较小的向量
        // 步骤：
        //   1) 三个分量各调 Fix64.Min —— 分量之间不互相比较
        // 上游：调用方做包围盒或范围钳制（目前只有 FixVector3Tests 直接调）
        // 下游：Fix64.Min
        public static FixVector3 Min(FixVector3 a, FixVector3 b)
        {
            return new FixVector3(Fix64.Min(a.X, b.X), Fix64.Min(a.Y, b.Y), Fix64.Min(a.Z, b.Z));
        }

        /// <summary>逐分量取大。</summary>
        // 功能：三个分量分别取较大值。
        // 入参：a 第一个向量；b 第二个向量（单位需一致）
        // 返回：逐分量较大的向量
        // 步骤：
        //   1) 三个分量各调 Fix64.Max —— 分量之间不互相比较
        // 上游：调用方做包围盒或范围钳制（目前只有 FixVector3Tests 直接调）
        // 下游：Fix64.Max
        public static FixVector3 Max(FixVector3 a, FixVector3 b)
        {
            return new FixVector3(Fix64.Max(a.X, b.X), Fix64.Max(a.Y, b.Y), Fix64.Max(a.Z, b.Z));
        }

        /// <summary>逐分量取绝对值；Fix64 最小值取绝对值会饱和到最大值。</summary>
        // 功能：三个分量分别取绝对值。
        // 入参：v 原向量（单位不变）
        // 返回：逐分量绝对值，非负
        // 步骤：
        //   1) 三个分量各调 Fix64.Abs —— 最小值取绝对值的饱和由 Fix64 处理
        // 注意：绝对值会抹掉方向信息，只该用在长度、误差这类场景。
        // 上游：调用方算误差或绝对值（目前只有 FixVector3Tests 直接调）
        // 下游：Fix64.Abs
        public static FixVector3 Abs(FixVector3 v)
        {
            return new FixVector3(Fix64.Abs(v.X), Fix64.Abs(v.Y), Fix64.Abs(v.Z));
        }

        /// <summary>把长度钳到 maxLength 以内：超出才缩放，不超原样返回；maxLength 小于等于 0 时返回零。</summary>
        /// <param name="v">原向量，单位不变；零向量直接返回 Zero。</param>
        /// <param name="maxLength">最大长度，必须为正；小于等于 0 按 0 处理，返回 Zero。</param>
        /// <returns>长度不超过 maxLength 的向量；v 为零向量时返回 Zero。</returns>
        // 功能：长度超过上限时按比例缩回，没超过就原样返回。
        // 入参：v 原向量（单位不变）；maxLength 最大长度（小于等于 0 按 0 处理）
        // 返回：长度不超过 maxLength 的向量；v 为零向量或 maxLength 小于等于 0 时返回 Zero
        // 步骤：
        //   1) 先处理 maxLength 小于等于 0 —— 负数上限没有意义，统一返回 Zero，避免后面的平方比较出现反向
        //   2) 用平方比较 v.SqrMagnitude 与 maxLength 的平方 —— 不超就原样返回，省一次开方也没有开方误差
        //   3) 只有超了才开方求真实长度，再按 maxLength / length 缩放
        //   4) 长度为 0 时返回 Zero —— 防除零；正常路径在第 2 步已经返回，这一步是兜底
        // 注意：比较全用平方，别改成先开方再比；maxLength 为负按 0 处理是契约，不是漏判。
        // 上游：调用方钳制位移或速度上限（目前只有 FixVector3Tests 直接调）
        // 下游：SqrMagnitude、Fix64.Sqrt、operator /、operator *
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

        /// <summary>调试文本，形如 (X, Y, Z)，各分量保留 6 位小数；只用于日志，不能参与逻辑判断。</summary>
        /// <returns>可读文本，例如 (1.000000, 2.000000, 3.000000)。</returns>
        // 功能：把三个分量拼成日志用的可读文本。
        // 入参：无（读自身）
        // 返回：形如 (X, Y, Z) 的字符串，分量顺序固定，各分量由 Fix64.ToString 保留 6 位小数
        // 步骤：
        //   1) 依次拼左括号、X、Y、Z、右括号 —— 顺序固定，方便日志对照
        //   2) 分量文本交给 Fix64.ToString —— 定点转字符串不经过浮点，跨平台输出一致
        // 注意：只用于日志或调试，不要拿字符串做逻辑判断（格式一变判断就错）。
        // 上游：日志与调试输出、FixVector3Tests
        // 下游：Fix64.ToString
        public override string ToString()
        {
            return "(" + X + ", " + Y + ", " + Z + ")";
        }
    }
}
