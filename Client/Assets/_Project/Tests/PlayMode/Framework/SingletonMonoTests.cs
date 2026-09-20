// ============================================================================
//  M1-A1 · MonoBehaviour 单例的 PlayMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2（FW-02、FW-03、FW-M01）
//  对应记录：Docs/06-框架改造记录.md §三 FW-02 / FW-03、§9.7
//
//  ---------------------------------------------------------------------------
//  为什么这些用例在 PlayMode 而不是 EditMode
//  ---------------------------------------------------------------------------
//  实测（见 EditMode 那份文件顶部的 [DIAG] 记录）：**EditMode 下 AddComponent 不触发 Awake**，
//  连最普通的 MonoBehaviour 都一样。所以"单例有没有被 Awake 注册"这件事
//  在 EditMode 里根本测不出来 —— 放在那里的结论是假的。
//
//  这里因此改用 `[UnityTest]` + `yield return null`：真的进入播放模式，
//  让 Awake / OnEnable / OnDestroy 真正发生。代价是 PlayMode 测试慢得多，
//  所以只把"必须依赖生命周期"的用例放这里，纯逻辑仍留在 EditMode。
//
//  ---------------------------------------------------------------------------
//  两个 PlayMode 特有的写法差异（和 EditMode 版对比）
//  ---------------------------------------------------------------------------
//  ① 销毁对象用 `Object.Destroy` + `yield return null`，不用 `DestroyImmediate`
//     —— Unity 明确不推荐在播放模式下用后者。
//  ② 清理钩子用 `[UnityTearDown]`（可 yield），而不是同步的 `[TearDown]`：
//     销毁是延迟到帧末的，必须等一帧才能确认 OnDestroy 真的跑过。
// ============================================================================

using System.Collections;
using System.Collections.Generic;
using NBC.Framework;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NBC.Tests.PlayMode
{
    // ========================================================================
    //  被测对象
    // ========================================================================

    /// <summary>场景挂载型单例的测试替身。</summary>
    public sealed class ProbeMonoSingleton : SingletonMono<ProbeMonoSingleton>
    {
        public int AwakeCount;
        public int DestroyCount;

        protected override void OnAwake()
        {
            AwakeCount++;
        }

        protected override void OnBeforeDestroy()
        {
            DestroyCount++;
        }
    }

    /// <summary>自动创建型单例的测试替身。</summary>
    public sealed class ProbeAutoMonoSingleton : SingletonAutoMono<ProbeAutoMonoSingleton>
    {
        public int AwakeCount;

        protected override void OnAwake()
        {
            AwakeCount++;
        }
    }

    // ========================================================================
    //  测试
    // ========================================================================

    [TestFixture]
    public class SingletonMonoTests
    {
        private readonly List<GameObject> m_spawned = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            SingletonRegistry.ResetAll();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (int i = 0; i < m_spawned.Count; i++)
            {
                if (m_spawned[i] != null)
                {
                    Object.Destroy(m_spawned[i]);
                }
            }

            m_spawned.Clear();

            // 等一帧让 Destroy 真正生效（OnDestroy 里会清静态引用）
            yield return null;

            SingletonRegistry.ResetAll();
        }

        private GameObject Spawn(string name)
        {
            GameObject go = new GameObject(name);
            m_spawned.Add(go);
            return go;
        }

        // --------------------------------------------------------------------
        //  FW-02：场景挂载型
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator SingletonMono_WhenInScene_ReturnsTheComponent()
        {
            GameObject go = Spawn("ProbeMono");
            ProbeMonoSingleton comp = go.AddComponent<ProbeMonoSingleton>();
            yield return null;

            Assert.AreSame(comp, ProbeMonoSingleton.Instance);
            Assert.AreSame(comp, ProbeMonoSingleton.GetInstance());
            Assert.IsTrue(ProbeMonoSingleton.HasInstance);
            Assert.AreEqual(1, comp.AwakeCount, "OnAwake 应被调用一次");
        }

        [UnityTest]
        public IEnumerator SingletonMono_FindFallback_FindsExistingComponentAfterStaticReset()
        {
            // FW-02 的核心验收点：原版的 GetInstance() 只 return 静态字段，
            // 一旦静态状态丢失（关闭 Domain Reload 重进 Play、或代码里手动重置），就返回 null。
            // 改造后应当能主动在场景里找到，而且**不应**误报"Awake 没执行"。
            GameObject go = Spawn("ProbeMonoFallback");
            ProbeMonoSingleton comp = go.AddComponent<ProbeMonoSingleton>();
            yield return null;

            SingletonRegistry.ResetAll();   // 模拟"静态状态丢了，但对象还在场景里"

            Assert.AreSame(comp, ProbeMonoSingleton.Instance, "应通过兜底查找拿回同一个组件");
        }

        [UnityTest]
        public IEnumerator SingletonMono_AfterDestroy_DoesNotReturnDestroyedObject()
        {
            GameObject go = Spawn("ProbeMonoDestroy");
            ProbeMonoSingleton comp = go.AddComponent<ProbeMonoSingleton>();
            yield return null;

            Assert.IsTrue(ProbeMonoSingleton.HasInstance);

            Object.Destroy(go);
            yield return null;

            // 注意：这里【不能】写 Assert.IsNull(comp)。
            // NUnit 的 IsNull 收到的是 object 类型形参，"== null" 走引用比较；
            // 而 Unity 的"伪 null"只在静态类型是 UnityEngine.Object 时才会命中重载的 operator ==。
            Assert.IsTrue(comp == null, "Unity 的伪 null：被销毁的对象 == null 为 true");
            Assert.IsFalse(ProbeMonoSingleton.HasInstance, "销毁后不应再持有实例");
            Assert.IsNull(ProbeMonoSingleton.Instance, "场景里没有实例时应返回 null，而不是已销毁对象");
            Assert.AreEqual(1, comp.DestroyCount, "OnBeforeDestroy 应被调用一次（托管对象仍在，字段可读）");
        }

        [UnityTest]
        public IEnumerator SingletonMono_DuplicateInScene_KeepsFirstAndLogsError()
        {
            GameObject first = Spawn("ProbeMonoDup1");
            ProbeMonoSingleton firstComp = first.AddComponent<ProbeMonoSingleton>();
            yield return null;

            GameObject second = Spawn("ProbeMonoDup2");

            // 期望：第二个实例被明确拒绝（LogError），而不是安静地各自为政。
            // Unity Test Runner 默认把"未预期的 LogError"判为失败，所以这里显式声明期望。
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("场景中存在多个"));
            ProbeMonoSingleton secondComp = second.AddComponent<ProbeMonoSingleton>();
            yield return null;

            Assert.AreSame(firstComp, ProbeMonoSingleton.Instance, "应保留先注册的那个");
            Assert.AreNotSame(secondComp, ProbeMonoSingleton.Instance);
        }

        // --------------------------------------------------------------------
        //  FW-03：自动创建型
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator SingletonAutoMono_WhenNotInScene_CreatesOne()
        {
            ProbeAutoMonoSingleton instance = ProbeAutoMonoSingleton.Instance;
            yield return null;

            Assert.IsNotNull(instance, "自动创建型单例不应返回 null");
            Assert.AreEqual(nameof(ProbeAutoMonoSingleton), instance.gameObject.name,
                "自动创建的对象应当以类型名命名，便于在 Hierarchy 里辨认");
            Assert.AreSame(instance, ProbeAutoMonoSingleton.GetInstance());
            Assert.AreEqual(1, instance.AwakeCount, "OnAwake 应被调用一次");

            m_spawned.Add(instance.gameObject);
        }

        [UnityTest]
        public IEnumerator SingletonAutoMono_CalledRepeatedly_DoesNotCreateMoreThanOne()
        {
            ProbeAutoMonoSingleton a = ProbeAutoMonoSingleton.Instance;
            yield return null;
            ProbeAutoMonoSingleton b = ProbeAutoMonoSingleton.Instance;
            yield return null;

            Assert.AreSame(a, b);

            // 用场景实际对象数量再确认一次：不是"返回了同一个引用"而已
            ProbeAutoMonoSingleton[] all =
                Object.FindObjectsByType<ProbeAutoMonoSingleton>(FindObjectsSortMode.None);
            Assert.AreEqual(1, all.Length, "场景中只应存在一个自动创建的单例");

            m_spawned.Add(a.gameObject);
        }

        [UnityTest]
        public IEnumerator SingletonAutoMono_AlreadyInScene_ReusesInsteadOfCreatingSecond()
        {
            // 这是原版没有的一步：原版只会"没有就建"，不管"其实已经有了"。
            GameObject go = Spawn("ProbeAutoMonoPreexisting");
            ProbeAutoMonoSingleton placed = go.AddComponent<ProbeAutoMonoSingleton>();
            yield return null;

            // 必须先清掉静态状态，否则 Instance 走快路径直接返回，压根到不了"先找再建"那一段。
            // 这个顺序也说明了那段代码的真实触发场景：
            // 静态状态丢失（关闭 Domain Reload 后重进 Play、或手动重置）而场景对象还在。
            SingletonRegistry.ResetAll();

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("场景中已存在"));

            ProbeAutoMonoSingleton got = ProbeAutoMonoSingleton.Instance;
            yield return null;

            Assert.AreSame(placed, got, "场景里已有实例时应直接复用");

            ProbeAutoMonoSingleton[] all =
                Object.FindObjectsByType<ProbeAutoMonoSingleton>(FindObjectsSortMode.None);
            Assert.AreEqual(1, all.Length, "不应再额外创建一个");
        }

        [UnityTest]
        public IEnumerator SingletonAutoMono_AfterDestroy_RecreatesOnNextAccess()
        {
            ProbeAutoMonoSingleton first = ProbeAutoMonoSingleton.Instance;
            yield return null;

            Object.Destroy(first.gameObject);
            yield return null;

            Assert.IsFalse(ProbeAutoMonoSingleton.HasInstance);

            ProbeAutoMonoSingleton second = ProbeAutoMonoSingleton.Instance;
            yield return null;

            Assert.IsNotNull(second);
            Assert.AreNotSame(first, second, "实例被销毁后应能自动重建");

            m_spawned.Add(second.gameObject);
        }
    }
}
