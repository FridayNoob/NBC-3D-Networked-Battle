// 这个文件里有：
//   PlayerAnimation（类）：把角色的前后 / 左右速度喂给 Animator 的 Speed 与 Direction 参数

using UnityEngine;

namespace NBC.Game.Player
{
    // 主要功能：每帧读 PlayerMove 的 ForwardSpeed / SideSpeed，各自除以移动速度归一化成 -1..1，
    //           写进 Animator 的 Speed（带符号，负=后退）与 Direction（-1 左 / +1 右）。
    // 主要数据成员：m_animator（目标 Animator）、m_damping（平滑时间）、m_move（同物体上的 PlayerMove）。
    // 主要外部方法：无，只有 Unity 的 LateUpdate 回调。
    // 谁在用它：和 PlayerMove 一起挂在角色物体上；状态与过渡全在 Animator Controller 里配。
    [RequireComponent(typeof(PlayerMove))]
    public sealed class PlayerAnimation : MonoBehaviour
    {
        // 前后参数：正值 = 前进，负值 = 后退（Controller 里 Speed < -0.1 会进 WalkBack）
        public const string SpeedParameter = "Speed";

        // 侧移参数：Controller 里是 1D 混合，-1 左移 / 0 前进 / +1 右移
        public const string DirectionParameter = "Direction";

        // 低于这个速度算静止，米/秒（0.1 足够滤掉浮点抖动）
        public const float WalkThreshold = 0.1f;

        // 目标 Animator：留空时自动在本物体及其子物体里找
        [SerializeField] private Animator m_animator = null;

        // SetFloat 的平滑时间（秒）。0 = 不平滑；0.1 让切换不生硬
        [SerializeField] private float m_damping = 0.1f;

        // 同物体上的位移组件（速度来源）
        private PlayerMove m_move;

        // 两个参数名的哈希值（每帧用整数查参数，不拼字符串）
        private int m_speedHash;
        private int m_directionHash;

        // 入参：无（引擎在开始帧调一次）
        // 返回：无
        // 步骤：
        //   1) 取同物体的 PlayerMove；没装就报错（本类不自己算速度）
        //   2) 找 Animator：没手填就 GetComponentInChildren，找不到就报错并停用自己
        //   3) 强制关掉 Root Motion（位移由 PlayerMove 管，动画再带位移会叠加）
        //   4) 校验 Controller 里确实有 Speed 与 Direction 两个 Float 参数，缺一个就点名报错
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
            m_directionHash = Animator.StringToHash(DirectionParameter);

            if (!ValidateParameter(SpeedParameter) || !ValidateParameter(DirectionParameter))
            {
                enabled = false;
            }
        }

        // 入参：无（引擎每帧在 Update 之后调）
        // 返回：无
        // 步骤：
        //   1) 取本帧的前后 / 左右实测速度（米/秒，带符号）
        //   2) 都除以配置的移动速度 ⇒ 全速时正好 ±1（与 Controller 的阈值同量纲）
        //   3) 低于阈值的分量按 0 喂（避免浮点抖动让状态来回切）
        //   4) 分别写进 Speed 与 Direction，带 damping 平滑
        // 上游：Unity 引擎（LateUpdate，保证在 PlayerMove.Update 之后）
        // 下游：Animator.SetFloat
        // 注意：不要改成 Update。同一物体上两个 Update 的先后不保证，顺序反了会喂上一帧的速度
        private void LateUpdate()
        {
            float moveSpeed = m_move.MoveSpeed;
            float divisor = moveSpeed > 0f ? moveSpeed : 1f;

            float forward = m_move.ForwardSpeed / divisor;
            float side = m_move.SideSpeed / divisor;

            if (Mathf.Abs(forward) < WalkThreshold)
            {
                forward = 0f;
            }

            if (Mathf.Abs(side) < WalkThreshold)
            {
                side = 0f;
            }

            m_animator.SetFloat(m_speedHash, Mathf.Clamp(forward, -1f, 1f), m_damping, Time.deltaTime);
            m_animator.SetFloat(m_directionHash, Mathf.Clamp(side, -1f, 1f), m_damping, Time.deltaTime);
        }

        // 入参：parameterName 要检查的参数名
        // 返回：true = 存在且是 Float；false = 不存在或类型不对
        // 步骤：
        //   1) 遍历 Controller 的参数表
        //   2) 找到同名且类型为 Float 的就通过
        //   3) 找不到就报错点名（否则每帧 SetFloat 都是无效调用，而且不报错）
        // 上游：Awake
        // 下游：无
        private bool ValidateParameter(string parameterName)
        {
            AnimatorControllerParameter[] parameters = m_animator.parameters;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == parameterName &&
                    parameters[i].type == AnimatorControllerParameterType.Float)
                {
                    return true;
                }
            }

            Debug.LogError(
                "[PlayerAnimation] Animator Controller 里没有 Float 参数 " + parameterName +
                "（现有参数：" + parameters.Length + " 个）。动画不会切换。", this);

            return false;
        }
    }
}
