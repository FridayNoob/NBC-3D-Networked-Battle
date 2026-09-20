// ============================================================================
//  NBV0_EnvironmentProbe —— M0 / D17 环境与 AOT 验证脚本
//  项目：3D联网战斗Demo
//
//  放置位置（等 D1 创建 Unity 工程后）：
//      Client/Assets/_Project/Tests/Manual/NBV0_EnvironmentProbe.cs
//
//  用途：
//      在【IL2CPP 打包后】的进程里验证三件事（这是 M0 的最终关卡）：
//        ① protobuf 序列化往返（**Google.Protobuf**，代码由 protoc 从 .proto 生成）——
//           验证风险 R4。注：曾用 protobuf-net，实测在 IL2CPP 下含集合字段必抛
//           NotSupportedException，已换库，详见 Docs/09-IL2CPP验证清单.md §五
//        ② xLua 能否创建 LuaEnv 并执行 Lua 代码
//        ③ 运行环境信息（是否真为 IL2CPP、监听日志）
//
//  使用方法：
//      1. 场景 Client/Assets/Scenes/Scene_AOTProbe.unity（由 M0 手册 D17 步骤 6 创建）
//      2. 场景里建一个空物体，命名 "AOTProbe"，挂上本脚本
//      3. 把该场景加入 Build Settings 的 Scenes In Build（放第一个）
//      4. 按 M0 手册 D17 用 IL2CPP 出 Release 包并运行
//      5. 观察屏幕上的大号结论文字 + Player.log
//
//  ⚠️ 本脚本刻意不使用任何项目自研类型（NBC.Shared 等），
//     只依赖生成的协议类 NBC.Protocol.ProbeMessage，目的是让 M0 阶段就能独立验证
//     第三方库，不被尚未完成的业务代码阻塞。
// ============================================================================

using System;
using System.IO;
using System.Text;
using UnityEngine;

#if NBC_HAS_PROTOBUF
using Google.Protobuf;   // ToByteArray() 等是 Google.Protobuf 里的扩展方法，必须有这个 using
using NBC.Protocol;
#endif

namespace NBC.Tests.Manual
{
    public sealed class NBV0_EnvironmentProbe : MonoBehaviour
    {
        [Header("验证开关")]
        [Tooltip("是否验证 protobuf（Google.Protobuf，protoc 生成代码）序列化往返")]
        public bool testProtobuf = true;

        [Tooltip("是否验证 xLua 执行 Lua 代码")]
        public bool testXLua = true;

        [Header("结论（运行时填充，Editor 中不可编辑）")]
        [SerializeField] private string _protobufResult = "(未执行)";
        [SerializeField] private string _xluaResult = "(未执行)";
        [SerializeField] private string _runtimeInfo = "(未执行)";

        private void Start()
        {
            Debug.Log("================ NBV0 环境探针开始 ================");

            ProbeRuntime();

            if (testProtobuf)
                ProbeProtobuf();

            if (testXLua)
                ProbeXLua();

            LogSummary();
        }

        // --------------------------------------------------------------------
        // ① 运行环境信息
        // --------------------------------------------------------------------
        private void ProbeRuntime()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Unity       : {Application.unityVersion}");
            sb.AppendLine($"Platform    : {Application.platform} / {Application.dataPath}");

#if ENABLE_IL2CPP
            sb.AppendLine("Scripting   : IL2CPP");
#else
            sb.AppendLine("Scripting   : Mono (Editor 或 Mono 出包)");
#endif

#if DEVELOPMENT_BUILD
            sb.AppendLine("Build       : Development");
#else
            sb.AppendLine("Build       : Release");
#endif
            sb.AppendLine($"PersistentData: {Application.persistentDataPath}");

            _runtimeInfo = sb.ToString().TrimEnd();
            Debug.Log("[NBV0][Runtime]\n" + _runtimeInfo);
        }

        // --------------------------------------------------------------------
        // ② protobuf 序列化往返（风险 R4 的直接验证）
        //
        //   2026-09-20 更换实现：原来是 protobuf-net，实测在 IL2CPP 下
        //   只要消息含集合字段就抛 NotSupportedException（IL2CPP 未实现
        //   RuntimeParameterInfo::GetTypeModifiers 这个 icall）→ 见 Docs/09 §五。
        //   现改为 Google.Protobuf：代码由 protoc 3.21.1 从 Protocol/nbc_probe.proto
        //   生成（纯 C#、零运行时反射），因此不受该 icall 影响。
        // --------------------------------------------------------------------
        private void ProbeProtobuf()
        {
#if !NBC_HAS_PROTOBUF
            _protobufResult = "跳过：未定义 NBC_HAS_PROTOBUF\n" +
                              "（说明 Google.Protobuf 尚未接入，见 M0 手册 D7）";
            Debug.LogWarning("[NBV0][Protobuf] " + _protobufResult);
#else
            try
            {
                var original = new ProbeMessage
                {
                    Id = 1001,
                    Name = "联网战斗Demo",
                    Hp = 3200,
                    Ratio = 0.75f,
                    Flag = true,
                };
                for (int i = 0; i < 8; i++)
                    original.SkillIds.Add(2001 + i);

                // 生成代码提供的是直接方法（无反射、无运行时模型构建）
                byte[] bytes = original.ToByteArray();
                ProbeMessage restored = ProbeMessage.Parser.ParseFrom(bytes);

                bool idOk = restored.Id == original.Id;
                bool nameOk = restored.Name == original.Name;
                bool hpOk = restored.Hp == original.Hp;
                bool ratioOk = Math.Abs(restored.Ratio - original.Ratio) < 1e-6f;
                bool flagOk = restored.Flag == original.Flag;
                bool listLenOk = restored.SkillIds.Count == original.SkillIds.Count;

                bool listContentOk = listLenOk;
                if (listLenOk)
                {
                    for (int i = 0; i < original.SkillIds.Count; i++)
                    {
                        if (restored.SkillIds[i] != original.SkillIds[i]) { listContentOk = false; break; }
                    }
                }

                bool allOk = idOk && nameOk && hpOk && ratioOk && flagOk && listContentOk;

                _protobufResult =
                    $"{(allOk ? "通过" : "失败")}  Google.Protobuf  ({bytes.Length} 字节)\n" +
                    $"  Id={restored.Id} Name={restored.Name} Hp={restored.Hp}\n" +
                    $"  SkillIds={restored.SkillIds.Count} 个（首个={FirstSkill(restored)}）\n" +
                    $"  id={idOk} name={nameOk} hp={hpOk} ratio={ratioOk} flag={flagOk} 列表={listLenOk}/{listContentOk}";

                if (allOk)
                    Debug.Log("[NBV0][Protobuf] 序列化往返成功：" + _protobufResult);
                else
                    Debug.LogError("[NBV0][Protobuf] 数据不一致：" + _protobufResult);
            }
            catch (Exception e)
            {
                _protobufResult = "异常：\n  " + e.GetType().Name + ": " + e.Message;
                Debug.LogError("[NBV0][Protobuf] " + _protobufResult + "\n" + e.StackTrace);
            }
#endif
        }

#if NBC_HAS_PROTOBUF
        private static string FirstSkill(ProbeMessage m)
            => m.SkillIds.Count > 0 ? m.SkillIds[0].ToString() : "(空)";
#endif

        // --------------------------------------------------------------------
        // ③ xLua 执行验证
        // --------------------------------------------------------------------
        private void ProbeXLua()
        {
#if !NBC_HAS_XLUA
            _xluaResult = "跳过：未定义 NBC_HAS_XLUA\n" +
                          "（说明 xLua 尚未接入，见 M0 手册 D6）";
            Debug.LogWarning("[NBV0][xLua] " + _xluaResult);
#else
            XLua.LuaEnv env = null;
            try
            {
                env = new XLua.LuaEnv();

                // 注意：这里是"内联 Lua 字符串"，不依赖外部 .lua 文件，
                //       因此可以独立验证 Lua 虚拟机与绑定是否可用。
                env.DoString(@"
                    local result = 0
                    for i = 1, 10 do
                        result = result + i
                    end
                    return 'lua ok, 1..10 = ' .. tostring(result)
                ", "NBV0Probe");

                // 用全局变量拿回结果（DoString 的返回值需要 LuaTable 承接，这里保持简单）
                env.DoString(@"
                    NBV0_SUM = 0
                    for i = 1, 10 do NBV0_SUM = NBV0_SUM + i end
                ", "NBV0Probe");

                object sum = env.Global.Get<int>("NBV0_SUM");
                bool ok = sum is int v && v == 55;

                _xluaResult = $"{(ok ? "通过" : "失败")}  Lua 求和 1..10 = {sum}（期望 55）";

                if (ok)
                    Debug.Log("[NBV0][xLua] " + _xluaResult);
                else
                    Debug.LogError("[NBV0][xLua] 结果不符：" + _xluaResult);
            }
            catch (Exception e)
            {
                _xluaResult = "异常：\n  " + e.GetType().Name + ": " + e.Message;
                Debug.LogError("[NBV0][xLua] " + _xluaResult + "\n" + e.StackTrace);
            }
            finally
            {
                // LuaEnv 必须释放，否则可能留下虚拟机资源
                if (env != null)
                {
                    try { env.Dispose(); }
                    catch (Exception e) { Debug.LogWarning("[NBV0][xLua] Dispose 异常：" + e.Message); }
                }
            }
#endif
        }

        // --------------------------------------------------------------------
        // 汇总
        // --------------------------------------------------------------------
        private void LogSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("================ NBV0 结论 ================");
            sb.AppendLine("[Runtime]").AppendLine(_runtimeInfo);
            sb.AppendLine("[protobuf]").AppendLine(_protobufResult);
            sb.AppendLine("[xLua]").AppendLine(_xluaResult);
            sb.AppendLine("==========================================");
            Debug.Log(sb.ToString());
        }

        private void OnGUI()
        {
            // 用 GUI 显示，保证在 IL2CPP 出包后（没有 Console 窗口时）也能直接看到结论
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
            };
            style.normal.textColor = Color.white;

            var box = new Rect(10, 10, Screen.width - 20, 300);
            GUI.Box(box, GUIContent.none);

            GUILayout.BeginArea(new Rect(20, 20, Screen.width - 40, 280));
            GUILayout.Label("NBV0 环境探针（M0 / D17）", style);
            GUILayout.Label("Runtime: " + _runtimeInfo.Replace("\n", " | "), style);
            GUILayout.Label("protobuf: " + Shorten(_protobufResult), style);
            GUILayout.Label("xLua: " + Shorten(_xluaResult), style);
            GUILayout.EndArea();
        }

        private static string Shorten(string s)
            => string.IsNullOrEmpty(s) ? "(空)" : s.Replace("\n", "  ");
    }
}
