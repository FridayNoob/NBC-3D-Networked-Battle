# 02 · 框架层（`NBC.Framework` / `.UI` / `.Net` / `.YooAsset`）

> **对应源码**：`Client\Assets\_Project\Framework\`、`Framework.UI\`、`Framework.Net\`、`Framework.YooAsset\`
> **对应程序集**：`Framework.asmdef`、`NBC.Framework.UI.asmdef`、`NBC.Framework.Net.asmdef`、`NBC.Framework.YooAsset.asmdef`
> **对应测试**：`Tests\EditMode\Framework\`（NUnit）
> **对应教学**：`Docs\教学\A*.md`（A1 单例 / A2 对象池 / A3 事件中心 / A4 Mono 宿主 / A5 场景加载 /
> A7 输入 / A8 音频 / A9 UI / A10 状态机）、`B2-B3-资源层.md`、`E1-性能看板.md`、`E2-日志系统.md`

---

## 一、这一层是什么

**与玩法无关、可以整个搬去下一个项目的东西。**

```
Framework\            纯 C# + 少量 MonoBehaviour（A1~A10）
Framework.UI\         UI 框架（层级 / 面板基类 / 加载遮罩 / 性能 HUD）
Framework.Net\        网络接缝（ITransport）+ TCP 适配 + 网络模拟器
Framework.YooAsset\   资源层的**适配实现**（唯一认识 YooAsset 的地方）
```

⚠️ **依赖方向是单向的**：`Framework` **不认识**任何业务类型（`Game\` 里的东西一个都不许引）。
`NBC.Server.Data` 之类更不认识它。这不是自觉，是 asmdef 写死的。

---

## 二、各子模块：一句话 + 关键类型 + **那条不能违反的规矩**

### 2.1 单例 —— `Singleton.cs` / `SingletonMono.cs` / `SingletonAutoMono.cs`

| 类型 | 用途 |
| --- | --- |
| `Singleton<T>` | 纯 C# 单例（`where T : new()`） |
| `SingletonMono<T>` | 需要挂场景的 MonoBehaviour 单例 |
| `SingletonAutoMono<T>` | 自动创建承载物体的 MonoBehaviour 单例 |

⚠️ **坑（`Docs\教学\A1`）**：`where T : new()` 与"禁止外部 `new`"**天生矛盾**。
另外一条副产品发现：**EditMode 下 Unity 根本不调用 `Awake`** —— 所以测试里不指望它。

### 2.2 事件中心 —— `EventCenter.cs` / `EventId.cs` / `EventChannelStat.cs`

```csharp
EventId id = EventId.Declare("Quest.Accepted");        // 编译期常量，不是字符串
EventCenter.Instance.AddEventListener<int>(id, OnAccepted);   // ⚠️ 泛型参数必须显式给
EventCenter.Instance.Trigger(id, 3001);
EventCenter.Instance.RemoveEventListener<int>(id, OnAccepted);
```

⚠️ **三条必须记住的**：

| 规矩 | 为什么 |
| --- | --- |
| **事件名用 `EventId`，不许用 `string`** | 原框架用字符串当事件名，**拼错一个字符就静默不触发**（FW-06）。改成 `EventId` 后，引用不存在的常量是**编译错误** |
| **`AddEventListener` 要写显式泛型参数** | 非泛型重载会和泛型重载抢，而"方法组"不参与泛型推断 ⇒ 报"无法从方法组转换为 `UnityAction`" |
| **派发中增删监听要有明确语义**，且与 C# 原生 `event` 一致 | 否则回调里退订自己就会炸（M1 踩过 `Collection was modified`） |

📌 **约定**：**每个模块在自己的文件里声明自己的事件**（`QuestEvents` / `AchievementEvents` / `BattleEvents` / `WorldEvents` / `SceneEvents`）——
这样两个人分别做两个模块，不会因为"都要往同一个 enum 里加一行"而天天冲突。

### 2.3 对象池 —— `ObjectPool.cs` / `GameObjectPool.cs` / `PoolRegistry.cs` / `PoolStats.cs`

⚠️ **判据（`Docs\教学\A2`）**：**给数字，不给容器。**
对外暴露的是 `PoolStats`（快照：借了多少、还了多少、峰值），
而不是把内部容器递出去 —— 否则用的人迟早会去改它。

### 2.4 Mono 宿主 —— `MonoManager.cs`

用一个 MonoBehaviour 给一堆**纯 C# 对象**"每帧能力"（`Update` / 协程 / 定时器 `TimerHandle`）。

⚠️ **"记得退订"要靠屏幕上的数字，不能靠自觉**（A4 的结论）——
所以有了 `EventChannelStat`（每个事件的监听数）与性能看板。

### 2.5 场景加载 —— `SceneLoader.cs` / `SceneEvents.cs`

```csharp
SceneLoadRequest req = ...;
// 进度与失败都走事件：SceneEvents.Progress / SceneEvents.Failed
```

⚠️ **坑（`Docs\教学\A5`）**：`yield return ao.progress` **不是等待**！
`float` 不在 Unity 协程能识别的那 7 种等待指令里 ⇒ 它会立刻继续。
⇒ 这一层的做法是**把时间从 Unity 手里拿回来**（自己控制进度语义）。

### 2.6 状态机 —— `StateMachine.cs` / `HierarchicalStateMachine.cs` / `IState.cs` / `StateBase.cs`

⚠️ **判据（`Docs\教学\A10`）**：把"**现在处于什么状态**"变成显式的值，
把"**能不能换**"变成集中检查的规则。纯 C#、帧驱动（`StateTick`）、层级约束。

⚠️ **一个真实的间接依赖**：`StateBase` 经 `EventId` **间接依赖 UnityEngine** ——
所以"纯 C#"要看**整条依赖链**，不能只看这个文件引了什么（`Docs\教学\M3-C` 记过这次）。

### 2.7 输入 —— `InputManager.cs` / `InputMapping.cs` / `InputTypes.cs` / `IInputSource.cs` / `LegacyInputSource.cs`

```csharp
InputActionId action = InputActionId.Declare(0, "Skill1");
mapping.Bind(action, KeyCode.J);                       // 动作 → 键 的绑定留在**实现侧**
InputCommand cmd = ...;                                // ⚠️ int，能过网线
```

⚠️ **判据（`Docs\教学\A7`）**：**`int` 能过网线，`KeyCode` 不能。**
所以"键盘"必须先变成"可传输、可重放、可测试的值"，再由 `InputCommand` 送出去。
`IInputSource` 是那道缝（测试里塞假的输入源即可，不需要真键盘）。

### 2.8 音频 —— `AudioManager.cs` / `AudioVoice.cs` / `AudioTypes.cs` / `IAudioPlaybackProbe.cs`

⚠️ **要解决的不是"怎么播声音"**，而是：**播几百次不出事、切场景不崩、音量能分开调**（`AudioBus`）。
`IAudioPlaybackProbe` 是"到底有没有真的在放"的探针接缝（测试用，不依赖真实音频设备）。

### 2.9 日志 —— `LogSystem.cs` / `ILogSink.cs` / `FileLogSink.cs` / `UnityConsoleLogSink.cs` / `LogTypes.cs`

分级（`LogLevel`）+ **按来源开关**（`LogChannel`）+ 能落盘。

⚠️ **坑（`Docs\教学\E2`）**：**依赖被缓冲过的数值做判断 = 赌它什么时候刷新。**

### 2.10 性能 —— `PerfSampler.cs` / `FpsCounter.cs` / `IPerfCounterSource.cs` / `PerfTypes.cs`

⚠️ **判据（`Docs\教学\E1`）**：**观测工具不能自己制造被观测的问题。**
看板要显示 GC 分配，它自己就不能每帧分配字符串。
另有两条：**"读不到 ≠ 是 0"**、**"平均值掩盖卡顿"**。

### 2.11 资源（接缝 + 适配）—— `AssetManager.cs` / `AssetTypes.cs` / `AssetHandle.cs` / `YooAssetProvider.cs`

```
Framework\Asset\        IAssetProvider / IAssetHandle / IAssetLoadOperation   ← 接缝（不认识 YooAsset）
Framework.YooAsset\     YooAssetProvider                                       ← 唯一认识 YooAsset 的地方
```

⚠️ **判据（`Docs\教学\B2-B3`）**：**依赖倒置**。框架侧一个 YooAsset 类型都不认识。
而它真正的回报是**可测性红利**：测试里塞一个假 provider 就能验资源流程。

```csharp
AssetHandle handle = ...;              // 有 AssetHandleAwaiter ⇒ 可以直接 await
```

### 2.12 UI 框架 —— `UIManager.cs` / `BasePanel.cs` / `UILayers.cs` / `LoadingMaskController.cs`

⚠️ **判据（`Docs\教学\A9`）**：目标不是"检测到错误"，而是**让错误自己说出来**。

⚠️ **两条 UI 专属坑（`Docs\教学\M2-D`）**：
1. **动态创建的按钮不会带上模板的点击监听** ⇒ 必须由代码显式绑。
2. **重建式刷新会让所有旧引用失效** ⇒ 刷新后访问旧引用会抛 `MissingReferenceException`。

### 2.13 网络接缝 —— `ITransport.cs` / `TcpTransport.cs` / `SimulatedTransport.cs` / `NetTypes.cs`

```csharp
public interface ITransport { ... }        // 引擎无关（asmdef: noEngineReferences 为假但只引 NBC.Shared）
TcpTransport                               // 真 TCP
SimulatedTransport + NetSimProfile         // ⚠️ 网络模拟器：延迟/抖动/丢包
```

⚠️ **`SimulatedTransport` 不是玩具**：它是"**网络变差还成立吗**"的唯一验法
（只在完美网络上验过的联网代码，上了真机全是问题 —— `Docs\教学\M3-C`）。
另外 `Framework.Net` 是**引擎无关**的，所以 `Server\_net-probe` 能在纯 .NET 里**真开端口跑**。

---

## 三、怎么验

```powershell
# ① UI 无关的框架逻辑：EditMode 测试（需在 Unity 里跑）
#    Window > General > Test Runner > EditMode > Run All

# ② 网络接缝与分帧（真 socket，纯 .NET，不需要 Unity）
Server\_net-probe\bin\Debug\net8.0\NBC.NetProbe.exe      # 93 全绿

# ③ 语法/API 闸门 + asmdef 边界（⚠️ 后者才能抓 CS0012）
dotnet build Server\_api-probe\ApiProbe.csproj -m:1
powershell -ExecutionPolicy Bypass -File Tools\Check-AsmdefBoundaries.ps1
```

---

## 四、改这一层之前

1. **它是"可以整个搬走"的东西吗？** —— 引入了业务类型（`Game\` 里的）⇒ **放错层了**。
2. **有没有把内部容器/可变状态递出去？** —— 有 ⇒ 改成**给快照**（`PoolStats` / `PerfSnapshot` / `EventChannelStat` 都是这个形状）。
3. **新加的东西要不要"能被假实现替换"？** —— 要 ⇒ 先立**接缝**（接口），再写适配实现。
   本层的每个可测点都是这个套路（`IAssetProvider` / `IInputSource` / `IAudioPlaybackProbe` / `IPerfCounterSource` / `ITransport`）。
4. ⚠️ **改完跑 `Check-AsmdefBoundaries.ps1`** —— 本层是"被引用方"，
   加公开类型/改签名很容易让**引用方**缺程序集（`CS0012`），而那类错单程序集闸门抓不到。
