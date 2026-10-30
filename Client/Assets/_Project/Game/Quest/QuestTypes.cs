// 这个文件里有：
//   EQuestState（枚举）：一个任务在玩家身上的状态，没接、已接、可交付、已交付四种
//   QuestActionResult（结构体）：一次任务操作的结果 —— 成功，或失败加一句给玩家看的原因
//   QuestReward（结构体）：一次奖励的内容，与配置表生成类型解耦，发奖方不必认识配置行
//   QuestConditionLine（类）：一条条件的展示行，任务与成就共用同一个形状
//   QuestOffer（结构体）：一个可接任务（配置里有、玩家还没接）的展示信息
//   QuestTracking（类）：一个任务的追踪视图，给任务追踪条与调试窗口显示用

using System.Collections.Generic;

namespace NBC.Game.Quest
{
    // 主要功能：给一个任务在玩家身上的进度定四种状态，状态迁移由 QuestRuntime 统一管。
    // 主要数据成员：None（0，没接或已放弃）、Accepted（1，已接）、Completed（2，条件全达成等交付）、Submitted（3，已交付）。
    // 主要外部方法：无（纯枚举，没有成员方法）。
    // 谁在用它：QuestRuntime 记每个任务的状态并据此拒绝非法操作，任务面板按状态分桶与决定交付按钮，Boot 的演示界面直接打印状态名。
    // 注意：Completed 只是「能交了」，奖励要到 Submitted 才发 —— 两者不能合并，否则界面分不清「可交」与「已交」。
    public enum EQuestState
    {
        // 没接过，或接过又放弃了；放弃成功后回到这里
        None = 0,

        // 已接取，条件还没全部达成
        Accepted = 1,

        // 条件全部达成、等着玩家交付；此时奖励还没发
        Completed = 2,

        // 已交付，奖励已发；之后再交付或放弃都会被拒
        Submitted = 3
    }

    /// <summary>一次任务操作的结果：成功，或失败并带一句给玩家看的原因。</summary>
    // 主要功能：把玩家操作的结果压成「成功 / 失败加一句人话」，取代拿异常做业务分支。
    // 主要数据成员：m_ok（成功与否）、m_reason（失败原因，成功时为 null）。
    // 主要外部方法：Ok / Reason（只读属性）、Success / Fail（工厂）、ToString（转人话）。
    // 谁在用它：Boot 的 M2DemoBehaviour 读 Ok / Reason 决定提示哪一句；QuestRuntime 与 QuestPanelModel 负责造。
    // 注意：只有程序错误（枚举值不合法、重复登记同一个条件）才抛异常；玩家点一次按钮就能触发的拒绝（重复接取、条件没达成就要交付）一律走返回值加原因，既不能把游戏炸掉，也不能让玩家点了没反应。
    public readonly struct QuestActionResult
    {
        // 成功了吗；true 表示操作生效，false 表示被业务规则拒绝（不是程序错误）
        private readonly bool m_ok;

        // 失败原因，一句给玩家看的中文；成功时为 null
        private readonly string m_reason;

        // 功能：把成功标志与失败原因装进结果。
        // 入参：ok 成功与否；reason 失败原因（一句给玩家看的中文，成功时传 null）
        // 返回：无（构造出的实例按值复制）
        // 步骤：
        //   1) 两个字段原样落位，空原因的兜底放在 Fail —— 只留一处，免得两个入口各写一套文案
        // 上游：Success / Fail（本类型的两个工厂，外部不直接调这个构造）
        // 下游：无
        private QuestActionResult(bool ok, string reason)
        {
            m_ok = ok;
            m_reason = reason;
        }

        /// <summary>成功了吗；false 表示被业务规则拒绝（不是程序错误）。</summary>
        public bool Ok
        {
            get { return m_ok; }
        }

        /// <summary>失败原因，一句给玩家看的中文；成功时为 null。</summary>
        public string Reason
        {
            get { return m_reason; }
        }

        // 功能：造一个成功结果，不带原因。
        public static QuestActionResult Success()
        {
            return new QuestActionResult(true, null);
        }

        // 功能：造一个失败结果，并保证它带着一句能显示给玩家的话。
        // 入参：reason 失败原因；null 或空串会被换成本项目的内部错误文案
        // 返回：Ok 为 false、Reason 不为空的结果
        // 步骤：
        //   1) 先判原因是否为空：空原因等于「玩家点了没反应」，正是本项目一直在防的形态
        //   2) 空则换成「（内部错误：拒绝原因没写）」，宁可把内部错误摆出来，也不让按钮点了像没反应
        // 上游：QuestRuntime.Reject、QuestPanelModel.RequestRemote（业务拒绝的统一出口）
        // 下游：QuestActionResult 构造函数
        // 注意：这里只兜空串，兜不了「文案写得看不懂」—— 文案质量仍在调用方。
        public static QuestActionResult Fail(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                reason = "（内部错误：拒绝原因没写）";
            }

            return new QuestActionResult(false, reason);
        }

        // 功能：转成一句人话，成功是「成功」，失败是「失败：原因」。
        // 入参：无
        // 返回：可直接拼进日志或界面的字符串；失败时必带原因
        // 步骤：
        //   1) 按 m_ok 二选一，成功不拼原因，免得出现「成功：」后面跟一个 null
        // 上游：暂无显式调用点，拼字符串或调试打印时隐式调用
        // 下游：无
        public override string ToString()
        {
            return m_ok ? "成功" : "失败：" + m_reason;
        }
    }

    /// <summary>一次奖励的内容：奖励编号、经验、金币与可选物品。</summary>
    // 主要功能：把一次奖励装成中性结构体，发奖方不必认识配置表生成类型。
    // 主要数据成员：RewardId（奖励表主键）、Exp（经验）、Gold（金币）、ItemId / ItemCount（物品，0 表示没有物品）。
    // 主要外部方法：构造函数、ToString（转人话）。
    // 谁在用它：QuestRuntime 从奖励表查到行后造，经 IQuestRewardSink 发给各接收方；Editor 的调试窗口读经验、金币、物品三项显示成一行日志。
    // 注意：字段刻意用中性 int，不收配置表生成类型 —— 奖励表改字段时只影响 QuestRuntime 一处，发奖接缝与显示端不用跟着改。
    public readonly struct QuestReward
    {
        // 奖励表主键，用于回查这一笔发的是什么
        public readonly int RewardId;

        /// <summary>本笔给的经验，单位：点（配置表给多少就是多少，可为 0）。</summary>
        public readonly int Exp;

        /// <summary>本笔给的金币，单位：枚（可为 0）。</summary>
        public readonly int Gold;

        /// <summary>物品编号，0 表示这一笔没有物品。</summary>
        public readonly int ItemId;

        /// <summary>物品件数，单位：件；ItemId 为 0 时无意义。</summary>
        public readonly int ItemCount;

        // 功能：把奖励表的五个字段装成一份中性奖励。
        // 入参：rewardId 奖励表主键；exp 经验（单位：点）；gold 金币（单位：枚）；itemId 物品编号（0 = 没有物品）；itemCount 物品件数
        // 返回：无（构造出的实例按值复制，交给发奖接缝）
        // 步骤：
        //   1) 五个字段原样落位，不钳位也不换算 —— 值来自奖励表，二次修正会掩盖配置本身的问题
        // 上游：QuestRuntime.Submit（查到奖励行之后）
        // 下游：无
        public QuestReward(int rewardId, int exp, int gold, int itemId, int itemCount)
        {
            RewardId = rewardId;
            Exp = exp;
            Gold = gold;
            ItemId = itemId;
            ItemCount = itemCount;
        }

        // 功能：把奖励转成一句人话，日志与调试窗口直接显示。
        // 入参：无
        // 返回：形如「经验 +100、金币 +50、物品 7001 ×3」；没有物品时不带物品段
        // 步骤：
        //   1) 经验与金币总写上，这两项是玩家最先看的
        //   2) 只有 ItemId 非 0 才追加物品段，免得出现「物品 0 ×0」这种噪声
        // 上游：暂无显式调用点，拼字符串或调试打印时隐式调用
        // 下游：无
        public override string ToString()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("经验 +").Append(Exp).Append("、金币 +").Append(Gold);

            if (ItemId != 0)
            {
                builder.Append("、物品 ").Append(ItemId).Append(" ×").Append(ItemCount);
            }

            return builder.ToString();
        }
    }

    /// <summary>一条条件的展示行：描述、已累计、需求、是否达成，以及这个数字的来源。</summary>
    // 主要功能：给界面提供一条条件的形状 —— 描述、已累计、需求、是否达成、数字来源。
    // 主要数据成员：Description（给玩家看的描述）、Current / Required（单位：次）、IsMet（是否达成）、Source（这个数字是谁说的）。
    // 主要外部方法：无（纯数据行，字段全公开，造好之后由运行时或权威覆盖填）。
    // 谁在用它：QuestRuntime.BuildTracking 与 AchievementRuntime.BuildTracking 造，任务面板与成就界面读，Boot 与 Editor 的调试界面也直接读描述与进度。
    // 注意：Source 默认必须是本地预测（保守），没被权威盖过就一律算预测 —— 反过来默认成权威，就会出现「忘了盖也显示得像事实」；界面显示时必须经 QuestProgressOverlay.Mark 标出来源。
    public sealed class QuestConditionLine
    {
        // 条件编号，条件表主键；权威进度按它回查
        public int ConditionId;

        /// <summary>给玩家看的一句描述（例如「击杀怪物 ×3（目标 6001）」）；配置有错时这里是错误提示，仍会占一行。</summary>
        public string Description;

        /// <summary>已累计的数量，单位：次；是否达成请看 IsMet，别用 Current 与 Required 重算。</summary>
        public int Current;

        /// <summary>这条条件需要多少，单位：次（与 Current 同单位，来自配置表）。</summary>
        public int Required;

        /// <summary>达成了没有；配置出错或条件还没登记时是 false。</summary>
        public bool IsMet;

        // 这一行数字的来源；默认是本地预测，被权威覆盖时由 QuestProgressOverlay.Apply 改写成服务端权威
        public EProgressSource Source = EProgressSource.LocalPrediction;
    }

    // 主要功能：把一个可接任务装成展示用的只读快照，UI 不必回头查配置表。
    // 主要数据成员：QuestId（任务表主键）、Name（任务名）、Description（任务描述）。
    // 主要外部方法：构造函数（internal，只有任务运行时造）、ToString（转「[编号] 名称」）。
    // 谁在用它：QuestRuntime.CopyOffers 填进缓冲，QuestPanelModel 拿来造可接列表行；Game 内部使用，不跨程序集。
    public readonly struct QuestOffer
    {
        // 任务编号，任务表主键
        public readonly int QuestId;

        // 任务名，来自配置表
        public readonly string Name;

        // 任务描述，来自配置表；可接列表的详情行直接用
        public readonly string Description;

        // 功能：把可接任务的三个展示字段装成只读快照。
        // 入参：questId 任务编号（任务表主键）；name 任务名；description 任务描述
        // 返回：无（构造出的实例按值复制）
        // 步骤：
        //   1) 三个字段原样落位，这里不做裁剪也不查表 —— 配置行已经由调用方查好了
        // 上游：QuestRuntime.CopyOffers（唯一造它的地方）
        // 下游：无
        internal QuestOffer(int questId, string name, string description)
        {
            QuestId = questId;
            Name = name;
            Description = description;
        }

        // 功能：转成「[任务编号] 任务名」，日志与可接列表调试用。
        public override string ToString()
        {
            return "[" + QuestId + "] " + Name;
        }
    }

    /// <summary>一个任务的追踪视图：名字、状态与每条条件的进度，给 UI 直接显示。</summary>
    // 主要功能：把一个任务此刻的样子（名字、状态、每条条件的累计/需求）装成一份给 UI 的视图。
    // 主要数据成员：QuestId / Name / Description / State、Conditions（按配置表顺序）、AllMet（是否全部达成）。
    // 主要外部方法：AllMet（只读属性，判断能否交付）、ToString（多行文本，调试用）。
    // 谁在用它：QuestRuntime.TryGetTracking / CopyActiveTrackings 造，任务面板与 Boot 的演示界面读。
    // 注意：UI 只要「任务名 + 每条条件的 2/3」，别让它去问 ConditionTracker 一堆编号 —— 把给 UI 看的形状与内部状态分开，换一套 UI 不用动逻辑。
    // 注意：每取一次视图都会新建一份对象，别在每帧里取，也别把旧视图当实时数据。
    public sealed class QuestTracking
    {
        /// <summary>任务编号（任务表主键）。</summary>
        public int QuestId;

        /// <summary>任务名，来自配置表。</summary>
        public string Name;

        // 任务描述，来自配置表；视图里带着，但目前没有显示点读取（详情只拼条件行）
        public string Description;

        /// <summary>当前状态，取值 None / Accepted / Completed / Submitted。</summary>
        public EQuestState State;

        /// <summary>各条条件的进度，顺序与配置表一致；字段只读但列表内容可变，造视图时逐条 Add。</summary>
        public readonly List<QuestConditionLine> Conditions = new List<QuestConditionLine>();

        // 功能：判断这个任务是否所有条件都已达成。
        // 入参：无
        // 返回：每条条件都 IsMet 且至少有一条条件时 true；条件列表为空也算未达成
        // 步骤：
        //   1) 顺序扫，遇到第一条未达成立即返回 false —— 调用方只看这一个结果，后面不必再看
        //   2) 全扫完才看数量：空条件表不给 true，否则无条件任务一建出来就可交付
        // 上游：目前只有 EditMode 用例断言它；任务面板判交付看 State 是否为 Completed，不读它
        // 下游：QuestConditionLine.IsMet
        public bool AllMet
        {
            get
            {
                for (int i = 0; i < Conditions.Count; i++)
                {
                    if (!Conditions[i].IsMet)
                    {
                        return false;
                    }
                }

                return Conditions.Count > 0;
            }
        }

        // 功能：把追踪视图转成多行文本，供控制台与调试窗口直接打印。
        // 入参：无
        // 返回：首行「[编号] 名称（状态）」，之后每条条件一行「描述 已累计/需求」，已达成的补一个勾
        // 步骤：
        //   1) 先拼首行，状态直接用枚举名，调试时一眼看出停在哪个阶段
        //   2) 再逐条拼条件，只有达成才补勾，没达成留空以免误读
        //   3) 每行都以换行结尾，调用方整段打印即可
        // 上游：QuestRuntime.DescribeActive（控制台总览）、调试窗口与 EditMode 用例的失败信息
        // 下游：QuestConditionLine 的四个字段
        public override string ToString()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append('[').Append(QuestId).Append("] ").Append(Name)
                   .Append("（").Append(State).Append("）\n");

            for (int i = 0; i < Conditions.Count; i++)
            {
                builder.Append("    · ").Append(Conditions[i].Description)
                       .Append("  ").Append(Conditions[i].Current).Append('/')
                       .Append(Conditions[i].Required)
                       .Append(Conditions[i].IsMet ? "  ✅" : string.Empty).Append('\n');
            }

            return builder.ToString();
        }
    }
}
