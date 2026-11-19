// 这个文件里有：
//   AssetPackageRoot（静态类）：找「模拟清单（EditorSimulateBuildPipeline）」最新的输出目录。

using System;
using System.IO;
using UnityEngine;

namespace NBC.Boot
{
    // 主要功能：在 <工程根>/Bundles/<平台>/<包名>/ 下找清单文件最新的那个目录，交给资源层安装。
    // 主要数据成员：无。
    // 主要外部方法：Resolve（返回目录全路径，找不到返回空串）。
    // 谁在用它：AppBootstrap（Login 启动装资源层）、HpBarDemoBehaviour（直接进 Battle 调试时兜底）。
    // 注意：只认目录里有 DefaultPackage*.bytes 的子目录（把 OutputCache 之类排除），
    //       这样「重新构建内容包」之后不用改代码，运行时自动取最新那份清单。
    public static class AssetPackageRoot
    {
        // 功能：找出最新的模拟清单目录。
        // 入参：无（平台与包名用 AssetBootstrapper 的默认值）。
        // 返回：目录全路径；找不到返回空串（让底层自己报错，别在这里假装成功）。
        // 步骤：
        //   1) 拼出 <工程根>/Bundles/<平台>/<包名>/，不存在直接返回空串
        //   2) 遍历子目录，只看里面有 DefaultPackage*.bytes 的
        //   3) 比清单文件的最后写入时间，取最新的那个
        // 上游：AppBootstrap.Start、HpBarDemoBehaviour.ResolveSimulatePackageRoot。
        // 下游：AssetBootstrapper.InstallAsync。
        public static string Resolve()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string packageFolder = Path.Combine(
                projectRoot,
                "Bundles",
                "StandaloneWindows64",
                AssetBootstrapper.DefaultPackageName);

            if (!Directory.Exists(packageFolder))
            {
                return string.Empty;
            }

            string newest = string.Empty;
            DateTime newestTime = DateTime.MinValue;
            string[] subDirectories = Directory.GetDirectories(packageFolder);

            for (int i = 0; i < subDirectories.Length; i++)
            {
                string[] manifests = Directory.GetFiles(
                    subDirectories[i],
                    AssetBootstrapper.DefaultPackageName + "*.bytes");

                if (manifests.Length == 0)
                {
                    continue;
                }

                DateTime time = File.GetLastWriteTime(manifests[0]);

                if (time > newestTime)
                {
                    newestTime = time;
                    newest = subDirectories[i];
                }
            }

            return newest;
        }
    }
}
