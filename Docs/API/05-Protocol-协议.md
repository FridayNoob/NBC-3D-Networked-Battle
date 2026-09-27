# 05 · 协议（`Protocol\nbc_m3.proto`）

> **对应源码**：`Protocol\nbc_m3.proto`（**唯一来源**）、`Protocol\nbc_probe.proto`（探针用）
> **生成物**：`Client\Assets\_Project\Protocol\NbcM3.cs`（**提交进仓库**，两端共用）
> **对应程序集**：`NBC.Protocol.asmdef` / `NBC.Protocol.csproj`（`overrideReferences: true` + `Google.Protobuf.dll`）
> **对应测试**：`Tests\EditMode\Net\ProtocolTests.cs`（**机械检查**契约版本一致）、`Server\_net-probe`（**93 全绿**，真 socket）
> **对应教学**：`Docs\教学\M3-A-网络层.md`、`M3-B-房间与状态同步.md`
> ⚠️ 详细说明文档 `Docs\03-协议定义说明.md` 仍是 **PLANNED**（未写）—— 本篇覆盖"改协议要知道的"，不是逐字段手册

---

## 一、三条铁律（改协议前必读）

| # | 铁律 | 违反了会怎样 |
| --- | --- | --- |
| ① | **`.proto` 是唯一来源**，两端用 `protoc` 从**同一份**生成 C#，**生成产物入库** | 手写协议类 ⇒ 两端漂移，而**漂移不报错**（同 D1「一份源码两边都编」的道理） |
| ② | **`protoc` 与运行时 `Google.Protobuf` 必须同版本（本项目都是 3.21.1）** | 混版本得到"编译不过 / 运行期报错"，而且**报错不会提示是版本问题** |
| ③ | **不兼容改动（删字段 / 改类型 / 改语义）必须 `CONTRACT_VERSION` +1**，并同步 `Shared\Net\NetContract.cs` | 老客户端连新服务端会出现"读到的字段是默认值"这种**静默错误** |

⚠️ **③ 有机械检查盯着**：`Tests\EditMode\Net\ProtocolTests.cs` **直接读 `.proto` 文件**比对两处版本号
（不是靠自觉）。⇒ **改了 `.proto` 里的版本号，必须同时改 `NetContract.Version`，否则测试红。**

---

## 二、生成命令（⚠️ 三条硬约束）

```
cd /d <仓库根>                       ← 工作目录走 Unicode API，中文路径没问题
"E:\U3D Projects\TeachNet\Protobuf\protoc.exe" ^
    --proto_path=Protocol ^
    --csharp_out=Client/Assets/_Project/Protocol ^
    Protocol/nbc_m3.proto
```

| 约束 | 为什么 |
| --- | --- |
| `protoc` 用 **3.21.1**（`E:\U3D Projects\TeachNet\Protobuf\protoc.exe`） | 与 `Google.Protobuf.dll` 配对（铁律②）。⚠️ 离线包里那份 `protoc-35.1` **不要用** |
| **绝不要给 `protoc` 传含中文的路径** | `argv` 走 ANSI 代码页 ⇒ 中文路径会被破坏（`Docs\11` §5.2 有完整诊断） |
| **参数只给相对路径 + 工作目录设成仓库根** | 同上；这是绕开中文路径的**唯一**稳妥做法 |
| 生成后**把 `NbcM3.cs` 一起提交** | 生成产物入库是刻意的（见铁律①）：克隆下来不用先装 protoc 就能编 |

⚠️ **改完必须做三件事**：① 重跑生成命令 ② 跑 `dotnet build Server\NBC.sln -m:1` **和**
`Tools\Check-AsmdefBoundaries.ps1`（协议程序集是 `references` 的，很容易漏引用）
③ 跑 `_net-probe`（真 socket 才验得出"两端读同一份字节"）。

---

## 三、协议内容导览（按"一次连接的顺序"）

### 3.1 握手与心跳

| 消息 | 用途 |
| --- | --- |
| `Handshake` / `HandshakeAck` | 连接后的第一件事：**校验协议版本**（SRV-19）。不匹配就拒绝并提示 |
| `Ping` / `Pong` | 心跳（`client_time_ms` / `server_time_ms`）⇒ 顺带能算 RTT |

⚠️ **握手也要有超时**（`Docs\教学\M3-A` 记的四处"顺序敏感"的坑之一）——
不然一个连上就不说话的客户端会一直占着会话。

### 3.2 房间与席位

| 消息/枚举 | 用途 |
| --- | --- |
| `RoomPhase` | 房间阶段（等待/准备/战斗中/结算） |
| `JoinRoomRequest` / `LeaveRoomRequest` | 加入/离开（**2~4 人**，1 房间 = 1 副本） |
| `RoomMember` / `RoomState` | 席位与房间状态 |
| `ErrorResponse` | ⚠️ **权威违反**的回执（见 §五） |

### 3.3 输入上行（**只发意图**）

```protobuf
message PlayerInput { ... }      // 对应 Shared\Net\NetContract.TickRate = 30Hz
```

⚠️ **移动是状态、动作是事件**（`Docs\教学\M3-B`）：
输入里既有"想往哪走"（会被服务端**每帧重新判定**）也有"按了哪个动作"（一次性事件）。
混起来就会出现"速度取决于网速"或"按住攻击每秒 30 下"。
另有 **输入保鲜期 6 帧**（`InputFreshTicks`）—— 过期输入**丢弃**，不能补算。

### 3.4 状态下行（**服务端是唯一世界**）

| 消息 | 用途 |
| --- | --- |
| `EntitySnapshot` | 一个实体一行（⚠️ 含 `owner_player_id = 10`，见 §四） |
| `WorldSnapshot` | **每 tick 全量快照**（本项目不做 delta / 不做 AOI，Q3 砍掉） |

⚠️ **客户端没有"自己那份血条"**：它只做两件事 —— **只发意图、只显示快照**。

### 3.5 战斗事件（**动作是事件**）

| 消息 | 用途 |
| --- | --- |
| `DamageEvent` | 一次伤害（含 `applied` = **实际扣了多少血**，不是"想要扣多少"） |
| `DeathEvent` | 一次死亡（带 `kind` + `config_id` + `killer_id`） |
| `DropEvent` | 一次掉落 |
| `ServerEvent` | **信封**，oneof 装着上面三种 |

⚠️ **`ServerEvent` 的 oneof 字段叫 `event`** —— 所以 C# 里是
`ServerEvent.EventCase` / `ServerEvent.EventOneofCase.Damage`，
**不是** `PayloadCase`（`Payload` 是**外层信封** `ServerMessage` 的字段）。
写错是 `CS0117`，闸门当场抓到（`Docs\27` §三 记过这次）。

⚠️ **死亡事件必须带 `kind`**：如果把"玩家死了"和"怪死了"合并成一个"击杀目标 0"的事件，
而 `0 = 任意目标`，那么**任何击杀任务都会涨进度** —— 这是一个真实差点发生的静默错
（`Docs\27` §三⓵，已写成回归用例）。

### 3.6 信封

| 消息 | 方向 |
| --- | --- |
| `ServerMessage` | 服务端 → 客户端（oneof `Payload`） |
| `ClientMessage` | 客户端 → 服务端 |

⚠️ **分帧与信封是两件事**：TCP 上没有消息边界 ⇒ 外层是 `Shared\Net\FrameCodec` 的
**`[4 字节小端长度][载荷]`**，里面才是 `ServerMessage` 的字节。
分帧代码在**双端共享层**（两端同一份），所以 `_net-probe` 能在纯 .NET 里真跑。

---

## 四、⚠️ 加字段 / 改字段的规矩

| 改动 | 兼容？ | 要做什么 |
| --- | --- | --- |
| **加一个新字段**（新编号） | ✅ 兼容 | 老客户端读到默认值 —— 所以**默认值必须是有意义的**（例：`owner_player_id = 10` 加进来时，老客户端读到 `0` = "不是任何人的" ⇒ 安全） |
| **改字段语义**（类型不变、含义变了） | ❌ **不兼容** | `CONTRACT_VERSION` +1 + 同步 `NetContract.Version` |
| **删字段 / 改类型 / 改编号** | ❌ **不兼容** | 同上；而且**删掉的编号不要复用**（老客户端会把新含义当旧的读） |
| **改枚举的值** | ❌ **不兼容** | 同上 |
| **加一个 `oneof` 分支** | ✅ 兼容 | 但要确认老客户端收到未知分支时**不会崩**（protobuf 会把未知分支当"未设置"） |

📌 **一个真实例子**：`EntitySnapshot.owner_player_id = 10` 是 M4-S1b 加的**兼容**改动 ——
目的是**让客户端认出"哪个是自己"**，而不是靠"第一个英雄就是自己"这种会在多人房里出错的假设。
当时还顺手加了回归用例（`SnapshotViewTests.FindHero_MatchesOwnerPlayerId_NotJustTheFirstHero`）。

---

## 五、⚠️ 两种"拒绝"要分开（协议层最容易做错的一处）

| 类型 | 表现 | 处理 |
| --- | --- | --- |
| **权威违反**（客户端发了越权的东西） | 回 `ErrorResponse` | **必须让客户端知道**：它作弊了/发错了 |
| **世界状态拒绝**（"这一刻不能这么做"，比如对着尸体放技能） | **只记日志** | ⚠️ **不回错误** —— 那是**正常的竞态**（延迟导致的先后差），回错误会让客户端弹一堆无意义的提示 |

> 📌 判据（`Docs\教学\M3-B`）：**"玩家做错了"和"世界刚好不允许"是两件事**，
> 混起来就是"网络一抖就弹一堆错误提示"。

---

## 六、怎么验

```powershell
# ① 真 socket 端到端（两个进程真的能说话 + 逐字段一致）
dotnet build Server\_net-probe\NetProbe.csproj -m:1
Server\_net-probe\bin\Debug\net8.0\NBC.NetProbe.exe          # 通过 93，失败 0

# ② 契约版本一致性（机械检查，直接读 .proto 比对）
#    Unity 里：Test Runner > EditMode > ProtocolTests

# ③ ⚠️ 改过 proto 之后必跑：程序集边界（协议程序集是 references 的）
powershell -ExecutionPolicy Bypass -File Tools\Check-AsmdefBoundaries.ps1
```

`_net-probe` 里与本篇直接相关的几条（它们各自钉住一条契约）：
**两端逐字段一致** / **`applied` 是实际扣血** / **死亡带 `kind` + `config_id` + `killer_id`** /
**同帧先后顺序** / **长度前缀是小端** / **超长帧判违规并说明原因**。

---

## 七、改协议之前

1. **这个改动兼容吗？** ⇒ §四的表。不兼容 ⇒ 版本 +1（两处！）。
2. **加了新字段，老客户端读到默认值会怎样？** ⇒ 默认值必须是**安全的**。
3. **重跑生成命令了吗？** ⇒ 生成物要一起提交。
4. **`protoc` 路径里没有中文吗？** ⇒ 有就换工作目录 + 相对路径。
5. **`ServerEvent` 里新加的分支，客户端认得吗？** ⇒ 不认得时**安静忽略**，不是抛异常
   （同 `ConditionTracker` 那套"通用层 + 多消费者"的边界）。
