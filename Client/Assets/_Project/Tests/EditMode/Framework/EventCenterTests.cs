// ============================================================================
//  M1-A3 · 事件中心的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2 / V7
//  对应记录：Docs/06-框架改造记录.md §三 FW-06 / FW-07、§四 P-16 / P-18
//
//  为什么全在 EditMode：`EventCenter` 继承的是 A1 的纯 C# 单例 `Singleton<T>`，
//  不碰 MonoBehaviour 生命周期，所以不需要播放模式（划分见 Docs/06 §9.7）。
//
//  这组测试里最重要的是第 9、10 两条 —— 它们把"派发中增删监听"的语义
//  **钉成契约**。P-18 的问题不是原版"错了"，而是**没人知道它会怎样**。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NBC.Framework;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NBC.Tests.EditMode
{
    /// <summary>测试用的事件声明。真实项目里每个模块各自声明自己的一组。</summary>
    internal static class TestEvents
    {
        public static readonly EventId Payload = EventId.Declare("Test.Payload");
        public static readonly EventId Void1 = EventId.Declare("Test.Void1");
        public static readonly EventId Void2 = EventId.Declare("Test.Void2");
        public static readonly EventId Never = EventId.Declare("Test.Never");
    }

    [TestFixture]
    public class EventCenterTests
    {
        private EventCenter m_center;

        [SetUp]
        public void SetUp()
        {
            // 单例静态状态隔离（EventCenter 是 Singleton<T>）
            SingletonRegistry.ResetAll();

            m_center = EventCenter.Instance;

            // 默认关掉"没有监听者"的告警，避免其它用例被噪音污染；
            // 专门测它的那条用例会自己打开。
            m_center.WarnOnMissingListener = false;
        }

        [TearDown]
        public void TearDown()
        {
            SingletonRegistry.ResetAll();
        }

        // ====================================================================
        //  基础：注册 / 触发 / 注销
        // ====================================================================

        [Test]
        public void AddEventListener_ThenTrigger_InvokesHandler()
        {
            int received = 0;
            m_center.AddEventListener(TestEvents.Payload, (int v) => received = v);

            m_center.Trigger(TestEvents.Payload, 42);

            Assert.AreEqual(42, received);
        }

        [Test]
        public void AddEventListener_NoPayloadOverload_Works()
        {
            int calls = 0;
            m_center.AddEventListener(TestEvents.Void1, () => calls++);

            m_center.Trigger(TestEvents.Void1);

            Assert.AreEqual(1, calls);
        }

        [Test]
        public void MultipleListeners_AreInvokedInRegistrationOrder()
        {
            List<int> order = new List<int>();
            m_center.AddEventListener(TestEvents.Void1, () => order.Add(1));
            m_center.AddEventListener(TestEvents.Void1, () => order.Add(2));
            m_center.AddEventListener(TestEvents.Void1, () => order.Add(3));

            m_center.Trigger(TestEvents.Void1);

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, order, "监听者应按注册顺序被调用");
        }

        [Test]
        public void RemoveEventListener_StopsDelivery()
        {
            int calls = 0;
            UnityEngine.Events.UnityAction handler = () => calls++;

            m_center.AddEventListener(TestEvents.Void1, handler);
            m_center.Trigger(TestEvents.Void1);
            Assert.AreEqual(1, calls);

            m_center.RemoveEventListener(TestEvents.Void1, handler);
            m_center.Trigger(TestEvents.Void1);
            Assert.AreEqual(1, calls, "注销后不应再收到事件");
        }

        [Test]
        public void RemoveEventListener_ForUnknownEvent_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
                m_center.RemoveEventListener(TestEvents.Never, (UnityEngine.Events.UnityAction)(() => { })));
        }

        [Test]
        public void RemoveEventListener_CleansUpEmptyChannel()
        {
            // P-16：原版移除监听后不清理空 key，字典里会一直残留空条目
            int before = m_center.ChannelCount;

            UnityEngine.Events.UnityAction handler = () => { };
            m_center.AddEventListener(TestEvents.Void1, handler);
            Assert.AreEqual(before + 1, m_center.ChannelCount);

            m_center.RemoveEventListener(TestEvents.Void1, handler);
            Assert.AreEqual(before, m_center.ChannelCount, "最后一个监听者被移除后，事件条目也应清掉");
        }

        // ====================================================================
        //  FW-06：未注册事件必须有反馈（原版是静默返回）
        // ====================================================================

        [Test]
        public void Trigger_WithNoListener_Warns()
        {
            m_center.WarnOnMissingListener = true;

            LogAssert.Expect(LogType.Warning, new Regex("没有任何监听者"));

            m_center.Trigger(TestEvents.Never);
        }

        [Test]
        public void Trigger_AfterRegisteringListener_DoesNotWarn()
        {
            m_center.WarnOnMissingListener = true;
            m_center.AddEventListener(TestEvents.Never, () => { });

            m_center.Trigger(TestEvents.Never);   // 有监听者，不应产生任何告警

            LogAssert.NoUnexpectedReceived();
        }

        // ====================================================================
        //  FW-07：同 key 异类型 —— 必须有明确异常，而不是 NRE
        // ====================================================================

        [Test]
        public void AddEventListener_SameIdDifferentType_ThrowsClearException()
        {
            m_center.AddEventListener(TestEvents.Payload, (int v) => { });

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => m_center.AddEventListener(TestEvents.Payload, (string v) => { }));

            StringAssert.Contains("Test.Payload", ex.Message, "异常消息要能定位到是哪个事件");
            StringAssert.Contains("Int32", ex.Message, "异常消息要说明已注册的类型");
            StringAssert.Contains("String", ex.Message, "异常消息要说明本次用的类型");
        }

        [Test]
        public void AddEventListener_GenericThenVoidOnSameId_Throws()
        {
            // 原版的第二条触发路径：泛型与非泛型共用同一个 key 空间
            m_center.AddEventListener(TestEvents.Void1, (int v) => { });

            Assert.Throws<InvalidOperationException>(
                () => m_center.AddEventListener(TestEvents.Void1, () => { }));
        }

        [Test]
        public void Trigger_WithMismatchedPayloadType_Throws()
        {
            m_center.AddEventListener(TestEvents.Payload, (int v) => { });

            Assert.Throws<InvalidOperationException>(() => m_center.Trigger(TestEvents.Payload, "not an int"));
        }

        [Test]
        public void AddEventListener_NullAction_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => m_center.AddEventListener(TestEvents.Void1, (UnityEngine.Events.UnityAction)null));
        }

        // ====================================================================
        //  P-18：派发中增删监听 —— 把语义钉成契约
        // ====================================================================

        [Test]
        public void AddListenerInsideHandler_DoesNotReceiveCurrentDispatch()
        {
            int addedCalls = 0;
            UnityEngine.Events.UnityAction added = () => addedCalls++;
            UnityEngine.Events.UnityAction first = () => m_center.AddEventListener(TestEvents.Void1, added);

            m_center.AddEventListener(TestEvents.Void1, first);

            m_center.Trigger(TestEvents.Void1);
            Assert.AreEqual(0, addedCalls, "派发中新增的监听者不应收到本次事件（本次名单已锁定）");

            m_center.Trigger(TestEvents.Void1);
            Assert.AreEqual(1, addedCalls, "但从下一次派发开始应当收到");
        }

        [Test]
        public void RemoveListenerInsideHandler_StillReceivesCurrentDispatch()
        {
            // ⚠️ 这条断言刻意把"看起来像 Bug"的行为固定成契约。
            // 原因：委托不可变，派发前读一次字段就锁定了本次名单；
            // 这与 C# 原生 event 的行为一致。行为"有测试盯着"和"碰巧如此"是两回事。
            int calls = 0;
            UnityEngine.Events.UnityAction second = null;
            UnityEngine.Events.UnityAction first = () =>
            {
                calls++;
                m_center.RemoveEventListener(TestEvents.Void1, second);
            };

            second = () => calls++;

            m_center.AddEventListener(TestEvents.Void1, first);
            m_center.AddEventListener(TestEvents.Void1, second);

            m_center.Trigger(TestEvents.Void1);
            Assert.AreEqual(2, calls, "本次派发的名单在开始时就锁定了，被移除的监听者仍会收到这一次");

            m_center.Trigger(TestEvents.Void1);
            Assert.AreEqual(3, calls, "下一次派发就只剩未被移除的那个");
        }

        // ====================================================================
        //  异常与递归防护
        // ====================================================================

        [Test]
        public void HandlerThrows_IsLogged_AndCenterStaysUsable()
        {
            m_center.AddEventListener(TestEvents.Void1, () => throw new System.InvalidOperationException("boom"));

            LogAssert.Expect(LogType.Exception, new Regex("boom"));
            m_center.Trigger(TestEvents.Void1);

            // 抛过异常之后，事件中心自身状态不能坏
            int calls = 0;
            m_center.Clear();
            m_center.AddEventListener(TestEvents.Void2, () => calls++);
            m_center.Trigger(TestEvents.Void2);

            Assert.AreEqual(1, calls, "一次监听者异常不应让事件中心失效");
        }

        [Test]
        public void RecursiveTrigger_IsStoppedAtDepthLimit()
        {
            // 递归触发如果不管，结果是栈溢出 —— 而栈溢出的报错几乎无法定位。
            UnityEngine.Events.UnityAction loop = null;
            loop = () => m_center.Trigger(TestEvents.Void1);
            m_center.AddEventListener(TestEvents.Void1, loop);

            LogAssert.Expect(LogType.Error, new Regex("派发深度"));

            Assert.DoesNotThrow(() => m_center.Trigger(TestEvents.Void1));
        }

        // ====================================================================
        //  清理与调试面板数据（V7）
        // ====================================================================

        [Test]
        public void Clear_RemovesEverything()
        {
            int calls = 0;
            m_center.AddEventListener(TestEvents.Void1, () => calls++);
            m_center.AddEventListener(TestEvents.Void2, () => calls++);

            m_center.Clear();

            Assert.AreEqual(0, m_center.ChannelCount);
            m_center.Trigger(TestEvents.Void1);
            m_center.Trigger(TestEvents.Void2);
            Assert.AreEqual(0, calls);
        }

        [Test]
        public void ClearListeners_RemovesOnlyThatEvent()
        {
            int calls = 0;
            m_center.AddEventListener(TestEvents.Void1, () => calls++);
            m_center.AddEventListener(TestEvents.Void2, () => calls++);

            m_center.ClearListeners(TestEvents.Void1);

            m_center.Trigger(TestEvents.Void1);
            m_center.Trigger(TestEvents.Void2);

            Assert.AreEqual(1, calls, "只应清掉指定事件的监听者");
        }

        [Test]
        public void GetListenerCount_ReflectsRegistrations()
        {
            Assert.AreEqual(0, m_center.GetListenerCount(TestEvents.Void1));

            UnityEngine.Events.UnityAction handler = () => { };
            m_center.AddEventListener(TestEvents.Void1, handler);
            m_center.AddEventListener(TestEvents.Void1, () => { });

            Assert.AreEqual(2, m_center.GetListenerCount(TestEvents.Void1));

            m_center.RemoveEventListener(TestEvents.Void1, handler);
            Assert.AreEqual(1, m_center.GetListenerCount(TestEvents.Void1));
        }

        [Test]
        public void CopyChannelStats_ReportsEveryChannel()
        {
            m_center.AddEventListener(TestEvents.Payload, (int v) => { });
            m_center.AddEventListener(TestEvents.Payload, (int v) => { });
            m_center.AddEventListener(TestEvents.Void1, () => { });

            List<EventChannelStat> buffer = new List<EventChannelStat>();
            m_center.CopyChannelStats(buffer);

            Assert.AreEqual(2, buffer.Count, "应报告两个事件");

            EventChannelStat payloadStat = buffer.Find(s => s.Id == TestEvents.Payload);
            Assert.AreEqual(2, payloadStat.ListenerCount);
            Assert.AreEqual(typeof(int), payloadStat.PayloadType);
            Assert.IsTrue(payloadStat.ToString().Contains("Test.Payload"), "面板要能直接打印出一行可读摘要");

            EventChannelStat voidStat = buffer.Find(s => s.Id == TestEvents.Void1);
            Assert.AreEqual(1, voidStat.ListenerCount);
            Assert.IsNull(voidStat.PayloadType, "无参数事件的 PayloadType 应为 null");
        }

        // ====================================================================
        //  EventId 自身
        // ====================================================================

        [Test]
        public void EventId_EqualityAndHash_AreByName()
        {
            EventId a = EventId.Declare("Test.Same");
            EventId b = EventId.Declare("Test.Same");
            EventId c = EventId.Declare("Test.Other");

            Assert.IsTrue(a == b);
            Assert.IsTrue(a != c);
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
            Assert.IsTrue(a.Equals((object)b));
            Assert.AreEqual("Test.Same", a.ToString());
        }

        [Test]
        public void DefaultEventId_IsInvalid()
        {
            EventId none = default(EventId);

            Assert.IsFalse(none.IsValid);
            Assert.AreEqual(string.Empty, none.Name);
            Assert.Throws<System.ArgumentException>(() => m_center.Trigger(none));
        }

        [Test]
        public void EventId_Declare_RejectsEmptyAndWhitespace()
        {
            Assert.Throws<System.ArgumentException>(() => EventId.Declare(null));
            Assert.Throws<System.ArgumentException>(() => EventId.Declare(""));
            Assert.Throws<System.ArgumentException>(() => EventId.Declare("Has Space"));
            Assert.Throws<System.ArgumentException>(() => EventId.Declare("Has\tTab"));
        }

        [Test]
        public void EventId_Declare_ReturnsUsableId()
        {
            EventId id = EventId.Declare("Test.Fresh");

            Assert.IsTrue(id.IsValid);
            Assert.AreEqual("Test.Fresh", id.Name);
        }

        // ====================================================================
        //  交叉验证：A1 的单例保证在 EventCenter 上同样成立
        // ====================================================================

        [Test]
        public void EventCenter_CannotBeConstructedDirectly()
        {
            Assert.Throws<System.InvalidOperationException>(() => new EventCenter());
        }

        [Test]
        public void EventCenter_InstanceAndGetInstance_AreSame()
        {
            Assert.AreSame(EventCenter.Instance, EventCenter.GetInstance());
        }
    }
}
