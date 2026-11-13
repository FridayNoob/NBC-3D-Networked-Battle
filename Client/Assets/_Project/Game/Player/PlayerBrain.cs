// 这个文件里有：
//   PlayerBrain（组件）：玩家的输入适配层 —— 读键位、按固定 60fps 驱动机器、把参数写给 Animator
//
// 它只做三件事：读输入、按固定步长推进 PlayerStateMachine、发布动画参数。
// 具体「什么状态、什么时候切」全在 PlayerStateMachine 里，本类不做判断。

using NBC.Framework.Fsm;
using NBC.Framework.Input;
using UnityEngine;

namespace NBC.Game.Player
{
    // 主要功能：把玩家输入与角色状态机接起来，并保证逻辑按固定 60fps 推进（不随渲染帧率抖动）。
    // 主要数据成员：m_move（位移）、m_publisher（动画参数）、m_context（共享状态）、m_machine（三层状态机）。
    // 主要外部方法：NotifyAttack / NotifyHit / NotifyDead（给网络消息与调试键用）。
    // 谁在用它：挂在 World 的 [Player_Hero] 根节点上；Battle 不用这套（Battle 走网络快照）。
    // 注意：只有本类允许读 Input 与 Time —— 状态机是纯逻辑，测试直接喂 PlayerInput 与帧号。
    public sealed class PlayerBrain : MonoBehaviour
    {
        // 每秒逻辑帧数（动作帧数都按这个口径标）
        private const float FixedDeltaSeconds = 1f / 60f;

        // 一帧最多补几个逻辑步（卡顿后防止一次补几百帧把角色瞬移）
        private const int MaxCatchUpSteps = 4;

        // 玩家身上的移动组件（Awake 自动找）
        [SerializeField]
        private PlayerMove m_move;

        // 是否关掉旧的 PlayerAnimation（World 改由状态机 + 参数驱动；关掉避免它继续 SetTrigger）
        [SerializeField]
        private bool m_disableLegacyAnimation = true;

        // World 专用的动画控制器副本（为空则沿用模型自带控制器；Battle 不挂本组件，不受影响）
        [SerializeField]
        private RuntimeAnimatorController m_animatorControllerOverride;

        [Header("速度档位（米/秒）")]
        // 慢走速度
        [SerializeField]
        private float m_walkSpeed = 2f;

        // 默认跑速
        [SerializeField]
        private float m_runSpeed = 5f;

        // 冲刺速度
        [SerializeField]
        private float m_sprintSpeed = 8f;

        // 蹲走速度
        [SerializeField]
        private float m_crouchWalkSpeed = 1.5f;

        // 蹲跑速度
        [SerializeField]
        private float m_crouchJogSpeed = 3f;

        [Header("动作时长（60fps 帧数）")]
        // 攻击一段帧数
        [SerializeField]
        private int m_attackFrames = 36;

        // 受击帧数
        [SerializeField]
        private int m_hitFrames = 18;

        // 技能帧数
        [SerializeField]
        private int m_skillFrames = 48;

        // 冲刺帧数
        [SerializeField]
        private int m_dashFrames = 18;

        // 硬直帧数
        [SerializeField]
        private int m_stunFrames = 45;

        // 姿势帧数
        [SerializeField]
        private int m_poseFrames = 90;

        [Header("按键")]
        // 攻击键（鼠标左键也可）
        [SerializeField]
        private KeyCode m_attackKey = KeyCode.J;

        // 受击调试键
        [SerializeField]
        private KeyCode m_hitKey = KeyCode.K;

        // 死亡调试键
        [SerializeField]
        private KeyCode m_dieKey = KeyCode.L;

        // 是否接受本地攻击键（Battle 可关掉，避免和网络输入双触发）
        [SerializeField]
        private bool m_enableLocalAttack = true;

        // 是否启用调试键 K/L（编辑器里始终启用，方便验收；构建版本按这个开关决定）
        [SerializeField]
        private bool m_enableDebugKeys = false;

        // 是否把每次状态切换打到 Console
        [SerializeField]
        private bool m_logStateChanges = true;

        // 位移组件（IPlayerMotion 的实现者）
        private PlayerMove m_motion;

        // 动画参数发布器
        private PlayerAnimatorPublisher m_publisher;

        // 状态机共享状态
        private PlayerStateContext m_context;

        // 三层状态机
        private PlayerStateMachine m_machine;

        // 已推进的逻辑帧号（必须单调递增）
        private int m_frame;

        // 距上一个逻辑帧攒下的时间，秒
        private float m_frameAccumulator;

        // 功能：找齐组件、构造状态机、订阅状态切换日志。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 找 PlayerMove（找不到就禁用自己）2) 找或加 Publisher 3) 关旧动画 4) 换控制器副本 5) 建上下文与状态机 6) 订阅日志。
        // 上游/下游：Unity 生命周期；下游 PlayerStateMachine 构造函数。
        private void Awake()
        {
            m_motion = m_move != null ? m_move : GetComponent<PlayerMove>();

            if (m_motion == null)
            {
                Debug.LogError("[PlayerBrain] 根节点上没有 PlayerMove，状态机无法驱动位移。", this);
                enabled = false;
                return;
            }

            m_publisher = GetComponent<PlayerAnimatorPublisher>();
            if (m_publisher == null)
            {
                m_publisher = gameObject.AddComponent<PlayerAnimatorPublisher>();
            }

            if (m_disableLegacyAnimation)
            {
                PlayerAnimation legacy = GetComponent<PlayerAnimation>();
                if (legacy != null)
                {
                    legacy.enabled = false;
                }
            }

            if (m_animatorControllerOverride != null)
            {
                Animator animator = GetComponentInChildren<Animator>();
                if (animator != null && animator.runtimeAnimatorController != m_animatorControllerOverride)
                {
                    animator.runtimeAnimatorController = m_animatorControllerOverride;
                }
            }

            m_context = new PlayerStateContext(m_motion);
            PlayerTimings timings = new PlayerTimings(
                m_attackFrames,
                m_hitFrames,
                m_skillFrames,
                m_dashFrames,
                m_stunFrames,
                m_poseFrames);
            PlayerSpeeds speeds = new PlayerSpeeds(
                m_walkSpeed,
                m_runSpeed,
                m_sprintSpeed,
                m_crouchWalkSpeed,
                m_crouchJogSpeed);
            m_machine = new PlayerStateMachine(m_context, timings, speeds);

            if (m_logStateChanges)
            {
                m_machine.StateChanged += OnStateChanged;
            }
        }

        // 功能：每帧读输入、按固定步长推进逻辑、发布动画参数。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 读一次输入 2) 先处理攻击/受击/死亡键（写 pending）3) 累加时间并按 1/60 秒补步 4) 只让第一步消费「蹲切换」 5) 发布了就写一次参数。
        // 上游/下游：Unity 生命周期；下游 PlayerStateMachine.Tick、PlayerAnimatorPublisher.Apply。
        private void Update()
        {
            if (m_machine == null)
            {
                return;
            }

            PlayerInput input = ReadInput();
            HandleActionKeys();

            m_frameAccumulator += Time.deltaTime;
            int steps = 0;
            bool firstStep = true;

            while (m_frameAccumulator >= FixedDeltaSeconds && steps < MaxCatchUpSteps)
            {
                m_frameAccumulator -= FixedDeltaSeconds;
                steps++;
                m_frame++;

                PlayerInput stepInput = firstStep
                    ? input
                    : new PlayerInput(input.MoveX, input.MoveY, input.WalkHeld, input.SprintHeld, false);
                firstStep = false;
                m_machine.Tick(m_frame, stepInput);
            }

            if (steps == 0)
            {
                return;
            }

            if (m_frameAccumulator > FixedDeltaSeconds * MaxCatchUpSteps)
            {
                m_frameAccumulator = 0f;
            }

            m_publisher.Apply(m_machine.Parameters);
        }

        // 功能：请求播攻击（本地键与网络消息都走这里）。
        // 入参：无。
        // 返回：无。
        // 步骤：转给状态机；被拒绝（已死/动作中）时什么都不做。
        // 上游/下游：Update 的攻击键、外部网络消息；下游 PlayerStateMachine.NotifyAttack。
        public void NotifyAttack()
        {
            if (m_machine != null)
            {
                m_machine.NotifyAttack();
            }
        }

        // 功能：请求播受击。
        // 入参：fromLeft 为 true 表示从左侧被打。
        // 返回：无。
        // 步骤：转给状态机。
        // 上游/下游：Update 的调试键、外部网络消息；下游 PlayerStateMachine.NotifyHit。
        public void NotifyHit(bool fromLeft)
        {
            if (m_machine != null)
            {
                m_machine.NotifyHit(fromLeft);
            }
        }

        // 功能：请求进入死亡状态。
        // 入参：无。
        // 返回：无。
        // 步骤：转给状态机。
        // 上游/下游：Update 的调试键、外部网络消息；下游 PlayerStateMachine.NotifyDead。
        public void NotifyDead()
        {
            if (m_machine != null)
            {
                m_machine.NotifyDead();
            }
        }

        // 当前是否已死亡
        public bool IsDead
        {
            get { return m_machine != null && m_machine.IsDead; }
        }

        // 当前下层状态（日志/调试面板用）
        public EPlayerState CurrentState
        {
            get { return m_machine != null ? m_machine.State : EPlayerState.Idle; }
        }

        // 功能：处理攻击/受击/死亡的按键请求。
        // 入参：无。
        // 返回：无。
        // 步骤：调试键先判（K 左受击、L 死亡），再看攻击键（J 或鼠标左键）；编辑器里调试键始终可用，构建版本看 Inspector 开关。
        // 上游/下游：Update 调用；下游 Notify*。
        private void HandleActionKeys()
        {
            if (m_enableDebugKeys || Application.isEditor)
            {
                if (Input.GetKeyDown(m_hitKey))
                {
                    NotifyHit(true);
                }

                if (Input.GetKeyDown(m_dieKey))
                {
                    NotifyDead();
                }
            }

            if (!m_enableLocalAttack)
            {
                return;
            }

            if (Input.GetKeyDown(m_attackKey) || Input.GetMouseButtonDown(0))
            {
                NotifyAttack();
            }
        }

        // 功能：把这一帧的键位读成 PlayerInput。
        // 入参：无。
        // 返回：输入快照。
        // 步骤：移动轴取 InputManager（-1000~1000 除 1000）；Alt=慢走、Shift=冲刺、Ctrl 按下=切换蹲；输入被禁用时轴按 0。
        // 上游/下游：Update 调用；下游 PlayerStateMachine.Tick。
        private static PlayerInput ReadInput()
        {
            float x = 0f;
            float y = 0f;

            InputManager manager = InputManager.Instance;
            if (manager != null && manager.IsEnabled)
            {
                InputCommand command = manager.Current;
                x = (float)command.MoveX / InputCommand.MoveScale;
                y = (float)command.MoveY / InputCommand.MoveScale;
            }

            bool walk = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool sprint = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool crouch = Input.GetKeyDown(KeyCode.LeftControl) || Input.GetKeyDown(KeyCode.RightControl);
            return new PlayerInput(x, y, walk, sprint, crouch);
        }

        // 功能：把一次状态切换打到 Console。
        // 入参：record 切换记录（含帧号与前后状态名）。
        // 返回：无。
        // 步骤：直接用记录自带的 ToString，格式 "[f12] Locomotion/Idle -> Locomotion/Run"。
        // 上游/下游：PlayerStateMachine.StateChanged 事件。
        private void OnStateChanged(StateChangeRecord record)
        {
            Debug.Log("[PlayerBrain] " + record);
        }

        // 功能：组件销毁时退订事件，避免状态机持有已销毁的 MonoBehaviour。
        // 入参：无。
        // 返回：无。
        // 步骤：有状态机就退订。
        // 上游/下游：Unity 生命周期。
        private void OnDestroy()
        {
            if (m_machine != null)
            {
                m_machine.StateChanged -= OnStateChanged;
            }
        }
    }
}
