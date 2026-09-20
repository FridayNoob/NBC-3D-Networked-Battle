// ============================================================================
//  NBV0_EnvironmentProbe —— M0 / D17 环境与 AOT 验证脚本
//  项目：3D联网战斗Demo
//
//  放置位置（等 D1 创建 Unity 工程后）：
//      Client/Assets/_Project/Tests/Manual/NBV0_EnvironmentProbe.cs
//
//  用途：
//      在【IL2CPP 打包后】的进程里验证三件事（这是 M0 的最终关卡）：
//        ① protobuf-net 能否正常序列化/反序列化（验证 AOT 裁剪问题，风险 R4）
//        ② xLua 能否创建 LuaEnv 并执行 Lua 代码
//        ③ 运行环境信息（是否真为 IL2CPP、监听日志）
//
//  使用方法：
//      1. 新建场景 Client/Assets/Scenes/Scene_Test_AOT.unity
//      2. 场景里建一个空物体，命名 "EnvProbe"，挂上本脚本
//      3. 把该场景加入 Build Settings 的 Scenes In Build（放第一个）
//      4. 按 M0 手册 D17 用 IL2CPP 出 Release 包并运行
//      5. 观察屏幕上的大号结论文字 + Player.log
//
//  ⚠️ 本脚本刻意不使用任何项目自研类型（NBC.Shared 等），
//     目的是让 M0 阶段就能独立验证第三方库，不被尚未完成的业务代码阻塞。
// ============================================================================

using System;
using System.IO;
using System.Text;
using UnityEngine;

#if NBC_HAS_PROTOBUF_NET
using ProtoBuf;
#endif

namespace NBC.Tests.Manual
{
    public sealed class NBV0_EnvironmentProbe : MonoBehaviour
    {
        [Header("验证开关")]
        [Tooltip("是否验证 protobuf-net 序列化往返")]
        public bool testProtobufNet = true;

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

            if (testProtobufNet)
                ProbeProtobufNet();

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
        // ② protobuf-net 序列化往返（风险 R4 的直接验证）
        // --------------------------------------------------------------------
        private void ProbeProtobufNet()
        {
#if !NBC_HAS_PROTOBUF_NET
            _protobufResult = "跳过：未定义 NBC_HAS_PROTOBUF_NET\n" +
                              "（说明 protobuf-net 尚未接入，见 M0 手册 D7）";
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

                byte[] bytes;
                using (var ms = new MemoryStream())
                {
                    Serializer.Serialize(ms, original);
                    bytes = ms.ToArray();
                }

                ProbeMessage restored;
                using (var ms = new MemoryStream(bytes))
                {
                    restored = Serializer.Deserialize<ProbeMessage>(ms);
                }

                bool nameOk = restored.Name == original.Name;
                bool hpOk = restored.Hp == original.Hp;
                bool listOk = restored.SkillIds.Count == original.SkillIds.Count;
                bool allOk = nameOk && hpOk && listOk && restored.Id == original.Id;

                _protobufResult =
                    $"{(allOk ? "通过" : "失败")}  ({bytes.Length} 字节)\n" +
                    $"  Id={restored.Id} Name={restored.Name} Hp={restored.Hp}\n" +
                    $"  SkillIds={restored.SkillIds.Count} 个\n" +
                    $"  Name匹配={nameOk} Hp匹配={hpOk} 列表长度匹配={listOk}";

                if (allOk)
                    Debug.Log("[NBV0][Protobuf] 序列化往返成功：" + _protobufResult);
                else
                    Debug.LogError("[NBV0][Protobuf] 数据不一致：" + _protobufResult);
            }
            catch (Exception e)
            {
                // 这一步失败最常见的原因就是 AOT 裁剪 —— 见 M0 手册 D7 / D17 排查表
                _protobufResult = "异常（极可能是 AOT/裁剪问题）：\n  " + e.GetType().Name + ": " + e.Message;
                Debug.LogError("[NBV0][Protobuf] " + _protobufResult + "\n" + e.StackTrace);
            }
#endif
        }

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
            sb.AppendLine("[protobuf-net]").AppendLine(_protobufResult);
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
            GUILayout.Label("protobuf-net: " + Shorten(_protobufResult), style);
            GUILayout.Label("xLua: " + Shorten(_xluaResult), style);
            GUILayout.EndArea();
        }

        private static string Shorten(string s)
            => string.IsNullOrEmpty(s) ? "(空)" : s.Replace("\n", "  ");
    }

#if NBC_HAS_PROTOBUF_NET
    /// <summary>
    /// 探针专用的 protobuf 消息。
    /// </summary>
    /// <remarks>
    /// 刻意定义在测试脚本内部，不引用项目的协议类 —— 这样即使协议层还没写，
    /// M0 也能独立验证 protobuf-net 本身在 IL2CPP 下能否工作。
    /// 正式协议类会放在 NBC.Shared/Protocol/（见需求文档 §6.7）。
    /// </remarks>
    [ProtoContract]
    public sealed class ProbeMessage
    {
        [ProtoMember(1)] public int Id { get; set; }
        [ProtoMember(2)] public string Name { get; set; }
        [ProtoMember(3)] public int Hp { get; set; }
        [ProtoMember(4)] public float Ratio { get; set; }
        [ProtoMember(5)] public bool Flag { get; set; }
        [ProtoMember(6)] public System.Collections.Generic.List<int> SkillIds { get; } = new();
    }
#endif
}
