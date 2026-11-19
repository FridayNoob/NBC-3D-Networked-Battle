// 这个文件里有：
//   HudQuestPanel（类）：副本 HUD 的任务追踪面板，把进行中的任务拼成最多三行文字。

using System.Collections.Generic;
using System.Text;
using NBC.Framework.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NBC.Game.UI
{
    // 主要功能：把任务本里「进行中」的任务拼成最多三行「标题 · 详情」，没有就整块隐藏。
    // 主要数据成员：m_root（整块显隐）、m_tracker（一段多行文字）、m_rows（复用缓冲，避免每帧分配）。
    // 主要外部方法：SetQuest（喂任务本）。
    // 谁在用它：RuntimeHudDriver 每 0.2 秒喂一次；prefab 在 Client/Assets/_Project/UI/HudQuestPanel.prefab。
    public sealed class HudQuestPanel : BasePanel
    {
        // 面板名必须等于 prefab 文件名
        public new const string PanelName = "HudQuestPanel";

        public const string RootControl = "Root";
        public const string TrackerControl = "Text_Tracker";

        // 任务行在 prefab 里能看到的最大条数
        public const int MaxRows = 3;

        private GameObject m_root;
        private TMP_Text m_tracker;
        private readonly List<QuestRow> m_rows = new List<QuestRow>();
        private readonly StringBuilder m_builder = new StringBuilder(256);

        // 功能：取 prefab 里的控件。
        // 入参：无。
        // 返回：无。
        // 上游：BasePanel.Initialize。
        protected override void OnInit()
        {
            m_root = RequireControl<Image>(RootControl).gameObject;
            m_tracker = RequireControl<TMP_Text>(TrackerControl);
        }

        // 功能：按任务本刷新追踪行。
        // 入参：model 任务本（可为 null）。
        // 返回：无。
        // 步骤：1) 取进行中的行 2) 转给 SetRows。
        // 上游：离线调试路径（RuntimeHudDriver 直接用 SetRows）。
        public void SetQuest(QuestPanelModel model)
        {
            m_rows.Clear();

            if (model != null)
            {
                model.CopyTrackingRows(m_rows);
            }

            SetRows(m_rows);
        }

        // 功能：直接按行刷新追踪区（联机时行由 RuntimeHudDriver 拼）。
        // 入参：rows 要显示的行，最多 MaxRows 条。
        // 返回：无。
        // 步骤：1) 没行就隐藏整块 2) 有行就显示并拼「标题 · 详情」。
        // 上游：RuntimeHudDriver.UpdateQuest。
        public void SetRows(IReadOnlyList<QuestRow> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                if (m_root != null && m_root.activeSelf)
                {
                    m_root.SetActive(false);
                }

                return;
            }

            if (m_root != null && !m_root.activeSelf)
            {
                m_root.SetActive(true);
            }

            m_builder.Length = 0;

            int count = Mathf.Min(MaxRows, rows.Count);

            for (int i = 0; i < count; i++)
            {
                QuestRow row = rows[i];

                if (i > 0)
                {
                    m_builder.Append('\n');
                }

                m_builder.Append(row.Title);

                if (!string.IsNullOrEmpty(row.Detail))
                {
                    m_builder.Append(" · ");
                    m_builder.Append(row.Detail);
                }
            }

            if (m_tracker != null)
            {
                m_tracker.text = m_builder.ToString();
            }
        }
    }
}
