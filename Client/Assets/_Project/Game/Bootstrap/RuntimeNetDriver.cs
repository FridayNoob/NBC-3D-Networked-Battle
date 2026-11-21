// 这个文件里有：
//   ERuntimeSyncMode（枚举）、RuntimeNetDriver（类）：运行时的会话驱动（自动连服/进房/发输入/喂表现）

using System;
using System.Collections;
using System.Collections.Generic;
using NBC.Framework.Input;
using NBC.Framework.Net;
using NBC.Framework.Net.Adapter;
using NBC.Game.Config;
using NBC.Game.GameFlow;
using NBC.Game.Net;
using NBC.Protocol;
using NBC.Game.Player;
using NBC.Game.UI;
using NBC.Game.View;
using NBC.Shared.Sim;
using UnityEngine;

namespace NBC.Game.Bootstrap
{
    // 运行时用哪种同步方式。只影响「客户端读哪一份世界 + 发哪条上行」，服务端两条链都在跑。
    public enum ERuntimeSyncMode
    {
        // 状态同步：读服务端快照，上行 SendInput
        StateSync = 0,

        // 帧同步：读锁步世界，上行 SendLockstepInput
        Lockstep = 1
    }

    // 主要功能：Play 后自动建会话、连接、进房、按模式发输入、把数据喂给表现层，并把统计写进 HUD。
    // 主要数据成员：m_host / m_port / m_account / m_password / m_dungeonId / m_mode / m_inputSendInterval。
    // 主要外部方法：IsOnline / IsJoined / LastError（只读状态）。
    // 谁在用它：由 Boot 侧（HpBarDemoBehaviour）在资源层装好后 AddComponent 挂上，或场景里手挂。
    public sealed class RuntimeNetDriver : MonoBehaviour
    {
        // 服务器地址
        [SerializeField] private string m_host = "127.0.0.1";

        // 服务器端口（9000 在 Windows 保留段 8945-9044 里，别用）
        [SerializeField] private int m_port = 7778;

        // 开发账号（服务端 --dev-accounts 时可用 dev1..dev4）
        [SerializeField] private string m_account = "dev1";

        // 账号密码
        [SerializeField] private string m_password = "dev-pw1";

        // 副本编号（房号留空 = 服务端安排）
        [SerializeField] private int m_dungeonId = 1001;

        // 握完手自动进房（入口脚本会先把它关掉，等切到副本再打开）
        [SerializeField] private bool m_autoJoin = true;

        // 是否把快照画成场景物体、并纠偏本地英雄（在 World 里连服时要关掉，否则会画出一堆副本怪）
        [SerializeField] private bool m_bindPresentation = true;

        // 副本进出的四个开关收在一处：进入 / 离开各只有一个入口，
        // 免得「只复位了一半」——参见 FIXES 2026-11-20「回世界后还挂在房间里」。
        private readonly BattlePresence m_presence = new BattlePresence();

        // 运行时同步方式
        [SerializeField] private ERuntimeSyncMode m_mode = ERuntimeSyncMode.StateSync;

        // 上行节流间隔（秒）。20Hz 对状态同步足够；锁步同样按这个节奏问一次。
        [SerializeField] private float m_inputSendInterval = 0.05f;

        // 会话（本组件的唯一持有者）
        private NetSession m_session;

        // 表现层数据源（快照 / 锁步二选一，复用 Editor 同一条路径）
        private PresentationFeed m_feed;

        // 3D 绑定器（缓存，不每帧找）
        private SnapshotSceneBinder m_binder;

        // R046 HUD 驱动（挂在本物体上，跨场景常驻；进副本时 Bind、回世界时 CloseAll）
        private RuntimeHudDriver m_hudDriver;

        // HUD 是否已经接上这一局的快照源（避免每帧重复 Bind、重复 ShowPanel）
        private bool m_hudBound;

        // 进副本时的启动编排（装资源 → 设配置来源 → 等配置表 → 挂 HUD）；回世界时作废
        private BattleBootSequence m_boot;

    // 「再来一次」的计时探针：记录点了按钮的时刻，等第一张含活怪的快照到了就打一条总耗时。
    // 只用于诊断「重开后怪多久才出现」，不影响任何逻辑。
    private float m_restartWentAt;
    private bool m_restartWatching;

        // 发送节流累加器
        private float m_sendAccumulator;

        // 最近一次错误（空 = 没出错）
        private string m_lastError = string.Empty;

        // 断线重连策略（退避与次数上限都在它里面，本组件只执行它给的动作）
        private NetReconnector m_reconnector;

        // 敌人模型 prefab（由装配者设置；null = 表现层回落胶囊）
        private GameObject m_monsterPrefab;

        // 攻击动作位：服务端约定「第 0 位 = 普攻」（RoomBattleService 里 ActionBits & 1u）
        private const uint AttackBitMask = 1u;

        // 客户端选目标的半径，毫米；服务端普攻射程 2000，这里留 1 米余量
        private const int AttackTargetRangeMm = 3000;

        // 本地玩家的动画组件（播攻击用）；只查一次
        // 英雄位置纠偏：服务端是权威，客户端只做"跟随式校正"（不是完整预测回滚）
        private PlayerMove m_playerMove;

        // 诊断：上一次打「位置漂移」日志的时间（限流，避免刷屏）
        private float m_lastDriftLogTime;

        // 诊断：上一次打「找不到我的英雄」日志的时间
        private float m_lastHeroMissingLogTime;

        // 允许的最大漂移（米）：小于它就不动，避免每帧抖动
        private const float HeroDriftDeadzoneM = 0.3f;

        // 超过它就直接瞬移（米）：通常是被场地边界挡住后的重新对齐
        private const float HeroSnapDistanceM = 2f;

        // 纠偏速度（米/秒）：往服务端位置靠拢
        private const float HeroReconcileSpeedMps = 8f;

        // 与服务端一致的场地半边长（毫米）。
        // 注意：R056 验收期间临时放大到 50 米（原 5000），改回时要和服务端
        // `DungeonBattle.PlayAreaHalfExtentMm` 一起改，否则人会「走出服务端场地」。
        private const int PlayAreaHalfExtentMm = 50000;
        private PlayerAnimation m_playerAnimation;

        // 本地英雄的移动脚本（懒加载）：上行输入要靠它把相机相对方向换成世界方向
        private PlayerMove m_localMove;

        // 本地英雄的大脑（血量为 0 时切到死亡状态；懒加载，只有 Battle 场景才存在）
        private PlayerBrain m_playerBrain;

        // 本地英雄是否还活着改由 m_presence 统一维护（见 BattlePresence）。

        // 当前快照里的英雄个数（1 = 单人副本，>1 = 多人副本；死亡后决定「退出」还是「旁观」）
        private int m_heroEntityCount;

        // 死亡分流组件是否已检查过（Battle 里只在第一次拿到英雄快照时挂一次）
        private bool m_deathFlowChecked;

        // 剑上的 Trigger 碰撞体（R019：用「剑碰到谁」决定打谁）；只找/建一次
        private Collider m_swordCollider;

        // 是否已经找过剑（避免每次都深搜层级）
        private bool m_swordLooked;

        // 下一次允许重找剑的时间（秒，unscaled）；找到之前每秒最多重试一次
        private float m_swordRetryTime;

        // 找剑失败的时间间隔（秒）
        private const float SwordRetrySeconds = 1f;

        // 「找不到玩家 / 找不到剑」的告警是否已经打过（只打一次，避免刷屏）
        private bool m_swordMissingLogged;

        // 本地英雄的当前血量（0 = 还不知道）；HUD 每帧读它（R020）
        public int HeroHp { get; private set; }

        // 本地英雄的满血值（0 = 还不知道）
        public int HeroMaxHp { get; private set; }

        // 本地英雄血量基线是否已建立（首帧只记不比）
        private bool m_heroHpKnown;

        // 上一帧本地英雄的血量
        private int m_heroLastHp;

        // 攻击判定窗口：按下后 [Start, End) 秒内采样剑的碰撞盒；碰到才发请求
        private const float AttackWindowStart = 0.10f;

        // 窗口结束时间（秒，相对按下时刻）；超过就关窗（这一刀打空）
        private const float AttackWindowEnd = 0.40f;

        // 窗口是否开着
        private bool m_attackWindowOpen;

        // 单次推进的最大毫秒数：卡顿/切窗口后 delta 可能是几秒，照实喂会让会话时钟猛跳、把心跳判成超时（假掉线）。
        private const int MaxPumpDeltaMs = 250;

        // 窗口开始的时刻（Time.time）
        private float m_attackWindowStart;

        // 这一刀是否已经发出去过（一刀只结算一次）
        private bool m_attackSent;

        // 下一次允许开窗的时刻；防止同一次按键开两次窗
        private float m_attackNextAllowed;

        // 上一帧是否在房间里（用来识别「重新进房」这个事件，重连回来时据此重置表现层）
        private bool m_wasInRoom;

        // 功能：告诉调用方是否已握手成功。
        // 入参：无
        // 返回：是否已握手成功
        public bool IsOnline
        {
            get { return m_session != null && m_session.IsOnline; }
        }

        // 功能：告诉调用方是否已进房。
        // 入参：无
        // 返回：是否已进房
        public bool IsJoined
        {
            get { return m_session != null && m_session.InRoom; }
        }

        // 功能：请求释放一个技能（目标不传就自动挑最近的活怪）。
        // 入参：skillId 技能编号；targetEntityId 目标实例编号（0 = 自动挑）
        // 返回：true 表示请求已经发出去（服务端还会再判射程/冷却，可能拒绝）
        // 步骤：
        //   1) 没连服就直接返回 false（避免离线时误发）
        //   2) 没有指定目标就找最近的活怪
        //   3) 交给 NetSession.SendSkillCast，并打一条日志方便对账
        // 上游：PlayerBrain 的 Q/E/R（R043b 临时入口；R043a 会换成 RuntimeSkillDriver）
        // 下游：NetSession.SendSkillCast
        // 功能：找「当前真正在副本里」的那个网络驱动。
        // 入参：无。
        // 返回：已进房的那个驱动；一个都没进房时退回任意一个（离线/主菜单）；一个都没有返回 null。
        // 步骤：
        //   1) 取场景里所有 RuntimeNetDriver
        //   2) 优先返回 IsJoined == true 的
        //   3) 退而求其次返回第一个非空的
        // 上游：PlayerBrain 发技能前
        // 下游：UnityEngine.Object.FindObjectsOfType
        // 注意：Login 的 [MenuSession] 会常驻，进副本又建了 [DungeonSession]，
        //       所以场景里可能同时有两个驱动；FindObjectOfType 拿到没进房的那个就会「发了但没人收」。
        public static RuntimeNetDriver FindJoined()
        {
            RuntimeNetDriver[] drivers = FindObjectsOfType<RuntimeNetDriver>();
            RuntimeNetDriver fallback = null;

            foreach (RuntimeNetDriver driver in drivers)
            {
                if (driver == null)
                {
                    continue;
                }

                if (fallback == null)
                {
                    fallback = driver;
                }

                if (driver.IsJoined)
                {
                    return driver;
                }
            }

            return fallback;
        }
        public bool SendSkillCast(int skillId, int targetEntityId = 0)
        {
            if (m_session == null || !m_session.IsOnline)
            {
                Debug.LogWarning("[技能] 还没连上服务器，技能 " + skillId + " 没发出去");
                return false;
            }

            int target = targetEntityId > 0 ? targetEntityId : FindNearestEnemyId();

            if (target == 0)
            {
                // 没有目标也要发（服务端会拒），但先把话说清楚：这类"按了没反应"最常见就是这里
                Debug.LogWarning("[技能] 视野里没有活怪，技能 " + skillId + " 没有目标（服务端会判「目标不存在」）");
            }
            else if (TryMeasureTarget(skillId, target, out int distanceMm, out int rangeMm) && distanceMm > rangeMm)
            {
                // 本地先说清楚差多远，省得玩家以为技能坏了（服务端的权威判定在 TryCast 里，这里只是提示）
                Debug.LogWarning("[技能] 目标太远：[" + (distanceMm / 1000f).ToString("0.0") + " 米] > 射程 [" +
                                 (rangeMm / 1000f).ToString("0.0") + " 米]（技能 " + skillId + "），走近一点再放");
                return false;
            }

            m_session.SendSkillCast(skillId, target);
            Debug.Log("[技能] 请求释放 " + skillId + " → 单位 " + target);
            return true;
        }

        // 功能：量一下"英雄到目标"的距离，并读出这个技能的射程（都从快照与配置表来）。
        // 入参：skillId 技能编号；targetId 目标实例编号
        // 返回：true 表示两个数都拿到了；false 表示快照里没英雄/没目标或配置表没读到
        // 步骤：
        //   1) 快照里找自己的英雄，再找目标实体
        //   2) 配置表里读 Skill 行的 rangeMm（读不到就当没有射程信息）
        //   3) 距离用 long 平方和后开方，只做显示与提示用
        // 上游：SendSkillCast
        // 下游：SnapshotView.FindHero / Entities、ConfigMgr.TryGet
        private bool TryMeasureTarget(int skillId, int targetId, out int distanceMm, out int rangeMm)
        {
            distanceMm = 0;
            rangeMm = 0;

            if (m_feed == null || m_feed.View == null)
            {
                return false;
            }

            EntitySnapshot hero = m_feed.View.FindHero(m_session.PlayerId);

            if (hero == null)
            {
                return false;
            }

            IReadOnlyList<EntitySnapshot> entities = m_feed.View.Entities;
            EntitySnapshot target = null;

            for (int i = 0; i < entities.Count; i++)
            {
                if (entities[i].EntityId == targetId)
                {
                    target = entities[i];
                    break;
                }
            }

            if (target == null)
            {
                return false;
            }

            ConfigMgr configs = ConfigMgr.Instance;
            SkillConfig skills;

            if (configs != null && configs.Source != null && configs.TryGet(out skills))
            {
                Config_Skill row;

                if (skills.TryGet(skillId, out row))
                {
                    rangeMm = row.rangeMm;
                }
            }

            long dx = target.PosXMm - hero.PosXMm;
            long dz = target.PosZMm - hero.PosZMm;
            distanceMm = (int)Math.Sqrt((double)(dx * dx + dz * dz));

            return rangeMm > 0;
        }

        // 功能：找离英雄最近的活着的怪物。
        // 入参：无
        // 返回：实例编号；没有英雄或没有活怪时返回 0
        // 步骤：
        //   1) 先拿自己的英雄当原点（拿不到就返回 0）
        //   2) 遍历快照里的实体，只认"活着且 Kind != 0"的，比较平方距离
        // 上游：SendSkillCast
        // 下游：SnapshotView.Entities / FindHero
        private int FindNearestEnemyId()
        {
            EntitySnapshot hero = m_feed.View.FindHero(m_session.PlayerId);

            if (hero == null)
            {
                return 0;
            }

            IReadOnlyList<EntitySnapshot> entities = m_feed.View.Entities;
            long best = long.MaxValue;
            int bestId = 0;

            for (int i = 0; i < entities.Count; i++)
            {
                EntitySnapshot entity = entities[i];

                if (!entity.Alive || entity.Kind == 0)
                {
                    continue;
                }

                long dx = entity.PosXMm - hero.PosXMm;
                long dz = entity.PosZMm - hero.PosZMm;
                long squared = dx * dx + dz * dz;

                if (squared < best)
                {
                    best = squared;
                    bestId = entity.EntityId;
                }
            }

            return bestId;
        }

        // 功能：告诉调用方本地英雄是否还活着。
        // 入参：无。
        // 返回：true 表示活着；false 表示已倒下（或还没收到快照时按 true）。
        // 步骤：直接返回缓存值，缓存每帧在 TrackHeroHealth 里更新。
        // 上游/下游：PlayerDeathFlow.Update 读它；TrackHeroHealth 写它。
        public bool HeroAlive
        {
            get { return m_presence.HeroAlive; }
        }

        // 功能：告诉调用方当前快照里有几个英雄。
        // 入参：无。
        // 返回：英雄实体个数；0 表示还没收到快照。
        // 步骤：直接返回缓存值，缓存每帧在 TrackHeroHealth 里数一遍。
        // 上游/下游：PlayerDeathFlow 用它区分单人/多人副本。
        public int HeroEntityCount
        {
            get { return m_heroEntityCount; }
        }

        // 功能：主动退出当前房间（死亡后回世界用）。
        // 入参：无。
        // 返回：无。
        // 步骤：会话存在就发 LeaveRoomRequest；没有会话时什么都不做（场景切换照常进行）。
        // 上游/下游：PlayerDeathFlow 倒计时结束调用；下游 NetSession.LeaveRoom。
        public void LeaveRoom()
        {
            if (m_session != null)
            {
                m_session.LeaveRoom();
            }
        }

        // 副本结算（R042）：一局结束时服务端只发一次，这里是唯一的转发点。
        public event Action<DungeonResult> DungeonEnded;

        // 功能：处理后端发来的副本结算：弹面板，并把两个按钮接到本驱动的动作上。
        // 入参：result 服务端结算（胜负/用时/击杀/死亡）。
        // 返回：无。
        // 步骤：
        //   1) 先抛事件（外部想看数据可以订阅）
        //   2) 再让面板显示，两个回调分别指向 RestartDungeon / ReturnToWorld
        // 上游/下游：NetSession.DungeonEnded；下游 DungeonResultPanel.Show。
        private void OnDungeonEnded(DungeonResult result)
        {
            Action<DungeonResult> handler = DungeonEnded;

            if (handler != null)
            {
                handler(result);
            }

            // ⚠️ 已经退房 / 已经离开副本时不能再弹结算：
            //    服务端可能在退房那一刻才把这条结算发出来；更常见的是退房后自动重新进房，
            //    结算跟着落到 World 场景里，就是「草地上压着一块失败面板」。
            //    判据用 BindPresentation（= 表现层正绑在副本里），比 IsJoined 更贴合"我现在在不在副本中"。
            if (!m_presence.InDungeon)
            {
                Debug.Log("[结算] 已经离开副本，忽略迟到的结算（" + DungeonOutcomeText.Title(result.Outcome) + "）。");
                return;
            }

            DungeonResultPanel.Ensure().Show(result, RestartDungeon, ReturnToWorld);
        }

        // 功能：再打一局：退房、等一拍、重新进同一副本、重载 Battle。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 关掉结算面板
        //   2) 退房（服务端把这一局清掉）
        //   3) 先暂停自动进房，等 0.5 秒让退房请求被处理
        //   4) 重新允许自动进房，并重载 Battle（新房间天然是新一局）
        // 上游/下游：DungeonResultPanel 的「再来一次」；下游 NetSession.LeaveRoom、SceneFlow.LoadAsync。
        public void RestartDungeon()
        {
            DungeonResultPanel.Ensure().Hide();
            StartCoroutine(RestartRoutine());
        }

        // 功能：回世界：退房、把玩家放回 World 出生点、切到 World 场景。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 关掉结算面板
        //   2) 退房
        //   3) 找到玩家身上的 PlayerDeathFlow，让它把玩家挪回世界出生点
        //   4) 切回 World
        // 上游/下游：DungeonResultPanel 的「返回世界」；下游 NetSession.LeaveRoom、PlayerDeathFlow.TeleportToWorldSpawn、SceneFlow.LoadAsync。
        public void ReturnToWorld()
        {
            DungeonResultPanel.Ensure().Hide();
            StartCoroutine(ReturnToWorldRoutine());
        }

        // 功能：再打一局的协程（退房 → 等一拍 → 重进 → 重载场景）。
        // 入参：无。
        // 返回：协程迭代器。
        // 步骤：见 RestartDungeon 的步骤说明
        // 上游/下游：RestartDungeon 启动。
        private IEnumerator RestartRoutine()
        {
            // 不退房：负责人 2026-11-17 定的口径是「再来一次不算退出房间」——
            //   退房会让房间清空并被删掉，下次进来就是新号（还拿回同一局死光的怪）。
            //   这里只请服务端把这一局重置（怪重新刷满），房号与席位都不变。
            if (m_session != null)
            {
                m_session.RestartDungeon();
            }

            m_restartWentAt = Time.realtimeSinceStartup;
            m_restartWatching = true;
            Debug.Log("[重开] 已请求服务端重开这一局（t=" + m_restartWentAt.ToString("F2") + "s）");

            // ⚠️ 不重载场景：副本场景里除了怪什么都没有，重开只是换一局战斗世界。
            //    原来走 SceneFlow.LoadAsync(Battle) 要重新反序列化整个场景（实测 1~2 秒），
            //    表现就是「点完按钮怪要等好几秒才出现」。这里改成原地清表现层：
            //    等新快照到（30Hz，两三个 tick）→ 清槽位与表现层 → 下一帧按新快照重建。
            yield return new WaitForSecondsRealtime(0.25f);

            // 重开这一局：上一局可能刚亮过死亡黑幕（比如输了之后点「再来一次」），一并收掉
            NBC.Game.UI.DeathScreenOverlay.HideIfShowing();

            // 新一局马上就会发来「活着的英雄」，这里先把死亡标记擦掉：
            //   不清的话 PlayerDeathFlow 会拿上一局的 false 立刻又进倒计时、黑幕再亮一次。
            m_presence.SetHeroAlive(true);
            ReviveLocalPlayerAfterRestart();

            if (m_binder != null)
            {
                m_binder.ClearSlots();
            }

            m_feed.Clear();

            Debug.Log("[重开] 已原地重置表现层（不重载场景），等新快照重建单位");
        }

        // 功能：新一局开始时把本地玩家的死亡表现复位（角色从「倒在地上」站起来）。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 优先找 PlayerDeathFlow：它管着倒计时、黑幕与旁观三件事，只复活状态机会留下半套状态
        //   2) 没有死亡流程组件就退回只让大脑复活（至少不会一直躺着）
        // 上游/下游：「再来一次」的协程调用；下游 PlayerDeathFlow.ReviveAfterRestart、PlayerBrain.NotifyRevive。
        private void ReviveLocalPlayerAfterRestart()
        {
            PlayerDeathFlow flow = UnityEngine.Object.FindObjectOfType<PlayerDeathFlow>();

            if (flow != null)
            {
                flow.ReviveAfterRestart();
                return;
            }

            if (m_playerBrain == null)
            {
                m_playerBrain = UnityEngine.Object.FindObjectOfType<PlayerBrain>();
            }

            if (m_playerBrain != null)
            {
                m_playerBrain.NotifyRevive();
            }
        }

        // 功能：回世界的协程（退房 → 关表现层 → 销毁带进副本的玩家 → 切 World）。
        // 入参：无。
        // 返回：协程迭代器。
        // 步骤：见 ReturnToWorld 的步骤说明
        // 上游/下游：ReturnToWorld 启动。
        private IEnumerator ReturnToWorldRoutine()
        {
            // ⚠️ 先把副本进出的四个开关一次性打到「在世界」：
            //    以前只发 LeaveRoom，没关 AutoJoin ⇒ 退房后 RoomId 变空，下一帧 Update 又自动进了房，
            //    人站在草地上却还挂在副本里（结算面板 / 死亡黑幕跟着冒出来）。
            //    现在只允许走 ExitDungeon 这一个入口，不许零散改字段。
            ExitDungeon();

            // ⚠️ 回世界前先把「表现层」彻底关掉：
            //    切场景会把 Battle 里的绑定器连槽位一起销毁，但驱动是跨场景常驻的，
            //    `m_feed` 里还留着上一局的实体快照 ⇒ 下一帧 `RefreshPresentation` 会在
            //    **World 场景里重新建一个绑定器**，把副本里的狼当成 World 的单位画出来。
            m_feed.Clear();
            CloseHud();

            // ⚠️ 死亡黑幕要在这里收掉：胜利结算时可能有一块「你已倒下」还亮着，
            //    它是 DontDestroyOnLoad 的 Canvas 下的面板，切场景不会自己消失。
            NBC.Game.UI.DeathScreenOverlay.HideIfShowing();
            NBC.Game.UI.DungeonResultPanel.HideIfShowing();

            if (m_binder != null)
            {
                m_binder.ClearSlots();
                m_binder = null;
            }

            // ⚠️ World 场景自带一份 `[Player_Hero]`；带进来的这份如果留着，同屏就是两个玩家
            //    （两套输入、两套动画，看起来像「闪了一下两个」）。回世界前把它销毁，
            //    交给 World 场景自带的那份；下次进副本时由 DungeonEntrance 重新标记。
            PlayerPersist.ReleasePersistent();

            yield return new WaitForSecondsRealtime(0.2f);

            m_presence.ClearJoinRequested();

            SceneFlow.Instance.LoadAsync(GameScene.World, OnDungeonSceneLoaded);
        }

        // 功能：场景切完后的统一回调（目前只有「返回世界」走这里）。
        // 入参：ok 是否成功；message 失败原因。
        // 返回：无。
        // 步骤：失败只打日志；成功后把摄像机重新接到玩家身上（新场景里的 Main Camera 是场景自带的，
        //       不接的话镜头会停在默认位置，看起来离玩家很远）
        // 上游/下游：SceneFlow.LoadAsync 的回调。
        private void OnDungeonSceneLoaded(bool ok, string message)
        {
            if (!ok)
            {
                Debug.LogWarning("[RuntimeNetDriver] 结算后切场景失败：" + message);
                return;
            }

            m_binder = null;
            m_feed.Clear();
            CloseHud();

            // 换场后再关一次两个「全屏面板」：闭包里有异步（面板是异步加载的），
            // 上一帧关掉、这一帧才加载完的实例会自己亮起来。
            NBC.Game.UI.DeathScreenOverlay.HideIfShowing();
            NBC.Game.UI.DungeonResultPanel.HideIfShowing();

            // World 场景不需要表现层（没人观战）；下次进副本时 DungeonEntrance 会重新打开它。
            m_presence.MarkInDungeon(false);
            PlayerMove player = FindObjectOfType<PlayerMove>();

            if (player != null)
            {
                PlayerPersist.BindCamera(player.gameObject);
            }
        }

        // 功能：设置敌人模型 prefab（必须在本组件 Start 之前设置，或在 Play 前配好）。
        // 入参：prefab 敌人模型；null = 回落胶囊
        // 返回：无
        // 步骤：直接赋值；表现层下次 Bind 时取用
        // 上游：HpBarDemoBehaviour 装配时
        // 下游：SnapshotSceneBinder.Ensure
        public GameObject MonsterPrefab
        {
            get { return m_monsterPrefab; }
            set { m_monsterPrefab = value; }
        }

        // 功能：返回最近一次错误描述。
        // 入参：无
        // 返回：最近一次错误描述（空 = 没出错）
        public string LastError
        {
            get { return m_lastError; }
        }

        // 功能：返回会话层的失败原因（例如「账号登录被拒：没有这个账号」）。
        // 入参：无
        // 返回：失败原因（空 = 会话没失败过）
        // 步骤：直接转发 NetSession.FailureReason；会话还没建时返回空
        // 上游：DungeonEntrance 在连接失败时读取，用来把真实原因显示给玩家
        // 下游：NetSession.FailureReason
        public string FailureReason
        {
            get { return m_session != null ? m_session.FailureReason : string.Empty; }
        }

        // 功能：控制握手成功后是否自动进房。
        // 入参：true = 在线就进房（默认）；false = 只握手，等外部叫进房
        // 返回：属性值
        // 步骤：直接读写序列化字段
        // 上游：DungeonEntrance 在 World 里先设 false，切到 Battle 后再设 true
        // 下游：Update 的进房判断
        public bool AutoJoin
        {
            get { return m_presence.AutoJoin; }
            set { m_autoJoin = value; m_presence.SetAutoJoin(value); }
        }

        // 功能：进副本：把四个开关一次性打到「在副本里」，允许自动进房，并把上一局的死亡表现复位。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 交给 BattlePresence.EnterDungeon
        //   2) 同步序列化字段，免得 Inspector 值与运行值不一致
        //   3) 复位本地玩家的死亡表现：上一局死过的话，状态机还停在 Die、PlayerDeathFlow 的 m_resolved 也还是 true
        //      （true 会让它不再开始倒计时，于是「人躺着、又没黑幕」）
        // 上游/下游：DungeonEntrance.FinishEnter；下游 BattlePresence、PlayerDeathFlow.ReviveAfterRestart。
        public void EnterDungeon()
        {
            m_presence.EnterDungeon();
            m_autoJoin = m_presence.AutoJoin;
            m_bindPresentation = m_presence.InDungeon;
            ClearStalePresentation();

            // 管道里还排着上一局的快照：在下一个席位表到达之前，一张都别用，
            // 否则新一局第一帧就照着上一局的尸体把玩家判死。
            if (m_session != null)
            {
                m_session.DiscardSnapshotsUntilRoomState();
            }

            Debug.Log("[副本] 进副本（第 " + Time.frameCount + " 帧）" + m_presence.ToString());
            ReviveLocalPlayerAfterRestart();
        }

        // 功能：清掉上一局留在表现层的快照，免得第一帧就按「上一局那个已经死了的英雄」判死。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 清快照视图（里面的英雄实例还是上一局那个，Alive=false）
        //   2) 清单位槽位，等新快照重建
        //   3) 复位血量记账，否则第一帧的「掉血飘字」会拿上一局的血量当基准
        // 上游/下游：EnterDungeon 调用；下游 PresentationFeed.Clear、SnapshotSceneBinder.ClearSlots。
        private void ClearStalePresentation()
        {
            if (m_feed != null)
            {
                m_feed.Clear();
            }

            if (m_binder != null)
            {
                m_binder.ClearSlots();
            }

            m_heroHpKnown = false;
            m_heroLastHp = 0;
        }

        // 功能：离开副本回世界：退房 + 把四个开关一次性打到「不在副本里」。
        // 入参：无。
        // 返回：无。
        // 步骤：
        //   1) 先关自动进房，再退房 —— 顺序反过来的话，退房让 RoomId 变空，下一帧 Update 会立刻自动重新进房，
        //      服务端就会在你还在切场景时又开一局（表现是「刚回世界就又被塞进副本 / 再进必死」）
        //   2) 退房走 NetSession.LeaveRoom
        //   3) 同步序列化字段，保持 Inspector 值与运行值一致
        // 上游/下游：PlayerDeathFlow.ExitToWorld、RuntimeNetDriver.ReturnToWorldRoutine；下游 BattlePresence.ExitToWorld。
        public void ExitDungeon()
        {
            m_presence.ExitToWorld();
            m_autoJoin = m_presence.AutoJoin;
            m_bindPresentation = m_presence.InDungeon;
            Debug.Log("[副本] 离开副本（第 " + Time.frameCount + " 帧）" + m_presence.ToString());
            LeaveRoom();

            // 注意：2026-11-21 加。只要场景里还有**第二个**副本驱动（历史上被 HUD/入口重复建出来过），
            // 我们自己关掉自动进房还不够 —— 那个驱动下一帧看见房间空了就会立刻重新进房，
            // 于是人已经在 World 里，服务端却还在副本里操控你的英雄（表现：World 里一直看到自己掉血）。
            // 所以离开副本时把**所有同一副本的驱动**一起关掉自动进房；进副本时由 EnterDungeon 只打开自己的。
            RuntimeNetDriver[] drivers = FindObjectsOfType<RuntimeNetDriver>();

            for (int i = 0; i < drivers.Length; i++)
            {
                RuntimeNetDriver other = drivers[i];

                if (other != null && other.m_dungeonId == m_dungeonId && other.AutoJoin)
                {
                    other.AutoJoin = false;
                    Debug.LogWarning("[副本] 关掉另一个副本驱动「" + other.gameObject.name + "」的自动进房（同副本只该有一个）");
                }
            }
        }

        // 功能：控制是否把快照画到场景里、并纠偏本地英雄。
        // 入参：true = 正常表现（默认）；false = 只连服不画东西
        // 返回：属性值
        // 步骤：直接读写序列化字段
        // 上游：DungeonEntrance 在 World 里设 false，进 Battle 后设 true
        // 下游：RefreshPresentation 的首行判断
        public bool BindPresentation
        {
            get { return m_presence.InDungeon; }
            set { m_bindPresentation = value; m_presence.MarkInDungeon(value); }
        }

        // 功能：在 Start 之前改连接参数（入口脚本用）。
        // 入参：host 服务器地址；port 端口；dungeonId 副本编号
        // 返回：无
        // 步骤：直接写序列化字段；账号密码沿用 Inspector 里的开发账号
        // 上游：DungeonEntrance.BeginEnter
        // 下游：Start 建会话时读取
        public void ConfigureSession(string host, int port, int dungeonId)
        {
            if (!string.IsNullOrEmpty(host))
            {
                m_host = host;
            }

            if (port > 0)
            {
                m_port = port;
            }

            if (dungeonId > 0)
            {
                m_dungeonId = dungeonId;
            }
        }

        // 功能：开机自检——场景里出现多个 RuntimeNetDriver 时，把名字与进房状态列出来。
        // 入参：无（Unity 引擎调用）
        // 返回：无
        // 步骤：数一遍场景里的驱动；拼「名字（已进房/未进房）」；只有两个以上都进房才算真冲突
        // 上游：Unity 引擎
        // 下游：RuntimeNetDriver.FindJoined（真正的挑选规则在那边）
        // 注意：Login 的 [MenuSession] 会常驻，进副本又建 [DungeonSession]，
        //       所以「两个驱动、都没进房」是正常现象 —— 只提示，不报错；
        //       真冲突是「两个都已进房」，那时发包一定会投错。
        private void Awake()
        {
            RuntimeNetDriver[] all = FindObjectsOfType<RuntimeNetDriver>();

            if (all.Length <= 1)
            {
                return;
            }

            var names = new System.Text.StringBuilder();
            int joined = 0;

            foreach (RuntimeNetDriver driver in all)
            {
                if (driver == null)
                {
                    continue;
                }

                if (driver.IsJoined)
                {
                    joined++;
                }

                if (names.Length > 0)
                {
                    names.Append("、");
                }

                names.Append(driver.gameObject.name).Append(driver.IsJoined ? "（已进房）" : "（未进房）");
            }

            string message = "[Net] 场景里有 " + all.Length + " 个 RuntimeNetDriver：" + names;

            if (joined > 1)
            {
                Debug.LogError(message + " —— 有多个已进房，发包会投错！", this);
                return;
            }

            Debug.Log(message + "（正常：常驻的菜单会话 + 新进的副本会话；发包统一走 RuntimeNetDriver.FindJoined()）", this);
        }

        // 功能：建传输层与会话，订阅状态事件，并发起连接。
        // 入参：无（引擎在开始帧调一次）
        // 返回：无
        // 步骤：
        //   1) 建表现层数据源
        //   2) 把传输层包上统计装饰器（HUD 的带宽靠它）
        //   3) 建会话并订阅状态/握手事件
        //   4) 发起连接（非阻塞，连上与否靠每帧 Pump 推进）
        // 上游：Unity 引擎
        // 下游：CountingTransport、NetSession.Connect
        private void Start()
        {
            // Inspector 上的初值要灌进状态表：之后所有读写都走 m_presence，避免两套真值。
            m_presence.SetAutoJoin(m_autoJoin);
            m_presence.MarkInDungeon(m_bindPresentation);

            // 注意：2026-11-21 加的硬约束 —— 一个副本只允许一个驱动。
            // 场景里已经有「[DungeonSession]」（副本入口建的那个）时，别的驱动必须自己让位：
            // 既不自动进房、也不接管表现层（否则它会一边进房一边发输入，人回 World 后英雄还在副本里挨打）。
            // 之所以要在这里兜底：进副本时驱动是「先建对象、后连服进房」，任何按 IsJoined 判断的代码
            // 都可能看不见它，从而又建一个。
            RuntimeNetDriver[] others = FindObjectsOfType<RuntimeNetDriver>();

            for (int i = 0; i < others.Length; i++)
            {
                RuntimeNetDriver other = others[i];

                if (other == null || other == this)
                {
                    continue;
                }

                if (other.gameObject.name == "[DungeonSession]" && gameObject.name != "[DungeonSession]")
                {
                    AutoJoin = false;
                    BindPresentation = false;
                    Debug.LogWarning("[Net] 场景里已有副本会话，驱动「" + gameObject.name + "」自动让位（不进房、不接管表现层）", this);
                    break;
                }
            }

            m_feed = new PresentationFeed();
            m_reconnector = new NetReconnector(5, m_dungeonId);

            ITransport transport = new CountingTransport(new TcpTransport());

            // 先清零：上一次运行（或 Editor 窗口）留下的统计不该带过来
            NetRuntimeStats.Reset();

            m_session = new NetSession(
                transport,
                "runtime-" + m_account,
                "unity-runtime/" + Application.unityVersion,
                NetSession.DefaultHeartbeatIntervalMs,
                NetSession.DefaultHeartbeatTimeoutMs,
                NetSession.DefaultHandshakeTimeoutMs,
                m_account,
                m_password);

            m_session.StateChanged += OnStateChanged;
            m_session.HandshakeCompleted += OnHandshakeCompleted;
            m_session.DungeonEnded += OnDungeonEnded;

            m_session.Connect(m_host, m_port);
        }

        // 功能：每帧驱动会话，并在需要时进房、发输入、喂表现层、写统计。
        // 入参：无（引擎每帧调）
        // 返回：无
        // 步骤：
        //   1) 会话没了就返回
        //   2) Pump：收字节 → 拆帧 → 处理消息（锁步帧在这一步就步进了）→ 心跳
        //   3) 在线且没进房 ⇒ 发一次进房请求
        //   4) 已进房 ⇒ 按节流发本地输入
        //   5) 选数据源并喂表现层 + 缓存 3D 绑定器
        //   6) 写 HUD 统计（模式 / RTT / 帧号）
        // 上游：Unity 引擎
        // 下游：NetSession.Pump / JoinRoom / SendInput / SendLockstepInput、PresentationFeed、SnapshotSceneBinder、NetRuntimeStats
        // 注意：deltaMs 必须是真实毫秒。传固定值会把会话内部时钟拖慢，心跳与超时全偏
        private void Update()
        {
            if (m_session == null)
            {
                return;
            }

            int deltaMs = Mathf.RoundToInt(Time.deltaTime * 1000f);
            deltaMs = Mathf.Min(deltaMs, MaxPumpDeltaMs);
            m_session.Pump(deltaMs);

            DriveReconnect();

            if (m_presence.AutoJoin && m_session.IsOnline && !m_presence.JoinRequested)
            {
                m_presence.MarkJoinRequested();

                // 房号留空 = 服务端安排；副本编号决定用哪张图
                Debug.Log("[副本] 自动进房（第 " + Time.frameCount + " 帧）" + m_presence.ToString());
                m_session.JoinRoom(string.Empty, m_dungeonId);
            }

            // 必须同时「在线」才发输入：InRoom 只看房间字段，断线后它仍是 true（见 NetSession.InRoom）
            if (m_session.IsOnline && m_session.InRoom)
            {
                NoteJoinedNow();
                PumpInput();
            }

            RefreshPresentation();
            ReconcileHeroPosition();
            PushStats();
        }

        // 功能：把客户端英雄往服务端位置"拉回"，并限制在场地范围内。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没在线/没快照就返回
        //   2) 取我的英雄快照（服务端权威位置）
        //   3) 找 PlayerMove（懒加载）
        //   4) 漂移小（< 0.3 米）不动；大（> 2 米）直接瞬移；中间按速度靠拢
        //   5) 无论是否漂移，都把英雄夹在 ±5 米内（与服务端 PlayAreaHalfExtentMm 一致）
        // 上游：Update
        // 下游：PlayerMove.transform
        // 注意：这里只做"跟随式纠偏"，不是预测回滚；帧同步模式不要用它（模式判断在调用处）
        private void ReconcileHeroPosition()
        {
            if (m_session == null || !m_session.IsOnline || m_feed == null || m_feed.View == null)
            {
                return;
            }

            // 先按「归属玩家」找英雄，**不看死活**：死亡后没快照是正常的，不该刷警告
            EntitySnapshot hero = null;
            IReadOnlyList<EntitySnapshot> entities = m_feed.View.Entities;

            for (int i = 0; i < entities.Count; i++)
            {
                EntitySnapshot candidate = entities[i];

                if (candidate.Kind == 0 && candidate.OwnerPlayerId == m_session.PlayerId)
                {
                    hero = candidate;
                    break;
                }
            }

            if (hero == null)
            {
                // 切场景 / 重进副本的间隙里还没进房，本来就没有实体 —— 这不算异常，别刷警告。
                if (!IsJoined)
                {
                    return;
                }

                // 诊断：真的认不出自己的英雄时，位置纠偏等于没开（会表现为"红圈外也挨打"）
                if (Time.time - m_lastHeroMissingLogTime > 5f)
                {
                    m_lastHeroMissingLogTime = Time.time;
                    Debug.LogWarning("[位置诊断] 找不到我的英雄快照：PlayerId=" + m_session.PlayerId
                        + "，在线=" + m_session.IsOnline + "，实体数=" + m_feed.View.EntityCount);
                }

                return;
            }

            // 死了就停下（不纠偏、也不打警告）
            if (hero.EntityId <= 0 || !hero.Alive)
            {
                return;
            }

            if (m_playerMove == null)
            {
                m_playerMove = UnityEngine.Object.FindObjectOfType<PlayerMove>();
            }

            if (m_playerMove == null)
            {
                return;
            }

            Transform heroTransform = m_playerMove.transform;
            Vector3 server = new Vector3(hero.PosXMm * 0.001f, heroTransform.position.y, hero.PosZMm * 0.001f);
            Vector3 current = new Vector3(heroTransform.position.x, heroTransform.position.y, heroTransform.position.z);
            float drift = Vector3.Distance(
                new Vector3(current.x, 0f, current.z),
                new Vector3(server.x, 0f, server.z));

            // 诊断：漂移超过 0.5 米就打一条（每秒最多一条），用来判断"红圈外挨打"是不是位置没对齐
            if (drift > 0.5f && Time.time - m_lastDriftLogTime > 1f)
            {
                m_lastDriftLogTime = Time.time;
                Debug.LogWarning("[位置诊断] 英雄漂移 " + drift.ToString("0.00") + " 米：客户端("
                    + current.x.ToString("0.00") + "," + current.z.ToString("0.00") + ") 服务端("
                    + server.x.ToString("0.00") + "," + server.z.ToString("0.00") + ")");
            }

            if (drift > HeroSnapDistanceM)
            {
                heroTransform.position = server;
            }
            else if (drift > HeroDriftDeadzoneM)
            {
                float step = Mathf.Min(drift, HeroReconcileSpeedMps * Time.deltaTime);
                heroTransform.position = Vector3.MoveTowards(
                    new Vector3(current.x, heroTransform.position.y, current.z),
                    new Vector3(server.x, heroTransform.position.y, server.z),
                    step);
            }

            // 场地边界：客户端的移动是本地积分，不夹住就会走出服务端场地，导致"怪在远处也能打你"
            float half = PlayAreaHalfExtentMm * 0.001f;
            Vector3 pos = heroTransform.position;
            float clampedX = Mathf.Clamp(pos.x, -half, half);
            float clampedZ = Mathf.Clamp(pos.z, -half, half);

            if (Mathf.Abs(clampedX - pos.x) > 0.0001f || Mathf.Abs(clampedZ - pos.z) > 0.0001f)
            {
                heroTransform.position = new Vector3(clampedX, pos.y, clampedZ);
            }
        }
        // 功能：每帧把会话状态喂给重连策略，并执行它给的下一步动作。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 没有策略就返回（Start 之前不会发生）
        //   2) 取策略的下一步动作与放弃原因
        //   3) 按动作执行：Connect 重连、JoinRoom 回原房、GiveUp 记错误并停手
        //   4) 阶段变了就打一行日志（只在变化时打，避免刷屏）
        // 上游：Update（Pump 之后）
        // 下游：NetSession.Connect / JoinRoom、NetReconnector.Tick
        // 注意：Connect 用的是同一个 NetSession —— 账号与密码摘要在构造时就固定，不需要也不应该重建会话
        private void DriveReconnect()
        {
            if (m_reconnector == null)
            {
                return;
            }

            bool online = m_session.State == ESessionState.Online;
            string reason;

            // 把会话状态（而不是布尔「在线」）喂给策略：它要区分「连接进行中」与「连接已失败」，
            // 否则会对着一个还在 Connecting 的会话重复 Connect，直接抛「已经在连」
            EReconnectAction action = m_reconnector.Tick(m_session.State, m_session.InRoom, Time.deltaTime, out reason);

            if (action == EReconnectAction.Connect)
            {
                Debug.Log("[Reconnect] 第 " + m_reconnector.Attempt + " 次重连：" + m_host + ":" + m_port);
                m_session.Connect(m_host, m_port);
            }
            else if (action == EReconnectAction.JoinRoom)
            {
                Debug.Log("[Reconnect] 已重连，回房：" + m_reconnector.LastRoomId);
                m_session.JoinRoom(m_reconnector.LastRoomId, m_dungeonId);
            }
            else if (action == EReconnectAction.Reset)
            {
                // 连接卡住超过 3 秒：先断掉，下一帧状态回到 Disconnected 才允许再 Connect
                Debug.LogWarning("[Reconnect] 连接超时，重置会话后重试");
                m_session.Disconnect("重连超时，重置连接");
            }
            else if (action == EReconnectAction.GiveUp)
            {
                m_lastError = reason;
                Debug.LogWarning("[Reconnect] 放弃重连：" + reason);
            }

            // 断线期间要把「在房」标记清掉，这样重连回来时才算一次「重新进房」（表现层才重置）。
            // ⚠️ 判据必须是「是否在线」：InRoom 只看房间字段，断开后它仍然是 true，用它判永远清不掉
            if (!online)
            {
                m_wasInRoom = false;
            }
        }

        // 功能：进房成功时把房号记给重连策略，并在「这是重连回来的」时重置表现层。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 房号为空（服务端还没下发 RoomState）就直接返回
        //   2) 之前已在房里 ⇒ 说明是第一次进房，只记房号
        //   3) 之前不在房里 ⇒ 这是重连回来：清表现层缓存、让绑定器重建
        //   4) 记房号（策略据此在下次断线时回原房）
        // 上游：Update（InRoom 为真时每帧调）
        // 下游：PresentationFeed.Clear、NetReconnector.NoteJoined
        // 注意：这里必须清表现层 —— 上一局的帧号与换局计数会让新会话被判成「帧号倒退」或「假分歧」
        private void NoteJoinedNow()
        {
            if (!m_session.InRoom || m_reconnector == null)
            {
                return;
            }

            string roomId = m_session.CurrentRoom == null ? string.Empty : m_session.CurrentRoom.RoomId;

            if (string.IsNullOrEmpty(roomId))
            {
                return;
            }

            if (!m_wasInRoom)
            {
                m_wasInRoom = true;

                // 阶段还在重连三态之一 ⇒ 这次进房是重连回来的，必须清表现层
                EReconnectPhase phase = m_reconnector.Phase;
                bool rejoined = phase == EReconnectPhase.Waiting
                    || phase == EReconnectPhase.Connecting
                    || phase == EReconnectPhase.Joining;

                if (rejoined)
                {
                    m_feed.Clear();
                    m_binder = null;
                    Debug.Log("[Reconnect] 已回到房间 " + roomId + "，表现层已重置");
                }
            }

            m_reconnector.NoteJoined(roomId);
        }
        // 功能：退订事件、释放会话，并把统计清零。
        // 入参：无
        // 返回：无
        // 上游：Update
        // 下游：NetSession.Disconnect、NetRuntimeStats.Reset
        private void OnDestroy()
        {
            m_presence.ClearJoinRequested();
            m_wasInRoom = false;

            if (m_reconnector != null)
            {
                m_reconnector.NoteIntentionalDisconnect();
            }

            if (m_session != null)
            {
                m_session.StateChanged -= OnStateChanged;
                m_session.HandshakeCompleted -= OnHandshakeCompleted;
                m_session.Dispose();
                m_session = null;
            }

            NetRuntimeStats.Reset();
        }

        // 功能：处理攻击键 —— 按下就播动画并开一个判定窗口；窗口内用剑的碰撞盒采样，**碰到才发请求**。
        // 入参：无
        // 返回：true = 本帧消费了这次按键（调用方这一帧不要再发普通输入，避免把攻击位裸发出去）
        // 步骤：
        //   1) 读输入；本帧按下攻击位且不在窗口内 ⇒ 播攻击动画、开窗、返回 true
        //   2) 窗口开着时，过了起始延迟就开始采样剑的碰撞盒（只采一次，采样到就发）
        //   3) 碰到怪 ⇒ SendInput(攻击位 + 目标)；没碰到 ⇒ 什么都不发（这一刀打空）
        //   4) 超过窗口结束时间 ⇒ 关窗
        // 上游：PumpInput
        // 下游：PlayerAnimation.PlayAttack、FindSwordHitTarget、NetSession.SendInput
        // 注意：**没有「剑没碰到就退回最近怪」的兜底** —— 负责人明确要求「没碰到就是没打中」，
        //       旧版那个兜底会让服务端在剑没接触时也算命中。
        private bool HandleAttack()
        {
            InputManager input = InputManager.Instance;

            if (input == null || !input.IsEnabled)
            {
                return false;
            }

            InputCommand command = input.Current;
            float now = Time.time;

            // ① 开窗：只在「本帧新按下」且冷却够了的时候
            if ((command.ActionBits & AttackBitMask) != 0u && now >= m_attackNextAllowed)
            {
                m_attackWindowOpen = true;
                m_attackWindowStart = now;
                m_attackSent = false;
                m_attackNextAllowed = now + AttackWindowEnd;

                if (m_playerAnimation == null)
                {
                    m_playerAnimation = UnityEngine.Object.FindObjectOfType<PlayerAnimation>();
                }

                if (m_playerAnimation != null)
                {
                    m_playerAnimation.PlayAttack();
                }

                return true;
            }

            if (!m_attackWindowOpen)
            {
                return false;
            }

            float elapsed = now - m_attackWindowStart;

            // ② 判定窗口内采样一次
            if (!m_attackSent && elapsed >= AttackWindowStart && m_mode != ERuntimeSyncMode.Lockstep)
            {
                int targetId = FindSwordHitTarget();

                if (targetId != 0)
                {
                    // 上行前先把「相机相对轴」换成世界方向，否则服务端按世界轴走、本地按相机轴转身
                    ToWorldInput(command.MoveX, command.MoveY, out int attackWorldX, out int attackWorldY);
                    m_session.SendInput(attackWorldX, attackWorldY, AttackBitMask, 0u, targetId);
                    m_attackSent = true;
                }
            }

            // ③ 窗口结束就关（打空也算结束）
            if (elapsed >= AttackWindowEnd)
            {
                m_attackWindowOpen = false;
            }

            return false;
        }
        // 功能：按节流间隔把本地输入发出去（锁步或状态同步二选一）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 累加时间，不到间隔就不发
        //   2) 读本地输入（InputManager 每帧自己 Tick，这里只取当前值）
        //   3) 按模式二选一发上行（两条链不能同时喂，否则权威数据打架）
        // 上游：Update
        // 下游：NetSession.SendInput / NetSession.SendLockstepInput
        // 功能：用「剑的碰撞盒」找出这一帧被剑碰到的怪（只决定「打谁」，命中仍由服务端裁决）。
        // 入参：无
        // 返回：被碰到的怪里离玩家最近的那个实体编号；剑不存在或没碰到 ⇒ 0
        // 步骤：
        //   1) 第一次调用时找武器骨骼并确保它有一个 Trigger 碰撞体
        //   2) 用该碰撞体的**当帧世界包围盒**做 OverlapBox
        //   3) 对每个相交的碰撞体，往前找 MonsterSlotTag（拿实体编号）
        //   4) 多个命中时取离玩家最近的
        // 上游：HandleAttack（判定窗口内）
        // 下游：Physics.OverlapBox、MonsterSlotTag
        // 注意：只采样一次 —— 剑在动画里会大幅摆动，持续检测会把「扫过」也算命中
        private int FindSwordHitTarget()
        {
            EnsureSwordCollider();

            if (m_swordCollider == null || !m_swordCollider.enabled)
            {
                return 0;
            }

            Bounds box = m_swordCollider.bounds;

            // QueryTriggerInteraction.Collide：剑上的碰撞体是 Trigger，不显式允许就查不到
            Collider[] hits = Physics.OverlapBox(box.center, box.extents, Quaternion.identity, ~0,
                QueryTriggerInteraction.Collide);

            if (hits == null || hits.Length == 0)
            {
                return 0;
            }

            Transform player = m_swordCollider.transform.root;
            int bestId = 0;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < hits.Length; i++)
            {
                MonsterSlotTag tag = hits[i].GetComponentInParent<MonsterSlotTag>();

                if (tag == null || tag.EntityId == 0)
                {
                    continue;
                }

                float distance = Vector3.Distance(player.position, tag.transform.position);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestId = tag.EntityId;
                }
            }

            return bestId;
        }

        // 功能：找到「本地玩家」的根节点，作为找武器骨骼的锚点。
        // 入参：无
        // 返回：玩家根 Transform；还没生成或找不到 ⇒ null
        // 步骤：
        //   1) 优先用已缓存的 PlayerMove（走路组件一直启用）
        //   2) 其次用已缓存的 PlayerBrain（状态机一直启用）
        //   3) 都没有再各查一次场景
        // 上游：EnsureSwordCollider
        // 下游：FindBone
        // 注意：刻意不用 PlayerAnimation —— 它在 World 里会被 PlayerBrain 关掉，FindObjectOfType 默认跳过未激活组件
        private Transform ResolvePlayerRoot()
        {
            if (m_playerMove == null)
            {
                m_playerMove = UnityEngine.Object.FindObjectOfType<PlayerMove>();
            }

            if (m_playerMove != null)
            {
                return m_playerMove.transform;
            }

            if (m_playerBrain == null)
            {
                m_playerBrain = UnityEngine.Object.FindObjectOfType<PlayerBrain>();
            }

            return m_playerBrain != null ? m_playerBrain.transform : null;
        }

        // 功能：找到武器骨骼并准备好一个 Trigger 碰撞体（找不到会按秒重试）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 用 PlayerMove / PlayerBrain 找玩家根节点（不用被关掉的 PlayerAnimation）
        //   2) 深度搜名字含 weapon_sword 且激活的骨骼；找不到退到 hand_r
        //   3) 骨骼上已有碰撞体（负责人会在 prefab 上加）就复用，任意类型都行
        //   4) 一个都没有才自己按 Renderer 包围盒建一个 Trigger 盒
        //   5) 无论来源，运行时都保证它是 Trigger（否则会推动带刚体的怪）
        // 上游：FindSwordHitTarget
        // 下游：Physics.OverlapBox
        // 注意：换模型后若没有这些骨骼，这里会告警一次并每秒重试 —— 结果就是「打不到人」，不会崩
        private void EnsureSwordCollider()
        {
            if (m_swordCollider != null && m_swordCollider.enabled)
            {
                return;
            }

            // 找失败要允许重试：角色是进副本以后才出现的，第一次可能还没生成完
            if (m_swordLooked && Time.unscaledTime < m_swordRetryTime)
            {
                return;
            }

            m_swordLooked = true;
            m_swordRetryTime = Time.unscaledTime + SwordRetrySeconds;

            // 注意：不能用 FindObjectOfType<PlayerAnimation>() 找玩家 —— World 里 PlayerBrain 会把这个旧组件关掉，
            // 而 FindObjectOfType 默认跳过未激活组件，结果就是「剑永远找不到、每刀都打空」。
            // 这里改用始终启用的 PlayerMove / PlayerBrain 当锚点。
            Transform playerRoot = ResolvePlayerRoot();

            if (playerRoot == null)
            {
                if (!m_swordMissingLogged)
                {
                    m_swordMissingLogged = true;
                    Debug.LogWarning("[攻击] 场景里找不到玩家（PlayerMove / PlayerBrain），本刀不判定命中");
                }

                return;
            }

            Transform weapon = FindBone(playerRoot, "weapon_sword");

            if (weapon == null)
            {
                weapon = FindBone(playerRoot, "hand_r");
            }

            if (weapon == null)
            {
                if (!m_swordMissingLogged)
                {
                    m_swordMissingLogged = true;
                    Debug.LogWarning("[攻击] 玩家身上找不到 weapon_sword / hand_r 骨骼，本刀不判定命中");
                }

                return;
            }

            Collider existing = weapon.GetComponent<Collider>();

            if (existing == null)
            {
                existing = weapon.GetComponentInChildren<Collider>(true);
            }

            if (existing == null)
            {
                BoxCollider box = weapon.gameObject.AddComponent<BoxCollider>();
                box.isTrigger = true;

                Renderer[] renderers = weapon.GetComponentsInChildren<Renderer>(true);
                Bounds local = new Bounds(Vector3.zero, new Vector3(0.3f, 0.3f, 1.0f));

                if (renderers != null && renderers.Length > 0)
                {
                    local = renderers[0].bounds;

                    for (int i = 1; i < renderers.Length; i++)
                    {
                        local.Encapsulate(renderers[i].bounds);
                    }
                }

                box.center = weapon.InverseTransformPoint(local.center);
                box.size = local.size;
                existing = box;
            }

            if (!existing.isTrigger)
            {
                existing.isTrigger = true;
                Debug.Log("[攻击] 剑上的 " + existing.GetType().Name + " 不是 Trigger，已在运行时按 Trigger 使用" +
                          "（否则会推动带刚体的怪）。要改请在 prefab 上勾 Is Trigger。", existing);
            }

            m_swordCollider = existing;
        }

        // 功能：在层级里按名字片段找一个激活的骨骼。
        // 入参：root 搜索起点；fragment 名字片段（小写）
        // 返回：第一个命中的 Transform；没有返回 null
        // 步骤：遍历全部子物体（含隐藏），名字转小写比片段，并要求该物体在激活层级里
        // 上游：EnsureSwordCollider
        // 下游：无
        private static Transform FindBone(Transform root, string fragment)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);

            for (int i = 0; i < all.Length; i++)
            {
                if (!all[i].gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (all[i].name.ToLower().Contains(fragment))
                {
                    return all[i];
                }
            }

            return null;
        }
        private void PumpInput()
        {
            // 攻击：按下开窗 + 窗口内用剑的碰撞盒采样（不套 50ms 节流）
            if (HandleAttack())
            {
                m_sendAccumulator = 0f;
                return;
            }

            m_sendAccumulator += Time.deltaTime;

            if (m_sendAccumulator < m_inputSendInterval)
            {
                return;
            }

            m_sendAccumulator = 0f;

            InputManager input = InputManager.Instance;

            if (input == null || !input.IsEnabled)
            {
                return;
            }

            InputCommand command = input.Current;

            // 上行前统一换算成世界方向：服务端 `ApplyMove` 把 move_x / move_y 当世界 +X / +Z 用，
            // 而本地 PlayerMove 是按相机轴转身；相机转过之后不换算，就会出现「位置往左、人朝右」。
            ToWorldInput(command.MoveX, command.MoveY, out int worldX, out int worldY);

            if (m_mode == ERuntimeSyncMode.Lockstep)
            {
                // 锁步：被挡下（没开局/重复帧）会返回 false，这里不当作错误
                m_session.SendLockstepInput(worldX, worldY, command.ActionBits);
            }
            else
            {
                // 注意：这里必须把「攻击位」抹掉。HandleAttack 返回 false 有两种情况：窗口没开，或
                // 「按了但还在冷却里」；后者会让本帧 command.ActionBits 仍然带着攻击位，如果原样发出去，
                // 服务端就会收到一条「目标 = 0」的普攻，日志刷「目标 0 不存在」。
                m_session.SendInput(worldX, worldY, command.ActionBits & ~AttackBitMask, 0u, 0);
            }
        }

        // 功能：把「相机相对」的输入轴换成世界方向，再上行给服务端。
        // 入参：moveX 左右轴（-1000~1000，右为正）；moveY 前后轴（-1000~1000，前为正）。
        // 返回：worldX / worldY 世界方向（-1000~1000，+X / +Z 为正；都为 0 表示没输入）。
        // 步骤：1) 没输入直接给 0 2) 懒加载本地 PlayerMove，找不到就原样发（退化成世界轴）
        //       3) 用 PlayerMove.GetMoveDirection 换算出世界方向，再按量化精度取整。
        // 上游：SendInputNow、HandleAttack
        // 下游：PlayerMove.GetMoveDirection
        // 注意：服务端不认相机，只有客户端知道镜头朝向，所以这一步必须在客户端做。
        private void ToWorldInput(int moveX, int moveY, out int worldX, out int worldY)
        {
            if (moveX == 0 && moveY == 0)
            {
                worldX = 0;
                worldY = 0;
                return;
            }

            if (m_localMove == null)
            {
                m_localMove = FindObjectOfType<PlayerMove>();
            }

            if (m_localMove == null)
            {
                worldX = moveX;
                worldY = moveY;
                return;
            }

            float axisX = (float)moveX / InputCommand.MoveScale;
            float axisZ = (float)moveY / InputCommand.MoveScale;
            Vector3 worldDirection = m_localMove.GetMoveDirection(axisX, axisZ);

            worldX = Mathf.RoundToInt(worldDirection.x * InputCommand.MoveScale);
            worldY = Mathf.RoundToInt(worldDirection.z * InputCommand.MoveScale);
        }

        // 功能：选好数据源并刷新表现层，必要时创建 3D 绑定器。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 把开关交给数据源（决定读快照还是读锁步世界）
        //   2) 刷新（内部处理换局、卡帧回退、不重复喂）
        //   3) 第一次拿到数据时创建/缓存 3D 绑定器
        // 上游：Update
        // 下游：PresentationFeed.Refresh、SnapshotSceneBinder.Ensure
        private void RefreshPresentation()
        {
            if (!m_presence.InDungeon)
            {
                return;
            }

            LockstepClient client = m_session.Lockstep;

            m_feed.UseLockstep = m_mode == ERuntimeSyncMode.Lockstep;

            m_feed.Refresh(
                m_session.PlayerId,
                m_session.World,
                client == null ? null : client.World,
                client == null ? -1 : client.LastSteppedFrame,
                client != null && client.Started,
                m_session.MatchSerial);

            // R020：玩家侧伤害反馈 + 把血量留给 HUD
            if (m_feed.View != null)
            {
                TrackHeroHealth(FindMyHeroSnapshot());
                WatchRestartMonsters();
            }

            if (m_binder == null && m_feed.View != null)
            {
                m_binder = SnapshotSceneBinder.Ensure(m_feed.View, m_session.PlayerId, m_monsterPrefab);
            }
            else if (m_binder != null)
            {
                // 每帧同步玩家编号：绑定只发生一次，而握手完成前它是 0 —— 不同步就永远跳不掉本地英雄
                m_binder.SetOwnerPlayerId(m_session.PlayerId);

                // 每帧同步敌人模型：驱动是跨场景常驻的，而模型由副本场景里的 HpBarDemo 稍后赋值。
                // 不补这一步，第一局永远是胶囊（binder 建槽时模型还是 null），第二局才变狼。
                m_binder.SetMonsterPrefab(m_monsterPrefab);
            }

            EnsureHud();
        }

        // 功能：第一次拿到快照时把 HUD 驱动接上（只接一次）。
        // 入参：无（读 m_feed / m_session）。
        // 返回：无。
        // 步骤：1) 已经接过就直接返回 2) 没有就挂组件 3) Bind 并记下已接。
        // 上游：RefreshPresentation。
        // 下游：RuntimeHudDriver.Bind。
        private void EnsureHud()
        {
            if (m_hudBound || m_feed == null || m_feed.View == null || m_session == null)
            {
                return;
            }

            if (m_boot == null)
            {
                // 把「装资源 → 设配置来源 → 等表 → 挂 HUD」写成一条链：
                // 顺序集中在一处，某一步没好就原地等，超时会打一条能直接定位的日志。
                m_boot = new BattleBootSequence(
                    () => BattleReadiness.AssetsReady,
                    () => BattleReadiness.ConfigSourceReady,
                    () => BattleReadiness.HudTablesReady,
                    () => BattleConfigBootstrap.Ensure(reason => Debug.LogWarning("[Boot] 预加载配置表失败：" + reason)),
                    BindHud,
                    message => Debug.Log(message));

                m_boot.Failed += reason => Debug.LogWarning("[Boot] " + reason);
                m_boot.Begin();
            }

            m_boot.Tick(Time.deltaTime);
        }

        // 功能：把 HUD 驱动接上快照源（既有的挂组件 + Bind）。
        // 入参：无（读 m_feed / m_session）。
        // 返回：无。
        // 步骤：1) 没有驱动就挂组件 2) Bind 3) 记下已接。
        // 上游：BattleBootSequence 最后一步；下游：RuntimeHudDriver.Bind。
        private void BindHud()
        {
            if (m_hudDriver == null)
            {
                m_hudDriver = GetComponent<RuntimeHudDriver>();

                if (m_hudDriver == null)
                {
                    m_hudDriver = gameObject.AddComponent<RuntimeHudDriver>();
                }
            }

            m_hudDriver.Bind(m_feed, m_session);
            m_hudBound = true;
        }

        // 功能：收起 HUD（回世界、切场景前调；重复调无害）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 没接过就直接返回 2) CloseAll 并清标记（下次进副本再 Bind）。
        // 上游：ReturnToWorldRoutine、OnDungeonSceneLoaded。
        // 下游：RuntimeHudDriver.CloseAll。
        private void CloseHud()
        {
            if (!m_hudBound)
            {
                return;
            }

            if (m_hudDriver != null)
            {
                m_hudDriver.CloseAll();
            }

            // 编排链跟着这一局一起作废：下次进副本要重新等资源、重新等表
            if (m_boot != null)
            {
                m_boot.Cancel();
                m_boot = null;
            }

            m_hudBound = false;
        }

        // 功能：诊断用 —— 「再来一次」之后，第一张带活怪的快照到达时打一条总耗时。
        // 入参：无（读 m_feed.View）
        // 返回：无
        // 步骤：
        //   1) 不在观察期就直接返回（平时每帧都走这一句，零分配）
        //   2) 数一遍活着、且不是英雄的实体
        //   3) 数到 > 0 就停在第一条日志上，结束观察
        // 上游：RefreshPresentation 每帧
        // 下游：无
        private void WatchRestartMonsters()
        {
            if (!m_restartWatching || m_feed.View == null)
            {
                return;
            }

            IReadOnlyList<EntitySnapshot> entities = m_feed.View.Entities;
            int alive = 0;

            for (int i = 0; i < entities.Count; i++)
            {
                EntitySnapshot e = entities[i];

                if (e.Alive && e.Kind == 1)
                {
                    alive++;
                }
            }

            if (alive <= 0)
            {
                return;
            }

            m_restartWatching = false;
            Debug.Log("[重开] 第一张含活怪的快照到了：距点击 " +
                      (Time.realtimeSinceStartup - m_restartWentAt).ToString("F2") + "s，活怪 " + alive + " 只");
        }
        // 功能：把模式、RTT、帧号写进 HUD 只读的静态统计。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 模式与 RTT 直接取会话的
        //   2) 帧号按模式取：状态同步 = 快照 tick；帧同步 = 已步进帧
        // 上游：Update
        // 下游：NetRuntimeStats（HUD 只读它）
        // 注意：不写这里，HUD 会一直显示「离线」—— 它只读静态统计，不认识会话
        // 功能：按「归属玩家」找我的英雄快照，**包括已经倒下的**。
        // 入参：无
        // 返回：我的英雄快照；还没生成或不在视野里返回 null
        // 步骤：
        //   1) 没会话/没表现层数据就返回 null
        //   2) 遍历实体，按 Kind == 0 且 OwnerPlayerId == 我的 PlayerId 找
        //   3) 找到就返回（不看 Alive）
        // 上游：RefreshPresentation（喂 TrackHeroHealth）
        // 下游：SnapshotView.Entities
        // 注意：不能用 SnapshotView.FindHero —— 它只返回活着的英雄，英雄一死就返回 null，
        //       于是 TrackHeroHealth 提前返回、HeroAlive 永远停在 true，结果「血量掉光但没有死亡流程」。
        private EntitySnapshot FindMyHeroSnapshot()
        {
            if (m_session == null || m_feed == null || m_feed.View == null)
            {
                return null;
            }

            IReadOnlyList<EntitySnapshot> entities = m_feed.View.Entities;

            for (int i = 0; i < entities.Count; i++)
            {
                EntitySnapshot candidate = entities[i];

                if (candidate.Kind == 0 && candidate.OwnerPlayerId == m_session.PlayerId)
                {
                    return candidate;
                }
            }

            return null;
        }

        // 功能：盯住本地英雄的血量 —— 掉血时飘伤害数字 + 播受击，并把最新血量留给 HUD。
        // 入参：hero 本地英雄的快照（可为 null）
        // 返回：无
        // 步骤：
        //   1) 没有数据就返回（还没进房 / 英雄还没生成）
        //   2) 首帧只记基线，避免一进场就飘一个「满血」数字
        //   3) 掉血 ⇒ DamagePopup + PlayHit；随后把最新血量写进 HeroHp/HeroMaxHp（HUD 读）
        // 上游：RefreshPresentation
        // 下游：DamagePopup.Show、PlayerAnimation.PlayHit、HeroHp / HeroMaxHp
        // 注意：只处理「血变少」；治疗不飘字（本版不做治疗数字）
        private void TrackHeroHealth(EntitySnapshot hero)
        {
            if (hero == null)
            {
                return;
            }

            EnsureDeathFlow();

            HeroHp = hero.Hp;
            HeroMaxHp = hero.MaxHp;
            m_presence.SetHeroAlive(hero.Alive);
            m_heroEntityCount = CountHeroEntities();

            // 血量为 0：让本地角色进死亡状态（只切一次，PlayerBrain 自己会拒绝重复请求）
            if (!hero.Alive)
            {
                Debug.Log("[副本] 快照说英雄已死（第 " + Time.frameCount + " 帧，HP=" + hero.Hp + "/" + hero.MaxHp
                    + "，实体 #" + hero.EntityId + "）" + m_presence.ToString());
                RequestPlayerDeathOnce();
            }

            if (!m_heroHpKnown)
            {
                m_heroHpKnown = true;
                m_heroLastHp = hero.Hp;
                return;
            }

            if (hero.Hp < m_heroLastHp)
            {
                int amount = m_heroLastHp - hero.Hp;

                if (m_playerAnimation == null)
                {
                    m_playerAnimation = UnityEngine.Object.FindObjectOfType<PlayerAnimation>();
                }

                if (m_playerAnimation != null)
                {
                    DamagePopup.Show(m_playerAnimation.transform.position + Vector3.up * 1.8f, amount);
                    m_playerAnimation.PlayHit();
                }
            }

            m_heroLastHp = hero.Hp;
        }
        // 功能：确保场景里有死亡分流组件（Battle 的英雄是运行时生成的，挂不到预制体上）。
        // 入参：无。
        // 返回：无。
        // 步骤：场景里已经有 PlayerDeathFlow 就返回，没有就挂在本驱动所在物体上。
        // 上游/下游：TrackHeroHealth 调用；下游 PlayerDeathFlow（它会自己去全场景找英雄引用）。
        private void EnsureDeathFlow()
        {
            if (m_deathFlowChecked)
            {
                return;
            }

            m_deathFlowChecked = true;

            if (UnityEngine.Object.FindObjectOfType<PlayerDeathFlow>() == null)
            {
                gameObject.AddComponent<PlayerDeathFlow>();
            }
        }

        // 功能：数一数当前快照里有几个英雄。
        // 入参：无。
        // 返回：英雄实体个数（Kind == 0）；没有快照返回 0。
        // 步骤：遍历表现层的实体列表，按 Kind 判英雄。
        // 上游/下游：TrackHeroHealth 调用；下游 PlayerDeathFlow.HeroEntityCount。
        private int CountHeroEntities()
        {
            if (m_feed == null || m_feed.View == null)
            {
                return 0;
            }

            IReadOnlyList<EntitySnapshot> entities = m_feed.View.Entities;
            int count = 0;

            for (int i = 0; i < entities.Count; i++)
            {
                if (entities[i].Kind == 0)
                {
                    count++;
                }
            }

            return count;
        }

        // 功能：本地英雄倒下时把大脑切到死亡状态（幂等）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 懒加载 PlayerBrain 2) 已经死了就不重复请求 3) 调 NotifyDead。
        // 上游/下游：TrackHeroHealth 调用；下游 PlayerBrain.NotifyDead。
        private void RequestPlayerDeathOnce()
        {
            if (m_playerBrain == null)
            {
                m_playerBrain = UnityEngine.Object.FindObjectOfType<PlayerBrain>();
            }

            if (m_playerBrain == null || m_playerBrain.IsDead)
            {
                return;
            }

            m_playerBrain.NotifyDead();
        }

        // 功能：诊断用 —— 「再来一次」之后，第一张带活怪的快照到达时打一条总耗时。
        // 入参：无（读 m_feed.View）
        // 返回：无
        // 步骤：
        //   1) 不在观察期就直接返回（平时每帧都走这一句，零分配）
        //   2) 数一遍活着、且不是英雄的实体
        //   3) 数到 > 0 就停在第一条日志上，结束观察
        // 上游：RefreshPresentation 每帧
        // 下游：无
        // 功能：把模式、RTT、帧号写进 HUD 只读的静态统计。
        // 入参：无。
        // 返回：无。
        // 步骤：状态同步写快照 tick，帧同步写已步进帧；没有会话就保持上一帧的值。
        // 上游/下游：Update 调用；下游 NetRuntimeStats（HUD 只读它）。
        private void PushStats()
        {
            NetRuntimeStats.SetRtt(m_session.RttMs);

            if (m_mode == ERuntimeSyncMode.Lockstep)
            {
                LockstepClient client = m_session.Lockstep;
                NetRuntimeStats.SetFrame(client == null ? -1 : client.LastSteppedFrame);
                NetRuntimeStats.SetMode(client != null && client.Started ? NetSyncMode.Lockstep : NetSyncMode.Offline);
            }
            else
            {
                NetRuntimeStats.SetFrame((int)m_session.World.ServerTick);
                NetRuntimeStats.SetMode(m_session.IsOnline ? NetSyncMode.StateSync : NetSyncMode.Offline);
            }
        }

        // 功能：会话失败或断开时把模式改回离线。
        // 入参：state 新状态
        // 返回：无
        // 上游：NetSession.StateChanged
        // 下游：无
        private void OnStateChanged(ESessionState state)
        {
            if (state == ESessionState.Failed || state == ESessionState.Disconnected)
            {
                NetRuntimeStats.SetMode(NetSyncMode.Offline);
            }
        }

        // 功能：握手成功后按当前模式点亮统计里的模式。
        // 入参：ack 握手结果
        // 返回：无
        // 上游：NetSession.HandshakeCompleted
        // 下游：无
        private void OnHandshakeCompleted(NBC.Protocol.HandshakeAck ack)
        {
            m_lastError = string.Empty;

            NetRuntimeStats.SetMode(m_mode == ERuntimeSyncMode.Lockstep
                ? NetSyncMode.Lockstep
                : NetSyncMode.StateSync);
        }
    }
}
