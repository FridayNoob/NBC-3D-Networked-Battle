// 这个文件里有：
//   GameTables（静态类）：客户端要预加载的配置表清单，以及按清单一次性预加载的入口

using System;

namespace NBC.Game.Config
{
    // 主要功能：维护客户端配置表的唯一清单（加表只改这里，散回启动流程会漏改且不报错），并按清单一次性预加载。
    // 主要数据成员：All（表名数组，元素是不带 Config 后缀的裸表名）。
    // 主要外部方法：PreloadAll（转发给 ConfigMgr 做逐张串行加载）。
    // 谁在用它：Boot 的 M2DemoBehaviour 启动装配时调用；Editor 的 NetDebugWindow 设置好编辑器来源后调用。
    /// <summary>客户端配置表清单与预加载入口，跨程序集供 Boot / Editor 调用。</summary>
    public static class GameTables
    {
        // 表名清单，元素是不带 Config 后缀的裸表名（如 Hero）；顺序无所谓，只影响加载顺序。
        // 注意：表名必须与 ConfigKit 产出的表名一致 —— ConfigMgr 按「表名 + Config」拼资产名，写成 HeroConfig 会找不到。
        /// <summary>客户端要预加载的全部表名，新增表只改这里。</summary>
        public static readonly string[] All =
        {
            // 基础数据
            "Hero",
            "Skill",
            "Level",

            // 战斗与任务
            "Monster",
            "Quest",
            "QuestCondition",
            "Reward",

            // 成就：与任务共用 QuestCondition / Reward，所以只多这一张表
            "Achievement"
        };

        // 注意：故意没有 Dungeon / DropTable —— 它们只由服务端直读 Configs\Design\*.csv，客户端拿的是快照；加进来只会每局多两次无用的资产加载。

        /// <summary>把清单里的表交给 ConfigMgr 逐张串行预加载，全部成功才回调 onDone，启动流程调用一次。</summary>
        public static void PreloadAll(Action onDone, Action<string> onFailed)
        {
            // 功能：按 All 预加载全部配置表，任一失败就停并报出原因。
            // 入参：onDone 全部成功时回调，无参；onFailed 任一张失败时回调，参数是一句人话原因。
            // 步骤：
            //   1) 直接把 All 交给 ConfigMgr.PreloadAll —— 表清单只在这里维护，本层不重复实现逐张加载与失败即停
            //   2) 两个回调原样透传，不在本层改写错误文案，失败原因只有 ConfigMgr 一处来源
            // 返回：无，结果走两个回调之一。
            // 上游：Boot 的 M2DemoBehaviour（装配资源层之后）、Editor 的 NetDebugWindow（设置编辑器来源之后）
            // 下游：ConfigMgr.PreloadAll
            // 注意：调用前必须已给 ConfigMgr 设置好来源，否则立刻走 onFailed；表已缓存时回调会在返回前同步触发。
            ConfigMgr.Instance.PreloadAll(All, onDone, onFailed);
        }
    }
}
