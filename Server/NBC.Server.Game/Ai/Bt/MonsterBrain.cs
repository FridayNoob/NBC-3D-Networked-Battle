// 这个文件里有：
//   MonsterBrain（类）：一只怪的大脑 —— 每逻辑帧刷新目标、Tick 行为树、维护状态名。
//
// 目标检测放在大脑里而不是树节点：树里的"检测节点"一旦返回成功，就会让 Selector 吃掉整帧（R022 的老坑）。

namespace NBC.Server.Game.Ai.Bt
{
    // 主要功能：把"世界 + 行为树"接起来 —— 每帧刷新最近英雄、Tick 根节点、把状态写回实体。
    // 主要数据成员：EntityId / HomeXmm / HomeZmm / StateName / m_ctx / m_root。
    // 主要外部方法：Think(world)。
    // 谁在用它：DungeonBattle 的每帧循环（`_brains`）。
    // 注意：`m_ctx` 每只怪一个、跨帧复用；`Self.Chasing` 每帧由 `ctx.Chasing` 覆盖，避免动作切换后残留。
    public sealed class MonsterBrain
    {
        // 大脑驱动的实体编号
        public int EntityId { get; }

        // 出生点 X（毫米）
        public int HomeXmm { get; }

        // 出生点 Z（毫米）
        public int HomeZmm { get; }

        // 当前状态名（日志/调试用：追击 / 普攻 / 回位 / 巡逻 / 站桩）
        public string StateName => m_ctx.LastAction ?? "站桩";

        // 状态切换次数（探针用）
        public int TransitionCount { get; private set; }

        // 复用的上下文
        private readonly BtContext m_ctx;

        // 树根
        private readonly BtNode m_root;

        // 上一次的状态名（统计切换次数）
        private string m_lastStateName;

        // 功能：建大脑。
        // 入参：self 实体；aiKind 种类名（wolf / boss）；profile 该怪型的 AI 参数
        // 返回：无
        // 步骤：记实体与出生点 → 建树 → 建上下文
        // 上游：DungeonBattle.BuildDungeon
        // 下游：BtTreeBuilder.Build
        public MonsterBrain(BattleEntity self, string aiKind, MonsterAiProfile profile)
        {
            EntityId = self.Id;
            HomeXmm = self.PosXmm;
            HomeZmm = self.PosZmm;
            m_root = BtTreeBuilder.Build(aiKind);
            m_ctx = new BtContext
            {
                Self = self,
                HomeXmm = HomeXmm,
                HomeZmm = HomeZmm,
                Profile = profile
            };
        }

        // 功能：跑一帧。
        // 入参：world 战斗世界
        // 返回：无
        // 步骤：挂世界 → 刷新目标（仇恨范围内最近英雄）→ 清本帧动作 → Tick 树 → 回写 Chasing → 记状态
        // 上游：DungeonBattle.Tick
        // 下游：BtNode.Tick
        public void Think(DungeonBattle world)
        {
            if (world == null)
            {
                return;
            }

            m_ctx.World = world;
            m_ctx.Tick++;
            m_ctx.Target = BtActionOps.FindNearestHero(m_ctx);
            m_ctx.Chasing = false;
            m_ctx.LastAction = null;

            m_root.Tick(m_ctx);

            m_ctx.Self.Chasing = m_ctx.Chasing;

            string current = StateName;

            if (current != m_lastStateName)
            {
                m_lastStateName = current;
                TransitionCount++;
            }
        }
    }
}
