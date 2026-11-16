// 这个文件里有：
//   DungeonConfigWindow（编辑器窗口）：列表方式编辑 DungeonConfig 的行，并提供导出 CSV、从 CSV 导入、校验三个动作

using System.Collections.Generic;
using NBC.Game.Config;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    // 主要功能：Dungeon 表的可视化编辑窗口，改完点「导出到 CSV」才会写磁盘，点「从 CSV 导入」会把资产覆盖成文件内容。
    // 主要数据成员：m_asset 是配置资产，m_errors 是最近一次校验的问题，m_status 是最近一次操作的结果。
    // 主要外部方法：无公开方法，入口是菜单 Tools/NBC/配置表/副本表（SO ↔ CSV）。
    // 谁在用它：负责人手工改副本配置；底层读写全部交给 DungeonCsvBridge。
    public sealed class DungeonConfigWindow : EditorWindow
    {
        // 资产文件名，拼在 ConfigImporter.AssetDirectory 后面
        private const string AssetFileName = "/DungeonConfig.asset";

        // 当前编辑的资产，窗口打开时加载
        private DungeonConfig m_asset;

        // 行列表的滚动位置，行数多时用
        private Vector2 m_scroll;

        // 最近一次操作的提示，成功或失败都写这里
        private string m_status = string.Empty;

        // 最近一次提示是不是错误，用来决定提示框颜色
        private bool m_statusIsError;

        // 最近一次校验的问题列表，点「校验」或导出失败时填充
        private readonly List<string> m_errors = new List<string>();

        // 功能：菜单入口，打开或聚焦本窗口。
        // 入参：无。
        // 返回含义：无。
        // 步骤：按固定标题取窗口，取不到就新建。
        // 上游/下游：上游是 Unity 菜单；下游是 OnEnable。
        [MenuItem("Tools/NBC/配置表/副本表（SO ↔ CSV）")]
        private static void Open()
        {
            GetWindow<DungeonConfigWindow>("副本表");
        }

        // 功能：窗口启用时加载资产。
        // 入参：无。
        // 返回含义：无。
        // 步骤：按导入器的资产目录拼路径并加载；加载不到就把提示写出来。
        // 上游/下游：上游是 Unity 窗口生命周期；下游是 AssetDatabase。
        private void OnEnable()
        {
            LoadAsset();
        }

        // 功能：画整个窗口。
        // 入参：无。
        // 返回含义：无。
        // 步骤：先画工具栏与脏提示，再画行列表，最后画状态与错误。
        // 上游/下游：上游是 Unity 的 IMGUI；下游是 DrawToolbar、DrawRows、DrawStatus。
        private void OnGUI()
        {
            DrawToolbar();
            DrawRows();
            DrawStatus();
        }

        // 功能：加载资产并清掉旧提示。
        // 入参：无。
        // 返回含义：无。
        // 步骤：拼路径加载；成功就清空提示，失败就写错误提示。
        // 上游/下游：上游是 OnEnable 与刷新按钮；下游是 AssetDatabase。
        private void LoadAsset()
        {
            m_asset = AssetDatabase.LoadAssetAtPath<DungeonConfig>(ConfigImporter.AssetDirectory + AssetFileName);

            if (m_asset == null)
            {
                m_status = "读不到 " + ConfigImporter.AssetDirectory + AssetFileName + "，请先跑一次「导入 TSV → ScriptableObject」";
                m_statusIsError = true;
            }
        }

        // 功能：画顶部工具栏，包含刷新、校验、导出、导入四个按钮和脏提示。
        // 入参：无。
        // 返回含义：无。
        // 步骤：先画按钮行，再调桥的 IsDirty 决定是否显示红字。
        // 上游/下游：上游是 OnGUI；下游是 DungeonCsvBridge 的四个方法。
        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("刷新", GUILayout.Width(60)))
            {
                LoadAsset();
                m_status = "已重新读取资产";
                m_statusIsError = false;
            }

            if (GUILayout.Button("校验", GUILayout.Width(60)))
            {
                RunValidate();
            }

            using (new EditorGUI.DisabledScope(m_asset == null))
            {
                if (GUILayout.Button("导出到 CSV", GUILayout.Width(110)))
                {
                    bool ok = DungeonCsvBridge.Export(m_asset, out string message);
                    m_status = message;
                    m_statusIsError = !ok;
                    RunValidate();
                }

                if (GUILayout.Button("从 CSV 导入", GUILayout.Width(110)))
                {
                    ImportWithConfirm();
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(m_asset == null ? "未加载资产" : "行数：" + m_asset.rows.Count);
            EditorGUILayout.EndHorizontal();

            if (m_asset != null && DungeonCsvBridge.IsDirty(m_asset, out string detail))
            {
                EditorGUILayout.HelpBox(detail, MessageType.Warning);
            }
        }

        // 功能：画所有数据行与「增加一行」按钮。
        // 入参：无。
        // 返回含义：无。
        // 步骤：滚动区内逐行调 DrawRow；删除请求延后到循环结束再执行，避免遍历时改列表。
        // 上游/下游：上游是 OnGUI；下游是 DrawRow。
        private void DrawRows()
        {
            if (m_asset == null)
            {
                return;
            }

            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
            int removeIndex = -1;

            for (int i = 0; i < m_asset.rows.Count; i++)
            {
                if (DrawRow(m_asset.rows[i], i))
                {
                    removeIndex = i;
                }
            }

            EditorGUILayout.EndScrollView();

            if (removeIndex >= 0)
            {
                Undo.RecordObject(m_asset, "删除副本行");
                m_asset.rows.RemoveAt(removeIndex);
                EditorUtility.SetDirty(m_asset);
            }

            if (GUILayout.Button("增加一行"))
            {
                Undo.RecordObject(m_asset, "增加副本行");
                m_asset.rows.Add(NewRow());
                EditorUtility.SetDirty(m_asset);
            }
        }

        // 功能：画一行数据，返回是否点了删除。
        // 入参：row 是数据行；index 是行号（从 0 起，显示时加一）。
        // 返回含义：true 表示这一行要删除。
        // 步骤：一行里依次画主键、名称、人数、半径、怪物、BOSS 和删除按钮，改动后标记资产脏。
        // 上游/下游：上游是 DrawRows；下游是 ParseMonsters。
        private bool DrawRow(Config_Dungeon row, int index)
        {
            bool remove = false;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField("#" + (index + 1), GUILayout.Width(34));

            EditorGUI.BeginChangeCheck();
            int id = EditorGUILayout.IntField(row.id, GUILayout.Width(70));
            string name = EditorGUILayout.TextField(row.name, GUILayout.Width(120));
            int maxPlayers = EditorGUILayout.IntField(row.maxPlayers, GUILayout.Width(40));
            int spawnRadiusMm = EditorGUILayout.IntField(row.spawnRadiusMm, GUILayout.Width(70));
            int bossId = EditorGUILayout.IntField(row.bossId, GUILayout.Width(70));

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(m_asset, "修改副本行");
                row.id = id;
                row.name = name;
                row.maxPlayers = maxPlayers;
                row.spawnRadiusMm = spawnRadiusMm;
                row.bossId = bossId;
                EditorUtility.SetDirty(m_asset);
            }

            if (GUILayout.Button("删除", GUILayout.Width(46)))
            {
                remove = true;
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("普通怪（逗号分隔）", GUILayout.Width(130));
            string monstersText = EditorGUILayout.TextField(string.Join(",", row.monsters));

            if (GUILayout.Button("应用", GUILayout.Width(46)))
            {
                Undo.RecordObject(m_asset, "修改普通怪");
                row.monsters = ParseMonsters(monstersText);
                EditorUtility.SetDirty(m_asset);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            return remove;
        }

        // 功能：画底部状态与错误清单。
        // 入参：无。
        // 返回含义：无。
        // 步骤：有状态就按成功/失败选消息类型；有错误就逐条列出来。
        // 上游/下游：上游是 OnGUI；下游是 EditorGUILayout。
        private void DrawStatus()
        {
            if (!string.IsNullOrEmpty(m_status))
            {
                EditorGUILayout.HelpBox(m_status, m_statusIsError ? MessageType.Error : MessageType.Info);
            }

            for (int i = 0; i < m_errors.Count; i++)
            {
                EditorGUILayout.HelpBox(m_errors[i], MessageType.Warning);
            }
        }

        // 功能：跑一次校验并刷新错误列表。
        // 入参：无。
        // 返回含义：无。
        // 步骤：清空错误 → 调桥校验 → 通过就把状态写成通过。
        // 上游/下游：上游是工具栏与导出流程；下游是 DungeonCsvBridge.Validate。
        private void RunValidate()
        {
            m_errors.Clear();

            if (m_asset == null)
            {
                return;
            }

            if (DungeonCsvBridge.Validate(m_asset, m_errors))
            {
                m_status = "校验通过";
                m_statusIsError = false;
            }
            else
            {
                m_status = "校验没通过，共 " + m_errors.Count + " 个问题";
                m_statusIsError = true;
            }
        }

        // 功能：导入前弹一次确认，避免误覆盖资产。
        // 入参：无。
        // 返回含义：无。
        // 步骤：弹对话框 → 用户点「覆盖」才调 Import。
        // 上游/下游：上游是导入按钮；下游是 DungeonCsvBridge.Import。
        private void ImportWithConfirm()
        {
            bool confirmed = EditorUtility.DisplayDialog(
                "从 CSV 导入",
                "会用 " + DungeonCsvBridge.CsvRelativePath + " 覆盖当前资产的 " + m_asset.rows.Count + " 行，继续吗？",
                "覆盖",
                "取消");

            if (!confirmed)
            {
                m_status = "已取消导入";
                m_statusIsError = false;
                return;
            }

            bool ok = DungeonCsvBridge.Import(m_asset, out string message);
            m_status = message;
            m_statusIsError = !ok;
            RunValidate();
        }

        // 功能：新建一行默认数据，主键取现有最大值加一。
        // 入参：无。
        // 返回含义：新建的数据行。
        // 步骤：扫一遍现有主键求最大值，再填默认名称、人数、半径。
        // 上游/下游：上游是「增加一行」按钮；下游是无。
        private Config_Dungeon NewRow()
        {
            int nextId = 1001;

            for (int i = 0; i < m_asset.rows.Count; i++)
            {
                if (m_asset.rows[i].id >= nextId)
                {
                    nextId = m_asset.rows[i].id + 1;
                }
            }

            return new Config_Dungeon
            {
                id = nextId,
                name = "新副本",
                maxPlayers = 4,
                spawnRadiusMm = 3000,
                monsters = new int[0],
                bossId = 0,
            };
        }

        // 功能：把逗号分隔的文本解析成怪物编号数组。
        // 入参：text 是用户输入的文本。
        // 返回含义：整数数组，空文本给空数组。
        // 步骤：切逗号后逐个 int.TryParse，解析不了当 0 交给校验去报。
        // 上游/下游：上游是 DrawRow 的「应用」按钮；下游是无。
        private static int[] ParseMonsters(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new int[0];
            }

            string[] parts = text.Split(',');
            int[] result = new int[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                result[i] = int.TryParse(parts[i].Trim(), out int value) ? value : 0;
            }

            return result;
        }
    }
}
