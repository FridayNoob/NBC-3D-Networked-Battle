// 这个文件里有：
//   ConfigImporter（静态类）：把 ConfigKit.Cli 生成的 .tsv 按表名反射灌进对应的 SO 资产，另提供只报告模式逐字段比对现有资产
//
// 注意：生成的 SO 类型（表名 + Config）在本脚本编译时可能还不存在，所以只能按类型名反射装配；反射只在编辑器里用，Editor 脚本不进包，没有 IL2CPP/AOT 那类问题。
// 注意：选 .tsv 而不是 .json，是因为 Unity 序列化不支持可空值类型、JsonUtility 把枚举按整数走；TSV 配生成的显式加载器把这两个问题都绕开，还不用引解析库（JSON 那份是给外部工具看的，详见 Tools\ConfigKit\README.md）。
// 注意：点菜单前必须先在命令行跑过 ConfigKit.Cli、并等 Unity 把生成的 .cs 编译完，否则找不到类型；找不到时报告会写清缺哪一步，不是静默跳过。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    // 主要功能：把生成目录里的每张 .tsv 反射灌进同名 SO 资产，并能在只报告模式下逐字段比对现有资产与源表。
    // 主要数据成员：TsvDirectory（源 .tsv 目录）、AssetDirectory（资产目录）、LoadMethodName 与 TypeSuffix（生成物契约：加载方法名、类型与资产名后缀）。
    // 主要外部方法：无（两个菜单入口都是 private），对外只公开两个目录常量。
    // 谁在用它：Unity 菜单 Tools/NBC/配置表/导入 TSV → ScriptableObject 与检查（只报告）；同程序集的 ConfigStalenessCheck、EditorConfigSource 复用两个目录常量。
    public static class ConfigImporter
    {
        // 生成的 .tsv 目录，工程相对路径；与 ConfigKit.Cli 的 --out 默认值一致，改这里要同步改命令行
        public const string TsvDirectory = "Assets/_Project/Game/Config/Generated";

        // SO 资产目录，工程相对路径；导入器写、ConfigStalenessCheck 与 EditorConfigSource 读，只留这一个真值
        public const string AssetDirectory = "Assets/_Project/Configs";

        // 生成的 SO 类型上约定的加载方法名，同时是「可导入」的判据；写错会一张表都找不到
        private const string LoadMethodName = "LoadFromTsv";
        // 表名拼成类型名与资产名的后缀（Hero 加 Config 得 HeroConfig），必须与生成器一致
        private const string TypeSuffix = "Config";

        // 功能：菜单入口，扫一遍全部表并真的写资产。
        [MenuItem("Tools/NBC/配置表/导入 TSV → ScriptableObject")]
        private static void ImportAll()
        {
            Run(true);
        }

        // 功能：菜单入口，只检查不写资产，先看会动哪些文件。
        [MenuItem("Tools/NBC/配置表/检查（只报告，不写资产）")]
        private static void CheckOnly()
        {
            Run(false);
        }

        // 功能：扫生成目录里的 .tsv，逐张调 ImportOne，最后把成功与失败汇总成一份报告打到 Console。
        // 入参：write 为 true 真的写资产；false 只报告、不落盘
        // 返回：无（结果走 Console 日志）
        // 步骤：
        //   1) 先把生成目录转成绝对路径，后面的读写都不依赖当前工作目录
        //   2) 目录不存在或没有 .tsv 就直接报错或警告退出——多半是还没跑过 ConfigKit.Cli，这里必须告诉人先跑哪两条命令
        //   3) 文件名排序后再导，报告顺序才稳定，两次运行能直接 diff
        //   4) 写模式先建资产目录，缺目录时 CreateAsset 会失败
        //   5) 逐张 ImportOne 并各自包 try/catch：一张表坏了不能拖停其余表，异常原因记进失败列表
        //   6) 全部导完只 SaveAssets 加 Refresh 一次，避免每张表各触发一次全量重导入
        // 上游：菜单 ImportAll / CheckOnly
        // 下游：ToAbsolute、ImportOne、Render
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

        // 功能：导入一张表——按表名找可导入类型，取或新建对应资产，用生成的加载器灌数据；只报告模式改为逐字段比对。
        // 入参：tableName 表名（.tsv 文件名去掉扩展名）；tsv 文件全文；write 是否写资产
        // 返回：true 表示这张表处理成功（只报告模式下「会新建」也算成功）；false 表示找不到类型、创建实例失败或内容不一致
        // 步骤：
        //   1) 先按表名拼出类型名（表名加 Config 后缀），再找可导入类型——判据里必须带上加载方法，不能只按名字找
        //   2) 取资产；写模式下没有就 CreateInstance 加 CreateAsset，只报告模式只提示「会新建」，不在磁盘上留东西
        //   3) 只报告模式：新建一个临时实例、用同一份 tsv 灌一遍，再与现有资产逐字段比内容，最后销毁临时实例
        //   4) 写模式：反射调生成的加载器灌进现有资产，它按字段名对列，调换列顺序不会错位；再 SetDirty 让 Unity 真正写盘
        // 上游：Run 逐张调用
        // 下游：FindImportableType、GetLoadMethod、CompareRows、CountRows
        // 注意：只报告模式过去只验证 tsv 能不能导入、从不和现有资产比，于是手改 SO 永远抓不到；而资产是后改的、比源表新，按修改时间的检查也会放过它，所以必须逐字段比内容。
        // 注意：资产是生成物，手改会被下次导入覆盖；服务端读的是源 CSV 而不是 SO，改资产对服务端没有任何影响。
        // 注意：这里不再判加载方法找没找到——FindImportableType 就是按它筛的，再判一次是永远走不到的分支，那种分支比没有分支更糟。
        private static bool ImportOne(string tableName, string tsv, bool write)
        {
            string soTypeName = tableName + TypeSuffix;

            string diagnostic;
            Type soType = FindImportableType(soTypeName, out diagnostic);

            if (soType == null)
            {
                Debug.LogError(
                    "[ConfigImporter] 找不到可用的类型 " + soTypeName + "。\n" + diagnostic);
                return false;
            }

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

            load.Invoke(asset, new object[] { tsv });

            EditorUtility.SetDirty(asset);

            if (created)
            {
                Debug.Log("[ConfigImporter] 新建 " + assetPath + "（" + CountRows(asset) + " 行）");
            }

            return true;
        }

        // 功能：把同一份 tsv 灌出来的新实例与现有资产逐字段比，产出人能看懂的差异清单。
        // 入参：asset 现有资产；fresh 用同一份 tsv 重新灌出来的新实例
        // 返回：差异列表，空表示一致；读不到 rows 或 id 字段时返回一条说明而不是抛异常
        // 步骤：
        //   1) 反射拿生成物约定的 rows 字段，拿不到先报「生成物变了」
        //   2) 用 id 建索引再按 id 比，行顺序变了不算差异（生成器的加表顺序可能变）
        //   3) 新表里缺的 id 记「资产缺少」，旧表里多出来的 id 记「资产多了」
        //   4) 同 id 逐字段比，数组交给 SameFieldValue 按元素比
        // 上游：ImportOne 的只报告模式
        // 下游：SameFieldValue、DescribeValue
        // 注意：必须比内容而不是比时间——资产是生成物，手改之后它比源表更新，任何按修改时间的检查都会放过它，而「改了却没生效」本身不报错，只能把两份内容摆在一起看。
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

        // 功能：比两个字段值是否相同，数组按元素逐个比。
        // 入参：a、b 两侧字段值，可能为 null（生成物里有 int[] skillIds 这类数组字段）
        // 返回：true 表示值相同；false 表示不同（数组长度不同或任一元素不同）
        // 步骤：
        //   1) 两侧都不是数组就退化成 Equals，交给类型自己的相等语义
        //   2) 只有一侧是数组就判不同；两侧都是数组再比长度
        //   3) 长度相同后逐元素 Equals——数组默认的引用相等在这里没用
        // 上游：CompareRows
        // 下游：无
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

        // 功能：把字段值写成人话，数组写成方括号加逗号分隔，null 写成「（null）」。
        // 入参：value 字段值，可能为 null
        // 返回：能直接拼进差异行的文本；null 与空字符串分别可辨
        // 步骤：
        //   1) null 先单独返回，否则 ToString 会给出空串，和「值是空字符串」分不清
        //   2) 不是数组直接 ToString
        //   3) 数组逐元素拼，元素之间只加逗号不加空格，差异行才短
        // 上游：CompareRows
        // 下游：无
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

        // 功能：在所有已加载程序集里找表名对应的 SO 类型，且必须带加载方法；找不到或有歧义时报出诊断文案。
        // 入参：typeName 类型名（表名加 Config 后缀）；diagnostic 找不到或歧义时的说明，成功时为 null
        // 返回：唯一可导入的类型；0 个或多个候选时返回 null——宁可报错也不猜
        // 步骤：
        //   1) 遍历 AppDomain 里所有程序集（测试程序集在编辑器里同样是加载着的），记下名字对上且是 ScriptableObject 的候选
        //   2) 再按有没有加载方法收窄成可导入候选：那个方法正是导入器需要的东西，用它当判据既准确又不会错杀
        //   3) 只剩一个就直接用；有多个就列出全名让人处理，猜错会把数据导进错误的类型，而且看起来一切正常
        //   4) 一个可导入的都没有时，把同名但不可导入的列出来，那才是真正的线索；连同名都没有才提示去跑 ConfigKit
        // 上游：ImportOne
        // 下游：GetLoadMethod、Describe
        // 注意：只按「名字像且是 ScriptableObject」找会随机挑到同名类型——生成物在 Game 程序集里，测试的假类型撞过同一个名且没有加载方法，而 GetAssemblies 的顺序不保证；那时的报错还误导人去重跑 ConfigKit，其实生成物是好的。
        // 注意：取类型时可能有个别类型加载失败，ReflectionTypeLoadException 里能拿到的那部分仍要用，少一个无关类型不该让整次导入失败。
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
                diagnostic =
                    "找到 " + importable.Count + " 个同名**且可导入**的类型，无法确定用哪一个：\n" +
                    Describe(importable) +
                    "\n请把多余的删掉/改名（生成物只应有一份）。";
                return null;
            }

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

        // 功能：取类型上公开的实例方法 LoadFromTsv(string)，没有就返回 null。
        // 入参：type 待检查的类型
        // 返回：方法信息；类型上没有这个方法时为 null，调用方据此判「不可导入」
        // 步骤：
        //   1) 按名字加明确的参数类型查，不靠参数个数猜，免得撞上重载
        // 上游：ImportOne、FindImportableType
        // 下游：无
        private static MethodInfo GetLoadMethod(Type type)
        {
            return type.GetMethod(LoadMethodName, BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string) }, null);
        }

        // 功能：把一组类型写成「全名（程序集）」的逐行列表，报歧义与线索时用。
        // 入参：types 待描述的类型
        // 返回：每行一个类型的文本，行首两个空格加圆点
        // 步骤：
        //   1) 逐类型拼全名
        //   2) 再取程序集名；取不到就写问号，不能让报错本身再炸一次
        // 上游：FindImportableType 的三条诊断分支
        // 下游：无
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
                    builder.Append("?");
                }

                builder.Append("）\n");
            }

            return builder.ToString();
        }

        // 功能：把以 Assets/ 开头的工程相对路径换成绝对路径。
        // 入参：assetRelativePath 以 Assets/ 开头的工程内路径
        // 返回：绝对路径，分隔符统一成正斜杠
        // 步骤：
        //   1) Application.dataPath 结尾就是 /Assets，截掉它得到工程根目录，避免依赖当前工作目录
        //   2) 拼上相对路径再把反斜杠换成斜杠，同一路径在日志里只有一种写法
        // 上游：Run 开头
        // 下游：无
        private static string ToAbsolute(string assetRelativePath)
        {
            string projectRoot = Application.dataPath.Substring(
                0, Application.dataPath.Length - "/Assets".Length);

            return Path.Combine(projectRoot, assetRelativePath).Replace('\\', '/');
        }

        // 功能：数一个 SO 资产里 rows 有多少条。
        // 入参：asset 资产
        // 返回：行数；没有 rows 字段或它不是集合时返回 0
        // 步骤：
        //   1) 生成物没有统一接口，只能反射取 rows
        //   2) 取不到就按 0 算——这行日志只是提示，不该因为数不出来就让整张表失败
        // 上游：ImportOne 新建资产后的日志
        // 下游：无
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

        // 功能：剥掉反射调用包在外面的 TargetInvocationException，只留真正的原因。
        // 入参：exception 捕获到的异常
        // 返回：包装层的内部异常；不是包装异常或没有内层时原样返回
        // 步骤：
        //   1) 只认 TargetInvocationException 这一层，也只剥一层——反射调用只会套一层，内层异常自带堆栈
        // 上游：Run 的 catch
        // 下游：无
        private static Exception Unwrap(Exception exception)
        {
            TargetInvocationException wrapper = exception as TargetInvocationException;
            return wrapper != null && wrapper.InnerException != null ? wrapper.InnerException : exception;
        }

        // 功能：把本次导入的数据来源、资产目录、模式、成功与失败表清单拼成一段报告文本。
        // 入参：done 处理成功的表名；failed 失败的表名与原因；write 是否真写了资产
        // 返回：报告全文；没有失败时不含失败段
        // 步骤：
        //   1) 先写表头与两个目录、模式，出问题时能一眼看出导的是哪份配置
        //   2) 再写成功表数与表名，空列表只留数字，不留空括号
        //   3) 有失败才追加逐条原因
        // 上游：Run 最后一次性输出
        // 下游：无
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
