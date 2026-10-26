// 这个文件里有：
//   HpBarPanel（类）：屏幕 HUD 的血条面板，只负责“按名字找控件 + 改长度与颜色”

using NBC.Framework.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NBC.Game.UI
{
    // 主要功能：把「当前血 / 最大血」画成一条会变短、会变色的血条；外观、位置、图片全部由 prefab 决定。
    // 主要数据成员：m_fill（填充图，必需）、m_bg（背景，可选）、m_tmpValue / m_legacyValue（数字，可选）。
    // 主要外部方法：SetHp（按当前/最大设置）、SetRatio（直接按 0..1 设置）。
    // 谁在用它：UIManager.ShowPanel("HpBarPanel", UILayer.Bot) 打开；由战斗 HUD 或演示驱动调 SetHp。
    public sealed class HpBarPanel : BasePanel
    {
        // 填充图的名字：prefab 里必须有，且 Image Type 必须是 Filled
        public const string FillControl = "Img_Fill";

        // 背景图的名字：可选，没有就不改颜色
        public const string BgControl = "Img_Bg";

        // 数字文字的名字：可选，TMP 或 UGUI Text 都支持
        public const string ValueControl = "Text_Value";

        private Image m_fill;
        private Image m_bg;
        private TMP_Text m_tmpValue;
        private Text m_legacyValue;

        // 当前比例：0 = 空血，1 = 满血
        public float Ratio { get; private set; }

        // 入参：无（BasePanel.Initialize 里调）
        // 返回：无
        // 步骤：
        //   1) 取 Img_Fill（必需，取不到就抛带指引的异常，避免“没反应又不说为什么”）
        //   2) 取 Img_Bg、Text_Value（可选，没有就跳过）
        // 上游：BasePanel.Initialize
        // 下游：BasePanel.GetControl / RequireControl
        protected override void OnInit()
        {
            m_fill = RequireControl<Image>(FillControl);
            m_bg = GetControl<Image>(BgControl);
            m_tmpValue = GetControl<TMP_Text>(ValueControl);

            if (m_tmpValue == null)
            {
                m_legacyValue = GetControl<Text>(ValueControl);
            }
        }

        // 入参：current 当前血（会被钳在 0..max）；max 最大血（<= 0 时按 0 比例处理）
        // 返回：无
        // 步骤：
        //   1) 钳住 current 到 0..max
        //   2) 算比例（max <= 0 时按 0）
        //   3) 转给 SetRatio 统一处理显示
        // 上游：战斗 HUD / 演示驱动
        // 下游：SetRatio
        public void SetHp(int current, int max)
        {
            if (max <= 0)
            {
                SetRatio(0f);
                return;
            }

            int hp = current < 0 ? 0 : (current > max ? max : current);
            SetRatio((float)hp / max);

            if (m_tmpValue != null)
            {
                m_tmpValue.text = hp + " / " + max;
            }
            else if (m_legacyValue != null)
            {
                m_legacyValue.text = hp + " / " + max;
            }
        }

        // 入参：ratio 0..1（越界会被钳住）
        // 返回：无
        // 步骤：
        //   1) 记录比例
        //   2) 改填充图的 fillAmount（这就是“血条变短”的全部秘密）
        //   3) 按比例把颜色从红渐变到绿；背景图若存在则不动
        // 上游：SetHp
        // 下游：Image.fillAmount、Image.color
        public void SetRatio(float ratio)
        {
            Ratio = Mathf.Clamp01(ratio);

            if (m_fill != null)
            {
                m_fill.fillAmount = Ratio;
                m_fill.color = Color.Lerp(Color.red, Color.green, Ratio);
            }

            if (m_bg != null && m_bg.color.a <= 0f)
            {
                m_bg.color = new Color(m_bg.color.r, m_bg.color.g, m_bg.color.b, 1f);
            }
        }
    }
}
