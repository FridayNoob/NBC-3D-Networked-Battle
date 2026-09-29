// ============================================================================
//  MatchRules / LockstepTeams / MatchVerdict —— 2v2 的**队伍与胜负**（M4-S4 S4-d）
//  项目：3D联网战斗Demo   对应：`Docs\27` §三十四
//
//  ---------------------------------------------------------------------------
//  一、⚠️ 为什么全放共享层纯函数、而且**优先不加协议**
//  ---------------------------------------------------------------------------
//  ① **队伍分配不加协议**：两端都能拿到 `RoomState` 的成员名单 ⇒ 只要**用同一个纯函数**
//     推导队伍，就不需要在协议里再传一份"谁在哪队" —— **少一个字段就少一处会不一致的地方**。
//     （这一条就是 §33.5 那条判据的应用：能靠"同一份纯逻辑"对齐的，就别靠额外字段。）
//  ② **胜负判定也不加协议之外的东西**：判定吃"世界状态 + 队伍映射"⇒ 纯函数。
//     ⚠️ **别读时钟、别读全局**（那样两端会因为"什么时候调"不同而算出不同结果）。
//  ③ 只要放 `Shared\Sim\`，就**不许出现 protobuf 类型**（W22：本目录通配进 `NBC.Shared`，
//     而 `NBC.Shared` 不认协议 —— 上次撞过，整个仓库编不过）。
//
//  ---------------------------------------------------------------------------
//  二、⚠️ 队伍分配必须**与入房顺序无关**（§29.2 判据②在这一刀的落点）
//  ---------------------------------------------------------------------------
//  做法：**先把成员按 `player_id` 升序排序**，再**交替**分两队（0,1,0,1…）。
//  · 交替而不是"前一半 A 后一半 B"：人数不满时交替仍然均衡（3 人 ⇒ A 2 / B 1），
//    而"切一半"在 3 人时会给出 A 2 / B 1 也一样，但 5 人以上交替更稳。
//  · ⚠️ **去重 + 跳过非正数**（游客/未登录没有实体），理由同 `LockstepRoster`。
//  ---------------------------------------------------------------------------
//  三、规则（**谁先满足**写死在这里，别在调用方各写一份）
//  ---------------------------------------------------------------------------
//      ① 任一队**击杀 ≥ `KillsToWin`** ⇒ 该队**立即**获胜（同一 tick 两队都达到 ⇒ 比击杀数，多者胜；相等 ⇒ 平）；
//      ② 否则跑满 `MaxTicks` ⇒ **击杀多者胜**；相等 ⇒ **平**；
//      ③ 都不满足 ⇒ **未结束**。
//  "击杀"的定义：**对方队伍里 `Hp <= 0` 的实体数**（本片不做助攻/复活）。
// ============================================================================

using System.Collections.Generic;

namespace NBC.Shared.Sim
{
    /// <summary>一局的判定结果。</summary>
    public enum EMatchOutcome
    {
        /// <summary>还没结束。</summary>
        Undecided = 0,

        /// <summary>A 队（队伍号 0）胜。</summary>
        TeamA = 1,

        /// <summary>B 队（队伍号 1）胜。</summary>
        TeamB = 2,

        /// <summary>平局。</summary>
        Draw = 3
    }

    /// <summary>2v2 的队伍分配（**纯函数**、与入房顺序无关）。</summary>
    public static class LockstepTeams
    {
        /// <summary>每队人数上限（2v2）。</summary>
        public const int TeamSize = 2;

        /// <summary>
        /// 按成员名单推导"某人在哪一队"。
        /// <para>⚠️ **按 `player_id` 升序 + 交替**（见文件头二）—— 与入房顺序无关。</para>
        /// </summary>
        /// <param name="members">成员编号（顺序无所谓；非正数跳过、重号去重）。</param>
        /// <returns>函数：`playerId` → 队号（0 或 1）；**不在名单里的返回 −1**。</returns>
        public static List<int> BuildTeamIndex(IReadOnlyList<long> members, out List<long> orderedIds)
        {
            // ⚠️ 先按 id 升序（**不许依赖调用方给的顺序**）
            orderedIds = LockstepRoster.SortIds(members);

            var teamOf = new List<int>(orderedIds.Count);

            for (int i = 0; i < orderedIds.Count; i++)
            {
                teamOf.Add(i % 2);      // 交替：0,1,0,1…
            }

            return teamOf;
        }

        /// <summary>查某人在哪一队（不在名单里 ⇒ −1）。</summary>
        /// <param name="members">成员编号（同 `BuildTeamIndex`）。</param>
        /// <param name="playerId">要查的玩家。</param>
        /// <returns>队号（0/1）；不在名单里返回 −1。</returns>
        public static int TeamOf(IReadOnlyList<long> members, long playerId)
        {
            List<long> ordered;
            List<int> teamOf = BuildTeamIndex(members, out ordered);

            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i] == playerId)
                {
                    return teamOf[i];
                }
            }

            return -1;
        }
    }

    /// <summary>2v2 的规则常量与判定（**纯函数**：不读时钟、不读全局）。</summary>
    public static class MatchRules
    {
        /// <summary>击杀数达标（先到这个数的队赢）。</summary>
        public const int KillsToWin = 5;

        /// <summary>限时（tick）—— 跑满就按击杀多者判。</summary>
        public const int MaxTicks = 1800;      // 30Hz ⇒ 60 秒

        /// <summary>
        /// 判定一局的结果。
        ///
        /// <para>⚠️ **纯函数**：只看传进来的世界与 tick；**不看时钟、不看全局**（见文件头一②）。</para>
        /// <para>规则见文件头三（"谁先满足"写死在这里）。</para>
        /// </summary>
        /// <param name="world">世界（只读它的实体表）。</param>
        /// <param name="teamOf">实体编号 → 队号（0/1；−1 = 不在对局里，忽略）。</param>
        /// <param name="killsA">累计到目前 A 队击杀数（调用方维护；本片就是"对方死的人数"）。</param>
        /// <param name="killsB">累计到目前 B 队击杀数。</param>
        /// <param name="tick">已经跑过的 tick 数。</param>
        /// <returns>判定。</returns>
        public static EMatchOutcome Decide(int killsA, int killsB, int tick)
        {
            bool aReached = killsA >= KillsToWin;
            bool bReached = killsB >= KillsToWin;

            // ① 任一队达标 ⇒ **立即**分胜负
            if (aReached || bReached)
            {
                if (killsA > killsB)
                {
                    return EMatchOutcome.TeamA;
                }

                if (killsB > killsA)
                {
                    return EMatchOutcome.TeamB;
                }

                return EMatchOutcome.Draw;
            }

            // ② 跑满限时 ⇒ 击杀多者胜、相等则平
            if (tick >= MaxTicks)
            {
                if (killsA > killsB)
                {
                    return EMatchOutcome.TeamA;
                }

                if (killsB > killsA)
                {
                    return EMatchOutcome.TeamB;
                }

                return EMatchOutcome.Draw;
            }

            // ③ 都还没满足
            return EMatchOutcome.Undecided;
        }

        /// <summary>
        /// 从**世界状态**数出两队各自的击杀数（"对方死的人数"）。
        /// <para>⚠️ 遍历走 `SnapshotSorted()`（按 Id 升序）⇒ 与登记顺序无关。</para>
        /// </summary>
        /// <param name="world">世界。</param>
        /// <param name="teamOf">实体编号 → 队号（−1 = 忽略）。</param>
        /// <param name="killsA">A 队击杀数。</param>
        /// <param name="killsB">B 队击杀数。</param>
        public static void CountKills(WorldState world, EntityTeam teamOf, out int killsA, out int killsB)
        {
            killsA = 0;
            killsB = 0;

            if (world == null || teamOf == null)
            {
                return;
            }

            SimEntity[] snapshot = world.SnapshotSorted();

            for (int i = 0; i < snapshot.Length; i++)
            {
                // 只数**死掉的**：他属于哪队，就算**对方**一次击杀
                if (snapshot[i].Hp > 0)
                {
                    continue;
                }

                int team = teamOf.TeamOf(snapshot[i].Id);

                if (team == 0)
                {
                    killsB++;       // A 队的人死了 ⇒ B 队得一分
                }
                else if (team == 1)
                {
                    killsA++;
                }
            }
        }

        /// <summary>
        /// wire 上的数字 → 判定（`1=A / 2=B / 3=平 / 其它=未结束`）。
        /// <para>⚠️ 写成函数而不是在两端各写一遍 `switch`：**一处规则**。</para>
        /// </summary>
        /// <param name="number">协议里的数字。</param>
        /// <returns>判定。</returns>
        public static EMatchOutcome FromNumber(int number)
        {
            switch (number)
            {
                case 1: return EMatchOutcome.TeamA;
                case 2: return EMatchOutcome.TeamB;
                case 3: return EMatchOutcome.Draw;
                default: return EMatchOutcome.Undecided;
            }
        }

        /// <summary>判定 → wire 上的数字（与 `FromNumber` **互为逆**）。</summary>
        /// <param name="outcome">判定。</param>
        /// <returns>数字。</returns>
        public static int ToNumber(EMatchOutcome outcome)
        {
            switch (outcome)
            {
                case EMatchOutcome.TeamA: return 1;
                case EMatchOutcome.TeamB: return 2;
                case EMatchOutcome.Draw: return 3;
                default: return 0;
            }
        }

        /// <summary>一条人话（日志/统计用）。</summary>
        /// <param name="outcome">判定。</param>
        /// <returns>文案。</returns>
        public static string Describe(EMatchOutcome outcome)
        {
            switch (outcome)
            {
                case EMatchOutcome.TeamA: return "A 队胜";
                case EMatchOutcome.TeamB: return "B 队胜";
                case EMatchOutcome.Draw: return "平局";
                default: return "未结束";
            }
        }
    }

    /// <summary>实体编号 → 队号的查询（用小接口而不是 `Func`，免得在共享层引入委托分配）。</summary>
    public interface EntityTeam
    {
        /// <summary>查队号。</summary>
        /// <param name="entityId">实体编号。</param>
        /// <returns>队号（0/1）；不在对局里返回 −1。</returns>
        int TeamOf(int entityId);
    }
}
