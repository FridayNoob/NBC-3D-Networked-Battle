// ============================================================================
//  QuestPanel —— 任务面板（**只负责"摆"**，算什么在 `QuestPanelModel`）
//  项目：3D联网战斗Demo   对应：M2-D（任务 UI，V6）
//
//  ---------------------------------------------------------------------------
//  它遵守 A9 定的那套面板规矩（一条都不新发明）
//  ---------------------------------------------------------------------------
//  · **控件按名字索引**（`RequireControl<T>("Title")`）：A9 的 `BasePanel` 会一次性收集
//    子物体上的所有 `UIBehaviour`，按 GameObject 名字建索引。
//    名字找不到时 `RequireControl` **抛带指引的异常** —— 这正是 A9 那句
//    "目标不是检测到错误，而是**让错误自己说出来**"。
//  · **不写 `Awake`**（A9 的硬约定：`BasePanel` 用公开的 `Initialize()` 做初始化，
//    因为 **EditMode 下 Unity 根本不调用 `Awake`**，依赖它的话面板一条都测不了）。
//  · `OnClick(按钮名)` 里**只做转发**：真正的动作与文案在模型那一层。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 动态生成的按钮必须**自己接线**（一个容易踩的点）
//  ---------------------------------------------------------------------------
//  `BasePanel.CollectControls()` 是在 `Initialize()` 时**扫一次**，而且它是
//  `button.onClick.AddListener(() => OnClick(那个 GameObject 的名字))` ——
//  **名字是收集那一刻的**。所以我这里 `Instantiate` 出来的行按钮：
//      · 名字要设成 `Accept_3004` 这种（模型给的契约名）
//      · **监听要自己 Add**（克隆体不会带上模板的监听）
//  漏了第二件，表现是"按钮点下去没反应"，而且不报错。
//
//  ---------------------------------------------------------------------------
//  预制体要摆哪些东西（摆一次，清单见 Docs\22 D 组）
//  ---------------------------------------------------------------------------
//      QuestPanel            （挂本脚本 + 一个 Image 之类的底）
//      ├─ Title              Text          —— 标题
//      ├─ Message            Text          —— "上一次操作的结果"（失败原因会显示在这儿）
//      ├─ OfferList          VerticalLayoutGroup —— 可接任务的容器
//      ├─ TrackingList       VerticalLayoutGroup —— 已接任务的容器
//      └─ RowTemplate        Button（**不勾选激活**）+ 一个子 Text
//
//  ⚠️ 容器的类型特意选 `VerticalLayoutGroup`：它**继承自 UIBehaviour**，所以会被
//     `CollectControls` 收集到（空 GameObject 不会被收集）；而且"列表容器本来就该有
//     竖排布局"，不是为了能收集才硬加的组件。
//
//  ⚠️ 行里的文字用 `GetComponentInChildren<Text>()` 取，**不按名字 Find**：
//     子物体叫什么名字是搭预制体的人随手决定的，代码不该依赖它（FW-10 的同一类问题）。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.UI;
using NBC.Game.Quest;
using UnityEngine;
using UnityEngine.UI;

namespace NBC.Game.UI
{
    /// <summary>任务面板：接取列表 + 追踪列表 + 交付按钮。</summary>
    public sealed class QuestPanel : BasePanel
    {
        /// <summary>标题控件的名字（预制体里按这个名字找）。</summary>
        public const string TitleControl = "Title";

        /// <summary>消息控件的名字。</summary>
        public const string MessageControl = "Message";

        /// <summary>可接列表容器的名字。</summary>
        public const string OfferListControl = "OfferList";

        /// <summary>已接列表容器的名字。</summary>
        public const string TrackingListControl = "TrackingList";

        /// <summary>行模板的名字（一个不激活的 Button）。</summary>
        public const string RowTemplateControl = "RowTemplate";

        /// <summary>标题控件。</summary>
        private Text m_title;

        /// <summary>消息控件（显示上一次操作的结果/失败原因）。</summary>
        private Text m_message;

        /// <summary>可接列表的父节点。</summary>
        private RectTransform m_offerList;

        /// <summary>已接列表的父节点。</summary>
        private RectTransform m_trackingList;

        /// <summary>行模板。</summary>
        private Button m_rowTemplate;

        /// <summary>行模板里的文字（克隆出来的行都用它）。</summary>
        private Text m_rowTemplateLabel;

        /// <summary>视图模型（`Bind` 之后才有）。</summary>
        private QuestPanelModel m_model;

        /// <summary>复用的可接行缓冲。</summary>
        private readonly List<QuestRow> m_offerRows = new List<QuestRow>();

        /// <summary>复用的已接行缓冲。</summary>
        private readonly List<QuestRow> m_trackingRows = new List<QuestRow>();

        /// <summary>已经生成出来的行数（**最近一次刷新**的合计；测试/调试用）。</summary>
        private int m_spawnedRowCount;

        /// <summary>订阅过任务事件没有（退订要用）。</summary>
        private bool m_subscribed;

        /// <summary>最近一次刷新生成了几行（两份列表合计）。</summary>
        public int SpawnedRowCount
        {
            get { return m_spawnedRowCount; }
        }

        /// <summary>拿到的模型（没绑时为 null）。</summary>
        public QuestPanelModel Model
        {
            get { return m_model; }
        }

        // ====================================================================
        //  BasePanel 的钩子
        // ====================================================================

        /// <summary>初始化：把控件按名字取出来（取不到就**当场报清楚**）。</summary>
        protected override void OnInit()
        {
            m_title = RequireControl<Text>(TitleControl);
            m_message = RequireControl<Text>(MessageControl);

            m_offerList = RequireControl<VerticalLayoutGroup>(OfferListControl).transform as RectTransform;
            m_trackingList = RequireControl<VerticalLayoutGroup>(TrackingListControl).transform as RectTransform;

            m_rowTemplate = RequireControl<Button>(RowTemplateControl);
            m_rowTemplateLabel = m_rowTemplate.GetComponentInChildren<Text>(true);

            if (m_rowTemplateLabel == null)
            {
                // 这是**预制体搭错了**，不是运行期数据问题 —— 说清怎么修
                Debug.LogError(
                    "[QuestPanel] 行模板「" + RowTemplateControl + "」下面没有 Text 子物体。\n" +
                    "请给模板按钮加一个子 Text（行里的文字会写在它上面）。\n" +
                    "（代码按组件找，不按子物体名字找 —— 名字由搭预制体的人定。）");
            }

            // 模板只是用来克隆的，本体不能显示、也不能被点到
            m_rowTemplate.gameObject.SetActive(false);
        }

        /// <summary>按钮点击：只做转发（动作与文案都在模型里）。</summary>
        /// <param name="buttonName">按钮名（模板名会被忽略）。</param>
        protected override void OnClick(string buttonName)
        {
            if (buttonName == RowTemplateControl)
            {
                return;   // 模板本体（正常情况点不到，双保险）
            }

            if (m_model == null)
            {
                Debug.LogWarning("[QuestPanel] 还没绑定模型（`Bind`），点击被忽略：" + buttonName);
                return;
            }

            m_model.HandleAction(buttonName);
            Refresh();
        }

        /// <summary>面板被销毁时退订（**订阅不退订是 A4/A8 反复踩的坑**）。</summary>
        private void OnDestroy()
        {
            Unsubscribe();
        }

        // ====================================================================
        //  绑定与刷新
        // ====================================================================

        /// <summary>
        /// 绑定视图模型并刷新一次。
        /// <para>绑定后本面板会**自己订阅任务事件**（接取/进度变化/完成/交付），
        /// 于是进度涨了界面会自己更新，不需要调用方每次手动刷新。</para>
        /// </summary>
        /// <param name="model">视图模型（不能为 null）。</param>
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

        /// <summary>按当前模型重画两份列表（可以随时手动调）。</summary>
        public void Refresh()
        {
            if (!IsInitialized)
            {
                // 还没初始化就谈"重画"没有意义（控件都还没收集）
                return;
            }

            if (m_title != null)
            {
                m_title.text = "任务";
            }

            if (m_message != null)
            {
                m_message.text = m_model == null ? string.Empty : m_model.LastMessage;
            }

            if (m_model == null)
            {
                return;
            }

            m_model.CopyOfferRows(m_offerRows);
            m_model.CopyTrackingRows(m_trackingRows);

            // ⚠️ 计数在**刷新开始**时归零：两份列表加起来才是"SpawnedRowCount"
            m_spawnedRowCount = 0;

            RebuildRows(m_offerRows, m_offerList);
            RebuildRows(m_trackingRows, m_trackingList);
        }

        // ====================================================================
        //  内部
        // ====================================================================

        /// <summary>
        /// 把一份行数据铺到某个容器里。
        /// <para>
        /// ⚠️ **只清这个容器里的行**（第一版我清的是"全局生成列表"，
        /// 于是第二次调用会把第一份列表刚生成的行也清掉 —— 那份列表就空了，
        /// 而且看起来像"可接任务没显示"这种玄学问题）。
        /// </para>
        /// </summary>
        /// <param name="rows">行数据。</param>
        /// <param name="parent">容器（**行模板不能放在它下面**，否则会被当行清掉）。</param>
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

                // 名字就是**按钮名**（契约：Accept_3004 / Submit_3004）——
                // 于是"哪个按钮对应哪个任务"在结构上就不可能错位
                button.name = row.ActionName;
                button.gameObject.SetActive(true);
                button.interactable = row.ActionEnabled;

                if (m_rowTemplateLabel != null)
                {
                    Text label = button.GetComponentInChildren<Text>(true);
                    label.text = row.Title + "\n" + row.Detail + "\n[" + row.ActionLabel + "]";
                }

                // ⚠️ 克隆体**不会带上模板的点击监听**，必须自己接（见文件头说明）
                string actionName = row.ActionName;   // 捕获局部变量，别捕获循环变量
                button.onClick.AddListener(() => OnClick(actionName));

                m_spawnedRowCount++;
            }
        }

        /// <summary>清掉某个容器里现有的行（**编辑器里要用 DestroyImmediate**，否则测试里它们还在）。</summary>
        /// <param name="parent">容器。</param>
        private static void ClearRowsIn(RectTransform parent)
        {
            // 从后往前删：正着删会让下标错位
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                GameObject child = parent.GetChild(i).gameObject;

                if (Application.isPlaying)
                {
                    Destroy(child);
                }
                else
                {
                    // EditMode 下 `Destroy` 是**延迟**的（要等一帧），而测试里那一帧不会来
                    DestroyImmediate(child);
                }
            }
        }

        /// <summary>订阅任务事件（重复调用无害）。</summary>
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

        /// <summary>退订任务事件（重复调用无害）。</summary>
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

        /// <summary>任务有任何变化就重画。</summary>
        /// <param name="questId">任务编号（这里用不到，只为对上事件签名）。</param>
        private void OnQuestChanged(int questId)
        {
            Refresh();
        }
    }
}
