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
  `password_hash` CHAR(64)        NOT NULL COMMENT 'SHA256(salt+password) 的十六进制小写',
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

-- ============================================================================
--  初始数据：测试账号
--
--  密码统一为 123456。这里直接写入预先算好的 SHA256(salt + password)，
--  避免依赖 MySQL 是否支持 SHA2 函数。
--
--  ⚠️ 服务端实现登录时，必须使用完全相同的拼接方式：
--         hash = SHA256( salt + 明文密码 )  → 转小写十六进制字符串（64 字符）
--     否则测试账号会登录失败。
-- ============================================================================

INSERT INTO `account` (`username`, `password_hash`, `salt`) VALUES
  ('test01', '6a94b5966341a30b882b2970228b03a0d3113f4abb4d2ca169979680d908292c', '6f1a2b3c4d5e6f70'),
  ('test02', 'cedc18947cb793276b418f94c6a205b7455925651de0850292f5cbce632f3f40', '7a2b3c4d5e6f7081'),
  ('test03', '0733ea2d549319104036f01cc2204dd7d083cacbb890790e47dc7454a93b91a6', '8b3c4d5e6f708192'),
  ('test04', '2560cab1006a2eeec482e728065549b06cf8ef6fe167e9042eeb5524fa2ed44b', '9c4d5e6f708192a3');

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

-- 4 张表是否都在
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
