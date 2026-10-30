// 这个文件里有：
//   DamageOutcome（结构体）：一次伤害结算的四个输出，实扣的血、剩余血、过量伤害、是否致死。
//   DamageMath（静态类）：伤害结算的唯一入口，纯整数运算，客户端与服务端编同一份源码。
//
// 注意：东西放这里的判据是「两端都必须算出同一个结果」—— 伤害结算属于这一类，刷怪 / 输入 / 表现 / UI 不属于。
// 注意：战斗数值一律整数 + 单位（点），这里出现浮点就是 bug：两端运行时的舍入可能不同，一次不同之后每一步都会放大，而且不报错（这条是需求侧定的，见 Docs\17）。
// 注意：HP 扣到 0 就停、不出负血；超出剩余血的部分单独记进 Overkill（伤害数字与溢出统计要用）；打 0 血目标什么都不发生且不算致死，否则击杀数会凭空多。
// 注意：非法输入一律抛异常、不静默修正 —— 负 HP 说明上一处忘了钳位，负伤害要么是治疗要么是配表错，静默归零会把问题变成悬案。

// 注意：本目录与 Shared\Condition 同理 —— Unity 侧未开可空、服务端开了（Server\Directory.Build.props），这里显式关掉可空检查，让两端按同一套规则编译；代价是本层没有可空静态检查（详见 ConditionTracker.cs 顶部）。
#nullable disable

using System;

namespace NBC.Shared.Battle
{
    // 主要功能：承载一次伤害结算的四个输出，只读；构造时不校验，数值由 DamageMath 算好。
    // 主要数据成员：m_applied（实扣，点）、m_remainingHp（剩余血，点）、m_overkill（过量，点）、m_isLethal（是否致死）。
    // 主要外部方法：Applied / RemainingHp / Overkill / IsLethal 四个只读属性，以及 ToString（日志用）。
    // 谁在用它：只由 DamageMath.Resolve 构造；NBC.Game 的 BattleAgent / BattleWorld、共享层 WorldStep、服务端 DungeonBattle 读它的属性。
    /// <summary>一次伤害结算的四个输出，构造后不可变。</summary>
    public readonly struct DamageOutcome
    {
        // 实际扣掉的血，点，取值范围 0..结算前的 HP。
        private readonly int m_applied;

        // 结算后的 HP，点，≥ 0（扣到 0 就停，不会出现负血）。
        private readonly int m_remainingHp;

        // 过量伤害，点，≥ 0；打 0 血目标时为 0，对尸体不记过量。
        private readonly int m_overkill;

        // 这一下是否致死，无单位；只有把大于 0 的血打到 0 才为真，0 伤害与打尸体都为 false。
        private readonly bool m_isLethal;

        // 功能：把已经算好的四个数装进结果对象，不校验也不钳位。
        // 入参：applied 实扣（点，0..结算前的 HP）；remainingHp 剩余血（点，≥ 0）；overkill 过量（点，≥ 0）；isLethal 是否致死
        // 返回：无
        // 步骤：
        //   1) 四个字段按参数原样赋值 —— 数值在 Resolve 里已经算好、钳好，这里再判一次只会把规则散成两处
        // 上游：DamageMath.Resolve
        // 下游：无
        internal DamageOutcome(int applied, int remainingHp, int overkill, bool isLethal)
        {
            m_applied = applied;
            m_remainingHp = remainingHp;
            m_overkill = overkill;
            m_isLethal = isLethal;
        }

        /// <summary>实际扣掉的血（点），不超过结算前的 HP。</summary>
        public int Applied
        {
            get { return m_applied; }
        }

        /// <summary>结算后的 HP（点），已钳到 ≥ 0。</summary>
        public int RemainingHp
        {
            get { return m_remainingHp; }
        }

        /// <summary>打出去超过剩余血的部分（点）；打尸体时为 0。</summary>
        public int Overkill
        {
            get { return m_overkill; }
        }

        /// <summary>这一下致死吗；0 伤害与打尸体都为 false。</summary>
        public bool IsLethal
        {
            get { return m_isLethal; }
        }

        // 功能：把四个字段拼成一句人话，供日志与用例断言直接读。
        // 入参：无
        // 返回：形如 -80（剩 220）的一行；过量大于 0 时追加「，过量 12」，致死时追加「，致死」
        // 步骤：
        //   1) 先拼实扣与剩余血，这两段必有
        //   2) 过量只在大于 0 时追加 —— 否则每次结算都拖一个「过量 0」的噪音
        //   3) 致死同理，只在为真时追加
        // 上游：日志；用例 DamageMathTests 拿它做断言
        // 下游：无
        /// <summary>转成一行可读文本，供日志与断言使用。</summary>
        /// <returns>形如 -80（剩 220）；有过量或致死时追加，例如「，过量 12」「，致死」。</returns>
        public override string ToString()
        {
            return "-" + m_applied + "（剩 " + m_remainingHp + "）" +
                   (m_overkill > 0 ? "，过量 " + m_overkill : string.Empty) +
                   (m_isLethal ? "，致死" : string.Empty);
        }
    }

    // 主要功能：纯整数的伤害结算，客户端与服务端编同一份源码，同一输入必定得到同一结果。
    // 主要数据成员：无（无状态静态类，不缓存任何东西）。
    // 主要外部方法：Resolve（唯一入口）。
    // 谁在用它：NBC.Game 的 BattleAgent.ApplyDamage、共享层 WorldStep、服务端 DungeonBattle.ApplyDamage、EditMode 的 DamageMathTests。
    /// <summary>伤害结算：整数运算、纯函数，双端同一份规则。</summary>
    public static class DamageMath
    {
        // 功能：把一次伤害结算成实扣、剩余血、过量伤害、是否致死四个数。
        // 入参：currentHp 结算前的 HP（点，必须 ≥ 0）；damage 本次打出的伤害（点，必须 ≥ 0）
        // 返回：DamageOutcome，四个字段都已钳好；剩余血一定 ≥ 0，致死只在「把还有血的目标打到 0」时为真
        // 步骤：
        //   1) 先校验两个入参，负值一律抛 ArgumentOutOfRangeException —— 静默修正会把上一处的漏钳藏成悬案
        //   2) currentHp 为 0 时直接返回全 0 且非致死 —— 打尸体什么都不发生，判致死会让击杀数凭空多
        //   3) 实扣取 damage 与 currentHp 的较小者，保证血不会变负
        //   4) 超出实扣的部分记进 Overkill —— 伤害数字与溢出统计要用；第 2 步的返回值为 0，与尸体不记过量一致
        //   5) 致死 = 实扣等于结算前 HP；第 2 步已把 0 血拦掉，这里不用再判
        // 上游：NBC.Game 的 BattleAgent.ApplyDamage、共享层 WorldStep、服务端 DungeonBattle.ApplyDamage、用例 DamageMathTests
        // 下游：只造 DamageOutcome，不调任何方法
        // 注意：两端编译同一份，任何一步改成浮点，极端值上两边会永久分叉且不报错。
        /// <summary>结算一次伤害，双端必须得出同一结果。</summary>
        /// <param name="currentHp">结算前的 HP（点），必须 ≥ 0；为负直接抛异常，不静默修正。</param>
        /// <param name="damage">本次打出的伤害（点），必须 ≥ 0；负数同样直接抛异常。</param>
        /// <returns>实扣、剩余血、过量伤害、是否致死；剩余血已钳到 ≥ 0。</returns>
        public static DamageOutcome Resolve(int currentHp, int damage)
        {
            if (currentHp < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(currentHp),
                    "[DamageMath] 结算前的 HP 不能是负数（当前 " + currentHp + "）。\n" +
                    "HP 变负说明**上一个扣血的地方忘了钳位** —— 修那里，不要在这里圆回来。" +
                    "（负血会让 UI 出现负的血条，也会让「已经死了」变成一道算术题。）");
            }

            if (damage < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(damage),
                    "[DamageMath] 伤害不能是负数（当前 " + damage + "）。\n" +
                    "负伤害要么是「治疗」（那应该另有一个函数），要么是配置填错" +
                    "（`Skill.damage` 的规则行是 `min(0)`）。当成 0 处理会让" +
                    "「这技能怎么不掉血」变成悬案。");
            }

            // 打 0 血目标：什么都不发生，且不算致死 —— 否则尸体再死一次，任务 / 成就里的击杀数会凭空多。
            if (currentHp == 0)
            {
                return new DamageOutcome(0, 0, 0, false);
            }

            // 实扣不超过剩余血；超出的部分单独记进 Overkill
            int applied = damage > currentHp ? currentHp : damage;

            return new DamageOutcome(applied, currentHp - applied, damage - applied, applied == currentHp);
        }
    }
}
