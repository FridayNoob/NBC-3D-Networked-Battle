// ============================================================================
//  NBC.Server.Host —— 服务端进程入口
//  项目：3D联网战斗Demo
//  对应需求文档：Docs/01-项目需求文档.md §13.3
//
//  M0 阶段目标：让 `dotnet run` 能启动并打印版本信息，验证骨架可用。
//  后续里程碑会在这里接入：配置加载 → 日志 → 数据库 → 网络监听 → 主循环
// ============================================================================

using System.Reflection;
using NBC.Shared;

namespace NBC.Server.Host;

internal static class Program
{
    /// <summary>
    /// 服务端进程入口。
    /// </summary>
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        Console.WriteLine("==============================================");
        Console.WriteLine("  NBC Server (Networked Battle Combat)");
        Console.WriteLine($"  Version : {version}");
        Console.WriteLine($"  Runtime : {Environment.Version}");
        Console.WriteLine($"  OS      : {Environment.OSVersion}");
        Console.WriteLine($"  Machine : {Environment.MachineName}");
        Console.WriteLine("==============================================");
        Console.WriteLine();

        // 验证 NBC.Shared 可被服务端引用（双端共享逻辑的前提，需求文档 §4.2 R5）
        Console.WriteLine($"[Check] NBC.Shared 可引用，共享层版本标识：{SharedInfo.LayerName}");
        Console.WriteLine();

        Console.WriteLine("[M0] 骨架启动成功。以下模块将在后续里程碑接入：");
        Console.WriteLine("     M1: 日志 / 定点数学（NBC.Shared）");
        Console.WriteLine("     M3: 配置加载 / 数据库 / TCP 网络层 / 房间流程");
        Console.WriteLine("     M4: 定长帧循环 / 三种同步策略");
        Console.WriteLine();
        Console.WriteLine("按 Ctrl+C 退出（M0 阶段会立即结束）。");

        // M0 阶段：不进入主循环，直接成功退出，便于自动化验证
        return 0;
    }
}
