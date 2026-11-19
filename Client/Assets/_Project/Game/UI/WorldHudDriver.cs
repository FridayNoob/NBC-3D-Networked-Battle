// 这个文件里有：
//   WorldHudDriver（类）：World 场景的常驻 HUD 驱动，显示玩家血条、网络状态与任务追踪。

using System.Collections;
using NBC.Framework.UI;
using NBC.Game.Bootstrap;
using NBC.Game.Config;
using UnityEngine;

namespace NBC.Game.UI
{
    // 主要功能：World 场景里把三块 HUD 面板打开（玩家血条 / 网络状态 / 任务追踪），离开场景时关掉。
    // 主要数据成员：m_heroId（英雄编号，取血上限用）、m_maxHp/m_hp（当前显示的血量）。
    // 主要外部方法：无（挂在 [Player_Hero] 上自动跑）。
    // 谁在用它：World 场景的 [Player_Hero]；面板本体在 Client/Assets/_Project/UI/ 下。
    // 注意：World 没有网络会话、也没有任务数据，所以血条按满血、任务行显示一句提示；
    //       真正随时变化的血量与任务进度在副本里由 RuntimeHudDriver 负责。
    public sealed class WorldHudDriver : MonoBehaviour
    {
        // 血条面板地址（= prefab 文件名）
        public const string HpBarPanelName = "HpBarPanel";

        // 网络状态面板地址（= prefab 文件名）
        public const string NetHudPanelName = "NetHudPanel";

        // 任务追踪面板地址（= prefab 文件名）
        public const string QuestPanelName = "HudQuestPanel";

        // 默认英雄编号（与 Dungeon/Login 里的 1001 剑士一致）
        public const int DefaultHeroId = 1001;

        // 查不到英雄表时的血量兜底（点）
        public const int FallbackMaxHp = 1200;

        // World 里任务追踪区显示的一句话（没有任务数据时的占位）
        public const string QuestHint = "世界区域：暂无进行中的任务";

        // 等资源层的上限（秒）；超时只告警，不影响后面继续等
        public const float AssetsWaitTimeoutSeconds = 15f;

        // 英雄编号（取血上限用）
        [SerializeField]
        private int m_heroId = DefaultHeroId;

        // 是否打诊断日志
        [SerializeField]
        private bool m_logDiagnostics = true;

        private bool m_bound;
        private bool m_warnedTimeout;
        private HpBarPanel m_hpPanel;
        private HudQuestPanel m_questPanel;
        private int m_maxHp = FallbackMaxHp;
        private int m_hp = FallbackMaxHp;

        // 功能：启动后等资源层就绪，再把三块面板打开。
        // 入参：无（引擎调用）。
        // 返回：无。
        // 步骤：起协程，逐帧问 BattleReadiness.AssetsReady。
        // 上游：Unity 引擎。
        // 下游：BindRoutine。
        private void Start()
        {
            StartCoroutine(BindRoutine());
        }

        // 功能：等资源层就绪后打开三块 HUD 面板。
        // 入参：无。
        // 返回：协程迭代器。
        // 步骤：
        //   1) 每帧问一次就绪；超过上限只告警一次，继续等（资源层可能慢，但一定会好）
        //   2) 就绪后读英雄表取血上限、血条按满血
        //   3) 依次 ShowPanel：血条、网络状态、任务追踪（任务区写占位提示）
        // 上游：Start。
        // 下游：UIManager.ShowPanel、HudQuestPanel.SetHint。
        private IEnumerator BindRoutine()
        {
            float waited = 0f;

            while (!BattleReadiness.AssetsReady)
            {
                if (!m_warnedTimeout && waited >= AssetsWaitTimeoutSeconds)
                {
                    m_warnedTimeout = true;
                    Debug.LogWarning("[WorldHud] 资源层 " + AssetsWaitTimeoutSeconds + " 秒还没就绪，继续等…");
                }

                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            UIManager ui = UIManager.Instance;

            if (ui == null)
            {
                Debug.LogWarning("[WorldHud] 没有 UIManager，跳过 HUD。");
                yield break;
            }

            ResolveMaxHp();

            ui.ShowPanel(
                HpBarPanelName,
                UILayer.Bot,
                panel =>
                {
                    m_hpPanel = panel as HpBarPanel;

                    if (m_hpPanel != null)
                    {
                        m_hpPanel.SetHp(m_hp, m_maxHp);
                    }
                },
                error => Debug.LogError("[WorldHud] 打不开血条面板：" + error.Message));

            ui.ShowPanel(
                NetHudPanelName,
                UILayer.Bot,
                null,
                error => Debug.LogError("[WorldHud] 打不开网络面板：" + error.Message));

            ui.ShowPanel(
                QuestPanelName,
                UILayer.Bot,
                panel =>
                {
                    m_questPanel = panel as HudQuestPanel;

                    if (m_questPanel != null)
                    {
                        m_questPanel.SetHint(QuestHint);
                    }
                },
                error => Debug.LogError("[WorldHud] 打不开任务面板：" + error.Message));

            m_bound = true;

            if (m_logDiagnostics)
            {
                Debug.Log("[WorldHud] World HUD 已打开：血条 " + m_hp + "/" + m_maxHp + "、网络状态、任务追踪");
            }
        }

        // 功能：从英雄表取血上限，取不到就用兜底值。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 配置表没来源就先用兜底 2) TryGet 命中就取 hp 3) 当前血先按满血。
        // 上游：BindRoutine。
        // 下游：ConfigMgr.TryGet(HeroConfig)。
        private void ResolveMaxHp()
        {
            m_maxHp = FallbackMaxHp;

            ConfigMgr configs = ConfigMgr.Instance;
            HeroConfig heroes = null;

            if (configs != null && configs.Source != null && configs.TryGet(out heroes))
            {
                Config_Hero row;

                if (heroes.TryGet(m_heroId, out row) && row.hp > 0)
                {
                    m_maxHp = row.hp;
                }
            }

            m_hp = m_maxHp;
        }

        // 功能：离开场景时把三块面板关掉（面板挂在常驻 Canvas 下，不会自己消失）。
        // 入参：无（引擎调用）。
        // 返回：无。
        // 步骤：停协程；有 UIManager 就逐个 HidePanel。
        // 上游：Unity 引擎（场景卸载）。
        // 下游：UIManager.HidePanel。
        private void OnDestroy()
        {
            StopAllCoroutines();

            if (!m_bound)
            {
                return;
            }

            UIManager ui = UIManager.Instance;

            if (ui == null)
            {
                return;
            }

            ui.HidePanel(HpBarPanelName);
            ui.HidePanel(NetHudPanelName);
            ui.HidePanel(QuestPanelName);
        }
    }
}
