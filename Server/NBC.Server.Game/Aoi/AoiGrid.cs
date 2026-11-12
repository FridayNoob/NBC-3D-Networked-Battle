// 这个文件里有：
//   AoiGrid（类）：九宫格兴趣管理 —— 判断"某个观察者该不该看到某个实体"。
//
// 它只决定**发不发**，不参与任何战斗判定；服务端的模拟永远看全世界。

using System;

namespace NBC.Server.Game.Aoi
{
    // 主要功能：按固定格子把平面切开，用"3×3 邻域"判断可见性。
    // 主要数据成员：CellSizeMm（格子边长，毫米）。
    // 主要外部方法：CellOf（坐标→格子）、IsVisible（观察者→目标是否在邻域内）。
    // 谁在用它：RoomBattleService.BroadcastSnapshot（按观察者过滤快照）。
    // 注意：坐标可以是负数，除法必须向下取整（不是朝零取整），否则 0 附近会塌成一格。
    public sealed class AoiGrid
    {
        // 默认格子边长（毫米）：10 米。demo 场地 10×10 米 ⇒ 等于全可见，行为不突变
        public const int DefaultCellSizeMm = 10_000;

        // 邻域半径（格）：1 = 3×3
        public const int NeighbourhoodRadiusCells = 1;

        // 格子边长（毫米）
        public int CellSizeMm { get; }

        // 功能：构造。
        // 入参：cellSizeMm 格子边长（毫米，必须 > 0；非法值回落到默认）
        // 返回：无
        // 步骤：夹住非法值
        // 上游：RoomBattleService
        // 下游：CellOf / IsVisible
        public AoiGrid(int cellSizeMm = DefaultCellSizeMm)
        {
            CellSizeMm = cellSizeMm > 0 ? cellSizeMm : DefaultCellSizeMm;
        }

        // 功能：把坐标换成格子下标（向下取整）。
        // 入参：mm 坐标（毫米，可为负）
        // 返回：格子下标
        // 步骤：非负直接整除；负数先转正再向上取整后取反
        // 上游：IsVisible
        // 下游：无
        public int CellOf(int mm)
        {
            if (mm >= 0)
            {
                return mm / CellSizeMm;
            }

            return -((-mm + CellSizeMm - 1) / CellSizeMm);
        }

        // 功能：判断目标是否在观察者的邻域内。
        // 入参：viewerXmm/viewerZmm 观察者坐标；targetXmm/targetZmm 目标坐标
        // 返回：true = 该发给这个观察者
        // 步骤：算两侧格子下标 → 两个轴的差都 <= 半径
        // 上游：RoomBattleService.BroadcastSnapshot
        // 下游：CellOf
        public bool IsVisible(int viewerXmm, int viewerZmm, int targetXmm, int targetZmm)
        {
            int dx = Math.Abs(CellOf(viewerXmm) - CellOf(targetXmm));
            int dz = Math.Abs(CellOf(viewerZmm) - CellOf(targetZmm));
            return dx <= NeighbourhoodRadiusCells && dz <= NeighbourhoodRadiusCells;
        }
    }
}
