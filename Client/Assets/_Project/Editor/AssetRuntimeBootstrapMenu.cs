// ============================================================================
//  资源运行模式冒烟菜单 —— 在 Editor 里验证 EditorSimulate / Offline 两种模式
//  项目：3D联网战斗Demo   对应：YOO-03（运行模式）
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么需要这个菜单（它补的是一个真实缺口）
//  ---------------------------------------------------------------------------
//  YooAsset **3.0.5 的"运行模式"不是一个窗口开关，而是初始化时的代码参数**：
//
//      AssetManager.Instance.InitializeAsync(AssetRuntimeMode.EditorSimulate);
//      AssetManager.Instance.InitializeAsync(AssetRuntimeMode.Offline);
//      AssetManager.Instance.InitializeAsync(AssetRuntimeMode.Host);   // M5 才实现
//
//  而 2026-09-22 实测：**游戏里没有任何地方调用它**（只有 EditMode/PlayMode 测试在调）。
//  也就是说"想切到 Offline 跑一次"当时**没有入口** —— 这个菜单就是那个入口。
//
//  ---------------------------------------------------------------------------
//  怎么用（两步）
//  ---------------------------------------------------------------------------
//      ① 进**播放模式**（本菜单只在播放模式下有效，因为要初始化运行时资源系统）
//      ② 菜单 Tools/NBC/资源/… → 选一种模式
//
//  它会打印：用的哪个模式、包名、以及**试着加载一个真实地址的结果** ——
//  于是"Offline 到底通没通"当场就有答案，不用去翻代码。
//
//  ⚠️ 地址来自 `Docs\17` 的配置表产物：生成物里 HeroConfig 的地址是 `HeroConfig`
//     （`AddressByFileName` 规则）；若你的收集配置改了地址规则，把下面的常量一起改。
// ============================================================================

using System;
using System.Threading.Tasks;
using NBC.Boot;
using NBC.Framework.Asset;
using UnityEditor;
using UnityEngine;

namespace NBC.EditorTools
{
    /// <summary>资源运行模式的 Editor 冒烟入口。</summary>
    public static class AssetRuntimeBootstrapMenu
    {
        /// <summary>冒烟用的资源地址（配置表产物；见 ConfigKit 生成的 HeroConfig.asset）。</summary>
        private const string ProbeLocation = "HeroConfig";

        /// <summary>YooAsset 的包名（必须与收集配置里的一致；实测当前为 `DefaultPackage`）。</summary>
        private const string PackageName = AssetBootstrapper.DefaultPackageName;

        /// <summary>
        /// 编辑器模拟模式要用的模拟清单目录。
        /// <para>⚠️ 只有 `EditorSimulate` 模式需要它；`Offline` 模式传空即可（读的是 StreamingAssets）。
        /// 若你要跑 `EditorSimulate` 冒烟，把这里改成模拟构建的输出目录（B3 操作单里有说明）。</para>
        /// </summary>
        private const string SimulatePackageRoot = "";

        /// <summary>用编辑器模拟模式初始化（日常开发：免打包，改资源即时生效）。</summary>
        [MenuItem("Tools/NBC/资源/① EditorSimulate 模式（日常开发）")]
        private static void InitEditorSimulate()
        {
            Run(AssetRuntimeMode.EditorSimulate);
        }

        /// <summary>用内置（Offline）模式初始化：**读 StreamingAssets 里的内容包**。</summary>
        [MenuItem("Tools/NBC/资源/② Offline 模式（读内置包）")]
        private static void InitOffline()
        {
            Run(AssetRuntimeMode.Offline);
        }

        /// <summary>
        /// 初始化 + 试加载一个地址。
        /// <para>⚠️ 必须在播放模式下点：资源系统是运行时的东西，编辑模式里初始化没有意义
        /// （而且 `Offline` 模式依赖 StreamingAssets，只有真正跑起来才成立）。</para>
        /// </summary>
        /// <param name="mode">模式。</param>
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

        /// <summary>异步跑一遍并打日志（Editor 菜单不能 await，所以用 fire-and-forget + 日志）。</summary>
        /// <param name="mode">模式。</param>
        private static async void ProbeAsync(AssetRuntimeMode mode)
        {
            try
            {
                Debug.Log("[资源冒烟] 开始装配 + 初始化：" + mode);

                // ⚠️ 这一步**必须走组合根**（`NBC.Boot`）：
                //    `AssetManager` 只是门面，它需要有人先把 `IAssetProvider` 装上去。
                //    我第一版直接调 `AssetManager.InitializeAsync` —— 于是报
                //    "还没有装上底层加载器"，而且那个报错**看起来像配置问题，其实是少了一层架构**。
                await AssetBootstrapper.InstallAsync(mode, PackageName, SimulatePackageRoot);

                Debug.Log("[资源冒烟] ✅ 装配 + 初始化成功：" + mode);

                // 再走一步真加载：光"初始化成功"证明不了资源真的能取到
                AssetHandle<GameObject> handle = AssetManager.Instance.LoadAsset<GameObject>(ProbeLocation);

                if (handle == null)
                {
                    Debug.LogWarning("[资源冒烟] 加载 " + ProbeLocation + " 返回了 null 句柄。");
                    return;
                }

                Debug.Log("[资源冒烟] ✅ 取到句柄：" + ProbeLocation +
                          "（Asset = " + (handle.Asset == null ? "null" : handle.Asset.name) + "）");

                handle.Dispose();
                Debug.Log("[资源冒烟] 完成。若这一步是通过的，说明 **" + mode + " 这条路是通的**。");
            }
            catch (Exception exception)
            {
                // ⚠️ Offline 模式最常见的失败原因是"还没打过包/没拷进 StreamingAssets"，
                //    所以把该做什么直接写进日志，而不是只丢一个异常堆栈。
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
