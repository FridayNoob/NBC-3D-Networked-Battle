// 这个文件里有：
//   WorldFollowCamera（MonoBehaviour）：World 的第三人称相机 —— 位置跟随角色，旋转由鼠标控制。
//
// 和上一版的区别：相机**不再跟着角色朝向转**。角色转身不影响镜头，镜头只吃鼠标输入。
// 这是负责人 2026-11-13 明确要求的：摄像机旋转独立于角色。

using UnityEngine;

namespace NBC.Game.GameFlow
{
    // 主要功能：跟随角色位置；右键拖动控制水平/俯仰角，滚轮控制距离。
    // 主要数据成员：m_target（跟谁）、m_yaw/m_pitch（鼠标控制的角）、m_distance（距离，米）。
    // 主要外部方法：SetTarget、Unity 的 LateUpdate。
    // 谁在用它：World 场景的 Main Camera。
    // 注意：水平角只由鼠标改，不读角色朝向 —— 否则角色一转镜头就跟着甩，观感很晕。
    public sealed class WorldFollowCamera : MonoBehaviour
    {
        // 跟随目标（玩家根节点）
        [SerializeField]
        private Transform m_target;

        // 初始水平角（度）；之后只由鼠标改
        [SerializeField]
        private float m_yaw;

        // 初始俯仰角（度，-20~80）
        [SerializeField]
        private float m_pitch = 25f;

        // 相机到角色的距离（米，2~15）
        [SerializeField]
        private float m_distance = 6f;

        // 看向角色身上哪个高度（米）
        [SerializeField]
        private float m_lookAtHeight = 1.5f;

        // 鼠标灵敏度（度/像素）
        [SerializeField]
        private float m_lookSensitivity = 3f;

        // 滚轮缩放速度（米/格）
        [SerializeField]
        private float m_zoomSpeed = 4f;

        // 位置平滑速度（越大越紧跟）
        [SerializeField]
        private float m_positionSmooth = 12f;

        // 功能：运行时改跟随目标。
        // 入参：target 新目标
        // 返回：无
        // 步骤：直接赋值
        // 上游：WorldPlayerSetup.Start
        // 下游：LateUpdate
        public void SetTarget(Transform target)
        {
            m_target = target;
        }

        // 功能：启动时就位（避免从场景里摆的位置滑过来）。
        // 入参：无
        // 返回：无
        // 步骤：没目标就按名字找一个；有目标就直接摆到目标位置
        // 上游：Unity
        // 下游：DesiredPosition
        private void Start()
        {
            if (m_target == null)
            {
                GameObject player = GameObject.Find("[Player_Hero]");

                if (player != null)
                {
                    m_target = player.transform;
                }
            }

            if (m_target != null)
            {
                transform.position = DesiredPosition();
                transform.LookAt(m_target.position + Vector3.up * m_lookAtHeight);
            }
        }

        // 功能：每帧更新相机位置与朝向。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 右键拖动改 yaw/pitch（pitch 夹 -20~80 防翻顶）
        //   2) 滚轮改距离（2~15 米）
        //   3) 位置平滑靠近目标位置
        //   4) 看向角色
        // 上游：Unity
        // 下游：Transform.position / rotation
        private void LateUpdate()
        {
            if (m_target == null)
            {
                return;
            }

            if (Input.GetMouseButton(1))
            {
                m_yaw += Input.GetAxis("Mouse X") * m_lookSensitivity;
                m_pitch -= Input.GetAxis("Mouse Y") * m_lookSensitivity;
                m_pitch = Mathf.Clamp(m_pitch, -20f, 80f);
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");

            if (Mathf.Abs(scroll) > 0.001f)
            {
                m_distance = Mathf.Clamp(m_distance - scroll * m_zoomSpeed, 2f, 15f);
            }

            transform.position = Vector3.Lerp(
                transform.position, DesiredPosition(), Mathf.Clamp01(m_positionSmooth * Time.deltaTime));
            transform.LookAt(m_target.position + Vector3.up * m_lookAtHeight);
        }

        // 功能：按当前 yaw/pitch/距离算出相机该在的位置。
        // 入参：无
        // 返回：世界坐标
        // 步骤：绕 Y 转 yaw、再抬头 pitch，把 (0,0,-distance) 转过去加到角色位置
        // 上游：Start / LateUpdate
        // 下游：无
        private Vector3 DesiredPosition()
        {
            Quaternion rotation = Quaternion.Euler(m_pitch, m_yaw, 0f);
            Vector3 offset = rotation * new Vector3(0f, 0f, -m_distance);
            return m_target.position + offset;
        }
    }
}
