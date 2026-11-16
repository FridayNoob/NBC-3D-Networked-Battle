// 这个文件里有：
//   DungeonOutcomeText：把结算数字转成玩家能读的文字（纯函数，不碰 UI、不碰场景）。

using NBC.Protocol;

namespace NBC.Game.UI
{
    /// <summary>
    /// 结算文案工具：胜负标题、用时格式、统计一行字。
    /// 主要数据成员：两个结果常量 Win / Lose（数值与协议 DungeonResult.outcome 一致）。
    /// 主要外部方法：Title、FormatDuration、Stats。
    /// 谁在用它：DungeonResultPanel 画面板，EditMode 用例直接断言这三个函数。
    /// </summary>
    public static class DungeonOutcomeText
    {
        /// <summary>结算结果：胜（怪清光）。数值 1 与协议约定一致。</summary>
        public const int Win = 1;

        /// <summary>结算结果：负（英雄全灭）。数值 2 与协议约定一致。</summary>
        public const int Lose = 2;

        // 功能：把结果码转成标题。
        // 入参：outcome 服务端给的结果码。
        // 返回：胜利 / 失败 / 未知（不认识的码不抛异常，界面照常显示）。
        // 步骤：1 判胜、2 判负、其余统一给未知
        // 上游/下游：DungeonResultPanel.Show 调用。
        public static string Title(int outcome)
        {
            if (outcome == Win)
            {
                return "胜利";
            }

            if (outcome == Lose)
            {
                return "失败";
            }

            return "未知";
        }

        // 功能：把毫秒转成 mm:ss。
        // 入参：milliseconds 本局用时（毫秒；负数按 0 处理）。
        // 返回：形如 "01:23" 的字符串。
        // 步骤：负数先夹到 0，再分别算分钟与秒，两位补零
        // 上游/下游：Stats 拼接、DungeonResultPanel 显示。
        public static string FormatDuration(int milliseconds)
        {
            if (milliseconds < 0)
            {
                milliseconds = 0;
            }

            int totalSeconds = milliseconds / 1000;
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;

            return minutes.ToString("00") + ":" + seconds.ToString("00");
        }

        // 功能：拼一行统计数据。
        // 入参：result 服务端结算（非空）。
        // 返回：形如 "用时 01:23 · 击杀 12 · 死亡 1"。
        // 步骤：三块数据各自格式化后用「 · 」连接，顺序固定便于验收比对
        // 上游/下游：DungeonResultPanel.Show 调用。
        public static string Stats(DungeonResult result)
        {
            return "用时 " + FormatDuration(result.DurationMs)
                 + " · 击杀 " + result.MonsterKills
                 + " · 死亡 " + result.HeroDeaths;
        }
    }
}
