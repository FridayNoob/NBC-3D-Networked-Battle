# 修复流水（不走 8 步的轻量通道）

> 判据：**改完之后用户会不会看到新东西？不会 ⇒ 记在这里；会 ⇒ 建 `Rxxx-` 文件夹走 8 步。**
> 一行一条，按时间倒序（新的在上）。只写四件事：症状、根因、改了哪些文件、怎么确认修好。

| 日期 | 症状（用户看到什么） | 根因（一句话） | 改了哪些文件 | 怎么确认 |
| --- | --- | --- | --- | --- |
| 2026-10-26 | Play 时报 `Location is invalid: 'AchievementConfig'` | YooAsset 模拟清单停在 09-23，而该表 09-26 才加 | 无（重跑一次构建） | 清单 `.bytes` 里能搜到该地址，Play 不再报错 |
| 2026-10-26 | 起 Host 报「监听 9000 失败（WSAEACCES）」 | 9000 落在 Windows 保留段 8945–9044 | 无（改用 `--port=7778`） | `netsh int ipv4 show excludedportrange` 不含 7778；Host 起来 |
| 2026-10-26 | 锁步模式下拖滑条，`单位-1` 不动 | 「自动追击」每帧把滑条值改写成 0 | `Editor/NetDebugWindow.cs`（追击改用自有字段） | 勾着自动追击时拖滑条，`单位-1` 仍能走 |

## 2026-10-28 新增 Tools\UnityMcp.ps1（AI 读编辑器实时状态）

- 现象：DSH 没有 MCP 客户端，AI 只能读文件，看不到「Inspector 里的真实值 / 控制台 / 当前场景层级」。
- 处理：接 AnkleBreaker MCP（Unity 插件 :7891 + npm 服务器 stdio），再用 `Tools\UnityMcp.ps1` 包一层一次性调用（`-List` / `-Schema` / `-Tool`）。
- 影响：每次调用起一次 `npx`，慢 2~4 秒；写操作会入 undo 栈。
- 注意：参数 JSON 走临时文件，否则 PowerShell 会吃掉双引号。

## 2026-10-28 Unity-chan 资产差点被提交（192 MB / 1085 文件）

- 现象：模型导入在 `Assets/UnityChan/`，而 `.gitignore` 只挡了 `Art/Characters/`。
- 处理：补 `Client/Assets/UnityChan/` 与 `UnityChan.meta`，README 说明补法。
- 依据：Unity-Chan License 不得单独再分发模型数据。

## 2026-10-30 代码注释规范化（方法改五段式，含「功能」）

- 规范变更：方法注释从四段改为五段 —— 功能 → 入参 → 步骤 → 返回 → 上游/下游（负责人要求「先说这个方法干什么」）。
  已同步 `csharp-code-comment` 技能（含 description 与自检三问）与 `AGENTS.md` 注释规范第 4 条。
- 已落地 13 个文件（56 个方法）：`PlayerMove` / `PlayerAnimation` / `FollowCamera` / `HpBarPanel` / `NetHudPanel` /
  `RuntimeNetDriver` / `NetRuntimeStats` / `CountingTransport` / `HpBarDemoBehaviour` / `IQuestRewardSink` /
  `IQuestStateAuthority` / `IConfigSource` / `WorldEvents`。
- 同时删掉：`====` 装饰线、工单编号（M2-A2 / §二十六）、`**加粗**`、反引号、`<summary>` XML 标签。
- 保留：所有「注意：」里的坑与边界（如「false = 服务端没说，不是未接取」）。
- 踩坑：批量插入脚本用 `WriteAllLines` 把 7 个 `.cs` 写成了 CRLF，已改回 LF；脚本本身也必须带 BOM（W7c），否则 PS5.1 读成乱码。
- 范围：按规范第 109 行，其余 200+ 文件不批量改，跟着后续需求逐步规范化。

## 2026-10-30 注释规范独立成文 + 公共 API 改分层

- 新文档 `Docs\新项目文档\项目代码注释规范.md`（12 节，唯一入口）：方法五段、TODO 格式、商业通行要求对照、存量推进策略。
- 公共 API 那条从「不做」改为**分层**：跨程序集 public 写 `///` 契约摘要，其余只用 `//` 五段；
  判定标准是「会不会被别的程序集调用」；`CS1591` 不开（会打爆 0 警告基线），靠自检与 review。
- 样板：`NetRuntimeStats.Snapshot`（`///` 在外、五段在方法体内，互不重复）。
- 其余 `///` 跟着后续需求逐步补（负责人 2026-10-30 选定节奏）。

## 2026-10-30 修回归：e2e 探针被 RuntimeNetDriver 带崩

- 现象：`dotnet build Server\_net-probe\NetProbe.csproj` 报 CS0234/CS0246（NBC.Game.View / MonoBehaviour / SerializeField 找不到）。
- 根因：`NetProbe.csproj` 用 `Compile Include="..\..\Client\Assets\_Project\Game\Net\**\*.cs"` 把整个目录编进探针；
  R008 新增的 `RuntimeNetDriver.cs` 依赖 UnityEngine 与 Game.View，住在 `Game\Net` 里 ⇒ 探针编不过。
- 修法：把 `RuntimeNetDriver.cs`（含 .meta）移到 `Game\Bootstrap\`，命名空间改 `NBC.Game.Bootstrap`，并补 `using NBC.Game.Net;`；
  `HpBarDemoBehaviour` 的 using 同步改名。**不再用「排除文件」绕过** —— `Game\Net` 应保持引擎无关。
- 验证：api-probe 0 错/10 警告；net-probe 0 错/0 警告；asmdef 12/12 全绿。
- 教训：往「被探针整目录收录」的目录里加文件前，先确认它是否引入引擎依赖。

## 2026-10-31 全项目注释规范化收尾（139 个文件）

- 结果：220 个 .cs 中 **139 个已按《项目代码注释规范》规范化**，未规范 0 个；
  排除：生成代码 22（Protocol / Config\Generated）、受保护 1（SnapshotView.cs）、Tests 58（负责人决定不改）。
- 范围：Game 42 / Shared 25 / Framework 46 / Framework.UI 7 / Framework.Net 6 / Framework.YooAsset 3 / Editor 7 / Boot 3。
- 验证手段：每批都做「剥掉注释后逐行比对代码」+ api-probe（0 错/10 警告）+ asmdef（12/12）；net-probe（0 错/8 警告）。
- 顺带修的真问题：`RuntimeNetDriver` 住在 `Game\Net` 导致 e2e 探针编不过 → 已移到 `Game\Bootstrap`；
  `Battle.unity` 被 Unity 重存时丢了一处 `m_ApplyRootMotion` 覆盖 → 已还原。
- 过程教训：手写文件路径清单出过两次错（列出不存在的文件，导致 3 个代理撞同一文件）→ 之后一律先 Get-ChildItem 实测再派发。

## 2026-10-31 记一条工具限制：MCP 的 execute_code 回调不跨帧

- 现象：想用 `unity_execute_code` 注册 `EditorApplication.update += tick` 做逐帧采样，代码编译通过、返回成功，
  但回调**从不执行**（文件没生成、日志没出现）。
- 原因：MCP 的执行上下文不跨帧存活，注册的委托下一帧就不在了；而 MCP 单次往返 2~4 秒，
  外部轮询也分辨不了 33ms 的帧间距。
- 结论：**帧级现象不要指望远程采样**，改用「宏观二值实验」——
  例如验证「瞬移兜底」时把槽位扔到 500 米外，看它是 2 秒内归位（归位分支）还是 8 秒以上（匀速追分支）。

## 2026-11-05 调试开关：怪物对玩家的伤害暂时关闭（R019 验收用）

- 位置：`Server\NBC.Server.Game\DungeonBattle.cs` 的 `MonstersCanDamageHeroes`
- 现状：`false`（怪物打英雄直接拒，日志写「调试：怪物对玩家的伤害已关闭」）
- 为什么：负责人要测客户端攻击，不想被怪反杀
- **恢复方法**：把该常量改成 `true` 并重编服务端 —— 伤害逻辑一行都没删
- 影响面：只拦「怪物 → 英雄」；英雄打怪、怪打怪不受影响

## 2026-11-09 修：行为树删节点后残留「引用了不存在的子节点」导致无法预览

- 现象：删掉画布上一个子节点后点【预览】，状态栏报「无法预览：节点 n1 引用了不存在的子节点 n4。」
- 根因：`BTGraphView.RemoveNode` 只从 `m_data.Nodes` 删了节点，**没把它的 id 从父节点的 `Children` 里摘掉**，`RootNodeId` 也可能指向已删节点
- 修法（三处）：
  1. `BTGraphData.RemoveDanglingReferences()`：清掉指向不存在节点的 `Children` 引用、清空失效的 `RootNodeId`，返回清理条数
  2. `BTGraphView.RemoveNode`：删完节点立刻调用它并重算根
  3. 兜底：`OnImport`（外部 JSON 可能自带脏引用）与 `BTPreviewRunner.Start`（改成「克隆 → 清理 → 校验」的顺序）都会清理并打警告
- 验证：造一棵 `n1 → [n2, n4]` 的脏树，清理前 `Validate=false`（消息与截图一致）、清理后 `true`；脏树直接预览 `Start=True`，`hp=10` 时 `Selector/HPBelow` 均为 `Success`
- 影响面：只在编辑器（`NBC.Editor`），不涉及服务端与打包

## 2026-11-09 修：同一节点被连两次 → 预览报「检测到环：节点 n2 被重复访问」

- 现象：删掉 n2 再重新连线后点【预览】，报「检测到环：节点 n2 被重复访问。行为树必须是有向无环图。」
- 实据：导出快照里 `n1.children = [n3, n2, n4, n2]` —— **n2 被连了两条边**，DFS 第二次访问同一 id 就被当成环
- 根因：连线允许重复（没有"这对节点已连过"的判断）；导出时逐条边写 `Children` 也没去重
- 修法：
  1. `BTGraphView.CompleteConnection`：同一对「输出 → 输入」已连过就直接忽略
  2. `BTGraphView.ExportData`：写 `Children` 前 `Contains` 去重（重复边只记一次）
  3. `BTGraphData.Validate`：先单独判「同一子节点连了两次」「子节点有多个父节点」，给出准确原因；真正的环才报环
- 验证：重复引用报「节点 n1 重复引用了子节点 n2」；多父报「子节点 n4 有多个父节点（n2 和 n3）」；往画布导入含重复子节点的数据，导出后 `children=[n2]` 且 `Validate=True`
- 附带修复：上一版对 `RemoveNode` 的行插入把注释与语句重复拼了 12 次（能编译但很脏），已还原成干净代码

## 2026-11-09 修：玩家不操作十几秒就「心跳超时」掉线

- 现象：进副本后不动（或切窗口），十几秒后掉线，提示心跳超时
- 根因（两处叠加）：
  1. **服务端**心跳超时 `TcpServerTransport.DefaultHeartbeatTimeoutMs = 10s`（10 秒没收到任何字节就断）
  2. **客户端** `RuntimeNetDriver.Update` 把 `Time.deltaTime` **原样**喂给会话时钟；卡顿/切窗口后某帧 delta 可能是几秒
     ⇒ 会话时钟猛跳，立刻判「超过 5s 没收到 Pong」；编辑器窗口那边早有 250ms 上限（注释就写着"照实喂会假掉线"），运行时却漏了
- 修法（三处）：
  1. `RuntimeNetDriver`：新增 `MaxPumpDeltaMs = 250`，`deltaMs` 夹住
  2. `ProjectSettings.asset`：`runInBackground: 0 → 1`（失去焦点时 Play 继续跑，心跳不断）
  3. `TcpServerTransport.DefaultHeartbeatTimeoutMs`：`10_000 → 30_000`（留足卡顿余量）
- 验证：探针 436/0 全绿（无用例依赖 10s）；服务端重编 0 错
- 影响面：只放宽"多久算掉线"，不改变任何战斗规则；真断线仍会在 30s 内被发现

## 2026-11-09 修：狼「横着走」—— 模型默认朝 +X，服务端朝向约定是 +Z

- 现象：狼走路动画正常，但身体与前进方向差 90°，像螃蟹横着平移
- 根因：服务端 `FacingDeg` 约定 **+Z = 0°**（巡逻 +X 时给 90°）；`SnapshotSceneBinder` 直接 `Euler(0, facingDeg, 0)`
  而 **Forest Wolf 模型默认朝 +X** ⇒ 多转了 90°。英雄由 `PlayerMove` 自己驱动朝向，所以只有怪不对
- 修法：`SnapshotSceneBinder.m_facingOffsetDegrees` 默认 `0 → -90`（只作用于怪物槽位）
- 验证：Play 里狼应正着走；若变成"倒着走"，把该值改成 `+90`
- 备注：以后换其它怪物模型（默认朝 +Z）时，要么把它改回 0，要么改成"按预制体配偏移"的表

## 2026-11-12 修：怪「隔着很远也能打到」—— 英雄位置在客户端本地积分、与服务端长期漂移

- 现象：狼在 3~5 米外（看着还在左右移动）仍能打掉玩家血
- 根因：**英雄位置只有客户端在算**（`PlayerMove` 用 `Time.deltaTime` 本地积分），服务端按输入在 30Hz 里算自己的那份；
  两者从不校正，还会被**场地边界**放大：服务端把英雄夹在 ±5000mm（`PlayAreaHalfExtentMm`），
  客户端没有边界 → 玩家能走出 5 米，客户端看到"人在远处"，服务端看到"人就在怪旁边" ⇒ 怪照样打中
- 修法（客户端 `RuntimeNetDriver`，每帧）：
  1. 用快照里的我的英雄位置（`SnapshotView.FindHero`）做纠偏：漂移 > 0.3m 按 8m/s 靠拢，> 2m 直接瞬移
  2. 把英雄夹在 ±5 米（与服务端一致）
  3. 只做"跟随式纠偏"，不是预测回滚；帧同步模式不走这条路径
- 验证：走远到边界外应被挡住；怪只在约 1.8 米内才打得中
- 备注：真正干净的做法是"预测 + 回滚"，属 M5；demo 阶段跟随纠偏足够

## 2026-11-12 修：R030 验收发现的三个问题

### ① 红圈外仍掉血（待用诊断日志定位）

- 已加**两端诊断日志**（临时，定位后可删）：
  - 服务端 `DungeonBattle.TryBasicAttack`：怪物打中英雄时打印 `[伤害诊断] 怪#X → 英雄#Y 距离=...mm（射程 2000mm）`
  - 客户端 `RuntimeNetDriver.ReconcileHeroPosition`：漂移 > 0.5m 打印 `[位置诊断] 英雄漂移 ...`；找不到英雄快照打印 `[位置诊断] 找不到我的英雄快照 PlayerId=...`
- 判据：服务端日志距离 **> 2000mm** ⇒ 两边位置不同步（看客户端漂移日志）；距离正常 ⇒ 是画圈用的半径/位置不对

### ② 狼悬空 / 碰撞体不平行地面

- 实据：狼的网格渲染器脚底在 `y = -0.439`（**陷进地里** 0.44m），碰撞体长边在 Z 而模型朝向 **+X**
- 修法：
  1. 碰撞体改成 `size (2.0, 0.9, 0.6)`、`center (0, 0.45, 0)`（长边朝 X，底面贴地）
  2. 预制体加 `Model` 容器，把所有模型子物体搬进去，整体上移 **0.439**，脚底对齐 `y=0`
- 结果：`脚底 minY = 0.000`；根下只剩 `Attack / Aggro / Ground / Model`

### ③ 玩家血条只变色、不变短

- 根因：`HpBarPanel.prefab` 的 `Img_Fill` **Sprite 为空** ⇒ `Image.fillAmount` 对空 Sprite 不生效（Type=Filled 也没用），只剩颜色变化
- 修法：
  1. 预制体：给 `Img_Fill` 赋内置 `UI/Skin/UISprite.psd`，Type=Filled、FillMethod=Horizontal、fillAmount=1
  2. 代码兜底：`HpBarPanel.SetRatio` 在「Sprite 为空 / Type 不是 Filled」时改用 `anchorMax.x = ratio` 收缩

## 2026-11-12 补：狼"浮空"真因（动画姿势）+ 死亡后误报位置警告

- 实测（Play 中查询）：怪与英雄 root `y=0.115`、**碰撞体底部 = 0.000** ⇒ 物理没浮空
- 真因：编辑期是按**绑定姿势**量的脚底，进 Play 后 Animator 摆姿势，脚就离地了
- 修法：`SnapshotSceneBinder` 每只怪**第一次**进入 LateUpdate 时，按当前姿势量模型包围盒，把 `Model` 容器整体偏移到脚底 = 地面（只做一次）
- 顺带修：英雄**死亡后** `FindHero`（只返回活着的）为 null 会刷「找不到我的英雄快照」警告；
  改成「按 OwnerPlayerId 找、不看死活」，死了就安静返回

## 2026-11-13 修：R033 验收三问题（F1 不回 Login / 云不见 / 场景改名）

### ① 按 F1 不回 Login

- 根因：`SceneFlow` 只走 YooAsset 一条路；地址没被收集器覆盖时**只报错不回退**，表现就是"按了没反应"
- 修法：`LoadRoutine` 改成「YooAsset 失败（抛异常或 handle.Error）→ **自动退回 SceneManager**」；
  成功时打印 `[SceneFlow] 已加载 X（路径：YooAsset|SceneManager）`，一眼看出走的哪条
- 判据：F1 后 Console 必有 `已加载 Login（路径：...）`

### ② 云不见了：叠加加载不会继承天空盒

- 根因：**天空盒/雾/环境光（RenderSettings）是每个场景各自一份**；`LoadSceneAsync(Additive)` 只加物体，**不套渲染设置**。
  云在环境场景的**天空盒材质**里 ⇒ 不搬就等于没有
- 修法：`WorldEnvLoader.ApplyEnvironmentRenderSettings(envScene)`：临时把活动场景切成环境场景读 RenderSettings，
  切回来写进当前场景（天空盒/雾/环境光/反射强度/太阳），最后 `DynamicGI.UpdateEnvironment()` 重算
- 光照策略反转：**保留环境场景的平行光**（原版布光），关掉**我们自己的**平行光（`m_worldLight`），避免双光过曝

### ③ 环境场景改名

- `SampleScene.unity` → **`Environment_World.unity`**（含 .meta 一起改）
- 同步三处：Build Settings 路径、`WorldEnvLoader.m_envSceneName`（场景里序列化值 + 代码默认值）
- 注意：美术包改名后**素材包升级会重新生成 SampleScene**，届时要重新改一次名（记在这条里）

## 2026-11-13 修：F1/F2 切不了场景（编辑器吃掉了按键）

- 现象：World 里按 F1/F2 完全没反应（连日志都没有）
- 根因：**F1~F5 是 Unity 编辑器的快捷键**（F1 帮助、F2 重命名），Play 模式下按键先被编辑器消费，`Input.GetKeyDown` 收不到
- 修法：
  1. 快捷键改成 **Ctrl+Shift+1~5**（1 Login / 2 World / 3 PVP_Room / 4 Battle / 5 Test）
  2. 每次按键都打两条日志：`请求加载 X` 与 `已切换到 X（路径：...）`；**没有第一条就说明按键没进来**
  3. 失败时打 `切换 X 失败：...`，不再静默
- 判据：按 Ctrl+Shift+1 后 Console 必须出现「请求加载 Login」
- 顺带：R032 的 06 手工验收步骤已同步改成新键位

## 2026-11-13 真因：快捷键按了没反应 = 组件是"丢失脚本"

- 现象：换成 Ctrl+Shift+1~5 后仍然**一条日志都没有**
- 真因：场景里 `[SceneFlowDebugKeys]` 对象在，但组件 `m_Script` 指向一个**内嵌的空 MonoScript**（fileID 536156266），
  实际是"缺失脚本" ⇒ 组件不运行，`Update` 从不执行，所以既没反应也没日志
- 为什么会这样：`SceneFlowDebugKeys` 与 `SceneFlow` 写在**同一个 .cs 文件**里。
  **Unity 一个文件只能挂一个与文件名同名的 MonoBehaviour**，第二个类挂上去就是缺失脚本
- 修法：
  1. 拆出独立文件 `Game/GameFlow/SceneFlowDebugKeys.cs`
  2. 两个场景的 `m_Script` 改为指向新脚本 guid `10dbef4e06a32ef418cab4e93ed94741`
- 验证（MCP 实测）：Login / World 均「组件=True，对象激活=True，组件启用=True」，`m_Script: {fileID: 0}` 计数 0
- 教训：判断"组件有没有生效"不能看 Hierarchy 里有名字，要看**场景文件里的 m_Script guid**；
  另外避免"Play 期间改 .unity 文件"——退出 Play 会用快照覆盖

## 2026-11-13 修：World 相机"跟不上"（其实是固定偏移、不跟朝向）

- 实测（Play 中）：玩家 `(7.07,4.34,34.85)`、相机 `(7.07,8.34,28.85)` ⇒ **位置正好是 玩家+(0,4,-6)**，跟随逻辑没坏
- 真因：旧 `FollowCamera` 用的是**世界固定偏移**（永远在世界 -Z 后方），角色转身后看到侧脸/正面，观感像"镜头没跟"
- 修法：新增 `WorldFollowCamera`（沿用 `WorldOrbitCamera` 的球坐标思路）
  1. 用 `LerpAngle` 把 yaw 平滑到角色朝向 ⇒ 镜头绕到角色**背后**
  2. 位置 `Lerp` 平滑，滚轮调距离（2~15 米）
  3. `WorldPlayerSetup` 在 `Start` 里**运行时自动**给它加上并接线；旧的 `FollowCamera` 自动 `enabled=false`
- 判据：转身 180° 后，画面应逐渐绕到角色背后（不是一直看侧脸）

## 2026-11-13 修：玩家"双份移动"（根与模型各挂一套 PlayerMove）

- 现象：相机像跟不上玩家；实际是**两个物体各自位移**，视觉模型跑到一边去了
- 实据（Play 中 MCP 查询 `[Player_Hero]` 整棵树）：
  - `PlayerMove` on `[Player_Hero]`（enabled=True，我们要的）
  - `PlayerMove` on `SwordGirl_FullBody`（enabled=False）
  - `PlayerAnimation` on `SwordGirl_FullBody`（enabled=True）—— 它对 `PlayerMove` 有 `[RequireComponent]`，会把它拉回来
- 根因：模型预制体自带一套玩家脚本；我们把模型当"纯视觉子物体"用时，它身上那套也会读同一份输入
- 修法（双保险）：
  1. `WorldPlayerSetup.Start` 加守卫：**关掉子物体上重复的 `PlayerMove` / `PlayerAnimation`**，并打 Warning（让问题自证）
  2. 非 Play 状态下把模型实例上的这两个组件**直接删掉**（Inspector 干净，不靠运行时兜底）
- 判据：Console 出现「关掉子物体 SwordGirl_FullBody 上重复的 PlayerMove」；走动时只有一个物体在动

## 2026-11-13 定：玩家层级职责拆分（方案 C）

- 负责人问"为什么不删根上的 PlayerMove" ⇒ 采纳折中方案 C：
  - **根 `[Player_Hero]` 负责"动"**：`PlayerMove` + `PlayerAnimation` + `WorldPlayerSetup`
  - **模型 `SwordGirl_FullBody` 只负责"好看"**：只剩 `Animator`（`Sword_Controller`，含 `Speed`/`Direction`）
- 为什么这么分：以后接网络只改**根**的位置；模型换皮不影响移动；`PlayerAnimation` 的 `Awake` 本来就用 `GetComponentInChildren<Animator>()`，放根上能自动找到子物体的 Animator ⇒ **零代码改动**
- 踩坑：先删模型的 `PlayerMove` 会失败（当时 `PlayerAnimation` 还在，`[RequireComponent]` 拦着）；顺序必须是**先删 PlayerAnimation、再删 PlayerMove**
- 验证：根 `PlayerMove=True PlayerAnimation=True`；模型 `PlayerMove=False PlayerAnimation=False Animator=True`；Unity 0 编译错误
