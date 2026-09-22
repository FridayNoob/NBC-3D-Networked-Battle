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
        /// <summary>生成的 .tsv 所在目录（与 ConfigKit.Cli 的 --out 默认值一致）。</summary>
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
            Type soType = FindType(soTypeName);

            if (soType == null)
            {
                Debug.LogError(
                    "[ConfigImporter] 找不到类型 " + soTypeName + "。\n" +
                    "两个常见原因：\n" +
                    "  ① 还没跑过 ConfigKit.Cli（没有生成 Config_" + tableName + ".cs / " + soTypeName + ".cs）\n" +
                    "  ② 生成了但 Unity 还没编译完 —— 等它编译完再点一次这个菜单\n" +
                    "（本脚本按**类型名**找，所以生成目录不写死在代码里。）");
                return false;
            }

            MethodInfo load = soType.GetMethod(LoadMethodName, BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string) }, null);

            if (load == null)
            {
                Debug.LogError(
                    "[ConfigImporter] " + soTypeName + " 上没有 " + LoadMethodName + "(string)。\n" +
                    "说明这个类型是**旧版生成器**产出的（或被人手改过）—— 重新跑一次 ConfigKit.Cli。");
                return false;
            }

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
                Debug.Log("[ConfigImporter] （只报告）会**更新**资产 " + assetPath);
                return true;
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

        /// <summary>在所有已加载程序集里按名字找 &lt;表&gt;Config（必须是 ScriptableObject 派生）。</summary>
        /// <param name="typeName">类型名。</param>
        /// <returns>类型；找不到则 null。</returns>
        private static Type FindType(string typeName)
        {
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

                    if (type != null && type.Name == typeName && typeof(ScriptableObject).IsAssignableFrom(type))
                    {
                        return type;
                    }
                }
            }

            return null;
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
