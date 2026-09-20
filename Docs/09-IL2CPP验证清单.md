# IL2CPP 出包与 AOT 验证清单（D17）

| 项 | 内容 |
| --- | --- |
| 文档版本 | v1.2（**2026-09-20：三次出包全部记录在案，最终通过** —— 第 1 次发现 protobuf-net 含集合字段必炸；第 2 次用 4 条路矩阵证明"自动/显式"两条都失败；**换 Google.Protobuf 后第 3 次通过**。见 §五） |
| 对应步骤 | M0 手册 §9 / D17 |
| 目的 | 在**项目还没写几行业务代码时**，就把 IL2CPP 的 AOT / 代码裁剪问题暴露出来 |
| 状态 | 🟢 **已通过（2026-09-20 第 3 次出包）** —— **protobuf（Google.Protobuf）与 xLua 双双通过**，M0 收关 |

> **为什么这一步不能跳过**：xLua 依赖**运行时反射**，而 IL2CPP 是 AOT 编译并会裁剪"看起来没用到"的代码。
> 结果是 **Editor 里一切正常、打包后崩溃** —— 这是最贵的一类问题。
> 拖到 M6 才发现，可能要推翻协议层实现；在第 1 周发现，成本是几小时。
> **本项目就是靠这一步在第 1 周抓出了序列化库不可用的问题（见 §五）。**

---

## 一、前置条件（出包前逐项确认）

| # | 检查项 | 在哪看 | 通过标准 |
| --- | --- | --- | --- |
| 1 | Unity 版本 | **Help → About Unity** | **2022.3.62f3c1**（2026-09-20 由 2022.3.17f1c1 升级，原因见 `Docs\11-环境配置说明.md` §六 / 需求文档 R16） |
| 2 | **IL2CPP 模块已安装** | Unity Hub → Installs → 该版本 → 齿轮 → Add modules | ☑ Windows Build Support (IL2CPP) |
| 3 | **VS2022 C++ 工作负载** | Visual Studio Installer → 修改 | ☑ 使用 C++ 的桌面开发；☑ Windows 10/11 SDK |
| 4 | Scripting Backend | **Edit → Project Settings → Player → Other Settings** | **IL2CPP** |
| 5 | Api Compatibility Level | 同上 | **.NET Standard 2.1** |
| 6 | Managed Stripping Level | 同上 | **Low**（M0 先求通过；M5 再用数据驱动收窄） |
| 7 | Allow 'unsafe' Code | 同上 | ☑ 勾选 |
| 8 | `link.xml` 已就位 | Project 窗口 | **项目里有 3 份，全部保留、全部生效**（Unity 合并）：<br>① `Assets/link.xml`（手写：protobuf-net / .Core / System.Collections.Immutable / **Assembly-CSharp**）<br>② `Assets/XLua/Gen/link.xml`（xLua 自动生成，精到 `<type>`；**每次 Generate Code 会覆盖，别手改**）<br>③ `Packages/com.arongranberg.astar/link.xml`（A\* 自带） |
| 9 | 探针脚本已就位 | Project 窗口 | `Assets/_Project/Tests/Manual/NBV0_EnvironmentProbe.cs` |
| 10 | 编译符号已注入 | Console 日志 / Player Settings | 出现 `[NBV0] 已更新编译符号：… =True` |
| 11 | 场景已加入 Build | **File → Build Settings** | **`Scene_AOTProbe`** 在 Scenes In Build 且为第一个（⚠️ 2026-09-20 修正：原文档写的 `Scene_Test_AOT` **从未创建过**；正确做法见 M0 手册 D17 步骤 6） |
| 12 | Development Build 已关闭 | **File → Build Settings** | ☐ 未勾选（要验 Release 的裁剪行为） |

---

## 二、执行步骤

1. **File → Build Settings** → Platform 选 **Windows, Mac, Linux** → Target Platform **Windows** → Architecture **x86_64** → **Switch Platform**
2. **Output** 目录设为 `E:\U3D Projects\0_MyFile\3D联网战斗Demo\Builds\Client\`
3. 确认 Scenes In Build **只有 `Scene_AOTProbe`**（M0 阶段避免混入其他场景干扰）—— 怎么建这个场景见 M0 手册 **D17 步骤 6**
4. 点 **Build**，等待完成（首次 IL2CPP 出包较慢，5~15 分钟属正常）
5. 双击 `Builds\Client\NBC.exe` 运行
6. 观察屏幕左上角的结论框 + 查看日志

---

## 三、验收标准

| # | 检查项 | 通过标准 | 实测结果 |
| --- | --- | --- | --- |
| A1 | 出包成功 | 生成 `NBC.exe` + `UnityPlayer.dll` + `NBC_Data/`，无报错 | ✅ 第 1 次出包即成功 |
| A2 | 程序能启动 | 不闪退，能看到结论框 | ✅ 三次出包都能启动 |
| A3 | 运行环境 | 显示 `Scripting: IL2CPP`、`Build: Release` | ✅ Unity 2022.3.62f3c1 / WindowsPlayer / IL2CPP / Release |
| A4 | **protobuf 序列化往返**（换库后为 **Google.Protobuf**） | 显示"**通过**"，且字节数与 Editor 中一致 | ✅ **第 3 次出包通过**（Google.Protobuf，**49 字节**；`Id/Name/Hp/Ratio/Flag` 与列表全部匹配）<br>❌ 第 1、2 次为 protobuf-net 失败（见 §五） |
| A5 | **xLua 执行** | 显示"**通过** Lua 求和 1..10 = 55" | ✅ **第 1 次出包即通过**（三次均通过） |
| A6 | 无异常日志 | 日志无 `Exception` / `NullReferenceException` | ✅ 第 3 次出包日志中两项均为"通过" |
| A7 | 包体记录 | 记录 `NBC_Data/` 体积（后续优化对比基线） | ⬜ 未记录（不影响 M0 收关，M5 性能阶段补） |
| A8 | 启动耗时记录 | 从双击到出画面（后续优化对比基线） | ⬜ 未记录（同上） |

> **A4 与 A5 是核心**。两项均"通过" → **M0 收关**（需求文档 §18.1 排期红线）。

---

## 四、失败排查表

| 现象 | 大概率原因 | 处理 | 验证方式 |
| --- | --- | --- | --- |
| 出包就失败，提示找不到 C++ 编译器 | 缺 VS2022「使用 C++ 的桌面开发」工作负载 | VS Installer → 修改 → 勾选该工作负载 + Windows SDK | 重新 Build |
| 提示 `Windows SDK not found` | 缺对应 SDK 版本 | VS Installer 勾选 Windows 10/11 SDK | 重新 Build |
| 程序**闪退**，日志有 `NullReferenceException` 指向 `Serializer` | **protobuf-net 未做预编译**（AOT 裁剪） | ➖ **已不适用** —— 本项目 2026-09-20 已换 **Google.Protobuf**（生成代码零反射）。若在别的项目遇到，见 M0 手册 D7 的历史说明 | — |
| protobuf-net 不抛异常但**字段全为默认值** | 与上同（裁剪的另一种表现，更隐蔽） | ➖ **已不适用**（同上） | — |
| `Reference has errors 'System.Runtime.CompilerServices.Unsafe'` | **少放了 DLL** | `Client\Assets\ThirdParty\GoogleProtobuf\` 应有 **2 个** DLL（见 M0 手册 D7 ①） | 重出包 |
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

## 五、实测结果（三次出包全记录 · 2026-09-20 最终通过）

### 5.1 三次出包的结果矩阵（历史不覆盖）

| 出包 | 序列化方案 | protobuf 项 | xLua 项 | 备注 |
| --- | --- | --- | --- | --- |
| **第 1 次** | protobuf-net 3.2.30（默认静态 `Serializer`） | ❌ 失败（`GetTypeModifiers` icall） | ✅ 通过 | 发现"含集合字段必炸" |
| **第 2 次** | protobuf-net 3.2.30 + **4 条路矩阵** | ❌ 失败（自动/显式两条都失败） | ✅ 通过 | 证明 protobuf-net 不可用 → **决策换库** |
| **第 3 次** | **Google.Protobuf 3.21.1** | ✅ **通过（49 字节）** | ✅ 通过 | **M0 收关** |

### 5.2 第 1 次出包的记录（保留为历史证据）

| 项 | 结果 |
| --- | --- |
| 执行日期 | **2026-09-20** |
| Unity 版本 | **2022.3.62f3c1** |
| Scripting Backend | **IL2CPP**（Build **Release**，未开 Development Build） |
| Stripping Level | **Low** |
| protobuf-net（Editor 基线） | 未单独记录（本次只记了出包结果） |
| **protobuf-net（IL2CPP 出包）** | ❌ **失败** —— `NotSupportedException`：IL2CPP **未实现** `RuntimeParameterInfo::GetTypeModifiers` 这个 icall（原文见下） |
| xLua（Editor 基线） | 未单独记录 |
| **xLua（IL2CPP 出包）** | ✅ **通过** —— Lua 求和 1..10 = **55**（期望 55） |
| `NBC_Data` 体积 | 未记录（A7/A8 留到 M5 性能阶段补） |
| 启动耗时 | 未记录（同上） |
| 遇到的报错原文 | 见下方「报错原文」 |
| 当时的判定 | ☑ **未通过（第 1 次）** —— 出包与运行**成功**，但 protobuf-net 项未通过 |

### 5.3 第 3 次出包的记录（**最终结果**）

| 项 | 结果 |
| --- | --- |
| 执行日期 | **2026-09-20**（换库后重出包） |
| Unity 版本 / 平台 | **2022.3.62f3c1** / WindowsPlayer |
| Scripting Backend | **IL2CPP**（**Release**） |
| 序列化库 | **Google.Protobuf 3.21.1** |
| **protobuf（IL2CPP 出包）** | ✅ **通过** —— `Google.Protobuf`，**49 字节**；`Id=1001 Name=联网战斗Demo Hp=3200`、`SkillIds=8 个（首个=2001）`、`id/name/hp/ratio/flag 全 True`、**`列表=True/True`** |
| **xLua（IL2CPP 出包）** | ✅ **通过** —— Lua 求和 1..10 = **55**（期望 55） |
| **最终判定** | ☑ **通过** —— **M0 全部 18 项完成（18/18）** |

**第 3 次出包的日志原文（来自 `%USERPROFILE%\AppData\LocalLow\NBC\NBC\Player.log`）**

```text
[protobuf] 通过  Google.Protobuf  (49 字节)
  Id=1001 Name=联网战斗Demo Hp=3200
  SkillIds=8 个（首个=2001）
  id=True name=True hp=True ratio=True flag=True 列表=True/True
[xLua]    通过  Lua 求和 1..10 = 55（期望 55）
```

> 📌 **关键点**：`SkillIds=8 个（首个=2001）` 与 `列表=True/True` —— 这正是**集合（repeated）字段**的往返，
> 也就是**第 1、2 次让 protobuf-net 必炸的那个形状**。换库后它在 IL2CPP 下**正确工作**。

### 5.4 第 1 次出包的报错原文（历史证据，保留）

```text
[NBV0][Protobuf] 异常（极可能是 AOT/裁剪问题）：
  NotSupportedException: D:\SOFT\Unity\Hub\Editor\2022.3.62f3c1\Editor\Data\il2cpp\libil2cpp\icalls\
      mscorlib\System.Reflection\RuntimeParameterInfo.cpp(23) :
      Unsupported internal call for IL2CPP:RuntimeParameterInfo::GetTypeModifiers
      - "This icall is not supported by il2cpp."
  at ProtoBuf.Meta.MetaType.<ResolveTupleConstructor>g__IsPublicSetter|76_0 (MethodInfo method)
  at ProtoBuf.Meta.MetaType.ResolveTupleConstructor (Type type, MemberInfo[]& mappedMembers)
  at ProtoBuf.Meta.MetaType.GetContractFamily (RuntimeTypeModel model, Type type, AttributeMap[] attributes)
  at ProtoBuf.Meta.RuntimeTypeModel.TryGetBasicTypeSerializer (Type type)
  at ProtoBuf.Meta.RuntimeTypeModel.FindOrAddAuto (...)
  at ProtoBuf.Meta.RuntimeTypeModel.TryGetRepeatedProvider (Type type, CompatibilityLevel ambient)
  at ProtoBuf.Meta.MetaType.ApplyDefaultBehaviour (bool isEnum, ProtoMemberAttribute normalizedAttribute)
  at ProtoBuf.Meta.MetaType.ApplyDefaultBehaviourImpl (CompatibilityLevel ambient)
  at ProtoBuf.Meta.MetaType.ApplyDefaultBehaviour (CompatibilityLevel ambient)
  at ProtoBuf.Meta.RuntimeTypeModel.FindOrAddAuto (...)
  at ProtoBuf.Meta.RuntimeTypeModel.<GetServicesSlow>g__GetServicesImpl|88_0 (...)
  at ProtoBuf.Meta.RuntimeTypeModel.GetServicesSlow (Type type, CompatibilityLevel ambient)
  at ProtoBuf.Meta.RuntimeTypeModel.GetSerializer[T] ()
  at ProtoBuf.Meta.TypeModel.TryGetSerializer[T] (TypeModel model)
  at ProtoBuf.Meta.TypeModel.SerializeImpl[T] (ProtoWriter+State& state, T value)
  at ProtoBuf.Serializer.Serialize[T] (Stream destination, T instance, object userState)
  at NBC.Tests.Manual.NBV0_EnvironmentProbe.ProbeProtobufNet ()
```

### 5.5 从第 1、2 次验证得出的结论（已回填）

1. **protobuf-net 的 AOT 问题【不是代码裁剪】，而是 IL2CPP 缺少一个 icall。**
   > ⚠️ **这条推翻了本文档与需求文档 R4 的原始假设**（原假设：IL2CPP 把反射要用的元数据裁掉了 → 加 `link.xml` 解决）。
   > 实测是 `RuntimeParameterInfo::GetTypeModifiers` **根本没实现** → **加多少 `link.xml` 都没用**。
   > 失败点在 **`RuntimeTypeModel` 运行时自动构建类型模型**这一步（`GetSerializer[T]` → `ApplyDefaultBehaviour` → `ResolveTupleConstructor`），
   > 而**不在**序列化本身。触发位置是解析"重复字段（`List<int>`）的元素类型"。
2. **`link.xml` 中实际生效的程序集名**：`Assembly-CSharp`（xLua 全树无 asmdef，其代码编进该程序集，实测 109 个 `XLua.*` 类型）；
   protobuf 三个程序集名为 `protobuf-net` / `protobuf-net.Core` / `System.Collections.Immutable`（均实测匹配）。
3. **是否需要额外的 `preserve` 条目**：本次报错与 `preserve` **无关**，无需为此新增。
4. **protobuf-net v3 的预编译 API 现状（实测签名，与网上老文章不同）**：
   - `RuntimeTypeModel.Compile(CompilerOptions)` → 返回 `TypeModel`；**没有** v2 时代的 `Compile(assemblyName, path)` 落盘重载
   - 可用的还有：`CompileInPlace()`、`Freeze()`、`Serializer.PrepareSerializer`、`MetaType.Add(int, string)`
   - 该 DLL 里**不含** `protobuf-net.BuildTools`（源生成器是独立 NuGet 包）
   → **所以"照着老文章生成一个序列化器 DLL"这条路不能照搬**，需另找方案。
5. **修复方向（当时按此改了探针，第 2 次出包实测证明无效）**：
   让 protobuf-net **不做自动发现** —— `RuntimeTypeModel.Create(name)` + `Add(typeof(T), applyDefaultBehaviour: **false**)` + 逐个显式 `Add(fieldNo, memberName)`。
   探针曾改为**一次测 4 条路**的矩阵（T1 纯标量 / T2 可写集合 / T3 原 DTO / T4 显式模型）。
   代码原在 `Client/Assets/_Project/Tests/Manual/` 下的 `NBV0_ProtobufAotMatrix.cs`
   —— ⚠️ **该文件已随 2026-09-20 的换库删除**（序列化改用 Google.Protobuf 后不再需要这个矩阵探针）。此处的记录保留为历史事实。

### 5.6 第 2 次出包结果（2026-09-20，矩阵实测）—— **两条路都不通**

| 用例 | 结果 | 说明 |
| --- | --- | --- |
| T1 默认API · 纯标量（无集合成员） | ✅ **通过** | 往返数据一致 → **标量成员没问题** |
| T2 默认API · 可写 `List<int>` | ❌ **失败** | 同一个 `GetTypeModifiers` icall → **集合是触发条件，与"只读"无关** |
| T3 默认API · 原 DTO（只读 `List<int>`） | ❌ **失败** | 同一个 icall（复现基线） |
| **T4 显式 `TypeModel` · 原 DTO** | ❌ **失败** | **同一个 icall** —— `Add(typeof(T), false)` + `AutoAddMissingTypes=false` **也绕不过去** |

**最终结论（有实测支撑）**：

> **protobuf-net 3.2.30 在 Unity 2022.3 + IL2CPP 下，只要消息含集合（repeated）字段就必然抛
> `NotSupportedException`；"自动发现"与"显式 TypeModel"两条主要路径均无法规避。**
> `mt.Add(fieldNo, memberName)` 在解析集合**元素类型**的契约时仍会走到 `ResolveTupleConstructor`。

**对本项目的意义**：网络协议**必然**包含 repeated 字段（技能列表、快照里的实体列表、输入队列……），
所以 **protobuf-net 不满足本项目的 AOT 要求**。剩余可行路线只有两条：

| 路线 | 说明 | 评估 |
| --- | --- | --- |
| 用 `protobuf-net.BuildTools` 源生成器（Unity 官方支持源生成器） | 编译期生成显式序列化代码，彻底不做运行时模型构建 | 需自取 NuGet analyzer DLL + `RoslynAnalyzer` 标签 + 与 Unity 的 Roslyn 版本匹配；**v3 的 DLL 里不含 BuildTools**，可行性未验证 |
| **改用 `Google.Protobuf`（`protoc` 生成纯 C#）** | 生成代码**零反射**，原生 AOT 安全；生成代码可放进 `NBC.Shared` **双端共用** | ✅ **已采用**（见需求文档 §5.4 的修订与决策记录） |

**第 2 次出包时的 D17 判定**：☑ 未通过（protobuf-net 项）→ 按排期红线**不进入 M1**，先定序列化方案。

### 5.7 后续：换库后第 3 次出包**一次通过**（2026-09-20）

按上表选择了 **Google.Protobuf**，并据此改造了客户端依赖与探针：

| 改动 | 内容 |
| --- | --- |
| 序列化库 | protobuf-net 3.2.30 → **Google.Protobuf 3.21.1**；Unity 侧 DLL **4 个 → 2 个** |
| 协议来源 | 新增仓库顶层 **`Protocol/`**（`.proto` 唯一来源）→ `protoc` 生成 `Client\Assets\_Project\Protocol\NbcProbe.cs`（命名空间 `NBC.Protocol`） |
| `link.xml` | protobuf 的 **3 条条目全部删除**（生成代码零反射，不需要裁剪保护）；只剩 1 条 `Assembly-CSharp`（为 xLua） |
| 编译符号 | `NBC_HAS_PROTOBUF_NET` → **`NBC_HAS_PROTOBUF`**（去掉库名，以后再换库不用改符号） |
| 探针 | `NBV0_ProtobufAotMatrix.cs` 删除；`NBV0_EnvironmentProbe.cs` 改用 `ProbeMessage.ToByteArray()` / `Parser.ParseFrom()` |

**第 3 次出包结果**：**protobuf（Google.Protobuf）与 xLua 双双通过** —— 见上面 **5.3**。
其中 `SkillIds=8 个（首个=2001）`、`列表=True/True` 证明**集合字段**在 IL2CPP 下正确往返
（这正是前两次让 protobuf-net 必炸的形状）。

> **D17 最终判定**：☑ **通过** —— **M0 全部 18 项完成（18/18）**。

---

## 六、这次验证的面试价值

这段经历可以直接讲成一个技术故事：

> "我在项目第 1 周、还没写业务代码的时候，就先做了一次 IL2CPP 出包验证 ——
> 因为 **Editor 里一切正常、打包后才崩**是最贵的一类问题。
> 我写了一个探针脚本，在出包后的进程里做一次 protobuf 序列化往返和一次 Lua 执行。
>
> **第 1 次出包**就抓到问题：`protobuf-net` 抛 `NotSupportedException`，IL2CPP 说
> `RuntimeParameterInfo::GetTypeModifiers` 这个 icall 没实现。
> 关键是**我没有停在"加 link.xml"这个惯性答案上** —— 我读了报错原文，确认这**不是裁剪问题**，
> 而是"功能未实现"，所以 link.xml 根本管不了。
>
> **第 2 次出包**我设计了一个**4 条路的矩阵**，一次出包就同时验证"默认自动发现"和"显式 `TypeModel`"两条路 ——
> 结果两条都不通，而且定位到触发条件是**集合（repeated）字段**。
>
> 这时候我做了一个**时机判断**：网络协议必然有 repeated 字段，protobuf-net 这条路要么用未验证的源生成器方案去赌，
> 要么换库；而**此刻协议层还没写一行代码，换库成本最低**。于是我换成 `protoc` 生成代码的 **Google.Protobuf**。
>
> **第 3 次出包一次通过**：protobuf 与 Lua 双双通过，集合字段往返正确（8 个技能 ID）。
> 顺带把 Unity 侧依赖从 **4 个 DLL 降到 2 个**、`link.xml` **少 3 条条目**、彻底消除运行时反射。"

**关键点**：主动暴露风险 + 用机械手段验证 + **分清"缺东西"和"缺功能"** + **用数据做换库决策**。
这比"我用了 protobuf-net"或"我发现有 bug 就换了个库"有说服力得多。
