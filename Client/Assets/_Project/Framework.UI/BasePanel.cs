// ============================================================================
//  NBC.Framework.UI · 面板基类
//  对应需求：FW-M09
//  缺陷编号：P-08（`Awake` 是 `protected virtual` 且承担关键初始化）
//  完整记录：Docs/06-框架改造记录.md §四 P-08
//
//  ---------------------------------------------------------------------------
//  它替你干什么（原版就有的好处，保留）
//  ---------------------------------------------------------------------------
//  你做一个面板预制体，挂上继承自本类的脚本，剩下的：
//    · 面板里的 Button / Image / Text / Toggle / Slider / ScrollRect / InputField
//      **自动被收集**，按 GameObject 名字取用（`GetControl<Button>("BtnStart")`）
//    · Button 的点击、Toggle 的勾选**自动接到** `OnClick` / `OnValueChanged`
//  你不用写一堆 `[SerializeField]` 或者 `transform.Find`。
//
//  ---------------------------------------------------------------------------
//  原版错在哪（P-08）
//  ---------------------------------------------------------------------------
//      public class BasePanel : MonoBehaviour
//      {
//          protected virtual void Awake () {        // ← protected virtual
//              FindChildrenControl<Button>();         // ← 7 次全树扫描
//              FindChildrenControl<Image>();
//              ... 还有 5 次
//          }
//      }
//
//  子类只要也写一个 `Awake` 而忘了 `base.Awake()`：
//    · `controlDic` 永远是空的 → `GetControl<T>` 全返回 null
//    · 按钮的监听也没注册 → **点任何按钮都没反应**
//    · **而且不报错**（C# 不强制要求调基类虚方法）
//
//  ---------------------------------------------------------------------------
//  修法：**根本不让 `Awake` 参与初始化**（比"改成 sealed"更彻底）
//  ---------------------------------------------------------------------------
//  `Awake` 是 Unity 按名字反射调用的，C# 的 `sealed` / `private` 都挡不住
//  "子类再声明一个同名方法"——Unity 只会调用最派生那一个。
//  所以**改成由 `UIManager` 显式调用 `Initialize()`**：
//    · 框架不再依赖 `Awake`，子类写不写 `Awake` 都不会破坏初始化
//    · 顺带把原版 **7 次全树扫描合并成 1 次**（`GetComponentsInChildren<UIBehaviour>` 再分桶）
//    · 副作用红利：`Initialize()` 可以在 EditMode 测试里**直接调**，
//      不用等 Unity 的播放循环（EditMode 下 `Awake` 根本不会被调用，见 Docs/06 §9.7）
//
//  ⚠️ **子类要用自己的 `Awake` 也没关系** —— 只要框架不依赖它。
//     要写"面板被创建时的初始化"，覆写 `OnInit()`。
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NBC.Framework.UI
{
    /// <summary>
    /// 所有 UI 面板的基类。**框架通过 <see cref="Initialize"/> 初始化它，不依赖 <c>Awake</c>。**
    /// </summary>
    public class BasePanel : MonoBehaviour
    {
        /// <summary>控件索引：GameObject 名字 到 该名字下的所有 UI 组件。</summary>
        private readonly Dictionary<string, List<UIBehaviour>> m_controls =
            new Dictionary<string, List<UIBehaviour>>();

        /// <summary>复用的临时列表（避免每次报错都分配）。</summary>
        private readonly List<string> m_nameBuffer = new List<string>();

        private bool m_initialized;

        /// <summary>
        /// 面板名（由 <c>UIManager</c> 设置）。**只用于报错信息**，让错误能指出是哪个面板。
        /// </summary>
        public string PanelName { get; internal set; }

        /// <summary>是否已经初始化过。</summary>
        public bool IsInitialized
        {
            get { return m_initialized; }
        }

        /// <summary>收集到的控件数量（调试 / 测试用）。</summary>
        public int ControlCount
        {
            get
            {
                int total = 0;
                foreach (KeyValuePair<string, List<UIBehaviour>> pair in m_controls)
                {
                    total += pair.Value.Count;
                }

                return total;
            }
        }

        // ====================================================================
        //  初始化
        // ====================================================================

        /// <summary>
        /// 初始化面板：收集控件、接上按钮 / 勾选框的监听，然后调用 <see cref="OnInit"/>。
        /// <para>
        /// ⚠️ **由 <c>UIManager</c> 在实例化面板之后调用**，不要自己调。
        /// **重复调用是安全的**（第二次直接返回），所以放进对象池反复用也没问题。
        /// </para>
        /// </summary>
        public void Initialize()
        {
            if (m_initialized)
            {
                return;
            }

            m_initialized = true;
            CollectControls();
            OnInit();
        }

        /// <summary>
        /// 收集所有子控件。**一次遍历**（原版是 7 次）。
        /// <para>测试可以直接调用它来检查"收集到了什么"，不需要走 <see cref="Initialize"/>。</para>
        /// </summary>
        public void CollectControls()
        {
            m_controls.Clear();

            // 一次全树扫描，拿到所有 UI 组件（含 Button / Image / Text / Toggle / Slider /
            // ScrollRect / InputField —— 它们都继承自 UIBehaviour）。
            UIBehaviour[] all = GetComponentsInChildren<UIBehaviour>(true);

            for (int i = 0; i < all.Length; i++)
            {
                UIBehaviour control = all[i];

                if (control == null)
                {
                    continue;
                }

                string controlName = control.gameObject.name;

                List<UIBehaviour> bucket;
                if (m_controls.TryGetValue(controlName, out bucket))
                {
                    bucket.Add(control);
                }
                else
                {
                    m_controls.Add(controlName, new List<UIBehaviour> { control });
                }

                WireControl(control, controlName);
            }
        }

        /// <summary>
        /// 给需要"事件"的控件接上默认处理（按钮点击、勾选框变化）。
        /// <para>其他控件（图片、文字、滑动条）只登记、不接线 —— 它们没有统一的"变化"语义。</para>
        /// </summary>
        /// <param name="control">控件。</param>
        /// <param name="controlName">控件所在 GameObject 的名字。</param>
        private void WireControl(UIBehaviour control, string controlName)
        {
            Button button = control as Button;
            if (button != null)
            {
                button.onClick.AddListener(() => OnClick(controlName));
                return;
            }

            Toggle toggle = control as Toggle;
            if (toggle != null)
            {
                toggle.onValueChanged.AddListener(value => OnValueChanged(controlName, value));
            }
        }

        // ====================================================================
        //  子类可覆写的钩子
        // ====================================================================

        /// <summary>
        /// 面板初始化完成时调用（**要写初始化逻辑就覆写这个，别写 `Awake`**）。
        /// </summary>
        protected virtual void OnInit()
        {
        }

        /// <summary>面板被显示时调用。</summary>
        public virtual void ShowMe()
        {
        }

        /// <summary>面板被隐藏时调用。</summary>
        public virtual void HideMe()
        {
        }

        /// <summary>
        /// 某个按钮被点击。**按 GameObject 名字区分**，所以一个 `OnClick` 能处理整个面板的按钮。
        /// </summary>
        /// <param name="buttonName">被点按钮所在 GameObject 的名字。</param>
        protected virtual void OnClick(string buttonName)
        {
        }

        /// <summary>某个勾选框状态变化。</summary>
        /// <param name="toggleName">勾选框所在 GameObject 的名字。</param>
        /// <param name="value">新的勾选状态。</param>
        protected virtual void OnValueChanged(string toggleName, bool value)
        {
        }

        // ====================================================================
        //  取控件
        // ====================================================================

        /// <summary>
        /// 按名字取控件。**没有则返回 null**（与原版行为一致，便于只关心"有没有"的场合）。
        /// <para>
        /// ⚠️ 名字打错时这里**不会报错**，要等到你用它的那一刻才崩，而且报错点离原因很远。
        /// 所以"这个名字必须有"的场合请用 <see cref="RequireControl{T}"/>。
        /// </para>
        /// </summary>
        /// <typeparam name="T">控件类型。</typeparam>
        /// <param name="controlName">GameObject 名字。</param>
        /// <returns>控件；没有则 null。</returns>
        protected T GetControl<T>(string controlName) where T : UIBehaviour
        {
            if (string.IsNullOrEmpty(controlName))
            {
                return null;
            }

            List<UIBehaviour> bucket;
            if (!m_controls.TryGetValue(controlName, out bucket))
            {
                return null;
            }

            for (int i = 0; i < bucket.Count; i++)
            {
                T typed = bucket[i] as T;
                if (typed != null)
                {
                    return typed;
                }
            }

            return null;
        }

        /// <summary>
        /// 按名字取控件；**取不到就当场抛异常**，并在消息里列出这个面板里所有可用的控件名。
        /// <para>
        /// 这是"名字打错 → 后面才崩"的修法：错误发生在**写错的那一行**，
        /// 而且**不用去翻预制体就知道该改成什么**。
        /// </para>
        /// </summary>
        /// <typeparam name="T">控件类型。</typeparam>
        /// <param name="controlName">GameObject 名字。</param>
        /// <returns>控件（一定不是 null）。</returns>
        protected T RequireControl<T>(string controlName) where T : UIBehaviour
        {
            T control = GetControl<T>(controlName);

            if (control != null)
            {
                return control;
            }

            throw new InvalidOperationException(
                "[BasePanel:" + DisplayName + "] 找不到叫 «" + controlName + "» 的 " +
                typeof(T).Name + "。\n" +
                "这个面板里现有这些控件：" + DescribeControls() + "\n" +
                "（请核对大小写与空格 —— 索引按 GameObject 名字精确匹配。）");
        }

        /// <summary>把控件名拷进一个列表（调试面板 / 报错用）。</summary>
        /// <param name="buffer">接收结果的列表（会先清空）。</param>
        public void CopyControlNames(List<string> buffer)
        {
            if (buffer == null)
            {
                return;
            }

            buffer.Clear();

            foreach (KeyValuePair<string, List<UIBehaviour>> pair in m_controls)
            {
                buffer.Add(pair.Key);
            }
        }

        /// <summary>把"名字(类型,类型)"拼成一句人话，供报错使用。</summary>
        /// <returns>控件清单；一个都没有时返回"（空）"。</returns>
        private string DescribeControls()
        {
            if (m_controls.Count == 0)
            {
                return "（空 —— 面板里一个 UI 组件都没有，或控件都挂在了根节点以外的地方）";
            }

            m_nameBuffer.Clear();
            CopyControlNames(m_nameBuffer);

            System.Text.StringBuilder builder = new System.Text.StringBuilder();

            for (int i = 0; i < m_nameBuffer.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                string controlName = m_nameBuffer[i];
                builder.Append(controlName);
                builder.Append('(');

                List<UIBehaviour> bucket = m_controls[controlName];
                for (int j = 0; j < bucket.Count; j++)
                {
                    if (j > 0)
                    {
                        builder.Append('/');
                    }

                    builder.Append(bucket[j].GetType().Name);
                }

                builder.Append(')');
            }

            return builder.ToString();
        }

        /// <summary>报错时用的自称（面板名没设时退回对象名）。</summary>
        private string DisplayName
        {
            get { return string.IsNullOrEmpty(PanelName) ? name : PanelName; }
        }
    }
}
