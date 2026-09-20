// ============================================================================
//  NBV0_DefineInitializer —— 自动注入 M0 探针所需的编译符号
//  项目：3D联网战斗Demo
//
//  放置位置（等 D1 创建 Unity 工程后）：
//      Client/Assets/_Project/Editor/NBV0_DefineInitializer.cs
//
//  为什么需要它：
//      NBV0_EnvironmentProbe.cs 用 #if NBC_HAS_PROTOBUF / #if NBC_HAS_XLUA 来
//      "有则验证、无则跳过"。但这两个符号默认不存在，手填容易漏，
//      而且手填错了不会报错、只会静默走"跳过"分支 —— 那 D17 就白验了。
//      本脚本在 Unity 编译完成后自动检测 dll 是否存在，并注入/移除符号。
//
//  2026-09-20 换库：序列化从 protobuf-net 改为 Google.Protobuf，因此
//      · 检测目标从 protobuf-net.dll 改为 Google.Protobuf.dll
//      · 符号名从 NBC_HAS_PROTOBUF_NET 改为 NBC_HAS_PROTOBUF（去掉库名，换库不再改符号）
//      · 并且会主动清理遗留的旧符号 NBC_HAS_PROTOBUF_NET（否则它会一直留在
//        Player Settings 里，变成一个谁也说不清用途的死符号）
//
//  ⚠️ 这是【Editor 脚本】，只在编辑器里运行，不会打进最终包。
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace NBC.EditorTools
{
    /// <summary>
    /// 检测 protobuf（Google.Protobuf）/ xLua 是否已接入，自动维护对应的 Scripting Define Symbols。
    /// </summary>
    [InitializeOnLoad]
    public static class NBV0_DefineInitializer
    {
        private const string DefineProtobuf = "NBC_HAS_PROTOBUF";
        private const string DefineXLua = "NBC_HAS_XLUA";

        /// <summary>已废弃的符号：换库前的 NBC_HAS_PROTOBUF_NET，检测到就清掉。</summary>
        private const string DefineProtobufNetObsolete = "NBC_HAS_PROTOBUF_NET";

        static NBV0_DefineInitializer()
        {
            // 延迟一帧，避免在 Unity 尚未完成资源数据库刷新时访问文件系统
            EditorApplication.delayCall += RunOnce;
        }

        private static void RunOnce()
        {
            bool hasProtobuf = FindProtobufDll() != null;
            bool hasXLua = FindXLuaDll() != null;

            bool changed = false;
            changed |= ApplyDefine(DefineProtobuf, hasProtobuf);
            changed |= ApplyDefine(DefineXLua, hasXLua);
            changed |= ApplyDefine(DefineProtobufNetObsolete, false);   // 清理遗留符号

            if (changed)
            {
                Debug.Log($"[NBV0] 已更新编译符号：{DefineProtobuf}={hasProtobuf}, {DefineXLua}={hasXLua}。" +
                          "Unity 将重新编译，之后即可运行 NBV0_EnvironmentProbe。");
            }
        }

        /// <summary>
        /// 在工程内查找 Google.Protobuf 的 dll。
        /// </summary>
        /// <remarks>
        /// ⚠️ 找不到时会返回 null，探针就只会走"跳过"分支。
        /// 如果 D7 明明放了 dll 却一直显示跳过，用菜单
        /// Tools/NBC/诊断 M0 探针依赖 打印实际扫描到的文件，确认 dll 名称是否与预期不同。
        /// </remarks>
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

        // --------------------------------------------------------------------
        // 诊断菜单：当自动检测没生效时，用它查实际文件名
        // --------------------------------------------------------------------
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
