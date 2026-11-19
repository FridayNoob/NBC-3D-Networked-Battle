// 这个文件里有：
//   BattleReadiness（静态类）：判断战斗表现层所需的资源层与配置表是否就绪，并说明缺什么。

using NBC.Framework.Asset;
using NBC.Game.Config;

namespace NBC.Game.Bootstrap
{
    // 主要功能：把「现在能不能让表现层干活」收敛成几个只读判断，避免每个消费者各写一套。
    // 主要数据成员：HudTables（HUD 真正依赖的三张表名）；其余都是即时计算，不缓存状态。
    // 主要外部方法：AssetsReady / ConfigSourceReady / HudTablesReady / HudReady / DescribeMissing。
    // 谁在用它：RuntimeNetDriver（能否挂 HUD）、RuntimeHudDriver（能否请求面板与查表）、BattleBootSequence（编排各步）。
    // 注意：这里只回答「现在是不是就绪」，不做任何加载与等待 —— 加载由启动方负责，等待由调用方按帧重试。
    public static class BattleReadiness
    {
        // HUD 真正要用的三张表（表名不带 Config 后缀，和 GameTables.All 里的写法一致）
        private static readonly string[] HudTables = { "Quest", "QuestCondition", "Monster" };

        // 功能：资源层（AssetManager）是否已经装好加载器并初始化。
        // 入参：无。
        // 返回：true = 可以加载资源；false = 还没装好。
        // 步骤：1) 取单例 2) 判空 3) 读 IsInitialized。
        // 上游：所有需要加载预制体/配置的调用方；下游：AssetManager.IsInitialized。
        public static bool AssetsReady
        {
            get
            {
                AssetManager assets = AssetManager.Instance;
                return assets != null && assets.IsInitialized;
            }
        }

        // 功能：配置表来源是否已经设置（没设来源时任何 Preload 都会立刻失败）。
        // 入参：无。
        // 返回：true = 有来源；false = 还没 SetSource。
        // 步骤：1) 取单例 2) 判空 3) 读 Source。
        // 上游：BattleConfigBootstrap、RuntimeHudDriver；下游：ConfigMgr.Source。
        public static bool ConfigSourceReady
        {
            get
            {
                ConfigMgr mgr = ConfigMgr.Instance;
                return mgr != null && mgr.Source != null;
            }
        }

        // 功能：GameTables.All 里的表是否都已加载进内存。
        // 入参：无。
        // 返回：true = 全部加载完；false = 还有表在加载或加载失败。
        // 步骤：1) 取单例 2) 比 LoadedTableCount 与 All.Length。
        // 上游：BattleBootSequence 判「表好了没有」；下游：ConfigMgr.LoadedTableCount。
        public static bool AllTablesReady
        {
            get
            {
                ConfigMgr mgr = ConfigMgr.Instance;
                return mgr != null && mgr.LoadedTableCount >= GameTables.All.Length;
            }
        }

        // 功能：HUD 要用的三张表（Quest / QuestCondition / Monster）是否都在内存里。
        // 入参：无。
        // 返回：true = 三张全有；false = 缺任意一张。
        // 步骤：1) 取单例 2) 逐张 IsLoaded 3) 有一张缺就返回 false。
        // 上游：BattleBootSequence、RuntimeHudDriver.ResolveConfigs；下游：ConfigMgr.IsLoaded。
        public static bool HudTablesReady
        {
            get
            {
                ConfigMgr mgr = ConfigMgr.Instance;

                if (mgr == null)
                {
                    return false;
                }

                for (int i = 0; i < HudTables.Length; i++)
                {
                    if (!mgr.IsLoaded(HudTables[i]))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        // 功能：HUD 能不能挂了（资源层就绪 + 三张表都在）。
        // 入参：无。
        // 返回：true = 可以 Bind；false = 还得等。
        // 步骤：1) AssetsReady 2) HudTablesReady 3) 两者都真才返回 true。
        // 上游：RuntimeNetDriver；下游：AssetsReady、HudTablesReady。
        public static bool HudReady
        {
            get { return AssetsReady && HudTablesReady; }
        }

        // 功能：说清现在缺什么（给日志用，不要拿它做判断）。
        // 入参：无。
        // 返回：缺项的人话描述；都齐了返回空串。
        // 步骤：1) 依次判资源层、配置来源、HUD 三张表 2) 第一个缺的写成一句话返回。
        // 上游：BattleBootSequence 失败时、驱动诊断日志；下游：无。
        public static string DescribeMissing()
        {
            if (!AssetsReady)
            {
                return "资源层还没装好";
            }

            if (!ConfigSourceReady)
            {
                return "配置表来源还没设置";
            }

            if (!HudTablesReady)
            {
                return "任务/怪物配置表还没加载完";
            }

            return string.Empty;
        }
    }
}
