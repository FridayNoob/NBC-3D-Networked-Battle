// ============================================================================
//  M1-D1 · 双端共享层的机械保证
//  对应验收：Docs/16-M1开工清单.md D1
//  需求依据：§4.2 R5（双端共享同一份战斗逻辑）、§6.4 DET-08、DET-02
//
//  ---------------------------------------------------------------------------
//  为什么要用"读文件 + 断言"来做测试
//  ---------------------------------------------------------------------------
//  D1 的约定全是**配置文件层面**的：
//    · `NBC.Shared.asmdef` 必须 `noEngineReferences: true`（帧同步硬前提）
//    · 共享源码里不许出现 `UnityEngine`
//    · 服务端工程必须**引用 Unity 侧那份源码**（而不是自己存一份）
//    · 服务端 `LangVersion` 必须钉在 Unity 的水平（C# 9）
//
//  这些约定**编译器只能挡住一部分**。比如"服务端偷偷又存了一份源码"，
//  编译器完全不会报错 —— 但那一刻双端就开始漂移了，而帧同步最怕这个。
//
//  所以用测试把它们钉死：**约定被人改坏时，测试当场变红。**
//  （这套手法和 `FrameworkLayeringTests` 断言 asmdef 引用为空是同一个思路。）
// ============================================================================

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NBC.Shared;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    /// <summary>
    /// D1：共享层装配的机械校验。
    /// </summary>
    public sealed class SharedLayerTests
    {
        /// <summary>`Assets` 目录的绝对路径。</summary>
        private static string AssetsRoot
        {
            get { return Path.GetDirectoryName(Application.dataPath); }
        }

        /// <summary>共享源码目录。</summary>
        private static string SharedDir
        {
            get { return Path.Combine(Application.dataPath, "_Project", "Shared"); }
        }

        /// <summary>共享程序集定义。</summary>
        private static string SharedAsmdef
        {
            get { return Path.Combine(SharedDir, "NBC.Shared.asmdef"); }
        }

        /// <summary>服务端共享工程。</summary>
        private static string ServerSharedCsproj
        {
            get
            {
                return Path.Combine(AssetsRoot, "..", "Server", "NBC.Shared", "NBC.Shared.csproj");
            }
        }

        // ====================================================================
        //  一、共享程序集不许碰引擎
        // ====================================================================

        /// <summary>
        /// `NBC.Shared` 必须关掉引擎引用 —— 这是帧同步的硬前提（服务端没有 UnityEngine）。
        /// </summary>
        [Test]
        public void SharedAsmdef_DisablesEngineReferences()
        {
            Assert.IsTrue(File.Exists(SharedAsmdef), "找不到 " + SharedAsmdef);

            string text = File.ReadAllText(SharedAsmdef);

            StringAssert.Contains("\"noEngineReferences\": true", text,
                "NBC.Shared 必须 noEngineReferences: true —— 否则共享逻辑可能悄悄依赖 UnityEngine，" +
                "而服务端没有它（帧同步直接废掉）");
        }

        /// <summary>`NBC.Shared` 不该引用任何别的程序集（它是双端共享的最底层）。</summary>
        [Test]
        public void SharedAsmdef_HasNoReferences()
        {
            List<string> references = ReadReferenceEntries(SharedAsmdef);

            CollectionAssert.IsEmpty(references,
                "NBC.Shared 的 references 必须为空（它是最底层，谁都不该依赖）。当前：" +
                string.Join(", ", references.ToArray()));
        }

        /// <summary>
        /// 共享源码里**真实代码**不许出现 `UnityEngine`（注释里提到不算）。
        /// <para>
        /// ⚠️ 必须排除注释行 —— 本文件顶部的说明里就写了 `UnityEngine` 这个词。
        /// 不排除的话会得到一堆假违规（这个坑本项目已经踩过 3 次了）。
        /// </para>
        /// </summary>
        [Test]
        public void SharedSource_ContainsNoUnityEngineUsage()
        {
            List<string> offenders = new List<string>();
            string[] files = Directory.GetFiles(SharedDir, "*.cs", SearchOption.AllDirectories);

            Assert.Greater(files.Length, 0, "共享目录下一个 .cs 都没有？");

            for (int i = 0; i < files.Length; i++)
            {
                string[] lines = File.ReadAllLines(files[i]);

                for (int line = 0; line < lines.Length; line++)
                {
                    string trimmed = lines[line].Trim();

                    if (trimmed.StartsWith("//"))
                    {
                        continue;
                    }

                    if (trimmed.Contains("UnityEngine"))
                    {
                        offenders.Add(Path.GetFileName(files[i]) + ":" + (line + 1) + "  " + trimmed);
                    }
                }
            }

            CollectionAssert.IsEmpty(offenders,
                "共享源码里出现了 UnityEngine（注释不算）：\n" + string.Join("\n", offenders.ToArray()));
        }

        // ====================================================================
        //  二、服务端引用的是 Unity 侧那份源码（不是自己存一份）
        // ====================================================================

        /// <summary>
        /// 服务端工程必须 `<Compile Include>` Unity 侧目录 —— 这是"一份源码"的技术落点。
        /// <para>它自己再存一份 .cs 的话，双端就开始漂移了，而编译器不会报错。</para>
        /// </summary>
        [Test]
        public void ServerProject_IncludesTheSharedFolder()
        {
            string path = ServerSharedCsproj;

            Assert.IsTrue(File.Exists(path), "找不到服务端共享工程：" + path);

            string text = File.ReadAllText(path);

            StringAssert.Contains("Client", text,
                "服务端工程应当用 <Compile Include> 引用 Unity 侧的共享源码目录");
            StringAssert.Contains("_Project", text, "引用路径里应当有 _Project");
            StringAssert.Contains("Shared", text, "引用路径里应当有 Shared");

            StringAssert.Contains("EnableDefaultCompileItems>false", text,
                "必须关掉默认编译项，否则服务端目录里再放 .cs 会被**偷偷编进去**，" +
                "于是又变成两份代码了");
        }

        /// <summary>
        /// 服务端工程里**不许再存放共享源码** —— 那些文件只应该存在于 Unity 侧。
        /// </summary>
        [Test]
        public void ServerProject_DirectoryContainsNoSourceFiles()
        {
            string dir = Path.GetDirectoryName(ServerSharedCsproj);
            string[] sources = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);

            List<string> real = new List<string>();

            for (int i = 0; i < sources.Length; i++)
            {
                // obj/ 下的是 SDK 自动生成的，不算
                if (sources[i].Contains("\\obj\\") || sources[i].Contains("/obj/"))
                {
                    continue;
                }

                real.Add(Path.GetFileName(sources[i]));
            }

            CollectionAssert.IsEmpty(real,
                "服务端共享目录里不该再存 .cs（唯一源码在 Unity 侧）。发现：" +
                string.Join(", ", real.ToArray()));
        }

        // ====================================================================
        //  三、服务端的语言版本必须和 Unity 齐平
        // ====================================================================

        /// <summary>
        /// 服务端 `LangVersion` 必须钉成 9.0。
        /// <para>
        /// ⚠️ 这条是 D1 顺手抓出来的**真隐患**：改之前服务端用 SDK 默认版本，
        /// **允许 C# 10 语法**，而 Unity 2022.3 只到 C# 9 ——
        /// 于是服务端**可以**写出"服务端编得过、Unity 编不过"的共享代码，**没有任何东西拦得住**。
        /// （改之前 `SharedInfo.cs` 恰好用了 C# 10 的文件作用域命名空间，就是一处真实隐患。）
        /// </para>
        /// </summary>
        [Test]
        public void ServerProject_PinsLangVersionToUnityLevel()
        {
            string text = File.ReadAllText(ServerSharedCsproj);

            StringAssert.Contains("<LangVersion>9.0</LangVersion>", text,
                "服务端必须把 LangVersion 钉成 9.0（与 Unity 2022.3 齐平）。" +
                "不钉的话，共享代码里可以用 C# 10 语法，而 Unity 编不过 —— " +
                "而且这个错误只有在打开 Unity 时才会暴露。");
        }

        /// <summary>
        /// 服务端工程必须关掉隐式 using。
        /// <para>
        /// 钉了 `LangVersion 9.0` 之后才暴露出来：SDK 默认开 `ImplicitUsings`，
        /// 它会自动生成带 `global using` 的文件（C# 10 特性）→ 一片 `CS8773`。
        /// 更重要的是 **Unity 根本没有隐式 using**，共享代码本来就必须写全。
        /// </para>
        /// </summary>
        [Test]
        public void ServerProject_DisablesImplicitUsings()
        {
            string text = File.ReadAllText(ServerSharedCsproj);

            StringAssert.Contains("<ImplicitUsings>disable</ImplicitUsings>", text,
                "必须关掉隐式 using —— Unity 没有这个特性，共享代码必须写全 using");
        }

        // ====================================================================
        //  四、Unity 侧真的能用到共享层（编译期证明）
        // ====================================================================

        /// <summary>
        /// Unity 侧能访问共享层的类型 —— 这条能编译通过，本身就是装配正确的证据。
        /// </summary>
        [Test]
        public void SharedInfo_IsAccessibleFromUnity()
        {
            Assert.AreEqual("NBC.Shared", SharedInfo.LayerName);
            Assert.AreEqual(1, SharedInfo.ContractVersion);
            Assert.Greater(SharedInfo.LogicTickRate, 0, "逻辑帧率必须是正数");
            StringAssert.Contains("NBC.Shared", SharedInfo.Describe());
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>从 asmdef 文本里抓出 references 数组的每一项。</summary>
        /// <param name="asmdefPath">asmdef 路径。</param>
        /// <returns>引用列表（已去空白与引号）。</returns>
        private static List<string> ReadReferenceEntries(string asmdefPath)
        {
            List<string> result = new List<string>();

            if (!File.Exists(asmdefPath))
            {
                return result;
            }

            string text = File.ReadAllText(asmdefPath);
            Match array = Regex.Match(text, "\"references\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);

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
