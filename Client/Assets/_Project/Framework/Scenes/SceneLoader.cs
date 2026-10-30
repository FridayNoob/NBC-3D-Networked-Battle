// 这个文件里有：
//   SceneLoadRequest（类）：一次场景加载请求，向调用方暴露地址、句柄、进度与成败，并记录 Tick 用的等待时长。
//   SceneLoader（类）：场景加载流程的单例，负责推进请求、去重广播进度、判超时与上报失败。
//
// 注意：SceneLoader 刻意不用协程。yield return 后面只能跟等待指令（null / AsyncOperation /
// WaitForSeconds 等），float 不是等待指令 —— 写成 yield return ao.progress 等于什么都没等，
// 那个 while 会退化成每帧轮询，并且每帧广播一次进度（一次加载几百条事件）。
// 改成 Tick(deltaTime) 驱动后：运行时由 MonoManager 推进，测试里自己调 Tick，超时逻辑才可测。

using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Asset;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NBC.Framework.Scenes
{
    // 主要功能：承载一次场景加载请求 —— 暴露地址、底层句柄、进度与成败，并记录 Tick 推进所需的等待时长。
    // 主要数据成员：Elapsed（已等待秒数）、LastPublishedProgress（上次广播的进度 0..1）、TimeoutSeconds（超时秒数），以及 Location / Handle / IsDone / Succeeded / Error。
    // 主要外部方法：Progress（只读进度）与 ToString（调试文本）；状态写入由 SceneLoader 内部完成。
    // 谁在用它：由 SceneLoader.Load 创建并推进；业务层与 EditMode 测试持有它的返回值来轮询或配合事件使用。
    /// <summary>一次场景加载请求：可轮询进度与状态，也可订阅 SceneEvents 上的全局事件。</summary>
    public sealed class SceneLoadRequest
    {
        // 已等待的秒数（秒）；由 Tick 每帧累加，完成或超时后不再变化。
        internal float Elapsed;

        // 上一次广播出去的进度（0..1）；初值 -1 保证第一次 Tick 一定会广播，哪怕进度还是 0。
        internal float LastPublishedProgress = -1f;

        // 超时阈值（秒）；小于等于 0 表示不超时。
        internal float TimeoutSeconds;

        // 功能：把地址、底层句柄与超时阈值绑到请求上；不改句柄状态，也不发起加载。
        // 上游：SceneLoader.Load（超时阈值已由它解析成最终值）
        // 下游：无
        internal SceneLoadRequest(string location, ISceneHandle handle, float timeoutSeconds)
        {
            Location = location;
            Handle = handle;
            TimeoutSeconds = timeoutSeconds;
        }

        /// <summary>场景地址（YooAsset 里配的 Address）。</summary>
        public string Location { get; private set; }

        /// <summary>底层场景句柄；对它 Dispose 会卸载场景，业务方不要自己释放。</summary>
        public ISceneHandle Handle { get; private set; }

        /// <summary>是否已结束（成功、失败、取消或超时）；结束后 Tick 不再推进它。</summary>
        public bool IsDone { get; internal set; }

        /// <summary>是否成功；只有 IsDone 为 true 时才有意义。</summary>
        public bool Succeeded { get; internal set; }

        /// <summary>失败原因文本；超时与取消也会有值，成功时为空串。</summary>
        public string Error { get; internal set; } = string.Empty;

        /// <summary>当前进度（0..1）；句柄为空时按 0 处理。</summary>
        public float Progress
        {
            get { return Handle == null ? 0f : Handle.Progress; }
        }

        /// <summary>拼出调试文本：地址、百分比，以及进行中 / 成功 / 失败的状态。</summary>
        public override string ToString()
        {
            // 功能：拼出地址 + 百分比 + 状态的可读文本，只用于日志与调试。
            // 返回：形如 SceneLoadRequest(地址 30% 进行中) 的调试字符串。
            return "SceneLoadRequest(" + Location + " " + (Progress * 100f).ToString("F0") +
                   "% " + (IsDone ? (Succeeded ? "成功" : "失败:" + Error) : "进行中") + ")";
        }
    }

    // 主要功能：驱动所有进行中的场景加载请求 —— 收尾完成、累加等待判超时、按阈值去重广播进度与成败。
    // 主要数据成员：m_pending（进行中的请求表）、ProgressEpsilon（进度广播阈值，0..1 的比例）、DefaultTimeoutSeconds（默认超时，秒）。
    // 主要外部方法：Load / Abort / Tick，以及 PendingCount、DefaultTimeoutSeconds 两个属性。
    // 谁在用它：MonoManager 每帧经 OnUpdate 调 Tick（仅播放模式）；EditMode 测试直接调 Load / Abort / Tick。
    /// <summary>场景加载流程的单例：推进请求、去重广播进度、判超时并上报失败；真正的加载动作交给 AssetManager。</summary>
    public sealed class SceneLoader : Singleton<SceneLoader>
    {
        // 进度广播阈值（0..1 的比例）：新旧进度差小于它就当没变，不广播。
        private const float ProgressEpsilon = 0.001f;

        // 进行中的请求表；增删只发生在 Load / Abort / Tick，Tick 逆序遍历并就地删除。
        private readonly List<SceneLoadRequest> m_pending = new List<SceneLoadRequest>();

        /// <summary>默认超时秒数；小于等于 0 表示不超时。</summary>
        public float DefaultTimeoutSeconds { get; set; } = 30f;

        /// <summary>当前还在进行的请求数，调试面板与测试用。</summary>
        public int PendingCount
        {
            get { return m_pending.Count; }
        }

        /// <summary>发起一次异步场景加载，返回可轮询的请求；推进由 Tick 负责。</summary>
        /// <param name="location">场景地址（YooAsset 的 Address）。</param>
        /// <param name="mode">加载模式：Single 切场景，Additive 叠加。</param>
        /// <param name="timeoutSeconds">超时秒数；小于 0 用默认值，等于 0 表示不超时。</param>
        /// <returns>加载请求；即使底层立刻完成，也要等下一次 Tick 才收尾。</returns>
        public SceneLoadRequest Load(string location, LoadSceneMode mode = LoadSceneMode.Single,
                                     float timeoutSeconds = -1f)
        {
            // 功能：向 AssetManager 要一个底层场景句柄，解析出最终超时阈值，并登记为进行中的请求。
            // 入参：location 场景地址；mode 加载模式；timeoutSeconds 超时秒数（小于 0 表示沿用默认值，0 表示不超时）。
            // 返回：新建的请求；调用方可以轮询它，但不要自己 Dispose 它的 Handle，请求表还要靠它推进。
            // 步骤：
            //   1) 先拿句柄 —— 地址非法或 AssetManager 未初始化会在这里抛异常，请求不会入表
            //   2) 再解析超时：只有负数才回退到 DefaultTimeoutSeconds，0 是「不超时」而不是「用默认值」
            //   3) 最后入表；此刻还没推进，进度要等下一次 Tick 才广播
            // 上游：业务层 / EditMode 测试
            // 下游：AssetManager.LoadSceneAsync（最终落到 YooAsset）
            // 注意：调用前 AssetManager 必须已初始化，否则第一行就抛异常，且不会留下半条请求。
            ISceneHandle handle = AssetManager.Instance.LoadSceneAsync(location, mode);

            float timeout = timeoutSeconds < 0f ? DefaultTimeoutSeconds : timeoutSeconds;
            SceneLoadRequest request = new SceneLoadRequest(location, handle, timeout);
            m_pending.Add(request);
            return request;
        }

        /// <summary>取消一次加载：标记失败、摘表并释放句柄（释放即卸载场景），否则场景会一直占内存。</summary>
        /// <param name="request">要取消的请求；为 null 或已结束时什么都不做。</param>
        public void Abort(SceneLoadRequest request)
        {
            // 功能：把请求标记为「已取消 / 失败」，从请求表摘除并释放句柄，从而卸载场景。
            // 入参：request 目标请求；null 或已结束的请求直接返回，因此重复取消是安全的。
            // 返回：无
            // 步骤：
            //   1) 先判空与已结束 —— 已完成的请求归 Finish / Timeout 管，再处理一次会覆盖真实的成败
            //   2) 再改状态、写「已取消」原因并摘表，避免后续 Tick 又看到它
            //   3) 最后 Dispose 句柄：ISceneHandle.Dispose 会卸载场景，这是取消真正生效的地方
            // 上游：业务层 / EditMode 测试
            // 下游：m_pending、ISceneHandle.Dispose → AssetManager 的卸载
            // 注意：Abort 不广播 LoadFailed（与 Finish / Timeout 不同），取消结果要调用方自己记。
            // 注意：只摘表不 Dispose 的话，那个场景会一直加载并占着内存。
            if (request == null || request.IsDone)
            {
                return;
            }

            request.IsDone = true;
            request.Succeeded = false;
            request.Error = "已取消";
            m_pending.Remove(request);

            if (request.Handle != null)
            {
                request.Handle.Dispose();
            }
        }

        /// <summary>推进所有进行中的请求：收尾已完成的、判超时、按阈值广播进度。</summary>
        /// <param name="deltaTime">本帧经过的秒数（秒）；测试里传 31f 就能一次跨过 30 秒超时。</param>
        public void Tick(float deltaTime)
        {
            // 功能：遍历请求表，先收尾已完成的、再累加等待时间判超时、最后按阈值广播进度。
            // 入参：deltaTime 本帧经过的秒数（秒）。
            // 返回：无
            // 步骤：
            //   1) 表空直接返回 —— 空跑是每帧的常态，不该有额外开销
            //   2) 逆序遍历：收尾与超时都要删掉当前项，倒着走下标才不会错位
            //   3) 先看句柄是否完成：完成就 Finish 并删表，这一帧的 deltaTime 不再计入（已经结束，等待时长没意义）
            //   4) 未完成才累加等待时间；阈值大于 0 且已达到或超过，就 Timeout 并删表
            //   5) 都还在进行中，才按阈值去重广播进度
            // 上游：MonoManager 每帧（经 OnUpdate，仅播放模式）；EditMode 测试自己调
            // 下游：Finish / Timeout / PublishProgress
            // 注意：不要用 foreach 或正序 RemoveAt —— 删完当前项后面的元素会前移，会漏掉请求。
            if (m_pending.Count == 0)
            {
                return;
            }

            for (int i = m_pending.Count - 1; i >= 0; i--)
            {
                SceneLoadRequest request = m_pending[i];

                if (request.Handle != null && request.Handle.IsDone)
                {
                    Finish(request);
                    m_pending.RemoveAt(i);
                    continue;
                }

                request.Elapsed += deltaTime;

                if (request.TimeoutSeconds > 0f && request.Elapsed >= request.TimeoutSeconds)
                {
                    Timeout(request);
                    m_pending.RemoveAt(i);
                    continue;
                }

                PublishProgress(request, request.Progress);
            }
        }

        // 功能：播放模式下把 OnUpdate 挂到 MonoManager，让每帧自动推进 Tick。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 用 Application.isPlaying 挡住 EditMode：EditMode 下 Update 不执行，注册了也不会被调
        //   2) 只在播放模式注册，避免平白创建一个宿主 GameObject
        // 上游：Singleton 第一次访问 Instance 时调用
        // 下游：MonoManager.AddUpdateListener
        // 注意：测试里别指望 OnUpdate —— EditMode 下它不会执行，要自己调 Tick。
        protected override void OnInit()
        {
            if (Application.isPlaying)
            {
                MonoManager.Instance.AddUpdateListener(OnUpdate);
            }
        }

        // 功能：把 Unity 的帧间隔转交给 Tick，是运行时唯一的自动推进入口。
        private void OnUpdate()
        {
            Tick(Time.deltaTime);
        }

        // 功能：给已完成的一次请求定成败，并广播对应的事件。
        // 入参：request 已完成的请求（调用方已保证句柄非空且 IsDone 为 true）。
        // 返回：无
        // 步骤：
        //   1) 先置 IsDone，之后任何 Tick 都不会再推进它
        //   2) 按句柄状态判成败：成功就补发进度 1，再广播成功事件
        //   3) 失败就抄下句柄的错误原因，再广播失败事件
        // 上游：Tick（句柄完成分支）
        // 下游：PublishProgress、EventCenter.Trigger（SceneEvents.LoadSucceeded / LoadFailed）
        // 注意：这里刻意不打 LogError —— 失败原因底层（YooAsset）已经报过，再报一遍只是噪音；
        //       而超时是 SceneLoader 自己判定的、底层不会报，所以 Timeout 里必须打日志。
        private static void Finish(SceneLoadRequest request)
        {
            request.IsDone = true;

            if (request.Handle.Status == AssetStatus.Succeeded)
            {
                request.Succeeded = true;
                PublishProgress(request, 1f);
                EventCenter.Instance.Trigger(SceneEvents.LoadSucceeded, request.Location);
                return;
            }

            request.Succeeded = false;
            request.Error = request.Handle.Error;

            EventCenter.Instance.Trigger(
                SceneEvents.LoadFailed, new SceneFailureInfo(request.Location, request.Error));
        }

        // 功能：判定超时失败、卸载卡住的场景，并广播失败。
        // 入参：request 等待时间已达到阈值的请求。
        // 返回：无
        // 步骤：
        //   1) 置 IsDone / 失败，并写清超时秒数与地址，错误文案带一位小数的秒数便于定位
        //   2) Dispose 句柄：一个永远加载不完的场景不卸会一直占内存，这一步不能省
        //   3) 打 LogError 再广播失败 —— 超时是这里自己判定的，底层不会有日志
        // 上游：Tick（超时分支）
        // 下游：ISceneHandle.Dispose、Debug.LogError、EventCenter.Trigger
        // 注意：超时后必须 Dispose 句柄，它才会真正卸载那个卡住的场景。
        private static void Timeout(SceneLoadRequest request)
        {
            request.IsDone = true;
            request.Succeeded = false;
            request.Error = "场景加载超时（" + request.TimeoutSeconds.ToString("F1") + " 秒）：" + request.Location;

            if (request.Handle != null)
            {
                request.Handle.Dispose();
            }

            Debug.LogError("[SceneLoader] " + request.Error);
            EventCenter.Instance.Trigger(
                SceneEvents.LoadFailed, new SceneFailureInfo(request.Location, request.Error));
        }

        // 功能：只在进度真的变化时广播 SceneEvents.ProgressChanged，把事件数从每帧一条压到每次变化一条。
        // 入参：request 目标请求；progress 本次采到的进度（0..1）。
        // 返回：无
        // 步骤：
        //   1) 与上次广播值比较，差值小于 ProgressEpsilon 就当没变，直接返回
        //   2) 先记下新值再发事件，回调若重入也不会重复广播同一条
        // 上游：Tick（进行中分支）、Finish（成功时补发 1）
        // 下游：EventCenter.Trigger（SceneEvents.ProgressChanged）
        // 注意：阈值判断不能省 —— 省掉就退化成每帧一条，一次加载几百条事件。
        private static void PublishProgress(SceneLoadRequest request, float progress)
        {
            if (Mathf.Abs(progress - request.LastPublishedProgress) < ProgressEpsilon)
            {
                return;
            }

            request.LastPublishedProgress = progress;
            EventCenter.Instance.Trigger(
                SceneEvents.ProgressChanged, new SceneProgressInfo(request.Location, progress));
        }
    }
}
