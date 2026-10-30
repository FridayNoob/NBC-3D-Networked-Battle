// 这个文件里有：
//   AssetRuntimeBootstrapMenu（静态类）：资源运行模式的编辑器冒烟菜单，装配好资源层后真加载一个地址，把这条路通不通打在 Console 上。
//
// 注意：运行模式是初始化时传进去的参数（不是窗口开关），换模式只能重新装配一次；这个菜单就是编辑器里手动重跑一次装配的入口。

using System;
using System.Threading.Tasks;
using NBC.Boot;
using NBC.Framework.Asset;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    // 主要功能：给 EditorSimulate / Offline 两种资源运行模式各放一个菜单项，点一下即装配资源层、初始化并试加载一个真实地址。
    // 主要数据成员：ProbeLocation（冒烟地址）、PackageName（YooAsset 包名）、SimulatePackageRoot（编辑器模拟清单目录）。
    // 主要外部方法：无 public 方法；两个菜单项 InitEditorSimulate / InitOffline 由 UnityEditor 按 MenuItem 路径调用。
    // 谁在用它：只在编辑器里由菜单 Tools/NBC/资源 触发，下游落到 NBC.Boot 的 AssetBootstrapper；没有运行时代码引用它。
    public static class AssetRuntimeBootstrapMenu
    {
        // 冒烟试加载的资源地址，必须是收集配置里真实存在的地址；当前取配置表产物 HeroConfig（走 AddressByFileName 命名规则），收集配置换了地址规则这里要跟着改。
        private const string ProbeLocation = "HeroConfig";

        // YooAsset 的包名，必须与收集配置里的包名一致（实测当前为 DefaultPackage，取自 AssetBootstrapper 的默认值）。
        private const string PackageName = AssetBootstrapper.DefaultPackageName;

        // 编辑器模拟模式的模拟清单目录；空串 = 没指定。只有 EditorSimulate 模式读它（Offline 读 StreamingAssets，传空即可）；要跑 EditorSimulate 冒烟就得改成模拟构建的输出目录。
        private const string SimulatePackageRoot = "";

        // 功能：用编辑器模拟模式跑一遍冒烟（日常开发：免打包，改资源即时生效）。
        [MenuItem("Tools/NBC/资源/① EditorSimulate 模式（日常开发）")]
        private static void InitEditorSimulate()
        {
            Run(AssetRuntimeMode.EditorSimulate);
        }

        // 功能：用内置（Offline）模式跑一遍冒烟，读的是 StreamingAssets 里的内容包。
        [MenuItem("Tools/NBC/资源/② Offline 模式（读内置包）")]
        private static void InitOffline()
        {
            Run(AssetRuntimeMode.Offline);
        }

        // 功能：不在播放模式就拦下来并提示，在播放模式才把模式转给 ProbeAsync。
        // 入参：mode 运行模式（EditorSimulate / Offline；Host 尚未实现）
        // 返回：无
        // 步骤：
        //   1) 先查 Application.isPlaying —— 资源系统是运行时的东西，编辑模式里初始化没有意义
        //   2) 不在播放模式就只打一条警告并返回，不让探针在编辑模式下给出假结论
        //   3) 在播放模式就 fire-and-forget 交给 ProbeAsync（菜单回调不能 await）
        // 上游：UnityEditor 的菜单系统（Tools/NBC/资源）
        // 下游：ProbeAsync
        // 注意：Offline 模式依赖 StreamingAssets，只有真正进了播放模式才成立。
        private static void Run(AssetRuntimeMode mode)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning(
                    "[资源冒烟] 请先 **进入播放模式** 再点这个菜单。\n" +
                    "原因：资源系统是运行时的东西；`Offline` 模式还要真的去读 StreamingAssets，\n" +
                    "在编辑模式里初始化拿不到有意义的结果。");
                return;
            }

            ProbeAsync(mode);
        }

        // 功能：先装配再初始化资源层，然后真加载一个地址并打日志，当场给出这条路通不通的结论。
        // 入参：mode 运行模式（EditorSimulate 读编辑器模拟清单，Offline 读随包内容）
        // 返回：无（async void，结果只落在 Console 日志里）
        // 步骤：
        //   1) 装配与初始化都走组合根 InstallAsync：AssetManager 只是门面，没人先把加载器装上去就抛还没有装上底层加载器
        //   2) 初始化成功证明不了能取到资源，再真加载 ProbeLocation 一次
        //   3) 泛型参数必须是 ScriptableObject：配置表产物 HeroConfig.asset 就是它
        //   4) 句柄为 null 与资产为 null 都要判，任一成立都算失败并提前返回
        //   5) 成功后 Dispose 句柄，把引用计数还回去
        // 上游：Run（两个菜单项最终都到这里）
        // 下游：AssetBootstrapper.InstallAsync、AssetManager.Instance.LoadAsset、AssetHandle.Dispose
        // 注意：类型不匹配时 YooAsset 不抛异常，只打一条 Failed to load asset 并返回 null 资产 —— 只判句柄非 null 就会把失败报成成功。
        // 注意：Offline 最常见的失败是还没打包、或 Build 输出没拷进 StreamingAssets，所以 catch 里直接写清该做什么，而不只丢一个堆栈。
        private static async void ProbeAsync(AssetRuntimeMode mode)
        {
            try
            {
                Debug.Log("[资源冒烟] 开始装配 + 初始化：" + mode);

                await AssetBootstrapper.InstallAsync(mode, PackageName, SimulatePackageRoot);

                Debug.Log("[资源冒烟] ✅ 装配 + 初始化成功：" + mode);

                AssetHandle<ScriptableObject> handle =
                    AssetManager.Instance.LoadAsset<ScriptableObject>(ProbeLocation);

                if (handle == null || handle.Asset == null)
                {
                    Debug.LogError(
                        "[资源冒烟] ❌ 加载 " + ProbeLocation + " 失败：句柄非 null 但资产是 null。\n" +
                        "最常见的原因是**类型不对**（例如用 GameObject 去取一个 ScriptableObject），\n" +
                        "Console 里应当另有一条 'Failed to load asset: …' 说明实际类型。");
                    return;
                }

                Debug.Log("[资源冒烟] ✅ 取到资产：" + ProbeLocation +
                          "（实际类型 = " + handle.Asset.GetType().FullName + "）");

                handle.Dispose();
                Debug.Log("[资源冒烟] 完成。若这一步是通过的，说明 **" + mode + " 这条路是通的**。");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "[资源冒烟] ❌ " + mode + " 失败：" + exception.Message + "\n" +
                    "两个常见原因：\n" +
                    "  ① **还没打包** —— 先用 `YooAsset → Bundle Builder` 打一次（见 Docs\\18 §七）\n" +
                    "  ② Offline 模式读的是 **StreamingAssets** —— 确认 Build 输出已经拷进去了\n" +
                    "    （Builder 的 Copy Buildin File Option 决定拷不拷、拷哪些）");
            }
        }
    }
}
