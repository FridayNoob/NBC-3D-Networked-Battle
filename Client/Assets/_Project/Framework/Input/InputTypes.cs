// ============================================================================
//  NBC.Framework.Input · 输入层的基础类型
//  对应需求：FW-M07（键位映射可配置、**输入采集与"输入命令"生成分离**、暂停/禁用）
//  缺陷编号：审计新增 P-14（每帧盲发事件）、FW-12（硬编码中文事件名）
//  完整记录：Docs/06-框架改造记录.md §四 P-14、§十五
//
//  ---------------------------------------------------------------------------
//  为什么必须分两层：采集 ≠ 命令
//  ---------------------------------------------------------------------------
//      玩家按键 → 采集（IInputSource） → 生成"输入命令"（InputCommand） → 发给服务端
//                                            ↑ 带帧号、可重放、确定性
//
//  · **采集层**关心"这一刻键盘/手柄是什么状态" —— 与设备相关，**不可确定性重放**
//  · **命令层**关心"这一帧玩家想做什么" —— 与设备**无关**、可序列化、可发给服务端
//
//  帧同步要求"同样的命令序列 → 同样的结果"。如果逻辑层直接读 `Input.GetKey`，
//  服务端根本没有键盘，重放也无从谈起。**所以这两层必须分开，不是风格问题。**
//
//  ---------------------------------------------------------------------------
//  命令为什么用"量化整数"而不是 float
//  ---------------------------------------------------------------------------
//  浮点数在不同平台/AOT 下可能有一致性问题，而且**序列化后不是精确相等的**。
//  所以移动轴量化成整数（-1000..1000，即千分之一精度）——
//  这也是真实联机游戏的常见做法（摇杆本来是模拟量，量化到 8~16 位足够）。
//
//  ⚠️ M4（帧同步）若需要更强的一致性，会换成自研定点数 `Fix64`（FW-M16）。
//     但"量化整数"这条思路不会变。
// ============================================================================

using System;

namespace NBC.Framework.Input
{
    /// <summary>
    /// 逻辑动作标识（"前进 / 技能1 / 交互"…），**与具体按键无关**。
    /// <para>
    /// 用 A3 的 `EventId` 同款写法：私有构造 + `Declare` 声明，拼错符号就编译不过。
    /// `Index` 是**稠密索引**（0..31），因为 <see cref="InputCommand.ActionBits"/> 用位掩码表示"本帧按下了哪些动作"。
    /// </para>
    /// </summary>
    public readonly struct InputActionId : IEquatable<InputActionId>
    {
        /// <summary>位掩码能表示的最大动作数。</summary>
        public const int MaxActionCount = 32;

        /// <summary>已声明的索引，用来抓"两个动作抢同一个索引"这种错。</summary>
        private static readonly System.Collections.Generic.List<int> s_declared = new System.Collections.Generic.List<int>();

        /// <summary>已声明的名字（调试用）。</summary>
        private static readonly System.Collections.Generic.List<string> s_names = new System.Collections.Generic.List<string>();

        private readonly int m_index;

        private InputActionId(int index)
        {
            m_index = index;
        }

        /// <summary>稠密索引（0..31）。`default(InputActionId)` 时是 0，**所以不要用 default 判断"无效"**。</summary>
        public int Index
        {
            get { return m_index; }
        }

        /// <summary>名字（调试用）。</summary>
        public string Name
        {
            get
            {
                return m_index >= 0 && m_index < s_names.Count ? s_names[m_index] : "Action#" + m_index;
            }
        }

        /// <summary>
        /// 声明一个逻辑动作。**每个索引只能声明一次** —— 抢索引会在声明处直接报错，
        /// 而不是等到运行期发现"按技能1 触发了技能2"。
        /// </summary>
        /// <param name="index">稠密索引（0..31）。</param>
        /// <param name="name">名字，仅用于日志与调试面板。</param>
        /// <returns>动作标识。</returns>
        public static InputActionId Declare(int index, string name)
        {
            if (index < 0 || index >= MaxActionCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index,
                    "[InputActionId] 索引必须在 0.." + (MaxActionCount - 1) + " 之间（命令用 32 位掩码表示动作）。");
            }

            if (s_declared.Contains(index))
            {
                throw new InvalidOperationException(
                    "[InputActionId] 索引 " + index + " 已经被声明过了（本次声明：" + name +
                    "）。两个动作不能共用一个索引，否则位掩码无法区分它们。");
            }

            while (s_names.Count <= index)
            {
                s_names.Add("Action#" + s_names.Count);
            }

            s_names[index] = name;
            s_declared.Add(index);
            return new InputActionId(index);
        }

        /// <summary>按索引比较。</summary>
        public bool Equals(InputActionId other)
        {
            return m_index == other.m_index;
        }

        /// <summary>按索引比较。</summary>
        public override bool Equals(object obj)
        {
            return obj is InputActionId && Equals((InputActionId)obj);
        }

        /// <summary>索引的哈希值。</summary>
        public override int GetHashCode()
        {
            return m_index;
        }

        /// <summary>调试文本。</summary>
        public override string ToString()
        {
            return Name + "(" + m_index + ")";
        }

        /// <summary>相等比较。</summary>
        public static bool operator ==(InputActionId left, InputActionId right)
        {
            return left.Equals(right);
        }

        /// <summary>不等比较。</summary>
        public static bool operator !=(InputActionId left, InputActionId right)
        {
            return !left.Equals(right);
        }

        /// <summary>测试用：清空声明记录。**只应在测试里调**。</summary>
        internal static void ResetDeclarationsForTests()
        {
            s_declared.Clear();
            s_names.Clear();
        }
    }

    /// <summary>
    /// 一帧的输入命令。**这是要发给服务端的东西**，所以要能精确比较、能序列化。
    /// </summary>
    public readonly struct InputCommand : IEquatable<InputCommand>
    {
        /// <summary>移动轴量化精度（-MoveScale..+MoveScale 表示 -1..+1）。</summary>
        public const int MoveScale = 1000;

        /// <summary>帧号（网络层的逻辑帧）。</summary>
        public readonly int Tick;

        /// <summary>左右轴（-1000..1000）。</summary>
        public readonly int MoveX;

        /// <summary>前后轴（-1000..1000）。</summary>
        public readonly int MoveY;

        /// <summary>**本帧新按下**的动作位掩码（第 i 位 = <see cref="InputActionId.Index"/> 为 i 的动作）。</summary>
        public readonly uint ActionBits;

        /// <summary>
        /// **本帧新松开**的动作位掩码。
        /// <para>
        /// ⚠️ 为什么必须有它：只发"按下"边沿是一种**常见的偷懒**，后果是逻辑层
        /// **无法知道玩家什么时候松手** —— 表现就是"松手了还在开火"。
        /// 有了按下 + 抬起两条边沿，逻辑层才能**精确重建**"持续按住"的状态
        /// （`held = held | pressed; held = held &amp; ~released`）。
        /// </para>
        /// </summary>
        public readonly uint ActionReleaseBits;

        /// <summary>构造命令。</summary>
        public InputCommand(int tick, int moveX, int moveY, uint actionBits, uint actionReleaseBits)
        {
            Tick = tick;
            MoveX = moveX;
            MoveY = moveY;
            ActionBits = actionBits;
            ActionReleaseBits = actionReleaseBits;
        }

        /// <summary>本帧是否按下了某个动作。</summary>
        /// <param name="action">动作标识。</param>
        public bool HasAction(InputActionId action)
        {
            return (ActionBits & (1u << action.Index)) != 0;
        }

        /// <summary>本帧是否松开了某个动作。</summary>
        /// <param name="action">动作标识。</param>
        public bool HasActionReleased(InputActionId action)
        {
            return (ActionReleaseBits & (1u << action.Index)) != 0;
        }

        /// <summary>本帧是否有移动输入。</summary>
        public bool HasMove
        {
            get { return MoveX != 0 || MoveY != 0; }
        }

        /// <summary>本帧是否什么都没做。</summary>
        public bool IsEmpty
        {
            get { return MoveX == 0 && MoveY == 0 && ActionBits == 0 && ActionReleaseBits == 0; }
        }

        /// <summary>逐字段比较。</summary>
        public bool Equals(InputCommand other)
        {
            return Tick == other.Tick && MoveX == other.MoveX && MoveY == other.MoveY
                   && ActionBits == other.ActionBits && ActionReleaseBits == other.ActionReleaseBits;
        }

        /// <summary>逐字段比较。</summary>
        public override bool Equals(object obj)
        {
            return obj is InputCommand && Equals((InputCommand)obj);
        }

        /// <summary>组合哈希。</summary>
        public override int GetHashCode()
        {
            int h = Tick;
            h = (h * 397) ^ MoveX;
            h = (h * 397) ^ MoveY;
            h = (h * 397) ^ (int)ActionBits;
            h = (h * 397) ^ (int)ActionReleaseBits;
            return h;
        }

        /// <summary>调试文本。</summary>
        public override string ToString()
        {
            return "InputCommand(tick=" + Tick + " move=(" + MoveX + "," + MoveY + ") down=0x" +
                   ActionBits.ToString("X") + " up=0x" + ActionReleaseBits.ToString("X") + ")";
        }

        /// <summary>相等比较。</summary>
        public static bool operator ==(InputCommand left, InputCommand right)
        {
            return left.Equals(right);
        }

        /// <summary>不等比较。</summary>
        public static bool operator !=(InputCommand left, InputCommand right)
        {
            return !left.Equals(right);
        }
    }
}
