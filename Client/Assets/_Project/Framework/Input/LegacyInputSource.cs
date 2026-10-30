// 这个文件里有：
//   LegacyInputSource（类）：IInputSource 的旧输入系统实现，把 UnityEngine.Input 的按键状态翻译成动作状态与 -1000..1000 的量化移动轴

using UnityEngine;

namespace NBC.Framework.Input
{
    // 主要功能：用旧输入系统（UnityEngine.Input）采集键盘状态，把「哪个物理键对应哪个动作」全部关在内部，对外只暴露与后端无关的动作状态和 -1000..1000 的量化轴；换新输入系统时另写一个 IInputSource 实现即可，上层不改。
    // 主要数据成员：m_mapping（键位映射表，动作与四个方向 → 物理键，默认只有 WASD 有值）。
    // 主要外部方法：Poll / MoveX / MoveY / WasActionPressed / WasActionReleased / IsActionHeld（IInputSource 的实现），以及 Mapping（暴露映射表，改它即可改键位）。
    // 谁在用它：InputManager 在播放模式下默认创建它；Boot 的 M2DemoBehaviour 传入自定义映射表后赋给 InputManager.Source。测试不直接用它——UnityEngine.Input 要真实设备，EditMode 里按键状态造不出来，改由假 IInputSource 顶替。
    // 注意：只查映射表里真的绑定了的动作，不扫写死的键表；也不往事件中心发事件——每帧一条的命令是数据，由 InputManager 组装后交给网络层，低频的游戏事件才走事件中心。
    /// <summary>旧输入系统的采集源：用 UnityEngine.Input 读键，产出与后端无关的量化轴和动作边沿状态。</summary>
    public sealed class LegacyInputSource : IInputSource
    {
        // 键位映射表：动作与四个方向 → 物理键；构造后不再换表，取值非 null（没传就用默认 WASD）。
        private readonly InputMapping m_mapping;

        /// <summary>构造采集源；mapping 为 null 时用只含 WASD 的默认映射表。</summary>
        public LegacyInputSource(InputMapping mapping = null)
        {
            // 功能：记下键位映射表，没传时退到默认 WASD 映射。
            // 入参：mapping 键位映射表；null 表示用默认表（动作键留给游戏层绑定）
            // 返回：无
            // 步骤：
            //   1) 先判 null 再落到默认值，保证 m_mapping 永远非空，后面每次取键都不用再判空
            // 上游：InputManager.OnInit 默认创建；Boot 的 M2DemoBehaviour 传自定义映射
            // 下游：InputMapping.CreateDefault
            m_mapping = mapping ?? InputMapping.CreateDefault();
        }

        /// <summary>当前生效的键位映射表；引用只读，改它的属性即可改键位。</summary>
        public InputMapping Mapping
        {
            // 功能：把内部映射表交出去，让外部能改键位或校验冲突。
            // 入参：无
            // 返回：映射表引用，永不为 null；是同一个对象，改它立刻影响后续取键
            // 步骤：
            //   1) 直接返回字段，不复制、不包装——复制会让改键位不生效
            // 上游：当前仓库里没有调用点，留给改键位与调试面板
            // 下游：无
            get { return m_mapping; }
        }

        /// <summary>每帧的采集钩子；旧输入系统的轮询状态由引擎每帧刷新，所以这里是空实现。</summary>
        public void Poll()
        {
            // 功能：每帧采集一次的空钩子——旧输入系统不需要本类累积任何状态。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 不做事：UnityEngine.Input 的状态每帧由引擎刷新，这里再读一遍也不会变
            //   2) 保留空方法是为了让需要自己累积状态的后端（比如新输入系统自己算轴死区）有地方落
            // 上游：InputManager.Tick 每帧先调一次，之后才读轴与动作
            // 下游：无
            // 注意：别在这里补额外逻辑，也别在 InputManager.Tick 之外再驱动一次——每帧只由 InputManager 推进这一遍。
            // 注意：故意留空不是漏实现，去掉它会让接口对「需要累积状态的后端」不成立。
        }

        /// <summary>左右轴，-1000..1000 的量化整数：按左为负、按右为正，两键同按为 0。</summary>
        public int MoveX
        {
            // 功能：把左移 / 右移两个键的按住状态合成一根横向轴。
            // 入参：无
            // 返回：横向轴，-InputCommand.MoveScale..+InputCommand.MoveScale，即 -1000..1000（1000 表示满舵）；两键同按互相抵消为 0
            // 步骤：
            //   1) 先置 0，没按键时天然就是「无输入」
            //   2) 按左减 MoveScale、按右加 MoveScale；不额外判互斥，两键同按时加减自然抵消
            // 上游：InputManager.Tick 先调 Poll 再读本属性，随后 ClampMove 钳位并写进 InputCommand.MoveX
            // 下游：m_mapping.MoveLeft / MoveRight、UnityEngine.Input.GetKey
            // 注意：单位是千分比（1000 = 1.0），不是 -1..1；键没绑（KeyCode.None）时 GetKey 恒为 false，轴保持 0。
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

        /// <summary>前后轴，-1000..1000 的量化整数：按后退为负、按前进为正，两键同按为 0。</summary>
        public int MoveY
        {
            // 功能：把前进 / 后退两个键的按住状态合成一根纵向轴。
            // 入参：无
            // 返回：纵向轴，-InputCommand.MoveScale..+InputCommand.MoveScale，即 -1000..1000（1000 表示满舵）；两键同按互相抵消为 0
            // 步骤：
            //   1) 先置 0，没按键时天然就是「无输入」
            //   2) 按后退减 MoveScale、按前进加 MoveScale；不额外判互斥，两键同按时加减自然抵消
            // 上游：InputManager.Tick 先调 Poll 再读本属性，随后 ClampMove 钳位并写进 InputCommand.MoveY
            // 下游：m_mapping.MoveBackward / MoveForward、UnityEngine.Input.GetKey
            // 注意：单位是千分比（1000 = 1.0），不是 -1..1；+1000 是向前，别把它当成场景坐标，换算由 PlayerMove 做。
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

        /// <summary>某个动作本帧是否刚按下；没绑定的动作返回 false，且不查键。</summary>
        public bool WasActionPressed(InputActionId action)
        {
            // 功能：查某个动作这一帧有没有「刚按下」的边沿。
            // 入参：action 动作标识；它的 Index（0..31）决定该动作在 InputCommand.ActionBits 里的位号
            // 返回：本帧刚按下返回 true；没绑定、或本帧没按下返回 false（不抛异常）
            // 步骤：
            //   1) 先查映射表，没绑定就直接 false——不把 KeyCode.None 递给 GetKeyDown
            //   2) 绑定了才问 GetKeyDown，边沿判定交给引擎
            // 上游：InputManager.Tick 遍历登记过的动作时调用，结果按位或进 ActionBits
            // 下游：InputMapping.TryGetKey、UnityEngine.Input.GetKeyDown
            // 注意：GetKeyDown 只在这一帧成立，同一帧重复问仍是 true；它不是「现在按住」，按住要用 IsActionHeld，否则按住不放会每帧各放一发。
            KeyCode key;
            return m_mapping.TryGetKey(action, out key) && UnityEngine.Input.GetKeyDown(key);
        }

        /// <summary>某个动作本帧是否刚松开；没绑定的动作返回 false，且不查键。</summary>
        public bool WasActionReleased(InputActionId action)
        {
            // 功能：查某个动作这一帧有没有「刚松开」的边沿。
            // 入参：action 动作标识；它的 Index（0..31）决定该动作在 InputCommand.ActionReleaseBits 里的位号
            // 返回：本帧刚松开返回 true；没绑定、或本帧没松开返回 false
            // 步骤：
            //   1) 先查映射表，没绑定直接 false，不拿 KeyCode.None 去问引擎
            //   2) 绑定了才问 GetKeyUp，边沿判定交给引擎
            // 上游：InputManager.Tick 遍历登记过的动作时调用，结果按位或进 ActionReleaseBits
            // 下游：InputMapping.TryGetKey、UnityEngine.Input.GetKeyUp
            // 注意：按下与松开两条边沿都要报，逻辑层才能重建「持续按住」；只报按下会「松手了还在开火」。
            KeyCode key;
            return m_mapping.TryGetKey(action, out key) && UnityEngine.Input.GetKeyUp(key);
        }

        /// <summary>某个动作当前是否被按住（持续状态，不是边沿）；没绑定的动作返回 false。</summary>
        public bool IsActionHeld(InputActionId action)
        {
            // 功能：查某个动作此刻是否被按住，返回持续状态而不是边沿。
            // 入参：action 动作标识；它的 Index（0..31）决定该动作在 InputCommand.ActionBits 里的位号
            // 返回：按住期间一直为 true；松开或没绑定为 false
            // 步骤：
            //   1) 先查映射表，没绑定直接 false
            //   2) 绑定了才查 GetKey（持续按住，与 GetKeyDown 的边沿不同）
            // 上游：当前仓库里只有接口契约使用，InputManager 组装命令只读按下 / 松开两条边沿
            // 下游：InputMapping.TryGetKey、UnityEngine.Input.GetKey
            // 注意：这是「现在按住」而不是「本帧新按下」，不能直接塞进 ActionBits，否则按住技能键会每帧触发一次。
            KeyCode key;
            return m_mapping.TryGetKey(action, out key) && UnityEngine.Input.GetKey(key);
        }
    }
}
