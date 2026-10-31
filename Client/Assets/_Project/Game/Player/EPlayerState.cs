// 这个文件里有：
//   EPlayerState（枚举）：角色「逻辑状态」的取值，和 Animator 的表现状态是两层东西。

namespace NBC.Game.Player
{
    // 主要功能：标识角色的逻辑状态，供 PlayerBrain 的状态机使用。
    // 主要数据成员：Idle（站着，禁止移动）、Move（可移动）。
    // 主要外部方法：无（数组下标 / 字典键直接用）。
    // 谁在用它：PlayerBrain（状态机泛型参数）、PlayerIdleState、PlayerMoveState。
    // 注意：这是逻辑状态，不是 Animator 的 S_Idle / S_Walk —— 逻辑管规则，Animator 管画面，两层不要混。
    // 注意：以后加 Attack / Dead 直接在这里加值；Dead 要能挡住 Move 的进入（用 CanEnterFrom 判）。
    public enum EPlayerState
    {
        // 站着：当前不许移动（PlayerMove 被禁用）
        Idle = 0,

        // 可移动：PlayerMove 启用，正常读输入
        Move = 1,
    }
}
