// 这个文件里有：
//   PlayerAnimatorNames（静态类）：动画参数名常量，状态机与发布器共用
//   PlayerAnimatorParams（只读结构）：一帧要写给 Animator 的全部参数值

namespace NBC.Game.Player
{
    // 主要功能：集中放动画参数名，避免「一个地方写 "Speed"、另一个地方写 "speed"」这种拼写事故。
    // 主要数据成员：十个参数名常量（前两个是控制器原有参数，其余是 R035a 新增）。
    // 主要外部方法：无。
    // 谁在用它：PlayerAnimatorPublisher（把它们换成哈希）、PlayerAnimation（沿用原有 Speed/Direction）。
    public static class PlayerAnimatorNames
    {
        // 移动速度（-1~1 的归一化值，控制器按阈值判走/跑/冲）
        public const string Speed = "Speed";

        // 横向速度（-1 左 ~ 1 右；目前只用于表现）
        public const string Direction = "Direction";

        // 是否蹲下（true 时走蹲三态）
        public const string Crouch = "Crouch";

        // 是否踩在地面上（跳跃时 false）
        public const string Grounded = "Grounded";

        // 竖直速度（米/秒，正数上升、负数下落）
        public const string VerticalSpeed = "VerticalSpeed";

        // 是否有动作在播（true 时进动作状态）
        public const string ActionBusy = "ActionBusy";

        // 动作编号（0 无 / 1~3 攻击 / 4~6 技能 / 7 冲刺 / 8 格挡 / 9 姿势 / 10 受击 / 11 硬直）
        public const string ActionId = "ActionId";

        // 受击方向（-1 左 / 1 右）
        public const string StackSide = "StackSide";

        // 姿势编号（0~6，对应 CBG_ 系列）
        public const string PoseIndex = "PoseIndex";

        // 是否死亡（true 后永久停在死亡状态）
        public const string Dead = "Dead";
    }

    // 主要功能：一帧动画参数的只读快照 —— 状态机算完，发布器照着写，谁也不回读 Animator。
    // 主要数据成员：Speed/Direction/VerticalSpeed 是浮点，Crouch/Grounded/ActionBusy/Dead 是布尔，ActionId/StackSide/PoseIndex 是编号。
    // 主要外部方法：构造函数（十个参数，顺序与字段一致）。
    // 谁在用它：PlayerStateMachine 产出、PlayerAnimatorPublisher 消费；测试里直接断言字段值。
    public readonly struct PlayerAnimatorParams
    {
        // 移动速度，-1~1（控制器按 0.1/3/6 阈值分流；不是米每秒）
        public readonly float Speed;

        // 横向速度，-1~1（左负右正）
        public readonly float Direction;

        // 是否蹲下
        public readonly bool Crouch;

        // 是否在地面上
        public readonly bool Grounded;

        // 竖直速度，米/秒（上升为正）
        public readonly float VerticalSpeed;

        // 是否有动作在播
        public readonly bool ActionBusy;

        // 动作编号，0~11（含义见 PlayerActionId）
        public readonly float ActionId;

        // 受击方向，-1 或 1
        public readonly float StackSide;

        // 姿势编号，0~6
        public readonly float PoseIndex;

        // 是否死亡
        public readonly bool Dead;

        // 功能：把状态机算好的一帧参数打包。
        // 入参：十个值，含义与同名字段一致。
        // 返回：结构本身（值语义，调用方拿到的是副本）。
        // 步骤：逐个赋值，不做范围校验 —— 校验放在状态机里，避免每帧多做一次判断。
        // 上游/下游：PlayerStateContext.BuildParams 调用；PlayerAnimatorPublisher.Apply 读取。
        public PlayerAnimatorParams(
            float speed,
            float direction,
            bool crouch,
            bool grounded,
            float verticalSpeed,
            bool actionBusy,
            float actionId,
            float stackSide,
            float poseIndex,
            bool dead)
        {
            Speed = speed;
            Direction = direction;
            Crouch = crouch;
            Grounded = grounded;
            VerticalSpeed = verticalSpeed;
            ActionBusy = actionBusy;
            ActionId = actionId;
            StackSide = stackSide;
            PoseIndex = poseIndex;
            Dead = dead;
        }
    }
}
