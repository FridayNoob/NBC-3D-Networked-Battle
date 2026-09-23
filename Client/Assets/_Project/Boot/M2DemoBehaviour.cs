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
        [Tooltip("EditorSimulate = 免打包（推荐，但要先做过一次模拟构建）；Offline = 读 StreamingAssets 里打好的包")]
        [SerializeField] private AssetRuntimeMode m_mode = AssetRuntimeMode.EditorSimulate;

        /// <summary>
        /// 编辑器模拟模式的清单目录（只有 EditorSimulate 用得上）。
        /// <para>
        /// ⚠️ **留空 = 自动挑最新的一份**（见 `ResolveSimulatePackageRoot`）。
        /// 这是因为"模拟清单目录"其实有**两个不同来源**（2026-09-23 读源码 + 实测确认）：
        /// </para>
        /// <list type="bullet">
        /// <item>`YooAsset → Bundle Builder` 窗口（Pipeline 选 `EditorSimulateBuildPipeline`）：
        /// 用的是**窗口上那个"构建版本"文本框**（`PackageVersion = _buildVersionField.value`，
        /// 默认是日期时间串）→ 输出到 `Bundles/&lt;平台&gt;/&lt;包名&gt;/&lt;构建版本&gt;/`</item>
        /// <item>运行时反射入口 `EditorSimulateBuildInvoker.Build(...)`（内部调
        /// `BundleSimulateBuilder.SimulateBuild`）：那里**写死** `PackageVersion = "Simulate"`
        /// → 输出到 `Bundles/&lt;平台&gt;/&lt;包名&gt;/Simulate/`</item>
        /// </list>
        /// <para>
        /// 📌 教训：**"读了源码"还不够，要读对代码路径** —— 同一个功能两个入口，
        /// 我第一版只读了 `BundleSimulateBuilder`，于是把约定路径写成了 `Simulate`，
        /// 而负责人从窗口构建出来的其实是 `2026-09-23-833`。
        /// </para>
        /// </summary>
        [Tooltip("模拟清单目录（相对工程根或绝对路径）。留空 = 自动挑 Bundles/<平台>/<包名>/ 下最新的一份")]
        [SerializeField] private string m_simulatePackageRoot = string.Empty;

        /// <summary>自动找清单时用的平台目录名（本机是 Windows；与 PlayMode 冒烟测试同一个约定）。</summary>
        private const string SimulatePlatformFolder = "StandaloneWindows64";

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
                // -------- 出门前的检查：把"必然会失败"的情况拦在这儿 --------
                // ⚠️ 为什么要自己先查：底层报错只说"必须提供模拟清单目录"，
                //    而真正该做的是"做一次模拟构建" —— 报错方向不对，人就卡住了。
                //    （2026-09-22 实测：负责人就是在这一步卡住的。）
                if (m_mode == AssetRuntimeMode.EditorSimulate)
                {
                    string root = ResolveSimulatePackageRoot();

                    if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root))
                    {
                        Fail(
                            "找不到编辑器模拟清单目录。我找过了：\n" +
                            "  · 你填的字段：" + (string.IsNullOrEmpty(m_simulatePackageRoot) ? "（空 = 自动挑）" : m_simulatePackageRoot) + "\n" +
                            "  · 自动挑的目录：" + PackageFolder() + "（下面没有任何 `*.bytes` 清单）\n\n" +
                            "先做一次『模拟构建』（免打包模式需要它）：\n" +
                            "  ① 菜单 `YooAsset → Bundle Builder`\n" +
                            "  ② 窗口**顶部**的 Pipeline 下拉框，选 `EditorSimulateBuildPipeline`\n" +
                            "  ③ 点那个绿色的 `Click Build` 按钮\n" +
                            "构建完窗口会**自动在文件管理器里定位到输出目录**；也可以直接把那个目录填进字段。\n\n" +
                            "或者改走另一条路：把 Mode 改成 `Offline`（读打好的包，见 Docs\\18 §七）。");
                        return;
                    }

                    Say("① 用模拟清单：" + root + "（" +
                        System.IO.File.GetLastWriteTime(root).ToString("MM-dd HH:mm") + "）");
                }

                Say("① 装配资源层：" + m_mode +
                    (m_mode == AssetRuntimeMode.EditorSimulate
                        ? "（⚠️ 模拟清单是**快照**：若它是加表之前做的，得重做一次）"
                        : string.Empty));

                string simulateRoot = m_mode == AssetRuntimeMode.EditorSimulate
                    ? ResolveSimulatePackageRoot()
                    : string.Empty;

                await AssetBootstrapper.InstallAsync(m_mode, AssetBootstrapper.DefaultPackageName, simulateRoot);

                Say("② 设置配置来源 + 预加载 " + GameTables.All.Length + " 张表");
                ConfigMgr.Instance.SetSource(new AssetConfigSource());
                GameTables.PreloadAll(OnConfigsReady, reason => Fail("预加载配置表失败：\n" + reason));
            }
            catch (Exception exception)
            {
                Fail("装配资源层失败：" + exception.Message + ModeHint());
            }
        }

        /// <summary>
        /// 解析模拟清单目录：**手填优先，留空则自动挑最新的一份**。
        /// <para>
        /// ⚠️ 为什么要"自动挑"：见字段注释 —— 窗口构建输出的是 `<构建版本>` 目录名，
        /// 而运行时反射入口输出的是 `Simulate`，**两种都可能存在**。
        /// 靠人来填这个路径是这个演示最脆的一环（负责人第一次就卡在这儿）。
        /// </para>
        /// </summary>
        /// <returns>绝对路径；一个都没找到时返回空串。</returns>
        private string ResolveSimulatePackageRoot()
        {
            // ① 手填优先（相对路径按工程根解析）
            if (!string.IsNullOrEmpty(m_simulatePackageRoot))
            {
                string configured = System.IO.Path.IsPathRooted(m_simulatePackageRoot)
                    ? m_simulatePackageRoot
                    : CombineWithProjectRoot(m_simulatePackageRoot);

                if (System.IO.Directory.Exists(configured))
                {
                    return configured;
                }

                // 填了但不存在：不静默改用别的，而是**说出来**（免得"我明明填了呀"）
                Debug.LogWarning("[M2演示] 你填的模拟清单目录不存在，改为自动挑最新的一份：\n  " + configured);
            }

            // ② 自动挑：<工程根>/Bundles/<平台>/<包名>/ 下，**清单文件最新的那个子目录**
            string packageFolder = PackageFolder();

            if (!System.IO.Directory.Exists(packageFolder))
            {
                return string.Empty;
            }

            string newest = null;
            DateTime newestTime = DateTime.MinValue;
            string[] subDirectories = System.IO.Directory.GetDirectories(packageFolder);

            for (int i = 0; i < subDirectories.Length; i++)
            {
                // 只认"里面有清单文件"的目录（把 OutputCache 之类排除掉）
                string[] manifests = System.IO.Directory.GetFiles(
                    subDirectories[i], AssetBootstrapper.DefaultPackageName + "*.bytes");

                if (manifests.Length == 0)
                {
                    continue;
                }

                DateTime time = System.IO.File.GetLastWriteTime(manifests[0]);

                if (time > newestTime)
                {
                    newestTime = time;
                    newest = subDirectories[i];
                }
            }

            return newest ?? string.Empty;
        }

        /// <summary>自动找清单时用的包目录：`&lt;工程根&gt;/Bundles/&lt;平台&gt;/&lt;包名&gt;`。</summary>
        /// <returns>绝对路径。</returns>
        private string PackageFolder()
        {
            return CombineWithProjectRoot(
                "Bundles/" + SimulatePlatformFolder + "/" + AssetBootstrapper.DefaultPackageName);
        }

        /// <summary>把相对路径拼到工程根上（工程根 = `Application.dataPath` 的上一级）。</summary>
        /// <param name="relative">相对路径。</param>
        /// <returns>绝对路径。</returns>
        private string CombineWithProjectRoot(string relative)
        {
            string projectRoot = Application.dataPath.Substring(
                0, Application.dataPath.Length - "/Assets".Length);

            return System.IO.Path.Combine(projectRoot, relative).Replace('\\', '/');
        }

        /// <summary>装配失败时按模式给一句"下一步做什么"。</summary>
        /// <returns>提示文本。</returns>
        private string ModeHint()
        {
            if (m_mode == AssetRuntimeMode.EditorSimulate)
            {
                return "\n\n下一步：菜单 `YooAsset → Bundle Builder` → **顶部 Pipeline 下拉框**选 " +
                       "`EditorSimulateBuildPipeline` → 点绿色 `Click Build`，然后重新进播放模式。\n" +
                       "（M2 加过 4 张新表，**旧的模拟清单里没有它们** —— 必须重做一次。\n" +
                       " 输出目录名是窗口上那个『构建版本』文本框；本演示留空会自动挑最新的一份。\n" +
                       " 完整操作单：Docs\\16 §10.4.2）";
            }

            return "\n\n下一步：菜单 `YooAsset → Bundle Builder` → **顶部 Pipeline 下拉框**选 " +
                   "`ScriptableBuildPipeline` → 构建一次，把内容包拷进 StreamingAssets（见 Docs\\18 §七）。\n" +
                   "（M2 加过 4 张新表，**旧的内容包里没有它们** —— 必须重打一次。）";
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
