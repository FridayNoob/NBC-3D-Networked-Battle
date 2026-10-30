// 这个文件里有：
//   BattleRules（类）：两端共用的战斗规则常量，管普攻射程与冷却，并提供纯整数的圆形射程判定与仅供显示的距离换算。

// 本目录同时被 Unity（未开可空）与服务端（开了 Nullable enable）编译，两边可空规则不同，这里显式关掉让两端看到同一套规则。
#nullable disable

namespace NBC.Shared.Battle
{
    // 主要功能：给出两端必须算出同一结果的普攻规则（射程与冷却），并提供不开方的圆形射程判定与距离换算。
    // 主要数据成员：BasicAttackRangeMm（毫米，圆形半径）、BasicAttackCooldownTicks（tick，15 tick = 0.5 秒）。
    // 主要外部方法：WithinBasicAttackRange（射程判定唯一入口）、DistanceMmRounded（仅显示用）、Describe（一句人话）。
    // 谁在用它：服务端 DungeonBattle 的攻击判定与客户端 NetDebugWindow 的自动追击；服务端同名常量是指向本文件的别名。
    /// <summary>两端共用的战斗规则常量：普攻射程与冷却的真值，以及纯整数的圆形射程判定。</summary>
    public static class BattleRules
    {
        /// <summary>普攻射程，毫米，圆形半径；改它即改规则，客户端与服务端都不要再写第二份。</summary>
        public const int BasicAttackRangeMm = 2000;

        /// <summary>普攻冷却，tick（逻辑帧），15 tick = 0.5 秒（30Hz）；服务端据此拒绝过密的攻击请求。</summary>
        public const int BasicAttackCooldownTicks = 15;

        /// <summary>圆形射程判定：比较距离平方与射程平方，含边界、全程整数、不开方，两端必得同一结果。</summary>
        public static bool WithinBasicAttackRange(int axMm, int azMm, int bxMm, int bzMm)
        {
            // 功能：判断甲乙两点在毫米坐标下是否落在普攻射程圆内。
            // 入参：axMm / azMm 甲的 X / Z（毫米）；bxMm / bzMm 乙的 X / Z（毫米）。
            // 返回：距离平方小于等于射程平方时为 true，边界算在射程内。
            // 步骤：
            //   1) 坐标差先各转 long，毫米坐标放大后相减或平方很容易越过 int 上限
            //   2) 不比开方后的距离，直接比平方和，省掉取舍，两端结果必然一致
            //   3) 射程也取 long 再平方，与平方和使用同一类型比较
            // 注意：判的是圆不是正方形，别退回切比雪夫「max(|dx|,|dz|)」，对角方向会多出最多约 1.41 倍，表现为明明还远却打得到。
            // 注意：要与帧同步链保持同一个圆，WorldStep 用的是 SqrMagnitude <= range * range。
            // 上游：服务端 DungeonBattle 的攻击判定、客户端调试窗的自动追击
            // 下游：无
            long dx = (long)axMm - bxMm;
            long dz = (long)azMm - bzMm;
            long range = BasicAttackRangeMm;

            return (dx * dx) + (dz * dz) <= range * range;
        }

        /// <summary>整数欧氏距离（毫米、四舍五入），只供显示与日志使用，不能拿来当射程判据。</summary>
        public static int DistanceMmRounded(int axMm, int azMm, int bxMm, int bzMm)
        {
            // 功能：把两点毫米坐标的直线距离换算成四舍五入的整数毫米，供界面与日志显示。
            // 入参：axMm / azMm 甲的 X / Z（毫米）；bxMm / bzMm 乙的 X / Z（毫米）。
            // 返回：非负的整数毫米；两点重合时为 0。
            // 步骤：
            //   1) 坐标差转 long 后平方求和，交给纯整数开方，全程不碰浮点
            //   2) 开方自己实现，避免 Math.Sqrt 让一端引入浮点、两端结果漂移
            // 注意：四舍五入有取舍，刚好在 2000mm 边界附近会抖，判射程必须用 WithinBasicAttackRange。
            // 注意：不要换成 Math.Sqrt，两端只要有一端走浮点，就过不了项目的浮点闸门。
            // 上游：服务端射程外提示文案、客户端调试窗的距离显示
            // 下游：IntegerSqrtRounded
            long dx = (long)axMm - bxMm;
            long dz = (long)azMm - bzMm;

            return (int)IntegerSqrtRounded((dx * dx) + (dz * dz));
        }

        // 功能：用纯整数二分求平方根并四舍五入，作为 Math.Sqrt 的确定性替代。
        // 入参：square 被开方的非负数，单位是毫米的平方。
        // 返回：平方根四舍五入后的整数；square 小于等于 0 时返回 0。
        // 步骤：
        //   1) 非正数直接回 0，二分只处理正数
        //   2) 上限钳到 3037000499（根号 long.MaxValue 的下取整），二分一定收敛
        //   3) 二分找满足 low * low 小于等于 square 的最大 low，判据写成 mid <= square / mid
        //   4) 余数 square - low * low 大于 low 就进位，等价于比较 (low + 0.5) 的平方
        // 注意：判据必须写成 mid <= square / mid；写成 mid * mid <= square 时，square 接近 long.MaxValue 会溢出成负数，二分立刻走错方向。
        // 注意：上限必须钳住，若取 high = square（例如 long.MaxValue），high - low + 1 会先溢出，mid 变负数后永不收敛，直接死循环。
        // 上游：DistanceMmRounded
        // 下游：无
        private static long IntegerSqrtRounded(long square)
        {
            if (square <= 0)
            {
                return 0;
            }

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

            long remainder = square - (low * low);

            return remainder > low ? low + 1 : low;
        }

        // 功能：把当前射程与冷却拼成一句中文，供日志与界面直接显示。
        // 入参：无
        // 返回：形如「普攻：射程 2000mm，冷却 15 帧」的字符串；数值取自本文件常量，改常量即改文案。
        // 步骤：
        //   1) 按固定顺序拼两个常量，单位写死为毫米与帧，避免调用方各自补单位
        // 上游：暂无跨程序集调用点
        // 下游：无
        public static string Describe()
        {
            return "普攻：射程 " + BasicAttackRangeMm + "mm，冷却 " + BasicAttackCooldownTicks + " 帧";
        }
    }
}
