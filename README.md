# NBC · 3D 联网战斗 Demo

> 一个用于应聘 **Unity 客户端游戏开发程序员** 的作品集项目。
> 自研框架底座 + 服务端权威同步 + 确定性帧同步 + Lua/AB 热更新 + 行为树 AI + 多算法寻路 + 数据驱动的 MVC 分层架构。

| 项 | 内容 |
| --- | --- |
| 项目代号 | **NBC**（Networked Battle Combat） |
| 引擎 | Unity **2022.3.62f3c1** LTS + **URP** |
| 服务端 | C# / **.NET 8** 独立控制台进程 + **MySQL 8.0** |
| 网络 | 自研 TCP（protobuf-net 序列化）+ 三种同步模式 |
| 热更 | **xLua** + **YooAsset**（底层 AssetBundle） |
| 平台 | PC（Windows） |
| 当前阶段 | **M0 · 项目奠基（进行中）** |

---

## 一、这个项目要证明什么

| # | 能力 | 落地方式 |
| --- | --- | --- |
| G1 | **网络同步** | 状态同步 / 帧同步 / 状态帧同步三种模型都有真实可跑的实现，含客户端预测与回滚 |
| G2 | **架构能力** | MVC 分层清晰、模块可插拔复用、设计模式用得恰当而非堆砌 |
| G3 | **工程能力** | 配置表工具、资源打包、性能看板，不是"手搓一堆脚本" |
| G4 | **性能意识** | 有 DrawCall / SetPass / GC Alloc 的可量化优化过程与前后对比数据 |
| G5 | **能落地** | 有真正能玩、能打包、能演示的完整闭环 |

### 技术亮点速览

- **三种同步模式共用一套传输层**（策略模式）：对比演示状态同步 / 帧同步 / 状态帧同步的带宽与手感差异
- **帧同步确定性**：自研定点数学 `Fix64` + 纯 C# 逻辑层（`noEngineReferences`）+ 状态哈希校验 + 战斗回放
- **客户端预测与回滚**：本地预测 → 服务端和解 → 误差平滑修正；内置网络模拟器（可注入延迟/丢包）
- **AI 分两套实现**（接口隔离）：插件行为树（客户端）+ 自研确定性决策（帧同步）；因为 Behavior Designer 依赖 Unity 运行时，无法在纯 .NET 服务端运行
- **四种寻路**：A\* Pathfinding Project Pro / Unity NavMesh / 自研流场+群体避障 / 自研定点 A\*（帧同步兜底）
- **热更分层**：YooAsset 负责资源分发，业务侧热更管理自研（需求要求"这部分不用框架"）
- **性能 HUD + 优化报告**：FPS / DC / SetPass / GC Alloc / 网络 / 回滚次数，含前后对比数据

---

## 二、目录结构

```
3D联网战斗Demo/
├─ Docs/                       # 文档（需求、工作规则、依赖清单、手册…）
├─ Client/                     # Unity 工程（Unity Hub 创建，URP）
│  └─ Assets/
│     ├─ _Project/             #   自研框架与业务代码
│     │  ├─ Framework/         #     框架底座（可整包导出复用）
│     │  ├─ Game/              #     业务逻辑（Model / View / Controller / Service）
│     │  ├─ Configs/           #     生成的 ScriptableObject 配置资产
│     │  ├─ Editor/            #     编辑器工具（配置表工具等）
│     │  └─ Tests/             #     EditMode / PlayMode 测试
│     ├─ LuaScripts/           #   Lua 业务脚本（热更对象）
│     ├─ ThirdParty/           #   xLua / protobuf-net 等
│     ├─ Art/                  #   美术资源
│     └─ Scenes/               #   场景
├─ ClientStaging/              # 待放入 Unity 工程的客户端文件（暂存区，见该目录 README）
├─ Server/                     # .NET 8 服务端解决方案
│  ├─ NBC.sln
│  ├─ NBC.Shared/              #   ★ 双端共享：协议 + 定点数学 + 战斗核心逻辑
│  ├─ NBC.Server.Core/         #   网络层、会话、房间、帧循环
│  ├─ NBC.Server.Game/         #   服务端战斗逻辑
│  ├─ NBC.Server.Host/         #   进程宿主（控制台）
│  └─ NBC.Server.Tests/        #   xUnit 单测
├─ Tools/                      # 独立工具（配置表导出、资源打包、热更发布）
├─ Builds/                     # 打包产物（不入库）
└─ DeepSeekOutput/             # 开发过程知识点总结（见规则 1）
```

---

## 三、快速开始

### 前置环境

| 依赖 | 版本 | 说明 |
| --- | --- | --- |
| Unity | 2022.3.62f3c1 | 必须勾选 **Windows Build Support (IL2CPP)** 模块 |
| .NET SDK | 8.0 | 服务端 |
| MySQL | 8.0 | 本机服务 |
| Git | 任意较新版本 | — |
| Visual Studio 2022 | 含「使用 C++ 的桌面开发」工作负载 | IL2CPP 出包需要 |

### 步骤

1. **建库**：执行 `Docs/08-数据库脚本.sql`（命令行可用 `source`，注意路径用正斜杠）
2. **创建 Unity 工程**：Unity Hub → New project → **3D (URP)** → 名称 `Client`，位置为本仓库根目录
3. **把 `ClientStaging/` 里的文件搬到 Unity 工程**（见 `ClientStaging/README.md`，含精确路径）
4. **导入插件**：YooAsset（OpenUPM）→ xLua → protobuf-net → **Behavior Designer 经典版 1.7.13** → **A\* Pathfinding Project Pro 5.4.7** → **DOTween 1.3.030**（后三者见 [§三之二](#三之二第三方插件如何导入本仓库不含付费插件)）
5. **编译服务端**：`cd Server && dotnet build`
6. **跑 AOT 验证**：按 `ClientStaging/README.md` 的 D17 流程出 IL2CPP 包并运行探针

> 📘 **完整步骤请看 [`Docs/10-M0手动操作手册.md`](Docs/10-M0手动操作手册.md)** —— 含每步的菜单路径、按钮名、预期结果与失败排查。

---

## 三之二、第三方插件如何导入（**本仓库不含付费插件**）

本仓库是公开作品集。**Behavior Designer** 与 **A\* Pathfinding Project Pro** 均为
Asset Store **Commercial License（按席位授权）**，**许可不允许再分发**，
因此这两个插件的文件被 `.gitignore` 排除，clone 后需要自行导入。

| 插件 | 版本 | 装到哪（实测） | 怎么获取 |
| --- | --- | --- | --- |
| **Behavior Designer（经典版）** | 1.7.13 | `Client/Assets/Behavior Designer/` + `Client/Assets/Gizmos/Behavior Designer*` | 自行购买（Asset Store） |
| **A\* Pathfinding Project Pro** | 5.4.7 | **`Client/Packages/com.arongranberg.astar/`**（5.x 起为 UPM 嵌入式包） | 自行购买（[arongranberg.com](https://arongranberg.com/astar/) 或 Asset Store） |
| **DOTween** | 1.3.030（免费版） | `Client/Assets/Plugins/Demigiant/` | [免费获取](http://dotween.demigiant.com) |

**导入方式**：Unity 菜单 **Assets → Import Package → Custom Package...** → 选中 `.unitypackage` → Import。

### ⚠️ 导入 A\* Pathfinding Project Pro 后必须做的三件事

1. **确认 `Client/Packages/manifest.json` 里出现了这 3 个依赖包**（它们由 A\* 的 `package.json` 声明）：
   `com.unity.burst`（1.8.7）· `com.unity.collections`（1.5.1）· `com.unity.mathematics`（1.2.6）
   —— 需要 Unity 能访问包注册表或已有本地缓存。
   **`com.arongranberg.astar` 本身不应写进 `manifest.json`**（嵌入式包由 Unity 自动发现）。
2. **Burst 需要 C++ 编译器**（IL2CPP 出包时用），并会**明显增加打包时间**。
3. **官方示例场景带 `~` 后缀**（`Packages/com.arongranberg.astar/ExampleScenes~/`），Unity 不会导入。
   要用示例时，把它**改名为 `ExampleScenes`**。

> 更细的验收判据、失败排查与"导入前后 `manifest.json` / `packages-lock.json` 该有什么变化"，
> 见 [`Docs/10-M0手动操作手册.md`](Docs/10-M0手动操作手册.md) 的 **D8 / D9 / D10**。

### clone 后会看到的几个"孤儿"文件（**正常，不用管**）

因为插件本体被忽略，仓库里仍保留了 6 个**属于我们自己**的小文件（合计约 2.4 KB，不含任何插件源码）：

| 文件 | 是什么 | 为什么保留 |
| --- | --- | --- |
| `Client/Assets/Behavior Designer.meta`、`Client/Assets/Gizmos.meta`、`Client/Assets/Plugins/Demigiant.meta` | Unity 的**文件夹 GUID 存根**（90~172 字节） | 保证目录 GUID 稳定，导入插件后立刻重新生效 |
| `Client/Assets/Resources/DOTweenSettings.asset`(+`.meta`) | **我们**的 DOTween 项目设置（1.3 KB） | 是配置，不是插件内容；丢了会在 clone 后回到默认值 |
| `Client/ProjectSettings/com.arongranberg.astar/settings.asset` | **我们**的 A\* 项目级设置（410 字节） | 同上；以后 Grid Graph 的设置也可能存在这里 |

导入对应插件之前，它们指向的目标暂时不存在 —— Unity 会提示"资源缺失"，**这是预期的**，导入后自动恢复。

---

## 四、文档索引

| 文档 | 内容 |
| --- | --- |
| [`Docs/00-项目工作规则.md`](Docs/00-项目工作规则.md) | 本项目的工作规则（含第三方依赖例外清单 E1~E5、工具边界） |
| [`Docs/01-项目需求文档.md`](Docs/01-项目需求文档.md) | **需求基准（Single Source of Truth）**：13 条需求收敛、优先级、验收标准、里程碑、风险 |
| [`Docs/05-依赖清单.md`](Docs/05-依赖清单.md) | 第三方依赖登记（版本 / 来源 / 许可 / 用途 / 是否例外） |
| [`Docs/08-数据库脚本.sql`](Docs/08-数据库脚本.sql) | 建库建表 + 测试数据 |
| [`Docs/10-M0手动操作手册.md`](Docs/10-M0手动操作手册.md) | M0 逐步操作手册 |
| [`ClientStaging/README.md`](ClientStaging/README.md) | 客户端暂存文件的搬运说明 + D17 验证流程 |
| `DeepSeekOutput/` | 开发过程知识点总结（含踩坑记录与新概念复习） |

---

## 五、里程碑

| 里程碑 | 周期 | 主题 | 关键产出 |
| --- | --- | --- | --- |
| **M0** | W1 | 项目奠基 | 工程骨架、依赖冻结、**IL2CPP AOT 验证**、**插件架构边界验证** |
| M1 | W2~W3 | 框架与基础设施 | 框架底座 + YooAsset 接入 + 配置表工具 + 性能 HUD 雏形 |
| M2 | W4~W6 | 单机可玩战斗 | **单机 ARPG 战斗可玩** + 三种敌人 AI + 四种寻路对比 |
| M3 | W7~W9 | 服务端 + 状态同步 | **2~4 人联网状态同步可玩** + MySQL 落库 |
| M4 | W10~W12 | 帧同步与预测 | **帧同步可玩 + 确定性校验 + 回放**（最高风险阶段） |
| M5 | W13~W14 | 热更新与性能 | **Lua 热重载 + 资源增量热更** + 性能优化报告 |
| M6 | W15~W16 | 收尾与演示 | 打包、文档、演示视频、面试问答准备 |

**排期红线**：M0 的 IL2CPP 验证（D17）未通过则不进入 M1 —— 这是唯一不能跳过的关卡。

---

## 六、说明

- 本项目为个人技术作品集，**非商业项目**。
- 第三方插件的许可信息见 `Docs/05-依赖清单.md`；引用第三方代码均注明来源与版本。
- 数据库密码不入库：`appsettings.json` 是模板，实际密码用环境变量 `NBC_DB_PASSWORD` 覆盖。
