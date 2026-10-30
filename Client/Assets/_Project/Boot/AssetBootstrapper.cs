// 这个文件里有：
//   AssetBootstrapper（静态类）：资源系统的组合根，把 YooAsset 加载器装到框架接缝上，再初始化资源层。
//
// 注意：AssetManager 只能由外部装加载器，缺了组合根不会编译报错，而是运行时抛「还没有装上底层加载器」。
// 注意：字符串 YooAsset 只许出现在 Framework.YooAsset 与 Boot 两处；写进 Game 会被机械检查判红。

using System.Threading.Tasks;
using NBC.Framework.Asset;
using NBC.Framework.Asset.Adapter;

namespace NBC.Boot
{
    // 主要功能：资源系统的组合根 —— 把具体加载器装到框架接缝上，再把资源层初始化起来。
    // 主要数据成员：DefaultPackageName（默认包名，须与 YooAsset 收集配置一致）。
    // 主要外部方法：InstallAsync（装加载器 + 初始化，一次做完）。
    // 谁在用它：Boot 的 HpBarDemoBehaviour / M2DemoBehaviour 与 Editor 的 AssetRuntimeBootstrapMenu，都在启动时调。
    /// <summary>资源系统的装配入口：装加载器并初始化，业务与编辑器启动都只走这一个口子。</summary>
    public static class AssetBootstrapper
    {
        /// <summary>默认包名，须与 YooAsset 收集配置里的包名一致，当前实测是 DefaultPackage。</summary>
        public const string DefaultPackageName = "DefaultPackage";

        /// <summary>装上 YooAsset 加载器并初始化资源层，顺序固定为先装后初始化。</summary>
        /// <param name="mode">运行模式：EditorSimulate 读编辑器模拟清单，Offline 读随包内容，Host 尚未实现。</param>
        /// <param name="packageName">包名，须与 YooAsset 收集配置一致，默认 DefaultPackageName。</param>
        /// <param name="editorSimulatePackageRoot">编辑器模拟清单目录，只有 EditorSimulate 需要；Offline 读随包内容，传空即可。</param>
        /// <returns>初始化任务，await 完成即资源层可加载；失败时异常向调用方抛，本方法不吞。</returns>
        // 功能：把 YooAsset 加载器装到 AssetManager 上，再等资源层初始化完成。
        // 入参：mode 运行模式；packageName 包名；editorSimulatePackageRoot 模拟清单目录（仅 EditorSimulate 用，Offline 传空）
        // 返回：无（async Task，await 之后资源层就绪；失败靠异常上抛）
        // 步骤：
        //   1) 先装加载器：AssetManager 只认接口，没人装它就一直空着
        //   2) 再初始化：建包与加载清单都要经由已装好的加载器，反了必抛未装加载器
        // 上游：Boot 的 HpBarDemoBehaviour / M2DemoBehaviour、Editor 的 AssetRuntimeBootstrapMenu
        // 下游：YooAssetProvider.Install、AssetManager.InitializeAsync
        // 注意：两步不能反；反过来的症状就是运行时那句「还没有装上底层加载器」，编译期不报。
        public static async Task InstallAsync(AssetRuntimeMode mode, string packageName = DefaultPackageName,
                                              string editorSimulatePackageRoot = "")
        {
            YooAssetProvider.Install(packageName, editorSimulatePackageRoot);

            await AssetManager.Instance.InitializeAsync(mode);
        }
    }
}
