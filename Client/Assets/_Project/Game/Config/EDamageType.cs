// 这个文件里有：
//   EDamageType（枚举）：伤害类型，配置表 Skill.damageType 用（手写，不由工具生成）

namespace NBC.Game.Config
{
    // 主要功能：给配置表提供伤害类型的成员名（表里写 Fire / Ice / Physical）。
    // 主要数据成员：Physical / Fire / Ice。
    // 主要外部方法：无。
    // 谁在用它：Skill 表加载时按成员名解析（Enum.Parse，忽略大小写）。
    // 注意：枚举必须手写 —— 工具不认识业务语义（谁是 Fire 属于游戏设计）。
    // 注意：写错成员名工具查不出来，要到运行时 Enum.Parse 才炸；
    //       想提前查，把成员登记进 ConfigPolicy.KnownEnumMembers。
    public enum EDamageType
    {
        // 物理
        Physical = 0,

        // 火焰
        Fire = 1,

        // 冰霜
        Ice = 2
    }
}
