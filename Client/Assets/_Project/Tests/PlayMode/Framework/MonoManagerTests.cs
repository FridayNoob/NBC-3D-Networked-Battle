// ============================================================================
//  M1-A4 · MonoManager 的 PlayMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2
//  对应记录：Docs/06-框架改造记录.md §四 P-05 / P-13、§十二
//
//  为什么全在 PlayMode（A 组第一个必须这样的模块）：
//  这个类存在的意义就是"每帧被调用" —— `Update` / `LateUpdate` / `FixedUpdate`、
//  定时器推进、协程。这些**只有在播放模式下才真的会发生**。
//  EditMode 下连 `Awake` 都不会被调用（A1 实测结论，见 Docs/06 §9.7），
//  所以在这里写 EditMode 测试等于自欺。
//
//  关于测试之间隔离：MonoManager 是常驻的（DontDestroyOnLoad），不会自己销毁。
//  所以每个用例结束时必须自己清干净 —— 这正是 `RemoveAllListeners` / `CancelAllTimers`
//  这两个公开接口的用途（它们本身也是"切场景收尾"需要的 API）。
// ============================================================================

using System.Collections;
using System.Text.RegularExpressions;
using NBC.Framework;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NBC.Tests.PlayMode
{
    [TestFixture]
    public class MonoManagerTests
    {
        private MonoManager m_mono;

        [SetUp]
        public void SetUp()
        {
            m_mono = MonoManager.Instance;
            Assert.IsNotNull(m_mono, "MonoManager 应当能自动创建");
            m_mono.RemoveAllListeners();
            m_mono.CancelAllTimers();
            m_mono.IsPaused = false;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (m_mono != null)
            {
                m_mono.RemoveAllListeners();
                m_mono.CancelAllTimers();
                m_mono.StopAll();
                m_mono.IsPaused = false;
            }

            yield return null;
        }

        // ====================================================================
        //  每帧回调
        // ====================================================================

        [UnityTest]
        public IEnumerator AddUpdateListener_IsCalledEveryFrame()
        {
            int count = 0;
            UnityEngine.Events.UnityAction listener = () => count++;
            m_mono.AddUpdateListener(listener);

            yield return null;
            yield return null;
            yield return null;

            Assert.GreaterOrEqual(count, 3, "注册后每帧都应被调用");
        }

        [UnityTest]
        public IEnumerator RemoveUpdateListener_StopsCalling()
        {
            int count = 0;
            UnityEngine.Events.UnityAction listener = () => count++;
            m_mono.AddUpdateListener(listener);

            yield return null;
            int afterOneFrame = count;
            Assert.GreaterOrEqual(afterOneFrame, 1);

            m_mono.RemoveUpdateListener(listener);
            yield return null;
            yield return null;

            Assert.AreEqual(afterOneFrame, count, "注销之后不应再被调用");
        }

        [UnityTest]
        public IEnumerator ReferenceTypeListener_CanBeRemovedBySameMethodGroup()
        {
            // 这条守的是一个很容易踩的坑：
            // 取"方法组"（method group）生成委托时，C# 里委托的 == 比较的是"目标对象 + 方法"，
            // 所以同一个实例方法能正确匹配、能删掉。
            // 但如果用 lambda，两次写 `() => Foo()` 是**两个不同的委托对象**，删不掉 ——
            // 这是"订阅了却退不掉"最常见的成因（审计 P-05 关注的就是这类不对称）。
            m_methodGroupTicks = 0;
            m_mono.AddUpdateListener(OnTickMethodGroup);

            yield return null;
            m_mono.RemoveUpdateListener(OnTickMethodGroup);
            int afterRemove = m_methodGroupTicks;
            yield return null;
            yield return null;

            Assert.AreEqual(afterRemove, m_methodGroupTicks, "同一个方法组应能正确注销");
        }

        private int m_methodGroupTicks;

        private void OnTickMethodGroup()
        {
            m_methodGroupTicks++;
        }

        [UnityTest]
        public IEnumerator FixedUpdateListener_IsCalled()
        {
            int count = 0;
            UnityEngine.Events.UnityAction listener = () => count++;
            m_mono.AddFixedUpdateListener(listener);

            // 物理帧默认 50Hz，等 0.1 秒足够跑到几次
            yield return new WaitForSeconds(0.1f);

            Assert.Greater(count, 0, "FixedUpdate 回调应被调用");
        }

        [UnityTest]
        public IEnumerator LateUpdateListener_IsCalled()
        {
            int count = 0;
            UnityEngine.Events.UnityAction listener = () => count++;
            m_mono.AddLateUpdateListener(listener);

            yield return null;
            yield return null;

            Assert.GreaterOrEqual(count, 2, "LateUpdate 回调应被调用");
        }

        // ====================================================================
        //  派发中增删 —— 与 A3 事件中心保持同一套语义
        // ====================================================================

        [UnityTest]
        public IEnumerator AddDuringDispatch_TakesEffectNextFrame()
        {
            int addedCalls = 0;
            UnityEngine.Events.UnityAction added = () => addedCalls++;
            UnityEngine.Events.UnityAction adder = () => m_mono.AddUpdateListener(added);
            m_mono.AddUpdateListener(adder);

            yield return null;      // 这一帧 adder 执行，把 added 攒进待处理队列
            Assert.AreEqual(0, addedCalls, "派发中新增的监听者本帧不应被调用");

            yield return null;      // 下一帧开始，added 生效
            Assert.GreaterOrEqual(addedCalls, 1, "下一帧起应当被调用");
        }

        [UnityTest]
        public IEnumerator RemoveDuringDispatch_TakesEffectNextFrame()
        {
            int firstCalls = 0;
            int secondCalls = 0;
            UnityEngine.Events.UnityAction second = () => secondCalls++;
            UnityEngine.Events.UnityAction first = () =>
            {
                firstCalls++;
                m_mono.RemoveUpdateListener(second);
            };

            m_mono.AddUpdateListener(first);
            m_mono.AddUpdateListener(second);

            yield return null;
            Assert.AreEqual(1, firstCalls);
            Assert.AreEqual(1, secondCalls, "本帧名单已锁定，被移除的监听者仍会收到这一次");

            int secondAfter = secondCalls;
            yield return null;
            yield return null;
            Assert.AreEqual(secondAfter, secondCalls, "下一帧起不应再被调用");
        }

        // ====================================================================
        //  定时器 / 延迟调用
        // ====================================================================

        [UnityTest]
        public IEnumerator CallLater_FiresOnceAfterDelay()
        {
            int count = 0;
            m_mono.CallLater(0.05f, () => count++);

            yield return null;
            Assert.AreEqual(0, count, "还没到点就不该触发");

            yield return new WaitForSeconds(0.12f);
            Assert.AreEqual(1, count, "到点后应触发一次");

            yield return new WaitForSeconds(0.12f);
            Assert.AreEqual(1, count, "延迟调用只应触发一次");
        }

        [UnityTest]
        public IEnumerator CallEvery_FiresRepeatedly()
        {
            int count = 0;
            m_mono.CallEvery(0.05f, () => count++);

            yield return new WaitForSeconds(0.22f);

            Assert.GreaterOrEqual(count, 3, "重复定时器应多次触发");
            Assert.LessOrEqual(count, 6, "间隔 0.05 秒 / 总时长 0.22 秒，不应明显超出预期次数");
        }

        [UnityTest]
        public IEnumerator Cancel_StopsTimer()
        {
            int count = 0;
            TimerHandle handle = m_mono.CallEvery(0.05f, () => count++);
            Assert.IsTrue(handle.IsValid);

            yield return new WaitForSeconds(0.12f);
            int beforeCancel = count;
            Assert.Greater(beforeCancel, 0);

            m_mono.Cancel(handle);
            yield return new WaitForSeconds(0.15f);

            Assert.AreEqual(beforeCancel, count, "取消后不应再触发");
        }

        [UnityTest]
        public IEnumerator Cancel_WithInvalidOrUnknownHandle_IsSafe()
        {
            m_mono.Cancel(TimerHandle.Invalid);      // 不该抛异常
            m_mono.Cancel(new TimerHandle(999999));  // 不存在的句柄也不该抛异常
            yield return null;
        }

        [UnityTest]
        public IEnumerator CancelAllTimers_StopsEverything()
        {
            int count = 0;
            m_mono.CallEvery(0.03f, () => count++);
            m_mono.CallLater(0.03f, () => count++);
            Assert.AreEqual(2, m_mono.TimerCount);

            m_mono.CancelAllTimers();
            Assert.AreEqual(0, m_mono.TimerCount);

            yield return new WaitForSeconds(0.1f);
            Assert.AreEqual(0, count, "全部取消后不应再有任何触发");
        }

        // ====================================================================
        //  暂停语义（文件头已写死：只停时间，不停每帧回调）
        // ====================================================================

        [UnityTest]
        public IEnumerator IsPaused_TimersDoNotAdvance()
        {
            int count = 0;
            m_mono.CallEvery(0.03f, () => count++);

            yield return new WaitForSeconds(0.08f);
            int beforePause = count;
            Assert.Greater(beforePause, 0);

            m_mono.IsPaused = true;
            yield return new WaitForSeconds(0.1f);

            Assert.AreEqual(beforePause, count, "暂停期间定时器不应推进");
        }

        [UnityTest]
        public IEnumerator IsPaused_UpdateListenersStillRun()
        {
            int count = 0;
            UnityEngine.Events.UnityAction listener = () => count++;
            m_mono.AddUpdateListener(listener);

            m_mono.IsPaused = true;
            yield return null;
            yield return null;

            Assert.GreaterOrEqual(count, 2, "暂停只影响时间相关的功能，每帧回调应照常执行");
        }

        // ====================================================================
        //  协程宿主（FW-M13）
        // ====================================================================

        [UnityTest]
        public IEnumerator Run_ExecutesCoroutine_AndStop_InterruptsIt()
        {
            m_coroutineSteps = 0;

            Coroutine routine = m_mono.Run(TickForever());
            yield return null;
            yield return null;
            Assert.GreaterOrEqual(m_coroutineSteps, 2, "协程应当被执行");

            m_mono.Stop(routine);
            int afterStop = m_coroutineSteps;
            yield return null;
            yield return null;
            Assert.AreEqual(afterStop, m_coroutineSteps, "Stop 之后协程不应继续");
        }

        private int m_coroutineSteps;

        private IEnumerator TickForever()
        {
            while (true)
            {
                m_coroutineSteps++;
                yield return null;
            }
        }

        // ====================================================================
        //  健壮性：一个回调抛异常不能拖垮整个帧驱动
        // ====================================================================

        [UnityTest]
        public IEnumerator OneListenerThrows_DoesNotStopOthers()
        {
            int healthyCalls = 0;
            int throws = 0;

            // 只抛一次：LogAssert.Expect 期望"恰好一条"匹配的日志，
            // 每帧都抛的话第二帧就会变成"未预期的日志"，测试会以另一种方式失败。
            m_mono.AddUpdateListener(() =>
            {
                if (throws++ == 0)
                {
                    throw new System.InvalidOperationException("boom");
                }
            });
            m_mono.AddUpdateListener(() => healthyCalls++);

            LogAssert.Expect(LogType.Exception, new Regex("boom"));
            yield return null;

            Assert.GreaterOrEqual(healthyCalls, 1, "一个回调抛异常不应影响其它回调");

            // 清理，避免影响后续帧与其它用例
            m_mono.RemoveAllListeners();
        }

        // ====================================================================
        //  P-13 的修复证据：宿主就地受保护，且常驻
        // ====================================================================

        [UnityTest]
        public IEnumerator Host_IsProtectedByDontDestroyOnLoad()
        {
            yield return null;

            Assert.AreEqual("DontDestroyOnLoad", m_mono.gameObject.scene.name,
                "宿主必须在创建那一刻就进入 DontDestroyOnLoad，不能等到 Start（原版 P-13 的一帧窗口）");
        }

        [UnityTest]
        public IEnumerator Host_IsReused_NotRecreated()
        {
            MonoManager again = MonoManager.Instance;
            yield return null;

            Assert.AreSame(m_mono, again, "第二次访问应复用同一个宿主，而不是再建一个");
        }
    }
}
