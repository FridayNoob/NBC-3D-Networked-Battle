// 这个文件里有：
//   GameScene（枚举）：正式流程里的场景编号。
//   GameSceneAddress（静态类）：枚举 → YooAsset 地址 / Unity 场景名。
//
// 地址规则必须与 YooAsset 收集器一致：collector = AddressByFileName、SupportExtensionless = 1
// ⇒ 地址就是"文件名去掉 .unity"，例如 Assets/_Project/Scenes/World.unity 的地址是 World。

using System;
using System.Collections.Generic;

namespace NBC.Game.GameFlow
{
    // 正式流程里的场景（Test 只给开发用）
    public enum GameScene
    {
        // 登录 / 注册
        Login = 0,

        // 主世界（登录后默认进这里）
        World = 1,

        // PVP 决斗场景
        PvpRoom = 2,

        // 多人联机副本
        Battle = 3,

        // 开发测试场景
        Test = 4,
    }

    // 主要功能：把 GameScene 换成"YooAsset 地址"与"Unity 场景名"。
    // 主要数据成员：Addresses（枚举 → 文件名）。
    // 主要外部方法：AddressOf / NameOf。
    // 谁在用它：SceneFlow（加载）、SceneFlowDebugKeys（调试快捷键）。
    // 注意：改名场景必须同步改这里，否则加载会报"地址不存在"。
    public static class GameSceneAddress
    {
        // 枚举 → 场景文件名（不含扩展名），也是 YooAsset 地址
        private static readonly Dictionary<GameScene, string> Addresses = new Dictionary<GameScene, string>
        {
            { GameScene.Login, "Login" },
            { GameScene.World, "World" },
            { GameScene.PvpRoom, "PVP_Room" },
            { GameScene.Battle, "Battle" },
            { GameScene.Test, "Test" },
        };

        // 功能：取 YooAsset 地址。
        // 入参：scene 场景编号
        // 返回：地址字符串
        // 步骤：查表；查不到直接抛（配置错要立刻暴露，不能静默加载错场景）
        // 上游：SceneFlow
        // 下游：无
        public static string AddressOf(GameScene scene)
        {
            if (Addresses.TryGetValue(scene, out string address))
            {
                return address;
            }

            throw new ArgumentOutOfRangeException(nameof(scene), scene, "[GameSceneAddress] 没有登记这个场景");
        }

        // 功能：取 Unity 场景名（SceneManager 兜底用）。
        // 入参：scene 场景编号
        // 返回：场景名（与地址相同，因为地址就是文件名）
        // 步骤：复用 AddressOf
        // 上游：SceneFlow
        // 下游：SceneManager.LoadSceneAsync
        public static string NameOf(GameScene scene)
        {
            return AddressOf(scene);
        }
    }
}
