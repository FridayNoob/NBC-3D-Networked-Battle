// 这个文件里有：
//   PlayerIdleState（状态）：站着不动，进入时禁用 PlayerMove。
//   PlayerMoveState（状态）：可移动，进入时启用 PlayerMove。

using NBC.Framework.Fsm;

namespace NBC.Game.Player
{
    // 主要功能：代表「站着」这一逻辑状态 —— 进入时禁用它身上的 PlayerMove，让角色不能移动。
    // 主要数据成员：m_move（要开关的那个组件）。
    // 主要外部方法：OnEnter / OnExit（开关移动）、Name。
    // 谁在用它：PlayerBrain 在 Awake 时注册进状态机。
    // 注意：本状态不碰 Animator —— 停下时动画自己回 Idle，因为 PlayerMove 被禁用后速度归零（见 PlayerMove.OnDisable）。
    internal sealed class PlayerIdleState : StateBase<EPlayerState>
    {
        private readonly PlayerMove m_move;

        // 功能：记住要开关的组件，状态本身不含其它逻辑。
        // 入参：move 玩家身上的 PlayerMove（不能为 null，由 PlayerBrain 保证）
        // 返回：无
        // 步骤：直接赋值
        // 上游：PlayerBrain.Awake
        // 下游：OnEnter 用它禁用移动
        public PlayerIdleState(PlayerMove move)
        {
            m_move = move;
        }

        // 功能：状态名，日志与状态历史里显示的就是它。
        // 返回：固定字符串 Idle
        public override string Name
        {
            get { return "Idle"; }
        }

        // 功能：进入站姿时禁止移动。
        // 入参：previous 上一个状态（本状态用不到）
        // 返回：无
        // 步骤：直接禁用 PlayerMove（它被禁用时会自己把速度清零）
        // 上游：状态机在切换时调用
        // 下游：PlayerMove.OnDisable、PlayerAnimation（读到 0 速度 ⇒ 回 Idle）
        public override void OnEnter(EPlayerState previous)
        {
            m_move.enabled = false;
        }
    }

    // 主要功能：代表「可移动」这一逻辑状态 —— 进入时启用 PlayerMove，让输入重新生效。
    // 主要数据成员：m_move（要开关的那个组件）。
    // 主要外部方法：OnEnter / Name。
    // 谁在用它：PlayerBrain 在 Awake 时注册进状态机。
    // 注意：本状态只管「允许移动」，实际怎么动由 PlayerMove 决定（四向转身是 R013 定稿的，不在这里）。
    internal sealed class PlayerMoveState : StateBase<EPlayerState>
    {
        private readonly PlayerMove m_move;

        // 功能：记住要开关的组件。
        // 入参：move 玩家身上的 PlayerMove（不能为 null）
        // 返回：无
        // 步骤：直接赋值
        // 上游：PlayerBrain.Awake
        // 下游：OnEnter 用它启用移动
        public PlayerMoveState(PlayerMove move)
        {
            m_move = move;
        }

        // 功能：状态名，日志与状态历史里显示的就是它。
        // 返回：固定字符串 Move
        public override string Name
        {
            get { return "Move"; }
        }

        // 功能：进入可移动状态时启用移动组件。
        // 入参：previous 上一个状态（本状态用不到）
        // 返回：无
        // 步骤：直接启用 PlayerMove
        // 上游：状态机在切换时调用
        // 下游：PlayerMove.Update 重新开始读输入
        public override void OnEnter(EPlayerState previous)
        {
            m_move.enabled = true;
        }
    }
}
