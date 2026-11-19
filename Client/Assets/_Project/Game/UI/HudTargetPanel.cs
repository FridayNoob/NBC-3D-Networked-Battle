// 这个文件里有：
//   HudTargetPanel（类）：副本 HUD 的「BOSS 血条 + 当前目标」面板，只按数值改填充与文字。

using NBC.Framework.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NBC.Game.UI
{
    // 主要功能：把 BOSS 与当前目标的「名字 + 当前/最大血」画成两条血条；显隐由调用方决定。
    // 主要数据成员：m_bossRoot/m_targetRoot（整行显隐）、两条 Fill（填充图）、四个文字。
    // 主要外部方法：SetBoss（BOSS 行）、SetTarget（目标行）。
    // 谁在用它：RuntimeHudDriver 每帧按快照喂数据；prefab 在 Client/Assets/_Project/UI/HudTargetPanel.prefab。
    public sealed class HudTargetPanel : BasePanel
    {
        // 面板名必须等于 prefab 文件名（收集器是 AddressByFileName）
        public new const string PanelName = "HudTargetPanel";

        public const string BossRootControl = "BossRoot";
        public const string BossLabelControl = "BossLabel";
        public const string BossValueControl = "BossValue";
        public const string BossFillControl = "BossFill";
        public const string TargetRootControl = "TargetRoot";
        public const string TargetLabelControl = "TargetLabel";
        public const string TargetValueControl = "TargetValue";
        public const string TargetFillControl = "TargetFill";

        // 两条血条行与文字（BossRoot/TargetRoot 各带一张背景图，所以能当控件取到）
        private GameObject m_bossRoot;
        private GameObject m_targetRoot;
        private TMP_Text m_bossLabel;
        private TMP_Text m_bossValue;
        private Image m_bossFill;
        private TMP_Text m_targetLabel;
        private TMP_Text m_targetValue;
        private Image m_targetFill;

        // 功能：取出 prefab 里的控件（两行各四个）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 两行根各取一张 Image 当显隐开关 2) 文字与填充图逐个取。
        // 上游：BasePanel.Initialize。
        // 下游：RequireControl / GetControl。
        protected override void OnInit()
        {
            m_bossRoot = RequireControl<Image>(BossRootControl).gameObject;
            m_targetRoot = RequireControl<Image>(TargetRootControl).gameObject;

            m_bossLabel = GetControl<TMP_Text>(BossLabelControl);
            m_bossValue = GetControl<TMP_Text>(BossValueControl);
            m_bossFill = GetControl<Image>(BossFillControl);

            m_targetLabel = GetControl<TMP_Text>(TargetLabelControl);
            m_targetValue = GetControl<TMP_Text>(TargetValueControl);
            m_targetFill = GetControl<Image>(TargetFillControl);
        }

        // 功能：设置 BOSS 行（显隐、名字、血条）。
        // 入参：visible 是否显示；label 名字；hp 当前血；maxHp 最大血。
        // 返回：无。
        // 步骤：1) 切换整行显隐 2) 隐藏就直接返回 3) 写名字与血条。
        // 上游：RuntimeHudDriver.Update。
        public void SetBoss(bool visible, string label, int hp, int maxHp)
        {
            if (m_bossRoot != null && m_bossRoot.activeSelf != visible)
            {
                m_bossRoot.SetActive(visible);
            }

            if (!visible)
            {
                return;
            }

            if (m_bossLabel != null)
            {
                m_bossLabel.text = label ?? string.Empty;
            }

            ApplyBar(m_bossFill, m_bossValue, hp, maxHp);
        }

        // 功能：设置目标行（显隐、名字、血条）。
        // 入参：visible 是否显示；label 名字；hp 当前血；maxHp 最大血。
        // 返回：无。
        // 步骤：同 SetBoss。
        // 上游：RuntimeHudDriver.Update。
        public void SetTarget(bool visible, string label, int hp, int maxHp)
        {
            if (m_targetRoot != null && m_targetRoot.activeSelf != visible)
            {
                m_targetRoot.SetActive(visible);
            }

            if (!visible)
            {
                return;
            }

            if (m_targetLabel != null)
            {
                m_targetLabel.text = label ?? string.Empty;
            }

            ApplyBar(m_targetFill, m_targetValue, hp, maxHp);
        }

        // 功能：把当前/最大血写进一条填充图与一个数字。
        // 入参：fill 填充图（可为空）；value 数字（可为空）；hp 当前血；maxHp 最大血。
        // 返回：无。
        // 步骤：1) maxHp <= 0 时比例按 0 2) hp 钳到 0..maxHp 3) 写 fillAmount 与「当前/最大」。
        private static void ApplyBar(Image fill, TMP_Text value, int hp, int maxHp)
        {
            float ratio = 0f;

            if (maxHp > 0)
            {
                int clamped = Mathf.Clamp(hp, 0, maxHp);
                ratio = (float)clamped / maxHp;

                if (value != null)
                {
                    value.text = clamped + "/" + maxHp;
                }
            }
            else if (value != null)
            {
                value.text = "0/0";
            }

            if (fill != null)
            {
                fill.fillAmount = ratio;
            }
        }
    }
}
