// ============================================================================
//  M1-D2 · 共享层的「确定性守卫」测试
//  需求依据：§6.4 DET-01 / DET-02（确定性数学、不依赖 UnityEngine）
//
//  ---------------------------------------------------------------------------
//  为什么要用"读源码 + 断言"来测这种东西
//  ---------------------------------------------------------------------------
//  "共享逻辑里不许有浮点"这条约定，**编译器完全不会帮你**：
//  写一句 `Math.Sin(x)` 或 `(double)a / b` 照样编译通过，
//  而后果是**帧同步悄悄失效** —— 服务端和客户端算出不同结果，
//  而且**只在联机时才暴露、极难复现**。
//
//  所以把它变成机械保证：**改坏了测试当场变红**。
//  （同一套思路：`SharedLayerTests` 断言 asmdef/csproj 配置，
//    `FrameworkLayeringTests` 断言 asmdef 引用为空。）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 这类检查必须"说明自己的规则"，否则会误报
//  ---------------------------------------------------------------------------
//  第一版我用 `Math\.` 做正则，结果误报了两处：
//    · `double.IsNaN(value)` —— 在 `FromDouble` 里，是**有意的转换边界**
//    · 错误消息里的字符串 `"[FixMath."` —— 它含 `Math.`，但根本不是调用
//
//  所以现在的规则写死成两条，并且**逐条说明允许什么**：
//    规则 1：不许调用 BCL 的 `Math.Xxx(...)`（`FixMath.Xxx` 是自己的，不算）
//    规则 2：`double` / `float` 只允许出现在**转换边界**那几个成员里
//            （`ToFloat` / `ToDouble` / `FromFloat` / `FromDouble` / 显式转换运算符）
//
//  这已经是本项目第 4 次"验证器本身要先被验证"了。
// ============================================================================

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// D2：共享层源码的确定性守卫。
    /// </summary>
    public sealed class DeterminismGuardTests
    {
        /// <summary>允许出现浮点的成员名（转换边界）。</summary>
        private static readonly string[] FloatBoundaryMembers =
        {
            "ToFloat", "ToDouble", "FromFloat", "FromDouble"
        };

        /// <summary>共享源码目录。</summary>
        private static string SharedDir
        {
            get { return Path.Combine(Application.dataPath, "_Project", "Shared"); }
        }

        // ====================================================================
        //  规则 1：不许调用 BCL 数学
        // ====================================================================

        /// <summary>
        /// 共享源码里**不许调用** `Math.Sin` / `Math.Sqrt` / `Math.Atan2` 这类 BCL 数学函数。
        /// <para>
        /// 理由：BCL 的实现**在不同运行时下可能不同**（服务端 .NET vs 客户端 Mono/IL2CPP），
        /// 用它算出来的结果不具备位级别确定性 —— 帧同步会悄悄失效。
        /// </para>
        /// <para>`FixMath.Xxx` 是自己写的，不算（用负向环视 `(?&lt;!Fix)` 排除）。</para>
        /// </summary>
        [Test]
        public void SharedSources_ContainNoBclMathCalls()
        {
            Regex pattern = new Regex(@"(?<!Fix)Math\.[A-Za-z_]+\s*\(");
            List<string> offenders = new List<string>();

            foreach (string file in SourceFiles())
            {
                string[] lines = File.ReadAllLines(file);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (IsComment(lines[i]))
                    {
                        continue;
                    }

                    if (pattern.IsMatch(lines[i]))
                    {
                        offenders.Add(Describe(file, i, lines[i]));
                    }
                }
            }

            CollectionAssert.IsEmpty(offenders,
                "共享源码里出现了 BCL 数学调用（会破坏确定性）：\n" + Join(offenders));
        }

        // ====================================================================
        //  规则 2：浮点只许出现在转换边界
        // ====================================================================

        /// <summary>
        /// `double` / `float` 只允许出现在**转换边界**：
        /// `ToFloat` / `ToDouble` / `FromFloat` / `FromDouble` / 显式转换运算符。
        /// <para>
        /// 为什么这几个允许：它们是"外部浮点 ↔ 内部定点"的**单向闸门**。
        /// 转换结果只取决于输入值本身（是确定的），而**进去之后就全是整数运算**。
        /// </para>
        /// <para>
        /// 判作用域的办法：往回找**最近的成员声明**，看它的名字在不在许可名单里。
        /// 靠关键词匹配会误报（第一版就误报过 `double.IsNaN`）。
        /// </para>
        /// </summary>
        [Test]
        public void SharedSources_UseFloatingPointOnlyAtConversionBoundary()
        {
            Regex declaration = new Regex(@"^\s*(public|private|protected|internal)\b.*?\b([A-Za-z_]\w*)\s*\(");
            List<string> offenders = new List<string>();

            foreach (string file in SourceFiles())
            {
                string[] lines = File.ReadAllLines(file);
                string currentMember = string.Empty;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];

                    if (IsComment(line))
                    {
                        continue;
                    }

                    Match m = declaration.Match(line);

                    if (m.Success)
                    {
                        currentMember = m.Groups[2].Value;
                    }

                    if (!Regex.IsMatch(line, @"\b(double|float)\b"))
                    {
                        continue;
                    }

                    // 转换运算符（implicit/explicit operator）本身就是边界
                    if (line.Contains("operator"))
                    {
                        continue;
                    }

                    if (!IsFloatBoundaryMember(currentMember))
                    {
                        offenders.Add(Describe(file, i, line) + "   [所在成员：" + currentMember + "]");
                    }
                }
            }

            CollectionAssert.IsEmpty(offenders,
                "共享源码里出现了转换边界之外的浮点：\n" + Join(offenders));
        }

        // ====================================================================
        //  规则 3：共享层不许引用 UnityEngine（与 SharedLayerTests 那条互补）
        // ====================================================================

        /// <summary>
        /// 共享源码里**真实代码**不许出现 `UnityEngine`（注释里提到不算 —— 说明文字里就有这个词）。
        /// <para>服务端没有 Unity 环境，引用了就编不过（而且只有服务端构建时才会发现）。</para>
        /// </summary>
        [Test]
        public void SharedSources_ContainNoUnityEngineInCode()
        {
            List<string> offenders = new List<string>();

            foreach (string file in SourceFiles())
            {
                string[] lines = File.ReadAllLines(file);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (IsComment(lines[i]))
                    {
                        continue;
                    }

                    if (lines[i].Contains("UnityEngine"))
                    {
                        offenders.Add(Describe(file, i, lines[i]));
                    }
                }
            }

            CollectionAssert.IsEmpty(offenders,
                "共享源码里出现了 UnityEngine（注释不算）：\n" + Join(offenders));
        }

        // ====================================================================
        //  规则 4：守卫本身要能测到东西（W9）
        // ====================================================================

        /// <summary>
        /// **阳性对照**：确认上面的规则真的能匹配到东西，而不是"永远通过"。
        /// <para>
        /// 一个写错的正则会让所有断言空过 —— 那是**假绿**。
        /// 所以拿一段"故意违规的样本"喂给同一套规则，必须被判定为违规。
        /// </para>
        /// </summary>
        [Test]
        public void Guards_ActuallyDetectViolations()
        {
            Regex bclMath = new Regex(@"(?<!Fix)Math\.[A-Za-z_]+\s*\(");

            // 应当被判为违规的样本
            Assert.IsTrue(bclMath.IsMatch("double v = Math.Sin(angle);"), "BCL 数学调用应当被检出");
            Assert.IsTrue(bclMath.IsMatch("return Math.Sqrt(x);"), "BCL 数学调用应当被检出");

            // 不应当误报的样本
            Assert.IsFalse(bclMath.IsMatch("return FixMath.Sin(angle);"), "FixMath 不该被误报");
            Assert.IsFalse(bclMath.IsMatch("throw new Exception(\"[FixMath.\" + name);"),
                "字符串里的 \"[FixMath.\" 不该被误报（第一版就是在这里误报的）");

            // 浮点判定：转换边界内允许，之外不允许
            Assert.IsTrue(IsFloatBoundaryMember("ToDouble"), "ToDouble 是转换边界");
            Assert.IsTrue(IsFloatBoundaryMember("FromDouble"), "FromDouble 是转换边界");
            Assert.IsFalse(IsFloatBoundaryMember("Sin"), "Sin 不是转换边界");
            Assert.IsFalse(IsFloatBoundaryMember("Add"), "Add 不是转换边界");
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>共享目录下的所有 .cs。</summary>
        /// <returns>文件路径列表。</returns>
        private static List<string> SourceFiles()
        {
            List<string> result = new List<string>();

            if (!Directory.Exists(SharedDir))
            {
                return result;
            }

            string[] files = Directory.GetFiles(SharedDir, "*.cs", SearchOption.AllDirectories);

            for (int i = 0; i < files.Length; i++)
            {
                result.Add(files[i]);
            }

            return result;
        }

        /// <summary>这一行是不是注释。</summary>
        /// <param name="line">源码行。</param>
        /// <returns>是注释返回 true。</returns>
        private static bool IsComment(string line)
        {
            return line.TrimStart().StartsWith("//");
        }

        /// <summary>成员名是不是"浮点转换边界"。</summary>
        /// <param name="member">成员名。</param>
        /// <returns>是的话返回 true。</returns>
        private static bool IsFloatBoundaryMember(string member)
        {
            for (int i = 0; i < FloatBoundaryMembers.Length; i++)
            {
                if (member == FloatBoundaryMembers[i])
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>把违规位置写成一句人话。</summary>
        /// <param name="file">文件。</param>
        /// <param name="lineIndex">行下标（0 基）。</param>
        /// <param name="line">行内容。</param>
        /// <returns>描述。</returns>
        private static string Describe(string file, int lineIndex, string line)
        {
            return Path.GetFileName(file) + ":" + (lineIndex + 1) + "  " + line.Trim();
        }

        /// <summary>把违规列表拼成一段文本。</summary>
        /// <param name="items">违规项。</param>
        /// <returns>文本。</returns>
        private static string Join(List<string> items)
        {
            return items.Count == 0 ? "（无）" : string.Join("\n", items.ToArray());
        }
    }
}
