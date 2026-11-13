// 这个文件里有：
//   PlayerAnimatorPublisher（组件）：把一帧动画参数写进 Animator，并保证「值没变就不写」

using System.Collections.Generic;
using UnityEngine;

namespace NBC.Game.Player
{
    // 主要功能：实现 IPlayerAnimatorSink —— 状态机算好的 bool/float 参数由它统一写进 Animator；Speed 在这里从旧档位平滑到新档位。
    // 主要数据成员：m_animator（目标动画机）、参数哈希与上次写入值（用于幂等）、m_speedSmoother（Speed 的线性平滑器）。
    // 主要外部方法：Apply 写一帧参数；Reset 在编辑器里自动抓 Animator。
    // 谁在用它：PlayerBrain.Update 每帧调一次；本身挂在玩家根节点上。
    // 注意：控制器没声明的参数只告警一次并跳过，不要每帧 SetFloat —— Unity 会为每个缺失参数刷一条警告， Console 会被淹掉。
    [DisallowMultipleComponent]
    public sealed class PlayerAnimatorPublisher : MonoBehaviour, IPlayerAnimatorSink
    {
        // 浮点参数在数组里的下标
        private const int FloatSpeed = 0;
        private const int FloatDirection = 1;
        private const int FloatVerticalSpeed = 2;
        private const int FloatActionId = 3;
        private const int FloatStackSide = 4;
        private const int FloatPoseIndex = 5;
        private const int FloatCount = 6;

        // 布尔参数在数组里的下标
        private const int BoolCrouch = 0;
        private const int BoolGrounded = 1;
        private const int BoolActionBusy = 2;
        private const int BoolDead = 3;
        private const int BoolCount = 4;

        // Speed 的变化小于这个值就不写 Animator（混合树上看不出来，省掉每帧 SetFloat）
        private const float FloatWriteEpsilon = 0.01f;

        // 六个浮点参数的控制器名字，顺序与上面的下标一致
        private static readonly string[] FloatNames =
        {
            PlayerAnimatorNames.Speed,
            PlayerAnimatorNames.Direction,
            PlayerAnimatorNames.VerticalSpeed,
            PlayerAnimatorNames.ActionId,
            PlayerAnimatorNames.StackSide,
            PlayerAnimatorNames.PoseIndex,
        };

        // 四个布尔参数的控制器名字，顺序与上面的下标一致
        private static readonly string[] BoolNames =
        {
            PlayerAnimatorNames.Crouch,
            PlayerAnimatorNames.Grounded,
            PlayerAnimatorNames.ActionBusy,
            PlayerAnimatorNames.Dead,
        };

        // 目标动画机；为空时 Awake 自动往子物体找
        [SerializeField] private Animator m_animator;

        // 控制器缺参数时是否打一条警告（只打一次）
        [SerializeField] private bool m_logMissingParameters = true;

        // Speed 从旧档位滑到新档位用多少秒（秒）；混合树靠它把「站→跑」变成连续过渡，0 表示不平滑
        [SerializeField] private float m_speedSmoothSeconds = 0.12f;

        // Speed 的平滑器；EnsureReady 里按 m_speedSmoothSeconds 创建
        private PlayerSpeedSmoother m_speedSmoother;

        // 参数哈希
        private int[] m_floatHashes;

        // 上次写入的浮点值（NaN 表示还没写过）
        private float[] m_floatLast;

        // 该浮点参数是否存在于控制器里
        private bool[] m_floatValid;

        // 参数哈希
        private int[] m_boolHashes;

        // 上次写入的布尔值
        private bool[] m_boolLast;

        // 该布尔参数是否存在于控制器里
        private bool[] m_boolValid;

        // 是否已经写过至少一次（用于首次必写）
        private bool[] m_boolWritten;

        // 是否完成了初始化（找 Animator + 查参数）
        private bool m_ready;

        // 功能：在编辑器里加组件时自动抓子物体的 Animator，省得手拖。
        // 入参：无。
        // 返回：无。
        // 步骤：直接 GetComponentInChildren 赋给序列化字段。
        // 上游/下游：Unity 编辑器调用。
        private void Reset()
        {
            m_animator = GetComponentInChildren<Animator>();
        }

        // 功能：场景启动时完成初始化。
        // 入参：无。
        // 返回：无。
        // 步骤：调用 EnsureReady 一次。
        // 上游/下游：Unity 生命周期；下游 EnsureReady。
        private void Awake()
        {
            EnsureReady();
        }

        // 功能：把一帧参数写进 Animator（幂等：没变就不写）。
        // 入参：parameters 是状态机产出的只读快照。
        // 返回：无。
        // 步骤：1) 确保初始化 2) 先把 Speed 平滑一帧（混合树要连续值）3) 其余浮点 4) 四个布尔。
        // 上游/下游：PlayerBrain.Update 调用；下游 WriteFloat / WriteBool。
        public void Apply(in PlayerAnimatorParams parameters)
        {
            EnsureReady();

            if (m_animator == null)
            {
                return;
            }

            float smoothedSpeed = m_speedSmoother == null
                ? parameters.Speed
                : m_speedSmoother.Step(parameters.Speed, Time.deltaTime);

            WriteFloat(FloatSpeed, smoothedSpeed, FloatWriteEpsilon);
            WriteFloat(FloatDirection, parameters.Direction);
            WriteFloat(FloatVerticalSpeed, parameters.VerticalSpeed);
            WriteFloat(FloatActionId, parameters.ActionId);
            WriteFloat(FloatStackSide, parameters.StackSide);
            WriteFloat(FloatPoseIndex, parameters.PoseIndex);

            WriteBool(BoolCrouch, parameters.Crouch);
            WriteBool(BoolGrounded, parameters.Grounded);
            WriteBool(BoolActionBusy, parameters.ActionBusy);
            WriteBool(BoolDead, parameters.Dead);
        }

        // 功能：找 Animator、查参数表、缓存哈希。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 已初始化直接返回 2) 找 Animator，找不到就禁用本组件 3) 关根运动 4) 按参数表建哈希，缺的标 invalid 并告警一次。
        // 上游/下游：Awake / Apply 调用。
        private void EnsureReady()
        {
            if (m_ready)
            {
                return;
            }

            m_ready = true;

            if (m_animator == null)
            {
                m_animator = GetComponentInChildren<Animator>();
            }

            if (m_animator == null)
            {
                Debug.LogError("[PlayerAnimator] 找不到 Animator，动画参数不会生效。", this);
                return;
            }

            m_animator.applyRootMotion = false;

            if (m_speedSmoother == null)
            {
                m_speedSmoother = new PlayerSpeedSmoother(m_speedSmoothSeconds);
            }

            Dictionary<string, AnimatorControllerParameterType> existing = new Dictionary<string, AnimatorControllerParameterType>();
            AnimatorControllerParameter[] parameters = m_animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                existing[parameters[i].name] = parameters[i].type;
            }

            m_floatHashes = new int[FloatCount];
            m_floatLast = new float[FloatCount];
            m_floatValid = new bool[FloatCount];
            m_boolHashes = new int[BoolCount];
            m_boolLast = new bool[BoolCount];
            m_boolValid = new bool[BoolCount];
            m_boolWritten = new bool[BoolCount];

            List<string> missing = new List<string>();

            for (int i = 0; i < FloatCount; i++)
            {
                m_floatHashes[i] = Animator.StringToHash(FloatNames[i]);
                m_floatLast[i] = float.NaN;
                m_floatValid[i] = HasParameter(existing, FloatNames[i], AnimatorControllerParameterType.Float);
                if (!m_floatValid[i])
                {
                    missing.Add(FloatNames[i] + "(Float)");
                }
            }

            for (int i = 0; i < BoolCount; i++)
            {
                m_boolHashes[i] = Animator.StringToHash(BoolNames[i]);
                m_boolValid[i] = HasParameter(existing, BoolNames[i], AnimatorControllerParameterType.Bool);
                if (!m_boolValid[i])
                {
                    missing.Add(BoolNames[i] + "(Bool)");
                }
            }

            if (missing.Count > 0 && m_logMissingParameters)
            {
                Debug.LogWarning("[PlayerAnimator] 控制器缺少参数：" + string.Join("、", missing) + "；对应的动画切换不会生效。", this);
            }
        }

        // 功能：判断控制器里有没有指定名字、指定类型的参数。
        // 入参：existing 参数名到类型的表；name 参数名；type 期望类型。
        // 返回：存在且类型一致返回 true。
        // 步骤：先查表再比类型。
        // 上游/下游：EnsureReady 调用。
        private static bool HasParameter(
            Dictionary<string, AnimatorControllerParameterType> existing,
            string name,
            AnimatorControllerParameterType type)
        {
            AnimatorControllerParameterType actual;
            return existing.TryGetValue(name, out actual) && actual == type;
        }

        // 功能：写一个浮点参数，值没变就跳过。
        // 入参：index 参数下标；value 目标值；epsilon 变化小于它就算没变（0 表示只跳过完全相等）。
        // 返回：无。
        // 步骤：参数缺失直接返回；与上次值差在 epsilon 内也返回；否则 SetFloat 并记下。
        // 上游/下游：Apply 调用；下游 Animator.SetFloat。
        private void WriteFloat(int index, float value, float epsilon = 0f)
        {
            if (!m_floatValid[index])
            {
                return;
            }

            if (!float.IsNaN(m_floatLast[index]))
            {
                float delta = Mathf.Abs(m_floatLast[index] - value);
                if (epsilon <= 0f ? Mathf.Approximately(m_floatLast[index], value) : delta <= epsilon)
                {
                    return;
                }
            }

            m_floatLast[index] = value;
            m_animator.SetFloat(m_floatHashes[index], value);
        }

        // 功能：写一个布尔参数，值没变就跳过。
        // 入参：index 参数下标；value 目标值。
        // 返回：无。
        // 步骤：参数缺失直接返回；写过且值相同也返回；否则 SetBool 并记下。
        // 上游/下游：Apply 调用；下游 Animator.SetBool。
        private void WriteBool(int index, bool value)
        {
            if (!m_boolValid[index])
            {
                return;
            }

            if (m_boolWritten[index] && m_boolLast[index] == value)
            {
                return;
            }

            m_boolWritten[index] = true;
            m_boolLast[index] = value;
            m_animator.SetBool(m_boolHashes[index], value);
        }
    }
}
