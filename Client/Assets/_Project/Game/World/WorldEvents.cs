// 这个文件里有：
//   WorldEvents（静态类）：大世界与场景流程对外广播的事件标识
//   AreaEnteredPayload（结构体）：进入区域的载荷

using NBC.Framework;

namespace NBC.Game.World
{
    // 主要功能：声明「玩家进入某区域」这个事件，让「到达区域」类条件有真实产出方。
    // 主要数据成员：AreaEntered（事件标识）。
    // 主要外部方法：无。
    // 谁在用它：关卡流程在「进关卡」时发；条件系统订阅后推进到达区域类条件。
    // 注意：与分区加载无关，只表达「玩家现在在 1 号区域」这个事实。
    public static class WorldEvents
    {
        // 玩家进入了某个区域（载荷 AreaEnteredPayload）
        public static readonly EventId AreaEntered = EventId.Declare("World.AreaEntered");
    }

    // 主要功能：进入区域的载荷。
    // 主要数据成员：AreaId（区域编号，与 Level 表主键对应）。
    // 主要外部方法：构造函数。
    // 谁在用它：WorldEvents.AreaEntered 的订阅方。
    public readonly struct AreaEnteredPayload
    {
        // 区域编号（与 Level 表的主键对应）
        public readonly int AreaId;

        // 功能：造一个进入区域的载荷。
        // 入参：areaId 区域编号
        // 返回：无
        public AreaEnteredPayload(int areaId)
        {
            AreaId = areaId;
        }
    }
}
