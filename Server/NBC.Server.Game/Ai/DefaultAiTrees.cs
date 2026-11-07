// 这个文件里有：
//   DefaultAiTrees（静态类）：内置的两棵树（野狼 / BOSS）+ 「优先读 JSON、失败回落内置」的加载入口。

using System;
using System.IO;

namespace NBC.Server.Game.Ai
{
    // 主要功能：给出每种怪的行为树 —— 先找编辑器导出的 JSON（BehaviorTrees/<aiKind>.json），没有就用代码内置的默认树。
    // 主要数据成员：无（纯静态）。
    // 主要外部方法：Build（按 aiKind 拿树）。
    // 谁在用它：MonsterBrain 构造时。
    // 注意：内置树是**兜底**，保证服务端起得来、探针不依赖资源文件；正式内容以 JSON 为准。
    internal static class DefaultAiTrees
    {
        // 行为树 JSON 的目录（相对服务端进程工作目录）
        private const string TreeDirectory = "BehaviorTrees";

        // 野狼的 aiKind（也是 JSON 文件名，不含扩展名）
        public const string WolfAiKind = "wolf";

        // BOSS 的 aiKind
        public const string BossAiKind = "boss";

        // 功能：按 aiKind 取树：先尝试 JSON，再回落内置。
        // 入参：aiKind 类型名（wolf / boss）
        // 返回：行为树根节点
        // 步骤：
        //   1) 拼路径 BehaviorTrees/<aiKind>.json
        //   2) TryLoadFromFile 成功就用它
        //   3) 否则用代码内置树（BuildWolf / BuildBoss）
        // 上游：MonsterBrain
        // 下游：BtJsonLoader.TryLoadFromFile / BuildWolf / BuildBoss
        public static BtNode Build(string aiKind)
        {
            string path = Path.Combine(AppContext.BaseDirectory, TreeDirectory, aiKind + ".json");
            BtNode fromFile = BtJsonLoader.TryLoadFromFile(path);

            if (fromFile != null)
            {
                Console.WriteLine("[AI] 已加载行为树：" + path);
                return fromFile;
            }

            BtNode built = aiKind == BossAiKind ? BuildBoss() : BuildWolf();
            Console.WriteLine("[AI] 未找到 " + path + "，使用内置行为树（" + aiKind + "）。");
            return built;
        }

        // 功能：野狼的内置树：死了什么都不做 → 攻击 → 追 → 巡逻。
        // 入参：无
        // 返回：根节点
        // 步骤：按 Selector 优先级拼树（与编辑器里摆的顺序一致）
        // 上游：Build
        // 下游：无
        // 注意：DetectTarget 必须在前 —— 它负责刷新 ctx.Target，后面的条件都依赖它
        private static BtNode BuildWolf()
        {
            BtSelector root = new BtSelector();

            root.AddChild(new BtIsDead());

            root.AddChild(new BtSequence()
                .AddChild(new BtHasTarget())
                .AddChild(new BtTargetInAttackRange())
                .AddChild(new BtAttackReady())
                .AddChild(new BtBasicAttack()));

            root.AddChild(new BtSequence()
                .AddChild(new BtHasTarget())
                .AddChild(new BtChase()));

            root.AddChild(new BtPatrol());

            return root;
        }

        // 功能：BOSS 的内置树：死了什么都不做 → 攻击 → 追 → 离家远就回位 → 站桩。
        // 入参：无
        // 返回：根节点
        // 步骤：与野狼相同的前三段；第四段是回位，最后是站桩（BOSS 不巡逻）
        // 上游：Build
        // 下游：无
        private static BtNode BuildBoss()
        {
            BtSelector root = new BtSelector();

            root.AddChild(new BtIsDead());

            root.AddChild(new BtSequence()
                .AddChild(new BtHasTarget())
                .AddChild(new BtTargetInAttackRange())
                .AddChild(new BtAttackReady())
                .AddChild(new BtBasicAttack()));

            root.AddChild(new BtSequence()
                .AddChild(new BtHasTarget())
                .AddChild(new BtChase()));

            root.AddChild(new BtSequence()
                .AddChild(new BtHomeFar())
                .AddChild(new BtReturnHome()));

            root.AddChild(new BtIdle());

            return root;
        }
    }
}
