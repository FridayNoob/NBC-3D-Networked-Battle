// 这个文件里有：
//   LockstepDesync（结构体）：一条锁步分歧报告，帧号加「我的 / 服务端」两份世界哈希。
//   LockstepMatchMismatch（结构体）：一条胜负判定对不上的报告，帧号加两份判定与两队击杀数。
//   LockstepClient（类）：客户端的锁步运行器，用普通数据建局、逐帧 Step，再拿哈希与服务端对账。
//
// 注意：本文件在 Shared\Sim 下，随目录通配进 NBC.Shared；而 NBC.Shared 不引用 NBC.Protocol，这里只吃 SimEntity / FrameInput 这类普通数据，协议转换留在 NBC.Game 的 LockstepWire（它两边都引得到）—— 放一个协议生成类型进来，整个仓库就编不过。
// 注意：这个运行器是帧同步的报警器 —— 客户端自己 Step 一遍再拿哈希与服务端比，所以它必须引擎无关、能被探针直驱、能被改坏后当场变红。
// 注意：本类不重置自己 —— 重复 ApplyStart 只计数拒绝，「新的一局」= 新的 LockstepClient，由 NetSession 在收到每条 lockstep_start 时重建（服务端一局只发一次 start、TCP 保序 ⇒ start 就是局边界）。
// 注意：下面的计数全是本局的数（换实例从 0 起）；要统计整个会话分歧了几次，得在 NetSession 或窗口那一层记，不在这里累计，否则「本局是否干净」会被上一局污染。
// 注意：单位按锁步世界这一套 —— 位置是米（SimEntity.Position，Fix64 定点，协议那边的 Q32.32 raw 由 LockstepWire 装回来），时间是 tick（30Hz 逻辑帧）；本文件不做任何单位换算，毫米与米的换算也不在这里。

using System;
using System.Collections.Generic;

namespace NBC.Shared.Sim
{
    // 主要功能：一条锁步分歧报告，把帧号与「我的 / 服务端」两份世界哈希装在一起，一眼能看出差在哪一帧、差多少。
    // 主要数据成员：Frame（帧号，tick）、Mine 与 Server（世界状态哈希，64 位，无单位）、AtStart（是否开局那一次）。
    // 主要外部方法：只有构造函数与 ToString，没有行为逻辑。
    // 谁在用它：LockstepClient 造它并经 DesyncDetected 报出；NetSession 转成会话事件再记日志；服务端联调探针直接读字段断言。
    /// <summary>一条锁步分歧报告：帧号加「我的 / 服务端」两份世界哈希。</summary>
    public readonly struct LockstepDesync
    {
        /// <summary>分歧帧号，单位 tick；开局那一次就是开局帧（通常 0）。</summary>
        public readonly int Frame;

        /// <summary>我这份世界算出来的 64 位哈希，无单位。</summary>
        public readonly ulong Mine;

        /// <summary>服务端广播的该帧 64 位哈希，无单位。</summary>
        public readonly ulong Server;

        /// <summary>true = 开局那一次（开局哈希对不上）；false = 逐帧对账时对不上。</summary>
        public readonly bool AtStart;

        /// <summary>造一条报告。</summary>
        /// <param name="frame">帧号，单位 tick。</param>
        /// <param name="mine">我算出来的哈希。</param>
        /// <param name="server">服务端给的哈希。</param>
        /// <param name="atStart">true = 开局那一次。</param>
        // 功能：把帧号、两份哈希与「是否开局」装成一条不可变的报告。
        // 入参：frame 帧号（tick）；mine 我的哈希；server 服务端哈希；atStart 是否开局那一次
        // 返回：无（构造）
        // 步骤：
        //   1) 四个字段原样落值，不校验也不换算 —— 它与服务端的值必须可比
        // 上游：LockstepClient 的开局校验与逐帧对账
        // 下游：无
        public LockstepDesync(int frame, ulong mine, ulong server, bool atStart)
        {
            Frame = frame;
            Mine = mine;
            Server = server;
            AtStart = atStart;
        }

        /// <summary>把报告拼成一句人话，供日志与界面直接显示。</summary>
        /// <returns>例：第 12 帧分歧：我 123 vs 服务端 456。</returns>
        // 功能：把报告拼成一句中文，日志里直接能看出差在哪一帧、两个哈希各是多少。
        // 入参：无
        // 返回：带帧号与两个哈希的一句文案；开局那一次单独标成开局分歧（帧 N）。
        // 步骤：
        //   1) 先按 AtStart 选前缀：开局那次必须单独标出来，否则会被当成普通帧分歧
        //   2) 后面统一接「我 X vs 服务端 Y」，两个哈希都写全
        // 上游：NetSession 的分歧日志、服务端联调探针
        // 下游：无
        public override string ToString()
        {
            return (AtStart ? "**开局**分歧（帧 " + Frame + "）" : "第 " + Frame + " 帧分歧") +
                   "：我 " + Mine + " vs 服务端 " + Server;
        }
    }

    // 主要功能：一条「胜负判定对不上」的报告，把帧号、我的判定与服务端判定、两队击杀数装在一起。
    // 主要数据成员：Frame（帧号，tick）、Mine 与 Server（判定）、四个击杀数（人）。
    // 主要外部方法：只有构造函数与 ToString，没有行为逻辑。
    // 谁在用它：LockstepClient.ApplyMatchResult 造它并经 MatchMismatchDetected 报出；NetSession 转成会话事件；服务端联调探针拿它断言。
    /// <summary>一条胜负判定对不上的报告：我的判定、服务端判定与两队击杀数。</summary>
    public readonly struct LockstepMatchMismatch
    {
        /// <summary>判定发生在哪一帧，单位 tick。</summary>
        public readonly int Frame;

        /// <summary>我用自己那份世界复算出来的判定。</summary>
        public readonly EMatchOutcome Mine;

        /// <summary>服务端宣布的判定（协议数字经 MatchRules.FromNumber 转来）。</summary>
        public readonly EMatchOutcome Server;

        /// <summary>我数出来的 A 队击杀数，单位人。</summary>
        public readonly int MyKillsA;

        /// <summary>我数出来的 B 队击杀数，单位人。</summary>
        public readonly int MyKillsB;

        /// <summary>服务端给的 A 队击杀数，单位人。</summary>
        public readonly int ServerKillsA;

        /// <summary>服务端给的 B 队击杀数，单位人。</summary>
        public readonly int ServerKillsB;

        /// <summary>造一条报告。</summary>
        /// <param name="frame">帧号，单位 tick。</param>
        /// <param name="mine">我的判定。</param>
        /// <param name="server">服务端判定。</param>
        /// <param name="myKillsA">我数的 A 队击杀，单位人。</param>
        /// <param name="myKillsB">我数的 B 队击杀，单位人。</param>
        /// <param name="serverKillsA">服务端给的 A 队击杀，单位人。</param>
        /// <param name="serverKillsB">服务端给的 B 队击杀，单位人。</param>
        // 功能：把帧号、两份判定与四个击杀数装成一条不可变的报告。
        // 入参：frame 帧号（tick）；mine 我的判定；server 服务端判定；myKillsA / myKillsB 我数的两队击杀（人）；serverKillsA / serverKillsB 服务端给的两队击杀（人）
        // 返回：无（构造）
        // 步骤：
        //   1) 七项原样落值，不校验也不换算 —— 报告要能如实反映两边各自的结果
        // 上游：LockstepClient.ApplyMatchResult
        // 下游：无
        public LockstepMatchMismatch(
            int frame, EMatchOutcome mine, EMatchOutcome server,
            int myKillsA, int myKillsB, int serverKillsA, int serverKillsB)
        {
            Frame = frame;
            Mine = mine;
            Server = server;
            MyKillsA = myKillsA;
            MyKillsB = myKillsB;
            ServerKillsA = serverKillsA;
            ServerKillsB = serverKillsB;
        }

        /// <summary>把报告拼成一句人话，供日志直接显示。</summary>
        /// <returns>例：第 40 帧判定不一致：我 A 队胜 vs 服务端 B 队胜（我数 5/2，服务端给 2/5）。</returns>
        // 功能：把两份判定与四个击杀数拼成一句中文，日志里能直接看出两边差在哪。
        // 入参：无
        // 返回：带帧号、两份判定与两队击杀数的一句文案；判定文案走 MatchRules.Describe。
        // 步骤：
        //   1) 先写帧号与我的判定，再写服务端判定
        //   2) 最后把四个击杀数按「我数 A/B，服务端给 A/B」摆出来 —— 只报「不一样」等于没说
        // 上游：NetSession 的判定不一致日志、服务端联调探针
        // 下游：MatchRules.Describe
        public override string ToString()
        {
            return "第 " + Frame + " 帧判定不一致：我 " + MatchRules.Describe(Mine) +
                   " vs 服务端 " + MatchRules.Describe(Server) +
                   "（我数 " + MyKillsA + "/" + MyKillsB +
                   "，服务端给 " + ServerKillsA + "/" + ServerKillsB + "）";
        }
    }

    // 主要功能：在客户端把一局锁步跑起来 —— 按开局数据建世界、逐帧 Step、每帧拿自己的哈希与服务端广播的哈希对账，并在收到胜负结果时用自己那份世界复算。
    // 主要数据成员：m_world（我这份世界，未开局为 null）、m_lastSteppedFrame（已推进到哪一帧，tick）、m_desyncs / m_ignoredBeforeStart / m_rejectedFrames（本局三种计数，次）、m_team（队伍映射，判胜负要用）。
    // 主要外部方法：ApplyStart（开局并对开局哈希）、ApplyFrame（收一帧并逐帧对账）、SetTeams（设队伍映射）、ApplyMatchResult（复算胜负）、Describe（一句人话），外加一批只读属性与两个事件。
    // 谁在用它：NBC.Game 的 NetSession 建局 / 喂帧 / 设队伍 / 对账，NetSession 与 RuntimeNetDriver 只读它的世界与帧号喂表现层；NBC.Editor 的 NetDebugWindow 读只读状态面板；服务端联调探针直接 new 它驱动。
    // 注意：它只吃普通数据（SimEntity / FrameInput），不许出现协议生成类型 —— NBC.Shared 不引用 NBC.Protocol，引进来整个仓库编不过；协议转换在 NBC.Game 的 LockstepWire。
    // 注意：三条静默错误的挡板都在 ApplyStart 与 ApplyFrame 里 —— 起点哈希不同不许开始、未开局收帧只忽略、同一帧与倒退帧一律拒绝；挡错了不会报错，只会让帧数悄悄错开。
    /// <summary>客户端的锁步运行器：引擎无关、只吃普通数据，探针能直接驱动。</summary>
    public sealed class LockstepClient
    {
        // 我这份世界（未开局或开局哈希对不上时为 null）；位置米、时间 tick，由 ApplyStart 建、ApplyFrame 推进。
        private WorldState? m_world;

        // 开局了没有；只有 ApplyStart 里开局哈希对得上才会置 true。
        private bool m_started;

        // 已经 Step 到哪一帧，单位 tick；未开局时为 -1，开局成功时为开局帧减 1。
        private int m_lastSteppedFrame = -1;

        // 已经 Step 过几次，单位次（本局）；正常必须等于接受过的帧数，不等就说明有人重复 Step。
        private int m_stepsExecuted;

        // 分歧次数，单位次（本局）；开局哈希对不上与逐帧哈希对不上都算一次。
        private int m_desyncs;

        // 未开局时被忽略的帧数，单位帧（本局）；这个数要看得见，否则「没反应」没法解释。
        private int m_ignoredBeforeStart;

        // 被拒绝的帧数，单位帧（本局）；含重复帧、倒退帧与重复开局。
        private int m_rejectedFrames;

        // 最近一条分歧报告；分歧次数为 0 时是默认值，无意义。
        private LockstepDesync m_lastDesync;

        /// <summary>报出一条分歧；NetSession 把它转成会话级事件。</summary>
        // 注意：这是本局运行器上的事件 —— 运行器每局换一个，下游要订阅会话上的同名事件，否则第二局起就收不到。
        public event Action<LockstepDesync>? DesyncDetected;

        /// <summary>开局了没有；开局哈希对不上时为 false，本局的标志（换局是新实例）。</summary>
        public bool Started
        {
            get { return m_started; }
        }

        /// <summary>我这份世界（未开局时为 null）；位置单位米、时间 tick，读它不会推进世界。</summary>
        public WorldState? World
        {
            get { return m_world; }
        }

        /// <summary>已经 Step 到哪一帧，单位 tick；未开局为 -1，开局成功时为开局帧减 1。</summary>
        public int LastSteppedFrame
        {
            get { return m_lastSteppedFrame; }
        }

        /// <summary>已经 Step 过几次，单位次（本局）；正常必须等于接受过的帧数。</summary>
        public int StepsExecuted
        {
            get { return m_stepsExecuted; }
        }

        /// <summary>分歧次数，单位次（本局）；开局哈希与逐帧哈希对不上都算。</summary>
        public int DesyncCount
        {
            get { return m_desyncs; }
        }

        /// <summary>有没有过分歧（本局）；等价于分歧次数大于 0。</summary>
        public bool HasDesync
        {
            get { return m_desyncs > 0; }
        }

        /// <summary>最近一条分歧，带帧号与两个哈希；没分歧时是默认值，无意义。</summary>
        public LockstepDesync LastDesync
        {
            get { return m_lastDesync; }
        }

        /// <summary>未开局时被忽略的帧数，单位帧（本局）；要看得见，否则「没反应」没法解释。</summary>
        public int IgnoredBeforeStart
        {
            get { return m_ignoredBeforeStart; }
        }

        /// <summary>被拒绝的帧数，单位帧（本局）；含重复帧、倒退帧与重复开局。</summary>
        public int RejectedFrames
        {
            get { return m_rejectedFrames; }
        }

        // 队伍映射（判胜负要用），未设为 null；NetSession 在开局后按成员名单的同一个纯函数设进来。
        private EntityTeam? m_team;

        // 与服务端判定不一致的次数，单位次（本局）。
        private int m_matchMismatches;

        // 最近一次判定不一致；不一致次数为 0 时是默认值，无意义。
        private LockstepMatchMismatch m_lastMatchMismatch;

        // 落定的结果（服务端宣布且我复算一致时才非 null）；还没落定为 null。
        private EMatchOutcome? m_outcome;

        /// <summary>报出一条胜负判定不一致（照 DesyncDetected 的形状）；NetSession 把它转成会话级事件。</summary>
        // 注意：同样是本局运行器上的事件 —— 运行器每局换一个，下游要订阅会话上的同名事件。
        public event Action<LockstepMatchMismatch>? MatchMismatchDetected;

        /// <summary>判定不一致次数，单位次（本局）。</summary>
        public int MatchMismatchCount
        {
            get { return m_matchMismatches; }
        }

        /// <summary>有没有过判定不一致（本局）。</summary>
        public bool HasMatchMismatch
        {
            get { return m_matchMismatches > 0; }
        }

        /// <summary>最近一次判定不一致；没发生过时是默认值，无意义。</summary>
        public LockstepMatchMismatch LastMatchMismatch
        {
            get { return m_lastMatchMismatch; }
        }

        /// <summary>落定的结果（服务端宣布且我复算一致时才非 null）；本局的数。</summary>
        public EMatchOutcome? Outcome
        {
            get { return m_outcome; }
        }

        /// <summary>设队伍映射，判胜负要用；之后只读。</summary>
        /// <param name="team">实体编号到队号的查询（NetSession 给的 SortedTeamMap）。</param>
        // 功能：把队伍映射存进来，供 ApplyMatchResult 数击杀用。
        // 入参：team 实体编号到队号的查询（由 NetSession 按成员名单建好的 SortedTeamMap）
        // 返回：无
        // 步骤：
        //   1) 只赋值，不复制也不校验 —— 映射与入房顺序无关这件事由 SortedTeamMap 保证
        // 注意：设晚了或没设，ApplyMatchResult 只能如实返回 false（不计不一致次数、也不发事件），调用方会当成复算不一致；NetSession 因此在结果到达前先同步一次。
        // 上游：NetSession.SyncLockstepTeams
        // 下游：无
        public void SetTeams(EntityTeam team)
        {
            m_team = team;
        }

        /// <summary>收服务端宣布的胜负结果，用自己那份世界复算后与它比。</summary>
        /// <param name="frame">判定发生在哪一帧，单位 tick。</param>
        /// <param name="serverOutcomeNumber">服务端判定，协议数字 1 / 2 / 3（交给 MatchRules.FromNumber 转）。</param>
        /// <param name="killsA">服务端给的 A 队击杀数，单位人。</param>
        /// <param name="killsB">服务端给的 B 队击杀数，单位人。</param>
        /// <returns>复算与服务端一致返回 true；没开局、没有队伍映射或确实不一致都返回 false。</returns>
        // 功能：拿自己那份世界重数两队击杀、重判一次胜负，与服务端宣布的结果比对，不一致就报出来。
        // 入参：frame 帧号（tick）；serverOutcomeNumber 服务端判定的协议数字（1 / 2 / 3）；killsA、killsB 服务端给的两队击杀数（人）
        // 返回：true = 复算与服务端一致且结果已落定；false = 没开局 / 没队伍映射复算不出来，或确实不一致（后者会计数并触发事件）
        // 步骤：
        //   1) 先把协议数字转成判定，客户端不另写一份 switch
        //   2) 没开局、世界为 null、队伍映射为 null 时如实返回 false —— 不许假装一致
        //   3) 用自己那份世界数两队击杀，再按同一套规则复算判定
        //   4) 不一致就计数、记下带三样数据的报告并触发事件；一致才把结果落定到 Outcome
        // 注意：复算必须留在这个引擎无关层 —— 搬到 NetSession 就只能靠起真服务验；报告必须带「我的判定 / 服务端判定 / 两队击杀数」，只说「不一样」排查不动。
        // 上游：NetSession.HandleMatchResult（收到胜负结果消息）
        // 下游：MatchRules.FromNumber、MatchRules.CountKills、MatchRules.Decide、MatchMismatchDetected
        public bool ApplyMatchResult(int frame, int serverOutcomeNumber, int killsA, int killsB)
        {
            EMatchOutcome server = MatchRules.FromNumber(serverOutcomeNumber);

            if (!m_started || m_world == null || m_team == null)
            {
                return false;
            }

            int myKillsA;
            int myKillsB;

            MatchRules.CountKills(m_world, m_team, out myKillsA, out myKillsB);

            EMatchOutcome mine = MatchRules.Decide(myKillsA, myKillsB, m_world.Tick);

            if (mine != server)
            {
                m_matchMismatches++;
                m_lastMatchMismatch = new LockstepMatchMismatch(
                    frame, mine, server, myKillsA, myKillsB, killsA, killsB);
                MatchMismatchDetected?.Invoke(m_lastMatchMismatch);
                return false;
            }

            m_outcome = server;
            return true;
        }

        /// <summary>把本局状态拼成一句人话，供日志与调试面板直接显示。</summary>
        /// <returns>例：锁步客户端：已开局、推进 12 帧、分歧 0、开局前忽略 0、拒 1。</returns>
        // 功能：把开局标志与本局几个计数拼成一句中文，排查时一眼看完。
        // 入参：无
        // 返回：一句话；结果已落定时末尾再补一段结果文案。
        // 步骤：
        //   1) 按开局标志选「已开局 / 未开局」，再依次接推进数、分歧数、开局前忽略数、拒绝数、判定不一致数
        //   2) 结果只在落定后追加 —— 未落定时不写「结果」两个字，免得被当成平局
        // 上游：NetSession 收到胜负结果时的日志、NetDebugWindow 的锁步面板、服务端联调探针
        // 下游：MatchRules.Describe
        public string Describe()
        {
            return "锁步客户端：" + (m_started ? "已开局" : "**未开局**") +
                   "、推进 " + m_stepsExecuted + " 帧、分歧 " + m_desyncs +
                   "、开局前忽略 " + m_ignoredBeforeStart + "、拒 " + m_rejectedFrames +
                   "、判定不一致 " + m_matchMismatches +
                   (m_outcome == null ? "" : ("、结果 " + MatchRules.Describe(m_outcome.Value)));
        }

        /// <summary>按开局数据建世界：实体按编号升序登记并回填真实血量与可攻击帧，再拿开局哈希与服务端核对。</summary>
        /// <param name="frame">开局帧号，单位 tick（世界 Tick 直接取它）。</param>
        /// <param name="initialHash">服务端给的开局哈希。</param>
        /// <param name="entities">初始实体（位置米、时间 tick），顺序无所谓，内部按编号升序重排。</param>
        /// <returns>开局成功且开局哈希对得上返回 true；实体表为 null、重复开局或哈希对不上都返回 false。</returns>
        // 功能：收下开局数据建一份世界，先自算一遍哈希与服务端的开局哈希比，对得上才允许开始。
        // 入参：frame 开局帧号（tick）；initialHash 服务端给的开局哈希；entities 初始实体（普通数据，顺序无所谓，内部按编号升序重排）
        // 返回：true = 世界已建好且开局哈希一致；false = 实体表为 null / 重复开局 / 哈希对不上（后两者都计数）
        // 步骤：
        //   1) 实体表为 null 或已经开过局就计数拒绝 —— 重复开局绝不重建世界，重建会把已推进的帧全抹掉而且不报错
        //   2) 先拷一份实体再按编号升序排 —— 排序责任只留在这里（协议转换那边刻意不排），建局不依赖实体到达顺序
        //   3) 逐个登记后用 GetAt / SetAt 把真实的 MaxHp 与 AttackReadyTick 写回（登记只吃编号、位置、血量，会把 MaxHp 设成血量、可攻击帧设成 0）
        //   4) 把世界 Tick 设成开局帧，自算一遍哈希与 initialHash 比
        //   5) 对不上就计数、报一条帧号等于开局帧的分歧并保持未开局；对得上才落世界，末帧游标置为开局帧减 1
        // 注意：第 3 步的回填不能省 —— 带伤出生或开局带冷却时，少了 MaxHp 与 AttackReadyTick，两端第 0 帧哈希就不同，真正的分歧会被后面的假分歧淹没。
        // 注意：开局哈希对不上时不许开始（拿不同起点的世界逐帧跑，每帧都会报分歧）；同一局也只能开一次，本方法不重置自己。
        // 上游：NetSession.HandleLockstepStart、服务端联调探针
        // 下游：WorldState.Add / IndexOf / GetAt / SetAt / SetTick、CompareEntityById、WorldStateHash.Compute、DesyncDetected
        public bool ApplyStart(int frame, ulong initialHash, IReadOnlyList<SimEntity> entities)
        {
            if (entities == null)
            {
                m_rejectedFrames++;
                return false;
            }

            if (m_started)
            {
                m_rejectedFrames++;
                return false;
            }

            var sorted = new List<SimEntity>(entities.Count);

            for (int i = 0; i < entities.Count; i++)
            {
                sorted.Add(entities[i]);
            }

            sorted.Sort(CompareEntityById);

            var world = new WorldState();

            for (int i = 0; i < sorted.Count; i++)
            {
                world.Add(sorted[i].Id, sorted[i].Position, sorted[i].Hp);

                int index = world.IndexOf(sorted[i].Id);
                SimEntity fixedUp = world.GetAt(index);
                fixedUp.MaxHp = sorted[i].MaxHp;
                fixedUp.AttackReadyTick = sorted[i].AttackReadyTick;
                world.SetAt(index, fixedUp);
            }

            world.SetTick(frame);

            ulong mine = WorldStateHash.Compute(world);

            if (mine != initialHash)
            {
                m_desyncs++;
                m_world = null;
                m_started = false;
                m_lastDesync = new LockstepDesync(frame, mine, initialHash, true);
                DesyncDetected?.Invoke(m_lastDesync);
                return false;
            }

            m_world = world;
            m_started = true;
            m_lastSteppedFrame = frame - 1;
            return true;
        }

        /// <summary>收一帧：把广播输入转成普通输入 Step 一次，再拿自己的哈希与服务端哈希比。</summary>
        /// <param name="frame">帧号，单位 tick。</param>
        /// <param name="inputs">这一帧各玩家的输入（玩家编号、方向、目标编号），可为 null，等价于空帧；缺输入的人已由服务端填好默认值。</param>
        /// <param name="serverHash">服务端算出来的该帧哈希。</param>
        /// <returns>真的推进了这一帧返回 true；未开局被忽略、重复帧或倒退帧被拒绝都返回 false（此时世界不动）。</returns>
        // 功能：把一帧的广播输入喂给世界推进一步，随后用哈希与服务端核对，对不上就报分歧。
        // 入参：frame 帧号（tick）；inputs 这一帧各玩家的输入（普通数据，可为 null，缺输入的人已由服务端填成默认值并标了 Missing）；serverHash 服务端算出来的该帧哈希
        // 返回：true = 这一帧真的推进了；false = 未开局（忽略并计数）或帧号不前进（拒绝并计数），两种情况世界都不动
        // 步骤：
        //   1) 未开局或世界为 null 就忽略并计数 —— 不许拿空世界先跑起来，那只会算出一屏假分歧
        //   2) 帧号不大于已推进的末帧就拒绝并计数 —— 同一帧只 Step 一次，重复 Step 会让帧数与服务端悄悄错开；倒退帧同理
        //   3) 把每条输入转成推进用的形状（玩家编号显式收窄成实体编号），转完再交出去
        //   4) 推进一帧，更新末帧游标与执行计数
        //   5) 自算哈希与服务端比，不等就计数、记报告并触发事件
        // 注意：玩家编号到实体编号是有意的显式收窄（协议里玩家编号是 int64、实体编号是 int）—— 别改成隐式转换，编号一旦超出 int 范围会被静默截断成另一个实体。
        // 注意：inputs 为 null 与空表都按「这一帧没有任何输入」推进，仍然要 Step；否则帧号会卡住，后面每帧都被当成重复帧拒绝。
        // 注意：本方法不看 Missing —— 缺输入的人拿到的就是服务端填好的默认值，两边照它 Step，才算同一帧。
        // 上游：NetSession.HandleLockstepFrame、服务端联调探针
        // 下游：WorldStep.Step、WorldStateHash.Compute、DesyncDetected
        public bool ApplyFrame(int frame, IReadOnlyList<FrameInput> inputs, ulong serverHash)
        {
            if (!m_started || m_world == null)
            {
                m_ignoredBeforeStart++;
                return false;
            }

            if (frame <= m_lastSteppedFrame)
            {
                m_rejectedFrames++;
                return false;
            }

            var stepInputs = new List<SimInput>(inputs == null ? 0 : inputs.Count);

            if (inputs != null)
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    SimInput input;
                    input.EntityId = (int)inputs[i].PlayerId;      // 玩家编号显式收窄成实体编号（协议约定），别改成隐式转换
                    input.MoveDirection = inputs[i].MoveDirection;
                    input.AttackTargetId = inputs[i].TargetEntityId;
                    stepInputs.Add(input);
                }
            }

            WorldStep.Step(m_world, stepInputs, 1);

            m_lastSteppedFrame = frame;
            m_stepsExecuted++;

            ulong mine = WorldStateHash.Compute(m_world);

            if (mine != serverHash)
            {
                m_desyncs++;
                m_lastDesync = new LockstepDesync(frame, mine, serverHash, false);
                DesyncDetected?.Invoke(m_lastDesync);
            }

            return true;
        }

        // 功能：按实体编号升序比较，供建局时 List.Sort 重排用。
        // 入参：a 左实体；b 右实体
        // 返回：负数表示 a 在前、正数表示 b 在前、0 表示编号相同。
        // 步骤：
        //   1) 只比 Id —— 编号是唯一键，不用看别的字段，也不用担心两个实体比不出先后
        // 注意：写成静态方法是有意的 —— 不捕获任何状态，比较逻辑只有这一处；别改成比较位置 —— 位置相同的实体排不出先后，两端到达顺序不同时次序就会错开。
        // 上游：ApplyStart 的 List.Sort
        // 下游：无
        private static int CompareEntityById(SimEntity a, SimEntity b)
        {
            return a.Id.CompareTo(b.Id);
        }
    }
}
