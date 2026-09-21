// ============================================================================
//  NBC.Framework.Input · 旧输入系统（`UnityEngine.Input`）的采集实现
//  对应需求：FW-M07
//
//  ---------------------------------------------------------------------------
//  这个类就是"将来能换新输入系统"的证据
//  ---------------------------------------------------------------------------
//  它把"哪个 KeyCode 对应哪个动作"这件事**全部关在自己内部**：
//  `IInputSource` 的接口里没有任何 `KeyCode`，`InputManager` 也不知道按键的存在。
//  换成 `com.unity.inputsystem` 时，新写一个 `InputSystemSource : IInputSource` 即可，
//  上层一行不改。
//
//  ---------------------------------------------------------------------------
//  修掉的 P-14（每帧盲发事件）
//  ---------------------------------------------------------------------------
//  原版 `InputMgr` 每帧对 4 个键各调 **2 次** `Input.GetKey*`，然后**无条件**往事件中心发 2 条事件，
//  哪怕没有任何监听者。这里的做法不同：
//
//    · **只查"映射表里真的绑了"的动作**，不再扫一组写死的键
//    · **不发事件**：命令是**每帧产出一次的数据**，交给网络层；把它塞进事件中心
//      （字典查找 + 委托链）是拿错了工具。低频的"游戏事件"才走事件中心（A3）
//
//  ⚠️ **物理按键状态本身没法在 EditMode 里伪造**（`UnityEngine.Input` 需要真实设备），
//     所以这个类**不做单元测试**；它上面的 `InputManager` 用假 `IInputSource` 测透。
//     这是诚实的边界，不是漏测。
// ============================================================================

using UnityEngine;

namespace NBC.Framework.Input
{
    /// <summary>
    /// 用旧输入系统（`UnityEngine.Input`）实现的采集源。
    /// </summary>
    public sealed class LegacyInputSource : IInputSource
    {
        /// <summary>键位映射表。</summary>
        private readonly InputMapping m_mapping;

        /// <summary>
        /// 构造。
        /// </summary>
        /// <param name="mapping">键位映射表；传 null 则用默认值。</param>
        public LegacyInputSource(InputMapping mapping = null)
        {
            m_mapping = mapping ?? InputMapping.CreateDefault();
        }

        /// <summary>当前使用的映射表（只读引用，改它即可改键位）。</summary>
        public InputMapping Mapping
        {
            get { return m_mapping; }
        }

        /// <summary>旧输入系统是轮询式的，但每帧的状态由 `Input` 自己维护，这里无需预处理。</summary>
        public void Poll()
        {
            // 故意留空：`UnityEngine.Input` 的状态每帧由引擎刷新。
            // 保留这个方法是为了让接口对"需要自己累积状态的后端"也成立
            // （例如新输入系统里如果要自己算轴的死区）。
        }

        /// <summary>左右轴。</summary>
        public int MoveX
        {
            get
            {
                int x = 0;
                if (UnityEngine.Input.GetKey(m_mapping.MoveLeft))
                {
                    x -= InputCommand.MoveScale;
                }

                if (UnityEngine.Input.GetKey(m_mapping.MoveRight))
                {
                    x += InputCommand.MoveScale;
                }

                return x;
            }
        }

        /// <summary>前后轴。</summary>
        public int MoveY
        {
            get
            {
                int y = 0;
                if (UnityEngine.Input.GetKey(m_mapping.MoveBackward))
                {
                    y -= InputCommand.MoveScale;
                }

                if (UnityEngine.Input.GetKey(m_mapping.MoveForward))
                {
                    y += InputCommand.MoveScale;
                }

                return y;
            }
        }

        /// <summary>本帧是否刚按下某个动作。**没绑定的动作返回 false，且不查键。**</summary>
        /// <param name="action">动作标识。</param>
        public bool WasActionPressed(InputActionId action)
        {
            KeyCode key;
            return m_mapping.TryGetKey(action, out key) && UnityEngine.Input.GetKeyDown(key);
        }

        /// <summary>本帧是否刚松开某个动作。</summary>
        /// <param name="action">动作标识。</param>
        public bool WasActionReleased(InputActionId action)
        {
            KeyCode key;
            return m_mapping.TryGetKey(action, out key) && UnityEngine.Input.GetKeyUp(key);
        }

        /// <summary>某个动作当前是否被按住。</summary>
        /// <param name="action">动作标识。</param>
        public bool IsActionHeld(InputActionId action)
        {
            KeyCode key;
            return m_mapping.TryGetKey(action, out key) && UnityEngine.Input.GetKey(key);
        }
    }
}
