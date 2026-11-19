// 这个文件里有：
//   BattleObstacleMarker（MonoBehaviour）：在编辑场景里描述一个战场障碍（圆或轴对齐矩形），
//   用 Gizmos 画出来，供编辑器菜单写回 Configs/Design/BattleObstacle.csv

using UnityEngine;

namespace NBC.Game.View
{
    // 主要功能：把"这个位置有一根柱子/一块石头"变成可写回配置的数据（形状 + 尺寸，单位毫米）。
    // 主要数据成员：Kind（0 圆 / 1 矩形）、RadiusMm、HalfXMm、HalfZMm。
    // 主要外部方法：无。它自己不参与运行时逻辑，只给编辑器菜单扫。
    // 谁在用它：Editor 的 BattleObstacleWriter（写回 CSV）；Scene 视图的 Gizmos 显示。
    /// <summary>
    /// 场景里的一个障碍标记。位置就是障碍中心（只看 X/Z，Y 只影响 Gizmos 画在哪），
    /// 尺寸填毫米，与服务端 `BattleField` 的判定口径一致。
    /// <para>注意：它不参与运行时碰撞 —— 判定在服务端，客户端只显示快照位置。</para>
    /// </summary>
    public sealed class BattleObstacleMarker : MonoBehaviour
    {
        /// <summary>形状：0 = 圆（用 RadiusMm），1 = 轴对齐矩形（用 HalfXMm / HalfZMm）。</summary>
        [Tooltip("0 = 圆（填半径），1 = 矩形（填半宽半高）")]
        [Range(0, 1)]
        public int Kind;

        /// <summary>圆的半径（毫米）。Kind = 0 时生效。</summary>
        [Tooltip("圆的半径（毫米），Kind = 0 时生效")]
        [Min(0)]
        public int RadiusMm = 800;

        /// <summary>矩形的半宽（毫米，X 方向）。Kind = 1 时生效。</summary>
        [Tooltip("矩形的半宽（毫米，X 方向），Kind = 1 时生效")]
        [Min(0)]
        public int HalfXMm = 1000;

        /// <summary>矩形的半高（毫米，Z 方向）。Kind = 1 时生效。</summary>
        [Tooltip("矩形的半高（毫米，Z 方向），Kind = 1 时生效")]
        [Min(0)]
        public int HalfZMm = 500;

        /// <summary>本标记在场景里的中心（米，含 Y；服务端只用 X/Z）。</summary>
        public Vector3 Center => transform.position;

        /// <summary>
        /// 在 Scene 视图画出障碍范围：红色线框。
        /// <para>只在编辑器里画（Gizmos 不进包），所以玩家在游戏里看不到 —— 要看画布效果请用真实模型。</para>
        /// </summary>
        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.25f, 0.2f, 0.9f);

            Vector3 center = transform.position;

            if (Kind == 0)
            {
                Gizmos.DrawWireSphere(center, RadiusMm * 0.001f);
                return;
            }

            Gizmos.DrawWireCube(center,
                new Vector3(HalfXMm * 0.002f, 0.05f, HalfZMm * 0.002f));
        }

        /// <summary>选中时把地面轮廓画成半透明，方便对齐位置。</summary>
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.25f, 0.2f, 0.15f);
            Vector3 center = transform.position;

            if (Kind == 0)
            {
                Gizmos.DrawSphere(center, RadiusMm * 0.001f);
                return;
            }

            Gizmos.DrawCube(center, new Vector3(HalfXMm * 0.002f, 0.05f, HalfZMm * 0.002f));
        }
    }
}
