// 这个文件里有：
//   QuestPanel（类）：任务面板，把模型给的可接 / 已接任务铺成按钮行，点击只转发给模型
//   LabelRef（类）：文字控件的极薄适配，同一套接口同时支持 UGUI Text 与 TextMeshProUGUI

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.UI;
using NBC.Game.Quest;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NBC.Game.UI
{
    /// <summary>任务面板：把模型给的任务铺成按钮行，点按钮转发给模型，由 Boot 装配并绑定。</summary>
    // 主要功能：把 QuestPanelModel 的可接 / 已接任务铺成按钮行，点按钮时把按钮名交给模型处理再重画。
    // 主要数据成员：五个控件名常量与可改的序列化字段、m_panelFont（TMP 字体资产，可空）、两份 QuestRow 缓冲。
    // 主要外部方法：Bind（绑定模型）、Refresh（重画）、SpawnedRowCount 与 Model（只读状态）。
    // 谁在用它：Boot 的 M2DemoBehaviour 装配并调用 Bind；EditMode 的 QuestPanelTests 验按钮名与刷新。
    public sealed class QuestPanel : BasePanel
    {

        // 标题控件的默认物体名，遵循「类型_物体名」命名；只是默认值，Inspector 上可改
        public const string TitleControl = "Text_Title";

        // 消息控件默认名，显示上一次操作的结果（失败原因也在这里）
        public const string MessageControl = "Text_Message";

        // 可接列表容器默认名，物体上要挂 VerticalLayoutGroup
        public const string OfferListControl = "Layout_OfferList";

        // 已接列表容器默认名，物体上要挂 VerticalLayoutGroup
        public const string TrackingListControl = "Layout_TrackingList";

        // 行模板按钮默认名，每一行都由它克隆出来
        public const string RowTemplateControl = "Button_RowTemplate";

        // 标题控件的物体名；默认 TitleControl，可在 Inspector 覆盖
        [Tooltip("标题的物体名（规范：Text_XXX）")]
        [SerializeField] private string m_titleControl = TitleControl;

        // 消息控件的物体名；默认 MessageControl，可在 Inspector 覆盖
        [Tooltip("消息行的物体名（规范：Text_XXX）")]
        [SerializeField] private string m_messageControl = MessageControl;

        // 可接列表容器的物体名；默认 OfferListControl，可在 Inspector 覆盖
        [Tooltip("可接列表容器的物体名（规范：Layout_XXX）")]
        [SerializeField] private string m_offerListControl = OfferListControl;

        // 已接列表容器的物体名；默认 TrackingListControl，可在 Inspector 覆盖
        [Tooltip("已接列表容器的物体名（规范：Layout_XXX）")]
        [SerializeField] private string m_trackingListControl = TrackingListControl;

        // 行模板按钮的物体名；默认 RowTemplateControl，可在 Inspector 覆盖
        [Tooltip("行模板的物体名（规范：Button_XXX）")]
        [SerializeField] private string m_rowTemplateControl = RowTemplateControl;

        // 面板所有文字用的 TMP 字体资产；null = 用 TMP 默认字体，两者都空才用组件自带字体（显式 = null 可免 CS0649）
        [Tooltip("可选：面板文字用的 TMP 字体（留空 = 用 TMP 默认字体）。中文方框就把它设上")]
        [SerializeField] private TMP_FontAsset m_panelFont = null;

        // 标题文字引用；可能是 UGUI Text 或 TextMeshProUGUI，OnInit 里取
        private LabelRef m_title;

        // 消息文字引用；两种文字组件都支持，OnInit 里取
        private LabelRef m_message;

        // 可接列表的父节点；取 VerticalLayoutGroup 所在物体的 RectTransform
        private RectTransform m_offerList;

        // 已接列表的父节点；取 VerticalLayoutGroup 所在物体的 RectTransform
        private RectTransform m_trackingList;

        // 行模板按钮；本体隐藏，只用来 Instantiate 克隆行
        private Button m_rowTemplate;

        // 模板行里的文字引用；克隆行的文字都写在同一位置
        private LabelRef m_rowTemplateLabel;

        // 视图模型；Bind 之前为 null
        private QuestPanelModel m_model;

        // 可接行的复用缓冲；每次 Refresh 由模型覆盖，元素数 = 可接任务数
        private readonly List<QuestRow> m_offerRows = new List<QuestRow>();

        // 已接行的复用缓冲；每次 Refresh 由模型覆盖，元素数 = 已接任务数
        private readonly List<QuestRow> m_trackingRows = new List<QuestRow>();

        // 最近一次刷新生成的行数（两份列表合计，>= 0），测试用它断言
        private int m_spawnedRowCount;

        // 是否已订阅任务事件；用于让订阅与退订成对
        private bool m_subscribed;

        // 字体日志是否已打；只打一次，免得每帧刷屏
        private bool m_fontLogged;

        // 无字体警告是否已打；只打一次
        private bool m_fontWarned;

        // 最近一次刷新生成的行数（两份列表合计），>= 0
        public int SpawnedRowCount
        {
            get { return m_spawnedRowCount; }
        }

        // 当前绑定的视图模型；未 Bind 时为 null
        public QuestPanelModel Model
        {
            get { return m_model; }
        }


        // 功能：按物体名取出标题、消息、两个列表容器与行模板，模板隐藏并套上字体。
        // 入参：无（控件名取自 Inspector 上的五个序列化字段）
        // 返回：无
        // 步骤：
        //   1) 两个文字控件走 ResolveLabel，取不到时抛出带修法的异常
        //   2) 两个容器按 VerticalLayoutGroup 取，再取它的 transform 当 RectTransform
        //   3) 行模板取 Button，并在它下面找文字组件
        //   4) 找不到文字只报错不抛，模板搭错时标题与消息还能显示
        //   5) 模板本体隐藏后套字体，否则克隆出来的行照样是方框
        // 上游：BasePanel.Initialize（Boot 与 EditMode 测试都从这里进来）
        // 下游：ResolveLabel、RequireControl、LabelRef.FindIn、ApplyFont
        // 注意：不写 Awake：EditMode 下 Unity 不调用 Awake，初始化必须走公开的 Initialize，否则面板一条都测不了。
        // 注意：容器特意选 VerticalLayoutGroup：它继承 UIBehaviour 才会被 CollectControls 收集（空物体收不到），列表本来也该竖排。
        // 注意：文字控件按 Graphic 基类取，两种文字组件都是它的子类；只认 Text 会报找不到标题。
        protected override void OnInit()
        {
            m_title = ResolveLabel(m_titleControl, "标题");
            m_message = ResolveLabel(m_messageControl, "消息行");

            m_offerList = RequireControl<VerticalLayoutGroup>(m_offerListControl).transform as RectTransform;
            m_trackingList = RequireControl<VerticalLayoutGroup>(m_trackingListControl).transform as RectTransform;

            m_rowTemplate = RequireControl<Button>(m_rowTemplateControl);
            m_rowTemplateLabel = LabelRef.FindIn(m_rowTemplate.gameObject);

            if (m_rowTemplateLabel == null)
            {
                Debug.LogError(
                    "[QuestPanel] 行模板「" + m_rowTemplateControl + "」下面没有文字组件。\n" +
                    "请给模板按钮加一个子物体，挂 `Text` 或 `TextMeshProUGUI` 都行" +
                    "（行里的文字会写在它上面）。\n" +
                    "（代码按组件找，不按子物体名字找 —— 名字由搭预制体的人定。）");
            }

            m_rowTemplate.gameObject.SetActive(false);

            ApplyFont(m_rowTemplateLabel);
        }

        // 功能：把按钮名转给模型执行动作，然后重画。
        // 入参：buttonName 按钮名，即行按钮的 ActionName（如 Accept_3004）；等于模板名时忽略
        // 返回：无
        // 步骤：
        //   1) 先挡模板名：模板本体正常点不到，这是双保险
        //   2) 没绑模型只警告不改状态，避免空引用
        //   3) 交给 QuestPanelModel.HandleAction，再 Refresh 让界面跟上
        // 上游：BasePanel 按物体名接的按钮监听、行按钮自己 Add 的监听
        // 下游：QuestPanelModel.HandleAction、Refresh
        protected override void OnClick(string buttonName)
        {
            if (buttonName == m_rowTemplateControl)
            {
                return;
            }

            if (m_model == null)
            {
                Debug.LogWarning("[QuestPanel] 还没绑定模型（`Bind`），点击被忽略：" + buttonName);
                return;
            }

            m_model.HandleAction(buttonName);
            Refresh();
        }

        // 功能：面板销毁时退订任务事件，免得事件回调打到已销毁的物体上。
        private void OnDestroy()
        {
            Unsubscribe();
        }


        /// <summary>绑定视图模型并立刻重画一次，之后任务事件由面板自己订阅刷新。</summary>
        // 功能：记下模型、订阅四个任务事件，然后重画一次。
        // 入参：model 视图模型，不能为 null（null 直接抛 ArgumentNullException）
        // 返回：无
        // 步骤：
        //   1) 先校验 null，避免把空模型记进去以后处处判空
        //   2) 先订阅再刷新，刷新过程中发生的事件不会漏
        // 上游：Boot 的 M2DemoBehaviour.BindQuestPanel；EditMode 的 QuestPanelTests
        // 下游：Subscribe、Refresh
        // 注意：重复 Bind 只是重复订阅（有标志挡着）再重画一次，不会叠出多份监听。
        public void Bind(QuestPanelModel model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model), "[QuestPanel] 模型不能为 null。");
            }

            m_model = model;

            Subscribe();
            Refresh();
        }

        // 功能：按当前模型重画标题、消息与两份任务列表。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没初始化直接返回：控件还没收集，谈重画没有意义
        //   2) 标题固定写「任务」，消息写模型的上一次操作结果
        //   3) 没模型就到此为止，列表保持原样
        //   4) 先把两份行数据拷进缓冲，再各自铺到对应容器
        //   5) 计数在铺之前归零，两份列表累加才是 SpawnedRowCount
        // 上游：Bind、OnClick、OnQuestChanged，也可由外部随时手动调
        // 下游：QuestPanelModel.CopyOfferRows 与 CopyTrackingRows、RebuildRows
        public void Refresh()
        {
            if (!IsInitialized)
            {
                return;
            }

            if (m_title != null)
            {
                m_title.Set("任务");
            }

            if (m_message != null)
            {
                m_message.Set(m_model == null ? string.Empty : m_model.LastMessage);
            }

            if (m_model == null)
            {
                return;
            }

            m_model.CopyOfferRows(m_offerRows);
            m_model.CopyTrackingRows(m_trackingRows);

            m_spawnedRowCount = 0;

            RebuildRows(m_offerRows, m_offerList);
            RebuildRows(m_trackingRows, m_trackingList);
        }


        // 功能：清掉容器里现有的行，再按数据逐条克隆模板并接上点击。
        // 入参：rows 行数据（顺序即显示顺序）；parent 容器（行模板不能放在它下面，否则会被当行清掉）
        // 返回：无
        // 步骤：
        //   1) 模板或容器缺失直接返回，不抛
        //   2) 先清空本容器，再逐条克隆
        //   3) 克隆体名字设成 ActionName，行按钮名因此与任务一一对应
        //   4) 按模板上的文字组件写标题 + 详情 + 动作名三行
        //   5) 用局部变量 actionName 捕获，别把循环变量带进闭包
        //   6) 每生成一行计数加一
        // 上游：Refresh
        // 下游：ClearRowsIn、LabelRef.FindIn、OnClick
        // 注意：克隆体不会带上模板的点击监听，必须自己 Add；漏了表现为按钮点下去没反应，而且不报错。
        // 注意：只清本容器里的行：改清全局生成列表的话，第二次调用会把另一份列表刚生成的行也清掉，看着像可接任务没显示。
        // 注意：行里的文字按组件找（GetComponentInChildren），不按子物体名字 Find，名字由搭预制体的人随手定。
        private void RebuildRows(List<QuestRow> rows, RectTransform parent)
        {
            if (parent == null || m_rowTemplate == null)
            {
                return;
            }

            ClearRowsIn(parent);

            for (int i = 0; i < rows.Count; i++)
            {
                QuestRow row = rows[i];

                Button button = Instantiate(m_rowTemplate, parent);

                button.name = row.ActionName;
                button.gameObject.SetActive(true);
                button.interactable = row.ActionEnabled;

                if (m_rowTemplateLabel != null)
                {
                    LabelRef label = LabelRef.FindIn(button.gameObject);

                    if (label != null)
                    {
                        label.Set(row.Title + "\n" + row.Detail + "\n[" + row.ActionLabel + "]");
                    }
                }

                string actionName = row.ActionName;
                button.onClick.AddListener(() => OnClick(actionName));

                m_spawnedRowCount++;
            }
        }

        // 功能：删掉某个容器下现有的所有行。
        // 入参：parent 容器（调用方已判过非空）
        // 返回：无
        // 步骤：
        //   1) 从后往前删，正着删会让下标错位
        //   2) 运行期用 Destroy，交给 Unity 在帧末回收
        //   3) 非运行期用 DestroyImmediate：EditMode 下 Destroy 要等一帧，而测试里那一帧不会来
        // 上游：RebuildRows
        // 下游：UnityEngine.Object.Destroy 与 DestroyImmediate
        // 注意：编辑器与测试里必须 DestroyImmediate，否则旧行还在，下一次断言会看到多出来的行。
        private static void ClearRowsIn(RectTransform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                GameObject child = parent.GetChild(i).gameObject;

                if (Application.isPlaying)
                {
                    Destroy(child);
                }
                else
                {
                    DestroyImmediate(child);
                }
            }
        }

        // 功能：订阅四个任务事件，重复调用不会重复订阅。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已订阅就直接返回
        //   2) 先置位，再逐个挂四个事件
        //   3) 四个事件共用 OnQuestChanged，载荷都是 int 任务编号
        // 上游：Bind
        // 下游：EventCenter.AddEventListener、OnQuestChanged
        // 注意：挂上就必须有对应的退订（见 Unsubscribe），否则面板销毁后回调照样进来。
        private void Subscribe()
        {
            if (m_subscribed)
            {
                return;
            }

            m_subscribed = true;

            EventCenter.Instance.AddEventListener<int>(QuestEvents.Accepted, OnQuestChanged);
            EventCenter.Instance.AddEventListener<int>(QuestEvents.ProgressChanged, OnQuestChanged);
            EventCenter.Instance.AddEventListener<int>(QuestEvents.Completed, OnQuestChanged);
            EventCenter.Instance.AddEventListener<int>(QuestEvents.Submitted, OnQuestChanged);
        }

        // 功能：退掉 Subscribe 挂的四个任务事件，重复调用不会多退。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没订阅就直接返回
        //   2) 先清标志，再逐个移除四个事件
        // 上游：OnDestroy
        // 下游：EventCenter.RemoveEventListener
        // 注意：必须与 Subscribe 一一对应，漏掉一个就会在销毁后继续收到回调。
        private void Unsubscribe()
        {
            if (!m_subscribed)
            {
                return;
            }

            m_subscribed = false;

            EventCenter.Instance.RemoveEventListener<int>(QuestEvents.Accepted, OnQuestChanged);
            EventCenter.Instance.RemoveEventListener<int>(QuestEvents.ProgressChanged, OnQuestChanged);
            EventCenter.Instance.RemoveEventListener<int>(QuestEvents.Completed, OnQuestChanged);
            EventCenter.Instance.RemoveEventListener<int>(QuestEvents.Submitted, OnQuestChanged);
        }

        // 功能：按名字取一个文字控件，取不到就抛带修法说明的异常。
        // 入参：controlName 控件物体名；what 这是什么（拼进报错，给人看）
        // 返回：文字控件的引用，一定不是 null
        // 步骤：
        //   1) 先按 Graphic 基类取：名字不对时 RequireControl 自己抛，并列出现有控件名
        //   2) 在那个物体上找文字组件，两种都认
        //   3) 名字对却没有文字组件属于另一种错，单列一条消息说清该挂什么
        //   4) 取到后先套字体再返回
        // 上游：OnInit
        // 下游：RequireControl、LabelRef.FindIn、ApplyFont
        // 注意：报错要带物体名与修法：目标不是检测到错误，而是让错误自己说出来。
        private LabelRef ResolveLabel(string controlName, string what)
        {
            Graphic graphic = RequireControl<Graphic>(controlName);

            LabelRef label = LabelRef.FindIn(graphic.gameObject);

            if (label == null)
            {
                throw new InvalidOperationException(
                    "[QuestPanel] 名字 «" + controlName + "» 找到了，但" + what +
                    "那个物体上挂的是 " + graphic.GetType().Name +
                    "，不是文字组件（`Text` 或 `TextMeshProUGUI`）。\n" +
                    "请给它挂一个文字组件，或者把面板上对应的控件名改成另一个物体的名字。");
            }

            ApplyFont(label);
            return label;
        }

        // 功能：把一个 TMP 字体直接写给文字控件，不走 TMP 的兜底链。
        // 入参：label 文字控件，可以为 null（null 直接返回）
        // 返回：无
        // 步骤：
        //   1) 取值顺序：Inspector 上的 m_panelFont，其次 TMP 默认字体，最后什么都不做
        //   2) 都没有时只警告一次，并给出两条修法
        //   3) 有字体就写进组件，并只打一次日志（字体名、模式、源字体）
        // 上游：OnInit（标题、消息、模板行）、ResolveLabel
        // 下游：LabelRef.SetFont
        // 注意：中文一开始显示成方框，是兜底链任何一环没接上都会静默失败；显式指定后一次解决，克隆出来的行也跟着走。
        // 注意：日志里若图集的 m_UsedGlyphRects 一直是空的，那是字体文件或模式的问题，不是接线问题。
        private void ApplyFont(LabelRef label)
        {
            if (label == null)
            {
                return;
            }

            TMP_FontAsset font = m_panelFont != null ? m_panelFont : TMP_Settings.defaultFontAsset;

            if (font == null)
            {
                if (!m_fontWarned)
                {
                    m_fontWarned = true;
                    Debug.LogWarning(
                        "[QuestPanel] 没有可用的 TMP 字体（Inspector 上没拖、TMP 默认字体也是空的）——\n" +
                        "中文可能显示成方框。两条路：① 把中文字体资产拖到面板的「Panel Font」字段上；\n" +
                        "② 跑一次 `Tools/NBC/UI/② 设为 TMP 默认字体 + 加为兜底字体`。");
                }

                return;
            }

            label.SetFont(font);

            if (!m_fontLogged)
            {
                m_fontLogged = true;
                Debug.Log("[QuestPanel] 文字字体 = " + font.name +
                          "（模式 " + font.atlasPopulationMode + "）；源字体 = " +
                          (font.sourceFontFile != null ? font.sourceFontFile.name : "（空）") + "。\n" +
                          "若中文仍是方框，且那份 .asset 里 `m_UsedGlyphRects` 一直是空的 —— " +
                          "那是**字体文件/模式**的问题（TMP 动态加载取不到字形），不是接线问题。");
            }
        }

        // 功能：收到任一任务事件就整页重画（事件载荷的任务编号用不到）。
        private void OnQuestChanged(int questId)
        {
            Refresh();
        }


        // 主要功能：包住一个 UGUI Text 或 TMP_Text，对外只暴露设文字与设字体。
        // 主要数据成员：m_legacy（UGUI 文字）、m_tmp（TMP 文字），两者二选一。
        // 主要外部方法：FindIn（在物体里找）、Set（写文字）、SetFont（只对 TMP 生效）。
        // 谁在用它：QuestPanel 的 OnInit、Refresh、RebuildRows、ResolveLabel；私有嵌套，不跨程序集。
        // 注意：项目里两种文字组件都有，硬要 Text 时 TMP 的预制体会直接报找不到控件。
        private sealed class LabelRef
        {
            // UGUI 文字组件；与 m_tmp 二选一，另一个为 null
            private readonly Text m_legacy;

            // TMP 文字组件；与 m_legacy 二选一，另一个为 null
            private readonly TMP_Text m_tmp;

            // 功能：造一个引用，两个参数最多一个非空。
            // 入参：legacy UGUI 文字；tmp TMP 文字（两个都为 null 请走 FindIn）
            // 返回：无
            // 步骤：只做赋值，不校验，二选一由调用方保证
            // 上游：FindIn
            // 下游：无
            private LabelRef(Text legacy, TMP_Text tmp)
            {
                m_legacy = legacy;
                m_tmp = tmp;
            }

            // 功能：把一段文字写进底层组件，TMP 优先。
            // 入参：text 要显示的文字
            // 返回：无
            // 步骤：
            //   1) 有 TMP 就写它并返回
            //   2) 否则写 UGUI Text
            //   3) 两个都空就什么都不做
            // 上游：Refresh（标题与消息）、RebuildRows（行文字）
            // 下游：TMP_Text.text 与 Text.text
            public void Set(string text)
            {
                if (m_tmp != null)
                {
                    m_tmp.text = text;
                    return;
                }

                if (m_legacy != null)
                {
                    m_legacy.text = text;
                }
            }

            // 功能：把 TMP 字体资产写给组件；UGUI 的 Text 刻意不管。
            // 入参：font TMP 字体资产，null 时什么都不做
            // 返回：无
            // 步骤：只有 m_tmp 与 font 都非空才写，避免用空值盖掉组件自带的字体
            // 上游：ApplyFont
            // 下游：TMP_Text.font
            // 注意：UGUI Text 用的是 Font，两套不通用；这里只改字体资产，prefab 上的材质会跟着字体走。
            public void SetFont(TMP_FontAsset font)
            {
                if (m_tmp != null && font != null)
                {
                    m_tmp.font = font;
                }
            }

            // 功能：在一个物体及其子物体里找文字组件，TMP 优先。
            // 入参：owner 物体，null 直接返回 null
            // 返回：找到的引用；两种文字组件都没有时返回 null
            // 步骤：
            //   1) 先找 TMP_Text，命中就包装返回
            //   2) 再找 UGUI Text
            //   3) 都没有返回 null
            // 上游：OnInit（模板行）、RebuildRows（克隆行）、ResolveLabel
            // 下游：GetComponentInChildren
            // 注意：必须传 includeInactive = true：行模板本身是未激活的，不传这个参数一行都取不到。
            // 注意：判空只能写 if (x == null)，不能写 a ?? b：Unity 的假 null 不走 ?? 的重载。
            public static LabelRef FindIn(GameObject owner)
            {
                if (owner == null)
                {
                    return null;
                }

                TMP_Text tmp = owner.GetComponentInChildren<TMP_Text>(true);

                if (tmp != null)
                {
                    return new LabelRef(null, tmp);
                }

                Text legacy = owner.GetComponentInChildren<Text>(true);

                if (legacy != null)
                {
                    return new LabelRef(legacy, null);
                }

                return null;
            }
        }
    }
}
