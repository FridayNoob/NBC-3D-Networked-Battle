// 这个文件里有：
//   PlayerAnimation（类）：把角色的实际速度喂给 Animator 的 Speed 参数

using UnityEngine;

namespace NBC.Game.Player
{
    // 主要功能：每帧读 PlayerMove 的 CurrentSpeed，写进 Animator 的 Speed 参数，由 Controller 决定播 Idle 还是 Walk。
    // 主要数据成员：m_animator（目标 Animator）、m_damping（SetFloat 平滑时间）、m_move（同物体上的 PlayerMove）。
    // 主要外部方法：无，只有 Unity 的 LateUpdate 回调。
    // 谁在用它：和 PlayerMove 一起挂在角色物体上；状态与过渡全在 Animator Controller 里配。
    [RequireComponent(typeof(PlayerMove))]
    public sealed class PlayerAnimation : MonoBehaviour
    {
        // 与 Controller 里的浮点参数同名（写错不会报错，只会不动，所以下面会校验）
        public const string SpeedParameter = "Speed";

        // 低于这个速度算静止，米/秒（0.1 足够滤掉浮点抖动）
        public const float WalkThreshold = 0.1f;

        // 目标 Animator：留空时自动在本物体及其子物体里找
        [SerializeField] private Animator m_animator = null;

        // SetFloat 的平滑时间（秒）。0 = 不平滑；0.1 让切换不生硬
        [SerializeField] private float m_damping = 0.1f;

        // 速度换算系数：把「米/秒」换成 Controller 期望的 0~1。
        // 默认 0.2 = 5 米/秒 → 1.0（因为 Unity-chan 的 BlendTree 阈值是 0 与 0.8）。
        // 换 Controller 时如果它直接吃米/秒，就把它改成 1。
        [SerializeField] private float m_speedScale = 0.2f;

        // 换算后最大的速度值（Controller 里按 0~1 设计，超过它没意义）
        private const float MaxNormalizedSpeed = 1f;

        // 同物体上的位移组件（速度来源）
        private PlayerMove m_move;

        // 参数名的哈希值（每帧用整数查参数，不拼字符串）
        private int m_speedHash;

        // 入参：无（引擎在开始帧调一次）
        // 返回：无
        // 步骤：
        //   1) 取同物体的 PlayerMove；没装就报错（本类不自己算速度）
        //   2) 找 Animator：没手填就 GetComponentInChildren，找不到就报错并停用自己
        //   3) 强制关掉 Root Motion（位移由 PlayerMove 管，动画再带位移会叠加）
        //   4) 校验 Controller 里确实有 Speed 参数，没有就点名报错
        // 上游：Unity 引擎
        // 下游：Animator.applyRootMotion / Animator.parameters
        private void Awake()
        {
            m_move = GetComponent<PlayerMove>();

            if (m_move == null)
            {
                Debug.LogError("[PlayerAnimation] 同一个物体上没有 PlayerMove，动画拿不到速度。", this);
                enabled = false;
                return;
            }

            if (m_animator == null)
            {
                m_animator = GetComponentInChildren<Animator>();
            }

            if (m_animator == null)
            {
                Debug.LogError("[PlayerAnimation] 没有 Animator（本物体和子物体都没找到）。", this);
                enabled = false;
                return;
            }

            m_animator.applyRootMotion = false;
            m_speedHash = Animator.StringToHash(SpeedParameter);
            ValidateParameter();
        }

        // 入参：无（引擎每帧在 Update 之后调）
        // 返回：无
        // 步骤：
        //   1) 读本帧实测速度（米/秒）
        //   2) 低于阈值就按 0 喂（避免浮点抖动让状态来回切）
        //   3) 乘 m_speedScale 换成 Controller 期望的 0~1，并钳住上限
        //   4) SetFloat 写到 Speed 参数，带 damping 平滑
        // 上游：Unity 引擎（LateUpdate，保证在 PlayerMove.Update 之后）
        // 下游：Animator.SetFloat
        // 注意：不要改成 Update。同一物体上两个 Update 的先后不保证，顺序反了会喂上一帧的速度
        private void LateUpdate()
        {
            float speed = m_move.CurrentSpeed;

            if (speed < WalkThreshold)
            {
                speed = 0f;
            }

            // Controller 的 BlendTree 按 0~1 设计（阈值 0 与 0.8），所以要换算；换直接吃米/秒的 Controller 时把系数设 1
            float normalized = Mathf.Clamp(speed * m_speedScale, 0f, MaxNormalizedSpeed);

            m_animator.SetFloat(m_speedHash, normalized, m_damping, Time.deltaTime);
        }

        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 遍历 Controller 的参数表
        //   2) 找到同名 float 参数就通过
        //   3) 找不到就报错并停用（否则每帧 SetFloat 都是无效调用，且不报错）
        // 上游：Awake
        // 下游：无
        private void ValidateParameter()
        {
            AnimatorControllerParameter[] parameters = m_animator.parameters;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == SpeedParameter &&
                    parameters[i].type == AnimatorControllerParameterType.Float)
                {
                    return;
                }
            }

            Debug.LogError(
                "[PlayerAnimation] Animator Controller 里没有 Float 参数 " + SpeedParameter +
                "（现有参数：" + parameters.Length + " 个）。动画不会切换。", this);

            enabled = false;
        }
    }
}
