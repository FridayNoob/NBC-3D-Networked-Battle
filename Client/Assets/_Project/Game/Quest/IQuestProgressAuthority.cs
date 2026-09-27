// ============================================================================
//  IQuestProgressAuthority —— 「这条条件的权威值是什么」的接缝（M4-S3 收口）
//  项目：3D联网战斗Demo   对应：`Docs\27` §21.4 未做#2 / §二十四
//
//  ---------------------------------------------------------------------------
//  一、它解决什么（一句话：**两个账房**）
//  ---------------------------------------------------------------------------
//  在此之前任务/成就进度**客户端自己也算一份**（= 预测）。两边数值一致，但那只是"碰巧" ——
//  只要配置、时序、或"哪个事件喂了几次"有半点不同，就会出现
//  「客户端显示已完成、服务端说没达成」，而且**不报错**。
//  有了这道缝，界面就能说清"这个数字**是谁说的**"。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 这道缝的全部要害：**"服务端没说" ≠ "进度是 0"**
//  ---------------------------------------------------------------------------
//  `TryGetCondition` 返回 **false** 表示**服务端根本没提过这条条件**（还没同步 / 没接权威 /
//  这条条件不在服务端管的那批里）—— **不是**"它的进度是 0"。
//
//      ❌ 把 false 当成 0：界面会画出「0/3」，而玩家其实已经打了 2 只
//         ⇒ 看起来像"进度被清零了"，玩家会以为服务端把数据弄丢了。
//      ✅ 正确做法：退回**本地那份**，并且**标出来它是本地预测**
//         （见 `QuestProgressOverlay`）。
//
//  这条是本片最容易回归的地方，所以它有一条专门的 EditMode 用例
//  （`QuestPanelModelTests`：`Authority_MissingCondition_IsNotRenderedAsAuthoritativeZero`）。
//
//  ---------------------------------------------------------------------------
//  三、为什么它住在 `Game\Quest\` 而不是 `Game\Net\`
//  ---------------------------------------------------------------------------
//  `Game\Net\` 是**引擎无关子集**（`Server\_net-probe` 直接 `<Compile Include>` 它）。
//  这道缝是纯逻辑、两边都能编，但**它的实现要认识 `NetSession`** ——
//  放进 `Game\Net\` 会让"网络层"反过来依赖"任务层"（方向错）。
//  所以：**缝 + 实现都放任务层**，网络层一行不改。
// ============================================================================

namespace NBC.Game.Quest
{
    /// <summary>一个进度数字**是谁说的**（界面必须能一眼分辨）。</summary>
    public enum EProgressSource
    {
        /// <summary>
        /// **本地预测**：客户端自己算出来的那份。
        /// <para>⚠️ 界面显示它时**必须标注** —— 否则玩家会把预测当成事实。</para>
        /// </summary>
        LocalPrediction = 0,

        /// <summary>**服务端权威**：服务端发下来的那份（唯一可以当"事实"的）。</summary>
        ServerAuthoritative = 1
    }

    /// <summary>一条条件的**权威值**（刻意不带 protobuf 类型：接缝不认识协议）。</summary>
    public readonly struct AuthoritativeProgress
    {
        /// <summary>已累计（服务端说的，已钳位）。</summary>
        public readonly int Current;

        /// <summary>需求值。</summary>
        public readonly int Required;

        /// <summary>达成了没有。</summary>
        public readonly bool Met;

        /// <summary>造一条。</summary>
        /// <param name="current">已累计。</param>
        /// <param name="required">需求值。</param>
        /// <param name="met">达成了没有。</param>
        public AuthoritativeProgress(int current, int required, bool met)
        {
            Current = current;
            Required = required;
            Met = met;
        }
    }

    /// <summary>「这条条件的权威值」的来源。</summary>
    public interface IQuestProgressAuthority
    {
        /// <summary>
        /// 取一条条件的**权威值**。
        /// <para>⚠️ 返回 **false = 服务端没说**，**不是**"进度是 0"（见文件头第二节）。
        /// 实现**不许**在拿不到数据时返回一个 0/Required 的假值。</para>
        /// </summary>
        /// <param name="conditionId">条件编号（`QuestCondition` 表主键）。</param>
        /// <param name="progress">权威值（返回 false 时内容无意义）。</param>
        /// <returns>服务端**确实说过**这条条件时返回 true。</returns>
        bool TryGetCondition(int conditionId, out AuthoritativeProgress progress);
    }
}
