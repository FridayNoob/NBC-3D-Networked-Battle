// ============================================================================
//  NBC.Framework · 事件标识（FW-06 的修法核心）
//  替代：唐老师框架 Event/EventCenter.cs 里直接用的 `string name`
//  缺陷编号：FW-06（string key 拼错 → 运行期静默不触发）
//  完整记录：Docs/06-框架改造记录.md §三 FW-06
//
//  ---------------------------------------------------------------------------
//  一、原版错在哪
//  ---------------------------------------------------------------------------
//  原框架的每个事件接口首参都是 `string name`：
//
//      EventCenter.GetInstance().AddEventListener("某键按下", OnKeyDown);
//      ScenesMgr.cs:51   EventTrigger("进度条更新", ao.progress);
//
//  拼错一个字符 -> 字典里找不到 key -> `EventTrigger` 里 `ContainsKey` 为 false
//  -> **静默返回**。不报错、不警告、不触发。表现为"这个回调怎么不执行"，
//  排查成本极高。原版甚至连一个常量类都没有，字面量散落在框架各处。
//
//  ---------------------------------------------------------------------------
//  二、修法：一个只认"声明"的强类型标识
//  ---------------------------------------------------------------------------
//      public static class BattleEvents
//      {
//          public static readonly EventId HeroDied = EventId.Declare("Battle.HeroDied");
//      }
//
//      EventCenter.Instance.AddEventListener(BattleEvents.HeroDied, OnHeroDied);
//
//  三条保证：
//    ① **构造函数是私有的**，唯一入口是 `Declare`。于是"随便写个字符串当事件"
//       在 C# 层面就写不出来 —— 你只能引用一个已声明的常量，而符号写错编译不过。
//    ② `Declare` 会校验名字（非空、不含空白），把"手滑"挡在**声明处**。
//    ③ 各模块在**自己的文件里**声明自己的事件 —— 不需要所有人往同一个 enum 里加，
//       避免多人协作时改同一个文件必冲突（这是本项目选 struct 而不是 enum 的主要原因）。
//
//  ---------------------------------------------------------------------------
//  三、为什么是 readonly struct 而不是 enum / const string
//  ---------------------------------------------------------------------------
//  | 方案 | 好处 | 代价 |
//  | --- | --- | --- |
//  | `enum` | 最直观，字典 key 不装箱 | 所有事件挤在一个 enum 里：多人协作改同一文件必冲突；模块之间被迫互相知道对方的事件；日志里只有数字，排查时要反查 |
//  | `const string` | 改动最小，日志直接可读 | 防不住"随手写个字符串字面量"，FW-06 只算治了一半 |
//  | **`readonly struct`（选它）** | 类型安全（不能把别的字符串传进来）+ 零装箱（实现 IEquatable）+ 日志可读 + 各模块自己声明 | 多写约 40 行 |
//
//  **零装箱**这一条要说清楚：`Dictionary<EventId, T>` 用 `EqualityComparer<EventId>.Default`，
//  只要 `EventId` 实现了 `IEquatable<EventId>`，就不会走 `object.Equals` 那条装箱路径。
//  所以"用 struct 做 key 会装箱"是个常见误解 —— **取决于有没有实现 IEquatable**。
//
//  ⚠️ 刻意**不提供**从 string 隐式转换的运算符。一旦有了 `implicit operator EventId(string)`，
//     "随手写字符串"这条路就又通了，上面第 ① 条保证立刻失效。
// ============================================================================

using System;
using UnityEngine;

namespace NBC.Framework
{
    /// <summary>
    /// 事件的强类型标识。只能通过 <see cref="Declare"/> 声明得到，无法在别处凭空构造。
    /// </summary>
    public readonly struct EventId : IEquatable<EventId>
    {
        /// <summary>事件名。`default(EventId)` 时该字段为 null。</summary>
        private readonly string m_name;

        /// <summary>
        /// 私有构造函数：唯一入口是 <see cref="Declare"/>。
        /// 这是"外部无法凭空造一个事件标识"的技术保证。
        /// </summary>
        private EventId(string name)
        {
            m_name = name;
        }

        /// <summary>事件名。未初始化时为 <see cref="string.Empty"/>。</summary>
        public string Name
        {
            get { return m_name ?? string.Empty; }
        }

        /// <summary>是否是有效的事件标识（<c>default(EventId)</c> 为 false）。</summary>
        public bool IsValid
        {
            get { return !string.IsNullOrEmpty(m_name); }
        }

        /// <summary>
        /// 声明一个事件标识。**这是唯一的创建入口**，应在常量类的静态字段里调用一次。
        /// </summary>
        /// <param name="name">
        /// 事件名。约定用 `模块.事件` 形式，例如 `"Battle.HeroDied"` ——
        /// 这样日志里一眼能看出事件属于哪个模块。
        /// </param>
        /// <returns>可用于注册 / 触发的事件标识。</returns>
        public static EventId Declare(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    "[EventId] 事件名不能为空。请传一个稳定的标识符，例如 \"Battle.HeroDied\"。", nameof(name));
            }

            for (int i = 0; i < name.Length; i++)
            {
                if (char.IsWhiteSpace(name[i]))
                {
                    // 空白字符几乎总是手滑。在这里拦下来，比在运行期查"为什么没触发"便宜得多。
                    throw new ArgumentException(
                        "[EventId] 事件名不能包含空白字符：" + name, nameof(name));
                }
            }

            if (name.IndexOf('.') < 0)
            {
                // 不强制，只提醒：命名建议带模块前缀，方便日志定位。
                Debug.LogWarning("[EventId] 建议事件名带上模块前缀（例如 \"Battle.HeroDied\"），当前为：" + name);
            }

            return new EventId(name);
        }

        /// <summary>按名字比较（序数比较，大小写敏感）。</summary>
        public bool Equals(EventId other)
        {
            return string.Equals(m_name, other.m_name, StringComparison.Ordinal);
        }

        /// <summary>按名字比较。</summary>
        public override bool Equals(object obj)
        {
            return obj is EventId && Equals((EventId)obj);
        }

        /// <summary>用名字的哈希值。</summary>
        public override int GetHashCode()
        {
            return m_name == null ? 0 : m_name.GetHashCode();
        }

        /// <summary>返回事件名，便于日志与调试面板直接打印。</summary>
        public override string ToString()
        {
            return Name;
        }

        /// <summary>相等比较。</summary>
        public static bool operator ==(EventId left, EventId right)
        {
            return left.Equals(right);
        }

        /// <summary>不等比较。</summary>
        public static bool operator !=(EventId left, EventId right)
        {
            return !left.Equals(right);
        }
    }
}
