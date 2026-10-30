// 这个文件里有：
//   EViewSource（枚举）：表现层这一帧的数据从哪来，只有服务端快照与锁步世界两种。
//   EFallbackReason（枚举）：这一帧没用锁步的原因，三种原因是三个不同的故障面。
//   ViewSourceDecision（静态类）：选源与「世界可用」的纯决策，无状态、无分配、可被探针直驱。
//   PresentationFeed（类）：表现层唯一的数据入口，按开关把快照或锁步世界喂进同一个 SnapshotView。
//
// 注意：分流的是「客户端喂给表现层的那份数据」，不是「服务端跑哪条链」—— 服务端两条链并存，
//   开关只换客户端读哪一份，界面文案也必须这么写（详见 Docs\27 的 35.5~35.8 节）。
// 注意：选源必须做成纯决策，不能写成窗口里的一句 if ——「锁步开着但还没开局」时锁步世界为 null，
//   照直用会拿空快照盖掉已有快照 ⇒ 场景里东西全没了，而且不报错、计数也不涨（最难查的假故障）。
// 注意：表现层帧号必须自己发（PresentationTick）—— 快照链是服务端 tick、锁步链是锁步帧号，
//   两套坐标系直接喂会被 SnapshotView.Apply 的单调闸静静丢掉 ⇒ 一切换源画面就停住或空白。
// 注意：已经开局之后帧不再前进同样是一类故障（一局结束或服务端停播后 Started 永远为 true）——
//   PresentationFeed 按「连续 N 次 Refresh 帧号没变」判停滞并回退，判据用刷新计数而不是墙上时钟。
// 注意：回锁步要滞回（连续 M 次真前进），否则刷新率高于步进率时会稳定成 30/1/30 抖动 ⇒ 画面闪。
// 注意：回退要记两个数 —— 事件数（状态跃迁）才是判据，帧数只解释回退持续了多久，混成一个就失效。
// 注意：世界可用并不是「非 null」：0 实体的世界照样会被转成空快照，把画面清空。
// 注意：PresentationFeed 只读（不写回世界、不调 Step、不改会话）、每帧不分配，且纯 C# 不碰 UnityEngine（否则进不了 _net-probe）——
//   只读与零分配这两条，探针各有检查钉着。
// 注意：锁步那条的实体表复用走 WorldState.CopySortedTo（不 new 数组）；别为省分配去改 SnapshotSorted()——
//   它被 WorldStep / WorldStateHash / MatchRules 用着，是已验过的确定性内核，只能新增重载。
// 注意：换局的第一信号是局号，兜底看世界对象换了或帧号严格倒退（见 Refresh）；计数分两套：
//   运行器那些数每局清零，PresentationFeed 的 FallbackEvents / FramesFromSnapshot / PresentationTick 跨局累计。

using System.Collections.Generic;
using NBC.Protocol;
using NBC.Shared.Sim;

namespace NBC.Game.Net
{
    // 主要功能：标示表现层这一帧的数据取自哪条链。
    // 主要数据成员：Snapshot = 0（服务端快照）、Lockstep = 1（锁步世界）。
    // 主要外部方法：无（纯枚举）。
    // 谁在用它：PresentationFeed 的 Source / Chosen 属性；Editor 的 NetDebugWindow 显示与比较。
    /// <summary>表现层这一帧的数据从哪来（只有快照与锁步两种，别再加第三种含糊的）。</summary>
    public enum EViewSource
    {
        // 服务端快照（状态同步链）；回退也落在这一支。取值 0。
        Snapshot = 0,

        // 锁步世界（帧同步链，经 LockstepSnapshotAdapter 转成同一形状）。取值 1。
        Lockstep = 1,
    }

    // 主要功能：说明这一帧为什么没用锁步（None = 真的用了锁步，没有回退）。
    // 主要数据成员：None = 0、NotStarted = 1、WorldUnusable = 2、Stalled = 3。
    // 主要外部方法：无（纯枚举）。
    // 谁在用它：PresentationFeed.FallbackReason；Editor 经 ViewSourceDecision.Describe 显示。
    /// <summary>这一帧没用锁步的原因；三种原因是三个不同的故障面，别混成一个「回退中」。</summary>
    public enum EFallbackReason
    {
        // 没有回退：这一帧用的就是锁步世界。取值 0。
        None = 0,

        // 锁步还没开局（进房到开局之间，这是正常的）。取值 1。
        NotStarted = 1,

        // 世界不可用：null 或 0 实体（见 ViewSourceDecision.IsWorldUsable）。取值 2。
        WorldUnusable = 2,

        // 锁步停滞：已开局但帧号连续 N 次 Refresh 没前进（见 TrackLockstepStall）。取值 3。
        Stalled = 3,
    }

    // 主要功能：把「这一帧用快照还是锁步世界」压成纯函数加两句人话，让回退规则只有一份。
    // 主要数据成员：无（无状态、无分配，所以探针能直接驱动它做阳性对照与变异）。
    // 主要外部方法：IsWorldUsable / ChooseSource / Describe（两个重载）。
    // 谁在用它：PresentationFeed.Refresh 每帧一次；Editor 的 NetDebugWindow 显示原因；_net-probe 直驱验收。
    /// <summary>选源那条规则的唯一实现：锁步模式且已开局且世界可用才用锁步，其余一律回退到快照。</summary>
    public static class ViewSourceDecision
    {
        // 功能：判断锁步世界这一刻能不能当数据源。
        // 入参：lockstepWorld 锁步世界（LockstepClient.World，可为 null）；lockstepStarted 是否真开局。
        // 返回：非 null 且实体数大于 0 且已开局返回 true，其余 false。
        // 步骤：
        //   1) 先看开没开局 —— 没开局时世界对象本来就没有意义
        //   2) 再看非 null 与实体数；只判 null 不够：0 实体的世界会被转成空快照把画面清空
        // 上游：PresentationFeed.Refresh
        // 下游：无
        /// <summary>世界可用 = 非 null 且实体数大于 0 且已开局；这是回退判据的全部内容。</summary>
        /// <returns>可用返回 true。</returns>
        public static bool IsWorldUsable(WorldState lockstepWorld, bool lockstepStarted)
        {
            return lockstepStarted && lockstepWorld != null && lockstepWorld.Count > 0;
        }

        // 功能：按开关、开局状态与世界可用性选出这一帧该用的源。
        // 入参：lockstepMode 开关（true = 帧同步）；lockstepStarted 是否真开局；
        //   worldUsable 世界可用吗（一般传 IsWorldUsable 的结果，它已经含「已开局」）。
        // 返回：三项全为 true 才返回锁步，其余一律快照 —— 包括「开着但还没开局」与「世界不可用」。
        // 步骤：单次合并判断 —— 不缓存、不记状态，同样的入参永远给同样的答案
        // 上游：PresentationFeed.Refresh
        // 下游：无
        /// <summary>这一帧该用哪个源：三项全真才用锁步，其余回退到快照。</summary>
        /// <returns>这一帧该用的数据源。</returns>
        public static EViewSource ChooseSource(bool lockstepMode, bool lockstepStarted, bool worldUsable)
        {
            return lockstepMode && lockstepStarted && worldUsable ? EViewSource.Lockstep : EViewSource.Snapshot;
        }

        // 功能：把数据源翻成界面与日志能直接用的一句中文。
        // 入参：source 数据源
        // 返回：锁步返回「锁步」，其余返回「快照」。
        // 步骤：按枚举值取词 —— 不查表、不分配
        // 上游：PresentationFeed.Describe、Editor 的 NetDebugWindow
        // 下游：无
        /// <summary>数据源的一句中文叫法（锁步或快照）。</summary>
        /// <returns>例：锁步。</returns>
        public static string Describe(EViewSource source)
        {
            return source == EViewSource.Lockstep ? "锁步" : "快照";
        }

        // 功能：把回退原因翻成界面与日志能直接用的一句中文。
        // 入参：reason 回退原因
        // 返回：三种原因各一句；None 以及任何未知值都返回「没有回退」。
        // 步骤：按原因分支取词，默认支兜住未知值（以后加枚举值也不会吐出空白）
        // 上游：PresentationFeed.Describe、Editor 的 NetDebugWindow
        // 下游：无
        /// <summary>回退原因的一句中文说明（界面直接显示）。</summary>
        /// <returns>例：锁步停滞（帧不再前进）。</returns>
        public static string Describe(EFallbackReason reason)
        {
            switch (reason)
            {
                case EFallbackReason.NotStarted:
                    return "还没开局";

                case EFallbackReason.WorldUnusable:
                    return "世界不可用（null 或 0 实体）";

                case EFallbackReason.Stalled:
                    return "锁步停滞（帧不再前进）";

                default:
                    return "没有回退";
            }
        }
    }

    // 主要功能：表现层唯一的数据入口 —— 按开关把服务端快照或锁步世界喂进同一个 SnapshotView。
    // 主要数据成员：m_view（唯一读取点）、m_buffer / m_pool（复用，零分配）、
    //   m_presentationTick（自己发的单调帧号）、m_stalled / m_stallRefreshes（停滞判据）。
    // 主要外部方法：Refresh（每帧一次）、Clear（断开与重连时复位）、Describe（一句人话），其余是只读计数属性。
    // 谁在用它：Game 的 RuntimeNetDriver 每帧喂；Editor 的 NetDebugWindow 显示与拨开关；_net-probe 直驱验收。
    /// <summary>表现层唯一的数据入口：按开关把快照或锁步世界转成同一份数据喂进 SnapshotView。</summary>
    public sealed class PresentationFeed
    {
        // 停滞判据阈值：连续多少次 Refresh 帧号没变就判停滞并回退到快照；单位 = 刷新次数，30 约编辑器半秒到一秒。
        // 注意：做成常量是为了探针能引用（连调 N+ 次）；判据用刷新计数而不是墙上时钟，时钟在探针里不可控。
        public const int StallRefreshThreshold = 30;

        // 回锁步的门槛：判停后要观察到连续多少次「帧号真实前进」才切回锁步；取值 3~5，现为 4。
        // 注意：这就是滞回 —— 少了它，刷新率远高于步进率时会稳定成 30 次不动就回退、1 帧前进就恢复，
        //   回退事件每周期 +1 且 View 在两个世界间来回翻 ⇒ 画面闪；数的是真前进次数，不是刷新次数。
        public const int StallRecoverThreshold = 4;

        // 表现层读的那一份；注意：这是唯一的读取点，别在表现层再开第二份。
        private readonly SnapshotView m_view = new SnapshotView();

        // 复用的目标快照；快照与锁步两条路径都往它里面填，再整体 Apply 给 m_view（每帧不分配）。
        private readonly WorldSnapshot m_buffer = new WorldSnapshot();

        // 复用的 EntitySnapshot 池，长度 = 上一次喂进来的实体数（只增不减）；锁步那条由适配器自己管池。
        private readonly List<EntitySnapshot> m_pool = new List<EntitySnapshot>();

        // 锁步世界转 WorldSnapshot 的适配器；用已验过的那个，不另起一套转换。
        private LockstepSnapshotAdapter m_adapter = new LockstepSnapshotAdapter();

        // 适配器当前绑在哪个世界对象上；换一局就换适配器（见 FeedFromLockstep）。
        private WorldState m_adapterWorld;

        // 表现层自己的帧号，每次真的喂一次 +1；单调递增，跨局不归零。
        private int m_presentationTick;

        // 上次采纳的快照源帧号（服务端 tick）；-1 = 还没采纳过。
        private int m_lastSnapshotFrame = -1;

        // 上次采纳的锁步源帧号（锁步帧号）；-1 = 还没采纳过。
        private int m_lastLockstepFrame = -1;

        // 适配器当前绑在第几局上；-1 = 调用方还没给局号。
        private int m_adapterMatchSerial = -1;

        // 开关：true = 帧同步（锁步），false = 状态同步（快照）。
        private bool m_useLockstep;

        // 上一次真正喂出去的源。
        private EViewSource m_lastSource = EViewSource.Snapshot;

        // 上一次的决策；可能因回退而与开关不一致，界面要能看见这个差。
        private EViewSource m_lastChosen = EViewSource.Snapshot;

        // 上一次采纳的源帧号；快照 = 服务端 tick，锁步 = 锁步帧号（两套坐标系，别互相比较）。
        private int m_lastSourceFrame = -1;

        // 回退事件数：状态跃迁进回退时才 +1，一段连续回退只算 1 次；判据看这个，跨局累计。
        private long m_fallbackEvents;

        // 回退帧数：回退期间每 Refresh +1（约等于编辑器帧率）；只说明回退持续了多久，不是次数。
        private long m_fallbackFrames;

        // 当前是否处在「回退中」这一段里，用来判跃迁。
        private bool m_inFallback;

        // 上一帧回退的原因；没回退时是 None。
        private EFallbackReason m_fallbackReason = EFallbackReason.None;

        // 停滞判据：上一次看到的锁步帧号；int.MinValue = 还没在锁步态里。
        private int m_stallTrackedFrame = int.MinValue;

        // 停滞判据：连续多少次 Refresh 帧号没变，上限 StallRefreshThreshold。
        private int m_stallRefreshes;

        // 判停后观察到几次帧号真实前进；达到 StallRecoverThreshold 才解除停滞。
        private int m_stallRecoverProgress;

        // 停滞判据当前跟的是第几局；-1 = 调用方还没给局号。
        private int m_stallMatchSerial = -1;

        // 是否已判定为停滞（连续 StallRecoverThreshold 次真前进才解除）。
        private bool m_stalled;

        // 开关被拨了几次（同值赋值不计数）。
        private long m_modeSwitches;

        // 实际数据源换了几次；拨开关不等于换源（未开局时回退，源其实没变）。
        private long m_sourceSwitches;

        // 采纳了多少次快照（快照链路）；跨局累计。
        private long m_fromSnapshot;

        // 采纳了多少次锁步世界（锁步链路）；跨局累计。
        private long m_fromLockstep;

        // 表现层读的那一份世界；只读，表现层只认它（也是唯一的读取点）。
        /// <summary>表现层读的那份世界快照，只读。</summary>
        public SnapshotView View
        {
            get { return m_view; }
        }

        // 开关：true = 帧同步（锁步），false = 状态同步（快照）。
        // 注意：改它只换客户端读哪一份，不停任何一条链（服务端两条链并存）；同值赋值不计数。
        /// <summary>数据源开关：true 读锁步世界，false 读服务端快照。</summary>
        public bool UseLockstep
        {
            get { return m_useLockstep; }
            set
            {
                if (m_useLockstep == value)
                {
                    return;
                }

                m_useLockstep = value;
                m_modeSwitches++;
            }
        }

        // 上一次真正喂出去的源（也就是表现层现在看的是谁）。
        /// <summary>表现层当前实际读的那个源。</summary>
        public EViewSource Source
        {
            get { return m_lastSource; }
        }

        // 上一次的决策：锁步模式下回退时，这里会是 Snapshot。
        /// <summary>上一次的选源决策；回退时与开关不一致，界面要能看见这个差。</summary>
        public EViewSource Chosen
        {
            get { return m_lastChosen; }
        }

        // 表现层帧号：每次真的喂一次 +1，单调递增，单位 = 次。
        /// <summary>表现层自己的单调帧号，每次真的喂一次 +1。</summary>
        public int PresentationTick
        {
            get { return m_presentationTick; }
        }

        // 上一次采纳的源帧号；快照 = 服务端 tick，锁步 = 锁步帧号。
        /// <summary>上一次采纳的源帧号（快照链是服务端 tick，锁步链是锁步帧号）。</summary>
        public int LastSourceFrame
        {
            get { return m_lastSourceFrame; }
        }

        // 回退事件数：一段连续回退只算 1 次，判据看这个，单位 = 次。
        // 注意：非 0 是正常的 —— 进房到开局之间本来就在回退。
        /// <summary>回退事件数（判据看这个）：一段连续回退只算 1 次。</summary>
        public long FallbackEvents
        {
            get { return m_fallbackEvents; }
        }

        // 回退帧数：回退期间每 Refresh +1，约等于编辑器帧率。
        // 注意：它不是「回退了几次」，量的是回退持续了多久；判据看 FallbackEvents。
        /// <summary>回退持续的帧数（每 Refresh +1），只说明回退持续了多久。</summary>
        public long FallbackFrames
        {
            get { return m_fallbackFrames; }
        }

        // 上一帧回退的原因；None = 这一帧用的就是锁步。
        /// <summary>这一帧没用锁步的原因；None 表示没有回退。</summary>
        public EFallbackReason FallbackReason
        {
            get { return m_fallbackReason; }
        }

        // 锁步停滞判定了吗（已开局但帧连续 N 次 Refresh 没前进）。
        /// <summary>是否已判定为锁步停滞。</summary>
        public bool Stalled
        {
            get { return m_stalled; }
        }

        // 连续多少次 Refresh 帧号没前进，上限 StallRefreshThreshold，界面直接用，单位 = 次。
        /// <summary>连续多少次刷新帧号没前进（上限 StallRefreshThreshold）。</summary>
        public int StallRefreshes
        {
            get { return m_stallRefreshes; }
        }

        // 判停后已观察到几次帧号真实前进，达到 StallRecoverThreshold 才回锁步，单位 = 次。
        // 注意：界面拿它显示「恢复进度 2/4」，让为什么还在回退看得见（不是卡住了）。
        /// <summary>判停后的恢复进度：攒够 StallRecoverThreshold 次真前进才回锁步。</summary>
        public int StallRecoverProgress
        {
            get { return m_stallRecoverProgress; }
        }

        // 开关被拨了几次（同值赋值不计数），单位 = 次。
        /// <summary>开关被拨动的次数。</summary>
        public long ModeSwitches
        {
            get { return m_modeSwitches; }
        }

        // 实际数据源换了几次；拨开关不等于换源（未开局时回退，源其实没变）。
        /// <summary>实际数据源换了几次；拨开关不等于换源。</summary>
        public long SourceSwitches
        {
            get { return m_sourceSwitches; }
        }

        // 采纳了多少次快照（快照链路），单位 = 帧。
        /// <summary>从快照链路采纳了多少帧。</summary>
        public long FramesFromSnapshot
        {
            get { return m_fromSnapshot; }
        }

        // 采纳了多少次锁步世界（锁步链路），单位 = 帧。
        /// <summary>从锁步链路采纳了多少帧。</summary>
        public long FramesFromLockstep
        {
            get { return m_fromLockstep; }
        }

        // 适配器挡下的不递增帧数，正常恒为 0，单位 = 帧；非 0 说明帧号在倒退。
        public long AdapterNonMonotonicBlocked
        {
            get { return m_adapter.NonMonotonicBlocked; }
        }

        // 功能：每帧把当前该用的那份数据喂进 View；真的喂了才发新帧号。
        // 入参：ownerPlayerId 我的玩家编号（写进锁步那条的 owner_player_id）；
        //   serverWorld 服务端快照的本地副本（只读，可为 null）；lockstepWorld 锁步世界（只读，可为 null）；
        //   lockstepFrame 锁步帧号；lockstepStarted 是否真开局；
        //   lockstepMatchSerial 本会话第几局（-1 = 调用方没给），这是换局的显式信号。
        // 返回：真的喂了一次（View 变了）返回 true；源帧号没变、世界不可用或没开局时返回 false。
        // 步骤：
        //   1) 先跑停滞判定，且必须在任何 return 之前 —— 否则「什么都没喂」的那些帧不计数，判停会漏
        //   2) 判世界可用（非 null 且实体数大于 0 且已开局）再按开关选源
        //   3) 停滞时即使决策说用锁步也强制回退 —— 否则一局结束或服务端停播之后就是「沉默冻结」
        //   4) 记回退：帧数每帧 +1，事件数只在跃迁时 +1；开关关着走快照不算回退
        //   5) 按选中的源喂；没喂成就只更新决策并返回 false
        //   6) 喂成了才 +1 表现层帧号并写回 ServerTick；源帧号另存，只用来判断有没有新数据
        // 上游：RuntimeNetDriver 与 NetDebugWindow 每帧一次；_net-probe 直驱
        // 下游：TrackLockstepStall、ViewSourceDecision、FeedFromLockstep、FeedFromSnapshot、SnapshotView.Apply
        /// <summary>每帧把当前该用的那份世界喂进表现层，只在真的采纳了新数据时更新 View 与帧号。</summary>
        /// <returns>真的喂了一次（View 变了）返回 true。</returns>
        public bool Refresh(
            long ownerPlayerId,
            SnapshotView serverWorld,
            WorldState lockstepWorld,
            int lockstepFrame,
            bool lockstepStarted,
            int lockstepMatchSerial = -1)
        {
            bool stalled = TrackLockstepStall(lockstepStarted, lockstepFrame, lockstepMatchSerial);

            bool worldUsable = ViewSourceDecision.IsWorldUsable(lockstepWorld, lockstepStarted);

            EViewSource chosen = ViewSourceDecision.ChooseSource(m_useLockstep, lockstepStarted, worldUsable);

            if (chosen == EViewSource.Lockstep && stalled)
            {
                chosen = EViewSource.Snapshot;
            }

            EFallbackReason reason = EFallbackReason.None;

            if (m_useLockstep && chosen != EViewSource.Lockstep)
            {
                reason = !lockstepStarted
                    ? EFallbackReason.NotStarted
                    : (!worldUsable ? EFallbackReason.WorldUnusable : EFallbackReason.Stalled);

                m_fallbackFrames++;

                if (!m_inFallback)
                {
                    m_fallbackEvents++;
                    m_inFallback = true;
                }
            }
            else
            {
                m_inFallback = false;
            }

            m_fallbackReason = reason;

            bool fed = chosen == EViewSource.Lockstep
                ? FeedFromLockstep(ownerPlayerId, lockstepWorld, lockstepFrame, lockstepMatchSerial)
                : FeedFromSnapshot(serverWorld);

            if (!fed)
            {
                m_lastChosen = chosen;
                return false;
            }

            m_presentationTick++;
            m_buffer.ServerTick = m_presentationTick;

            if (chosen != m_lastSource)
            {
                m_sourceSwitches++;
            }

            m_lastChosen = chosen;
            m_lastSource = chosen;

            return m_view.Apply(m_buffer);
        }

        // 功能：断开或重连时把 View 与所有源记忆、判停状态复位；累计计数与表现层帧号保留。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 清 View、三个源帧记忆，换一个新适配器并把局号归 -1 —— 旧适配器的单调闸会记住上一局的末帧
        //   2) 停滞与回退的当前状态清零；累计计数是全程计数，不动
        // 上游：Editor 的 NetDebugWindow（断开时）
        // 下游：SnapshotView.Clear
        /// <summary>断开与重连时复位：清空 View 与源帧记忆，表现层帧号与累计计数保留。</summary>
        public void Clear()
        {
            m_view.Clear();
            m_lastSnapshotFrame = -1;
            m_lastLockstepFrame = -1;
            m_lastSourceFrame = -1;
            m_adapter = new LockstepSnapshotAdapter();
            m_adapterWorld = null;
            m_adapterMatchSerial = -1;

            m_stallTrackedFrame = int.MinValue;
            m_stallRefreshes = 0;
            m_stallRecoverProgress = 0;
            m_stallMatchSerial = -1;
            m_stalled = false;
            m_inFallback = false;
            m_fallbackReason = EFallbackReason.None;
        }

        // 功能：把当前源、帧号、回退与切换计数拼成一行给人看。
        // 入参：无
        // 返回：一行中文摘要；回退原因不是 None 时在末尾补上原因。
        // 步骤：
        //   1) 依次拼来源、表现帧号、源帧号
        //   2) 回退事件数与帧数一起给 —— 两个口径不同，必须分开显示
        //   3) 有回退原因才补括号，最后补切换次数与两条链各自的采纳次数
        // 上游：Editor 的 NetDebugWindow；_net-probe 的失败信息
        // 下游：ViewSourceDecision.Describe
        /// <summary>当前源、帧号、回退与切换次数的一行人话，供界面与日志直接显示。</summary>
        /// <returns>例：来源 快照｜表现帧 12｜源帧 40｜回退 事件 1 / 帧 10｜切换 1/1｜快照 12 锁步 0。</returns>
        public string Describe()
        {
            return "来源 " + ViewSourceDecision.Describe(m_lastSource) +
                   "｜表现帧 " + m_presentationTick +
                   "｜源帧 " + m_lastSourceFrame +
                   "｜回退 事件 " + m_fallbackEvents + " / 帧 " + m_fallbackFrames +
                   (m_fallbackReason == EFallbackReason.None
                       ? string.Empty
                       : "（" + ViewSourceDecision.Describe(m_fallbackReason) + "）") +
                   "｜切换 " + m_sourceSwitches + "/" + m_modeSwitches +
                   "｜快照 " + m_fromSnapshot + " 锁步 " + m_fromLockstep;
        }

        // 功能：按「连续多少次 Refresh 锁步帧号没变」判停滞，并用「连续 M 次真前进」的滞回解除。
        // 入参：lockstepStarted 是否已开局；lockstepFrame 这一帧的锁步帧号；
        //   lockstepMatchSerial 第几局（-1 = 调用方没给）
        // 返回：判定为停滞、或还在滞回窗口里返回 true（调用方据此强制回退）；否则 false。
        // 注意：倒退时不把「见过的最高帧」拉回去 —— 否则 5→6→5→6 会被反复算成前进，滞回形同虚设。
        // 步骤：
        //   1) 没开锁步或没开局 ⇒ 复位判据并返回 false（这时的停滞没有意义，本来就是回退）
        //   2) 局号变了 ⇒ 重新开始算，不继承上一局的停滞（新一局第 0 帧可能比上一局末帧还小）
        //   3) 帧号变大 ⇒ 记一次真前进；已在判停就要攒够 M 次才解除，期间继续回退
        //   4) 帧号变小 ⇒ 只有判停中才把恢复进度清零（抖动那一格），否则当成换了坐标系重来
        //   5) 帧号相同 ⇒ 累加没前进的次数，到阈值就判停
        // 上游：PresentationFeed.Refresh（每帧、在任何 return 之前）
        // 下游：ResetStallTracking
        private bool TrackLockstepStall(bool lockstepStarted, int lockstepFrame, int lockstepMatchSerial)
        {
            if (!m_useLockstep || !lockstepStarted)
            {
                ResetStallTracking(int.MinValue, lockstepMatchSerial);
                return false;
            }

            if (lockstepMatchSerial >= 0 && m_stallMatchSerial >= 0 &&
                lockstepMatchSerial != m_stallMatchSerial)
            {
                ResetStallTracking(lockstepFrame, lockstepMatchSerial);
                return false;
            }

            if (lockstepMatchSerial >= 0)
            {
                m_stallMatchSerial = lockstepMatchSerial;
            }

            if (lockstepFrame > m_stallTrackedFrame)
            {
                m_stallTrackedFrame = lockstepFrame;

                if (m_stalled)
                {
                    m_stallRecoverProgress++;

                    if (m_stallRecoverProgress < StallRecoverThreshold)
                    {
                        return true;
                    }

                    ResetStallTracking(lockstepFrame, lockstepMatchSerial);
                    return false;
                }

                m_stallRefreshes = 0;
                return false;
            }

            if (lockstepFrame < m_stallTrackedFrame)
            {
                if (m_stalled)
                {
                    m_stallRecoverProgress = 0;
                    return true;
                }

                m_stallTrackedFrame = int.MinValue;
                m_stallRefreshes = 0;
                return false;
            }

            if (m_stalled)
            {
                return true;
            }

            if (m_stallRefreshes < StallRefreshThreshold)
            {
                m_stallRefreshes++;
            }

            if (m_stallRefreshes >= StallRefreshThreshold)
            {
                m_stalled = true;
                m_stallRecoverProgress = 0;
            }

            return m_stalled;
        }

        // 功能：把停滞判据的当前状态复位，累计计数不动。
        // 入参：trackedFrame 把「见过的最高帧」设成什么（int.MinValue = 还没在锁步态里）；
        //   matchSerial 当前局号（-1 = 保持原值）
        // 返回：无
        // 步骤：
        //   1) 复位帧号、未前进次数、恢复进度与停滞标志
        //   2) 只有给了非负局号才写回 —— -1 表示调用方没给，别覆盖已有值
        // 上游：TrackLockstepStall
        // 下游：无
        private void ResetStallTracking(int trackedFrame, int matchSerial)
        {
            m_stallTrackedFrame = trackedFrame;
            m_stallRefreshes = 0;
            m_stallRecoverProgress = 0;
            m_stalled = false;

            if (matchSerial >= 0)
            {
                m_stallMatchSerial = matchSerial;
            }
        }

        // 功能：锁步那条 —— 换局时换适配器，同一局里只采纳真正的新帧，把世界转进复用缓冲。
        // 入参：ownerPlayerId 我的玩家编号（写进 owner_player_id）；world 锁步世界（null ⇒ 没能喂）；
        //   frame 锁步帧号；matchSerial 第几局（-1 = 调用方没给）
        // 返回：真的把缓冲填好了返回 true。
        // 步骤：
        //   1) 世界为 null 直接失败 —— 不可用的世界会把画面清空
        //   2) 判换局：局号变了（显式信号）优先，其次是换了世界对象，兜底是同对象但帧号严格倒退
        //   3) 换局就换新适配器并把上次帧号归 -1 —— 旧适配器的单调闸会记住上一局末帧，挡掉新一局第 0 帧
        //   4) 同一局里 frame 不大于上次采纳的帧就返回 false（重复喂只会白涨采纳计数）
        //   5) 交给适配器写缓冲；被它的单调闸挡下也返回 false（正常走不到，上面已经挡过一遍）
        // 注意：frame 等于上次采纳的帧不算换局（换局必然带新局号或新世界对象），那一格只可能是同一局的重复帧。
        // 上游：Refresh（选源为锁步时）
        // 下游：LockstepSnapshotAdapter.ApplyTo
        private bool FeedFromLockstep(long ownerPlayerId, WorldState world, int frame, int matchSerial)
        {
            if (world == null)
            {
                return false;
            }

            bool newMatch = !ReferenceEquals(world, m_adapterWorld) || frame < m_lastLockstepFrame;

            if (matchSerial >= 0 && m_adapterMatchSerial >= 0 && matchSerial != m_adapterMatchSerial)
            {
                newMatch = true;
            }

            if (matchSerial >= 0)
            {
                m_adapterMatchSerial = matchSerial;
            }

            if (newMatch)
            {
                m_adapter = new LockstepSnapshotAdapter();
                m_adapterWorld = world;
                m_lastLockstepFrame = -1;
            }

            if (frame <= m_lastLockstepFrame)
            {
                return false;
            }

            if (!m_adapter.ApplyTo(world, frame, ownerPlayerId, m_buffer))
            {
                return false;
            }

            m_lastLockstepFrame = frame;
            m_lastSourceFrame = frame;
            m_fromLockstep++;
            return true;
        }

        // 功能：快照那条 —— 把只读的 SnapshotView 逐字段抄进复用缓冲，先覆盖旧位再从尾部摘多余。
        // 入参：serverWorld 服务端快照的本地副本（可为 null）
        // 返回：真的把缓冲填好了返回 true；快照没来或帧号没变返回 false。
        // 步骤：
        //   1) 没有世界或快照还没来 ⇒ false，绝不清空已有数据
        //   2) 帧号不大于上次采纳的就 false（同一个 tick 喂几十遍只会白涨计数）
        //   3) 池子撑到够用，然后逐字段照抄，owner_player_id 原样带过来（服务端已经标对了）
        //   4) 已有的位子用 indexer 覆盖，实体变多才 Add，变少从尾部 RemoveAt —— 全程不分配
        // 注意：不要写 m_buffer.Entities.Clear()：对非空字段实测每次分配 88 字节，每帧一次就是每帧一个分配。
        // 上游：Refresh（选源为快照时）
        // 下游：EnsurePool、SnapshotView.Entities
        private bool FeedFromSnapshot(SnapshotView serverWorld)
        {
            if (serverWorld == null || !serverWorld.HasWorld)
            {
                return false;
            }

            int frame = (int)serverWorld.ServerTick;

            if (frame <= m_lastSnapshotFrame)
            {
                return false;
            }

            IReadOnlyList<EntitySnapshot> entities = serverWorld.Entities;

            EnsurePool(entities.Count);

            int existing = m_buffer.Entities.Count;

            for (int i = 0; i < entities.Count; i++)
            {
                EntitySnapshot source = entities[i];
                EntitySnapshot target = m_pool[i];

                target.EntityId = source.EntityId;
                target.ConfigId = source.ConfigId;
                target.Kind = source.Kind;
                target.Hp = source.Hp;
                target.MaxHp = source.MaxHp;
                target.PosXMm = source.PosXMm;
                target.PosZMm = source.PosZMm;
                target.FacingDeg = source.FacingDeg;
                target.Alive = source.Alive;
                target.OwnerPlayerId = source.OwnerPlayerId;

                if (i < existing)
                {
                    m_buffer.Entities[i] = target;
                }
                else
                {
                    m_buffer.Entities.Add(target);
                }
            }

            while (m_buffer.Entities.Count > entities.Count)
            {
                m_buffer.Entities.RemoveAt(m_buffer.Entities.Count - 1);
            }

            m_lastSnapshotFrame = frame;
            m_lastSourceFrame = frame;
            m_fromSnapshot++;
            return true;
        }

        // 功能：把复用池撑到至少 count 个 EntitySnapshot。
        // 入参：count 需要的条数（当前实体数）
        // 返回：无
        // 步骤：只补不删 —— 池子只增不减，稳态下不再分配
        // 上游：FeedFromSnapshot
        // 下游：无
        private void EnsurePool(int count)
        {
            while (m_pool.Count < count)
            {
                m_pool.Add(new EntitySnapshot());
            }
        }
    }
}
