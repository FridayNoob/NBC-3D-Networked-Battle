// ============================================================================
//  M2-B1 · 共享层伤害结算的 EditMode 测试
//  对应验收：Docs\22-M2开工清单.md B1
//  被测：Client\Assets\_Project\Shared\Battle\DamageMath.cs
//
//  ---------------------------------------------------------------------------
//  为什么这一组全是"边界"
//  ---------------------------------------------------------------------------
//  正常情况的伤害结算（100 血挨 30）**不值得写用例** —— 它不可能错。
//  真正会出错、而且错了以后很难查的是这四种：
//
//      ① **致死的那一下**：伤害正好等于剩余血 -> 必须判"致死"，不能判成"没死"
//      ② **过量伤害**：剩 10 血挨 999 -> 实扣必须是 10（不是 999），剩血必须是 0（不是 -989）
//      ③ **打尸体**：已经 0 血了再挨一下 -> 不能算第二次致死
//         （算了的话，任务/成就里的击杀数会凭空多出来，而"多"是最难发现的错）
//      ④ **0 伤害**：不能致死（`damage >= hp` 这个写法在 damage = 0、hp = 0 时会误判）
//
//  ⚠️ ④ 正是"边界条件写错"的经典形态：`0 >= 0` 为真。
//     本文件用"已经死了就打不动"这条规则把它挡在外面（见文件头规则③）。
// ============================================================================

using NBC.Shared.Battle;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>M2-B1：伤害结算的测试。</summary>
    public sealed class DamageMathTests
    {
        // ====================================================================
        //  一、常规
        // ====================================================================

        /// <summary>普通一下：扣掉伤害、剩血正确、不致死。</summary>
        [Test]
        public void Resolve_NormalDamage_SubtractsAndSurvives()
        {
            DamageOutcome outcome = DamageMath.Resolve(100, 30);

            Assert.AreEqual(30, outcome.Applied);
            Assert.AreEqual(70, outcome.RemainingHp);
            Assert.AreEqual(0, outcome.Overkill);
            Assert.IsFalse(outcome.IsLethal);
        }

        /// <summary>0 伤害：什么都不扣，也**不致死**。</summary>
        [Test]
        public void Resolve_ZeroDamage_DoesNothingAndIsNotLethal()
        {
            DamageOutcome outcome = DamageMath.Resolve(50, 0);

            Assert.AreEqual(0, outcome.Applied);
            Assert.AreEqual(50, outcome.RemainingHp);
            Assert.IsFalse(outcome.IsLethal, "0 伤害不该被当成致死");
        }

        // ====================================================================
        //  二、致死与过量（最容易写错的两个边界）
        // ====================================================================

        /// <summary>伤害正好等于剩余血：**致死**（`applied == hp` 才算致死）。</summary>
        [Test]
        public void Resolve_ExactLethal_IsLethal()
        {
            DamageOutcome outcome = DamageMath.Resolve(80, 80);

            Assert.AreEqual(80, outcome.Applied);
            Assert.AreEqual(0, outcome.RemainingHp);
            Assert.AreEqual(0, outcome.Overkill);
            Assert.IsTrue(outcome.IsLethal);
        }

        /// <summary>过量伤害：实扣 = 剩余血，剩血 = 0，多出来的记进 `Overkill`。</summary>
        [Test]
        public void Resolve_Overkill_ClampsHpToZeroAndRecordsOverkill()
        {
            DamageOutcome outcome = DamageMath.Resolve(10, 999);

            Assert.AreEqual(10, outcome.Applied, "实扣不能超过剩余血");
            Assert.AreEqual(0, outcome.RemainingHp, "剩血必须钳到 0，不能是 -989");
            Assert.AreEqual(989, outcome.Overkill);
            Assert.IsTrue(outcome.IsLethal);
        }

        /// <summary>已经死掉（0 血）的目标再挨一下：**什么都不发生，也不算致死**。</summary>
        [Test]
        public void Resolve_TargetAlreadyDead_DoesNothingAndIsNotLethal()
        {
            DamageOutcome outcome = DamageMath.Resolve(0, 999);

            Assert.AreEqual(0, outcome.Applied);
            Assert.AreEqual(0, outcome.RemainingHp);
            Assert.AreEqual(0, outcome.Overkill, "对尸体不该记过量伤害");
            Assert.IsFalse(outcome.IsLethal, "尸体不能\"再死一次\"，否则击杀数会凭空多出来");
        }

        // ====================================================================
        //  三、非法输入必须响亮地失败
        // ====================================================================

        /// <summary>结算前的 HP 是负数：报错（说明上一处忘了钳位）。</summary>
        [Test]
        public void Resolve_NegativeHp_Throws()
        {
            System.ArgumentOutOfRangeException exception =
                Assert.Throws<System.ArgumentOutOfRangeException>(() => DamageMath.Resolve(-1, 10));

            StringAssert.Contains("钳位", exception.Message);
        }

        /// <summary>负伤害：报错，不静默当成 0。</summary>
        [Test]
        public void Resolve_NegativeDamage_Throws()
        {
            System.ArgumentOutOfRangeException exception =
                Assert.Throws<System.ArgumentOutOfRangeException>(() => DamageMath.Resolve(100, -5));

            StringAssert.Contains("治疗", exception.Message);
        }

        // ====================================================================
        //  四、不变量：连打 N 下，HP 永远不会变负
        // ====================================================================

        /// <summary>
        /// 连打 100 下，HP **一步都不会变成负数**，而且"致死"只发生一次。
        /// <para>这条比"某一个具体数字对不对"更值钱：它检查的是**不变量**。</para>
        /// </summary>
        [Test]
        public void Resolve_RepeatedDamage_NeverGoesNegativeAndDiesOnce()
        {
            int hp = 30;
            int lethalCount = 0;

            for (int i = 0; i < 100; i++)
            {
                DamageOutcome outcome = DamageMath.Resolve(hp, 7);
                hp = outcome.RemainingHp;

                Assert.GreaterOrEqual(hp, 0, "第 " + (i + 1) + " 下之后 HP 变成了 " + hp);

                if (outcome.IsLethal)
                {
                    lethalCount++;
                }
            }

            Assert.AreEqual(0, hp, "打了 100 下总该死透了");
            Assert.AreEqual(1, lethalCount, "\"致死\"只能发生一次");
        }

        /// <summary>结算结果的人话描述里带上了实扣、剩血、致死标记。</summary>
        [Test]
        public void Resolve_ToString_DescribesOutcome()
        {
            string text = DamageMath.Resolve(10, 999).ToString();

            StringAssert.Contains("-10", text);
            StringAssert.Contains("剩 0", text);
            StringAssert.Contains("过量 989", text);
            StringAssert.Contains("致死", text);
        }
    }
}
