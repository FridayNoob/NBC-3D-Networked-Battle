# 修复流水（不走 8 步的轻量通道）

> 判据：**改完之后用户会不会看到新东西？不会 ⇒ 记在这里；会 ⇒ 建 `Rxxx-` 文件夹走 8 步。**
> 一行一条，按时间倒序（新的在上）。只写四件事：症状、根因、改了哪些文件、怎么确认修好。

| 日期 | 症状（用户看到什么） | 根因（一句话） | 改了哪些文件 | 怎么确认 |
| --- | --- | --- | --- | --- |
| 2026-11-20 | 第二次进副本「血条还有血却判死亡」（HUD 读到没进房的驱动） | `HpBarDemoBehaviour.Start` 用 `RuntimeNetDriver.FindJoined()`，第二次进副本时它可能退回「没进房的菜单会话」（`HeroMaxHp == 0`）⇒ 血条此后永不刷新，停在上一局的值 | `Client/Assets/_Project/Boot/HpBarDemoBehaviour.cs`（`Update` 每次确认手里的是已进房驱动，不是就重新 `FindJoined()`，并只在 `IsJoined` 时才写血条） | 血条与实际血量一致；Console 出现「血条改读已进房的驱动」 |
| 2026-11-20 | 再进副本「立刻死亡」（前两条的真正根因） | 英雄出生点 (-500,0) 距 BOSS 仅 2500mm（= 旋风斩射程）、距最近的怪 3500mm（< 仇恨 4000）；客户端加载 Battle 要 5~7 秒，服务端早已开打 ⇒ 玩家接管时人已经死了 | `Server/NBC.Server.Game/DungeonBattle.cs`（HeroSpawnPoint 挪到 (0,4500)）、`Server/_net-probe/Program.cs`（追击改二维逼近、边界用例按圆形距离、BOSS 仇恨判据反转） | 服务端日志「进入副本 15:51:01.353 → 死亡 15:51:08.601」= 加载期间被打死；net-probe 492/0 |
| 2026-11-20 | 同上（第二版）：加了局号仍在进场那一帧读到 HP=0/1200 | 那一刻还没收到新的席位表，m_room 仍指向旧局，局号比较过不掉 | `Client/Assets/_Project/Game/Net/NetSession.cs`（DiscardSnapshotsUntilRoomState）、`Client/Assets/_Project/Game/Bootstrap/RuntimeNetDriver.cs` | EditMode 817/0、net-probe 492/0；Console 出现一次「丢弃进房前收到的快照…等新的席位表」 |
| 2026-11-20 | 死亡回 World 再进副本，一进场就判死（HP=0/1200）且无黑幕 | 上一局残留、还在网络管道里的快照被新一局当成真死；死亡退出路径也没关 AutoJoin | `Protocol/nbc_m3.proto`（WorldSnapshot 加 match_id=3）、`Server/NBC.Server.Game/RoomBattleService.cs`、`Client/Assets/_Project/Game/Net/NetSession.cs`、`Client/Assets/_Project/Game/Bootstrap/RuntimeNetDriver.cs`、`Client/Assets/_Project/Game/Player/PlayerDeathFlow.cs` | net-probe 492/0、EditMode 817/0；Console 出现「丢弃上一局的残留快照：第 N 帧属于第 X 局」 |
| 2026-11-20 | （结构改进）副本进出的四个开关分散在驱动里，只复位一个就会被下一帧推翻（回世界后自动又进房） | 状态没有单一入口：`m_autoJoin` / `m_joinRequested` / `m_heroAlive` / `m_bindPresentation` 各自被零散赋值 | 新增 `Client/Assets/_Project/Game/Bootstrap/BattlePresence.cs`（四个开关 + `EnterDungeon()` / `ExitToWorld()` 两个入口 + `ToString()` 诊断串）、`RuntimeNetDriver`（字段改为转发、`Start` 灌初值、新增 `EnterDungeon()`）、`DungeonEntrance.FinishEnter`（改调 `driver.EnterDungeon()`）；新增 `Client/Assets/_Project/Tests/EditMode/Game/BattlePresenceTests.cs` 5 条 | EditMode 通过；运行时自检 `joined=False heroAlive=True` |
| 2026-11-20 | 结算面板里点「返回世界」，到 World 后失败面板 / 黑幕还压在草地上 | ① 退房只发了 `LeaveRoom`，没关 `m_autoJoin` ⇒ 下一帧自动重新进房（房间常驻，一进就中），World 里 `joined=True`；② `m_heroAlive` 停在 false，World 的死亡流程判定为死；③ 结算消息随后在 World 里 `Show` | `Client/Assets/_Project/Game/Bootstrap/RuntimeNetDriver.cs`（`ReturnToWorldRoutine` 先 `m_autoJoin=false` 并 `m_heroAlive=true`；`OnDungeonEnded` 门禁改 `!m_bindPresentation`）、`Client/Assets/_Project/Game/Player/PlayerDeathFlow.cs`（`IsDeadNow` 追加 `driver.BindPresentation`）、`Client/Assets/_Project/Game/UI/DungeonResultPanel.cs`（新增 `OnDestroy` 清 `s_instance`/`IsShowing`，并新增静态 `HideIfShowing`）、`Client/Assets/_Project/Game/UI/DeathScreenOverlay.cs`（`HideIfShowing` 先清 `s_wantShow` 再 Hide + `UIManager.HidePanel`） | Play：死亡 → 失败面板 → 返回世界，Console 出现 `[结算] 已经离开副本，忽略迟到的结算（失败）。`，草地上无面板无黑幕；运行时自检 `[DungeonSession] joined=False`；EditMode 812/0 |
| 2026-11-20 | 技能多段（`hitFrames` 写成 `15,29,41`）以后，`net-probe` 三条红：BOSS 技能掉血对不上、`§17d 第 15 帧扣血` 期望 500 实得 166 | 探针里还写死「2001 是一段 500 伤害」，而表已经改成 3 段均分（166/166/168） | `Server/_net-probe/Program.cs`（`Boss_AttacksInRange` 与 `Skills_CastAndCooldown` 改成从表推导：`SkillSystem.SplitDamage(sum, hitFrames.Length)` + 逐帧对照，BOSS 段数按 `MonsterRow.SkillIds` 展开） | `dotnet run --project Server\_net-probe\NetProbe.csproj -m:1 --no-build` ⇒ `通过 492，失败 0` |
| 2026-11-20 | 副本里按 Q/E/R 没伤害，服务端日志一条都没有（普攻正常） | 场景里有两个 `RuntimeNetDriver`（Login 的 `[MenuSession]` 常驻 + 副本的 `[DungeonSession]`），`PlayerBrain` 用 `FindObjectOfType` 可能拿到没进房的那个，消息发出去被服务端「不在房间」静默丢掉 | `Client/Assets/_Project/Game/Bootstrap/RuntimeNetDriver.cs`（新增 `static FindJoined()`：优先 `IsJoined`）、`PlayerBrain.cs`、`PlayerDeathFlow.cs`、`HpBarDemoBehaviour.cs`（三处改用它） | 进副本按 Q，Console 出现 `[技能] 请求释放 2001 → 单位 N`，服务端日志出现 `玩家 1 释放技能 2001`；若仍无，看是否提示「找到的 RuntimeNetDriver 还没进房」 |
| 2026-11-20 | 副本里按 E 放技能，Console 报 `AnimationEvent 'OnSkillHit' on animation 'S_Sp_Skill2' has no receiver!` | 动画事件只派发给「Animator 所在物体」上的组件，而 `PlayerAnimationEvents` 被挂在了玩家根 `[Player_Hero]` 上（模型是子物体 `SwordGirl_FullBody`） | `Client/Assets/_Project/Game/Player/PlayerBrain.cs`（`Awake` 解析出 Animator 后，若 `animator.gameObject` 上没有接收器就 `AddComponent`） | 进副本按 Q/E/R，Console 出现 `[动画事件] 技能 2001 命中帧 @ 0.747s`，不再报 no receiver |
| 2026-11-20 | 在动画里加了好几个 `OnSkillHit`，点「试算/写回」却还是只显示一个时间、写回只写一段 | 试算与写回用的是上次「扫描」缓存的 `rows`，加完事件没重扫就是旧快照 | `Client/Assets/_Project/Editor/SkillHitSection.cs`（试算与写回前都先 `rows = SkillHitWriter.Collect()` 重扫） | 加 4 个事件后点试算 ⇒ `2001：15 → 15,16,29,41`（四段），不再只出第一段 |
| 2026-11-20 | 在动画事件窗口点「添加/删除」时 Console 刷 `Please use Editor.AnimationUtility to add persistent animation events`；参数名只有 Int/String/Float，不知道该填什么 | 代码用 AnimationClip.events 的旧 setter 写事件（Unity 不持久化还会告警），参数标签也没按事件语义起名 | Client/Assets/_Project/Editor/AnimationEventEditorWindow.cs（读写改 AnimationUtility.Get/SetAnimationEvents，新增 IntParameterLabel/DescribeInt）、Client/Assets/_Project/Editor/SkillHitWriter.cs（读事件同样改 AnimationUtility） | 反射调 AddEvent/RemoveEventAt ⇒ before=2 afterAdd=3 afterRemove=2 且无告警；窗口里「整数参数」标签随函数变（技能编号 / 技能序号 / 连击段数） |
| 2026-11-20 | 手数「这一刀在第几帧」容易错，改动画又要重算，`Skill.csv` 的 `hitFrames` 不好维护 | 帧数只存在表里，和动画事件没有联系 | 新增 `Client/Assets/_Project/Editor/SkillHitWriter.cs`（扫 `OnSkillHit(技能编号)` → 按 `round(秒×30÷控制器速度)` 换算帧）与 `SkillHitSection.cs`（窗口里「扫描 / 试算 / 写回 Skill.csv」）；`PlayerAnimationEvents.OnSkillHit` 的 int 参数改为技能编号；三个技能片段各加一个示范事件 | 窗口里点「扫描」看到 2001/2002/2003 三行且帧号 40/22/48 与表一致；改事件时间后「试算」显示 `40 → 40,52`，点「写回」CSV 变成 `"40,52"` |
| 2026-11-20 | 技能/普攻的伤害时机只能靠 Skill.csv 的 hitFrames 去对动画，换动画就错位；负责人想自己在关键帧挂 Event | 缺一个「动画播到哪一拍」的客户端通知点 | 新增 `Client/Assets/_Project/Game/Player/PlayerAnimationEvents.cs`（只做表现，不发协议） | 挂到带 Animator 的模型物体、在 Animation 窗口加 Event 后，Console 出现 `[动画事件] 技能 1 命中帧` |
| 2026-11-20 | 按 Q 放技能既没伤害也没无敌 | 最近的目标在 6309mm 外，服务端按射程 2500mm 拒；客户端没有任何提示，看起来像技能坏了 | `Client/Assets/_Project/Game/Bootstrap/RuntimeNetDriver.cs`（发请求前量距离 + 读 `Skill.csv` 的 `rangeMm`，超了打 `[技能] 目标太远`）、`PlayerBrain.cs`（技能编号读不到时不再缓存成空数组） | Console 出现 `[技能] 目标太远：6.3 米 > 射程 2.5 米`；走近再按 Q 服务端日志出现「释放技能」 |
| 2026-11-20 | 副本里玩家死亡后点「再来一次」，新一局角色仍保持倒地姿势（黑幕也再亮一次） | 「再来一次」只清了表现层与黑幕，从没调用 `PlayerBrain.NotifyRevive`（状态机还停在 `Reaction/Die`）；同时 `RuntimeNetDriver.m_heroAlive` 停在上一局的 false，`PlayerDeathFlow.IsDeadNow()` 立刻又判死 | `Client/Assets/_Project/Game/Player/PlayerDeathFlow.cs`（新增 `ReviveAfterRestart()`：清倒计时/已结算/旁观三个标记 + 关黑幕 + `ResetToGround` + `NotifyRevive` + 恢复渲染器，不搬位置）、`Client/Assets/_Project/Game/Bootstrap/RuntimeNetDriver.cs`（`RestartRoutine` 里 `m_heroAlive = true;` 并调新增的 `ReviveLocalPlayerAfterRestart()`，找不到死亡流程就只让大脑复活） | Unity 编译 0 错误；死亡 → 再来一次，角色站起可走动、黑幕不再亮（2026-11-20 负责人复验通过）；EditMode 812/0/11.78s |
| 2026-11-19 | 进副本瞬间刷三条 `[HUD] …表加载失败：还没设置配置来源` + 两条 `[AssetManager] 资源层尚未初始化`，随后 `[HUD] 打不开 HudTargetPanel/HudQuestPanel` | `RuntimeNetDriver.RefreshPresentation` 在进副本第一帧就 `EnsureHud`，而 `HpBarDemoBehaviour.Start` 还在 `await AssetBootstrapper.InstallAsync`；HUD 提前去请求 Canvas 与配置表 | `RuntimeNetDriver.EnsureHud` 与 `RuntimeHudDriver.Bind` 都加「`AssetManager.Instance.IsInitialized` 才行」的门；`RuntimeHudDriver.ResolveConfigs` 在没设来源时不打「已请求」标记，等启动流程 `SetSource` 后自动重试 | Unity 编译 0 错误；进副本不再出现这两类报错；EditMode 805/0/11.95s |
| 2026-11-19 | 按 E 进副本报 `InvalidOperationException: [ConfigMgr] 表「Quest」还没加载`，并提示「已加载的表：（一张都没有）」 | 新流程（Login → World → Battle）里没人调过 `ConfigMgr.SetSource` / `GameTables.PreloadAll`（只有旧离线 demo `M2DemoBehaviour` 会做），而 `RuntimeHudDriver.Bind` 一上来就 `ConfigMgr.Get<QuestConfig>()`，缺表即抛 | `Client/Assets/_Project/Boot/HpBarDemoBehaviour.cs`（资源层就绪后 `EnsureConfigSource()`：没来源就 `SetSource(new AssetConfigSource())`，空表时 `GameTables.PreloadAll`）、`Client/Assets/_Project/Game/UI/RuntimeHudDriver.cs`（去掉 `Get` 改 `ResolveConfigs`：`TryGet` 拿不到就发一次 `Preload` 并静默跳过，缺表不炸 HUD） | Unity 编译 0 错误；进副本不再抛；EditMode 805/0/12.11s |
| 2026-11-19 | Login 里点「开始游戏」就报 `[AssetManager] 还没有装上底层加载器。请先调用 AssetManager.Instance.SetProvider(...)`，此时还没进副本 | R046 把死亡黑幕从「运行时自建 UGUI」改成 `BasePanel`；`PlayerDeathFlow.Awake` 预热 `DeathScreenOverlay.Ensure()`，而 Awake 在 Login 场景也会跑，那时资源层没装加载器 | `Client/Assets/_Project/Game/UI/DeathScreenOverlay.cs`（`Ensure` 加 `CanLoadPanels()`：`AssetManager.Instance.IsInitialized` 才去 `ShowPanel`）、`Client/Assets/_Project/Game/Player/PlayerDeathFlow.cs`（预热从 `Awake` 挪到 `Start`） | Unity 编译 0 错误；Login 点开始不再报错；EditMode 805/0/12.30s |
| 2026-11-17 | 服务端控制台里怪物名是一串 `?`，攻击力显示 0（`怪 6001 ??? hp 300 / 攻 0`） | `Configs/Design/Monster.csv` 被 Excel 另存成 **GBK 无 BOM**，而 `ServerTables` 里 `File.ReadAllLines(path, Encoding.UTF8)` 硬按 UTF-8 读 ⇒ 中文名变问号、`IntOr` 把解析失败的数字静默回落成 0 | `Server/NBC.Server.Game/ServerTables.cs`（新增 `ReadAllLinesAuto` / `DecodeAuto`：BOM 优先 → 严格 UTF-8 → 退回 GBK(936)；真出现 U+FFFD 就抛 `InvalidDataException` 点名表名）；`Server/_net-probe/Program.cs`（§17b 三条：GBK 表能加载、名字读对、数字没坏）；`Client/Assets/_Project/Game/Net/NetSession.cs`（结算日志不再引用 `NBC.Game.UI`，探针才编得过） | net-probe 443 通过 / 0 失败（§17b 三条全绿）；服务端重启后横幅「阵容：怪 6001 野狼 hp 300 / 攻 20」 |
| 2026-11-16 | 进 Battle 后看不到玩家，Console 每 5 秒刷「找不到我的英雄快照：PlayerId=1，在线=True，实体数=0」 | 切场景时挂在 World 物体上的协程被销毁，`yield` 之后的「开 AutoJoin / 开 BindPresentation / 接相机 / 摆出生点」永远不执行 | `Client/Assets/_Project/Game/GameFlow/DungeonEntrance.cs`（收尾搬进 `SceneFlow.LoadAsync` 回调 + `FinishEnter` + `m_battleSpawn`） | 进副本 Console 出现「已进入副本，打开自动进房与表现绑定」；玩家在原点、相机跟随、实体数 > 0 |
| 2026-11-16 | 进副本后玩家模型上多出一堆脚本，Animator 控制器也不是 World 里那份 | Battle.unity 遗留旧玩家预制体实例 962813207（自带 PlayerMove/PlayerAnimation/PlayerBrain），与跨场景带过来的玩家并存；相机 m_target 仍指向它 | 删实例 + SceneRoots 项 + m_target 置 0；PlayerPersist.BindCamera 在 Battle 加载后接相机到带过来的玩家 | Battle 层级里只有一个 [Player_Hero]；相机跟随正常 |
| 2026-11-16 | 按 E 只报「连接超时」，实际是服务端 9000 / 客户端 7778 不一致；改成 7778 后又报「账号登录被拒：没有这个账号 dev1」 | 端口各写一份；--dev-accounts 没开时 dev1 不存在 | 端口全部对齐 7778（appsettings / HostConfig / TcpServerTransport / net-probe / NetDebugWindow / Docs 11）；入口失败提示改为显示真实原因（FailureReason > LastError > 超时） | 服务端加 --dev-accounts 启动；横幅出现「调试账号 已启用」 |
| 2026-11-14 | 格挡中按左键/J 还能起手攻击；连击单测停在 Attack1/Idle | `NotifyAttack` 只判死亡没判格挡；测试辅助 `AdvanceTo` 会把下一段推过头 | `PlayerStateMachine.cs`（首行加 m_blocking 判断）、`PlayerActionLayerTests.cs`（新增 TickFrames） | Test Runner 全量 747/747 绿 |
| 2026-11-14 | 按 F 摆姿势播到一半，角色整个人跳一下又落回（Angry / Smile / Surprise 明显） | 贴地射线遮罩是 `-1`，摆姿势时手里的剑（`Weapon_Sword` 的 `BoxCollider`）移到射线下面，被当成地面把玩家抬到剑的高度 | `Client/Assets/_Project/Game/GameFlow/WorldPlayerSetup.cs`（`LateUpdate` 改 `RaycastNonAlloc` 并跳过玩家自身碰撞体） | 七个姿势轮换时角色不再上下跳；站原地按 F，Console 无异常 |
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
- 补充：模型上还查出 **`PlayerBrain`**（玩家状态机）也被一起清掉；相机侧正式挂上 `WorldFollowCamera` 并接线（旧 `FollowCamera` 置灰）

## 2026-11-13 修：玩家状态机被误删，改挂到根节点

- 现象：走路动画正常了，但玩家状态机（`PlayerBrain`）没了
- 根因：方案 C 清理"模型上的逻辑组件"时，把 `PlayerBrain` 和 `PlayerMove` 一起删了；但它其实是要保留的状态机
- 修法：把 `PlayerBrain` 加到**根节点**（它 `Awake` 里 `GetComponent<PlayerMove>()`，根上正好有）
- 最终根组件：`Transform / PlayerMove / WorldPlayerSetup / PlayerAnimation / PlayerBrain`
- 判据：Console 有状态切换日志（Idle ↔ Move），走动时状态名会变

## 2026-11-13 修：转身无效（场景里 turnSpeed=5）+ 相机改为鼠标控制

### ① 按 A/D 不转身：场景序列化值是 5

- 实测：`World.unity` 里 `PlayerMove.m_turnSpeed = 5`（Battle 场景是 720）⇒ 转 90° 要 18 秒，肉眼看就是"不转"
- 注意：**代码默认值 720 对已序列化的场景无效**；`[SerializeField]` 一旦写进场景，就以场景为准
- 修法：把 `World.unity` 的 `m_turnSpeed` 改成 720 并**保存场景**（已在文件里回读确认：`m_turnSpeed: 720`）
- 判据：退出 Play 再进，仍能转身（上次只改了运行时值，所以退出就还原）

### ② 相机不该跟着角色转

- 需求：角色转身时镜头不动；**镜头旋转由鼠标控制**
- 修法：`WorldFollowCamera` 去掉「yaw 平滑跟随角色朝向」，改为：
  - **右键拖动** 控制 yaw/pitch（pitch 夹 -20~80 度）
  - 滚轮控制距离（2~15 米）
  - 只跟随**位置**（平滑 Lerp），不跟朝向
- 判据：按住 A 转身时画面朝向不变；右键拖动时镜头能自己转

## 2026-11-13 需求：玩家移动改用相机坐标系

- 负责人要求：W/A/S/D 相对**相机**（W 永远是镜头前方），不是世界轴
- 修法（可选开关，默认关，Battle 不受影响）：
  1. `PlayerMove` 加 `m_useReferenceAxes` + `m_reference`，以及 `SetReferenceAxes(bool, Transform)`
  2. 打开后：`forwardAxis / rightAxis` 取自参考体的 `forward / right`，并 **`ProjectOnPlane` 压到水平面**（镜头俯视时角色不会往地里钻）
  3. `WorldPlayerSetup.Start` 里对玩家调用 `SetReferenceAxes(true, Camera.main.transform)`
- 兼容：`m_useReferenceAxes = false` 时行为与改前完全一致（世界四向）
- 判据：转动镜头后按 W，角色朝镜头前方走；Battle 场景仍按世界轴走

## 2026-11-13 修复：K/L 调试键在编辑器里没反应 + 缺 PlayerAnimation 日志

- 现象：R035a 06 验收时按 K 受击、L 死亡无反应；Console 没有「关掉根节点上的 PlayerAnimation」
- 根因一：`PlayerBrain.m_enableDebugKeys` 默认 false，`World.unity` 序列化值也是 0，`HandleActionKeys` 整段被开关包住
- 修法一：条件改成 `m_enableDebugKeys || Application.isEditor`（编辑器里始终可用，构建版本仍受开关控制），场景值不用改
- 根因二：`WorldPlayerSetup` 只在「PlayerAnimation 仍是启用」时打日志，而 `PlayerBrain.Awake` 更早把它关了 ⇒ 日志被跳过
- 修法二：改成无论此前是否已关都打一行，并写明「本次由 WorldPlayerSetup 关掉」/「此前已被 PlayerBrain 关掉」
- 判据：编辑器 Play 后 Console 必有「根节点上的 PlayerAnimation 已关闭」；按 K 出 `S_Hit_L`、按 L 出 `S_Die`

## 2026-11-14 修复：按 F 七个表情只播第一个 + 角色/剑闪一下

- 现象：R035b 06 第 9 条验收，按 F 始终只播 `CBG_Angry`，而且每次按下角色/剑会闪一下
- 根因一：`PlayerStateMachine.NotifyPose()` 只改内部字段 `m_poseIndex`，**没写 `m_context.PoseIndex`**；`PlayerStateContext.BuildParams()` 发出去永远是 0 ⇒ Animator 的 `PoseIndex≈0` 恒命中第 55 条过渡，只有第一段能播
- 修法一：`NotifyPose` 里补 `m_context.PoseIndex = m_poseIndex;`；单测改成断言 `Parameters.PoseIndex`（只断言内部字段会漏掉这类 bug）
- 根因二：七个 `CBG_*` clip 在 t=0 都带一条 `SwitchSocket` 动画事件，字符串 `To_Put_Socket_Sword`，进姿势的瞬间把剑从右手切到背后、退出时再切回右手 ⇒ 视觉上就是「闪一下」；战斗/技能动作的事件是 `To_Hand_R_Socket`（剑本来就在右手，看不出变化），所以技能不闪
- 修法二：`PlayerBrain.Awake` 里调 `StripPoseClipEvents`，遍历控制器 clip、把名字以 `CBG_` 开头的 `events` 清空；`AnimationClip.events` 运行期改动只在本次运行有效（退出 Play 还原、不写回美术包），所以控制器与美术包都不动
- 判据：按 F 时第 1 次播 `CBG_Angry`、第 8 次回到第 1 个，PoseIndex 按 0..6 轮转；按下的瞬间没有角色/剑闪跳
## 2026-11-15 跳跃高度不够 + 下落/落地陷进地面

- 现象：按 Space 跳，最高点明显偏低；下落或落地时下半身像插进地面
- 根因一：`m_jumpHeight` 默认 0.9 米，负责人要 1.2 米
- 根因二：三条 Mixamo clip 把 `Root Transform Position (Y)` 勾了 `Bake Into Pose`，clip 自带的根 Y 曲线把整个身体压到原点以下 0.86 米（落地日志实据：地面 1.414、脚骨 0.554、胯 1.376），代码又把根节点按物理高度钉在地面 ⇒ 半身入地
- 修法：三条 clip 的 `Root Transform Position (Y)` 改回不勾（`lockRootHeightY=false`、`keepOriginalPositionY=true`），竖直高度只由物理算；`Client/Assets/_Project/Game/Player/PlayerJumpMotor.cs` —— `m_jumpHeight` 改 1.2；新增 `m_alignFeetToGround` / `m_alignMaxOffset`(2 米) / `m_alignDeadZone`(5 毫米) 与 `LateUpdate → TryCalibrateSole / AlignFeetToGround`；落地日志补脚骨/胯/鞋底偏移
- 判据：Console 里 `[PlayerJumpMotor] 落地` 那行的脚骨 ≈ 地面高度；贴地修正日志不连续刷屏；起跳目标高度显示 1.20 米
### 2026-11-16 副本里左键攻击不掉血 + 血量掉光没有黑幕（R042 验收返修）

- 现象一：副本里用鼠标左键打怪，动画在播但怪物血量不动
- 根因一：HpBarDemoBehaviour.RegisterAttackInput 只把动作 0 绑到 J，而 PlayerBrain 本机会处理鼠标左键 ⇒ 有动画、没有 ActionBits，服务端收不到攻击请求
- 修法一：Client/Assets/_Project/Framework/Input/InputMapping.cs 新增备用键（BindAlternate / TryGetAlternateKey，Validate 一并查冲突）；LegacyInputSource 三条查询都改「主键或备用键」；HpBarDemoBehaviour 把备用键绑到 KeyCode.Mouse0
- 现象二：血量掉光后角色不倒、没有黑幕、Console 也没有死亡相关日志
- 根因二：SnapshotView.FindHero 的注释写明「只返回活着的英雄」，英雄一死就返回 null ⇒ RuntimeNetDriver.TrackHeroHealth(null) 提前 return，HeroAlive 永远是 true，PlayerDeathFlow.IsDeadNow() 永远 false
- 修法二：RuntimeNetDriver 新增 FindMyHeroSnapshot()（按 OwnerPlayerId 扫 m_feed.View.Entities，不看 Alive），RefreshPresentation 改用它；SnapshotView 不动（它的语义对「操作目标」是对的）
- 判据：Console 出现「攻击键已登记：J / 鼠标左键」；左键打怪怪掉血；血量归零后约 1 秒出现「你已倒下」黑幕与 5 秒倒计时；退出 Play 后 EditMode 全量 771/0
- 现象三：剑砍到怪身上也不掉血；服务端反复打印「玩家 1 的普攻没打出去：目标 0 不存在」
- 根因三：EnsureSwordCollider 用 FindObjectOfType<PlayerAnimation>() 当玩家锚点，而 World 的 PlayerAnimation 被 PlayerBrain 关掉（FindObjectOfType 跳过未激活组件）⇒ 剑碰撞盒永远为空；且 m_swordLooked 先置 true、只失败一次就再不重试。另外冷却期内的攻击位会被 PumpInput 的周期发送原样带出去（目标 0）
- 修法三：RuntimeNetDriver 新增 ResolvePlayerRoot()（先 PlayerMove、再 PlayerBrain）；EnsureSwordCollider 改成找不到就每秒重试、只告警一次；PumpInput 周期发送改为 `command.ActionBits & ~AttackBitMask`
- 现象四：死亡倒计时结束退出副本，YooAsset 报 Location is invalid: World，且回 World 可能出现两个 [Player_Hero]
- 根因四：ExitToWorld 只依赖 SceneFlow 的回调写 warning，没有自己兜底，也没有清理 DontDestroyOnLoad 带进副本的玩家与会话物体
- 修法四：PlayerDeathFlow.ExitToWorld 改协程：LeaveRoom → 关黑幕 → SceneFlow.LoadAsync → 失败退 SceneManager.LoadScene → 成功后 CleanupAfterExit() 销毁 [DungeonSession] 与带过来的玩家（DungeonEntrance.SessionObjectName 改为 public const）
- 判据补充：砍中怪要有伤害且 Console 无「目标 0 不存在」；回 World 后 Hierarchy 里只有一个 [Player_Hero]
- 现象五：死亡退出副本仍报错，第四段堆栈是 YooInternalException: Unexpected provider type: ErrorProvider（SceneFlow.cs:145 的 handle.Dispose）
- 根因五：YooAsset 加载失败后 Dispose 会去卸载未加载成功的场景并抛异常，异常打断协程 ⇒ 第 154 行的 SceneManager 兜底根本没跑到
- 修法五：失败时不 Dispose、成功时 Dispose 包 try/catch；SceneFlow 记 s_assetSceneFailures，资源包没有的场景本进程后续直接走 SceneManager
- 补充六：编辑器里退出副本仍会刷两行 YooAsset 红字 ⇒ SceneFlow 在 Application.isEditor 时跳过 YooAsset、直接用 SceneManager（开发期资源包本来没打场景）；出包后仍保持 YooAsset 优先后兜底
- 2026-11-16 副本表窗口表头乱码 + 「从 CSV 导入」不生效：CSV 被 Excel 另存成 GBK（无 BOM），桥按 UTF-8 读导致中文列名变乱码、路径解析后仍按英文列名匹配时全部落空；同时 Excel 把怪物列 6001,6001 变成 600,160,016,001。修法：ReadCsvText 先看 BOM（UTF-8/UTF-16），无 BOM 时严格 UTF-8 解码失败即按 GBK 读；导出统一写 UTF-8 带 BOM；校验对不足 4 位的怪物编号提示「Excel 加了千位分隔符」。判据：GBK 文件读回中文正常、导入后 SO 半径跟着变、千位分隔符被明确拒绝。
- 2026-11-16 副本表窗口报 IOException: Sharing violation（打开 Excel 时每帧刷异常、窗口画不出来）：File.ReadAllBytes 默认只允许别家只读打开，Excel 存盘期间持有独占句柄；异常抛在 OnGUI 里导致整个面板 ImmediateModeException。修法：ReadAllBytesShared 用 FileShare.ReadWrite | Delete 打开并重试 5×80ms；IsDirty/ReadColumns 捕获 IOException 转成红字提示（不抛），Import/Export 给出「请先关闭 Excel」。判据：Excel 开着时窗口不报错、导入能读、写盘失败时提示人话。
- 2026-11-17 配置表窗口「导出后还红着」+「Excel 改过 CSV 导入失败」：`IsDirty` 只对磁盘侧 `Normalize`（重拼侧带结尾 `\n`）⇒ 永远不一致；Excel 把 `2002,2003` 写成 `20,022,003` ⇒ 解析成 20/22/3、校验报「Skill 表里没有编号 20」。修法：两边都 `Normalize`；`TryParseCell` 对 int[] 增加「4 位一组还原」；`GetCellText` 显示不再带 CSV 引号；导出后复查 `IsDirty` 并把 `DescribeFirstDifference` 写进状态栏。判据：导出后 `dirty=False`；导入窗口显示「已从 … 导入 3 行」。
- 2026-11-17 重开副本的两个报错：① 切场景间隙 `ReconcileHeroPosition` 每 5 秒刷「找不到我的英雄快照（实体数=0）」——那时还没进房，本来就没有实体；② 点「再来一次」后 `[HpBarDemo] 资源层初始化失败：已经装过加载器了`，且 `SetCanvasLocation` 在 Canvas 已加载后会抛异常。修法：① `hero == null` 且 `!IsJoined` 直接返回、不告警；② `HpBarDemoBehaviour.Start` 先看 `AssetManager.Instance.IsInitialized`，已初始化就跳过 InstallAsync 与两处 UI 地址设置，重复 `ShowPanel` 走 UIManager 的「已显示只 ShowMe」分支。判据：进副本与重开副本都不再有这两条红/黄字。
- 2026-11-17 重开副本第三次报错 `InvalidOperationException: [InputActionId] 索引 0 已经被声明过了（本次声明：Attack）`：动作声明是进程级静态表，`HpBarDemoBehaviour.Start` 重载场景后又跑一遍 `InputActionId.Declare(0, "Attack")`。修法：框架新增 `InputActionId.GetOrDeclare(int index, string name)`（已声明就取回、名字保持第一次的），Boot 改用它；补 2 条单测（可重复调用、不覆盖名字）。判据：重开副本不再抛，Test Runner 798/0。
- 2026-11-17 重开副本后「镜头离玩家很远」+「死掉的怪不刷新」：① 重开走的是 `SceneFlow.LoadAsync(Battle)`，Battle 的 `Main Camera` 是新场景里的一台，没人给它 `SetTarget`，所以停在场景默认位置；修法：`RuntimeNetDriver.OnDungeonSceneLoaded` 由 static 改实例方法，切场景成功后就 `PlayerPersist.BindCamera(玩家)`。② `RoomRegistry.FindJoinableRoom` 不看 `room.Phase`，退房再进房会复用那间已 `Finished` 的房 ⇒ 拿回同一局战斗世界（怪还是死的）；修法：`Phase == RoomPhase.Finished` 的房间不再可加入，客户端会拿到新房、服务端新开一局。判据：重开副本镜头跟着玩家、怪满血刷新。
- 2026-11-17 **副本房间不再被「再来一次」删掉**：RuntimeNetDriver.RestartRoutine 原来走「退房 → 再进房」，退房会让房间清空并被删（房号从 r1 变 r2），复用房又会拿回同一局死光的怪。改为：协议新增 RestartDungeonRequest（ClientMessage.restart_dungeon = 8），RoomService 抛 RestartRequested，RoomBattleService.ResetBattle 丢掉旧战斗世界并把 Phase 重置，RoomRegistry.FindJoinableRoom 不再跳过打完的房；客户端只发请求、不退房。判据：服务端日志「房间 r1 重开：这一局已重置（旧世界已丢弃）」，新一局怪满血、房号不变。
- 2026-11-17 **第一局怪是胶囊、第二局才变狼**：驱动是跨场景常驻的，而敌人模型由副本场景里的 HpBarDemo 稍后赋值（它 Start 里有 wait 资源层）——binder 建槽时模型还是 null 就回落胶囊，且槽位建好后不会换模型。修法：SnapshotSceneBinder.SetMonsterPrefab(GameObject) 换模型时销毁旧槽位让它们下一帧按新模型重建；RuntimeNetDriver.RefreshPresentation 每帧同步一次。判据：第一局进副本就是狼，Console 出现「敌人模型变了，重建 N 个槽位」。
- 2026-11-17 重开副本后要等十几秒才刷怪：快照帧号取战斗世界 Tick，新一局从 0 重数 ⇒ 客户端 SnapshotView 把新快照全当旧的丢掉。改为 RoomBattleService 房间级单调帧号（NextServerTick，重开不复位），net-probe 加「重开后帧号继续增大」用例（444/0）。
- 2026-11-17 从副本「返回世界」后 World 里多出 NBC-视图接管 与 单位 1..4（副本的狼被带到 World）：驱动跨场景常驻，OnDungeonSceneLoaded 在**切完场景后**才清 m_feed/m_binder，而中间那些帧里 RefreshPresentation 已经在 World 里按旧快照重建了绑定器。修法：ReturnToWorldRoutine 在切场景**之前**就 m_bindPresentation=false + m_feed.Clear() + m_binder.ClearSlots()，并在回调里保持关闭（下次进副本由 DungeonEntrance.FinishEnter 重新打开）。
- 2026-11-17 「返回世界」瞬间同屏两个玩家：World 场景自带一份 [Player_Hero]，而带进副本的那份被 DontDestroyOnLoad 保留、回加载时两份并存（两套输入/动画）。修法：PlayerPersist 记住被标记的玩家并提供 ReleasePersistent()，RuntimeNetDriver.ReturnToWorldRoutine 在切 World **之前**把它销毁，交还给场景自带的那份；下次进副本由 DungeonEntrance.FinishEnter 重新标记。
- 2026-11-17 **点「返回世界」后副本的血条/网络面板留在 World**：两个面板由 HpBarDemo 在 Battle 里 ShowPanel 打开，而它们挂在 DontDestroyOnLoad 的 Canvas 下，Battle 场景卸载时没人关。修法：HpBarDemoBehaviour 加 OnDestroy，退出副本时 HidePanel("HpBarPanel") 与 HidePanel("NetHudPanel")。判据：回 World 后屏幕上没有血条与网络面板，再次进副本它们照常出现。
- 2026-11-17 **打完一局、返回世界、再进副本时不立刻刷怪**：快照帧号原来是「按房号」计数（`_serverTicks[roomId]`），新房间又从 1 开始，而客户端 `SnapshotView.ServerTick` 还停在上一局几百帧 ⇒ 新房间的快照全被判「旧的」丢掉。修法：服务端改**全局单调**计数器 `_serverTick`（不按房间、重开也不复位），客户端 `NetSession.HandleRoomState` 发现房号变化就 `m_world.Clear()` + 重置首帧日志。判据：新房间进副本 1 秒内出现怪，Console 出现「第一张含活怪的快照到了」。
- 2026-11-17 返回世界后立刻再进副本没有怪：房间空的时长可能短于一个 tick，主循环没看到空房 ⇒ 旧的（怪全死）战斗世界一直留在 _battles 里。修法：RoomService.HandleLeaveRoom / OnSessionClosed 在房间变空时立刻 RestartRequested（作废这一局、世界丢掉、房间保留）。判据：net-probe 446/0（新增「空房立刻丢世界」用例）。
- 2026-11-19 **进副本报「配置表一张都没有」「资源层尚未初始化」、HUD 抢跑**：新流程（Login → World → Battle）里没人调 `ConfigMgr.SetSource` / `GameTables.PreloadAll`，只有旧离线 demo 会做；而 `RuntimeNetDriver` 进副本第一帧就挂 HUD，那时 `HpBarDemoBehaviour.Start` 还在 `await InstallAsync`。修法见 R054：`BattleReadiness`（统一就绪判断）+ `BattleConfigBootstrap`（幂等设来源/预加载）+ `BattleBootSequence`（等资源 → 设来源 → 等表 → 挂 HUD，单步 8 秒超时），`RuntimeNetDriver.EnsureHud` 改为每帧 Tick 编排、`CloseHud` 取消。判据：Console 依次出现 `[Boot] 进入第 2/3/4 步` 与 `[Boot] 表现层就绪`，且不再有「表还没加载」「资源层尚未初始化」。
- 2026-11-19 目标血条的**数字在掉、红条不变短**：`Client/Assets/_Project/UI/HudTargetPanel.prefab` 里 `BossFill`/`TargetFill` 的 Image 没有 sprite（`m_Sprite: {fileID: 0}`）却设了 `m_Type: 3`（Filled）。Unity 的 Image 在没有 sprite 时走的是普通矩形网格，`fillAmount` 完全不生效（`HpBarPanel` 的 `Img_Fill` 有 sprite 所以正常）。修法：`HudTargetPanel.ApplyBar` 先判 `fill.type == Image.Type.Filled && fill.sprite != null` 才用 `fillAmount`，否则改右锚点 `anchorMax.x = ratio` 收缩。判据：砍狼王时红条随数字一起变短。
- 2026-11-19 **胜利画面与「你已倒下」黑幕叠在一起、回世界后黑幕还在**：`DeathScreenOverlay.Hide()` 只关了背景 `m_backdrop`，而三段文字挂在另一条父链上（prefab 里 `m_Father` 不同），所以文字留在屏幕上；另外 panel 首次加载时 `OnPanelShown` 在「没请求显示」的分支里没有 Hide，`PlayerDeathFlow.Start` 的预热 `Ensure()` 一加载就自带标题；回世界时也没有任何人关它（面板在 DontDestroyOnLoad 的 Canvas 下，切场景不会消失）。修法：`OnInit` 记住背景 + 三段文字四个物体、统一走 `SetVisualsActive`；`OnPanelShown` 没请求就 Hide；新增 `DeathScreenOverlay.HideIfShowing()`，在 `PlayerDeathFlow.Update` 的结算分支、`RuntimeNetDriver.RestartRoutine`、`ReturnToWorldRoutine` 三处调用。判据：胜利结算不出现「你已倒下」，回 World 后屏幕干净。
- 2026-11-19 **第二次进副本时 HUD 面板整体不出现（只有第一次正常）**：`RuntimeHudDriver.CloseAll()` 用 `UIManager.HidePanel` 把两块面板收起来，但没清 `m_targetPanel` / `m_questPanel` 引用；下次 `Bind` 里是 `if (m_targetPanel == null) ShowPanel(...)`，引用还非空就跳过请求 —— 面板留在 UIManager 的池里，永远不会被重新 Show。修法：`CloseAll` 末尾把两个引用置 null（注释写明原因），下次 `Bind` 自然重新 `ShowPanel`（UIManager 对池里的面板会复活并同步回调）。判据：进副本 → 返回世界 → 再进副本，目标血条与任务追踪仍在；EditMode 812/0。
- 2026-11-19 取证备忘：怪物头顶血条（`MonsterHealthBar`，世界空间 Sprite）在第二次进副本时是**正常的**（实测 3 条都 active、sprite 完好、能渲染出来）；看上去"消失"的是 UGUI 的 HUD 面板。排查这类问题先分清「世界空间物体」与「挂在 DDOL Canvas 上的面板」，两边的生命周期完全不同。
- 2026-11-19 **进副本后「任务面板不见了」**：`HudQuestPanel.SetRows` 收到 0 行时会把整块 `Root` 收起来（设计如此：没任务就别占屏幕），而副本里没人接过任务，追踪区恒为 0 行 ⇒ 面板等于隐身。World 那边因为有 `SetHint` 占位，所以看着「World 有、副本没有」。修法：`RuntimeHudDriver.UpdateQuest` 在 `m_rows.Count == 0` 时改调 `m_questPanel.SetHint(EmptyQuestHint)`（新增常量「副本区域：暂无进行中的任务」），有行时照旧 `SetRows`。判据：进副本就能看到右上角一句「副本区域：暂无进行中的任务」；接任务后自动换成任务行。
- 2026-11-19 **点「写回障碍到 CSV」报 `DirectoryNotFoundException`（路径多了一层 `Client`）**：本工程的 Unity 工程目录是 `<仓库>\Client`，所以仓库根在 `Application.dataPath` 往上**两级**；我按「上一级」拼成了 `Client\Configs\Design\...`。修法：`Client/Assets/_Project/Editor/BattleObstacleWriter.cs` 的 `GetProjectRoot()` 改成往上逐级找第一个含 `Configs\Design` 的目录（最多找 6 级），找不到就弹窗报错并且**不写文件**。判据：菜单写回后 Console 打出以仓库根开头的完整路径，`Configs\Design\BattleObstacle.csv` 被更新。
- 2026-11-19 **临时放开活动范围（R056 验收用）**：负责人要到处走、试障碍，把场地半边长从 5 米放大到 **50 米**——服务端 `Server/NBC.Server.Game/DungeonBattle.cs` 的 `PlayAreaHalfExtentMm = 50000`，客户端 `Client/Assets/_Project/Game/Bootstrap/RuntimeNetDriver.cs` 的同名常量同步改 50000（两边必须一起改，否则会「走出服务端场地」被拉回）。Battle 场景地面是 10 倍缩放的 Unity Plane ⇒ 100×100 米，±50 米仍在实地面上。**验收完记得改回 5000**。判据：net-probe 461/0、EditMode 812/0、客户端编译 0 错误。
- 2026-11-19 **移动方向与朝向对不上（按 W 像在倒退、按 D 往反方向走）**：客户端 PlayerMove.GetMoveDirection 用**相机轴**换算方向并转身，而服务端 DungeonBattle.ApplyMove 把 move_x/move_y 当**世界 +X/+Z** 用；相机 yaw 不是 0 时两边各说各话（实测相机 yaw ≈ 182°，于是「朝 -Z 转身、位置往 +Z 走」）。模型与动画都正常（从 +Z 侧渲染看到正脸；走路时肩骨推出的身体朝向与 root.forward 点积 +0.997）。修法（方案 A）：Client/Assets/_Project/Game/Bootstrap/RuntimeNetDriver.cs 新增 ToWorldInput(moveX, moveY, out worldX, out worldY)，用本地 PlayerMove.GetMoveDirection 把输入换算成世界方向再上行（SendInputNow 的状态同步与锁步两条路、HandleAttack 的普攻那条路共三处），服务端一行未改。判据：站在相机前侧按 W，角色朝远离相机的方向走、位置也远离；EditMode 812/0。
