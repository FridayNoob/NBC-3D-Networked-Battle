// ============================================================================
//  M2-D 追加 · **asmdef 引用守卫**：源码用到的命名空间，程序集必须引用
//  项目：3D联网战斗Demo   对应：Docs\00 里"闸门绿不等于 Unity 绿"那条
//
//  ---------------------------------------------------------------------------
//  为什么需要它（2026-09-23 **同一个坑踩了两次**）
//  ---------------------------------------------------------------------------
//  编译闸门 `Server\_api-probe` 是把**所有代码编进一个程序集**，所以它能查
//  "API 在不在、语法合不合法"，**但查不出 asmdef 级的程序集边界问题**。同一晚踩了两次：
//
//      ① `NBC.Boot` 用了 `QuestPanel`，而 `Initialize()` 是基类 `BasePanel` 的成员
//         （在 `NBC.Framework.UI`）→ **CS0012**（类型在未引用的程序集中定义）
//      ② `NBC.Editor` 用了 `using TMPro;`，但没引用 `Unity.TextMeshPro`
//         → **CS0246**（找不到命名空间 TMPro）
//
//  **两次都必须等 Unity 编译才暴露** —— 而这个守卫把 ② 那类（**命名空间级**）拉回 EditMode：
//  它读每个 asmdef 的 `references`，再扫那个目录下源码的 `using`，对不上的就红。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 它**查不到**什么（写清楚，免得当成万能）
//  ---------------------------------------------------------------------------
//  · **CS0012 那类**："我用了一个类型，它的**基类**在别的程序集里" ——
//    `using` 列表上看不出来（② 能查，① 查不到）
//  · 用了**全限定名**而没 `using` 的情况（例如直接写 `TMPro.TMP_Text`）：
//    本文件按 `using` 行匹配，全限定名会漏。**如实记**：想覆盖它得解析语法树，
//    那时应该直接上 Roslyn，而不是再加正则
//  · 引擎/包程序集**真实名字**与命名空间的对应关系靠下面那张表维护（不够就加一行）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 老规矩：机械检查必须带**阳性对照**，而且**要剥注释**
//  ---------------------------------------------------------------------------
//  "机械检查命中注释"本项目管理史上栽过 6 次，所以这里：
//    · 先 `StripComment` 再匹配
//    · 带一条阳性对照（喂一段"用了 TMPro 但没引用"的样本，必须被判定为违规）
//    · 带"自己命名空间不算违规"的负向对照（`NBC.Framework` 内部当然会 `using NBC.Framework`）
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>asmdef 引用守卫：源码里 `using` 的命名空间，所在程序集必须引用对应程序集。</summary>
    public sealed class AsmdefReferenceGuardTests
    {
        /// <summary>命名空间 → 需要引用的程序集名。</summary>
        private static readonly Dictionary<string, string> NamespaceToAssembly =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "TMPro", "Unity.TextMeshPro" },
                { "UnityEngine.UI", "UnityEngine.UI" },
                { "UnityEngine.EventSystems", "UnityEngine.UI" },
                { "NBC.Framework.UI", "NBC.Framework.UI" },
                { "NBC.Framework.Asset", "NBC.Framework" },
                { "NBC.Framework.Input", "NBC.Framework" },
                { "NBC.Framework", "NBC.Framework" },
                { "NBC.Shared", "NBC.Shared" },
                { "NBC.Game", "NBC.Game" },
                { "NBC.Boot", "NBC.Boot" },
                { "NBC.Model", "NBC.Model" }
            };

        /// <summary>`_Project` 目录（所有 asmdef 都在它下面）。</summary>
        private static string ProjectDir
        {
            get { return Path.Combine(Application.dataPath, "_Project"); }
        }

        // ====================================================================
        //  一、主检查
        // ====================================================================

        /// <summary>
        /// 每个 asmdef：源码里 `using` 的命名空间，都必须在它的 `references` 里有对应程序集。
        /// </summary>
        [Test]
        public void EveryAsmdef_ReferencesTheAssembliesItsSourcesUse()
        {
            string[] asmdefs = Directory.GetFiles(ProjectDir, "*.asmdef", SearchOption.AllDirectories);
            Array.Sort(asmdefs, StringComparer.Ordinal);

            Assert.Greater(asmdefs.Length, 5,
                "一个 asmdef 都没扫到？目录结构变了的话这条守卫会**空过**（假绿）。实际找到 " +
                asmdefs.Length + " 个。");

            List<string> offenders = new List<string>();

            for (int i = 0; i < asmdefs.Length; i++)
            {
                string asmdefPath = asmdefs[i];
                string assemblyName = ReadName(asmdefPath);
                List<string> references = ReadReferences(asmdefPath);
                List<string> used = CollectUsedNamespaces(Path.GetDirectoryName(asmdefPath));

                for (int u = 0; u < used.Count; u++)
                {
                    string required;

                    if (!NamespaceToAssembly.TryGetValue(used[u], out required))
                    {
                        continue;   // 表里没有的命名空间（BCL、引擎模块…）不管
                    }

                    // ⚠️ 自己程序集里的命名空间不用"引用自己"
                    if (string.Equals(required, assemblyName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!references.Contains(required))
                    {
                        offenders.Add(
                            Path.GetFileName(asmdefPath) + "（程序集 " + assemblyName + "）：" +
                            "源码里 `using " + used[u] + ";`，但没有引用 `" + required + "`");
                    }
                }
            }

            CollectionAssert.IsEmpty(offenders,
                "asmdef 引用不全会让 **Unity 编译报错**（CS0246/CS0012），而编译闸门抓不到 ——\n" +
                "闸门是把所有代码编进一个程序集，看不到程序集边界。违规：\n" +
                string.Join("\n", offenders.ToArray()));
        }

        /// <summary>阳性对照：一段"用了 TMPro 但没引用"的样本，必须被判为违规。</summary>
        [Test]
        public void Guard_ActuallyDetectsMissingReference()
        {
            Assert.IsTrue(NeedsReference("using TMPro;", "NBC.Editor", new List<string> { "NBC.Game" }),
                "用了 TMPro 却没引用 Unity.TextMeshPro —— 应当被检出");

            Assert.IsTrue(NeedsReference("using UnityEngine.UI;", "NBC.Game", new List<string>()),
                "用了 UnityEngine.UI 却没引用 —— 应当被检出");

            // 负向对照一：引用了就不算违规
            Assert.IsFalse(NeedsReference("using TMPro;", "NBC.Editor",
                new List<string> { "Unity.TextMeshPro" }), "引用了就不该报");

            // 负向对照二：**自己程序集里的命名空间**不算违规（否则 NBC.Framework 会永远红）
            Assert.IsFalse(NeedsReference("using NBC.Framework;", "NBC.Framework", new List<string>()),
                "自己程序集内的命名空间不该要求引用自己");

            // 负向对照三：注释里的 using 不算（"机械检查命中注释"栽过 6 次）
            Assert.IsFalse(NeedsReference(StripComment("// using TMPro; 举例"), "NBC.Editor",
                new List<string>()), "注释里的 using 不该被当成真的引用");

            // 负向对照四：表里没有的命名空间不管
            Assert.IsFalse(NeedsReference("using System.Text;", "NBC.Game", new List<string>()),
                "BCL 命名空间不在表里，不该报");
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>这一行（剥注释后）用到的命名空间，需要却没引用吗？</summary>
        /// <param name="line">源码行。</param>
        /// <param name="assemblyName">所在程序集名。</param>
        /// <param name="references">该程序集的引用列表。</param>
        /// <returns>缺引用返回 true。</returns>
        private static bool NeedsReference(string line, string assemblyName, List<string> references)
        {
            string code = StripComment(line);
            Match match = Regex.Match(code.Trim(), @"^using\s+(?:static\s+)?([A-Za-z_][\w\.]*)\s*;");

            if (!match.Success)
            {
                return false;
            }

            string required;

            if (!NamespaceToAssembly.TryGetValue(match.Groups[1].Value, out required))
            {
                return false;
            }

            if (string.Equals(required, assemblyName, StringComparison.Ordinal))
            {
                return false;
            }

            return !references.Contains(required);
        }

        /// <summary>剥掉这一行的注释部分。</summary>
        /// <param name="line">源码行。</param>
        /// <returns>去掉注释的部分。</returns>
        private static string StripComment(string line)
        {
            int index = line.IndexOf("//", StringComparison.Ordinal);
            return index < 0 ? line : line.Substring(0, index);
        }

        /// <summary>扫一个目录下所有 .cs 用到的命名空间（去重、排序）。</summary>
        /// <param name="directory">目录。</param>
        /// <returns>命名空间列表。</returns>
        private static List<string> CollectUsedNamespaces(string directory)
        {
            List<string> result = new List<string>();
            string[] files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);

            for (int i = 0; i < files.Length; i++)
            {
                string[] lines = File.ReadAllLines(files[i]);

                for (int line = 0; line < lines.Length; line++)
                {
                    string code = StripComment(lines[line]);
                    Match match = Regex.Match(code.Trim(), @"^using\s+(?:static\s+)?([A-Za-z_][\w\.]*)\s*;");

                    if (!match.Success)
                    {
                        continue;
                    }

                    string ns = match.Groups[1].Value;

                    if (!result.Contains(ns))
                    {
                        result.Add(ns);
                    }
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>读 asmdef 的 `name`。</summary>
        /// <param name="asmdefPath">路径。</param>
        /// <returns>程序集名。</returns>
        private static string ReadName(string asmdefPath)
        {
            Match match = Regex.Match(File.ReadAllText(asmdefPath), "\"name\"\\s*:\\s*\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        /// <summary>读 asmdef 的 `references` 列表。</summary>
        /// <param name="asmdefPath">路径。</param>
        /// <returns>引用列表。</returns>
        private static List<string> ReadReferences(string asmdefPath)
        {
            List<string> result = new List<string>();
            Match array = Regex.Match(File.ReadAllText(asmdefPath),
                "\"references\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);

            if (!array.Success)
            {
                return result;
            }

            MatchCollection items = Regex.Matches(array.Groups[1].Value, "\"([^\"]+)\"");

            for (int i = 0; i < items.Count; i++)
            {
                result.Add(items[i].Groups[1].Value.Trim());
            }

            return result;
        }
    }
}
