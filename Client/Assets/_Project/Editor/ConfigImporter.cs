// ============================================================================
//  ConfigImporter —— 把 ConfigKit 生成的 .tsv 灌进 ScriptableObject 资产
//  项目：3D联网战斗Demo   对应：需求 8（Excel → SO）、Docs\17-配置表规范.md §九
//
//  ---------------------------------------------------------------------------
//  它在整条流水线里的位置
//  ---------------------------------------------------------------------------
//      Excel(.xlsx) ──ConfigKit.Cli──► Config_<表>.cs（类型）
//                                      <表>Config.cs（SO 类型 + LoadFromTsv）
//                                      <表>.tsv      （数据）   ← ★ 本脚本读它
//
//  ⚠️ **为什么读 .tsv 而不是 .json**：见 Tools\ConfigKit\README.md §七。
//     一句话：Unity 的序列化器不支持可空值类型、`JsonUtility` 里枚举按整数走，
//     而这两条在本机无法实测；TSV + 生成的显式加载器把这两个问题都绕开了，
//     而且不需要任何解析库。JSON 那份产物是给**外部工具/人**看的。
//
//  ---------------------------------------------------------------------------
//  为什么这里必须用反射
//  ---------------------------------------------------------------------------
//  `<表>Config` 是**生成出来的**类型 —— 本脚本编译时它可能还不存在，
//  所以只能按名字找。这不是偷懒，是"生成物↔消费者"必须解耦的必然结果
//  （ConfigKit.Cli 生成 → Unity 编译 → 本脚本按名字装配）。
//
//  ⚠️ 反射只在**编辑器**里用，不影响打包产物（Editor 脚本不进包），
//     所以不存在 IL2CPP/AOT 那类问题。
//
//  ---------------------------------------------------------------------------
//  用法（菜单）
//  ---------------------------------------------------------------------------
//      Tools/NBC/配置表/导入 TSV → ScriptableObject
//      Tools/NBC/配置表/检查（只报告，不写资产）
//
//  ⚠️ 前提：先在命令行跑过 ConfigKit.Cli（生成 .cs 与 .tsv），
//     并且 Unity 已经把这些 .cs 编译过一遍 —— 否则本脚本找不到类型，
//     它会**明确告诉你去跑哪条命令**，而不是静默什么都不做。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    /// <summary>把 ConfigKit 生成的 .tsv 导入成 &lt;表&gt;Config 资产。</summary>
    public static class ConfigImporter
    {
        /// <summary>生成的 .tsv 所在目录（与 ConfigKit.Cli 的 out 参数默认值一致）。</summary>
        public const string TsvDirectory = "Assets/_Project/Game/Config/Generated";

        /// <summary>生成出来的 SO 资产放在哪。</summary>
        public const string AssetDirectory = "Assets/_Project/Configs";

        private const string LoadMethodName = "LoadFromTsv";
        private const string TypeSuffix = "Config";

        /// <summary>导入全部表（写资产）。</summary>
        [MenuItem("Tools/NBC/配置表/导入 TSV → ScriptableObject")]
        private static void ImportAll()
        {
            Run(true);
        }

        /// <summary>只检查、不写资产（先看看会动哪些文件）。</summary>
        [MenuItem("Tools/NBC/配置表/检查（只报告，不写资产）")]
        private static void CheckOnly()
        {
            Run(false);
        }

        // ====================================================================
        //  主流程
        // ====================================================================

        /// <summary>跑一遍。</summary>
        /// <param name="write">true = 真的写资产；false = 只报告。</param>
        private static void Run(bool write)
        {
            string absolute = ToAbsolute(TsvDirectory);

            if (!Directory.Exists(absolute))
            {
                Debug.LogError(
                    "[ConfigImporter] 找不到目录 " + TsvDirectory + "。\n" +
                    "先在命令行生成一次：\n" +
                    "  dotnet build Tools\\ConfigKit\\src\\ConfigKit.Cli\\ConfigKit.Cli.csproj -m:1\n" +
                    "  Tools\\ConfigKit\\src\\ConfigKit.Cli\\bin\\Debug\\net8.0\\NBC.ConfigKit.Cli.exe " +
                    "--source <你的表目录> --out " + TsvDirectory);
                return;
            }

            string[] files = Directory.GetFiles(absolute, "*.tsv");
            Array.Sort(files, StringComparer.Ordinal);

            if (files.Length == 0)
            {
                Debug.LogWarning("[ConfigImporter] " + TsvDirectory + " 里没有 .tsv 文件。");
                return;
            }

            if (write)
            {
                Directory.CreateDirectory(ToAbsolute(AssetDirectory));
            }

            List<string> done = new List<string>();
            List<string> failed = new List<string>();

            for (int i = 0; i < files.Length; i++)
            {
                string tableName = Path.GetFileNameWithoutExtension(files[i]);

                try
                {
                    if (ImportOne(tableName, File.ReadAllText(files[i], Encoding.UTF8), write))
                    {
                        done.Add(tableName);
                    }
                }
                catch (Exception exception)
                {
                    failed.Add(tableName + "：" + Unwrap(exception).Message);
                }
            }

            if (write)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            Debug.Log(Render(done, failed, write));
        }

        /// <summary>导入一张表。返回"是否真的处理了"（false = 跳过，不算失败）。</summary>
        /// <param name="tableName">表名。</param>
        /// <param name="tsv">tsv 文本。</param>
        /// <param name="write">是否写资产。</param>
        /// <returns>处理了没有。</returns>
        private static bool ImportOne(string tableName, string tsv, bool write)
        {
            string soTypeName = tableName + TypeSuffix;

            // ⚠️ 找类型时**必须同时要求它有 LoadFromTsv(string)** —— 见 FindImportableType 的说明：
            //    只按"名字 + 是 ScriptableObject"找会**随机挑到同名类型**（测试里的假类型就撞过名）。
            string diagnostic;
            Type soType = FindImportableType(soTypeName, out diagnostic);

            if (soType == null)
            {
                Debug.LogError(
                    "[ConfigImporter] 找不到可用的类型 " + soTypeName + "。\n" + diagnostic);
                return false;
            }

            // 这里不再判 `LoadFromTsv` 找没找到：`FindImportableType` **就是按它筛的**，
            // 所以再判一次是**永远走不到的分支**（那种分支比没有分支更糟，见 M2-B 的教训）。
            MethodInfo load = GetLoadMethod(soType);

            string assetPath = AssetDirectory + "/" + soTypeName + ".asset";
            ScriptableObject asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            bool created = false;

            if (asset == null)
            {
                if (!write)
                {
                    Debug.Log("[ConfigImporter] （只报告）会**新建**资产 " + assetPath);
                    return true;
                }

                asset = ScriptableObject.CreateInstance(soType);

                if (asset == null)
                {
                    Debug.LogError("[ConfigImporter] CreateInstance(" + soTypeName + ") 返回了 null。");
                    return false;
                }

                AssetDatabase.CreateAsset(asset, assetPath);
                created = true;
            }

            if (!write)
            {
                // ⚠️ 2026-09-26 修：只报告模式原来**只验证"TSV 能不能导入"**，从不与现有资产比对 ——
                //    于是"手改了 SO"这类问题**永远抓不到**。负责人就撞上了：他在 Unity 的
                //    `MonsterConfig.asset` 里把狼王血量改成 1000，测试时狼王还是 2000
                //    （因为**服务端读的是 `Configs\Design\Monster.csv` 真源**，它看不到 SO）。
                //    注意"比时间"也抓不到：资产是**后改**的，比源表更新，`ConfigStalenessCheck`
                //    那类按 mtime 的检查会放过它。⇒ 必须**逐字段比内容**。
                ScriptableObject fresh = ScriptableObject.CreateInstance(soType);
                load.Invoke(fresh, new object[] { tsv });

                List<string> diffs = CompareRows(asset, fresh);

                UnityEngine.Object.DestroyImmediate(fresh);

                if (diffs.Count == 0)
                {
                    Debug.Log("[ConfigImporter] （只报告）" + assetPath + " **与源表一致**");
                    return true;
                }

                Debug.LogError("[ConfigImporter] （只报告）" + assetPath + " **与源表不一致**（"
                    + diffs.Count + " 处）。\n"
                    + "⚠️ 资产是**生成物**：手改它会被下次「导入 TSV」覆盖，而且**服务端读的是源 CSV**\n"
                    + "   （`Configs\\Design\\*.csv`）—— 改资产对服务端**没有任何影响**。\n"
                    + "   要改数值请改源 CSV，然后：跑 ConfigKit → 点「导入 TSV → ScriptableObject」。\n"
                    + "   差异：\n  " + string.Join("\n  ", diffs.ToArray()));
                return false;
            }

            // 反射调用生成的加载器（它按**字段名**对列，所以调换列顺序不会错位）
            load.Invoke(asset, new object[] { tsv });

            EditorUtility.SetDirty(asset);

            if (created)
            {
                Debug.Log("[ConfigImporter] 新建 " + assetPath + "（" + CountRows(asset) + " 行）");
            }

            return true;
        }

        // ====================================================================
        //  查找与报告
        // ====================================================================

        /// <summary>
        /// 逐字段比较两个资产的数据行（**只报告模式**用）。
        /// <para>⚠️ 为什么比内容而不是比时间：资产是**生成物**，手改之后它比源表**更新** ——
        /// 任何"比 mtime"的检查（如 `ConfigStalenessCheck`）都会放过它。
        /// 而"改了却没生效"这类问题**不报错**，只能靠把两份内容摆在一起看。</para>
        /// <para>用 `id` 建索引再比，所以**行顺序变了不算差异**（生成器的加表顺序可能变）。</para>
        /// </summary>
        /// <param name="asset">现有资产。</param>
        /// <param name="fresh">用同一份 TSV 重新灌出来的新实例。</param>
        /// <returns>差异（人话；空 = 一致）。</returns>
        private static List<string> CompareRows(ScriptableObject asset, ScriptableObject fresh)
        {
            var diffs = new List<string>();

            FieldInfo rowsField = asset.GetType().GetField("rows", BindingFlags.Public | BindingFlags.Instance);

            if (rowsField == null)
            {
                diffs.Add("类型 " + asset.GetType().Name + " 上没有 `rows` 字段（生成物变了？）");
                return diffs;
            }

            var oldRows = rowsField.GetValue(asset) as System.Collections.IEnumerable;
            var newRows = rowsField.GetValue(fresh) as System.Collections.IEnumerable;

            if (oldRows == null || newRows == null)
            {
                diffs.Add("读不到 `rows`（资产里是 null？）");
                return diffs;
            }

            var oldById = new Dictionary<int, object>();
            var seen = new HashSet<int>();
            FieldInfo idField = null;

            foreach (object row in oldRows)
            {
                if (idField == null)
                {
                    idField = row.GetType().GetField("id", BindingFlags.Public | BindingFlags.Instance);
                }

                if (idField == null)
                {
                    diffs.Add("行类型 " + row.GetType().Name + " 上没有 `id` 字段");
                    return diffs;
                }

                oldById[(int)idField.GetValue(row)] = row;
            }

            foreach (object row in newRows)
            {
                int id = (int)idField.GetValue(row);
                seen.Add(id);

                object existing;

                if (!oldById.TryGetValue(id, out existing))
                {
                    diffs.Add("资产里**缺少** id=" + id + "（源表有）");
                    continue;
                }

                foreach (FieldInfo field in row.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (field.Name == "id")
                    {
                        continue;
                    }

                    object assetValue = field.GetValue(existing);
                    object sourceValue = field.GetValue(row);

                    if (!SameFieldValue(assetValue, sourceValue))
                    {
                        diffs.Add("id=" + id + " 字段 `" + field.Name + "`：资产=" + DescribeValue(assetValue)
                            + "，源表=" + DescribeValue(sourceValue));
                    }
                }
            }

            foreach (KeyValuePair<int, object> pair in oldById)
            {
                if (!seen.Contains(pair.Key))
                {
                    diffs.Add("资产里**多了** id=" + pair.Key + "（源表没有）");
                }
            }

            return diffs;
        }

        /// <summary>两个字段值是否相同（数组按元素比 —— 生成物里有 `int[] skillIds` 这类字段）。</summary>
        /// <param name="a">甲。</param>
        /// <param name="b">乙。</param>
        /// <returns>相同返回 true。</returns>
        private static bool SameFieldValue(object a, object b)
        {
            var arrayA = a as Array;
            var arrayB = b as Array;

            if (arrayA == null || arrayB == null)
            {
                return Equals(a, b);
            }

            if (arrayA.Length != arrayB.Length)
            {
                return false;
            }

            for (int i = 0; i < arrayA.Length; i++)
            {
                if (!Equals(arrayA.GetValue(i), arrayB.GetValue(i)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>把字段值写成人话（数组写成 `[1,2]`；null 写成 `（null）`）。</summary>
        /// <param name="value">值。</param>
        /// <returns>人话。</returns>
        private static string DescribeValue(object value)
        {
            if (value == null)
            {
                return "（null）";
            }

            var array = value as Array;

            if (array == null)
            {
                return value.ToString();
            }

            var text = new StringBuilder("[");

            for (int i = 0; i < array.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                text.Append(array.GetValue(i));
            }

            return text.Append(']').ToString();
        }

        /// <summary>
        /// 在所有已加载程序集里找**可导入**的 &lt;表&gt;Config。
        ///
        /// ------------------------------------------------------------------
        /// ⚠️ 为什么"按名字找"不够（2026-09-23 真踩到，负责人点菜单报错）
        /// ------------------------------------------------------------------
        /// 原来的实现是"**任意程序集里第一个**同名且是 ScriptableObject 的类型"。
        /// 工程里出现**同名类型**之后就变成了**随机挑**：
        ///
        ///     · `NBC.Game.Config.HeroConfig`（生成物，有 `LoadFromTsv`）
        ///     · `NBC.Tests.EditMode.ConfigMgrTests+HeroConfig`（测试里的假类型，**没有**）
        ///
        /// 测试程序集在编辑器里**是加载着的**，而 `GetAssemblies()` 的顺序不保证，
        /// 于是点导入菜单会报"H​eroConfig 上没有 LoadFromTsv(string)" ——
        /// 报错还误导人去重跑 ConfigKit（其实生成物是好的）。
        /// 现象很能说明问题：当时**只有 HeroConfig / SkillConfig 失败**，
        /// 因为**只有这两个名字撞了**（其余 5 张表只有一个同名类型）。
        ///
        /// ✅ 改法：判据从"名字像"收紧成"**名字像 且 真的有 `LoadFromTsv(string)`**" ——
        ///    那个方法正是导入器**需要的东西**，用它当判据既准确又不会错杀。
        ///    若仍有多个候选，**不猜**，把候选列表报出来让人处理。
        /// </summary>
        /// <param name="typeName">类型名（如 `HeroConfig`）。</param>
        /// <param name="diagnostic">找不到/有歧义时的说明（成功时为 null）。</param>
        /// <returns>可用的类型；找不到或有歧义时返回 null。</returns>
        private static Type FindImportableType(string typeName, out string diagnostic)
        {
            List<Type> nameMatches = new List<Type>();
            List<Type> importable = new List<Type>();

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < assemblies.Length; i++)
            {
                Type[] types;

                try
                {
                    types = assemblies[i].GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    // 部分类型加载失败时，仍把成功的那部分拿来用
                    types = exception.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                if (types == null)
                {
                    continue;
                }

                for (int t = 0; t < types.Length; t++)
                {
                    Type type = types[t];

                    if (type == null || type.Name != typeName || !typeof(ScriptableObject).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    nameMatches.Add(type);

                    if (GetLoadMethod(type) != null)
                    {
                        importable.Add(type);
                    }
                }
            }

            if (importable.Count == 1)
            {
                diagnostic = null;
                return importable[0];
            }

            if (importable.Count > 1)
            {
                // 有歧义就**别猜**：猜错了会把数据导进错误的类型，而且看起来一切正常
                diagnostic =
                    "找到 " + importable.Count + " 个同名**且可导入**的类型，无法确定用哪一个：\n" +
                    Describe(importable) +
                    "\n请把多余的删掉/改名（生成物只应有一份）。";
                return null;
            }

            // 一个可导入的都没有：把"同名但不可导入"的列出来，这才是真正的线索
            if (nameMatches.Count > 0)
            {
                diagnostic =
                    "找到了 " + nameMatches.Count + " 个同名类型，但**都没有 " + LoadMethodName + "(string)**：\n" +
                    Describe(nameMatches) +
                    "\n最常见的两个原因：\n" +
                    "  ① 有**别的程序集定义了同名类型**（测试里的假类型就撞过这个名 —— " +
                    "判据已收紧为\"必须有 " + LoadMethodName + "\"，正常情况下不会再挑到它）\n" +
                    "  ② 这个类型是**旧版生成器**产出的（或被人手改过）—— 重新跑一次 ConfigKit.Cli";
                return null;
            }

            diagnostic =
                "整个 AppDomain 里都没有这个名字的类型。两个常见原因：\n" +
                "  ① 还没跑过 ConfigKit.Cli（没有生成 `Config_" + typeName + ".cs` / `" + typeName + ".cs`）\n" +
                "  ② 生成了但 Unity 还没编译完 —— 等它编译完再点一次这个菜单\n" +
                "（本脚本按**类型名**找，所以生成目录不写死在代码里。）";
            return null;
        }

        /// <summary>取一个类型的 `LoadFromTsv(string)` 公开实例方法（没有就返回 null）。</summary>
        /// <param name="type">类型。</param>
        /// <returns>方法；没有则 null。</returns>
        private static MethodInfo GetLoadMethod(Type type)
        {
            return type.GetMethod(LoadMethodName, BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string) }, null);
        }

        /// <summary>把一组类型写成"全名（程序集）"的多行列表（报歧义/报线索用）。</summary>
        /// <param name="types">类型。</param>
        /// <returns>文本。</returns>
        private static string Describe(List<Type> types)
        {
            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < types.Count; i++)
            {
                builder.Append("  · `").Append(types[i].FullName).Append("`（程序集 ");

                try
                {
                    builder.Append(types[i].Assembly.GetName().Name);
                }
                catch (Exception)
                {
                    // 取不到程序集名不该让报错本身炸掉
                    builder.Append("?");
                }

                builder.Append("）\n");
            }

            return builder.ToString();
        }

        /// <summary>把 "Assets/..." 换成绝对路径（Application.dataPath 去掉末尾的 /Assets）。</summary>
        /// <param name="assetRelativePath">以 Assets/ 开头的路径。</param>
        /// <returns>绝对路径。</returns>
        private static string ToAbsolute(string assetRelativePath)
        {
            string projectRoot = Application.dataPath.Substring(
                0, Application.dataPath.Length - "/Assets".Length);

            return Path.Combine(projectRoot, assetRelativePath).Replace('\\', '/');
        }

        /// <summary>数一个 SO 资产里有多少行数据（用反射，因为它没有统一接口）。</summary>
        /// <param name="asset">资产。</param>
        /// <returns>行数。</returns>
        private static int CountRows(ScriptableObject asset)
        {
            FieldInfo rows = asset.GetType().GetField("rows", BindingFlags.Public | BindingFlags.Instance);

            if (rows == null)
            {
                return 0;
            }

            System.Collections.ICollection collection = rows.GetValue(asset) as System.Collections.ICollection;
            return collection == null ? 0 : collection.Count;
        }

        /// <summary>把反射调用抛出的包装异常剥开（否则报错信息是 "Exception has been thrown by the target..."）。</summary>
        /// <param name="exception">异常。</param>
        /// <returns>里面那个。</returns>
        private static Exception Unwrap(Exception exception)
        {
            TargetInvocationException wrapper = exception as TargetInvocationException;
            return wrapper != null && wrapper.InnerException != null ? wrapper.InnerException : exception;
        }

        /// <summary>拼报告。</summary>
        /// <param name="done">处理好的表。</param>
        /// <param name="failed">失败的表。</param>
        /// <param name="write">是否写了资产。</param>
        /// <returns>报告文本。</returns>
        private static string Render(List<string> done, List<string> failed, bool write)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("=== ConfigImporter ===\n");
            builder.Append("数据来源：").Append(TsvDirectory).Append('\n');
            builder.Append("资产目录：").Append(AssetDirectory).Append('\n');
            builder.Append("模式：").Append(write ? "写入" : "只报告").Append('\n');
            builder.Append("处理 ").Append(done.Count).Append(" 张表");
            builder.Append(done.Count > 0 ? "：" + string.Join(", ", done.ToArray()) : string.Empty).Append('\n');

            if (failed.Count > 0)
            {
                builder.Append("⚠️ 失败 ").Append(failed.Count).Append(" 张：\n");

                for (int i = 0; i < failed.Count; i++)
                {
                    builder.Append("  · ").Append(failed[i]).Append('\n');
                }
            }

            return builder.ToString();
        }
    }
}
