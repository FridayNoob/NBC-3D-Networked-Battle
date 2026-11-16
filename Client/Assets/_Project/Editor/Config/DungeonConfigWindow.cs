// 这个文件里有：
//   DungeonConfigWindow（编辑器窗口）：带中文表头的副本表编辑器，导出 CSV、从 CSV 导入、校验

using System.Collections.Generic;
using NBC.Game.Config;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    // 主要功能：像表格一样编辑 DungeonConfig 的行，表头的中文名与规则直接读自 CSV 前 4 行。
    // 主要数据成员：m_asset 是配置资产，m_columns 是列说明，m_errors 是最近一次校验的问题。
    // 主要外部方法：无公开方法，入口是菜单 Tools/NBC/配置表/副本表（SO ↔ CSV）。
    // 谁在用它：负责人手工改副本配置；读写磁盘全部交给 DungeonCsvBridge。
    public sealed class DungeonConfigWindow : EditorWindow
    {
        // 资产文件名，拼在 ConfigImporter.AssetDirectory 后面
        private const string AssetFileName = "/DungeonConfig.asset";

        // 各列宽度，按 CSV 字段名取；没配的字段给默认宽度
        private const float IdWidth = 60f;

        // 名称列宽
        private const float NameWidth = 140f;

        // 人数列宽
        private const float PlayersWidth = 70f;

        // 出生半径列宽
        private const float RadiusWidth = 110f;

        // 怪物编号列宽
        private const float MonstersWidth = 240f;

        // BOSS 列宽
        private const float BossWidth = 70f;

        // 删除按钮列宽
        private const float RemoveWidth = 50f;

        // 当前编辑的资产，窗口打开时加载
        private DungeonConfig m_asset;

        // 列说明，读自 CSV 表头，加载资产时一并读
        private readonly List<CsvColumn> m_columns = new List<CsvColumn>();

        // 读表头失败的原因，空表示成功
        private string m_columnsError = string.Empty;

        // 行区域滚动位置
        private Vector2 m_scroll;

        // 最近一次操作的提示
        private string m_status = string.Empty;

        // 提示是不是错误，决定底色
        private bool m_statusIsError;

        // 最近一次校验的问题列表
        private readonly List<string> m_errors = new List<string>();

        // 怪物列的输入缓冲：按键过程中不能每帧从 row.monsters 重新拿文本，否则输入会被吞
        private readonly Dictionary<Config_Dungeon, string> m_monsterDrafts = new Dictionary<Config_Dungeon, string>();

        // 上一次检查「资产有没有被别的操作换掉」的时间，避免每帧都查
        private double m_assetCheckTime;

        // 资产被换掉的检查间隔，秒
        private const double AssetCheckInterval = 1.0;

        // 是否展开列说明
        private bool m_showColumnHelp;

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

        // 功能：窗口启用时加载资产与表头。
        // 入参：无。
        // 返回含义：无。
        // 步骤：先加载资产，再读 CSV 表头。
        // 上游/下游：上游是 Unity 窗口生命周期；下游是 LoadAsset、LoadColumns。
        private void OnEnable()
        {
            Reload();
        }

        // 功能：画整个窗口。
        // 入参：无。
        // 返回含义：无。
        // 步骤：说明行 → 工具栏 → 列说明 → 表头 → 数据行 → 状态。
        // 上游/下游：上游是 IMGUI；下游是各 DrawXxx。
        private void OnGUI()
        {
            CheckAssetReplaced();

            EditorGUILayout.HelpBox(
                "服务端只读 Configs/Design/Dungeon.csv。这里改的是 SO：红字=SO 与 CSV 不一致；「导出到 CSV」=把 SO 推给 CSV 并重新生成；「从 CSV 导入」=丢掉 SO 改动、以 CSV 为准。",
                MessageType.None);

            DrawToolbar();
            DrawColumnHelp();
            DrawTable();
            DrawStatus();
        }

        // 功能：发现资产被重新导入或被别的窗口替换时，自动重新加载，避免对着旧对象编辑。
        // 入参：无。
        // 返回含义：无。
        // 步骤：每秒最多查一次；按路径重新加载，拿到的对象不是当前对象就 Reload。
        // 上游/下游：上游是 OnGUI；下游是 Reload。
        private void CheckAssetReplaced()
        {
            if (EditorApplication.timeSinceStartup - m_assetCheckTime < AssetCheckInterval)
            {
                return;
            }

            m_assetCheckTime = EditorApplication.timeSinceStartup;

            if (m_asset == null)
            {
                return;
            }

            DungeonConfig current = AssetDatabase.LoadAssetAtPath<DungeonConfig>(ConfigImporter.AssetDirectory + AssetFileName);

            if (current != null && !ReferenceEquals(current, m_asset))
            {
                m_status = "检测到资产被重新导入，已自动刷新";
                m_statusIsError = false;
                Reload();
            }
        }

        // 功能：同时重新加载资产与列说明。
        // 入参：无。
        // 返回含义：无。
        // 步骤：先加载资产，再读表头，失败时把原因留着显示。
        // 上游/下游：上游是 OnEnable 与刷新按钮；下游是 AssetDatabase、DungeonCsvBridge.ReadColumns。
        private void Reload()
        {
            m_asset = AssetDatabase.LoadAssetAtPath<DungeonConfig>(ConfigImporter.AssetDirectory + AssetFileName);

            if (m_asset == null)
            {
                m_status = "读不到 " + ConfigImporter.AssetDirectory + AssetFileName + "，请先跑一次「导入 TSV → ScriptableObject」";
                m_statusIsError = true;
            }

            m_columns.Clear();
            m_columns.AddRange(DungeonCsvBridge.ReadColumns(out m_columnsError));
            m_monsterDrafts.Clear();
        }

        // 功能：画工具栏，含刷新、校验、导出、导入与脏提示。
        // 入参：无。
        // 返回含义：无。
        // 步骤：画按钮行 → 显示行数 → 显示脏提示。
        // 上游/下游：上游是 OnGUI；下游是桥的四个方法。
        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("刷新", GUILayout.Width(60)))
            {
                Reload();
                m_status = "已重新读取资产与表头";
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

            if (!string.IsNullOrEmpty(m_columnsError))
            {
                EditorGUILayout.HelpBox(m_columnsError, MessageType.Error);
            }
        }

        // 功能：折叠显示每一列的中文名、字段名、类型与规则。
        // 入参：无。
        // 返回含义：无。
        // 步骤：用 Foldout 控制展开，展开后逐列画一行说明。
        // 上游/下游：上游是 OnGUI；下游是 m_columns。
        private void DrawColumnHelp()
        {
            m_showColumnHelp = EditorGUILayout.Foldout(m_showColumnHelp, "列说明（来自 CSV 表头）", true);

            if (!m_showColumnHelp)
            {
                return;
            }

            for (int i = 0; i < m_columns.Count; i++)
            {
                CsvColumn column = m_columns[i];
                string rule = string.IsNullOrWhiteSpace(column.Rule) ? "无规则" : column.Rule;
                EditorGUILayout.LabelField(column.Label + "（" + column.Name + "）", column.Type + " · " + rule);
            }
        }

        // 功能：画表头与所有数据行。
        // 入参：无。
        // 返回含义：无。
        // 步骤：无资产直接返回；先画表头行，再在滚动区画数据行，删除延后执行。
        // 上游/下游：上游是 OnGUI；下游是 DrawHeader、DrawRow。
        private void DrawTable()
        {
            if (m_asset == null)
            {
                return;
            }

            DrawHeader();
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
                m_monsterDrafts.Remove(m_asset.rows[removeIndex]);
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

        // 功能：画表头一行，中文名来自 CSV 第二行，鼠标悬停显示字段名与规则。
        // 入参：无。
        // 返回含义：无。
        // 步骤：按固定列宽依次画 Label，样式用 boldLabel。
        // 上游/下游：上游是 DrawTable；下游是 LabelFor。
        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            LabelFor("id", IdWidth);
            LabelFor("name", NameWidth);
            LabelFor("maxPlayers", PlayersWidth);
            LabelFor("spawnRadiusMm", RadiusWidth);
            LabelFor("monsters", MonstersWidth);
            LabelFor("bossId", BossWidth);
            GUILayout.Label(string.Empty, GUILayout.Width(RemoveWidth));

            EditorGUILayout.EndHorizontal();
        }

        // 功能：按字段名找中文名并画成表头。
        // 入参：fieldName 是 CSV 字段名；width 是列宽。
        // 返回含义：无。
        // 步骤：在 m_columns 里找同名字段，找不到就用字段名本身；tooltip 带类型与规则。
        // 上游/下游：上游是 DrawHeader；下游是 GUILayout.Label。
        private void LabelFor(string fieldName, float width)
        {
            string label = fieldName;
            string tooltip = string.Empty;

            for (int i = 0; i < m_columns.Count; i++)
            {
                if (!string.Equals(m_columns[i].Name, fieldName, System.StringComparison.Ordinal))
                {
                    continue;
                }

                CsvColumn column = m_columns[i];
                label = string.IsNullOrWhiteSpace(column.Label) ? fieldName : column.Label;
                tooltip = column.Name + " · " + column.Type + " · " + (string.IsNullOrWhiteSpace(column.Rule) ? "无规则" : column.Rule);
                break;
            }

            GUILayout.Label(new GUIContent(label, tooltip), EditorStyles.boldLabel, GUILayout.Width(width));
        }

        // 功能：画一行数据，返回是否点了删除。
        // 入参：row 是数据行；index 是行号。
        // 返回含义：true 表示要删除这一行。
        // 步骤：单行横向排列六个字段和删除按钮；怪物用文本框加「应用」。
        // 上游/下游：上游是 DrawTable；下游是 ParseMonsters。
        private bool DrawRow(Config_Dungeon row, int index)
        {
            bool remove = false;

            EditorGUILayout.BeginHorizontal();

            if (!m_monsterDrafts.TryGetValue(row, out string draft))
            {
                draft = string.Join(",", row.monsters);
            }

            EditorGUI.BeginChangeCheck();
            int id = EditorGUILayout.IntField(row.id, GUILayout.Width(IdWidth));
            string name = EditorGUILayout.TextField(row.name, GUILayout.Width(NameWidth));
            int maxPlayers = EditorGUILayout.IntField(row.maxPlayers, GUILayout.Width(PlayersWidth));
            int spawnRadiusMm = EditorGUILayout.IntField(row.spawnRadiusMm, GUILayout.Width(RadiusWidth));
            string monstersText = EditorGUILayout.TextField(draft, GUILayout.Width(MonstersWidth - 50f));
            bool applyMonsters = GUILayout.Button("应用", GUILayout.Width(46f));
            int bossId = EditorGUILayout.IntField(row.bossId, GUILayout.Width(BossWidth));
            bool fieldsChanged = EditorGUI.EndChangeCheck();

            if (fieldsChanged || applyMonsters)
            {
                Undo.RecordObject(m_asset, "修改副本行");
                row.id = id;
                row.name = name;
                row.maxPlayers = maxPlayers;
                row.spawnRadiusMm = spawnRadiusMm;
                row.bossId = bossId;

                if (applyMonsters)
                {
                    row.monsters = ParseMonsters(monstersText);
                    m_monsterDrafts[row] = string.Join(",", row.monsters);
                }
                else
                {
                    m_monsterDrafts[row] = monstersText;
                    row.monsters = ParseMonsters(monstersText);
                }

                EditorUtility.SetDirty(m_asset);
            }

            if (GUILayout.Button("删除", GUILayout.Width(RemoveWidth)))
            {
                remove = true;
            }

            EditorGUILayout.EndHorizontal();
            return remove;
        }

        // 功能：画底部状态与错误清单。
        // 入参：无。
        // 返回含义：无。
        // 步骤：有状态按成功/失败选类型；错误逐条列。
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
        // 步骤：清空 → 调桥 → 写状态。
        // 上游/下游：上游是工具栏与导出；下游是 DungeonCsvBridge.Validate。
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

        // 功能：导入前弹确认框，避免误覆盖。
        // 入参：无。
        // 返回含义：无。
        // 步骤：确认后调桥导入，再校验一次。
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
        // 返回含义：新行。
        // 步骤：扫最大主键，填默认值。
        // 上游/下游：上游是「增加一行」；下游是无。
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

        // 功能：把逗号分隔文本解析成怪物编号数组。
        // 入参：text 用户输入。
        // 返回含义：整数数组。
        // 步骤：切逗号逐个 TryParse，失败当 0 交给校验报。
        // 上游/下游：上游是 DrawRow；下游是无。
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
