// 这个文件里有：
//   DungeonOutcomeTextTests：副本结算文案的 EditMode 用例（纯函数，不依赖场景与网络）。

using NBC.Game.UI;
using NBC.Protocol;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// 结算文案用例：锁住胜负标题、时间格式与统计一行的输出。
    /// 主要数据成员：无。
    /// 主要外部方法：8 个 [Test]。
    /// 谁在用它：AI 通过 Test Runner 代跑（AGENTS.md 4.3），结果写进 R042 的 05。
    /// </summary>
    public sealed class DungeonOutcomeTextTests
    {
        [Test]
        public void Title_Win_ReturnsVictoryText()
        {
            Assert.AreEqual("胜利", DungeonOutcomeText.Title(DungeonOutcomeText.Win));
        }

        [Test]
        public void Title_Lose_ReturnsDefeatText()
        {
            Assert.AreEqual("失败", DungeonOutcomeText.Title(DungeonOutcomeText.Lose));
        }

        [Test]
        public void Title_UnknownCode_ReturnsPlaceholder()
        {
            // 服务端以后加了新结果码，老客户端也不能崩，只显示「未知」
            Assert.AreEqual("未知", DungeonOutcomeText.Title(99));
        }

        [Test]
        public void FormatDuration_Zero_ReturnsZeroClock()
        {
            Assert.AreEqual("00:00", DungeonOutcomeText.FormatDuration(0));
        }

        [Test]
        public void FormatDuration_Negative_ClampsToZero()
        {
            Assert.AreEqual("00:00", DungeonOutcomeText.FormatDuration(-1500));
        }

        [Test]
        public void FormatDuration_NinetyNineSeconds_PadsBothParts()
        {
            Assert.AreEqual("01:39", DungeonOutcomeText.FormatDuration(99000));
        }

        [Test]
        public void FormatDuration_TwelveMinutes_PadsMinutes()
        {
            Assert.AreEqual("12:34", DungeonOutcomeText.FormatDuration(754000));
        }

        [Test]
        public void Stats_JoinsThreeNumbers()
        {
            var result = new DungeonResult
            {
                Outcome = DungeonOutcomeText.Win,
                DurationMs = 83000,
                MonsterKills = 12,
                HeroDeaths = 1,
            };

            Assert.AreEqual("用时 01:23 · 击杀 12 · 死亡 1", DungeonOutcomeText.Stats(result));
        }
    }
}
