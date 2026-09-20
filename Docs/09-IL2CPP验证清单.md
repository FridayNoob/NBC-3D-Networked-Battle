# IL2CPP 出包与 AOT 验证清单（D17）

| 项 | 内容 |
| --- | --- |
| 文档版本 | v1.0（**待实测回填**） |
| 对应步骤 | M0 手册 §9 / D17 |
| 目的 | 在**项目还没写几行业务代码时**，就把 IL2CPP 的 AOT / 代码裁剪问题暴露出来 |
| 状态 | ⬜ 等待执行（本文档的"实测结果"章节由执行后回填） |

> **为什么这一步不能跳过**：protobuf-net 与 xLua 都依赖**运行时反射**，而 IL2CPP 是 AOT 编译并会裁剪"看起来没用到"的代码。
> 结果是 **Editor 里一切正常、打包后崩溃** —— 这是最贵的一类问题。
> 拖到 M6 才发现，可能要推翻协议层实现；在第 1 周发现，成本是几小时。

---

## 一、前置条件（出包前逐项确认）

| # | 检查项 | 在哪看 | 通过标准 |
| --- | --- | --- | --- |
| 1 | Unity 版本 | **Help → About Unity** | 2022.3.17f1c1 |
| 2 | **IL2CPP 模块已安装** | Unity Hub → Installs → 该版本 → 齿轮 → Add modules | ☑ Windows Build Support (IL2CPP) |
| 3 | **VS2022 C++ 工作负载** | Visual Studio Installer → 修改 | ☑ 使用 C++ 的桌面开发；☑ Windows 10/11 SDK |
| 4 | Scripting Backend | **Edit → Project Settings → Player → Other Settings** | **IL2CPP** |
| 5 | Api Compatibility Level | 同上 | **.NET Standard 2.1** |
| 6 | Managed Stripping Level | 同上 | **Low**（M0 先求通过；M5 再用数据驱动收窄） |
| 7 | Allow 'unsafe' Code | 同上 | ☑ 勾选 |
| 8 | `link.xml` 已就位 | Project 窗口 | `Assets/link.xml` 存在 |
| 9 | 探针脚本已就位 | Project 窗口 | `Assets/_Project/Tests/Manual/NBV0_EnvironmentProbe.cs` |
| 10 | 编译符号已注入 | Console 日志 / Player Settings | 出现 `[NBV0] 已更新编译符号：… =True` |
| 11 | 场景已加入 Build | **File → Build Settings** | `Scene_Test_AOT` 在 Scenes In Build 且为第一个 |
| 12 | Development Build 已关闭 | **File → Build Settings** | ☐ 未勾选（要验 Release 的裁剪行为） |

---

## 二、执行步骤

1. **File → Build Settings** → Platform 选 **Windows, Mac, Linux** → Target Platform **Windows** → Architecture **x86_64** → **Switch Platform**
2. **Output** 目录设为 `E:\U3D Projects\0_MyFile\3D联网战斗Demo\Builds\Client\`
3. 确认 Scenes In Build 只有 `Scene_Test_AOT`（M0 阶段避免混入其他场景干扰）
4. 点 **Build**，等待完成（首次 IL2CPP 出包较慢，5~15 分钟属正常）
5. 双击 `Builds\Client\NBC.exe` 运行
6. 观察屏幕左上角的结论框 + 查看日志

---

## 三、验收标准

| # | 检查项 | 通过标准 | 实测结果 |
| --- | --- | --- | --- |
| A1 | 出包成功 | 生成 `NBC.exe` + `UnityPlayer.dll` + `NBC_Data/`，无报错 | ⬜ |
| A2 | 程序能启动 | 不闪退，能看到结论框 | ⬜ |
| A3 | 运行环境 | 显示 `Scripting: IL2CPP`、`Build: Release` | ⬜ |
| A4 | **protobuf-net 序列化往返** | 显示"**通过**"，且字节数与 Editor 中一致 | ⬜ |
| A5 | **xLua 执行** | 显示"**通过** Lua 求和 1..10 = 55" | ⬜ |
| A6 | 无异常日志 | 日志无 `Exception` / `NullReferenceException` | ⬜ |
| A7 | 包体记录 | 记录 `NBC_Data/` 体积（后续优化对比基线） | ⬜ ____ MB |
| A8 | 启动耗时记录 | 从双击到出画面（后续优化对比基线） | ⬜ ____ 秒 |

> **A4 与 A5 是核心**。任何一项不是"通过"，M0 就不算收关（需求文档 §18.1 排期红线）。

---

## 四、失败排查表

| 现象 | 大概率原因 | 处理 | 验证方式 |
| --- | --- | --- | --- |
| 出包就失败，提示找不到 C++ 编译器 | 缺 VS2022「使用 C++ 的桌面开发」工作负载 | VS Installer → 修改 → 勾选该工作负载 + Windows SDK | 重新 Build |
| 提示 `Windows SDK not found` | 缺对应 SDK 版本 | VS Installer 勾选 Windows 10/11 SDK | 重新 Build |
| 程序**闪退**，日志有 `NullReferenceException` 指向 `Serializer` | **protobuf-net 未做预编译**（AOT 裁剪） | ① 确认 `link.xml` 生效 ② 按 M0 手册 D7 生成静态序列化器 ③ 启动时注册 `TypeModel` | 重出包 |
| protobuf-net 不抛异常但**字段全为默认值** | 与上同（裁剪的另一种表现，更隐蔽） | 同上 | 重出包 |
| `XLua.LuaException: attempt to call a nil value` | xLua 绑定代码未生成 | 菜单 **XLua → Generate Code** 后重出包 | 重出包 |
| `MissingMethodException` / `ExecutionEngineException` | 泛型被裁剪 | 在 `link.xml` 里对该程序集加 `preserve="all"` | 重出包 |
| 两项都显示"**跳过**" | 编译符号没注入 | **Tools → NBC → 诊断 M0 探针依赖**，看实际 dll 名 | 把输出发给 AI |
| 结论框是空白/全黑 | 场景没进 Build 列表，跑的是空场景 | 检查 Build Settings 的 Scenes In Build | — |
| Console 有 `[NBV0]` 日志但屏幕看不到 | `OnGUI` 被其他 UI 遮挡 | 临时禁用场景中其他 Canvas | — |

### 日志位置

| 场景 | 路径 |
| --- | --- |
| 出包后运行 | `%USERPROFILE%\AppData\LocalLow\NBC\NBC\Player.log` |
| Editor 内 Play | `%LOCALAPPDATA%\Unity\Editor\Editor.log` |

---

## 五、实测结果（执行后回填）

> 执行完 D17 后，把下面的表格填上（或把日志/截图发给 AI，由 AI 回填）。

| 项 | 结果 |
| --- | --- |
| 执行日期 | |
| Unity 版本 | |
| Scripting Backend | |
| Stripping Level | |
| protobuf-net（Editor 基线） | |
| protobuf-net（IL2CPP 出包） | |
| xLua（Editor 基线） | |
| xLua（IL2CPP 出包） | |
| `NBC_Data` 体积 | |
| 启动耗时 | |
| 遇到的报错原文 | |
| 最终判定 | ⬜ 通过 / ⬜ 未通过 |

### 从本次验证得出的结论（回填）

1. **protobuf-net 预生成的确切命令/API**：
   （待实测填写 —— 这是 M0 手册 D7 刻意留空的部分，因为 2.x 与 3.x 差异较大，凭记忆写会误导）
2. **`link.xml` 中实际生效的程序集名**：
   （待实测填写 —— 与 dll 实际文件名核对后才算确认）
3. **是否需要额外的 `preserve` 条目**：
   （若 Stripping Low 仍裁剪，记录补了哪些条目）

---

## 六、这次验证的面试价值

这段经历可以直接讲成一个技术故事：

> "我在项目第 1 周、还没写业务代码的时候，就先做了一次 IL2CPP 出包验证。
> 因为 protobuf-net 依赖运行时反射生成序列化器，而 IL2CPP 是 AOT 并会裁剪代码，
> 这类问题在 Editor 里完全看不出来，只会在打包后崩。
> 我写了一个探针脚本，在出包后的进程里做一次序列化往返和一次 Lua 执行，
> 结果发现问题：……（按实测填写）。所以我加了 link.xml，并把序列化器改成预生成。
> 如果拖到项目后期才发现，可能要推翻整个协议层。"

**关键点**：主动暴露风险 + 用机械手段验证 + 能说清原理与后果。
这比"我用了 protobuf-net"有说服力得多。
