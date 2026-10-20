// ============================================================================
//  BattleRules —— **两端共用的战斗规则常量**（M4-S1b）
//  项目：3D联网战斗Demo   对应：D1（"两端必须算出同一个值的东西放共享层"）
//
//  ---------------------------------------------------------------------------
//  为什么这两个数必须住在这里（而不是各写一份）
//  ---------------------------------------------------------------------------
//  它们原来是服务端 `DungeonBattle` 里的两个 `const`。客户端本来不需要它们 ——
//  直到 2026-09-26 负责人实测"目标不在范围内，难测"，我给编辑器调试窗加了
//  「自动追击并攻击」：它得知道"多近才算够得着""按一下之后要等多久"。
//
//  这时有两条路：
//      · 在窗口里**再写一份** 2000 / 15 —— 看着最省事，代价是**两端会漂移**：
//        服务端哪天把射程改成 2500，窗口还在 2000 处按键，表现是"走到一半就停下不打"，
//        而且**不报错**（本项目对"静默不一致"有明确规矩：它就是 D1 要防的那类东西）。
//      · 放进共享层，**两端引用同一个常量** —— 就是这里的做法。
//
//  ⚠️ 判据（沿用 D1）：**"两端都必须算出同一个值"的东西才放共享层**。
//     射程与冷却正是这种东西：服务端用它判定"打不打得到"，客户端用它决定"走到哪儿停"。
//
//  ⚠️ 这里的值**必须与服务端的实际判定一致**：改这里 = 改规则。
//     服务端 `DungeonBattle.BasicAttackRangeMm` / `BasicAttackCooldownTicks`
//     现在是**指向本文件**的别名（const 引用 const），所以只可能有一个真值。
// ============================================================================

// 与 Shared\Condition\、Shared\Battle\ 同一处理由：本目录在 Unity（未开可空）
// 与服务端（开了 <Nullable>enable</Nullable>）下规则不同，显式关掉让两端看到同一套规则。
#nullable disable

namespace NBC.Shared.Battle
{
    /// <summary>两端共用的战斗规则常量。</summary>
    public static class BattleRules
    {
        /// <summary>
        /// 普攻射程（毫米）。**圆形射程**（2026-10-20 起）：判定请用 <see cref="WithinBasicAttackRange"/>，
        /// 它比的是**距离平方**（整数精确、不开方、两端一致）。
        /// <para>⚠️ 别再用切比雪夫距离（`max(|dx|,|dz|)`）判射程：那是**正方形**，对角方向会多出
        /// 最多 √2 ≈ 1.41 倍 —— 表现"明明还远却打得到"。服务端与客户端调试窗都已改为本判据。</para>
        /// <para>⚠️ 与帧同步链一致：`WorldStep` 用的是 `SqrMagnitude &lt;= range*range`，同一个圆。</para>
        /// </summary>
        public const int BasicAttackRangeMm = 2000;

        /// <summary>普攻冷却（逻辑帧）。15 帧 = 0.5 秒（30Hz）。服务端据此拒绝过密的攻击请求。</summary>
        public const int BasicAttackCooldownTicks = 15;

        /// <summary>
        /// **圆形射程判定**：距离平方 ≤ 射程平方（**不开方** ⇒ 整数精确、两端必算出同一个结果）。
        /// <para>⚠️ 用 `long` 算平方：毫米坐标放大后 `dx*dx` 很容易越过 `int` 上限。</para>
        /// </summary>
        /// <param name="axMm">甲 X（毫米）。</param>
        /// <param name="azMm">甲 Z（毫米）。</param>
        /// <param name="bxMm">乙 X（毫米）。</param>
        /// <param name="bzMm">乙 Z（毫米）。</param>
        /// <returns>在射程内返回 true。</returns>
        public static bool WithinBasicAttackRange(int axMm, int azMm, int bxMm, int bzMm)
        {
            long dx = (long)axMm - bxMm;
            long dz = (long)azMm - bzMm;
            long range = BasicAttackRangeMm;

            return (dx * dx) + (dz * dz) <= range * range;
        }

        /// <summary>
        /// 整数欧氏距离（毫米，四舍五入）—— **只给显示/日志用**。
        /// <para>⚠️ 别拿它做判定：判定请用 <see cref="WithinBasicAttackRange"/>（无取舍）。
        /// 开方即使四舍五入也有取舍，拿它判边界会在"刚好 2000mm"处抖。</para>
        /// <para>为什么不用 `Math.Sqrt`：一端浮点就可能两端不一致（本项目还有浮点闸门）。</para>
        /// </summary>
        /// <param name="axMm">甲 X（毫米）。</param>
        /// <param name="azMm">甲 Z（毫米）。</param>
        /// <param name="bxMm">乙 X（毫米）。</param>
        /// <param name="bzMm">乙 Z（毫米）。</param>
        /// <returns>距离（毫米）。</returns>
        public static int DistanceMmRounded(int axMm, int azMm, int bxMm, int bzMm)
        {
            long dx = (long)axMm - bxMm;
            long dz = (long)azMm - bzMm;

            return (int)IntegerSqrtRounded((dx * dx) + (dz * dz));
        }

        /// <summary>整数平方根（四舍五入，纯整数二分）—— `Math.Sqrt` 的确定性替代品。</summary>
        /// <param name="square">被开方的非负数。</param>
        /// <returns>平方根（四舍五入）。</returns>
        private static long IntegerSqrtRounded(long square)
        {
            if (square <= 0)
            {
                return 0;
            }

            // ⚠️ 用 `mid <= square / mid` 而不是 `mid * mid <= square`：后者在 square 接近
            //    long.MaxValue 时会溢出成负数，二分立刻走错方向。
            // ⚠️ 上限必须**钳住**：若 `high = square`（比如 long.MaxValue），`high - low + 1` 会先溢出，
            //    mid 变负数后永不收敛 ⇒ **死循环**（2026-10-20 被独立验收实测抓到）。上限取 ⌊√long.MaxValue⌋ 就够。
            const long MaxRoot = 3037000499;

            long low = 0;
            long high = square < MaxRoot ? square : MaxRoot;

            while (low < high)
            {
                long mid = low + ((high - low + 1) / 2);

                if (mid <= square / mid)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            // 四舍五入：(low + 0.5)² = low² + low + 0.25 ⇒ 余数 > low 就进位
            long remainder = square - (low * low);

            return remainder > low ? low + 1 : low;
        }

        /// <summary>转成一句人话（日志/界面用）。</summary>
        /// <returns>描述。</returns>
        public static string Describe()
        {
            return "普攻：射程 " + BasicAttackRangeMm + "mm，冷却 " + BasicAttackCooldownTicks + " 帧";
        }
    }
}
