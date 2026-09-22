// ============================================================================
//  M1-C4 · ConfigMgr 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md C4（CFG-R1~R4）
//
//  ---------------------------------------------------------------------------
//  为什么这一整套逻辑能在 EditMode 里测
//  ---------------------------------------------------------------------------
//  因为 `ConfigMgr` 是**纯 C# 单例**（`Singleton<T>`，不是 MonoBehaviour）：
//  没有 `Awake`、没有帧循环、没有协程，全部逻辑都是"调方法 → 回调"。
//  而"资产从哪来"被 `IConfigSource` 挡在外面 —— 测试塞一个**同步完成的假来源**，
//  连一个真实 `.asset` 都不需要。
//
//  📌 这就是 B2/A9/E1 那条老经验的又一次兑现：**依赖倒置换来的可测性**。
//     真来源（走 YooAsset）等 B4/B5 打包做完再接，**这些测试一行都不用改**。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Game.Config;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>C4：运行时配置表查询入口的测试。</summary>
    public sealed class ConfigMgrTests
    {
        private ConfigMgr m_mgr;

        /// <summary>每个用例开始前清干净（单例是常驻的）。</summary>
        [SetUp]
        public void SetUp()
        {
            EnsureInstance();
            m_mgr.Clear();
            m_mgr.SetSource(null);
        }

        /// <summary>收尾：清缓存、断开来源，并销毁测试里造出来的资产（别留下泄漏）。</summary>
        [TearDown]
        public void TearDown()
        {
            if (m_mgr != null)
            {
                FakeSource source = m_mgr.Source as FakeSource;

                if (source != null)
                {
                    source.DestroyCreated();
                }

                m_mgr.Clear();
                m_mgr.SetSource(null);
            }
        }

        /// <summary>拿到单例（`Singleton&lt;T&gt;` 会按需创建）。</summary>
        private void EnsureInstance()
        {
            m_mgr = ConfigMgr.Instance;
        }

        // ====================================================================
        //  建表用的假资产类型（**必须叫 &lt;表&gt;Config**：命名约定是硬要求）
        // ====================================================================

        /// <summary>假的 Hero 配置资产。</summary>
        public sealed class HeroConfig : ScriptableObject
        {
            /// <summary>主键。</summary>
            public int id;
        }

        /// <summary>假的 Skill 配置资产。</summary>
        public sealed class SkillConfig : ScriptableObject
        {
            /// <summary>主键。</summary>
            public int id;
        }

        /// <summary>名字不符合约定的类型（用来验报错）。</summary>
        public sealed class NotAConfigTable : ScriptableObject
        {
        }

        /// <summary>测试用的假来源：**同步**完成，可指定让哪张表失败。</summary>
        private sealed class FakeSource : IConfigSource
        {
            /// <summary>这些表加载失败。</summary>
            public readonly HashSet<string> Failing = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>每张表被请求了几次（验"不会重复加载"）。</summary>
            public readonly Dictionary<string, int> RequestCount = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>把回调攒起来、稍后手动触发（验异步路径）。</summary>
            public readonly List<Action> Deferred = new List<Action>();

            /// <summary>手动模式：不立刻回调。</summary>
            public bool Manual;

            /// <summary>造出来的资产（收尾时销毁，避免测试泄漏）。</summary>
            private readonly List<ScriptableObject> m_created = new List<ScriptableObject>();

            /// <summary>来源描述。</summary>
            public string Description
            {
                get { return "假来源（测试）"; }
            }

            /// <summary>销毁造出来的资产。</summary>
            public void DestroyCreated()
            {
                for (int i = 0; i < m_created.Count; i++)
                {
                    if (m_created[i] != null)
                    {
                        UnityEngine.Object.DestroyImmediate(m_created[i]);
                    }
                }

                m_created.Clear();
            }

            /// <summary>加载。</summary>
            public void LoadTable(string tableName, Action<ScriptableObject> onLoaded, Action<string> onFailed)
            {
                int count;
                RequestCount.TryGetValue(tableName, out count);
                RequestCount[tableName] = count + 1;

                if (Manual)
                {
                    Deferred.Add(() => Fire(tableName, onLoaded, onFailed));
                    return;
                }

                Fire(tableName, onLoaded, onFailed);
            }

            /// <summary>手动模式：把攒下的回调都放出来。</summary>
            public void Release()
            {
                Action[] waiting = Deferred.ToArray();
                Deferred.Clear();

                for (int i = 0; i < waiting.Length; i++)
                {
                    waiting[i]();
                }
            }

            /// <summary>真的回调。</summary>
            private void Fire(string tableName, Action<ScriptableObject> onLoaded, Action<string> onFailed)
            {
                if (Failing.Contains(tableName))
                {
                    onFailed("假来源故意让它失败");
                    return;
                }

                ScriptableObject asset = tableName == "Hero"
                    ? ScriptableObject.CreateInstance<HeroConfig>()
                    : ScriptableObject.CreateInstance<SkillConfig>();

                m_created.Add(asset);
                onLoaded(asset);
            }
        }

        // ====================================================================
        //  一、命名约定（表名 ↔ 类型名）
        // ====================================================================

        /// <summary>表名 → 资产类型名。</summary>
        [Test]
        public void AssetNameOf_AppendsConfig()
        {
            Assert.AreEqual("HeroConfig", ConfigMgr.AssetNameOf("Hero"));
        }

        /// <summary>资产类型 → 表名。</summary>
        [Test]
        public void TableNameOf_StripsConfigSuffix()
        {
            Assert.AreEqual("Hero", ConfigMgr.TableNameOf<HeroConfig>());
        }

        /// <summary>
        /// 名字不符约定时**当场报错**，而不是猜一个表名出来。
        /// <para>猜的后果是"加载一个不存在的表"，报错点离真正的原因很远。</para>
        /// </summary>
        [Test]
        public void TableNameOf_RejectsNameWithoutSuffix()
        {
            Assert.Throws<ArgumentException>(() => ConfigMgr.TableNameOf<NotAConfigTable>());
        }

        // ====================================================================
        //  二、预加载
        // ====================================================================

        /// <summary>预加载成功后能查到。</summary>
        [Test]
        public void Preload_ThenGet_ReturnsAsset()
        {
            FakeSource source = new FakeSource();
            m_mgr.SetSource(source);

            HeroConfig loaded = null;
            m_mgr.Preload<HeroConfig>(() => loaded = m_mgr.Get<HeroConfig>());

            Assert.IsNotNull(loaded, "预加载应当回调");
            Assert.IsTrue(m_mgr.IsLoaded("Hero"));
            Assert.AreEqual(1, m_mgr.LoadedTableCount);
        }

        /// <summary>没预加载就查 → **抛异常，且消息要说清原因与解法**（CFG-R4）。</summary>
        [Test]
        public void Get_WithoutPreload_ThrowsActionableMessage()
        {
            FakeSource source = new FakeSource();
            m_mgr.SetSource(source);

            InvalidOperationException error =
                Assert.Throws<InvalidOperationException>(() => m_mgr.Get<HeroConfig>());

            StringAssert.Contains("Hero", error.Message, "要说清是哪张表");
            StringAssert.Contains("忘了预加载", error.Message, "要点出最常见原因");
            StringAssert.Contains("Preload<HeroConfig>", error.Message, "要给出该怎么改");
        }

        /// <summary>没设置来源时的报错也要说清怎么解决。</summary>
        [Test]
        public void Preload_WithoutSource_ReportsHowToFix()
        {
            string failure = null;
            m_mgr.Preload<HeroConfig>(null, reason => failure = reason);

            Assert.IsNotNull(failure, "必须回调失败（**不能静默什么都不做**）");
            StringAssert.Contains("SetSource", failure);
        }

        /// <summary>
        /// **同一个表名同时只有一条加载任务**（语义②）。
        /// <para>这条是从 A9 的 `UIManager` 抄来的：那边踩过"同帧连点两下 = 加载两遍"。</para>
        /// </summary>
        [Test]
        public void Preload_SameTableTwice_LoadsOnce()
        {
            FakeSource source = new FakeSource { Manual = true };
            m_mgr.SetSource(source);

            int first = 0;
            int second = 0;

            m_mgr.Preload<HeroConfig>(() => first++);
            m_mgr.Preload<HeroConfig>(() => second++);

            Assert.AreEqual(1, source.RequestCount["Hero"], "第二次请求应当**挂到已有任务上**，不再加载一次");
            Assert.IsTrue(m_mgr.IsLoading("Hero"));

            source.Release();

            Assert.AreEqual(1, first, "两个回调都该被叫到");
            Assert.AreEqual(1, second, "两个回调都该被叫到");
        }

        /// <summary>已经加载过的表再预加载 → 直接回调，**不再请求来源**。</summary>
        [Test]
        public void Preload_AfterLoaded_DoesNotHitSourceAgain()
        {
            FakeSource source = new FakeSource();
            m_mgr.SetSource(source);

            m_mgr.Preload<HeroConfig>();
            Assert.AreEqual(1, source.RequestCount["Hero"]);

            m_mgr.Preload<HeroConfig>();
            Assert.AreEqual(1, source.RequestCount["Hero"], "缓存命中就不要再问来源");
        }

        /// <summary>失败要回调、且**不会**把失败的表塞进缓存。</summary>
        [Test]
        public void Preload_Failure_ReportsAndCachesNothing()
        {
            FakeSource source = new FakeSource();
            source.Failing.Add("Hero");
            m_mgr.SetSource(source);

            string failure = null;
            m_mgr.Preload<HeroConfig>(null, reason => failure = reason);

            Assert.IsNotNull(failure);
            StringAssert.Contains("Hero", failure);
            Assert.IsFalse(m_mgr.IsLoaded("Hero"), "失败的表**不能**进缓存");
            Assert.IsFalse(m_mgr.IsLoading("Hero"), "失败之后要清掉 pending");
        }

        /// <summary>多张表串行预加载（全部成功才回调成功）。</summary>
        [Test]
        public void PreloadAll_AllSucceed_CallsDone()
        {
            FakeSource source = new FakeSource();
            m_mgr.SetSource(source);

            bool done = false;
            m_mgr.PreloadAll(new List<string> { "Hero", "Skill" }, () => done = true, null);

            Assert.IsTrue(done);
            Assert.AreEqual(2, m_mgr.LoadedTableCount);
        }

        /// <summary>
        /// 串行预加载里**任一张失败就停**，且不再继续加载后面的表。
        /// <para>不这么做的话，失败之后还会继续去加载 —— 而那时候调用方已经收到失败信号了。</para>
        /// </summary>
        [Test]
        public void PreloadAll_StopsAtFirstFailure()
        {
            FakeSource source = new FakeSource();
            source.Failing.Add("Hero");
            m_mgr.SetSource(source);

            string failure = null;
            bool done = false;

            m_mgr.PreloadAll(new List<string> { "Hero", "Skill" }, () => done = true, reason => failure = reason);

            Assert.IsNotNull(failure);
            Assert.IsFalse(done, "有失败就不该报成功");
            Assert.IsFalse(m_mgr.IsLoaded("Skill"), "第一张失败之后**不该继续**加载第二张");
        }

        // ====================================================================
        //  三、查询与卸载
        // ====================================================================

        /// <summary>`TryGet` 在没加载时**不抛异常**。</summary>
        [Test]
        public void TryGet_WithoutPreload_ReturnsFalse()
        {
            HeroConfig table;

            Assert.IsFalse(m_mgr.TryGet(out table));
            Assert.IsNull(table);
        }

        /// <summary>卸载之后查就查不到了（而卸载前能查到）。</summary>
        [Test]
        public void Unload_MakesTableUnavailable()
        {
            FakeSource source = new FakeSource();
            m_mgr.SetSource(source);
            m_mgr.Preload<HeroConfig>();

            Assert.IsTrue(m_mgr.IsLoaded("Hero"));
            Assert.IsTrue(m_mgr.Unload("Hero"));
            Assert.IsFalse(m_mgr.IsLoaded("Hero"));
            Assert.IsFalse(m_mgr.Unload("Hero"), "重复卸载返回 false，而不是抛异常");
        }

        /// <summary>空表名不该崩（它只是"没这张表"）。</summary>
        [Test]
        public void EmptyTableName_IsRejectedWithoutCrash()
        {
            string failure = null;
            m_mgr.Preload(string.Empty, null, reason => failure = reason);

            Assert.IsNotNull(failure);
            Assert.IsFalse(m_mgr.IsLoaded(string.Empty));
        }
    }
}
