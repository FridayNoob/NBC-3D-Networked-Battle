// ============================================================================
//  PresentationSource —— 「**喂给表现层的那份数据由谁决定**」（M4-S4 S4-e 收尾 + 验收修正）
//  项目：3D联网战斗Demo   对应：`Docs\27` §三十五 的 35.5 / 35.6 / **35.8**
//
//  ---------------------------------------------------------------------------
//  一、这个文件回答的问题（只有一句）
//  ---------------------------------------------------------------------------
//  **客户端喂给表现层的那一份世界，是「服务端快照」还是「锁步世界」？**
//
//  答案由那个开关决定（`UseLockstep`）：**关 = 服务端快照；开 = 锁步世界经适配器转出来的那份**。
//  ⚠️ 注意这道题的**主语**：分流的对象是「**客户端喂给表现层的那份数据**」，
//     **不是**「服务端跑哪条链」。服务端**两条链并存**（状态同步链一行未改 + 锁步链照常广播）——
//     开关只换客户端**读哪一份**，不停任何一条链。界面上也必须这么写（§35.6）。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 为什么要把「选源」做成**纯决策**（而不是写在窗口里的一句 if）
//  ---------------------------------------------------------------------------
//  「锁步模式下但还没开局」这一格是最容易写错、也最难看见的一格：
//  照直写 `if (lockstepMode) 用锁步世界` ⇒ 未开局时 `LockstepClient.World == null`
//  ⇒ 适配器产出一份**空快照** ⇒ 喂进 `SnapshotView` ⇒ **把已有的服务端快照整份盖掉**
//  ⇒ 表现成「**场景里东西全没了**」，而且**不报错、计数也不涨**（极难查的假故障）。
//
//  所以这条决策被抽成 `ViewSourceDecision`：**纯函数、无状态、无分配**，
//  探针可以**直接驱动**它（阳性对照 = 世界不可用时回退；变异 = 去掉回退 ⇒ 当场红）。
//
//  ---------------------------------------------------------------------------
//  三、⚠️ 帧号单调：为什么表现层要**自己发号**（而不是把源帧号直接当 `ServerTick`）
//  ---------------------------------------------------------------------------
//  `SnapshotView.Apply` 有一条硬闸：`if (snapshot.ServerTick <= ServerTick) { StaleIgnored++; return; }`
//  （见那个类的文件头第二节）。而**两种数据源的帧号是两套坐标系**：
//  快照链是服务端 tick（状态同步链每 tick 一张），锁步链是锁步帧号 —— 两个数**没有可比性**。
//  把源帧号直接当 `ServerTick` 喂进去 ⇒ **一切换源就可能倒退** ⇒ 被那条闸**静静丢掉**
//  ⇒ 画面停住/空白，而 `Applied` 不涨、日志也不报。
//
//  ⇒ 本类给表现层发一个**自己的单调帧号**（`PresentationTick`，每次真的喂一次 +1），
//    源帧号另存一份（`LastSourceFrame`）只用来**判断有没有新数据**。
//    ⭐ 探针有一条专门的检查盯这件事（切回去之后 `StaleIgnored` 必须仍是 0）。
//
//  ---------------------------------------------------------------------------
//  四、⚠️⚠️ 「沉默冻结」：`Started` 之后帧不再前进，**也是一类故障**（本轮修正）
//  ---------------------------------------------------------------------------
//  上一版只在 `!Started` 时回退；`Started == true` 就认定"锁步活着"。
//  可是 `LockstepClient.ApplyStart` **拒绝第二局开局**（重复开局 ⇒ `m_rejectedFrames++` + `return false`），
//  而 `Started` 之后再没人把它置回 false：
//  ⇒ **一局结束 / 服务端停播之后，`LastSteppedFrame` 永远不再变**，`Started` 也永远是 true
//  ⇒ 画面**永远停在上一帧**，同时 `回退=0、StaleIgnored=0、Applied 不动`
//  ⇒ 正是本片设计要消灭的那类**静默故障**（不报错、看着像"这局就这样"）。
//
//  ⇒ 本类加一条**停滞**判据：`Started` 但 `LastSteppedFrame` 连续
//    `StallRefreshThreshold`（= 30）次 `Refresh` **没有变** ⇒ **回退到快照 + 计数**，
//    界面显示「⚠️ 锁步停滞（已回退）」。
//  ⚠️ 判据用**刷新计数**、**不用墙上时钟**：刷新次数是可直驱、可测的量
//    （探针连调 N+ 次 `Refresh` 就能把它变成红的），墙上时钟在探针里不可控。
//
//  ---------------------------------------------------------------------------
//  四·二、⚠️ 回锁步要**滞回**（M 次真前进）—— 否则 30/1/30 抖动会稳定地闪
//  ---------------------------------------------------------------------------
//  上一版"帧一前进就立刻回锁步"。当**刷新率远高于步进率**时（编辑器 60+ fps、锁步 30Hz
//  甚至更低），形状会稳定成：`30 次刷新不动 ⇒ 回退` → `1 帧前进 ⇒ 恢复` → `30 次不动 ⇒ 回退`…
//  ⇒ `FallbackEvents` 每周期 +1（≈1800/分钟），而且 `View` 在两个不同世界间来回翻 ⇒ **画面闪烁**。
//
//  ⇒ 现在**回锁步的门槛是「连续 M 次真前进」**（`StallRecoverThreshold` = 4，见那个常量的注释），
//    期间**仍然回退**。抖动形状下永远攒不满 M 次 ⇒ 回退事件只 +1、`View` 一次都不翻。
//  ⚠️ 这里数的是**帧号的真实前进次数**，不是"刷新次数"：刷新率高于步进率时，
//    一次刷新本来就看不到新帧 —— 按刷新数会让正常（30Hz 步进 / 60Hz 刷新）也永远回不去。
//  ⚠️ 倒退（`frame < 已见过的最高帧`）**不**把"已见过的最高帧"拉回去：否则 5→6→5→6
//    会被反复算成"前进" ⇒ 滞回形同虚设（探针 §S4e6 的抖动检查就是照这个形状写的）。
//
//  ---------------------------------------------------------------------------
//  五、⚠️ 回退要记**两个数**（上一版混成一个 ⇒ 那个数不能当判据）
//  ---------------------------------------------------------------------------
//  上一版每调一次 `Refresh` 就 `m_fallbacks++`。`Refresh` 是**每编辑器帧**调一次的
//  ⇒ 一分钟就是**上万**，于是「回退 1 **次**」和「回退 1 **帧**」分不清 ⇒ 判据失效。
//  ⇒ 现在拆成两个计数，**分别显示**（名字要一眼能分辨）：
//    · `FallbackEvents`（回退**事件**数）：状态**跃迁**进回退时才 +1 —— 一段连续回退只算 1 次；
//    · `FallbackFrames`（回退**帧**数）：回退期间每帧 +1（≈编辑器帧率，只用来量"回退持续了多久"）。
//  判据看**事件数**；帧数只解释"持续了多久"。
//
//  ---------------------------------------------------------------------------
//  六、⚠️ 「世界可用」不是「非 null」（上一版只堵了一半）
//  ---------------------------------------------------------------------------
//  上一版只判 `lockstepWorld == null`。可 `world != null 但 0 实体`（开局名单为空、世界被清空、
//  或喂错了世界）照样会被适配器转成一份**空 `WorldSnapshot`** ⇒ 喂进 `SnapshotView` ⇒ **画面被清空**。
//  那一版之所以没当场露出来，靠的是 `m_lastLockstepFrame` / `frame <= m_lastLockstepFrame`
//  这两个**巧合**恰好挡着 —— **巧合不是判据**。
//  ⇒ 现在判据写死在 `ViewSourceDecision.IsWorldUsable`：
//    **世界可用 = 非 null 且 实体数 > 0 且 已开局**；不满足**一律回退**。
//
//  ---------------------------------------------------------------------------
//  七、硬约束（与 `LockstepSnapshotAdapter` 一致，这里再守一遍）
//  ---------------------------------------------------------------------------
//  ① **只读**：不写回 `WorldState`、不调 `Step`、不改 `NetSession.World`（只读它的公开面）；
//     探针有一条 `WorldStateHash.Compute(world)` 前后相等的检查把它钉死（§S4e5）。
//  ② **每帧不分配**：`EntitySnapshot` 全部复用池子（两条路径都复用，不只适配器那条）；
//     锁步那条连"排好序的实体表"也复用（`WorldState.CopySortedTo`，**不 new 数组**）——
//     ⚠️ **别**为这条去改 `SnapshotSorted()`：它被 `WorldStep` / `WorldStateHash` / `MatchRules` 用着，
//     改它就是动已验过的确定性内核。**只新增重载，不动老方法。**
//  ③ **`owner_player_id` 要标对**：锁步那条由适配器写，快照那条**原样照抄服务端**；
//  ④ 纯 C#、不碰 UnityEngine ⇒ 能进 `_net-probe`（它通配编 `Game\Net\**`）⇒ 能被直驱、能变异。
//
//  ---------------------------------------------------------------------------
//  八、⚠️ **换局**与**每局计数**（本轮修正，与 `NetSession` 那半条一起看）
//  ---------------------------------------------------------------------------
//  · 换局的**第一信号是局号**（`NetSession.MatchSerial`，每收到一条 `lockstep_start` +1）：
//    `Refresh(..., lockstepMatchSerial)` 变了 ⇒ 换适配器、`m_lastLockstepFrame` 归 −1。
//  · 兜底两条：**世界对象变了**（`NetSession` 每局重建运行器 ⇒ 世界对象必然换）、
//    **帧号严格倒退**（`frame < m_lastLockstepFrame`，同对象时才有可能）。
//  ⚠️ 为什么不再用 `frame == m_lastLockstepFrame` 当"换局"：上一版用严格小于，
//    漏掉"上一局末帧恰好是 0、新一局从 0 起"这一格（新一局第 0 帧被当重复丢掉）。
//    现在**那一格只可能是"同一局的重复帧"**（换局必然带新局号/新世界对象）⇒ 用 `<=` 挡掉是对的。
//  · **"不重复喂"靠什么**：同一局内 `frame <= m_lastLockstepFrame` 直接返回 false（不 `Apply`、
//    不涨 `Applied`），外加适配器自己那道单调闸（`NonMonotonicBlocked` 应恒为 0）。
//  · **计数语义**（必须与文档 §35.9 一致）：
//    - 每局清零的是**运行器**那些数（`DesyncCount` / `MatchMismatchCount` / `Outcome` /
//      `RejectedFrames` / `StepsExecuted` / `LastSteppedFrame`）—— 因为新局换新运行器；
//    - 本类（表现层）的 `FallbackEvents` / `FallbackFrames` / `FramesFromSnapshot` /
//      `FramesFromLockstep` / `PresentationTick` / `SourceSwitches` 是**跨局累计**的：
//      它们量的是一段"客户端会话"里的表现层行为，不属于任何一局。
// ============================================================================

using System.Collections.Generic;
using NBC.Protocol;
using NBC.Shared.Sim;

namespace NBC.Game.Net
{
    /// <summary>表现层这一帧的数据**从哪来**（就两种，别再加第三种含糊的）。</summary>
    public enum EViewSource
    {
        /// <summary>服务端快照（状态同步链；**回退**也落在这一支）。</summary>
        Snapshot = 0,

        /// <summary>锁步世界（帧同步链，经 `LockstepSnapshotAdapter` 转成同一形状）。</summary>
        Lockstep = 1,
    }

    /// <summary>
    /// **为什么这一帧没用锁步**（`None` = 真的用了锁步，没有回退）。
    /// <para>⚠️ 三种原因是三个**不同**的故障面，混成一个「回退中」就分别不出该去查哪一段代码。</para>
    /// </summary>
    public enum EFallbackReason
    {
        /// <summary>没有回退：这一帧用的就是锁步世界。</summary>
        None = 0,

        /// <summary>锁步还没开局（进房到开局之间，**这是正常的**）。</summary>
        NotStarted = 1,

        /// <summary>世界不可用：null **或** 0 实体（见文件头第六节）。</summary>
        WorldUnusable = 2,

        /// <summary>锁步停滞：`Started` 但帧号连续 N 次 `Refresh` 没前进（见文件头第四节）。</summary>
        Stalled = 3,
    }

    /// <summary>
    /// 「锁步模式 + 世界可用 + 帧还在前进 ⇒ 这一帧用哪个源」的**纯决策**（无状态、无分配、可直驱）。
    /// <para>⚠️ 这个类的唯一职责就是那条**回退**规则；别把别的东西塞进来（它要能被探针一眼看懂）。</para>
    /// </summary>
    public static class ViewSourceDecision
    {
        /// <summary>
        /// **世界可用吗**。这是回退判据的**全部**内容（见文件头第六节）：
        /// **非 null** 且 **实体数 > 0** 且 **已开局**。
        /// <para>⚠️ 只判 `!= null` 是不够的：0 实体的世界会被转成一份空快照 ⇒ 把画面清空。</para>
        /// </summary>
        /// <param name="lockstepWorld">锁步世界（`LockstepClient.World`，可为 null）。</param>
        /// <param name="lockstepStarted">锁步真开局了吗（`LockstepClient.Started`）。</param>
        /// <returns>可用返回 true。</returns>
        public static bool IsWorldUsable(WorldState lockstepWorld, bool lockstepStarted)
        {
            return lockstepStarted && lockstepWorld != null && lockstepWorld.Count > 0;
        }

        /// <summary>
        /// 选源（**唯一**的那条规则，别在别处再写一份）。
        /// <para>⭐ 判据：`lockstepMode && lockstepStarted && worldUsable` 才用锁步；
        /// **其余一律回退到快照** —— 包括「开着但还没开局」「世界不可用」。</para>
        /// <para>⚠️ `worldUsable` 一般传 `IsWorldUsable(...)` 的结果（它已经含"已开局"）。</para>
        /// </summary>
        /// <param name="lockstepMode">开关：true = 帧同步（锁步）模式。</param>
        /// <param name="lockstepStarted">锁步真开局了吗（`LockstepClient.Started`）。</param>
        /// <param name="worldUsable">世界可用吗（`IsWorldUsable`）。</param>
        /// <returns>这一帧该用哪个源。</returns>
        public static EViewSource ChooseSource(bool lockstepMode, bool lockstepStarted, bool worldUsable)
        {
            return lockstepMode && lockstepStarted && worldUsable ? EViewSource.Lockstep : EViewSource.Snapshot;
        }

        /// <summary>一句人话（界面/日志直接用）。</summary>
        /// <param name="source">源。</param>
        /// <returns>例：`锁步`。</returns>
        public static string Describe(EViewSource source)
        {
            return source == EViewSource.Lockstep ? "锁步" : "快照";
        }

        /// <summary>一句人话：**为什么**回退了（界面直接用）。</summary>
        /// <param name="reason">原因。</param>
        /// <returns>例：`锁步停滞（帧不再前进）`。</returns>
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

    /// <summary>
    /// 表现层的**唯一**数据入口：按开关把「快照」或「锁步世界」喂进**同一个** `SnapshotView`。
    /// <para>表现层（窗口 / 场景里的视图接管）**只读 `View`**，永远不认识两条链的差别。</para>
    /// </summary>
    public sealed class PresentationFeed
    {
        /// <summary>
        /// 停滞判据的阈值：**连续多少次 `Refresh` 帧号没变**就判停滞、回退到快照。
        /// <para>30 次 ≈ 编辑器帧率下**半秒到一秒**；刻意做成常量 ⇒ 探针能引用它（连调 N+ 次）。</para>
        /// <para>⚠️ 用**刷新计数**而不是墙上时钟：探针里没有可控时钟，计数才是可测的判据。</para>
        /// </summary>
        public const int StallRefreshThreshold = 30;

        /// <summary>
        /// **回锁步的门槛**：判停之后，要观察到**连续多少次「帧号真实前进」**才切回锁步（M = 4，取 3~5 之间）。
        /// <para>⚠️ 为什么必须有它（**滞回**）：上一版"帧一前进就回锁步"，当刷新率远高于步进率时
        /// 会稳定成 `30 次不动 ⇒ 回退 → 1 帧前进 ⇒ 恢复 → 30 次不动 ⇒ 回退`…
        /// ⇒ `FallbackEvents` 每个周期 +1（≈1800/分钟），`View` 在两个世界间来回翻 ⇒ **画面闪烁**。</para>
        /// <para>⚠️ 数的是**帧号真实前进的次数**，不是刷新次数：刷新率高于步进率时，一次刷新本来
        /// 就看不到新帧 —— 按刷新数会让正常的 30Hz 步进 / 60Hz 刷新也永远回不到锁步。</para>
        /// <para>期间**仍然回退**（宁可让画面稳在快照上，也不在两个世界之间闪）。</para>
        /// </summary>
        public const int StallRecoverThreshold = 4;

        /// <summary>表现层读的那一份（⚠️ 唯一读取点 —— 别在表现层再开第二份）。</summary>
        private readonly SnapshotView m_view = new SnapshotView();

        /// <summary>复用的目标快照（两条路径都往它里面填，再 `Apply` 给 `m_view`）。</summary>
        private readonly WorldSnapshot m_buffer = new WorldSnapshot();

        /// <summary>复用的 `EntitySnapshot` 池（快照那条路径用；锁步那条由适配器自己管池子）。</summary>
        private readonly List<EntitySnapshot> m_pool = new List<EntitySnapshot>();

        /// <summary>锁步世界 → `WorldSnapshot`（已验过的那个适配器，**不另起一套转换**）。</summary>
        private LockstepSnapshotAdapter m_adapter = new LockstepSnapshotAdapter();

        /// <summary>适配器当前绑在哪个世界上（换了一局就换适配器 —— 见 `FeedFromLockstep`）。</summary>
        private WorldState m_adapterWorld;

        /// <summary>表现层自己的帧号（**单调**；见文件头第三节）。</summary>
        private int m_presentationTick;

        /// <summary>上次采纳的**快照源帧号**（用来判断「有没有新快照」）。</summary>
        private int m_lastSnapshotFrame = -1;

        /// <summary>上次采纳的**锁步源帧号**（同上）。</summary>
        private int m_lastLockstepFrame = -1;

        /// <summary>适配器当前绑在**第几局**上（`-1` = 调用方还没给局号）。</summary>
        private int m_adapterMatchSerial = -1;

        /// <summary>开关（true = 帧同步）。</summary>
        private bool m_useLockstep;

        /// <summary>上一次真正喂出去的源。</summary>
        private EViewSource m_lastSource = EViewSource.Snapshot;

        /// <summary>上一次的决策（可能因回退而与开关不一致 —— 界面要能看见这个差）。</summary>
        private EViewSource m_lastChosen = EViewSource.Snapshot;

        /// <summary>上一次采纳的**源**帧号（快照 = 服务端 tick；锁步 = 锁步帧号）。</summary>
        private int m_lastSourceFrame = -1;

        /// <summary>回退**事件**数：状态**跃迁**进回退时才 +1（见文件头第五节）。</summary>
        private long m_fallbackEvents;

        /// <summary>回退**帧**数：回退期间每 `Refresh` +1（≈编辑器帧率）。</summary>
        private long m_fallbackFrames;

        /// <summary>当前是不是处在「回退中」这一段里（判"跃迁"用）。</summary>
        private bool m_inFallback;

        /// <summary>上一帧回退的**原因**（没回退时是 `None`）。</summary>
        private EFallbackReason m_fallbackReason = EFallbackReason.None;

        /// <summary>停滞判据：上一次看到的锁步帧号（`int.MinValue` = 还没在锁步态里）。</summary>
        private int m_stallTrackedFrame = int.MinValue;

        /// <summary>停滞判据：连续多少次 `Refresh` 帧号没变（上限 `StallRefreshThreshold`）。</summary>
        private int m_stallRefreshes;

        /// <summary>判停之后**观察到几次帧号真实前进**（达到 `StallRecoverThreshold` 才回锁步）。</summary>
        private int m_stallRecoverProgress;

        /// <summary>停滞判据当前跟的是**第几局**（`-1` = 调用方还没给局号）。</summary>
        private int m_stallMatchSerial = -1;

        /// <summary>已经判定为停滞了吗（连续 M 次真前进才解除）。</summary>
        private bool m_stalled;

        /// <summary>开关被拨了几次。</summary>
        private long m_modeSwitches;

        /// <summary>**实际数据源**换了几次（拨开关 ≠ 换源：未开局时回退，源没变）。</summary>
        private long m_sourceSwitches;

        /// <summary>采纳了多少次快照。</summary>
        private long m_fromSnapshot;

        /// <summary>采纳了多少次锁步世界。</summary>
        private long m_fromLockstep;

        /// <summary>表现层读的那一份世界（**只读**；表现层只认它）。</summary>
        public SnapshotView View
        {
            get { return m_view; }
        }

        /// <summary>
        /// 开关：true = 帧同步（锁步），false = 状态同步（快照）。
        /// <para>⚠️ 改它只换**客户端读哪一份**，不停任何一条链（服务端两条链并存）。</para>
        /// </summary>
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

        /// <summary>上一次真正喂出去的源（＝表现层现在看的是谁）。</summary>
        public EViewSource Source
        {
            get { return m_lastSource; }
        }

        /// <summary>上一次的决策（锁步模式下回退时，这里会是 `Snapshot`）。</summary>
        public EViewSource Chosen
        {
            get { return m_lastChosen; }
        }

        /// <summary>表现层帧号（**单调**递增；每次真的喂一次 +1）。</summary>
        public int PresentationTick
        {
            get { return m_presentationTick; }
        }

        /// <summary>上一次采纳的源帧号（快照 = 服务端 tick；锁步 = 锁步帧号）。</summary>
        public int LastSourceFrame
        {
            get { return m_lastSourceFrame; }
        }

        /// <summary>
        /// 回退**事件**数（**判据看这个**）：一段连续回退只算 1 次。
        /// <para>⚠️ 非 0 是**正常**的：进房到开局之间就在回退。</para>
        /// </summary>
        public long FallbackEvents
        {
            get { return m_fallbackEvents; }
        }

        /// <summary>
        /// 回退**帧**数（回退期间每 `Refresh` +1，≈编辑器帧率）。
        /// <para>⚠️ 它**不是**「回退了几次」—— 量的是"回退持续了多久"。判据看 `FallbackEvents`。</para>
        /// </summary>
        public long FallbackFrames
        {
            get { return m_fallbackFrames; }
        }

        /// <summary>上一帧回退的**原因**（`None` = 这一帧用的就是锁步）。</summary>
        public EFallbackReason FallbackReason
        {
            get { return m_fallbackReason; }
        }

        /// <summary>**锁步停滞**判定了吗（`Started` 但帧连续 N 次 `Refresh` 没前进）。</summary>
        public bool Stalled
        {
            get { return m_stalled; }
        }

        /// <summary>连续多少次 `Refresh` 帧号没前进（上限 `StallRefreshThreshold`；界面直接用）。</summary>
        public int StallRefreshes
        {
            get { return m_stallRefreshes; }
        }

        /// <summary>
        /// 判停之后已经观察到**几次帧号真实前进**（达到 `StallRecoverThreshold` 才回锁步）。
        /// <para>⚠️ 界面拿它显示「恢复进度 2/4」—— 让"为什么还在回退"看得见（不是卡住了）。</para>
        /// </summary>
        public int StallRecoverProgress
        {
            get { return m_stallRecoverProgress; }
        }

        /// <summary>开关被拨了几次。</summary>
        public long ModeSwitches
        {
            get { return m_modeSwitches; }
        }

        /// <summary>**实际数据源**换了几次（开关拨了不等于换了 —— 未开局时回退，源其实没变）。</summary>
        public long SourceSwitches
        {
            get { return m_sourceSwitches; }
        }

        /// <summary>采纳了多少次快照（快照链路）。</summary>
        public long FramesFromSnapshot
        {
            get { return m_fromSnapshot; }
        }

        /// <summary>采纳了多少次锁步世界（锁步链路）。</summary>
        public long FramesFromLockstep
        {
            get { return m_fromLockstep; }
        }

        /// <summary>适配器挡下的不递增帧数（正常恒为 0）。</summary>
        public long AdapterNonMonotonicBlocked
        {
            get { return m_adapter.NonMonotonicBlocked; }
        }

        /// <summary>
        /// 每帧调一次：按开关把**当前该用的那份**数据喂进 `View`。
        ///
        /// <para>⚠️ 五条行为写死（每条都对应一类**静默**错误）：</para>
        /// <list type="number">
        /// <item>**世界不可用**（未开局 / null / 0 实体）⇒ **不碰 `View`**（回退用快照）+ 计数
        /// —— 用不可用的世界盖掉快照就是「场景里东西全没了」（见文件头第六节）。</item>
        /// <item>**锁步停滞**（`Started` 但帧连续 N 次 `Refresh` 没变）⇒ 同样**回退到快照** + 计数，
        /// 界面显示「⚠️ 锁步停滞（已回退）」；**连续 M 次真前进**才回锁步（滞回，见文件头第四·二节）。
        /// 少了这条，「一局结束 / 服务端停播」之后画面就**永远停在上一帧**，且计数一动不动。</item>
        /// <item>源帧号**没变** ⇒ 什么都不做（同一个 tick 喂几十遍只会白涨 `Applied`）。</item>
        /// <item>表现层帧号**自己发** ⇒ 切源也不会倒退（见文件头第三节）。</item>
        /// <item>换了一局（**局号变了** / **换了世界对象** / **同一个对象但帧号严格倒退**）⇒ **换一个新适配器**
        /// —— 旧适配器的单调闸会记住上一局的末帧，把新一局第 0 帧挡掉（那样表现层会**一直不更新**）。
        /// 判据与"不重复喂"的分工见文件头第八节。</item>
        /// </list>
        /// </summary>
        /// <param name="ownerPlayerId">「我」的玩家编号（写进锁步那条的 `owner_player_id`）。</param>
        /// <param name="serverWorld">服务端快照的本地副本（`NetSession.World`；**只读**，可为 null）。</param>
        /// <param name="lockstepWorld">锁步世界（`LockstepClient.World`；**只读**，可为 null）。</param>
        /// <param name="lockstepFrame">锁步帧号（`LockstepClient.LastSteppedFrame`）。</param>
        /// <param name="lockstepStarted">锁步真开局了吗。</param>
        /// <param name="lockstepMatchSerial">
        /// 本会话打到**第几局**（`NetSession.MatchSerial`；`-1` = 调用方没给局号）。
        /// <para>⚠️ 这是"换了一局"的**显式信号**（见文件头第八节）—— 不给的话只能靠世界对象/帧号倒退猜，
        /// 而"上一局末帧 0、新一局从 0 起"那一格是猜不出来的。</para>
        /// </param>
        /// <returns>真的喂了一次（`View` 变了）返回 true。</returns>
        public bool Refresh(
            long ownerPlayerId,
            SnapshotView serverWorld,
            WorldState lockstepWorld,
            int lockstepFrame,
            bool lockstepStarted,
            int lockstepMatchSerial = -1)
        {
            // ① 停滞判定（**刷新计数**判据；必须在任何 return 之前跑，否则"什么都没喂"的那些帧就不计数了）
            bool stalled = TrackLockstepStall(lockstepStarted, lockstepFrame, lockstepMatchSerial);

            // ② 世界可用：非 null **且** 实体数 > 0 **且** 已开局（只判 null 是不够的）
            bool worldUsable = ViewSourceDecision.IsWorldUsable(lockstepWorld, lockstepStarted);

            EViewSource chosen = ViewSourceDecision.ChooseSource(m_useLockstep, lockstepStarted, worldUsable);

            // ③ 停滞 ⇒ 即使决策说用锁步，也要**回退**（否则就是"沉默冻结"）
            if (chosen == EViewSource.Lockstep && stalled)
            {
                chosen = EViewSource.Snapshot;
            }

            // ④ 回退的**两种计数**（见文件头第五节）
            EFallbackReason reason = EFallbackReason.None;

            if (m_useLockstep && chosen != EViewSource.Lockstep)
            {
                reason = !lockstepStarted
                    ? EFallbackReason.NotStarted
                    : (!worldUsable ? EFallbackReason.WorldUnusable : EFallbackReason.Stalled);

                m_fallbackFrames++;

                if (!m_inFallback)
                {
                    m_fallbackEvents++;     // **跃迁**才 +1（一段连续回退 = 1 次）
                    m_inFallback = true;
                }
            }
            else
            {
                m_inFallback = false;       // 用了锁步，或开关关着（关着时走快照**不叫回退**）
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

            // ⑤ 表现层自己发号（**单调**；见文件头第三节）
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

        /// <summary>清空（断开/重连时用）：`View` 清掉、源帧记忆复位（表现层帧号**不清** —— 它只增）。</summary>
        public void Clear()
        {
            m_view.Clear();
            m_lastSnapshotFrame = -1;
            m_lastLockstepFrame = -1;
            m_lastSourceFrame = -1;
            m_adapter = new LockstepSnapshotAdapter();
            m_adapterWorld = null;
            m_adapterMatchSerial = -1;

            // 停滞/回退的"当前状态"复位（累计计数保留：它们是全程计数，不是状态）
            m_stallTrackedFrame = int.MinValue;
            m_stallRefreshes = 0;
            m_stallRecoverProgress = 0;
            m_stallMatchSerial = -1;
            m_stalled = false;
            m_inFallback = false;
            m_fallbackReason = EFallbackReason.None;
        }

        /// <summary>一句人话（界面/日志直接用）。</summary>
        /// <returns>例：`来源 快照｜表现帧 12｜源帧 40｜回退 事件 1 / 帧 10｜切换 1/1｜快照 12 锁步 0`。</returns>
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

        /// <summary>
        /// 停滞判据：**连续多少次 `Refresh` 锁步帧号没变**（见文件头第四节），以及**回锁步的滞回**
        /// （判停之后要连续 `StallRecoverThreshold` 次**真前进**才解除，见文件头第四·二节）。
        ///
        /// <para>⚠️ 只在「开着 + 已开局」时才成立：其余情况停滞这个概念没有意义（那本来就是回退）。</para>
        /// <para>⚠️ **换局**（局号变了 / 帧号比见过的最高帧还低）不是"停滞解除"，是**重新开始算**：
        /// 新一局的第 0 帧本来就可能比上一局末帧小，拿它当"前进"会把 stalled 抖掉。</para>
        /// <para>⚠️ 判停之后帧号**倒退**不更新"见过的最高帧"：否则 5→6→5→6 会被反复当成"前进"，
        /// 滞回形同虚设（探针 §S4e6 的抖动检查就是这个形状）。</para>
        /// </summary>
        /// <param name="lockstepStarted">开局了吗。</param>
        /// <param name="lockstepFrame">这一帧的锁步帧号。</param>
        /// <param name="lockstepMatchSerial">第几局（`-1` = 调用方没给）。</param>
        /// <returns>判定为停滞（或还在滞回窗口里）返回 true。</returns>
        private bool TrackLockstepStall(bool lockstepStarted, int lockstepFrame, int lockstepMatchSerial)
        {
            if (!m_useLockstep || !lockstepStarted)
            {
                ResetStallTracking(int.MinValue, lockstepMatchSerial);
                return false;
            }

            // 换局 ⇒ 停滞这件事**重新算**（旧局的"停滞"不该继承给新局）
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
                // 前进（真前进一次；倒退回来过的不算第二次 —— 见方法注释）
                m_stallTrackedFrame = lockstepFrame;

                if (m_stalled)
                {
                    m_stallRecoverProgress++;

                    if (m_stallRecoverProgress < StallRecoverThreshold)
                    {
                        return true;        // **滞回**：还没攒够 M 次真前进 ⇒ 继续回退
                    }

                    ResetStallTracking(lockstepFrame, lockstepMatchSerial);   // 攒够了 ⇒ 回锁步
                    return false;
                }

                m_stallRefreshes = 0;
                return false;
            }

            if (lockstepFrame < m_stallTrackedFrame)
            {
                // 倒退：**不**把"见过的最高帧"拉回去（否则 5→6→5→6 会被反复算成前进）
                if (m_stalled)
                {
                    m_stallRecoverProgress = 0;     // 抖动的关键一格：恢复进度归零
                    return true;
                }

                m_stallTrackedFrame = int.MinValue; // 没在判停：当成"换了坐标系"，重新从这一帧开始数
                m_stallRefreshes = 0;
                return false;
            }

            // 没前进（帧号相同）⇒ 累加"多少次刷新没前进"
            if (m_stalled)
            {
                return true;        // 已经在回退里（等 M 次真前进）
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

        /// <summary>把停滞判据的所有"当前状态"复位（累计计数不动）。</summary>
        /// <param name="trackedFrame">"见过的最高帧"设成什么（`int.MinValue` = 还没在锁步态里）。</param>
        /// <param name="matchSerial">当前局号（`-1` 保持原值）。</param>
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

        /// <summary>锁步那条：适配器转 + 池子复用（**只读**世界）。</summary>
        /// <param name="ownerPlayerId">我的玩家编号。</param>
        /// <param name="world">锁步世界（可为 null ⇒ 什么都没做）。</param>
        /// <param name="frame">锁步帧号。</param>
        /// <param name="matchSerial">第几局（`-1` = 调用方没给）。</param>
        /// <returns>真的填了返回 true。</returns>
        private bool FeedFromLockstep(long ownerPlayerId, WorldState world, int frame, int matchSerial)
        {
            if (world == null)
            {
                return false;
            }

            // ④ 换了一局 ⇒ 换新适配器（旧适配器的单调闸会挡掉新一局的第 0 帧）。
            //    三条判据（优先级从高到低，见文件头第八节）：
            //      · **局号变了**（`NetSession.MatchSerial`）—— 显式信号，唯一不会猜错的那条
            //      · **换了世界对象**（`NetSession` 每局重建运行器 ⇒ 世界对象必然换）
            //      · **同一个对象、但帧号严格倒退**（`frame < 上次采纳的帧`）—— 兜底
            //    ⚠️ `frame == 上次采纳的帧` **不算换局**：换局必然带新局号/新对象
            //       ⇒ 那一格只可能是"同一局的重复帧"（文件头第八节讲了上一版这里漏过什么）
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

            // ② 源帧号没变 ⇒ 不重喂（同一个帧喂几十遍只会白涨 `Applied`）
            if (frame <= m_lastLockstepFrame)
            {
                return false;
            }

            if (!m_adapter.ApplyTo(world, frame, ownerPlayerId, m_buffer))
            {
                return false;       // 被适配器的单调闸挡下（正常走不到：上面已经挡过一遍）
            }

            m_lastLockstepFrame = frame;
            m_lastSourceFrame = frame;
            m_fromLockstep++;
            return true;
        }

        /// <summary>快照那条：从**只读的** `SnapshotView` 抄进复用缓冲（⚠️ 不碰它的任何状态）。</summary>
        /// <param name="serverWorld">服务端快照的本地副本（可为 null）。</param>
        /// <returns>真的填了返回 true。</returns>
        private bool FeedFromSnapshot(SnapshotView serverWorld)
        {
            if (serverWorld == null || !serverWorld.HasWorld)
            {
                return false;       // 快照还没来 ⇒ 什么都不做（**别清空**已有的）
            }

            int frame = (int)serverWorld.ServerTick;

            if (frame <= m_lastSnapshotFrame)
            {
                return false;
            }

            IReadOnlyList<EntitySnapshot> entities = serverWorld.Entities;

            EnsurePool(entities.Count);

            // ⚠️ **不要**写 `m_buffer.Entities.Clear()`：`RepeatedField<T>.Clear()` 对**非空**字段
            //    实测会分配 88 字节/次（探针 §S4e5 的零分配那条量出来的）⇒ 每帧一次 = 每帧一个分配。
            //    indexer 覆盖 + 尾部 `RemoveAt` 摘掉 = 0 分配（与适配器那边同一套写法）。
            int existing = m_buffer.Entities.Count;

            for (int i = 0; i < entities.Count; i++)
            {
                EntitySnapshot source = entities[i];
                EntitySnapshot target = m_pool[i];

                // 逐字段照抄（**不 new**；`owner_player_id` 原样带过来 —— 服务端已经标对了）
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
                    m_buffer.Entities[i] = target;      // 位子已经有了 ⇒ 覆盖（不分配）
                }
                else
                {
                    m_buffer.Entities.Add(target);      // 只有"实体变多了"才长
                }
            }

            while (m_buffer.Entities.Count > entities.Count)
            {
                m_buffer.Entities.RemoveAt(m_buffer.Entities.Count - 1);   // 实体变少了 ⇒ 从尾部摘掉
            }

            m_lastSnapshotFrame = frame;
            m_lastSourceFrame = frame;
            m_fromSnapshot++;
            return true;
        }

        /// <summary>把池子撑到至少 `count` 个（只增不减 ⇒ 稳态不再分配）。</summary>
        /// <param name="count">需要的条数。</param>
        private void EnsurePool(int count)
        {
            while (m_pool.Count < count)
            {
                m_pool.Add(new EntitySnapshot());
            }
        }
    }
}
