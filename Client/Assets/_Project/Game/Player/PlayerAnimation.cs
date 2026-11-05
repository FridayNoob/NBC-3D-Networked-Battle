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

        // 攻击动画的触发参数名（Sword_Controller 里的 Trigger）；R014 配的控制器现成有 ATTACK 1/2/3
        public const string AttackTrigger = "ATTACK 1";

        // 受击动画的两个触发参数（Sword_Controller 现成有）；两个交替播，避免看起来像卡住
        public const string HitLeftTrigger = "HIT L";

        // 受击动画（右侧）的触发参数名
        public const string HitRightTrigger = "HIT R";

        // 目标 Animator：留空时自动在本物体及其子物体里找
        [SerializeField] private Animator m_animator = null;

        // 攻击 Trigger 是否已确认可用（确认过就不再查参数）
        private bool m_attackTriggerReady;

        // 受击 Trigger 是否已确认可用
        private bool m_hitTriggerReady;

        // 是否已经为「缺受击 Trigger」告过警
        private bool m_hitTriggerWarned;

        // 下一次受击播左边还是右边（交替）
        private bool m_nextHitIsLeft = true;

        // 是否已经为「缺攻击 Trigger」告过警（只报一次）
        private bool m_attackTriggerWarned;

        // SetFloat 的平滑时间（秒）。0 = 不平滑；0.1 让切换不生硬
        [SerializeField] private float m_damping = 0.1f;

        // 同物体上的位移组件（速度来源）
        private PlayerMove m_move;

        // 两个参数名的哈希值（每帧用整数查参数，不拼字符串）
        private int m_speedHash;
        private int m_directionHash;

        // 功能：播一次攻击动画（触发一次 Trigger），供攻击输入调用。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没有动画器或没有该 Trigger 参数 ⇒ 只告警一次，直接返回（不抛、不刷屏）
        //   2) 有就 SetTrigger
        // 上游：RuntimeNetDriver（玩家按下攻击键时）
        // 下游：Animator.SetTrigger
        // 注意：Trigger 是「一次性」的，连按会重入动画；冷却由服务端裁决，客户端不做节流
        public void PlayAttack()
        {
            if (m_animator == null)
            {
                return;
            }

            if (!m_attackTriggerReady)
            {
                if (m_attackTriggerWarned)
                {
                    return;
                }

                m_attackTriggerWarned = true;

                if (!ValidateParameter(AttackTrigger, AnimatorControllerParameterType.Trigger))
                {
                    Debug.LogWarning("[PlayerAnimation] controller 里没有 Trigger 参数 " + AttackTrigger +
                                     "，攻击时不会播动作（战斗逻辑仍由服务端裁决）。", this);
                    return;
                }

                m_attackTriggerReady = true;
            }

            m_animator.SetTrigger(AttackTrigger);
        }

        // 功能：播一次受击动画（左/右交替）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没有动画器 ⇒ 返回
        //   2) 第一次调用时确认两个受击 Trigger 是否存在，缺了只告警一次并放弃
        //   3) 按交替标记 SetTrigger，然后翻转标记
        // 上游：RuntimeNetDriver（玩家掉血时）
        // 下游：Animator.SetTrigger
        // 注意：受击是**表现**，玩家的真实血量以服务端快照为准
        public void PlayHit()
        {
            if (m_animator == null)
            {
                return;
            }

            if (!m_hitTriggerReady)
            {
                if (m_hitTriggerWarned)
                {
                    return;
                }

                m_hitTriggerWarned = true;

                if (!ValidateParameter(HitLeftTrigger, AnimatorControllerParameterType.Trigger)
                    || !ValidateParameter(HitRightTrigger, AnimatorControllerParameterType.Trigger))
                {
                    Debug.LogWarning("[PlayerAnimation] controller 里缺少受击 Trigger（" + HitLeftTrigger + " / " +
                                     HitRightTrigger + "），玩家受击时不会播动作。", this);
                    return;
                }

                m_hitTriggerReady = true;
            }

            m_animator.SetTrigger(m_nextHitIsLeft ? HitLeftTrigger : HitRightTrigger);
            m_nextHitIsLeft = !m_nextHitIsLeft;
        }

        // 功能：接好绑定并做启动校验（必须有 PlayerMove、Animator、两个 Float 参数）。
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

            if (!ValidateParameter(SpeedParameter, AnimatorControllerParameterType.Float)
                || !ValidateParameter(DirectionParameter, AnimatorControllerParameterType.Float))
            {
                Debug.LogError(
                    "[PlayerAnimation] Animator Controller 里缺少 Float 参数 " + SpeedParameter +
                    " 或 " + DirectionParameter + "（现有参数：" + m_animator.parameters.Length +
                    " 个）。动画不会切换。", this);
                enabled = false;
            }
        }

        // 功能：把前后 / 左右分量归一化后写进 Animator 的 Speed 与 Direction。
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

        // 功能：检查 Controller 里是否存在指定的 Float 参数，缺了就点名报错。
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
            return ValidateParameter(parameterName, AnimatorControllerParameterType.Float);
        }

        // 功能：检查 Controller 里是否存在指定名字与类型的参数。
        // 入参：parameterName 参数名；expectedType 期望类型
        // 返回：true = 存在且类型对；false = 不存在或类型不对（已打过一次 Error）
        // 步骤：
        //   1) 遍历 Controller 的参数表
        //   2) 同名且类型一致就通过
        //   3) 否则保持静默返回 false —— 是否报错由调用方决定（攻击 Trigger 只告警、移动 Float 才报错）
        // 上游：Awake（Float）、PlayAttack（Trigger）
        // 下游：无
        private bool ValidateParameter(string parameterName, AnimatorControllerParameterType expectedType)
        {
            AnimatorControllerParameter[] parameters = m_animator.parameters;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == parameterName && parameters[i].type == expectedType)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
