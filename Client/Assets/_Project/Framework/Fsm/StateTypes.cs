// 这个文件里有：
//   StateTick（结构体）：一次推进所带的逻辑帧信息，只给帧号与相隔帧数
//   StateChangeRecord（结构体）：一条状态转移记录，给事件广播、历史回看与调试可视化用

#nullable disable
// 双端共用：服务端也编这个文件（Server\NBC.Server.Game.csproj），
// 服务端开了可空、Unity 没开，所以在这里统一关掉 —— 与 Shared\ 同一条理由。
using System;

namespace NBC.Framework.Fsm
{
    // 主要功能：一次推进所带的「时间」信息，只用逻辑帧号表达，刻意不留秒。
    // 主要数据成员：Frame（逻辑帧号）、DeltaFrames（距上次推进的帧数，首次为 0）。
    // 主要外部方法：构造函数、ToString。
    // 谁在用它：StateMachine / HierarchicalStateMachine 每次 Tick 时构造，经 OnUpdate 交给当前状态；生产代码尚未接入，目前只有测试在用。
    // 注意：整个 Fsm 是纯 C#（不继承 MonoBehaviour、不用协程），时间只能由外部用 Tick(frame) 注入；
    //   帧同步要的是「同样的输入序列 → 同样的结果」，所以这里不给秒或 DeltaTime ——
    //   逻辑层一旦能拿到真实时间，就会有人写出依赖帧率的逻辑，单机看不出来、一进帧同步就表现为两端不同步。
    //   服务端也没有 MonoBehaviour 环境；表现层要缓动请自己用 Time.deltaTime，那不是状态机的职责。
    //   状态里不要出现 Time.deltaTime：要「持续 0.3 秒」就按帧率换算成帧数（60 帧/秒下是 18 帧）。
    /// <summary>一次推进所带的逻辑帧信息，由状态机传给当前状态。</summary>
    public readonly struct StateTick
    {
        /// <summary>逻辑帧号，由逻辑层给；非负整数、只增不减，与 Unity 的帧无关。</summary>
        public readonly int Frame;

        /// <summary>距上一次推进过了几帧，不小于 0；首次推进为 0，同一帧重复推进也是 0。</summary>
        public readonly int DeltaFrames;

        /// <summary>构造一次推进的快照。</summary>
        /// <param name="frame">逻辑帧号，由逻辑层给。</param>
        /// <param name="deltaFrames">距上一次推进的帧数，首次为 0。</param>
        public StateTick(int frame, int deltaFrames)
        {
            // 功能：把帧号与相隔帧数固定成一次推进的快照，供状态在 OnUpdate 里读。
            // 入参：frame 逻辑帧号；deltaFrames 距上次推进的帧数（不小于 0）
            // 返回：无
            // 步骤：两个字段都是 readonly，构造时一次写死 —— 推进信息会沿状态链传给多个状态，不能在中途被改。
            // 上游：状态机推进时
            // 下游：无
            Frame = frame;
            DeltaFrames = deltaFrames;
        }

        /// <summary>输出便于打日志的调试文本。</summary>
        /// <returns>形如 StateTick(frame=12 delta=1) 的字符串。</returns>
        public override string ToString()
        {
            // 功能：把帧号与相隔帧数拼成一行调试文本。
            // 入参：无
            // 返回：形如 StateTick(frame=12 delta=1) 的字符串；只供人读，不要反向解析。
            // 步骤：按固定字段顺序拼接，保证日志格式稳定、便于肉眼比对。
            // 上游：日志与调试面板
            // 下游：无
            return "StateTick(frame=" + Frame + " delta=" + DeltaFrames + ")";
        }
    }

    // 主要功能：一条状态转移记录，说明「第几帧、从哪来、到哪去、是不是强制」。
    // 主要数据成员：Frame（逻辑帧号）、From / To（状态名）、Forced（是否跳过准入检查）。
    // 主要外部方法：构造函数、ToString。
    // 谁在用它：状态机记录转移历史与广播事件时创建，经 StateChanged / TransitionRejected 事件和 CopyHistory 交给调试面板；生产代码尚未接入，目前只有测试在用。
    // 注意：历史默认只留最近 10 条（容量在状态机构造时指定），更早的会被挤掉，别假设能拿到全量。
    /// <summary>一条状态转移记录，供事件广播、历史回看与调试可视化使用。</summary>
    public readonly struct StateChangeRecord
    {
        /// <summary>发生转移时的逻辑帧号；Start 早于首次推进时为 -1，其余为非负整数。</summary>
        public readonly int Frame;

        /// <summary>原状态名（非空）；起始记录里与 To 相同（都是初始状态名）。</summary>
        public readonly string From;

        /// <summary>目标状态名（非空）；被拒绝时是这次请求的目标，实际状态并没有变。</summary>
        public readonly string To;

        /// <summary>是否强制转移（跳过目标状态的 CanEnterFrom 检查）；被拒绝的记录恒为 false。</summary>
        public readonly bool Forced;

        /// <summary>构造一条状态转移记录。</summary>
        /// <param name="frame">逻辑帧号。</param>
        /// <param name="from">原状态名。</param>
        /// <param name="to">目标状态名。</param>
        /// <param name="forced">true 表示跳过目标状态的准入检查。</param>
        public StateChangeRecord(int frame, string from, string to, bool forced)
        {
            // 功能：把一次转移的帧号、两端状态名与是否强制记录成不可变快照。
            // 入参：frame 逻辑帧号；from / to 状态名；forced 是否强制转移
            // 返回：无
            // 步骤：四个只读字段一次写死 —— 事件订阅者与历史列表拿到同一份记录，事后不会被谁改掉。
            // 上游：状态机的转移与拒绝分支
            // 下游：事件订阅者与历史列表
            Frame = frame;
            From = from;
            To = to;
            Forced = forced;
        }

        /// <summary>输出便于打日志的调试文本。</summary>
        /// <returns>形如 [f12] Idle -> Run 的字符串；强制转移末尾会多出「(强制)」。</returns>
        public override string ToString()
        {
            // 功能：把帧号、两端状态名与是否强制拼成一行调试文本。
            // 入参：无
            // 返回：形如 [f12] Idle -> Run 的字符串；强制转移末尾会多出「(强制)」。
            // 步骤：按帧号、From、To、强制标记的顺序拼接，保证日志格式稳定、便于肉眼比对。
            // 上游：日志与调试面板
            // 下游：无
            return "[f" + Frame + "] " + From + " -> " + To + (Forced ? " (强制)" : string.Empty);
        }
    }
}
