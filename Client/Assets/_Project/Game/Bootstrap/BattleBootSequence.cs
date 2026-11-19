// 这个文件里有：
//   EBootStep（枚举）、BattleBootSequence（类）：进副本装表现层的四步编排，带超时与失败原因。

using System;

namespace NBC.Game.Bootstrap
{
    // 战斗表现层启动的四个阶段（顺序即执行顺序）。
    public enum EBootStep
    {
        // 等资源层装好（HpBarDemo 里 await InstallAsync 那一段）
        WaitAssets = 0,

        // 设配置表来源（缺来源时任何 Preload 都会失败）
        PrepareConfigs = 1,

        // 等 HUD 要用的配置表加载完
        WaitTables = 2,

        // 打开 HUD 面板并接上数据源
        BindHud = 3,

        // 四步都完成
        Done = 4
    }

    // 主要功能：把「进副本要做哪几件有先后的事」写成一条可观察的步骤链，每步带超时与失败原因。
    // 主要数据成员：m_step（当前步）、m_stepElapsed（当前步已耗时，秒）、m_stepTimeoutSeconds（单步超时，秒）。
    // 主要外部方法：Begin / Tick / Cancel；事件 Completed / Failed。
    // 谁在用它：RuntimeNetDriver（每次进副本 Begin 一次，Update 里 Tick）。
    // 注意：本类不直接碰 Unity 与单例，全部依赖由构造函数注入 —— 这样 EditMode 测试能拿假条件跑。
    public sealed class BattleBootSequence
    {
        // 单步默认超时：8 秒还没好就判失败（资源加载慢于这个值基本就是出问题了）
        public const float DefaultStepTimeoutSeconds = 8f;

        // 读「资源层好了吗」
        private readonly Func<bool> m_assetsReady;

        // 读「配置来源设了吗」
        private readonly Func<bool> m_configSourceReady;

        // 读「HUD 要用的表齐了吗」
        private readonly Func<bool> m_tablesReady;

        // 第二步要执行的动作（设来源 + 发起预加载）
        private readonly Action m_prepareConfigs;

        // 第四步要执行的动作（打开面板、接上数据源）
        private readonly Action m_bindHud;

        // 日志出口（可空；空则不输出）
        private readonly Action<string> m_log;

        // 单步超时秒数
        private readonly float m_stepTimeoutSeconds;

        // 当前步骤
        private EBootStep m_step = EBootStep.WaitAssets;

        // 当前步骤已经耗时多少秒
        private float m_stepElapsed;

        // 是否在跑（Begin 之后、完成/取消之前为 true）
        private bool m_running;

        // 是否已经完成过一次
        private bool m_finished;

        // 功能：注入四个判断/动作与日志出口。
        // 入参：assetsReady/configSourceReady/tablesReady 三个只读判断；prepareConfigs/bindHud 两个动作；log 日志出口可空；stepTimeoutSeconds 单步超时秒数。
        // 返回：无（构造即就绪，调用 Begin 才开始跑）。
        // 步骤：1) 逐项存下依赖 2) 超时取正数（传 0 或负数时回落到默认值）。
        // 上游：RuntimeNetDriver、EditMode 测试。
        public BattleBootSequence(
            Func<bool> assetsReady,
            Func<bool> configSourceReady,
            Func<bool> tablesReady,
            Action prepareConfigs,
            Action bindHud,
            Action<string> log = null,
            float stepTimeoutSeconds = DefaultStepTimeoutSeconds)
        {
            m_assetsReady = assetsReady;
            m_configSourceReady = configSourceReady;
            m_tablesReady = tablesReady;
            m_prepareConfigs = prepareConfigs;
            m_bindHud = bindHud;
            m_log = log;
            m_stepTimeoutSeconds = stepTimeoutSeconds > 0f ? stepTimeoutSeconds : DefaultStepTimeoutSeconds;
        }

        // 四步都走完时触发（只触发一次）
        public event Action Completed;

        // 某一步超时时触发，参数是人话原因
        public event Action<string> Failed;

        // 当前步骤
        public EBootStep Step
        {
            get { return m_step; }
        }

        // 是否正在跑
        public bool IsRunning
        {
            get { return m_running; }
        }

        // 是否已经跑完过
        public bool IsFinished
        {
            get { return m_finished; }
        }

        // 功能：从头开始跑这条链（重复调用会重置到第一步）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 复位步骤与计时 2) 置运行中 3) 打一条开始日志。
        // 上游：RuntimeNetDriver.EnsureHud；下游：无。
        public void Begin()
        {
            m_step = EBootStep.WaitAssets;
            m_stepElapsed = 0f;
            m_running = true;
            m_finished = false;
            Log("[Boot] 开始装表现层（第 1 步：等资源层）");
        }

        // 功能：推进一次（由调用方每帧驱动）。
        // 入参：deltaSeconds 本帧耗时（秒）。
        // 返回：无。
        // 步骤：1) 没在跑就返回 2) 累加耗时并判超时 3) 按当前步的条件决定前进还是继续等。
        // 上游：RuntimeNetDriver.Update；下游：m_prepareConfigs、m_bindHud、Completed、Failed。
        public void Tick(float deltaSeconds)
        {
            if (!m_running)
            {
                return;
            }

            m_stepElapsed += deltaSeconds > 0f ? deltaSeconds : 0f;

            if (m_stepElapsed > m_stepTimeoutSeconds)
            {
                Fail("第 " + ((int)m_step + 1) + " 步（" + NameOf(m_step) + "）超过 " + m_stepTimeoutSeconds + " 秒还没完成");
                return;
            }

            switch (m_step)
            {
                case EBootStep.WaitAssets:
                    if (m_assetsReady != null && m_assetsReady())
                    {
                        Enter(EBootStep.PrepareConfigs);
                    }

                    break;

                case EBootStep.PrepareConfigs:
                    if (m_configSourceReady != null && m_configSourceReady())
                    {
                        Enter(EBootStep.WaitTables);
                    }

                    break;

                case EBootStep.WaitTables:
                    if (m_tablesReady != null && m_tablesReady())
                    {
                        // 表刚齐：同一次 Tick 里就走到最后一步，避免「等下一帧才挂 HUD」这类一帧空窗
                        Enter(EBootStep.BindHud);
                        Finish();
                    }

                    break;

                case EBootStep.BindHud:
                    Finish();
                    break;
            }
        }

        // 功能：收尾 —— 挂 HUD、标记完成、触发 Completed（只会执行一次）。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 先置完成状态（避免动作里重入）2) 执行 m_bindHud 3) 打日志并触发 Completed。
        // 上游：Tick（表齐那一刻、BindHud 步）；下游：m_bindHud、Completed。
        private void Finish()
        {
            if (m_finished)
            {
                return;
            }

            m_step = EBootStep.Done;
            m_running = false;
            m_finished = true;
            m_stepElapsed = 0f;

            if (m_bindHud != null)
            {
                m_bindHud();
            }

            Log("[Boot] 表现层就绪");
            Completed?.Invoke();
        }

        // 功能：取消（回世界、切场景时调），之后不再触发任何回调。
        // 入参：无。
        // 返回：无。
        // 步骤：1) 清运行标记 2) 打一条日志。
        // 上游：RuntimeNetDriver.CloseHud；下游：无。
        public void Cancel()
        {
            if (!m_running)
            {
                return;
            }

            m_running = false;
            Log("[Boot] 已取消（第 " + ((int)m_step + 1) + " 步：" + NameOf(m_step) + "）");
        }

        // 功能：步骤名的中文说法（日志与失败原因用）。
        // 入参：step 步骤。
        // 返回：中文名字。
        // 步骤：1) 按枚举返回固定文案。
        // 上游：Tick、Cancel；下游：无。
        public static string NameOf(EBootStep step)
        {
            switch (step)
            {
                case EBootStep.WaitAssets:
                    return "等资源层";
                case EBootStep.PrepareConfigs:
                    return "设配置来源";
                case EBootStep.WaitTables:
                    return "等配置表";
                case EBootStep.BindHud:
                    return "挂 HUD";
                default:
                    return "完成";
            }
        }

        // 功能：切到下一步，并在进入「设配置来源」时执行一次准备动作。
        // 入参：next 目标步骤。
        // 返回：无。
        // 步骤：1) 记新步骤并清零计时 2) 打日志 3) 轮到准备配置时执行一次 m_prepareConfigs。
        // 上游：Tick；下游：m_prepareConfigs。
        private void Enter(EBootStep next)
        {
            m_step = next;
            m_stepElapsed = 0f;
            Log("[Boot] 进入第 " + ((int)next + 1) + " 步：" + NameOf(next));

            if (next == EBootStep.PrepareConfigs && m_prepareConfigs != null)
            {
                m_prepareConfigs();
            }
        }

        // 功能：判超时失败并停止。
        // 入参：reason 人话原因。
        // 返回：无。
        // 步骤：1) 停跑 2) 打错误日志 3) 触发 Failed。
        // 上游：Tick；下游：Failed 订阅方。
        private void Fail(string reason)
        {
            m_running = false;
            Log("[Boot] 失败：" + reason);
            Failed?.Invoke(reason);
        }

        // 功能：写一条日志（没注入日志出口时什么都不做）。
        // 入参：message 文本。
        // 返回：无。
        // 步骤：1) 判空 2) 转调注入的日志动作。
        // 上游：Begin/Enter/Fail/Cancel/Tick；下游：注入的日志出口。
        private void Log(string message)
        {
            if (m_log != null)
            {
                m_log(message);
            }
        }
    }
}
