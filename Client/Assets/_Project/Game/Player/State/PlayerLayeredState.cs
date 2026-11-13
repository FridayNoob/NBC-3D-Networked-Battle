// 这个文件里有：
//   PlayerLayeredState（状态类）：分层状态机里每一个下层状态的最小实现

using NBC.Framework.Fsm;

namespace NBC.Game.Player
{
    // 主要功能：给每个 EPlayerState 提供一个状态对象，行为全部由 PlayerStateMachine 统一驱动。
    // 主要数据成员：m_id（它代表的状态值）。
    // 主要外部方法：Name（日志里显示 "Idle"/"Attack1" 这样的名字）、Id。
    // 谁在用它：PlayerStateMachine 在注册移动层/动作层/反应层时各 new 一个。
    // 注意：本类故意不写 OnEnter/OnExit 业务逻辑 —— 进入/离开时要改的字段都在 PlayerStateContext 里，由状态机统一改，避免两处各改一半。
    public sealed class PlayerLayeredState : StateBase<EPlayerState>
    {
        // 本状态代表的状态值
        private readonly EPlayerState m_id;

        // 功能：记住自己代表哪个状态。
        // 入参：id 是 EPlayerState 的下层状态值。
        // 返回：无。
        // 步骤：只保存，不做校验。
        // 上游/下游：PlayerStateMachine.构造函数调用。
        public PlayerLayeredState(EPlayerState id)
        {
            m_id = id;
        }

        // 当前状态值，供日志与调试显示
        public EPlayerState Id
        {
            get { return m_id; }
        }

        // 功能：状态名 —— 状态机 CurrentPath 与转移记录都读它。
        // 入参：无。
        // 返回：枚举名，例如 "CrouchJog"。
        // 步骤：直接 ToString；不额外拼前缀（层名由 CurrentPath 拼）。
        // 上游/下游：HierarchicalStateMachine.CurrentPath、StateChangeRecord 读取。
        public override string Name
        {
            get { return m_id.ToString(); }
        }
    }
}
