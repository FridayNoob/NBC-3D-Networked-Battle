// 这个文件里有：
//   M2DemoBehaviour（类）：单机 PVE 演示的壳子，只在 Unity 侧做装配、喂输入、画状态三件事

using System;
using System.Collections.Generic;
using NBC.Framework;
using NBC.Framework.Asset;
using NBC.Framework.Input;
using NBC.Game.Battle;
using NBC.Game.Config;
using NBC.Game.GameFlow;
using NBC.Game.Quest;
using NBC.Game.UI;
using UnityEngine;

namespace NBC.Boot
{
    // 主要功能：进播放模式后自动装配资源层与配置表，验过演示要用的 id 就装一局、接任务、进关卡；之后每帧把输入喂给这一局，并用 OnGUI 把状态画在左上角。
    // 主要数据成员：m_mode / m_simulatePackageRoot（资源来源）、m_heroId / m_monsterId / m_monsterCount / m_areaId / m_questId（这一局的参数）、m_session（装配好的一局，null = 还没装好）。
    // 主要外部方法：没有公开方法；外部唯一入口是 Unity 调的 Start / Update / OnGUI / OnDestroy。
    // 谁在用它：场景里手动挂的空物体（Hierarchy 右键 Create Empty → 改名 M2Demo → 拖本脚本 → 进播放模式）。注意：本类只干「只有 Unity 才能干」的三件事 —— 在正确时机跑异步装配、每帧喂输入、把状态画出来；判断标准是把它删掉，BattleSession 那条链的用例一条都不该红，所以这里出现任何「判断」都说明抄了逻辑层的活。
    public sealed class M2DemoBehaviour : MonoBehaviour
    {
        // 资源运行模式；EditorSimulate = 读编辑器模拟清单（免打包，但要先做过一次模拟构建），Offline = 读 StreamingAssets 里打好的内容包
        [Tooltip("EditorSimulate = 免打包（推荐，但要先做过一次模拟构建）；Offline = 读 StreamingAssets 里打好的包")]
        [SerializeField] private AssetRuntimeMode m_mode = AssetRuntimeMode.EditorSimulate;

        // 编辑器模拟模式的清单目录；相对工程根或绝对路径，空串 = 自动挑 Bundles/<平台>/<包名>/ 下清单最新的那一份
        // 注意：这个目录有两个来源 —— Bundle Builder 窗口用窗口上那个「构建版本」文本框当目录名（默认是日期时间串，如 2026-09-23-833），运行时反射入口 EditorSimulateBuildInvoker.Build 则写死 Simulate，两种目录都可能存在；同一个功能两个入口，只读一条代码路径就会把约定写错。
        [Tooltip("模拟清单目录（相对工程根或绝对路径）。留空 = 自动挑 Bundles/<平台>/<包名>/ 下最新的一份")]
        [SerializeField] private string m_simulatePackageRoot = string.Empty;

        // 自动找清单时用的平台目录名；固定 StandaloneWindows64（本机是 Windows，与 PlayMode 冒烟测试同一约定）
        private const string SimulatePlatformFolder = "StandaloneWindows64";

        // 玩家英雄的 Hero 表主键；1001 剑士（火球 / 冰箭）、1002 法师（穿心箭），默认 1002
        [Tooltip("Hero 表主键：1001 剑士（火球/冰箭）、1002 法师（穿心箭）")]
        [SerializeField] private int m_heroId = 1002;

        // 刷的怪物 Monster 表主键；默认 6001（野狼）
        [SerializeField] private int m_monsterId = 6001;

        // 刷几只；>= 1，默认 3
        [SerializeField] private int m_monsterCount = 3;

        // 进哪个区域的编号；0 = 不广播区域事件，默认 1
        [SerializeField] private int m_areaId = 1;

        // 接哪个任务的 Quest 表主键；默认 3004（清剿野狼 = 击杀 3 只野狼 + 抵达 1 号区域）
        [SerializeField] private int m_questId = 3004;

        // 放技能用的键；默认 J
        [SerializeField] private KeyCode m_skillKey = KeyCode.J;

        // 交付任务用的键；默认 K
        [SerializeField] private KeyCode m_submitKey = KeyCode.K;

        // 重置用的键（重接任务 + 重刷怪）；默认 R
        [SerializeField] private KeyCode m_resetKey = KeyCode.R;

        // 可选：左上角那块 OnGUI HUD 用的字体；null = 运行时自动去系统字体里找一个中文字体兜底
        // 注意：OnGUI 走的是 IMGUI + 系统动态字体，完全不经过 TMP —— 所以「TMP 字体资产接好了」对它没有任何帮助，中文照样是方框；要固定下来、不依赖开发机，就把 Assets/_Project/Art/Fonts/AlibabaPuHuiTi-3-55-RegularL3.otf 拖到这里。
        [Tooltip("可选：OnGUI HUD 用的字体（拖那份 .otf 进来）；不拖会自动找系统中文字体兜底")]
        // 显式 = null：序列化字段由 Unity 赋值，代码里从不赋值会让编译器报 CS0649（闸门要求 0 警告）
        [SerializeField] private Font m_guiFont = null;

        // 功能：保证 OnGUI HUD 有中文字体，只做一次（把字体挂到两个 GUIStyle 上，并打一条来源日志）。
        // 入参：无（读 m_guiFont）
        // 返回：无
        // 步骤：
        //   1) m_guiFontReady 为 true 直接返回 —— OnGUI 每帧都会调到这里
        //   2) 先用 Inspector 上拖的字体；没拖就按最可能存在的顺序试几个系统中文字体（Microsoft YaHei / 微软雅黑 / SimHei / 黑体 / SimSun / Arial Unicode MS），谁先命中用谁
        //   3) 建 label 与 box 两个样式并把字体挂上去，OnGUI 全程只用它们
        // 上游：OnGUI（每帧）
        // 下游：Font.CreateDynamicFontFromOSFont、GUIStyle
        // 注意：字体必须挂在 GUIStyle 上，光设 GUI.skin.font 没用 —— 默认皮肤里 label / box 各自带着内置 Arial 字体，不会继承皮肤字体，症状是日志说字体找到了、屏幕上还是方框。
        // 注意：OnGUI 与 TMP 是两套完全独立的字体系统，TMP 那边接好了对 IMGUI 毫无帮助；系统字体兜底只在开发机上有效，正式界面换成 TMP 后这条路本来就会退场。
        private void EnsureGuiFont()
        {
            if (m_guiFontReady)
            {
                return;
            }

            m_guiFontReady = true;

            Font font = m_guiFont;
            string source = "Inspector 上指定的字体";

            if (font == null)
            {
                string[] candidates = { "Microsoft YaHei", "微软雅黑", "SimHei", "黑体", "SimSun", "Arial Unicode MS" };

                for (int i = 0; i < candidates.Length && font == null; i++)
                {
                    font = Font.CreateDynamicFontFromOSFont(candidates[i], 16);

                    if (font != null)
                    {
                        source = "系统字体「" + candidates[i] + "」";
                    }
                }
            }

            s_labelStyle = new GUIStyle(GUI.skin.label);
            s_labelStyle.font = font;
            s_labelStyle.fontSize = 14;
            s_labelStyle.wordWrap = false;

            m_boxStyle = new GUIStyle(GUI.skin.box);
            m_boxStyle.font = font;

            if (font != null)
            {
                GUI.skin.font = font;

                Debug.Log("[M2演示] HUD 字体：" + source + "（已挂到 GUIStyle 上）。\n" +
                          "想固定下来、不依赖开发机的系统字体，就把 `Assets/_Project/Art/Fonts/` " +
                          "里那份 .otf 拖到「Gui Font」字段上。");
                return;
            }

            Debug.LogWarning("[M2演示] HUD 没找到中文字体，中文会显示成方框。\n" +
                             "把 `Assets/_Project/Art/Fonts/AlibabaPuHuiTi-3-55-RegularL3.otf` " +
                             "拖到这个组件上的「Gui Font」字段即可。\n" +
                             "（注意：这只影响左上角这块 **OnGUI** HUD；任务面板走 TMP，是另一套。）");
        }

        // 可选：场景里的任务面板；null = 不绑，只看左上角的 OnGUI
        // 注意：拖了就由本壳子替它调一次 Initialize —— 正常流程里这一步该由 UIManager 的启动链做，这里为了少一层依赖手动调了。
        [Tooltip("可选：任务面板（见 Docs\\22 D 组的预制体清单）；不拖就只看左上角的 OnGUI")]
        // 同样显式 = null（理由同上：Unity 赋值的序列化字段会让编译器报 CS0649）
        [SerializeField] private QuestPanel m_questPanel = null;

        // 装配好的一局；null = 还没装配好
        private BattleSession m_session;

        // 玩家技能对应的输入动作 id；由 InputActionId.Declare 声明（位号 0、名字 Skill1）
        private InputActionId m_skillAction;

        // 发奖实现（内存版，控制台能看到到账数字）；装配成功后非 null
        private InMemoryQuestRewardSink m_sink;

        // 界面上的滚动消息；最多留 8 条，超出丢最旧的一条
        private readonly List<string> m_log = new List<string>();

        // 装配是否已开始；true = Start 已跑过，用来防重入
        private bool m_assembling;

        // HUD 字体是否已经处理过；OnGUI 每帧都会调，这件事只该做一次
        private bool m_guiFontReady;

        // HUD 的文字样式（中文字体挂在这上面）；做成 static 是因为 Label 那个小助手是静态方法，取不到实例成员
        private static GUIStyle s_labelStyle;

        // HUD 的外框样式（同样挂着中文字体 —— 否则框上的内置字体仍会让中文变方框）
        private GUIStyle m_boxStyle;

        // 装配失败时的原因；null = 没失败
        private string m_failure;


        // 功能：进播放模式后自动装配。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 直接转给 AssembleAsync —— Start 不能 await，异步与异常处理全在那边
        // 上游：Unity 引擎（开始帧调一次）
        // 下游：AssembleAsync
        private void Start()
        {
            AssembleAsync();
        }

        // 功能：装资源层 → 设配置来源 → 预加载配置表，一条链跑完，异常全部落到界面上。
        // 入参：无
        // 返回：无（async void 的 fire-and-forget，结果只能从界面与 Console 看）
        // 步骤：
        //   1) 用 m_assembling 防重入，避免 Start 里跑两次
        //   2) EditorSimulate 先自己查一遍清单目录在不在：底层报错只会说「必须提供模拟清单目录」，而真正该做的是「先做一次模拟构建」，报错方向不对人就卡住了
        //   3) 失败文案写清填了什么、自动挑到了哪里、怎么修（菜单 → Bundle Builder → 选 EditorSimulateBuildPipeline → Click Build），并把改走 Offline 也列成一条出路
        //   4) 装资源层，再把配置来源设成走资源层的实现，最后预加载 GameTables.All
        //   5) 预加载成功回调 OnConfigsReady，失败回调 Fail
        // 上游：Start
        // 下游：AssetBootstrapper.InstallAsync、ConfigMgr.SetSource、GameTables.PreloadAll、OnConfigsReady、Fail
        // 注意：不要在这里吞异常 —— 装配失败必须让玩家看见原因，否则就是「点了没反应」。
        private async void AssembleAsync()
        {
            if (m_assembling)
            {
                return;
            }

            m_assembling = true;

            try
            {
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

        // 功能：解析模拟清单目录 —— 手填优先，留空则自动挑清单最新的那份子目录。
        // 入参：无（读 m_simulatePackageRoot）
        // 返回：绝对路径；一份都没找到时返回空串
        // 步骤：
        //   1) 手填了就按工程根解析相对路径，目录存在就用它
        //   2) 填了但不存在不静默换目录，先打一条 warning 说出来（免得出现「我明明填了呀」）
        //   3) 自动挑时遍历 Bundles/<平台>/<包名>/ 下的子目录，只认里面真有 <包名>*.bytes 清单的（把 OutputCache 之类排除掉）
        //   4) 按清单文件的最后写入时间取最新的一份，一个都没有就返回空串
        // 上游：AssembleAsync（装配前检查与真正装配各一次）
        // 下游：PackageFolder、CombineWithProjectRoot
        // 注意：靠人填这个路径是本演示最脆的一环（负责人第一次就卡在这儿），所以留空必须能自己挑到。
        private string ResolveSimulatePackageRoot()
        {
            if (!string.IsNullOrEmpty(m_simulatePackageRoot))
            {
                string configured = System.IO.Path.IsPathRooted(m_simulatePackageRoot)
                    ? m_simulatePackageRoot
                    : CombineWithProjectRoot(m_simulatePackageRoot);

                if (System.IO.Directory.Exists(configured))
                {
                    return configured;
                }

                Debug.LogWarning("[M2演示] 你填的模拟清单目录不存在，改为自动挑最新的一份：\n  " + configured);
            }

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

        // 功能：拼出自动找清单时用的包目录 <工程根>/Bundles/<平台>/<包名>。
        // 入参：无
        // 返回：绝对路径
        // 步骤：
        //   1) 平台目录名固定 SimulatePlatformFolder，包名取 AssetBootstrapper.DefaultPackageName，两者都得与实际输出目录一致
        // 上游：ResolveSimulatePackageRoot、AssembleAsync（拼报错文本）
        // 下游：CombineWithProjectRoot
        private string PackageFolder()
        {
            return CombineWithProjectRoot(
                "Bundles/" + SimulatePlatformFolder + "/" + AssetBootstrapper.DefaultPackageName);
        }

        // 功能：把相对路径拼到工程根上。
        // 入参：relative 工程根下的相对路径
        // 返回：绝对路径（分隔符统一成 /）
        // 步骤：
        //   1) 工程根 = Application.dataPath 去掉末尾的 /Assets
        //   2) 与 relative 合并后把反斜杠换成斜杠，方便直接填进 Inspector 与贴进日志
        // 上游：PackageFolder、ResolveSimulatePackageRoot
        // 下游：System.IO.Path.Combine
        private string CombineWithProjectRoot(string relative)
        {
            string projectRoot = Application.dataPath.Substring(
                0, Application.dataPath.Length - "/Assets".Length);

            return System.IO.Path.Combine(projectRoot, relative).Replace('\\', '/');
        }

        // 功能：装配失败时按当前模式给一句「下一步做什么」。
        // 入参：无（读 m_mode）
        // 返回：给玩家看的提示文本，带具体菜单与下拉框选项
        // 步骤：
        //   1) EditorSimulate 指向 Bundle Builder 窗口做一次模拟构建，并点明加过的 4 张新表旧清单里没有、必须重做
        //   2) Offline 指向 ScriptableBuildPipeline 重打包，并提醒旧内容包里同样没有这些新表
        // 上游：AssembleAsync 的 catch
        // 下游：无
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

        // 功能：数据预检 —— 演示要用的英雄、技能、怪、任务、奖励这些 id 在当前加载的资产里是否都存在。
        // 入参：无（读 m_heroId / m_monsterId / m_questId）
        // 返回：没问题返回 null；有问题返回一句人话（带最可能的原因与处理办法）
        // 步骤：
        //   1) 英雄在 Hero 表里，且 skillIds 非空
        //   2) 英雄的每个技能都在 Skill 表里
        //   3) 怪在 Monster 表里
        //   4) 任务在 Quest 表里（这一条最常踩：加任务时常忘了重新导入）
        //   5) 任务的 rewardId 在 Reward 表里
        // 上游：OnConfigsReady（装一局之前）
        // 下游：MissingId
        // 注意：查的是数据（资产里有没有这个 id）而不是文件时间，所以在打包后的 Player 里也有效；「比文件时间」那套只在编辑器里有意义。
        private string VerifyConfiguredIds()
        {
            Config_Hero hero;

            if (!ConfigMgr.Instance.Get<HeroConfig>().TryGet(m_heroId, out hero))
            {
                return MissingId("Hero", m_heroId, "英雄");
            }

            if (hero.skillIds == null || hero.skillIds.Length == 0)
            {
                return "英雄 " + m_heroId + " 在 `Hero.skillIds` 里没有技能，演示没法放技能。";
            }

            SkillConfig skills = ConfigMgr.Instance.Get<SkillConfig>();

            for (int i = 0; i < hero.skillIds.Length; i++)
            {
                Config_Skill skill;

                if (!skills.TryGet(hero.skillIds[i], out skill))
                {
                    return MissingId("Skill", hero.skillIds[i], "技能（英雄 " + m_heroId + " 的技能列表里）");
                }
            }

            Config_Monster monster;

            if (!ConfigMgr.Instance.Get<MonsterConfig>().TryGet(m_monsterId, out monster))
            {
                return MissingId("Monster", m_monsterId, "怪物");
            }

            Config_Quest quest;

            if (!ConfigMgr.Instance.Get<QuestConfig>().TryGet(m_questId, out quest))
            {
                return MissingId("Quest", m_questId, "任务");
            }

            Config_Reward reward;

            if (!ConfigMgr.Instance.Get<RewardConfig>().TryGet(quest.rewardId, out reward))
            {
                return MissingId("Reward", quest.rewardId, "奖励（任务 " + m_questId + " 的）");
            }

            return null;
        }

        // 功能：拼一句「某张表里没有某个 id」的报错，并给出最可能的原因与两步处理办法。
        // 入参：table 表名；id 缺的编号；what 这是什么（人话，用于拼句子）
        // 返回：报错文本
        // 步骤：
        //   1) 第一句点明表名、是什么、缺哪个 id
        //   2) 原因写「配置资产是旧的」：改过 CSV 却没重新导入，这一步不报错，只会在运行到一半才暴露
        //   3) 处理办法两步都给全：先跑 ConfigKit.Cli 生成 .tsv / .cs，再点导入菜单
        // 上游：VerifyConfiguredIds
        // 下游：无
        private static string MissingId(string table, int id, string what)
        {
            return "配置表 " + table + " 里没有" + what + " " + id + "。\n\n" +
                   "最常见的原因：**配置资产是旧的**（改过 CSV，但没重新导入）。\n" +
                   "处理办法（两步都别漏）：\n" +
                   "  ① 改过 CSV 的话，先跑一次 ConfigKit.Cli 生成 .tsv / .cs\n" +
                   "  ② 点一次菜单 `Tools/NBC/配置表/导入 TSV → ScriptableObject`\n\n" +
                   "想先看是哪几张表过期：跑菜单 `Tools/NBC/配置表/检查资产是否过期（只报告）`。";
        }

        // 功能：配置表就绪后的下一步 —— 先数据预检，再装一局、绑输入、接任务进关卡、绑任务面板。
        // 入参：无（GameTables.PreloadAll 成功后的回调）
        // 返回：无
        // 步骤：
        //   1) 先 VerifyConfiguredIds，有问题就地 Fail 并停住，这样报错方向才是「资产过期」
        //   2) 造内存发奖接缝，按 m_heroId 装一局 BattleSession.FromConfigMgr
        //   3) 声明技能动作、绑到 m_skillKey，注入 LegacyInputSource，并把动作登进 InputManager（动作 → 键的绑定留在实现侧）
        //   4) 把这局的 Caster 绑到英雄的第一个技能
        //   5) 接任务进关卡，再绑任务面板
        // 上游：GameTables.PreloadAll（经 AssembleAsync）
        // 下游：VerifyConfiguredIds、BattleSession.FromConfigMgr、InputManager、StartLevel、BindQuestPanel、Fail
        // 注意：「改过 CSV、忘了点导入菜单」会让资产变成旧快照，而且不报任何错，表现是运行到一半才说某张表里没有某个 id，所以必须在装局之前拦。
        private void OnConfigsReady()
        {
            try
            {
                string problem = VerifyConfiguredIds();

                if (problem != null)
                {
                    Fail(problem);
                    return;
                }

                Say("③ 装配一局（BattleSession）");
                m_sink = new InMemoryQuestRewardSink();
                m_session = BattleSession.FromConfigMgr(m_heroId, m_sink);

                InputMapping mapping = new InputMapping();
                m_skillAction = InputActionId.Declare(0, "Skill1");
                mapping.Bind(m_skillAction, m_skillKey);
                InputManager.Instance.Source = new LegacyInputSource(mapping);
                InputManager.Instance.TrackAction(m_skillAction);
                m_session.Caster.Bind(m_skillAction, FirstSkillOfHero());

                Say("④ 接任务 " + m_questId + " + 进关卡");
                StartLevel();

                BindQuestPanel();

                Say("就绪：J = 放技能，K = 交付任务，R = 重置");
            }
            catch (Exception exception)
            {
                Fail("装配一局失败：" + exception.Message);
            }
        }

        // 功能：Inspector 上拖了任务面板就把它绑到这一局，没拖就什么都不做。
        // 入参：无（读 m_questPanel）
        // 返回：无
        // 步骤：
        //   1) 面板为空直接返回
        //   2) 没初始化过就先调 Initialize —— 面板的初始化入口是公开方法（EditMode 下 Unity 不会调 Awake）；正常流程该由 UIManager 调，这里手动补一次
        //   3) 用这一局的任务运行时造一份 QuestPanelModel 绑上去
        // 上游：OnConfigsReady
        // 下游：QuestPanel.Initialize / Bind、QuestPanelModel
        private void BindQuestPanel()
        {
            if (m_questPanel == null)
            {
                return;
            }

            if (!m_questPanel.IsInitialized)
            {
                m_questPanel.Initialize();
            }

            m_questPanel.Bind(new QuestPanelModel(m_session.Quests));
            Say("任务面板已绑定（接取/追踪/交付都在上面）");
        }

        // 功能：先接任务，再进关卡（刷怪 + 广播区域事件）。
        // 入参：无（读 m_questId / m_monsterId / m_monsterCount / m_areaId）
        // 返回：无
        // 步骤：
        //   1) 先 AcceptQuest，失败就 Say 一句原因并停住，不带着没接上的任务进关卡
        //   2) 再 EnterLevel 刷怪进区域
        // 上游：OnConfigsReady、Reset
        // 下游：BattleSession.AcceptQuest / EnterLevel
        // 注意：顺序不能反 —— 「抵达区域」是瞬时事件，先走进区域、再接到「抵达区域」的任务，那条条件永远不会推进（没有接任务时回填当前状态这一步）；正解是接取时做一次状态回填。
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

        // 功能：取玩家配置里的第一个技能，演示只绑一个键。
        // 入参：无
        // 返回：技能编号
        // 步骤：
        //   1) 从 m_session.Hero.SkillIds 取第 0 个
        //   2) 一个技能都没有就抛 —— 没技能演示放不出来，不能静默返回 0
        // 上游：OnConfigsReady
        // 下游：无
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


        // 功能：每帧推进输入并喂给这一局，另外处理交付与重置两个按键。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 这一局还没装好就直接返回
        //   2) InputManager.Tick 负责采集 + 生成命令，BattleSession.HandleInput 负责把命令翻译成技能
        //   3) 真放出技能就 Say 一句；K 交付、R 重置
        // 上游：Unity 引擎（每帧）
        // 下游：InputManager.Tick、BattleSession.HandleInput、Submit、Reset
        private void Update()
        {
            if (m_session == null)
            {
                return;
            }

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

        // 功能：交付任务，成功时把到账奖励打出来。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) SubmitQuest 后按 Ok 分流
        //   2) 成功 Say 发奖汇总（内存版累计），失败 Say 拒绝原因
        // 上游：Update（按 m_submitKey）
        // 下游：BattleSession.SubmitQuest
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

        // 功能：重置演示 —— 放弃任务、清场，再重刷怪重接任务，可以反复跑。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 放弃任务并清空世界，把上一局的痕迹清掉
        //   2) StartLevel 重新接任务进关卡
        // 上游：Update（按 m_resetKey）
        // 下游：QuestRuntime.Abandon、BattleWorld.Clear、StartLevel
        private void Reset()
        {
            m_session.Quests.Abandon(m_questId);
            m_session.World.Clear();

            Say("—— 重置 ——");
            StartLevel();
        }

        // 功能：退出时把这一局拆干净（退订、释放）。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 有这一局就 Dispose 再置空，避免下次进播放模式还碰到旧实例
        // 上游：Unity 引擎（物体销毁时）
        // 下游：BattleSession.Dispose
        // 注意：订阅不退订是反复踩过的坑 —— 所以退出时必须 Dispose，不能只把字段置空。
        private void OnDestroy()
        {
            if (m_session != null)
            {
                m_session.Dispose();
                m_session = null;
            }
        }


        // 功能：把这一局的状态画在左上角 —— 失败原因、玩家、活怪与目标、任务追踪、日志。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先 EnsureGuiFont（每帧调，内部只做一次），然后把整块内容包进一个 640x480 的框
        //   2) 有失败就优先只画原因并结束，不让半装好的状态混进来
        //   3) 还没装好画「正在装配」，装好了画玩家与活怪，再画接取中的任务与每条条件的进度
        //   4) 日志倒序画，最新的在最上面
        // 上游：Unity 引擎（每帧，IMGUI）
        // 下游：EnsureGuiFont、Label、QuestRuntime.CopyActiveTrackings
        // 注意：目标选择是「打第一只活着的怪」，没有点选 / 锁定 UI；本演示也没有模型、动画与伤害飘字，只有 Console 日志加这块文字，表现层是消费事件的人，不在这里。
        private void OnGUI()
        {
            EnsureGuiFont();

            GUILayout.BeginArea(new Rect(12, 12, 640, 480), m_boxStyle);

            Label("=== M2 演示：单机 PVE + 任务系统 ===");
            Label("J = 放技能    K = 交付任务    R = 重置");

            if (m_failure != null)
            {
                Label("❌ " + m_failure);
                GUILayout.EndArea();
                return;
            }

            if (m_session == null)
            {
                Label("… 正在装配（看 Console 的 ① ② ③ ④ 步骤）");
            }
            else
            {
                Label("玩家：" + m_session.Hero);
                Label("场上活怪：" + m_session.World.AliveMonsterCount +
                      "，当前目标 #" + m_session.CurrentTargetInstanceId);
                GUILayout.Space(6);

                List<QuestTracking> trackings = new List<QuestTracking>();
                m_session.Quests.CopyActiveTrackings(trackings);

                if (trackings.Count == 0)
                {
                    Label("（没有接取中的任务）");
                }

                for (int i = 0; i < trackings.Count; i++)
                {
                    QuestTracking tracking = trackings[i];
                    Label("【" + tracking.QuestId + "】" + tracking.Name + "（" + tracking.State + "）");

                    for (int c = 0; c < tracking.Conditions.Count; c++)
                    {
                        QuestConditionLine line = tracking.Conditions[c];
                        Label("    · " + line.Description + "   " +
                              line.Current + "/" + line.Required + (line.IsMet ? "  ✅" : string.Empty));
                    }
                }
            }

            GUILayout.Space(6);
            Label("—— 日志 ——");

            for (int i = m_log.Count - 1; i >= 0; i--)
            {
                Label(m_log[i]);
            }

            GUILayout.EndArea();
        }

        // 功能：用带中文字体的样式画一行（样式由 EnsureGuiFont 建好）。
        private static void Label(string text)
        {
            GUILayout.Label(text, s_labelStyle);
        }


        // 功能：记一条消息 —— 同时进界面日志与 Console。
        // 入参：message 消息正文
        // 返回：无
        // 步骤：
        //   1) 先入列，超过 8 条就丢最旧的一条，界面只留最近若干条
        //   2) 再打一条带 [M2演示] 前缀的 Console 日志
        // 上游：装配各步、Update、Submit、Reset、BindQuestPanel
        // 下游：Debug.Log
        private void Say(string message)
        {
            m_log.Add(message);

            if (m_log.Count > 8)
            {
                m_log.RemoveAt(0);
            }

            Debug.Log("[M2演示] " + message);
        }

        // 功能：记一次失败并画到界面上（不能只进 Console）。
        // 入参：message 失败原因
        // 返回：无
        // 步骤：
        //   1) 存进 m_failure，让 OnGUI 优先显示它
        //   2) 再打一条 LogError
        // 上游：装配与预检各处的失败分支
        // 下游：Debug.LogError
        private void Fail(string message)
        {
            m_failure = message;
            Debug.LogError("[M2演示] " + message);
        }
    }
}
