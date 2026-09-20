# ClientStaging —— 待放入 Unity 工程的客户端文件（暂存区）

> **为什么有这个目录**：按项目规则 13，AI 不能创建/操作 Unity 编辑器。
> 而 Unity 工程（`Client/`）必须由你用 Unity Hub 手动创建（M0 手册 D1）。
> 在 D1 完成之前，AI 无法把文件写进 `Client/Assets/` —— 所以先暂存在这里，
> 等工程创建好之后由你（或我，届时路径已存在）搬到目标位置。

---

## 一、文件清单与目标位置

### 1.1 验证脚本与配置

| 暂存文件 | 搬到（相对 `3D联网战斗Demo/`） | 作用 |
| --- | --- | --- |
| `NBV0_EnvironmentProbe.cs` | `Client/Assets/_Project/Tests/Manual/NBV0_EnvironmentProbe.cs` | **D17 的验证脚本**：在 IL2CPP 出包后验证 protobuf-net 与 xLua 是否可用 |
| `Editor/NBV0_DefineInitializer.cs` | `Client/Assets/_Project/Editor/NBV0_DefineInitializer.cs` | 自动检测 dll 并注入编译符号 `NBC_HAS_PROTOBUF_NET` / `NBC_HAS_XLUA`；另带诊断菜单 |
| `link.xml` | `Client/Assets/link.xml` | **IL2CPP 裁剪保护**：防止 protobuf-net / xLua 被 strip 掉 |

### 1.2 程序集定义（asmdef）

| 暂存文件 | 搬到 | 程序集名 | 关键设置 |
| --- | --- | --- | --- |
| `AsmDefs/Framework.asmdef` | `Client/Assets/_Project/Framework/Framework.asmdef` | `NBC.Framework` | 框架底座；允许引用引擎 |
| `AsmDefs/Model.asmdef` | `Client/Assets/_Project/Game/Model/Model.asmdef` | `NBC.Model` | **`noEngineReferences: true`** ← 见 §1.3 |
| `AsmDefs/Game.asmdef` | `Client/Assets/_Project/Game/Game.asmdef` | `NBC.Game` | View / Controller / Service |
| `AsmDefs/Editor.asmdef` | `Client/Assets/_Project/Editor/Editor.asmdef` | `NBC.Editor` | 仅编辑器平台 |
| `AsmDefs/Tests_EditMode.asmdef` | `Client/Assets/_Project/Tests/EditMode/Tests_EditMode.asmdef` | `NBC.Tests.EditMode` | 测试程序集（Editor 平台） |
| `AsmDefs/Tests_PlayMode.asmdef` | `Client/Assets/_Project/Tests/PlayMode/Tests_PlayMode.asmdef` | `NBC.Tests.PlayMode` | 测试程序集（全平台） |

### 1.3 ⚠️ 为什么 `NBC.Model` 要设 `noEngineReferences: true`

这是本项目**帧同步能否成立的第一道机械防线**。

需求文档 §4.2 R1 与 §6.4 DET-02 要求：**逻辑层禁止引用 `UnityEngine`**。原因链：

```
帧同步要求每个客户端算出完全相同的结果
   ↑
浮点运算不保证跨端一致（DET-01）
   ↑
UnityEngine 的 Vector3 / Mathf 内部是 float
   ↑
所以逻辑层不能碰 UnityEngine —— 一碰就可能引入 float
```

**`noEngineReferences: true` 让 Unity 在编译期就拦住这件事**：
只要 `_Project/Game/Model/` 下的任何脚本出现 `using UnityEngine;`，编译直接报错，
而不是等到 M4 阶段发现"双端结果不一致"再回头排查（那会非常难查）。

> 对应的机械校验（需求文档 §14.3 MOD-02）在 M1 用单元测试补上，形成"编辑器 + 测试"双重保险。

**代价与应对**：Model 层因此不能用 `Debug.Log`、`Vector3`、`ScriptableObject`。应对方式：

| 原本想用 | 改用 |
| --- | --- |
| `Debug.Log` | 自研 `Log`（Framework 层提供接口，Model 只依赖接口） |
| `Vector3` / `Mathf` | 自研 `FixVector3` / `FixMath`（定点，M1 交付） |
| `ScriptableObject` 配置类 | 放在 `NBC.Game` 层作为**数据容器**，读取后转成 Model 的纯数据对象 |

### 1.4 搬运方式（二选一）

**方式 A：资源管理器手动复制**

按上面两张表的路径逐个复制。目录不存在就先新建。

**方式 B：命令行一键复制（推荐，顺带建好目录）**

```powershell
$root = "E:\U3D Projects\0_MyFile\3D联网战斗Demo"
$A    = "$root\Client\Assets"

# 建目录（若已存在不报错）
@(
  "$A\_Project\Framework",
  "$A\_Project\Game\Model",
  "$A\_Project\Editor",
  "$A\_Project\Tests\Manual",
  "$A\_Project\Tests\EditMode",
  "$A\_Project\Tests\PlayMode",
  "$A\_Project\Configs",
  "$A\_Project\Art",
  "$A\LuaScripts",
  "$A\Scenes"
) | ForEach-Object { New-Item -ItemType Directory -Force -Path $_ | Out-Null }

# 验证脚本与配置
Copy-Item "$root\ClientStaging\NBV0_EnvironmentProbe.cs"        "$A\_Project\Tests\Manual\"
Copy-Item "$root\ClientStaging\Editor\NBV0_DefineInitializer.cs" "$A\_Project\Editor\"
Copy-Item "$root\ClientStaging\link.xml"                          "$A\link.xml"

# 程序集定义
Copy-Item "$root\ClientStaging\AsmDefs\Framework.asmdef"      "$A\_Project\Framework\Framework.asmdef"
Copy-Item "$root\ClientStaging\AsmDefs\Model.asmdef"          "$A\_Project\Game\Model\Model.asmdef"
Copy-Item "$root\ClientStaging\AsmDefs\Game.asmdef"           "$A\_Project\Game\Game.asmdef"
Copy-Item "$root\ClientStaging\AsmDefs\Editor.asmdef"         "$A\_Project\Editor\Editor.asmdef"
Copy-Item "$root\ClientStaging\AsmDefs\Tests_EditMode.asmdef" "$A\_Project\Tests\EditMode\Tests_EditMode.asmdef"
Copy-Item "$root\ClientStaging\AsmDefs\Tests_PlayMode.asmdef" "$A\_Project\Tests\PlayMode\Tests_PlayMode.asmdef"

Write-Host "搬运完成。回到 Unity 等待编译。"
```

> ⚠️ **搬完检查 Unity Console**：应当**没有红色报错**。
> 如果报 `NBC.Model` 找不到 `NBC.Framework` 之类的引用错误，见下方 §1.5。

### 1.5 已知的 asmdef 引用问题与处理

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 提示找不到 `NBC.Framework` | asmdef 的 `references` 是**按名字引用**，而 `NBC.Framework` 程序集此刻还没建（本目录的 Framework.asmdef 为空壳，M1 才填内容） | **正常现象**。若 Unity 报错影响使用，可先把 Model/Editor/Tests 的 `references` 里的 `NBC.Framework` 临时删掉，M1 再加回 |
| 提示 `_Project/Tests/Manual/` 下的脚本报 NUnit 错误 | 该目录名含 `Tests`，Unity 可能按测试程序集规则处理 | 本目录已把 `Tests/Manual` 排除在测试程序集之外（它归属 `NBC.Editor`）。若仍报错，把该脚本挪到 `_Project/Scripts_Manual/` 下即可 |
| asmdef 的 `references` 显示为一串数字而不是名字 | Unity Inspector 勾了 **Use GUIDs** | 不影响功能。本项目统一用**名称引用**，便于阅读与手写 |
| 修改 asmdef 后编译没反应 | Unity 有时不重新触发编译 | 菜单 **Assets → Refresh**（`Ctrl+R`） |

---

## 二、D17 的操作流程（配合 M0 手册使用）

### 步骤 1：建验证场景

1. Unity 菜单 **File → New Scene** → 选 **Basic (URP)** 模板 → 保存为
   `Client/Assets/Scenes/Scene_Test_AOT.unity`
2. Hierarchy 里右键 **Create Empty**，命名 `EnvProbe`
3. 选中 `EnvProbe` → Inspector → **Add Component** → 搜 `NBV0_EnvironmentProbe` 添加
4. 确认两个勾选框都是 ✅（`testProtobufNet` / `testXLua`）

### 步骤 2：加入 Build 列表

**File → Build Settings** → 把 `Scene_Test_AOT` 拖到 **Scenes In Build** 第一位

### 步骤 3：确认编译符号已自动注入

1. 看 Console 是否有这条日志：
   `[NBV0] 已更新编译符号：NBC_HAS_PROTOBUF_NET=True, NBC_HAS_XLUA=True`
2. 若没有，或值为 False，打开 **Tools → NBC → 诊断 M0 探针依赖**，把输出贴给我
3. 也可手动核对：**Edit → Project Settings → Player → Other Settings → Scripting Define Symbols**

### 步骤 4：先在 Editor 里跑一次（基线）

点 **Play**，应该看到屏幕左上角出现结论框：

```
NBV0 环境探针（M0 / D17）
Runtime: 2022.3.62f3c1 | ... | Scripting: Mono (Editor 或 Mono 出包)
protobuf-net: 通过 (XX 字节) ...
xLua: 通过 Lua 求和 1..10 = 55（期望 55）
```

> **这一步只是基线**。Editor 走 Mono/JIT，AOT 问题**不会**在这里暴露 —— 必须出包才算验证。

### 步骤 5：IL2CPP Release 出包

按 M0 手册 **D17** 执行（**Windows Build Support (IL2CPP)** 必须已安装）：

1. Build Settings → 平台切到 **Windows / x86_64**
2. ☐ **取消勾选** Development Build、Script Autoconnection、Script Debugging
3. **Player Settings** 确认：**Scripting Backend = IL2CPP**、**Api Compatibility = .NET Standard 2.1**、**Managed Stripping Level = Low**
4. **Build** → 输出到 `Builds/Client/`
5. 双击运行 `Builds/Client/NBC.exe`

### 步骤 6：判定

| 画面显示 | 判定 | 下一步 |
| --- | --- | --- |
| 两项都"通过" | ✅ **D17 通过，M0 收关** | 把截图/日志给我，我记录实测结论 |
| protobuf-net 报异常 | ❌ AOT 裁剪问题（**风险 R4 命中**） | 见下方排查 |
| xLua 报异常 | ❌ 绑定代码未生成或符号未找到 | 菜单 **XLua → Generate Code** 后重新出包 |
| 两项都显示"跳过" | ⚠️ 编译符号没注入 | Tools → NBC → 诊断 M0 探针依赖 |
| 游戏闪退，看不到画面 | 看日志判定 | 见下方排查 |

**排查表**

| 现象 | 大概率原因 | 处理 |
| --- | --- | --- |
| 打包就失败，提示找不到 C++ 编译器 | 缺 VS2022「使用 C++ 的桌面开发」工作负载 | VS Installer → 修改 → 勾选该工作负载 + Windows 10/11 SDK |
| protobuf-net 抛 `NullReferenceException` / 字段全默认值 | **未做预编译序列化器** 或 link.xml 未生效 | ① 确认 `Assets/link.xml` 到位 ② 按 M0 手册 D7 生成静态序列化器 ③ 确认 dll 名称与 link.xml 一致 |
| `XLua.LuaException: attempt to call a nil value` | xLua 绑定代码未生成 | **XLua → Generate Code** |
| 编译符号一直是 False，但 dll 明明放了 | dll 文件名与脚本探测的候选名不同 | 跑诊断菜单，把实际文件名给我，我改探测列表 |
| 画面全黑但日志有输出 | 场景没加进 Build 列表，跑的是空场景 | 检查 Build Settings 的 Scenes In Build |

---

## 三、日志文件位置（出包后查看）

| 平台 | 路径 |
| --- | --- |
| Windows（出包） | `%USERPROFILE%\AppData\LocalLow\NBC\NBC\Player.log` |
| Unity Editor | `%LOCALAPPDATA%\Unity\Editor\Editor.log` |

---

## 四、这些文件什么时候可以删

| 文件 | 保留到 | 说明 |
| --- | --- | --- |
| `NBV0_EnvironmentProbe.cs` | **M6 之前建议保留** | 每次改动依赖版本后重跑一次，可当作 AOT 回归测试 |
| `Editor/NBV0_DefineInitializer.cs` | 可长期保留 | 自动维护编译符号，后续接入新库时还能复用（扩展候选名即可） |
| `link.xml` | **必须长期保留** | 它是 AOT 正常工作的一部分，不是临时文件 |

---

## 五、给 AI 的备注（记录设计意图，便于后续迭代）

| 设计点 | 原因 |
| --- | --- |
| 探针不使用 `NBC.Shared` 类型，自带 `ProbeMessage` | 让 M0 能独立验证第三方库，不被尚未完成的业务代码阻塞 |
| 用 `#if NBC_HAS_*` 条件编译而不是直接引用 | 避免"库还没接入时整个工程编译不过" |
| 编译符号由 Editor 脚本自动注入 | 手填易漏，且漏了只会静默走"跳过"分支，导致 D17 白验 |
| 结论同时输出到 Console 与 `OnGUI` | 出包后没有 Console 窗口，必须能在屏幕上看到 |
| `link.xml` 先用 `preserve="all"` | M0 目标是通过验证；收窄留到 M5 用性能与包体数据驱动 |
