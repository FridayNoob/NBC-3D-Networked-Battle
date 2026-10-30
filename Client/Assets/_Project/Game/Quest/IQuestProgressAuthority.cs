// 这个文件里有：
//   EProgressSource（枚举）：一个进度数字的来源标签，标明它是本地预测还是服务端权威
//   AuthoritativeProgress（结构体）：一条条件权威值的快照，只有已累计、需求值、是否达成三个字段
//   IQuestProgressAuthority（接口）：查「这条条件的权威值」的接缝，用 false 表示服务端没说

namespace NBC.Game.Quest
{
    // 主要功能：给每个进度数字打上来源标签，让界面能一眼说清这个数是本地预测还是服务端权威。
    // 主要数据成员：LocalPrediction（0，本地预测）、ServerAuthoritative（1，服务端权威）。
    // 主要外部方法：无（纯枚举，没有成员方法）。
    // 谁在用它：QuestProgressOverlay 逐行写它，任务面板与成就界面读它决定要不要标「本地预测」。
    public enum EProgressSource
    {
        // 本地预测：客户端自己算出来的那份，界面显示时必须标注，否则玩家会把预测当成事实
        LocalPrediction = 0,

        // 服务端权威：服务端发下来的那份，唯一可以当事实的；界面不标注
        ServerAuthoritative = 1
    }

    // 主要功能：把一条条件的权威值装成一个只看数值的快照，不携带任何协议类型。
    // 主要数据成员：Current（已累计次数）、Required（需求次数）、Met（服务端判定的达成）。
    // 主要外部方法：构造函数（唯一外部方法，一次造齐三个只读字段）。
    // 谁在用它：IQuestProgressAuthority 的实现造它，QuestProgressOverlay.Apply 读它盖到条件行上。
    // 注意：刻意不带 protobuf 类型 —— 这道缝不认识协议，协议换了这里不用改。
    public readonly struct AuthoritativeProgress
    {
        // 已累计多少，单位：次（按条件类型计次数，无物理单位），范围 0..Required，服务端已钳位
        public readonly int Current;

        // 达成需要多少次，单位：次，范围 >= 0，与 Current 同单位
        public readonly int Required;

        // 服务端判定的成没成，true 已达成 / false 未达成；以它为准，别在本地用 Current 与 Required 重算
        public readonly bool Met;

        // 功能：把一份服务端给的权威值装进结构体，三个字段原样落位。
        // 入参：current 已累计（调用方保证已钳位到 0..required）；required 需求值（>= 0）；met 服务端判定的达成结果
        // 步骤：
        //   1) 三个字段直接赋值，这里不做钳位也不补算 —— 值来自服务端快照，二次修正会掩盖服务端数据本身的问题
        // 返回：无（构造出的实例交给调用方，按值复制）
        // 上游：IQuestProgressAuthority 的实现（NetSessionProgressAuthority、用例里的 FakeAuthority）
        // 下游：无
        public AuthoritativeProgress(int current, int required, bool met)
        {
            Current = current;
            Required = required;
            Met = met;
        }
    }

    // 主要功能：定一道「这条条件的权威值是什么」的接缝，让界面能问出这个数字是谁说的。
    // 主要数据成员：无（纯契约，实现自己持有会话或快照）。
    // 主要外部方法：TryGetCondition（唯一查询入口，只读、无副作用）。
    // 谁在用它：任务面板 QuestPanelModel 与成就运行时 AchievementRuntime 把它当可空依赖持有，交给 QuestProgressOverlay.Apply 逐行查询；Boot / Editor 不直接调用。
    // 注意：客户端自己也有一份进度，那只是预测；两边数值一致靠巧合，一旦配置或时序不同就会「界面显示已完成、服务端说没达成」，而且不报错。
    // 注意：这道缝与它的实现都住在 Game\Quest 而不是 Game\Net —— 实现要认识 NetSession，放进网络层会让网络层反过来依赖任务层。
    public interface IQuestProgressAuthority
    {
        // 功能：按条件编号问出服务端给的权威值，拿不到就明确说「服务端没说」，而不是给一个 0。
        // 入参：conditionId 条件编号（QuestCondition 表主键，>= 0）；progress 出参，返回 false 时内容无意义
        // 步骤：
        //   1) 按编号查服务端下发过的这条条件；还没同步、没接权威、或这条不归服务端管，都算查不到
        //   2) 查不到就直接返回 false，且不填 progress —— 这是全文件唯一把「没有」翻成 false 的地方
        //   3) 查到了才填已累计、需求值、是否达成并返回 true
        // 返回：true 服务端确实说过这条，progress 可以当事实用；false 服务端没说，绝不是「进度是 0」
        // 上游：QuestProgressOverlay.Apply（任务面板与成就显示共用同一份）
        // 下游：NetSessionProgressAuthority.TryGetCondition（运行时实现）→ NetSession 里的权威进度快照；EditMode 用例的 FakeAuthority 是测试替身
        // 注意：服务端明确说 0 时返回 true 且 Current 为 0，那是真的 0；把 false 当 0 会把「还没同步」画成「进度被清零」，实现也不许拿 0/Required 顶替。
        // 注意：这条最容易回归，有专门的 EditMode 用例守着（QuestPanelModelTests.Authority_SilentCondition_KeepsLocalValue_AndMarksItAsPrediction）。
        bool TryGetCondition(int conditionId, out AuthoritativeProgress progress);
    }
}
