// 这个文件里有：
//   BasePanel（类）：所有 UI 面板的基类，一次收集子控件并按 GameObject 名字索引，自动接上按钮点击与勾选变化

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NBC.Framework.UI
{
    // 主要功能：面板基类——实例化后由 UIManager 调 Initialize，一次全树扫描收集 Button / Image / Text / Toggle / Slider / ScrollRect / InputField 并按 GameObject 名字索引，顺手接好按钮与勾选监听。
    // 主要数据成员：m_controls（名字 → 该名字下的控件列表）、m_nameBuffer（拼报错清单用的临时列表）、m_initialized（是否已初始化）、PanelName（面板名，仅报错用）。
    // 主要外部方法：Initialize / CollectControls / CopyControlNames、ShowMe / HideMe（public）；OnInit / OnClick / OnValueChanged / GetControl / RequireControl（子类覆写或调用的 protected）。
    // 谁在用它：NBC.Framework.UI 的 UIManager 实例化面板后调 Initialize / ShowMe / HideMe；Game 的 HpBarPanel / NetHudPanel / QuestPanel 继承它；Boot 的 M2DemoBehaviour 与 EditMode / PlayMode 测试直接调 Initialize。
    // 注意：初始化刻意不放进 Awake——子类再写一个 Awake 而忘了 base.Awake() 时，控件索引是空的、监听也没注册，点任何按钮都没反应而且不报错。
    // 注意：Unity 按名字反射只会调最派生的那个 Awake，sealed / private 都挡不住子类重名声明，所以只能让 UIManager 显式调 Initialize，框架不依赖 Awake。
    /// <summary>所有 UI 面板的基类：由 UIManager 调 Initialize 完成初始化，框架不依赖 Awake。</summary>
    public class BasePanel : MonoBehaviour
    {
        // 控件索引：GameObject 名字 → 该名字下的全部 UI 组件（同名可挂多个，取用时按类型挑）；未收集时为空
        private readonly Dictionary<string, List<UIBehaviour>> m_controls =
            new Dictionary<string, List<UIBehaviour>>();

        // 拼报错控件清单时复用的临时列表，避免每次报错都新分配；只在 DescribeControls 里用
        private readonly List<string> m_nameBuffer = new List<string>();

        // 是否已初始化；true 后 Initialize 直接返回，对象池反复复用靠它保持幂等
        private bool m_initialized;

        /// <summary>面板名，由 UIManager 设置；只用于报错信息，让错误能指出是哪个面板。</summary>
        public string PanelName { get; internal set; }

        /// <summary>是否已经初始化过。</summary>
        public bool IsInitialized
        {
            get { return m_initialized; }
        }

        /// <summary>收集到的控件数量（同名的多个组件分别计数）；调试与测试用。</summary>
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

        // 初始化

        /// <summary>初始化面板：收集控件、接好按钮与勾选监听，再交回子类的 OnInit；由 UIManager 在实例化后调用。</summary>
        // 功能：把面板从「刚实例化」带到「能用」：一次收集全部子控件、接上按钮点击与勾选变化，最后交给子类的 OnInit。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 已初始化过就直接返回，保证对象池反复进出时控件索引与监听不被重置
        //   2) 先置 m_initialized 再收集、再调 OnInit：置位提前，使 OnInit 里重入 Initialize 时直接返回，不会递归
        //   3) CollectControls 负责收集与接线，OnInit 留给子类
        // 上游：UIManager 实例化面板之后调；Boot 的 M2DemoBehaviour 与测试也会直接调
        // 下游：CollectControls、OnInit
        // 注意：要写「面板被创建时的初始化」请覆写 OnInit，不要写 Awake——框架不依赖 Awake，写了也不会用它做初始化。
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

        /// <summary>收集面板下的所有 UI 控件、按 GameObject 名字建索引，并接好按钮与勾选监听；测试可直接调。</summary>
        // 功能：一次全树扫描拿到所有 UIBehaviour（Button / Image / Text / Toggle / Slider / ScrollRect / InputField 都继承它），按所在 GameObject 名分桶并接线。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先清空索引，重复收集不会留下上次的旧引用
        //   2) 用 GetComponentsInChildren<UIBehaviour>(true) 一次拿全（含未激活节点）：原版按类型扫 7 次全树，这里合并成 1 次
        //   3) 逐个按 GameObject 名分桶，同名下的多个组件进同一个列表，取控件时再按类型挑
        //   4) 每收一个就交给 WireControl 接线，收集与接线绑在一起，不会两边漏配
        // 上游：Initialize；测试直接调来检查收集结果（EditMode 下也能调，不用等播放循环）
        // 下游：WireControl
        // 注意：只收 UIBehaviour 子类——只挂 RectTransform 的空物体（纯容器）收不到，要取它就得挂一个 UI 组件。
        public void CollectControls()
        {
            m_controls.Clear();

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

        // 功能：给按钮接点击、给勾选框接勾选变化；其他控件只登记、不接线。
        // 入参：control 控件，只认 Button / Toggle；controlName 控件所在 GameObject 的名字，回调时用它区分是谁
        // 返回：无
        // 步骤：
        //   1) 是 Button 就加 onClick 监听，回调里带名字，一个 OnClick 就能处理整个面板的按钮
        //   2) 不是 Button 再看是否 Toggle，加 onValueChanged 监听
        //   3) 其余（Image / Text / Slider 等）不接线：它们没有统一的「变化」语义，硬接只会猜错意图
        // 上游：CollectControls
        // 下游：子类覆写的 OnClick / OnValueChanged
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

        // 子类可覆写的钩子

        // 功能：面板初始化完成时的回调，子类在这里做自己的初始化。
        // 入参：无
        // 返回：无
        // 步骤：空实现，子类按需覆写
        // 上游：Initialize
        // 下游：无
        // 注意：不要为了「早点执行」把初始化改回 Awake，那会重新引入「子类忘了调 base 就静默失效」的老问题。
        protected virtual void OnInit()
        {
        }

        /// <summary>面板被显示时的回调，子类覆写。</summary>
        // 功能：面板每次被显示时调一次，子类在这里刷新数据。
        // 入参：无
        // 返回：无
        // 步骤：空实现，子类按需覆写（常见做法是重新拉一次数据再刷 UI）
        // 上游：UIManager 显示面板时；已显示的面板重复请求也只会再调本方法，不重新加载
        // 下游：无
        public virtual void ShowMe()
        {
        }

        /// <summary>面板被隐藏时的回调，子类覆写。</summary>
        // 功能：面板被收起来时调一次，子类在这里停掉定时刷新、解绑外部事件。
        // 入参：无
        // 返回：无
        // 步骤：空实现，子类按需覆写
        // 上游：UIManager.HidePanel
        // 下游：无
        public virtual void HideMe()
        {
        }

        // 功能：某个按钮被点击时的回调，按 GameObject 名字区分是哪个按钮。
        // 入参：buttonName 被点按钮所在 GameObject 的名字，与预制体里的名字精确一致
        // 返回：无
        // 步骤：空实现，子类按需覆写，通常在里面按名字分支
        // 上游：WireControl 接的 Button.onClick
        // 下游：无
        // 注意：名字按 GameObject 精确匹配，改了预制体里的名字而没同步这里，对应分支会静默走不到。
        protected virtual void OnClick(string buttonName)
        {
        }

        // 功能：某个勾选框状态变化时的回调，按 GameObject 名字区分是哪个勾选框。
        // 入参：toggleName 勾选框所在 GameObject 的名字；value 变化后的勾选状态
        // 返回：无
        // 步骤：空实现，子类按需覆写
        // 上游：WireControl 接的 Toggle.onValueChanged
        // 下游：无
        // 注意：名字不匹配时同 OnClick，静默不处理，不会报错。
        protected virtual void OnValueChanged(string toggleName, bool value)
        {
        }

        // 取控件

        // 功能：按 GameObject 名字取控件，没有就返回 null。
        // 入参：controlName 控件所在 GameObject 的名字（精确匹配，含大小写与空格）；空串或 null 直接返回 null
        // 返回：第一个能转成 T 的控件；名字不存在或该名字下没有 T 类型时为 null
        // 步骤：
        //   1) 名字为空直接返回 null，省一次查表
        //   2) 按名字查桶，没有这个键就返回 null
        //   3) 桶里按加入顺序找第一个能转成 T 的组件
        // 上游：子类 OnInit 与面板内部逻辑
        // 下游：m_controls
        // 注意：名字打错时这里不报错，要等到用它的那一刻才崩，报错点离原因很远——「这个名字必须有」请改用 RequireControl。
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

        // 功能：按名字取控件，取不到就当场抛异常，并在消息里列出本面板所有可用的控件名。
        // 入参：controlName 要找的 GameObject 名字（精确匹配）
        // 返回：控件，一定不是 null
        // 步骤：
        //   1) 先走 GetControl
        //   2) 取到了直接返回
        //   3) 取不到立刻抛 InvalidOperationException，消息里带面板名、要找的名字与类型、现有控件清单，不用翻预制体就知道该改成什么
        // 上游：子类 OnInit
        // 下游：GetControl、DescribeControls
        // 注意：这是「名字打错 → 后面才崩」的修法：错误发生在写错的那一行，而不是等到用控件的时候。
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

        /// <summary>把索引里的控件名拷进调用方给的列表（调试与报错用）。</summary>
        // 功能：把索引里的控件名逐个拷进调用方给的列表。
        // 入参：buffer 接收结果的列表，会先清空；传 null 直接返回
        // 返回：无
        // 步骤：
        //   1) null 直接返回，不抛
        //   2) 先清空 buffer，调用方复用列表时不会残留上次的名字
        //   3) 顺序遍历索引的键逐个加入；字典顺序不保证，只适合展示与报错
        // 上游：DescribeControls、调试面板
        // 下游：无
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

        // 功能：把索引拼成一句人话（名字(类型/类型), ...），供报错使用。
        // 入参：无
        // 返回：控件清单字符串；索引为空时返回一句说明，指出面板里一个 UI 组件都没有
        // 步骤：
        //   1) 空索引直接返回固定说明，不拼空串
        //   2) 借 m_nameBuffer 拿一份名字快照，避免边遍历字典边被改
        //   3) 每个名字后跟括号里的组件类型名，多个类型用 / 分隔，名字之间用逗号
        // 上游：RequireControl 的异常消息
        // 下游：CopyControlNames
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

        // 报错时用的自称：面板名没设（null 或空串）时退回 Unity 对象名
        private string DisplayName
        {
            get { return string.IsNullOrEmpty(PanelName) ? name : PanelName; }
        }
    }
}
