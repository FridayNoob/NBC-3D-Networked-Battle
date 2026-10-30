// 这个文件里有：
//   NBV0_DefineInitializer（静态类）：检测 protobuf 与 xLua 的 dll 是否接入，自动增删对应的编译符号，并清理换库遗留的旧符号
//
// 注意：编译符号手填漏了不会报错 —— 探针只会静默走「跳过」分支，等于没验；本文件就是防这个。

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace NBC.EditorTools
{
    // 主要功能：编辑器加载后扫一遍 Assets，按 dll 是否存在维护 NBC_HAS_PROTOBUF 与 NBC_HAS_XLUA 两个编译符号，并清掉换库前的旧符号。
    // 主要数据成员：DefineProtobuf / DefineXLua / DefineProtobufNetObsolete（三个编译符号名常量，取值就是符号字面量）。
    // 主要外部方法：没有公开方法；入口是静态构造函数（自动触发）与 Diagnose（诊断菜单手工触发）。
    // 谁在用它：只在本程序集内；[InitializeOnLoad] 让编辑器每次加载程序集时自动跑，另有 Tools/NBC 菜单可手工诊断；属 Editor 脚本，不进最终包。
    [InitializeOnLoad]
    public static class NBV0_DefineInitializer
    {
        // 编译符号名：取值恒为 NBC_HAS_PROTOBUF，找到 Google.Protobuf 的 dll 就加上；刻意不带库名，换库时符号不用改
        private const string DefineProtobuf = "NBC_HAS_PROTOBUF";
        // 编译符号名：取值恒为 NBC_HAS_XLUA，找到 xLua 的 dll 就加上
        private const string DefineXLua = "NBC_HAS_XLUA";

        // 已废弃的旧符号：取值恒为 NBC_HAS_PROTOBUF_NET；必须主动移除，否则会永远留在 Player Settings 里变成一个谁也说不清用途的死符号
        private const string DefineProtobufNetObsolete = "NBC_HAS_PROTOBUF_NET";

        // 功能：登记一次延迟回调，等编辑器空闲后再跑检测，不在构造函数里当场扫盘。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 只把 RunOnce 挂到 delayCall，不当场执行 —— 此时资源数据库可能还没刷新完，扫盘会误判 dll 不存在
        // 上游：Unity 编辑器在程序集加载后自动调用（InitializeOnLoad）
        // 下游：RunOnce
        static NBV0_DefineInitializer()
        {
            EditorApplication.delayCall += RunOnce;
        }

        // 功能：扫一遍 Assets，按 dll 是否存在把三个编译符号改成应有的状态，有改动时打日志并触发重编译。
        // 入参：无
        // 返回：无
        // 步骤：
        //   1) 先找 Google.Protobuf 与 xLua 的 dll，得到这两个符号「应该有」的结论
        //   2) 三个符号逐个 ApplyDefine，把三次结果累积起来，只要有一个变了就值得重编译
        //   3) 旧符号 NBC_HAS_PROTOBUF_NET 固定按「不应该有」处理，顺手清掉历史遗留
        //   4) 只有真变了才打日志 —— 没变还打，每次编译都会刷一条
        // 上游：静态构造函数登记的 delayCall
        // 下游：FindProtobufDll / FindXLuaDll / ApplyDefine
        private static void RunOnce()
        {
            bool hasProtobuf = FindProtobufDll() != null;
            bool hasXLua = FindXLuaDll() != null;

            bool changed = false;
            changed |= ApplyDefine(DefineProtobuf, hasProtobuf);
            changed |= ApplyDefine(DefineXLua, hasXLua);
            changed |= ApplyDefine(DefineProtobufNetObsolete, false);

            if (changed)
            {
                Debug.Log($"[NBV0] 已更新编译符号：{DefineProtobuf}={hasProtobuf}, {DefineXLua}={hasXLua}。" +
                          "Unity 将重新编译，之后即可运行 NBV0_EnvironmentProbe。");
            }
        }

        // 功能：在 Assets 下按候选文件名找 Google.Protobuf 的 dll。
        // 入参：无
        // 返回：命中文件的绝对路径；找不到返回 null，探针就只会走「跳过」分支
        // 步骤：
        //   1) 先试 Google.Protobuf.dll，再试 Google.Protobuf.Core.dll —— 不同导入方式落盘的文件名不一样
        //   2) 命中就返回，不继续试后面的候选
        // 上游：RunOnce / Diagnose
        // 下游：FindFileInAssets
        // 注意：找不到不算错误；若明明放了 dll 却一直显示跳过，用诊断菜单打印实际扫到的文件名，核对大小写与命名。
        private static string FindProtobufDll()
        {
            var candidates = new[] { "Google.Protobuf.dll", "Google.Protobuf.Core.dll" };
            foreach (var name in candidates)
            {
                var found = FindFileInAssets(name);
                if (found != null) return found;
            }
            return null;
        }

        // 功能：在 Assets 下按候选文件名找 xLua 的 dll。
        // 入参：无
        // 返回：命中文件的绝对路径；找不到返回 null，探针只会走「跳过」分支
        // 步骤：
        //   1) 先试 XLua.dll，再试全小写的 xlua.dll —— Windows 不区分大小写，但 macOS / Linux 上导入后的文件名不一定还是 XLua
        //   2) 命中就返回，不继续试后面的候选
        // 上游：RunOnce / Diagnose
        // 下游：FindFileInAssets
        private static string FindXLuaDll()
        {
            var candidates = new[] { "XLua.dll", "xlua.dll" };
            foreach (var name in candidates)
            {
                var found = FindFileInAssets(name);
                if (found != null) return found;
            }
            return null;
        }

        // 功能：在 Assets 下递归找第一个同名文件，返回它的绝对路径。
        // 入参：fileName 纯文件名（含扩展名；不能带目录，带了会被当成搜索模式，一个都匹配不到）
        // 返回：第一个命中文件的绝对路径；没有命中或扫描抛异常时返回 null
        // 步骤：
        //   1) 用 EnumerateFiles 懒枚举，Take(1) 只取第一个就停，不为判断存在性把整个工程列一遍
        //   2) 有命中取第一个，没有就返回 null
        //   3) 整段包 try/catch —— 权限或路径异常只告警并返回 null，扫盘失败不该阻断编辑器加载
        // 上游：FindProtobufDll / FindXLuaDll
        // 下游：无
        private static string FindFileInAssets(string fileName)
        {
            try
            {
                var hits = Directory
                    .EnumerateFiles(Application.dataPath, fileName, SearchOption.AllDirectories)
                    .Take(1)
                    .ToArray();
                return hits.Length > 0 ? hits[0] : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBV0] 扫描 Assets 时出错：" + e.Message);
                return null;
            }
        }

        // 功能：把单个编译符号改成「应该有」或「不应该有」，并报告是否真的改动了 Player Settings。
        // 入参：define 符号名（不带分隔符）；shouldHave true 表示应当存在，false 表示应当移除
        // 返回：true 表示符号集合被改了、Unity 会重编译；false 表示本来就符合预期，或当前平台组未知没法写
        // 步骤：
        //   1) 取当前选中的 BuildTargetGroup，Unknown 直接放弃 —— 没有确定的平台，写下去会落到错误的组
        //   2) 转成 NamedBuildTarget 并读出现有符号
        //   3) 现状与期望一致就返回 false —— 每次加载都重写设置会白触发一次全量重编译
        //   4) 否则转成 List 做增 / 删，Distinct 去重后写回
        // 上游：RunOnce
        // 下游：PlayerSettings.GetScriptingDefineSymbols / SetScriptingDefineSymbols
        private static bool ApplyDefine(string define, bool shouldHave)
        {
            var group = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (group == BuildTargetGroup.Unknown)
                return false;

            var target = NamedBuildTarget.FromBuildTargetGroup(group);
            PlayerSettings.GetScriptingDefineSymbols(target, out var symbols);

            bool has = symbols != null && symbols.Contains(define);
            if (has == shouldHave)
                return false;

            var list = (symbols ?? Array.Empty<string>()).ToList();
            if (shouldHave)
                list.Add(define);
            else
                list.RemoveAll(s => s == define);

            PlayerSettings.SetScriptingDefineSymbols(target, list.Distinct().ToArray());
            return true;
        }

        // 功能：把两个 dll 的查找结果、当前平台已生效的符号、以及 Assets 下名字含 proto / lua 的 dll 打到 Console，用来排查自动检测为什么没生效。
        // 入参：无（菜单无参入口）
        // 返回：无
        // 步骤：
        //   1) 先跑正式查找逻辑，输出两个 dll 的命中路径或「未找到」
        //   2) 再打印当前平台的符号，确认符号到底有没有写进去
        //   3) 最后单独扫一遍 *.dll，列出文件名含 proto 或 lua 的，用来核对真实文件名（路径换成 Assets 开头的相对形式，方便直接定位）
        //   4) 扫描异常只写进输出文本，不让诊断菜单本身崩掉
        // 上游：Unity 菜单 Tools/NBC 下的诊断项（人工点击）
        // 下游：FindProtobufDll / FindXLuaDll
        [MenuItem("Tools/NBC/诊断 M0 探针依赖")]
        private static void Diagnose()
        {
            var protobuf = FindProtobufDll();
            var xlua = FindXLuaDll();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== M0 探针依赖诊断 ===");
            sb.AppendLine("Google.Protobuf : " + (protobuf ?? "未找到"));
            sb.AppendLine("xLua         : " + (xlua ?? "未找到"));
            sb.AppendLine();
            sb.AppendLine("当前编译符号 : " +
                          string.Join(", ", PlayerSettings.GetScriptingDefineSymbols(
                              NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup))));
            sb.AppendLine();
            sb.AppendLine("在 Assets 下扫描到的相关 dll：");

            try
            {
                foreach (var f in Directory.EnumerateFiles(Application.dataPath, "*.dll", SearchOption.AllDirectories))
                {
                    var n = Path.GetFileName(f).ToLowerInvariant();
                    if (n.Contains("proto") || n.Contains("lua"))
                        sb.AppendLine("  " + f.Replace(Application.dataPath, "Assets"));
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("  扫描失败：" + e.Message);
            }

            Debug.Log(sb.ToString());
        }
    }
}
