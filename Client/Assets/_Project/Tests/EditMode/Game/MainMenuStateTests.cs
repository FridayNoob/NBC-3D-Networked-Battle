// 这个文件里有：
// MainMenuStateTests：主菜单纯逻辑的 EditMode 用例（昵称规范化、连点保护、失败提示）。

using NBC.Game.Menu;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// MainMenuState 的单测。
    /// <para>主要功能：把「昵称怎么算合法」「什么时候能点开始」「失败提示怎么写」钉死，避免只靠进 Play 手点。</para>
    /// <para>主要数据成员：无（每个用例自己 new 状态）。</para>
    /// <para>主要外部方法：六条 [Test]。</para>
    /// <para>谁在用它：Test Runner（AI 代跑，结果写进 R045 的 05 文档）。</para>
    /// </summary>
    public sealed class MainMenuStateTests
    {
        [Test]
        public void NormalizeName_EmptyBecomesDefault()
        {
            Assert.AreEqual(MainMenuState.DefaultName, MainMenuState.NormalizeName(null));
            Assert.AreEqual(MainMenuState.DefaultName, MainMenuState.NormalizeName("   "));
            Assert.AreEqual(MainMenuState.DefaultName, MainMenuState.NormalizeName("\r\n"));
        }

        [Test]
        public void NormalizeName_TrimsAndTruncates()
        {
            Assert.AreEqual("狼王", MainMenuState.NormalizeName("  狼王  "));
            Assert.AreEqual(12, MainMenuState.NormalizeName("1234567890123456").Length);
        }

        [Test]
        public void BeginConnect_BlocksRepeatedClicks()
        {
            var state = new MainMenuState();
            Assert.IsTrue(state.CanStart);
            Assert.IsTrue(state.BeginConnect());
            Assert.IsFalse(state.CanStart);
            Assert.IsFalse(state.BeginConnect());
            Assert.IsTrue(state.IsConnecting);
        }

        [Test]
        public void Fail_RestoresStartAndShowsReason()
        {
            var state = new MainMenuState();
            state.BeginConnect();
            state.Fail("端口不对");

            Assert.IsTrue(state.CanStart);
            Assert.AreEqual("端口不对", state.Hint);
        }

        [Test]
        public void Fail_WithoutReason_UsesDefaultHint()
        {
            var state = new MainMenuState();
            state.BeginConnect();
            state.Fail(string.Empty);

            Assert.AreEqual(MainMenuState.DefaultFailureHint, state.Hint);
        }

        [Test]
        public void Succeed_BlocksStartUntilSceneLoads()
        {
            var state = new MainMenuState();
            state.BeginConnect();
            state.Succeed();

            Assert.IsFalse(state.CanStart);
            Assert.IsFalse(state.IsConnecting);
            Assert.IsTrue(state.IsSucceeded);
            StringAssert.Contains("进入世界", state.Hint);
        }

        [Test]
        public void BeginConnect_ClearsPreviousHint()
        {
            var state = new MainMenuState();
            state.BeginConnect();
            state.Fail("失败一次");
            state.BeginConnect();

            Assert.AreEqual(string.Empty, state.Hint);
        }
    }
}
