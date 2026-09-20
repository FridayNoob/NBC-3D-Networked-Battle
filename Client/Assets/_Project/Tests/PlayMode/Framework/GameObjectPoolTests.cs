// ============================================================================
//  M1-A2 · GameObject 池的 PlayMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2 / V8
//  对应记录：Docs/06-框架改造记录.md §三 FW-05、§四 P-04 / P-10
//
//  为什么在 PlayMode：这组用例要碰 SetActive、父子关系、以及 Object.Destroy 的
//  "延迟到帧末生效"。这些都属于 Unity 的运行时行为 ——
//  按 A1 用实验定下的划分（Docs/06 §9.7），它们必须真进播放模式。
// ============================================================================

using System.Collections;
using System.Collections.Generic;
using NBC.Framework;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NBC.Tests.PlayMode
{
    [TestFixture]
    public class GameObjectPoolTests
    {
        private GameObjectPool m_pool;
        private int m_created;
        private readonly List<GameObject> m_spawned = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            PoolRegistry.Clear();
            m_created = 0;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (m_pool != null)
            {
                m_pool.Dispose();
                m_pool = null;
            }

            for (int i = 0; i < m_spawned.Count; i++)
            {
                if (m_spawned[i] != null)
                {
                    Object.Destroy(m_spawned[i]);
                }
            }

            m_spawned.Clear();
            yield return null;
            PoolRegistry.Clear();
        }

        /// <summary>构造一个受测试管理的池（自动记录创建次数、自动清理）。</summary>
        private GameObjectPool CreatePool(string name, int maxSize = 0, int initialSize = 0,
                                          Transform root = null)
        {
            m_pool = new GameObjectPool(name, () =>
            {
                m_created++;
                return new GameObject("PooledItem");
            }, maxSize, initialSize, root);
            return m_pool;
        }

        private GameObject Spawn(string name)
        {
            GameObject go = new GameObject(name);
            m_spawned.Add(go);
            return go;
        }

        // --------------------------------------------------------------------
        //  取出 / 归还的基本语义
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Spawn_ActivatesAndParentsToGivenParent()
        {
            GameObject parent = Spawn("Parent");
            GameObjectPool pool = CreatePool("p1");

            GameObject go = pool.Spawn(parent.transform);
            yield return null;

            Assert.IsNotNull(go);
            Assert.IsTrue(go.activeSelf, "取出后必须是激活状态");
            Assert.AreSame(parent.transform, go.transform.parent, "应挂到使用者给的父节点下");
            Assert.AreEqual(1, m_created);
        }

        [UnityTest]
        public IEnumerator Despawn_DeactivatesAndParentsToPoolRoot()
        {
            GameObjectPool pool = CreatePool("p2");

            GameObject go = pool.Spawn();
            yield return null;
            pool.Despawn(go);
            yield return null;

            Assert.IsFalse(go.activeSelf, "归还后必须失活（否则还在跑 Update / 还在渲染）");
            Assert.AreSame(pool.Root, go.transform.parent, "归还后应挂回池根下");
            Assert.AreEqual(1, pool.CountInPool);
            Assert.AreEqual(0, pool.CountInUse);
        }

        [UnityTest]
        public IEnumerator Despawn_ThenSpawn_ReusesSameGameObject()
        {
            GameObjectPool pool = CreatePool("p3");

            GameObject a = pool.Spawn();
            yield return null;
            pool.Despawn(a);
            yield return null;

            GameObject b = pool.Spawn();
            yield return null;

            Assert.AreSame(a, b, "应复用同一个 GameObject，而不是新建");
            Assert.AreEqual(1, m_created, "整个过程只应创建 1 个 GameObject");
        }

        [UnityTest]
        public IEnumerator Prewarm_CreatesInactiveObjectsUnderRoot()
        {
            GameObjectPool pool = CreatePool("p4", 0, 4);
            yield return null;

            Assert.AreEqual(4, pool.CountInPool);
            Assert.AreEqual(4, m_created);
            Assert.AreEqual(4, pool.Root.childCount, "预热对象应挂在池根下");
            Assert.IsFalse(pool.Root.GetChild(0).gameObject.activeSelf, "预热对象应是失活的");
        }

        // --------------------------------------------------------------------
        //  FW-05：容量上限 —— 超出即销毁
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Despawn_BeyondMaxSize_DestroysExtraObjects()
        {
            GameObjectPool pool = CreatePool("p5", 2);

            GameObject[] items = new GameObject[3];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = pool.Spawn();
            }

            yield return null;

            for (int i = 0; i < items.Length; i++)
            {
                pool.Despawn(items[i]);
            }

            yield return null;

            Assert.AreEqual(2, pool.CountInPool, "闲置数不得超过上限");
            Assert.AreEqual(1, pool.GetStats().TotalDestroyed, "超出的 1 个应被销毁");
            Assert.AreEqual(2, pool.Root.childCount);
        }

        // --------------------------------------------------------------------
        //  P-04：Clear 必须真的销毁闲置对象，不能留场景孤儿
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Clear_DestroysIdleObjects_SoNoOrphansRemain()
        {
            GameObjectPool pool = CreatePool("p6");

            GameObject[] items = new GameObject[3];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = pool.Spawn();
            }

            yield return null;

            for (int i = 0; i < items.Length; i++)
            {
                pool.Despawn(items[i]);
            }

            yield return null;

            Assert.AreEqual(3, pool.Root.childCount, "清理前应有 3 个闲置对象");

            pool.Clear();
            yield return null;

            Assert.AreEqual(0, pool.CountInPool);
            Assert.AreEqual(0, pool.Root.childCount, "原版 Clear 会把对象留成场景孤儿；这里必须真的清掉");
            Assert.AreEqual(3, pool.GetStats().TotalDestroyed);
        }

        [UnityTest]
        public IEnumerator DespawnAll_ReturnsEverythingInUse()
        {
            GameObjectPool pool = CreatePool("p7");

            for (int i = 0; i < 3; i++)
            {
                pool.Spawn();
            }

            yield return null;
            Assert.AreEqual(3, pool.CountInUse);

            pool.DespawnAll();
            yield return null;

            Assert.AreEqual(0, pool.CountInUse, "所有在外的对象都应被收回");
            Assert.AreEqual(3, pool.CountInPool);
        }

        // --------------------------------------------------------------------
        //  池里的对象被外部销毁（最典型的场景：场景卸载）
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Spawn_SkipsObjectsDestroyedExternally()
        {
            GameObjectPool pool = CreatePool("p8");

            GameObject a = pool.Spawn();
            yield return null;
            pool.Despawn(a);
            yield return null;

            // 模拟"对象被外部销毁"（场景卸载时挂在场景里的对象就是这样没的）。
            // 此时池的 Stack 里还留着它的引用 —— 直接复用就会炸在离原因很远的地方。
            Object.Destroy(a);
            yield return null;

            GameObject b = pool.Spawn();
            yield return null;

            Assert.IsNotNull(b, "取出的对象不能是已销毁的");
            Assert.AreNotSame(a, b, "已销毁的对象不应被复用");
            Assert.AreEqual(1, pool.GetStats().TotalDestroyed, "失效对象应被丢弃并计数");
        }

        // --------------------------------------------------------------------
        //  池根的归属
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Dispose_DestroysSelfCreatedRoot()
        {
            GameObjectPool pool = CreatePool("p9");
            Transform root = pool.Root;

            Assert.IsNotNull(root);

            pool.Dispose();
            m_pool = null;
            yield return null;

            Assert.IsTrue(root == null, "自建的池根应由池自己销毁");
        }

        [UnityTest]
        public IEnumerator Dispose_DoesNotDestroyProvidedRoot()
        {
            GameObject provided = Spawn("ProvidedRoot");
            GameObjectPool pool = CreatePool("p10", 0, 0, provided.transform);

            Assert.AreSame(provided.transform, pool.Root);

            pool.Dispose();
            m_pool = null;
            yield return null;

            Assert.IsTrue(provided != null, "使用者提供的池根不归池管，不能被销毁");
        }

        [UnityTest]
        public IEnumerator Dispose_IsIdempotent_AndPoolBecomesUnusable()
        {
            GameObjectPool pool = CreatePool("p11");
            pool.Spawn();
            yield return null;

            pool.Dispose();
            m_pool = null;
            yield return null;

            Assert.DoesNotThrow(() => pool.Dispose(), "重复 Dispose 不应报错");
            Assert.Throws<System.ObjectDisposedException>(() => pool.Spawn());
        }
    }
}
