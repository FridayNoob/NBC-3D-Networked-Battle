// 这个文件里有：
//   IConfigSource（接口）：配置资产的来源接缝 —— 从哪读一张表

using System;
using UnityEngine;

namespace NBC.Game.Config
{
    // 主要功能：给一个表名，异步还一个「表名 + Config」的资产，把「怎么读」与「怎么管缓存」分开。
    // 主要数据成员：无。
    // 主要外部方法：Description（来源描述）、LoadTable（加载一张表）。
    // 谁在用它：ConfigMgr 加载配置表；EditMode 测试塞假来源，不需要任何资产。
    // 注意：用回调而不是 async/await —— await 的续体要回 UnitySynchronizationContext，
    //       而 EditMode 没有帧循环，测试会卡死。
    // 注意：只给表名、不给 Type —— 资产地址是字符串；塞 Type 会把资产系统和反射绑在一起。
    // 注意：成功与失败都必须回调，且只在主线程回调一次；两个都不回会让调用方永远等下去。
    public interface IConfigSource
    {
        // 功能：告诉调用方这个来源是什么（写日志与报错时用）。
        // 入参：无
        // 返回：来源描述（报错与日志用，如 YooAsset / 假来源（测试））
        string Description { get; }

        // 功能：按表名把一张配置资产读进来，成功或失败都通过回调告知。
        // 入参：tableName 表名（如 Hero）；onLoaded 成功回调（交出资产）；onFailed 失败回调（交出一句人话原因）
        // 返回：无
        // 上游：ConfigMgr 的加载流程
        // 下游：具体实现（YooAsset / 编辑器直读 / 测试假来源）
        void LoadTable(string tableName, Action<ScriptableObject> onLoaded, Action<string> onFailed);
    }
}
