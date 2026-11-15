// 这个文件里有：
//   PlayerJumpMotor（MonoBehaviour）：把 PlayerVerticalMotion 算出的高度写到 transform，并负责地面探测与起跳入口

using UnityEngine;

namespace NBC.Game.Player
{
    // 主要功能：驱动玩家的竖直位移 —— 起跳、重力下落、落地判定，并把结果告诉状态机（Grounded / VerticalSpeed / 落地事件）。
    // 主要数据成员：m_motion（纯逻辑积分器）、m_machine（状态机，可空）、m_groundY（最近一次探到的地面高度，米）。
    // 主要外部方法：TryJump 起跳、Step 每帧推进、Bind 绑定状态机、IsAirborne 是否离地。
    // 谁在用它：PlayerBrain（Awake 里补挂并 Bind，Update 里每帧 Step，Space 键调 TryJump）；WorldPlayerSetup 见到它就不再自己贴地。
    // 注意：地面探测跳过玩家自己的碰撞体 —— 手里的剑有 BoxCollider，姿势动作会把它摆到脚下，命中它会把角色顶起来。
    [DisallowMultipleComponent]
    public sealed class PlayerJumpMotor : MonoBehaviour
    {
        // 重力加速度，米/秒²
        [SerializeField] private float m_gravity = PlayerVerticalMotion.DefaultGravity;

        // 起跳高度，米（默认 1.2 米，约 0.99 秒滞空）
        [SerializeField] private float m_jumpHeight = 1.2f;

        // 地面探测的起点高度（相对脚底），米
        [SerializeField] private float m_probeHeight = 5f;

        // 地面探测的最大距离（从起点往下），米
        [SerializeField] private float m_probeDistance = 20f;

        // 哪些层算地面；默认全部层，靠「跳过自己」排除武器
        [SerializeField] private LayerMask m_groundMask = ~0;

        // 是否打印起跳/落地日志（验收时开，平时关）
        [SerializeField] private bool m_logDiagnostics = true;

        // 是否用脚底把角色贴回地面（落地与站立时修正动画带来的下沉），默认开
        [SerializeField] private bool m_alignFeetToGround = true;

        // 单帧修正的最大幅度，米；超过它多半是地面探错，宁可不修，免得把角色弹飞
        [SerializeField] private float m_alignMaxOffset = 2.0f;

        // 修正死区，米；比它小的误差不动，避免站立时上下抖
        [SerializeField] private float m_alignDeadZone = 0.005f;

        // 复用的射线命中缓冲，避免每帧分配（16 个够用：地面 + 少量装饰）
        private readonly RaycastHit[] m_hits = new RaycastHit[16];

        // 竖直运动积分器（纯逻辑）
        private PlayerVerticalMotion m_motion;

        // 状态机引用，可空（没 Bind 时只做位移，不回报状态）
        private PlayerStateMachine m_machine;

        // 最近一次探到的地面高度，米；没探到就一直沿用上一次
        private float m_groundY;

        // 是否已经拿到过地面高度
        private bool m_hasGround;

        // 子物体上的 Animator，用来读两只脚骨骼的高度（可空）
        private Animator m_animator;

        // 脚骨骼到鞋底的距离，米（校准时量一次，之后按它把鞋底贴到地面）
        private float m_soleOffset;

        // 是否已经量过 m_soleOffset
        private bool m_soleOffsetReady;

        // 下一次允许打贴地日志的时间（秒），避免每帧刷屏
        private float m_nextAlignLogTime;

        // 是否在空中（离地且不是在落地缓冲）
        public bool IsAirborne
        {
            get { return m_motion != null && !m_motion.Grounded; }
        }

        // 功能：绑状态机，让电机能把 Grounded / VerticalSpeed / 落地事件写回去。
        // 入参：machine 玩家状态机，可为 null（测试或纯表现场景）。
        // 返回：无。
        // 步骤：保存引用并确保积分器已创建。
        // 上游/下游：PlayerBrain.Awake 调用；内部调 EnsureMotion。
        public void Bind(PlayerStateMachine machine)
        {
            m_machine = machine;
            EnsureMotion();
        }

        // 功能：请求起跳。
        // 入参：无。
        // 返回：true 表示真的跳了；false 表示在空中、已死、有动作在播或正在落地缓冲。
        // 步骤：1) 先让积分器算初速度 2) 状态机拒绝就回滚到地面 3) 都通过才算成功。
        // 上游/下游：PlayerBrain 的 Space 键调用；下游 PlayerVerticalMotion.Jump、PlayerStateMachine.NotifyJump。
        public bool TryJump()
        {
            EnsureMotion();
            if (!m_motion.Jump(m_jumpHeight))
            {
                return false;
            }

            if (m_machine != null && !m_machine.NotifyJump())
            {
                m_motion.ResetToGround();
                return false;
            }

            if (m_logDiagnostics)
            {
                Debug.Log("[PlayerJumpMotor] 起跳，目标高度 " + m_jumpHeight.ToString("0.00") + " 米");
            }

            return true;
        }

        // 功能：推进一帧的竖直运动并写到 transform。
        // 入参：deltaSeconds 帧时长，秒。
        // 返回：无。
        // 步骤：1) 先探地面（拿到脚下地面高度）2) 积分高度 3) 把 y 设成地面高度加离地高度 4) 回报状态机 5) 落地打日志。
        // 上游/下游：PlayerBrain.Update 每帧调用；下游 PlayerStateMachine.SetVertical / NotifyLand。
        public void Step(float deltaSeconds)
        {
            EnsureMotion();
            ProbeGround();
            m_motion.Step(deltaSeconds);

            Vector3 position = transform.position;
            position.y = m_groundY + m_motion.Height;
            transform.position = position;

            if (m_machine != null)
            {
                m_machine.SetVertical(m_motion.Grounded, m_motion.VerticalSpeed);
                if (m_motion.JustLanded)
                {
                    m_machine.NotifyLand();
                }
            }

            if (m_logDiagnostics && m_motion.JustLanded)
            {
                float foot = 0f;
                float hip = 0f;
                Animator animator = GetComponentInChildren<Animator>();
                if (animator != null && animator.isInitialized)
                {
                    Transform leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                    Transform rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                    Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                    if (leftFoot != null && rightFoot != null)
                    {
                        foot = Mathf.Min(leftFoot.position.y, rightFoot.position.y);
                    }

                    if (hips != null)
                    {
                        hip = hips.position.y;
                    }
                }

                Debug.Log("[PlayerJumpMotor] 落地，地面高度 " + m_groundY.ToString("0.000") + " 米，脚骨 " + foot.ToString("0.000")
                    + " 米，胯 " + hip.ToString("0.000") + " 米，鞋底偏移 " + m_soleOffset.ToString("0.000") + " 米");
            }
        }

        // 功能：往上找「不是自己」的最近地面，记下它的高度。
        // 入参：无。
        // 返回：无；没命中时保留上一次的地面高度。
        // 步骤：1) 从脚底上方 m_probeHeight 处向下打一条射线 2) 跳过自己与子物体（手里的剑） 3) 取最近命中的点。
        // 上游/下游：Step 每帧调用；不依赖 WorldPlayerSetup 的贴地逻辑。
        private void ProbeGround()
        {
            Vector3 origin = transform.position + Vector3.up * m_probeHeight;
            int count = Physics.RaycastNonAlloc(
                origin,
                Vector3.down,
                m_hits,
                m_probeHeight + m_probeDistance,
                m_groundMask,
                QueryTriggerInteraction.Ignore);

            float nearest = float.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = m_hits[i];
                Transform hitTransform = hit.transform;
                if (hitTransform == null)
                {
                    continue;
                }

                if (hitTransform == transform || hitTransform.IsChildOf(transform))
                {
                    continue;
                }

                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    m_groundY = hit.point.y;
                    found = true;
                }
            }

            if (found)
            {
                m_hasGround = true;
                return;
            }

            if (!m_hasGround)
            {
                m_groundY = transform.position.y;
                m_hasGround = true;
            }
        }

        // 功能：确保积分器存在。
        // 入参：无。
        // 返回：无。
        // 步骤：为空就按 Inspector 的重力 new 一个。
        // 上游/下游：Awake / Bind / TryJump / Step 调用。
        private void EnsureMotion()
        {
            if (m_motion == null)
            {
                m_motion = new PlayerVerticalMotion(m_gravity);
            }
        }

        // 功能：每帧在动画更新之后，把踩在地上的姿势“按”回地面。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 只在贴地（含落地缓冲）时修 2) 第一次先量鞋底偏移 3) 之后按脚底与地面的差修正 y。
        // 上游/下游：Unity 生命周期调用（在 Animator 之后）；下游 AlignFeetToGround、TryCalibrateSole。
        // 注意：必须在 LateUpdate 做 —— Update 里动画还没摆好，量到的是上一帧的姿势。
        private void LateUpdate()
        {
            if (!m_alignFeetToGround || m_motion == null || !m_motion.Grounded)
            {
                return;
            }

            if (!m_soleOffsetReady)
            {
                TryCalibrateSole();
                return;
            }

            AlignFeetToGround();
        }

        // 功能：量一次「脚骨骼比鞋底高多少」，供之后对齐使用。
        // 入参：无。
        // 返回：无；Animator 还没初始化或没有脚骨时静默返回，下一帧再试。
        // 步骤：1) 取左右脚骨骼 2) 取所有渲染器包围盒的最低点 3) 两者相减存起来。
        // 上游/下游：LateUpdate 调用；下游 AlignFeetToGround 使用 m_soleOffset。
        private void TryCalibrateSole()
        {
            if (m_animator == null)
            {
                m_animator = GetComponentInChildren<Animator>();
            }

            if (m_animator == null || !m_animator.isInitialized)
            {
                return;
            }

            Transform leftFoot = m_animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform rightFoot = m_animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (leftFoot == null || rightFoot == null)
            {
                return;
            }

            float lowest = float.MaxValue;
            Renderer[] renderers = GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                float minY = renderers[i].bounds.min.y;
                if (minY < lowest)
                {
                    lowest = minY;
                }
            }

            if (lowest == float.MaxValue)
            {
                return;
            }

            float footY = Mathf.Min(leftFoot.position.y, rightFoot.position.y);
            m_soleOffset = footY - lowest;
            m_soleOffsetReady = true;
        }

        // 功能：把当前姿势的鞋底对齐到 m_groundY。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 算两只脚骨骼里较低的那只 2) 减去鞋底偏移得到鞋底高度 3) 差值在死区内或过大就跳过 4) 否则平移 y。
        // 上游/下游：LateUpdate 调用；读 m_groundY（ProbeGround 每帧更新）。
        // 注意：修复的是「动画把腿伸到地面以下」这类下沉（例如 Mixamo 落地动作的深蹲），不动地面高度本身。
        private void AlignFeetToGround()
        {
            Transform leftFoot = m_animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform rightFoot = m_animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (leftFoot == null || rightFoot == null)
            {
                return;
            }

            float footY = Mathf.Min(leftFoot.position.y, rightFoot.position.y) - m_soleOffset;
            float delta = m_groundY - footY;
            if (Mathf.Abs(delta) <= m_alignDeadZone || Mathf.Abs(delta) > m_alignMaxOffset)
            {
                return;
            }

            if (m_logDiagnostics && Time.unscaledTime >= m_nextAlignLogTime)
            {
                m_nextAlignLogTime = Time.unscaledTime + 0.3f;
                Debug.Log("[PlayerJumpMotor] 贴地修正 " + delta.ToString("0.000") + " 米（地面 " + m_groundY.ToString("0.000")
                    + "，脚底 " + footY.ToString("0.000") + "，鞋底偏移 " + m_soleOffset.ToString("0.000") + "）");
            }

            Vector3 position = transform.position;
            position.y += delta;
            transform.position = position;
        }

        // 功能：初始化地面高度，避免第一帧把角色拽到 0。
        // 入参：无。
        // 返回：无。
        // 步骤：记下当前 y、标成已有地面，再建积分器。
        // 上游/下游：Unity 生命周期调用。
        private void Awake()
        {
            EnsureMotion();
            m_groundY = transform.position.y;
            m_hasGround = true;
        }
    }
}
