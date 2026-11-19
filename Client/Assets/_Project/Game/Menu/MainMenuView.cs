// 这个文件里有：
// MainMenuView：主菜单的运行时 UGUI（标题、昵称输入框、剑士卡片、三个按钮、提示行），只负责画和收集输入。

using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NBC.Game.Menu
{
    /// <summary>
    /// 主菜单的运行时界面。
    /// <para>主要功能：用代码搭一块 Canvas，摆好标题、昵称输入框、剑士卡片与三个按钮，并把点击转成回调。</para>
    /// <para>主要数据成员：m_canvas（整块 Canvas）、m_nameInput（昵称输入框）、m_startLabel（开始按钮文字）、m_hint（提示行）。</para>
    /// <para>主要外部方法：Bind（绑三个回调）、ReadName / SetName（读写昵称）、SetStartLabel / SetHint（改文字）、SetStartInteractable（按钮可用性）。</para>
    /// <para>谁在用它：MainMenuController（建它、绑回调、按状态刷文字）。</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuView : MonoBehaviour
    {
        private const string BuiltinFontName = "LegacyRuntime.ttf";
        private const string LegacyBuiltinFontName = "Arial.ttf";

        private Canvas m_canvas;
        private InputField m_nameInput;
        private Text m_startLabel;
        private Button m_startButton;
        private Text m_hint;

        /// <summary>
        /// 把三个按钮接到回调上。
        /// </summary>
        /// <param name="onStart">点「开始游戏」。</param>
        /// <param name="onSettings">点「设置」。</param>
        /// <param name="onQuit">点「退出」。</param>
        public void Bind(Action onStart, Action onSettings, Action onQuit)
        {
            BuildIfNeeded();

            m_startButton.onClick.RemoveAllListeners();
            m_startButton.onClick.AddListener(() => { if (onStart != null) { onStart(); } });

            // 设置与退出按钮在 Build 里各存了一个 Button 字段
            m_settingsButton.onClick.RemoveAllListeners();
            m_settingsButton.onClick.AddListener(() => { if (onSettings != null) { onSettings(); } });

            m_quitButton.onClick.RemoveAllListeners();
            m_quitButton.onClick.AddListener(() => { if (onQuit != null) { onQuit(); } });
        }

        private Button m_settingsButton;
        private Button m_quitButton;

        /// <summary>读输入框里的原始文本。</summary>
        /// <returns>输入框文本；视图还没建好时返回空串。</returns>
        public string ReadName()
        {
            return m_nameInput != null ? m_nameInput.text : string.Empty;
        }

        /// <summary>把名字写进输入框（启动时回填上次昵称）。</summary>
        /// <param name="name">要显示的昵称。</param>
        public void SetName(string name)
        {
            BuildIfNeeded();

            if (m_nameInput != null)
            {
                m_nameInput.text = name ?? string.Empty;
            }
        }

        /// <summary>改开始按钮上的文字（空闲「开始游戏」、连接中「连接中…」）。</summary>
        /// <param name="text">新文字。</param>
        public void SetStartLabel(string text)
        {
            if (m_startLabel != null)
            {
                m_startLabel.text = text;
            }
        }

        /// <summary>设开始按钮能不能点。</summary>
        /// <param name="interactable">true 可点。</param>
        public void SetStartInteractable(bool interactable)
        {
            if (m_startButton != null)
            {
                m_startButton.interactable = interactable;
            }
        }

        /// <summary>设置按钮下方的提示（空串表示隐藏）。</summary>
        /// <param name="hint">提示文字。</param>
        public void SetHint(string hint)
        {
            if (m_hint != null)
            {
                m_hint.text = hint ?? string.Empty;
            }
        }

        // 功能：第一次用到时把界面搭出来（重复调用无副作用）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 已有 Canvas 就直接返回 2) 建 Canvas 与缩放器 3) 摆标题、昵称、卡片、按钮、提示。
        // 上游/下游：Bind / SetName 调用；下游 CreateText / CreateButton / EnsureEventSystem。
        private void BuildIfNeeded()
        {
            if (m_canvas != null)
            {
                return;
            }

            EnsureEventSystem();

            GameObject canvasObject = new GameObject("MenuCanvas");
            canvasObject.transform.SetParent(transform, false);

            m_canvas = canvasObject.AddComponent<Canvas>();
            m_canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();

            CreateBackground(canvasObject.transform);

            CreateText(canvasObject.transform, "Title", "3D 联网战斗 Demo", 84,
                new Vector2(0f, 320f), new Vector2(1400f, 120f), TextAnchor.MiddleCenter);

            CreateText(canvasObject.transform, "NameLabel", "昵称", 36,
                new Vector2(-360f, 150f), new Vector2(160f, 60f), TextAnchor.MiddleRight);

            m_nameInput = CreateInput(canvasObject.transform, new Vector2(120f, 150f), new Vector2(520f, 64f));

            CreateHeroCard(canvasObject.transform);

            m_startButton = CreateButton(canvasObject.transform, "StartButton", "开始游戏",
                new Vector2(0f, -245f), new Vector2(360f, 84f), out m_startLabel);
            m_settingsButton = CreateButton(canvasObject.transform, "SettingsButton", "设置",
                new Vector2(0f, -340f), new Vector2(360f, 76f), out _);
            m_quitButton = CreateButton(canvasObject.transform, "QuitButton", "退出",
                new Vector2(0f, -430f), new Vector2(360f, 76f), out _);

            m_hint = CreateText(canvasObject.transform, "Hint", string.Empty, 28,
                new Vector2(0f, -500f), new Vector2(1400f, 44f), TextAnchor.MiddleCenter);
            m_hint.color = new Color(1f, 0.78f, 0.3f, 1f);
        }

        // 功能：铺一层深色背景，避免主菜单直接露着天空盒看不清字。
        // 入参：parent 父节点。
        // 返回：无。
        private static void CreateBackground(Transform parent)
        {
            GameObject go = new GameObject("Background");
            go.transform.SetParent(parent, false);

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Image image = go.AddComponent<Image>();
            image.color = new Color(0.06f, 0.08f, 0.12f, 0.92f);
            image.raycastTarget = false;
        }

        // 功能：建一张「剑士」卡片，表示当前只有一个可选角色（1001）。
        // 入参：parent 父节点。
        // 返回：无。
        private static void CreateHeroCard(Transform parent)
        {
            GameObject card = new GameObject("HeroCard");
            card.transform.SetParent(parent, false);

            RectTransform rect = card.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, -60f);
            rect.sizeDelta = new Vector2(420f, 180f);

            Image image = card.AddComponent<Image>();
            image.color = new Color(0.16f, 0.22f, 0.32f, 1f);
            image.raycastTarget = false;

            CreateText(card.transform, "HeroName", "剑士（1001）", 44,
                new Vector2(0f, 30f), new Vector2(380f, 60f), TextAnchor.MiddleCenter);
            CreateText(card.transform, "HeroHint", "唯一可选角色 · 已选中", 26,
                new Vector2(0f, -35f), new Vector2(380f, 50f), TextAnchor.MiddleCenter);
        }

        // 功能：建一个单行输入框。
        // 入参：parent 父节点；position 居中偏移；size 尺寸。
        // 返回：输入框组件。
        private static InputField CreateInput(Transform parent, Vector2 position, Vector2 size)
        {
            GameObject go = new GameObject("NameInput");
            go.transform.SetParent(parent, false);

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Image background = go.AddComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.94f);

            GameObject textObject = new GameObject("Text");
            textObject.transform.SetParent(go.transform, false);
            RectTransform textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(12f, 6f);
            textRect.offsetMax = new Vector2(-12f, -6f);

            Text text = textObject.AddComponent<Text>();
            text.font = LoadBuiltinFont();
            text.fontSize = 34;
            text.color = new Color(0.08f, 0.08f, 0.1f, 1f);
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;

            InputField input = go.AddComponent<InputField>();
            input.targetGraphic = background;
            input.textComponent = text;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = MainMenuState.MaxNameLength;
            return input;
        }

        // 功能：建一个带文字的按钮。
        // 入参：parent 父节点；name 物体名；label 按钮文字；position 居中偏移；size 尺寸；labelText 返回文字组件。
        // 返回：按钮组件。
        private static Button CreateButton(Transform parent, string name, string label,
            Vector2 position, Vector2 size, out Text labelText)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Image image = go.AddComponent<Image>();
            image.color = new Color(0.22f, 0.42f, 0.72f, 1f);

            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;

            labelText = CreateText(go.transform, "Label", label, 36,
                Vector2.zero, size, TextAnchor.MiddleCenter);
            return button;
        }

        // 功能：建一段居中的文字。
        // 入参：parent 父节点；name 物体名；content 内容；fontSize 字号；position 偏移；size 尺寸；alignment 对齐。
        // 返回：文字组件。
        private static Text CreateText(Transform parent, string name, string content, int fontSize,
            Vector2 position, Vector2 size, TextAnchor alignment)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Text text = go.AddComponent<Text>();
            text.font = LoadBuiltinFont();
            text.fontSize = fontSize;
            text.text = content;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        // 功能：确保场景里有一个 EventSystem，否则按钮收不到点击。
        // 入参：无。
        // 返回：无。
        private static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            GameObject go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }

        // 功能：取一个内置字体给 UGUI 用。
        // 入参：无。
        // 返回：内置字体；两个名字都取不到时返回 null（文字不显示，但不会崩）。
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
