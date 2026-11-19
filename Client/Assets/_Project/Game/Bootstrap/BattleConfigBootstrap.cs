// 这个文件里有：
//   BattleConfigBootstrap（静态类）：进副本时把配置表来源接上并预加载，重复调用无害。

using System;
using NBC.Game.Config;

namespace NBC.Game.Bootstrap
{
    // 主要功能：一处负责「配置表从哪读、什么时候读」，避免每个消费者各自 SetSource。
    // 主要数据成员：无（全部状态由 ConfigMgr 单例持有）。
    // 主要外部方法：Ensure（设来源 + 空表时预加载全部）。
    // 谁在用它：HpBarDemoBehaviour（进副本装配点）、BattleBootSequence（第二步）。
    // 注意：本方法幂等 —— 已有来源不覆盖、已有表不再加载；失败只通过 onFailed 上报，不抛异常。
    public static class BattleConfigBootstrap
    {
        // 功能：确保配置表来源已设置，并在「一张表都没有」时预加载全部表。
        // 入参：onFailed 预加载失败时的原因回调，可空。
        // 返回：无（加载是异步的，结果稍后通过 ConfigMgr 的表缓存体现）。
        // 步骤：1) 取单例 2) 没来源就设成走资源层的 AssetConfigSource 3) 已有表就返回 4) 否则 GameTables.PreloadAll。
        // 上游：HpBarDemoBehaviour.Start、BattleBootSequence；下游：ConfigMgr.SetSource、GameTables.PreloadAll。
        public static void Ensure(Action<string> onFailed = null)
        {
            ConfigMgr mgr = ConfigMgr.Instance;

            if (mgr == null)
            {
                if (onFailed != null)
                {
                    onFailed("ConfigMgr 单例不可用");
                }

                return;
            }

            if (mgr.Source == null)
            {
                mgr.SetSource(new AssetConfigSource());
            }

            if (mgr.LoadedTableCount > 0)
            {
                return;
            }

            GameTables.PreloadAll(null, onFailed);
        }
    }
}
