// 这个文件里有：
//   RuntimeHudDriver（类）：副本里唯一的 HUD 大脑，按快照挑 BOSS 与目标、按任务状态拼追踪行。

using System.Collections.Generic;
using NBC.Framework.UI;
using NBC.Game.Config;
using NBC.Game.Net;
using NBC.Protocol;
using UnityEngine;

namespace NBC.Game.UI
{
    // 主要功能：把「谁在打我 / 我在打谁 / 我还有哪些任务」翻译成三个面板上的数字与文字。
    // 主要数据成员：m_feed（表现层快照源）、m_session（任务权威）、三块面板、m_rows（复用行缓冲）。
    // 主要外部方法：Bind（进副本时接线）、CloseAll（回 World 时收起）。
    // 谁在用它：RuntimeNetDriver 在进房成功后 Bind、返回世界前 CloseAll。
    public sealed class RuntimeHudDriver : MonoBehaviour
    {
        // BOSS 在快照里的 Kind（协议约定：1 普通怪、2 BOSS、0 英雄）
        private const int KindHero = 0;
        private const int KindBoss = 2;

        // 服务端任务阶段（与 Server/NBC.Server.Game/IQuestStateStore.cs 的 EQuestStage 数值对齐）
        private const int StageAccepted = 1;
        private const int StageCompleted = 2;

        // 任务行刷新间隔，秒：任务不是每帧都变，省掉每帧拼字符串
        private const float QuestRefreshSeconds = 0.25f;

        // 追踪区最多显示几条任务
        private const int MaxQuestRows = HudQuestPanel.MaxRows;

        private PresentationFeed m_feed;
        private NetSession m_session;
        private HudTargetPanel m_targetPanel;
        private HudQuestPanel m_questPanel;
        private QuestConfig m_questConfig;
        private QuestConditionConfig m_conditionConfig;
        private MonsterConfig m_monsterConfig;
        private readonly List<QuestRow> m_rows = new List<QuestRow>();
        private float m_questTimer;

        // 功能：接上快照源与会话，并打开 HUD 面板。
        // 入参：feed 表现层快照源；session 联机会话。
        // 返回：无。
        // 步骤：1) 记住来源与配置表 2) 打开两个面板（失败只告警）3) 立刻刷新一次。
        // 上游：RuntimeNetDriver.OnJoinSucceeded；下游：UIManager.ShowPanel。
        public void Bind(PresentationFeed feed, NetSession session)
        {
            m_feed = feed;
            m_session = session;
            m_questConfig = ConfigMgr.Instance.Get<QuestConfig>();
            m_conditionConfig = ConfigMgr.Instance.Get<QuestConditionConfig>();
            m_monsterConfig = ConfigMgr.Instance.Get<MonsterConfig>();

            UIManager ui = UIManager.Instance;

            if (ui != null)
            {
                if (m_targetPanel == null)
                {
                    ui.ShowPanel(HudTargetPanel.PanelName, UILayer.Bot, panel => m_targetPanel = panel as HudTargetPanel);
                }

                if (m_questPanel == null)
                {
                    ui.ShowPanel(HudQuestPanel.PanelName, UILayer.Bot, panel => m_questPanel = panel as HudQuestPanel);
                }
            }

            if (m_targetPanel == null)
            {
                Debug.LogWarning("[HUD] 打不开 " + HudTargetPanel.PanelName + "（预制体或地址缺失）");
            }

            if (m_questPanel == null)
            {
                Debug.LogWarning("[HUD] 打不开 " + HudQuestPanel.PanelName + "（预制体或地址缺失）");
            }

            UpdateTargets();
            UpdateQuest();
        }

        // 功能：收起 HUD（回 World 时调）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 两条血条隐藏 2) 关掉两块面板。
        // 上游：RuntimeNetDriver.ReturnToWorldRoutine。
        public void CloseAll()
        {
            m_feed = null;
            m_session = null;

            if (m_targetPanel != null)
            {
                m_targetPanel.SetBoss(false, null, 0, 0);
                m_targetPanel.SetTarget(false, null, 0, 0);
            }

            UIManager ui = UIManager.Instance;

            if (ui != null)
            {
                ui.HidePanel(HudTargetPanel.PanelName);
                ui.HidePanel(HudQuestPanel.PanelName);
            }
        }

        // 功能：每帧刷血条，每 0.25 秒刷任务行。
        // 入参：无。
        // 返回：无。
        private void Update()
        {
            if (m_feed == null || m_session == null)
            {
                return;
            }

            UpdateTargets();

            m_questTimer -= Time.unscaledDeltaTime;

            if (m_questTimer <= 0f)
            {
                m_questTimer = QuestRefreshSeconds;
                UpdateQuest();
            }
        }

        // 功能：按当前快照挑 BOSS 与最近的目标，喂给面板。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 找英雄位置 2) 遍历实体找 BOSS 与最近的目标 3) 写面板。
        // 上游：Update / Bind。
        private void UpdateTargets()
        {
            if (m_targetPanel == null)
            {
                return;
            }

            IReadOnlyList<EntitySnapshot> entities = m_feed.View != null ? m_feed.View.Entities : null;
            SnapshotView view = m_feed.View;

            if (entities == null || entities.Count == 0)
            {
                m_targetPanel.SetBoss(false, null, 0, 0);
                m_targetPanel.SetTarget(false, null, 0, 0);
                return;
            }

            EntitySnapshot hero = view != null ? view.FindHero(m_session.PlayerId) : null;
            EntitySnapshot boss = null;
            EntitySnapshot nearest = null;
            long nearestDistanceSq = long.MaxValue;

            for (int i = 0; i < entities.Count; i++)
            {
                EntitySnapshot entity = entities[i];

                if (!entity.Alive || entity.Kind == KindHero)
                {
                    continue;
                }

                if (entity.Kind == KindBoss)
                {
                    if (boss == null)
                    {
                        boss = entity;
                    }

                    continue;
                }

                if (hero == null)
                {
                    continue;
                }

                long dx = entity.PosXMm - hero.PosXMm;
                long dz = entity.PosZMm - hero.PosZMm;
                long distanceSq = dx * dx + dz * dz;

                if (distanceSq < nearestDistanceSq)
                {
                    nearestDistanceSq = distanceSq;
                    nearest = entity;
                }
            }

            if (boss != null)
            {
                m_targetPanel.SetBoss(true, NameOf(boss), boss.Hp, boss.MaxHp);
            }
            else
            {
                m_targetPanel.SetBoss(false, null, 0, 0);
            }

            if (nearest != null)
            {
                m_targetPanel.SetTarget(true, NameOf(nearest), nearest.Hp, nearest.MaxHp);
            }
            else
            {
                m_targetPanel.SetTarget(false, null, 0, 0);
            }
        }

        // 功能：把「已接取 / 可交付」的任务拼成最多三行。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 取任务状态全量 2) 逐条过滤阶段并查表拼标题与进度 3) 交给面板。
        // 上游：Update / Bind。
        private void UpdateQuest()
        {
            if (m_questPanel == null)
            {
                return;
            }

            m_rows.Clear();
            QuestStateSync states = m_session.LastQuestStates;

            if (states != null && m_questConfig != null)
            {
                for (int i = 0; i < states.Entries.Count && m_rows.Count < MaxQuestRows; i++)
                {
                    QuestStateEntry entry = states.Entries[i];

                    if (entry.State != StageAccepted && entry.State != StageCompleted)
                    {
                        continue;
                    }

                    Config_Quest quest;

                    if (!m_questConfig.TryGet(entry.QuestId, out quest))
                    {
                        continue;
                    }

                    m_rows.Add(new QuestRow
                    {
                        QuestId = entry.QuestId,
                        Title = "[" + entry.QuestId + "] " + quest.name,
                        Detail = BuildDetail(quest)
                    });
                }
            }

            m_questPanel.SetRows(m_rows);
        }

        // 功能：把一条任务的完成条件拼成一行进度文字。
        // 入参：quest 任务配置行。
        // 返回：形如「击杀怪物 2/3」；没有条件时返回空串。
        // 步骤：1) 没有条件表就返回空 2) 逐条取进度 3) 用「；」拼起来。
        private string BuildDetail(Config_Quest quest)
        {
            if (m_conditionConfig == null || quest.conditionIds == null || quest.conditionIds.Length == 0)
            {
                return string.Empty;
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder(64);

            for (int i = 0; i < quest.conditionIds.Length; i++)
            {
                int conditionId = quest.conditionIds[i];
                Config_QuestCondition condition;

                if (!m_conditionConfig.TryGet(conditionId, out condition))
                {
                    continue;
                }

                ConditionProgressEntry progress = m_session.FindProgress(conditionId);

                if (builder.Length > 0)
                {
                    builder.Append('；');
                }

                builder.Append(string.IsNullOrEmpty(condition.note) ? "条件 " + conditionId : condition.note);
                builder.Append(' ');
                builder.Append(progress.Current);
                builder.Append('/');
                builder.Append(condition.requiredCount);
            }

            return builder.ToString();
        }

        // 功能：取实体的显示名（怪物查配置表，查不到就用编号）。
        // 入参：entity 快照条目。
        // 返回：给玩家看的名字。
        private string NameOf(EntitySnapshot entity)
        {
            if (m_monsterConfig != null)
            {
                Config_Monster monster;

                if (m_monsterConfig.TryGet(entity.ConfigId, out monster) && !string.IsNullOrEmpty(monster.name))
                {
                    return monster.name;
                }
            }

            return "怪物 " + entity.ConfigId;
        }
    }
}
