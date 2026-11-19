// 这个文件里有：
//   DeathScreenOverlay（类）：死亡时的黑幕面板（标题 + 倒计时 + 状态行），视觉来自预制体。

using NBC.Framework.Asset;
using NBC.Framework.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NBC.Game.UI
{
    // 主要功能：玩家死亡时盖一层黑幕、显示标题与倒计时；复活后关掉。
    // 主要数据成员：m_backdrop（背景图，同时是显隐开关）、m_title/m_countdown/m_status（三段文字）、m_isStub（占位实例）。
    // 主要外部方法：Ensure（静态入口）、Show、SetCountdown、SetStatusText、Hide。
    // 谁在用它：PlayerDeathFlow 在死亡/复活流程里调；DungeonResultPanel 结算前调 Hide。
    // 注意：面板是异步加载的，第一帧还拿不到预制体实例 —— 这时 Ensure 返回一个占位实例，
    //       先把 Show/SetCountdown 的参数记下来，等真面板到了再补一遍（见 OnPanelShown）。
    public sealed class DeathScreenOverlay : BasePanel
    {
        // 面板名必须等于 prefab 文件名
        public new const string PanelName = "DeathScreenOverlay";

        public const string BackdropControl = "Img_Backdrop";
        public const string TitleControl = "Text_Title";
        public const string CountdownControl = "Text_Countdown";
        public const string StatusControl = "Text_Status";

        // 全屏黑幕的透明度：0.85 让玩家还能隐约看到世界，但不刺眼
        public const float BackdropAlpha = 0.85f;

        private static DeathScreenOverlay s_instance;
        private static bool s_panelRequested;

        // 占位期记下的「待应用状态」：真面板一到就照着补上
        private static bool s_wantShow;
        private static string s_wantTitle;
        private static string s_wantStatus;
        private static float s_wantCountdown = -1f;

        private Image m_backdrop;
        private TMP_Text m_title;
        private TMP_Text m_countdown;
        private TMP_Text m_status;
        private bool m_showing;
        private bool m_isStub;

        // 功能：现在黑幕是不是显示着。
        // 入参：无。
        // 返回：true 表示玩家能看到黑幕（占位实例返回「想显示」的状态）。
        public bool IsShowing
        {
            get { return m_isStub ? s_wantShow : m_showing; }
        }

        // 功能：取黑幕面板（没有就请求加载预制体）。
        // 入参：无。
        // 返回：面板实例或占位实例，保证非空。
        // 步骤：1) 已拿到真面板直接返回 2) 没有就建占位 3) 请求一次加载。
        // 上游：PlayerDeathFlow；下游：UIManager.ShowPanel。
        public static DeathScreenOverlay Ensure()
        {
            if (s_instance != null && !s_instance.m_isStub)
            {
                return s_instance;
            }

            if (s_instance == null)
            {
                GameObject stub = new GameObject("[DeathScreenOverlay]");
                s_instance = stub.AddComponent<DeathScreenOverlay>();
                s_instance.m_isStub = true;
            }

            if (!s_panelRequested && CanLoadPanels())
            {
                s_panelRequested = true;
                UIManager ui = UIManager.Instance;

                if (ui != null)
                {
                    ui.ShowPanel(PanelName, UILayer.Top, OnPanelShown);
                }
            }

            return s_instance;
        }

        // 功能：现在能不能让 UIManager 去加载面板（要底层资源加载器已就位）。
        // 入参：无。
        // 返回：true = 可以请求加载；false = 资源层还没装好，等下一次 Ensure 再试。
        // 步骤：1) 取 AssetManager 单例 2) 没装上加载器就返回 false。
        // 上游：Ensure。
        private static bool CanLoadPanels()
        {
            AssetManager assets = AssetManager.Instance;
            return assets != null && assets.IsInitialized;
        }

        // 功能：真面板加载好之后的回调：接管状态、把占位期记下的内容补上。
        // 入参：panel 加载好的面板。
        // 返回：无。
        // 步骤：1) 类型不对就返回 2) 换成真实例、藏掉占位 3) 应用待定状态。
        // 上游：UIManager.ShowPanel 的 onShown。
        private static void OnPanelShown(BasePanel panel)
        {
            DeathScreenOverlay real = panel as DeathScreenOverlay;

            if (real == null)
            {
                return;
            }

            DeathScreenOverlay stub = s_instance;
            real.m_isStub = false;
            s_instance = real;

            if (stub != null && stub != real)
            {
                stub.Hide();
                Object.Destroy(stub.gameObject);
            }

            if (s_wantShow)
            {
                real.Show(s_wantTitle, s_wantStatus);

                if (s_wantCountdown >= 0f)
                {
                    real.SetCountdown(s_wantCountdown);
                }
            }
        }

        // 功能：取 prefab 里的控件。
        // 入参：无。
        // 返回：无。
        // 上游：BasePanel.Initialize。
        protected override void OnInit()
        {
            m_backdrop = RequireControl<Image>(BackdropControl);
            m_title = GetControl<TMP_Text>(TitleControl);
            m_countdown = GetControl<TMP_Text>(CountdownControl);
            m_status = GetControl<TMP_Text>(StatusControl);

            Color color = m_backdrop.color;
            color.a = BackdropAlpha;
            m_backdrop.color = color;
            m_backdrop.raycastTarget = false;
        }

        // 功能：打开黑幕并写入标题与提示。
        // 入参：title 大标题；hint 状态行（可为空）。
        // 返回：无。
        // 步骤：1) 占位实例只记状态 2) 真面板显示背景、写标题、清倒计时。
        public void Show(string title, string hint)
        {
            s_wantShow = true;
            s_wantTitle = title;
            s_wantStatus = hint;

            if (m_isStub)
            {
                return;
            }

            m_showing = true;

            if (m_backdrop != null)
            {
                m_backdrop.gameObject.SetActive(true);
            }

            if (m_title != null)
            {
                m_title.text = title ?? string.Empty;
            }

            if (m_countdown != null)
            {
                m_countdown.text = string.Empty;
            }

            SetStatusText(hint);
        }

        // 功能：更新倒计时数字。
        // 入参：remaining 剩余秒（向上取整显示）。
        // 返回：无。
        public void SetCountdown(float remaining)
        {
            s_wantCountdown = remaining;

            if (!m_isStub && m_countdown != null)
            {
                m_countdown.text = Mathf.CeilToInt(Mathf.Max(0f, remaining)).ToString();
            }
        }

        // 功能：更新状态行文字。
        // 入参：text 要显示的字（null 当空串）。
        // 返回：无。
        public void SetStatusText(string text)
        {
            s_wantStatus = text;

            if (!m_isStub && m_status != null)
            {
                m_status.text = text ?? string.Empty;
            }
        }

        // 功能：关掉黑幕（面板实例保留，下次直接复用）。
        // 入参：无。
        // 返回：无。
        public void Hide()
        {
            s_wantShow = false;
            s_wantCountdown = -1f;
            m_showing = false;

            if (!m_isStub && m_backdrop != null)
            {
                m_backdrop.gameObject.SetActive(false);
            }
        }
    }
}
