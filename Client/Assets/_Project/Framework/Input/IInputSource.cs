// 这个文件里有：
//   IInputSource（接口）：输入采集源，把某个后端的原始状态翻译成与后端无关的动作状态与量化轴

namespace NBC.Framework.Input
{
    // 主要功能：定义输入采集契约 —— 每帧采集一次，按动作报按下 / 松开两条边沿与按住状态，移动量给出量化整数轴。
    // 主要数据成员：无字段；轴是 MoveX（左右）与 MoveY（前后），都是 -1000..1000 的量化整数（-1..+1，千分之一精度）。
    // 主要外部方法：Poll（每帧采集）、MoveX / MoveY（轴）、WasActionPressed / WasActionReleased（边沿）、IsActionHeld（按住）。
    // 谁在用它：InputManager 持有并每帧调用，NBC.Game 经 InputManager 间接取输入；实现类是 LegacyInputSource，测试里注入假实现。
    // 注意：接口里刻意不出现 KeyCode —— 那是旧输入系统的类型，「哪个物理键对应哪个动作」全部留在实现类（见 InputMapping）；
    //   否则换新输入系统时还得改接口，这个抽象就是假的。
    /// <summary>输入采集源：把某个后端的原始状态翻译成与后端无关的动作状态与量化轴。</summary>
    public interface IInputSource
    {
        // 功能：让实现类在每帧读轴与边沿之前，把需要自己累积的状态准备好。
        // 入参：无
        // 返回：无
        // 步骤：1) 需要自己累积状态的后端在这里刷新缓存（轮询式后端可留空，引擎每帧自己刷新）
        //   2) 顺序必须在上游读轴与边沿之前，否则读到的还是上一帧的状态
        // 上游：InputManager.Tick 每帧调一次，且在读取轴与边沿之前
        // 下游：实现类内部（LegacyInputSource 为空实现）
        // 注意：刻意不传 deltaTime —— 对输入做平滑 / 积分会引入与帧率相关的状态，而联机同步要的是确定的输入；需要平滑请放到表现层。
        /// <summary>采集这一帧的状态；轮询式后端必须在读轴与边沿之前调用。</summary>
        void Poll();

        /// <summary>左右轴，-1000..1000 的量化整数（-1000 = 最左，+1000 = 最右，0 = 无输入）。</summary>
        int MoveX { get; }

        /// <summary>前后轴，-1000..1000 的量化整数（-1000 = 最后，+1000 = 最前，0 = 无输入）。</summary>
        int MoveY { get; }

        // 功能：查某个动作在这一帧是不是刚被按下（上升沿）。
        // 入参：action 动作标识，其 Index 是 0..31 的稠密索引，对应命令位掩码里的一位
        // 返回：true 表示本帧刚按下；已按住但本帧没再按、或这个动作根本没绑定，都返回 false
        // 步骤：1) 实现类取本帧的上升沿
        //   2) 只返回这一帧的边沿，不和上一帧合并 —— 合并就把边沿变成了持续状态
        // 上游：InputManager.Tick 按登记过的动作逐个查询，结果打进 InputCommand.ActionBits
        // 下游：实现类（LegacyInputSource 转 UnityEngine.Input.GetKeyDown）
        // 注意：这是边沿判定，只在按下的那一帧为 true；当成「现在按住」用会按住不放每帧触发一次。
        /// <summary>某个动作本帧是否刚按下（上升沿）。</summary>
        /// <returns>true 仅在本帧刚按下那一帧成立；按住不放的后续帧为 false。</returns>
        bool WasActionPressed(InputActionId action);

        // 功能：查某个动作在这一帧是不是刚被松开（下降沿）。
        // 入参：action 动作标识，其 Index 是 0..31 的稠密索引，对应命令位掩码里的一位
        // 返回：true 表示本帧刚松开；没绑定的动作返回 false
        // 步骤：1) 实现类取本帧的下降沿
        //   2) 与按下边沿各自独立上报 —— 合成一条就分不清是刚按下还是刚松手
        // 上游：InputManager.Tick 按登记过的动作逐个查询，结果打进 InputCommand.ActionReleaseBits
        // 下游：实现类（LegacyInputSource 转 UnityEngine.Input.GetKeyUp）
        // 注意：按下与松开两条边沿必须配对上报 —— 只报按下的话逻辑层不知道玩家何时松手，现象是「松手了还在开火」；
        //   有了两条边沿才能精确重建持续按住：本帧按下的位置 1，再把本帧松开的位清 0。
        /// <summary>某个动作本帧是否刚松开（下降沿）。</summary>
        /// <returns>true 仅在本帧刚松开那一帧成立。</returns>
        bool WasActionReleased(InputActionId action);

        // 功能：查某个动作此刻是否被按住（持续状态，不是边沿）。
        // 入参：action 动作标识，其 Index 是 0..31 的稠密索引
        // 返回：true 表示此刻按住；没绑定的动作返回 false
        // 步骤：1) 实现类读后端的当前按下状态
        //   2) 不做边沿判定，也不缓存 —— 按住期间每帧都返回 true
        // 上游：框架内目前没有调用点，契约留给表现层查询持续状态（如长按蓄力）；命令只带边沿，这条路不进 InputCommand
        // 下游：实现类（LegacyInputSource 转 UnityEngine.Input.GetKey）
        // 注意：与 WasActionPressed 不能混用 —— 拿按住状态去触发一次性动作，按住多久就触发多少帧。
        /// <summary>某个动作当前是否被按住（持续状态）。</summary>
        /// <returns>true 表示此刻按住；与边沿无关，按住期间持续为 true。</returns>
        bool IsActionHeld(InputActionId action);
    }
}
