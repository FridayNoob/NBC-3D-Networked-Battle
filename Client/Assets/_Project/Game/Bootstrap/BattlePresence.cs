// 这个文件里有：
//   BattlePresence：一张副本进出相关的开关表，进入与离开各只有一个入口。

namespace NBC.Game.Bootstrap
{
    // 功能：把「此刻我和副本的关系」收敛成四个开关，并提供唯一的进入 / 离开入口。
    // 主要数据成员：AutoJoin（看到在线就自动进房）、JoinRequested（已经发过进房请求）、
    //   HeroAlive（服务端说我的英雄还活着）、InDungeon（表现层正绑在副本里）。
    // 主要外部方法：EnterDungeon（进副本）、ExitToWorld（回世界）、SetHeroAlive（快照带来的生死）。
    // 谁在用它：RuntimeNetDriver 每个 Update 读它、DungeonEntrance 与结算流程改它。
    //
    // 为什么要单独成类：这四个开关互相依赖，只复位其中一个，下一次 Update 就会把「离开」重新推翻
    // （2026-11-20 的真实事故：退房后 m_autoJoin 仍为 true，下一帧又自动进房，人站在草地上却还在副本里）。
    // 收进一处之后，规则是「要么 EnterDungeon，要么 ExitToWorld，不许零散赋值」。
    public sealed class BattlePresence
    {
        // 是否允许「在线就自动进房」：进副本时为 true，回世界时必须为 false。
        private bool m_autoJoin = true;

        // 是否已经发过进房请求：防止每帧重复发。
        private bool m_joinRequested;

        // 服务端快照里我的英雄是否还活着：死了才会走死亡流程。
        private bool m_heroAlive = true;

        // 表现层是否正绑在副本里：只有它为 true 时，结算面板 / 死亡黑幕才该出现。
        private bool m_inDungeon = true;

        // 功能：现在允许自动进房吗。
        // 入参：无。
        // 返回：true = Update 里可以自己发进房请求。
        public bool AutoJoin
        {
            get { return m_autoJoin; }
        }

        // 功能：已经发过进房请求了吗。
        // 入参：无。
        // 返回：true = 本帧不要再发。
        public bool JoinRequested
        {
            get { return m_joinRequested; }
        }

        // 功能：服务端说我的英雄还活着吗。
        // 入参：无。
        // 返回：true = 活着；false = 已倒下（只在副本里有意义）。
        public bool HeroAlive
        {
            get { return m_heroAlive; }
        }

        // 功能：表现层现在是不是绑在副本里。
        // 入参：无。
        // 返回：true = 在副本里；false = 在世界 / 菜单。
        public bool InDungeon
        {
            get { return m_inDungeon; }
        }

        // 功能：直接设定「是否允许自动进房」。
        // 入参：value true 允许、false 禁止。
        // 返回：无。
        // 上游/下游：RuntimeNetDriver.AutoJoin 属性、DungeonEntrance 连接阶段临时关掉。
        public void SetAutoJoin(bool value)
        {
            m_autoJoin = value;
        }

        // 功能：记下「已发过进房请求」。
        // 入参：无。
        // 返回：无。
        // 上游/下游：RuntimeNetDriver.Update。
        public void MarkJoinRequested()
        {
            m_joinRequested = true;
        }

        // 功能：清掉「已发过进房请求」。
        // 入参：无。
        // 返回：无。
        // 上游/下游：RuntimeNetDriver 进房失败 / 重连后允许再试。
        public void ClearJoinRequested()
        {
            m_joinRequested = false;
        }

        // 功能：写入「我的英雄是否活着」。
        // 入参：value true 活着、false 倒下。
        // 返回：无。
        // 上游/下游：RuntimeNetDriver 的快照跟踪与离场复位。
        public void SetHeroAlive(bool value)
        {
            m_heroAlive = value;
        }

        // 功能：写入「表现层是否绑在副本里」。
        // 入参：value true 进副本、false 离开。
        // 返回：无。
        // 上游/下游：RuntimeNetDriver.BindPresentation 属性。
        public void MarkInDungeon(bool value)
        {
            m_inDungeon = value;
        }

        // 功能：进入副本：打开自动进房、清掉请求标记、按「活着」起步、打开表现层。
        // 入参：无。
        // 返回：无。
        // 步骤：一次把四个开关写成同一个方向，避免「进了一半」。
        // 上游/下游：DungeonEntrance 进副本成功后。
        public void EnterDungeon()
        {
            m_autoJoin = true;
            m_joinRequested = false;
            m_heroAlive = true;
            m_inDungeon = true;
        }

        // 功能：回世界：关掉自动进房、清请求标记、复位存活、关掉表现层。
        // 入参：无。
        // 返回：无。
        // 步骤：一次写成同一个方向；重复调用结果相同（幂等）。
        // 上游/下游：RuntimeNetDriver.ReturnToWorldRoutine 与死亡回世界流程。
        public void ExitToWorld()
        {
            m_autoJoin = false;
            m_joinRequested = false;
            m_heroAlive = true;
            m_inDungeon = false;
        }

        // 功能：把四个开关拼成一行，供诊断日志使用。
        // 入参：无。
        // 返回：例如「autoJoin=False joinReq=False heroAlive=True inDungeon=False」。
        public override string ToString()
        {
            return "autoJoin=" + m_autoJoin
                + " joinReq=" + m_joinRequested
                + " heroAlive=" + m_heroAlive
                + " inDungeon=" + m_inDungeon;
        }
    }
}
