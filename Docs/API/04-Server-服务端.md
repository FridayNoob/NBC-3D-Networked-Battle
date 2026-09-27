# 04 · 服务端（`NBC.Server.Core` / `.Game` / `.Data` / `.Host`）

> **对应源码**：`Server\NBC.Server.Core\`、`NBC.Server.Game\`、`NBC.Server.Data\`、`NBC.Server.Host\`
> **对应测试/探针**：`Server\_net-probe`（**93 全绿**，真 socket + 两客户端）、
> `Server\_db-probe`（**51 全绿**，真 MySQL 往返）、`Server\_condition-probe`（47）
> **对应文档**：`Docs\01-项目需求文档.md` §13（服务端与数据库，**SRV-01~21 / DB-01~10**）、
> `Docs\27-M4开工清单.md` §十一、`Docs\教学\M3-A/B/C`、`M4-B`

---

## 一、四个工程的分工（**依赖方向是单向的**）

```
NBC.Server.Host   控制台进程：读配置 → 初始化 → 起网络 → 起主循环
      ↓
NBC.Server.Data   数据层（Dapper + MySqlConnector）── 只引 NBC.Shared
NBC.Server.Game   逻辑层（战斗/副本/AI/房间战斗服务）── 不引 Data（纯逻辑要能没数据库跑）
NBC.Server.Core   网络与主循环基础设施（会话/传输/Tick/房间）
      ↓
NBC.Shared        双端共享（**唯一源码在 Client\Assets\_Project\Shared\**）
```

⚠️ **`NBC.Shared` 是 `netstandard2.1` + **无第三方依赖** + `#nullable disable`** ——
它要被 Unity 与 .NET 8 **各编一遍**。往里面放 MySQL 依赖 = 让客户端也拖一个数据库驱动。

⚠️ 完整的程序集依赖图由 `Tools\Check-AsmdefBoundaries.ps1` **从 asmdef 与 csproj 真源机械校验**
（客户端部分），服务端部分是普通 `ProjectReference`。**别手画依赖图** —— 手画的一定会漂。

---

## 二、`NBC.Server.Core` —— 网络与主循环

| 类型 | 职责 |
| --- | --- |
| `TcpServerTransport` / `INetTransport` | 监听 + 收包（接缝，测试可换假实现） |
| `ClientSession` / `SessionPhase` | 连接 → 登录 → 绑定玩家 → 断线清理 |
| `ServerMessagePump` | **生产者-消费者队列**：网络 IO 只做"解包 + 入队" |
| `ServerMessageRouter` / `DispatchResult` | 消息分发 |
| `RoomRegistry` / `Room` / `RoomSeat` / `RoomJoinResult` | 房间与席位（**2~4 人**，1 房间 = 1 副本） |
| `RoomService` | 房间生命周期 |
| `TickScheduler` | **单线程定长 Tick（30Hz）** |

⚠️ **两条最要紧的设计（SRV-04 / SRV-05）**：

| 规矩 | 为什么 |
| --- | --- |
| **主循环单线程串行**，所有逻辑在该线程执行 | 避免锁与竞态，**简化调试** —— 这条一破，后面所有"偶发不同步"都会变成悬案 |
| **网络 IO 回调只做"解包 + 入队"**，逻辑全在主循环处理 | 需求文档点名：**这是避免多线程 Bug 的关键设计** |
| **DB 操作全部异步 + 脱离主循环线程**（DB-04） | 见 §四。⚠️ 结果要**回投主循环队列**，不能在 IO 线程上直接改游戏状态 |

---

## 三、`NBC.Server.Game` —— 逻辑层（纯逻辑，可无数据库运行）

| 类型 | 职责 |
| --- | --- |
| `DungeonBattle` / `BattleEntity` / `DamageResult` | 副本战斗世界（服务端权威） |
| `BossBrain` / `EBossState` | BOSS 的**纯 C# 状态机**（时间 = 逻辑帧号 ⇒ 可复现） |
| `BattleRandom` | **自研 xorshift32** —— ⚠️ `System.Random` 跨 .NET 版本不可复现 |
| `BattleInstance` / `SyncMode` | 一局战斗（状态同步 / 帧同步 / 混合） |
| `RoomBattleService` | 把副本接到房间上：每 tick 推进 + 广播 |
| `ServerTables` / `MonsterRow` / `HeroRow` / `DungeonRow` / `DropRow` / `SkillRow` | **服务端读的是 `Configs\Design\*.csv`（真源）**，不是 Unity 的 SO |
| `UnityEngineDebugShim` | 让共享层里的 `Debug` 在服务端也能用 |

⚠️ **`RoomBattleService` 每 tick 的顺序是有意义的**（`M4-S1`）：

```
ReconcileHeroes → ApplyInputs → Step → BroadcastCombatEvents（**先伤害后死亡**）
                                     → BroadcastDrops → BroadcastSnapshot
```

⚠️ 并抽出了 `SendToRoom`，让快照/战斗事件/掉落三条广播**共用同一份"同一份字节发全房"** ——
各自序列化一遍就会出现"两个客户端收到的字节不一样"。

⚠️ **BOSS 的可测性修正**（`M4-S1b`，真实调过数值）：
`BossAggroRangeMm = 2200`（够得着就追、够不着就回家）+ `BossLeashSlackMm = 200` +
出生环 `500mm`（保证最小距离 2500 > 2200）。
⇒ 起因是"**英雄一出生就被 BOSS 打死**"，算下来是 BOSS 120 dps 打 1200 血只要 10 秒。

---

## 四、`NBC.Server.Data` —— 数据层（**M4-S3 新增**）

| 类型 | 职责 |
| --- | --- |
| `DatabaseOptions` | 纯 POCO 连接参数 + `NBC_DB_PASSWORD` 覆盖 + **`Describe()` 绝不打印密码** |
| `DbConnectionFactory` | 开连接 + **把连接失败翻译成"该怎么办"**（DB-10） |
| `ConditionProgressDao` / `IConditionProgressDao` | 条件进度表的 Dapper 读写 |
| `CachingConditionProgressStore` | `IConditionProgressStore` 的**写回缓存**实现 |
| `RewardLedgerDao` / `IRewardLedgerDao` | 已发奖励台账的 Dapper 读写 |
| `MySqlRewardLedger` | `IRewardLedger` 的持久化实现 |
| `BattleRecordDao` / `BattleRecordWriter` | 一局战绩落库（一次事务）+ 排队/单飞写（**只给 `player_id > 0` 写明细**） |
| `AccountDirectory` / `AccountDao` / `LoginAuditWriter` | 账号登录（见 4.6） |
| `DatabaseUnavailableException` / `PingResult` | 精确捕获"数据库不可用"（DB-10 降级用） |

> ⚠️ **2026-09-27 退役**：`PlayerProfileDao` 与 `PlayerProfileSlots`（"玩家档案槽位"）
> **已删除**。它们存在的唯一理由是"还没有账号系统"，而那个理由已被真实登录消掉；
> 更关键的是**它们是一个数据污染源**：槽位发出去的 1~4 恰好是 4 个账号的档案 id
> ⇒ 游客的战绩被记进了**别人**的累计里（见 `Docs\27` §二十）。
> 现在游客拿**负数**编号，**`player_id > 0` ⟺ 真实账号档案**。

### 4.1 为什么**必须**是写回缓存

`ConditionTracker.Notify` 里**对每个已登记条件各读一次、各写一次**，
打死一只怪就是十几次 `Get` + 十几次 `Set`，30Hz 下每秒几百次。
接口 `IConditionProgressStore` **本来就是同步的** ⇒ **实现就必须是内存的**。

```
GetProgress / SetProgress / Remove   ← 纯内存，一次 IO 都没有（主循环在调）
LoadAsync()   ← 进副本/登录时调一次（异步）
FlushAsync()  ← 按"落库时机"调（异步）
```

### 4.2 三条线程语义（**最容易写错的地方**）

| # | 语义 | 违反了会怎样 |
| --- | --- | --- |
| ① | **绝不持锁做 IO** —— 锁里只拷脏集快照，出了锁再发 SQL | 持锁发 SQL = 主循环卡在等网络 = **等于没写缓存** |
| ② | **单飞（single-flight）** —— 上一次没写完不开新的 | 两次写**乱序落地** ⇒ 后落的写进**更旧的**绝对值 = **静默数据回退**。⇒ 单飞是**正确性**不是优化 |
| ③ | **`Remove` 要写 0**（不只删内存） | "内存删了库里还在" ⇒ 下次登录又读回来 ⇒ **"我明明重置过，怎么又有了"** |

### 4.3 落库时机（DB-04）

| 时机 | 为什么 |
| --- | --- |
| 进副本 / 登录 `LoadAsync()` | 进度是**跨局累计**的，必须先全部读回来 |
| 离开副本 / **成就解锁** `FlushAsync()` | 里程碑时刻，值得立刻落盘 |
| 定时兜底 | 崩溃最多丢一个周期 |
| 优雅关闭（SRV-17） | 等一次 `FlushAsync` 完成再退 |

### 4.4 ⚠️ 两个 store **必须成对使用**

```
① 进度活着（CachingConditionProgressStore）→ 否则重启后条件回 0，根本不会触发解锁
② 台账活着（MySqlRewardLedger）           → 否则触发了就又发一遍
```

只上一个都是半成品，而且**表现不同**：只上①⇒**重复发奖**；只上②⇒**成就不解锁**。
⇒ 这是"加了持久化之后才发现原有代码隐含假设了进度会随进程消失"（`Docs\教学\M4-B`）。

### 4.5 两张表（`Docs\08` 初始化 / `Docs\08b` **幂等迁移**）

| 表 | 主键 | 关键设计 |
| --- | --- | --- |
| `condition_progress` | `(player_id, condition_key)` | 主键是「玩家 + **条件**」（任务与成就**共用条件系统**，接口说话的单位就是条件编号）；存**绝对值** ⇒ 落库**幂等** |
| `reward_granted` | `(player_id, owner_kind, owner_id)` | 台账（**单调**：发过就是发过）。`owner_kind` 与 `ERewardOwnerKind` 一一对应 |

⚠️ **`Docs\08` 第 38 行是 `DROP DATABASE`（会清库）** —— 加表请用幂等的 `Docs\08b-...`。

⚠️ 写台账用的是 **`ON DUPLICATE KEY UPDATE`** 而不是 `INSERT IGNORE` ——
后者会把**所有**错误降级成警告，包括"外键不存在"，那就成了"发奖记录悄悄没写进去且不报错"。

### 4.6 账号与登录（**M4-S3 / SRV-06 收口**）

| 类 | 职责 | 一句话规矩 |
| --- | --- | --- |
| `IAccountStore`（在 **Core**） | 接缝：`LoginResult Login(account, passwordDigest)` | **同步、只查内存** —— 它跑在网络泵的握手里 |
| `AccountDirectory`（Data） | 启动时一次 `account JOIN player_profile` 读进内存 | `OrdinalIgnoreCase`（**跟库的 `_ci` 排序规则一致**）；密码**先验**再看档案 |
| `AccountDao`（Data） | 只写 `last_login_at = NOW()` | **让数据库自己打时间**（别和 `created_at` 混时区） |
| `LoginAuditWriter`（Data） | 登录审计"入队 + 单飞排空" | 与 `BattleRecordWriter` **同一套形状**（握手里只许入队） |

⚠️ **配方是双端契约**：`NBC.Shared.Auth.PasswordDigest`（**唯一实现**，两端编同一份源码）
```
线上：digest = SHA256(明文密码)          ← 客户端算，明文不过网络
入库：stored = SHA256(salt + digest)     ← 服务端算，与 account.password_hash 比
```
**改这条配方必须同步重算种子数据**（`Docs\08` 的 4 行 + `Docs\08c` 幂等迁移），
否则 4 个测试账号全部登不上 —— 而 `_db-probe`【八】的**阳性对照**就是盯这件事的。

⚠️ **身份不许静默降级**：带账号的握手失败就**拒**（先回说明再断开），
**不是**"那就当游客吧"（那会让玩家以为自己登录了，战绩却记到别人头上）。
没接数据库时也会明确说"用不了 + 下一步怎么做"。

⚠️ 摘要**没有任何出口**：不进日志、不进 `HandshakeAck.Reason`、不进 `LoginResult`
（`_net-probe`【十五】有一条专门翻遍所有服务端 Note 找它）。

### 4.6.1 服务端权威的成就判定（**M4-S3 第一刀**）

| 类型 | 职责 |
| --- | --- |
| `QuestTables`（`NBC.Server.Game`） | 服务端读 `Quest`/`QuestCondition`/`Achievement`/`Reward` 四张 CSV；**加载时校验跨表引用** |
| `ProgressFact` | 一条"发生过的事实"（谁 杀了什么/捡了什么 几个）—— 与下发给客户端的**事件**分开 |
| `IPlayerProgressStore` / `IPlayerRewardLedger` | 共享接口 + **带 IO 的 `LoadAsync`/`FlushAsync`**（共享接口故意是同步的） |
| `AchievementAuthority` | 每个登录玩家一个共享层 `ConditionTracker` + 进度 + 台账；喂事实 / 判解锁 / 防重复 / 冲库 |

⚠️ **三个必须守住的次序**（错一个就静默丢数据）：
① `LoadAsync` 在 `Register` **之前**（否则把库里进度覆盖成 0，且"已达成未记台账"的成就永远补不上）；
② 台账 `LoadAsync` 在第一次 `HasGranted` **之前**（否则每次启动重复发奖）；
③ 登录同步、Load 异步 ⇒ 中间的事实**排队不许丢**。

⚠️ **两条"设计如此"**：**进度是钳位的**（条件达成后不再累计）；`resetProgress:false`
每次启动都会回调"已达成"的条件（这就是"登录即解锁"），所以**必须靠台账挡重复发奖**。

⚠️ **这一刀没做**：奖励的**实际发放**（exp/gold 写进 `player_profile`，要与台账同事务）、
**权威进度下发客户端**（客户端仍在本地算 = 预测）、**任务状态机**（要新建 `quest_state` 表）。
见 `Docs\27` §21.4。

### 4.7 `player_id` 的取值约定（**一句话，全项目通用**）

```
player_id > 0   ← 真实账号的档案（player_profile.player_id）
player_id < 0   ← 游客（从 -1 往下发，一连接一个；**没有档案**）
player_id == 0  ← 还没有身份（还没握手 / 实体无主）
```

- **判据**：数据层所有"要不要落库"的判断**只看 `> 0`**；游戏层所有"这是不是某个玩家的东西"用 `!= 0`。
- ⚠️ **别把 `> 0` / `<= 0` 当万能写法** —— 它们混了两个不同的意思，见 `Docs\00` **W15**
  （改成负数之后，服务端 5 处旧比较立刻变成了 bug，是探针当场抓到的）。
- 游客的战绩只写 `battle_record`（局汇总），**不写** `battle_player_detail` / 累计 / 进度 / 台账
  （那三张表的外键都指向 `player_profile`，负数在那里不存在）。

---

## 五、`NBC.Server.Host` —— 控制台进程（SRV-01 / SRV-17 / SRV-20）

读 `appsettings.json` → 初始化日志 → 初始化 DB → 启动网络 → 启动主循环。

⚠️ **启动横幅里的 `Built :`（四个 dll 里最新的时间戳）不是装饰**：
负责人是**双击 `bin` 里的 exe** 启动的，而**双击永远不会编译**。
⇒ 曾经出现"改了代码、重启服务端、什么都没变"，根因是**跑的是旧 exe**
（完整复盘：`Docs\排查手册\06-改了却没生效（旧产物·生成物·真源）.md`）。
另外**源码比产物新就大声报警**（只比 `Server\**\*.cs`，**不比 CSV** —— 表是运行时读的）。

⚠️ **给别人的"重启服务端"指令必须写成"先编译、再启动"**：
`-t:Compile` **只算语法体检**（不写输出目录），`--no-build` 明确表示"跑现成的"。

---

## 六、怎么验

```powershell
cd "E:\U3D Projects\0_MyFile\3D联网战斗Demo"

# ① 编译（⚠️ 加 -m:1；本机并行 MSBuild 会静默失败）
dotnet build Server\NBC.sln -m:1

# ② 真 socket 端到端（两客户端 + 逐字段一致 + 战斗事件 + 掉落）
Server\_net-probe\bin\Debug\net8.0\NBC.NetProbe.exe                    # 93 全绿

# ③ 真 MySQL 往返（进度 / 台账 / DB-10 报错形状）
$env:NBC_DB_PASSWORD = [Environment]::GetEnvironmentVariable("NBC_DB_PASSWORD","User")
Server\_db-probe\bin\Debug\net8.0\NBC.DbProbe.exe                      # 51 全绿

# ④ 条件系统（双端共享逻辑）
Server\_condition-probe\bin\Debug\net8.0\NBC.ConditionProbe.exe        # 47 全绿
```

⚠️ **如果 ①失败且报 `MSB3027 / MSB3021 文件被 NBC.Server.Host (PID) 锁定`** ——
那是**你的服务端正在跑**锁住了 `bin`，**不是代码错**。关掉服务端再编，
或先用 `dotnet build Server\NBC.sln -m:1 -t:Compile` 做语法体检。

---

## 七、改这一层之前

1. **这段逻辑属于哪个工程？** ⇒ §一。特别是：**`NBC.Server.Game` 不许引 `NBC.Server.Data`**
   （纯逻辑要能在没有数据库的环境里跑测试）。
2. **碰了主循环线程吗？** ⇒ DB 一律 `async` + 脱离主循环；网络 IO 只入队。
3. **改了共享层的公开签名吗？** ⇒ `dotnet build Server\NBC.sln -m:1` **和**
   `Tools\Check-AsmdefBoundaries.ps1` 都要跑（后者抓客户端侧的 `CS0012`）。
4. **改了 `Configs\Design\*.csv` 吗？** ⇒ 服务端**运行时读 CSV**，**重启即可生效、不用重编**；
   但客户端要跑 ConfigKit + 导入菜单。
5. **加了新的持久化状态吗？** ⇒ 先问"它能不能从已有数据推导"。
   能推导就别建表；**不能推导（比如"发过奖没有"）才建表** —— 这正是台账存在的理由。
