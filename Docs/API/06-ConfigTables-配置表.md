# 06 · 配置表（`Configs\Design\*.csv` → 生成物 → SO）

> **真源**：`Configs\Design\*.csv`（**10 张表**）
> **工具**：`Tools\ConfigKit\`（校验 + 生成）、`Client\Assets\_Project\Editor\ConfigImporter.cs`（TSV → SO）
> **生成物**：`Client\Assets\_Project\Game\Config\Generated\`（`*.cs` + `*.tsv` + `*.json`）
> **对应测试**：`Tools\ConfigKit\tests\ConfigKit.SelfTest`（**58 全绿**）、`Tests\EditMode\Game\Config*`
> **对应文档**：`Docs\17-配置表规范.md`（**规范正文**）、`Tools\ConfigKit\README.md`（工具与跨表规则）

---

## 一、⚠️ 最重要的一条：**谁是"真源"，谁是"生成物"**

```
Configs\Design\*.csv          ← **真源**（策划/你改这里）
        ↓ ConfigKit CLI（校验 + 生成）
Game\Config\Generated\        ← 生成物：*.cs / *.tsv / *.json（**提交进仓库**）
        ↓ Unity 菜单「Tools/NBC/配置表/导入 TSV → ScriptableObject」
Assets\_Project\Configs\*.asset   ← 生成物：SO（Unity 运行时读它）
```

⚠️ **服务端读的是 `Configs\Design\*.csv`（真源）**，客户端读的是 SO（生成物）。
**两边读的不是同一个文件** —— 这是本项目真实踩过的坑：

> 有人在 Unity 的 SO 里把狼王血量改成 1000，测试时还是 2000 ——
> 因为**服务端读 CSV**，SO 只是客户端那份。
> 完整复盘：`Docs\排查手册\06-改了却没生效（旧产物·生成物·真源）.md`

📌 **判据：改数值永远改 `Configs\Design\*.csv`；改完要"跑生成 + 导入 SO + 重启服务端"。**

---

## 二、当前 10 张表

| 表 | 行数 | 主键 | 一句话 |
| --- | --- | --- | --- |
| `Hero.csv` | 2 | `id` | 英雄（服务端固定用 `1001`） |
| `Skill.csv` | 2 | `id` | 技能 |
| `Level.csv` | 2 | `id` | 关卡 |
| `Monster.csv` | 2 | `id` | 怪物（`6001` 野狼 / `6003` 狼王） |
| `Dungeon.csv` | 2 | `id` | 副本：**`monsters` 列用重复次数表示"刷几只"**（`"6001,6001,6001"` = 3 只）+ `bossId` |
| `DropTable.csv` | 5 | `id` | 掉落（`chancePerTenThousand` 万分比） |
| `Quest.csv` | 4 | `id` | 任务（`conditionIds` / `rewardId`） |
| `QuestCondition.csv` | 6 | `id` | **条件**（`eventType` / `targetId` / `requiredCount`）—— 任务与成就**共用** |
| `Reward.csv` | 3 | `id` | 奖励（经验/金币/物品） |
| `Achievement.csv` | 3 | `id` | 成就（与 `Quest` 同构，语义不同：**跨局累计**） |

⚠️ **表清单在代码里只有一处**：`Game\Config\GameTables.cs` 的 `All`（客户端预加载用）。
**服务端**不需要它 —— 服务端直接读 CSV。
⚠️ `Dungeon` / `DropTable` **故意不在** `GameTables.All` 里（那两张是服务端读的）。

---

## 三、CSV 的写法（**规范正文在 `Docs\17`**，这里只列最常踩的）

每张表前 4 行是表头，第 5 行起是数据：

```
id|name|hp|attack          ← ① 字段名（英文，就是生成的 C# 字段名）
编号|名称|血量|攻击         ← ② 中文名（给人看）
int|string|int|int         ← ③ 类型（含 ref: 外键、enum: 枚举、ref:[] 数组）
key|len(1,16)|range(1,999999)|min(0)   ← ④ 校验规则
6001|野狼|300|20           ← 数据
```

⚠️ **本项目的 CSV 用 `|` 分隔而不是逗号**（文件后缀是 `.csv`，但分隔符是竖线）——
所以你**不会**踩"含逗号的格子要加引号"那个坑。
但**数组列**用的是**逗号**：`"6001,6001,6001"` ⇒ 那里仍然要注意引号规则。

⚠️ **多行内容必须逐行写**（这是我自己踩过的）：不管用哪条路径喂表，
**把多行拼成一个带换行的字符串只会让第一行进表**，表现是"外键指向 4002 但那边没有这个主键"
—— 看着像检查器坏了。

**空值的写法**：该空的格子**留空**。
⚠️ 写 `-` / `--` / `null` / `无` / `N/A` **都是错的**（`CFG0007` 专抓这个）：
"没填"和"填了一个像空值的字符串"是两件事，后者会让 `ref:` 校验通过而运行期炸。

---

## 四、校验分两层（**这是本工具最值钱的设计**）

### 4.1 单表校验（`CFG0001~0019`）—— "这张表自己合不合规"

| 码 | 抓什么 |
| --- | --- |
| `CFG0001` | 表头结构不对（缺行、列数不齐） |
| `CFG0004` / `CFG0017` | 主键声明问题 / 主键重复（**报错时指出两行**） |
| `CFG0006` / `CFG0007` | 该填的空着 / 空值的**写法**不对（`-`、`null`、`无`…） |
| `CFG0008` / `CFG0009` / `CFG0012` | 值和类型对不上 / `range` 越界 / `len` 越界 |
| `CFG0010` / `CFG0011` | `unique` 冲突 / **外键指向的键不存在**（表不存在与值不存在**分开报**） |
| `CFG0013` | **`float` 字段没标 `view`** —— ⚠️ 战斗数值不许用浮点 |
| `CFG0014` / `CFG0015` | **表中间空行**（后面的数据会被静默丢弃）/ 字段名中断后右边又有内容（**有一列会被静默丢弃**） |
| `CFG0016` / `CFG0019` | 合并单元格/公式 / **可空值类型**（Unity 序列化不支持 `int?`） |

### 4.2 跨表校验（`CFG0020~0023`）—— "**单表全绿 ≠ 这张表能被玩通**"

| 码 | 抓什么 | 级别 |
| --- | --- | --- |
| `CFG0020` | `KillMonster X × N`：必须有某个副本能提供 **≥ N 只** X<br>⚠️ 只对**任务**引用的条件、且 `targetId > 0` 成立 | 错误 |
| `CFG0021` | `CollectItem I`：I 必须在 `DropTable`，**或**在**别的**持有者的奖励里<br>（只在"它自己那个任务"的奖励里 = **循环依赖** ⇒ 拿不到） | 错误 |
| `CFG0022` | 用到了**当前没有事件源**的事件类型（表在 `ConfigPolicy.EventTypesWithoutSource`） | **警告**（实现缺口，不是数据写错） |
| `CFG0023` | 一条 `QuestCondition` 被**多个持有者**引用 ⇒ 运行期 `ConditionTracker.Register` **直接抛异常** | 错误 |

⚠️ **`CFG0023` 的由来**：`ConditionTracker` 对**重复登记**是当场抛 `InvalidOperationException` 的
（它故意不静默覆盖）。所以"两个任务共用一条条件""任务与成就共用一条条件"都是
**数据单看全绿、炸在运行期**。

⚠️ **`CFG0020` 的两处豁免都是被误报逼出来的**（"天天误报的警告等于没有警告"）：
① 只查**任务**引用的条件（成就用 `resetProgress:false` 是**跨局累计**）；
② `targetId = 0`（**任意怪**）跳过（它本来就不在任何副本的怪列表里）。
⇒ 两条豁免都做了**变异测试**证明"改坏了会红"。

---

## 五、日常操作（三个命令 + 一个菜单）

```powershell
cd "E:\U3D Projects\0_MyFile\3D联网战斗Demo"

# ① 只校验、不落盘（改完表先跑这个，最便宜）
Tools\ConfigKit\src\ConfigKit.Cli\bin\Debug\net8.0\NBC.ConfigKit.Cli.exe `
    --source Configs\Design --out Client\Assets\_Project\Game\Config\Generated --check-only

# ② 校验 + 生成（0 错 0 警告时写出 40 个文件）
Tools\ConfigKit\src\ConfigKit.Cli\bin\Debug\net8.0\NBC.ConfigKit.Cli.exe `
    --source Configs\Design --out Client\Assets\_Project\Game\Config\Generated

# ③ 工具自己的自测（含跨表规则的阳/阴对照）
Tools\ConfigKit\tests\ConfigKit.SelfTest\bin\Debug\net8.0\NBC.ConfigKit.SelfTest.exe    # 58 全绿
```

**Unity 里**：菜单 **「Tools/NBC/配置表/导入 TSV → ScriptableObject」**
（⚠️ 它**一次导入全部 `.tsv`**，所以加表之后点一次就够；另有「检查（只报告，不写资产）」菜单）。
**服务端**：读 CSV ⇒ **重启服务端即可生效，不用重编**。

⚠️ **顺序不能反**：改 CSV → 跑 CLI（生成 TSV）→ 点导入菜单（生成 SO）。
跳过中间那步直接点菜单 ⇒ 导入的是**旧 TSV**（"我明明改了"的经典成因）。

---

## 六、加一张新表要改哪几处（一份清单）

| # | 改哪 | 为什么 |
| --- | --- | --- |
| 1 | 新建 `Configs\Design\<表名>.csv`（**4 行表头 + 数据**） | 真源 |
| 2 | 跑 CLI 生成 | 产出 `Config_<表名>.cs` / `<表名>Config.cs` / `.tsv` / `.json` |
| 3 | **`Game\Config\GameTables.cs` 的 `All` 加表名** | ⚠️ 客户端预加载的**唯一清单**。忘了加 ⇒ 运行期第一次用到那张表才炸 |
| 4 | Unity 菜单导入 TSV → SO | 生成 `.asset` |
| 5 | 若服务端要读 ⇒ `Server\NBC.Server.Game\ServerTables.cs` 加读取 | 服务端读 CSV，需要自己的行类型 |
| 6 | 若加了**新的校验规则/跨表关系** ⇒ `Tools\ConfigKit\src\ConfigKit.Core\Policy.cs` 或 `CrossTableChecks.cs` | ⚠️ 规则要放 `Policy`（**数据**）而不是散在 `if` 里 |

---

## 七、⚠️ 改表之前

1. **改的是真源（CSV）还是生成物（SO / Generated）？** ⇒ 只有真源算数。
2. **改了条件/物品/副本吗？** ⇒ 跑 CLI，让**跨表检查**说话（它专抓"永远达不成/永远拿不到"）。
3. **新加的字段类型是 `float` 吗？** ⇒ 战斗数值**不许**用浮点（`CFG0013`）；
   要浮点必须标 `view`（表示"只给显示用"）。
4. **新加的 `ref:` 指向的表存在吗？** ⇒ 外键校验会报，但**先想清楚那张表在不在生成范围内**。
5. **改了表结构之后，Unity 的 SO 重新导入了吗？** ⇒ 没导入的话，客户端还是旧结构，
   表现是"字段是默认值"（**不报错**）。
