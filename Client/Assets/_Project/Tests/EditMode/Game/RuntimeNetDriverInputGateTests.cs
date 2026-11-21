// 这个文件里有：
// RuntimeNetDriverInputGateTests：验「什么时候才允许把输入发给服务端」这道闸门的真值表。

using NUnit.Framework;
using NBC.Game.Bootstrap;

namespace NBC.Tests.EditMode.Game
{
    /// <summary>
    /// 输入闸门的真值表。
    /// <para>这道闸门修的是 2026-11-21 的事故：人在 World 场景（已经退出副本），
    /// 但会话还在房间里，客户端照样每 50ms 发一次输入 —— 服务端把这条输入当成「玩家回来了」，
    /// 取消开局保护、继续打英雄，玩家再进副本时血条就少了一截。</para>
    /// <para>判据：三个条件（在线 / 在房间 / 人在副本里）缺一不可。</para>
    /// </summary>
    public sealed class RuntimeNetDriverInputGateTests
    {
        // 功能：三个条件都满足才允许发输入。
        // 入参：无。
        // 返回：无（断言失败即抛）。
        // 步骤：1) 全真 ⇒ 允许；2) 任意一个假 ⇒ 拒绝（逐个验，防止以后只判其中两个）。
        [Test]
        public void ShouldSendInput_RequiresAllThreeConditions()
        {
            Assert.IsTrue(RuntimeNetDriver.ShouldSendInput(true, true, true), "三个条件都满足却不让发输入");

            Assert.IsFalse(RuntimeNetDriver.ShouldSendInput(false, true, true), "不在线也发输入（断线后 InRoom 仍为 true，会一直发）");
            Assert.IsFalse(RuntimeNetDriver.ShouldSendInput(true, false, true), "不在房间也发输入");
            Assert.IsFalse(RuntimeNetDriver.ShouldSendInput(true, true, false), "人不在副本里也发输入（这条就是 2026-11-21 的事故）");
        }

        // 功能：人不在副本时，哪怕在线且占着座位也不许发 —— 单独拎出来写清事故语义。
        // 入参：无。
        // 返回：无。
        // 步骤：1) online=true, inRoom=true, inDungeon=false ⇒ false；2) 断言消息点名场景。
        [Test]
        public void ShouldSendInput_InWorldWithSeat_StillRefuses()
        {
            Assert.IsFalse(RuntimeNetDriver.ShouldSendInput(true, true, false),
                "人在 World 但还占着房间座位时仍然发了输入（服务端会据此取消开局保护）");
        }
    }
}
