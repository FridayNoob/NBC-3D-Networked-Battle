// 这个文件里有：
//   EventId（readonly struct）：事件的强类型标识，只能经 Declare 声明，代替裸字符串事件名，拼错编译不过

#nullable disable
// 注意：双端共用（服务端也编它，见 Server\NBC.Server.Game.csproj）：服务端开了可空、Unity 没开，
//   这里统一关掉，与 Shared\ 是同一条理由。
using System;
using UnityEngine;

namespace NBC.Framework
{
    // 主要功能：事件的强类型标识，只能经 Declare 声明，代替裸字符串事件名；拼错是编译错误，不会到运行期静默不触发。
    // 主要数据成员：m_name（事件名，private readonly string，default(EventId) 时为 null）。
    // 主要外部方法：Declare（唯一创建入口）、Name / IsValid（只读属性）、Equals / GetHashCode / ToString 与 == / !=。
    // 谁在用它：事件中心 EventCenter 拿它当字典键；Game 各模块的事件常量类（Battle / Quest / Achievement / World）与 SceneEvents 用它声明；Framework 的 FSM（SendEvent / OnEvent）与服务端编源码复用也用它。
    // 注意：做成 readonly struct 而不是 enum，是为了让各模块在自己的文件里声明事件，多人协作不必抢改同一个 enum，日志里也能直接读到事件名；取舍细节见 Docs\06-框架改造记录.md。
    // 注意：实现 IEquatable<EventId> 是为了 Dictionary 用 EqualityComparer 时走强类型比较、不装箱；没有它才会退回 object.Equals 那条装箱路径。
    /// <summary>事件的强类型标识：只能经 Declare 声明，default(EventId) 无效。</summary>
    public readonly struct EventId : IEquatable<EventId>
    {
        private readonly string m_name;   // 事件名，取值范围是「非空、不含空白、形如模块.事件」；default(EventId) 时为 null，经 Name 取成空串

        // 功能：把声明好的事件名存进只读字段；外部拿不到构造函数，只能经 Declare 走到这里。
        // 入参：name 事件名，调用方（Declare）已保证非空且不含空白字符。
        // 返回：无。
        // 步骤：
        //   1) 直接写进只读字段 —— 校验全部留在 Declare，避免两处规则各说一套
        //   2) 字段只读，标识造好即固定，可以安全地当字典键
        // 上游：Declare
        // 下游：无
        private EventId(string name)
        {
            m_name = name;
        }

        /// <summary>事件名；未声明（default）时返回空串，调用方不用判空。</summary>
        public string Name
        {
            get { return m_name ?? string.Empty; }
        }

        /// <summary>是否有效；default(EventId) 为 false，用来在注册前挡掉没声明过的标识。</summary>
        public bool IsValid
        {
            get { return !string.IsNullOrEmpty(m_name); }
        }

        /// <summary>声明一个事件标识（唯一创建入口）；名字建议用「模块.事件」形式，非空且不含空白。</summary>
        public static EventId Declare(string name)
        {
            // 功能：校验事件名并造出唯一标识，把「拼错」从运行期静默不触发提前到编译期。
            // 入参：name 事件名，建议「模块.事件」形式（如 Battle.HeroDied）；空名与含空白字符的名字会被抛异常拒绝。
            // 返回：新的事件标识，可直接用于 EventCenter 的注册与触发。
            // 步骤：
            //   1) 先判空：空名没有标识作用，还会和 default(EventId) 取出来的空串撞成同一个键
            //   2) 再逐字符查空白：空白几乎总是手滑，在声明处拦下比到运行期查「为什么不触发」便宜得多
            //   3) 最后看有没有模块前缀；没有只告警不抛错 —— 前缀只是建议，方便日志定位
            //   4) 校验全过才 new，任何非法输入都不会留下半个标识
            // 上游：各模块的事件常量类（BattleEvents / QuestEvents / AchievementEvents / WorldEvents / SceneEvents）与测试
            // 下游：私有构造函数
            // 注意：刻意不提供从 string 的隐式转换 —— 一旦有了它，随手写字符串的路又通了，拼错依旧会在运行期静默。
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    "[EventId] 事件名不能为空。请传一个稳定的标识符，例如 \"Battle.HeroDied\"。", nameof(name));
            }

            for (int i = 0; i < name.Length; i++)
            {
                if (char.IsWhiteSpace(name[i]))
                {
                    throw new ArgumentException(
                        "[EventId] 事件名不能包含空白字符：" + name, nameof(name));
                }
            }

            if (name.IndexOf('.') < 0)
            {
                Debug.LogWarning("[EventId] 建议事件名带上模块前缀（例如 \"Battle.HeroDied\"），当前为：" + name);
            }

            return new EventId(name);
        }

        /// <summary>按事件名比较，序数比较、大小写敏感。</summary>
        public bool Equals(EventId other)
        {
            // 功能：比较两边的原始 m_name，null 与 null 视为相等。
            return string.Equals(m_name, other.m_name, StringComparison.Ordinal);
        }

        /// <summary>按事件名比较；非 EventId 或 null 一律不相等。</summary>
        public override bool Equals(object obj)
        {
            // 功能：先判类型再转，非 EventId（含 null）直接 false，不会抛 InvalidCastException。
            return obj is EventId && Equals((EventId)obj);
        }

        /// <summary>事件名的哈希值；default(EventId) 为 0。</summary>
        public override int GetHashCode()
        {
            // 功能：返回 m_name 的哈希，null 固定给 0，default(EventId) 也能安全求哈希。
            return m_name == null ? 0 : m_name.GetHashCode();
        }

        /// <summary>返回事件名，日志与调试面板可直接打印。</summary>
        public override string ToString()
        {
            // 功能：直接返回 Name，未声明时打出来是空串而不是 null。
            return Name;
        }

        /// <summary>按事件名判等。</summary>
        public static bool operator ==(EventId left, EventId right)
        {
            // 功能：转给 left.Equals(right)；两边都是 default 时也算相等。
            return left.Equals(right);
        }

        /// <summary>按事件名判不等。</summary>
        public static bool operator !=(EventId left, EventId right)
        {
            // 功能：== 的取反，与 == 严格互补。
            return !left.Equals(right);
        }
    }
}
