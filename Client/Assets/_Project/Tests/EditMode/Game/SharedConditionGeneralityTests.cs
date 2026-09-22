// ============================================================================
//  M2-A1 · 条件系统「通用性」的机械检查
//  对应验收：Docs\22-M2开工清单.md A1（V1：**不认识"任务/成就"这两个词**）
//
//  ---------------------------------------------------------------------------
//  为什么要测"某个词没出现"
//  ---------------------------------------------------------------------------
//  `Docs\20` §四 的设计要点是：
//      **条件系统不认识"任务"或"成就"** —— 它只认识"事件 + 条件 + 进度 + 达成回调"。
//  这条约定**编译器完全不会帮你**：往共享层写一句 `if (quest...)` 照样编译通过，
//  而后果是**服务端也被迫认识任务系统**（M4 要在服务端校验任务进度）——
//  通用层一旦沾上业务词，复用就结束了，而且没有任何东西会提醒你。
//
//  所以把它变成机械保证：**改坏了测试当场变红**。
//  （同一套思路：`SharedLayerTests` 断言 asmdef 配置、`DeterminismGuardTests` 断言共享层无浮点。）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 这类检查最怕两件事，本文件都堵上了
//  ---------------------------------------------------------------------------
//  ① **假绿**：目录写错 / 文件被搬走 → 一个文件都没扫到 → "0 违规" → 通过。
//     堵法：先断言**扫到了足够多的文件**（顺带核对文件名清单）。
//  ② **命中注释**：本项目已经栽过 **6 次**（"机械检查命中注释"）。
//     堵法：先剥注释再匹配，并且用**阳性对照**证明"违规真的会被抓到"。
// ============================================================================

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>M2-A1：共享层条件系统的通用性检查。</summary>
    public sealed class SharedConditionGeneralityTests
    {
        /// <summary>条件系统所在目录。</summary>
        private static string ConditionDir
        {
            get { return Path.Combine(Application.dataPath, "_Project", "Shared", "Condition"); }
        }

        /// <summary>不许出现在**代码**里的业务词。</summary>
        private static readonly string[] ForbiddenWords = { "Quest", "Achievement" };

        /// <summary>条件系统最少要有几个文件（丢了文件不许"空过"）。</summary>
        private const int ExpectedFileCount = 6;

        // ====================================================================
        //  一、主检查
        // ====================================================================

        /// <summary>
        /// 共享层条件系统的**代码**里不许出现 "Quest" / "Achievement"（注释里讨论设计可以）。
        /// </summary>
        [Test]
        public void ConditionSources_ContainNoBusinessWords()
        {
            List<string> files = SourceFiles();

            Assert.GreaterOrEqual(files.Count, ExpectedFileCount,
                "条件系统的文件数不对（" + files.Count + " < " + ExpectedFileCount + "）：\n" +
                "如果目录被改名/搬走了，这条检查会**一个文件都扫不到**，然后假装通过。");

            List<string> offenders = new List<string>();

            for (int i = 0; i < files.Count; i++)
            {
                string[] lines = File.ReadAllLines(files[i]);

                for (int line = 0; line < lines.Length; line++)
                {
                    string code = StripComment(lines[line]);

                    for (int w = 0; w < ForbiddenWords.Length; w++)
                    {
                        if (code.Contains(ForbiddenWords[w]))
                        {
                            offenders.Add(Path.GetFileName(files[i]) + ":" + (line + 1) +
                                          "  [" + ForbiddenWords[w] + "]  " + code.Trim());
                        }
                    }
                }
            }

            CollectionAssert.IsEmpty(offenders,
                "条件系统（共享层）里出现了业务词 —— 它会被服务端一起编译，\n" +
                "所以必须对\"任务/成就\"零知识（`Docs\\20` §四）。违规：\n" +
                string.Join("\n", offenders.ToArray()));
        }

        /// <summary>对答案用：目录里应当就是这几个文件（防止"扫到了别的东西"）。</summary>
        [Test]
        public void ConditionSources_HaveExpectedFileNames()
        {
            List<string> names = new List<string>();

            List<string> files = SourceFiles();

            for (int i = 0; i < files.Count; i++)
            {
                names.Add(Path.GetFileName(files[i]));
            }

            names.Sort();

            CollectionAssert.AreEqual(
                new[]
                {
                    "ConditionDef.cs",
                    "ConditionProgress.cs",
                    "ConditionTracker.cs",
                    "EConditionEvent.cs",
                    "IConditionProgressStore.cs",
                    "InMemoryConditionProgressStore.cs"
                },
                names,
                "条件系统的文件清单变了 —— 如果是有意新增/改名，把这条断言一起更新。");
        }

        // ====================================================================
        //  二、阳性对照（W9：检查器本身要先被验证）
        // ====================================================================

        /// <summary>
        /// **阳性对照**：故意拿一段含业务词的代码喂给同一套规则，必须被判为违规。
        /// <para>没有这一条的话，"正则写错了"会让上面那条断言**永远通过**。</para>
        /// </summary>
        [Test]
        public void Checker_ActuallyDetectsBusinessWords()
        {
            Assert.IsTrue(HasForbiddenWord("if (quest != null) { }"), "含 Quest 的代码应当被检出");
            Assert.IsTrue(HasForbiddenWord("AchievementRuntime.OnUnlocked();"), "含 Achievement 应当被检出");
            Assert.IsFalse(HasForbiddenWord("public readonly struct ConditionDef"), "正常代码不该被误报");

            // ⚠️ 注释里的词**不算违规**（本文件顶部的说明里就有这两个词）
            Assert.IsFalse(HasForbiddenWord(StripComment("// QuestRuntime 需要 ConditionTracker")),
                "注释里提到不算违规 —— 剥离注释这条规则本身也要有对照");
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>这一行（去掉注释后）含不含违禁词。</summary>
        /// <param name="code">代码行。</param>
        /// <returns>含就返回 true。</returns>
        private static bool HasForbiddenWord(string code)
        {
            for (int i = 0; i < ForbiddenWords.Length; i++)
            {
                if (code.Contains(ForbiddenWords[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 剥掉这一行的注释部分（`//` 之后）。
        /// <para>
        /// ⚠️ 已知局限（如实记）：它不处理**字符串字面量里的 `//`**（例如 URL）。
        /// 本目录里没有那种代码；真出现了会误报，那时再升级成状态机。
        /// 这类"说明自己边界的检查"是本项目的既定做法。
        /// </para>
        /// </summary>
        /// <param name="line">源码行。</param>
        /// <returns>去掉注释的部分。</returns>
        private static string StripComment(string line)
        {
            int index = line.IndexOf("//", System.StringComparison.Ordinal);
            return index < 0 ? line : line.Substring(0, index);
        }

        /// <summary>条件系统目录下的所有 .cs（按名字排序，结果稳定）。</summary>
        /// <returns>文件路径列表。</returns>
        private static List<string> SourceFiles()
        {
            List<string> result = new List<string>();

            if (!Directory.Exists(ConditionDir))
            {
                return result;
            }

            string[] files = Directory.GetFiles(ConditionDir, "*.cs", SearchOption.TopDirectoryOnly);
            System.Array.Sort(files, System.StringComparer.Ordinal);

            for (int i = 0; i < files.Length; i++)
            {
                result.Add(files[i]);
            }

            return result;
        }
    }
}
