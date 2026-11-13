// 这个文件里有：
//   EPlayerLayer（枚举）：状态机三层 —— 移动层 / 动作层 / 反应层
//   EPlayerState（枚举）：所有状态的取值；前三个是「上层标识」，其余是「下层状态」

namespace NBC.Game.Player
{
    // 主要功能：标明一个状态属于哪一层，决定它的优先级与「谁先被让路」。
    // 主要数据成员：Locomotion（移动）、Action（动作）、Reaction（反应）。
    // 主要外部方法：无；层归属请用 PlayerStateCatalog.LayerOf 从状态值推。
    // 谁在用它：PlayerStateCatalog、PlayerStateMachine。
    public enum EPlayerLayer
    {
        // 移动层：站 / 走 / 跑 / 冲 / 蹲三态
        Locomotion = 0,

        // 动作层：攻击 / 技能 / 冲刺 / 格挡 / 姿势
        Action = 1,

        // 反应层：受击 / 硬直 / 死亡（被打断的那一类）
        Reaction = 2,
    }

    // 主要功能：给分层状态机一个统一的标识类型 —— 上层与下层共用这一个枚举。
    // 主要数据成员：三个上层标识（Locomotion/Action/Reaction）+ 全部下层状态。
    // 主要外部方法：无；层归属、入口、是否禁移动都由 PlayerStateCatalog 提供。
    // 谁在用它：PlayerStateMachine（HierarchicalStateMachine<EPlayerState>）、PlayerBrain（只读当前状态）。
    // 注意：数值按层分段（0/100/200 与 1~9 / 101~109 / 201~204），LayerOf 靠区间判断，新增状态必须落在本段内。
    public enum EPlayerState
    {
        // 上层：移动层
        Locomotion = 0,

        // 上层：动作层
        Action = 100,

        // 上层：反应层
        Reaction = 200,

        // 站着不动，米/秒 = 0
        Idle = 1,

        // 慢走，默认 2 米/秒（按住 Alt）
        Walk = 2,

        // 跑，默认 5 米/秒（默认可移动速度）
        Run = 3,

        // 冲刺，默认 8 米/秒（按住 Shift）
        Sprint = 4,

        // 蹲着不动
        CrouchIdle = 5,

        // 蹲着慢走，默认 1.5 米/秒
        CrouchWalk = 6,

        // 蹲着快走，默认 3 米/秒（蹲着按 Shift）
        CrouchJog = 7,

        // 跳跃上升段（R035d 接线，本步只占位）
        Jump = 8,

        // 下落段（R035d 接线，本步只占位）
        Fall = 9,

        // 攻击第 1 段
        Attack1 = 101,

        // 攻击第 2 段（R035b 接线）
        Attack2 = 102,

        // 攻击第 3 段（R035b 接线）
        Attack3 = 103,

        // 技能 1（R035b 接线）
        Skill1 = 104,

        // 技能 2（R035b 接线）
        Skill2 = 105,

        // 技能 3（R035b 接线）
        Skill3 = 106,

        // 冲刺动作（R035b 接线）
        Dash = 107,

        // 格挡（按住右键，R035b 接线）
        Block = 108,

        // 姿势 / 表情（R035c 接线）
        Pose = 109,

        // 左侧受击
        HitLeft = 201,

        // 右侧受击（R035c 接线）
        HitRight = 202,

        // 硬直（R035c 接线）
        Stun = 203,

        // 死亡：永久，不可离开
        Die = 204,
    }
}
