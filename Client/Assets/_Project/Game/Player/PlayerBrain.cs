// 这个文件里有：
//   PlayerBrain（组件）：用框架状态机管角色的逻辑状态（Idle / Move），并据此开关移动。

using NBC.Framework.Fsm;
using NBC.Framework.Input;
using UnityEngine;

namespace NBC.Game.Player
{
    // 主要功能：每帧判断「有没有方向输入」，据此在 Idle / Move 之间切逻辑状态，并把状态变化打成一条日志。
    // 主要数据成员：m_move（要开关的移动组件）、m_fsm（框架状态机）、m_frame（自增帧号，喂给 Tick）。
    // 主要外部方法：Current / CurrentName / IsMoving / RegisteredCount（只读观测）。
    // 谁在用它：挂在玩家物体上，和 PlayerMove / PlayerAnimation 同一个物体；由场景配置启用。
    // 注意：状态机是帧驱动（StateTick 只有 Frame / DeltaFrames，没有 DeltaTime）—— 位移仍由 PlayerMove 按真实时间算，别在这里算位移。
    // 注意：逻辑状态（本组件）与表现状态（Animator 的 S_Idle / S_Walk）是两层，本组件不写 Animator。
    // 注意：帧号用自增计数器而不是 Time.frameCount，避免编辑器暂停 / 时间缩放让语义变化；帧号只增不减。
    public sealed class PlayerBrain : MonoBehaviour
    {
        // 玩家身上的移动组件，Awake 时自动找（没找到会报错并停用自己）
        [SerializeField]
        private PlayerMove m_move;

        // 调试观察：当前状态名（运行时自动更新，只给人看，别手改）—— Unity 默认 Inspector 只显示字段，不显示属性
        [SerializeField]
        private string m_debugStateName = "Idle";

        // 调试观察：已注册状态数（运行时自动更新，应为 2）
        [SerializeField]
        private int m_debugRegistered;

        // 框架状态机：注册 Idle / Move，切换由「有没有方向输入」驱动
        private StateMachine<EPlayerState> m_fsm;

        // 喂给状态机的帧号：每帧 +1，从 0 起（状态机允许从 0 开始）
        private int m_frame;

        // 功能：当前逻辑状态。
        // 返回：EPlayerState 值；状态机没启动时返回 Idle
        public EPlayerState Current
        {
            get { return m_fsm == null || !m_fsm.IsStarted ? EPlayerState.Idle : m_fsm.Current; }
        }

        // 功能：当前状态名，给日志与调试面板用。
        // 返回：状态名（Idle / Move）；未启动时返回 Idle
        public string CurrentName
        {
            get { return m_fsm == null || !m_fsm.IsStarted ? "Idle" : m_fsm.CurrentName; }
        }

        // 功能：是否处于可移动状态。
        // 返回：true = Move
        public bool IsMoving
        {
            get { return Current == EPlayerState.Move; }
        }

        // 功能：已注册的状态数，用于在 Inspector 里确认装配成功（应为 2）。
        // 返回：注册数；未初始化时 0
        public int RegisteredCount
        {
            get { return m_fsm == null ? 0 : m_fsm.RegisteredCount; }
        }

        // 功能：装配状态机 —— 找移动组件、注册两个状态、订阅日志、以 Idle 启动。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没配 m_move 就在本物体上找
        //   2) 找不到就报错并停用（否则后面每帧都会空引用）
        //   3) 建状态机、注册 Idle / Move
        //   4) 订阅 StateChanged 打日志（只在这里打，状态类不打，避免重复）
        //   5) 以 Idle 启动（启动前不能切状态）
        // 上游：Unity 引擎（物体激活时）
        // 下游：PlayerIdleState / PlayerMoveState、Debug.Log
        // 注意：必须在 Start 之前启动状态机；TryChangeState 在 Start 之前调用会抛异常
        private void Awake()
        {
            if (m_move == null)
            {
                m_move = GetComponent<PlayerMove>();
            }

            if (m_move == null)
            {
                Debug.LogError("[PlayerBrain] 找不到 PlayerMove，逻辑状态机无法开关移动。", this);
                enabled = false;
                return;
            }

            m_fsm = new StateMachine<EPlayerState>();
            m_fsm.Register(EPlayerState.Idle, new PlayerIdleState(m_move));
            m_fsm.Register(EPlayerState.Move, new PlayerMoveState(m_move));
            m_fsm.StateChanged += OnStateChanged;
            m_fsm.Start(EPlayerState.Idle);

            m_debugStateName = m_fsm.CurrentName;
            m_debugRegistered = m_fsm.RegisteredCount;
        }

        // 功能：每帧按「有没有方向输入」决定逻辑状态，然后推进状态机。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 读输入；输入被禁用或读不到时当作「没输入」
        //   2) 有方向分量（X 或 Y 非 0）就是要移动
        //   3) 期望状态与当前不一致时才 TryChangeState（同状态切是空操作，但避免每帧白调）
        //   4) Tick 推进（帧号自增），状态的 OnUpdate 在这时被调用
        // 上游：Unity 引擎每帧调用
        // 下游：StateMachine.TryChangeState / Tick、PlayerMove.enabled
        // 注意：判据用「输入有没有方向」而不是「当前速度」—— 被墙挡住时速度是 0，用速度判会反复切状态
        private void Update()
        {
            bool hasInput = HasDirectionInput();
            EPlayerState wanted = hasInput ? EPlayerState.Move : EPlayerState.Idle;

            if (wanted != m_fsm.Current)
            {
                m_fsm.TryChangeState(wanted);
            }

            m_frame++;
            m_fsm.Tick(m_frame);
        }

        // 功能：组件被禁用时停掉状态机订阅，避免残留回调。
        // 入参：无
        // 返回：无
        // 步骤：有状态机就退订
        // 上游：Unity 引擎
        // 下游：无
        private void OnDisable()
        {
            if (m_fsm != null)
            {
                m_fsm.StateChanged -= OnStateChanged;
            }
        }

        // 功能：判断本帧输入里有没有方向分量。
        // 入参：无
        // 返回：true = X 或 Y 不为 0（量化轴，范围 ±1000）
        // 步骤：
        //   1) 取 InputManager；为空或整体被禁用则视为没输入
        //   2) 看 Current 的 MoveX / MoveY 是否有非 0
        // 上游：Update
        // 下游：InputManager.Instance.Current、InputCommand.MoveX / MoveY
        private static bool HasDirectionInput()
        {
            InputManager input = InputManager.Instance;

            if (input == null || !input.IsEnabled)
            {
                return false;
            }

            InputCommand command = input.Current;
            return command.MoveX != 0 || command.MoveY != 0;
        }

        // 功能：状态变化时打一条日志（只在这里打，保证「一次切换一条」）。
        // 入参：record 状态变化记录（含 From / To / Frame / Forced）
        // 返回：无
        // 步骤：直接拼字符串输出
        // 上游：StateMachine.StateChanged
        // 下游：Console
        // 注意：日志必须只在切换时产生 —— 每帧打印会把 Console 刷满，也掩盖真正的问题
        private void OnStateChanged(StateChangeRecord record)
        {
            Debug.Log("[PlayerBrain] " + record.From + " → " + record.To);
            m_debugStateName = record.To;
        }
    }
}
