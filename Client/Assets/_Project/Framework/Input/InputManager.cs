// ============================================================================
//  NBC.Framework.Input · 输入管理（采集 → 命令）
//  替代：唐老师框架 Input/InputMgr.cs（原 56 行）
//  缺陷编号：审计新增 P-14（每帧盲发事件 + 每帧两次 GetKey）
//  需求条目：FW-M07（键位映射可配置、**采集与命令生成分离**、暂停/禁用）
//  完整记录：Docs/06-框架改造记录.md §四 P-14、§十五
//
//  ---------------------------------------------------------------------------
//  原版错在哪（P-14）
//  ---------------------------------------------------------------------------
//      private void CheckKeyCode(KeyCode key)          // 每帧被调 4 次（WASD）
//      {
//          if (Input.GetKeyDown(key)) EventTrigger("某键按下", key);
//          if (Input.GetKeyUp(key))   EventTrigger("某键抬起", key);
//      }
//
//  三处浪费/隐患：
//    ① 每帧 **8 次** `Input.GetKey*`（4 键 × 2），扫的还是一组写死的键
//    ② **无条件**往事件中心发事件 —— 哪怕一个监听者都没有（字典查找 + 委托链白跑）
//    ③ 事件名硬编码中文（FW-12），拼错还静默不触发（FW-06）
//
//  本类的做法：
//    · **只查"游戏明确登记过的动作"**（`TrackAction`），不再扫写死的键表
//    · **不发事件**：每帧一条的**数据**不该走事件中心 —— 它是低频"游戏事件"的工具。
//      命令通过 `CommandGenerated`（强类型 C# 事件）或轮询 `Current` 交给网络层
//
//  ---------------------------------------------------------------------------
//  为什么是 `Tick(tick)` 而不是协程（和 A5 同一个理由）
//  ---------------------------------------------------------------------------
//  运行时由 `MonoManager`（A4）每帧驱动；**测试里直接调 `Tick`** ——
//  于是"按一下技能键 → 命令里出现对应位"这种断言完全确定，不需要真按键盘。
//  更关键的是：**真按键在自动化测试里是造不出来的**，而注入一个假 `IInputSource` 可以。
//
//  ---------------------------------------------------------------------------
//  「禁用」的确切语义（写死，避免猜）
//  ---------------------------------------------------------------------------
//    `IsEnabled == false` 时：
//      · `Tick` **不查任何按键**（省掉全部开销）
//      · 产出**空命令**并写入 `Current` —— 网络层按 tick 轮询时拿到的就是"这一帧没操作"，
//        时序不会错位
//      · **不触发 `CommandGenerated`** —— 监听者收不到命令，符合"禁用后无输入命令"
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework.Input
{
    /// <summary>
    /// 输入管理：把采集到的原始状态，按 tick 变成可发送的 <see cref="InputCommand"/>。
    /// </summary>
    public sealed class InputManager : Singleton<InputManager>
    {
        /// <summary>采集源（可注入 —— 测试里换假实现）。</summary>
        private IInputSource m_source;

        /// <summary>游戏明确登记过的动作。**只查这些**，这就是 P-14 的修法。</summary>
        private readonly List<InputActionId> m_trackedActions = new List<InputActionId>();

        private int m_nextTick;
        private bool m_warnedNoTrackedActions;

        /// <summary>最新一帧的命令（轮询用）。</summary>
        public InputCommand Current { get; private set; }

        /// <summary>是否启用输入。false 时命令为空且不触发 <see cref="CommandGenerated"/>。</summary>
        public bool IsEnabled { get; private set; } = true;

        /// <summary>已登记的动作数量。</summary>
        public int TrackedActionCount
        {
            get { return m_trackedActions.Count; }
        }

        /// <summary>本帧产出了一条命令时触发（禁用时不触发）。</summary>
        public event Action<InputCommand> CommandGenerated;

        /// <summary>
        /// 采集源。默认在播放模式下是 <see cref="LegacyInputSource"/>（旧输入系统）；
        /// **测试里请注入假实现**。
        /// </summary>
        public IInputSource Source
        {
            get { return m_source; }
            set { m_source = value; }
        }

        // ====================================================================
        //  登记动作
        // ====================================================================

        /// <summary>
        /// 登记一个"要进命令"的动作。
        /// <para>
        /// 游戏层声明完 `InputActionId` 之后必须登记，否则那个动作永远不会出现在命令里。
        /// **这是刻意的显式行为**：原版扫的是一组写死的键，想加一个动作就得改框架。
        /// </para>
        /// </summary>
        /// <param name="action">动作标识。</param>
        public void TrackAction(InputActionId action)
        {
            if (!m_trackedActions.Contains(action))
            {
                m_trackedActions.Add(action);
            }
        }

        /// <summary>清空登记的动作（切玩法 / 测试隔离用）。</summary>
        public void ClearTrackedActions()
        {
            m_trackedActions.Clear();
        }

        // ====================================================================
        //  启用 / 禁用
        // ====================================================================

        /// <summary>禁用输入（过场动画、打开菜单、失焦时用）。</summary>
        public void Pause()
        {
            IsEnabled = false;
        }

        /// <summary>恢复输入。</summary>
        public void Resume()
        {
            IsEnabled = true;
        }

        // ====================================================================
        //  推进
        // ====================================================================

        /// <summary>
        /// 推进一帧：采集 → 生成命令。
        /// </summary>
        /// <param name="tick">逻辑帧号（由网络层 / 逻辑层给，命令里会带上它）。</param>
        /// <returns>这一帧的命令。</returns>
        public InputCommand Tick(int tick)
        {
            if (m_source == null)
            {
                throw new InvalidOperationException(
                    "[InputManager] 采集源还没设置。播放模式下会默认用 LegacyInputSource；" +
                    "测试里请先赋值 InputManager.Instance.Source = new FakeInputSource()。");
            }

            // —— 禁用：不查键、产出空命令、不触发事件 ——
            if (!IsEnabled)
            {
                Current = new InputCommand(tick, 0, 0, 0u, 0u);
                return Current;
            }

            if (m_trackedActions.Count == 0 && !m_warnedNoTrackedActions)
            {
                m_warnedNoTrackedActions = true;
                Debug.LogWarning(
                    "[InputManager] 还没有登记任何动作（TrackAction）。" +
                    "当前只会产出移动轴，不会产出任何动作位 —— 检查游戏层的初始化顺序。");
            }

            m_source.Poll();

            int moveX = ClampMove(m_source.MoveX);
            int moveY = ClampMove(m_source.MoveY);

            uint pressed = 0u;
            uint released = 0u;

            // **只遍历登记过的动作** —— 这是 P-14 的直接修法：
            // 原版每帧扫 4 个写死的键、调 8 次 GetKey；这里只问"游戏真正关心的动作"。
            for (int i = 0; i < m_trackedActions.Count; i++)
            {
                InputActionId action = m_trackedActions[i];
                uint bit = 1u << action.Index;

                if (m_source.WasActionPressed(action))
                {
                    pressed |= bit;
                }

                if (m_source.WasActionReleased(action))
                {
                    released |= bit;
                }
            }

            Current = new InputCommand(tick, moveX, moveY, pressed, released);

            Action<InputCommand> handlers = CommandGenerated;
            if (handlers != null)
            {
                handlers(Current);
            }

            return Current;
        }

        /// <summary>把轴钳到合法范围，防止后端给出越界值污染命令。</summary>
        private static int ClampMove(int value)
        {
            if (value > InputCommand.MoveScale)
            {
                return InputCommand.MoveScale;
            }

            if (value < -InputCommand.MoveScale)
            {
                return -InputCommand.MoveScale;
            }

            return value;
        }

        /// <summary>
        /// 播放模式下挂到 `MonoManager` 上，并默认用旧输入系统。
        /// **EditMode 下不注册**（`Update` 不执行，注册了也没用）—— 测试直接调 `Tick`。
        /// </summary>
        protected override void OnInit()
        {
            if (Application.isPlaying)
            {
                if (m_source == null)
                {
                    m_source = new LegacyInputSource();
                }

                MonoManager.Instance.AddUpdateListener(OnUpdate);
            }
        }

        private void OnUpdate()
        {
            Tick(m_nextTick++);
        }
    }
}
