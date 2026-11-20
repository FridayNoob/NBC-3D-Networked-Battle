// 这个文件里有：
//   SkillHitSection（静态类）：AnimationEventEditorWindow 的「技能命中帧」分区的绘制入口。

using System.Text;
using UnityEditor;
using UnityEngine;

namespace NBC.Editor
{
    // 主要功能：在动画事件窗口顶部画「把 OnSkillHit 事件换算成 Skill.csv 的 hitFrames」这一块。
    // 主要数据成员：无（状态存在窗口里）。
    // 主要外部方法：Draw（画界面）、Format（把一行扫描结果拼成人看的文本）。
    // 谁在用它：AnimationEventEditorWindow.OnGUI。
    internal static class SkillHitSection
    {
        // 功能：画扫描 / 试算 / 写回三个按钮与结果列表。
        // 入参：rows 扫描结果（可为 null）；scroll 列表滚动位置；扫描与写回的回调由窗口提供
        // 返回：无
        // 步骤：
        //   1) 标题 + 换算公式说明
        //   2) 按钮行：扫描（原地更新 rows）
        //   3) 结果列表：每个技能一行，带片段名、速度、时间与帧
        // 上游：AnimationEventEditorWindow.OnGUI
        // 下游：SkillHitWriter.Collect / WriteBack
        internal static void Draw(ref System.Collections.Generic.List<SkillHitRow> rows, ref Vector2 scroll)
        {
            EditorGUILayout.LabelField("技能命中帧（从动画事件写回 Skill.csv）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "给片段的 OnSkillHit 事件填 int 参数 = 技能编号（例如 2001），时间轴拖到该打的那一下。" +
                "\n换算：逻辑帧 = round(事件时间 × 30 ÷ 控制器播放速度)。写回后跑一次 ConfigKit、重启服务端生效。",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("扫描所有片段里的 OnSkillHit", GUILayout.Height(24f)))
                {
                    rows = SkillHitWriter.Collect();
                    Debug.Log("[动画事件] 扫到 " + (rows == null ? 0 : rows.Count) + " 条带技能命中事件的片段");
                }

                bool none = rows == null || rows.Count == 0;

                using (new EditorGUI.DisabledScope(none))
                {
                    if (GUILayout.Button("试算（不写文件）", GUILayout.Height(24f), GUILayout.Width(140f)))
                    {
                        // 先重扫：加完事件直接点试算时，rows 还是上次扫描的旧快照，会给出过期的帧号。
                        rows = SkillHitWriter.Collect();
                        string message;
                        SkillHitWriter.WriteBack(rows, true, out message);
                        Debug.Log("[动画事件] 试算结果：\n" + message);
                    }

                    if (GUILayout.Button("写回 Skill.csv", GUILayout.Height(24f), GUILayout.Width(140f)))
                    {
                        // 写文件前同样重扫，保证写进去的是当前事件，不是上次的结果。
                        rows = SkillHitWriter.Collect();
                        string message;
                        int changed = SkillHitWriter.WriteBack(rows, false, out message);
                        EditorUtility.DisplayDialog(
                            "技能命中帧",
                            changed > 0 ? "改了 " + changed + " 条：\n\n" + message : "没有改动：\n\n" + message,
                            "好");
                        AssetDatabase.SaveAssets();
                    }
                }
            }

            if (rows == null)
            {
                return;
            }

            if (rows.Count == 0)
            {
                EditorGUILayout.HelpBox("还没扫到事件：先选片段，在下面加 OnSkillHit 并填技能编号。", MessageType.Warning);
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(120f));

            foreach (SkillHitRow row in rows)
            {
                EditorGUILayout.LabelField(Format(row), EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        // 功能：把一行扫描结果拼成「技能 ← 片段 速度 时间 ⇒ 帧」的文本。
        // 入参：row 扫描结果
        // 返回：单行字符串
        // 步骤：依次拼技能编号、片段名、速度、各事件时间、换算帧、问题说明
        // 上游：Draw
        // 下游：无
        private static string Format(SkillHitRow row)
        {
            var text = new StringBuilder();
            text.Append(row.SkillId <= 0 ? "（未填编号）" : row.SkillId.ToString());
            text.Append("  ←  ").Append(row.ClipName);
            text.Append("   速度 ").Append(row.Speed.ToString("0.##"));
            text.Append("   时间 ");

            for (int i = 0; i < row.Times.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(" / ");
                }

                text.Append(row.Times[i].ToString("0.###")).Append("s");
            }

            text.Append("   ⇒ 帧 ");

            for (int i = 0; i < row.Frames.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                text.Append(row.Frames[i]);
            }

            if (!string.IsNullOrEmpty(row.Problem))
            {
                text.Append("   [跳过] ").Append(row.Problem);
            }

            return text.ToString();
        }
    }
}
