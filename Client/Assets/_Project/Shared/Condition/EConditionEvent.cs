// 这个文件里有：
//   EConditionEvent（枚举）：条件事件的类型，也就是条件的主语；配置表 QuestCondition.eventType 填它的成员名，不填数字
//   ConditionEvents（静态类）：条件事件的名字解析、合法性校验、稳定字符串键与报错文案

// 注意：顶部关掉 nullable 是让双端看到同一套规则 —— Unity 侧（NBC.Shared.asmdef）没开可空引用类型，
//     服务端（NBC.Shared.csproj → Server\Directory.Build.props）开了；不关的话服务端会多出一批 CS86xx，
//     而为了消警告去写可空注解，Unity 侧又会报 CS8632，两头都不干净。
// 注意：代价是本目录放弃可空静态检查，「可能为 null」靠运行期校验兜住；将来两端统一开启可空，删掉这条指令即可。
// 注意：本文件不引用 UnityEngine（服务端没有 Unity），也不得引用 NBC.Protocol —— 它被客户端与服务端同一份源码编译。
// 注意：targetId 是配置编号（正整数，0 表示任意），不是坐标；本文件只处理事件类型与编号，没有长度或时间量，不涉及毫米 / 米或 tick / 秒。

#nullable disable

namespace NBC.Shared.Condition
{
    // 主要功能：把「玩家做到了某件事」归一成一个枚举值，条件系统只认这一种输入，任务与成就因此能靠配置表表达而不改代码。
    // 主要数据成员：KillMonster / CollectItem / ReachArea / UseSkill / TalkToNpc 五个成员，值固定为 0..4，顺序不能动。
    // 主要外部方法：无（名字解析、校验与显示都在 ConditionEvents 里）。
    // 谁在用它：客户端 ConditionEventBridge 绑定事件、ConditionTracker 与任务 / 成就运行时计数；服务端 QuestTables 解析配置、ProgressFact 与 DungeonBattle 产生条件事实；Editor 调试窗口与联调探针也直接引用；两端编同一份源码。
    // 注意：新增成员只能往后加，不许改已有成员的值 —— 值会写进配置表和服务端进度表，改值等于悄悄改变历史存档的含义。
    // 注意：枚举是唯一权威来源，表里写成员名、值由 C# 决定；表里写错名字工具查不出，要到运行期解析才报错，想提前查可把成员登记进 ConfigPolicy.KnownEnumMembers。
    /// <summary>条件事件的类型，条件的主语就是它；配置表 QuestCondition.eventType 填成员名。</summary>
    public enum EConditionEvent
    {
        /// <summary>击杀了一个怪；targetId 填怪物编号（0 表示任意怪）；成员值固定为 0。</summary>
        KillMonster = 0,

        /// <summary>获得了物品；targetId 填物品编号（0 表示任意物品）；成员值固定为 1。</summary>
        CollectItem = 1,

        /// <summary>到达了区域；targetId 填区域编号（0 表示任意区域）；成员值固定为 2。</summary>
        ReachArea = 2,

        /// <summary>用技能命中；targetId 填技能编号（0 表示任意技能）；成员值固定为 3。</summary>
        UseSkill = 3,

        /// <summary>和 NPC 对话；targetId 填 NPC 编号（0 表示任意 NPC）；成员值固定为 4。</summary>
        TalkToNpc = 4
    }

    // 主要功能：给 EConditionEvent 提供名字与值的互相翻译、合法性校验、稳定字符串键和报错文案。
    // 主要数据成员：Count（合法成员个数，值 5，必须与枚举成员数一致）。
    // 主要外部方法：IsDefined / TryParse / NameOf / DescribeAllNames / Describe / KeyOf。
    // 谁在用它：客户端 ConditionEventBridge 绑定事件时校验、ConditionTracker 入口拦假值；服务端 QuestTables 读表解析、ProgressFact 打日志；Editor 调试窗口间接触发。
    // 注意：名字映射手写在 NameOf 里，新增枚举成员必须同时加 Count 与 NameOf，否则新成员会被当成未知值。
    /// <summary>EConditionEvent 的名字解析、合法性校验与显示辅助。</summary>
    public static class ConditionEvents
    {
        // 合法成员个数，无单位；值 5，必须等于枚举成员数，新增成员时 +1（IsDefined 的范围判断与 DescribeAllNames 的遍历都靠它）
        public const int Count = 5;

        /// <summary>判断一个值是不是枚举里真实存在的成员，把强转出来的假值挡在系统入口。</summary>
        /// <param name="value">待检查的事件类型；可能由任意 int 强转而来，不保证合法。</param>
        /// <returns>落在 0..Count-1 内返回 true，否则 false。</returns>
        // 功能：判断一个值是不是枚举里真实存在的成员，把强转出来的假值挡在系统入口。
        // 入参：value 待检查的事件类型；可能是任意 int 强转来的值，不保证合法
        // 返回：true 表示能匹配到真实成员；false 表示是假值，调用方必须拒绝
        // 步骤：
        //   1) 先转成 int 再做范围判断，不逐个成员比较
        //   2) 成员值从 0 连续排列，落在 0..Count-1 内就等价于成员存在
        // 上游：ConditionDef.Validate、ConditionTracker.Notify（同程序集），客户端 ConditionEventBridge.Bind
        // 下游：无
        // 注意：(EConditionEvent)99 在 C# 里完全合法，不报错也不抛异常；放过去会匹配不到任何条件，表现为任务永远做不完。
        public static bool IsDefined(EConditionEvent value)
        {
            int raw = (int)value;
            return raw >= 0 && raw < Count;
        }

        /// <summary>把配置表里写的名字解析成枚举值，大小写敏感、按序数逐字符比较。</summary>
        /// <param name="name">表里填的名字，不带「条件.」前缀；null 或空串一律解析失败。</param>
        /// <param name="value">解析结果；失败时是 default，即 0（KillMonster）。</param>
        /// <returns>名字是已知成员返回 true，否则 false。</returns>
        // 功能：把配置表里的成员名解析成枚举值，名字必须逐字符完全一致。
        // 入参：name 表里填的名字（不带「条件.」前缀，null 或空串按不认识处理）；value 解析结果，失败时写 default
        // 返回：true 表示名字是已知成员；false 表示不认识，value 已置为 0
        // 步骤：
        //   1) 从 0 到 Count-1 遍历成员，不另维护名字表，避免两处对不上
        //   2) 用 Ordinal 比较名字，大小写不同即失败，不让 killmonster 这类拼错静默生效
        //   3) 全不命中才写 default，保证失败时 value 不是上一次的残留
        // 上游：服务端 QuestTables 读 CSV；客户端读生成的 TSV 走生成代码里的 Enum.Parse，不经过这里
        // 下游：NameOf
        // 注意：本方法大小写敏感，客户端生成的表加载器却忽略大小写；拼错的名字可能客户端能过、服务端报错，改表时别依赖客户端的宽松。
        // 注意：解析放共享层是为了两端认同一套名字；各写一份实现，迟早一边多认一个名字，出现客户端认为条件成立、服务端认为配置有错的漂移。
        public static bool TryParse(string name, out EConditionEvent value)
        {
            for (int i = 0; i < Count; i++)
            {
                var candidate = (EConditionEvent)i;

                if (string.Equals(NameOf(candidate), name, System.StringComparison.Ordinal))
                {
                    value = candidate;
                    return true;
                }
            }

            value = default(EConditionEvent);
            return false;
        }

        /// <summary>把枚举值翻成配置表里使用的成员名；不认识的值返回带数字的未知文案，不抛异常。</summary>
        /// <param name="value">事件类型，允许是任意 int 强转来的值。</param>
        /// <returns>成员名；未知值时形如「未知(99)」，数字便于报错定位。</returns>
        // 功能：把枚举值翻成配置表里使用的成员名，未知值也不抛异常。
        // 入参：value 事件类型；允许是任意 int 强转来的值
        // 返回：成员名；不认识的值返回「未知(n)」，n 是它的整数值
        // 步骤：
        //   1) 逐个成员 switch 直接返回字面名字，与表里的填法完全一致
        //   2) 兜底返回带数值的未知文案，让上层自己决定怎么报错
        // 上游：TryParse、DescribeAllNames、KeyOf（同程序集），服务端 ProgressFact 的文本，ConditionDef 的显示
        // 下游：无
        // 注意：这张映射是手写的唯一权威，新增枚举成员必须在这里补一行，否则该成员会被当成未知值。
        public static string NameOf(EConditionEvent value)
        {
            switch (value)
            {
                case EConditionEvent.KillMonster: return "KillMonster";
                case EConditionEvent.CollectItem: return "CollectItem";
                case EConditionEvent.ReachArea: return "ReachArea";
                case EConditionEvent.UseSkill: return "UseSkill";
                case EConditionEvent.TalkToNpc: return "TalkToNpc";
                default: return "未知(" + (int)value + ")";
            }
        }

        /// <summary>把所有合法名字拼成一句人话，配置报错时告诉填表人能填什么。</summary>
        /// <returns>形如「KillMonster / CollectItem / ...」的字符串，顺序与枚举值一致。</returns>
        // 功能：把所有合法名字拼成一句人话，报错时直接告诉填表人能填什么。
        // 入参：无
        // 返回：名字按枚举值顺序用「 / 」分隔；至少有一个名字，不会为空
        // 步骤：
        //   1) 从 0 遍历到 Count-1，顺序稳定，不依赖字典或反射
        //   2) 除第一个外先补分隔符，避免结尾多一个「 / 」
        //   3) 名字统一走 NameOf，和解析共用同一套拼法
        // 上游：服务端 QuestTables 的配置报错
        // 下游：NameOf
        public static string DescribeAllNames()
        {
            string text = string.Empty;

            for (int i = 0; i < Count; i++)
            {
                if (i > 0)
                {
                    text += " / ";
                }

                text += NameOf((EConditionEvent)i);
            }

            return text;
        }

        // 功能：拼出稳定的字符串键「条件.成员名」，供日志、事件注册与外部协议使用；客户端事件中心用的是强类型 EventId，这个字符串只作两端共识，须经桥翻译，不要在业务代码里裸传。
        public static string KeyOf(EConditionEvent value)
        {
            return "条件." + NameOf(value);
        }

        // 功能：把事件类型转成一句中文，供条件描述与报错文案使用。
        // 入参：value 事件类型；允许是任意 int 强转来的值
        // 返回：中文名；不认识的值返回「未知事件（n）」，n 是它的整数值
        // 步骤：
        //   1) 逐个成员 switch 直接返回中文名，保证界面与日志用词统一
        //   2) 兜底返回带数值的未知文案，不抛异常
        // 上游：ConditionDef.Describe（同程序集），目前没有跨程序集直接调用
        // 下游：无
        // 注意：中文名与 NameOf 的成员名是两套文案，新增成员时两边都要补。
        public static string Describe(EConditionEvent value)
        {
            switch (value)
            {
                case EConditionEvent.KillMonster: return "击杀怪物";
                case EConditionEvent.CollectItem: return "获得物品";
                case EConditionEvent.ReachArea: return "到达区域";
                case EConditionEvent.UseSkill: return "用技能命中";
                case EConditionEvent.TalkToNpc: return "与 NPC 对话";
                default: return "未知事件（" + (int)value + "）";
            }
        }
    }
}
