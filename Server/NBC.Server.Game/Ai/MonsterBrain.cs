// 这个文件里有：
//   MonsterBrain（类）：一只怪的大脑 —— 持有行为树与上下文，每逻辑帧 Tick 一次。
//   它替代了原来的 BossBrain（那套 3 状态机），所有怪（含 BOSS）都用行为树。

namespace NBC.Server.Game.Ai
{
    // 主要功能：把「行为树」接到服务端战斗上：每帧刷新目标 → Tick 树 → 把结果翻成可读状态（日志/探针用）。
    // 主要数据成员：HomeXmm / HomeZmm（出生点）、StateName（最近一个执行到的动作名）、TransitionCount（状态切换次数）、m_root / m_ctx。
    // 主要外部方法：Think（每逻辑帧调用一次）。
    // 谁在用它：DungeonBattle.Step 里的 _brains 循环。
    // 注意：大脑**不自己移动**，移动都由节点的 BtOps 调世界的 MoveTowardsPoint；大脑只负责决策与置 Chasing 标志。
    public sealed class MonsterBrain
    {
        // 这个大脑控制的实体编号（探针按 id 找对应大脑，不依赖列表顺序）
        public int EntityId { get; }

        // 出生点 X（毫米）
        public int HomeXmm { get; }

        // 出生点 Z（毫米）
        public int HomeZmm { get; }

        // 最近一个执行到的动作名（"巡逻"/"追击"/"攻击"/"回位"/"站桩"）；给日志与探针用
        public string StateName { get; private set; } = "站桩";

        // 状态切换次数（排查"AI 抖不抖"用）
        public int TransitionCount { get; private set; }

        // 行为树根节点
        private readonly BtNode m_root;

        // 执行上下文（每帧复用，避免每帧分配）
        private readonly BtContext m_ctx = new BtContext();

        // 功能：构造一只怪的大脑（按 aiKind 取树，出生点取当前坐标）。
        // 入参：self 这只怪；aiKind 行为树类型（wolf / boss）；profile AI 参数档位
        // 返回：无
        // 步骤：记出生点 → 取树 → 填上下文常量部分
        // 上游：DungeonBattle.FromDungeon
        // 下游：DefaultAiTrees.Build
        public MonsterBrain(BattleEntity self, string aiKind, MonsterAiProfile profile)
        {
            EntityId = self.Id;
            HomeXmm = self.PosXmm;
            HomeZmm = self.PosZmm;

            m_root = DefaultAiTrees.Build(aiKind);

            m_ctx.Self = self;
            m_ctx.HomeXmm = HomeXmm;
            m_ctx.HomeZmm = HomeZmm;
            m_ctx.Profile = profile;
        }

        // 功能：想一帧（刷新上下文 → Tick 树 → 更新状态名）。
        // 入参：world 世界
        // 返回：无
        // 步骤：
        //   1) 填 World / Tick / Chasing 初值
        //   2) Tick 根节点（节点内部会刷新 Target 并可能改 Chasing）
        //   3) 把 Self.Chasing 同步给实体（Step 用它跳过巡逻）
        //   4) 从上下文里取最近动作名，变了就累加切换次数
        // 上游：DungeonBattle.Step
        // 下游：BtNode.Tick / BtOps
        public void Think(DungeonBattle world)
        {
            m_ctx.World = world;
            m_ctx.Tick = (int)world.Tick;
            m_ctx.Chasing = false;

            // 目标检测放在大脑里（不进树）：
            // ① 检测是「每帧都要做的前置动作」，不是一种行为；
            // ② 若做成树里的动作节点，它返回 Success 会让 Selector 立刻短路，后面的追击/攻击永远不执行（踩过）。
            m_ctx.Target = BtOps.FindNearestHero(m_ctx);
            m_root.Tick(m_ctx);

            // 把"这一帧要不要巡逻"的决定交给实体（Step 在 Chasing 时跳过自己的巡逻位移）
            m_ctx.Self.Chasing = m_ctx.Chasing;

            string next = m_ctx.LastAction ?? StateName;

            if (next != StateName)
            {
                StateName = next;
                TransitionCount++;
            }
        }
    }
}
