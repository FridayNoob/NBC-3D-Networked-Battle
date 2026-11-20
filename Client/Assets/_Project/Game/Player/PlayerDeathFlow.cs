// 这个文件里有：
//   PlayerDeathFlow：角色死后的分流处理（世界回出生点复活 / 副本单人退出 / 副本多人留房旁观）。

using System;
using System.Collections;
using NBC.Game.Bootstrap;
using NBC.Game.Config;
using NBC.Game.GameFlow;
using NBC.Game.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NBC.Game.Player
{
    // 主要功能：发现角色死亡 → 黑屏倒计时 → 按场景分流（世界复活、副本单人退出、副本多人旁观）。
    // 主要数据成员：m_countdownSeconds（秒）、m_worldSpawn（世界出生点）、m_levelId（读 Level 表用）、m_brain / m_move / m_jumpMotor / m_driver。
    // 主要外部方法：无（挂在玩家根节点上，Update 里自己驱动）。
    // 谁在用它：World 与 Battle 两个场景里的 [Player_Hero] 根节点。
    public sealed class PlayerDeathFlow : MonoBehaviour
    {
        // 世界场景名（用来区分「本地世界」与「联网副本」）
        private const string WorldSceneName = "World";

        // 读不到 Level 表时的兜底倒计时（秒）
        private const float FallbackCountdownSeconds = 5f;

        // 倒计时秒数；Start 里会优先用 Level 表的 deathCountdownSeconds
        [SerializeField] private float m_countdownSeconds = FallbackCountdownSeconds;

        // 世界出生点（村口，和 R034 记的坐标一致）
        [SerializeField] private Vector3 m_worldSpawn = new Vector3(280.799f, 1.45f, 425.014f);

        // 读哪一行 Level 配置（默认第 1 行）
        [SerializeField] private int m_levelId = 1;

        // 死亡期间要不要把角色藏起来（旁观模式用；世界复活不需要）
        [SerializeField] private bool m_hideWhenDead = true;

        // 要不要打诊断日志
        [SerializeField] private bool m_logDiagnostics = true;

        // 玩家根节点上的大脑 / 移动 / 跳跃电机（懒找）
        private PlayerBrain m_brain;
        private PlayerMove m_move;
        private PlayerJumpMotor m_jumpMotor;

        // 联网驱动（只有 Battle 场景有）
        private RuntimeNetDriver m_driver;

        // 角色身上所有渲染器（隐藏/恢复用）
        private Renderer[] m_renderers;

        // 是否正在倒计时
        private bool m_counting;

        // 剩余秒数
        private float m_remaining;

        // 是否已进入旁观模式
        private bool m_spectating;

        // 倒计时结束后的分流是否已经执行过
        private bool m_resolved;

        // 功能：缓存引用、读配置。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 找大脑/移动/跳跃电机 2) 收渲染器列表 3) 读 Level 表倒计时。
        // 上游/下游：Unity 生命周期；下游 LevelConfig.Get。
        // 注意：黑幕不在这里预热 —— Awake 时机在 Login 场景也可能触发，那时资源层还没装好，
        //       让 UIManager 去加载面板会报「还没有装上底层加载器」。改到 Start 预热（见下）。
        private void Awake()
        {
            m_brain = GetComponent<PlayerBrain>();
            m_move = GetComponent<PlayerMove>();
            m_jumpMotor = GetComponent<PlayerJumpMotor>();
            m_renderers = GetComponentsInChildren<Renderer>(true);
            m_countdownSeconds = ResolveCountdownSeconds();
        }

        // 功能：资源层就位后预热黑幕面板（真死的时候不闪）。
        // 入参：无。
        // 返回：无。
        // 步骤：调一次 Ensure；资源层没就位时 Ensure 自己会跳过，等死亡流程再请求。
        // 上游/下游：Unity 生命周期；下游 DeathScreenOverlay.Ensure。
        private void Start()
        {
            DeathScreenOverlay.Ensure();
        }

        // 功能：补找 Component 式的引用（Battle 场景里本组件挂在别的物体上时用）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 缺大脑就全场景找一个 2) 缺移动就全场景找一个 3) 渲染器为空就从角色模型上收一遍。
        // 上游/下游：BeginCountdown / EnterSpectator 前调用。
        private void EnsureFallbackRefs()
        {
            if (m_brain == null)
            {
                m_brain = UnityEngine.Object.FindObjectOfType<PlayerBrain>();
            }

            if (m_move == null)
            {
                m_move = UnityEngine.Object.FindObjectOfType<PlayerMove>();
            }

            if (m_jumpMotor == null && m_move != null)
            {
                m_jumpMotor = m_move.GetComponent<PlayerJumpMotor>();
            }

            if ((m_renderers == null || m_renderers.Length == 0) && m_move != null)
            {
                m_renderers = m_move.GetComponentsInChildren<Renderer>(true);
            }
        }

        // 功能：每帧看角色死没死，死了就开始倒计时，倒计时结束做分流。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 没死就返回 2) 第一次发现死亡就开倒计时 3) 倒计时递减并刷黑幕 4) 到点分流。
        // 上游/下游：Unity 生命周期；下游 DeathScreenOverlay、ResolveWorld / ResolveBattle。
        private void Update()
        {
            // R042：结算面板已经接管屏幕时，死亡流程让位（结算优先，不让「你已倒下」再压上去）
            if (NBC.Game.UI.DungeonResultPanel.IsShowing)
            {
                // 结算是一局的终点：不再走死亡倒计时，并且把可能已经亮起来的黑幕收掉。
                // 不收的话，胜利画面会和「你已倒下」叠在一起，而且回世界后还挂在屏幕上。
                m_counting = false;
                m_resolved = true;
                NBC.Game.UI.DeathScreenOverlay.HideIfShowing();
                return;
            }

            if (!IsDeadNow())
            {
                return;
            }

            if (!m_counting && !m_resolved)
            {
                BeginCountdown();
            }

            if (m_spectating)
            {
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    ExitToWorld("手动退出副本");
                }

                return;
            }

            if (!m_counting)
            {
                return;
            }

            m_remaining -= Time.deltaTime;
            DeathScreenOverlay overlay = DeathScreenOverlay.Ensure();
            overlay.SetCountdown(m_remaining);

            if (m_remaining <= 0f)
            {
                m_counting = false;
                m_resolved = true;
                ResolveDeath();
            }
        }

        // 功能：判断「现在算不算死了」。
        // 入参：无。
        // 返回：true 表示该走死亡流程。
        // 步骤：本地世界看大脑的死亡标记；联网副本先看驱动有没有收到「英雄已倒下」。
        // 上游/下游：Update 调用。
        private bool IsDeadNow()
        {
            if (m_brain != null && m_brain.IsDead)
            {
                return true;
            }

            RuntimeNetDriver driver = GetDriver();

            // 只有"表现层正绑在副本里"才算副本死亡：
            // 回 World 之后驱动可能还留着上一局的 heroAlive=False（甚至自动重新进了房），
            // 只看 IsJoined 会让人在草地上又倒一次。
            if (driver != null && driver.IsJoined && driver.BindPresentation && !driver.HeroAlive)
            {
                return true;
            }

            return false;
        }

        // 功能：开始倒计时并显示黑幕。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 标记倒计时 2) 复位剩余秒数 3) 判断单人还是多人，写不同提示 4) 显示黑幕。
        // 上游/下游：Update 调用；下游 DeathScreenOverlay.Show。
        private void BeginCountdown()
        {
            EnsureFallbackRefs();
            m_counting = true;
            m_remaining = m_countdownSeconds;

            DeathScreenOverlay overlay = DeathScreenOverlay.Ensure();
            overlay.Show("你已倒下", HintForMode());
            overlay.SetCountdown(m_remaining);

            if (m_logDiagnostics)
            {
                Debug.Log("[死亡流程] 开始倒计时 " + m_countdownSeconds.ToString("0.0") + " 秒"
                    + "（场景=" + SceneManager.GetActiveScene().name
                    + "，英雄数=" + (GetDriver() != null ? GetDriver().HeroEntityCount : 0) + "）");
            }
        }

        // 功能：倒计时结束后按场景与人数分流。
        // 入参：无。
        // 返回：无。
        // 步骤：世界场景直接复活；副本里多人留房旁观、单人退出回世界。
        // 上游/下游：Update 调用；下游 ReviveInWorld / EnterSpectator / ExitToWorld。
        private void ResolveDeath()
        {
            if (IsWorldScene())
            {
                ReviveInWorld();
                return;
            }

            RuntimeNetDriver driver = GetDriver();
            int heroes = driver != null ? driver.HeroEntityCount : 1;

            if (heroes > 1)
            {
                EnterSpectator();
            }
            else
            {
                ExitToWorld("单人副本倒下");
            }
        }

        // 功能：在世界里把角色原地复活并送回出生点。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 传送 2) 竖直积分清零 3) 让状态机复活 4) 关黑幕。
        // 上游/下游：ResolveDeath 调用；下游 PlayerBrain.NotifyRevive、PlayerJumpMotor.ResetToGround。
        private void ReviveInWorld()
        {
            // 先关黑幕：后面的复活步骤任何一步出异常，画面也不能一直黑着
            DeathScreenOverlay.Ensure().Hide();
            transform.position = m_worldSpawn;

            if (m_jumpMotor != null)
            {
                m_jumpMotor.ResetToGround();
            }

            if (m_brain != null)
            {
                m_brain.NotifyRevive();
            }

            SetRenderersVisible(true);
            m_resolved = false;

            if (m_logDiagnostics)
            {
                Debug.Log("[死亡流程] 已在世界复活，位置=" + m_worldSpawn);
            }
        }

        // 功能：把玩家挪回世界出生点（结算面板点「返回世界」时用，不要求角色已死）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 传送到出生点 2) 竖直积分清零 3) 恢复渲染器（旁观时可能被藏起来）
        // 上游/下游：RuntimeNetDriver.ReturnToWorldRoutine 调用；下游 PlayerJumpMotor.ResetToGround。
        public void TeleportToWorldSpawn()
        {
            EnsureFallbackRefs();
            transform.position = m_worldSpawn;

            if (m_jumpMotor != null)
            {
                m_jumpMotor.ResetToGround();
            }

            SetRenderersVisible(true);
            m_resolved = false;
            m_spectating = false;

            if (m_logDiagnostics)
            {
                Debug.Log("[死亡流程] 结算后回到世界出生点，位置=" + m_worldSpawn);
            }
        }

        // 功能：新一局开始时把死亡流程整个复位（「再来一次」用，不要求角色已经死）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 清掉倒计时/旁观/已结算三个标记 2) 关黑幕 3) 竖直积分清零 4) 让状态机复活 5) 恢复渲染器。
        // 上游/下游：RuntimeNetDriver.RestartRoutine 调用；下游 PlayerBrain.NotifyRevive、PlayerJumpMotor.ResetToGround。
        // 注意：这里**不搬位置** —— 新一局出生点由服务端快照决定，搬了反而会和快照对账打架。
        public void ReviveAfterRestart()
        {
            EnsureFallbackRefs();
            m_counting = false;
            m_resolved = false;
            m_spectating = false;

            DeathScreenOverlay.HideIfShowing();

            if (m_jumpMotor != null)
            {
                m_jumpMotor.ResetToGround();
            }

            if (m_brain != null)
            {
                m_brain.NotifyRevive();
            }

            SetRenderersVisible(true);

            if (m_logDiagnostics)
            {
                Debug.Log("[死亡流程] 新一局开始，死亡状态已复位");
            }
        }

        // 功能：多人副本里进入旁观：藏起自己、关掉操作、黑幕改成提示。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 标记旁观 2) 藏渲染器 3) 关移动与跳跃 4) 黑幕写「按 Esc 退出」。
        // 上游/下游：ResolveDeath 调用；下游 DeathScreenOverlay。
        private void EnterSpectator()
        {
            EnsureFallbackRefs();
            m_spectating = true;

            if (m_hideWhenDead)
            {
                SetRenderersVisible(false);
            }

            if (m_move != null)
            {
                m_move.MovementEnabled = false;
            }

            if (m_jumpMotor != null)
            {
                m_jumpMotor.enabled = false;
            }

            DeathScreenOverlay overlay = DeathScreenOverlay.Ensure();
            overlay.Show("你已倒下", "正在观战，按 Esc 退出副本");
            overlay.SetStatusText("观战中");

            if (m_logDiagnostics)
            {
                Debug.Log("[死亡流程] 进入旁观（英雄数=" + (GetDriver() != null ? GetDriver().HeroEntityCount : 0) + "）");
            }
        }

        // 功能：退出副本并回到世界场景。
        // 入参：reason 退出原因（只用于日志）。
        // 返回：无。
        // 步骤：1) 让会话退出房间 2) 关黑幕 3) 协程载入 World（YooAsset 失败会退 SceneManager）
        //       4) 成功后销毁「带进副本的玩家」和会话物体，让 World 用自己场景里的玩家重新开始。
        // 上游/下游：ResolveDeath / Update 的 Esc 调用；下游 RuntimeNetDriver.LeaveRoom、SceneFlow.LoadAsync。
        // 注意：玩家是 DontDestroyOnLoad 带进副本的；不销毁就会和 World 场景里的 [Player_Hero] 变成两个人。
        private void ExitToWorld(string reason)
        {
            m_spectating = false;
            m_counting = false;

            RuntimeNetDriver driver = GetDriver();

            if (driver != null)
            {
                // 只发 LeaveRoom 是不够的：驱动还开着 AutoJoin，退房后下一帧会自动重新进房，
                // 服务端会在你切场景的途中又开一局（再进副本必死就是这么来的）。
                // ExitDungeon 会先关 AutoJoin 再退房，并把表现层标记切到「不在副本里」。
                driver.ExitDungeon();
            }

            DeathScreenOverlay.Ensure().Hide();

            if (m_logDiagnostics)
            {
                Debug.Log("[死亡流程] 退出副本回世界（原因=" + reason + "）");
            }

            StartCoroutine(ExitToWorldRoutine());
        }

        // 功能：真正切回 World 的协程（等 SceneFlow 回调，失败再兜底）。
        // 入参：无
        // 返回：迭代器
        // 步骤：1) 没有 SceneFlow 就直接 SceneManager 2) 有就走 SceneFlow 并等回调
        //       3) 失败再用 SceneManager 兜底 4) 成功清场
        // 上游：ExitToWorld
        // 下游：SceneFlow.LoadAsync、SceneManager.LoadScene、CleanupAfterExit
        private IEnumerator ExitToWorldRoutine()
        {
            SceneFlow flow = SceneFlow.Instance;

            if (flow == null)
            {
                Debug.LogWarning("[死亡流程] 找不到 SceneFlow，直接 LoadScene(World)");
                SceneManager.LoadScene(WorldSceneName);
                yield break;
            }

            bool done = false;
            bool ok = false;

            flow.LoadAsync(GameScene.World, (result, error) =>
            {
                ok = result;

                if (!result && m_logDiagnostics)
                {
                    Debug.LogWarning("[死亡流程] SceneFlow 加载 World 失败：" + error + "，改用 SceneManager 兜底");
                }

                done = true;
            });

            while (!done)
            {
                yield return null;
            }

            if (!ok)
            {
                SceneManager.LoadScene(WorldSceneName);
                yield break;
            }

            CleanupAfterExit();
        }

        // 功能：回 World 后清掉「副本残留」——会话物体与带过来的玩家。
        // 入参：无
        // 返回：无
        // 步骤：1) 销毁 [DungeonSession]（里面有 RuntimeNetDriver） 2) 销毁玩家根节点
        // 上游：ExitToWorldRoutine（只在 World 已经加载成功后调用）
        // 下游：Object.Destroy
        // 注意：玩家根节点上就是本组件自己，所以这一句之后本对象不再存在，后面不要再写逻辑。
        private void CleanupAfterExit()
        {
            GameObject session = GameObject.Find(DungeonEntrance.SessionObjectName);

            if (session != null)
            {
                Destroy(session);
            }

            if (m_logDiagnostics)
            {
                Debug.Log("[死亡流程] 已回到 World，销毁副本残留（会话物体 + 带过来的玩家）");
            }

            Destroy(gameObject);
        }

        // 功能：按模式给一行提示（世界写「秒后复活」，副本写「秒后退出/观战」）。
        // 入参：无。
        // 返回：提示文字。
        // 步骤：世界固定文案；副本按人数给两种文案。
        // 上游/下游：BeginCountdown 调用。
        private string HintForMode()
        {
            if (IsWorldScene())
            {
                return "秒后回到出生点";
            }

            RuntimeNetDriver driver = GetDriver();
            int heroes = driver != null ? driver.HeroEntityCount : 1;
            return heroes > 1 ? "秒后进入观战" : "秒后退出副本";
        }

        // 功能：从 Level 表读死亡倒计时（秒）。
        // 入参：无。
        // 返回：倒计时秒数；读不到就返回兜底值。
        // 步骤：1) 拿 LevelConfig 2) 按 m_levelId 取行 3) 夹到 1~15 秒。
        // 上游/下游：Awake 调用；下游 ConfigMgr.TryGet、LevelConfig.Get。
        private float ResolveCountdownSeconds()
        {
            LevelConfig table;

            if (ConfigMgr.Instance == null || !ConfigMgr.Instance.TryGet<LevelConfig>(out table) || table == null)
            {
                return m_countdownSeconds > 0f ? m_countdownSeconds : FallbackCountdownSeconds;
            }

            try
            {
                Config_Level row = table.Get(m_levelId);

                if (row != null && row.deathCountdownSeconds > 0)
                {
                    return Mathf.Clamp(row.deathCountdownSeconds, 1, 15);
                }
            }
            catch (Exception)
            {
                // 表里没有这一行时保持 Inspector 上的值
            }

            return m_countdownSeconds > 0f ? m_countdownSeconds : FallbackCountdownSeconds;
        }

        // 功能：判断当前是不是世界场景。
        // 入参：无。
        // 返回：true 表示 World。
        // 步骤：比较当前激活场景名。
        // 上游/下游：IsDeadNow / HintForMode / ResolveDeath 调用。
        private bool IsWorldScene()
        {
            return SceneManager.GetActiveScene().name == WorldSceneName;
        }

        // 功能：懒加载联网驱动。
        // 入参：无。
        // 返回：驱动；不在副本里返回 null。
        // 步骤：第一次调用时 FindObjectOfType 并缓存。
        // 上游/下游：IsDeadNow / BeginCountdown / ResolveDeath / ExitToWorld 调用。
        private RuntimeNetDriver GetDriver()
        {
            // 场景里可能有两个驱动（Login 的 [MenuSession] 常驻 + 副本的 [DungeonSession]），
            // 优先用已进房的那个，否则死亡判定会读到一个永远 IsJoined=false 的驱动。
            if (m_driver == null || !m_driver.IsJoined)
            {
                m_driver = RuntimeNetDriver.FindJoined();
            }

            return m_driver;
        }

        // 功能：把所有渲染器一次开/关（旁观时藏起自己）。
        // 入参：visible true 显示、false 隐藏。
        // 返回：无。
        // 步骤：遍历 Awake 缓存的渲染器数组逐个设置 enabled。
        // 上游/下游：ReviveInWorld / EnterSpectator 调用。
        private void SetRenderersVisible(bool visible)
        {
            if (m_renderers == null)
            {
                return;
            }

            for (int i = 0; i < m_renderers.Length; i++)
            {
                if (m_renderers[i] != null)
                {
                    m_renderers[i].enabled = visible;
                }
            }
        }
    }
}
