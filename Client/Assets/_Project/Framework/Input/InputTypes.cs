// 这个文件里有：
//   InputActionId（结构体）：逻辑动作标识，与具体按键无关，索引 0..31 同时是命令位掩码里的位号
//   InputCommand（结构体）：一帧的输入命令 —— 量化移动轴 + 本帧按下 / 松开的两组动作位掩码，值语义、可直接发给服务端

using System;

namespace NBC.Framework.Input
{
    /// <summary>逻辑动作标识：与具体按键无关，索引 0..31 对应命令 32 位掩码里的位号。</summary>
    // 主要功能：给「前进 / 技能1 / 交互」这类逻辑动作一个拼不错的名字，索引同时是命令位掩码里要移的那一位。
    // 主要数据成员：MaxActionCount（掩码位宽 32）、m_index（稠密索引，0..31）、s_declared（已声明的索引，查重名）、s_names（索引对应的名字，调试用）。
    // 主要外部方法：Declare（声明，唯一入口）；Index / Name（只读属性）；Equals / GetHashCode / ToString / == / !=（值语义）。
    // 谁在用它：Game 的 SkillCaster 与 Boot 的 M2DemoBehaviour 声明动作并绑技能；InputManager / InputMapping 拿它当位号与字典键，IInputSource 的查询接口拿它当参数。
    public readonly struct InputActionId : IEquatable<InputActionId>
    {
        /// <summary>位掩码能表示的动作数上限：32（uint 的 32 位各对应一个动作，索引 0..31）。</summary>
        public const int MaxActionCount = 32;

        // 已声明的索引，声明时查重 —— 两个动作抢同一索引要在声明处就报错
        private static readonly System.Collections.Generic.List<int> s_declared = new System.Collections.Generic.List<int>();

        // 索引对应的名字（与索引一对一），只给日志与调试面板用
        private static readonly System.Collections.Generic.List<string> s_names = new System.Collections.Generic.List<string>();

        // 稠密索引，0..31；default(InputActionId) 得到的也是 0，而 0 是合法动作，所以它不能当「无效」用
        private readonly int m_index;

        // 功能：只给 Declare 用的私有构造，把已经校验过的索引存下来。
        // 入参：index 稠密索引（0..31，调用方已保证合法）
        // 返回：无
        // 步骤：
        //   1) 原样写入 —— 越界与重复都在 Declare 拦过了，这里不重复校验
        // 上游：Declare
        // 下游：无
        private InputActionId(int index)
        {
            m_index = index;
        }

        /// <summary>稠密索引（0..31），同时是命令位掩码里的位号。</summary>
        // 功能：读声明时定下的索引，也就是命令里要左移的那一位。
        // 入参：无
        // 返回：索引，0..31
        // 步骤：
        //   1) 直接返回存下的值，不做校验 —— 只有 Declare 能造出实例，越界在那一步已经拦掉
        // 上游：InputManager 拼位掩码、InputCommand.HasAction / HasActionReleased 查位（都是 1u << Index）；InputMapping 当字典键；EditMode 用例
        // 下游：无
        // 注意：default(InputActionId) 的 Index 是 0，正好等于「索引 0 的那个动作」，所以不要拿 default 判断「无效」。
        public int Index
        {
            get { return m_index; }
        }

        /// <summary>索引对应的名字（调试用）；没声明过的索引返回占位名。</summary>
        // 功能：把索引翻成名字，没声明过的索引给一个可读的占位名，免得日志里只剩数字。
        // 入参：无
        // 返回：名字；索引越界或还没声明过时返回占位名（Action 拼上索引）
        // 步骤：
        //   1) 先看索引有没有落在 s_names 里：越界或没填过就用占位名
        //   2) 否则直接取表里的名字 —— 表按索引对齐，取出来就是这条动作的名字
        // 上游：ToString 拼调试文本、EditMode 用例
        // 下游：无
        public string Name
        {
            get
            {
                return m_index >= 0 && m_index < s_names.Count ? s_names[m_index] : "Action#" + m_index;
            }
        }

        /// <summary>声明一个逻辑动作并返回它的标识；索引越界或重复声明都直接抛异常。</summary>
        /// <returns>索引对应的动作标识。</returns>
        // 功能：给一个索引登记名字，并把重名与越界拦在声明处，而不是等运行期按错技能再回头查。
        // 入参：index 稠密索引（0..31）；name 名字（只用于日志与调试面板）
        // 返回：对应索引的动作标识；越界抛 ArgumentOutOfRangeException，重复声明抛 InvalidOperationException
        // 步骤：
        //   1) 先查索引范围：掩码只有 32 位，越界就放不进命令，所以先抛
        //   2) 再查这个索引声明过没有 —— 两个动作共用一位，掩码就分不清它们
        //   3) 把 s_names 补到 index 长（先填占位名），再写入本次名字，最后记进已声明表
        // 上游：Game 的 SkillCaster、Boot 的 M2DemoBehaviour 在启动时声明；EditMode 用例先清空再声明
        // 下游：无
        // 注意：重复声明报错是刻意的 —— 抢索引的后果是位掩码分不清两个动作，运行期几乎查不出来。
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

        /// <summary>按索引比较两个动作标识。</summary>
        // 功能：只比索引 —— 索引在声明时唯一分配，索引相等就是同一个动作。
        // 入参：other 另一个动作标识
        // 返回：索引相同返回 true；索引不同返回 false
        // 步骤：
        //   1) 只比 m_index，不比名字：名字只是调试文本，重复了也不影响判定
        // 上游：InputMapping 的字典查找、== / != 运算符、EditMode 用例
        // 下游：无
        public bool Equals(InputActionId other)
        {
            return m_index == other.m_index;
        }

        /// <summary>装箱比较：类型对就转给强类型 Equals。</summary>
        // 功能：转给 Equals(InputActionId) 做真正的比较，类型不对直接判不相等。
        public override bool Equals(object obj)
        {
            return obj is InputActionId && Equals((InputActionId)obj);
        }

        /// <summary>用索引当哈希值（索引唯一且稠密）。</summary>
        // 功能：直接拿索引当哈希值 —— 同索引必然同哈希，与 Equals 的判定一致。
        // 入参：无
        // 返回：索引（可能为 0，也可能为 31）
        // 步骤：
        //   1) 不混入名字：名字不参与相等判定，混进来会让相等的两个值哈希不同
        // 上游：InputMapping 的 Dictionary 查找
        // 下游：无
        public override int GetHashCode()
        {
            return m_index;
        }

        /// <summary>调试文本：名字加索引。</summary>
        // 功能：拼成「名字(索引)」，让日志与调试面板一眼看出是哪个动作。
        // 入参：无
        // 返回：调试文本；名字没声明过时用 Name 给的占位名
        // 步骤：
        //   1) 先取 Name（未声明的占位已在那边处理），再在后面跟索引
        // 上游：日志 / 调试面板
        // 下游：Name
        public override string ToString()
        {
            return Name + "(" + m_index + ")";
        }

        /// <summary>相等比较，转给 Equals。</summary>
        // 功能：转给 Equals(InputActionId)，索引相同即相等。
        public static bool operator ==(InputActionId left, InputActionId right)
        {
            return left.Equals(right);
        }

        /// <summary>不等比较，取相等结果的反。</summary>
        // 功能：转给 Equals(InputActionId)，索引不同才返回 true。
        public static bool operator !=(InputActionId left, InputActionId right)
        {
            return !left.Equals(right);
        }

        // 功能：清空声明记录，让同一个索引能重新声明（测试之间要隔离）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 两张表一起清 —— 只清一张会让名字与索引错位
        // 上游：EditMode 用例的 SetUp（经 InternalsVisibleTo 访问）
        // 下游：无
        // 注意：只应在测试里调；运行时清空会让已经发出去的动作标识再也取不到名字。
        internal static void ResetDeclarationsForTests()
        {
            s_declared.Clear();
            s_names.Clear();
        }
    }

    /// <summary>一帧的输入命令：量化移动轴 + 两组动作边沿掩码，可精确比较、可直接序列化发服务端。</summary>
    // 主要功能：把「这一帧玩家想做什么」压成一个值 —— 与设备无关、可精确比较、可发给服务端重放。
    // 主要数据成员：MoveScale（量化精度 1000，轴 -1000..1000 对应 -1..+1）、MoveX / MoveY（左右与前后轴）、ActionBits / ActionReleaseBits（32 位动作边沿掩码）、Tick（逻辑帧号）。
    // 主要外部方法：HasAction / HasActionReleased（查边沿）、HasMove / IsEmpty（查空帧）、Equals / GetHashCode / ToString / == / !=（值语义）。
    // 谁在用它：InputManager.Tick 每帧产出（Current 轮询或 CommandGenerated 事件）；Game 的 PlayerMove / RuntimeNetDriver / NetSession / BattleSession / SkillCaster 与 Boot 的 M2DemoBehaviour 消费。
    // 注意：逻辑层不许直接读 Input.GetKey —— 服务端没有键盘，重放无从谈起，所以采集（IInputSource）与命令（本类型）必须分两层。
    public readonly struct InputCommand : IEquatable<InputCommand>
    {
        /// <summary>移动轴量化精度：轴的 -1000..1000 对应 -1..+1（千分之一精度）。</summary>
        // 注意：轴用整数量化而不是 float，因为浮点在跨平台 / AOT 与序列化之后不保证逐位相等；若帧同步需要更强一致性会换自研定点数 Fix64，但量化整数这条思路不变。
        public const int MoveScale = 1000;

        /// <summary>帧号：网络层的逻辑帧号（tick，整数），服务端按它对账，重放时靠它定位到这一帧。</summary>
        public readonly int Tick;

        /// <summary>左右轴：-1000..1000 对应 -1..+1（-1000 最左、+1000 最右），PlayerMove 按它算场景 X 方向的位移。</summary>
        public readonly int MoveX;

        /// <summary>前后轴：-1000..1000 对应 -1..+1（+1000 前进、-1000 后退），PlayerMove 按它算场景 Z 方向的位移。</summary>
        public readonly int MoveY;

        /// <summary>本帧新按下的动作位掩码：第 i 位对应索引 i 的动作，1 = 本帧按下，0 = 没按（共 32 位）。</summary>
        public readonly uint ActionBits;

        /// <summary>本帧新松开的动作位掩码：第 i 位对应索引 i 的动作，1 = 本帧松开，0 = 没松（共 32 位）。</summary>
        // 注意：这一路不能省 —— 只发按下的话逻辑层不知道玩家什么时候松手，表现是「松手了还在开火」；两条边沿凑齐，逻辑层才能用「按住 = 按住 或 本帧按下，再清掉本帧松开」重建持续按住状态。
        public readonly uint ActionReleaseBits;

        /// <summary>按字段顺序装一帧命令，不做钳制。</summary>
        // 功能：把帧号、两条轴与两组边沿掩码原样装进一个值，钳制留给 InputManager。
        // 入参：tick 逻辑帧号；moveX / moveY 量化轴（-1000..1000）；actionBits / actionReleaseBits 按下与松开的位掩码
        // 返回：无
        // 步骤：
        //   1) 五个字段按参数顺序逐个赋值 —— 不校验、不钳制，这里保持纯数据
        // 上游：InputManager.Tick（每帧一条）、EditMode 用例手搓命令
        // 下游：无
        public InputCommand(int tick, int moveX, int moveY, uint actionBits, uint actionReleaseBits)
        {
            Tick = tick;
            MoveX = moveX;
            MoveY = moveY;
            ActionBits = actionBits;
            ActionReleaseBits = actionReleaseBits;
        }

        /// <summary>本帧是否「新按下」某个动作（边沿，不是按住）。</summary>
        // 功能：查本帧新按下的那一位，不是查现在按住没按住。
        // 入参：action 要查的动作（索引必须在 0..31，否则移位结果无意义）
        // 返回：本帧新按下返回 true；按住不放或压根没按返回 false
        // 步骤：
        //   1) 把 1u 左移 action.Index 位，得到这个动作的位掩码
        //   2) 与 ActionBits 相与，非 0 就是本帧新按下 —— ActionBits 只存边沿，不用再比上一帧
        // 上游：Game 的 SkillCaster.Handle（每帧按绑定表逐条查）；EditMode 用例
        // 下游：无
        // 注意：读成「按住」会让按住不放每帧触发一次（一秒 60 发），而且看起来还挺跟手。
        public bool HasAction(InputActionId action)
        {
            return (ActionBits & (1u << action.Index)) != 0;
        }

        /// <summary>本帧是否「新松开」某个动作（边沿）。</summary>
        // 功能：查本帧新松开的那一位，和 ActionBits 一起才能还原「按住」状态。
        // 入参：action 要查的动作（索引必须在 0..31）
        // 返回：本帧新松开返回 true；一直没按或还按着返回 false
        // 步骤：
        //   1) 把 1u 左移 action.Index 位，得到这个动作的位掩码
        //   2) 与 ActionReleaseBits 相与，非 0 就是本帧新松开
        // 上游：EditMode 用例；运行时暂无调用点
        // 下游：无
        // 注意：边沿只在这一帧置位，漏读一帧就丢了这次松手。
        public bool HasActionReleased(InputActionId action)
        {
            return (ActionReleaseBits & (1u << action.Index)) != 0;
        }

        /// <summary>本帧是否有移动输入（任一轴非 0）。</summary>
        // 功能：任一轴非 0 就算有移动输入，只看轴不看按键。
        // 入参：无
        // 返回：任一轴非 0 返回 true；两轴都是 0 返回 false
        // 步骤：
        //   1) 两个轴任一非 0 就返回 true —— 不看 Tick 与动作位，那是「有没有做事」的另一问
        // 上游：EditMode 用例；运行时暂无调用点
        // 下游：无
        public bool HasMove
        {
            get { return MoveX != 0 || MoveY != 0; }
        }

        /// <summary>本帧是否什么都没做（两条轴与两组动作位全为 0）。</summary>
        // 功能：判断这是不是一个空帧 —— 输入被禁用时 InputManager 产出的就是这种命令。
        // 入参：无
        // 返回：两条轴与两组掩码全为 0 返回 true；任何一项非 0 返回 false
        // 步骤：
        //   1) 四个字段全为 0 才算空 —— 只看轴会漏掉「站着按技能」这种帧
        //   2) 不看 Tick：空帧也带帧号，网络层按帧号轮询才不会错位
        // 上游：EditMode 用例（禁用期间断言命令为空）；运行时暂无调用点
        // 下游：无
        public bool IsEmpty
        {
            get { return MoveX == 0 && MoveY == 0 && ActionBits == 0 && ActionReleaseBits == 0; }
        }

        /// <summary>逐字段比较两条命令：帧号、两条轴与两组掩码全相等才算相等。</summary>
        // 功能：五个字段逐个比 —— 命令要能精确比较，服务端才能按帧号对账与回放。
        // 入参：other 另一条命令
        // 返回：五个字段全相等返回 true；任一字段不同返回 false
        // 步骤：
        //   1) 帧号先比：不同帧的命令本来就不该相等
        //   2) 再比两条轴与两组边沿掩码，任一不同立刻返回 false
        // 上游：== / != 运算符、EditMode 用例
        // 下游：无
        public bool Equals(InputCommand other)
        {
            return Tick == other.Tick && MoveX == other.MoveX && MoveY == other.MoveY
                   && ActionBits == other.ActionBits && ActionReleaseBits == other.ActionReleaseBits;
        }

        /// <summary>装箱比较：类型对就转给强类型 Equals。</summary>
        // 功能：转给 Equals(InputCommand) 做真正的比较，类型不对直接判不相等。
        public override bool Equals(object obj)
        {
            return obj is InputCommand && Equals((InputCommand)obj);
        }

        /// <summary>五个字段组合哈希，与 Equals 保持一致。</summary>
        // 功能：把五个字段按固定顺序混成一个哈希，保证相等的两条命令哈希相同。
        // 入参：无
        // 返回：哈希值（可能为负，不要当编号用）
        // 步骤：
        //   1) 从帧号起步，逐个做「乘 397 再异或」—— 397 是质数，字段顺序不同结果也不同
        //   2) 两个 uint 掩码先转 int 再混，因为哈希必须是 int
        // 上游：字典 / 集合；EditMode 用例
        // 下游：无
        public override int GetHashCode()
        {
            int h = Tick;
            h = (h * 397) ^ MoveX;
            h = (h * 397) ^ MoveY;
            h = (h * 397) ^ (int)ActionBits;
            h = (h * 397) ^ (int)ActionReleaseBits;
            return h;
        }

        /// <summary>调试文本：帧号、两条轴与两组掩码（掩码用十六进制）。</summary>
        // 功能：拼成一行能直接贴进日志的文本，掩码写十六进制方便一位一位对。
        // 入参：无
        // 返回：调试文本，形如 InputCommand(tick=… move=(x,y) down=0x… up=0x…)
        // 步骤：
        //   1) 帧号与两条轴写十进制，看到的就是量化值
        //   2) 两组掩码写十六进制，查哪个动作没按下去更快
        // 上游：日志 / 调试面板
        // 下游：无
        public override string ToString()
        {
            return "InputCommand(tick=" + Tick + " move=(" + MoveX + "," + MoveY + ") down=0x" +
                   ActionBits.ToString("X") + " up=0x" + ActionReleaseBits.ToString("X") + ")";
        }

        /// <summary>相等比较，转给 Equals。</summary>
        // 功能：转给 Equals(InputCommand)，五个字段全相等才算相等。
        public static bool operator ==(InputCommand left, InputCommand right)
        {
            return left.Equals(right);
        }

        /// <summary>不等比较，取相等结果的反。</summary>
        // 功能：转给 Equals(InputCommand)，任一字段不同才返回 true。
        public static bool operator !=(InputCommand left, InputCommand right)
        {
            return !left.Equals(right);
        }
    }
}
