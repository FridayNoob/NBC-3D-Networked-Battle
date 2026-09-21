// ============================================================================
//  NBC.Framework.UI · 加载遮罩的"什么时候显示"（策略）
//  对应需求：FW-M09（异步加载的生命周期）、FW-M06（场景进度）
//  消费：A5 的 `Scenes.SceneEvents.ProgressChanged` / `LoadSucceeded` / `LoadFailed`
//
//  ---------------------------------------------------------------------------
//  它是什么
//  ---------------------------------------------------------------------------
//  把 A5 广播的**场景加载进度**，翻译成"遮罩该不该出现、上面写什么"。
//
//      SceneEvents.ProgressChanged  →  遮罩出现 + 更新进度
//      SceneEvents.LoadSucceeded    →  遮罩收起
//      SceneEvents.LoadFailed       →  遮罩**留着**并显示错误
//
//  ---------------------------------------------------------------------------
//  为什么策略单独一个类，而不是塞进 LoadingMaskPanel
//  ---------------------------------------------------------------------------
//  "面板长什么样"是美术的事，"什么时候出现"是逻辑的事。分开之后：
//    · 换一套遮罩皮肤不用改逻辑
//    · 逻辑可以**脱离预制体单独测**（本类的测试就不需要任何 UI 预制体）
//
//  ⚠️ 而且它是一个**普通类**，不是单例、不是 MonoBehaviour ——
//     **由游戏层决定什么时候装上去**。框架不该擅自决定"这个项目一定要有加载遮罩"。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 一个容易踩的坑：移除监听要传**同一个委托实例**
//  ---------------------------------------------------------------------------
//  `EventCenter.RemoveEventListener<T>` 是按委托相等性匹配的。
//  所以三个回调**必须存在字段里**，不能每次现写 `OnProgress`
//  —— 那样移除时匹配不上，监听会一直留着（就是 P-05 那个毛病）。
// ============================================================================

using System;
using NBC.Framework.Scenes;
using UnityEngine;
using UnityEngine.Events;

namespace NBC.Framework.UI
{
    /// <summary>
    /// 加载遮罩的控制器：订阅场景加载事件，决定遮罩的显示与内容。
    /// </summary>
    public sealed class LoadingMaskController : IDisposable
    {
        /// <summary>默认的遮罩面板名（预制体地址 = <c>UI/</c> + 这个名字）。</summary>
        public const string DefaultPanelName = "LoadingMask";

        private readonly UIManager m_ui;
        private readonly EventCenter m_events;
        private readonly string m_panelName;

        /// <summary>⚠️ 这三个**必须存字段**，移除监听时要传同一个实例（见文件头）。</summary>
        private readonly UnityAction<SceneProgressInfo> m_onProgress;
        private readonly UnityAction<string> m_onSucceeded;
        private readonly UnityAction<SceneFailureInfo> m_onFailed;

        private bool m_attached;
        private bool m_requested;

        /// <summary>构造（不自动订阅，要显式 <see cref="Attach"/>）。</summary>
        /// <param name="ui">UI 管理器。</param>
        /// <param name="panelName">遮罩面板名。</param>
        public LoadingMaskController(UIManager ui, string panelName = DefaultPanelName)
        {
            if (ui == null)
            {
                throw new ArgumentNullException(nameof(ui));
            }

            m_ui = ui;
            m_panelName = string.IsNullOrEmpty(panelName) ? DefaultPanelName : panelName;

            m_onProgress = HandleProgress;
            m_onSucceeded = HandleSucceeded;
            m_onFailed = HandleFailed;

            m_events = EventCenter.Instance;
        }

        /// <summary>是否已经订阅。</summary>
        public bool IsAttached
        {
            get { return m_attached; }
        }

        /// <summary>是否已经请求过显示遮罩（可能还在加载预制体）。</summary>
        public bool IsMaskRequested
        {
            get { return m_requested; }
        }

        /// <summary>遮罩面板名。</summary>
        public string PanelName
        {
            get { return m_panelName; }
        }

        /// <summary>最近一次收到的进度。</summary>
        public float LastProgress { get; private set; }

        /// <summary>最近一次收到的场景地址。</summary>
        public string LastLocation { get; private set; }

        /// <summary>显示次数（测试 / 调试用）。</summary>
        public int ShowCount { get; private set; }

        /// <summary>收起次数（测试 / 调试用）。</summary>
        public int HideCount { get; private set; }

        /// <summary>订阅场景加载事件。</summary>
        public void Attach()
        {
            if (m_attached)
            {
                return;
            }

            m_attached = true;

            m_events.AddEventListener(SceneEvents.ProgressChanged, m_onProgress);
            m_events.AddEventListener(SceneEvents.LoadSucceeded, m_onSucceeded);
            m_events.AddEventListener(SceneEvents.LoadFailed, m_onFailed);
        }

        /// <summary>退订。**不退订就是泄漏**（P-05 那个毛病）。</summary>
        public void Detach()
        {
            if (!m_attached)
            {
                return;
            }

            m_attached = false;

            m_events.RemoveEventListener(SceneEvents.ProgressChanged, m_onProgress);
            m_events.RemoveEventListener(SceneEvents.LoadSucceeded, m_onSucceeded);
            m_events.RemoveEventListener(SceneEvents.LoadFailed, m_onFailed);
        }

        /// <summary>退订并复位。</summary>
        public void Dispose()
        {
            Detach();

            m_requested = false;
            LastProgress = 0f;
            LastLocation = null;
            ShowCount = 0;
            HideCount = 0;
        }

        /// <summary>
        /// 手动收起遮罩（例如游戏层想在加载结束后自己控制时机）。
        /// </summary>
        /// <returns>true 表示确实收起了一个。</returns>
        public bool HideNow()
        {
            m_requested = false;

            if (m_ui.HidePanel(m_panelName))
            {
                HideCount++;
                return true;
            }

            return false;
        }

        // ====================================================================
        //  事件处理
        // ====================================================================

        /// <summary>进度变化：确保遮罩出现，并更新进度。</summary>
        /// <param name="info">进度信息。</param>
        private void HandleProgress(SceneProgressInfo info)
        {
            LastProgress = info.Progress;
            LastLocation = info.Location;

            if (!m_requested)
            {
                m_requested = true;
                ShowCount++;

                string capturedName = m_panelName;
                m_ui.ShowPanel(capturedName, UILayer.System, OnMaskShown, OnMaskFailed);
                return;
            }

            // 已经显示出来了就直接更新；还在加载中就先记着，等它出来再补上。
            ApplyProgress();
        }

        /// <summary>加载成功：收起遮罩。</summary>
        /// <param name="location">场景地址。</param>
        private void HandleSucceeded(string location)
        {
            HideNow();
        }

        /// <summary>加载失败：遮罩**留着**，把错误写上去让玩家看见。</summary>
        /// <param name="info">失败信息。</param>
        private void HandleFailed(SceneFailureInfo info)
        {
            LastLocation = info.Location;

            LoadingMaskPanel mask = m_ui.GetPanel<LoadingMaskPanel>(m_panelName);

            if (mask != null)
            {
                mask.SetError(info.Error);
                return;
            }

            // 遮罩还没出来（第一次加载就失败）：请求显示，出来之后写错误。
            if (!m_requested)
            {
                m_requested = true;
                ShowCount++;

                string capturedError = info.Error;
                m_ui.ShowPanel(
                    m_panelName,
                    UILayer.System,
                    panel =>
                    {
                        LoadingMaskPanel shown = panel as LoadingMaskPanel;
                        if (shown != null)
                        {
                            shown.SetError(capturedError);
                        }
                    },
                    OnMaskFailed);
            }
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>遮罩显示出来了：把"最新进度"补上。</summary>
        /// <param name="panel">遮罩面板。</param>
        private void OnMaskShown(BasePanel panel)
        {
            LoadingMaskPanel mask = panel as LoadingMaskPanel;

            if (mask == null)
            {
                // 说清楚是哪一步错了：预制体上挂错脚本了。
                Debug.LogError(
                    "[LoadingMaskController] 面板 «" + m_panelName + "» 的根节点上挂的不是 " +
                    "LoadingMaskPanel（实际是 " + panel.GetType().Name + "）。\n" +
                    "遮罩预制体请挂 LoadingMaskPanel 或它的子类，否则进度没办法传进去。");
                return;
            }

            mask.SetProgress(LastProgress, LastLocation);
        }

        /// <summary>遮罩加载失败：只能报出来（总不能为了报错再去弹一个遮罩）。</summary>
        /// <param name="error">错误。</param>
        private static void OnMaskFailed(Exception error)
        {
            Debug.LogError("[LoadingMaskController] 遮罩面板自己加载失败了：" + error.Message);
        }

        /// <summary>把最新进度写到已经显示出来的遮罩上。</summary>
        private void ApplyProgress()
        {
            LoadingMaskPanel mask = m_ui.GetPanel<LoadingMaskPanel>(m_panelName);

            if (mask != null)
            {
                mask.SetProgress(LastProgress, LastLocation);
            }
        }
    }
}
