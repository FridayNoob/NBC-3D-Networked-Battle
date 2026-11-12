// 这个文件里有：
//   MonsterAiDebugViewEditor（编辑器类）：MonsterAiDebugView 的自定义 Inspector —— 对比表值与显示值、一键写回 CSV。
//
// 【仅编辑器】写回只改 CSV，不自动跑重生成（遵守 AGENTS.md 第 3 节：禁止一键脚本）。

using NBC.Game.View;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    // 主要功能：把"表值 / 显示值 / 是否一致"摊开给负责人看，并提供「写回 Monster.csv」。
    // 主要数据成员：无（只用 target）。
    // 主要外部方法：OnInspectorGUI。
    // 谁在用它：Inspector 里选中带 MonsterAiDebugView 的怪物预制体时。
    // 注意：覆盖值未写回时必须在顶部显示红字，否则又会变成"显示 3 米、实际 2 米"。
    [CustomEditor(typeof(MonsterAiDebugView))]
    public sealed class MonsterAiDebugViewEditor : UnityEditor.Editor
    {
        // 功能：画自定义 Inspector。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 画默认字段
        //   2) 三行对比（攻击 / 检测 / 接地）
        //   3) 有覆盖就红字提示
        //   4) 两个按钮：写回、清空覆盖
        // 上游：Unity Inspector
        // 下游：MonsterAiCsvWriter.TryWrite
        public override void OnInspectorGUI()
        {
            MonsterAiDebugView view = (MonsterAiDebugView)target;

            DrawDefaultInspector();
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("判定长度对比（毫米）", EditorStyles.boldLabel);
            DrawCompareRow("攻击（红）", view.TableAttackRangeMm, view.DisplayAttackRangeMm);
            DrawCompareRow("玩家检测（白）", view.TableAggroRangeMm, view.DisplayAggroRangeMm);
            DrawCompareRow("接地线（蓝）", view.TableGroundLineMm, view.DisplayGroundLineMm);
            EditorGUILayout.Space();

            if (view.IsOverriding)
            {
                EditorGUILayout.HelpBox(
                    "显示值 ≠ 服务端值：现在画的圈只影响显示，服务端判定仍用表里的值。\n点「写回 Monster.csv」才会把它变成真值。",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox("显示值与服务端一致（都来自 Monster.csv）。", MessageType.Info);
            }

            if (GUILayout.Button("写回 Monster.csv（攻击 / 检测 / 接地）", GUILayout.Height(28f)))
            {
                bool ok = MonsterAiCsvWriter.TryWrite(
                    view.MonsterConfigId,
                    view.DisplayAttackRangeMm,
                    view.DisplayAggroRangeMm,
                    view.DisplayGroundLineMm,
                    out string message);

                if (ok)
                {
                    view.ClearOverrides();
                    EditorUtility.SetDirty(view);
                    EditorUtility.DisplayDialog("已写回", message, "知道了");
                }
                else
                {
                    EditorUtility.DisplayDialog("写回失败", message, "知道了");
                }
            }

            if (GUILayout.Button("清空覆盖（回到表值）"))
            {
                view.ClearOverrides();
                EditorUtility.SetDirty(view);
            }
        }

        // 功能：画一行「名称 + 表值 + 显示值」。
        // 入参：label 名称；tableValue 表值；displayValue 显示值
        // 返回：无
        // 步骤：水平排三个字段
        // 上游：OnInspectorGUI
        // 下游：无
        private static void DrawCompareRow(string label, int tableValue, int displayValue)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(110f));
            EditorGUILayout.LabelField("表 " + tableValue, GUILayout.Width(90f));
            EditorGUILayout.LabelField("显示 " + displayValue, GUILayout.Width(90f));

            if (tableValue != displayValue)
            {
                GUILayout.Label("≠", EditorStyles.boldLabel, GUILayout.Width(20f));
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
