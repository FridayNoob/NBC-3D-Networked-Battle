// 这个文件里有：
//   LoadingMaskController（类）：订阅场景加载进度与结果，决定加载遮罩何时出现、上面写什么，不负责遮罩长什么样

using System;
using NBC.Framework.Scenes;
using UnityEngine;
using UnityEngine.Events;

namespace NBC.Framework.UI
{
    // 主要功能：把场景加载进度与结果翻译成遮罩的显示时机与内容——进度到来就露出并更新，成功就收起，失败就留着写错误。
    // 主要数据成员：m_ui（取面板的 UI 管理器）、m_panelName（预制体地址末段，默认 LoadingMask）、m_attached（是否已订阅）、m_requested（是否已请求显示）、LastProgress（0..1）、LastLocation（场景地址）。
    // 主要外部方法：Attach / Detach / Dispose / HideNow，以及 IsAttached / IsMaskRequested / PanelName / ShowCount / HideCount 这几个只读属性。
    // 谁在用它：不是单例、不是 MonoBehaviour，由调用方显式 new 再 Attach，框架不自动安装；当前仓库里由 EditMode 测试 LoadingMaskControllerTests 直接构造，游戏层在 Game / Boot 程序集里也可按需安装。
    /// <summary>加载遮罩的控制器：订阅场景加载事件，决定遮罩何时显示、显示什么。</summary>
    public sealed class LoadingMaskController : IDisposable
    {
        /// <summary>默认遮罩面板名；UIManager 按「UI/」加这个名字拼出预制体地址，默认 LoadingMask。</summary>
        public const string DefaultPanelName = "LoadingMask";

        // 面板显示 / 收起 / 查找的入口；构造时注入，此后不再更换，不能为 null。
        private readonly UIManager m_ui;
        // 事件中心实例；构造时缓存，运行期不会替换，省掉每次订阅时的单例访问。
        private readonly EventCenter m_events;
        // 实际使用的遮罩面板名；用于 ShowPanel / HidePanel / GetPanel 三处，取值非空。
        private readonly string m_panelName;

        // 进度 / 成功 / 失败三个回调的委托实例，Attach 与 Detach 必须用同一实例成对增删；
        // 注意：EventCenter.RemoveEventListener 按委托相等性匹配，若每次现写方法组，移除时匹配不上，监听会一直留着。
        private readonly UnityAction<SceneProgressInfo> m_onProgress;   // 进度变化，参数含进度（0..1）与场景地址
        private readonly UnityAction<string> m_onSucceeded;             // 加载成功，参数是场景地址
        private readonly UnityAction<SceneFailureInfo> m_onFailed;      // 加载失败，参数含场景地址与错误文本

        // 是否已订阅三个场景事件；true 时 Attach 直接返回，避免重复注册导致回调触发两次。
        private bool m_attached;
        // 是否已请求过显示遮罩；注意：它只表示「请求过」，面板是异步加载的，此刻未必已经可见。
        private bool m_requested;

        /// <summary>绑定 UI 管理器与面板名并缓存三个回调委托；不自动订阅，要显式调用 Attach。</summary>
        /// <param name="ui">取面板用的 UI 管理器，不能为 null。</param>
        /// <param name="panelName">遮罩面板名；null 或空串时回落到 DefaultPanelName。</param>
        public LoadingMaskController(UIManager ui, string panelName = DefaultPanelName)
        {
            // 功能：记住 UI 管理器与面板名，并把三个事件回调一次性绑成字段，供 Attach / Detach 成对使用。
            // 入参：ui 取面板用的 UI 管理器，不能为 null；panelName 遮罩面板名，null 或空串时回落到 DefaultPanelName。
            // 返回：无
            // 步骤：
            //   1) 先挡掉 ui 为 null——后面每个方法都直接用 m_ui，早失败比在事件回调里空引用好
            //   2) 存面板名并做空值回落，否则 UIManager 会拿空名去拼预制体地址
            //   3) 三个回调在这里就绑成字段，Attach 与 Detach 才能用同一实例成对增删
            //   4) 最后缓存 EventCenter.Instance——运行期不变，省掉每次订阅时的查找
            // 上游：游戏层 / 测试构造 LoadingMaskController 时
            // 下游：EventCenter.Instance
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

        /// <summary>是否已订阅场景加载事件；Attach 之后为 true，Detach 之后为 false。</summary>
        public bool IsAttached
        {
            get { return m_attached; }
        }

        /// <summary>是否已请求过显示遮罩；为 true 不代表面板已经可见，预制体可能还在加载。</summary>
        public bool IsMaskRequested
        {
            get { return m_requested; }
        }

        /// <summary>实际使用的遮罩面板名（构造时已把空值换成 DefaultPanelName）。</summary>
        public string PanelName
        {
            get { return m_panelName; }
        }

        /// <summary>最近一次收到的加载进度，0..1；未收到过时为 0。</summary>
        public float LastProgress { get; private set; }

        /// <summary>最近一次收到的场景地址，可能为 null。</summary>
        public string LastLocation { get; private set; }

        /// <summary>累计请求显示遮罩的次数，只增不减，Dispose 时归零；测试与调试用。</summary>
        public int ShowCount { get; private set; }

        /// <summary>累计成功收起遮罩的次数，UIManager 确实收掉面板才加，Dispose 时归零；测试与调试用。</summary>
        public int HideCount { get; private set; }

        /// <summary>订阅场景进度、加载成功、加载失败三个事件；重复调用无副作用。</summary>
        public void Attach()
        {
            // 功能：装上三个场景事件监听，之后进度与结果都会转给本控制器。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 已订阅就直接返回，否则事件会被注册两遍、同一进度回调两次
            //   2) 先置 m_attached 挡重入，再依次注册三个事件，注册期间再进 Attach 不会重复注册
            //   3) 三个事件都用构造时缓存的委托字段注册，Detach 才能按相等性摘掉
            // 上游：游戏层在需要显示加载遮罩时装上本控制器
            // 下游：EventCenter.AddEventListener
            if (m_attached)
            {
                return;
            }

            m_attached = true;

            m_events.AddEventListener(SceneEvents.ProgressChanged, m_onProgress);
            m_events.AddEventListener(SceneEvents.LoadSucceeded, m_onSucceeded);
            m_events.AddEventListener(SceneEvents.LoadFailed, m_onFailed);
        }

        /// <summary>退订三个场景事件；未订阅时直接返回。</summary>
        public void Detach()
        {
            // 功能：摘掉三个场景事件监听，断开事件中心对本控制器的引用。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 未订阅直接返回，重复移除没有意义
            //   2) 先清 m_attached 挡重入，再逐个移除
            //   3) 必须传 m_onProgress / m_onSucceeded / m_onFailed 这三个字段，现写方法组摘不掉
            // 注意：不退订就是泄漏——事件中心会一直持有本对象，连 GC 都收不走。
            // 上游：Dispose，或游戏层主动卸载加载流程时
            // 下游：EventCenter.RemoveEventListener
            if (!m_attached)
            {
                return;
            }

            m_attached = false;

            m_events.RemoveEventListener(SceneEvents.ProgressChanged, m_onProgress);
            m_events.RemoveEventListener(SceneEvents.LoadSucceeded, m_onSucceeded);
            m_events.RemoveEventListener(SceneEvents.LoadFailed, m_onFailed);
        }

        /// <summary>退订并清空累计状态，便于同一个控制器重新 Attach 复用时从零开始。</summary>
        public void Dispose()
        {
            // 功能：先退订，再把请求标记与统计复位，让下一次加载从初始态开始。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 先 Detach 摘监听，避免状态清了事件却还在回调
            //   2) 复位 m_requested 与 LastProgress / LastLocation，否则下次加载会沿用上一轮的旧进度
            //   3) 显示 / 收起次数归零，测试与调试读到的才是本轮数据
            // 上游：游戏层卸载加载流程时；实现 IDisposable 以便放进 using
            // 下游：Detach
            Detach();

            m_requested = false;
            LastProgress = 0f;
            LastLocation = null;
            ShowCount = 0;
            HideCount = 0;
        }

        /// <summary>主动收起遮罩，例如游戏层想在加载结束后自己挑时机。</summary>
        /// <returns>true 表示确实收起了一个可见面板；false 表示面板本来没显示或还在加载。</returns>
        public bool HideNow()
        {
            // 功能：清掉「已请求」标记并让 UIManager 收起遮罩，供游戏层自己挑时机。
            // 入参：无
            // 返回：true 表示 UIManager 确实收起了一个可见面板；false 表示面板本来没显示或还在加载。
            // 步骤：
            //   1) 先清 m_requested——无论面板在不在，后续进度都要能重新触发一次显示
            //   2) 再问 UIManager 收面板，只有它返回 true 才累加 HideCount，避免计数虚高
            // 上游：HandleSucceeded；游戏层需要手动收起时
            // 下游：UIManager.HidePanel
            m_requested = false;

            if (m_ui.HidePanel(m_panelName))
            {
                HideCount++;
                return true;
            }

            return false;
        }


        // 功能：收到进度就记住最新值并确保遮罩已经请求显示，之后每次进度都写进面板。
        // 入参：info 进度事件，取 Progress（0..1）与 Location（场景地址，可能为 null）
        // 返回：无
        // 步骤：
        //   1) 先存最新进度与场景地址，面板晚到时靠这两个值补齐
        //   2) 只有第一次收到进度才请求显示，并先置 m_requested 与 ShowCount，避免重复 ShowPanel
        //   3) 请求显示后立刻返回——面板是异步加载的，此刻拿不到实例，等 OnMaskShown 回调再补进度
        //   4) 已经请求过就直接 ApplyProgress 刷新；面板还没出来时这次刷新落空，下一次进度会再补
        // 上游：EventCenter 派发 SceneEvents.ProgressChanged
        // 下游：UIManager.ShowPanel、ApplyProgress
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

        // 功能：加载成功就收起遮罩；失败时请对照 HandleFailed——那种情况遮罩故意不收起。
        private void HandleSucceeded(string location)
        {
            HideNow();
        }

        // 功能：加载失败时保留遮罩并把错误写上去，让玩家看见到底出了什么事。
        // 入参：info 失败事件，取 Location（场景地址）与 Error（错误文本）
        // 返回：无
        // 步骤：
        //   1) 先记场景地址，遮罩若随后才显示也能带上它
        //   2) 面板已在就直接 SetError 并返回，这是最常见的失败路径，不必重新请求显示
        //   3) 面板不在（第一次加载就失败）才请求显示，并把错误文本捕获进闭包，等面板出来再写
        //   4) 传入 OnMaskFailed 兜住「遮罩自己都加载失败」，否则那条异常没人报
        // 注意：这条路径绝不调 HideNow——失败信息必须留在屏幕上，收起等于把错误藏了。
        // 上游：EventCenter 派发 SceneEvents.LoadFailed
        // 下游：LoadingMaskPanel.SetError、UIManager.ShowPanel
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


        // 功能：面板加载完成时把最新进度补写进去，补上「进度先到、面板后到」的空档。
        // 入参：panel 刚加载出来的面板根组件，类型不一定是 LoadingMaskPanel
        // 返回：无
        // 步骤：
        //   1) 先尝试转成 LoadingMaskPanel，转不过去只可能是预制体根节点挂错了脚本
        //   2) 挂错就报一条能直接定位的错并返回——写清面板名、实际类型与修法，否则进度静默丢失，玩家只看到一块卡住的遮罩
        //   3) 类型对才 SetProgress，用的是字段里存的最新值，而不是构造时的初值
        // 上游：UIManager.ShowPanel 的加载成功回调，由 HandleProgress / HandleFailed 传入
        // 下游：LoadingMaskPanel.SetProgress
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

        // 功能：遮罩面板自己加载失败时只记一条错误日志。
        // 入参：error 加载失败原因
        // 返回：无
        // 步骤：
        //   1) 直接报面板名与异常消息并结束——遮罩都起不来，再弹一层提示只会套娃
        // 上游：UIManager.ShowPanel 的失败回调
        // 下游：Debug.LogError
        private static void OnMaskFailed(Exception error)
        {
            Debug.LogError("[LoadingMaskController] 遮罩面板自己加载失败了：" + error.Message);
        }

        // 功能：把最新进度写到已经显示的遮罩上；面板还没出来就静默跳过，等下次回调。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 按面板名取面板，GetPanel 取不到返回 null，不抛异常
        //   2) 拿到才 SetProgress，避免面板异步加载期间空引用
        // 上游：HandleProgress，面板已请求显示之后的每次进度
        // 下游：UIManager.GetPanel、LoadingMaskPanel.SetProgress
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
