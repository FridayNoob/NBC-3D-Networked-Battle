// ============================================================================
//  NBC.Framework.UI · UI 管理器
//  替代：唐老师框架 UI/UIManager.cs（原 150 行）
//  缺陷编号：FW-10（硬编码层路径）、P-09（销毁与字典不同步）、P-11（public 容器）、
//            P-12（as 转换不检查）
//  需求条目：FW-M09（生命周期、异步加载、UI 栈、层级管理）
//  完整记录：Docs/06-框架改造记录.md §三 FW-10、§四 P-09 / P-11 / P-12
//
//  ---------------------------------------------------------------------------
//  它负责什么
//  ---------------------------------------------------------------------------
//  一句话：**面板从哪来、挂到哪一层、什么时候收起来。**
//
//      UIManager.Instance.ShowPanel("BagPanel", UILayer.Mid, panel => { ... });
//      UIManager.Instance.HidePanel("BagPanel");    // 收起来（回池，**不销毁**）
//      UIManager.Instance.ClosePanel("BagPanel");   // 真的关掉（销毁 + 释放素材句柄）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么核心是「回调」而不是 `async/await`（这一条很重要，别改回去）
//  ---------------------------------------------------------------------------
//  C# 的 `await` 在恢复执行时，会把"后续代码"**投递回它当初捕获的那个
//  SynchronizationContext**（同步上下文）—— Unity 编辑器/播放模式里装了
//  `UnitySynchronizationContext`，它的"投递"要有**帧循环**才会被执行。
//
//  问题在于：**EditMode 测试没有帧循环。**
//  于是在 EditMode 里 `await` 一个"稍后才完成"的加载，续体被投递到队列里，
//  却永远没人来跑它 → 测试卡死（或者只能靠轮询硬凑）。
//
//  而本框架既有的异步风格是**回调 + 句柄**（`AssetHandle.OnComplete`、
//  `SceneLoader.Tick`），回调是**在完成的那一刻被直接调用**的，
//  不经过任何上下文投递 —— 所以它在 EditMode 里**确定性地**能跑。
//
//  结论：**内部一律用回调**。`ShowPanelAsync` 只是包在外面的一个便利层
//  （`TaskCompletionSource`），因为它在回调触发的那一刻就 `SetResult` 了，
//  所以外面 `await` 到的是一个**已经完成**的任务，同样不会卡。
//
//  ---------------------------------------------------------------------------
//  原版错在哪（逐条）
//  ---------------------------------------------------------------------------
//  **P-09：`Destroy` 是延迟的，字典是即时的。**
//      public void HidePanel(string panelName)
//      {
//          panelDic[panelName].HideMe();
//          GameObject.Destroy(panelDic[panelName].gameObject);   // 这一帧末尾才真删
//          panelDic.Remove(panelName);                            // 但这里当场就移除了
//      }
//  后果：同一帧里"关掉再打开"会重新走一遍异步加载，还可能拿到**那个还没被删掉的旧实例**。
//
//  **修法（结构上让它不可能发生）**：面板关掉时**根本不销毁**，只是收起来回池。
//  既然不销毁，就不存在"延迟销毁"这个时间窗口，P-09 自然消失 ——
//  这比"加个标记去挡"更彻底。
//
//  **P-11：`public Dictionary<string, BasePanel> panelDic`** 把内部容器敞开给外面。
//  这里全部 `private`，对外只给只读查询方法。
//
//  **P-12：`(obj.transform as RectTransform).offsetMax = ...` 不检查。**
//  预制体根节点不是 `RectTransform` 时静默变 null → NRE，报错点离配置错误很远。
//  这里改成显式检查 + 点名报错。
//
//  ---------------------------------------------------------------------------
//  两条必须写死的语义（避免猜）
//  ---------------------------------------------------------------------------
//  **① 同一个面板名，同时只会有一条加载任务。**
//     第二次请求会**挂到同一条任务上**，而不是再加载一次。
//     （原版没有这个保护：同帧连点两下 = 加载两遍 + `panelDic.Add` 抛重复键异常。）
//
//  **② 显示中的面板重复请求 = 只调 `ShowMe()`，不重新加载。** 原版既有语义，保留。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 一个必须记住的既有约定（Docs/06 §13.7）
//  ---------------------------------------------------------------------------
//  `LoadAssetAsync<GameObject>` 返回的是**原始 prefab**，不是实例。
//  **要实例请自己 `Instantiate`。** 本类就是这么做的。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NBC.Framework.Asset;
using UnityEngine;

namespace NBC.Framework.UI
{
    /// <summary>
    /// UI 管理器：加载 Canvas、显示 / 隐藏面板、管理 UI 栈。
    /// </summary>
    public sealed class UIManager : Singleton<UIManager>
    {
        /// <summary>默认的 Canvas 预制体地址。</summary>
        public const string DefaultCanvasLocation = "UI/Canvas";

        /// <summary>默认的面板预制体地址前缀（完整地址 = 前缀 + 面板名）。</summary>
        public const string DefaultPanelLocationPrefix = "UI/";

        /// <summary>
        /// 一次"正在加载中"的面板请求。**同名的后续请求会挂到这一个上**（语义①）。
        /// </summary>
        private sealed class PendingLoad
        {
            /// <summary>加载成功时要通知的回调。</summary>
            public readonly List<Action<BasePanel>> OnSuccess = new List<Action<BasePanel>>();

            /// <summary>加载失败时要通知的回调。</summary>
            public readonly List<Action<Exception>> OnFailure = new List<Action<Exception>>();
        }

        /// <summary>显示中的面板。**private**，对外不给容器（P-11）。</summary>
        private readonly Dictionary<string, BasePanel> m_visible = new Dictionary<string, BasePanel>();

        /// <summary>收起来的面板（**不销毁**，等着下次复用）。</summary>
        private readonly Dictionary<string, BasePanel> m_pooled = new Dictionary<string, BasePanel>();

        /// <summary>正在加载中的面板：名字 到 那一条加载。</summary>
        private readonly Dictionary<string, PendingLoad> m_pending =
            new Dictionary<string, PendingLoad>();

        /// <summary>面板预制体的素材句柄。**面板还活着就不能释放**（三级释放语义）。</summary>
        private readonly Dictionary<string, AssetHandle<GameObject>> m_panelHandles =
            new Dictionary<string, AssetHandle<GameObject>>();

        /// <summary>UI 栈：存的是**面板名**，栈顶是最后压进去的那个。</summary>
        private readonly List<string> m_stack = new List<string>();

        /// <summary>遍历 / 复查时用的临时列表（避免每次分配）。</summary>
        private readonly List<string> m_nameBuffer = new List<string>();

        /// <summary>Canvas 正在加载时，等着它的那些回调。</summary>
        private readonly List<Action> m_canvasReadyCallbacks = new List<Action>();

        /// <summary>Canvas 加载失败时要通知的回调。</summary>
        private readonly List<Action<Exception>> m_canvasFailedCallbacks = new List<Action<Exception>>();

        private string m_canvasLocation = DefaultCanvasLocation;
        private string m_panelLocationPrefix = DefaultPanelLocationPrefix;

        private GameObject m_canvasInstance;
        private AssetHandle<GameObject> m_canvasHandle;
        private UILayers m_layers;
        private bool m_canvasLoading;

        // ====================================================================
        //  查询（对外只读，不给容器）
        // ====================================================================

        /// <summary>Canvas 是否已经加载好、并且层配置校验通过。</summary>
        public bool IsCanvasReady
        {
            get { return m_layers != null; }
        }

        /// <summary>Canvas 实例。**只读用途**，没加载好时是 null。</summary>
        public GameObject Canvas
        {
            get { return m_canvasInstance; }
        }

        /// <summary>层引用持有者。没加载好时是 null。</summary>
        public UILayers Layers
        {
            get { return m_layers; }
        }

        /// <summary>正在显示的面板数量。</summary>
        public int VisiblePanelCount
        {
            get { return m_visible.Count; }
        }

        /// <summary>收起来备用（池里）的面板数量。</summary>
        public int PooledPanelCount
        {
            get { return m_pooled.Count; }
        }

        /// <summary>正在加载中的面板数量。</summary>
        public int LoadingPanelCount
        {
            get { return m_pending.Count; }
        }

        /// <summary>UI 栈深度。</summary>
        public int StackDepth
        {
            get { return m_stack.Count; }
        }

        /// <summary>Canvas 预制体地址。</summary>
        public string CanvasLocation
        {
            get { return m_canvasLocation; }
        }

        /// <summary>面板预制体地址前缀。</summary>
        public string PanelLocationPrefix
        {
            get { return m_panelLocationPrefix; }
        }

        /// <summary>
        /// 改 Canvas 预制体地址。**必须在第一次加载之前调**。
        /// </summary>
        /// <param name="location">地址。</param>
        public void SetCanvasLocation(string location)
        {
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

        /// <summary>改面板地址前缀。默认 <see cref="DefaultPanelLocationPrefix"/>。</summary>
        /// <param name="prefix">前缀。</param>
        public void SetPanelLocationPrefix(string prefix)
        {
            m_panelLocationPrefix = prefix ?? string.Empty;
        }

        /// <summary>某个面板现在是否显示中。</summary>
        /// <param name="panelName">面板名。</param>
        /// <returns>是否显示中。</returns>
        public bool IsPanelVisible(string panelName)
        {
            if (string.IsNullOrEmpty(panelName))
            {
                return false;
            }

            BasePanel panel;
            return m_visible.TryGetValue(panelName, out panel) && panel != null;
        }

        /// <summary>
        /// 取一个**正在显示**的面板。没显示时返回 null。
        /// </summary>
        /// <typeparam name="T">面板类型。</typeparam>
        /// <param name="panelName">面板名。</param>
        /// <returns>面板；没显示则 null。</returns>
        public T GetPanel<T>(string panelName) where T : BasePanel
        {
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

        // ====================================================================
        //  显示 / 隐藏 / 关闭
        // ====================================================================

        /// <summary>
        /// 请求显示一个面板（需要时先异步加载）。**这是核心入口，回调驱动。**
        /// </summary>
        /// <param name="panelName">面板名（同时决定预制体地址：前缀 + 名字）。</param>
        /// <param name="layer">挂到哪一层。</param>
        /// <param name="onShown">显示完成后的回调（可能**同步**被调用，见下面的说明）。</param>
        /// <param name="onFailed">失败回调。</param>
        public void ShowPanel(string panelName, UILayer layer = UILayer.Mid,
                              Action<BasePanel> onShown = null, Action<Exception> onFailed = null)
        {
            if (string.IsNullOrEmpty(panelName))
            {
                Report(onFailed, new ArgumentException("[UIManager] 面板名不能为空。"));
                return;
            }

            // —— ① 已经在显示：只调 ShowMe，不重新加载（原版既有语义）——
            BasePanel shown;
            if (m_visible.TryGetValue(panelName, out shown) && shown != null)
            {
                shown.ShowMe();
                Invoke(onShown, shown);
                return;
            }

            // —— ② 正在加载：挂到同一条上（语义①，P-09 的修法）——
            PendingLoad pending;
            if (m_pending.TryGetValue(panelName, out pending))
            {
                AddCallback(pending, onShown, onFailed);
                return;
            }

            // —— ③ 池里有收起来的同款：直接复活 ——
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

            // —— ④ 冷启动：建一条加载，把自己的回调挂上去 ——
            pending = new PendingLoad();
            AddCallback(pending, onShown, onFailed);
            m_pending.Add(panelName, pending);

            string capturedName = panelName;
            UILayer capturedLayer = layer;

            EnsureCanvas(
                () => StartPanelLoad(capturedName, capturedLayer),
                error => FailPending(capturedName, error));
        }

        /// <summary>
        /// <see cref="ShowPanel"/> 的 `await` 版本。
        /// <para>
        /// ⚠️ 实现只是把回调包成 `TaskCompletionSource`。因为回调触发时任务**当场就完成了**，
        /// 所以外面 `await` 到的是"已经完成的任务"，**不会**碰到
        /// "续体被投递回同步上下文、而 EditMode 没有帧循环"那个坑（见文件头说明）。
        /// </para>
        /// </summary>
        /// <param name="panelName">面板名。</param>
        /// <param name="layer">挂到哪一层。</param>
        /// <returns>面板。</returns>
        public Task<BasePanel> ShowPanelAsync(string panelName, UILayer layer = UILayer.Mid)
        {
            TaskCompletionSource<BasePanel> source = new TaskCompletionSource<BasePanel>();

            ShowPanel(
                panelName,
                layer,
                panel => source.TrySetResult(panel),
                error => source.TrySetException(error));

            return source.Task;
        }

        /// <summary>
        /// 把面板**收起来**（回池，不销毁）。
        /// <para>⚠️ 与原版最大的区别：**原版这里会 `Destroy`**，于是有了 P-09 那个时间窗口。</para>
        /// </summary>
        /// <param name="panelName">面板名。</param>
        /// <returns>true 表示确实收起来了一个。</returns>
        public bool HidePanel(string panelName)
        {
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

        /// <summary>
        /// 真的关掉一个面板：显示中的和池里的都销毁，并释放它的素材句柄。
        /// <para>用于"这个面板以后不再用了"；日常开关请用 <see cref="HidePanel"/>。</para>
        /// </summary>
        /// <param name="panelName">面板名。</param>
        /// <returns>true 表示确实处理了一个面板。</returns>
        public bool ClosePanel(string panelName)
        {
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

        /// <summary>把所有面板收起来（进池），并清空 UI 栈。**不销毁。**</summary>
        public void HideAll()
        {
            m_stack.Clear();
            CopyVisibleNames(m_nameBuffer);

            for (int i = 0; i < m_nameBuffer.Count; i++)
            {
                HidePanel(m_nameBuffer[i]);
            }

            m_nameBuffer.Clear();
        }

        /// <summary>关掉所有面板并释放它们的素材句柄。**用于切场景收尾 / 管理器销毁。**</summary>
        public void CloseAll()
        {
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

        // ====================================================================
        //  UI 栈
        // ====================================================================

        /// <summary>
        /// 把一个**正在显示**的面板压进 UI 栈（表示"它可以被返回键关掉"）。
        /// <para>⚠️ 栈是**显式**的：框架不猜哪些面板该进栈，由游戏层决定。</para>
        /// </summary>
        /// <param name="panelName">面板名。</param>
        public void PushPanel(string panelName)
        {
            if (!IsPanelVisible(panelName))
            {
                throw new InvalidOperationException(
                    "[UIManager] 不能把 «" + panelName + "» 压进 UI 栈 —— 它现在**没有在显示**。\n" +
                    "栈的语义是\"这个面板可以被返回键关掉\"；把没显示的面板压进去，只会让返回键失灵。");
            }

            m_stack.Remove(panelName);   // 同一个面板不要在栈里出现两次
            m_stack.Add(panelName);
        }

        /// <summary>栈顶的面板名；栈空时 null。</summary>
        /// <returns>面板名。</returns>
        public string PeekPanel()
        {
            return m_stack.Count > 0 ? m_stack[m_stack.Count - 1] : null;
        }

        /// <summary>
        /// 把栈顶面板隐藏掉并返回它的名字。
        /// <para>会跳过"已经被别的途径关掉"的残留项，所以返回值**可能不是**你上次压进去的那个。</para>
        /// </summary>
        /// <returns>被关掉的面板名；栈空返回 null。</returns>
        public string PopPanel()
        {
            while (m_stack.Count > 0)
            {
                int top = m_stack.Count - 1;
                string panelName = m_stack[top];
                m_stack.RemoveAt(top);

                if (HidePanel(panelName))
                {
                    return panelName;
                }

                // 这个已经在别处关掉了，继续往下找。
            }

            return null;
        }

        /// <summary>
        /// 处理一次"返回"操作。
        /// <para>
        /// ⚠️ **框架不读键盘**（FW-12）。请由游戏层把返回键绑到这个方法：
        /// 返回 true 表示"这次返回被 UI 吃掉了"，就别再去做别的事（比如退出游戏）。
        /// </para>
        /// </summary>
        /// <returns>是否消费了这次返回。</returns>
        public bool HandleBack()
        {
            return PopPanel() != null;
        }

        // ====================================================================
        //  内部：Canvas 引导
        // ====================================================================

        /// <summary>
        /// 保证 Canvas 就绪。**并发调用只会加载一次**，其余的回调排队等同一个结果。
        /// </summary>
        /// <param name="onReady">就绪回调。</param>
        /// <param name="onFailed">失败回调。</param>
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

        /// <summary>Canvas 素材加载完成。</summary>
        /// <param name="handle">素材句柄。</param>
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
                    // 和 A8 的音频宿主同一个理由：UI 要跨场景活着。
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

            // **先把回调拷出来再清空**：回调里可能又发起新的加载，
            // 那时它们应该落到新的列表里，而不是被打乱。
            List<Action> callbacks = new List<Action>(m_canvasReadyCallbacks);
            m_canvasReadyCallbacks.Clear();
            m_canvasFailedCallbacks.Clear();

            for (int i = 0; i < callbacks.Count; i++)
            {
                callbacks[i]();
            }
        }

        /// <summary>Canvas 引导失败：通知所有等着的回调。</summary>
        /// <param name="error">失败原因。</param>
        private void FailCanvas(Exception error)
        {
            m_canvasLoading = false;

            List<Action<Exception>> callbacks = new List<Action<Exception>>(m_canvasFailedCallbacks);
            m_canvasReadyCallbacks.Clear();
            m_canvasFailedCallbacks.Clear();

            if (callbacks.Count == 0)
            {
                // 没人接手也绝不能静默 —— 否则又变成"界面出不来，Console 还干净"。
                Report(null, error);
                return;
            }

            for (int i = 0; i < callbacks.Count; i++)
            {
                callbacks[i](error);
            }
        }

        // ====================================================================
        //  内部：面板加载
        // ====================================================================

        /// <summary>发起面板预制体的加载。</summary>
        /// <param name="panelName">面板名。</param>
        /// <param name="layer">目标层。</param>
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

        /// <summary>面板素材加载完成：实例化 → 挂层 → 初始化 → 显示。</summary>
        /// <param name="panelName">面板名。</param>
        /// <param name="layer">目标层。</param>
        /// <param name="handle">素材句柄。</param>
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
                // ⚠️ 既有约定（Docs/06 §13.7）：拿到的是**原始 prefab**，要自己实例化。
                instance = UnityEngine.Object.Instantiate(handle.Asset);

                if (instance == null)
                {
                    throw new InvalidOperationException(
                        "[UIManager] 面板 «" + panelName + "» 实例化失败（地址：\"" + location + "\"）。");
                }

                // 去掉 "(Clone)" 后缀，Hierarchy 里才看得清是哪个面板。
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

                // 句柄要留住：面板还活着，素材就不能释放。
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

        /// <summary>把面板挂到指定层，并把它的 RectTransform 铺满。</summary>
        /// <param name="instance">面板实例。</param>
        /// <param name="panelName">面板名（只用于报错）。</param>
        /// <param name="layer">目标层。</param>
        private void AttachToLayer(GameObject instance, string panelName, UILayer layer)
        {
            // P-12 的修法：`as` 转换**必须检查**。
            // 原版直接 `(obj.transform as RectTransform).offsetMax = ...`，
            // 根节点不是 RectTransform 时静默变 null → NRE，报错点离配置错误很远。
            RectTransform rect = instance.transform as RectTransform;

            if (rect == null)
            {
                throw new InvalidOperationException(
                    "[UIManager] 面板 «" + panelName + "» 的根节点不是 RectTransform（实际类型：" +
                    instance.transform.GetType().Name + "）。\n" +
                    "UI 预制体的根节点必须是 RectTransform（在 Canvas 下创建的 UI 默认就是）。\n" +
                    "⚠️ 常见原因：把一个 3D 物体或一个**空 GameObject**当成了 UI 预制体。");
            }

            // 取层：配置有问题时 Get 会抛出**带人话**的异常（见 UILayers）。
            Transform parent = m_layers.Get(layer);

            // ⚠️ `worldPositionStays: false` —— UI 必须用这个。
            // 不传的话 Unity 会为了"保持世界坐标"去反算 localScale，界面会被拉伸或缩小。
            rect.SetParent(parent, false);

            rect.localPosition = Vector3.zero;
            rect.localScale = Vector3.one;
            rect.offsetMax = Vector2.zero;
            rect.offsetMin = Vector2.zero;
        }

        // ====================================================================
        //  内部：回调派发
        // ====================================================================

        /// <summary>把回调挂到一条正在进行的加载上。</summary>
        /// <param name="pending">加载记录。</param>
        /// <param name="onShown">成功回调。</param>
        /// <param name="onFailed">失败回调。</param>
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

        /// <summary>加载成功：摘掉记录，通知所有等待者。</summary>
        /// <param name="panelName">面板名。</param>
        /// <param name="panel">面板。</param>
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

        /// <summary>加载失败：摘掉记录，通知所有等待者。</summary>
        /// <param name="panelName">面板名。</param>
        /// <param name="error">失败原因。</param>
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

        /// <summary>安全地调一个无参回调。</summary>
        /// <param name="callback">回调。</param>
        private static void Invoke(Action callback)
        {
            if (callback != null)
            {
                callback();
            }
        }

        /// <summary>安全地调一个面板回调。</summary>
        /// <param name="callback">回调。</param>
        /// <param name="panel">面板。</param>
        private static void Invoke(Action<BasePanel> callback, BasePanel panel)
        {
            if (callback != null)
            {
                callback(panel);
            }
        }

        /// <summary>
        /// 报告一个**没人接手**的错误。
        /// <para>⚠️ 没有失败回调时绝不能静默 —— 那就变成"界面不出现，Console 还干净"的老毛病了。</para>
        /// </summary>
        /// <param name="onFailed">失败回调（可为 null）。</param>
        /// <param name="error">错误。</param>
        private static void Report(Action<Exception> onFailed, Exception error)
        {
            if (onFailed != null)
            {
                onFailed(error);
                return;
            }

            Debug.LogError("[UIManager] " + error.Message);
        }

        // ====================================================================
        //  内部：清理
        // ====================================================================

        /// <summary>把显示中的面板名拷进缓冲。</summary>
        /// <param name="buffer">缓冲（会先清空）。</param>
        private void CopyVisibleNames(List<string> buffer)
        {
            buffer.Clear();

            foreach (KeyValuePair<string, BasePanel> pair in m_visible)
            {
                buffer.Add(pair.Key);
            }
        }

        /// <summary>释放某个面板的素材句柄。</summary>
        /// <param name="panelName">面板名。</param>
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

        /// <summary>把面板从 UI 栈里摘掉（关面板时用）。</summary>
        /// <param name="panelName">面板名。</param>
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

        /// <summary>
        /// 销毁一个 GameObject。
        /// <para>⚠️ EditMode 下不能调 `Object.Destroy`（Unity 会报 "Destroy may not be called from
        /// edit mode"），而这条错误会污染测试结果。所以按运行模式分流。</para>
        /// </summary>
        /// <param name="go">目标对象。</param>
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

        // ====================================================================
        //  生命周期
        // ====================================================================

        /// <summary>
        /// 销毁时把所有面板、Canvas、素材句柄**一起清干净**。
        /// <para>⚠️ 面板必须**真正销毁**，否则单例没了、对象还挂在场景里 —— 那就是泄漏。</para>
        /// </summary>
        protected override void OnDispose()
        {
            // 还在等加载的调用方必须收到失败，否则它们会**永远等下去**。
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
