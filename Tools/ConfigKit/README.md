# ConfigKit · 配置表工具链

> 对应：`Docs\17-配置表规范.md`（C1，**契约**）、`Docs\16-M1开工清单.md` C 组、需求 8 / 需求 13
>
> **一句话**：把"策划填的表格"变成"程序能用的强类型配置"，**并且在表填错的时候告诉他错在第几行第几列**。

---

## 一、为什么要做成"工具链"而不是"一个脚本"

`Docs\16` 的 **M1-R3** 风险条目写的是：

> 配置表工具做成"一次性脚本" → 表一改就崩

一次性脚本的特征是：**读取、校验、生成三件事缠在一坨**，
换个项目就废、加一条规则就要动主流程、想测一下还得先准备真 Excel。

所以本工具链按**四个可替换的接缝**切开，每一块都能单独用、单独测：

```
                 ┌──────────────────────────────────────────────┐
   表格文件  ──► │ ① ITableSource      「从哪读」               │
                 │      ├─ Delimited（CSV/TSV，零依赖）         │
                 │      └─ Xlsx（NPOI，唯一带第三方依赖的）      │
                 └───────────────────┬──────────────────────────┘
                                     ▼
                 ┌──────────────────────────────────────────────┐
                 │        RawTable（中立的内存表）               │
                 │   行号/列号/原文一律保留 —— 报错定位靠它       │
                 └───────────────────┬──────────────────────────┘
                                     ▼
   ConfigPolicy ─►┌──────────────────────────────────────────────┐
   （项目策略）    │ ② SchemaReader   五行表头 → TableSchema       │
                 │ ③ ValidationEngine 规则 → 结构化诊断           │
                 └───────────────────┬──────────────────────────┘
                                     ▼
                 ┌──────────────────────────────────────────────┐
                 │ ④ IDiagnosticFormatter / ITableEmitter        │
                 │   报错渲染成人话 / 产物写成 C#、JSON、SO…      │
                 └──────────────────────────────────────────────┘
```

**四个接缝就是"通用化"的全部内容**：

| 接缝 | 换掉它意味着 | 为什么需要 |
| --- | --- | --- |
| ① `ITableSource` | 换输入格式（xlsx / csv / 数据库 / 手写内存表） | 换项目时输入格式常常不同；**测试时根本不该需要文件** |
| ② `SchemaReader` | 换表头约定（比如别人用 JSON Schema 描述字段） | 五行表头是本项目的约定，不是所有项目的 |
| ③ `ConfigPolicy` | 换项目策略（命名规范、空值策略、浮点禁令） | **这才是"项目知识"该待的地方** —— Core 里不许出现 `Hero`、`万分比` 这种词 |
| ④ `IDiagnosticFormatter` / `ITableEmitter` | 换输出去向（控制台 / JSON 给 IDE / Unity 资产 / protobuf） | 同一份诊断要能给人看、也能给工具吃 |

---

## 二、工程划分与依赖方向（**机械保证**）

| 工程 | 目标框架 | 第三方依赖 | 职责 |
| --- | --- | --- | --- |
| `src/ConfigKit.Core` | `netstandard2.1` | **零** | 模型、接缝接口、表头解析、校验、诊断、代码生成 |
| `src/ConfigKit.Sources.Delimited` | `netstandard2.1` | **零** | ① 的 CSV / TSV 实现 |
| `src/ConfigKit.Sources.Xlsx` | `netstandard2.1` | **NPOI 2.8.0** | ① 的 `.xlsx` 实现（唯一允许引第三方的工程） |
| `src/ConfigKit.Cli` | `net8.0` | 零 | 薄宿主：命令行 → 串起来 → 退出码 |
| `tests/ConfigKit.SelfTest` | `net8.0` | 零 | 自测运行器（**为什么不是 xUnit 见 §五**） |

依赖方向**单向**，且由自测里的架构守卫机械检查：

```
Cli ─────► Core ◄───── Sources.Delimited
  │          ▲
  │          └──────── Sources.Xlsx
  └────► Sources.*（Cli 只认 ITableSource，不认具体实现）
```

> **规则**：`ConfigKit.Core` **不得**引用任何 `Sources.*`，也不得有任何 `PackageReference`。
> 违反时自测会红（`架构守卫` 那几条），不是靠自觉。

### ⚠️ 为什么 Core 钉在 `netstandard2.1` + C# 9

因为**同一份源码将来要能直接丢进 Unity 编译**（需求 13：这套工具要能复用到别的项目，
而"别的项目"很可能就是另一个 Unity 工程 —— 那时希望能在 Editor 菜单里直接跑校验，
不必依赖一个 dotnet 进程）。

代价是三条限制，写在 `Directory.Build.props` 里，也在这里重复一次：

| 限制 | 原因 |
| --- | --- |
| 不用 file-scoped namespace | 那是 C# 10 |
| 不用 `record` / `init` | 需要 `System.Runtime.CompilerServices.IsExternalInit`，netstandard2.1 里没有（要么手写 shim，要么别用） |
| 不开 `ImplicitUsings` | Unity 没有隐式 using，靠它编译会一堆 `CS0246` |

只有 `Cli` / `SelfTest` 两个**宿主**放开到 `net8.0` + C# 12 —— 它们不打算进 Unity。

---

## 三、"通用化"硬约束：Core 里不许出现项目词汇

| ❌ 不许出现在 `ConfigKit.Core` | ✅ 应该在哪 |
| --- | --- |
| 表名 `Hero` / `Skill` / `Level` | 由 Excel 的表头与 sheet 名提供（**数据**） |
| 单位"万分比""毫米" | 中文注释里（**给人看**），或 `ConfigPolicy` 的单位约定表 |
| "战斗数值禁止 float" | `ConfigPolicy.FloatPolicy`（**策略**，可被别的项目改成 `Allowed`） |
| "主键必须叫 id" | `ConfigPolicy.KeyRules` |
| "空单元格写 `-` 要报错" | `ConfigPolicy.EmptyCellRules` |

**判据**：如果一条规则"另一个项目可能不想要"，它就必须是 `ConfigPolicy` 的一个字段，
而不是 Core 里的一句 `if`。

---

## 四、位置信息是一等公民（报错定位的实现基础）

`Docs\17` §八 的报错契约要求"**文件 + 表 + 行 + 列**"四个要素。
做法是：**从读到的那一刻就把位置带上**，一路传到诊断里，而不是最后再回头查。

```
RawCell { Row(Excel 真实行号), Column(序号), ColumnName, Text }
    └─► Diagnostic { Severity, Code, Location(File, Sheet, Row, Column, ColumnName, ExcelLetter), Message, Expected, Actual }
            └─► TextDiagnosticFormatter  ← 只负责"排版成人话"，不负责"发现错误"
```

两个刻意的选择：

- **`SourceLocation` 存的是"Excel 真实行号"**（数据从第 5 行开始，第一条数据的报错就是"第 5 行"）。
  这条在规范里写死了：策划打开的是 Excel，所以行号必须是 Excel 的行号。
- **诊断是结构化的，排版是可换的**。以后想输出成 JSON 给 IDE / 给 CI 做注释，
  只要换一个 `IDiagnosticFormatter`，**校验逻辑一行都不用动**。

---

## 五、为什么自测不用 xUnit（**实测过的环境约束**）

2026-09-22 实测：

```
$ dotnet restore   （引用 NPOI 2.8.0 的最小工程）
error NU1301: 无法加载源 https://api.nuget.org/v3/index.json ...
              The SSL connection could not be established / Authentication failed
```

**本机在这个沙箱里还原不了任何 NuGet 包**（NPOI、xUnit 都一样；
`NBC.Server.Tests.csproj` 里的 xUnit 引用至今仍是注释状态，原因同此）。
而"**故意写错一格 → 报错定位到文件+表+行+列**"是 C2 的验收标准 ——
**验收标准必须能被跑一遍看到**，不能只写在文档里。

所以 `tests/ConfigKit.SelfTest` 是一个**零依赖的控制台自测运行器**：

```
dotnet run --project Tools/ConfigKit/tests/ConfigKit.SelfTest
```

- **退出码 0 = 全绿**；非 0 = 有几条红（会逐条打印）
- 这不是"造轮子"的偏好，而是**环境逼出来的等价物**，并且是本项目一贯做法
  （`Server\_api-probe` 编译闸门、`.dsh-tmp\fixprobe` 数值对拍探针、`Server\_unity-ref-probe` 都是这么来的）
- **将来 xUnit 能还原时**，把 `Program.cs` 里的断言搬进 `[Fact]` 即可 ——
  断言体本身不依赖这个运行器

### NPOI 的处置（诚实记录）

| 谁是瓶颈 | 状态 |
| --- | --- |
| 我（AI）还原 NPOI | ❌ 不行（无网络，已实测） |
| 你（负责人）还原 NPOI | ✅ 可以：在能联网的机器上 `dotnet restore Tools\ConfigKit\ConfigKit.sln` |
| 在此之前怎么办 | 用 `Sources.Delimited`（CSV/TSV，**零依赖**）跑通全流程。Excel 里"另存为 CSV（UTF-8）"即可 |

> 这不是降级方案：`Docs\01` 的 **CFG-10（`.csv` 来源，P2）** 本来就在路线图上。
> 只是它现在从"顺便支持"变成了"先支持"。

---

## 六、怎么用

### ⚠️ 本环境的一条硬规矩：`dotnet run` 必须配 `--no-build`

2026-09-22 实测：**`dotnet run` 带隐式构建会失败**（它要去捕获构建过程的 stdio，
在沙箱里被挡），而报错是误导性的 `生成失败。请修复生成错误并重新运行。`（构建其实没问题）。
所以本仓库里一律**先 build、再 `--no-build`**，或者**直接跑生成的 exe**：

```powershell
# ① 自测（零依赖，随时可跑）
dotnet build Tools\ConfigKit\tests\ConfigKit.SelfTest\ConfigKit.SelfTest.csproj -m:1
dotnet run --project Tools\ConfigKit\tests\ConfigKit.SelfTest\ConfigKit.SelfTest.csproj --no-build

# ② 导出（CSV/TSV 来源，零依赖）
dotnet build Tools\ConfigKit\src\ConfigKit.Cli\ConfigKit.Cli.csproj -m:1
Tools\ConfigKit\src\ConfigKit.Cli\bin\Debug\net8.0\NBC.ConfigKit.Cli.exe `
    --source Client\Assets\_Project\Configs\Design --out Client\Assets\_Project\Game\Config\Generated

# ③ 只校验不落盘（CI / 改表后先跑一遍）
... --source <目录> --check
```

**退出码**：`0` 成功 / `2` **表里有错（未产出任何文件）** / `1` 用法或环境错误。

### ⚠️ CSV 的一条约定：**含逗号的格子必须用双引号包起来**

这条是端到端测试当场抓出来的：规则行写成 `key,range(1,10)` 时，
CSV 会把它拆成 `range(1` 和 `10)` 两格 —— 于是报"不认识的规则"。
工具现在会**额外给一句提示**（"看起来像被逗号拆开了"），但正解是加引号：

| 内容 | 在 `.csv` 里必须写成 |
| --- | --- |
| `range(1,999999)` / `len(1,16)` | `"range(1,999999)"` / `"len(1,16)"` |
| 数组 `2001,2002` | `"2001,2002"` |
| 备注里带逗号的文本 | `"他说，你好"` |

（`.xlsx` 来源**没有这个问题** —— 单元格就是单元格。这也是 NPOI 还原后值得切过去的原因之一。）

---

## 七、进度

| 步骤 | 状态 |
| --- | --- |
| 骨架 + 统一构建属性（netstandard2.1 / C# 9 约束） | ✅ 2026-09-22 |
| `ConfigKit.Core`：诊断 / 位置 / 表模型 / 表头解析 / 校验引擎 / 策略 | ✅ |
| `ConfigKit.Sources.Delimited`（CSV / TSV） | ✅ |
| `ConfigKit.SelfTest`（自测运行器 + 架构守卫） | ✅ **39 条全绿** |
| `ConfigKit.Core` 代码生成（`Config_*.cs` + `<表>Config.cs`） | ✅ |
| `ConfigKit.Cli`（命令行 + 退出码 0/1/2） | ✅ |
| `ConfigKit.Sources.Xlsx`（NPOI，**需你先还原**） | ⏳ 待还原 |
| Unity 侧薄导入器（读生成物 → 建 `.asset`） | ⏳ |
| `ITableEmitter` 的 JSON 实现 | ⏳ |

---

## 八、已知局限（写清楚，不藏）

| # | 局限 | 说明 / 什么时候补 |
| --- | --- | --- |
| 1 | **校验规则是"封闭集合"** | `key / unique / min / max / range / len / view` 七条写死在 `ValidationRuleCatalog`，而且**加一条要改两处**（目录 + 引擎实现）。这确实削弱了"模块化" —— 真正的可插拔规则应该是"注册表 + 规则委托"。**M2 若需要项目自定义规则再改**；现在只有 3 张表，为此引入一层注册表接口是过度设计 |
| 2 | **枚举成员不校验** | 枚举定义在 C# 里（`Docs\17` §二），工具在没被告知成员时只检查"非空 + 像标识符"。想严格校验就往 `ConfigPolicy.KnownEnumMembers` 里填，或将来让工具去扫 `enum` 的 C# 源码 |
| 3 | **不做外键环检测** | M1 只做存在性（`Docs\17` §十一 已如实记录） |
| 4 | **分隔符来源不支持多行单元格** | 行号一律按**物理行**算 —— 因为报错里的"第 12 行"必须是策划在文件里看到的那一行。真要多行文本请用 xlsx 来源 |
| 5 | **CSV 没有 sheet 概念** | 所以约定 **文件名 = 表名**；xlsx 来源里文件名与表名是两个东西，两者的"表名来源"不同，`ITableSource` 已经把这件事挡在接缝后面 |
| 6 | **`Nullable` 关着** | Core 要能原样丢进 Unity（那边不开 nullable 上下文）。空引用安全靠注释 + 防御性判空 |
