// 这个文件里有：
//   HpBarDemoBehaviour（类）：演示脚手架，装资源层 → 开血条面板 → 按空格扣血

using System;
using System.IO;
using System.Threading.Tasks;
using NBC.Framework.Asset;
using NBC.Framework.UI;
using NBC.Game.Bootstrap;
using NBC.Game.UI;
using UnityEngine;

namespace NBC.Boot
{
    // 主要功能：把「资源层初始化 + 打开血条 HUD + 按空格扣血」合成一个演示脚手架，只用于人工验收。
    // 主要数据成员：m_mode（资源模式）、m_maxHp（最大血）、m_damagePerPress（每次扣多少）。
    // 主要外部方法：无（挂到场景物体上自动跑）；正式战斗 HUD 接上后本类可删。
    // 谁在用它：只挂在验收场景里。
    // 为什么放在 NBC.Boot 而不是 NBC.Game：它要用 AssetBootstrapper，而 asmdef 的引用方向是
    //   NBC.Boot -> NBC.Game（单向）。放进 Game 会报 CS0234 —— 这正是 2026-10-26 踩过的坑。
    public sealed class HpBarDemoBehaviour : MonoBehaviour
    {
        // 资源模式：编辑器里用 EditorSimulate（免打包，但要先构建过模拟清单）
        [SerializeField] private AssetRuntimeMode m_mode = AssetRuntimeMode.EditorSimulate;

        // 最大血：血条满的时候的值
        [SerializeField] private int m_maxHp = 100;

        // 每次按空格扣多少血
        [SerializeField] private int m_damagePerPress = 10;

        // 当前血
        private int m_hp;

        // 血条面板（打开成功后才有）
        private HpBarPanel m_panel;

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

            // ⚠️ 地址对齐（2026-10-26 实际踩过）：
            //   UIManager 的默认地址是 "UI/Canvas" 与 "UI/<面板名>"，
            //   而我们的收集器规则是 AddressByFileName ⇒ 地址只是 "Canvas" / "HpBarPanel"。
            //   不对齐就会报 Location is invalid: 'UI/Canvas'。必须在第一次 ShowPanel 之前设（之后会抛异常）。
            UIManager.Instance.SetCanvasLocation("Canvas");
            UIManager.Instance.SetPanelLocationPrefix(string.Empty);

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
            gameObject.AddComponent<RuntimeNetDriver>();
            Debug.Log("[HpBarDemo] 已挂上 RuntimeNetDriver（自动连服 / 进房 / 发输入）");
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
        private void Update()
        {
            if (!m_assetsReady || m_panel == null || !Input.GetKeyDown(KeyCode.Space))
            {
                return;
            }

            m_hp = Mathf.Max(0, m_hp - m_damagePerPress);
            m_panel.SetHp(m_hp, m_maxHp);
        }

        // 功能：找出最新的模拟清单目录，交给资源层安装。
        // 入参：无
        // 返回：模拟清单目录（找不到返回空串，让底层自己报错）
        // 步骤：
        //   1) 在 <工程根>/Bundles/<平台>/<包名>/ 下遍历子目录
        //   2) 只认里面有 DefaultPackage*.bytes 的目录（把 OutputCache 之类排除）
        //   3) 取清单文件最新的那一个
        // 上游：Start
        // 下游：AssetBootstrapper.InstallAsync
        private static string ResolveSimulatePackageRoot()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string packageFolder = Path.Combine(projectRoot,
                "Bundles", "StandaloneWindows64", AssetBootstrapper.DefaultPackageName);

            if (!Directory.Exists(packageFolder))
            {
                return string.Empty;
            }

            string newest = string.Empty;
            DateTime newestTime = DateTime.MinValue;
            string[] subDirectories = Directory.GetDirectories(packageFolder);

            for (int i = 0; i < subDirectories.Length; i++)
            {
                string[] manifests = Directory.GetFiles(
                    subDirectories[i], AssetBootstrapper.DefaultPackageName + "*.bytes");

                if (manifests.Length == 0)
                {
                    continue;
                }

                DateTime time = File.GetLastWriteTime(manifests[0]);

                if (time > newestTime)
                {
                    newestTime = time;
                    newest = subDirectories[i];
                }
            }

            return newest;
        }
    }
}
