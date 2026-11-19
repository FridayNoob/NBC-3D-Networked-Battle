// 这个文件里有：
//   BattleField（类）：把 BattleObstacle 表的行编译成"哪些坐标不能走"，并提供整数判定

using System;
using System.Collections.Generic;

namespace NBC.Server.Game;

/// <summary>
/// 一张副本战场的障碍判定表：读表后只留下"能不能站"，不涉及任何物理。
/// <para>
/// 主要数据成员：圆障碍（圆心 + 半径）与轴对齐矩形（中心 + 半宽半高），全部是整数毫米。
/// 主要外部方法：`From`（按副本过滤并校验）、`IsBlocked`（判断一个坐标是否被挡）、`IsEmpty`。
/// 谁在用它：`DungeonBattle` 建世界时造一份，`BattleEntity.ApplyMove` 每次位移前问它。
/// </para>
/// </summary>
public sealed class BattleField
{
    /// <summary>没有障碍的战场（空表 / 副本没有配障碍时用它，行为与改动前一致）。</summary>
    public static readonly BattleField Empty = new(Array.Empty<Circle>(), Array.Empty<Box>());

    /// <summary>一个圆障碍：圆心与半径，单位毫米。</summary>
    private readonly struct Circle
    {
        /// <summary>圆心 X（毫米）。</summary>
        public readonly int Xmm;

        /// <summary>圆心 Z（毫米）。</summary>
        public readonly int Zmm;

        /// <summary>半径（毫米）。</summary>
        public readonly int RadiusMm;

        /// <summary>记一个圆。</summary>
        /// <param name="xMm">圆心 X（毫米）。</param>
        /// <param name="zMm">圆心 Z（毫米）。</param>
        /// <param name="radiusMm">半径（毫米）。</param>
        public Circle(int xMm, int zMm, int radiusMm)
        {
            Xmm = xMm;
            Zmm = zMm;
            RadiusMm = radiusMm;
        }
    }

    /// <summary>一个轴对齐矩形障碍：中心与半宽半高，单位毫米。</summary>
    private readonly struct Box
    {
        /// <summary>中心 X（毫米）。</summary>
        public readonly int Xmm;

        /// <summary>中心 Z（毫米）。</summary>
        public readonly int Zmm;

        /// <summary>半宽（毫米，X 方向）。</summary>
        public readonly int HalfXMm;

        /// <summary>半高（毫米，Z 方向）。</summary>
        public readonly int HalfZMm;

        /// <summary>记一个矩形。</summary>
        /// <param name="xMm">中心 X（毫米）。</param>
        /// <param name="zMm">中心 Z（毫米）。</param>
        /// <param name="halfXMm">半宽（毫米）。</param>
        /// <param name="halfZMm">半高（毫米）。</param>
        public Box(int xMm, int zMm, int halfXMm, int halfZMm)
        {
            Xmm = xMm;
            Zmm = zMm;
            HalfXMm = halfXMm;
            HalfZMm = halfZMm;
        }
    }

    /// <summary>圆障碍列表（顺序与表里编号一致，判定结果因此可复现）。</summary>
    private readonly Circle[] m_circles;

    /// <summary>矩形障碍列表。</summary>
    private readonly Box[] m_boxes;

    /// <summary>记一份判定表（外部统一走 `From`，这里只给内部构造用）。</summary>
    /// <param name="circles">圆障碍。</param>
    /// <param name="boxes">矩形障碍。</param>
    private BattleField(Circle[] circles, Box[] boxes)
    {
        m_circles = circles;
        m_boxes = boxes;
        Count = circles.Length + boxes.Length;
    }

    /// <summary>生效的障碍条数。</summary>
    public int Count { get; }

    /// <summary>因为数值非法被跳过的条数（半径或半宽半高 ≤ 0）。</summary>
    public int Skipped { get; private set; }

    /// <summary>一条障碍都没有时为真，此时调用方可以直接走老路径。</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>把表里的行按副本过滤并校验，编译成判定表。</summary>
    /// <param name="rows">`BattleObstacle` 表的全部行。</param>
    /// <param name="dungeonId">只取这个副本的行。</param>
    /// <returns>判定表；没有可用行时返回 `Empty`。</returns>
    /// <remarks>
    /// 校验规则：圆要求半径 &gt; 0；矩形要求半宽与半高都 &gt; 0。不合法的行跳过并计数，
    /// 由调用方（`DungeonBattle`）在日志里报出来，避免刷屏。
    /// </remarks>
    public static BattleField From(IReadOnlyList<ObstacleRow> rows, int dungeonId)
    {
        if (rows == null || rows.Count == 0)
        {
            return Empty;
        }

        var circles = new List<Circle>();
        var boxes = new List<Box>();
        int skipped = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            ObstacleRow row = rows[i];

            if (row.DungeonId != dungeonId)
            {
                continue;
            }

            if (row.Kind == 0)
            {
                if (row.RadiusMm <= 0)
                {
                    skipped++;
                    continue;
                }

                circles.Add(new Circle(row.Xmm, row.Zmm, row.RadiusMm));
            }
            else if (row.Kind == 1)
            {
                if (row.HalfXMm <= 0 || row.HalfZMm <= 0)
                {
                    skipped++;
                    continue;
                }

                boxes.Add(new Box(row.Xmm, row.Zmm, row.HalfXMm, row.HalfZMm));
            }
            else
            {
                skipped++;
            }
        }

        if (circles.Count == 0 && boxes.Count == 0)
        {
            return Empty;
        }

        return new BattleField(circles.ToArray(), boxes.ToArray()) { Skipped = skipped };
    }

    /// <summary>判断某个坐标是否落在障碍内部（含边界）。</summary>
    /// <param name="xMm">X（毫米）。</param>
    /// <param name="zMm">Z（毫米）。</param>
    /// <returns>被挡住返回 true；场地边界不在这里判（由调用方的钳位负责）。</returns>
    public bool IsBlocked(int xMm, int zMm)
    {
        for (int i = 0; i < m_circles.Length; i++)
        {
            Circle circle = m_circles[i];
            long dx = xMm - circle.Xmm;
            long dz = zMm - circle.Zmm;
            long r = circle.RadiusMm;

            // 平方比较，避免开方；用 long 防止 20000 级别的平方溢出 int
            if (dx * dx + dz * dz <= r * r)
            {
                return true;
            }
        }

        for (int i = 0; i < m_boxes.Length; i++)
        {
            Box box = m_boxes[i];

            if (Math.Abs(xMm - box.Xmm) <= box.HalfXMm && Math.Abs(zMm - box.Zmm) <= box.HalfZMm)
            {
                return true;
            }
        }

        return false;
    }
}
