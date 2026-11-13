// 这个文件里有：
//   WorldPlayerSetup（MonoBehaviour）：把 World 里的玩家接到跟随相机上，并让他贴着地面走。
//
// 为什么需要它：World 场景原来只有观察相机（WorldOrbitCamera）；有了玩家之后，
// 相机要跟人走，人还要贴地形高度 —— 这两件事不能写死在相机/玩家各自的脚本里。

using NBC.Game.Player;

using UnityEngine;

namespace NBC.Game.GameFlow
{
    // 主要功能：找到玩家 → 把跟随相机的目标设成他 → 关掉观察相机 → 每帧把人贴到地面。
    // 主要数据成员：m_player（玩家根节点）、m_followCamera、m_orbitCamera、m_groundMask（哪些层算地面）。
    // 主要外部方法：Unity 的 Start / LateUpdate。
    // 谁在用它：World 场景里的玩家对象（[Player_Hero]）。
    // 注意：贴地用的是向下射线；地形/房子都要有碰撞体，否则人会穿下去。
    public sealed class WorldPlayerSetup : MonoBehaviour
    {
        // 玩家根节点（留空 = 用挂这个组件的对象）
        [SerializeField]
        private Transform m_player;

        // 跟随相机（World 场景的 Main Camera；旧版：世界固定偏移）
        [SerializeField]
        private FollowCamera m_followCamera;

        // 第三人称跟随相机（优先用这个：会绕到角色背后）
        [SerializeField]
        private WorldFollowCamera m_worldFollowCamera;

        // 观察相机（没有玩家时用的那个；有玩家就关掉它）
        [SerializeField]
        private WorldOrbitCamera m_orbitCamera;

        // 贴地射线的层遮罩（默认全部）
        [SerializeField]
        private LayerMask m_groundMask = ~0;

        // 贴地射线的起点高度（米，够高才能打到地面）
        [SerializeField]
        private float m_probeHeight = 5f;

        // 贴地射线的最长距离（米）
        [SerializeField]
        private float m_probeDistance = 50f;

        // 功能：启动时接线：相机跟人、关观察相机。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 玩家没配就用自己
        //   2) 关掉观察相机（避免两套相机抢着写位置）
        //   3) 把跟随相机的目标设成玩家
        // 上游：Unity
        // 下游：FollowCamera / WorldOrbitCamera
        private void Start()
        {
            if (m_player == null)
            {
                m_player = transform;
            }

            if (m_orbitCamera != null)
            {
                m_orbitCamera.enabled = false;
            }

            // 没在 Inspector 里配第三人称相机 ⇒ 运行时给 Main Camera 加一个（场景不用改）
            if (m_worldFollowCamera == null)
            {
                Camera mainCamera = Camera.main;

                if (mainCamera != null)
                {
                    m_worldFollowCamera = mainCamera.GetComponent<WorldFollowCamera>();

                    if (m_worldFollowCamera == null)
                    {
                        m_worldFollowCamera = mainCamera.gameObject.AddComponent<WorldFollowCamera>();
                    }
                }
            }

            if (m_worldFollowCamera != null)
            {
                m_worldFollowCamera.enabled = true;
                m_worldFollowCamera.SetTarget(m_player);
            }

            if (m_followCamera != null)
            {
                // 旧的"世界固定偏移"相机：有新的就关掉，避免两套都写位置
                m_followCamera.enabled = m_worldFollowCamera == null;

                if (m_worldFollowCamera == null)
                {
                    m_followCamera.SetTarget(m_player);
                }
            }

            if (m_worldFollowCamera == null && m_followCamera == null)
            {
                Debug.LogWarning("[WorldPlayerSetup] 没配跟随相机，画面会停在原地");
            }

            // World 需要「镜头相对移动」：W 永远是镜头前方（Battle 用世界轴，那边不动）
            PlayerMove relativeMove = GetComponent<PlayerMove>();

            if (relativeMove != null)
            {
                Camera mainCameraForAxes = Camera.main;
                relativeMove.SetReferenceAxes(true, mainCameraForAxes != null ? mainCameraForAxes.transform : null);
            }
            // 防"双份移动"：模型身上若也挂了 PlayerMove / PlayerAnimation，两套会各读一份输入、各走一次
            PlayerMove[] moves = GetComponentsInChildren<PlayerMove>(true);

            for (int i = 0; i < moves.Length; i++)
            {
                if (moves[i].gameObject != gameObject && moves[i].enabled)
                {
                    moves[i].enabled = false;
                    Debug.LogWarning("[WorldPlayerSetup] 关掉子物体 " + moves[i].name + " 上重复的 PlayerMove（会造成双份位移）");
                }
            }

            PlayerAnimation[] animations = GetComponentsInChildren<PlayerAnimation>(true);

            for (int i = 0; i < animations.Length; i++)
            {
                if (animations[i].gameObject != gameObject && animations[i].enabled)
                {
                    animations[i].enabled = false;
                    Debug.LogWarning("[WorldPlayerSetup] 关掉子物体 " + animations[i].name + " 上重复的 PlayerAnimation");
                }
            }
            // World 的动画改由 PlayerBrain + 动画参数驱动；旧的 PlayerAnimation 还会自己 SetTrigger，会和状态机打架
            PlayerAnimation selfAnimation = GetComponent<PlayerAnimation>();

            if (selfAnimation != null && selfAnimation.enabled)
            {
                selfAnimation.enabled = false;
                Debug.Log("[WorldPlayerSetup] 关掉根节点上的 PlayerAnimation（World 动画改由状态机参数驱动）");
            }

            Debug.Log("[WorldPlayerSetup] 玩家已就位：" + m_player.name
                + "，第三人称相机=" + (m_worldFollowCamera != null)
                + "，旧跟随相机=" + (m_followCamera != null));
        }

        // 诊断：是否打印「输入 → 朝向/速度」日志（排查转身/动画问题用，默认关）
        [SerializeField]
        private bool m_debugLog = false;

        // 诊断用：上一次打印的时间
        private float m_lastLogTime;

        // 功能：临时诊断，打印输入轴、当前朝向、速度。
        // 入参：无
        // 返回：无
        // 步骤：每 0.5 秒一条，内容含 MoveX/MoveY、yaw、速度
        // 上游：LateUpdate
        // 下游：Debug.Log
        private void LogDiagnostics()
        {
            if (!m_debugLog)
            {
                return;
            }

            if (Time.time - m_lastLogTime < 0.5f)
            {
                return;
            }

            m_lastLogTime = Time.time;

            NBC.Framework.Input.InputManager input = NBC.Framework.Input.InputManager.Instance;
            int moveX = input != null ? input.Current.MoveX : 0;
            int moveY = input != null ? input.Current.MoveY : 0;
            NBC.Game.Player.PlayerMove move = GetComponent<NBC.Game.Player.PlayerMove>();

            Debug.Log("[玩家诊断] 输入 X=" + moveX + " Y=" + moveY
                + " 朝向 y=" + transform.eulerAngles.y.ToString("0")
                + " 速度=" + (move != null ? move.CurrentSpeed.ToString("0.00") : "?")
                + " 移动组件启用=" + (move != null && move.enabled));
        }

        // 功能：每帧把玩家贴到地面（地形有起伏，不贴会浮空/陷地）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 从玩家上方往下打一条射线
        //   2) 打到地面就把 y 设成命中点（保留 x/z）
        // 上游：Unity
        // 下游：Transform.position
        // 注意：只改 y；水平移动仍由 PlayerMove 负责
        private void LateUpdate()
        {
            if (m_player == null)
            {
                return;
            }

            LogDiagnostics();

            Vector3 origin = m_player.position + Vector3.up * m_probeHeight;

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, m_probeDistance, m_groundMask, QueryTriggerInteraction.Ignore))
            {
                Vector3 position = m_player.position;
                position.y = hit.point.y;
                m_player.position = position;
            }
        }
    }
}
