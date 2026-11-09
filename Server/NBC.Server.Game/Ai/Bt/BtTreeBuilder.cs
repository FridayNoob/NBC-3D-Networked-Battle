// 这个文件里有：
//   BtParams（类）：服务端用的参数来源实现（R022-3 会换成从 JSON 读；当前由代码填）。
//   BtTreeBuilder（静态类）：用代码搭 wolf / boss 两套默认树。
//
// 树的结构与策划在编辑器里搭的一致；R022-3 落地后这里改成"读 JSON 建树"，本文件会缩掉。

using System.Collections.Generic;

namespace NBC.Server.Game.Ai.Bt
{
    // 主要功能：把字符串键值喂给节点读参数（Configure 阶段用）。
    // 主要数据成员：m_values（键 → 文本）。
    // 主要外部方法：Set、TryGetInt、TryGetBool、TryGetFloat。
    // 谁在用它：BtTreeBuilder、R022-3 的 JSON 解析。
    // 注意：只在建树阶段用，Tick 阶段节点不再读它。
    public sealed class BtParams : IBtParamSource
    {
        // 键 → 文本值
        private readonly Dictionary<string, string> m_values = new Dictionary<string, string>();

        // 功能：填一个参数。
        // 入参：key 键；value 文本值
        // 返回：自身（便于链式书写）
        // 步骤：写字典
        // 上游：BtTreeBuilder
        // 下游：节点的 Configure
        public BtParams Set(string key, string value)
        {
            m_values[key] = value;
            return this;
        }

        // 功能：读整数。
        // 入参：key 键；value 值
        // 返回：true = 存在且可解析
        // 步骤：查字典 + int.TryParse
        // 上游：节点 Configure
        // 下游：无
        public bool TryGetInt(string key, out int value)
        {
            value = 0;
            return m_values.TryGetValue(key, out string raw) && int.TryParse(raw, out value);
        }

        // 功能：读布尔。
        // 入参：key 键；value 值
        // 返回：true = 存在且可解析
        // 步骤：查字典 + bool.TryParse
        // 上游：节点 Configure
        // 下游：无
        public bool TryGetBool(string key, out bool value)
        {
            value = false;
            return m_values.TryGetValue(key, out string raw) && bool.TryParse(raw, out value);
        }

        // 功能：读浮点（固定用 InvariantCulture，避免小数点随地区变化）。
        // 入参：key 键；value 值
        // 返回：true = 存在且可解析
        // 步骤：查字典 + float.TryParse(InvariantCulture)
        // 上游：节点 Configure
        // 下游：无
        public bool TryGetFloat(string key, out float value)
        {
            value = 0f;
            return m_values.TryGetValue(key, out string raw)
                   && float.TryParse(raw, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }

    // 主要功能：搭默认树 —— 普通怪（wolf）与 BOSS（boss）各一棵。
    // 主要数据成员：WolfAiKind / BossAiKind（种类名）。
    // 主要外部方法：Build(aiKind)。
    // 谁在用它：DungeonBattle 建怪时、MonsterBrain 构造。
    // 注意：树的形状要与客户端编辑器里搭的一致；R022-3 用 JSON 取代本文件。
    public static class BtTreeBuilder
    {
        // 普通怪的种类名
        public const string WolfAiKind = "wolf";

        // BOSS 的种类名
        public const string BossAiKind = "boss";

        // 功能：按种类搭树。
        // 入参：aiKind 种类名（未知当作 wolf）
        // 返回：根节点
        // 步骤：boss → BuildBoss；其它 → BuildWolf
        // 上游：MonsterBrain 构造
        // 下游：各节点构造函数
        public static BtNode Build(string aiKind)
        {
            return aiKind == BossAiKind ? BuildBoss() : BuildWolf();
        }

        // 功能：普通怪：死了停 → 能打就打（1 段）→ 有目标就追 → 巡逻。
        // 入参：无
        // 返回：根节点
        // 步骤：逐层 AddChild
        // 上游：Build
        // 下游：条件/动作节点
        private static BtNode BuildWolf()
        {
            BtSelector root = new BtSelector();
            root.AddChild(new IsDeadCondition());

            BtSequence attack = new BtSequence();
            attack.AddChild(new HasTargetCondition());
            attack.AddChild(Configured(new InAttackRangeCondition(), new BtParams()));
            attack.AddChild(Configured(new AttackReadyCondition(), new BtParams().Set("skillId", "0")));
            attack.AddChild(Configured(new BasicAttackAction(), new BtParams().Set("hits", "1").Set("intervalTicks", "5")));
            root.AddChild(attack);

            BtSequence chase = new BtSequence();
            chase.AddChild(new HasTargetCondition());
            chase.AddChild(new ChaseAction());
            root.AddChild(chase);

            root.AddChild(new PatrolAction());
            return root;
        }

        // 功能：BOSS：死了停 → 能打就打（3 段）→ 有目标就追 → 离家远就回位 → 站桩。
        // 入参：无
        // 返回：根节点
        // 步骤：逐层 AddChild
        // 上游：Build
        // 下游：条件/动作节点
        private static BtNode BuildBoss()
        {
            BtSelector root = new BtSelector();
            root.AddChild(new IsDeadCondition());

            BtSequence attack = new BtSequence();
            attack.AddChild(new HasTargetCondition());
            attack.AddChild(Configured(new InAttackRangeCondition(), new BtParams()));
            attack.AddChild(Configured(new AttackReadyCondition(), new BtParams().Set("skillId", "0")));
            attack.AddChild(Configured(new BasicAttackAction(), new BtParams().Set("hits", "3").Set("intervalTicks", "5")));
            root.AddChild(attack);

            BtSequence chase = new BtSequence();
            chase.AddChild(new HasTargetCondition());
            chase.AddChild(new ChaseAction());
            root.AddChild(chase);

            BtSequence home = new BtSequence();
            home.AddChild(new HomeFarCondition());
            home.AddChild(new ReturnHomeAction());
            root.AddChild(home);

            root.AddChild(new IdleAction());
            return root;
        }

        // 功能：给节点灌参数（小工具，避免每处写两行）。
        // 入参：node 节点；parameters 参数
        // 返回：同一个节点
        // 步骤：调 Configure
        // 上游：BuildWolf / BuildBoss
        // 下游：节点.Configure
        private static BtNode Configured(BtNode node, BtParams parameters)
        {
            node.Configure(parameters);
            return node;
        }
    }
}
