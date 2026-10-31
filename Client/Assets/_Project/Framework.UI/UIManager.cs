// 这个文件里有：
//   UIManager（类）：UI 总管，加载 Canvas、按层显示 / 收起 / 关闭面板，维护 UI 栈、面板池与素材句柄。
//   PendingLoad（类）：一次进行中的面板加载，收集同名请求挂上来的成功与失败回调。
// 设计取舍与历史踩坑见 Docs\06-框架改造记录.md 的 UI 管理器一节。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NBC.Framework.Asset;
using UnityEngine;

namespace NBC.Framework.UI
{
    // 主要功能：加载 Canvas，按层显示 / 收起 / 关闭面板，维护 UI 栈、面板池与素材句柄。
    // 主要数据成员：m_visible（显示中的面板）、m_pooled（回池面板）、m_pending（同名共享的加载任务）、m_panelHandles（预制体句柄）、m_stack（UI 栈）、m_layers / m_canvasInstance（Canvas 与层引用）。
    // 主要外部方法：ShowPanel / ShowPanelAsync / HidePanel / ClosePanel / HideAll / CloseAll / PushPanel / PopPanel / HandleBack，以及 IsPanelVisible / GetPanel 与各只读属性。
    // 谁在用它：Boot 的 HpBarDemoBehaviour、Framework.UI 的 LoadingMaskController 与 EditMode 测试。
    /// <summary>UI 管理器：加载 Canvas、按层显示 / 收起 / 关闭面板，管理 UI 栈与素材句柄。</summary>
    public sealed class UIManager : Singleton<UIManager>
    {
        /// <summary>默认的 Canvas 预制体地址。</summary>
        public const string DefaultCanvasLocation = "UI/Canvas";

        /// <summary>默认的面板预制体地址前缀；完整地址 = 前缀 + 面板名。</summary>
        public const string DefaultPanelLocationPrefix = "UI/";

        // 主要功能：记录一次进行中的同名面板加载，把后来的请求挂到同一条任务上。
        // 主要数据成员：OnSuccess / OnFailure（等待这条加载结果的回调列表，按注册顺序调用）。
        // 主要外部方法：无 —— 只由 UIManager 读写这两个列表。
        // 谁在用它：UIManager.ShowPanel / AddCallback / SucceedPending / FailPending。
        private sealed class PendingLoad
        {
            // 加载成功时要通知的回调；按注册顺序调用。
            public readonly List<Action<BasePanel>> OnSuccess = new List<Action<BasePanel>>();

            // 加载失败时要通知的回调；按注册顺序调用。
            public readonly List<Action<Exception>> OnFailure = new List<Action<Exception>>();
        }

        // 显示中的面板，键是面板名；只在本类内可见，对外只给查询方法。
        private readonly Dictionary<string, BasePanel> m_visible = new Dictionary<string, BasePanel>();

        // 收起来的面板，键是面板名；不销毁，等下次 ShowPanel 复用。
        private readonly Dictionary<string, BasePanel> m_pooled = new Dictionary<string, BasePanel>();

        // 正在加载的面板：名字到那一条加载；同名请求共享同一条任务。
        private readonly Dictionary<string, PendingLoad> m_pending =
            new Dictionary<string, PendingLoad>();

        // 面板预制体句柄，键是面板名；面板活着就不能释放。
        private readonly Dictionary<string, AssetHandle<GameObject>> m_panelHandles =
            new Dictionary<string, AssetHandle<GameObject>>();

        // UI 栈，存的是面板名；栈顶是最后压进去的那个。
        private readonly List<string> m_stack = new List<string>();

        // 遍历 / 复查时复用的临时名字列表，避免每次分配。
        private readonly List<string> m_nameBuffer = new List<string>();

        // Canvas 加载中排队的就绪回调。
        private readonly List<Action> m_canvasReadyCallbacks = new List<Action>();

        // Canvas 加载失败时要通知的回调。
        private readonly List<Action<Exception>> m_canvasFailedCallbacks = new List<Action<Exception>>();

        // Canvas 预制体地址；第一次加载之前可改，加载开始后不可改。
        private string m_canvasLocation = DefaultCanvasLocation;
        // 面板预制体地址前缀；完整地址 = 前缀 + 面板名。
        private string m_panelLocationPrefix = DefaultPanelLocationPrefix;

        // Canvas 实例；没加载好时为 null。
        private GameObject m_canvasInstance;
        // Canvas 预制体句柄；Canvas 活着就不能释放。
        private AssetHandle<GameObject> m_canvasHandle;
        // Canvas 上的层引用；非 null 就表示 Canvas 已就绪。
        private UILayers m_layers;
        // 是否正在加载 Canvas；保证并发调用只加载一次。
        private bool m_canvasLoading;

        /// <summary>Canvas 是否已加载好且层配置校验通过；加载失败或还没开始时为 false。</summary>
        public bool IsCanvasReady
        {
            get { return m_layers != null; }
        }

        /// <summary>Canvas 实例；没加载好时为 null，只读用途。</summary>
        public GameObject Canvas
        {
            get { return m_canvasInstance; }
        }

        /// <summary>Canvas 上的层引用；没加载好时为 null。</summary>
        public UILayers Layers
        {
            get { return m_layers; }
        }

        /// <summary>正在显示的面板数量。</summary>
        public int VisiblePanelCount
        {
            get { return m_visible.Count; }
        }

        /// <summary>池里收起来的面板数量。</summary>
        public int PooledPanelCount
        {
            get { return m_pooled.Count; }
        }

        /// <summary>正在加载中的面板数量，按名字去重。</summary>
        public int LoadingPanelCount
        {
            get { return m_pending.Count; }
        }

        /// <summary>UI 栈深度，即栈里面板名的个数。</summary>
        public int StackDepth
        {
            get { return m_stack.Count; }
        }

        /// <summary>当前 Canvas 预制体地址。</summary>
        public string CanvasLocation
        {
            get { return m_canvasLocation; }
        }

        /// <summary>当前面板预制体地址前缀。</summary>
        public string PanelLocationPrefix
        {
            get { return m_panelLocationPrefix; }
        }

        /// <summary>改 Canvas 预制体地址；必须在第一次加载之前调用，否则抛异常。</summary>
        /// <param name="location">资源地址，不能为空。</param>
        public void SetCanvasLocation(string location)
        {
            // 功能：改 Canvas 预制体地址，并挡住「已经开加载再改」的无效调用。
            // 入参：location 资源地址，不能为空
            // 返回：无
            // 步骤：
            //   1) 先挡空地址 —— 空地址要到加载时才发现，报错点会离调用处很远
            //   2) 再挡已经开加载 —— Canvas 已在加载或就绪时改地址没有意义，当场抛异常
            //   3) 最后才写入
            // 上游：Boot 的 HpBarDemoBehaviour 与测试启动前
            // 下游：EnsureCanvas（真正的加载与使用）
            if (string.IsNullOrEmpty(location))
            {
                throw new ArgumentException("[UIManager] Canvas 地址不能为空。", nameof(location));
            }

            if (m_layers != null || m_canvasLoading)
            {
                throw new InvalidOperationException(
                    "[UIManager] Canvas 已经开始加载了，现在改地址没有意义。" +
                    "请在第一次 ShowPanel 之前设置。");
            }

            m_canvasLocation = location;
        }

        /// <summary>改面板预制体地址前缀；传 null 按空前缀处理。</summary>
        /// <param name="prefix">前缀；完整地址 = 前缀 + 面板名。</param>
        public void SetPanelLocationPrefix(string prefix)
        {
            // 功能：改面板地址前缀，null 回落成空串，让面板名直接当地址用。
            // 入参：prefix 前缀，可为 null
            // 返回：无
            // 步骤：只做 null 回落再赋值 —— 前缀允许为空（面板名就是完整地址），所以不校验空串。
            // 上游：Boot 的 HpBarDemoBehaviour 与测试启动前
            // 下游：StartPanelLoad 拼地址
            m_panelLocationPrefix = prefix ?? string.Empty;
        }

        /// <summary>某个面板现在是否显示中；名字为空、只在池里或不在显示表里都返回 false。</summary>
        /// <param name="panelName">面板名。</param>
        /// <returns>显示中返回 true；否则 false。</returns>
        public bool IsPanelVisible(string panelName)
        {
            // 功能：只查显示表判断面板是否显示中，不触发加载、不改状态。
            // 入参：panelName 面板名，可为 null 或空
            // 返回：显示中返回 true；空名、没显示、只在池里都返回 false
            // 步骤：空名直接 false；再查表并核对实例没被外部销毁；未命中不抛异常。
            // 上游：PushPanel 与测试查询
            // 下游：m_visible
            if (string.IsNullOrEmpty(panelName))
            {
                return false;
            }

            BasePanel panel;
            return m_visible.TryGetValue(panelName, out panel) && panel != null;
        }

        /// <summary>取一个正在显示的面板并转成指定类型；没显示或类型不符返回 null。</summary>
        /// <typeparam name="T">面板类型，必须是 BasePanel 的子类。</typeparam>
        /// <param name="panelName">面板名。</param>
        /// <returns>面板实例；没显示、已被外部销毁或类型不符时返回 null。</returns>
        public T GetPanel<T>(string panelName) where T : BasePanel
        {
            // 功能：按名字取显示中的面板，并安全地转成调用方要的类型。
            // 入参：panelName 面板名，可为 null 或空
            // 返回：取到且类型匹配返回实例；空名、没显示、被销毁或类型不符返回 null
            // 步骤：
            //   1) 空名直接返回 null，不去查表
            //   2) 只查显示表，池里的不算 —— 池里面板不处于可用的显示状态
            //   3) 用 as 转换，类型不符时返回 null 而不是抛异常
            // 上游：LoadingMaskController 刷新遮罩
            // 下游：m_visible
            if (string.IsNullOrEmpty(panelName))
            {
                return null;
            }

            BasePanel panel;
            if (!m_visible.TryGetValue(panelName, out panel))
            {
                return null;
            }

            return panel as T;
        }

        /// <summary>请求显示一个面板：需要时先加载，已在显示只刷新，加载中则共享同一条任务；回调驱动。</summary>
        /// <param name="panelName">面板名，同时决定预制体地址（前缀 + 名字）。</param>
        /// <param name="layer">挂到哪一层。</param>
        /// <param name="onShown">显示完成后的回调；可能同步触发。</param>
        /// <param name="onFailed">失败回调；为 null 时错误打到 Console。</param>
        public void ShowPanel(string panelName, UILayer layer = UILayer.Mid,
                              Action<BasePanel> onShown = null, Action<Exception> onFailed = null)
        {
            // 功能：显示面板的统一入口 —— 已显示只调 ShowMe，加载中挂到同一条任务，池里有就复活，否则冷启动加载。
            // 入参：panelName 面板名，同时决定预制体地址；layer 目标层；onShown 成功回调，可能同步触发；onFailed 失败回调，为 null 时错误打到 Console
            // 返回：无；结果只经回调返回
            // 步骤：
            //   1) 先挡空名，按失败回调规则报出去
            //   2) 已在显示：只调 ShowMe 并同步回调 —— 重复请求不重新加载
            //   3) 正在加载：把回调挂到同一条任务上，避免同帧连点两下加载两遍、或重复键抛异常
            //   4) 池里有同款：复活、挂层并 ShowMe；挂层失败要放回池里，不能把面板弄丢
            //   5) 都没有：新建一条加载记录，等 Canvas 就绪后再开始加载
            // 上游：Boot 的 HpBarDemoBehaviour、LoadingMaskController 与测试
            // 下游：EnsureCanvas / StartPanelLoad / AttachToLayer / ShowMe
            // 注意：onShown 可能同步触发（已显示或池中复活时），调用方不要假设它一定在下一帧才到。
            if (string.IsNullOrEmpty(panelName))
            {
                Report(onFailed, new ArgumentException("[UIManager] 面板名不能为空。"));
                return;
            }

            BasePanel shown;
            if (m_visible.TryGetValue(panelName, out shown) && shown != null)
            {
                shown.ShowMe();
                Invoke(onShown, shown);
                return;
            }

            PendingLoad pending;
            if (m_pending.TryGetValue(panelName, out pending))
            {
                AddCallback(pending, onShown, onFailed);
                return;
            }

            BasePanel pooled;
            if (m_pooled.TryGetValue(panelName, out pooled))
            {
                m_pooled.Remove(panelName);

                if (pooled != null)
                {
                    pooled.gameObject.SetActive(true);

                    try
                    {
                        AttachToLayer(pooled.gameObject, panelName, layer);
                    }
                    catch (Exception attachError)
                    {
                        // 挂不上就放回池里，别把它弄丢。
                        pooled.gameObject.SetActive(false);
                        m_pooled[panelName] = pooled;
                        Report(onFailed, attachError);
                        return;
                    }

                    pooled.ShowMe();
                    m_visible[panelName] = pooled;
                    Invoke(onShown, pooled);
                    return;
                }
            }

            pending = new PendingLoad();
            AddCallback(pending, onShown, onFailed);
            m_pending.Add(panelName, pending);

            string capturedName = panelName;
            UILayer capturedLayer = layer;

            EnsureCanvas(
                () => StartPanelLoad(capturedName, capturedLayer),
                error => FailPending(capturedName, error));
        }

        /// <summary>ShowPanel 的 await 版本；返回的任务在回调触发时即已完成。</summary>
        /// <param name="panelName">面板名。</param>
        /// <param name="layer">挂到哪一层。</param>
        /// <returns>显示完成的面板；失败时任务带原始异常。</returns>
        public Task<BasePanel> ShowPanelAsync(string panelName, UILayer layer = UILayer.Mid)
        {
            // 功能：把 ShowPanel 的回调包成 TaskCompletionSource，供外部 await。
            // 入参：panelName 面板名；layer 目标层
            // 返回：显示完成的面板任务；失败时任务带原始异常
            // 步骤：直接把两个回调接到 TrySetResult / TrySetException 上，不另写加载逻辑。
            // 上游：需要 await 显示结果的调用方与测试
            // 下游：ShowPanel
            // 注意：这里刻意不用 async/await —— Unity 会把续体投递回同步上下文，而 EditMode 没有帧循环，await 一个稍后完成的任务会永久卡住；回调在完成那一刻直接触发，任务当场就已完结，所以外面 await 不会卡。
            TaskCompletionSource<BasePanel> source = new TaskCompletionSource<BasePanel>();

            ShowPanel(
                panelName,
                layer,
                panel => source.TrySetResult(panel),
                error => source.TrySetException(error));

            return source.Task;
        }

        /// <summary>把显示中的面板收起来放回池里，不销毁；返回是否确实收起了一个。</summary>
        /// <param name="panelName">面板名。</param>
        /// <returns>true 表示确实从显示表里收起了一个面板。</returns>
        public bool HidePanel(string panelName)
        {
            // 功能：把显示中的面板隐藏并放进池里等下次复用，同时挡掉池里的同名残留。
            // 入参：panelName 面板名，可为 null 或空
            // 返回：收起成功返回 true；空名、没显示返回 false；实例已被外部销毁也算处理过，返回 true
            // 步骤：
            //   1) 空名直接 false
            //   2) 只从显示表取，没显示就 false
            //   3) 先移出显示表，再判断实例是否已被外部销毁；销毁了就清掉池里同名残留，免得下次拿到死对象
            //   4) HideMe 并停用 GameObject，再放进池里
            //   5) 池里已有不同的同名实例说明有别处乱动，销毁旧的免得泄漏
            // 上游：LoadingMaskController、PopPanel 与测试
            // 下游：BasePanel.HideMe、m_pooled
            // 注意：这里只回池、不销毁 —— 原版先 Destroy 再移字典，而 Destroy 要帧末才生效，同一帧关掉再打开会重走异步加载、还可能拿到没删掉的旧实例。
            if (string.IsNullOrEmpty(panelName))
            {
                return false;
            }

            BasePanel panel;
            if (!m_visible.TryGetValue(panelName, out panel))
            {
                return false;
            }

            m_visible.Remove(panelName);

            if (panel == null)
            {
                // 面板被外部销毁了，清掉池里的同名残留，免得下次拿到死对象。
                m_pooled.Remove(panelName);
                return true;
            }

            panel.HideMe();
            panel.gameObject.SetActive(false);

            // 同名只可能有一个池对象；真撞上了说明有别的地方在乱动，销毁旧的免得泄漏。
            BasePanel stale;
            if (m_pooled.TryGetValue(panelName, out stale) && stale != null && stale != panel)
            {
                DestroyObject(stale.gameObject);
            }

            m_pooled[panelName] = panel;
            return true;
        }

        /// <summary>真正关闭面板：显示中的和池里的都销毁，并释放素材句柄与栈记录。</summary>
        /// <param name="panelName">面板名。</param>
        /// <returns>true 表示确实处理了显示中或池里的一个面板。</returns>
        public bool ClosePanel(string panelName)
        {
            // 功能：把面板彻底关掉 —— 显示表和池里的同名实例都销毁，再释放句柄、移出 UI 栈。
            // 入参：panelName 面板名，可为 null 或空
            // 返回：确实处理了显示中或池里的面板返回 true；两处都没有返回 false
            // 步骤：
            //   1) 空名直接 false
            //   2) 先处理池里的，再处理显示中的；两处都要移出字典后再销毁
            //   3) 最后释放素材句柄并移出 UI 栈 —— 实例销毁之后才放手素材
            // 上游：LoadingMaskController 放弃遮罩、CloseAll 与测试
            // 下游：DestroyObject / ReleasePanelHandle / RemoveFromStack
            // 注意：日常开关请用 HidePanel；本方法销毁实例并释放素材，之后再次 ShowPanel 会重新加载。
            if (string.IsNullOrEmpty(panelName))
            {
                return false;
            }

            bool touched = false;

            BasePanel pooled;
            if (m_pooled.TryGetValue(panelName, out pooled))
            {
                m_pooled.Remove(panelName);

                if (pooled != null)
                {
                    DestroyObject(pooled.gameObject);
                }

                touched = true;
            }

            BasePanel shown;
            if (m_visible.TryGetValue(panelName, out shown))
            {
                m_visible.Remove(panelName);

                if (shown != null)
                {
                    DestroyObject(shown.gameObject);
                }

                touched = true;
            }

            ReleasePanelHandle(panelName);
            RemoveFromStack(panelName);

            return touched;
        }

        /// <summary>把所有显示中的面板收起回池并清空 UI 栈；不销毁。</summary>
        public void HideAll()
        {
            // 功能：一次性收起所有显示中的面板，并把 UI 栈清空。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 先清空 UI 栈 —— 栈里的名字随后都会进池，留着只会让返回键反复碰到不存在的东西
            //   2) 先把名字拷进缓冲再遍历 —— HidePanel 会改显示表，边遍历边删会抛异常
            //   3) 逐个 HidePanel，最后清掉缓冲
            // 上游：游戏层整体收起界面（当前只有测试调用）
            // 下游：CopyVisibleNames / HidePanel
            m_stack.Clear();
            CopyVisibleNames(m_nameBuffer);

            for (int i = 0; i < m_nameBuffer.Count; i++)
            {
                HidePanel(m_nameBuffer[i]);
            }

            m_nameBuffer.Clear();
        }

        /// <summary>关闭所有面板并释放素材句柄；用于切场景收尾与管理器销毁。</summary>
        public void CloseAll()
        {
            // 功能：关掉显示中的和池里的所有面板，释放它们的素材句柄。
            // 入参：无
            // 返回：无
            // 步骤：
            //   1) 先清 UI 栈
            //   2) 把显示中的名字拷进缓冲后逐个 ClosePanel —— ClosePanel 会改显示表，不能边遍历边删
            //   3) 再把池里的名字拷进缓冲后逐个 ClosePanel，直到池清空
            // 上游：OnDispose 与测试
            // 下游：CopyVisibleNames / ClosePanel
            m_stack.Clear();

            CopyVisibleNames(m_nameBuffer);
            for (int i = 0; i < m_nameBuffer.Count; i++)
            {
                ClosePanel(m_nameBuffer[i]);
            }

            m_nameBuffer.Clear();

            foreach (KeyValuePair<string, BasePanel> pair in m_pooled)
            {
                m_nameBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_nameBuffer.Count; i++)
            {
                ClosePanel(m_nameBuffer[i]);
            }

            m_nameBuffer.Clear();
        }

        /// <summary>把一个正在显示的面板压进 UI 栈，表示它可以被返回键关掉；未显示时抛异常。</summary>
        /// <param name="panelName">面板名。</param>
        public void PushPanel(string panelName)
        {
            // 功能：把一个正在显示的面板压到 UI 栈顶，同一个面板不会在栈里出现两次。
            // 入参：panelName 面板名，必须正在显示
            // 返回：无；不在显示中时抛 InvalidOperationException
            // 步骤：
            //   1) 先校验正在显示 —— 把没显示的面板压进去只会让返回键失灵，不如当场报错
            //   2) 先从栈里移除同名项，再压到栈顶 —— 同一个面板只保留最上面那一份
            // 上游：游戏层返回流程（当前只有测试调用）
            // 下游：m_stack
            // 注意：栈是显式的，框架不猜哪些面板该进栈，由游戏层决定。
            if (!IsPanelVisible(panelName))
            {
                throw new InvalidOperationException(
                    "[UIManager] 不能把 «" + panelName + "» 压进 UI 栈 —— 它现在**没有在显示**。\n" +
                    "栈的语义是\"这个面板可以被返回键关掉\"；把没显示的面板压进去，只会让返回键失灵。");
            }

            m_stack.Remove(panelName);
            m_stack.Add(panelName);
        }

        /// <summary>取栈顶面板名；栈空时返回 null。</summary>
        /// <returns>栈顶面板名；栈空为 null。</returns>
        public string PeekPanel()
        {
            // 功能：只读栈顶面板名，不隐藏、不出栈。
            // 入参：无
            // 返回：栈顶面板名；栈空返回 null
            // 步骤：直接取列表末项，空栈返回 null，不抛异常。
            // 上游：外部查询（当前为测试）
            // 下游：m_stack
            return m_stack.Count > 0 ? m_stack[m_stack.Count - 1] : null;
        }

        /// <summary>隐藏栈顶面板并返回其名字；跳过已被别处关掉的残留项。</summary>
        /// <returns>被收起的面板名；栈空或全是残留时返回 null。</returns>
        public string PopPanel()
        {
            // 功能：弹出并收起栈顶面板，跳过已经被别的途径关掉的残留项。
            // 入参：无
            // 返回：确实收起了的面板名；栈空或全是残留时返回 null
            // 步骤：
            //   1) 从栈顶往下逐个取出名字并移除
            //   2) 调 HidePanel，成功就返回这个名字
            //   3) 失败说明这个面板已被别处关掉，继续往下找
            // 上游：HandleBack 与游戏层返回流程
            // 下游：HidePanel
            // 注意：返回值可能不是你上次压进去的那个 —— 中间已被别处关掉的面板会被跳过。
            while (m_stack.Count > 0)
            {
                int top = m_stack.Count - 1;
                string panelName = m_stack[top];
                m_stack.RemoveAt(top);

                if (HidePanel(panelName))
                {
                    return panelName;
                }
            }

            return null;
        }

        /// <summary>处理一次返回：框架不读键盘，由游戏层把返回键绑到这里；true 表示 UI 吃掉了这次返回。</summary>
        /// <returns>true 表示收起了一个面板，调用方不要再去做退出游戏等动作。</returns>
        public bool HandleBack()
        {
            // 功能：把一次「返回」转发给 PopPanel，有面板可收就算消费掉了。
            return PopPanel() != null;
        }

        // 功能：保证 Canvas 已就绪；并发调用只加载一次，其余回调排队等同一个结果。
        // 入参：onReady 就绪回调；onFailed 失败回调，两者都可为 null
        // 返回：无
        // 步骤：
        //   1) 已就绪就当场回调，直接返回
        //   2) 未就绪先把两个回调排进等待队列
        //   3) 已经在加载就直接返回 —— 后到的调用只是排队，不会再发一次加载
        //   4) 标记加载中并取素材；同步抛出也走统一失败路径
        //   5) 挂上 OnComplete，等素材加载完成
        // 上游：ShowPanel 冷启动
        // 下游：AssetManager.LoadAssetAsync、OnCanvasLoaded / FailCanvas
        // 注意：并发调用只会加载一次，靠 m_canvasLoading 与两个回调队列保证。
        private void EnsureCanvas(Action onReady, Action<Exception> onFailed)
        {
            if (m_layers != null)
            {
                Invoke(onReady);
                return;
            }

            if (onReady != null)
            {
                m_canvasReadyCallbacks.Add(onReady);
            }

            if (onFailed != null)
            {
                m_canvasFailedCallbacks.Add(onFailed);
            }

            if (m_canvasLoading)
            {
                return;
            }

            m_canvasLoading = true;

            AssetHandle<GameObject> handle;

            try
            {
                handle = AssetManager.Instance.LoadAssetAsync<GameObject>(m_canvasLocation);
            }
            catch (Exception error)
            {
                FailCanvas(error);
                return;
            }

            handle.OnComplete += OnCanvasLoaded;
        }

        // 功能：Canvas 素材加载完成：实例化、校验 UILayers、保存引用，再一次性通知所有等待者。
        // 入参：handle 素材句柄
        // 返回：无
        // 步骤：
        //   1) 句柄无效就释放并走 FailCanvas，错误里带上地址与原因
        //   2) 实例化并改名为 Canvas；运行时用 DontDestroyOnLoad 让 UI 跨场景活着
        //   3) 校验根节点上的 UILayers 与层配置，缺一个都当场抛出可照着修的错误
        //   4) 全部校验通过才保存实例、句柄与层引用，避免半成品状态被外部看到
        //   5) 先把待通知回调拷出来再清空，逐个调用 —— 回调里可能又发起新的加载，不能让它们落进正在遍历的列表
        // 上游：AssetHandle.OnComplete
        // 下游：FailCanvas、DestroyObject
        // 注意：实例化或校验中途失败要销毁临时实例并释放句柄，否则会留下没人管的 Canvas 与泄漏的素材。
        private void OnCanvasLoaded(AssetHandle<GameObject> handle)
        {
            if (!handle.IsValid)
            {
                string reason = handle.Error;
                handle.Dispose();

                FailCanvas(new InvalidOperationException(
                    "[UIManager] Canvas 预制体加载失败（地址：\"" + m_canvasLocation + "\"）。\n" +
                    "原因：" + reason));
                return;
            }

            GameObject instance = null;

            try
            {
                instance = UnityEngine.Object.Instantiate(handle.Asset);
                instance.name = "Canvas";

                if (Application.isPlaying)
                {
                    UnityEngine.Object.DontDestroyOnLoad(instance);
                }

                UILayers layers = instance.GetComponent<UILayers>();

                if (layers == null)
                {
                    throw new InvalidOperationException(
                        "[UIManager] Canvas 预制体 «" + m_canvasLocation + "» 上没有 UILayers 组件。\n" +
                        "请在该预制体**根节点**上挂 UILayers，并把 Bot / Mid / Top / System 四个层节点拖进去。\n" +
                        "（没有它的后果是：面板不知道往哪挂，界面不显示而且不报错。）");
                }

                string problem = layers.DescribeProblem();

                if (problem != null)
                {
                    throw new InvalidOperationException(problem);
                }

                m_canvasInstance = instance;
                m_canvasHandle = handle;
                m_layers = layers;
            }
            catch (Exception error)
            {
                if (instance != null)
                {
                    DestroyObject(instance);
                }

                handle.Dispose();
                FailCanvas(error);
                return;
            }

            m_canvasLoading = false;

            List<Action> callbacks = new List<Action>(m_canvasReadyCallbacks);
            m_canvasReadyCallbacks.Clear();
            m_canvasFailedCallbacks.Clear();

            for (int i = 0; i < callbacks.Count; i++)
            {
                callbacks[i]();
            }
        }

        // 功能：Canvas 引导失败：复位加载标记，并通知所有等着的失败回调。
        // 入参：error 失败原因
        // 返回：无
        // 步骤：
        //   1) 先复位 m_canvasLoading，让下次还能重试
        //   2) 拷出失败回调并清空两个队列
        //   3) 没人接手就打到 Console —— 绝不能静默，否则又是「界面出不来、Console 还干净」
        //   4) 否则逐个回调原始异常
        // 上游：EnsureCanvas / OnCanvasLoaded / OnDispose
        // 下游：Report / 调用方失败回调
        private void FailCanvas(Exception error)
        {
            m_canvasLoading = false;

            List<Action<Exception>> callbacks = new List<Action<Exception>>(m_canvasFailedCallbacks);
            m_canvasReadyCallbacks.Clear();
            m_canvasFailedCallbacks.Clear();

            if (callbacks.Count == 0)
            {
                Report(null, error);
                return;
            }

            for (int i = 0; i < callbacks.Count; i++)
            {
                callbacks[i](error);
            }
        }

        // 功能：发起一次面板预制体加载，完成后转给 OnPanelLoaded。
        // 入参：panelName 面板名；layer 目标层
        // 返回：无
        // 步骤：
        //   1) 拼地址：前缀 + 面板名
        //   2) 加载同步抛出时直接走 FailPending，不留半条加载
        //   3) 先捕获名字与层，再挂完成回调 —— 回调里不能再读可能已变的成员
        // 上游：ShowPanel 冷启动（Canvas 就绪后）
        // 下游：AssetManager.LoadAssetAsync、OnPanelLoaded
        private void StartPanelLoad(string panelName, UILayer layer)
        {
            string location = m_panelLocationPrefix + panelName;
            AssetHandle<GameObject> handle;

            try
            {
                handle = AssetManager.Instance.LoadAssetAsync<GameObject>(location);
            }
            catch (Exception error)
            {
                FailPending(panelName, error);
                return;
            }

            string capturedName = panelName;
            UILayer capturedLayer = layer;

            handle.OnComplete += asset => OnPanelLoaded(capturedName, capturedLayer, asset);
        }

        // 功能：面板素材加载完成：实例化、挂层、初始化、显示，最后通知所有等待者。
        // 入参：panelName 面板名；layer 目标层；handle 素材句柄
        // 返回：无
        // 步骤：
        //   1) 句柄无效就释放并走 FailPending，错误里带上地址与原因
        //   2) 实例化原始 prefab（加载返回的是原始资源，不是实例），并去掉 (Clone) 后缀
        //   3) 取根节点上的 BasePanel 子类；没有就当场抛出能照着修的错误
        //   4) 写面板名、挂到目标层、调 Initialize 与 ShowMe
        //   5) 留住句柄（面板活着素材不能释放），最后登记显示表并通知等待者
        // 上游：AssetHandle.OnComplete
        // 下游：AttachToLayer / BasePanel.Initialize / ShowMe / SucceedPending
        // 注意：中途任何一步失败都要销毁临时实例并释放句柄；句柄必须留到面板关闭，提前释放会让面板的贴图与预制体对象变空。
        private void OnPanelLoaded(string panelName, UILayer layer, AssetHandle<GameObject> handle)
        {
            string location = m_panelLocationPrefix + panelName;

            if (!handle.IsValid)
            {
                string reason = handle.Error;
                handle.Dispose();

                FailPending(panelName, new InvalidOperationException(
                    "[UIManager] 面板 «" + panelName + "» 加载失败（地址：\"" + location + "\"）。\n" +
                    "原因：" + reason));
                return;
            }

            GameObject instance = null;

            try
            {
                instance = UnityEngine.Object.Instantiate(handle.Asset);

                if (instance == null)
                {
                    throw new InvalidOperationException(
                        "[UIManager] 面板 «" + panelName + "» 实例化失败（地址：\"" + location + "\"）。");
                }

                instance.name = panelName;

                BasePanel panel = instance.GetComponent<BasePanel>();

                if (panel == null)
                {
                    throw new InvalidOperationException(
                        "[UIManager] 面板预制体 «" + location + "» 的根节点上没有挂任何 BasePanel 子类。\n" +
                        "面板脚本必须挂在**预制体根节点**上，否则框架拿不到它，也没法调它的 Initialize / ShowMe。");
                }

                panel.PanelName = panelName;

                AttachToLayer(instance, panelName, layer);
                panel.Initialize();
                panel.ShowMe();

                AssetHandle<GameObject> previous;
                if (m_panelHandles.TryGetValue(panelName, out previous) && previous != null)
                {
                    previous.Dispose();
                }

                m_panelHandles[panelName] = handle;
                m_visible[panelName] = panel;

                SucceedPending(panelName, panel);
            }
            catch (Exception error)
            {
                if (instance != null)
                {
                    DestroyObject(instance);
                }

                handle.Dispose();
                FailPending(panelName, error);
            }
        }

        // 功能：把面板实例挂到指定层，并把它的 RectTransform 铺满。
        // 入参：instance 面板实例；panelName 面板名（只用于报错）；layer 目标层
        // 返回：无；根节点不是 RectTransform 或层配置有问题时抛异常
        // 步骤：
        //   1) 先检查根节点是 RectTransform —— 不是就点名报错，不要等后面空引用
        //   2) 按层取父节点，配置有问题时 UILayers 会抛带人话的异常
        //   3) SetParent 必须传 worldPositionStays: false，再清零 localPosition / localScale / offset
        // 上游：ShowPanel（池中复活）、OnPanelLoaded
        // 下游：UILayers.Get
        // 注意：SetParent 不传 false 时 Unity 会为保持世界坐标反算 localScale，界面会被拉伸或缩小。
        private void AttachToLayer(GameObject instance, string panelName, UILayer layer)
        {
            RectTransform rect = instance.transform as RectTransform;

            if (rect == null)
            {
                throw new InvalidOperationException(
                    "[UIManager] 面板 «" + panelName + "» 的根节点不是 RectTransform（实际类型：" +
                    instance.transform.GetType().Name + "）。\n" +
                    "UI 预制体的根节点必须是 RectTransform（在 Canvas 下创建的 UI 默认就是）。\n" +
                    "⚠️ 常见原因：把一个 3D 物体或一个**空 GameObject**当成了 UI 预制体。");
            }

            // 取层：配置有问题时 Get 会抛出带人话的异常（见 UILayers）。
            Transform parent = m_layers.Get(layer);

            rect.SetParent(parent, false);

            rect.localPosition = Vector3.zero;
            rect.localScale = Vector3.one;
            rect.offsetMax = Vector2.zero;
            rect.offsetMin = Vector2.zero;
        }

        // 功能：把两个回调挂到一条进行中的加载上，空回调忽略。
        // 入参：pending 加载记录；onShown 成功回调；onFailed 失败回调，两者都可为 null
        // 返回：无
        // 步骤：非空才加入对应列表；顺序即注册顺序，完成时按顺序通知。
        // 上游：ShowPanel
        // 下游：PendingLoad
        private static void AddCallback(PendingLoad pending, Action<BasePanel> onShown,
                                        Action<Exception> onFailed)
        {
            if (onShown != null)
            {
                pending.OnSuccess.Add(onShown);
            }

            if (onFailed != null)
            {
                pending.OnFailure.Add(onFailed);
            }
        }

        // 功能：加载成功：摘掉加载记录，按注册顺序通知所有等待者。
        // 入参：panelName 面板名；panel 已显示的面板
        // 返回：无
        // 步骤：
        //   1) 没有对应记录就直接返回（可能已被 OnDispose 取消）
        //   2) 先移除记录，再清掉失败回调 —— 成功之后失败回调不会再被触发
        //   3) 把成功回调拷出来再逐个调用 —— 回调里可能再次 ShowPanel / HidePanel，不能边遍历边改列表
        // 上游：OnPanelLoaded
        // 下游：PendingLoad.OnSuccess
        // 注意：派发用的是回调拷贝，回调里增删面板不会打乱本次遍历。
        private void SucceedPending(string panelName, BasePanel panel)
        {
            PendingLoad pending;
            if (!m_pending.TryGetValue(panelName, out pending))
            {
                return;
            }

            m_pending.Remove(panelName);

            List<Action<BasePanel>> callbacks = new List<Action<BasePanel>>(pending.OnSuccess);
            pending.OnFailure.Clear();

            for (int i = 0; i < callbacks.Count; i++)
            {
                callbacks[i](panel);
            }
        }

        // 功能：加载失败：摘掉记录并通知所有等待者；没人接手时打到 Console。
        // 入参：panelName 面板名；error 失败原因
        // 返回：无
        // 步骤：
        //   1) 没有对应记录说明这次失败没人等（或已取消），直接 Report
        //   2) 移除记录并清掉成功回调
        //   3) 拷出失败回调；一个都没有就 Report，绝不静默
        //   4) 否则逐个回调原始异常
        // 上游：StartPanelLoad / OnPanelLoaded / OnDispose
        // 下游：Report
        // 注意：先拷贝回调再派发 —— 回调里再发起新的加载不会打乱本次遍历。
        private void FailPending(string panelName, Exception error)
        {
            PendingLoad pending;
            if (!m_pending.TryGetValue(panelName, out pending))
            {
                Report(null, error);
                return;
            }

            m_pending.Remove(panelName);

            List<Action<Exception>> callbacks = new List<Action<Exception>>(pending.OnFailure);
            pending.OnSuccess.Clear();

            if (callbacks.Count == 0)
            {
                Report(null, error);
                return;
            }

            for (int i = 0; i < callbacks.Count; i++)
            {
                callbacks[i](error);
            }
        }

        // 功能：回调非空时才调用的无参转发，省掉每个调用处的判空。
        private static void Invoke(Action callback)
        {
            if (callback != null)
            {
                callback();
            }
        }

        // 功能：回调非空时才调用的面板回调转发，省掉每个调用处的判空。
        private static void Invoke(Action<BasePanel> callback, BasePanel panel)
        {
            if (callback != null)
            {
                callback(panel);
            }
        }

        // 功能：报告一个没人接手的错误：有失败回调就交出去，没有就打到 Console。
        // 入参：onFailed 失败回调，可为 null；error 错误
        // 返回：无
        // 步骤：先尝试失败回调；再兜底 Debug.LogError，绝不静默。
        // 上游：ShowPanel / FailCanvas / FailPending
        // 下游：Debug.LogError
        // 注意：这里不要再加 [UIManager] 前缀 —— 本类抛出的消息自己已经带了来源标识，再加一次会变成叠词。
        private static void Report(Action<Exception> onFailed, Exception error)
        {
            if (onFailed != null)
            {
                onFailed(error);
                return;
            }

            Debug.LogError(error.Message);
        }

        // 功能：把显示表中的面板名拷进缓冲，供遍历时安全地增删。
        // 入参：buffer 接收结果的列表，会先清空
        // 返回：无
        // 步骤：先清空缓冲再逐个加入键 —— 先拷贝再遍历，避免遍历时改显示表抛异常。
        // 上游：HideAll / CloseAll
        // 下游：m_visible
        private void CopyVisibleNames(List<string> buffer)
        {
            buffer.Clear();

            foreach (KeyValuePair<string, BasePanel> pair in m_visible)
            {
                buffer.Add(pair.Key);
            }
        }

        // 功能：从表里摘掉并释放某个面板的预制体句柄。
        // 入参：panelName 面板名
        // 返回：无
        // 步骤：先查表；命中就先移除再 Dispose，避免重复释放。
        // 上游：ClosePanel
        // 下游：m_panelHandles
        private void ReleasePanelHandle(string panelName)
        {
            AssetHandle<GameObject> handle;
            if (m_panelHandles.TryGetValue(panelName, out handle))
            {
                m_panelHandles.Remove(panelName);

                if (handle != null)
                {
                    handle.Dispose();
                }
            }
        }

        // 功能：把某个面板名从 UI 栈里全部摘掉。
        // 入参：panelName 面板名
        // 返回：无
        // 步骤：从栈顶往下倒序遍历删除 —— 倒序不会因删除而跳过元素；同名项可能不止一个。
        // 上游：ClosePanel
        // 下游：m_stack
        private void RemoveFromStack(string panelName)
        {
            for (int i = m_stack.Count - 1; i >= 0; i--)
            {
                if (m_stack[i] == panelName)
                {
                    m_stack.RemoveAt(i);
                }
            }
        }

        // 功能：销毁一个 GameObject；按运行 / 编辑模式分流。
        // 入参：go 目标对象，可为 null
        // 返回：无
        // 步骤：null 直接返回；运行时用 Destroy，非运行时用 DestroyImmediate —— EditMode 调 Destroy 会报错并污染测试结果。
        // 上游：HidePanel / ClosePanel / OnCanvasLoaded / OnPanelLoaded / OnDispose
        // 下游：UnityEngine.Object
        private static void DestroyObject(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(go);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // 功能：销毁时把未完成的加载、面板、Canvas 与素材句柄一起清干净。
        // 入参：无（Singleton 的销毁钩子）
        // 返回：无
        // 步骤：
        //   1) 先取消所有进行中的面板加载，通过失败回调报出去 —— 否则还在 await 的调用方会永远挂着
        //   2) 再取消 Canvas 的等待回调
        //   3) CloseAll 销毁所有面板并释放句柄；清空栈与缓冲
        //   4) 释放 Canvas 句柄并销毁实例，最后复位状态与地址
        // 上游：Singleton.DisposeInstance
        // 下游：FailPending / FailCanvas / CloseAll / DestroyObject
        // 注意：面板必须真正销毁，否则单例没了、对象还挂在场景里就是泄漏；加载途中销毁会按设计多一条 error，不想看到就先取消自己的请求。
        protected override void OnDispose()
        {
            List<string> pendingNames = new List<string>(m_pending.Keys);

            for (int i = 0; i < pendingNames.Count; i++)
            {
                FailPending(pendingNames[i], new InvalidOperationException(
                    "[UIManager] 管理器已销毁，面板 «" + pendingNames[i] + "» 的这次加载被取消。"));
            }

            m_pending.Clear();

            if (m_canvasReadyCallbacks.Count > 0 || m_canvasFailedCallbacks.Count > 0)
            {
                FailCanvas(new InvalidOperationException(
                    "[UIManager] 管理器已销毁，Canvas 的这次加载被取消。"));
            }

            CloseAll();

            m_stack.Clear();
            m_nameBuffer.Clear();

            if (m_canvasHandle != null)
            {
                m_canvasHandle.Dispose();
                m_canvasHandle = null;
            }

            if (m_canvasInstance != null)
            {
                DestroyObject(m_canvasInstance);
                m_canvasInstance = null;
            }

            m_layers = null;
            m_canvasLoading = false;
            m_canvasLocation = DefaultCanvasLocation;
            m_panelLocationPrefix = DefaultPanelLocationPrefix;
        }
    }
}
