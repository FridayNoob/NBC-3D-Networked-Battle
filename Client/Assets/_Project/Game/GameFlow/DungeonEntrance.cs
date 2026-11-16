// 这个文件里有：
//   DungeonEntrance（类）：World 里的副本入口，负责触发判定、屏幕提示、先连服再切场景。

using System.Collections;
using NBC.Game.Bootstrap;
using NBC.Game.Player;
using UnityEngine;

namespace NBC.Game.GameFlow
{
    // 类：副本入口
    // 主要功能：玩家走进范围后按 E，先让 RuntimeNetDriver 与服务器握手，成功再加载 Battle 场景。
    // 主要数据成员：m_triggerRadius（米）、m_enterKey、m_host / m_port / m_dungeonId、m_connectTimeoutSeconds（秒）。
    // 主要外部方法：IsConnecting（只读，是否正在连接）。
    // 谁在用它：World 场景里的入口物体（Inspector 挂载）；切场景由 SceneFlow 执行。
    public sealed class DungeonEntrance : MonoBehaviour
    {
        // 会话物体名：跨场景保留，Battle 里的 HpBarDemo 会复用它而不是再建一个
        private const string SessionObjectName = "[DungeonSession]";

        // 触发半径（米）：玩家根节点与入口的距离小于它就显示提示
        [SerializeField] private float m_triggerRadius = 3.5f;

        // 进入副本的按键
        [SerializeField] private KeyCode m_enterKey = KeyCode.E;

        // 服务器地址
        [SerializeField] private string m_host = "127.0.0.1";

        // 服务器端口（9000 在 Windows 保留段里，别用）
        [SerializeField] private int m_port = 7778;

        // 副本编号：决定服务端用哪张地图
        [SerializeField] private int m_dungeonId = 1001;

        // 握手超时（秒）：超过这个时间还没连上就提示失败
        [SerializeField] private float m_connectTimeoutSeconds = 8f;

        // 是否打印诊断日志
        [SerializeField] private bool m_logDiagnostics = true;

        // 玩家根节点（缓存，第一次 Update 时找）
        private Transform m_player;

        // 玩家当前是否在触发范围内
        private bool m_inRange;

        // 是否正在连接（防连点）
        private bool m_connecting;

        // 屏幕上要显示的一句话（空 = 不显示）
        private string m_status;

        // 这句话显示到什么时候（Time.realtimeSinceStartup 的秒数）
        private float m_statusUntil;

        // 功能：告诉外部是否正在连接。
        // 入参：无
        // 返回：true 表示连接/切场景进行中
        // 步骤：直接返回缓存字段
        // 上游/下游：入口自身与将来的组队逻辑读取
        public bool IsConnecting
        {
            get { return m_connecting; }
        }

        // 功能：每帧判断玩家是否在入口旁，并处理按 E。
        // 入参：无（引擎每帧调）
        // 返回：无
        // 步骤：
        //   1) 找玩家（只找一次，找不到就每帧重试）
        //   2) 距离判定落在 m_triggerRadius 内
        //   3) 在范围内且没在连接、按下 E ⇒ 开始进副本
        // 上游：Unity 引擎
        // 下游：EnterRoutine、PlayerPersist、SceneFlow.LoadAsync
        private void Update()
        {
            ResolvePlayer();

            if (m_player == null)
            {
                m_inRange = false;
                return;
            }

            float distance = Vector3.Distance(m_player.position, transform.position);
            m_inRange = distance <= m_triggerRadius;

            if (m_inRange && !m_connecting && Input.GetKeyDown(m_enterKey))
            {
                StartCoroutine(EnterRoutine());
            }
        }

        // 功能：在屏幕上画一行提示（零资产，不依赖 UI 框架）。
        // 入参：无（引擎在 GUI 阶段调）
        // 返回：无
        // 步骤：
        //   1) 有错误/状态文字且还没过期 ⇒ 画状态
        //   2) 否则在范围内 ⇒ 画「按 E 进入副本」
        //   3) 正在连接 ⇒ 画连接中
        // 上游：Unity OnGUI
        // 下游：无
        private void OnGUI()
        {
            string text = null;

            if (!string.IsNullOrEmpty(m_status) && Time.realtimeSinceStartup < m_statusUntil)
            {
                text = m_status;
            }
            else if (m_connecting)
            {
                text = "正在连接副本服务器…";
            }
            else if (m_inRange)
            {
                text = "按 " + m_enterKey + " 进入副本";
            }

            if (text == null)
            {
                return;
            }

            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = 22;
            style.alignment = TextAnchor.MiddleCenter;
            style.normal.textColor = Color.white;

            Rect area = new Rect(0f, Screen.height * 0.72f, Screen.width, 40f);
            GUI.Label(area, text, style);
        }

        // 功能：执行「先连服、再切场景」的完整流程。
        // 入参：无
        // 返回：协程
        // 步骤：
        //   1) 找或建 [DungeonSession]，拿到/补上 RuntimeNetDriver，写入连接参数
        //   2) 关掉自动进房与表现绑定：World 里进房会让副本英雄被输入带着走，也会画出一堆副本怪
        //   3) 等握手成功；超时则销毁会话并提示
        //   4) 把玩家标成跨场景保留，再加载 Battle
        //   5) 切场景成功后打开自动进房与表现绑定；失败则销毁会话并提示
        // 上游：Update 检测到按 E
        // 下游：RuntimeNetDriver.ConfigureSession/IsOnline、PlayerPersist.MarkPersistent、SceneFlow.LoadAsync
        private IEnumerator EnterRoutine()
        {
            m_connecting = true;
            Log("开始进入副本，先握手");

            GameObject session = GameObject.Find(SessionObjectName);
            if (session == null)
            {
                session = new GameObject(SessionObjectName);
                DontDestroyOnLoad(session);
            }

            RuntimeNetDriver driver = session.GetComponent<RuntimeNetDriver>();
            if (driver == null)
            {
                driver = session.AddComponent<RuntimeNetDriver>();
            }

            driver.ConfigureSession(m_host, m_port, m_dungeonId);
            driver.AutoJoin = false;
            driver.BindPresentation = false;

            float deadline = Time.realtimeSinceStartup + Mathf.Max(1f, m_connectTimeoutSeconds);
            while (!driver.IsOnline
                && string.IsNullOrEmpty(driver.FailureReason)
                && string.IsNullOrEmpty(driver.LastError)
                && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (!driver.IsOnline)
            {
                // 原因优先级：会话失败原因（如「账号登录被拒」）> 重连放弃原因 > 纯超时
                string reason = driver.FailureReason;
                if (string.IsNullOrEmpty(reason))
                {
                    reason = driver.LastError;
                }
                if (string.IsNullOrEmpty(reason))
                {
                    reason = "超时（" + m_host + ":" + m_port + "）";
                }
                SetStatus("连接副本服务器失败：" + reason);
                Destroy(session);
                m_connecting = false;
                yield break;
            }

            Log("握手成功，准备切场景");

            if (!PlayerPersist.MarkPersistent(m_player.gameObject))
            {
                SetStatus("玩家物体无效，无法进入副本");
                Destroy(session);
                m_connecting = false;
                yield break;
            }

            bool finished = false;
            bool loaded = false;
            string error = null;

            SceneFlow.Instance.LoadAsync(GameScene.Battle, (ok, message) =>
            {
                loaded = ok;
                error = message;
                finished = true;
            });

            while (!finished)
            {
                yield return null;
            }

            if (!loaded)
            {
                SetStatus("加载副本场景失败：" + error);
                Destroy(session);
                m_connecting = false;
                yield break;
            }

            driver.AutoJoin = true;
            driver.BindPresentation = true;

            Log("已进入副本，打开自动进房与表现绑定");
            m_connecting = false;
            enabled = false;
        }

        // 功能：找到 World 里的玩家根节点并缓存。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已经有就直接返回
        //   2) 按 PlayerBrain 找（World 里唯一），取它的 Transform
        // 上游：Update
        // 下游：无
        private void ResolvePlayer()
        {
            if (m_player != null)
            {
                return;
            }

            PlayerBrain brain = Object.FindObjectOfType<PlayerBrain>();
            if (brain != null)
            {
                m_player = brain.transform;
            }
        }

        // 功能：设置屏幕提示并保持几秒。
        // 入参：text 提示文字
        // 返回：无
        // 步骤：写字段，并记下过期时间（3 秒）
        // 上游：EnterRoutine 的失败分支
        // 下游：OnGUI
        private void SetStatus(string text)
        {
            m_status = text;
            m_statusUntil = Time.realtimeSinceStartup + 3f;
            Debug.LogWarning("[DungeonEntrance] " + text);
        }

        // 功能：按开关打印诊断日志。
        // 入参：message 日志正文
        // 返回：无
        // 步骤：m_logDiagnostics 为 false 时直接返回
        // 上游：EnterRoutine
        // 下游：Unity Console
        private void Log(string message)
        {
            if (m_logDiagnostics)
            {
                Debug.Log("[DungeonEntrance] " + message);
            }
        }
    }
}
