// ============================================================================
//  NetDebugWindow —— 编辑器里的网络调试窗口（M3-S3）
//  项目：3D联网战斗Demo   对应：Docs\25 §八 S3 的验收（"负责人亲手连一次"）
//
//  ---------------------------------------------------------------------------
//  为什么是"窗口"而不是一条菜单项
//  ---------------------------------------------------------------------------
//  菜单点一下只能给一个**瞬间**的结论（连上了/报错了），而联调里真正要看的是
//  **持续状态**：RTT 稳不稳、心跳有没有在发、失败原因是什么、收发统计涨不涨。
//  所以这里做一个常驻窗口，把 `NetSession` 的状态摊开显示。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 时间也是"喂"进去的：为什么要把 delta 夹到 250ms
//  ---------------------------------------------------------------------------
//  `NetSession` 的时钟由调用方喂（它的文件头第二节）。编辑器不是游戏循环：
//  窗口失去焦点、编辑器卡顿、断点停住之后，`EditorApplication.update` 的间隔可能有好几秒。
//  如果照实喂进去，`NetSession` 会立刻判"心跳超时"——**假掉线**。
//  所以这里把单次 delta **夹到 250ms**：时钟宁可走慢，也不许跳。
//
//  ⚠️ 已知局限（如实记）：`OnDisable` 会断开。
//     窗口关掉、或进入播放模式触发域重载时，静态/窗口状态会丢，连接随之消失。
//     M3 的客户端会话本来就该挂在**游戏流程**里（S5 接进战斗），这里只是"能点着看"的入口。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;             // M4-S4 S4-e 收尾：`DescribeUnits`（把表现层那份世界里的编号串成一行）
using NBC.Framework;           // M4-S1：`EventCenter`（全局事件中心）
using NBC.Framework.Net;       // R004：`CountingTransport` / `NetRuntimeStats` / `NetSyncMode`（HUD 同源统计）
using NBC.Framework.Net.Adapter;
using NBC.Game.Achievement;    // M4-S2：`AchievementRuntime` / `AchievementTracking` / `AchievementEvents`
using NBC.Game.Battle;         // M4-S1：`ServerEventBridge`（服务端事件 → 事件中心）
using NBC.Game.Config;         // M4-S1c：`ConfigMgr` / `GameTables`（真任务要读配置表）
using NBC.Game.GameFlow;       // M4-S1c：`BattleSession`（条件 → 任务 → 发奖 那条链的装配）
using NBC.Game.Net;
using NBC.Game.Quest;          // M4-S1：`ConditionEventBridge`（事件中心 → 条件系统，M2 就有的那座桥）
using NBC.Game.UI;             // M4-S1c：`QuestPanelModel` / `QuestRow`（真面板的视图模型，窗口复用同一份）
using NBC.Game.View;           // M4-S4 S4-e 收尾：`SnapshotSceneBinder`（最小视图接管，3D 真的能看见）
using NBC.Protocol;           // M4-S3 收口：`ProgressSync` / `ConditionProgressEntry`（服务端权威进度）
using NBC.Shared.Battle;       // M4-S1b：`BattleRules`（射程/冷却——**两端同一个常量**，别在窗口里再写一份）
using NBC.Shared.Condition;    // M4-S1：`ConditionTracker` / `ConditionDef` / `EConditionEvent`
using NBC.Shared.Net;       // M4-S3 §二十六：NetErrors（把错误码翻译成人话）
using NBC.Shared.Sim;       // M4-S4 S4-e：`LockstepClient`（帧同步面板要读它的只读状态）
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    /// <summary>编辑器里的网络调试窗口：连接 / 断开 / 看心跳与状态。</summary>
    public sealed class NetDebugWindow : EditorWindow
    {
        /// <summary>单次推进的最大毫秒数（见文件头：防止编辑器卡顿造成"假掉线"）。</summary>
        private const int MaxDeltaMs = 250;

        /// <summary>日志区最多保留多少行。</summary>
        private const int MaxLogLines = 200;

        /// <summary>服务端地址（M3 的默认验收就是本机）。</summary>
        [SerializeField] private string m_host = "127.0.0.1";

        /// <summary>
        /// 端口。
        /// <para>⚠️ 这里是 **9000**，与服务端 `appsettings.json` 的 `Server.ListenPort` 一致
        /// （`Docs\11` 也记的 9000）。</para>
        /// <para>⚠️ **2026-09-27 修正一处"巧合"**：这个字段原来是 **7777**
        /// （= `TcpServerTransport.DefaultPort`，那是代码里的兜底值）。
        /// 在 SRV-01 之前，服务端**忽略** `appsettings.json`、用的正是那个兜底值 7777 ——
        /// 于是"客户端 7777 / 服务端 7777"**碰巧**能连上。
        /// SRV-01 之后服务端**真的读配置**了 ⇒ 它监听 **9000**，而客户端还在 7777 ⇒ **连不上**。
        /// ⇒ 对齐到配置里那个值（9000），并且**不要再让两边各写一个默认值**。</para>
        /// </summary>
        [SerializeField] private int m_port = 9000;

        /// <summary>玩家名（**游客**用；登录成功时显示服务端给的档案昵称）。</summary>
        [SerializeField] private string m_playerName = "剑士";

        /// <summary>
        /// 登录名（**留空 = 游客**，M4-S3 追加）。
        /// <para>⚠️ 字段上的值会被 Unity **序列化到窗口状态里**：填了 test01 之后
        /// 下次开窗口还是 test01 —— 想回游客就把这一格清空。</para>
        /// </summary>
        [SerializeField] private string m_account = "";

        /// <summary>
        /// 明文密码（**只在点"连接"的那一瞬间用一下**，算成摘要就丢）。
        /// <para>
        /// ⚠️ 它会被 Unity 序列化进编辑器窗口状态（`Library/` 里）——
        /// 这是**演示级的取舍**：测试账号的密码本来就写在 `Docs\08` 里，不是什么秘密。
        /// 真实项目**不该**把密码放进序列化字段，而是每次输入、只留内存。
        /// </para>
        /// </summary>
        [SerializeField] private string m_password = "";

        /// <summary>想进哪个副本（`Dungeon` 表主键；S6 才会真用它建副本）。</summary>
        [SerializeField] private int m_dungeonId = 1001;

        /// <summary>指定房号（留空 = 让服务端按副本安排一个）。</summary>
        [SerializeField] private string m_roomId = "";

        /// <summary>输入：左右轴（-1000..1000，沿用 A7 的量化约定）。</summary>
        [SerializeField] private int m_inputX;

        /// <summary>输入：前后轴（-1000..1000）。</summary>
        [SerializeField] private int m_inputY;

        /// <summary>勾上就每帧把当前输入发出去（模拟「一直按着方向」）。</summary>
        [SerializeField] private bool m_inputContinuous;

        /// <summary>当前目标（攻击发给谁；0 = 没目标）。</summary>
        [SerializeField] private int m_targetEntityId;

        /// <summary>
        /// M4-S1b：**自动追击并攻击**（负责人实测"目标不在范围内，难测"才加的）。
        /// <para>⚠️ 它只是**替你按键**：位置、伤害、掉落照样全由服务端算 —— 这不是客户端预测，
        /// 而是个调试工具（和"持续发送"同一性质）。</para>
        /// </summary>
        [SerializeField] private bool m_autoChase;

        /// <summary>自动追击：距离下一次可以按键还剩几帧（与服务端冷却同源）。</summary>
        private int m_autoAttackCooldownTicks;

        /// <summary>自动追击：一共按了多少次普攻（界面上看得见"到底有没有按出去"）。</summary>
        private int m_autoAttackCount;

        /// <summary>
        /// 自动追击**自己**的方向（与滑条 `m_inputX/m_inputY` 分开）。
        /// <para>⚠️ 存在的唯一理由：滑条那对值是**玩家手动意图**、且被两条链共用
        /// （状态同步 `SendInput` + 帧同步 `SendLockstepInput`）。追击若去写它，
        /// 就会把玩家发给帧同步的方向**悄悄改掉**（2026-10-26 真踩过，见 `TickAutoChase` 的注释）。</para>
        /// </summary>
        private int m_chaseInputX;

        /// <summary>自动追击自己的方向（前后轴）。</summary>
        private int m_chaseInputY;

        /// <summary>会话（没连时为 null）。</summary>
        private NetSession m_session;

        /// <summary>
        /// 「帧同步（锁步）」开关（S4-e 收尾起**它是真的**）：决定**客户端喂给表现层的那份数据**
        /// 是「服务端快照」还是「锁步世界经适配器转出来的那份」（见 `PresentationFeed`）。
        /// <para>⚠️ 它**不改变服务端**：那边两条链并存（状态同步链一行未改 + 锁步链照常广播）。</para>
        /// </summary>
        private bool m_lockstepMode;

        /// <summary>
        /// S4-f 的**上行**（与「帧同步」那个勾选框**无关**：勾选框管「我看哪一份数据」，
        /// 这条管「我参不参与这一局」）：上一次为哪一帧采集过意图。
        /// <para>⚠️ 用「帧号」而不是计时器当节拍：锁步里**一帧一包**才是对的，
        /// 按编辑器帧率（60+）发只会被服务端当重复丢掉（还会把「被拒」计数刷起来）。</para>
        /// </summary>
        private int m_lastLockstepInputFrame = -1;

        /// <summary>上一次锁步上行时是哪一局（`MatchSerial`）。换局要把「发到第几帧」清零 —— 见 `PumpLockstepInput`。</summary>
        private int m_lockstepSeenMatchSerial = -1;

        /// <summary>下一次锁步上行要带的按键位（普攻按钮按一下 ⇒ 置第 0 位，发出去就清掉）。</summary>
        private uint m_lockstepPendingButtons;

        /// <summary>
        /// 表现层数据源（S4-e 收尾）：按开关把「服务端快照」或「锁步世界」喂进**同一个** `SnapshotView`。
        /// <para>⚠️ 它就是"真分流"的执行点：选源那条规则在 `ViewSourceDecision.ChooseSource`（纯函数、探针直驱）。</para>
        /// </summary>
        private PresentationFeed m_feed;

        /// <summary>场景里的那条最小视图接管（只有你点过那个按钮、且**在播放模式**时才有）。</summary>
        private SnapshotSceneBinder m_binder;

        /// <summary>M4-S1：把服务端事件接进事件中心的桥（**联机战斗能推进任务**就靠它）。</summary>
        private ServerEventBridge m_eventBridge;

        /// <summary>M4-S1：演示用的条件系统（登记"击杀野狼 6001 × 3"）。</summary>
        private ConditionTracker m_conditions;

        /// <summary>M4-S1：事件中心 → 条件系统的那座桥（M2 就有的那一个，任务模块不认识网络）。</summary>
        private ConditionEventBridge m_conditionBridge;

        /// <summary>M4-S1：演示条件的编号（真项目里是 `QuestCondition` 表主键）。</summary>
        private const int DemoConditionKey = 5001;

        /// <summary>M4-S1：演示条件**优先**挑哪种怪（副本 1001 是野狼；本副本没有它就退化成"第一只非 BOSS 怪"）。</summary>
        private const int DemoPreferredMonsterId = 6001;

        /// <summary>M4-S1：手填的目标只数（**0 = 自动**，从快照里的真实阵容算）。</summary>
        [SerializeField] private int m_demoKillTarget;

        /// <summary>M4-S1：演示条件是否已经登记（登记要用到"本副本有几只"，所以等到第一张快照）。</summary>
        private bool m_demoConditionRegistered;

        /// <summary>M4-S1：上次登记演示条件时的房号（换房间要按新阵容重新登记）。</summary>
        private string m_demoConditionRoomId = string.Empty;

        /// <summary>M4-S1：实际用的怪编号（界面上显示；自动模式下来自快照）。</summary>
        private int m_demoMonsterIdInUse = DemoPreferredMonsterId;

        /// <summary>M4-S1：实际用的目标只数（界面上显示）。</summary>
        private int m_demoKillTargetInUse;

        /// <summary>M4-S1：这个演示条件达成过几次（达成回调计数）。</summary>
        private int m_demoConditionMetCount;

        // ====================================================================
        //  M4-S1c：**真任务**（读 `Quest` / `QuestCondition` / `Reward` 三张表）
        // ====================================================================

        /// <summary>配置表是否就绪（就绪后才装真任务）。</summary>
        private bool m_configsReady;

        /// <summary>配置表加载失败原因（就绪前显示在界面上）。</summary>
        private string m_configError;

        /// <summary>
        /// 真任务的会话（**复用 M2 的 `BattleSession` 装配**）。
        /// <para>⚠️ 为什么直接用它的：里面就是"`ConditionTracker` + `ConditionEventBridge` +
        /// `QuestRuntime` + `IQuestRewardSink`"那条链 —— 再在窗口里写一遍接线就是**两处实现**
        /// （迟早不一致）。窗口只用它的 `Quests` / `Conditions`，本地那个 `BattleWorld` 不参与联机。</para>
        /// </summary>
        private BattleSession m_questSession;

        /// <summary>任务面板已经接到哪个联网会话上（换了会话就要重接；见 `WireQuestPanelToServer`）。</summary>
        private NetSession m_questWiredSession;

        /// <summary>§二十八：把"跟随权威"施加到本地任务运行时的出口（只改状态）。</summary>
        private RuntimeQuestFollowSink m_questFollowSink;

        /// <summary>真面板用的**同一个视图模型**（`QuestPanel` 也用它）—— 窗口与面板一套逻辑。</summary>
        private QuestPanelModel m_questModel;

        /// <summary>可接取任务的显示行（每帧重填，避免每帧分配新列表）。</summary>
        private readonly List<QuestRow> m_offerRows = new List<QuestRow>();

        /// <summary>进行中任务的显示行。</summary>
        private readonly List<QuestRow> m_trackingRows = new List<QuestRow>();

        /// <summary>§二十七：**可交付**区（服务端说 `Completed`）。</summary>
        private readonly List<QuestRow> m_readyRows = new List<QuestRow>();

        /// <summary>§二十七：**已交付**区（服务端说 `Submitted`）。</summary>
        private readonly List<QuestRow> m_doneRows = new List<QuestRow>();

        /// <summary>M4-S2：成就的追踪视图（每帧重填，避免每帧分配新列表）。</summary>
        private readonly List<AchievementTracking> m_achievementRows = new List<AchievementTracking>();

        /// <summary>发奖记录（新的在前，最多 20 条）。</summary>
        private readonly List<string> m_rewards = new List<string>();

        /// <summary>传输（图省事由窗口自己持有，方便 Dispose）。</summary>
        private TcpTransport m_transport;

        /// <summary>界面上的日志（新的在下面）。</summary>
        private readonly List<string> m_log = new List<string>();

        /// <summary>上次推进的时刻（算 delta 用）。</summary>
        private double m_lastUpdateTime;

        /// <summary>日志框的高度（像素）—— 见 `DrawLog`：外层滚动之后必须给固定高度。</summary>
        private const float LogBoxHeight = 170f;

        /// <summary>日志滚动位置。</summary>
        private Vector2 m_logScroll;

        /// <summary>
        /// 整窗滚动位置（2026-09-26 补）。
        /// <para>⚠️ 这个窗口原来是一整列、**没有任何滚动容器**：节一多（状态 / 房间 / 世界 / 掉落 / 输入 /
        /// 任务条件 / 日志），下半截就被窗口边界裁掉，而且**没有办法翻到**——
        /// 负责人实测"全屏也看不到任务完成情况"。加一个外层滚动视图 + 顶部常驻摘要行即可。</para>
        /// </summary>
        private Vector2 m_scroll;

        /// <summary>打开窗口。</summary>
        [MenuItem("Tools/NBC/网络/网络调试窗口")]
        public static void Open()
        {
            NetDebugWindow window = GetWindow<NetDebugWindow>("NBC 网络调试");
            window.minSize = new Vector2(420f, 380f);
            window.Show();
        }

        /// <summary>挂上编辑器帧回调。</summary>
        private void OnEnable()
        {
            m_lastUpdateTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;

            // M4-S2：成就解锁/失败要**在界面上留痕**（成就没有玩家操作，不写日志就完全没有痕迹）
            EventCenter.Instance.AddEventListener<AchievementUnlockedPayload>(
                AchievementEvents.Unlocked, OnAchievementUnlocked);
            EventCenter.Instance.AddEventListener<AchievementUnlockFailedPayload>(
                AchievementEvents.UnlockFailed, OnAchievementUnlockFailed);

            LoadConfigs();      // M4-S1c：先把配置表喂进来（真任务要用）
        }

        /// <summary>摘掉回调并断开（见文件头"已知局限"）。</summary>
        private void OnDisable()
        {
            EditorApplication.update -= Tick;

            EventCenter.Instance.RemoveEventListener<AchievementUnlockedPayload>(
                AchievementEvents.Unlocked, OnAchievementUnlocked);
            EventCenter.Instance.RemoveEventListener<AchievementUnlockFailedPayload>(
                AchievementEvents.UnlockFailed, OnAchievementUnlockFailed);

            CloseSession("窗口关闭");

            // ⚠️ 订阅了就必须退订：这两座桥都挂在全局的 `EventCenter` / `NetSession` 上，
            //    不退订 = 窗口关了回调还在跑（A4/A8 踩过的同一个坑）。
            if (m_conditionBridge != null)
            {
                m_conditionBridge.Dispose();
                m_conditionBridge = null;
            }

            m_conditions = null;

            // M4-S1c：真任务的会话也要收掉（它内部订阅了 `EventCenter`，不退订就是"对象没人用了回调还在跑"）
            if (m_questSession != null)
            {
                m_questSession.Dispose();
                m_questSession = null;
                m_questModel = null;
            }
        }

        /// <summary>
        /// M4-S1c：把配置表喂进 `ConfigMgr`，就绪后装一局真任务。
        /// <para>⚠️ 编辑器里用的是 `EditorConfigSource`（**AssetDatabase 直读**），**不走 YooAsset** ——
        /// 因为这里没进 Play、资源层没装配。那条路只在运行时走（`AssetConfigSource`）。</para>
        /// </summary>
        private void LoadConfigs()
        {
            m_configsReady = false;
            m_configError = null;

            ConfigMgr.Instance.SetSource(new EditorConfigSource());

            GameTables.PreloadAll(
                OnConfigsReady,
                reason =>
                {
                    m_configError = reason;
                    AddLog("配置表预加载失败：" + reason);
                });
        }

        /// <summary>配置表就绪 → 装一局真任务（`BattleSession` 里那条"条件 → 任务 → 发奖"的链）。</summary>
        private void OnConfigsReady()
        {
            m_configsReady = true;

            // ⚠️ 英雄编号**从表里取**（第一个 Hero），不写死：
            //    窗口只拿它装配本地那一局；联机打的怪由服务端说了算（服务端固定英雄 1001，见 `DungeonBattle.HeroConfigId`）。
            int heroId = FirstHeroId();

            m_questSession = BattleSession.FromConfigMgr(heroId, new EditorQuestRewardSink(this));
            m_questModel = new QuestPanelModel(m_questSession.Quests);

            // §二十六：把面板的「接取 / 交付」接到**联网**会话上（列表仍来自本地 `BattleSession`）
            WireQuestPanelToServer();

            AddLog("配置表就绪：真任务已装好（英雄 " + heroId + "）");
        }

        /// <summary>取 `Hero` 表里第一个英雄的编号（取不到就 1001 —— M3 服务端固定的那个）。</summary>
        /// <returns>英雄编号。</returns>
        /// <summary>
        ///     §二十六：任务面板 → 联网会话的**三行装配**。
        ///     <para>⚠️ 面板的**列表**来自本地 `BattleSession`，而**动作**要发给服务端 ——
        ///     这是两个对象，所以必须显式接一次。</para>
        ///     <para>① 「接取 / 交付」⇒ `SendQuestAction`（本地**不自己改状态**，等它确认）；
        ///     ② 收到 `quest_state` ⇒ 清「待确认」；③ 收到错误 ⇒ 落定并把**原因原文**显示出来。</para>
        ///     <para>⚠️ 幂等：同一个会话只接一次；换了会话（重连）自动重接。</para>
        /// </summary>
        private void WireQuestPanelToServer()
        {
            if (m_questModel == null || m_session == null || ReferenceEquals(m_questWiredSession, m_session))
            {
                return;
            }

            m_questWiredSession = m_session;

            // ① 点按钮 ⇒ 发请求（`SendAction` 一旦设上，`Accept`/`Submit` 就**不再本地改状态**）
            m_questModel.SendAction = (questId, action) => m_session.SendQuestAction(questId, action);

            // ①′ §二十七：**任务状态**的权威来源（与条件进度那个 `Authority` 并排，不合并）
            //     —— 面板据此把任务分区（可接 / 进行中 / 可交付 / 已交付）；服务端没说时返回 false，
            //     面板退回本地判断并标「（本地预测）」。
            m_questModel.StateAuthority = new NetSessionQuestStateAuthority(m_session);

            // ①″ §二十八：本地状态机**跟随**权威的出口（只改状态：不发奖/不重置/不提示）
            m_questFollowSink = new RuntimeQuestFollowSink(m_questSession.Quests);

            // ② 服务端表态（无论"接了""已交付"还是"没接过"）⇒ 清掉「待确认」
            m_session.QuestStateReceived += sync =>
            {
                for (int i = 0; i < sync.Entries.Count; i++)
                {
                    m_questModel.ClearPending(sync.Entries[i].QuestId);
                }

                // §二十八：本地状态机也跟着走（决策是引擎无关的纯逻辑，见 `QuestStateReconciler`）
                FollowLocalQuestState(sync);
            };

            // ③ 被拒 ⇒ 落定 + 原因原文（错误包里没有任务编号 ⇒ 只能一起落定，见 `FailAllPending`）
            m_session.ErrorReceived += error =>
            {
                if (error.Code == NetErrors.QuestRejected ||
                    error.Code == NetErrors.QuestUnavailable ||
                    error.Code == NetErrors.PlayerMismatch)
                {
                    int settled = m_questModel.FailAllPending(error.Message);

                    AddLog("[任务] 请求被服务端拒绝（码 " + error.Code + "：" +
                           NetErrors.Describe(error.Code) + "）：" + error.Message +
                           (settled > 0 ? "（落定 " + settled + " 个待确认）" : string.Empty));
                }
            };

            AddLog("[任务] 面板已接到联网会话：点「接取 / 交付」会发给服务端（本地不再自己改状态）");
        }

        /// <summary>
        /// §二十八：**本地状态机跟随权威** —— 服务端那份状态 ⇒ 本地该做什么。
        /// <para>⚠️ 决策在 `QuestStateReconciler`（引擎无关、探针能跑能变异）；这里只负责
        /// 喂数据与施加，**不自己判断**。</para>
        /// <para>⚠️ 只跟随**同步里出现的**任务；"全量表里没有它"= 未接取，由面板分区去表达
        /// （见 `QuestStateReconciler.Plan` 的注释）—— 这里不越权发明任务。</para>
        /// </summary>
        /// <param name="sync">服务端发来的全量任务状态。</param>
        private void FollowLocalQuestState(QuestStateSync sync)
        {
            if (m_questSession == null || m_questFollowSink == null || sync == null)
            {
                return;
            }

            // 输入：同步里每条 = 「任务编号 + 服务端状态」
            //（`QuestSectionPlanner.Entry.LocalState` 那一栏在这里装的是**服务端**状态 —— 见 Plan 的注释）
            var entries = new List<QuestSectionPlanner.Entry>(sync.Entries.Count);

            for (int i = 0; i < sync.Entries.Count; i++)
            {
                entries.Add(new QuestSectionPlanner.Entry(sync.Entries[i].QuestId, sync.Entries[i].State));
            }

            List<QuestSyncDecision> plan = QuestStateReconciler.Plan(
                entries,
                questId => (int)m_questSession.Quests.StateOf(questId),
                questId => m_questModel != null && m_questModel.IsPending(questId));

            if (plan.Count == 0)
            {
                return;
            }

            int applied = QuestStateReconciler.Apply(plan, m_questFollowSink);

            if (applied > 0)
            {
                AddLog("[任务] 本地状态跟随权威：" + applied + " 个任务（只改状态：不发奖/不重置/不提示）");
            }
        }

        private static int FirstHeroId()
        {
            if (!ConfigMgr.Instance.IsLoaded("Hero"))
            {
                return 1001;
            }

            HeroConfig heroes = ConfigMgr.Instance.Get<HeroConfig>();

            return heroes != null && heroes.rows != null && heroes.rows.Count > 0
                ? heroes.rows[0].id
                : 1001;
        }

        /// <summary>把一份奖励记成界面上看得见的一行（真面板走的是**同一个** `IQuestRewardSink` 接缝）。</summary>
        /// <param name="questId">任务编号。</param>
        /// <param name="reward">奖励内容。</param>
        private void OnRewardGranted(int questId, QuestReward reward)
        {
            string line = "奖励到账：任务 " + questId
                + " → 经验 " + reward.Exp + "、金币 " + reward.Gold
                + (reward.ItemId > 0 ? "、物品 " + reward.ItemId + "×" + reward.ItemCount : string.Empty);

            m_rewards.Insert(0, line);

            while (m_rewards.Count > 20)
            {
                m_rewards.RemoveAt(m_rewards.Count - 1);
            }

            AddLog(line);
        }

        /// <summary>
        /// M4-S2：成就解锁了。
        /// <para>⚠️ 成就**没有玩家操作**（条件一齐就自动解锁），所以"什么时候解的"
        /// 只有日志能告诉你 —— 玩家反馈"我明明打了 10 只狼"时，这里就是唯一的证据。</para>
        /// </summary>
        /// <param name="payload">载荷。</param>
        private void OnAchievementUnlocked(AchievementUnlockedPayload payload)
        {
            AddLog("🏆 成就解锁：" + payload.AchievementId + "（奖励 " + payload.RewardId + " 已发）");
        }

        /// <summary>M4-S2：成就该解锁却没解开（配置问题）—— **必须显眼**，否则就是一条无声的悬案。</summary>
        /// <param name="payload">载荷。</param>
        private void OnAchievementUnlockFailed(AchievementUnlockFailedPayload payload)
        {
            AddLog("⚠️ 成就 " + payload.AchievementId + " 解锁失败：" + payload.Reason);
        }

        /// <summary>奖励接缝的编辑器实现：把"发奖"变成窗口里的一行日志。</summary>
        private sealed class EditorQuestRewardSink : IQuestRewardSink
        {
            private readonly NetDebugWindow m_window;

            /// <summary>建一个发奖口。</summary>
            /// <param name="window">窗口（把奖励写进它的日志）。</param>
            public EditorQuestRewardSink(NetDebugWindow window)
            {
                m_window = window;
            }

            /// <summary>发一份奖励。</summary>
            /// <param name="questId">任务编号。</param>
            /// <param name="reward">奖励内容。</param>
            public void Grant(int questId, QuestReward reward)
            {
                m_window.OnRewardGranted(questId, reward);
            }
        }

        /// <summary>编辑器帧回调：推进会话并重绘。</summary>
        private void Tick()
        {
            if (m_session == null)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            int deltaMs = (int)Math.Round((now - m_lastUpdateTime) * 1000.0);
            m_lastUpdateTime = now;

            if (deltaMs < 0)
            {
                deltaMs = 0;
            }
            else if (deltaMs > MaxDeltaMs)
            {
                deltaMs = MaxDeltaMs;   // 见文件头：宁可走慢，不许跳
            }

            m_session.Pump(deltaMs);
            FeedPresentationLayer();                // ⭐ 真分流：按开关把「快照 / 锁步」喂给表现层
            EnsureDemoConditionRegistered();        // 第一张快照到了就按阵容登记演示条件
            SendContinuousInput();
            PumpLockstepInput();                    // ⭐ S4-f：锁步对局里一帧一条上行（否则 PVP 世界里没人动）
            TickAutoChase();
            Repaint();
        }

        /// <summary>
        /// M4-S1b：自动追击并攻击（每编辑器帧一次；关掉开关就什么都不做）。
        /// <para>做三件事：① 目标死了/没了就换成**最近的活怪**；② 不在射程内就朝它走；
        /// ③ 进了射程就**按一下普攻**再等冷却（和服务端同一个冷却常量）。</para>
        /// <para>⚠️ 距离用**切比雪夫距离** —— 与服务端 `DungeonBattle.DistanceMm` 同一种量法；
        /// 用欧氏距离会在四角误判成"还差一点"，于是你会站在射程边上永远不按攻击。</para>
        /// <para>⚠️⚠️ **它绝不写滑条那两个值**（`m_inputX` / `m_inputY`）：那是**玩家手动意图**，
        /// 而且现在被**两条链共用**（状态同步的 `SendInput` 与帧同步的 `SendLockstepInput`）。
        /// 2026-10-26 真踩过：这个调试开关把滑条一路改写成 0 ⇒ 帧同步上行的方向**永远是 0**，
        /// 表现为「滑条拉到 1000，`单位-1` 却一动不动」，而服务端与对账**全都正常**
        /// （0 位移两边一致，`DesyncCount` 还是 0）—— 只有看画面才发现。见 `Docs\27` §37.6 ③。
        /// 它要用方向就写自己的那对字段（`m_chaseInputX` / `m_chaseInputY`）。</para>
        /// </summary>
        private void TickAutoChase()
        {
            if (!m_autoChase || m_session == null || !m_session.IsOnline || !m_session.InRoom)
            {
                return;
            }

            SnapshotView world = m_session.World;
            NBC.Protocol.EntitySnapshot mine = world.FindHero(m_session.PlayerId);

            if (mine == null)
            {
                // 快照里还没有"我的英雄"（刚进房 / 已经死了）—— 什么都不发
                m_autoAttackCooldownTicks = 0;
                return;
            }

            NBC.Protocol.EntitySnapshot target = FindEntity(world, m_targetEntityId);

            if (target == null || !target.Alive)
            {
                m_targetEntityId = NearestAliveMonsterId();
                target = FindEntity(world, m_targetEntityId);

                if (target == null)
                {
                    return;     // 没怪可打了（副本清完）
                }
            }

            // ⚠️ 射程判定用**圆**（平方比较），与服务端 `BattleRules.WithinBasicAttackRange` 同一份规则。
            //    别再退回切比雪夫（正方形）：那会在对角方向多算 1.41 倍 ⇒ 客户端以为能打、服务端说够不着。
            bool inRange = BattleRules.WithinBasicAttackRange(
                mine.PosXMm, mine.PosZMm, target.PosXMm, target.PosZMm);

            if (!inRange)
            {
                int moveX = target.PosXMm == mine.PosXMm ? 0 : (target.PosXMm > mine.PosXMm ? 1000 : -1000);
                int moveY = target.PosZMm == mine.PosZMm ? 0 : (target.PosZMm > mine.PosZMm ? 1000 : -1000);

                m_autoAttackCooldownTicks = 0;      // 赶路时不用按攻击
                m_chaseInputX = moveX;              // ⚠️ 自己的字段：滑条归玩家，追人归追人
                m_chaseInputY = moveY;
                m_session.SendInput(moveX, moveY, 0u, 0u, m_targetEntityId);
                return;
            }

            // 射程内：站住 + 按一下普攻（按下 → 下一帧松开 → 等冷却，模拟"点一下"）
            m_chaseInputX = 0;
            m_chaseInputY = 0;

            if (m_autoAttackCooldownTicks > 0)
            {
                bool release = m_autoAttackCooldownTicks == BattleRules.BasicAttackCooldownTicks;
                m_autoAttackCooldownTicks--;
                m_session.SendInput(0, 0, 0u, release ? 1u : 0u, m_targetEntityId);
                return;
            }

            m_session.SendInput(0, 0, 1u, 0u, m_targetEntityId);
            m_autoAttackCount++;
            m_autoAttackCooldownTicks = BattleRules.BasicAttackCooldownTicks;
        }

        /// <summary>
        /// 勾了「持续发送」就每帧发一条输入（模拟"一直按着方向键"）。
        /// <para>
        /// ⚠️ 客户端**发多密都不影响速度** —— 服务端每 tick 只应用一次（移动是状态）。
        /// 这里按编辑器帧率发（可能 60+/秒），正好也是在验证这条限速。
        /// </para>
        /// </summary>
        private void SendContinuousInput()
        {
            if (!m_inputContinuous || m_session == null || !m_session.IsOnline || !m_session.InRoom)
            {
                return;
            }

            m_session.SendInput(m_inputX, m_inputY, 0u, 0u, m_targetEntityId);
        }

        /// <summary>
        /// S4-f：锁步对局里的**上行** —— 服务端每推进一帧，就把我此刻的意图发一条 `lockstep_input`。
        ///
        /// <para>⚠️ 触发条件是「**本局已开局**」，**不是**「帧同步勾选框被勾上」：
        /// 那个勾选框管的是「我**看**哪一份数据」（`PresentationFeed` 的选源），
        /// 而上行管的是「我**参不参与**这一局」。把它挂在勾选框上会有一个很隐蔽的后果 ——
        /// 切回快照模式后我的这一席就再没人交输入，锁步世界里的我永远被判「缺人」，
        /// 而因为画面上看的是快照，**没有任何人会注意到**。</para>
        ///
        /// <para>⚠️ 为什么按「帧」而不是按「编辑器帧」节拍：锁步里**一帧一包**才是对的。
        /// 按 60Hz 发会被服务端当「同一帧的重复输入」丢掉并计数（`LockstepScheduler` 文件头 ②），
        /// 于是界面上看着在发，实际只有第一条生效 —— 那种"看起来在动"的假象最难查。</para>
        ///
        /// <para>⚠️ 换局必须清零：新一局的 `LastSteppedFrame` 从 -1 重新开始，
        /// 若还拿着上一局的「发到第 500 帧」，那要等新局也走到 500 帧才会再发一条 —— 表现为
        /// 「第二局我的英雄不动」，而且不报错。清零的判据用 `MatchSerial`（服务端换局给的新编号），
        /// 不用"帧号有没有倒退"去猜。</para>
        ///
        /// <para>⚠️ 这条只管**锁步那条链**：`SendInput`（状态同步）照旧由 `SendContinuousInput` 发，
        /// 两条链并存、互不替代（见 `NetSession` 文件头）。</para>
        /// </summary>
        private void PumpLockstepInput()
        {
            if (m_session == null)
            {
                return;
            }

            LockstepClient client = m_session.Lockstep;

            // ⚠️ **没有「进行中的对局」就整体清零** —— 这一条专门管**断线重连**：
            //    每次「连接」都会**新建一个 `NetSession`**（`ConnectSession`），而它的 `Connect()`
            //    把 `MatchSerial` 清回 0 ⇒ 新会话第一局又是 **1**，与上一局撞号。
            //    只按局号比较的话，这里会以为「还是同一局」，把上一局的「已发到第 500 帧」留着 ⇒
            //    新局要等追平 500 帧才恢复上行。表现就是「重连后我的英雄不动」，**而且不报错**。
            if (client == null || !client.Started)
            {
                ResetLockstepUplinkState();
                return;
            }

            if (m_lockstepSeenMatchSerial != m_session.MatchSerial)
            {
                // 同一会话里的**第二局**（运行器被换掉、`LastSteppedFrame` 从 -1 重开）
                m_lockstepSeenMatchSerial = m_session.MatchSerial;
                m_lastLockstepInputFrame = -1;
                m_lockstepPendingButtons = 0u;
            }

            if (!m_session.CanSendLockstepInput)
            {
                return;
            }

            if (client.LastSteppedFrame <= m_lastLockstepInputFrame)
            {
                return;         // 这一帧已经交过了 ⇒ 不重复发
            }

            m_lastLockstepInputFrame = client.LastSteppedFrame;

            uint buttons = m_lockstepPendingButtons;
            m_lockstepPendingButtons = 0u;

            // 计数不在这里另存一份：`NetSession.LockstepInputsSent` / `LockstepInputsSkipped` 就是唯一口径
            // （界面直接读那两个数，少一处会跟线上对不上的影子计数）
            //
            // ⚠️ 一次 Tick **至多一条**：`Pump` 可能一口气吞下好几帧（编辑器卡顿后补帧），
            //    那中间那几帧**必然缺我这个人的输入**（服务端填默认 + `Missing`）。
            //    这是锁步的固有代价，不是 bug —— 换个更密的节拍只会变成「同一帧发两次」被服务端丢掉。
            m_session.SendLockstepInput(m_inputX, m_inputY, buttons, m_targetEntityId);
        }

        /// <summary>把「上行发到第几帧」那三个游标清零（换会话 / 断线 / 还没开局时都要清）。</summary>
        private void ResetLockstepUplinkState()
        {
            m_lastLockstepInputFrame = -1;
            m_lockstepSeenMatchSerial = -1;
            m_lockstepPendingButtons = 0u;
        }

        /// <summary>
        /// 同步模式开关 + 帧同步（锁步）状态面板 + ⭐ **表现层数据源**（M4-S4 S4-e 收尾）。
        ///
        /// <para>⭐ **这个开关现在是真的**：它决定**客户端喂给表现层的那份数据**是「服务端快照」
        /// 还是「锁步世界经适配器转出来的那份」（执行点是 `FeedPresentationLayer` → `PresentationFeed.Refresh`）。</para>
        /// <para>⚠️ 但它**不改变服务端**：那边**两条链并存**（状态同步链一行未改 + 锁步链照常广播），
        /// 开关只换客户端**读哪一份**、不停任何一条链。</para>
        /// <para>⚠️ 上一轮那块黄色警告写的是「该开关当前只影响本窗口的显示」—— **那句话本轮已经不成立了**，
        /// 所以换成了下面这段真话（**不做假开关，也不留过期说明**）。</para>
        /// </summary>
        private void DrawLockstepSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("【同步模式】", EditorStyles.boldLabel);

            bool previous = m_lockstepMode;

            m_lockstepMode = EditorGUILayout.ToggleLeft(
                "帧同步（锁步）：服务端按帧广播输入、客户端自己 Step 并对账　／　关闭 = 状态同步（快照）",
                m_lockstepMode);

            if (m_lockstepMode != previous)
            {
                SetLockstepMode(m_lockstepMode, "勾选框");
            }

            EditorGUILayout.HelpBox(
                "⭐ 这个开关真的分流：它决定客户端喂给表现层的那份数据 —— " +
                "关 = 服务端快照；开 = 锁步世界经适配器（`LockstepSnapshotAdapter`）转出来的那份。\n" +
                "⚠️ 服务端两条链并存（状态同步链一行未改 + 锁步链照常广播）：开关只换客户端读哪一份，" +
                "不停任何一条链。\n" +
                "⚠️ 开着但**世界不可用**（没开局 / 世界是 null / **世界 0 实体**）时一律回退用快照，并计入下面的计数；\n" +
                "⚠️ **已开局但帧不再前进**（一局结束 / 服务端停播）也算故障 ⇒ 连续 " +
                PresentationFeed.StallRefreshThreshold + " 次刷新帧号没变就**自动回退到快照**（「⚠️ 锁步停滞（已回退）」）；\n" +
                "⚠️ 回锁步要**滞回**：连续 " + PresentationFeed.StallRecoverThreshold +
                " 次**真前进**才切回锁步（期间仍回退 —— 否则 30 次回退 / 1 帧恢复会稳定地闪）。",
                MessageType.Info);

            DrawPresentationSource();

            NetSession session = m_session;

            DrawLockstepPanel(session);
        }

        /// <summary>
        /// ⭐ **表现层数据源**那一块：现在喂的是谁、喂了多少次、回退了几次、切了几次、世界长什么样，
        /// 外加「把这份数据接到场景」那个按钮（最小视图接管）。
        /// </summary>
        private void DrawPresentationSource()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("⭐ 表现层数据源（开关决定它）", EditorStyles.boldLabel);

            PresentationFeed feed = m_feed;

            if (feed == null || m_session == null)
            {
                EditorGUILayout.LabelField("⭐ 当前喂给表现层的来源", "（还没连接 —— 先点「连接」）");
                return;
            }

            EditorGUILayout.LabelField("⭐ 当前喂给表现层的来源", ViewSourceDecision.Describe(feed.Source));

            EditorGUILayout.LabelField(
                "开关要求 / 实际生效",
                (feed.UseLockstep ? "锁步" : "快照") + " / " + ViewSourceDecision.Describe(feed.Source)
                + (feed.UseLockstep && feed.Chosen != EViewSource.Lockstep
                    ? "　⚠️ 回退中（" + ViewSourceDecision.Describe(feed.FallbackReason) + "）"
                    : string.Empty));

            EditorGUILayout.LabelField(
                "表现层帧号（本地单调）",
                feed.PresentationTick + "（源帧 " + feed.LastSourceFrame + "）");

            EditorGUILayout.LabelField(
                "采纳次数",
                "快照 " + feed.FramesFromSnapshot + " 次 / 锁步 " + feed.FramesFromLockstep + " 次");

            // ⚠️ 两个**不同口径**的数，分开显示（上一版混成一个 ⇒ 一分钟上万 ⇒ 判据失效）。
            //    **事件数**：跃迁进回退才 +1（一段连续回退 = 1 次）—— 判据看这个。
            //    **帧数**：回退期间每帧 +1（≈编辑器帧率）—— 只用来量"回退持续了多久"。
            EditorGUILayout.LabelField(
                "⚠️ 回退事件数（跃迁进回退才 +1）",
                feed.FallbackEvents + (feed.FallbackEvents > 0
                    ? "（一段连续回退只算 1 次；进房到开局之间非 0 是**正常**的）"
                    : "（还没回退过）"));

            EditorGUILayout.LabelField(
                "⚠️ 回退帧数（回退中每帧 +1）",
                feed.FallbackFrames + "（≈编辑器帧率 ⇒ **它不表示「回退了几次」**，判据看上面那个事件数）");

            // ⭐ 停滞（**沉默冻结**）那条：`Started` 但帧不再前进 ⇒ 已自动回退到快照。
            //    上一版这里没有任何一行字 —— 一局结束/服务端停播之后画面**永远停在上一帧**，而计数一动不动。
            //    ⚠️ 本轮加**滞回**：回锁步要连续 `StallRecoverThreshold` 次真前进（否则 30/1/30 抖动会闪）。
            EditorGUILayout.LabelField(
                "⚠️ 锁步停滞（已回退）",
                feed.Stalled
                    ? "是（连续 " + feed.StallRefreshes + " 次刷新帧号没前进 ⇒ **已经回退到快照**；" +
                      "回锁步进度 " + feed.StallRecoverProgress + "/" + PresentationFeed.StallRecoverThreshold +
                      " 次真前进）"
                    : "否（连续 " + feed.StallRefreshes + "/" + PresentationFeed.StallRefreshThreshold +
                      " 次没前进就会判停；判停后要 " + PresentationFeed.StallRecoverThreshold +
                      " 次真前进才回锁步）");

            EditorGUILayout.LabelField(
                "切换次数",
                "实际换源 " + feed.SourceSwitches + " 次 / 拨开关 " + feed.ModeSwitches + " 次");

            SnapshotView shown = feed.View;

            EditorGUILayout.LabelField(
                "表现层里的世界",
                shown.AliveCount + "/" + shown.EntityCount + " 个活着　（表现帧 " + shown.ServerTick + "）");

            // ⭐ 这一行是「分流切对了没有」最直观的判据：
            //    关（快照）时**不该**有机器人 9001…；开（锁步）时**应该**有 1、9001、9002、9003
            EditorGUILayout.LabelField("表现层里的单位", DescribeUnits(shown));

            EditorGUILayout.LabelField(
                "丢弃旧帧（非 0 = 帧号在倒退）",
                shown.StaleIgnored + (shown.StaleIgnored > 0
                    ? "　❌ 帧号在倒退 ⇒ 数据会被静静丢掉（画面停住/空白）"
                    : "　✅"));

            // ---- 最小视图接管（3D）：**只在播放模式**里建，不动任何场景/Prefab 资产 ----
            EditorGUILayout.LabelField(
                "场景里的视图接管（3D）",
                m_binder == null ? "未建（点下面的按钮）" : m_binder.Describe());

            if (GUILayout.Button("把这份数据接到场景（建一个运行时视图接管）", GUILayout.Height(22f)))
            {
                m_binder = SnapshotSceneBinder.Ensure(feed.View, m_session.PlayerId);

                AddLog(m_binder == null
                    ? "[表现层] 没建成视图接管：只在播放模式下能建（编辑器里请先点 Play）"
                    : "[表现层] 已在场景里建好视图接管：" + m_binder.Describe() +
                      "（用 Unity 自带几何体；没动任何场景/Prefab 资产）");
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "⚠️ 视图接管只能在播放模式里建（运行时 `AddComponent`）。" +
                    "本轮不许改场景资产，所以默认场里没有它 —— 点 Play 之后回到这个窗口点上面那个按钮。",
                    MessageType.Warning);
            }
        }

        /// <summary>
        /// 把表现层那份世界里的单位编号串成一行（例：`1、9001、9002、9003`）。
        /// <para>⭐ 这是「分流切对了没有」最直观的判据：关（快照）时不该有机器人，开（锁步）时该有。</para>
        /// </summary>
        /// <param name="view">表现层那份数据。</param>
        /// <returns>编号表；没有单位时给一句人话。</returns>
        private static string DescribeUnits(SnapshotView view)
        {
            if (view == null || view.EntityCount == 0)
            {
                return "（一个都没有）";
            }

            var builder = new StringBuilder();

            for (int i = 0; i < view.Entities.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('、');
                }

                builder.Append(view.Entities[i].EntityId);
            }

            return builder.ToString();
        }

        /// <summary>帧同步（锁步）那份只读状态面板（与上一轮同源，只是从 `DrawLockstepSection` 拆出来）。</summary>
        /// <param name="session">当前会话（可为 null）。</param>
        private void DrawLockstepPanel(NetSession session)
        {
            if (session == null || session.Lockstep == null)
            {
                EditorGUILayout.LabelField("锁步运行器", "未创建（还没收到任何锁步消息）");
                return;
            }

            LockstepClient client = session.Lockstep;

            // ⚠️ 局号与"每局清零"的那些数（§35.9）：换一局运行器就换一个 ⇒ 下面的计数是**本局**的，
            //    所以标签里必须写"本局"，否则会被当成本会话的累计值（那会让"这局干不干净"看不出来）。
            EditorGUILayout.LabelField("本会话第几局（每收到一次开局 +1）", session.MatchSerial.ToString());
            EditorGUILayout.LabelField("锁步已开局", client.Started ? "是" : "否");
            EditorGUILayout.LabelField("帧号推进", client.StepsExecuted + " 帧（末帧 " + client.LastSteppedFrame + "）");
            EditorGUILayout.LabelField("⭐ 分歧计数（本局；每局清零）", client.DesyncCount.ToString());
            EditorGUILayout.LabelField("未开局忽略（本局）", client.IgnoredBeforeStart.ToString());
            EditorGUILayout.LabelField("重复/倒退被拒（本局）", client.RejectedFrames.ToString());
            EditorGUILayout.LabelField("胜负判定不一致（本局）", session.MatchMismatchCount.ToString());
            EditorGUILayout.LabelField("结果（本局）", session.MatchOutcome == null ? "未宣布" : session.MatchOutcome.Value.ToString());
            EditorGUILayout.LabelField("一句话", client.Describe());

            // ⭐ S4-f：**上行**（没有这条，服务端永远把我当「缺人」，画面上就是谁都不动）
            EditorGUILayout.LabelField("上行（我发给服务端的输入）",
                "已发 " + session.LockstepInputsSent + " 条／被挡 " + session.LockstepInputsSkipped
                + " 次；下一条打第 " + session.NextLockstepFrame + " 帧");
            EditorGUILayout.LabelField("能不能发上行", session.CanSendLockstepInput
                ? "能（在线 + 已开局 + 本局还没宣布胜负）"
                : "不能（未在线／未开局／已宣布胜负）");
        }

        /// <summary>
        /// 拨开关：**同一个动作**做两件事 —— 改 `PresentationFeed` 的选源 + **记一行日志**（可回溯）。
        /// <para>⚠️ 日志里必须写清"服务端两条链都还在跑"，否则下一个看到它的人会以为真的"换了一条链"。</para>
        /// </summary>
        /// <param name="on">true = 帧同步。</param>
        /// <param name="from">谁拨的（勾选框 / 代码）。</param>
        private void SetLockstepMode(bool on, string from)
        {
            m_lockstepMode = on;

            PresentationFeed feed = EnsureFeed();
            feed.UseLockstep = on;

            // R004：模式写进同源统计（没连的时候仍是「离线」，不为切换而谎报在线）
            if (m_session != null)
            {
                NetRuntimeStats.SetMode(on ? NetSyncMode.Lockstep : NetSyncMode.StateSync);
            }

            AddLog("[表现层] 数据源切换（" + from + "）⇒ " + (on ? "锁步" : "快照") +
                   "：服务端两条链都还在跑，只有客户端改读哪一份。");
        }

        /// <summary>
        /// 取（必要时建）表现层数据源，并让它的开关跟窗口上的勾选态保持一致。
        /// <para>⚠️ 幂等：`UseLockstep` 的值没变就不会涨计数（每帧都会调到这里）。</para>
        /// </summary>
        /// <returns>数据源。</returns>
        private PresentationFeed EnsureFeed()
        {
            if (m_feed == null)
            {
                m_feed = new PresentationFeed();
            }

            m_feed.UseLockstep = m_lockstepMode;
            return m_feed;
        }

        /// <summary>
        /// ⭐ **真分流唯一的执行点**：每帧把「当前该用的那份」喂给表现层。
        /// <para>它**只读**两条链的状态（`NetSession.World` / `LockstepClient`）——
        /// 不写回世界、不调 `Step`、不改会话。</para>
        /// </summary>
        private void FeedPresentationLayer()
        {
            if (m_session == null)
            {
                return;
            }

            PresentationFeed feed = EnsureFeed();
            LockstepClient client = m_session.Lockstep;

            feed.Refresh(
                m_session.PlayerId,
                m_session.World,
                client == null ? null : client.World,
                client == null ? -1 : client.LastSteppedFrame,
                client != null && client.Started,
                // ⚠️ **局号**（本轮加的"换局"显式信号）：不传的话只能靠世界对象/帧号倒退猜，
                //    而"上一局末帧 0、新一局从 0 起"那一格是猜不出来的（§35.9 必修 1 / 小坑 5）。
                m_session.MatchSerial);

            // R004：RTT 与帧号写进同源统计。
            //  ⚠️ 两种模式的「帧号」语义不同：状态同步 = 快照 tick，帧同步 = 已步进帧 —— 按模式取，别混。
            NetRuntimeStats.SetRtt(m_session.RttMs);
            NetRuntimeStats.SetFrame(m_lockstepMode
                ? (client == null ? -1 : client.LastSteppedFrame)
                : (int)m_session.World.ServerTick);
        }

        private void OnGUI()
        {            // ⚠️ 2026-09-26：这个窗口原来是一整列、**没有滚动**的 —— 节一多（状态/房间/世界/掉落/输入/
            //    任务条件/日志），下半截就被裁掉且**翻不到**（负责人："全屏也看不到任务完成情况"）。
            //    现在分两层：
            //      ① **顶部常驻摘要行**（在滚动视图外面）—— 最要紧的几个数不用滚就能看到；
            //      ② 其余内容统一放进**整窗滚动视图** —— 滚轮 / 拖拽都能翻到底。
            DrawSummaryLine();

            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("服务端", EditorStyles.boldLabel);

            m_host = EditorGUILayout.TextField("地址", m_host);
            m_port = EditorGUILayout.IntField("端口", m_port);
            m_playerName = EditorGUILayout.TextField("玩家名", m_playerName);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("账号（M4-S3：留空 = 游客）", EditorStyles.boldLabel);

            m_account = EditorGUILayout.TextField("登录名", m_account);
            m_password = EditorGUILayout.PasswordField("密码", m_password);

            if (!string.IsNullOrEmpty(m_account))
            {
                EditorGUILayout.HelpBox(
                    "登录成功后 player_id 来自**账号自己的档案** —— 断开再连还是同一个编号，" +
                    "战绩会累计到同一个人身上。\n" +
                    "测试账号：test01~test04，密码 123456（见 Docs\\08 的种子数据）。\n" +
                    "⚠️ 登录**只查内存**：服务端启动后又新建的账号要重启服务端才认。",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "游客：player_id 由服务端**按连接**分配（旧行为）。要验证登录就填上面两格。",
                    MessageType.None);
            }

            EditorGUILayout.Space();

            bool busy = m_session != null
                        && (m_session.State == ESessionState.Connecting
                            || m_session.State == ESessionState.Handshaking
                            || m_session.State == ESessionState.Online);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(busy))
                {
                    if (GUILayout.Button("连接", GUILayout.Height(24f)))
                    {
                        ConnectSession();
                    }
                }

                using (new EditorGUI.DisabledScope(!busy))
                {
                    if (GUILayout.Button("断开", GUILayout.Height(24f)))
                    {
                        CloseSession("手动断开");
                    }

                    if (GUILayout.Button("发一次心跳", GUILayout.Height(24f)))
                    {
                        m_session.SendPingNow();
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("房间", EditorStyles.boldLabel);

            m_dungeonId = EditorGUILayout.IntField("副本编号", m_dungeonId);
            m_roomId = EditorGUILayout.TextField("房号（留空 = 服务端安排）", m_roomId);

            bool online = m_session != null && m_session.IsOnline;
            bool inRoom = m_session != null && m_session.InRoom;

            DrawLockstepSection();

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!online || inRoom))
                {
                    if (GUILayout.Button("加入房间", GUILayout.Height(22f)))
                    {
                        m_session.JoinRoom(m_roomId, m_dungeonId);
                    }
                }

                using (new EditorGUI.DisabledScope(!inRoom))
                {
                    if (GUILayout.Button("离开房间", GUILayout.Height(22f)))
                    {
                        m_session.LeaveRoom();
                    }
                }
            }

            EditorGUILayout.Space();
            DrawStatus();
            DrawQuestLoop();
            DrawRealQuests();
            DrawRealAchievements();
            EditorGUILayout.Space();
            DrawLog();

            if (m_session == null)
            {
                EditorGUILayout.HelpBox(
                    "还没连接。先起服务端，再点「连接」：\n\n" +
                    "Server\\NBC.Server.Host\\bin\\Debug\\net8.0\\NBC.Server.Host.exe --port=" + m_port + "\n\n" +
                    "（或在仓库根目录跑 dotnet build Server\\NBC.sln -m:1 之后用上面这个 exe）",
                    MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 顶部**常驻摘要行**：不滚动也能看到的那几个数（会话 / 房间 / 世界 / **任务进度** / 事件计数 / 输入）。
        /// <para>为什么要它：整窗滚动解决了"看不到"，但**最要紧的数仍要滚一下才能看到**；
        /// 而调试时你反复要看的恰恰就是"任务涨没涨、事件有没有来"。所以把它钉在顶上。</para>
        /// </summary>
        private void DrawSummaryLine()
        {
            if (m_session == null)
            {
                EditorGUILayout.LabelField("会话：未连接（点下面的「连接」）", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space();
                return;
            }

            SnapshotView world = m_session.World;

            EditorGUILayout.LabelField(
                "会话：" + m_session.State
                + "（玩家 " + m_session.PlayerId
                + "，RTT " + (m_session.RttMs < 0 ? "?" : m_session.RttMs + "ms") + "）"
                + "　房间：" + (m_session.InRoom
                    ? m_session.CurrentRoom.RoomId + "（" + m_session.CurrentRoom.Members.Count
                      + "/" + m_session.CurrentRoom.Capacity + "）"
                    : "无")
                + "　世界：" + world.AliveCount + "/" + world.EntityCount
                + " 活着（帧 " + world.ServerTick + "）",
                EditorStyles.wordWrappedMiniLabel);

            string quest = "任务：（未开始 —— 先连接）";
            ConditionProgress progress;

            if (m_conditions != null && m_conditions.TryGetProgress(DemoConditionKey, out progress))
            {
                quest = "任务：" + progress + (progress.IsMet ? " ✅ 已达成" : "（还差 " + progress.Remaining + " 只）")
                    + "，达成回调 " + m_demoConditionMetCount + " 次";
            }

            EditorGUILayout.LabelField(
                quest
                + "　服务端事件：伤害 " + m_session.HitsReceived
                + "、死亡 " + m_session.DeathsReceived
                + "、掉落 " + m_session.DropsReceived
                + "　输入已发 " + m_session.InputsSent,
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space();
        }

        /// <summary>画状态区。</summary>
        private void DrawStatus()
        {
            EditorGUILayout.LabelField("状态", EditorStyles.boldLabel);

            if (m_session == null)
            {
                EditorGUILayout.LabelField("会话", "（未创建）");
                return;
            }

            EditorGUILayout.LabelField("状态", m_session.State.ToString());
            EditorGUILayout.LabelField("人话", m_session.Description);
            EditorGUILayout.LabelField("玩家 id", m_session.PlayerId.ToString());
            EditorGUILayout.LabelField("RTT", m_session.RttMs < 0 ? "（还没测到）" : m_session.RttMs + " ms");
            EditorGUILayout.LabelField("心跳", "已发 " + m_session.PingsSent + "，已收 " + m_session.PongsReceived);

            if (m_session.Ack != null)
            {
                EditorGUILayout.LabelField("服务端", m_session.Ack.ServerVersion
                    + "（协议 v" + m_session.Ack.ProtocolVersion + "，tick " + m_session.Ack.TickHz + "Hz）");
            }

            NBC.Framework.Net.TransportStats stats = m_session.TransportStats;
            EditorGUILayout.LabelField("传输", stats.ToString());

            if (!string.IsNullOrEmpty(m_session.FailureReason))
            {
                EditorGUILayout.HelpBox(m_session.FailureReason, MessageType.Error);
            }

            DrawRoom();
        }

        /// <summary>画房间区（席位表 + 最近一次被拒）。</summary>
        private void DrawRoom()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("房间与席位", EditorStyles.boldLabel);
            NBC.Protocol.RoomState room = m_session.CurrentRoom;

            if (room == null || string.IsNullOrEmpty(room.RoomId))
            {
                EditorGUILayout.LabelField("位置", "不在任何房间");
            }
            else
            {
                EditorGUILayout.LabelField("房号", room.RoomId);
                EditorGUILayout.LabelField("副本", room.DungeonId.ToString());
                EditorGUILayout.LabelField("席位", room.Members.Count + " / " + room.Capacity);
                EditorGUILayout.LabelField("阶段", room.Phase.ToString());

                for (int i = 0; i < room.Members.Count; i++)
                {
                    NBC.Protocol.RoomMember member = room.Members[i];

                    EditorGUILayout.LabelField(
                        "  · 玩家 " + member.PlayerId,
                        member.PlayerName + (member.IsHost ? "（房主）" : string.Empty)
                        + (member.Ready ? "（已准备）" : string.Empty));
                }
            }

            if (m_session.LastError != null)
            {
                // 被拒 ≠ 掉线：这里用 Warning 而不是 Error，提示"这个请求没成"
                EditorGUILayout.HelpBox(
                    "上次请求被拒（" + m_session.LastError.Code + "）：" + m_session.LastError.Message,
                    MessageType.Warning);
            }

            DrawWorld();
            DrawDrops();
            DrawInput();
        }

        /// <summary>
        /// 画掉落区（S7/S9）：**客户端只负责显示** —— 掷骰全在服务端。
        /// <para>⚠️ 狼的两条掉落概率是 50% / 30% ⇒ **"什么都没掉"有 35% 的概率**。
        /// 所以"打死了没看到奖励"有两种可能：真没掉，或者没打中那 65%。BOSS 是 10000（必掉）。</para>
        /// </summary>
        private void DrawDrops()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("掉落（服务端掷的）", EditorStyles.boldLabel);

            if (m_session == null)
            {
                EditorGUILayout.LabelField("累计", "0");
                return;
            }

            EditorGUILayout.LabelField("累计收到", m_session.DropsReceived + " 条");

            if (m_session.LastDrop != null)
            {
                EditorGUILayout.LabelField("最近一次",
                    "物品 " + m_session.LastDrop.ItemId + " × " + m_session.LastDrop.Count
                    + "（归玩家 " + m_session.LastDrop.WinnerPlayerId + "）");
            }
            else
            {
                EditorGUILayout.LabelField("最近一次", "（还没掉过）");
            }

            IReadOnlyList<NBC.Protocol.DropEvent> recent = m_session.RecentDrops;

            for (int i = 0; i < recent.Count; i++)
            {
                EditorGUILayout.LabelField(
                    "  · " + (i == 0 ? "最新" : i.ToString()),
                    "物品 " + recent[i].ItemId + " × " + recent[i].Count
                    + " → 玩家 " + recent[i].WinnerPlayerId);
            }

            if (m_session.DropsReceived == 0 && m_session.InRoom)
            {
                EditorGUILayout.HelpBox(
                    "还没掉过东西。注意：狼的掉落概率是 50% / 30% —— 「什么都没掉」有 35% 的概率；\n" +
                    "BOSS（狼王 6003）两条都是 10000（必掉），打它可以稳定看到掉落。",
                    MessageType.Info);
            }
        }

        /// <summary>画输入区（S5b：客户端只发**意图**，能走多远、打不打得到由服务端说了算）。</summary>
        private void DrawInput()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("输入（只发意图）", EditorStyles.boldLabel);

            bool canInput = m_session != null && m_session.IsOnline && m_session.InRoom;

            using (new EditorGUI.DisabledScope(!canInput))
            {
                // ---- M4-S1b：自动追击（负责人实测"目标不在范围内，难测"） ----
                m_autoChase = EditorGUILayout.Toggle("自动追击并攻击（调试用）", m_autoChase);

                if (m_autoChase)
                {
                    EditorGUILayout.HelpBox(
                        "会自动朝目标走过去（按服务端算的切比雪夫距离判断射程），进了 2000mm 就按一下普攻、"
                        + "再等冷却（15 帧）。目标死了会自动换最近的活怪。\n"
                        + "⚠️ 它**只是替你按键**：位置、伤害、掉落照样全由服务端算 —— 这是调试工具，不是客户端预测。",
                        MessageType.None);
                }

                EditorGUILayout.Space();
                m_inputX = EditorGUILayout.IntSlider("左右（-1000..1000）", m_inputX, -1000, 1000);
                m_inputY = EditorGUILayout.IntSlider("前后（-1000..1000）", m_inputY, -1000, 1000);
                m_inputContinuous = EditorGUILayout.Toggle("持续发送（模拟一直按着）", m_inputContinuous);
                m_targetEntityId = EditorGUILayout.IntField("目标实例编号（0 = 不打）", m_targetEntityId);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("发一次输入", GUILayout.Height(22f)))
                    {
                        m_session.SendInput(m_inputX, m_inputY, 0u, 0u, m_targetEntityId);
                    }

                    if (GUILayout.Button("普攻目标（第 0 位）", GUILayout.Height(22f)))
                    {
                        m_session.SendInput(m_inputX, m_inputY, 1u, 0u, m_targetEntityId);
                    }

                    if (GUILayout.Button("选最近的活怪当目标", GUILayout.Height(22f)))
                    {
                        m_targetEntityId = NearestAliveMonsterId();
                    }
                }

                // ⭐ S4-f：上面那一排**全是状态同步那条链**的（一行语义都没改）。
                //    锁步那条链的上行是另一个按钮 —— 两条链并存，按钮也分开，免得"按了不知道喂了谁"。
                bool lockstepInput = m_session != null && m_session.CanSendLockstepInput;

                using (new EditorGUI.DisabledScope(!lockstepInput))
                {
                    if (GUILayout.Button(
                            lockstepInput
                                ? "锁步：把普攻挂到下一条上行（第 0 位；下一帧发第 " + m_session.NextLockstepFrame + " 帧）"
                                : "锁步：把普攻挂到下一条上行（本局还没开局）",
                            GUILayout.Height(22f)))
                    {
                        m_lockstepPendingButtons |= 1u;
                    }
                }

                if (lockstepInput)
                {
                    EditorGUILayout.HelpBox(
                        "本局是**锁步对局**（服务端已开局）：上面两个滑条与「目标」会被**自动**上行成 "
                        + "`lockstep_input` —— **服务端每推进一帧、我发一条**（一帧一包，节拍是帧号不是时间）。\n"
                        + "⚠️ 一次编辑器 Tick **至多一条**：要是卡顿后一口气补了好几帧，中间那几帧**必然缺我的输入**"
                        + "（服务端会填默认并标 `Missing`）—— 这是锁步的固有代价。\n"
                        + "⚠️ 它和上面那排「发一次输入／持续发送」**不是同一条链**：那排喂状态同步（快照），"
                        + "这条喂帧同步（锁步）。两条链在服务端**并存**，各走各的。",
                        MessageType.Info);
                }

                DrawTargetHint();
            }

            if (!canInput)
            {
                EditorGUILayout.LabelField("提示", "要先「连接」并「加入房间」才能发输入");
            }

            EditorGUILayout.LabelField("已发输入", m_session == null ? "0" : m_session.InputsSent.ToString());

            // ⚠️ 这里**不看**「帧同步」那个勾选框：勾选框管的是"我**看**哪一份数据"，
            //    而上行管的是"我**参不参与**这一局" —— 只要服务端开了局，我这一席就该交输入，
            //    否则一换回快照模式，我的英雄在锁步世界里就永远被判「缺人」（而且没人会注意到）。
            if (m_session != null && m_session.Lockstep != null)
            {
                EditorGUILayout.LabelField("锁步上行",
                    "已发 " + m_session.LockstepInputsSent + " 条／被挡 " + m_session.LockstepInputsSkipped
                    + " 次；下一条打第 " + m_session.NextLockstepFrame + " 帧"
                    + "（提前量 " + NetSession.LockstepInputLeadFrames + " 帧）");
            }

            if (m_autoChase && canInput)
            {
                EditorGUILayout.LabelField("自动追击", "已按 " + m_autoAttackCount
                    + " 次普攻；冷却剩余 " + m_autoAttackCooldownTicks + " 帧"
                    + "；**它自己的方向** X=" + m_chaseInputX + " Y=" + m_chaseInputY
                    + "（滑条那对值归你，它不碰）");
            }
        }

        /// <summary>
        /// 把"目标在不在射程内"直接写出来（原来只有一个目标编号，得自己去算，
        /// 于是现象是"打了没反应"，而且不报错 —— 这正是负责人说的"目标不在范围内，难测"）。
        /// </summary>
        private void DrawTargetHint()
        {
            if (m_session == null || m_targetEntityId == 0)
            {
                return;
            }

            SnapshotView world = m_session.World;
            NBC.Protocol.EntitySnapshot mine = world.FindHero(m_session.PlayerId);
            NBC.Protocol.EntitySnapshot target = FindEntity(world, m_targetEntityId);

            if (mine == null || target == null)
            {
                EditorGUILayout.LabelField("目标", "快照里还没看到" + (mine == null ? "你自己" : "目标")
                    + "（等一张快照 / 先确认自己活着）");
                return;
            }

            // 判定用圆（平方比较）；显示用真实距离（四舍五入）—— 两者别再混用同一个数。
            bool inRange = BattleRules.WithinBasicAttackRange(
                mine.PosXMm, mine.PosZMm, target.PosXMm, target.PosZMm);

            int distance = BattleRules.DistanceMmRounded(
                mine.PosXMm, mine.PosZMm, target.PosXMm, target.PosZMm);

            // ⚠️ 出界时四舍五入可能把 2000.5 显示成 2000 ⇒ 会看到「2000mm ❌ 太远」这种自相矛盾的提示。
            //    既然判出界，就至少显示「射程 + 1」（与服务端日志同一处理）。
            if (!inRange && distance <= BattleRules.BasicAttackRangeMm)
            {
                distance = BattleRules.BasicAttackRangeMm + 1;
            }

            EditorGUILayout.LabelField("距离", distance + " mm"
                + (inRange ? "　✅ 在射程内（" + BattleRules.BasicAttackRangeMm + "mm）" : "　❌ 太远，打不到"));

            EditorGUILayout.LabelField("我 / 目标",
                "我 (" + mine.PosXMm + ", " + mine.PosZMm + ") → 目标 (" + target.PosXMm + ", " + target.PosZMm + ")"
                + "，目标 HP " + target.Hp + "/" + target.MaxHp);
        }

        /// <summary>
        /// 从本地世界副本里挑**离我最近的**活怪当目标（省得手填编号）。
        /// <para>⚠️ 原来叫 `FirstAliveMonsterId`：它取"快照里第一只活怪"，而快照顺序是**创建顺序**
        /// ⇒ 常常给你指到地图另一头的那只（负责人就撞上了"目标不在范围内，难测"）。</para>
        /// </summary>
        /// <returns>实例编号；没有就返回 0。</returns>
        private int NearestAliveMonsterId()
        {
            if (m_session == null)
            {
                return 0;
            }

            SnapshotView world = m_session.World;
            NBC.Protocol.EntitySnapshot mine = world.FindHero(m_session.PlayerId);

            int bestId = 0;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < world.Entities.Count; i++)
            {
                NBC.Protocol.EntitySnapshot e = world.Entities[i];

                if (e.Kind != 1 || !e.Alive)
                {
                    continue;
                }

                // 找不到自己的英雄时（快照还没到）退化成"第一只活怪"
                int distance = mine == null ? 0 : Chebyshev(mine, e);

                if (bestId == 0 || distance < bestDistance)
                {
                    bestId = e.EntityId;
                    bestDistance = distance;
                }
            }

            return bestId;
        }

        /// <summary>按实例编号找单位（找不到返回 null）。</summary>
        /// <param name="world">本地世界副本。</param>
        /// <param name="entityId">实例编号。</param>
        /// <returns>单位或 null。</returns>
        private static NBC.Protocol.EntitySnapshot FindEntity(SnapshotView world, int entityId)
        {
            if (world == null || entityId == 0)
            {
                return null;
            }

            for (int i = 0; i < world.Entities.Count; i++)
            {
                if (world.Entities[i].EntityId == entityId)
                {
                    return world.Entities[i];
                }
            }

            return null;
        }

        /// <summary>切比雪夫距离（毫米）——**与服务端 `DungeonBattle.DistanceMm` 同一种量法**。</summary>
        /// <param name="a">甲。</param>
        /// <param name="b">乙。</param>
        /// <returns>距离。</returns>
        private static int Chebyshev(NBC.Protocol.EntitySnapshot a, NBC.Protocol.EntitySnapshot b)
        {
            int dx = Math.Abs(a.PosXMm - b.PosXMm);
            int dz = Math.Abs(a.PosZMm - b.PosZMm);
            return dx > dz ? dx : dz;
        }

        /// <summary>画世界区（服务端快照的本地副本 —— M3 客户端的"画面数据"全在这儿）。</summary>
        private void DrawWorld()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("世界（服务端快照）", EditorStyles.boldLabel);

            SnapshotView world = m_session.World;

            EditorGUILayout.LabelField("服务端帧", world.ServerTick < 0 ? "（还没收到）" : world.ServerTick.ToString());
            EditorGUILayout.LabelField("单位", world.AliveCount + " / " + world.EntityCount + " 个活着");
            EditorGUILayout.LabelField("快照", "采纳 " + world.Applied + "，丢弃旧快照 " + world.StaleIgnored);

            for (int i = 0; i < world.Entities.Count; i++)
            {
                NBC.Protocol.EntitySnapshot e = world.Entities[i];

                EditorGUILayout.LabelField(
                    "  · 实例 " + e.EntityId + "（配置 " + e.ConfigId + "）",
                    (e.Alive ? "HP " + e.Hp + "/" + e.MaxHp : "已死亡")
                    + "　位置 (" + e.PosXMm + ", " + e.PosZMm + ") mm　朝向 " + e.FacingDeg + "°");
            }
        }

        /// <summary>
        /// 画 M4-S1 的任务条件区：**联机打死野狼，这里的进度会涨**。
        /// <para>这条链是：服务端 `DeathEvent` → `NetSession` → `ServerEventBridge` →
        /// `EventCenter` → `ConditionEventBridge` → `ConditionTracker`。
        /// 条件的定义是**造出来的**（不是配置表读的）—— 目的是让"接线对不对"肉眼可见，
        /// 不用先跑通整套配置表（真任务走的是同一个 `ConditionTracker`，见 `QuestRuntime`）。</para>
        /// <para>⚠️ 2026-09-26：目标**不再写死**。原来写的是"野狼 × 3"，而副本 1001 只刷 **2** 只
        /// （`Dungeon.csv` 的 `"6001,6001"`）⇒ **目标永远达不成**（负责人实测："没办法达成 3 只狼的任务目标"）。
        /// 现在默认**从快照里的真实阵容算**（本副本有哪种怪、有几只），也可以手填覆盖。</para>
        /// </summary>
        private void DrawQuestLoop()
        {
            if (m_session == null)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("任务条件（M4-S1：联机也能涨）", EditorStyles.boldLabel);

            EditorGUILayout.LabelField("条件", "击杀怪 " + m_demoMonsterIdInUse + " × " + m_demoKillTargetInUse
                + (m_demoKillTarget > 0 ? "（**手填**的目标）" : "（**自动取自本副本阵容**）")
                + "　—— 演示用；真任务从 `QuestCondition` 表读");

            if (m_conditions == null)
            {
                EditorGUILayout.LabelField("进度", "（还没开始 —— 先点「连接」）");
                return;
            }

            ConditionProgress progress;

            if (m_conditions.TryGetProgress(DemoConditionKey, out progress))
            {
                EditorGUILayout.LabelField("进度", progress.ToString()
                    + (progress.IsMet ? "　✅ 已达成" : "　还差 " + progress.Remaining + " 只"));
            }
            else
            {
                EditorGUILayout.LabelField("进度", "（还没登记 —— 进房后会按本副本阵容设目标）");
            }

            EditorGUILayout.LabelField("达成回调", "触发过 " + m_demoConditionMetCount + " 次");

            EditorGUILayout.LabelField("事件中心监听者",
                "MonsterDied = " + EventCenter.Instance.GetListenerCount(BattleEvents.MonsterDied)
                + "，DamageDealt = " + EventCenter.Instance.GetListenerCount(BattleEvents.DamageDealt));

            if (m_eventBridge != null)
            {
                EditorGUILayout.LabelField("桥收到",
                    "伤害 " + m_eventBridge.HitsForwarded
                    + "，怪死 " + m_eventBridge.MonstersDied
                    + "，英雄死 " + m_eventBridge.HeroesDied
                    + "，我的掉落 " + m_eventBridge.DropsClaimed
                    + "，别人的掉落 " + m_eventBridge.DropsOfOthers);
            }

            EditorGUILayout.LabelField("网络层收到",
                "伤害 " + m_session.HitsReceived
                + "，死亡 " + m_session.DeathsReceived
                + "，掉落 " + m_session.DropsReceived);

            using (new EditorGUILayout.HorizontalScope())
            {
                m_demoKillTarget = EditorGUILayout.IntField("目标只数覆盖（0 = 自动）", m_demoKillTarget);

                if (GUILayout.Button("按本副本阵容重设", GUILayout.Height(20f)))
                {
                    ResetDemoCondition(forceAuto: true);
                }
            }

            if (GUILayout.Button("重置演示条件进度（保留目标）", GUILayout.Height(22f)))
            {
                ResetDemoCondition(forceAuto: false);
            }

            if (!m_session.InRoom)
            {
                EditorGUILayout.HelpBox(
                    "还没进房。进房后打死的怪才会变成事件 —— 这就是 M3 收关时那条「任务闭环断口」，"
                    + "现在由 `ServerEventBridge` 接上了。",
                    MessageType.Info);
            }
        }

        /// <summary>
        /// M4-S1c：**真任务**区 —— 读 `Quest` / `QuestCondition` / `Reward` 三张表，
        /// 走的是与真面板**同一个视图模型**（`QuestPanelModel`）和**同一条链**
        /// （`QuestRuntime` → `ConditionTracker` → `ConditionEventBridge` → `EventCenter`，
        /// 联机时由 `ServerEventBridge` 把服务端事件喂进来）。
        /// </summary>
        private void DrawRealQuests()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("真任务（读配置表：接取 → 进度 → 交付 → 发奖）", EditorStyles.boldLabel);

            if (m_configError != null)
            {
                EditorGUILayout.HelpBox(m_configError, MessageType.Error);

                if (GUILayout.Button("重新加载配置表", GUILayout.Height(22f)))
                {
                    LoadConfigs();
                }

                return;
            }

            if (!m_configsReady || m_questModel == null)
            {
                EditorGUILayout.LabelField("配置表", "（加载中…）");
                return;
            }

            // §二十七：**一次遍历填四区** —— 分区（可接 / 进行中 / 可交付 / 已交付）由
            // `CopySections` 按**服务端权威状态**决定（服务端没说才退回本地并标「（本地预测）」）。
            // ⚠️ 不要在窗口里自己"从可接删掉、再加到进行中"：漏掉"加"的那一步，任务就会
            //    从界面上**消失且不报错** —— 这正是 `CopySections` 里那个 `placed` 集合要防的。
            m_questModel.CopySections(m_offerRows, m_trackingRows, m_readyRows, m_doneRows);

            DrawQuestSection("可接取", m_offerRows);
            DrawQuestSection("进行中", m_trackingRows);
            DrawQuestSection("可交付", m_readyRows);
            DrawQuestSection("已交付", m_doneRows);

            if (!string.IsNullOrEmpty(m_questModel.LastMessage))
            {
                EditorGUILayout.LabelField("消息", m_questModel.LastMessage);
            }

            if (m_questSession != null)
            {
                EditorGUILayout.LabelField("状态", m_questSession.Quests.DescribeActive());
            }

            if (m_rewards.Count > 0)
            {
                EditorGUILayout.LabelField("发奖记录", m_rewards[0] + (m_rewards.Count > 1 ? "（共 " + m_rewards.Count + " 条）" : string.Empty));
            }

            EditorGUILayout.HelpBox(
                "进度**来自联机事件**：服务端死亡/掉落 → `ServerEventBridge` → `EventCenter` → 条件桥 → 条件系统。\n"
                + "所以：先「连接」并「加入房间」，再去打怪（或勾「自动追击并攻击」），回来这里看进度与交付。",
                MessageType.None);
        }

        /// <summary>
        /// M4-S2：**成就**区。
        /// <para>和真任务区是**同一条链** —— 成就与任务共用同一个 `ConditionTracker`，
        /// 所以打怪时两条一起涨；差别只在"成都不清零、不用接、条件一齐当场解锁"。</para>
        /// <para>⚠️ 成就是**跨局累计**的：它用的是 `ConditionTracker` 的持久进度，
        /// 窗口重开、会话重建都不会把它清零（这正是成就与任务最本质的区别）。</para>
        /// </summary>
        private void DrawRealAchievements()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("成就（与任务共用条件系统；条件一齐当场解锁发奖）", EditorStyles.boldLabel);

            if (m_questSession == null)
            {
                EditorGUILayout.LabelField("成就", "（配置表还没就绪，或这一局没装成就）");
                return;
            }

            AchievementRuntime achievements = m_questSession.Achievements;

            if (achievements == null)
            {
                EditorGUILayout.HelpBox(
                    "这一局**没有成就**：`AchievementConfig` 没加载。\n"
                    + "踩一步就修好：菜单 「Tools/NBC/配置表/导入 TSV → ScriptableObject」"
                    + "（`Achievement.tsv` 已经由 ConfigKit 生成好了），然后点这里的「重新加载配置表」。",
                    MessageType.Warning);

                if (GUILayout.Button("重新加载配置表", GUILayout.Height(22f)))
                {
                    LoadConfigs();
                }

                return;
            }

            achievements.CopyTrackings(m_achievementRows);

            EditorGUILayout.LabelField("已解锁",
                achievements.UnlockedCount + " / " + achievements.AchievementCount);

            for (int i = 0; i < m_achievementRows.Count; i++)
            {
                AchievementTracking row = m_achievementRows[i];

                EditorGUILayout.LabelField(
                    "　" + (row.IsUnlocked ? "🏆 " : "　") + row.Name,
                    "[" + row.AchievementId + "] " + row.Description);

                for (int c = 0; c < row.Conditions.Count; c++)
                {
                    EditorGUILayout.LabelField("　　　　",
                        row.Conditions[c].Description + "　" + row.Conditions[c].Current
                        + "/" + row.Conditions[c].Required + (row.Conditions[c].IsMet ? "　✅" : string.Empty));
                }
            }

            if (!string.IsNullOrEmpty(achievements.LastProblem))
            {
                EditorGUILayout.HelpBox(achievements.LastProblem, MessageType.Error);
            }
        }

        /// <summary>画一行任务（标题 + 详情 + 一个动作按钮）。</summary>
        /// <param name="row">显示行。</param>
        /// <summary>画一个区（标题 + 该区的行）。§二十七 起四个区共用这一段。</summary>
        /// <param name="title">区名。</param>
        /// <param name="rows">该区的行。</param>
        private void DrawQuestSection(string title, List<QuestRow> rows)
        {
            EditorGUILayout.LabelField(title, rows.Count == 0 ? "（没有）" : rows.Count + " 个");

            for (int i = 0; i < rows.Count; i++)
            {
                DrawQuestRow(rows[i]);
            }
        }

        private void DrawQuestRow(QuestRow row)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("　" + row.Title, row.Detail);

                if (GUILayout.Button(row.ActionLabel + "（" + row.QuestId + "）", GUILayout.Width(110f)))
                {
                    RunQuestAction(row.ActionName);
                }
            }
        }

        /// <summary>执行一个任务动作（接取 / 交付）—— **走真面板同一个入口** `QuestPanelModel.HandleAction`。</summary>
        /// <param name="actionName">动作名（`Accept_<id>` / `Submit_<id>`）。</param>
        private void RunQuestAction(string actionName)
        {
            if (m_questModel == null)
            {
                return;
            }

            bool handled = m_questModel.HandleAction(actionName);

            AddLog((handled ? "任务动作：" : "任务动作被拒：") + actionName
                + "　" + m_questModel.LastMessage);
        }

        /// <summary>画日志区（**固定高度的内层滚动**，见 `OnGUI` 里"整窗滚动"的说明）。</summary>
        private void DrawLog()
        {
            EditorGUILayout.LabelField("日志（最近 " + MaxLogLines + " 条；框内可滚轮）", EditorStyles.boldLabel);

            // ⚠️ 原来是 `GUILayout.ExpandHeight(true)` —— 放进**外层滚动视图**之后那个写法会失去意义
            //    （外层高度是无界的）⇒ 日志框塌成 0 高、一条都看不见。改成固定高度：
            //    外层管"整窗翻页"、内层管"日志自己翻"，各管一段，互不打架。
            m_logScroll = EditorGUILayout.BeginScrollView(m_logScroll, GUILayout.Height(LogBoxHeight));

            for (int i = 0; i < m_log.Count; i++)
            {
                EditorGUILayout.LabelField(m_log[i], EditorStyles.miniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>连上去：造传输 + 造会话 + 挂事件。</summary>
        private void ConnectSession()
        {
            CloseSession("重连前先断开");

            m_transport = new TcpTransport();

            // R004：在真实传输层外面包一层字节统计（装饰器）。HUD 与这个窗口都读同一份数据，
            //      所以这里只**包**、不自己算带宽（否则两边数字会不一致）。
            ITransport counting = new CountingTransport(m_transport);
            NetRuntimeStats.Reset();

            // ⚠️ 第 7、8 个参数就是 M4-S3 的账号 / 密码（留空 = 游客，行为与 M3 一字不差）。
            //    密码在这里交给 `NetSession`，它算完摘要就把明文丢掉 —— 见那个字段的说明。
            m_session = new NetSession(
                counting,
                m_playerName,
                "unity-editor/" + Application.unityVersion,
                NetSession.DefaultHeartbeatIntervalMs,
                NetSession.DefaultHeartbeatTimeoutMs,
                NetSession.DefaultHandshakeTimeoutMs,
                m_account,
                m_password);

            // §二十六：会话刚建好 ⇒ 把任务面板接上（`m_questModel` 可能还没就绪，见那个方法的守卫）
            WireQuestPanelToServer();

            m_session.Note += line => AddLog(line);
            m_session.StateChanged += state => AddLog("状态 → " + state);
            m_session.HandshakeCompleted += ack =>
                AddLog("握手完成：玩家 " + ack.PlayerId +
                       "，昵称 " + ack.Nickname +
                       "，服务端 " + ack.ServerVersion);

                // R004：握手成功才算「在线」，模式跟着窗口上的锁步开关走
                NetRuntimeStats.SetMode(m_lockstepMode ? NetSyncMode.Lockstep : NetSyncMode.StateSync);
            m_session.RttUpdated += rtt => AddLog("RTT " + rtt + "ms");

            // ------------------------------------------------------------------
            //  M4-S3 收口：**服务端权威进度**（§23）
            // ------------------------------------------------------------------
            //  ⚠️ 这一段的意义不是"多打一行日志"，而是**把"谁是权威"变成看得见的东西**：
            //    在此之前进度只有客户端自己算的一份（= 预测），界面上看不出真假；
            //    现在服务端把它的账发下来，这里**逐条念出来**（含每个条件的 当前/需求/是否达成）。
            //  ⚠️ 顺带一条边界：**只有登录玩家才有权威进度**（游客没有档案 ⇒ 服务端不追踪、也不下发）。
            //     所以用游客连接时这里**一条都不该出现** —— 那不是 bug，是设计（见 `Docs\27` §二十）。
            m_session.ProgressReceived += progress =>
            {
                AddLog("=== 服务端权威进度（tick " + progress.ServerTick + "，" +
                       progress.Entries.Count + " 条）===");

                for (int i = 0; i < progress.Entries.Count; i++)
                {
                    ConditionProgressEntry entry = progress.Entries[i];

                    AddLog("  [权威] 条件 " + entry.ConditionKey + "：" +
                           entry.Current + "/" + entry.Required +
                           (entry.Met ? "（**已达成**）" : string.Empty));
                }
            };

            m_lastUpdateTime = EditorApplication.timeSinceStartup;
            AddLog("=== 连接 " + m_host + ":" + m_port +
                   (string.IsNullOrEmpty(m_account)
                       ? "（游客，玩家名 " + m_playerName + "）"
                       : "（账号 " + m_account + "）") + " ===");

            m_session.Connect(m_host, m_port);

            StartQuestLoopDemo();
        }

        /// <summary>
        /// M4-S1：接上"联机战斗 → 任务条件"这条链。
        /// <para>⚠️ 条件系统与它的桥**只建一次**（它们挂在全局 `EventCenter` 上）——
        /// 每次连接都建一份的话，同一个事件会被喂进条件系统两次，进度翻倍且看不出来。
        /// 每次连接只重建"跟当前会话绑定"的那座 `ServerEventBridge`。</para>
        /// <para>⚠️ **条件本身不在这里登记**：目标只数要"本副本有几只怪"，那要等第一张快照
        /// （见 `EnsureDemoConditionRegistered`）。原来写死"× 3"、而副本只有 2 只狼 ⇒ 永远达不成。</para>
        /// </summary>
        private void StartQuestLoopDemo()
        {
            if (m_conditions == null)
            {
                m_conditions = new ConditionTracker(new InMemoryConditionProgressStore());

                m_conditionBridge = new ConditionEventBridge(m_conditions);
                m_conditionBridge.Bind<MonsterDiedPayload>(BattleEvents.MonsterDied,
                    EConditionEvent.KillMonster, payload => payload.MonsterConfigId);

                m_conditions.ConditionMet += (key, progress) =>
                {
                    m_demoConditionMetCount++;
                    AddLog("条件达成：" + progress + "（条件编号 " + key + "）");
                };
            }

            m_eventBridge = new ServerEventBridge(m_session);
            m_demoConditionRegistered = false;      // 换了会话 → 等第一张快照重新登记
        }

        /// <summary>
        /// 从**快照里的真实阵容**算出演示条件的目标（"本副本有哪种怪、有几只"）。
        /// <para>算法：按 `config_id` 分组数怪，**优先 `DemoPreferredMonsterId`（6001 野狼）**；
        /// 本副本没有它就取"**出现次数最多的那种怪**"。后者顺带解决了"哪个是 BOSS"这个问题 ——
        /// BOSS 只有一只，而小怪是一批（副本 1001 → 6001×2；1002 → 6002×3），
        /// 所以**不需要**给快照再加一个 `is_boss` 字段。平局按编号升序打破（确定性）。</para>
        /// </summary>
        /// <param name="monsterId">算出来的怪编号（算不出时为 0）。</param>
        /// <param name="count">算出来的只数（算不出时为 0）。</param>
        private void ResolveDemoTarget(out int monsterId, out int count)
        {
            monsterId = 0;
            count = 0;

            if (m_session == null)
            {
                return;
            }

            SnapshotView world = m_session.World;
            var counts = new Dictionary<int, int>();

            for (int i = 0; i < world.Entities.Count; i++)
            {
                NBC.Protocol.EntitySnapshot e = world.Entities[i];

                if (e.Kind != 1)
                {
                    continue;
                }

                int seen;
                counts.TryGetValue(e.ConfigId, out seen);
                counts[e.ConfigId] = seen + 1;
            }

            if (counts.Count == 0)
            {
                return;
            }

            int preferred;
            if (counts.TryGetValue(DemoPreferredMonsterId, out preferred))
            {
                monsterId = DemoPreferredMonsterId;
                count = preferred;
                return;
            }

            foreach (KeyValuePair<int, int> pair in counts)
            {
                bool better = pair.Value > count
                              || (pair.Value == count && monsterId != 0 && pair.Key < monsterId);

                if (better)
                {
                    monsterId = pair.Key;
                    count = pair.Value;
                }
            }
        }

        /// <summary>
        /// 第一张快照到了之后登记演示条件（**目标只数从阵容算**，不写死）。
        /// <para>每帧调一次；同一个房间登记过就直接返回。**换房间（换副本）会重新登记** ——
        /// 否则进了蜘蛛洞（1002）还在要求"击杀野狼"，又变成达不成的目标。</para>
        /// </summary>
        private void EnsureDemoConditionRegistered()
        {
            if (m_conditions == null || m_session == null || !m_session.InRoom)
            {
                return;
            }

            string roomId = m_session.CurrentRoom == null ? string.Empty : m_session.CurrentRoom.RoomId;

            if (m_demoConditionRegistered && string.Equals(roomId, m_demoConditionRoomId, StringComparison.Ordinal))
            {
                return;
            }

            int monsterId;
            int count;

            if (m_demoKillTarget > 0)
            {
                // 手填覆盖：怪还是按阵容挑，只数听你的
                ResolveDemoTarget(out monsterId, out count);
                count = m_demoKillTarget;
            }
            else
            {
                ResolveDemoTarget(out monsterId, out count);
            }

            if (count <= 0)
            {
                return;     // 还没收到快照（或本副本没有普通怪）→ 下一帧再看
            }

            m_conditions.Unregister(DemoConditionKey);
            m_conditions.Register(DemoConditionKey,
                new ConditionDef(EConditionEvent.KillMonster, monsterId, count), true);

            m_demoMonsterIdInUse = monsterId;
            m_demoKillTargetInUse = count;
            m_demoConditionRegistered = true;
            m_demoConditionRoomId = roomId;

            AddLog("演示条件已登记：击杀怪 " + monsterId + " × " + count
                + (m_demoKillTarget > 0 ? "（手填）" : "（按本副本阵容自动算）"));
        }

        /// <summary>重设演示条件（按钮用）。</summary>
        /// <param name="forceAuto">true = 清掉手填值、改回"按阵容自动"。</param>
        private void ResetDemoCondition(bool forceAuto)
        {
            if (m_conditions == null)
            {
                return;
            }

            if (forceAuto)
            {
                m_demoKillTarget = 0;
            }

            m_conditions.Unregister(DemoConditionKey);
            m_demoConditionRegistered = false;
            m_demoConditionMetCount = 0;

            EnsureDemoConditionRegistered();

            AddLog("演示条件已重设：怪 " + m_demoMonsterIdInUse + " × " + m_demoKillTargetInUse
                + (m_demoKillTarget > 0 ? "（手填）" : "（自动）"));
        }

        /// <summary>断开并释放。</summary>
        /// <param name="reason">原因（写进日志）。</param>
        private void CloseSession(string reason)
        {
            if (m_session == null)
            {
                return;
            }

            AddLog("=== " + reason + " ===");

            // 先退订桥（它订阅的是这个会话的事件），再 Dispose 会话
            if (m_eventBridge != null)
            {
                m_eventBridge.Dispose();
                m_eventBridge = null;
            }

            m_session.Dispose();
            m_session = null;
            m_transport = null;

            // R004：断开就把同源统清零（模式回「离线」，带宽归零，帧号清成未知）
            NetRuntimeStats.Reset();

            // ⚠️ 换会话就把上行游标清掉：新会话的 `MatchSerial` 又从 0 数起（第一次开局还是 1），
            //    不清的话它会与上一局的编号撞上 —— 见 `PumpLockstepInput` 里那段说明（重连后不动）。
            ResetLockstepUplinkState();

            // ⚠️ 断开就把表现层那份清掉：不清的话下一局的第 0 帧可能比上一局的末帧小（画面停住）
            if (m_feed != null)
            {
                m_feed.Clear();
            }

            m_binder = null;        // 场景里那个组件还活着（播放模式），但窗口不再显示它的状态
        }

        /// <summary>加一行日志（超出上限就丢最旧的）。</summary>
        /// <param name="line">内容。</param>
        private void AddLog(string line)
        {
            m_log.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line);

            while (m_log.Count > MaxLogLines)
            {
                m_log.RemoveAt(0);
            }
        }
    }
}
