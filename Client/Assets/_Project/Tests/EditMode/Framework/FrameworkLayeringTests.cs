// ============================================================================
//  NBC.Tests.EditMode · 分层方向的机械校验（M1-A11 的 Plan B）
//  建立日期：2026-09-20
//  对应：需求文档 §7.1.1 FW-12、§4.2 R4；Docs/06-框架改造记录.md §八 第 4 项、§十一
//
//  ---------------------------------------------------------------------------
//  为什么会有这个文件（一段真实的翻车记录）
//  ---------------------------------------------------------------------------
//  FW-12 的修法原文写的是："靠 asmdef 引用方向**机械保证**框架层不得引用业务层"。
//  "机械保证"的意思是：**即使有人手滑，也编译不过**，不依赖自觉。
//
//  为了把这句话从"设计意图"变成"实测事实"，2026-09-20 做了一次实验：
//  临时把 `Framework.asmdef` 的 references 加上 `NBC.Game`，期待 Unity 报循环引用。
//
//  结果：**没有报错**。但进一步测量发现这次实验**根本没跑起来**：
//    · `Framework.asmdef` 的修改时间是 17:07:54
//    · 而 `Library/ScriptAssemblies/NBC.Framework.dll` 的编译时间是 17:04:28
//    · Unity 的 Editor.log 里那一次之后**没有任何 Csc 编译记录**
//    → 也就是说：**"没报错"的真实含义是"还没轮到它报错"**，
//      而不是"Unity 允许循环引用"。
//
//  同时还发现一件事：`Model.asmdef`（以及同样没有脚本的 `Game.asmdef`）会被 Unity 警告
//  "will not be compiled, because it has no scripts associated with it" ——
//  **空程序集根本不会被生成**，那么"引用一个不存在的程序集"是否还算循环，就更说不清了。
//
//  ---------------------------------------------------------------------------
//  结论：不要依赖"期待编辑器报错"，改成一条自己能跑的断言
//  ---------------------------------------------------------------------------
//  两个理由：
//    ① 依赖编辑器行为 = 依赖一个我们**没有实测过**的前提（而本次实测是 inconclusive）；
//    ② 就算它真的会报错，那也只在**打开编辑器时**报，进不了 CI、也不会在别人机器上自动跑。
//
//  所以 Plan B 是：把"分层方向"变成**EditMode 测试里的一条断言**。
//  这样它随 V13 的批量测试自动覆盖，失败信息也由我们自己写。
//
//  ---------------------------------------------------------------------------
//  ⚠️ 这个文件里最值得学的一招：给自己配一个"阳性对照"
//  ---------------------------------------------------------------------------
//  一个"断言 Framework 不引用业务程序集"的测试有个致命弱点：
//  **如果解析器写错了、什么都没解析出来，测试会永远绿**。
//  所以这里额外加了 `PositiveControl_...` 与 `GuidResolver_...` 两条用例：
//  它们断言"确实解析到了 Game -> Framework 这条真实存在的引用"。
//  解析器一旦失灵，这两条会先红 —— 这就是工作规则 W9（验证器本身也要被验证）的落地做法。
// ============================================================================

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace NBC.Tests.EditMode
{
    [TestFixture]
    public class FrameworkLayeringTests
    {
        /// <summary>Assets 目录的绝对路径。EditMode 下 `Application.dataPath` 就是它。</summary>
        private static string AssetsRoot
        {
            get { return Application.dataPath; }
        }

        private static string FrameworkAsmdef
        {
            get { return Path.Combine(AssetsRoot, "_Project", "Framework", "Framework.asmdef"); }
        }

        private static string GameAsmdef
        {
            get { return Path.Combine(AssetsRoot, "_Project", "Game", "Game.asmdef"); }
        }

        // ====================================================================
        //  被测断言：框架层不得引用任何东西（含第三方）
        // ====================================================================

        [Test]
        public void FrameworkAssembly_MustHaveNoReferencesAtAll()
        {
            Assert.IsTrue(File.Exists(FrameworkAsmdef), "找不到 Framework.asmdef：" + FrameworkAsmdef);

            List<string> references = ResolveReferences(FrameworkAsmdef);

            // ⚠️ 这条断言原来写的是"不得引用 NBC.*"，**太弱了**。
            // 2026-09-20 讨论 YooAsset 适配层放哪时发现：YooAsset 不是 NBC.*，
            // 也就是说"把 YooAsset 塞进 NBC.Framework"照样能让那条测试变绿。
            // 真正的约束是：**框架底座什么都不依赖** —— 所以改成断言"references 必须为空"。
            CollectionAssert.IsEmpty(
                references,
                "NBC.Framework 的 references 必须为空（底座不依赖任何东西，含第三方）。" +
                "当前引用了：" + string.Join(", ", references.ToArray()) + "。" +
                "需要接触第三方（如 YooAsset）的代码请放进独立的适配层程序集，见 Docs/02 ADR-001 决策 6。");
        }

        [Test]
        public void GameAssembly_MustNotReferenceYooAsset()
        {
            Assert.IsTrue(File.Exists(GameAsmdef), "找不到 Game.asmdef：" + GameAsmdef);

            List<string> references = ResolveReferences(GameAsmdef);

            // 需求 YOO-09 / 约束 C1：业务层永远不该看见 YooAsset。
            // 这条断言比 `rg "YooAsset"` 更可靠：不受注释、字符串字面量影响，看的是**真实的程序集依赖**。
            CollectionAssert.DoesNotContain(
                references, "YooAsset",
                "分层被破坏：NBC.Game 直接引用了 YooAsset 程序集。业务层只能依赖 AssetManager 接口，" +
                "不能依赖具体资源库（否则换资源方案时业务层全要改）。见 Docs/02 ADR-001 决策 3。");
        }

        // ====================================================================
        //  阳性对照：证明"解析器真的解析出了东西"
        // ====================================================================

        [Test]
        public void PositiveControl_GameAssembly_DoesReferenceFramework()
        {
            Assert.IsTrue(File.Exists(GameAsmdef), "找不到 Game.asmdef：" + GameAsmdef);

            List<string> references = ResolveReferences(GameAsmdef);

            CollectionAssert.Contains(
                references, "NBC.Framework",
                "阳性对照失败：解析器没能从 Game.asmdef 里读出 'NBC.Framework'。" +
                "这说明解析逻辑坏了 —— 此时上面那条'框架不引用业务'的断言是**假绿**。");
        }

        [Test]
        public void GuidResolver_ResolvesRealGuidFromMetaFile()
        {
            // Unity 在 Inspector 里选引用时，会把名字写成 "GUID:xxxx"，
            // 所以解析器必须能吃下这种形式 —— 而项目里平常可能一个都没有，
            // 于是用"现读一个真实 .meta 的 guid"来验证这条分支。
            string metaPath = FrameworkAsmdef + ".meta";
            Assert.IsTrue(File.Exists(metaPath), "找不到 asmdef 的 .meta：" + metaPath);

            string guid = ReadGuid(metaPath);
            Assert.IsFalse(string.IsNullOrEmpty(guid), "Framework.asmdef.meta 里没读到 guid");

            Dictionary<string, string> map = BuildGuidMap();
            Assert.IsTrue(map.ContainsKey(guid), "guid 映射表里没有 " + guid);
            Assert.AreEqual("NBC.Framework", map[guid], "guid 映射结果不对");
        }

        [Test]
        public void GuidMap_CoversEveryAsmdefInTheProject()
        {
            Dictionary<string, string> map = BuildGuidMap();

            Assert.IsTrue(map.ContainsKey(ReadGuid(GameAsmdef + ".meta")), "Game.asmdef 未进入映射表");
            Assert.IsTrue(map.ContainsKey(ReadGuid(FrameworkAsmdef + ".meta")), "Framework.asmdef 未进入映射表");
        }

        // ====================================================================
        //  源码级网关检查：只有适配层可以 using YooAsset
        //  —— 补的是**编译闸门的盲区**（2026-09-20 被真实错误逼出来的）
        // ====================================================================

        /// <summary>
        /// 除了适配层目录，任何源码都不得 `using YooAsset;`。
        /// <para>
        /// **为什么需要这条**：编译闸门（`Server\_api-probe`）把**所有源码编进同一个程序集**，
        /// 所以它验证得了"C# 写得对不对"，**验证不了"程序集接得对不对"**。
        /// 真实踩到的例子：`YooAssetAdapterSmokeTests` 里写了 `using YooAsset;` 去调 `YooAssets.Destroy()`，
        /// 闸门绿灯通过，Unity 里却报 `CS0246` —— 因为 **Unity 的 asmdef 引用不传递**：
        /// 测试引用了 `NBC.Framework.YooAsset`，仍然看不到适配层引用的 `YooAsset`。
        /// </para>
        /// <para>
        /// ⚠️ **它的局限（诚实标注）**：只查 `using` 指令这一行，且跳过 `//` 注释行。
        /// 完全限定名（`YooAsset.AssetHandle`）绕过它查不出来。
        /// 它是一条"网关检查"，与程序集级断言（`GameAssembly_MustNotReferenceYooAsset`）**互补**，不是替代。
        /// </para>
        /// </summary>
        [Test]
        public void OnlyAdapterSource_MayImportYooAssetNamespace()
        {
            string projectDir = Path.Combine(AssetsRoot, "_Project");
            string adapterDir = Path.Combine(projectDir, "Framework.YooAsset");

            Assert.IsTrue(Directory.Exists(adapterDir), "找不到适配层目录：" + adapterDir);

            List<string> offenders = new List<string>();
            string[] files = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories);

            for (int i = 0; i < files.Length; i++)
            {
                if (files[i].StartsWith(adapterDir, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;   // 适配层就是那个"唯一允许"的地方
                }

                string[] lines = File.ReadAllLines(files[i]);
                for (int line = 0; line < lines.Length; line++)
                {
                    string trimmed = lines[line].TrimStart();
                    if (trimmed.StartsWith("//"))
                    {
                        continue;   // 注释里提到 YooAsset 是允许的（文档说明需要）
                    }

                    if (Regex.IsMatch(trimmed, @"^using\s+YooAsset\s*;"))
                    {
                        offenders.Add(files[i].Substring(projectDir.Length + 1) + ":" + (line + 1));
                    }
                }
            }

            CollectionAssert.IsEmpty(
                offenders,
                "只有 NBC.Framework.YooAsset 可以 using YooAsset。越界文件：" +
                string.Join(", ", offenders.ToArray()) +
                "。业务/测试代码请走框架的 AssetManager / 适配层公开 API。");
        }

        // ====================================================================
        //  解析实现（不依赖 UnityEditor / 第三方 JSON 库，纯 BCL）
        //  —— 这样它也能被 Server\_api-probe 的编译闸门覆盖到
        // ====================================================================

        /// <summary>读出 asmdef 的 references，并把 GUID 形式解析成程序集名。</summary>
        private static List<string> ResolveReferences(string asmdefPath)
        {
            List<string> raw = ReadReferenceEntries(asmdefPath);
            Dictionary<string, string> map = BuildGuidMap();

            List<string> resolved = new List<string>();
            for (int i = 0; i < raw.Count; i++)
            {
                string entry = raw[i];
                if (entry.StartsWith("GUID:"))
                {
                    string guid = entry.Substring(5);
                    string name;
                    resolved.Add(map.TryGetValue(guid, out name) ? name : entry);
                }
                else
                {
                    resolved.Add(entry);
                }
            }

            return resolved;
        }

        /// <summary>从 asmdef 文本里抓出 references 数组中的每一项（不引入 JSON 库）。</summary>
        private static List<string> ReadReferenceEntries(string asmdefPath)
        {
            List<string> result = new List<string>();
            string text = File.ReadAllText(asmdefPath);

            Match array = Regex.Match(text, "\"references\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!array.Success)
            {
                return result;
            }

            foreach (Match item in Regex.Matches(array.Groups[1].Value, "\"([^\"]+)\""))
            {
                result.Add(item.Groups[1].Value);
            }

            return result;
        }

        /// <summary>扫描 Assets 下所有 *.asmdef.meta，建立 guid -> 程序集名 的映射。</summary>
        private static Dictionary<string, string> BuildGuidMap()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();

            string[] metaFiles = Directory.GetFiles(AssetsRoot, "*.asmdef.meta", SearchOption.AllDirectories);
            for (int i = 0; i < metaFiles.Length; i++)
            {
                string asmdefPath = metaFiles[i].Substring(0, metaFiles[i].Length - ".meta".Length);
                string name = ReadAsmdefName(asmdefPath);
                string guid = ReadGuid(metaFiles[i]);

                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(guid) && !map.ContainsKey(guid))
                {
                    map.Add(guid, name);
                }
            }

            return map;
        }

        /// <summary>读取 asmdef 里的程序集名。</summary>
        private static string ReadAsmdefName(string asmdefPath)
        {
            if (!File.Exists(asmdefPath))
            {
                return null;
            }

            Match m = Regex.Match(File.ReadAllText(asmdefPath), "\"name\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>读取 .meta 里的 guid。</summary>
        private static string ReadGuid(string metaPath)
        {
            Match m = Regex.Match(File.ReadAllText(metaPath), "^guid:\\s*([0-9a-fA-F]+)", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
