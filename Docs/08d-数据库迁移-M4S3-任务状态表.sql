-- ============================================================================
--  NBC 数据库迁移：M4-S3 任务权威化（新增 1 张表：`quest_state`）
--  项目：3D联网战斗Demo
--  对应需求文档：Docs/01-项目需求文档.md §13.4 / DB-01
--  对应切片：`Docs\27-M4开工清单.md` §二十五（§21.4 未做#3 的第一刀）
--  生成日期：2026-09-27
--
--  ---------------------------------------------------------------------------
--  ⚠️ 为什么单独一个文件，而不是让你重跑 Docs\08-数据库脚本.sql
--  ---------------------------------------------------------------------------
--  `08-数据库脚本.sql` 第 38 行是 `DROP DATABASE IF EXISTS nbc_db;`
--  —— 它是**初始化脚本**（先删再建），跑一次就把你的数据全清了。
--  本次只是**加一张表**，所以给一个**只增不删、可重复执行**的迁移脚本。
--
--  什么时候用哪个（与 `08b` / `08c` 同一套分工）：
--    · 全新机器 / 想清库重来  → 跑 `08-数据库脚本.sql`（已经包含本表）
--    · 已经建过库、只想加表  → 跑**本文件**（幂等，跑几次都行）
--
--  执行方式（**在 cmd 里**，不要在 PowerShell 里；mysql 打不开非 ASCII 路径，
--  但 cmd 的重定向可以 —— 见 `08` 文件头那段 `error: 42` 的说明）：
--
--    cd /d "E:\U3D Projects\0_MyFile\3D联网战斗Demo"
--    mysql -u root -p --default-character-set=utf8mb4 < "Docs\08d-数据库迁移-M4S3-任务状态表.sql"
--
--  验证（应看到 7 张表）：
--    mysql -u root -p nbc_db -e "SHOW TABLES;"
--
--  文件本身：UTF-8 **无 BOM**、LF 换行 —— 有 BOM 会让第一条语句报 1064，别加。
--
--  ⚠️ 本脚本用 `CREATE TABLE IF NOT EXISTS`：
--     它**只保证"表在"，不保证"表结构对"**。如果表已经存在但字段是旧的，
--     本脚本会安静地什么都不做 —— 那种情况要手工 `ALTER` 或 `DROP` 后重建。
--     这是"幂等迁移"的通用取舍，写在这里免得下次误判成"脚本没生效"。
-- ============================================================================

USE `nbc_db`;

-- ---------------------------------------------------------------------------
-- 1. 任务状态表：一个玩家**接过哪些任务、到哪一步了**
--
--    ⚠️ 为什么必须落库（这一刀的全部理由）：
--       在此之前"接取/交付"只活在**客户端内存**里 —— 服务端根本不知道玩家接过什么，
--       于是任务进度**没有权威值**可发（`Docs\27` §21.4 未做#3）。
--       成就没有"接取"这个动作，所以它先做完了（§二十一）；任务有状态机，所以留到了这一刀。
--
--    ⚠️ 存**状态**而不是"打过几个怪"：
--       次数那件事由 `condition_progress` 负责（同一套条件系统，主键是「玩家 + 条件」）。
--       这张表只管**状态机**：未接 / 已接 / 已完成（可交付）/ 已交付。
--       两张表各管一件事，于是"进度"和"到哪一步了"不会互相覆盖。
--
--    ⚠️ 状态用**数字**而不是 ENUM：
--       与 `ERewardOwnerKind` / `ERewardGrantedKind` 那些列同一个套路 ——
--       枚举名会改，数字不会；改数字是**不兼容改动**，要在代码里显式说。
--       取值与 `EQuestState` 一一对应（见 `QuestTypes.cs`）：
--         1 = Accepted   2 = Completed   3 = Submitted
--       （**故意没有 0**：0 = "表里没这一行" = 未接取，
--         把"没有记录"和"有记录且是某个状态"混成一个值，是这类表最常见的坑）
--
--    ⚠️ 主键「玩家 + 任务」⇒ `INSERT ... ON DUPLICATE KEY UPDATE` 天然**幂等**：
--       重复交付不会写出第二行（配合 `reward_granted` 的台账，两道保险）。
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `quest_state` (
  `player_id`    BIGINT UNSIGNED NOT NULL,
  `quest_id`     INT             NOT NULL COMMENT '对应 Config_Quest.id',
  `state`        TINYINT         NOT NULL COMMENT '1=Accepted 2=Completed 3=Submitted；0 不存（没有行=未接取）',
  `accepted_at`  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `submitted_at` DATETIME        NULL     DEFAULT NULL COMMENT '交付时间；未交付为 NULL',
  `updated_at`   DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`player_id`, `quest_id`),
  KEY `idx_state` (`state`),
  CONSTRAINT `fk_quest_state_player` FOREIGN KEY (`player_id`)
      REFERENCES `player_profile` (`player_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='任务状态机（接取/完成/交付）';

-- ---------------------------------------------------------------------------
-- 自检：把加完之后的表列出来（跑完应当看到 7 张表）
-- ---------------------------------------------------------------------------
SHOW TABLES;
