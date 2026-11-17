// 这个文件里有：ConfigTableWindow（通用配置表窗口：任意表的 SO 与 CSV 双向编辑）。
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    // 功能：一个窗口管理全部配置表——左上角选表，中间编辑单元格，工具栏导出/导入。
    // 主要数据成员：m_tableIndex 当前表序号、m_asset 当前 SO、m_rows 行列表、m_columns 列定义。
    // 主要外部方法：ShowWindow 菜单入口、Load 换表/刷新、DrawToolbar、DrawTable。
    // 谁在用它：开发者通过菜单 Tools/NBC/配置表/配置表（SO ↔ CSV）打开。
    public sealed class ConfigTableWindow : EditorWindow
    {
        // 单元格输入缓冲的键分隔符，行号与列号用冒号拼成字符串
        private const char BufferKeySeparator = ':';
        // 资产被外部重新导入的轮询间隔（秒），太快会每帧 LoadAssetAtPath
        private const double AssetCheckIntervalSeconds = 1.0;

        // 当前选中的表序号，0 表示第一张表
        private int m_tableIndex;
        // 当前表的 SO，换表或刷新时重新加载
        private ScriptableObject m_asset;
        // 当前表的行列表（反射自 SO 的 rows），编辑它等于编辑资产
        private IList m_rows;
        // 当前表的列定义，来自 CSV 前 4 行
        private List<ConfigColumn> m_columns = new List<ConfigColumn>();
        // 列定义读取失败时的原因，非空表示这张表不能用
        private string m_columnError = string.Empty;
        // 单元格输入缓冲：解析失败时保留玩家输入的原文，键是「行号:列号」
        private readonly Dictionary<string, string> m_buffers = new Dictionary<string, string>(StringComparer.Ordinal);
        // 上一次轮询资产的时间，秒
        private double m_nextAssetCheckTime;
        // 是否显示「列说明」折叠区
        private bool m_showColumns;
        // 表格滚动位置
        private Vector2 m_scroll;
        // 工具栏下方的状态文本，成功/失败都写这里
        private string m_status = string.Empty;
        // 校验错误的滚动位置

        // 功能：菜单入口，打开或聚焦窗口。
        // 入参：无。
        // 返回含义：无。
        // 步骤：GetWindow 取已有窗口（没有就建）→ Show。
        // 上游/下游：Unity 菜单调用。
        [MenuItem("Tools/NBC/配置表/配置表（SO ↔ CSV）", false, 0)]
        private static void ShowWindow()
        {
            ConfigTableWindow window = GetWindow<ConfigTableWindow>("配置表");
            window.minSize = new Vector2(760f, 420f);
            window.Show();
        }

        // 功能：窗口第一次显示时绑定默认表。
        // 入参：无。
        // 返回含义：无。
        // 步骤：加载第 0 张表。
        // 上游/下游：Unity 调用。
        private void OnEnable()
        {
            Load();
        }

        // 功能：窗口获得焦点时刷新一次，避免外部改过 CSV 后看不到。
        // 入参：无。
        // 返回含义：无。
        // 步骤：直接 Load。
        // 上游/下游：Unity 调用。
        private void OnFocus()
        {
            Load();
        }

        // 功能：画整个窗口。
        // 入参：无。
        // 返回含义：无。
        // 步骤：工具栏 → 脏提示 → 列说明 → 表格 → 状态。
        // 上游/下游：Unity OnGUI 调用。
        private void OnGUI()
        {
            CheckAssetReplaced();
            DrawToolbar();

            if (m_asset == null || m_rows == null)
            {
                EditorGUILayout.HelpBox(m_columnError.Length > 0 ? m_columnError : "这张表还没有加载成功。", MessageType.Error);
                return;
            }

            DrawDirtyBanner();
            DrawColumnHelp();
            DrawTable();
            DrawStatus();
        }

        // 功能：画顶部工具栏（选表、刷新、校验、导出、导入）。
        // 入参：无。
        // 返回含义：无。
        // 步骤：表下拉 → 四个按钮 → 行数统计。
        // 上游/下游：OnGUI 调用。
        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            string[] names = new string[ConfigTableRegistry.All.Count];

            for (int i = 0; i < names.Length; i++)
            {
                names[i] = ConfigTableRegistry.All[i].Name;
            }

            int picked = EditorGUILayout.Popup(m_tableIndex, names, EditorStyles.toolbarPopup, GUILayout.Width(160f));

            if (picked != m_tableIndex)
            {
                m_tableIndex = picked;
                Load();
            }

            if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(56f)))
            {
                Load();
            }

            if (GUILayout.Button("校验", EditorStyles.toolbarButton, GUILayout.Width(56f)))
            {
                ValidateNow();
            }

            if (GUILayout.Button("导出到 CSV", EditorStyles.toolbarButton, GUILayout.Width(96f)))
            {
                ExportNow();
            }

            if (GUILayout.Button("从 CSV 导入", EditorStyles.toolbarButton, GUILayout.Width(96f)))
            {
                ImportNow();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label("行数：" + (m_rows == null ? 0 : m_rows.Count), EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        // 功能：红字提示「SO 与 CSV 不一致」。
        // 入参：无。
        // 返回含义：无。
        // 步骤：IsDirty → 有改动就画 Warning。
        // 上游/下游：OnGUI 调用。
        private void DrawDirtyBanner()
        {
            ConfigTableEntry entry = CurrentEntry;

            if (entry == null)
            {
                return;
            }

            bool dirty = ConfigTableBridge.IsDirty(entry, m_asset, out string detail);

            if (dirty)
            {
                EditorGUILayout.HelpBox("SO 与 CSV 不一致：" + detail + "\n「导出到 CSV」会保留 SO 的改动；「从 CSV 导入」会丢弃 SO 的改动。", MessageType.Warning);
            }
        }

        // 功能：可折叠的「列说明」，直接展示 CSV 表头的字段名/类型/规则。
        // 入参：无。
        // 返回含义：无。
        // 步骤：Foldout → 每列一行。
        // 上游/下游：OnGUI 调用。
        private void DrawColumnHelp()
        {
            m_showColumns = EditorGUILayout.Foldout(m_showColumns, "列说明（来自 CSV 表头）", true);

            if (!m_showColumns)
            {
                return;
            }

            EditorGUI.indentLevel++;

            foreach (ConfigColumn column in m_columns)
            {
                EditorGUILayout.LabelField(column.Label, column.Name + " · " + column.Type + " · " + column.Rule);
            }

            EditorGUI.indentLevel--;
        }

        // 功能：画表头与所有行，支持增删行。
        // 入参：无。
        // 返回含义：无。
        // 步骤：横向表头 → 滚动列表 → 每行删按钮 + 单元格 → 底部「增加一行」。
        // 上游/下游：OnGUI 调用。
        private void DrawTable()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label("操作", EditorStyles.miniLabel, GUILayout.Width(48f));

            foreach (ConfigColumn column in m_columns)
            {
                GUILayout.Label(new GUIContent(column.Label, column.Name + " · " + column.Type), EditorStyles.miniLabel, GUILayout.Width(ColumnWidth(column)));
            }

            EditorGUILayout.EndHorizontal();

            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

            for (int rowIndex = 0; rowIndex < m_rows.Count; rowIndex++)
            {
                object row = m_rows[rowIndex];
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("删", GUILayout.Width(44f)))
                {
                    RemoveRow(rowIndex);
                    EditorGUILayout.EndHorizontal();
                    break;
                }

                foreach (ConfigColumn column in m_columns)
                {
                    DrawCell(rowIndex, row, column);
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("增加一行", GUILayout.Height(24f)))
            {
                AddRow();
            }
        }

        // 功能：画一个可编辑单元格。
        // 入参：rowIndex 行号；row 行对象；column 列定义。
        // 返回含义：无。
        // 步骤：取缓冲或格式化值 → TextField → 有变化就解析写回。
        // 上游/下游：DrawTable 调用。
        private void DrawCell(int rowIndex, object row, ConfigColumn column)
        {
            string key = rowIndex + BufferKeySeparator.ToString() + column.Name;
            string shown = m_buffers.TryGetValue(key, out string buffer)
                ? buffer
                : ConfigTableBridge.GetCellText(CurrentEntry, row, column);

            string edited = EditorGUILayout.TextField(shown, GUILayout.Width(ColumnWidth(column)));

            if (string.Equals(edited, shown, StringComparison.Ordinal))
            {
                return;
            }

            m_buffers[key] = edited;

            if (ConfigTableBridge.TrySetCell(CurrentEntry, row, column, edited, out string problem))
            {
                m_buffers.Remove(key);
                MarkDirty();
                m_status = "已修改 " + column.Label + "（尚未导出）";
            }
            else
            {
                m_status = "「" + column.Label + "」" + problem;
            }
        }

        // 功能：画底部状态与校验错误列表。
        // 入参：无。
        // 返回含义：无。
        // 步骤：状态文本 → 有错误就滚动显示。
        // 上游/下游：OnGUI 调用。
        private void DrawStatus()
        {
            if (m_status.Length > 0)
            {
                EditorGUILayout.HelpBox(m_status, MessageType.Info);
            }
        }

        // 功能：当前选中的表登记。
        // 入参：无。
        // 返回含义：登记项；序号越界返回 null。
        // 步骤：按 m_tableIndex 取 All。
        // 上游/下游：窗口各方法使用。
        private ConfigTableEntry CurrentEntry
        {
            get
            {
                if (m_tableIndex < 0 || m_tableIndex >= ConfigTableRegistry.All.Count)
                {
                    return null;
                }

                return ConfigTableRegistry.All[m_tableIndex];
            }
        }

        // 功能：按列类型给一个合适的编辑宽度。
        // 入参：column 列定义。
        // 返回含义：像素宽度。
        // 步骤：数组列宽一些，其余 120。
        // 上游/下游：DrawTable、DrawCell 调用。
        private static float ColumnWidth(ConfigColumn column)
        {
            return column.Type.EndsWith("[]", StringComparison.Ordinal) ? 180f : 120f;
        }

        // 功能：加载当前表的 SO、行与列定义。
        // 入参：无。
        // 返回含义：无。
        // 步骤：清缓冲 → 按登记读资产 → ReadColumns → 失败写 m_columnError。
        // 上游/下游：OnEnable、OnFocus、换表、刷新、导入后调用。
        private void Load()
        {
            m_buffers.Clear();
            m_status = string.Empty;
            m_columns.Clear();
            m_columnError = string.Empty;
            m_rows = null;
            m_asset = null;

            ConfigTableEntry entry = CurrentEntry;

            if (entry == null)
            {
                return;
            }

            m_columns = ConfigTableBridge.ReadColumns(entry, out m_columnError);

            if (m_columnError.Length > 0)
            {
                return;
            }

            m_asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(entry.AssetPath);

            if (m_asset == null)
            {
                m_columnError = "找不到资产 " + entry.AssetPath + "（先在菜单里跑一次「导入 TSV → ScriptableObject」）";
                return;
            }

            m_rows = ConfigTableBridge.GetRows(m_asset);

            if (m_rows == null)
            {
                m_columnError = entry.AssetPath + " 上没有 rows 字段（生成的 SO 类型不对）";
            }
        }

        // 功能：校验当前表并把错误显示出来。
        // 入参：无。
        // 返回含义：无。
        // 步骤：Validate → 无错记成功，有错拼成多行。
        // 上游/下游：工具栏「校验」调用。
        private void ValidateNow()
        {
            List<string> errors = new List<string>();
            bool ok = ConfigTableBridge.Validate(CurrentEntry, m_rows, errors);

            if (ok)
            {
                m_status = "校验通过：" + CurrentEntry.Name + "（" + m_rows.Count + " 行）";
                return;
            }

            m_status = "校验没通过（" + errors.Count + " 条）：\n" + string.Join("\n", errors);
        }

        // 功能：导出并跑 ConfigKit。
        // 入参：无。
        // 返回含义：无。
        // 步骤：Export → 状态写结果 → 成功就重新加载。
        // 上游/下游：工具栏「导出到 CSV」调用。
        private void ExportNow()
        {
            if (!ConfigTableBridge.Export(CurrentEntry, m_asset, out string message))
            {
                m_status = "导出失败：" + message;
                return;
            }

            m_status = "导出成功。\n" + message;
            Load();

            // 注意：导出后 CSV 可能又被 Excel 改掉（用户忘了关表格），这时红字会重新亮起，
            // 必须把「第一处不同」写进状态栏，否则用户只会看到「导出了但还红着」。
            if (ConfigTableBridge.IsDirty(CurrentEntry, m_asset, out _))
            {
                m_status += "\n但 CSV 随后又被改动：" + ConfigTableBridge.DescribeFirstDifference(CurrentEntry, m_asset);
            }
        }

        // 功能：从 CSV 覆盖 SO。
        // 入参：无。
        // 返回含义：无。
        // 步骤：先确认（会丢 SO 改动）→ Import → 成功重新加载。
        // 上游/下游：工具栏「从 CSV 导入」调用。
        private void ImportNow()
        {
            bool dirty = ConfigTableBridge.IsDirty(CurrentEntry, m_asset, out _);

            if (dirty && !EditorUtility.DisplayDialog(
                    "从 CSV 导入",
                    "SO 里有未导出的改动，导入会把它们丢掉。继续？",
                    "继续导入",
                    "取消"))
            {
                return;
            }

            if (!ConfigTableBridge.Import(CurrentEntry, m_asset, out string message))
            {
                m_status = "导入失败：" + message;
                return;
            }

            m_status = "导入成功。\n" + message;
            Load();
        }

        // 功能：增加一行并选中它。
        // 入参：无。
        // 返回含义：无。
        // 步骤：CreateRow → 加到 rows → 标记脏。
        // 上游/下游：DrawTable 调用。
        private void AddRow()
        {
            object row = ConfigTableBridge.CreateRow(CurrentEntry);
            m_rows.Add(row);
            MarkDirty();
            m_status = "已增加一行（尚未导出）";
        }

        // 功能：删除指定行。
        // 入参：rowIndex 行号。
        // 返回含义：无。
        // 步骤：rows.RemoveAt → 清掉该行的缓冲 → 标记脏。
        // 上游/下游：DrawTable 调用。
        private void RemoveRow(int rowIndex)
        {
            m_rows.RemoveAt(rowIndex);

            foreach (string key in new List<string>(m_buffers.Keys))
            {
                if (key.StartsWith(rowIndex + BufferKeySeparator.ToString(), StringComparison.Ordinal))
                {
                    m_buffers.Remove(key);
                }
            }

            MarkDirty();
            m_status = "已删除一行（尚未导出）";
        }

        // 功能：把 SO 标记成已改动并保存资产。
        // 入参：无。
        // 返回含义：无。
        // 步骤：SetDirty → SaveAssets。
        // 上游/下游：单元格编辑、增删行调用。
        private void MarkDirty()
        {
            EditorUtility.SetDirty(m_asset);
            AssetDatabase.SaveAssets();
        }

        // 功能：发现资产被外部重新导入时自动刷新，避免继续编辑旧对象。
        // 入参：无。
        // 返回含义：无。
        // 步骤：每秒一次 → 按路径重新加载 → 对象变了就 Load。
        // 上游/下游：OnGUI 调用。
        private void CheckAssetReplaced()
        {
            if (EditorApplication.timeSinceStartup < m_nextAssetCheckTime)
            {
                return;
            }

            m_nextAssetCheckTime = EditorApplication.timeSinceStartup + AssetCheckIntervalSeconds;

            ConfigTableEntry entry = CurrentEntry;

            if (entry == null || m_asset == null)
            {
                return;
            }

            ScriptableObject current = AssetDatabase.LoadAssetAtPath<ScriptableObject>(entry.AssetPath);

            if (current != null && !ReferenceEquals(current, m_asset))
            {
                Load();
                m_status = "检测到资产被重新导入，已自动刷新";
            }
        }
    }
}
