// 这个文件里有：
//   AppBootstrap（类）：整个客户端只装一次资源层与配置表，并把自己带到后面所有场景。

using System;
using System.Threading.Tasks;
using NBC.Framework.Asset;
using NBC.Framework.UI;
using NBC.Game.Bootstrap;
using UnityEngine;

namespace NBC.Boot
{
    // 主要功能：在 Login 场景启动时装好资源层、对齐 UIManager 的地址、预加载配置表，然后跨场景常驻。
    // 主要数据成员：m_mode（资源模式）、m_logDiagnostics（是否打日志）、Ready（是否装好）。
    // 主要外部方法：Ready（静态属性，供 World 的 HUD 判断能不能开始加载面板）。
    // 谁在用它：Login 场景的 [AppBootstrap]；World 的 WorldHudDriver 读 Ready 等着。
    // 注意：装在 Login 是因为「整局只该装一次」——World 与 Battle 都用同一份资源层，
    //       以前只有 Battle 的 HpBarDemo 会装，结果 World 里连一个 HUD 面板都加载不出来。
    [DefaultExecutionOrder(-200)]
    public sealed class AppBootstrap : MonoBehaviour
    {
        // 资源模式：编辑器里用 EditorSimulate（免打包，但要先构建过模拟清单）
        [SerializeField]
        private AssetRuntimeMode m_mode = AssetRuntimeMode.EditorSimulate;

        // 是否打诊断日志（默认开：启动阶段只有几条，值得看）
        [SerializeField]
        private bool m_logDiagnostics = true;

        // 资源层是否已经装好（静态：后面场景里的组件不用找物体就能问）
        public static bool Ready { get; private set; }

        // 最近一次失败原因（空串 = 没失败）
        public static string LastError { get; private set; } = string.Empty;

        // 进程里唯一的那一个（从 Login 带过来的那份；World 里自带的会在 Awake 里自杀）
        private static AppBootstrap s_instance;

        // 功能：保证整局只有一份，并把自己标记成常驻。
        // 入参：无（引擎调用）。
        // 返回：无。
        // 步骤：1) 已经有别人了就销毁自己 2) 否则记下自己并 DontDestroyOnLoad。
        // 上游：Unity 引擎（场景加载时）。
        // 注意：World 场景也挂了一份（方便直接从 World 按 Play 调试），靠这里去重。
        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // 功能：启动时装资源层、对齐地址、预加载配置表，并把自己标记成常驻。
        // 入参：无（引擎调用）。
        // 返回：无（async void 只用于 Unity 生命周期，内部自己兜异常）。
        // 步骤：
        //   1) 先 DontDestroyOnLoad，保证切场景不销毁（后续场景都靠这份资源层）
        //   2) 已经装过就跳过（重开编辑器播放、或有人先装过）
        //   3) 找模拟清单目录 → InstallAsync
        //   4) 第一次装好后对齐 UIManager 地址（之后再设会抛异常）
        //   5) 让 Game 层把配置表来源设好并预加载（幂等）
        // 上游：Unity 引擎（Login 场景启动）。
        // 下游：AssetPackageRoot.Resolve、AssetBootstrapper.InstallAsync、UIManager、BattleConfigBootstrap.Ensure。
        private async void Start()
        {
            if (Ready || AssetManager.Instance.IsInitialized)
            {
                Ready = true;
                Log("资源层已经就绪，直接复用");
                EnsureConfigs();
                return;
            }

            try
            {
                string root = AssetPackageRoot.Resolve();
                Log("开始装资源层，清单目录=" + (string.IsNullOrEmpty(root) ? "(空)" : root));
                await AssetBootstrapper.InstallAsync(
                    m_mode, AssetBootstrapper.DefaultPackageName, root);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.LogError("[AppBootstrap] 资源层初始化失败：" + ex.Message);
                return;
            }

            // ⚠️ 地址对齐：UIManager 默认找 "UI/Canvas" 与 "UI/<面板名>"，
            //   而收集器规则是 AddressByFileName ⇒ 真实地址只是 "Canvas" / "HpBarPanel"。
            //   这一步必须在第一次 ShowPanel 之前做，之后再做会抛异常。
            UIManager.Instance.SetCanvasLocation("Canvas");
            UIManager.Instance.SetPanelLocationPrefix(string.Empty);

            Ready = true;
            LastError = string.Empty;
            Log("资源层已就绪");

            EnsureConfigs();
        }

        // 功能：接上配置表来源并预加载（World 的任务追踪也要查表）。
        // 入参：无。
        // 返回：无。
        // 步骤：转交 BattleConfigBootstrap.Ensure（设来源 + 空表时预加载，重复调用无害）。
        // 上游：Start。
        // 下游：BattleConfigBootstrap.Ensure。
        private void EnsureConfigs()
        {
            BattleConfigBootstrap.Ensure(reason => Debug.LogError("[AppBootstrap] 预加载配置表失败：" + reason));
        }

        // 功能：打一条启动日志。
        // 入参：message 内容。
        // 返回：无。
        // 步骤：m_logDiagnostics 关掉时直接返回。
        // 上游：Start 的各个阶段。
        private void Log(string message)
        {
            if (m_logDiagnostics)
            {
                Debug.Log("[AppBootstrap] " + message);
            }
        }
    }
}
