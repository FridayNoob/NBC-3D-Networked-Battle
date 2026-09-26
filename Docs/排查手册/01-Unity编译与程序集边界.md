# 01 · Unity 编译与程序集边界（CS0246 / CS0012 / asmdef / 批处理编译）

> 现象归类：**第 ② 层（编译 / 程序集边界）**
> 本篇全部来自本项目实测（2026-09-20 ~ 09-23），不是抄文档。

---

## 一、现象

三种最常见的形状：

| 报错 | 长什么样 | **它告不告诉你缺什么** |
| --- | --- | --- |
| `CS0246` | `未能找到类型或命名空间名"Font"` | ❌ **不告诉**。只说"找不到"，不说缺哪个程序集 |
| `CS0234` | `命名空间"UnityEngine"中不存在类型或命名空间名"Input"` | ❌ 不告诉 |
| `CS0012` | `类型"GlyphRenderMode"在未引用的程序集中定义。**必须添加对程序集"UnityEngine.TextCoreFontEngineModule"的引用**` | ✅ **点名了** |

---

## 二、先确认是哪一层

按顺序问自己三个问题（每个问题都能用一个命令/一次点击回答）：

1. **是"编译不过"还是"运行不对"？** —— 编译不过 → 本篇；运行不对 → 转第 ③④⑤ 层。
2. **是我这台机器的代码问题，还是程序集边界问题？** —— 本项目有一个**编译闸门**
   （`Server\_api-probe\ApiProbe.csproj`，把 `_Project` 下的源码全编进**一个**程序集）。
   **闸门绿 + Unity 红 ⇒ 一定是程序集边界问题**（闸门看不到边界）。
3. **Unity 的 Console 里报错点名的程序集/进程是什么？** —— 有名字就照着加引用，别猜。

---

## 三、定位步骤（照着做）

### 情况 A：`CS0012`（点名了程序集）

```
1. 读报错原文，抄下它点名的程序集名（例如 UnityEngine.TextCoreFontEngineModule）
2. 在 Unity 安装目录里确认这个 DLL 真的在：
      D:\SOFT\Unity\Hub\Editor\<版本>\Editor\Data\Managed\UnityEngine\<名字>.dll
3. Unity 侧：**通常不用改 asmdef**（Unity 会自动引用所有引擎模块）
   闸门侧：往 ApiProbe.csproj 加一条 <Reference Include="..."><HintPath>...</HintPath></Reference>
4. 重新编译，看是否变成"另一个 CS0012"（那说明依赖链还有一层，继续照做）
```

> 本项目撞过 8 次，规律一句话：**Unity 引擎按模块拆程序集**；闸门是"显式列引用"，少一个就报错。

### 情况 B：`CS0246`（没点名）

```
1. 先判断这个类型**属于谁**：引擎？某个包？还是我们自己的程序集？
   · 引擎类型：去 Managed\UnityEngine\ 下搜哪个模块 DLL 里有它（用字符串表搜，见下）
   · 包类型：Library\ScriptAssemblies\<包程序集>.dll
   · 自己的程序集：看它属于哪个 asmdef 目录
2. asmdef 侧：**在"用到它的那个程序集"的 references 里加上**（注意：**引用不传递**！）
3. 闸门侧：同上加 <Reference>
```

**怎么确认一个类型在哪个 DLL 里**（实测有效）：

```powershell
# 在 Unity 的 Managed\UnityEngine 目录里按字符串搜（比翻文档可靠）
Get-ChildItem 'D:\SOFT\Unity\Hub\Editor\2022.3.62f3c1\Editor\Data\Managed\UnityEngine\*.dll' |
  ForEach-Object { $t = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::Latin1)
                   if ($t -like '*GlyphRenderMode*') { $_.Name } }
```

### 情况 C：Unity 里报"类型找不到"，但它明明在某个 asmdef 里

**九成是 asmdef 的 `references` 少了那一项**，而且**引用不传递**：
`A → B → C` 时，A **看不到** C 的类型（Unity 侧和 csproj 侧都是这样，本项目一天内撞过 3 次）。

```text
判据：`NBC.Boot` 用了 `QuestPanel.Initialize()`（它定义在基类 `BasePanel`，属于 NBC.Framework.UI）
      → Boot.asmdef 必须自己引用 NBC.Framework.UI，光引 NBC.Game 不够
```

---

## 四、根因

1. **Unity 把引擎拆成很多模块程序集**（`UnityEngine.CoreModule` / `InputLegacyModule` / `AudioModule` / `TextRenderingModule` / `TextCoreFontEngineModule` …），**编译闸门只列它认识的** → 少列就红。
2. **asmdef 的引用不传递**（这是设计，不是 bug）：每个程序集必须显式引用它**直接用到**的类型所在的程序集。
3. **闸门把一切编成一个程序集**，所以它**永远看不到**边界问题 —— 这就是"闸门绿 ≠ Unity 绿"。

---

## 五、解决方案（本项目已落地的四道闸门）

| 闸门 | 作用 | 位置 |
| --- | --- | --- |
| **① 编译闸门**（ApiProbe） | 不开 Unity 就能验"C# 语法/API 是否存在" | `Server\_api-probe\ApiProbe.csproj`（不进 Git，HintPath 指本机 Unity） |
| **② asmdef 引用守卫**（EditMode 用例） | 扫每个 asmdef 的 `references` 与源码 `using`，对不上就红 | `Tests\EditMode\Framework\AsmdefReferenceGuardTests.cs` |
| **③ Unity 批处理编译**（2026-09-23 起，负责人特许） | **真的让 Unity 编一遍**，把边界问题一次抓完 | 见下节命令 |
| **④ IL2CPP 验证清单** | 打包后才暴露的问题 | `Docs\09-IL2CPP验证清单.md` |

### ⭐ Unity 批处理编译（**不开编辑器界面**，命令行编译 + 跑测试）

```powershell
# 前提：**先关掉 Unity 编辑器**（同一个工程不能开两个实例，Client\Temp\UnityLockfile 会挡住）

# A. 只编译（导入 + 编译 + 退出；报错全在日志里）
& 'D:\SOFT\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe' `
    -batchmode -nographics -quit `
    -projectPath 'E:\U3D Projects\0_MyFile\3D联网战斗Demo\Client' `
    -logFile 'E:\U3D Projects\0_MyFile\3D联网战斗Demo\Client\Logs\unity-batch-compile.log'

# 然后只看关键行（别通读几万行日志）
Select-String -Path 'Client\Logs\unity-batch-compile.log' -Pattern 'error CS|Scripts have compiler errors|Compilation failed' 
```

```powershell
# B. 跑 EditMode 用例（把 647 条在命令行里跑完，出 NUnit 结果文件）
& 'D:\SOFT\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe' `
    -batchmode -nographics -quit `
    -projectPath 'E:\U3D Projects\0_MyFile\3D联网战斗Demo\Client' `
    -runTests -testPlatform EditMode `
    -testResults 'E:\U3D Projects\0_MyFile\3D联网战斗Demo\Client\Logs\editmode-results.xml' `
    -logFile 'E:\U3D Projects\0_MyFile\3D联网战斗Demo\Client\Logs\unity-batch-tests.log'
```

**⚠️ 注意事项（实测/预判）**：

| 注意 | 说明 |
| --- | --- |
| 必须**先关编辑器** | 否则会以"工程已被占用"退出，或两个实例抢 `Library\` |
| **另一个办法：用克隆工程** | ParrelSync 的 `Client_clone_0` 里 `Assets` 是**符号链接**（指向真 `Assets`）→ 在克隆上跑编译**不改动你的编辑器**、也不用你关 Unity。代价：克隆有自己的 `Library`，首次要全量导入（慢） |
| `-nographics` | 只编译/跑逻辑测试时用它；**需要渲染的用例**（截图、Shader）别加这个参数 |
| 日志会很大 | 别 `Get-Content` 整份；用 `Select-String` 过滤（本项目 W3a：`Get-Content` 行数都不可信） |
| 首次可能很慢 | 冷启动 + 导入几分钟是正常的，别当成卡死 |

---

## 六、防复发（已落地）

1. `AsmdefReferenceGuardTests`：改了 asmdef 就会有一条用例盯着（含阳性/阴性对照）。
2. `ApiProbe` 的关键引用处都写了**"这是第几次撞同一条规律"**的注释（现在是第 8 次），下次加引用时能直接抄。
3. **规则**（`Docs\00`）：**改 asmdef、或跨程序集使用类型时，必须有一次 Unity 编译兜底**；闸门只能证明"API 与语法没问题"。

---

## 七、一句话讲清（面试版）

> 编译类问题我只做两件事：**先读报错原文**（`CS0012` 会点名程序集，`CS0246` 不会），
> **再判断是"代码问题"还是"程序集边界问题"** —— 我们有一个把全部源码编进单程序集的编译闸门，
> 所以"闸门绿、Unity 红"就一定是**边界**问题，而 Unity 的 asmdef **引用不传递**，
> 每个程序集必须显式引用它直接用到的东西。现在我还能用 `Unity -batchmode` 真编一遍来兜底。
