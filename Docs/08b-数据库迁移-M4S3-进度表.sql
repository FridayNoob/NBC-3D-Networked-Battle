-- ============================================================================
--  NBC 数据库迁移：M4-S3 进度持久化（新增 2 张表）
--  项目：3D联网战斗Demo
--  对应需求文档：Docs/01-项目需求文档.md §13.4 / DB-01
--  生成日期：2026-09-26
--
--  ---------------------------------------------------------------------------
--  ⚠️ 为什么单独一个文件，而不是让你重跑 Docs\08-数据库脚本.sql
--  ---------------------------------------------------------------------------
--  `08-数据库脚本.sql` 第 38 行是 `DROP DATABASE IF EXISTS nbc_db;`
--  —— 它是**初始化脚本**（先删再建），跑一次就把你的数据全清了。
--  本次只是**加两张表**，所以给一个**只增不删、可重复执行**的迁移脚本。
--
--  什么时候用哪个：
--    · 全新机器 / 想清库重来  → 跑 `08-数据库脚本.sql`（已经包含这两张表）
--    · 已经建过库、只想加表  → 跑**本文件**（幂等，跑几次都行）
--
--  执行方式（**在 cmd 里**，不要在 PowerShell 里；mysql 打不开非 ASCII 路径，
--  但 cmd 的重定向可以 —— 见 `08` 文件头那段 `error: 42` 的说明）：
--
--    cd /d "E:\U3D Projects\0_MyFile\3D联网战斗Demo"
--    mysql -u root -p --default-character-set=utf8mb4 < "Docs\08b-数据库迁移-M4S3-进度表.sql"
--
--  验证（应看到 6 张表）：
--    mysql -u root -p nbc_db -e "SHOW TABLES;"
--
--  文件本身：UTF-8 **无 BOM**、LF 换行 —— 有 BOM 会让第一条语句报 1064，别加。
--
--  ⚠️ 本脚本用 `CREATE TABLE IF NOT EXISTS`：
--     它**只保证"表在"，不保证"表结构对"**。如果这两张表已经存在但字段是旧的，
--     本脚本会安静地什么都不做 —— 那种情况要手工 `ALTER` 或 `DROP` 后重建。
--     这是"幂等迁移"的通用取舍，写在这里免得下次误判成"脚本没生效"。
-- ============================================================================

USE `nbc_db`;

-- ---------------------------------------------------------------------------
-- 1. 条件进度表：任务/成就的**跨局累计**进度
--
--    ⚠️ 主键是「玩家 + 条件」而不是「玩家 + 任务」：
--       任务与成就**共用同一套条件系统**（`ConditionTracker`），
--       而 `IConditionProgressStore` 说话的单位就是**条件编号** ——
--       所以一张表同时承载任务与成就，将来加别的"条件消费者"也不用再建表。
--
--    ⚠️ 存**绝对值**而不是增量：落库因此是**幂等**的
--       （`INSERT ... ON DUPLICATE KEY UPDATE` 重发一次不会把进度算两遍）。
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `condition_progress` (
  `player_id`     BIGINT UNSIGNED NOT NULL,
  `condition_key` INT             NOT NULL COMMENT '对应 Config_QuestCondition.id',
  `progress`      INT             NOT NULL DEFAULT 0 COMMENT '已累计数量（绝对值）',
  `updated_at`    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`player_id`, `condition_key`),
  KEY `idx_updated` (`updated_at`) COMMENT '清理长期不活跃的进度用',
  CONSTRAINT `fk_progress_player` FOREIGN KEY (`player_id`)
      REFERENCES `player_profile` (`player_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='条件进度表（任务/成就共用，跨局累计）';

-- ---------------------------------------------------------------------------
-- 2. 已发奖励台账：防"服务端一重启就把成就奖励再发一遍"
--
--    ⚠️ 这张表是**加了持久化之后才暴露出来的一个 bug 的解药**：
--       `AchievementRuntime` 用 `resetProgress: false` 登记条件，而条件系统的语义是
--       "**注册时若已达成，当场回调一次**"（这正是"登录即解锁"要的行为）。
--       以前进度存内存、重启就没了，所以永远不会重复解锁；
--       **一旦进度落库，每次重启都会把所有已完成的成就再发一遍奖。**
--
--    ⇒ "解锁状态"能从进度表推导，但"**奖励发过没有**"推导不出来，必须记账。
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `reward_granted` (
  `player_id`   BIGINT UNSIGNED NOT NULL,
  `owner_kind`  TINYINT         NOT NULL COMMENT '1=任务 2=成就',
  `owner_id`    INT             NOT NULL COMMENT '对应 Config_Quest.id / Config_Achievement.id',
  `reward_id`   INT             NOT NULL COMMENT '对应 Config_Reward.id',
  `granted_at`  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`player_id`, `owner_kind`, `owner_id`),
  KEY `idx_granted_at` (`granted_at`),
  CONSTRAINT `fk_granted_player` FOREIGN KEY (`player_id`)
      REFERENCES `player_profile` (`player_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='已发奖励台账（防重启重复发奖）';

-- ============================================================================
--  自检：把加完之后的表列出来（跑完应当看到 6 张表）
-- ============================================================================
SHOW TABLES;