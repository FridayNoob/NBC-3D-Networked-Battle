// ============================================================================
//  M1-A7 · InputManager 的 EditMode 测试
//  对应验收：Docs/16-M1开工清单.md V1 / V2
//  对应记录：Docs/06-框架改造记录.md §四 P-14、§十五
//
//  ---------------------------------------------------------------------------
//  为什么要注入假采集源，而不是真按键
//  ---------------------------------------------------------------------------
//  **真键盘在自动化测试里造不出来**（`UnityEngine.Input` 需要真实设备）。
//  所以 A7 把采集抽成 `IInputSource`，测试喂一个假实现：
//    · 可以精确指定"这一帧按下了哪些动作、移动轴是多少"
//    · 可以**统计"到底查了哪些动作"** —— 这正是 P-14 的修复证据（见下）
//
//  这也是"采集与命令分离"这条需求的**直接回报**：分开之后，命令层变得可测。
//
//  ---------------------------------------------------------------------------
//  本文件最核心的两条
//  ---------------------------------------------------------------------------
//    · `Tick_OnlyQueriesTrackedActions` —— 原版每帧扫 4 个写死的键、调 8 次 GetKey；
//      改造后**只问游戏真正登记过的动作**
//    · `Pause_DoesNotQueryAnyAction`   —— 禁用时**一次键都不查**（零开销），
//      而不是"查完再丢掉"
// ============================================================================

using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Input;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>假采集源：状态由测试直接指定，并记录"被查询了哪些动作"。</summary>
    internal sealed class FakeInputSource : IInputSource
    {
        public int MoveXRaw;
        public int MoveYRaw;

        /// <summary>本帧"刚按下"的动作索引。</summary>
        public readonly HashSet<int> Pressed = new HashSet<int>();

        /// <summary>本帧"刚松开"的动作索引。</summary>
        public readonly HashSet<int> Released = new HashSet<int>();

        /// <summary>当前"被按住"的动作索引。</summary>
        public readonly HashSet<int> Held = new HashSet<int>();

        /// <summary>Poll 被调了几次。</summary>
        public int PollCalls;

        /// <summary>被查询过的动作索引（用来证明"只查登记过的动作"）。</summary>
        public readonly List<int> QueriedActions = new List<int>();

        public void Poll()
        {
            PollCalls++;
        }

        public int MoveX
        {
            get { return MoveXRaw; }
        }

        public int MoveY
        {
            get { return MoveYRaw; }
        }

        public bool WasActionPressed(InputActionId action)
        {
            QueriedActions.Add(action.Index);
            return Pressed.Contains(action.Index);
        }

        public bool WasActionReleased(InputActionId action)
        {
            QueriedActions.Add(action.Index);
            return Released.Contains(action.Index);
        }

        public bool IsActionHeld(InputActionId action)
        {
            QueriedActions.Add(action.Index);
            return Held.Contains(action.Index);
        }

        public void Reset()
        {
            Pressed.Clear();
            Released.Clear();
            QueriedActions.Clear();
            PollCalls = 0;
        }
    }

    [TestFixture]
    public class InputManagerTests
    {
        private FakeInputSource m_source;
        private InputManager m_input;
        private InputActionId m_skill1;
        private InputActionId m_skill2;

        [SetUp]
        public void SetUp()
        {
            // `InputActionId.Declare` 有全局重名检查（两个动作抢同一索引会直接抛），
            // 所以测试之间要清掉声明记录。
            InputActionId.ResetDeclarationsForTests();
            SingletonRegistry.ResetAll();

            m_skill1 = InputActionId.Declare(0, "Skill1");
            m_skill2 = InputActionId.Declare(1, "Skill2");

            m_input = InputManager.Instance;
            m_source = new FakeInputSource();
            m_input.Source = m_source;
        }

        [TearDown]
        public void TearDown()
        {
            InputActionId.ResetDeclarationsForTests();
            SingletonRegistry.ResetAll();
        }

        // ====================================================================
        //  基础：采集 → 命令
        // ====================================================================

        [Test]
        public void Tick_ProducesCommandCarryingTheTickNumber()
        {
            InputCommand command = m_input.Tick(42);

            Assert.AreEqual(42, command.Tick);
            Assert.AreEqual(42, m_input.Current.Tick);
            Assert.IsTrue(command.IsEmpty, "没有任何输入时命令应为空");
        }

        [Test]
        public void Tick_ForwardsMoveAxis()
        {
            m_source.MoveXRaw = 500;
            m_source.MoveYRaw = -1000;

            InputCommand command = m_input.Tick(1);

            Assert.AreEqual(500, command.MoveX);
            Assert.AreEqual(-1000, command.MoveY);
            Assert.IsTrue(command.HasMove);
        }

        [Test]
        public void Tick_ClampsOutOfRangeAxis()
        {
            // 后端给出越界值（摇杆漂移 / 第三方设备）不应污染命令
            m_source.MoveXRaw = 999999;
            m_source.MoveYRaw = -999999;

            InputCommand command = m_input.Tick(1);

            Assert.AreEqual(InputCommand.MoveScale, command.MoveX);
            Assert.AreEqual(-InputCommand.MoveScale, command.MoveY);
        }

        [Test]
        public void Tick_ClampsNegativeOverflowToo()
        {
            m_source.MoveXRaw = int.MinValue;

            Assert.AreEqual(-InputCommand.MoveScale, m_input.Tick(1).MoveX);
        }

        // ====================================================================
        //  动作边沿：按下 + 松开
        // ====================================================================

        [Test]
        public void Tick_ProducesPressedBitsForTrackedActions()
        {
            m_input.TrackAction(m_skill1);
            m_input.TrackAction(m_skill2);
            m_source.Pressed.Add(m_skill2.Index);

            InputCommand command = m_input.Tick(1);

            Assert.IsFalse(command.HasAction(m_skill1));
            Assert.IsTrue(command.HasAction(m_skill2));
        }

        [Test]
        public void Tick_ProducesReleasedBits()
        {
            // "松手"这条边沿必须有 —— 否则逻辑层不知道玩家什么时候松手，
            // 表现就是"松手了还在开火"。
            m_input.TrackAction(m_skill1);
            m_source.Released.Add(m_skill1.Index);

            InputCommand command = m_input.Tick(1);

            Assert.IsFalse(command.HasAction(m_skill1), "这一帧只是松开，不是按下");
            Assert.IsTrue(command.HasActionReleased(m_skill1));
        }

        [Test]
        public void Tick_UntrackedActionNeverEntersCommand()
        {
            // 没登记的动作：哪怕采集源说它被按了，也不该进命令
            m_source.Pressed.Add(m_skill1.Index);

            InputCommand command = m_input.Tick(1);

            Assert.IsTrue(command.IsEmpty, "没登记过的动作不应出现在命令里");
        }

        [Test]
        public void TrackAction_IsIdempotent()
        {
            m_input.TrackAction(m_skill1);
            m_input.TrackAction(m_skill1);
            m_input.TrackAction(m_skill1);

            Assert.AreEqual(1, m_input.TrackedActionCount);
        }

        // ====================================================================
        //  P-14 的修复证据：只查登记过的动作
        // ====================================================================

        [Test]
        public void Tick_OnlyQueriesTrackedActions()
        {
            // 原版：每帧扫 4 个写死的键、调 8 次 GetKey。
            // 改造后：只问游戏真正登记过的动作。
            m_input.TrackAction(m_skill1);
            m_source.Reset();

            m_input.Tick(1);

            // 每个动作查 2 次（按下 + 松开），所以登记 1 个动作 = 2 次查询
            Assert.AreEqual(2, m_source.QueriedActions.Count,
                "只登记了 1 个动作，却查询了 " + m_source.QueriedActions.Count + " 次");
            CollectionAssert.AreEqual(new[] { m_skill1.Index, m_skill1.Index }, m_source.QueriedActions);
        }

        [Test]
        public void Tick_QueriesScaleWithTrackedActions_NotWithAHardcodedKeyList()
        {
            m_input.TrackAction(m_skill1);
            m_input.TrackAction(m_skill2);
            m_source.Reset();

            m_input.Tick(1);

            Assert.AreEqual(4, m_source.QueriedActions.Count, "2 个动作 × 2 条边沿 = 4 次查询");
        }

        [Test]
        public void Tick_PollsSourceExactlyOnce()
        {
            m_input.Tick(1);
            m_input.Tick(2);
            m_input.Tick(3);

            Assert.AreEqual(3, m_source.PollCalls, "每帧只应采集一次");
        }

        // ====================================================================
        //  暂停 / 禁用
        // ====================================================================

        [Test]
        public void Pause_ProducesEmptyCommand()
        {
            m_input.TrackAction(m_skill1);
            m_source.MoveXRaw = 1000;
            m_source.Pressed.Add(m_skill1.Index);

            m_input.Pause();
            InputCommand command = m_input.Tick(1);

            Assert.IsTrue(command.IsEmpty, "禁用期间命令应为空（玩家无法操作）");
            Assert.IsFalse(command.HasMove);
        }

        [Test]
        public void Pause_DoesNotQueryAnyAction()
        {
            // 禁用要**省掉开销**，而不是"查完再丢掉"
            m_input.TrackAction(m_skill1);
            m_input.Pause();
            m_source.Reset();

            m_input.Tick(1);

            Assert.AreEqual(0, m_source.QueriedActions.Count, "禁用期间不应查询任何动作");
            Assert.AreEqual(0, m_source.PollCalls, "禁用期间也不该采集");
        }

        [Test]
        public void Pause_DoesNotRaiseCommandGenerated()
        {
            int raised = 0;
            m_input.CommandGenerated += c => raised++;

            m_input.Tick(1);
            Assert.AreEqual(1, raised);

            m_input.Pause();
            m_input.Tick(2);
            Assert.AreEqual(1, raised, "禁用期间不应触发 CommandGenerated（禁用后无输入命令）");
        }

        [Test]
        public void Resume_RestoresInput()
        {
            m_input.TrackAction(m_skill1);
            m_input.Pause();
            m_input.Tick(1);

            m_source.Reset();
            m_source.Pressed.Add(m_skill1.Index);

            m_input.Resume();
            InputCommand command = m_input.Tick(2);

            Assert.IsTrue(command.HasAction(m_skill1));
        }

        // ====================================================================
        //  事件
        // ====================================================================

        [Test]
        public void CommandGenerated_RaisedOncePerTick()
        {
            List<int> ticks = new List<int>();
            m_input.CommandGenerated += c => ticks.Add(c.Tick);

            m_input.Tick(10);
            m_input.Tick(11);
            m_input.Tick(12);

            CollectionAssert.AreEqual(new[] { 10, 11, 12 }, ticks);
        }

        // ====================================================================
        //  引导错误要说清楚
        // ====================================================================

        [Test]
        public void Tick_WithoutSource_ThrowsWithActionableMessage()
        {
            SingletonRegistry.ResetAll();
            InputManager fresh = InputManager.Instance;

            System.InvalidOperationException ex = Assert.Throws<System.InvalidOperationException>(
                () => fresh.Tick(1));

            StringAssert.Contains("Source", ex.Message, "报错要告诉人怎么修");
        }

        [Test]
        public void InputManager_CannotBeConstructedDirectly()
        {
            Assert.Throws<System.InvalidOperationException>(() => new InputManager());
        }

        // ====================================================================
        //  InputActionId 自身
        // ====================================================================

        [Test]
        public void InputActionId_Declare_RejectsDuplicateIndex()
        {
            // 两个动作抢同一个索引 → 位掩码无法区分 → 必须在**声明处**就报错
            System.InvalidOperationException ex = Assert.Throws<System.InvalidOperationException>(
                () => InputActionId.Declare(m_skill1.Index, "另一个技能"));

            StringAssert.Contains("已经被声明过", ex.Message);
        }

        [Test]
        public void InputActionId_Declare_RejectsOutOfRangeIndex()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => InputActionId.Declare(InputActionId.MaxActionCount, "越界"));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => InputActionId.Declare(-1, "越界"));
        }

        [Test]
        public void InputActionId_CarriesName()
        {
            Assert.AreEqual("Skill1", m_skill1.Name);
            Assert.AreEqual(0, m_skill1.Index);
        }

        [Test]
        public void InputActionId_EqualityIsByIndex()
        {
            InputActionId again = InputActionId.Declare(0, "Skill1");
            Assert.IsTrue(m_skill1 == again);
            Assert.IsTrue(m_skill1.Equals((object)again));
            Assert.AreEqual(m_skill1.GetHashCode(), again.GetHashCode());
            Assert.IsTrue(m_skill1 != m_skill2);
        }

        // ====================================================================
        //  映射表
        // ====================================================================

        [Test]
        public void InputMapping_BindAndRebind()
        {
            InputMapping mapping = InputMapping.CreateDefault();
            mapping.Bind(m_skill1, KeyCode.Alpha1);

            KeyCode key;
            Assert.IsTrue(mapping.TryGetKey(m_skill1, out key));
            Assert.AreEqual(KeyCode.Alpha1, key);

            // "可配置"的核心：**能改**。原版把 WASD 烧死在 CheckKeyCode 里，连改的机会都没有。
            mapping.Bind(m_skill1, KeyCode.Q);
            Assert.IsTrue(mapping.TryGetKey(m_skill1, out key));
            Assert.AreEqual(KeyCode.Q, key);
        }

        [Test]
        public void InputMapping_Validate_DetectsTwoActionsOnOneKey()
        {
            InputMapping mapping = InputMapping.CreateDefault();
            mapping.Bind(m_skill1, KeyCode.Q);
            mapping.Bind(m_skill2, KeyCode.Q);

            KeyCode conflict;
            Assert.IsFalse(mapping.Validate(out conflict), "两个动作绑同一个键应当被查出来");
            Assert.AreEqual(KeyCode.Q, conflict);
        }

        [Test]
        public void InputMapping_Validate_DetectsActionCollidingWithMoveKey()
        {
            InputMapping mapping = InputMapping.CreateDefault();
            mapping.Bind(m_skill1, mapping.MoveForward);   // 抢 W

            KeyCode conflict;
            Assert.IsFalse(mapping.Validate(out conflict));
            Assert.AreEqual(KeyCode.W, conflict);
        }

        [Test]
        public void InputMapping_DefaultIsValid()
        {
            InputMapping mapping = InputMapping.CreateDefault();

            KeyCode conflict;
            Assert.IsTrue(mapping.Validate(out conflict), "默认映射不应有冲突");
            Assert.AreEqual(KeyCode.None, conflict);
            Assert.AreEqual(0, mapping.ActionCount, "框架不该替游戏决定动作键位（那是游戏层的事）");
        }

        [Test]
        public void InputMapping_UnboundActionReportsFalse()
        {
            InputMapping mapping = InputMapping.CreateDefault();

            KeyCode key;
            Assert.IsFalse(mapping.TryGetKey(m_skill1, out key));
            Assert.AreEqual(KeyCode.None, key);
        }
    }
}
