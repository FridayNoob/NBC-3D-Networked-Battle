// 这个文件里有：
//   HpBarDemoBehaviour（类）：演示脚手架，装资源层 → 开血条面板 → 按空格扣血

using System;
using System.Threading.Tasks;
using NBC.Framework.Asset;
using NBC.Framework.Input;
using NBC.Framework.UI;
using NBC.Game.Bootstrap;
using NBC.Game.Config;
using NBC.Game.UI;
using UnityEngine;

namespace NBC.Boot
{
    // 主要功能：把「资源层初始化 + 打开血条 HUD + 按空格扣血」合成一个演示脚手架，只用于人工验收。
    // 主要数据成员：m_mode（资源模式）、m_maxHp（最大血，服务端真血到达前的初始显示）。
    // 主要外部方法：无（挂到场景物体上自动跑）；正式战斗 HUD 接上后本类可删。
    // 谁在用它：只挂在验收场景里。
    // 为什么放在 NBC.Boot 而不是 NBC.Game：它要用 AssetBootstrapper，而 asmdef 的引用方向是
    //   NBC.Boot -> NBC.Game（单向）。放进 Game 会报 CS0234 —— 这正是 2026-10-26 踩过的坑。
    public sealed class HpBarDemoBehaviour : MonoBehaviour
    {
        // 资源模式：编辑器里用 EditorSimulate（免打包，但要先构建过模拟清单）
        // 敌人用的模型 prefab（R015）；留空 = 表现层用胶囊
        [SerializeField]
        private GameObject m_monsterPrefab = null;

        // 攻击键（R019）；默认 J。RegisterAttackInput 会把它绑到动作 0 上
        [SerializeField]
        private KeyCode m_attackKey = KeyCode.J;

        [SerializeField] private AssetRuntimeMode m_mode = AssetRuntimeMode.EditorSimulate;

        // 最大血：血条满的时候的值
        [SerializeField] private int m_maxHp = 100;

        // 每次按空格扣多少血

        // 当前血
        private int m_hp;

        // 血条面板（打开成功后才有）
        private HpBarPanel m_panel;

        // 运行时网络驱动；HUD 每帧从它读服务端的真实血量（R020）
        private RuntimeNetDriver m_driver;

        // 资源层是否已经装好
        private bool m_assetsReady;

        // 功能：装资源层、顺手打开血条与网络面板，并挂上运行时网络驱动。
        // 入参：无（引擎在开始帧调一次）
        // 返回：无（异步方法，内部 await）
        // 步骤：
        //   1) 当前血设为满
        //   2) 找模拟清单目录，调组合根装资源层（先装加载器，再初始化，顺序不能反）
        //   3) 装好后打开血条面板并挂到 HUD 层（Bot）
        // 上游：Unity 引擎
        // 下游：AssetBootstrapper.InstallAsync、UIManager.ShowPanel
        // 注意：Battle 场景原本没有任何启动器，初始化必须由这里做，否则加载 prefab 会报“还没有装上底层加载器”
        private async void Start()
        {
            m_hp = m_maxHp;

            // R042 返修：重开副本会重新加载 Battle 场景，Start 会再跑一遍；
            //   资源层是单例，第二次 InstallAsync 会抛「已经装过加载器了」，
            //   而 SetCanvasLocation 在 Canvas 已开始加载后也会抛异常 ⇒ 已就绪就整段跳过。
            bool alreadyInstalled = AssetManager.Instance.IsInitialized;

            if (!alreadyInstalled)
            {
                try
                {
                    string root = ResolveSimulatePackageRoot();
                    await AssetBootstrapper.InstallAsync(m_mode, AssetBootstrapper.DefaultPackageName, root);
                    m_assetsReady = true;
                }
                catch (Exception ex)
                {
                    Debug.LogError("[HpBarDemo] 资源层初始化失败：" + ex.Message);
                    return;
                }
            }
            else
            {
                m_assetsReady = true;
                Debug.Log("[HpBarDemo] 资源层已就绪（重开副本），直接复用");
            }

            if (!alreadyInstalled)
            {
                // ⚠️ 地址对齐（2026-10-26 实际踩过）：
                //   UIManager 的默认地址是 "UI/Canvas" 与 "UI/<面板名>"，
                //   而我们的收集器规则是 AddressByFileName ⇒ 地址只是 "Canvas" / "HpBarPanel"。
                //   不对齐就会报 Location is invalid: 'UI/Canvas'。必须在第一次 ShowPanel 之前设（之后会抛异常）。
                UIManager.Instance.SetCanvasLocation("Canvas");
                UIManager.Instance.SetPanelLocationPrefix(string.Empty);
            }

            EnsureConfigSource();

            UIManager.Instance.ShowPanel(
                "HpBarPanel",
                UILayer.Bot,
                panel =>
                {
                    m_panel = panel as HpBarPanel;

                    if (m_panel != null)
                    {
                        m_panel.SetHp(m_hp, m_maxHp);
                    }
                },
                error => Debug.LogError("[HpBarDemo] 打不开血条面板：" + error.Message));

            // R004：顺带把网络状态面板也打开（右上角）。它自己每 0.25 秒读一次统计，这里不需要喂数据。
            UIManager.Instance.ShowPanel(
                "NetHudPanel",
                UILayer.Bot,
                null,
                error => Debug.LogError("[HpBarDemo] 打不开网络面板：" + error.Message));

        // R008：资源层与面板都就绪后，才挂运行时网络驱动。
            //   顺序有意义：它一挂上就会建会话，而会话要用 HUD 的统计（NetRuntimeStats）与资源层。
            //   ⚠️ 与 Editor 的「网络调试」窗口不要同时连 —— 会占两个座位。
            RegisterAttackInput();

            // R049：入口脚本可能已经建好跨场景的 [DungeonSession] 并挂了驱动，这里只在没有驱动时才补一个。
            RuntimeNetDriver existing = FindObjectOfType<RuntimeNetDriver>();
            if (existing != null)
            {
                existing.MonsterPrefab = m_monsterPrefab;
                m_driver = existing;
                Debug.Log("[HpBarDemo] 沿用已有的 RuntimeNetDriver（由副本入口创建）");
                return;
            }

            RuntimeNetDriver driver = gameObject.AddComponent<RuntimeNetDriver>();
            driver.MonsterPrefab = m_monsterPrefab;
            m_driver = driver;
            Debug.Log("[HpBarDemo] 已挂上 RuntimeNetDriver（自动连服 / 进房 / 发输入）");
        }

        // 功能：接上配置表来源，并把全部配置表预加载进内存（HUD、任务、掉落都要查表）。
        // 入参：无。
        // 返回：无（加载是异步的，失败只打一条错误日志）。
        // 步骤：1) 转交 BattleConfigBootstrap.Ensure（设来源 + 空表时预加载，重复调用无害）。
        // 上游：Start（资源层就绪之后）。
        // 下游：BattleConfigBootstrap.Ensure。
        // 注意：顺序与幂等由 BattleConfigBootstrap 统一负责，这里只提供进副本这个时机。
        private void EnsureConfigSource()
        {
            BattleConfigBootstrap.Ensure(
                reason => Debug.LogError("[HpBarDemo] 预加载配置表失败：" + reason));
        }

        // 功能：按空格扣血并刷新血条（演示用）。
        // 入参：无（引擎每帧调）
        // 返回：无
        // 步骤：
        //   1) 资源没装好、或面板还没打开 ⇒ 什么都不做
        //   2) 按空格才处理
        //   3) 扣血（钳到 0）后交给面板
        // 上游：Unity 引擎
        // 下游：HpBarPanel.SetHp
        // 注意：这里只为演示扣血；正式战斗的血量必须来自服务端快照
        // 功能：把攻击键登记成一个输入动作（动作 0 = 服务端约定的普攻位）。
        // 入参：无（键位来自 Inspector 的 m_attackKey）
        // 返回：无
        // 步骤：
        //   1) 建一张键位表（构造时自带 WASD 默认键）
        //   2) 声明动作 0（名字 Attack），主键绑 m_attackKey、备用键绑鼠标左键
        //   3) 把键位表注入 InputManager，再登记该动作 —— 不登记就永远产不出动作位
        // 上游：HpBarDemoBehaviour 装配流程
        // 下游：LegacyInputSource、InputManager.TrackAction、RuntimeNetDriver（读 ActionBits 第 0 位）
        // 注意：动作 0 是**服务端约定**（RoomBattleService 里 `ActionBits & 1u` 就是普攻），别改成别的编号
        // 注意：只绑 J 的话，玩家用鼠标左键只会看到本机动画、网络层不发攻击请求（表现成「打不掉血」）
        private void RegisterAttackInput()
        {
            if (InputManager.Instance == null)
            {
                Debug.LogError("[HpBarDemo] InputManager 还没就绪，攻击键登记失败。");
                return;
            }

            InputMapping mapping = new InputMapping();
            // 用 GetOrDeclare 而不是 Declare：重开副本会重载场景、Start 会再跑一遍，
            //   而动作声明是进程级的静态表，第二次 Declare 会抛「索引 0 已经被声明过了」。
            InputActionId attack = InputActionId.GetOrDeclare(0, "Attack");
            mapping.Bind(attack, m_attackKey);
            mapping.BindAlternate(attack, KeyCode.Mouse0);

            InputManager.Instance.Source = new LegacyInputSource(mapping);
            InputManager.Instance.TrackAction(attack);

            Debug.Log("[HpBarDemo] 攻击键已登记：" + m_attackKey + " / 鼠标左键（动作 0 = 普攻）");
        }
        private void Update()
        {
            if (!m_assetsReady || m_panel == null || m_driver == null)
            {
                return;
            }

            // R020：HUD 显示服务端的真实血量；拿到之前（HeroMaxHp == 0）保持面板原样
            if (m_driver.HeroMaxHp > 0 && m_driver.HeroHp != m_hp)
            {
                m_hp = m_driver.HeroHp;
                m_panel.SetHp(m_driver.HeroHp, m_driver.HeroMaxHp);
            }
        }

        // 功能：找出最新的模拟清单目录，交给资源层安装。
        // 入参：无
        // 返回：模拟清单目录（找不到返回空串，让底层自己报错）
        // 步骤：转交 AssetPackageRoot.Resolve —— 这段逻辑 AppBootstrap 也要用，抽出去只有一份。
        // 上游：Start
        // 下游：AssetPackageRoot.Resolve
        private static string ResolveSimulatePackageRoot()
        {
            return AssetPackageRoot.Resolve();
        }

        // 功能：离开副本场景时把 HUD 面板关掉。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先取 UIManager 单例；没初始化（例如编辑器里只打开了 World）就直接返回
        //   2) 依次关闭血条面板与网络面板，让它们从屏幕上消失
        // 上游：Unity 在 Battle 场景卸载时调用
        // 下游：UIManager.HidePanel
        // 注意：面板挂在 DontDestroyOnLoad 的 Canvas 下，不会随场景一起销毁；
        //       不在这里关，回到 World 后血条会一直留在屏幕上。
        private void OnDestroy()
        {
            UIManager ui = UIManager.Instance;

            if (ui == null)
            {
                return;
            }

            ui.HidePanel("HpBarPanel");
            ui.HidePanel("NetHudPanel");
        }
    }
}
