// 这个文件里有：
//   NetDebugWindow（类）：编辑器里的网络联调窗口，负责连接/断开、看会话与快照、驱动锁步上行，并把任务与成就摊开显示
//   EditorQuestRewardSink（类）：发奖接缝的编辑器实现，把一次发奖写成窗口里的一行日志

using System;
using System.Collections.Generic;
using System.Text;
using NBC.Framework;
using NBC.Framework.Net;
using NBC.Framework.Net.Adapter;
using NBC.Game.Achievement;
using NBC.Game.Battle;
using NBC.Game.Config;
using NBC.Game.GameFlow;
using NBC.Game.Net;
using NBC.Game.Quest;
using NBC.Game.UI;
using NBC.Game.View;
using NBC.Protocol;
using NBC.Shared.Battle;
using NBC.Shared.Condition;
using NBC.Shared.Net;
using NBC.Shared.Sim;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    // 主要功能：编辑器里常驻的网络联调窗口；连接服务端后持续显示会话、房间、快照、掉落与输入，并驱动真任务、成就与锁步上行。
    // 主要数据成员：m_session（会话）、m_feed（表现层数据源）、m_questSession（真任务装配）、m_conditions（演示条件）。
    // 主要外部方法：Open（菜单入口；本文件对外只有这一个入口）。
    // 谁在用它：只在编辑器里用；菜单 Tools/NBC/网络/网络调试窗口 打开。
    public sealed class NetDebugWindow : EditorWindow
    {
        // 单次推进的最大毫秒数；编辑器卡顿时宁可让会话时钟走慢，也不许一次喂太大把心跳判成超时。
        private const int MaxDeltaMs = 250;

        // 日志区最多保留的行数；超出丢最旧的。
        private const int MaxLogLines = 200;

        // 服务端地址；默认验收就是本机。
        [SerializeField] private string m_host = "127.0.0.1";

        // 端口；9000 与服务端 appsettings.json 的 ListenPort 一致 —— 早期用代码兜底值 7777 碰巧能连，配置生效后就对不上，别让两边各写默认值。
        [SerializeField] private int m_port = 9000;

        // 玩家名；游客用，登录成功后显示服务端给的档案昵称。
        [SerializeField] private string m_playerName = "剑士";

        // 登录名；留空 = 游客。会被 Unity 序列化进窗口状态，填过之后想回游客要手动清空。
        [SerializeField] private string m_account = "";

        // 明文密码；只在点「连接」那一瞬间用一下，但它会被序列化进编辑器窗口状态 —— 演示级取舍，真实项目应每次输入、只留内存。
        [SerializeField] private string m_password = "";

        // 想进的副本编号；Dungeon 表主键，建副本由服务端做。
        [SerializeField] private int m_dungeonId = 1001;

        // 指定房号；留空 = 让服务端按副本安排一个。
        [SerializeField] private string m_roomId = "";

        // 输入：左右轴；-1000..1000（满量程代表一个方向）。
        [SerializeField] private int m_inputX;

        // 输入：前后轴；-1000..1000。
        [SerializeField] private int m_inputY;

        // 勾上就每帧把当前输入发出去；模拟一直按着方向。
        [SerializeField] private bool m_inputContinuous;

        // 当前目标实例编号；0 = 没目标。
        [SerializeField] private int m_targetEntityId;

        // 自动追击并攻击开关；只是替你按键，位置、伤害、掉落仍全由服务端算，不是客户端预测。
        [SerializeField] private bool m_autoChase;

        // 自动追击：距离下一次能按键还剩几帧；与服务端普攻冷却同源。
        private int m_autoAttackCooldownTicks;

        // 自动追击：一共按了多少次普攻；用来确认真的按出去了。
        private int m_autoAttackCount;

        // 自动追击自己的左右方向；不能借用滑条的值 —— 那对值同时喂状态同步与锁步上行，改它会把玩家的方向悄悄改掉。
        private int m_chaseInputX;

        // 自动追击自己的前后方向；与滑条分开的理由见 m_chaseInputX。
        private int m_chaseInputY;

        // 当前会话；没连时为 null。
        private NetSession m_session;

        // 帧同步（锁步）开关；只决定客户端喂给表现层的是服务端快照还是锁步世界，不改变服务端（那边两条链并存）。
        private bool m_lockstepMode;

        // 锁步上行：上一次为哪一帧采集过意图；用帧号当节拍，一帧一包，多发会被服务端当重复丢掉。
        private int m_lastLockstepInputFrame = -1;

        // 锁步上行：上一次采集时是哪一局；换局要把游标清零，见 PumpLockstepInput。
        private int m_lockstepSeenMatchSerial = -1;

        // 锁步上行：下一条要带的按键位；普攻按一下置第 0 位，发出去就清掉。
        private uint m_lockstepPendingButtons;

        // 表现层数据源；按开关把服务端快照或锁步世界喂进同一个 SnapshotView，选源规则在 ViewSourceDecision。
        private PresentationFeed m_feed;

        // 场景里的最小视图接管；只有点过按钮且在播放模式时才有。
        private SnapshotSceneBinder m_binder;

        // 服务端事件 → 事件中心的桥；联机战斗能推进任务就靠它。
        private ServerEventBridge m_eventBridge;

        // 演示用的条件系统；登记「击杀指定怪 × N」。
        private ConditionTracker m_conditions;

        // 事件中心 → 条件系统的桥；任务模块不认识网络，靠它转接。
        private ConditionEventBridge m_conditionBridge;

        // 演示条件的编号；真项目里是 QuestCondition 表主键，取值 5001。
        private const int DemoConditionKey = 5001;

        // 演示条件优先挑哪种怪；6001 是野狼，本副本没有它就退化成出现次数最多的普通怪。
        private const int DemoPreferredMonsterId = 6001;

        // 手填的目标只数；0 = 自动，从快照里的真实阵容算。
        [SerializeField] private int m_demoKillTarget;

        // 演示条件是否已登记；登记要等第一张快照拿到阵容，换房间还要重新登记。
        private bool m_demoConditionRegistered;

        // 上次登记演示条件时的房号；换房间要按新阵容重新登记。
        private string m_demoConditionRoomId = string.Empty;

        // 实际用的怪编号；显示用，自动模式下来自快照。
        private int m_demoMonsterIdInUse = DemoPreferredMonsterId;

        // 实际用的目标只数；显示用。
        private int m_demoKillTargetInUse;

        // 演示条件达成回调的触发次数。
        private int m_demoConditionMetCount;


        // 配置表是否就绪；就绪后才装真任务。
        private bool m_configsReady;

        // 配置表加载失败的原因；就绪前显示在界面上。
        private string m_configError;

        // 真任务的会话；复用现成的 BattleSession 装配，窗口只用它的 Quests / Conditions，本地那个 BattleWorld 不参与联机 —— 再写一遍接线就是两处实现，迟早不一致。
        private BattleSession m_questSession;

        // 任务面板已经接到哪个联网会话上；换会话要重接，见 WireQuestPanelToServer。
        private NetSession m_questWiredSession;

        // 把「跟随权威」施加到本地任务运行时的出口；只改状态，不发奖、不重置、不提示。
        private RuntimeQuestFollowSink m_questFollowSink;

        // 真面板用的同一个视图模型；窗口与面板一套逻辑。
        private QuestPanelModel m_questModel;

        // 可接取任务的显示行；每帧重填，避免每帧分配新列表。
        private readonly List<QuestRow> m_offerRows = new List<QuestRow>();

        // 进行中任务的显示行。
        private readonly List<QuestRow> m_trackingRows = new List<QuestRow>();

        // 可交付区（服务端说 Completed）的显示行。
        private readonly List<QuestRow> m_readyRows = new List<QuestRow>();

        // 已交付区（服务端说 Submitted）的显示行。
        private readonly List<QuestRow> m_doneRows = new List<QuestRow>();

        // 成就的追踪视图；每帧重填，避免每帧分配新列表。
        private readonly List<AchievementTracking> m_achievementRows = new List<AchievementTracking>();

        // 发奖记录；新的在前，最多 20 条。
        private readonly List<string> m_rewards = new List<string>();

        // 传输；图省事由窗口自己持有，方便 Dispose。
        private TcpTransport m_transport;

        // 界面上的日志；新的在下面。
        private readonly List<string> m_log = new List<string>();

        // 上次推进的时刻；算 delta 用，单位秒（编辑器启动以来的时间）。
        private double m_lastUpdateTime;

        // 日志框的高度；像素 —— 放进整窗滚动后必须给固定高度，否则会塌成 0。
        private const float LogBoxHeight = 170f;

        // 日志滚动位置。
        private Vector2 m_logScroll;

        // 整窗滚动位置；原来的窗口没有滚动容器，下半节被裁掉且翻不到。
        private Vector2 m_scroll;

        // 功能：打开（或聚焦）网络调试窗口，并给一个最小尺寸。
        // 入参：无
        // 返回：无
        // 步骤：GetWindow 已存在就复用、不存在就新建，随后设最小尺寸并 Show。
        // 上游：菜单 Tools/NBC/网络/网络调试窗口
        // 下游：无
        [MenuItem("Tools/NBC/网络/网络调试窗口")]
        public static void Open()
        {
            NetDebugWindow window = GetWindow<NetDebugWindow>("NBC 网络调试");
            window.minSize = new Vector2(420f, 380f);
            window.Show();
        }

        // 功能：窗口启用时挂上编辑器帧回调与成就事件监听，并开始加载配置表。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 记下当前时刻，供 Tick 算第一次 delta
        //   2) 订阅 update —— 窗口靠它推进会话并重绘
        //   3) 订阅成就解锁/失败事件；成就没有玩家操作，不写日志就完全没有痕迹
        //   4) 加载配置表，就绪后装真任务
        // 上游：Unity 编辑器在窗口启用时调用
        // 下游：Tick / OnAchievementUnlocked / OnAchievementUnlockFailed / LoadConfigs
        private void OnEnable()
        {
            m_lastUpdateTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;

            EventCenter.Instance.AddEventListener<AchievementUnlockedPayload>(
                AchievementEvents.Unlocked, OnAchievementUnlocked);
            EventCenter.Instance.AddEventListener<AchievementUnlockFailedPayload>(
                AchievementEvents.UnlockFailed, OnAchievementUnlockFailed);

            LoadConfigs();
        }

        // 功能：窗口禁用时摘掉所有回调并断开连接。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 退订 update 与成就事件
        //   2) 关闭会话；注意窗口关闭、进播放模式触发域重载都会断开连接 —— 客户端会话本该挂在游戏流程里，窗口只是能点着看的调试入口
        //   3) 释放两座桥与真任务会话：它们的回调挂在全局事件中心上，不退订就是窗口关了回调还在跑
        //   4) 清空引用，下次启用不会拿到已释放的对象
        // 上游：Unity 编辑器在窗口关闭 / 域重载时调用
        // 下游：CloseSession / ConditionEventBridge.Dispose / BattleSession.Dispose
        private void OnDisable()
        {
            EditorApplication.update -= Tick;

            EventCenter.Instance.RemoveEventListener<AchievementUnlockedPayload>(
                AchievementEvents.Unlocked, OnAchievementUnlocked);
            EventCenter.Instance.RemoveEventListener<AchievementUnlockFailedPayload>(
                AchievementEvents.UnlockFailed, OnAchievementUnlockFailed);

            CloseSession("窗口关闭");

            if (m_conditionBridge != null)
            {
                m_conditionBridge.Dispose();
                m_conditionBridge = null;
            }

            m_conditions = null;

            if (m_questSession != null)
            {
                m_questSession.Dispose();
                m_questSession = null;
                m_questModel = null;
            }
        }

        // 功能：把配置表喂进 ConfigMgr，加载完成后回调装一局真任务。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先清空就绪标记与错误原因，重试时不带旧错误
        //   2) 换用 EditorConfigSource：编辑器里用 AssetDatabase 直读，不走 YooAsset（没进 Play，资源层没装配）
        //   3) GameTables.PreloadAll：成功走 OnConfigsReady，失败把原因显示到界面并记日志
        // 上游：OnEnable / 界面上的「重新加载配置表」
        // 下游：OnConfigsReady
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

        // 功能：配置表就绪后装配一局真任务（条件 → 任务 → 发奖那条链）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 英雄编号从 Hero 表取第一个，不在窗口里写死；联机打什么怪仍由服务端决定
        //   2) 用 BattleSession.FromConfigMgr 复用现成的装配，发奖口换成写日志的实现
        //   3) 用同一个 QuestPanelModel 建视图模型（真面板也用它）
        //   4) 把面板的接取/交付接到联网会话上
        // 上游：LoadConfigs 的加载完成回调
        // 下游：FirstHeroId / WireQuestPanelToServer
        private void OnConfigsReady()
        {
            m_configsReady = true;

            int heroId = FirstHeroId();

            m_questSession = BattleSession.FromConfigMgr(heroId, new EditorQuestRewardSink(this));
            m_questModel = new QuestPanelModel(m_questSession.Quests);

            WireQuestPanelToServer();

            AddLog("配置表就绪：真任务已装好（英雄 " + heroId + "）");
        }

        // 功能：把任务面板接到当前联网会话：接取/交付发给服务端，并接收服务端的任务状态与拒绝原因。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没有视图模型、没有会话、或已经接在同一个会话上就直接返回（幂等；重连换了会话会自动重接）
        //   2) 装上 SendAction：之后 Accept/Submit 不再本地改状态，等服务端确认
        //   3) 装上 StateAuthority：面板据此把任务分区；服务端没说时退回本地判断并标「（本地预测）」
        //   4) 装上跟随出口：本地状态机只跟随权威改状态，不发奖、不重置、不提示
        //   5) 收到 quest_state 就清掉待确认，并让本地状态机跟随
        //   6) 收到任务类错误就落定所有待确认，并把服务端原文写进日志
        // 上游：OnConfigsReady / ConnectSession
        // 下游：SendQuestAction / FollowLocalQuestState / FailAllPending
        private void WireQuestPanelToServer()
        {
            if (m_questModel == null || m_session == null || ReferenceEquals(m_questWiredSession, m_session))
            {
                return;
            }

            m_questWiredSession = m_session;

            m_questModel.SendAction = (questId, action) => m_session.SendQuestAction(questId, action);

            m_questModel.StateAuthority = new NetSessionQuestStateAuthority(m_session);

            m_questFollowSink = new RuntimeQuestFollowSink(m_questSession.Quests);

            m_session.QuestStateReceived += sync =>
            {
                for (int i = 0; i < sync.Entries.Count; i++)
                {
                    m_questModel.ClearPending(sync.Entries[i].QuestId);
                }

                FollowLocalQuestState(sync);
            };

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

        // 功能：把服务端的全量任务状态交给状态机，让本地任务状态跟随权威。
        // 入参：sync 服务端发来的全量任务状态；为 null 时直接返回
        // 返回：无
        // 步骤：
        //   1) 会话、跟随出口、同步三者缺一即返回
        //   2) 把同步里的「任务编号 + 服务端状态」转成分区规划的输入
        //   3) 用当前本地状态与待确认集合做规划；规划为空就返回
        //   4) 施加规划，真的改了才记一行日志（只改状态，不发奖、不重置、不提示）
        // 上游：WireQuestPanelToServer 订阅的 QuestStateReceived
        // 下游：QuestStateReconciler.Plan / QuestStateReconciler.Apply
        private void FollowLocalQuestState(QuestStateSync sync)
        {
            if (m_questSession == null || m_questFollowSink == null || sync == null)
            {
                return;
            }

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

        // 功能：取 Hero 表里第一个英雄的编号。
        // 入参：无
        // 返回：英雄编号；Hero 表没加载或没有行时退回 1001（服务端固定的那个）。
        // 步骤：表没加载就直接兜底，加载了取第一行，空表同样兜底。
        // 上游：OnConfigsReady
        // 下游：无
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

        // 功能：把一份奖励记成界面上看得见的一行，并写进日志。
        // 入参：questId 任务编号；reward 奖励内容（经验、金币、物品编号与数量）
        // 返回：无
        // 步骤：
        //   1) 拼一行「任务编号 → 经验 / 金币 / 物品」
        //   2) 插到发奖记录最前面
        //   3) 超过 20 条就丢最旧的
        //   4) 同一条写进日志
        // 上游：EditorQuestRewardSink.Grant（真面板走同一个 IQuestRewardSink 接缝）
        // 下游：AddLog
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

        // 功能：成就解锁时记一行日志。
        // 入参：payload 解锁载荷（成就编号、奖励编号）
        // 返回：无
        // 步骤：直接写日志。
        //   注意：成就没有玩家操作，不写日志就完全没有痕迹 —— 玩家反馈「我明明打了 10 只狼」时，这里是唯一证据。
        // 上游：EventCenter 的 AchievementEvents.Unlocked（OnEnable 订阅）
        // 下游：AddLog
        private void OnAchievementUnlocked(AchievementUnlockedPayload payload)
        {
            AddLog("🏆 成就解锁：" + payload.AchievementId + "（奖励 " + payload.RewardId + " 已发）");
        }

        // 功能：成就该解锁却没解开时记一行显眼的日志。
        // 入参：payload 失败载荷（成就编号、失败原因）
        // 返回：无
        // 步骤：直接写日志；配置问题不记下来就是一条无声的悬案。
        // 上游：EventCenter 的 AchievementEvents.UnlockFailed（OnEnable 订阅）
        // 下游：AddLog
        private void OnAchievementUnlockFailed(AchievementUnlockFailedPayload payload)
        {
            AddLog("⚠️ 成就 " + payload.AchievementId + " 解锁失败：" + payload.Reason);
        }

        // 主要功能：奖励接缝的编辑器实现，把发奖变成窗口里的一行日志。
        // 主要数据成员：m_window（窗口，奖励写进它的日志）。
        // 主要外部方法：Grant（IQuestRewardSink 的实现）。
        // 谁在用它：NetDebugWindow.OnConfigsReady 建它，传给 BattleSession.FromConfigMgr。
        private sealed class EditorQuestRewardSink : IQuestRewardSink
        {
            // 窗口；奖励写进它的日志。
            private readonly NetDebugWindow m_window;

            // 功能：建一个发奖口，记下要写日志的窗口。
            // 入参：window 窗口
            // 返回：无
            // 步骤：只保存引用。
            // 上游：OnConfigsReady
            // 下游：无
            public EditorQuestRewardSink(NetDebugWindow window)
            {
                m_window = window;
            }

            // 功能：把奖励转给窗口的 OnRewardGranted —— 本类是发奖接缝的编辑器实现。
            public void Grant(int questId, QuestReward reward)
            {
                m_window.OnRewardGranted(questId, reward);
            }
        }

        // 功能：编辑器帧回调，推进会话并重绘窗口。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没会话直接返回
        //   2) 用时刻差算本次 delta（毫秒），并夹在 0..MaxDeltaMs
        //   3) 把 delta 喂给会话：Pump 推进心跳、收发与快照
        //   4) 依次刷新表现层、登记演示条件、发持续输入、发锁步上行、跑自动追击
        //   5) 请求重绘
        //   注意：delta 必须夹住 —— 编辑器卡顿或断点停住后间隔可能好几秒，照实喂会让心跳立刻超时，变成假掉线。
        // 上游：EditorApplication.update（OnEnable 订阅）
        // 下游：FeedPresentationLayer / EnsureDemoConditionRegistered / SendContinuousInput / PumpLockstepInput / TickAutoChase
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
                deltaMs = MaxDeltaMs;
            }

            m_session.Pump(deltaMs);
            FeedPresentationLayer();
            EnsureDemoConditionRegistered();
            SendContinuousInput();
            PumpLockstepInput();
            TickAutoChase();
            Repaint();
        }

        // 功能：自动追击并攻击（每编辑器帧一次；开关关掉就什么都不做）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没开开关、没在线或不在房间就返回
        //   2) 快照里找不到自己的英雄（刚进房或已死）就清冷却并返回，什么都不发
        //   3) 目标没了或死了就换成最近的活怪；连活怪都没有就返回
        //   4) 不在射程内就朝目标走：方向按坐标差取 -1000..1000，写进追击自己的字段
        //   5) 在射程内就站住，按一下普攻（按下 → 下一帧松开 → 等冷却），冷却按服务端常量
        //   注意：射程判定必须用圆（平方比较），与服务端同一份规则；退回切比雪夫会在对角方向多算 1.41 倍，客户端以为能打、服务端说够不着。
        //   注意：它绝不写滑条那两个值 —— 那是玩家手动意图，且被状态同步与锁步上行共用；真踩过：追击把滑条改写成 0，帧同步方向永远是 0，单位一动不动，而服务端与对账全都正常，只有看画面才发现。
        // 上游：Tick
        // 下游：NearestAliveMonsterId / FindEntity / SendInput
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
                    return;
                }
            }

            bool inRange = BattleRules.WithinBasicAttackRange(
                mine.PosXMm, mine.PosZMm, target.PosXMm, target.PosZMm);

            if (!inRange)
            {
                int moveX = target.PosXMm == mine.PosXMm ? 0 : (target.PosXMm > mine.PosXMm ? 1000 : -1000);
                int moveY = target.PosZMm == mine.PosZMm ? 0 : (target.PosZMm > mine.PosZMm ? 1000 : -1000);

                m_autoAttackCooldownTicks = 0;
                m_chaseInputX = moveX;
                m_chaseInputY = moveY;
                m_session.SendInput(moveX, moveY, 0u, 0u, m_targetEntityId);
                return;
            }

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

        // 功能：勾了持续发送就每帧发一条输入，模拟一直按着方向键。
        // 入参：无
        // 返回：无
        // 步骤：守卫（开关 / 在线 / 在房间）通过后用当前滑条值与目标发一条 SendInput；发多密都不影响速度（服务端每 tick 只应用一次），按编辑器帧率发正好也在验证这条限速。
        // 上游：Tick
        // 下游：NetSession.SendInput
        private void SendContinuousInput()
        {
            if (!m_inputContinuous || m_session == null || !m_session.IsOnline || !m_session.InRoom)
            {
                return;
            }

            m_session.SendInput(m_inputX, m_inputY, 0u, 0u, m_targetEntityId);
        }

        // 功能：锁步对局里的上行 —— 服务端每推进一帧，就把我此刻的意图发一条 lockstep_input。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没会话就返回
        //   2) 没有进行中的对局（运行器为空或未开局）就整体清零游标并返回
        //   3) 局号变了就清零游标、丢掉挂起的按键（同一会话里的第二局）
        //   4) 不能发上行（未在线 / 未开局 / 已宣布胜负）就返回
        //   5) 帧号没前进说明这一帧已经交过，不重复发
        //   6) 记下已交帧号，取出并清掉挂起的按键，发一条上行
        //   注意：没有进行中的对局就整体清零，专门管断线重连 —— 每次连接都新建会话、MatchSerial 清回 0，新会话第一局又是 1，与上一局撞号；只比局号会以为还是同一局，把上一局的「已发到第 500 帧」留着，重连后英雄不动而且不报错。
        //   注意：触发条件是「本局已开局」，不是帧同步勾选框 —— 勾选框管「我看哪份数据」，上行管「我参不参与这一局」；挂在勾选框上，切回快照后这一席再没人交输入，锁步世界永远判缺人，而画面上看不出来。
        //   注意：按帧节拍而不是编辑器帧率 —— 一帧一包才对，60Hz 发会被服务端当同一帧的重复丢并计数，看着在发实际只有第一条生效。
        //   注意：一次 Tick 至多一条 —— Pump 可能一口气补好几帧，中间那几帧必然缺我的输入（服务端填默认并标 Missing），这是锁步的固有代价，不是 bug。
        //   注意：这条只管锁步链，SendInput 照旧由 SendContinuousInput 发，两条链并存、互不替代。
        //   注意：真正打帧号的是 NetSession：默认取「已推进帧 + 1 + 提前量」，提前量 1 帧等于把网络预算从 1 个 tick 拉到 2 个；RTT 大时这点预算照样会被吃光（本地被挡计数还是 0），加大提前量的代价是按键到画面更迟。
        // 上游：Tick
        // 下游：ResetLockstepUplinkState / NetSession.SendLockstepInput
        private void PumpLockstepInput()
        {
            if (m_session == null)
            {
                return;
            }

            LockstepClient client = m_session.Lockstep;

            if (client == null || !client.Started)
            {
                ResetLockstepUplinkState();
                return;
            }

            if (m_lockstepSeenMatchSerial != m_session.MatchSerial)
            {
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
                return;
            }

            m_lastLockstepInputFrame = client.LastSteppedFrame;

            uint buttons = m_lockstepPendingButtons;
            m_lockstepPendingButtons = 0u;

            m_session.SendLockstepInput(m_inputX, m_inputY, buttons, m_targetEntityId);
        }

        // 功能：把上行游标（已交帧号、局号、挂起按键）清零。
        // 入参：无
        // 返回：无
        // 步骤：三个字段各回默认值；换会话、断线、还没开局时都要清。
        // 上游：PumpLockstepInput / CloseSession
        // 下游：无
        private void ResetLockstepUplinkState()
        {
            m_lastLockstepInputFrame = -1;
            m_lockstepSeenMatchSerial = -1;
            m_lockstepPendingButtons = 0u;
        }

        // 功能：画同步模式开关、锁步状态面板与表现层数据源那一块。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 画「同步模式」标题与勾选框；勾选态变了就调 SetLockstepMode（会记日志）
        //   2) 用 HelpBox 说明这个开关真的分流，以及世界不可用、停滞、滞回三条回退规则
        //   3) 画表现层数据源明细
        //   4) 画锁步只读状态面板
        //   注意：开关只换客户端读哪一份数据，不改服务端 —— 那边状态同步链与锁步链都还在跑。
        // 上游：OnGUI
        // 下游：SetLockstepMode / DrawPresentationSource / DrawLockstepPanel
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

        // 功能：画表现层数据源明细：现在喂的是谁、采纳与回退计数、停滞状态、切换次数、世界里的单位，以及「接到场景」按钮。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没数据源或没会话时显示提示并返回
        //   2) 逐行显示当前源、开关要求与实际生效、表现层帧号、采纳次数
        //   3) 分开显示回退事件数与回退帧数（两个口径不同）
        //   4) 显示停滞与滞回进度、切换次数、世界里的单位与丢弃旧帧计数
        //   5) 画视图接管状态与按钮；不在播放模式时给警告
        //   注意：回退事件数与回退帧数不能混成一个数 —— 帧数约等于编辑器帧率，一分钟上万，拿它当「回退了几次」的判据会失效。
        // 上游：DrawLockstepSection
        // 下游：DescribeUnits / SnapshotSceneBinder.Ensure
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

            EditorGUILayout.LabelField(
                "⚠️ 回退事件数（跃迁进回退才 +1）",
                feed.FallbackEvents + (feed.FallbackEvents > 0
                    ? "（一段连续回退只算 1 次；进房到开局之间非 0 是**正常**的）"
                    : "（还没回退过）"));

            EditorGUILayout.LabelField(
                "⚠️ 回退帧数（回退中每帧 +1）",
                feed.FallbackFrames + "（≈编辑器帧率 ⇒ **它不表示「回退了几次」**，判据看上面那个事件数）");

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

            EditorGUILayout.LabelField("表现层里的单位", DescribeUnits(shown));

            EditorGUILayout.LabelField(
                "丢弃旧帧（非 0 = 帧号在倒退）",
                shown.StaleIgnored + (shown.StaleIgnored > 0
                    ? "　❌ 帧号在倒退 ⇒ 数据会被静静丢掉（画面停住/空白）"
                    : "　✅"));

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

        // 功能：把表现层那份世界里的单位编号串成一行。
        // 入参：view 表现层那份数据；为 null 或没有单位时给一句人话
        // 返回：编号串（例：1、9001、9002、9003）；没有单位时返回「（一个都没有）」。
        // 步骤：逐个实体编号用顿号连接；这是判断分流切对了没有最直观的判据 —— 关（快照）时不该有机器人，开（锁步）时该有。
        // 上游：DrawPresentationSource
        // 下游：无
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

        // 功能：画帧同步（锁步）那份只读状态：局号、开局、帧号推进、分歧与各计数、上行统计。
        // 入参：session 当前会话；为 null 或还没有锁步运行器时只显示一行提示
        // 返回：无
        // 步骤：
        //   1) 没有运行器就显示「未创建」并返回
        //   2) 显示本会话第几局、是否开局、帧号推进
        //   3) 显示分歧、未开局忽略、重复被拒、胜负不一致、结果
        //   4) 显示上行已发/被挡与下一条要打的帧号
        //   注意：局号相关的计数都是本局的，标签里必须写「本局」，否则会被误读成整个会话的累计值，看不出这局干不干净。
        // 上游：DrawLockstepSection
        // 下游：LockstepClient.Describe
        private void DrawLockstepPanel(NetSession session)
        {
            if (session == null || session.Lockstep == null)
            {
                EditorGUILayout.LabelField("锁步运行器", "未创建（还没收到任何锁步消息）");
                return;
            }

            LockstepClient client = session.Lockstep;

            EditorGUILayout.LabelField("本会话第几局（每收到一次开局 +1）", session.MatchSerial.ToString());
            EditorGUILayout.LabelField("锁步已开局", client.Started ? "是" : "否");
            EditorGUILayout.LabelField("帧号推进", client.StepsExecuted + " 帧（末帧 " + client.LastSteppedFrame + "）");
            EditorGUILayout.LabelField("⭐ 分歧计数（本局；每局清零）", client.DesyncCount.ToString());
            EditorGUILayout.LabelField("未开局忽略（本局）", client.IgnoredBeforeStart.ToString());
            EditorGUILayout.LabelField("重复/倒退被拒（本局）", client.RejectedFrames.ToString());
            EditorGUILayout.LabelField("胜负判定不一致（本局）", session.MatchMismatchCount.ToString());
            EditorGUILayout.LabelField("结果（本局）", session.MatchOutcome == null ? "未宣布" : session.MatchOutcome.Value.ToString());
            EditorGUILayout.LabelField("一句话", client.Describe());

            EditorGUILayout.LabelField("上行（我发给服务端的输入）",
                "已发 " + session.LockstepInputsSent + " 条／被挡 " + session.LockstepInputsSkipped
                + " 次；下一条打第 " + session.NextLockstepFrame + " 帧");
            EditorGUILayout.LabelField("能不能发上行", session.CanSendLockstepInput
                ? "能（在线 + 已开局 + 本局还没宣布胜负）"
                : "不能（未在线／未开局／已宣布胜负）");
        }

        // 功能：拨锁步开关：改表现层数据源的选源，并记一行可回溯的日志。
        // 入参：on true = 帧同步；from 谁拨的（勾选框 / 代码）
        // 返回：无
        // 步骤：
        //   1) 记下开关值，取（必要时建）数据源并设 UseLockstep
        //   2) 有会话时把模式写进同源统计；没连时仍是离线，不为切换而谎报在线
        //   3) 记一行日志，写明服务端两条链都还在跑，只有客户端改读哪一份
        //   注意：日志里必须写清服务端两条链都还在跑，否则下一个看到的人会以为真的换了一条链。
        // 上游：DrawLockstepSection 的勾选框 / 代码
        // 下游：EnsureFeed / NetRuntimeStats.SetMode / AddLog
        private void SetLockstepMode(bool on, string from)
        {
            m_lockstepMode = on;

            PresentationFeed feed = EnsureFeed();
            feed.UseLockstep = on;

            if (m_session != null)
            {
                NetRuntimeStats.SetMode(on ? NetSyncMode.Lockstep : NetSyncMode.StateSync);
            }

            AddLog("[表现层] 数据源切换（" + from + "）⇒ " + (on ? "锁步" : "快照") +
                   "：服务端两条链都还在跑，只有客户端改读哪一份。");
        }

        // 功能：取（必要时建）表现层数据源，并让它的开关跟窗口上的勾选态一致。
        // 入参：无
        // 返回：数据源（不为 null）。
        // 步骤：为空就新建；随后把 UseLockstep 同步成当前勾选态。幂等：值没变就不会涨计数（每帧都会调到这里）。
        // 上游：SetLockstepMode / FeedPresentationLayer
        // 下游：无
        private PresentationFeed EnsureFeed()
        {
            if (m_feed == null)
            {
                m_feed = new PresentationFeed();
            }

            m_feed.UseLockstep = m_lockstepMode;
            return m_feed;
        }

        // 功能：真分流唯一的执行点 —— 每帧把「当前该用的那份」喂给表现层。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没会话直接返回
        //   2) 取数据源与锁步运行器，把玩家编号、服务端世界、锁步世界、末帧、是否开局、局号一起交给 Refresh；只读两条链的状态，不写回世界、不调 Step、不改会话
        //   3) 把 RTT 与帧号写进同源统计：状态同步取快照 tick，帧同步取已步进帧，按模式取别混
        //   注意：必须传局号 —— 不传就只能靠世界对象或帧号倒退去猜，而「上一局末帧 0、新一局从 0 起」那一格猜不出来。
        // 上游：Tick
        // 下游：PresentationFeed.Refresh / NetRuntimeStats
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
                m_session.MatchSerial);

            NetRuntimeStats.SetRtt(m_session.RttMs);
            NetRuntimeStats.SetFrame(m_lockstepMode
                ? (client == null ? -1 : client.LastSteppedFrame)
                : (int)m_session.World.ServerTick);
        }

        // 功能：画整个窗口：顶部常驻摘要行 + 服务端/账号/房间/状态/任务/成就/日志。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先画顶部常驻摘要行（在滚动视图外面，最要紧的数不用滚就能看到）
        //   2) 用整窗滚动视图包住其余内容：滚轮 / 拖拽都能翻到底
        //   3) 画服务端与账号输入、连接/断开/心跳按钮
        //   4) 画房间入口与同步模式面板
        //   5) 依次画状态、任务条件、真任务、成就、日志
        //   6) 没连接时提示怎么起服务端
        //   注意：原来这里是一整列且没有滚动容器，节一多下半截就被窗口边界裁掉、翻不到；所以必须保留摘要行 + 整窗滚动这两层。
        // 上游：Unity 编辑器在重绘时调用
        // 下游：DrawSummaryLine / DrawLockstepSection / DrawStatus / DrawQuestLoop / DrawRealQuests / DrawRealAchievements / DrawLog
        private void OnGUI()
        {
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

        // 功能：画顶部常驻摘要行：会话、房间、世界、任务进度、服务端事件计数、已发输入。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没会话时显示「未连接」并返回
        //   2) 一行写会话状态、玩家编号、RTT、房间人数与容量、世界存活数
        //   3) 另一行写任务进度、达成回调次数、伤害/死亡/掉落计数与已发输入
        //   注意：它必须留在滚动视图外面 —— 整窗滚动解决了看不到，但最要紧的数仍要钉在顶上不用滚。
        // 上游：OnGUI 的第一个调用
        // 下游：无
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

        // 功能：画状态区：会话状态、人话说明、玩家编号、RTT、心跳、服务端版本、传输统计与失败原因。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没会话时显示「未创建」并返回
        //   2) 逐行显示状态与各计数
        //   3) 有握手信息就显示服务端版本、协议版本与 tick 频率
        //   4) 有失败原因就用错误框显示
        //   5) 继续画房间区（席位、世界、掉落、输入都在它下面）
        // 上游：OnGUI
        // 下游：DrawRoom
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

        // 功能：画房间区：房号、副本、席位与成员列表、最近一次被拒，然后接着画世界、掉落与输入。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 不在房间就显示「不在任何房间」
        //   2) 在房间就逐行显示房号、副本、席位数、阶段与每个成员（房主 / 已准备）
        //   3) 有上次错误就用警告框显示：被拒不等于掉线，所以不按错误处理
        //   4) 依次画世界、掉落、输入
        // 上游：DrawStatus
        // 下游：DrawWorld / DrawDrops / DrawInput
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
                EditorGUILayout.HelpBox(
                    "上次请求被拒（" + m_session.LastError.Code + "）：" + m_session.LastError.Message,
                    MessageType.Warning);
            }

            DrawWorld();
            DrawDrops();
            DrawInput();
        }

        // 功能：画掉落区：累计条数、最近一次、最近几条记录；客户端只负责显示，掷骰全在服务端。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没会话时累计显示 0 并返回
        //   2) 显示累计收到与最近一次掉落（物品、数量、归属玩家）
        //   3) 列出最近几条掉落
        //   4) 在房间且一条都没掉过时给概率提示
        //   注意：狼的两条掉落概率是 50% / 30%，什么都没掉有 35% 的概率；BOSS 是 10000（必掉）。
        // 上游：DrawRoom
        // 下游：无
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

        // 功能：画输入区：自动追击开关、两个方向滑条、持续发送、目标编号、状态同步与锁步两排按钮。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 只有在线且在房间时输入控件可用
        //   2) 画自动追击开关与说明
        //   3) 画左右/前后滑条、持续发送、目标编号，以及状态同步那排按钮（发一次输入 / 普攻 / 选最近活怪）
        //   4) 画锁步上行按钮：把普攻挂到下一条上行；不能发时置灰
        //   5) 画锁步说明、目标距离提示、已发输入与锁步上行统计、自动追击计数
        //   注意：上面那排按钮全是状态同步链的，锁步上行是另一个按钮 —— 两条链并存，按钮分开，免得按了不知道喂了谁。
        //   注意：这里不看帧同步勾选框：勾选框管我看哪份数据，上行管我参不参与这一局；只要服务端开了局，我这一席就该交输入，否则换回快照后锁步世界永远判缺人而没人会注意到。
        // 上游：DrawRoom
        // 下游：SendInput / NearestAliveMonsterId / DrawTargetHint
        private void DrawInput()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("输入（只发意图）", EditorStyles.boldLabel);

            bool canInput = m_session != null && m_session.IsOnline && m_session.InRoom;

            using (new EditorGUI.DisabledScope(!canInput))
            {
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

        // 功能：把目标在不在射程内直接写出来，省得自己去算。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没会话或没目标就返回
        //   2) 快照里找不到自己或目标就提示等快照
        //   3) 判定用圆（平方比较），显示用真实距离（四舍五入）—— 两者别混用同一个数
        //   4) 判出界时把显示距离抬到射程 + 1，避免出现「2000mm 太远」这种自相矛盾的提示
        //   5) 显示距离与双方坐标、目标血量
        // 上游：DrawInput
        // 下游：BattleRules.WithinBasicAttackRange / DistanceMmRounded
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

            bool inRange = BattleRules.WithinBasicAttackRange(
                mine.PosXMm, mine.PosZMm, target.PosXMm, target.PosZMm);

            int distance = BattleRules.DistanceMmRounded(
                mine.PosXMm, mine.PosZMm, target.PosXMm, target.PosZMm);

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

        // 功能：从本地快照里挑离我最近的活怪当目标，省得手填编号。
        // 入参：无
        // 返回：实例编号；没会话或没有活怪时返回 0。
        // 步骤：
        //   1) 没会话返回 0
        //   2) 遍历实体，只留 Kind 为怪且活着的
        //   3) 找不到自己的英雄（快照还没到）时距离按 0 算，退化成第一只活怪；否则按切比雪夫距离比
        //   4) 记下最近的编号
        //   注意：不要退回「快照里第一只活怪」—— 快照顺序是创建顺序，常常指到地图另一头，看起来就是打了没反应。
        // 上游：DrawInput / TickAutoChase
        // 下游：Chebyshev
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

                int distance = mine == null ? 0 : Chebyshev(mine, e);

                if (bestId == 0 || distance < bestDistance)
                {
                    bestId = e.EntityId;
                    bestDistance = distance;
                }
            }

            return bestId;
        }

        // 功能：按实例编号在本地快照里找单位。
        // 入参：world 本地世界副本；entityId 实例编号（0 表示没目标）
        // 返回：找到的单位；world 为空、编号为 0 或找不到时返回 null。
        // 步骤：先挡掉空世界与 0 号，再线性扫一遍比对实例编号。
        // 上游：DrawTargetHint / TickAutoChase / DrawInput
        // 下游：无
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

        // 功能：算两个单位在水平面上的切比雪夫距离，单位毫米。
        // 入参：a 甲；b 乙（都用协议坐标，单位毫米）
        // 返回：X / Z 两个方向坐标差里较大的那个；单位毫米。
        // 步骤：取两个方向差的绝对值，返回较大值。与服务端 DungeonBattle.DistanceMm 同一种量法，但它只用来选最近目标；射程判定用圆，别混。
        // 上游：NearestAliveMonsterId
        // 下游：无
        private static int Chebyshev(NBC.Protocol.EntitySnapshot a, NBC.Protocol.EntitySnapshot b)
        {
            int dx = Math.Abs(a.PosXMm - b.PosXMm);
            int dz = Math.Abs(a.PosZMm - b.PosZMm);
            return dx > dz ? dx : dz;
        }

        // 功能：画世界区：服务端快照的本地副本，列出服务端帧、存活数与每个单位。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 显示服务端帧（还没收到就写还没收到）、存活数、快照采纳与丢弃数
        //   2) 逐个单位显示实例编号、配置编号、血量或已死亡、位置（毫米）与朝向角度
        // 上游：DrawRoom
        // 下游：无
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

        // 功能：画演示任务条件区：条件定义、进度、达成回调、事件计数、桥收到与网络层收到的计数，以及目标只数覆盖。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没会话就返回
        //   2) 显示当前条件（怪编号 × 只数）与来源（手填 / 自动取自阵容）
        //   3) 显示进度与达成回调次数；还没登记时给提示
        //   4) 显示事件中心监听者数、桥计数、网络层计数
        //   5) 提供目标只数覆盖与两个重设按钮；没进房时给提示
        //   注意：演示条件是按代码造出来的，不是配置表读的；真任务走同一个条件系统。
        //   注意：目标只数不能写死 —— 副本 1001 只刷 2 只野狼，写死 3 只永远达不成，所以默认从快照阵容算，也允许手填覆盖。
        // 上游：OnGUI
        // 下游：ResetDemoCondition
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

        // 功能：画真任务区：读配置表，按可接取 / 进行中 / 可交付 / 已交付四区显示，并给出接取/交付入口。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 配置表加载失败时显示错误与「重新加载配置表」按钮，然后返回
        //   2) 配置表没就绪或没有视图模型时显示加载中
        //   3) 一次调用 CopySections 填满四个区：分区按服务端权威状态决定，服务端没说才退回本地并标「（本地预测）」
        //   4) 逐区画行；再显示消息、活动任务描述与最近一条发奖记录
        //   5) 用提示框说明进度来自联机事件
        //   注意：不要在窗口里自己把任务从可接删掉再加到进行中 —— 漏掉「加」这一步，任务会从界面消失且不报错；那正是 CopySections 的 placed 集合要防的。
        // 上游：OnGUI
        // 下游：DrawQuestSection / QuestPanelModel.CopySections
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

        // 功能：画成就区：已解锁数、每条成就与它的条件进度、最后的配置问题。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没有任务会话时提示配置表还没就绪
        //   2) 没有成就运行时时说明原因，并给「重新加载配置表」按钮
        //   3) 刷新追踪列表，显示已解锁数与每条成就的条件进度
        //   4) 有配置问题就用错误框显示
        //   注意：成就与任务共用同一个条件系统，打怪时两条一起涨；差别是成就不清零、不用接、条件一齐当场解锁。
        //   注意：成就是跨局累计的 —— 用的是条件的持久进度，窗口重开、会话重建都不清零。
        // 上游：OnGUI
        // 下游：AchievementRuntime.CopyTrackings
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

        // 功能：画一个任务区：区名、数量与区内每一行。
        // 入参：title 区名；rows 该区的显示行
        // 返回：无
        // 步骤：先画标题与数量（空区写「没有」），再逐行转给 DrawQuestRow。
        // 上游：DrawRealQuests（四个区共用这一段）
        // 下游：DrawQuestRow
        private void DrawQuestSection(string title, List<QuestRow> rows)
        {
            EditorGUILayout.LabelField(title, rows.Count == 0 ? "（没有）" : rows.Count + " 个");

            for (int i = 0; i < rows.Count; i++)
            {
                DrawQuestRow(rows[i]);
            }
        }

        // 功能：画一行任务：标题、详情与一个动作按钮。
        // 入参：row 显示行（标题、详情、动作名、动作文案、任务编号）
        // 返回：无
        // 步骤：横排画标题与详情；点按钮就用动作名执行。
        // 上游：DrawQuestSection
        // 下游：RunQuestAction
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

        // 功能：执行一个任务动作（接取 / 交付），走真面板同一个入口。
        // 入参：actionName 动作名（Accept_编号 / Submit_编号）
        // 返回：无
        // 步骤：没有视图模型就返回；否则交给 HandleAction，并把是否被受理与服务端消息写进日志。
        // 上游：DrawQuestRow
        // 下游：QuestPanelModel.HandleAction / AddLog
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

        // 功能：画日志区：固定高度的内层滚动，列出最近的日志。
        // 入参：无
        // 返回：无
        // 步骤：先画标题；内层滚动给固定高度 LogBoxHeight，再逐行显示日志。
        //   注意：原来用 ExpandHeight(true)，放进外层滚动视图后外层高度无界，日志框会塌成 0 高、一条都看不见；所以外层管整窗翻页、内层管日志自己翻，各管一段。
        // 上游：OnGUI
        // 下游：无
        private void DrawLog()
        {
            EditorGUILayout.LabelField("日志（最近 " + MaxLogLines + " 条；框内可滚轮）", EditorStyles.boldLabel);

            m_logScroll = EditorGUILayout.BeginScrollView(m_logScroll, GUILayout.Height(LogBoxHeight));

            for (int i = 0; i < m_log.Count; i++)
            {
                EditorGUILayout.LabelField(m_log[i], EditorStyles.miniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        // 功能：连上去：先断旧会话，再造传输、造会话、挂事件，最后发起连接并启动任务演示链。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先关掉旧会话（重连前先断开）
        //   2) 造 TcpTransport，外面包一层 CountingTransport 做字节统计；HUD 与窗口读同一份数据，所以这里只包、不自己算带宽
        //   3) 造 NetSession：第 7、8 个参数是账号与密码，留空就是游客，行为与原来一字不差；密码交给它算完摘要就丢
        //   4) 把任务面板接到新会话上（视图模型可能还没就绪，方法里有守卫）
        //   5) 订阅 Note / 状态变化 / 握手完成 / RTT 更新，并把模式写进同源统计
        //   6) 订阅服务端权威进度：逐条念出每个条件的当前/需求/是否达成
        //   7) 记下起始时刻，连接，最后启动任务演示链
        //   注意：只有登录玩家才有权威进度；游客没有档案，服务端不追踪也不下发，所以游客连接时一条都不该出现 —— 那不是 bug，是设计。
        // 上游：界面的「连接」按钮
        // 下游：CloseSession / WireQuestPanelToServer / StartQuestLoopDemo
        private void ConnectSession()
        {
            CloseSession("重连前先断开");

            m_transport = new TcpTransport();

            ITransport counting = new CountingTransport(m_transport);
            NetRuntimeStats.Reset();

            m_session = new NetSession(
                counting,
                m_playerName,
                "unity-editor/" + Application.unityVersion,
                NetSession.DefaultHeartbeatIntervalMs,
                NetSession.DefaultHeartbeatTimeoutMs,
                NetSession.DefaultHandshakeTimeoutMs,
                m_account,
                m_password);

            WireQuestPanelToServer();

            m_session.Note += line => AddLog(line);
            m_session.StateChanged += state => AddLog("状态 → " + state);
            m_session.HandshakeCompleted += ack =>
                AddLog("握手完成：玩家 " + ack.PlayerId +
                       "，昵称 " + ack.Nickname +
                       "，服务端 " + ack.ServerVersion);

                NetRuntimeStats.SetMode(m_lockstepMode ? NetSyncMode.Lockstep : NetSyncMode.StateSync);
            m_session.RttUpdated += rtt => AddLog("RTT " + rtt + "ms");

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

        // 功能：接上「联机战斗 → 任务条件」这条链。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 第一次调用才建条件系统与它的桥，并绑定怪物死亡事件
        //   2) 订阅条件达成回调，记次数并写日志
        //   3) 每次连接只重建跟当前会话绑定的 ServerEventBridge
        //   4) 把登记标记清掉，等第一张快照重新登记条件
        //   注意：条件系统与桥只建一次 —— 它们挂在全局事件中心上，每次连接都建一份会让同一个事件喂进两次，进度翻倍且看不出来。
        //   注意：条件本身不在这里登记 —— 目标只数要等第一张快照知道本副本有几只怪，见 EnsureDemoConditionRegistered。
        // 上游：ConnectSession
        // 下游：EnsureDemoConditionRegistered
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
            m_demoConditionRegistered = false;
        }

        // 功能：从快照里的真实阵容算出演示条件的目标：打哪种怪、打几只。
        // 入参：monsterId 算出来的怪编号（算不出为 0）；count 算出来的只数（算不出为 0）
        // 返回：无（两个值都走 out）
        // 步骤：
        //   1) 先归零；没会话直接返回
        //   2) 只数 Kind 为怪的实体，按配置编号分组计数
        //   3) 没有怪就返回
        //   4) 有优先怪（6001 野狼）就取它和它的只数
        //   5) 否则取出现次数最多的那种；平局按编号升序打破，保证确定性
        //   注意：取出现次数最多的那种怪，顺带解决了「哪个是 BOSS」—— BOSS 只有一只，小怪是一批，不用给快照再加 is_boss 字段。
        // 上游：EnsureDemoConditionRegistered
        // 下游：无
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

        // 功能：第一张快照到了之后按阵容登记演示条件。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没条件系统、没会话或不在房间就返回
        //   2) 同一个房间已经登记过就直接返回
        //   3) 手填只数 > 0 时仍按阵容挑怪，只数听手填的；否则全按阵容算
        //   4) 只数还算不出来就下一帧再看
        //   5) 先注销旧条件再按新定义登记，并记下实际用的怪、只数与房号
        //   注意：换房间（换副本）必须重新登记 —— 否则进了蜘蛛洞还在要求击杀野狼，又变成永远达不成的目标。
        // 上游：Tick
        // 下游：ResolveDemoTarget / ConditionTracker.Register
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
                ResolveDemoTarget(out monsterId, out count);
                count = m_demoKillTarget;
            }
            else
            {
                ResolveDemoTarget(out monsterId, out count);
            }

            if (count <= 0)
            {
                return;
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

        // 功能：重设演示条件（按钮用）。
        // 入参：forceAuto true = 清掉手填值、改回按阵容自动
        // 返回：无
        // 步骤：没有条件系统就返回；需要时清掉手填只数，注销旧条件并清登记标记与达成计数，再立即重新登记一次。
        // 上游：DrawQuestLoop 的两个按钮
        // 下游：EnsureDemoConditionRegistered
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

        // 功能：断开并释放当前会话与相关订阅。
        // 入参：reason 原因（写进日志）
        // 返回：无
        // 步骤：
        //   1) 没会话直接返回
        //   2) 写一行分隔日志
        //   3) 先退订事件桥（它订阅的是这个会话的事件），再 Dispose 会话
        //   4) 清空传输引用，把同源统计归零（模式回离线、带宽归零、帧号清成未知）
        //   5) 清掉锁步上行游标：新会话的 MatchSerial 又从 0 数起，不清会与上一局撞号
        //   6) 清掉表现层数据源：不清的话下一局第 0 帧可能比上一局末帧小，画面停住
        //   7) 丢掉场景视图接管的引用（组件还活着，但窗口不再显示它的状态）
        //   注意：清理顺序不能反 —— 先退订再 Dispose，否则回调可能在已释放的会话上跑。
        // 上游：OnDisable / 断开按钮 / ConnectSession 重连前
        // 下游：ResetLockstepUplinkState / PresentationFeed.Clear / NetRuntimeStats.Reset
        private void CloseSession(string reason)
        {
            if (m_session == null)
            {
                return;
            }

            AddLog("=== " + reason + " ===");

            if (m_eventBridge != null)
            {
                m_eventBridge.Dispose();
                m_eventBridge = null;
            }

            m_session.Dispose();
            m_session = null;
            m_transport = null;

            NetRuntimeStats.Reset();

            ResetLockstepUplinkState();

            if (m_feed != null)
            {
                m_feed.Clear();
            }

            m_binder = null;
        }

        // 功能：加一行带时间戳的日志，超出上限就丢最旧的。
        // 入参：line 日志内容
        // 返回：无
        // 步骤：先拼上时分秒毫秒，再从头丢到不超过 MaxLogLines。
        // 上游：本文件各处
        // 下游：无
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
