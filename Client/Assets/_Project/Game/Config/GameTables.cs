// ============================================================================
//  GameTables —— 「这个游戏需要哪几张配置表」的**唯一清单**
//  项目：3D联网战斗Demo   对应：M2-C1（启动预加载）
//
//  ---------------------------------------------------------------------------
//  为什么要单独一个清单，而不是在启动流程里散着写
//  ---------------------------------------------------------------------------
//  `ConfigMgr` 的设计是"**同步查表 + 启动预加载**"（M1-C4 的三条语义之一）：
//  查表时不碰 IO，代价是**启动时必须有人把表load 进来**。
//  那个"有人"就是启动流程 —— 而它需要知道**到底要几张表**。
//
//  如果这份清单散在启动流程里：
//      · M3 加了 `Dungeon` 表，得记得同时改启动流程（很容易忘，而且**忘了不报错**，
//        直到某个功能第一次用到那张表才炸）
//      · 服务端/工具也想预加载时，只能再抄一遍
//  抽成一份之后，加表改一处，而且**它就是"这个游戏有哪些表"的文档**。
//
//  ⚠️ 名字必须与 `ConfigKit` 产出的表名一致（`Hero` 而不是 `HeroConfig`）——
//     `ConfigMgr` 会按 `<表名>Config` 拼资产名。
// ============================================================================

using System;

namespace NBC.Game.Config
{
    /// <summary>游戏需要的配置表清单与预加载入口。</summary>
    public static class GameTables
    {
        /// <summary>
        /// 全部表的表名（**顺序无所谓**，但保持分组可读）。
        /// <para>⚠️ 新增表时**只改这里**。</para>
        /// </summary>
        public static readonly string[] All =
        {
            // 基础数据（M1 就有）
            "Hero",
            "Skill",
            "Level",

            // M2：战斗与任务
            "Monster",
            "Quest",
            "QuestCondition",
            "Reward",

            // M4-S2：成就（与任务**共用** `QuestCondition` / `Reward`，所以只多这一张表）
            "Achievement"
        };

        // ⚠️ 这里**故意没有** `Dungeon` / `DropTable`：它们是**服务端**读的
        //    （服务端直接读 `Configs\Design\*.csv`，见 `Docs\27` §三）。
        //    客户端拿到的是快照，不需要这两张表 —— 把它们加进来只会让每局多两次无用的资产加载。

        /// <summary>把全部表预加载起来（启动流程调用一次）。</summary>
        /// <param name="onDone">全部成功回调。</param>
        /// <param name="onFailed">任一失败回调（参数是一句人话原因）。</param>
        public static void PreloadAll(Action onDone, Action<string> onFailed)
        {
            ConfigMgr.Instance.PreloadAll(All, onDone, onFailed);
        }
    }
}
