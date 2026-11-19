// 这个文件里有：
//   BattleObstacleWriter（编辑器菜单）：把场景里的 BattleObstacleMarker 写回
//   Configs/Design/BattleObstacle.csv，并在 Console 报出条数与校验问题

using System.Collections.Generic;
using System.IO;
using System.Text;
using NBC.Game.View;
using UnityEditor;
using UnityEngine;

namespace NBC.Editor
{
    // 主要功能：把"场景里摆好的障碍标记"转成服务端能读的 CSV（§0.4：界面里调，写回配置源）。
    // 主要数据成员：CsvPath / CsvHeader（表头三行 + 规则行，与 ConfigKit 约定一致）。
    // 主要外部方法：WriteObstacles（菜单入口）。
    // 谁在用它：负责人从菜单 Tools/NBC/战场/写回障碍到 CSV 调用。
    /// <summary>
    /// 障碍写回工具：扫当前打开场景里的所有 <see cref="BattleObstacleMarker"/>，
    /// 按名字排序后覆盖写 `Configs/Design/BattleObstacle.csv`。
    /// <para>注意：写回后必须重跑 ConfigKit 并重启服务端才生效（与 Monster 表一致）。</para>
    /// </summary>
    internal static class BattleObstacleWriter
    {
        /// <summary>CSV 相对工程根的位置。</summary>
        private const string CsvPath = "Configs/Design/BattleObstacle.csv";

        /// <summary>默认副本编号（本计划只有 1001 一个副本；以后从 Dungeon 表取）。</summary>
        private const int DungeonId = 1001;

        /// <summary>障碍离出生点太近的告警半径（毫米）。</summary>
        private const int SpawnGuardMm = 1500;

        /// <summary>提示里用的出生点（与服务端 `RoomBattleService` 一致：(-500, 0) 毫米）。</summary>
        private static readonly Vector2 SpawnMm = new Vector2(-500f, 0f);

        /// <summary>
        /// 菜单入口：扫场景 → 校验 → 写 CSV → Console 与弹窗汇报。
        /// <para>步骤：1) 找标记并排序（编号稳定，便于 diff）2) 逐条校验尺寸与出生点距离
        /// 3) 拼 CSV 文本 4) 写文件 5) 汇报。</para>
        /// </summary>
        [MenuItem("Tools/NBC/战场/写回障碍到 CSV", priority = 210)]
        private static void WriteObstacles()
        {
            BattleObstacleMarker[] markers =
                Object.FindObjectsOfType<BattleObstacleMarker>(true);

            if (markers.Length == 0)
            {
                EditorUtility.DisplayDialog("写回障碍",
                    "当前场景里没有任何 BattleObstacleMarker。\n\n" +
                    "请先建一个空物体、挂上 BattleObstacleMarker 当障碍。", "知道了");
                return;
            }

            var sorted = new List<BattleObstacleMarker>(markers);
            sorted.Sort((left, right) =>
                string.CompareOrdinal(left.gameObject.name, right.gameObject.name));

            var problems = new List<string>();
            var rows = new StringBuilder();

            for (int i = 0; i < sorted.Count; i++)
            {
                BattleObstacleMarker marker = sorted[i];
                Vector3 center = marker.Center;
                int xMm = Mathf.RoundToInt(center.x * 1000f);
                int zMm = Mathf.RoundToInt(center.z * 1000f);

                if (marker.Kind == 0 && marker.RadiusMm <= 0)
                {
                    problems.Add($"{marker.gameObject.name}：圆障碍半径必须 > 0");
                }

                if (marker.Kind == 1 && (marker.HalfXMm <= 0 || marker.HalfZMm <= 0))
                {
                    problems.Add($"{marker.gameObject.name}：矩形障碍的半宽与半高都必须 > 0");
                }

                float dx = xMm - SpawnMm.x;
                float dz = zMm - SpawnMm.y;

                if (Mathf.Sqrt(dx * dx + dz * dz) < SpawnGuardMm)
                {
                    problems.Add($"{marker.gameObject.name}：离出生点 (-500, 0) 不到 {SpawnGuardMm}mm，" +
                                 "出生时可能被卡住");
                }

                rows.Append(i + 1).Append(',')
                    .Append(DungeonId).Append(',')
                    .Append(marker.Kind).Append(',')
                    .Append(xMm).Append(',')
                    .Append(zMm).Append(',')
                    .Append(marker.Kind == 0 ? marker.RadiusMm : 0).Append(',')
                    .Append(marker.Kind == 1 ? marker.HalfXMm : 0).Append(',')
                    .Append(marker.Kind == 1 ? marker.HalfZMm : 0).Append(',')
                    .Append(marker.gameObject.name)
                    .Append('\n');
            }

            string header =
                "id,dungeonId,kind,xMm,zMm,radiusMm,halfXMm,halfZMm,name\n" +
                "编号,副本编号,形状(0圆1方),中心X（毫米）,中心Z（毫米）,半径（毫米）,半宽（毫米）,半高（毫米）,备注\n" +
                "int,int,int,int,int,int,int,int,string\n" +
                "key,\"range(1,999999)\",\"range(0,1)\",,,\"range(0,20000)\",\"range(0,20000)\",\"range(0,20000)\",\"len(0,32)\"\n";

            string projectRoot = GetProjectRoot();

            if (projectRoot.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "写回障碍",
                    "找不到仓库根目录（没有找到含 Configs/Design 的那一级）。\n\n" +
                    "没有写任何文件，请检查工程目录结构。",
                    "知道了");
                return;
            }

            string path = Path.Combine(projectRoot, CsvPath);
            File.WriteAllText(path, header + rows, new UTF8Encoding(false));

            Debug.Log($"[障碍] 已写回 {sorted.Count} 条到 {path}（副本 {DungeonId}）");

            foreach (string problem in problems)
            {
                Debug.LogWarning("[障碍] " + problem);
            }

            string detail = problems.Count == 0
                ? $"已写回 {sorted.Count} 条障碍。\n\n下一步：重启服务端才生效。"
                : $"已写回 {sorted.Count} 条，但有 {problems.Count} 个问题（见 Console）：\n\n" +
                  string.Join("\n", problems);

            EditorUtility.DisplayDialog("写回障碍", detail, "知道了");
        }

        /// <summary>
        /// 取仓库根目录（`Configs\Design` 所在的那一级）。
        /// <para>
        /// 注意：本工程的 Unity 目录不是仓库根 —— `Application.dataPath` 是
        /// `<仓库>\Client\Assets`，往上两级才是 `<仓库>`（`Configs`、`Server`、`Docs` 都在这儿）。
        /// 所以这里不写死"上一级"，而是往上找第一个含 `Configs\Design` 的目录：
        /// 以后 Unity 工程挪位置也不会再写错地方。
        /// </para>
        /// </summary>
        /// <returns>仓库根绝对路径；找不到返回空串（调用方负责报错，别在错的地方写文件）。</returns>
        private static string GetProjectRoot()
        {
            DirectoryInfo dir = new DirectoryInfo(Application.dataPath);

            for (int depth = 0; depth < 6; depth++)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Configs", "Design")))
                {
                    return dir.FullName;
                }

                DirectoryInfo parent = dir.Parent;

                if (parent == null)
                {
                    break;
                }

                dir = parent;
            }

            return string.Empty;
        }
    }
}
