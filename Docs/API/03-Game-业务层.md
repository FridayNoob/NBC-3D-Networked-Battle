# 03 · 客户端业务层（`NBC.Game` / `NBC.Boot`）

> **对应源码**：`Client\Assets\_Project\Game\`（业务 + 配置）、`Boot\`（组合根）
> **对应程序集**：`Game.asmdef`（`NBC.Game`）、`Model\Model.asmdef`（`NBC.Model`）、`Boot\NBC.Boot.asmdef`
> **对应测试**：`Tests\EditMode\Game\`（**691 全绿**）、`Tests\EditMode\Framework\`
> **对应教学**：`Docs\教学\M2-A/B/C/D`、`M3-B`、`M4-A`、`M4-B`

---

## 一、三个程序集的分工（**这是本层最容易搞错的地方**）

| 程序集 | 目录 | 里面放什么 | 判据 |
| --- | --- | --- | --- |
| **`NBC.Model`** | `Game\Model\` | 游戏数据模型（目前基本是空的） | 与 Unity 无关的数据结构 |
| **`NBC.Game`** | `Game\`（**不含 `Model\`**，那是嵌套 asmdef） | 配置运行时 + 任务 + 成就 + 战斗 + 联机 + 装配 | 业务逻辑，**引用 `NBC.Shared`** |
| **`NBC.Boot`** | `Boot\` | **组合根**：唯一同时认识"框架接缝"与"适配实现"的地方 | ⚠️ **不许有业务判断**，只做装配 |

⚠️ **`NBC.Boot` 为什么必须独立一个程序集**：业务层有一条机械检查 ——
**`Game\` 里不许出现 `YooAsset` 字样**（`YOO-09`）。所以"把 YooAsset 塞进业务层"这条路是堵死的，
装配只能待在 `Boot\`。见 `AssetBootstrapper.cs` 的文件头。

⚠️ **⚠️ 本层最出名的一个坑（`CS0012`）**：**`NBC.Boot` 没有引用 `NBC.Shared`**。
- `Model\` 是**嵌套在 `Game\` 里面**的 asmdef ⇒ 生成闸门时必须**排除**它，否则文件集合是错的
- 而 `Boot` 只引 `NBC.Framework` / `.UI` / `.YooAsset` / `NBC.Game`
- ⇒ **一旦 `NBC.Game` 的公开方法签名里出现 `NBC.Shared` 的类型（哪怕只是可选参数的默认值），
  `NBC.Boot` 就编不过**，而 `M2DemoBehaviour.cs` 里**一个字都没提那个类型**
- ⇒ 这类错 **`_api-probe` 永远抓不到**，只能用 `Tools\Check-AsmdefBoundaries.ps1`
  （完整复盘：`Docs\27-M4开工清单.md` §十三）

---

## 二、配置运行时（`Game\Config\`）

```csharp
// 唯一入口
ConfigMgr.Instance.SetSource(new EditorConfigSource());      // 编辑器：AssetDatabase 直读
ConfigMgr.Instance.SetSource(new AssetConfigSource());       // 运行时：走 YooAsset
GameTables.PreloadAll(onDone, onFailed);                     // 启动时预加载（清单**只在这里**）
ConfigMgr.Instance.Get<HeroConfig>().Get(1001);              // 类型安全、无反射
```

| 类型 | 职责 |
| --- | --- |
| `ConfigMgr` | 缓存 + 查询（**单例**）。`Get<T>()` **同步**（启动预加载过） |
| `IConfigSource` | 「资产从哪来」的接缝（`EditorConfigSource` / `AssetConfigSource`） |
| `GameTables` | 「这个游戏要哪几张表」的**唯一清单** |
| `<表>Config` / `Config_<表>` | **ConfigKit 生成**（`Rows` + 主键索引 `Get(id)` / `TryGet`） |

⚠️ **四条必须记住的**：

| 规矩 | 为什么 |
| --- | --- |
| **`ConfigMgr.Get<T>()` 在表没加载时抛异常，且报错说清"忘了预加载"** | CFG-R4：缺表最常见的原因是忘了预加载，而不是表不存在 |
| **新加表只改 `GameTables.All` 一处** | 散在启动流程里的话，"M3 加了 `Dungeon` 表忘了改启动"**不会报错**，直到第一次用到才炸 |
| **`Dungeon` / `DropTable` 故意不在 `All` 里** | 它们是**服务端**读的（服务端直接读 CSV）。客户端拿到的是快照，不需要这两张 |
| **成就表用 `TryGet` 而不是 `Get`** | 成就是"锦上添花"，缺表不该让整局起不来；但"表没加载"**不静默**（`PreloadAll` 会先报一次，调试面板也会写在明面上） |

⚠️ **生成物 vs 真源**（踩过）：`Configs\Design\*.csv` 是**真源**；
Unity 里的 `.asset`（SO）是**生成物**。
⇒ 直接改 SO **不会**影响服务端（服务端读 CSV）。完整复盘：`Docs\排查手册\06-改了却没生效（旧产物·生成物·真源）.md`。

---

## 三、任务与成就（`Game\Quest\` + `Game\Achievement\`）

```
QuestRuntime         接取 → 进度 → 完成 → 交付 → 发奖（**玩家点两下**）
AchievementRuntime   构造即登记 → 条件一齐**当场解锁发奖**（**没有玩家动作**）
        ↓ 两者共用
ConditionTracker（在 NBC.Shared）+ IQuestRewardSink + IRewardLedger
```

**成就与任务只差三点**（`Docs\教学\M4-A`）：

| | 任务 | 成就 |
| --- | --- | --- |
| 登记时机 | 玩家点「接取」 | **构造时全部登记** |
| `resetProgress` | `true` | **`false`（跨局累计）** |
| 结束方式 | 玩家点「交付」才发奖 | 条件一齐**当场**解锁发奖 |

⇒ 所以成就**没有状态枚举**（只有"锁着 / 解锁了"两态）。

⚠️ **三条本层专属的坑**：

| 坑 | 后果 | 正确做法 |
| --- | --- | --- |
| ⚠️ **登记者顺序**：`resetProgress:false` 的 `Register` 在"已达成"时**当场回调** | 写成"登记一条→记一条归属" ⇒ 回调查不到自己 ⇒ **成就永远不解锁且无日志** | **三段**：① 先记**全部**归属 ② 再逐条登记 ③ 统一评一遍 |
| ⚠️ **重启重复发奖** | 进度落库后，每次重启把已完成的成就再发一遍 | **`IRewardLedger` 台账**（`Docs\教学\M4-B`） |
| ⚠️ **`Unlock` 里"先发奖、再记账"** | 反过来 `Grant` 抛异常 ⇒ "记了账但没发" ⇒ 玩家**永远拿不到** | 先 `Grant` 再 `MarkGranted`（最坏是"发了没记上"⇒ 下次**补发**） |

**事件**（各模块自己声明）：`QuestEvents` / `AchievementEvents` → 见 `NBC.Framework.EventId`。

⚠️ **成就的失败要广播 `Achievement.UnlockFailed`**：成就是自动解锁的，
**没有"返回值给谁看"**，所以出问题必须留痕（`Docs\教学\M4-A`）。

---

## 四、战斗与联机（`Game\Battle\` + `Game\Net\` + `Game\World\`）

| 类型 | 职责 |
| --- | --- |
| `BattleWorld` / `BattleAgent` | 单机 PVE 的战斗世界（`SkillCastOutcome`） |
| `SkillCaster` | 输入 → 技能 |
| `NetSession` | 联机会话（`ESessionState`）：`Pump` / 快照 / 发输入 |
| `SnapshotView` | 服务端快照的**只读视图**（含 `FindHero(playerId)`） |
| `ServerEventBridge` | 服务端事件 → `EventCenter`（**桥**） |
| `ConditionEventBridge` | `EventCenter` → 条件系统（**桥**，M2 就有） |
| `BattleEvents` / `WorldEvents` | 战斗/世界事件 + 载荷 |

⚠️ **两条"桥"的归属是本层最重要的设计**（`Docs\教学\M2-A` / `M4-A`）：

```
生产事件的模块（战斗）  完全不知道「任务」存在
        ↓
   ConditionEventBridge（Bind<T>(事件, 条件类型, 取目标编号)）
        ↓
   ConditionTracker
```

⇒ 于是加"成就"时，**条件系统那一层一个字符都没动**。

⚠️ **`Game\Net\` 是"引擎无关子集"**（`_net-probe` 要能编它）：
**它不许引 `Game\Battle\`** —— 踩过一次（加一行 using，`_net-probe` 立刻 `CS0234`）。
所以 `NetSession` **只如实转发，不解释 `kind`**，翻译工作归 `Game\Battle\ServerEventBridge`。
📌 判据（D14）：**看整条依赖链**，不只看那个文件引了什么。

⚠️ **移动是状态、动作是事件**（`Docs\教学\M3-B`，本层最容易做错的一条）：
混起来就是"速度取决于网速"或"按住攻击每秒 30 下"。
另有：**输入保鲜期 6 帧**、**两种拒绝分开处理**（权威违反回错误 / 世界状态拒绝只记日志）。

---

## 五、装配（`Game\GameFlow\BattleSession.cs` + `Boot\`）

⚠️ **判据（`Docs\教学\M2-C`）**：**装配层不含逻辑。**
> 出现"判断"或"计算" ⇒ 多半放错了地方。

`BattleSession` 只做三件事：① 按依赖顺序把对象造出来 ② 把桥接上 ③ 给调用方一层门面。

```csharp
// 两个入口，**签名不同是有意的**
new BattleSession(quests, conditions, rewards, monsters, heroes, skills,
                  achievements, heroId, rewardSink, rewardLedger);   // 全参：测试 / 服务端
BattleSession.FromConfigMgr(heroId, rewardSink);                     // 运行时便利入口（内部用内存台账）
```

⚠️ **为什么 `FromConfigMgr` 不接台账参数**（`CS0012` 那次修法，`Docs\27` §十三）：
客户端本来就没有持久化台账；**而且给一个"没人用得上"的参数，代价不只是多一行 ——
它会把一个程序集依赖钉进调用方的依赖图里**（`Boot` 就得引用 `NBC.Shared`）。

⚠️ **`Boot\M2DemoBehaviour.cs`** 是"能点着玩"的那一半：读键盘、建场景物体、画 HUD。
它和 `BattleSession` 分开，于是"**闭环的正确性**"与"**能不能点着玩**"是两个可以各自验证的东西。

---

## 六、怎么验

```powershell
# ① 客户端语法/API + 全部 EditMode 测试源码能否编译
dotnet build Server\_api-probe\ApiProbe.csproj -m:1

# ② ⚠️ 程序集边界（唯一能抓 CS0012 的；改过公开签名就必跑）
powershell -ExecutionPolicy Bypass -File Tools\Check-AsmdefBoundaries.ps1

# ③ 行为：Unity 里跑 EditMode（当前 691 全绿）
#    Window > General > Test Runner > EditMode > Run All

# ④ 联机：真 socket 端到端（纯 .NET）
Server\_net-probe\bin\Debug\net8.0\NBC.NetProbe.exe       # 93 全绿
```

---

## 七、改这一层之前

1. **它属于哪个程序集？** ⇒ 见 §一。特别地：**`Game\` 里不许出现 `YooAsset` 字样**。
2. **改了 `BattleSession` / `AchievementRuntime` 等"被别人 new 的类"的公开签名吗？**
   ⇒ 必须跑 ②。**加一个可选参数就够让 `Boot` 编不过。**
3. **这件事该由谁决定？** —— 伤害/血量/掉落 ⇒ **服务端**；客户端只发意图、只显示快照。
4. **新加的事件，用 `EventId` 声明了吗？** 载荷：一个字段用 `int`、多个字段用 `readonly struct`。
5. **新加的状态要跨局活着吗？** ⇒ 那是持久化问题，见 `Docs\API\04-Server-服务端.md`。
