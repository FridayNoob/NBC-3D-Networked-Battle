// ============================================================================
//  NBC.Framework.Fsm · 状态机的基础类型
//  对应需求：FW-M14（有限状态机基类（分层）、泛型状态、状态切换事件）
//  需求条目：FSM-04（**逻辑帧驱动**）、FSM-09（**纯 C#，可被服务端复用**）
//  完整记录：Docs/06-框架改造记录.md §十八
//
//  ---------------------------------------------------------------------------
//  为什么整个 FSM 是纯 C#（不继承 MonoBehaviour、不用协程）
//  ---------------------------------------------------------------------------
//  这是 FSM-09 的**硬要求**，理由是帧同步：
//      "同样的输入序列 → 同样的结果"
//  如果状态机用 `Update` / `Time.deltaTime` / 协程计时，那么它的推进就**依赖真实时间**，
//  服务端和客户端不可能算出一样的结果。而**服务端根本没有 MonoBehaviour 环境**。
//
//  所以时间必须**由外部注入**（和 A5 的 `Tick(deltaTime)`、A7 的 `Tick(tick)` 同一个手法）：
//      machine.Tick(frame);        // frame 是**逻辑帧号**，由逻辑层给
//
//  ⚠️ 状态里**不许出现 `Time.deltaTime`**。需要"持续 0.3 秒"就换算成帧数
//     （比如 60 帧/秒下 0.3 秒 = 18 帧），用帧计数器判断。
// ============================================================================

#nullable disable
// ↑ 双端共用（服务端也编它，见 Server\NBC.Server.Game.csproj）：服务端开了可空、Unity 没开
//   —— 与 Shared\ 同一处理由（M3-S6b，2026-09-23）。
using System;

namespace NBC.Framework.Fsm
{
    /// <summary>
    /// 一次推进所带的"时间"信息。
    /// <para>
    /// ⚠️ **这里刻意没有"秒"字段。** 逻辑层只能用帧号 ——
    /// FSM-04 要求"用帧计数器，不用协程计时"，FSM-09 要求"服务端可复用"。
    /// 留一个 `DeltaTime` 字段等于**邀请**人写出依赖真实时间的逻辑，
    /// 而那种 bug 在单机上看不出来、一进帧同步就表现为"两端不同步"。
    /// **让它不可能发生，比写一句注释管用。**
    /// </para>
    /// <para>表现层要缓动请自己用 `Time.deltaTime` —— 那不是状态机的职责。</para>
    /// </summary>
    public readonly struct StateTick
    {
        /// <summary>当前**逻辑帧号**（由逻辑层给，不是 Unity 的帧）。</summary>
        public readonly int Frame;

        /// <summary>距上一次推进过了几帧（第一次推进时是 0）。</summary>
        public readonly int DeltaFrames;

        /// <summary>构造。</summary>
        /// <param name="frame">逻辑帧号。</param>
        /// <param name="deltaFrames">相隔帧数。</param>
        public StateTick(int frame, int deltaFrames)
        {
            Frame = frame;
            DeltaFrames = deltaFrames;
        }

        /// <summary>调试文本。</summary>
        /// <returns>可读描述。</returns>
        public override string ToString()
        {
            return "StateTick(frame=" + Frame + " delta=" + DeltaFrames + ")";
        }
    }

    /// <summary>
    /// 一条状态转移记录（FSM-08 的调试可视化靠它）。
    /// </summary>
    public readonly struct StateChangeRecord
    {
        /// <summary>发生转移时的逻辑帧号。</summary>
        public readonly int Frame;

        /// <summary>从哪个状态来。</summary>
        public readonly string From;

        /// <summary>到哪个状态去。</summary>
        public readonly string To;

        /// <summary>是不是**强制**转移（跳过了 `CanEnterFrom` 检查）。</summary>
        public readonly bool Forced;

        /// <summary>构造。</summary>
        /// <param name="frame">逻辑帧号。</param>
        /// <param name="from">原状态名。</param>
        /// <param name="to">目标状态名。</param>
        /// <param name="forced">是否强制。</param>
        public StateChangeRecord(int frame, string from, string to, bool forced)
        {
            Frame = frame;
            From = from;
            To = to;
            Forced = forced;
        }

        /// <summary>调试文本。</summary>
        /// <returns>可读描述。</returns>
        public override string ToString()
        {
            return "[f" + Frame + "] " + From + " -> " + To + (Forced ? " (强制)" : string.Empty);
        }
    }
}
