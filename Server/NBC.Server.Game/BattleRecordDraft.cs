// ============================================================================
//  BattleRecordDraft —— 一局打完之后的"战绩草稿"（**中性结构，不认识数据库**）
//  项目：3D联网战斗Demo   对应：需求文档 SRV-13（战绩落库）
//
//  ---------------------------------------------------------------------------
//  为什么是"草稿"，而且住在 `NBC.Server.Game` 而不是 `NBC.Server.Data`
//  ---------------------------------------------------------------------------
//  依赖方向必须单向：`Game`（纯逻辑）**不许**引 `Data`（那会把数据库拖进逻辑层，
//  于是"想验一个伤害公式，得先起 MySQL"）。所以：
//
//      `RoomBattleService`（Game）  ——产出一份**草稿**——>  `BattleRecordWriter`（Data）——> MySQL
//                     ↑ 中间只有这个中性结构，两边都不认识对方的细节
//
//  这与 M2 的 `IQuestRewardSink`（"该不该发"与"怎么发"分开）是**同一个套路**。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 谁负责"什么时候算打完"
//  ---------------------------------------------------------------------------
//  不是这里。这里只描述"打完了是什么样"。
//  `DungeonBattle.IsFinished`（怪清光 或 英雄全死）是判据，`RoomBattleService` 负责在
//  那一刻把草稿交出去 —— 见那个类里的 `BattleFinished` 事件。
// ============================================================================

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NBC.Server.Game
{
    /// <summary>
    /// 一个玩家在这一局里的统计（**累加器**，`DungeonBattle` 里一边打一边加）。
    /// <para>⚠️ 它是**可变**的：每帧都在改，所以只应该在服务端逻辑线程上碰它。</para>
    /// </summary>
    public sealed class BattlePlayerStat
    {
        /// <summary>玩家编号（= `ClientSession.PlayerId`）。</summary>
        public long PlayerId;

        /// <summary>这一局用的英雄（`Hero` 表主键）。</summary>
        public int HeroConfigId;

        /// <summary>击杀（只算怪）。</summary>
        public int Kill;

        /// <summary>死亡。</summary>
        public int Death;

        /// <summary>造成的伤害（**实际扣掉的血**之和）。</summary>
        public long DamageDealt;

        /// <summary>承受的伤害。</summary>
        public long DamageTaken;

        /// <summary>治疗（本项目还没有治疗技能 ⇒ 恒为 0，**字段先留着**）。</summary>
        public int Heal;
    }

    /// <summary>一个玩家在战绩里的一行（**不可变**，交出去之后就不再改）。</summary>
    public readonly struct BattlePlayerDraft
    {
        /// <summary>玩家编号。</summary>
        public readonly long PlayerId;

        /// <summary>英雄编号。</summary>
        public readonly int HeroConfigId;

        /// <summary>队伍（本项目 PVE 恒 0）。</summary>
        public readonly int Team;

        /// <summary>击杀。</summary>
        public readonly int Kill;

        /// <summary>死亡。</summary>
        public readonly int Death;

        /// <summary>助攻（本项目还没做 ⇒ 恒 0）。</summary>
        public readonly int Assist;

        /// <summary>造成伤害。</summary>
        public readonly long DamageDealt;

        /// <summary>承受伤害。</summary>
        public readonly long DamageTaken;

        /// <summary>治疗。</summary>
        public readonly int Heal;

        /// <summary>赢了没有。</summary>
        public readonly bool IsWin;

        /// <summary>是不是 AI（本项目恒 false：AI 是怪，不算玩家明细）。</summary>
        public readonly bool IsAi;

        /// <summary>造一行。</summary>
        /// <param name="stat">累加器。</param>
        /// <param name="isWin">赢了没有。</param>
        public BattlePlayerDraft(BattlePlayerStat stat, bool isWin)
        {
            PlayerId = stat.PlayerId;
            HeroConfigId = stat.HeroConfigId;
            Team = 0;
            Kill = stat.Kill;
            Death = stat.Death;
            Assist = 0;
            DamageDealt = stat.DamageDealt;
            DamageTaken = stat.DamageTaken;
            Heal = stat.Heal;
            IsWin = isWin;
            IsAi = false;
        }
    }

    /// <summary>一局打完之后的战绩草稿（交给数据层去落库）。</summary>
    public sealed class BattleRecordDraft
    {
        /// <summary>游戏模式：1 = PVE、2 = PVP（本项目的副本都是 PVE）。</summary>
        public const int GameModePve = 1;

        /// <summary>同步模式编号：1 = 状态同步、2 = 帧同步、3 = 混合（与 `BattleInstance.SyncMode` 对应）。</summary>
        public int SyncModeCode = 1;

        /// <summary>房间号（**字符串** —— 数据库那列是 BIGINT，转换由数据层做，见那个 DAO）。</summary>
        public string RoomId = string.Empty;

        /// <summary>副本编号（`Dungeon` 表主键）。</summary>
        public int MapId;

        /// <summary>随机种子（帧同步复现用）。</summary>
        public int RandomSeed;

        /// <summary>起始帧。</summary>
        public long StartTick;

        /// <summary>结束帧。</summary>
        public long EndTick;

        /// <summary>一帧多少毫秒（30Hz ⇒ 33）。</summary>
        public int TickIntervalMs = 33;

        /// <summary>玩家明细（**按玩家编号升序**，让同一个房间的多次记录可对比）。</summary>
        public readonly List<BattlePlayerDraft> Players = new List<BattlePlayerDraft>();

        /// <summary>
        /// 赢了没有（"怪清光了"算赢）。
        /// <para>⚠️ 它与每个玩家的 `IsWin` 是**两件事**：这里说的是"这一局的结果"，
        /// 明细里的那个是"这个玩家的结果"。PVE 里两者一致，但**别把它们合并成一个字段** ——
        /// 将来做 PVP 时，"队伍赢"与"玩家赢"就会分叉。</para>
        /// </summary>
        public bool IsWin;

        /// <summary>这一局打了多久（毫秒）。</summary>
        public int DurationMs
        {
            get
            {
                long ticks = EndTick - StartTick;
                if (ticks < 0)
                {
                    ticks = 0;
                }

                long ms = ticks * TickIntervalMs;

                // 数据库那列是 INT（上限约 24 天），钳一下防止溢出写进去变成负数
                return ms > int.MaxValue ? int.MaxValue : (int)ms;
            }
        }

        /// <summary>
        /// 拼 `battle_record.result_json`。
        /// <para>⚠️ **手写 JSON，不上序列化器**：这里的形状是固定的（全是数字 + 房间号字符串），
        /// 而手写能让"写进去的到底是什么"一眼看全 —— 排查时不用去想某个序列化器的默认行为。
        /// 代价是**转义要自己做**（见 `AppendJsonString`），别漏。</para>
        /// </summary>
        /// <returns>一行 JSON。</returns>
        public string ToResultJson()
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"room\":");
            AppendJsonString(sb, RoomId);
            sb.Append(",\"map\":").Append(MapId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"win\":").Append(IsWin ? "true" : "false");
            sb.Append(",\"startTick\":").Append(StartTick.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"endTick\":").Append(EndTick.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"players\":[");

            for (int i = 0; i < Players.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                BattlePlayerDraft p = Players[i];
                sb.Append("{\"playerId\":").Append(p.PlayerId.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"heroId\":").Append(p.HeroConfigId.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"kill\":").Append(p.Kill.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"death\":").Append(p.Death.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"damageDealt\":").Append(p.DamageDealt.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"damageTaken\":").Append(p.DamageTaken.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"win\":").Append(p.IsWin ? "true" : "false");
                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>把一段文本按 JSON 字符串规则写进去（**双引号与反斜杠必须转义**）。</summary>
        /// <param name="sb">目标。</param>
        /// <param name="text">文本。</param>
        private static void AppendJsonString(StringBuilder sb, string text)
        {
            sb.Append('"');

            if (!string.IsNullOrEmpty(text))
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];

                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ')
                            {
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                sb.Append(c);
                            }

                            break;
                    }
                }
            }

            sb.Append('"');
        }
    }
}
