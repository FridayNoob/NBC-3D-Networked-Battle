// 这个文件里有：
//   DeathScreenOverlay：死亡时盖在屏幕上的黑幕、标题、倒计时与提示（运行时建 UGUI，不依赖预制体）。

using UnityEngine;
using UnityEngine.UI;

namespace NBC.Game.UI
{
    // 主要功能：角色倒下后显示全屏黑幕、标题、倒计时秒数与一行提示，复活或退出时隐藏。
    // 主要数据成员：m_canvas / m_background / m_title / m_countdown / m_hint（都是运行时创建的 UGUI 对象）。
    // 主要外部方法：Ensure（拿单例）、Show（显示并给标题与提示）、SetCountdown（更新秒数）、Hide（隐藏）。
    // 谁在用它：PlayerDeathFlow（死亡倒计时与旁观提示都走这里）。
    public sealed class DeathScreenOverlay : MonoBehaviour
    {
        // 内置字体名（Unity 2022 起 Arial.ttf 改叫 LegacyRuntime.ttf）
        private const string BuiltinFontName = "LegacyRuntime.ttf";

        // 旧版 Unity 的内置字体名，第一项取不到时兜底
        private const string LegacyBuiltinFontName = "Arial.ttf";

        // 黑幕不透明度（0~1）：留一点画面能看见角色倒下的姿势
        private const float BackgroundAlpha = 0.82f;

        // 单例（全场景只允许一个）
        private static DeathScreenOverlay s_instance;

        private Canvas m_canvas;  // 全屏画布（ScreenSpaceOverlay，排序值 900）
        private Image m_background;  // 全屏黑幕
        private Text m_title;  // 标题（例如「你已倒下」）
        private Text m_countdown;  // 倒计时秒数（秒）
        private Text m_hint;  // 底部提示

        // 功能：拿到单例；场景里没有就现建一个挂在新建的 GameObject 上。
        // 入参：无。
        // 返回：单例（永不为 null）。
        // 步骤：1) 已有就返回 2) 没有就新建空物体并挂本组件 3) 返回组件。
        // 上游/下游：PlayerDeathFlow 调用；下游 Awake（建 UI）。
        public static DeathScreenOverlay Ensure()
        {
            if (s_instance != null)
            {
                return s_instance;
            }

            GameObject host = new GameObject("[DeathScreenOverlay]");
            return host.AddComponent<DeathScreenOverlay>();
        }

        // 功能：当前是否正在显示黑幕。
        // 入参：无。
        // 返回：true 表示黑幕可见。
        // 步骤：读背景对象的 activeSelf。
        // 上游/下游：PlayerDeathFlow 判断要不要重复 Show。
        public bool IsShowing
        {
            get { return m_background != null && m_background.gameObject.activeSelf; }
        }

        // 功能：登记单例并建好整套 UI。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 已有别的实例就自毁 2) 登记自己 3) 建 UI 并先隐藏。
        // 上游/下游：Unity 生命周期；下游 Build / Hide。
        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            Build();
            Hide();
        }

        // 功能：销毁时把单例清掉，避免下次拿到已销毁对象。
        // 入参：无。
        // 返回：无。
        // 步骤：是自己就置空。
        // 上游/下游：Unity 生命周期。
        private void OnDestroy()
        {
            if (s_instance == this)
            {
                s_instance = null;
            }
        }

        // 功能：显示黑幕并写标题与提示。
        // 入参：title 标题文字；hint 底部提示文字（空串表示不显示）。
        // 返回：无。
        // 步骤：1) 没有 UI 就先建 2) 写标题与提示 3) 倒计时先隐藏（由 SetCountdown 决定要不要出现）4) 打开黑幕。
        // 上游/下游：PlayerDeathFlow 开始倒计时 / 进入旁观时调用。
        public void Show(string title, string hint)
        {
            if (m_background == null)
            {
                Build();
            }

            m_title.text = title;
            m_hint.text = hint;
            m_countdown.text = string.Empty;
            m_background.gameObject.SetActive(true);
        }

        // 功能：更新倒计时秒数（向上取整，小于等于 0 时显示「0」）。
        // 入参：remaining 剩余秒数。
        // 返回：无。
        // 步骤：取整后拼成「N 秒后复活」；负数按 0 处理。
        // 上游/下游：PlayerDeathFlow.Update 每帧调用。
        public void SetCountdown(float remaining)
        {
            if (m_countdown == null)
            {
                return;
            }

            int seconds = Mathf.Max(0, Mathf.CeilToInt(remaining));
            m_countdown.text = seconds + " 秒";
        }

        // 功能：把倒计时文字换成一句状态提示（旁观的「等待其他玩家」用）。
        // 入参：text 要显示的文字。
        // 返回：无。
        // 步骤：直接写文字，不判空以外的逻辑。
        // 上游/下游：PlayerDeathFlow 进入旁观后调用。
        public void SetStatusText(string text)
        {
            if (m_countdown == null)
            {
                return;
            }

            m_countdown.text = text;
        }

        // 功能：隐藏整块黑幕。
        // 入参：无。
        // 返回：无。
        // 步骤：黑幕存在就关掉它的 GameObject。
        // 上游/下游：PlayerDeathFlow 复活 / 退出副本时调用。
        public void Hide()
        {
            if (m_background != null)
            {
                m_background.gameObject.SetActive(false);
            }
        }

        // 功能：一次性搭好 Canvas、黑幕、标题、倒计时和提示。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 建 Canvas 与缩放器 2) 建黑幕铺满 3) 建三段文字并摆好位置 4) 缓存引用。
        // 上游/下游：Awake / Show 调用。
        private void Build()
        {
            if (m_canvas != null)
            {
                return;
            }

            GameObject canvasObject = new GameObject("Canvas");
            canvasObject.transform.SetParent(transform, false);

            m_canvas = canvasObject.AddComponent<Canvas>();
            m_canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_canvas.sortingOrder = 900;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();

            m_background = CreateImage(canvasObject.transform, "Background", new Color(0f, 0f, 0f, BackgroundAlpha));
            m_title = CreateText(canvasObject.transform, "Title", 96, new Vector2(0f, 120f), new Vector2(1600f, 160f));
            m_countdown = CreateText(canvasObject.transform, "Countdown", 72, new Vector2(0f, -40f), new Vector2(1200f, 120f));
            m_hint = CreateText(canvasObject.transform, "Hint", 40, new Vector2(0f, -220f), new Vector2(1600f, 120f));

            m_title.text = string.Empty;
            m_countdown.text = string.Empty;
            m_hint.text = string.Empty;
        }

        // 功能：建一张铺满父节点的单色图片。
        // 入参：parent 父节点；name 物体名；color 颜色。
        // 返回：建好的 Image。
        // 步骤：1) 建空物体并拉伸铺满 2) 挂 Image 并上色 3) 关掉射线检测（不挡点击）。
        // 上游/下游：Build 调用。
        private static Image CreateImage(Transform parent, string name, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Image image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        // 功能：建一段居中文字。
        // 入参：parent 父节点；name 物体名；fontSize 字号；anchoredPosition 相对屏幕中心的位置；size 文本框尺寸。
        // 返回：建好的 Text。
        // 步骤：1) 建空物体并设居中锚点 2) 挂 Text 设置字体、字号、对齐 3) 关掉射线检测。
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
