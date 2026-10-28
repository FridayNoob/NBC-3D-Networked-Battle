// 这个文件里有：
//   PlayerMove（类）：把 InputManager 产出的输入命令变成角色的移动与转身

using NBC.Framework.Input;
using UnityEngine;

namespace NBC.Game.Player
{
    // 主要功能：每帧读 InputManager 的当前命令，把量化轴还原成方向，转身并位移；并把本帧实际速度暴露给动画。
    // 主要数据成员：m_moveSpeed（米/秒）、m_turnSpeed（度/秒）、m_currentSpeed（本帧实测速度，米/秒）。
    // 主要外部方法：CurrentSpeed（只读属性，给 PlayerAnimation 用）。
    // 谁在用它：挂在角色物体上；输入取自 InputManager.Instance.Current，键位在 InputMapping 里可改。
    public sealed class PlayerMove : MonoBehaviour
    {
        // 每秒走多少米。场景里 1 单位 = 1 米，5 就是每秒 5 米
        [SerializeField] private float m_moveSpeed = 5f;

        // 转身速度，度/秒。720 约等于 0.2 秒转半圈，手感不拖
        [SerializeField] private float m_turnSpeed = 720f;

        // 本帧实测速度（米/秒）：位移长度 / deltaTime；没动就是 0
        private float m_currentSpeed;

        // 入参：无
        // 返回：本帧实测速度，米/秒（0 = 这一帧没动）
        // 上游：PlayerAnimation.LateUpdate
        // 下游：无
        public float CurrentSpeed
        {
            get { return m_currentSpeed; }
        }

        // 入参：无（引擎每帧调用）
        // 返回：无
        // 步骤：
        //   1) 取 InputManager 单例；播放模式下它已经把 LegacyInputSource 装好，并每帧自己 Tick 一次
        //   2) 读 Current 的 MoveX / MoveY（已是 -1000..1000 的量化轴），除以 MoveScale 还原成 -1..1
        //   3) 方向是零就返回（本帧速度为 0）；不返回会把朝向强行转到默认方向
        //   4) 归一化；斜着走（W+D）不归一化会比直走快 1.41 倍
        //   5) 由方向求朝向，按 m_turnSpeed 限速转过去
        //   6) 位移 = 方向 乘 速度 乘 deltaTime
        //   7) 用「这一帧实际移动了多远 / deltaTime」算出 CurrentSpeed（给动画用）
        // 上游：Unity 引擎每帧调用
        // 下游：InputManager.Current、Transform.rotation、Transform.position
        // 注意：这里不要再调 InputManager.Tick。它每帧已经自己 Tick 过一次，再调一次会打乱「刚按下/刚松开」的边沿判定
        private void Update()
        {
            // 本帧没动就是 0（下面提前 return 的分支也走这里，所以先清掉）
            m_currentSpeed = 0f;

            InputManager input = InputManager.Instance;

            if (input == null || !input.IsEnabled)
            {
                return;
            }

            InputCommand command = input.Current;

            float x = (float)command.MoveX / InputCommand.MoveScale;
            float z = (float)command.MoveY / InputCommand.MoveScale;

            Vector3 direction = new Vector3(x, 0f, z);

            if (direction.sqrMagnitude < 0.0001f)
            {
                return;
            }

            direction.Normalize();

            Quaternion target = Quaternion.LookRotation(direction, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, target, m_turnSpeed * Time.deltaTime);

            Vector3 before = transform.position;
            transform.position += direction * (m_moveSpeed * Time.deltaTime);

            // 实测位移（不是「我想要走多快」）：被挡住时它自然变成 0，动画就会回站姿
            if (Time.deltaTime > 0f)
            {
                m_currentSpeed = (transform.position - before).magnitude / Time.deltaTime;
            }
        }
    }
}
