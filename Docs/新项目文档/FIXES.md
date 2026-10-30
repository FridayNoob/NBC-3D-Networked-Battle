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
  已同步 `csharp-code-comment` 技能（含 description 与自检三问）与 `Docs\00` 注释规范第 4 条。
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
