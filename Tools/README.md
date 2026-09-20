# Tools —— 辅助脚本

## Test-Network.ps1

**只读**网络诊断脚本：定位 HTTPS / 代理 / NuGet 连接**断在哪一层**。

### 为什么需要它

第 5 轮实测发现本机 `dotnet restore` 失败：

```
error NU1301: The SSL connection could not be established
error NU1301: Authentication failed / 安全包中没有可用的凭证
```

但"SSL 失败"这个报错**不足以定位原因** —— 它可能是 DNS、防火墙、TLS 拦截、证书信任、代理未开、
甚至只是当前沙箱限制。各层的原因与修法完全不同，所以需要分层探测。

### 分层探测思路

```
DNS 解析  ->  TCP 443  ->  TLS 握手  ->  HTTP  ->  代理  ->  NuGet 端点
```

| 第一处失败的层 | 结论 |
| --- | --- |
| DNS | hosts 文件 / DNS 服务器问题 |
| TCP | 防火墙 / 无路由 |
| **TCP 通但 HTTP 失败** | **TLS 被拦 / 证书信任问题 / 沙箱限制** |
| 直连失败但走代理成功 | 需要打开系统代理 |
| 代理端口无监听 | 代理客户端在跑，但它的代理功能是关的 |

### ⚠️ 请务必在沙箱外的普通 PowerShell 里也跑一次

这是**本脚本最重要的用法**。原因：

- AI 的运行环境带**网络沙箱**，其中从子进程发出的**所有直连 HTTPS 都失败**（已实测：连 `https://www.baidu.com` 都失败）
- 所以**在沙箱里跑出的"TLS 失败"不能代表你平时敲命令行的环境**
- **真正决定 M1 能否引进依赖的，是你在普通 PowerShell 窗口里的结果**

> 注意区分：本脚本通过 URL 发起 HEAD 请求，**这只证明"该 URL 从当前进程可达"**，
> 并**不能**推论"浏览器也能访问" —— 浏览器有自己的代理设置与 TLS 栈，两者相互独立。

### 用法

```powershell
cd "E:\U3D Projects\0_MyFile\3D联网战斗Demo"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Test-Network.ps1

# 探测指定的代理端口
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Test-Network.ps1 -ProxyPort 7890
```

### 它检查什么

| 段 | 内容 |
| --- | --- |
| 1 | PowerShell 版本、OS、本机 UTC 时间、**与服务器时间的偏差**（偏差 >5 分钟会直接破坏 TLS） |
| 2 | 代理环境变量、WinINET（`ProxyEnable`/`ProxyServer`）、WinHTTP 代理设置 |
| 3 | **本地代理端口是否真的在监听**（列出占用进程）+ 代理类进程清单 |
| 4 | 对 3 个目标（普通 HTTPS / NuGet 官方源 / NuGet 国内镜像）逐层测试 DNS → TCP → HTTP |
| 5 | 若检测到监听的代理端口，**自动经该代理重试一遍**做对比 |
| 6 | 结论：按"第一处失败的层"给出原因，并列出 4 个由便宜到贵的修法 |

### 本机（沙箱内）的实测结果

| 层 | 结果 |
| --- | --- |
| DNS | ✅ 正常（3 个目标都解析出 IP） |
| TCP 443 | ✅ 可达 |
| **TLS / HTTP** | ❌ **全部失败**，内层错误「安全包中没有可用的凭证」 |
| 代理监听 | ❌ 无任何端口在监听 |
| 代理进程 | ⚠️ `clash-verge-service`、`verge-mihomo` **在运行**，但代理未启用（配置里 `mixed-port: 7890`，而 7890 无监听） |

**推断**：两种可能并存 —— ① 沙箱限制（沙箱内所有直连 HTTPS 都失败）；② 系统代理未开。
**只有在沙箱外跑一次才能区分**，而那才是 M1 的真实条件。

### 退出码

| 码 | 含义 |
| --- | --- |
| 0 | 报告已生成（脚本只报告不判定失败） |

---

## Verify-PluginDlls.ps1

**只读**验证脚本：读取 DLL 的**真实程序集名**，用于确认 `link.xml` 写对了、以及 Google.Protobuf 的依赖闭包放齐了。

### 为什么需要它：这两件事都会"静默失败"

| 风险 | 静默失败的表现 |
| --- | --- |
| `link.xml` 里的 `fullname` 与真实程序集名不符 | Unity **不报错、不警告**，保护条目被直接忽略。只在 **IL2CPP 出包后**表现为崩溃或数据错误（风险 R4） |
| `Assets/ThirdParty/GoogleProtobuf/` 少放了传递依赖 DLL | 编译报"类型定义在未引用的程序集中"，但不容易联想到是"少放了一个包" |

**程序集名不能从包名推断** —— 脚本会从 DLL 元数据里读出来告诉你。例如把任意 DLL 重命名为 `MyProto.dll`，
脚本仍会报出它内部真实的程序集名（这正是我在测试中验证过的行为）。

### 用法

```powershell
cd "E:\U3D Projects\0_MyFile\3D联网战斗Demo"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Verify-PluginDlls.ps1

# 追加扫描自定义目录
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Verify-PluginDlls.ps1 -ExtraPaths "Client\Assets\ThirdParty"
```

### 它检查什么

| 项 | 说明 |
| --- | --- |
| DLL 程序集名清单 | 列出 `Assets/ThirdParty`、`Behavior Designer`、`AstarPathfindingProject` 下所有 DLL 的真实程序集名与版本 |
| protobuf 依赖闭包 | 检查 `Google.Protobuf` / `System.Runtime.CompilerServices.Unsafe` 两个是否都在（缺任一都会编译失败） |
| `link.xml` 条目校验 | 逐条比对真实程序集名，报出**匹配不到**的条目（静默失效的那些） |
| 反向检查 | 对 protobuf/lua/immutable 类程序集，若未进 `link.xml`，**直接打印该加的那一行** |
| 资产目录盘点 | 列出关键目录是否存在及文件数 |

### 建议的使用时机

| 时机 | 目的 |
| --- | --- |
| 导入完 Google.Protobuf 后（M0/D7） | 确认 **2 个** DLL 放齐 |
| 导入完 xLua 后（M0/D6） | 确认程序集名与 `link.xml` 一致 |
| **出 IL2CPP 包之前（M0/D17）** | 最后一道防线，避免静默失效 |
| 每次升级插件版本后 | 程序集名可能变，`link.xml` 要同步 |

### 退出码

| 码 | 含义 |
| --- | --- |
| 0 | 报告已生成（含发现的问题；本脚本只报告不判定失败） |
| 1 | `Client\Assets` 不存在（需先做 M0 的 D1） |

---

## ~~Prepare-M0.ps1~~ —— 已按规则 15 退役（2026-09-16）

**这个脚本已被删除，是刻意的。**

它原本把「建 14 个目录 + 搬 9 个文件」打包成一次执行。依据 **规则 15**（本项目的目的包括培养你的
操作能力与知识，**尽量避免使用一键脚本，即使它能提高效率**），它属于"替用户完成流程"的类型，
因此退役；对应步骤改写为 **`Docs\10-M0手动操作手册.md` §1.3 的手工教学版**（逐步命令 + 每个目录的作用
+ 验证方式 + 失败排查）。

**为什么删掉"更省事"的那个反而不亏**：这 14 个目录与 6 个 asmdef 要陪你走完 M1~M6。
自己建一遍，你会知道每个目录为什么存在、程序集边界是怎么划的；这对后面加代码、排错都直接有用。

> 若你想了解当时的实现（例如作为一个"PowerShell 批处理脚本"的参考），可查
> `DeepSeekOutput\2026-09-16-知识点总结-2-M0启动.md` 中的记录 —— 但它**不再作为操作手段**。

---

## 关于本目录的脚本：允许什么、禁止什么

依据规则 15，本目录**只允许保留"帮用户观察与判断"的只读脚本**：

| 允许 | 判据 | 例子 |
| --- | --- | --- |
| ✅ **只读诊断** | 输出信息供你判断，不改任何东西 | `Test-Network.ps1` |
| ✅ **只读校验** | 把"配置/依赖/引用是否正确"呈现给你，改由你动手 | `Verify-PluginDlls.ps1`、`Check-DocLinks.ps1` |
| ❌ **一键流程自动化** | 把多步骤打包执行，使中间动作不再出现在你眼前 | 已退役的 `Prepare-M0.ps1` |

**今后新增工具时的自检问题**：*它是在替用户做事，还是在帮用户看清事情？* 前者不写。

### 当前脚本一览

| 脚本 | 类型 | 一句话 |
| --- | --- | --- |
| `Test-Network.ps1` | 只读诊断 | 分层探测 HTTPS / 代理 / NuGet 断在哪一层 |
| `Verify-PluginDlls.ps1` | 只读校验 | 读 DLL 真实程序集名，校验 `link.xml` 与依赖闭包 |
| `Check-DocLinks.ps1` | 只读校验 | 分类文档引用：计划中 / 待创建 / 已退役 / **真断链** |

---

## Check-DocLinks.ps1

**只读**文档引用检查器：扫描所有 `.md` 里的项目内路径引用，把"缺失"**分类**，从而区分"计划中"与"真写错了"。

### 为什么需要它

本项目文档大量**前向引用**未来里程碑才产出的文件，例如需求文档里规划要产出：

```
Docs\06-框架改造记录.md   Docs\07-性能优化报告.md   Docs\13-设计模式说明.md
Docs\02-架构设计文档.md   Docs\03-协议定义说明.md   Docs\04-演示脚本.md   ...
（编号占用表见 Docs\01-项目需求文档.md §2.4.1 —— 新增文档前先查它，避免撞号）
```

这是**正常且有意的**。但累积之后，你无法再区分：

| 情况 | 含义 |
| --- | --- |
| 计划中（后续里程碑产出） | 正常，不用管 |
| **打错字 / 相对路径写错 / 本该已存在** | **真实缺陷** |

脚本把这条线自动划出来：先扫需求文档的交付物表，学会"哪些路径是计划中的"，
再把每处缺失引用分类为 `PLANNED` / `PENDING` / `RETIRED` / `BROKEN`。

### 输出怎么读

| 标记 | 含义 | 要不要管 |
| --- | --- | --- |
| `OK` | 目标存在 | 不用 |
| `PLANNED` | 需求文档里声明过的未来交付物 | 不用 |
| `PENDING` | `Client\` 下的**搬运目标路径**，M0 的 D1 之后才出现 | 不用 |
| `RETIRED` | 已被有意删除的文件（只作为历史说明出现） | 可选清理 |
| **`BROKEN`** | **缺失且无解释** | **要查**：打错字？路径错？本该已存在？ |

### 用法

```powershell
cd "E:\U3D Projects\0_MyFile\3D联网战斗Demo"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Check-DocLinks.ps1

# 只看真正的问题（隐藏预期中的 PLANNED/PENDING）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Check-DocLinks.ps1 -BrokenOnly
```

### 退出码

| 码 | 含义 |
| --- | --- |
| 0 | 没有 `BROKEN` 引用 |
| 1 | 至少有一条 `BROKEN` 引用需要人工判断 |

### 不用脚本，你自己怎么得出同样结论

```powershell
# 找出所有被文档引用、但当前不存在的项目内文件
$refs = Get-ChildItem -Recurse -Filter *.md | ForEach-Object {
    $t = Get-Content $_.FullName -Raw -Encoding UTF8
    [regex]::Matches($t, '`((?:Docs|Tools|ClientStaging|Server|Client)[\\/][^`\s]*?\.(?:md|ps1|sql|cs))`') |
        ForEach-Object { $_.Groups[1].Value.Replace('/','\') }
} | Sort-Object -Unique
$refs | Where-Object { -not (Test-Path $_) }
```

拿到缺失清单后，**你自己判断**每一条属于哪一类 —— 这正是脚本帮你省掉的机械部分，而判断权仍在你手上。

### ⚠️ 这个脚本是"必须含非 ASCII 的例外"

它需要真实的中文路径 `Docs\01-项目需求文档.md` 才能定位需求文档，所以它**不能是全 ASCII 脚本**。
按规则 W7b，它**保存为 UTF-8 with BOM**（PS 5.1 才能正确解码）。

**改它之后要补回 BOM** —— 编辑工具会按无 BOM 回写。自检命令见 `Docs\00-项目工作规则.md` 的
「PowerShell 脚本编码自检」小节。

---

## Test-Network.ps1 —— 为什么这样设计 / 不用脚本你怎么自己判断

### 为什么要"分层"而不是直接看报错

`dotnet restore` 报的是 `The SSL connection could not be established`。**这句话本身无法定位原因** ——
它至少对应六种情况，而修法完全不同。所以脚本按网络栈自下而上逐层探测，让你找"**第一处失败的层**"：

```
DNS 解析  →  TCP 443  →  TLS 握手  →  HTTP  →  代理  →  目标端点
```

### 不用脚本，你自己怎么得出同样结论

| 层 | 手工命令 | 怎么读结果 |
| --- | --- | --- |
| DNS | `Resolve-DnsName nuget.org` | 有 IP = 解析正常 |
| TCP | `Test-NetConnection nuget.org -Port 443` | `TcpTestSucceeded : True` = 端口通 |
| TLS/HTTP | `Invoke-WebRequest https://nuget.org -Method Head -UseBasicParsing` | 报 SSL 错 = **卡在 TLS 这一层** |
| 环境变量代理 | `$env:HTTPS_PROXY` | 空 = 没设 |
| 系统代理 | `Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' \| Select ProxyEnable,ProxyServer` | `ProxyEnable=0` = 系统代理关闭 |
| **代理是否真的在跑** | `Get-NetTCPConnection -LocalPort 7890 -State Listen` | **无输出 = 代理没在监听**（这条最容易误判） |

> **关键判断链**：TCP 通 + HTTP 失败 ⇒ 问题在 **TLS 或代理**这一层，而不是 DNS/防火墙。
> 这也是本次的实际结论：DNS ✅、TCP ✅、TLS ❌、且**没有任何代理端口在监听**。

### 为什么要求"在沙箱外再跑一次"

AI 的运行环境带网络沙箱，其中**所有直连 HTTPS 都失败**（实测连 `https://www.baidu.com` 都失败）。
所以在这里跑出的"TLS 失败"**区分不了**是沙箱限制还是你机器的真问题。
**决定 M1 能否引进依赖的是你自己的 PowerShell 环境**，因此必须在那边复验。

---

## Verify-PluginDlls.ps1 —— 为什么这样设计 / 不用脚本你怎么自己判断

### 为什么需要读 DLL 元数据

Unity 的 `link.xml` 用 `<assembly fullname="..." />` 声明"这些程序集不要被裁剪"。
**这个 `fullname` 必须与 DLL 内部的真实程序集名完全一致**，否则 Unity **不报错、不警告**，
该条目被静默忽略 —— 只在 IL2CPP 出包后表现为崩溃或数据错误（风险 R4）。

而**程序集名不能从文件名或包名推断**。这不是理论：本脚本的测试里，把任意 DLL 重命名为
`MyProto.dll` 放进去，它内部的真实名字是别的（`ILLink.CodeFixProvider`）。

### 不用脚本，你自己怎么得出同样结论

```powershell
# 读任意 DLL 的真实程序集名与版本（这就是脚本内部用的 API）
[System.Reflection.AssemblyName]::GetAssemblyName("C:\path\to\your.dll") | Select Name, Version
```

对照步骤：
1. 对 `Client\Assets\ThirdParty\GoogleProtobuf\` 下每个 DLL 跑一遍上面的命令，记下 `Name`
2. 打开 `Client\Assets\link.xml`，逐条比对 `<assembly fullname="...">` 与记下的 `Name`
3. **对不上的那条就是静默失效的** —— 改成正确名字，或删掉它

### 为什么还要检查"依赖闭包"

Unity 不认 NuGet：服务端用 `PackageReference` 会自动拉传递依赖，**Unity 只能靠你手工放 DLL**。
`Google.Protobuf 3.21.1` 需要 **2 个 DLL**（主包 + `System.Runtime.CompilerServices.Unsafe`），
少放任何一个都会编译失败，而报错信息指向的是"找不到某个类型"，很难联想到是"少放了一个包"。

---

## ⚠️ 本目录脚本的重要约定：**正文全 ASCII**

### 为什么

本机是 **Windows PowerShell 5.1**（没有 `pwsh`）。Microsoft 官方文档明确说明：

> 在 Windows PowerShell 中……**ANSI 也是 PowerShell 引擎从文件读取源代码时使用的内容**。
> 如果需要在脚本中使用非 Ascii 字符，请使用 BOM 将它们另存为 UTF-8。
> **如果没有 BOM，Windows PowerShell 会将脚本误解为使用过时的"ANSI"代码页进行编码。**

也就是说，**一个含中文的无 BOM `.ps1` 会被 PowerShell 按 GBK 解码**，中文字符串字面量被破坏，
表现为一堆莫名其妙的 `Unexpected token` / `hash literal was incomplete` 语法错误 ——
而文件本身是合法 UTF-8，用普通文本编辑器看完全正常。**这个坑很难第一眼看出原因。**

### 本项目的取舍

| 选项 | 评价 |
| --- | --- |
| **脚本全 ASCII（本项目采用）** | ✅ 编码无关，任何编辑器/任何 PowerShell 版本都能正确解析，随便改都不会坏 |
| 脚本含中文 + 保存为 UTF-8 **带 BOM** | ⚠️ 可运行，但**很多编辑器默认按无 BOM 保存**，一次误存就坏掉，且排查成本高 |

因此：**本目录两个保留脚本的注释与输出全部用英文，中文说明放在 `Docs/` 与本文档里。**

**你可以自己验证这条约定**（这也是一个有用的检查手法）：

```powershell
$p = "Tools\Test-Network.ps1"
$b = [System.IO.File]::ReadAllBytes($p)
"非 ASCII 字节数: " + ($b | Where-Object { $_ -gt 127 }).Count   # 应为 0
"是否有 BOM     : " + ($b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)   # 应为 False
```

### 相关规则

这条已写入 `Docs/00-项目工作规则.md` 的「代码书写附加条款」（条款 W7 / W8）。

---

## 后续工具规划

依据规则 15，**凡是"替你完成流程"的工具都不做**。下面这些原本列在计划里，
现在改变定位：**优先写成"你照着做的步骤"，只有在"只读观察"确实有价值时才提供脚本**。

| 计划 | 里程碑 | 定位（按规则 15 调整） |
| --- | --- | --- |
| 配置表导出（Excel → SO） | M1 | **属于工具本身不是脚本**：这是给策划用的生产工具，属于项目交付物；但**你会亲手写它**，我不代写 |
| 资源包构建 | M1 / M5 | 只给"菜单路径 + 参数含义"，你在 YooAsset 窗口里点 |
| 热更资源发布 | M5 | 给逐步操作（生成清单 → 复制到资源服务器目录） |
| 可复用模块导出（需求 13） | M6 | 给 Unity 菜单里的导出步骤 |

