// ============================================================================
//  NBC.Framework · 事件通道的统计快照
//  来源：为 M1-A3 新增；对应需求文档 §7.1.2 FW-M02、验收 V7（调试面板可看监听数量）
//
//  为什么要单独一个类型，而不让面板直接读 EventCenter 的内部字典：
//    ① 内部字典是 `private`，面板不该也不需要拿到它（原框架的 `public` 容器问题见 P-11）；
//    ② 只读快照保证面板拿到的是一份**一致的值**，不会在遍历时被派发过程改动；
//    ③ 面板只依赖这个 struct，于是它不必知道事件中心内部是字典还是别的结构。
// ============================================================================

using System;

namespace NBC.Framework
{
    /// <summary>
    /// 某个事件在某一时刻的状态快照。
    /// </summary>
    public readonly struct EventChannelStat
    {
        /// <summary>事件标识。</summary>
        public readonly EventId Id;

        /// <summary>参数类型；无参数事件为 null。</summary>
        public readonly Type PayloadType;

        /// <summary>当前监听者数量。</summary>
        public readonly int ListenerCount;

        /// <summary>构造快照。由 <see cref="EventCenter"/> 内部调用。</summary>
        public EventChannelStat(EventId id, Type payloadType, int listenerCount)
        {
            Id = id;
            PayloadType = payloadType;
            ListenerCount = listenerCount;
        }

        /// <summary>一行摘要，给调试面板直接用。</summary>
        public override string ToString()
        {
            string type = PayloadType == null ? "-" : PayloadType.Name;
            return Id.Name + "  [" + type + "]  监听者=" + ListenerCount;
        }
    }
}
