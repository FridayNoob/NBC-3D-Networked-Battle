// ============================================================================
//  AssetBootstrapper —— 资源系统的**组合根**（composition root）
//  项目：3D联网战斗Demo   对应：ADR-001 决策 6、YOO-02/03、Docs\02 §1.2
//
//  ---------------------------------------------------------------------------
//  ⚠️ 为什么必须有这么一个程序集（这是 2026-09-22 被一次真报错逼出来的）
//  ---------------------------------------------------------------------------
//  现象：在 Editor 里点"Offline 模式冒烟"，报
//      [AssetManager] 还没有装上底层加载器。请先调用 SetProvider(...)
//
//  根因不是配置错，而是**架构上少了一层**：
//
//      NBC.Framework          ← 定义接缝 `IAssetProvider`，**不认识 YooAsset**（references: []）
//      NBC.Framework.YooAsset ← 实现接缝（`YooAssetProvider`），**唯一接触 YooAsset**
//      ???                    ← **谁把实现装到接缝上？** ← 之前没人是这个角色
//
//  `AssetManager` 是"框架侧的门面"，它**只能被外部装**（ADR-001 决策 6）。
//  而"装"这件事必须由一个**同时认识两边**的地方做 —— 那就是组合根。
//
//  ---------------------------------------------------------------------------
//  为什么不让 `NBC.Game` 直接当组合根
//  ---------------------------------------------------------------------------
//  业务层有一条机械检查（YOO-09）：
//      `rg -n "YooAsset" Client\Assets\_Project\Game` → **必须是 0 结果**
//  一旦业务层写 `new YooAssetProvider()`，这条检查就红了。
//  所以组合根**单独一个程序集**：字符串 "YooAsset" 只出现在
//  `_Project/Framework.YooAsset`（实现）与 `_Project/Boot`（装配）两处。
//
//  > 📌 这就是"**依赖倒置 + 组合根**"的完整形状：
//  > 接缝在框架、实现在适配层、**装配在一个谁都不依赖的顶端**。
//  > 少了最后一块，接缝就永远空着 —— 而且症状是运行时报错，不是编译错误。
// ============================================================================

using System.Threading.Tasks;
using NBC.Framework.Asset;
using NBC.Framework.Asset.Adapter;

namespace NBC.Boot
{
    /// <summary>资源系统的装配入口：**装加载器 + 初始化**。</summary>
    public static class AssetBootstrapper
    {
        /// <summary>
        /// 默认包名。
        /// <para>⚠️ 必须与 YooAsset 收集配置里的包名一致（实测当前是 `DefaultPackage`）。</para>
        /// </summary>
        public const string DefaultPackageName = "DefaultPackage";

        /// <summary>
        /// 装上 YooAsset 加载器并初始化。
        /// <para>引导代码里一行就够：<c>await AssetBootstrapper.InstallAsync(AssetRuntimeMode.Offline);</c></para>
        /// </summary>
        /// <param name="mode">运行模式（`EditorSimulate` / `Offline`；`Host` 属 M5）。</param>
        /// <param name="packageName">包名。</param>
        /// <param name="editorSimulatePackageRoot">
        /// 编辑器模拟清单目录。**只有 `EditorSimulate` 模式需要**；
        /// `Offline` 模式读的是内置（StreamingAssets）资源，传空即可。
        /// </param>
        /// <returns>初始化任务。</returns>
        public static async Task InstallAsync(AssetRuntimeMode mode, string packageName = DefaultPackageName,
                                              string editorSimulatePackageRoot = "")
        {
            // ⚠️ 顺序不能反：**先装加载器，再初始化**。
            //    反过来的症状就是那句"还没有装上底层加载器"。
            YooAssetProvider.Install(packageName, editorSimulatePackageRoot);

            await AssetManager.Instance.InitializeAsync(mode);
        }
    }
}
