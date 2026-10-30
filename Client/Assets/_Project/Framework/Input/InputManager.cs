// 这个文件里有：
//   InputManager（类）：输入管理的单例入口，按 tick 把采集源的轴与动作边沿压成一条待发送的命令

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NBC.Framework.Input
{
    // 主要功能：每帧把采集源的状态压成一条 InputCommand —— 轴钳到 -1000..1000、动作边沿打成位掩码，禁用时只产出空命令。
    // 主要数据成员：m_source（采集源，可注入）、m_trackedActions（登记的动作表，Index 0..31）、m_nextTick（自增帧号，从 0 起）、Current（最新命令）。
    // 主要外部方法：Tick / TrackAction / ClearTrackedActions / Pause / Resume，属性 Current / Source / IsEnabled / TrackedActionCount，事件 CommandGenerated。
    // 谁在用它：Boot 的 M2DemoBehaviour（注入 Source、登记动作、每帧 Tick）；Game 的 PlayerMove 与 RuntimeNetDriver 轮询 Current；EditMode 测试直接调 Tick。
    // 注意：禁用（IsEnabled 为 false）时 Tick 不查任何按键、产出一条空命令写进 Current、且不触发 CommandGenerated，网络层按 tick 轮询时序才不会错位。
    // 注意：驱动用 Tick 而不是协程 —— 测试里直接调 Tick 就能断言「按一下技能键 → 命令里出现对应位」，真按键在自动化测试里造不出来。
    /// <summary>输入管理的单例入口：按 tick 把采集源的状态压成一条可发送的输入命令。</summary>
    public sealed class InputManager : Singleton<InputManager>
    {
        // 采集源，可注入：播放模式默认 LegacyInputSource，EditMode / 测试里要自己赋值，为 null 时 Tick 抛异常
        private IInputSource m_source;

        // 游戏明确登记过的动作表，Tick 只查这些；元素 Index 必须是 0..31 的稠密索引，决定命令位掩码里的位号
        private readonly List<InputActionId> m_trackedActions = new List<InputActionId>();

        // 自增逻辑帧号，从 0 起、每帧 +1；只给播放模式下的 OnUpdate 用，不等于网络层的 tick
        private int m_nextTick;
        // 「还没登记任何动作」的警告只打一次的标志，避免每帧刷屏
        private bool m_warnedNoTrackedActions;

        // 轴 MoveX / MoveY 是 -1000..1000 的量化值（1000 = 满轴），两个 uint 是按下 / 松开的动作位掩码，第 i 位对应 Index 为 i 的动作
        /// <summary>最近一次 Tick 产出的命令，供网络层轮询；禁用时是轴与动作位全 0 的空命令。</summary>
        public InputCommand Current { get; private set; }

        // true 时每帧查键；false 时 Tick 直接短路，连一次按键查询都不做
        /// <summary>输入总开关，默认启用；禁用时 Tick 只产出空命令且不触发事件。</summary>
        public bool IsEnabled { get; private set; } = true;

        // 取值范围 0..32：动作位掩码是 uint，最多 32 个动作
        /// <summary>已登记的动作数量。</summary>
        public int TrackedActionCount
        {
            get { return m_trackedActions.Count; }
        }

        // 每帧一条的高频数据用强类型 C# 事件或轮询 Current 交给网络层，不走低频的「游戏事件」事件中心
        /// <summary>本帧产出一条命令时触发，参数是本帧命令；禁用时不触发。</summary>
        public event Action<InputCommand> CommandGenerated;

        // get 返回 null 表示还没初始化；默认在播放模式下是 LegacyInputSource（旧输入系统），测试里请换假实现
        /// <summary>采集源，可注入假实现以便在 EditMode 里造输入。</summary>
        public IInputSource Source
        {
            get { return m_source; }
            set { m_source = value; }
        }

        // 登记动作：游戏层先显式登记，Tick 才查得到

        /// <summary>登记一个动作，让 Tick 每帧查它的按下 / 松开边沿；重复登记无副作用。</summary>
        public void TrackAction(InputActionId action)
        {
            // 功能：把动作加进 Tick 要查的集合；不登记的动作永远不会出现在命令里。
            // 入参：action 动作标识，其 Index 决定位掩码里的位号（0..31）
            // 返回：无
            // 步骤：
            //   1) 先查重，避免同一动作每帧被查两遍、位掩码也重复算
            //   2) 追加到表尾，Tick 按表的顺序遍历
            // 上游：Boot 的 M2DemoBehaviour、EditMode 测试
            // 下游：Tick
            if (!m_trackedActions.Contains(action))
            {
                m_trackedActions.Add(action);
            }
        }

        /// <summary>清空登记的动作，切玩法或做测试隔离时用。</summary>
        public void ClearTrackedActions()
        {
            // 功能：把动作表清空，之后 Tick 产出的命令不再带任何动作位。
            // 注意：m_warnedNoTrackedActions 不跟着复位，清空后再忘登记也不会再警告。
            m_trackedActions.Clear();
        }

        // 启用 / 禁用：两条都只改标志

        /// <summary>禁用输入，过场动画、打开菜单、失焦时用。</summary>
        public void Pause()
        {
            // 功能：把总开关置 false，之后 Tick 不查键、产空命令、不触发事件。
            IsEnabled = false;
        }

        /// <summary>恢复输入，下一帧 Tick 起重新查键。</summary>
        public void Resume()
        {
            // 功能：把总开关置回 true。
            // 注意：恢复不补帧，停顿期间的空命令要等下一次 Tick 才会被真实命令覆盖。
            IsEnabled = true;
        }

        // 推进：采集 → 命令

        /// <summary>推进一帧，把采集结果变成一条带帧号的输入命令。</summary>
        /// <returns>本帧命令；禁用时是 tick 照传、轴与动作位全 0 的空命令。</returns>
        public InputCommand Tick(int tick)
        {
            // 功能：让采集源 Poll 一次，把轴钳进 -1000..1000，把登记动作的按下 / 松开边沿压成两个位掩码，产出本帧命令。
            // 入参：tick 逻辑帧号（网络层或逻辑层给的号，原样写进命令的 Tick 字段）
            // 返回：本帧命令；禁用时是空命令，其 tick 与入参一致
            // 步骤：
            //   1) 采集源为 null 就抛异常：与其静默产出空命令，不如让初始化顺序的错误当场暴露
            //   2) 禁用时先写空命令再返回，网络层按 tick 轮询拿到的是「这一帧没操作」，时序不错位
            //   3) 「没有登记动作」只警告一次，否则每帧刷屏
            //   4) Poll 一次取本帧快照，避免多次读取之间状态变化
            //   5) 轴逐个钳到 -1000..1000，越界值不许进命令
            //   6) 只遍历登记过的动作，按 1u << Index 拼位掩码
            //   7) 先写好 Current 再触发事件，监听者在回调里读到的就是本帧命令
            // 上游：Boot 的 M2DemoBehaviour 每帧调；EditMode 测试直接调；PlayerMove / RuntimeNetDriver 读 Current
            // 下游：m_source 的 Poll / MoveX / MoveY / WasActionPressed / WasActionReleased、ClampMove、CommandGenerated 的监听者
            // 注意：两个位掩码只表示本帧的按下 / 松开边沿，要表示「持续按住」得由使用方自己把按下并进来、再清掉松开。
            if (m_source == null)
            {
                throw new InvalidOperationException(
                    "[InputManager] 采集源还没设置。播放模式下会默认用 LegacyInputSource；" +
                    "测试里请先赋值 InputManager.Instance.Source = new FakeInputSource()。");
            }

            // 禁用：不查键、产出空命令、不触发事件 —— 三件事绑死
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

            // 只遍历登记过的动作：原版每帧扫 4 个写死的键、调 8 次 GetKey，这里只问游戏真正关心的动作
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

        // 功能：把采集源给的轴钳在 -1000..1000，防止越界值污染命令。
        // 入参：value 原始轴值，单位是量化格（-1000..1000 为合法，1000 = 满轴 1.0）
        // 返回：钳过的轴值，范围 -1000..1000
        // 步骤：
        //   1) 超过 +1000 直接截到 +1000
        //   2) 低于 -1000 直接截到 -1000
        //   3) 其余原样返回
        // 上游：Tick
        // 下游：InputCommand 构造函数
        // 注意：钳位上限取 InputCommand.MoveScale，下游按它除回去才还原成 -1..1。
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

        // 功能：单例第一次创建时初始化：播放模式下补默认采集源并挂上每帧回调。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 只在播放模式初始化：EditMode 下 Update 不执行，注册了也没人驱动
        //   2) 采集源为空才 new LegacyInputSource，保留游戏或测试提前注入的实现
        //   3) 把 OnUpdate 挂进 MonoManager 的每帧回调
        // 上游：Singleton 第一次访问 Instance
        // 下游：MonoManager.AddUpdateListener、OnUpdate
        // 注意：EditMode 测试请直接调 Tick，别指望 OnUpdate 被驱动。
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

        // 功能：MonoManager 的每帧回调，用自增帧号调一次 Tick。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 取 m_nextTick 当前值传给 Tick，再自增，帧号从 0 起连续
        //   2) Tick 的返回值这里丢弃，命令已经写进 Current
        // 上游：MonoManager（仅播放模式）
        // 下游：Tick
        // 注意：这里的帧号只是自增计数，不等同网络层的 tick。
        private void OnUpdate()
        {
            Tick(m_nextTick++);
        }
    }
}
