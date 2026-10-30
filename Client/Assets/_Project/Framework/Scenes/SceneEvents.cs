// 这个文件里有：
//   SceneProgressInfo（结构体）：一次场景加载的进度快照，带场景地址与 0..1 的进度
//   SceneFailureInfo（结构体）：一次场景加载失败的快照，带场景地址与给人看的原因
//   SceneEvents（静态类）：场景加载对外广播的三个事件标识，进度变化、成功、失败各一个

using NBC.Framework;

namespace NBC.Framework.Scenes
{
    // 主要功能：把「哪个场景、加载到几成」打包成一次进度快照，供进度条与加载遮罩读取。
    // 主要数据成员：Location（场景地址）、Progress（加载进度，0..1 的归一化比例，1 = 加载完成）。
    // 主要外部方法：构造函数、ToString（调试文本）。
    // 谁在用它：SceneLoader 在进度真的变化时造并广播；Framework.UI 的 LoadingMaskController 与 EditMode 的 SceneLoaderTests / LoadingMaskControllerTests 订阅后读。
    // 注意：SceneLoader 只在进度变化不小于 0.001 时才广播，订阅方别指望每帧都有回调。
    // 注意：Progress 是 0..1 的比例，不是百分比也不是字节数；要显示成百分比得先乘 100。
    /// <summary>一次场景加载的进度快照：场景地址 + 0..1 的归一化进度。</summary>
    public readonly struct SceneProgressInfo
    {
        /// <summary>场景地址（发起加载时传的地址串，原样回传，不做路径改写）。</summary>
        public readonly string Location;

        /// <summary>加载进度，0..1 的归一化比例，1 = 加载完成。</summary>
        public readonly float Progress;

        /// <summary>构造一次进度事件的载荷。</summary>
        // 功能：把场景地址与进度装成一次进度事件的载荷。
        // 入参：location 场景地址（原样存下）；progress 加载进度（0..1 的比例，这里不做钳位）
        // 返回：无（构造函数不返回值）
        // 步骤：
        //   1) 两个值直接写进只读字段，不做范围钳位 —— 传进来什么就存什么
        //   2) 字段只读，载荷造好即固定，订阅方拿到的是快照，不会被别人改掉
        // 上游：SceneLoader.PublishProgress
        // 下游：无
        public SceneProgressInfo(string location, float progress)
        {
            Location = location;
            Progress = progress;
        }

        /// <summary>调试文本：地址 + 取整后的百分比。</summary>
        // 功能：把地址与百分比拼成一行调试文本，形如「Level1 42%」。
        // 入参：无
        // 返回：给人看的一行文本，格式固定，不要拿去解析或本地化
        // 步骤：
        //   1) 先乘 100 再按 F0 取整，日志里不出现 0.4231234 这种噪声
        //   2) 地址前置，打印日志时能一眼看出是哪个场景
        // 上游：日志与调试面板打印本结构体时自动调用
        // 下游：无
        public override string ToString()
        {
            return Location + " " + (Progress * 100f).ToString("F0") + "%";
        }
    }

    // 主要功能：把「哪个场景、为什么没加载出来」打包成一次失败快照，供遮罩与日志显示。
    // 主要数据成员：Location（场景地址）、Error（给人看的失败原因，超时时带已等秒数）。
    // 主要外部方法：构造函数、ToString（调试文本）。
    // 谁在用它：SceneLoader 在加载失败或超时时造并广播；LoadingMaskController 读它显示错误，SceneLoaderTests / LoadingMaskControllerTests 订阅后断言。
    // 注意：Error 是已经拼好的人话，直接显示即可，不要当错误码解析。
    /// <summary>一次场景加载失败的快照：场景地址 + 失败原因。</summary>
    public readonly struct SceneFailureInfo
    {
        /// <summary>场景地址（发起加载时传的地址串，原样回传）。</summary>
        public readonly string Location;

        /// <summary>失败原因文本（超时时形如「场景加载超时（30.0 秒）：地址」；取消那一路填「已取消」但不走本事件）。</summary>
        public readonly string Error;

        /// <summary>构造一次失败事件的载荷。</summary>
        // 功能：把场景地址与失败原因装成一次失败事件的载荷。
        // 入参：location 场景地址（原样存下）；error 失败原因（会直接显示给玩家，别塞英文堆栈）
        // 返回：无（构造函数不返回值）
        // 步骤：
        //   1) 两个值直接写进只读字段，不做校验 —— 原因已由 SceneLoader 拼好
        //   2) 字段只读，载荷造好即固定，订阅方拿到就能直接显示
        // 上游：SceneLoader.Finish（底层报错）与 SceneLoader.Timeout（自己判定超时）
        // 下游：无
        public SceneFailureInfo(string location, string error)
        {
            Location = location;
            Error = error;
        }

        /// <summary>调试文本：「地址 失败：原因」一行。</summary>
        // 功能：把地址与原因拼成一行失败调试文本。
        // 入参：无
        // 返回：给人看的一行文本，形如「Level1 失败：下载超时」
        // 步骤：
        //   1) 直接串地址与原因，不加时间戳与堆栈 —— 调用方打日志时自己补上下文
        // 上游：日志与调试面板打印本结构体时自动调用
        // 下游：无
        public override string ToString()
        {
            return Location + " 失败：" + Error;
        }
    }

    // 主要功能：声明场景加载流程对外广播的三个事件，让进度、成功与失败都能被订阅。
    // 主要数据成员：ProgressChanged、LoadSucceeded、LoadFailed（三个 EventId 常量，事件名形如「Scene.ProgressChanged」）。
    // 主要外部方法：无（这里只放事件标识，真正的触发在 SceneLoader）。
    // 谁在用它：SceneLoader 触发；Framework.UI 的 LoadingMaskController 与 EditMode 的 SceneLoaderTests / LoadingMaskControllerTests 订阅。
    // 注意：事件标识用 EventId.Declare 声明而不是裸字符串，拼错是编译错误，不会像字符串那样到运行期才静默不触发。
    // 注意：订阅方的泛型参数必须与载荷类型一一对应，EventCenter 注册时就校验，不一致当场抛异常，不会静默丢事件。
    // 注意：取消（SceneLoader.Abort）只把请求标成失败并填「已取消」，不发 LoadFailed —— 想感知取消只能轮询请求的 IsDone 与 Succeeded。
    /// <summary>场景加载对外广播的三个事件标识。</summary>
    public static class SceneEvents
    {
        /// <summary>进度真的变了（变化不小于 0.001）；载荷：SceneProgressInfo。</summary>
        public static readonly EventId ProgressChanged = EventId.Declare("Scene.ProgressChanged");

        /// <summary>加载完成、场景已可用；载荷是场景地址（string），不是结构体。</summary>
        public static readonly EventId LoadSucceeded = EventId.Declare("Scene.LoadSucceeded");

        /// <summary>加载失败或超时；载荷：SceneFailureInfo（地址 + 原因）。</summary>
        public static readonly EventId LoadFailed = EventId.Declare("Scene.LoadFailed");
    }
}
