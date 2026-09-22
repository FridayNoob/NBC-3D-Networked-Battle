# NBC · 3D 联网战斗 Demo

> 一个用于应聘 **Unity 客户端游戏开发程序员** 的作品集项目。
> 自研框架底座 + 服务端权威同步 + 确定性帧同步 + Lua/AB 热更新 + 行为树 AI + 多算法寻路 + 数据驱动的 MVC 分层架构。

| 项 | 内容 |
| --- | --- |
| 项目代号 | **NBC**（Networked Battle Combat） |
| 引擎 | Unity **2022.3.62f3c1** LTS + **URP** |
| 服务端 | C# / **.NET 8** 独立控制台进程 + **MySQL 8.0** |
| 网络 | 自研 TCP（**Google.Protobuf** 序列化）+ 三种同步模式 |
| 热更 | **xLua** + **YooAsset**（底层 AssetBundle） |
| 平台 | PC（Windows） |
| 当前阶段 | 🚧 **M1 · 框架与基础设施（进行中）** —— M0 已收关（2026-09-20，18/18）；M1 已完成 **A1~A5、A7、A8** 与 **B2/B3**，见下方「M1 进度」 |

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
- **序列化选型用实测数据改过一次**：IL2CPP 出包证明 protobuf-net 在消息含集合字段时必抛异常（IL2CPP 未实现它依赖的 icall）；再用 **4 条路矩阵**把"自动发现 / 显式 TypeModel"两条路都验证否掉，随后换成 Google.Protobuf（`protoc` 生成纯 C#、零反射）—— **不靠偏好，靠三次出包的数据**
- **性能 HUD + 优化报告**：FPS / DC / SetPass / GC Alloc / 网络 / 回滚次数，含前后对比数据

---

## 一之二、M1 进度（框架与基础设施）

每一个模块都遵循同一条纪律：**先读原框架源码逐行核对缺陷 → 先复现再修 → 写可执行的验证 → 记录"现象/根因/改法/验证"四段式**。

| 任务 | 内容 | 修掉的原框架缺陷 | 验证 |
| --- | --- | --- | --- |
| **A1** | 单例三件套（`Singleton<T>` / `SingletonMono` / `SingletonAutoMono`） | FW-01 可被 `new` 出第二个实例、FW-02 `GetInstance` 返回 null、FW-03 缺并发保护 | EditMode 7 + PlayMode 8 |
| **A2** | 对象池（`ObjectPool<T>` / `GameObjectPool`） | FW-04 `List.RemoveAt(0)` 是 O(n)、FW-05 无上限无回收、P-04 `Clear` 名不副实、P-10 补货对象不设父、P-11 public 容器 | EditMode 20 + PlayMode 11（含 O(1) 对比实测：**快 21.1 倍**） |
| **A3** | 事件中心（`EventCenter` + 强类型 `EventId`） | FW-06 string key 拼错静默不触发、FW-07 同 key 异类型 NRE、P-16 未注册事件无反馈、P-18 派发中增删监听语义不确定 | EditMode 26 |
| **A4** | 公共 Mono 宿主（`MonoManager`） | P-13 宿主一帧未受保护窗口、P-05 订阅不退订 | PlayMode 18 |
| **A5** | 场景加载（`SceneLoader`） | FW-11 用 `yield return ao.progress`（一个 `float`）**当等待** —— 它根本不是等待指令 | EditMode 13 |
| **A7** | 输入（`InputManager` / `IInputSource`） | P-14 每帧对 4 个写死的键调 8 次 `GetKey` 并无条件发事件、事件名硬编码中文 | EditMode 28 |
| **A8** | 音频（`AudioManager`） | FW-09 音效对象不复用（每次 `AddComponent` 播完 `Destroy`）、宿主未 `DontDestroyOnLoad` → 切场景后 `MissingReferenceException`、`Update` 里 `isPlaying` 无防御 | EditMode 41 + PlayMode 6 |
| **B2** | 资源层（`AssetManager` + `YooAssetProvider`） | FW-08 基于 `Resources` 无法热更、且**没有任何释放入口** | EditMode 21（用假加载器确定性测试） |
| **A9** | UI 框架（UILayers / BasePanel / UIManager / 加载遮罩） | FW-10 层路径硬编码、P-08 Awake 脆弱、P-09 销毁与字典不同步、P-11 public 容器、P-12 s 不检查 | EditMode 66（含**反射式机械守卫**：BasePanel 不许有 Awake） |
| **A10** | 通用状态机（`StateMachine` / `HierarchicalStateMachine`） | 新写 FW-M14（原框架没有）：**纯 C# 零 UnityEngine 依赖**（帧同步前提）、帧驱动、分层约束 | EditMode 42（含 FSM-09 机械检查：不许出现 `MonoBehaviour` 或协程或 `Time.deltaTime`） |
| **D1** | 双端共享层（`NBC.Shared`） | 新写：**唯一源码在 Unity 侧**，服务端 `Compile Include` 引用它。⭐ 顺带消掉一个真隐患：服务端原本允许 C# 10 而 Unity 只到 C# 9 | EditMode 8（机械保证：asmdef 设置、不许有 `UnityEngine`、服务端目录不许存 .cs、`LangVersion` 必须 9.0） |
| **D2** | 确定性定点数学（`Fix64` / `FixVector3` / `FixMath`） | 新写 FW-M16：**Q32.32 定点 + CORDIC 三角**，位级别可复现。⭐ 附**数值对拍探针**（随机几百万组与 `double` 对拍，量出误差） | EditMode 65（含 4 条机械守卫：不许有 BCL 数学调用 / 浮点只许在转换边界 / 不许有 `UnityEngine`） |
| **E2** | 日志系统（`LogSystem`） | 新写 FW-M11：**分级 + 频道开关 + 文件落盘（滚动）+ 环形缓冲** | EditMode 36 |
| **B3** | YooAsset 初始化与运行模式 | 同上（运行期验证） | PlayMode 41（**编辑器模拟模式下真的加载到资源并断言了内容**） |
| **A11** | 分层方向的机械校验 | FW-12 框架与业务耦合 | EditMode 5（含**阳性对照**，防止断言假绿） |

**几条值得一提的工程细节**：

- **YooAsset 3.0.5 是重构过的 v3 API** —— v2.3 那套写法（`InitializeAsync` / `EditorSimulateModeParameters`）在 3.0.5 里被 `#if YOOASSET_LEGACY_API` 包着且**默认不编译**。这是读包源码 + 四条证据确认的，不是查教程得来的。
- **框架侧不认识 YooAsset**：`NBC.Framework` 的 `references` 为**空**（有一条测试机械保证），适配层独立成 `NBC.Framework.YooAsset` 程序集 —— 换资源方案只需删这一个程序集。
- **引用计数逻辑放在框架侧**，于是可以用假加载器在 EditMode 里**确定性地**测掉，不需要 YooAsset、不需要打包。
- **一条用实验定下来的测试划分**：EditMode 下连 `Awake` 都不会被调用（5 对照实验实测，连基线都是 0），所以 MonoBehaviour 生命周期一律放 PlayMode 测。
- **一套"把不可控环境变成可注入参数"的手法**（A5/A7/A8 反复用）：超时靠 `Tick(deltaTime)` 注入时间、输入靠注入假 `IInputSource`、音频靠注入 `IAudioPlaybackProbe`。好处是**连"探针被问了几次"都能断言**，而不只是断言最终结果。
- **编译闸门抓到的两类真问题**：① `UnityEngine.Input` **不在** `CoreModule`（在 `InputLegacyModule`）；② `AudioSource`/`AudioClip` **不在** `CoreModule`（在 `AudioModule`）。**规律：Unity 引擎按模块拆程序集，闸门是"显式列引用"，少一个就 `CS0246`。**

> 完整的"现象 → 根因 → 改法 → 验证"记录、以及对原框架 12 条缺陷的**逐行审计**（含 18 条清单之外的新发现），见 [`Docs/06-框架改造记录.md`](Docs/06-框架改造记录.md)。

---

## 二、目录结构

```
3D联网战斗Demo/
├─ Docs/                       # 文档（需求、工作规则、依赖清单、手册…）
├─ Protocol/                   # ★ .proto 协议定义的【唯一来源】（protoc 据此生成 C#）
├─ Client/                     # Unity 工程（Unity Hub 创建，URP）
│  └─ Assets/
│     ├─ _Project/             #   自研框架与业务代码
│     │  ├─ Framework/         #     框架底座（**零外部依赖**，可整包导出复用）
│     │  │  ├─ Core/           #       单例与注册表（A1）
│     │  │  ├─ Pool/           #       对象池与池统计（A2）
│     │  │  ├─ Event/          #       事件中心 + 强类型 EventId（A3）
│     │  │  ├─ Mono/           #       公共 Mono 宿主与定时器（A4）
│     │  │  ├─ Asset/          #       资源接缝 IAssetProvider / AssetHandle（B2）
│     │  │  ├─ Scenes/         #       场景加载（A5）
│     │  │  ├─ Input/          #       采集与命令分离（A7）
│     │  │  └─ Audio/          #       音频池 + 音量分组（A8）
│     │  ├─ Framework.YooAsset/#     ★ YooAsset 适配层（全工程唯一引用 YooAsset 的地方）
│     │  ├─ Framework.UI/      #     ★ UI 模块（全工程唯一引用 UGUI 的地方，A9）
│     │  ├─ Shared/            #     ★ 双端共享逻辑的唯一源码（服务端 Compile Include 引用它，D1）
│     │  ├─ Game/              #     业务逻辑（Model / View / Controller / Service）
│     │  ├─ Configs/           #     生成的 ScriptableObject 配置资产
│     │  ├─ Protocol/          #     protoc 生成的协议 C#（命名空间 NBC.Protocol，入库）
│     │  ├─ Editor/            #     编辑器工具（配置表工具等）
│     │  └─ Tests/             #     EditMode / PlayMode 测试
│     ├─ LuaScripts/           #   Lua 业务脚本（热更对象）
│     ├─ ThirdParty/           #   xLua / Google.Protobuf 等
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

> 📌 **改协议的正确姿势**：改 `Protocol/*.proto` → 用 `protoc` **重新生成** `Client/Assets/_Project/Protocol/*.cs` → 两个产物**一起提交**。
> 生成命令（含"不能给 protoc 传中文路径"那个坑）见 [`Docs/11-环境配置说明.md`](Docs/11-环境配置说明.md) 与 [`Docs/10-M0手动操作手册.md`](Docs/10-M0手动操作手册.md) 的 **D7**。

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
4. **放入序列化库**：把 **`Google.Protobuf.dll` + `System.Runtime.CompilerServices.Unsafe.dll`（共 2 个）** 放进 `Client/Assets/ThirdParty/GoogleProtobuf/`（见 M0 手册 **D7**）
5. **导入插件**：YooAsset（OpenUPM）→ xLua → **Behavior Designer 经典版 1.7.13** → **A\* Pathfinding Project Pro 5.4.7** → **DOTween 1.3.030**（后三者见 [§三之二](#三之二第三方插件如何导入本仓库不含付费插件)）
6. **编译服务端**：`cd Server && dotnet build`
7. **跑 AOT 验证**：按 M0 手册 **D17** 出 IL2CPP 包并运行探针

> 📘 **完整步骤请看 [`Docs/10-M0手动操作手册.md`](Docs/10-M0手动操作手册.md)** —— 含每步的菜单路径、按钮名、预期结果与失败排查。
>
> 📌 **协议代码已经入库**（`Client/Assets/_Project/Protocol/NbcProbe.cs`），clone 后**不需要装 protoc** 就能编译；
> 只有要改 `.proto` 时才需要，见上文的「改协议的正确姿势」。

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
| [`Docs/02-架构设计文档.md`](Docs/02-架构设计文档.md) | 架构与 **ADR**：分层与依赖方向、**ADR-001 `AssetManager` 接口定稿**（含被否决的备选方案） |
| [`Docs/05-依赖清单.md`](Docs/05-依赖清单.md) | 第三方依赖登记（版本 / 来源 / 许可 / 用途 / 是否例外）；含 **YooAsset 3.0.5 v3 API 实测对照表** |
| [`Docs/06-框架改造记录.md`](Docs/06-框架改造记录.md) | ★ **原框架逐行审计 + 每个模块的"现象→根因→改法→验证"**（12 条已登记缺陷 + 18 条审计新增发现） |
| [`Docs/08-数据库脚本.sql`](Docs/08-数据库脚本.sql) | 建库建表 + 测试数据 |
| [`Docs/10-M0手动操作手册.md`](Docs/10-M0手动操作手册.md) | M0 逐步操作手册 |
| [`Docs/11-环境配置说明.md`](Docs/11-环境配置说明.md) | 环境速查、protoc 生成、**不开 Unity 也能核对 Unity API 的编译闸门** |
| [`Docs/16-M1开工清单.md`](Docs/16-M1开工清单.md) | M1 任务拆分 / 验收 V1~V13 / **手动验证操作单** / 工具说明（Test Runner 是什么） |
| [`Docs/教学/README.md`](Docs/教学/README.md) | ★ **模块教学留档**：每个模块收关后讲一遍并留成一篇（`A7-输入管理.md`…），供随时复习 —— 见 `Docs/00` §15.3.1 |
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
