// ============================================================================
//  BattleRecordDao —— 一局战绩的落库（**一次事务**：记录 + 明细 + 档案累计）
//  项目：3D联网战斗Demo   对应：需求文档 SRV-13、§13.4（`battle_record` / `battle_player_detail`）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么必须是**一个事务**
//  ---------------------------------------------------------------------------
//  一局战绩在库里是**三处**改动：
//
//      ① `battle_record`              一条（这一局的汇总）
//      ② `battle_player_detail`       每人一条（`record_id` 外键指向①）
//      ③ `player_profile`             每人一次累计（`total_kill` / `total_death` / `total_win`…）
//
//  只写一半会得到**假数据**，而且看不出来：
//      · 只有①没有②  ⇒ "这一局有人打过，但没人有战绩"
//      · 只有①没有③  ⇒ 战绩查得到，但玩家的累计数**永远不涨**（最难发现的一种：
//                       单看任何一张表都正常）
//  ⇒ 所以三处**同生共死**。（`Docs\00` 那条"半成品状态最难查"在这里的具体形状。）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 房间号：`battle_record.room_id` 是 BIGINT，而运行时房号是 `"r1"` 这样的字符串
//  ---------------------------------------------------------------------------
//  `RoomRegistry` 生成的是 `"r" + 递增号`（见那里的 `_nextRoomNumber`）。
//  两个选择：
//      · 改列成 VARCHAR —— 要迁移 + 要你手工跑脚本
//      · **转换**：`"r12"` → `12`；能解析就用数字
//  ⇒ 选后者（**零手工步骤**），并把**原始字符串**写进 `result_json` 的 `"room"` 字段 ——
//    于是"数字不好读"这件事不影响排查：想知道是哪个房间，看 JSON。
//  ⚠️ 解析不出来时（将来房号方案变了）退化成**稳定哈希**，并在 `result_json` 里留着原文。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 只给"真实账号的档案"写明细（`player_id > 0`）
//  ---------------------------------------------------------------------------
//  `battle_player_detail.player_id` 有外键指向 `player_profile`，所以**能写进去的
//  只有真实档案**。而玩家编号现在有两种（2026-09-27 SRV-17a 起）：
//
//      `player_id > 0`  = **账号自己的档案**（`AccountDirectory` 给的，见 `Docs\27` §十九）
//      `player_id < 0`  = **游客**（负数，见 `ServerMessageRouter.HandleHandshake`）
//
//  ⇒ 本 DAO 的处理是：
//      · **游客**（负数）：**按设计**不写明细与累计（他本来就没有档案）+ **如实报数**
//      · **正数但库里查不到**：这是**数据问题**（账号有 id 却没档案？），单独报数、要响
//  ⇒ 两种"没写进去"的原因**完全不同**，日志里必须分得开 ——
//    否则"游客不记明细"（正常）会把"某人的战绩丢了"（故障）淹掉。
//  ⇒ 历史：这里原来是"第 5 个连上来的人会被外键拒绝"的补丁（`Docs\27` §十四）；
//     SRV-17a 把游客改成负数之后，那段欠账从根上消失了（游客**不可能**撞上档案 id）。
// ============================================================================

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using NBC.Server.Game;

namespace NBC.Server.Data
{
    /// <summary>一次战绩落库的结果（**如实报数**，含跳过了几个）。</summary>
    public readonly struct BattleRecordWriteResult
    {
        /// <summary>新记录的 `record_id`（0 = 没写进去）。</summary>
        public readonly long RecordId;

        /// <summary>写了几条玩家明细。</summary>
        public readonly int DetailRows;

        /// <summary>
        /// 有几个**游客**（`player_id < 0`）没写明细。
        /// <para>⚠️ 这是**设计如此**，不是故障：游客没有档案，就没人可以记（见文件头）。</para>
        /// </summary>
        public readonly int SkippedGuests;

        /// <summary>
        /// 有几个玩家**有正数 id 但库里没有他的档案** —— ⚠️ 这是**数据问题**，该响。
        /// </summary>
        public readonly int SkippedUnknownPlayers;

        /// <summary>造一个。</summary>
        /// <param name="recordId">记录编号。</param>
        /// <param name="detailRows">明细条数。</param>
        /// <param name="skippedGuests">跳过的游客数（设计如此）。</param>
        /// <param name="skippedUnknownPlayers">跳过的"有 id 无档案"玩家数（数据问题）。</param>
        public BattleRecordWriteResult(
            long recordId, int detailRows, int skippedGuests, int skippedUnknownPlayers)
        {
            RecordId = recordId;
            DetailRows = detailRows;
            SkippedGuests = skippedGuests;
            SkippedUnknownPlayers = skippedUnknownPlayers;
        }

        /// <summary>一句人话。</summary>
        /// <returns>例如 `record#12、明细 2 条（游客 1 人不记明细）`。</returns>
        public override string ToString()
        {
            var text = new StringBuilder();

            text.Append("record#").Append(RecordId).Append("、明细 ").Append(DetailRows).Append(" 条");

            if (SkippedGuests > 0)
            {
                text.Append("（游客 ").Append(SkippedGuests).Append(" 人按设计不记明细）");
            }

            if (SkippedUnknownPlayers > 0)
            {
                text.Append("（⚠️ ").Append(SkippedUnknownPlayers).Append(" 个玩家**有 id 却没有档案**）");
            }

            return text.ToString();
        }
    }

    /// <summary>一局战绩的落库（记录 + 明细 + 档案累计，**一次事务**）。</summary>
    public sealed class BattleRecordDao
    {
        /// <summary>连接来源。</summary>
        private readonly DbConnectionFactory m_factory;

        /// <summary>命令超时（秒）。</summary>
        private readonly int m_commandTimeoutSeconds;

        /// <summary>造一个 DAO。</summary>
        /// <param name="factory">连接工厂（不能为 null）。</param>
        public BattleRecordDao(DbConnectionFactory factory)
        {
            if (factory == null)
            {
                throw new System.ArgumentNullException(nameof(factory), "[BattleRecordDao] 连接工厂是 null。");
            }

            m_factory = factory;
            m_commandTimeoutSeconds = factory.Options.CommandTimeoutSeconds;
        }

        /// <summary>
        /// 把一局战绩写进库（**一个事务**：记录 + 明细 + 档案累计）。
        /// <para>⚠️ 这是**异步**的，而且调用方（Tick 线程）**绝不能等它** ——
        /// 排队的活归 `BattleRecordWriter`。</para>
        /// </summary>
        /// <param name="draft">战绩草稿。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>落库结果（含跳过了几个无档案玩家）。</returns>
        public async Task<BattleRecordWriteResult> SaveAsync(
            BattleRecordDraft draft, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (draft == null)
            {
                throw new System.ArgumentNullException(nameof(draft));
            }

            using (MySqlConnection connection = await m_factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            using (MySqlTransaction transaction = await connection
                       .BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    // ---- ① 先问库里有哪些 player_id（决定哪些明细写得进去，见文件头）----
                    List<long> allowed = await LoadExistingProfileIdsAsync(
                        connection, transaction, draft, cancellationToken).ConfigureAwait(false);

                    // ---- ② battle_record ----
                    const string insertRecord =
                        "INSERT INTO `battle_record` " +
                        "(`room_id`, `game_mode`, `sync_mode`, `map_id`, `random_seed`, " +
                        " `start_tick`, `end_tick`, `duration_ms`, `result_json`) " +
                        "VALUES (@RoomId, @GameMode, @SyncMode, @MapId, @RandomSeed, " +
                        "        @StartTick, @EndTick, @DurationMs, @ResultJson); " +
                        "SELECT LAST_INSERT_ID();";

                    long recordId = await connection.ExecuteScalarAsync<long>(
                        new CommandDefinition(insertRecord, new
                        {
                            RoomId = ToNumericRoomId(draft.RoomId),
                            GameMode = BattleRecordDraft.GameModePve,
                            SyncMode = draft.SyncModeCode,
                            MapId = draft.MapId,
                            RandomSeed = draft.RandomSeed,
                            StartTick = draft.StartTick,
                            EndTick = draft.EndTick,
                            DurationMs = draft.DurationMs,
                            ResultJson = draft.ToResultJson(),
                        },
                        transaction, commandTimeout: m_commandTimeoutSeconds,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);

                    // ---- ③ battle_player_detail（**只给真实账号的档案**，见文件头）----
                    int detailRows = 0;
                    int skippedGuests = 0;
                    int skippedUnknown = 0;

                    for (int i = 0; i < draft.Players.Count; i++)
                    {
                        BattlePlayerDraft p = draft.Players[i];

                        // ⚠️ 先按**约定**分流，再按**库里的真值**判断 —— 两种"没写进去"的原因
                        //    完全不同（一个是设计，一个是故障），日志里必须分得开：
                        //      ① `player_id < 0`  ⇒ **游客**（设计如此：没有档案就没人可记）
                        //      ② `player_id > 0` 但库里查不到 ⇒ **数据问题**（该响）
                        if (p.PlayerId < 0)
                        {
                            skippedGuests++;
                            continue;
                        }

                        if (!allowed.Contains(p.PlayerId))
                        {
                            skippedUnknown++;
                            continue;
                        }

                        const string insertDetail =
                            "INSERT INTO `battle_player_detail` " +
                            "(`record_id`, `player_id`, `hero_id`, `team`, `kill`, `death`, `assist`, " +
                            " `damage_dealt`, `damage_taken`, `heal`, `is_win`, `is_ai`) " +
                            "VALUES (@RecordId, @PlayerId, @HeroId, @Team, @Kill, @Death, @Assist, " +
                            "        @DamageDealt, @DamageTaken, @Heal, @IsWin, @IsAi)";

                        detailRows += await connection.ExecuteAsync(
                            new CommandDefinition(insertDetail, new
                            {
                                RecordId = recordId,
                                PlayerId = p.PlayerId,
                                HeroId = p.HeroConfigId,
                                Team = p.Team,
                                Kill = p.Kill,
                                Death = p.Death,
                                Assist = p.Assist,
                                DamageDealt = p.DamageDealt,
                                DamageTaken = p.DamageTaken,
                                Heal = p.Heal,
                                IsWin = p.IsWin ? 1 : 0,
                                IsAi = p.IsAi ? 1 : 0,
                            },
                            transaction, commandTimeout: m_commandTimeoutSeconds,
                            cancellationToken: cancellationToken)).ConfigureAwait(false);

                        // ---- ④ 档案累计（SRV-13 的后半句"更新玩家经验/金币/胜场"）----
                        // ⚠️ 用 `x = x + @delta`（**在库里加**）而不是"读出来、加好、写回去"：
                        //    后者在并发下会丢更新，而且要多一次往返。
                        // ⚠️ 经验/金币**这里不加**：本项目还没有经验/金币的产出规则
                        //    （那是任务奖励的事，走 `reward_granted` 台账那条路）。
                        //    只加"能确定的"：击杀 / 死亡 / 胜场 / 败场。
                        const string addTotals =
                            "UPDATE `player_profile` SET " +
                            "  `total_kill`  = `total_kill`  + @Kill, " +
                            "  `total_death` = `total_death` + @Death, " +
                            "  `total_win`   = `total_win`   + @Win, " +
                            "  `total_lose`  = `total_lose`  + @Lose " +
                            "WHERE `player_id` = @PlayerId";

                        await connection.ExecuteAsync(
                            new CommandDefinition(addTotals, new
                            {
                                Kill = p.Kill,
                                Death = p.Death,
                                Win = p.IsWin ? 1 : 0,
                                Lose = p.IsWin ? 0 : 1,
                                PlayerId = p.PlayerId,
                            },
                            transaction, commandTimeout: m_commandTimeoutSeconds,
                            cancellationToken: cancellationToken)).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new BattleRecordWriteResult(recordId, detailRows, skippedGuests, skippedUnknown);
                }
                catch
                {
                    // ⚠️ 回滚：**三处改动同生共死**（见文件头）。不回滚会留下
                    //    "有记录没明细"或"有明细没累计"的半成品。
                    try
                    {
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 回滚都失败了：连接层面已经不干净，交给 Dispose 收尾。
                        // ⚠️ **不吞掉原始异常**（下面 `throw;` 抛的是它）。
                    }

                    throw;
                }
            }
        }

        /// <summary>问库里有哪些 `player_profile.player_id`（只查草稿里出现过的那些）。</summary>
        /// <param name="connection">连接。</param>
        /// <param name="transaction">事务。</param>
        /// <param name="draft">草稿。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>存在的玩家编号。</returns>
        private async Task<List<long>> LoadExistingProfileIdsAsync(
            MySqlConnection connection, MySqlTransaction transaction,
            BattleRecordDraft draft, CancellationToken cancellationToken)
        {
            var result = new List<long>();

            if (draft.Players.Count == 0)
            {
                return result;
            }

            var sql = new StringBuilder("SELECT `player_id` FROM `player_profile` WHERE `player_id` IN (");
            var parameters = new DynamicParameters();

            for (int i = 0; i < draft.Players.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                string name = "p" + i;
                sql.Append('@').Append(name);
                parameters.Add(name, draft.Players[i].PlayerId);
            }

            sql.Append(')');

            IEnumerable<long> ids = await connection.QueryAsync<long>(
                new CommandDefinition(sql.ToString(), parameters, transaction,
                                      commandTimeout: m_commandTimeoutSeconds,
                                      cancellationToken: cancellationToken)).ConfigureAwait(false);

            result.AddRange(ids);
            return result;
        }

        /// <summary>
        /// 房号字符串 → `room_id` 那一列的数字（见文件头）。
        /// <para>`"r12"` → `12`；`"12"` → `12`；其余退化成**稳定哈希**（FNV-1a）。</para>
        /// </summary>
        /// <param name="roomId">房号。</param>
        /// <returns>数字房号。</returns>
        public static long ToNumericRoomId(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                return 0;
            }

            // `r12` / `R12` → 12
            int start = 0;

            if ((roomId[0] == 'r' || roomId[0] == 'R') && roomId.Length > 1)
            {
                start = 1;
            }

            long parsed;

            if (long.TryParse(roomId.Substring(start), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            // ⚠️ 退化路径：**稳定**（同样的字符串永远得到同一个数），但**不可逆**。
            //    所以原始房号还会被写进 `result_json`（见文件头）。
            const uint offset = 2166136261;
            const uint prime = 16777619;
            uint hash = offset;

            for (int i = 0; i < roomId.Length; i++)
            {
                hash ^= roomId[i];
                hash *= prime;
            }

            return hash;
        }
    }
}
