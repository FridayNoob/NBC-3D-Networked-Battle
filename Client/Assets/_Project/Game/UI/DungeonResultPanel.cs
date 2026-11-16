// 这个文件里有：
//   DungeonResultPanel：副本结算面板（运行时 UGUI，胜/负 + 数据 + 两个按钮）。

using System;
using NBC.Protocol;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NBC.Game.UI
{
    /// <summary>
    /// 副本结算面板：一局结束后显示胜负与数据，并给出「再来一次」「返回世界」两个动作。
    /// 主要数据成员：m_canvas / m_title / m_stats / m_hint 与两个按钮的回调。
    /// 主要外部方法：静态 Ensure、Show、Hide、静态 IsShowing。
    /// 谁在用它：RuntimeNetDriver 收到 DungeonResult 后调用；PlayerDeathFlow 靠 IsShowing 让位。
    /// </summary>
    public sealed class DungeonResultPanel : MonoBehaviour
    {
        // 运行时建的面板放在这个物体上，名字带中括号表示「系统生成」
        private const string PanelObjectName = "[DungeonResultPanel]";

        // 画布层级：比死亡黑幕（900）高，结算永远压在最上面
        private const int CanvasSortingOrder = 950;

        // 背景透明度：比死亡黑幕淡一点，能看出后面是战场
        private const float BackgroundAlpha = 0.72f;

        // 内置字体名（Unity 2022 起 Arial.ttf 改叫 LegacyRuntime.ttf）
        private const string BuiltinFontName = "LegacyRuntime.ttf";

        // 兜底字体名（老版本 Unity 还叫这个名字）
        private const string LegacyBuiltinFontName = "Arial.ttf";

        private static DungeonResultPanel s_instance;

        private Canvas m_canvas;
        private Text m_title;
        private Text m_stats;
        private Text m_hint;
        private Action m_onRestart;
        private Action m_onReturn;

        /// <summary>面板现在是不是显示着（死亡流程用它判断要不要让位）。</summary>
        public static bool IsShowing { get; private set; }

        // 功能：拿到（没有就创建）面板单例。
        // 入参：无。
        // 返回：场景里唯一的面板。
        // 步骤：1) 先找场景里已有的 2) 没有就新建一个带中括号名字的物体挂本组件
        // 上游/下游：RuntimeNetDriver 收到结算时调用；DungeonResultPanel.Awake 里 Build。
        public static DungeonResultPanel Ensure()
        {
            if (s_instance != null)
            {
                return s_instance;
            }

            DungeonResultPanel existing = FindObjectOfType<DungeonResultPanel>();

            if (existing != null)
            {
                s_instance = existing;
                return s_instance;
            }

            GameObject go = new GameObject(PanelObjectName);
            s_instance = go.AddComponent<DungeonResultPanel>();
            return s_instance;
        }

        // 功能：显示结算。
        // 入参：result 服务端结算；onRestart 点「再来一次」要做什么；onReturn 点「返回世界」要做什么。
        // 返回：无。
        // 步骤：
        //   1) 记下两个回调，并把数据写进文字
        //   2) 先关死亡黑幕（结算优先，不让「你已倒下」压在上面）
        //   3) 打开整块画布、解锁鼠标（要能点按钮）
        // 上游/下游：RuntimeNetDriver 的事件回调；下游 DeathScreenOverlay.Hide。
        public void Show(DungeonResult result, Action onRestart, Action onReturn)
        {
            if (result == null)
            {
                return;
            }

            BuildIfNeeded();
            EnsureEventSystem();

            m_onRestart = onRestart;
            m_onReturn = onReturn;

            m_title.text = DungeonOutcomeText.Title(result.Outcome);
            m_title.color = result.Outcome == DungeonOutcomeText.Win
                ? new Color(1f, 0.86f, 0.35f)
                : new Color(0.95f, 0.42f, 0.38f);
            m_stats.text = DungeonOutcomeText.Stats(result);
            m_hint.text = "R 再来一次    Esc 返回世界";

            DeathScreenOverlay.Ensure().Hide();

            m_canvas.gameObject.SetActive(true);
            IsShowing = true;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // 功能：关掉面板。
        // 入参：无。
        // 返回：无。
        // 步骤：关整块画布（只关背景会留下文字）、清回调、复位显示标记
        // 上游/下游：两个按钮与快捷键；RuntimeNetDriver 切场景前也可调。
        public void Hide()
        {
            if (m_canvas != null)
            {
                m_canvas.gameObject.SetActive(false);
            }

            m_onRestart = null;
            m_onReturn = null;
            IsShowing = false;
        }

        // 功能：初始化时把画布建好（只建一次）。
        // 入参：无。
        // 返回：无。
        // 步骤：确保单例指向自己，然后 Build
        // 上游：Unity 生命周期。
        // 下游：Build。
        private void Awake()
        {
            s_instance = this;
            BuildIfNeeded();
        }

        // 功能：面板显示时响应快捷键（R 再来一次，Esc 返回世界）。
        // 入参：无。
        // 返回：无。
        // 步骤：只在显示时处理；先判空再 Invoke，回调里可能把面板关掉
        // 上游：Unity 生命周期。
        // 下游：m_onRestart / m_onReturn。
        private void Update()
        {
            if (!IsShowing)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.R))
            {
                Action action = m_onRestart;

                if (action != null)
                {
                    action();
                }
            }
            else if (Input.GetKeyDown(KeyCode.Escape))
            {
                Action action = m_onReturn;

                if (action != null)
                {
                    action();
                }
            }
        }

        // 功能：第一次用到时把画布与控件建出来。
        // 入参：无。
        // 返回：无。
        // 步骤：已经建过就直接返回，避免重复建
        // 上游/下游：Awake 与 Show 调用；下游 Build。
        private void BuildIfNeeded()
        {
            if (m_canvas != null)
            {
                return;
            }

            Build();
        }

        // 功能：建画布、背景、三行文字、两个按钮。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 建 ScreenSpaceOverlay 画布并设层级（压过死亡黑幕）
        //   2) 建全屏半透明背景
        //   3) 建标题、统计、提示三行字
        //   4) 建两个按钮，点击分别走「再来一次」「返回世界」
        //   5) 先关掉整块画布，等 Show 再打开
        // 上游/下游：BuildIfNeeded 调用；下游 CreateText / CreateButton。
        private void Build()
        {
            GameObject canvasObject = new GameObject("Canvas");
            canvasObject.transform.SetParent(transform, false);

            m_canvas = canvasObject.AddComponent<Canvas>();
            m_canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_canvas.sortingOrder = CanvasSortingOrder;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            canvasObject.AddComponent<GraphicRaycaster>();

            GameObject background = new GameObject("Background");
            background.transform.SetParent(canvasObject.transform, false);

            RectTransform backgroundRect = background.AddComponent<RectTransform>();
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;

            Image backgroundImage = background.AddComponent<Image>();
            backgroundImage.color = new Color(0f, 0f, 0f, BackgroundAlpha);
            backgroundImage.raycastTarget = true;

            m_title = CreateText(canvasObject.transform, "Title", 108, new Vector2(0f, 220f), new Vector2(1600f, 160f));
            m_stats = CreateText(canvasObject.transform, "Stats", 46, new Vector2(0f, 70f), new Vector2(1600f, 100f));
            m_hint = CreateText(canvasObject.transform, "Hint", 34, new Vector2(0f, -300f), new Vector2(1600f, 80f));

            CreateButton(canvasObject.transform, "RestartButton", "再来一次", new Vector2(-220f, -140f), OnRestartClicked);
            CreateButton(canvasObject.transform, "ReturnButton", "返回世界", new Vector2(220f, -140f), OnReturnClicked);

            m_canvas.gameObject.SetActive(false);
        }

        // 功能：点「再来一次」时转发给外部回调。
        // 入参：无。
        // 返回：无。
        // 步骤：直接 Invoke 记下的回调（回调由 RuntimeNetDriver 提供）
        // 上游/下游：按钮点击；下游 m_onRestart。
        private void OnRestartClicked()
        {
            Action action = m_onRestart;

            if (action != null)
            {
                action();
            }
        }

        // 功能：点「返回世界」时转发给外部回调。
        // 入参：无。
        // 返回：无。
        // 步骤：直接 Invoke 记下的回调
        // 上游/下游：按钮点击；下游 m_onReturn。
        private void OnReturnClicked()
        {
            Action action = m_onReturn;

            if (action != null)
            {
                action();
            }
        }

        // 功能：场景里没有 EventSystem 时补一个（否则按钮点不动）。
        // 入参：无。
        // 返回：无。
        // 步骤：找现有 EventSystem，没有就建一个空物体挂 EventSystem + StandaloneInputModule
        // 上游/下游：Show 调用。
        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            GameObject go = new GameObject("[EventSystem]");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }

        // 功能：建一段居中文字。
        // 入参：parent 父节点；name 物体名；fontSize 字号；anchoredPosition 相对屏幕中心的位置；size 文本框尺寸。
        // 返回：建好的 Text。
        // 步骤：1) 建空物体并设居中锚点 2) 挂 Text 设字体字号对齐 3) 关掉射线检测
        // 上游/下游：Build 调用；下游 LoadBuiltinFont。
        private static Text CreateText(Transform parent, string name, int fontSize, Vector2 anchoredPosition, Vector2 size)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            Text text = go.AddComponent<Text>();
            text.font = LoadBuiltinFont();
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        // 功能：建一个按钮（底色 + 文字 + 点击回调）。
        // 入参：parent 父节点；name 物体名；label 按钮文字；anchoredPosition 位置；onClick 点击回调。
        // 返回：建好的按钮。
        // 步骤：1) 建带 RectTransform 的物体并挂 Image 当底 2) 挂 Button 并设目标图 3) 建子文字 4) 绑点击
        // 上游/下游：Build 调用。
        private static Button CreateButton(Transform parent, string name, string label, Vector2 anchoredPosition, Action onClick)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = new Vector2(320f, 96f);

            Image image = go.AddComponent<Image>();
            image.color = new Color(0.22f, 0.26f, 0.34f, 0.95f);

            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(delegate { onClick(); });

            Text text = CreateText(go.transform, "Label", 40, Vector2.zero, new Vector2(300f, 80f));
            text.text = label;

            return button;
        }

        // 功能：取一个内置字体给 UGUI 用。
        // 入参：无。
        // 返回：内置字体；两个名字都取不到时返回 null（文字不显示，但不会崩）。
        // 步骤：先试 LegacyRuntime.ttf，再试 Arial.ttf。
        // 上游/下游：CreateText 调用。
        private static Font LoadBuiltinFont()
        {
            Font font = Resources.GetBuiltinResource<Font>(BuiltinFontName);

            if (font == null)
            {
                font = Resources.GetBuiltinResource<Font>(LegacyBuiltinFontName);
            }

            return font;
        }
    }
}
