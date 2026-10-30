// 这个文件里有：
//   EMatchOutcome（枚举）：一局 2v2 的判定结果，未结束 / A 胜 / B 胜 / 平局，编号即协议数字。
//   LockstepTeams（静态类）：按成员名单推 2v2 的两队，规则是 player_id 升序后交替分。
//   MatchRules（静态类）：2v2 的胜负规则常量与判定，全是纯函数。
//   EntityTeam（接口）：实体编号到队号的查询，给数击杀的纯函数用。
//   SortedTeamMap（类）：从成员名单一次算好队号映射，双端共用同一个实现。
//
// 注意：本文件在 Shared\Sim 下，随目录通配进 NBC.Shared；而 NBC.Shared 不引用 NBC.Protocol，这里不许出现任何 protobuf 类型，否则整个仓库编不过。
// 注意：队伍分配与胜负判定都写死成纯函数，不读时钟、不读全局 —— 两端调用时机不同，一读就会各算一套结果；谁先满足哪条规则也只在这里写一次。
// 注意：单位按锁步世界这一套 —— 时间是 tick（30Hz 逻辑帧，1800 tick = 60 秒）；本文件没有长度量（该层长度单位是米），人数与击杀数的单位是人。
// 规则与理由详见 Docs\27 §三十四。

using System.Collections.Generic;

namespace NBC.Shared.Sim
{
    // 主要功能：表示一局 2v2 走完之后的判定结果，只有未结束、A 胜、B 胜、平局四种。
    // 主要数据成员：Undecided / TeamA / TeamB / Draw 四个成员，取值 0 / 1 / 2 / 3。
    // 主要外部方法：无；到协议数字的转换走 MatchRules.FromNumber / ToNumber，文案走 Describe。
    // 谁在用它：服务端 LockstepService 判胜负、客户端 LockstepClient 复算对账、NetSession 转对外结果，联调探针也拿它断言。
    /// <summary>一局的判定结果；枚举值与协议数字一一对应（0 未结束、1 A 胜、2 B 胜、3 平局）。</summary>
    public enum EMatchOutcome
    {
        /// <summary>还没结束；编号 0。</summary>
        Undecided = 0,

        /// <summary>A 队（队伍号 0）获胜；编号 1。</summary>
        TeamA = 1,

        /// <summary>B 队（队伍号 1）获胜；编号 2。</summary>
        TeamB = 2,

        /// <summary>平局；编号 3。</summary>
        Draw = 3
    }

    // 主要功能：把一份成员名单分成 2v2 的两队，规则固定为 player_id 升序后按序号交替分。
    // 主要数据成员：TeamSize（每队人数上限，人）。
    // 主要外部方法：BuildTeamIndex（一次算出全员队号）、TeamOf（查单个人）。
    // 谁在用它：SortedTeamMap 构造时调 BuildTeamIndex；服务端、客户端与探针都只用 SortedTeamMap，TeamOf 目前没有调用点。
    // 注意：结果必须与入房顺序无关 —— 两端拿到的成员顺序可能不同，按原序分队会分出两套队伍，胜负判定当场错开。
    /// <summary>按 player_id 升序再交替分 2v2 两队；与入房顺序无关，双端必须得到同一份结果。</summary>
    public static class LockstepTeams
    {
        /// <summary>每队人数上限，单位人；2v2 固定为 2。</summary>
        public const int TeamSize = 2;

        /// <summary>按成员名单推出全员队号，并把规范化后的名单从 orderedIds 交出。</summary>
        /// <param name="members">成员编号（player id，顺序无所谓；非正数跳过、重号去重由 LockstepRoster.SortIds 负责）。</param>
        /// <param name="orderedIds">出参，去重并按 id 升序后的成员编号。</param>
        /// <returns>与 orderedIds 同序的队号表，0 = A 队、1 = B 队。</returns>
        // 功能：把成员名单去重排序，再按序号交替分到 0 / 1 两队，同时交出排序后的名单。
        // 入参：members 成员编号（player id，无单位；顺序无所谓，非正数与重号由 SortIds 丢掉）；orderedIds 出参，规范化后的成员编号。
        // 返回：与 orderedIds 同序的队号表，0 = A 队、1 = B 队；名单为空或全是不合法编号时返回空表。
        // 步骤：
        //   1) 先走 LockstepRoster.SortIds：非正数跳过、重号去重、按 id 升序 —— 队伍结果才能与入房顺序无关
        //   2) 再按序号 i % 2 交替分队：人数不满时也均衡（3 人 ⇒ 2 / 1），不会一边倒
        // 注意：不许依赖调用方传进来的顺序，也别在这里另写排序 —— 实体表用的也是 SortIds，两处顺序一错开，第 0 帧哈希就分歧。
        // 上游：SortedTeamMap 构造函数、联调探针
        // 下游：LockstepRoster.SortIds
        public static List<int> BuildTeamIndex(IReadOnlyList<long> members, out List<long> orderedIds)
        {
            orderedIds = LockstepRoster.SortIds(members);

            var teamOf = new List<int>(orderedIds.Count);

            for (int i = 0; i < orderedIds.Count; i++)
            {
                teamOf.Add(i % 2);
            }

            return teamOf;
        }

        /// <summary>查某个玩家分到哪一队。</summary>
        /// <param name="members">成员编号（player id，顺序无所谓）。</param>
        /// <param name="playerId">要查的玩家编号。</param>
        /// <returns>队号 0 或 1；不在名单里返回 −1。</returns>
        // 功能：在成员名单里查某个玩家属于哪一队。
        // 入参：members 成员编号（player id，顺序无所谓）；playerId 要查的玩家编号。
        // 返回：队号 0 或 1；不在名单里返回 −1（含非正数与被去重掉的编号）。
        // 步骤：
        //   1) 直接复用 BuildTeamIndex 拿排序后的名单与队号，分队规则只留一处
        //   2) 顺序扫一遍找 playerId，命中就回同下标的队号
        // 注意：每次调用都会重建整份映射（含一次排序），只适合零星查询；热点循环里应当先建一个 SortedTeamMap 反复查。
        // 上游：暂无调用点
        // 下游：BuildTeamIndex
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

    // 主要功能：给出 2v2 一局的胜负常量与判定，全部在共享层用纯函数算，双端得同一结果。
    // 主要数据成员：KillsToWin（击杀门槛，人）、MaxTicks（限时上限，tick）。
    // 主要外部方法：Decide（判结果）、CountKills（数两队击杀）、FromNumber / ToNumber（协议数字互转）、Describe（一句人话）。
    // 谁在用它：服务端 LockstepService 判胜负；客户端 LockstepClient 复算对账；NetSession 收发结果；联调探针验规则。
    // 注意：判定不许读时钟、读全局，也不许把规则在调用方各写一份 —— 两端调用时机不同，一读就会算出不同结果。
    /// <summary>2v2 的胜负规则常量与纯函数判定：不读时钟、不读全局，双端必得同一结果。</summary>
    public static class MatchRules
    {
        /// <summary>任一队击杀到这个数就立即获胜，单位人（击杀数）；必须小于等于 LockstepTeams.TeamSize，否则规则在真实 2v2 里不可达（每队只有 2 个敌人、本 Demo 不做复活），成了死分支。</summary>
        public const int KillsToWin = 2;

        /// <summary>限时上限，单位 tick（30Hz 逻辑帧，1800 tick = 60 秒）；跑满就按击杀多者判。</summary>
        // 注意：它是僵局兜底，不是主要取胜路径（主路径是团灭对方，即 2 击杀）。按当前 WorldStep.AttackDamage = 25、SpawnLayout.SpawnHp = 100 算，四下打死一个人，正常对局远用不到 60 秒；只有双方都不打时才靠它收场，那时 0/0 ⇒ 平局，是有意义的结果。
        // 注意：保持 1800 不变 —— 调小会让正常对局被「时间到」打断，调大只是让僵局多耗一会儿。
        public const int MaxTicks = 1800;

        /// <summary>判一局结束了没有、谁赢。</summary>
        /// <param name="killsA">A 队累计击杀数，单位人（调用方维护）。</param>
        /// <param name="killsB">B 队累计击杀数，单位人。</param>
        /// <param name="tick">已经跑过的 tick 数（锁步世界时间，30Hz 逻辑帧；1800 tick = 60 秒，与 MaxTicks 同单位）。</param>
        /// <returns>TeamA / TeamB / Draw；条件都没满足时返回 Undecided。</returns>
        // 功能：按「先看击杀门槛、再看限时、最后比击杀数」的顺序判一局有没有结束。
        // 入参：killsA / killsB 两队累计击杀数（人，调用方维护）；tick 已跑过的 tick 数（30Hz 逻辑帧，与 MaxTicks 同单位）。
        // 返回：TeamA 或 TeamB 表示该队获胜；Draw 表示平局；Undecided 表示还没结束。
        // 步骤：
        //   1) 先看任一队是否到 KillsToWin：到了就立即分胜负，不让已经团灭对方的局被限时提前掐断
        //   2) 两队同时达标就比击杀数，多者胜、相等判平 —— 同一 tick 双杀时也不会随意偏袒某一队
        //   3) 没人到门槛再看 tick 是否跑满 MaxTicks：跑满按击杀多者胜、相等判平
        //   4) 以上都不满足才回未结束
        // 注意：不许读时钟或全局状态 —— 两端调用时机不同，读了就会各算一份结果；这正是它放在共享层的原因。
        // 上游：服务端 LockstepService 判胜负、客户端 LockstepClient 复算对账、联调探针
        // 下游：无
        public static EMatchOutcome Decide(int killsA, int killsB, int tick)
        {
            bool aReached = killsA >= KillsToWin;
            bool bReached = killsB >= KillsToWin;

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

            return EMatchOutcome.Undecided;
        }

        /// <summary>数出两队各自拿了多少击杀，口径是对方队伍里血量为 0 的实体数。</summary>
        /// <param name="world">世界状态，只读它的实体表；null 时两队击杀都按 0 返回。</param>
        /// <param name="teamOf">实体编号到队号的映射；null 时两队击杀都按 0 返回，队号 −1 的实体不计入任何一方。</param>
        /// <param name="killsA">出参，A 队击杀数，单位人。</param>
        /// <param name="killsB">出参，B 队击杀数，单位人。</param>
        // 功能：遍历世界里的实体，按「对方死的人数」反推两队各自的击杀数。
        // 入参：world 世界状态（可为 null）；teamOf 实体编号到队号的映射（可为 null，队号 −1 表示不在对局里）；killsA / killsB 两个出参。
        // 返回：无；结果走 killsA / killsB 两个出参，world 或 teamOf 为 null 时两者都是 0。
        // 步骤：
        //   1) 先把两个出参清零：调用方不必预先置 0，提前 return 也不会留下旧值
        //   2) world 或 teamOf 为 null 就直接回 0，判空不散落到下面的循环里
        //   3) 用 SnapshotSorted 取按 Id 升序的快照 —— 结果与实体登记顺序无关
        //   4) 血量为正的跳过，只数死掉的
        //   5) A 队的人死了给 B 队记一分，B 队的人死了给 A 队记一分；队号 −1 的人两边都不算
        // 注意：口径是「对方死的人数」而不是击杀事件，本 Demo 不做助攻与复活，所以死亡只增不减；一旦加复活，这个口径会把复活过的人也数进去。
        // 注意：必须走 SnapshotSorted，直接遍历内部表会随登记顺序变化，两端可能数出不同结果。
        // 上游：客户端 LockstepClient 复算对账、服务端 LockstepService 判胜负、联调探针
        // 下游：WorldState.SnapshotSorted、EntityTeam.TeamOf
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
                if (snapshot[i].Hp > 0)
                {
                    continue;
                }

                int team = teamOf.TeamOf(snapshot[i].Id);

                if (team == 0)
                {
                    killsB++;
                }
                else if (team == 1)
                {
                    killsA++;
                }
            }
        }

        /// <summary>协议数字转判定：1 = A 队胜、2 = B 队胜、3 = 平、其它 = 未结束。</summary>
        /// <param name="number">协议里的判定数字（整数，约定取值 1 / 2 / 3）。</param>
        /// <returns>对应判定；不认识的值一律按未结束返回，不抛异常。</returns>
        // 功能：把协议里的判定数字翻成枚举。
        // 入参：number 协议字段里的整数，1 = A 胜、2 = B 胜、3 = 平。
        // 返回：对应判定；0 与任何未知值都返回未结束。
        // 步骤：
        //   1) 逐个 case 直译协议约定
        //   2) 默认分支把未知值收成未结束，与 ToNumber 的默认 0 对称
        // 注意：这是双端唯一的翻译处，别在收发两端各写一份 switch，否则迟早对不上；数字含义属协议，改它等于改协议。
        // 上游：NetSession 收到胜负结果、客户端 LockstepClient 对账
        // 下游：无
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

        /// <summary>判定转协议数字：A 胜 1、B 胜 2、平 3、未结束 0，与 FromNumber 互为逆。</summary>
        /// <param name="outcome">判定值。</param>
        /// <returns>协议数字，取值 0..3。</returns>
        // 功能：把判定翻回协议里的整数。
        // 入参：outcome 判定值。
        // 返回：A 胜 1、B 胜 2、平 3、未结束 0（默认分支）。
        // 步骤：
        //   1) 逐个 case 直译协议约定，默认回 0 —— 与 FromNumber 的默认分支对称，往返不丢信息
        // 注意：这些数字是与服务端约定的协议值，改动等于改协议，两端必须同时上线。
        // 上游：服务端 LockstepService 宣告胜负、联调探针
        // 下游：无
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

        /// <summary>把判定翻成一句中文，供日志与界面直接显示。</summary>
        /// <param name="outcome">判定值。</param>
        /// <returns>「A 队胜」「B 队胜」「平局」「未结束」之一；未知值按未结束处理。</returns>
        // 功能：把判定翻成可以直接写进日志的中文。
        // 入参：outcome 判定值。
        // 返回：固定四选一的文案，未知值兜底成未结束。
        // 步骤：
        //   1) 逐个 case 给文案，默认分支兜底成未结束，日志里不会出现空串
        // 上游：客户端 LockstepClient 的分歧报告、NetSession 的结果日志、服务端 LockstepService 的日志
        // 下游：无
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

    // 主要功能：把「实体编号 → 队号」抽象成一个查询，供数击杀的纯函数调用。
    // 主要数据成员：无（纯接口）。
    // 主要外部方法：TeamOf（查队号）。
    // 谁在用它：MatchRules.CountKills 的入参类型；实现是 SortedTeamMap，客户端 LockstepClient 只持有这个接口，联调探针另有一个自己的实现。
    // 注意：用接口而不是 Func / 委托 —— 共享层每次查询都不产生闭包分配。
    /// <summary>实体编号到队号的查询；用接口而不是委托，避免共享层每次查询都分配闭包。</summary>
    public interface EntityTeam
    {
        /// <summary>查实体在哪一队。</summary>
        /// <param name="entityId">实体编号（玩家 id）。</param>
        /// <returns>队号 0 或 1；不在对局里返回 −1。</returns>
        int TeamOf(int entityId);
    }

    // 主要功能：构造时按成员名单算好队号映射并缓存，之后按实体编号查队。
    // 主要数据成员：m_ordered（去重升序后的成员编号，player id）。
    // 主要外部方法：TeamOf（查队号）、Count（名单人数）。
    // 谁在用它：NetSession.SyncLockstepTeams 建房后 new 一份交给 LockstepClient.SetTeams；服务端 LockstepService 用它判胜负；联调探针也 new 它。
    // 注意：只此一份实现 —— 两端各写一份映射就是两处会不一致的地方，而它直接决定谁赢。
    /// <summary>从成员名单推出来的队伍映射，服务端与客户端共用同一个实现。</summary>
    public sealed class SortedTeamMap : EntityTeam
    {
        // 去重升序后的成员编号（player id，正整数，无单位）；下标即分队序号，队号 = 下标 % 2。
        private readonly List<long> m_ordered;

        /// <summary>按成员名单建映射；分队规则只走 LockstepTeams.BuildTeamIndex，这里不复制第二份。</summary>
        /// <param name="members">成员编号（player id，顺序无所谓；非正数跳过、重号去重）。</param>
        // 功能：按成员名单算出并缓存「成员编号 → 队号」的映射。
        // 入参：members 成员编号（player id，顺序无所谓；非正数与重号由 LockstepRoster.SortIds 丢掉）。
        // 返回：无（构造）。
        // 步骤：
        //   1) 调 LockstepTeams.BuildTeamIndex 拿规范化后的名单，顺便让它把队号算好
        //   2) 查队号时用下标 % 2 现算，分队规则仍然只有 BuildTeamIndex 一处
        //   3) 两个表长度对不上就清空名单：宁可全查不到，也不能按下标越界
        // 注意：这里刻意不保存 BuildTeamIndex 返回的 teamOf（它按序号存）—— 存下来就多一份可能和规则漂移的副本。
        // 上游：NetSession.SyncLockstepTeams、服务端 LockstepService、联调探针
        // 下游：LockstepTeams.BuildTeamIndex
        public SortedTeamMap(IReadOnlyList<long> members)
        {
            List<int> teamOf = LockstepTeams.BuildTeamIndex(members, out m_ordered);

            if (teamOf.Count != m_ordered.Count)
            {
                m_ordered = new List<long>();
            }
        }

        /// <summary>查实体在哪一队；不在名单里返回 −1。</summary>
        /// <param name="entityId">实体编号（玩家 id）。</param>
        /// <returns>队号 0 或 1；名单里没有该编号时返回 −1。</returns>
        // 功能：在缓存的名单里查实体编号，返回它所在的队伍。
        // 入参：entityId 实体编号（玩家 id，无单位）。
        // 返回：队号 0 或 1；名单里没有这个编号时返回 −1。
        // 步骤：
        //   1) 顺序扫 m_ordered 找编号：2v2 最多几个人，不必为它建字典
        //   2) 命中后按下标 % 2 算队号，与 LockstepTeams 的交替规则一致
        // 注意：队号只能按下标现算；另存一份队号表就多一处会与分队规则对不上的地方。
        // 上游：MatchRules.CountKills
        // 下游：无
        public int TeamOf(int entityId)
        {
            for (int i = 0; i < m_ordered.Count; i++)
            {
                if (m_ordered[i] == entityId)
                {
                    return i % 2;
                }
            }

            return -1;
        }

        /// <summary>名单里有几个成员，单位人；排查用。</summary>
        public int Count
        {
            get { return m_ordered.Count; }
        }
    }
}
