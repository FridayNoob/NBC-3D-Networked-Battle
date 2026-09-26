// ============================================================================
//  M4-S1 · `ServerEventBridge` 的测试（**纯逻辑，不开端口、不开 Unity**）
//  被测：`Client\Assets\_Project\Game\Net\ServerEventBridge.cs`
//
//  它证明的是 M3 收关时**如实画出来的那条断口**被接上了：
//
//      服务端 DamageEvent / DeathEvent / DropEvent
//            ↓  NetSession（只负责收下来）
//            ↓  ServerEventBridge（本类：翻译成游戏事件）
//          EventCenter
//            ↓  ConditionEventBridge（M2 就有的那座桥）
//          ConditionTracker → QuestRuntime
//
//  ⚠️ 最后一条用例是**端到端**的：喂服务端死亡事件 → 任务条件进度真的涨。
//     它跑的是生产代码的真实链路（两个桥都是产品代码，不是测试替身）。
//
//  ⚠️ 本组用例必须清 `EventCenter`：它是**单例**，监听者会跨用例残留
//     （M2 也踩过"订阅不退订"那一族）。
// ============================================================================

using System.Collections.Generic;
using Google.Protobuf;          // `ToByteArray()`（protobuf 的扩展方法；少这个就是 CS1061）
using NBC.Framework;
using NBC.Framework.Net;
using NBC.Game.Battle;
using NBC.Game.Net;
using NBC.Game.Quest;
using NBC.Protocol;
using NBC.Shared.Condition;
using NBC.Shared.Net;
using NUnit.Framework;

namespace NBC.Tests.EditMode.Game
{
    /// <summary>`ServerEventBridge` 的用例。</summary>
    public sealed class ServerEventBridgeTests
    {
        /// <summary>服务端分配给测试玩家的 id（见 `Online`）。</summary>
        private const long MyPlayerId = 7;

        /// <summary>每个用例结束后把事件中心的残留清掉（它是单例）。</summary>
        [TearDown]
        public void TearDown()
        {
            EventCenter.Instance.Clear();
        }

        // ====================================================================
        //  一、死亡：**按 kind 分派成两个不同的事件**
        // ====================================================================

        /// <summary>怪死了 → `MonsterDied`，且载荷里的编号如实带过去。</summary>
        [Test]
        public void MonsterDeath_BecomesMonsterDied()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);
            ServerEventBridge bridge = new ServerEventBridge(session);

            MonsterDiedPayload got = default(MonsterDiedPayload);
            int count = 0;
            EventCenter.Instance.AddEventListener<MonsterDiedPayload>(BattleEvents.MonsterDied, payload =>
            {
                got = payload;
                count++;
            });

            int heroDied = 0;
            EventCenter.Instance.AddEventListener<HeroDiedPayload>(BattleEvents.HeroDied, _ => heroDied++);

            Push(fake, session, Death(entityId: 12, configId: 6001, kind: 1, killerId: 3));

            Assert.AreEqual(1, count, "应当转发一条怪物死亡事件");
            Assert.AreEqual(12, got.InstanceId, "实例编号要如实带过去（从世界里摘掉它时要靠它）");
            Assert.AreEqual(6001, got.MonsterConfigId, "配置编号要如实带过去（任务条件、掉落靠它）");
            Assert.AreEqual(3, got.KillerInstanceId);
            Assert.AreEqual(0, heroDied, "怪死**不能**同时触发英雄死亡事件");
            Assert.AreEqual(1, bridge.MonstersDied);
            Assert.AreEqual(0, bridge.HeroesDied);
        }

        /// <summary>
        /// 英雄死了 → `HeroDied`（**不能**被当成"击杀了一个目标 0"）。
        /// <para>⚠️ 这条是 M2 那个坑的**回归钉**（见 `BattleEvents.cs` 文件头）：
        /// 若把两种死亡合成一个"带 kind 的事件"、让订阅方自己判，
        /// 判错就会让"击杀任意怪 N 只"的任务**凭空涨进度**。</para>
        /// </summary>
        [Test]
        public void HeroDeath_BecomesHeroDied_AndNotMonsterDied()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);
            ServerEventBridge bridge = new ServerEventBridge(session);

            int monsterDied = 0;
            EventCenter.Instance.AddEventListener<MonsterDiedPayload>(BattleEvents.MonsterDied, _ => monsterDied++);

            HeroDiedPayload got = default(HeroDiedPayload);
            int heroDied = 0;
            EventCenter.Instance.AddEventListener<HeroDiedPayload>(BattleEvents.HeroDied, payload =>
            {
                got = payload;
                heroDied++;
            });

            Push(fake, session, Death(entityId: 1, configId: 1001, kind: 0, killerId: 9));

            Assert.AreEqual(1, heroDied, "应当转发一条英雄死亡事件");
            Assert.AreEqual(1001, got.HeroConfigId);
            Assert.AreEqual(0, monsterDied, "英雄死**绝不能**被当成怪物死亡（否则击杀任务会凭空涨）");
            Assert.AreEqual(0, bridge.MonstersDied);
            Assert.AreEqual(1, bridge.HeroesDied);
        }

        // ====================================================================
        //  二、伤害：每次扣血一条
        // ====================================================================

        /// <summary>伤害事件 → `DamageDealt`，且"实际扣血"与"剩余血"如实。</summary>
        [Test]
        public void Damage_BecomesDamageDealt()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);
            ServerEventBridge bridge = new ServerEventBridge(session);

            DamageDealtPayload got = default(DamageDealtPayload);
            EventCenter.Instance.AddEventListener<DamageDealtPayload>(BattleEvents.DamageDealt, payload => got = payload);

            // 打只剩 5 血的怪、打出去 100 → 协议里 applied 就应该是 5（不是 100）
            Push(fake, session, Damage(attackerId: 3, targetId: 12, applied: 5, remainingHp: 0));

            Assert.AreEqual(3, got.AttackerInstanceId);
            Assert.AreEqual(12, got.TargetInstanceId);
            Assert.AreEqual(5, got.Applied, "发的必须是**实际扣掉的血**，不是打出去的伤害值");
            Assert.AreEqual(0, got.RemainingHp);
            Assert.AreEqual(1, bridge.HitsForwarded);
        }

        // ====================================================================
        //  三、掉落：**只有归我**的才发游戏事件
        // ====================================================================

        /// <summary>归我的掉落 → `ItemDropped`。</summary>
        [Test]
        public void Drop_Mine_BecomesItemDropped()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);
            ServerEventBridge bridge = new ServerEventBridge(session);

            ItemDroppedPayload got = default(ItemDroppedPayload);
            int count = 0;
            EventCenter.Instance.AddEventListener<ItemDroppedPayload>(BattleEvents.ItemDropped, payload =>
            {
                got = payload;
                count++;
            });

            Push(fake, session, Drop(itemId: 9001, count: 2, winnerPlayerId: MyPlayerId));

            Assert.AreEqual(1, count);
            Assert.AreEqual(9001, got.ItemId);
            Assert.AreEqual(2, got.Count);
            Assert.AreEqual(1, bridge.DropsClaimed);
            Assert.AreEqual(0, bridge.DropsOfOthers);
        }

        /// <summary>
        /// 别人的战利品 → **不发** `ItemDropped`（否则"收集物品"任务会被别人推进）。
        /// <para>⚠️ 归属判断放在产生事件的地方，而不是让订阅方自己判 —— 判错是**静默**的。</para>
        /// </summary>
        [Test]
        public void Drop_OfAnotherPlayer_IsNotRaisedAsMine()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);
            ServerEventBridge bridge = new ServerEventBridge(session);

            int count = 0;
            EventCenter.Instance.AddEventListener<ItemDroppedPayload>(BattleEvents.ItemDropped, _ => count++);

            Push(fake, session, Drop(itemId: 9001, count: 1, winnerPlayerId: MyPlayerId + 1));

            Assert.AreEqual(0, count, "别人的掉落不该发成「我获得了」");
            Assert.AreEqual(1, bridge.DropsOfOthers, "要记一笔，免得看起来像漏了");
            Assert.AreEqual(0, bridge.DropsClaimed);
            Assert.AreEqual(1, session.DropsReceived, "网络层照样如实记数（过滤是桥的职责）");
        }

        // ====================================================================
        //  四、生命周期：订阅了就必须退订
        // ====================================================================

        /// <summary>`Dispose` 之后再来的事件**不再转发**（A4/A8 踩过的那个坑）。</summary>
        [Test]
        public void Dispose_StopsForwarding()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);
            ServerEventBridge bridge = new ServerEventBridge(session);

            int count = 0;
            EventCenter.Instance.AddEventListener<MonsterDiedPayload>(BattleEvents.MonsterDied, _ => count++);

            Push(fake, session, Death(entityId: 12, configId: 6001, kind: 1, killerId: 3));
            Assert.AreEqual(1, count, "Dispose 之前应当转发");

            bridge.Dispose();
            Assert.IsTrue(bridge.IsDisposed);

            Push(fake, session, Death(entityId: 13, configId: 6001, kind: 1, killerId: 3));
            Assert.AreEqual(1, count, "Dispose 之后**不该**再转发");

            bridge.Dispose();       // 重复 Dispose 必须无害
            Assert.IsTrue(bridge.IsDisposed);
        }

        /// <summary>没给会话就直接抛（fail loud，别等到运行时才发现桥没接上）。</summary>
        [Test]
        public void Constructor_WithoutSession_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => new ServerEventBridge(null));
        }

        // ====================================================================
        //  五、端到端：**服务端死亡事件 → 任务条件进度真的涨**（M4-S1 的验收）
        // ====================================================================

        /// <summary>
        /// 打死 3 只野狼（6001）→ 条件「击杀 6001 三只」达成。
        /// <para>这条链路全部是**产品代码**：`NetSession` → `ServerEventBridge` →
        /// `EventCenter` → `ConditionEventBridge` → `ConditionTracker`。
        /// 它证明"M3 收关时画的那条断口"已经接上 —— 联机战斗能推进任务了。</para>
        /// </summary>
        [Test]
        public void ServerDeathEvents_ProgressQuestCondition()
        {
            FakeTransport fake = new FakeTransport();
            NetSession session = Online(fake);

            ConditionTracker tracker = new ConditionTracker(new InMemoryConditionProgressStore());
            ConditionEventBridge conditionBridge = new ConditionEventBridge(tracker);
            ServerEventBridge serverBridge = new ServerEventBridge(session);

            const int conditionKey = 5001;      // 造一个条件编号（真项目里是 QuestCondition 表主键）
            tracker.Register(conditionKey, new ConditionDef(EConditionEvent.KillMonster, 6001, 3), true);
            conditionBridge.Bind<MonsterDiedPayload>(BattleEvents.MonsterDied,
                EConditionEvent.KillMonster, payload => payload.MonsterConfigId);

            int metCount = 0;
            tracker.ConditionMet += (key, progress) => metCount++;

            // 两只野狼（6001）+ 一只森林蜘蛛（6002，**不该**计入"击杀野狼"）
            Push(fake, session, Death(entityId: 11, configId: 6001, kind: 1, killerId: 3));
            Push(fake, session, Death(entityId: 12, configId: 6001, kind: 1, killerId: 3));
            Push(fake, session, Death(entityId: 13, configId: 6002, kind: 1, killerId: 3));

            ConditionProgress progress;
            Assert.IsTrue(tracker.TryGetProgress(conditionKey, out progress));
            Assert.AreEqual(2, progress.Current, "打了两只野狼就该是 2（蜘蛛不算）");
            Assert.AreEqual(3, progress.Required);
            Assert.IsFalse(tracker.IsMet(conditionKey), "还差一只，不该达成");
            Assert.AreEqual(0, metCount);

            // 第三只野狼 → 达成
            Push(fake, session, Death(entityId: 14, configId: 6001, kind: 1, killerId: 3));

            Assert.IsTrue(tracker.TryGetProgress(conditionKey, out progress));
            Assert.AreEqual(3, progress.Current);
            Assert.IsTrue(tracker.IsMet(conditionKey), "三只野狼打完就该达成");
            Assert.AreEqual(1, metCount, "达成回调只该发一次");

            conditionBridge.Dispose();
            serverBridge.Dispose();
        }

        // ====================================================================
        //  夹具
        // ====================================================================

        /// <summary>造一个"已经握手完成"的会话（照 `NetSessionTests` 的夹具，玩家 id = 7）。</summary>
        /// <param name="fake">假传输。</param>
        /// <returns>在线会话。</returns>
        private static NetSession Online(FakeTransport fake)
        {
            NetSession session = new NetSession(fake, "测试玩家");

            session.Connect("127.0.0.1", 7777);
            session.Pump(0);
            fake.PushFrame(Ack(MyPlayerId));
            session.Pump(0);

            Assert.AreEqual(ESessionState.Online, session.State, "夹具没把会话带到 Online");

            return session;
        }

        /// <summary>把一条服务端事件塞进会话并推进一帧。</summary>
        /// <param name="fake">假传输。</param>
        /// <param name="session">会话。</param>
        /// <param name="serverEvent">事件。</param>
        private static void Push(FakeTransport fake, NetSession session, ServerEvent serverEvent)
        {
            fake.PushFrame(new ServerMessage { Event = serverEvent }.ToByteArray());
            session.Pump(0);
        }

        /// <summary>造一条死亡事件。</summary>
        /// <param name="entityId">实例编号。</param>
        /// <param name="configId">配置编号。</param>
        /// <param name="kind">0 = 英雄，1 = 怪物。</param>
        /// <param name="killerId">击杀者实例编号（0 = 无）。</param>
        /// <returns>事件。</returns>
        private static ServerEvent Death(int entityId, int configId, int kind, int killerId)
        {
            return new ServerEvent
            {
                Death = new DeathEvent
                {
                    EntityId = entityId,
                    ConfigId = configId,
                    Kind = kind,
                    KillerId = killerId,
                },
            };
        }

        /// <summary>造一条伤害事件。</summary>
        /// <param name="attackerId">攻击者实例编号。</param>
        /// <param name="targetId">受击者实例编号。</param>
        /// <param name="applied">实际扣血。</param>
        /// <param name="remainingHp">剩余血。</param>
        /// <returns>事件。</returns>
        private static ServerEvent Damage(int attackerId, int targetId, int applied, int remainingHp)
        {
            return new ServerEvent
            {
                Damage = new DamageEvent
                {
                    AttackerId = attackerId,
                    TargetId = targetId,
                    Applied = applied,
                    RemainingHp = remainingHp,
                },
            };
        }

        /// <summary>造一条掉落事件。</summary>
        /// <param name="itemId">物品编号。</param>
        /// <param name="count">数量。</param>
        /// <param name="winnerPlayerId">归谁。</param>
        /// <returns>事件。</returns>
        private static ServerEvent Drop(int itemId, int count, long winnerPlayerId)
        {
            return new ServerEvent
            {
                Drop = new DropEvent { ItemId = itemId, Count = count, WinnerPlayerId = winnerPlayerId },
            };
        }

        /// <summary>造一条"握手通过"的答复。</summary>
        /// <param name="playerId">分配给测试玩家的 id。</param>
        /// <returns>序列化后的载荷。</returns>
        private static byte[] Ack(long playerId)
        {
            return new ServerMessage
            {
                HandshakeAck = new HandshakeAck
                {
                    Accepted = true,
                    PlayerId = playerId,
                    ProtocolVersion = NetContract.Version,
                    ServerVersion = "unit-server",
                    TickHz = NetContract.TickRate,
                },
            }.ToByteArray();
        }
    }
}
