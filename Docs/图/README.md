# 项目图集（Mermaid 真源 + SVG 产物）

> 建立日期：2026-09-26。用途：**面试/复习用**，以及给 `Docs\*.md` 里的文字配图。
> 原则：**图里每个数字都能追到代码/配置表**（每篇末尾都有"出处"表），不凭印象画。

---

## 一、篇目

| 篇 | 内容 | 图 |
| --- | --- | --- |
| [`01-M3网络架构与状态同步.md`](01-M3网络架构与状态同步.md) | 客户端/共享层/传输/服务端四层 + 一帧里的数据流 + D1~D8 落在哪 | 分层图、时序图 |
| [`02-副本·掉落·任务闭环.md`](02-副本·掉落·任务闭环.md) | 进房 → 打死 → 掷掉落 → 两端一致；**任务闭环现在通到哪、哪里是断的** | 时序图、现状图 |
| [`03-程序集与模块依赖.md`](03-程序集与模块依赖.md) | Unity asmdef 依赖 + 服务端"源码 Compile Include"复用 + 引用不传递 | 依赖图 ×2 |

产物在 `out\`（6 张 SVG，**是生成物**，别手改）：`out\01-...-1.svg` 这种命名 = 《第几篇》-《md 里第几个 mermaid 块》。

---

## 二、怎么改图（**只改 md**）

```powershell
# 1) 改 Docs\图\*.md 里的 ```mermaid 代码块（它就是唯一真源）
# 2) 重新渲染 + 回读校验
pwsh -File Docs\图\render.ps1
```

`render.ps1` 会：把每个 md 里的 mermaid 块抠出来 → 逐块渲成 `out\<篇名>-<序号>.svg` →
**用 mmdc 自己的退出码**判断成功失败（成功 0、语法错误 1）→ 有任何一块失败就退出码 1。

> ⚠️ **别手工渲一遍然后只改 md** —— 图与 md 会**静默漂移**（本项目最恨这个）。
> 改图只改 md，然后跑脚本。

GitHub 会**直接渲染 md 里的 mermaid**，所以线上看图不需要 `out\`；
`out\` 是给"不能渲染 mermaid 的地方"用的（离线、PPT、发给面试官）。

---

## 三、渲染器怎么装（一次性，**装在仓库外面**）

```powershell
$env:PUPPETEER_SKIP_DOWNLOAD = "true"          # 跳过下载 Chromium，直接用本机浏览器
mkdir C:\TEMP\mmdc; cd C:\TEMP\mmdc
'{"name":"mmdc-local","private":true}' | Set-Content -Encoding ascii package.json
npm install @mermaid-js/mermaid-cli --no-audit --no-fund

# 告诉 puppeteer 用哪个浏览器（本机 Chrome/Edge 都行）
'{"executablePath":"C:/Program Files/Google/Chrome/Application/chrome.exe","args":["--no-sandbox","--disable-gpu"]}' |
    Set-Content -Encoding ascii puppeteer.json
```

`render.ps1` 默认就去 `C:\TEMP\mmdc\` 找，也可以用 `-MmdcPath` / `-PuppeteerConfig` 指定别处。

---

## 四、⚠️ 五个已经踩过的坑（都落成了规则/工具）

### 1. 含中文的 `.ps1` **必须存成 UTF-8 带 BOM**

这台机器上的 shell 是 **Windows PowerShell 5.1**（`$PSVersionTable.PSVersion` = 5.1，Desktop 版）。
它读**无 BOM** 的脚本文件时按**系统 ANSI（本机 = GBK）**解码 ⇒ 中文字符串字面量**当场变乱码**：

| 症状 | 后果 |
| --- | --- |
| `Write-Output "中文"` 打出 `[娉ㄦ剰]` 这种 | 轻则输出不可读（我一开始误判成"控制台编码问题"，其实是**脚本被按 GBK 读了**） |
| 乱码字节凑出了 `?` `[` 之类的记号 | 重则**直接解析失败**：`Array index expression is missing or not valid.` |

**规则**：脚本里要写中文 → 用 `[System.IO.File]::WriteAllText(路径, 文本, [System.Text.UTF8Encoding]::new($true))`
写成**带 BOM**；仓库里既有的 `Tools\Check-DocLinks.ps1` 就是这么存的。
⚠️ 这条**不是新规则**：`Docs\00-项目工作规则.md` 的 **W7 / W7b / W7c** 早就写了
（"含非 ASCII 的 `.ps1` 必须 UTF-8 with BOM"、"用编辑器改过带 BOM 的脚本后必须补回 BOM"）。
我这次的问题是**没去套用它**，而且旧自检只扫 `Tools\*.ps1`、漏掉了 `Docs\图\` ——
所以补的是**检查覆盖范围**（`Tools\Check-ScriptEncoding.ps1` 扫全仓库 + 走真实 `Parser::ParseFile`），不是新规则。

### 2. 别自己写"语法检查"正则，用工具自己的判定

`render.ps1` 第一版里我拿 `error-icon` 当"渲染失败"的判据 —— 而 mermaid **每一张图都会**输出
`.error-icon{...}` 这段**默认 CSS** ⇒ 六张图**全被误判成语法错误**。
改成读 **mmdc 的退出码**（实测：正常 0、语法错误 1）之后 6/6 全绿。

### 3. ⚠️ 脚本要能**并发跑**（2026-09-26 两位子代理同时跑时暴露）

`render.ps1` 原来有两处"只适合一个人用"的写法，两个进程一起跑就会**偶发假失败**
（其中一方退出码 1、且连汇总表格都没打出来）：

| 缺陷 | 为什么会假失败 | 修法 |
| --- | --- | --- |
| 临时目录固定为 `%TEMP%\nbc_mermaid` | 两边互相覆写 `.mmd` / `.svg` / `.log` | 改成 `%TEMP%\nbc_mermaid_<PID>`（按进程隔离） |
| **开局**就把 `out\*.svg` 全删 | 一方正在渲染，另一方把它的产物删掉 → "退出码 0 但没有产物" | 改成**跑完再清**：先算本次应有的产物清单，再删清单外的旧 SVG |

> 📌 顺带一条判据：**"偶发失败"优先怀疑共享资源**（固定临时目录、固定文件、固定端口），
> 而不是先怀疑渲染器。这次真凶就在脚本自己那两行里。


> 教训和 M2 那次一样：**真实检查与工具的真实判定必须走同一个来源**，自己另写一套迟早不一致。

### 4. ⚠️ 画 mermaid 时最容易踩的**语法**坑（都是实测撞出来的）

| 坑 | 症状 | 正确写法 |
| --- | --- | --- |
| **`classDiagram` 的关系标签里出现第二个 `:`** | `Foo ..> Bar : 泛型约束 where T : Bar` → `Parse error on line N`（2026-09-26 子代理在 C4 撞到） | 标签里只用中文说明，别把类型/`where` 塞进去：`Foo ..> Bar : 泛型约束 T 必须是 Bar` |
| **在 `classDiagram` 的成员行里写中文注释** | 偶发解析失败（成员行的语法比 `flowchart` 严得多） | 成员只写 `+方法名(参数) 返回类型`，中文说明放正文或关系标签 |
| **`flowchart` 标签里用半角引号 / 未加引号的中文括号** | 标签被截断或整块报错 | 标签一律用 `["…"]` 包起来；引号用「」 |
| **子图里的 `direction` 不生效** | 明明写了 `direction TB`，节点还是排成一行/一列 | mermaid 在"子图与外部有连线"时会忽略子图方向 ⇒ **别指望它排版，改结构**（本项目实测过四种版式） |

> 📌 判据一条：**画完就跑 `render.ps1`**。它是唯一能当场告诉你"这块到底能不能渲染"的东西
> —— 而且现在遇到语法错也**不会**整批终止了（见 §四.5），会点名是哪一块、原文是什么、出错的是哪一行源码。

### 5. ⚠️ 一个坏输入**不该**让整批校验失去报告能力（2026-09-26 修正）

`render.ps1` 原来把 mmdc 调用直接写在 `$ErrorActionPreference = "Stop"` 的作用域里，
而 Windows PowerShell 5.1 会把**原生程序写到 stderr 的内容**包成 ErrorRecord ⇒
mmdc 一报语法错，脚本就在那一块**当场终止**：既看不到是哪块坏的，也**打不出汇总表**。
（子代理实测"跑到第 19 块终止、连表都没打"；**我最初还把它误判成并发问题**。）

修法：把 mmdc 调用包成 `Invoke-Mmdc`，**只在这一次调用期间**把 EAP 设回 `Continue`，
让失败变成可记录的数据；失败信息改成抓 **mmdc 自己那行** `Error: Parse error on line N:`
并带上**出错的源码片段**。复现用例：一个"好块 + 坏块"的 md，跑 `-ExtraDirs <目录>`
→ 好块 OK、坏块被点名、汇总表正常打出、退出码 1。

---

## 五、为什么用 Mermaid，而不是 Visio（2026-09-26 的实测结论）

负责人问过能不能用他机器上的 **Visio 2010** 画图。实测结论：

| 结论 | 证据 |
| --- | --- |
| **技术上能驱动** | COM 拉起 `Visio.Application`，进程确实是 `D:\SOFT\Visio2010\Office14\VISIO.EXE`（Version 14.0 / Build 6022）；画了矩形与带箭头连线，并存出 `probe.vsd`（16384 B，文件头 `D0 CF 11 E0` = 真正的 OLE 复合文档） |
| **但 2010 版是坏的** | 它的窗口标题就是「**产品激活失败**」；`Page.Export()` 导出 PNG 时**直接卡死**（不报错），只能杀进程 |
| **而且 COM 入口被它抢了** | 注册表 `Visio.Application → Office14\VISIO.EXE`；机器上另有**订阅版 Visio 2024**（`VisioPro2024Retail`），但 ProgID 解析不到它 |
| **所以换成 Mermaid** | ① GitHub 原生渲染 ② 文本真源、**能 diff** ③ 一条命令可重渲染 ④ `.vsd` 在 GitHub **不能预览、不能 diff**，面试官也打不开 |

> 📌 这段留着有用：以后要再尝试 Visio（激活修好、或改用 2024 版），先回来看这三个坑，
> 别再从"能不能调起来"开始试。
