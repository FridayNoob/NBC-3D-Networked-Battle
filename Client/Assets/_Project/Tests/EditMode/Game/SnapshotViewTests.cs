// ============================================================================
//  M3-S5 · `SnapshotView` 的测试（**纯数据，不开端口、不开 Unity**）
//  对应验收：Docs\25 §8.9
//  被测：`Client\Assets\_Project\Game\Net\SnapshotView.cs`
//
//  ---------------------------------------------------------------------------
//  为什么要单独测这一层
//  ---------------------------------------------------------------------------
//  服务端权威（D3）在客户端的落点就是这句话：**客户端没有"自己那份血条"**，
//  它的世界完全由快照决定。所以"客户端表现与服务端一致"这件事，
//  等价于"`SnapshotView` 有没有把快照照实存下来" —— 这正是可以在 EditMode 里钉住的部分。
//
//  ⚠️ 其中一条专门喂**旧快照**：`SnapshotView` 里那条"丢弃旧快照"的分支
//     在 TCP 下**正常永远走不到**。本项目刚立过规矩"走不到的分支比没有分支更糟"，
//     所以它必须**能被测**才算数 —— 这条用例就是它的"存在理由"。
// ============================================================================

using System.Collections.Generic;
using NBC.Game.Net;
using NBC.Protocol;
using NUnit.Framework;

namespace NBC.Tests.EditMode
{
    /// <summary>M3-S5：客户端世界视图的测试。</summary>
    public sealed class SnapshotViewTests
    {
        // ====================================================================
        //  一、应用快照
        // ====================================================================

        /// <summary>应用一张快照：帧号、单位、存活数都对上。</summary>
        [Test]
        public void Apply_UpdatesTickAndEntities()
        {
            SnapshotView view = new SnapshotView();

            Assert.IsFalse(view.HasWorld, "还没收到快照时不该声称有世界");
            Assert.AreEqual(-1, view.ServerTick);

            view.Apply(Snapshot(tick: 12, (1, 0, 300, true), (2, 1, 120, true), (3, 1, 0, false)));

            Assert.IsTrue(view.HasWorld);
            Assert.AreEqual(12, view.ServerTick);
            Assert.AreEqual(3, view.EntityCount);
            Assert.AreEqual(2, view.AliveCount, "hp=0 的那个不算活着");
            Assert.AreEqual(1, view.Applied);
            Assert.AreEqual(0, view.StaleIgnored);
        }

        /// <summary>新快照**整份替换**旧的那份（不是增量）—— 服务端死了怪，客户端就得少一只。</summary>
        [Test]
        public void Apply_ReplacesTheWholeWorld()
        {
            SnapshotView view = new SnapshotView();

            view.Apply(Snapshot(tick: 1, (1, 0, 300, true), (2, 1, 120, true)));
            view.Apply(Snapshot(tick: 2, (1, 0, 300, true)));

            Assert.AreEqual(2, view.ServerTick);
            Assert.AreEqual(1, view.EntityCount, "第二张快照里只有 1 个单位 → 视图里也该只剩 1 个");
            Assert.IsNull(view.Find(2), "上一帧的怪不该还留着");
        }

        /// <summary>
        /// **旧快照要被丢掉**（否则画面会倒退）。
        /// <para>这条分支在 TCP 下正常走不到（有序）；它防的是"重连/换了连接之后迟到的旧包"。</para>
        /// </summary>
        [Test]
        public void Apply_StaleSnapshotIsIgnored()
        {
            SnapshotView view = new SnapshotView();

            view.Apply(Snapshot(tick: 10, (1, 0, 300, true), (2, 1, 120, true)));
            bool accepted = view.Apply(Snapshot(tick: 5, (1, 0, 300, true)));

            Assert.IsFalse(accepted, "旧快照不该被采纳");
            Assert.AreEqual(10, view.ServerTick, "帧号不许倒退");
            Assert.AreEqual(2, view.EntityCount, "世界不许被旧快照改小");
            Assert.AreEqual(1, view.Applied);
            Assert.AreEqual(1, view.StaleIgnored, "被丢掉的张数要能被看到");
        }

        /// <summary>同一帧重复到达也算旧快照（`<=` 而不是 `<`）。</summary>
        [Test]
        public void Apply_SameTickIsAlsoIgnored()
        {
            SnapshotView view = new SnapshotView();

            view.Apply(Snapshot(tick: 7, (1, 0, 300, true)));
            view.Apply(Snapshot(tick: 7, (1, 0, 1, true)));

            Assert.AreEqual(7, view.ServerTick);
            Assert.AreEqual(300, view.Find(1).Hp, "同一帧的重复快照不该覆盖已有世界");
            Assert.AreEqual(1, view.StaleIgnored);
        }

        /// <summary>null 载荷（0 长度帧解出来就是 null）不该崩，也不该污染世界。</summary>
        [Test]
        public void Apply_NullIsIgnored()
        {
            SnapshotView view = new SnapshotView();

            Assert.IsFalse(view.Apply(null));
            Assert.IsFalse(view.HasWorld);
            Assert.AreEqual(0, view.Applied);
            Assert.AreEqual(0, view.StaleIgnored, "null 不算「旧快照」，它是「没东西」");
        }

        // ====================================================================
        //  二、查询与清理
        // ====================================================================

        /// <summary>按实例编号查得到（表现层要靠它把数据贴到模型上）。</summary>
        [Test]
        public void Find_ReturnsEntityById()
        {
            SnapshotView view = new SnapshotView();
            view.Apply(Snapshot(tick: 3, (1, 0, 300, true), (2, 1, 77, true)));

            Assert.IsNotNull(view.Find(2));
            Assert.AreEqual(77, view.Find(2).Hp);
            Assert.AreEqual(1000, view.Find(2).PosXMm);
            Assert.IsNull(view.Find(99), "不存在的实例应当返回 null，而不是随便给一个");
        }

        /// <summary>断开/重连要能清干净（否则下一局开局就显示上一局的尸体）。</summary>
        [Test]
        public void Clear_ResetsWorld()
        {
            SnapshotView view = new SnapshotView();
            view.Apply(Snapshot(tick: 9, (1, 0, 300, true)));

            view.Clear();

            Assert.IsFalse(view.HasWorld);
            Assert.AreEqual(-1, view.ServerTick);
            Assert.AreEqual(0, view.EntityCount);
        }

        /// <summary>`Describe()` 要能在窗口里直接显示（人话，不抛异常）。</summary>
        [Test]
        public void Describe_IsHumanReadable()
        {
            SnapshotView view = new SnapshotView();
            StringAssert.Contains("还没有世界", view.Describe());

            view.Apply(Snapshot(tick: 42, (1, 0, 300, true), (2, 1, 0, false)));
            StringAssert.Contains("42", view.Describe());
            StringAssert.Contains("1/2", view.Describe());
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>造一张快照（[帧号] + 若干个 [实例号, 类型, 血量, 是否活着]）。</summary>
        /// <param name="tick">服务端帧号。</param>
        /// <param name="entities">单位（实例号、类型、血量、是否活着）。</param>
        /// <returns>快照。</returns>
        private static WorldSnapshot Snapshot(int tick, params (int Id, int Kind, int Hp, bool Alive)[] entities)
        {
            var snapshot = new WorldSnapshot { ServerTick = tick };

            for (int i = 0; i < entities.Length; i++)
            {
                snapshot.Entities.Add(new EntitySnapshot
                {
                    EntityId = entities[i].Id,
                    ConfigId = 6000 + entities[i].Id,
                    Kind = entities[i].Kind,
                    Hp = entities[i].Hp,
                    MaxHp = 300,
                    PosXMm = 1000 * entities[i].Id,
                    PosZMm = 0,
                    FacingDeg = 90,
                    Alive = entities[i].Alive,
                });
            }

            return snapshot;
        }
    }
}
