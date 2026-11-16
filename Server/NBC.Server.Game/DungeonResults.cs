// 这个文件里有：
//   DungeonResults：把一局副本的现状拼成结算消息（纯函数，不发送、不改房间）。

using NBC.Protocol;

namespace NBC.Server.Game;

/// <summary>
/// 副本结算构建器：把 DungeonBattle 的现状转换成下发给客户端的 DungeonResult。
/// 主要数据成员：两个结果常量 OutcomeWin / OutcomeLose（数值与协议约定一致）。
/// 主要外部方法：Build(battle)。
/// 谁在用它：RoomBattleService.RecordIfFinished（副本结束那一次），以及 _net-probe 的用例。
/// </summary>
public static class DungeonResults
{
    /// <summary>结算结果：胜（怪清光）。数值 1 与协议 DungeonResult.outcome 的约定一致。</summary>
    public const int OutcomeWin = 1;

    /// <summary>结算结果：负（英雄全灭）。数值 2 与协议约定一致。</summary>
    public const int OutcomeLose = 2;

    // 功能：把一局战斗的现状拼成结算消息。
    // 入参：battle 这一局的世界（调用方保证非空）。
    // 返回：可直接塞进 ServerMessage 的 DungeonResult。
    // 步骤：
    //   1) 怪物清光算胜，否则算负（与 RoomBattleService 的历史判据一致）
    //   2) 用时、击杀、死亡都从世界现算，不在别处另存一份
    // 上游/下游：RoomBattleService.RecordIfFinished 调用；不访问网络与数据库。
    public static DungeonResult Build(DungeonBattle battle)
    {
        return new DungeonResult
        {
            Outcome = battle.AliveMonsterCount == 0 ? OutcomeWin : OutcomeLose,
            DurationMs = battle.ElapsedMs,
            MonsterKills = battle.MonsterKills,
            HeroDeaths = battle.HeroDeaths,
        };
    }
}
