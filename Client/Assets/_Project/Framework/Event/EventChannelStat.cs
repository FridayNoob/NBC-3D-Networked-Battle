// 这个文件里有：
//   EventChannelStat（结构体）：某个事件在某一时刻的只读统计快照，含事件标识、参数类型与监听者数量

using System;

namespace NBC.Framework
{
    // 主要功能：给调试面板提供某个事件的只读状态快照，让面板不必直接读事件中心的内部字典。
    // 主要数据成员：Id（事件标识）、PayloadType（参数类型，无参数事件为 null）、ListenerCount（监听者数量，≥ 0）。
    // 主要外部方法：构造函数（由事件中心填充）、ToString（面板直接显示的一行摘要）。
    // 谁在用它：EventCenter.CopyChannelStats 填充后交给调试面板显示；EditMode 的 EventCenterTests 直接断言字段。
    /// <summary>某个事件在某一时刻的只读状态快照，供调试面板显示监听数量。</summary>
    public readonly struct EventChannelStat
    {
        /// <summary>事件标识。</summary>
        public readonly EventId Id;

        /// <summary>参数类型；无参数事件为 null。</summary>
        public readonly Type PayloadType;

        /// <summary>当前监听者数量，取值 ≥ 0。</summary>
        public readonly int ListenerCount;

        /// <summary>把事件中心某一时刻的通道状态装进一份只读快照。</summary>
        public EventChannelStat(EventId id, Type payloadType, int listenerCount)
        {
            // 功能：把事件中心内部的三个字段装进一份构造后不再变化的快照。
            // 入参：id 事件标识；payloadType 参数类型，无参数事件传 null；listenerCount 当时的监听者数量，≥ 0
            // 返回：无（构造函数）
            // 步骤：依次赋给三个只读字段，之后谁也不能改，面板拿到的值不会在遍历途中被派发改动。
            // 上游：EventCenter.CopyChannelStats
            // 下游：无
            // 注意：必须是拷贝出来的快照，不能把内部字典的引用交出去；否则派发中增删订阅会让面板读到不一致的数据。
            Id = id;
            PayloadType = payloadType;
            ListenerCount = listenerCount;
        }

        /// <summary>拼一行可直接显示的摘要，给调试面板用。</summary>
        public override string ToString()
        {
            // 功能：把事件标识、参数类型与监听者数量拼成一行，省得面板自己格式化。
            // 入参：无
            // 返回：形如「事件名  [参数类型名]  监听者=数量」；无参数事件时方括号里显示为 -
            // 步骤：
            //   1) 取参数类型名，null 时用 - 代替，避免拼出空字符串
            //   2) 按固定顺序拼事件名、类型名、监听者数量，便于面板横向对比
            // 上游：调试面板 / 日志输出
            // 下游：无
            string type = PayloadType == null ? "-" : PayloadType.Name;
            return Id.Name + "  [" + type + "]  监听者=" + ListenerCount;
        }
    }
}
