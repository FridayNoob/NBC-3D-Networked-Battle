-- ============================================================================
--  NBC（Networked Battle Combat）数据库初始化脚本
--  项目：3D联网战斗Demo
--  对应需求文档：Docs/01-项目需求文档.md §13.4
--  数据库：MySQL 8.0  字符集：utf8mb4  引擎：InnoDB
--  生成日期：2026-09-16
--
--  执行方式：
--
--   ✅ 方式 A（推荐）：用 cmd 的输入重定向，【在 cmd 里执行，不要在 PowerShell 里】
--        cd /d "E:\U3D Projects\0_MyFile\3D联网战斗Demo"
--        mysql -u root -p --default-character-set=utf8mb4 < "Docs\08-数据库脚本.sql"
--      或在任意目录用完整路径：
--        mysql -u root -p --default-character-set=utf8mb4 < "E:\U3D Projects\0_MyFile\3D联网战斗Demo\Docs\08-数据库脚本.sql"
--
--   ❌ 不要用 `source` 直接喂这个路径：
--        mysql> source E:/U3D Projects/0_MyFile/3D联网战斗Demo/Docs/08-数据库脚本.sql
--        → ERROR: Failed to open file '...', error: 42
--      原因：`error: 42` 在 Windows CRT 里是 EILSEQ（非法字节序列），即
--            **mysql.exe 自己打不开含非 ASCII 字符的路径**（路径编码转换失败）。
--            与脚本内容无关 —— cmd 能打开同一路径（已实测），只有 mysql 的 fopen 不行。
--      若确实想用 source：先把脚本复制到**纯 ASCII 路径**（目录名和文件名都要 ASCII），例如
--        mkdir C:\nbc && copy "Docs\08-数据库脚本.sql" C:\nbc\nbc_db.sql
--        mysql> source C:/nbc/nbc_db.sql
--      （缺点：副本会与 Docs 下的原文件脱节，所以优先用方式 A）
--
--  说明：
--   · 本脚本可重复执行（先 DROP 再 CREATE），便于开发期反复重建
--   · ⚠️ 执行会清空 nbc_db 下的既有数据，请确认无重要数据后再跑
--   · 测试账号密码：所有测试账号的明文密码都是 123456
--     （存储的是 SHA256(salt + password) 的十六进制，不是明文）
--   · 文件本身：UTF-8 **无 BOM**、LF 换行 —— 有 BOM 会让第一条语句报 1064，别加
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 0. 建库
-- ---------------------------------------------------------------------------
DROP DATABASE IF EXISTS `nbc_db`;
CREATE DATABASE `nbc_db`
    DEFAULT CHARACTER SET utf8mb4
    DEFAULT COLLATE utf8mb4_general_ci;
USE `nbc_db`;

-- ---------------------------------------------------------------------------
-- 1. 账号表
-- ---------------------------------------------------------------------------
CREATE TABLE `account` (
  `account_id`    BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `username`      VARCHAR(32)     NOT NULL COMMENT '登录名',
  `password_hash` CHAR(64)        NOT NULL COMMENT 'SHA256(salt + SHA256(密码)) 的十六进制小写（配方见本文件末尾的种子数据说明）',
  `salt`          CHAR(16)        NOT NULL COMMENT '每账号独立盐值',
  `created_at`    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `last_login_at` DATETIME        NULL,
  PRIMARY KEY (`account_id`),
  UNIQUE KEY `uk_username` (`username`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='账号表';

-- ---------------------------------------------------------------------------
-- 2. 玩家档案表（与账号 1:1）
-- ---------------------------------------------------------------------------
CREATE TABLE `player_profile` (
  `player_id`     BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `account_id`    BIGINT UNSIGNED NOT NULL,
  `nickname`      VARCHAR(32)     NOT NULL,
  `level`         INT             NOT NULL DEFAULT 1,
  `exp`           BIGINT          NOT NULL DEFAULT 0,
  `gold`          BIGINT          NOT NULL DEFAULT 0,
  `total_kill`    INT             NOT NULL DEFAULT 0,
  `total_death`   INT             NOT NULL DEFAULT 0,
  `total_win`     INT             NOT NULL DEFAULT 0,
  `total_lose`    INT             NOT NULL DEFAULT 0,
  `selected_hero` INT             NOT NULL DEFAULT 1001 COMMENT '对应 Config_Hero.id',
  `updated_at`    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`player_id`),
  UNIQUE KEY `uk_account` (`account_id`),
  KEY `idx_gold` (`gold`) COMMENT '排行榜查询用',
  CONSTRAINT `fk_profile_account` FOREIGN KEY (`account_id`)
      REFERENCES `account` (`account_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='玩家档案表';

-- ---------------------------------------------------------------------------
-- 3. 战斗记录表（一局一条）
-- ---------------------------------------------------------------------------
CREATE TABLE `battle_record` (
  `record_id`   BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `room_id`     BIGINT UNSIGNED NOT NULL,
  `game_mode`   TINYINT         NOT NULL COMMENT '1=PVE 2=PVP',
  `sync_mode`   TINYINT         NOT NULL COMMENT '1=State 2=LockStep 3=Hybrid',
  `map_id`      INT             NOT NULL,
  `random_seed` INT             NOT NULL COMMENT '帧同步复现用随机种子',
  `start_tick`  BIGINT          NOT NULL,
  `end_tick`    BIGINT          NOT NULL,
  `duration_ms` INT             NOT NULL,
  `result_json` JSON            NOT NULL COMMENT '各玩家击杀/死亡/伤害/治疗汇总',
  `created_at`  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`record_id`),
  KEY `idx_created` (`created_at`),
  KEY `idx_room` (`room_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='战斗记录表';

-- ---------------------------------------------------------------------------
-- 4. 战斗玩家明细表（一人一行，便于统计与排行）
-- ---------------------------------------------------------------------------
CREATE TABLE `battle_player_detail` (
  `detail_id`    BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `record_id`    BIGINT UNSIGNED NOT NULL,
  `player_id`    BIGINT UNSIGNED NOT NULL,
  `hero_id`      INT             NOT NULL,
  `team`         TINYINT         NOT NULL DEFAULT 0,
  `kill`         INT             NOT NULL DEFAULT 0,
  `death`        INT             NOT NULL DEFAULT 0,
  `assist`       INT             NOT NULL DEFAULT 0,
  `damage_dealt` BIGINT          NOT NULL DEFAULT 0,
  `damage_taken` BIGINT          NOT NULL DEFAULT 0,
  `heal`         BIGINT          NOT NULL DEFAULT 0,
  `is_win`       TINYINT         NOT NULL DEFAULT 0,
  `is_ai`        TINYINT         NOT NULL DEFAULT 0,
  PRIMARY KEY (`detail_id`),
  KEY `idx_player` (`player_id`, `detail_id`) COMMENT '查某玩家最近战绩用',
  KEY `idx_record` (`record_id`),
  CONSTRAINT `fk_detail_record` FOREIGN KEY (`record_id`)
      REFERENCES `battle_record` (`record_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='战斗玩家明细表';

-- ---------------------------------------------------------------------------
-- 5. 条件进度表（M4-S3 新增：任务/成就的**跨局累计**进度）
--
--    ⚠️ 为什么主键是「玩家 + 条件」而不是「玩家 + 任务」：
--       任务和成就**共用同一套条件系统**（`ConditionTracker`），
--       而 `IConditionProgressStore` 说话的单位就是**条件编号** ——
--       所以一张表就能同时承载任务与成就的进度，将来加别的"条件消费者"也不用再建表。
--
--    ⚠️ 为什么存**绝对值**而不是增量：
--       落库是"重试安全的"（`INSERT ... ON DUPLICATE KEY UPDATE` 幂等）——
--       网络抖一下重发一次不会把进度算两遍。存增量就必须再有一张"已应用"表。
--
--    ⚠️ 成就的"解锁状态"**不单独存**：它可以从本表推导（该成就全部条件都 >= 需求）。
--       但"**奖励有没有发过**"推导不出来 —— 所以另见第 6 张表。
-- ---------------------------------------------------------------------------
CREATE TABLE `condition_progress` (
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
-- 6. 已发奖励台账（M4-S3 新增）
--
--    ⚠️ 这张表是**加了持久化之后才暴露出来的一个 bug 的解药**（本片最值钱的一条）：
--
--       `AchievementRuntime` 用 `resetProgress: false` 登记条件，而 `ConditionTracker`
--       的语义②是"**注册时若已达成，当场回调一次**"（这是"登录即解锁"要的行为）。
--       以前进度存在内存里、进程一重启就没了，所以永远不会重复解锁；
--       **一旦进度落库，服务端每重启一次就会把所有已完成的成就再发一遍奖。**
--
--       ⇒ "解锁状态"可以从第 5 张表推导，但"**奖励发过没有**"推导不出来，必须记账。
--         台账与进度表**分开**的理由：进度是"玩家的数据"，台账是"系统的承诺"
--         （发过就不能再发）—— 两者生命周期与语义都不同。
-- ---------------------------------------------------------------------------
CREATE TABLE `reward_granted` (
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

-- ---------------------------------------------------------------------------
-- 7. 任务状态表：一个玩家**接过哪些任务、到哪一步了**（M4-S3 §二十五）
--
--    ⚠️ 为什么必须落库：在此之前"接取/交付"只活在**客户端内存**里，
--       服务端根本不知道玩家接过什么 ⇒ 任务进度**没有权威值**可发。
--       成就没有"接取"这个动作，所以它先做完了（§二十一）；任务有状态机，留到了这一刀。
--
--    ⚠️ 与第 5 张表（`condition_progress`）的分工：
--       次数那件事归 `condition_progress`（主键「玩家 + 条件」）；
--       这张表只管**状态机**：1=Accepted 2=Completed 3=Submitted。
--       **故意没有 0**：0 = "表里没这一行" = 未接取 ——
--       把"没有记录"和一个具体状态混成一个值，是这类表最常见的坑。
--
--    ⚠️ 主键「玩家 + 任务」⇒ `ON DUPLICATE KEY UPDATE` 天然幂等：
--       重复交付不会写出第二行（再配合 `reward_granted` 台账，两道保险）。
--
--    📌 幂等迁移版（已建过库、只想加这张表）：`Docs\08d-数据库迁移-M4S3-任务状态表.sql`
-- ---------------------------------------------------------------------------
CREATE TABLE `quest_state` (
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
-- ============================================================================
--  初始数据：测试账号
--
--  密码统一为 123456。这里直接写入预先算好的摘要，避免依赖 MySQL 是否支持 SHA2 函数。
--
--  ⚠️⚠️ **配方在 M4-S3（2026-09-27）改过一次**，别照抄旧文档：
--        旧（已作废）：hash = SHA256( salt + 明文密码 )
--        新（当前）  ：hash = SHA256( salt + SHA256(明文密码) )
--                      其中 SHA256(明文密码) 就是**客户端发上来的那个摘要**
--                      （`NBC.Shared.Auth.PasswordDigest.FromPassword`）。
--
--  为什么改：**明文密码不再上线**（客户端先做一次摘要）。服务端再把
--  这个摘要叠上 salt 入库 ⇒ 库里存的依然不是"能直接拿去登录的东西"。
--  ⚠️ 代价（如实记录）：线上那个摘要是**未加盐**的，弱密码仍可能被彩虹表反查，
--     而且没有 TLS ⇒ 抓到摘要就能重放。真正的做法是 TLS + 挑战应答 + 慢哈希，
--     **本项目没做到那一步**，理由见 `Docs\27` §19.2。
--
--  ⚠️ 服务端实现登录时，必须使用与上表完全相同的配方（**唯一实现在共享层**：
--     `Client\Assets\_Project\Shared\Auth\PasswordDigest.cs`，两端编同一份源码）。
--     `Server\_db-probe` 的【八】里有一条**阳性对照**专门与下面这 4 行对账 ——
--     配方一旦改错，它会红，而不是让测试账号"莫名其妙登不上"。
-- ============================================================================

INSERT INTO `account` (`username`, `password_hash`, `salt`) VALUES
  ('test01', 'c4824c4da76bcaf8c29971c8750bced3f13ea8fbd2eedd3d79c9830aaa56abc8', '6f1a2b3c4d5e6f70'),
  ('test02', '7b4a038dd8f330b253812717d9924ffbacbda23cb83b1749b1f6e1966e3115c4', '7a2b3c4d5e6f7081'),
  ('test03', 'cc8f69f2861d3ac681fc8bcab8450e0ff606476f2e724f9920fc029f1f223dd1', '8b3c4d5e6f708192'),
  ('test04', '3d5bdce083bcd09ac0c58c44f41103d7471b90cd11bae8346987e6b162ed5a8e', '9c4d5e6f708192a3');

INSERT INTO `player_profile`
  (`account_id`, `nickname`, `level`, `exp`, `gold`, `total_kill`, `total_death`, `total_win`, `total_lose`, `selected_hero`)
VALUES
  (1, '测试玩家一', 5, 1200, 3000, 12, 7, 4, 3, 1001),
  (2, '测试玩家二', 3, 600,  1500, 5,  9, 1, 5, 1002),
  (3, '测试玩家三', 8, 3400, 7200, 25, 14, 9, 6, 1003),
  (4, '测试玩家四', 1, 0,    200,  0,  2, 0, 2, 1001);

-- ============================================================================
--  验证查询（执行后人工确认结果）
-- ============================================================================

-- 7 张表是否都在（account / player_profile / battle_record / battle_player_detail
--                / condition_progress / reward_granted / quest_state）
SELECT TABLE_NAME, TABLE_COMMENT
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = 'nbc_db'
ORDER BY TABLE_NAME;

-- 测试账号是否可查（预期 4 行）
SELECT a.account_id, a.username, p.nickname, p.level, p.gold, p.selected_hero
FROM `account` a
JOIN `player_profile` p ON p.account_id = a.account_id
ORDER BY a.account_id;

-- ============================================================================
--  需求文档 §13.4 要求的 3 个"面试展示用"查询
--  （此处仅列出，供后续写入服务端 DAO / 演示时使用）
-- ============================================================================

-- ① 玩家胜率排行（Top 20）：聚合 + 索引
-- SELECT p.nickname, p.total_win, p.total_lose,
--        ROUND(p.total_win / GREATEST(p.total_win + p.total_lose, 1) * 100, 1) AS win_rate
-- FROM player_profile p
-- WHERE p.total_win + p.total_lose >= 3
-- ORDER BY win_rate DESC, p.total_win DESC
-- LIMIT 20;

-- ② 某玩家最近 10 局：JOIN + 排序分页
-- SELECT b.record_id, b.game_mode, b.duration_ms, d.hero_id,
--        d.kill, d.death, d.damage_dealt, d.damage_taken, d.is_win
-- FROM battle_player_detail d
-- JOIN battle_record b ON b.record_id = d.record_id
-- WHERE d.player_id = 1
-- ORDER BY d.detail_id DESC
-- LIMIT 10;

-- ③ 各英雄出场率与胜率：双表关联统计
-- SELECT d.hero_id, COUNT(*) AS games, SUM(d.is_win) AS wins,
--        ROUND(AVG(d.damage_dealt)) AS avg_damage
-- FROM battle_player_detail d
-- WHERE d.is_ai = 0
-- GROUP BY d.hero_id
-- ORDER BY games DESC;

-- ============================================================================
--  注意事项
-- ============================================================================
-- 1. password_hash 已是【真实可用】的哈希，明文密码统一为 123456。
--    生成方式：hash = SHA256( salt + 明文密码 ) 的小写十六进制（64 字符）。
--    ⚠️ 服务端实现登录时必须使用完全相同的拼接顺序（salt 在前），否则测试账号登不上。
-- 2. 若要新增测试账号，按同样方式算哈希，或直接用服务端的注册接口创建。
-- 2. 开发期可反复执行本脚本重建库（会清空数据）。
-- 3. result_json 使用 MySQL 8.0 的 JSON 类型；若你的服务端选择 5.7 需改为 TEXT。
