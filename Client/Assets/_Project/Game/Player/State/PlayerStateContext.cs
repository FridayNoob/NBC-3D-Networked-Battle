// 这个文件里有：
//   PlayerInput（只读结构）：一帧输入快照
//   PlayerTimings（结构）：各动作持续帧数
//   PlayerSpeeds（结构）：五档移动速度
//   PlayerStateContext（类）：状态机与外部共享的可变状态

namespace NBC.Game.Player
{
    // 主要功能：把「这一帧玩家按了什么」定格下来，状态机只读它、不碰 Input。
    // 主要数据成员：MoveX/MoveY 是 -1~1 的方向，WalkHeld/SprintHeld 是按住，CrouchToggle 是这一帧刚按下。
    // 主要外部方法：构造函数。
    // 谁在用它：PlayerBrain 每帧构造一次；PlayerStateMachine.Tick 读取。
    public readonly struct PlayerInput
    {
        // 左右方向，-1 左 ~ 1 右
        public readonly float MoveX;

        // 前后方向，-1 后 ~ 1 前
        public readonly float MoveY;

        // 是否按住「慢走」（Alt）
        public readonly bool WalkHeld;

        // 是否按住「冲刺」（Shift）
        public readonly bool SprintHeld;

        // 是否这一帧刚按下「蹲下」切换（Ctrl）
        public readonly bool CrouchToggle;

        // 功能：打包一帧输入。
        // 入参：五个值，含义与同名字段一致。
        // 返回：结构本身。
        // 步骤：直接赋值；方向由调用方保证在 -1~1。
        // 上游/下游：PlayerBrain.ReadInput 调用；PlayerStateMachine.ChooseLocomotion 读取。
        public PlayerInput(float moveX, float moveY, bool walkHeld, bool sprintHeld, bool crouchToggle)
        {
            MoveX = moveX;
            MoveY = moveY;
            WalkHeld = walkHeld;
            SprintHeld = sprintHeld;
            CrouchToggle = crouchToggle;
        }

        // 功能：判断这一帧有没有方向输入。
        // 入参：无。
        // 返回：true 表示至少一个轴不为 0（阈值 0.0001，避免摇杆漂移）。
        // 步骤：两轴取绝对值比阈值。
        // 上游/下游：PlayerStateMachine.ChooseLocomotion 调用。
        public bool HasDirection()
        {
            return System.Math.Abs(MoveX) > 0.0001f || System.Math.Abs(MoveY) > 0.0001f;
        }
    }

    // 主要功能：集中放「某个动作要播多少帧」，方便 Inspector 调手感。
    // 主要数据成员：攻击/受击/技能/冲刺/硬直/姿势 六个帧数（60fps 下 36 帧=0.6 秒）。
    // 主要外部方法：无。
    // 谁在用它：PlayerStateMachine 在 BeginAction 时读对应的帧数。
    public readonly struct PlayerTimings
    {
        // 攻击一段的帧数（60fps 下 36 帧 = 0.6 秒）
        public readonly int AttackFrames;

        // 受击的帧数（18 帧 = 0.3 秒）
        public readonly int HitFrames;

        // 技能的帧数（48 帧 = 0.8 秒）
        public readonly int SkillFrames;

        // 冲刺的帧数（18 帧 = 0.3 秒）
        public readonly int DashFrames;

        // 硬直的帧数（45 帧 = 0.75 秒）
        public readonly int StunFrames;

        // 姿势的帧数（90 帧 = 1.5 秒）
        public readonly int PoseFrames;

        // 功能：打包六个动作时长。
        // 入参：六个帧数，单位是 60fps 的帧；小于等于 0 表示「不自动结束」，由调用方决定。
        // 返回：结构本身。
        // 步骤：直接赋值。
        // 上游/下游：PlayerBrain.Awake 用 Inspector 值构造；PlayerStateMachine.Notify* 读取。
        public PlayerTimings(int attackFrames, int hitFrames, int skillFrames, int dashFrames, int stunFrames, int poseFrames)
        {
            AttackFrames = attackFrames;
            HitFrames = hitFrames;
            SkillFrames = skillFrames;
            DashFrames = dashFrames;
            StunFrames = stunFrames;
            PoseFrames = poseFrames;
        }
    }

    // 主要功能：集中放五档移动速度，避免把 2/5/8 这类数字散落在判断里。
    // 主要数据成员：Walk/Run/Sprint/CrouchWalk/CrouchJog，单位都是米/秒。
    // 主要外部方法：无。
    // 谁在用它：PlayerStateMachine 按当前状态取速度，写回 IPlayerMotion。
    public readonly struct PlayerSpeeds
    {
        // 慢走速度，默认 2 米/秒
        public readonly float Walk;

        // 默认跑速，默认 5 米/秒
        public readonly float Run;

        // 冲刺速度，默认 8 米/秒
        public readonly float Sprint;

        // 蹲走速度，默认 1.5 米/秒
        public readonly float CrouchWalk;

        // 蹲跑速度，默认 3 米/秒
        public readonly float CrouchJog;

        // 功能：打包五档速度。
        // 入参：五个速度，单位米/秒，必须为正。
        // 返回：结构本身。
        // 步骤：直接赋值。
        // 上游/下游：PlayerBrain.Awake 用 Inspector 值构造；PlayerStateMachine.SpeedOf 读取。
        public PlayerSpeeds(float walk, float run, float sprint, float crouchWalk, float crouchJog)
        {
            Walk = walk;
            Run = run;
            Sprint = sprint;
            CrouchWalk = crouchWalk;
            CrouchJog = crouchJog;
        }
    }

    // 主要功能：装状态机每帧要读写的全部状态，兼作「动画参数」的唯一来源。
    // 主要数据成员：Crouch/Grounded/ActionBusy/Dead 等布尔，Speed/Direction/ActionId 等浮点，以及动作计时 ActionFrame/ActionFrames。
    // 主要外部方法：BuildParams 产出参数快照、ResetAction 清动作。
    // 谁在用它：PlayerStateMachine（读写）、PlayerBrain（构造并发布参数）、EditMode 测试（直接断言字段）。
    public sealed class PlayerStateContext
    {
        // 位移能力，由 PlayerMove 实现
        public readonly IPlayerMotion Motion;

        // 当前移动速度，-1~1（写进动画参数用于分流）
        public float Speed;

        // 横向速度，-1~1（左负右正）
        public float Direction;

        // 是否蹲下（Ctrl 切换）
        public bool Crouch;

        // 是否踩在地面上（R035d 之前恒为 true）
        public bool Grounded = true;

        // 竖直速度，米/秒（R035d 之前恒为 0）
        public float VerticalSpeed;

        // 是否有动作在播
        public bool ActionBusy;

        // 当前动作编号，0~11
        public float ActionId = PlayerActionId.None;

        // 受击方向，-1 左 / 1 右
        public float StackSide = 1f;

        // 姿势编号，0~6
        public float PoseIndex;

        // 是否死亡
        public bool Dead;

        // 动作已播帧数，0 表示刚开始
        public int ActionFrame;

        // 动作总帧数，0 表示不自动结束
        public int ActionFrames;

        // 功能：绑定位移能力。
        // 入参：motion 是位移实现，不能为 null。
        // 返回：无。
        // 步骤：只保存引用，不改它的任何字段。
        // 上游/下游：PlayerBrain.Awake 构造；PlayerStateMachine 每帧调 Motion。
        public PlayerStateContext(IPlayerMotion motion)
        {
            Motion = motion;
        }

        // 功能：把当前字段打成一帧动画参数。
        // 入参：无。
        // 返回：PlayerAnimatorParams 值快照。
        // 步骤：按构造参数顺序逐个取字段，不做换算（换算在状态机里）。
        // 上游/下游：PlayerStateMachine.Tick 末尾调用；PlayerBrain 交给 IPlayerAnimatorSink.Apply。
        public PlayerAnimatorParams BuildParams()
        {
            return new PlayerAnimatorParams(
                Speed,
                Direction,
                Crouch,
                Grounded,
                VerticalSpeed,
                ActionBusy,
                ActionId,
                StackSide,
                PoseIndex,
                Dead);
        }

        // 功能：清掉当前动作，让状态机可以回到移动层。
        // 入参：无。
        // 返回：无。
        // 步骤：先关标记再清编号与计时，避免中途被读到「忙碌但没有编号」。
        // 上游/下游：PlayerStateMachine.EndAction / ApplyPendingAction 调用。
        public void ResetAction()
        {
            ActionBusy = false;
            ActionId = PlayerActionId.None;
            ActionFrame = 0;
            ActionFrames = 0;
        }
    }
}
