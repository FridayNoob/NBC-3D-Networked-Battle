// 这个文件里有：
//   LoadingMaskPanel（类）：场景加载遮罩面板，按外部给的数据显示百分比进度或错误文字

using UnityEngine;
using UnityEngine.UI;

namespace NBC.Framework.UI
{
    // 主要功能：切场景时盖在最上层的「正在加载 N%」遮罩，只按外部传入的进度与错误改显示，不决定何时出现或收起。
    // 主要数据成员：m_progressBar（Image，可空）、m_progressText（Text，可空）、CurrentProgress（0..1）、HasError（是否错误态）。
    // 主要外部方法：SetProgress（写进度并清错误）、SetError（写错误并保持遮罩）、CurrentText（当前显示的文本）。
    // 谁在用它：预制体根节点挂本组件；LoadingMaskController 在场景加载事件里调 SetProgress / SetError。
    /// <summary>加载遮罩面板：显示加载进度或错误文字，显示与收起的时机由 LoadingMaskController 决定。</summary>
    public class LoadingMaskPanel : BasePanel
    {
        /// <summary>进度条节点的名字，节点上挂 Image（Type 需为 Filled，否则 fillAmount 不生效）；该节点可选。</summary>
        public const string ProgressBarControlName = "ProgressBar";

        /// <summary>进度文字节点的名字，节点上挂 Text；该节点可选。</summary>
        public const string ProgressTextControlName = "ProgressText";

        private Image m_progressBar;    // 进度条控件；预制体没有该节点时为 null，写入前必须判空
        private Text m_progressText;    // 进度文字控件；预制体没有该节点时为 null，写入前必须判空

        /// <summary>最近一次设置的进度，0..1。</summary>
        public float CurrentProgress { get; private set; }

        /// <summary>最近一次设置的场景地址；还没设置过时为 null。</summary>
        public string CurrentLocation { get; private set; }

        /// <summary>当前显示的是不是错误文字；SetError 后为 true，SetProgress 后回到 false。</summary>
        public bool HasError { get; private set; }

        /// <summary>当前显示的文字；进度文字控件不存在时为空串。</summary>
        public string CurrentText
        {
            get { return m_progressText != null ? m_progressText.text : string.Empty; }
        }

        // 功能：从已收集好的控件里取出可选的进度条与文字，并把显示归零到 0%。
        // 入参：无（由 BasePanel.Initialize 调用，此时控件已收集完）
        // 返回：无
        // 步骤：
        //   1) 用 GetControl 取进度条与文字，取不到就是 null，不抛异常
        //   2) 立刻 SetProgress(0f, null)，让还没收到进度时显示 0% 而不是上一轮的残留
        // 注意：遮罩样式由美术决定，两个节点都允许缺；这里故意不用 RequireControl，缺一个也能用，换成 RequireControl 会让只有文字或只有进度条的预制体直接抛异常。
        // 上游：BasePanel.Initialize
        // 下游：BasePanel.GetControl、SetProgress
        protected override void OnInit()
        {
            m_progressBar = GetControl<Image>(ProgressBarControlName);
            m_progressText = GetControl<Text>(ProgressTextControlName);

            SetProgress(0f, null);
        }

        /// <summary>设置加载进度，刷新进度条与百分比文字，并把错误态清掉。</summary>
        /// <param name="progress">进度，0..1；超出范围会被钳到这个区间。</param>
        /// <param name="location">正在加载的场景地址；可为 null 或空串，此时文字只显示百分比。</param>
        public void SetProgress(float progress, string location)
        {
            // 功能：记下进度与场景地址、切回非错误态，再更新进度条长度和文字。
            // 入参：progress 进度；location 场景地址，null 或空串表示不显示地址
            // 返回：无
            // 步骤：
            //   1) 先钳住 progress 到 0..1 再存，保证对外属性与显示用同一个值
            //   2) 清 HasError：能收到进度说明已经从错误态恢复
            //   3) 进度条存在才写 fillAmount（0..1）
            //   4) 文字存在才拼「正在加载 [地址] N%」，N 由 0..1 四舍五入成整数
            // 注意：两个控件都可能为 null，不能直接解引用；预制体只放其中一个也能工作。
            // 上游：LoadingMaskController.ApplyProgress、LoadingMaskController.OnMaskShown、OnInit
            // 下游：无
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

        /// <summary>显示错误文字；遮罩不会自动消失，错误要等下一次 SetProgress 才会被覆盖。</summary>
        /// <param name="error">错误文本，会拼在「加载失败：」后面。</param>
        public void SetError(string error)
        {
            // 功能：把进度文字换成「加载失败：」加错误文本，并标记当前是错误态。
            // 入参：error 错误文本；为 null 或空串时仍会拼出「加载失败：」
            // 返回：无
            // 步骤：
            //   1) 先置 HasError，保证即使没有文字控件，外部也能从属性看出失败
            //   2) 有文字控件才写错误文案，进度条保持最后一次的长度不动
            // 注意：遮罩不会自动收起也不清进度，就是要让玩家看清错误；只能靠上层调 SetProgress 或收起面板离开这个状态。
            // 上游：LoadingMaskController.HandleFailed（含遮罩首次出现时的回调）
            // 下游：无
            HasError = true;

            if (m_progressText != null)
            {
                m_progressText.text = "加载失败：" + error;
            }
        }
    }
}
