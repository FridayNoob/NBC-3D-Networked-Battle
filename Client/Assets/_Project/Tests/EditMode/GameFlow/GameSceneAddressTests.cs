// 这个文件里有：
//   GameSceneAddressTests（NUnit 测试）：场景枚举 → 地址 / 场景名的映射必须和实际文件一致。
//
// 它挡的是"改了场景文件名忘了改映射"——那种错在运行期表现为黑屏，很难查。

using System;
using NBC.Game.GameFlow;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    // 主要功能：校验五个场景的地址映射与异常分支。
    // 主要数据成员：无。
    // 主要外部方法：Addresses / Test / Unknown。
    // 谁在用它：Unity Test Runner（EditMode）。
    public sealed class GameSceneAddressTests
    {
        // 功能：五个场景的地址必须等于文件名。
        // 入参：场景枚举、期望文件名
        // 返回：无
        // 步骤：逐个断言
        // 上游：Unity Test Runner
        // 下游：GameSceneAddress.AddressOf
        [TestCase(GameScene.Login, "Login")]
        [TestCase(GameScene.World, "World")]
        [TestCase(GameScene.PvpRoom, "PVP_Room")]
        [TestCase(GameScene.Battle, "Battle")]
        [TestCase(GameScene.Test, "Test")]
        public void Addresses(GameScene scene, string expected)
        {
            Assert.AreEqual(expected, GameSceneAddress.AddressOf(scene));
            Assert.AreEqual(expected, GameSceneAddress.NameOf(scene));
        }

        // 功能：未登记的枚举值必须抛异常（不能静默加载错场景）。
        // 入参：无
        // 返回：无
        // 步骤：传一个越界值，断言抛 ArgumentOutOfRangeException
        // 上游：Unity Test Runner
        // 下游：GameSceneAddress.AddressOf
        [Test]
        public void Unknown()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => GameSceneAddress.AddressOf((GameScene)99));
        }
    }
}
