// ============================================================================
//  M2DemoBehaviour —— M2 的**演示壳子**：让你能亲手跑一遍那个闭环
//  项目：3D联网战斗Demo   对应：M2-C2（"你能亲手跑一遍"）
//
//  ---------------------------------------------------------------------------
//  它做什么（四步，全自动）
//  ---------------------------------------------------------------------------
//      ① 经组合根装配资源层（`AssetBootstrapper.InstallAsync`）
//      ② 把配置来源设成走资源层的实现，并预加载 `GameTables.All`
//      ③ 装配一局（`BattleSession`）
//      ④ 接任务 -> 进关卡（刷 3 只野狼 + 广播"进入 1 号区域"）
//
//  然后你按键就能玩：
//      **J** = 放技能（穿心箭）      **K** = 交付任务      **R** = 重置（重接 + 重刷）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 它是壳子，**不含任何逻辑**（这一点决定了它薄不薄）
//  ---------------------------------------------------------------------------
//  伤害、条件、任务、发奖全在 `BattleSession` 那一条链上（并且有 EditMode 用例覆盖）。
//  本类只干三件"只有 Unity 才能干"的事：
//      ① 在正确的时机跑异步装配（`Start` / 协程）
//      ② 每帧把输入喂进去（`Update`）
//      ③ 把状态画出来（`OnGUI`）
//
//  📌 判据：**把本类删掉，`BattleSession` 的用例一条都不该红。**
//     反过来说，本类里出现任何"判断"都说明它抄了逻辑层的活。
//
//  ---------------------------------------------------------------------------
//  怎么用（3 步，都是你在 Unity 里做）
//  ---------------------------------------------------------------------------
//      ① 打开 `Assets/_Project/Scenes` 里任意一个场景（没有就用默认场景）
//      ② 新建空物体（Hierarchy 右键 → Create Empty），改名 `M2Demo`
//      ③ 把本脚本拖到它身上 → 进播放模式
//
//  想换运行模式（编辑器模拟 / 内置包）就改 Inspector 上的 Mode：
//      · `EditorSimulate`：日常开发，免打包（但要先配好模拟清单目录）
//      · `Offline`：读 StreamingAssets 里的内容包（**要先打一次包**，见 Docs\18 §七）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 已知缺口（如实记，不假装没有）
//  ---------------------------------------------------------------------------
//  · **"抵达区域"是瞬时事件**：如果玩家**先**走进区域、**再**接到"抵达区域"的任务，
//    那条条件永远不会推进（没有"接任务时回填当前状态"这一步）。
//    → 所以本演示的顺序是**先接任务、再进关卡**。
//    正解是接取时做一次状态回填（M4 做成就时会撞上同一个问题：
//    成就的进度是可以持久化的，登录时必须按已有进度回填）。
//  · **目标选择是"打第一只活着的怪"**（没有点选/锁定 UI）。
//  · **没有表现层**：没有模型、动画、伤害飘字 —— 只有 Console 日志 + OnGUI 文字。
//    表现层是消费事件的人，属 M3。
// ============================================================================

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Asset;
using NBC.Framework.Input;
using NBC.Game.Battle;
using NBC.Game.Config;
using NBC.Game.GameFlow;
using NBC.Game.Quest;
using UnityEngine;

namespace NBC.Boot
{
    /// <summary>M2 演示壳子：装配 + 输入 + 画状态。</summary>
    public sealed class M2DemoBehaviour : MonoBehaviour
    {
        /// <summary>资源运行模式。</summary>
        [Tooltip("EditorSimulate = 免打包；Offline = 读 StreamingAssets 里的内容包（要先打包）")]
        [SerializeField] private AssetRuntimeMode m_mode = AssetRuntimeMode.EditorSimulate;

        /// <summary>编辑器模拟模式的清单目录（只有 EditorSimulate 用得上）。</summary>
        [Tooltip("只有 EditorSimulate 模式需要：模拟构建的输出目录；留空表示用默认")]
        [SerializeField] private string m_simulatePackageRoot = string.Empty;

        /// <summary>用哪个英雄当玩家（`Hero` 表主键）。</summary>
        [Tooltip("Hero 表主键：1001 剑士（火球/冰箭）、1002 法师（穿心箭）")]
        [SerializeField] private int m_heroId = 1002;

        /// <summary>刷哪种怪。</summary>
        [SerializeField] private int m_monsterId = 6001;

        /// <summary>刷几只。</summary>
        [SerializeField] private int m_monsterCount = 3;

        /// <summary>进哪个区域（0 = 不广播区域事件）。</summary>
        [SerializeField] private int m_areaId = 1;

        /// <summary>接哪个任务（默认 3004 清剿野狼 = 击杀 3 只野狼 + 抵达一号区域）。</summary>
        [SerializeField] private int m_questId = 3004;

        /// <summary>放技能用的键。</summary>
        [SerializeField] private KeyCode m_skillKey = KeyCode.J;

        /// <summary>交付任务用的键。</summary>
        [SerializeField] private KeyCode m_submitKey = KeyCode.K;

        /// <summary>重置用的键（重接任务 + 重刷怪）。</summary>
        [SerializeField] private KeyCode m_resetKey = KeyCode.R;

        /// <summary>装配好的一局（null = 还没装配好）。</summary>
        private BattleSession m_session;

        /// <summary>玩家技能对应的输入动作。</summary>
        private InputActionId m_skillAction;

        /// <summary>发奖实现（内存版：控制台能看到到账数字）。</summary>
        private InMemoryQuestRewardSink m_sink;

        /// <summary>界面上的滚动消息（最近若干条）。</summary>
        private readonly List<string> m_log = new List<string>();

        /// <summary>装配是否已开始（防止 Start 里跑两次）。</summary>
        private bool m_assembling;

        /// <summary>装配失败时的原因（画在界面上）。</summary>
        private string m_failure;

        // ====================================================================
        //  生命周期：装配
        // ====================================================================

        /// <summary>进播放模式后自动装配。</summary>
        private void Start()
        {
            AssembleAsync();
        }

        /// <summary>
        /// 异步装配（`Start` 不能 await，所以这里 fire-and-forget + 全部异常落到界面上）。
        /// <para>⚠️ **不要在这里吞异常**：装配失败必须让玩家看见原因，否则就是"点了没反应"。</para>
        /// </summary>
        private async void AssembleAsync()
        {
            if (m_assembling)
            {
                return;
            }

            m_assembling = true;

            try
            {
                Say("① 装配资源层：" + m_mode);
                await AssetBootstrapper.InstallAsync(m_mode, AssetBootstrapper.DefaultPackageName,
                                                     m_simulatePackageRoot);

                Say("② 设置配置来源 + 预加载 " + GameTables.All.Length + " 张表");
                ConfigMgr.Instance.SetSource(new AssetConfigSource());
                GameTables.PreloadAll(OnConfigsReady, reason => Fail("预加载配置表失败：" + reason));
            }
            catch (Exception exception)
            {
                Fail("装配资源层失败：" + exception.Message +
                     "\n（Offline 模式要先打一次包；EditorSimulate 要先配模拟清单目录 —— 见 Docs\\18 §七）");
            }
        }

        /// <summary>配置表就绪：装配一局、接任务、进关卡。</summary>
        private void OnConfigsReady()
        {
            try
            {
                Say("③ 装配一局（BattleSession）");
                m_sink = new InMemoryQuestRewardSink();
                m_session = BattleSession.FromConfigMgr(m_heroId, m_sink);

                // 把玩家的技能绑到"J"上（A7：动作 -> 键 的绑定留在实现侧）
                InputMapping mapping = new InputMapping();
                m_skillAction = InputActionId.Declare(0, "Skill1");
                mapping.Bind(m_skillAction, m_skillKey);
                InputManager.Instance.Source = new LegacyInputSource(mapping);
                InputManager.Instance.TrackAction(m_skillAction);
                m_session.Caster.Bind(m_skillAction, FirstSkillOfHero());

                Say("④ 接任务 " + m_questId + " + 进关卡");
                StartLevel();

                Say("就绪：J = 放技能，K = 交付任务，R = 重置");
            }
            catch (Exception exception)
            {
                Fail("装配一局失败：" + exception.Message);
            }
        }

        /// <summary>接任务 + 进关卡（顺序有讲究：**先接任务，区域事件才计得进条件**，见文件头缺口）。</summary>
        private void StartLevel()
        {
            QuestActionResult accepted = m_session.AcceptQuest(m_questId);

            if (!accepted.Ok)
            {
                Say("接任务失败：" + accepted.Reason);
                return;
            }

            m_session.EnterLevel(m_monsterId, m_monsterCount, m_areaId);
            Say("已进关卡：刷了 " + m_monsterCount + " 只 " + m_monsterId);
        }

        /// <summary>取玩家配置里的第一个技能（演示只绑一个键）。</summary>
        /// <returns>技能编号。</returns>
        private int FirstSkillOfHero()
        {
            int[] skills = m_session.Hero.SkillIds;

            if (skills == null || skills.Length == 0)
            {
                throw new InvalidOperationException(
                    "[M2Demo] 英雄 " + m_heroId + " 在 `Hero.skillIds` 里一个技能都没有，演示没法放技能。");
            }

            return skills[0];
        }

        // ====================================================================
        //  生命周期：每帧输入
        // ====================================================================

        /// <summary>每帧：推进输入（A7）→ 喂给这一局。</summary>
        private void Update()
        {
            if (m_session == null)
            {
                return;
            }

            // A7 的 `Tick` 负责"采集 + 生成命令"；`HandleInput` 负责"把命令翻译成技能"
            InputCommand command = InputManager.Instance.Tick(Time.frameCount);

            int cast = m_session.HandleInput(command, null);

            if (cast > 0)
            {
                Say("放了 " + cast + " 发技能");
            }

            if (Input.GetKeyDown(m_submitKey))
            {
                Submit();
            }

            if (Input.GetKeyDown(m_resetKey))
            {
                Reset();
            }
        }

        /// <summary>交付任务（成功时把到账奖励打出来）。</summary>
        private void Submit()
        {
            QuestActionResult result = m_session.SubmitQuest(m_questId);

            if (result.Ok)
            {
                Say("✅ 交付成功！奖励到账：" + m_sink.ToString());
                return;
            }

            Say("交付被拒绝：" + result.Reason);
        }

        /// <summary>重置：重刷怪 + 重接任务（演示可以反复跑）。</summary>
        private void Reset()
        {
            m_session.Quests.Abandon(m_questId);
            m_session.World.Clear();

            Say("—— 重置 ——");
            StartLevel();
        }

        /// <summary>退出时拆干净（**订阅不退订是 A4/A8 反复踩的坑**）。</summary>
        private void OnDestroy()
        {
            if (m_session != null)
            {
                m_session.Dispose();
                m_session = null;
            }
        }

        // ====================================================================
        //  生命周期：画状态（OnGUI 够用了，演示不引入 UI 框架）
        // ====================================================================

        /// <summary>把状态画在屏幕上（没有模型/动画，就先这么看）。</summary>
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 560, 480), GUI.skin.box);

            GUILayout.Label("=== M2 演示：单机 PVE + 任务系统 ===");
            GUILayout.Label("J = 放技能    K = 交付任务    R = 重置");

            if (m_failure != null)
            {
                GUILayout.Label("❌ " + m_failure);
                GUILayout.EndArea();
                return;
            }

            if (m_session == null)
            {
                GUILayout.Label("… 正在装配（看 Console 的 ① ② ③ ④ 步骤）");
            }
            else
            {
                GUILayout.Label("玩家：" + m_session.Hero);
                GUILayout.Label("场上活怪：" + m_session.World.AliveMonsterCount +
                                "，当前目标 #" + m_session.CurrentTargetInstanceId);
                GUILayout.Space(6);

                List<QuestTracking> trackings = new List<QuestTracking>();
                m_session.Quests.CopyActiveTrackings(trackings);

                if (trackings.Count == 0)
                {
                    GUILayout.Label("（没有接取中的任务）");
                }

                for (int i = 0; i < trackings.Count; i++)
                {
                    QuestTracking tracking = trackings[i];
                    GUILayout.Label("【" + tracking.QuestId + "】" + tracking.Name + "（" + tracking.State + "）");

                    for (int c = 0; c < tracking.Conditions.Count; c++)
                    {
                        QuestConditionLine line = tracking.Conditions[c];
                        GUILayout.Label("    · " + line.Description + "   " +
                                        line.Current + "/" + line.Required + (line.IsMet ? "  ✅" : string.Empty));
                    }
                }
            }

            GUILayout.Space(6);
            GUILayout.Label("—— 日志 ——");

            for (int i = m_log.Count - 1; i >= 0; i--)
            {
                GUILayout.Label(m_log[i]);
            }

            GUILayout.EndArea();
        }

        // ====================================================================
        //  小工具
        // ====================================================================

        /// <summary>记一条消息（同时发到 Console 和界面，最多留 8 条）。</summary>
        /// <param name="message">消息。</param>
        private void Say(string message)
        {
            m_log.Add(message);

            if (m_log.Count > 8)
            {
                m_log.RemoveAt(0);
            }

            Debug.Log("[M2演示] " + message);
        }

        /// <summary>记一次失败（界面上要能看见 —— 不能只进 Console）。</summary>
        /// <param name="message">原因。</param>
        private void Fail(string message)
        {
            m_failure = message;
            Debug.LogError("[M2演示] " + message);
        }
    }
}
