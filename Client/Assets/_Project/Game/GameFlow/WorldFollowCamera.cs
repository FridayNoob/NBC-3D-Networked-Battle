// 这个文件里有：
//   WorldFollowCamera（MonoBehaviour）：World 的第三人称跟随相机 —— 永远待在角色背后，可滚轮缩放。
//
// 和 Battle 用的 FollowCamera 的区别：那个是"世界固定偏移"（永远在世界 -Z 后方），
// 适合固定视角的副本；开放世界要的是"角色转身，镜头绕到他背后"。

using UnityEngine;

namespace NBC.Game.GameFlow
{
    // 主要功能：跟随角色并按他的朝向绕到背后，滚轮调距离。
    // 主要数据成员：m_target（跟谁）、m_distance（距离，米）、m_height（高度，米）、m_yawSmooth（转向平滑）。
    // 主要外部方法：SetTarget、Unity 的 LateUpdate。
    // 谁在用它：World 场景的 Main Camera。
    // 注意：位置用 Lerp 平滑、朝向用 LerpAngle 平滑 —— 两者都平滑才不会"转半圈"。
    public sealed class WorldFollowCamera : MonoBehaviour
    {
        // 跟随目标（玩家根节点）
        [SerializeField]
        private Transform m_target;

        // 相机到角色的水平距离（米，2~15）
        [SerializeField]
        private float m_distance = 6f;

        // 相机相对角色的高度（米，1~10）
        [SerializeField]
        private float m_height = 3.5f;

        // 看向角色身上哪个高度（米，1~2）
        [SerializeField]
        private float m_lookAtHeight = 1.5f;

        // 朝向平滑速度（越大越紧跟，6~15）
        [SerializeField]
        private float m_yawSmooth = 8f;

        // 位置平滑速度（越大越紧跟，6~15）
        [SerializeField]
        private float m_positionSmooth = 10f;

        // 滚轮缩放速度（米/格）
        [SerializeField]
        private float m_zoomSpeed = 4f;

        // 当前绕到背后的角度（度）；单独存是为了平滑，不直接读角色朝向
        private float m_yaw;

        // 功能：运行时改跟随目标。
        // 入参：target 新目标
        // 返回：无
        // 步骤：直接赋值并把 yaw 对齐到目标当前朝向（避免第一帧转半圈）
        // 上游：WorldPlayerSetup.Start
        // 下游：LateUpdate
        public void SetTarget(Transform target)
        {
            m_target = target;

            if (m_target != null)
            {
                m_yaw = m_target.eulerAngles.y;
            }
        }

        // 功能：启动时若没配目标就自己找一个玩家。
        // 入参：无
        // 返回：无
        // 步骤：按名字找 [Player_Hero]
        // 上游：Unity
        // 下游：SetTarget
        private void Start()
        {
            if (m_target == null)
            {
                GameObject player = GameObject.Find("[Player_Hero]");

                if (player != null)
                {
                    SetTarget(player.transform);
                }
            }

            // 第一帧就位，避免从场景里摆的位置滑过来
            if (m_target != null)
            {
                transform.position = DesiredPosition();
                transform.LookAt(m_target.position + Vector3.up * m_lookAtHeight);
            }
        }

        // 功能：每帧跟到角色背后。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 滚轮改距离
        //   2) 目标朝向 → LerpAngle 平滑成当前 yaw
        //   3) 按 yaw/距离/高度算目标位置 → Lerp 平滑过去
        //   4) 看向角色
        // 上游：Unity
        // 下游：Transform.position / rotation
        private void LateUpdate()
        {
            if (m_target == null)
            {
                return;
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");

            if (Mathf.Abs(scroll) > 0.001f)
            {
                m_distance = Mathf.Clamp(m_distance - scroll * m_zoomSpeed, 2f, 15f);
            }

            m_yaw = Mathf.LerpAngle(m_yaw, m_target.eulerAngles.y, Mathf.Clamp01(m_yawSmooth * Time.deltaTime));

            Vector3 desired = DesiredPosition();
            transform.position = Vector3.Lerp(transform.position, desired, Mathf.Clamp01(m_positionSmooth * Time.deltaTime));
            transform.LookAt(m_target.position + Vector3.up * m_lookAtHeight);
        }

        // 功能：算出"背后偏上"的目标位置。
        // 入参：无
        // 返回：世界坐标
        // 步骤：按当前 yaw 把 (0, height, -distance) 转过去，再加角色位置
        // 上游：Start / LateUpdate
        // 下游：无
        private Vector3 DesiredPosition()
        {
            Quaternion rotation = Quaternion.Euler(0f, m_yaw, 0f);
            Vector3 offset = rotation * new Vector3(0f, m_height, -m_distance);
            return m_target.position + offset;
        }
    }
}
