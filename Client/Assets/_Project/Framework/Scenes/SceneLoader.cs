// ============================================================================
//  NBC.Framework.Scenes · 场景加载流程（进度 / 超时 / 事件广播）
//  替代：唐老师框架 Scenes/ScenesMgr.cs（原 58 行）
//  缺陷编号：FW-11（`yield return ao.progress` —— float 不是合法的等待指令）
//  需求条目：FW-M06（异步加载 + 进度事件 + 加载超时）
//  完整记录：Docs/06-框架改造记录.md §三 FW-11、§十四
//
//  ---------------------------------------------------------------------------
//  一、原版错在哪（FW-11，这条对协程的理解很典型）
//  ---------------------------------------------------------------------------
//      while (!ao.isDone)
//      {
//          EventTrigger("进度条更新", ao.progress);
//          yield return ao.progress;          // ← 作者以为这是"等一会儿"
//      }
//
//  `yield return` 后面跟的**不是"一个值"，而是"一条等待指令"**。Unity 只认
//  `null` / `AsyncOperation` / `WaitForSeconds` / `WaitForEndOfFrame` /
//  `WaitForFixedUpdate` / `WaitUntil` / `WaitWhile` —— **`float` 不在其中**。
//  所以 `yield return ao.progress` **什么都没等**，那个 while 其实是"每帧轮询"。
//
//  两个后果：
//    ① 语义完全错位：`ao.progress`（0~1 的进度值）被当成了"等待时长"
//    ② **每帧都广播一次进度事件** —— 一次加载发几百条；而且参数是 `float`，
//       监听方若按 `int` 注册就会命中 A3 那个类型冲突
//
//  ---------------------------------------------------------------------------
//  二、改造：把"时间"从 Unity 手里拿回来
//  ---------------------------------------------------------------------------
//  本类**不用协程**，而是一个 `Tick(float deltaTime)` 驱动的状态机：
//
//      · 游戏运行时：由 `MonoManager`（A4）每帧调用 `Tick`
//      · 单元测试里：**测试自己调 `Tick`**，可以精确控制"经过了多少秒"
//
//  这是"把时间抽出来"的好处 —— 超时逻辑（30 秒）如果靠真等 30 秒才能测，
//  没人会去测它；而抽成 Tick 之后，一次 `Tick(31f)` 就能测完。
//
//  ---------------------------------------------------------------------------
//  三、进度只在"真的变了"时广播（FW-11 的核心修法）
//  ---------------------------------------------------------------------------
//  用 `Mathf.Approximately` 之外的一个更直观的阈值判断：变化小于
//  `ProgressEpsilon` 就不发。这样一次加载的事件条数 ≈ 进度变化次数，
//  而不是帧数 —— 从"几百条"降到"几条到几十条"。
// ============================================================================

using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Asset;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NBC.Framework.Scenes
{
    /// <summary>
    /// 一次场景加载请求。可以轮询它的 <see cref="Progress"/> / <see cref="IsDone"/>，
    /// 也可以订阅 <see cref="SceneEvents"/> 上的全局事件。
    /// </summary>
    public sealed class SceneLoadRequest
    {
        /// <summary>已等待的秒数。</summary>
        internal float Elapsed;

        /// <summary>上一次广播出去的进度（-1 表示还没广播过）。</summary>
        internal float LastPublishedProgress = -1f;

        /// <summary>超时秒数（&lt;= 0 表示不超时）。</summary>
        internal float TimeoutSeconds;

        internal SceneLoadRequest(string location, ISceneHandle handle, float timeoutSeconds)
        {
            Location = location;
            Handle = handle;
            TimeoutSeconds = timeoutSeconds;
        }

        /// <summary>场景地址。</summary>
        public string Location { get; private set; }

        /// <summary>底层场景句柄。</summary>
        public ISceneHandle Handle { get; private set; }

        /// <summary>是否已结束（成功、失败或超时）。</summary>
        public bool IsDone { get; internal set; }

        /// <summary>是否成功。</summary>
        public bool Succeeded { get; internal set; }

        /// <summary>失败原因（超时也有值）。</summary>
        public string Error { get; internal set; } = string.Empty;

        /// <summary>当前进度 0..1。</summary>
        public float Progress
        {
            get { return Handle == null ? 0f : Handle.Progress; }
        }

        /// <summary>调试文本。</summary>
        public override string ToString()
        {
            return "SceneLoadRequest(" + Location + " " + (Progress * 100f).ToString("F0") +
                   "% " + (IsDone ? (Succeeded ? "成功" : "失败:" + Error) : "进行中") + ")";
        }
    }

    /// <summary>
    /// 场景加载器：负责"进度广播 + 超时 + 失败上报"这些**流程**，
    /// 真正的加载动作交给 <see cref="AssetManager"/>（从而最终交给 YooAsset）。
    /// </summary>
    public sealed class SceneLoader : Singleton<SceneLoader>
    {
        /// <summary>进度变化小于这个值就不广播（避免每帧一条事件）。</summary>
        private const float ProgressEpsilon = 0.001f;

        private readonly List<SceneLoadRequest> m_pending = new List<SceneLoadRequest>();

        /// <summary>默认超时秒数。&lt;= 0 表示不超时。</summary>
        public float DefaultTimeoutSeconds { get; set; } = 30f;

        /// <summary>当前还在进行的请求数（调试面板用）。</summary>
        public int PendingCount
        {
            get { return m_pending.Count; }
        }

        /// <summary>
        /// 发起一次场景加载。加载是异步的，靠 <see cref="Tick"/> 推进。
        /// </summary>
        /// <param name="location">场景地址。</param>
        /// <param name="mode">加载模式。</param>
        /// <param name="timeoutSeconds">超时秒数；&lt; 0 表示用 <see cref="DefaultTimeoutSeconds"/>。</param>
        /// <returns>加载请求。</returns>
        public SceneLoadRequest Load(string location, LoadSceneMode mode = LoadSceneMode.Single,
                                     float timeoutSeconds = -1f)
        {
            ISceneHandle handle = AssetManager.Instance.LoadSceneAsync(location, mode);

            float timeout = timeoutSeconds < 0f ? DefaultTimeoutSeconds : timeoutSeconds;
            SceneLoadRequest request = new SceneLoadRequest(location, handle, timeout);
            m_pending.Add(request);
            return request;
        }

        /// <summary>
        /// 取消一次加载：**会卸载那个还没加载完的场景**，否则它会一直占着内存。
        /// </summary>
        /// <param name="request">要取消的请求。</param>
        public void Abort(SceneLoadRequest request)
        {
            if (request == null || request.IsDone)
            {
                return;
            }

            request.IsDone = true;
            request.Succeeded = false;
            request.Error = "已取消";
            m_pending.Remove(request);

            // Dispose 会触发卸载 —— 见 ISceneHandle 的说明
            if (request.Handle != null)
            {
                request.Handle.Dispose();
            }
        }

        /// <summary>
        /// 推进所有进行中的请求。
        /// <para>
        /// 游戏里由 <see cref="MonoManager"/> 每帧调用；**测试里可以直接调用**，
        /// 从而精确控制"过了多少秒"（超时逻辑因此可测）。
        /// </para>
        /// </summary>
        /// <param name="deltaTime">本帧经过的秒数。</param>
        public void Tick(float deltaTime)
        {
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

        /// <summary>
        /// 注册到 MonoManager（仅播放模式）。
        /// <para>
        /// **刻意用 `Application.isPlaying` 挡住 EditMode**：EditMode 下 `Update` 不执行
        /// （A1 实测结论），注册了也不会被调用，反而会平白创建一个宿主 GameObject。
        /// 测试里改为**直接调 `Tick`** —— 这正是把时间抽出来的价值。
        /// </para>
        /// </summary>
        protected override void OnInit()
        {
            if (Application.isPlaying)
            {
                MonoManager.Instance.AddUpdateListener(OnUpdate);
            }
        }

        private void OnUpdate()
        {
            Tick(Time.deltaTime);
        }

        /// <summary>结束一次请求：判定成败并广播。</summary>
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

        /// <summary>超时：判定失败、**卸载那个场景**、并广播。</summary>
        private static void Timeout(SceneLoadRequest request)
        {
            request.IsDone = true;
            request.Succeeded = false;
            request.Error = "场景加载超时（" + request.TimeoutSeconds.ToString("F1") + " 秒）：" + request.Location;

            // ⚠️ 必须卸载：一个"永远加载不完"的场景如果不卸，会一直占着内存
            if (request.Handle != null)
            {
                request.Handle.Dispose();
            }

            Debug.LogError("[SceneLoader] " + request.Error);
            EventCenter.Instance.Trigger(
                SceneEvents.LoadFailed, new SceneFailureInfo(request.Location, request.Error));
        }

        /// <summary>
        /// 广播进度 —— **只在真的变化时发**（FW-11 的核心修法）。
        /// </summary>
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
