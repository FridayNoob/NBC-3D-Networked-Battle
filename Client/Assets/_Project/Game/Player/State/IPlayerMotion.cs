// 这个文件里有：
//   IPlayerMotion（接口）：状态机对「位移能力」的全部要求，PlayerMove 实现它
//   IPlayerAnimatorSink（接口）：状态机对「写动画参数」的全部要求，PlayerAnimatorPublisher 实现它

namespace NBC.Game.Player
{
    // 主要功能：把「能不能走 / 目标速度 / 当前速度」抽成一个窄接口，让状态机不依赖 MonoBehaviour。
    // 主要数据成员：MovementEnabled 属性 + CurrentSpeed 只读属性。
    // 主要外部方法：SetMoveSpeed 设置这一帧的移动速度。
    // 谁在用它：PlayerStateMachine（生产端只调这三个成员）；PlayerMove（唯一实现）。
    // 约定：MovementEnabled=false 时实现方必须停住并零速；CurrentSpeed 单位米/秒，只读。
    public interface IPlayerMotion
    {
        // 是否允许位移（false 时实现方必须原地站住，但不要求禁用组件）
        bool MovementEnabled { get; set; }

        // 当前实际移动速度，米/秒（用于表现层归一化）
        float CurrentSpeed { get; }

        // 功能：设置这一帧移动时使用的速度。
        // 入参：metersPerSecond 单位米/秒，0 表示不动，负数由实现方忽略。
        // 返回：无。
        // 上游/下游：PlayerStateMachine.Tick 调用；PlayerMove.Update 读取。
        void SetMoveSpeed(float metersPerSecond);
    }

    // 主要功能：把「写动画参数」抽成接口，让状态机可以在 EditMode 测试里用假实现断言参数。
    // 主要数据成员：无。
    // 主要外部方法：Apply 一次性写入一帧全部参数（实现方必须幂等：值没变就不写）。
    // 谁在用它：PlayerStateMachine 产出参数后由 PlayerBrain 调；PlayerAnimatorPublisher（唯一实现）。
    public interface IPlayerAnimatorSink
    {
        // 功能：把一帧参数写进动画系统。
        // 入参：parameters 是只读快照，实现方不得缓存引用（结构体本身是值）。
        // 返回：无。
        // 上游/下游：PlayerBrain.Update 调用；PlayerAnimatorPublisher.Apply 实现。
        void Apply(in PlayerAnimatorParams parameters);
    }
}
