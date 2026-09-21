// ============================================================================
//  NBC.Framework.Input · 输入采集层（与具体输入后端解耦）
//  对应需求：FW-M07（**输入采集与"输入命令"生成分离**）
//
//  ---------------------------------------------------------------------------
//  这个接口就是"将来能换新输入系统"的那道门
//  ---------------------------------------------------------------------------
//  2026-09-20 的决策：**先实现旧输入系统（`UnityEngine.Input`），但把采集抽成接口**。
//  于是换成 Unity 新输入系统（`com.unity.inputsystem`）时，只需要**新写一个实现类**，
//  `InputManager` 与逻辑层一行不改。
//
//  ⚠️ 接口里**刻意不出现 `KeyCode`** —— 那是旧输入系统的类型。
//     任何"哪个物理键对应哪个动作"的信息都留在实现类里（见 `InputMapping`）。
//     否则这个抽象就是假的：换了后端还得改接口。
//
//  ---------------------------------------------------------------------------
//  为什么同时要"按下"和"松开"
//  ---------------------------------------------------------------------------
//  只报"按下"的话，逻辑层不知道玩家什么时候松手 → "松手了还在开火"。
//  两条边沿都报，逻辑层才能精确重建"持续按住"：
//      held = held | pressed;  held = held & ~released;
// ============================================================================

namespace NBC.Framework.Input
{
    /// <summary>
    /// 输入采集源：把"某个后端"的原始状态翻译成与后端无关的**动作状态**。
    /// </summary>
    public interface IInputSource
    {
        /// <summary>
        /// 采集这一帧的状态。旧输入系统是轮询式的，所以需要每帧调一次
        /// （新输入系统的实现可能是空实现，因为它走回调）。
        /// <para>
        /// ⚠️ **刻意不传 `deltaTime`**：任何"对输入做平滑/积分"的后端都会引入
        /// 与帧率相关的状态，而**联机同步要的是确定的输入**。需要平滑请放在表现层。
        /// </para>
        /// </summary>
        void Poll();

        /// <summary>左右轴，取值 -<see cref="InputCommand.MoveScale"/>..+<see cref="InputCommand.MoveScale"/>。</summary>
        int MoveX { get; }

        /// <summary>前后轴，取值 -<see cref="InputCommand.MoveScale"/>..+<see cref="InputCommand.MoveScale"/>。</summary>
        int MoveY { get; }

        /// <summary>某个动作本帧是否**刚按下**。</summary>
        /// <param name="action">动作标识。</param>
        bool WasActionPressed(InputActionId action);

        /// <summary>某个动作本帧是否**刚松开**。</summary>
        /// <param name="action">动作标识。</param>
        bool WasActionReleased(InputActionId action);

        /// <summary>某个动作当前是否**被按住**（持续状态）。</summary>
        /// <param name="action">动作标识。</param>
        bool IsActionHeld(InputActionId action);
    }
}
