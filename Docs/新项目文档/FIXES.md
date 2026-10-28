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
