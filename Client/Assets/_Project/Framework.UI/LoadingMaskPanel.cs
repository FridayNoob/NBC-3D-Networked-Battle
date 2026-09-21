// ============================================================================
//  NBC.Framework.UI · 加载遮罩面板
//  对应需求：FW-M09（生命周期）、FW-M06（场景进度事件）
//  消费：A5 的 `Scenes.SceneEvents.ProgressChanged`
//
//  ---------------------------------------------------------------------------
//  它是什么
//  ---------------------------------------------------------------------------
//  切场景时盖在最上面的那块"正在加载 42%"。
//
//  ⚠️ **它只是一个"显示"的面板，不做任何决策。**
//     什么时候显示、什么时候收起，由 `LoadingMaskController` 决定 ——
//     这样"面板长什么样"和"什么时候出现"是分开的，两边都能单独换。
//
//  ---------------------------------------------------------------------------
//  预制体要长什么样
//  ---------------------------------------------------------------------------
//  根节点挂**本组件**（或它的子类），然后：
//    · 有个叫 `ProgressBar` 的节点，上面挂 `Image`  → 用 `fillAmount` 显示进度
//    · 有个叫 `ProgressText` 的节点，上面挂 `Text`  → 显示"正在加载 42%"
//
//  ⚠️ **两个都是可选的**。只有进度条、或只有文字，都能正常工作 ——
//     所以这里用的是 `GetControl`（找不到返回 null，不报错），
//     而不是 `RequireControl`（找不到抛异常）。
//     这是本框架里少数**故意不 fail-fast** 的地方：遮罩的样式是美术决定的。
// ============================================================================

using UnityEngine;
using UnityEngine.UI;

namespace NBC.Framework.UI
{
    /// <summary>
    /// 加载遮罩。显示进度或错误文字。
    /// </summary>
    public class LoadingMaskPanel : BasePanel
    {
        /// <summary>进度条节点的名字（上面挂 <see cref="Image"/>）。**可选。**</summary>
        public const string ProgressBarControlName = "ProgressBar";

        /// <summary>进度文字节点的名字（上面挂 <see cref="Text"/>）。**可选。**</summary>
        public const string ProgressTextControlName = "ProgressText";

        private Image m_progressBar;
        private Text m_progressText;

        /// <summary>最近一次设置的进度（0..1）。</summary>
        public float CurrentProgress { get; private set; }

        /// <summary>最近一次设置的场景地址。</summary>
        public string CurrentLocation { get; private set; }

        /// <summary>当前显示的是不是错误信息。</summary>
        public bool HasError { get; private set; }

        /// <summary>当前显示的文字。</summary>
        public string CurrentText
        {
            get { return m_progressText != null ? m_progressText.text : string.Empty; }
        }

        /// <summary>取两个可选控件。</summary>
        protected override void OnInit()
        {
            m_progressBar = GetControl<Image>(ProgressBarControlName);
            m_progressText = GetControl<Text>(ProgressTextControlName);

            SetProgress(0f, null);
        }

        /// <summary>
        /// 设置进度。
        /// </summary>
        /// <param name="progress">进度（0..1），会被钳到这个范围。</param>
        /// <param name="location">正在加载的场景地址；可为 null。</param>
        public void SetProgress(float progress, string location)
        {
            CurrentProgress = Mathf.Clamp01(progress);
            CurrentLocation = location;
            HasError = false;

            if (m_progressBar != null)
            {
                m_progressBar.fillAmount = CurrentProgress;
            }

            if (m_progressText != null)
            {
                m_progressText.text = string.IsNullOrEmpty(location)
                    ? "正在加载 " + Mathf.RoundToInt(CurrentProgress * 100f) + "%"
                    : "正在加载 " + location + " " + Mathf.RoundToInt(CurrentProgress * 100f) + "%";
            }
        }

        /// <summary>
        /// 显示错误。**遮罩不会自动消失** —— 让玩家看清楚出了什么事。
        /// </summary>
        /// <param name="error">错误文本。</param>
        public void SetError(string error)
        {
            HasError = true;

            if (m_progressText != null)
            {
                m_progressText.text = "加载失败：" + error;
            }
        }
    }
}
