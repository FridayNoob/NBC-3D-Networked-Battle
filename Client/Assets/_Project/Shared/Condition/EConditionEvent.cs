// ============================================================================
//  EConditionEvent —— 「条件事件」的类型（配置表 QuestCondition.eventType 用）
//  项目：3D联网战斗Demo   对应：M2-A1（条件系统）、Docs\20 §四
//
//  ---------------------------------------------------------------------------
//  它是什么
//  ---------------------------------------------------------------------------
//  "条件系统"要回答一个问题：**"玩家做到了某件事"这个事实，怎么变成进度？**
//
//  做法是：游戏里发生的一切**先被归一成"条件事件"**，条件系统只认这一种东西。
//  例如：
//      战斗里打死了怪   -> KillMonster, targetId = 怪物编号
//      背包里进了一件物品 -> CollectItem, targetId = 物品编号
//      玩家走进某块区域  -> ReachArea,  targetId = 区域编号
//
//  于是"击杀 3 只野狼"就是一条**数据**（`KillMonster` + 6001 + 3），不是代码。
//  加一个新任务**不用改一行 C#** —— 这是整个任务系统能被策划自己维护的前提。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么它住在「共享层」（`Shared\`）而不是客户端
//  ---------------------------------------------------------------------------
//  任务进度在 M4 要**由服务端校验**（客户端说自己打完了不算数）。
//  `Shared\` 是**一份源码、双端都编**的地方（D1 的规矩），
//  所以条件系统的核心放这里，服务端拿到的是**同一份逻辑**，不会漂移。
//  这也是为什么本文件里**一个 `UnityEngine` 都没有**（服务端没有 Unity）。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 枚举是「手写」的，不由配置表工具生成
//  ---------------------------------------------------------------------------
//  规范（`Docs\17` §二）写死了：`enum:类型名` 的语义是"**表里写成员名，值由 C# 决定**"。
//  工具不认识业务语义（谁是 KillMonster 是游戏设计），所以枚举**只有这一处权威来源**。
//  ⚠️ 已知局限（`Docs\17` 如实记过）：表里写错成员名（`KillMonsterr`）工具查不出来，
//     要到运行时 `Enum.Parse` 才炸。想提前查，把成员填进 `ConfigPolicy.KnownEnumMembers`。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 新增成员只能**往后加**，不许改已有成员的值
//  ---------------------------------------------------------------------------
//  枚举值会被写进**配置表**和（M4 起的）**进度表**。
//  改动已有成员的值 = 让历史上所有存档的含义悄悄变了 —— 典型的静默数据损坏。
// ============================================================================

// ============================================================================
//  ⚠️ `#nullable disable` —— 让**双端看到同一套规则**（D1 的"不许漂移"）
// ============================================================================
//  同一份源码在两个 nullable 设置**不同**的工程里编译：
//      · Unity 侧（`NBC.Shared.asmdef`）      ：没开可空引用类型
//      · 服务端（`NBC.Shared.csproj` → `Server\Directory.Build.props`）：`<Nullable>enable</Nullable>`
//
//  不处理的话会出现**两头都不干净**：
//      · 服务端多出一批 CS86xx（事件没初始化、局部变量赋 null……）
//      · 而如果为了消警告去写 `string?` / `List<T>?`，**Unity 侧**又会因为
//        "可空注解出现在未开启可空上下文的文件里"报 **CS8632**
//
//  所以这里显式关掉：**两边都按"没有可空注解"这套规则编译**。
//  这不是"为了消警告而关检查"，而是**把环境差异钉死在一处** ——
//  否则"服务端编得过、Unity 编不过"（或反过来）正是 D1 要防的那种漂移。
//
//  ⚠️ 代价（如实记）：本目录里**放弃**了可空引用类型的静态检查，
//     "可能为 null"要靠 XML 注释和运行期校验（本目录两个都做了）。
//     若将来两端统一开启可空，把这几行删掉即可 —— 它们集中且显眼。
// ============================================================================

#nullable disable

namespace NBC.Shared.Condition
{
    /// <summary>条件事件的类型。条件的"主语"就是它。</summary>
    public enum EConditionEvent
    {
        /// <summary>击杀了一个怪（<c>targetId</c> = 怪物编号，0 = 任意怪）。</summary>
        KillMonster = 0,

        /// <summary>获得了物品（<c>targetId</c> = 物品编号，0 = 任意物品）。</summary>
        CollectItem = 1,

        /// <summary>到达了区域（<c>targetId</c> = 区域编号，0 = 任意区域）。</summary>
        ReachArea = 2,

        /// <summary>用技能命中（<c>targetId</c> = 技能编号，0 = 任意技能）。</summary>
        UseSkill = 3,

        /// <summary>和 NPC 对话（<c>targetId</c> = NPC 编号，0 = 任意 NPC）。</summary>
        TalkToNpc = 4
    }

    /// <summary>`EConditionEvent` 的辅助方法。</summary>
    public static class ConditionEvents
    {
        /// <summary>本枚举的**合法成员个数**（用于校验"表里填了个不存在的值"）。</summary>
        public const int Count = 5;

        /// <summary>
        /// 这个值是不是一个**真实存在**的成员。
        /// <para>
        /// ⚠️ 为什么要专门检查：`(EConditionEvent)99` 在 C# 里**完全合法**（不报错、不抛异常），
        /// 它会安静地待在变量里，然后**匹配不到任何条件** —— 于是任务永远做不完。
        /// 这是典型的"看起来正常的假值"，必须在入口拦掉。
        /// </para>
        /// </summary>
        /// <param name="value">待检查的值。</param>
        /// <returns>是合法成员返回 true。</returns>
        public static bool IsDefined(EConditionEvent value)
        {
            int raw = (int)value;
            return raw >= 0 && raw < Count;
        }

        /// <summary>
        /// 稳定的**字符串键**（日志、事件注册、外部协议里用它）。
        /// <para>约定形状：`条件.<成员名>`，例如 `条件.KillMonster`。</para>
        /// <para>
        /// ⚠️ 拿它当**事件中心**的 key 时要注意：客户端用的是强类型 `EventId`
        /// （A3 的 FW-06 修法），这里的字符串只是"两边都认的同一句话"，
        /// 由客户端侧的桥把它翻译成 `EventId` —— 不要在业务代码里裸传这个字符串。
        /// </para>
        /// </summary>
        /// <param name="value">事件类型。</param>
        /// <returns>字符串键。</returns>
        public static string KeyOf(EConditionEvent value)
        {
            switch (value)
            {
                case EConditionEvent.KillMonster: return "条件.KillMonster";
                case EConditionEvent.CollectItem: return "条件.CollectItem";
                case EConditionEvent.ReachArea: return "条件.ReachArea";
                case EConditionEvent.UseSkill: return "条件.UseSkill";
                case EConditionEvent.TalkToNpc: return "条件.TalkToNpc";
                default: return "条件.未知(" + (int)value + ")";
            }
        }

        /// <summary>转成一句人话（报错消息里用）。</summary>
        /// <param name="value">事件类型。</param>
        /// <returns>中文描述。</returns>
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
