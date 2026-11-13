// 这个文件里有：
//   IPlayerDash（接口）：让状态机之外的位移组件执行一段短冲刺

namespace NBC.Game.Player
{
    // 主要功能：给「前冲」这类有时限的位移留一个窄接口，状态机只负责动作与动画，位移交给位移组件。
    // 主要数据成员：无。
    // 主要外部方法：StartDash 指定速度与时长。
    // 谁在用它：PlayerBrain 在 Dash 动作开始时调用；PlayerMove 实现它。
    public interface IPlayerDash
    {
        // 功能：开始一段匀速前冲，再次调用会覆盖上一段。
        // 入参：speedMetersPerSecond 前进速度（米/秒，正数往前）；durationSeconds 持续秒数（<=0 视为无效）。
        // 返回：无。
        // 步骤：实现方自己记录结束时间，在 Update 里推进。
        // 上游/下游：PlayerBrain.NotifyDash 成功后调用；PlayerMove.Update 执行。
        void StartDash(float speedMetersPerSecond, float durationSeconds);
    }
}
