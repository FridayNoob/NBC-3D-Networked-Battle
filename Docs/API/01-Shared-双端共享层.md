# 01 · `NBC.Shared` —— 双端共享层

> **对应源码**：`Client\Assets\_Project\Shared\`
> **对应程序集**：`NBC.Shared.asmdef` / `Server\NBC.Shared\NBC.Shared.csproj`（**同一份源码**）
> **对应测试**：`Server\_condition-probe`（47 全绿）、`Client\Assets\_Project\Tests\EditMode\`（NUnit）
> **对应教学**：`Docs\教学\D1-双端共享层.md`、`D2-定点数学.md`

---

## 一、这一层是什么，为什么它最重要

**同一份 `.cs` 源码，Unity 与 .NET 8 各编一遍。**

```
Client\Assets\_Project\Shared\  ← 唯一源码（这里没有第二份拷贝）
        ├── NBC.Shared.asmdef        → Unity 用它
        └── NBC.Shared.csproj        → 服务端用它（<Compile Include> 指向上面那个目录）
```

⚠️ **为什么"拷贝两份"是不行的**：两份一定会漂移，而**漂移不报错** ——
表现是"客户端算 120 伤害、服务端算 118"，玩家只觉得"血条偶尔不对"。
帧同步里这种偏差会**逐步放大**，最后两边世界完全不同。

📌 **所以本层的第一判据是**：**"两端都必须算出同一个结果的东西，才放这里。"**
不属于这条的（UI、资源、Unity 类型）一律不许进来。

---

## 二、⚠️ 本层的三条硬约束（改代码前必读）

| 约束 | 谁在保证 | 违反了会怎样 |
| --- | --- | --- |
| **不许引 UnityEngine** | `NBC.Shared.asmdef` 的 `noEngineReferences: true` + `Tools\Check-AsmdefBoundaries.ps1`（一个引擎引用都不给） | 服务端编不过；或更糟 —— 服务端为了编过而**偷偷引了 UnityEngine**，那它就再也跑不了纯 .NET |
| **只许用 C# 9** | `NBC.Shared.csproj` 的 `<LangVersion>9.0</LangVersion>` | 服务端能编、**Unity 编不过**（Unity 2022.3 只到 C# 9）。⚠️ 历史上真发生过：`SharedInfo.cs` 用过 C# 10 的文件作用域命名空间 |
| **不许引第三方** | 同上（没有 `PackageReference`） | Unity 侧要跟着拖依赖；而且 `#nullable disable` + netstandard2.1 的组合会被打破 |

### 还有一条容易忽略的：`#nullable disable`

本层每个文件顶部都有：

```csharp
#nullable disable
```

**为什么**：同一份源码，Unity 侧**没开**可空引用类型，服务端侧**开了**
（`Server\Directory.Build.props` 里 `<Nullable>enable</Nullable>`）。不关掉会**两头都不干净**：

- 服务端多出一批 `CS86xx`（事件没初始化、局部变量赋 null…）
- 而为了消警告去写 `string?` ⇒ **Unity 侧**报 `CS8632`（可空注解出现在未开启可空上下文的文件里）

⇒ 这是**把环境差异钉死在一处**，不是"关掉检查图省事"。代价如实记：本层放弃可空静态检查，
所以"可能为 null"要靠 **XML 注释 + 运行期校验**（本层的类两个都做了）。

---

## 三、公开 API（按"你多半要碰的顺序"排）

### 3.1 `Condition` —— 条件系统（任务与成就的共同底座）

**一句话**：有人告诉它"发生了一件事" → 它把相关的条件往前推 → **恰好跨过需求线的那一次**回调一次。

**它不知道**进度是任务的还是成就的，也不知道达成之后要发奖还是弹窗 —— 那些在回调的另一头。

```csharp
// Shared\Condition\ConditionTracker.cs
public sealed class ConditionTracker
{
    public ConditionTracker(IConditionProgressStore store);          // 进度存哪

    public event Action<int, ConditionProgress> ConditionMet;        // "满了"（一条只发一次）
    public event Action<int, ConditionProgress> ProgressChanged;     // "2/3 了"（每次推进都发）

    public void Register(int conditionKey, ConditionDef def, bool resetProgress);
    public bool Unregister(int conditionKey);                        // ⚠️ 只注销登记，**不动进度**
    public bool IsRegistered(int conditionKey);
    public bool IsMet(int conditionKey);
    public bool TryGetProgress(int conditionKey, out ConditionProgress progress);

    public int Notify(EConditionEvent eventType, int targetId, int count);  // 返回"这一次让几条达成"
    public void Clear();                                             // 清登记，**不动进度**
    public string Describe();                                        // 调试面板用
}
```

**四条必须记住的语义**（都写在 `ConditionTracker.cs` 文件头，这里只摘结论）：

| # | 语义 | 为什么 |
| --- | --- | --- |
| ① | **进度是钳位的** ⇒ 达成**只回调一次**，不需要"已达成"标志位 | 少一个状态就少一处能不同步的地方 |
| ② | **`Register(..., resetProgress)` 那个 bool 就是"任务"与"成就"的唯一差别**<br>任务 `true`（接了才计数）／成就 `false`（一直生效，**且注册时已达成会当场回调**） | 这条对"上次已经打了 10 只、这次登录就该解锁"是**必须**的，否则玩家要**再打一只**才解锁（看起来像玄学） |
| ③ | **一次 `Notify` 里，条件只在"派发开始那一刻"的登记表里找**<br>回调里新登记的条件**不会**被这次事件影响 | 回调里可能接下一个任务，而"刚接的任务被上一只怪计数"玩家完全无法理解 |
| ④ | **先发全部 `ProgressChanged`，再发全部 `ConditionMet`** | 反过来的话 UI 会先收到"任务完成"再收到"2/3"，显示上闪一下 |

⚠️ **②的副作用是个真坑**（`Docs\27` §10.2 有完整复盘）：
`resetProgress: false` 的 `Register` 会**当场重入**你的回调。
⇒ 如果构造函数里写成"登记一条 → 记一条归属"，回调查归属表时**还没记进去**
⇒ 认不出自己的条件 ⇒ 安静 `return` ⇒ **成就永远不解锁，而且一行日志都没有**。
**正确写法是三段**：① 先记**全部**归属 ② 再逐条登记 ③ 全部登记完**统一评一遍**。

**条件定义**：

```csharp
// Shared\Condition\ConditionDef.cs
public sealed class ConditionDef
{
    public ConditionDef(EConditionEvent eventType, int targetId, int requiredCount);
    public EConditionEvent EventType { get; }
    public int TargetId { get; }          // 0 = "任意目标"
    public int RequiredCount { get; }
    public bool IsAnyTarget { get; }

    public static string Validate(EConditionEvent eventType, int targetId, int requiredCount);  // null = 合法
    public static bool TryCreate(EConditionEvent eventType, int targetId, int requiredCount,
                                 out ConditionDef def, out string error);
    public bool Matches(EConditionEvent eventType, int targetId);
    public string Describe();             // 人话，给 UI/日志用
}
```

⚠️ **`targetId = 0` 表示"任意目标"** —— 它**不在任何副本的怪列表里**，
所以"跨表校验"必须**跳过**它（`CFG0020` 的第一版就栽在这上面，见 `Tools\ConfigKit\README.md` §11.2）。

**进度存放处（接缝）**：

```csharp
// Shared\Condition\IConditionProgressStore.cs
public interface IConditionProgressStore
{
    int GetProgress(int conditionKey);              // ⚠️ 读不到必须给 0，**不是异常**
    void SetProgress(int conditionKey, int value);
    bool Remove(int conditionKey);
}
```

⚠️ **接口刻意只有三个方法、只用 `int`**，而且**没有 `playerId` 参数** ——
"归属"靠**构造时绑定**（一个实例对应一个玩家）。
⇒ 好处：`ConditionTracker` **一行都不用改**就能落库；也正因为这样，
它既能被客户端的 `InMemoryConditionProgressStore` 实现，也能被服务端的
`CachingConditionProgressStore`（`NBC.Server.Data`）实现。

**事件类型**：`Shared\Condition\EConditionEvent.cs`（`KillMonster` / `CollectItem` / `UseSkill` / `ReachArea`）。
⚠️ 每种事件的**事件源**在配置策略里有一张表（`ConfigPolicy.EventTypesWithoutSource`）——
"数据里用了但没有事件源"的那种条件**永远不会涨进度**，跨表检查会给**警告**（CFG0022）。

---

### 3.2 `Reward` —— 已发奖励台账（**防"重启重复发奖"**）

```csharp
// Shared\Reward\IRewardLedger.cs
public enum ERewardOwnerKind { Quest = 1, Achievement = 2 }   // ⚠️ 与 DB 的 `owner_kind` 一一对应

public interface IRewardLedger
{
    bool HasGranted(ERewardOwnerKind kind, int ownerId);
    void MarkGranted(ERewardOwnerKind kind, int ownerId, int rewardId);   // ⚠️ 必须幂等
}
```

**为什么需要它**（`Docs\27` §11.5 完整复盘）：
成就用 `resetProgress: false` 登记条件 ⇒ 条件系统"注册时若已达成**当场回调**"
⇒ 进度一旦落库，**服务端每重启一次就会把所有已完成的成就再发一遍奖**。

📌 记住这个区分：
- **"解锁了吗"** = 全部条件是否都 ≥ 需求 → **能从进度推导**
- **"发过奖吗"** = 一个**已经发生过的动作** → **推导不出来，必须记账**

> 这是"**状态**"与"**事件**"的区别（同 `M3-B`：**移动是状态、动作是事件**）。

⚠️ **本层只放接缝与内存实现**（`InMemoryRewardLedger`）。
持久化实现在 `NBC.Server.Data\MySqlRewardLedger.cs` —— 它**必须**和
`CachingConditionProgressStore` **成对使用**，只上一个是半成品，而且表现不同：

| 只上了 | 表现 |
| --- | --- |
| 只上进度 | 重启后条件满 ⇒ **重复发奖** |
| 只上台账 | 重启后条件回到 0 ⇒ **成就不解锁**（玩家觉得"我明明打够了"） |

---

### 3.3 `Battle` —— 战斗数值（**必须两端同一个数**）

```csharp
// Shared\Battle\DamageMath.cs
public static DamageOutcome DamageMath.Resolve(int currentHp, int damage);

// Shared\Battle\BattleRules.cs
public static class BattleRules
{
    public const int BasicAttackRangeMm     = 2000;   // ⚠️ 射程（毫米）
    public const int BasicAttackCooldownTicks = 15;   // ⚠️ 冷却（逻辑帧，30Hz ⇒ 0.5 秒）
    public static string Describe();
}
```

⚠️ **这两个常量是"两端同一个来源"的示例**：
服务端 `DungeonBattle` 与客户端调试窗口都**别名**到它，
**不许各自再写一遍**（各写一遍 = 迟早一个改了一个没改）。

伤害规则三条（`Docs\教学\M2-B` 讲透了，这里只列结论）：
**HP 钳 0** / **过量伤害单独算** / **打 0 血不判致死**（否则击杀数会凭空多）。

---

### 3.4 `Net` —— 分帧与协议常量

```csharp
// Shared\Net\FrameCodec.cs
public static byte[] FrameCodec.Encode(ReadOnlySpan<byte> payload);   // [4 字节小端长度][载荷]
public static int    FrameCodec.ReadLength(byte[] buffer, int offset);

public sealed class FrameDecoder            // 解决 TCP 粘包/拆包
{
    public void Append(byte[] buffer, int offset, int count);
    public bool TryDequeue(out byte[] frame, out string error);   // ⚠️ 违规要**说明原因**，不能只给 false
    public void Reset();
}

// Shared\Net\NetContract.cs
public static class NetContract
{
    public const int Version      = 1;
    public const int TickRate     = 30;
    public const int TickIntervalMs = 1000 / TickRate;
    public const int MaxRoomMembers = 4;
    public const int FrameLengthPrefixBytes = 4;
    public const int MaxFrameBytes = 1024 * 512;
    public static string Describe();
}
```

⚠️ **长度前缀是 4 字节小端**，而 `Append`/`ReadLength` **手写位移**，不用 `BitConverter` ——
理由是"两端 + 大小端可预测"（`Docs\教学\M3-A`）。

⚠️ **`MaxFrameBytes` 是防御**：超长帧要**判协议违规并断开**，
否则对方发一个"长度 = 4GB"的头，你就照着攒内存了。

---

### 3.5 定点数与其它

| 类型 | 位置 | 用途 |
| --- | --- | --- |
| `Fix64` / `FixMath` / `FixVector3` | `Shared\Fix64.cs` 等 | **帧同步的确定性数学**。⚠️ `float` 的 `Sin`/`Sqrt` 在不同运行时实现可以不同，一次不同之后**每一步都会放大** |
| `SharedInfo` | `Shared\SharedInfo.cs` | 两端共享的常量（`LogicTickRate` 必须与 `appsettings.json` 的 `Simulation.LogicTickRate` 一致） |
| `NetErrors` | `Shared\Net\NetErrors.cs` | 错误码/文案 |

---

## 四、⚠️ 改这一层之前，先想清楚这四件事

1. **"这东西两端都必须算出同一个结果吗？"** —— 不是 ⇒ **别放这里**。
2. **有没有引入 C# 10+ 语法 / `float` / `System.Random` / 字典遍历顺序依赖？**
   任何一个"是"都会在**帧同步里**变成"偶发地不同步"。
3. **加了公开类型之后，消费方要不要加引用？**
   ⚠️ **`using` 里没提到它、它也可能通过"被调方法的签名"泄漏进去**，
   而 **asmdef 引用不传递** —— 这就是 `CS0012` 最常见的一种成因。
   ⇒ 改完**必须**跑 `Tools\Check-AsmdefBoundaries.ps1`（单程序集闸门抓不到这类错）。
4. **改了 `NetContract` 里的常量吗？** —— 那是**协议契约**，要按 §3.4 的版本策略走。

---

## 五、怎么验（四条命令）

```powershell
# ① 双端共享逻辑真跑一遍（条件系统 47 条；不需要 Unity、不需要数据库）
dotnet build Server\_condition-probe\ConditionProbe.csproj -m:1
Server\_condition-probe\bin\Debug\net8.0\NBC.ConditionProbe.exe

# ② asmdef 边界（⚠️ 抓 CS0012/CS0246 这类"单程序集闸门永远抓不到"的错）
powershell -ExecutionPolicy Bypass -File Tools\Check-AsmdefBoundaries.ps1

# ③ 语法/API 闸门（C# 9 + 本机 Unity 程序集）
dotnet build Server\_api-probe\ApiProbe.csproj -m:1

# ④ Unity 侧（EditMode 测试，需在 Unity 里跑）
#    Window > General > Test Runner > EditMode > Run All
```

> 📌 **①②的关系必须理解**：`_api-probe` 把所有源码编进**一个**程序集，
> 所以它**没有"程序集边界"这回事** —— 它的"绿"**不代表** Unity 能编过。
> 这就是 `Tools\Check-AsmdefBoundaries.ps1` 存在的唯一理由。
