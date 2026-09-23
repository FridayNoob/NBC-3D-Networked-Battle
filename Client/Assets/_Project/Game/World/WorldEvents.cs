// ============================================================================
//  WorldEvents —— 大世界 / 场景流程对外广播的事件
//  项目：3D联网战斗Demo   对应：M2-C（让"到达区域"这类条件也有真实产出方）
//
//  ---------------------------------------------------------------------------
//  为什么 M2 就需要它
//  ---------------------------------------------------------------------------
//  条件系统支持五种条件事件（`EConditionEvent`），M2 只有战斗模块在产事件
//  （击杀 / 用技能命中）。剩下的三种里，**"到达区域"是最容易有真实产出方的一个**：
//
//      "进关卡"这件事本身就等于"进入了某个区域" ——
//      而 M2 的验收就是"**进关卡 → 打怪 → 任务完成 → 交付**"。
//
//  有了它，演示用的任务（3004 清剿野狼 = 击杀 3 只野狼 + 抵达一号区域）
//  凑齐了**两条不同来源的条件**，于是"全部条件都满了才完成"这条语义
//  在真实事件上也被走了一遍（而不只是测试里手搓 `Notify`）。
//
//  ⚠️ 另外两种（`CollectItem` / `TalkToNpc`）的产出方分别是
//     **背包/掉落**（M3）与 **NPC 对话**（M4）—— M2 明确不做，
//     但条件系统那一侧已经支持了（有测试覆盖），接上时只需加一行 `Bind`。
// ============================================================================

using NBC.Framework;

namespace NBC.Game.World
{
    /// <summary>大世界模块的事件标识。</summary>
    public static class WorldEvents
    {
        /// <summary>
        /// 玩家进入了某个区域。载荷：<see cref="AreaEnteredPayload"/>。
        /// <para>
        /// 由**关卡流程**发（M2 里就是"进关卡"这一步）。
        /// 分区加载（Q5 的决定）在 M5 才做无缝地形，本事件与它无关 ——
        /// 它只表达"玩家现在在 1 号区域"这个事实。
        /// </para>
        /// </summary>
        public static readonly EventId AreaEntered = EventId.Declare("World.AreaEntered");
    }

    /// <summary>进入区域的载荷。</summary>
    public readonly struct AreaEnteredPayload
    {
        /// <summary>区域编号（与 `Level` 表的主键对应）。</summary>
        public readonly int AreaId;

        /// <summary>造一个载荷。</summary>
        /// <param name="areaId">区域编号。</param>
        public AreaEnteredPayload(int areaId)
        {
            AreaId = areaId;
        }
    }
}
