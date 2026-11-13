// 这个文件里有：
//   WorldOrbitCamera（MonoBehaviour）：World 场景的观察相机 —— 右键拖动转视角、滚轮缩放、WASD 平移。
//
// 它只是"看环境"用的工具相机；玩家跟随相机等角色系统落地后，它会退成调试相机。

using UnityEngine;

namespace NBC.Game.GameFlow
{
    // 主要功能：绕着一个焦点转的轨道相机，方便在没有角色的情况下查看场景。
    // 主要数据成员：m_pivot（焦点）、m_distance（距离，米）、m_yaw/m_pitch（角度）、m_panSpeed。
    // 主要外部方法：Unity 的 LateUpdate。
    // 谁在用它：World 场景的 Main Camera。
    // 注意：用右键转视角（左键留给以后的角色操作），避免和将来的点击/攻击冲突。
    public sealed class WorldOrbitCamera : MonoBehaviour
    {
        // 观察焦点（世界的哪个点在屏幕中心）
        [SerializeField]
        private Vector3 m_pivot = new Vector3(0f, 1.5f, 0f);

        // 相机到焦点的距离（米，2~60）
        [SerializeField]
        private float m_distance = 12f;

        // 水平角（度）
        [SerializeField]
        private float m_yaw = 0f;

        // 俯仰角（度，-20~80）
        [SerializeField]
        private float m_pitch = 25f;

        // 右键拖动灵敏度（度/像素）
        [SerializeField]
        private float m_lookSensitivity = 0.25f;

        // 滚轮缩放速度（米/格）
        [SerializeField]
        private float m_zoomSpeed = 2f;

        // WASD 平移速度（米/秒）
        [SerializeField]
        private float m_panSpeed = 8f;

        // 功能：每帧根据输入更新相机位姿。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 右键拖动改 yaw/pitch（pitch 夹住，避免翻过顶）
        //   2) 滚轮改距离（夹住 2~60）
        //   3) WASD 平移焦点（按当前朝向的水平方向）
        //   4) 按球坐标算相机位置并看向焦点
        // 上游：Unity
        // 下游：无
        private void LateUpdate()
        {
            if (Input.GetMouseButton(1))
            {
                m_yaw += Input.GetAxis("Mouse X") * m_lookSensitivity * 10f;
                m_pitch -= Input.GetAxis("Mouse Y") * m_lookSensitivity * 10f;
                m_pitch = Mathf.Clamp(m_pitch, -20f, 80f);
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");

            if (Mathf.Abs(scroll) > 0.001f)
            {
                m_distance = Mathf.Clamp(m_distance - scroll * m_zoomSpeed * 5f, 2f, 60f);
            }

            float horizontal = Input.GetAxis("Horizontal");
            float vertical = Input.GetAxis("Vertical");

            if (Mathf.Abs(horizontal) > 0.001f || Mathf.Abs(vertical) > 0.001f)
            {
                Quaternion flatRotation = Quaternion.Euler(0f, m_yaw, 0f);
                Vector3 move = flatRotation * new Vector3(horizontal, 0f, vertical);
                m_pivot += move * m_panSpeed * Time.deltaTime;
            }

            Quaternion rotation = Quaternion.Euler(m_pitch, m_yaw, 0f);
            Vector3 offset = rotation * new Vector3(0f, 0f, -m_distance);
            transform.position = m_pivot + offset;
            transform.rotation = rotation;
        }
    }
}
