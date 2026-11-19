// 这个文件里有：
// MainMenuController：主菜单的流程控制（读昵称、点开始连服、成功切 World、失败提示、设置占位、退出）。

using System.Collections;
using NBC.Game.GameFlow;
using NBC.Game.Net;
using NBC.Game.Bootstrap;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NBC.Game.Menu
{
    /// <summary>
    /// 主菜单的流程控制。
    /// <para>主要功能：把「昵称 → 连服务器 → 切场景」串起来，失败时把原因写在按钮下方。</para>
    /// <para>主要数据成员：m_state（纯逻辑）、m_view（界面）、m_sessionObject（常驻会话物体）。</para>
    /// <para>主要外部方法：StartGame（点开始）、ToggleSettings（设置占位）、QuitGame（退出）。</para>
    /// <para>谁在用它：Login 场景里的 [MainMenu] 物体挂载它。</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuController : MonoBehaviour
    {
        /// <summary>上次用的昵称存这里。</summary>
        public const string PlayerNameKey = "nbc.menu.playerName";

        /// <summary>会话物体名（放进 DontDestroyOnLoad，进 World 后继续用同一份连接）。</summary>
        public const string SessionObjectName = "[MenuSession]";

        private const string Host = "127.0.0.1";
        private const int Port = 7778;
        private const int DungeonId = 1001;

        [SerializeField] private float m_connectTimeoutSeconds = 3f;
        [SerializeField] private bool m_logDiagnostics = true;

        private readonly MainMenuState m_state = new MainMenuState();
        private MainMenuView m_view;
        private GameObject m_sessionObject;
        private RuntimeNetDriver m_driver;
        private bool m_switching;

        /// <summary>当前状态（测试与排查用）。</summary>
        public MainMenuState State
        {
            get { return m_state; }
        }

        // 功能：启动时建界面、回填昵称、绑按钮。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 拿或加 View 2) 读 PlayerPrefs 回填 3) 绑三个回调 4) 按状态刷一次文字。
        private void Start()
        {
            m_view = GetComponent<MainMenuView>();

            if (m_view == null)
            {
                m_view = gameObject.AddComponent<MainMenuView>();
            }

            string saved = PlayerPrefs.GetString(PlayerNameKey, string.Empty);
            m_state.SetPlayerName(saved);
            m_view.SetName(m_state.PlayerName);
            m_view.Bind(StartGame, ToggleSettings, QuitGame);
            Refresh();
        }

        // 功能：点「开始游戏」——记昵称、建常驻会话、开始连服。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 状态机拒绝（正在连/已连上）就直接返回 2) 规范化并存昵称 3) 找或建会话物体 4) 起协程等连接。
        // 上游/下游：View 的按钮回调；下游 StartCoroutine(ConnectRoutine)。
        public void StartGame()
        {
            if (!m_state.BeginConnect())
            {
                return;
            }

            m_state.SetPlayerName(m_view.ReadName());
            PlayerPrefs.SetString(PlayerNameKey, m_state.PlayerName);
            PlayerPrefs.Save();

            EnsureSession();
            Refresh();

            // 连接开始时就把目标地址打出来：失败时能立刻分辨「连的是哪」。
            Debug.Log("[主菜单] 正在连接 " + Host + ":" + Port + "（昵称 " + m_state.PlayerName + "）");

            StartCoroutine(ConnectRoutine());
        }

        // 功能：设置图标的占位行为（真设置留给 R047）。
        // 入参：无。
        // 返回：无。
        private void ToggleSettings()
        {
            m_view.SetHint("设置将在 R047 做（音量 / 画质 / 鼠标灵敏度）");
        }

        // 功能：退出游戏（编辑器里停止播放，构建版退出进程）。
        // 入参：无。
        // 返回：无。
        private void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // 功能：等连接结果（成功切场景 / 失败提示）。
        // 入参：无。
        // 返回：协程。
        // 步骤：1) 每帧看 IsOnline 2) 超时或拿到失败原因就失败 3) 成功就切 World。
        private IEnumerator ConnectRoutine()
        {
            float deadline = Time.realtimeSinceStartup + Mathf.Max(0.5f, m_connectTimeoutSeconds);

            while (Time.realtimeSinceStartup < deadline)
            {
                if (m_driver != null && m_driver.IsOnline)
                {
                    m_state.Succeed();
                    Refresh();
                    SwitchToWorld();
                    yield break;
                }

                if (m_driver != null && !string.IsNullOrEmpty(m_driver.FailureReason))
                {
                    FailAndCleanup(m_driver.FailureReason);
                    yield break;
                }

                yield return null;
            }

            string reason = m_driver != null && !string.IsNullOrEmpty(m_driver.LastError)
                ? m_driver.LastError
                : "连不上服务器（" + Host + ":" + Port + "），请确认服务端已启动";
            FailAndCleanup(reason);
        }

        // 功能：失败后恢复界面，并把这次建的会话物体销毁（下次点开始是全新连接）。
        // 入参：reason 失败原因。
        // 返回：无。
        // 步骤：1) 写状态与提示 2) 打一条 warning（排查时不用盯着界面看）3) 销毁这次建的会话物体。
        // 上游：ConnectRoutine / OnSceneLoaded；下游：MainMenuState.Fail、MainMenuView.SetHint。
        private void FailAndCleanup(string reason)
        {
            m_state.Fail(reason);
            Refresh();

            // 界面上的提示容易被忽略，Console 也留一条：失败原因要能从日志里查。
            Debug.LogWarning("[主菜单] 连接失败：" + reason);

            if (m_sessionObject != null)
            {
                Destroy(m_sessionObject);
                m_sessionObject = null;
                m_driver = null;
            }
        }

        // 功能：切到 World（已经连上，会话物体常驻过去）。
        // 入参：无。
        // 返回：无。
        private void SwitchToWorld()
        {
            if (m_switching)
            {
                return;
            }

            m_switching = true;

            if (m_logDiagnostics)
            {
                Debug.Log("[主菜单] 已连接，进入世界（昵称 " + m_state.PlayerName + "）");
            }

            if (SceneFlow.Instance != null)
            {
                SceneFlow.Instance.LoadAsync(GameScene.World, OnSceneLoaded);
                return;
            }

            SceneManager.LoadScene("World");
        }

        // 功能：场景加载回调（只记日志；失败时把原因写在提示行）。
        // 入参：ok 是否成功；message 失败原因。
        // 返回：无。
        private void OnSceneLoaded(bool ok, string message)
        {
            if (ok)
            {
                return;
            }

            m_switching = false;
            m_state.Fail("世界场景加载失败：" + message);
            Refresh();
        }

        // 功能：找或建常驻会话物体，并把驱动配成「只连服、不进房、不接管表现」。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 先找同名物体（重复点开始要复用）2) 没有就新建并 DontDestroyOnLoad 3) 配连接参数与两个开关。
        private void EnsureSession()
        {
            if (m_sessionObject != null)
            {
                return;
            }

            GameObject existing = GameObject.Find(SessionObjectName);

            if (existing != null)
            {
                m_sessionObject = existing;
            }
            else
            {
                m_sessionObject = new GameObject(SessionObjectName);
                DontDestroyOnLoad(m_sessionObject);
            }

            m_driver = m_sessionObject.GetComponent<RuntimeNetDriver>();

            if (m_driver == null)
            {
                m_driver = m_sessionObject.AddComponent<RuntimeNetDriver>();
            }

            m_driver.ConfigureSession(Host, Port, DungeonId);
            m_driver.AutoJoin = false;
            m_driver.BindPresentation = false;
        }

        // 功能：把状态刷到界面上（按钮文字、可用性、提示）。
        // 入参：无。
        // 返回：无。
        private void Refresh()
        {
            if (m_view == null)
            {
                return;
            }

            m_view.SetStartLabel(m_state.IsConnecting ? "连接中…" : "开始游戏");
            m_view.SetStartInteractable(m_state.CanStart);
            m_view.SetHint(m_state.Hint);
        }
    }
}
