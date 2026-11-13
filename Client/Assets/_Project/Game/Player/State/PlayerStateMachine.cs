// 这个文件里有：
//   PlayerStateMachine（类）：玩家分层状态机 —— 纯逻辑，不读 Time / Input / Animator，测试可直接驱动

using System;
using NBC.Framework.Fsm;

namespace NBC.Game.Player
{
    // 主要功能：按「反应 > 动作 > 移动」的优先级决定当前该在哪个状态，并把结果翻译成移动闸门与动画参数。
    // 主要数据成员：m_context（共享状态）、m_timings（动作帧数）、m_speeds（五档速度）、m_fsm（三层状态机）。
    // 主要外部方法：Tick 推进一帧；NotifyAttack/NotifyHit/NotifyDead 接收外部事件；Parameters 读当前动画参数。
    // 谁在用它：PlayerBrain 每帧调 Tick 并发布 Parameters；EditMode 测试用假 IPlayerMotion 驱动它。
    // 注意：本类不引用 UnityEngine —— 取绝对值用 System.Math，状态名用枚举，保证能在 EditMode 无场景下跑。
    public sealed class PlayerStateMachine
    {
        // 共享状态（蹲/动作/死亡等），每帧读写
        private readonly PlayerStateContext m_context;

        // 各动作持续帧数，60fps 口径
        private readonly PlayerTimings m_timings;

        // 五档移动速度，米/秒
        private readonly PlayerSpeeds m_speeds;

        // 三层状态机：移动层 / 动作层 / 反应层
        private readonly HierarchicalStateMachine<EPlayerState> m_fsm;

        // 有没有「等着这一帧执行」的动作请求
        private bool m_hasPendingAction;

        // 待执行动作的状态
        private EPlayerState m_pendingState;

        // 待执行动作的编号（写进 ActionId）
        private float m_pendingActionId;

        // 待执行动作的总帧数
        private int m_pendingFrames;

        // 连击是否已排队：窗口内按过攻击键，等当前段播完再切下一段
        private bool m_comboQueued;

        // 是否正在按住格挡；格挡不占 ActionBusy，但会关掉移动闸门
        private bool m_blocking;

        // 当前姿势序号 0~6，按 F 轮换
        private int m_poseIndex;

        // 功能：注册三层状态机并进入移动层的 Idle。
        // 入参：context 共享状态（不能为 null，且 Motion 不能为 null）；timings 动作帧数；speeds 五档速度。
        // 返回：无。
        // 步骤：1) 校验参数 2) 依次注册三层（先上层后下层，第一层下层自动成为入口）3) 启动到移动层。
        // 上游/下游：PlayerBrain.Awake 构造；内部调用 RegisterLocomotion/Action/Reaction。
        public PlayerStateMachine(PlayerStateContext context, PlayerTimings timings, PlayerSpeeds speeds)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context), "[PlayerStateMachine] 缺少共享状态。");
            }

            if (context.Motion == null)
            {
                throw new ArgumentNullException(nameof(context), "[PlayerStateMachine] 共享状态里没有 IPlayerMotion。");
            }

            m_context = context;
            m_timings = timings;
            m_speeds = speeds;
            m_fsm = new HierarchicalStateMachine<EPlayerState>();

            m_fsm.RegisterUpper(EPlayerState.Locomotion, new PlayerLayeredState(EPlayerState.Locomotion), EPlayerState.Idle);
            RegisterLocomotionStates();
            m_fsm.RegisterUpper(EPlayerState.Action, new PlayerLayeredState(EPlayerState.Action), EPlayerState.Attack1);
            RegisterActionStates();
            m_fsm.RegisterUpper(EPlayerState.Reaction, new PlayerLayeredState(EPlayerState.Reaction), EPlayerState.HitLeft);
            RegisterReactionStates();
            m_fsm.Start(EPlayerState.Locomotion);

            m_context.Motion.MovementEnabled = true;
        }

        // 当前上层（Locomotion / Action / Reaction）
        public EPlayerLayer Layer
        {
            get { return PlayerStateCatalog.LayerOf(m_fsm.UpperCurrent); }
        }

        // 当前下层状态（真正的「在干什么」）
        public EPlayerState State
        {
            get { return m_fsm.LowerCurrent; }
        }

        // 当前状态路径，形如 "Locomotion/Run"；给日志与调试面板看
        public string CurrentPath
        {
            get { return m_fsm.CurrentPath; }
        }

        // 是否已死亡（死亡后 Tick 只维持参数、不再切换状态）
        public bool IsDead
        {
            get { return m_context.Dead; }
        }

        // 这一帧要写给 Animator 的参数快照
        public PlayerAnimatorParams Parameters
        {
            get { return m_context.BuildParams(); }
        }

        // 当前姿势序号 0~6；按 F 依次轮换
        public int PoseIndex
        {
            get { return m_poseIndex; }
        }

        // 是否正按住格挡
        public bool Blocking
        {
            get { return m_blocking; }
        }

        // 状态真的发生切换后触发；参数是切换记录（含帧号与前后状态名）
        public event Action<StateChangeRecord> StateChanged
        {
            add { m_fsm.StateChanged += value; }
            remove { m_fsm.StateChanged -= value; }
        }

        // 功能：推进一帧 —— 先看有没有动作要播，再决定移动状态，最后同步移动闸门与速度。
        // 入参：frame 逻辑帧号（必须单调递增，60fps 口径）；input 这一帧的输入快照。
        // 返回：无。
        // 步骤：1) 死亡时只维持零速 2) 处理蹲切换 3) 推进/消费动作 4) 动作中不选移动态 5) 同步 MovementEnabled 与速度 6) 推状态机帧。
        // 上游/下游：PlayerBrain.Update 每个固定步长调用一次；内部调用 ChooseLocomotion/ApplyLocomotion。
        public void Tick(int frame, in PlayerInput input)
        {
            if (m_context.Dead)
            {
                m_context.Speed = 0f;
                m_context.Direction = 0f;
                m_context.ResetAction();
                m_context.Motion.MovementEnabled = false;
                m_context.Motion.SetMoveSpeed(0f);
                m_fsm.Tick(frame);
                return;
            }

            if (input.CrouchToggle && !m_context.ActionBusy)
            {
                m_context.Crouch = !m_context.Crouch;
            }

            AdvanceAction();

            if (m_hasPendingAction)
            {
                ApplyPendingAction();
            }

            if (!m_context.ActionBusy)
            {
                ApplyLocomotion(ChooseLocomotion(input));
            }

            bool canMove = !m_blocking && !PlayerStateCatalog.BlocksMovement(m_fsm.LowerCurrent);
            m_context.Speed = canMove ? SpeedOf(m_fsm.LowerCurrent) : 0f;
            m_context.Direction = canMove ? ClampDirection(input.MoveX) : 0f;
            m_context.Motion.SetMoveSpeed(m_context.Speed);
            m_context.Motion.MovementEnabled = canMove;

            m_fsm.Tick(frame);
        }

        // 功能：请求播攻击；空闲时起手第 1 段，连击窗口内则排队下一段。
        // 入参：无。
        // 返回：true 表示已起手或已排队；false 表示已死、动作不在攻击链上、或不在窗口内。
        // 步骤：1) 死亡拒绝 2) 空闲直接起手第 1 段 3) 动作层里只接第 1/2 段 4) 帧号不在窗口内拒绝 5) 只做排队，不切当前状态。
        // 上游/下游：PlayerBrain 攻击键调用；内部调用 BeginAction，下一段在 AdvanceAction 里接。
        public bool NotifyAttack()
        {
            if (m_context.Dead)
            {
                return false;
            }

            if (!m_context.ActionBusy)
            {
                return BeginAction(EPlayerState.Attack1, PlayerActionId.Attack1, m_timings.AttackFrames);
            }

            if (PlayerStateCatalog.LayerOf(m_fsm.LowerCurrent) != EPlayerLayer.Action)
            {
                return false;
            }

            if (m_context.ActionId != PlayerActionId.Attack1 && m_context.ActionId != PlayerActionId.Attack2)
            {
                return false;
            }

            if (m_context.ActionFrame < m_timings.ComboWindowStart)
            {
                return false;
            }

            if (m_context.ActionFrames > 0 && m_context.ActionFrame > m_context.ActionFrames - m_timings.ComboEndBuffer)
            {
                return false;
            }

            m_comboQueued = true;
            return true;
        }

        // 功能：请求播第 index 个技能（只播动画，不产生伤害）。
        // 入参：index 取 1~3，超出范围拒绝。
        // 返回：true 表示已排队；false 表示已死、有动作在播、或正在格挡。
        // 步骤：先做前置拒绝，再按序号取状态、ActionId 与帧数。
        // 上游/下游：PlayerBrain 的 Q/E/R 调用；内部调用 BeginAction。
        public bool NotifySkill(int index)
        {
            if (m_context.Dead || m_context.ActionBusy || m_blocking)
            {
                return false;
            }

            if (index < 1 || index > 3)
            {
                return false;
            }

            EPlayerState state = (EPlayerState)((int)EPlayerState.Skill1 + index - 1);
            int frames = index == 1 ? m_timings.Skill1Frames : (index == 2 ? m_timings.Skill2Frames : m_timings.Skill3Frames);
            return BeginAction(state, PlayerActionId.OfSkill(index), frames);
        }

        // 功能：请求前冲一段。
        // 入参：无。
        // 返回：true 表示已排队；false 表示已死、有动作在播、或正在格挡。
        // 步骤：前置拒绝后进 Dash，帧数取 DashFrames；位移由 PlayerBrain 通过 IPlayerDash 触发。
        // 上游/下游：PlayerBrain 的 V 键调用；内部调用 BeginAction。
        public bool NotifyDash()
        {
            if (m_context.Dead || m_context.ActionBusy || m_blocking)
            {
                return false;
            }

            return BeginAction(EPlayerState.Dash, PlayerActionId.Dash, m_timings.DashFrames);
        }

        // 功能：轮换到下一个姿势（七个 CBG 表情动作）。
        // 入参：无。
        // 返回：true 表示已排队；false 表示已死、有动作在播、或正在格挡。
        // 步骤：先把 PoseIndex 加一并对 7 取模，再进 Pose。
        // 上游/下游：PlayerBrain 的 F 键调用；内部调用 BeginAction。
        public bool NotifyPose()
        {
            if (m_context.Dead || m_context.ActionBusy || m_blocking)
            {
                return false;
            }

            m_poseIndex = (m_poseIndex + 1) % 7;
            return BeginAction(EPlayerState.Pose, PlayerActionId.Pose, m_timings.PoseFrames);
        }

        // 功能：按住/松开格挡。格挡是常驻动作，不占 ActionBusy。
        // 入参：blocking 为 true 表示按住右键。
        // 返回：无。
        // 步骤：按住时若有动作在播就忽略；否则写 ActionId=8 并关移动闸门。松开时清回 None。
        // 上游/下游：PlayerBrain 每帧调用；动画控制器按 ActionId==8 进 S_Block。
        public void SetBlocking(bool blocking)
        {
            if (m_context.Dead)
            {
                return;
            }

            if (blocking)
            {
                if (m_blocking || m_context.ActionBusy)
                {
                    return;
                }

                m_blocking = true;
                m_context.ActionId = PlayerActionId.Block;
                m_context.ActionFrame = 0;
                m_context.ActionFrames = 0;
                return;
            }

            if (!m_blocking)
            {
                return;
            }

            m_blocking = false;
            m_context.ActionId = PlayerActionId.None;
        }

        // 功能：请求播受击；受击优先级高于攻击，会把正在播的攻击打断。
        // 入参：fromLeft 为 true 表示从左侧被打（决定 StackSide 与 HitLeft/HitRight）。
        // 返回：true 表示请求已受理；false 表示已死。
        // 步骤：死亡直接拒绝；否则写 StackSide 并排队受击。
        // 上游/下游：PlayerBrain 受击调试键 / 将来网络受击消息调用。
        public bool NotifyHit(bool fromLeft)
        {
            if (m_context.Dead)
            {
                return false;
            }

            m_context.StackSide = fromLeft ? -1f : 1f;
            m_comboQueued = false;
            m_blocking = false;
            EPlayerState state = fromLeft ? EPlayerState.HitLeft : EPlayerState.HitRight;
            return BeginAction(state, PlayerActionId.Hit, m_timings.HitFrames);
        }

        // 功能：请求进入死亡状态，之后任何移动/动作请求都无效。
        // 入参：无。
        // 返回：true 表示这次真的死了；false 表示之前已经死了。
        // 步骤：1) 置死亡标记 2) 清动作与位移闸门 3) 强制切到反应层的 Die（跳过准入检查）。
        // 上游/下游：PlayerBrain 死亡调试键 / 将来网络死亡消息调用。
        public bool NotifyDead()
        {
            if (m_context.Dead)
            {
                return false;
            }

            m_context.Dead = true;
            m_context.ResetAction();
            m_hasPendingAction = false;
            m_comboQueued = false;
            m_blocking = false;
            m_context.Motion.MovementEnabled = false;
            m_context.Motion.SetMoveSpeed(0f);
            m_fsm.ForceChange(EPlayerState.Reaction, EPlayerState.Die);
            return true;
        }

        // 功能：按输入选一个移动状态（只在没有动作在播时用）。
        // 入参：input 这一帧的输入。
        // 返回：Idle / Walk / Run / Sprint / CrouchIdle / CrouchWalk / CrouchJog 之一。
        // 步骤：没方向就站着；蹲着优先蹲三态；否则按 Shift→冲刺、Alt→慢走、默认跑。
        // 上游/下游：Tick 调用。
        private EPlayerState ChooseLocomotion(in PlayerInput input)
        {
            if (!input.HasDirection())
            {
                return m_context.Crouch ? EPlayerState.CrouchIdle : EPlayerState.Idle;
            }

            if (m_context.Crouch)
            {
                return input.SprintHeld ? EPlayerState.CrouchJog : EPlayerState.CrouchWalk;
            }

            if (input.SprintHeld)
            {
                return EPlayerState.Sprint;
            }

            return input.WalkHeld ? EPlayerState.Walk : EPlayerState.Run;
        }

        // 功能：把目标移动状态落到状态机上（同层只换下层，跨层才换上层）。
        // 入参：desired 目标移动状态。
        // 返回：无；切换被拒绝时保持原状态。
        // 步骤：当前已在移动层就 TryChangeLower，否则 TryChangeUpper(移动层, desired)。
        // 上游/下游：Tick 调用；下游 HierarchicalStateMachine.TryChangeLower/TryChangeUpper。
        private void ApplyLocomotion(EPlayerState desired)
        {
            if (m_fsm.UpperCurrent == EPlayerState.Locomotion)
            {
                if (m_fsm.LowerCurrent != desired)
                {
                    m_fsm.TryChangeLower(desired);
                }

                return;
            }

            m_fsm.TryChangeUpper(EPlayerState.Locomotion, desired);
        }

        // 功能：推进当前动作的帧计数，到点就结束动作或接上排队的下一段连击。
        // 入参：无。
        // 返回：无。
        // 步骤：没有动作直接返回；有动作就 +1 帧；到点先看有没有排队连击，能接就接下一段，否则清动作。
        // 上游/下游：Tick 调用；下游 TryStartNextCombo / PlayerStateContext.ResetAction。
        private void AdvanceAction()
        {
            if (!m_context.ActionBusy)
            {
                return;
            }

            m_context.ActionFrame++;
            if (m_context.ActionFrames <= 0 || m_context.ActionFrame < m_context.ActionFrames)
            {
                return;
            }

            if (m_comboQueued && TryStartNextCombo())
            {
                m_comboQueued = false;
                return;
            }

            m_comboQueued = false;
            m_context.ResetAction();
        }

        // 功能：把当前攻击段接到下一段（1→2、2→3）。
        // 入参：无。
        // 返回：true 表示已登记下一段；false 表示当前不是第 1/2 段。
        // 步骤：按当前 ActionId 决定目标状态与帧数，再 BeginAction 排队，下一帧生效。
        // 上游/下游：AdvanceAction 调用；下游 BeginAction。
        private bool TryStartNextCombo()
        {
            if (m_context.ActionId == PlayerActionId.Attack1)
            {
                return BeginAction(EPlayerState.Attack2, PlayerActionId.Attack2, m_timings.Attack2Frames);
            }

            if (m_context.ActionId == PlayerActionId.Attack2)
            {
                return BeginAction(EPlayerState.Attack3, PlayerActionId.Attack3, m_timings.Attack3Frames);
            }

            return false;
        }

        // 功能：把排队中的动作请求真正执行（切到对应层与状态）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 先写 ActionBusy/ActionId/帧数 2) 目标就是当前配置时只重置计时 3) 否则 TryChangeUpper，被拒绝则回滚为「没有动作」。
        // 上游/下游：Tick 调用；下游 HierarchicalStateMachine.TryChangeUpper。
        private void ApplyPendingAction()
        {
            m_hasPendingAction = false;
            m_context.ActionBusy = true;
            m_context.ActionId = m_pendingActionId;
            m_context.ActionFrame = 0;
            m_context.ActionFrames = m_pendingFrames;

            EPlayerState upper = PlayerStateCatalog.UpperOf(PlayerStateCatalog.LayerOf(m_pendingState));

            if (m_fsm.UpperCurrent == upper && m_fsm.LowerCurrent == m_pendingState)
            {
                return;
            }

            if (!m_fsm.TryChangeUpper(upper, m_pendingState))
            {
                m_context.ResetAction();
            }
        }

        // 功能：登记一个待执行动作。
        // 入参：state 目标状态；actionId 写进动画参数的动作编号；frames 总帧数（60fps，<=0 表示不自动结束）。
        // 返回：true 表示已登记。
        // 步骤：只写 pending 字段，下一帧 Tick 再执行 —— 保证一帧内只发生一次状态切换。
        // 上游/下游：NotifyAttack / NotifyHit 调用。
        private bool BeginAction(EPlayerState state, float actionId, int frames)
        {
            m_pendingState = state;
            m_pendingActionId = actionId;
            m_pendingFrames = frames;
            m_hasPendingAction = true;
            return true;
        }

        // 功能：按状态取这一档移动速度。
        // 入参：state 当前移动状态。
        // 返回：米/秒；非移动状态返回 0。
        // 步骤：switch 一一映射到 PlayerSpeeds 的五个字段。
        // 上游/下游：Tick 调用。
        private float SpeedOf(EPlayerState state)
        {
            switch (state)
            {
                case EPlayerState.Walk:
                    return m_speeds.Walk;
                case EPlayerState.Run:
                    return m_speeds.Run;
                case EPlayerState.Sprint:
                    return m_speeds.Sprint;
                case EPlayerState.CrouchWalk:
                    return m_speeds.CrouchWalk;
                case EPlayerState.CrouchJog:
                    return m_speeds.CrouchJog;
                default:
                    return 0f;
            }
        }

        // 功能：把横向输入夹到 -1~1。
        // 入参：value 原始横向输入。
        // 返回：夹紧后的值。
        // 步骤：两段比较，不调用 UnityEngine.Mathf（本类保持纯 C#）。
        // 上游/下游：Tick 调用。
        private static float ClampDirection(float value)
        {
            if (value < -1f)
            {
                return -1f;
            }

            return value > 1f ? 1f : value;
        }

        // 功能：注册移动层的九个状态。
        // 入参：无。
        // 返回：无。
        // 步骤：Idle 必须第一个注册（它会成为入口），其余顺序无关。
        // 上游/下游：构造函数调用。
        private void RegisterLocomotionStates()
        {
            RegisterSub(EPlayerState.Idle);
            RegisterSub(EPlayerState.Walk);
            RegisterSub(EPlayerState.Run);
            RegisterSub(EPlayerState.Sprint);
            RegisterSub(EPlayerState.CrouchIdle);
            RegisterSub(EPlayerState.CrouchWalk);
            RegisterSub(EPlayerState.CrouchJog);
            RegisterSub(EPlayerState.Jump);
            RegisterSub(EPlayerState.Fall);
        }

        // 功能：注册动作层的九个状态。
        // 入参：无。
        // 返回：无。
        // 步骤：Attack1 第一个注册（成为入口）。
        // 上游/下游：构造函数调用。
        private void RegisterActionStates()
        {
            RegisterSub(EPlayerState.Attack1);
            RegisterSub(EPlayerState.Attack2);
            RegisterSub(EPlayerState.Attack3);
            RegisterSub(EPlayerState.Skill1);
            RegisterSub(EPlayerState.Skill2);
            RegisterSub(EPlayerState.Skill3);
            RegisterSub(EPlayerState.Dash);
            RegisterSub(EPlayerState.Block);
            RegisterSub(EPlayerState.Pose);
        }

        // 功能：注册反应层的四个状态。
        // 入参：无。
        // 返回：无。
        // 步骤：HitLeft 第一个注册（成为入口）。
        // 上游/下游：构造函数调用。
        private void RegisterReactionStates()
        {
            RegisterSub(EPlayerState.HitLeft);
            RegisterSub(EPlayerState.HitRight);
            RegisterSub(EPlayerState.Stun);
            RegisterSub(EPlayerState.Die);
        }

        // 功能：把某个下层状态注册到它所属的层下。
        // 入参：sub 下层状态。
        // 返回：无。
        // 步骤：用 LayerOf 算出层，再 UpperOf 换成上层标识 —— 层归属只在这里推一次。
        // 上游/下游：上面三个 Register*States 调用；下游 HierarchicalStateMachine.RegisterSub。
        private void RegisterSub(EPlayerState sub)
        {
            EPlayerState upper = PlayerStateCatalog.UpperOf(PlayerStateCatalog.LayerOf(sub));
            m_fsm.RegisterSub(upper, sub, new PlayerLayeredState(sub));
        }
    }
}
