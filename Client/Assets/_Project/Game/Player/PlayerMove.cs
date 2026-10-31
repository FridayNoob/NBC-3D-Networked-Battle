// 这个文件里有：
//   PlayerMove（类）：四向移动（W/A/S/D 都是转身朝该方向再前进）；不斜走，朝向只由方向键决定

using NBC.Framework.Input;
using UnityEngine;

namespace NBC.Game.Player
{
    // 主要功能：每帧读输入，取主导轴得到 W/A/S/D 四种之一，统一「转身朝该方向 + 沿该方向前进」。
    // 主要数据成员：m_moveSpeed（米/秒）、m_turnSpeed（度/秒）、m_forwardSpeed / m_sideSpeed（给动画的分量）。
    // 主要外部方法：ForwardSpeed / SideSpeed / MoveSpeed / CurrentSpeed（只读属性）。
    // 谁在用它：挂在角色物体上；输入取自 InputManager.Instance.Current，键位在 InputMapping 里可改。
    public sealed class PlayerMove : MonoBehaviour
    {
        // 每秒走多少米。场景里 1 单位 = 1 米，5 就是每秒 5 米
        [SerializeField] private float m_moveSpeed = 5f;

        // 转身速度，度/秒。720 约等于 0.125 秒转 90 度
        [SerializeField] private float m_turnSpeed = 720f;

        // 前后分量，米/秒：W 为正、S 为负
        private float m_forwardSpeed;

        // 左右分量，米/秒：转身后基本为 0（留着给以后侧移用）
        private float m_sideSpeed;

        // 合速度，米/秒（≥ 0），给 HUD 之类用
        private float m_currentSpeed;

        // 功能：读当前的前后速度分量（米/秒，W 正、S 负）。
        // 入参：无
        // 返回：前后速度，米/秒（W 正、S 负）
        // 上游：PlayerAnimation
        // 下游：无
        public float ForwardSpeed
        {
            get { return m_forwardSpeed; }
        }

        // 功能：读当前的左右速度分量（米/秒，D 正、A 负）。
        // 入参：无
        // 返回：左右速度，米/秒（转身时会有短暂非零）
        // 上游：PlayerAnimation
        // 下游：无
        public float SideSpeed
        {
            get { return m_sideSpeed; }
        }

        // 功能：读配置的移动速度，给动画做归一化的分母。
        // 入参：无
        // 返回：配置的移动速度，米/秒（给动画做归一化的分母）
        // 上游：PlayerAnimation
        // 下游：无
        public float MoveSpeed
        {
            get { return m_moveSpeed; }
        }

        // 功能：读本帧合速度（米/秒），给 HUD 与调试用。
        // 入参：无
        // 返回：合速度，米/秒（≥ 0）
        // 上游：HUD / 调试
        // 下游：无
        public float CurrentSpeed
        {
            get { return m_currentSpeed; }
        }

        // 功能：每帧把输入压成四向之一，转身朝该方向并位移，同时算出前后 / 左右分量。
        // 入参：无（引擎每帧调用）
        // 返回：无
        // 步骤：
        //   1) 三个速度先清 0；提前 return 的分支不会残留上一帧的值
        //   2) 取 InputManager；把量化轴还原成 -1..1（x 左右、z 前后）
        //   3) 取主导轴：|z| >= |x| 用前后，否则用左右 ⇒ 只有四种方向，不斜走
        //   4) 按四种情况决定「朝向」与「位移方向」：
        //        W（z>0）  ：朝向 = 世界前方，沿该方向前进
        //        A（x<0）  ：朝向 = 世界左方，转身后沿该方向前进
        //        D（x>0）  ：朝向 = 世界右方，转身后沿该方向前进
        //        S（z<0）  ：朝向 = 世界后方，转身后沿该方向前进
        //   5) 转身用 RotateTowards 限速（0.125 秒转 90 度），位移用「目标方向」而不是当前朝向，避免转弯时走弧线
        //   6) 位移后按 forward / right 点乘，拆出前后 / 左右分量
        // 上游：Unity 引擎每帧调用
        // 下游：InputManager.Current、Transform.rotation、Transform.position
        // 注意：四向一律会转身（S 也转身，2026-10-31 由负责人改；R012 时曾是「S 不转身倒退」）
        private void Update()
        {
            m_forwardSpeed = 0f;
            m_sideSpeed = 0f;
            m_currentSpeed = 0f;

            InputManager input = InputManager.Instance;

            if (input == null || !input.IsEnabled)
            {
                return;
            }

            InputCommand command = input.Current;

            float x = (float)command.MoveX / InputCommand.MoveScale;
            float z = (float)command.MoveY / InputCommand.MoveScale;

            if (Mathf.Abs(x) < 0.0001f && Mathf.Abs(z) < 0.0001f)
            {
                return;
            }

            bool forwardWins = Mathf.Abs(z) >= Mathf.Abs(x);

            // 目标是世界坐标的四个基本方向之一（S 就是 (0,0,-1)，与 A/D 同样对待）
            Vector3 targetDirection = forwardWins
                ? new Vector3(0f, 0f, Mathf.Sign(z))
                : new Vector3(Mathf.Sign(x), 0f, 0f);

            // 四向统一：先转身朝目标方向（限速），位移仍用目标方向，避免边走边转出弧线
            Quaternion targetRotation = Quaternion.LookRotation(targetDirection, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, targetRotation, m_turnSpeed * Time.deltaTime);

            Vector3 moveDirection = targetDirection;

            Vector3 before = transform.position;
            transform.position += moveDirection * (m_moveSpeed * Time.deltaTime);

            float dt = Time.deltaTime;
            if (dt <= 0f)
            {
                return;
            }

            Vector3 moved = transform.position - before;

            // 实测位移拆成前后 / 左右：以后加碰撞，被挡住时这两个值会自然变小
            m_forwardSpeed = Vector3.Dot(moved, transform.forward) / dt;
            m_sideSpeed = Vector3.Dot(moved, transform.right) / dt;
            m_currentSpeed = moved.magnitude / dt;
        }

        // 功能：被禁用时把三个速度清零，避免下游继续读到「停之前那一帧」的旧速度。
        // 入参：无
        // 返回：无
        // 步骤：直接归零（这三个字段只在 Update 里写，禁用后不会再被刷新）
        // 上游：Unity 引擎（组件被禁用时调用）、PlayerBrain 的状态闸门
        // 下游：PlayerAnimation 读这三个值
        // 注意：不清零的话，状态机禁用本组件后动画会一直保持「在走」——因为 PlayerAnimation 读到的是旧值
        private void OnDisable()
        {
            m_forwardSpeed = 0f;
            m_sideSpeed = 0f;
            m_currentSpeed = 0f;
        }
    }
}
