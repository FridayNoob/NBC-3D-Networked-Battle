// ============================================================================
//  DamageMath —— 伤害结算（**共享层**：PVP 帧同步时两端必须算出同一个结果）
//  项目：3D联网战斗Demo   对应：M2-B1、需求 §6.1（帧同步）、Docs\17 §十二 决定 2
//
//  ---------------------------------------------------------------------------
//  为什么这一个文件要放共享层（判据要写清楚，否则会变成"什么都往里塞"）
//  ---------------------------------------------------------------------------
//  **判据：两端都必须算出同一个结果的东西，才放共享层。**
//
//      ✅ 伤害结算、HP、命中判定 —— PVP 走帧同步（Q2 的决定），
//         客户端 A 算出"这一下打掉 80 血"、客户端 B 算出 81，两边就**永久分叉**，
//         而且**不会报任何错**（D2 的定点数就是为这件事存在的）
//      ❌ 刷怪、输入、表现、UI —— 只有一端会做，放共享层只是徒增约束
//
//  所以这里只有"整数 + 钳位"，一行引擎代码都没有，也没有一个浮点。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么伤害是 `int` 而不是 `float`（这条是需求侧定的，不是这里临时决定）
//  ---------------------------------------------------------------------------
//  `Docs\17` §十二 决定 2：**战斗数值禁止 `float`**，改用
//  整数 + 单位（万分比 / 毫米 / 毫秒）。原因是浮点在两个运行时下的
//  舍入可能不同 —— 一次不同之后**每一步都会放大**（D2 的原话）。
//  所以本文件里出现 `float` 就是 bug。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 三条写死的结算规则
//  ---------------------------------------------------------------------------
//  ① **HP 钳在 0**：扣完就是 0，不会出现 -20（负血会让"已经死了"变成一道算术题，
//     而且 UI 上会出现负的血条）
//  ② **过量伤害要单独算出来**（`Overkill`）：它有用 —— 伤害数字要显示"这一下多疼"、
//     统计要做"溢出伤害"、以后有"斩杀"类技能也要看它
//  ③ **打已经死掉的目标 = 什么都不发生**，并且**不算致死**
//     （否则"尸体"会再死一次，任务/成就里的击杀数就会凭空多出来）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 非法输入一律**抛异常**，不静默修正
//  ---------------------------------------------------------------------------
//  · `currentHp < 0`：说明**上一个扣血的地方忘了钳位** —— 报出来，别帮我圆
//  · `damage < 0`：负伤害要么是"治疗"（那应该有另一个函数），
//    要么是策划表填错（`Skill.damage` 的规则行是 `min(0)`）。
//    静默当成 0 会让"这技能怎么不掉血"变成悬案。
// ============================================================================

// 与 Shared\Condition\ 同一处理由：本目录在 Unity（未开可空）与服务端
// （`Server\Directory.Build.props` 开了 `<Nullable>enable</Nullable>`）下规则不同。
// 显式关掉，让**两端看到同一套规则**（详见 ConditionTracker.cs 顶部那段）。
#nullable disable

using System;

namespace NBC.Shared.Battle
{
    /// <summary>一次伤害结算的结果（纯数据）。</summary>
    public readonly struct DamageOutcome
    {
        /// <summary>**实际扣掉**的血（0 ~ 结算前的 HP）。</summary>
        private readonly int m_applied;

        /// <summary>结算后的 HP（**一定 ≥ 0**）。</summary>
        private readonly int m_remainingHp;

        /// <summary>过量伤害（打出去超过剩余血的那部分）。</summary>
        private readonly int m_overkill;

        /// <summary>这一下是否**致死**（把目标从"还活着"打成 0）。</summary>
        private readonly bool m_isLethal;

        /// <summary>造一个结算结果（只有 <see cref="DamageMath"/> 会用）。</summary>
        /// <param name="applied">实际扣掉的血。</param>
        /// <param name="remainingHp">结算后的 HP。</param>
        /// <param name="overkill">过量伤害。</param>
        /// <param name="isLethal">是否致死。</param>
        internal DamageOutcome(int applied, int remainingHp, int overkill, bool isLethal)
        {
            m_applied = applied;
            m_remainingHp = remainingHp;
            m_overkill = overkill;
            m_isLethal = isLethal;
        }

        /// <summary>实际扣掉的血。</summary>
        public int Applied
        {
            get { return m_applied; }
        }

        /// <summary>结算后的 HP（≥ 0）。</summary>
        public int RemainingHp
        {
            get { return m_remainingHp; }
        }

        /// <summary>过量伤害。</summary>
        public int Overkill
        {
            get { return m_overkill; }
        }

        /// <summary>这一下致死吗。</summary>
        public bool IsLethal
        {
            get { return m_isLethal; }
        }

        /// <summary>转成一句人话（日志用）。</summary>
        /// <returns>例如「-80（剩 220）」。</returns>
        public override string ToString()
        {
            return "-" + m_applied + "（剩 " + m_remainingHp + "）" +
                   (m_overkill > 0 ? "，过量 " + m_overkill : string.Empty) +
                   (m_isLethal ? "，致死" : string.Empty);
        }
    }

    /// <summary>伤害结算（**纯函数、整数运算、双端一致**）。</summary>
    public static class DamageMath
    {
        /// <summary>
        /// 结算一次伤害。
        /// </summary>
        /// <param name="currentHp">结算前的 HP（必须 ≥ 0）。</param>
        /// <param name="damage">打出的伤害（必须 ≥ 0）。</param>
        /// <returns>结算结果。</returns>
        /// <exception cref="ArgumentOutOfRangeException">HP 或伤害为负时抛（见文件头说明：不静默修正）。</exception>
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

            // 规则③：打已经死掉的目标 —— 什么都不发生，也**不算致死**
            // （否则"尸体"会再死一次，任务/成就的击杀数会凭空多出来）
            if (currentHp == 0)
            {
                return new DamageOutcome(0, 0, 0, false);
            }

            // 规则①：实扣不超过剩余血；规则②：超出的部分单独记
            int applied = damage > currentHp ? currentHp : damage;

            return new DamageOutcome(applied, currentHp - applied, damage - applied, applied == currentHp);
        }
    }
}
